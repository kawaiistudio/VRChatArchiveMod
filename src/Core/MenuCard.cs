using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace VRChatArchiveMod.Core
{
	// ONE recipe for turning a cloned VRChat menu card into one of ours.
	//
	// This lived twice — once in the QuickMenu tab, once in the per-user menu — and the two
	// drifted: the tab got the sprite repair, the white text and the hover sound, the user-menu
	// card did not, so it sat there greyed out next to its neighbours. Every hard-won detail is
	// now in one place:
	//
	//   * strip only the ROOT's foreign components, so VRChat's own handler cannot still fire the
	//     donor's action while the children keep their font and sprites
	//   * reuse the EXISTING Graphic — Unity allows one per object, and these cards already carry
	//     a UIInvisibleGraphic, so AddComponent<Image> returns null and the next line throws
	//   * unlock the CanvasGroup, which is how VRChat greys a card out
	//   * paint the Background, because it has no StyleElement and nothing themes it in a page the
	//     game never opens
	//   * force the label white, otherwise a cloned card keeps a dimmed "disabled" look
	//   * hover/press through Unity's own transition, plus the game's click sound
	public static class MenuCard
	{
		public static readonly Color Bg = new Color(0.38f, 0.39f, 0.42f, 1.0f);     // VRTool solid opaque gray
		public static readonly Color On = new Color(0.26f, 0.28f, 0.32f, 1.0f);     // active/lit state - solid darker gray
		public static readonly Color BorderOff = new Color(0.48f, 0.50f, 0.54f, 1.0f); // clean solid border
		public static readonly Color BorderOn  = new Color(0.75f, 0.78f, 0.85f, 1.0f); // bright solid border when active
		public static readonly Color Glow = new Color(0.4f, 0.4f, 0.45f, 0.25f);
		public static readonly Color GlowOn  = new Color(0.6f, 0.65f, 0.8f, 0.45f);
		public static readonly Color GlowOff = new Color(0.2f, 0.2f, 0.25f, 0.2f);
		public static readonly Color IconDimmed = new Color(0.50f, 0.52f, 0.56f, 0.75f); // Dimmed gray for inactive logo or active X

		private static Sprite _crossSprite;

		public static Sprite CrossSprite()
		{
			if (_crossSprite != null) return _crossSprite;
			try
			{
				const int N = 64;
				const float thick = 6.0f;
				const float p1 = 14f, p2 = 50f;
				var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				var px = new Color[N * N];
				for (int y = 0; y < N; y++)
				{
					for (int x = 0; x < N; x++)
					{
						float d1 = DistSegment(x, y, p1, p1, p2, p2);
						float d2 = DistSegment(x, y, p1, p2, p2, p1);
						float d = Mathf.Min(d1, d2);
						float alpha = Mathf.Clamp01((thick - d) / 1.5f + 0.5f);
						px[y * N + x] = new Color(1f, 1f, 1f, alpha);
					}
				}
				tex.SetPixels(px);
				tex.Apply(false, false);

				_crossSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f));
				if (_crossSprite != null) _crossSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] cross sprite failed: {e.Message}"); }
			return _crossSprite;
		}

		private static Sprite _checkSprite;
		public static Sprite CheckSprite()
		{
			if (_checkSprite != null) return _checkSprite;
			try
			{
				const int N = 64;
				const float thick = 5.5f;
				const float p1x = 14f, p1y = 30f;
				const float p2x = 26f, p2y = 16f;
				const float p3x = 50f, p3y = 48f;
				var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				var px = new Color[N * N];
				for (int y = 0; y < N; y++)
				{
					for (int x = 0; x < N; x++)
					{
						float d1 = DistSegment(x, y, p1x, p1y, p2x, p2y);
						float d2 = DistSegment(x, y, p2x, p2y, p3x, p3y);
						float d = Mathf.Min(d1, d2);
						float alpha = Mathf.Clamp01((thick - d) / 1.5f + 0.5f);
						px[y * N + x] = new Color(1f, 1f, 1f, alpha);
					}
				}
				tex.SetPixels(px);
				tex.Apply(false, false);

				_checkSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f));
				if (_checkSprite != null) _checkSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] check sprite failed: {e.Message}"); }
			return _checkSprite;
		}

		private static readonly byte[] BrickBytes = new byte[921] {
			0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
			0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x20, 0x08, 0x06, 0x00, 0x00, 0x00, 0x73, 0x7A, 0x7A,
			0xF4, 0x00, 0x00, 0x03, 0x60, 0x49, 0x44, 0x41, 0x54, 0x78, 0x01, 0xED, 0xC1, 0xCB, 0x6B, 0x5C,
			0x55, 0x1C, 0xC0, 0xF1, 0xEF, 0xEF, 0xDC, 0x33, 0xF7, 0xE4, 0x3E, 0xE6, 0xE6, 0xE6, 0x61, 0x68,
			0x6C, 0x4A, 0x15, 0x8A, 0xBA, 0x74, 0xE1, 0x1F, 0xD0, 0xA5, 0xCB, 0xFE, 0x01, 0x59, 0x97, 0x6E,
			0x24, 0x6E, 0xA4, 0x04, 0x5B, 0x0C, 0xD8, 0xA2, 0x90, 0x8A, 0xB8, 0xEB, 0x46, 0x14, 0x6C, 0xF7,
			0x75, 0x21, 0xB8, 0x8C, 0x3B, 0x97, 0x75, 0xA7, 0xA0, 0x8B, 0x30, 0x79, 0x74, 0x12, 0xCC, 0x63,
			0x32, 0x93, 0xCC, 0xDC, 0x73, 0xEF, 0x3D, 0x26, 0xD8, 0xE0, 0x65, 0x9C, 0x34, 0xCF, 0x22, 0x48,
			0x3F, 0x1F, 0x5E, 0xF9, 0xAF, 0x09, 0x17, 0x68, 0xAD, 0xD9, 0x7C, 0x27, 0xB7, 0x76, 0x06, 0x91,
			0xDF, 0x44, 0xA9, 0x87, 0x93, 0x93, 0x93, 0x5D, 0x8E, 0x21, 0x5C, 0x80, 0xE5, 0x46, 0xE3, 0x0D,
			0xA5, 0xD4, 0x27, 0x45, 0x51, 0x4C, 0x03, 0x9A, 0xBF, 0x2D, 0xF9, 0xC6, 0xDC, 0xAD, 0xF9, 0xFE,
			0xA3, 0x91, 0x91, 0x91, 0x9C, 0x23, 0x08, 0xE7, 0xB0, 0xB4, 0xB8, 0x78, 0x09, 0xB8, 0x8D, 0xC8,
			0x2D, 0x60, 0x88, 0xC1, 0x7E, 0xC5, 0xB9, 0xD9, 0xA9, 0xAB, 0x57, 0x9F, 0x30, 0x80, 0x70, 0x06,
			0x8D, 0x46, 0x23, 0x15, 0xF8, 0x10, 0xE7, 0x66, 0x80, 0x94, 0x3E, 0xCA, 0xF3, 0x10, 0xA0, 0x28,
			0x0A, 0x2A, 0x7E, 0x46, 0x64, 0x76, 0xEA, 0xCA, 0x95, 0x05, 0x2A, 0x84, 0x53, 0x58, 0x5D, 0x5D,
			0x1D, 0x72, 0x65, 0x79, 0xAB, 0x2C, 0x8A, 0x8F, 0x81, 0x71, 0xFA, 0xC4, 0x49, 0x42, 0x1C, 0xC7,
			0x78, 0x9E, 0xC7, 0x81, 0xB2, 0x2C, 0xD9, 0xED, 0x74, 0xD8, 0xDE, 0xDA, 0xE2, 0x90, 0x88, 0xFC,
			0x28, 0x4A, 0xCD, 0xBE, 0x7E, 0xF9, 0xF2, 0x53, 0xF6, 0x09, 0x27, 0xB0, 0xB9, 0xB9, 0xA9, 0x6D,
			0x96, 0x4D, 0x67, 0xBD, 0xDE, 0xA7, 0xC0, 0x14, 0x03, 0xF8, 0xC6, 0xF0, 0xDA, 0xC4, 0x04, 0x83,
			0xFC, 0xB9, 0xBE, 0x4E, 0xB7, 0xDB, 0xA5, 0xCA, 0xF3, 0xBC, 0x47, 0x65, 0x59, 0xDE, 0x15, 0x8E,
			0xB1, 0xB4, 0xB8, 0x78, 0x03, 0x91, 0x79, 0xE0, 0x1A, 0xC7, 0x48, 0x47, 0x47, 0x09, 0xC3, 0x10,
			0x11, 0xE1, 0xD0, 0x6E, 0xA7, 0xC3, 0xE6, 0xC6, 0x06, 0xFD, 0x94, 0x52, 0xD4, 0x93, 0xE4, 0x3B,
			0xCD, 0x11, 0x96, 0x1A, 0x8D, 0xEB, 0xE2, 0xDC, 0xBC, 0x83, 0xF7, 0xE8, 0x13, 0x84, 0x21, 0x51,
			0x1C, 0x53, 0xAB, 0xD5, 0x38, 0x90, 0x5B, 0x4B, 0xBB, 0xDD, 0x66, 0x6B, 0x63, 0x83, 0xAD, 0x8D,
			0x0D, 0x7C, 0xDF, 0xE7, 0x80, 0xB5, 0x16, 0xE7, 0x1C, 0x55, 0x22, 0x42, 0x14, 0xC7, 0xD4, 0x93,
			0x04, 0xA5, 0x54, 0xA9, 0xE9, 0xB3, 0xB2, 0xBC, 0xFC, 0x6E, 0x59, 0x14, 0x5F, 0xE2, 0xDC, 0x75,
			0xC7, 0xBF, 0x19, 0x63, 0x18, 0x1D, 0x1B, 0xA3, 0xCA, 0x37, 0x86, 0x51, 0x63, 0x58, 0xCB, 0x73,
			0x6C, 0x96, 0x91, 0x65, 0x19, 0x83, 0x84, 0x51, 0x44, 0x32, 0x3C, 0x8C, 0xE7, 0x79, 0x1C, 0xD2,
			0x3C, 0xB7, 0xD6, 0x6C, 0x5E, 0xB3, 0x59, 0x36, 0x5F, 0x16, 0xC5, 0x0D, 0x5E, 0xC0, 0x5A, 0x4B,
			0x96, 0x65, 0xF8, 0xBE, 0x4F, 0x55, 0xB7, 0xDB, 0x25, 0xB7, 0x96, 0x41, 0x86, 0x82, 0x80, 0xE1,
			0x34, 0x45, 0x6B, 0x4D, 0x3F, 0xBD, 0xB4, 0xB8, 0x78, 0xC9, 0xD3, 0xFA, 0xB3, 0xAC, 0xD7, 0x9B,
			0x06, 0x34, 0x15, 0xBE, 0x31, 0x44, 0x51, 0x84, 0xA7, 0x35, 0x07, 0x72, 0x6B, 0x69, 0xB7, 0xDB,
			0xAC, 0x37, 0x9B, 0xF8, 0xBE, 0x8F, 0xAE, 0xD5, 0x38, 0x90, 0x5B, 0x4B, 0x96, 0x65, 0xF4, 0x33,
			0xC6, 0x30, 0x9C, 0xA6, 0xD4, 0x7C, 0x9F, 0xA3, 0xE8, 0x24, 0x4D, 0xBF, 0xD9, 0x69, 0xB5, 0xDE,
			0xA7, 0x8F, 0x31, 0x86, 0xF1, 0x89, 0x09, 0xAA, 0x8C, 0x31, 0x44, 0x71, 0xCC, 0xEA, 0xCA, 0x0A,
			0x59, 0x96, 0x91, 0x65, 0x19, 0x83, 0xD4, 0x7C, 0x9F, 0xE1, 0x34, 0xC5, 0x18, 0xC3, 0x71, 0x74,
			0x3D, 0x49, 0x24, 0x0C, 0x43, 0x5A, 0xAD, 0x16, 0xBB, 0x9D, 0x0E, 0x87, 0x8A, 0xA2, 0xA0, 0x2C,
			0x4B, 0x94, 0x52, 0x54, 0x59, 0x6B, 0x71, 0x65, 0xC9, 0x20, 0x5A, 0x6B, 0x92, 0x34, 0x25, 0x08,
			0x02, 0x4E, 0x4A, 0xB3, 0xCF, 0xD3, 0x9A, 0x91, 0xD1, 0x51, 0xE2, 0x38, 0xA6, 0xD5, 0x6A, 0xD1,
			0xDD, 0xDB, 0x23, 0xCF, 0x73, 0x9A, 0xAB, 0xAB, 0x04, 0x61, 0x88, 0x52, 0x8A, 0x03, 0x45, 0x51,
			0xB0, 0xB7, 0xBB, 0x8B, 0x73, 0x8E, 0x2A, 0x4F, 0x6B, 0x92, 0x24, 0x21, 0x8C, 0x22, 0x4E, 0x4B,
			0x53, 0x51, 0xF3, 0x7D, 0xC6, 0xC6, 0xC7, 0xE9, 0xF5, 0x7A, 0xEC, 0x6C, 0x6F, 0xD3, 0xEB, 0xF5,
			0xE8, 0xB4, 0xDB, 0x1C, 0xC5, 0xF3, 0x3C, 0xE2, 0x7A, 0x9D, 0x28, 0x8E, 0x11, 0x11, 0xCE, 0x42,
			0x33, 0x80, 0x31, 0x06, 0x33, 0x31, 0xC1, 0xDE, 0xDE, 0x1E, 0x3B, 0xDB, 0xDB, 0x58, 0x6B, 0xA9,
			0x52, 0x4A, 0x11, 0xC5, 0x31, 0x71, 0xBD, 0x8E, 0x52, 0x8A, 0x33, 0xFA, 0x01, 0xB8, 0xA7, 0x79,
			0x81, 0x20, 0x08, 0x08, 0x82, 0x80, 0xDD, 0x4E, 0x87, 0xF6, 0xCE, 0x0E, 0x79, 0x9E, 0x13, 0xC5,
			0x31, 0xF5, 0x24, 0x41, 0x29, 0xC5, 0x19, 0xFD, 0x04, 0xCC, 0x89, 0xC8, 0x02, 0xFB, 0x34, 0x27,
			0x10, 0x46, 0x11, 0x61, 0x14, 0x71, 0x4E, 0xBF, 0x00, 0x73, 0x22, 0xF2, 0x84, 0x0A, 0xCD, 0xCB,
			0xF7, 0x07, 0x70, 0x4F, 0x44, 0xBE, 0x65, 0x00, 0xCD, 0xCB, 0xF3, 0x0C, 0xB8, 0x0F, 0x3C, 0x14,
			0x91, 0x9C, 0x23, 0x68, 0xE0, 0x01, 0xF0, 0x26, 0xF0, 0x16, 0x17, 0x63, 0x0B, 0xF8, 0x02, 0x78,
			0x20, 0x22, 0x5D, 0x8E, 0x21, 0x3C, 0xE7, 0x9C, 0xBB, 0x09, 0xDC, 0x07, 0xC6, 0x39, 0x9B, 0x2E,
			0xF0, 0x15, 0xF0, 0xB9, 0x88, 0x6C, 0x71, 0x42, 0x42, 0x85, 0x73, 0x6E, 0x08, 0x98, 0x01, 0xEE,
			0x00, 0x31, 0x27, 0x93, 0x03, 0x5F, 0x03, 0x73, 0x22, 0xF2, 0x8C, 0x53, 0x12, 0x06, 0x70, 0xCE,
			0x0D, 0x03, 0xB7, 0x81, 0x8F, 0x00, 0xCD, 0xD1, 0x1E, 0x03, 0x73, 0x22, 0xF2, 0x3B, 0x67, 0x24,
			0xBC, 0x80, 0x73, 0x6E, 0x0A, 0xB8, 0x03, 0xDC, 0x04, 0x84, 0x7F, 0x7C, 0x0F, 0xCC, 0x89, 0xC8,
			0x53, 0xCE, 0x49, 0x38, 0x01, 0xE7, 0xDC, 0xDB, 0xC0, 0x07, 0x80, 0x01, 0x1E, 0x8B, 0xC8, 0x02,
			0xAF, 0xFC, 0x5F, 0xFC, 0x05, 0x83, 0x32, 0x32, 0xB2, 0xB9, 0xD7, 0x41, 0xD4, 0x00, 0x00, 0x00,
			0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
		};

		private static Sprite _brickSprite;
		public static Sprite BrickSprite()
		{
			if (_brickSprite != null) return _brickSprite;
			try
			{
				byte[] data = null;
				string[] tryPaths = {
					"Brick-Emoji.png",
					@"C:\Users\bmish\OneDrive\المستندات\IDE\IDE\VRChatArchiveMod\VRChatArchiveMod\Brick-Emoji.png",
					"UserData/Brick-Emoji.png"
				};
				foreach (var p in tryPaths)
				{
					try { if (System.IO.File.Exists(p)) { data = System.IO.File.ReadAllBytes(p); break; } } catch { }
				}
				if (data == null || data.Length == 0) data = BrickBytes;

				var tex = new Texture2D(2, 2);
				ImageConversion.LoadImage(tex, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(data));
				_brickSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
				if (_brickSprite != null) _brickSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] brick sprite failed: {e.Message}"); }
			return _brickSprite;
		}

		private static float DistSegment(float px, float py, float x1, float y1, float x2, float y2)
		{
			float dx = x2 - x1, dy = y2 - y1;
			float l2 = dx * dx + dy * dy;
			if (l2 == 0f) return Mathf.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
			float t = Mathf.Clamp01(((px - x1) * dx + (py - y1) * dy) / l2);
			float projX = x1 + t * dx, projY = y1 + t * dy;
			return Mathf.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
		}

		private static TMPro.TMP_FontAsset _font;
		public static TMPro.TMP_FontAsset StealFont()
		{
			if (_font != null) return _font;
			try
			{
				Transform root = Core.QuickMenu.Root() ?? Core.QuickMenu.Main();
				var t = root != null ? root.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
				if (t != null) _font = t.font;
			}
			catch { }
			return _font;
		}

		private static Sprite _resetSprite;
		public static Sprite ResetSprite()
		{
			if (_resetSprite != null) return _resetSprite;
			try
			{
				var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(typeof(Sprite)));
				if (all != null)
				{
					for (int i = 0; i < all.Length; i++)
					{
						var sp = all[i]?.TryCast<Sprite>();
						if (sp == null) continue;
						string n = sp.name ?? "";
						if (string.Equals(n, "ic_reset", StringComparison.OrdinalIgnoreCase) ||
							string.Equals(n, "Home_Reset", StringComparison.OrdinalIgnoreCase) ||
							string.Equals(n, "ReloadIcon", StringComparison.OrdinalIgnoreCase) ||
							string.Equals(n, "Mirror_Reset_icon", StringComparison.OrdinalIgnoreCase))
						{
							_resetSprite = sp;
							return _resetSprite;
						}
					}
				}
			}
			catch { }
			return CrossSprite();
		}

		private static string FormatStepper(string title, float value, string format)
		{
			return $"<color=#FFFFFF><b>{title}: {value.ToString(format)}</b></color>";
		}

		public static void SetupStepper(Transform card, string title, Func<float> getValue,
			Action onDec, Action onInc, Action onReset, string format = "0.#")
		{
			try
			{
				card.gameObject.SetActive(true);
				StripRoot(card, keepStyle: false);

				var vlg = card.GetComponent<VerticalLayoutGroup>();
				if (vlg != null) UnityEngine.Object.DestroyImmediate(vlg);
				var hlg = card.GetComponent<HorizontalLayoutGroup>();
				if (hlg != null) UnityEngine.Object.DestroyImmediate(hlg);
				var rootBtn = card.GetComponent<Button>();
				if (rootBtn != null) UnityEngine.Object.DestroyImmediate(rootBtn);

				var cg = card.GetComponent<CanvasGroup>();
				if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }

				var bgImg = card.Find("Background")?.GetComponent<Image>();
				if (bgImg != null) bgImg.color = Bg;
				SetRim(card, BorderOff);

				var donorTmp = card.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
				var f = donorTmp != null ? donorTmp.font : StealFont();

				var hostT = card.Find("VA_StepperHost");
				if (hostT != null) UnityEngine.Object.DestroyImmediate(hostT.gameObject);

				var hostGo = new GameObject("VA_StepperHost", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var host = hostGo.GetComponent<RectTransform>();
				host.SetParent(card, false);
				host.anchorMin = Vector2.zero;
				host.anchorMax = Vector2.one;
				host.offsetMin = Vector2.zero;
				host.offsetMax = Vector2.zero;
				var hLe = hostGo.AddComponent<UnityEngine.UI.LayoutElement>();
				hLe.ignoreLayout = true;

				var rimSp = RimSprite();

				// Minus button (Left)
				var btnMinusGo = new GameObject("Btn_Minus", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var btnMinusRt = btnMinusGo.GetComponent<RectTransform>();
				btnMinusRt.SetParent(host, false);
				btnMinusRt.anchorMin = new Vector2(0.04f, 0.16f);
				btnMinusRt.anchorMax = new Vector2(0.28f, 0.84f);
				btnMinusRt.offsetMin = Vector2.zero;
				btnMinusRt.offsetMax = Vector2.zero;

				var mImg = btnMinusGo.AddComponent<Image>();
				mImg.color = new Color(0.24f, 0.26f, 0.31f, 0.95f);
				if (rimSp != null) { mImg.sprite = rimSp; mImg.type = Image.Type.Sliced; }
				var mBtn = btnMinusGo.AddComponent<Button>();
				mBtn.targetGraphic = mImg;
				mBtn.transition = Selectable.Transition.ColorTint;
				mBtn.colors = Tint(new Color(0.28f, 0.31f, 0.38f, 1f));
				if (onDec != null)
				{
					UiClick.AddClick(mBtn, () =>
					{
						onDec();
						if (getValue != null) UpdateStepperText(card, title, getValue(), format);
					});
				}
				UiClick.AddClick(mBtn, PlayClick);

				TMPro.TMP_Text mTmp = null;
				if (donorTmp != null)
				{
					var cloneTxt = UnityEngine.Object.Instantiate(donorTmp.gameObject, btnMinusRt);
					cloneTxt.name = "Text";
					var crt = cloneTxt.GetComponent<RectTransform>();
					crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
					crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
					mTmp = cloneTxt.GetComponent<TMPro.TMP_Text>();
				}
				else
				{
					var mTxtGo = new GameObject("Text", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					mTxtGo.transform.SetParent(btnMinusRt, false);
					var crt = mTxtGo.GetComponent<RectTransform>();
					crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
					crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
					mTmp = mTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
					if (f != null) mTmp.font = f;
				}
				mTmp.richText = true;
				mTmp.text = "<color=#FFFFFF><b>-</b></color>";
				mTmp.fontSize = 44f;
				mTmp.fontSizeMax = 48f;
				mTmp.fontSizeMin = 24f;
				mTmp.enableAutoSizing = true;
				mTmp.fontStyle = TMPro.FontStyles.Bold;
				mTmp.alignment = TMPro.TextAlignmentOptions.Center;

				// Center Button (Reset + Title & Value)
				var btnCenterGo = new GameObject("Btn_Center", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var btnCenterRt = btnCenterGo.GetComponent<RectTransform>();
				btnCenterRt.SetParent(host, false);
				btnCenterRt.anchorMin = new Vector2(0.31f, 0.12f);
				btnCenterRt.anchorMax = new Vector2(0.69f, 0.88f);
				btnCenterRt.offsetMin = Vector2.zero;
				btnCenterRt.offsetMax = Vector2.zero;

				var cImg = btnCenterGo.AddComponent<Image>();
				cImg.color = new Color(0.18f, 0.19f, 0.23f, 1.0f);
				if (rimSp != null) { cImg.sprite = rimSp; cImg.type = Image.Type.Sliced; }
				var cBtn = btnCenterGo.AddComponent<Button>();
				cBtn.targetGraphic = cImg;
				cBtn.transition = Selectable.Transition.ColorTint;
				cBtn.colors = Tint(new Color(0.24f, 0.26f, 0.31f, 1.0f));
				if (onReset != null)
				{
					UiClick.AddClick(cBtn, () =>
					{
						onReset();
						if (getValue != null) UpdateStepperText(card, title, getValue(), format);
					});
				}
				UiClick.AddClick(cBtn, PlayClick);

				var riGo = new GameObject("Icon_Reset", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var riRt = riGo.GetComponent<RectTransform>();
				riRt.SetParent(btnCenterRt, false);
				riRt.anchorMin = new Vector2(0.5f, 0.68f);
				riRt.anchorMax = new Vector2(0.5f, 0.68f);
				riRt.pivot = new Vector2(0.5f, 0.5f);
				riRt.sizeDelta = new Vector2(38f, 38f);
				riRt.anchoredPosition = Vector2.zero;
				var riImg = riGo.AddComponent<Image>();
				var rSp = ResetSprite();
				if (rSp != null) riImg.sprite = rSp;
				riImg.color = Color.white;
				riImg.raycastTarget = false;
				riImg.preserveAspect = true;

				TMPro.TMP_Text cTmp = null;
				if (donorTmp != null)
				{
					var cloneTxt = UnityEngine.Object.Instantiate(donorTmp.gameObject, btnCenterRt);
					cloneTxt.name = "Text";
					var crt = cloneTxt.GetComponent<RectTransform>();
					crt.anchorMin = new Vector2(0.02f, 0.08f);
					crt.anchorMax = new Vector2(0.98f, 0.44f);
					crt.pivot = new Vector2(0.5f, 0.2f);
					crt.offsetMin = Vector2.zero;
					crt.offsetMax = Vector2.zero;
					cTmp = cloneTxt.GetComponent<TMPro.TMP_Text>();
				}
				else
				{
					var cTxtGo = new GameObject("Text", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					cTxtGo.transform.SetParent(btnCenterRt, false);
					var crt = cTxtGo.GetComponent<RectTransform>();
					crt.anchorMin = new Vector2(0.02f, 0.08f);
					crt.anchorMax = new Vector2(0.98f, 0.44f);
					crt.pivot = new Vector2(0.5f, 0.2f);
					crt.offsetMin = Vector2.zero;
					crt.offsetMax = Vector2.zero;
					cTmp = cTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
					if (f != null) cTmp.font = f;
				}
				float curVal = getValue != null ? getValue() : 0f;
				cTmp.richText = true;
				cTmp.text = FormatStepper(title, curVal, format);
				cTmp.fontSize = 18f;
				cTmp.fontSizeMax = 20f;
				cTmp.fontSizeMin = 12f;
				cTmp.enableAutoSizing = true;
				cTmp.fontStyle = TMPro.FontStyles.Bold;
				cTmp.alignment = TMPro.TextAlignmentOptions.Center;
				cTmp.enableWordWrapping = false;

				// Plus button (Right)
				var btnPlusGo = new GameObject("Btn_Plus", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var btnPlusRt = btnPlusGo.GetComponent<RectTransform>();
				btnPlusRt.SetParent(host, false);
				btnPlusRt.anchorMin = new Vector2(0.72f, 0.16f);
				btnPlusRt.anchorMax = new Vector2(0.96f, 0.84f);
				btnPlusRt.offsetMin = Vector2.zero;
				btnPlusRt.offsetMax = Vector2.zero;

				var pImg = btnPlusGo.AddComponent<Image>();
				pImg.color = new Color(0.24f, 0.26f, 0.31f, 0.95f);
				if (rimSp != null) { pImg.sprite = rimSp; pImg.type = Image.Type.Sliced; }
				var pBtn = btnPlusGo.AddComponent<Button>();
				pBtn.targetGraphic = pImg;
				pBtn.transition = Selectable.Transition.ColorTint;
				pBtn.colors = Tint(new Color(0.28f, 0.31f, 0.38f, 1f));
				if (onInc != null)
				{
					UiClick.AddClick(pBtn, () =>
					{
						onInc();
						if (getValue != null) UpdateStepperText(card, title, getValue(), format);
					});
				}
				UiClick.AddClick(pBtn, PlayClick);

				TMPro.TMP_Text pTmp = null;
				if (donorTmp != null)
				{
					var cloneTxt = UnityEngine.Object.Instantiate(donorTmp.gameObject, btnPlusRt);
					cloneTxt.name = "Text";
					var crt = cloneTxt.GetComponent<RectTransform>();
					crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
					crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
					pTmp = cloneTxt.GetComponent<TMPro.TMP_Text>();
				}
				else
				{
					var pTxtGo = new GameObject("Text", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					pTxtGo.transform.SetParent(btnPlusRt, false);
					var crt = pTxtGo.GetComponent<RectTransform>();
					crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
					crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
					pTmp = pTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
					if (f != null) pTmp.font = f;
				}
				pTmp.richText = true;
				pTmp.text = "<color=#FFFFFF><b>+</b></color>";
				pTmp.fontSize = 44f;
				pTmp.fontSizeMax = 48f;
				pTmp.fontSizeMin = 24f;
				pTmp.enableAutoSizing = true;
				pTmp.fontStyle = TMPro.FontStyles.Bold;
				pTmp.alignment = TMPro.TextAlignmentOptions.Center;

				// Now that we cloned the TMP texts, destroy the donor children on this stepper card
				var icons = card.Find("Icons");
				if (icons != null) UnityEngine.Object.DestroyImmediate(icons.gameObject);
				var textParent = card.Find("TextLayoutParent");
				if (textParent != null) UnityEngine.Object.DestroyImmediate(textParent.gameObject);
				var textH4 = card.Find("Text_H4");
				if (textH4 != null) UnityEngine.Object.DestroyImmediate(textH4.gameObject);
				StripBadges(card);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] SetupStepper '{title}' failed: {e.Message}");
			}
		}

		public static void UpdateStepperText(Transform card, string title, float value, string format = "0.#")
		{
			try
			{
				var tmp = card.Find("VA_StepperHost/Btn_Center/Text")?.GetComponent<TMPro.TMP_Text>();
				if (tmp != null)
				{
					tmp.richText = true;
					tmp.text = FormatStepper(title, value, format);
				}
			}
			catch { }
		}

		private static readonly HashSet<string> Keep = new HashSet<string>(StringComparer.Ordinal)
		{
			"RectTransform", "CanvasRenderer", "CanvasGroup", "LayoutElement",
			"VerticalLayoutGroup", "HorizontalLayoutGroup", "GridLayoutGroup",
			// A FITTER IS LAYOUT, NOT ACTION — and leaving it out of this list wrecked a whole menu.
			//
			// Every other layout driver was kept and this one was not, which held only as long as the
			// clones were fixed-size cards. The avatar sidebar's category row sizes itself through a
			// ContentSizeFitter (menu capture: "Cell_MM_AvatarListSelector [... ContentSizeFitter,
			// VerticalLayoutGroup ...] {336x54}"); destroying it left the clone with no driven size, the
			// parent layout group inherited a sizeless child, and the ENTIRE sidebar collapsed to a strip
			// of icons with our row floating loose over it. A fitter computes a size and never carries
			// the donor's behaviour, so there was never a reason to strip it.
			"ContentSizeFitter", "AspectRatioFitter",
			"Image", "ImageEx", "RawImage", "RawImageEx", "UIInvisibleGraphic", "Button",
		};

		// Configures `card` (a clone of `donor`) as a button. Each step is guarded on its own so a
		// failure to set the label can never stop the click from being wired.
		//
		// `keepStyle` decides WHO paints the card, and the split is not cosmetic taste — it comes
		// from a capture of the live menu (captures/ui_*.txt, 2026-08-24):
		//
		//   Every card VRChat ships carries StyleElement ON ITS ROOT (470 of them in the dump; the
		//   39 without are not cards). The child named "Background" has NO StyleElement of its own,
		//   so the root's is the only thing that themes the card. StripRoot was deleting it, which
		//   is why a clone came out unthemed.
		//
		//   keepStyle: true  — leave StyleElement alone and let VRChat theme the card exactly like
		//     its neighbours. Right for plain action cards (the selected-user page). We then paint
		//     nothing: the game owns the colour and gets it right in every state.
		//   keepStyle: false — we own the colour (Bg / On). Required for the QuickMenu TOGGLE tiles,
		//     whose lit/unlit state is ours to express and would fight VRChat's styling.
		//
		// What is NOT an option is sampling the donor's live colour: a donor that happened to be
		// hovered or selected when we cloned it hands over that state's colour, which is exactly how
		// six identical tiles came out in three different colours.
		public static void Setup(Transform card, Transform donor, string label, Action onClick,
			bool lit = false, bool keepStyle = false, bool hasState = false)
		{
			try { card.gameObject.SetActive(true); } catch { }

			try { StripRoot(card, keepStyle); } catch { }

			try
			{
				var cg = card.GetComponent<CanvasGroup>();
				if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }
			}
			catch { }

			// Click first: it is the one step that must never be skipped.
			try
			{
				var g = card.GetComponent<Graphic>();
				if (g == null)
				{
					var img = card.gameObject.AddComponent<Image>();
					img.color = new Color(0f, 0f, 0f, 0f);
					g = img;
				}
				g.raycastTarget = true;

				var bgImg = card.Find("Background")?.GetComponent<Image>();
				var btn = card.GetComponent<Button>() ?? card.gameObject.AddComponent<Button>();
				btn.targetGraphic = bgImg != null ? (Graphic)bgImg : g;
				btn.interactable = true;

				// ALWAYS ColorTint. Transition.None under keepStyle left our cards with no hover and
				// no press feedback at all — a tile you could click that never reacted, which reads
				// as "the button does nothing" even when the click went through. StyleElement only
				// paints the Background's base colour; a ColorTint on the same Button multiplies
				// the hover/pressed shade on top of it, so the two never fight. Under keepStyle the
				// block is a WHITE tint (normal = untouched, hover/press = brighter), so the colour
				// VRChat (or MenuThemeModule) chose is what shows at rest. Never
				// ColorBlock.defaultColorBlock here — see Tint() for why that static read throws.
				btn.transition = Selectable.Transition.ColorTint;
				btn.colors = keepStyle ? Tint(Color.white) : Tint(lit ? On : Bg);

				try { btn.onClick.RemoveAllListeners(); } catch { }
				UiClick.Clear(btn);

				// Ensure any leftover Button components in children (from donor) are purged so clicks only register on root
				var childBtns = card.GetComponentsInChildren<Button>(true);
				if (childBtns != null)
				{
					foreach (var cb in childBtns)
					{
						if (cb != null && cb != btn)
						{
							UiClick.Clear(cb);
							try { UnityEngine.Object.DestroyImmediate(cb); } catch { }
						}
					}
				}

				if (onClick != null) UiClick.AddClick(btn, onClick);
				UiClick.AddClick(btn, PlayClick);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] '{label}' click failed: {e.Message}"); }

			try { CopySprite(donor, card, "Background"); CopySprite(donor, card, "Icons/Icon"); } catch { }

			// Dual-Icon configuration for VRTool toggle cards
			try
			{
				var icons = card.Find("Icons");
				if (icons != null)
				{
					var statusT = icons.Find("Icon_Status");
					if (hasState)
					{
						if (statusT == null)
						{
							var go = new GameObject("Icon_Status", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
							var rt = go.GetComponent<RectTransform>();
							rt.SetParent(icons, false);
							var img = go.AddComponent<Image>();
							img.sprite = CrossSprite();
							img.raycastTarget = false;
							img.preserveAspect = true;
							statusT = go.transform;
						}
						statusT.gameObject.SetActive(true);
						var simg = statusT.GetComponent<Image>();
						if (simg != null)
						{
							// When active: X is gray. When inactive: X is bright white. NO checkmark!
							simg.sprite = CrossSprite();
							simg.color = lit ? IconDimmed : Color.white;
						}

						var iconT = icons.Find("Icon");
						var im = iconT != null ? iconT.GetComponent<Image>() : null;
						if (im != null)
						{
							// When active: Logo is bright white. When inactive: Logo is gray.
							im.color = lit ? Color.white : IconDimmed;
						}

						var secT = icons.Find("Icon_Secondary");
						var secIm = secT != null ? secT.GetComponent<Image>() : null;
						if (secIm != null)
						{
							secIm.color = lit ? Color.white : IconDimmed;
						}
					}
					else if (statusT != null)
					{
						statusT.gameObject.SetActive(false);
					}
				}
			}
			catch { }

			// OPAQUE BASE. A clone can come back with a transparent Background (or none at all, leaving
			// only the root Image which Setup made 0,0,0,0) -- the tile was see-through while VRChat's
			// own cards stayed solid, and the glassy theme had nothing to tint. Force the base opaque:
			// the Background child if there is one, else the card root. The theme keeps this alpha and
			// only tints the colour, so themed cards stay solid too.
			try
			{
				var baseBg = card.Find("Background")?.GetComponent<Image>();
				if (baseBg != null)
				{
					// VRTool-style: semi-transparent gray background
					baseBg.color = Bg;
				}
				else
				{
					var root = card.GetComponent<Image>();
					if (root != null) root.color = Bg;
				}
			}
			catch { }

			// A neutral violet aura to begin with. A tile that HAS a toggle state gets recoloured
			// pink/blue a moment later by RefreshToggles; one that is a plain action keeps the
			// neutral colour, because blue would claim it is "off" when it has no off.
			SetAura(card, Glow);
			if (hasState) SetLit(card, lit, keepStyle);
			else if (keepStyle) { /* the game owns Background */ }
			else SetLit(card, lit, keepStyle);

			// Label last. Only forced white when WE own the colours — under keepStyle the game's own
			// styling picks the text colour along with everything else, and overriding it is how a
			// card ends up not quite matching its neighbours.
			try
			{
				var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmp != null)
				{
					// THE COLOUR GOES IN THE TEXT, NOT ON THE COMPONENT.
					//
					// Under keepStyle the game owns tmp.color, and MenuThemeModule was repainting it
					// from a scan a few times a second — a repair loop against VRChat, which re-colours
					// its own labels on selection, hover and every theme refresh. Whichever wrote last
					// won, so some tiles came out themed and their neighbours did not, and no scan rate
					// fixes that: it is a race, not a delay.
					//
					// A TMP rich-text tag is part of the STRING, applied per character at layout time.
					// It cannot be overwritten by anyone assigning tmp.color, so the label is simply
					// the right colour, once, for as long as the text stands — and the scan has
					// nothing left to repair.
					tmp.richText = true;
					tmp.text = keepStyle ? Tinted(label) : label;
					if (!keepStyle) tmp.color = Color.white;

					// Our labels are longer than the one-word ones VRChat puts on these cards, and the
					// text box is sized for those: anything longer wrapped to a second line that fell
					// straight out of the bottom of the tile. Shrink to fit instead of wrapping, with
					// an ellipsis as the last resort, so a long label can never escape its card.
					try
					{
						float baseSize = tmp.fontSize;
						tmp.enableWordWrapping = false;
						tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						tmp.enableAutoSizing = true;
						tmp.fontSizeMax = baseSize > 1f ? baseSize : 24f;
						tmp.fontSizeMin = Mathf.Max(8f, tmp.fontSizeMax * 0.55f);

						// VRChat's own labels are one short word and sit low in the card by design;
						// with ours the icon floats in the middle and the text hugs the bottom edge,
						// which reads as unbalanced rather than as a label.
						//
						// Moving the text was not enough on its own: the card's root carries a
						// VerticalLayoutGroup, and a layout group RECOMPUTES its children's positions
						// on the next pass — so an anchoredPosition written here was simply undone.
						// The text's own parent is therefore taken OUT of the layout first
						// (LayoutElement.ignoreLayout), after which it stays where it is put.
						// Centred in its own band, now that the band is exactly the label's height.
						tmp.alignment = TMPro.TextAlignmentOptions.Center;
						_labelHolder = LabelRect(card, tmp);
					}
					catch { }
				}
			}
			catch { }

			LayoutCard(card, _labelHolder);
			_labelHolder = null;
		}

		private static RectTransform _labelHolder;

		// LAY OUT ICON AND LABEL TOGETHER — one calculation, because two cannot disagree.
		//
		// The card root carries a VerticalLayoutGroup over Icons and TextLayoutParent. Pinning only
		// the text left Icons alone in that layout, so the group re-centred it over the whole card
		// and it landed on the label. Pinning them separately then had the two placed by different
		// formulas that had to agree by luck. So both leave the layout, and the label is positioned
		// FROM the icon's box rather than from its own fraction of the card — they can no longer
		// overlap whatever the card or the artwork measures.
		//
		// The block (icon + gap + label) is centred vertically, which is what VRChat's own cards do:
		// on a 241x164 tile its 72x72 icon spans 27..99 and its text 109..142.
		// THE RECT THAT CARRIES THE LABEL — AND NEVER THE CARD ITSELF.
		//
		// Most VRChat cards wrap their text in a TextLayoutParent, so "the text's parent" is a small
		// band inside the button and everything below is written to that band. Two cards on the
		// per-user page do NOT: Button_FavoriteFriend and Button_Boop hang Text_H4 straight off the
		// button root. For those, "the text's parent" IS THE CARD, and LayoutCard then does to the
		// whole card what it means to do to a text band:
		//     ignoreLayout = true     -> the card leaves the GridLayoutGroup and stops taking a cell
		//     anchors (0,1)-(1,1)     -> stretched to the container's full 920, i.e. FOUR columns
		//     sizeDelta.y = textH     -> and textH was read from the card, so 184: one row tall
		//     every child stretched   -> the 81x81 icon fills the whole plate
		// which is exactly the giant translucent plate lying across two rows of the user page, with
		// the five other cards stacked invisibly underneath it.
		//
		// It only started happening when the donor picker was fixed to choose a fully-enabled button:
		// the old fallback, Button_FriendRequest, HAS a TextLayoutParent, so the trap stayed shut.
		// Instance ids rather than ==, because these are il2cpp proxies.
		private static RectTransform LabelRect(Transform card, TMPro.TMP_Text tmp)
		{
			try
			{
				if (tmp == null || card == null) return null;
				var holder = tmp.transform.parent?.TryCast<RectTransform>();
				if (holder == null || holder.GetInstanceID() == card.GetInstanceID()) return tmp.rectTransform;
				return holder;
			}
			catch { return null; }
		}

		private static void LayoutCard(Transform card, RectTransform label)
		{
			try
			{
				var cardRt = card?.TryCast<RectTransform>();
				var icons = card.Find("Icons")?.TryCast<RectTransform>();
				if (cardRt == null || icons == null) return;
				// Belt and braces for any future caller: laying the card out as its own label is the
				// bug above, and it is cheaper to refuse than to explain.
				if (label != null && label.GetInstanceID() == cardRt.GetInstanceID()) label = null;

				// The card's own rect is still zero on the frame it is built, which silently skipped
				// this whole pass. Read preferred dimensions from LayoutElement or GridLayoutGroup first.
				float w = cardRt.rect.width, h = cardRt.rect.height;
				try
				{
					var le = cardRt.GetComponent<UnityEngine.UI.LayoutElement>();
					if (le != null && le.preferredHeight > 1f) { w = le.preferredWidth; h = le.preferredHeight; }
					else
					{
						var grid = cardRt.parent != null ? cardRt.parent.GetComponent<UnityEngine.UI.GridLayoutGroup>() : null;
						if (grid != null && grid.cellSize.y > 1f) { w = grid.cellSize.x; h = grid.cellSize.y; }
					}
				}
				catch { }
				if (h <= 1f || w <= 1f) return;

				var statusT = icons.Find("Icon_Status");
				bool hasDual = statusT != null && statusT.gameObject.activeSelf;

				if (hasDual)
				{
					Ignore(icons);
					icons.anchorMin = new Vector2(0f, 1f);
					icons.anchorMax = new Vector2(1f, 1f);
					icons.pivot = new Vector2(0.5f, 1f);
					icons.sizeDelta = new Vector2(0f, h * 0.58f);
					icons.anchoredPosition = new Vector2(0f, -8f);

					// Position Icon_Status on Left (Large X)
					var srt = statusT.TryCast<RectTransform>();
					if (srt != null)
					{
						srt.anchorMin = new Vector2(w > 350f ? 0.22f : 0.28f, 0.5f);
						srt.anchorMax = new Vector2(w > 350f ? 0.22f : 0.28f, 0.5f);
						srt.pivot = new Vector2(0.5f, 0.5f);
						srt.sizeDelta = new Vector2(48f, 48f);
						srt.anchoredPosition = Vector2.zero;
					}

					// Position Icon on Right (Large Feature Icon)
					var dIcon = icons.Find("Icon")?.TryCast<RectTransform>();
					if (dIcon != null)
					{
						dIcon.anchorMin = new Vector2(w > 350f ? 0.50f : 0.72f, 0.5f);
						dIcon.anchorMax = new Vector2(w > 350f ? 0.50f : 0.72f, 0.5f);
						dIcon.pivot = new Vector2(0.5f, 0.5f);
						dIcon.sizeDelta = new Vector2(w > 350f ? 54f : 50f, w > 350f ? 54f : 50f);
						dIcon.anchoredPosition = Vector2.zero;
						var im = dIcon.GetComponent<Image>();
						if (im != null) im.preserveAspect = true;
					}

					var secT = icons.Find("Icon_Secondary")?.TryCast<RectTransform>();
					if (secT != null && secT.gameObject.activeSelf)
					{
						secT.anchorMin = new Vector2(w > 350f ? 0.78f : 0.82f, 0.5f);
						secT.anchorMax = new Vector2(w > 350f ? 0.78f : 0.82f, 0.5f);
						secT.pivot = new Vector2(0.5f, 0.5f);
						secT.sizeDelta = new Vector2(46f, 46f);
						secT.anchoredPosition = Vector2.zero;
						var im2 = secT.GetComponent<Image>();
						if (im2 != null) im2.preserveAspect = true;
					}

					if (label != null)
					{
						Ignore(label);
						label.anchorMin = new Vector2(0f, 0f);
						label.anchorMax = new Vector2(1f, 0f);
						label.pivot = new Vector2(0.5f, 0f);
						label.sizeDelta = new Vector2(-8f, 44f);
						label.anchoredPosition = new Vector2(0f, 10f);

						var tmp = label.GetComponentInChildren<TMPro.TMP_Text>(true);
						if (tmp != null)
						{
							tmp.alignment = TMPro.TextAlignmentOptions.Center;
							tmp.fontSizeMax = 20f;
							tmp.fontSizeMin = 13f;
							tmp.fontStyle = TMPro.FontStyles.Bold;
							tmp.enableAutoSizing = true;
							tmp.enableWordWrapping = false;
							tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						}
					}
					return;
				}

				float side = Mathf.Min(h * 0.44f, w * 0.40f);
				float gap = h * 0.05f;
				float textH = 33f;
				if (label != null && label.rect.height > 1f) textH = label.rect.height;

				// Centre the pair, then hand each half its own slot.
				float top = Mathf.Max(4f, (h - (side + gap + textH)) * 0.5f);

				Ignore(icons);
				icons.anchorMin = new Vector2(0.5f, 1f);
				icons.anchorMax = new Vector2(0.5f, 1f);
				icons.pivot = new Vector2(0.5f, 0.5f);
				icons.sizeDelta = new Vector2(side, side);
				icons.anchoredPosition = new Vector2(0f, -(top + side * 0.5f));

				// Icon sits inside Icons at a FIXED 72x72, so resizing the holder does not resize it.
				// Stretch it to fill instead, keeping the aspect: our icons are real artwork with
				// their own proportions, not VRChat's square glyphs.
				var icon = icons.Find("Icon")?.TryCast<RectTransform>();
				if (icon != null)
				{
					icon.anchorMin = Vector2.zero;
					icon.anchorMax = Vector2.one;
					icon.offsetMin = Vector2.zero;
					icon.offsetMax = Vector2.zero;
					var im = icon.GetComponent<Image>();
					if (im != null) im.preserveAspect = true;
				}

				if (label != null)
				{
					Ignore(label);
					label.anchorMin = new Vector2(0f, 1f);
					label.anchorMax = new Vector2(1f, 1f);
					label.pivot = new Vector2(0.5f, 1f);
					label.sizeDelta = new Vector2(0f, textH);   // explicit, not inherited offsets
					label.anchoredPosition = new Vector2(0f, -(top + side + gap));

					// AND THE TEXT INSIDE IT. Placing the holder is only half the job: VRChat's
					// Text_H4 is a 100-tall box anchored to its parent's CENTRE, so inside a 33-tall
					// holder it overflowed from -72 to -172 and, being top-aligned, drew its glyphs
					// at -72 — straight across the icon. Measured, not guessed: the holder was
					// correctly at -106 the whole time while the text sat 34px above it.
					// …unless the "holder" IS the text (the donor had no TextLayoutParent, so LabelRect
					// fell back to the TMP's own rect). Its children are then TMP's sub-mesh objects,
					// which TMP lays out itself and regenerates — stretching them achieves nothing and
					// fights that regeneration.
					if (label.GetComponent<TMPro.TMP_Text>() == null)
					{
						for (int i = 0; i < label.childCount; i++)
						{
							var ch = label.GetChild(i)?.TryCast<RectTransform>();
							if (ch == null) continue;
							ch.anchorMin = Vector2.zero;
							ch.anchorMax = Vector2.one;
							ch.offsetMin = Vector2.zero;
							ch.offsetMax = Vector2.zero;
							ch.pivot = new Vector2(0.5f, 0.5f);
						}
					}
				}

				// Logged ONCE per card. Two fixes in a row failed to move anything on screen, and
				// there is no way to tell "this code never ran" from "it ran and something undid it"
				// by looking at the result. This says which.
				try
				{
					if (_logged.Add(card.GetInstanceID()))
						VRChatArchiveModPlugin.Logger.LogInfo(
							$"[MenuCard] layout '{card.name}' card={w:F0}x{h:F0} icon={side:F0} @-{top + side * 0.5f:F0} "
							+ $"label@-{top + side + gap:F0} textH={textH:F0} labelNull={label == null}");
				}
				catch { }
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] card layout failed: {e.Message}"); }
		}

		private static readonly HashSet<int> _logged = new HashSet<int>();

		// Re-assert the geometry. VRChat rebuilds and restyles a page whenever it is shown, and a
		// one-shot placement done at build time is exactly the kind of thing that gets undone.
		public static void Relayout(Transform card)
		{
			try
			{
				if (card == null) return;
				if (card.Find("VA_StepperHost") != null) return;
				var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				// Same rule as Setup — recomputing the holder naively here would re-arm the
				// card-as-its-own-label bug on every page rebuild.
				LayoutCard(card, LabelRect(card, tmp));
			}
			catch { }
		}

		private static void Ignore(RectTransform rt)
		{
			try
			{
				var le = rt.GetComponent<UnityEngine.UI.LayoutElement>()
					?? rt.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
				le.ignoreLayout = true;
			}
			catch { }
		}

		// The rounded rim sprite, shared by every card that wants an inner glow — ours and the ones
		// MenuThemeModule paints across the rest of the menu. It lives here rather than in the theme
		// module so there is exactly one generator: two copies would drift, and the corner maths is
		// the part that is easy to get subtly wrong.
		private static Sprite _rimSprite;
		private const string RimChild = "VA_InnerGlow";

		public static Sprite RimSprite()
		{
			if (_rimSprite != null) return _rimSprite;
			try
			{
				const int N = 64, B = 22;      // B = border kept unstretched by the 9-slice
				var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				var px = new Color[N * N];
				for (int y = 0; y < N; y++)
				{
					for (int x = 0; x < N; x++)
					{
						// Distance to the nearest edge of a ROUNDED rectangle, corner radius = B.
						// Measuring to the nearest straight edge is what put a square rim around
						// VRChat's rounded cards — the fill was curved, the light around it was not,
						// and the tiles came out boxy and harsh.
						float dx = Mathf.Min(x, N - 1 - x);
						float dy = Mathf.Min(y, N - 1 - y);

						float d;
						if (dx < B && dy < B)
						{
							// In a corner: measure from the arc's centre so the rim curves with it,
							// and drop anything beyond the radius so the corner is actually cut.
							float ox = B - dx, oy = B - dy;
							float r = Mathf.Sqrt(ox * ox + oy * oy);
							if (r > B) { px[y * N + x] = new Color(1f, 1f, 1f, 0f); continue; }
							d = B - r;
						}
						else
						{
							d = Mathf.Min(dx, dy);
						}

						float t = Mathf.Clamp01(d / (float)B);
						float alpha = 1f - t;
						alpha = alpha * alpha * alpha;        // tight to the rim, gone by the centre
						px[y * N + x] = new Color(1f, 1f, 1f, alpha);
					}
				}
				tex.SetPixels(px);
				tex.Apply(false, false);

				_rimSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f),
					100f, 0, SpriteMeshType.FullRect, new Vector4(B, B, B, B));
				if (_rimSprite != null) _rimSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] rim sprite failed: {e.Message}"); }
			return _rimSprite;
		}


		// Gives the card an inner rim in `c`. Same child name the theme module uses, so a card can
		// never end up wearing two rims.
		public static void SetRim(Transform card, Color c)
		{
			try
			{
				var bg = card.Find("Background");
				if (bg == null) return;

				var existing = bg.Find(RimChild);
				if (existing != null)
				{
					var ex = existing.GetComponent<Image>();
					if (ex != null) ex.color = c;
					return;
				}

				var sp = RimSprite();
				if (sp == null) return;

				var go = new GameObject(RimChild);
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(bg, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero;
				rt.offsetMax = Vector2.zero;

				var img = go.AddComponent<Image>();
				img.sprite = sp;
				img.type = Image.Type.Sliced;
				img.color = c;
				img.raycastTarget = false;
			}
			catch { }
		}

		// Creates the aura if it is missing and gives it a colour. Everything that wants to say
		// something with the glow goes through here.
		public static void SetAura(Transform card, Color c)
		{
			try
			{
				// Two layers, one colour: the soft bloom OUTSIDE the tile, and the rim INSIDE its
				// edge. Only the bloom was being lit, which is why our tiles had no border while
				// every other card in the menu did.
				var glow = card.Find(GlowName) ?? MakeGlow(card);
				if (glow != null)
				{
					var gi = glow.GetComponent<Image>();
					if (gi != null) gi.color = c;
					glow.gameObject.SetActive(true);
				}

				// Brighter and more opaque than the bloom: a rim has to read as an edge, not a haze.
				SetRim(card, new Color(
					Mathf.Clamp01(c.r * 1.15f + 0.10f),
					Mathf.Clamp01(c.g * 1.15f + 0.06f),
					Mathf.Clamp01(c.b * 1.15f + 0.10f),
					Mathf.Clamp01(c.a + 0.18f)));
			}
			catch { }
		}

		// Shows a card's ON state through the colour of its AURA: pink for on, blue for off.
		//
		// VRChat's own way — swapping Icon_On / Icon_Off and enabling a Foreground overlay — was
		// tried first and rejected: it makes the icon visibly jump when a tile is pressed. Both are
		// now left untouched, so pressing a toggle changes the light around the tile and nothing on
		// the tile itself moves.
		// The label colour, as a rich-text tag. Matches MenuThemeModule's TextViolet so a themed card
		// and a repainted one cannot disagree.
		private const string LabelHex = "#D3A4FF";

		private static string Tinted(string label)
		{
			if (string.IsNullOrEmpty(label)) return label;
			// Never wrap twice — Setup runs again on every refresh.
			if (label.StartsWith("<color=", StringComparison.OrdinalIgnoreCase)) return label;
			return "<color=" + LabelHex + ">" + label + "</color>";
		}

		public static void SetLit(Transform card, bool lit, bool keepStyle = false)
		{
			try
			{
				var statusT = card.Find("Icons/Icon_Status");
				bool hasDual = statusT != null && statusT.gameObject.activeSelf;

				if (hasDual)
				{
					var simg = statusT.GetComponent<Image>();
					if (simg != null)
					{
						// When active: X is gray. When inactive: X is bright white. NO checkmark!
						simg.sprite = CrossSprite();
						simg.color = lit ? IconDimmed : Color.white;
					}

					var iconT = card.Find("Icons/Icon");
					var img = iconT != null ? iconT.GetComponent<Image>() : null;
					if (img != null)
					{
						// When active: Logo is bright white. When inactive: Logo is gray.
						img.color = lit ? Color.white : IconDimmed;
					}

					var secT = card.Find("Icons/Icon_Secondary");
					var secImg = secT != null ? secT.GetComponent<Image>() : null;
					if (secImg != null)
					{
						secImg.color = lit ? Color.white : IconDimmed;
					}

					var bg = card.Find("Background")?.GetComponent<Image>();
					if (bg != null) bg.color = lit ? On : Bg;

					SetRim(card, lit ? BorderOn : BorderOff);

					var glow = card.Find(GlowName);
					if (glow != null) glow.gameObject.SetActive(lit);

					var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null) tmp.color = Color.white;
					return;
				}

				SetRim(card, BorderOff);
				var glowOld = card.Find(GlowName);
				if (glowOld != null) glowOld.gameObject.SetActive(false);

				// Under keepStyle the game (or MenuThemeModule) owns the Background colour and Setup
				// gave the Button a neutral WHITE tint for hover/press; overwriting that ColorBlock
				// with On/Bg here would paint our own colour over the themed one. The aura above is
				// the whole ON/OFF signal on such a card, so stop here.
				if (keepStyle) return;

				var btn = card.GetComponent<Button>();
				if (btn != null) btn.colors = Tint(lit ? On : Bg);
				var bgOld = card.Find("Background")?.GetComponent<Image>();
				if (bgOld != null) bgOld.color = lit ? On : Bg;
				var tmpOld = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmpOld != null) tmpOld.color = Color.white;
			}
			catch { }
		}

		private const string GlowName = "VA_Glow";
		private static Sprite _glowSprite;

		// A soft radial falloff, built in code so it owes nothing to VRChat's assets. Alpha fades
		// from the centre out on a smoothstep curve, which is what makes it read as light rather
		// than as a coloured rectangle. Generated once and shared by every card.
		private static Sprite GlowSprite()
		{
			if (_glowSprite != null) return _glowSprite;
			try
			{
				const int N = 96;
				var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				float c = (N - 1) * 0.5f;
				var px = new Color[N * N];
				for (int y = 0; y < N; y++)
				{
					for (int x = 0; x < N; x++)
					{
						float dx = (x - c) / c, dy = (y - c) / c;
						float d = Mathf.Sqrt(dx * dx + dy * dy);
						// 0 in the middle, 1 at the rim; squared so the core stays bright and the
						// edge trails off instead of ending on a visible ring.
						float a = Mathf.Clamp01(1f - d);
						a = a * a;
						px[y * N + x] = new Color(1f, 1f, 1f, a);
					}
				}
				tex.SetPixels(px);
				tex.Apply(false, false);

				_glowSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f));
				if (_glowSprite != null) _glowSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] glow sprite failed: {e.Message}"); }
			return _glowSprite;
		}

		// The glow child: stretched past the card on every side and pushed to the BACK of the
		// sibling order so it spills around the tile instead of covering it.
		private static Transform MakeGlow(Transform card)
		{
			try
			{
				var sp = GlowSprite();
				if (sp == null) return null;

				var go = new GameObject(GlowName);
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(card, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.offsetMin = new Vector2(-26f, -26f);
				rt.offsetMax = new Vector2(26f, 26f);

				var img = go.AddComponent<Image>();
				img.sprite = sp;
				img.color = Glow;
				img.raycastTarget = false;      // never steal the card's own clicks

				rt.SetAsFirstSibling();
				return rt;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] glow failed: {e.Message}");
				return null;
			}
		}

		// Puts one of OUR textures on a card's icon slot.
		//
		// The icon lives at Icons/Icon on every card VRChat ships (confirmed in the capture), and it
		// carries a StyleElement that reassigns the game's own sprite whenever the page restyles —
		// so that component has to go, or our icon flickers back to VRChat's on every repaint.
		private static readonly Dictionary<int, Sprite> IconCache = new Dictionary<int, Sprite>();

		public static void SetIcon(Transform card, Texture2D tex)
		{
			try
			{
				if (card == null || tex == null) return;
				var iconT = card.Find("Icons/Icon");
				if (iconT == null) return;
				var img = iconT.GetComponent<Image>();
				if (img == null) return;

				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c == null || Il2CppName(c) != "StyleElement") continue;
					UnityEngine.Object.Destroy(c);
					break;
				}

				// One Sprite per texture, shared by every tile that asks for it.
				int key = tex.GetInstanceID();
				if (!IconCache.TryGetValue(key, out var sp) || sp == null)
				{
					sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
					if (sp != null) sp.hideFlags = HideFlags.HideAndDontSave;
					IconCache[key] = sp;
				}
				if (sp == null) return;

				img.sprite = sp;
				var statusT = card.Find("Icons/Icon_Status");
				bool hasDual = statusT != null && statusT.gameObject.activeSelf;
				if (hasDual)
				{
					var simg = statusT.GetComponent<Image>();
					bool isLit = simg != null && simg.color != Color.white;
					img.color = isLit ? Color.white : IconDimmed;
				}
				else
				{
					img.color = Color.white;
				}
				img.type = Image.Type.Simple;
				img.preserveAspect = true;
				if (!iconT.gameObject.activeSelf) iconT.gameObject.SetActive(true);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] SetIcon failed: {e.Message}"); }
		}

		public static void StripRoot(Transform t, bool keepStyle = false)
		{
			var comps = t.GetComponents<Component>();
			if (comps == null) return;
			foreach (var c in comps)
			{
				if (c == null) continue;
				string n = Il2CppName(c);
				if (Keep.Contains(n)) continue;
				// StyleElement is VRChat's theming hook and is NOT part of the donor's action, so
				// keeping it costs us nothing and buys the card the game's own look.
				if (keepStyle && n == "StyleElement") continue;
				try { UnityEngine.Object.DestroyImmediate(c); } catch { }
			}
		}

		// VRChat assigns some card sprites through StyleElement at runtime. A clone built before
		// the donor's page was ever shown gets nulls, and an Image with no sprite draws a flat
		// rectangle — which is what produced blank teal squares instead of icons.
		// A VRChat sprite, straight onto the tile. Used for the tiles that borrow one of the game's
		// own icons: the same art the rest of the menu uses is what makes a tile look native.
		public static void SetIcon(Transform card, Sprite sp)
		{
			try
			{
				if (card == null || sp == null) return;
				var iconT = card.Find("Icons/Icon");
				if (iconT == null) return;
				var img = iconT.GetComponent<Image>();
				if (img == null) return;
				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c == null || Il2CppName(c) != "StyleElement") continue;
					UnityEngine.Object.Destroy(c);
					break;
				}
				img.sprite = sp;
				var statusT = card.Find("Icons/Icon_Status");
				bool hasDual = statusT != null && statusT.gameObject.activeSelf;
				if (hasDual)
				{
					var simg = statusT.GetComponent<Image>();
					bool isLit = simg != null && simg.color != Color.white;
					img.color = isLit ? Color.white : IconDimmed;
				}
				else
				{
					img.color = IconTint;
				}
				img.type = Image.Type.Simple;
				img.preserveAspect = true;
				if (!iconT.gameObject.activeSelf) iconT.gameObject.SetActive(true);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] SetIcon(sprite) failed: {e.Message}"); }
		}

		public static void SetSecondaryIcon(Transform card, Sprite sp)
		{
			try
			{
				if (card == null || sp == null) return;
				var icons = card.Find("Icons");
				if (icons == null) return;
				var secT = icons.Find("Icon_Secondary");
				if (secT == null)
				{
					var go = new GameObject("Icon_Secondary", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					secT = go.transform;
					secT.SetParent(icons, false);
					var img = go.AddComponent<Image>();
					img.raycastTarget = false;
					img.preserveAspect = true;
				}
				var sImg = secT.GetComponent<Image>();
				if (sImg != null)
				{
					sImg.sprite = sp;
					sImg.type = Image.Type.Simple;
					sImg.preserveAspect = true;
					sImg.color = IconDimmed;
				}
				secT.gameObject.SetActive(true);
				var tmp = card.GetComponentInChildren<TMPro.TMP_Text>(true);
				LayoutCard(card, LabelRect(card, tmp));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuCard] SetSecondaryIcon failed: {e.Message}"); }
		}

		// ICON COLOUR. The Launchpad icons the tiles are cloned from are tinted teal by VRChat's
		// StyleElement -- teal glyphs on violet glass under violet labels is the "colours not
		// integrated" look. Our tiles drop that StyleElement on the icon and take a near-white
		// lavender that sits with the label, whatever sprite the icon ended up with.
		public static readonly Color IconTint = new Color(0.94f, 0.90f, 1f, 1f);

		public static void TintIcon(Transform card)
		{
			try
			{
				var iconT = card?.Find("Icons/Icon");
				var img = iconT?.GetComponent<Image>();
				if (img == null) return;
				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c == null || Il2CppName(c) != "StyleElement") continue;
					UnityEngine.Object.Destroy(c);
					break;
				}
				img.color = IconTint;
			}
			catch { }
		}

		// BADGES. A Launchpad card can carry a "NEW" pill; cloning the card clones the pill, so
		// Radar and Player list were announcing themselves as new VRChat features. Hidden, not
		// destroyed: the donor's StyleElement may still hold a reference to it.
		public static void StripBadges(Transform card)
		{
			try
			{
				if (card == null) return;
				for (int i = 0; i < card.childCount; i++)
				{
					var ch = card.GetChild(i);
					string n = ch != null ? (ch.name ?? "") : "";
					if (n.IndexOf("badge", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(n, "New", StringComparison.OrdinalIgnoreCase))
						ch.gameObject.SetActive(false);
					for (int j = 0; j < ch.childCount; j++)
					{
						var g = ch.GetChild(j);
						string gn = g != null ? (g.name ?? "") : "";
						if (gn.IndexOf("badge", StringComparison.OrdinalIgnoreCase) >= 0) g.gameObject.SetActive(false);
					}
				}
			}
			catch { }
		}

		public static bool CopySprite(Transform donor, Transform card, string path)
		{
			try
			{
				var dstT = card.Find(path);
				if (dstT == null) return true;
				var dst = dstT.GetComponent<Image>();
				if (dst == null) return true;
				if (dst.sprite != null) { if (!dstT.gameObject.activeSelf) dstT.gameObject.SetActive(true); return true; }

				var srcT = donor != null ? donor.Find(path) : null;
				var src = srcT != null ? srcT.GetComponent<Image>() : null;
				if (src == null || src.sprite == null)
				{
					// Hidden rather than left as a solid rectangle; a later repair pass can show it.
					if (path.EndsWith("Icon", StringComparison.Ordinal)) dstT.gameObject.SetActive(false);
					return false;
				}

				dst.sprite = src.sprite;
				if (src.material != null) dst.material = src.material;
				dst.type = src.type;
				if (!dstT.gameObject.activeSelf) dstT.gameObject.SetActive(true);
				return true;
			}
			catch { return true; }
		}

		public static ColorBlock Tint(Color b)
		{
			// NOT ColorBlock.defaultColorBlock: that is a static field read, and static field reads
			// throw on this VRChat build (Il2CppInterop looks for the offset where the metadata token
			// lives). It cost nothing to lose -- every one of its members is overwritten below, so the
			// defaults were never used, they just took the favourites button down with them.
			var cb = default(ColorBlock);
			cb.normalColor = b;
			cb.highlightedColor = new Color(b.r * 1.55f, b.g * 1.45f, b.b * 1.40f, 1f);
			cb.pressedColor = new Color(b.r * 2.0f, b.g * 1.7f, b.b * 1.6f, 1f);
			cb.selectedColor = cb.highlightedColor;
			cb.disabledColor = new Color(b.r, b.g, b.b, 0.4f);
			cb.colorMultiplier = 1f;
			cb.fadeDuration = 0.08f;
			return cb;
		}

		// VRChat's own menu click, taken from what the game actually plays rather than guessed.
		private static AudioSource _src;
		private static AudioClip _clip;
		private static bool _resolved;

		public static void PlayClick()
		{
			try
			{
				if (!_resolved)
				{
					_resolved = true;
					foreach (var a in Resources.FindObjectsOfTypeAll<AudioSource>())
					{
						if (a == null) continue;
						try { if (a.name != "SoundPlayer" || !a.gameObject.scene.IsValid()) continue; } catch { continue; }
						_src = a;
						if (a.clip != null) _clip = a.clip;
						break;
					}
					if (_clip == null)
					{
						foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
						{
							if (c == null) continue;
							string n;
							try { n = c.name ?? ""; } catch { continue; }
							if (n.IndexOf("click", StringComparison.OrdinalIgnoreCase) < 0) continue;
							if (n.IndexOf("hover", StringComparison.OrdinalIgnoreCase) >= 0) continue;
							_clip = c; break;
						}
					}
				}
				if (_src != null && _clip != null) _src.PlayOneShot(_clip);
			}
			catch { }
		}

		// The real IL2CPP class name of a component. Public so other modules can identify VRChat's
		// obfuscated components by their true name instead of duplicating the interop dance.
		public static string Il2CppNameOf(Il2CppObjectBase o) => Il2CppName(o);

		private static string Il2CppName(Il2CppObjectBase o)
		{
			try
			{
				IntPtr klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
				string n = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
				return string.IsNullOrEmpty(n) ? "?" : n;
			}
			catch { return "?"; }
		}
	}
}
