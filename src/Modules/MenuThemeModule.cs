using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MENU THEME — repaints VRChat's QuickMenu in the Archive's pink → violet instead of its stock
	// teal, and turns its text violet.
	//
	// It has to run on a TIMER rather than once. VRChat themes its own UI through StyleEngine /
	// StyleElement, which re-asserts colours whenever a page is shown, restyled or rebuilt; a
	// one-shot pass is undone the moment you open a different tab. Every 2 seconds is often enough
	// to look permanent and rare enough to cost nothing.
	//
	// The "gradient" is across the GRID, not inside each button: a UI Image is a single flat colour,
	// so there is no way to gradient one card on its own without a custom shader. Instead each card
	// is tinted by WHERE it sits on screen, so a page of buttons reads as one pink→violet sweep.
	// That is an honest approximation, and it is the one that actually looks like something.
	//
	// Everything is remembered and restored: switch the toggle off and the menu goes back to
	// VRChat's own colours without a restart.
	public class MenuThemeModule : IModule
	{
		public override string Name => "MenuTheme";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";

		// Defaults, used when the config hex is blank or unparseable. The live values come from
		// config (QuickMenu/GradientStart, GradientEnd, TextColor) so they are editable from the
		// desktop client's MOD SETTINGS page, and re-read cheaply each pass (cached by the hex string).
		private static readonly Color PinkDefault = new Color(1.000f, 0.416f, 0.835f);
		private static readonly Color VioletDefault = new Color(0.506f, 0.263f, 0.902f);
		private static readonly Color TextVioletDefault = new Color(0.827f, 0.643f, 1.000f);

		private static string _txtHex, _gsHex, _geHex;
		private static Color _txtCol = new Color(0.827f, 0.643f, 1.000f), _gsCol = new Color(1.000f, 0.416f, 0.835f), _geCol = new Color(0.506f, 0.263f, 0.902f);
		private static Color Hex(string h, ref string cacheKey, ref Color cacheVal, Color fallback)
		{
			if (h == cacheKey) return cacheVal;
			cacheKey = h;
			cacheVal = (!string.IsNullOrEmpty(h) && ColorUtility.TryParseHtmlString(h, out var c)) ? c : fallback;
			return cacheVal;
		}
		private static Color TextCol() { try { return Hex(ModConfig.QmTextColor?.Value, ref _txtHex, ref _txtCol, TextVioletDefault); } catch { return TextVioletDefault; } }
		private static Color GradStart() { try { return Hex(ModConfig.QmGradientStart?.Value, ref _gsHex, ref _gsCol, PinkDefault); } catch { return PinkDefault; } }
		private static Color GradEnd() { try { return Hex(ModConfig.QmGradientEnd?.Value, ref _geHex, ref _geCol, VioletDefault); } catch { return VioletDefault; } }

		// Targets found by the LAST full scan. Re-asserting colours on these is a handful of property
		// writes; finding them again means walking every Image and every text in the menu, which is
		// what made this module the most expensive thing in the profiler. The scan is rare, the
		// re-assert is frequent.
		private sealed class Target
		{
			public Image Img;
			public Selectable Sel;      // resolved once: GetComponentInParent per image per pass is
			public Color Want;          // a tree walk of its own
			public bool Ours;
		}
		// ONE LIST PER CANVAS. Both used to share a single list that was CLEARED on every scan —
		// and since a scan only walks one canvas, the other canvas's targets were thrown away and
		// went back to VRChat's teal until its own turn came round, up to five seconds later. That
		// is the unreliable colouring: at any moment only half the menu was actually ours.
		//
		// Keeping them apart means a scan replaces only what it just rediscovered, and the fast
		// pass re-asserts BOTH every tick. The reason the scan was split in the first place — never
		// paying for two full walks in one frame — is untouched.
		private readonly List<Target>[] _targetsBy = { new List<Target>(), new List<Target>() };
		private readonly List<TMPro.TMP_Text>[] _liveBy = { new List<TMPro.TMP_Text>(), new List<TMPro.TMP_Text>() };
		private int _slot;   // which canvas the CURRENT scan is filling

		private List<Target> _targets => _targetsBy[_slot];
		// Set to true by anyone who ADDS UI under a themed canvas, so the next tick re-walks it
		// instead of waiting out the scan cadence.
		private static bool _dirty;

		/// <summary>Ask for an immediate rescan. Call this right after creating menu UI of our own:
		/// the full scan alternates canvases every 5 s, so a card created just after one ran waits up
		/// to TEN seconds to be enrolled — and until then it keeps the flat dark colour it was built
		/// with while every VRChat button around it is themed. That gap is exactly what made the
		/// user-menu cards come out black next to their neighbours.</summary>
		public static void Invalidate() { _dirty = true; }

		private float _nextScan;

		// ---- the incremental re-assert ------------------------------------------------------
		// Putting our colours back over EVERY themed text and card, ~3 times a second, is O(n)
		// il2cpp property crossings on a main menu holding thousands of texts. Measured on build
		// 1903: MenuTheme = 425 ms/s of a 519 ms/s mod, the game at 8 fps with the menu open --
		// this module alone was the "lag de fou", not the features it themes.
		//
		// Nothing on screen needs that rate. VRChat's StyleEngine repaints a page when it SHOWS
		// it, which always follows a press, so the full sweep runs around a press (and just after
		// a rescan, when the target lists are new) and a bounded slice the rest of the time. A
		// colour VRChat undoes on its own is still corrected, just a second later instead of in
		// 350 ms -- which nobody can see, because nothing is undoing them while nothing is clicked.
		private const int IdleSlice = 192;      // items per canvas per tick when idle
		private const float BusyWindow = 1.25f; // seconds after a press that still sweep in full
		private readonly int[] _txtCursor = new int[2];
		private readonly int[] _imgCursor = new int[2];
		private float _lastScanAt = -99f;
		private bool _scanFlip;   // alternates the two canvases so one frame never pays for both

		private readonly List<(Graphic G, Color C)> _painted = new List<(Graphic, Color)>();
		private readonly List<(TMPro.TMP_Text T, Color C)> _texts = new List<(TMPro.TMP_Text, Color)>();
		private readonly HashSet<int> _seen = new HashSet<int>();
		private float _next;
		private float _nextBootstrap;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.MenuThemeEnabled.Value)
				{
					if (_painted.Count > 0 || _texts.Count > 0) RestoreAll();
					return;
				}

				float now = VaClock.Now;
				if (now < _next) return;
				_next = now + 0.35f;

				// A CLOSED menu needs no theming. This module walks every Image and every text in the
				// menu, and it was doing that several times a second whether or not anybody was
				// looking at it — the single most expensive thing the mod did. Skipping the work
				// beats making it cheaper.
				if (!Core.QuickMenu.AnyVisible) return;

				Repaint();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[MenuTheme] update threw: {e.Message}"); }
		}

		public override void OnShutdown() => RestoreAll();

		public override void OnSceneLoaded(int buildIndex)
		{
			// RESTORE BEFORE FORGETTING (2026-09-13). _texts is the undo ledger: it holds each text's
			// ORIGINAL colour. Clearing it without restoring left every still-live label painted in
			// the theme colour with nothing able to undo it — and worse, the next Scan() then recorded
			// that themed colour AS the original (`if (_seen.Add(id)) _texts.Add((t, t.color));`), so
			// a later OFF "restored" the theme instead of VRChat's own palette, permanently. The menu
			// canvas often survives a world change, which is exactly when this bites. RestoreAll()
			// proves liveness before each write, so it is safe when the canvas really did go.
			RestoreAll();
			// Those Graphics belong to a canvas that may have been rebuilt; forget them rather than
			// hold references we can no longer restore correctly.
			// Both slots: `_live`/`_targets` only reach the canvas scanned last, and the other
			// canvas's stale references would be re-asserted on dead objects after the reload.
			_painted.Clear(); _texts.Clear(); _seen.Clear();
			_liveBy[0].Clear(); _liveBy[1].Clear(); _targetsBy[0].Clear(); _targetsBy[1].Clear();
			_next = 0f; _nextScan = 0f;
			Core.QuickMenu.Forget();   // the canvas may not survive the world change
		}

		// WHY A LABEL STAYS GREY, ANSWERED WITH ITS OWN STATE.
		//
		// Some menu labels take the theme colour and some do not — in the same row, at the same
		// instant. Writing tmp.color harder cannot fix that, because there are exactly three ways a
		// TMP ends up a colour other than the one in .color, and only one is a plain timing race:
		//   1. .color really is grey     — someone writes it faster than our ~3 Hz pass;
		//   2. enableVertexGradient      — TMP ignores .color entirely and uses the gradient;
		//   3. a <color=…> tag in .text  — applied per character at layout, so it wins outright.
		// Everything else in the pipeline (canvasRenderer colour, _FaceColor, alpha) MULTIPLIES, and
		// multiplying a violet by anything cannot produce neutral grey.
		//
		// So rather than guess, each label that refuses reports which of the three it is. Ten of them,
		// once per session: enough to name the mechanism, not enough to fill a log.
		private static int _diagLeft = 10;
		private static readonly HashSet<int> _diagSeen = new HashSet<int>();

		private static void Diagnose(TMPro.TMP_Text t)
		{
			if (_diagLeft <= 0) return;
			try
			{
				Color want = TextCol();
				bool grad = false;
				try { grad = t.enableVertexGradient; } catch { }
				string txt = null;
				try { txt = t.text; } catch { }
				bool tagged = !string.IsNullOrEmpty(txt) && txt.IndexOf("<color", StringComparison.OrdinalIgnoreCase) >= 0;
				bool kept = t.color == want;
				if (kept && !grad && !tagged) return;   // took the colour, nothing to explain

				if (!_diagSeen.Add(t.GetInstanceID())) return;
				_diagLeft--;
				VRChatArchiveModPlugin.Logger.LogInfo("[MenuTheme] '" + t.name + "' resists: "
					+ (kept ? "" : "colour-overwritten ")
					+ (grad ? "vertexGradient " : "")
					+ (tagged ? "rich-text-tag " : "")
					+ "| color=" + ColorUtility.ToHtmlStringRGBA(t.color)
					+ " want=" + ColorUtility.ToHtmlStringRGBA(want)
					+ (tagged ? " | text=" + (txt.Length > 60 ? txt.Substring(0, 60) : txt) : ""));
			}
			catch { }
		}

		private void Repaint()
		{
			float now = VaClock.Now;

			// THE "NOTHING FOUND YET, KEEP LOOKING" CLAUSE ALMOST KILLED THE FRAME RATE.
			//
			// It used to read `_targetsBy[0].Count == 0 && _targetsBy[1].Count == 0`, meant as a
			// bootstrap: rescan every tick until the first scan finds something. But `_targets` holds
			// only the IMAGE/card targets -- themed TEXT goes into `_live` -- so on a canvas that has
			// text and no themed backgrounds (or on the tick right after scanning the wrist menu,
			// which yields none), BOTH image lists read 0 while `_live` was full. The clause then
			// forced a complete GetComponentsInChildren sweep of Canvas_MainMenu THREE TIMES A SECOND.
			// Measured: MenuTheme=700 ms/s, the whole frame, with the menu open.
			//
			// Two fixes: the emptiness test now counts text as well, so a themed-text canvas is not
			// "empty"; and the bootstrap rescan is rate-limited to the scan cadence's own floor rather
			// than firing every tick, so even a genuinely bare menu cannot spin here.
			bool nothingThemed = _targetsBy[0].Count == 0 && _targetsBy[1].Count == 0
				&& _liveBy[0].Count == 0 && _liveBy[1].Count == 0;
			bool bootstrap = nothingThemed && now >= _nextBootstrap;
			if (_dirty || now >= _nextScan || bootstrap)
			{
				if (bootstrap) _nextBootstrap = now + 1f;
				// ONE CANVAS PER SCAN, AND LESS OFTEN.
				//
				// The spike hunter caught this module producing 143 ms frames. Both canvases were
				// walked in the SAME frame, every three seconds — GetComponentsInChildren over every
				// text and every image of the entire menu twice over — so the cost of the whole job
				// landed on one unlucky frame, which is precisely what a stutter is.
				//
				// Splitting the two canvases across alternate scans halves the worst frame, and 5s
				// instead of 3s makes those frames rarer. Nothing is lost: the FAST pass below
				// re-asserts colours every tick from the cached target list, so what the user sees
				// is unchanged — only the rediscovery is slower, and the menu's structure does not
				// change between one three-second window and the next anyway.
				_dirty = false;
				_nextScan = now + 5f;
				_lastScanAt = now;
				_scanFlip = !_scanFlip;
				_slot = _scanFlip ? 0 : 1;
				// Only this canvas's own findings are replaced; the other canvas keeps its colours.
				_targets.Clear();
				_live.Clear();
				// The QuickMenu is the wrist menu; everything else the user opens — the avatar page,
				// settings, the world list — lives on Canvas_MainMenu, which this module never
				// looked at, which is why its text stayed VRChat's own colour.
				if (_scanFlip) Scan(Core.QuickMenu.Root());
				else Scan(Core.QuickMenu.Main());
			}

			// Text is re-asserted on the FAST pass, not only when we rescan. VRChat's StyleEngine
			// repaints a page the moment it is shown, so a colour only refreshed every 3 seconds
			// flickers back to stock every time you change tab.
			//
			// BOTH canvases, and indexed by `canvas` — not through `_live`, which is a view on the
			// slot of the LAST scan. The loop used to count the other canvas's list but read the
			// current one, so it re-asserted the same texts twice (and walked past the end of the
			// shorter list, swallowed by the catch) while the other canvas's text silently went
			// back to stock between its scans.
			// Full sweep only when the menu can actually have been restyled; a slice otherwise.
			bool full = (now - Core.UiClick.LastPressAt) < BusyWindow || (now - _lastScanAt) < BusyWindow;

			// Hoisted: TextCol() parses a hex string, and it was being called once PER TEXT.
			var want = TextCol();
			for (int canvas = 0; canvas < 2; canvas++)
			{
				var live = _liveBy[canvas];
				int n = live.Count;
				if (n == 0) { _txtCursor[canvas] = 0; continue; }
				int take = full || n <= IdleSlice ? n : IdleSlice;
				int start = full ? 0 : _txtCursor[canvas] % n;
				for (int k = 0; k < take; k++)
				{
					try
					{
						var t = live[(start + k) % n];
						if (t == null) continue;
						if (t.color != want) t.color = want;
					}
					catch { }
				}
				_txtCursor[canvas] = (start + take) % n;
			}

			// The cheap pass: no searching, just putting the colours back where VRChat may have
			// undone them. Same rule as the text: BOTH canvases' cached targets every tick,
			// otherwise the canvas not scanned last keeps whatever VRChat restyled it to.
			for (int canvas = 0; canvas < 2; canvas++)
			{
				var targets = _targetsBy[canvas];
				int n = targets.Count;
				if (n == 0) { _imgCursor[canvas] = 0; continue; }
				int take = full || n <= IdleSlice ? n : IdleSlice;
				int start = full ? 0 : _imgCursor[canvas] % n;
				for (int k = 0; k < take; k++)
				{
					var t = targets[(start + k) % n];
					try
					{
						if (t.Img == null) continue;
						if (t.Sel != null)
						{
							var cb = t.Sel.colors;
							if (cb.normalColor != t.Want)
							{
								cb.normalColor = t.Want;
								cb.highlightedColor = new Color(t.Want.r * 1.75f, t.Want.g * 1.55f, t.Want.b * 1.55f, 1f);
								cb.pressedColor = new Color(t.Want.r * 2.3f, t.Want.g * 1.9f, t.Want.b * 1.9f, 1f);
								cb.selectedColor = cb.highlightedColor;
								cb.colorMultiplier = 1f;
								t.Sel.colors = cb;
							}
						}
						else if (t.Img.color != t.Want)
						{
							t.Img.color = t.Want;
						}
					}
					catch { }
				}
				_imgCursor[canvas] = (start + take) % n;
			}
		}

		// Every text currently themed, rebuilt on each scan. Separate from _texts, which is the
		// restore ledger and must keep the ORIGINAL colour of everything ever touched.
		private List<TMPro.TMP_Text> _live => _liveBy[_slot];

		// True for the text INSIDE a text field — the editable content, its placeholder, or
		// anything else parented under the field. Checked a few levels up rather than on the
		// component itself, because the field sits above its text in the hierarchy.
		// True when this text lives inside one of ArchiveFavButtonModule's cloned buttons — the
		// Save/Remove-to-Archive CTA and the Copy-id / Get-metadata pair. Those are ours to colour.
		private static bool IsOurButton(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					string n = p.name ?? "";
					if (n == "VA_ArchiveFavButton" || n == "VA_CopyId" || n == "VA_GetMeta") return true;
					// The two side panels (Core/PanelSkin.cs). Their text is a deliberate palette
					// ported from the mod's IMGUI roster — trust-coloured names, green coordinates,
					// coloured platform badges — and this pass would flatten all of it to one theme
					// colour. That is exactly why the old panel's title rendered red: it was the one
					// string with no colour tag on it.
					if (n == "VA_PlayersPanel" || n == "VA_LogPanel") return true;
				}
			}
			catch { }
			return false;
		}

		private static bool IsEditable(TMPro.TMP_Text t)
		{
			try
			{
				Transform p = t.transform;
				for (int i = 0; i < 4 && p != null; i++, p = p.parent)
				{
					if (p.GetComponent<TMPro.TMP_InputField>() != null) return true;
					if (p.GetComponent<UnityEngine.UI.InputField>() != null) return true;
				}
			}
			catch { }
			return false;
		}

		private void Scan(Transform qm)
		{
			if (qm == null) return;
			float h = Mathf.Max(1f, Screen.height);

			// ---- text ------------------------------------------------------------------------
			try
			{
				var texts = qm.GetComponentsInChildren<TMPro.TMP_Text>(false);
				if (texts != null)
				{
					foreach (var t in texts)
					{
						if (t == null) continue;
						try
						{
							// NEVER an input field's text. Its component is driven by the field
							// itself — caret, selection, IME composition — and writing to it several
							// times a second from outside fights all three. Chat became untypeable
							// the day this module started covering the main menu, which is where
							// VRChat's text inputs live.
							// ONCE PER TEXT, NOT ONCE PER SCAN. IsEditable walks four ancestors calling
							// GetComponent twice at each -- eight interop calls per text, every scan, for
							// an answer that cannot change: a label does not become an input field. A text
							// already in _seen passed both filters when it was first met.
							int id = t.GetInstanceID();
							bool known = _seen.Contains(id);
							if (!known && IsEditable(t)) continue;

							// NEVER the Archive buttons' labels. ArchiveFavButtonModule owns those
							// (white text on flat violet); painting them violet here is exactly why
							// "Remove from Archive" came out violet-on-dark instead of clean white.
							if (!known && IsOurButton(t.transform)) continue;

							if (!known) { _seen.Add(id); _texts.Add((t, t.color)); }   // how to undo it
							t.color = TextCol();
							Diagnose(t);
							_live.Add(t);
						}
						catch { }
					}
				}
			}
			catch { }

			// ---- card backgrounds -------------------------------------------------------------
			try
			{
				var imgs = qm.GetComponentsInChildren<Image>(false);
				if (imgs == null) return;

				foreach (var im in imgs)
				{
					if (im == null) continue;
					try
					{
						var tr = im.transform;
						// Only the panel behind a button. Icons keep their own art, and a blanket
						// repaint of every Image turns the menu into a solid block of colour.
						if ((tr.name ?? "") != "Background") continue;
						var parent = tr.parent;
						if (parent == null || !(parent.name ?? "").StartsWith("Button_", StringComparison.Ordinal)) continue;


						// Position on screen drives the tint, so the grid sweeps pink at the top to
						// violet at the bottom.
						float y = 0.5f;
						try { y = Mathf.Clamp01(tr.position.y / h); } catch { }
						Color want = Color.Lerp(GradStart(), GradEnd(), 1f - y);
						// GLASS, NOT TAR. This used to multiply the tint down to about a third and force
						// alpha to 1: every card in the menu became an OPAQUE near-black purple slab,
						// which against the wallpaper simply reads as black -- and because the paint
						// lands on a scan tick, a freshly built page showed VRChat's own look for a
						// moment and then went dark, the "sometimes black" the owner reported. VRChat's
						// cards are translucent; the theme now keeps the card's own alpha (floored so
						// white text stays readable) and tints, rather than blackens, the colour.
						float keepA = 1f;
						try { keepA = im.color.a; } catch { }
						if (keepA < 0.55f) keepA = 0.55f;
						want = new Color(want.r * 0.62f, want.g * 0.55f, want.b * 0.80f, keepA);

						// THE CARD FILL IS NO LONGER PAINTED — only the rim is.
						//
						// Tinting the Background never landed evenly: it happens on a scan tick, so a
						// page that has just been built or restyled shows some cards themed and some
						// still VRChat's, and the two states sit side by side in the same grid. The
						// owner's user menu is the clearest case — half its tiles solid magenta, half
						// black — and no cadence fixes it, because StyleElement repaints on its own
						// schedule and we only ever get to answer afterwards.
						//
						// The RIM has none of that problem: it is a child WE create, nothing else
						// writes to it, and it is what actually reads as the theme. So the colour now
						// lives entirely in the outline and VRChat keeps its own card fill — which is
						// also the look the owner asked for: "remove la couleur des bouton eux memes,
						// on garde LE system de contour de color".
						//
						// _painted / _targets are left alone deliberately: they are the undo ledger and
						// the fast re-assert list, and both are correct being empty of fills now. Cards
						// painted by an earlier build simply drift back as StyleElement rewrites them.

						// Hand the colour to the SELECTABLE when there is one, instead of only
						// stamping the Image. VRChat tints a card on hover through its own
						// transition; painting the Image behind its back means the two take turns
						// writing the same pixel — which is the flicker you get when the mouse
						// passes over a tile. Driving the ColorBlock makes hover and press derive
						// FROM our colour, so there is nothing left to fight over.
						/* fill painting removed — see the note above
						var sel = im.GetComponentInParent<UnityEngine.UI.Selectable>();
						if (sel != null && sel.targetGraphic != im) sel = null;
						// A Selectable whose transition is NOT ColorTint ignores its ColorBlock
						// entirely, so writing one is a no-op; such a card goes down the
						// paint-the-Image path instead. Our own cloned cards no longer hit this:
						// MenuCard always sets ColorTint now (a white tint under keepStyle, so hover
						// and press react), so they take the Selectable path above like every stock
						// card — expected, and it is what keeps their hover shade derived from the
						// violet we write into normalColor here.
						if (sel != null && sel.transition != UnityEngine.UI.Selectable.Transition.ColorTint) sel = null;
						_targets.Add(new Target { Img = im, Sel = sel, Want = want, Ours = IsOurGrid(parent) });
						*/

						// The BACKGROUND above is themed for every card including ours, so the Archive tab
						// matches the rest of the menu. The RIM is different: on our tiles it carries the
						// toggle state (MenuCard paints it pink/blue), and repainting it here every 0.35s
						// would wipe that out a third of a second after every press.
						if (!IsOurGrid(parent)) InnerGlow(tr, want);
					}
					catch { }
				}
			}
			catch { }
		}

		// INNER GLOW — a violet rim that fades INWARD from the card's edge.
		//
		// Built as a 9-sliced sprite rather than a stretched gradient: a plain radial texture
		// stretched across a wide card would smear its falloff into an oval. With a border, the rim
		// keeps the same thickness whatever size the card is.
		private const string GlowChild = "VA_InnerGlow";

		private void InnerGlow(Transform background, Color baseColor)
		{
			try
			{
				var existing = background.Find(GlowChild);
				Color rim = new Color(
					Mathf.Clamp01(baseColor.r * 3.4f + 0.18f),
					Mathf.Clamp01(baseColor.g * 2.0f + 0.06f),
					Mathf.Clamp01(baseColor.b * 3.0f + 0.30f), 0.75f);

				if (existing != null)
				{
					var ex = existing.GetComponent<UnityEngine.UI.Image>();
					if (ex != null) ex.color = rim;
					// RE-ADOPT IT (2026-09-13). This path re-uses a rim found in the scene but used to
					// return without registering it, so any rim that outlived a _glows reset — a scene
					// load, a rebuild — was ours to see and nobody's to destroy: RestoreAll() walks
					// _glows alone, so those rims stayed on the menu with the theme switched off.
					// Guarded against double-add for the common case where it is already tracked.
					try { var g = existing.gameObject; if (g != null && !_glows.Contains(g)) _glows.Add(g); } catch { }
					return;
				}

				var sp = Core.MenuCard.RimSprite();
				if (sp == null) return;

				var go = new GameObject(GlowChild);
				var rt = go.AddComponent<RectTransform>();
				rt.SetParent(background, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero;
				rt.offsetMax = Vector2.zero;

				var img = go.AddComponent<UnityEngine.UI.Image>();
				img.sprite = sp;
				img.type = UnityEngine.UI.Image.Type.Sliced;
				img.color = rim;
				img.raycastTarget = false;      // must never eat the card's clicks
				_glows.Add(go);
			}
			catch { }
		}

		private readonly List<GameObject> _glows = new List<GameObject>();

		// True for a card sitting on the Archive tab's own grid.
		private static bool IsOurGrid(Transform card)
		{
			try
			{
				var g = card.parent;
				if (g == null) return false;
				string n = g.name ?? "";
				return n == "Buttons_Archive" || n == "Buttons_ArchivesTools" || n == "Buttons_ArchiveTools";
			}
			catch { return false; }
		}

		private void RestoreAll()
		{
			for (int i = 0; i < _painted.Count; i++)
			{
				try { if (_painted[i].G != null) _painted[i].G.color = _painted[i].C; }
				catch { }
			}
			for (int i = 0; i < _texts.Count; i++)
			{
				try { if (_texts[i].T != null) _texts[i].T.color = _texts[i].C; }
				catch { }
			}
			for (int i = 0; i < _glows.Count; i++)
			{
				try { if (_glows[i] != null) UnityEngine.Object.Destroy(_glows[i]); }
				catch { }
			}
			_glows.Clear();
			// BOTH canvases' lists, not `_live`/`_targets` — those are views on the CURRENT slot
			// only, so the other canvas's targets survived a restore and were re-asserted violet on
			// the next tick with the toggle off.
			_painted.Clear(); _texts.Clear(); _seen.Clear();
			_liveBy[0].Clear(); _liveBy[1].Clear(); _targetsBy[0].Clear(); _targetsBy[1].Clear();
		}

	}
}
