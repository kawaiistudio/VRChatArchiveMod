using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// IL2CPP STRIPPED THE GUIStyle SETTERS, SO SETTING ONE THROWS AND TAKES THE WHOLE PANEL WITH IT.
	//
	// VRChat barely uses IMGUI, so on the Unity 6 build il2cpp removed every GUIStyle setter nothing
	// in the game calls. Dumping the live class shows the asymmetry plainly: get_fontSize,
	// get_alignment, get_fontStyle, get_richText, get_wordWrap, get_normal and get_padding are all
	// there, and the only setter left is set_contentOffset. Il2CppInterop still generates the
	// setters, bound to an unresolved icall, so writing one raises
	// "ICall with signature UnityEngine.GUIStyle::set_fontSize was not resolved".
	//
	// The mod builds its styles with object initialisers -- new GUIStyle { fontSize = 12, ... } --
	// and an initialiser is all-or-nothing: the first dead setter aborts the construction, the style
	// is never assigned, and the draw that needed it throws. That is why Watchlist, InstancePanels
	// and the features HUD all went dark at once, and why the menu could not paint a single label.
	//
	// Rather than rewrite a hundred and twenty initialisers, the dead setters are turned into
	// no-ops, once, at load. The style then keeps Unity's default for that property instead of the
	// mod's chosen value -- text at the default size rather than no text at all -- and every other
	// property on the same style still applies. Which setters are dead is MEASURED, not assumed:
	// each one is probed by reading the property and writing the same value straight back, so a
	// build where they work is left completely alone.
	internal static class GuiCompat
	{
		private static bool _done;

		// True once Install has MEASURED that this build's GUIStyle setters are dead. Everything that
		// has to decide "stock IMGUI or the fallback" reads this rather than guessing again.
		internal static bool BrokenImgui { get; private set; }

		// Everything the mod assigns on a GUIStyle. contentOffset is included on purpose: it is the
		// one that survives here, so it also proves the probe does not simply condemn everything.
		private static readonly string[] Props =
		{
			"fontSize", "fontStyle", "alignment", "richText", "wordWrap", "normal", "padding",
			"font", "border", "margin", "fixedWidth", "fixedHeight", "clipping", "stretchWidth",
			"stretchHeight", "imagePosition", "contentOffset",
		};

		internal static void Install()
		{
			if (_done) return;
			_done = true;
			try
			{
				GUIStyle probe;
				try { probe = new GUIStyle(); }
				catch (Exception e)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[GuiCompat] GUIStyle n'est pas constructible sur ce build (" + e.GetType().Name + ") — l'interface du mod restera muette.");
					return;
				}

				var dead = new List<string>();
				MethodInfo noop = typeof(GuiCompat).GetMethod(nameof(Skip), BindingFlags.Static | BindingFlags.NonPublic);

				foreach (string name in Props)
				{
					PropertyInfo pi;
					try { pi = typeof(GUIStyle).GetProperty(name, BindingFlags.Instance | BindingFlags.Public); }
					catch { continue; }
					MethodInfo setter = pi?.GetSetMethod(true);
					MethodInfo getter = pi?.GetGetMethod(true);
					if (setter == null || getter == null) continue;

					// Read then write the SAME value back: harmless where it works, and the exception
					// where it does not is exactly the one the modules were dying on.
					bool alive = true;
					try
					{
						object cur = getter.Invoke(probe, null);
						setter.Invoke(probe, new[] { cur });
					}
					catch (TargetInvocationException tie) when (tie.InnerException != null) { alive = false; }
					catch { alive = false; }
					if (alive) continue;

					try
					{
						VRChatArchiveModPlugin.HarmonyInstance.Patch(setter, prefix: new HarmonyMethod(noop));
						dead.Add(name);
					}
					catch (Exception e)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[GuiCompat] {name} est mort mais n'a pas pu etre neutralise : {e.GetType().Name} — les styles qui l'utilisent resteront casses.");
					}
				}

				// GUIStyleState setters are the SAME breakage one level down: Hud.Panel sets
				// normal.textColor, and on this build GUIStyleState.set_textColor is mis-bound to the
				// wrong native method — invoking it is a FATAL access violation (it killed the game in
				// RadarModule.OnGui the moment the gate opened and the HUD modules started drawing).
				// It cannot be probed by invoking (the AV is uncatchable), so when this build has ALREADY
				// shown dead GUIStyle setters above, the matching GUIStyleState setters are neutralised
				// outright. A healthy build (no dead GUIStyle setters) is left completely alone.
				if (dead.Count > 0)
				{
					foreach (string sn in new[] { "textColor", "background", "scaledBackgrounds" })
					{
						try
						{
							PropertyInfo pi = typeof(GUIStyleState).GetProperty(sn, BindingFlags.Instance | BindingFlags.Public);
							MethodInfo setter = pi?.GetSetMethod(true);
							if (setter == null) continue;
							VRChatArchiveModPlugin.HarmonyInstance.Patch(setter, prefix: new HarmonyMethod(noop));
							dead.Add("GUIStyleState." + sn);
						}
						catch (Exception e)
						{
							VRChatArchiveModPlugin.Logger.LogWarning($"[GuiCompat] GUIStyleState.{sn} n'a pas pu etre neutralise : {e.GetType().Name} — le HUD qui l'utilise peut encore crasher.");
						}
					}
				}

				// Dead GETTERS too: GUIStyle.get_fontSize is unresolved on this build, so any draw code
				// that READS a style property throws ("ICall ... not resolved") — ActiveFeaturesHud died
				// on it every frame. Neutralise the int-returning getter to 0 (which IMGUI reads as "use
				// the font's default size"), so reading it is harmless instead of throwing. Gated on this
				// being the broken build (dead setters already seen).
				if (dead.Count > 0)
				{
					try
					{
						MethodInfo getFs = typeof(GUIStyle).GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod(true);
						if (getFs != null)
						{
							VRChatArchiveModPlugin.HarmonyInstance.Patch(getFs, prefix: new HarmonyMethod(
								typeof(GuiCompat).GetMethod(nameof(GetIntZero), BindingFlags.Static | BindingFlags.NonPublic)));
							dead.Add("get_fontSize");
						}
					}
					catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[GuiCompat] get_fontSize non neutralise : " + e.GetType().Name); }
				}

				BrokenImgui = dead.Count > 0;

				if (dead.Count == 0)
					VRChatArchiveModPlugin.Logger.LogInfo("[GuiCompat] tous les setters de GUIStyle repondent sur ce build — rien a neutraliser.");
				else
					VRChatArchiveModPlugin.Logger.LogWarning("[GuiCompat] setters absents de ce build, neutralises pour que l'interface se construise quand meme : "
						+ string.Join(", ", dead) + ". Ces proprietes garderont la valeur par defaut d'Unity.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[GuiCompat] non installe : " + e.Message); }
		}

		private static bool Skip() => false;
		private static bool GetIntZero(ref int __result) { __result = 0; return false; }

		// MEASURE TEXT WITHOUT GUIContent OR CalcSize.
		//
		// `new GUIContent(string)` is mis-bound on this build and invoking it is a FATAL access
		// violation — it took the game down from InstancePanels' roster every time it measured a
		// column. GUIStyle.CalcSize goes the same way. The panels only need a width to lay columns
		// out, so it is estimated from the font size instead: the mod's tables are drawn in a
		// monospace style, where a glyph is a stable fraction of the em.
		internal static float TextWidth(string text, float fontSize)
		{
			if (string.IsNullOrEmpty(text)) return 0f;
			if (fontSize <= 0f) fontSize = 12f;
			return text.Length * fontSize * 0.54f;
		}

		internal static Vector2 TextSize(string text, float fontSize)
		{
			if (fontSize <= 0f) fontSize = 12f;
			return new Vector2(TextWidth(text, fontSize), fontSize * 1.3f);
		}

		// Sends a bare GUI.Label through the 3-arg overload, which IS bound on this build. No recursion:
		// the 3-arg method is a different one and is never patched.
		// EVERY BARE LABEL GOES THROUGH HERE.
		//
		// GUI.Label(Rect, string) — the overload without a style — is mis-bound on this build and
		// calling it is a FATAL access violation. It cannot be detoured either: ProxyGuard refuses to
		// let Harmony patch a method whose binding it does not trust. So the call sites use this
		// instead, which goes through the 3-arg overload — the one that IS bound, and the one the HUD
		// already draws all of its text through.
		// NEVER TOUCH GUI.skin ON A BROKEN BUILD.
		//
		// `new GUIStyle(GUI.skin.label)` is the normal way to start a style from the game's own look,
		// and it is how five places in this mod built theirs. On this build `GUISkin.get_label` is
		// mis-bound: its cached native MethodInfo is null, so il2cpp_runtime_invoke dereferences it
		// and the process dies — not an exception, a FATAL access violation, which is exactly what
		// killed the game the moment the menu was opened (ErrorLog: GUISkin.get_label ->
		// GuiCompat.Label -> Menu.Draw). `new GUIStyle()` IS constructible here (Install proves it
		// every boot by probing with one), so that is what the broken build gets. The caller then
		// applies its own font size and alignment on top exactly as before.
		internal static GUIStyle BaseStyle()
		{
			if (!BrokenImgui)
			{
				try { return new GUIStyle(GUI.skin.label); } catch { }
			}
			try { return new GUIStyle(); } catch { return null; }
		}

		private static GUIStyle _labelStyle;
		internal static void Label(Rect position, string text)
		{
			try
			{
				if (_labelStyle == null)
				{
					_labelStyle = BaseStyle();
					if (_labelStyle == null) return;
				}
				GUI.Label(position, text, _labelStyle);
			}
			catch { }
		}
	}
}
