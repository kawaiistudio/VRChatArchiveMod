using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU SKIN — replaces the QuickMenu wallpaper with the VRChat Archive image.
	//
	// This is the same surface VRChat sells as a VRC+ perk ("menu background"), and a capture of the
	// live menu (captures/ui_*.txt, 2026-08-24) shows where it actually lives:
	//
	//   Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/QMParent/BackgroundLayer01
	//     Previous  (ImageEx, CanvasGroup)   1024x1120
	//     Target    (ImageEx, StyleElement, CanvasGroup)  1024x1120
	//
	// TWO images, not one: VRChat crossfades Previous → Target when the wallpaper changes, so
	// painting only one leaves the old picture showing through at whatever alpha the fade sits at.
	// Both get our sprite.
	//
	// The path this used to target — CanvasGroup/Container/Background — does not exist. The only
	// "Background" under Container is inside "Back Window", which the capture shows is INACTIVE, so
	// the swap was landing on nothing (or on a hidden object) and the menu stayed VRChat purple.
	//
	// Target carries a StyleElement, which reassigns the sprite whenever the menu restyles, so the
	// swap is re-applied on a timer rather than once.
	public class MenuSkinModule : IModule
	{
		public override string Name => "MenuSkin";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string LayerPath = "CanvasGroup/Container/Window/QMParent/BackgroundLayer01";

		private Image _bg;          // Target: the one VRChat actually shows
		private Image _bgPrev;      // Previous: the other half of the crossfade
		private Sprite _original;   // VRChat's own wallpaper, restored on toggle-off
		private Sprite _originalPrev;
		private Sprite _ours;
		private float _nextCheck;
		private int _fails;
		// Edge memory for MenuSkinEnabled: lets OFF re-arm the resolve budget and ON apply at once.
		private bool _wasSkinOn;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.MenuSkinEnabled.Value)
				{
					Restore();
					RestoreVeil();
					// The resolve budget is spent per SESSION unless something clears it, and nothing
					// did: after 41 unlucky resolves the skin could never be turned on again. The OFF
					// state is the natural place to re-arm it, so the next ON gets a fresh budget.
					_fails = 0;
					_wasSkinOn = false;
					return;
				}
				// ON EDGE: a fresh budget and an immediate pass, instead of inheriting a spent counter
				// and waiting out the 2 s gate.
				if (!_wasSkinOn) { _wasSkinOn = true; _fails = 0; _nextCheck = 0f; }

				// The liquid layer animates PER FRAME while the menu is open — that is the whole
				// point — so it runs before the 2s gate: upload the newest computed frame.
				if (Core.QuickMenu.Visible) AnimateLiquid();
				else if (_liquid != null) _liquid.enabled = false;

				float now = VaClock.Now;
				if (now < _nextCheck) return;
				_nextCheck = now + 2f;                 // gentle: never per frame
				// Nothing to reskin while the menu is closed, and the veil sweep walks the whole
				// Body subtree.
				if (!Core.QuickMenu.Visible) return;
				if (_fails > 40 && _bg == null) return;

				if (_bg == null) { if (!Resolve()) { _fails++; return; } }

				// SOLID colour or the Archive wallpaper image (QuickMenu/BackgroundSolid), then the dim.
				bool solid = false; try { solid = ModConfig.QmBackgroundSolid != null && ModConfig.QmBackgroundSolid.Value; } catch { }
				var sp = solid ? SolidSprite() : OurSprite();
				if (sp == null) return;
				Apply(_bg, sp, ref _original);
				Apply(_bgPrev, sp, ref _originalPrev);
				// COLOUR each pass (Apply forces white only when the sprite actually changes). Solid mode
				// tints the flat sprite; image mode darkens by WallpaperDim (a grey multiply on the sprite).
				Color tint;
				if (solid) { tint = SolidColor(); }
				else { float dim = 0f; try { dim = Mathf.Clamp01(ModConfig.QmWallpaperDim != null ? ModConfig.QmWallpaperDim.Value : 0f); } catch { } float v = 1f - dim; tint = new Color(v, v, v, 1f); }
				try { if (_bg != null) _bg.color = tint; } catch { }
				try { if (_bgPrev != null) _bgPrev.color = tint; } catch { }

				// Liquid wallpaper removed: the animated overlay never worked right on the current
				// QuickMenu and was only ever a toggle that did nothing useful. Anything a previous
				// build left on screen is torn down here.
				if (_liquid != null) DestroyLiquid();

				if (ModConfig.MenuSkinClearVeil.Value)
				{
					Transform qmNow = Core.QuickMenu.Root();
					if (qmNow != null) ClearVeil(qmNow);
				}
				else if (_veil.Count > 0)
				{
					RestoreVeil();
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuSkin] update threw: {e.Message}"); }
		}

		public override void OnShutdown() { Restore(); RestoreVeil(); DestroyLiquid(); }

		// ---------------------------------------------------------------- liquid overlay
		//
		// A real per-pixel fragment shader cannot be loaded into VRChat's Il2Cpp UI. But the shader
		// the user wants is Inigo Quilez's DOMAIN-WARPING fbm (iquilezles.org/articles/warp), and its
		// field evolves very slowly (t*0.007). So it is COMPUTED — the exact GLSL ported to C# — on a
		// background thread at low resolution, and each finished frame is uploaded to a RawImage that
		// stretches over the wallpaper. Bilinear upscale from 160px hides the coarseness, and a low
		// alpha keeps it a transparent liquid film over the picture rather than replacing it.
		// BAKE ONCE, THEN CYCLE. Computing the domain-warp live is what tanked the framerate — even
		// sliced, ~430 noise samples per pixel every frame is too much for the main thread. So the
		// exact same field is baked into a short SEAMLESS LOOP of frames ONCE, spread over a few
		// seconds at low res so it never blocks, and after that each frame is a free texture swap.
		// The loop is seamless because the animating time is fed as a full 2*PI turn over N frames.
		private const int LiquidRes = 96;
		private const int LiquidFrames = 40;
		private const int RowsPerBakeTick = 6;             // rows computed per frame while baking
		private static UnityEngine.UI.RawImage _liquid;
		private static Texture2D[] _frames;
		private static float[] _noiseData;
		private static Color32[] _bakeBuf;
		private static int _bakeFrame, _bakeRow;
		private static bool _baked;
		private static float _cycle;

		private void EnsureLiquid()
		{
			// RETIRED. The animated liquid overlay never read right over the menu wallpaper and the
			// user asked for it gone. This is a hard no-op so no lingering config flag or menu
			// toggle can bring it back; the plain background skin (SkinBackground) is untouched.
			return;
#pragma warning disable CS0162 // unreachable — kept for reference only
			try
			{
				if (_liquid != null) { _liquid.enabled = true; return; }
				if (_bg == null) return;
				Transform parent = _bg.transform.parent;
				if (parent == null) return;

				BuildBaseNoise(256);
				_bakeBuf = new Color32[LiquidRes * LiquidRes];
				_frames = new Texture2D[LiquidFrames];
				for (int i = 0; i < LiquidFrames; i++)
					_frames[i] = new Texture2D(LiquidRes, LiquidRes, TextureFormat.RGBA32, false)
					{ wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
				_bakeFrame = 0; _bakeRow = 0; _baked = false;

				var go = new GameObject("VA_Liquid");
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(parent, false);
				rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
				rt.SetSiblingIndex(_bg.transform.GetSiblingIndex() + 1);

				_liquid = go.AddComponent<UnityEngine.UI.RawImage>();
				_liquid.texture = _frames[0];
				_liquid.raycastTarget = false;
				_liquid.color = new Color(1f, 1f, 1f, 0.35f);
				VRChatArchiveModPlugin.Logger.LogInfo("[MenuSkin] liquid: baking " + LiquidFrames + " frames…");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[MenuSkin] liquid: " + e.Message); }
		}

		private void AnimateLiquid()
		{
			try
			{
				if (_liquid == null || _frames == null) return;
				_liquid.enabled = true;
				_liquid.color = new Color(1f, 1f, 1f, 0.35f);

				if (!_baked) { BakeTick(); return; }

				// Free after baking: pick the frame for the current time. ~14 fps loop.
				_cycle += VaClock.Delta;
				int idx = ((int)(_cycle * 14f)) % LiquidFrames;
				if (idx < 0) idx += LiquidFrames;
				_liquid.texture = _frames[idx];
			}
			catch { }
		}

		// One slice of one frame per call, so the whole bake is spread over a few seconds and never
		// costs more than a few hundred pixels in a single frame.
		private void BakeTick()
		{
			int n = LiquidRes;
			// tt goes one full turn across the loop, so frame N-1 flows back into frame 0.
			float ang = (_bakeFrame / (float)LiquidFrames) * Mathf.PI * 2f;
			float tt = (Mathf.Sin(ang) * 0.5f + 0.5f) * 0.9f;   // periodic, in the shader's tt range

			int end = Mathf.Min(_bakeRow + RowsPerBakeTick, n);
			for (int y = _bakeRow; y < end; y++)
			{
				float uy = (float)y / n;
				float py = y * (640f / n) * 0.004f;
				for (int x = 0; x < n; x++)
				{
					float ux = (float)x / n;
					float px = x * (640f / n) * 0.004f;

					float qx = Fbm(px, py);
					float qy = Fbm(px + 10f, py + 1.3f);
					float rx = Fbm(px + 4f * qx + tt + 1.7f, py + 4f * qy + tt + 9.2f);
					float ry = Fbm(px + 4f * qx + tt + 8.3f, py + 4f * qy + tt + 2.8f);
					float gx = Fbm(px + 2f * rx + tt * 20f + 2f, py + 2f * ry + tt * 20f + 6f);
					float gy = Fbm(px + 2f * rx + tt * 10f + 5f, py + 2f * ry + tt * 10f + 3f);
					float nz = Fbm(px + 5.5f * gx - tt * 7f, py + 5.5f * gy - tt * 7f);

					float sm = Smooth01(nz);
					float cr = Lerp(0.1f, 0.5f, sm), cg = Lerp(0.4f, 0.7f, sm), cb = Lerp(0.4f, 0.0f, sm);
					float qq = qx * qx + qy * qy;
					cr = Lerp(cr, 0.35f, qq); cg = Lerp(cg, 0f, qq); cb = Lerp(cb, 0.1f, qq);
					float w1 = 0.2f * gy * gy;
					cr = Lerp(cr, 0f, w1); cg = Lerp(cg, 0.2f, w1); cb = Lerp(cb, 1f, w1);
					float w2 = Smoothstep(0f, 0.6f, 0.6f * ry * ry);
					cr = Lerp(cr, 0.3f, w2); cg = Lerp(cg, 0f, w2); cb = Lerp(cb, 0f, w2);
					float w3 = 0.1f * gx;
					cr = Lerp(cr, 0f, w3); cg = Lerp(cg, 0.5f, w3); cb = Lerp(cb, 0f, w3);

					float o1 = Smoothstep(0.3f, 0.5f, nz) * Smoothstep(0.5f, 0.3f, nz);
					cr = Lerp(cr, 0f, o1); cg = Lerp(cg, 0f, o1); cb = Lerp(cb, 0f, o1);
					float o2 = Smoothstep(0.7f, 0.8f, nz) * Smoothstep(0.8f, 0.7f, nz);
					cr = Lerp(cr, 0f, o2); cg = Lerp(cg, 0f, o2); cb = Lerp(cb, 0f, o2);

					float c2 = nz * 2f;
					cr *= c2; cg *= c2; cb *= c2;
					float vig = 0.70f + 0.65f * (float)Math.Sqrt(70f * ux * uy * (1f - ux) * (1f - uy));
					cr *= vig; cg *= vig; cb *= vig;

					_bakeBuf[y * n + x] = new Color32(
						(byte)(Clamp01(cr) * 255f), (byte)(Clamp01(cg) * 255f), (byte)(Clamp01(cb) * 255f), 255);
				}
			}
			_bakeRow = end;

			if (_bakeRow >= n)
			{
				_frames[_bakeFrame].SetPixels32(_bakeBuf);
				_frames[_bakeFrame].Apply(false, false);
				_bakeRow = 0;
				_bakeFrame++;
				if (_bakeFrame >= LiquidFrames)
				{
					_baked = true;
					_bakeBuf = null;
					VRChatArchiveModPlugin.Logger.LogInfo("[MenuSkin] liquid: baked, cycling (zero per-frame cost).");
				}
			}
		}

		private static void DestroyLiquid()
		{
			try { if (_liquid != null) UnityEngine.Object.Destroy(_liquid.gameObject); } catch { }
			try { if (_frames != null) foreach (var f in _frames) if (f != null) UnityEngine.Object.Destroy(f); } catch { }
			_liquid = null; _frames = null; _bakeBuf = null; _baked = false; _bakeFrame = 0; _bakeRow = 0;
		}

		// noise() from the shader: bilinear sample of the base field, matching textureLod on iChannel0.
		private static float Noise(float x, float y)
		{
			float pfx = Mathf.Floor(x), pfy = Mathf.Floor(y);
			float fx = x - pfx, fy = y - pfy;
			fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
			float a = Sample(pfx + 0.5f, pfy + 0.5f);
			float b = Sample(pfx + 1.5f, pfy + 0.5f);
			float c = Sample(pfx + 0.5f, pfy + 1.5f);
			float d = Sample(pfx + 1.5f, pfy + 1.5f);
			return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
		}

		private static float Sample(float x, float y)
		{
			int ix = ((int)Mathf.Floor(x / 256f * 256f)) & 255;
			int iy = ((int)Mathf.Floor(y / 256f * 256f)) & 255;
			var d = _noiseData;
			return d != null ? d[iy * 256 + ix] : 0.5f;
		}

		private static readonly float[,] Mtx = { { 0.80f, 0.60f }, { -0.60f, 0.80f } };

		private static float Fbm(float x, float y)
		{
			float f = 0f;
			f += 0.5f * Noise(x, y);        Mul(ref x, ref y, 2.02f);
			f += 0.25f * Noise(x, y);       Mul(ref x, ref y, 2.03f);
			f += 0.125f * Noise(x, y);      Mul(ref x, ref y, 2.01f);
			f += 0.0625f * Noise(x, y);     Mul(ref x, ref y, 2.04f);
			f += 0.03125f * Noise(x, y);    Mul(ref x, ref y, 2.01f);
			f += 0.015625f * Noise(x, y);
			return f / 0.96875f;
		}

		private static void Mul(ref float x, ref float y, float s)
		{
			float nx = (Mtx[0, 0] * x + Mtx[0, 1] * y) * s;
			float ny = (Mtx[1, 0] * x + Mtx[1, 1] * y) * s;
			x = nx; y = ny;
		}

		private static float Lerp(float a, float b, float t) => a + (b - a) * t;
		private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
		private static float Smooth01(float v) { v = Clamp01(v); return v * v * (3f - 2f * v); }
		private static float Smoothstep(float e0, float e1, float x)
		{
			float t = Clamp01((x - e0) / (e1 - e0));
			return t * t * (3f - 2f * t);
		}

		// iChannel0: a 256x256 grey value-noise field, generated once, kept as a float[] the worker
		// samples off the main thread (a Texture2D cannot be read from a background thread).
		private static void BuildBaseNoise(int n)
		{
			if (_noiseData != null) return;
			var rnd = new System.Random(1337);
			_noiseData = new float[n * n];
			for (int i = 0; i < n * n; i++) _noiseData[i] = (float)rnd.NextDouble();
			// one box blur so the field is smooth, not white static
			var tmp = new float[n * n];
			for (int y = 0; y < n; y++)
				for (int x = 0; x < n; x++)
				{
					float s = 0f; int c = 0;
					for (int dy = -1; dy <= 1; dy++)
						for (int dx = -1; dx <= 1; dx++)
						{
							int xx = (x + dx) & (n - 1), yy = (y + dy) & (n - 1);
							s += _noiseData[yy * n + xx]; c++;
						}
					tmp[y * n + x] = s / c;
				}
			_noiseData = tmp;
		}

		// ---------------------------------------------------------------- the veil
		//
		// VRChat draws translucent full-panel layers over the wallpaper so its own white text stays
		// readable. With a picture behind them that reads as a haze across the whole menu.
		//
		// The dump could not name them: Body and the page canvases are obfuscated, and the capture
		// prints no colour for ImageEx, so there is nothing to match on by name. This measures
		// instead \u2014 at runtime, every Image under the menu that (a) covers nearly the whole panel and
		// (b) is partly see-through is a veil by definition, because an opaque one would hide the
		// wallpaper entirely and a small one is a widget. Each hit is logged with its name and alpha
		// so the list can be checked, and every alpha is remembered for the restore.
		private readonly List<(Graphic G, Color C)> _veil = new List<(Graphic, Color)>();
		private float _nextVeil;

		private void ClearVeil(Transform qm)
		{
			float now = VaClock.Now;
			if (now < _nextVeil) return;
			_nextVeil = now + 3f;          // pages appear as you browse; keep sweeping, gently

			try
			{
				Transform body = qm.Find("CanvasGroup/Container/Window/QMParent/Body");
				if (body == null) return;

				var graphics = body.GetComponentsInChildren<Graphic>(false);
				if (graphics == null) return;

				foreach (var g in graphics)
				{
					if (g == null) continue;
					try
					{
						// Text is never a veil, whatever size it is.
						if (g.TryCast<UnityEngine.UI.Text>() != null) continue;
						if (g.GetComponentInChildren<TMPro.TMP_Text>(true) != null) continue;

						var rt = g.rectTransform;
						if (rt == null) continue;
						Rect rr = rt.rect;
						if (rr.width < 900f || rr.height < 900f) continue;   // full-panel only

						Color c = g.color;
						if (c.a <= 0.02f) continue;                          // already invisible

						string gname = g.gameObject.name ?? "";
						string sprite = "";
						try
						{
							var im = g.TryCast<Image>();
							if (im != null && im.sprite != null) sprite = im.sprite.name ?? "";
						}
						catch { }

						// Two kinds of veil, and the first version only caught one of them:
						//   - a flat translucent fill (colour alpha below 1)
						//   - an OPAQUE colour on a sprite whose own pixels are see-through, which is
						//     how VRChat's backdrops are actually drawn. Those have alpha 1.0 and were
						//     skipped, which is why the picture still looked washed after the first pass.
						// The second kind is only touched when the object NAMES itself a backdrop, so a
						// real piece of page content can never be blanked by accident.
						bool translucent = c.a < 0.98f;
						bool backdropName =
							gname.IndexOf("Scrim", StringComparison.OrdinalIgnoreCase) >= 0
							|| gname.IndexOf("Backdrop", StringComparison.OrdinalIgnoreCase) >= 0
							|| gname.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0
							|| gname.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0
							|| gname.IndexOf("Vignette", StringComparison.OrdinalIgnoreCase) >= 0;

						// Everything full-panel is REPORTED whether it is touched or not: that list is
						// the only way to find out what else is stacked over the wallpaper.
						VRChatArchiveModPlugin.Logger.LogInfo(
							$"[MenuSkin] over-wallpaper layer: '{gname}' {rr.width:F0}x{rr.height:F0} "
							+ $"alpha={c.a:F2} sprite='{sprite}' -> {(translucent || backdropName ? "HIDDEN" : "left alone")}");

						if (!translucent && !backdropName) continue;

						_veil.Add((g, c));
						c.a = 0f;
						g.color = c;
					}
					catch { }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuSkin] veil pass failed: {e.Message}"); }
		}

		private void RestoreVeil()
		{
			for (int i = 0; i < _veil.Count; i++)
			{
				try { if (_veil[i].G != null) _veil[i].G.color = _veil[i].C; }
				catch { }
			}
			_veil.Clear();
			_nextVeil = 0f;
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// RESTORE BEFORE FORGETTING (2026-09-13). The menu SURVIVES scene loads — as the original
			// note here said — so nulling _original / clearing _veil left OUR wallpaper sprite on
			// VRChat's Image and the veil Graphics still at alpha 0, with every handle that could undo
			// them thrown away. Restore() then no-opped forever, and because Apply() early-returns
			// when `img.sprite == sp`, _original was never re-learned either: the skin became
			// permanent and its switch dead. Restore first; the refs are dropped straight after, so a
			// genuinely rebuilt Image is still re-resolved on the next pass.
			Restore();
			RestoreVeil();
			_bg = null; _bgPrev = null; _original = null; _originalPrev = null; _nextCheck = 0f;
			_veil.Clear(); _nextVeil = 0f;
			_fails = 0;        // new world, fresh resolve budget
			_wasSkinOn = false;
		}

		// Paints one half of the crossfade, remembering what was there first.
		private static void Apply(Image img, Sprite sp, ref Sprite original)
		{
			try
			{
				if (img == null || img.sprite == sp) return;
				if (original == null) original = img.sprite;
				img.sprite = sp;
				img.color = Color.white;
				img.type = Image.Type.Simple;
				img.preserveAspect = false;        // fill the whole panel
				// A half mid-fade sits at partial alpha; force it visible or our picture appears
				// washed out over whatever it was fading from.
				var cg = img.GetComponent<CanvasGroup>();
				if (cg != null) cg.alpha = 1f;
			}
			catch { }
		}

		private bool Resolve()
		{
			Transform qm = Core.QuickMenu.Root();
			if (qm == null) return false;
			Transform layer = qm.Find(LayerPath);
			if (layer == null) return false;

			_bg = layer.Find("Target")?.GetComponent<Image>();
			_bgPrev = layer.Find("Previous")?.GetComponent<Image>();
			if (_bg == null && _bgPrev == null)
			{
				// Named children are what the capture shows; if a build renames them, take whatever
				// Images the layer holds rather than giving up entirely.
				var imgs = layer.GetComponentsInChildren<Image>(true);
				if (imgs != null)
				{
					foreach (var im in imgs)
					{
						if (im == null) continue;
						if (_bg == null) _bg = im;
						else if (_bgPrev == null) _bgPrev = im;
					}
				}
			}
			if (_bg == null && _bgPrev == null) return false;

			// StyleElement on Target is what re-asserts VRChat's own wallpaper. Dropping it makes the
			// swap stick instead of flickering back every time the menu restyles.
			try
			{
				if (_bg != null)
				{
					foreach (var c in _bg.GetComponents<Component>())
					{
						if (c == null || Core.MenuCard.Il2CppNameOf(c) != "StyleElement") continue;
						UnityEngine.Object.DestroyImmediate(c);
						break;
					}
				}
			}
			catch { }

			VRChatArchiveModPlugin.Logger.LogInfo(
				$"[MenuSkin] QuickMenu wallpaper found (target={_bg != null}, previous={_bgPrev != null}).");
			return true;
		}

		private static Sprite _solid;
		private Sprite SolidSprite()
		{
			if (_solid != null) return _solid;
			try
			{
				var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
				var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white;
				tex.SetPixels(px); tex.Apply(); tex.hideFlags = HideFlags.HideAndDontSave;
				_solid = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
				if (_solid != null) _solid.hideFlags = HideFlags.HideAndDontSave;
			}
			catch { }
			return _solid;
		}
		private static Color SolidColor()
		{
			try { if (ColorUtility.TryParseHtmlString(ModConfig.QmBackgroundColor?.Value, out var c)) return c; } catch { }
			return new Color(0.043f, 0.027f, 0.078f, 1f);
		}

		private string _srcKey;
		private Sprite OurSprite()
		{
			try
			{
				string path = null; try { path = ModConfig.QmCustomBackgroundImage != null ? ModConfig.QmCustomBackgroundImage.Value : null; } catch { }
				Texture2D tex = null; string key = "embedded";
				if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
				{
					long mt = 0; try { mt = System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch { }
					key = "file:" + path + "|" + mt;
					if (key == _srcKey && _ours != null) return _ours;
					try
					{
						byte[] data = System.IO.File.ReadAllBytes(path);
						var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); t.hideFlags = HideFlags.HideAndDontSave; t.wrapMode = TextureWrapMode.Clamp;
						if (ImageConversion.LoadImage(t, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(data))) tex = t;
						else VRChatArchiveModPlugin.Logger.LogWarning("[MenuSkin] custom image did not decode: " + path);
					}
					catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[MenuSkin] custom image read failed: " + e.Message); }
				}
				if (tex == null) { key = "embedded"; if (key == _srcKey && _ours != null) return _ours; tex = AssetLoader.MenuBackground; }
				if (tex == null) return _ours;
				_ours = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
				if (_ours != null) { _ours.hideFlags = HideFlags.HideAndDontSave; _srcKey = key; VRChatArchiveModPlugin.Logger.LogInfo("[MenuSkin] wallpaper source: " + key + " (" + tex.width + "x" + tex.height + ")."); }
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[MenuSkin] OurSprite: " + e.Message); }
			return _ours;
		}

		private void Restore()
		{
			try { if (_bg != null && _original != null && (_bg.sprite == _ours || _bg.sprite == _solid)) _bg.sprite = _original; }
			catch { }
			try { if (_bgPrev != null && _originalPrev != null && (_bgPrev.sprite == _ours || _bgPrev.sprite == _solid)) _bgPrev.sprite = _originalPrev; }
			catch { }
			// Our dim/solid tint must not linger on VRChat's own wallpaper after a toggle-off.
			try { if (_bg != null) _bg.color = Color.white; } catch { }
			try { if (_bgPrev != null) _bgPrev.color = Color.white; } catch { }
		}

	}
}
