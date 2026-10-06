using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// WHICH WORLD YOU ARE IN, READ OFF VRCHAT'S OWN LOG LINES.
	//
	// The primary source is RoomManager -> ApiWorldInstance -> world.id (VaTagsModule.CurrentInstance),
	// and on a build where those classes are renamed it hands back an empty id. Everything keyed on the
	// world then goes quiet at once: the client's Udon world profiles, its preset packs, the world name
	// in the sync. The failure is silent, which is the worst part — nothing says "I do not know where
	// you are", the features simply do nothing.
	//
	// VRChat writes the answer to its own log on every join, in plain text that no obfuscator touches:
	//     [Behaviour] Joining wrld_xxxxxxxx-....:12345~region(eu)
	//     [Behaviour] Joining or Creating Room: <the world's name>
	// UnityLog already hooks Application.CallLogCallback (no delegate bridge, see Core/UnityLog), so
	// these lines arrive live and cost one regex on the few lines that start with "Joining".
	//
	// This is a FALLBACK, not a replacement: when the game's own objects answer, they win — they are
	// right about a world change the instant it happens, where a log line is right a moment later.
	internal static class WorldFromLog
	{
		private static readonly Regex JoinRe = new Regex(@"wrld_[0-9a-fA-F-]{36}", RegexOptions.Compiled);
		private static bool _armed;

		internal static string WorldId = "";
		internal static string WorldName = "";
		internal static string InstanceId = "";

		internal static void Arm()
		{
			if (_armed) return;
			_armed = true;
			try { UnityLog.Subscribe(OnLine, "WorldFromLog"); }
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[WorldFromLog] " + e.Message); }
		}

		private static void OnLine(string condition, string stack, LogType type)
		{
			try
			{
				if (string.IsNullOrEmpty(condition)) return;
				int at = condition.IndexOf("Joining", StringComparison.Ordinal);
				if (at < 0) return;

				// "Joining or Creating Room: <name>" — the world's name, on its own line.
				int room = condition.IndexOf("Joining or Creating Room:", StringComparison.Ordinal);
				if (room >= 0)
				{
					string name = condition.Substring(room + "Joining or Creating Room:".Length).Trim();
					if (name.Length > 0 && name.Length < 200) WorldName = name;
					return;
				}

				Match m = JoinRe.Match(condition);
				if (!m.Success) return;
				string id = m.Value;
				if (!string.Equals(id, WorldId, StringComparison.Ordinal))
				{
					WorldId = id;
					InstanceId = "";
					VRChatArchiveModPlugin.Logger.LogInfo("[WorldFromLog] monde courant lu dans le log de VRChat : " + id);
				}
				// "...wrld_xxx:12345~region(eu)" — the instance number sits between ':' and '~'.
				int colon = condition.IndexOf(':', m.Index + m.Length - 1);
				if (colon > 0 && colon + 1 < condition.Length)
				{
					string tail = condition.Substring(colon + 1);
					int tilde = tail.IndexOf('~');
					if (tilde > 0) tail = tail.Substring(0, tilde);
					tail = tail.Trim();
					if (tail.Length > 0 && tail.Length < 40) InstanceId = tail;
				}
			}
			catch { }
		}
	}
}
