using System;
using System.Collections.Generic;

namespace VRChatArchiveMod.Core
{
	// Central registry + dispatcher for feature modules.
	// Every dispatch is guarded so one misbehaving module can never take down the others
	// (this is the same fail-safe philosophy as the Munchen client's anti-crash design).
	public static class ModuleManager
	{
		private static readonly List<IModule> Modules = new List<IModule>();
		private static bool _uiReady;

		// ---- per-module profiler -------------------------------------------------------
		// Guessing at what costs frame time did not settle it, so measure instead. Each
		// dispatch is timed with raw Stopwatch timestamps (no allocation), accumulated per
		// module, and turned into a "ms per second of wall clock" figure once a second. That
		// number is directly comparable between modules: 16.6 ms/s means the module eats one
		// frame's worth of time every second at 60 fps.
		private static readonly Dictionary<string, long> Ticks = new Dictionary<string, long>(StringComparer.Ordinal);
		private static readonly Dictionary<string, float> LastMs = new Dictionary<string, float>(StringComparer.Ordinal);
		private static float _windowStart;
		private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		public static bool Profiling { get; set; }

		private static void Account(IModule m, long startTs)
		{
			if (!Profiling) return;
			long d = System.Diagnostics.Stopwatch.GetTimestamp() - startTs;
			string k = m?.Name ?? "?";
			Ticks.TryGetValue(k, out long cur);
			Ticks[k] = cur + d;
		}

		// Called once per frame from the runner; rolls the 1-second window.
		public static void ProfileTick()
		{
			if (!Profiling) return;
			float now = VaClock.Now;
			if (_windowStart <= 0f) { _windowStart = now; return; }
			float span = now - _windowStart;
			if (span < 1f) return;
			_windowStart = now;
			LastMs.Clear();
			foreach (var kv in Ticks) LastMs[kv.Key] = (float)(kv.Value * TickMs / span);
			Ticks.Clear();
		}

		// THE COSTLIEST MODULE OF THE SECOND CURRENTLY IN PROGRESS, in milliseconds.
		//
		// ProfileReport() answers a different question: it is the LAST COMPLETED one-second window,
		// so at the moment a spike happens it describes the second BEFORE it — which is precisely
		// the second the spike is not in. For attributing a stutter we want the time accumulated so
		// far in the window the stutter just landed in, and that is the live Ticks table.
		public static KeyValuePair<string, float> CurrentHot()
		{
			string best = null;
			long most = 0;
			foreach (var kv in Ticks)
				if (kv.Value > most) { most = kv.Value; best = kv.Key; }
			return new KeyValuePair<string, float>(best ?? "?", (float)(most * TickMs));
		}

		// Modules sorted by cost, worst first: (name, ms per second).
		public static List<KeyValuePair<string, float>> ProfileReport()
		{
			var list = new List<KeyValuePair<string, float>>(LastMs);
			list.Sort((a, b) => b.Value.CompareTo(a.Value));
			return list;
		}

		public static float ProfileTotalMs()
		{
			float t = 0f;
			foreach (var kv in LastMs) t += kv.Value;
			return t;
		}

		public static void Register(IModule module)
		{
			if (module == null) return;
			Modules.Add(module);
		}

		public static T Get<T>() where T : IModule
		{
			for (int i = 0; i < Modules.Count; i++)
			{
				if (Modules[i] is T t) return t;
			}
			return null;
		}

		public static void InitializeAll()
		{
			// Name each module to disk, FLUSHED, before initialising it. A native access violation
			// is not catchable and kills the process instantly, so anything still buffered is lost
			// and the BepInEx log simply stops -- which is why the crashing module stayed unknown.
			// Whatever this file ends on is the module that died.
			string trace = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "module-init.log");
			foreach (var m in Modules)
			{
				try
				{
					using var fs = new System.IO.FileStream(trace, System.IO.FileMode.Append,
						System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
					var b = System.Text.Encoding.UTF8.GetBytes(
						DateTime.Now.ToString("HH:mm:ss.fff") + "  -> " + m.GetType().Name + Environment.NewLine);
					fs.Write(b, 0, b.Length);
					fs.Flush(true);
				}
				catch { }
				try { m.OnInitialize(); }
				catch (Exception e) { LogModuleError(m, "OnInitialize", e); }
			}
			try
			{
				System.IO.File.AppendAllText(trace, "=== TOUS LES MODULES INITIALISES ===" + Environment.NewLine);
			}
			catch { }
		}

		public static void NotifyUiReady()
		{
			if (_uiReady) return;
			_uiReady = true;
			foreach (var m in Modules)
			{
				try { m.OnUiReady(); }
				catch (Exception e) { LogModuleError(m, "OnUiReady", e); }
			}
		}

