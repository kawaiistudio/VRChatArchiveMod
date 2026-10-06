using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// GRAVITY — turn it off and float.
	//
	// READ THIS BEFORE EXPECTING IT TO BE A PRANK: gravity is simulated by each client for itself.
	// This changes YOUR gravity on YOUR machine. Everyone else keeps falling normally, and on their
	// screens you are simply a player who is drifting upward.
	//
	// There is no API to set somebody else's gravity, and there is no honest way to fake one: it
	// would mean seizing the world's networked objects and driving the shared state every other
	// person in the instance has to live in. Same line as the object-orbit gag — that one is local
	// too, and for the same reason.
	//
	// Two independent switches, because they break different things:
	//   PLAYER : VRCPlayerApi.SetGravityStrength on the local player. Sanctioned SDK call, the same
	//            one worlds use for low-gravity rooms. Reversible, affects nothing but you.
	//   WORLD  : Physics.gravity, which floats every loose rigidbody on your client. This one can
	//            genuinely break a world FOR YOU — lifts, physics puzzles and doors that rely on
	//            falling stop working — so it is off by default and says so.
	public class GravityModule : IModule
	{
		public override string Name => "Gravity";

		private bool _playerApplied;
		private float _originalPlayer = 1f;

		private bool _worldApplied;
		private Vector3 _originalWorld = new Vector3(0f, -9.81f, 0f);

		private float _nextReassert;

		public static string LastStatus = "";

		public override void OnUpdate()
		{
			try
			{
				ApplyPlayer();
				ApplyWorld();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Gravity] update threw: {e.Message}"); }
		}

		private void ApplyPlayer()
		{
			bool want = ModConfig.GravityPlayerOff.Value;

			if (!want)
			{
				if (!_playerApplied) return;
				// ONLY FORGET ONCE THE RESTORE ACTUALLY LANDED (2026-09-13). The flag used to be
				// cleared unconditionally: a single frame where the local API was momentarily null —
				// exactly what happens around a respawn or a world entry, which is also when VRChat
				// resets gravity — dropped the only record that we had changed anything, and the
				// player floated for the rest of the session with the switch reading OFF. Leaving
				// _playerApplied set means the next frame simply tries again.
				var api0 = PlayerRef.LocalApi();
				if (api0 == null) return;
				try { api0.SetGravityStrength(_originalPlayer); }
				catch { return; }   // transient: keep the flag and retry next frame
				_playerApplied = false;
				LastStatus = "gravity restored";
				return;
			}

			var api = PlayerRef.LocalApi();
			if (api == null) return;

			if (!_playerApplied)
			{
				try { _originalPlayer = api.GetGravityStrength(); } catch { _originalPlayer = 1f; }
				// A world that already runs at zero would otherwise be "restored" to zero later.
				if (_originalPlayer <= 0.001f) _originalPlayer = 1f;
				try { api.SetGravityStrength(0f); } catch { return; }
				_playerApplied = true;
				LastStatus = "gravity off — for you only, nobody else sees you float";
				VRChatArchiveModPlugin.Logger.LogInfo($"[Gravity] player gravity 0 (was {_originalPlayer:F2}).");
				return;
			}

			// VRChat resets gravity on respawn and on world entry, so a one-shot call quietly stops
			// applying after the first teleport. Re-asserted on a slow timer rather than per frame.
			float now = VaClock.Now;
			if (now < _nextReassert) return;
			_nextReassert = now + 1f;
			try { if (api.GetGravityStrength() > 0.001f) api.SetGravityStrength(0f); }
			catch { }
		}

		private void ApplyWorld()
		{
			bool want = ModConfig.GravityWorldOff.Value;

			if (want && !_worldApplied)
			{
				try
				{
					_originalWorld = Physics.gravity;
					if (_originalWorld.sqrMagnitude < 0.001f) _originalWorld = new Vector3(0f, -9.81f, 0f);
					Physics.gravity = Vector3.zero;
					_worldApplied = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[Gravity] world physics gravity 0 (local only).");
				}
				catch { }
			}
			else if (!want && _worldApplied)
			{
				try { Physics.gravity = _originalWorld; } catch { }
				_worldApplied = false;
			}
		}

		// Put everything back. A world left floating after the mod stops would look like the world
		// itself is broken, and the user has no way to tell the difference.
		public override void OnShutdown() => RestoreAll();

		public override void OnSceneLoaded(int buildIndex)
		{
			// The new world sets its own gravity; forget what the old one had so we never "restore"
			// a value that belonged somewhere else.
			_playerApplied = false;
			_originalPlayer = 1f;
			_nextReassert = 0f;
			if (_worldApplied)
			{
				// PUT IT BACK FIRST (2026-09-13). Physics.gravity is a GLOBAL, not a per-scene value:
				// a scene load does not reset it. Clearing the flag on its own left the world at zero
				// gravity with nothing left tracking it — every loose object in the new world floats —
				// and replaced the real pre-mod value with a hardcoded default, so even a later
				// restore would have written the wrong number. Restore, THEN forget.
				try { Physics.gravity = _originalWorld; } catch { }
				_worldApplied = false;
				_originalWorld = new Vector3(0f, -9.81f, 0f);
			}
		}

		private void RestoreAll()
		{
			try
			{
				if (_playerApplied)
				{
					var api = PlayerRef.LocalApi();
					if (api != null) api.SetGravityStrength(_originalPlayer);
					_playerApplied = false;
				}
			}
			catch { }
			try { if (_worldApplied) { Physics.gravity = _originalWorld; _worldApplied = false; } }
			catch { }
		}
	}
}
