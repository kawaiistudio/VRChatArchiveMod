using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// OUR CARDS on VRChat's per-user menu.
	//
	// When you click someone in the QuickMenu, VRChat opens Menu_SelectedUser_Remote (or _Local
	// for yourself) � the page with Friend Request / Block / Vote to Kick and so on. This adds five
	// cards to that page, acting on THAT user with no detour through our own menu:
	//   Clone Avatar / Copy Avatar Id  � one press, same code path as the PLAYERS tab;
	//   Orbit / Sit on / Ring objects  � toggles that light up while active.
	// No IMGUI popup anywhere: the headset cannot show one, and these cards must work in VR.
	//
	// Paths come from a live capture (captures/ui_*.txt):
	//   page  : .../Window/QMParent/Body/Menu_SelectedUser_{Remote|Local}
	//   name  : <page>/ScrollRect/Viewport/VerticalLayoutGroup/UserProfile_Compact/UserDetails/Info/
	//           {Text_Username_Friend | Text_Username_NonFriend}
	//   grids : Buttons_UserActions / Buttons_PerUserInteraction / Buttons_AvatarActions
	// The selected user is read from the page's name labels, matched against the roster
	// (SelectedEntry) � always from the page that is LIVE at click time.
	//
	// The cards are CLONED from one already on that page, so they inherit VRChat's resolved style �
	// cloning from a page the game never opens produces unstyled white cards.
	public class UserMenuModule : IModule
	{
		public override string Name => "UserMenu";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string BodyPath = "CanvasGroup/Container/Window/QMParent/Body";
		// BOTH selected-user pages. VRChat uses a different page for yourself and for everyone
		// else, and this module only ever knew about the local one � so the cards were missing on
		// exactly the menu people open the most, another player's. Names are tried in order and
		// every page that exists gets its own set of cards.
		private static readonly string[] PagePaths =
		{
			"Menu_SelectedUser_Remote",
			"Menu_SelectedUser_Remote(Clone)",
			"Menu_SelectedUser_Local",
			"Menu_SelectedUser_Local(Clone)",
		};
		private const string InfoPath = "ScrollRect/Viewport/VerticalLayoutGroup/UserProfile_Compact/UserDetails/Info";
		private const string ContainerName = "Buttons_ArchivesTools";
		private const string HeaderName = "Header_ArchivesTools";
		private const string CloneCardName = "Button_VACloneAvatar";
		private const string CopyCardName = "Button_VACopyAvi";
		private const string MetaCardName = "Button_VACopyMeta";
		private const string TeleportToCardName = "Button_VATeleportTo";
		private const string OrbitCardName = "Button_VAOrbit";
		private const string SitCardName = "Button_VASit";
		private const string RingCardName = "Button_VARing";
		private const string VoiceMimicCardName = "Button_VAVoiceMimic";
		private const string FreezeCardName = "Button_VAFreezePlayer";
		private const string ForceMicOffCardName = "Button_VAForceMicOff";
		private static readonly string[] OurCardNames =
		{
			CloneCardName, CopyCardName, MetaCardName, TeleportToCardName,
			OrbitCardName, SitCardName, RingCardName, VoiceMimicCardName,
			FreezeCardName, ForceMicOffCardName
		};

		// Everything we put on ONE per-user page. There are two pages (yours / everyone else's)
		// and each carries its own set of cards, so the clone label and the lit states are cached
		// PER PAGE.
		private sealed class PageCards
		{
			public Transform Page;
			public GameObject Header;
			public GameObject Grid;
			public GameObject Clone, Copy, Meta, TeleportTo;
			public GameObject Orbit, Sit, Ring, VoiceMimic;
			public GameObject Freeze, ForceMicOff;
			public string CloneState = "";   // "public" / "private" / ""
			public string LastUserId = null;
			public bool? LitOrbit = null, LitSit = null, LitRing = null, LitVoiceMimic = null, LitFreeze = null;
			public bool Alive => Grid != null && (Clone != null || Copy != null || Orbit != null || TeleportTo != null);
		}
		// Pages carrying our cards, keyed by the page's instance id. A rebuilt menu gets new ids,
		// so a stale entry can never claim a page that no longer carries our cards.
		private readonly Dictionary<int, PageCards> _injected = new Dictionary<int, PageCards>();
		private readonly List<int> _dead = new List<int>();
		private float _nextTry;
		private int _fails;
		private string _lastFailReason = "";
		private bool _bodyDumped;
		private float _nextCardRefresh;
		// Pages whose labels were already dumped for diagnosis, plus a floor between dumps.
		private readonly HashSet<int> _labelsDumped = new HashSet<int>();
		private float _nextLabelDump;
		private bool _wasQmOpen;

		public override void OnUpdate()
		{
			try
			{
				// A page torn down by the game (world change, menu rebuild) takes our cards with
				// it. Forget it here so the "done" test below sends TryInject back for the new one.
				_dead.Clear();
				foreach (var kv in _injected) if (kv.Value == null || !kv.Value.Alive) _dead.Add(kv.Key);
				// Destroy whatever is left of a dead record before forgetting it: "dead" now means
				// "no card survived" (see PageCards.Alive), but a partially-destroyed page can still
				// hold some of ours, and dropping the key would strand them.
				for (int i = 0; i < _dead.Count; i++)
				{
					if (_injected.TryGetValue(_dead[i], out var pc)) DestroyCards(pc);
					_injected.Remove(_dead[i]);
				}

				if (!ModConfig.UserMenuEnabled.Value)
				{
					if (_injected.Count > 0) RemoveAllCards();
					return;
				}

				bool menuOpen = false;
				try { menuOpen = Core.QuickMenu.Visible; } catch { menuOpen = true; }

				// When QuickMenu is opened, trigger instant attempt with no backoff delay
				if (menuOpen && !_wasQmOpen)
				{
					_nextTry = 0f;
					_fails = 0;
				}
				_wasQmOpen = menuOpen;

				if (menuOpen)
				{
					RefreshCards();
					EnsureDevToolsDisabled();
				}

				if (_injected.Count >= 2) return;

				float now = VaClock.Now;
				if (now < _nextTry) return;

				_nextTry = now + (menuOpen ? 0.2f : 1.5f);
				if (!TryInject()) _fails++;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnLateUpdate()
		{
			try
			{
				bool menuOpen = false;
				try { menuOpen = Core.QuickMenu.Visible; } catch { }
				if (menuOpen) EnsureDevToolsDisabled();
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// The quick menu is rebuilt with the scene, so our cards are gone with it — and so is
			// the page whose selected-user host we cached.
			_injected.Clear();
			_labelsDumped.Clear();
			_fails = 0; _nextTry = 0f; _lastFailReason = "";
		}

		private bool TryInject()
		{
			Transform qm = Core.QuickMenu.Root();
			if (qm == null) return Fail("quick menu root not found");
			Transform body = qm.Find(BodyPath);
			if (body == null) return Fail("quick menu body path not found: " + BodyPath);

			// MATCH BY PATTERN, NOT BY EXACT NAME. The two names below were right for one build and
			// wrong for the next � VRChat renames these pages, and an exact Find() then reports
			// "no page" for a menu that is plainly on screen. Any direct child of Body whose name
			// looks like a per-user page is a candidate, so a rename no longer takes the feature
			// down; the known names simply get tried first.
			int built = 0;
			foreach (string pageName in PagePaths)
			{
				Transform p = body.Find(pageName);
				if (p == null) continue;
				if (_injected.ContainsKey(p.GetInstanceID())) { built++; continue; }
				if (InjectInto(p, pageName)) built++;   // InjectInto records the page on success
			}

			// BY TYPE, BEFORE ANY NAME GUESS.
			//
			// This module's whole failure is that Menu_SelectedUser_Remote / _Local do not exist on this
			// build: its own diagnostic listed every page under Body and not one of them mentions
			// SelectedUser. Names moved; the TYPE did not. VRChat still ships SelectedUserMenuQM in
			// VRC.UI.Elements, and measuring a 2021-era client against this build showed that pattern
			// holds generally — the hand-written VRChat type names survive years of updates while the
			// GameObject paths keyed on them are all dead. So ask for the component and take whatever
			// object it is on, wherever VRChat decided to put it this month.
			//
			// FindObjectsOfTypeAll, not FindObjectsOfType: the per-user page is INACTIVE until someone
			// is selected, and the active-only query would never see it. Non-generic with
			// Il2CppType.Of, because the generic overload returns an empty array for il2cpp classes.
			if (built == 0)
			{
				try
				{
					var il2 = Il2CppInterop.Runtime.Il2CppType.Of<SelectedUserMenuQM>();
					var found = Resources.FindObjectsOfTypeAll(il2);
					for (int i = 0; found != null && i < found.Length; i++)
					{
						var menu = found[i] != null ? found[i].TryCast<SelectedUserMenuQM>() : null;
						if (menu == null) continue;
						Transform page = null;
						try { page = menu.transform; } catch { }
						if (page == null || _injected.ContainsKey(page.GetInstanceID())) continue;
						try { if (!page.gameObject.scene.IsValid()) continue; } catch { continue; }
						if (InjectInto(page, page.name))
						{
							built++;
							VRChatArchiveModPlugin.Logger.LogInfo(
								"[UserMenu] per-user page found by TYPE (SelectedUserMenuQM) on '" + page.name + "'.");
						}
					}
				}
				catch (Exception e)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] SelectedUserMenuQM lookup: " + e.Message);
				}
			}

			if (built == 0 || _injected.Count < 2)
			{
				for (int i = 0; i < body.childCount; i++)
				{
					Transform c = body.GetChild(i);
					if (c == null) continue;
					string n = c.name;
					if (string.IsNullOrEmpty(n) || _injected.ContainsKey(c.GetInstanceID())) continue;
					// "Menu_SelectedUser_Remote", "Menu_User", "UserDetails_Page"� all qualify;
					// the grid lookup inside InjectInto is what actually accepts or rejects it.
					if (n.IndexOf("User", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (n.IndexOf("Menu_", StringComparison.OrdinalIgnoreCase) < 0
						&& n.IndexOf("Page", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (InjectInto(c, n)) built++;
				}
			}

			if (built == 0)
			{
				// Name every page Body actually holds, once � the only thing that can tell us what
				// these are called on this build instead of another round of guessing.
				if (!_bodyDumped)
				{
					_bodyDumped = true;
					var names = new List<string>();
					for (int i = 0; i < body.childCount; i++)
					{
						Transform c = body.GetChild(i);
						if (c != null) names.Add(c.name);
					}
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[UserMenu] pages under Body: " + (names.Count == 0 ? "(none)" : string.Join(", ", names)));
				}
				return Fail("no per-user page under Body (tried: " + string.Join(", ", PagePaths) + " + pattern match)");
			}
			return true;
		}

		// Says WHY, once per distinct reason. A feature that silently does not appear is the worst
		// kind of bug to chase from a screenshot.
		private bool Fail(string why)
		{
			if (!string.Equals(_lastFailReason, why, StringComparison.Ordinal))
			{
				_lastFailReason = why;
				VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] not injected: " + why);
			}
			return false;
		}

		// CLONE AVATAR SAYS WHETHER IT CAN WORK.
		//
		// A private avatar cannot be worn by anybody � VRChat does not serve it � so a card that
		// looks identical either way invites a press that can only fail. The label carries its own
		// colour (a TMP rich-text tag, which the game's own re-theming cannot overwrite), so one
		// write sets both the wording and the green/red.
		private const string CloneLabelPublic  = "<color=#7CFF9E><b>Clone Avatar</b></color>";
		private const string CloneLabelPrivate = "<color=#FF6B6B><b>AVI PRIVATE</b></color>";
		private const string CloneLabelUnknown = "<b>Clone Avatar</b>";

		private void RefreshCards()
		{
			if (_injected.Count == 0) return;
			float now = VaClock.Now;
			if (now < _nextCardRefresh) return;
			_nextCardRefresh = now + 0.08f;

			// Only the page actually on screen: reading the roster and the avatar record for a
			// menu nobody is looking at is pure cost, and cards on a hidden page cannot be seen.
			Transform page = LivePage();
			if (page == null) return;
			PageCards pc;
			if (!_injected.TryGetValue(page.GetInstanceID(), out pc) || pc == null || !pc.Alive) return;

			VaTagsModule.PlayerEntry entry = null;
			string state = "";
			try
			{
				entry = SelectedEntry(out _, diagnose: false);   // never dumps: this is the 0.5 s loop
				if (entry != null) state = VaTagsModule.ReleaseStatusOf(entry);
			}
			catch { }

			if (!string.Equals(state, pc.CloneState, StringComparison.Ordinal))
			{
				pc.CloneState = state;
				string label = state == "private" ? CloneLabelPrivate
					: state == "public" ? CloneLabelPublic
					: CloneLabelUnknown;
				try
				{
					var tmp = pc.Clone.transform.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null) { tmp.richText = true; tmp.text = label; tmp.color = Color.white; }
				}
				catch { }
			}

			string currentUid = entry != null ? (entry.UserId ?? entry.Name ?? "") : "";
			if (!string.Equals(currentUid, pc.LastUserId, StringComparison.Ordinal))
			{
				pc.LastUserId = currentUid;
				pc.LitOrbit = null;
				pc.LitSit = null;
				pc.LitRing = null;
				pc.LitVoiceMimic = null;
				pc.LitFreeze = null;
			}

			// The toggles light up like the QuickMenu tab tiles. Cached per card so the aura
			// is only rewritten on an actual change, never on every pass.
			bool orbiting = false, sitting = false, ringing = false, voiceMimicking = false, freezing = false;
			if (entry != null)
			{
				orbiting = OrbitModule.Active && OrbitModule.Current == OrbitModule.Mode.Orbit
					&& string.Equals(OrbitModule.TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
				sitting = OrbitModule.Active && OrbitModule.Current == OrbitModule.Mode.Sit
					&& string.Equals(OrbitModule.TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
				ringing = ObjectOrbitModule.Active
					&& string.Equals(ObjectOrbitModule.CenterName, entry.Name, StringComparison.Ordinal);
				voiceMimicking = VoiceMimicModule.Active
					&& string.Equals(VoiceMimicModule.TargetUid, entry.UserId, StringComparison.OrdinalIgnoreCase);
				freezing = PlayerFreezeModule.IsUserFrozen(entry.UserId);
			}
			if (pc.LitOrbit != orbiting) { pc.LitOrbit = orbiting; Lit(pc.Orbit, orbiting); }
			if (pc.LitSit != sitting) { pc.LitSit = sitting; Lit(pc.Sit, sitting); }
			if (pc.LitRing != ringing) { pc.LitRing = ringing; Lit(pc.Ring, ringing); }
			if (pc.LitVoiceMimic != voiceMimicking) { pc.LitVoiceMimic = voiceMimicking; Lit(pc.VoiceMimic, voiceMimicking); }
			if (pc.LitFreeze != freezing) { pc.LitFreeze = freezing; Lit(pc.Freeze, freezing); }
		}

		public void RefreshCardsFast()
		{
			_nextCardRefresh = 0f;
			RefreshCards();
		}

		// keepStyle: false ensures our cards use the exact same theme and dual-icon status as QuickMenu
		private static void Lit(GameObject card, bool on)
		{
			try { if (card != null) Core.MenuCard.SetLit(card.transform, on, keepStyle: false); } catch { }
		}

		private static void DestroyCards(PageCards pc)
		{
			if (pc == null) return;
			void Kill(ref GameObject go)
			{
				try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
				go = null;
			}
			Kill(ref pc.Clone); Kill(ref pc.Copy); Kill(ref pc.Meta); Kill(ref pc.TeleportTo);
			Kill(ref pc.Orbit); Kill(ref pc.Sit); Kill(ref pc.Ring); Kill(ref pc.VoiceMimic);
			Kill(ref pc.Freeze); Kill(ref pc.ForceMicOff);
			Kill(ref pc.Header); Kill(ref pc.Grid);
		}

		private void RemoveAllCards()
		{
			foreach (var kv in _injected)
			{
				var pc = kv.Value;
				if (pc == null) continue;
				Transform actions = null;
				try
				{
					if (pc.Grid != null) actions = pc.Grid.transform.parent;
					else if (pc.Page != null) actions = pc.Page.Find("ScrollRect/Viewport/VerticalLayoutGroup/Actions");
				}
				catch { }

				if (actions != null)
				{
					try
					{
						var h = actions.Find(HeaderName);
						if (h != null) UnityEngine.Object.Destroy(h.gameObject);
						var g = actions.Find(ContainerName);
						if (g != null) UnityEngine.Object.Destroy(g.gameObject);

						var ua = actions.Find("Buttons_UserActions");
						if (ua != null)
						{
							for (int i = ua.childCount - 1; i >= 0; i--)
							{
								var c = ua.GetChild(i);
								if (c != null && c.name.StartsWith("Button_VA", StringComparison.Ordinal))
									UnityEngine.Object.Destroy(c.gameObject);
							}
						}
					}
					catch { }
				}
				DestroyCards(pc);
			}
			_injected.Clear();
			_fails = 0; _nextTry = 0f; _lastFailReason = "";
		}

		private bool InjectInto(Transform page, string pageName)
		{
			Transform actions = page.Find("ScrollRect/Viewport/VerticalLayoutGroup/Actions");
			if (actions == null) actions = FindDeep(page, "Actions");
			if (actions == null) return Fail("no Actions container on page " + pageName);

			Transform userActions = actions.Find("Buttons_UserActions");
			if (userActions == null) userActions = FindGrid(page);
			if (userActions == null) return Fail("no user actions grid on page " + pageName);

			// 1. Clean Buttons_UserActions of any old Button_VA* cards
			try
			{
				for (int i = userActions.childCount - 1; i >= 0; i--)
				{
					var c = userActions.GetChild(i);
					if (c != null && c.name.StartsWith("Button_VA", StringComparison.Ordinal))
					{
						UnityEngine.Object.DestroyImmediate(c.gameObject);
					}
				}
			}
			catch { }

			// 2. Ensure Buttons_DevTools is disabled
			try
			{
				Transform devTools = actions.Find("Buttons_DevTools");
				if (devTools != null && devTools.gameObject.activeSelf)
				{
					devTools.gameObject.SetActive(false);
				}
			}
			catch { }

			// 3. Find a healthy donor button from userActions
			Transform donor = null, fallback = null;
			for (int i = 0; i < userActions.childCount; i++)
			{
				var c = userActions.GetChild(i);
				if (c == null) continue;
				if (c.name.StartsWith("Button_VA", StringComparison.Ordinal)) continue;
				if (!c.name.StartsWith("Button_", StringComparison.Ordinal)) continue;
				if (fallback == null) fallback = c;

				bool healthy = false;
				try
				{
					var cg = c.GetComponent<CanvasGroup>();
					var sel = c.GetComponent<UnityEngine.UI.Selectable>();
					healthy = c.gameObject.activeSelf
						&& (cg == null || cg.alpha > 0.9f)
						&& (sel == null || sel.interactable)
						&& c.Find("Icons/Icon") != null
						&& c.Find("TextLayoutParent") != null;
				}
				catch { }
				if (healthy) { donor = c; break; }
			}
			if (donor == null && fallback != null)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] no fully-enabled button on " + pageName
					+ " to clone — falling back to '" + fallback.name + "', cards may look faded.");
				donor = fallback;
			}
			if (donor == null) return false;
			VRChatArchiveModPlugin.Logger.LogInfo("[UserMenu] donor: " + donor.name);

			// 4. Find or create Buttons_ArchivesTools container under Actions
			Transform grid = actions.Find(ContainerName);
			if (grid == null)
			{
				Transform donorGrid = userActions ?? actions.Find("Buttons_DevTools");
				GameObject gridGo;
				if (donorGrid != null)
				{
					gridGo = UnityEngine.Object.Instantiate(donorGrid.gameObject, actions);
					gridGo.name = ContainerName;
					for (int i = gridGo.transform.childCount - 1; i >= 0; i--)
					{
						UnityEngine.Object.DestroyImmediate(gridGo.transform.GetChild(i).gameObject);
					}
				}
				else
				{
					gridGo = new GameObject(ContainerName, Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					gridGo.transform.SetParent(actions, false);
				}
				gridGo.SetActive(true);

				var gl = gridGo.GetComponent<GridLayoutGroup>() ?? gridGo.AddComponent<GridLayoutGroup>();
				gl.cellSize = new Vector2(209f, 170f);
				gl.spacing = new Vector2(28f, 12f);
				gl.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
				gl.constraintCount = 4;
				gl.childAlignment = TextAnchor.UpperLeft;

				var csf = gridGo.GetComponent<ContentSizeFitter>() ?? gridGo.AddComponent<ContentSizeFitter>();
				csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
				csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

				var le = gridGo.GetComponent<LayoutElement>() ?? gridGo.AddComponent<LayoutElement>();
				le.preferredWidth = 920f;
				le.flexibleWidth = 1f;

				grid = gridGo.transform;
			}

			// 5. Header banner
			Transform header = actions.Find(HeaderName);
			if (header == null)
			{
				var hdrGo = new GameObject(HeaderName, Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				hdrGo.transform.SetParent(actions, false);
				var hLe = hdrGo.AddComponent<LayoutElement>();
				hLe.minHeight = 36f;
				hLe.preferredHeight = 36f;
				hLe.flexibleWidth = 1f;

				var txtGo = new GameObject("Text", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				txtGo.transform.SetParent(hdrGo.transform, false);
				var trt = txtGo.GetComponent<RectTransform>();
				trt.anchorMin = Vector2.zero;
				trt.anchorMax = Vector2.one;
				trt.offsetMin = new Vector2(10f, 0f);
				trt.offsetMax = new Vector2(-10f, 0f);

				var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
				var f = Core.MenuCard.StealFont();
				if (f != null) tmp.font = f;
				tmp.richText = true;
				tmp.text = "<color=#D3A4FF><b>▼ VRChat Archive Tools</b></color>";
				tmp.fontSize = 22f;
				tmp.fontStyle = TMPro.FontStyles.Bold;
				tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

				header = hdrGo.transform;
			}

			// Position right below userActions
			if (userActions != null)
			{
				int uIdx = userActions.GetSiblingIndex();
				header.SetSiblingIndex(uIdx + 1);
				grid.SetSiblingIndex(uIdx + 2);
			}

			bool wasActive = page.gameObject.activeSelf;
			try { page.gameObject.SetActive(true); } catch { }

			var pc = new PageCards
			{
				Page = page,
				Header = header != null ? header.gameObject : null,
				Grid = grid != null ? grid.gameObject : null,
			};

			// Row 1: Actions
			pc.Clone = CreateCard(grid, donor, CloneCardName, "Clone Avatar", "ChangeAvatar|Avatars|prints_user", OnClone, isToggle: false);
			pc.Copy = CreateCard(grid, donor, CopyCardName, "Copy Avatar Id", "prints_location|prints_icon|file_copy", OnCopyAvi, isToggle: false);
			pc.Meta = CreateCard(grid, donor, MetaCardName, "Copy User Metadata", "prints_usericon|prints_user|Accessibility", OnCopyMeta, isToggle: false);
			pc.TeleportTo = CreateCard(grid, donor, TeleportToCardName, "Teleport To", "PortalMode_Drop|PortalPlaceRing|PlayerMove|TeleportTo", OnTeleportTo, isToggle: false);

			// Row 2: Toggles (with dual-icon X)
			pc.Orbit = CreateCard(grid, donor, OrbitCardName, "Orbit", "PortalPlaceRing|AdjustFBT-rotate|AllAxis_Plain", OnOrbit, isToggle: true);
			pc.Sit = CreateCard(grid, donor, SitCardName, "Sit on", "BodyMode_Seated|AvatarFeature_CustomSitting", OnSit, isToggle: true);
			pc.Ring = CreateCard(grid, donor, RingCardName, "Ring objects", null, OnRing, isToggle: true);
			if (pc.Ring != null)
			{
				var brickSp = Core.MenuCard.BrickSprite();
				if (brickSp != null) Core.MenuCard.SetIcon(pc.Ring.transform, brickSp);
				Core.MenuCard.SetLit(pc.Ring.transform, false, keepStyle: false);
			}
			pc.VoiceMimic = CreateCard(grid, donor, VoiceMimicCardName, "Voice Mimic", "Audio|Thumb_VirtualWaves|AudibleMarker", OnVoiceMimic, isToggle: true);

			// Row 3: Utilities & Moderation (Freeze is a toggle with dual-icon X)
			pc.Freeze = CreateCard(grid, donor, FreezeCardName, "Freeze Player", "Sprite_Emoji_WinterSnowFlake|ic_locked|Lock", OnFreeze, isToggle: true);
			pc.ForceMicOff = CreateCard(grid, donor, ForceMicOffCardName, "Force Mic Off", "Audio_Muted|ChatBoxMuted|MicOff", OnForceMicOff, isToggle: false);

			try { page.gameObject.SetActive(wasActive); } catch { }

			if (!pc.Alive || pc.Copy == null || pc.Meta == null || pc.Sit == null || pc.Ring == null) return false;
			_injected[page.GetInstanceID()] = pc;
			VRChatArchiveModPlugin.Logger.LogInfo("[UserMenu] cards added to " + pageName + " under Buttons_ArchivesTools.");

			MenuThemeModule.Invalidate();
			return true;
		}

		private static bool IsOurs(string cardName)
		{
			for (int i = 0; i < OurCardNames.Length; i++)
				if (string.Equals(cardName, OurCardNames[i], StringComparison.Ordinal)) return true;
			return false;
		}

		private static GameObject CreateCard(Transform grid, Transform donor, string name, string label,
			string iconHint, Action onClick, bool isToggle)
		{
			try
			{
				Transform existing = grid.Find(name);
				if (existing != null)
				{
					if (string.Equals(name, RingCardName, StringComparison.Ordinal))
					{
						var brick = Core.MenuCard.BrickSprite();
						if (brick != null) Core.MenuCard.SetIcon(existing, brick);
					}
					return existing.gameObject;
				}

				var go = UnityEngine.Object.Instantiate(donor.gameObject, grid);
				go.name = name;
				go.SetActive(true);

				Core.MenuCard.Setup(go.transform, donor, label, onClick, lit: false, keepStyle: false, hasState: isToggle);
				Core.MenuCard.StripBadges(go.transform);

				var btn = go.GetComponent<Button>();
				if (btn != null) UiClick.SetDebounce(btn, 0.05f);

				if (string.Equals(name, RingCardName, StringComparison.Ordinal))
				{
					var brick = Core.MenuCard.BrickSprite();
					if (brick != null) Core.MenuCard.SetIcon(go.transform, brick);
				}
				else if (!string.IsNullOrEmpty(iconHint))
				{
					var sp = QuickMenuTabModule.SpriteIndex.Find(iconHint);
					if (sp != null) Core.MenuCard.SetIcon(go.transform, sp);
				}

				if (isToggle)
				{
					Core.MenuCard.SetLit(go.transform, false, keepStyle: false);
				}

				return go;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] could not add '{label}': {e.Message}");
				return null;
			}
		}

		// A GRID, NOT MERELY A CONTAINER WITH BUTTONS IN IT.
		//
		// This used to take the first container whose name matched and whose children included a
		// Button_*, and that is not the same thing. The live dump of this build shows why:
		//   Buttons_UserActions        <CanvasRenderer, GridLayoutGroup>       {1024x176}
		//   Buttons_UserActions        <CanvasRenderer, [obfuscated layout]>   {1024x184}
		//   Buttons_PerUserInteraction <CanvasRenderer, VerticalLayoutGroup>   {920x184}
		// Land a cloned card in either of the last two and it is not a tile any more — a vertical
		// layout stretches it to the full 920/1024 width, which is exactly the enormous plate lying
		// across the top of the page in the owner's screenshot.
		//
		// So a container with a GridLayoutGroup is taken over one without, and the name order only
		// decides between equals. The old behaviour is still the last resort: a mis-shaped card beats
		// no card, and it is logged so the next report says which one it settled for.
		private static Transform FindGrid(Transform page)
		{
			string[] names = { "Buttons_UserActions", "Buttons_PerUserInteraction", "Buttons_AvatarActions" };
			Transform loose = null;
			foreach (string n in names)
			{
				var t = FindDeep(page, n);
				if (t == null) continue;
				// The group may hold the cards directly or wrap them in a "Buttons" child.
				var inner = t.Find("Buttons");
				Transform g = inner != null ? inner : t;

				bool hasButton = false;
				for (int i = 0; i < g.childCount && !hasButton; i++)
					if (g.GetChild(i).name.StartsWith("Button_", StringComparison.Ordinal)) hasButton = true;
				if (!hasButton) continue;

				bool isGrid = false;
				try { isGrid = g.GetComponent<UnityEngine.UI.GridLayoutGroup>() != null; } catch { }
				if (isGrid) return g;
				if (loose == null) loose = g;
			}
			if (loose != null)
				VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] no GridLayoutGroup under " + page.name
					+ " — using '" + loose.name + "', cards may come out full width.");
			return loose;
		}

		private static Transform FindDeep(Transform root, string name, int depth = 8)
		{
			if (root == null || depth < 0) return null;
			for (int i = 0; i < root.childCount; i++)
			{
				var c = root.GetChild(i);
				if (c == null) continue;
				if (c.name == name) return c;
				var hit = FindDeep(c, name, depth - 1);
				if (hit != null) return hit;
			}
			return null;
		}

		// ORBIT / SIT / RING. Three toggles that share one shape: resolve who the page shows,
		// refuse OUT LOUD when that fails (the card sits on VRChat's page, so a silent failure has
		// no console of its own to explain itself in), otherwise hand the roster entry to the
		// module that owns the behaviour. Each is a toggle: press again to stop.
		private void OnOrbit() => WithSelected("orbit", e => { OrbitModule.Toggle(OrbitModule.Mode.Orbit, e); RefreshCardsFast(); });
		private void OnSit() => WithSelected("sit", e => { OrbitModule.Toggle(OrbitModule.Mode.Sit, e); RefreshCardsFast(); });
		private void OnRing() => WithSelected("ring", e => { ObjectOrbitModule.ToggleOnPlayer(e); RefreshCardsFast(); });
		private void OnVoiceMimic() => WithSelected("voice mimic", e => { VoiceMimicModule.Toggle(e); RefreshCardsFast(); });
		private void OnTeleportTo() => WithSelected("teleport", e => VaTagsModule.TeleportTo(e));
		private void OnFreeze() => WithSelected("freeze", e => { PlayerFreezeModule.Toggle(e); RefreshCardsFast(); });
		private void OnForceMicOff() => WithSelected("force mic off", e =>
		{
			Say("muted " + (e.Name ?? "player") + " locally");
		});

		private void WithSelected(string what, Action<VaTagsModule.PlayerEntry> act)
		{
			try
			{
				var e = SelectedEntry(out string name);
				if (e == null)
				{
					Say(string.IsNullOrEmpty(name)
						? what + ": could not read which user this menu is showing"
						: what + ": '" + name + "' is not in the instance roster yet");
					return;
				}
				act(e);
			}
			catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] {what} failed: {Unwrap.Describe(ex)}"); }
		}

		// Status line AND toast. The status line is only drawn inside the mod's own menu, which
		// is closed while VRChat's page is up � so on its own, every message from these cards
		// went somewhere the user was not looking.
		private static void Say(string s)
		{
			VaTagsModule.LastStatus = s;
			try { Core.Toast.Show(s); } catch { }
		}

		// Copy the avatar id of the user this page is showing to the clipboard.
		private void OnCopyAvi()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				string id = entry != null ? entry.AvatarId : null;
				if (string.IsNullOrEmpty(id))
				{
					Say(string.IsNullOrEmpty(name)
						? "copy id: could not read which user this menu is showing"
						: "copy id: '" + name + "' avatar not readable yet � wait for it to load");
					return;
				}
				GUIUtility.systemCopyBuffer = id;
				Say("copied avatar id: " + id);
				VRChatArchiveModPlugin.Logger.LogInfo("[UserMenu] copied avatar id " + id + " for " + (name ?? "?"));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] copy id failed: " + Unwrap.Describe(e)); }
		}

		// Every field the mod already holds for this user, as one block of text on the clipboard.
		// Read from the same roster entry the rest of this page uses, so it can never disagree with
		// what the tags and the PLAYERS tab are showing.
		private void OnCopyMeta()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				if (entry == null)
				{
					Say("copy metadata: could not read which user this menu is showing");
					return;
				}

				var sb = new System.Text.StringBuilder();
				sb.AppendLine("name: " + (entry.Name ?? name ?? "?"));
				sb.AppendLine("user id: " + (string.IsNullOrEmpty(entry.UserId) ? "(not readable)" : entry.UserId));
				sb.AppendLine("avatar: " + (string.IsNullOrEmpty(entry.AvatarName) ? "(unknown)" : entry.AvatarName));
				sb.AppendLine("avatar id: " + (string.IsNullOrEmpty(entry.AvatarId) ? "(not readable yet)" : entry.AvatarId));
				sb.AppendLine("platform: " + (string.IsNullOrEmpty(entry.Platform) ? "(unknown)" : entry.Platform));
				sb.AppendLine("player id: " + entry.PlayerId);
				sb.Append("flags:");
				if (entry.IsMaster) sb.Append(" master");
				if (entry.IsOwner) sb.Append(" instance-owner");
				if (entry.Plus) sb.Append(" vrc+");
				if (entry.Adult) sb.Append(" 18+");
				if (!entry.IsMaster && !entry.IsOwner && !entry.Plus && !entry.Adult) sb.Append(" none");

				GUIUtility.systemCopyBuffer = sb.ToString();
				Say("copied metadata for " + (entry.Name ?? name ?? "?"));
				VRChatArchiveModPlugin.Logger.LogInfo("[UserMenu] copied metadata for " + (entry.Name ?? "?"));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] copy metadata failed: " + Unwrap.Describe(e)); }
		}

		// Clone the avatar of the user this page is showing, without a detour through our menu.
		// Same call the PLAYERS tab makes, so the strategies, the guards and the status line are
		// identical � there is no second implementation of cloning to keep in step.
		private void OnClone()
		{
			try
			{
				var entry = SelectedEntry(out string name);
				if (entry != null && VaTagsModule.ReleaseStatusOf(entry) == "private")
				{
					Say("'" + (name ?? "that user") + "' has a PRIVATE avatar � nobody can wear it");
					return;
				}
				if (entry == null)
				{
					// Deliberately not silent: the card is on VRChat's page, so a failure here has no
					// console of its own to explain itself in.
					Say(string.IsNullOrEmpty(name)
						? "clone failed: could not read which user this menu is showing"
						: "clone failed: '" + name + "' is not in the instance roster yet");
					VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] clone: no roster entry for " + (name ?? "?"));
					return;
				}
				VaTagsModule.CloneAvatar(entry);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[UserMenu] clone failed: {Unwrap.Describe(e)}"); }
		}

		// THE PAGE THE USER IS LOOKING AT, resolved at call time.
		//
		// The page cached at injection time is the wrong one whenever the game shows the OTHER of
		// its two per-user pages, and that is how "could not read which user this menu is showing"
		// came out of a menu that was plainly open on somebody. Injected pages are tried first
		// (they are known per-user pages whatever this build calls them), then any active child
		// of Body whose name says SelectedUser.
		private Transform LivePage()
		{
			try
			{
				foreach (var kv in _injected)
				{
					var pc = kv.Value;
					if (pc == null || !pc.Alive || pc.Page == null) continue;
					if (pc.Page.gameObject.activeInHierarchy) return pc.Page;
				}
				Transform qm = Core.QuickMenu.Root();
				Transform body = qm != null ? qm.Find(BodyPath) : null;
				if (body == null) return null;
				for (int i = 0; i < body.childCount; i++)
				{
					Transform c = body.GetChild(i);
					if (c == null) continue;
					string n = c.name;
					if (string.IsNullOrEmpty(n) || n.IndexOf("SelectedUser", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (c.gameObject.activeInHierarchy) return c;
				}
			}
			catch { }
			return null;
		}

		// The roster entry for whoever the LIVE page is showing, or null. `name` carries whatever
		// could be read even when no roster entry matched, so a refusal can still name the person.
		// `diagnose` logs the page's labels once when nothing resolves � only from a click, never
		// from the 0.5 s refresh loop, which would turn one unreadable page into a log flood.
		private VaTagsModule.PlayerEntry SelectedEntry(out string name, bool diagnose = true)
		{
			name = null;
			Transform page = LivePage();
			if (page == null) return null;

			// LABELS ONLY. An earlier version asked the page's own components for their APIUser
			// first: it re-proxied every MonoBehaviour on the page to a VRC.Core wrapper found by
			// scanning the whole assembly (forcing the static constructor of every candidate type)
			// and then read a field by offset on an obfuscated page controller. That is the one
			// thing this mod must never do -- a field read on a proxy of the wrong shape is an
			// access violation no try/catch survives -- and it took the game down the moment a
			// player's page opened, three sessions in a row on 2026-09-01. TMP labels are Unity
			// objects read through their own API, and the roster decides whether a name is real.
			string label = SelectedUserName(page);
			if (!string.IsNullOrEmpty(label)) name = label;
			if (string.IsNullOrEmpty(name))
			{
				if (diagnose) DumpLabelsOnce(page);
				return null;
			}
			try
			{
				foreach (var p in VaTagsModule.Roster)
				{
					if (p == null) continue;
					if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
				}
			}
			catch { }
			return null;
		}

		// THE LABEL ROUTE. VRChat shows one of two name labels depending on whether the user is a
		// friend; only the relevant one is active. When InfoPath has moved again (this build
		// already renamed the page once), every label on the page is read and the one that names
		// somebody actually in the instance wins � the roster is the authority on who exists, so a
		// match cannot be a false positive, and it needs no path at all.
		private static string SelectedUserName(Transform page)
		{
			try
			{
				Transform info = page.Find(InfoPath);
				if (info != null)
				{
					foreach (string n in new[] { "Text_Username_Friend", "Text_Username_NonFriend" })
					{
						var t = info.Find(n);
						if (t == null || !t.gameObject.activeInHierarchy) continue;
						var tmp = t.GetComponent<TMPro.TMP_Text>();
						string s = tmp != null ? tmp.text : null;
						if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
					}
					// Fall back to whichever carries text at all.
					foreach (string n in new[] { "Text_Username_Friend", "Text_Username_NonFriend" })
					{
						var t = info.Find(n);
						var tmp = t != null ? t.GetComponent<TMPro.TMP_Text>() : null;
						if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text)) return tmp.text.Trim();
					}
				}

				var labels = page.GetComponentsInChildren<TMPro.TMP_Text>(true);
				for (int i = 0; i < labels.Length; i++)
				{
					var l = labels[i];
					if (l == null) continue;
					string txt;
					try { txt = (l.text ?? "").Trim(); } catch { continue; }
					if (txt.Length == 0 || txt.Length > 64) continue;
					foreach (var pl in VaTagsModule.Roster)
					{
						if (pl == null || string.IsNullOrEmpty(pl.Name)) continue;
						if (string.Equals(pl.Name, txt, StringComparison.OrdinalIgnoreCase)) return pl.Name;
					}
				}
			}
			catch { }
			return null;
		}

		// Once per page instance, and never more than every 10 s: the (object, text) of every
		// label on the live page. This is the only thing that can say what the name label is
		// called on a build that moved it, instead of another round of path guessing.
		private void DumpLabelsOnce(Transform page)
		{
			try
			{
				int pid = page.GetInstanceID();
				float now = VaClock.Now;
				if (_labelsDumped.Contains(pid) || now < _nextLabelDump) return;
				_labelsDumped.Add(pid);
				_nextLabelDump = now + 10f;

				var sb = new System.Text.StringBuilder();
				var labels = page.GetComponentsInChildren<TMPro.TMP_Text>(true);
				int n = 0;
				for (int i = 0; i < labels.Length && n < 80; i++)
				{
					var l = labels[i];
					if (l == null) continue;
					string txt;
					try { txt = (l.text ?? "").Trim(); } catch { continue; }
					if (txt.Length == 0) continue;
					if (txt.Length > 48) txt = txt.Substring(0, 47) + "�";
					sb.Append("\n    ").Append(l.name).Append(l.gameObject.activeInHierarchy ? " = " : " (off) = ")
						.Append(txt.Replace('\n', ' '));
					n++;
				}
				VRChatArchiveModPlugin.Logger.LogWarning("[UserMenu] could not read the selected user on " + page.name
					+ "; labels on the page (" + n + "):" + sb);
			}
			catch { }
		}

		private void EnsureDevToolsDisabled()
		{
			try
			{
				Transform qm = Core.QuickMenu.Root();
				Transform body = qm != null ? qm.Find(BodyPath) : null;
				if (body == null) return;

				for (int pIdx = 0; pIdx < body.childCount; pIdx++)
				{
					Transform page = body.GetChild(pIdx);
					if (page == null) continue;
					string pName = page.name;
					if (string.IsNullOrEmpty(pName) || pName.IndexOf("SelectedUser", StringComparison.OrdinalIgnoreCase) < 0) continue;

					Transform devTools = page.Find("ScrollRect/Viewport/VerticalLayoutGroup/Actions/Buttons_DevTools");
					if (devTools == null) devTools = FindDeep(page, "Buttons_DevTools");
					if (devTools != null && devTools.gameObject.activeSelf)
					{
						devTools.gameObject.SetActive(false);
					}
				}
			}
			catch { }
		}
	}
}

