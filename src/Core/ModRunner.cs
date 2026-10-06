using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// Il2Cpp-injected MonoBehaviour that pumps the module update loop each frame.
	//
	// NOT USED ON VRCHAT BUILD 1903. ClassInjector now gets as far as finishing AddComponent (the token
	// repair fixed that much), but Unity never calls a single callback on the resulting component: the
	// injected Il2CppClass is built to a layout this build does not use, so its method table is wrong
	// and the first invocation ends the process. Core/FramePump drives the modules instead, riding on
	// Unity's own per-frame methods, and is the default. VA_RUNNER=inject selects this one to re-test
	// injection after a VRChat update or an interop regeneration.
	public class ModRunner : MonoBehaviour
	{
		public ModRunner(IntPtr ptr) : base(ptr) { }

		// Scene changes were never dispatched: ModuleManager.SceneLoaded had no caller, so
		// every per-instance reset (watchlist priming, anti-block bookkeeping) silently never
		// ran and carried stale state from world to world.
		private int _lastScene = -1;

		private void Update()
		{
			try
			{
				int idx = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
				if (idx != _lastScene) { _lastScene = idx; ModuleManager.SceneLoaded(idx); }
			}
			catch { }

			Menu.HandleInput();
			Menu.EnforceCursor();
			ModuleManager.ProfileTick();   // rolls the 1-second profiler window
			ModuleManager.Update();
		}

		private void OnGUI()
		{
			// LAST CHANCE EACH FRAME. Update and LateUpdate both re-assert the cursor, but VRChat's
			// own mouselook can still re-lock it after our LateUpdate — and a re-locked cursor is
			// pinned to the centre of the screen, which is exactly what "the mouse stays in the
			// middle" is. OnGUI runs after both, so claiming it here is what actually holds.
			// Guarded to the repaint pass so it costs one comparison per frame, not one per event.
			if (Event.current != null && Event.current.type == EventType.Repaint) Menu.EnforceCursor();

			ModuleManager.OnGui();
			Menu.Draw();
		}

		private void LateUpdate()
		{
			// Re-assert AFTER the game's own input pass, which re-locks the cursor each frame.
			Menu.EnforceCursor();
			ModuleManager.LateUpdate();
		}

		private void FixedUpdate()
		{
			ModuleManager.FixedUpdate();
		}

		private void OnDestroy()
		{
			// Never leave the game with its input suspended: close the menu first so the
			// cursor and every disabled component are handed back before we go away.
			try { Menu.Visible = false; Menu.EnforceCursor(); } catch { }
			ModuleManager.ShutdownAll();
		}
	}
}
