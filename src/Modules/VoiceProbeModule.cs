using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// VOICE PROBE — TEMPORARY. Find out what carries player voice on THIS build.
	//
	// The voice-relay attempt showed nothing: NetSend never counted an outbound Photon code 7, and
	// the receive hook saw 0 voice packets. Either voice rides some OTHER Photon event code, or it
	// bypasses the Photon event bus entirely (a separate voice/data channel). This tells the two
	// apart empirically: shout with a friend and read the log.
	//
	// TWO INSTRUMENTS, both read-only, logged once a second and ONLY when something is happening:
	//   1. INBOUND PHOTON EVENTS per code per second, with a rough payload size and the sending
	//      actors. A talking player is a stream: ~20-50 small packets a second. Whichever code
	//      jumps when your friend shouts is the voice carrier (and the relay can target it). If
	//      NOTHING in this list moves while they are clearly talking, voice is NOT a Photon event.
	//   2. REMOTE VOICE AUDIO SOURCES. Every remote player carries an AudioSource for their voice;
	//      when they talk it plays. This lights up whether or not (1) does, so a friend shouting
	//      with silence in (1) but a hit in (2) proves voice is on a separate transport.
	//
	// Delete this module once the answer is in hand. It is registered last in Plugin.cs and gated
	// on a config flag (VoiceProbe/Enabled) so it can be switched off without a rebuild.
	public class VoiceProbeModule : IModule
	{
		public override string Name => "VoiceProbe";

		private static bool _hooked;
		private static PropertyInfo _pCode, _pSender, _pCustom;

		// Per-code counters for the current second.
		private sealed class Bucket { public int Count; public int MinLen = int.MaxValue; public int MaxLen; public readonly HashSet<int> Senders = new HashSet<int>(); }
		private static readonly Dictionary<byte, Bucket> _sec = new Dictionary<byte, Bucket>();
		private static readonly object Gate = new object();
		private static float _nextRoll;

		public override void OnInitialize()
		{
			if (!Enabled) return;
			InstallHook();
		}

		private static bool Enabled
		{
			get { try { return ModConfig.VoiceProbeEnabled != null && ModConfig.VoiceProbeEnabled.Value; } catch { return true; } }
		}

		public override void OnUpdate()
		{
			if (!Enabled) return;
			try
			{
				if (!_hooked) { InstallHook(); }
				float now = VaClock.Now;
				if (now < _nextRoll) return;
				_nextRoll = now + 1f;
				RollEvents();
				ScanVoiceSources();
			}
			catch { }
		}

		// ---------------------------------------------------------------- inbound events

		private static void InstallHook()
		{
			if (_hooked) return;
			try
			{
				Type client = FindType("VRCNetworkingClient");
				if (client == null) { return; }
				MethodInfo target = null;
				foreach (var m in client.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (m.Name != "OnEvent") continue;
					var ps = m.GetParameters();
					if (ps.Length == 1 && ps[0].ParameterType.Name.Contains("EventData")) { target = m; break; }
				}
				if (target == null) return;
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, postfix: new HarmonyMethod(typeof(VoiceProbeModule).GetMethod(nameof(OnEventPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[VoiceProbe] armed — TEMPORARY. Shout with a friend; the per-second lines below show which inbound Photon code carries voice, or that none does.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VoiceProbe] hook failed: " + e.Message); }
		}

		private static void OnEventPostfix(object __0)
		{
			if (__0 == null) return;
			// SWITCHED OFF, NOTHING MEASURED (2026-09-13). This postfix runs for EVERY inbound Photon
			// event — hundreds a second — and had no switch test: with the probe off it still did
			// three reflection GetValue calls, took a lock and added to a HashSet, while RollEvents()
			// (the only thing that ever clears _sec) was gated off, so the buckets grew without
			// bound for the whole session. The hook stays patched on purpose — un-patching a method
			// on the Photon receive path at runtime is the risky operation, not the un-taken branch.
			if (!Enabled) return;
			try
			{
				var t = __0.GetType();
				_pCode ??= t.GetProperty("Code");
				_pSender ??= t.GetProperty("Sender");
				_pCustom ??= t.GetProperty("CustomData");
				if (_pCode == null) return;
				byte code = (byte)_pCode.GetValue(__0);
				int sender = -1;
				try { sender = (int)_pSender.GetValue(__0); } catch { }
				int len = -1;
				try { len = ByteLength(_pCustom?.GetValue(__0)); } catch { }
				lock (Gate)
				{
					if (!_sec.TryGetValue(code, out var b)) { b = new Bucket(); _sec[code] = b; }
					b.Count++;
					if (len >= 0) { if (len < b.MinLen) b.MinLen = len; if (len > b.MaxLen) b.MaxLen = len; }
					if (sender > 0) b.Senders.Add(sender);
				}
			}
			catch { }
		}

		private static void RollEvents()
		{
			var lines = new List<string>();
			lock (Gate)
			{
				foreach (var kv in _sec)
				{
					var b = kv.Value;
					if (b.Count <= 0) continue;
					string size = b.MaxLen < 0 ? "?" : (b.MinLen == b.MaxLen ? b.MinLen + "b" : b.MinLen + "-" + b.MaxLen + "b");
					lines.Add("code " + kv.Key + " x" + b.Count + "/s (" + size + ", " + b.Senders.Count + " sender" + (b.Senders.Count == 1 ? "" : "s") + ")");
				}
				_sec.Clear();
			}
			if (lines.Count == 0) return;   // quiet second: no log
			lines.Sort((a, x) => 0);
			VRChatArchiveModPlugin.Logger.LogInfo("[VoiceProbe] inbound this second: " + string.Join("  |  ", lines));
		}

		// ---------------------------------------------------------------- voice audio sources
		//
		// LEVEL, NOT isPlaying. A streaming voice source can report isPlaying forever while carrying
		// silence, so the signal is the actual output level: 256 samples read off the source each
		// second, RMS above a small floor = that player is producing voice audio right now.
		//
		// WHO PLAYS IT. The first time a player is heard, every Behaviour on the voice source's
		// GameObject and its two parents is named by il2cpp class. The voice DECODER/receiver lives
		// right there, obfuscated or not — that class name is the next thing to open in the interop
		// and is what a relay must hook when voice is not a Photon event.
		private static readonly HashSet<int> _dumped = new HashSet<int>();
		private static Il2CppStructArray<float> _buf;
		private static bool _localDumped;

		private static void ScanVoiceSources()
		{
			try
			{
				if (!_localDumped) { _localDumped = true; DumpLocalVoiceSide(); }
				_buf ??= new Il2CppStructArray<float>(256);
				// CAP: GetOutputData per source cost ~955 ms/s once and dropped the game to 1 fps.
				// Voice = code 1 is proven, so this is only a spot check: at most 8 players a second.
				var talking = new List<string>();
				int scanned = 0;
				foreach (var p in VaTagsModule.Roster)
				{
					if (p == null || p.IsLocal || p.Transform == null) continue;
					if (scanned++ >= 8) break;
					AudioSource[] srcs;
					try { srcs = p.Transform.GetComponentsInChildren<AudioSource>(true); } catch { continue; }
					if (srcs == null) continue;
					for (int i = 0; i < srcs.Length; i++)
					{
						var s = srcs[i];
						if (s == null) continue;
						float rms = 0f;
						try
						{
							s.GetOutputData(_buf, 0);
							double acc = 0; for (int k = 0; k < 256; k++) { float v = _buf[k]; acc += v * v; }
							rms = (float)Math.Sqrt(acc / 256.0);
						}
						catch { continue; }
						if (rms < 0.005f) continue;
						string nm = "?"; try { nm = s.gameObject.name; } catch { }
						talking.Add(p.Name + "[" + nm + " rms=" + rms.ToString("F3") + "]");
						if (_dumped.Add(p.PlayerId)) DumpVoiceOwner(p.Name, s);
						break;
					}
				}
				if (talking.Count > 0)
					VRChatArchiveModPlugin.Logger.LogInfo("[VoiceProbe] TALKING now: " + string.Join(", ", talking)
						+ " — if no inbound code above moves with this, voice bypasses Photon events.");
			}
			catch { }
		}

		// The components around a voice source: this GameObject, its parent, its grandparent.
		private static void DumpVoiceOwner(string player, AudioSource s)
		{
			try
			{
				var sb = new StringBuilder();
				Transform t = s.transform;
				for (int depth = 0; depth < 3 && t != null; depth++, t = t.parent)
				{
					sb.Append(depth == 0 ? "" : " <- ").Append('\'').Append(t.name).Append("'{");
					var comps = t.GetComponents<Component>();
					bool first = true;
					if (comps != null)
						foreach (var c in comps)
						{
							if (c == null) continue;
							string n; try { n = MenuCard.Il2CppNameOf(c); } catch { continue; }
							if (string.IsNullOrEmpty(n) || n == "Transform") continue;
							if (!first) sb.Append(", "); first = false;
							sb.Append(n);
						}
					sb.Append('}');
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[VoiceProbe] VOICE OWNER for " + player + ": " + sb);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VoiceProbe] owner dump failed: " + e.Message); }
		}

		// The local player's mic/voice side, once: every Behaviour under the local player root whose
		// class name smells like microphone, voice, speech, audio, codec. The SENDER a relay would
		// have to feed is in this list.
		private static void DumpLocalVoiceSide()
		{
			try
			{
				var lt = PlayerRef.LocalTransform();
				if (lt == null) { _localDumped = false; return; }   // not spawned yet: retry next tick
				Transform root = lt; int g = 0; while (root.parent != null && g++ < 32) root = root.parent;
				var seen = new HashSet<string>(StringComparer.Ordinal);
				var sb = new StringBuilder();
				var comps = root.GetComponentsInChildren<Component>(true);
				if (comps != null)
					foreach (var c in comps)
					{
						if (c == null) continue;
						string n; try { n = MenuCard.Il2CppNameOf(c); } catch { continue; }
						if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
						string lo = n.ToLowerInvariant();
						if (lo.Contains("mic") || lo.Contains("voice") || lo.Contains("speak") || lo.Contains("audio")
						 || lo.Contains("opus") || lo.Contains("codec") || lo.Contains("talk") || lo.Contains("lipsync"))
						{
							string owner = "?"; try { owner = c.gameObject.name; } catch { }
							sb.Append(n).Append('@').Append(owner).Append("  ");
						}
					}
				VRChatArchiveModPlugin.Logger.LogInfo("[VoiceProbe] LOCAL voice-side components: " + (sb.Length == 0 ? "(none matched)" : sb.ToString().Trim()));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VoiceProbe] local dump failed: " + e.Message); }
		}

		// ---------------------------------------------------------------- helpers

		private static int ByteLength(object o)
		{
			try
			{
				if (o is Il2CppObjectBase b)
				{
					var arr = b.TryCast<Il2CppStructArray<byte>>();
					if (arr != null) return arr.Length;
				}
				if (o is byte[] managed) return managed.Length;
			}
			catch { }
			return -1;
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			return null;
		}
	}
}
