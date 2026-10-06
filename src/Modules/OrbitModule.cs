using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ORBIT / SIT — two ways to park yourself relative to another player.
	//
	// Both are ordinary SELF-MOVEMENT. They drive the LOCAL player through the same sanctioned
	// entry point the fly and turn code already uses (VRCPlayerApi.TeleportTo), once per frame.
	// Nothing is sent to anybody, no other player's transform is touched, and no networked object
	// is written: your own position syncs exactly the way it does when you walk. Which also means
	// the honest caveat — everyone in the instance SEES you circling or perched on them. This
	// hides nothing and is not meant to.
	//
	//   Orbit : circle the target at a fixed radius and height, always facing them.
	//   Sit   : ride the top of the target's avatar, following it as they move.
	//
	// Any movement key cancels both, so the mode can never take your controls hostage.
	public class OrbitModule : IModule
	{
		public override string Name => "Orbit";

		public enum Mode { Off, Orbit, Sit }

		public static Mode Current { get; private set; } = Mode.Off;
		public static string TargetUid { get; private set; }
		public static string TargetName { get; private set; }

		private static float _angle;          // degrees around the target, orbit mode
		private static int _missing;          // consecutive frames the target could not be resolved


		public static bool Active => Current != Mode.Off;

		public static void Start(Mode mode, VaTagsModule.PlayerEntry entry)
		{
			if (mode == Mode.Off || entry == null) { Stop("no target"); return; }
			if (entry.IsLocal) { VaTagsModule.LastStatus = "that's you — pick another player"; return; }
			if (entry.Transform == null) { VaTagsModule.LastStatus = "can't reach that player's position yet"; return; }

			Current = mode;
			TargetUid = entry.UserId;
			TargetName = entry.Name;
			_missing = 0;

			// Start from where you already stand so the first frame is not a jump across the room.
			try
			{
				var api = PlayerRef.LocalApi();
				if (api != null)
				{
					Vector3 d = api.GetPosition() - entry.Transform.position;
					d.y = 0f;
					if (d.sqrMagnitude > 0.01f) _angle = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
				}
			}
			catch { }

			VaTagsModule.LastStatus = (mode == Mode.Orbit ? "orbiting " : "sitting on ") + entry.Name
				+ " — press the button again to stop";
			VRChatArchiveModPlugin.Logger.LogInfo($"[Orbit] {mode} started on {entry.Name}.");
		}

		public static void Stop(string why)
		{
			// Nothing was orbiting: do not touch the player at all. This is the path a scene change takes
			// (OnSceneLoaded => Stop("")), on the very first frame, before the local player exists and
			// before the il2cpp player APIs are safe to call -- reaching into PlayerRef here was an
			// access violation that took the whole process down on load.
			if (Current == Mode.Off) return;
			Current = Mode.Off;
			TargetUid = null;
			TargetName = null;
			// The orbit handed the network a velocity every frame; now that it has ended, hand it a zero
			// once. Only reached when an orbit really was running, so the local player is present.
			try { var lp = PlayerRef.LocalPlayer(); if (lp != null) PlayerRef.ZeroVelocity(lp); } catch { }
			if (!string.IsNullOrEmpty(why)) VaTagsModule.LastStatus = why;
		}

		public static void Toggle(Mode mode, VaTagsModule.PlayerEntry entry)
		{
			bool same = Current == mode && entry != null
				&& string.Equals(TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
			if (same) Stop("stopped");
			else Start(mode, entry);
		}

		private bool _hotkeyWasDown;

		// Previous frame's orbit position, for the velocity the network is handed (see OnUpdate).
		private static Vector3 _prevPos;
		private static bool _velValid;

		public override void OnUpdate()
		{
			// RightShift+O throws the world's props into a ring around you (ObjectOrbitModule).
			// Same edge-detect shape as the radar's RightShift+M, so the two hotkeys behave alike.
			try
			{
				bool combo = Input.GetKey(KeyCode.RightShift) && Input.GetKey(KeyCode.O);
				if (combo && !_hotkeyWasDown) ObjectOrbitModule.ToggleOnSelf();
				_hotkeyWasDown = combo;
			}
			catch { }

			if (Current == Mode.Off) { _velValid = false; return; }   // next start begins with no stale velocity

			// SCROLL = RADIUS, while orbiting. Reaching for the slider in the mod menu to nudge how
			// far out you circle means opening the menu, which hides the very thing you are
			// adjusting. The wheel does it while you watch.
			//
			// Skipped while the mod menu is open: the wheel belongs to its scroll views then.
			if (Current == Mode.Orbit && !Menu.Visible)
			{
				try
				{
					float w = Input.GetAxis("Mouse ScrollWheel");
					if (Mathf.Abs(w) > 0.0001f)
					{
						// Stepped proportionally to the current radius: a fixed step feels glacial
						// at 8m and violent at 1m.
						float r = ModConfig.OrbitRadius.Value;
						ModConfig.OrbitRadius.Value = Mathf.Clamp(r + w * Mathf.Max(1f, r) * 2f, 0.5f, 20f);
					}
				}
				catch { }
			}

			try
			{
				// NO input cancel. An earlier version stopped on WASD / Space / Escape as a safety
				// valve, but Escape is pressed constantly in normal play and it kept switching the
				// mode off behind the user's back — which made the buttons look like they were not
				// toggles at all. The menu button is the off switch, and it is the only one.
				var entry = Resolve();
				if (entry == null || entry.Transform == null)
				{
					// One missing frame is a roster rebuild, not a departure. Only a run of them is.
					if (++_missing < 30) return;
					Stop("stopped — " + (TargetName ?? "target") + " is no longer in the instance");
					return;
				}
				_missing = 0;
				Transform tt = entry.Transform;

				var api = PlayerRef.LocalApi();
				if (api == null) return;

				Vector3 tp = tt.position;

				Vector3 pos;
				Quaternion rot;
				if (Current == Mode.Orbit)
				{
					_angle += ModConfig.OrbitSpeed.Value * VaClock.Delta;
					if (_angle >= 360f) _angle -= 360f;
					float r = Mathf.Max(0.5f, ModConfig.OrbitRadius.Value);
					float rad = _angle * Mathf.Deg2Rad;
					pos = new Vector3(tp.x + Mathf.Cos(rad) * r,
						tp.y + ModConfig.OrbitHeight.Value,
						tp.z + Mathf.Sin(rad) * r);

					// Face the target, flat: VRChat keeps players upright, so a pitched look
					// rotation only leaves the rig crooked.
					Vector3 look = tp - pos;
					look.y = 0f;
					rot = look.sqrMagnitude > 0.0001f
						? Quaternion.LookRotation(look.normalized, Vector3.up)
						: Quaternion.identity;
				}
				else
				{
					// Top of the target's actual avatar, measured from its renderers rather than
					// assumed: avatars range from knee-high to giant, so a fixed offset would put
					// you inside a tall one and far above a small one.
					float top = MeshTop(tt, tp.y);
					pos = new Vector3(tp.x, top + ModConfig.SitHeight.Value, tp.z);
					Vector3 fwd = tt.forward;
					fwd.y = 0f;
					rot = fwd.sqrMagnitude > 0.0001f
						? Quaternion.LookRotation(fwd.normalized, Vector3.up)
						: Quaternion.identity;
				}

				// MOVE, DO NOT TELEPORT — this is what other players see.
				//
				// TeleportTo every frame made the orbit stutter for everyone else. VRChat samples a
				// remote player's position on a slow tick and INTERPOLATES between samples, which is
				// what makes normal walking look smooth at a fraction of the frame rate. A teleport
				// is the one thing that is not interpolated: it is meant to be a jump, so remote
				// clients snap to it. Circling with it means one snap per sync, i.e. a polygon
				// instead of a circle.
				//
				// Writing the transform is the same path the fly code uses, and it reads as ordinary
				// movement, so their client smooths it the way it smooths walking.
				// WHAT THE NETWORK NEEDS TO DRAW A CIRCLE: a VELOCITY. VRChat serialises a remote
				// player as position + velocity on a slow tick and dead-reckons in between; a player
				// whose position changes but whose velocity reads zero is drawn stepping from sample
				// to sample, which is the stutter other people saw. So the orbit's own tangential
				// velocity is set every frame (it also cancels the gravity build-up the old
				// ZeroVelocity call was there for). And the rotation nudge no longer uses the plain
				// TeleportTo: that overload is a SNAP by contract, so every 4 degrees the remote rig
				// jumped. lerpOnRemote is VRChat's own flag for "interpolate this on other clients".
				float dtv = Mathf.Max(VaClock.Delta, 0.0001f);
				Vector3 vel = _velValid ? (pos - _prevPos) / dtv : Vector3.zero;
				if (vel.sqrMagnitude > 30f * 30f) vel = vel.normalized * 30f;   // a teleport-in, not a velocity
				_prevPos = pos; _velValid = true;

				var t = PlayerRef.LocalTransform();
				if (t != null)
				{
					t.position = pos;
					if (Quaternion.Angle(t.rotation, rot) > 4f)
						api.TeleportTo(pos, rot, VRC.SDKBase.VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, true);
				}
				else
				{
					api.TeleportTo(pos, rot, VRC.SDKBase.VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, true);
				}
				try { api.SetVelocity(vel); } catch { }

				// (Velocity is now written above every frame; zeroing it here again would hand the
				// network the very zero that made the orbit stutter for everyone else.)
			}
			catch (Exception e)
			{
				Stop("stopped — " + e.Message);
			}
		}

		// Highest point of the target's renderers, in world space. Falls back to the transform's
		// own height plus a person-sized guess when an avatar exposes nothing measurable.
		private static float MeshTop(Transform root, float fallbackY)
		{
			try
			{
				var rends = root.GetComponentsInChildren<Renderer>(false);
				if (rends != null)
				{
					float top = float.NegativeInfinity;
					foreach (var r in rends)
					{
						if (r == null) continue;
						try { if (r.bounds.max.y > top) top = r.bounds.max.y; } catch { }
					}
					if (top > float.NegativeInfinity && top - fallbackY < 40f) return top;
				}
			}
			catch { }
			return fallbackY + 1.8f;
		}

		private static VaTagsModule.PlayerEntry Resolve()
		{
			if (string.IsNullOrEmpty(TargetUid)) return null;
			try
			{
				foreach (var p in VaTagsModule.Roster)
				{
					if (p == null) continue;
					if (string.Equals(p.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) return p;
				}
			}
			catch { }
			return null;
		}

		public override void OnSceneLoaded(int buildIndex) => Stop("");
	}
}
