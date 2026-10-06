using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	/// <summary>
	/// Curated color palettes and theme presets for ESP and Active Features HUD.
	/// </summary>
	public static class ColorPalette
	{
		public struct NamedColor
		{
			public string Name;
			public Color Color;
			public NamedColor(string name, Color color) { Name = name; Color = color; }
		}

		public struct HudPreset
		{
			public string Name;
			public int MovementIdx;
			public int EspIdx;
			public int UtilityIdx;
			public int SecurityIdx;
			public HudPreset(string name, int mov, int esp, int util, int sec)
			{
				Name = name;
				MovementIdx = mov;
				EspIdx = esp;
				UtilityIdx = util;
				SecurityIdx = sec;
			}
		}

		public static readonly NamedColor[] EspPlayerColors = new NamedColor[]
		{
			new NamedColor("Trust Rank", Color.clear), // clear = use native VRChat Trust Rank color
			new NamedColor("Cyan", new Color(0.00f, 0.85f, 1.00f, 1f)),
			new NamedColor("Neon Green", new Color(0.18f, 0.95f, 0.40f, 1f)),
			new NamedColor("Gold Yellow", new Color(1.00f, 0.85f, 0.15f, 1f)),
			new NamedColor("Vibrant Orange", new Color(1.00f, 0.55f, 0.15f, 1f)),
			new NamedColor("Ruby Red", new Color(1.00f, 0.25f, 0.25f, 1f)),
			new NamedColor("Hot Pink", new Color(1.00f, 0.35f, 0.75f, 1f)),
			new NamedColor("Purple", new Color(0.70f, 0.35f, 1.00f, 1f)),
			new NamedColor("Ice White", new Color(0.95f, 0.95f, 1.00f, 1f))
		};

		public static readonly NamedColor[] EspItemColors = new NamedColor[]
		{
			new NamedColor("Gold", new Color(1.00f, 0.75f, 0.20f, 1f)),
			new NamedColor("Cyan", new Color(0.00f, 0.85f, 1.00f, 1f)),
			new NamedColor("Lime Green", new Color(0.20f, 0.95f, 0.40f, 1f)),
			new NamedColor("Hot Pink", new Color(1.00f, 0.35f, 0.75f, 1f)),
			new NamedColor("Violet", new Color(0.70f, 0.35f, 1.00f, 1f)),
			new NamedColor("Pure White", new Color(0.95f, 0.95f, 1.00f, 1f)),
			new NamedColor("Crimson", new Color(1.00f, 0.25f, 0.25f, 1f))
		};

		public static readonly NamedColor[] EspPortalColors = new NamedColor[]
		{
			new NamedColor("Purple", new Color(0.66f, 0.36f, 1.00f, 1f)),
			new NamedColor("Magenta", new Color(1.00f, 0.25f, 0.80f, 1f)),
			new NamedColor("Cyan", new Color(0.00f, 0.85f, 1.00f, 1f)),
			new NamedColor("Emerald", new Color(0.18f, 0.92f, 0.45f, 1f)),
			new NamedColor("Sun Gold", new Color(1.00f, 0.80f, 0.20f, 1f)),
			new NamedColor("Sky Blue", new Color(0.25f, 0.60f, 1.00f, 1f)),
			new NamedColor("Crimson", new Color(1.00f, 0.25f, 0.25f, 1f))
		};

		public static readonly NamedColor[] HudColors = new NamedColor[]
		{
			new NamedColor("Amber Yellow", new Color(1.00f, 0.88f, 0.20f, 1f)),
			new NamedColor("Neon Cyan", new Color(0.00f, 0.85f, 1.00f, 1f)),
			new NamedColor("Lime Green", new Color(0.25f, 0.95f, 0.45f, 1f)),
			new NamedColor("Purple Violet", new Color(0.75f, 0.50f, 1.00f, 1f)),
			new NamedColor("Neon Pink", new Color(1.00f, 0.40f, 0.75f, 1f)),
			new NamedColor("Bright Orange", new Color(1.00f, 0.60f, 0.20f, 1f)),
			new NamedColor("Coral Red", new Color(0.98f, 0.30f, 0.30f, 1f)),
			new NamedColor("Clean White", new Color(0.95f, 0.95f, 0.98f, 1f))
		};

		public static readonly HudPreset[] HudPresets = new HudPreset[]
		{
			new HudPreset("Classic Vibrant", 0, 1, 2, 6),
			new HudPreset("Cyberpunk Neon", 1, 4, 2, 0),
			new HudPreset("Purple Twilight", 3, 4, 1, 6),
			new HudPreset("Emerald Matrix", 2, 2, 1, 7),
			new HudPreset("Solar Amber", 5, 0, 3, 6)
		};
	}
}
