using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PHOTON GUARD — drops hostile inbound Photon events BEFORE VRChat ever sees them.
	//
	// THE ATTACK. Every crasher that works over the wire arrives as a Photon event: a client in the
	// room raises one event code hundreds or thousands of times a second (RPC storms, serialization
	// floods, malformed payloads on a code the game trusts). VRChat dispatches each one on the main
	// thread, and the cost of handling them is what freezes or kills the receiving client. Nothing in
	// the game rate-limits the receive side per sender.
	//
	// THE GUARD. One Harmony PREFIX on the same receive funnel NetworkLog listens on,
	// VRCNetworkingClient.OnEvent(EventData). Returning false from the prefix makes Harmony skip
	// the original, so a dropped event is never dispatched — no handler runs, no allocation is
	// made, the game does not know it existed. Two rules, both O(1) per event:
	//   BLOCK CODES  — codes the user listed are dropped unconditionally. VRChat publishes no event
	//                  names, so the EVENTS console is how a code gets identified before it is listed.
	//   RATE LIMIT   — per (actor, code) pair, a rolling one-second counter. Past the limit the pair
	//                  is SUSPENDED for SuspendSeconds and every further event from it is dropped.
	//                  Only that actor's flooding code is muted: their other traffic, and everyone
	//                  else's, is untouched. The server (actor 0 / -1) is never rate-limited, because
	//                  muting the room's own join/leave/property events desyncs the client — it can
	//                  still be block-listed explicitly, that is the user's call.
	//
	// WHAT IT NEVER TOUCHES. Receive side only. This module never sends, raises, edits or replays
	// an event, and it never keeps the EventData: Photon RECYCLES the instance (see NetworkLog), so
	// Code and Sender are snapshotted to plain values and the object is forgotten inside the prefix.
	// NetworkLog's POSTFIX on the same method still runs for dropped events (Harmony runs postfixes
	// regardless of the prefix result), which is wanted: the console shows what was on the wire.
	//
	// HOT PATH DISCIPLINE. This runs for every inbound event, hundreds per second in a busy room.
	// Config is hoisted into plain fields once per frame, the block list is a 256-slot bool table,
	// the per-pair state is a dictionary keyed on one long, and nothing is logged per event.
	public class PhotonGuardModule : IModule
	{
		public override string Name => "PhotonGuard";

		// ---- what the client reads (sync block + HUD) ------------------------------------
		public static int Blocked { get; private set; }            // events dropped since start / Reset
		public static int SuspendedCount { get; private set; }     // live (actor, code) suspensions
		public static string LastBlocked = "";

		private static bool _hooked;
		public static bool HookAttached => _hooked;
		public static string HookInfo = "not attached";

		// ---- config, hoisted --------------------------------------------------------------
		// Read once per frame from OnUpdate: a ConfigEntry read is a dictionary lookup behind a
		// property, and paying it on every inbound event is exactly the kind of cost a guard must
		// not add to the path it protects.
		private static bool _fEnabled = true;
		private static bool _fLog = true;
		private static int _fRate = 500;
		private static int _fSuspendMs = 10_000;

		// Block list as a lookup table rather than a HashSet: a byte indexes it directly, no hashing,
		// no boxing. Replaced wholesale (never mutated) when the config string changes, so the hot
		// path can read it without a lock.
		private static bool[] _blockTable = new bool[256];

		// NEVER RATE-LIMITED, but ONLY the codes where dropping ONE desyncs you: 253 (properties),
		// 254 (leave), 255 (join) are room membership/state, and 7 is voice (muting a normal talker
		// is bad UX, never fatal). Codes 1, 8, 9, 33 are deliberately NOT here: two other clients
		// (Late Night's StarLag floods event 9 and event 1 to lag/disconnect the room; its Safety
		// treats 8/33/9 as crash vectors) prove these are the ones an attack actually abuses, so they
		// stay under the rate limit -- the 500/s threshold clears normal sync and still catches a
		// flood. The explicit BLOCK LIST still applies to everything, exemptions included.
		private static readonly bool[] _neverRate = BuildNeverRate();
		private static bool[] BuildNeverRate()
		{
			var t = new bool[256];
			foreach (int c in new[] { 7, 253, 254, 255 }) t[c] = true;
			return t;
		}
		private static string _blockSource = null;   // the raw string the table was parsed from

		// ---- per-pair state -----------------------------------------------------------------
		// Key = (sender << 8) | code. One long, so a single dictionary lookup covers both.
		private sealed class Window { public long StartMs; public int Count; }
		private static readonly Dictionary<long, Window> _windows = new Dictionary<long, Window>();
		private static readonly Dictionary<long, long> _suspendedUntil = new Dictionary<long, long>();
		private static readonly object Gate = new object();

		// LastBlocked is only rebuilt when the (actor, code) pair changes: a flood is thousands of
		// identical drops, and a string allocation per drop would be the guard's own cost spike.
		private static long _lastBlockedKey = long.MinValue;
		private static long _lastPruneMs;
		private static int _dropsSinceLog;
		private const int DropLogEvery = 200;

		// Monotonic, thread-neutral clock. VaClock.Now is main-thread only, and while
		// Photon dispatch is on the main thread today, a guard that would throw from a worker thread
		// is a guard that silently returns true (see the catch in the prefix) — i.e. no guard.
		private static long NowMs => Environment.TickCount64;

		private static long Key(int sender, byte code) => ((long)sender << 8) | code;

		public override void OnInitialize()
		{
			RefreshFlags();
			InstallHook();
		}

		public override void OnUpdate()
		{
			try
			{
				RefreshFlags();

				// The install can fail once at startup because the game assemblies are not all
				// loaded yet. A guard that quietly stays unarmed is the worst outcome, so it retries
				// on a slow cadence and reports when it gives up.
				if (!_hooked && _hookAttempts < MaxHookAttempts)
				{
					long t = NowMs;
					if (t >= _nextHookTryMs)
					{
						_nextHookTryMs = t + 3000;
						_hookAttempts++;
						InstallHook();
						if (!_hooked && _hookAttempts >= MaxHookAttempts && !_gaveUpLogged)
						{
							_gaveUpLogged = true;
							VRChatArchiveModPlugin.Logger.LogError(
								"[PhotonGuard] never attached after " + MaxHookAttempts + " tries — no inbound event is being "
								+ "filtered on this build (" + HookInfo + "). Report so we can re-target it.");
						}
					}
				}

				// Expired suspensions are dropped lazily on the hot path only for the pairs that keep
				// talking; a pair that went quiet would otherwise sit in the map forever and inflate
				// SuspendedCount. Once a second is plenty: the map holds a handful of entries.
				long now = NowMs;
				if (now - _lastPruneMs >= 1000)
				{
					_lastPruneMs = now;
					Prune(now);
				}
			}
			catch { }
		}

		// Actor numbers are PER ROOM: actor 7 in the next instance is a different person, so a
		// suspension carried across a world change would mute an innocent. Counters go with it.
		public override void OnSceneLoaded(int buildIndex)
		{
			lock (Gate)
			{
				_windows.Clear();
				_suspendedUntil.Clear();
				SuspendedCount = 0;
			}
		}

		// Client action "photonGuardReset": forget every suspension and counter, zero the stats.
		public static void Reset()
		{
			lock (Gate)
			{
				_windows.Clear();
				_suspendedUntil.Clear();
				SuspendedCount = 0;
				Blocked = 0;
				LastBlocked = "";
				_lastBlockedKey = long.MinValue;
				_dropsSinceLog = 0;
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[PhotonGuard] reset — suspensions and counters cleared.");
		}

		// ---------------------------------------------------------------- config

		private static void RefreshFlags()
		{
			try
			{
				_fEnabled = ModConfig.PhotonGuardEnabled.Value;
				_fLog = ModConfig.PhotonGuardLogBlocked.Value;
				_fRate = Math.Max(1, ModConfig.PhotonGuardRatePerSender.Value);
				_fSuspendMs = Math.Max(1, ModConfig.PhotonGuardSuspendSeconds.Value) * 1000;

				// Re-parse only when the string itself changed. A reference/ordinal compare per frame
				// is nothing; parsing per frame would be needless garbage.
				string src = ModConfig.PhotonGuardBlockCodes.Value ?? "";
				if (!string.Equals(src, _blockSource, StringComparison.Ordinal))
				{
					_blockTable = ParseBlockCodes(src, out int n);
					_blockSource = src;
					VRChatArchiveModPlugin.Logger.LogInfo(n == 0
						? "[PhotonGuard] block list empty — only the rate limit is active."
						: "[PhotonGuard] block list: " + n + " code(s) dropped unconditionally [" + src.Trim() + "].");
				}
			}
			catch { }
		}

		// "1, 9,  200" -> table. Anything that is not a 0..255 integer is ignored rather than
		// failing the whole list, so one typo does not silently disarm the codes around it.
		private static bool[] ParseBlockCodes(string src, out int count)
		{
			var table = new bool[256];
			count = 0;
			if (string.IsNullOrWhiteSpace(src)) return table;
			foreach (string part in src.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
			{
				if (int.TryParse(part.Trim(), out int v) && v >= 0 && v <= 255 && !table[v])
				{
					table[v] = true;
					count++;
				}
			}
			return table;
		}

		// ---------------------------------------------------------------- hook

		private static int _hookAttempts;
		private static long _nextHookTryMs;
		private static bool _gaveUpLogged;
		private const int MaxHookAttempts = 40;   // ~2 min at the 3s cadence

		private static void InstallHook()
		{
			if (_hooked) return;
			try
			{
				Type client = FindType("VRCNetworkingClient");
				if (client == null)
				{
					HookInfo = "VRCNetworkingClient not found";
					if (_hookAttempts == 0)
						VRChatArchiveModPlugin.Logger.LogWarning("[PhotonGuard] VRCNetworkingClient not found — nothing is filtered (will retry).");
					return;
				}

				// Same target as NetworkLog, for the same reason: the override on VRCNetworkingClient is
				// what the vtable reaches, its name is not obfuscated, and being virtual it survives AOT.
				MethodInfo target = null;
				foreach (var m in client.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (m.Name != "OnEvent") continue;
					var ps = m.GetParameters();
					if (ps.Length != 1) continue;
					if (!ps[0].ParameterType.Name.Contains("EventData")) continue;
					target = m; break;
				}
				if (target == null)
				{
					HookInfo = "OnEvent(EventData) not found on VRCNetworkingClient";
					if (_hookAttempts == 0)
						VRChatArchiveModPlugin.Logger.LogWarning("[PhotonGuard] " + HookInfo + " (will retry).");
					return;
				}

				var pre = new HarmonyMethod(typeof(PhotonGuardModule)
					.GetMethod(nameof(OnEventPrefix), BindingFlags.Static | BindingFlags.NonPublic));
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: pre);

				_hooked = true;
				HookInfo = "prefix on VRCNetworkingClient.OnEvent";
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[PhotonGuard] armed — filtering inbound events on VRCNetworkingClient.OnEvent"
					+ " (rate " + _fRate + "/s per actor per code, mute " + (_fSuspendMs / 1000) + "s"
					+ (_fEnabled ? ")." : ") — DISABLED in config, passing everything through."));
			}
			catch (Exception e)
			{
				HookInfo = "install failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogError($"[PhotonGuard] hook install failed: {e}");
			}
		}

		// Harmony PREFIX. false = Harmony skips VRCNetworkingClient.OnEvent for this event: dropped.
		// true = untouched. Every failure mode inside resolves to true — a guard that breaks the
		// networking it protects is worse than no guard.
		private static bool OnEventPrefix(object __0)
		{
			try
			{
				if (!_fEnabled) return true;
				if (__0 == null) return true;

				// Snapshot NOW and never keep __0: Photon reuses the EventData instance.
				byte code = 0;
				int sender = -1;
				try { code = (byte)GetMember(__0, "Code"); } catch { return true; }
				try { sender = (int)GetMember(__0, "Sender"); } catch { return true; }

				// Rule 0: Frozen players are dropped unconditionally (motion, voice, avatar sync)
				if (sender > 0 && PlayerFreezeModule.IsActorFrozen(sender))
				{
					return false;
				}

				if (!_fEnabled) return true;

				// If Code cannot be resolved on this build every event reads as code 0 from nobody,
				// and a rate limit on that would mute the ENTIRE room. Unidentifiable = untouched.
				if (_mCode == null) return true;

				// Rule 1: explicit block list.
				if (_blockTable[code])
				{
					Drop(code, sender);
					return false;
				}

				// Rule 2: per-(actor, code) rate limit. The server is exempt (see header), and so are
				// the room-state and core-stream codes (see _neverRate).
				if (sender <= 0) return true;
				if (_neverRate[code]) return true;

				long now = NowMs;
				long key = Key(sender, code);
				lock (Gate)
				{
					if (_suspendedUntil.TryGetValue(key, out long until))
					{
						if (now < until)
						{
							DropLocked(code, sender, key);
							return false;
						}
						_suspendedUntil.Remove(key);
						SuspendedCount = _suspendedUntil.Count;
					}

					if (!_windows.TryGetValue(key, out Window w))
					{
						// Bounded: a room cannot legitimately produce more distinct pairs than this;
						// hitting it means someone is cycling codes, and a reset is safer than growth.
						if (_windows.Count >= 4096) _windows.Clear();
						w = new Window { StartMs = now, Count = 0 };
						_windows[key] = w;
					}
					else if (now - w.StartMs >= 1000)
					{
						w.StartMs = now;
						w.Count = 0;
					}

					if (++w.Count <= _fRate) return true;

					// Over the limit: mute this pair and say so ONCE, not once per dropped event.
					int rate = w.Count;
					_suspendedUntil[key] = now + _fSuspendMs;
					SuspendedCount = _suspendedUntil.Count;
					w.Count = 0;
					if (_fLog)
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[PhotonGuard] actor " + sender + " flooding event " + code + " at " + rate
							+ "+/s (limit " + _fRate + "), muted for " + (_fSuspendMs / 1000) + "s.");
					DropLocked(code, sender, key);
					return false;
				}
			}
			catch { return true; }
		}

		private static void Drop(byte code, int sender)
		{
			lock (Gate) DropLocked(code, sender, Key(sender, code));
		}

		// Called under Gate. Counts the drop; rebuilds the LastBlocked string only when the pair
		// changed; logs one line per DropLogEvery drops so a flood never becomes a log flood.
		private static void DropLocked(byte code, int sender, long key)
		{
			Blocked++;
			if (key != _lastBlockedKey)
			{
				_lastBlockedKey = key;
				LastBlocked = "code " + code + " from " + (sender > 0 ? "actor " + sender : "server");
			}
			if (_fLog && ++_dropsSinceLog >= DropLogEvery)
			{
				_dropsSinceLog = 0;
				VRChatArchiveModPlugin.Logger.LogInfo("[PhotonGuard] " + Blocked + " event(s) dropped so far — last: " + LastBlocked + ".");
			}
		}

		// Drop expired suspensions so SuspendedCount is the LIVE number, not a high-water mark.
		private static List<long> _expired;
		private static void Prune(long now)
		{
			lock (Gate)
			{
				if (_suspendedUntil.Count == 0) return;
				_expired ??= new List<long>();
				_expired.Clear();
				foreach (var kv in _suspendedUntil) if (now >= kv.Value) _expired.Add(kv.Key);
				foreach (long k in _expired) _suspendedUntil.Remove(k);
				SuspendedCount = _suspendedUntil.Count;
			}
		}

		// ---------------------------------------------------------------- member resolution

		// Code and Sender may surface as a property or a field depending on the build (see the
		// same note in NetworkLog). Both are tried, cached, and the outcome is said once — because
		// here an unresolved Code does not merely log zeros, it disarms the guard (checked above).
		private static MemberInfo _mCode, _mSender;
		private static bool _resolveAttempted, _resolutionLogged;

		private static object GetMember(object ev, string name)
		{
			bool isCode = name == "Code";
			MemberInfo mi = isCode ? _mCode : _mSender;
			if (mi == null && !_resolveAttempted)
			{
				Type t = ev.GetType();
				const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
				_mCode = (MemberInfo)t.GetProperty("Code", F) ?? t.GetField("Code", F);
				_mSender = (MemberInfo)t.GetProperty("Sender", F) ?? t.GetField("Sender", F);
				_resolveAttempted = true;
				LogResolution(t);
				mi = isCode ? _mCode : _mSender;
			}
			if (mi == null) return null;
			if (!NativeGuard.Alive(ev)) return null;
			if (mi is PropertyInfo p) return p.GetValue(ev);
			if (mi is FieldInfo f) return f.GetValue(ev);
			return null;
		}

		private static void LogResolution(Type t)
		{
			if (_resolutionLogged) return;
			_resolutionLogged = true;
			if (_mCode == null)
				VRChatArchiveModPlugin.Logger.LogError(
					"[PhotonGuard] EventData.Code NOT FOUND on " + t.Name + " — the guard cannot identify events and is passing everything through.");
			else
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[PhotonGuard] event shape on " + t.Name + ": Code=" + Describe(_mCode) + ", Sender=" + Describe(_mSender) + ".");
		}

		private static string Describe(MemberInfo mi)
			=> mi == null ? "NOT FOUND" : (mi is PropertyInfo ? "property " : "field ") + mi.Name;

		private static Type FindType(string name)
		{
			try
			{
				var t = Assembly.Load("Assembly-CSharp").GetType(name);
				if (t != null) return t;
			}
			catch { }
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { var t = asm.GetType(name, false); if (t != null) return t; } catch { }
			}
			return null;
		}
	}
}
