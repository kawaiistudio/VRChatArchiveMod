using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// GLOBAL UDON INTERACT
	// Intercepts the local player's interactions with world buttons (uGUI Button.Press) and
	// interactables (UdonBehaviour.Interact), broadcasting the resulting custom events globally
	// to all players via SendCustomNetworkEvent(NetworkEventTarget.All, eventName) and taking
	// ownership of the target object so synced variables propagate to the whole instance.
	public class GlobalUdonModule : IModule
	{
		public override string Name => "GlobalUdon";

		private static bool _hooked;
		private static bool _userClickingButton;
		private static bool _userClickingToggle;
		private static bool _userInteracting;
		private static bool _inBroadcast;
		private static Button _lastPressedButton;
		private static Toggle _lastPressedToggle;
		private static VRC.Udon.UdonBehaviour _lastInteractedUdon;

		public override void OnInitialize()
		{
			Hook();
		}

		private static void Hook()
		{
			if (_hooked) return;
			try
			{
				var harm = VRChatArchiveModPlugin.HarmonyInstance;
				if (harm == null) return;

				// 1. Hook UnityEngine.UI.Button.Press
				try
				{
					var press = typeof(Button).GetMethod("Press",
						BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
					if (press != null)
					{
						harm.Patch(press,
							prefix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ButtonPressPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
							postfix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ButtonPressPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
					}
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] Button.Press patch failed: {ex.Message}");
				}

				// 2. Hook UnityEngine.UI.Toggle (InternalToggle, OnPointerClick, OnSubmit)
				try
				{
					var internalToggle = typeof(Toggle).GetMethod("InternalToggle",
						BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
					if (internalToggle != null)
					{
						harm.Patch(internalToggle,
							prefix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ToggleClickPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
							postfix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ToggleClickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
					}

					var toggleClick = typeof(Toggle).GetMethod("OnPointerClick",
						BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
					if (toggleClick != null)
					{
						harm.Patch(toggleClick,
							prefix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ToggleClickPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
							postfix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ToggleClickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
					}

					var toggleSubmit = typeof(Toggle).GetMethod("OnSubmit",
						BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
					if (toggleSubmit != null)
					{
						harm.Patch(toggleSubmit,
							prefix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ToggleClickPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
							postfix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(ToggleClickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
					}
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] Toggle patch failed: {ex.Message}");
				}

				// 3. Hook VRC.Udon.UdonBehaviour.Interact & SendCustomEvent
				Type ubType = Type.GetType("VRC.Udon.UdonBehaviour, VRC.Udon", false)
				              ?? FindType("VRC.Udon.UdonBehaviour");

				if (ubType != null)
				{
					// Hook Interact
					try
					{
						var interact = ubType.GetMethod("Interact", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
						if (interact != null)
						{
							harm.Patch(interact,
								prefix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(InteractPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
								postfix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(InteractPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
						}
					}
					catch (Exception ex)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] UdonBehaviour.Interact patch failed: {ex.Message}");
					}

					// Hook SendCustomEvent(string)
					try
					{
						var sendCustom = ubType.GetMethod("SendCustomEvent", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
						if (sendCustom != null)
						{
							harm.Patch(sendCustom,
								prefix: new HarmonyMethod(typeof(GlobalUdonModule).GetMethod(nameof(SendCustomEventPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
						}
					}
					catch (Exception ex)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] UdonBehaviour.SendCustomEvent patch failed: {ex.Message}");
					}
				}

				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[GlobalUdon] armed — Button.Press, Toggle.InternalToggle/Click, UdonBehaviour.Interact & SendCustomEvent hooked.");
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[GlobalUdon] Hook failed: {ex}");
			}
		}

		private static bool IsWorldButton(Button btn)
		{
			if (btn == null) return false;
			try
			{
				var go = btn.gameObject;
				if (go == null) return false;
				var scene = go.scene;
				if (!scene.IsValid() || !scene.isLoaded || scene.name == "DontDestroyOnLoad") return false;

				var root = go.transform.root;
				if (root == null) return false;
				string rootName = root.name;
				if (rootName == "UserInterface"
				 || rootName.StartsWith("Canvas_QuickMenu", StringComparison.OrdinalIgnoreCase)
				 || rootName.StartsWith("VRChatArchiveMod", StringComparison.OrdinalIgnoreCase)
				 || rootName.StartsWith("ActionMenu", StringComparison.OrdinalIgnoreCase)
				 || rootName.StartsWith("TrackingVolume", StringComparison.OrdinalIgnoreCase)
				 || rootName.Equals("Cohtml", StringComparison.OrdinalIgnoreCase))
					return false;

				return true;
			}
			catch { return false; }
		}

		private static bool IsWorldToggle(Toggle tog)
		{
			if (tog == null) return false;
			try
			{
				var go = tog.gameObject;
				if (go == null) return false;
				var scene = go.scene;
				if (!scene.IsValid() || !scene.isLoaded || scene.name == "DontDestroyOnLoad") return false;

				var root = go.transform.root;
				if (root == null) return false;
				string rootName = root.name;
				if (rootName == "UserInterface"
				 || rootName.StartsWith("Canvas_QuickMenu", StringComparison.OrdinalIgnoreCase)
				 || rootName.StartsWith("VRChatArchiveMod", StringComparison.OrdinalIgnoreCase)
				 || rootName.StartsWith("ActionMenu", StringComparison.OrdinalIgnoreCase)
				 || rootName.StartsWith("TrackingVolume", StringComparison.OrdinalIgnoreCase)
				 || rootName.Equals("Cohtml", StringComparison.OrdinalIgnoreCase))
					return false;

				return true;
			}
			catch { return false; }
		}

		private static void ButtonPressPrefix(Button __instance)
		{
			if (__instance == null || ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value) return;
			if (IsWorldButton(__instance))
			{
				_userClickingButton = true;
				_lastPressedButton = __instance;

				if (IsGroupOrMenuAction(null, __instance.name))
				{
					EnsureMainMenuRootPage();
				}
			}
		}

		private static void ButtonPressPostfix(Button __instance)
		{
			if (__instance == null || ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value)
			{
				_userClickingButton = false;
				_lastPressedButton = null;
				return;
			}

			if (_userClickingButton && _lastPressedButton == __instance)
			{
				try
				{
					UdonManagerModule.TakeOwnershipDirect(__instance.gameObject);

					bool triggeredEvent = false;
					var ev = __instance.onClick;
					if (ev != null)
					{
						int n = ev.GetPersistentEventCount();
						for (int i = 0; i < n; i++)
						{
							var target = ev.GetPersistentTarget(i);
							if (target == null) continue;
							var ub = target.TryCast<VRC.Udon.UdonBehaviour>();
							if (ub != null)
							{
								UdonManagerModule.TakeOwnershipDirect(ub.gameObject);
								try { ub.RequestSerialization(); } catch { }

								string mName = ev.GetPersistentMethodName(i);
								if (!string.IsNullOrEmpty(mName) && mName != "SendCustomEvent" && mName[0] != '_')
								{
									triggeredEvent = UdonManagerModule.RunGlobalDirect(ub, mName) || triggeredEvent;
								}
							}
						}
					}

					// Show toast feedback if not already broadcasted via SendCustomEvent
					if (!triggeredEvent)
					{
						Toast.Show($"[Global Udon] '{__instance.gameObject.name}' applied to ALL!");
						VRChatArchiveModPlugin.Logger.LogInfo($"[GlobalUdon] World Button '{__instance.gameObject.name}' triggered globally.");
					}
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] ButtonPressPostfix error: {ex.Message}");
				}
			}

			_userClickingButton = false;
			_lastPressedButton = null;
		}

		private static void ToggleClickPrefix(Toggle __instance)
		{
			if (__instance == null || ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value) return;
			if (IsWorldToggle(__instance))
			{
				_userClickingToggle = true;
				_lastPressedToggle = __instance;

				if (IsGroupOrMenuAction(null, __instance.name))
				{
					EnsureMainMenuRootPage();
				}
			}
		}

		private static void ToggleClickPostfix(Toggle __instance)
		{
			if (__instance == null || ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value)
			{
				_userClickingToggle = false;
				_lastPressedToggle = null;
				return;
			}

			if (_userClickingToggle && _lastPressedToggle == __instance)
			{
				try
				{
					// 1. Take ownership of the toggle object
					UdonManagerModule.TakeOwnershipDirect(__instance.gameObject);

					var ownUb = __instance.GetComponent<VRC.Udon.UdonBehaviour>();
					if (ownUb != null)
					{
						UdonManagerModule.TakeOwnershipDirect(ownUb.gameObject);
						try { ownUb.RequestSerialization(); } catch { }
					}

					// 2. Take ownership of all persistent targets on onValueChanged
					var ev = __instance.onValueChanged;
					if (ev != null)
					{
						int n = ev.GetPersistentEventCount();
						for (int i = 0; i < n; i++)
						{
							var target = ev.GetPersistentTarget(i);
							if (target == null) continue;
							var ub = target.TryCast<VRC.Udon.UdonBehaviour>();
							if (ub != null)
							{
								UdonManagerModule.TakeOwnershipDirect(ub.gameObject);
								try { ub.RequestSerialization(); } catch { }

								string mName = ev.GetPersistentMethodName(i);
								if (!string.IsNullOrEmpty(mName) && mName != "SendCustomEvent" && mName[0] != '_')
								{
									UdonManagerModule.RunGlobalDirect(ub, mName);
								}
							}
						}
					}

					// 3. Find any world game object matching this toggle's name (e.g. "8ball") and take ownership
					string togName = __instance.gameObject.name;
					try
					{
						var matchingRoot = GameObject.Find(togName);
						if (matchingRoot != null && matchingRoot != __instance.gameObject)
						{
							UdonManagerModule.TakeOwnershipDirect(matchingRoot);
							var ubs = matchingRoot.GetComponentsInChildren<VRC.Udon.UdonBehaviour>(true);
							if (ubs != null)
							{
								foreach (var subUb in ubs)
								{
									if (subUb != null)
									{
										UdonManagerModule.TakeOwnershipDirect(subUb.gameObject);
										try { subUb.RequestSerialization(); } catch { }
									}
								}
							}
						}
					}
					catch { }

					// 4. Always show user feedback!
					string stateStr = __instance.isOn ? "ON" : "OFF";
					Toast.Show($"[Global Udon] '{togName}' ({stateStr}) applied to ALL!");
					VRChatArchiveModPlugin.Logger.LogInfo($"[GlobalUdon] World Toggle '{togName}' -> {stateStr} synchronized globally.");
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] ToggleClickPostfix error: {ex.Message}");
				}
			}

			_userClickingToggle = false;
			_lastPressedToggle = null;
		}

		private static void InteractPrefix(VRC.Udon.UdonBehaviour __instance)
		{
			if (__instance == null || ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value) return;
			_userInteracting = true;
			_lastInteractedUdon = __instance;

			try
			{
				if (IsGroupOrMenuAction(__instance, null))
				{
					EnsureMainMenuRootPage();
				}

				UdonManagerModule.TakeOwnershipDirect(__instance.gameObject);
			}
			catch { }
		}

		private static void InteractPostfix(VRC.Udon.UdonBehaviour __instance)
		{
			if (__instance == null || ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value)
			{
				_userInteracting = false;
				_lastInteractedUdon = null;
				return;
			}

			try
			{
				try { __instance.RequestSerialization(); } catch { }
			}
			catch { }

			_userInteracting = false;
			_lastInteractedUdon = null;
		}

		private static bool IsGroupOrMenuAction(VRC.Udon.UdonBehaviour ub, string evName)
		{
			if (!string.IsNullOrEmpty(evName))
			{
				if (evName.IndexOf("Group", StringComparison.OrdinalIgnoreCase) >= 0
				 || evName.IndexOf("Page", StringComparison.OrdinalIgnoreCase) >= 0
				 || evName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0
				 || evName.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			}

			if (ub != null)
			{
				string n = ub.name;
				if (n.IndexOf("Group", StringComparison.OrdinalIgnoreCase) >= 0
				 || n.IndexOf("Page", StringComparison.OrdinalIgnoreCase) >= 0
				 || n.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
					return true;

				var p = ub.transform.parent;
				while (p != null)
				{
					if (p.name.IndexOf("Group", StringComparison.OrdinalIgnoreCase) >= 0
					 || p.name.IndexOf("Page", StringComparison.OrdinalIgnoreCase) >= 0)
						return true;
					p = p.parent;
				}
			}

			return false;
		}

		private static bool SendCustomEventPrefix(VRC.Udon.UdonBehaviour __instance, string __0)
		{
			try
			{
				if (ModConfig.GlobalUdonInteract == null || !ModConfig.GlobalUdonInteract.Value) return true;
				if (_inBroadcast) return true; // Prevent recursion
				if (__instance == null) return true;
				if (string.IsNullOrEmpty(__0)) return true;

				// ONLY act if this event was triggered by the local player actively clicking a world button, toggle, or interacting
				if (!_userClickingButton && !_userInteracting && !_userClickingToggle) return true;

				// If event begins with '_', it cannot be sent via SendCustomNetworkEvent (VRChat blocks it),
				// but we STILL claim ownership and serialize so variables synchronize to all players!
				if (__0[0] == '_')
				{
					UdonManagerModule.TakeOwnershipDirect(__instance.gameObject);
					try { __instance.RequestSerialization(); } catch { }
					VRChatArchiveModPlugin.Logger.LogInfo($"[GlobalUdon] Synced internal event '{__0}' on '{__instance.gameObject.name}' via ownership & serialization.");
					return true;
				}

				// If opening a group or menu page, ensure Main Menu is switched to a root page first
				// to prevent VRChat's "Unexpected PushPage before SwitchToRootPage" crash!
				if (IsGroupOrMenuAction(__instance, __0))
				{
					EnsureMainMenuRootPage();
				}

				bool broadcasted = false;
				_inBroadcast = true;
				try
				{
					// 1. Take ownership of the target object
					UdonManagerModule.TakeOwnershipDirect(__instance.gameObject);

					// 2. Broadcast event globally to ALL players
					broadcasted = UdonManagerModule.RunGlobalDirect(__instance, __0);

					// 3. Request serialization
					try { __instance.RequestSerialization(); } catch { }

					if (broadcasted)
					{
						Toast.Show($"[Global Udon] Sent '{__0}' to ALL!");
						VRChatArchiveModPlugin.Logger.LogInfo($"[GlobalUdon] Broadcasted event '{__0}' on '{__instance.gameObject.name}' globally!");
					}
				}
				finally
				{
					_inBroadcast = false;
					// Consume the user interaction so nested event calls don't re-broadcast
					_userClickingButton = false;
					_userInteracting = false;
					_userClickingToggle = false;
				}
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] SendCustomEventPrefix failed: {ex.Message}");
			}

			// Always return true so the user's client executes the event locally while others receive the network broadcast
			return true;
		}

		private static void EnsureMainMenuRootPage()
		{
			try
			{
				var mainRoot = Core.QuickMenu.Main();
				if (mainRoot == null) return;
				Transform strip = mainRoot.Find("Container/PageButtons/HorizontalLayoutGroup");
				if (strip == null) return;

				bool anyActive = false;
				for (int i = 0; i < strip.childCount; i++)
				{
					var tab = strip.GetChild(i);
					if (tab == null) continue;
					var rt = tab.GetComponent<RectTransform>();
					if (rt != null && rt.sizeDelta.y > 125f)
					{
						anyActive = true;
						break;
					}
				}

				if (!anyActive)
				{
					var tabBtn = strip.Find("Groups_Button_Tab") ?? strip.Find("Worlds_Button_Tab");
					if (tabBtn != null)
					{
						var btn = tabBtn.GetComponent<Button>() ?? tabBtn.GetComponentInChildren<Button>(true);
						if (btn != null && btn.onClick != null)
						{
							btn.onClick.Invoke();
							VRChatArchiveModPlugin.Logger.LogInfo("[GlobalUdon] Activated Groups_Button_Tab to prime MainMenu before pushing page.");
						}
					}
				}
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[GlobalUdon] EnsureMainMenuRootPage error: {ex.Message}");
			}
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { var t = asm.GetType(full, false); if (t != null) return t; } catch { }
			}
			return null;
		}
	}
}
