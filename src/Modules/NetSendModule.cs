using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// NET SEND — what THIS client pushes onto the wire, counted per second and per event code.
	//
	// WHY. Two ServerTimeouts on 2026-09-02 ("VRChat is not responding") happened while the game
	// was demonstrably alive seconds earlier: the CONNECTION starved, not the CPU. Every guess about
	// the cause (a synced object ring, a guard, a hook) was a guess, because the mod could see what
	// came IN (NetworkLog) and nothing about what went OUT. EvilEye hooks the send side too
	// (OpRaiseEvent), which is where this comes from.
	//
	// HOW. One Harmony POSTFIX on Photon's OpRaiseEvent — found by SIGNATURE, never by name, since
	// the name is obfuscated and changes per build: a method whose parameters are (byte code, object
	// payload, RaiseEventOptions, SendOptions), on LoadBalancingClient and on VRChat's subclass
	// VRCNetworkingClient if it overrides it. The postfix does one array increment. Once a second
	// the counters roll into a 15-entry history; a spike is logged as it happens, and the whole
	// history is printed on every scene change — which is exactly the moment after a disconnect,
	// so the log that follows a timeout shows the last 15 s of outbound traffic above it.
	//
	// Receive-side only for reading: this module never sends, blocks or alters anything.
	public class NetSendModule : IModule
	{
		public override string Name => "NetSend";

		private static bool _hooked;
		public static string HookInfo = "not attached";
		private static int _attempts;
		private static float _nextTry;

		// Hot counters (this second). Byte-indexed so the postfix does no hashing.
		private static readonly int[] _codeNow = new int[256];
		private static int _totalNow;

		// Rolled statistics, for the log and the client.
		public sealed class Second { public int Total; public string Top = ""; public float At; }
		private static readonly List<Second> _history = new List<Second>(16);
		private const int HistoryLen = 15;
		public static int LastPerSec { get; private set; }
		public static int PeakPerSec { get; private set; }
		public static string LastTop { get; private set; } = "";
		private static float _nextRoll;

		// Above this many events in one second the line is written as a WARNING as it happens.
		// Normal play in a busy instance is a few dozen a second; a runaway loop is hundreds.
		private const int WarnPerSec = 150;

		public override void OnInitialize() { InstallHook(); }

		public override void OnUpdate()
		{
			// Arm on the RISING EDGE of the switch, so one flip is one round of dumping and leaving it
			// on does not re-arm forever.
			try
			{
				bool want = ModConfig.DumpOutbound.Value;
				if (want && !_dumpArmed) ArmDump();
				_dumpArmed = want;
			}
			catch { }

			try
			{
				float now = VaClock.Now;
				if (!_hooked && _attempts < 40 && now >= _nextTry) { _nextTry = now + 3f; _attempts++; InstallHook(); }
				if (now < _nextRoll) return;
				_nextRoll = now + 1f;
				Roll(now);
			}
			catch { }
		}

		private static void Roll(float now)
		{
			int total = _totalNow;
			// Top codes of the second, highest first, at most four.
			var sb = new StringBuilder();
			for (int k = 0; k < 4; k++)
			{
				int best = -1, bestN = 0;
				for (int c = 0; c < 256; c++) if (_codeNow[c] > bestN) { bestN = _codeNow[c]; best = c; }
				if (best < 0) break;
				if (sb.Length > 0) sb.Append(' ');
				sb.Append(best).Append('x').Append(bestN);
				_codeNow[best] = 0;
			}
			Array.Clear(_codeNow, 0, 256);
			_totalNow = 0;

			LastPerSec = total;
			LastTop = sb.ToString();
			if (total > PeakPerSec) PeakPerSec = total;
			_history.Add(new Second { Total = total, Top = LastTop, At = now });
			if (_history.Count > HistoryLen) _history.RemoveAt(0);

			if (total >= WarnPerSec)
				VRChatArchiveModPlugin.Logger.LogWarning("[NetSend] " + total + " event(s) sent this second (" + LastTop + ").");
		}

		// The last 15 s of outbound traffic, oldest first. Printed on scene change, i.e. right after
		// a disconnect-and-rejoin, so a timeout's log carries its own send history.
		public static string HistoryLine()
		{
			var sb = new StringBuilder();
			for (int i = 0; i < _history.Count; i++)
			{
				if (i > 0) sb.Append(" | ");
				sb.Append(_history[i].Total);
				if (!string.IsNullOrEmpty(_history[i].Top)) sb.Append('(').Append(_history[i].Top).Append(')');
			}
			return sb.Length == 0 ? "(nothing sent yet)" : sb.ToString();
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			try
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] outbound per second before this scene change (oldest first): " + HistoryLine()
					+ " — peak " + PeakPerSec + "/s. Hook: " + HookInfo);
			}
			catch { }
			_history.Clear();
			PeakPerSec = 0;
		}

		// ---------------------------------------------------------------- hook

		private static void InstallHook()
		{
			if (_hooked) return;
			try
			{
				int patched = 0;
				var notes = new StringBuilder();
				foreach (string typeName in new[] { "Photon.Realtime.LoadBalancingClient", "LoadBalancingClient", "VRCNetworkingClient" })
				{
					Type t = FindType(typeName);
					if (t == null) continue;
					foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						var ps = m.GetParameters();
						if (ps.Length != 4) continue;
						if (ps[0].ParameterType != typeof(byte)) continue;
						if (ps[2].ParameterType.Name.IndexOf("RaiseEventOptions", StringComparison.Ordinal) < 0) continue;
						if (ps[3].ParameterType.Name.IndexOf("SendOptions", StringComparison.Ordinal) < 0) continue;
						try
						{
							var post = new HarmonyMethod(typeof(NetSendModule).GetMethod(nameof(RaisePostfix), BindingFlags.Static | BindingFlags.NonPublic));
							VRChatArchiveModPlugin.HarmonyInstance.Patch(m, postfix: post);
							patched++;
							notes.Append(t.Name).Append('.').Append(m.Name).Append(' ');
						}
						catch (Exception pe) { notes.Append("[").Append(t.Name).Append('.').Append(m.Name).Append(" failed: ").Append(pe.Message).Append("] "); }
					}
				}
				if (patched == 0)
				{
					HookInfo = "no (byte, object, RaiseEventOptions, SendOptions) method found" + (notes.Length > 0 ? " " + notes : "");
					if (_attempts <= 1) VRChatArchiveModPlugin.Logger.LogWarning("[NetSend] " + HookInfo + " (will retry).");
					return;
				}
				_hooked = true;
				HookInfo = "postfix on " + notes.ToString().Trim();
				VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] armed — counting outbound Photon events (" + HookInfo + ").");
			}
			catch (Exception e)
			{
				HookInfo = "install failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogError("[NetSend] hook install failed: " + e);
			}
		}

		// ---------------------------------------------------------------- outbound packet dump
		//
		// READ-ONLY, CAPPED, AND OFF UNLESS ARMED. Counting told us the RATES per code; it could
		// never tell us what a packet CONTAINS, and every question about rewriting the pose stream
		// starts there: which code carries the body, and whether a rotation is in it at all.
		//
		// Arm it from the config (Network/DumpOutbound), rotate on the spot, and read the log. The
		// budget stops it by itself, so a forgotten toggle cannot bleed the send path.
		private static readonly int[] _dumpLeftPerCode = new int[256];
		private static int _dumpLeft;
		private const int DumpPerCode = 4;
		private const int DumpTotal = 40;
		private static bool _dumpArmed;

		/// <summary>Arm one round of dumping. Idempotent while a round is still running.</summary>
		public static void ArmDump()
		{
			if (_dumpLeft > 0) return;
			for (int i = 0; i < 256; i++) _dumpLeftPerCode[i] = DumpPerCode;
			_dumpLeft = DumpTotal;
			VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] dump armed — next " + DumpTotal
				+ " outbound event(s), at most " + DumpPerCode + " per code. MOVE AND ROTATE NOW.");
		}

		// Two increments and one compare. Nothing else may run here: this is on the send path of
		// every event, 150+ times a second in a busy instance.
		private static void RaisePostfix(byte __0, Il2CppSystem.Object __1)
		{
			_codeNow[__0]++;
			_totalNow++;
			if (_dumpLeft > 0 && _dumpLeftPerCode[__0] > 0) Dump(__0, __1);
		}

		// Out of line on purpose: the hot path above must stay branch-and-return.
		private static void Dump(byte code, Il2CppSystem.Object payload)
		{
			_dumpLeft--;
			_dumpLeftPerCode[code]--;
			try
			{
				if (payload == null)
				{
					VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] e" + code + ": payload null");
					return;
				}

				// Name FIRST, and only from a live object. Reading an il2cpp class name is metadata
				// only; casting a rotten proxy is what faults, so nothing is cast before this passes.
				if (!Core.NativeGuard.Alive(payload))
				{
					VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] e" + code + ": payload not alive");
					return;
				}
				string tn = Core.MenuCard.Il2CppNameOf(payload) ?? "?";

				// A FlatBuffer pose packet arrives as a byte array. Anything else is named and left
				// alone — guessing at the shape of a type we have not identified is how a send-path
				// hook kills the game.
				var bytes = payload.TryCast<Il2CppStructArray<byte>>();
				if (bytes == null)
				{
					VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] e" + code + ": " + tn + " (not a byte array)");
					return;
				}

				int n = bytes.Length;
				int show = n < 64 ? n : 64;
				var sb = new StringBuilder(show * 3 + 64);
				for (int i = 0; i < show; i++) sb.Append(bytes[i].ToString("x2"));
				VRChatArchiveModPlugin.Logger.LogInfo("[NetSend] e" + code + ": " + tn + " " + n
					+ " byte(s) " + sb + (n > show ? "…" : ""));
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[NetSend] dump of e" + code + " threw: " + e.Message);
			}
		}

		private static int _fullScans;
		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			// Simple-name fallback for obfuscated/global namespaces. Walking every type of every
			// assembly costs real milliseconds, so at most five such scans per session.
			if (_fullScans >= 5) return null;
			_fullScans++;
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					foreach (var t in asm.GetTypes())
						if (t.Name == full) return t;
				}
				catch { }
			}
			return null;
		}
	}
}
