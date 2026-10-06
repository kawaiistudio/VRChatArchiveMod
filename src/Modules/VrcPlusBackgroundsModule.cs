using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU BACKGROUNDS — unlocks the QuickMenu/Main Menu backgrounds the client hides behind VRChat
	// Plus, including the animated (parallax) ones.
	//
	// WHAT IS ACTUALLY TOUCHED, AND WHY IT IS THIS AND NOTHING ELSE.
	//
	// Each background is a VRC.BackgroundOption: an id, a localised name, a preview Sprite, a
	// MATERIAL name, and one bool — _isVRCPlus. That bool means "this background is a VRC+ one", so
	// the unlock is setting it to FALSE, not true; turning them all on would lock every background
	// instead. They live in a VRC.BackgroundOptions ScriptableObject, which the menu reads to build
	// its list. The parallax is not a separate feature to enable: an animated background is simply an
	// option whose visual comes from _materialName -> _loadedMaterial rather than a flat texture, and
	// it is gated by that same one bool. One field unlocks the picture AND the parallax.
	//
	// This deliberately does NOT spoof VRC+ itself. VRC.Core.APIUser.isSupporter and
	// VRC.Core.ApiProduct.SubscriberExclusive look like tempting single points, but they are fields of
	// ApiModel objects — the same base as APISubscription, carrying Save(), Put(), PostOrPut() and
	// SendPutRequest(). A value forged on one of those can be serialised back to VRChat by any code
	// path that saves the model, which turns a local display tweak into modified data arriving at the
	// server under your account. BackgroundOption is not an API model: it is a ScriptableObject that
	// ships inside the game's own assets. Nothing here is sent anywhere, nothing can be written back,
	// and the server keeps reporting isSupporter = false because we never touched it.
	//
	// Nothing is written to disk either: these are assets loaded into memory for the session, so the
	// change lives exactly as long as the game process. OFF restores every original value.
	//
	// The sweep repeats because the asset is not loaded at startup — it appears with the menu — and
	// because a fresh copy can be loaded later. A list the menu has already built does not re-read the
	// flags, so the menu has to be closed and reopened once for new entries to show.
	public class VrcPlusBackgroundsModule : IModule
	{
		public override string Name => "VrcPlusBackgrounds";

		public static bool Active { get; private set; }
		public static int Count => _touched.Count;
		public static string Status = "";

		private sealed class Held
		{
			public VRC.BackgroundOption Option;
			public bool Was;
		}

		private static readonly List<Held> _touched = new List<Held>();
		private static readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
		private static float _nextSweep;
		private static Il2CppSystem.Type _setIl2;

		public override void OnUpdate()
		{
			try
			{
				if (!Active) return;
				float now = VaClock.Now;
				if (now < _nextSweep) return;
				// 5 s, not 2: this walks the loaded-asset table, which is far heavier than a scene
				// query, and the set of backgrounds changes about once per session.
				_nextSweep = now + 5f;
				Sweep();
			}
			catch (Exception e) { Status = "menu backgrounds: " + e.Message; }
		}

		public static void Toggle()
		{
			if (Active)
			{
				RestoreAll();
				Active = false;
				Status = "menu backgrounds locked again";
			}
			else
			{
				Active = true;
				_nextSweep = 0f;
				Sweep();
				Status = _touched.Count > 0
					? "menu backgrounds unlocked — " + _touched.Count + " (reopen the menu to see them)"
					: "menu backgrounds: none found yet — open the menu once, it retries on its own";
			}
			VaTagsModule.LastStatus = Status;
			VRChatArchiveModPlugin.Logger.LogInfo("[VrcPlusBackgrounds] " + Status);
		}

		private static void Sweep()
		{
			// Re-assert what we already hold: the menu reloads its option set when it rebuilds, and a
			// reloaded asset comes back with the original flag.
			for (int i = _touched.Count - 1; i >= 0; i--)
			{
				Held h = _touched[i];
				try
				{
					if (h.Option == null || !NativeGuard.Alive(h.Option)) { _touched.RemoveAt(i); continue; }
					if (h.Option._isVRCPlus) h.Option._isVRCPlus = false;
				}
				catch { _touched.RemoveAt(i); }
			}

			try
			{
				// NON-GENERIC, like every other VRChat type in this mod: the generic
				// Resources.FindObjectsOfTypeAll<T>() comes back empty for il2cpp classes.
				// FindObjectsOfTypeAll and not FindObjectsOfType — BackgroundOptions is a
				// ScriptableObject asset, it is never a live scene object.
				if (_setIl2 == null) _setIl2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.BackgroundOptions>();
				var sets = Resources.FindObjectsOfTypeAll(_setIl2);
				if (sets == null) return;

				int added = 0;
				for (int s = 0; s < sets.Length; s++)
				{
					VRC.BackgroundOptions set = null;
					try { set = sets[s] != null ? sets[s].TryCast<VRC.BackgroundOptions>() : null; } catch { }
					if (set == null || !NativeGuard.Alive(set)) continue;
					added += Unlock(set);
				}
				if (added > 0)
				{
					Status = "menu backgrounds unlocked — " + _touched.Count + " (reopen the menu to see them)";
					VRChatArchiveModPlugin.Logger.LogInfo("[VrcPlusBackgrounds] " + added + " newly unlocked, " + _touched.Count + " total");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VrcPlusBackgrounds] sweep: " + e.Message); }
		}

		private static int Unlock(VRC.BackgroundOptions set)
		{
			int added = 0;
			// `var`, deliberately: the element type of this array is an obfuscated interface whose name
			// changes with every VRChat update. Naming it here would break the build each time; letting
			// the compiler infer it, and casting each element to the STABLE VRC.BackgroundOption, does
			// not. VRC.BackgroundOption and VRC.BackgroundOptions keep their real names — they are not
			// run through the obfuscator.
			var options = set.Options;
			if (options == null) return 0;

			for (int i = 0; i < options.Length; i++)
			{
				VRC.BackgroundOption opt = null;
				try
				{
					var raw = options[i];
					if (raw == null) continue;
					// Through Il2CppObjectBase ON PURPOSE, instead of calling TryCast on `raw` directly.
					// The array's element type is an il2cpp INTERFACE, and depending on how
					// Il2CppInterop generated it for this build, `raw` may be typed as a C# interface —
					// which carries no TryCast. Casting to the base class first compiles either way,
					// and at runtime the object always is one.
					var obj = raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
					if (obj == null) continue;
					opt = obj.TryCast<VRC.BackgroundOption>();
				}
				catch { continue; }
				if (opt == null || !NativeGuard.Alive(opt)) continue;

				// Identity by native pointer: BackgroundOption is a plain il2cpp object, not a
				// UnityEngine.Object, so it has no GetInstanceID() to key on.
				IntPtr key;
				try { key = opt.Pointer; } catch { continue; }
				if (key == IntPtr.Zero || !_seen.Add(key)) continue;

				bool was;
				try { was = opt._isVRCPlus; } catch { _seen.Remove(key); continue; }
				_touched.Add(new Held { Option = opt, Was = was });
				if (!was) continue;   // already free: remembered, so OFF cannot mislabel it as VRC+

				try { opt._isVRCPlus = false; added++; }
				catch (Exception e)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[VrcPlusBackgrounds] could not unlock an option: " + e.Message);
				}
			}
			return added;
		}

		private static void RestoreAll()
		{
			for (int i = 0; i < _touched.Count; i++)
			{
				Held h = _touched[i];
				try { if (h.Option != null && NativeGuard.Alive(h.Option)) h.Option._isVRCPlus = h.Was; } catch { }
			}
			_touched.Clear();
			_seen.Clear();
		}

		// The assets survive a scene change, but the proxies we hold may not: drop them and let the
		// next sweep find the set again rather than reading a dead pointer.
		public override void OnSceneLoaded(int buildIndex)
		{
			_touched.Clear();
			_seen.Clear();
			_nextSweep = 0f;
		}

		public override void OnShutdown() { RestoreAll(); Active = false; }
	}
}
