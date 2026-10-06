using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// NETWORK EVENT LOG — a passive record of the Photon events this client RECEIVES.
	//
	// LISTEN ONLY. This module never transmits, raises, queues or replays anything. It attaches a
	// single Harmony POSTFIX to the receive-side funnel and reads two values off the event object.
	// The transmit-side API names are deliberately absent from this file, so `grep -v '^\s*//'`
	// over it genuinely proves the claim — which is why this note avoids spelling them out.
	//
	// The hook target, from a metadata scan of the shipped assemblies:
	//   Photon.Client.IPhotonPeerListener.OnEvent(EventData) is the receive contract.
	//   LoadBalancingClient implements it; VRChat's VRCNetworkingClient OVERRIDES it, so the
	//   override is what the vtable actually reaches. Its name is NOT obfuscated (unlike the
	//   downstream handlers, which appear only as Method_Public_Virtual_Final_New_Void_EventData_0),
	//   and being virtual it cannot be inlined away by IL2CPP AOT.
	// We patch that ONE method: patching LoadBalancingClient.OnEvent as well would double-log.
	//
	// EventData instances are RECYCLED by Photon (PeerBase keeps a single reusableEventData, and
	// byte payloads come from a pool that is returned after dispatch). Everything is therefore
	// snapshotted to plain values inside the postfix; no EventData reference ever outlives it.
	public class NetworkLogModule : IModule
	{
		public override string Name => "NetworkLog";

		public const int Capacity = 400;

		public sealed class Entry
		{
			public float Time;          // realtimeSinceStartup, so the EVENTS console can interleave
			public string Clock;        // these rows with the Udon ones in true order
			public byte Code;
			public int Sender;
			public int Repeats = 1;
		}

		private static readonly List<Entry> Log = new List<Entry>(Capacity);
		private static readonly object Gate = new object();
		private static readonly Dictionary<byte, int> CodeCounts = new Dictionary<byte, int>();

		// ---- what a code IS, measured rather than guessed -------------------------------
		// VRChat does not publish the meaning of its Photon event codes, and the shipped
		// assemblies carry no enum for them (checked: PhotonClient.dll has no EventCode type and
		// the VRC assemblies are obfuscated past the point of naming one). So this console does not
		// invent names. It reports two things it can actually establish:
		//   PEAK RATE  \u2014 continuous serialisation runs at hundreds/s, a real event is rare. That is
		//                what "interesting" filters on, and it self-calibrates per world.
		//   SHAPE      \u2014 the payload the event carries (byte blob and its size, hashtable, \u2026),
		//                sampled a few times per code and then cached, so it costs nothing per event.
		private static readonly Dictionary<byte, int> CodePeak = new Dictionary<byte, int>();
		private static readonly Dictionary<byte, int> _codeWindow = new Dictionary<byte, int>();
		private static readonly Dictionary<byte, string> CodeShape = new Dictionary<byte, string>();
		private static readonly Dictionary<byte, int> _shapeSamples = new Dictionary<byte, int>();
		private const int ShapeSampleCount = 3;

		public static int TotalSeen { get; private set; }
		public static int PerSecond { get; private set; }
		private static int _rateCount;
		private static float _rateAt;

		private static bool _hooked;
		public static bool HookAttached => _hooked;
		public static string HookInfo = "not attached";

		private static StreamWriter _sink;
		// Last asserted state of NetworkLogToFile, so the sink can be opened/closed on the edge
		// instead of only once at startup. Primed in OnInitialize to match OpenSinkIfWanted().
		private static bool _sinkWanted;

		public override void OnInitialize()
		{
			InstallHook();
			OpenSinkIfWanted();
			// Record what was just asserted so the runtime edge test in OnUpdate does not immediately
			// re-open (or close) a sink that is already in the right state.
			try { _sinkWanted = ModConfig.NetworkLogToFile.Value || DiagnosticsModule.Debug; } catch { }
		}

		public override void OnUpdate()
		{
			try
			{
				// THE FILE SWITCH ACTS AT RUNTIME (2026-09-13). OpenSinkIfWanted() was only ever called
				// from OnInitialize, and WriteSink only tests `_sink == null`: turning NetworkLogToFile
				// ON did nothing until the game was restarted, and turning it OFF kept the file open
				// and kept writing to it. Both edges are honoured here, once, on the second tick below.
				bool wantFile = false;
				try { wantFile = ModConfig.NetworkLogToFile.Value || DiagnosticsModule.Debug; } catch { }
				if (wantFile != _sinkWanted)
				{
					_sinkWanted = wantFile;
					if (wantFile)
					{
						OpenSinkIfWanted();
					}
					else
					{
						lock (Gate) { try { _sink?.Flush(); _sink?.Dispose(); } catch { } _sink = null; }
					}
				}

				float now = VaClock.Now;
				if (now - _rateAt >= 1f)
				{
					_rateAt = now;
					PerSecond = _rateCount;
					_rateCount = 0;
					RollCodeRates();      // per-code peaks, which is what "interesting" filters on
					lock (Gate) { try { _sink?.Flush(); } catch { } }
				}
			}
			catch { }
		}

		public override void OnShutdown()
		{
			lock (Gate) { try { _sink?.Flush(); _sink?.Dispose(); } catch { } _sink = null; }
		}

		// ---------------------------------------------------------------- hook

		private static void InstallHook()
		{
			if (_hooked) return;
			try
			{
				Type client = FindType("VRCNetworkingClient");
				if (client == null)
				{
					HookInfo = "VRCNetworkingClient not found";
					VRChatArchiveModPlugin.Logger.LogWarning("[NetworkLog] VRCNetworkingClient not found — no network events will be seen.");
					return;
				}

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
					VRChatArchiveModPlugin.Logger.LogWarning("[NetworkLog] " + HookInfo);
					return;
				}

				var post = new HarmonyMethod(typeof(NetworkLogModule)
					.GetMethod(nameof(OnEventPostfix), BindingFlags.Static | BindingFlags.NonPublic));
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, postfix: post);
				// The PREFIX only brackets WATCHED codes (entry stamp + payload capture before VRChat's
				// handler); it never returns false, so nothing is ever dropped by this module.
				try
				{
					var pre = new HarmonyMethod(typeof(NetworkLogModule)
						.GetMethod(nameof(OnEventPrefix), BindingFlags.Static | BindingFlags.NonPublic));
					VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: pre);
					_prefixArmed = true;
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[NetworkLog] timing prefix not installed (handler duration unavailable): " + e.Message); }

				_hooked = true;
				HookInfo = "postfix on VRCNetworkingClient.OnEvent" + (_prefixArmed ? " (+ timing prefix)" : "");
				VRChatArchiveModPlugin.Logger.LogInfo("[NetworkLog] armed — listening on VRCNetworkingClient.OnEvent (receive only).");
			}
			catch (Exception e)
			{
				HookInfo = "install failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogError($"[NetworkLog] hook install failed: {e}");
			}
		}

		// Runs for EVERY inbound event, so it does the cheapest possible work: an enabled check,
		// two property reads, and a merge against the previous entry.
		private static void OnEventPostfix(object __0)
		{
			try
			{
				if (__0 == null) return;

				// NOTHING WANTED, NOTHING READ (2026-09-13). This runs for EVERY inbound Photon event
				// — hundreds a second. The enabled test used to sit ~25 lines below, AFTER two
				// reflection member reads and a lock, so a logger that was switched OFF still paid
				// that cost on the whole receive path. Watched codes (event 33, for the block
				// observer) are captured even with logging off, so the fast-out has to consider both;
				// when neither wants anything, there is genuinely nothing to do.
				bool logOn = false;
				try { logOn = ModConfig.NetworkLogEnabled.Value; } catch { }
				if (!logOn)
				{
					bool anyWatched;
					lock (Gate) anyWatched = _watchCodes.Count > 0;
					if (!anyWatched) return;
				}

				// Snapshot NOW: Photon reuses the EventData instance, so keeping the object or its
				// payload past this method would log whatever the NEXT event overwrites it with.
				byte code = 0;
				int sender = -1;
				try { code = (byte)GetMember(__0, "Code"); } catch { }
				try { sender = (int)GetMember(__0, "Sender"); } catch { }

				// WATCHED CODES (event 33 = moderation / instance control) are captured WITH their
				// decoded payload, even when logging is off, because the block observer asked for them
				// specifically. Rare by nature, so decoding them here costs nothing in steady state.
				bool watched = false;
				lock (Gate) watched = _watchCodes.Count > 0 && _watchCodes.Contains(code);
				if (watched)
				{
					if (_prefixArmed && _inWatched == code)
					{
						// Captured in the prefix; this is the EXIT stamp of VRChat's handler.
						double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _enterTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
						var lc = _lastCap; if (lc != null) lc.HandlerMs = ms;
						_inWatched = 0;
						try { WatchedExit?.Invoke(code, _seq); } catch { }
					}
					else
					{
						// The prefix did not bracket this one (re-entrant dispatch, or it bailed): capture here and say so.
						Capture(code, sender, __0);
						if (_prefixArmed) { try { VRChatArchiveModPlugin.Logger.LogInfo("[NetworkLog] watched event " + code + " reached the postfix without a prefix entry (re-entrant dispatch?) — captured late, no handler timing for it."); } catch { } }
					}
				}

				if (!ModConfig.NetworkLogEnabled.Value) return;

				// Shape is sampled only the first few times a code is ever seen: reading the payload
				// on every event would put reflection on the receive path of a hundreds-per-second
				// stream, which is exactly the kind of cost a logger must not add.
				int samples;
				lock (Gate) { _shapeSamples.TryGetValue(code, out samples); }
				if (samples < ShapeSampleCount)
				{
					string shape = DescribePayload(__0);
					lock (Gate)
					{
						_shapeSamples[code] = samples + 1;
						if (!string.IsNullOrEmpty(shape)) CodeShape[code] = shape;
					}
				}

				TotalSeen++;
				_rateCount++;
				Push(code, sender);
			}
			catch { /* a logger must never break the networking it observes */ }
		}

		// PREFIX (observer): for WATCHED codes only, stamp the entry and capture the payload before
		// VRChat's handler can consume it. Always lets the original run (void prefix).
		private static void OnEventPrefix(object __0)
		{
			try
			{
				if (__0 == null) return;
				int nWatched; lock (Gate) nWatched = _watchCodes.Count;
				if (nWatched == 0) return;
				byte code = 0; int sender = -1;
				try { code = (byte)GetMember(__0, "Code"); } catch { return; }
				bool watched; lock (Gate) watched = _watchCodes.Contains(code);
				if (!watched) return;
				try { sender = (int)GetMember(__0, "Sender"); } catch { }
				_enterTicks = System.Diagnostics.Stopwatch.GetTimestamp();
				_inWatched = code;
				try { WatchedEnter?.Invoke(code, _seq + 1); } catch { }
				Capture(code, sender, __0);
			}
			catch { }
		}

		// Code and Sender may be exposed as either a property or a field depending on how the game
		// build was generated, and asking only for a property is how this ends up logging a stream of
		// "event 0" from an unknown sender: GetProperty returns null, the cast on null throws, the
		// catch upstream swallows it, and the module looks armed while recording nothing real. So it
		// tries both and SAYS what it found, once.
		private static MemberInfo _mCode, _mSender;
		private static bool _resolutionLogged;

		private static object GetMember(object ev, string name)
		{
			bool isCode = name == "Code";
			MemberInfo mi = isCode ? _mCode : _mSender;
			if (mi == null)
			{
				Type t = ev.GetType();
				const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
				mi = (MemberInfo)t.GetProperty(name, F) ?? t.GetField(name, F);
				if (isCode) _mCode = mi; else _mSender = mi;
				LogResolution(t);
				if (mi == null) return null;
			}
			if (!Core.NativeGuard.Alive(ev)) return null;
			if (mi is PropertyInfo p) return p.GetValue(ev);
			if (mi is FieldInfo f) return f.GetValue(ev);
			return null;
		}

		private static void LogResolution(Type t)
		{
			if (_resolutionLogged || _mCode == null && _mSender == null) return;
			if (_mCode == null || _mSender == null) return;   // wait until both have been attempted
			_resolutionLogged = true;
			VRChatArchiveModPlugin.Logger.LogInfo(
				"[NetworkLog] event shape on " + t.Name + ": Code=" + Describe(_mCode)
				+ ", Sender=" + Describe(_mSender) + ".");
		}

		private static string Describe(MemberInfo mi)
			=> mi == null ? "NOT FOUND (every event will read as 0)"
				: (mi is PropertyInfo ? "property " : "field ") + mi.Name;

		private static void Push(byte code, int sender)
		{
			string clock = DateTime.Now.ToString("HH:mm:ss");
			float t = 0f;
			try { t = VaClock.Now; } catch { }
			lock (Gate)
			{
				CodeCounts.TryGetValue(code, out int c);
				CodeCounts[code] = c + 1;
				_codeWindow.TryGetValue(code, out int w);
				_codeWindow[code] = w + 1;

				// Bursts of the same code from the same actor collapse into one row with a counter,
				// exactly like the Udon console — otherwise serialization traffic drowns everything.
				if (Log.Count > 0)
				{
					var last = Log[Log.Count - 1];
					if (last.Code == code && last.Sender == sender) { last.Repeats++; WriteSink(code, sender, last.Repeats); return; }
				}

				Log.Add(new Entry { Time = t, Clock = clock, Code = code, Sender = sender });
				if (Log.Count > Capacity) Log.RemoveAt(0);
				WriteSink(code, sender, 1);
			}
		}

		// ---------------------------------------------------------------- file sink

		private static void OpenSinkIfWanted()
		{
			try
			{
				if (!ModConfig.NetworkLogToFile.Value && !DiagnosticsModule.Debug) return;
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "network");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "net_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
				_sink = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
				_sink.WriteLine("VRCHAT ARCHIVE MOD — inbound Photon events (receive only, nothing is ever sent)");
				_sink.WriteLine("time      code  sender  repeats");
				VRChatArchiveModPlugin.Logger.LogInfo("[NetworkLog] writing to " + path);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[NetworkLog] sink failed: {e.Message}"); }
		}

		// Called under Gate. Buffered writer, flushed once a second from OnUpdate — an event storm
		// must not turn into thousands of blocking writes on the main thread.
		private static void WriteSink(byte code, int sender, int repeats)
		{
			if (_sink == null) return;
			try { _sink.WriteLine($"{DateTime.Now:HH:mm:ss}  {code,4}  {sender,6}  {repeats,7}"); }
			catch { }
		}

		// ---------------------------------------------------------------- read side

		public static List<Entry> Snapshot()
		{
			lock (Gate) return new List<Entry>(Log);
		}

		public static List<KeyValuePair<byte, int>> TopCodes(int n)
		{
			lock (Gate)
			{
				var list = new List<KeyValuePair<byte, int>>(CodeCounts);
				list.Sort((a, b) => b.Value.CompareTo(a.Value));
				if (list.Count > n) list.RemoveRange(n, list.Count - n);
				return list;
			}
		}

		public static void Clear()
		{
			// CodePeak and CodeShape deliberately SURVIVE a clear: they are what the console knows
			// about this world's traffic, and re-learning them would let bulk sync flood the view
			// again for a second every time the user pressed CLEAR.
			lock (Gate) { Log.Clear(); CodeCounts.Clear(); _codeWindow.Clear(); }
			TotalSeen = 0;
		}

		// ---------------------------------------------------------------- classification

		// Roll the per-code counters into a peak. Called once a second from OnUpdate, next to the
		// overall rate.
		private static void RollCodeRates()
		{
			lock (Gate)
			{
				foreach (var kv in _codeWindow)
				{
					CodePeak.TryGetValue(kv.Key, out int peak);
					if (kv.Value > peak) CodePeak[kv.Key] = kv.Value;
				}
				_codeWindow.Clear();
			}
		}

		// Bulk = a code that has EVER been seen running at sync rate. Sticky on the peak rather
		// than the current rate, because serialisation goes quiet when nobody moves and a
		// momentary lull must not dump 300 rows of it back into the console.
		public static bool IsBulk(byte code)
		{
			int limit;
			try { limit = Mathf.Max(1, ModConfig.NetworkBulkPerSecond.Value); }
			catch { limit = 20; }
			lock (Gate) { return CodePeak.TryGetValue(code, out int peak) && peak > limit; }
		}

		public static int PeakOf(byte code)
		{
			lock (Gate) { return CodePeak.TryGetValue(code, out int p) ? p : 0; }
		}

		// Deliberately literal. Naming these would mean guessing, and a console that guesses is
		// worse than one that reports \u2014 the code number IS the identity here.
		public static string Label(byte code) => "event " + code;

		public static string ShapeOf(byte code)
		{
			lock (Gate) { return CodeShape.TryGetValue(code, out string s) ? s : ""; }
		}

		// Rows the console should show: everything, or only the codes that are not bulk sync.
		public static List<Entry> VisibleRows()
		{
			bool only;
			try { only = ModConfig.NetworkInterestingOnly.Value; } catch { only = true; }
			lock (Gate)
			{
				if (!only) return new List<Entry>(Log);
				var outp = new List<Entry>(Log.Count);
				foreach (var e in Log) if (!IsBulk(e.Code)) outp.Add(e);
				return outp;
			}
		}

		// How many distinct codes we have classified, and how many of them are bulk. Shown in the
		// UDON tab so "interesting only" can be seen to be doing something.
		public static (int codes, int bulk) CodeStats()
		{
			lock (Gate)
			{
				int limit;
				try { limit = Mathf.Max(1, ModConfig.NetworkBulkPerSecond.Value); }
				catch { limit = 20; }
				int bulk = 0;
				foreach (var kv in CodePeak) if (kv.Value > limit) bulk++;
				return (CodeCounts.Count, bulk);
			}
		}

		// ---------------------------------------------------------------- payload shape

		// Describes what an event CARRIES, read from the snapshot inside the postfix and never
		// retained. Read-only: it looks at the payload, it does not decode, replay or forward it.
		private static PropertyInfo _pCustom, _pParams, _pParamCount;
		// ---------------------------------------------------------------- payload capture (watched codes)
		//
		// EVENT 33 = MODERATION / INSTANCE CONTROL (per the community event-code list; unverifiable
		// statically because this build ships no event-code enum, so it is captured empirically). The
		// block observer registers code 33 here; every 33 that arrives is decoded to a string and kept
		// in a small ring the observer reads. READ-ONLY: the event is only inspected, never altered or
		// dropped — this postfix runs AFTER VRChat has already handled it.
		public sealed class Cap
		{
			public float Time; public string Clock; public byte Code; public int Sender; public string Payload;
			public int Seq;                 // 1-based capture number
			public long Ticks;              // Stopwatch.GetTimestamp() at arrival (prefix = before VRChat's handler ran)
			public int Frame;               // Unity frame of arrival (-1 off the main thread)
			public double HandlerMs = -1;   // how long VRChat's OnEvent took for this event (postfix - prefix); -1 = unknown
		}
		private static readonly List<Cap> Caps = new List<Cap>(64);
		private static readonly HashSet<byte> _watchCodes = new HashSet<byte>();
		private const int MaxCaps = 64;

		// Watched-code handler bracketing (3.9.77). The prefix stamps the entry and captures the payload
		// BEFORE VRChat's own handler runs; the postfix stamps the exit. Between the two, InsideWatchedCode
		// is the code being handled, so another observer can tell "done synchronously inside OnEvent(33)"
		// from "done frames later". Both callbacks are observers registered by Event33TraceModule.
		public static Action<byte, int> WatchedEnter, WatchedExit;
		private static byte _inWatched; private static long _enterTicks; private static int _seq; private static bool _prefixArmed; private static Cap _lastCap;
		public static byte InsideWatchedCode => _inWatched;
		public static int LastSeq => _seq;

		/// <summary>Start decoding+keeping the payload of this event code (e.g. 33). Idempotent.</summary>
		public static void WatchCode(byte code) { lock (Gate) _watchCodes.Add(code); }
		/// <summary>Stop capturing and forget what was captured.</summary>
		public static void UnwatchCodes() { lock (Gate) { _watchCodes.Clear(); Caps.Clear(); } }
		/// <summary>Captured watched-code events at or after <paramref name="from"/> (realtimeSinceStartup).</summary>
		public static List<Cap> CapturesSince(float from)
		{
			lock (Gate) { var o = new List<Cap>(); for (int i = 0; i < Caps.Count; i++) if (Caps[i].Time >= from) o.Add(Caps[i]); return o; }
		}

		private static System.Reflection.PropertyInfo _pCustomCap;
		private static void Capture(byte code, int sender, object ev)
		{
			string payload;
			try { payload = DecodeCapture(ev); }
			catch (Exception e) { payload = "decode unavailable (" + e.GetType().Name + ")"; }
			int frame = -1; try { frame = Time.frameCount; } catch { }
			long ticks = (_inWatched == code && _enterTicks != 0) ? _enterTicks : System.Diagnostics.Stopwatch.GetTimestamp();
			var c = new Cap { Time = VaClock.Now, Clock = DateTime.Now.ToString("HH:mm:ss.fff"), Code = code, Sender = sender, Payload = payload, Ticks = ticks, Frame = frame };
			lock (Gate) { c.Seq = ++_seq; Caps.Add(c); if (Caps.Count > MaxCaps) Caps.RemoveAt(0); }
			_lastCap = c;
			try { VRChatArchiveModPlugin.Logger.LogInfo("[NetworkLog] captured event " + code + " (watched) from actor " + sender + ": " + payload); } catch { }

			// THE BLOCK LISTS COME FROM HERE, AND FROM NOWHERE ELSE.
			//
			// Event 33 subtype 21 is the server stating, on entering an instance, who has blocked whom.
			// This capture already decodes it safely -- it has been printing correct payloads all session
			// without a single incident, and it does NOT deep-enumerate il2cpp collections, which this mod
			// documents as able to crash.
			//
			// The alternative was the BlockDebug observer, and that cost the owner FOUR crashes in one
			// afternoon (0xc0000005 in coreclr, fault offset 0x0) because its per-frame WatchName snapshot
			// walks a player class whose members cannot be placed on 1903. Feeding the marker from the
			// logger instead means the feature needs no debug switch, no hooks on VRChat methods, and no
			// off-main-thread object reads.
			if (code == 33)
			{
				try { Core.Event33Moderation.Consume(payload); } catch { }
			}
		}

		// The custom data of a watched event, decoded WITHOUT deep il2cpp enumeration (which this mod
		// documents as able to crash). A byte[] is hex-dumped and its printable ASCII runs extracted —
		// which is where a usr_/wrld_ id would show if the moderation packet carries one; anything else
		// is reported by type and Count only.
		private static string DecodeCapture(object ev)
		{
			var sb = new StringBuilder();
			try
			{
				_pCustomCap ??= ev.GetType().GetProperty("CustomData");
				object data = _pCustomCap?.GetValue(ev);
				if (data == null) { sb.Append("CustomData=null"); }
				else
				{
					int len = ByteLength(data);
					if (len >= 0)
					{
						sb.Append("bytes[").Append(len).Append("] ");
						HexAscii(data, len, sb);
					}
					else
					{
						sb.Append("type=").Append(Il2CppTypeName(data));
						// The dictionary is NOT enumerated. Reading it.Value / TryCast builds a managed
						// wrapper by calling il2cpp_object_get_class on each value, and that is an
						// UNCATCHABLE access violation when a value pointer is not what the object pool
						// expects — it crashed VRChat on the very first event 33 (3.9.73). Only the count
						// is taken, by managed reflection, which constructs no wrapper.
						try { var cp = data.GetType().GetProperty("Count"); if (cp?.GetValue(data) is int n) sb.Append(" count=").Append(n); } catch { }
						// 3.9.77: guarded RAW read of the dictionary's entries (Core/Event33Payload). No wrapper
						// is built for any key or value, so the 3.9.73 fault cannot recur on this path.
						try
						{
							bool decode = false;
							try { decode = ModConfig.BlockDebugDecodeEvent33 != null && ModConfig.BlockDebugDecodeEvent33.Value; } catch { }
							var ob = data as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
							if (decode && ob != null) sb.Append(' ').Append(Core.Event33Payload.Describe(ob.Pointer));
						}
						catch (Exception e) { sb.Append(" decode unavailable (").Append(e.GetType().Name).Append(')'); }
					}
				}
			}
			catch (Exception e) { sb.Append("CustomData unavailable (").Append(e.GetType().Name).Append(')'); }
			try
			{
				_pParams ??= ev.GetType().GetProperty("Parameters");
				object pars = _pParams?.GetValue(ev);
				if (pars != null)
				{
					_pParamCount ??= pars.GetType().GetProperty("Count");
					if (_pParamCount?.GetValue(pars) is int pn && pn > 0) sb.Append(" | params(").Append(pn).Append(')');
				}
			}
			catch { }
			return sb.ToString();
		}

		// Hex + printable-ASCII of the first bytes of an il2cpp byte[], plus any ASCII run >=4 chars
		// (this is what surfaces a usr_/wrld_ id inside a serialized moderation blob). Bounded read.
		private static void HexAscii(object data, int len, StringBuilder sb)
		{
			try
			{
				var arr = (data as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?
					.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>>();
				if (arr == null) { sb.Append("(not readable as byte[])"); return; }
				int n = Math.Min(len, 96);
				var hex = new StringBuilder(n * 3);
				var run = new StringBuilder();
				var runs = new List<string>();
				for (int i = 0; i < n; i++)
				{
					byte b = arr[i];
					hex.Append(b.ToString("X2")).Append(' ');
					if (b >= 0x20 && b < 0x7F) { run.Append((char)b); }
					else { if (run.Length >= 4) runs.Add(run.ToString()); run.Clear(); }
				}
				if (run.Length >= 4) runs.Add(run.ToString());
				sb.Append("hex=").Append(hex.ToString().TrimEnd());
				if (len > n) sb.Append(" …(+").Append(len - n).Append(" more)");
				if (runs.Count > 0) sb.Append(" | ascii: ").Append(string.Join(" · ", runs.ToArray()));
			}
			catch (Exception e) { sb.Append("(hex unavailable: ").Append(e.GetType().Name).Append(')'); }
		}

		private static string DescribePayload(object ev)
		{
			try
			{
				string shape = "";
				try
				{
					_pCustom ??= ev.GetType().GetProperty("CustomData");
					object data = _pCustom?.GetValue(ev);
					if (data != null)
					{
						string tn = Il2CppTypeName(data);
						int len = ByteLength(data);
						shape = len >= 0 ? $"bytes[{len}]" : (string.IsNullOrEmpty(tn) ? "" : tn);
					}
				}
				catch { }

				try
				{
					_pParams ??= ev.GetType().GetProperty("Parameters");
					object pars = _pParams?.GetValue(ev);
					if (pars != null)
					{
						_pParamCount ??= pars.GetType().GetProperty("Count");
						if (_pParamCount?.GetValue(pars) is int n && n > 0)
							shape = string.IsNullOrEmpty(shape) ? $"params({n})" : shape + $" +{n}";
					}
				}
				catch { }

				return shape;
			}
			catch { return ""; }
		}

		private static string Il2CppTypeName(object o)
		{
			try
			{
				if (o is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)
				{
					IntPtr k = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(b.Pointer);
					string n = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
						Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(k));
					return n ?? "";
				}
				return o.GetType().Name;
			}
			catch { return ""; }
		}

		// -1 when the payload is not a byte array.
		private static int ByteLength(object o)
		{
			try
			{
				if (o is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)
				{
					var arr = b.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>>();
					if (arr != null) return arr.Length;
				}
				if (o is byte[] managed) return managed.Length;
			}
			catch { }
			return -1;
		}

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
