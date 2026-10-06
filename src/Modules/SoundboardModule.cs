using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// SOUNDBOARD — a member presses a button, every other mod hears it.
	//
	// What actually crosses the network is a KEY ("rezero_scary"), never audio and never a URL.
	// The clips are embedded in this DLL, so a trigger can only ever play something the listener
	// already has. Broadcasting a URL instead would let anyone make every mod user's client fetch
	// an arbitrary file at once — and hand out their IPs in the process.
	//
	// No account is needed to trigger or to hear. Signing in only changes the ATTRIBUTION: a
	// member shows up by name, everyone else as "guest".
	//
	// It plays audio in other people's headsets, so the listener keeps the last word:
	//   * a master toggle, off-able at any time
	//   * a volume that is theirs, not the sender's
	//   * only sounds newer than the last one seen are played, so a client that was paused or
	//     alt-tabbed never dumps a backlog of horns at once
	public class SoundboardModule : IModule
	{
		public override string Name => "Soundboard";

		public sealed class Clip
		{
			public string Key;         // what goes over the wire
			public string Label;       // what the button says
			public string Resource;    // embedded WAV
			// Optional embedded PNG for the button. A clip without one falls back to the shared
			// heart, which is what every clip used to get — six identical hearts told you nothing,
			// so a clip that HAS a face gets to show it.
			public string Image;
			public AudioClip Audio;
			public bool Tried;
		}

		public static readonly Clip[] Clips =
		{
			new Clip { Key = "rezero_scary",   Label = "Re:Zero — Scary",   Resource = "sb_scary.wav" },
			new Clip { Key = "rezero_respawn", Label = "Re:Zero — Respawn", Resource = "sb_respawn.wav" },
			new Clip { Key = "gay_echo",       Label = "Gay Echo",           Resource = "sb_gay.wav" },
			new Clip { Key = "mambo",          Label = "MAMBO",              Resource = "sb_mambo.wav", Image = "sb_mambo.png" },
		};

		public static string LastStatus = "";
		public static string LastHeard = "";
		public static int Played;

		private static AudioSource _src;
		private static int _since = -1;          // -1 = not synced yet
		private static float _nextPoll;
		private static bool _posting;

		private static readonly HttpClient Http = Make();
		private static HttpClient Make()
		{
			var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
			c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 VRChatArchiveMod/3.4");
			return c;
		}

		private static string Base => ModConfig.VaTagsApiBase.Value.TrimEnd('/');

		// ---------------------------------------------------------------- send

		// No account needed to fire a clip. The session is still sent when there IS one, so a
		// member shows up by name instead of as "guest" — but it is not required.
		public static void Send(Clip clip)
		{
			if (clip == null || _posting) return;
			_posting = true;
			LastStatus = "sending " + clip.Label + "…";
			_ = SendAsync(clip);
		}

		private static async System.Threading.Tasks.Task SendAsync(Clip clip)
		{
			try
			{
				// Send WHO, not just WHAT. Without a name the server has nothing to show but
				// "guest", so a busy soundboard was an anonymous pile of noise. This is the VRChat
				// display name — a label, never a credential: the endpoint needs no login and the
				// server rate-limits by IP, so nothing here decides what anyone is allowed to do.
				string who = "";
				try
				{
					var api = PlayerRef.LocalApi();
					if (api != null) who = api.displayName ?? "";
				}
				catch { }
				if (who.Length > 40) who = who.Substring(0, 40);

				string body = "{\"sound\":\"" + clip.Key + "\""
					+ (who.Length > 0 ? ",\"name\":\"" + Esc(who) + "\"" : "")
					+ "}";
				// Straight to the server. It used to go through the desktop client's LocalBridge
				// first — a route that was never built there, so every trigger 404'd, fell through to
				// a cookie the mod does not hold in bridge mode, and died as "login required"
				// WITHOUT a single request ever reaching the server. That is why nothing played.
				var (ok, raw, status) = await VaAuth.PostSoundAsync(body);
				if (ok) { LastStatus = "played " + clip.Label + " for everyone"; return; }

				string err = null;
				try
				{
					using var d = JsonDocument.Parse(raw);
					if (d.RootElement.TryGetProperty("error", out var e)) err = e.GetString();
				}
				catch { }
				LastStatus = err ?? ("soundboard refused (" + status + ")");
			}
			catch (Exception e) { LastStatus = "soundboard failed: " + e.Message; }
			finally { _posting = false; }
		}

		// ---------------------------------------------------------------- receive

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.SoundboardEnabled.Value) return;
				float now = VaClock.Now;
				if (now < _nextPoll) return;
				_nextPoll = now + Mathf.Clamp(ModConfig.SoundboardPollSeconds.Value, 1f, 15f);
				_ = PollAsync();
			}
			catch { }
		}

		private static async System.Threading.Tasks.Task PollAsync()
		{
			try
			{
				string url = Base + "/api/mod-sound/feed" + (_since >= 0 ? "?since=" + _since : "");
				using var resp = await Http.GetAsync(url);
				if (!resp.IsSuccessStatusCode) return;
				using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
				var root = doc.RootElement;

				int head = root.TryGetProperty("now", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;

				// FIRST poll only syncs the cursor. Without this, a mod that starts up mid-session
				// would immediately play everything still inside the server's window.
				if (_since < 0) { _since = head; return; }

				if (!root.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array) return;
				foreach (var ev in evs.EnumerateArray())
				{
					int id = ev.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
					if (id > _since) _since = id;

					string key = ev.TryGetProperty("sound", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
					string by = ev.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "?";
					if (string.IsNullOrEmpty(key)) continue;

					// An event older than the clip itself is stale — play nothing rather than fire a
					// horn seconds after the person who sent it has moved on.
					double age = ev.TryGetProperty("age", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0;
					if (age > 12) continue;

					_pending.Enqueue((key, by));
					Remember(by, key);
				}
			}
			catch { }
		}

		// WHO PLAYED WHAT, AND WHEN. A shared soundboard with no attribution is just noise coming
		// from nowhere — you cannot tell whether one person is spamming or six people are having
		// fun. Newest first, capped: this is a running log, not a record to keep.
		public sealed class Entry
		{
			public string When;     // local clock, formatted once — the row is drawn every frame
			public string Who;
			public string What;
		}

		private static readonly List<Entry> History = new List<Entry>();
		private static readonly object HistGate = new object();
		public const int HistoryMax = 40;

		public static List<Entry> Recent()
		{
			lock (HistGate) return new List<Entry>(History);
		}

		private static void Remember(string by, string key)
		{
			try
			{
				string label = key;
				foreach (var c in Clips) { if (c.Key == key) { label = c.Label; break; } }
				var e = new Entry
				{
					When = DateTime.Now.ToString("HH:mm:ss"),
					Who = string.IsNullOrWhiteSpace(by) ? "?" : by,
					What = label,
				};
				lock (HistGate)
				{
					History.Insert(0, e);
					if (History.Count > HistoryMax) History.RemoveRange(HistoryMax, History.Count - HistoryMax);
				}
				VRChatArchiveModPlugin.Logger.LogInfo($"[Soundboard] {e.When}  {e.Who} -> {e.What}");
			}
			catch { }
		}

		private static string Esc(string s)
			=> (s ?? "").Replace("\\", "").Replace("\"", "").Replace("\n", " ").Replace("\r", " ");

		// Playback has to happen on the main thread; the poll runs off it.
		private static readonly Queue<(string key, string by)> _pending = new Queue<(string, string)>();

		public override void OnLateUpdate()
		{
			try
			{
				// OFF MEANS SILENT, THIS FRAME (2026-09-13). The drain had no switch test at all, so a
				// poll already in flight when the master switch went off still played its clip — a
				// toggle you can hear ignore you — and nothing ever emptied this queue while off, so
				// everything that arrived meanwhile was replayed in a burst the moment it came back.
				bool sbOn = false;
				try { sbOn = ModConfig.SoundboardEnabled.Value; } catch { }
				if (!sbOn) { _pending.Clear(); return; }

				while (_pending.Count > 0)
				{
					var (key, by) = _pending.Dequeue();
					Clip clip = null;
					foreach (var c in Clips)
						if (string.Equals(c.Key, key, StringComparison.Ordinal)) { clip = c; break; }
					if (clip == null) continue;      // a key this build does not ship
					Play(clip, by);
				}
			}
			catch { }
		}

		private static void Play(Clip clip, string by)
		{
			// Soundboard audio disabled per user request
			return;
		}

		private static AudioClip Resolve(Clip clip)
		{
			if (clip.Audio != null || clip.Tried) return clip.Audio;
			clip.Tried = true;
			try
			{
				using var res = typeof(SoundboardModule).Assembly.GetManifestResourceStream(clip.Resource);
				if (res == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Soundboard] missing resource " + clip.Resource);
					return null;
				}
				var wav = new byte[res.Length];
				int off = 0, n;
				while (off < wav.Length && (n = res.Read(wav, off, wav.Length - off)) > 0) off += n;
				clip.Audio = WavAudio.Decode(wav, "sb_" + clip.Key);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Soundboard] decode failed: {e.Message}"); }
			return clip.Audio;
		}

		// Preview WITHOUT broadcasting, so you can hear what a button does before firing it at
		// everybody.
		public static void Preview(Clip clip)
		{
			if (clip == null) return;
			Play(clip, "you (preview)");
			LastStatus = "preview only — nobody else heard that";
		}
	}
}
