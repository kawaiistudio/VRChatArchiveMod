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
	/// <summary>
	/// Injects a dedicated "Archive Main menu" tab into VRChat's Big Main Menu (Canvas_MainMenu)
	/// on the bottom navigation bar beside Shop, and clones Menu_Settings to provide a fully native,
	/// authentic VRChat Archive Mod settings and control dashboard.
	/// </summary>
	public class MainMenuTabModule : IModule
	{
		public override string Name => "MainMenuTab";

		private const string MainCanvasName = "Canvas_MainMenu(Clone)";
		private const string PageButtonsPath = "Container/PageButtons/HorizontalLayoutGroup";
		private const string HeaderOffsetPath = "Container/MMParent/HeaderOffset";
		private const string SettingsMenuName = "Menu_Settings(Clone)";
		private const string OurTabName = "Archive_Button_Tab";
		private const string OurPageName = "Menu_Archive(Clone)";

		private GameObject _ourTabButton;
		private Image _tabBgImage;
		private Image _tabIconImage;
		private TMPro.TMP_Text _tabLabel;

		private GameObject _ourPage;
		private Transform _headerOffset;
		private Transform _pageButtonsStrip;
		private static Transform _searchFieldDonor;

		private float _nextScan;
		private int _scanAttempts;
		private bool _built;
		private bool _isOurTabActive;

		// Sidebar & Page content state
		private readonly List<SidebarCategory> _categories = new List<SidebarCategory>();
		private int _activeCategoryIndex = 0;
		private TMPro.TMP_Text _contentHeaderTitle;
		private ScrollRect _contentScrollRect;

		private sealed class SidebarCategory
		{
			public string Name;
			public GameObject CellGo;
			public GameObject ContentPageGo;
			public TMPro.TMP_Text Label;
			public Image BgImage;
			public Image IconImage;
		}

		private sealed class ToggleTile
		{
			public Transform Card;
			public Func<bool> State;
			public bool Last;
			public Image PillImage;
			public RectTransform KnobTransform;
		}
		private readonly List<ToggleTile> _toggles = new List<ToggleTile>();

		private sealed class StepperRef
		{
			public Transform Card;
			public string Title;
			public BepInEx.Configuration.ConfigEntry<float> Cfg;
			public float LastVal;
			public string Format;
			public TMPro.TMP_Text ValText;
		}
		private readonly List<StepperRef> _steppers = new List<StepperRef>();

		private sealed class SectionUI
		{
			public string Title;
			public Transform Header;
			public Transform Body;
			public bool Expanded;
			public TMPro.TMP_Text HeaderText;
		}
		private readonly List<SectionUI> _sections = new List<SectionUI>();

		// Card donor reference from Launchpad or donor tab
		private Transform _donorCard;

		// Unity components to keep on cloned roots
		private static readonly HashSet<string> Keep = new HashSet<string>(StringComparer.Ordinal)
		{
			"RectTransform", "Transform", "CanvasRenderer", "Canvas", "CanvasGroup", "GraphicRaycaster",
			"Image", "ImageEx", "RawImage", "RawImageEx",
			"LayoutElement", "HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup",
			"ContentSizeFitter", "AspectRatioFitter", "RectMask2D", "VRCRectMask2D", "Mask", "ScrollRect", "Scrollbar",
			"UIInvisibleGraphic", "Button",
		};

		// Colors matching VRChat's Big Main Menu design tokens
		private static readonly Color ColorInactiveTabBg = new Color(0.14f, 0.15f, 0.18f, 0.60f);
		private static readonly Color ColorActiveTabBg   = new Color(0.00f, 0.72f, 0.85f, 1.00f); // Native VRChat Cyan
		private static readonly Color ColorInactiveText  = new Color(0.85f, 0.88f, 0.92f, 1.00f);
		private static readonly Color ColorActiveText    = new Color(0.06f, 0.08f, 0.11f, 1.00f); // Dark contrast on Cyan
		private static readonly Color ColorHoverTabBg    = new Color(0.25f, 0.27f, 0.33f, 0.85f);

		private static MainMenuTabModule _instance;
		public static MainMenuTabModule Instance => _instance;

		// =========================================================================
		// LIFECYCLE
		// =========================================================================

		public override void OnInitialize()
		{
			_instance = this;
		}

		public override void OnUpdate()
		{
			try
			{

				if (!_built)
				{
					float now = VaClock.Now;
					if (now < _nextScan) return;
					_nextScan = now + 2f;
					if (_scanAttempts > 60) return;

					if (TryBuild())
					{
						_built = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[MainMenuTab] Successfully injected Archive Big Main Menu tab and page!");
					}
					else
					{
						_scanAttempts++;
					}
					return;
				}

				HandleInlineTyping();
				Pump();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MainMenuTab] OnUpdate error: {e}");
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_instance = this;
			_built = false;
			_activeInputRow = null;
			_ourTabButton = null;
			_tabBgImage = null;
			_tabIconImage = null;
			_tabLabel = null;
			_ourPage = null;
			_searchFieldDonor = null;
			_isOurTabActive = false;
			_scanAttempts = 0;
			_nextScan = 0f;
			_categories.Clear();
			_toggles.Clear();
			_steppers.Clear();
			_sections.Clear();
			_contentScrollRect = null;
		}

		public override void OnShutdown()
		{
			Teardown();
		}

		private void Teardown()
		{
			try
			{
				if (_ourTabButton != null) UnityEngine.Object.Destroy(_ourTabButton);
				if (_ourPage != null) UnityEngine.Object.Destroy(_ourPage);
			}
			catch { }
			_ourTabButton = null;
			_ourPage = null;
			_searchFieldDonor = null;
			_contentScrollRect = null;
			_built = false;
			_isOurTabActive = false;
			_categories.Clear();
			_toggles.Clear();
			_steppers.Clear();
			_sections.Clear();
		}

		// =========================================================================
		// BUILD / INJECTION
		// =========================================================================

		private static Transform FindSettingsDonor(Transform headerOffset)
		{
			if (headerOffset != null)
			{
				for (int i = 0; i < headerOffset.childCount; i++)
				{
					var c = headerOffset.GetChild(i);
					if (c != null && c.name.StartsWith("Menu_Settings", StringComparison.OrdinalIgnoreCase))
						return c;
				}
			}

			try
			{
				var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Transform>());
				if (all != null)
				{
					for (int i = 0; i < all.Length; i++)
					{
						var t = all[i]?.TryCast<Transform>();
						if (t != null && t.name.StartsWith("Menu_Settings", StringComparison.OrdinalIgnoreCase))
						{
							if (t.Find("Menu_MM_DynamicSidePanel") != null || t.Find("ScrollRect_Navigation_Container") != null)
								return t;
						}
					}
				}
			}
			catch { }
			return null;
		}

		private bool TryBuild()
		{
			var mainRoot = Core.QuickMenu.Main();
			if (mainRoot == null) return false;

			Transform strip = mainRoot.Find(PageButtonsPath);
			Transform headerOffset = mainRoot.Find(HeaderOffsetPath);

			if (strip == null || headerOffset == null) return false;

			_pageButtonsStrip = strip;
			_headerOffset = headerOffset;

			// Locate donor Menu_Settings
			Transform settingsDonor = FindSettingsDonor(headerOffset);

			if (_ourTabButton == null) BuildTabButton(strip);

			if (settingsDonor != null && _ourPage == null)
			{
				BuildMainPage(settingsDonor, headerOffset);
			}

			return _ourTabButton != null && _ourPage != null;
		}

		private static Sprite _logoSprite;
		private static Sprite LogoSprite()
		{
			try
			{
				if (_logoSprite != null) return _logoSprite;
				var tex = AssetLoader.ArchiveLogo;
				if (tex == null) return null;
				_logoSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
				if (_logoSprite != null) _logoSprite.hideFlags = HideFlags.HideAndDontSave;
				return _logoSprite;
			}
			catch { return null; }
		}

		private bool BuildTabButton(Transform strip)
		{
			Transform donorBtn = null;
			for (int i = strip.childCount - 1; i >= 0; i--)
			{
				var c = strip.GetChild(i);
				if (c != null && c.gameObject.activeSelf && c.name != OurTabName)
				{
					donorBtn = c;
					break;
				}
			}

			if (donorBtn == null) return false;

			var existing = strip.Find(OurTabName);
			if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

			var clonedTab = UnityEngine.Object.Instantiate(donorBtn.gameObject, strip);
			clonedTab.name = OurTabName;
			clonedTab.transform.SetAsLastSibling();

			// Strip foreign page/tab state controllers from the cloned tab root
			StripRoot(clonedTab.transform);

			// Ensure CanvasGroup is unmasked
			var cg = clonedTab.GetComponent<CanvasGroup>();
			if (cg != null)
			{
				cg.alpha = 1f;
				cg.interactable = true;
				cg.blocksRaycasts = true;
			}

			// Background Graphic
			var bgTransform = clonedTab.transform.Find("Background");
			_tabBgImage = bgTransform != null ? bgTransform.GetComponent<Image>() : clonedTab.GetComponent<Image>();
			if (_tabBgImage == null)
			{
				_tabBgImage = clonedTab.AddComponent<Image>();
			}
			_tabBgImage.raycastTarget = true;
			_tabBgImage.color = ColorInactiveTabBg;

			// Button Component
			var btn = clonedTab.GetComponent<Button>() ?? clonedTab.gameObject.AddComponent<Button>();
			btn.targetGraphic = _tabBgImage;
			btn.interactable = true;
			btn.transition = Selectable.Transition.ColorTint;

			var cb = default(ColorBlock);
			cb.normalColor = ColorInactiveTabBg;
			cb.highlightedColor = ColorHoverTabBg;
			cb.pressedColor = ColorActiveTabBg;
			cb.selectedColor = ColorActiveTabBg;
			cb.disabledColor = new Color(0.14f, 0.15f, 0.18f, 0.3f);
			cb.colorMultiplier = 1f;
			cb.fadeDuration = 0.08f;
			btn.colors = cb;
			UiClick.AddClick(btn, OnTabClicked);
			UiClick.AddClick(btn, MenuCard.PlayClick);

			// Rename Label
			SetLabelRecursive(clonedTab.transform, "Archive Main menu", out _tabLabel);
			if (_tabLabel != null) _tabLabel.color = ColorInactiveText;

			// Brand Icon
			ApplyTabLogo(clonedTab.transform, out _tabIconImage);
			if (_tabIconImage != null) _tabIconImage.color = ColorInactiveText;

			_ourTabButton = clonedTab;
			HookSiblingTabs(strip);
			return true;
		}

		private static void ApplyTabLogo(Transform tab, out Image outImg)
		{
			outImg = null;
			try
			{
				Transform iconT = tab.Find("Icon") ?? tab.Find("Icons/Icon") ?? tab.Find("Image");
				if (iconT == null)
				{
					foreach (var im in tab.GetComponentsInChildren<Image>(true))
					{
						if (im != null && im.gameObject.name != "Background" && im.gameObject != tab.gameObject)
						{
							iconT = im.transform;
							break;
						}
					}
				}

				if (iconT == null) return;

				// Strip StyleElement on icon only so VRChat doesn't revert the sprite
				foreach (var c in iconT.GetComponents<Component>())
				{
					if (c != null && Il2CppName(c) == "StyleElement")
					{
						try { UnityEngine.Object.DestroyImmediate(c); } catch { }
					}
				}

				var img = iconT.GetComponent<Image>();
				if (img != null)
				{
					var sp = LogoSprite();
					if (sp != null)
					{
						img.sprite = sp;
						img.overrideSprite = sp;
						img.color = ColorInactiveText;
						img.raycastTarget = false;
						outImg = img;
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MainMenuTab] ApplyTabLogo failed: {e.Message}");
			}
		}

		// =========================================================================
		// MAIN PAGE SETUP & CLONING
		// =========================================================================

		private bool BuildMainPage(Transform settingsDonor, Transform headerOffset)
		{
			try
			{
				var existing = headerOffset.Find(OurPageName);
				if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

				var clonedPage = UnityEngine.Object.Instantiate(settingsDonor.gameObject, headerOffset);
				clonedPage.name = OurPageName;

				// Reset Transform
				clonedPage.transform.localPosition = Vector3.zero;
				clonedPage.transform.localScale = Vector3.one;
				clonedPage.transform.localRotation = new Quaternion(0f, 0f, 0f, 1f);

				// Strip native page controllers on clone root
				StripRoot(clonedPage.transform);

				// Strip any hidden UIPage from entire cloned hierarchy to avoid VRChat duplicate page collisions
				try
				{
					var t = Il2CppInterop.Runtime.Il2CppType.Of<VRC.UI.Elements.UIPage>();
					var comps = clonedPage.GetComponentsInChildren(t, true);
					if (comps != null)
					{
						for (int i = 0; i < comps.Length; i++)
						{
							if (comps[i] != null) UnityEngine.Object.DestroyImmediate(comps[i]);
						}
					}
				}
				catch { }

				// Prepare page structures
				SetupClonedPageStructure(clonedPage.transform);

				// Initially hide our page
				clonedPage.SetActive(false);

				_ourPage = clonedPage;
				return true;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MainMenuTab] BuildMainPage threw: {e}");
				return false;
			}
		}

		private void SetupClonedPageStructure(Transform pageRoot)
		{
			_categories.Clear();
			_toggles.Clear();
			_steppers.Clear();
			_sections.Clear();

			// Locate ScrollRect_Navigation_Container
			Transform navContainer = FindRecursive(pageRoot, "ScrollRect_Navigation_Container");
			if (navContainer == null)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MainMenuTab] ScrollRect_Navigation_Container not found in cloned page.");
				return;
			}

