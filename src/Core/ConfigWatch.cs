using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace VRChatArchiveMod.Core
{
	// WHO CHANGED THIS SETTING, AND WHEN.
	//
	// A switch that reads ON in the desktop client while the mod behaves as if it were OFF has three
	// possible explanations, and they need three different fixes: the command never arrived, it
	// arrived and was refused, or it landed and something wrote it straight back. Reading the config
	// file afterwards cannot tell them apart — it only shows where the value ended up.
	//
	// So the value announces its own changes. BepInEx raises SettingChanged on every real change (it
	// short-circuits writes of an identical value, so a menu that rewrites its toggles every frame
	// stays silent here), and the mod marks the moments it is itself applying something from the
	// client. Anything that changes outside those moments came from somewhere else, and the log says
	// so instead of leaving it to be guessed at.
	internal static class ConfigWatch
	{
		// Set by ModControlModule around an incoming command, so a change can be attributed.
		internal static string ApplyingFrom;

		private static readonly List<string> Watched = new List<string>();

		internal static void Watch(ConfigEntryBase entry)
		{
			if (entry == null) return;
			string id = entry.Definition.Section + "/" + entry.Definition.Key;
			if (Watched.Contains(id)) return;
			Watched.Add(id);
			try
			{
				entry.ConfigFile.SettingChanged += (s, e) =>
				{
					try
					{
						if (e?.ChangedSetting == null) return;
						string changed = e.ChangedSetting.Definition.Section + "/" + e.ChangedSetting.Definition.Key;
						if (changed != id) return;
						string trace = Environment.StackTrace;
						VRChatArchiveModPlugin.Logger.LogInfo(
							$"[ConfigWatch] [{DateTime.Now:HH:mm:ss.fff}] {changed} -> {e.ChangedSetting.BoxedValue}  ({ApplyingFrom ?? "in-game menu or code"})\n{trace}");
					}
					catch { }
				};
			}
			catch { }
		}
	}
}
