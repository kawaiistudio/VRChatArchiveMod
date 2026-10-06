using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// THE OBJECTS WE CLONE, RESOLVED ONCE.
	//
	// Our UI is built by hand — new GameObject, new RectTransform, our own sprites — and it shows: it
	// does not inherit VRChat's font, material, hover animation, click sound or theme, and two
	// injections fail outright on every world load (ArchiveHijack's FindPanel, UserMenu's page hunt).
	// Every client whose menu looks native does the opposite: it takes a REAL VRChat object and
	// Object.Instantiate()s it. A clone inherits all of that for free, and keeps inheriting it when
	// VRChat restyles the menu.
	//
	// WHAT A CLONE NEEDS IS A PATH, AND PATHS MOVE. That is the whole reason this file exists rather
	// than a string constant at each call site:
	//   · a 2022-era reference client reads "UserInterface/QuickMenu/ShortcutMenu/WorldsButton" —
	//     nothing of that path survives on this build;
	//   · another clones "Page_Buttons_QM/HorizontalLayoutGroup/Page_DevTools" and THROWS when it is
	//     missing. Page_DevTools does not exist here (it is Page_Launchpad now), and because its whole
	//     button API is behind one all-or-nothing ready check, that single missing node would take
	//     every button it makes down with it.
	// So: ordered candidates, first hit wins, a miss is logged once and degrades one feature instead
	// of the menu, and the cache re-resolves when Unity destroys what it held.
	//
	// Every path below was READ OUT OF OUR OWN CAPTURE of this build
	// (captures\ui_2026-08-24_22-38-00.txt), not carried over from another client.
	public static class MenuDonor
	{
		private sealed class Slot
		{
			public readonly string Label;
			public readonly string[] Paths;
			public Transform T;
			public float NextTry;
			public bool Complained;
			public Slot(string label, params string[] paths) { Label = label; Paths = paths; }
		}

		// The button every card is made of. 200x176 with LayoutElement, StyleElement, CanvasGroup and
		// VerticalLayoutGroup — a grid cell, not a full-width row, which is what our cards want anyway.
		//
		// CAUTION for callers: on this build this object carries NO UnityEngine.UI.Button. Its click
		// components are obfuscated VRChat classes, so GetComponent<Button>() returns null and a
		// reference client that assumes otherwise would throw here. MenuCard already hedges with
		// "?? AddComponent<Button>()"; keep that.
		private static readonly Slot _button = new Slot("button",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_Here/ScrollRect/Viewport/VerticalLayoutGroup/Buttons_WorldActions/Button_NewInstance",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_Here/ScrollRect/Viewport/VerticalLayoutGroup/Buttons_WorldActions/Button_SelectInstance",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_Dashboard/ScrollRect/Viewport/VerticalLayoutGroup/Buttons_QuickLinks/Button_Worlds");

		// A labelled scroll panel: header + scrolling content, 420x810, already themed. This is the
		// shape a players list and an instance log both want, and cloning it means the scrollbar, the
		// mask, the header font and the padding all come for free.
		private static readonly Slot _wingPanel = new Slot("wing panel",
			"CanvasGroup/Container/Window/Wing_Left/Container/InnerContainer/Friends/Panel_Wing_ScrollRect_Labeled",
			"CanvasGroup/Container/Window/Wing_Right/Container/InnerContainer/Friends/Panel_Wing_ScrollRect_Labeled",
			"CanvasGroup/Container/Window/Wing_Left/Container/InnerContainer/Groups/Panel_Wing_ScrollRect_Labeled");

		// Where our panels are parented. PLAYERS go LEFT, the instance LOG goes RIGHT.
		// Two candidates each, because "Container/InnerContainer" is the part that moved: the August
		// capture has it, the live build answered "none of the 1 known paths exist" for the right wing.
		// The wing itself is stable; what hangs under it is not.
		private static readonly Slot _wingLeft = new Slot("left wing",
			"CanvasGroup/Container/Window/Wing_Left/Container/InnerContainer",
			"CanvasGroup/Container/Window/Wing_Left");

		private static readonly Slot _wingRight = new Slot("right wing",
			"CanvasGroup/Container/Window/Wing_Right/Container/InnerContainer",
			"CanvasGroup/Container/Window/Wing_Right");

		/// <summary>A real VRChat menu button to Instantiate. Null when the menu is not up yet.</summary>
		public static Transform Button() => Resolve(_button);

		/// <summary>A real labelled scroll panel to Instantiate.</summary>
		public static Transform WingPanel() => Resolve(_wingPanel);

		// The Launch Pad's scroll content — the vertical list whose FIRST row is the promo carousel.
		// That row is the slot the event console takes over (LaunchpadConsoleModule).
		private static readonly Slot _launchpad = new Slot("launchpad content",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_QM_Launchpad/ScrollRect/Viewport/VerticalLayoutGroup",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_QM_Launchpad/Scrollrect/Viewport/VerticalLayoutGroup");

		/// <summary>The Launch Pad page's scroll content. Null when the menu is not up.</summary>
		public static Transform LaunchpadContent() => Resolve(_launchpad);

		// The menu's Window. Measured: no StyleElement, no layout group, no CanvasGroup and no mask —
		// which is why everything this mod draws renders correctly there and nowhere else.
		private static readonly Slot _window = new Slot("window", "CanvasGroup/Container/Window");

		/// <summary>The QuickMenu's Window: the host for anything we draw ourselves.</summary>
		public static Transform Window() => Resolve(_window);

		// The Launch Pad's page title. Read from the live dump:
		//   Menu_QM_Launchpad/Header_H1/LeftItemContainer/Text_Title {700x64 pos(0,-60)}
		private static readonly Slot _launchpadTitle = new Slot("launchpad title",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_QM_Launchpad/Header_H1/LeftItemContainer/Text_Title",
			"CanvasGroup/Container/Window/QMParent/Body/Menu_QM_Launchpad/Header_H1/LeftItemContainer/Text_Header");

		/// <summary>The "Launch Pad" heading. Null when the menu is not up.</summary>
		public static Transform LaunchpadTitle() => Resolve(_launchpadTitle);

		/// <summary>The container a wing panel is parented into. left = the PLAYERS side.</summary>
		public static Transform Wing(bool left) => Resolve(left ? _wingLeft : _wingRight);

		/// <summary>True once every donor this mod needs has been found — for the diagnostics panel,
		/// so "my buttons look wrong" has an answer that is not a guess.</summary>
		public static bool Ready => Button() != null && WingPanel() != null && Wing(true) != null && Wing(false) != null;

		public static string Status()
		{
			return "donors: button " + (Button() != null ? "✓" : "✗")
				+ " · panel " + (WingPanel() != null ? "✓" : "✗")
				+ " · wings " + (Wing(true) != null ? "L" : "-") + (Wing(false) != null ? "R" : "-");
		}

		// TYPE FIRST, PATH SECOND — and that order is the opposite of what it looks like.
		//
		// The instinct is that obfuscation eats type names while GameObject names are safe. Measured
		// against this build, it is the other way round for VRChat's OWN types: VRC.UI.Elements.QuickMenu,
		// UIPage, MenuStateController, MenuTab, SelectedUserMenuQM, AudioInputDeviceMenu and
		// StyleElement are all still present and unrenamed years after a 2021-era client keyed on them,
		// while EVERY GameObject path that client used is gone from our capture — VRChat inserted a
		// whole "Body" level and renamed the pages. BeeByte renames the GENERATED names
		// (MonoBehaviourPublicObGaCoUnique), not the hand-written ones.
		//
		// Our own broken lookups are the proof: ArchiveHijack keys on three obfuscated aliases and
		// fails every world load; a type-keyed lookup would have survived.
		//
		// Non-generic FindObjectsOfTypeAll with Il2CppType.Of, never the generic form: the generic
		// overload returns an empty array for il2cpp classes.
		private static Transform _rootByType;
		private static float _rootNextTry;

		private static Transform RootByType()
		{
			if (_rootByType != null) return _rootByType;
			float now = VaClock.Now;
			if (now < _rootNextTry) return null;
			_rootNextTry = now + 2f;
			try
			{
				var il2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.UI.Elements.QuickMenu>();
				var found = Resources.FindObjectsOfTypeAll(il2);
				if (found == null) return null;
				for (int i = 0; i < found.Length; i++)
				{
					var qm = found[i] != null ? found[i].TryCast<VRC.UI.Elements.QuickMenu>() : null;
					if (qm == null) continue;
					Transform t;
					try { t = qm.transform; } catch { continue; }
					if (t == null) continue;
					// Climb to the ancestor the paths below are written FROM — the one carrying
					// "CanvasGroup/Container/Window" — not blindly to the topmost parent. Climbing all
					// the way can land on a canvas ABOVE the QuickMenu, and then every relative path is
					// silently wrong: that is what made the right wing "not exist" on a build whose own
					// capture clearly contains it.
					int guard = 0;
					Transform probe = null;
					while (t != null && guard++ < 12)
					{
						try { if (t.Find("CanvasGroup/Container/Window") != null) { probe = t; break; } } catch { }
						t = t.parent;
					}
					if (probe == null) continue;
					t = probe;
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					_rootByType = t;
					return _rootByType;
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] type lookup: " + e.Message); }
			return null;
		}

		private static Transform Root()
		{
			Transform t = RootByType();
			if (t != null) return t;
			// Fallback: the name-keyed scan the rest of the mod already uses.
			return QuickMenu.Root();
		}

		private static Transform Resolve(Slot s)
		{
			// Unity's == treats a destroyed object as null, so this also catches the menu being torn
			// down and rebuilt between worlds.
			if (s.T != null) return s.T;

			float now = VaClock.Now;
			if (now < s.NextTry) return null;
			s.NextTry = now + 2f;   // a menu that does not exist yet must not become a per-frame search

			Transform root = Root();
			if (root == null) return null;

			for (int i = 0; i < s.Paths.Length; i++)
			{
				Transform t = null;
				try { t = root.Find(s.Paths[i]); } catch { }
				if (t == null) continue;
				s.T = t;
				if (i > 0)
				{
					// Worth saying out loud: the first choice moved, which is the early warning that a
					// VRChat update has reshaped the menu.
					VRChatArchiveModPlugin.Logger.LogInfo("[MenuDonor] " + s.Label + ": fell back to candidate "
						+ (i + 1) + "/" + s.Paths.Length + " (" + s.Paths[i] + ")");
				}
				return s.T;
			}

			if (!s.Complained)
			{
				s.Complained = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] " + s.Label + ": none of the "
					+ s.Paths.Length + " known paths exist on this build — the feature that clones it will be skipped, "
					+ "not broken. Run DUMP MENU TREE and add the current path.");
			}
			return null;
		}

		// ---------------------------------------------------------------- inert clones

		// CLONING A PAGE COSTS THE USER THEIR MENU UNLESS THE UIPage IS REMOVED FIRST.
		//
		// Measured, not guessed. Two sessions cloned a wing page; VRChat's own log says what happened:
		//
		//   Error - Duplicate UIPage with same name: Root - Root, Root
		//   Error - InitializeUI canceled, threw exeption:System.ArgumentException:
		//           An item with the same key has already been added. Key: root
		//
		// VRChat collects the UIPage components under the menu into a dictionary keyed by page name. A
		// clone brings a second page with the SAME name, Dictionary.Add throws, and InitializeUI aborts
		// — after which the QuickMenu will not open at all. Eighteen sessions the same day, with no
		// panel, logged neither line; the two sessions with one logged both. One clone gave two errors
		// and blank wings, two clones killed the menu outright.
		//
		// So the clone is made where its Awake CANNOT run — under a parent that is inactive, which is
		// what keeps Unity from waking a freshly instantiated object — the UIPage components are
		// destroyed there, and only then is it reparented and shown. And if any survives, the clone is
		// thrown away rather than attached: no feature is worth the menu.
		private static GameObject _limbo;

		private static Transform Limbo()
		{
			if (_limbo != null) return _limbo.transform;
			try
			{
				_limbo = new GameObject("VA_CloneLimbo");
				_limbo.SetActive(false);   // the whole point: children instantiated here never wake
				UnityEngine.Object.DontDestroyOnLoad(_limbo);
				return _limbo.transform;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] limbo: " + e.Message);
				return null;
			}
		}

		/// <summary>Every UIPage under <paramref name="root"/>, found by TYPE. The non-generic overload
		/// with Il2CppType.Of is deliberate — the generic form returns nothing for il2cpp classes — and
		/// so is going through the type at all: UIPage carries [ObfuscatedName], so its runtime class
		/// name is unreadable garbage and matching on the string "UIPage" would find none of them.</summary>
		private static int CountPages(Transform root)
		{
			try
			{
				var t = Il2CppInterop.Runtime.Il2CppType.Of<VRC.UI.Elements.UIPage>();
				var comps = root.GetComponentsInChildren(t, true);
				return comps == null ? 0 : comps.Length;
			}
			catch (Exception e)
			{
				// Unknown beats zero here: a caller that cannot count pages must not be told there are
				// none, because "none" is the answer that lets a clone reach the menu.
				VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] page count: " + e.Message);
				return -1;
			}
		}

		private static int StripPages(Transform root)
		{
			int n = 0;
			try
			{
				var t = Il2CppInterop.Runtime.Il2CppType.Of<VRC.UI.Elements.UIPage>();
				var comps = root.GetComponentsInChildren(t, true);
				if (comps == null) return 0;
				for (int i = 0; i < comps.Length; i++)
				{
					if (comps[i] == null) continue;
					// Immediate, not deferred: a Destroy that lands at end of frame would leave the page
					// registrable for the rest of this one.
					try { UnityEngine.Object.DestroyImmediate(comps[i]); n++; } catch { }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] strip pages: " + e.Message); }
			return n;
		}

		/// <summary>Clones a donor that is a PAGE, with every UIPage removed, so VRChat's page table
		/// never sees it. Null when the clone could not be made safe — the caller must then skip its
		/// feature entirely.</summary>
		public static Transform CloneInert(Transform donor, Transform parent, string name)
		{
			if (donor == null || parent == null) return null;
			Transform limbo = Limbo();
			if (limbo == null) return null;

			GameObject go = null;
			try
			{
				go = UnityEngine.Object.Instantiate(donor.gameObject, limbo);
				go.name = name;

				int stripped = StripPages(go.transform);
				int left = CountPages(go.transform);

				// ZERO STRIPPED IS A FAILURE, NOT A CLEAN PAGE. VRChat logged exactly one duplicate per
				// clone, so a page donor is KNOWN to carry a UIPage; finding none means the type lookup
				// came back empty, and then "left == 0" is not evidence of anything. Without this the
				// one way this can go wrong — a lookup that silently matches nothing — is also the one
				// way it would sail past the check and break the menu again.
				if (stripped <= 0 || left != 0)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] " + name + ": "
						+ (stripped <= 0
							? "found no UIPage to strip — the type lookup failed, because a page donor always has one"
							: stripped + " UIPage(s) stripped but " + (left < 0 ? "the recount failed" : left + " remain"))
						+ " — discarding the clone rather than risking the menu.");
					UnityEngine.Object.DestroyImmediate(go);
					return null;
				}

				// worldPositionStays: false — this is UI, the anchors decide where it lands.
				go.transform.SetParent(parent, false);
				VRChatArchiveModPlugin.Logger.LogInfo("[MenuDonor] " + name + ": inert clone ready ("
					+ stripped + " UIPage(s) removed).");
				return go.transform;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] inert clone of " + name + " failed: " + e.Message);
				try { if (go != null) UnityEngine.Object.DestroyImmediate(go); } catch { }
				return null;
			}
		}

		/// <summary>Clones a donor under a parent and names it. Null on any failure — a caller that
		/// gets null must skip its feature, never build a hand-made substitute: a lookalike that does
		/// not theme with the menu is exactly what this file exists to stop.
		/// For a donor that is a PAGE, use CloneInert instead: a plain clone keeps its UIPage and that
		/// stops the QuickMenu from opening.</summary>
		public static Transform Clone(Transform donor, Transform parent, string name)
		{
			if (donor == null || parent == null) return null;
			try
			{
				GameObject go = UnityEngine.Object.Instantiate(donor.gameObject, parent);
				go.name = name;
				go.SetActive(true);
				return go.transform;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MenuDonor] clone of " + name + " failed: " + e.Message);
				return null;
			}
		}
	}
}
