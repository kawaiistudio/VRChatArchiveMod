using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRC.Core;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// VRCHAT ARCHIVE — an EXTRA world favourites list, added beside VRChat's own.
	//
	// Nothing is overwritten: "Favorite Worlds 1..4" keep their contents and their slots, and one
	// more list appears under them. That is possible for worlds where it was not for avatars,
	// because the worlds sidebar renders straight from FavoriteArea._worlds — the rows ARE the
	// list — while the avatar sidebar is composed elsewhere, which is why the avatar equivalent of
	// this module never rendered and the avatars tab had to borrow an existing category instead.
	//
	// WHY THIS ONE IS BUILT BY COPYING. The first attempt at a synthetic favourites record set the
	// six properties that looked necessary — id, name, displayName, type, requiresSubscription,
	// Populated — and crashed the game on menu open. A live capture of a REAL FavoriteListModel
	// showed what that record was actually missing:
	//
	//     _favorites            List<FavoriteModel>
	//     _favoritesById        Dictionary<string, FavoriteModel>
	//     _favoritesByContentId Dictionary<string, FavoriteModel>
	//     _FieldsMissingFromOriginalResponse   HashSet<string>   (non-null)
	//     Endpoint / supportedPlatforms / visibility / ownerId / ownerDisplayName
	//
	// Left null, those are dereferenced by native code the moment the menu draws. So this takes a
	// real entry as a TEMPLATE and copies every property off it, then overrides only the handful
	// that identify the list. Anything VRChat adds in a future update is copied too, without this
	// file having to know it exists.
	//
	// The collections are always FRESH. Copying those by reference would hand our list the same
	// backing store as a real one, and adding a world to ours would add it to theirs.
	//
	// _worlds, never _vrcPlusWorlds: the VRC+ lists carry requiresSubscription and go read-only the
	// day the subscription lapses — the same trap that put a "Resubscribe to VRC+" banner over the
	// avatars category when it borrowed slot 5.
	// The three shelves share every line of this: only WHICH collection, WHICH FavoriteType and
	// WHERE the ids come from differ. Subclassing rather than copying means a fix to the record
	// shape — the thing that was wrong for a year — is fixed once for all of them.
	public abstract class FavListInjector : IModule
	{
		protected abstract string ListId { get; }
		protected abstract string ListName { get; }
		protected virtual string DisplayName => "VRCHAT ARCHIVE";
		protected abstract FavoriteType Kind { get; }
		protected abstract Il2CppSystem.Collections.Generic.List<FavoriteListModel> Collection(FavoriteArea area);
		protected abstract List<string> Ids();
		protected abstract int Rev { get; }

		public string Status = "off";
		public bool Active { get; private set; }

		protected virtual bool Enabled => true;

		private FavoriteListModel _model;
		private float _next;
		private int _lastRev = -1;

		public override void OnUpdate()
		{
			try
			{
				if (!Enabled)
				{
					if (_model != null) Remove();
					Status = "off"; Active = false;
					return;
				}

				float now = VaClock.Now;
				if (now < _next) return;
				_next = now + 2f;

				var area = Area();
				if (area == null) { Status = "waiting for VRChat's favourites"; return; }

				if (_model == null || !Present(area)) Inject(area);
				else
				{
					// The trial survived its window: the section is genuinely accepted.
					if (_trial && VaClock.Now > _trialUntil)
					{
						DisarmTrial();
						VRChatArchiveModPlugin.Logger.LogInfo("[" + Name + "] forced section survived — it works.");
						Status = "forced section accepted";
					}

					int rev = Rev;
					if (rev != _lastRev) { _lastRev = rev; Fill(); }
				}
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[" + Name + "] " + e.Message);
			}
		}

		public override void OnSceneLoaded(int buildIndex) { _model = null; _lastRev = -1; }
		public override void OnShutdown() => Remove();

		private static FavoriteArea Area()
		{
			try { return API.Favorites; } catch { return null; }
		}

		private bool Present(FavoriteArea area)
		{
			try
			{
				// BY OBJECT, NOT BY ID. Some of VRChat's own lists carry an EMPTY id, so comparing
				// ids never matched what we had just taken — and the module re-borrowed it on every
				// tick, filling the log with the same line twice a second forever. The reference we
				// are holding is the identity that cannot be wrong.
				if (_model == null) return false;
				var list = Collection(area);
				if (list == null) return false;
				for (int i = 0; i < list.Count; i++)
				{
					var m = list[i];
					if (m == null) continue;
					if (ReferenceEquals(m, _model)) return true;
					try { if (m.Pointer == _model.Pointer) return true; } catch { }
				}
			}
			catch { }
			return false;
		}

		// ------------------------------------------------------------------ borrow

		// BORROW A REAL LIST, DO NOT BUILD ONE.
		//
		// The first version added a synthetic FavoriteListModel to the collection. It was copied
		// field-for-field from a real one and it still broke the menu: every world list vanished,
		// not just ours. The log said why — "VP MainMenuWorlds OnPageAddedToStack / Start /
		// OnPageShown": the sidebar is a Voyager PAGE that BUILDS ITSELF when you navigate to it.
		// It does not read the collection continuously, so an entry that appears at the wrong point
		// in that lifecycle does not simply fail to show — the whole build fails, and you get an
		// empty sidebar.
		//
		// So nothing is added any more. We take a list VRChat itself created — every field correct,
		// already known to the page, already in the right place in its lifecycle — rename the text
		// it displays, and put OUR contents in it through ReplaceFavoritesIndexed, which is the same
		// call the game uses to fill one. It is the approach that has been working for avatars all
		// along.
		//
		// The list we borrow is an EMPTY one by preference, so nothing of the user's is displaced.
		// If every list has contents we take none and say so, rather than hiding somebody's worlds.
		private string _borrowedId = "";
		private string _origName = "";

		// ---------------------------------------------------------------- forced new section
		//
		// Adding a section of our own SHOULD work — the collection is a plain list and the page
		// reads it. When it was tried, every world list vanished instead, and the reason was never
		// established because the attempt was reverted before anyone read the exception.
		//
		// So this is the same experiment run properly: inject, then WATCH. Unity's log callback is
		// already subscribed for the menu log, so any exception thrown while our entry is present is
		// captured, written down, and our entry is pulled straight back out. The menu repairs
		// itself on the next navigation and we are left holding the reason it failed — which is the
		// thing that was missing.
		//
		// Off by default. Switching it on costs one broken sidebar until you navigate away, and
		// buys the exact error.
		private bool _trial;
		private float _trialUntil;
		private static string _lastFailure = "";
		public static string LastFailure => _lastFailure;

		private void InjectSynthetic(FavoriteArea area)
		{
			try
			{
				var list = Collection(area);
				if (!Settled(list)) return;

				FavoriteListModel template = null;
				for (int i = 0; i < list.Count && template == null; i++)
					if (list[i] != null) template = list[i];
				if (template == null) { Status = "no template"; return; }

				var m = new FavoriteListModel();
				CopyFrom(template, m);
				FreshCollections(m);
				m.id = ListId;
				m.name = ListName;
				m.displayName = DisplayName;
				m.type = Kind;
				m.requiresSubscription = false;
				m.Populated = true;

				_model = m;
				_borrowedId = ListId;
				list.Add(m);
				Active = true;
				_lastRev = -1;

				// Arm the watchdog. Anything that throws in the next few seconds is very likely the
				// page choking on this entry, so we take it back out and keep the message.
				_trial = true;
				_trialUntil = VaClock.Now + 8f;
				// Through Core.UnityLog: hooking Application.CallLogCallback needs no il2cpp delegate,
				// and a watchdog that cannot start is a watchdog that never catches the crash it exists
				// for -- the forced section would stay in place after breaking the page.
				_watch ??= OnTrialLog;
				Core.UnityLog.Subscribe(_watch, Name);

				Status = "forced section added — watching for errors";
				VRChatArchiveModPlugin.Logger.LogInfo($"[{Name}] FORCED a new section into {Kind}; watchdog armed for 8s.");
			}
			catch (Exception e)
			{
				Status = "forced inject failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[" + Name + "] forced inject: " + e);
			}
		}

		private Action<string, string, UnityEngine.LogType> _watch;

		private void OnTrialLog(string condition, string stack, UnityEngine.LogType type)
		{
			try
			{
				if (!_trial) return;
				if (type != UnityEngine.LogType.Exception && type != UnityEngine.LogType.Error) return;

				_lastFailure = condition;
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[" + Name + "] the forced section threw — pulling it back out. REASON: " + condition);
				if (!string.IsNullOrEmpty(stack))
					VRChatArchiveModPlugin.Logger.LogWarning("[" + Name + "] at: " + stack);

				DisarmTrial();
				Remove();
				Status = "forced section rejected: " + Trunc(condition, 60);
			}
			catch { }
		}

		private void DisarmTrial()
		{
			_trial = false;
			try { Core.UnityLog.Unsubscribe(_watch); } catch { }
		}

		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

		// Every readable+writable property, copied across. Reflection rather than a hand-written
		// list precisely because a hand-written list is what failed before: the fields that mattered
		// were the ones nobody thought to name.
		private static int CopyFrom(object from, object to)
		{
			int n = 0;
			try
			{
				var t = from.GetType();
				foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (p.GetIndexParameters().Length > 0) continue;
					if (!p.CanRead || !p.CanWrite) continue;
					string nm = p.Name;
					if (nm == "Pointer" || nm == "ObjectClass" || nm == "WasCollected") continue;
					try { p.SetValue(to, p.GetValue(from)); n++; } catch { }
				}
			}
			catch { }
			return n;
		}

		// FRESH containers. CopyFrom hands over references, so without this our list and a real one
		// share the same backing store and adding to ours adds to theirs.
		private static void FreshCollections(object m)
		{
			try
			{
				var t = m.GetType();
				Set(t, m, "_favorites", new Il2CppSystem.Collections.Generic.List<FavoriteModel>());
				Set(t, m, "_favoritesById", new Il2CppSystem.Collections.Generic.Dictionary<string, FavoriteModel>());
				Set(t, m, "_favoritesByContentId", new Il2CppSystem.Collections.Generic.Dictionary<string, FavoriteModel>());
				Set(t, m, "_FieldsMissingFromOriginalResponse_k__BackingField",
					new Il2CppSystem.Collections.Generic.HashSet<string>());
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[FavList] fresh collections: " + e.Message); }
		}

		private static void Set(Type t, object on, string prop, object value)
		{
			try
			{
				var p = t.GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (p != null && p.CanWrite) { p.SetValue(on, value); return; }
				var f = t.GetField(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (f != null) f.SetValue(on, value);
			}
			catch { }
		}

		// WAIT UNTIL VRCHAT HAS FINISHED BUILDING ITS OWN.
		//
		// The log caught the real fault: our entry went in while the collection held ZERO lists —
		// during load, before the game had created any. Ours was then the first and only entry, and
		// whatever the page does when it later populates, it did not survive that. Every list
		// disappearing rather than just ours is exactly what a failed build looks like.
		//
		// VRChat ships four world lists, four avatar lists and several friend groups, so anything
		// under two means it is still loading. It also has to hold steady for a couple of seconds:
		// a collection caught mid-populate has a count too, and it is not done.
		private int _lastCount = -1;
		private float _steadySince;

		private bool Settled(Il2CppSystem.Collections.Generic.List<FavoriteListModel> list)
		{
			if (list == null) { Status = "no collection"; return false; }

			int n = list.Count;
			if (n < 2) { Status = "waiting — VRChat has " + n + " list(s) so far"; _lastCount = n; return false; }

			float now = VaClock.Now;
			if (n != _lastCount) { _lastCount = n; _steadySince = now; }
			if (now - _steadySince < 2f) { Status = "waiting — the list is still filling"; return false; }
			return true;
		}

		// Which SLOT of the collection this module takes over, 0-based and in sidebar order.
		protected virtual int WantedSlot() => -1;

		private void Inject(FavoriteArea area)
		{
			// The forced experiment is a compile-time constant now (ModConfig.ForceNewSection):
			// there is no registered module to try it from, so it stopped being a setting.
			if (ModConfig.ForceNewSection) { InjectSynthetic(area); return; }

			try
			{
				var list = Collection(area);
				if (!Settled(list)) return;

				// BY NAME, NOT BY GUESSWORK.
				//
				// Two attempts at detecting an "empty" list both took a full one — avatars1 with 50
				// in it, Favorite Worlds 1 with 25. The reason is that a list's contents are NOT in
				// _favorites when we look: VRChat draws "25/100" from its own record long before that
				// collection is filled, so every list reads as empty and the first one wins.
				//
				// Detection was the wrong idea. The user knows which shelf they do not use, so they
				// name it. If the name is not there, nothing is renamed — never a fallback that
				// might take somebody's list.
				// BY SLOT, NOT BY NAME.
				//
				// Matching names failed twice for two different reasons: the display name is only
				// stored when a list has been renamed (the rest carry worlds2/worlds3/worlds4 and
				// the menu derives "Favorite Worlds 4" for display), and a name is a moving target
				// across locales and VRChat versions anyway.
				//
				// The dumps give the structure directly: _worlds holds four lists in the order the
				// sidebar shows them, _friends holds the friend groups in theirs. So the shelf is
				// identified by its POSITION — slot 3 is the fourth world list, slot 2 is the third
				// friend group — which is exactly what the user points at on screen and does not
				// depend on what anything is called.
				int slot = WantedSlot();
				// A negative slot means this shelf has no configured position — the avatars one, which
				// is covered by the category hijack instead. It logged "slot -1 out of range" every
				// two seconds forever; silence is the correct output for "not my job".
				if (slot < 0) { Status = "not configured"; return; }
				FavoriteListModel pick = null;
				var names = new List<string>();

				for (int i = 0; i < list.Count; i++)
				{
					var c = list[i];
					if (c == null) continue;
					string nm2 = "";
					try { nm2 = c.name ?? ""; } catch { }
					names.Add(i + ":" + nm2);
					if (i == slot) pick = c;
				}

				if (pick == null)
				{
					Status = "slot " + slot + " does not exist (" + list.Count + " list(s))";
					VRChatArchiveModPlugin.Logger.LogWarning(
						$"[{Name}] slot {slot} out of range. Slots present: {string.Join(", ", names)}");
					return;
				}

				_model = pick;
				_borrowedId = pick.id ?? "";
				try { _origName = pick.displayName ?? ""; } catch { }

				// Only the label. The record stays entirely VRChat's.
				try { pick.displayName = DisplayName; } catch { }

				Active = true;
				_lastRev = -1;

				Status = "borrowed '" + _origName + "'";
				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[{Name}] borrowed VRChat's own empty list '{_origName}' (id {_borrowedId}) and renamed it '{DisplayName}'.");
			}
			catch (Exception e)
			{
				Status = "could not borrow a list: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[" + Name + "] borrow failed: " + e);
			}
		}

		// ------------------------------------------------------------------ contents

		private void Fill()
		{
			try
			{
				if (_model == null) return;

				var ids = Ids();
				var members = new Il2CppSystem.Collections.Generic.List<FavoriteModel>();
				foreach (string id in ids)
				{
					if (string.IsNullOrEmpty(id)) continue;
					members.Add(new FavoriteModel { type = Kind, contentId = id });
				}

				_model.ReplaceFavoritesIndexed(members);
				Status = ids.Count + " item(s)";
				// The real number, and where it came from — so an empty section is unambiguous:
				// 0 here means the SOURCE is empty (the client relay), not the injection.
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[" + Name + "] filled with " + members.Count + " member(s) from " + ids.Count
					+ " id(s). If 0, the client bridge returned nothing for this kind.");
			}
			catch (Exception e)
			{
				Status = "could not fill the list: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[" + Name + "] fill: " + e.Message);
			}
		}

		// Hand the list back exactly as it was: its own name, and empty again — we only ever put
		// our own contents in it, so clearing is what returns it to the state we found it in.
		private void Remove()
		{
			try
			{
				if (_model != null)
				{
					if (!string.IsNullOrEmpty(_origName)) { try { _model.displayName = _origName; } catch { } }
					try { _model.ReplaceFavoritesIndexed(new Il2CppSystem.Collections.Generic.List<FavoriteModel>()); } catch { }
				}
			}
			catch { }
			_model = null;
			_borrowedId = "";
			Active = false;
		}
	}

	// ---------------------------------------------------------------- the three shelves

	// WORLDS. The sidebar renders straight from _worlds, so an extra entry there simply appears
	// under "Favorite Worlds 1-4" without touching them.
	public class WorldFavListModule : FavListInjector
	{
		public override string Name => "WorldFavList";
		protected override string ListId => "fvgrp_vrchatarchive_worlds";
		protected override string ListName => "vrchatarchive_worlds";
		protected override FavoriteType Kind => FavoriteType.World;
		// Unconditional since 2026-09-01: the Favorites/* switches went, and whether this runs is
		// decided by its Register line in Plugin.cs (currently disarmed).
		protected override bool Enabled => true;
		protected override Il2CppSystem.Collections.Generic.List<FavoriteListModel> Collection(FavoriteArea a) => a._worlds;
		protected override int WantedSlot() => ModConfig.WorldListSlot;
		protected override List<string> Ids() => WorldFavoritesModule.Snapshot();
		protected override int Rev => WorldFavoritesModule.Revision;
	}

	// SOCIAL. _friends holds the friend groups, and a live capture confirmed it carries three real
	// FavoriteListModels of FavoriteType.Friend — which is what made this shelf possible at all.
	public class UserFavListModule : FavListInjector
	{
		public override string Name => "UserFavList";
		protected override string ListId => "fvgrp_vrchatarchive_users";
		protected override string ListName => "vrchatarchive_users";
		protected override FavoriteType Kind => FavoriteType.Friend;
		protected override bool Enabled => true;   // see WorldFavListModule
		protected override Il2CppSystem.Collections.Generic.List<FavoriteListModel> Collection(FavoriteArea a) => a._friends;
		protected override int WantedSlot() => ModConfig.SocialListSlot;
		protected override List<string> Ids() => UserFavoritesModule.Snapshot();
		protected override int Rev => UserFavoritesModule.Revision;
	}

	// AVATARS. Kept OFF by default and separate from the working avatars tab: the avatar sidebar is
	// composed elsewhere, so an entry in _avatars may still not render — that is exactly why the
	// avatars tab borrows a category instead. With the record shape finally correct this is worth
	// re-testing, but not worth switching on for everyone before it is seen to work.
	public class AvatarFavListModule : FavListInjector
	{
		public override string Name => "AvatarFavList";
		protected override string ListId => "fvgrp_vrchatarchive_avatars";
		protected override string ListName => "vrchatarchive_avatars";
		protected override FavoriteType Kind => FavoriteType.Avatar;
		// Favorites/AvatarList went 2026-09-01; the Register line in Plugin.cs (commented out) is
		// the switch now, so Enabled has nothing left to read.
		protected override bool Enabled => true;
		protected override Il2CppSystem.Collections.Generic.List<FavoriteListModel> Collection(FavoriteArea a) => a._avatars;
		protected override List<string> Ids() => FavoritesModule.Snapshot();
		protected override int Rev => FavoritesModule.Revision;
	}
}