		// Flushed, per-module trace of the first frames only. The process now dies inside the very
		// first Update tick, and a native access violation leaves no managed trace behind -- so a line
		// is written to disk before each module callback while VA_TRACE_TICKS is set, and whatever
		// module-tick.log ends on is the callback that did not return. Off in normal play.
		private static readonly bool TraceTicks = System.Environment.GetEnvironmentVariable("VA_TRACE_TICKS") == "1";
		private static int _tracedTicks;
		// The name of the callback in flight, kept for the crash report: a native access violation
		// leaves nothing behind, so the last module to be entered is written where the next launch
		// can read it.
		internal static string InFlight = "";

		private static void Tick(string phase, IModule m)
		{
			InFlight = phase + " " + m.GetType().Name;
			if (!TraceTicks || _tracedTicks > 4000) return;
			_tracedTicks++;
			try
			{
				using var fs = new System.IO.FileStream(
					System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "module-tick.log"),
					System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
				var b = System.Text.Encoding.UTF8.GetBytes(phase + " " + m.GetType().Name + System.Environment.NewLine);
				fs.Write(b, 0, b.Length);
				fs.Flush(true);
			}
			catch { }
		}

		public static void Update()
		{
			// SUPPORTER GATE. Every module passes through these loops, so the check lives here
			// rather than in sixty separate modules where one omission would reopen everything.
			// Evaluated per frame on purpose: the account level arrives asynchronously from the
			// client bridge and can lapse mid-session. See ModGate.
			ModGate.NoteState();
			bool gateOpen = ModGate.Allowed;
			for (int i = 0; i < Modules.Count; i++)
			{
				if (!gateOpen && !ModGate.IsFree(Modules[i].GetType().Name)) continue;
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				Tick("U", Modules[i]);
				try { Modules[i].OnUpdate(); Tick("u", Modules[i]); }
				catch (Exception e) { LogModuleError(Modules[i], "OnUpdate", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void LateUpdate()
		{
			bool gateOpen = ModGate.Allowed;
			for (int i = 0; i < Modules.Count; i++)
			{
				if (!gateOpen && !ModGate.IsFree(Modules[i].GetType().Name)) continue;
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				Tick("L", Modules[i]);
				try { Modules[i].OnLateUpdate(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnLateUpdate", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void FixedUpdate()
		{
			bool gateOpen = ModGate.Allowed;
			for (int i = 0; i < Modules.Count; i++)
			{
				if (!gateOpen && !ModGate.IsFree(Modules[i].GetType().Name)) continue;
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				Tick("F", Modules[i]);
				try { Modules[i].OnFixedUpdate(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnFixedUpdate", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void OnGui()
		{
			for (int i = 0; i < Modules.Count; i++)
			{
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				Tick("G", Modules[i]);
				try { Modules[i].OnGui(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnGui", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void SceneLoaded(int buildIndex)
		{
			ApiUsers.Clear();   // player objects are gone; their cached APIUser handles are stale
			PlayerRef.Invalidate();   // and so is the local player PlayerRef now holds across frames
			VRChatArchiveMod.Modules.VaTagsModule.ForgetLocalUserId();   // read against the scene that just went away
			VRChatArchiveMod.Modules.FewTagsModule.ForgetNameplates();   // the manager went with the old scene
			foreach (var m in Modules)
			{
				Tick("S", m);
				try { m.OnSceneLoaded(buildIndex); Tick("s", m); }
				catch (Exception e) { LogModuleError(m, "OnSceneLoaded", e); }
			}
		}

		public static void ShutdownAll()
		{
			foreach (var m in Modules)
			{
				try { m.OnShutdown(); }
				catch (Exception e) { LogModuleError(m, "OnShutdown", e); }
			}
		}

		// Rate-limited so a module that throws EVERY frame cannot bury the game. Without this, one
		// broken module produced a full exception + stack string per frame, each of which the
		// diagnostics writer then wrote to disk — the logging itself became the performance
		// problem, exactly when something was already wrong. Same message = at most one line per
		// 10 s, with a count of what was suppressed.
		private sealed class ErrRate { public float At; public int Suppressed; }
		private static readonly System.Collections.Generic.Dictionary<string, ErrRate> Rates =
			new System.Collections.Generic.Dictionary<string, ErrRate>(StringComparer.Ordinal);

		private static void LogModuleError(IModule module, string phase, Exception e)
		{
			try
			{
				string key = (module?.Name ?? "?") + "/" + phase;
				float now = VaClock.Now;
				if (!Rates.TryGetValue(key, out ErrRate r))
				{
					Rates[key] = new ErrRate { At = now };
				}
				else if (now - r.At < 10f)
				{
					r.Suppressed++;
					return;
				}
				else
				{
					if (r.Suppressed > 0)
						VRChatArchiveModPlugin.Logger?.LogError($"[{module?.Name ?? "?"}] {phase}: {r.Suppressed} identical error(s) suppressed in the last {now - r.At:F0}s.");
					r.At = now; r.Suppressed = 0;
				}
			}
			catch { }
			VRChatArchiveModPlugin.Logger?.LogError($"[{module?.Name ?? "?"}] {phase} threw: {e}");
		}
	}
}
