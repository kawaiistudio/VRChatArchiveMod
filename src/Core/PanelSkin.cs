using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace VRChatArchiveMod.Core
{
	// THE SIDE PANELS, BUILT RATHER THAN CLONED.
	//
	// Three earlier attempts cloned one of VRChat's own wing pages and tried to bend it into a panel.
	// Every one failed, and each for its own reason, all of them measured:
	//   · nested inside a wing's list, two layout groups fought and the panel came out {356x0};
	//   · left always-active among the wing's pages, it painted over whatever the wing's own
	//     MenuStateController was showing, so the wings went blank;
	//   · attached beside the wings it looked right, but the clone carried a UIPage and VRChat logged
	//     "Duplicate UIPage with same name: Root" then "InitializeUI canceled" — the QuickMenu would
	//     no longer OPEN (see MenuDonor.CloneInert).
	//
	// Cloning was the right instinct for BUTTONS, where inheriting VRChat's font, sprites, hover and
	// click sound is the whole point. It is the wrong instinct here, because this panel is not meant
	// to look like VRChat at all — it is meant to look like the mod's own IMGUI roster. Everything a
	// clone would give us for free is something we would then have to fight:
	//   · StyleElement repaints Image.sprite and Image.color on every restyle, and comes BACK on a
	//     clone after being destroyed (ArchiveFavButtonModule keeps a 2 Hz re-strip loop for exactly
	//     that). A hand-built GameObject never carries one, so there is nothing to fight — measured:
	//     VA_Sliders, VA_Glow, VA_InnerGlow and the fav grid all hold their colours with no timer.
	//   · a page brings a UIPage, and that is what cost the user their menu.
	//
	// So this file builds the panel out of new GameObjects, Images and TMP texts, with every sprite
	// generated in code. The one thing it still borrows from VRChat is the FONT, taken off a live TMP
	// so the text matches the menu instead of falling back to a default face.
	//
	// The palette and geometry are ported from the IMGUI panel (Core/Hud.cs + Core/GuiKit.cs +
	// InstancePanelsModule) so the two read as the same product.
	public static class PanelSkin
	{
		// ---------------------------------------------------------------- palette (from Core/Hud.cs)

		private static Color Rgb(int hex, float a = 1f) =>
			new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

		private static readonly Color Body = Rgb(0x0B0B14, 0.90f);   // panel body
		private static readonly Color Head = Rgb(0x161323, 0.95f);   // header slab
		private static readonly Color Pink = Rgb(0xFF6AD5);
		private static readonly Color Violet = Rgb(0x8143E6);
		private static readonly Color Hair = new Color(1f, 1f, 1f, 0.06f);   // under the header
		private static readonly Color Rule = new Color(1f, 1f, 1f, 0.10f);   // under the column heads

		public const string HexText = "F2F5FC";
		// LIGHTER than the HUD's #7A879C it was ported from. That grey works on the HUD, which sits on
		// flat near-black; here the column headings and the timestamps sat on a dimmed WALLPAPER and
		// simply disappeared into it. Contrast against the actual background is what matters, not
		// fidelity to the original hex.
		public const string HexDim = "A9BAD4";
		public const string HexPink = "FF6AD5";
		// The byline sat in the dim grey used for column headings and simply did not read. It is a
		// signature, not a column label, so it takes the panel's own accent instead.
		public const string HexByline = "E07BFF";

		// ---------------------------------------------------------------- geometry

		public const float Width = 480f;
		public const float Height = 900f;
		private const float Radius = 14f;
		private const float HeaderH = 46f;
		private const float Bleed = 18f;    // how far the violet halo spills past the edge
		private const float RowH = 32f;
		private const float ColHeadH = 26f;
		private const float PadX = 12f;

		private const float ColId = 50f;     // "[12]"
		private const float ColPos = 104f;   // "0.1 -0.0 -12.4"
		private const float ColBadge = 126f; // "[B] VRC+ 18+ PC" — the blocked tag rides in this column
		private const float ColGap = 6f;

		// The wing is 420 wide when open; the panel clears it plus a small gap. Both are needed at
		// runtime, not just at build time, because the panel slides in as the wing retracts.
		private const float WingWidth = 420f;
		private const float Gap = 16f;

		// How far below the top of Window the panel starts. Not a taste value: the live dump has
		// "Wing_Left {0x1024 pos(2,-146) a(0,1-0,1) p(1,1)}", so the wings themselves hang 146 units
		// down. Sitting at 0 is what made the panel float above them.
		private const float TopY = -146f;

		// How much of the wallpaper is drowned out. 0 = raw art, 1 = black. Two layers, because one
		// value cannot serve both jobs: the art has to stay visible, and the text has to stay
		// readable over it. Veil dims the whole panel; VeilRows darkens only the strip the rows sit
		// on, which is where legibility is actually decided. Kept here rather than baked into the
		// JPEG so both can be retuned without regenerating art.
		// Covers the WHOLE panel, evenly. Three arrangements were tried before this one: a light veil
		// (artwork beautiful, text unreadable), a heavy plate under each row (text sharp, but with a
		// full roster the adjacent rows merged into a black slab across the middle of the picture and
		// the art only survived where the list ran out), and no plate at all with the legibility left
		// to the glyph halo (still not enough over bright passages of the wallpaper). One even veil
		// over everything is what actually works: the artwork stays readable AS artwork because it is
		// dimmed uniformly rather than blotched, and every row gets the same dark ground whether the
		// list holds one player or forty.
		private const float Veil = 0.86f;

		// Per-row plates, off. Superseded by the veil above — two darkening layers stacked would put
		// the rows back to near-black. Set above 0 only if the veil is ever lowered again.
		private const float VeilRows = 0f;

		// Text sizes. Bigger than a straight port of the IMGUI panel (13 px at 1080p): these are
		// canvas units on a surface the user reads at arm's length, and in VR from further still.
		private const float SizeTitle = 22f;
		private const float SizeCount = 19f;
		private const float SizeRow = 19f;
		private const float SizeHead = 15f;

		// ---------------------------------------------------------------- public shape

		public struct Row
		{
			public string Id;      // "[1]"    / "19:26:11"
			public string Name;    // the rich-text label
			public string Pos;     // "0.1 -0.0 -12.4" / ""
			public string Badge;   // "18+ PC" / "JOIN"
		}

		public sealed class Panel
		{
			public Transform Root;
			internal RectTransform RootR;   // the same object, resolved once (see Host)
			internal Transform Rows;
			internal TMPro.TMP_Text Count;
			internal readonly List<Transform> Pool = new List<Transform>();
			internal TMPro.TMP_FontAsset Font;
			internal CanvasGroup Wing;      // the wing's fade group, when it has one
			internal Transform WingT;       // the wing object itself
			internal RectTransform WingR;   // the wing's 420-wide content, whose edge we track
			internal RectTransform Host;    // Window, the space both are measured in
			internal Image Frame;           // the border, rebaked when the panel is resized
			internal int FrameW, FrameH;    // the size its texture was baked for
			internal bool Left;
			// Size and row pitch belong to the INSTANCE, not to the class: the floating panels are
			// 480x900 with 32-unit rows, while the console embedded in the Launch Pad is 1024x280 and
			// needs a tighter pitch to be worth anything. Everything that used the constants directly
			// now reads these.
			// ONE CELL PER ROW instead of four. A console line is a sentence, not a record: the log in
			// the Launch Pad kept coming out with only its right-hand column filled, and rather than
			// keep chasing four rects per row across two hosts, a console asks for the whole width and
			// composes its own line — which is also exactly what the design being copied looks like.
			internal bool Single;
			// Built inside one of VRChat's own layouts (the Launch Pad), rather than floating under
			// Window. It matters for MATERIALS: see Text().
			internal bool Inline;
			// Column widths belong to the INSTANCE too. The defaults are sized for the side panels,
			// where the first column holds "[12]" and the last holds "VRC+ 18+ PC". The console puts a
			// full clock in one and a word in the other, and both came out clipped mid-character.
			internal float ColIdW = ColId;
			internal float ColBadgeW = ColBadge;
			internal float W = Width;
			internal float H = Height;
			internal float RH = RowH;
			private float _x = float.NaN;   // where the panel currently sits, in Window's space
			private bool _wasOpen = true;   // last reading, so the diagnostic logs only on a flip

			/// <summary>Keeps the panel glued to the wing's outer edge, so it slides in with a
			/// retracting wing instead of leaving a hole where the wing used to be.
			///
			/// MEASURE THE EDGE, DO NOT INFER IT. Two earlier attempts asked the wrong question and
			/// both did nothing at all: the CanvasGroup alpha never changes, and neither does the
			/// active state. VRChat's own log gives the answer —
			///     "Tween startup failed (NULL target/property - UnityEngine.Vector2 &lt;DOAnchorPosX&gt;…)"
			/// — the wings retract by TWEENING anchoredPosition.x. Nothing fades and nothing is turned
			/// off; the wing simply slides. So this reads where the wing actually IS, every frame, and
			/// puts the panel a fixed gap beyond its outer corner.
			///
			/// Corners rather than anchoredPosition, because it does not matter which object in the
			/// wing carries the tween, nor what its anchors and pivot are: the world corners of the
			/// wing's content, expressed in Window's space, are the truth in every case.</summary>
			private static Il2CppStructArray<Vector3> _corners;   // reused: this runs every frame

			public void Follow()
			{
				try
				{
					var rt = RootR;
					if (rt == null || Host == null) return;

					// THE WING CAN VANISH THREE DIFFERENT WAYS, so all three are checked. Measuring the
					// corners alone was not enough — a DEACTIVATED object keeps its transform, so its
					// corners never move and the panel sat still while the wing was plainly gone.
					bool active = true;
					try { active = WingT == null || WingT.gameObject.activeInHierarchy; } catch { }
					float alpha = 1f;
					try { if (Wing != null) alpha = Mathf.Clamp01(Wing.alpha); } catch { }
					bool open = active && alpha > 0.05f;

					float half = Host.rect.width * 0.5f;
					float target;
					float minX = 0f, maxX = 0f;

					if (open && WingR != null)
					{
						if (_corners == null) _corners = new Il2CppStructArray<Vector3>(4);
						WingR.GetWorldCorners(_corners);
						minX = float.MaxValue; maxX = float.MinValue;
						for (int i = 0; i < 4; i++)
						{
							Vector3 l = Host.InverseTransformPoint(_corners[i]);
							if (l.x < minX) minX = l.x;
							if (l.x > maxX) maxX = l.x;
						}
						// anchoredPosition is measured from this panel's anchor, which sits on Window's
						// left or right edge; Host-local coordinates are measured from its centre.
						target = Left ? (minX - Gap + half) : (maxX + Gap - half);
					}
					else
					{
						// Retracted (or no wing to read): sit against Window's own edge instead.
						target = Left ? (-half - Gap + half) : (half + Gap - half);
						target = Left ? -Gap : Gap;
					}

					// One line whenever the open/closed reading flips. Three attempts at this have now
					// failed on a wrong assumption about HOW the wing hides; this makes the next one a
					// measurement instead of another guess.
					if (open != _wasOpen)
					{
						_wasOpen = open;
						VRChatArchiveModPlugin.Logger.LogInfo("[PanelSkin] wing " + (Left ? "L" : "R")
							+ (open ? " OPEN" : " CLOSED") + ": active=" + active + " alpha=" + alpha.ToString("F2")
							+ " x=[" + minX.ToString("F0") + "," + maxX.ToString("F0") + "]"
							+ " half=" + half.ToString("F0") + " -> target=" + target.ToString("F0"));
					}

					if (float.IsNaN(_x)) _x = target;                        // first frame: no slide
					else if (Mathf.Abs(_x - target) < 0.5f) return;          // settled: write nothing
					else _x = Mathf.Lerp(_x, target, 1f - Mathf.Pow(0.001f, VaClock.Delta));

					rt.anchoredPosition = new Vector2(_x, TopY);
				}
				catch { }
			}

			/// <summary>Called with the row index when a row is clicked. Set it BEFORE the first
			/// SetRows: rows are pooled and only wired once, at creation, so a handler attached later
			/// would miss every row already made.</summary>
			public Action<int> OnRow;

			public bool Alive
			{
				get { try { return Root != null; } catch { return false; } }
			}

			/// <summary>The number shown on the right of the header, in pink. Empty hides it.</summary>
			public void SetCount(string s)
			{
				try
				{
					if (Count == null) return;
					Count.text = string.IsNullOrEmpty(s) ? "" : Tag(HexPink, "<b>" + s + "</b>");
				}
				catch { }
			}

			/// <summary>Fills the panel. Rows are pooled: the list only ever grows, spare rows are
			/// deactivated rather than destroyed, because a roster that oscillates between 12 and 13
			/// players would otherwise allocate and free a row several times a second.</summary>
			public void SetRows(List<Row> rows)
			{
				if (rows == null) return;
				try
				{
					int want = Mathf.Min(rows.Count, Capacity);
					while (Pool.Count < want)
					{
						var r = MakeRow(this, Pool.Count);
						if (r == null) break;
						Pool.Add(r);
					}
					for (int i = 0; i < Pool.Count; i++)
					{
						var t = Pool[i];
						if (t == null) continue;
						bool used = i < want;
						if (t.gameObject.activeSelf != used) t.gameObject.SetActive(used);
						if (!used) continue;
						WriteRow(t, rows[i], i);
					}
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] rows: " + e.Message); }
			}

			/// <summary>How many rows physically fit. The caller trims to this instead of overflowing,
			/// mirroring the IMGUI panel, which also draws only what fits.</summary>
			public int Capacity
			{
				get { return Mathf.Max(1, (int)((H - HeaderH - ColHeadH - 12f) / RH)); }
			}
		}

		/// <summary>Wraps text in a colour tag. EVERY string this panel shows goes through here, and
		/// that is not decoration: our own MenuThemeModule walks every TMP_Text under the QuickMenu
		/// canvas and overwrites .color every 0.35 s. A rich-text tag is applied per character at
		/// layout time, so it wins that race by not entering it.</summary>
		public static string Tag(string hex, string text) => "<color=#" + hex + ">" + text + "</color>";

		// ---------------------------------------------------------------- build

		/// <summary>Builds a panel under <paramref name="host"/>. left = hangs off the left edge.
		/// Null if the host is gone. Idempotent by name.</summary>
		public static Panel Build(Transform host, bool left, string name, string title, string wallpaper,
			Transform wing)
		{
			if (host == null) return null;
			try
			{
				var existing = host.Find(name);
				if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);

				var p = new Panel();
				p.Left = left;
				p.WingT = wing;
				// GetComponent, never "as". A Transform proxy does NOT cast to RectTransform with the
				// C# "as" operator under Il2CppInterop, even when the object really is one — it just
				// yields null. That single line is why the panel never followed the wings: Host came
				// back null and Follow() returned before measuring anything.
				p.Host = host.GetComponent<RectTransform>();
				try { if (wing != null) p.Wing = wing.GetComponent<CanvasGroup>(); } catch { }
				// The 420-wide content is what actually moves and what we measure; the Wing_* node
				// itself is 0 wide in the dump, so its corners would tell us nothing.
				try
				{
					if (wing != null)
						p.WingR = (wing.Find("Container/InnerContainer") ?? wing).GetComponent<RectTransform>();
				}
				catch { }
				p.Font = StealFont(host);
				if (p.Font == null)
				{
					// Worth a warning rather than a silent build: a TextMeshProUGUI added at runtime
					// has no font asset of its own and renders NOTHING, so the panel would come out as
					// chrome with invisible text and look like a layout bug instead of a missing font.
					VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] no TMP font found under the menu — "
						+ "text in '" + name + "' may be invisible.");
				}

				Transform root = Rect(name, host);
				p.Root = root;
				p.RootR = root.GetComponent<RectTransform>();
				var rt = (RectTransform)root;
				// Anchored to the window's top edge and pivoted AWAY from it, so the panel hangs
				// outside the menu rather than over it. The offset clears a fully open wing (420) plus
				// a gap, so an extended wing never slides underneath.
				rt.anchorMin = rt.anchorMax = new Vector2(left ? 0f : 1f, 1f);
				rt.pivot = new Vector2(left ? 1f : 0f, 1f);
				rt.anchoredPosition = new Vector2(left ? -(WingWidth + Gap) : (WingWidth + Gap), TopY);
				rt.sizeDelta = new Vector2(Width, Height);
				rt.localScale = Vector3.one;
				rt.localRotation = Quaternion.identity;

				// --- the frame. LAST in build order but drawn over everything, because a border that
				// sits under the body is a border you cannot see; it is added at the end of this
				// method. Nothing to do here but reserve the intent.

				bool hasWall = Assemble(p, root, title, wallpaper);

				// The wing handles are logged because the panel following the wing depends entirely on
				// them: "wing -" here is the one-line explanation for "it does not slide".
				VRChatArchiveModPlugin.Logger.LogInfo("[PanelSkin] built '" + name + "' ("
					+ (left ? "left" : "right") + ", capacity " + p.Capacity
					+ ", wallpaper " + (hasWall ? "yes" : "no")
					+ ", wing " + (wing != null ? wing.name : "-")
					+ ", tracking " + (p.WingR != null ? p.WingR.name : "none") + ").");
				return p;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] build of " + name + " failed: " + e.Message);
				return null;
			}
		}

		/// <summary>Everything a panel is made of, once its root exists and has been positioned:
		/// body, wallpaper, veil, header, rows and frame. Split out from Build because there are now
		/// two ways to place a panel — floating beside a wing, or embedded in one of VRChat's own
		/// vertical layouts — and only the PLACEMENT differs between them. Returns whether a
		/// wallpaper was actually found, which the caller logs.</summary>
		private static bool Assemble(Panel p, Transform root, string title, string wallpaper)
		{
			// --- body: rounded fill, and the mask that gives the wallpaper the same rounded corners.
			// A panel with no wallpaper is a console, and a console wants a solid ground: at 0.90 the
			// menu's own artwork showed through the log text. With a wallpaper the veil handles this,
			// so only the bare case is darkened.
			// FULLY opaque with no wallpaper. 0.97 still let the menu's own artwork read through the
			// log text — three percent of a bright picture is plenty to ruin small type. A console has
			// no reason to be translucent at all; the panels that DO show art keep their veil instead.
			var bodyImg = Img(Rect("VA_Body", root), RoundSprite(),
				string.IsNullOrEmpty(wallpaper) ? new Color(0.035f, 0.030f, 0.062f, 1f) : Body);
			Stretch(bodyImg.rectTransform, 0f);
			bodyImg.type = Image.Type.Sliced;
			// A graphic with real alpha is required to receive drags later; it also has to stay
			// non-blocking so it never eats a click meant for the menu behind it.
			bodyImg.raycastTarget = false;
			Transform bodyT = bodyImg.transform;
			// ONLY WHEN THERE IS ARTWORK TO CLIP. The Mask exists for one reason: to give the wallpaper
			// the body's rounded corners. With no wallpaper it clips nothing — and it is not free.
			//
			// A Mask works through the stencil buffer, and the Launch Pad console lives inside VRChat's
			// own ScrollRect, under a RectMask2D, on a nested Canvas. Stacking our stencil mask inside
			// that is the one thing the console did differently from the two side panels that render
			// correctly, and its rows drew nothing at all while its header, frame and background were
			// all fine — with the diagnostic confirming the row existed, was active, was 1024x22 and
			// held 207 characters of text. A mask that clips nothing is not worth that risk.
			if (!string.IsNullOrEmpty(wallpaper))
			{
				try
				{
					var mask = bodyImg.gameObject.AddComponent<Mask>();
					mask.showMaskGraphic = true;   // the body IS the visible background
				}
				catch (Exception e)
				{
					// Not fatal: without the mask the wallpaper simply has square corners.
					VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] mask: " + e.Message);
				}
			}

			// --- wallpaper + veil, both inside the mask so they take the rounded corners.
			var wall = LoadSprite(wallpaper);
			if (wall != null)
			{
				var w = Img(Rect("VA_Wall", bodyT), wall, Color.white);
				Stretch(w.rectTransform, 0f);
				w.preserveAspect = true;   // the art is already cropped to this exact aspect
				w.raycastTarget = false;
				var veil = Img(Rect("VA_Veil", bodyT), null, new Color(0.02f, 0.02f, 0.05f, Veil));
				Stretch(veil.rectTransform, 0f);
				veil.raycastTarget = false;
			}

			// --- header slab, its accent bar, bullet, title and count.
			var head = Img(Rect("VA_Header", root), RoundSprite(), Head);
			var hrt = head.rectTransform;
			hrt.anchorMin = new Vector2(0f, 1f); hrt.anchorMax = new Vector2(1f, 1f);
			hrt.pivot = new Vector2(0.5f, 1f);
			hrt.offsetMin = new Vector2(0f, -HeaderH); hrt.offsetMax = Vector2.zero;
			head.type = Image.Type.Sliced;
			head.raycastTarget = false;

			var accent = Img(Rect("VA_Accent", head.transform), GradientSprite(Pink, Violet), Color.white);
			var art = accent.rectTransform;
			art.anchorMin = new Vector2(0f, 1f); art.anchorMax = new Vector2(1f, 1f);
			art.pivot = new Vector2(0.5f, 1f);
			art.offsetMin = new Vector2(7f, -3f); art.offsetMax = new Vector2(-7f, 0f);
			accent.raycastTarget = false;

			var dot = Img(Rect("VA_Dot", head.transform), DotSprite(), Pink);
			var drt = dot.rectTransform;
			drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f);
			drt.pivot = new Vector2(0f, 0.5f);
			drt.anchoredPosition = new Vector2(PadX, -1f);
			drt.sizeDelta = new Vector2(9f, 9f);
			dot.raycastTarget = false;

			var titleT = Text(p, head.transform, "VA_Title", SizeTitle, TMPro.TextAlignmentOptions.Left);
			var trt = titleT.rectTransform;
			trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
			trt.offsetMin = new Vector2(PadX + 18f, 0f); trt.offsetMax = new Vector2(-PadX - 60f, 0f);
			titleT.text = Tag(HexText, "<b>" + (title ?? "") + "</b>");

			var countT = Text(p, head.transform, "VA_Count", SizeCount, TMPro.TextAlignmentOptions.Right);
			var crt = countT.rectTransform;
			crt.anchorMin = new Vector2(0f, 0f); crt.anchorMax = new Vector2(1f, 1f);
			crt.offsetMin = new Vector2(PadX, 0f); crt.offsetMax = new Vector2(-PadX, 0f);
			p.Count = countT;

			var hair = Img(Rect("VA_Hair", root), null, Hair);
			var hairt = hair.rectTransform;
			hairt.anchorMin = new Vector2(0f, 1f); hairt.anchorMax = new Vector2(1f, 1f);
			hairt.pivot = new Vector2(0.5f, 1f);
			hairt.offsetMin = new Vector2(0f, -HeaderH - 1f); hairt.offsetMax = new Vector2(0f, -HeaderH);
			hair.raycastTarget = false;

			// --- rows container, below the column headings.
			Transform rows = Rect("VA_Rows", root);
			var rrt = (RectTransform)rows;
			rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(1f, 1f);
			rrt.offsetMin = new Vector2(0f, 0f);
			rrt.offsetMax = new Vector2(0f, -(HeaderH + ColHeadH));
			p.Rows = rows;

			// --- the frame, added last so it draws over the wallpaper, the header and the rows.
			// Its texture already extends Bleed past the panel on every side, so the rect is grown
			// by exactly that much and the sprite is left Simple: it is generated at this panel's
			// size, so there is nothing to slice or stretch.
			var frame = Img(Rect("VA_Frame", root), BorderSprite((int)p.W, (int)p.H), Color.white);
			Stretch(frame.rectTransform, -Bleed);
			frame.raycastTarget = false;
			p.Frame = frame;

			return wall != null;
		}

		/// <summary>A panel built INSIDE one of VRChat's own vertical layouts, rather than floating
		/// beside the menu. Used to take over the Launch Pad's promo carousel slot.
		///
		/// The layout, not the anchors, decides where this lands, so the root is stretched and given a
		/// real LayoutElement instead of a hand-placed rect. That LayoutElement is not optional: a
		/// child with no preferred height reports zero to a VerticalLayoutGroup and simply vanishes —
		/// the same trap the settings block hit (QuickMenuTabModule's VA_Sliders footer) before it was
		/// given one.</summary>
		public static Panel BuildInline(Transform parent, string name, string title, string wallpaper,
			float width, float height, float rowPitch, bool singleColumn = false)
		{
			if (parent == null) return null;
			try
			{
				var existing = parent.Find(name);
				if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);

				var p = new Panel { W = width, H = height, RH = rowPitch, Single = singleColumn, Inline = true };
				p.Font = StealFont(parent);
				if (p.Font == null)
					VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] no TMP font under " + parent.name
						+ " — text in '" + name + "' may be invisible.");

				Transform root = Rect(name, parent);
				p.Root = root;
				p.RootR = root.GetComponent<RectTransform>();
				// EXACTLY the geometry of the object being replaced, read from the live dump:
				//   Carousel_Banners {1024x280 pos(512,-140) a(0,1-0,1) p(0.5,0.5)}
				// A FIXED size, not a stretch. Stretching was the first attempt and it collapsed the
				// panel to the height of its own header: this VerticalLayoutGroup does not impose a
				// height on its children, it READS theirs, so a rect anchored to its parent had none
				// of its own to report.
				var rt = p.RootR;
				rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
				rt.pivot = new Vector2(0.5f, 0.5f);
				rt.sizeDelta = new Vector2(width, height);
				rt.localScale = Vector3.one;
				rt.localRotation = Quaternion.identity;

				// The LayoutElement covers the other case — a layout that DOES drive its children —
				// so the panel keeps its size whichever way this group is configured.
				var le = root.gameObject.AddComponent<LayoutElement>();
				le.preferredHeight = height;
				le.minHeight = height;
				le.preferredWidth = width;
				le.minWidth = width;

				bool hasWall = Assemble(p, root, title, wallpaper);
				VRChatArchiveModPlugin.Logger.LogInfo("[PanelSkin] inline '" + name + "' under "
					+ parent.name + " (" + (int)width + "x" + (int)height + ", capacity " + p.Capacity
					+ ", wallpaper " + (hasWall ? "yes" : "no") + ").");
				return p;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] inline build of " + name + " failed: " + e.Message);
				return null;
			}
		}

		/// <summary>Turns the header into a branded one: the pink bullet is replaced by an image and
		/// the title carries a byline. Used where the panel stands in for something of VRChat's own —
		/// the Launch Pad console sits in the slot the store banners used, so it should say whose it
		/// is rather than look like an unlabelled hole in the menu.</summary>
		public static void SetBrand(Panel p, string logoResource, string title, string byline)
		{
			if (p == null || p.Root == null) return;
			try
			{
				Transform head = p.Root.Find("VA_Header");
				if (head == null) return;

				var logo = LoadSprite(logoResource);
				if (logo != null)
				{
					// The bullet and the mark want the same spot, so the bullet is hidden rather than
					// covered — two marks in one corner reads as a bug.
					try { var dot = head.Find("VA_Dot"); if (dot != null) dot.gameObject.SetActive(false); } catch { }

					Transform held = head.Find("VA_Logo");
					Image img = held != null ? held.GetComponent<Image>() : Img(Rect("VA_Logo", head), logo, Color.white);
					img.sprite = logo;
					img.preserveAspect = true;
					img.raycastTarget = false;
					var lrt = img.rectTransform;
					lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 0.5f);
					lrt.pivot = new Vector2(0f, 0.5f);
					lrt.anchoredPosition = new Vector2(PadX, 0f);
					// Overflows the header band slightly, the way a badge does. Fitting it strictly
					// inside made the mark a thumbnail on a 1024-wide panel.
					float d = HeaderH + 6f;
					lrt.sizeDelta = new Vector2(d, d);

					// The title then starts after the mark instead of after the bullet.
					var t0 = head.Find("VA_Title");
					var trt = t0 != null ? t0.GetComponent<RectTransform>() : null;
					if (trt != null) trt.offsetMin = new Vector2(PadX + d + 10f, 0f);
				}

				var tmp = head.Find("VA_Title");
				var text = tmp != null ? tmp.GetComponent<TMPro.TextMeshProUGUI>() : null;
				if (text != null)
				{
					// A branded header is the panel's masthead, not a row label: it carries the name at
					// a size you read across the room, with the byline stepped down beside it.
					text.fontSize = SizeTitle * 1.35f;
					text.text = Tag(HexText, "<b>" + (title ?? "") + "</b>")
						+ (string.IsNullOrEmpty(byline) ? "" : Tag(HexByline, "   <size=72%><b>" + byline + "</b></size>"));
				}
				var cnt = head.Find("VA_Count");
				var ctm = cnt != null ? cnt.GetComponent<TMPro.TextMeshProUGUI>() : null;
				if (ctm != null) ctm.fontSize = SizeCount * 1.2f;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] brand: " + e.Message); }
		}

		/// <summary>A panel that is a CHILD OF WINDOW but sits wherever a target rect happens to be.
		///
		/// Built because the in-layout variant could not be made to render. The Launch Pad console was
		/// parented into VRChat's own ScrollRect — a nested Canvas, under a RectMask2D, inside a
		/// VerticalLayoutGroup — and its rows never drew, while the identical code under Window drew
		/// perfectly for both side panels. Chrome, header and frame appeared; rows did not, and neither
		/// did a single-line placeholder that could not have been empty. Mask removal and per-panel
		/// materials did not change it.
		///
		/// Rather than keep guessing at that context, this puts the panel in the context KNOWN to work
		/// and moves it onto the target instead. PlaceOver measures the target's world corners in
		/// Window's space every tick, so it tracks the page exactly — the same trick that finally made
		/// the side panels follow the wings.</summary>
		public static Panel BuildOver(Transform host, string name, string title, string wallpaper,
			float rowPitch, bool singleColumn, float colIdW = ColId, float colBadgeW = ColBadge)
		{
			if (host == null) return null;
			try
			{
				var existing = host.Find(name);
				if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);

				var p = new Panel { RH = rowPitch, Single = singleColumn, ColIdW = colIdW, ColBadgeW = colBadgeW };
				p.Host = host.GetComponent<RectTransform>();
				p.Font = StealFont(host);

				Transform root = Rect(name, host);
				p.Root = root;
				p.RootR = root.GetComponent<RectTransform>();
				var rt = p.RootR;
				// Anchored to Window's top-left corner; PlaceOver writes position and size each tick.
				rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
				rt.pivot = new Vector2(0f, 1f);
				rt.localScale = Vector3.one;
				rt.localRotation = Quaternion.identity;

				bool hasWall = Assemble(p, root, title, wallpaper);
				VRChatArchiveModPlugin.Logger.LogInfo("[PanelSkin] over-panel '" + name + "' built under "
					+ host.name + " (wallpaper " + (hasWall ? "yes" : "no") + ").");
				return p;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] over-panel " + name + " failed: " + e.Message);
				return null;
			}
		}

		/// <summary>Moves and sizes an over-panel onto a target rect, both measured in Window's space.
		/// Returns false when the target is gone or not currently visible.</summary>
		private static Il2CppStructArray<Vector3> _overCorners;

		public static bool PlaceOver(Panel p, RectTransform target)
		{
			try
			{
				if (p == null || p.RootR == null || p.Host == null || target == null) return false;
				if (!target.gameObject.activeInHierarchy) return false;

				// A PAGE YOU NAVIGATED AWAY FROM IS NOT DEACTIVATED. VRChat fades it out and leaves it
				// active, so activeInHierarchy says "visible" for a page nobody is looking at — which is
				// how the console ended up drawn out in the world beside the menu the moment you opened
				// any other tab.
				//
				// The walk starts at the PARENT and never at the target: the carousel's own CanvasGroup
				// is the one WE set to zero to hide VRChat's banner, so testing the target itself would
				// hide the console permanently — the same trap that made deactivating the banner fail.
				Transform up = target.parent;
				for (int guard = 0; up != null && guard < 12; guard++, up = up.parent)
				{
					var cgUp = up.GetComponent<CanvasGroup>();
					if (cgUp != null && cgUp.alpha < 0.5f) return false;
					if (up == p.Host.transform) break;
				}

				if (_overCorners == null) _overCorners = new Il2CppStructArray<Vector3>(4);
				target.GetWorldCorners(_overCorners);
				float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
				for (int i = 0; i < 4; i++)
				{
					Vector3 l = p.Host.InverseTransformPoint(_overCorners[i]);
					if (l.x < minX) minX = l.x;
					if (l.x > maxX) maxX = l.x;
					if (l.y < minY) minY = l.y;
					if (l.y > maxY) maxY = l.y;
				}
				float w = maxX - minX, h = maxY - minY;
				if (w < 8f || h < 8f) return false;   // collapsed or off-layout this frame

				// AND IT MUST STILL BE OVER THE WINDOW. The fade check above catches a page hidden by
				// alpha; this catches one slid or scrolled away while fully opaque. The panel is not
				// inside VRChat's scroll mask (it is a child of Window, which is the only host that
				// renders our rows at all), so nothing else would ever clip it — off the window means
				// floating in the world.
				//
				// Measured on the CENTRE rather than by overlap: the banner slot is wider than the
				// window on purpose, so demanding full containment would hide a console that is
				// perfectly placed.
				Rect hostRect = p.Host.rect;
				float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
				if (cx < hostRect.xMin - 8f || cx > hostRect.xMax + 8f ||
				    cy < hostRect.yMin - 8f || cy > hostRect.yMax + 8f) return false;

				// The target is a row of a scrolling layout and is WIDER than the window that shows
				// it, so copying its width put the panel's frame outside the menu. Clamped to the
				// host, with the padding VRChat's own pages leave at their edges.
				float maxW = p.Host.rect.width - 24f;
				if (w > maxW)
				{
					minX += (w - maxW) * 0.5f;   // keep it centred on the slot it replaces
					w = maxW;
				}

				// Host-local coordinates run from its centre; this panel's anchor is the top-left.
				float halfW = p.Host.rect.width * 0.5f, halfH = p.Host.rect.height * 0.5f;
				p.RootR.sizeDelta = new Vector2(w, h);
				p.RootR.anchoredPosition = new Vector2(minX + halfW, maxY - halfH);

				// REBAKE THE BORDER WHEN THE PANEL RESIZES. The frame's texture is generated at the
				// panel's exact size and drawn Simple, so a panel built at 480x900 and then placed at
				// 1000x280 wore a border stretched by more than three to one — visibly warped corners
				// and a rim thick on two sides and thin on the others.
				//
				// Rounded to 8 units before comparing, because the target's measured size jitters by
				// fractions of a unit as the page settles, and each distinct size bakes its own
				// texture into the cache.
				int bw = Mathf.RoundToInt(w / 8f) * 8, bh = Mathf.RoundToInt(h / 8f) * 8;
				if (p.Frame != null && (bw != p.FrameW || bh != p.FrameH))
				{
					p.FrameW = bw; p.FrameH = bh;
					try { p.Frame.sprite = BorderSprite(bw, bh); } catch { }
				}

				p.W = w; p.H = h;
				return true;
			}
			catch { return false; }
		}

		/// <summary>The four column headings, in the IMGUI panel's grey italic. Any null is skipped,
		/// so the log panel can show three headings where the roster shows four.</summary>
		public static void SetHeadings(Panel p, string id, string name, string pos, string badge)
		{
			if (p == null || p.Root == null) return;
			try
			{
				Transform host = p.Root.Find("VA_ColHead");
				if (host == null)
				{
					host = Rect("VA_ColHead", p.Root);
					var rt = (RectTransform)host;
					rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
					rt.pivot = new Vector2(0.5f, 1f);
					rt.offsetMin = new Vector2(0f, -(HeaderH + ColHeadH)); rt.offsetMax = new Vector2(0f, -HeaderH);

					var rule = Img(Rect("VA_ColRule", host), null, Rule);
					var rl = rule.rectTransform;
					rl.anchorMin = new Vector2(0f, 0f); rl.anchorMax = new Vector2(1f, 0f);
					rl.pivot = new Vector2(0.5f, 0f);
					rl.offsetMin = new Vector2(PadX * 0.5f, 0f); rl.offsetMax = new Vector2(-PadX * 0.5f, 1f);
					rule.raycastTarget = false;

					// Same rule for the headings, against the heading band's own height.
					Cells(p, host, Mathf.Min(SizeHead, ColHeadH * 0.62f), true);
				}
				Write(host, "Id", id == null ? "" : Tag(HexDim, "<i>" + id + "</i>"));
				Write(host, "Name", name == null ? "" : Tag(HexDim, "<i>" + name + "</i>"));
				Write(host, "Pos", pos == null ? "" : Tag(HexDim, "<b>" + pos + "</b>"));
				Write(host, "Badge", badge == null ? "" : Tag(HexDim, "<b>" + badge + "</b>"));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] headings: " + e.Message); }
		}

		// ---------------------------------------------------------------- rows

		private static Transform MakeRow(Panel p, int index)
		{
			Transform row = Rect("VA_Row" + index, p.Rows);
			var rt = (RectTransform)row;
			rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
			rt.pivot = new Vector2(0.5f, 1f);
			rt.offsetMin = new Vector2(0f, -p.RH * (index + 1));
			rt.offsetMax = new Vector2(0f, -p.RH * index);

			// A DARK PLATE UNDER EVERY ROW — which is what makes the colours read as vivid.
			//
			// The HUD's palette looks electric because it sits on flat near-black. The same colours on
			// a wallpaper come out washed: the picture behind them raises the floor, and contrast, not
			// saturation, is what was missing. Dimming the WHOLE panel would fix that and throw the
			// artwork away with it — so only the rows get a plate, and only where there is data. Rows
			// are pitch-tall and adjacent, so a populated list reads as one dark band while an empty
			// panel still shows the picture.
			//
			// Note this is NOT the IMGUI panel's zebra: no alternation. A white 3% band on every other
			// row guides the eye on flat black, but over a picture the lightened stripes pick up
			// whatever is beneath them and the panel just looks striped.
			if (VeilRows > 0.001f)
			{
				var bg = Img(Rect("VA_RowBg", row), null, new Color(0.015f, 0.015f, 0.035f, VeilRows));
				var brt = bg.rectTransform;
				brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(1f, 1f);
				brt.offsetMin = new Vector2(2f, 0f); brt.offsetMax = new Vector2(-2f, 0f);
				bg.raycastTarget = false;
				brt.SetAsFirstSibling();
			}

			// The click target, when the caller wants rows to be clickable. It needs a graphic with
			// real (if invisible) alpha to receive the raycast at all, and it is the ONLY thing in the
			// panel with raycastTarget on — everything else is decoration and must never eat a click.
			if (p.OnRow != null)
			{
				try
				{
					var hit = Img(Rect("VA_Hit", row), null, new Color(1f, 1f, 1f, 0.001f));
					Stretch(hit.rectTransform, 0f);
					hit.raycastTarget = true;
					hit.rectTransform.SetAsFirstSibling();
					var btn = hit.gameObject.AddComponent<Button>();
					btn.transition = Selectable.Transition.None;   // we draw our own states, if any
					int captured = index;
					var handler = p.OnRow;
					UiClick.AddClick(btn, () => handler(captured));
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] row click: " + e.Message); }
			}

			// FONT SIZE FROM THE ROW PITCH, NOT A CONSTANT.
			//
			// A TMP with overflowMode = Ellipsis and word wrapping off renders NOTHING when the text
			// does not fit its rect — not a clipped version, nothing. So a 19pt label in a 22-unit row
			// is invisible, while the same label in the side panels' 32-unit rows is fine. That is the
			// whole reason the Launch Pad console drew empty rows for a whole evening: its pitch is
			// 22, and the size was hard-coded for the panels that happen to be roomier.
			//
			// 0.62 of the pitch leaves room for descenders and the outline, and never exceeds the size
			// the panels were designed with.
			Cells(p, row, Mathf.Min(SizeRow, p.RH * 0.68f), false);
			return row;
		}

		// The four cells, anchored so the panel can be resized without re-doing arithmetic: ID pinned
		// left, BADGE and POS pinned right, NAME taking whatever is left. This is the "real per-cell
		// layout" the IMGUI panel could not have — there it faked columns by padding with spaces,
		// which only ever looked right by accident with a proportional font.
		private static void Cells(Panel p, Transform host, float size, bool heading)
		{
			if (p.Single)
			{
				var only = Text(p, host, "Name", size, TMPro.TextAlignmentOptions.Left);
				var ort = only.rectTransform;
				ort.anchorMin = new Vector2(0f, 0f); ort.anchorMax = new Vector2(1f, 1f);
				ort.offsetMin = new Vector2(PadX, 0f);
				ort.offsetMax = new Vector2(-PadX, 0f);
				return;
			}

			var id = Text(p, host, "Id", size - 1f, TMPro.TextAlignmentOptions.Left);
			var a = id.rectTransform;
			a.anchorMin = new Vector2(0f, 0f); a.anchorMax = new Vector2(0f, 1f);
			a.pivot = new Vector2(0f, 0.5f);
			a.anchoredPosition = new Vector2(PadX, 0f);
			a.sizeDelta = new Vector2(p.ColIdW, 0f);

			var name = Text(p, host, "Name", size, TMPro.TextAlignmentOptions.Left);
			var b = name.rectTransform;
			b.anchorMin = new Vector2(0f, 0f); b.anchorMax = new Vector2(1f, 1f);
			b.offsetMin = new Vector2(PadX + p.ColIdW + ColGap, 0f);
			b.offsetMax = new Vector2(-(PadX + p.ColBadgeW + ColGap + ColPos + ColGap), 0f);

			var pos = Text(p, host, "Pos", size - 2f, TMPro.TextAlignmentOptions.Right);
			var c = pos.rectTransform;
			c.anchorMin = new Vector2(1f, 0f); c.anchorMax = new Vector2(1f, 1f);
			c.pivot = new Vector2(1f, 0.5f);
			c.anchoredPosition = new Vector2(-(PadX + p.ColBadgeW + ColGap), 0f);
			c.sizeDelta = new Vector2(ColPos, 0f);

			var badge = Text(p, host, "Badge", size - 2f, TMPro.TextAlignmentOptions.Right);
			var d = badge.rectTransform;
			d.anchorMin = new Vector2(1f, 0f); d.anchorMax = new Vector2(1f, 1f);
			d.pivot = new Vector2(1f, 0.5f);
			d.anchoredPosition = new Vector2(-PadX, 0f);
			d.sizeDelta = new Vector2(p.ColBadgeW, 0f);
		}

		// One-shot dump of the first row that is ever written: the string handed to each cell and the
		// width the cell actually has. A cell that is empty on screen is either being given nothing or
		// given no room, and those two look identical from the outside.
		// PER PANEL, not once globally: the first version was a single bool, so the side panel consumed
		// it and the Launch Pad console — the one actually misbehaving — never reported anything.
		private static readonly HashSet<string> _rowDumped = new HashSet<string>();

		private static void WriteRow(Transform row, Row r, int index)
		{
			string owner = "?";
			try { owner = row.parent != null && row.parent.parent != null ? row.parent.parent.name : "?"; } catch { }
			if (index == 0 && _rowDumped.Add(owner))
			{
				try
				{
					var sb = new System.Text.StringBuilder("[PanelSkin] row0 under ").Append(owner);
					try
					{
						// The row's own geometry and its container's: a cell can be perfectly filled and
						// still invisible because the rect it lives in has no height, or sits outside
						// the panel entirely.
						var rr = row.GetComponent<RectTransform>();
						var pr = row.parent != null ? row.parent.GetComponent<RectTransform>() : null;
						sb.Append(" | row ").Append(rr != null ? rr.rect.width.ToString("F0") + "x" + rr.rect.height.ToString("F0") : "?")
						  .Append(" @").Append(rr != null ? rr.anchoredPosition.ToString() : "?")
						  .Append(" active=").Append(row.gameObject.activeInHierarchy)
						  .Append(" | container ").Append(pr != null ? pr.rect.width.ToString("F0") + "x" + pr.rect.height.ToString("F0") : "?");
					}
					catch { }
					foreach (string cell in new[] { "Id", "Name", "Pos", "Badge" })
					{
						var c = row.Find(cell);
						var t = c != null ? c.GetComponent<TMPro.TextMeshProUGUI>() : null;
						var crt = c != null ? c.GetComponent<RectTransform>() : null;
						string val = cell == "Id" ? r.Id : cell == "Name" ? r.Name : cell == "Pos" ? r.Pos : r.Badge;
						sb.Append(" | ").Append(cell).Append(": found=").Append(t != null)
						  .Append(" w=").Append(crt != null ? crt.rect.width.ToString("F0") : "-")
						  .Append(" rich=").Append(t != null && t.richText)
						  .Append(" len=").Append(val != null ? val.Length : -1);
					}
					VRChatArchiveModPlugin.Logger.LogInfo(sb.ToString());
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] row0: " + e.Message); }
			}

			Write(row, "Id", string.IsNullOrEmpty(r.Id) ? "" : Tag(HexDim, r.Id));
			Write(row, "Name", r.Name ?? "");
			Write(row, "Pos", r.Pos ?? "");
			Write(row, "Badge", r.Badge ?? "");
		}

		private static void Write(Transform host, string child, string text)
		{
			try
			{
				var t = host.Find(child);
				if (t == null) return;
				var tmp = t.GetComponent<TMPro.TextMeshProUGUI>();
				if (tmp != null) tmp.text = text;
			}
			catch { }
		}

		// ---------------------------------------------------------------- primitives

		private static Transform Rect(string name, Transform parent)
		{
			var go = new GameObject(name, new Il2CppSystem.Type[] { Il2CppType.Of<RectTransform>() });
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			rt.localScale = Vector3.one;
			rt.localRotation = Quaternion.identity;
			return rt;
		}

		private static void Stretch(RectTransform rt, float outset)
		{
			rt.anchorMin = new Vector2(0f, 0f);
			rt.anchorMax = new Vector2(1f, 1f);
			rt.offsetMin = new Vector2(outset, outset);
			rt.offsetMax = new Vector2(-outset, -outset);
		}

		private static Image Img(Transform t, Sprite sprite, Color c)
		{
			var img = t.gameObject.AddComponent<Image>();
			if (sprite != null) img.sprite = sprite;
			img.color = c;
			img.raycastTarget = false;
			return img;
		}

		private static TMPro.TextMeshProUGUI Text(Panel p, Transform parent, string name, float size,
			TMPro.TextAlignmentOptions align)
		{
			Transform t = Rect(name, parent);
			var tmp = t.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
			if (p != null && p.Font != null)
			{
				tmp.font = p.Font;
				// ONE shared material for the whole panel, not tmp.fontMaterial per label. Touching
				// fontMaterial instantiates a material behind each text, and a panel has four labels
				// per row: at ~25 rows that is a hundred materials and a hundred draw calls for one
				// visual effect. A single instance keeps them batched — and being OUR copy, nothing
				// VRChat renders with is modified.
				//
				// NOT FOR AN INLINE PANEL. TextMeshPro encodes the masking state INTO the material
				// (stencil id, clip rect), and the Launch Pad console lives inside VRChat's ScrollRect
				// under a RectMask2D while the side panels hang under Window with no mask at all.
				// Handing both the same material asks one shared stencil to satisfy two different
				// mask hierarchies, and the console drew nothing — not one row, not even a
				// single-line placeholder that could not have been empty.
				if (!p.Inline)
				{
					var m = OutlineMat(p.Font);
					if (m != null) tmp.fontSharedMaterial = m;
				}
			}
			tmp.fontSize = size;
			tmp.richText = true;
			tmp.alignment = align;
			tmp.enableWordWrapping = false;
			tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
			tmp.raycastTarget = false;
			// A colour is still set, even though every string is tagged: an untagged fallback keeps a
			// stray label legible instead of black-on-black if a caller ever forgets Tag().
			tmp.color = Rgb(0xECEFF7);
			tmp.text = "";
			return tmp;
		}

		/// <summary>A copy of the menu font's material carrying a black outline and a soft dark
		/// underlay. This is what makes text readable ON TOP OF ARTWORK: a wallpaper has light and
		/// dark passages, so no single text colour can hold contrast everywhere, and darkening the
		/// picture enough to fix that would defeat having a picture at all. An outline gives every
		/// glyph its own contrast, whatever it happens to be sitting on.
		///
		/// A COPY, never the font's own material — that one is shared with every text in the menu, so
		/// writing to it would outline the whole of VRChat's UI.</summary>
		private static Material _mat;
		private static bool _matTried;
		private static Material OutlineMat(TMPro.TMP_FontAsset font)
		{
			if (_matTried) return _mat;
			_matTried = true;
			try
			{
				var src = font != null ? font.material : null;
				if (src == null) return null;
				_mat = new Material(src);
				_mat.hideFlags = HideFlags.HideAndDontSave;
				// Property names rather than ShaderUtilities ids: a font whose shader lacks them
				// simply ignores the writes, which is the graceful outcome we want.
				// THE OUTLINE IS NOW THE ONLY THING MAKING THE TEXT READABLE — there is no plate behind
				// the rows any more — so the work is split between two effects rather than piled onto
				// one. A thick outline would fatten each glyph INWARDS and eat the pixels carrying its
				// colour, which is exactly how an earlier build came out muted; so the outline stays
				// modest and crisp, and the heavy lifting goes to the underlay: a dilated, softened
				// black halo that sits AROUND the glyph without touching its shape. Same principle as
				// subtitles over film, and the reason they stay legible over anything.
				_mat.EnableKeyword("OUTLINE_ON");
				_mat.SetFloat("_OutlineWidth", 0.14f);
				_mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 1f));
				_mat.EnableKeyword("UNDERLAY_ON");
				_mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 1f));
				_mat.SetFloat("_UnderlaySoftness", 0.20f);
				_mat.SetFloat("_UnderlayDilate", 0.50f);
				_mat.SetFloat("_UnderlayOffsetX", 0f);
				_mat.SetFloat("_UnderlayOffsetY", 0f);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] outline material: " + e.Message);
				_mat = null;
			}
			return _mat;
		}

		/// <summary>A live TMP font from the menu, so the panel reads as part of VRChat's UI rather
		/// than falling back to TMP's default face.</summary>
		private static TMPro.TMP_FontAsset _font;
		private static TMPro.TMP_FontAsset StealFont(Transform host)
		{
			if (_font != null) return _font;
			try
			{
				var t = host.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (t != null) _font = t.font;
			}
			catch { }
			return _font;
		}

		// ---------------------------------------------------------------- generated sprites
		//
		// All chrome is generated, because nothing else is available: AssetLoader can only reach what
		// the csproj embeds, and no border or glow art is embedded. Each sprite is built once, cached,
		// and marked HideAndDontSave so a scene change cannot collect it.

		private static Sprite _round, _halo, _dot;
		private static readonly Dictionary<string, Sprite> _wall = new Dictionary<string, Sprite>();

		private const int Tex = 64;
		private const int Corner = 22;   // 9-slice border, same convention as MenuCard.RimSprite

		/// <summary>Signed distance from (x,y) to the edge of a rounded rect inset by <paramref name="inset"/>.
		/// Positive inside. The corner case is a real arc, so the corners are actually round rather
		/// than chamfered.</summary>
		private static float RoundDist(int x, int y, int size, float radius, float inset)
		{
			float dx = Mathf.Min(x, size - 1 - x) - inset;
			float dy = Mathf.Min(y, size - 1 - y) - inset;
			if (dx >= radius || dy >= radius) return Mathf.Min(dx, dy);
			float ox = radius - dx, oy = radius - dy;
			return radius - Mathf.Sqrt(ox * ox + oy * oy);
		}

		private static Texture2D NewTex(int w, int h)
		{
			var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
			t.hideFlags = HideFlags.HideAndDontSave;
			t.wrapMode = TextureWrapMode.Clamp;
			t.filterMode = FilterMode.Bilinear;
			return t;
		}

		private static Sprite Wrap(Texture2D t, float border)
		{
			var s = Sprite.Create(t, new UnityEngine.Rect(0f, 0f, t.width, t.height),
				new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
				new Vector4(border, border, border, border));
			s.hideFlags = HideFlags.HideAndDontSave;
			return s;
		}

		/// <summary>Solid rounded rect, 9-sliced so the corner radius stays constant at any size.</summary>
		private static Sprite RoundSprite()
		{
			if (_round != null) return _round;
			var t = NewTex(Tex, Tex);
			var px = new Il2CppStructArray<Color32>(Tex * Tex);
			for (int y = 0; y < Tex; y++)
				for (int x = 0; x < Tex; x++)
				{
					float d = RoundDist(x, y, Tex, Corner, 0f);
					float a = Mathf.Clamp01(d + 0.5f);   // half-pixel ramp = cheap antialiasing
					px[y * Tex + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
				}
			t.SetPixels32(px);
			t.Apply(false, false);
			_round = Wrap(t, Corner);
			return _round;
		}

		/// <summary>The frame: a crisp pink-to-violet border hugging the panel's rounded edge, plus the
		/// violet halo spilling outside it.
		///
		/// Generated at the panel's EXACT size and NOT 9-sliced, which is the whole point. A uGUI Image
		/// is one flat colour, and a 9-sliced sprite repeats its edge, so neither can carry a gradient
		/// along the border — the mod's IMGUI panel works around that by stacking five flat violet
		/// rects, and the menu grid by tinting each card from its screen position. Here the panel has a
		/// fixed size, so the gradient can simply be baked in: one 456x936 texture, built once, shared
		/// by both panels because both are the same size and palette.</summary>
		private static readonly Dictionary<long, Sprite> _borders = new Dictionary<long, Sprite>();

		private static Sprite BorderSprite(int w, int h)
		{
			// Keyed by SIZE. The sprite is baked at the panel's exact dimensions, so a single cached
			// one was fine while every panel was 480x900 — the moment a second shape exists (the
			// 1024x280 console in the Launch Pad) a shared cache hands it the wrong frame, stretched.
			long key = ((long)w << 32) | (uint)h;
			if (_borders.TryGetValue(key, out var cached)) return cached;
			int bw = w + (int)(Bleed * 2f), bh = h + (int)(Bleed * 2f);
			var t = NewTex(bw, bh);
			var px = new Il2CppStructArray<Color32>(bw * bh);
			const float RimW = 2.5f;    // how thick the bright edge reads
			const float Halo = 0.50f;   // peak alpha of the spill just outside the rim
			for (int y = 0; y < bh; y++)
			{
				for (int x = 0; x < bw; x++)
				{
					// Signed distance to the panel's own rounded edge; positive inside the panel.
					float dx = Mathf.Min(x, bw - 1 - x) - Bleed;
					float dy = Mathf.Min(y, bh - 1 - y) - Bleed;
					float d;
					if (dx >= Radius || dy >= Radius) d = Mathf.Min(dx, dy);
					else { float ox = Radius - dx, oy = Radius - dy; d = Radius - Mathf.Sqrt(ox * ox + oy * oy); }

					float a;
					if (d < 0f) { float k = Mathf.Max(0f, 1f + d / Bleed); a = k * k * Halo; }   // outside: glow
					else if (d < RimW) a = 1f;                                                   // the edge itself
					else a = Mathf.Max(0f, 1f - (d - RimW) / 2f);                                 // fade inwards
					if (a <= 0.002f) { px[y * bw + x] = new Color32(0, 0, 0, 0); continue; }

					// Diagonal sweep: pink at the top-left, violet at the bottom-right, the same two
					// stops the header's accent bar uses so the whole panel reads as one palette.
					float k2 = Mathf.Clamp01((x / (float)bw + (1f - y / (float)bh)) * 0.5f);
					Color c = Color.Lerp(Violet, Pink, k2);
					px[y * bw + x] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f),
						(byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
				}
			}
			t.SetPixels32(px);
			t.Apply(false, false);
			var sp = Sprite.Create(t, new UnityEngine.Rect(0f, 0f, bw, bh), new Vector2(0.5f, 0.5f));
			sp.hideFlags = HideFlags.HideAndDontSave;
			_borders[key] = sp;
			return sp;
		}

		private static Sprite DotSprite()
		{
			if (_dot != null) return _dot;
			const int N = 32;
			var t = NewTex(N, N);
			var px = new Il2CppStructArray<Color32>(N * N);
			float r = N * 0.5f - 0.5f;
			for (int y = 0; y < N; y++)
				for (int x = 0; x < N; x++)
				{
					float dx = x - r, dy = y - r;
					float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
					px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
				}
			t.SetPixels32(px);
			t.Apply(false, false);
			_dot = Sprite.Create(t, new UnityEngine.Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f));
			_dot.hideFlags = HideFlags.HideAndDontSave;
			return _dot;
		}

		/// <summary>The pink-to-violet accent bar. A uGUI Image is one flat colour, so a gradient has
		/// to live in a texture — this is the same two stops Hud.AccentBar lerps between, except
		/// smooth instead of banded into twelve quads.</summary>
		private static Sprite _grad;
		private static Sprite GradientSprite(Color a, Color b)
		{
			if (_grad != null) return _grad;
			const int W = 64;
			var t = NewTex(W, 2);
			var px = new Il2CppStructArray<Color32>(W * 2);
			for (int x = 0; x < W; x++)
			{
				Color c = Color.Lerp(a, b, x / (float)(W - 1));
				var c32 = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), 255);
				px[x] = c32;
				px[W + x] = c32;
			}
			t.SetPixels32(px);
			t.Apply(false, false);
			_grad = Sprite.Create(t, new UnityEngine.Rect(0f, 0f, W, 2), new Vector2(0.5f, 0.5f));
			_grad.hideFlags = HideFlags.HideAndDontSave;
			return _grad;
		}

		private static Sprite LoadSprite(string resource)
		{
			if (string.IsNullOrEmpty(resource)) return null;
			if (_wall.TryGetValue(resource, out var s)) return s;   // misses cached too
			s = null;
			try
			{
				var tex = AssetLoader.Icon(resource);
				if (tex != null)
				{
					s = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
					s.hideFlags = HideFlags.HideAndDontSave;
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PanelSkin] wallpaper " + resource + ": " + e.Message); }
			_wall[resource] = s;
			return s;
		}
	}
}
