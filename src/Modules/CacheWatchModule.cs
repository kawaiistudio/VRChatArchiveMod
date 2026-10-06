using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// EVERY AVATAR BUNDLE THAT COMES THROUGH, WITH ITS ID, AS IT ARRIVES.
	//
	// This feeds the CACHE LOGS half of the Launch Pad console. It was first wired to the desktop
	// client's cache viewer, which records the same avatars — but that only reports what the CLIENT
	// notices, after the fact. The mod is already inside the game while the bundle is being loaded,
	// so it sees them first and it sees all of them.
	//
	// The source is the roster the mod already maintains (VaTagsModule), where every player carries
	// the avatar id they are currently wearing. An id appearing there IS a bundle the game has just
	// pulled from cache or downloaded — which is exactly what the owner asked to see: "tous les
	// bundles dans le cache VRChat, les id doivent être loggés dès qu'un nouvel assetbundle est là".
	//
	// IDS, NOT NAMES. A name is often missing (VRChat resolves it asynchronously, and never at all for
	// a blocked or private avatar) and it is not what you can act on afterwards; the id is the thing
	// you paste into the archive. The name is added when it is known, never waited for.
	public class CacheWatchModule : IModule
	{
		public override string Name => "CacheWatch";

		// Avatar ids already announced. Session-scoped: re-announcing the same avatar every time
		// somebody re-enters range would drown the feed in things you already saw.
		private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// The roster is rebuilt a few times a second; there is nothing to gain from looking oftener
		// than a bundle can possibly load.
		private float _next;

		public override void OnUpdate()
		{
			try
			{
				float now = VaClock.Now;
				if (now < _next) return;
				_next = now + 1f;

				var roster = VaTagsModule.Roster;
				for (int i = 0; i < roster.Count; i++)
				{
					var p = roster[i];
					if (p == null) continue;

					string id = p.AvatarId;
					if (string.IsNullOrWhiteSpace(id)) continue;
					if (!_seen.Add(id)) continue;

					// "<id>  <name>  · worn by <player>" — id first, because it is the column you scan
					// for and the only part that is always present.
					string line = id;
					if (!string.IsNullOrWhiteSpace(p.AvatarName)) line += "  " + p.AvatarName;
					if (!string.IsNullOrWhiteSpace(p.Name)) line += "  ·  " + p.Name;

					ArchiveFeed.Add(ArchiveFeed.Kind.Cache, line);
				}
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// KEPT ACROSS WORLDS, deliberately. The set answers "have I already told you about this
			// bundle", and that stays true after a world change — clearing it would re-announce every
			// avatar that follows you from one instance to the next.
		}
	}
}
