using System;
using System.Collections.Generic;

namespace VRChatArchiveMod.Core
{
	/// <summary>
	/// "THAT PASS WAS SLOW, AND HERE IS WHAT IT WAS DOING."
	///
	/// The per-module profiler (ModuleManager) answers WHICH module costs time. It cannot answer
	/// WHY, because a module is one number: VaTags reading 236 ms/s told us nothing about whether
	/// that was thirty-five players resolved the hard way or one pathological plate. Finding that
	/// out meant reading code and guessing — twice, wrongly, before the real cause turned up.
	///
	/// HighlightEspModule already solved this for itself ("world pass took 28 ms (26 pickups...)"),
	/// and those lines are what actually located its cost. This is that same idea, shared, so any
	/// expensive pass can report itself in one line at the call site.
	///
	/// IT MUST NOT BECOME THE COST IT MEASURES. Two rules:
	///   * a threshold — a pass that ran fast says nothing at all, so the normal case is two
	///     Stopwatch reads and a compare (no allocation, no string built);
	///   * a rate limit per tag — a pass that is slow EVERY time says so once every few seconds
	///     instead of sixty times a second, which is the difference between a diagnosis and a
	///     second performance problem.
	/// The detail string is built by the caller, so pass it as an interpolated string ONLY when it
	/// is cheap to build; anything expensive should be computed inside the `if` the caller already
	/// has.
	/// </summary>
	internal static class PerfLog
	{
		private static readonly Dictionary<string, float> _nextAt = new Dictionary<string, float>(StringComparer.Ordinal);

		/// <summary>Timestamp to hand back to Slow(). Raw ticks: no allocation, no DateTime.</summary>
		public static long Start() => System.Diagnostics.Stopwatch.GetTimestamp();

		public static double Ms(long start)
			=> (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		/// <summary>Reports the pass ONLY when it exceeded thresholdMs, and at most once per
		/// everySec for this tag. Never throws — a diagnostic must not be able to break the thing
		/// it is watching.</summary>
		public static void Slow(string tag, long start, double thresholdMs, string detail, float everySec = 10f)
		{
			try
			{
				double ms = Ms(start);
				if (ms < thresholdMs) return;
				float now = VaClock.Now;
				if (_nextAt.TryGetValue(tag, out float next) && now < next) return;
				_nextAt[tag] = now + everySec;
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[" + tag + "] pass took " + ms.ToString("0.0") + " ms — " + detail);
			}
			catch { }
		}
	}
}
