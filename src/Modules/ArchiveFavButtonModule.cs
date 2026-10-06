using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ADD TO ARCHIVE FAVORITES — our own button, next to VRChat's star.
	//
	// Deliberately NOT a hook on VRChat's favourite button. VRChat's own lists are capped
	// (FavoriteArea.MAX_FAVORITES_PER_LIST), so favourite #51 cannot exist on their side and there
	// would be nothing to mirror — an unlimited Archive cannot round-trip through a limited store.
	// A button of our own writes straight to the Archive, with no cap and no ambiguity about what
	// the user just clicked.
	//
	// Structure taken from a live dump of the avatar detail pane:
	//   Avatar_Action_Buttons
	//     Button_MM_Report
	//     Favorite_Avatar_Button   -> Background_Button, Text_ButtonName ("Add Favorite")
	//     Try_Avatar_Button        -> Background_Button, Text_ButtonName ("Applied")
	//     Purchase_Avatar_Button
	// We clone Favorite_Avatar_Button so ours is styled by VRChat exactly like its neighbours.
	public class ArchiveFavButtonModule : IModule
	{
		public override string Name => "ArchiveFavButton";

		private static ArchiveFavButtonModule _instance;
		public ArchiveFavButtonModule() { _instance = this; }

		private const string CloneName = "VA_ArchiveFavButton";
		private static readonly Color ArchiveViolet = new Color(0.36f, 0.20f, 0.62f, 1f);

		private Transform _row;          // Avatar_Action_Buttons
		private Transform _btn;          // our clone
		private Transform _metaBtn;
		private TMPro.TMP_Text _label;
		private float _next;
		// Consecutive passes that did not find the marketplace pane; drives the back-off above.
		private int _misses;

		// The avatar the pane is currently showing. Name is cheap to read every pass and tells us
		// when the selection changed; the id costs a subtree scan, so it is resolved once per
		// selection and then cached.
		private string _seenName = "";
		private string _id = "";
		private bool _busy;
		private static string _selectedArchiveId;
		private static string _selectedArchiveName;

		public static string Status = "";
		// Written by Run()'s pool-thread continuation, consumed by OnUpdate on the main thread.
		private static volatile string _pendingToast;

		public static void ClearArchiveSelection()
		{
			_selectedArchiveId = null;
			_selectedArchiveName = null;
		}

		public static void SelectAvatar(string id, string name, string author = null, string image = null)
		{
			if (string.IsNullOrEmpty(id)) return;
			_selectedArchiveId = id;
			_selectedArchiveName = name;
			ArchiveHijackModule.LastPreviewedId = id;
			if (_instance != null)
			{
				_instance._id = id;
				_instance._seenName = name;
				_instance.UpdatePaneUI(id, name);
			}
			Core.AvatarPreview.Open(id, name, author, image);
		}

		private void UpdatePaneUI(string id, string name)
		{
			try
			{
				if (_row == null) Build();
				if (_row == null) return;

				// Update Avatar Name Text
				var nameT = _row.Find("Avatar_Name_Text");
				if (nameT != null)
				{
					var tmp = nameT.GetComponent<TMPro.TMP_Text>();
					if (tmp != null && !string.IsNullOrEmpty(name))
					{
						tmp.text = name;
						tmp.color = Color.white;
					}
				}

				// Update Apply Button (Avatar_CTA_Button)
				var cta = _row.Find("Avatar_CTA_Button");
				if (cta != null)
				{
					HookApply(cta);
					bool isWearing = string.Equals(VaTagsModule.LocalAvatarId(), id, StringComparison.Ordinal);
					var ctaText = cta.Find("Text_ButtonName")?.GetComponent<TMPro.TMP_Text>();
					if (ctaText != null)
					{
						ctaText.text = isWearing ? "Applied" : "Apply";
						ctaText.color = Color.white;
					}
					var btn = cta.GetComponent<Button>();
					if (btn != null)
					{
						btn.interactable = !isWearing;
					}
					var bg = cta.Find("Background_Button")?.GetComponent<Image>();
					if (bg != null)
					{
						bg.color = isWearing ? new Color(0.2f, 0.25f, 0.3f, 1f) : new Color(0f, 0.82f, 0.70f, 1f);
					}
					var cg = cta.GetComponent<CanvasGroup>();
					if (cg != null)
					{
						cg.alpha = 1f;
						cg.interactable = !isWearing;
						cg.blocksRaycasts = true;
					}
				}

				Retitle();
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] UpdatePaneUI: " + ex.Message);
			}
		}

		public override void OnUpdate()
		{
			try
			{
				// ADAPTIVE POLL — the pane does not move while nobody is looking at it.
				//
				// Two passes per second, in every world, was measured at 220 ms/s on a big instance.
				// The pane can only appear when the QuickMenu is open, so a closed menu drops us to
				// once every four seconds — enough to notice the pane the frame it appears — and
				// even a fully-built button coasts at once a second, since the whole point of the
				// pass is to REACT to the pane changing.
				float now = VaClock.Now;

				// A status produced off the main thread (Run's continuation) is toasted here, before
				// the throttle below, so it shows the next frame rather than waiting.
				string pending = _pendingToast;
				if (pending != null)
				{
					_pendingToast = null;
					Core.Toast.Show(pending);
					Retitle();
				}

				if (now < _next) return;
				bool menuOpen = false;
				try { menuOpen = Core.QuickMenu.MainVisible; } catch { }

				if (!menuOpen) _misses = 0;
				// Cadence when menu is open (0.8s) keeps UI responsive without CPU spikes
				float wait = menuOpen
					? (_btn != null ? 0.8f : Mathf.Min(4f, 0.5f * (1 << Mathf.Min(4, _misses))))
					: 3f;
				_next = now + wait;

				if (_row == null || _btn == null)
				{
					Build();
					if (_btn != null) _misses = 0; else if (_misses < 4) _misses++;
					return;
				}

				if (!_row.gameObject.activeInHierarchy) return;

				HookApply(_row.Find("Avatar_CTA_Button"));

				string nm = NameOnPane();

				// If an archive selection is active, verify the pane hasn't navigated away
				if (!string.IsNullOrEmpty(_selectedArchiveId))
				{
					if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(_selectedArchiveName)
						&& !string.Equals(nm.Trim(), _selectedArchiveName.Trim(), StringComparison.OrdinalIgnoreCase))
					{
						// User navigated away to a different avatar in VRChat menus
						ClearArchiveSelection();
					}
					else if (!string.Equals(_id, _selectedArchiveId, StringComparison.Ordinal))
					{
						_id = _selectedArchiveId;
						_seenName = _selectedArchiveName ?? nm;
						UpdatePaneUI(_id, _seenName);
					}
				}

				// If not in archive selection (or selection was just cleared), follow the pane's avatar
				if (string.IsNullOrEmpty(_selectedArchiveId))
				{
					if (!string.Equals(nm, _seenName, StringComparison.Ordinal) || string.IsNullOrEmpty(_id))
					{
						_seenName = nm;
						_id = ResolveId();
					}
				}

				Retitle();

				KeepViolet(_btn);
				KeepViolet(_metaBtn);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] " + e.Message); }
		}

		private void KeepViolet(Transform t)
		{
			try
			{
				if (t == null) return;

				// THE CANVASGROUP IS ALWAYS CHECKED FIRST.
				//
				// The old fast path returned as soon as the background was violet — and never looked
				// at the CanvasGroup. So a button whose fill was the right colour but whose
				// CanvasGroup alpha had been dragged below 1 (VRChat re-adds one when it restyles the
				// pane) stayed permanently DIM: exactly the faded "Get metadata" next to the solid
				// main button. This is one component read; cheap enough to do every pass.
				var cg = t.GetComponent<CanvasGroup>();
				if (cg != null && (cg.alpha < 0.99f || !cg.interactable || !cg.blocksRaycasts))
				{ cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }

				var bg = t.Find("Background_Button")?.GetComponent<Image>();
				// Fast path for the colour repair only, now that the CanvasGroup is already handled.
				if (bg != null && bg.color == ArchiveViolet)
				{
					var tmpF = t.Find("Text_ButtonName")?.GetComponent<TMPro.TMP_Text>();
					if (tmpF != null && tmpF.color != Color.white) tmpF.color = Color.white;
					return;
				}

				if (bg != null)
				{
					// A StyleElement that came back would keep repainting it; drop it again.
					foreach (var c in t.GetComponentsInChildren<Component>(true))
						if (c != null && MenuCard.Il2CppNameOf(c) == "StyleElement") DisableStyle(c);
					bg.color = ArchiveViolet;
				}
				var tmp = t.Find("Text_ButtonName")?.GetComponent<TMPro.TMP_Text>();
				if (tmp != null && tmp.color != Color.white) tmp.color = Color.white;
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex) { _row = null; _btn = null; _label = null; }

		public override void OnShutdown()
		{
			try { if (_btn != null) UnityEngine.Object.Destroy(_btn.gameObject); } catch { }
			_btn = null; _row = null; _label = null;
		}

		// ------------------------------------------------------------------ build

		private void Build()
		{
			try
			{
				// Avatar_Marketplace_Panel, NOT Avatar_Action_Buttons. The first target was the
				// marketplace card's own button row, which is inactive in normal browsing — the
				// pane the user actually sees is this one:
				//   Avatar_Marketplace_Panel
				//     Avatar_Name_Text            "VRCHAT ARCHIVE ASSETBUNDLE"
				//     Avatar_CTA_Button           -> Text_ButtonName "Apply"/"Applied"   <- donor
				//     Avatar_Functions_Container  -> Avatar_Details_Button, Favorite_Avatar_Button
				_row = FindByName("Avatar_Marketplace_Panel");
				if (_row == null) return;

				// Already there from a previous pass (the pane is rebuilt as you browse).
				var existing = _row.Find(CloneName);
				if (existing != null) { Adopt(existing); return; }

				// The CTA is the labelled, full-width button; the two round ones next to it are
				// icon-only and have nowhere to put "Remove from Archive".
				var donor = _row.Find("Avatar_CTA_Button");
				if (donor == null) { Status = "VRChat's Apply button not found"; return; }

				var go = UnityEngine.Object.Instantiate(donor.gameObject, _row);
				go.name = CloneName;
				var t = go.transform;
				t.SetSiblingIndex(donor.GetSiblingIndex() + 1);

				// Same treatment as our menu cards: drop the donor's own handlers from the ROOT so
				// VRChat's favourite action cannot still fire, keep StyleElement so the game themes
				// it like its neighbours.
				try { MenuCard.StripRoot(t, keepStyle: true); } catch { }

				var g = t.GetComponent<Graphic>();
				if (g == null)
				{
					var img = go.AddComponent<Image>();
					img.color = new Color(0f, 0f, 0f, 0f);
					g = img;
				}
				g.raycastTarget = true;

				var bg = t.Find("Background_Button")?.GetComponent<Image>();
				var btn = t.GetComponent<Button>() ?? go.AddComponent<Button>();
				btn.targetGraphic = bg != null ? (Graphic)bg : g;
				btn.interactable = true;
				// ColorTint, not None. "None" was right while VRChat's StyleElement drove the
				// visuals — but Unfade() strips that off precisely because it painted the button
				// disabled, and a button with neither is dead on hover. Unity's own transition
				// gives the hover and press states back, derived from OUR colour so nothing can
				// disagree about which one is the base.
				btn.transition = Selectable.Transition.ColorTint;
				btn.colors = MenuCard.Tint(ArchiveViolet);
				try { btn.onClick.RemoveAllListeners(); } catch { }
				Core.UiClick.AddClick(btn, OnClick);

				go.SetActive(true);
				Adopt(t);
				HookApply(_row.Find("Avatar_CTA_Button"));

				// Two more, cloned the same way and placed just under ours: copy the id, and pull the
				// full metadata. Both read the avatar currently on the pane, so they work anywhere a
				// detail pane is open — not only inside our category. Archive violet, like the main
				// button, so the three read as one set.
				// No "Copy avatar id" button: "Get metadata" already puts the id on the clipboard
				// along with everything else about the avatar, so a second button was one more thing
				// to read on a pane that only has room for a few.
				_metaBtn = CloneButton(donor, "VA_GetMeta", "Get metadata", ArchiveViolet, GetMetadata);
				if (_metaBtn != null) _metaBtn.SetSiblingIndex(t.GetSiblingIndex() + 1);

				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] buttons added to the avatar detail pane.");
			}
			catch (Exception e)
			{
				Status = "could not add the button: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] build failed: " + e);
			}
		}

		// One reusable clone: same recipe as the main button, a fixed label and a click. Kept simple
		// because these two do not change label with state the way Save/Remove does.
		private Transform CloneButton(Transform donor, string name, string label, Color bgColor, Action onClick)
		{
			try
			{
				if (_row == null || donor == null) return null;
				var existing = _row.Find(name);
				if (existing != null) return existing;

				var go = UnityEngine.Object.Instantiate(donor.gameObject, _row);
				go.name = name;
				var t = go.transform;
				try { MenuCard.StripRoot(t, keepStyle: true); } catch { }

				var g = t.GetComponent<Graphic>() ?? go.AddComponent<Image>();
				g.raycastTarget = true;
				var btn = t.GetComponent<Button>() ?? go.AddComponent<Button>();
				var bg = t.Find("Background_Button")?.GetComponent<Image>();
				btn.targetGraphic = bg != null ? (Graphic)bg : g;
				btn.interactable = true;
				btn.transition = Selectable.Transition.ColorTint;
				btn.colors = MenuCard.Tint(bgColor);
				try { btn.onClick.RemoveAllListeners(); } catch { }
				Core.UiClick.AddClick(btn, onClick);

				// Strip StyleElement (else it renders disabled), paint it, size the label.
				try
				{
					foreach (var c in t.GetComponentsInChildren<Component>(true))
						if (c != null && MenuCard.Il2CppNameOf(c) == "StyleElement") DisableStyle(c);
					if (bg != null) bg.color = bgColor;
					var tmp = t.Find("Text_ButtonName")?.GetComponent<TMPro.TMP_Text>();
					if (tmp != null)
					{
						tmp.text = label;
						tmp.color = Color.white;
						tmp.enableAutoSizing = true; tmp.enableWordWrapping = false;
						tmp.fontSizeMin = 10f; tmp.alignment = TMPro.TextAlignmentOptions.Center;
					}
				}
				catch { }

				go.SetActive(true);
				return t;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] clone '" + name + "': " + e.Message); return null; }
		}

		private void CopyId()
		{
			try
			{
				string id = CurrentId();
				if (string.IsNullOrEmpty(id)) { Status = "no avatar id on this pane"; Core.Toast.Show(Status); return; }
				GUIUtility.systemCopyBuffer = id;
				Status = "copied " + id;
				Core.Toast.Show(Status);
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] copied id " + id);
			}
			catch (Exception e) { Status = "copy failed: " + e.Message; Core.Toast.Show(Status); }
		}

		private void GetMetadata()
		{
			try
			{
				string id = CurrentId();
				if (string.IsNullOrEmpty(id)) { Status = "no avatar id on this pane"; return; }

				// Read from VRChat's own record — the one the game already has for any avatar it has
				// LOADED (every player you can see, and the one you are wearing). NO API.Fetch: that
				// call takes the process down on a private avatar (an access violation, not an
				// exception), and it is exactly what crashed this button before. If the game holds no
				// record, the avatar simply is not loaded here — the desktop client's Get metadata
				// does the authenticated GET /avatars/{id} for that case.
				var a = VRC.Core.API.FromCacheOrNew<VRC.Core.ApiAvatar>(id);

				// A dead proxy is an AV, and .Populated is itself a read, so the liveness gate is first.
				bool done = false;
				if (a != null && Core.NativeGuard.Alive(a)) { try { done = a.Populated; } catch { } }
				if (!done)
				{
					GUIUtility.systemCopyBuffer = id;
					Status = "avatar not loaded here — id copied; use the client's Get metadata for the full record";
					Core.Toast.Show(Status);
					return;
				}

				var sb = new System.Text.StringBuilder();
				sb.Append("id: ").Append(id).Append('\n');
				// Only the string properties ArchiveHijack reads safely every day. version, dates and
				// the tags list each touch a nested il2cpp value the gate above does not cover.
				try { sb.Append("name: ").Append(a.name ?? "").Append('\n'); } catch { }
				try { sb.Append("author: ").Append(a.authorName ?? "").Append("  (").Append(a.authorId ?? "").Append(")\n"); } catch { }
				try { sb.Append("release: ").Append(a.releaseStatus ?? "").Append('\n'); } catch { }
				try { sb.Append("image: ").Append(a.imageUrl ?? "").Append('\n'); } catch { }
				try { sb.Append("description: ").Append(a.description ?? "").Append('\n'); } catch { }

				GUIUtility.systemCopyBuffer = sb.ToString();
				Status = "metadata copied to clipboard";
				Core.Toast.Show(Status);
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] metadata copied:\n" + sb);
			}
			catch (Exception e) { Status = "metadata failed: " + e.Message; Core.Toast.Show(Status); }
		}

		private static string Str(object o, string prop)
		{
			try { var p = o.GetType().GetProperty(prop); var v = p != null ? p.GetValue(o) : null; return v != null ? v.ToString() : ""; }
			catch { return ""; }
		}

		// The id of the avatar this pane is showing: current resolved id, or a fresh resolve.
		private string CurrentId()
		{
			if (!string.IsNullOrEmpty(_id)) return _id;
			return ResolveId();
		}

		// DESTROY, not disable. Disabling a StyleElement fires its OnDisable, which reverts the
		// Background_Button back to VRChat's OUTLINED FRAME sprite — our violet then only tints that
		// frame, so the button reads as a dark, hard-edged box instead of the clean flat violet it
		// used to be. That was the regression. Destroying the component leaves the flat sprite and
		// colour we painted in place. VRChat re-adds a StyleElement on a later restyle; KeepViolet
		// destroys the replacement on its next pass, so the only cost is a brief flash on restyle,
		// not a permanently ugly button.
		private static void DisableStyle(Component c)
		{
			try { UnityEngine.Object.Destroy(c); } catch { }
		}

		private void Adopt(Transform t)
		{
			_btn = t;
			try { _label = t.Find("Text_ButtonName")?.GetComponent<TMPro.TMP_Text>(); } catch { }
			if (_label == null) { try { _label = t.GetComponentInChildren<TMPro.TMP_Text>(true); } catch { } }
			Unfade(t);
			Retitle();
		}

		// The clone came out greyed next to a bright "Apply". VRChat paints these buttons through
		// StyleElement, and the state it paints is driven by a component we had to strip off the
		// root so the donor's own action could not fire. Left alone it therefore renders the
		// DISABLED variant forever. Since the game can no longer be trusted to style it, we take
		// the colours over completely rather than fight for them every repaint.
		private static void Unfade(Transform t)
		{
			try
			{
				var cg = t.GetComponent<CanvasGroup>();
				if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }

				foreach (var c in t.GetComponentsInChildren<Component>(true))
				{
					if (c == null) continue;
					if (MenuCard.Il2CppNameOf(c) != "StyleElement") continue;
					try { UnityEngine.Object.Destroy(c); } catch { }
				}

				var bg = t.Find("Background_Button")?.GetComponent<Image>();
				if (bg != null) bg.color = ArchiveViolet;

				var tmp = t.Find("Text_ButtonName")?.GetComponent<TMPro.TMP_Text>();
				if (tmp != null)
				{
					tmp.color = Color.white;

					// SHRINK, NEVER CLIP. The button is sized for VRChat's one-word labels, so the
					// longer of our two states ran off the right edge — "REMOVE FROM ARCHIVE FA".
					// Picking shorter words fixes it for today's two strings and breaks again the
					// next time one changes; making the text fit its box fixes it for good.
					try
					{
						float baseSize = tmp.fontSize;
						tmp.enableWordWrapping = false;
						tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
						tmp.enableAutoSizing = true;
						tmp.fontSizeMax = baseSize > 1f ? baseSize : 24f;
						tmp.fontSizeMin = Mathf.Max(9f, tmp.fontSizeMax * 0.55f);
						tmp.alignment = TMPro.TextAlignmentOptions.Center;
					}
					catch { }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] unfade: " + e.Message); }
		}

		private void Retitle()
		{
			try
			{
				if (_label == null) return;
				// Says what the CLICK does, not what the state is — "Archived" would leave you
				// guessing whether pressing it saves or unsaves. Sentence case and "Archive" rather
				// than "ARCHIVE FAV" to sit properly next to VRChat's own "Apply": its buttons are
				// one or two plain words, and shouting next to them looked like a different app.
				bool has = !string.IsNullOrEmpty(_id) && FavoritesModule.Has(_id);
				_label.text = _busy ? "…" : (has ? "Remove from Archive" : "Save to Archive");
			}
			catch { }
		}

		// ------------------------------------------------------------------ click

		private void OnClick()
		{
			try
			{
				if (_busy) return;
				string pn = NameOnPane();
				if (string.IsNullOrEmpty(_id) || (!string.IsNullOrEmpty(pn) && !string.Equals(pn, _seenName, StringComparison.Ordinal)))
				{
					_seenName = pn;
					_id = ResolveId();
				}

				if (string.IsNullOrEmpty(_id))
				{
					Status = "could not tell which avatar is shown";
					Core.Toast.Show(Status);
					VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] no avatar id found on the detail pane.");
					return;
				}

				bool has = FavoritesModule.Has(_id);
				_busy = true;
				Retitle();
				_ = Run(_id, _seenName, has);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] click: " + e.Message); }
		}

		private async System.Threading.Tasks.Task Run(string targetId, string targetName, bool remove)
		{
			try
			{
				bool ok;
				if (remove)
				{
					ok = await FavoritesModule.RemoveAsync(targetId);
				}
				else
				{
					var idx = AvatarIndex.ById(targetId);
					string name = !string.IsNullOrEmpty(targetName) ? targetName : idx?.Name;
					string author = idx?.AuthorName;
					string image = idx?.ThumbUrl ?? idx?.ImageUrl;
					if (string.IsNullOrEmpty(name)) name = targetName;
					if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(image))
					{
						try
						{
							var a = VRC.Core.API.FromCacheOrNew<VRC.Core.ApiAvatar>(targetId);
							if (a != null && Core.NativeGuard.Alive(a) && a.Populated)
							{
								if (string.IsNullOrEmpty(name)) name = a.name;
								if (string.IsNullOrEmpty(author)) author = a.authorName;
								if (string.IsNullOrEmpty(image)) image = !string.IsNullOrEmpty(a.thumbnailImageUrl) ? a.thumbnailImageUrl : a.imageUrl;
							}
						}
						catch { }
					}
					ok = await FavoritesModule.AddAsync(targetId, name, author, image);
				}
				Status = ok
					? (remove ? "removed from your Archive favourites" : "saved to your Archive favourites")
					: FavoritesModule.LastStatus;
				VRChatArchiveModPlugin.Logger.LogInfo($"[ArchiveFavBtn] {(remove ? "remove" : "add")} {targetId} -> {ok}");
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
			finally
			{
				_busy = false;
				_pendingToast = Status;
			}
		}

		// ------------------------------------------------------------------ Apply

		// ASK THE SERVER, AND TAKE ITS ANSWER.
		//
		// "Apply" is PUT /api/1/avatars/{id}/select, and that endpoint is arbitrated by VRChat's own
		// backend — it decides whether you may wear an avatar. The "This user has cloning turned
		// off" popup is the CLIENT deciding not to ask. Sometimes it is right; sometimes the record
		// it judged from was incomplete, which is exactly the state our freshly-fetched avatars pass
		// through.
		//
		// So this asks. It forges nothing, carries no credential of its own — it goes out on the
		// game's own authenticated session — and if the server refuses, the avatar simply does not
		// change and we say so. The authority stays where it already was.
		// The INSTANCE we hooked, not a bare "done" flag. VRChat rebuilds this pane as you browse,
		// so a static bool meant we attached to the first Apply button ever seen and then refused to
		// touch its replacements — the hook silently stopped existing the moment the pane was
		// rebuilt, which is exactly what the log showed: hooked once, then never fired again.
		private static int _applyHookedId;

		private void HookApply(Transform cta)
		{
			try
			{
				if (cta == null) return;
				int id = cta.GetInstanceID();
				if (_applyHookedId == id) return;
				var btn = cta.GetComponent<Button>();
				if (btn == null) return;

				// ADDED, never replacing: VRChat's own handler still runs first and wins whenever it
				// is willing to act.
				Core.UiClick.AddClick(btn, OnApply);
				_applyHookedId = id;
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] Apply hooked (asks the select endpoint when the client declines).");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] Apply hook: " + e.Message); }
		}

		private void OnApply()
		{
			try
			{
				string id = _id;
				if (string.IsNullOrEmpty(id)) id = ResolveId();
				if (string.IsNullOrEmpty(id)) return;

				// ARCHIVE AVATARS ONLY. This hook is ADDED to VRChat's own Apply, which has already
				// run by the time we get here — so on an ordinary avatar (one you own, a public one
				// from the marketplace) the game has just switched you and there is nothing left to
				// do. Re-issuing WearById on top of it wore every avatar TWICE: two clone requests,
				// two loads, and a "switching…" status on panes that never needed our help. The
				// only case this hook exists for is an avatar that reached the pane through OUR
				// category (a favourite, or the id we last previewed), which VRChat's client may
				// decline to wear from its borrowed list slot. Anything else is left to the game.
				if (!(FavoritesModule.Has(id) || string.Equals(ArchiveHijackModule.LastPreviewedId, id, StringComparison.Ordinal))) return;

				// Already wearing it: nothing to do.
				if (string.Equals(VaTagsModule.LocalAvatarId(), id, StringComparison.Ordinal)) return;

				// STRAIGHT TO WEAR. The select endpoint respects the state of the list the avatar
				// sits in — and our list is a BORROWED shelf. A VRC+ favourites slot is locked
				// without a subscription ("you cannot switch into them"); SDK Test has its own
				// rules. So select refused, every time, for reasons that have nothing to do with the
				// avatar. WearById clones by id and ignores the list entirely — it is the exact path
				// the mod's own WEAR button uses, which is what "make Apply do what WEAR does" means.
				Status = "switching…";
				Core.Toast.Show(Status);
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] apply -> WearById " + id);
				try { VaTagsModule.WearById(id, _seenName); }
				catch (Exception we) { Status = "wear failed: " + we.Message; Core.Toast.Show(Status); }
			}
			catch (Exception e)
			{
				Status = "apply failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFavBtn] apply: " + e.Message);
			}
		}

		// ------------------------------------------------------------------ which avatar?

		private string NameOnPane()
		{
			try
			{
				var t = _row != null ? _row.Find("Avatar_Name_Text") : null;
				var tmp = t != null ? t.GetComponent<TMPro.TMP_Text>() : null;
				return tmp != null ? (tmp.text ?? "") : "";
			}
			catch { return ""; }
		}

		// IDENTIFY BY VALUE, not by name. Which obfuscated property carries the avatar id is not
		// something the interop assemblies can tell us — every method body there is a native shim.
		// But an avatar id is unmistakable on sight, so this walks the detail pane's components and
		// takes the first string that looks like one. Runs on click only, never per frame.
		private string ResolveId()
		{
			try
			{
				string pn = NameOnPane();

				// 1. If currently showing an archive selection and the pane name matches it:
				if (!string.IsNullOrEmpty(_selectedArchiveId) && !string.IsNullOrEmpty(_selectedArchiveName))
				{
					if (string.IsNullOrEmpty(pn) || string.Equals(pn.Trim(), _selectedArchiveName.Trim(), StringComparison.OrdinalIgnoreCase))
					{
						return _selectedArchiveId;
					}
				}

				// 2. Check AvatarIndex by displayed name (direct parse hook hit!)
				if (!string.IsNullOrEmpty(pn))
				{
					string idByName = AvatarIndex.IdForName(pn);
					if (!string.IsNullOrEmpty(idByName))
					{
						VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] resolved id by name '" + pn + "' -> " + idByName);
						return idByName;
					}

					// 2b. Check FavoritesModule by name
					string idByFav = FavoritesModule.IdForName(pn);
					if (!string.IsNullOrEmpty(idByFav))
					{
						VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] resolved id by favorite name '" + pn + "' -> " + idByFav);
						return idByFav;
					}
				}

				// 3. Check if pane shows the worn avatar (name matches worn avatar)
				string worn = WornIdIfPaneShowsIt();
				if (!string.IsNullOrEmpty(worn))
				{
					VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] pane shows the worn avatar -> " + worn);
					return worn;
				}

				// 4. Fallback: inspect pane hierarchy
				Transform root = _row;
				for (int up = 0; up < 4 && root != null && root.parent != null; up++) root = root.parent;
				if (root != null)
				{
					string hit = "";
					Scan(root, 0, ref hit);
					if (!string.IsNullOrEmpty(hit))
					{
						VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFavBtn] resolved avatar id by scan " + hit);
						return hit;
					}
				}

				// 5. Fallback to recently parsed avatar (within 2s)
				string recent = AvatarIndex.RecentId(2f);
				if (!string.IsNullOrEmpty(recent)) return recent;

				return "";
			}
			catch { return ""; }
		}

		private string WornIdIfPaneShowsIt()
		{
			try
			{
				string wornName = VaTagsModule.LocalAvatarName();
				string paneName = NameOnPane();
				if (string.IsNullOrEmpty(wornName) || string.IsNullOrEmpty(paneName)) return null;

				bool sameName = string.Equals(wornName.Trim(), paneName.Trim(), StringComparison.OrdinalIgnoreCase);
				if (!sameName) return null;

				return VaTagsModule.LocalAvatarId();
			}
			catch { return null; }
		}

		private static void Scan(Transform t, int depth, ref string hit)
		{
			if (t == null || depth > 8 || !string.IsNullOrEmpty(hit)) return;
			try
			{
				var comps = t.GetComponents<Component>();
				if (comps != null)
					foreach (var c in comps)
					{
						// TWO checks, not one. `c == null` is Unity's destroyed-object operator; it
						// catches an object the engine tore down. NativeGuard.Alive checks the il2cpp
						// object BEHIND the proxy is still readable. Double-clicking a favourite
						// rebuilds this pane WHILE this scan walks it, so components die mid-loop —
						// and reading a property off a dead one is an access violation that the
						// try/catch below cannot catch. It crashed the game to desktop.
						if (c == null || !Core.NativeGuard.Alive(c)) continue;
						string s = StringsOf(c);
						if (!string.IsNullOrEmpty(s)) { hit = s; return; }
					}
			}
			catch { }
			for (int i = 0; i < t.childCount && string.IsNullOrEmpty(hit); i++)
			{
				try { Scan(t.GetChild(i), depth + 1, ref hit); } catch { }
			}
		}

		private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

		// Name -> il2cpp proxy type, built once on first use. Only types with the (IntPtr)
		// constructor are kept, because that is the only thing StringsOf can re-wrap through.
		private static Dictionary<string, Type> _typeIndex;

		private static Dictionary<string, Type> TypeIndex()
		{
			if (_typeIndex != null) return _typeIndex;
			var idx = new Dictionary<string, Type>(StringComparer.Ordinal);
			float t0 = VaClock.Now;
			try
			{
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type[] types;
					try { types = asm.GetTypes(); }
					catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }
					catch { continue; }
					foreach (var x in types)
					{
						if (x == null) continue;
						string n = x.Name;
						if (string.IsNullOrEmpty(n) || idx.ContainsKey(n)) continue;
						try { if (x.GetConstructor(new[] { typeof(IntPtr) }) == null) continue; }
						catch { continue; }
						idx[n] = x;
					}
				}
			}
			catch { }
			_typeIndex = idx;
			VRChatArchiveModPlugin.Logger.LogInfo(
				"[ArchiveFavBtn] type index built: " + idx.Count + " type(s) in "
				+ ((VaClock.Now - t0) * 1000f).ToString("0") + " ms (once per session).");
			return _typeIndex;
		}

		// The string properties of each il2cpp class, resolved once. GetProperties + the filter ran
		// for every component of every node on every scan; the profiler put this module at 776 ms/s
		// with the menu open. The set of properties on a type never changes at runtime, so it is
		// resolved on first sight and reused.
		private static readonly Dictionary<Type, PropertyInfo[]> StringPropCache = new Dictionary<Type, PropertyInfo[]>();

		private static PropertyInfo[] StringProps(Type t)
		{
			if (StringPropCache.TryGetValue(t, out var cached)) return cached;
			var list = new List<PropertyInfo>();
			try
			{
				foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
					if (p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
						list.Add(p);
			}
			catch { }
			var arr = list.ToArray();
			StringPropCache[t] = arr;
			return arr;
		}

		private static string StringsOf(Component c)
		{
			try
			{
				// The component is typed as UnityEngine.Component here; reflecting its real members
				// requires re-wrapping it through the proxy for its ACTUAL il2cpp class. Skipping
				// this step is what made three earlier probes report "nothing found".
				string cls = MenuCard.Il2CppNameOf(c);
				if (string.IsNullOrEmpty(cls) || cls == "?") return "";
				if (!TypeCache.TryGetValue(cls, out var t))
				{
					// ONE SCAN, EVER — this loop was the single worst thing in the mod.
					//
					// It used to walk EVERY loaded assembly and materialise EVERY type in each one
					// (asm.GetTypes() over ~250 interop DLLs) for each class name it had not seen
					// yet. A detail pane holds dozens of distinct component classes, so opening one
					// meant dozens of full-domain type scans back to back: the profiler caught it at
					// 974 ms/s with the game at 2 fps.
					//
					// The set of loaded types does not change while you browse a menu, so it is
					// indexed once and every later lookup is a dictionary hit.
					TypeIndex().TryGetValue(cls, out t);
					TypeCache[cls] = t;
				}
				if (t == null) return "";

				// TYPE-IDENTITY CHECK, THE CRASH GUARD.
				//
				// t was resolved by SIMPLE NAME, and two il2cpp classes in different namespaces can
				// share one — so t could be the managed binding for a DIFFERENT class than c really
				// is. Reconstructing a proxy of the wrong type over c's pointer and reading its
				// properties reads memory at offsets that do not belong to the object: an access
				// violation that takes the game down, which is what crashed on opening the avatar
				// pane. Il2CppType.From(t) is the il2cpp class t binds; il2cpp_object_get_class is
				// the class c actually is. If they are not the SAME pointer, t is the wrong binding
				// and we must not read through it.
				IntPtr cptr = ((Il2CppObjectBase)c).Pointer;
				if (cptr == IntPtr.Zero) return "";
				IntPtr realClass = IL2CPP.il2cpp_object_get_class(cptr);
				IntPtr boundClass;
				try { boundClass = Il2CppType.From(t)?.Pointer ?? IntPtr.Zero; }
				catch { return ""; }
				if (realClass == IntPtr.Zero || boundClass == IntPtr.Zero || realClass != boundClass)
					return "";   // name matched but the type does not — refuse to read through it

				object proxy;
				try { proxy = t.GetConstructor(new[] { typeof(IntPtr) }).Invoke(new object[] { cptr }); }
				catch { return ""; }

				foreach (var p in StringProps(t))
				{
					// Re-checked INSIDE the loop: reading one property can run game code that
					// destroys the object before the next read. The guard has to be as fresh as
					// the read it protects.
					if (!Core.NativeGuard.Alive(c)) return "";
					string v;
					try { v = p.GetValue(proxy) as string; }
					catch { continue; }
					if (!string.IsNullOrEmpty(v) && v.StartsWith("avtr_", StringComparison.Ordinal)) return v;
				}
			}
			catch { }
			return "";
		}

		// SCOPED TO THE MENU, NOT THE WHOLE GAME.
		//
		// This used to be Resources.FindObjectsOfTypeAll<Transform>(), which walks EVERY Transform
		// loaded in the process — tens of thousands in a populated instance. Build() calls it twice
		// a second whenever the avatar panel is not on screen, which is nearly all the time, and the
		// profiler caught the result: ArchiveFavButton at 815 ms per SECOND, the game at 7 fps. All
		// of it spent looking through an entire world for a menu object that only ever exists inside
		// one canvas.
		//
		// The panel lives under the main menu, so that is where we look. Core.QuickMenu caches that
		// canvas and re-finds it only when destroyed, so this is a few thousand nodes at worst and
		// nothing at all while the menu is closed.
		// FINDING THE PANE COST 964 ms/s \u2014 96% of a whole second, and the game ran at 1 fps.
		//
		// This used to walk root.GetComponentsInChildren<Transform>(true): every node of VRChat's
		// menu tree, INACTIVE ONES INCLUDED, which is thousands of transforms \u2014 and reading .name
		// on each is a separate il2cpp interop call. One pass measured around half a second, and
		// because the pane only exists while you are actually looking at an avatar, the usual
		// outcome was to walk the whole tree, find nothing, and do it again half a second later,
		// for as long as the menu stayed open on any other page.
		//
		// GameObject.Find does the same job in ONE native call: it searches active objects by name
		// inside the engine, which is exactly the set we wanted (the check below was already
		// discarding inactive hits). The managed walk is kept only as a fallback for the case the
		// name is not unique in the scene, and it is then scoped to the menu root as before.
		private static Transform FindByName(string name)
		{
			try
			{
				if (!Core.QuickMenu.MainVisible) return null;   // closed menu: the panel cannot exist

				try
				{
					var go = GameObject.Find(name);
					if (go != null) return go.transform;
				}
				catch { }

				var main = Core.QuickMenu.Main();
				if (main != null)
				{
					foreach (var t in main.GetComponentsInChildren<Transform>(true))
					{
						if (t != null && t.name == name) return t;
					}
				}
				return null;
			}
			catch { }
			return null;
		}
	}
}
