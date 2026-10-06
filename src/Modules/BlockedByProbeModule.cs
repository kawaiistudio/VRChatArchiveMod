using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Core;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// WHO BLOCKED ME — the data half. Draws nothing; it fills two sets the player lists read.
	//
	// VRChat never tells you outright that someone blocked you, but its own API model does carry both
	// directions: VRC.Core.ApiPlayerModeration has FetchAllMine (what I did to others) and
	// FetchAllAgainstMe (what others did to me), each entry holding a moderationType, a targetUserId
	// and a sourceUserId. For an "against me" entry of type Block, the SOURCE is the person who
	// blocked me — that is the whole trick, and it needs no guessing from how an avatar is rendered.
	//
	// THIS IS A PROBE FIRST, A FEATURE SECOND. Three things are unknown and each one can make the
	// answer empty, so it logs a breakdown by type instead of quietly showing nothing:
	//   1. whether VRChat's server still returns Block rows on the against-me endpoint at all —
	//      it has been restricted over the years, and the client method existing proves nothing;
	//   2. whether the il2cpp delegate bridge works on this build (see below);
	//   3. whether the game has already fetched this for its own BlockedByUsers list, in which case
	//      a local read would be cheaper than any call.
	//
	// THE DELEGATE BRIDGE IS THE DANGEROUS PART. FetchAllAgainstMe takes two Action callbacks, so it
	// needs DelegateSupport.ConvertDelegate — the call this mod documents as dying inside
	// Il2CppSystem.Delegate.set_method_ptr with an access violation no try/catch can survive. Every
	// use goes through Il2CppDelegates.TryConvert, which refuses when the bridge is closed rather
	// than attempting it: probing that is the crash. Bridge closed = this module logs why and stops,
	// and nothing else in the mod notices.
	//
	// ONE CALL PER SESSION. These are authenticated API requests made as the user; ApiPlayerModeration
	// even carries its own ListCacheTime. Fired once, a little after the UI is up, and never on a
	// timer — a moderation list that changes mid-session is not worth a rate limit.
	public class BlockedByProbeModule : IModule
	{
		public override string Name => "BlockedByProbe";

		/// <summary>User ids of people who have blocked ME. Read by the player lists.</summary>
		// Edge memory for the BlockedBy switch, so the falling edge can clear the lists exactly once
		// instead of every frame.
		private static bool _wasProbeOn = true;

		public static readonly HashSet<string> BlockedMe = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>User ids I have blocked myself — the other direction, so the two can be told apart.</summary>
		public static readonly HashSet<string> IBlocked = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>"" until an answer arrived; otherwise a short sentence for the diagnostics panel.</summary>
		public static string Status = "";

		private static bool _fired;
		private static float _at;
		private static bool _autoArmed;
		private const string TrailName = "blockedby";

		// NOTHING RUNS BY ITSELF HERE, AND THAT IS THE WHOLE POINT.
		//
		// The first cut of this module fired 20 s after the UI came up. That was wrong: the fetch needs
		// DelegateSupport.ConvertDelegate, the one call this mod documents as killing the PROCESS with
		// a fault .NET cannot catch, and Il2CppDelegates.Available opens as soon as FieldOffsetFix is
		// verified — so on a healthy install the gate was open and every user ran that call on every
		// world load, unasked. A crash was reported the same day. Whether or not this was its cause, a
		// probe that CAN take the game down must never be something you did not ask for.
		//
		// AND YET IT NOW RUNS BY ITSELF AGAIN (2026-09-08), because a badge nobody can reach is not a
		// feature: ModLink.WhoBlockedMe() existed in the client and NOTHING EVER CALLED IT, so the
		// command this comment sends you to was unreachable and the BLOCKED pill could never appear.
		// The owner asked for it on by default, and not behind a toggle.
		//
		// What makes that defensible now, where it was not before:
		//   * IT IS GATED. Il2CppDelegates.Available is false unless FieldOffsetFix verified the
		//     offset slot, and the bridge REFUSES rather than guessing. On a build where the repair
		//     failed, nothing is asked at all.
		//   * IT LEAVES A TRAIL. Core/CrashTrail writes each step to disk and flushes BEFORE the call
		//     it describes, so if this ever does take the process down, the next launch says exactly
		//     which line did it. The old "a crash was reported the same day, whether or not this was
		//     its cause" is precisely the uncertainty that tooling removes.
		//   * ONCE PER SESSION, and late. Not on every world load, and not while the game is still
		//     bringing the instance up.
		public override void OnUiReady() { }

		// WHAT THE PREVIOUS SESSION DIED ON, read exactly once. CrashTrail.Check DELETES the file it
		// reads, so calling it from two places would make whichever ran second see nothing — the
		// answer is taken here, at startup, and everyone else reads this.
		private static string _lastCrash = "";

		public override void OnInitialize()
		{
			try { _lastCrash = Core.CrashTrail.Check(TrailName) ?? ""; }
			catch { _lastCrash = ""; }
			if (_lastCrash.Length > 0)
			{
				// REPORTED, NOT ACTED ON. An earlier draft made this disable the probe for a session,
				// and that was the wrong instinct: a feature that switches itself off is a feature the
				// user has lost, and the answer to "people are crashing" is to stop the crash, not to
				// stop the feature. The trail is here so a crash names its own line on the next launch.
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[BlockedBy] the previous session died inside this probe at \"" + _lastCrash + "\". "
					+ "Running again with the pointer checks below; if this line comes back, it names the step.");
			}
		}

		public override void OnUpdate()
		{
			// THE SWITCH GOVERNS THE LIVE SOURCE TOO (2026-09-13).
			//
			// SampleLive is what actually feeds BlockedMe on this build — the REST probe below is
			// dead (FetchAllAgainstMe answers "not implemented") — so it is the only thing that lights
			// the BLOCKED marker. It used to run unconditionally, ABOVE the only read of the switch,
			// which made the option contradict its own description ("Turning it off costs you the
			// BLOCKED tag and nothing else"): the tag stayed, and the whole-roster reflection walk
			// went on twice a second. Now OFF really means off, and the lists are emptied once on the
			// falling edge so no stale marker is left behind. Default is ON, so nothing changes for
			// anyone who has not deliberately turned it off.
			bool probeOn = true;
			try { probeOn = ModConfig.BlockedByProbe == null || ModConfig.BlockedByProbe.Value; } catch { }
			if (!probeOn)
			{
				if (_wasProbeOn)
				{
					_wasProbeOn = false;
					BlockedMe.Clear();
					IBlocked.Clear();
					Status = "blocked-by: switched off in the config";
				}
				return;
			}
			_wasProbeOn = true;

			// THE MARKER REGISTERS ITS OWN EVENT. IT MUST NOT DEPEND ON A DEBUG SWITCH.
			//
			// Event 33 is the only source that ever told the truth about who blocked you, and NetworkLog
			// only captures codes someone asked it to watch. Both callers of WatchCode(33) live in the
			// BlockDebug modules -- so switching that debug off, which had to happen because its per-frame
			// observer crashed the game four times, silently took the FEATURE down with it. The owner then
			// joined an instance where they were blocked and nothing was captured at all.
			//
			// A feature asks for what it needs. Watching one event code costs a set lookup on the receive
			// path and nothing else.
			if (!_watchAsked)
			{
				_watchAsked = true;
				try { NetworkLogModule.WatchCode(33); VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] event 33 surveille — le marqueur [B] se remplira au prochain join."); } catch { }
			}

			// THE LOCAL SHAPE FIRST: it is the only source that reacts WHILE you are in the instance.
			//
			// Event 33 is authoritative but the server only sends it on ENTERING -- watched live through a
			// block and an unblock, nothing arrived in between. The avatar swap, on the other hand, happens
			// immediately and is visible with a plain Transform.childCount.
			// BlockByAvatarShape is NOT called any more: measured live on 2026-09-18, a blocked player's
			// avatar stayed at 51 children with VRIK, IK and Animator intact -- identical to unblocked.
			// The 0 seen earlier was an avatar still loading. A refuted signal must not drive a marker
			// that accuses real people.

			// THE WIRE FIRST. Event 33 subtype 21 is the server stating who has blocked us -- authoritative,
			// no member index, no delegate, no endpoint. It arrives as a snapshot on joining and as a
			// per-actor delta whenever the state changes, so it tracks a block AND an unblock live.
			// The old boolean sampling only runs when the wire has said nothing yet.
			try
			{
				// ONCE THE SERVER HAS SPOKEN IT IS THE ANSWER, INCLUDING WHEN IT SAYS "NOBODY".
				//
				// This used to require BlockedMeActors.Count > 0, so the wire could only ever SET the
				// marker, never clear it. The unblock delta empties the set, the branch was skipped, and
				// [B] would stay on someone who had just lifted their block -- the exact sequence measured
				// on 2026-09-18, where {1=2, 10=true} was followed minutes later by {1=2, 10=false}.
				// HasSpoken separates "the server says nobody" from "the server has not said anything
				// yet", which an empty set on its own cannot express.
				if (Event33Moderation.HasSpoken)
				{
					// KEY 10 — measured both ways: they blocked you.
					var fromWire = Event33Moderation.BlockedMeUserIds();
					BlockedMe.Clear();
					foreach (string id in fromWire) BlockedMe.Add(id);

					// KEY 11 — the other direction, fed here so [B] is TOTAL in both senses. Its meaning
					// (you blocked them) is not yet confirmed: in the block test the owner blocked nobody,
					// so key 11 stayed false the whole time. A mutual-block test settles it — the owner
					// blocks the friend and we watch whether key 11 lights on the friend's actor. This is
					// the owner's OWN data either way, so an unconfirmed label is self-checking, not an
					// accusation. Event33Moderation logs both keys raw so the test is decisive.
					var mine = Event33Moderation.OtherAxisUserIds();
					IBlocked.Clear();
					foreach (string id in mine) IBlocked.Add(id);

					_flagBogus = false;
					Status = "blocked-by: " + fromWire.Count + " they->you, " + mine.Count
						+ " you->them (source : event 33)";
					return;
				}
			}
			catch { }

			SampleLive();

			// Arm the automatic pass once, a while after the game has settled.
			if (!_autoArmed && !_fired && _at <= 0f)
			{
				float now0 = VaClock.Now;
				if (now0 < 45f) return;             // let the session finish coming up first
				// An escape hatch, ON by default: nothing is disabled, but a user who wants this off
				// should not have to delete the mod to get it.
				// OFF DOES NOT LATCH (2026-09-13). This used to set _autoArmed = true on the OFF path,
				// which is the arm-once flag: switching the option back ON could then never fire the
				// probe for the rest of the session, because the "arm" had already been spent while
				// the feature was disabled. Returning WITHOUT arming leaves the probe eligible, so
				// turning it on works immediately and the toggle is honest in both directions.
				try { if (ModConfig.BlockedByProbe != null && !ModConfig.BlockedByProbe.Value) { Status = "blocked-by: switched off in the config"; return; } }
				catch { }
				// AND WAIT TO ACTUALLY BE IN A WORLD. 45 s after launch can still be the loading
				// screen on a cold start, and an authenticated API call made before the account is
				// live comes back empty — which would look exactly like "nobody blocked you" and
				// then never be retried, because this fires once.
				try { if (PlayerRef.LocalApi() == null) return; }
				catch { return; }
				_autoArmed = true;
				// The closed delegate bridge is no longer a reason not to ask: the HTTP route in Fetch()
				// needs neither a delegate nor a member index.

				_at = now0;
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] asking automatically (once this session).");
			}

			if (_at <= 0f || _fired) return;
			if (VaClock.Now < _at) return;
			_fired = true;

			// THE TRAIL IS OPEN ACROSS THE WHOLE CALL. FetchAllAgainstMe goes through
			// DelegateSupport.ConvertDelegate, which this mod documents as able to kill the process
			// with a fault no catch can see — so every step is on disk before it runs.
			Core.CrashTrail.Begin(TrailName, "automatic blocked-by probe\r\nmod=" + PluginInfo.Version);
			try { Fetch(); }
			catch (Exception e)
			{
				Status = "blocked-by probe failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] " + Status);
			}
			finally { Core.CrashTrail.End(); }
		}

		/// <summary>What the PREVIOUS session's probe died on, or "" if it completed. Reading consumes
		/// it, so a crash is reported once. Called by the diagnostics report.</summary>
		public static string CrashedAt() => _lastCrash;

		/// <summary>Asks once, on request. Re-arming a second time is allowed — the answer can change
		/// between sessions — but never automatically.</summary>
		public static void RequestFetch()
		{
			_fired = false;
			_at = VaClock.Now;   // next Update, on the main thread, where il2cpp wants it
			Status = "blocked-by: asking…";
			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] requested by the user");
		}

		private static void Fetch()
		{
			// THE HTTP ROUTE FIRST, because it works on this build and the game's own call does not.
			//
			// FetchAllAgainstMe needs a managed callback handed to il2cpp, and that conversion kills the
			// process on 1903. The same answer is an endpoint away, asked with the session's own cookie,
			// and it depends on no member index -- so it survives the next time VRChat permutes members.
			string err;
			var viaApi = VrcModerations.FetchBlockedMe(out err);
			if (viaApi != null)
			{
				BlockedMe.Clear();
				foreach (string id in viaApi) BlockedMe.Add(id);
				_flagBogus = false;   // the answer no longer comes from the boolean that was misreading
				Status = "blocked-by: " + viaApi.Count + " personne(s) t'ont bloque (source : API VRChat).";
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] l'API n'a pas repondu (" + err + ") — repli sur l'appel du jeu.");

			if (!Il2CppDelegates.Available)
			{
				Status = "blocked-by: l'API n'a pas repondu (" + err + ") et le pont de delegues est ferme sur ce build";
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}

			Core.CrashTrail.Step("converting the four delegates");
			var okAgainst = Il2CppDelegates.TryConvert<Il2CppSystem.Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>>(
				new Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>(list => Consume(list, true)), "BlockedBy/againstMe");
			var okMine = Il2CppDelegates.TryConvert<Il2CppSystem.Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>>(
				new Action<Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration>>(list => Consume(list, false)), "BlockedBy/mine");
			var errAgainst = Il2CppDelegates.TryConvert<Il2CppSystem.Action<string>>(
				new Action<string>(e => Fail("against me", e)), "BlockedBy/againstMe-err");
			var errMine = Il2CppDelegates.TryConvert<Il2CppSystem.Action<string>>(
				new Action<string>(e => Fail("mine", e)), "BlockedBy/mine-err");

			if (okAgainst == null || errAgainst == null)
			{
				Status = "blocked-by: the delegate bridge refused — nothing was asked";
				VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
				return;
			}

			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] asking the API for player moderations…");
			Core.CrashTrail.Step("FetchAllAgainstMe — the call that needs ConvertDelegate");
			ApiPlayerModeration.FetchAllAgainstMe(okAgainst, errAgainst);
			Core.CrashTrail.Step("FetchAllAgainstMe returned");
			if (okMine != null && errMine != null)
			{
				Core.CrashTrail.Step("FetchAllMine");
				ApiPlayerModeration.FetchAllMine(okMine, errMine);
				Core.CrashTrail.Step("FetchAllMine returned");
			}
		}

		private static void Fail(string which, string error)
		{
			Status = "blocked-by (" + which + "): " + (error ?? "unknown error");
			VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] " + Status);
		}

		// againstMe: I am the target, so the SOURCE is the person who acted on me.
		// mine:      I am the source, so the TARGET is the person I acted on.
		private static void Consume(Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration> list, bool againstMe)
		{
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			int total = 0, blocks = 0;
			try
			{
				// THE SECOND HALF OF THE SAME CRASH. Walk stops a dead pointer reaching TryCast; these
				// lines then read FIELDS off the rows it returned, and that is the other uncatchable
				// fault this mod knows about — moderationType is an il2cpp field (Core/FieldOffsetFix:
				// unrepaired, the offset is a metadata token and the read lands outside the object),
				// and sourceUserId/targetUserId are il2cpp STRINGS, whose generated getter hands the
				// pointer straight to Il2CppStringToManaged and memmoves from it. Both end the process
				// from inside, past every catch here.
				//
				// So the rows are re-validated before they are read — a row can die between the walk
				// and here, the callback is not instantaneous — and the strings go through
				// Core/Il2CppStr, which proves the header and length before converting.
				bool fieldsSafe = false;
				try { fieldsSafe = Core.FieldOffsetFix.Verified; } catch { }
				if (!fieldsSafe)
				{
					Status = "blocked-by: field offsets unrepaired on this build — not reading the rows";
					VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] " + Status);
					return;
				}

				foreach (ApiPlayerModeration m in Walk(list))
				{
					if (m == null || !Core.NativeGuard.Alive(m)) continue;
					total++;
					string type;
					try { type = m.moderationType.ToString(); } catch { type = "?"; }
					byType.TryGetValue(type, out int n);
					byType[type] = n + 1;

					bool isBlock;
					try { isBlock = m.moderationType == ApiPlayerModeration.ModerationType.Block; } catch { continue; }
					if (!isBlock) continue;

					string id = ReadId(m, againstMe);
					if (string.IsNullOrEmpty(id)) continue;
					if (againstMe) { if (BlockedMe.Add(id)) blocks++; }
					else { if (IBlocked.Add(id)) blocks++; }
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] reading the list: " + e.Message);
			}

			// The breakdown is the point of the probe: "0 rows" and "40 rows, none of them Block" are
			// completely different answers, and only one of them means the feature is impossible.
			var parts = new List<string>();
			foreach (var kv in byType) parts.Add(kv.Key + "=" + kv.Value);
			string where = againstMe ? "against me" : "mine";
			Status = "blocked-by (" + where + "): " + total + " row(s)" + (parts.Count > 0 ? " [" + string.Join(", ", parts.ToArray()) + "]" : "")
				+ " → " + (againstMe ? BlockedMe.Count : IBlocked.Count) + " block(s)";
			VRChatArchiveModPlugin.Logger.LogInfo("[BlockedBy] " + Status);
		}

		// The one enumeration that every il2cpp collection honours, per this mod's Il2CppSeq notes: a
		// generic IEnumerable<T> proxy is NOT a managed IEnumerable, so it is cast to the NATIVE
		// non-generic interface and its enumerator walked by hand.
		// THE CRASH THIS IS WRITTEN AGAINST (2026-09-08, a user's ErrorLog.log):
		//
		//     Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
		//        at Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_assignable_from(IntPtr, IntPtr)
		//        at Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase.TryCast[[System.__Canon]]()
		//        at VRChatArchiveMod.Modules.BlockedByProbeModule.Walk(...)
		//
		// AND THE `catch` THAT USED TO WRAP TryCast NEVER RAN. On .NET 6 an AccessViolationException is
		// a corrupted-state exception: it is not delivered to managed catch blocks at all, the process
		// simply ends. So `try { m = cur.TryCast<...>(); } catch { }` read like a guard and was
		// decorative — the same lesson as the displayName crash in SpoofModule the same day. The only
		// thing that works is not making the call when it would fault.
		//
		// WHAT TryCast<T> ACTUALLY DOES: it takes the target class pointer from
		// Il2CppClassPointerStore<T>, the object's own class from il2cpp_object_get_class(Pointer),
		// and hands BOTH to il2cpp_class_is_assignable_from, which dereferences them. Two pointers,
		// either of which can be rotten:
		//   * the OBJECT's — an element whose native object is gone, or an enumerator handing back a
		//     stale pointer. il2cpp_object_get_class then reads freed memory and returns garbage.
		//   * the TARGET CLASS's — resolved lazily per T, and this build re-obfuscates every VRChat
		//     type on each update, so a lookup that half-succeeds leaves a non-zero, invalid pointer.
		// The stack cannot say which, and it does not matter: both are refused below, so the walk is
		// safe under either.
		// The user id on one row, read the safe way: the raw field pointer, then Core/Il2CppStr, which
		// refuses a string whose header or length does not stand up instead of memmoving from it.
		// Field handles are resolved once per session off the first row we see.
		private static IntPtr _fSource, _fTarget;
		private static bool _idFieldsTried;

		private static string ReadId(ApiPlayerModeration m, bool againstMe)
		{
			try
			{
				IntPtr obj;
				try { obj = m.Pointer; } catch { return ""; }
				if (!Core.NativeGuard.IsLiveObject(obj)) return "";

				if (!_idFieldsTried)
				{
					_idFieldsTried = true;
					_fSource = Core.Il2CppStr.FindField(obj, "sourceUserId");
					_fTarget = Core.Il2CppStr.FindField(obj, "targetUserId");
					if (_fSource == IntPtr.Zero || _fTarget == IntPtr.Zero)
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[BlockedBy] ApiPlayerModeration has no sourceUserId/targetUserId field on this build "
							+ "— no ids will be read, and nothing is guessed.");
				}

				IntPtr fi = againstMe ? _fSource : _fTarget;
				if (fi == IntPtr.Zero) return "";
				IntPtr sp;
				if (!Core.Il2CppStr.TryFieldPtr(obj, fi, out sp)) return "";
				string s;
				return Core.Il2CppStr.TryRead(sp, out s) ? (s ?? "") : "";
			}
			catch { return ""; }
		}

		private static bool ClassOk<T>() where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
		{
			try
			{
				IntPtr k = Il2CppInterop.Runtime.Il2CppClassPointerStore<T>.NativeClassPtr;
				return k != IntPtr.Zero && Core.NativeGuard.IsReadable(k, 16);
			}
			catch { return false; }   // a type initializer that threw is a managed failure, and catchable
		}

		private static IEnumerable<ApiPlayerModeration> Walk(Il2CppSystem.Collections.Generic.IEnumerable<ApiPlayerModeration> list)
		{
			var outp = new List<ApiPlayerModeration>();
			if (list == null) return outp;

			// Both class pointers, once, before anything is cast. If either is not a readable class,
			// no cast in this method can be made safely and the honest answer is no rows.
			if (!ClassOk<Il2CppSystem.Collections.IEnumerable>() || !ClassOk<ApiPlayerModeration>())
			{
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[BlockedBy] the il2cpp class pointers for IEnumerable/ApiPlayerModeration did not "
					+ "validate on this build — not walking the list. VRChat has probably moved the type.");
				return outp;
			}
			if (!Core.NativeGuard.Alive(list)) { VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] the list VRChat handed back is not a live object — nothing read."); return outp; }

			// THE REAL REPAIR: DO NOT CAST IN THE LOOP AT ALL.
			//
			// Guarding TryCast per element makes the walk survivable. Not calling it is better, and it
			// is available: VRChat hands back a List<ApiPlayerModeration>, and Il2CppInterop's List<T>
			// exposes Count and an indexer whose result is ALREADY typed — get_Item goes through
			// Il2CppObjectPool.Get<T>, which never touches il2cpp_class_is_assignable_from. So the
			// function that faulted is not on this path even once per row.
			//
			// One cast remains, on the LIST itself, and it is the one call we can afford to check
			// properly: both class pointers validated above, the object validated as live. One guarded
			// call instead of N unguarded ones.
			try
			{
				if (ClassOk<Il2CppSystem.Collections.Generic.List<ApiPlayerModeration>>())
				{
					Il2CppSystem.Collections.Generic.List<ApiPlayerModeration> typed = null;
					try { typed = list.TryCast<Il2CppSystem.Collections.Generic.List<ApiPlayerModeration>>(); } catch { }
					if (typed != null && Core.NativeGuard.Alive(typed))
					{
						int n = -1;
						try { n = typed.Count; } catch { n = -1; }
						// A Count read off a rebuilt object could be anything; a moderation list of a
						// hundred thousand rows is not a real answer, it is a bad read.
						if (n >= 0 && n <= 50000)
						{
							int dead = 0;
							for (int i = 0; i < n; i++)
							{
								ApiPlayerModeration m = null;
								try { m = typed[i]; } catch { continue; }
								if (m == null) continue;
								if (!Core.NativeGuard.Alive(m)) { dead++; continue; }
								outp.Add(m);
							}
							VRChatArchiveModPlugin.Logger.LogInfo(
								"[BlockedBy] read " + outp.Count + " row(s) by indexer"
								+ (dead > 0 ? " (" + dead + " skipped: object gone)" : "") + " — no per-row cast.");
							return outp;
						}
						VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] List.Count read back " + n + " — refusing it, falling back to the enumerator.");
					}
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] indexer path: " + e.Message); }

			// FALLBACK — an enumerator walk, for a build where the callback hands back something that
			// is not a List<T>. Same guards, per element, because here the cast is unavoidable.
			try
			{
				var native = list.TryCast<Il2CppSystem.Collections.IEnumerable>();
				if (native == null || !Core.NativeGuard.Alive(native)) return outp;
				var it = native.GetEnumerator();
				if (it == null || !Core.NativeGuard.Alive(it)) return outp;

				int guard = 0, skipped = 0;
				while (guard++ < 5000)
				{
					// The enumerator is re-checked EVERY step: MoveNext runs the world's own code and
					// the collection can be rebuilt underneath us between two elements.
					if (!Core.NativeGuard.Alive(it)) break;
					bool more;
					try { more = it.MoveNext(); } catch { break; }
					if (!more) break;

					var cur = it.Current;
					if (cur == null) continue;

					// THE LINE THE CRASH WAS ON. IsLiveObject proves exactly what
					// il2cpp_class_is_assignable_from is about to assume: the pointer is mapped,
					// 8-aligned, and the class pointer in its first eight bytes is mapped too.
					IntPtr p;
					try { p = cur.Pointer; } catch { skipped++; continue; }
					if (!Core.NativeGuard.IsLiveObject(p)) { skipped++; continue; }

					ApiPlayerModeration m = null;
					try { m = cur.TryCast<ApiPlayerModeration>(); } catch { }
					if (m != null) outp.Add(m);
				}
				if (skipped > 0)
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[BlockedBy] skipped " + skipped + " row(s) whose object was not there any more. "
						+ "This is the guard doing its job — that cast used to end the process.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy] walk: " + e.Message); }
			return outp;
		}

		// ---------------------------------------------------------------- live block flag (always on)
		//
		// VRChat's OWN per-player flag, not an inference: on each remote VRC.Player, prop_Boolean_17
		// goes true the moment a block lands and false the moment it is lifted. Measured over three
		// block/unblock cycles on 2026-09-11 (event-33 trace): it flips ~33 ms after the moderation
		// packet, one frame before the avatar is swapped for the empty shell, and it is the only
		// member on VRC.Player that tracks block state per player.
		//
		// DIRECTION IS NOT PROVEN. The flag means "you two no longer render each other"; a block you
		// set yourself very likely raises it too. IBlocked (your own list) is what tells the two apart
		// when it is populated — on this build its ids do not read, so the marker means "blocked",
		// not "blocked BY THEM", and nothing in the UI claims otherwise.
		//
		// Read-only: one property getter per remote player, twice a second, behind a liveness check.
		// It writes nothing and reveals nothing — the marker is a label, not an un-hide.
		// FINDING THE REAL FLAG BY WHAT IT DOES, NOT BY WHAT IT IS CALLED.
		//
		// prop_Boolean_17 is a 1886 INDEX and 1903 permuted the members, so the name is worthless here
		// -- it landed on a boolean that is true for nearly everyone and put [B] on the whole room. The
		// marker was disarmed rather than left lying about real people, which was right.
		//
		// The way back is the one that worked for the 3D preview: identify the member by its BEHAVIOUR.
		// "Someone blocked you" is rare and individual, so the real flag is the one boolean that is
		// false for every remote player EXCEPT the one who actually blocked you. This prints, per
		// player, which boolean offsets read true -- and with one known blocker in the room the odd one
		// out identifies itself. No index is trusted, nothing is written, and it only runs when armed.
		internal static bool DumpFlags;
		private static float _nextDump;
		private static readonly Dictionary<string, string> _lastProfile = new Dictionary<string, string>();

		// THE FIELD LAYOUT OF VRC.Player, BY TYPE SEQUENCE.
		//
		// A dump of the class on an inspectable build named the member we actually want: IsBlockedByUser
		// -- "this person has blocked YOU" -- sitting beside IsBlocked ("you blocked them"). The mod was
		// reading prop_Boolean_17, a 1886 INDEX, which 1903 permuted onto some other boolean.
		//
		// But that dump also showed the member is surrounded by types the obfuscator CANNOT rename,
		// because they are hand-written and shared: VRC.Core.APIUser, Photon.Realtime.Player,
		// VRC.SDKBase.VRCPlayerApi. Those are anchors. Printing the live field list with each type name
		// in order lets the boolean be located by WHERE IT SITS relative to them, which is the same
		// method that recovered the 3D preview -- and unlike an index, a sequence of type names survives
		// a member permutation.
		//
		// Read-only, once per session, and it names nothing it cannot read.
		private static bool _layoutDumped;

		internal static unsafe void DumpPlayerLayout(object player)
		{
			if (_layoutDumped || player == null) return;
			try
			{
				var bo = player as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (bo == null) return;
				IntPtr obj; try { obj = bo.Pointer; } catch { return; }
				if (obj == IntPtr.Zero || !NativeGuard.IsLiveObject(obj)) return;

				// THE ROSTER ALREADY HANDS US VRC.Player — the component itself.
				// The first version cast it to VRCPlayerApi, got null every time, and returned before printing
				// anything: that is why no [PlayerLayout] line ever appeared. No lookup is needed at all.
				IntPtr comp = obj;
				IntPtr cls; try { cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(comp); } catch { return; }
				if (cls == IntPtr.Zero) return;

				_layoutDumped = true;
				int size;
				try { size = (int)Il2CppInterop.Runtime.Runtime.UnityVersionHandler.Wrap((Il2CppInterop.Runtime.Runtime.Il2CppClass*)cls).InstanceSize; }
				catch { return; }

				var rows = new List<string>();
				foreach (IntPtr f in MemberAlign.LiveFields(cls))
				{
					uint off; try { off = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f); } catch { continue; }
					string tn = null;
					try
					{
						IntPtr tp = MemberAlign.FieldTypePtr(f);
						if (tp != IntPtr.Zero)
						{
							IntPtr fk = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(tp);
							if (fk != IntPtr.Zero)
							{
								IntPtr np = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(fk);
								tn = np == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(np);
							}
						}
					}
					catch { }
					if (tn != null) foreach (char ch in tn) if (ch > 126) { tn = "<obf>"; break; }

					string val = "";
					if (tn == "Boolean" && off >= 0x10 && off + 1 <= size && NativeGuard.IsReadable(comp, size))
					{
						try { val = "=" + (System.Runtime.InteropServices.Marshal.ReadByte(comp, (int)off) != 0 ? "true" : "false"); } catch { }
					}
					rows.Add("0x" + off.ToString("X") + ":" + (tn ?? "?") + val);
					if (rows.Count >= 48) break;
				}
				VRChatArchiveModPlugin.Logger.LogWarning("[PlayerLayout] VRC.Player champs (offset:type=valeur) — "
					+ string.Join("  ", rows));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[PlayerLayout] " + e.Message); }
		}

		internal static unsafe void DumpBooleanProfile(object player, string userId, bool isLocal)
		{
			if (!DumpFlags || player == null || string.IsNullOrEmpty(userId)) return;
			try
			{
				// THROTTLED, because the first version was not and it showed.
				// _nextDump was read and never written, so this walked EVERY field of the player class for
				// EVERY player on EVERY pass. Measured immediately: BlockedByProbe = 238.3 ms/s, the largest
				// cost in the mod at that moment -- for a diagnostic whose whole point is to notice a change
				// that happens once in a session. Twice a second sees the same transitions at a fortieth of
				// the price.
				float now; try { now = VaClock.Now; } catch { return; }
				if (now < _nextDump) return;
				_nextDump = now + 0.5f;

				var bo = player as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (bo == null) return;
				IntPtr obj; try { obj = bo.Pointer; } catch { return; }
				if (obj == IntPtr.Zero || !NativeGuard.IsLiveObject(obj)) return;
				IntPtr k; try { k = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(obj); } catch { return; }
				if (k == IntPtr.Zero) return;

				int size;
				try { size = (int)Il2CppInterop.Runtime.Runtime.UnityVersionHandler.Wrap((Il2CppInterop.Runtime.Runtime.Il2CppClass*)k).InstanceSize; }
				catch { return; }
				if (size < 0x18 || size > 0x20000 || !NativeGuard.IsReadable(obj, size)) return;

				var on = new List<string>();
				foreach (IntPtr f in MemberAlign.LiveFields(k))
				{
					string tn = null;
					try
					{
						IntPtr tp = MemberAlign.FieldTypePtr(f);
						if (tp == IntPtr.Zero) continue;
						IntPtr fk = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(tp);
						if (fk == IntPtr.Zero) continue;
						IntPtr np = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(fk);
						tn = np == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(np);
					}
					catch { continue; }
					if (tn != "Boolean") continue;

					uint off; try { off = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f); } catch { continue; }
					if (off < 0x10 || off + 1 > size) continue;   // a static field's offset is not in the instance
					byte v; try { v = System.Runtime.InteropServices.Marshal.ReadByte(obj, (int)off); } catch { continue; }
					if (v != 0) on.Add("0x" + off.ToString("X"));
					if (on.Count >= 40) break;
				}

				string profile = string.Join(",", on);
				string had;
				if (_lastProfile.TryGetValue(userId, out had) && had == profile) return;   // only on change
				_lastProfile[userId] = profile;
				if (_lastProfile.Count > 64) _lastProfile.Clear();
				VRChatArchiveModPlugin.Logger.LogWarning("[BlockedBy/flags] " + (isLocal ? "MOI  " : "autre")
					+ " " + userId + " -> booleens VRAIS : " + (on.Count == 0 ? "(aucun)" : profile));
			}
			catch { }
		}

		private static System.Reflection.PropertyInfo _pBlockFlag;
		private static System.Type _pBlockFlagType;
		private static float _liveAt;

		/// <summary>True when the blocked-by probe is enabled (default on). VaTags reads the block
		/// flag inside its own roster pass when this is true, folding the read into the single
		/// per-player scan.</summary>
		internal static bool WantBlockProbe()
		{
			try { return ModConfig.BlockedByProbe == null || ModConfig.BlockedByProbe.Value; }
			catch { return true; }
		}

		/// <summary>Reads VRChat's own "this player is blocked / hidden from me" flag
		/// (prop_Boolean_17 on the player proxy). The CALLER must have already proven the player
		/// alive — this is called from the roster pass, which does. Reflection PropertyInfo is cached
		/// per type; any read failure is false.</summary>
		private static bool _flagChecked, _flagTrusted;
		private static bool _watchAsked;

		internal static bool ReadBlockedFlag(object player)
		{
			// prop_Boolean_17 IS A 1886 INDEX, AND 1903 PERMUTED THE MEMBERS.
			//
			// This reads "the 17th boolean" of the player proxy by its 1886 name. The obfuscator does
			// not just rename members, it REORDERS them, so on 1903 that name lands on a DIFFERENT
			// boolean -- one that happens to be true for almost everyone (loaded / visible / has an
			// avatar). The result was a [B] "they blocked you" marker on nearly every player in the
			// roster, which is not a small bug: it is the mod stating a falsehood about real people.
			//
			// The mod already knows whether it can place this class's members: TokenShiftFix's anchor
			// table re-binds prop_Boolean_17 to the right getter ONLY when the class is trustworthy.
			// For the player class on this build it is NOT (the startup log says so outright), so the
			// flag is meaningless here. When it cannot be trusted the marker is switched OFF entirely
			// -- no marker is honest ("unknown"), a wrong marker is a lie.
			if (_flagBogus) return false;                       // already proven a room-wide misread
			if (_flagChecked && !_flagTrusted) return false;
			try
			{
				if (player == null) return false;

				if (!_flagChecked)
				{
					_flagChecked = true;
					_flagTrusted = ClassTrustworthy(player);
					if (!_flagTrusted)
					{
						// Armed exactly here: the index cannot be trusted, so the only way back is to find the
						// right boolean by behaviour. Costs nothing until a profile CHANGES, and changes are rare.
						DumpFlags = true;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[BlockedBy] le drapeau de blocage (prop_Boolean_17) n'est pas fiable sur ce build de VRChat "
							+ "— les membres de la classe joueur sont permutes, l'index 17 tomberait sur un autre booleen. "
							+ "Le marqueur [B] est DESACTIVE plutot que d'afficher de faux blocages.");
						return false;
					}
				}

				System.Type t = player.GetType();
				if (_pBlockFlag == null || _pBlockFlagType != t)
				{
					_pBlockFlagType = t;
					try { _pBlockFlag = t.GetProperty("prop_Boolean_17"); } catch { _pBlockFlag = null; }
				}
				if (_pBlockFlag == null) return false;
				return (bool)_pBlockFlag.GetValue(player, null);
			}
			catch { return false; }
		}

		// Can the mod place this player class's members on this build? Only then does prop_Boolean_17
		// resolve to the real getter. The object's OWN runtime class is used, not the declared proxy
		// type, so it is the actual VRCPlayer/VRC.Player the anchor table was measured against.
		private static bool ClassTrustworthy(object player)
		{
			try
			{
				var b = player as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (b == null) return false;
				IntPtr ptr = b.Pointer;
				if (ptr == IntPtr.Zero || !Core.NativeGuard.IsLiveObject(ptr)) return false;
				IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(ptr);
				if (klass == IntPtr.Zero) return false;
				return Core.TokenShiftFix.MembersTrustworthy(klass);
			}
			catch { return false; }
		}

		// NO SECOND WALK (2026-09-13). The flag is read in VaTags' roster pass now (entry.Blocked),
		// so this is a plain bool copy per player — no reflection, no liveness syscall, no separate
		// traversal of the 40 player objects. Kept as its own method so the OFF path and the REST
		// probe below stay exactly as they were.
		private static void SampleLive()
		{
			try
			{
				var roster = VaTagsModule.Roster;
				if (roster == null) return;
				int n;
				try { n = roster.Count; } catch { return; }
				// "EVERYONE BLOCKED YOU" IS NEVER TRUE — a build-agnostic sanity gate.
				//
				// The block flag is read by a 1886 member name; if that name lands on the wrong boolean
				// (a permuted index, or a member of the wrong class that is simply true for every loaded
				// player) the marker fires on the whole room. The owner saw exactly that: [B] on all
				// five remote players at once. No real block pattern does that -- someone blocking you
				// is rare and individual -- so when the flag marks every remote player present, it is
				// the flag that is wrong, not the room. Drop the whole sample and disarm for the session
				// rather than accuse everyone.
				int remotes = 0, flagged = 0;
				for (int i = 0; i < n; i++)
				{
					VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null || e.IsLocal || string.IsNullOrEmpty(e.UserId)) continue;
					remotes++;
					if (e.Blocked) flagged++;
				}
				if (remotes >= 2 && flagged == remotes)
				{
					if (!_flagBogus)
					{
						_flagBogus = true;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[BlockedBy] le drapeau a marque les " + remotes + " joueurs distants comme t'ayant bloque — "
							+ "impossible, donc c'est une mauvaise lecture (index de membre 1886 permute sur ce build). "
							+ "Marqueur [B] desactive pour la session, aucun faux blocage affiche.");
					}
					BlockedMe.Clear();
					return;
				}
				if (_flagBogus) return;   // once proven wrong this session, never trust it again

				for (int i = 0; i < n; i++)
				{
					VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null || e.IsLocal) continue;
					string uid = e.UserId;
					if (string.IsNullOrEmpty(uid)) continue;
					if (e.Blocked) BlockedMe.Add(uid);
					else BlockedMe.Remove(uid);
				}
			}
			catch { }
		}

		private static bool _flagBogus;

		/// <summary>BLOCKED / BLOCKED BY YOU / "" for a user id — what the lists print.</summary>
		public static string Tag(string userId)
		{
			if (string.IsNullOrEmpty(userId)) return "";
			if (BlockedMe.Contains(userId)) return "BLOCKED";
			if (IBlocked.Contains(userId)) return "BLOCKED BY YOU";
			return "";
		}

		// The lists belong to the account, not the world: a scene change must not clear them, and must
		// not re-ask either.
		public override void OnShutdown() { BlockedMe.Clear(); IBlocked.Clear(); _fired = false; }
	}
}
