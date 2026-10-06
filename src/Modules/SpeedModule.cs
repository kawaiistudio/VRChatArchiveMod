using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// WALK / RUN / STRAFE / JUMP — your own locomotion, through VRChat's own setters.
	//
	// These are the sanctioned per-player API (VRCPlayerApi.SetWalkSpeed and friends): the same
	// calls a world's Udon uses to make you faster in a race or slower in mud. They apply to the
	// LOCAL player only — there is no API to change anybody else's, and nothing is sent.
	//
	// A world is allowed to set these too, and some do it constantly. So this re-applies on a slow
	// tick rather than once: setting it once means the next world script to touch locomotion wins
	// and the slider silently stops meaning anything. Re-applying twice a second is enough to hold
	// and cheap enough not to matter.
	//
	// RESTORE IS PART OF IT. The values VRChat starts with are captured before the first write, so
	// switching the feature off puts locomotion back exactly as the world wanted it instead of
	// leaving you permanently fast.
	public class SpeedModule : IModule
	{
		public override string Name => "Speed";

		public static string Status = "off";

		// VRChat's stock locomotion, read once from the live player before we ever write.
		private static float _origWalk = 2f, _origRun = 4f, _origStrafe = 2f, _origJump = 3f;
		private static bool _captured;
		private static bool _applied;
		// Per-kind previous toggle state, so a switch that is OFF is left ENTIRELY to the world
		// instead of being pinned every frame (see the jump bug note in OnUpdate).
		private static bool _wasW, _wasR, _wasJ;


		// MANAGED CLOCK for the gate: Stopwatch is a QueryPerformanceCounter read — no interop, no
		// allocation. VaClock.Now is an il2cpp_runtime_invoke that BOXES its float on
		// the il2cpp heap: one native call plus one native allocation per frame, spent nine frames
		// out of ten on deciding "not yet". Measured at 20-100 us a call on this machine (SpawnSound,
		// whose whole frame is two of these reads, sits at 2.5-13 ms/s in the same runs).
		private long _nextApplyTs;
		private static readonly long ApplyEveryTs = System.Diagnostics.Stopwatch.Frequency / 10;   // 100 ms

		public override void OnUpdate()
		{
			try
			{
				// TEN TIMES A SECOND, not every frame.
				//
				// This did run every frame, to stop VRChat's own locomotion and world scripts from
				// winning the value back — twice a second genuinely was too slow for that. But the
				// profiler measured this module at 180 ms/s: at 100+ fps the resolve plus four
				// il2cpp setter calls are anything but free, and a re-assert every 100 ms closes the
				// same gap for a tenth of the cost. Ten times a second is faster than anyone can
				// perceive a speed change, and still far tighter than the half-second that failed.
				long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
				if (nowTs < _nextApplyTs) return;
				_nextApplyTs = nowTs + ApplyEveryTs;

				// While flying, MovementModule zeros locomotion speed so the avatar stays in neutral upright pose.
				if (ModConfig.FlyEnabled.Value) return;

				// PER KIND. Each switch owns one value: what is off keeps the world's own number,
				// what is on is held to yours.
				bool w = ModConfig.WalkMod.Value, r = ModConfig.RunMod.Value, j = ModConfig.JumpMod.Value;

				// EVERY SWITCH OFF AND NOTHING LEFT TO UNDO: not one il2cpp call. The player lookup
				// below is an interop clock read plus a periodic VirtualQuery, and with no setter to
				// follow it, a module that is switched off cost the same as one that is on. Only
				// taken once the stock values are captured, so the first tick after spawn still
				// captures them exactly as before and a later restore is unchanged.
				if (_captured && !w && !r && !j && !_wasW && !_wasR && !_wasJ)
				{
					Status = "off — the world's own values";
					return;
				}

				// A MISSING API IS NOT A NEW WORLD (2026-09-13). This used to clear _captured, which is
				// the snapshot of the world's OWN locomotion values. The local API is momentarily null
				// around a respawn or an avatar load — while our overrides are still applied — so the
				// next tick re-captured the numbers THIS MODULE was currently forcing and recorded them
				// as "the world's own". Every later restore then wrote the modded speeds back, and the
				// world's real values were gone for the session. The snapshot is invalidated only by a
				// genuine world change, which OnSceneLoaded already handles.
				var api = PlayerRef.LocalApi();
				if (api == null) return;

				if (!_captured) Capture(api);

				// THE JUMP BUG. The old code wrote every value EVERY FRAME, even when its toggle was
				// off — writing `_origJump` (a single spawn-time read) forever. If that read returned 0
				// (a world that spawns with jump disabled, or a transient at spawn), jump was pinned to
				// 0 permanently and nothing but turning JumpMod ON could restore it — exactly the
				// "can't jump even when the jump mod is off" report. The fix: while a toggle is OFF we
				// do NOT touch that channel at all, so the WORLD owns it (its avatar/Udon jump works
				// normally). We only write the captured original ONCE, on the frame the toggle flips
				// off, to undo our own override — then hands off completely.
				if (w) { try { api.SetWalkSpeed(ModConfig.WalkSpeed.Value); api.SetStrafeSpeed(ModConfig.StrafeSpeed.Value); } catch { } }
				else if (_wasW) { try { api.SetWalkSpeed(_origWalk); api.SetStrafeSpeed(_origStrafe); } catch { } }

				if (r) { try { api.SetRunSpeed(ModConfig.RunSpeed.Value); } catch { } }
				else if (_wasR) { try { api.SetRunSpeed(_origRun); } catch { } }

				// ZERO MEANS "DON'T OVERRIDE", NOT "CANNOT JUMP".
				//
				// A jump impulse of 0 is a legitimate number that makes jumping physically
				// impossible, and the slider reaches it at the far left — so dragging it down
				// silently took jumping away, with the switch still reading ON. Nobody wants that
				// as a setting; they want "leave it alone". So 0 hands the channel back to the
				// world, exactly as switching the mod off does.
				if (j && ModConfig.JumpImpulse.Value > 0f)
				{
					try { api.SetJumpImpulse(ModConfig.JumpImpulse.Value); } catch { }
				}
				else if (_wasJ) { try { api.SetJumpImpulse(_origJump); } catch { } }

				_wasW = w; _wasR = r;
				_wasJ = j && ModConfig.JumpImpulse.Value > 0f;
				_applied = w || r || j;
				Status = _applied
					? (w ? "walk " + ModConfig.WalkSpeed.Value.ToString("0.#") + " " : "")
					  + (r ? "run " + ModConfig.RunSpeed.Value.ToString("0.#") + " " : "")
					  + (j ? "jump " + ModConfig.JumpImpulse.Value.ToString("0.#") : "")
					: "off — the world's own values";
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// A new world means new stock values — and possibly a world that sets its own on spawn.
			// Clear the edge state too so no spurious one-time "restore" fires in the new world.
			_captured = false;
			_applied = false;
			_wasW = _wasR = _wasJ = false;
		}

		public override void OnShutdown()
		{
			try { if (_applied) Restore(PlayerRef.LocalApi()); } catch { }
		}

		private static void Capture(VRC.SDKBase.VRCPlayerApi api)
		{
			try
			{
				_origWalk = api.GetWalkSpeed();
				_origRun = api.GetRunSpeed();
				_origStrafe = api.GetStrafeSpeed();
				_origJump = api.GetJumpImpulse();
				_captured = true;
			}
			catch { }
		}

		private static void Apply(VRC.SDKBase.VRCPlayerApi api)
		{
			try
			{
				api.SetWalkSpeed(ModConfig.WalkSpeed.Value);
				api.SetRunSpeed(ModConfig.RunSpeed.Value);
				api.SetStrafeSpeed(ModConfig.StrafeSpeed.Value);
				api.SetJumpImpulse(ModConfig.JumpImpulse.Value);
				Status = $"walk {ModConfig.WalkSpeed.Value:F1} · run {ModConfig.RunSpeed.Value:F1} · jump {ModConfig.JumpImpulse.Value:F1}";
			}
			catch (Exception e) { Status = "could not set: " + e.Message; }
		}

		private static void Restore(VRC.SDKBase.VRCPlayerApi api)
		{
			try
			{
				if (api == null || !_captured) return;
				api.SetWalkSpeed(_origWalk);
				api.SetRunSpeed(_origRun);
				api.SetStrafeSpeed(_origStrafe);
				api.SetJumpImpulse(_origJump);
				Status = "restored to the world's own values";
			}
			catch { }
		}

		// So the UI can offer "back to normal" without the user hunting for the numbers.
		public static void ResetToWorld()
		{
			try
			{
				ModConfig.WalkSpeed.Value = _origWalk;
				ModConfig.RunSpeed.Value = _origRun;
				ModConfig.StrafeSpeed.Value = _origStrafe;
				ModConfig.JumpImpulse.Value = _origJump;
				ModConfig.WalkMod.Value = false;
				ModConfig.RunMod.Value = false;
				ModConfig.JumpMod.Value = false;
				ForceJumpModule.Deactivate();
				var api = PlayerRef.LocalApi();
				if (api != null && _captured)
				{
					try { api.SetWalkSpeed(_origWalk); } catch { }
					try { api.SetRunSpeed(_origRun); } catch { }
					try { api.SetStrafeSpeed(_origStrafe); } catch { }
					try { api.SetJumpImpulse(_origJump); } catch { }
				}
				Toast.Show("Movement reset to world");
			}
			catch { }
		}
	}
}
