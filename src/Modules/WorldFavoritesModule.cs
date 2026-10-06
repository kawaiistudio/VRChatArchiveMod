using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// The world ids behind the VRCHAT ARCHIVE world list. Data only — WorldFavListModule renders it.
	//
	// Same shape as FavoritesModule (which does this for avatars), and it goes through the same
	// route: the desktop client's local bridge, never VRChat's API. The bridge already understands
	// worlds — its relay maps kind=world to the wrld_ prefix and the site's world favourites
	// endpoint — so nothing new is asked of the server.
	public class WorldFavoritesModule : IModule
	{
		public override string Name => "WorldFavorites";

		private static readonly List<string> Ids = new List<string>();
		private static readonly object Gate = new object();

		// Bumped on every change, so the renderer can notice without polling the list itself.
		public static int Revision { get; private set; }
		private static void Bump() { unchecked { Revision++; } }

		public static int Count { get { lock (Gate) return Ids.Count; } }
		public static List<string> Snapshot() { lock (Gate) return new List<string>(Ids); }
		public static string Status = "idle";

		private float _next;
		private static bool _gotOnce;

		public override void OnUpdate()
		{
			try
			{
				// No config gate: Favorites/WorldList went 2026-09-01. Whether this runs at all is
				// decided by its Register line in Plugin.cs (currently disarmed).
				float now = VaClock.Now;
				if (now < _next) return;

				// FAST UNTIL IT WORKS, then slow. The first attempt often fires before the mod has
				// connected to the desktop bridge, gets "needs the desktop client", and — with a flat
				// 120 s interval — left the section empty for two minutes for no reason. Retry every
				// 5 s until one refresh actually returns data, then settle to the human timescale.
				_next = now + (_gotOnce ? 120f : 5f);
				_ = RefreshAsync();
			}
			catch { }
		}

		public static async System.Threading.Tasks.Task RefreshAsync()
		{
			try
			{
				// Client-only, like the avatar favourites: the mod never holds the account key.
				if (!VaAuth.InsideClient) { Status = "needs the desktop client"; return; }

				var (ok, raw, code) = await VaAuth.FavRawAsync(
					"{\"action\":\"list\",\"kind\":\"world\",\"id\":\"\"}");
				if (!ok) { Status = "list failed (" + code + ")"; return; }

				var found = Collect(raw);
				lock (Gate) { Ids.Clear(); Ids.AddRange(found); }
				if (found.Count > 0) _gotOnce = true;
				Bump();

				Status = found.Count + " world(s)";
				VRChatArchiveModPlugin.Logger.LogInfo($"[WorldFav] {found.Count} world id(s) from the Archive.");
			}
			catch (Exception e)
			{
				Status = "refresh failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[WorldFav] refresh: " + e.Message);
			}
		}

		// Every wrld_ id in the response, in order, without duplicates. Scraping the ids out rather
		// than modelling the payload keeps this working when the server's shape changes — the same
		// approach the avatar list uses for avtr_.
		private static List<string> Collect(string raw)
		{
			var outp = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(raw)) return outp;

			const string pfx = "wrld_";
			int i = 0;
			while (true)
			{
				int at = raw.IndexOf(pfx, i, StringComparison.Ordinal);
				if (at < 0) break;

				int end = at + pfx.Length;
				while (end < raw.Length)
				{
					char c = raw[end];
					if (char.IsLetterOrDigit(c) || c == '-') end++;
					else break;
				}

				string id = raw.Substring(at, end - at);
				// wrld_ + a 36-character uuid; anything shorter is a truncated or malformed match.
				if (id.Length >= pfx.Length + 30 && seen.Add(id)) outp.Add(id);
				i = end;
			}
			return outp;
		}
	}
}
