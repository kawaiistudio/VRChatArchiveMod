using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// DIAGNOSTICS REPORT — the small file a tester can actually send back.
	//
	// LogCaptureModule already mirrors the WHOLE game log, but that runs to hundreds of
	// megabytes and nobody can mail it. This writes a compact, mod-focused report instead:
	//
	//   * a header describing the machine, the game and every mod setting
	//   * every line OUR plugin logs (info / warning / error), with timestamps
	//   * Unity errors and exceptions only — never the game's ordinary chatter
	//   * a HEALTH snapshot every 30 s (what each feature is actually doing)
	//   * PROBLEM lines: conditions that mean a feature is silently doing nothing
	//
	// The file is capped and rotated, so it stays small enough to attach to a message.
	// Written to BepInEx/VRChatArchiveMod/diagnostics/.
	public class DiagnosticsModule : IModule
	{
		public override string Name => "Diagnostics";

		private const long MaxBytes = 3L * 1024 * 1024;   // stays attachable
		private const float HealthEvery = 30f;
		private const float HealthEveryDebug = 5f;        // debug build: sample far more often

		// DEBUG BUILD. Either the config flag, or simply dropping a file named DEBUG next to the
		// reports — that way a "debug version" is one file to add, not a second DLL to keep in
		// sync with the real one (two binaries would silently drift apart).
		public static bool Debug { get; private set; }

		private static bool DetectDebug()
		{
			try { if (ModConfig.DebugMode != null && ModConfig.DebugMode.Value) return true; } catch { }
			try
			{
				string marker = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "DEBUG");
				return File.Exists(marker);
			}
			catch { return false; }
		}

		private static readonly object Lock = new object();
		private static StreamWriter _w;
		private static string _path = "(off)";
		private static long _bytes;
		public static string ReportPath => _path;
		// DISTINCT problems, not occurrences. Counting every line made the report say "16918
		// problem(s) recorded" — which is not a diagnosis, it is the same handful of VRChat errors
		// repeating thousands of times, and it buries anything that happened once. The count is now
		// how many DIFFERENT things went wrong; Occurrences keeps the raw total for context.
		public static int Problems { get; private set; }
		public static long Occurrences { get; private set; }

		private static readonly System.Collections.Generic.Dictionary<string, int> Seen =
			new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
		private const int MaxPerProblem = 3;   // write the first few, then only count

		// True when this one is still worth WRITING. Numbers, ids and paths are stripped so the
		// same fault with a different object is recognised as the same fault.
		private static bool ShouldWrite(string text)
		{
			try
			{
				Occurrences++;
				string key = Signature(text);
				lock (Lock)
				{
					Seen.TryGetValue(key, out int n);
					Seen[key] = n + 1;
					if (n == 0) { Problems++; return true; }
					if (n == MaxPerProblem)
					{
						Line("           | (further repeats of this one are counted, not written)");
						return false;
					}
					return n < MaxPerProblem;
				}
			}
			catch { return true; }
		}

		private static string Signature(string text)
		{
			if (string.IsNullOrEmpty(text)) return "";
			var sb = new System.Text.StringBuilder(Math.Min(text.Length, 160));
			int n = 0;
			foreach (char c in text)
			{
				if (n >= 160) break;
				if (char.IsDigit(c)) continue;      // instance ids, counts, coordinates
				sb.Append(c); n++;
			}
			return sb.ToString();
		}

		private float _nextHealth;
		private float _nextFlush;
		private Action<string, string, LogType> _unityHandler;
		private BepInEx.Logging.ManualLogSource _src;

		public override void OnInitialize()
		{
			try
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "diagnostics");
				Directory.CreateDirectory(dir);
				_path = Path.Combine(dir, "va-report_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
				// AutoFlush=false + a timed flush from OnUpdate. With AutoFlush the writer issued a
				// syscall per LINE, and one VRChat failed-join storm produced 468 exceptions in a
				// single second (each several lines) — thousands of blocking writes on the main
				// thread, i.e. a freeze exactly when the game is already struggling. The report is
				// still flushed every second and on shutdown/SaveNow, so nothing is lost in practice.
				_w = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };

				WriteHeader();

				// Our own plugin's output — this is what actually explains a broken feature.
				_src = VRChatArchiveModPlugin.Logger;
				if (_src != null) _src.LogEvent += OnPluginLog;

				// Unity's errors only. Ordinary Debug.Log traffic belongs in LogCapture. Routed through
				// Core.UnityLog rather than a converted Application.LogCallback: the delegate bridge is
				// closed on this build, and a diagnostics report that cannot see Unity's own errors is
				// exactly the report you need when something is broken.
				_unityHandler = OnUnityLog;
				Core.UnityLog.Subscribe(_unityHandler, "Diagnostics");

				// ALWAYS ON, not only in a debug build.
				//
				// This used to be inside the Debug branch, which meant a release build — the one
				// everybody actually runs — measured nothing. When the frame rate collapsed there
				// was no data at all, and the only way to get any was to know an undocumented
				// keybind. The measurement itself is two Stopwatch reads per module per phase:
				// a few microseconds a frame, against a report that names the culprit outright.
				ModuleManager.Profiling = true;

				Debug = DetectDebug();
				if (Debug)
				{
					Line("*** DEBUG BUILD — health every 5s, warnings captured ***");
					VRChatArchiveModPlugin.Logger.LogInfo("[Diagnostics] DEBUG MODE ON.");
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[Diagnostics] report: " + _path);
			}
			catch (Exception e)
			{
				try { VRChatArchiveModPlugin.Logger.LogWarning($"[Diagnostics] could not start: {Core.Unwrap.Describe(e)}"); } catch { }
			}
		}

		public override void OnShutdown()
		{
			try
			{
				if (_src != null) _src.LogEvent -= OnPluginLog;
				Core.UnityLog.Unsubscribe(_unityHandler); _unityHandler = null;
			}
			catch { }
			Line("=== session ended ===");
			lock (Lock) { try { _w?.Flush(); _w?.Dispose(); } catch { } _w = null; }
		}

		private void WriteHeader()
		{
			Line("==================================================================");
			Line($" VRCHAT ARCHIVE MOD  v{PluginInfo.Version}  —  diagnostics report");
			Line($" generated  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			Line("==================================================================");
			try
			{
				Line($" unity      : {Application.unityVersion}");
				Line($" game       : {Application.productName} {Application.version}");
				Line($" platform   : {Application.platform}   screen {Screen.width}x{Screen.height}");
				Line($" system     : {SystemInfo.operatingSystem}");
				Line($" gpu        : {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB)");
				Line($" cpu        : {SystemInfo.processorType} x{SystemInfo.processorCount}, {SystemInfo.systemMemorySize} MB RAM");
			}
			catch { }
			Line("");
			Line("--- settings ---");
			try
			{
				Line($" vaTags           : {ModConfig.VaTagsEnabled.Value}  plates={ModConfig.VaTagsShowPlates.Value}");
				Line($" fewTags          : {ModConfig.FewTagsEnabled.Value}");
				Line($" esp              : capsule={ModConfig.EspCapsule.Value} meshGlow={ModConfig.EspHighlight.Value} portals={ModConfig.EspPortals.Value} items={ModConfig.EspItems.Value} throughWalls={ModConfig.EspThroughWalls.Value}");
				Line($" radar            : {ModConfig.RadarEnabled.Value}");
				Line($" antiCrash        : {ModConfig.AntiCrashEnabled.Value}");
				Line($" udonLog          : {ModConfig.UdonLogEnabled.Value}  frameEvents={ModConfig.UdonLogFrameEvents.Value}");
				Line($" watchlist        : {ModConfig.WatchlistEnabled.Value}");
			}
			catch (Exception e) { Line(" (settings dump failed: " + e.Message + ")"); }
			Line("");
			Line("--- log ---");
		}

		// ---------------------------------------------------------------- capture

		private static void OnPluginLog(object sender, BepInEx.Logging.LogEventArgs e)
		{
			try
			{
				string lvl = e.Level.ToString().ToUpperInvariant();
				bool bad = e.Level == BepInEx.Logging.LogLevel.Error || e.Level == BepInEx.Logging.LogLevel.Warning;
				string body = e.Data?.ToString() ?? "";
				if (bad && !ShouldWrite(body)) return;
				Line($"[{DateTime.Now:HH:mm:ss}] {lvl,-7} {body}");
			}
			catch { }
		}

		private static void OnUnityLog(string condition, string stackTrace, LogType type)
		{
			try
			{
				if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert
				    && !(Debug && type == LogType.Warning)) return;
				Line($"[{DateTime.Now:HH:mm:ss}] UNITY-{type.ToString().ToUpperInvariant()} {Trim(condition, 400)}");
				if (!string.IsNullOrEmpty(stackTrace))
				{
					string st = Trim(stackTrace, 600).Replace("\r", "");
					foreach (string l in st.Split('\n'))
						if (l.Length > 0) Line("           | " + l);
				}
				Problems++;
			}
			catch { }
		}

		// ---------------------------------------------------------------- health

		public override void OnUpdate()
		{
			try
			{
				if (_w == null) return;
				float now = VaClock.Now;

				// Buffered writer (AutoFlush is off): push it to disk about once a second, so a log
				// storm costs one syscall per second instead of thousands per second.
				if (now >= _nextFlush)
				{
					_nextFlush = now + 1f;
					lock (Lock) { try { _w.Flush(); } catch { } }
				}

				SlowWatch(now);

				if (now < _nextHealth) return;
				_nextHealth = now + (Debug ? HealthEveryDebug : HealthEvery);
				Health();
			}
			catch { }
		}

		// SUSTAINED SLOWNESS, WHICH THE SPIKE HUNTER CANNOT SEE.
		//
		// A spike is one frame over 50 ms. That catches a stutter and misses the thing people
		// actually complain about: a machine that should run at 100 fps sitting at 22, where every
		// frame takes ~45 ms and not one of them crosses the bar. The whole session looks healthy
		// and nothing is ever written down.
		//
		// So this watches the AVERAGE instead, and when it is bad it writes what the profiler
		// already knows — the costliest modules, in ms per second of wall clock — straight into the
		// log. Rate-limited to once every 15 s: the point is to name the culprit, not to narrate.
		private static float _slowWindowStart, _slowFrames, _nextSlowReport;

		// Both destinations on purpose: the report file is the record, the BepInEx console is where
		// someone looking at a bad frame rate is actually looking.
		private static void Perf(string msg)
		{
			Line("  " + msg);
			try { VRChatArchiveModPlugin.Logger.LogWarning(msg); } catch { }
		}

		private static void SlowWatch(float now)
		{
			if (_slowWindowStart <= 0f) { _slowWindowStart = now; _slowFrames = 0f; return; }
			_slowFrames++;
			float span = now - _slowWindowStart;
			if (span < 1f) return;

			float fps = _slowFrames / span;
			_slowWindowStart = now;
			_slowFrames = 0f;

			if (fps >= SlowFps || now < _nextSlowReport) return;
			_nextSlowReport = now + 15f;

			var report = ModuleManager.ProfileReport();
			if (report.Count == 0)
			{
				Perf($"[Perf] {fps:F0} fps — profiler has no data yet.");
				return;
			}

			// Total first: it says immediately whether the mod is even the problem. A 45 ms frame
			// with 3 ms of mod in it is a VRChat/world problem, and chasing our own modules would
			// be chasing the wrong thing.
			float total = 0f;
			for (int i = 0; i < report.Count; i++) total += report[i].Value;

			// THE TWO NUMBERS THAT DECIDE HOW TO READ THE REST (2026-09-13).
			//
			// The same 400 ms/s means completely different things depending on whether the QuickMenu
			// was open (the menu's own drawing is most of it, and closing it costs nothing) and how
			// many players are in the instance (every per-player pass scales with it). Both were
			// missing, so every reading of this line needed a second investigation to interpret —
			// the menu state alone was what separated "31 ms/s" from "443 ms/s" in the same session.
			bool qmOpen = false;
			try { qmOpen = Core.QuickMenu.Visible; } catch { }
			int players = 0;
			try { players = VaTagsModule.Roster.Count; } catch { }

			var sb = new System.Text.StringBuilder(192);
			sb.Append("[Perf] ").Append(fps.ToString("F0")).Append(" fps — mod costs ")
			  .Append(total.ToString("F1")).Append(" ms/s in total")
			  .Append(" [menu ").Append(qmOpen ? "OPEN" : "shut")
			  .Append(", ").Append(players).Append(" player(s)]. Worst:");
			for (int i = 0; i < report.Count && i < 5; i++)
			{
				if (report[i].Value < 0.5f) break;
				sb.Append(' ').Append(report[i].Key).Append('=')
				  .Append(report[i].Value.ToString("F1")).Append("ms");
			}
			Perf(sb.ToString());
		}

		// Below this, a report is written. Chosen well under any refresh rate worth having, so it
		// stays quiet on a machine that is merely not at 144.
		private const float SlowFps = 70f;

		// A snapshot of what each feature is REALLY doing, plus explicit PROBLEM lines when a
		// feature is enabled yet demonstrably inert — the case that used to look like silence.
		private static void Health()
		{
			try
			{
				int players = -1;
				if (Core.VaPlayers.Ready)
					try { var ps = VRChatArchiveMod.Core.VaPlayers.All(); players = ps != null ? ps.Count : -1; } catch { players = -1; }

				Line("");
				Line($"--- health @ {DateTime.Now:HH:mm:ss}  ({Mathf.RoundToInt(1f / Mathf.Max(0.0001f, VaClock.Delta))} fps) ---");
				Line($"  players in instance : {players}");
				Line($"  roster (VaTags)     : {VaTagsModule.Roster.Count}   tagged users in DB: {VaTagsModule.RecordsLoaded}");
				Line($"  tag fetch           : {VaTagsModule.LastFetchInfo}");
				Line($"  last tag status     : {VaTagsModule.LastStatus}");
				Line($"  udon events         : {UdonLogModule.PerSecond}/s   total {UdonLogModule.TotalSeen}");

				// Measured per-module frame cost, when the profiler is on. This turns "the mod
				// lags" into a ranked number instead of a guess.
				if (ModuleManager.Profiling)
				{
					var rep = ModuleManager.ProfileReport();
					Line($"  module cost         : {ModuleManager.ProfileTotalMs():F2} ms/s total (16.6 = one 60fps frame per second)");
					for (int i = 0; i < rep.Count && i < 6; i++)
						Line($"      {rep[i].Value,7:F2} ms/s  {rep[i].Key}");
				}

				// Actionable problems.
				// "0 records" alone is NOT a failure: the first health snapshot can land before the
				// first fetch has come back (5s in debug), and the endpoint answers fine. Only report
				// when a fetch actually FAILED — verified live: GET /api/va-tags returns 200.
				if (ModConfig.VaTagsEnabled.Value && VaTagsModule.RecordsLoaded == 0
					&& (VaTagsModule.LastFetchInfo ?? "").IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
					Problem("VA tags are ON but the last fetch FAILED: " + VaTagsModule.LastFetchInfo);
				// "No Udon event yet" is NOT evidence the hook failed: a world can legitimately
				// contain zero UdonBehaviours (a real capture showed exactly that), and while the
				// client is stuck in the Error World there is nothing to fire at all. Only report it
				// when the hook genuinely did not attach, or after a long grace period in a world
				// that actually has Udon objects — otherwise this alone logged hundreds of "problems".
				if (ModConfig.UdonLogEnabled.Value && !UdonLogModule.HookAttached)
					Problem("Udon console is ON but the RunProgram hook did not attach.");
			}
			catch (Exception e) { Line("  (health failed: " + e.Message + ")"); }
		}

		private static void Problem(string msg)
		{
			Problems++;
			Line("  >>> PROBLEM: " + msg);
		}

		// ---------------------------------------------------------------- io

		public static void Line(string text)
		{
			lock (Lock)
			{
				if (_w == null) return;
				try
				{
						if (_bytes > MaxBytes) return;          // cap: the report must stay sendable
					// This report exists to be SENT to someone, so it is the last place a credential
					// should survive. The loader key rides in on VRChat's own command-line dump.
					text = Core.Redact.Block(text);
					_w.WriteLine(text);
					_bytes += text.Length + 2;
					if (_bytes > MaxBytes)
					{
						_w.WriteLine("");
						_w.WriteLine("=== report size cap reached — further lines are in BepInEx/LogOutput.log ===");
					}
				}
				catch { }
			}
		}

		// Menu button: force a snapshot now, so a tester can grab the file immediately.
		public static string SaveNow()
		{
			Line("");
			Line($"=== manual snapshot requested at {DateTime.Now:HH:mm:ss} ===");
			Health();
			lock (Lock) { try { _w?.Flush(); } catch { } }
			return _path;
		}

		private static string Trim(string s, int max)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
	}
}
