using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	public class NameplateEspModule : IModule
	{
		public override string Name => "NameplateESP";

		private const string EspMatName = "VA_EspMaterial";
		private static GameObject _cachedManager;
		private static float _lastCheckTime;
		private static bool _lastState;

		public override void OnInitialize()
		{
			if (ModConfig.NameplateEsp != null)
			{
				ModConfig.NameplateEsp.SettingChanged += (_, _) =>
				{
					ApplyNameplateEsp(ModConfig.NameplateEsp.Value);
				};
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_cachedManager = null;
			_lastState = false;
		}

		public override void OnUpdate()
		{
			bool want = ModConfig.NameplateEsp != null && ModConfig.NameplateEsp.Value;

			// If state changed or periodic refresh if active (to handle newly joined players / pooled nameplates)
			if (want != _lastState)
			{
				_lastState = want;
				ApplyNameplateEsp(want);
				_lastCheckTime = VaClock.Now;
			}
			else if (want && VaClock.Now - _lastCheckTime > 1.5f)
			{
				_lastCheckTime = VaClock.Now;
				ApplyNameplateEsp(true);
			}
		}

		public static void ApplyNameplateEsp(bool enable)
		{
			try
			{
				int ztest = enable ? 8 : 4; // 8 = CompareFunction.Always, 4 = CompareFunction.LEqual
				int queue = enable ? 4000 : 3000;

				// 1. Update all VRChat Nameplate materials in-place.
				// NEVER clone them with 'new Material()', because VRChat's NameplateManager updates
				// the camera billboard matrix on the original material instances every frame.
				// Modifying the ZTest on the live materials makes them ESP through walls while preserving
				// 100% accurate billboarding towards the camera and following the player!
				try
				{
					var allMats = Resources.FindObjectsOfTypeAll<Material>();
					if (allMats != null)
					{
						for (int i = 0; i < allMats.Count; i++)
						{
							var m = allMats[i];
							if (m == null || !NativeGuard.Alive(m)) continue;
							var s = m.shader;
							if (s == null) continue;

							string sName = s.name;
							if (sName == "VRChat/UI/Nameplates" || sName.IndexOf("Nameplate", StringComparison.OrdinalIgnoreCase) >= 0)
							{
								try { m.SetInt("unity_GUIZTestMode", ztest); } catch { }
								try { m.SetInt("_ZTestMode", ztest); } catch { }
								try { m.SetInt("_ZTest", ztest); } catch { }
								try { m.SetInt("_GUIZTestMode", ztest); } catch { }
								m.renderQueue = queue;
							}
						}
					}
				}
				catch { }

				var mgr = GetManager();
				if (mgr == null || !NativeGuard.Alive(mgr)) return;

				int childCount = mgr.transform.childCount;
				for (int i = 0; i < childCount; i++)
				{
					var container = mgr.transform.GetChild(i);
					if (container == null || !NativeGuard.Alive(container)) continue;

					// Safety check: ensure Canvas overrideSorting remains false (never detach from world coordinates)
					var canvas = container.GetComponentInChildren<Canvas>(true);
					if (canvas != null && NativeGuard.Alive(canvas) && canvas.overrideSorting)
					{
						try
						{
							canvas.overrideSorting = false;
							canvas.sortingOrder = 0;
						}
						catch { }
					}

					// 2. TextMeshProUGUI elements (Player Name, Group Name, Pronouns / Rank)
					var tmps = container.GetComponentsInChildren<TextMeshProUGUI>(true);
					if (tmps != null)
					{
						for (int j = 0; j < tmps.Length; j++)
						{
							var t = tmps[j];
							if (t == null || !NativeGuard.Alive(t)) continue;
							try
							{
								if (t.isOverlay != enable)
								{
									t.isOverlay = enable;
								}
							}
							catch { }
						}
					}

					// 3. Clean up any stale disconnected cloned materials from previous attempts,
					// and ensure graphics refresh their material dirty state
					var graphics = container.GetComponentsInChildren<Graphic>(true);
					if (graphics != null)
					{
						for (int j = 0; j < graphics.Length; j++)
						{
							var g = graphics[j];
							if (g == null || !NativeGuard.Alive(g)) continue;
							if (g is TextMeshProUGUI) continue;

							try
							{
								// If a disconnected clone was previously assigned, restore original template
								if (g.material != null && g.material.name.StartsWith(EspMatName, StringComparison.Ordinal))
								{
									Material orig = null;
									if (g.name == "Background") orig = FindTemplateMaterial("Nameplate_UI_Material_Background");
									else if (g.name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0) orig = FindTemplateMaterial("Nameplate_UI_Material_GroupBanner");
									else if (g.name == "Icon") orig = FindTemplateMaterial("Nameplate_UI_Material_Icon");
									else if (g.name == "Border") orig = FindTemplateMaterial("Nameplate_UI_Material_General");

									var oldMat = g.material;
									g.material = orig;
									g.SetMaterialDirty();
									try { UnityEngine.Object.Destroy(oldMat); } catch { }
								}

								// Also ensure the graphic's active material reflects the depth mode
								var gm = g.materialForRendering ?? g.material;
								if (gm != null && gm.shader != null && gm.shader.name == "VRChat/UI/Nameplates")
								{
									try { gm.SetInt("unity_GUIZTestMode", ztest); } catch { }
									try { gm.SetInt("_ZTestMode", ztest); } catch { }
									try { gm.SetInt("_ZTest", ztest); } catch { }
									try { gm.SetInt("_GUIZTestMode", ztest); } catch { }
									gm.renderQueue = queue;
									g.SetMaterialDirty();
								}
							}
							catch { }
						}
					}
				}
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[NameplateESP] Apply failed: {ex.Message}");
			}
		}

		private static Material FindTemplateMaterial(string matName)
		{
			try
			{
				var mats = Resources.FindObjectsOfTypeAll<Material>();
				if (mats != null)
				{
					for (int i = 0; i < mats.Count; i++)
					{
						var m = mats[i];
						if (m != null && m.name == matName) return m;
					}
				}
			}
			catch { }
			return null;
		}

		private static GameObject GetManager()
		{
			if (_cachedManager != null && NativeGuard.Alive(_cachedManager))
				return _cachedManager;

			try
			{
				_cachedManager = GameObject.Find("NameplateManager");
				if (_cachedManager != null && NativeGuard.Alive(_cachedManager))
					return _cachedManager;

				var all = Resources.FindObjectsOfTypeAll<GameObject>();
				for (int i = 0; i < all.Count; i++)
				{
					var g = all[i];
					if (g != null && g.name == "NameplateManager")
					{
						_cachedManager = g;
						return _cachedManager;
					}
				}
			}
			catch { }

			return null;
		}
	}
}


