using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Core;
using VRC.DataModel;
using VRChatArchiveMod.Core;

using Category  = ObjectPublicStBo1BoILSt1NuBoInUnique;
using Panel     = MonoBehaviourPublicOb_aGa_c_aOb_sGa_e_lUnique;
using Il2IList  = Il2CppSystem.Collections.IList;

namespace VRChatArchiveMod.Modules
{
	// VRCHAT ARCHIVE — a real category in VRChat's own avatar menu.
	//
	// Two earlier attempts failed for the same reason, and the reason is worth stating because it
	// is the whole design of this file:
	//
	//   1. Adding a FavoriteListModel to VRC.Core.API.Favorites._avatars. It landed — the log
	//      showed 7 lists holding 147 avatars — and nothing appeared. FavoriteArea has NO events,
	//      NO observers, and the avatar menu never reads it. It is an API response cache.
	//   2. Cloning a sidebar row GameObject (ArchiveSidebarRowModule). It shows a row, but that row
	//      is scenery: the grid is bound to an observable the clone is not part of, so selecting it
	//      can never fill it.
	//
	// What actually draws the sidebar is a ListBinding over an OBSERVABLE List<Category> held by the
	// avatar-collections panel. Two consequences drive everything below:
	//
	//   * Notification happens on the observable's SETTER. Adding to the list it currently holds
	//     notifies nobody — the same trap as _avatars. A NEW list has to be assigned.
	//   * Each Category carries its OWN avatar source. That is what makes this worth doing: we hand
	//     ours a source built from the Archive, and VRChat's own grid renders our avatars as native
	//     cards, with Apply, info and the star.
	//
	// Every obfuscated name here is a per-build binding. Only the serialized field names
	// (_avatarListSelectorPrefab, _avatarListView) and the VRC.Core / VRC.DataModel types survive a
	// VRChat update, so the panel is verified through one of those before anything is touched.
	public class ArchiveCategoryModule : IModule
	{
		public override string Name => "ArchiveCategory";

		private const string CatId = "fvgrp_vrchatarchive";
		private const string CatName = "VRCHAT ARCHIVE";

		public static string Status = "off";
		// Read by ArchiveSidebarRowModule: with a real category published, its cloned decoy row
		// would be a second VRCHAT ARCHIVE line in the same sidebar.
		public static bool Published { get; private set; }

		private float _next;
		private int _lastCount = -1;

