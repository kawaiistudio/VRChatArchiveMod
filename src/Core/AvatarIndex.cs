using System;
using System.Collections.Generic;
using HarmonyLib;

namespace VRChatArchiveMod.Core
{
	// EVERY AVATAR THE CLIENT PARSES, REMEMBERED — WITHOUT READING THE MENU.
	//
	// Finding "which avatar is on screen" by walking the UI is a losing game on an obfuscated
	// build. The detail pane's own components are renamed every release, the id is held in a field
	// whose index is permuted, and on the avatar you are already WEARING the pane binds no avatar
	// to its buttons at all — so the subtree contains no avtr_ string to find. That is why
	// "Get metadata" answered "no avatar id" on the owner's own avatar: the scan was not failing,
	// there was genuinely nothing there to read.
	//
	// So this stops looking at the UI. VRChat cannot show an avatar it has not parsed, and it
	// parses through ONE door:
	//
	//     VRC.Core.ApiAvatar.SetApiFieldsFromJson(...)
	//
	// Hand-written names, in VRCCore-Standalone, on a class the obfuscator leaves alone — the same
	// reason nameplate text and menu labels are matched on words rather than on paths. A postfix
	// there sees every avatar the client has ever loaded, at the moment it loads it, with the whole
	// record already deserialised: id, name, author, image, tags. The menu then does not have to be
	// interrogated at all; it only has to say which NAME it is showing, which is the one thing it
	// displays honestly in every language and on every build.
	//
	// TWO RULES THIS FILE KEEPS
	//
	//   1. The hook does the least possible work. It runs on VRChat's parse path, several hundred
	//      times while a list loads, and every property read crosses into il2cpp. Six strings are
	//      taken and nothing else; anything richer is fetched by id, later, off this path.
	//
	//   2. Nothing here throws. The postfix sits inside VRChat's own call stack, where an escaping
	//      managed exception crosses the il2cpp boundary and is not a caught exception but a dead
	//      process.
	internal static class AvatarIndex
	{
		internal sealed class Rec
		{
			public string Id = "";
			public string Name = "";
			public string AuthorName = "";
			public string AuthorId = "";
			public string ImageUrl = "";
			public string ThumbUrl = "";
			public float SeenAt;
		}

		private static readonly object _lock = new object();
		private static readonly Dictionary<string, Rec> _byId = new Dictionary<string, Rec>(StringComparer.Ordinal);
		// Display name -> id. Names are not unique across authors, so the LAST one parsed wins: the
		// avatar you just opened is the one VRChat just parsed, which is exactly the tie-break wanted.
		private static readonly Dictionary<string, string> _byName = new Dictionary<string, string>(StringComparer.Ordinal);
		private static string _lastId = "";
		private static float _lastAt;
		private static int _seen;
		private static bool _hooked, _failed;

		internal static int Count { get { lock (_lock) return _byId.Count; } }
		internal static int Seen => _seen;
		internal static bool Armed => _hooked;
		internal static string Status
		{
			get
			{
				if (_failed) return "hook refuse";
				if (!_hooked) return "off";
				lock (_lock) return _byId.Count + " avatar(s) indexes, " + _seen + " parse(s)";
			}
		}

		// ---------------------------------------------------------------- install

