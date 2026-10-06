using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace VRChatArchiveMod.Core
{
	// EVERY BUTTON IN THIS MOD WENT THROUGH A CAST THAT KILLS THE GAME.
	//
	//     btn.onClick.AddListener((UnityAction)onClick);
	//
	// That cast is an implicit conversion operator Il2CppInterop generates, and it calls
	// DelegateSupport.ConvertDelegate underneath — the same call that takes VRChat down with an access
	// violation on this build. Searching the source for "ConvertDelegate" does not find any of these
	// lines, which is exactly why the crash survived a first pass: the fatal call is spelled as a cast.
	//
	// SO THE MOD STOPS ASKING FOR A DELEGATE AT ALL.
	//
	// Refusing the conversion kept the game alive and left the menu dead: every panel drew and nothing
	// responded — "the submenus do not work" was the entire feature set sitting behind a wall. But a
	// click handler is only needed because Unity has no other way to call back INTO managed code, and
	// this mod is already there. Unity's own Button.Press() runs on every click, from the pointer and
	// from submit alike; hooking it and looking the button up in a table of our own dispatches the click
	// without a single il2cpp delegate existing anywhere.
	//
	// The table is keyed by the button's INSTANCE ID, not by the managed wrapper: Il2CppInterop hands
	// out a fresh wrapper for the same native button whenever it is re-pooled, so anything keyed on the
	// wrapper would quietly stop matching after a menu rebuild.
	internal static class UiClick
	{
		private static readonly Dictionary<int, List<Action>> _clicks = new Dictionary<int, List<Action>>();
		private static readonly Dictionary<int, List<Action<float>>> _slides = new Dictionary<int, List<Action<float>>>();
		private static readonly Dictionary<int, float> _lastPress = new Dictionary<int, float>();
		private static bool _hooked, _hookFailed;
		private static int _refused, _dispatched;

		internal static int Wired { get { lock (_clicks) return _clicks.Count; } }

		// WHEN THE MENU LAST CHANGED. Button.Press is hooked for every button in the game, so this
		// is the cheapest honest answer to "did VRChat just restyle a page?" -- it repaints on show,
		// and a page is shown because something was pressed. MenuThemeModule uses it to re-assert
		// colours over the WHOLE menu only around a press, instead of every tick forever.
		internal static float LastPressAt;
		internal static int Dispatched => _dispatched;

		internal static void AddClick(Button btn, Action onClick)
		{
			if (btn == null || onClick == null) return;

			// THE NATIVE PATH, only on a build someone has explicitly opened the bridge for.
			//
			// It used to be spelled `btn.onClick.AddListener((UnityAction)onClick)` — the implicit cast
			// described above. That cast converts the delegate WITHOUT going through Il2CppDelegates,
			// so when it killed the process it left no marker, and the gate's "remember the crash and
			// stay shut" logic never ran: the same user crashed on every single launch. Routing it
			// through TryConvert means the conversion is bracketed by the on-disk breadcrumb like every
			// other one, and a death here is recorded instead of repeated.
			if (Il2CppDelegates.Available)
			{
				var native = Il2CppDelegates.TryConvert<UnityAction>(onClick, "UiClick");
				if (native != null)
				{
					try { btn.onClick.AddListener(native); return; }
					catch (Exception e)
					{
						VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] listener refused: " + Unwrap.Describe(e));
					}
				}
			}

			if (!EnsureHook()) { Refuse(); return; }
			int id;
			try { id = btn.GetInstanceID(); } catch { Refuse(); return; }
			lock (_clicks)
			{
				if (!_clicks.TryGetValue(id, out var list)) _clicks[id] = list = new List<Action>(2);
				if (!list.Contains(onClick)) list.Add(onClick);
			}
		}

		internal static void AddValueChanged(Slider slider, Action<float> onChanged)
		{
			if (slider == null || onChanged == null) return;
			// Same cast, same hole, same fix as AddClick above.
			if (Il2CppDelegates.Available)
			{
				var native = Il2CppDelegates.TryConvert<UnityAction<float>>(onChanged, "UiClick");
				if (native != null)
				{
					try { slider.onValueChanged.AddListener(native); return; }
					catch (Exception e)
					{
						VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] value listener refused: " + Unwrap.Describe(e));
					}
				}
			}

			if (!EnsureHook()) { Refuse(); return; }
			int id;
			try { id = slider.GetInstanceID(); } catch { Refuse(); return; }
			lock (_slides)
			{
				if (!_slides.TryGetValue(id, out var list)) _slides[id] = list = new List<Action<float>>(2);
				list.Add(onChanged);
			}
		}

		private static readonly Dictionary<int, float> _debounceTimes = new Dictionary<int, float>();

		internal static void SetDebounce(Button btn, float seconds)
		{
			if (btn == null) return;
			try
			{
				int id = btn.GetInstanceID();
				lock (_clicks) { _debounceTimes[id] = seconds; }
			}
			catch { }
		}

		internal static void Clear(Button btn) => Forget(btn);

		internal static void SetClick(Button btn, Action onClick)
		{
			Forget(btn);
			AddClick(btn, onClick);
		}

		// A rebuilt menu leaves its old buttons destroyed; their entries would otherwise pile up for the
		// life of the process, and Unity DOES reuse instance ids.
		internal static void Forget(Button btn)
		{
			if (btn == null) return;
			try
			{
				int id = btn.GetInstanceID();
				lock (_clicks)
				{
					_clicks.Remove(id);
					_lastPress.Remove(id);
					_debounceTimes.Remove(id);
				}
			}
			catch { }
		}

		private static bool EnsureHook()
		{
			if (_hooked) return true;
			if (_hookFailed) return false;
			try
			{
				var press = typeof(Button).GetMethod("Press",
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
				if (press == null) throw new MissingMethodException("UnityEngine.UI.Button.Press");
				VRChatArchiveModPlugin.HarmonyInstance.Patch(press, postfix: new HarmonyMethod(
					typeof(UiClick).GetMethod(nameof(PressPostfix), BindingFlags.Static | BindingFlags.NonPublic)));

				// Sliders report through Set(float, bool); the public setter routes into it too.
				try
				{
					var set = typeof(Slider).GetMethod("Set",
						BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
						null, new[] { typeof(float), typeof(bool) }, null);
					if (set != null)
						VRChatArchiveModPlugin.HarmonyInstance.Patch(set, postfix: new HarmonyMethod(
							typeof(UiClick).GetMethod(nameof(SetPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] sliders not wired: " + e.Message); }

				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[UiClick] clics cables SANS le pont de delegues — Button.Press est hooke et le mod "
					+ "dispatche lui-meme, donc le menu repond meme quand Il2CppInterop ne peut convertir aucun delegue.");
				return true;
			}
			catch (Exception e)
			{
				_hookFailed = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] impossible de hooker Button.Press (" + e.Message + ") — le menu s'affichera sans repondre.");
				return false;
			}
		}

		private static void PressPostfix(Button __instance)
		{
			// Stamped for EVERY button, ours or VRChat's, before the early-out below: a press we do
			// not handle is exactly the one that changes the page.
			try { LastPressAt = VaClock.Now; } catch { }
			if (__instance == null) return;
			List<Action> list;
			int id;
			try { id = __instance.GetInstanceID(); } catch { return; }

			float now = VaClock.Now;
			lock (_clicks)
			{
				float debounceThreshold = 0.35f;
				if (_debounceTimes.TryGetValue(id, out float custom)) debounceThreshold = custom;

				if (_lastPress.TryGetValue(id, out float last) && (now - last) < debounceThreshold) return;
				_lastPress[id] = now;

				if (!_clicks.TryGetValue(id, out var found)) return;
				list = new List<Action>(found);   // a handler may rebuild the menu, and with it this table
			}
			_dispatched++;
			for (int i = 0; i < list.Count; i++)
			{
				// One handler that throws must not swallow the others, and must never reach VRChat's own
				// UI code as an exception crossing the il2cpp boundary.
				try { list[i](); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] handler threw: " + Unwrap.Describe(e)); }
			}
		}

		private static void SetPostfix(Slider __instance, float input, bool sendCallback)
		{
			if (__instance == null || !sendCallback) return;
			List<Action<float>> list;
			int id;
			try { id = __instance.GetInstanceID(); } catch { return; }
			lock (_slides)
			{
				if (!_slides.TryGetValue(id, out var found)) return;
				list = new List<Action<float>>(found);
			}
			for (int i = 0; i < list.Count; i++)
			{
				try { list[i](input); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] slider handler threw: " + Unwrap.Describe(e)); }
			}
		}

		// One line for the first, then a count — dozens of identical warnings would bury the log.
		private static void Refuse()
		{
			_refused++;
			if (_refused == 1)
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[UiClick] buttons are being drawn WITHOUT click handlers: neither the delegate bridge "
					+ "nor the Button.Press hook is available on this build. The menu will appear but not respond.");
			else if (_refused % 25 == 0)
				VRChatArchiveModPlugin.Logger.LogWarning("[UiClick] " + _refused + " unwired controls so far.");
		}
	}
}
