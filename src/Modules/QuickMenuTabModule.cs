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
	// NATIVE QUICKMENU TAB — adds a "VRChat Archive" tab to VRChat's own QuickMenu tab strip,
	// with its own page of buttons, so the mod lives inside the game's menu.
	//
	// Every path here comes from a real capture of the live menu (captures/ui_*.txt):
	//   tab strip : Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/Page_Buttons_QM/HorizontalLayoutGroup
	//               (HorizontalLayoutGroup + ContentSizeFitter -> a new child lays itself out)
	//   page host : Canvas_QuickMenu(Clone)/CanvasGroup/Container/Window/QMParent/Body
	//   donors    : Page_DevTools (tab) + Menu_DevTools (page) — both ship DISABLED, so cloning
	//               them disturbs nothing a normal user sees.
	//   buttons   : the cloned Menu_DevTools page brings its OWN button grid with it
//               (Scrollrect/Viewport/VerticalLayoutGroup/Buttons) — we re-purpose those cards
	//
	// Two rules keep this safe:
	//   1. We never modify VRChat's own objects — only our CLONES.
	//   2. VRChat's obfuscated controller components are stripped off the clone ROOTS (page root,
	//      tab root, card roots) so the game's page state-machine cannot fight us. Children keep
	//      their components, which is what gives the cloned buttons a real font and real sprites —
	//      building UI from scratch produced invisible text, because a fresh TextMeshProUGUI has
	//      no font asset assigned.
	public class QuickMenuTabModule : IModule
	{
		public override string Name => "QuickMenuTab";

		private const string TabName  = "Page_VRChatArchive";
		private const string PageName = "Menu_VRChatArchive";

		private const string QmRoot = "Canvas_QuickMenu(Clone)";
		private const string StripPath = "CanvasGroup/Container/Window/Page_Buttons_QM/HorizontalLayoutGroup";
		private const string BodyPath  = "CanvasGroup/Container/Window/QMParent/Body";
		// The Launchpad's quick-link cards are the ones the player actually sees, so VRChat has
		// already applied its style to them. Cloning one copies those resolved colours. The
		// DevTools page's own tiles are never opened by the game, so they were never styled —
		// cloning THOSE is what produced plain white cards.
		private const string QuickLinksPath = "Menu_QM_Launchpad/ScrollRect/Viewport/VerticalLayoutGroup/Buttons_QuickLinks";

		private GameObject _tab, _page;
		private Transform _body;
		private float _nextTry;
		private int _fails;
		private bool _loggedOnce;
		private bool _tabWasActive, _pageWasActive;
		private static Sprite _logoSprite;
		private static float _lastTabClickTime;
		private static bool _tabClickHooked;
		private static GameObject _pageRef;

		// Unity components worth keeping on a clone ROOT. Everything else (VRChat's obfuscated
		// tab / page / style / tooltip controllers) is destroyed so the game cannot drive it.
		private static readonly HashSet<string> Keep = new HashSet<string>(StringComparer.Ordinal)
		{
			"RectTransform", "Transform", "CanvasRenderer", "Canvas", "CanvasGroup", "GraphicRaycaster",
			"Image", "ImageEx", "RawImage", "RawImageEx",
			"LayoutElement", "HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup",
			"ContentSizeFitter", "AspectRatioFitter", "RectMask2D", "Mask", "ScrollRect", "Scrollbar",
			"UIInvisibleGraphic", "Button",
		};

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.QMTabEnabled.Value)
				{
					if (_tab != null || _page != null) Teardown();
					return;
				}

				if (!Core.QuickMenu.Visible)
				{
					_lastTabClickTime = 0f;
				}

				if (_tab != null && _page != null) { Pump(); return; }

				float now = VaClock.Now;
				if (now < _nextTry) return;
				_nextTry = now + 3f;              // the QuickMenu only exists once the UI is built
				if (_fails > 40) return;          // give up quietly rather than scan forever
				if (!TryBuild()) _fails++;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_tabClickHooked = false;
			_lastTabClickTime = 0f;
			if (_tab == null || _page == null) { _tab = null; _page = null; _pageRef = null; _fails = 0; _nextTry = 0f; _wasActive = false; }
		}

		public override void OnShutdown() => Teardown();

		// ---------------------------------------------------------------- build

		private bool TryBuild()
		{
			Transform qm = Core.QuickMenu.Root();
			if (qm == null) return false;

			Transform strip = qm.Find(StripPath);
			Transform body = qm.Find(BodyPath);
			if (strip == null || body == null)
			{
				if (!_loggedOnce)
				{
					_loggedOnce = true;
					VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] QuickMenu found but strip={(strip != null)} body={(body != null)} — layout changed?");
				}
				return false;
			}
			_body = body;

			// THE DEVTOOLS METHOD. VRChat already ships a complete, fully-styled tab + page that
			// nobody uses: Page_DevTools and Menu_DevTools, both disabled. Rather than cloning a
			// tab and stripping it — which threw away the StyleElement that themes it, so ours
			// rendered as a flat coloured square that matched nothing — we simply switch the real
			// ones ON and fill the page. The tab is then genuinely VRChat's: its hover, selected
			// state and page-opening are driven by the game's own controller, for free.
			Transform tab = strip.Find("Page_DevTools") ?? strip.Find("VRCHAT ARCHIVE");
			Transform page = body.Find("Menu_DevTools");
			if (tab == null || page == null)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] DevTools tab/page not present (tab={(tab != null)} page={(page != null)}).");
				return false;
			}

			_tabWasActive = tab.gameObject.activeSelf;
			_pageWasActive = page.gameObject.activeSelf;

			tab.gameObject.SetActive(true);
			BrandTab(tab);

			// Build the page while it is ACTIVE. Cloning tiles into a disabled page means Unity
			// never runs Awake on them, so VRChat's own text component is still uninitialised and
			// setting its text throws — which is exactly why the four toggle tiles failed. The
			// QuickMenu itself is closed at this point, so nothing flashes on screen; the previous
			// state is restored immediately afterwards.
			bool pageWas = page.gameObject.activeSelf;
			try { page.gameObject.SetActive(true); } catch { }
			FillPage(page, body);
			try { page.gameObject.SetActive(pageWas); } catch { }

			_tab = tab.gameObject;
			_page = page.gameObject;
			_pageRef = _page;
			VRChatArchiveModPlugin.Logger.LogInfo("[QMTab] VRChat's own DevTools tab enabled and re-purposed as the VRChat Archive tab.");
			return true;
		}


		private static Transform FindDonorTab(Transform strip)
		{
			string[] preferred = { "Page_DevTools", "Page_Store", "Page_VRCPlus_Subscribed" };
			foreach (string n in preferred) { var t = strip.Find(n); if (t != null) return t; }
			for (int i = 0; i < strip.childCount; i++)
			{
				var c = strip.GetChild(i);
				if (c != null && c.name.StartsWith("Page_", StringComparison.Ordinal)) return c;
			}
			return null;
		}

		private static Transform FindDonorPage(Transform body)
		{
			string[] preferred = { "Menu_DevTools", "Menu_QM_ClockMode" };
			foreach (string n in preferred) { var t = body.Find(n); if (t != null) return t; }
			return null;
		}

		// Strip only THIS object's foreign components (children keep theirs).
		private static void StripRoot(Transform t)
		{
			try
			{
				var comps = t.GetComponents<Component>();
				if (comps == null) return;
				foreach (var c in comps)
				{
					if (c == null) continue;
					if (Keep.Contains(Il2CppName(c))) continue;
					try { UnityEngine.Object.DestroyImmediate(c); } catch { }
				}
			}
			catch { }
		}

		private static void StripTree(Transform t, int depth)
		{
			if (t == null || depth < 0) return;
			StripRoot(t);
			for (int i = 0; i < t.childCount; i++) StripTree(t.GetChild(i), depth - 1);
		}

		// Our identity on the cloned tab: the real VRChat Archive logo as its icon.
		// Only the icon is ours. Colours, hover and the selected highlight stay with VRChat's
		// StyleElement — overriding them is what made the cloned tab look like a foreign square.
		private static void BrandTab(Transform tab)
		{
			try
			{
				var icon = tab.Find("Icon");
				if (icon == null) return;

				// The Icon carries a StyleElement, and that is what kept putting VRChat's DevTools
				// wrench back over our logo. Drop it on this one object (the tab's own Background
				// keeps its StyleElement, so the tab still themes with the rest of the strip).
				foreach (var c in icon.GetComponents<Component>())
				{
					if (c == null) continue;
					if (Il2CppName(c) != "StyleElement") continue;
					try { UnityEngine.Object.DestroyImmediate(c); } catch { }
				}

				var img = icon.GetComponent<Image>();
				if (img == null) return;
				var sp = LogoSprite();
				if (sp != null) { img.sprite = sp; img.color = Color.white; }
			}
			catch { }

			RenameTooltip(tab);
		}

		// The hover bubble still read "Developer Tools": it comes from one of VRChat's own
		// components on the tab, which we deliberately keep (that is what makes the tab behave
		// natively). The component type name is obfuscated and changes every build, so instead of
		// guessing it we walk the tab's components and rewrite any string member that currently
		// holds the stock caption. Also covers the tab's Badge label if it carries one.
		private static void RenameTooltip(Transform tab)
		{
			const string NewName = "VRCHAT ARCHIVE";
			try
			{
				foreach (var comp in tab.GetComponents<Component>())
				{
					if (comp == null) continue;
					Type t = comp.GetType();

					foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
					{
						if (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite) continue;
						if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase)) continue; // Never overwrite GameObject / Object name
						try
						{
							if (prop.GetValue(comp) is string cur && LooksLikeStockCaption(cur))
								prop.SetValue(comp, NewName);
						}
						catch { }
					}

					foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
					{
						if (fld.FieldType != typeof(string)) continue;
						if (string.Equals(fld.Name, "name", StringComparison.OrdinalIgnoreCase)) continue;
						try
						{
							if (fld.GetValue(comp) is string cur && LooksLikeStockCaption(cur))
								fld.SetValue(comp, NewName);
						}
						catch { }
					}
				}

				// The badge label, if this tab shows one.
				var badge = tab.Find("Badge");
				if (badge != null)
				{
					var tmp = badge.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null && LooksLikeStockCaption(tmp.text)) tmp.text = NewName;
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] tooltip rename failed: {e.Message}"); }
		}

		private static bool LooksLikeStockCaption(string s)
		{
			if (string.IsNullOrEmpty(s)) return false;
			return s.IndexOf("Developer", StringComparison.OrdinalIgnoreCase) >= 0
			    || s.IndexOf("DevTools", StringComparison.OrdinalIgnoreCase) >= 0;
		}

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

		// The page's rows: real VRChat button cards, cloned from the Launchpad's quick-links grid
		// so they carry the game's own font, sprites and layout, then relabelled and re-wired.
		// A tile is either a one-shot ACTION (VRChat's Warp tile shape) or a TOGGLE (VRChat's
		// Invisible/Tag tile shape, which carries an Icon_Off / Icon_On pair). Reusing the game's
		// own two shapes is what makes the page look native instead of home-made.
		// Icon: an optional texture of OURS to put on the tile instead of whatever the donor card
		// happened to carry. Without it a soundboard tile inherits some unrelated VRChat glyph.
		// IconName: hints, '|'-separated, matched case-insensitively against the names of the sprites
		// VRChat itself has loaded (SpriteIndex). The first hint that names a real sprite wins, so a
		// tile wears the game's own glyph for what it does instead of whatever the donor carried.
		private struct Act { public string Label; public Action Do; public Func<bool> State; public Func<Texture2D> Icon; public string IconName; public string SecondaryIconName; }

		// SPRITE INDEX. Every Sprite the game has loaded, by name, built once per page fill and
		// thrown away with the page. Lets a tile wear one of VRChat's own icons by asking for it
		// by (part of) its name. The first fill also writes every candidate name to
		// BepInEx/qm_sprites.txt, once, so the hints in ActsFor can be tuned against the real
		// list instead of guessed.
		internal static class SpriteIndex
		{
			private static readonly Dictionary<string, Sprite> _byName = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
			private static readonly List<string> _names = new List<string>();
			private static bool _built, _dumped;

			public static void Invalidate() { _built = false; _byName.Clear(); _names.Clear(); }

			private static void Build()
			{
				_built = true;
				try
				{
					var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(typeof(Sprite)));
					if (all == null) return;
					for (int i = 0; i < all.Length; i++)
					{
						var sp = all[i]?.TryCast<Sprite>();
						if (sp == null) continue;
						string n = sp.name ?? "";
						if (n.Length == 0 || _byName.ContainsKey(n)) continue;
						_byName[n] = sp; _names.Add(n);
					}
					VRChatArchiveModPlugin.Logger.LogInfo("[QMTab] sprite index: " + _names.Count + " name(s).");
					if (!_dumped)
					{
						_dumped = true;
						try
						{
							string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "qm_sprites.txt");
							var sorted = new List<string>(_names); sorted.Sort(StringComparer.OrdinalIgnoreCase);
							System.IO.File.WriteAllLines(path, sorted);
							VRChatArchiveModPlugin.Logger.LogInfo("[QMTab] sprite names written to " + path);
						}
						catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[QMTab] sprite dump failed: " + e.Message); }
					}
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[QMTab] sprite index failed: " + e.Message); }
			}

			// First hint that is contained in a loaded sprite's name wins; an exact name beats a
			// substring so a hint like "eye" cannot land on "keyeye_bg" when "Icon_Eye" exists.
			public static Sprite Find(string hints)
			{
				if (string.IsNullOrEmpty(hints)) return null;
				if (!_built) Build();
				foreach (string raw in hints.Split('|'))
				{
					string h = raw.Trim();
					if (h.Length == 0) continue;
					if (_byName.TryGetValue(h, out var exact) && exact != null) return exact;
					for (int i = 0; i < _names.Count; i++)
					{
						string n = _names[i];
						if (n.IndexOf(h, StringComparison.OrdinalIgnoreCase) < 0) continue;
						// icons only: skip obvious non-glyph art (backgrounds, gradients, wallpapers)
						if (n.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("wallpaper", StringComparison.OrdinalIgnoreCase) >= 0) continue;
						if (_byName.TryGetValue(n, out var sp) && sp != null) return sp;
					}
				}
				return null;
			}
		}

		private sealed class ToggleTile { public Transform Card; public Func<bool> State; public bool Last; }
		private readonly List<ToggleTile> _toggles = new List<ToggleTile>();

		private sealed class Pair { public Transform Clone; public Transform Donor; }
		private readonly List<Pair> _pairs = new List<Pair>();
		private bool _spritesResolved;

		private sealed class StepperRef
		{
			public Transform Card;
			public string Title;
			public BepInEx.Configuration.ConfigEntry<float> Cfg;
			public float LastVal;
			public string Format;
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

		// ------------------------------------------------------------------ interaction helpers
		private static GameObject _fullbrightGo;
		private static bool _fullbright;
		public static bool FullbrightActive => _fullbright;
		public static void ToggleFullbright()
		{
			_fullbright = !_fullbright;
			try
			{
				if (_fullbright)
				{
					if (_fullbrightGo == null)
					{
						_fullbrightGo = new GameObject("VA_FullbrightLight");
						var l = _fullbrightGo.AddComponent<Light>();
						l.type = LightType.Directional;
						l.color = Color.white;
						l.intensity = 1.2f;
						l.shadows = LightShadows.None;
						UnityEngine.Object.DontDestroyOnLoad(_fullbrightGo);
					}
					var cam = Camera.main;
					if (cam != null)
					{
						_fullbrightGo.transform.SetParent(cam.transform, false);
						_fullbrightGo.transform.localPosition = Vector3.zero;
						_fullbrightGo.transform.localRotation = new Quaternion(0f, 0f, 0f, 1f);
					}
					_fullbrightGo.SetActive(true);
					Toast.Show("Fullbright ON");
				}
				else
				{
					if (_fullbrightGo != null) _fullbrightGo.SetActive(false);
					Toast.Show("Fullbright OFF");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[QMTab] Fullbright failed: " + e.Message); }
		}

		private static bool _deafened;
		public static bool DeafenActive => _deafened;
		public static void ToggleDeafen()
		{
			_deafened = !_deafened;
			try
			{
				AudioListener.pause = _deafened;
				Toast.Show(_deafened ? "Mute / Deafen ON" : "Mute / Deafen OFF");
			}
			catch { }
		}

		public static void RespawnAllPickups()
		{
			try
			{
				var il2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.SDKBase.VRC_Pickup>();
				var found = Resources.FindObjectsOfTypeAll(il2);
				int respawned = 0;
				if (found != null)
				{
					var me = VRC.SDKBase.Networking.LocalPlayer;
					for (int i = 0; i < found.Length; i++)
					{
						var pk = found[i]?.TryCast<VRC.SDKBase.VRC_Pickup>();
						if (pk == null || pk.gameObject == null) continue;
						try
						{
							if (me != null && VRC.SDKBase.Networking.GetOwner(pk.gameObject) != me)
								VRC.SDKBase.Networking.SetOwner(me, pk.gameObject);
							pk.transform.position = new Vector3(0, -1000f, 0);
							respawned++;
						}
						catch { }
					}
				}
				Toast.Show($"Respawned {respawned} pickup(s)");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[QMTab] Respawn pickups: " + e.Message);
			}
		}

		// ------------------------------------------------------------------ VRTool layout builders
		private void CreateSection(Transform content, string title, out Transform rowHost, bool startExpanded = true)
		{
			var secGo = new GameObject("Section_" + title.Replace(" ", ""), Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var secRt = secGo.GetComponent<RectTransform>();
			secRt.SetParent(content, false);
			secRt.sizeDelta = new Vector2(980f, 0f);

			var sLe = secGo.AddComponent<UnityEngine.UI.LayoutElement>();
			sLe.flexibleWidth = 1f;
			sLe.minHeight = -1f;
			sLe.preferredHeight = -1f;

			var sVlg = secGo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
			sVlg.childForceExpandHeight = false;
			sVlg.childControlHeight = true;
			sVlg.childForceExpandWidth = true;
			sVlg.childControlWidth = true;
			sVlg.spacing = 10f;
			sVlg.padding = new RectOffset(0, 0, 4, 12);

			var csf = secGo.AddComponent<UnityEngine.UI.ContentSizeFitter>();
			csf.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
			csf.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;

			// Header Banner
			var hdrGo = new GameObject("Header", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var hdrRt = hdrGo.GetComponent<RectTransform>();
			hdrRt.SetParent(secRt, false);

			var hLe = hdrGo.AddComponent<UnityEngine.UI.LayoutElement>();
			hLe.minHeight = 44f;
			hLe.preferredHeight = 44f;
			hLe.flexibleWidth = 1f;

			var hImg = hdrGo.AddComponent<UnityEngine.UI.Image>();
			hImg.color = new Color(0.10f, 0.11f, 0.14f, 0.0f);

			var hBtn = hdrGo.AddComponent<UnityEngine.UI.Button>();
			hBtn.targetGraphic = hImg;
			hBtn.transition = UnityEngine.UI.Selectable.Transition.ColorTint;
			hBtn.colors = Core.MenuCard.Tint(new Color(0.14f, 0.15f, 0.19f, 0.3f));

			// Header Text
			var txtGo = new GameObject("Text", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var txtRt = txtGo.GetComponent<RectTransform>();
			txtRt.SetParent(hdrRt, false);
			txtRt.anchorMin = Vector2.zero;
			txtRt.anchorMax = Vector2.one;
			txtRt.offsetMin = new Vector2(16f, 0f);
			txtRt.offsetMax = new Vector2(-16f, 0f);

			var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
			var font = Core.MenuCard.StealFont();
			if (font != null) tmp.font = font;
			tmp.fontSize = 24f;
			tmp.fontStyle = TMPro.FontStyles.Bold;
			tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
			tmp.color = new Color(0.85f, 0.88f, 0.94f, 1f);
			tmp.text = (startExpanded ? "▼ " : "▶ ") + title;

			// Row Host (Container for rows)
			var bodyGo = new GameObject("RowsHost", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var bodyRt = bodyGo.GetComponent<RectTransform>();
			bodyRt.SetParent(secRt, false);
			bodyRt.sizeDelta = new Vector2(980f, 0f);

			var bLe = bodyGo.AddComponent<UnityEngine.UI.LayoutElement>();
			bLe.flexibleWidth = 1f;
			bLe.minHeight = -1f;
			bLe.preferredHeight = -1f;

			var bVlg = bodyGo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
			bVlg.childForceExpandHeight = false;
			bVlg.childControlHeight = true;
			bVlg.childForceExpandWidth = true;
			bVlg.childControlWidth = true;
			bVlg.spacing = 10f;

			var bCsf = bodyGo.AddComponent<UnityEngine.UI.ContentSizeFitter>();
			bCsf.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
			bCsf.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;

			bodyGo.SetActive(startExpanded);
			rowHost = bodyRt;

			var secItem = new SectionUI
			{
				Title = title,
				Header = hdrRt,
				Body = bodyRt,
				Expanded = startExpanded,
				HeaderText = tmp
			};
			_sections.Add(secItem);

			Core.UiClick.AddClick(hBtn, () =>
			{
				secItem.Expanded = !secItem.Expanded;
				secItem.Body.gameObject.SetActive(secItem.Expanded);
				secItem.HeaderText.text = (secItem.Expanded ? "▼ " : "▶ ") + secItem.Title;
				Core.MenuCard.PlayClick();
			});
		}

		private static Transform AddRow(Transform rowsHost)
		{
			var rowGo = new GameObject("Row", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(rowsHost, false);
			rowRt.sizeDelta = new Vector2(980f, 170f);

			var rLe = rowGo.AddComponent<UnityEngine.UI.LayoutElement>();
			rLe.minHeight = 170f;
			rLe.preferredHeight = 170f;
			rLe.flexibleWidth = 1f;
			rLe.flexibleHeight = 0f;

			var hlg = rowGo.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
			hlg.childForceExpandHeight = true;
			hlg.childControlHeight = true;
			hlg.childForceExpandWidth = true;
			hlg.childControlWidth = true;
			hlg.childAlignment = TextAnchor.MiddleLeft;
			hlg.spacing = 10f;

			return rowRt;
		}

		private void AddCard(Transform row, Act act, Transform donor, int colSpan = 1)
		{
			try
			{
				var cardGo = UnityEngine.Object.Instantiate(donor.gameObject, row);
				cardGo.name = "Btn_" + act.Label.Replace(" ", "");
				cardGo.SetActive(true);
				var card = cardGo.transform;

				float width = (colSpan == 2) ? 468f : 228f;
				var le = card.GetComponent<UnityEngine.UI.LayoutElement>() ?? cardGo.AddComponent<UnityEngine.UI.LayoutElement>();
				le.ignoreLayout = false;
				le.minWidth = width;
				le.preferredWidth = width;
				le.minHeight = 170f;
				le.preferredHeight = 170f;
				le.flexibleWidth = colSpan;
				le.flexibleHeight = 0f;

				bool lit = false;
				try { lit = act.State != null && act.State(); } catch { }

				Action wrappedDo = null;
				if (act.Do != null)
				{
					if (act.State != null)
					{
						float lastToggle = 0f;
						wrappedDo = () =>
						{
							float now = VaClock.Now;
							if (now - lastToggle < 0.08f) return;
							lastToggle = now;
							act.Do();
							try
							{
								bool newState = act.State();
								Core.MenuCard.SetLit(card, newState, false);
							}
							catch { }
						};
					}
					else
					{
						wrappedDo = act.Do;
					}
				}

				Core.MenuCard.Setup(card, donor, act.Label, wrappedDo, lit, keepStyle: false, hasState: act.State != null);
				Core.MenuCard.StripBadges(card);

				var bComp = card.GetComponent<Button>();
				if (bComp != null) UiClick.SetDebounce(bComp, 0.05f);

				if (!string.IsNullOrEmpty(act.IconName))
				{
					var sp = SpriteIndex.Find(act.IconName);
					if (sp != null) Core.MenuCard.SetIcon(card, sp);
				}

				if (!string.IsNullOrEmpty(act.SecondaryIconName))
				{
					var sp2 = SpriteIndex.Find(act.SecondaryIconName);
					if (sp2 != null) Core.MenuCard.SetSecondaryIcon(card, sp2);
				}

				if (act.Icon != null)
				{
					try
					{
						var tex = act.Icon();
						if (tex != null) Core.MenuCard.SetIcon(card, tex);
					}
					catch { }
				}

				_pairs.Add(new Pair { Clone = card, Donor = donor });
				if (act.State != null)
				{
					_toggles.Add(new ToggleTile { Card = card, State = act.State, Last = !lit });
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] AddCard '{act.Label}' failed: {e.Message}");
			}
		}

		private void AddStepper(Transform row, string title, BepInEx.Configuration.ConfigEntry<float> cfg,
			float step, float defVal, float min, float max, Transform donor, string format = "0.#")
		{
			try
			{
				var cardGo = UnityEngine.Object.Instantiate(donor.gameObject, row);
				cardGo.name = "Stepper_" + title.Replace(" ", "");
				cardGo.SetActive(true);
				var card = cardGo.transform;

				var le = card.GetComponent<UnityEngine.UI.LayoutElement>() ?? cardGo.AddComponent<UnityEngine.UI.LayoutElement>();
				le.ignoreLayout = false;
				le.minWidth = 468f;
				le.preferredWidth = 468f;
				le.minHeight = 170f;
				le.preferredHeight = 170f;
				le.flexibleWidth = 2f;
				le.flexibleHeight = 0f;

				Core.MenuCard.SetupStepper(card, title, () => cfg.Value,
					onDec: () =>
					{
						cfg.Value = Mathf.Clamp(cfg.Value - step, min, max);
						if (cfg == ModConfig.JumpImpulse)
						{
							if (ModConfig.ForceJumpForce != null) ModConfig.ForceJumpForce.Value = cfg.Value;
							if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = true;
							try { PlayerRef.LocalApi()?.SetJumpImpulse(cfg.Value); } catch { }
							Toast.Show($"Jump Power: {cfg.Value:0.#} m/s");
						}
					},
					onInc: () =>
					{
						cfg.Value = Mathf.Clamp(cfg.Value + step, min, max);
						if (cfg == ModConfig.JumpImpulse)
						{
							if (ModConfig.ForceJumpForce != null) ModConfig.ForceJumpForce.Value = cfg.Value;
							if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = true;
							try { PlayerRef.LocalApi()?.SetJumpImpulse(cfg.Value); } catch { }
							Toast.Show($"Jump Power: {cfg.Value:0.#} m/s");
						}
					},
					onReset: () =>
					{
						cfg.Value = defVal;
						if (cfg == ModConfig.JumpImpulse)
						{
							if (ModConfig.ForceJumpForce != null) ModConfig.ForceJumpForce.Value = cfg.Value;
							if (ModConfig.JumpMod != null) ModConfig.JumpMod.Value = true;
							try { PlayerRef.LocalApi()?.SetJumpImpulse(cfg.Value); } catch { }
							Toast.Show($"Jump Power: {cfg.Value:0.#} m/s");
						}
					},
					format: format
				);

				foreach (var b in card.GetComponentsInChildren<Button>(true))
				{
					if (b != null) UiClick.SetDebounce(b, 0.05f);
				}

				_steppers.Add(new StepperRef
				{
					Card = card,
					Title = title,
					Cfg = cfg,
					LastVal = cfg.Value,
					Format = format
				});
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] AddStepper '{title}' failed: {e.Message}");
			}
		}

		private void AddSpeedStepper(Transform row, Transform donor)
		{
			try
			{
				var cardGo = UnityEngine.Object.Instantiate(donor.gameObject, row);
				cardGo.name = "Stepper_Speed";
				cardGo.SetActive(true);
				var card = cardGo.transform;

				var le = card.GetComponent<UnityEngine.UI.LayoutElement>() ?? cardGo.AddComponent<UnityEngine.UI.LayoutElement>();
				le.ignoreLayout = false;
				le.minWidth = 468f;
				le.preferredWidth = 468f;
				le.minHeight = 170f;
				le.preferredHeight = 170f;
				le.flexibleWidth = 2f;
				le.flexibleHeight = 0f;

				Action<float> setSpeed = (val) =>
				{
					ModConfig.RunSpeed.Value = val;
					ModConfig.WalkSpeed.Value = val;
					ModConfig.StrafeSpeed.Value = val;
					try
					{
						var api = PlayerRef.LocalApi();
						if (api != null)
						{
							api.SetRunSpeed(val);
							api.SetWalkSpeed(val);
							api.SetStrafeSpeed(val);
						}
					}
					catch { }
				};

				Core.MenuCard.SetupStepper(card, "Speed", () => ModConfig.RunSpeed.Value,
					onDec: () =>
					{
						float val = Mathf.Clamp(ModConfig.RunSpeed.Value - 1f, 1f, 40f);
						setSpeed(val);
						Toast.Show($"Speed: {val:0.#}");
					},
					onInc: () =>
					{
						float val = Mathf.Clamp(ModConfig.RunSpeed.Value + 1f, 1f, 40f);
						setSpeed(val);
						Toast.Show($"Speed: {val:0.#}");
					},
					onReset: () =>
					{
						float val = 4f;
						setSpeed(val);
						Toast.Show("Speed: Reset (4)");
					},
					format: "0.#"
				);

				foreach (var b in card.GetComponentsInChildren<Button>(true))
				{
					if (b != null) UiClick.SetDebounce(b, 0.05f);
				}

				_steppers.Add(new StepperRef
				{
					Card = card,
					Title = "Speed",
					Cfg = ModConfig.RunSpeed,
					LastVal = ModConfig.RunSpeed.Value,
					Format = "0.#"
				});
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] AddSpeedStepper failed: {e.Message}");
			}
		}


		private void FillPage(Transform page, Transform body)
		{
			try
			{
				// DARK SCRIM BACKDROP: Add a solid black/charcoal background behind the entire page
				// so that all buttons, icons, and text are crisp and clearly legible against custom wallpapers/bright worlds.
				Transform scrim = page.Find("Background_Scrim");
				if (scrim == null)
				{
					var scrimGo = new GameObject("Background_Scrim", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					var scrimRt = scrimGo.GetComponent<RectTransform>();
					scrimRt.SetParent(page, false);
					scrimRt.anchorMin = Vector2.zero;
					scrimRt.anchorMax = Vector2.one;
					scrimRt.offsetMin = Vector2.zero;
					scrimRt.offsetMax = Vector2.zero;
					var sImg = scrimGo.AddComponent<UnityEngine.UI.Image>();
					sImg.color = new Color(0.08f, 0.09f, 0.11f, 0.98f);
					sImg.raycastTarget = false;
					scrimGo.transform.SetAsFirstSibling();
				}
				else
				{
					scrim.SetAsFirstSibling();
					var sImg = scrim.GetComponent<UnityEngine.UI.Image>();
					if (sImg != null)
					{
						sImg.color = new Color(0.08f, 0.09f, 0.11f, 0.98f);
						sImg.raycastTarget = false;
					}
				}

				Transform content = FindContent(page);
				if (content == null) { VRChatArchiveModPlugin.Logger.LogWarning("[QMTab] page has no content node."); return; }

				Transform donorGrid = body.Find(QuickLinksPath);
				if (donorGrid == null || donorGrid.childCount == 0)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[QMTab] Launchpad quick-links grid not found — page left as-is.");
					return;
				}
				Transform donorCard = donorGrid.GetChild(0);

				// Clear existing content and forget old buttons
				for (int i = content.childCount - 1; i >= 0; i--)
				{
					var ch = content.GetChild(i);
					if (ch != null)
					{
						var btns = ch.GetComponentsInChildren<Button>(true);
						if (btns != null)
						{
							foreach (var b in btns) Core.UiClick.Forget(b);
						}
						try { UnityEngine.Object.DestroyImmediate(ch.gameObject); } catch { }
					}
				}

				_toggles.Clear();
				_steppers.Clear();
				_sections.Clear();
				_pairs.Clear();
				_spritesResolved = false;

				var contentRt = content.GetComponent<RectTransform>();
				if (contentRt != null)
				{
					contentRt.anchorMin = new Vector2(0f, 1f);
					contentRt.anchorMax = new Vector2(1f, 1f);
					contentRt.pivot = new Vector2(0.5f, 1f);
				}

				var vlg = content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
				if (vlg != null)
				{
					vlg.spacing = 16f;
					vlg.padding = new RectOffset(14, 14, 14, 100);
					vlg.childForceExpandHeight = false;
					vlg.childControlHeight = true;
					vlg.childForceExpandWidth = true;
					vlg.childControlWidth = true;
				}
				var csf = content.GetComponent<UnityEngine.UI.ContentSizeFitter>();
				if (csf == null) csf = content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
				csf.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
				csf.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;

				// Ensure ScrollRect is enabled, vertical, clamped, and sensitive
				var sr = page.GetComponentInChildren<UnityEngine.UI.ScrollRect>(true);
				if (sr != null)
				{
					sr.enabled = true;
					sr.vertical = true;
					sr.horizontal = false;
					sr.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
					sr.scrollSensitivity = 1.0f;
					sr.inertia = true;
					sr.decelerationRate = 0.135f;

					var srRt = sr.GetComponent<RectTransform>();
					if (srRt != null)
					{
						srRt.anchorMin = new Vector2(0f, 0f);
						srRt.anchorMax = new Vector2(1f, 1f);
						srRt.pivot = new Vector2(0.5f, 0.5f);
						srRt.offsetMin = new Vector2(10f, 25f);
						srRt.offsetMax = new Vector2(-10f, -105f);
					}

					var vp = sr.viewport ?? sr.transform.Find("Viewport")?.GetComponent<RectTransform>();
					if (vp != null)
					{
						vp.anchorMin = new Vector2(0f, 0f);
						vp.anchorMax = new Vector2(1f, 1f);
						vp.pivot = new Vector2(0.5f, 0.5f);
						vp.anchoredPosition = Vector2.zero;
						vp.sizeDelta = Vector2.zero;

						var mask = vp.GetComponent<UnityEngine.UI.RectMask2D>();
						if (mask == null) mask = vp.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
						mask.enabled = true;
					}

					if (contentRt != null) sr.content = contentRt;
					SetupScrollbar(page, sr, body);

					var srGo = sr.gameObject;
					var bgImg = srGo.GetComponent<UnityEngine.UI.Image>();
					if (bgImg == null) bgImg = srGo.AddComponent<UnityEngine.UI.Image>();
					bgImg.color = new Color(0.12f, 0.13f, 0.16f, 0.98f);
					bgImg.raycastTarget = false;
				}

				SpriteIndex.Invalidate();

				// ==================== SECTION 1: MOVEMENT ====================
				CreateSection(content, "Movement", out var movHost, startExpanded: true);

				// ROW 1: Ghost (Col 1) | Flight (Cols 2-3) | Save position (Col 4)
				var movR1 = AddRow(movHost);
				AddCard(movR1, new Act {
					Label = "Ghost",
					IconName = "visibility|Eye|Eye_Disabled",
					Do = GhostModule.Toggle,
					State = () => GhostModule.Active
				}, donorCard);

				AddCard(movR1, new Act {
					Label = "Flight",
					IconName = "ic_fly_mode|Drone_FlightModes|WingLeft",
					SecondaryIconName = "PlayerMove|BodyMode_Standing|Hand_Avatar",
					Do = () => {
						ModConfig.FlyEnabled.Value = !ModConfig.FlyEnabled.Value;
						Toast.Show(ModConfig.FlyEnabled.Value ? "Flight: ON" : "Flight: OFF");
					},
					State = () => ModConfig.FlyEnabled.Value
				}, donorCard, colSpan: 2);

				AddCard(movR1, new Act {
					Label = "Ghost Save Position",
					IconName = "ic_save|TeleportTo|DropPortal|FileSave",
					Do = () => {
						ModConfig.GhostSavePosition.Value = !ModConfig.GhostSavePosition.Value;
						Toast.Show(ModConfig.GhostSavePosition.Value ? "Ghost Save Pos: ON" : "Ghost Save Pos: OFF");
					},
					State = () => ModConfig.GhostSavePosition != null && ModConfig.GhostSavePosition.Value
				}, donorCard);

				// ROW 2: Speed mod (Col 1) | Speed Stepper (Cols 2-3) | Back to normal (Col 4)
				var movR2 = AddRow(movHost);
				AddCard(movR2, new Act {
					Label = "Speed mod",
					IconName = "PlayerMove|BodyMode_Standing|Arrow_Right",
					Do = () => {
						bool next = !(ModConfig.WalkMod.Value || ModConfig.RunMod.Value);
						ModConfig.WalkMod.Value = next;
						ModConfig.RunMod.Value = next;
						Toast.Show(next ? "Speed Mod: ON" : "Speed Mod: OFF");
					},
					State = () => ModConfig.WalkMod.Value || ModConfig.RunMod.Value
				}, donorCard);

				AddSpeedStepper(movR2, donorCard);

				AddCard(movR2, new Act {
					Label = "Back to normal",
					IconName = "ic_reset|reset|Home_Reset",
					Do = SpeedModule.ResetToWorld
				}, donorCard);

				// ROW 3: Jump mod (Col 1) | Jump power Stepper (Cols 2-3) | Click to teleport (Col 4)
				var movR3 = AddRow(movHost);
				AddCard(movR3, new Act {
					Label = "Jump mod",
					IconName = "arrow_up|Arrow_Right",
					Do = ForceJumpModule.Toggle,
					State = () => ForceJumpModule.Active || (ModConfig.JumpMod != null && ModConfig.JumpMod.Value)
				}, donorCard);

				AddStepper(movR3, "Jump power", ModConfig.JumpImpulse, 0.5f, 3.0f, 0.5f, 30f, donorCard);

				AddCard(movR3, new Act {
					Label = "Click to teleport",
					IconName = "TeleportTo|TeleportToMe|DropPortal",
					Do = () => {
						ModConfig.ClickTpEnabled.Value = !ModConfig.ClickTpEnabled.Value;
						Toast.Show(ModConfig.ClickTpEnabled.Value ? "Click TP: ON" : "Click TP: OFF");
					},
					State = () => ModConfig.ClickTpEnabled.Value
				}, donorCard);

				// ==================== SECTION 2: ESP ====================
				CreateSection(content, "ESP", out var espHost, startExpanded: true);
				var espR1 = AddRow(espHost);
				AddCard(espR1, new Act {
					Label = "Player ESP",
					IconName = "icon_user|Icon_Safety_Avatar_Shape|BodyMode_Standing|PlayerMove",
					Do = () => {
						ModConfig.EspCapsule.Value = !ModConfig.EspCapsule.Value;
						CapsuleEspModule.TriggerRelight();
						Toast.Show(ModConfig.EspCapsule.Value ? "Player ESP: ON" : "Player ESP: OFF");
					},
					State = () => ModConfig.EspCapsule.Value
				}, donorCard);
				AddCard(espR1, new Act {
					Label = "Player list",
					IconName = "Social|Friends|icon_user",
					Do = () => {
						ModConfig.InstancePanelsEnabled.Value = !ModConfig.InstancePanelsEnabled.Value;
						Toast.Show(ModConfig.InstancePanelsEnabled.Value ? "Player List: ON" : "Player List: OFF");
					},
					State = () => ModConfig.InstancePanelsEnabled.Value
				}, donorCard);
				AddCard(espR1, new Act {
					Label = "Avatar Outlines",
					IconName = "Icon_Spotlight|glow|DynamicLight",
					Do = () => {
						ModConfig.EspHighlight.Value = !ModConfig.EspHighlight.Value;
						HighlightEspModule.TriggerRelight();
						Toast.Show(ModConfig.EspHighlight.Value ? "Outlines: ON" : "Outlines: OFF");
					},
					State = () => ModConfig.EspHighlight.Value
				}, donorCard);
				AddCard(espR1, new Act {
					Label = "Pickups",
					IconName = "Grab|hand|Handshake",
					Do = () => {
						ModConfig.EspItems.Value = !ModConfig.EspItems.Value;
						HighlightEspModule.TriggerRelight();
						Toast.Show(ModConfig.EspItems.Value ? "ESP Pickups: ON" : "ESP Pickups: OFF");
					},
					State = () => ModConfig.EspItems.Value
				}, donorCard);

				var espR2 = AddRow(espHost);
				AddCard(espR2, new Act {
					Label = "Box ESP",
					IconName = "Icon_Safety_Avatar_Shape|ic_box|icon_user",
					Do = () => {
						ModConfig.EspEnabled.Value = !ModConfig.EspEnabled.Value;
						Toast.Show(ModConfig.EspEnabled.Value ? "Box ESP: ON" : "Box ESP: OFF");
					},
					State = () => ModConfig.EspEnabled.Value
				}, donorCard);
				AddCard(espR2, new Act {
					Label = "Portals",
					IconName = "DropPortal|Portal|TeleportTo",
					Do = () => {
						ModConfig.EspPortals.Value = !ModConfig.EspPortals.Value;
						HighlightEspModule.TriggerRelight();
						Toast.Show(ModConfig.EspPortals.Value ? "ESP Portals: ON" : "ESP Portals: OFF");
					},
					State = () => ModConfig.EspPortals.Value
				}, donorCard);
				AddCard(espR2, new Act {
					Label = "Radar",
					IconName = "prints_location|LocationUnavailable|TeleportToMe",
					Do = () => {
						ModConfig.RadarEnabled.Value = !ModConfig.RadarEnabled.Value;
						Toast.Show(ModConfig.RadarEnabled.Value ? "Radar: ON" : "Radar: OFF");
					},
					State = () => ModConfig.RadarEnabled.Value
				}, donorCard);
				AddCard(espR2, new Act {
					Label = "See through walls",
					IconName = "Eye|visibility|Eye_Disabled",
					Do = () => {
						ModConfig.EspThroughWalls.Value = !ModConfig.EspThroughWalls.Value;
						HighlightEspModule.TriggerRelight();
						CapsuleEspModule.TriggerRelight();
						Toast.Show(ModConfig.EspThroughWalls.Value ? "Through Walls: ON" : "Through Walls: OFF");
					},
					State = () => ModConfig.EspThroughWalls.Value
				}, donorCard);

				var espR3 = AddRow(espHost);
				AddCard(espR3, new Act {
					Label = "ESP Nameplate",
					IconName = "Tag|Chat|Social|Friends|icon_user",
					Do = () => {
						ModConfig.NameplateEsp.Value = !ModConfig.NameplateEsp.Value;
						NameplateEspModule.ApplyNameplateEsp(ModConfig.NameplateEsp.Value);
						Toast.Show(ModConfig.NameplateEsp.Value ? "ESP Nameplate: ON" : "ESP Nameplate: OFF");
					},
					State = () => ModConfig.NameplateEsp != null && ModConfig.NameplateEsp.Value
				}, donorCard);
				AddCard(espR3, new Act { Label = "Active Features HUD", IconName = "Logging|debug|Tag|Icon_UdonSpotlight|visibility", Do = () => { ModConfig.ActiveFeaturesHudEnabled.Value = !ModConfig.ActiveFeaturesHudEnabled.Value; Toast.Show(ModConfig.ActiveFeaturesHudEnabled.Value ? "Active Features HUD: ON" : "Active Features HUD: OFF"); }, State = () => ModConfig.ActiveFeaturesHudEnabled.Value }, donorCard);
				AddCard(espR3, new Act { Label = "Bottom Roster", IconName = "arrow_down|Arrow_Right|Social|Friends", Do = () => { ModConfig.RosterBottom.Value = !ModConfig.RosterBottom.Value; Toast.Show(ModConfig.RosterBottom.Value ? "Roster: Bottom" : "Roster: Top"); }, State = () => ModConfig.RosterBottom.Value }, donorCard);
				AddCard(espR3, new Act { Label = "Fast Sync", IconName = "Logging|debug|Icon_UdonSpotlight", Do = () => { if (ModConfig.FastSync != null) ModConfig.FastSync.Value = !ModConfig.FastSync.Value; }, State = () => ModConfig.FastSync != null && ModConfig.FastSync.Value }, donorCard);

				// ==================== SECTION 3: INTERACTION ====================
				CreateSection(content, "Interaction", out var intHost, startExpanded: true);
				var intR1 = AddRow(intHost);
				AddCard(intR1, new Act { Label = "Fullbright", IconName = "DynamicLight|Icon_Spotlight|glow", Do = ToggleFullbright, State = () => FullbrightActive }, donorCard);
				AddCard(intR1, new Act { Label = "Force Pickup", IconName = "Grab|hand|Handshake", Do = ForcePickupModule.Toggle, State = () => ForcePickupModule.Active }, donorCard);
				AddCard(intR1, new Act { Label = "Player Grab", IconName = "icon_user|Social|Friends|BodyMode_Standing|PlayerMove", Do = PlayerGrabModule.Toggle, State = () => PlayerGrabModule.Active }, donorCard);
				AddCard(intR1, new Act { Label = "Respawn pickups", IconName = "ReloadIcon|ic_reset|reset", Do = RespawnAllPickups }, donorCard);

				var intR2 = AddRow(intHost);
				AddCard(intR2, new Act { Label = "Object Orbit", IconName = "Drone_FlightModes|ic_fly_mode|ReloadIcon", Do = ObjectOrbitModule.ToggleOnSelf, State = () => ObjectOrbitModule.Active }, donorCard);
				AddCard(intR2, new Act { Label = "Elevator", IconName = "arrow_up|Arrow_Right", Do = ElevatorModule.ToggleOnSelf, State = () => ElevatorModule.Active }, donorCard);
				AddCard(intR2, new Act { Label = "Player Rotator", IconName = "BodyMode_Standing|PlayerMove|Hand_Avatar", Do = PlayerRotatorModule.Toggle, State = () => PlayerRotatorModule.Active }, donorCard);
				AddCard(intR2, new Act { Label = "Self Hide", IconName = "Eye_Disabled|visibility|Eye", Do = () => { if (ModConfig.SelfHide != null) ModConfig.SelfHide.Value = !ModConfig.SelfHide.Value; }, State = () => ModConfig.SelfHide != null && ModConfig.SelfHide.Value }, donorCard);

				var intR3 = AddRow(intHost);
				AddCard(intR3, new Act { Label = "Box Drop", IconName = "LocationUnavailable|prints_location|TeleportToMe", Do = BoxDropModule.Toggle, State = () => BoxDropModule.Active }, donorCard);
				AddCard(intR3, new Act { Label = "Bad Apple", IconName = "Icon_Spotlight|glow|DynamicLight", Do = BadAppleModule.RequestToggle, State = () => BadAppleModule.Playing }, donorCard);
				AddCard(intR3, new Act { Label = "Mute / Deafen", IconName = "Tag_Disabled|Tag|StopIcon", Do = ToggleDeafen, State = () => DeafenActive }, donorCard);

				// ==================== SECTION 4: TOOLS ====================
				CreateSection(content, "Tools", out var tlsHost, startExpanded: true);
				var tlsR1 = AddRow(tlsHost);
				AddCard(tlsR1, new Act { Label = "Crash protection", IconName = "Icon_Shield|Icon_Shield_Custom|shield", Do = () => ModConfig.AntiCrashEnabled.Value = !ModConfig.AntiCrashEnabled.Value, State = () => ModConfig.AntiCrashEnabled.Value }, donorCard);
				AddCard(tlsR1, new Act { Label = "Block crashers", IconName = "BlockUser|Blocked_White_Transparent|icon_listener_blocked|block", Do = () => ModConfig.UdonBlockCrashers.Value = !ModConfig.UdonBlockCrashers.Value, State = () => ModConfig.UdonBlockCrashers.Value }, donorCard);
				AddCard(tlsR1, new Act { Label = "Udon Block All", IconName = "StopIcon|stop|Icon_Close_X", Do = () => ModConfig.UdonBlockAll.Value = !ModConfig.UdonBlockAll.Value, State = () => ModConfig.UdonBlockAll.Value }, donorCard);
				AddCard(tlsR1, new Act { Label = "Udon Console", IconName = "Icon_UdonSpotlight|Logging|debug", Do = () => ModConfig.UdonLogEnabled.Value = !ModConfig.UdonLogEnabled.Value, State = () => ModConfig.UdonLogEnabled.Value }, donorCard);

				var tlsR2 = AddRow(tlsHost);
				AddCard(tlsR2, new Act { Label = "Re-check players", IconName = "ReloadIcon|ic_reset|reset", Do = Core.Menu.RequestRescan }, donorCard);
				AddCard(tlsR2, new Act { Label = "Refresh Tags", IconName = "Tag|Tag_Disabled|ReloadIcon", Do = VaTagsModule.RequestRefresh }, donorCard);
				AddCard(tlsR2, new Act { Label = "Udon Rescan", IconName = "ReloadIcon|ic_reset|reset", Do = UdonManagerModule.Rescan }, donorCard);
				AddCard(tlsR2, new Act { Label = "Restore Udon", IconName = "Home_Reset|ic_reset|reset", Do = () => { ModConfig.UdonBlockAll.Value = false; ModConfig.UdonBlockCrashers.Value = false; Toast.Show("Udon Blockers Reset"); } }, donorCard);

				var tlsR3 = AddRow(tlsHost);
				AddCard(tlsR3, new Act
				{
					Label = "Global Udon",
					IconName = "Icon_UdonSpotlight|Globe|WorldIcon|NetworkIcon",
					Do = () =>
					{
						ModConfig.GlobalUdonInteract.Value = !ModConfig.GlobalUdonInteract.Value;
						Toast.Show(ModConfig.GlobalUdonInteract.Value ? "Global Udon: ON" : "Global Udon: OFF");
					},
					State = () => ModConfig.GlobalUdonInteract.Value
				}, donorCard);

				// ==================== SECTION 5: SOUNDS (Collapsed by default) ====================
				CreateSection(content, "Sounds", out var sndHost, startExpanded: false);
				var sndR1 = AddRow(sndHost);
				foreach (var clip in SoundboardModule.Clips)
				{
					var c = clip;
					AddCard(sndR1, new Act
					{
						Label = c.Label,
						Do = () => SoundboardModule.Send(c),
						Icon = () => AssetLoader.Icon(c.Image) ?? AssetLoader.HeartIcon,
					}, donorCard);
				}

				RefreshToggles();

				if (sr != null)
				{
					sr.verticalNormalizedPosition = 1f;
					if (sr.verticalScrollbar != null) sr.verticalScrollbar.value = 1f;
				}

				var header = page.Find("Header_DevTools") ?? page.Find("Header_H1");
				if (header != null)
				{
					// Bring header to the very front so it renders above the scrollrect and cards
					header.SetAsLastSibling();

					// Enable and style HeaderBackground so the header is completely opaque
					var bg = header.Find("HeaderBackground");
					if (bg != null)
					{
						bg.gameObject.SetActive(true);
						var bgImg = bg.GetComponent<UnityEngine.UI.Image>();
						if (bgImg != null)
						{
							bgImg.color = new Color(0.08f, 0.09f, 0.11f, 0.80f);
						}
					}
					else
					{
						var hImg = header.GetComponent<UnityEngine.UI.Image>();
						if (hImg == null) hImg = header.gameObject.AddComponent<UnityEngine.UI.Image>();
						hImg.color = new Color(0.08f, 0.09f, 0.11f, 0.80f);
					}

					var htmp = header.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (htmp != null) htmp.text = "VRChat Archive";

				}

				VRChatArchiveModPlugin.Logger.LogInfo($"[QMTab] page filled with continuous VRTool-style sections and {(_toggles.Count + _steppers.Count)} control(s).");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] page fill failed: {e.Message}");
			}
		}

		private static void SetupScrollbar(Transform page, UnityEngine.UI.ScrollRect sr, Transform body)
		{
			if (sr == null || page == null) return;
			try
			{
				var oldSb = sr.transform.Find("Scrollbar_Vertical");
				if (oldSb != null) UnityEngine.Object.DestroyImmediate(oldSb.gameObject);

				// Sample native track/handle sprites from body if present in QM
				Sprite donorTrackSprite = null;
				Sprite donorHandleSprite = null;
				if (body != null)
				{
					var donors = body.GetComponentsInChildren<UnityEngine.UI.Scrollbar>(true);
					if (donors != null && donors.Length > 0)
					{
						foreach (var d in donors)
						{
							if (d != null && d.direction == UnityEngine.UI.Scrollbar.Direction.BottomToTop)
							{
								var trkImg = d.GetComponent<UnityEngine.UI.Image>();
								if (trkImg != null && trkImg.sprite != null) donorTrackSprite = trkImg.sprite;
								var hdlImg = d.handleRect != null ? d.handleRect.GetComponent<UnityEngine.UI.Image>() : null;
								if (hdlImg != null && hdlImg.sprite != null) donorHandleSprite = hdlImg.sprite;
								break;
							}
						}
					}
				}

				// Build the custom scrollbar on sr.transform
				var sbGo = new GameObject("Scrollbar_Vertical", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var sbRt = sbGo.GetComponent<RectTransform>();
				sbRt.SetParent(sr.transform, false);

				// Position in the exact yellow rectangle area:
				// Right edge of the page, full height between header line and bottom bar
				sbRt.anchorMin = new Vector2(1f, 0f);
				sbRt.anchorMax = new Vector2(1f, 1f);
				sbRt.pivot = new Vector2(1f, 0.5f);
				sbRt.sizeDelta = new Vector2(20f, 0f);
				sbRt.offsetMin = new Vector2(-28f, 10f);
				sbRt.offsetMax = new Vector2(-8f, -10f);

				var trackImg = sbGo.AddComponent<UnityEngine.UI.Image>();
				trackImg.sprite = donorTrackSprite ?? Core.MenuCard.RimSprite();
				trackImg.type = UnityEngine.UI.Image.Type.Sliced;
				trackImg.color = new Color(0.10f, 0.11f, 0.14f, 0.85f);
				trackImg.raycastTarget = true;

				var sb = sbGo.AddComponent<UnityEngine.UI.Scrollbar>();
				sb.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;

				// Sliding Area
				var slideGo = new GameObject("Sliding Area", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var slideRt = slideGo.GetComponent<RectTransform>();
				slideRt.SetParent(sbRt, false);
				slideRt.anchorMin = Vector2.zero;
				slideRt.anchorMax = Vector2.one;
				slideRt.offsetMin = new Vector2(2f, 4f);
				slideRt.offsetMax = new Vector2(-2f, -4f);

				// Handle (thumb)
				var handleGo = new GameObject("Handle", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var handleRt = handleGo.GetComponent<RectTransform>();
				handleRt.SetParent(slideRt, false);
				handleRt.anchorMin = Vector2.zero;
				handleRt.anchorMax = Vector2.one;
				handleRt.offsetMin = Vector2.zero;
				handleRt.offsetMax = Vector2.zero;

				var handleImg = handleGo.AddComponent<UnityEngine.UI.Image>();
				handleImg.sprite = donorHandleSprite ?? Core.MenuCard.RimSprite();
				handleImg.type = UnityEngine.UI.Image.Type.Sliced;
				handleImg.color = new Color(0.35f, 0.38f, 0.46f, 0.95f);
				handleImg.raycastTarget = true;

				// Grip Indicator in center of thumb
				var gripGo = new GameObject("HandleIcon", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
				var gripRt = gripGo.GetComponent<RectTransform>();
				gripRt.SetParent(handleRt, false);
				gripRt.anchorMin = new Vector2(0.5f, 0.5f);
				gripRt.anchorMax = new Vector2(0.5f, 0.5f);
				gripRt.pivot = new Vector2(0.5f, 0.5f);
				gripRt.sizeDelta = new Vector2(8f, 22f);
				var gripImg = gripGo.AddComponent<UnityEngine.UI.Image>();
				gripImg.sprite = Core.MenuCard.RimSprite();
				gripImg.type = UnityEngine.UI.Image.Type.Sliced;
				gripImg.color = new Color(0.60f, 0.63f, 0.74f, 0.85f);
				gripImg.raycastTarget = false;

				sb.handleRect = handleRt;
				sb.targetGraphic = handleImg;
				sb.interactable = true;
				sb.transition = UnityEngine.UI.Selectable.Transition.ColorTint;
				sb.colors = Core.MenuCard.Tint(new Color(0.36f, 0.39f, 0.48f, 1f));

				// Attach to ScrollRect
				sr.verticalScrollbar = sb;
				sr.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.Permanent;
				sr.verticalScrollbarSpacing = 0f;

				sbGo.SetActive(true);
				VRChatArchiveModPlugin.Logger.LogInfo("[QMTab] Vertical scrollbar created and attached to ScrollRect.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[QMTab] SetupScrollbar failed: {e.Message}");
			}
		}

		private static Transform FindContent(Transform page)
		{
			string[] paths = { "ScrollRect/Viewport/VerticalLayoutGroup", "Scrollrect/Viewport/VerticalLayoutGroup" };
			foreach (string p in paths) { var t = page.Find(p); if (t != null) return t; }
			return null;
		}

		private void RepairSprites()
		{
			if (_spritesResolved || _pairs.Count == 0) return;
			bool allDone = true;
			for (int i = 0; i < _pairs.Count; i++)
			{
				var p = _pairs[i];
				if (p == null || p.Clone == null || p.Donor == null) continue;
				if (!Core.MenuCard.CopySprite(p.Donor, p.Clone, "Background")) allDone = false;
				if (!Core.MenuCard.CopySprite(p.Donor, p.Clone, "Icons/Icon")) allDone = false;
			}
			if (allDone)
			{
				_spritesResolved = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[QMTab] card sprites resolved.");
			}
		}

		private void RefreshToggles()
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
					Core.MenuCard.SetLit(t.Card, on, keepStyle: false);
				}
				catch { }
			}
		}

		private bool _wasActive;

		private void Pump()
		{
			try
			{
				bool active = _page != null && _page.activeSelf;
				bool shown = active && !_wasActive;
				_wasActive = active;
				if (!active) return;

				if (shown)
				{
					_page.transform.SetAsLastSibling();
					var h = _page.transform.Find("Header_DevTools") ?? _page.transform.Find("Header_H1");
					if (h != null) h.SetAsLastSibling();
				}
				var lp = _body != null ? _body.Find("Menu_QM_Launchpad") : null;
				if (lp != null && lp.gameObject.activeSelf)
				{
					int want = lp.GetSiblingIndex() + 1;
					if (_page.transform.GetSiblingIndex() < want) _page.transform.SetSiblingIndex(want);
				}

				RepairSprites();
				RefreshToggles();

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
							Core.MenuCard.UpdateStepperText(s.Card, s.Title, cur, s.Format);
						}
					}
					catch { }
				}

				float now = VaClock.Now;
				if (now >= _nextLayout)
				{
					_nextLayout = now + 0.5f;
					for (int i = 0; i < _pairs.Count; i++)
					{
						var p = _pairs[i];
						if (p != null && p.Clone != null) Core.MenuCard.Relayout(p.Clone);
					}
				}
			}
			catch { }
		}

		private float _nextLayout;

		private void Teardown()
		{
			try { if (_page != null) _page.SetActive(_pageWasActive); } catch { }
			try { if (_tab != null) _tab.SetActive(_tabWasActive); } catch { }

			if (_page != null)
			{
				var content = FindContent(_page.transform);
				if (content != null)
				{
					for (int i = content.childCount - 1; i >= 0; i--)
					{
						try { UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject); } catch { }
					}
				}
			}

			_steppers.Clear();
			_sections.Clear();
			_pairs.Clear();
			_toggles.Clear();
			_wasActive = false;
			_page = null; _tab = null; _body = null; _fails = 0; _nextTry = 0f;
		}

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