		internal static void Install()
		{
			if (_hooked || _failed) return;
			try
			{
				var t = typeof(VRC.Core.ApiAvatar);
				int patched = 0;

				// Two overloads (one takes the raw json Tokens, the other plain objects) and VRChat
				// uses whichever the caller has. Both are patched by NAME rather than by signature:
				// the parameter types are generic interfaces whose interop spelling has moved between
				// builds, and a signature that fails to match would silently patch nothing.
				foreach (var m in AccessTools.GetDeclaredMethods(t))
				{
					if (m == null || m.Name != "SetApiFieldsFromJson") continue;
					try
					{
						VRChatArchiveModPlugin.HarmonyInstance.Patch(m, postfix: new HarmonyMethod(
							typeof(AvatarIndex).GetMethod(nameof(Parsed),
								System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
						patched++;
					}
					catch (Exception e)
					{
						VRChatArchiveModPlugin.Logger.LogWarning("[AvatarIndex] surcharge non patchee : " + e.Message);
					}
				}

				if (patched == 0)
				{
					_failed = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[AvatarIndex] ApiAvatar.SetApiFieldsFromJson introuvable — l'id d'un avatar devra etre devine depuis le menu.");
					return;
				}

				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[AvatarIndex] " + patched + " point(s) de parse d'avatar sous surveillance — "
					+ "l'id vient de l'API, plus du menu.");
			}
			catch (Exception e)
			{
				_failed = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[AvatarIndex] installation impossible : " + e.Message);
			}
		}

		// ---------------------------------------------------------------- the hook
		//
		// __instance only. The parameters are a generic dictionary and a by-ref string whose interop
		// types differ between the two overloads, and Harmony binds injected parameters by NAME —
		// naming them would refuse the patch on one of the two. The record is already filled by the
		// time a postfix runs, so the arguments are not needed.
		private static void Parsed(VRC.Core.ApiAvatar __instance)
		{
			try
			{
				if (__instance == null) return;

				string id = null;
				if (Live.Readable(typeof(VRC.Core.ApiAvatar), "id")) { try { id = __instance.id; } catch { } }
				if (string.IsNullOrEmpty(id) || !id.StartsWith("avtr_", StringComparison.Ordinal)) return;

				var r = new Rec { Id = id };
				// EACH FIELD IS CHECKED BEFORE IT IS READ, AND THE try/catch IS NOT THE CHECK.
				//
				// These are il2cpp proxy getters. When one is bound to a missing-member stub -- and
				// ApiAvatar inherits name/description from the generic ApiContentModel<T>, which this
				// build moved -- reading it is an access violation, which no catch can intercept: the
				// process dies. It died here on every avatar the game parsed. Live.Readable asks once
				// whether the getter is bound to real code; a field that is not simply stays empty,
				// which the index already treats as "unknown".
				var T = typeof(VRC.Core.ApiAvatar);
				if (Live.Readable(T, "name")) { try { r.Name = __instance.name ?? ""; } catch { } }
				if (Live.Readable(T, "authorName")) { try { r.AuthorName = __instance.authorName ?? ""; } catch { } }
				if (Live.Readable(T, "authorId")) { try { r.AuthorId = __instance.authorId ?? ""; } catch { } }
				if (Live.Readable(T, "imageUrl")) { try { r.ImageUrl = __instance.imageUrl ?? ""; } catch { } }
				if (Live.Readable(T, "thumbnailImageUrl")) { try { r.ThumbUrl = __instance.thumbnailImageUrl ?? ""; } catch { } }
				try { r.SeenAt = VaClock.Now; } catch { }

				lock (_lock)
				{
					_seen++;
					_byId[id] = r;
					if (!string.IsNullOrEmpty(r.Name)) _byName[r.Name] = id;
					_lastId = id;
					_lastAt = r.SeenAt;

					// A session that browses the shop can parse thousands. The index is a lookup aid,
					// not an archive: the oldest half goes when it gets silly, so a long session cannot
					// grow this without bound.
					if (_byId.Count > 4000) Trim();
				}

			}
			catch { }
		}

		// Caller already holds the lock.
		private static void Trim()
		{
			try
			{
				var all = new List<Rec>(_byId.Values);
				all.Sort((a, b) => a.SeenAt.CompareTo(b.SeenAt));
				int drop = all.Count / 2;
				for (int i = 0; i < drop; i++)
				{
					var r = all[i];
					_byId.Remove(r.Id);
					// Only unbind the name when it still points at the record being dropped.
					if (!string.IsNullOrEmpty(r.Name) && _byName.TryGetValue(r.Name, out string cur) && cur == r.Id)
						_byName.Remove(r.Name);
				}
			}
			catch { }
		}

		// ---------------------------------------------------------------- registration & lookup

		internal static void Register(string id, string name, string author = null, string image = null)
		{
			if (string.IsNullOrEmpty(id) || !id.StartsWith("avtr_", StringComparison.Ordinal)) return;
			lock (_lock)
			{
				if (!_byId.TryGetValue(id, out var r))
				{
					r = new Rec { Id = id };
					_byId[id] = r;
				}
				if (!string.IsNullOrEmpty(name))
				{
					r.Name = name;
					_byName[name.Trim()] = id;
				}
				if (!string.IsNullOrEmpty(author)) r.AuthorName = author;
				if (!string.IsNullOrEmpty(image)) r.ImageUrl = image;
				r.SeenAt = VaClock.Now;
			}
		}

		internal static Rec ById(string id)
		{
			if (string.IsNullOrEmpty(id)) return null;
			lock (_lock) return _byId.TryGetValue(id, out var r) ? r : null;
		}

		// The name as the menu displays it. Trimmed, because a label may carry padding, and matched
		// exactly first — then case-insensitively, since a heading may be upper-cased for display.
		internal static string IdForName(string shown)
		{
			if (string.IsNullOrEmpty(shown)) return "";
			string want = shown.Trim();
			if (want.Length == 0) return "";
			lock (_lock)
			{
				if (_byName.TryGetValue(want, out string id)) return id;
				foreach (var kv in _byName)
					if (string.Equals(kv.Key, want, StringComparison.OrdinalIgnoreCase)) return kv.Value;
			}
			return "";
		}

		internal static string AuthorFor(string id)
		{
			var r = ById(id);
			return r != null ? r.AuthorName : "";
		}

		// The avatar VRChat parsed most recently, and only while that is still a fair guess. Opening
		// a detail page fetches that avatar, so for the couple of seconds after it the last parse IS
		// the avatar on screen; long after, it is whatever a background list happened to load, which
		// would pin the wrong id on a button. A caller that cannot match by name gets this or nothing.
		internal static string RecentId(float withinSeconds)
		{
			try
			{
				lock (_lock)
				{
					if (string.IsNullOrEmpty(_lastId)) return "";
					float now = VaClock.Now;
					if (now - _lastAt > withinSeconds) return "";
					return _lastId;
				}
			}
			catch { return ""; }
		}
	}
}
