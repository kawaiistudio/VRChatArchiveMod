using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VRChatArchiveMod.Core
{
	// WHO BLOCKED YOU, READ OFF THE WIRE.
	//
	// HOW THIS WAS ESTABLISHED (measured, from the owner's own Photon captures, 2026-09-18)
	//
	// Event 33 is "moderation / instance control", and its subcode lives in key 0. Subtype 21 arrives
	// in two shapes, and the pair proved each other:
	//
	//   delta     {0=(byte)21, 1=<actor>, 10=<bool>, 11=<bool>}
	//   snapshot  {0=(byte)21, 10=Int32[]{actors…}, 11=Int32[]{actors…}}
	//
	// THE CONTROLLED TEST THAT SETTLED IT (2026-09-18, 22:08-22:11, owner + CyberChimp201)
	//
	// A friend blocked the owner, then unblocked, both staying in the same instance. The owner blocked
	// nobody at any point. Captured from actor 0 (the server), verbatim:
	//
	//   join      {0=(byte)20, 3=String[][1]{"58aa78"}, 12=Int32[][1]{5}}   <- names the user by ID
	//   join      {0=(byte)21, 10=Int32[][0], 11=Int32[][0]}                <- snapshot, both EMPTY
	//   blocked   {0=(byte)21, 1=2, 10=true,  11=false}
	//   unblocked {0=(byte)21, 1=2, 10=false, 11=false}
	//
	// Actor 2 is the friend (usr_58aa785b, the same id the subtype-20 event names). KEY 10 RISES WHEN
	// SOMEONE BLOCKS YOU AND FALLS WHEN THEY UNBLOCK YOU. Key 11 stayed false throughout.
	//
	// This corrects two things that had been asserted here on weaker evidence:
	//
	//   * The keys were mapped the wrong way round. Harmonyasha's reference documents the OUTGOING
	//     form a client sends when IT blocks someone; what arrives FROM the server is not that packet,
	//     and reasoning from one to the other was a guess. The marker had been reading key 11.
	//   * "Event 33 only arrives when you ENTER an instance" is false. The join snapshot came back
	//     EMPTY both times and every real state change arrived afterwards as a per-actor delta. The
	//     earlier test saw nothing because nothing was decoding the payload, not because nothing came.
	//
	// Key 11 is NOT claimed here. It was false in every frame of this test, so its meaning is untested
	// -- the owner blocking someone is the experiment that would settle it, and it has not been run.
	// It is kept and reported separately rather than folded into the marker.
	//
	// Why this route and not a field on the player: it needs no member index, no delegate and no
	// endpoint, so it survives VRChat permuting its members on the next build. A memory-offset
	// candidate (VRC.Player+0x52) was tested the same evening and REFUTED -- it did not change when
	// the block was lifted, so it means "remote", not "blocked".
	internal static class Event33Moderation
	{
		/// <summary>Actor numbers the server says have blocked us — KEY 10, measured (see above).</summary>
		internal static readonly HashSet<int> BlockedMeActors = new HashSet<int>();

		/// <summary>Key 11: the second axis, meaning UNTESTED. Reported, never used to mark anyone.</summary>
		internal static readonly HashSet<int> OtherAxisActors = new HashSet<int>();

		/// <summary>
		/// True once the server has stated the moderation state at least once this session.
		///
		/// The caller needs this to tell "the wire says nobody has blocked you" from "the wire has not
		/// spoken yet" -- an empty set means both, and they demand opposite behaviour. Without it the
		/// marker could only ever be SET by the wire, never cleared: the unblock delta empties the set,
		/// the caller sees an empty set, skips the whole branch, and leaves [B] on a person who had just
		/// lifted their block. Measured 2026-09-18: {1=2, 10=false} does arrive, so this is reachable.
		/// </summary>
		internal static bool HasSpoken { get; private set; }

		private static readonly Regex RxSub = new Regex(@"0=\(byte\)(\d+)", RegexOptions.Compiled);
		private static readonly Regex RxActor = new Regex(@"(?<![0-9])1=(\d+)", RegexOptions.Compiled);
		private static readonly Regex RxFlag10 = new Regex(@"10=(true|false)", RegexOptions.Compiled);
		private static readonly Regex RxFlag11 = new Regex(@"11=(true|false)", RegexOptions.Compiled);
		private static readonly Regex RxList10 = new Regex(@"10=Int32\[\]\[\d+\]\{([^}]*)\}", RegexOptions.Compiled);
		private static readonly Regex RxList11 = new Regex(@"11=Int32\[\]\[\d+\]\{([^}]*)\}", RegexOptions.Compiled);

		// AN EMPTY LIST HAS NO BRACES. The snapshot that actually arrives on joining reads
		// "10=Int32[][0], 11=Int32[][0]" -- no {...} at all -- so the two regexes above did not match it
		// and an empty snapshot was silently treated as "not a snapshot". The sets then kept whatever
		// the PREVIOUS instance had put there, which is how a stale [B] would follow you into a room
		// where nobody has blocked you. These match the list FORM, present or empty, and that is what
		// decides whether this payload is a snapshot.
		private static readonly Regex RxForm10 = new Regex(@"10=Int32\[\]\[\d+\]", RegexOptions.Compiled);
		private static readonly Regex RxForm11 = new Regex(@"11=Int32\[\]\[\d+\]", RegexOptions.Compiled);

		/// <summary>
		/// Reads one decoded event-33 payload. The input is the string this mod itself renders, whose
		/// shape is ours and therefore stable -- parsing it keeps every il2cpp read on the one path that
		/// has been running all session without incident, instead of adding another collection walk.
		/// </summary>
		internal static void Consume(string described)
		{
			if (string.IsNullOrEmpty(described)) return;
			try
			{
				var sub = RxSub.Match(described);
				if (!sub.Success || sub.Groups[1].Value != "21") return;

				if (RxForm10.IsMatch(described) || RxForm11.IsMatch(described))
				{
					// The snapshot REPLACES what we knew: it is the server's complete statement, and an
					// empty one states that nobody is flagged -- which must clear, not be ignored.
					BlockedMeActors.Clear();
					OtherAxisActors.Clear();
					var l10 = RxList10.Match(described);
					var l11 = RxList11.Match(described);
					AddAll(l10.Success ? l10.Groups[1].Value : "", BlockedMeActors);   // key 10, measured
					AddAll(l11.Success ? l11.Groups[1].Value : "", OtherAxisActors);
					HasSpoken = true;
					Report("instantane a la connexion");
					return;
				}

				var who = RxActor.Match(described);
				var f10 = RxFlag10.Match(described);
				var f11 = RxFlag11.Match(described);
				if (!who.Success || (!f10.Success && !f11.Success)) return;

				int actor;
				if (!int.TryParse(who.Groups[1].Value, out actor)) return;

				// KEY 10 IS THE BLOCK AGAINST YOU. Measured both ways in one sitting: true the moment the
				// friend blocked, false the moment they unblocked, with the owner blocking nobody.
				if (f10.Success) Set(BlockedMeActors, actor, f10.Groups[1].Value == "true");
				if (f11.Success) Set(OtherAxisActors, actor, f11.Groups[1].Value == "true");
				HasSpoken = true;
				Report("changement sur l'acteur " + actor);
			}
			catch { }
		}

		private static void Set(HashSet<int> set, int actor, bool on)
		{
			if (on) set.Add(actor); else set.Remove(actor);
		}

		private static void AddAll(string csv, HashSet<int> set)
		{
			if (string.IsNullOrEmpty(csv)) return;
			foreach (string part in csv.Split(','))
			{
				int n;
				if (int.TryParse(part.Trim(), out n)) set.Add(n);
			}
		}

		private static string _last;

		// BOTH keys are printed, key 11 plainly labelled as unidentified rather than given a meaning it
		// has not earned. If it ever turns out to carry something, this line is where it will show up.
		private static void Report(string why)
		{
			try
			{
				string line = why + " | 10 (T'ONT BLOQUE) : " + Names(BlockedMeActors)
					+ " | 11 (second axe, non identifie) : " + Names(OtherAxisActors);
				if (line == _last) return;
				_last = line;
				VRChatArchiveModPlugin.Logger.LogWarning("[EV33-MOD] " + line);
			}
			catch { }
		}

		private static string Names(HashSet<int> actors)
		{
			if (actors.Count == 0) return "(personne)";
			var parts = new List<string>();
			foreach (int a in actors)
			{
				string n = NameOfActor(a);
				parts.Add(n == null ? a.ToString() : (a + "=" + n));
			}
			return string.Join(", ", parts);
		}

		/// <summary>Actor number -> display name, through the roster the mod already keeps. VRChat's
		/// photon actor number is the same value VRCPlayerApi calls playerId.</summary>
		internal static string NameOfActor(int actor)
		{
			try
			{
				var roster = Modules.VaTagsModule.Roster;
				if (roster == null) return null;
				int n; try { n = roster.Count; } catch { return null; }
				for (int i = 0; i < n; i++)
				{
					Modules.VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null) continue;
					int pid = e.PlayerId;   // the roster stores VRC.Player, so cast to VRCPlayerApi was always null
					if (pid == actor) return e.Name;
				}
			}
			catch { }
			return null;
		}

		/// <summary>User ids of everyone the wire says has blocked us, resolved through the roster.</summary>
		internal static HashSet<string> BlockedMeUserIds()
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				var roster = Modules.VaTagsModule.Roster;
				if (roster == null) return set;
				int n; try { n = roster.Count; } catch { return set; }
				for (int i = 0; i < n; i++)
				{
					Modules.VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null || string.IsNullOrEmpty(e.UserId)) continue;
					int pid = e.PlayerId;   // the roster stores VRC.Player, so cast to VRCPlayerApi was always null
					if (BlockedMeActors.Contains(pid)) set.Add(e.UserId);
				}
			}
			catch { }
			return set;
		}

		/// <summary>User ids on the SECOND moderation axis (key 11), resolved through the roster. Meaning
		/// unconfirmed — kept separate from key 10 so a mutual-block test can settle it without ever
		/// mixing the two directions.</summary>
		internal static HashSet<string> OtherAxisUserIds()
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				var roster = Modules.VaTagsModule.Roster;
				if (roster == null) return set;
				int n; try { n = roster.Count; } catch { return set; }
				for (int i = 0; i < n; i++)
				{
					Modules.VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null || string.IsNullOrEmpty(e.UserId)) continue;
					int pid = e.PlayerId;
					if (OtherAxisActors.Contains(pid)) set.Add(e.UserId);
				}
			}
			catch { }
			return set;
		}

		internal static void Clear()
		{
			// HasSpoken is NOT reset: it records that this SESSION has seen the server state at all.
			try { BlockedMeActors.Clear(); OtherAxisActors.Clear(); _last = null; } catch { }
		}
	}
}
