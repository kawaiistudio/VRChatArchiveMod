using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	/// <summary>
	/// Displays plain text active feature status (e.g. fly on, ESP Portal, ESP Player)
	/// in the top-left corner with custom colors and drop shadow.
	/// </summary>
	public class ActiveFeaturesHudModule : IModule
	{
		public override string Name => "ActiveFeaturesHud";

		private static GUIStyle _textStyle;
		private static GUIStyle _shadowStyle;
		private static float _lastScale = -1f;

		private struct FeatureEntry
		{
			public string Text;
			public Color Color;
			public FeatureEntry(string text, Color color)
			{
				Text = text;
				Color = color;
			}
		}

		private static readonly List<FeatureEntry> _entries = new List<FeatureEntry>();

		private static void EnsureStyles()
		{
			int baseSize = ModConfig.ActiveFeaturesFontSize != null ? ModConfig.ActiveFeaturesFontSize.Value : 11;
			if (baseSize < 8 || baseSize > 24) baseSize = 11;

			// Scale lightly with resolution: 11px at 1080p, ~12-13px at 1440p
			float sc = Mathf.Clamp(Hud.Scale, 1.0f, 1.35f);
			int fontSize = Mathf.RoundToInt(baseSize * (sc > 1f ? (1f + (sc - 1f) * 0.4f) : 1f));

			if (_textStyle != null && _textStyle.fontSize == fontSize) return;

			_textStyle = new GUIStyle
			{
				fontSize = fontSize,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.UpperLeft,
				richText = true
			};
			_shadowStyle = new GUIStyle
			{
				fontSize = fontSize,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.UpperLeft,
				richText = true
			};
		}

		public override void OnGui()
		{
			try
			{
				if (Event.current.type != EventType.Repaint) return;
				if (ModConfig.ActiveFeaturesHudEnabled != null && !ModConfig.ActiveFeaturesHudEnabled.Value) return;

				EnsureStyles();
				DrawFeatures();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[ActiveFeaturesHud] draw threw: {e.Message}");
			}
		}

		public static Color GetMovementColor()
		{
			try
			{
				int idx = ModConfig.HudMovementColorIndex != null ? ModConfig.HudMovementColorIndex.Value : 0;
				if (idx < 0 || idx >= ColorPalette.HudColors.Length) idx = 0;
				return ColorPalette.HudColors[idx].Color;
			}
			catch { return new Color(1f, 0.88f, 0.20f); }
		}

		public static Color GetEspColor()
		{
			try
			{
				int idx = ModConfig.HudEspColorIndex != null ? ModConfig.HudEspColorIndex.Value : 1;
				if (idx < 0 || idx >= ColorPalette.HudColors.Length) idx = 1;
				return ColorPalette.HudColors[idx].Color;
			}
			catch { return new Color(0.0f, 0.85f, 1.0f); }
		}

		public static Color GetUtilityColor()
		{
			try
			{
				int idx = ModConfig.HudUtilityColorIndex != null ? ModConfig.HudUtilityColorIndex.Value : 2;
				if (idx < 0 || idx >= ColorPalette.HudColors.Length) idx = 2;
				return ColorPalette.HudColors[idx].Color;
			}
			catch { return new Color(0.25f, 0.95f, 0.45f); }
		}

		public static Color GetSecurityColor()
		{
			try
			{
				int idx = ModConfig.HudSecurityColorIndex != null ? ModConfig.HudSecurityColorIndex.Value : 6;
				if (idx < 0 || idx >= ColorPalette.HudColors.Length) idx = 6;
				return ColorPalette.HudColors[idx].Color;
			}
			catch { return new Color(0.98f, 0.30f, 0.30f); }
		}

		private void CollectActiveFeatures()
		{
			_entries.Clear();

			Color movColor = GetMovementColor();
			Color espColor = GetEspColor();
			Color utilColor = GetUtilityColor();
			Color secColor = GetSecurityColor();

			// 1. Movement features
			if (ModConfig.FlyEnabled != null && ModConfig.FlyEnabled.Value)
				_entries.Add(new FeatureEntry("fly on", movColor));

			if (ModConfig.ClickTpEnabled != null && ModConfig.ClickTpEnabled.Value)
				_entries.Add(new FeatureEntry("Click TP", movColor));

			if (ModConfig.WalkMod != null && ModConfig.WalkMod.Value)
				_entries.Add(new FeatureEntry("Walk Mod", movColor));

			if (ModConfig.RunMod != null && ModConfig.RunMod.Value)
				_entries.Add(new FeatureEntry("Run Mod", movColor));

			if (ForceJumpModule.Active || (ModConfig.JumpMod != null && ModConfig.JumpMod.Value))
				_entries.Add(new FeatureEntry("Force Jump", movColor));

			// 2. ESP features
			if (ModConfig.EspEnabled != null && ModConfig.EspEnabled.Value)
				_entries.Add(new FeatureEntry("ESP Box", espColor));

			if (ModConfig.EspPortals != null && ModConfig.EspPortals.Value)
				_entries.Add(new FeatureEntry("ESP Portal", espColor));

			if (ModConfig.EspCapsule != null && ModConfig.EspCapsule.Value)
				_entries.Add(new FeatureEntry("ESP Player", espColor));

			if (ModConfig.EspHighlight != null && ModConfig.EspHighlight.Value)
				_entries.Add(new FeatureEntry("ESP Outline", espColor));

			if (ModConfig.EspItems != null && ModConfig.EspItems.Value)
				_entries.Add(new FeatureEntry("ESP Pickup", espColor));

			if (ModConfig.EspThroughWalls != null && ModConfig.EspThroughWalls.Value)
				_entries.Add(new FeatureEntry("See through walls", espColor));

			if (ModConfig.RadarEnabled != null && ModConfig.RadarEnabled.Value)
				_entries.Add(new FeatureEntry("Radar", espColor));

			// 3. Stealth / Utility / Fun
			if (GhostModule.Active)
				_entries.Add(new FeatureEntry("Ghost on", utilColor));

			if (QuickMenuTabModule.FullbrightActive)
				_entries.Add(new FeatureEntry("Fullbright", utilColor));

			if (ForcePickupModule.Active)
				_entries.Add(new FeatureEntry("Force Pickup", utilColor));

			if (PlayerGrabModule.Active)
				_entries.Add(new FeatureEntry("Player Grab", utilColor));

			if (ObjectOrbitModule.Active)
				_entries.Add(new FeatureEntry("Object Orbit", utilColor));

			if (ElevatorModule.Active)
				_entries.Add(new FeatureEntry("Elevator", utilColor));

			if (PlayerRotatorModule.Active)
				_entries.Add(new FeatureEntry("Player Rotator", utilColor));

			if (ModConfig.FastSync != null && ModConfig.FastSync.Value)
				_entries.Add(new FeatureEntry("Fast Sync", utilColor));

			if (ModConfig.SelfHide != null && ModConfig.SelfHide.Value)
				_entries.Add(new FeatureEntry("Self Hide", utilColor));

			if (BoxDropModule.Active)
				_entries.Add(new FeatureEntry("Box Drop", utilColor));

			if (QuickMenuTabModule.DeafenActive)
				_entries.Add(new FeatureEntry("Deafen", utilColor));

			if (BadAppleModule.Playing)
				_entries.Add(new FeatureEntry("Bad Apple", utilColor));

			// 4. Security / Blocker features
			if (ModConfig.UdonBlockCrashers != null && ModConfig.UdonBlockCrashers.Value)
				_entries.Add(new FeatureEntry("Block Crashers", secColor));

			if (ModConfig.UdonBlockAll != null && ModConfig.UdonBlockAll.Value)
				_entries.Add(new FeatureEntry("Udon Block All", secColor));

			if (ModConfig.UdonLogEnabled != null && ModConfig.UdonLogEnabled.Value)
				_entries.Add(new FeatureEntry("Udon Console", espColor));

			if (ModConfig.GlobalUdonInteract != null && ModConfig.GlobalUdonInteract.Value)
				_entries.Add(new FeatureEntry("Global Udon", utilColor));
		}

		private void DrawFeatures()
		{
			CollectActiveFeatures();
			if (_entries.Count == 0) return;

			float startX = Hud.S(14f);
			float startY = Hud.S(14f);
			float lineH = _textStyle.fontSize + 3f;
			float textW = Hud.S(300f);

			for (int i = 0; i < _entries.Count; i++)
			{
				var entry = _entries[i];
				float y = startY + i * lineH;
				var r = new Rect(startX, y, textW, lineH);

				// Black shadow pass (1px offset for clean small text)
				_shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
				GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), entry.Text, _shadowStyle);

				// Foreground colored text
				_textStyle.normal.textColor = entry.Color;
				GUI.Label(r, entry.Text, _textStyle);
			}
		}
	}
}
