using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace VRChatArchiveMod.Core
{
	// HUD ON / OFF — one switch over everything the mod draws on your screen.
	//
	// The radar, the PLAYERS and INSTANCE LOG panels, the glows, the Udon overlay: each has its own
	// switch, and clearing the screen meant finding all of them, then remembering what had been on
	// when you wanted them back. This is that one press.
	//
	// IT REMEMBERS, IT DOES NOT RESET. Switching off writes down which members were on and turns them
	// off; switching on puts back exactly that list. Turning the HUD off and on again must leave the
	// mod as it found it — a master switch that silently enables things you had deliberately off is
	// worse than no master switch.
	//
	// THE MENUS ARE NOT TOUCHED. VRChat's own menu, the Archive tab and the wing panels live inside
	// the menu, not over the game, and they stay exactly where they are.
	internal static class HudMaster
	{
		// Every switch this master owns, by a short stable key (the key is what gets written to the
		// config, so renaming one would lose a saved state — don't).
		private static IEnumerable<KeyValuePair<string, ConfigEntry<bool>>> Members()
		{
			yield return new KeyValuePair<string, ConfigEntry<bool>>("radar", ModConfig.RadarEnabled);
			yield return new KeyValuePair<string, ConfigEntry<bool>>("panels", ModConfig.InstancePanelsEnabled);
			yield return new KeyValuePair<string, ConfigEntry<bool>>("capsule", ModConfig.EspCapsule);
			yield return new KeyValuePair<string, ConfigEntry<bool>>("highlight", ModConfig.EspHighlight);
			yield return new KeyValuePair<string, ConfigEntry<bool>>("items", ModConfig.EspItems);
			yield return new KeyValuePair<string, ConfigEntry<bool>>("portals", ModConfig.EspPortals);
			yield return new KeyValuePair<string, ConfigEntry<bool>>("udon", ModConfig.UdonLogOverlay);
		}

		internal static bool On => ModConfig.HudEnabled == null || ModConfig.HudEnabled.Value;

		internal static string Status = "";

		internal static void Toggle() => Set(!On);

		internal static void Set(bool on)
		{
			try
			{
				if (on == On) return;
				if (!on) TurnOff(); else TurnOn();
				if (ModConfig.HudEnabled != null) ModConfig.HudEnabled.Value = on;
				VRChatArchiveModPlugin.Logger.LogInfo("[HUD] " + Status);
				try { Toast.Show(on ? "HUD on" : "HUD off — the mod's menus still work"); } catch { }
			}
			catch (Exception e)
			{
				Status = "HUD: " + e.Message;
				try { VRChatArchiveModPlugin.Logger.LogWarning("[HUD] " + e); } catch { }
			}
		}

		private static void TurnOff()
		{
			var wasOn = new List<string>();
			foreach (var m in Members())
			{
				if (m.Value == null || !m.Value.Value) continue;
				wasOn.Add(m.Key);
				m.Value.Value = false;
			}
			if (ModConfig.HudSaved != null) ModConfig.HudSaved.Value = string.Join(",", wasOn.ToArray());
			Status = "off — " + wasOn.Count + " overlay(s) hidden, remembered for when you switch it back on";
		}

		private static void TurnOn()
		{
			string saved = ModConfig.HudSaved != null ? ModConfig.HudSaved.Value ?? "" : "";
			// NOTHING REMEMBERED = NOTHING TO RESTORE. That happens when the config was edited by hand
			// or the switch was already off at first launch; putting a guessed set of overlays on the
			// screen would be the master switch deciding for the user, so it says so instead.
			if (saved.Trim().Length == 0)
			{
				Status = "on — nothing was remembered, so no overlay was turned back on; pick the ones you want";
				return;
			}
			var want = new HashSet<string>(saved.Split(','), StringComparer.OrdinalIgnoreCase);
			int back = 0;
			foreach (var m in Members())
			{
				if (m.Value == null || !want.Contains(m.Key)) continue;
				m.Value.Value = true;
				back++;
			}
			Status = "on — " + back + " overlay(s) back as they were";
		}
	}
}
