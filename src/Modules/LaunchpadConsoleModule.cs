using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE EVENT CONSOLE, IN THE SPACE VRCHAT USES FOR ADVERTS.
	//
	// The Launch Pad's first row is a promo carousel — the store banners that rotate above the tiles.
	// It is a fixed 1024x280 slot in the page's own VerticalLayoutGroup:
	//
	//   Menu_QM_Launchpad/ScrollRect/Viewport/VerticalLayoutGroup/Carousel_Banners
	//     <CanvasRenderer, LayoutElement, StyleElement, …>  {1024x280 pos(512,-140)}
	//
	// A whole banner's worth of prime menu real estate, showing something nobody opened the menu for.
	// This puts the Udon/network event console there instead.
	//
	// TAKEN OVER, NOT DESTROYED. The carousel is deactivated, never deleted, and reactivated the
	// moment this feature is switched off — VRChat rebuilds that page from its own prefab and would
	// have every right to be upset about a missing child. Our console is a SIBLING inserted at the
	// carousel's index, so the layout puts it in exactly the same place.
	//
	// It is BUILT, not cloned: cloning a menu object drags VRChat's StyleElement along, and a cloned
	// page once brought a UIPage that stopped the QuickMenu from opening at all (MenuDonor.CloneInert
	// has that story). A hand-made subtree has neither.
	public class LaunchpadConsoleModule : IModule
	{
		public override string Name => "LaunchpadConsole";

		private const string PanelName = "VA_LaunchpadConsole";
		private const string CarouselName = "Carousel_Banners";
		// The carousel's own footprint, read from the live dump. Matching it exactly is what keeps
		// the rest of the page — the tiles below — from moving when we take the slot.
		// 918, not the carousel's own 1024: its visible artwork is clipped by
		//   Carousel_Banners/Image_MASK {918x280}
		// so the banner READS as 918 wide even though its rect is the full page width. Matching the
		// rect made the console run wider than everything else on the page.
		private const float SlotW = 918f;
		private const float SlotH = 280f;
		// 26, not 22. The pitch caps the font size (a TMP that does not fit its rect renders nothing at
		// all), so a tight pitch quietly forces small text — and this panel is read from across a room.
		// At 26 the slot still holds eight lines, which is what it showed before.
		private const float RowPitch = 26f;

		/// <summary>ON by default — the owner asked for this slot specifically. It is still a toggle,
		/// because it does replace part of VRChat's own menu and anyone should be able to give the
		/// banner back. The carousel is only ever deactivated, never destroyed, so that is one flip.</summary>
		public static bool Active { get; private set; } = true;
		public static string Status = "";

		private PanelSkin.Panel _panel;
		private Transform _carousel;
		private RectTransform _slot;   // the carousel's rect: what the panel is placed over
		private TMPro.TMP_Text _pageTitle;
		private float _nextTitle;
		private float _nextTry;
		private int _fails;
		private int _shown = -1;

		public static void Toggle()
		{
			Active = !Active;
			Status = Active ? "event console takes the Launch Pad banner slot"
			                : "Launch Pad banner restored";
			VRChatArchiveModPlugin.Logger.LogInfo("[LaunchpadConsole] " + Status);
		}

		public override void OnUpdate()
		{
			try
			{
				// OFF RESTORES EVEN WHEN OUR PANEL IS ALREADY GONE (2026-09-13). The test used to be
				// `_panel != null`, but the thing that most needs undoing is VRChat's own banner: Drop()
				// re-shows _carousel and puts its CanvasGroup alpha back to 1. A rebuilt page leaves
				// _panel null while _carousel is still hidden, and OFF then skipped the teardown
				// entirely — the banner stayed invisible with the console switched off.
				if (!Active) { if (_panel != null || _carousel != null) Drop(); return; }

				// Independent of the panel: the heading is worth claiming even on a tick where the
				// console itself could not be built, and it costs one string compare.
				RenameThePage();

				if (_panel == null || !_panel.Alive)
				{
					_panel = null;
					float now = VaClock.Now;
					if (now < _nextTry) return;
					_nextTry = now + 3f;
					if (_fails > 20) return;
					if (!TryBuild()) { _fails++; return; }
					_fails = 0;
					_shown = -1;
				}

				// Track the slot: the Launch Pad scrolls, and the panel is no longer a child of it.
				// Also the honest way to hide: when that page is not on screen its rect is not either,
				// so the console goes with it instead of floating over whatever page you opened.
				bool placed = PanelSkin.PlaceOver(_panel, _slot);
				if (_panel.Root != null && _panel.Root.gameObject.activeSelf != placed)
					_panel.Root.gameObject.SetActive(placed);
				if (!placed) return;

				// Redraw on the feed's VERSION, which changes when a line arrives and when the user
				// switches feeds. A count would miss the switch entirely, and would also rebuild for a
				// line added to the feed nobody is currently looking at.
				int n = 0;
				try { n = Core.ArchiveFeed.Version; } catch { }
				if (n == _shown) return;
				_shown = n;
				Refresh();
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// The menu survives a world change, but the page can be rebuilt under us.
			//
			// RESTORE BEFORE DROPPING THE HANDLES (2026-09-13). This used to null _carousel outright.
			// Since the menu SURVIVES the load — as the line above says — VRChat's banner was still
			// sitting there hidden at alpha 0, and the only handle that could bring it back had just
			// been thrown away: the Launch Pad banner stayed blank for the rest of the session. Drop()
			// restores it and is safe on already-destroyed objects, so it is right either way.
			if (_panel == null || !_panel.Alive)
			{
				if (_carousel != null || _panel != null) Drop();
				_panel = null; _carousel = null; _fails = 0; _nextTry = 0f;
			}
			_shown = -1;
		}

		public override void OnShutdown() { Drop(); }

		private void Drop()
		{
			// Order matters: give VRChat its banner back BEFORE our panel goes, so the layout never
			// has a frame with neither in it.
			try
			{
				if (_carousel != null)
				{
					_carousel.gameObject.SetActive(true);
					var cg = _carousel.GetComponent<CanvasGroup>();
					if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; cg.interactable = true; }
				}
			}
			catch { }
			_carousel = null;
			try { if (_panel != null && _panel.Root != null) UnityEngine.Object.Destroy(_panel.Root.gameObject); } catch { }
			_panel = null;
			_shown = -1;
		}

		// ---------------------------------------------------------------- build

		private bool TryBuild()
		{
			Transform root = MenuDonor.LaunchpadContent();
			if (root == null) return false;

			Transform carousel = root.Find(CarouselName);
			if (carousel == null) return false;

			// UNDER WINDOW, NOT IN THE PAGE. See PanelSkin.BuildOver: parented into VRChat's own
			// ScrollRect this panel's rows never rendered, while the same code under Window renders
			// fine for both side panels. It is placed over the carousel's rect each tick instead.
			Transform host = MenuDonor.Window();
			if (host == null) return false;

			// FOUR COLUMNS, NOT ONE. The single-column mode is the one thing this console did that the
			// two side panels never do, and it is the only difference left standing after the mask,
			// the shared material, the nested canvas and the layout group were each ruled out — its
			// rows drew nothing, not even a placeholder that could not be empty. The four-column path
			// is proven twice over on this exact build, so the console uses it and simply leaves the
			// columns it does not need empty.
			// Wider first and last columns than the side panels use: theirs hold "[12]" and a platform
			// badge, this one holds a full clock and a word. At the default 50/96 both were clipped
			// mid-character — "23:3..." and "ARCH" — which reads as a bug rather than as truncation.
			var p = PanelSkin.BuildOver(host, PanelName, "EVENTS", null, RowPitch, false,
				colIdW: 86f, colBadgeW: 118f);
			if (p == null) return false;

			// The slot used to carry VRChat's branding, so ours goes there in its place rather than
			// leaving an unlabelled box in the middle of the menu.
			PanelSkin.SetBrand(p, "kawaii_logo.png", "VRCHAT ARCHIVE MOD", "by Kawaii Studio");
			PanelSkin.SetHeadings(p, "TIME", "WHAT THE ARCHIVE IS DOING", null, "KIND");

			_slot = carousel.GetComponent<RectTransform>();
			if (!PanelSkin.PlaceOver(p, _slot))
			{
				// The page is not on screen yet; keep the panel and let OnUpdate place it.
				VRChatArchiveModPlugin.Logger.LogInfo("[LaunchpadConsole] built, waiting for the Launch Pad to be shown.");
			}
			// HIDDEN, BUT STILL THERE. Deactivating the banner was the obvious move and it silently
			// removed the console: the panel is placed over the carousel's RECT, and PlaceOver refuses
			// an inactive target — so the thing being measured was the thing being switched off, and
			// the console hid itself on every tick.
			//
			// Fading it instead keeps its rect alive and, better, keeps its SPACE in the layout: the
			// tiles below do not shift at all, which deactivating it would also have caused. Nothing
			// is destroyed either way — VRChat rebuilds this page from its own prefab.
			try
			{
				var cg = carousel.GetComponent<CanvasGroup>() ?? carousel.gameObject.AddComponent<CanvasGroup>();
				cg.alpha = 0f;
				cg.blocksRaycasts = false;   // it is invisible; it must not eat clicks meant for us
				cg.interactable = false;
			}
			catch { try { carousel.gameObject.SetActive(false); } catch { } }

			_carousel = carousel;
			_panel = p;
			VRChatArchiveModPlugin.Logger.LogInfo("[LaunchpadConsole] console placed over the banner slot (capacity "
				+ p.Capacity + ").");
			return true;
		}

		// THE PAGE TITLE. VRChat calls this page "Launch Pad"; with our console sitting in it and our
		// name on that console, the heading may as well say what the page is now for.
		//
		// RE-ASSERTED, not written once. The label carries a StyleElement and VRChat rewrites it from
		// its localisation table whenever the page is shown or restyled — a single write survives
		// until the next tab change. Twice a second is enough to look permanent and costs one string
		// compare; the write itself only happens when the text is not already ours.
		private const string PageTitle = "It's Time To Archive";

		private void RenameThePage()
		{
			try
			{
				float now = VaClock.Now;
				if (now < _nextTitle) return;
				_nextTitle = now + 0.5f;

				if (_pageTitle == null)
				{
					Transform t = MenuDonor.LaunchpadTitle();
					if (t == null) return;
					_pageTitle = t.GetComponent<TMPro.TMP_Text>();
					if (_pageTitle == null) return;
					VRChatArchiveModPlugin.Logger.LogInfo("[LaunchpadConsole] page title claimed ('"
						+ _pageTitle.text + "' -> '" + PageTitle + "').");
				}

				if (_pageTitle.text != PageTitle) _pageTitle.text = PageTitle;
			}
			catch { _pageTitle = null; }
		}

		// Pads to a fixed cell width, and TRUNCATES past it — without the cut a long object name
		// pushes everything after it out of alignment for that row only, which is worse than a name
		// ending in a dot.
		private static string Pad(string s, int width)
		{
			s = s ?? "";
			if (s.Length > width - 1) s = s.Substring(0, width - 2) + "…";
			return s.PadRight(width);
		}

		// ---------------------------------------------------------------- rows

		private void Refresh()
		{
			if (_panel == null) return;

			// THE ARCHIVE'S OWN WORK, not VRChat's Udon traffic.
			//
			// The Udon feed was what this slot could show before the client could talk to it; it is
			// noise to everyone except someone debugging a world. What people actually want while
			// playing is what the mod is FOR — the avatars the AUTO ARCHIVER is uploading and the ones
			// the CACHE VIEWER is recording. Both happen in the desktop client, on a screen nobody is
			// looking at in VR, and both now arrive here (Core/ArchiveFeed).
			IReadOnlyList<Core.ArchiveFeed.Entry> feed;
			try { feed = Core.ArchiveFeed.Current; } catch { return; }

			int cap = _panel.Capacity;
			int want = Mathf.Min(feed.Count, cap);
			int from = Mathf.Max(0, feed.Count - want);

			var rows = new List<PanelSkin.Row>(want);
			for (int i = 0; i < want; i++)
			{
				var e = feed[from + i];
				bool cache = e.Kind == Core.ArchiveFeed.Kind.Cache;
				rows.Add(new PanelSkin.Row
				{
					// Tagged, always: MenuThemeModule rewrites every TMP colour under this canvas
					// several times a second, and a rich-text tag is the one it cannot take.
					Id = PanelSkin.Tag("C79BFA", e.Clock ?? ""),
					// Pink rather than near-white: this is the Archive's own console, and the white was
					// VRChat's text colour showing through on a panel that is meant to look like ours.
					Name = PanelSkin.Tag(PanelSkin.HexPink, e.Text ?? ""),
					Pos = "",
					Badge = cache ? PanelSkin.Tag("4DE3FF", "CACHE") : PanelSkin.Tag("8FEEB2", "ARCHIVED"),
				});
			}

			if (rows.Count == 0)
			{
				// An empty console is indistinguishable from a broken one, and this one has a history
				// of looking broken. It says which feed it is on and that it is merely waiting.
				rows.Add(new PanelSkin.Row
				{
					// Pink, not the dim grey used for column labels: this line is the console telling you
					// it is alive and idle, which is worth reading — the grey made it look like a
					// disabled heading, i.e. exactly like the broken console it spent all evening
					// impersonating.
					Id = PanelSkin.Tag(PanelSkin.HexPink, "--:--:--"),
					Name = PanelSkin.Tag(PanelSkin.HexPink,
						"waiting for " + Core.ArchiveFeed.CurrentTitle.ToLowerInvariant() + " — nothing yet this session"),
					Pos = "",
					Badge = "",
				});
			}

			_panel.SetRows(rows);
			_panel.SetCount(PanelSkin.Tag("3BFF7A", "●") + " "
				+ PanelSkin.Tag(PanelSkin.HexDim, Core.ArchiveFeed.CurrentTitle) + "   "
				// The RUNNING TOTAL, not the length of the 200-entry ring the panel scrolls. The ring
				// saturates within minutes of a busy session and then reads 200 for ever, which is
				// indistinguishable from a console that has stopped receiving anything.
				+ PanelSkin.Tag(PanelSkin.HexText, Core.ArchiveFeed.CurrentTotal.ToString("N0")));
		}
	}
}
