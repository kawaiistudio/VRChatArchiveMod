using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FORCE JUMP — a TOGGLE that gives you a working (and strong) jump, even in worlds that switched
	// jumping off.
	//
	// It is ordinary self-movement, the same nature as fly and the speed sliders already in the mod:
	// it writes YOUR OWN jump height over VRChat's sanctioned VRCPlayerApi.SetJumpImpulse — the exact
	// call the SDK gives worlds to decide how high a player jumps. Others see you jump normally, no
	// other client is touched, and there is no world state involved. It is in the "fun" family.
	//
	// WHY A TOGGLE, NOT A ONE-SHOT (the change the owner asked for). Clicking a button to fire a single
	// hop did nothing useful in a world that had disabled jumping — the next real Space press was dead
	// again. As a toggle it OWNS your jump for as long as it is on: it re-applies the impulse every
	// frame, so a world that keeps jump at zero (the way "no-jump" worlds do) is overridden on the very
	// next frame and Space jumps you anyway. Turning it off puts the world's own jump height back
	// exactly as it was.
	public class ForceJumpModule : IModule
	{
		public override string Name => "ForceJump";

		/// <summary>True while we are holding the local player's jump impulse open. Read by the sync
		/// so the client's button can show STOP/START.</summary>
		public static bool Active { get; private set; }
		public static string Status = "";

		// The world's own jump height, captured the moment we take over so toggle-off restores it.
		private static float _origJump = 3f;
		private static bool _captured;

		// The impulse, in metres/second of upward velocity, read live from config so the slider takes
		// effect without a restart. A normal VRChat jump is ~3; the default here is a strong-but-sane
		// hop, clamped so a stray value cannot fling you out of the world.
		public static float ConfiguredForce
		{
			get
			{
				try
				{
					float val = ModConfig.JumpImpulse != null && ModConfig.JumpImpulse.Value > 0f
						? ModConfig.JumpImpulse.Value
						: (ModConfig.ForceJumpForce != null ? ModConfig.ForceJumpForce.Value : 8f);
					return Mathf.Clamp(val, 1f, 50f);
				}
				catch { return 8f; }
			}
			set
			{
				try
				{
					float clamped = Mathf.Clamp(value, 1f, 50f);
					if (ModConfig.JumpImpulse != null) ModConfig.JumpImpulse.Value = clamped;
					if (ModConfig.ForceJumpForce != null) ModConfig.ForceJumpForce.Value = clamped;
				}
				catch { }
			}
		}

		public override void OnUpdate()
		{
			try
			{
				// RightShift+J toggles it, matching the mod's family (RightShift+G = Force Pickup).
				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.J)) Toggle();

				// OWN THE JUMP while on. Re-applied every frame so a world that zeroes the impulse to
				// forbid jumping is overridden on the next frame — this is what lets you jump where the
				// world blocks it. VRChat still only jumps you when grounded, so this is a jump, not a
				// flight. One il2cpp setter per frame, negligible.
				bool shouldHold = Active || (ModConfig.JumpMod != null && ModConfig.JumpMod.Value);
				if (shouldHold)
				{
					var api = PlayerRef.LocalApi();
					if (api != null)
					{
						try { api.SetJumpImpulse(ConfiguredForce); } catch { }
					}
				}
			}
			catch { }
		}

		/// <summary>Flip the toggle. On enable, remembers the world's jump height and takes it over; on
		/// disable, restores it.</summary>
		public static void Toggle()
		{
			try
			{
				var api = PlayerRef.LocalApi();
				if (api == null) { Status = "no local player yet"; return; }

				if (!Active)
				{
					try { _origJump = api.GetJumpImpulse(); _captured = true; }
					catch { _captured = false; }
					try { api.SetJumpImpulse(ConfiguredForce); } catch { }
					Active = true;
					if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = true;
					Status = "on — you can jump at " + ConfiguredForce.ToString("0.#")
						+ " m/s, even where the world blocks it";
					Toast.Show($"Force Jump: ON ({ConfiguredForce:0.#} m/s)");
					VRChatArchiveModPlugin.Logger.LogInfo($"[ForceJump] ON — impulse {ConfiguredForce:0.#}.");
				}
				else
				{
					Active = false;
					if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = false;
					if (_captured) { try { api.SetJumpImpulse(_origJump); } catch { } }
					Status = "off — the world's own jump restored";
					Toast.Show("Force Jump: OFF");
					VRChatArchiveModPlugin.Logger.LogInfo("[ForceJump] OFF.");
				}
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		/// <summary>
		/// One-shot upward launch, kept for any caller that still asks for a single hop. It does NOT
		/// change the toggle; it just sets vertical velocity once. Horizontal momentum is preserved and
		/// Y is set absolutely so a repeat is a re-launch, not an ever-growing rocket.
		/// </summary>
		public static bool Launch(float force = 0f)
		{
			try
			{
				var api = PlayerRef.LocalApi();
				if (api == null) { Status = "no local player yet"; return false; }
				float up = force > 0f ? Mathf.Clamp(force, 1f, 50f) : ConfiguredForce;
				Vector3 v;
				try { v = api.GetVelocity(); }
				catch { v = Vector3.zero; }
				try { api.SetVelocity(new Vector3(v.x, up, v.z)); }
				catch (Exception ex) { Status = "could not apply velocity: " + ex.Message; return false; }
				Status = "jumped (" + up.ToString("0.#") + " m/s up)";
				return true;
			}
			catch (Exception e) { Status = "failed: " + e.Message; return false; }
		}

		public static void Deactivate()
		{
			if (!Active) return;
			Active = false;
			try
			{
				var api = PlayerRef.LocalApi();
				if (api != null && _captured) api.SetJumpImpulse(_origJump);
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// New world = new stock jump height. If we are still on, re-capture so toggle-off restores
			// THIS world's value, not the previous one's; otherwise just forget the old capture.
			if (!Active) { _captured = false; return; }
			try { _origJump = PlayerRef.LocalApi().GetJumpImpulse(); _captured = true; }
			catch { _captured = false; }
		}

		public override void OnShutdown()
		{
			try
			{
				if (Active && _captured)
				{
					var api = PlayerRef.LocalApi();
					if (api != null) api.SetJumpImpulse(_origJump);
				}
			}
			catch { }
			Active = false;
		}
	}
}
