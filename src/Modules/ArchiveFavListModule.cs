using System;
using UnityEngine;
using VRC.Core;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// VRCHAT ARCHIVE — a category of our own inside VRChat's OWN avatar menu.
	//
	// The route came out of a live dump, not out of the assemblies. VRC.Core.API.Favorites is a
	// static property holding a FavoriteArea, and its _avatars list held exactly six
	// FavoriteListModel objects — the same six rows the menu draws in its sidebar, in the same
	// order, with readable displayNames. The sidebar IS that list. So is the "Choose a list" dialog
	// the star button opens. One entry added to it should therefore show up in both.
	//
	// That is a much smaller lever than the alternative, which was cloning VRChat's
	// AvatarContentSection and driving its render method by obfuscated name. Every type and member
	// touched here is readable and public:
	//
	//   VRC.Core.API.Favorites                       -> FavoriteArea
	//   FavoriteArea._avatars                        -> List<FavoriteListModel>
	//   new FavoriteListModel()                      -> public parameterless ctor
	//   FavoriteListModel.ReplaceFavoritesIndexed()  -> public; fills the list with our own members
	//   new FavoriteModel { type, contentId }        -> public
	//
	// NOTHING IS SENT TO VRCHAT. The list is built in memory and populated from the Archive; we
	// never call FetchMembers, and Populated is set so the game has no reason to either — a
	// synthetic list id does not exist server-side and asking for its members would 404.
	//
	// Off by default, and removable: switching the toggle off takes the entry back out.
	public class ArchiveFavListModule : IModule
	{
		public override string Name => "ArchiveFavList";

		// Recognisable on sight in a dump, and impossible to confuse with a real fvgrp_ id.
		private const string ListId = "fvgrp_vrchatarchive";
		private const string ListName = "vrchatarchive";
		private const string DisplayName = "VRCHAT ARCHIVE";

		public static string Status = "off";
		public static int Injected { get; private set; }

		private bool _on;
		private float _next;
		private int _lastCount = -1;
		private FavoriteListModel _model;

		public override void OnUpdate()
		{
			try
			{
				// No gate: the Archive category is always on.

				float now = VaClock.Now;
				if (now < _next) return;
				_next = now + 2f;

				var area = Area();
				if (area == null) { Status = "waiting for VRChat's favourites to load"; return; }

				// Re-add rather than assume: VRChat refetches its lists (login, favourites refresh)
				// and rebuilds the collection, which drops anything of ours that was in it.
				if (!Present(area)) Inject(area);
				else Sync(force: false);
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFav] " + e.Message);
			}
		}

		public override void OnShutdown() => Remove();

		// ------------------------------------------------------------------ the collection

		private static FavoriteArea Area()
		{
			try { return API.Favorites; }
			catch { return null; }
		}

		private bool Present(FavoriteArea area)
		{
			try
			{
				var list = area._avatars;
				if (list == null) return false;
				for (int i = 0; i < list.Count; i++)
				{
					var m = list[i];
					if (m != null && string.Equals(m.id, ListId, StringComparison.Ordinal))
					{
						_model = m;
						return true;
					}
				}
			}
			catch { }
			return false;
		}

		private void Inject(FavoriteArea area)
		{
			try
			{
				var list = area._avatars;
				if (list == null) { Status = "no _avatars collection"; return; }

				var m = new FavoriteListModel
				{
					id = ListId,
					name = ListName,
					displayName = DisplayName,
					type = FavoriteType.Avatar,
					// Not a VRC+ list: ours is not subject to VRChat's per-list cap, and claiming it
					// were would put a subscription lock on a category the user already owns.
					requiresSubscription = false,
					// The one that matters: an unpopulated list is a list the game may decide to go
					// and fetch, and this id does not exist on VRChat's servers.
					Populated = true,
				};

				try { m.ownerId = OwnerId(area); } catch { }

				_model = m;
				list.Add(m);
				_on = true;
				Sync(force: true);

				Status = "category added to VRChat's avatar menu";
				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[ArchiveFav] injected '{DisplayName}' into API.Favorites._avatars (now {list.Count} list(s)).");
			}
			catch (Exception e)
			{
				Status = "could not add the category: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFav] inject failed: " + e);
			}
		}

		// Borrowed from a real list, so our entry belongs to the same account as its neighbours
		// rather than carrying an empty owner.
		private static string OwnerId(FavoriteArea area)
		{
			try
			{
				var list = area._avatars;
				for (int i = 0; i < list.Count; i++)
				{
					var m = list[i];
					if (m == null || string.Equals(m.id, ListId, StringComparison.Ordinal)) continue;
					if (!string.IsNullOrEmpty(m.ownerId)) return m.ownerId;
				}
			}
			catch { }
			return "";
		}

		// ------------------------------------------------------------------ contents

		private void Sync(bool force)
		{
			try
			{
				if (_model == null) return;
				var ids = FavoritesModule.Snapshot();
				if (!force && ids.Count == _lastCount) return;
				_lastCount = ids.Count;

				var members = new Il2CppSystem.Collections.Generic.List<FavoriteModel>();
				foreach (string id in ids)
				{
					if (string.IsNullOrEmpty(id)) continue;
					members.Add(new FavoriteModel { type = FavoriteType.Avatar, contentId = id });
				}

				// The game's own method for swapping a list's members wholesale — public, and the
				// reason none of this needs a network request.
				_model.ReplaceFavoritesIndexed(members);
				Injected = ids.Count;
				Status = Injected + " avatar(s) in the VRCHAT ARCHIVE category";
				VRChatArchiveModPlugin.Logger.LogInfo($"[ArchiveFav] category now holds {Injected} avatar(s).");
			}
			catch (Exception e)
			{
				Status = "could not fill the category: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFav] sync failed: " + e);
			}
		}

		// ------------------------------------------------------------------ undo

		private void Remove()
		{
			try
			{
				var area = Area();
				if (area != null && area._avatars != null)
				{
					var list = area._avatars;
					for (int i = list.Count - 1; i >= 0; i--)
					{
						var m = list[i];
						if (m != null && string.Equals(m.id, ListId, StringComparison.Ordinal))
						{
							list.RemoveAt(i);
							VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveFav] category removed.");
						}
					}
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveFav] remove failed: " + e.Message); }
			_on = false;
			_model = null;
			_lastCount = -1;
			Injected = 0;
			Status = "off";
		}
	}
}
