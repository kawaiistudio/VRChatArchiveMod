using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// RE-AIM THE IMGUI INTEROP AT THE REAL METHODS, BY NAME, FROM LIVE IL2CPP METADATA.
	//
	// Every IMGUI crash this build produced has the same shape in the error log: a managed frame
	// calling an interop proxy, then `il2cpp_runtime_invoke`, then a fatal access violation. The
	// proxy is fine; the pointer it hands the invoker is not. Il2CppInterop binds each proxy once,
	// in its static constructor, by TOKEN -- `NativeMethodInfoPtr_get_label_Public_get_GUIStyle_0 =
	// GetIl2CppMethodByToken(class, 0x06001234)`. The interop in this pack was generated against the
	// Aug 27 VRChat build and the game is the Oct 2 one, so a good share of those tokens now name a
	// different method or no method at all. When the lookup fails the slot stays NULL and the first
	// call dereferences it; when it lands on a neighbour the call corrupts the stack. Either way the
	// process dies, which is why fixing these one crash at a time never converged: GUI.Label, then
	// GUIContent..ctor, then GUIStyleState.set_textColor, then GUISkin.get_label, each found only by
	// losing a session to it.
	//
	// UnityEngine is not obfuscated, so the NAMES are still there and still correct: `get_label` is
	// called `get_label` in the live metadata. That is a stronger key than a token from another
	// build. For every proxy in the IMGUI assembly this walks the live class, keeps the methods whose
	// name matches AND whose shape agrees with what the proxy declares (MemberAlign.SlotMatchesProxy
	// -- arity, staticness, return and parameter kinds), and when exactly one survives it writes that
	// pointer into the proxy's cached slot. One pass fixes setters, getters, overloads and
	// constructors together, instead of neutralising them one by one and losing their values --
	// which is what left the HUD's text black and unsized.
	//
	// SAFETY. A slot is only written when the live candidate is unique AND agrees with the proxy's
	// own signature, so a re-aimed call is always made with the right number of arguments of the
	// right kinds. A slot already pointing at that same method is left untouched. Nothing is guessed:
	// no unique match means no write, and GuiCompat's probe (which runs after this) still catches
	// whatever is left dead.
	internal static class GuiRebind
	{
		private static bool _done;

		// How many proxies this pass re-aimed. GuiCompat reads it so its own report says whether the
		// styles it is about to probe have already been repaired here.
		internal static int Repaired { get; private set; }

		internal static void Install()
		{
			if (_done) return;
			_done = true;

			Assembly imgui;
			try { imgui = typeof(GUIStyle).Assembly; }
			catch (Exception e) { Log("[GuiRebind] assemblage IMGUI introuvable : " + e.GetType().Name); return; }

			Type[] types;
			try { types = imgui.GetTypes(); }
			catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
			catch (Exception e) { Log("[GuiRebind] types IMGUI illisibles : " + e.GetType().Name); return; }

			int typesOk = 0, classMissing = 0, noSlot = 0, alreadyOk = 0, ambiguous = 0, unresolved = 0, repaired = 0;
			var fixedNames = new List<string>();
			var leftBroken = new List<string>();

			foreach (Type t in types)
			{
				if (t == null || t.IsInterface || t.IsEnum) continue;

				IntPtr klass;
				try { klass = Il2CppClassPointerStore.GetNativeClassPointer(t); }
				catch { classMissing++; continue; }
				if (klass == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(klass)) { classMissing++; continue; }
				typesOk++;

				// The live class, read once: name -> every method carrying it (overloads included).
				Dictionary<string, List<IntPtr>> live = LiveByName(klass);
				if (live.Count == 0) continue;

				var members = new List<MethodBase>();
				try
				{
					members.AddRange(t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
					members.AddRange(t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
				}
				catch { continue; }

				foreach (MethodBase m in members)
				{
					FieldInfo slot;
					try { slot = ProxyGuard.NativeSlot(m); } catch { continue; }
					// No cached slot: this proxy reaches il2cpp through a resolved ICALL delegate
					// instead, which is a different mechanism with nothing here to re-aim. GuiCompat
					// neutralises those when they are dead.
					if (slot == null) { noSlot++; continue; }

					IntPtr cur;
					try { cur = (IntPtr)slot.GetValue(null); } catch { continue; }

					string name = m is ConstructorInfo ? ".ctor" : m.Name;
					List<IntPtr> candidates;
					if (!live.TryGetValue(name, out candidates))
					{
						if (cur == IntPtr.Zero) { unresolved++; leftBroken.Add(t.Name + "." + name); }
						continue;
					}

					// Name is the key; the proxy's own signature settles the overloads.
					IntPtr hit = IntPtr.Zero;
					int seen = 0;
					foreach (IntPtr c in candidates)
					{
						bool ok;
						try { ok = MemberAlign.SlotMatchesProxy(c, m); } catch { ok = false; }
						if (!ok) continue;
						if (++seen > 1) break;
						hit = c;
					}

					if (seen != 1)
					{
						if (cur == IntPtr.Zero)
						{
							if (seen > 1) ambiguous++; else unresolved++;
							leftBroken.Add(t.Name + "." + name);
						}
						continue;
					}

					if (cur == hit) { alreadyOk++; continue; }

					try
					{
						slot.SetValue(null, hit);
						repaired++;
						if (fixedNames.Count < 60) fixedNames.Add(t.Name + "." + name);
					}
					catch { }
				}
			}

			Repaired = repaired;

			try
			{
				if (repaired > 0)
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[GuiRebind] interop IMGUI re-vise par NOM sur les metadonnees vivantes : " + repaired
						+ " methode(s) recablee(s) sur " + typesOk + " type(s) — " + alreadyOk + " deja correcte(s), "
						+ noSlot + " par ICall, " + ambiguous + " ambigue(s), " + unresolved + " sans correspondance, "
						+ classMissing + " classe(s) absente(s). Exemples : " + Join(fixedNames, 20)
						+ ". Les setters/getters IMGUI appliquent de nouveau leur valeur.");
				else
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[GuiRebind] rien a recabler : " + typesOk + " type(s), " + alreadyOk + " methode(s) deja correcte(s), "
						+ noSlot + " par ICall, " + ambiguous + " ambigue(s), " + unresolved + " sans correspondance, "
						+ classMissing + " classe(s) absente(s).");

				if (leftBroken.Count > 0)
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[GuiRebind] " + leftBroken.Count + " proxy(s) IMGUI restent non lies (tout appel serait fatal) : "
						+ Join(leftBroken, 30));
			}
			catch { }
		}

		private static string Join(List<string> l, int max)
		{
			if (l == null || l.Count == 0) return "(aucun)";
			int n = Math.Min(max, l.Count);
			var a = new string[n];
			for (int i = 0; i < n; i++) a[i] = l[i];
			return string.Join(", ", a) + (l.Count > n ? ", …" : "");
		}

		// name -> live methods. One walk of the class; the iterator is the only way to read methods
		// that il2cpp_class_get_method_from_name cannot separate.
		private static Dictionary<string, List<IntPtr>> LiveByName(IntPtr klass)
		{
			var map = new Dictionary<string, List<IntPtr>>(StringComparer.Ordinal);
			try
			{
				IntPtr iter = IntPtr.Zero, m;
				int guard = 0;
				while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero && guard++ < 8192)
				{
					string n = null;
					try
					{
						IntPtr p = IL2CPP.il2cpp_method_get_name(m);
						if (p != IntPtr.Zero) n = Marshal.PtrToStringAnsi(p);
					}
					catch { }
					if (string.IsNullOrEmpty(n)) continue;
					List<IntPtr> l;
					if (!map.TryGetValue(n, out l)) map[n] = l = new List<IntPtr>();
					l.Add(m);
				}
			}
			catch { }
			return map;
		}

		private static void Log(string s) { try { VRChatArchiveModPlugin.Logger.LogWarning(s); } catch { } }
	}
}
