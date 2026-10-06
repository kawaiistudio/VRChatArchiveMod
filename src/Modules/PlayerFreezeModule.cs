using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	/// <summary>
	/// Freezes remote players locally:
	/// 1. Drops inbound network events (motion, avatar parameters, voice) for their Photon actor number.
	/// 2. Pins their root Transform position and rotation in LateUpdate to prevent drift / dead-reckoning.
	/// 3. Pauses their Avatar Animator (speed = 0) so all bone motion and idle cycles stop dead in their tracks.
	/// 4. Renders a sleek frost-badge / snowflake indicator under their nameplate avatar icon.
	/// </summary>
	public class PlayerFreezeModule : IModule
	{
		public override string Name => "PlayerFreeze";

		public sealed class FrozenPlayer
		{
			public string UserId;
			public string Name;
			public int ActorId = -1;
			public Vector3 Position;
			public Quaternion Rotation;
			public Transform Transform;
			public Animator Animator;
			public float OriginalSpeed = 1f;
			public bool DropNetwork = true;
			public bool PauseAnimator = true;
			public bool PinTransform = true;
		}

		private static readonly Dictionary<string, FrozenPlayer> _frozenByUid =
			new Dictionary<string, FrozenPlayer>(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<int> _frozenActors = new HashSet<int>();
		private static readonly Dictionary<string, GameObject> _badges =
			new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
		private static readonly object _gate = new object();
		private static Sprite _badgeSprite;
		private static float _nextBadgeCheck;

		public static bool IsActorFrozen(int actorId)
		{
			if (actorId <= 0) return false;
			lock (_gate) return _frozenActors.Contains(actorId);
		}

		public static bool IsUserFrozen(string userId)
		{
			if (string.IsNullOrEmpty(userId)) return false;
			lock (_gate) return _frozenByUid.ContainsKey(userId);
		}

		public static IReadOnlyCollection<FrozenPlayer> AllFrozen
		{
			get
			{
				lock (_gate) return new List<FrozenPlayer>(_frozenByUid.Values);
			}
		}

		public override void OnInitialize()
		{
			try
			{
				if (ModConfig.FreezeBadgeY != null && (ModConfig.FreezeBadgeY.Value < 250f || ModConfig.FreezeBadgeY.Value > 550f))
				{
					ModConfig.FreezeBadgeY.Value = 385f;
				}
				if (ModConfig.FreezeBadgeX != null && (ModConfig.FreezeBadgeX.Value < -80f || ModConfig.FreezeBadgeX.Value > 80f))
				{
					ModConfig.FreezeBadgeX.Value = 0f;
				}
				if (ModConfig.FreezeBadgeSize != null && (ModConfig.FreezeBadgeSize.Value < 40f || ModConfig.FreezeBadgeSize.Value > 160f))
				{
					ModConfig.FreezeBadgeSize.Value = 90f;
				}
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			lock (_gate)
			{
				_frozenByUid.Clear();
				_frozenActors.Clear();
				_badges.Clear();
			}
		}

		public static void Toggle(VaTagsModule.PlayerEntry entry)
		{
			if (entry == null) return;
			string uid = entry.UserId;
			if (string.IsNullOrEmpty(uid)) uid = entry.Name ?? "unknown";

			if (IsUserFrozen(uid))
				Unfreeze(uid);
			else
				Freeze(entry);
		}

		public static bool Freeze(VaTagsModule.PlayerEntry entry, bool dropNet = true, bool pauseAnim = true, bool pinTr = true)
		{
			if (entry == null) return false;
			string uid = entry.UserId;
			if (string.IsNullOrEmpty(uid)) uid = entry.Name ?? "unknown";

			lock (_gate)
			{
				Vector3 pos = entry.Transform != null ? entry.Transform.position : entry.Pos;
				Quaternion rot = entry.Transform != null ? entry.Transform.rotation : Quaternion.identity;

				var fp = new FrozenPlayer
				{
					UserId = uid,
					Name = entry.Name ?? "Player",
					ActorId = entry.PlayerId,
					Transform = entry.Transform,
					Position = pos,
					Rotation = rot,
					DropNetwork = dropNet,
					PauseAnimator = pauseAnim,
					PinTransform = pinTr,
				};

				if (fp.Transform != null)
				{
					try
					{
						var anim = fp.Transform.GetComponentInChildren<Animator>(true);
						if (anim != null)
						{
							fp.Animator = anim;
							fp.OriginalSpeed = anim.speed;
							if (pauseAnim)
							{
								anim.speed = 0f;
							}
						}
					}
					catch { }
				}

				_frozenByUid[uid] = fp;
				if (dropNet && fp.ActorId > 0)
				{
					_frozenActors.Add(fp.ActorId);
				}

				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[PlayerFreeze] FROZEN {fp.Name} (uid={fp.UserId}, actor={fp.ActorId}, dropNet={dropNet}, pauseAnim={pauseAnim}, pinTr={pinTr}) at {fp.Position}");
				try { Toast.Show($"Frozen: {fp.Name}"); } catch { }

				// Immediately trigger badge display on their Nameplate
				UpdateBadgeFor(entry, true);
				return true;
			}
		}

		public static bool Unfreeze(string userId)
		{
			if (string.IsNullOrEmpty(userId)) return false;

			lock (_gate)
			{
				if (_frozenByUid.TryGetValue(userId, out var fp))
				{
					if (fp.Animator != null)
					{
						try
						{
							fp.Animator.speed = fp.OriginalSpeed > 0f ? fp.OriginalSpeed : 1f;
						}
						catch { }
					}

					if (fp.ActorId > 0)
					{
						_frozenActors.Remove(fp.ActorId);
					}

					_frozenByUid.Remove(userId);

					// Hide badge on Nameplate
					if (_badges.TryGetValue(userId, out var b) && b != null)
					{
						try { b.SetActive(false); } catch { }
					}

					VRChatArchiveModPlugin.Logger.LogInfo($"[PlayerFreeze] UNFROZEN {fp.Name} (uid={fp.UserId})");
					try { Toast.Show($"Unfrozen: {fp.Name}"); } catch { }
					return true;
				}
			}
			return false;
		}

		public override void OnUpdate()
		{
			float now = VaClock.Now;
			if (now < _nextBadgeCheck) return;
			_nextBadgeCheck = now + 0.5f;

			try
			{
				lock (_gate)
				{
					if (_frozenByUid.Count == 0 && _badges.Count == 0) return;

					// Scan roster to update badges for all frozen players
					lock (VaTagsModule.Roster)
					{
						foreach (var entry in VaTagsModule.Roster)
						{
							if (entry == null || string.IsNullOrEmpty(entry.UserId)) continue;
							bool frozen = _frozenByUid.ContainsKey(entry.UserId);
							if (frozen || _badges.ContainsKey(entry.UserId))
							{
								UpdateBadgeFor(entry, frozen);
							}
						}
					}
				}
			}
			catch { }
		}

		public override void OnLateUpdate()
		{
			if (_frozenByUid.Count == 0) return;

			lock (_gate)
			{
				foreach (var kv in _frozenByUid)
				{
					var fp = kv.Value;
					if (fp == null) continue;

					// 1. Pin Transform
					if (fp.PinTransform && fp.Transform != null)
					{
						try
						{
							fp.Transform.position = fp.Position;
							fp.Transform.rotation = fp.Rotation;
						}
						catch { }
					}

					// 2. Enforce Animator Pause
					if (fp.PauseAnimator && fp.Animator != null)
					{
						try
						{
							if (fp.Animator.speed != 0f)
							{
								fp.Animator.speed = 0f;
							}
						}
						catch { }
					}
				}
			}
		}

		// ---------------------------------------------------------------- Nameplate Freeze Badge

		public static Sprite GetBadgeSprite()
		{
			if (_badgeSprite != null) return _badgeSprite;
			try
			{
				const int W = 64;
				const int H = 64;
				const float radius = 28f;
				const float borderThick = 4.0f;

				var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;

				var px = new Color[W * H];
				Color bgColor = new Color(0.04f, 0.08f, 0.16f, 0.92f); // deep dark frosted blue
				Color borderColor = new Color(0.22f, 0.74f, 0.97f, 1.0f); // bright cyan / ice blue (#38bdf8)

				float halfW = (W - 1) * 0.5f;
				float halfH = (H - 1) * 0.5f;

				for (int y = 0; y < H; y++)
				{
					for (int x = 0; x < W; x++)
					{
						float dx = x - halfW;
						float dy = y - halfH;
						float dist = Mathf.Sqrt(dx * dx + dy * dy);

						if (dist > radius)
						{
							float edgeAlpha = Mathf.Clamp01(1f - (dist - radius));
							px[y * W + x] = new Color(borderColor.r, borderColor.g, borderColor.b, borderColor.a * edgeAlpha);
						}
						else if (dist > radius - borderThick)
						{
							float t = (dist - (radius - borderThick)) / borderThick;
							Color c = Color.Lerp(bgColor, borderColor, t);
							px[y * W + x] = c;
						}
						else
						{
							px[y * W + x] = bgColor;
						}
					}
				}

				tex.SetPixels(px);
				tex.Apply(false, false);
				_badgeSprite = Sprite.Create(tex, new Rect(0f, 0f, W, H), new Vector2(0.5f, 0.5f));
				if (_badgeSprite != null) _badgeSprite.hideFlags = HideFlags.HideAndDontSave;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[PlayerFreeze] badge sprite failed: {e.Message}");
			}
			return _badgeSprite;
		}

		private static void UpdateBadgeFor(VaTagsModule.PlayerEntry entry, bool isFrozen)
		{
			if (entry == null || string.IsNullOrEmpty(entry.UserId)) return;
			string uid = entry.UserId;

			try
			{
				bool badgeEnabled = ModConfig.FreezeBadgeEnabled?.Value ?? true;
				if (!badgeEnabled)
				{
					if (_badges.TryGetValue(uid, out var b) && b != null) b.SetActive(false);
					return;
				}

				if (isFrozen)
				{
					float sz = (ModConfig.FreezeBadgeSize != null && ModConfig.FreezeBadgeSize.Value > 0f) ? ModConfig.FreezeBadgeSize.Value : 90f;
					float posX = ModConfig.FreezeBadgeX?.Value ?? 0f;
					float posY = ModConfig.FreezeBadgeY?.Value ?? 385f;

					// If badge already exists and is alive, keep active and update position/size live
					if (_badges.TryGetValue(uid, out var existing) && existing != null && NativeGuard.Alive(existing))
					{
						if (!existing.activeSelf) existing.SetActive(true);
						existing.transform.localPosition = new Vector3(posX, posY, 0f);

						float pHeight = Mathf.Max(64f, sz * 0.82f);
						Transform ePill = FewTagsModule.FindDeep(existing.transform, "GroupPill");
						if (ePill != null)
						{
							var le = ePill.GetComponent<UnityEngine.UI.LayoutElement>();
							if (le != null)
							{
								le.minWidth = sz;
								le.preferredWidth = sz;
								le.minHeight = pHeight;
								le.preferredHeight = pHeight;
							}
							var pRt = ePill.GetComponent<RectTransform>();
							if (pRt != null)
							{
								pRt.sizeDelta = new Vector2(sz, pHeight);
							}
						}
						var eTmp = existing.GetComponentInChildren<TextMeshProUGUI>(true);
						if (eTmp != null)
						{
							eTmp.fontSize = sz * 0.65f;
						}
						return;
					}

					// 1. Resolve native plate template (ExpandedInfo under NameplateFragment)
					// Cloned native objects inherit VRChat's camera billboarding and dynamic distance scaling for free!
					GameObject quickStats = null;
					Transform contents = null;

					if (entry.Player != null)
					{
						FewTagsModule.ResolveNameplate(entry.Player, out quickStats, out contents);
					}

					if (quickStats == null || contents == null)
					{
						GameObject container = FindNameplateContainerFor(entry);
						if (container != null && NativeGuard.Alive(container))
						{
							CleanStaleBadges(container);
							Transform panel = container.transform.Find("PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/ExpandedInfo")
								?? container.transform.Find("PlayerNameplate/Canvas/NameplateGroup/Nameplate/Contents/Quick Stats");
							if (panel != null)
							{
								quickStats = panel.gameObject;
								contents = panel.parent;
							}
						}
					}
					else if (contents.parent != null)
					{
						CleanStaleBadges(contents.parent.gameObject);
					}

					if (quickStats != null && contents != null && NativeGuard.Alive(quickStats) && NativeGuard.Alive(contents))
					{
						// Clean old clone under contents if any
						Transform oldPlate = contents.Find("VA_FreezePlate");
						if (oldPlate != null && NativeGuard.Alive(oldPlate))
						{
							try { UnityEngine.Object.Destroy(oldPlate.gameObject); } catch { }
						}

						GameObject freezePlate = MakeFreezePlate(quickStats, contents, posX, posY, sz);
						if (freezePlate != null)
						{
							_badges[uid] = freezePlate;
							return;
						}
					}

					// Fallback: standalone ad-hoc badge under NameplateGroup if native clone couldn't resolve
					GameObject fbContainer = FindNameplateContainerFor(entry);
					if (fbContainer == null || !NativeGuard.Alive(fbContainer)) return;
					CleanStaleBadges(fbContainer);

					Transform targetParent = fbContainer.transform.Find("PlayerNameplate/Canvas/NameplateGroup")
						?? fbContainer.transform.Find("PlayerNameplate/Canvas/NameplateFragment")
						?? fbContainer.transform.Find("PlayerNameplate/Canvas/Nameplate");
					if (targetParent == null || !NativeGuard.Alive(targetParent)) return;

					GameObject badgeGo = new GameObject("VA_FreezeBadge", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					badgeGo.transform.SetParent(targetParent, false);

					var rt = badgeGo.GetComponent<RectTransform>();
					rt.anchorMin = new Vector2(0.5f, 0f);
					rt.anchorMax = new Vector2(0.5f, 0f);
					rt.pivot = new Vector2(0.5f, 0.5f);
					rt.sizeDelta = new Vector2(sz, sz);
					rt.localScale = Vector3.one;
					rt.localPosition = new Vector3(posX, (posY > 250f ? 150f : posY), 0f);

					var leFallback = badgeGo.AddComponent<LayoutElement>();
					leFallback.ignoreLayout = true;

					var cg = badgeGo.AddComponent<CanvasGroup>();
					cg.alpha = 1f;
					cg.blocksRaycasts = false;
					cg.interactable = false;

					badgeGo.AddComponent<CanvasRenderer>();
					var img = badgeGo.AddComponent<Image>();
					img.sprite = GetBadgeSprite();
					img.type = Image.Type.Simple;
					img.color = Color.white;
					img.raycastTarget = false;

					var iconGo = new GameObject("Icon", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
					iconGo.transform.SetParent(badgeGo.transform, false);

					var iconSubRt = iconGo.GetComponent<RectTransform>();
					iconSubRt.anchorMin = Vector2.zero;
					iconSubRt.anchorMax = Vector2.one;
					iconSubRt.pivot = new Vector2(0.5f, 0.5f);
					iconSubRt.offsetMin = Vector2.zero;
					iconSubRt.offsetMax = Vector2.zero;
					iconSubRt.localScale = Vector3.one;

					iconGo.AddComponent<CanvasRenderer>();
					var tmpFallback = iconGo.AddComponent<TextMeshProUGUI>();
					var stolenFont = fbContainer.GetComponentInChildren<TMP_Text>(true)?.font ?? MenuCard.StealFont();
					if (stolenFont != null) tmpFallback.font = stolenFont;
					tmpFallback.richText = true;
					tmpFallback.text = "<color=#E0F2FE><b>❄</b></color>";
					tmpFallback.fontSize = sz * 0.62f;
					tmpFallback.alignment = TextAlignmentOptions.Center;
					tmpFallback.raycastTarget = false;

					badgeGo.transform.SetAsLastSibling();
					badgeGo.SetActive(true);
					_badges[uid] = badgeGo;
				}
				else
				{
					if (_badges.TryGetValue(uid, out var b) && b != null && NativeGuard.Alive(b))
					{
						if (b.activeSelf) b.SetActive(false);
					}
				}
			}
			catch { }
		}

		private static GameObject MakeFreezePlate(GameObject quickStats, Transform contents, float x, float y, float size)
		{
			try
			{
				// Use FewTagsModule.MakePlate which properly configures TMP, native VRChat billboard materials, and layout alignment
				string badgeText = "<color=#38bdf8><b>❄</b></color>";
				GameObject clone = FewTagsModule.MakePlate(quickStats, contents, y, badgeText, hideBackground: false);
				if (clone == null) return null;
				clone.name = "VA_FreezePlate";

				clone.transform.localPosition = new Vector3(x, y, 0f);
				clone.transform.localRotation = Quaternion.identity;
				clone.transform.localScale = Vector3.one;

				// Disable CanvasGroup so it never fades when selection closes
				var cg = clone.GetComponent<CanvasGroup>();
				if (cg != null) cg.enabled = false;

				// Center HorizontalLayoutGroup on clone
				var cloneHlg = clone.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
				if (cloneHlg != null)
				{
					cloneHlg.childAlignment = TextAnchor.MiddleCenter;
					cloneHlg.childControlWidth = false;
					cloneHlg.childControlHeight = false;
					cloneHlg.childForceExpandWidth = false;
					cloneHlg.childForceExpandHeight = false;
				}

				float pillHeight = Mathf.Max(64f, size * 0.82f);

				// Configure GroupPill as a neat prominent pill
				Transform pill = FewTagsModule.FindDeep(clone.transform, "GroupPill");
				if (pill != null)
				{
					pill.gameObject.SetActive(true);

					Transform groupIcon = FewTagsModule.FindDeep(pill, "Group Icon");
					if (groupIcon != null) groupIcon.gameObject.SetActive(false);

					var le = pill.GetComponent<UnityEngine.UI.LayoutElement>() ?? pill.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
					le.ignoreLayout = false;
					le.minWidth = size;
					le.preferredWidth = size;
					le.minHeight = pillHeight;
					le.preferredHeight = pillHeight;

					var pillRt = pill.GetComponent<RectTransform>();
					if (pillRt != null)
					{
						pillRt.sizeDelta = new Vector2(size, pillHeight);
					}

					var pillHlg = pill.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
					if (pillHlg != null)
					{
						pillHlg.childAlignment = TextAnchor.MiddleCenter;
					}
				}

				// Ensure TMP label is centered and large
				var tmp = FewTagsModule.LastPlateLabel;
				if (tmp != null)
				{
					tmp.fontSize = size * 0.65f;
					tmp.text = "<color=#38bdf8><b>❄</b></color>";
					tmp.alignment = TextAlignmentOptions.Center;
					tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
					try { tmp.isOverlay = ModConfig.NameplateEsp != null && ModConfig.NameplateEsp.Value; } catch { }
				}

				clone.SetActive(true);
				return clone;
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[PlayerFreeze] MakeFreezePlate threw: {ex.Message}");
				return null;
			}
		}


		private static void CleanStaleBadges(GameObject container)
		{
			if (container == null) return;
			try
			{
				string[] stale = {
					"PlayerNameplate/Canvas/NameplateGroup/VA_FreezeBadge",
					"PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/Background/VA_FreezeBadge",
					"PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/ExpandedInfo/VA_FreezeBadge",
					"PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/VA_FreezeBadge",
					"PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/VA_FreezePlate",
					"PlayerNameplate/Canvas/Nameplate/Contents/Quick Stats/VA_FreezeBadge",
					"PlayerNameplate/Canvas/Nameplate/Contents/VA_FreezePlate"
				};
				for (int i = 0; i < stale.Length; i++)
				{
					Transform t = container.transform.Find(stale[i]);
					if (t != null && NativeGuard.Alive(t))
					{
						try { UnityEngine.Object.Destroy(t.gameObject); } catch { }
					}
				}
			}
			catch { }
		}


		private static GameObject FindNameplateContainerFor(VaTagsModule.PlayerEntry entry)
		{
			if (entry == null) return null;

			// 1. Standard FewTags lookup
			if (entry.Player != null)
			{
				try
				{
					var c = FewTagsModule.FindNameplateContainer(entry.Player);
					if (c != null && NativeGuard.Alive(c))
					{
						if (string.IsNullOrEmpty(entry.Name) || ShowsName(c, entry.Name))
							return c;
					}
				}
				catch { }
			}

			// 2. Scan NameplateManager children by Name and Proximity
			try
			{
				var all = Resources.FindObjectsOfTypeAll<GameObject>();
				GameObject mgr = null;
				for (int i = 0; i < all.Count; i++)
				{
					var g = all[i];
					if (g != null && g.name == "NameplateManager")
					{
						mgr = g;
						break;
					}
				}

				if (mgr != null && NativeGuard.Alive(mgr))
				{
					Vector3 playerPos = entry.Transform != null ? entry.Transform.position : entry.Pos;
					GameObject nameMatch = null;
					GameObject posMatch = null;
					float bestDist = 0.6f;

					int n = mgr.transform.childCount;
					for (int i = 0; i < n; i++)
					{
						var ch = mgr.transform.GetChild(i);
						if (ch == null || !NativeGuard.Alive(ch)) continue;
						var go = ch.gameObject;
						if (go == null || !NativeGuard.Alive(go) || go.name != "NameplateContainer") continue;

						if (!string.IsNullOrEmpty(entry.Name) && ShowsName(go, entry.Name))
						{
							nameMatch = go;
							break;
						}

						if (playerPos != Vector3.zero)
						{
							float dx = go.transform.position.x - playerPos.x;
							float dz = go.transform.position.z - playerPos.z;
							float dist = Mathf.Sqrt(dx * dx + dz * dz);
							if (dist < bestDist)
							{
								bestDist = dist;
								posMatch = go;
							}
						}
					}

					if (nameMatch != null) return nameMatch;
					if (posMatch != null) return posMatch;
				}
			}
			catch { }

			// 3. Fallback: direct player transform child
			if (entry.Transform != null && NativeGuard.Alive(entry.Transform))
			{
				try
				{
					var np = entry.Transform.Find("NameplateContainer") ?? entry.Transform.Find("PlayerNameplate");
					if (np != null && NativeGuard.Alive(np)) return np.gameObject;
				}
				catch { }
			}

			return null;
		}

		private static bool ShowsName(GameObject container, string want)
		{
			if (container == null || string.IsNullOrEmpty(want)) return false;
			try
			{
				var texts = container.GetComponentsInChildren<TMP_Text>(true);
				if (texts == null) return false;
				for (int i = 0; i < texts.Length; i++)
				{
					var t = texts[i];
					if (t == null) continue;
					string s2;
					try { s2 = t.text; } catch { continue; }
					if (string.IsNullOrEmpty(s2)) continue;
					if (string.Equals(s2, want, StringComparison.Ordinal) ||
					    string.Equals(StripRichText(s2), want, StringComparison.Ordinal))
						return true;
				}
			}
			catch { }
			return false;
		}

		private static string StripRichText(string s)
		{
			if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s;
			var sb = new System.Text.StringBuilder(s.Length);
			bool inside = false;
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (c == '<') { inside = true; continue; }
				if (c == '>') { inside = false; continue; }
				if (!inside) sb.Append(c);
			}
			return sb.ToString().Trim();
		}
	}
}
