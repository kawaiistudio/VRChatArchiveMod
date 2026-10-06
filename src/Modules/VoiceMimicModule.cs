using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// VOICE MIMIC — your outgoing voice BECOMES the target's voice stream. Nothing else moves.
	//
	// THE MODEL (2026-09-02, third and final shape, the one the owner asked for): "my voice should
	// be the copied stream, that's all". VRChat voice travels as Photon event 7: each talker's
	// client raises event 7 with an encoded (Opus) packet, every other client decodes it on that
	// talker's USpeaker. So the copy is a RELAY at the packet level:
	//   * every event 7 that arrives FROM THE TARGET is re-raised by us as our own event 7,
	//   * our own microphone's event 7 is dropped while the relay is on (otherwise two streams),
	//   * the packet header is patched from their actor number to ours when, and only when, the
	//     first four bytes read exactly as their actor number (self-validating: if the format is
	//     different the bytes are left alone and the log says so).
	// No avatar, bone, IK or position is touched. Ever. The two earlier versions (teleport to
	// their head; write our head bone onto theirs) are gone.
	//
	// WHAT IT IS NOT. It does not decode audio, does not mix, does not touch USpeak (whose class is
	// obfuscated on this build). It moves bytes from one Photon event to another. The receiving
	// clients decode our relayed packet with the same codec they decode the original with.
	//
	// HOOKS. Receive: postfix on VRCNetworkingClient.OnEvent(EventData) — the funnel NetworkLog and
	// PhotonGuard already use. Send (3.9.247): VRChat's own FlatBufferNetworkSerializer send, the
	// road its voice really takes; the relay reuses the int and SendOptions VRChat passed for our own
	// last voice packet. OpRaiseEvent is still hooked (it keeps the client instance fresh) but the
	// relay no longer goes through it: VRChat's voice never does.
	public class VoiceMimicModule : IModule
	{
		public override string Name => "VoiceMimic";

		public static string TargetUid { get; private set; } = "";
		public static string TargetName { get; private set; } = "";
		public static bool Active => !string.IsNullOrEmpty(TargetUid);
		public static string Status = "";

		// Voice rides Photon event code 1 on this build (VoiceProbe, 2026-09-02: code 1 sender count
		// tracked the talkers exactly). Read from config so it can be retargeted without a rebuild.
		private static byte _code = 1;
		private static byte VoiceCode => _code;
		private static void RefreshCode() { try { int c = ModConfig.VoiceMimicCode != null ? ModConfig.VoiceMimicCode.Value : 1; _code = (byte)(c < 0 ? 0 : c > 255 ? 255 : c); } catch { _code = 1; } }
		private static bool MuteSelf { get { try { return ModConfig.VoiceMimicMuteSelf != null && ModConfig.VoiceMimicMuteSelf.Value; } catch { return false; } } }

		private static int _targetActor = -1;
		private static int _localActor = -1;
		private static float _nextResolve;

		// Counters for the status line and the log.
		private static int _relayed, _droppedOwn, _headerPatched, _headerLeft;
		private static bool _firstLogged;

		// Re-entrancy flag: our own relayed sends must pass the "drop own mic" prefix.
		[ThreadStatic] private static bool _sending;

		// ---------------------------------------------------------------- public API

		public static void Toggle(VaTagsModule.PlayerEntry e)
		{
			if (e == null) { Status = "no such player"; return; }
			if (Active && string.Equals(e.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) { Stop("stopped"); return; }
			Start(e);
		}

		public static void Start(VaTagsModule.PlayerEntry e)
		{
			if (e == null || e.IsLocal) { Status = "pick somebody else"; Toast.Show("voice mimic: pick somebody else"); return; }
			EnsureHooks();
			if (!_recvHooked || (!_serHooked && _peerHooked == 0) || _baseSend == null)
			{
				Status = "voice relay unavailable: " + HookInfo;
				Toast.Show("voice mimic: " + Status);
				VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] " + Status);
				return;
			}

			TargetUid = e.UserId ?? "";
			TargetName = e.Name ?? "";
			_targetActor = e.PlayerId;
			_localActor = LocalActor();
			RefreshCode();
			_relayed = _droppedOwn = _headerPatched = _headerLeft = _queueDropped = 0;
			lock (_outGate) _out.Clear();
			_firstLogged = false;
			_nextResolve = 0f;

			Status = Learned ? "relaying voice of " + TargetName : "unmute and say a word once - the mod learns VRChat's voice channel from your own voice";
			// MUTE YOURSELF. Confirmed working 2026-09-23 (3.9.254) with the owner muted in VRChat: your
			// own mic and the relay leave on the same voice channel, and together they garble.
			Toast.Show(Learned ? "Voice mimic: " + TargetName + " - MUTE YOURSELF in VRChat to make it work"
				: "Voice mimic: say a word once (unmuted) to arm it, then MUTE YOURSELF");
			VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] relay ON: target " + TargetName + " (actor " + _targetActor + ") -> us (actor " + _localActor + ") on Photon code " + _code
				+ ". Voice channel " + (Learned ? "learned" : "NOT learned yet - waiting for one packet of your own voice") + ". MuteSelf=" + MuteSelf + ".");
		}

		public static void Stop(string why)
		{
			if (!Active) return;
			VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] relay OFF (" + why + "): relayed " + _relayed + " packet(s) from " + TargetName
				+ ", dropped " + _droppedOwn + " of our own, header patched " + _headerPatched + " / left " + _headerLeft
				+ ", " + _queueDropped + " stale packet(s) skipped.");
			TargetUid = ""; TargetName = ""; _targetActor = -1;
			lock (_outGate) _out.Clear();
			Status = why;
			Toast.Show("voice mimic: " + why);
		}

		// ---------------------------------------------------------------- lifecycle

		public override void OnInitialize() { EnsureHooks(); }

		public override void OnSceneLoaded(int buildIndex) { if (Active) Stop("world changed"); }

		public override void OnUpdate()
		{
			try { LogHistogram(); } catch { }
			if (!Active) return;
			try { Flush(); } catch { }
			try
			{
				float now = VaClock.Now;
				if (now < _nextResolve) return;
				_nextResolve = now + 2f;

				// Actor numbers are per room and a rejoin renumbers everyone; re-read both on a slow
				// tick from the roster, and stop when the target is gone.
				var entry = FindEntry();
				if (entry == null) { Stop("they left"); return; }
				if (entry.PlayerId > 0) _targetActor = entry.PlayerId;
				RefreshCode();
				int la = LocalActor(); if (la > 0) _localActor = la;
				Status = Learned ? "relaying voice of " + TargetName + " (" + _relayed + " packets) - keep yourself muted"
					: "unmute and say a word once - the mod learns VRChat's voice channel from your own voice";
			}
			catch { }
		}

		private static VaTagsModule.PlayerEntry FindEntry()
		{
			try
			{
				foreach (var p in VaTagsModule.Roster)
					if (p != null && string.Equals(p.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) return p;
			}
			catch { }
			return null;
		}

		private static int LocalActor()
		{
			try { var lp = VRC.SDKBase.Networking.LocalPlayer; return lp != null ? lp.playerId : -1; }
			catch { return -1; }
		}

		// ---------------------------------------------------------------- hooks

		private static bool _recvHooked, _sendHooked, _tried;
		public static string HookInfo = "not attached";
		private static MethodInfo _raise;          // OpRaiseEvent(byte, object, RaiseEventOptions, SendOptions)
		private static object _raiseTarget;        // the LoadBalancingClient instance, captured from the send prefix
		private static Type _optType, _sendOptType;
		private static PropertyInfo _pCode, _pSender, _pCustom;

		private static void EnsureHooks()
		{
			if (_tried) return;
			_tried = true;
			var notes = new StringBuilder();
			// RECEIVE.
			try
			{
				Type client = FindType("VRCNetworkingClient");
				MethodInfo target = null;
				if (client != null)
					foreach (var m in client.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						if (m.Name != "OnEvent") continue;
						var ps = m.GetParameters();
						if (ps.Length == 1 && ps[0].ParameterType.Name.Contains("EventData")) { target = m; break; }
					}
				if (target != null)
				{
					VRChatArchiveModPlugin.HarmonyInstance.Patch(target, postfix: new HarmonyMethod(typeof(VoiceMimicModule).GetMethod(nameof(OnEventPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
					_recvHooked = true; notes.Append("recv=VRCNetworkingClient.OnEvent ");
				}
				else notes.Append("recv=OnEvent(EventData) not found ");
			}
			catch (Exception e) { notes.Append("recv failed: ").Append(e.Message).Append(' '); }

			// SEND: the same signature hunt NetSendModule does. The FIRST match is also what we call.
			try
			{
				foreach (string typeName in new[] { "Photon.Realtime.LoadBalancingClient", "LoadBalancingClient", "VRCNetworkingClient" })
				{
					Type t = FindType(typeName);
					if (t == null) continue;
					foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						var ps = m.GetParameters();
						if (ps.Length != 4 || ps[0].ParameterType != typeof(byte)) continue;
						if (ps[2].ParameterType.Name.IndexOf("RaiseEventOptions", StringComparison.Ordinal) < 0) continue;
						if (ps[3].ParameterType.Name.IndexOf("SendOptions", StringComparison.Ordinal) < 0) continue;
						VRChatArchiveModPlugin.HarmonyInstance.Patch(m, prefix: new HarmonyMethod(typeof(VoiceMimicModule).GetMethod(nameof(RaisePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
						if (_raise == null) { _raise = m; _optType = ps[2].ParameterType; _sendOptType = ps[3].ParameterType; }
						_sendHooked = true; notes.Append("send=").Append(t.Name).Append('.').Append(m.Name).Append(' ');
					}
				}
				if (!_sendHooked) notes.Append("send=OpRaiseEvent signature not found ");
			}
			catch (Exception e) { notes.Append("send failed: ").Append(e.Message).Append(' '); }

			// THE FUNNEL: PhotonPeer.SendOperation(byte op, ParameterDictionary, SendOptions).
			//
			// NetSend counts every event through LoadBalancingClient.OpRaiseEvent and has NEVER seen
			// code 1 in a whole session of talking, so VRChat's voice does not take that road, and
			// re-raising it there reached nobody (83 packets, 2026-09-23). 3.9.248 then tried
			// VRC.Networking.FlatBufferNetworkSerializer - that turned out to be the per-object sync
			// component, and it never saw voice either. Every operation sent to the Photon server
			// goes through the peer's SendOperation, whatever built it: a raised event is op 253
			// with its code under key 244 and its payload under key 245. Our own voice is watched
			// here, VRChat's exact parameters and SendOptions for it are copied once, and the relay
			// sends the target's payload with those - nothing made up. VRChat's own peer subclass is
			// hooked as well as Photon's base class; whichever carries the voice is the one used.
			//
			// 3.9.249 LESSON: ProxyGuard refuses VRChat's peer subclass ("membre non identifiable de
			// facon sure"), and that refusal, thrown inside one shared try, also skipped Photon's base
			// class behind it. Each candidate is now tried on its own, and the WATCHING moved to the
			// one funnel that is Photon's own code with its own name, untouched by the obfuscator:
			// PeerBase.SerializeOperationToMessage(op, parameters, ...) - every operation of every
			// kind is serialised there before it leaves. The SENDING uses Photon's base
			// PhotonPeer.SendOperation, invoked on the live peer.
			Type basePeer = FindType("Photon.Client.PhotonPeer");
			foreach (var t in new[] { FindType("PhotonPeerPublicObInCoIn2ByInUIObCoUnique"), basePeer })
			{
				if (t == null) continue;
				foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (m.Name != "SendOperation" || m.ReturnType != typeof(bool)) continue;
					var ps = m.GetParameters();
					if (ps.Length != 3 || ps[0].ParameterType != typeof(byte)) continue;
					if (ps[1].ParameterType != typeof(Photon.Client.ParameterDictionary)) continue;
					if (ps[2].ParameterType.Name.IndexOf("SendOptions", StringComparison.Ordinal) < 0) continue;
					if (t == basePeer) { _baseSend = m; _sendOptsType = ps[2].ParameterType; }
					try
					{
						VRChatArchiveModPlugin.HarmonyInstance.Patch(m, prefix: new HarmonyMethod(typeof(VoiceMimicModule).GetMethod(nameof(PeerPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
						_peerHooked++;
						notes.Append(" peer=").Append(t.Name).Append(".SendOperation");
					}
					catch (Exception e) { notes.Append(" peer ").Append(t.Name).Append(" refused (").Append(Short(e.Message)).Append(')'); }
				}
			}
			try
			{
				Type pb = FindType("Photon.Client.PeerBase");
				MethodInfo ser = null;
				if (pb != null)
					foreach (var m in pb.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						if (m.Name != "SerializeOperationToMessage") continue;
						var ps = m.GetParameters();
						if (ps.Length >= 2 && ps[0].ParameterType == typeof(byte) && ps[1].ParameterType == typeof(Photon.Client.ParameterDictionary)) { ser = m; break; }
					}
				// THE HOOK IS ONLY AS SAFE AS WHAT THE PREFIX THEN CALLS.
				//
				// SerializePrefix reads the ParameterDictionary it is handed -- ContainsKey and the
				// indexer -- and those are il2cpp proxy members like any other. On a build where they
				// are bound to missing-member stubs, the prefix dies inside il2cpp on the FIRST
				// operation VRChat sends, which is an access violation no catch can intercept and which
				// no amount of checking the hook TARGET would have predicted. The hook target itself
				// passes every identity test here; it is the body that cannot run. So the watch is
				// installed only when the dictionary API it depends on is really bound.
				// ContainsKey is not the only member the prefix touches: ReadCode also goes through the
				// INDEXER, and a bound ContainsKey says nothing about get_Item. Both are required.
				// DISABLED ON UNITY 6 (2026-10-04), AND NOT BY A CHECK -- BY MEASUREMENT.
				//
				// Every identity test this mod has passes for this hook: PeerBase is anchored 58/60,
				// the target matches the proxy signature, and both ParameterDictionary members the
				// prefix uses (ContainsKey and the indexer) are bound. It still kills the process on
				// the first operation VRChat sends, inside the il2cpp->managed bridge, ~30 s into every
				// session -- reproduced on four consecutive builds. What is left is the generic Photon
				// type the prefix casts to (StructWrapper<byte>), and generic instantiations are the
				// one area still broken here: Il2CppInterop builds them through MethodInfoStoreGeneric
				// / MakeGenericMethod, which crashed during Load() earlier in this same port.
				// This watch only LEARNS the voice event code; recv and send hook fine without it, so
				// the cost of switching it off is a diagnostic, and the cost of leaving it on is the
				// session. Re-enable once generic members resolve (regenerated interop).
				bool dictOk = false;
				if (ser != null && !dictOk)
				{
					notes.Append(" watch off (Unity 6 : generiques Photon non resolus)");
					VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] watch Photon desactive sur ce build : le prefix meurt dans le pont il2cpp (generiques Photon non resolus). recv/send restent actifs.");
					ser = null;
				}
				if (ser != null)
				{
					VRChatArchiveModPlugin.HarmonyInstance.Patch(ser, prefix: new HarmonyMethod(typeof(VoiceMimicModule).GetMethod(nameof(SerializePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
					_serHooked = true;
					notes.Append(" watch=PeerBase.SerializeOperationToMessage");
				}
				else notes.Append(" watch=SerializeOperationToMessage not found");
			}
			catch (Exception e) { notes.Append(" watch refused (").Append(Short(e.Message)).Append(')'); }
			if (_baseSend == null) notes.Append(" base PhotonPeer.SendOperation not found");

			HookInfo = notes.ToString().Trim();
			VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] hooks: " + HookInfo);
		}

		private const byte OpRaiseEvent = 253, KeyCode = 244, KeyData = 245;
		private static int _peerHooked;
		private static object _peerTarget;         // the peer our own voice went through, when the SendOperation hook sees it
		private static object _peerOpts;           // VRChat's SendOptions for its voice (boxed struct)

		// Counters for the periodic "what does VRChat send" line: ops, and event codes inside op 253.
		private static readonly int[] _opHits = new int[256], _evHits = new int[256];
		private static float _nextHist;
		private static int _histLines;

		private static bool Learned => _voiceSendCode >= 0;

		private static MethodInfo _baseSend;       // Photon.Client.PhotonPeer.SendOperation - what the relay calls
		private static Type _sendOptsType;
		private static bool _serHooked;

		// WHAT 3.9.252 SHOWED (2026-09-23), and why voice is now recognised by its CONTENT:
		//  * every op 253 carries exactly two keys: 244 = the event code, held in a POOLED
		//    Photon StructWrapper<byte> (not a boxed byte - so the old read failed on every packet,
		//    and the wrapper is recycled after the send, so it must never be kept), 245 = the data.
		//  * the voice we RECEIVE as event 1 starts with 4 bytes that climb by the milliseconds
		//    between packets: a SERVER timestamp. The server stamps it and hands voice out as event
		//    1 - so what a client sends is most likely the bare frames, under whatever code VRChat
		//    uses on the way up. Re-raising event 1 ourselves (3.9.247) sent the server something
		//    it does not relay.
		// VRChat's voice frames have a shape: [seq u16][length u16][Opus TOC 0x78..] repeated. Our
		// own outgoing events are checked for it; the code that carries it is the voice code, and
		// the relay sends the target's frames, timestamp stripped, under that code.
		private static int _voiceSendCode = -1;
		private static int _ownShape0, _ownShape4;
		private static bool _ownHasStamp = true;
		private static readonly int[] _voiceLike = new int[256];
		private static int _ownLogged;

		// SendOperation hook, where ProxyGuard allows it: the peer instance, VRChat's real
		// SendOptions for voice, and the only place our own voice can be dropped (MuteSelf).
		private static bool PeerPrefix(object __instance, byte __0, object[] __args)
		{
			if (_sending) return true;
			try
			{
				var dict = __args != null && __args.Length >= 3 ? __args[1] as Photon.Client.ParameterDictionary : null;
				int code = Classify(__0, dict, !_serHooked, out bool voice);
				if (code < 0) return true;
				if (__instance != null) _peerTarget = __instance;
				if (voice && code == _voiceSendCode)
				{
					if (__args[2] != null) _peerOpts = __args[2];
					if (Active && MuteSelf) { _droppedOwn++; return false; }
				}
			}
			catch { }
			return true;
		}

		// THE WATCH. Photon serialises every operation here. Never skips the original (a null
		// StreamBuffer back to Photon would end the process): it only looks.
		private static void SerializePrefix(byte __0, Photon.Client.ParameterDictionary __1)
		{
			if (_sending) return;
			try { Classify(__0, __1, true, out _); }
			catch { }
		}

		// Event code of an op 253, or -1. Counts, and learns the voice code from the frames' shape.
		private static int Classify(byte op, Photon.Client.ParameterDictionary dict, bool count, out bool voice)
		{
			voice = false;
			if (count) System.Threading.Interlocked.Increment(ref _opHits[op]);
			if (op != OpRaiseEvent || dict == null) return -1;
			int code = ReadCode(dict);
			if (code < 0) return -1;
			if (count) System.Threading.Interlocked.Increment(ref _evHits[code]);
			// 3.9.253 LOG: event 1 left 385-414 times per 30 s while the owner talked, and NONE of it
			// matched the frame shape at offset 0. So our own voice is event 1 and it already carries
			// the 4-byte timestamp on the way UP - the shape sits at offset 4, exactly like the voice
			// we receive. Both offsets are checked; the one our own voice uses decides whether the
			// relay strips anything (it should not).
			if (code != VoiceCode) return code;
			voice = true;
			if (!count) return code;
			Il2CppStructArray<byte> data = null;
			try { if (dict.ContainsKey(KeyData)) data = dict[KeyData]?.TryCast<Il2CppStructArray<byte>>(); } catch { }
			if (data == null) return code;
			bool s0 = LooksLikeVoice(data, 0), s4 = LooksLikeVoice(data, 4);
			if (s0) System.Threading.Interlocked.Increment(ref _ownShape0);
			if (s4) System.Threading.Interlocked.Increment(ref _ownShape4);
			int n = System.Threading.Interlocked.Increment(ref _voiceLike[code]);
			if (_ownLogged < 3)
			{
				_ownLogged++;
				VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] own outgoing event " + code + ": " + data.Length + " bytes, head " + Head(data)
					+ " (frames at offset 0: " + s0 + ", at offset 4: " + s4 + ")");
			}
			if (_voiceSendCode < 0 && n >= 10)
			{
				_ownHasStamp = _ownShape4 >= _ownShape0;
				_voiceSendCode = code;
				VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] learned: your own voice leaves as event " + code + ", "
					+ (_ownHasStamp ? "WITH the 4-byte timestamp (relayed as received)" : "WITHOUT the timestamp (relay strips it)")
					+ " - frame shape at offset 4: " + _ownShape4 + "/10, at offset 0: " + _ownShape0 + "/10.");
			}
			return code;
		}

		private static int ReadCode(Photon.Client.ParameterDictionary dict)
		{
			try
			{
				if (!dict.ContainsKey(KeyCode)) return -1;
				var v = dict[KeyCode];
				if (v == null) return -1;
				var w = v.TryCast<Photon.Client.StructWrapping.StructWrapper<byte>>();
				if (w != null) return w.Unwrap();
				return v.Unbox<byte>();
			}
			catch { return -1; }
		}

		// [seq u16][length u16][Opus TOC] with the length fitting inside the packet. TOC config 15
		// (0x78-0x7F) is what every captured frame carries.
		private static bool LooksLikeVoice(Il2CppStructArray<byte> d, int off)
		{
			int n = d.Length;
			if (n < off + 5) return false;
			int len = d[off + 2] | (d[off + 3] << 8);
			return len > 0 && off + 4 + len <= n && (d[off + 4] >> 3) == 15;
		}

		private static bool LooksLikeVoice(byte[] d, int off)
		{
			int n = d.Length;
			if (n < off + 5) return false;
			int len = d[off + 2] | (d[off + 3] << 8);
			return len > 0 && off + 4 + len <= n && (d[off + 4] >> 3) == 15;
		}

		private static string Head(Il2CppStructArray<byte> d)
		{
			var hex = new StringBuilder();
			for (int k = 0; k < Math.Min(12, d.Length); k++) hex.Append(d[k].ToString("x2")).Append(' ');
			return hex.ToString().Trim();
		}


		// The live PhotonPeer, when the SendOperation hook could not hand it over: the client we
		// already hold (captured from OpRaiseEvent) carries it as a member typed PhotonPeer or a
		// subclass. Looked up by TYPE, never by name.
		private static object FindPeer()
		{
			if (_peerTarget != null) return _peerTarget;
			var client = _raiseTarget;
			if (client == null) return null;
			try
			{
				for (var t = client.GetType(); t != null && t != typeof(object); t = t.BaseType)
					foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
					{
						if (!typeof(Photon.Client.PhotonPeer).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length != 0) continue;
						object v = null;
						try { v = p.GetValue(client); } catch { }
						if (v != null) return v;
					}
			}
			catch { }
			return null;
		}

		// One line every 30 s, 20 at most per session: which ops and event codes VRChat sent. If voice
		// ever stops being learned, this says where it went instead.
		private static void LogHistogram()
		{
			float now = VaClock.Now;
			if (now < _nextHist || _histLines >= 20) return;
			_nextHist = now + 30f;
			var sb = new StringBuilder();
			for (int i = 0; i < 256; i++) { int n = System.Threading.Interlocked.Exchange(ref _opHits[i], 0); if (n > 0) sb.Append("op").Append(i).Append('x').Append(n).Append(' '); }
			sb.Append("| events: ");
			for (int i = 0; i < 256; i++) { int n = System.Threading.Interlocked.Exchange(ref _evHits[i], 0); if (n > 0) sb.Append(i).Append('x').Append(n).Append(' '); }
			if (sb.Length < 14) return;
			_histLines++;
			VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] sent in 30 s: " + sb.ToString().Trim() + (Learned ? "" : " (voice not learned yet)"));
		}

		private static string DescribeOpts(object o)
		{
			if (o == null) return "null";
			try
			{
				var sb = new StringBuilder();
				foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) sb.Append(f.Name).Append('=').Append(f.GetValue(o)).Append(' ');
				foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) { try { sb.Append(p.Name).Append('=').Append(p.GetValue(o)).Append(' '); } catch { } }
				return sb.ToString().Trim();
			}
			catch { return o.ToString(); }
		}

		// SEND PREFIX. Two jobs: remember the client instance we must call OpRaiseEvent on, and drop
		// our own microphone's event 7 while the relay runs (our relayed sends set _sending).
		private static bool RaisePrefix(object __instance, byte __0)
		{
			try
			{
				// ALWAYS the latest instance, not the first one ever seen. The client captured at the
				// first send of the session can be a torn-down one after a reconnect; calling
				// OpRaiseEvent on it queues into a dead peer.
				if (__instance != null) _raiseTarget = __instance;
				if (Active && MuteSelf && __0 == VoiceCode && !_sending) { _droppedOwn++; return false; }
			}
			catch { }
			return true;
		}

		// RECEIVE POSTFIX. Runs for every inbound event; does nothing unless the relay is on, the
		// code is voice, and the sender is the target.
		private static void OnEventPostfix(object __0)
		{
			if (!Active || __0 == null) return;
			try
			{
				var t = __0.GetType();
				_pCode ??= t.GetProperty("Code");
				_pSender ??= t.GetProperty("Sender");
				_pCustom ??= t.GetProperty("CustomData");
				if (_pCode == null || _pSender == null || _pCustom == null) return;
				if ((byte)_pCode.GetValue(__0) != VoiceCode) return;
				if ((int)_pSender.GetValue(__0) != _targetActor) return;

				object data = _pCustom.GetValue(__0);
				var arr = (data as Il2CppObjectBase)?.TryCast<Il2CppStructArray<byte>>();
				if (arr == null) { if (!_firstLogged) { _firstLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] voice payload is not a byte[] (" + (data?.GetType().Name ?? "null") + ") - cannot relay on this build."); } return; }

				int n = arr.Length;
				if (n <= 0) return;
				var bytes = new byte[n];
				for (int i = 0; i < n; i++) bytes[i] = arr[i];

				// Header: patch their actor number to ours ONLY if the first int reads as theirs.
				bool patched = false;
				if (n >= 4 && BitConverter.ToInt32(bytes, 0) == _targetActor && _localActor > 0)
				{
					var mine = BitConverter.GetBytes(_localActor);
					Buffer.BlockCopy(mine, 0, bytes, 0, 4);
					patched = true; _headerPatched++;
				}
				else _headerLeft++;

				if (!_firstLogged)
				{
					_firstLogged = true;
					var hex = new StringBuilder();
					for (int i = 0; i < Math.Min(12, n); i++) hex.Append(arr[i].ToString("x2")).Append(' ');
					VRChatArchiveModPlugin.Logger.LogInfo("[VoiceMimic] first voice packet from actor " + _targetActor + ": " + n + " bytes, head " + hex.ToString().Trim()
						+ (patched ? " (actor header patched to " + _localActor + ")" : " (no actor header recognised; sent as-is)"));
				}

				// QUEUED, NOT SENT FROM HERE. This runs inside VRChat's OnEvent dispatch; calling
				// OpRaiseEvent from inside it re-enters Photon while it is handing out incoming events,
				// and if the dispatch is on a network thread it races the main thread's own sends on the
				// same outgoing queue. That race is what took the game down on 2026-09-23 (crash on a
				// Unity worker thread, reading 0x3f800000 as a pointer) in a 34-player instance, while a
				// 2-player one had relayed 103 packets fine. OnUpdate sends them on the main thread.
				lock (_outGate)
				{
					if (_out.Count >= MaxQueued) { _out.Dequeue(); _queueDropped++; }
					_out.Enqueue(bytes);
				}
			}
			catch (Exception e)
			{
				if (!_firstLogged) { _firstLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] relay read failed: " + e.Message); }
			}
		}

		// A few packets at most: voice is 20-60 ms per packet and stale audio is worse than none.
		private const int MaxQueued = 12;
		private static readonly System.Collections.Generic.Queue<byte[]> _out = new System.Collections.Generic.Queue<byte[]>();
		private static readonly object _outGate = new object();
		private static int _queueDropped;

		private static void Flush()
		{
			while (true)
			{
				byte[] b;
				lock (_outGate)
				{
					if (_out.Count == 0) return;
					b = _out.Dequeue();
				}
				if (!Active) { lock (_outGate) _out.Clear(); return; }
				Send(b);
			}
		}

		private static Il2CppStructArray<byte> _lastPayload;   // kept alive until the next send
		private static bool _noPeerLogged, _shapeLogged;

		private static void Send(byte[] bytes)
		{
			// Only through VRChat's own voice path, with its own arguments. Not learned yet = the
			// packet is skipped (stale audio is worthless anyway), never sent down a guessed road.
			if (_voiceSendCode < 0 || _baseSend == null) { _queueDropped++; return; }
			// Strip the server's 4-byte timestamp: what a client sends is the bare frames. Only when
			// the frames are really there after it; anything else is not sent at all.
			if (LooksLikeVoice(bytes, 4))
			{
				if (!_ownHasStamp) { var bare = new byte[bytes.Length - 4]; Buffer.BlockCopy(bytes, 4, bare, 0, bare.Length); bytes = bare; }
			}
			else if (!LooksLikeVoice(bytes, 0))
			{
				_queueDropped++;
				if (!_shapeLogged) { _shapeLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] a received voice packet does not have the voice-frame shape - not relayed."); }
				return;
			}
			object peer = FindPeer();
			if (peer == null)
			{
				_queueDropped++;
				if (!_noPeerLogged) { _noPeerLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] voice learned, but the live PhotonPeer could not be found - nothing sent."); }
				return;
			}
			try
			{
				_peerOpts ??= Activator.CreateInstance(_sendOptsType);   // unreliable, like voice
				var payload = new Il2CppStructArray<byte>(bytes);
				_lastPayload = payload;
				// Exactly the two keys VRChat's own voice carries: the code it goes up under, and the
				// frames. The code is added as a plain byte; Photon wraps it itself.
				var dict = new Photon.Client.ParameterDictionary();
				dict.Add(KeyCode, (byte)_voiceSendCode);
				dict.Add(KeyData, new Il2CppSystem.Object(payload.Pointer));
				_sending = true;
				try { _baseSend.Invoke(peer, new object[] { OpRaiseEvent, dict, _peerOpts }); }
				finally { _sending = false; }
				_relayed++;
			}
			catch (Exception e)
			{
				if (_relayed == 0) { VRChatArchiveModPlugin.Logger.LogWarning("[VoiceMimic] relay send failed: " + e.Message); Stop("send failed - " + Short(e.Message)); }
			}
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

		private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 80 ? s.Substring(0, 79) + "…" : s);
	}
}
