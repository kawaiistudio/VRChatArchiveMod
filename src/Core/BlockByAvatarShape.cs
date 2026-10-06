using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// BLOCKED, SEEN LOCALLY — AND THE OWNER IS THE ONE WHO SPOTTED IT.
	//
	// THE OBSERVATION (same instance, minutes apart, 2026-09-18)
	//
	//   while Briarr had blocked:   VRCPlayer[Remote] …/ForwardDirection/Avatar   kids = 0
	//   after Briarr unblocked:     VRCPlayer[Remote] …/ForwardDirection/Avatar   kids = 51  ("Airi_Skeleton")
	//
	// Same player, same path, same session. When someone blocks you VRChat swaps their avatar for an
	// empty placeholder -- no children, no renderers -- and puts the real one back when they unblock.
	// I had seen the 0 and called it ambiguous; the owner pointed at the visible avatar and was right.
	//
	// WHY THIS ROUTE AND NOT THE OTHERS
	//
	//   * The 1886 boolean (prop_Boolean_17) lands on a different member on 1903 and marked the whole
	//     room as blockers.
	//   * Event 33 carries the truth but the server only sends it when you ENTER an instance -- watched
	//     live through a block AND an unblock, nothing arrives in between.
	//   * The BlockDebug observer reads it per frame off a player class whose members cannot be placed
	//     on this build. Four crashes in one afternoon.
	//
	// This is Transform.childCount. A plain Unity call on an object we already hold: no obfuscated
	// member, no il2cpp collection walk, no hook, nothing off the main thread. It cannot crash the way
	// the others did.
	//
	// WHAT IT REFUSES TO CLAIM
	//
	// An empty avatar also means "still loading" or "failed to load". So an avatar that was NEVER seen
	// full proves nothing, and this says nothing about that player. Only a player whose avatar was
	// observed WITH children and then went empty is reported -- a transition, not a snapshot. That is
	// the difference between an observation and an accusation, and the marker is worth having only if
	// it never makes the second one.
	internal static class BlockByAvatarShape
	{
		private sealed class Seen
		{
			public bool EverFull;      // an avatar with children was observed at least once
			public bool Empty;         // currently empty
			public float ChangedAt;    // when the current state began
		}

		private static readonly Dictionary<string, Seen> _byUser = new Dictionary<string, Seen>(StringComparer.Ordinal);

		/// <summary>User ids whose avatar was full and is now empty: they blocked you.</summary>
		internal static readonly HashSet<string> BlockedMe = new HashSet<string>(StringComparer.Ordinal);

		// A placeholder swap is not instant and an avatar change briefly empties the node too, so a
		// state has to hold before it counts. Two seconds is far longer than a swap and far shorter
		// than anyone would notice.
		private const float SettleSeconds = 2f;

		private static float _next;

		/// <summary>Cheap: four times a second, one childCount per remote player.</summary>
		internal static void Tick()
		{
			float now;
			try { now = VaClock.Now; } catch { return; }
			if (now < _next) return;
			_next = now + 0.25f;

			try
			{
				var roster = Modules.VaTagsModule.Roster;
				if (roster == null) return;
				int n; try { n = roster.Count; } catch { return; }

				for (int i = 0; i < n; i++)
				{
					Modules.VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null || e.IsLocal || string.IsNullOrEmpty(e.UserId)) continue;

					int kids = AvatarChildCount(e.Player);
					if (kids < 0) continue;              // could not look: say nothing

					Seen s;
					if (!_byUser.TryGetValue(e.UserId, out s) || s == null)
					{
						s = new Seen { Empty = kids == 0, EverFull = kids > 0, ChangedAt = now };
						_byUser[e.UserId] = s;
						if (_byUser.Count > 128) Trim();
						continue;
					}

					bool empty = kids == 0;
					if (empty != s.Empty) { s.Empty = empty; s.ChangedAt = now; }
					if (!empty) s.EverFull = true;

					// Only a player seen FULL and now settled EMPTY is reported.
					bool blocked = s.EverFull && s.Empty && (now - s.ChangedAt) >= SettleSeconds;
					if (blocked) { if (BlockedMe.Add(e.UserId)) Say(e.Name, e.UserId, true); }
					else { if (BlockedMe.Remove(e.UserId)) Say(e.Name, e.UserId, false); }
				}
			}
			catch { }
		}

		private static void Say(string name, string uid, bool blocked)
		{
			try
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockShape] " + (string.IsNullOrEmpty(name) ? uid : name)
					+ (blocked ? " : avatar passe de plein a VIDE — cette personne t'a bloque."
					           : " : avatar de nouveau plein — le blocage est leve."));
			}
			catch { }
		}

		// ForwardDirection/Avatar is where VRChat parents the worn avatar; the placeholder it swaps in
		// when you are blocked sits at the same path with nothing under it.
		private static int AvatarChildCount(object player)
		{
			try
			{
				var api = player as VRC.SDKBase.VRCPlayerApi;
				if (api == null) return -1;
				GameObject go; try { go = api.gameObject; } catch { return -1; }
				if (go == null || !NativeGuard.Alive(go)) return -1;

				Transform fwd; try { fwd = go.transform.Find("ForwardDirection"); } catch { return -1; }
				if (fwd == null) return -1;
				Transform av; try { av = fwd.Find("Avatar"); } catch { return -1; }
				if (av == null) return -1;               // no node at all: not the same as empty
				try { return av.childCount; } catch { return -1; }
			}
			catch { return -1; }
		}

		private static void Trim()
		{
			try
			{
				var dead = new List<string>();
				foreach (var kv in _byUser) if (!BlockedMe.Contains(kv.Key)) dead.Add(kv.Key);
				foreach (string k in dead) _byUser.Remove(k);
			}
			catch { }
		}

		internal static void Clear()
		{
			try { _byUser.Clear(); BlockedMe.Clear(); } catch { }
		}
	}
}
