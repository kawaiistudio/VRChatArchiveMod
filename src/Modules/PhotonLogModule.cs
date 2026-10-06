using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PHOTON LOG — every inbound Photon event this client receives, written to its own .log file.
	//
	// LISTEN ONLY, like NetworkLog: one Harmony POSTFIX on the receive funnel, two property reads and
	// a payload decode. Nothing is ever sent, raised, dropped or replayed. The transmit-side API names
	// do not appear in this file.
	//
	// OFF BY DEFAULT, AND OFF MEANS OFF. The hook is not installed until the switch is turned on, and
	// it is UNPATCHED when the switch goes off — leaving it off costs nothing at all, not even an
	// early-return on a detour. This matters: OnEvent runs hundreds of times a second.
	//
	// WHY A SECOND MODULE RATHER THAN A FLAG ON NetworkLog: NetworkLog is the live EVENTS console. It
	// deliberately samples a payload only the first three times a code is ever seen, because decoding
	// a hundreds-per-second stream on the receive path is exactly the cost a console must not add.
	// This module makes the opposite trade on purpose — decode EVERYTHING, pay for it, write it to
	// disk — so the console stays cheap and the forensic log stays complete. Turning this on is
	// expected to cost frames; that is the deal, and the header of every file says so.
	//
	// WHAT A LINE CARRIES: wall clock to the millisecond, Unity frame, seconds since launch, the code
	// and its best-known name, the sending actor, and the payload — byte arrays as length + hex + the
	// printable ASCII runs inside them (which is where a usr_/wrld_/avtr_ id shows up), dictionaries
	// decoded key by key through the guarded RAW reader (Core/Event33Payload), anything else by il2cpp
	// class name and count.
	//
	// THREADING: OnEvent runs on a NETWORK thread, not Unity's. So the postfix only formats a string
	// and puts it in a queue; the file write happens on the main thread in OnUpdate. No file I/O, no
	// Unity call, and no il2cpp wrapper construction ever happens on the network thread.
	public class PhotonLogModule : IModule
	{
		public override string Name => "PhotonLog";

		private static readonly string Tag = "[PhotonLog] ";
		private const int MaxQueue = 40000;      // ~a few seconds of a very loud instance
		private const int MaxHexBytes = 160;     // per event, then "+N more"
		private const int MaxLine = 2400;

		private static readonly List<string> Queue = new List<string>(4096);
		private static readonly object Gate = new object();
		private static volatile bool _on;
		private static long _seq, _dropped, _written;
		private static readonly Dictionary<byte, long> Counts = new Dictionary<byte, long>();

		private StreamWriter _sink;
		private string _path = "";
		private bool _wasOn;
		private float _flushAt, _statsAt;

		// ---------------------------------------------------------------- code names
		//
		// From the community reference (github.com/Harmonyasha/VRChat-Event-Codes), which states plainly
		// that its entries are a best guess from traffic analysis. This build ships no event-code enum
		// (checked: no EventCode type in any of the 247 interop assemblies), so a name here is a LABEL,
		// never an authority — the number is the identity, and the number is always printed first.
		private static readonly Dictionary<byte, string> Names = new Dictionary<byte, string>
		{
			{ 1,  "voice" },
			{ 2,  "show message" },
			{ 3,  "sync request" },
			{ 4,  "master sync" },
			{ 5,  "data sync request" },
			{ 6,  "RPC trigger" },
			{ 7,  "unreliable serialization" },
			{ 8,  "serialization interest table" },
			{ 10, "udon sync (scene)" },
			{ 11, "udon sync (player/RPC)" },
			{ 12, "player serialization / IK" },
			{ 13, "avatar parameter sync" },
			{ 14, "props" },
			{ 16, "object sync / pickups" },
			{ 17, "object sync / pickups" },
			{ 18, "udon sync (player/RPC)" },
			{ 20, "join world / loaded" },
			{ 22, "take ownership" },
			{ 24, "instance snapshot" },
			{ 25, "instance snapshot" },
			{ 33, "moderation / instance control" },
			{ 34, "rate limit table" },
			{ 35, "ping-ish" },
			{ 40, "avatar refresh" },
			{ 42, "VR mode / nameplate group" },
			{ 43, "chatbox message" },
			{ 44, "chatbox typing" },
			{ 51, "join world / loaded" },
			{ 53, "store products" },
			{ 60, "physbones permissions" },
			{ 66, "EAC heartbeat" },
			{ 67, "AppId" },
			{ 71, "emoji (legacy)" },
			{ 73, "private avatar token" },
			{ 74, "all world objects" },
			{ 76, "emoji" },
			{ 202, "instantiate" },
			{ 226, "photon internal (join)" },
			{ 253, "photon internal (raise event)" },
			{ 254, "photon internal (leave)" },
			{ 255, "photon internal" },
		};

		private static string NameOf(byte code)
		{
			string n;
			return Names.TryGetValue(code, out n) ? n : "unknown";
		}

		// ---------------------------------------------------------------- lifecycle

		public override void OnUpdate()
		{
			bool on = false;
			try { on = ModConfig.PhotonLogEnabled != null && ModConfig.PhotonLogEnabled.Value; } catch { }

			if (on && !_wasOn) { _wasOn = true; Start(); }
			else if (!on && _wasOn) { _wasOn = false; Stop(); }
			if (!_wasOn) return;

			float now = VaClock.Now;
			if (now - _flushAt >= 0.5f) { _flushAt = now; Drain(); }
			if (now - _statsAt >= 30f) { _statsAt = now; WriteStats(); }
		}

		public override void OnShutdown() { if (_wasOn) { _wasOn = false; Stop(); } }

		private void Start()
		{
			try
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "photon");
				Directory.CreateDirectory(dir);
				_path = Path.Combine(dir, "photon_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
				_sink = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
				WriteHeader();
				lock (Gate) { Queue.Clear(); Counts.Clear(); _seq = 0; _dropped = 0; _written = 0; }
				InstallHook();
				_on = true;
				_flushAt = _statsAt = VaClock.Now;
				Log("ON — every inbound Photon event is being written to " + _path
					+ " . This decodes EVERY packet on a hundreds-per-second stream: expect it to cost frames while it runs.");
				FeatureHealth.Ok(HealthId, "recording to " + Path.GetFileName(_path));
			}
			catch (Exception e)
			{
				Log("could not start: " + Unwrap.Describe(e));
				FeatureHealth.Broken(HealthId, "could not open the log file: " + Unwrap.Root(e).Message);
				_wasOn = false;
			}
		}

		private void Stop()
		{
			_on = false;
			RemoveHook();
			try { Drain(); WriteStats(); } catch { }
			lock (Gate)
			{
				try { if (_sink != null) { _sink.WriteLine("-- stopped " + DateTime.Now.ToString("HH:mm:ss.fff") + " --"); _sink.Flush(); _sink.Dispose(); } }
				catch { }
				_sink = null;
				Queue.Clear();
			}
			Log("OFF — " + _written + " event(s) written" + (_dropped > 0 ? ", " + _dropped + " dropped (queue full)" : "") + ". File: " + _path);
			FeatureHealth.Idle(HealthId, "off");
		}

		private static readonly string HealthId = "PhotonLog/Enabled";

		private void WriteHeader()
		{
			var sb = new StringBuilder(2048);
			sb.AppendLine("VRCHAT ARCHIVE MOD — PHOTON EVENT LOG (inbound, receive-only)");
			sb.AppendLine("mod " + PluginInfo.Version + "   started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			sb.AppendLine();
			sb.AppendLine("Every event this client RECEIVES, in arrival order. Nothing is sent, dropped or altered by this log.");
			sb.AppendLine("Outbound traffic is NOT here — that is a different hook (see the NetSend counters).");
			sb.AppendLine();
			sb.AppendLine("columns:  clock | frame | rt | code name | actor | payload");
			sb.AppendLine("  clock   wall clock, millisecond precision");
			sb.AppendLine("  frame   Unity frame number, or -1 when the event arrived off the main thread (the usual case)");
			sb.AppendLine("  rt      seconds since launch");
			sb.AppendLine("  actor   PHOTON ACTOR NUMBER of the sender, not the playerId shown in the roster. 0 = the server.");
			sb.AppendLine("  payload CustomData, decoded: byte arrays as length + hex + the printable ASCII runs inside them;");
			sb.AppendLine("          dictionaries key by key; anything else by il2cpp class name and count. Then the parameter count.");
			sb.AppendLine();
			sb.AppendLine("code names are a BEST GUESS from community traffic analysis (github.com/Harmonyasha/VRChat-Event-Codes).");
			sb.AppendLine("This build ships no event-code enum, so the NUMBER is the identity and the name is only a label:");
			var keys = new List<byte>(Names.Keys); keys.Sort();
			var line = new StringBuilder("  ");
			foreach (byte k in keys)
			{
				string piece = k + "=" + Names[k] + "   ";
				if (line.Length + piece.Length > 118) { sb.AppendLine(line.ToString()); line.Length = 0; line.Append("  "); }
				line.Append(piece);
			}
			if (line.Length > 2) sb.AppendLine(line.ToString());
			sb.AppendLine();
			sb.AppendLine(new string('-', 118));
			lock (Gate) { try { _sink.Write(sb.ToString()); _sink.Flush(); } catch { } }
		}

		private void WriteStats()
		{
			try
			{
				List<KeyValuePair<byte, long>> rows;
				long total, dropped;
				lock (Gate)
				{
					if (_sink == null) return;
					rows = new List<KeyValuePair<byte, long>>(Counts);
					total = _written; dropped = _dropped;
				}
				rows.Sort((a, b) => b.Value.CompareTo(a.Value));
				var sb = new StringBuilder(512);
				sb.Append("-- totals at ").Append(DateTime.Now.ToString("HH:mm:ss")).Append(": ").Append(total).Append(" event(s)");
				if (dropped > 0) sb.Append(", ").Append(dropped).Append(" DROPPED (queue full — the stream outran the writer)");
				sb.Append(" | by code: ");
				for (int i = 0; i < rows.Count && i < 24; i++)
					sb.Append(rows[i].Key).Append('(').Append(NameOf(rows[i].Key)).Append(")=").Append(rows[i].Value).Append("  ");
				lock (Gate) { try { _sink.WriteLine(sb.ToString()); } catch { } }
			}
			catch { }
		}

		private void Drain()
		{
			try
			{
				List<string> take = null;
				lock (Gate)
				{
					if (_sink == null || Queue.Count == 0) return;
					take = new List<string>(Queue);
					Queue.Clear();
				}
				lock (Gate)
				{
					if (_sink == null) return;
					for (int i = 0; i < take.Count; i++) _sink.WriteLine(take[i]);
					_sink.Flush();
				}
			}
			catch (Exception e) { Log("write failed: " + Unwrap.Describe(e)); }
		}

		// ---------------------------------------------------------------- hook

		private static bool _hooked;
		private static MethodBase _target;
		private static MethodInfo _postfix;

		private static void InstallHook()
		{
			if (_hooked) return;
			try
			{
				Type client = FindType("VRCNetworkingClient");
				if (client == null) { Log("VRCNetworkingClient not found — nothing can be recorded."); return; }
				MethodBase target = null;
				foreach (var m in client.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (m.Name != "OnEvent") continue;
					var ps = m.GetParameters();
					if (ps.Length != 1 || !ps[0].ParameterType.Name.Contains("EventData")) continue;
					target = m; break;
				}
				if (target == null) { Log("OnEvent(EventData) not found on VRCNetworkingClient — nothing can be recorded."); return; }

				_postfix = typeof(PhotonLogModule).GetMethod(nameof(OnEventPostfix), BindingFlags.Static | BindingFlags.NonPublic);
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, postfix: new HarmonyMethod(_postfix));
				_target = target;
				_hooked = true;
			}
			catch (Exception e) { Log("hook install failed: " + Unwrap.Describe(e)); }
		}

		// Unpatched by the EXACT patch method, never by harmony id: the id is shared with every other
		// module, and unpatching by it would tear NetworkLog and PhotonGuard off the same method.
		private static void RemoveHook()
		{
			if (!_hooked) return;
			try { VRChatArchiveModPlugin.HarmonyInstance.Unpatch(_target, _postfix); }
			catch (Exception e) { Log("could not remove the hook (it stays until VRChat restarts): " + Unwrap.Root(e).Message); }
			_hooked = false; _target = null; _postfix = null;
		}

		// Runs on the NETWORK thread for every single inbound event. Formats, queues, returns.
		private static void OnEventPostfix(object __0)
		{
			try
			{
				if (!_on || __0 == null) return;

				byte code = 0; int sender = -1;
				try { code = (byte)GetMember(__0, "Code"); } catch { }
				try { sender = (int)GetMember(__0, "Sender"); } catch { }

				int frame = -1;
				try { frame = Time.frameCount; } catch { }     // throws off the main thread: -1 says so
				float rt = 0f;
				try { rt = VaClock.Now; } catch { }

				var sb = new StringBuilder(256);
				sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
				  .Append(" | f").Append(frame)
				  .Append(" | ").Append(rt.ToString("0.000"))
				  .Append(" | code ").Append(code.ToString().PadLeft(3)).Append(' ').Append(NameOf(code).PadRight(30))
				  .Append(" | actor ").Append(sender.ToString().PadLeft(3))
				  .Append(" | ").Append(Payload(__0));
				string line = sb.ToString();
				if (line.Length > MaxLine) line = line.Substring(0, MaxLine) + " …(truncated)";

				lock (Gate)
				{
					_seq++;
					long c; Counts.TryGetValue(code, out c); Counts[code] = c + 1;
					if (Queue.Count >= MaxQueue) { _dropped++; return; }
					Queue.Add(line);
					_written++;
				}
			}
			catch { /* a logger must never break the networking it observes */ }
		}

		// ---------------------------------------------------------------- payload

		private static PropertyInfo _pCustom, _pParams, _pParamCount;

		private static string Payload(object ev)
		{
			var sb = new StringBuilder(128);
			try
			{
				if (_pCustom == null) _pCustom = ev.GetType().GetProperty("CustomData");
				object data = _pCustom != null ? _pCustom.GetValue(ev) : null;
				if (data == null) sb.Append("CustomData=null");
				else
				{
					var ob = data as Il2CppObjectBase;
					int len = ByteLength(data);
					if (len >= 0)
					{
						sb.Append("bytes[").Append(len).Append("] ");
						HexAscii(data, len, sb);
					}
					else
					{
						string cls = ClassName(ob) ?? data.GetType().Name;
						sb.Append(cls);
						// Dictionaries go through the guarded RAW reader: it never builds an il2cpp wrapper
						// for a key or a value, which is the read that killed the process in 3.9.73.
						if (ob != null && cls.StartsWith("Dictionary", StringComparison.Ordinal))
							sb.Append(' ').Append(Event33Payload.Describe(ob.Pointer));
						else
						{
							try { var cp = data.GetType().GetProperty("Count"); if (cp != null && cp.GetValue(data) is int n) sb.Append(" count=").Append(n); }
							catch { }
						}
					}
				}
			}
			catch (Exception e) { sb.Append("CustomData unavailable (").Append(e.GetType().Name).Append(')'); }

			try
			{
				if (_pParams == null) _pParams = ev.GetType().GetProperty("Parameters");
				object pars = _pParams != null ? _pParams.GetValue(ev) : null;
				if (pars != null)
				{
					if (_pParamCount == null) _pParamCount = pars.GetType().GetProperty("Count");
					if (_pParamCount != null && _pParamCount.GetValue(pars) is int pn) sb.Append(" | params(").Append(pn).Append(')');
				}
			}
			catch { }
			return sb.ToString();
		}

		private static void HexAscii(object data, int len, StringBuilder sb)
		{
			try
			{
				var b = data as Il2CppObjectBase;
				var arr = b != null ? b.TryCast<Il2CppStructArray<byte>>() : null;
				if (arr == null) { sb.Append("(not readable as byte[])"); return; }
				int n = Math.Min(len, MaxHexBytes);
				var hex = new StringBuilder(n * 3);
				var run = new StringBuilder();
				var runs = new List<string>();
				for (int i = 0; i < n; i++)
				{
					byte v = arr[i];
					hex.Append(v.ToString("X2")).Append(' ');
					if (v >= 0x20 && v < 0x7F) run.Append((char)v);
					else { if (run.Length >= 4) runs.Add(run.ToString()); run.Length = 0; }
				}
				if (run.Length >= 4) runs.Add(run.ToString());
				sb.Append("hex=").Append(hex.ToString().TrimEnd());
				if (len > n) sb.Append(" …(+").Append(len - n).Append(" more)");
				if (runs.Count > 0) sb.Append(" | ascii: ").Append(string.Join(" · ", runs.ToArray()));
			}
			catch (Exception e) { sb.Append("(hex unavailable: ").Append(e.GetType().Name).Append(')'); }
		}

		private static int ByteLength(object o)
		{
			try
			{
				var b = o as Il2CppObjectBase;
				if (b != null) { var arr = b.TryCast<Il2CppStructArray<byte>>(); if (arr != null) return arr.Length; }
				var managed = o as byte[];
				if (managed != null) return managed.Length;
			}
			catch { }
			return -1;
		}

		private static string ClassName(Il2CppObjectBase o)
		{
			try
			{
				if (o == null) return null;
				IntPtr p = o.Pointer;
				if (p == IntPtr.Zero || !NativeGuard.IsReadable(p, 8)) return null;
				IntPtr k = IL2CPP.il2cpp_object_get_class(p);
				string n = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(k));
				return string.IsNullOrEmpty(n) ? null : n;
			}
			catch { return null; }
		}

		// ---------------------------------------------------------------- helpers

		private static MemberInfo _mCode, _mSender;

		private static object GetMember(object ev, string name)
		{
			bool isCode = name == "Code";
			MemberInfo mi = isCode ? _mCode : _mSender;
			if (mi == null)
			{
				Type t = ev.GetType();
				const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
				mi = (MemberInfo)t.GetProperty(name, F) ?? t.GetField(name, F);
				if (isCode) _mCode = mi; else _mSender = mi;
				if (mi == null) return null;
			}
			if (!NativeGuard.Alive(ev)) return null;
			var p = mi as PropertyInfo;
			if (p != null) return p.GetValue(ev);
			var f = mi as FieldInfo;
			if (f != null) return f.GetValue(ev);
			return null;
		}

		private static Type FindType(string name)
		{
			try { var t = Assembly.Load("Assembly-CSharp").GetType(name); if (t != null) return t; }
			catch { }
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { var t = asm.GetType(name, false); if (t != null) return t; } catch { }
			}
			return null;
		}

		private static void Log(string s)
		{
			try { VRChatArchiveModPlugin.Logger.LogInfo(Tag + s); } catch { }
		}
	}
}