		public override void OnUpdate()
		{
			try
			{
				// No gate: the Archive category is always on.

				float now = VaClock.Now;
				if (now < _next) return;
				_next = now + 2f;

				Panel panel = FindPanel();
				if (panel == null) { Status = "waiting for the avatar menu"; return; }

				var obs = panel.field_Private_ReactiveProperty_1_List_1_ObjectPublicStBo1BoILSt1NuBoInUnique_0;
				if (obs == null) { Status = "panel not initialised"; return; }

				// SNAPSHOT, NEVER live[i]. List<T>.get_Item is mis-bound on VRChat 1903 and invoking it
				// ends the process inside il2cpp_runtime_invoke -- it is what crashed the game from
				// ArchiveHijackModule.PickTarget. Il2CppSeq.Items reads _items/_size, which are fields.
				var live = Core.Il2CppSeq.Items(obs.prop_T_0);
				if (live.Count == 0) { Status = "no categories yet"; return; }

				Hook(panel);

				var ids = FavoritesModule.Snapshot();
				bool present = IndexOfOurs(live) >= 0;
				// The store rebuilds and republishes its list on menu open and on any favourites
				// refresh, which drops ours. Re-publishing whenever it is missing is the self-heal.
				if (present && ids.Count == _lastCount) { Published = true; return; }
				if (ids.Count == 0) { Status = "no Archive favourites to show"; return; }

				// Publishing a new list makes the binding rebuild EVERY row, which cancels whatever
				// the user had selected. Racing the store on a 2s timer therefore did not just add a
				// flicker — it made VRChat's own categories unselectable, because a rebuild landed
				// between the click and the grid update. Once the hooks are in we republish only
				// when the store itself has just done so, and this poll is the slow safety net.
				if (_hooked && now < _publishedAt + 15f && present) return;

				// Donor: a category the game already built. Copying it carries its unnamed booleans
				// and its sprite, so none of them has to be guessed — the one part of this that is
				// pure inference is the part we avoid by not writing it at all.
				Category donor = null;
				for (int i = 0; i < live.Count; i++)
				{
					var c = live[i];
					if (c == null) continue;
					if (string.Equals(c.field_Public_String_0, CatId, StringComparison.Ordinal)) continue;
					donor = c; break;
				}
				if (donor == null) { Status = "no donor category"; return; }

				var ours = new Category(donor);
				ours.field_Public_String_0 = CatId;

				var nameObs = new ReactiveProperty<string>(CatName)
					.TryCast<InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique<string>>();
				if (nameObs != null)
					ours.field_Public_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_String_0 = nameObs;
				else
					VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] name observable cast failed — the row will keep the donor's title.");

				Il2IList avatars = BuildAvatars(ids);
				if (avatars == null) { Status = "could not build the avatar list"; return; }

				var fetch = new Object2PublicIDisposable1ObBo1ObSiAcSiStAcUnique<Il2IList>(avatars)
					.TryCast<InterfacePublicAbstractIDisposableVoAc1ObStAc1UnObUnUnique<Il2IList>>();
				if (fetch == null) { Status = "could not build the avatar source"; return; }
				ours.field_Public_InterfacePublicAbstractIDisposableVoAc1ObStAc1UnObUnUnique_1_IList_0 = fetch;
				// Cleared, not copied: a slot index inherited from the donor would make our row act
				// as that favourite slot for rename and selection.
				ours.field_Public_Nullable_1_Int32_0 = new Il2CppSystem.Nullable<int>();

				// PUBLISH BY ASSIGNMENT. This is the entire point: obs.prop_T_0.Add(ours) would
				// mutate the list in place and notify nothing, which is exactly how the previous
				// two attempts failed.
				var copy = new Il2CppSystem.Collections.Generic.List<Category>();
				for (int i = 0; i < live.Count; i++)
				{
					var c = live[i];
					if (c == null) continue;
					if (string.Equals(c.field_Public_String_0, CatId, StringComparison.Ordinal)) continue;
					copy.Add(c);
				}
				copy.Add(ours);
				obs.prop_T_0 = copy;

				_lastCount = ids.Count;
				_publishedAt = now;
				Published = true;
				LastAvatars = avatars;
				Status = ids.Count + " avatar(s) in the VRCHAT ARCHIVE category";
				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[ArchiveCat] published: {copy.Count} categories, {ids.Count} avatars in ours.");
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] " + e);
			}
		}

		public override void OnSceneLoaded(int buildIndex) { _lastCount = -1; Published = false; }

		// ------------------------------------------------------------------ hooks
		//
		// Selecting our row runs the panel's own selection handler. That handler resolves a category
		// to its avatars through the store's id mapping, and our id maps to nothing there — so the
		// grid keeps whatever it was showing, which is the "it says Recent while avatars1 is
		// highlighted" symptom. The postfix puts our avatars into the grid directly, using the one
		// public data entry point the grid section exposes.
		private static bool _hooked;
		private static float _publishedAt;
		private static Il2IList LastAvatars;
		private static Panel _panel;

		private static void Hook(Panel panel)
		{
			_panel = panel;
			if (_hooked) return;
			try
			{
				var t = typeof(Panel);
				var sel = t.GetMethod("Method_Private_Void_ObjectPublicStBo1BoILSt1NuBoInUnique_PDM_0",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Instance);
				if (sel == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] selection handler not found — the grid will not fill.");
					_hooked = true;    // do not retry every 2s
					return;
				}

				var post = new HarmonyLib.HarmonyMethod(typeof(ArchiveCategoryModule).GetMethod(
					nameof(OnCategorySelected),
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
				VRChatArchiveModPlugin.HarmonyInstance.Patch(sel, postfix: post);

				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveCat] hooked the category selection handler.");
			}
			catch (Exception e)
			{
				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] hook failed: " + e.Message);
			}
		}

		private static void OnCategorySelected(Category __0)
		{
			try
			{
				if (__0 == null || LastAvatars == null) return;
				if (!string.Equals(__0.field_Public_String_0, CatId, StringComparison.Ordinal)) return;
				if (_panel == null) return;

				var view = _panel._avatarListView;
				if (view == null) return;

				var src = new ReactiveProperty<Il2IList>(LastAvatars)
					.TryCast<InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique<Il2IList>>();
				if (src == null) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] grid source cast failed."); return; }

				view.Method_Public_Void_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_IList_Boolean_0(src, false);
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveCat] grid filled from the Archive category.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] fill: " + e.Message); }
		}

		// Takes the SNAPSHOT, not the native list: indexing the native one calls get_Item, which is
		// mis-bound on this build and kills the process. Callers already hold the snapshot.
		private static int IndexOfOurs(System.Collections.Generic.List<Category> live)
		{
			for (int i = 0; i < live.Count; i++)
			{
				var c = live[i];
				if (c != null && string.Equals(c.field_Public_String_0, CatId, StringComparison.Ordinal)) return i;
			}
			return -1;
		}

		// Our favourites as objects the grid accepts, with NO request to VRChat.
		//
		// The grid's element converter takes IAvatar, never FavoriteModel and never ApiAvatar. The
		// bridge is the game's own DataModel<ApiAvatar> wrapper, which is publicly constructible —
		// that is what makes 150 favourites free instead of 150 API round trips on every menu open.
		private static Il2IList BuildAvatars(List<string> ids)
		{
			try
			{
				var list = new Il2CppSystem.Collections.Generic.List<IAvatar>();
				var favs = FavoritesModule.Favourites();
				var meta = new Dictionary<string, FavoritesModule.Fav>(StringComparer.OrdinalIgnoreCase);
				foreach (var f in favs) if (f != null && !string.IsNullOrEmpty(f.Id)) meta[f.Id] = f;

				int made = 0;
				foreach (string id in ids)
				{
					if (string.IsNullOrEmpty(id)) continue;
					ApiAvatar a = API.FromCacheOrNew<ApiAvatar>(id);
					if (a == null) continue;

					// Filled from what the Archive already resolved for the mod's own grid, so the
					// native cards show a real name, author and thumbnail rather than bare ids.
					if (meta.TryGetValue(id, out var m) && m != null)
					{
						if (!string.IsNullOrEmpty(m.Name)) a.name = m.Name;
						if (!string.IsNullOrEmpty(m.Author)) a.authorName = m.Author;
						if (!string.IsNullOrEmpty(m.Image))
						{
							a.imageUrl = m.Image;
							a.thumbnailImageUrl = m.Image;
						}
					}
					// Marks the record as complete so nothing goes looking for the rest. The values
					// above come from VRChat's own metadata by way of our server, so this fills the
					// shared cache with accurate data rather than placeholders.
					a.Populated = true;

					var dm = new Object1PublicOb1ILOb1CaILNuIn1Unique();
					dm.Method_Public_Virtual_Void_ApiAvatar_0(a);
					var av = dm.TryCast<IAvatar>();
					if (av == null) continue;
					list.Add(av);
					made++;
				}

				if (made == 0)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] no IAvatar could be built — the DataModel cast failed.");
					return null;
				}
				return list.TryCast<Il2IList>();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] BuildAvatars: " + e.Message);
				return null;
			}
		}

		// The panel is identified by a READABLE serialized field, not by its obfuscated class name,
		// so a VRChat update that renames the class fails loudly here instead of silently doing
		// nothing somewhere further down.
		// NON-GENERIC (2026-09-08) — the same fix as ArchiveHijackModule.FindPanel, for the same
		// reason: Resources.FindObjectsOfTypeAll<Panel>() returns an EMPTY array. Panel is an alias
		// for an OBFUSCATED VRChat class, and the generic overload cannot resolve those. That is the
		// sharper form of a rule the mod already half-knew: generics are fine for real Unity types
		// (Transform, AudioSource and TMP_Text all work elsewhere in this codebase) and fail on the
		// game's own renamed proxies. Il2CppType.Of<T>() plus TryCast<T>() is what sees them.
		private static Il2CppSystem.Type _panelIl2;

		private static Panel FindPanel()
		{
			try
			{
				if (_panelIl2 == null) _panelIl2 = Il2CppInterop.Runtime.Il2CppType.Of<Panel>();
				var found = Resources.FindObjectsOfTypeAll(_panelIl2);
				if (found == null) return null;
				for (int i = 0; i < found.Length; i++)
				{
					var o = found[i];
					if (o == null) continue;
					Panel p = null;
					try { p = o.TryCast<Panel>(); } catch { continue; }
					if (p == null) continue;
					try { if (!p.gameObject.scene.IsValid()) continue; }
					catch { continue; }
					return p;
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveCat] FindPanel: " + e.Message); }
			return null;
		}
	}
}