// Ensure parent containers are active
			Transform dynSidePanel = FindRecursive(pageRoot, "Menu_MM_DynamicSidePanel");
			if (dynSidePanel != null) dynSidePanel.gameObject.SetActive(true);
			Transform panelSectionList = FindRecursive(pageRoot, "Panel_SectionList");
			if (panelSectionList != null) panelSectionList.gameObject.SetActive(true);
			navContainer.gameObject.SetActive(true);

			// 1. Rename Header Title on Left Sidebar & clean stock buttons
			Transform header = navContainer.Find("DynamicSidePanel_Header") ?? FindRecursive(navContainer, "DynamicSidePanel_Header");
			if (header != null)
			{
				SetLabelRecursive(header, "Archive Main menu", out _);

				// Remove unwanted stock buttons from our custom menu sidebar
				string[] buttonsToRemove = new[] { "Button_Logout", "Button_Exit", "Button_LinkAccount", "Button_LinkDiscordAccount", "SearchBtn" };
				foreach (var btnName in buttonsToRemove)
				{
					var b = header.Find(btnName) ?? FindRecursive(header, btnName);
					if (b != null)
					{
						UnityEngine.Object.DestroyImmediate(b.gameObject);
					}
				}

				// Adjust header height since buttons are removed (TitleContainer 96px + Separator ~10px)
				var headerRt = header.GetComponent<RectTransform>();
				if (headerRt != null)
				{
					headerRt.sizeDelta = new Vector2(headerRt.sizeDelta.x, 106f);
				}
			}

			// 2. Locate Sidebar Navigation ScrollRect
			Transform scrollNav = navContainer.Find("ScrollRect_Navigation") ?? FindRecursive(navContainer, "ScrollRect_Navigation");
			Transform navContent = null;
			if (scrollNav != null)
			{
				scrollNav.gameObject.SetActive(true);
				var scrollNavRt = scrollNav.GetComponent<RectTransform>();
				if (scrollNavRt != null)
				{
					// Move ScrollRect_Navigation to start right below the 106px header
					scrollNavRt.anchorMin = new Vector2(0f, 0f);
					scrollNavRt.anchorMax = new Vector2(1f, 1f);
					scrollNavRt.pivot = new Vector2(0f, 1f);
					scrollNavRt.offsetMin = new Vector2(0f, 20f);
					scrollNavRt.offsetMax = new Vector2(0f, -106f);
				}

				// Add RectMask2D on nav Viewport and remove old stencil mask so cells never bleed into header
				Transform navVp = scrollNav.Find("Viewport");
				if (navVp != null)
				{
					try
					{
						foreach (var comp in navVp.GetComponents<Component>())
						{
							if (comp == null) continue;
							string n = Il2CppName(comp);
							if (n == "RectTransform" || n == "CanvasRenderer") continue;
							try { UnityEngine.Object.DestroyImmediate(comp); } catch { }
						}
					}
					catch { }
					var navRm = navVp.GetComponent<RectMask2D>() ?? navVp.gameObject.AddComponent<RectMask2D>();
					navRm.enabled = true;
				}

				var sr = scrollNav.GetComponent<ScrollRect>() ?? scrollNav.GetComponentInChildren<ScrollRect>(true);
				if (sr != null)
				{
					sr.vertical = false; // 6 items fit in 432px; lock vertical scrolling so it never scrolls into header
					sr.horizontal = false;
					if (sr.content != null) navContent = sr.content;
				}
				if (navContent == null)
					navContent = scrollNav.Find("Viewport/Content") ?? scrollNav.Find("Viewport/VerticalLayoutGroup") ?? FindRecursive(scrollNav, "VerticalLayoutGroup");

				if (navContent != null)
				{
					var ncRt = navContent.GetComponent<RectTransform>();
					if (ncRt != null)
					{
						ncRt.anchorMin = new Vector2(0f, 1f);
						ncRt.anchorMax = new Vector2(1f, 1f);
						ncRt.pivot = new Vector2(0.5f, 1f);
						ncRt.anchoredPosition = Vector2.zero;
					}
				}
			}

			// 3. Locate Content ScrollRect on pageRoot (NOT navContainer, since they are siblings!)
			Transform scrollContent = pageRoot.Find("ScrollRect_Content") ?? FindRecursive(pageRoot, "ScrollRect_Content");
			Transform contentParent = null;
			if (scrollContent != null)
			{
				scrollContent.gameObject.SetActive(true);
				var sr = scrollContent.GetComponent<ScrollRect>() ?? scrollContent.GetComponentInChildren<ScrollRect>(true);
				if (sr != null && sr.content != null)
					contentParent = sr.content;
				else
					contentParent = scrollContent.Find("Viewport/VerticalLayoutGroup") ?? FindRecursive(scrollContent, "VerticalLayoutGroup") ?? scrollContent.Find("Viewport/Content");

				// Ensure Viewport has RectMask2D, RaycastTarget Image, and proper boundary so content NEVER spills out of the menu!
				Transform viewport = scrollContent.Find("Viewport") ?? FindRecursive(scrollContent, "Viewport");
				if (viewport != null)
				{
					viewport.gameObject.SetActive(true);

					// 1. Remove ANY conflicting Mask / Obfuscated Mask on Viewport so RectMask2D works 100% cleanly
					try
					{
						foreach (var comp in viewport.GetComponents<Component>())
						{
							if (comp == null) continue;
							string n = Il2CppName(comp);
							if (n == "RectTransform" || n == "CanvasRenderer") continue;
							try { UnityEngine.Object.DestroyImmediate(comp); } catch { }
						}
					}
					catch { }

					// 2. Adjust Viewport bounds to strictly match the inner viewing window:
					// Top starts 96px below header (offsetMax.y = -96)
					// Bottom stops 100px above bottom bar (offsetMin.y = 100)
					var vpRt = viewport.GetComponent<RectTransform>();
					if (vpRt != null)
					{
						vpRt.anchorMin = Vector2.zero;
						vpRt.anchorMax = Vector2.one;
						vpRt.pivot = new Vector2(0.5f, 0.5f);
						vpRt.offsetMin = new Vector2(0f, 100f); // 100px above bottom bar
						vpRt.offsetMax = new Vector2(0f, -96f); // 96px below top header
					}

					// 3. Add transparent Image with raycastTarget = true so dragging on the screen and scrolling background works!
					var vpImg = viewport.GetComponent<Image>() ?? viewport.gameObject.AddComponent<Image>();
					vpImg.color = Color.clear;
					vpImg.raycastTarget = true;

					// 4. Add RectMask2D cleanly
					var rectMask = viewport.GetComponent<RectMask2D>() ?? viewport.gameObject.AddComponent<RectMask2D>();
					rectMask.enabled = true;
				}

				// Ensure ScrollRect is properly configured on scrollContent
				var contentSr = scrollContent.GetComponent<ScrollRect>() ?? scrollContent.gameObject.AddComponent<ScrollRect>();
				contentSr.enabled = true;
				if (contentParent != null)
				{
					var cpRt = contentParent.GetComponent<RectTransform>();
					if (cpRt != null)
					{
						cpRt.anchorMin = new Vector2(0f, 1f);
						cpRt.anchorMax = new Vector2(1f, 1f);
						cpRt.pivot = new Vector2(0.5f, 1f);
						cpRt.anchoredPosition = Vector2.zero;
					}
					contentSr.content = cpRt;

					// Ensure contentParent has raycastTarget Image and ContentSizeFitter so it auto-sizes to the active page
					var cpImg = contentParent.GetComponent<Image>() ?? contentParent.gameObject.AddComponent<Image>();
					cpImg.color = Color.clear;
					cpImg.raycastTarget = true;

					var cpCsf = contentParent.GetComponent<ContentSizeFitter>() ?? contentParent.gameObject.AddComponent<ContentSizeFitter>();
					cpCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
					cpCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
				}
				if (viewport != null) contentSr.viewport = viewport.GetComponent<RectTransform>();
				contentSr.horizontal = false;
				contentSr.vertical = true;
				contentSr.movementType = ScrollRect.MovementType.Clamped;
				contentSr.scrollSensitivity = 50f;
				contentSr.inertia = true;
				contentSr.decelerationRate = 0.135f;

				Transform sbar = scrollContent.Find("Scrollbar") ?? FindRecursive(scrollContent, "Scrollbar");
				if (sbar != null)
				{
					sbar.gameObject.SetActive(true);
					var sbarRt = sbar.GetComponent<RectTransform>();
					if (sbarRt != null)
					{
						sbarRt.anchorMin = new Vector2(1f, 0f);
						sbarRt.anchorMax = new Vector2(1f, 1f);
						sbarRt.offsetMin = new Vector2(-24f, 100f);
						sbarRt.offsetMax = new Vector2(0f, -96f);
					}
					var sbComp = sbar.GetComponent<Scrollbar>() ?? sbar.GetComponentInChildren<Scrollbar>(true);
					if (sbComp != null)
					{
						contentSr.verticalScrollbar = sbComp;
						contentSr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
					}
				}

				_contentScrollRect = contentSr;
			}

			// Always clean navContent immediately if found
			if (navContent != null)
			{
				for (int i = navContent.childCount - 1; i >= 0; i--)
				{
					var c = navContent.GetChild(i);
					if (c != null) UnityEngine.Object.DestroyImmediate(c.gameObject);
				}
			}

			if (navContent == null || contentParent == null)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MainMenuTab] Container resolve failed: navContent={(navContent != null)}, contentParent={(contentParent != null)}");
				return;
			}

			// 4. Find the main title text in scrollContent (Header_MM_H2/LeftItemContainer/Text_Title)
			_contentHeaderTitle = null;
			var titleTrans = scrollContent.Find("Header_MM_H2/LeftItemContainer/Text_Title")
			                 ?? FindRecursive(scrollContent, "Text_Title");
			if (titleTrans != null)
			{
				_contentHeaderTitle = titleTrans.GetComponent<TMPro.TMP_Text>() ?? titleTrans.GetComponentInChildren<TMPro.TMP_Text>(true);
			}

			if (_contentHeaderTitle == null)
			{
				foreach (var tmp in scrollContent.GetComponentsInChildren<TMPro.TMP_Text>(true))
				{
					if (tmp != null && !tmp.transform.IsChildOf(contentParent))
					{
						if (tmp.text.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 || tmp.fontSize >= 24f)
						{
							_contentHeaderTitle = tmp;
							break;
						}
					}
				}
			}

			if (_contentHeaderTitle != null)
			{
				_contentHeaderTitle.text = "Protection";
			}

			// Capture search field donor for our custom input rows before anything is modified
			_searchFieldDonor = scrollContent.Find("Header_MM_H2/RightItemContainer/Search Field")
			                    ?? FindRecursive(scrollContent, "Search Field");
			if (_searchFieldDonor != null)
			{
				try { _searchFieldDonor.gameObject.SetActive(false); } catch { }
			}

			// 5. DESTROY ALL stock children in contentParent so NO background overlap occurs
			for (int i = contentParent.childCount - 1; i >= 0; i--)
			{
				var c = contentParent.GetChild(i);
				if (c != null) UnityEngine.Object.DestroyImmediate(c.gameObject);
			}

			// 6. Define Categories matching clean game Settings layout
			var catDefs = new (string Name, string Icon)[]
			{
				("Protection", "Icon_Safety_Shield|Security|Safe"),
				("Movement", "ic_fly_mode|Drone_FlightModes|WingLeft"),
				("Visuals & ESP", "visibility|Eye"),
				("User Interface", "Logging|debug|Tag"),
				("Interactions", "Hand_Avatar|ic_box"),
				("Tools & Settings", "ic_tool|Tool|Console|Speed")
			};

			for (int i = 0; i < catDefs.Length; i++)
			{
				string catName = catDefs[i].Name;
				string catIcon = catDefs[i].Icon;
				int catIndex = i;

				// A. Create clean, authentic Sidebar Button (VRTool X style)
				var cellGo = CreateSidebarCategoryButton(navContent, catName, catIcon, catIndex, out var cellLabel, out var cellBg, out var cellIcon);

				// B. Dedicated Content Panel in contentParent
				var pageGo = new GameObject("Archive_Page_" + catName.Replace(" ", "").Replace("&", ""), Il2CppType.Of<RectTransform>());
				var pageRt = pageGo.GetComponent<RectTransform>();
				pageRt.SetParent(contentParent, false);
				pageRt.anchorMin = new Vector2(0f, 1f);
				pageRt.anchorMax = new Vector2(1f, 1f);
				pageRt.pivot = new Vector2(0.5f, 1f);
				pageRt.sizeDelta = new Vector2(0f, 0f);

				var pLe = pageGo.AddComponent<LayoutElement>();
				pLe.minWidth = 600f;
				pLe.preferredWidth = -1f;
				pLe.flexibleWidth = 1f;

				var vlg = pageGo.AddComponent<VerticalLayoutGroup>();
				vlg.spacing = 10f;
				vlg.padding = new RectOffset(20, 24, 16, 24);
				vlg.childForceExpandHeight = false;
				vlg.childControlHeight = true;
				vlg.childForceExpandWidth = true;
				vlg.childControlWidth = true;

				var csf = pageGo.AddComponent<ContentSizeFitter>();
				csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
				csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

				// Populate controls for this category
				PopulateCategoryContent(catName, pageRt);

				pageGo.SetActive(i == 0);

				_categories.Add(new SidebarCategory
				{
					Name = catName,
					CellGo = cellGo,
					ContentPageGo = pageGo,
					Label = cellLabel,
					BgImage = cellBg,
					IconImage = cellIcon
				});
			}

			SelectCategory(0);
		}

		private static void RestartGame()
		{
			try
			{
				Toast.Show("Restarting VRChat...");
				var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
				if (!string.IsNullOrEmpty(exePath))
				{
					System.Diagnostics.Process.Start(exePath, Environment.CommandLine);
				}
				Application.Quit();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MainMenuTab] RestartGame failed: {e.Message}");
			}
		}

		private GameObject CreateSidebarActionButton(Transform parent, string title, string iconHints, Action onClick)
		{
			var go = new GameObject("Archive_TopAction_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);

			var le = go.AddComponent<LayoutElement>();
			le.minHeight = 46f;
			le.preferredHeight = 46f;
			le.flexibleWidth = 1f;

			var bg = go.AddComponent<Image>();
			bg.color = new Color(0.18f, 0.20f, 0.26f, 0.95f);
			bg.raycastTarget = true;

			var btn = go.AddComponent<Button>();
			btn.targetGraphic = bg;
			var navA = new Navigation();
			navA.mode = Navigation.Mode.None;
			btn.navigation = navA;

			// Icon on left
			var iconGo = new GameObject("Icon", Il2CppType.Of<RectTransform>());
			var iconRt = iconGo.GetComponent<RectTransform>();
			iconRt.SetParent(rt, false);
			iconRt.anchorMin = new Vector2(0f, 0.5f);
			iconRt.anchorMax = new Vector2(0f, 0.5f);
			iconRt.pivot = new Vector2(0f, 0.5f);
			iconRt.anchoredPosition = new Vector2(16f, 0f);
			iconRt.sizeDelta = new Vector2(24f, 24f);

			var iconImg = iconGo.AddComponent<Image>();
			iconImg.raycastTarget = false;
			var sp = FindSprite(iconHints);
			if (sp != null)
			{
				iconImg.sprite = sp;
				iconImg.color = new Color(1f, 0.40f, 0.40f, 1f);
			}
			else
			{
				iconImg.color = Color.clear;
			}

			// Label
			var txtGo = new GameObject("Text", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rt, false);
			txtRt.anchorMin = Vector2.zero;
			txtRt.anchorMax = Vector2.one;
			txtRt.offsetMin = new Vector2(50f, 0f);
			txtRt.offsetMax = new Vector2(-16f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.text = title;
			tmp.fontSize = 19f;
			tmp.fontStyle = TMPro.FontStyles.Bold;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.color = new Color(0.95f, 0.95f, 0.98f, 1f);
			tmp.enableWordWrapping = false;
			tmp.raycastTarget = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			UiClick.AddClick(btn, onClick);
			UiClick.AddClick(btn, MenuCard.PlayClick);
			return go;
		}

		private GameObject CreateSidebarCategoryButton(Transform parent, string title, string iconHints, int index, out TMPro.TMP_Text outLabel, out Image outBg, out Image outIcon)
		{
			var go = new GameObject("Archive_Cell_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);

			var le = go.AddComponent<LayoutElement>();
			le.minHeight = 72f;
			le.preferredHeight = 72f;
			le.flexibleWidth = 1f;

			outBg = go.AddComponent<Image>();
			outBg.color = (index == 0) ? new Color(0.20f, 0.23f, 0.32f, 0.95f) : new Color(0.12f, 0.13f, 0.16f, 0.85f);
			outBg.raycastTarget = true;

			var btn = go.AddComponent<Button>();
			btn.targetGraphic = outBg;
			var navC = new Navigation();
			navC.mode = Navigation.Mode.None;
			btn.navigation = navC;

			// Icon on left
			var iconGo = new GameObject("Icon", Il2CppType.Of<RectTransform>());
			var iconRt = iconGo.GetComponent<RectTransform>();
			iconRt.SetParent(rt, false);
			iconRt.anchorMin = new Vector2(0f, 0.5f);
			iconRt.anchorMax = new Vector2(0f, 0.5f);
			iconRt.pivot = new Vector2(0f, 0.5f);
			iconRt.anchoredPosition = new Vector2(18f, 0f);
			iconRt.sizeDelta = new Vector2(32f, 32f);

			outIcon = iconGo.AddComponent<Image>();
			outIcon.raycastTarget = false;
			var sp = FindSprite(iconHints);
			if (sp != null)
			{
				outIcon.sprite = sp;
				outIcon.color = (index == 0) ? new Color(0.00f, 0.85f, 1.00f, 1f) : new Color(0.85f, 0.88f, 0.94f, 1f);
			}
			else
			{
				outIcon.color = Color.clear;
			}

			// Label
			var txtGo = new GameObject("Text", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rt, false);
			txtRt.anchorMin = Vector2.zero;
			txtRt.anchorMax = Vector2.one;
			txtRt.offsetMin = new Vector2(62f, 0f);
			txtRt.offsetMax = new Vector2(-16f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.text = title;
			tmp.fontSize = 22.5f;
			tmp.fontStyle = TMPro.FontStyles.Bold;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.color = (index == 0) ? new Color(0.00f, 0.85f, 1.00f, 1f) : new Color(0.88f, 0.90f, 0.95f, 1f);
			tmp.raycastTarget = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			outLabel = tmp;

			UiClick.AddClick(btn, () => SelectCategory(index));
			UiClick.AddClick(btn, MenuCard.PlayClick);
			return go;
		}

		private void PopulateCategoryContent(string category, Transform root)
		{
			switch (category)
			{
				case "Protection":
					BuildProtectionsPage(root);
					break;
				case "Movement":
					BuildMovementPage(root);
					break;
				case "Visuals & ESP":
					BuildVisualsPage(root);
					break;
				case "User Interface":
					BuildHudPage(root);
					break;
				case "Interactions":
					BuildInteractionsPage(root);
					break;
				case "Tools & Settings":
					BuildToolsPage(root);
					break;
			}
		}

		private void BuildProtectionsPage(Transform root)
		{
			CreateSection(root, "General", out var gHost);
			AddToggleRow(gHost, "Anti-Crash Protection", () => ModConfig.AntiCrashEnabled.Value, () =>
			{
				ModConfig.AntiCrashEnabled.Value = !ModConfig.AntiCrashEnabled.Value;
				Toast.Show(ModConfig.AntiCrashEnabled.Value ? "Anti-Crash: ON" : "Anti-Crash: OFF");
			});
			AddToggleRow(gHost, "Anti World Triggers (Block Exploits)", () => ModConfig.UdonBlockCrashers.Value, () =>
			{
				ModConfig.UdonBlockCrashers.Value = !ModConfig.UdonBlockCrashers.Value;
			});
			AddToggleRow(gHost, "Photon Guard (Rate Limiter)", () => ModConfig.PhotonGuardEnabled.Value, () =>
			{
				ModConfig.PhotonGuardEnabled.Value = !ModConfig.PhotonGuardEnabled.Value;
			});
			AddToggleRow(gHost, "Avatar Over-Budget Guard", () => ModConfig.HideAvatarOverBudget.Value, () =>
			{
				ModConfig.HideAvatarOverBudget.Value = !ModConfig.HideAvatarOverBudget.Value;
			});
			AddToggleRow(gHost, "Block Network Exploits", () => ModConfig.NetworkLogEnabled.Value, () =>
			{
				ModConfig.NetworkLogEnabled.Value = !ModConfig.NetworkLogEnabled.Value;
			});
			AddToggleRow(gHost, "Global Udon (Networked Interact)", () => ModConfig.GlobalUdonInteract.Value, () =>
			{
				ModConfig.GlobalUdonInteract.Value = !ModConfig.GlobalUdonInteract.Value;
				Toast.Show(ModConfig.GlobalUdonInteract.Value ? "Global Udon: ON" : "Global Udon: OFF");
			});

			CreateSection(root, "Custom Username (Udon Spoof)", out var uHost);
			AddInputRow(uHost, "Custom Username",
				"The name the world's Udon scripts read for you. World player lists, leaderboards and name signs show it, and when a world syncs the name it read, everyone in the instance sees it. VRChat's own nameplate and QuickMenu keep your real name. Re-applied every time you join a world. Leave it empty to switch it off.",
				"Enter custom username (e.g. Kawaiix)...",
				() => ModConfig.UdonNameSpoof.Value,
				name =>
				{
					string s = SanitizeInput(name, 32);
					ModConfig.UdonNameSpoof.Value = s;
					Toast.Show(string.IsNullOrEmpty(s) ? "Custom Username: OFF" : $"Custom Username: {s}");
				},
				val => string.IsNullOrEmpty(val)
					? "<color=#22D3EE>✓ off — worlds read your real name again</color>"
					: $"<color=#3BFF7A>●</color> <color=#22D3EE>active: <b>{val}</b> (worlds read this name)</color>",
				("APPLY", input =>
				{
					string val = SanitizeInput(input != null ? input.text : "", 32);
					ModConfig.UdonNameSpoof.Value = val;
					if (input != null) input.text = val;
					Toast.Show(string.IsNullOrEmpty(val) ? "Custom Username: OFF" : $"Custom Username: {val}");
				}, new Color(0.92f, 0.22f, 0.58f, 0.95f)),
				("RANDOM", input =>
				{
					string[] names = new[] { "Kawaiix", "CyberGhost", "VRCElite", "Starlight", "ZeroTwo", "PhantomX", "NeonBlade", "ShadowFox", "NexusCore", "VRChatVIP" };
					string r = names[UnityEngine.Random.Range(0, names.Length)];
					if (input != null) input.text = r;
					ModConfig.UdonNameSpoof.Value = r;
					Toast.Show($"Random Username: {r}");
				}, new Color(0.55f, 0.30f, 0.85f, 0.95f)),
				("OFF", input =>
				{
					if (input != null) input.text = "";
					ModConfig.UdonNameSpoof.Value = "";
					Toast.Show("Custom Username: OFF");
				}, new Color(0.38f, 0.16f, 0.24f, 0.95f))
			);

			CreateSection(root, "Spoofing", out var sHost);
			AddToggleRow(sHost, "VRC+ Features Spoof", () => ModConfig.VrcPlusSpoof.Value, () =>
			{
				ModConfig.VrcPlusSpoof.Value = !ModConfig.VrcPlusSpoof.Value;
			});
			AddToggleRow(sHost, "Fast Serialization Rate", () => ModConfig.FastSync.Value, () =>
			{
				ModConfig.FastSync.Value = !ModConfig.FastSync.Value;
			});
			AddToggleRow(sHost, "Store Ownership Spoof", () => ModConfig.StoreOwnership.Value, () =>
			{
				ModConfig.StoreOwnership.Value = !ModConfig.StoreOwnership.Value;
			});
		}

		private void BuildMovementPage(Transform root)
		{
			CreateSection(root, "Flight & Noclip", out var fHost);
			AddToggleRow(fHost, "Fly Mode", () => ModConfig.FlyEnabled.Value, () => ModConfig.FlyEnabled.Value = !ModConfig.FlyEnabled.Value);
			AddStepperRow(fHost, "Fly Speed", ModConfig.FlySpeed, 1.0f, 8.0f, 1.0f, 60.0f);
			AddToggleRow(fHost, "Ghost / Noclip", () => GhostModule.Active, GhostModule.Toggle);
			AddToggleRow(fHost, "Click to Teleport (RMB + LMB)", () => ModConfig.ClickTpEnabled.Value, () => ModConfig.ClickTpEnabled.Value = !ModConfig.ClickTpEnabled.Value);

			CreateSection(root, "Speed & Jump", out var sHost);
			AddToggleRow(sHost, "Walk Speed Modifier", () => ModConfig.WalkMod.Value, () => ModConfig.WalkMod.Value = !ModConfig.WalkMod.Value);
			AddStepperRow(sHost, "Walk Speed", ModConfig.WalkSpeed, 0.5f, 4.0f, 0.1f, 20.0f);
			AddToggleRow(sHost, "Run Speed Modifier", () => ModConfig.RunMod.Value, () => ModConfig.RunMod.Value = !ModConfig.RunMod.Value);
			AddStepperRow(sHost, "Run Speed", ModConfig.RunSpeed, 0.5f, 6.0f, 0.1f, 40.0f);
			AddToggleRow(sHost, "Jump Modifier", () => ModConfig.JumpMod.Value, () => ModConfig.JumpMod.Value = !ModConfig.JumpMod.Value);
			AddStepperRow(sHost, "Jump Power", ModConfig.JumpImpulse, 0.5f, 3.0f, 0.5f, 25.0f);
			AddToggleRow(sHost, "Force Jump (Bypass World Jump Lock)", () => ForceJumpModule.Active, ForceJumpModule.Toggle);
			AddActionRow(sHost, "Reset Speed & Jump to World Default", "Reset", SpeedModule.ResetToWorld);

			CreateSection(root, "Player Rotator & Body Invert", out var rHost);
			AddToggleRow(rHost, "Player Rotator (Tilt & Flip)", () => PlayerRotatorModule.Active, PlayerRotatorModule.Toggle);
			AddButtonGroupRow(rHost, new (string Label, string IconHints, Color BgColor, Action OnClick)[]
			{
				("🔄 Flip 180° (Upside Down)", "ic_rotate", new Color(0.55f, 0.30f, 0.85f, 0.95f), PlayerRotatorModule.Flip),
				("⬆️ Reset Upright", "ic_reset", new Color(0.20f, 0.24f, 0.35f, 0.95f), PlayerRotatorModule.ResetUpright)
			});

			CreateSection(root, "What You Send (Network State)", out var wHost);
			AddToggleRow(wHost, "Ghost Mode (Freeze Position for Others)", () => GhostModule.Active, () =>
			{
				GhostModule.Toggle();
				Toast.Show(GhostModule.Active ? "Ghost Mode: ON (frozen for others)" : "Ghost Mode: OFF");
			});
			AddToggleRow(wHost, "Fast Serialization Rate (Smooth Pose Sync)", () => ModConfig.FastSync.Value, () =>
			{
				ModConfig.FastSync.Value = !ModConfig.FastSync.Value;
				Toast.Show(ModConfig.FastSync.Value ? "Fast Sync: ON (faster updates to others)" : "Fast Sync: OFF");
			});
		}

		private void BuildVisualsPage(Transform root)
		{
			CreateSection(root, "Player Outlines & ESP", out var eHost);
			AddToggleRow(eHost, "Player Box ESP (2D Boxes & Names)", () => ModConfig.EspEnabled.Value, () =>
			{
				ModConfig.EspEnabled.Value = !ModConfig.EspEnabled.Value;
				Toast.Show(ModConfig.EspEnabled.Value ? "Box ESP: ON" : "Box ESP: OFF");
			});
			AddToggleRow(eHost, "Avatar 3D Capsules (Capsule ESP)", () => ModConfig.EspCapsule.Value, () =>
			{
				ModConfig.EspCapsule.Value = !ModConfig.EspCapsule.Value;
				CapsuleEspModule.TriggerRelight();
				Toast.Show(ModConfig.EspCapsule.Value ? "Capsule ESP: ON" : "Capsule ESP: OFF");
			});
			AddToggleRow(eHost, "Avatar Mesh Outlines (HighlightsFX)", () => ModConfig.EspHighlight.Value, () =>
			{
				ModConfig.EspHighlight.Value = !ModConfig.EspHighlight.Value;
				HighlightEspModule.TriggerRelight();
				Toast.Show(ModConfig.EspHighlight.Value ? "Outlines: ON" : "Outlines: OFF");
			});
			AddToggleRow(eHost, "ESP Nameplate (See Through Walls)", () => ModConfig.NameplateEsp != null && ModConfig.NameplateEsp.Value, () =>
			{
				ModConfig.NameplateEsp.Value = !ModConfig.NameplateEsp.Value;
				NameplateEspModule.ApplyNameplateEsp(ModConfig.NameplateEsp.Value);
				Toast.Show(ModConfig.NameplateEsp.Value ? "ESP Nameplate: ON" : "ESP Nameplate: OFF");
			});
			AddToggleRow(eHost, "Show ESP on Self (Alone / 3rd Person)", () => ModConfig.EspShowSelf.Value, () =>
			{
				ModConfig.EspShowSelf.Value = !ModConfig.EspShowSelf.Value;
				HighlightEspModule.TriggerRelight();
				CapsuleEspModule.TriggerRelight();
				Toast.Show(ModConfig.EspShowSelf.Value ? "ESP on Self: ON" : "ESP on Self: OFF");
			});
			AddColorPickerRow(eHost, "Player ESP Color", ModConfig.EspPlayerColorIndex, ColorPalette.EspPlayerColors, () =>
			{
				HighlightEspModule.TriggerRelight();
				CapsuleEspModule.TriggerRelight();
			});
			AddToggleRow(eHost, "See Through Walls", () => ModConfig.EspThroughWalls.Value, () =>
			{
				ModConfig.EspThroughWalls.Value = !ModConfig.EspThroughWalls.Value;
				HighlightEspModule.TriggerRelight();
				CapsuleEspModule.TriggerRelight();
				Toast.Show(ModConfig.EspThroughWalls.Value ? "Through Walls: ON" : "Through Walls: OFF");
			});

			CreateSection(root, "World & Objects ESP", out var wHost);
			AddToggleRow(wHost, "Pickups / Items ESP", () => ModConfig.EspItems.Value, () => ModConfig.EspItems.Value = !ModConfig.EspItems.Value);
			AddColorPickerRow(wHost, "Pickups ESP Color", ModConfig.EspItemColorIndex, ColorPalette.EspItemColors, () =>
			{
				HighlightEspModule.TriggerRelight();
			});
			AddToggleRow(wHost, "Portals ESP", () => ModConfig.EspPortals.Value, () => ModConfig.EspPortals.Value = !ModConfig.EspPortals.Value);
			AddColorPickerRow(wHost, "Portals ESP Color", ModConfig.EspPortalColorIndex, ColorPalette.EspPortalColors, () =>
			{
				HighlightEspModule.TriggerRelight();
			});
			AddToggleRow(wHost, "Fullbright World Lighting", () => QuickMenuTabModule.FullbrightActive, QuickMenuTabModule.ToggleFullbright);

			CreateSection(root, "Radar & World Map", out var rHost);
			AddToggleRow(rHost, "Mini Radar Map", () => ModConfig.RadarEnabled.Value, () => ModConfig.RadarEnabled.Value = !ModConfig.RadarEnabled.Value);
			AddToggleRow(rHost, "Instance Player Roster", () => ModConfig.InstancePanelsEnabled.Value, () => ModConfig.InstancePanelsEnabled.Value = !ModConfig.InstancePanelsEnabled.Value);
			AddToggleRow(rHost, "Ghost (Invisible to Others)", () => GhostModule.Active, GhostModule.Toggle);
		}

		private void BuildHudPage(Transform root)
		{
			CreateSection(root, "Active Features HUD", out var fHost);
			AddToggleRow(fHost, "Show Active Features HUD", () => ModConfig.ActiveFeaturesHudEnabled.Value, () =>
			{
				ModConfig.ActiveFeaturesHudEnabled.Value = !ModConfig.ActiveFeaturesHudEnabled.Value;
				Toast.Show(ModConfig.ActiveFeaturesHudEnabled.Value ? "Active Features HUD: ON" : "Active Features HUD: OFF");
			});
			AddColorPickerRow(fHost, "HUD Movement Color", ModConfig.HudMovementColorIndex, ColorPalette.HudColors);
			AddColorPickerRow(fHost, "HUD ESP Color", ModConfig.HudEspColorIndex, ColorPalette.HudColors);
			AddColorPickerRow(fHost, "HUD Utility Color", ModConfig.HudUtilityColorIndex, ColorPalette.HudColors);
			AddColorPickerRow(fHost, "HUD Security Color", ModConfig.HudSecurityColorIndex, ColorPalette.HudColors);
			AddThemePickerRow(fHost, "HUD Theme Preset", ModConfig.HudThemeIndex, ColorPalette.HudPresets);

			CreateSection(root, "Display & Performance", out var dHost);
			AddToggleRow(dHost, "Bottom Roster Position", () => ModConfig.RosterBottom.Value, () =>
			{
				ModConfig.RosterBottom.Value = !ModConfig.RosterBottom.Value;
				Toast.Show(ModConfig.RosterBottom.Value ? "Roster: Bottom" : "Roster: Top");
			});
			AddToggleRow(dHost, "Performance Profiler HUD", () => ProfilerHudModule.Active, ProfilerHudModule.Toggle);
			AddToggleRow(dHost, "Self Hide (Hide Local Avatar)", () => ModConfig.SelfHide.Value, () =>
			{
				ModConfig.SelfHide.Value = !ModConfig.SelfHide.Value;
				Toast.Show(ModConfig.SelfHide.Value ? "Self Hide: ON" : "Self Hide: OFF");
			});
		}

		private void BuildInteractionsPage(Transform root)
		{
			CreateSection(root, "Pickups & Physics", out var pHost);
			AddToggleRow(pHost, "Force Pickup Any Object (Unlock Locked Pickups)", () => ForcePickupModule.Active, () =>
			{
				ForcePickupModule.Toggle();
				Toast.Show(ForcePickupModule.Active ? "Force Pickup: ON (locked pickups unlocked)" : "Force Pickup: OFF");
			});
			AddToggleRow(pHost, "Float Objects (Zero Gravity Pickups)", () => ObjectGravityModule.Active, () =>
			{
				ObjectGravityModule.Toggle();
				Toast.Show(ObjectGravityModule.Active ? "Float Objects: ON (zero gravity)" : "Float Objects: OFF");
			});
			AddActionRow(pHost, "Respawn All Loose Pickups", "Respawn", QuickMenuTabModule.RespawnAllPickups);

			CreateSection(root, "Mark & Loose Objects", out var mHost);
			AddInfoBox(mHost, "Aim at any spot (or 3m ahead) and place an anchor mark. Loose pickups and objects can then be teleported, orbited, or arranged into shapes around it.");
			AddButtonGroupRow(mHost, new (string Label, string IconHints, Color BgColor, Action OnClick)[]
			{
				("📍 Put Mark", "ic_pin|ic_box", new Color(0.00f, 0.72f, 0.85f, 0.95f), () =>
				{
					MarkModule.PutMark();
					Toast.Show(MarkModule.Status);
				}),
				("🌀 TP Objects", "ic_teleport|ic_fly", new Color(0.25f, 0.45f, 0.85f, 0.95f), () =>
				{
					MarkModule.TeleportObjectsToMark();
					Toast.Show(MarkModule.Status);
				}),
				("💫 Orbit Objects", "ic_spin|ic_loop", new Color(0.55f, 0.30f, 0.85f, 0.95f), () =>
				{
					if (ObjectOrbitModule.Active)
					{
						ObjectOrbitModule.Stop("objects put back");
						Toast.Show("Orbit stopped — objects restored");
					}
					else
					{
						if (!MarkModule.HasMark) MarkModule.PutMark();
						MarkModule.OrbitAroundMark();
						Toast.Show(MarkModule.Status);
					}
				}),
				("❌ Clear Mark", "ic_close|ic_delete", new Color(0.70f, 0.20f, 0.25f, 0.95f), () =>
				{
					MarkModule.Clear();
					Toast.Show("Mark cleared — objects restored");
				})
			});
			AddToggleRow(mHost, "Network Sync (Shapes & TP Seen by Everyone)", () => MarkModule.Mode == "vrchat", () =>
			{
				MarkModule.SetMode(MarkModule.Mode == "vrchat" ? "local" : "vrchat");
				Toast.Show(MarkModule.Status);
			});

			CreateSection(root, "Object Shapes (Around Mark)", out var sHost);
			AddShapeGrid(sHost);

			CreateSection(root, "Audio & Misc", out var aHost);
			AddToggleRow(aHost, "Mute / Deafen Audio", () => QuickMenuTabModule.DeafenActive, QuickMenuTabModule.ToggleDeafen);
		}

		private void BuildToolsPage(Transform root)
		{
			CreateSection(root, "World Video Player (URL Injector)", out var vHost);
			AddInputRow(vHost, "World Video URL",
				"Sends a video URL to every video player in the world (USharpVideo, ProTV, VideoTXL) and asks it to play.",
				"Paste video URL (YouTube, MP4, RTSP)...",
				() => VideoUrlModule.LastUrl,
				url =>
				{
					string s = SanitizeInput(url, 512);
					int count = VideoUrlModule.Inject(s);
					Toast.Show(count > 0 ? $"Sent video to {count} player(s)!" : VideoUrlModule.LastStatus);
				},
				val => string.IsNullOrEmpty(val)
					? "<color=#64748B>Direct video stream (.mp4, m3u8, livestream)</color>"
					: $"<color=#22D3EE>Ready to play in world</color>",
				("Play in World", input =>
				{
					string val = SanitizeInput(input != null ? input.text : "", 512);
					int count = VideoUrlModule.Inject(val);
					Toast.Show(count > 0 ? $"Playing video on {count} player(s)!" : VideoUrlModule.LastStatus);
				}, new Color(0.00f, 0.72f, 0.85f, 0.95f)),
				("Clear", input =>
				{
					if (input != null) input.text = "";
					Toast.Show("Video URL cleared");
				}, new Color(0.24f, 0.26f, 0.32f, 0.90f))
			);

			CreateSection(root, "Force Join Instance", out var jHost);
			AddInputRow(jHost, "World / Instance ID",
				"Paste a world ID, instance ID, or launch link (wrld_..., launch?worldId=...) to join directly.",
				"Enter wrld_... or launch link...",
				() => "",
				id =>
				{
					string s = SanitizeInput(id, 128);
					bool ok = Core.ForceJoin.Go(s);
					Toast.Show(ok ? "Joining room..." : Core.ForceJoin.LastStatus);
				},
				val => "<color=#64748B>Target world or instance ID</color>",
				("Join Now", input =>
				{
					string val = SanitizeInput(input != null ? input.text : "", 128);
					bool ok = Core.ForceJoin.Go(val);
					Toast.Show(ok ? "Joining room..." : Core.ForceJoin.LastStatus);
				}, new Color(0.18f, 0.75f, 0.35f, 0.95f)),
				("Copy Current ID", input =>
				{
					var inst = VaTagsModule.CurrentInstance();
					string curId = !string.IsNullOrEmpty(inst.InstanceId) ? $"{inst.WorldId}:{inst.InstanceId}" : inst.WorldId;
					if (!string.IsNullOrEmpty(curId))
					{
						if (input != null) input.text = curId;
						GUIUtility.systemCopyBuffer = curId;
						Toast.Show($"Copied Instance ID: {curId}");
					}
					else Toast.Show("Could not resolve current instance ID");
				}, new Color(0.55f, 0.30f, 0.85f, 0.95f)),
				("Clear", input =>
				{
					if (input != null) input.text = "";
					Toast.Show("Instance ID cleared");
				}, new Color(0.24f, 0.26f, 0.32f, 0.90f))
			);

			CreateSection(root, "Portals", out var poHost);
			AddToggleRow(poHost, "Infinite Portal (No Despawn / Cooldown)", () => PortalInfiniteModule.Active, () =>
			{
				PortalInfiniteModule.Toggle();
				Toast.Show(PortalInfiniteModule.Active ? "Infinite Portal: ON" : "Infinite Portal: OFF");
			});

			CreateSection(root, "System & Diagnostics", out var tHost);
			AddActionRow(tHost, "Dump UI Hierarchy to Game Console", "Dump Tree", () =>
			{
				UiTreeDumpModule.Request();
				Toast.Show("UI Tree Dump queued!");
			});
			AddActionRow(tHost, "Restart Injections (Re-scan UI)", "Re-Scan", () =>
			{
				_built = false;
				_scanAttempts = 0;
				Toast.Show("Re-scanning UI...");
			});
			AddActionRow(tHost, "Close VRChat Application", "Exit", () => Application.Quit());
		}

		private void CreateSection(Transform parent, string title, out Transform rowHost)
		{
			var secGo = new GameObject("Section_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var secRt = secGo.GetComponent<RectTransform>();
			secRt.SetParent(parent, false);

			var secLe = secGo.AddComponent<LayoutElement>();
			secLe.flexibleWidth = 1f;
			secLe.minWidth = 600f;
			secLe.preferredWidth = -1f;

			var secVlg = secGo.AddComponent<VerticalLayoutGroup>();
			secVlg.childForceExpandHeight = false;
			secVlg.childControlHeight = true;
			secVlg.childForceExpandWidth = true;
			secVlg.childControlWidth = true;
			secVlg.spacing = 8f;

			var secCsf = secGo.AddComponent<ContentSizeFitter>();
			secCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			secCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			// Header Text with neon cyan accent bar
			var hdrGo = new GameObject("Header", Il2CppType.Of<RectTransform>());
			var hdrRt = hdrGo.GetComponent<RectTransform>();
			hdrRt.SetParent(secRt, false);

			var hLe = hdrGo.AddComponent<LayoutElement>();
			hLe.minHeight = 44f;
			hLe.preferredHeight = 44f;
			hLe.flexibleWidth = 1f;

			// Left Neon Cyan Accent Line
			var barGo = new GameObject("AccentBar", Il2CppType.Of<RectTransform>());
			var barRt = barGo.GetComponent<RectTransform>();
			barRt.SetParent(hdrRt, false);
			barRt.anchorMin = new Vector2(0f, 0.15f);
			barRt.anchorMax = new Vector2(0f, 0.85f);
			barRt.pivot = new Vector2(0f, 0.5f);
			barRt.sizeDelta = new Vector2(5f, 0f);
			barRt.anchoredPosition = new Vector2(2f, 0f);
			var barImg = barGo.AddComponent<Image>();
			barImg.color = new Color(0.00f, 0.85f, 1.00f, 1f);

			var txtGo = new GameObject("Text", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(hdrRt, false);
			txtRt.anchorMin = Vector2.zero;
			txtRt.anchorMax = Vector2.one;
			txtRt.offsetMin = new Vector2(18f, 0f);
			txtRt.offsetMax = new Vector2(-8f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;
			tmp.fontSize = 25f;
			tmp.fontStyle = TMPro.FontStyles.Bold;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.color = new Color(0.96f, 0.97f, 1.00f, 1f);
			tmp.text = title;
			tmp.enableWordWrapping = false;

			// Rows Container
			var bodyGo = new GameObject("RowsHost", Il2CppType.Of<RectTransform>());
			var bodyRt = bodyGo.GetComponent<RectTransform>();
			bodyRt.SetParent(secRt, false);

			var bLe = bodyGo.AddComponent<LayoutElement>();
			bLe.flexibleWidth = 1f;
			bLe.minWidth = 600f;
			bLe.preferredWidth = -1f;

			var bVlg = bodyGo.AddComponent<VerticalLayoutGroup>();
			bVlg.childForceExpandHeight = false;
			bVlg.childControlHeight = true;
			bVlg.childForceExpandWidth = true;
			bVlg.childControlWidth = true;
			bVlg.spacing = 8f;

			var bCsf = bodyGo.AddComponent<ContentSizeFitter>();
			bCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			bCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			rowHost = bodyRt;
		}

		private void AddToggleRow(Transform parent, string title, Func<bool> getState, Action toggleAction)
		{
			var rowGo = new GameObject("Row_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(parent, false);

			var le = rowGo.AddComponent<LayoutElement>();
			le.minHeight = 58f;
			le.preferredHeight = 58f;
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var bg = rowGo.AddComponent<Image>();
			bg.color = new Color(0.12f, 0.13f, 0.17f, 0.80f);

			// Title Label on left
			var txtGo = new GameObject("Label", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rowRt, false);
			txtRt.anchorMin = new Vector2(0f, 0f);
			txtRt.anchorMax = new Vector2(1f, 1f);
			txtRt.offsetMin = new Vector2(22f, 0f);
			txtRt.offsetMax = new Vector2(-100f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.fontSize = 21f;
			tmp.color = new Color(0.92f, 0.94f, 0.98f, 1f);
			tmp.text = title;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.enableWordWrapping = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			// Switch Pill on right
			var pillGo = new GameObject("SwitchPill", Il2CppType.Of<RectTransform>());
			var pillRt = pillGo.GetComponent<RectTransform>();
			pillRt.SetParent(rowRt, false);
			pillRt.anchorMin = new Vector2(1f, 0.5f);
			pillRt.anchorMax = new Vector2(1f, 0.5f);
			pillRt.pivot = new Vector2(1f, 0.5f);
			pillRt.anchoredPosition = new Vector2(-20f, 0f);
			pillRt.sizeDelta = new Vector2(58f, 30f);

			var pillImg = pillGo.AddComponent<Image>();
			bool initialOn = false;
			try { initialOn = getState != null && getState(); } catch { }
			pillImg.color = initialOn ? new Color(0.00f, 0.82f, 0.95f, 1f) : new Color(0.24f, 0.26f, 0.31f, 1f);

			// Knob inside switch pill
			var knobGo = new GameObject("Knob", Il2CppType.Of<RectTransform>());
			var knobRt = knobGo.GetComponent<RectTransform>();
			knobRt.SetParent(pillRt, false);
			knobRt.sizeDelta = new Vector2(24f, 24f);
			knobRt.anchorMin = new Vector2(0.5f, 0.5f);
			knobRt.anchorMax = new Vector2(0.5f, 0.5f);
			knobRt.pivot = new Vector2(0.5f, 0.5f);
			knobRt.anchoredPosition = new Vector2(initialOn ? 14f : -14f, 0f);

			var knobImg = knobGo.AddComponent<Image>();
			knobImg.color = Color.white;

			// Row Click Button
			var btn = rowGo.AddComponent<Button>();
			btn.targetGraphic = bg;
			btn.transition = Selectable.Transition.ColorTint;
			var cb = default(ColorBlock);
			cb.normalColor = new Color(0.12f, 0.13f, 0.17f, 0.80f);
			cb.highlightedColor = new Color(0.18f, 0.22f, 0.30f, 0.90f);
			cb.pressedColor = new Color(0.24f, 0.28f, 0.38f, 0.95f);
			cb.selectedColor = new Color(0.12f, 0.13f, 0.17f, 0.80f);
			cb.disabledColor = new Color(0.12f, 0.13f, 0.17f, 0.3f);
			cb.colorMultiplier = 1f;
			cb.fadeDuration = 0.08f;
			btn.colors = cb;

			UiClick.Clear(btn);
			float lastRowToggle = 0f;
			UiClick.AddClick(btn, () =>
			{
				float now = VaClock.Now;
				if (now - lastRowToggle < 0.35f) return;
				lastRowToggle = now;

				toggleAction?.Invoke();
				bool on = false;
				try { on = getState != null && getState(); } catch { }
				pillImg.color = on ? new Color(0.00f, 0.82f, 0.95f, 1f) : new Color(0.24f, 0.26f, 0.31f, 1f);
				knobRt.anchoredPosition = new Vector2(on ? 14f : -14f, 0f);
				MenuCard.PlayClick();
			});

			if (getState != null)
			{
				_toggles.Add(new ToggleTile
				{
					Card = rowRt,
					State = getState,
					Last = initialOn,
					PillImage = pillImg,
					KnobTransform = knobRt
				});
			}
		}

		private void AddStepperRow(Transform parent, string title, BepInEx.Configuration.ConfigEntry<float> cfg,
			float step, float defVal, float min, float max, string format = "0.#")
		{
			var rowGo = new GameObject("Row_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(parent, false);

			var le = rowGo.AddComponent<LayoutElement>();
			le.minHeight = 58f;
			le.preferredHeight = 58f;
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var bg = rowGo.AddComponent<Image>();
			bg.color = new Color(0.12f, 0.13f, 0.17f, 0.80f);

			// Title Label on left
			var txtGo = new GameObject("Label", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rowRt, false);
			txtRt.anchorMin = new Vector2(0f, 0f);
			txtRt.anchorMax = new Vector2(1f, 1f);
			txtRt.offsetMin = new Vector2(22f, 0f);
			txtRt.offsetMax = new Vector2(-210f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.fontSize = 21f;
			tmp.color = new Color(0.92f, 0.94f, 0.98f, 1f);
			tmp.text = title;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.enableWordWrapping = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			// Right Stepper Pill Container: [ - ] [ 3.0 ] [ + ]
			var stepGo = new GameObject("StepperPill", Il2CppType.Of<RectTransform>());
			var stepRt = stepGo.GetComponent<RectTransform>();
			stepRt.SetParent(rowRt, false);
			stepRt.anchorMin = new Vector2(1f, 0.5f);
			stepRt.anchorMax = new Vector2(1f, 0.5f);
			stepRt.pivot = new Vector2(1f, 0.5f);
			stepRt.anchoredPosition = new Vector2(-20f, 0f);
			stepRt.sizeDelta = new Vector2(180f, 38f);

			var stepBg = stepGo.AddComponent<Image>();
			stepBg.color = new Color(0.08f, 0.09f, 0.12f, 0.95f);

			// Minus button on left of pill
			var minusGo = new GameObject("MinusBtn", Il2CppType.Of<RectTransform>());
			var minusRt = minusGo.GetComponent<RectTransform>();
			minusRt.SetParent(stepRt, false);
			minusRt.anchorMin = new Vector2(0f, 0.5f);
			minusRt.anchorMax = new Vector2(0f, 0.5f);
			minusRt.pivot = new Vector2(0f, 0.5f);
			minusRt.anchoredPosition = new Vector2(4f, 0f);
			minusRt.sizeDelta = new Vector2(38f, 32f);

			var mImg = minusGo.AddComponent<Image>();
			mImg.color = new Color(0.18f, 0.20f, 0.26f, 0.9f);
			var mBtn = minusGo.AddComponent<Button>();
			mBtn.targetGraphic = mImg;

			var mTxt = new GameObject("T", Il2CppType.Of<RectTransform>());
			mTxt.transform.SetParent(minusRt, false);
			var mTmp = mTxt.AddComponent<TMPro.TextMeshProUGUI>();
			mTmp.text = "-";
			mTmp.fontSize = 22f;
			mTmp.fontStyle = TMPro.FontStyles.Bold;
			mTmp.alignment = TMPro.TextAlignmentOptions.Center;
			mTmp.color = Color.white;
			if (font != null) mTmp.font = font;

			// Plus button on right of pill
			var plusGo = new GameObject("PlusBtn", Il2CppType.Of<RectTransform>());
			var plusRt = plusGo.GetComponent<RectTransform>();
			plusRt.SetParent(stepRt, false);
			plusRt.anchorMin = new Vector2(1f, 0.5f);
			plusRt.anchorMax = new Vector2(1f, 0.5f);
			plusRt.pivot = new Vector2(1f, 0.5f);
			plusRt.anchoredPosition = new Vector2(-4f, 0f);
			plusRt.sizeDelta = new Vector2(38f, 32f);

			var pImg = plusGo.AddComponent<Image>();
			pImg.color = new Color(0.18f, 0.20f, 0.26f, 0.9f);
			var pBtn = plusGo.AddComponent<Button>();
			pBtn.targetGraphic = pImg;

			var pTxt = new GameObject("T", Il2CppType.Of<RectTransform>());
			pTxt.transform.SetParent(plusRt, false);
			var pTmp = pTxt.AddComponent<TMPro.TextMeshProUGUI>();
			pTmp.text = "+";
			pTmp.fontSize = 22f;
			pTmp.fontStyle = TMPro.FontStyles.Bold;
			pTmp.alignment = TMPro.TextAlignmentOptions.Center;
			pTmp.color = Color.white;
			if (font != null) pTmp.font = font;

			// Value display text between minus and plus
			var valGo = new GameObject("Val", Il2CppType.Of<RectTransform>());
			var valRt = valGo.GetComponent<RectTransform>();
			valRt.SetParent(stepRt, false);
			valRt.anchorMin = Vector2.zero;
			valRt.anchorMax = Vector2.one;
			valRt.offsetMin = new Vector2(42f, 0f);
			valRt.offsetMax = new Vector2(-42f, 0f);

			var vTmp = valGo.AddComponent<TMPro.TextMeshProUGUI>();
			vTmp.text = cfg.Value.ToString(format);
			vTmp.fontSize = 20f;
			vTmp.fontStyle = TMPro.FontStyles.Bold;
			vTmp.alignment = TMPro.TextAlignmentOptions.Center;
			vTmp.color = new Color(0.00f, 0.85f, 1.00f, 1f);
			if (font != null) vTmp.font = font;

			UiClick.AddClick(mBtn, () =>
			{
				cfg.Value = Mathf.Clamp(cfg.Value - step, min, max);
				vTmp.text = cfg.Value.ToString(format);
				if (cfg == ModConfig.JumpImpulse)
				{
					if (ModConfig.ForceJumpForce != null) ModConfig.ForceJumpForce.Value = cfg.Value;
					if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = true;
					try { PlayerRef.LocalApi()?.SetJumpImpulse(cfg.Value); } catch { }
					Toast.Show($"Jump Power: {cfg.Value:0.#} m/s");
				}
				MenuCard.PlayClick();
			});

			UiClick.AddClick(pBtn, () =>
			{
				cfg.Value = Mathf.Clamp(cfg.Value + step, min, max);
				vTmp.text = cfg.Value.ToString(format);
				if (cfg == ModConfig.JumpImpulse)
				{
					if (ModConfig.ForceJumpForce != null) ModConfig.ForceJumpForce.Value = cfg.Value;
					if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = true;
					try { PlayerRef.LocalApi()?.SetJumpImpulse(cfg.Value); } catch { }
					Toast.Show($"Jump Power: {cfg.Value:0.#} m/s");
				}
				MenuCard.PlayClick();
			});

			_steppers.Add(new StepperRef
			{
				Card = rowRt,
				Title = title,
				Cfg = cfg,
				LastVal = cfg.Value,
				Format = format,
				ValText = vTmp
			});
		}

		private void AddColorPickerRow(Transform parent, string title, BepInEx.Configuration.ConfigEntry<int> cfg,
			ColorPalette.NamedColor[] palette, Action onColorChanged = null)
		{
			var rowGo = new GameObject("Row_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(parent, false);

			var le = rowGo.AddComponent<LayoutElement>();
			le.minHeight = 58f;
			le.preferredHeight = 58f;
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var bg = rowGo.AddComponent<Image>();
			bg.color = new Color(0.12f, 0.13f, 0.17f, 0.80f);

			// Title on left
			var txtGo = new GameObject("Label", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rowRt, false);
			txtRt.anchorMin = new Vector2(0f, 0f);
			txtRt.anchorMax = new Vector2(1f, 1f);
			txtRt.offsetMin = new Vector2(22f, 0f);
			txtRt.offsetMax = new Vector2(-360f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.fontSize = 21f;
			tmp.color = new Color(0.92f, 0.94f, 0.98f, 1f);
			tmp.text = title;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.enableWordWrapping = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			// Right Picker Pill: [ ■ Swatch ] [ < ] [ Name ] [ > ] [ 🎨 Palette ]
			var pillGo = new GameObject("ColorPickerPill", Il2CppType.Of<RectTransform>());
			var pillRt = pillGo.GetComponent<RectTransform>();
			pillRt.SetParent(rowRt, false);
			pillRt.anchorMin = new Vector2(1f, 0.5f);
			pillRt.anchorMax = new Vector2(1f, 0.5f);
			pillRt.pivot = new Vector2(1f, 0.5f);
			pillRt.anchoredPosition = new Vector2(-20f, 0f);
			pillRt.sizeDelta = new Vector2(340f, 38f);

			var pillBg = pillGo.AddComponent<Image>();
			pillBg.color = new Color(0.08f, 0.09f, 0.12f, 0.95f);

			// Swatch box on left of pill
			var swatchGo = new GameObject("Swatch", Il2CppType.Of<RectTransform>());
			var swatchRt = swatchGo.GetComponent<RectTransform>();
			swatchRt.SetParent(pillRt, false);
			swatchRt.anchorMin = new Vector2(0f, 0.5f);
			swatchRt.anchorMax = new Vector2(0f, 0.5f);
			swatchRt.pivot = new Vector2(0f, 0.5f);
			swatchRt.anchoredPosition = new Vector2(8f, 0f);
			swatchRt.sizeDelta = new Vector2(24f, 24f);

			var swatchImg = swatchGo.AddComponent<Image>();

			// Left Arrow [<]
			var prevGo = new GameObject("PrevBtn", Il2CppType.Of<RectTransform>());
			var prevRt = prevGo.GetComponent<RectTransform>();
			prevRt.SetParent(pillRt, false);
			prevRt.anchorMin = new Vector2(0f, 0.5f);
			prevRt.anchorMax = new Vector2(0f, 0.5f);
			prevRt.pivot = new Vector2(0f, 0.5f);
			prevRt.anchoredPosition = new Vector2(38f, 0f);
			prevRt.sizeDelta = new Vector2(32f, 32f);

			var prevImg = prevGo.AddComponent<Image>();
			prevImg.color = new Color(0.18f, 0.20f, 0.26f, 0.9f);
			var prevBtn = prevGo.AddComponent<Button>();
			prevBtn.targetGraphic = prevImg;

			var prevTxtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
			prevTxtGo.transform.SetParent(prevRt, false);
			var prevTmp = prevTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			prevTmp.text = "<";
			prevTmp.fontSize = 20f;
			prevTmp.fontStyle = TMPro.FontStyles.Bold;
			prevTmp.alignment = TMPro.TextAlignmentOptions.Center;
			prevTmp.color = Color.white;
			if (font != null) prevTmp.font = font;

			// Palette Drawer Toggle Button [ 🎨 ]
			var palGo = new GameObject("PalBtn", Il2CppType.Of<RectTransform>());
			var palRt = palGo.GetComponent<RectTransform>();
			palRt.SetParent(pillRt, false);
			palRt.anchorMin = new Vector2(1f, 0.5f);
			palRt.anchorMax = new Vector2(1f, 0.5f);
			palRt.pivot = new Vector2(1f, 0.5f);
			palRt.anchoredPosition = new Vector2(-6f, 0f);
			palRt.sizeDelta = new Vector2(40f, 32f);

			var palImg = palGo.AddComponent<Image>();
			palImg.color = new Color(0.20f, 0.24f, 0.35f, 0.95f);
			var palBtn = palGo.AddComponent<Button>();
			palBtn.targetGraphic = palImg;

			var palTxtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
			palTxtGo.transform.SetParent(palRt, false);
			var palTmp = palTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			palTmp.text = "🎨";
			palTmp.fontSize = 18f;
			palTmp.alignment = TMPro.TextAlignmentOptions.Center;
			palTmp.color = Color.white;

			// Right Arrow [>]
			var nextGo = new GameObject("NextBtn", Il2CppType.Of<RectTransform>());
			var nextRt = nextGo.GetComponent<RectTransform>();
			nextRt.SetParent(pillRt, false);
			nextRt.anchorMin = new Vector2(1f, 0.5f);
			nextRt.anchorMax = new Vector2(1f, 0.5f);
			nextRt.pivot = new Vector2(1f, 0.5f);
			nextRt.anchoredPosition = new Vector2(-50f, 0f);
			nextRt.sizeDelta = new Vector2(32f, 32f);

			var nextImg = nextGo.AddComponent<Image>();
			nextImg.color = new Color(0.18f, 0.20f, 0.26f, 0.9f);
			var nextBtn = nextGo.AddComponent<Button>();
			nextBtn.targetGraphic = nextImg;

			var nextTxtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
			nextTxtGo.transform.SetParent(nextRt, false);
			var nextTmp = nextTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			nextTmp.text = ">";
			nextTmp.fontSize = 20f;
			nextTmp.fontStyle = TMPro.FontStyles.Bold;
			nextTmp.alignment = TMPro.TextAlignmentOptions.Center;
			nextTmp.color = Color.white;
			if (font != null) nextTmp.font = font;

			// Center Name Label
			var nameGo = new GameObject("Name", Il2CppType.Of<RectTransform>());
			var nameRt = nameGo.GetComponent<RectTransform>();
			nameRt.SetParent(pillRt, false);
			nameRt.anchorMin = Vector2.zero;
			nameRt.anchorMax = Vector2.one;
			nameRt.offsetMin = new Vector2(74f, 0f);
			nameRt.offsetMax = new Vector2(-86f, 0f);

			var nameTmp = nameGo.AddComponent<TMPro.TextMeshProUGUI>();
			nameTmp.fontSize = 16.5f;
			nameTmp.fontStyle = TMPro.FontStyles.Bold;
			nameTmp.alignment = TMPro.TextAlignmentOptions.Center;
			nameTmp.enableWordWrapping = false;
			if (font != null) nameTmp.font = font;

			// Expandable Palette Grid Container (right below the row)
			var palettePanelGo = new GameObject("PaletteGrid_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var palPanelRt = palettePanelGo.GetComponent<RectTransform>();
			palPanelRt.SetParent(parent, false);

			var palLe = palettePanelGo.AddComponent<LayoutElement>();
			palLe.flexibleWidth = 1f;
			palLe.minWidth = 600f;
			palLe.preferredWidth = -1f;

			var palVlg = palettePanelGo.AddComponent<VerticalLayoutGroup>();
			palVlg.padding = new RectOffset(16, 16, 12, 12);
			palVlg.spacing = 8f;
			palVlg.childControlWidth = true;
			palVlg.childControlHeight = true;
			palVlg.childForceExpandWidth = true;
			palVlg.childForceExpandHeight = false;

			var palPanelBg = palettePanelGo.AddComponent<Image>();
			palPanelBg.color = new Color(0.08f, 0.09f, 0.12f, 0.95f);

			var palCsf = palettePanelGo.AddComponent<ContentSizeFitter>();
			palCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			palCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			// Grid inside palette panel
			var gridGo = new GameObject("Grid", Il2CppType.Of<RectTransform>());
			var gridRt = gridGo.GetComponent<RectTransform>();
			gridRt.SetParent(palPanelRt, false);

			var glg = gridGo.AddComponent<GridLayoutGroup>();
			glg.cellSize = new Vector2(170f, 44f);
			glg.spacing = new Vector2(8f, 8f);
			glg.startCorner = GridLayoutGroup.Corner.UpperLeft;
			glg.startAxis = GridLayoutGroup.Axis.Horizontal;
			glg.childAlignment = TextAnchor.UpperLeft;

			var gridCsf = gridGo.AddComponent<ContentSizeFitter>();
			gridCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			gridCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			var tileBgs = new List<Image>();
			var tileBorders = new List<Image>();

			Action updateDisplay = () =>
			{
				int idx = Mathf.Clamp(cfg.Value, 0, palette.Length - 1);
				var item = palette[idx];
				nameTmp.text = item.Name;
				bool isClear = item.Color.a < 0.1f;
				nameTmp.color = isClear ? Color.white : item.Color;
				swatchImg.color = isClear ? new Color(0.25f, 0.45f, 0.85f, 1f) : item.Color;

				// Update tile highlights
				for (int i = 0; i < tileBgs.Count && i < palette.Length; i++)
				{
					bool sel = (i == idx);
					if (tileBgs[i] != null)
						tileBgs[i].color = sel ? new Color(0.18f, 0.24f, 0.36f, 0.95f) : new Color(0.12f, 0.13f, 0.17f, 0.85f);
					if (tileBorders[i] != null)
						tileBorders[i].color = sel ? new Color(0.00f, 0.85f, 1.00f, 1f) : new Color(0.20f, 0.22f, 0.28f, 0.4f);
				}
			};

			// Populate color tiles in grid
			for (int i = 0; i < palette.Length; i++)
			{
				int colorIdx = i;
				var cItem = palette[i];

				var tileGo = new GameObject("Tile_" + cItem.Name.Replace(" ", ""), Il2CppType.Of<RectTransform>());
				var tileRt = tileGo.GetComponent<RectTransform>();
				tileRt.SetParent(gridRt, false);

				var tileImg = tileGo.AddComponent<Image>();
				tileImg.color = (colorIdx == cfg.Value) ? new Color(0.18f, 0.24f, 0.36f, 0.95f) : new Color(0.12f, 0.13f, 0.17f, 0.85f);
				tileBgs.Add(tileImg);

				var tileBtn = tileGo.AddComponent<Button>();
				tileBtn.targetGraphic = tileImg;

				// Border line on left of tile
				var borderGo = new GameObject("Border", Il2CppType.Of<RectTransform>());
				var borderRt = borderGo.GetComponent<RectTransform>();
				borderRt.SetParent(tileRt, false);
				borderRt.anchorMin = new Vector2(0f, 0f);
				borderRt.anchorMax = new Vector2(0f, 1f);
				borderRt.pivot = new Vector2(0f, 0.5f);
				borderRt.sizeDelta = new Vector2(4f, 0f);
				borderRt.anchoredPosition = Vector2.zero;

				var borderImg = borderGo.AddComponent<Image>();
				borderImg.color = (colorIdx == cfg.Value) ? new Color(0.00f, 0.85f, 1.00f, 1f) : new Color(0.20f, 0.22f, 0.28f, 0.4f);
				tileBorders.Add(borderImg);

				// Swatch box inside tile
				var tSwatchGo = new GameObject("Swatch", Il2CppType.Of<RectTransform>());
				var tSwatchRt = tSwatchGo.GetComponent<RectTransform>();
				tSwatchRt.SetParent(tileRt, false);
				tSwatchRt.anchorMin = new Vector2(0f, 0.5f);
				tSwatchRt.anchorMax = new Vector2(0f, 0.5f);
				tSwatchRt.pivot = new Vector2(0f, 0.5f);
				tSwatchRt.anchoredPosition = new Vector2(10f, 0f);
				tSwatchRt.sizeDelta = new Vector2(20f, 20f);

				var tSwatchImg = tSwatchGo.AddComponent<Image>();
				tSwatchImg.color = (cItem.Color.a < 0.1f) ? new Color(0.25f, 0.45f, 0.85f, 1f) : cItem.Color;

				// Name text inside tile
				var tNameGo = new GameObject("Text", Il2CppType.Of<RectTransform>());
				var tNameRt = tNameGo.GetComponent<RectTransform>();
				tNameRt.SetParent(tileRt, false);
				tNameRt.anchorMin = Vector2.zero;
				tNameRt.anchorMax = Vector2.one;
				tNameRt.offsetMin = new Vector2(36f, 0f);
				tNameRt.offsetMax = new Vector2(-4f, 0f);

				var tNameTmp = tNameGo.AddComponent<TMPro.TextMeshProUGUI>();
				tNameTmp.fontSize = 14.5f;
				tNameTmp.fontStyle = TMPro.FontStyles.Bold;
				tNameTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
				tNameTmp.enableWordWrapping = false;
				tNameTmp.text = cItem.Name;
				tNameTmp.color = (cItem.Color.a < 0.1f) ? Color.white : cItem.Color;
				if (font != null) tNameTmp.font = font;

				UiClick.AddClick(tileBtn, () =>
				{
					cfg.Value = colorIdx;
					updateDisplay();
					onColorChanged?.Invoke();
					MenuCard.PlayClick();
					Toast.Show($"{title}: {cItem.Name}");
				});
			}

			// Initially start with palette closed
			palettePanelGo.SetActive(false);

			// Toggle palette on button click
			Action togglePalette = () =>
			{
				bool open = !palettePanelGo.activeSelf;
				palettePanelGo.SetActive(open);
				palImg.color = open ? new Color(0.00f, 0.85f, 1.00f, 0.95f) : new Color(0.20f, 0.24f, 0.35f, 0.95f);
				palTmp.color = open ? Color.black : Color.white;
				MenuCard.PlayClick();
			};

			UiClick.AddClick(palBtn, togglePalette);

			updateDisplay();

			UiClick.AddClick(prevBtn, () =>
			{
				int cur = cfg.Value;
				cur--;
				if (cur < 0) cur = palette.Length - 1;
				cfg.Value = cur;
				updateDisplay();
				onColorChanged?.Invoke();
				MenuCard.PlayClick();
			});

			UiClick.AddClick(nextBtn, () =>
			{
				int cur = cfg.Value;
				cur++;
				if (cur >= palette.Length) cur = 0;
				cfg.Value = cur;
				updateDisplay();
				onColorChanged?.Invoke();
				MenuCard.PlayClick();
			});
		}

		private void AddThemePickerRow(Transform parent, string title, BepInEx.Configuration.ConfigEntry<int> cfg,
			ColorPalette.HudPreset[] presets)
		{
			var rowGo = new GameObject("Row_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(parent, false);

			var le = rowGo.AddComponent<LayoutElement>();
			le.minHeight = 58f;
			le.preferredHeight = 58f;
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var bg = rowGo.AddComponent<Image>();
			bg.color = new Color(0.12f, 0.13f, 0.17f, 0.80f);

			// Title on left
			var txtGo = new GameObject("Label", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rowRt, false);
			txtRt.anchorMin = new Vector2(0f, 0f);
			txtRt.anchorMax = new Vector2(1f, 1f);
			txtRt.offsetMin = new Vector2(22f, 0f);
			txtRt.offsetMax = new Vector2(-280f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.fontSize = 21f;
			tmp.color = new Color(0.92f, 0.94f, 0.98f, 1f);
			tmp.text = title;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.enableWordWrapping = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			// Right Pill: [ < ] [ Preset Name ] [ > ]
			var pillGo = new GameObject("ThemePill", Il2CppType.Of<RectTransform>());
			var pillRt = pillGo.GetComponent<RectTransform>();
			pillRt.SetParent(rowRt, false);
			pillRt.anchorMin = new Vector2(1f, 0.5f);
			pillRt.anchorMax = new Vector2(1f, 0.5f);
			pillRt.pivot = new Vector2(1f, 0.5f);
			pillRt.anchoredPosition = new Vector2(-20f, 0f);
			pillRt.sizeDelta = new Vector2(260f, 38f);

			var pillBg = pillGo.AddComponent<Image>();
			pillBg.color = new Color(0.08f, 0.09f, 0.12f, 0.95f);

			// Left Arrow [<]
			var prevGo = new GameObject("PrevBtn", Il2CppType.Of<RectTransform>());
			var prevRt = prevGo.GetComponent<RectTransform>();
			prevRt.SetParent(pillRt, false);
			prevRt.anchorMin = new Vector2(0f, 0.5f);
			prevRt.anchorMax = new Vector2(0f, 0.5f);
			prevRt.pivot = new Vector2(0f, 0.5f);
			prevRt.anchoredPosition = new Vector2(6f, 0f);
			prevRt.sizeDelta = new Vector2(32f, 32f);

			var prevImg = prevGo.AddComponent<Image>();
			prevImg.color = new Color(0.18f, 0.20f, 0.26f, 0.9f);
			var prevBtn = prevGo.AddComponent<Button>();
			prevBtn.targetGraphic = prevImg;

			var prevTxtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
			prevTxtGo.transform.SetParent(prevRt, false);
			var prevTmp = prevTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			prevTmp.text = "<";
			prevTmp.fontSize = 20f;
			prevTmp.fontStyle = TMPro.FontStyles.Bold;
			prevTmp.alignment = TMPro.TextAlignmentOptions.Center;
			prevTmp.color = Color.white;
			if (font != null) prevTmp.font = font;

			// Right Arrow [>]
			var nextGo = new GameObject("NextBtn", Il2CppType.Of<RectTransform>());
			var nextRt = nextGo.GetComponent<RectTransform>();
			nextRt.SetParent(pillRt, false);
			nextRt.anchorMin = new Vector2(1f, 0.5f);
			nextRt.anchorMax = new Vector2(1f, 0.5f);
			nextRt.pivot = new Vector2(1f, 0.5f);
			nextRt.anchoredPosition = new Vector2(-6f, 0f);
			nextRt.sizeDelta = new Vector2(32f, 32f);

			var nextImg = nextGo.AddComponent<Image>();
			nextImg.color = new Color(0.18f, 0.20f, 0.26f, 0.9f);
			var nextBtn = nextGo.AddComponent<Button>();
			nextBtn.targetGraphic = nextImg;

			var nextTxtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
			nextTxtGo.transform.SetParent(nextRt, false);
			var nextTmp = nextTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			nextTmp.text = ">";
			nextTmp.fontSize = 20f;
			nextTmp.fontStyle = TMPro.FontStyles.Bold;
			nextTmp.alignment = TMPro.TextAlignmentOptions.Center;
			nextTmp.color = Color.white;
			if (font != null) nextTmp.font = font;

			// Center Preset Name
			var nameGo = new GameObject("Name", Il2CppType.Of<RectTransform>());
			var nameRt = nameGo.GetComponent<RectTransform>();
			nameRt.SetParent(pillRt, false);
			nameRt.anchorMin = Vector2.zero;
			nameRt.anchorMax = Vector2.one;
			nameRt.offsetMin = new Vector2(40f, 0f);
			nameRt.offsetMax = new Vector2(-40f, 0f);

			var nameTmp = nameGo.AddComponent<TMPro.TextMeshProUGUI>();
			nameTmp.fontSize = 16.5f;
			nameTmp.fontStyle = TMPro.FontStyles.Bold;
			nameTmp.alignment = TMPro.TextAlignmentOptions.Center;
			nameTmp.enableWordWrapping = false;
			nameTmp.color = new Color(0.00f, 0.85f, 1.00f, 1f);
			if (font != null) nameTmp.font = font;

			Action applyPreset = () =>
			{
				int idx = Mathf.Clamp(cfg.Value, 0, presets.Length - 1);
				var p = presets[idx];
				nameTmp.text = p.Name;
				ModConfig.HudMovementColorIndex.Value = p.MovementIdx;
				ModConfig.HudEspColorIndex.Value = p.EspIdx;
				ModConfig.HudUtilityColorIndex.Value = p.UtilityIdx;
				ModConfig.HudSecurityColorIndex.Value = p.SecurityIdx;
			};

			int initIdx = Mathf.Clamp(cfg.Value, 0, presets.Length - 1);
			nameTmp.text = presets[initIdx].Name;

			UiClick.AddClick(prevBtn, () =>
			{
				int cur = cfg.Value;
				cur--;
				if (cur < 0) cur = presets.Length - 1;
				cfg.Value = cur;
				applyPreset();
				Toast.Show($"HUD Theme: {presets[cur].Name}");
				MenuCard.PlayClick();
			});

			UiClick.AddClick(nextBtn, () =>
			{
				int cur = cfg.Value;
				cur++;
				if (cur >= presets.Length) cur = 0;
				cfg.Value = cur;
				applyPreset();
				Toast.Show($"HUD Theme: {presets[cur].Name}");
				MenuCard.PlayClick();
			});
		}

		private void AddActionRow(Transform parent, string title, string btnLabel, Action action)
		{
			var rowGo = new GameObject("Row_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(parent, false);

			var le = rowGo.AddComponent<LayoutElement>();
			le.minHeight = 58f;
			le.preferredHeight = 58f;
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var bg = rowGo.AddComponent<Image>();
			bg.color = new Color(0.12f, 0.13f, 0.17f, 0.80f);

			// Title Label on left
			var txtGo = new GameObject("Label", Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(rowRt, false);
			txtRt.anchorMin = new Vector2(0f, 0f);
			txtRt.anchorMax = new Vector2(1f, 1f);
			txtRt.offsetMin = new Vector2(22f, 0f);
			txtRt.offsetMax = new Vector2(-160f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.fontSize = 21f;
			tmp.color = new Color(0.92f, 0.94f, 0.98f, 1f);
			tmp.text = title;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.enableWordWrapping = false;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;

			// Action Button on right
			var btnGo = new GameObject("ActionBtn", Il2CppType.Of<RectTransform>());
			var btnRt = btnGo.GetComponent<RectTransform>();
			btnRt.SetParent(rowRt, false);
			btnRt.anchorMin = new Vector2(1f, 0.5f);
			btnRt.anchorMax = new Vector2(1f, 0.5f);
			btnRt.pivot = new Vector2(1f, 0.5f);
			btnRt.anchoredPosition = new Vector2(-20f, 0f);
			btnRt.sizeDelta = new Vector2(136f, 38f);

			var bImg = btnGo.AddComponent<Image>();
			bImg.color = new Color(0.20f, 0.24f, 0.32f, 0.95f);
			bImg.raycastTarget = true;

			var btn = btnGo.AddComponent<Button>();
			btn.targetGraphic = bImg;
			var nav = new Navigation();
			nav.mode = Navigation.Mode.None;
			btn.navigation = nav;

			var bTxtGo = new GameObject("Text", Il2CppType.Of<RectTransform>());
			var bTxtRt = bTxtGo.GetComponent<RectTransform>();
			bTxtRt.SetParent(btnRt, false);
			bTxtRt.anchorMin = Vector2.zero;
			bTxtRt.anchorMax = Vector2.one;
			bTxtRt.offsetMin = Vector2.zero;
			bTxtRt.offsetMax = Vector2.zero;

			var bTmp = bTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			bTmp.fontSize = 17.5f;
			bTmp.fontStyle = TMPro.FontStyles.Bold;
			bTmp.color = Color.white;
			bTmp.alignment = TMPro.TextAlignmentOptions.Center;
			bTmp.text = btnLabel;
			bTmp.raycastTarget = false;
			if (font != null) bTmp.font = font;

			UiClick.AddClick(btn, action);
			UiClick.AddClick(btn, MenuCard.PlayClick);
		}

		public sealed class InputAdapter
		{
			private string _val;
			private readonly Action<string> _setter;

			public InputAdapter(string initialText, Action<string> setter)
			{
				_val = initialText ?? "";
				_setter = setter;
			}

			public string text
			{
				get => _val;
				set
				{
					_val = value ?? "";
					_setter?.Invoke(_val);
				}
			}
		}

		private static InputRowState _activeInputRow;
		private static float _cursorBlinkTimer;
		private static bool _cursorBlinkOn;

		private sealed class InputRowState
		{
			public string Title;
			public string Buffer;
			public string Placeholder;
			public Action<string> OnApply;
			public Action<string> UpdateVisual;
			public InputAdapter Adapter;
		}

		private static string SanitizeInput(string raw, int maxLength = 32, bool singleLine = true)
		{
			if (string.IsNullOrEmpty(raw)) return "";
			string s = raw.Trim();
			if (singleLine && (s.Contains("\n") || s.Contains("\r")))
			{
				var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
				s = lines.Length > 0 ? lines[0].Trim() : "";
			}
			if (s.Length > maxLength) s = s.Substring(0, maxLength).Trim();
			return s;
		}

		private static int GetInputMaxLen(string title)
		{
			if (string.IsNullOrEmpty(title)) return 64;
			if (title.IndexOf("Username", StringComparison.OrdinalIgnoreCase) >= 0) return 32;
			if (title.IndexOf("URL", StringComparison.OrdinalIgnoreCase) >= 0) return 512;
			if (title.IndexOf("ID", StringComparison.OrdinalIgnoreCase) >= 0) return 128;
			return 64;
		}

		private static void HandleInlineTyping()
		{
			if (_activeInputRow == null) return;

			int maxLen = GetInputMaxLen(_activeInputRow.Title);

			// Handle Enter: commit and apply
			if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
			{
				string res = SanitizeInput(_activeInputRow.Buffer, maxLen);
				_activeInputRow.Adapter.text = res;
				_activeInputRow.OnApply?.Invoke(res);
				Toast.Show(string.IsNullOrEmpty(res) ? $"{_activeInputRow.Title}: Cleared" : $"{_activeInputRow.Title}: {res}");
				_activeInputRow = null;
				return;
			}

			// Handle Escape: cancel
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				_activeInputRow.Adapter.text = _activeInputRow.Adapter.text;
				_activeInputRow = null;
				return;
			}

			// Handle Ctrl+V: paste from clipboard
			if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.V))
			{
				string clip = (GUIUtility.systemCopyBuffer ?? "").Trim();
				if (!string.IsNullOrEmpty(clip))
				{
					string cleanClip = SanitizeInput(clip, maxLen);
					_activeInputRow.Buffer = cleanClip;
				}
			}

			// Handle Backspace: delete character
			if (Input.GetKeyDown(KeyCode.Backspace))
			{
				if (_activeInputRow.Buffer.Length > 0)
				{
					_activeInputRow.Buffer = _activeInputRow.Buffer.Substring(0, _activeInputRow.Buffer.Length - 1);
				}
			}

			// Handle regular typing characters
			string typed = Input.inputString;
			if (!string.IsNullOrEmpty(typed))
			{
				foreach (char c in typed)
				{
					if (c == '\b' || c == '\r' || c == '\n') continue;
					if (c >= ' ' && _activeInputRow.Buffer.Length < maxLen)
					{
						_activeInputRow.Buffer += c;
					}
				}
			}

			// Cursor blink
			float now = VaClock.Now;
			if (now >= _cursorBlinkTimer)
			{
				_cursorBlinkTimer = now + 0.45f;
				_cursorBlinkOn = !_cursorBlinkOn;
			}

			string cursor = _cursorBlinkOn ? "<color=#00E5FF><b>|</b></color>" : " ";
			_activeInputRow.UpdateVisual(_activeInputRow.Buffer + cursor);
		}

		private void AddInputRow(Transform parent, string title, string subtitle, string placeholder,
			Func<string> getVal, Action<string> onApply,
			params (string Label, Action<InputAdapter> OnClick, Color BgColor)[] extraButtons)
		{
			AddInputRow(parent, title, subtitle, placeholder, getVal, onApply, null, extraButtons);
		}

		private void AddInputRow(Transform parent, string title, string subtitle, string placeholder,
			Func<string> getVal, Action<string> onApply,
			Func<string, string> getStatusText,
			params (string Label, Action<InputAdapter> OnClick, Color BgColor)[] extraButtons)
		{
			var cardGo = new GameObject("Card_" + title.Replace(" ", ""), Il2CppType.Of<RectTransform>());
			var cardRt = cardGo.GetComponent<RectTransform>();
			cardRt.SetParent(parent, false);

			var le = cardGo.AddComponent<LayoutElement>();
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var vlg = cardGo.AddComponent<VerticalLayoutGroup>();
			vlg.padding = new RectOffset(20, 20, 16, 16);
			vlg.spacing = 10f;
			vlg.childControlWidth = true;
			vlg.childControlHeight = true;
			vlg.childForceExpandWidth = true;
			vlg.childForceExpandHeight = false;

			var bg = cardGo.AddComponent<Image>();
			bg.color = new Color(0.12f, 0.13f, 0.17f, 0.85f);

			var csf = cardGo.AddComponent<ContentSizeFitter>();
			csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			var font = MenuCard.StealFont();

			// 1. Header Line (Title + Subtitle)
			var topGo = new GameObject("HeaderLine", Il2CppType.Of<RectTransform>());
			var topRt = topGo.GetComponent<RectTransform>();
			topRt.SetParent(cardRt, false);

			var topVlg = topGo.AddComponent<VerticalLayoutGroup>();
			topVlg.spacing = 3f;
			topVlg.childControlWidth = true;
			topVlg.childControlHeight = true;
			topVlg.childForceExpandWidth = true;
			topVlg.childForceExpandHeight = false;

			var titleGo = new GameObject("Title", Il2CppType.Of<RectTransform>());
			titleGo.transform.SetParent(topRt, false);
			var titleTmp = titleGo.AddComponent<TMPro.TextMeshProUGUI>();
			titleTmp.text = title;
			titleTmp.fontSize = 21f;
			titleTmp.fontStyle = TMPro.FontStyles.Bold;
			titleTmp.color = new Color(0.94f, 0.96f, 1f, 1f);
			if (font != null) titleTmp.font = font;

			if (!string.IsNullOrEmpty(subtitle))
			{
				var subGo = new GameObject("Sub", Il2CppType.Of<RectTransform>());
				subGo.transform.SetParent(topRt, false);
				var subTmp = subGo.AddComponent<TMPro.TextMeshProUGUI>();
				subTmp.text = subtitle;
				subTmp.fontSize = 14.5f;
				subTmp.enableWordWrapping = true;
				subTmp.color = new Color(0.65f, 0.70f, 0.82f, 1f);
				if (font != null) subTmp.font = font;
			}

			// 2. Full-Width Clean Input Box (Row 1)
			var inputBarGo = new GameObject("InputBar", Il2CppType.Of<RectTransform>());
			var inputBarRt = inputBarGo.GetComponent<RectTransform>();
			inputBarRt.SetParent(cardRt, false);

			var inBarLe = inputBarGo.AddComponent<LayoutElement>();
			inBarLe.minHeight = 48f;
			inBarLe.preferredHeight = 48f;
			inBarLe.flexibleWidth = 1f;

			var inBarBg = inputBarGo.AddComponent<Image>();
			inBarBg.color = new Color(0.07f, 0.08f, 0.11f, 0.95f);

			var inBarBtn = inputBarGo.AddComponent<Button>();
			inBarBtn.targetGraphic = inBarBg;

			var displayTxtGo = new GameObject("DisplayTxt", Il2CppType.Of<RectTransform>());
			var displayTxtRt = displayTxtGo.GetComponent<RectTransform>();
			displayTxtRt.SetParent(inputBarRt, false);
			displayTxtRt.anchorMin = Vector2.zero;
			displayTxtRt.anchorMax = Vector2.one;
			displayTxtRt.offsetMin = new Vector2(16f, 0f);
			displayTxtRt.offsetMax = new Vector2(-16f, 0f);

			var displayTmp = displayTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
			displayTmp.fontSize = 18f;
			displayTmp.fontStyle = TMPro.FontStyles.Normal;
			displayTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			displayTmp.enableWordWrapping = false;
			if (font != null) displayTmp.font = font;

			TMPro.TextMeshProUGUI statusTmp = null;
			string currentVal = getVal != null ? (getVal() ?? "") : "";

			Action<string> updateDisplay = (val) =>
			{
				if (displayTmp != null)
				{
					if (string.IsNullOrEmpty(val))
					{
						displayTmp.text = $"<color=#64748B>✏️  {placeholder}</color>";
					}
					else
					{
						displayTmp.text = $"<color=#00E5FF>✏️  {val}</color>";
					}
				}
				if (statusTmp != null && getStatusText != null)
				{
					statusTmp.text = getStatusText(val);
				}
			};

			var adapter = new InputAdapter(currentVal, val =>
			{
				updateDisplay(val);
			});

			Action startEditing = () =>
			{
				_activeInputRow = new InputRowState
				{
					Title = title,
					Buffer = adapter != null ? adapter.text : "",
					Placeholder = placeholder,
					OnApply = s =>
					{
						onApply?.Invoke(s);
						updateDisplay(s);
					},
					UpdateVisual = (text) =>
					{
						if (displayTmp != null)
						{
							if (string.IsNullOrEmpty(text))
								displayTmp.text = $"<color=#00E5FF>✏️  Type now... |</color>";
							else
								displayTmp.text = $"<color=#00E5FF>✏️  {text} |</color>";
						}
					},
					Adapter = adapter
				};
				updateDisplay((adapter != null ? adapter.text : "") + " |");
				Toast.Show($"Type with keyboard & press Enter to Apply (Esc to cancel, Ctrl+V to paste)");
				MenuCard.PlayClick();
			};

			UiClick.AddClick(inBarBtn, startEditing);
			UiClick.AddClick(inBarBtn, MenuCard.PlayClick);

			// 3. Action Buttons Line (Row 2)
			var btnsGo = new GameObject("ActionButtonsLine", Il2CppType.Of<RectTransform>());
			var btnsRt = btnsGo.GetComponent<RectTransform>();
btnsRt.SetParent(cardRt, false);

			var btnsHlg = btnsGo.AddComponent<HorizontalLayoutGroup>();
			btnsHlg.spacing = 8f;
			btnsHlg.childControlWidth = false;
			btnsHlg.childControlHeight = true;
			btnsHlg.childForceExpandWidth = false;
			btnsHlg.childForceExpandHeight = false;

			var btnsLe = btnsGo.AddComponent<LayoutElement>();
			btnsLe.preferredHeight = 40f;
			btnsLe.minHeight = 40f;

			Action<string, Action, Color, float> createBtn = (btnLabel, onClickAction, btnColor, minW) =>
			{
				var bGo = new GameObject("Btn_" + btnLabel.Replace(" ", "").Replace("📋", "").Replace("⌨️", "").Replace("✕", ""), Il2CppType.Of<RectTransform>());
				var bRt = bGo.GetComponent<RectTransform>();
				bRt.SetParent(btnsRt, false);

				var bLe = bGo.AddComponent<LayoutElement>();
				bLe.minWidth = minW;
				bLe.preferredWidth = minW;
				bLe.minHeight = 38f;
				bLe.preferredHeight = 38f;

				var bImg = bGo.AddComponent<Image>();
				bImg.color = btnColor;

				var bBtn = bGo.AddComponent<Button>();
				bBtn.targetGraphic = bImg;

				var bTxtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
				bTxtGo.transform.SetParent(bRt, false);
				var bTmp = bTxtGo.AddComponent<TMPro.TextMeshProUGUI>();
				bTmp.text = btnLabel;
				bTmp.fontSize = 16f;
				bTmp.fontStyle = TMPro.FontStyles.Bold;
				bTmp.alignment = TMPro.TextAlignmentOptions.Center;
				bTmp.color = Color.white;
				if (font != null) bTmp.font = font;

				UiClick.AddClick(bBtn, () =>
				{
					onClickAction?.Invoke();
					MenuCard.PlayClick();
				});
			};

			if (extraButtons != null)
			{
				for (int i = 0; i < extraButtons.Length; i++)
				{
					var (label, onClick, btnColor) = extraButtons[i];
					float btnWidth = Mathf.Max(80f, label.Length * 11f + 24f);
					createBtn(label, () =>
					{
						onClick?.Invoke(adapter);
						updateDisplay(adapter != null ? adapter.text : "");
					}, btnColor, btnWidth);
				}
			}

			// Add [ 📋 Paste ] button
			createBtn("📋 Paste", () =>
			{
				string clip = (GUIUtility.systemCopyBuffer ?? "").Trim();
				if (!string.IsNullOrEmpty(clip))
				{
					int maxLen = GetInputMaxLen(title);
					clip = SanitizeInput(clip, maxLen);
					if (adapter != null) adapter.text = clip;
					onApply?.Invoke(clip);
					updateDisplay(clip);
					Toast.Show($"Pasted: {clip}");
				}
				else
				{
					Toast.Show("Clipboard is empty");
				}
			}, new Color(0.12f, 0.48f, 0.45f, 0.95f), 95f);

			// Add [ ⌨️ Type ] button
			createBtn("⌨️ Type", startEditing, new Color(0.15f, 0.20f, 0.32f, 0.95f), 90f);

			// 4. Status Line (Row 3, below buttons)
			if (getStatusText != null)
			{
				var statusGo = new GameObject("StatusLine", Il2CppType.Of<RectTransform>());
				statusGo.transform.SetParent(cardRt, false);
				statusTmp = statusGo.AddComponent<TMPro.TextMeshProUGUI>();
				statusTmp.fontSize = 15f;
				statusTmp.enableWordWrapping = true;
				if (font != null) statusTmp.font = font;
			}

			updateDisplay(currentVal);
		}

		private void AddButtonGroupRow(Transform parent, (string Label, string IconHints, Color BgColor, Action OnClick)[] buttons)
		{
			var rowGo = new GameObject("BtnRow", Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(parent, false);

			var le = rowGo.AddComponent<LayoutElement>();
			le.minHeight = 52f;
			le.preferredHeight = 52f;
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var hlg = rowGo.AddComponent<HorizontalLayoutGroup>();
			hlg.spacing = 10f;
			hlg.childControlWidth = true;
			hlg.childControlHeight = true;
			hlg.childForceExpandWidth = true;
			hlg.childForceExpandHeight = false;

			var font = MenuCard.StealFont();

			foreach (var (label, iconHints, bgColor, onClick) in buttons)
			{
				var btnGo = new GameObject("Btn_" + label.Replace(" ", ""), Il2CppType.Of<RectTransform>());
				var btnRt = btnGo.GetComponent<RectTransform>();
				btnRt.SetParent(rowRt, false);

				var bImg = btnGo.AddComponent<Image>();
				bImg.color = bgColor;
				bImg.raycastTarget = true;

				var btn = btnGo.AddComponent<Button>();
				btn.targetGraphic = bImg;
				var nav = new Navigation();
				nav.mode = Navigation.Mode.None;
				btn.navigation = nav;

				var txtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
				var tRt = txtGo.GetComponent<RectTransform>();
				tRt.SetParent(btnRt, false);
				tRt.anchorMin = Vector2.zero;
				tRt.anchorMax = Vector2.one;
				tRt.offsetMin = Vector2.zero;
				tRt.offsetMax = Vector2.zero;

				var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
				tmp.text = label;
				tmp.fontSize = 17.5f;
				tmp.fontStyle = TMPro.FontStyles.Bold;
				tmp.alignment = TMPro.TextAlignmentOptions.Center;
				tmp.color = Color.white;
				tmp.raycastTarget = false;
				if (font != null) tmp.font = font;

				UiClick.AddClick(btn, onClick);
				UiClick.AddClick(btn, MenuCard.PlayClick);
			}
		}

		private void AddShapeGrid(Transform parent)
		{
			var gridGo = new GameObject("ShapesGrid", Il2CppType.Of<RectTransform>());
			var gridRt = gridGo.GetComponent<RectTransform>();
			gridRt.SetParent(parent, false);

			var le = gridGo.AddComponent<LayoutElement>();
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var glg = gridGo.AddComponent<GridLayoutGroup>();
			glg.cellSize = new Vector2(252f, 48f);
			glg.spacing = new Vector2(12f, 10f);
			glg.startCorner = GridLayoutGroup.Corner.UpperLeft;
			glg.startAxis = GridLayoutGroup.Axis.Horizontal;
			glg.childAlignment = TextAnchor.UpperLeft;

			var csf = gridGo.AddComponent<ContentSizeFitter>();
			csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			var font = MenuCard.StealFont();

			var shapes = new (string Name, string Icon, Color Color, Action Action)[]
			{
				("Ring", "⭕", new Color(0.20f, 0.45f, 0.85f, 0.95f), () => TriggerShape("ring")),
				("Line", "📏", new Color(0.20f, 0.50f, 0.70f, 0.95f), () => TriggerShape("line")),
				("Wall", "🧱", new Color(0.55f, 0.35f, 0.80f, 0.95f), () => TriggerShape("wall")),
				("Heart", "💖", new Color(0.85f, 0.25f, 0.55f, 0.95f), () => TriggerShape("heart")),
				("Star", "⭐", new Color(0.90f, 0.65f, 0.15f, 0.95f), () => TriggerShape("star")),
				("Spiral", "🌀", new Color(0.15f, 0.75f, 0.75f, 0.95f), () => TriggerShape("spiral")),
				("Cube", "🧊", new Color(0.25f, 0.65f, 0.40f, 0.95f), () => TriggerShape("cube")),
				("Restore", "🔄", new Color(0.70f, 0.20f, 0.25f, 0.95f), () =>
				{
					MarkModule.Clear();
					Toast.Show("Restored objects to original positions!");
				})
			};

			foreach (var (name, icon, col, act) in shapes)
			{
				var btnGo = new GameObject("Shape_" + name, Il2CppType.Of<RectTransform>());
				var btnRt = btnGo.GetComponent<RectTransform>();
				btnRt.SetParent(gridRt, false);

				var img = btnGo.AddComponent<Image>();
				img.color = new Color(col.r * 0.35f, col.g * 0.35f, col.b * 0.35f, 0.90f);
				img.raycastTarget = true;

				var btn = btnGo.AddComponent<Button>();
				btn.targetGraphic = img;
				var nav = new Navigation();
				nav.mode = Navigation.Mode.None;
				btn.navigation = nav;

				// Left color accent bar
				var barGo = new GameObject("Bar", Il2CppType.Of<RectTransform>());
				var barRt = barGo.GetComponent<RectTransform>();
				barRt.SetParent(btnRt, false);
				barRt.anchorMin = new Vector2(0f, 0f);
				barRt.anchorMax = new Vector2(0f, 1f);
				barRt.pivot = new Vector2(0f, 0.5f);
				barRt.sizeDelta = new Vector2(6f, 0f);
				barRt.anchoredPosition = Vector2.zero;
				var barImg = barGo.AddComponent<Image>();
				barImg.color = col;
				barImg.raycastTarget = false;

				var txtGo = new GameObject("T", Il2CppType.Of<RectTransform>());
				var tRt = txtGo.GetComponent<RectTransform>();
				tRt.SetParent(btnRt, false);
				tRt.anchorMin = Vector2.zero;
				tRt.anchorMax = Vector2.one;
				tRt.offsetMin = Vector2.zero;
				tRt.offsetMax = Vector2.zero;

				var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
				tmp.text = $"{icon}  {name}";
				tmp.fontSize = 17f;
				tmp.fontStyle = TMPro.FontStyles.Bold;
				tmp.alignment = TMPro.TextAlignmentOptions.Center;
				tmp.color = Color.white;
				tmp.raycastTarget = false;
				if (font != null) tmp.font = font;

				UiClick.AddClick(btn, () =>
				{
					act?.Invoke();
					MenuCard.PlayClick();
				});
			}
		}

		private static void TriggerShape(string shapeName)
		{
			if (!MarkModule.HasMark)
			{
				MarkModule.PutMark();
				Toast.Show("Placed anchor mark 3m ahead!");
			}
			MarkModule.Shape(shapeName);
			Toast.Show($"Arranged objects into {shapeName}!");
		}

		private void AddInfoBox(Transform parent, string info)
		{
			var boxGo = new GameObject("InfoBox", Il2CppType.Of<RectTransform>());
			var boxRt = boxGo.GetComponent<RectTransform>();
			boxRt.SetParent(parent, false);

			var le = boxGo.AddComponent<LayoutElement>();
			le.flexibleWidth = 1f;
			le.minWidth = 600f;
			le.preferredWidth = -1f;

			var hlg = boxGo.AddComponent<HorizontalLayoutGroup>();
			hlg.padding = new RectOffset(18, 18, 12, 12);
			hlg.spacing = 12f;
			hlg.childControlWidth = true;
			hlg.childControlHeight = true;
			hlg.childForceExpandWidth = true;
			hlg.childForceExpandHeight = false;

			var img = boxGo.AddComponent<Image>();
			img.color = new Color(0.09f, 0.11f, 0.16f, 0.90f);

			var csf = boxGo.AddComponent<ContentSizeFitter>();
			csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

			var txtGo = new GameObject("Text", Il2CppType.Of<RectTransform>());
			txtGo.transform.SetParent(boxRt, false);
			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			tmp.text = $"<color=#00E5FF>ℹ️</color>  <color=#C7D2FE>{info}</color>";
			tmp.fontSize = 16.5f;
			tmp.enableWordWrapping = true;
			var font = MenuCard.StealFont();
			if (font != null) tmp.font = font;
		}

		private static Sprite FindSprite(string hints)
		{
			try
			{
				foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
				{
					if (sp == null || string.IsNullOrEmpty(sp.name)) continue;
					foreach (var h in hints.Split('|'))
					{
						if (sp.name.IndexOf(h.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
							return sp;
					}
				}
			}
			catch { }
			return null;
		}

		// =========================================================================
		// NAVIGATION & MUTUAL EXCLUSION
		// =========================================================================

		private void SelectCategory(int index)
		{
			if (index < 0 || index >= _categories.Count) return;
			_activeCategoryIndex = index;

			if (_contentHeaderTitle == null && _ourPage != null)
			{
				var t = _ourPage.transform.Find("Menu_MM_DynamicSidePanel/Panel_SectionList/ScrollRect_Navigation_Container/ScrollRect_Content/Header_MM_H2/LeftItemContainer/Text_Title")
				        ?? FindRecursive(_ourPage.transform, "Text_Title");
				if (t != null)
				{
					_contentHeaderTitle = t.GetComponent<TMPro.TMP_Text>() ?? t.GetComponentInChildren<TMPro.TMP_Text>(true);
				}
			}

			if (_contentHeaderTitle != null)
			{
				_contentHeaderTitle.text = _categories[index].Name;
			}

			for (int i = 0; i < _categories.Count; i++)
			{
				var cat = _categories[i];
				bool active = (i == index);

				if (cat.ContentPageGo != null)
					cat.ContentPageGo.SetActive(active);

				if (cat.Label != null)
					cat.Label.color = active ? new Color(0.00f, 0.85f, 1.00f, 1f) : new Color(0.88f, 0.90f, 0.95f, 1f);

				if (cat.IconImage != null && cat.IconImage.color != Color.clear)
					cat.IconImage.color = active ? new Color(0.00f, 0.85f, 1.00f, 1f) : new Color(0.85f, 0.88f, 0.94f, 1f);

				if (cat.BgImage != null)
					cat.BgImage.color = active ? new Color(0.20f, 0.24f, 0.34f, 0.95f) : new Color(0.12f, 0.13f, 0.16f, 0.85f);
			}

// Reset scroll position to top whenever switching categories
			if (_contentScrollRect != null)
			{
				try
				{
					_contentScrollRect.verticalNormalizedPosition = 1f;
					if (_contentScrollRect.content != null)
					{
						var cpRt = _contentScrollRect.content;
						cpRt.anchoredPosition = new Vector2(cpRt.anchoredPosition.x, 0f);
						UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(cpRt);
					}
				}
				catch { }
			}

						MenuCard.PlayClick();
		}

		private float _tabOpenTime;
		private GameObject _previousActivePage;

		public void CloseOurPage()
		{
			_isOurTabActive = false;
			if (_ourPage != null)
			{
				try { _ourPage.SetActive(false); } catch { }
			}
			SetOurTabActiveVisual(false);

			if (_previousActivePage != null)
			{
				try
				{
					// Only restore if no other page is currently active under HeaderOffset
					bool anyOtherActive = false;
					if (_headerOffset != null)
					{
						for (int i = 0; i < _headerOffset.childCount; i++)
						{
							var child = _headerOffset.GetChild(i);
							if (child != null && child.gameObject != _ourPage && child.gameObject.activeSelf)
							{
								anyOtherActive = true;
								break;
							}
						}
					}
					if (!anyOtherActive)
					{
						_previousActivePage.SetActive(true);
					}
				}
				catch { }
				_previousActivePage = null;
			}
		}

		private void HookSiblingTabs(Transform strip)
		{
			if (strip == null) return;
			try
			{
				for (int i = 0; i < strip.childCount; i++)
				{
					var c = strip.GetChild(i);
					if (c == null || c.gameObject == _ourTabButton) continue;
					var btn = c.GetComponent<Button>() ?? c.GetComponentInChildren<Button>(true);
					if (btn != null)
					{
						UiClick.AddClick(btn, CloseOurPage);
					}
				}
			}
			catch { }
		}

		public static Button FindQuickMenuExpandButton()
		{
			var qm = Core.QuickMenu.Root();
			if (qm == null) return null;

			// 1. Try to find an ACTIVE Button_QM_Expand in the hierarchy first!
			foreach (var b in qm.GetComponentsInChildren<Button>(false))
			{
				if (b != null && b.gameObject.name == "Button_QM_Expand")
				{
					return b;
				}
			}

			string[] candidatePaths = {
				"CanvasGroup/Container/Window/QMParent/Body/Menu_QM_GeneralSettings/QMHeader_H1/RightItemContainer/Button_QM_Expand",
				"CanvasGroup/Container/Window/QMParent/Body/Menu_QM_AudioSettings/QMHeader_H1/RightItemContainer/Button_QM_Expand",
				"CanvasGroup/Container/Window/QMParent/Body/Menu_Here/QMHeader_H1/RightItemContainer/Button_QM_Expand",
				"CanvasGroup/Container/Window/QMParent/Body/Modal_QM_ChangeShieldLevel/MenuPanel/QMHeader_H2/RightItemContainer/Button_QM_Expand"
			};

			foreach (var path in candidatePaths)
			{
				var t = qm.Find(path);
				if (t != null)
				{
					var b = t.GetComponent<Button>();
					if (b != null) return b;
				}
			}

			// 2. Fall back to any Button_QM_Expand even if inactive
			foreach (var b in qm.GetComponentsInChildren<Button>(true))
			{
				if (b != null && b.gameObject.name == "Button_QM_Expand" && b.transform.parent != null && b.transform.parent.name == "RightItemContainer")
				{
					if (b.transform.parent.parent != null && b.transform.parent.parent.name.Contains("DevTools")) continue;
					return b;
				}
			}

			return null;
		}

		public static void OpenArchiveMainMenu()
		{
			if (_instance == null) return;
			_instance.TriggerOpenArchive();
		}

		public void TriggerOpenArchive()
		{
			try
			{
				if (_ourPage == null || _ourTabButton == null)
				{
					TryBuild();
				}

				// If Main Menu is currently open, switch directly to Archive tab
				var mainRoot = Core.QuickMenu.Main();
				if (mainRoot != null && mainRoot.gameObject.activeInHierarchy)
				{
					ActivateArchivePage(playClick: true);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[MainMenuTab] TriggerOpenArchive error: {e.Message}");
			}
		}

		private void OnTabClicked() => ActivateArchivePage(playClick: true);

		public void ActivateArchivePage(bool playClick = false)
		{
			if (_headerOffset == null)
			{
				var mainRoot = Core.QuickMenu.Main();
				if (mainRoot != null) _headerOffset = mainRoot.Find(HeaderOffsetPath);
			}

			// If page not cloned yet, build it now
			if (_ourPage == null && _headerOffset != null)
			{
				Transform settingsDonor = FindSettingsDonor(_headerOffset);
				if (settingsDonor != null)
				{
					BuildMainPage(settingsDonor, _headerOffset);
				}
			}

			if (_ourPage == null || _headerOffset == null) return;

			// 1. Find the currently active stock page under HeaderOffset and hide it cleanly, saving it to restore later
			_previousActivePage = null;
			for (int i = 0; i < _headerOffset.childCount; i++)
			{
				var p = _headerOffset.GetChild(i);
				if (p == null || p.gameObject == _ourPage) continue;
				if (p.gameObject.activeSelf)
				{
					string pName = p.name;
					if (pName.StartsWith("Menu_", StringComparison.OrdinalIgnoreCase)
						&& !pName.Contains("ToolTip")
						&& !pName.Contains("Modal")
						&& !pName.Contains("Keyboard")
						&& !pName.Contains("Empty"))
					{
						_previousActivePage = p.gameObject;
						try { p.gameObject.SetActive(false); } catch { }
						break;
					}
				}
			}

			// 2. Unmask and activate our page completely
			UnmaskAndActivateHierarchy(_ourPage);
			_ourPage.SetActive(true);
			_ourPage.transform.SetAsLastSibling();

			_isOurTabActive = true;
			_tabOpenTime = VaClock.Now;

			// 3. Highlight our tab button and deselect sibling buttons
			SetOurTabActiveVisual(true);
			DeselectSiblingTabs();
			HookSiblingTabs(_pageButtonsStrip);

			if (playClick) MenuCard.PlayClick();
		}

		private void Pump()
		{
			if (_ourPage == null)
			{
				TryBuild();
				if (_ourPage == null) return;
			}
			if (_headerOffset == null) return;

			var mainRoot = Core.QuickMenu.Main();

			// 1. If the Main Menu is closed or minimized, ensure our page is closed
			if (mainRoot == null || !mainRoot.gameObject.activeInHierarchy)
			{
				if (_isOurTabActive)
				{
					CloseOurPage();
				}
				return;
			}

			// 2. Sibling Tab or Page Check:
			// If our page is active, check if the user clicked ANY other bottom tab or top header button:
			if (_isOurTabActive && VaClock.Now - _tabOpenTime > 0.15f)
			{
				bool tabSwitched = false;

				// A. Check bottom tabs (PageButtons): If any sibling tab has height > 125 (active size 144)
				if (_pageButtonsStrip != null)
				{
					for (int i = 0; i < _pageButtonsStrip.childCount; i++)
					{
						var tabChild = _pageButtonsStrip.GetChild(i);
						if (tabChild == null || tabChild.gameObject == _ourTabButton) continue;
						var rt = tabChild.GetComponent<RectTransform>();
						if (rt != null && rt.sizeDelta.y > 125f)
						{
							tabSwitched = true;
							break;
						}
					}
				}

				// B. Check HeaderOffset: If ANY stock Menu_ page has become active
				if (!tabSwitched && _headerOffset != null)
				{
					for (int i = 0; i < _headerOffset.childCount; i++)
					{
						var child = _headerOffset.GetChild(i);
						if (child == null || child.gameObject == _ourPage) continue;
						if (child.gameObject.activeSelf)
						{
							string n = child.name;
							if (n.StartsWith("Menu_", StringComparison.OrdinalIgnoreCase)
								&& !n.Contains("ToolTip")
								&& !n.Contains("Modal")
								&& !n.Contains("Keyboard")
								&& !n.Contains("Empty"))
							{
								tabSwitched = true;
								break;
							}
						}
					}
				}

				if (tabSwitched)
				{
					CloseOurPage();
					return;
				}
			}

			// 3. Main Menu is active and our tab is selected:
			if (_isOurTabActive)
			{
				RefreshLiveControls();
			}
		}

		private void RefreshLiveControls()
		{
			for (int i = 0; i < _toggles.Count; i++)
			{
				var t = _toggles[i];
				if (t == null || t.Card == null) continue;
				bool on;
				try { on = t.State(); } catch { continue; }
				if (on == t.Last) continue;
				t.Last = on;
				try
				{
					var img = t.Card.GetComponent<Image>() ?? t.Card.Find("Background")?.GetComponent<Image>();
					if (img != null) img.color = on ? MenuCard.On : MenuCard.Bg;
					if (t.PillImage != null)
						t.PillImage.color = on ? new Color(0.00f, 0.82f, 0.95f, 1f) : new Color(0.24f, 0.26f, 0.31f, 1f);
					if (t.KnobTransform != null)
						t.KnobTransform.anchoredPosition = new Vector2(on ? 12f : -12f, 0f);
				}
				catch { }
			}

			for (int i = 0; i < _steppers.Count; i++)
			{
				var s = _steppers[i];
				if (s == null || s.Card == null || s.Cfg == null) continue;
				try
				{
					float cur = s.Cfg.Value;
					if (Mathf.Abs(cur - s.LastVal) > 0.001f)
					{
						s.LastVal = cur;
						MenuCard.UpdateStepperText(s.Card, s.Title, cur, s.Format);
					}
				}
				catch { }
			}
		}

		private void SetOurTabActiveVisual(bool active)
		{
			if (_tabBgImage != null)
				_tabBgImage.color = active ? ColorActiveTabBg : ColorInactiveTabBg;

			if (_tabIconImage != null)
				_tabIconImage.color = active ? ColorActiveText : ColorInactiveText;

			if (_tabLabel != null)
				_tabLabel.color = active ? ColorActiveText : ColorInactiveText;
		}

		private void DeselectSiblingTabs()
		{
			if (_pageButtonsStrip == null) return;
			try
			{
				for (int i = 0; i < _pageButtonsStrip.childCount; i++)
				{
					var c = _pageButtonsStrip.GetChild(i);
					if (c == null || c.gameObject == _ourTabButton) continue;

					// Find Background Image
					var bg = c.Find("Background")?.GetComponent<Image>() ?? c.GetComponent<Image>();
					if (bg != null) bg.color = ColorInactiveTabBg;

					// Find Icon
					var icon = c.Find("Icon")?.GetComponent<Image>() ?? c.Find("Icons/Icon")?.GetComponent<Image>();
					if (icon != null) icon.color = ColorInactiveText;

					// Find Text
					var txt = c.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (txt != null) txt.color = ColorInactiveText;
				}
			}
			catch { }
		}

		private static void UnmaskAndActivateHierarchy(GameObject page)
		{
			if (page == null) return;
			try
			{
				page.SetActive(true);

				// Reset local scales
				foreach (var t in page.GetComponentsInChildren<Transform>(true))
				{
					if (t != null && t.localScale.sqrMagnitude < 0.001f)
						t.localScale = Vector3.one;
				}

				// Enable all Canvases
				foreach (var cv in page.GetComponentsInChildren<Canvas>(true))
				{
					if (cv == null) continue;
					cv.enabled = true;
				}

				// Unmask all CanvasGroups recursively
				foreach (var cg in page.GetComponentsInChildren<CanvasGroup>(true))
				{
					if (cg == null) continue;
					cg.alpha = 1f;
					cg.interactable = true;
					cg.blocksRaycasts = true;
					cg.ignoreParentGroups = true;
				}

				// Enable GraphicRaycasters
				foreach (var gr in page.GetComponentsInChildren<GraphicRaycaster>(true))
				{
					if (gr == null) continue;
					gr.enabled = true;
				}

				// Enable RectMask2D
				foreach (var mask in page.GetComponentsInChildren<RectMask2D>(true))
				{
					if (mask != null) mask.enabled = true;
				}

				// Ensure key container GameObjects are active
				Transform dyn = FindRecursive(page.transform, "Menu_MM_DynamicSidePanel");
				if (dyn != null) dyn.gameObject.SetActive(true);
				Transform panelSec = FindRecursive(page.transform, "Panel_SectionList");
				if (panelSec != null) panelSec.gameObject.SetActive(true);
				Transform navC = FindRecursive(page.transform, "ScrollRect_Navigation_Container");
				if (navC != null) navC.gameObject.SetActive(true);
				Transform nav = FindRecursive(page.transform, "ScrollRect_Navigation");
				if (nav != null)
				{
					nav.gameObject.SetActive(true);
					Transform vp = nav.Find("Viewport");
					if (vp != null) vp.gameObject.SetActive(true);
				}
				Transform cont = FindRecursive(page.transform, "ScrollRect_Content");
				if (cont != null)
				{
					cont.gameObject.SetActive(true);
					Transform vp = cont.Find("Viewport");
					if (vp != null) vp.gameObject.SetActive(true);
				}
			}
			catch { }
		}

		// =========================================================================
		// HELPER UTILITIES
		// =========================================================================

		private static void SetLabelRecursive(Transform root, string text, out TMPro.TMP_Text outTmp)
		{
			outTmp = null;
			if (root == null) return;
			try
			{
				var tmps = root.GetComponentsInChildren<TMPro.TMP_Text>(true);
				if (tmps != null)
				{
					foreach (var t in tmps)
					{
						if (t != null && !string.IsNullOrEmpty(t.text))
						{
							t.text = text;
							if (outTmp == null) outTmp = t;
						}
					}
				}

				var texts = root.GetComponentsInChildren<Text>(true);
				if (texts != null)
				{
					foreach (var t in texts)
					{
						if (t != null && !string.IsNullOrEmpty(t.text))
						{
							t.text = text;
						}
					}
				}
			}
			catch { }
		}

		private static Transform FindRecursive(Transform root, string name)
		{
			if (root == null) return null;
			if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return root;

			for (int i = 0; i < root.childCount; i++)
			{
				var found = FindRecursive(root.GetChild(i), name);
				if (found != null) return found;
			}
			return null;
		}

		private static void StripRoot(Transform t)
		{
			if (t == null) return;
			try
			{
				var comps = t.GetComponents<Component>();
				if (comps == null) return;
				foreach (var c in comps)
				{
					if (c == null) continue;
					string n = Il2CppName(c);
					if (Keep.Contains(n)) continue;
					try { UnityEngine.Object.DestroyImmediate(c); } catch { }
				}
			}
			catch { }
		}

		private static string Il2CppName(Component c)
		{
			try { return c.GetIl2CppType().Name; }
			catch { return c.GetType().Name; }
		}
	}
}
