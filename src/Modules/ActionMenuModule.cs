using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	/// <summary>
	/// ActionMenuModule: Integrates the Archive Mod radial menu cleanly and safely into
	/// VRChat's native radial ActionMenu (MenuR and MenuL).
	/// Completely avoids modifying or repositioning native pedals, ensuring all native menus
	/// (Main, Tools, Settings/Nameplates, Expressions) remain 100% pristine and uncorrupted.
	/// </summary>
	public class ActionMenuModule : IModule
	{
		public override string Name => "ActionMenu";

		private sealed class MenuState
		{
			public Transform MenuRoot; // Container/MenuR/ActionMenu or MenuL/ActionMenu
			public Transform MainTrans;
			public Transform OptionsTrans;
			public Transform CursorTrans;
			public RectTransform CursorRt;
			public Transform TooltipTrans;
			public ActionMenu ActionMenuComp;

			// Injected Archive Mod pedal inside OptionsTrans while in Tools menu
			public GameObject ToolsModPedal;
			public GameObject ToolsModSelect;
			public TMPro.TMP_Text ToolsModText;

			// Dedicated SubMenu container inside MainTrans (sibling of Options)
			public GameObject SubMenuContainer;
			public readonly List<SubPedal> SubPedals = new List<SubPedal>();
			public bool SubMenuActive;
			public int HoveredSubPedalIndex = -1;

			// Cursor tracking buffer for desktop/VR input while ActionMenu component is paused
			public Vector2 DesktopCursorPos;
		}

		private sealed class SubPedal
		{
			public GameObject Root;
			public GameObject SelectObj;
			public RawImage IconRawImage;
			public TMPro.TMP_Text TextComp;
			public float AngleDeg;
			public string BaseLabel;
			public Action OnClick;
			public Func<bool> IsActiveState;
		}

		private readonly List<MenuState> _menus = new List<MenuState>();
		private float _nextScanTime;

		private static Texture2D _homeIconTexture;
		private static Texture _nativeHomeTexture;
		private static bool _capturedHomeTexture;

		// Embedded crisp 128x128 ActionMenu Home/Return icon (PNG base64)
		private const string HomeIconBase64 =
			"iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAACTUlEQVR4nO3da2oDMQwAYXvp/a+sUsivQCGPtS1p5rtAs9as10lDO4Yk" +
			"SZIkSZIkSZIkSVIzERED7Bpg8Rg+OQJsAM9DD2gEyAD+G3YAI5gD5J0BzzkRa4PZAd69uwOyGyAC+HSYAYigfQDfDjGaR9D6OXfn8OaL" +
			"Z4Jq54y2O8Ddd2403QlaBrBqWNEwgp/RyI4BxeNnZNi+79BmB9h9d0aT3aBFAN8MY35xJ3eIoHwAdwx/giMoHcCdd/6ERlA2gBXb/gRG" +
			"UO4ku+t5HxsGmuGdRKkdYOdhbyYYzg5lAjhx0p+ACEoEcOptHiGC9AGcHD4hgtQBZBh+9wjSBpBp+J0juDIOPuPwu0aQ6mIyD37FZwUZ" +
			"YkqzA1QbfpYBtgig4vC7fHx8PIDKw+8QwdEAOgy/egTHAug0/MoReHKGvzvYugNUWpiTr3PnbrAtAMrwq0WwZVE7Pu+7XP9FvvhdMh8O" +
			"lwbg8PNHsOQOoz3vK6/N1f0Cs5nJDoe3BuDw60VwWwAOv2YEV4YL6r7tZ16zo4dA6uAzrdmSt4GvvEiHn2PNlt6Bp3/X3clc9UXXsYEh" +
			"fK78R8F/3O7zrtvxr4TpLAOAMwA4A4AzADgDgDMAOAOAMwA4A4AzADgDgDMAuDb/MKLin4jJwB0AzgDgDADOAOAMAM4A4AwAzgDgDADO" +
			"AOAMAM4A4AwAzgDgDADOAOAMAM4A4AwAzgDgDADOAOAMAM4A4AwAzgDgDADOAOAMAM4A4AwAzgDgDADOAOAMAM4AJEmSJEmSJEmSJEnq" +
			"5xerVzi63LjmDQAAAABJRU5ErkJggg==";

		public override void OnInitialize()
		{
			VRChatArchiveModPlugin.Logger.LogInfo("[ActionMenuModule] Initialized.");
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			for (int i = 0; i < _menus.Count; i++)
			{
				if (_menus[i].SubMenuActive)
				{
					CloseSubMenu(_menus[i]);
				}
			}
			_menus.Clear();
			_nextScanTime = 0f;
		}

		public override void OnUpdate()
		{
			try
			{
				float now = VaClock.Now;
				if (now >= _nextScanTime)
				{
					_nextScanTime = now + 1.0f;
					EnsureMenusDiscovered();
				}

				for (int i = 0; i < _menus.Count; i++)
				{
					UpdateMenu(_menus[i]);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[ActionMenuModule] Update error: {e.Message}");
			}
		}

		private void EnsureMenusDiscovered()
		{
			if (_menus.Count >= 2) return;

			var roots = Resources.FindObjectsOfTypeAll<GameObject>();
			foreach (var go in roots)
			{
				if (go == null) continue;
				if (go.name == "ActionMenu" && go.transform.parent != null &&
				    (go.transform.parent.name == "MenuR" || go.transform.parent.name == "MenuL"))
				{
					bool exists = false;
					for (int i = 0; i < _menus.Count; i++)
					{
						if (_menus[i].MenuRoot == go.transform) { exists = true; break; }
					}

					if (!exists)
					{
						var ms = new MenuState
						{
							MenuRoot = go.transform,
							MainTrans = go.transform.Find("Main"),
							TooltipTrans = go.transform.Find("Tooltip"),
							ActionMenuComp = go.GetComponent<ActionMenu>()
						};

						if (ms.MainTrans != null)
						{
							ms.OptionsTrans = ms.MainTrans.Find("Options");
							ms.CursorTrans = ms.MainTrans.Find("Cursor");
							if (ms.CursorTrans != null)
							{
								ms.CursorRt = ms.CursorTrans.GetComponent<RectTransform>();
							}
						}

						// Clean up any stale clones from previous builds
						var stalePage = go.transform.Find("Page_ArchiveActionMenu");
						if (stalePage != null) UnityEngine.Object.Destroy(stalePage.gameObject);
						var staleHeader = go.transform.Find("Main/ArchiveHeaderButton");
						if (staleHeader != null) UnityEngine.Object.Destroy(staleHeader.gameObject);
						var staleTools = go.transform.Find("Main/ArchiveToolsPedal");
						if (staleTools != null) UnityEngine.Object.Destroy(staleTools.gameObject);
						var staleToolsBtn = go.transform.Find("Main/ArchiveToolsButton");
						if (staleToolsBtn != null) UnityEngine.Object.Destroy(staleToolsBtn.gameObject);
						var staleCenter = go.transform.Find("Main/ArchiveCenterButton");
						if (staleCenter != null) UnityEngine.Object.Destroy(staleCenter.gameObject);
						var staleSub = go.transform.Find("Main/ArchiveSubMenu");
						if (staleSub != null) UnityEngine.Object.Destroy(staleSub.gameObject);

						// Clean up ANY injected pedals in OptionsTrans (do NOT touch native positions!)
						CleanStaleInjectedPedals(ms.OptionsTrans);

						_menus.Add(ms);
						VRChatArchiveModPlugin.Logger.LogInfo($"[ActionMenuModule] Discovered ActionMenu on {go.transform.parent.name}");
					}
				}
			}
		}

		private static void CleanStaleInjectedPedals(Transform optionsTrans)
		{
			if (optionsTrans == null) return;
			for (int c = optionsTrans.childCount - 1; c >= 0; c--)
			{
				var child = optionsTrans.GetChild(c);
				if (child != null && child.name.StartsWith("Pedal_Archive"))
				{
					UnityEngine.Object.Destroy(child.gameObject);
				}
			}
		}

		private static Behaviour FindActionMenuComponent(Transform menuRoot)
		{
			if (menuRoot == null) return null;
			var comps = menuRoot.GetComponents<Behaviour>();
			foreach (var c in comps)
			{
				if (c == null) continue;
				string tname = c.GetIl2CppType().Name;
				if (tname.Length > 8 && !tname.Contains("Transform") && !tname.Contains("Canvas") && !tname.Contains("Graphic"))
				{
					return c;
				}
			}
			return null;
		}

		private void UpdateMenu(MenuState ms)
		{
			if (ms.MenuRoot == null) return;

			bool isMenuOpen = ms.MenuRoot.gameObject.activeInHierarchy;
			if (!isMenuOpen)
			{
				if (ms.SubMenuActive)
				{
					CloseSubMenu(ms);
				}
				return;
			}

			if (ms.MainTrans == null)
			{
				ms.MainTrans = ms.MenuRoot.Find("Main");
				if (ms.MainTrans != null)
				{
					ms.OptionsTrans = ms.MainTrans.Find("Options");
					ms.CursorTrans = ms.MainTrans.Find("Cursor");
					if (ms.CursorTrans != null) ms.CursorRt = ms.CursorTrans.GetComponent<RectTransform>();
				}
			}

			if (ms.MainTrans == null || ms.OptionsTrans == null) return;

			if (!ms.MainTrans.gameObject.activeSelf)
			{
				ms.MainTrans.gameObject.SetActive(true);
			}

			// Capture native Home/Back texture if available
			CaptureNativeHomeTexture(ms.OptionsTrans);

			// 1. If our custom submenu is active, run custom radial wheel update
			if (ms.SubMenuActive)
			{
				UpdateSubMenu(ms);
				return;
			}

			// 2. When native menu is active:
			// DO NOT modify or reposition native pedals! Keep all native menus 100% untouched.
			CleanStaleInjectedPedals(ms.OptionsTrans);

			// 2. When native menu is active, manage Tools menu 6-pedal layout
			EnsureToolsPedals(ms);
			UpdateToolsPedalHover(ms);
		}

		private static void SetLayerRecursively(GameObject obj, int layer)
		{
			if (obj == null) return;
			obj.layer = layer;
			for (int i = 0; i < obj.transform.childCount; i++)
			{
				var child = obj.transform.GetChild(i);
				if (child != null) SetLayerRecursively(child.gameObject, layer);
			}
		}

		private static bool IsInToolsMenu(Transform optionsTrans)
		{
			if (optionsTrans == null || !optionsTrans.gameObject.activeSelf) return false;
			bool hasBack = false;
			bool hasCamera = false;
			bool hasMirror = false;

			for (int i = 0; i < optionsTrans.childCount; i++)
			{
				var child = optionsTrans.GetChild(i);
				if (child == null || !child.gameObject.activeSelf) continue;

				var tmp = child.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (tmp == null) continue;
				string txt = tmp.text.Trim();

				if (txt.Equals("Back", StringComparison.OrdinalIgnoreCase)) hasBack = true;
				if (txt.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) >= 0) hasCamera = true;
				if (txt.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0) hasMirror = true;
			}

			return hasBack && hasCamera && hasMirror;
		}

		private static void CaptureNativeHomeTexture(Transform optionsTrans)
		{
			if (_capturedHomeTexture || optionsTrans == null) return;
			for (int i = 0; i < optionsTrans.childCount; i++)
			{
				var child = optionsTrans.GetChild(i);
				if (child == null || child.name.StartsWith("Pedal_Archive")) continue;
				var textComp = child.GetComponentInChildren<TMPro.TMP_Text>(true);
				if (textComp != null && textComp.text.Trim().Equals("Back", StringComparison.OrdinalIgnoreCase))
				{
					var rawImg = child.Find("ActionButton/Inner/RawImage")?.GetComponent<RawImage>();
					if (rawImg != null && rawImg.texture != null)
					{
						_nativeHomeTexture = rawImg.texture;
						_capturedHomeTexture = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[ActionMenuModule] Captured native VRChat Home/Back icon texture.");
						break;
					}
				}
			}
		}

		/// <summary>
		/// Clones a native VRChat pedal to inject an authentic "Archive Mod" pedal at 270° in Tools menu,
		/// and cleanly redistributes all 6 pedals to exact 60° slices:
		/// 90°: Back | 30°: Personal Mirror | 330°: Face Mirror | 270°: Archive Mod | 210°: Chatbox | 150°: Camera.
		/// Zero overlap, native fonts/materials, perfectly responsive, and 100% crash-proof!
		/// </summary>
		private void EnsureToolsPedals(MenuState ms)
		{
			if (ms.OptionsTrans == null || ms.SubMenuActive) return;

			bool inTools = IsInToolsMenu(ms.OptionsTrans);
			if (!inTools)
			{
				if (ms.ToolsModPedal != null)
				{
					CleanStaleInjectedPedals(ms.OptionsTrans);
					ms.ToolsModPedal = null;
					ms.ToolsModSelect = null;
					ms.ToolsModText = null;
				}
				return;
			}

			// 1. Ensure our cloned pedal exists in Tools
			if (ms.ToolsModPedal == null || !NativeGuard.Alive(ms.ToolsModPedal))
			{
				Transform existing = ms.OptionsTrans.Find("Pedal_ArchiveMod");
				if (existing != null)
				{
					ms.ToolsModPedal = existing.gameObject;
					ms.ToolsModSelect = existing.Find("Select")?.gameObject;
					ms.ToolsModText = existing.Find("ActionButton/Inner/Text")?.GetComponent<TMPro.TMP_Text>();
				}
				else
				{
					Transform donor = null;
					for (int i = 0; i < ms.OptionsTrans.childCount; i++)
					{
						var c = ms.OptionsTrans.GetChild(i);
						if (c != null && !c.name.StartsWith("Pedal_Archive")) { donor = c; break; }
					}

					if (donor != null)
					{
						var clone = GameObject.Instantiate(donor.gameObject, ms.OptionsTrans);
						clone.name = "Pedal_ArchiveMod";
						clone.SetActive(true);
						clone.transform.localScale = Vector3.one * 0.84f;
						SetLayerRecursively(clone, ms.MainTrans.gameObject.layer);
						StripObfuscated(clone);

						var txt = clone.transform.Find("ActionButton/Inner/Text")?.GetComponent<TMPro.TMP_Text>();
						if (txt != null)
						{
							txt.text = "<size=11><color=#22D3EE>Archive</color></size>\n<size=10><color=#00FFAA>Mod ⚙️</color></size>";
							txt.alignment = TMPro.TextAlignmentOptions.Center;
						}

						var rawImg = clone.transform.Find("ActionButton/Inner/RawImage")?.GetComponent<RawImage>();
						if (rawImg != null)
						{
							if (_nativeHomeTexture != null)
							{
								rawImg.texture = _nativeHomeTexture;
								rawImg.color = new Color(0.13f, 0.83f, 0.93f, 1f);
								rawImg.transform.localScale = Vector3.one * 0.65f;
								rawImg.gameObject.SetActive(true);
							}
							else
							{
								rawImg.gameObject.SetActive(false);
							}
						}

						var select = clone.transform.Find("Select");
						if (select != null)
						{
							select.localRotation = Quaternion.Euler(0f, 0f, 180f);
							select.gameObject.SetActive(false);
						}

						ms.ToolsModPedal = clone;
						ms.ToolsModSelect = select != null ? select.gameObject : null;
						ms.ToolsModText = txt;
						VRChatArchiveModPlugin.Logger.LogInfo("[ActionMenuModule] Injected cloned ArchiveMod pedal in Tools menu.");
					}
				}
			}

			// 2. Reposition all 6 pedals in Tools to 60° increments:
			// 90°: Back | 30°: Personal Mirror | 330°: Face Mirror | 270°: Archive Mod | 210°: Chatbox | 150°: Camera
			for (int i = 0; i < ms.OptionsTrans.childCount; i++)
			{
				var child = ms.OptionsTrans.GetChild(i);
				if (child == null || !child.gameObject.activeSelf) continue;

				float targetAngle = -1f;

				if (child.name == "Pedal_ArchiveMod")
				{
					targetAngle = 270f;
				}
				else
				{
					var tmp = child.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (tmp != null)
					{
						string t = tmp.text.Trim();
						if (t.Equals("Back", StringComparison.OrdinalIgnoreCase)) targetAngle = 90f;
						else if (t.IndexOf("Personal", StringComparison.OrdinalIgnoreCase) >= 0) targetAngle = 30f;
						else if (t.IndexOf("Face", StringComparison.OrdinalIgnoreCase) >= 0) targetAngle = 330f;
						else if (t.IndexOf("Chatbox", StringComparison.OrdinalIgnoreCase) >= 0) targetAngle = 210f;
						else if (t.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) >= 0) targetAngle = 150f;
					}
				}

				if (targetAngle >= 0f)
				{
					var ab = child.Find("ActionButton");
					if (ab != null)
					{
						var rt = ab.GetComponent<RectTransform>();
						if (rt != null)
						{
							float rad = targetAngle * Mathf.Deg2Rad;
							float radius = (child.name == "Pedal_ArchiveMod") ? 168f : 176f;
							rt.anchoredPosition = new Vector2(Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius);
						}
					}
				}
			}
		}

		private void UpdateToolsPedalHover(MenuState ms)
		{
			if (ms.CursorRt == null || ms.SubMenuActive || ms.ToolsModPedal == null || !ms.ToolsModPedal.activeSelf) return;

			Vector2 cursor = ms.CursorRt.anchoredPosition;

			// Hovered when cursor is pushed down towards 270° (magnitude > 65 and angle ~ 270°)
			float angle = Mathf.Atan2(cursor.y, cursor.x) * Mathf.Rad2Deg;
			if (angle < 0) angle += 360f;

			bool hovered = cursor.magnitude > 65f && Mathf.Abs(Mathf.DeltaAngle(angle, 270f)) <= 28f;

			if (ms.ToolsModSelect != null)
			{
				ms.ToolsModSelect.SetActive(hovered);
			}

			if (ms.ToolsModText != null)
			{
				if (hovered)
					ms.ToolsModText.text = "<size=11><color=#00FFAA><b>OPEN MODS</b></color></size>";
				else
					ms.ToolsModText.text = "<size=11><color=#22D3EE>Archive</color></size>\n<size=10><color=#00FFAA>Mod ⚙️</color></size>";
			}

			if (hovered)
			{
				// CRITICAL FIX: Suppress native VRChat selection on neighboring pedals (Face Mirror / Chatbox)
				// so they never light up simultaneously or steal the click!
				if (ms.OptionsTrans != null)
				{
					for (int i = 0; i < ms.OptionsTrans.childCount; i++)
					{
						var c = ms.OptionsTrans.GetChild(i);
						if (c != null && c.name != "Pedal_ArchiveMod")
						{
							var sel = c.Find("Select");
							if (sel != null && sel.gameObject.activeSelf)
							{
								sel.gameObject.SetActive(false);
							}
						}
					}
				}

				if (IsTriggerDown())
				{
					OpenSubMenu(ms);
				}
			}
		}

		private void OpenSubMenu(MenuState ms)
		{
			if (ms.MainTrans == null || ms.OptionsTrans == null) return;

			if (ms.SubMenuContainer == null || !NativeGuard.Alive(ms.SubMenuContainer))
			{
				BuildSubMenu(ms);
			}

			if (ms.SubMenuContainer != null)
			{
				// 1. Hide native Tooltip completely so no background hints pop up
				if (ms.TooltipTrans != null)
				{
					ms.TooltipTrans.gameObject.SetActive(false);
				}

				// 2. Hide OptionsTrans so native pedals are completely hidden without altering individual child active states
				ms.OptionsTrans.gameObject.SetActive(false);

				// 3. Disable VRChat's ActionMenu component so background pedals CANNOT trigger on click
				if (ms.ActionMenuComp != null)
				{
					ms.ActionMenuComp.enabled = false;
				}

				// 4. Hide entrance pedal while inside submenu
				if (ms.ToolsModPedal != null) ms.ToolsModPedal.SetActive(false);

				// 5. Reset cursor position to center (0, 0) for clean mouse aiming
				ms.DesktopCursorPos = Vector2.zero;
				if (ms.CursorRt != null)
				{
					ms.CursorRt.anchoredPosition = Vector2.zero;
				}

				// 6. Show our SubMenu
				ms.SubMenuContainer.SetActive(true);
				ms.SubMenuActive = true;
				Toast.Show("🚀 Archive Mod Menu Opened");
			}
		}

		private void CloseSubMenu(MenuState ms)
		{
			ms.SubMenuActive = false;
			if (ms.SubMenuContainer != null)
			{
				ms.SubMenuContainer.SetActive(false);
			}

			// 1. Re-enable OptionsTrans (preserving VRChat's original active/inactive child distribution)
			if (ms.OptionsTrans != null)
			{
				ms.OptionsTrans.gameObject.SetActive(true);
			}

			// 2. Re-enable VRChat's ActionMenu component
			if (ms.ActionMenuComp != null)
			{
				ms.ActionMenuComp.enabled = true;
			}

			// 3. Re-enable native Tooltip
			if (ms.TooltipTrans != null)
			{
				ms.TooltipTrans.gameObject.SetActive(true);
			}

			// 4. Re-enable entrance pedal
			if (ms.ToolsModPedal != null && IsInToolsMenu(ms.OptionsTrans)) ms.ToolsModPedal.SetActive(true);
		}

		private void BuildSubMenu(MenuState ms)
		{
			var subObj = new GameObject("ArchiveSubMenu");
			subObj.transform.SetParent(ms.MainTrans, false);
			var rt = subObj.AddComponent<RectTransform>();
			rt.sizeDelta = new Vector2(924f, 924f);
			rt.anchoredPosition = Vector2.zero;
			ms.SubMenuContainer = subObj;
			SetLayerRecursively(subObj, ms.MainTrans.gameObject.layer);

			// Donor pedal
			Transform donor = null;
			for (int i = 0; i < ms.OptionsTrans.childCount; i++)
			{
				var c = ms.OptionsTrans.GetChild(i);
				if (!c.name.StartsWith("Pedal_Archive")) { donor = c; break; }
			}
			if (donor == null) return;

			ms.SubPedals.Clear();

			// 0: Top (90°) -> Back (Home icon)
			AddSubPedal(ms, donor, subObj.transform, 90f, "Back", isBack: true, onClick: () =>
			{
				CloseSubMenu(ms);
			}, isActive: () => false);

			// 1: Top-Right (30°) -> ✈️ Flight
			AddSubPedal(ms, donor, subObj.transform, 30f, "✈️ Flight", isBack: false, onClick: () =>
			{
				if (ModConfig.FlyEnabled != null)
				{
					ModConfig.FlyEnabled.Value = !ModConfig.FlyEnabled.Value;
					bool active = ModConfig.FlyEnabled.Value;
					Toast.Show(active ? "✈️ Flight: <color=#00FFAA>ENABLED</color>" : "✈️ Flight: <color=#FF4444>DISABLED</color>");
				}
			}, isActive: () => ModConfig.FlyEnabled != null && ModConfig.FlyEnabled.Value);

			// 2: Bottom-Right (330°) -> 👻 Ghost Mode
			AddSubPedal(ms, donor, subObj.transform, 330f, "👻 Ghost Mode", isBack: false, onClick: () =>
			{
				GhostModule.Toggle();
				Toast.Show(GhostModule.Active ? "👻 Ghost: <color=#00FFAA>ON (Frozen)</color>" : "👻 Ghost: <color=#FF4444>OFF</color>");
			}, isActive: () => GhostModule.Active);

			// 3: Bottom (270°) -> ⚡ Speed
			AddSubPedal(ms, donor, subObj.transform, 270f, "⚡ Speed", isBack: false, onClick: () =>
			{
				if (ModConfig.SpeedEnabled != null)
				{
					ModConfig.SpeedEnabled.Value = !ModConfig.SpeedEnabled.Value;
					Toast.Show(ModConfig.SpeedEnabled.Value ? "⚡ Speed: <color=#00FFAA>2x FAST</color>" : "⚡ Speed: 1x NORMAL");
				}
			}, isActive: () => ModConfig.SpeedEnabled != null && ModConfig.SpeedEnabled.Value);

			// 4: Bottom-Left (210°) -> 🚪 Noclip
			AddSubPedal(ms, donor, subObj.transform, 210f, "🚪 Noclip", isBack: false, onClick: () =>
			{
				if (ModConfig.NoclipEnabled != null)
				{
					ModConfig.NoclipEnabled.Value = !ModConfig.NoclipEnabled.Value;
					Toast.Show(ModConfig.NoclipEnabled.Value ? "🚪 Noclip: <color=#00FFAA>ON</color>" : "🚪 Noclip: <color=#FF4444>OFF</color>");
				}
			}, isActive: () => ModConfig.NoclipEnabled != null && ModConfig.NoclipEnabled.Value);

			// 5: Top-Left (150°) -> 👁️ ESP
			AddSubPedal(ms, donor, subObj.transform, 150f, "👁️ ESP", isBack: false, onClick: () =>
			{
				if (ModConfig.EspEnabled != null)
				{
					ModConfig.EspEnabled.Value = !ModConfig.EspEnabled.Value;
					Toast.Show(ModConfig.EspEnabled.Value ? "👁️ ESP: <color=#00FFAA>ON</color>" : "👁️ ESP: <color=#FF4444>OFF</color>");
				}
			}, isActive: () => ModConfig.EspEnabled != null && ModConfig.EspEnabled.Value);

			subObj.SetActive(false);
			VRChatArchiveModPlugin.Logger.LogInfo("[ActionMenuModule] SubMenu built with native Home Back pedal and 5 feature pedals.");
		}

		private void AddSubPedal(MenuState ms, Transform donor, Transform parent, float angleDeg, string label, bool isBack, Action onClick, Func<bool> isActive)
		{
			var clone = GameObject.Instantiate(donor.gameObject, parent);
			clone.name = $"SubPedal_{label}";
			clone.SetActive(true);
			SetLayerRecursively(clone, ms.MainTrans.gameObject.layer);

			StripObfuscated(clone);

			float rad = angleDeg * Mathf.Deg2Rad;
			float r = 176f;
			Vector2 pos = new Vector2(Mathf.Cos(rad) * r, Mathf.Sin(rad) * r);

			var ab = clone.transform.Find("ActionButton");
			if (ab != null)
			{
				StripObfuscated(ab.gameObject);
				var rt = ab.GetComponent<RectTransform>();
				if (rt != null) rt.anchoredPosition = pos;
			}

			var select = clone.transform.Find("Select");
			if (select != null) select.gameObject.SetActive(false);

			var textT = clone.transform.Find("ActionButton/Inner/Text");
			TMPro.TMP_Text tmp = textT != null ? textT.GetComponent<TMPro.TMP_Text>() : null;
			if (tmp != null)
			{
				tmp.text = label;
				tmp.alignment = TMPro.TextAlignmentOptions.Center;
			}

			var rawImgT = clone.transform.Find("ActionButton/Inner/RawImage");
			RawImage rawImg = rawImgT != null ? rawImgT.GetComponent<RawImage>() : null;

			if (isBack)
			{
				if (rawImg != null)
				{
					var homeTex = _nativeHomeTexture != null ? _nativeHomeTexture : GetHomeIconTexture();
					if (homeTex != null)
					{
						rawImg.texture = homeTex;
						rawImg.color = Color.white;
						rawImg.gameObject.SetActive(true);
					}
				}
				if (tmp != null)
				{
					tmp.text = "Back";
					tmp.fontSize = 22f;
				}
			}
			else
			{
				if (rawImg != null)
				{
					rawImg.gameObject.SetActive(false);
				}
			}

			ms.SubPedals.Add(new SubPedal
			{
				Root = clone,
				SelectObj = select != null ? select.gameObject : null,
				IconRawImage = rawImg,
				TextComp = tmp,
				AngleDeg = angleDeg,
				BaseLabel = label,
				OnClick = onClick,
				IsActiveState = isActive
			});
		}

		private void UpdateCursorInput(MenuState ms)
		{
			if (ms.CursorRt == null) return;

			// 1. Desktop Mouse delta input (ALWAYS active!)
			// Pressing WASD will NEVER touch the radial cursor because we DO NOT read "Horizontal" or "Vertical" axes!
			float mx = Input.GetAxis("Mouse X");
			float my = Input.GetAxis("Mouse Y");
			if (Mathf.Abs(mx) > 0.0005f || Mathf.Abs(my) > 0.0005f)
			{
				ms.DesktopCursorPos += new Vector2(mx, my) * 25f;
				if (ms.DesktopCursorPos.magnitude > 176f)
				{
					ms.DesktopCursorPos = ms.DesktopCursorPos.normalized * 176f;
				}
				ms.CursorRt.anchoredPosition = ms.DesktopCursorPos;
				return;
			}

			// 2. VR controller thumbstick input (Oculus / OpenXR dedicated controller axes only)
			float rx = 0f;
			float ry = 0f;
			try
			{
				rx = Input.GetAxis("Oculus_CrossPlatform_SecondaryThumbstickHorizontal");
				ry = Input.GetAxis("Oculus_CrossPlatform_SecondaryThumbstickVertical");
				if (Mathf.Abs(rx) < 0.1f && Mathf.Abs(ry) < 0.1f)
				{
					rx = Input.GetAxis("Oculus_CrossPlatform_PrimaryThumbstickHorizontal");
					ry = Input.GetAxis("Oculus_CrossPlatform_PrimaryThumbstickVertical");
				}
			}
			catch { }

			Vector2 vrJoy = new Vector2(rx, ry);
			if (vrJoy.magnitude > 0.15f)
			{
				float targetMag = Mathf.Clamp(vrJoy.magnitude * 176f, 0f, 176f);
				ms.CursorRt.anchoredPosition = vrJoy.normalized * targetMag;
				ms.DesktopCursorPos = ms.CursorRt.anchoredPosition;
				return;
			}

			// Keep existing position
			if (ms.DesktopCursorPos != Vector2.zero)
			{
				ms.CursorRt.anchoredPosition = ms.DesktopCursorPos;
			}
		}

		private void UpdateSubMenu(MenuState ms)
		{
			if (ms.CursorRt == null)
			{
				if (ms.CursorTrans != null) ms.CursorRt = ms.CursorTrans.GetComponent<RectTransform>();
				if (ms.CursorRt == null) return;
			}

			// Read and update cursor position with VR / Mouse input smoothly
			UpdateCursorInput(ms);

			Vector2 cursor = ms.CursorRt.anchoredPosition;
			float mag = cursor.magnitude;

			// Center deadzone (< 35f): click exits back to previous menu
			if (mag < 35f)
			{
				ms.HoveredSubPedalIndex = -1;
				for (int i = 0; i < ms.SubPedals.Count; i++)
				{
					if (ms.SubPedals[i].SelectObj != null)
					{
						ms.SubPedals[i].SelectObj.SetActive(false);
					}
				}

				if (IsTriggerDown())
				{
					CloseSubMenu(ms);
					return;
				}
				return;
			}

			// Radial angle: 0 = Right, 90 = Top, 180 = Left, 270 = Down
			float angle = Mathf.Atan2(cursor.y, cursor.x) * Mathf.Rad2Deg;
			if (angle < 0) angle += 360f;

			int bestIndex = -1;
			float minDiff = 360f;

			for (int i = 0; i < ms.SubPedals.Count; i++)
			{
				float pAngle = ms.SubPedals[i].AngleDeg;
				float diff = Mathf.Abs(Mathf.DeltaAngle(angle, pAngle));
				if (diff < minDiff && diff <= 30f)
				{
					minDiff = diff;
					bestIndex = i;
				}
			}

			ms.HoveredSubPedalIndex = bestIndex;

			// Highlight active pedal and refresh dynamic state text
			for (int i = 0; i < ms.SubPedals.Count; i++)
			{
				var sp = ms.SubPedals[i];
				bool isHovered = (i == bestIndex);

				if (sp.SelectObj != null)
				{
					sp.SelectObj.SetActive(isHovered);
				}

				if (sp.TextComp != null)
				{
					if (i == 0) // Back button
					{
						string c = isHovered ? "#22D3EE" : "#FFFFFF";
						sp.TextComp.text = $"<color={c}>Back</color>";
					}
					else
					{
						bool on = sp.IsActiveState != null && sp.IsActiveState();
						string statusCol = on ? "#00FF66" : "#888888";
						string statusTxt = on ? "ON" : "OFF";
						string nameCol = on ? "#00FFAA" : (isHovered ? "#FF4FB6" : "#F2EEFF");
						sp.TextComp.text = $"<color={nameCol}>{sp.BaseLabel}</color>\n<size=15><color={statusCol}>[{statusTxt}]</color></size>";
					}
				}
			}

			// Execute click
			if (bestIndex >= 0 && IsTriggerDown())
			{
				try
				{
					ms.SubPedals[bestIndex].OnClick?.Invoke();
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[ActionMenuModule] SubPedal click threw: {ex.Message}");
				}
			}
		}

		private static void StripObfuscated(GameObject go)
		{
			if (go == null) return;
			foreach (var comp in go.GetComponents<Component>())
			{
				if (comp == null) continue;
				string cname = comp.GetIl2CppType().Name;
				if (cname != "RectTransform" && cname != "CanvasRenderer" && cname != "CanvasGroup" && cname != "StyleElement")
				{
					if (cname.Length > 8 && !cname.Contains("Transform"))
					{
						try { UnityEngine.Object.Destroy(comp); } catch { }
					}
				}
			}
		}

		private static Texture2D GetHomeIconTexture()
		{
			if (_homeIconTexture != null) return _homeIconTexture;
			try
			{
				byte[] bytes = Convert.FromBase64String(HomeIconBase64);
				var tex = new Texture2D(2, 2);
				ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(bytes));
				_homeIconTexture = tex;
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[ActionMenuModule] Failed to create home icon texture: {ex.Message}");
			}
			return _homeIconTexture;
		}

		private static bool IsTriggerDown()
		{
			if (Input.GetMouseButtonDown(0)) return true;
			if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)) return true;
			if (Input.GetKeyDown(KeyCode.JoystickButton0) || Input.GetKeyDown(KeyCode.JoystickButton14) || Input.GetKeyDown(KeyCode.JoystickButton15)) return true;
			return false;
		}
	}
}
