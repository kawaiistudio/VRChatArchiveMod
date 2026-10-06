using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ARCHIVE FAVORITES for WORLDS and SOCIAL — rendered as OUR OWN grid.
	//
	// The whole point, learned the hard way: injecting ids into VRChat's FavoriteArea does NOT
	// render. The worlds and social pages are Voyager pages that rebuild their grid from a server
	// fetch on selection, so a FavoriteListModel we fill lands in the data (the log even said
	// "filled with 54 member(s)") and the screen still shows (0). And there is nothing to hijack the
	// way the avatar tab is hijacked: the interop has an AvatarContentSection with an observable
	// IList setter, but there is NO WorldContentSection / UserContentSection and no IWorld / IUser
	// to feed one. Confirmed against the on-disk 2026 interop.
	//
	// So this takes the route that has never failed us and is what FavCat actually does: build our
	// own grid and graft it in. A cloned sidebar row ("ARCHIVE FAVORITES") toggles an opaque overlay
	// we own, laid over the menu's content column, filled with cells we build from scratch (a
	// thumbnail + a name) — the exact from-scratch approach that finally made the QuickMenu sliders
	// render. No VRChat cell is cloned, so no obfuscated per-cell component can null our image back
	// out, and the thumbnails come from the Archive bridge (which even has images for private or
	// deleted content that VRChat's live fetch would 404 on).
	//
	// Paths come from a live capture (captures/menu_2026-08-25_15-40-43.txt), not guessed:
	//   WORLDS  Menu_MM_Worlds(Clone)
	//     ScrollRect_Navigation .../ Playlists / Button_MM_WorldPlaylist(Clone)   <- sidebar donor
	//     ScrollRect_Content    .../ Content_WorldCategory / CellGrid_MM_Content   <- content column
	//   SOCIAL  Menu_Social(Clone)
	//     ScrollRect_Navigation .../ Cell_MM_SidebarListItem_*                     <- sidebar donor
	//     ScrollRect_Content    .../ (grouped cells)                               <- content column
	// Both donor rows share the children we drive: Mask/Text_Name and Background_SelectedState.
	public class ArchiveFavGridModule : IModule
	{
		public override string Name => "ArchiveFavGrid";

		private readonly Pane _worlds;
		private readonly Pane _users;
		private readonly Pane _avatars;

		public static string WorldStatus = "idle";
		public static string UserStatus = "idle";
		public static string AvatarStatus = "idle";

		private static string _lastPickedId;
		private static float _lastPickTime;

		public ArchiveFavGridModule()
		{
			_worlds = new Pane
			{
				Kind = "world",
				MenuContains = "Menu_MM_Worlds",
				DonorPrefix = "Button_MM_WorldPlaylist",
				Title = "ARCHIVE FAVORITES",
				IdSource = () => WorldFavoritesModule.Snapshot(),
				RevSource = () => WorldFavoritesModule.Revision,
				Enabled = () => false,  // OFF: worlds are the desktop client's FAVORIS page (owner, 2026-08-28)
				CellSize = new Vector2(288f, 250f),   // matches the native Cell_MM_World
				ThumbAspect = 0.62f,   // 16:10-ish world card (from-scratch fallback only)
				Columns = 4,
				CellDonorName = "Cell_MM_World",     // confirmed on screen: renders exactly like VRChat's
			};
			_users = new Pane
			{
				Kind = "user",
				MenuContains = "Menu_Social",
				DonorPrefix = "Cell_MM_SidebarListItem",
				Title = "ARCHIVE FAVORITES",
				IdSource = () => UserFavoritesModule.Snapshot(),
				RevSource = () => UserFavoritesModule.Revision,
				Enabled = () => false,  // OFF: users are the desktop client's FAVORIS page (owner, 2026-08-28)
				CellSize = new Vector2(288f, 250f),
				ThumbAspect = 0.72f,   // friend-card proportions: portrait over a name + status line
				Columns = 4,
				// The WORLD card, reused for users — and it renders correctly on screen (portrait,
				// name, native styling), so it stays until a capture of the friends grid names the
				// real user cell. The blank grey boxes blamed on it were thumbnails that had not
				// downloaded yet, not the wrong donor. MenuCaptureModule records the friends page
				// now, and the real cell name goes here once that capture exists.
				CellDonorName = "Cell_MM_World",
			};
			// AVATARS — the one kind the MOD keeps (wearing is something only the game can do). Its own
			// grid, laid over the avatars page's content column, filled from the 34 Archive favourites
			// the bridge already fetched. Clicking a card WEARS that avatar (VaTagsModule.WearById), the
			// same proven path the Save-to-Archive button uses. No obfuscated VRChat type is touched:
			// the category row is renamed by its TEXT and the grid is built from scratch, so this cannot
			// rot the way ArchiveHijackModule did when the panel type was reassigned on 1903.
			_avatars = new Pane
			{
				Kind = "avatar",
				MenuContains = "Menu_MM_Avatars",
				// FROM THE LIVE 1903 DUMP, not from the 1886 capture.
				//
				// "Cell_MM_AvatarListSelector" looked right in the old capture and is the WRONG row: on
				// this build it exists only as an INACTIVE template sitting directly under the menu root,
				// so cloning it parented our row next to the templates instead of into the visible list --
				// the row existed, the log said so, and nothing appeared on screen. The eleven live
				// category rows are "Cell_MM_SidebarListItem prefab(Clone)" inside "Avatars Container".
				DonorPrefix = "Cell_MM_SidebarListItem",
				Title = "VRCX FAVORITES",
				ReplaceLabel = null,
				// Prefilled entries: FavoritesModule already holds id + name + author + image, so the bridge meta
				// pump the world/user panes need is skipped entirely.
				DirectEntries = () =>
				{
					var outp = new List<Entry>();
					try
					{
						foreach (var f in FavoritesModule.Favourites())
						{
							outp.Add(new Entry
							{
								Id = f.Id,
								Name = string.IsNullOrEmpty(f.Name) ? f.Id : f.Name,
								Author = f.Author ?? "",
								Image = f.Image ?? "",
								MetaDone = true
							});
						}
					}
					catch { }
					return outp;
				},
				RevSource = () => FavoritesModule.Revision,
				// Selection updates the right-side preview pane and Apply button. Double-clicking wears immediately.
				OnPick = e =>
				{
					try
					{
						float now = VaClock.Now;
						bool isDoubleClick = (string.Equals(_lastPickedId, e.Id, StringComparison.Ordinal) && (now - _lastPickTime < 0.45f));
						_lastPickedId = e.Id;
						_lastPickTime = now;

						_avatars?.SetCardSelected(e);
						ArchiveFavButtonModule.SelectAvatar(e.Id, e.Name, e.Author, e.Image);

						if (isDoubleClick)
						{
							VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid] double click -> WearById " + e.Id);
							VaTagsModule.WearById(e.Id, e.Name);
						}
					}
					catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid] preview : " + ex.Message); }
				},
				// A SWITCH, because this pane clones a row into VRChat's own sidebar layout. The first
				// attempt collapsed that sidebar to a strip of icons, and nobody should have to wait for
				// a rebuild from me to get their avatar menu back.
				Enabled = () => { try { return ModConfig.ArchiveFavAvatarGrid == null || ModConfig.ArchiveFavAvatarGrid.Value; } catch { return true; } },
				// VRCHAT'S OWN CARD, VRCHAT'S OWN GRID. Measured from the live tree on 1903:
				//   Panel_MM_Avatars/Panel_MM_ScrollRect/Viewport/VerticalLayoutGroup
				//     Cell_MM_AvatarListView
				//       CellGrid_MM_3Column          <GridLayoutGroup, ContentSizeFitter>
				//         Cell_MM_MarketplaceAvatar(Clone)   {288x297}
				//           Detail       -- name + author
				//           Image_Mask   -- thumbnail
				// So: three columns and 288x297, not four columns of hand-drawn 240x260. The owner
				// asked for "exactly VRChat's system" and was right to: cards built from scratch can
				// only ever approximate the game's, and the approximation is what looked wrong.
				CellSize = new Vector2(288f, 297f),
				ThumbAspect = 0.55f,
				Columns = 3,
				// VerticalLayoutGroup is the real content column: Panel_MM_ScrollRect is the scroll
				// view itself, two levels too high, so the grid was laid over the viewport instead of
				// inside it and did not scroll with the page.
				ContentNames = new[] { "ScrollRect_Content", "Contents", "VerticalLayoutGroup", "Panel_MM_ScrollRect" },
				// The card VRChat uses for EVERY avatar grid on this build, the shop included; it is
				// the only 288x297 cell in the menu and the one the stock lists are made of.
				CellDonorName = "Cell_MM_MarketplaceAvatar",
				ListViewDonor = "Cell_MM_AvatarListView",
			};
		}

		public override void OnUpdate()
		{
			_worlds.Tick(); WorldStatus = _worlds.Status;
			_users.Tick();  UserStatus = _users.Status;
			_avatars.Tick(); AvatarStatus = _avatars.Status;
		}

		public override void OnSceneLoaded(int buildIndex) { _worlds.OnScene(); _users.OnScene(); _avatars.OnScene(); }
		public override void OnShutdown() { _worlds.Teardown(); _users.Teardown(); _avatars.Teardown(); }

		// ================================================================= entry (one favourite)
		internal sealed class Entry
		{
			public string Id;
			public string Name = "";
			public string Image = "";        // vrchat.cloud image url from the OG preview
			public bool MetaDone;
			public int ThumbState;           // 0 idle, 1 fetching, 2 ready, 3 failed
			public Texture2D Thumb;

			// live cell
			public Transform Cell;
			public RawImage ThumbImg;
			public Image ThumbBg;
			public TMPro.TMP_Text NameTmp;
			// The card's second line. A cloned native card arrives carrying the DONOR's author,
			// so this is always written -- blanked when we have no author -- or the grid shows
			// 34 avatars all credited to whoever happened to be the template.
			public TMPro.TMP_Text SubTmp;
			// The card's aspect driver. VRChat sets its ratio when IT loads a thumbnail; we destroyed
			// the component that did that, so it has to be set from our own texture instead.
			public AspectRatioFitter ThumbFit;
			public string Author = "";
			public string ShownName = null;  // last text pushed to NameTmp (main-thread sync)
		}

		// ================================================================= one menu (worlds or social)
		internal sealed class Pane
		{
			public string Kind, MenuContains, DonorPrefix, Title;
			// Name of VRChat's own card to clone for this pane's grid. null = build from scratch.
			public string CellDonorName;

			// CLONE VRCHAT'S OWN LIST INSTEAD OF LAYING AN OVERLAY OVER IT.
			//
			// An overlay is a panel of ours -- our header, our scroll view, our Close button --
			// dropped on top of the page. It was never what was asked for, and it could not be made
			// right by styling: the owner said three times that the section had to BE VRChat's, and
			// an imitation is the one thing it can never be. Parenting it into the page's content
			// column made it worse still -- that column is a VerticalLayoutGroup, which drives the
			// size of everything inside it, so the overlay was squeezed to nothing and only its
			// header survived on screen.
			//
			// Named here, this clones the list view the page already uses for avatars1 / avatars2 --
			// its grid, its scrolling, its spacing, its header -- empties it, and fills it with our
			// favourites. Nothing of ours is drawn: the section IS one of VRChat's lists.
			public string ListViewDonor;
			public Func<List<string>> IdSource;
			// AVATARS supply their entries directly (id+name+image already in hand); worlds/users
			// give an IdSource and let the bridge fill the rest.
			public Func<List<Entry>> DirectEntries;
			// Clicked-card action. Worlds/users have none (view only); avatars wear the avatar.
			public Action<Entry> OnPick;
			// Content-column names to try in order; the avatars page does not use ScrollRect_Content.
			public string[] ContentNames;
			// TAKE OVER the row whose label reads this, instead of adding a row of our own.
			// Null = clone a new row (worlds/users still do that).
			public string ReplaceLabel;
			public Func<int> RevSource;
			public Func<bool> Enabled;
			public Vector2 CellSize;
			public float ThumbAspect;
			public int Columns;

			public string Status = "idle";

			private Transform _root;            // our submenu (Menu_MM_Worlds / Menu_Social), cached
			private Transform _row, _overlay, _gridContent;
			private TMPro.TMP_Text _rowCount, _headerCount;
			private bool _shown;
			private int _lastRev = int.MinValue;
			private static TMPro.TMP_FontAsset _font;
			private bool _loggedGrid;
			private bool _contentDumped;
			private bool _donorDumped;

			// The native card to clone, PER PANE. It used to be one static shared by both panes, so
			// whichever page was visited first won — and social ended up cloning a WORLD card, which
			// is why its grid came out as blank grey boxes. Each pane now resolves its own donor,
			// scoped to its own page, and a pane with no donor name draws from-scratch cards.
			private Transform _cellDonor;
			private float _donorNext;

			// What VRChat's own content column looked like when we took the screen over. If it
			// changes, VRChat has switched to one of its own sections and ours must get out of the
			// way — see WatchStockSelection.
			private string _contentSig;

			private readonly List<Transform> _stockRows = new List<Transform>();
			private readonly HashSet<int> _baselineSelected = new HashSet<int>();
			private readonly List<Entry> _entries = new List<Entry>();
			private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
			private readonly ConcurrentQueue<(Entry e, byte[] data)> _decoded = new ConcurrentQueue<(Entry, byte[])>();
			private int _inflight;
			private float _nextMeta;
			private bool _metaBusy;

			private const int MaxInflight = 4;

			private static bool Dead(UnityEngine.Object o) => o == null;

			// ----------------------------------------------------------------- lifecycle
			public void OnScene()
			{
				_root = null;
				_row = null; _overlay = null; _gridContent = null; _rowCount = null; _headerCount = null;
				_shown = false; _lastRev = int.MinValue; _font = null; _loggedGrid = false;
				_cellDonor = null; _donorNext = 0f; _contentSig = null;
				_stockRows.Clear(); _baselineSelected.Clear();
				// Cells belonged to the destroyed menu; drop their references so a reopen rebuilds them.
				foreach (var e in _entries) { e.Cell = null; e.ThumbImg = null; e.ThumbBg = null; e.NameTmp = null; e.SubTmp = null; e.ThumbFit = null; e.ShownName = null; }
			}

			public void SetCardSelected(Entry e)
			{
				try
				{
					foreach (var other in _entries)
					{
						if (Dead(other.Cell)) continue;
						var sel = other.Cell.Find("Detail/SelectedState");
						if (!Dead(sel)) sel.gameObject.SetActive(other == e);
					}
				}
				catch { }
			}

			public void Teardown()
			{
				try { if (!Dead(_overlay)) UnityEngine.Object.Destroy(_overlay.gameObject); } catch { }
				try { if (!Dead(_row)) UnityEngine.Object.Destroy(_row.gameObject); } catch { }
				OnScene();
				Status = "off";
			}

			// Rate-limit stamps for the subtree searches above.
			private float _nextRootScan, _nextBuildScan;

			public void Tick()
			{
				try
				{
					if (Enabled == null || !Enabled())
					{
						if (!Dead(_overlay) || !Dead(_row)) Teardown();
						return;
					}

					// CHEAP GATE FIRST. The old code called Resources.FindObjectsOfTypeAll<Transform>()
					// every frame for BOTH panes — that walk over every loaded Transform is what pinned
					// the game at ~5 FPS with the menu open. Core.QuickMenu caches the main-menu canvas
					// and exposes a one-bool "is it on screen" check, so when the menu is closed this
					// returns immediately at zero cost.
					if (!Core.QuickMenu.MainVisible) return;

					// Resolve our submenu ONCE, by walking only the cached main-menu subtree (a few
					// thousand nodes, not the whole scene), and keep the reference. The clone persists
					// across open/close, so this search runs once per world, not once per frame.
					if (Dead(_root))
					{
						// A MISS IS RATE-LIMITED, exactly like Core.QuickMenu does for the canvases.
						//
						// The comment above is right that this runs "once per world, not once per
						// frame" — but only when it SUCCEEDS. When our submenu is not there, and on
						// this build it often is not, FindInSubtree walked the whole main-menu
						// subtree, returned null, and did it again next frame. Two panes, so twice
						// per frame. The profiler measured this module at 458 ms per second: the
						// single most expensive thing in the mod, and all of it spent failing.
						float now = VaClock.Now;
						if (now < _nextRootScan) return;
						_nextRootScan = now + 1f;

						var mm = Core.QuickMenu.Main();
						if (Dead(mm)) return;
						_root = FindInSubtree(mm, MenuContains);
						if (Dead(_root)) return;
					}
					Transform root = _root;
					if (!IsActive(root)) return;

					// The preview needs the avatars menu, and we are already standing in it. Handing it
					// over beats letting it guess which canvas QuickMenu.Main() returns -- a wrong guess
					// there fails silently, which is how "clicking does nothing" got its first diagnosis.
					// The preview probe needs the avatars menu, and we are standing in it. Handing it over
					// beats letting it guess which canvas QuickMenu.Main() returns -- that reaches the
					// WRIST menu, a different canvas, and the miss is silent.
					if (!string.IsNullOrEmpty(ListViewDonor))
					{
						Core.AvatarPreview.MenuRoot = root;
						try { Core.AvatarPreview.Discover(); } catch { }
						try { Core.AvatarPreview.Probe(); } catch { }
					}

					// SAME TREATMENT. Each of these searches the subtree for a donor, and each one
					// retried every frame for as long as it failed to find it — the same unbounded
					// scan as above, three more times.
					if (Dead(_font) || Dead(_row) || Dead(_overlay))
					{
						float now = VaClock.Now;
						if (now < _nextBuildScan) return;
						_nextBuildScan = now + 1f;

						if (Dead(_font)) StealFont(root);
						if (Dead(_row)) BuildRow(root);
						if (Dead(_overlay)) BuildOverlay(root);
					}

					// Keep data current from the id source (RevSource is a cheap int compare).
					int rev = RevSource != null ? RevSource() : 0;
					if (rev != _lastRev) { _lastRev = rev; RebuildData(); if (_shown) Repopulate(); }

					UpdateCounts();

					// THE PAGE HEADER TOO, and on a cadence rather than once: renaming only the sidebar row
					// left the big heading over the grid still reading "SDK Test Avatars", so the row said one
					// thing and the page you landed on said another. VRChat rewrites that heading every time
					// the category is selected, so a single rename is undone the moment you click it.
					if (!string.IsNullOrEmpty(ReplaceLabel)) RetitleHeader(root);

					// Decode any thumbnails that arrived (main-thread only), even when hidden, so a
					// reopen is instant.
					DrainDecoded(2);

					// VRCHAT OWNS THE SELECTION, SO IT OWNS THE VISIBILITY.
					//
					// Our list is a permanent neighbour in the content column, so it stayed on screen
					// while avatars1 was showing -- the page listed VRChat's avatars and the Archive's at
					// the same time. It only ever hid when OUR row was clicked a second time, which is
					// not how a category works.
					//
					// The row we took over is one of VRChat's own, so the game lights and unlights it
					// exactly as it does the others. Reading that is both simpler and more correct than
					// tracking clicks ourselves: whatever VRChat considers selected, we follow.
					FollowSelection();

					// The heavy per-frame work only matters while OUR page is the one on screen.
					bool pageActive = IsActive(root);
					if (_shown && pageActive)
					{
						WatchStockSelection();
						PumpMetaThrottled();
						RequestThumbs();
						SyncCells();
					}
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] " + e.Message); }
			}

			// ----------------------------------------------------------------- sidebar row
			private void BuildRow(Transform root)
			{
				// A DONOR IS A ROW THAT CARRIES A LABEL, WHEREVER THE LABEL SITS.
				//
				// This used to demand a child at exactly "Mask/Text_Name" -- the worlds/social row
				// anatomy. The avatars category row (Cell_MM_AvatarListSelector) keeps its label under
				// Layout_CategoryButtonContents/Container_Text instead, so no donor was ever accepted,
				// the status sat on "waiting for the sidebar" without a word, and the ARCHIVE FAVORITES
				// row never existed for anyone to click: the grid was built and unreachable. The only
				// thing a donor must have is a TMP_Text somewhere beneath it, which every VRChat row does.
				Transform donor = FindDescendantByPrefix(root, DonorPrefix, requireChild: null, requireText: true);
				if (Dead(donor))
				{
					Status = "waiting for the sidebar";
					if (!_donorDumped)
					{
						_donorDumped = true;
						try
						{
							var cands = new List<string>();
							foreach (var tr in root.GetComponentsInChildren<Transform>(true))
							{
								if (Dead(tr)) continue;
								string n2; try { n2 = tr.name; } catch { continue; }
								if ((n2.StartsWith("Cell_", StringComparison.Ordinal) || n2.StartsWith("Button_", StringComparison.Ordinal))
									&& !cands.Contains(n2) && cands.Count < 25) cands.Add(n2);
							}
							VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] aucune ligne '" + DonorPrefix + "' a cloner sous " + MenuContains
								+ ". Lignes presentes : " + (cands.Count == 0 ? "(aucune)" : string.Join(" | ", cands)));
						}
						catch { }
					}
					return;
				}
				Transform container = donor.parent;
				if (Dead(container)) { Status = "sidebar has no container"; return; }

				string rowName = "VA_FavGridRow_" + Kind;

				// Restore any row previously renamed from takeover back to SDK Test Avatars
				try
				{
					var oldRenamed = FindRowByLabel(root, "ARCHIVE FAVORITES");
					if (!Dead(oldRenamed) && oldRenamed.name != rowName)
					{
						var tx = oldRenamed.GetComponentInChildren<TMPro.TMP_Text>(true);
						if (!Dead(tx)) tx.text = "SDK Test Avatars";
					}
				}
				catch { }

				// ---- TAKEOVER: adopt VRChat own row if ReplaceLabel is specified -------------------
				if (!string.IsNullOrEmpty(ReplaceLabel))
				{
					Transform mine = FindRowByLabel(root, Title) ?? FindRowByLabel(root, ReplaceLabel);
					if (Dead(mine)) { Status = "waiting for the " + ReplaceLabel + " row"; return; }
					DropClones(root, rowName);
					bool first = Dead(_row);
					AdoptRow(mine, root);
					if (first)
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] section " + ReplaceLabel
							+ " reprise et renommee en " + Title + " — aucune ligne ajoutee.");
					return;
				}

				var existing = container.Find(rowName);
				if (!Dead(existing)) { AdoptRow(existing, root); return; }

				// SWEEP UP A ROW AN EARLIER BUILD MISPLACED.
				try
				{
					foreach (var stale in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(stale) || ReferenceEquals(stale.parent, container)) continue;
						string sn; try { sn = stale.name; } catch { continue; }
						if (sn != rowName) continue;
						UnityEngine.Object.Destroy(stale.gameObject);
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] ancienne ligne mal parentee supprimee.");
					}
				}
				catch { }

				float donorH = 0f, donorW = 0f;
				try
				{
					var dr = donor.GetComponent<RectTransform>() ?? donor.TryCast<RectTransform>();
					if (dr != null) { donorH = dr.rect.height; donorW = dr.rect.width; }
				}
				catch { }

				var go = UnityEngine.Object.Instantiate(donor.gameObject, container);
				go.name = rowName;
				var t = go.transform;
				t.SetAsLastSibling(); // Placed at the bottom of the category container

				try { MenuCard.StripRoot(t, keepStyle: true); } catch { }

				try
				{
					if (donorH > 1f)
					{
						var le = t.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
						if (!Dead(le))
						{
							le.minHeight = donorH;
							le.preferredHeight = donorH;
							if (donorW > 1f) le.preferredWidth = donorW;
							le.ignoreLayout = false;
						}
						var rt = t.GetComponent<RectTransform>() ?? t.TryCast<RectTransform>();
						if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, donorH);
					}
					else
					{
						VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind
							+ "] hauteur du donneur illisible — la ligne pourrait etre invisible.");
					}
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] taille de ligne : " + e.Message); }

				var g = t.GetComponent<Graphic>();
				if (Dead(g)) { var im = go.AddComponent<Image>(); im.color = new Color(0f, 0f, 0f, 0f); g = im; }
				g.raycastTarget = true;

				var btn = t.GetComponent<Button>();
				if (Dead(btn)) btn = go.AddComponent<Button>();
				btn.targetGraphic = g;
				btn.interactable = true;
				btn.transition = Selectable.Transition.None;
				try { btn.onClick.RemoveAllListeners(); } catch { }
				Core.UiClick.AddClick(btn, SelectOurCategory);

				try { t.Find("Background_SelectedState")?.gameObject.SetActive(false); } catch { }
				go.SetActive(true);

				AdoptRow(t, root);

				string where = "?";
				try
				{
					where = container.name;
					for (Transform up = container.parent; !Dead(up) && where.Length < 90; up = up.parent) where = up.name + "/" + where;
				}
				catch { }
				bool donorLive = false; try { donorLive = donor.gameObject.activeInHierarchy; } catch { }
				float myH = -1f, myW = -1f; bool myLive = false; int sib = -1, sibs = -1;
				try
				{
					var rt2 = t.GetComponent<RectTransform>() ?? t.TryCast<RectTransform>();
					if (rt2 != null) { myH = rt2.rect.height; myW = rt2.rect.width; }
					myLive = go.activeInHierarchy;
					sib = t.GetSiblingIndex();
					sibs = container.childCount;
				}
				catch { }
				VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] ligne " + Title + " ajoutee — donneur '"
					+ donor.name + "' (vivant=" + donorLive + ", " + donorW.ToString("0") + "x" + donorH.ToString("0") + ")"
					+ " -> notre ligne " + myW.ToString("0") + "x" + myH.ToString("0") + " visible=" + myLive
					+ " place " + sib + "/" + sibs + " dans " + where);
			}

			// The live row whose visible label reads exactly this. Text is what the user sees and what
			// cannot rot between builds, unlike an object name or a child path.
			private static Transform FindRowByLabel(Transform root, string label)
			{
				try
				{
					if (Dead(root) || string.IsNullOrEmpty(label)) return null;
					foreach (var tx in root.GetComponentsInChildren<TMPro.TMP_Text>(false))
					{
						if (Dead(tx)) continue;
						string v; try { v = (tx.text ?? "").Trim(); } catch { continue; }
						if (!string.Equals(v, label, StringComparison.Ordinal)) continue;
						// Walk up to the row itself: the label sits a couple of levels inside it.
						for (Transform up = tx.transform; !Dead(up); up = up.parent)
						{
							string n; try { n = up.name; } catch { break; }
							if (n.StartsWith("Cell_", StringComparison.Ordinal) || n.StartsWith("Button_", StringComparison.Ordinal))
								return up;
						}
					}
				}
				catch { }
				return null;
			}

			// Remove rows an earlier build added, now that the section is taken over instead.
			private static void DropClones(Transform root, string rowName)
			{
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (n == rowName) UnityEngine.Object.Destroy(t.gameObject);
					}
				}
				catch { }
			}

			private void AdoptRow(Transform t, Transform root)
			{
				_row = t;

				try
				{
					var btn = t.GetComponent<Button>();
					if (Dead(btn)) btn = t.gameObject.AddComponent<Button>();
					if (!Dead(btn))
					{
						btn.transition = Selectable.Transition.None;
						btn.interactable = true;
						var gr = t.GetComponent<Graphic>();
						if (!Dead(gr)) gr.raycastTarget = true;
						Core.UiClick.Forget(btn);
						Core.UiClick.AddClick(btn, SelectOurCategory);
					}
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] clic de ligne : " + e.Message); }
				try
				{
					var tn = t.Find("Mask/Text_Name");
					var tmp = Dead(tn) ? null : tn.GetComponent<TMPro.TMP_Text>();
					if (Dead(tmp)) tmp = t.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (!Dead(tmp)) tmp.text = Title;
				}
				catch { }

				_rowCount = FindText(t, "Mask/Text_Name") != null ? (FindText(t, "Count_BG/Text_Number") ?? FindText(t, "Count_BG_Alt/Text_Number") ?? CountBadge(t)) : null;
				try { t.Find("Count_BG")?.gameObject.SetActive(true); } catch { }
				try { t.Find("Text_Subtitle")?.gameObject.SetActive(false); } catch { }
				try { t.Find("Subtext/Text_Subtitle")?.gameObject.SetActive(false); } catch { }

				CollectStockRows(root);
				Status = "row shown; " + _entries.Count + " favourite(s)";
			}

			private void SelectOurCategory()
			{
				try
				{
					if (!Dead(_row))
					{
						var sel = _row.Find("Background_SelectedState");
						if (!Dead(sel)) sel.gameObject.SetActive(true);
					}

					foreach (var sr in _stockRows)
					{
						if (Dead(sr)) continue;
						try
						{
							var sel = sr.Find("Background_SelectedState");
							if (!Dead(sel)) sel.gameObject.SetActive(false);
						}
						catch { }
					}

					var head = PageHeading(_root);
					if (!Dead(head)) head.text = Title;

					Show();
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] SelectOurCategory: " + ex.Message);
				}
			}

			private void CollectStockRows(Transform root)
			{
				_stockRows.Clear();
				try
				{
					var all = root.GetComponentsInChildren<Transform>(true);
					foreach (var tr in all)
					{
						if (Dead(tr) || ReferenceEquals(tr, _row)) continue;
						string n; try { n = tr.name; } catch { continue; }
						if (n.StartsWith("VA_FavGridRow_", StringComparison.Ordinal)) continue;
						bool isRow = n.StartsWith("Cell_MM_SidebarListItem", StringComparison.Ordinal)
								   || n.StartsWith("Button_MM_WorldPlaylist", StringComparison.Ordinal)
								   || (!string.IsNullOrEmpty(DonorPrefix) && n.StartsWith(DonorPrefix, StringComparison.Ordinal));
						if (!isRow) continue;
						if (Dead(tr.Find("Background_SelectedState"))) continue;
						_stockRows.Add(tr);
					}
				}
				catch { }
			}

			// ----------------------------------------------------------------- overlay grid
			private void BuildOverlay(Transform root)
			{
				// The content column, by any of the names this menu might use. The avatars page nests
				// its grid under Panel_MM_ScrollRect / Contents rather than ScrollRect_Content, so one
				// hard-coded name found nothing there. A miss dumps the scroll/grid candidates once, so
				// the real container names on this build land in the log instead of being guessed.
				//
				// A DORMANT COLUMN DRAWS NOTHING. The avatars page carries four content panels stacked
				// in the same parent -- Panel_My_Current_Avatar, Panel_MM_AvatarLooks, Panel_MM_Accessories
				// and Panel_MM_Avatars -- of which VRChat leaves exactly ONE switched on. Taking the first
				// name match landed the grid inside Panel_MM_AvatarLooks, which is off: the cards were
				// built, the log said "populated 34 cell(s)", and the screen stayed empty. So: sweep every
				// candidate name for a LIVE container first, and only settle for a dormant one if the page
				// has nothing live at all (better a grid that appears late than no column found).
				Transform content = null;
				var names = ContentNames ?? new[] { "ScrollRect_Content" };
				for (int i = 0; i < names.Length && Dead(content); i++) content = FindDescendantByName(root, names[i], true);
				bool live = !Dead(content);
				for (int i = 0; i < names.Length && Dead(content); i++) content = FindDescendantByName(root, names[i], false);
				if (Dead(content))
				{
					Status = "no content column";
					if (!_contentDumped)
					{
						_contentDumped = true;
						try
						{
							var cands = new List<string>();
							foreach (var t in root.GetComponentsInChildren<Transform>(true))
							{
								if (Dead(t)) continue;
								string tn; try { tn = t.name; } catch { continue; }
								bool hit = tn.IndexOf("ScrollRect", StringComparison.Ordinal) >= 0 || tn.IndexOf("Content", StringComparison.Ordinal) >= 0
									|| tn.IndexOf("Grid", StringComparison.Ordinal) >= 0;
								if (hit && !cands.Contains(tn) && cands.Count < 25) cands.Add(tn);
							}
							VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] colonne de contenu introuvable sous " + MenuContains
								+ ". Candidats presents : " + (cands.Count == 0 ? "(aucun)" : string.Join(" | ", cands)));
						}
						catch { }
					}
					return;
				}

				// VRChat's own list, when this pane asks for it. Everything below -- the hand-built
				// overlay -- is the fallback for a pane with no list to borrow.
				if (!string.IsNullOrEmpty(ListViewDonor) && BuildNativeList(content, live)) return;

				string overlayName = "VA_FavGridOverlay_" + Kind;
				var existing = content.Find(overlayName);
				if (!Dead(existing)) { AdoptOverlay(existing); return; }

				// Overlay: opaque, fills the content column, sits ON TOP of (does not scroll with) the
				// stock cells. Direct child of ScrollRect_Content, so the stock viewport mask does not
				// clip it and the stock scroll does not move it.
				var overlay = NewRect(overlayName, content);
				Stretch(overlay);
				var obg = overlay.gameObject.AddComponent<Image>();
				obg.color = new Color(0.05f, 0.06f, 0.10f, 1f);
				obg.raycastTarget = true;

				// Header: title + count + close.
				var header = NewRect("Header", overlay);
				header.anchorMin = new Vector2(0f, 1f); header.anchorMax = new Vector2(1f, 1f);
				header.pivot = new Vector2(0.5f, 1f);
				header.sizeDelta = new Vector2(0f, 72f);
				header.anchoredPosition = Vector2.zero;
				var hbg = header.gameObject.AddComponent<Image>();
				hbg.color = new Color(0.09f, 0.20f, 0.23f, 1f);

				var htitle = NewRect("Title", header);
				htitle.anchorMin = new Vector2(0f, 0f); htitle.anchorMax = new Vector2(0.7f, 1f);
				htitle.offsetMin = new Vector2(28f, 0f); htitle.offsetMax = Vector2.zero;
				var titleTmp = Label(htitle, Title, 26f, TMPro.TextAlignmentOptions.MidlineLeft);
				titleTmp.color = new Color(0.86f, 0.66f, 1f);

				var hcount = NewRect("Count", header);
				hcount.anchorMin = new Vector2(0.7f, 0f); hcount.anchorMax = new Vector2(0.9f, 1f);
				hcount.offsetMin = Vector2.zero; hcount.offsetMax = Vector2.zero;
				_headerCount = Label(hcount, "", 20f, TMPro.TextAlignmentOptions.MidlineRight);

				var close = NewRect("Close", header);
				close.anchorMin = new Vector2(0.9f, 0.15f); close.anchorMax = new Vector2(1f, 0.85f);
				close.offsetMin = new Vector2(0f, 0f); close.offsetMax = new Vector2(-20f, 0f);
				var cbg = close.gameObject.AddComponent<Image>();
				cbg.color = new Color(0.86f, 0.24f, 0.44f, 0.9f);
				var cbtn = close.gameObject.AddComponent<Button>();
				cbtn.targetGraphic = cbg;
				cbtn.transition = Selectable.Transition.None;
				Core.UiClick.AddClick(cbtn, Hide);
				var ctext = NewRect("X", close);
				Stretch(ctext);
				Label(ctext, "✕  Close", 18f, TMPro.TextAlignmentOptions.Center);

				// Scroll area under the header.
				var scroll = NewRect("Scroll", overlay);
				scroll.anchorMin = new Vector2(0f, 0f); scroll.anchorMax = new Vector2(1f, 1f);
				scroll.offsetMin = new Vector2(0f, 0f); scroll.offsetMax = new Vector2(0f, -72f);
				var sr = scroll.gameObject.AddComponent<ScrollRect>();
				sr.horizontal = false; sr.vertical = true;
				sr.scrollSensitivity = 32f;
				sr.movementType = ScrollRect.MovementType.Clamped;

				var viewport = NewRect("Viewport", scroll);
				Stretch(viewport);
				viewport.gameObject.AddComponent<RectMask2D>();
				var vim = viewport.gameObject.AddComponent<Image>();
				vim.color = new Color(0f, 0f, 0f, 0.001f);   // must have a graphic to receive drag

				var gridContent = NewRect("Content", viewport);
				gridContent.anchorMin = new Vector2(0f, 1f); gridContent.anchorMax = new Vector2(1f, 1f);
				gridContent.pivot = new Vector2(0.5f, 1f);
				gridContent.anchoredPosition = Vector2.zero;
				var grid = gridContent.gameObject.AddComponent<GridLayoutGroup>();
				grid.cellSize = CellSize;
				grid.spacing = new Vector2(22f, 22f);
				grid.padding = new RectOffset(28, 28, 22, 28);
				grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
				grid.startAxis = GridLayoutGroup.Axis.Horizontal;
				grid.childAlignment = TextAnchor.UpperCenter;
				grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
				grid.constraintCount = Columns;
				var fitter = gridContent.gameObject.AddComponent<ContentSizeFitter>();
				fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
				fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

				sr.viewport = viewport;
				sr.content = gridContent;

				overlay.gameObject.SetActive(false);
				_overlay = overlay;
				_gridContent = gridContent;

				if (_shown) { _overlay.gameObject.SetActive(true); Repopulate(); }
				// NAMED, for the same reason the row log is: "built" is true even when it is built over
				// the wrong column, and only the container path tells those two apart.
				string col = "?";
				try
				{
					col = content.name;
					for (Transform up = content.parent; !Dead(up) && col.Length < 90; up = up.parent) col = up.name + "/" + col;
				}
				catch { }
				VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] grille posee sur la colonne " + col
					+ (live ? " [vivante]" : " [DORMANTE — aucune colonne active trouvee, la grille restera invisible]"));
			}

			private static readonly HashSet<string> CellKeep = new HashSet<string>(StringComparer.Ordinal)
			{
				"RectTransform", "Transform", "CanvasRenderer", "Canvas", "CanvasGroup", "GraphicRaycaster",
				"Image", "ImageEx", "RawImage", "RawImageEx",
				"LayoutElement", "HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup",
				"ContentSizeFitter", "AspectRatioFitter", "RectMask2D", "Mask",
				"UIInvisibleGraphic", "Button", "TextMeshProUGUI", "TMP_Text", "TextMeshPro", "Text", "TMP_SubMeshUI", "StyleElement"
			};

			private static void StripAllForeign(Transform t, int depth = 8)
			{
				if (Dead(t) || depth < 0) return;
				try
				{
					var comps = t.GetComponents<Component>();
					if (comps != null)
					{
						foreach (var c in comps)
						{
							if (c == null) continue;
							if (c.TryCast<TMPro.TMP_Text>() != null || c.TryCast<UnityEngine.UI.Graphic>() != null) continue;
							if (c.TryCast<UnityEngine.UI.Selectable>() != null) continue;
							if (c.TryCast<UnityEngine.UI.LayoutGroup>() != null) continue;
							if (c.TryCast<UnityEngine.UI.ILayoutElement>() != null) continue;
							string n = MenuCard.Il2CppNameOf(c);
							if (CellKeep.Contains(n)) continue;
							try { UnityEngine.Object.DestroyImmediate(c); } catch { }
						}
					}
				}
				catch { }

				for (int i = 0; i < t.childCount; i++)
				{
					try
					{
						var child = t.GetChild(i);
						if (!Dead(child)) StripAllForeign(child, depth - 1);
					}
					catch { }
				}
			}

			// ---- VRChat's own list view, borrowed -------------------------------------------------
			//
			// Returns false when there is nothing to borrow yet (the page has not drawn a list), so
			// the caller keeps trying rather than falling back to an overlay for the session.
			private bool BuildNativeList(Transform content, bool liveColumn)
			{
				try
				{
					string listName = "VA_FavList_" + Kind;

					// Already built: re-adopt it rather than cloning a second one.
					var mine = content.Find(listName);
					if (!Dead(mine))
					{
						_overlay = mine;
						_gridContent = GridIn(mine);
						return !Dead(_gridContent);
					}

					var donor = FindListDonor(_root) ?? FindListDonor(Core.QuickMenu.Main());
					if (Dead(donor)) { Status = "waiting for a stock avatar list to borrow"; return false; }

					var go = UnityEngine.Object.Instantiate(donor.gameObject, content);
					go.name = listName;
					var clone = go.transform;

					// Recursively strip all non-whitelist components so no donor binders survive.
					try { StripAllForeign(clone); } catch { }

					var grid = GridIn(clone);
					if (Dead(grid))
					{
						try { UnityEngine.Object.Destroy(go); } catch { }
						VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] la liste clonee ne contient pas de grille — overlay de secours.");
						return false;
					}
					try { MenuCard.StripRoot(grid, keepStyle: true); } catch { }

					try
					{
						var cv = clone.GetComponent<Canvas>() ?? clone.gameObject.AddComponent<Canvas>();
						cv.enabled = true;
						var gr = clone.GetComponent<GraphicRaycaster>() ?? clone.gameObject.AddComponent<GraphicRaycaster>();
						gr.enabled = true;

						var listCg = clone.GetComponent<CanvasGroup>() ?? clone.gameObject.AddComponent<CanvasGroup>();
						listCg.alpha = 1f;
						listCg.blocksRaycasts = true;
						listCg.interactable = true;

						var cvGrid = grid.GetComponent<Canvas>() ?? grid.gameObject.AddComponent<Canvas>();
						cvGrid.enabled = true;
						var grGrid = grid.GetComponent<GraphicRaycaster>() ?? grid.gameObject.AddComponent<GraphicRaycaster>();
						grGrid.enabled = true;
					}
					catch { }

					// The donor's cards came along with it.
					for (int i = grid.childCount - 1; i >= 0; i--)
					{
						try { UnityEngine.Object.DestroyImmediate(grid.GetChild(i).gameObject); } catch { }
					}

					// The heading still reads the borrowed list's name. The biggest label outside the
					// grid is it; a second one is where VRChat prints "9/50", which is where our count
					// belongs too.
					RetitleList(clone, grid);

					// BUILT HIDDEN. FollowSelection turns it on when the heading says our category is the
					// one showing, and never before -- otherwise a fresh clone appears inside whatever
					// category happened to be open when the menu was first reached.
					go.SetActive(false);
					_shown = false;
					_overlay = clone;
					_gridContent = grid;
					Unhide(clone);
					if (_shown) { go.SetActive(true); Repopulate(); }

					if (!_loggedOverlay)
					{
						_loggedOverlay = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] liste native '" + donor.name
							+ "' clonee dans " + content.name + " — grille=" + grid.name
							+ (liveColumn ? " [colonne vivante]" : " [COLONNE DORMANTE]") + ". Aucun overlay dessine.");
					}
					return true;
				}
				catch (Exception e)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] liste native : " + e.Message);
					return false;
				}
			}

			private bool _loggedOverlay;

			// A stock list view: one of VRChat's, holding a grid, not one of ours.
			private Transform FindListDonor(Transform scope)
			{
				try
				{
					if (Dead(scope)) return null;
					foreach (var t in scope.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (!n.StartsWith(ListViewDonor, StringComparison.Ordinal)) continue;
						if (n.StartsWith("VA_", StringComparison.Ordinal)) continue;
						if (IsOurs(t)) continue;
						if (Dead(GridIn(t))) continue;
						return t;
					}
				}
				catch { }
				return null;
			}

			// The grid inside a list view, found by its COMPONENT. CellGrid_MM_3Column is what this
			// build calls it; a build that renames it, or offers four columns instead of three, still
			// has exactly one GridLayoutGroup in there.
			private static Transform GridIn(Transform listView)
			{
				try
				{
					if (Dead(listView)) return null;
					foreach (var g in listView.GetComponentsInChildren<GridLayoutGroup>(true))
					{
						if (Dead(g)) continue;
						return g.transform;
					}
				}
				catch { }
				return null;
			}

			// Labels belonging to the list itself, not to a card inside it.
			private void RetitleList(Transform clone, Transform grid)
			{
				try
				{
					var outside = new List<TMPro.TMP_Text>();
					foreach (var tx in clone.GetComponentsInChildren<TMPro.TMP_Text>(true))
					{
						if (Dead(tx)) continue;
						bool inGrid = false;
						for (Transform up = tx.transform; !Dead(up); up = up.parent)
							if (ReferenceEquals(up, grid)) { inGrid = true; break; }
						if (!inGrid) outside.Add(tx);
					}
					outside.Sort((a, b) =>
					{
						float fa = 0f, fb = 0f;
						try { fa = a.fontSize; } catch { }
						try { fb = b.fontSize; } catch { }
						return fb.CompareTo(fa);
					});
					if (outside.Count > 0) { try { outside[0].text = Title; } catch { } }
					if (outside.Count > 1) _headerCount = outside[1];
				}
				catch { }
			}

			private void AdoptOverlay(Transform overlay)
			{
				_overlay = overlay;
				_gridContent = overlay.Find("Scroll/Viewport/Content");
				_headerCount = FindText(overlay, "Header/Count");
			}

			// ----------------------------------------------------------------- show / hide
			// The il2cpp→managed boundary: anything escaping here is not a normal exception, it is an
			// interop error that swallows the click entirely. Nothing gets out.
			private void Toggle()
			{
				try { if (_shown) Hide(); else SelectOurCategory(); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] toggle: " + e.Message); }
			}

			private void HideStockLists(bool hide)
			{
				try
				{
					if (Dead(_overlay) || Dead(_overlay.parent)) return;
					var parent = _overlay.parent;
					for (int i = 0; i < parent.childCount; i++)
					{
						var child = parent.GetChild(i);
						if (Dead(child) || ReferenceEquals(child, _overlay)) continue;
						string n; try { n = child.name; } catch { continue; }
						if (n.StartsWith("Cell_MM_AvatarListView", StringComparison.Ordinal)
						 || n.StartsWith("Content_Avatars", StringComparison.Ordinal)
						 || n.StartsWith("Banner_", StringComparison.Ordinal))
						{
							child.gameObject.SetActive(!hide);
						}
					}
				}
				catch { }
			}

			private void Show()
			{
				if (Dead(_overlay)) return;
				_overlay.gameObject.SetActive(true);
				PlaceInColumn();
				Unhide(_overlay);
				HideStockLists(true);

				try
				{
					var cg = _overlay.GetComponent<CanvasGroup>();
					if (!Dead(cg)) { cg.blocksRaycasts = true; cg.interactable = true; }
				}
				catch { }

				bool onScreen; try { onScreen = _overlay.gameObject.activeInHierarchy; } catch { onScreen = false; }
				if (!onScreen && !Dead(_root))
				{
					try { _overlay.gameObject.name = "VA_Dying"; } catch { }
					try { UnityEngine.Object.Destroy(_overlay.gameObject); } catch { }
					_overlay = null; _gridContent = null; _headerCount = null;
					BuildOverlay(_root);
					if (Dead(_overlay)) { Status = "no live content column"; return; }
					_overlay.gameObject.SetActive(true);
					PlaceInColumn();
					Unhide(_overlay);
					HideStockLists(true);
					VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] colonne rebasculee : la grille a ete reconstruite sur le panneau actif.");
				}
				_shown = true;
				if (!Dead(_row))
				{
					try { _row.Find("Background_SelectedState")?.gameObject.SetActive(true); } catch { }
				}

				_baselineSelected.Clear();
				_contentSig = ContentSignature();
				Repopulate();
				Status = "shown; " + _entries.Count + " favourite(s)";
			}

			// WHERE IN THE COLUMN OUR LIST SITS.
			private static TMPro.TMP_Text CountBadge(Transform row)
			{
				try
				{
					if (Dead(row)) return null;
					foreach (var tx in row.GetComponentsInChildren<TMPro.TMP_Text>(true))
					{
						if (Dead(tx)) continue;
						for (Transform up = tx.transform; !Dead(up); up = up.parent)
						{
							string n; try { n = up.name; } catch { break; }
							if (n.IndexOf("Count", StringComparison.OrdinalIgnoreCase) >= 0
							 || n.IndexOf("Number", StringComparison.OrdinalIgnoreCase) >= 0)
							{
								try { up.gameObject.SetActive(true); } catch { }
								return tx;
							}
							if (ReferenceEquals(up, row)) break;
						}
					}
				}
				catch { }
				return null;
			}

			private void FollowSelection()
			{
				try
				{
					if (string.IsNullOrEmpty(ListViewDonor) || Dead(_overlay) || Dead(_root)) return;

					// If the avatars menu root is inactive or moved off-screen (e.g. user entered AvatarDetail or Settings)
					bool rootIsCurrent = false;
					try
					{
						if (_root.gameObject.activeInHierarchy)
						{
							var rt = _root.GetComponent<RectTransform>() ?? _root.TryCast<RectTransform>();
							if (rt != null && Mathf.Abs(rt.anchoredPosition.x) < 50f && Mathf.Abs(rt.anchoredPosition.y) < 50f)
								rootIsCurrent = true;
						}
					}
					catch { }

					if (!rootIsCurrent)
					{
						if (_shown)
						{
							Hide();
							HideStockLists(false);
						}
						return;
					}

					var h = PageHeading(_root);
					string txt = !Dead(h) ? (h.text ?? "").Trim() : "";

					// Check if any stock sidebar row is currently selected
					bool anyStockSelected = false;
					foreach (var sr in _stockRows)
					{
						if (Dead(sr)) continue;
						var sel = sr.Find("Background_SelectedState");
						if (!Dead(sel) && sel.gameObject.activeSelf)
						{
							anyStockSelected = true;
							break;
						}
					}

					if (anyStockSelected || (!string.IsNullOrEmpty(txt) && !string.Equals(txt, Title, StringComparison.Ordinal)))
					{
						if (_shown)
						{
							Hide();
							HideStockLists(false);
						}
					}
					else if (_shown)
					{
						if (!Dead(h) && !string.Equals(h.text, Title, StringComparison.Ordinal)) h.text = Title;
						HideStockLists(true);
					}
				}
				catch { }
			}

			// The content area's own heading: an ACTIVE "Header_..." whose parent also holds the
			// scroll view, which is what distinguishes the page header from every other header in the
			// menu. The biggest label inside it is the title rather than a button caption beside it.
			private static TMPro.TMP_Text PageHeading(Transform root)
			{
				try
				{
					if (Dead(root)) return null;
					foreach (var t in root.GetComponentsInChildren<Transform>(false))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (!n.StartsWith("Header", StringComparison.Ordinal)) continue;

						var parent = t.parent;
						if (Dead(parent)) continue;
						bool holdsScroll = false;
						for (int i = 0; i < parent.childCount && !holdsScroll; i++)
						{
							var c = parent.GetChild(i);
							if (Dead(c)) continue;
							string cn; try { cn = c.name; } catch { continue; }
							if (cn.IndexOf("ScrollRect", StringComparison.Ordinal) >= 0) holdsScroll = true;
						}
						if (!holdsScroll) continue;

						TMPro.TMP_Text best = null; float bf = -1f;
						foreach (var tx in t.GetComponentsInChildren<TMPro.TMP_Text>(false))
						{
							if (Dead(tx)) continue;
							float f = 0f; try { f = tx.fontSize; } catch { }
							if (f > bf) { bf = f; best = tx; }
						}
						if (!Dead(best)) return best;
					}
				}
				catch { }
				return null;
			}

			private void PlaceInColumn()
			{
				try
				{
					if (Dead(_overlay)) return;
					if (string.IsNullOrEmpty(ListViewDonor)) _overlay.SetAsLastSibling();
					else _overlay.SetAsFirstSibling();
				}
				catch { }
			}

			// ACTIVE IS NOT VISIBLE, AND THAT COST THREE BUILDS.
			//
			// Read live from the running game: our list existed, was active, sat in the live column
			// and held all 34 cards with their Detail and Image_Mask children -- and the screen was
			// empty. The list view this clones is the page's TEMPLATE, which VRChat parks beside the
			// real content and keeps switched ON while hiding it by other means: a CanvasGroup at
			// alpha 0, a disabled Canvas, a LayoutElement pinned to no height. None of those change
			// activeInHierarchy, so every check the mod had said "it is on screen" and every one was
			// answering a different question from the one that mattered.
			//
			// A clone is ours: whatever the template used to keep itself dark is undone here, on the
			// list and on its grid, every time it is shown.
			private void Unhide(Transform t)
			{
				if (Dead(t) || string.IsNullOrEmpty(ListViewDonor)) return;
				UnhideOne(t);
				UnhideOne(_gridContent);
				if (!_loggedVisible)
				{
					_loggedVisible = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] visibilite : " + Describe(t)
						+ " | grille " + Describe(_gridContent));
				}
			}

			private static bool _loggedVisible;

			private static void UnhideOne(Transform t)
			{
				if (Dead(t)) return;
				try
				{
					var cg = t.GetComponent<CanvasGroup>();
					if (!Dead(cg)) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; cg.ignoreParentGroups = false; }
				}
				catch { }
				try
				{
					var cv = t.GetComponent<Canvas>();
					if (!Dead(cv))
					{
						cv.enabled = true;
						var gr = t.GetComponent<GraphicRaycaster>() ?? t.gameObject.AddComponent<GraphicRaycaster>();
						gr.enabled = true;
					}
				}
				catch { }
				try
				{
					// -1 means "no opinion", which hands the decision back to the ContentSizeFitter and
					// the grid. A template pinned to minHeight 0 would otherwise keep our list flat.
					var le = t.GetComponent<LayoutElement>();
					if (!Dead(le)) { le.ignoreLayout = false; le.minHeight = -1f; le.preferredHeight = -1f; le.flexibleHeight = -1f; }
				}
				catch { }
				try
				{
					// The list must grow with its cards, or a grid of 34 draws inside a strip of nothing.
					var f = t.GetComponent<ContentSizeFitter>();
					if (!Dead(f)) f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
				}
				catch { }
			}

			// Said once: the three numbers that decide whether an ACTIVE object is actually seen.
			private static string Describe(Transform t)
			{
				try
				{
					if (Dead(t)) return "(absent)";
					float a = -1f; try { var cg = t.GetComponent<CanvasGroup>(); if (!Dead(cg)) a = cg.alpha; } catch { }
					float w = 0f, h = 0f; try { var rt = t.GetComponent<RectTransform>() ?? t.TryCast<RectTransform>(); if (!Dead(rt)) { w = rt.rect.width; h = rt.rect.height; } } catch { }
					bool onH = false; try { onH = t.gameObject.activeInHierarchy; } catch { }
					return t.name + " actif=" + onH + " alpha=" + (a < 0f ? "(aucun)" : a.ToString("0.##"))
						+ " " + (int)w + "x" + (int)h + " enfants=" + t.childCount;
				}
				catch { return "(?)"; }
			}

			private void Hide()
			{
				if (!Dead(_overlay))
				{
					_overlay.gameObject.SetActive(false);
					var cg = _overlay.GetComponent<CanvasGroup>();
					if (!Dead(cg)) { cg.blocksRaycasts = false; cg.interactable = false; }
				}
				if (!Dead(_row))
				{
					try { _row.Find("Background_SelectedState")?.gameObject.SetActive(false); } catch { }
				}
				_shown = false;
				Status = "hidden";
				HideStockLists(false);
				ArchiveFavButtonModule.ClearArchiveSelection();
			}

			private void WatchStockSelection()
			{
				// PRIMARY SIGNAL: VRChat's own content column changed. Our overlay covers the WHOLE
				// content column, so when VRChat swaps the section inside it (clicking "Uploaded",
				// "Recently Visited", "All Friends"…) nothing looks different — our grid just stays
				// on top, which is exactly the bug. Watching the column's own active-section
				// fingerprint catches EVERY section change, including the foldout rows that carry no
				// Background_SelectedState and so were invisible to the row check below.
				string sig = ContentSignature();
				if (sig != null && _contentSig != null && sig != _contentSig) { Hide(); return; }

				// SECONDARY: a stock sidebar row lit up that was not lit when we opened.
				foreach (var sr in _stockRows)
				{
					if (Dead(sr)) continue;
					var sel = sr.Find("Background_SelectedState");
					if (Dead(sel) || !sel.gameObject.activeSelf) continue;
					if (_baselineSelected.Contains(sr.GetInstanceID())) continue;
					Hide();
					return;
				}
			}

			// ----------------------------------------------------------------- data
			private void RebuildData()
			{
				var keep = new Dictionary<string, Entry>(_byId, StringComparer.OrdinalIgnoreCase);
				_entries.Clear(); _byId.Clear();
				if (DirectEntries != null)
				{
					// Entries arrive complete (id+name+image). A kept entry keeps its live cell, so a
					// revision bump does not tear down and rebuild every card.
					foreach (var fresh in DirectEntries())
					{
						if (fresh == null || string.IsNullOrEmpty(fresh.Id) || _byId.ContainsKey(fresh.Id)) continue;
						if (keep.TryGetValue(fresh.Id, out var old)) { old.Name = fresh.Name; old.Image = fresh.Image; _entries.Add(old); _byId[fresh.Id] = old; }
						else { _entries.Add(fresh); _byId[fresh.Id] = fresh; }
					}
				}
				else
				{
					var ids = IdSource != null ? IdSource() : new List<string>();
					foreach (var id in ids)
					{
						if (string.IsNullOrEmpty(id) || _byId.ContainsKey(id)) continue;
						Entry e = keep.TryGetValue(id, out var old) ? old : new Entry { Id = id, Name = Short(id) };
						_entries.Add(e); _byId[id] = e;
					}
				}
				Status = _entries.Count + " favourite(s)";
			}

			private void PumpMetaThrottled()
			{
				if (DirectEntries != null) return;   // entries already complete; no bridge meta needed
				if (!VaAuth.InsideClient) return;
				float now = VaClock.Now;
				if (now < _nextMeta || _metaBusy) return;
				_nextMeta = now + 1f;

				var batch = new List<Entry>(100);
				foreach (var e in _entries) { if (!e.MetaDone) { batch.Add(e); if (batch.Count >= 100) break; } }
				if (batch.Count == 0) return;
				_metaBusy = true;
				_ = ResolveMetaAsync(batch);
			}

			private async System.Threading.Tasks.Task ResolveMetaAsync(List<Entry> batch)
			{
				try
				{
					var sb = new StringBuilder("{\"action\":\"meta\",\"kind\":\"").Append(Kind).Append("\",\"ids\":[");
					for (int i = 0; i < batch.Count; i++)
					{
						if (i > 0) sb.Append(',');
						sb.Append('"').Append(batch[i].Id).Append('"');
					}
					sb.Append("]}");

					var (ok, raw, status) = await VaAuth.FavRawAsync(sb.ToString());
					if (!ok) { foreach (var f in batch) f.MetaDone = true; return; }

					// Only plain data is mutated here — this continuation is off the main thread, so
					// no Unity object may be touched. The main-thread SyncCells() paints the change.
					using var doc = JsonDocument.Parse(raw);
					if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object)
					{
						foreach (var p in res.EnumerateObject())
						{
							string key = p.Name;                       // "world:wrld_xxx" / "user:usr_xxx"
							int c = key.IndexOf(':');
							string id = c >= 0 ? key.Substring(c + 1) : key;
							if (!_byId.TryGetValue(id, out var e)) continue;
							string title = Str(p.Value, "title");
							string image = Str(p.Value, "image");
							title = CleanTitle(title);
							if (!string.IsNullOrWhiteSpace(title)) e.Name = title;
							if (!string.IsNullOrWhiteSpace(image)) e.Image = image;
						}
					}
					foreach (var f in batch) f.MetaDone = true;
				}
				catch { foreach (var f in batch) f.MetaDone = true; }
				finally { _metaBusy = false; }
			}

			private void RequestThumbs()
			{
				if (!VaAuth.InsideClient) return;
				int noUrl = 0, done = 0, failed = 0;
				foreach (var e in _entries)
				{
					// THE CLIENT ALREADY KNOWS THE PICTURE. A favourite saved before the Archive kept
					// preview urls has no Image, and with no url there is nothing to fetch -- which is a
					// card with a name, an author and a grey square. AvatarIndex is fed by VRChat's own
					// parse, so any avatar the client has ever loaded carries its thumbnail url already.
					if (string.IsNullOrEmpty(e.Image))
					{
						try
						{
							var rec = Core.AvatarIndex.ById(e.Id);
							if (rec != null)
								e.Image = !string.IsNullOrEmpty(rec.ThumbUrl) ? rec.ThumbUrl : rec.ImageUrl;
						}
						catch { }
					}

					if (e.ThumbState == 2) done++;
					else if (e.ThumbState == 3) failed++;
					else if (string.IsNullOrEmpty(e.Image)) noUrl++;

					if (_inflight >= MaxInflight) break;
					if (e.ThumbState != 0 || !e.MetaDone || string.IsNullOrEmpty(e.Image)) continue;
					e.ThumbState = 1; _inflight++;
					_ = FetchThumbAsync(e);
				}

				// Said once, a few seconds in: "no thumbnails" has three different causes and they need
				// telling apart -- no url to fetch, a fetch that failed, or a picture that arrived and
				// is being covered up. Guessing between them costs a relaunch each time.
				try
				{
					float now2 = VaClock.Now;
					if (_thumbReportAt == 0f) _thumbReportAt = now2 + 8f;
					else if (_thumbReportAt > 0f && now2 >= _thumbReportAt)
					{
						_thumbReportAt = -1f;
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] vignettes : " + done + " prete(s), "
							+ failed + " echouee(s), " + noUrl + " sans url, sur " + _entries.Count
							+ " | index API=" + Core.AvatarIndex.Status);
					}
				}
				catch { }
			}

			private float _thumbReportAt;

			private async System.Threading.Tasks.Task FetchThumbAsync(Entry e)
			{
				byte[] data = null;
				try
				{
					string cache = Path.Combine(ThumbDir, e.Id + ".img");
					try { if (File.Exists(cache)) data = File.ReadAllBytes(cache); } catch { }
					if (data == null || data.Length < 100)
					{
						data = await GetImageAsync(Small(e.Image));
						if (data == null) data = await GetImageAsync(e.Image);
						if (data != null && data.Length >= 100) { try { File.WriteAllBytes(cache, data); } catch { } }
					}
				}
				catch { }
				finally { _decoded.Enqueue((e, data)); }
			}

			private void DrainDecoded(int max)
			{
				for (int n = 0; n < max; n++)
				{
					if (!_decoded.TryDequeue(out var item)) return;
					_inflight--; if (_inflight < 0) _inflight = 0;
					try
					{
						if (item.data == null || item.data.Length < 100) { item.e.ThumbState = 3; continue; }
						var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
						{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
						if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(item.data)))
						{ item.e.ThumbState = 3; continue; }
						item.e.Thumb = tex;
						item.e.ThumbState = 2;
					}
					catch { item.e.ThumbState = 3; }
				}
			}

			// ----------------------------------------------------------------- cells
			private void Repopulate()
			{
				if (Dead(_gridContent)) return;

				// Rebuild only when the set changed size (add/remove); otherwise reuse cells.
				if (_gridContent.childCount != _entries.Count)
				{
					for (int i = _gridContent.childCount - 1; i >= 0; i--)
					{
						try { UnityEngine.Object.Destroy(_gridContent.GetChild(i).gameObject); } catch { }
					}
					foreach (var e in _entries) { e.Cell = null; e.ThumbImg = null; e.ThumbBg = null; e.NameTmp = null; e.SubTmp = null; e.ThumbFit = null; e.ShownName = null; }
					Transform donor = ResolveCellDonor();

					// THE GRID MUST MAKE ROOM FOR THE CARD WE ACTUALLY CLONE.
					//
					// Measured live: the borrowed list's GridLayoutGroup was at cell 288x250 while
					// Cell_MM_MarketplaceAvatar is built for 288x297. VRChat has a compact/large toggle
					// (ToggleCellSize in the avatars header) and the list we cloned happened to be in
					// COMPACT mode, so every card was squeezed 47px short and its detail block spilled
					// over the row beneath -- cards stacked on top of each other.
					//
					// The clone is ours, so its grid is ours to size: take the donor's real rect. That
					// also follows VRChat if a future build changes either number.
					if (!Dead(donor) && !Dead(_gridContent))
					{
						try
						{
							var glg = _gridContent.GetComponent<GridLayoutGroup>();
							// TryCast, NEVER `as`. A C# `as` is a MANAGED cast and returns null for every
							// il2cpp proxy, so this whole block silently did nothing and the cards kept
							// overlapping. Same trap already fixed once in InspectServer -- writing it a
							// second time is exactly why it is worth naming here.
							var drt = donor.TryCast<RectTransform>();
							if (!Dead(glg) && !Dead(drt))
							{
								float dw = drt.rect.width, dh = drt.rect.height;
								// The donor may report its layout size rather than its drawn rect; its own
								// LayoutElement is the authority on how tall the card wants to be.
								try
								{
									var dle = donor.GetComponent<LayoutElement>();
									if (!Dead(dle) && dle.preferredHeight > 1f) dh = dle.preferredHeight;
									if (!Dead(dle) && dle.preferredWidth > 1f) dw = dle.preferredWidth;
								}
								catch { }
								if (dw > 1f && dh > 1f && (Mathf.Abs(glg.cellSize.x - dw) > 0.5f || Mathf.Abs(glg.cellSize.y - dh) > 0.5f))
								{
									VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] cellule de grille " + (int)glg.cellSize.x
										+ "x" + (int)glg.cellSize.y + " -> " + (int)dw + "x" + (int)dh + " (taille reelle de la carte clonee).");
									glg.cellSize = new Vector2(dw, dh);
								}
							}
						}
						catch { }
					}

					foreach (var e in _entries) { if (Dead(donor)) BuildScratchCell(e); else BuildNativeCell(e, donor); }

					if (!_loggedGrid)
					{
						_loggedGrid = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid:" + Kind + "] populated " + _gridContent.childCount
							+ " cell(s) for " + _entries.Count + " favourite(s); style=" + (Dead(donor) ? "scratch" : "native"));
					}
				}
				SyncCells();
			}

			// The native world card, cloned. Gives VRChat's exact look; the obfuscated data-model is
			// stripped from the ROOT (so nothing re-binds it), and SyncCells re-asserts our name and
			// texture every frame in case a surviving child binder tries to blank them.
			private void BuildNativeCell(Entry e, Transform donor)
			{
				var go = UnityEngine.Object.Instantiate(donor.gameObject, _gridContent);
				go.name = "VA_Cell";
				var cell = go.transform;
				try { StripAllForeign(cell); } catch { }
				try { go.SetActive(true); } catch { }

				// Drop the world-only chrome so the card reads as a clean thumbnail + name.
				HideChild(cell, "Detail/StatRow_Population");
				HideChild(cell, "Detail/PreloadProgressBG");
				HideChild(cell, "Image_Mask/AttributeRow");
				HideChild(cell, "Hover");
				HideChild(cell, "Detail/SelectedState");

				// THE DONOR'S OWN STATE, WHICH IS NOT OURS TO SHOW.
				//
				// Read live from the running game: Thumbnail_AvatarUnavailable was ACTIVE on every
				// cloned card, and it is a later sibling of ContentImage inside the same mask -- so it
				// is drawn ON TOP of the thumbnail. Every card was a flat grey plate, and setting the
				// texture underneath would have changed nothing.
				//
				// The attribute row goes too: those badges (platform, performance, favourite) describe
				// the avatar the template was showing, not ours. A wrong badge is worse than none.
				HideChild(cell, "Image_Mask/Thumbnail_AvatarUnavailable");
				HideChild(cell, "Image_Mask/Cell_Avatar_Attribute_Row");
				HideChild(cell, "Detail/Gradient/ContentWarningBanner");
				HideChild(cell, "Detail/SelectedState");
				HideChild(cell, "Detail/AvatarAttributes/Text_CreditPrice");

				var attr = cell.Find("Detail/AvatarAttributes");
				if (!Dead(attr)) attr.gameObject.SetActive(true);
				var tan = cell.Find("Detail/AvatarAttributes/Text_AvatarName");
				if (!Dead(tan)) tan.gameObject.SetActive(true);
				var tau = cell.Find("Detail/AvatarAttributes/Text_AuthorName");
				if (!Dead(tau))
				{
					tau.gameObject.SetActive(true);
					var cg = tau.GetComponent<CanvasGroup>();
					if (!Dead(cg)) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = false; }
				}

				CardParts(cell, out var nameTmp, out var subTmp, out var thumb, out var thumbBg);
				if (!Dead(nameTmp)) nameTmp.raycastTarget = false;
				if (!Dead(subTmp)) subTmp.raycastTarget = false;
				try { var g = cell.Find("Detail/Gradient")?.GetComponent<Graphic>(); if (!Dead(g)) g.raycastTarget = false; } catch { }
				try { var m = cell.Find("Image_Mask")?.GetComponent<Graphic>(); if (!Dead(m)) m.raycastTarget = false; } catch { }
				try { var r = cell.Find("Image_Mask/Cell_Avatar_Attribute_Row")?.GetComponent<Graphic>(); if (!Dead(r)) r.raycastTarget = false; } catch { }
				e.NameTmp = nameTmp; e.SubTmp = subTmp; e.ThumbImg = thumb; e.ThumbBg = thumbBg;
				e.ThumbFit = null;
				if (!Dead(thumb)) { try { e.ThumbFit = thumb.GetComponent<AspectRatioFitter>(); } catch { } }

				// THE THUMBNAIL HAS BINDERS OF ITS OWN, AND THEY WIN.
				//
				// Read live from the running game: ContentImage carries RawImageEx, an AspectRatioFitter
				// and TWO obfuscated components -- the avatar cell's image binders. StripRoot cleaned the
				// CELL root, not this child, so those binders lived on and re-nulled the texture the
				// instant SyncCells set it: 34 thumbnails downloaded and decoded, every card still grey.
				// Stripping ContentImage the same way -- Keep holds RawImage/RawImageEx and the fitter,
				// so only the binders go -- leaves our texture the only thing writing to it.
				if (!Dead(thumb)) { try { MenuCard.StripRoot(thumb.transform, keepStyle: true); } catch { } }

				// THE CLICK, WHICH THE NATIVE PATH NEVER HAD.
				//
				// Only BuildScratchCell wired one, so every card built from VRChat's own prefab was
				// inert -- clicking did nothing at all. StripRoot removes the cell's own click
				// component along with its binders, so one has to be put back: a Button with no
				// transition and no target graphic, dispatched through UiClick (Button.Press is hooked;
				// an il2cpp delegate would end the process on this build).
				if (OnPick != null)
				{
					try
					{
						var cellCg = cell.GetComponent<CanvasGroup>();
						if (!Dead(cellCg))
						{
							cellCg.blocksRaycasts = true;
							cellCg.interactable = true;
						}

						var ent = e;

						// 1. Root button & raycast target
						var btn = cell.GetComponent<Button>();
						if (Dead(btn)) btn = cell.gameObject.AddComponent<Button>();
						if (!Dead(btn))
						{
							btn.transition = Selectable.Transition.None;
							btn.interactable = true;
							var gr = cell.GetComponent<Graphic>();
							if (Dead(gr))
							{
								var img = cell.gameObject.AddComponent<Image>();
								img.color = new Color(0f, 0f, 0f, 0f);
								gr = img;
							}
							if (!Dead(gr)) gr.raycastTarget = true;
							btn.targetGraphic = gr;
							Core.UiClick.Forget(btn);
							Core.UiClick.AddClick(btn, () => { OnPick(ent); });
						}

						// 2. Thumbnail button & raycast target
						if (!Dead(thumb))
						{
							thumb.raycastTarget = true;
							var tBtn = thumb.GetComponent<Button>();
							if (Dead(tBtn)) tBtn = thumb.gameObject.AddComponent<Button>();
							if (!Dead(tBtn))
							{
								tBtn.transition = Selectable.Transition.None;
								tBtn.interactable = true;
								tBtn.targetGraphic = thumb;
								Core.UiClick.Forget(tBtn);
								Core.UiClick.AddClick(tBtn, () => { OnPick(ent); });
							}
						}

						// 3. Child background button & raycast target
						var bg = cell.Find("Detail/Background");
						if (!Dead(bg))
						{
							var bgImg = bg.GetComponent<Graphic>();
							if (!Dead(bgImg)) bgImg.raycastTarget = true;
							var bgBtn = bg.GetComponent<Button>();
							if (Dead(bgBtn)) bgBtn = bg.gameObject.AddComponent<Button>();
							if (!Dead(bgBtn))
							{
								bgBtn.transition = Selectable.Transition.None;
								bgBtn.interactable = true;
								if (!Dead(bgImg)) bgBtn.targetGraphic = bgImg;
								Core.UiClick.Forget(bgBtn);
								Core.UiClick.AddClick(bgBtn, () => { OnPick(ent); });
							}
						}

						// 4. Dedicated full-card overlay catch
						try
						{
							var catchGo = new GameObject("VA_ClickCatch", Il2CppType.Of<RectTransform>());
							catchGo.transform.SetParent(cell, false);
							var catchRt = catchGo.GetComponent<RectTransform>();
							Stretch(catchRt);
							catchRt.SetAsLastSibling();
							var catchImg = catchGo.AddComponent<Image>();
							catchImg.color = new Color(0f, 0f, 0f, 0f);
							catchImg.raycastTarget = true;
							var catchBtn = catchGo.AddComponent<Button>();
							catchBtn.transition = Selectable.Transition.None;
							catchBtn.targetGraphic = catchImg;
							catchBtn.interactable = true;
							var catchLe = catchGo.AddComponent<LayoutElement>();
							catchLe.ignoreLayout = true;
							Core.UiClick.Forget(catchBtn);
							Core.UiClick.AddClick(catchBtn, () => { OnPick(ent); });
						}
						catch { }
					}
					catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] clic de carte : " + ex.Message); }
				}

				e.Cell = cell; e.ShownName = null;
			}

			// From-scratch fallback, used only until a native Cell_MM_World exists to clone. Brighter
			// than before so the card is clearly visible even before its thumbnail loads.
			// EVERY STEP GUARDED. This runs from a Button.onClick, i.e. inside an il2cpp→managed
			// trampoline, where an escaping NullReferenceException is not a caught exception but a
			// logged interop error that abandons the whole click — which is exactly what happened
			// the first time the social page was opened before any world card existed to clone.
			private void BuildScratchCell(Entry e)
			{
				try
				{
					var cell = NewRect("Cell", _gridContent);
					if (Dead(cell)) return;
					var cbg = cell.gameObject.AddComponent<Image>();
					if (!Dead(cbg)) cbg.color = new Color(0.16f, 0.20f, 0.28f, 1f);

					var thumb = NewRect("Thumb", cell);
					RawImage raw = null; Image tbg = null;
					if (!Dead(thumb))
					{
						thumb.anchorMin = new Vector2(0.05f, 1f - ThumbAspect - 0.02f);
						thumb.anchorMax = new Vector2(0.95f, 0.96f);
						thumb.offsetMin = Vector2.zero; thumb.offsetMax = Vector2.zero;
						tbg = thumb.gameObject.AddComponent<Image>();
						if (!Dead(tbg)) { tbg.color = new Color(0.22f, 0.27f, 0.36f, 1f); tbg.raycastTarget = false; }
						raw = thumb.gameObject.AddComponent<RawImage>();
						if (!Dead(raw)) { raw.color = new Color(1f, 1f, 1f, 0f); raw.raycastTarget = false; }
					}

					TMPro.TextMeshProUGUI ntmp = null;
					var name = NewRect("Name", cell);
					if (!Dead(name))
					{
						name.anchorMin = new Vector2(0.05f, 0.02f);
						name.anchorMax = new Vector2(0.95f, 1f - ThumbAspect - 0.04f);
						name.offsetMin = Vector2.zero; name.offsetMax = Vector2.zero;
						ntmp = Label(name, "", 16f, TMPro.TextAlignmentOptions.Top);
						if (!Dead(ntmp))
						{
							ntmp.enableWordWrapping = true;
							ntmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						}
					}

					e.Cell = cell; e.ThumbImg = raw; e.ThumbBg = tbg; e.NameTmp = ntmp; e.ShownName = null;
					// CLICK TO ACT (avatars: wear it). Through UiClick.AddClick, which hooks Button.Press
					// and dispatches itself, so no il2cpp delegate is created -- the bridge is off on 1903.
					if (OnPick != null)
					{
						var btn = cell.gameObject.AddComponent<Button>();
						if (!Dead(btn)) { btn.transition = Selectable.Transition.None; var ent = e; Core.UiClick.AddClick(btn, () => { OnPick(ent); }); }
						if (!Dead(cbg)) cbg.raycastTarget = true;
					}
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[FavGrid:" + Kind + "] scratch cell: " + ex.Message);
				}
			}

			// Find one native world card to clone. Prefer a live Cell_MM_World; cache it statically so
			// both panes share it and the search runs at most once every few seconds until found.
			private Transform ResolveCellDonor()
			{
				if (string.IsNullOrEmpty(CellDonorName)) return null;   // this pane draws from scratch
				if (!Dead(_cellDonor)) return _cellDonor;
				float now = VaClock.Now;
				if (now < _donorNext) return null;
				_donorNext = now + 3f;
				// OUR page first, then the whole main menu. Scoping alone is not enough: the social
				// grid borrows the WORLD card, which only exists under the worlds page, so a
				// strictly-scoped search would find nothing and fall back to scratch cards forever.
				var found = FindDonorIn(_root) ?? FindDonorIn(Core.QuickMenu.Main());
				if (!Dead(found)) _cellDonor = found;
				return _cellDonor;
			}

			private Transform FindDonorIn(Transform scope)
			{
				try
				{
					if (Dead(scope)) return null;
					foreach (var t in scope.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (!n.StartsWith(CellDonorName, StringComparison.Ordinal)) continue;
						if (n.StartsWith("VA_", StringComparison.Ordinal)) continue;
						if (IsOurs(t)) continue;
						// Must carry the parts we drive, or it is not a usable donor -- tested by SHAPE,
						// not by child name. "Detail/Text_WorldName" and "Image_Mask/ContentImage" are the
						// world card's own spelling; the avatar card has the same anatomy under different
						// names, and a dump cannot even reach that depth. A card is a card when it has a
						// label and a picture, which is exactly what we are about to write into.
						CardParts(t, out var pn, out _, out var pt, out _);
						if (Dead(pn) || Dead(pt)) continue;
						return t;
					}
				}
				catch { }
				return null;
			}

			// THE PARTS OF A NATIVE CARD, FOUND BY SHAPE.
			//
			// Child NAMES differ per card (the world card spells them Detail/Text_WorldName and
			// Image_Mask/ContentImage; the avatar card does not) and they are past the depth any
			// hierarchy dump reaches, so nothing about them can be relied on. What IS reliable is
			// what the card is made of: a picture at the top, the biggest label under it for the
			// title, the next one down for the author.
			//
			// Inactive children are included: a card whose thumbnail has not loaded keeps its image
			// object switched off, and skipping it would pick the wrong graphic on exactly the cards
			// we are about to fill.
			private static bool _anatomyLogged;
			private static void CardParts(Transform cell, out TMPro.TMP_Text name, out TMPro.TMP_Text sub,
			                              out RawImage thumb, out Image thumbBg)
			{
				name = null; sub = null; thumb = null; thumbBg = null;
				try
				{
					if (Dead(cell)) return;

					// ---- labels, direct path lookup first ------------------------------------
					var tName = cell.Find("Detail/AvatarAttributes/Text_AvatarName");
					if (!Dead(tName)) name = tName.GetComponent<TMPro.TMP_Text>() ?? tName.GetComponentInChildren<TMPro.TMP_Text>(true);
					var tAuthor = cell.Find("Detail/AvatarAttributes/Text_AuthorName");
					if (!Dead(tAuthor)) sub = tAuthor.GetComponent<TMPro.TMP_Text>() ?? tAuthor.GetComponentInChildren<TMPro.TMP_Text>(true);

					if (Dead(name))
					{
						var texts = new List<TMPro.TMP_Text>();
						foreach (var t in cell.GetComponentsInChildren<TMPro.TMP_Text>(true))
						{
							if (Dead(t)) continue;
							texts.Add(t);
						}
						texts.Sort((a, b) =>
						{
							float fa = 0f, fb = 0f;
							try { fa = a.fontSize; } catch { }
							try { fb = b.fontSize; } catch { }
							return fb.CompareTo(fa);
						});
						if (texts.Count > 0) name = texts[0];
						if (texts.Count > 1 && Dead(sub)) sub = texts[1];
					}

					// Fallback: create TextMeshProUGUI if missing on the container
					if (Dead(name) && !Dead(tName))
					{
						var tmp = tName.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
						if (!Dead(_font)) tmp.font = _font;
						tmp.fontSize = 24f;
						tmp.enableWordWrapping = false;
						tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						name = tmp;
					}
					if (Dead(sub) && !Dead(tAuthor))
					{
						var tmp = tAuthor.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
						if (!Dead(_font)) tmp.font = _font;
						tmp.fontSize = 18f;
						tmp.enableWordWrapping = false;
						tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						sub = tmp;
					}

					// ---- the picture ----------------------------------------------------------
					// A RawImage is what a downloaded thumbnail lands in (an Image takes a Sprite, which
					// we do not build). "The widest RawImage" was not enough: it chose Detail/Gradient,
					// the decorative wash drawn ACROSS the whole card, so every card showed a gradient
					// where its avatar should be. The thumbnail is the one inside the card's MASK -- that
					// is what a mask is there for, and it is how the world card is built too
					// (Image_Mask/ContentImage). So: masked candidates first, widest among them; only if
					// the card has no mask at all does the unmasked search decide.
					float best = 0f, bestFree = 0f;
					RawImage free = null;
					foreach (var ri in cell.GetComponentsInChildren<RawImage>(true))
					{
						if (Dead(ri)) continue;
						float w = 0f; try { w = ri.rectTransform.rect.width; } catch { }
						if (Masked(ri.transform, cell))
						{
							if (w > best) { best = w; thumb = ri; }
						}
						else if (w > bestFree) { bestFree = w; free = ri; }
					}
					if (Dead(thumb)) thumb = free;

					// The plate BEHIND the picture, dimmed once a real thumbnail is in place.
					//
					// NEVER THE MASK'S OWN GRAPHIC. A Unity Mask writes its graphic into the stencil and
					// clips on its ALPHA, so dimming it to transparent -- exactly what a background plate
					// wants -- makes the mask reject every pixel of the very thumbnail it frames. On the
					// avatar card the thumbnail's parent IS Image_Mask, so SyncCells' own tidy-up turned 34
					// correctly downloaded and decoded textures into 34 grey squares. The world card never
					// hit this because it pinned ThumbBg to null by hand; resolving it generically is what
					// walked into the trap.
					if (!Dead(thumb))
					{
						try
						{
							var holder = thumb.transform.parent;
							bool masks = false;
							if (!Dead(holder))
								try { masks = !Dead(holder.GetComponent<Mask>()) || !Dead(holder.GetComponent<RectMask2D>()); }
								catch { masks = true; }   // unsure means hands off
							if (!Dead(holder) && !masks) thumbBg = holder.GetComponent<Image>();
						}
						catch { }
					}

					// Said once, so the card's real child names reach the log even though no dump can
					// reach that depth -- and so a future build that moves them is visible, not silent.
					if (!_anatomyLogged && !Dead(name) && !Dead(thumb))
					{
						_anatomyLogged = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid] carte native '" + RelPath(cell, cell) + "' : titre="
							+ RelPath(name.transform, cell) + " sous-titre=" + (Dead(sub) ? "(aucun)" : RelPath(sub.transform, cell))
							+ " vignette=" + RelPath(thumb.transform, cell) + " fond=" + (Dead(thumbBg) ? "(aucun)" : RelPath(thumbBg.transform, cell)));
						LogGraphics(cell);
					}
				}
				catch { }
			}

			// True when this graphic is drawn THROUGH a mask belonging to the card -- i.e. it is the
			// content the card frames, not chrome laid over it.
			private static bool Masked(Transform t, Transform cell)
			{
				try
				{
					for (Transform up = t; !Dead(up); up = up.parent)
					{
						if (!Dead(up.GetComponent<Mask>()) || !Dead(up.GetComponent<RectMask2D>())) return true;
						if (ReferenceEquals(up, cell)) break;
					}
				}
				catch { }
				return false;
			}

			// EVERY graphic on the card, said once. The single "anatomy" line names only what was
			// CHOSEN, which is exactly no help when the choice is wrong -- Detail/Gradient looked
			// perfectly plausible in the log. This lists what there was to choose from, so the next
			// wrong pick is read off the log instead of guessed at over another relaunch.
			private static void LogGraphics(Transform cell)
			{
				try
				{
					var parts = new List<string>();
					foreach (var g in cell.GetComponentsInChildren<Graphic>(true))
					{
						if (Dead(g) || parts.Count >= 24) continue;
						string kind = "?"; try { kind = MenuCard.Il2CppNameOf(g); } catch { }
						float w = 0f, h = 0f;
						try { w = g.rectTransform.rect.width; h = g.rectTransform.rect.height; } catch { }
						parts.Add(RelPath(g.transform, cell) + " <" + kind + "> " + (int)w + "x" + (int)h
							+ (Masked(g.transform, cell) ? " [masque]" : ""));
					}
					VRChatArchiveModPlugin.Logger.LogInfo("[FavGrid] graphismes de la carte : " + string.Join(" | ", parts));
				}
				catch { }
			}

			// A child's path relative to the card, for the anatomy line above.
			private static string RelPath(Transform t, Transform root)
			{
				try
				{
					if (Dead(t)) return "(null)";
					string s2 = t.name;
					for (Transform up = t.parent; !Dead(up) && !ReferenceEquals(up, root) && s2.Length < 90; up = up.parent)
						s2 = up.name + "/" + s2;
					return s2;
				}
				catch { return "(?)"; }
			}

			// True when the transform lives inside our own overlay — never clone our own clone.
			private bool IsOurs(Transform t)
			{
				try
				{
					if (Dead(_overlay)) return false;
					var p = t;
					while (p != null) { if (ReferenceEquals(p, _overlay)) return true; p = p.parent; }
				}
				catch { }
				return false;
			}

			// A cheap fingerprint of VRChat's own content column: the names of the sections it has
			// switched on. Used to notice that the user picked one of ITS sections.
			private string ContentSignature()
			{
				try
				{
					if (Dead(_overlay)) return null;
					var host = _overlay.parent;                       // ScrollRect_Content
					if (Dead(host)) return null;
					var vp = host.Find("Viewport");
					if (Dead(vp)) return null;

					var sb = new StringBuilder(64);
					Stack(sb, vp, 0);
					return sb.ToString();
				}
				catch { return null; }
			}

			private static void Stack(StringBuilder sb, Transform t, int depth)
			{
				if (depth > 2) return;
				int n = 0; try { n = t.childCount; } catch { return; }
				for (int i = 0; i < n; i++)
				{
					Transform c = null; try { c = t.GetChild(i); } catch { continue; }
					if (Dead(c)) continue;
					bool on; try { on = c.gameObject.activeSelf; } catch { continue; }
					if (!on) continue;
					try { sb.Append(c.name).Append('|'); } catch { }
					Stack(sb, c, depth + 1);
				}
			}

			private static void HideChild(Transform cell, string path)
			{
				try { var t = cell.Find(path); if (!Dead(t)) t.gameObject.SetActive(false); } catch { }
			}

			private void SyncCells()
			{
				foreach (var e in _entries)
				{
					if (!Dead(e.NameTmp))
					{
						string want = string.IsNullOrEmpty(e.Name) ? Short(e.Id) : e.Name;
						// Compare the ACTUAL text, not just our cached copy: a native cell may carry a
						// surviving binder that blanks Text_WorldName, and re-asserting each frame wins.
						string have = null; try { have = e.NameTmp.text; } catch { }
						if (have != want) { try { e.NameTmp.text = want; e.ShownName = want; } catch { } }
						try { e.NameTmp.color = Color.white; e.NameTmp.gameObject.SetActive(true); } catch { }
					}

					// The author, when the client has already met this avatar. Free: AvatarIndex is fed
					// by VRChat's own parse, so a favourite that has ever been displayed is in it, and
					// one that has not simply shows no author line rather than a wrong one.
					if (string.IsNullOrEmpty(e.Author))
					{
						try { e.Author = Core.AvatarIndex.AuthorFor(e.Id) ?? ""; } catch { }
					}

					// ALWAYS WRITTEN, never left alone: a cloned native card arrives carrying the
					// donor's author line, so an entry with no author must blank it rather than skip it.
					if (!Dead(e.SubTmp))
					{
						string wantSub = string.IsNullOrEmpty(e.Author) ? "" : "By: " + e.Author;
						string haveSub = null; try { haveSub = e.SubTmp.text; } catch { }
						if (haveSub != wantSub) { try { e.SubTmp.text = wantSub; } catch { } }
						try
						{
							e.SubTmp.color = new Color(0.7f, 0.72f, 0.8f, 1f);
							e.SubTmp.gameObject.SetActive(true);
							var subCg = e.SubTmp.GetComponent<CanvasGroup>();
							if (!Dead(subCg)) subCg.alpha = 1f;
						}
						catch { }
					}

					if (e.ThumbState == 2 && !Dead(e.Thumb) && !Dead(e.ThumbImg))
					{
						// WHITE IS RE-ASSERTED EVERY PASS, not only when the texture is first set.
						//
						// Most cards rendered in greyscale while a few stayed in colour: the clone keeps
						// VRChat's StyleElement (that is what makes it look native), and the StyleElement
						// re-tints the graphic AFTER we set it. A colour written once is a colour VRChat
						// gets to overwrite at its leisure -- the same lesson as the menu text, which is
						// why THAT is re-asserted too. One comparison per card per pass, no write when it
						// is already right.
						try { if (e.ThumbImg.color != Color.white) e.ThumbImg.color = Color.white; } catch { }

						if (!ReferenceEquals(e.ThumbImg.texture, e.Thumb))
						{
							try
							{
								e.ThumbImg.texture = e.Thumb;
								e.ThumbImg.color = Color.white;

								// THE RATIO MUST COME FROM THE PICTURE WE JUST PUT IN.
								//
								// Measured live: our ContentImage sat at 320x160 -- a 2.0 ratio -- inside a
								// 278x160 mask, while VRChat's own card puts it at 278x208 (about 4:3). The
								// AspectRatioFitter survives our strip (it is layout, not a binder) but the
								// component that UPDATED its ratio on load did not -- so every card kept the
								// template's ratio and every thumbnail came out stretched wide and squashed
								// short. Keeping the fitter and feeding it the truth is what makes the card
								// read like VRChat's instead of nearly like it.
								if (!Dead(e.ThumbFit))
								{
									try
									{
										int tw = e.Thumb.width, th = e.Thumb.height;
										if (tw > 0 && th > 0) e.ThumbFit.aspectRatio = (float)tw / th;
									}
									catch { }
								}
								// The donor may hand over a card whose image component was switched off for an
								// avatar it could not show; ours always has something to draw.
								try { e.ThumbImg.enabled = true; e.ThumbImg.gameObject.SetActive(true); } catch { }
								if (!Dead(e.ThumbBg)) e.ThumbBg.color = new Color(0f, 0f, 0f, 0f);
							}
							catch { }
						}
					}
				}
			}

			// Every ACTIVE label under the menu that still reads the borrowed category's name becomes
			// ours. Matching on the text, never on a path: the heading's object name changes between
			// builds, the words on screen do not.
			private float _nextHeader;
			private void RetitleHeader(Transform root)
			{
				try
				{
					float now = VaClock.Now;
					if (now < _nextHeader) return;
					_nextHeader = now + 0.35f;
					if (Dead(root)) return;

					// OUR ROW TOO, and on this same cadence.
					//
					// Scoping the rename to the page heading stopped the sidebar row being re-asserted,
					// so VRChat rewrote it and the menu ended up saying two different things at once:
					// the page read ARCHIVE FAVORITES and the row that opens it read SDK Test Avatars.
					// Our row only -- never a sweep: our own label reads Title, and FollowSelection tells
					// "our category is showing" apart from "our row exists" precisely because the two
					// live in different subtrees.
					if (!Dead(_row))
					{
						try
						{
							var rt = _row.Find("Mask/Text_Name");
							var rl = Dead(rt) ? null : rt.GetComponent<TMPro.TMP_Text>();
							if (Dead(rl)) rl = _row.GetComponentInChildren<TMPro.TMP_Text>(true);
							if (!Dead(rl) && !string.Equals(rl.text, Title, StringComparison.Ordinal)) rl.text = Title;
						}
						catch { }
					}

					// THE PAGE HEADING, precisely. Sweeping every label that reads the borrowed
					// category renames whatever else happens to say it, and FollowSelection then has to
					// tell our heading apart from our row. Renaming the one heading keeps that signal clean.
					var head = PageHeading(root);
					if (!Dead(head))
					{
						string hv; try { hv = head.text; } catch { hv = null; }
						if (string.Equals(hv, ReplaceLabel, StringComparison.Ordinal))
							try { head.text = Title; } catch { }
						return;
					}

					// No heading found (a build that moved it): fall back to the old sweep rather than
					// leaving the page saying the borrowed category's name.
					foreach (var tx in root.GetComponentsInChildren<TMPro.TMP_Text>(false))
					{
						if (Dead(tx)) continue;
						string v; try { v = tx.text; } catch { continue; }
						if (!string.Equals(v, ReplaceLabel, StringComparison.Ordinal)) continue;
						try { tx.text = Title; } catch { }
					}
				}
				catch { }
			}

			private void UpdateCounts()
			{
				string n = _entries.Count.ToString();
				if (!Dead(_rowCount))
				{
					// THE PILL WAS ON, THE NUMBER INSIDE IT WAS OFF.
					//
					// Measured live on the row we take over:
					//   Count_BG      [ACTIVE 52x40]
					//     Text_Number [INACTIVE 36x36]
					// so the badge drew as a blank white pill: the count WAS being written, into a
					// switched-off object. VRChat leaves it off for a category that has no count of its
					// own, which the borrowed one does not -- ours does.
					try
					{
						var go = _rowCount.gameObject;
						if (!go.activeSelf) go.SetActive(true);
						var pill = _rowCount.transform.parent;
						if (!Dead(pill) && !pill.gameObject.activeSelf) pill.gameObject.SetActive(true);
					}
					catch { }
					try { if (_rowCount.text != n) _rowCount.text = n; } catch { }
				}
				if (!Dead(_headerCount)) { try { string h = "(" + n + ")"; if (_headerCount.text != h) _headerCount.text = h; } catch { } }
			}

			// ----------------------------------------------------------------- helpers
			private void StealFont(Transform root)
			{
				try { var t = root.GetComponentInChildren<TMPro.TMP_Text>(true); if (!Dead(t)) _font = t.font; } catch { }
			}

			private RectTransform NewRect(string name, Transform parent)
			{
				var go = new GameObject(name, Il2CppType.Of<RectTransform>());
				var rt = go.GetComponent<RectTransform>();
				rt.SetParent(parent, false);
				return rt;
			}

			private static void Stretch(RectTransform rt)
			{
				rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
				rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
			}

			private TMPro.TextMeshProUGUI Label(RectTransform rt, string text, float size, TMPro.TextAlignmentOptions align)
			{
				var tmp = rt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
				if (!Dead(_font)) tmp.font = _font;
				tmp.text = text; tmp.fontSize = size; tmp.alignment = align;
				tmp.enableWordWrapping = false;
				tmp.color = new Color(0.93f, 0.92f, 1f);
				tmp.raycastTarget = false;
				return tmp;
			}

			private static readonly HttpClient _directHttp = MakeDirectHttp();
			private static HttpClient MakeDirectHttp()
			{
				var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
				c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
					"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 VRChatArchiveMod/3.9");
				return c;
			}

			private async System.Threading.Tasks.Task<byte[]> GetImageAsync(string url)
			{
				try
				{
					if (string.IsNullOrEmpty(url)) return null;
					if (VaAuth.InsideClient)
					{
						try
						{
							string body = "{\"action\":\"image\",\"kind\":\"" + Kind + "\",\"url\":\"" + url.Replace("\"", "") + "\"}";
							var (ok, raw, _) = await VaAuth.FavRawAsync(body);
							if (ok)
							{
								using var doc = JsonDocument.Parse(raw);
								if (doc.RootElement.TryGetProperty("b64", out var b) && b.ValueKind == JsonValueKind.String)
									return Convert.FromBase64String(b.GetString());
							}
						}
						catch { }
					}

					if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
					{
						using var resp = await _directHttp.GetAsync(url);
						if (resp.IsSuccessStatusCode)
						{
							return await resp.Content.ReadAsByteArrayAsync();
						}
					}
				}
				catch { return null; }
				return null;
			}

			private static string ThumbDir
			{
				get
				{
					string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "thumbs");
					try { Directory.CreateDirectory(d); } catch { }
					return d;
				}
			}

			private static string Small(string image)
			{
				try
				{
					foreach (string marker in new[] { "/api/1/file/", "/api/1/image/" })
					{
						int i = image.IndexOf(marker, StringComparison.Ordinal);
						if (i < 0) continue;
						string[] parts = image.Substring(i + marker.Length).Split('/');
						if (parts.Length < 2 || !parts[0].StartsWith("file_", StringComparison.Ordinal)) continue;
						return image.Substring(0, i) + "/api/1/image/" + parts[0] + "/" + parts[1] + "/256";
					}
				}
				catch { }
				return image;
			}

			private string CleanTitle(string title)
			{
				if (string.IsNullOrEmpty(title)) return title;
				title = title.Trim();
				// World OG titles read "Name by Author"; users are a plain display name.
				if (Kind == "world")
				{
					int at = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
					if (at > 0) title = title.Substring(0, at).Trim();
				}
				const string tail = " - an avatar on VRChat";
				int t = title.IndexOf(tail, StringComparison.OrdinalIgnoreCase);
				if (t > 0) title = title.Substring(0, t);
				return title;
			}

			private static string Str(JsonElement e, string prop)
				=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

			private static string Short(string id)
			{
				if (string.IsNullOrEmpty(id)) return "";
				int i = id.IndexOf('_');
				string tail = i >= 0 ? id.Substring(i + 1) : id;
				return tail.Length > 8 ? tail.Substring(0, 8) : tail;
			}

			// --- transform search (scoped to a subtree, never a whole-scene scan) ---
			private static bool IsActive(Transform t)
			{
				try { return !Dead(t) && t.gameObject.activeInHierarchy; } catch { return false; }
			}

			// Find our submenu inside the cached main-menu subtree (includes inactive), by name-contains.
			// Scoped and run once per world (the caller caches the result), so no per-frame scan.
			private static Transform FindInSubtree(Transform root, string contains)
			{
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (n.IndexOf(contains, StringComparison.Ordinal) >= 0) return t;
					}
				}
				catch { }
				return null;
			}

			// activeOnly: skip objects that are switched off. VRChat parks several complete copies of
			// a page side by side and turns on exactly one, so the FIRST match by name is very often a
			// dormant one -- the same trap as the inactive prefab row.
			private static Transform FindDescendantByName(Transform root, string name, bool activeOnly = false)
			{
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (n != name) continue;
						if (activeOnly)
						{
							bool live; try { live = t.gameObject.activeInHierarchy; } catch { continue; }
							if (!live) continue;
						}
						return t;
					}
				}
				catch { }
				return null;
			}

			// A LIVE ROW, NOT THE PREFAB TEMPLATE.
			//
			// GetComponentsInChildren(true) walks INACTIVE objects too, and VRChat keeps an inactive
			// prefab of every list row next to the live list ("Cell_MM_AvatarListSelector [off]" in the
			// menu capture). The first match was therefore the template: the clone was parented into
			// the template's container instead of the visible list, so the log honestly said "ARCHIVE
			// FAVORITES row added" while nothing appeared on screen. A live row is both the right thing
			// to copy AND the proof of the right parent, so active candidates win; the template is only
			// a last resort (better a wrong parent than no row at all on a build we have not seen).
			private static Transform FindDescendantByPrefix(Transform root, string prefix, string requireChild, bool requireText = false)
			{
				Transform fallback = null;
				try
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (Dead(t)) continue;
						string n; try { n = t.name; } catch { continue; }
						if (!n.StartsWith(prefix, StringComparison.Ordinal)) continue;
						if (n.StartsWith("VA_", StringComparison.Ordinal)) continue;   // never our own clone
						// NOT a name filter on "prefab": VRChat names the live clones
						// "Cell_MM_SidebarListItem prefab(Clone)", so skipping every name containing "prefab"
						// threw away exactly the rows we want and left only the inactive template. Active vs
						// inactive already separates a live row from a template, and it cannot be fooled by a
						// naming convention.
						if (!string.IsNullOrEmpty(requireChild) && Dead(t.Find(requireChild))) continue;
						if (requireText && Dead(t.GetComponentInChildren<TMPro.TMP_Text>(true))) continue;
						bool live = false;
						try { live = t.gameObject.activeInHierarchy; } catch { }
						if (!live) { if (Dead(fallback)) fallback = t; continue; }
						return t;
					}
				}
				catch { }
				return fallback;
			}

			private static TMPro.TMP_Text FindText(Transform root, string path)
			{
				try { var t = root.Find(path); return Dead(t) ? null : t.GetComponent<TMPro.TMP_Text>(); }
				catch { return null; }
			}
		}
	}
}
