using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// Immediate-mode (IMGUI) drawing kit for the on-screen menu. This is the polished
	// "glow box" style — dark panels, an animated accent edge, ON/OFF toggles and custom
	// sliders — rendered entirely by us via GUI.DrawTexture / GUI.Button. It does NOT
	// touch VRChat's own menu, so it can't break when the game's obfuscated UI rotates.
	//
	// The visual approach (elite box + corner brackets + neon fill) is adapted from a
	// generic IMGUI styling pattern; all content here is our own and drives only this
	// mod's features.
	public static class GuiKit
	{
		// Accent color (set once by the menu). Cyan by default to match the mod's brand.
		public static Color Accent = new Color(0.22f, 0.78f, 0.96f, 1f);
		public static Color Neon => new Color(
			Mathf.Min(Accent.r * 1.4f, 1f),
			Mathf.Min(Accent.g * 1.4f, 1f),
			Mathf.Min(Accent.b * 1.4f, 1f), 1f);

		private static readonly Color DarkBox = new Color(0.03f, 0.035f, 0.05f, 0.98f);
		private static readonly Color OffGray = new Color(0.18f, 0.19f, 0.22f, 1f);

		private static Texture2D _tex;
		public static Texture2D Pixel
		{
			get
			{
				if (_tex == null)
				{
					_tex = new Texture2D(1, 1);
					_tex.SetPixel(0, 0, Color.white);
					_tex.Apply();
					_tex.hideFlags = HideFlags.HideAndDontSave;
				}
				return _tex;
			}
		}

		// Cached label styles.
		private static GUIStyle _boxLabel, _slider;
		// Public because the overlay draws its own headers and labels and should look like the rest
		// of the kit rather than carry a second, slightly-different copy of the same style.
		// Built from GuiCompat.BaseStyle(), not from GUI.skin.label: reading the skin is a fatal
		// access violation on this build (see GuiCompat.BaseStyle). Written as statements rather
		// than an object initialiser so a dead setter cannot abort the whole construction.
		public static GUIStyle BoxLabel => _boxLabel ??= MakeBoxLabel();
		private static GUIStyle SliderLabel => _slider ??= MakeSliderLabel();

		private static GUIStyle MakeBoxLabel()
		{
			GUIStyle s = GuiCompat.BaseStyle() ?? new GUIStyle();
			try { s.alignment = TextAnchor.MiddleCenter; } catch { }
			try { s.fontStyle = FontStyle.Bold; } catch { }
			try { s.richText = true; } catch { }
			try { s.fontSize = 12; } catch { }
			return s;
		}

		private static GUIStyle MakeSliderLabel()
		{
			GUIStyle s = GuiCompat.BaseStyle() ?? new GUIStyle();
			try { s.richText = true; } catch { }
			try { s.fontStyle = FontStyle.Bold; } catch { }
			try { s.fontSize = 11; } catch { }
			return s;
		}

		// --- primitives ---

		public static void Fill(Rect r, Color c)
		{
			GUI.color = c;
			GUI.DrawTexture(r, Pixel);
			GUI.color = Color.white;
		}

		// Soft, rounded rectangle (anti-aliased corners via Unity's native rounded-texture
		// draw). This is the heart of the "soft" look — every button/card/toggle uses it.
		public static void RoundedFill(Rect r, Color c, float radius)
		{
			if (r.width <= 0f || r.height <= 0f) return;
			radius = Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f);
			var rad = new Vector4(radius, radius, radius, radius);
			GUI.DrawTexture(r, Pixel, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, rad);
		}

		// Rounded outline: a rounded fill in the border colour with the body inset on top.
		public static void RoundedBorder(Rect r, Color body, Color border, float radius, float thick)
		{
			RoundedFill(r, border, radius);
			var inner = new Rect(r.x + thick, r.y + thick, r.width - thick * 2f, r.height - thick * 2f);
			RoundedFill(inner, body, radius - thick);
		}

		// Soft outer glow — a few concentric rounded rects fading outward. Cheap and gentle.
		public static void SoftGlow(Rect r, Color c, float radius, float strength, int layers = 6, float step = 2.4f)
		{
			for (int i = 1; i <= layers; i++)
			{
				float s = i * step;
				var g = new Color(c.r, c.g, c.b, strength / (i * 1.5f));
				RoundedFill(new Rect(r.x - s, r.y - s, r.width + s * 2f, r.height + s * 2f), g, radius + s);
			}
		}

		// Eight L-shaped strokes framing a rect (the "tech corner" look).
		public static void Corners(Rect r, float len, float thick, float offset)
		{
			GUI.DrawTexture(new Rect(r.x - offset, r.y - offset, len, thick), Pixel);
			GUI.DrawTexture(new Rect(r.x - offset, r.y - offset, thick, len), Pixel);
			GUI.DrawTexture(new Rect(r.xMax - len + offset, r.y - offset, len, thick), Pixel);
			GUI.DrawTexture(new Rect(r.xMax - thick + offset, r.y - offset, thick, len), Pixel);
			GUI.DrawTexture(new Rect(r.x - offset, r.yMax - thick + offset, len, thick), Pixel);
			GUI.DrawTexture(new Rect(r.x - offset, r.yMax - len + offset, thick, len), Pixel);
			GUI.DrawTexture(new Rect(r.xMax - len + offset, r.yMax - thick + offset, len, thick), Pixel);
			GUI.DrawTexture(new Rect(r.xMax - thick + offset, r.yMax - len + offset, thick, len), Pixel);
		}

		// The core styled control: a soft, rounded pill with a gentle glow when active or
		// hovered. Every button, toggle, card and slider track goes through this, so the whole
		// menu shares one calm, rounded look. Corner radius scales with height so short pills
		// stay fully rounded and tall cards get a friendly curve.
		public static void Box(Rect rect, bool active)
		{
			bool hovered = rect.Contains(Event.current.mousePosition);
			float radius = Mathf.Clamp(rect.height * 0.42f, 8f, 18f);

			Color accent = active ? Accent : new Color(0.55f, 0.58f, 0.66f);

			// Soft outer glow — brighter when active, a touch on hover.
			float glow = active ? (hovered ? 0.20f : 0.12f) : (hovered ? 0.08f : 0f);
			if (glow > 0f) SoftGlow(rect, accent, radius, glow);

			// Body: a soft dark pill, tinted toward the accent when active. The inactive body is a
			// violet-biased neutral rather than a blue-grey one, so an OFF control sits on the same
			// palette as the redesigned violet chrome instead of reading as a different UI.
			Color body = active
				? new Color(accent.r * 0.28f + 0.05f, accent.g * 0.28f + 0.06f, accent.b * 0.28f + 0.08f, 0.94f)
				: new Color(0.118f, 0.094f, 0.176f, 0.94f);
			if (hovered)
				body = new Color(body.r + 0.05f, body.g + 0.05f, body.b + 0.06f, body.a);

			// Gentle border ring.
			Color border = active
				? new Color(accent.r, accent.g, accent.b, hovered ? 0.85f : 0.55f)
				: new Color(1f, 1f, 1f, hovered ? 0.16f : 0.07f);

			RoundedBorder(rect, body, border, radius, 1.4f);

			// Faint top sheen for a soft, glassy feel.
			var sheen = new Rect(rect.x + 3f, rect.y + 2f, rect.width - 6f, rect.height * 0.42f);
			RoundedFill(sheen, new Color(1f, 1f, 1f, hovered ? 0.05f : 0.03f), radius * 0.7f);

			GUI.color = Color.white;
		}

		// --- controls (return the new value / whether clicked) ---

		public static bool Button(Rect rect, string label)
		{
			Box(rect, true);
			var style = BoxLabel;
			style.normal.textColor = rect.Contains(Event.current.mousePosition) ? Color.white : new Color(0.9f, 0.9f, 0.9f);
			bool clicked = GUI.Button(rect, "", GUIStyle.none);
			GUI.Label(rect, label.ToUpper(), style);
			return clicked;
		}

		public static bool Toggle(Rect rect, string label, bool value)
		{
			Box(rect, value);
			bool clicked = GUI.Button(rect, "", GUIStyle.none);

			string state = value ? "ON" : "OFF";
			string hex = ColorUtility.ToHtmlStringRGB(value ? Neon : Color.gray);
			GUI.Label(rect, $"{label.ToUpper()} [<color=#{hex}>{state}</color>]", BoxLabel);

			return clicked ? !value : value;
		}

		// SEGMENTED CONTROL — pick ONE of N. Returns the chosen index.
		//
		// This exists because a one-of-N choice was being drawn as N independent Toggles, each
		// showing [ON]/[OFF]. That is a different promise: a toggle says "this switch is off, you
		// may turn it on", so three of them side by side read as three unrelated switches that
		// happen to disagree — and nothing said picking one UNPICKS the others. A segmented control
		// says what is true: these are alternatives, exactly one is active, clicking another moves
		// the selection. No ON/OFF text, because a mode is not a state you turn off.
		public static int Segmented(Rect rect, string[] labels, int index)
		{
			if (labels == null || labels.Length == 0) return index;

			float radius = Mathf.Clamp(rect.height * 0.42f, 8f, 18f);
			// One recessed trough holding the segments, so they read as a single control rather
			// than as separate buttons that happen to be adjacent.
			RoundedBorder(rect, new Color(0.075f, 0.06f, 0.118f, 0.94f), new Color(1f, 1f, 1f, 0.07f), radius, 1.2f);

			const float pad = 3f;
			float segW = (rect.width - pad * 2f) / labels.Length;
			int result = index;

			for (int i = 0; i < labels.Length; i++)
			{
				var seg = new Rect(rect.x + pad + i * segW, rect.y + pad, segW, rect.height - pad * 2f);
				bool active = i == index;
				bool hover = seg.Contains(Event.current.mousePosition);
				float r2 = Mathf.Clamp(seg.height * 0.42f, 6f, 16f);

				if (active)
				{
					// The selected segment is a filled accent pill — the only lit thing in the row,
					// so "which one am I on" is answerable at a glance.
					RoundedFill(seg, new Color(Accent.r * 0.55f + 0.06f, Accent.g * 0.42f + 0.05f, Accent.b * 0.62f + 0.10f, 0.98f), r2);
					RoundedBorder(seg, new Color(0f, 0f, 0f, 0f), new Color(Accent.r, Accent.g, Accent.b, 0.75f), r2, 1.2f);
				}
				else if (hover) RoundedFill(seg, new Color(1f, 1f, 1f, 0.05f), r2);

				if (GUI.Button(seg, "", GUIStyle.none)) result = i;

				var st = BoxLabel;
				st.normal.textColor = active ? Color.white
					: (hover ? new Color(0.88f, 0.85f, 0.95f) : new Color(0.663f, 0.624f, 0.769f));
				GUI.Label(seg, labels[i].ToUpper(), st);
			}

			GUI.color = Color.white;
			return result;
		}

		// A toggle for something DESTRUCTIVE. Same control, red instead of accent, so a switch that
		// breaks the world cannot look identical to one that changes a colour.
		public static bool DangerToggle(Rect rect, string label, bool value)
		{
			Color keep = Accent;
			Accent = new Color(0.97f, 0.35f, 0.35f, 1f);
			bool result = Toggle(rect, label, value);
			Accent = keep;
			return result;
		}

		// Horizontal slider with a neon fill. Returns the (possibly changed) value.
		public static float Slider(Rect labelRect, Rect barRect, string label, float value, float min, float max, string format = "F0")
		{
			var ls = SliderLabel;
			ls.normal.textColor = Accent;
			GUI.Label(labelRect, $"{label.ToUpper()}: <color=white>{value.ToString(format)}</color>", ls);

			Box(barRect, true);

			float pad = 6f;
			float innerW = barRect.width - pad * 2f;
			float t = Mathf.InverseLerp(min, max, value);
			float fillW = innerW * t;
			float cy = barRect.y + barRect.height * 0.5f;
			bool hovered = barRect.Contains(Event.current.mousePosition);

			// empty track (soft, rounded)
			RoundedFill(new Rect(barRect.x + pad, cy - 2f, innerW, 4f), new Color(0.06f, 0.06f, 0.08f, 1f), 2f);
			// filled (rounded accent)
			if (t > 0f)
				RoundedFill(new Rect(barRect.x + pad, cy - 2f, fillW, 4f), Neon, 2f);
			// handle (soft round knob with a glow)
			float hx = barRect.x + pad + fillW;
			float hr = barRect.height * 0.32f;
			var knob = new Rect(hx - hr, cy - hr, hr * 2f, hr * 2f);
			SoftGlow(knob, Neon, hr, hovered ? 0.25f : 0.15f, 4, 1.8f);
			RoundedFill(knob, Color.white, hr);
			GUI.color = Color.white;

			if ((Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag) && hovered)
			{
				float mt = (Event.current.mousePosition.x - (barRect.x + pad)) / innerW;
				float nv = Mathf.Clamp(min + mt * (max - min), min, max);
				value = nv;
				Event.current.Use();
			}
			return value;
		}

		// Animated accent brackets floating just outside a rect (window chrome).
		public static void Brackets(Rect r, float offset, float length, float thick)
		{
			float xMin = r.x - offset, xMax = r.xMax + offset, yMin = r.y - offset, yMax = r.yMax + offset;
			float a = Mathf.PingPong(VaClock.Now * 2f, 0.4f) + 0.6f;
			for (int i = 1; i <= 10; i++)
			{
				GUI.color = new Color(Accent.r, Accent.g, Accent.b, 0.04f / i * a);
				RawBrackets(xMin, xMax, yMin, yMax, length + i * 1.4f, thick + i * 0.7f);
			}
			GUI.color = new Color(Accent.r, Accent.g, Accent.b, a);
			RawBrackets(xMin, xMax, yMin, yMax, length, thick);
			GUI.color = Color.white;
		}

		private static void RawBrackets(float xMin, float xMax, float yMin, float yMax, float l, float t)
		{
			GUI.DrawTexture(new Rect(xMin, yMin, l, t), Pixel);
			GUI.DrawTexture(new Rect(xMin, yMin, t, l), Pixel);
			GUI.DrawTexture(new Rect(xMax - l, yMin, l, t), Pixel);
			GUI.DrawTexture(new Rect(xMax - t, yMin, t, l), Pixel);
			GUI.DrawTexture(new Rect(xMin, yMax - t, l, t), Pixel);
			GUI.DrawTexture(new Rect(xMin, yMax - l, t, l), Pixel);
			GUI.DrawTexture(new Rect(xMax - l, yMax - t, l, t), Pixel);
			GUI.DrawTexture(new Rect(xMax - t, yMax - l, t, l), Pixel);
		}

		public static void Grid(Rect area, int step = 34)
		{
			GUI.color = new Color(1f, 1f, 1f, 0.025f);
			for (int i = 0; i < area.width; i += step)
				GUI.DrawTexture(new Rect(area.x + i, area.y, 1, area.height), Pixel);
			for (int j = 0; j < area.height; j += step)
				GUI.DrawTexture(new Rect(area.x, area.y + j, area.width, 1), Pixel);
			GUI.color = Color.white;
		}
	}
}
