using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace VRChatArchiveMod.Core
{
	// DROPS THE BENIGN Il2CppInterop ERROR SPAM FROM THE CONSOLE AND DISK LOG.
	//
	// On this VRChat build Il2CppInterop logs a flood of red lines at startup that look alarming but
	// are harmless framework noise, not the mod and not a fault:
	//   "Field <X> was not found on class <Y>"   -- il2cpp strips compile-time constant fields
	//     (Char.MaxValue, Int32.MinValue, Encoding.UTF8, Single.NaN ...); the generated static cctor
	//     cannot bind them and says so once per field. The mod never reads those constants.
	//   "Assembly <X> is not registered in il2cpp"  -- an interop assembly (VRC.SDKBase/SDK3) whose
	//     types moved on this build; MissingTypeGuard already handles their absence.
	//   "Failed to init IL2CPP patch backend ..."   -- one optional fast-path Il2CppInterop skips.
	//
	// None of it affects behaviour, but it buries the mod's real messages. This wraps the existing
	// BepInEx listeners (console + disk) in a proxy that forwards everything EXCEPT those exact
	// patterns from the Il2CppInterop source. Any other error -- including a real Il2CppInterop one --
	// passes through untouched. Fully guarded: if anything about the log pipeline is unexpected it
	// leaves logging exactly as it was.
	internal sealed class LogNoiseFilter : ILogListener
	{
		private readonly ILogListener[] _downstream;
		private LogNoiseFilter(ILogListener[] downstream) { _downstream = downstream; }

		// Receive every level; the forwarding decision (and the noise drop) is made in LogEvent so each
		// downstream listener still applies its own level filter exactly as before.
		public LogLevel LogLevelFilter => LogLevel.All;

		internal static void Install()
		{
			try
			{
				var current = new List<ILogListener>(Logger.Listeners);
				if (current.Count == 0) return;
				foreach (var l in current) if (l is LogNoiseFilter) return;   // already installed

				var proxy = new LogNoiseFilter(current.ToArray());
				foreach (var l in current) Logger.Listeners.Remove(l);
				Logger.Listeners.Add(proxy);
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[LogFilter] bruit Il2CppInterop filtre (champs constants strippes, assemblys absentes) — "
					+ "les vraies erreurs passent toujours.");
			}
			catch (Exception e)
			{
				try { VRChatArchiveModPlugin.Logger?.LogWarning("[LogFilter] non installe : " + e.Message); } catch { }
			}
		}

		private static bool IsNoise(LogEventArgs e)
		{
			try
			{
				if (e?.Source == null || e.Source.SourceName != "Il2CppInterop") return false;
				string m = e.Data?.ToString();
				if (string.IsNullOrEmpty(m)) return false;
				return m.IndexOf("was not found on class", StringComparison.Ordinal) >= 0
					|| m.IndexOf("is not registered in il2cpp", StringComparison.Ordinal) >= 0
					|| m.IndexOf("Failed to init IL2CPP patch backend", StringComparison.Ordinal) >= 0;
			}
			catch { return false; }
		}

		public void LogEvent(object sender, LogEventArgs eventArgs)
		{
			if (IsNoise(eventArgs)) return;
			for (int i = 0; i < _downstream.Length; i++)
				try
				{
					// Preserve each downstream listener's own level filter, exactly as BepInEx would
					// have applied it before we sat in front of them.
					if ((_downstream[i].LogLevelFilter & eventArgs.Level) != LogLevel.None)
						_downstream[i].LogEvent(sender, eventArgs);
				}
				catch { }
		}

		public void Dispose()
		{
			for (int i = 0; i < _downstream.Length; i++)
				try { _downstream[i].Dispose(); } catch { }
		}
	}
}
