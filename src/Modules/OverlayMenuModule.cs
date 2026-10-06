using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE MENU, DECLARED.
	//
	// Every page below is written once, here, at startup. Nothing in this file runs per frame: the
	// lambdas are stored, and Overlay.Draw() reads the declaration and paints it. That is the whole
	// difference from the uGUI menu, where showing a control meant building one.
	//
	// Controls are bound to their ConfigEntry, not to a copy of it. A toggle IS the setting — it
	// reads it to draw and writes it when the user moves it — so there is no sync step, and none of
	// the "the switch flipped back on its own" class of bug can exist here.
	public class OverlayMenuModule : IModule
	{
		public override string Name => "OverlayMenu";

		public override void OnInitialize()
		{
			Build();
			VRChatArchiveModPlugin.Logger.LogInfo(
				"[Overlay] built: " + Overlay.PageCount + " page(s) — Right-Shift+N opens it (Right-Shift+M is the radar's).");
		}

		public override void OnUpdate()
		{
			// One key check per frame. GetKeyDown is edge-triggered, so no debounce is needed.
			// Right-Shift+N, not +M: +M is the radar's toggle (ModConfig.RadarEnabled, Menu.cs,
			// OrbitModule all say so) and one press was flipping both — the overlay opened and
			// the radar vanished at the same time.
			if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.N))
				Overlay.Visible = !Overlay.Visible;
		}

		public override void OnGui() => Overlay.Draw();

		// ---------------------------------------------------------------- the declaration

		private static void Build()
		{
			Overlay.Clear();

			// ---- MOVEMENT
			Overlay.Page move = Overlay.AddPage("MOVEMENT");

			move.Group("FLY")
				.Toggle("Fly", ModConfig.FlyEnabled)
				.Toggle("Noclip", ModConfig.NoclipEnabled);

			move.Group("SPEED")
				.Toggle("Custom speed", ModConfig.SpeedEnabled)
				.Toggle("Override walk", ModConfig.WalkMod)
				.Toggle("Override run", ModConfig.RunMod)
				.Toggle("Override jump", ModConfig.JumpMod);

			// The numbers only mean anything while custom speed is on, so they hide with it rather
			// than sitting there looking editable and doing nothing.
			// Quarter-unit steps: speeds are read as numbers you can say out loud, and a free float
			// gives 4.3187262 for a value nobody chose.
			move.Group("SPEED VALUES")
				.Slider("Walk", ModConfig.WalkSpeed, 0f, 20f, "F2", 0.25f)
				.Slider("Run", ModConfig.RunSpeed, 0f, 30f, "F2", 0.25f)
				.Slider("Strafe", ModConfig.StrafeSpeed, 0f, 20f, "F2", 0.25f)
				.Slider("Jump impulse", ModConfig.JumpImpulse, 0f, 15f, "F2", 0.25f)
				.OnlyWhen(() => ModConfig.SpeedEnabled.Value);

			move.Group("GRAVITY")
				.Toggle("Player gravity off", ModConfig.GravityPlayerOff)
				.Toggle("World gravity off", ModConfig.GravityWorldOff);

			// ---- VISUALS
			Overlay.Page vis = Overlay.AddPage("VISUALS");

			vis.Group("RADAR")
				.Toggle("Radar", ModConfig.RadarEnabled)
				.Slider("Range (m)", ModConfig.RadarRange, 10f, 500f, "F0", 5f)
				.Slider("Size (px)", ModConfig.RadarSize, 80f, 600f, "F0", 10f)
				.Toggle("Show names", ModConfig.RadarNames)
				.OnlyWhen(() => ModConfig.RadarEnabled.Value);

			vis.Group("RADAR MAP")
				.Toggle("World map", ModConfig.RadarMap)
				.Slider("Resolution", ModConfig.RadarMapResolution, 64, 1024)
				.Slider("Height", ModConfig.RadarMapHeight, 1f, 200f, "F0")
				.Slider("Opacity", ModConfig.RadarMapOpacity, 0f, 1f, "F2")
				.OnlyWhen(() => ModConfig.RadarMap.Value);

			// ---- STATUS: read-only, and proof the overlay costs nothing to keep live
			Overlay.Page info = Overlay.AddPage("STATUS");
			info.Group("SESSION")
				.Label(() => "fps        " + Mathf.RoundToInt(1f / Mathf.Max(VaClock.Delta, 0.0001f)))
				.Label(() => "pages      " + Overlay.PageCount)
				.Action("Close overlay", () => Overlay.Visible = false);
		}
	}
}
