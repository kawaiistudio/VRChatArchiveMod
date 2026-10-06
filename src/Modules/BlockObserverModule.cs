using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// BLOCK OBSERVER — a READ-ONLY diagnostic, and nothing else.
	//
	// When a remote player's avatar goes from drawn to not drawn (or back) on THIS client, we do not
	// know today which of these VRChat actually does: deactivate the player root, deactivate the
	// avatar object, disable its renderers, destroy the avatar, replace it, strip components, or keep
	// everything alive and flip some flag of its own. The old AntiBlockModule guessed; this measures.
	//
	// It writes to NOTHING. No SetActive, no renderer.enabled, no Photon, no moderation list, no field
	// write of any kind. Every value is read through a getter, and any value that cannot be reached
	// safely is reported as "unavailable" rather than fetched by force. If a reader wants proof, grep
	// this file for '=' followed by a Unity member: there is none.
	//
	// HOW IT WORKS
	//   * Baseline: every remote player is sampled ~4x/s (a slice of the roster per frame, so a full
	//     instance costs a few ms/s, not a spike): root active flags, avatar root instance id, how
	//     many renderers are drawable, position. The last 2.6 s are kept per player — that is where
	//     T-2 and T-1 come from when something happens.
	//   * Transition: a player seen DRAWN whose sample turns HIDDEN (root off, avatar off, or no
	//     drawable renderer left) opens a 5-second window; so does a re-show, or the avatar root
	//     changing instance. Not "a block" — the word is never claimed — just "a transition".
	//   * Window: full snapshots at T0, +0.1, +0.25, +0.5, +1, +2, +5 s (player/avatar objects, every
	//     renderer, animator, IK, components, VRChat's own flags read by name, nameplate, audio), each
	//     one diffed against the previous so only CHANGES are printed; and between them a per-frame
	//     watch of the cached renderer list, the root, and the avatar object, which is what catches
	//     "destroyed at T+0.13" or "re-disabled every frame".
	//   * Summary: before/after and a plain-language classification (A–F in the task list), with the
	//     Photon events NetworkLog saw in the window, the moderation hints the mod already has, and a
	//     warning when one of our own modules (TrueView, NsfwFilter, over-budget hiding) could have
	//     produced what was measured.
	//
	// THE SAFETY PATTERNS ARE THE MOD'S OWN (Core/NativeGuard, PlayerStateProbeModule): il2cpp
	// objects are validated before reflection; field_* members are read only when FieldOffsetFix has
	// verified the offset slot; il2cpp STRINGS are never read off VRChat objects here (the display
	// name comes from the SDK once, at first sight, and from the roster); no delegate bridge, no
	// Harmony patch of an obfuscated method; per-frame work is bounded to the ≤4 open windows.
	public class BlockObserverModule : IModule
	{
		public override string Name => "BlockDebug";

		private static readonly string Tag = "[BLOCK-DEBUG] ";
		private static readonly string HealthId = "BlockDebug/Enabled";

		private const float LightTick = 0.25f;        // baseline sampling period per remote player
		private const float HistorySeconds = 2.6f;    // pre-window kept per player (T-2 needs it)
		private const float RendererRefresh = 2f;     // re-collect a player's renderer list this often
		private const float AvatarRetry = 1f;         // look again for a missing avatar root this often
		private const float Cooldown = 10f;           // after a window closes, before the same player can open another
		private const int MaxSessions = 4;
		private const int MaxTracked = 200;
		private const int MaxLinesPerSession = 400;
		private const int MaxRenderersTracked = 512;
		private const float WindowEnd = 5f;
		private static readonly float[] Offsets = { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f };

		// ---------------------------------------------------------------- state

		private enum Vis { Unknown, NoAvatar, Visible, Hidden }

		// One baseline sample. Small on purpose: there are ~4 per second per remote player.
		private sealed class Light
		{
			public float T;
			public bool RootExists, RootActive, RootInHier;
			public int AvatarId;                 // 0 = no live avatar root
			public bool AvatarActive, AvatarInHier;
			public int Renderers, Drawable, EnabledFlag, ForceOff;
			public bool HasPos; public Vector3 Pos;
			public bool HasNetPos; public Vector3 NetPos;
			public Vis State;
		}

		private sealed class RendInfo
		{
			public Renderer R;
			public GameObject Go;
			public int Id;
			public string Kind;
			public string Name;
			public bool En, Fo;        // last known enabled / forceRenderingOff (window diffs)
			public bool DeadLogged;
		}
		private static readonly RendInfo[] NoRenderers = new RendInfo[0];

		private sealed class Tracked
		{
			public int PlayerId;
			public string Name = "?";
			public string UserId = "";
			public string ApiAvatarId = "";
			public GameObject Root; public int RootId;
			public GameObject Avatar; public int AvatarId;   // AvatarId = last live id, kept after a destroy
			public float AvatarTriedAt;
			public RendInfo[] Rend = NoRenderers; public float RendAt;
			public readonly Queue<Light> History = new Queue<Light>();
			public Vis LastState = Vis.Unknown;
			public bool EverVisible;
			public int PrevAvatarId;
			public float FirstSeen;
			public int LastSampleFrame = -1;
			public float CooldownUntil;
			public Session Session;
			public bool Watched, BaselineLogged;
			// Window-independent teardown detector state (catches a block that never becomes a HIDE window).
			public int CorrAvId; public int CorrRend; public bool CorrInit; public float CorrAt;
		}

		// A full snapshot: everything the task list asks for, read once per scheduled point.
		private sealed class Full
		{
			public string Label; public float T;
			public bool RootExists, RootActive, RootInHier; public int RootId, RootLayer;
			public bool HasPos; public Vector3 Pos; public Vector3 Euler;
			public bool HasNetPos; public Vector3 NetPos;
			public bool HasHead; public Vector3 Head;
			public bool PlayerComp; public int PlayerCompId; public string PlayerCompEnabled = "unavailable";
			public bool VrcPlayer; public int VrcPlayerId; public string VrcPlayerEnabled = "unavailable";
			public bool Mgr; public int MgrId; public string MgrEnabled = "unavailable";
			public bool AvatarExists, AvatarActive, AvatarInHier; public int AvatarId, AvatarLayer; public string AvatarName = "";
			public bool Desc; public int DescId;
			public int Renderers, Skinned, Mesh, EnabledFlag, ForceOff, IsVisible, GoActive, Drawable;
			public string Layers = "";
			public bool Anim; public int AnimId; public bool AnimEnabled, AnimActive, AnimController, AnimAvatar, AnimHuman;
			public readonly List<string> Ik = new List<string>();
			public int IkCount, IkEnabled;
			// Component TYPE sets keyed by il2cpp class pointer (see NameOf for why not by name).
			public readonly HashSet<long> RootComps = new HashSet<long>();
			public readonly HashSet<long> AvatarComps = new HashSet<long>();
			public int RootCompCount, AvatarCompCount;
			public int PhysBones;
			public int Audio, AudioEnabled, AudioPlaying;
			public string Nameplate = "unavailable";
			public readonly Dictionary<string, string> Flags = new Dictionary<string, string>(StringComparer.Ordinal);
			// Floats are printed with the T0 dump but never diffed: a countdown that ticks every frame
			// (ModerationManager.field_Private_Single_0 did) would otherwise be "a change" at every point.
			public readonly Dictionary<string, string> Floats = new Dictionary<string, string>(StringComparer.Ordinal);
			public string Moderation = "";
			public string ApiAvatarId = "";
			// motion accumulated since the previous scheduled point (filled by the session)
			public float MovedRoot, MovedNet, MovedHead;
		}

		private sealed class Session
		{
			public Tracked P;
			public string Kind;              // HIDE | SHOW | AVATAR-REPLACED
			public Vis From, To;
			public float T0;
			public int Next;                 // index into Offsets
			public Light Before2, Before1, BeforeLast;
			public Full First, Last;
			public int Lines, Suppressed;
			// per-frame observation
			public bool HaveFrame, HaveNet, HaveHead;
			public Vector3 LastPos, LastNet, LastHeadRel;
			public float MovedRoot, MovedNet, MovedHead;          // since the last scheduled point
			public float TotalRoot, TotalNet, TotalHead;          // over the whole window
			public bool RootActiveLast, RootGoneLogged;
			public bool AvatarAliveLast, AvatarActiveLast, AvatarHierLast;
			public int AvatarIdAtT0, AvatarDestroyCount, AvatarNewCount; public float AvatarDestroyedAt = -1f, AvatarNewAt = -1f;
			public int Flips, FlipFrames, DeadRenderers;
			public float VisibleAgainAt = -1f, HiddenAgainAt = -1f;
			public int MassCount;            // how many players hid within 1 s of this one (incl. itself)
			public Dictionary<string, string> FlagChanges = new Dictionary<string, string>(StringComparer.Ordinal);
		}

		private readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
		private readonly List<Session> _sessions = new List<Session>();
		private readonly List<float> _recentHides = new List<float>();
		private int _cursor, _watchPid, _sessionsDone, _localPid;
		private float _lastPrune, _lastHealth, _capDrain, _lastCorrTally;
		private bool _wasOn;

		// EVENT 33 <-> avatar-teardown correlation (READ-ONLY). NetworkLog captures each event 33 with a
		// timestamp; when a window turns out to be an avatar teardown we time-match the nearest event 33
		// and record the delta. Nothing about event 33 is altered — only its arrival time is read.
		private static int _tearTotal, _tearAfter33;
		private static readonly List<float> _tearDeltasMs = new List<float>();

		// ---------------------------------------------------------------- lifecycle

		public override void OnLateUpdate()
		{
			try
			{
				bool on = false;
				try { on = ModConfig.BlockDebugEnabled != null && ModConfig.BlockDebugEnabled.Value; } catch { }
				if (!on)
				{
					if (_wasOn) { Reset("switched off"); _wasOn = false; FeatureHealth.Idle(HealthId, "off"); }
					return;
				}
				if (!_wasOn) { _wasOn = true; Announce(); }   // WatchCode(33) now lives in BlockedByProbeModule, always-on
				float now = VaClock.Now;

				// 1. Open windows first: their per-frame watch and their scheduled snapshots.
				if (_sessions.Count > 0) StepSessions(now);

				// 2. Baseline: a slice of the player list every frame, so each player is sampled ~4x/s
				//    without one frame paying for the whole instance.
				if (!Core.VaPlayers.Ready) return;
				var players = VRChatArchiveMod.Core.VaPlayers.All();
				if (players == null) return;
				int count;
				try { count = players.Count; } catch { return; }
				if (count > 0)
				{
					float dt = VaClock.Delta;
					if (dt <= 0f || dt > LightTick) dt = LightTick;
					int budget = (int)Math.Ceiling(count * dt / LightTick);
					if (budget < 1) budget = 1;
					if (budget > count) budget = count;
					for (int k = 0; k < budget; k++)
					{
						_cursor = (_cursor + 1) % count;
						VRCPlayerApi api;
						try { api = players[_cursor]; } catch { continue; }
						if (api == null) continue;
						bool local;
						try { local = api.isLocal; } catch { continue; }
						if (local) continue;
						int pid;
						try { pid = api.playerId; } catch { continue; }
						Tracked t = GetOrTrack(pid, api, now);
						if (t == null || t.LastSampleFrame == Time.frameCount) continue;
						Sample(t, api, now);
					}
				}

				// The watched player (BlockDebug/WatchName) is sampled EVERY frame: frame-exact T0 and a
				// dense T-2..T0 history for the one person doing the test with you.
				if (_watchPid != 0 && _tracked.TryGetValue(_watchPid, out Tracked wt) && wt.LastSampleFrame != Time.frameCount)
				{
					VRCPlayerApi wapi = null;
					try { wapi = VRCPlayerApi.GetPlayerById(_watchPid); } catch { }
					if (wapi != null) Sample(wt, wapi, now);
				}

				// Event 33 (moderation / instance control) captured by NetworkLog: surface each new one
				// under our own tag with its decoded payload, even when no window is open — a real block's
				// 33 may arrive slightly before the local hide, or hide the avatar by a path we do not
				// open a window on.
				DrainEvent33(now);

				// 3. Once a second: forget players who left, and say what we are doing.
				if (now - _lastPrune >= 1f)
				{
					_lastPrune = now;
					Prune(players, count, now);
					FeatureHealth.Ok(HealthId, "on — " + _tracked.Count + " remote player(s) tracked, " + _sessions.Count
						+ " window(s) open" + (_sessionsDone > 0 ? ", " + _sessionsDone + " summarised" : "")
						+ (_watchPid != 0 ? ", watching #" + _watchPid + " every frame" : ""));

					// Periodic correlation tally, so the causal answer is visible without waiting for a summary.
					if (_tearTotal > 0 && now - _lastCorrTally >= 15f)
					{
						_lastCorrTally = now;
						Log("[EV33-CORR] session tally: " + _tearAfter33 + "/" + _tearTotal
							+ " avatar teardown(s) preceded by an event 33 within 2 s"
							+ (_tearDeltasMs.Count > 0 ? " — median Δ(event33→teardown) " + MedianMs() + " ms" : "")
							+ ". READ-ONLY: event 33 is only observed, never altered.");
					}
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError(Tag + "update threw: " + e); }
		}

		public override void OnSceneLoaded(int buildIndex) { if (_wasOn) Reset("world changed"); }
		public override void OnShutdown() { if (_wasOn) Reset("shutdown"); }

		private void Announce()
		{
			ResolveTypes();
			Log("observer ON (mod " + PluginInfo.Version + ") — READ-ONLY: nothing on any VRChat object is written, no block state is touched.");
			bool nsfw = false, overBudget = false, roster = false, netlog = false, verified = false;
			string watch = "";
			try { nsfw = ModConfig.NsfwFilter.Value; } catch { }
			try { overBudget = ModConfig.HideAvatarOverBudget.Value; } catch { }
			try { roster = ModConfig.VaTagsEnabled.Value; } catch { }
			try { netlog = ModConfig.NetworkLogEnabled.Value; } catch { }
			try { verified = FieldOffsetFix.Verified; } catch { }
			try { watch = ModConfig.BlockDebugWatchName.Value ?? ""; } catch { }
			Log("environment: FieldOffsetFix.Verified=" + verified + " (field_* flags " + (verified ? "readable" : "SKIPPED: unavailable") + ")"
				+ " | NetworkLog=" + (netlog ? "on" : "OFF (no Photon correlation)")
				+ " | roster(VaTags)=" + (roster ? "on" : "OFF (userId/avatarId may read unavailable)")
				+ " | NsfwFilter=" + (nsfw ? "ON  <-- can disable renderers itself" : "off")
				+ " | HideAvatarOverBudget=" + (overBudget ? "ON  <-- can hide avatars itself" : "off")
				+ " | WatchName='" + watch + "'");
			Log("local: playerId=" + LocalPidText() + " userId=" + Or(LocalUid()));
			Log("types: VRC.Player=" + (_tPlayer != null) + " VRCPlayer=" + (_tVrcPlayer != null) + " VRCAvatarManager=" + (_tAvatarMgr != null)
				+ " PlayerManager=" + (_tPlayerManager != null) + " ModerationManager=" + (_tModerationMgr != null)
				+ " IK controllers=" + _ikResolved + "/" + IkTypeNames.Length);
		}

		// Event 33 arrives via NetworkLog's OnEvent hook; log any captured since the last drain.
		private void DrainEvent33(float now)
		{
			try
			{
				var caps = NetworkLogModule.CapturesSince(_capDrain);
				_capDrain = now;
				for (int i = 0; i < caps.Count; i++)
				{
					var c = caps[i];
					Log("event " + c.Code + " (moderation / instance control) at " + c.Clock + " | code=" + c.Code + " sender(actor)=" + c.Sender
						+ " | payload " + c.Payload + " | local playerId=" + LocalPidText()
						+ " | affected remote id: resolved by avatar-side correlation below, NOT read from the payload");
				}
			}
			catch { }
		}

		private void Reset(string reason)
		{
			try
			{
				if (_sessions.Count > 0)
					foreach (var s in _sessions) Log("window for " + Who(s.P) + " dropped (" + reason + ") at T+" + (VaClock.Now - s.T0).ToString("0.00") + "s — no summary.");
			}
			catch { }
			_sessions.Clear();
			_tracked.Clear();
			_watchPid = 0;
			_cursor = 0;
			if (reason == "switched off" || reason == "shutdown") { try { NetworkLogModule.UnwatchCodes(); } catch { } }
		}

		// ---------------------------------------------------------------- tracking

		private Tracked GetOrTrack(int pid, VRCPlayerApi api, float now)
		{
			if (_tracked.TryGetValue(pid, out Tracked t)) return t;
			if (_tracked.Count >= MaxTracked) return null;
			t = new Tracked { PlayerId = pid, FirstSeen = now };
			// The one il2cpp string read this module does on a VRChat object: the SDK's display name,
			// off an entry fresh from AllPlayers this frame, validated first. Never re-read per frame.
			try { if (NativeGuard.Alive(api)) t.Name = api.displayName ?? "?"; } catch { t.Name = "?"; }
			RefreshIdentity(t);
			_tracked[pid] = t;
			CheckWatched(t);
			return t;
		}

		private void CheckWatched(Tracked t)
		{
			if (t.Watched || _watchPid != 0) return;
			string w = null;
			try { w = ModConfig.BlockDebugWatchName.Value; } catch { }
			if (string.IsNullOrEmpty(w)) return;
			w = w.Trim();
			if (w.Length == 0) return;
			if (!string.Equals(w, t.Name, StringComparison.OrdinalIgnoreCase)
			 && !string.Equals(w, t.UserId, StringComparison.OrdinalIgnoreCase)) return;
			t.Watched = true;
			_watchPid = t.PlayerId;
			Log("watching " + Who(t) + " every frame (BlockDebug/WatchName matches). A BASELINE snapshot follows as soon as their objects exist.");
		}

		// Name / user id / avatar id from the roster VaTags maintains (it already did the risky reads).
		private static void RefreshIdentity(Tracked t)
		{
			try
			{
				var roster = VaTagsModule.Roster;
				if (roster == null) return;
				for (int i = 0; i < roster.Count; i++)
				{
					var e = roster[i];
					if (e == null || e.PlayerId != t.PlayerId) continue;
					if ((t.Name == "?" || string.IsNullOrEmpty(t.Name)) && !string.IsNullOrEmpty(e.Name)) t.Name = e.Name;
					if (!string.IsNullOrEmpty(e.UserId)) t.UserId = e.UserId;
					if (!string.IsNullOrEmpty(e.AvatarId)) t.ApiAvatarId = e.AvatarId;
					return;
				}
			}
			catch { }
		}

		private static VaTagsModule.PlayerEntry RosterEntry(int pid)
		{
			try
			{
				var roster = VaTagsModule.Roster;
				if (roster == null) return null;
				for (int i = 0; i < roster.Count; i++)
				{
					var e = roster[i];
					if (e != null && e.PlayerId == pid) return e;
				}
			}
			catch { }
			return null;
		}

		private void Prune(System.Collections.Generic.List<VRCPlayerApi> players, int count, float now)
		{
			// YOUR playerId changes when you land in another instance (11 -> 13 in the first session):
			// every remote object we hold is then a teardown artefact, not evidence. Same when several
			// players vanish at once. Both drop the open windows instead of summarising them.
			int lp = -1;
			try { var la = PlayerRef.LocalApi(); if (la != null) lp = la.playerId; } catch { }
			if (lp > 0 && _localPid > 0 && lp != _localPid) { Reset("instance changed: your playerId " + _localPid + " -> " + lp); _localPid = lp; return; }
			if (lp > 0) _localPid = lp;

			if (_tracked.Count == 0) return;
			var present = new HashSet<int>();
			for (int i = 0; i < count; i++)
			{
				// A null check is not a liveness check: VRCPlayerApi is not a UnityEngine.Object, so `== null` is
				// the plain managed test and says nothing about the il2cpp object behind the handle. Reading any
				// member off a stale one is an access violation inside the proxy, which no try/catch can stop.
				try { var a = players[i]; if (a != null && Core.NativeGuard.Alive(a)) present.Add(a.playerId); } catch { }
			}
			List<int> gone = null;
			foreach (var kv in _tracked)
				if (!present.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
			if (gone == null) return;
			if (gone.Count >= 3) { Reset("instance teardown: " + gone.Count + " players gone at once"); return; }
			foreach (int pid in gone)
			{
				Tracked t = _tracked[pid];
				if (t.Session != null) Finish(t.Session, now, "player left the instance (no longer in VRChatArchiveMod.Core.VaPlayers.All())");
				if (t.Watched) { Log(Who(t) + " left — the every-frame watch ends with them."); _watchPid = 0; }
				_tracked.Remove(pid);
			}
		}

		// ---------------------------------------------------------------- baseline sample

		private void Sample(Tracked t, VRCPlayerApi api, float now)
		{
			t.LastSampleFrame = Time.frameCount;
			var l = new Light { T = now };

			GameObject go = null;
			try { go = api.gameObject; } catch { go = null; }
			if (go != null)
			{
				int id = 0;
				try { id = go.GetInstanceID(); } catch { }
				if (t.Root is null || t.RootId != id)
				{
					// A new root object for the same playerId: everything cached under it is stale.
					t.Root = go; t.RootId = id;
					t.Avatar = null; t.Rend = NoRenderers; t.RendAt = 0f; t.AvatarTriedAt = 0f;
				}
				l.RootExists = true;
				try { l.RootActive = go.activeSelf; l.RootInHier = go.activeInHierarchy; } catch { }
				try { l.Pos = go.transform.position; l.HasPos = true; } catch { }

				// Avatar root: the child carrying the SDK descriptor. Re-looked-up only when missing, and
				// not more than once a second outside a window (the lookup is a hierarchy walk).
				bool avatarSet = !(t.Avatar is null);
				bool avatarAlive = avatarSet && t.Avatar != null;   // Unity's own destroyed check
				if (!avatarAlive && (t.Session != null || now - t.AvatarTriedAt >= AvatarRetry))
				{
					t.AvatarTriedAt = now;
					if (avatarSet) t.Avatar = null;                   // destroyed: keep AvatarId as "last known"
					GameObject a = FindAvatarRoot(go);
					if (a != null)
					{
						int aid = 0;
						try { aid = a.GetInstanceID(); } catch { }
						t.Avatar = a; t.AvatarId = aid;
						t.Rend = NoRenderers; t.RendAt = 0f;          // renderers belong to the new object
						avatarAlive = true;
					}
				}
				if (avatarAlive)
				{
					l.AvatarId = t.AvatarId;
					try { l.AvatarActive = t.Avatar.activeSelf; l.AvatarInHier = t.Avatar.activeInHierarchy; } catch { }
				}

				if (t.Rend.Length == 0 || now - t.RendAt >= RendererRefresh) Recollect(t, go, now);
				CountRenderers(t, l);
			}

			try { l.NetPos = api.GetPosition(); l.HasNetPos = true; } catch { }

			l.State = Classify(l);
			Detect(t, api, l, now);
			DetectTeardown(t, l, now);

			t.History.Enqueue(l);
			while (t.History.Count > 0 && now - t.History.Peek().T > HistorySeconds) t.History.Dequeue();

			if (t.Watched && !t.BaselineLogged && l.RootExists)
			{
				t.BaselineLogged = true;
				RefreshIdentity(t);
				Full f = TakeFull(t, api, "BASELINE", now);
				Log("---------------- BASELINE for " + Who(t) + " (no transition; proves the pipeline) ----------------");
				LogFull(f, null);
				LogFlags(f, null);
			}
		}

		// The stand-in VRChat shows while an avatar loads (or forever, when it never loads: blocked before
		// you saw them) carries a few renderers of its own on the player root. They must not count as
		// "the avatar": without a descriptor-bearing avatar root the state is NoAvatar, whatever is drawn.
		private static Vis Classify(Light l)
		{
			if (!l.RootExists) return Vis.Unknown;
			if (!l.RootActive || !l.RootInHier) return Vis.Hidden;   // the whole player object switched off
			if (l.AvatarId == 0) return Vis.NoAvatar;                   // loading, destroyed, or never loaded
			if (!l.AvatarInHier) return Vis.Hidden;
			if (l.Renderers == 0) return Vis.NoAvatar;
			if (l.Drawable == 0) return Vis.Hidden;
			return Vis.Visible;
		}

		private void Detect(Tracked t, VRCPlayerApi api, Light l, float now)
		{
			Vis prev = t.LastState;
			t.LastState = l.State;
			bool seenVisibleBefore = t.EverVisible;   // BEFORE this sample: a first load is not a re-show
			if (l.State == Vis.Visible) t.EverVisible = true;

			string kind = null;
			if (prev == Vis.Visible && (l.State == Vis.Hidden || l.State == Vis.NoAvatar)) kind = "HIDE";
			else if (prev != Vis.Unknown && prev != Vis.Visible && l.State == Vis.Visible && seenVisibleBefore) kind = "SHOW";
			else if (prev == Vis.Visible && l.State == Vis.Visible && t.PrevAvatarId != 0 && l.AvatarId != 0 && l.AvatarId != t.PrevAvatarId) kind = "AVATAR-REPLACED";
			if (l.AvatarId != 0) t.PrevAvatarId = l.AvatarId;
			if (kind == null) return;

			if (t.Session != null) return;   // the open window's own snapshots record it
			if (now < t.CooldownUntil)
			{
				Log(kind + " for " + Who(t) + " during cooldown (" + prev + " -> " + l.State + ") — not opening another window.");
				return;
			}
			// Several players hiding in the same second is you leaving the instance, or avatar culling
			// kicking in — never an individual block. Measured 2026-09-11: three roots deactivated
			// within 0.6 s, all "left" a moment later, local playerId changed right after.
			if (kind == "HIDE")
			{
				_recentHides.RemoveAll(x => now - x > 1f);
				_recentHides.Add(now);
				if (_recentHides.Count >= 3)
				{
					Log("mass HIDE: " + _recentHides.Count + " players hidden within 1 s (" + Who(t) + " is the latest) — an instance change or avatar culling, not an individual event; no window opened.");
					t.CooldownUntil = now + 2f;
					return;
				}
			}
			Open(t, api, kind, prev, l, now);
		}

		// ---------------------------------------------------------------- renderers

		private static void Recollect(Tracked t, GameObject root, float now)
		{
			t.RendAt = now;
			try
			{
				GameObject from = (!(t.Avatar is null) && t.Avatar != null) ? t.Avatar : root;
				Renderer[] rs = from.GetComponentsInChildren<Renderer>(true);
				if (rs == null) { t.Rend = NoRenderers; return; }
				int n = Math.Min(rs.Length, MaxRenderersTracked);
				var list = new List<RendInfo>(n);
				for (int i = 0; i < n; i++)
				{
					Renderer r = rs[i];
					if (r == null) continue;
					var ri = new RendInfo { R = r };
					try { ri.Id = r.GetInstanceID(); } catch { }
					try { ri.Go = r.gameObject; } catch { }
					try { ri.Kind = r.TryCast<SkinnedMeshRenderer>() != null ? "SkinnedMeshRenderer" : r.TryCast<MeshRenderer>() != null ? "MeshRenderer" : NameOf(r); } catch { ri.Kind = "Renderer"; }
					try { string nm = r.name ?? ""; ri.Name = nm.Length > 40 ? nm.Substring(0, 40) : nm; } catch { ri.Name = "?"; }
					try { ri.En = r.enabled; } catch { }
					try { ri.Fo = r.forceRenderingOff; } catch { }
					list.Add(ri);
				}
				t.Rend = list.ToArray();
			}
			catch { t.Rend = NoRenderers; }
		}

		private static void CountRenderers(Tracked t, Light l)
		{
			int total = 0, drawable = 0, enabledFlag = 0, forceOff = 0, dead = 0;
			var arr = t.Rend;
			for (int i = 0; i < arr.Length; i++)
			{
				var ri = arr[i];
				try
				{
					if (ri.R == null) { dead++; continue; }
					total++;
					bool en = ri.R.enabled;
					bool fo = ri.R.forceRenderingOff;
					bool ga = ri.Go != null && ri.Go.activeInHierarchy;
					if (en) enabledFlag++;
					if (fo) forceOff++;
					if (en && !fo && ga) drawable++;
				}
				catch { }
			}
			// Every cached renderer destroyed: the list is stale, re-collect on the next sample.
			if (arr.Length > 0 && dead == arr.Length) t.RendAt = 0f;
			l.Renderers = total; l.Drawable = drawable; l.EnabledFlag = enabledFlag; l.ForceOff = forceOff;
		}

		// ---------------------------------------------------------------- window

		private void Open(Tracked t, VRCPlayerApi api, string kind, Vis prev, Light l, float now)
		{
			if (_sessions.Count >= MaxSessions)
			{
				Log("transition " + kind + " for " + Who(t) + " NOT tracked: " + MaxSessions + " windows already open.");
				t.CooldownUntil = now + 2f;
				return;
			}
			RefreshIdentity(t);
			var s = new Session { P = t, Kind = kind, From = prev, To = l.State, T0 = now, MassCount = kind == "HIDE" ? _recentHides.Count : 0 };
			t.Session = s;
			_sessions.Add(s);

			Log("================ TRANSITION " + kind + " ================");
			SLog(s, "Player=" + t.PlayerId + " name='" + t.Name + "' userId=" + Or(t.UserId) + " | local playerId=" + LocalPidText()
				+ " userId=" + Or(LocalUid()) + " | " + prev + " -> " + l.State + " | clock=" + DateTime.Now.ToString("HH:mm:ss.fff")
				+ " rt=" + now.ToString("F3") + " frame=" + Time.frameCount + " | tracked for " + (now - t.FirstSeen).ToString("0.0") + "s"
				+ (t.Watched ? " | sampled every frame" : " | sampled ~4x/s (T0 is within " + LightTick + "s of the change)"));

			// Pre-window, from the baseline history.
			s.Before2 = Nearest(t, now - 2f);
			s.Before1 = Nearest(t, now - 1f);
			s.BeforeLast = Previous(t, now);
			LogLight(s, "T-2.00s", s.Before2, now);
			LogLight(s, "T-1.00s", s.Before1, now);
			if (s.BeforeLast != null && (s.Before1 == null || s.BeforeLast != s.Before1)) LogLight(s, "T-last ", s.BeforeLast, now);

			// T0.
			Full f = TakeFull(t, api, "T+0.00s", now);
			s.First = s.Last = f;
			LogFull(f, s);
			LogFlags(f, s);

			s.RootActiveLast = f.RootActive;
			s.AvatarAliveLast = f.AvatarExists; s.AvatarActiveLast = f.AvatarActive; s.AvatarHierLast = f.AvatarInHier;
			s.AvatarIdAtT0 = f.AvatarExists ? f.AvatarId : 0;
			if (f.HasPos) { s.HaveFrame = true; s.LastPos = f.Pos; }
			if (f.HasNetPos) { s.HaveNet = true; s.LastNet = f.NetPos; }
			if (f.HasHead && f.HasPos) { s.HaveHead = true; s.LastHeadRel = f.Head - f.Pos; }
			// The per-frame renderer watch starts from the states read at T0.
			foreach (var ri in t.Rend) { try { if (ri.R != null) { ri.En = ri.R.enabled; ri.Fo = ri.R.forceRenderingOff; } } catch { } }
		}

		private static Light Nearest(Tracked t, float at)
		{
			Light best = null; float bd = float.MaxValue;
			foreach (var l in t.History)
			{
				float d = Math.Abs(l.T - at);
				if (d < bd) { bd = d; best = l; }
			}
			return bd <= 0.35f ? best : null;
		}

		private static Light Previous(Tracked t, float before)
		{
			Light best = null;
			foreach (var l in t.History) if (l.T < before) best = l;
			return best;
		}

		private void StepSessions(float now)
		{
			var copy = _sessions.ToArray();
			for (int i = 0; i < copy.Length; i++)
			{
				var s = copy[i];
				if (!_sessions.Contains(s)) continue;
				try
				{
					VRCPlayerApi api = null;
					try { api = VRCPlayerApi.GetPlayerById(s.P.PlayerId); } catch { api = null; }
					if (api == null) { Finish(s, now, "player left the instance (VRCPlayerApi.GetPlayerById returned null)"); continue; }

					FrameStep(s, api, now);

					if (s.Next < Offsets.Length && now - s.T0 >= Offsets[s.Next])
					{
						string label = "T+" + Offsets[s.Next].ToString("0.00") + "s";
						Full f = TakeFull(s.P, api, label, now);
						f.MovedRoot = s.MovedRoot; f.MovedNet = s.MovedNet; f.MovedHead = s.MovedHead;
						s.MovedRoot = s.MovedNet = s.MovedHead = 0f;
						LogDiff(s, s.Last, f);
						LogFull(f, s);
						s.Last = f;
						s.Next++;
						if (s.Next >= Offsets.Length) Finish(s, now, null);
					}
				}
				catch (Exception e) { SLog(s, "window step threw: " + Unwrap.Describe(e)); }
			}
		}

		// Between scheduled points: the cheap things that can change in ONE frame — root/avatar
		// existence and active flags, and the enabled/forceRenderingOff of the cached renderers.
		private void FrameStep(Session s, VRCPlayerApi api, float now)
		{
			Tracked t = s.P;
			float dt = now - s.T0;
			string at = "  [T+" + dt.ToString("0.00") + "s]";
			GameObject go = t.Root;

			if (!(go is null) && go == null)
			{
				if (!s.RootGoneLogged) { s.RootGoneLogged = true; SLog(s, "Player root object destroyed: Player=" + t.PlayerId + " InstanceID=" + t.RootId + at); }
				go = null;
			}
			if (go != null)
			{
				try
				{
					bool ra = go.activeSelf;
					if (ra != s.RootActiveLast) { SLog(s, "Player root activeSelf changed: Player=" + t.PlayerId + " " + s.RootActiveLast + " -> " + ra + at); s.RootActiveLast = ra; }
				}
				catch { }
				try
				{
					Vector3 p = go.transform.position;
					if (s.HaveFrame) { float d = (p - s.LastPos).magnitude; s.MovedRoot += d; s.TotalRoot += d; }
					s.LastPos = p; s.HaveFrame = true;
				}
				catch { }
			}
			try
			{
				Vector3 np = api.GetPosition();
				if (s.HaveNet) { float d = (np - s.LastNet).magnitude; s.MovedNet += d; s.TotalNet += d; }
				s.LastNet = np; s.HaveNet = true;
			}
			catch { }
			try
			{
				Vector3 h = api.GetBonePosition(HumanBodyBones.Head);
				if (h != Vector3.zero && s.HaveFrame)
				{
					Vector3 rel = h - s.LastPos;
					if (s.HaveHead) { float d = (rel - s.LastHeadRel).magnitude; s.MovedHead += d; s.TotalHead += d; }
					s.LastHeadRel = rel; s.HaveHead = true;
				}
			}
			catch { }

			// Avatar object: destroyed? replaced? active flags?
			bool avSet = !(t.Avatar is null);
			bool avAlive = avSet && t.Avatar != null;
			if (s.AvatarAliveLast && !avAlive)
			{
				s.AvatarDestroyCount++;
				if (s.AvatarDestroyedAt < 0f) s.AvatarDestroyedAt = dt;
				SLog(s, "Avatar object destroyed: Player=" + t.PlayerId + " InstanceID=" + t.AvatarId + at);
				if (avSet) t.Avatar = null;
			}
			if (!avAlive && go != null)
			{
				GameObject a = FindAvatarRoot(go);
				if (a != null)
				{
					int aid = 0;
					try { aid = a.GetInstanceID(); } catch { }
					string nm = "?";
					try { nm = a.name; } catch { }
					if (aid != t.AvatarId || s.AvatarDestroyCount > 0)
					{
						s.AvatarNewCount++;
						if (s.AvatarNewAt < 0f) s.AvatarNewAt = dt;
						SLog(s, "New avatar instance created: Player=" + t.PlayerId + " InstanceID=" + aid + " (was " + t.AvatarId + ") '" + nm + "'" + at);
					}
					t.Avatar = a; t.AvatarId = aid;
					Recollect(t, go, now);
					avAlive = true;
					try { s.AvatarActiveLast = a.activeSelf; s.AvatarHierLast = a.activeInHierarchy; } catch { }
				}
			}
			else if (avAlive)
			{
				try
				{
					bool aa = t.Avatar.activeSelf, ah = t.Avatar.activeInHierarchy;
					if (aa != s.AvatarActiveLast) { SLog(s, "Avatar GameObject activeSelf changed: Player=" + t.PlayerId + " InstanceID=" + t.AvatarId + " " + s.AvatarActiveLast + " -> " + aa + at); s.AvatarActiveLast = aa; }
					if (ah != s.AvatarHierLast) { SLog(s, "Avatar GameObject activeInHierarchy changed: Player=" + t.PlayerId + " InstanceID=" + t.AvatarId + " " + s.AvatarHierLast + " -> " + ah + at); s.AvatarHierLast = ah; }
				}
				catch { }
			}
			s.AvatarAliveLast = avAlive;

			// Renderers, per frame, against the last state we printed.
			int changed = 0, drawable = 0, alive = 0;
			var arr = t.Rend;
			for (int i = 0; i < arr.Length; i++)
			{
				var ri = arr[i];
				try
				{
					if (ri.R == null)
					{
						if (!ri.DeadLogged)
						{
							ri.DeadLogged = true; s.DeadRenderers++; changed++;
							if (changed <= 8) SLog(s, "Renderer destroyed: Player=" + t.PlayerId + " Renderer=" + ri.Id + " (" + ri.Kind + " '" + ri.Name + "')" + at);
						}
						continue;
					}
					alive++;
					bool en = ri.R.enabled;
					bool fo = ri.R.forceRenderingOff;
					if (en != ri.En)
					{
						changed++; s.Flips++;
						if (changed <= 8) SLog(s, "Renderer changed: Player=" + t.PlayerId + " Renderer=" + ri.Id + " (" + ri.Kind + " '" + ri.Name + "') enabled: " + Bool(ri.En) + " -> " + Bool(en) + at);
						ri.En = en;
					}
					if (fo != ri.Fo)
					{
						changed++; s.Flips++;
						if (changed <= 8) SLog(s, "Renderer changed: Player=" + t.PlayerId + " Renderer=" + ri.Id + " (" + ri.Kind + " '" + ri.Name + "') forceRenderingOff: " + Bool(ri.Fo) + " -> " + Bool(fo) + at);
						ri.Fo = fo;
					}
					if (en && !fo && ri.Go != null && ri.Go.activeInHierarchy) drawable++;
				}
				catch { }
			}
			if (changed > 8) SLog(s, "  ... and " + (changed - 8) + " more renderer change(s) this frame" + at);
			if (changed > 0) s.FlipFrames++;

			// Visible again / hidden again, for the summary's "transient?" answer.
			bool drawn = go != null && s.RootActiveLast && avAlive && s.AvatarHierLast && drawable > 0;
			if (s.Kind == "HIDE" && drawn && s.VisibleAgainAt < 0f) { s.VisibleAgainAt = dt; SLog(s, "Avatar is DRAWN again: " + drawable + "/" + alive + " renderer(s) drawable" + at); }
			if (s.Kind == "SHOW" && !drawn && alive > 0 && s.HiddenAgainAt < 0f) { s.HiddenAgainAt = dt; SLog(s, "Avatar is HIDDEN again: 0/" + alive + " renderer(s) drawable" + at); }
		}

		private void Finish(Session s, float now, string early)
		{
			if (!_sessions.Remove(s)) return;
			try { Summary(s, now, early); }
			catch (Exception e) { Log("summary for " + Who(s.P) + " threw: " + Unwrap.Describe(e)); }
			s.P.Session = null;
			s.P.CooldownUntil = now + Cooldown;
			_sessionsDone++;
			if (s.Suppressed > 0) Log(s.Suppressed + " line(s) of this window were suppressed by the " + MaxLinesPerSession + "-line cap.");
			Log("================ end of window for " + Who(s.P) + " (" + s.Lines + " lines) ================");
		}

		// ---------------------------------------------------------------- full snapshot

		private Full TakeFull(Tracked t, VRCPlayerApi api, string label, float now)
		{
			var f = new Full { Label = label, T = now };
			string localUid = LocalUid();
			f.ApiAvatarId = t.ApiAvatarId ?? "";

			GameObject go = null;
			try { go = api.gameObject; } catch { }
			if (go == null && !(t.Root is null) && t.Root != null) go = t.Root;
			if (go != null)
			{
				f.RootExists = true;
				try { f.RootId = go.GetInstanceID(); } catch { }
				try { f.RootActive = go.activeSelf; f.RootInHier = go.activeInHierarchy; f.RootLayer = go.layer; } catch { }
				try { var tr = go.transform; f.Pos = tr.position; f.Euler = tr.rotation.eulerAngles; f.HasPos = true; } catch { }
			}
			try { f.NetPos = api.GetPosition(); f.HasNetPos = true; } catch { }
			try { Vector3 h = api.GetBonePosition(HumanBodyBones.Head); if (h != Vector3.zero) { f.Head = h; f.HasHead = true; } } catch { }

			// VRC.Player → VRCPlayer → VRCAvatarManager, by the members the mod already resolves elsewhere.
			object player = null, vrcPlayer = null, mgr = null;
			try
			{
				var e = RosterEntry(t.PlayerId);
				if (e != null && e.Player != null && NativeGuard.Alive(e.Player)) player = e.Player;
				if (player == null && go != null && _il2Player != null)
				{
					Component c = null;
					try { c = go.GetComponentSafe(_il2Player); } catch { }
					if (c == null) { try { c = go.GetComponentInChildrenSafe(_il2Player, true); } catch { } }
					if (c != null && NativeGuard.Alive(c)) player = AsConcrete(c, _tPlayer) ?? c;
				}
			}
			catch { }
			if (player != null)
			{
				f.PlayerComp = true;
				DescribeComponent(player, out f.PlayerCompId, out f.PlayerCompEnabled);
				if (string.IsNullOrEmpty(t.UserId)) { try { t.UserId = FewTagsModule.UserIdOf(player) ?? ""; } catch { } }
				try { vrcPlayer = FewTagsModule.GetMemberByTypeName(player, "VRCPlayer", "_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0"); } catch { }
			}
			if (vrcPlayer != null && NativeGuard.Alive(vrcPlayer))
			{
				f.VrcPlayer = true;
				DescribeComponent(vrcPlayer, out f.VrcPlayerId, out f.VrcPlayerEnabled);
				try { mgr = FewTagsModule.GetMemberByTypeName(vrcPlayer, "VRCAvatarManager", "prop_VRCAvatarManager_0", "field_Private_VRCAvatarManager_0"); } catch { }
			}
			else vrcPlayer = null;
			if (mgr != null && NativeGuard.Alive(mgr))
			{
				f.Mgr = true;
				DescribeComponent(mgr, out f.MgrId, out f.MgrEnabled);
			}
			else mgr = null;

			// VRChat's OWN flags, read by member name and never interpreted: every bool/int/enum, every
			// Unity-object reference (as instance id + active), every collection (count, and whether it
			// contains the local or the remote user id) on the objects that own this player; floats are
			// kept aside for the T0 dump only. A diff between two snapshots is what names the flag a
			// block flips, if there is one. Done BEFORE the component walk below: reading these typed
			// members also teaches the class-name cache what the obfuscated components are called.
			ProbeFlags(f, "VRC.Player", player, localUid, t.UserId);
			ProbeFlags(f, "VRCPlayer", vrcPlayer, localUid, t.UserId);
			ProbeFlags(f, "VRCAvatarManager", mgr, localUid, t.UserId);
			for (int i = 0; i < IkTypeNames.Length; i++)
			{
				if (_ikIl2 == null || _ikIl2[i] == null || go == null) continue;
				try
				{
					Component c = go.GetComponentInChildrenSafe(_ikIl2[i], true);
					if (c == null) { f.Flags[IkTypeNames[i]] = "none"; continue; }
					Learn(ClassOf(c), IkTypeNames[i]);
					object conc = AsConcrete(c, _ikTypes[i]) ?? c;
					ProbeFlags(f, IkTypeNames[i], conc, localUid, t.UserId);
				}
				catch { }
			}
			ProbeStatic(f, "PlayerManager", _tPlayerManager, "prop_PlayerManager_0", localUid, t.UserId);
			ProbeStatic(f, "ModerationManager", _tModerationMgr, "prop_ModerationManager_0", localUid, t.UserId);

			// Avatar root.
			GameObject avatar = (!(t.Avatar is null) && t.Avatar != null) ? t.Avatar : null;
			if (avatar == null && go != null)
			{
				avatar = FindAvatarRoot(go);
				if (avatar != null) { t.Avatar = avatar; try { t.AvatarId = avatar.GetInstanceID(); } catch { } t.Rend = NoRenderers; }
			}
			if (avatar != null)
			{
				f.AvatarExists = true;
				try { f.AvatarId = avatar.GetInstanceID(); } catch { }
				try { f.AvatarActive = avatar.activeSelf; f.AvatarInHier = avatar.activeInHierarchy; f.AvatarLayer = avatar.layer; } catch { }
				try { string n = avatar.name ?? ""; f.AvatarName = n.Length > 48 ? n.Substring(0, 48) : n; } catch { }
				try
				{
					var d = avatar.GetComponent<VRCAvatarDescriptor>();
					if (d != null) { f.Desc = true; f.DescId = d.GetInstanceID(); }
				}
				catch { }
			}
			GameObject scope = avatar ?? go;

			// Renderers, fresh (not the cached list): counts by kind and by state, and their layers.
			if (scope != null)
			{
				try
				{
					var rs = scope.GetComponentsInChildren<Renderer>(true);
					var layers = new SortedSet<int>();
					if (rs != null)
						for (int i = 0; i < rs.Length; i++)
						{
							var r = rs[i];
							if (r == null) continue;
							f.Renderers++;
							try { if (r.TryCast<SkinnedMeshRenderer>() != null) f.Skinned++; else if (r.TryCast<MeshRenderer>() != null) f.Mesh++; } catch { }
							bool en = false, fo = false, ga = false;
							try { en = r.enabled; if (en) f.EnabledFlag++; } catch { }
							try { fo = r.forceRenderingOff; if (fo) f.ForceOff++; } catch { }
							try { if (r.isVisible) f.IsVisible++; } catch { }
							try { var rg = r.gameObject; ga = rg != null && rg.activeInHierarchy; if (ga) f.GoActive++; layers.Add(rg.layer); } catch { }
							if (en && !fo && ga) f.Drawable++;
						}
					var sb = new StringBuilder();
					int k = 0;
					foreach (int l in layers) { if (k++ > 0) sb.Append(','); if (k > 8) { sb.Append("…"); break; } sb.Append(l); }
					f.Layers = sb.ToString();
				}
				catch { }

				try
				{
					var anim = scope.GetComponentInChildren<Animator>(true);
					if (anim != null)
					{
						f.Anim = true;
						try { f.AnimId = anim.GetInstanceID(); } catch { }
						try { f.AnimEnabled = anim.enabled; } catch { }
						try { f.AnimActive = anim.isActiveAndEnabled; } catch { }
						try { f.AnimController = anim.runtimeAnimatorController != null; } catch { }
						try { f.AnimAvatar = anim.avatar != null; } catch { }
						try { f.AnimHuman = anim.isHuman; } catch { }
					}
				}
				catch { }

				try { var pb = scope.GetComponentsInChildren<VRCPhysBone>(true); f.PhysBones = pb != null ? pb.Length : 0; } catch { }
			}

			// IK / tracking / pose components anywhere under the player root, by class name (MimicPose's
			// list), with enabled state — the E) question. Also every component TYPE on the two roots.
			if (go != null)
			{
				try
				{
					var bs = go.GetComponentsInChildren<Behaviour>(true);
					if (bs != null)
						for (int i = 0; i < bs.Length && f.Ik.Count < 24; i++)
						{
							var b = bs[i];
							if (b == null) continue;
							string n = NameOf(b);
							if (!IsIkName(n)) continue;
							f.IkCount++;
							bool on = false, act = false; int id = 0;
							try { on = b.enabled; } catch { }
							try { act = b.isActiveAndEnabled; } catch { }
							try { id = b.GetInstanceID(); } catch { }
							if (on) f.IkEnabled++;
							f.Ik.Add(n + "#" + id + (on ? (act ? " on" : " on/inactive") : " OFF"));
						}
				}
				catch { }
				try { CollectComponentNames(go, f.RootComps, out f.RootCompCount); } catch { }
				if (avatar != null) { try { CollectComponentNames(avatar, f.AvatarComps, out f.AvatarCompCount); } catch { } }

				try
				{
					var au = go.GetComponentsInChildren<AudioSource>(true);
					if (au != null)
						for (int i = 0; i < au.Length; i++)
						{
							var a = au[i];
							if (a == null) continue;
							f.Audio++;
							try { if (a.enabled) f.AudioEnabled++; } catch { }
							try { if (a.isPlaying) f.AudioPlaying++; } catch { }
						}
				}
				catch { }
			}

			// Nameplate container (FewTags' resolver), since a block might hide that too.
			if (player != null)
			{
				try
				{
					var np = FewTagsModule.FindNameplateContainer(player);
					if (np == null) f.Nameplate = "not found";
					else f.Nameplate = "#" + np.GetInstanceID() + " active=" + Bool(np.activeSelf) + "/" + Bool(np.activeInHierarchy);
				}
				catch (Exception e) { f.Nameplate = "unavailable (" + Unwrap.Root(e).GetType().Name + ")"; }
			}

			// What the mod already believes, so the reader can weigh it against the objects.
			try
			{
				string tag = BlockedByProbeModule.Tag(t.UserId ?? "");
				f.Moderation = string.IsNullOrEmpty(tag) ? "local-list=none" : "local-list=" + tag;
			}
			catch { f.Moderation = "unavailable"; }
			return f;
		}

		private static void DescribeComponent(object o, out int id, out string enabled)
		{
			id = 0; enabled = "unavailable";
			try
			{
				var b = o as Il2CppObjectBase;
				if (b == null) return;
				var c = b.TryCast<Component>();
				if (c != null) { try { id = c.GetInstanceID(); } catch { } }
				var bh = b.TryCast<Behaviour>();
				if (bh != null) { try { enabled = bh.enabled ? "on" : "OFF"; } catch { } }
				else if (c == null) enabled = "n/a (not a Component)";
			}
			catch { }
		}

		private static void CollectComponentNames(GameObject go, HashSet<long> into, out int count)
		{
			count = 0;
			var comps = go.GetComponents<Component>();
			if (comps == null) return;
			count = comps.Length;
			for (int i = 0; i < comps.Length && i < 64; i++)
			{
				var c = comps[i];
				if (c == null) { into.Add(0L); continue; }   // 0 = a destroyed component slot
				IntPtr k = ClassOf(c);
				into.Add((long)k);
				NameOf(c);   // teaches the name cache while we are here
			}
		}

		private static string RenderClasses(IEnumerable<long> classes)
		{
			var sb = new StringBuilder();
			foreach (long k in classes) { if (sb.Length > 0) sb.Append(", "); sb.Append(k == 0L ? "<destroyed>" : NameForClass((IntPtr)k)); }
			return sb.ToString();
		}

		// ---------------------------------------------------------------- flags probe

		private static readonly Dictionary<Type, PropertyInfo[]> PropCache = new Dictionary<Type, PropertyInfo[]>();

		private static void ProbeStatic(Full f, string prefix, Type type, string instanceProp, string localUid, string remoteUid)
		{
			if (type == null) { f.Flags[prefix] = "unavailable (type not found)"; return; }
			try
			{
				PropertyInfo p = type.GetProperty(instanceProp, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (p == null) { f.Flags[prefix] = "unavailable (no " + instanceProp + ")"; return; }
				object inst = p.GetValue(null, null);
				ProbeFlags(f, prefix, inst, localUid, remoteUid);
			}
			catch (Exception e) { f.Flags[prefix] = "unavailable (" + Unwrap.Root(e).GetType().Name + ")"; }
		}

		private static void ProbeFlags(Full f, string prefix, object obj, string localUid, string remoteUid)
		{
			try
			{
				if (obj == null) { f.Flags[prefix] = "null"; return; }
				var b = obj as Il2CppObjectBase;
				if (b != null && !NativeGuard.Alive(b)) { f.Flags[prefix] = "not a live object"; return; }

				Type ty = obj.GetType();
				PropertyInfo[] props;
				if (!PropCache.TryGetValue(ty, out props))
				{
					try { props = ty.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
					catch { props = new PropertyInfo[0]; }
					PropCache[ty] = props;
				}
				bool fieldsOk = false;
				try { fieldsOk = FieldOffsetFix.Verified; } catch { }
				int n = 0, skippedFields = 0;
				for (int i = 0; i < props.Length && n < 120; i++)
				{
					var p = props[i];
					string name = p.Name;
					bool isField = name.StartsWith("field_", StringComparison.Ordinal);
					if (!isField && !name.StartsWith("prop_", StringComparison.Ordinal)) continue;
					if (isField && !fieldsOk) { skippedFields++; continue; }
					if (!p.CanRead) continue;
					try { if (p.GetIndexParameters().Length > 0) continue; } catch { continue; }
					Type pt = p.PropertyType;
					string key = prefix + "." + name;
					try
					{
						if (pt == typeof(float) || pt == typeof(double))
						{
							object v = p.GetValue(obj, null);
							f.Floats[key] = v == null ? "null" : v.ToString();
							n++;
						}
						else if (IsPrimitiveish(pt))
						{
							object v = p.GetValue(obj, null);
							f.Flags[key] = v == null ? "null" : v.ToString();
							n++;
						}
						else if (typeof(UnityEngine.Object).IsAssignableFrom(pt))
						{
							var uo = p.GetValue(obj, null) as UnityEngine.Object;
							if (uo != null) Learn(ClassOf(uo), pt.Name);   // the declared type names the obfuscated class
							f.Flags[key] = DescribeUnityObject(uo);
							n++;
						}
						else if (IsCollectionType(pt))
						{
							f.Flags[key] = DescribeCollection(p.GetValue(obj, null), localUid, remoteUid);
							n++;
						}
						// Strings, structs and other references are deliberately NOT read here: an il2cpp
						// string off a stale field is the one read this mod knows to be fatal.
					}
					catch (Exception e) { f.Flags[key] = "unavailable (" + Unwrap.Root(e).GetType().Name + ")"; }
				}
				if (skippedFields > 0) f.Flags[prefix + ".<field_*>"] = "unavailable (" + skippedFields + " field members skipped: FieldOffsetFix not verified)";
				if (n == 0 && skippedFields == 0) f.Flags[prefix] = "no readable member";
			}
			catch (Exception e) { f.Flags[prefix] = "unavailable (" + Unwrap.Root(e).GetType().Name + ")"; }
		}

		private static bool IsPrimitiveish(Type ty)
		{
			if (ty == null) return false;
			if (ty.IsEnum) return true;
			return ty == typeof(bool) || ty == typeof(int) || ty == typeof(uint)
				|| ty == typeof(float) || ty == typeof(double) || ty == typeof(byte)
				|| ty == typeof(short) || ty == typeof(long) || ty == typeof(ushort) || ty == typeof(ulong);
		}

		private static bool IsCollectionType(Type pt)
		{
			if (pt == null) return false;
			if (typeof(Il2CppArrayBase).IsAssignableFrom(pt)) return true;
			string n = pt.Name;
			return n.StartsWith("HashSet`", StringComparison.Ordinal) || n.StartsWith("List`", StringComparison.Ordinal)
				|| n.StartsWith("Dictionary`", StringComparison.Ordinal);
		}

		private static string DescribeUnityObject(UnityEngine.Object o)
		{
			try
			{
				if (o == null) return "null";
				// THE FRAME THAT KILLED THE CLR (dump 2026-09-18, thread 0):
				//   DescribeUnityObject -> UnityEngine.Object.GetInstanceID() -> il2cpp_runtime_invoke -> ExecutionEngineException.
				// `o == null` is a UnityEngine.Object lifetime check, not an il2cpp liveness check: a wrapper
				// whose native object was already freed passes == null yet still enters the native invoke on a
				// dead pointer, corrupting the runtime -- an EE exception no try/catch can contain.
				if (!Core.NativeGuard.Alive(o)) return "dead";
				string s = "#" + o.GetInstanceID();
				var go = o.TryCast<GameObject>();
				if (go != null) return s + " active=" + Bool(go.activeSelf) + "/" + Bool(go.activeInHierarchy);
				var bh = o.TryCast<Behaviour>();
				if (bh != null) return s + " enabled=" + Bool(bh.enabled);
				var r = o.TryCast<Renderer>();
				if (r != null) return s + " enabled=" + Bool(r.enabled) + " forceOff=" + Bool(r.forceRenderingOff);
				return s;
			}
			catch { return "unavailable"; }
		}

		private static string DescribeCollection(object v, string localUid, string remoteUid)
		{
			try
			{
				if (v == null) return "null";
				var b = v as Il2CppObjectBase;
				if (b == null) return "?";
				if (!NativeGuard.Alive(b)) return "not a live object";
				var arr = v as Il2CppArrayBase;
				if (arr != null) return "len=" + arr.Length;
				Type vt = v.GetType();
				var sb = new StringBuilder();
				PropertyInfo cp = vt.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
				if (cp != null) { try { sb.Append("n=").Append(cp.GetValue(v, null)); } catch { sb.Append("n=?"); } }
				MethodInfo contains = null;
				try { contains = vt.GetMethod("Contains", new[] { typeof(string) }); } catch { }
				if (contains != null)
				{
					if (!string.IsNullOrEmpty(localUid)) { try { sb.Append(" hasLocalUid=").Append(Bool((bool)contains.Invoke(v, new object[] { localUid }))); } catch { sb.Append(" hasLocalUid=?"); } }
					if (!string.IsNullOrEmpty(remoteUid)) { try { sb.Append(" hasRemoteUid=").Append(Bool((bool)contains.Invoke(v, new object[] { remoteUid }))); } catch { sb.Append(" hasRemoteUid=?"); } }
				}
				return sb.Length == 0 ? "(" + vt.Name + ")" : sb.ToString();
			}
			catch { return "unavailable"; }
		}

		// ---------------------------------------------------------------- output

		private static void Log(string s)
		{
			try { VRChatArchiveModPlugin.Logger.LogInfo(Tag + s); } catch { }
		}

		// A line that belongs to a window: counted, and capped so one bad frame cannot flood the log.
		private static void SLog(Session s, string line)
		{
			if (s.Lines >= MaxLinesPerSession) { s.Suppressed++; return; }
			s.Lines++;
			Log(line);
		}

		private static void LogLight(Session s, string label, Light l, float t0)
		{
			if (l == null) { SLog(s, label + ": no sample (player tracked for " + (t0 - s.P.FirstSeen).ToString("0.0") + "s; history " + s.P.History.Count + " sample(s))"); return; }
			SLog(s, label + " (actual T" + (l.T - t0 >= 0 ? "+" : "") + (l.T - t0).ToString("0.00") + "s): state=" + l.State
				+ " | root " + (l.RootExists ? "YES active=" + Bool(l.RootActive) + "/" + Bool(l.RootInHier) : "NO")
				+ " | avatar " + (l.AvatarId != 0 ? "#" + l.AvatarId + " active=" + Bool(l.AvatarActive) + "/" + Bool(l.AvatarInHier) : "none")
				+ " | renderers=" + l.Renderers + " drawable=" + l.Drawable + " enabledFlag=" + l.EnabledFlag + " forceOff=" + l.ForceOff
				+ " | pos=" + (l.HasPos ? V(l.Pos) : "unavailable") + " netPos=" + (l.HasNetPos ? V(l.NetPos) : "unavailable"));
		}

		private static void LogFull(Full f, Session s)
		{
			var sb = new StringBuilder(512);
			sb.Append(f.Label).Append(s != null ? " Player=" + s.P.PlayerId : "").Append(" | root ")
			  .Append(f.RootExists ? "#" + f.RootId + " active=" + Bool(f.RootActive) + "/" + Bool(f.RootInHier) + " layer=" + f.RootLayer : "NONE")
			  .Append(" pos=").Append(f.HasPos ? V(f.Pos) : "unavailable").Append(" rot=").Append(f.HasPos ? V(f.Euler) : "unavailable")
			  .Append(" netPos=").Append(f.HasNetPos ? V(f.NetPos) : "unavailable")
			  .Append(" | VRC.Player ").Append(f.PlayerComp ? "#" + f.PlayerCompId + " " + f.PlayerCompEnabled : "NONE")
			  .Append(" | VRCPlayer ").Append(f.VrcPlayer ? "#" + f.VrcPlayerId + " " + f.VrcPlayerEnabled : "NONE")
			  .Append(" | AvatarManager ").Append(f.Mgr ? "#" + f.MgrId + " " + f.MgrEnabled : "NONE")
			  .Append(" | avatar ").Append(f.AvatarExists ? "#" + f.AvatarId + " '" + f.AvatarName + "' active=" + Bool(f.AvatarActive) + "/" + Bool(f.AvatarInHier) + " layer=" + f.AvatarLayer + " descriptor=" + (f.Desc ? "#" + f.DescId : "NONE") : "NONE")
			  .Append(" | renderers=").Append(f.Renderers).Append(" (skinned ").Append(f.Skinned).Append(", mesh ").Append(f.Mesh).Append(")")
			  .Append(" enabled=").Append(f.EnabledFlag).Append(" forceOff=").Append(f.ForceOff).Append(" goActive=").Append(f.GoActive)
			  .Append(" drawable=").Append(f.Drawable).Append(" isVisible=").Append(f.IsVisible).Append(" layers=[").Append(f.Layers).Append(']')
			  .Append(" | animator ").Append(f.Anim ? "#" + f.AnimId + " enabled=" + Bool(f.AnimEnabled) + " active=" + Bool(f.AnimActive) + " controller=" + Bool(f.AnimController) + " avatar=" + Bool(f.AnimAvatar) + " human=" + Bool(f.AnimHuman) : "NONE")
			  .Append(" | IK ").Append(f.IkCount).Append(" (").Append(f.IkEnabled).Append(" enabled)");
			if (f.Ik.Count > 0) { sb.Append(" ["); for (int i = 0; i < f.Ik.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(f.Ik[i]); } sb.Append(']'); }
			sb.Append(" | components root=").Append(f.RootCompCount).Append(" avatar=").Append(f.AvatarCompCount)
			  .Append(" | physbones=").Append(f.PhysBones)
			  .Append(" | audio ").Append(f.Audio).Append(" (enabled ").Append(f.AudioEnabled).Append(", playing ").Append(f.AudioPlaying).Append(")")
			  .Append(" | nameplate ").Append(f.Nameplate)
			  .Append(" | head=").Append(f.HasHead ? V(f.Head) : "unavailable")
			  .Append(" | apiAvatar=").Append(Or(f.ApiAvatarId))
			  .Append(" | ").Append(f.Moderation);
			if (s != null && f.Label != "T+0.00s")
				sb.Append(" | moved since last point: root ").Append(f.MovedRoot.ToString("0.00")).Append("m net ").Append(f.MovedNet.ToString("0.00")).Append("m head-vs-root ").Append(f.MovedHead.ToString("0.00")).Append('m');
			if (s != null) SLog(s, sb.ToString()); else Log(sb.ToString());
		}

		private static void LogFlags(Full f, Session s)
		{
			var keys = new List<string>(f.Flags.Keys);
			keys.Sort(StringComparer.Ordinal);
			var sb = new StringBuilder(1024);
			sb.Append(f.Label).Append(" flags (VRChat's own members, read by name, not interpreted): ");
			for (int i = 0; i < keys.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(keys[i]).Append('=').Append(f.Flags[keys[i]]); }
			if (keys.Count == 0) sb.Append("none readable");
			if (f.Floats.Count > 0)
			{
				var fk = new List<string>(f.Floats.Keys);
				fk.Sort(StringComparer.Ordinal);
				sb.Append(" || floats (dumped here, never diffed): ");
				for (int i = 0; i < fk.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(fk[i]).Append('=').Append(f.Floats[fk[i]]); }
			}
			if (s != null) SLog(s, sb.ToString()); else Log(sb.ToString());
		}

		// Only what CHANGED between two scheduled snapshots.
		private static void LogDiff(Session s, Full a, Full b)
		{
			if (a == null || b == null) return;
			var va = View(a); var vb = View(b);
			var keys = new SortedSet<string>(va.Keys, StringComparer.Ordinal);
			foreach (var k in vb.Keys) keys.Add(k);
			int n = 0;
			foreach (var k in keys)
			{
				va.TryGetValue(k, out string x); vb.TryGetValue(k, out string y);
				if (string.Equals(x, y, StringComparison.Ordinal)) continue;
				n++;
				SLog(s, "  " + b.Label + " changed: " + k + " " + (x ?? "<absent>") + " -> " + (y ?? "<absent>"));
				if (a.Flags.ContainsKey(k) || b.Flags.ContainsKey(k))   // VRChat's own members only
					s.FlagChanges[k] = (x ?? "<absent>") + " -> " + (y ?? "<absent>");
			}
			var removed = new List<long>(); var added = new List<long>();
			foreach (long c in a.AvatarComps) if (!b.AvatarComps.Contains(c)) removed.Add(c);
			foreach (long c in b.AvatarComps) if (!a.AvatarComps.Contains(c)) added.Add(c);
			if (removed.Count > 0) { n++; SLog(s, "  " + b.Label + " component types REMOVED from the avatar root: " + RenderClasses(removed)); }
			if (added.Count > 0) { n++; SLog(s, "  " + b.Label + " component types ADDED to the avatar root: " + RenderClasses(added)); }
			removed.Clear(); added.Clear();
			foreach (long c in a.RootComps) if (!b.RootComps.Contains(c)) removed.Add(c);
			foreach (long c in b.RootComps) if (!a.RootComps.Contains(c)) added.Add(c);
			if (removed.Count > 0) { n++; SLog(s, "  " + b.Label + " component types REMOVED from the player root: " + RenderClasses(removed)); }
			if (added.Count > 0) { n++; SLog(s, "  " + b.Label + " component types ADDED to the player root: " + RenderClasses(added)); }
			if (n == 0) SLog(s, "  " + b.Label + ": no change since " + a.Label);
		}

		// The comparable face of a snapshot: every state field except the positions (motion has its
		// own accumulators) so LogDiff can report exactly the fields that moved.
		private static Dictionary<string, string> View(Full f)
		{
			var d = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["root.exists"] = Bool(f.RootExists), ["root.instanceId"] = f.RootId.ToString(), ["root.activeSelf"] = Bool(f.RootActive), ["root.activeInHierarchy"] = Bool(f.RootInHier), ["root.layer"] = f.RootLayer.ToString(),
				["VRC.Player.exists"] = Bool(f.PlayerComp), ["VRC.Player.instanceId"] = f.PlayerCompId.ToString(), ["VRC.Player.enabled"] = f.PlayerCompEnabled,
				["VRCPlayer.exists"] = Bool(f.VrcPlayer), ["VRCPlayer.instanceId"] = f.VrcPlayerId.ToString(), ["VRCPlayer.enabled"] = f.VrcPlayerEnabled,
				["VRCAvatarManager.exists"] = Bool(f.Mgr), ["VRCAvatarManager.instanceId"] = f.MgrId.ToString(), ["VRCAvatarManager.enabled"] = f.MgrEnabled,
				["avatar.exists"] = Bool(f.AvatarExists), ["avatar.instanceId"] = f.AvatarId.ToString(), ["avatar.activeSelf"] = Bool(f.AvatarActive), ["avatar.activeInHierarchy"] = Bool(f.AvatarInHier), ["avatar.layer"] = f.AvatarLayer.ToString(), ["avatar.name"] = f.AvatarName,
				["avatar.descriptor"] = f.Desc ? "#" + f.DescId : "NONE",
				["renderers.count"] = f.Renderers.ToString(), ["renderers.skinned"] = f.Skinned.ToString(), ["renderers.mesh"] = f.Mesh.ToString(),
				["renderers.enabled"] = f.EnabledFlag.ToString(), ["renderers.forceRenderingOff"] = f.ForceOff.ToString(), ["renderers.goActive"] = f.GoActive.ToString(),
				["renderers.drawable"] = f.Drawable.ToString(), ["renderers.isVisible"] = f.IsVisible.ToString(), ["renderers.layers"] = f.Layers,
				["animator.exists"] = Bool(f.Anim), ["animator.instanceId"] = f.AnimId.ToString(), ["animator.enabled"] = Bool(f.AnimEnabled), ["animator.isActiveAndEnabled"] = Bool(f.AnimActive),
				["animator.controller"] = Bool(f.AnimController), ["animator.avatar"] = Bool(f.AnimAvatar), ["animator.isHuman"] = Bool(f.AnimHuman),
				["ik.count"] = f.IkCount.ToString(), ["ik.enabled"] = f.IkEnabled.ToString(), ["ik.list"] = string.Join(", ", f.Ik.ToArray()),
				["components.root"] = f.RootCompCount.ToString(), ["components.avatar"] = f.AvatarCompCount.ToString(),
				["physbones"] = f.PhysBones.ToString(),
				["audio.count"] = f.Audio.ToString(), ["audio.enabled"] = f.AudioEnabled.ToString(), ["audio.playing"] = f.AudioPlaying.ToString(),
				["nameplate"] = f.Nameplate, ["head.known"] = Bool(f.HasHead), ["apiAvatar"] = f.ApiAvatarId ?? "", ["moderation"] = f.Moderation,
			};
			foreach (var kv in f.Flags) d[kv.Key] = kv.Value;
			return d;
		}

		// ---------------------------------------------------------------- summary

		private void Summary(Session s, float now, string early)
		{
			Tracked t = s.P;
			Full a = s.First, z = s.Last;
			Light b1 = s.Before1 ?? s.Before2 ?? s.BeforeLast;
			var sb = new StringBuilder(2048);
			sb.Append("\n========== BLOCK DEBUG SUMMARY ==========\n");
			sb.Append("Player: ").Append(t.PlayerId).Append("  '").Append(t.Name).Append("'  userId=").Append(Or(t.UserId)).Append('\n');
			sb.Append("Local:  playerId=").Append(LocalPidText()).Append("  userId=").Append(Or(LocalUid())).Append('\n');
			sb.Append("Transition: ").Append(s.Kind).Append(" (").Append(s.From).Append(" -> ").Append(s.To).Append(") at T0=")
			  .Append(DateTime.Now.AddSeconds(-(now - s.T0)).ToString("HH:mm:ss.fff")).Append(", window T-2s .. T+").Append((now - s.T0).ToString("0.0")).Append("s")
			  .Append(early != null ? "  [ENDED EARLY: " + early + "]" : "").Append('\n');
			if (s.MassCount >= 2)
				sb.Append("NOTE: ").Append(s.MassCount).Append(" players hid within 1 s of this one — a mass event (instance change / avatar culling), not an individual hide.\n");
			bool nsfw = false, over = false;
			try { nsfw = ModConfig.NsfwFilter.Value; } catch { }
			try { over = ModConfig.HideAvatarOverBudget.Value; } catch { }
			if (nsfw || over)
				sb.Append("CONTAMINATION: our own modules were ON during the window: ").Append(nsfw ? "NsfwFilter " : "").Append(over ? "HideAvatarOverBudget " : "")
				  .Append("— they write renderer/avatar state themselves. Repeat with them off before trusting the conclusion.\n");

			sb.Append('\n').Append("Before (").Append(b1 != null ? "T" + (b1.T - s.T0).ToString("0.00") + "s" : "no pre-window sample").Append("):\n");
			if (b1 != null)
			{
				sb.Append("    Player object: ").Append(b1.RootExists ? "YES (activeSelf=" + Bool(b1.RootActive) + ", activeInHierarchy=" + Bool(b1.RootInHier) + ")" : "NO").Append('\n');
				sb.Append("    Avatar object: ").Append(b1.AvatarId != 0 ? "YES (#" + b1.AvatarId + ", active=" + Bool(b1.AvatarActive) + "/" + Bool(b1.AvatarInHier) + ")" : "NONE").Append('\n');
				sb.Append("    Renderers: ").Append(b1.Renderers).Append("  drawable: ").Append(b1.Drawable).Append("  enabled-flag: ").Append(b1.EnabledFlag).Append("  forceRenderingOff: ").Append(b1.ForceOff).Append('\n');
				sb.Append("    State: ").Append(b1.State).Append('\n');
			}
			sb.Append("    IK (at T0, not sampled in the pre-window): ").Append(a.IkCount == 0 ? "none found" : a.IkCount + " component(s), " + a.IkEnabled + " enabled").Append('\n');

			sb.Append('\n').Append("After (").Append(z.Label).Append("):\n");
			sb.Append("    Player object: ").Append(z.RootExists ? "YES (#" + z.RootId + ", activeSelf=" + Bool(z.RootActive) + ", activeInHierarchy=" + Bool(z.RootInHier) + ")" : "NO").Append('\n');
			sb.Append("    VRC.Player: ").Append(z.PlayerComp ? "YES #" + z.PlayerCompId + " " + z.PlayerCompEnabled : "NONE").Append("   VRCPlayer: ").Append(z.VrcPlayer ? "YES #" + z.VrcPlayerId + " " + z.VrcPlayerEnabled : "NONE")
			  .Append("   VRCAvatarManager: ").Append(z.Mgr ? "YES #" + z.MgrId + " " + z.MgrEnabled : "NONE").Append('\n');
			string avatarLine;
			if (!z.AvatarExists) avatarLine = s.AvatarIdAtT0 != 0 || (b1 != null && b1.AvatarId != 0) ? "NONE — DESTROYED (was #" + (s.AvatarIdAtT0 != 0 ? s.AvatarIdAtT0 : b1.AvatarId) + (s.AvatarDestroyedAt >= 0f ? ", gone at T+" + s.AvatarDestroyedAt.ToString("0.00") + "s" : "") + ")" : "NONE (there was none before either)";
			else if (b1 != null && b1.AvatarId != 0 && b1.AvatarId != z.AvatarId) avatarLine = "YES #" + z.AvatarId + " — REPLACED (was #" + b1.AvatarId + (s.AvatarNewAt >= 0f ? ", new instance at T+" + s.AvatarNewAt.ToString("0.00") + "s" : "") + ")";
			else avatarLine = "YES #" + z.AvatarId + " — same instance" + (s.AvatarDestroyCount > 0 ? " id, but destroyed/recreated " + s.AvatarDestroyCount + "x in the window" : "");
			sb.Append("    Avatar object: ").Append(avatarLine).Append('\n');
			if (z.AvatarExists) sb.Append("    Avatar GameObject active: self=").Append(Bool(z.AvatarActive)).Append(" hierarchy=").Append(Bool(z.AvatarInHier)).Append("  layer=").Append(z.AvatarLayer).Append("  descriptor=").Append(z.Desc ? "YES" : "NONE").Append('\n');
			sb.Append("    Renderers: ").Append(z.Renderers).Append("  (skinned ").Append(z.Skinned).Append(", mesh ").Append(z.Mesh).Append(")\n");
			sb.Append("    Enabled renderers: ").Append(z.EnabledFlag).Append("   forceRenderingOff: ").Append(z.ForceOff).Append("   GameObject active: ").Append(z.GoActive).Append("   drawable: ").Append(z.Drawable).Append("   isVisible: ").Append(z.IsVisible).Append('\n');
			sb.Append("    Animator: ").Append(z.Anim ? "YES #" + z.AnimId + " enabled=" + Bool(z.AnimEnabled) + " isActiveAndEnabled=" + Bool(z.AnimActive) + " controller=" + Bool(z.AnimController) + " human=" + Bool(z.AnimHuman) : "NONE").Append('\n');
			sb.Append("    IK: ").Append(z.IkCount == 0 ? "none found" : (z.IkEnabled > 0 ? "ACTIVE" : "INACTIVE") + " (" + z.IkEnabled + "/" + z.IkCount + " enabled: " + string.Join(", ", z.Ik.ToArray()) + ")").Append('\n');
			sb.Append("    Components: player root ").Append(a.RootCompCount).Append(" -> ").Append(z.RootCompCount).Append(", avatar root ").Append(a.AvatarCompCount).Append(" -> ").Append(z.AvatarCompCount).Append('\n');
			sb.Append("    Transform updates (root moved over the window): ").Append(s.TotalRoot > 0.02f ? "YES (" + s.TotalRoot.ToString("0.00") + " m)" : "NO (" + s.TotalRoot.ToString("0.000") + " m)").Append('\n');
			sb.Append("    Network position updates (VRCPlayerApi.GetPosition): ").Append(s.TotalNet > 0.02f ? "YES (" + s.TotalNet.ToString("0.00") + " m)" : "NO (" + s.TotalNet.ToString("0.000") + " m)").Append('\n');
			sb.Append("    Bone motion (head relative to root): ").Append(s.HaveHead ? (s.TotalHead > 0.02f ? "YES (" + s.TotalHead.ToString("0.00") + " m)" : "NO (" + s.TotalHead.ToString("0.000") + " m)") : "unavailable (no head bone position)").Append('\n');
			sb.Append("    Renderer state changes seen frame-by-frame: ").Append(s.Flips).Append(" over ").Append(s.FlipFrames).Append(" frame(s)").Append(s.DeadRenderers > 0 ? ", " + s.DeadRenderers + " renderer(s) destroyed" : "").Append('\n');
			sb.Append("    Nameplate: ").Append(z.Nameplate).Append('\n');
			sb.Append("    Audio sources: ").Append(z.Audio).Append(" (enabled ").Append(z.AudioEnabled).Append(", playing ").Append(z.AudioPlaying).Append(")\n");
			if (s.VisibleAgainAt >= 0f) sb.Append("    Drawn again at: T+").Append(s.VisibleAgainAt.ToString("0.00")).Append("s\n");
			if (s.HiddenAgainAt >= 0f) sb.Append("    Hidden again at: T+").Append(s.HiddenAgainAt.ToString("0.00")).Append("s\n");

			sb.Append('\n').Append("VRChat flags that changed inside the window (member names, uninterpreted):\n");
			if (s.FlagChanges.Count == 0) sb.Append("    none\n");
			else
			{
				var keys = new List<string>(s.FlagChanges.Keys); keys.Sort(StringComparer.Ordinal);
				int shown = 0;
				foreach (var k in keys) { if (shown++ >= 40) { sb.Append("    ... ").Append(keys.Count - 40).Append(" more\n"); break; } sb.Append("    ").Append(k).Append(": ").Append(s.FlagChanges[k]).Append('\n'); }
			}
			sb.Append("Moderation hints already known to the mod: ").Append(z.Moderation).Append('\n');
			sb.Append("ApiAvatar id (roster): T0=").Append(Or(a.ApiAvatarId)).Append("  now=").Append(Or(z.ApiAvatarId)).Append('\n');

			sb.Append('\n').Append("Photon events received in the window (NetworkLog, bulk codes filtered):\n");
			foreach (string line in NetworkEventsInWindow(s.T0, s.T0 - 2.5f, now)) sb.Append("    ").Append(line).Append('\n');

			CorrelateEvent33(s, z, b1, sb);

			sb.Append('\n').Append("Conclusion:\n");
			foreach (string line in Conclude(s, a, z, b1, early)) sb.Append("    ").Append(line).Append('\n');
			sb.Append("==========================================");
			Log(sb.ToString());
		}

		// EVENT 33 -> AVATAR TEARDOWN, correlated in time. READ-ONLY: reads NetworkLog's event-33
		// capture ring (arrival timestamps only) and this window's own avatar-lifecycle snapshots, and
		// reports whether the teardown followed an event 33 and by how long. Nothing is altered, dropped,
		// or replayed. The affected remote player is known from the AVATAR side (whose avatar was torn
		// down), never decoded from the event-33 payload.
		private static void CorrelateEvent33(Session s, Full z, Light b1, StringBuilder sb)
		{
			try
			{
				int prevAv = (b1 != null && b1.AvatarId != 0) ? b1.AvatarId : s.AvatarIdAtT0;
				bool destroyed = !z.AvatarExists && prevAv != 0;
				bool replacedEmpty = z.AvatarExists && prevAv != 0 && prevAv != z.AvatarId && z.Renderers == 0;
				bool emptiedSame = z.AvatarExists && z.Renderers == 0 && b1 != null && b1.Renderers > 0;
				bool teardown = destroyed || replacedEmpty || emptiedSame;

				sb.Append('\n').Append("EVENT 33 ↔ AVATAR TEARDOWN CORRELATION (read-only):\n");
				if (!teardown)
				{
					sb.Append("    this window was not an avatar teardown (no destroy / replace-to-empty), so there is nothing to correlate here.\n");
					return;
				}

				float teardownRel = s.AvatarNewAt >= 0f ? s.AvatarNewAt : (s.AvatarDestroyedAt >= 0f ? s.AvatarDestroyedAt : 0f);
				float teardownAbs = s.T0 + teardownRel;

				// Nearest event 33 at (or just before) the teardown, within a 2 s look-back.
				NetworkLogModule.Cap best = null; float bestDelta = 999f;
				try
				{
					foreach (var c in NetworkLogModule.CapturesSince(s.T0 - 2f))
					{
						float d = teardownAbs - c.Time;                 // >0 : event 33 came first
						if (d >= -0.30f && d <= 2f && Math.Abs(d) < Math.Abs(bestDelta)) { bestDelta = d; best = c; }
					}
				}
				catch { }

				_tearTotal++;
				sb.Append("    Affected remote player: ").Append(s.P.PlayerId).Append(" '").Append(s.P.Name).Append("'  (local playerId=").Append(LocalPidText()).Append(")\n");
				sb.Append("    Teardown: ").Append(destroyed ? "avatar DESTROYED, no new instance" : replacedEmpty ? "avatar REPLACED with an EMPTY instance" : "avatar EMPTIED in place").Append(" at T+").Append(teardownRel.ToString("0.00")).Append("s\n");
				sb.Append("    prev avatar #").Append(prevAv).Append(" -> ").Append(z.AvatarExists ? "new #" + z.AvatarId + " (" + z.Renderers + " renderers, skinned " + z.Skinned + ", descriptor " + (z.Desc ? "yes" : "NONE") + ", animator " + (z.Anim ? "present" : "NONE") + ")" : "NONE (destroyed)").Append('\n');
				sb.Append("    new instance empty? ").Append((z.AvatarExists && z.Renderers == 0 && !z.Desc) ? "YES" : (z.AvatarExists ? "no" : "n/a")).Append("   transform/IK after teardown: ").Append((s.TotalRoot > 0.02f || s.TotalNet > 0.02f) ? "STILL UPDATING (root " + s.TotalRoot.ToString("0.00") + " m, net " + s.TotalNet.ToString("0.00") + " m)" : "stopped").Append('\n');

				if (best != null)
				{
					_tearAfter33++; _tearDeltasMs.Add(bestDelta * 1000f);
					sb.Append("    EVENT 33: received at ").Append(best.Clock).Append(" (T+").Append((best.Time - s.T0).ToString("0.00")).Append("s), code ").Append(best.Code).Append(", actor ").Append(best.Sender).Append(", payload ").Append(best.Payload).Append('\n');
					sb.Append("    => Δ(event 33 → teardown) = ").Append((bestDelta * 1000f).ToString("0")).Append(" ms ").Append(bestDelta >= -0.05f ? "— event 33 PRECEDES the teardown" : "— within one frame of it").Append('\n');
					sb.Append("    SEQUENCE OBSERVED: event 33 received → VRChat processes it → avatar #").Append(prevAv).Append(" disappears → ").Append(z.AvatarExists ? "empty #" + z.AvatarId + " appears" : "no replacement").Append('\n');
				}
				else
				{
					sb.Append("    EVENT 33: NONE within 2 s before this teardown — so THIS teardown was not preceded by an event 33 (ordinary avatar load/unload or another cause).\n");
				}
				sb.Append("    session so far: ").Append(_tearAfter33).Append('/').Append(_tearTotal).Append(" teardown(s) preceded by an event 33 within 2 s");
				if (_tearDeltasMs.Count > 0) sb.Append(", median Δ ").Append(MedianMs()).Append(" ms");
				sb.Append(". If this ratio stays high, the event 33 → teardown link is consistent.\n");
			}
			catch (Exception e) { sb.Append("    correlation failed: ").Append(Unwrap.Describe(e)).Append('\n'); }
		}

		private static string MedianMs()
		{
			try
			{
				if (_tearDeltasMs.Count == 0) return "?";
				var d = new List<float>(_tearDeltasMs); d.Sort();
				return d[d.Count / 2].ToString("0");
			}
			catch { return "?"; }
		}

		// WINDOW-INDEPENDENT teardown detector, run every sample (~4 Hz per player). A block whose victim
		// never becomes "Visible" first (already blocked = stuck NoAvatar) produces no HIDE window, so the
		// summary-based correlation above never sees it. This watches each player's avatar SIGNATURE
		// (instance id + renderer count) between samples and, on a teardown, time-matches the nearest
		// event 33 directly. READ-ONLY. Skips players with an open window (the summary handles those).
		private void DetectTeardown(Tracked t, Light l, float now)
		{
			try
			{
				int avId = l.AvatarId;      // 0 = no live avatar root
				int rend = l.Renderers;     // total renderer components under the avatar
				if (!t.CorrInit) { t.CorrInit = true; t.CorrAvId = avId; t.CorrRend = rend; return; }

				bool hadAvatar = t.CorrAvId != 0 || t.CorrRend > 0;
				bool replaced = t.CorrAvId != 0 && avId != 0 && avId != t.CorrAvId;   // new instance
				bool destroyed = t.CorrAvId != 0 && avId == 0;                          // gone, no replacement
				bool emptied = avId != 0 && avId == t.CorrAvId && t.CorrRend > 0 && rend == 0;  // same id, renderers stripped
				bool teardown = hadAvatar && (replaced || destroyed || emptied);

				if (teardown && t.Session == null && now - t.CorrAt > 1f)
				{
					t.CorrAt = now;
					CorrelateDirect(t, t.CorrAvId, avId, t.CorrRend, rend, replaced ? "REPLACED" : destroyed ? "DESTROYED" : "EMPTIED", now);
				}
				t.CorrAvId = avId; t.CorrRend = rend;
			}
			catch { }
		}

		// One teardown, time-matched to the nearest event 33 (arrival timestamp only — never altered).
		private static void CorrelateDirect(Tracked t, int prevAv, int newAv, int prevRend, int newRend, string kind, float now)
		{
			NetworkLogModule.Cap best = null; float bestDelta = 999f;
			try
			{
				foreach (var c in NetworkLogModule.CapturesSince(now - 2.5f))
				{
					float d = now - c.Time;                 // >0 : event 33 came first
					if (d >= -0.30f && d <= 2.5f && Math.Abs(d) < Math.Abs(bestDelta)) { bestDelta = d; best = c; }
				}
			}
			catch { }

			_tearTotal++;
			var sb = new StringBuilder();
			sb.Append("[EV33-CORR] avatar teardown: player ").Append(t.PlayerId).Append(" '").Append(t.Name).Append("' — ").Append(kind)
			  .Append(" prev #").Append(prevAv).Append(" (").Append(prevRend).Append(" rend) -> ").Append(newAv == 0 ? "NONE (destroyed)" : "#" + newAv + " (" + newRend + " rend)")
			  .Append(newAv != 0 && newRend == 0 ? " [EMPTY shell]" : "");
			if (best != null)
			{
				_tearAfter33++; _tearDeltasMs.Add(bestDelta * 1000f);
				sb.Append(" | EVENT 33 at ").Append(best.Clock).Append(", Δ=").Append((bestDelta * 1000f).ToString("0")).Append(" ms (")
				  .Append(bestDelta >= -0.05f ? "event 33 PRECEDES the teardown" : "~same frame").Append("), payload ").Append(best.Payload);
			}
			else sb.Append(" | EVENT 33: none within 2.5 s before → this teardown is NOT event-33-driven (avatar reload/cull)");
			sb.Append(" | session ").Append(_tearAfter33).Append('/').Append(_tearTotal);
			if (_tearDeltasMs.Count > 0) sb.Append(", median Δ ").Append(MedianMs()).Append(" ms");
			Log(sb.ToString());
		}

		private static List<string> Conclude(Session s, Full a, Full z, Light b1, string early)
		{
			var c = new List<string>();
			bool hadAvatar = (b1 != null && b1.AvatarId != 0) || s.AvatarIdAtT0 != 0;
			bool drawnNow = z.RootExists && z.RootActive && z.RootInHier && z.AvatarExists && z.AvatarInHier && z.Drawable > 0;

			// A player who leaves has their root deactivated a moment before the object goes: that is
			// what "root activeSelf=false, then gone" is, and it must not be read as a hide.
			if (early != null && early.IndexOf("left", StringComparison.Ordinal) >= 0)
			{
				c.Add("The player LEFT the instance during the window (" + early + "). What was measured is VRChat tearing their objects down — root deactivated, then removed — not a hide. No A–F verdict.");
				return c;
			}
			if (s.MassCount >= 2)
				c.Add("MASS EVENT: " + s.MassCount + " players hid within the same second. Read the verdict below as 'what culling / an instance change does', not as a block.");

			if (!z.RootExists)
				c.Add("The remote Player object itself is gone (" + (early ?? "root destroyed") + "). Nothing was hidden: the player left, or VRChat tore the object down.");
			else if (s.Kind == "SHOW")
			{
				c.Add(drawnNow ? "The avatar is drawn again (" + z.Drawable + "/" + z.Renderers + " renderers drawable): the earlier hide was undone — an unblock, an unhide, or the avatar finishing a reload."
				               : "A SHOW was detected but the avatar is not drawn at the end of the window; read the per-point snapshots above.");
			}
			else if (!z.AvatarExists && hadAvatar)
			{
				c.Add("C) The avatar GameObject was DESTROYED/unloaded after the transition and no new instance existed by " + z.Label + ".");
				c.Add("   The Player object stayed (" + (z.PlayerComp ? "VRC.Player present" : "VRC.Player NOT found") + ", " + (z.VrcPlayer ? "VRCPlayer present" : "VRCPlayer NOT found") + ").");
			}
			else if (z.AvatarExists && b1 != null && b1.AvatarId != 0 && b1.AvatarId != z.AvatarId)
			{
				bool apiChanged = !string.IsNullOrEmpty(a.ApiAvatarId) && !string.IsNullOrEmpty(z.ApiAvatarId) && !string.Equals(a.ApiAvatarId, z.ApiAvatarId, StringComparison.OrdinalIgnoreCase);
				c.Add("The avatar was REPLACED by a new instance (#" + b1.AvatarId + " -> #" + z.AvatarId + ")." + (apiChanged ? " The ApiAvatar id changed too: this looks like an ordinary AVATAR CHANGE, not a hide." : " Same ApiAvatar id: VRChat re-instantiated the same avatar."));
				if (drawnNow) c.Add("   It is drawn at the end of the window (" + z.Drawable + " renderers drawable).");
			}
			else if (z.AvatarExists && (!z.AvatarActive || !z.AvatarInHier))
				c.Add("B') The avatar GameObject still exists but is DEACTIVATED (activeSelf=" + Bool(z.AvatarActive) + ", activeInHierarchy=" + Bool(z.AvatarInHier) + "); its " + z.AvatarCompCount + " root components and " + z.Renderers + " renderers are still there.");
			else if (z.AvatarExists && !z.RootActive)
				c.Add("A') The remote Player ROOT object was deactivated (activeSelf=false); the avatar under it is intact.");
			else if (z.AvatarExists && z.Renderers > 0 && z.Drawable == 0)
				c.Add("B) The avatar and its " + z.Renderers + " renderers still exist; NONE is drawable — enabled=false on " + (z.Renderers - z.EnabledFlag) + ", forceRenderingOff on " + z.ForceOff + ", inactive GameObject on " + (z.Renderers - z.GoActive) + ". Rendering was disabled locally, the objects were not removed.");
			else if (z.AvatarExists && z.Renderers == 0)
				c.Add("D) The avatar GameObject exists (" + z.AvatarCompCount + " components on its root) but its RENDERER components are gone" + (s.DeadRenderers > 0 ? " (" + s.DeadRenderers + " seen destroyed in the window)" : "") + ".");
			else if (drawnNow && s.Kind == "HIDE")
				c.Add("The HIDE was TRANSIENT: the avatar is drawn again at " + z.Label + (s.VisibleAgainAt >= 0f ? " (back at T+" + s.VisibleAgainAt.ToString("0.00") + "s)" : "") + ". A short reload or a one-frame hide, not a lasting one.");
			else
				c.Add("F) The end state matches none of A–E: root active=" + Bool(z.RootActive) + "/" + Bool(z.RootInHier) + ", avatar=" + (z.AvatarExists ? "#" + z.AvatarId : "none") + ", renderers=" + z.Renderers + " drawable=" + z.Drawable + ". Read the snapshots.");

			if (z.RootExists && !drawnNow)
			{
				if (s.TotalHead > 0.02f || s.TotalRoot > 0.02f)
					c.Add("E) IK/transforms KEPT UPDATING while hidden: root moved " + s.TotalRoot.ToString("0.00") + " m, head-vs-root " + s.TotalHead.ToString("0.00") + " m" + (z.IkEnabled > 0 ? ", " + z.IkEnabled + " IK component(s) still enabled" : "") + ".");
				else if (s.TotalNet > 0.02f)
					c.Add("Network position kept updating (" + s.TotalNet.ToString("0.00") + " m) but the local transform did NOT follow (" + s.TotalRoot.ToString("0.000") + " m): the object is frozen locally while data still arrives.");
				else
					c.Add("No motion at all in the window (root " + s.TotalRoot.ToString("0.000") + " m, net " + s.TotalNet.ToString("0.000") + " m) — either they stood still, or updates stopped. Repeat while they walk.");
				if (z.Anim) c.Add("Animator: " + (z.AnimEnabled ? "still enabled" : "DISABLED") + (z.AnimController ? ", controller still assigned" : ", controller REMOVED") + ".");
				if (s.Flips > 0)
					c.Add("Renderer states were re-written " + s.Flips + " time(s) over " + s.FlipFrames + " frame(s) after T0: " + (s.FlipFrames >= 10 ? "VRChat re-asserts the hidden state repeatedly (a fight, not a one-shot)." : "a one-shot (or a few) writes, not a per-frame re-assert."));
				else
					c.Add("No renderer flag changed after T0's snapshot: the hidden state was set once, not re-applied per frame.");
			}
			c.Add("Direction is NOT proven by any of this. It is equally what VRChat's avatar culling (Performance options), a Safety 'hide avatar', a block by them, or a block by you looks like from here — see the moderation hints and the Photon events above.");
			return c;
		}

		private static List<string> NetworkEventsInWindow(float t0, float from, float to)
		{
			var lines = new List<string>();
			try
			{
				bool on = false;
				try { on = ModConfig.NetworkLogEnabled.Value; } catch { }
				if (!on) { lines.Add("unavailable (NetworkLog/Enabled is off)"); return lines; }
				if (!NetworkLogModule.HookAttached) { lines.Add("unavailable (NetworkLog hook not attached: " + NetworkLogModule.HookInfo + ")"); return lines; }
				var snap = NetworkLogModule.Snapshot();
				var agg = new Dictionary<string, KeyValuePair<int, float>>(StringComparer.Ordinal);
				float oldest = float.MaxValue;
				foreach (var e in snap)
				{
					if (e == null) continue;
					if (e.Time < oldest) oldest = e.Time;
					if (e.Time < from || e.Time > to) continue;
					if (NetworkLogModule.IsBulk(e.Code)) continue;
					string key = "event " + e.Code + " from actor " + e.Sender;
					if (agg.TryGetValue(key, out var cur)) agg[key] = new KeyValuePair<int, float>(cur.Key + Math.Max(1, e.Repeats), Math.Min(cur.Value, e.Time));
					else agg[key] = new KeyValuePair<int, float>(Math.Max(1, e.Repeats), e.Time);
				}
				if (agg.Count == 0) lines.Add("none (no non-bulk Photon event landed between T-2.5s and the end of the window)");
				else
				{
					var rows = new List<KeyValuePair<string, KeyValuePair<int, float>>>(agg);
					rows.Sort((x, y) => x.Value.Value.CompareTo(y.Value.Value));
					foreach (var r in rows)
					{
						float d = r.Value.Value - t0;
						lines.Add(r.Key + " x" + r.Value.Key + ", first at T" + (d >= 0f ? "+" : "") + d.ToString("0.00") + "s");
					}
				}
				if (snap.Count >= NetworkLogModule.Capacity && oldest > from) lines.Add("(the NetworkLog ring wrapped: events before T" + (oldest - t0).ToString("0.00") + "s are lost)");
				lines.Add("(actor = Photon actor number, not the playerId; codes are unnamed on this build — bulk = above NetworkLog/BulkPerSecond)");

				// Event 33 payloads captured in the window — the dev's lead, decoded.
				try
				{
					var caps = NetworkLogModule.CapturesSince(from);
					int shown33 = 0;
					foreach (var c in caps)
					{
						if (c.Time > to) continue;
						shown33++;
						lines.Add("EVENT " + c.Code + " (moderation/instance control) at T" + (c.Time - t0 >= 0f ? "+" : "") + (c.Time - t0).ToString("0.00") + "s, actor " + c.Sender + ": " + c.Payload);
					}
					if (shown33 == 0) lines.Add("event 33 (moderation/instance control): NONE captured in the window — if this was a real block, the packet did not arrive in T-2.5s..T+" + (to - t0).ToString("0.0") + "s.");
				}
				catch { }
			}
			catch (Exception e) { lines.Add("unavailable (" + Unwrap.Describe(e) + ")"); }
			return lines;
		}

		// ---------------------------------------------------------------- resolution helpers

		private static Type _tPlayer, _tVrcPlayer, _tAvatarMgr, _tPlayerManager, _tModerationMgr;
		private static Il2CppSystem.Type _il2Player;
		private static readonly string[] IkTypeNames = { "IkController", "VRCVrIkController", "VRCFbbIkController" };
		private static Type[] _ikTypes; private static Il2CppSystem.Type[] _ikIl2; private static int _ikResolved;
		private static bool _typesResolved;

		private static void ResolveTypes()
		{
			if (_typesResolved) return;
			_typesResolved = true;
			try
			{
				Assembly asm = Assembly.Load("Assembly-CSharp");
				_tPlayer = asm.GetType("VRC.Player");
				_tVrcPlayer = asm.GetType("VRCPlayer");
				_tAvatarMgr = asm.GetType("VRCAvatarManager");
				_tPlayerManager = asm.GetType("PlayerManager");
				_tModerationMgr = asm.GetType("VRC.ModerationManager");
				try { if (_tPlayer != null) _il2Player = Il2CppType.From(_tPlayer); } catch { _il2Player = null; }
				SeedClassNames(asm);
				_ikTypes = new Type[IkTypeNames.Length];
				_ikIl2 = new Il2CppSystem.Type[IkTypeNames.Length];
				for (int i = 0; i < IkTypeNames.Length; i++)
				{
					try
					{
						_ikTypes[i] = asm.GetType(IkTypeNames[i]);
						if (_ikTypes[i] != null) { _ikIl2[i] = Il2CppType.From(_ikTypes[i]); _ikResolved++; }
					}
					catch { _ikIl2[i] = null; }
				}
			}
			catch (Exception e) { Log("type resolution failed: " + Unwrap.Describe(e)); }
		}

		// Il2CppObjectBase.TryCast<T> for a T known only at runtime — the generic Component wrapper
		// GetComponent hands back exposes none of the interop members reflection needs to see.
		private static object AsConcrete(object obj, Type concrete)
		{
			try
			{
				if (obj == null || concrete == null) return null;
				var m = typeof(Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(concrete);
				return m?.Invoke(obj, null);
			}
			catch { return null; }
		}

		// The child that carries the SDK descriptor, else the one named the way VRChat names avatar
		// instances (SelfHideModule's recipe). Read-only lookup.
		private static GameObject FindAvatarRoot(GameObject root)
		{
			try
			{
				var desc = root.GetComponentInChildren<VRCAvatarDescriptor>(true);
				if (desc != null) { var g = desc.gameObject; if (g != null) return g; }
			}
			catch { }
			try
			{
				var all = root.GetComponentsInChildren<Transform>(true);
				if (all != null)
					for (int i = 0; i < all.Length; i++)
					{
						var tr = all[i];
						if (tr == null) continue;
						string n;
						try { n = tr.name ?? ""; } catch { continue; }
						if (n.StartsWith("prefab-id-v1_avtr", StringComparison.OrdinalIgnoreCase) || n.IndexOf("avtr_", StringComparison.OrdinalIgnoreCase) >= 0 || n == "Avatar")
							return tr.gameObject;
					}
			}
			catch { }
			return null;
		}

		// COMPONENT NAMES ARE KEYED BY THE IL2CPP CLASS POINTER, never by the wrapper's managed type.
		// Il2CppInterop pools wrappers per object: the same component came back typed `Component` in
		// one GetComponents call and `VRCMotionState` in the next (after a typed field read had created
		// a better wrapper), so a name-based set diff reported a phantom "REMOVED …, ADDED
		// VRCMotionState" in the first session. The class pointer is stable for the process; the best
		// readable name seen for it (the real il2cpp name when it is not obfuscated, else the interop
		// type name a typed member revealed, else obf@<pointer>) is remembered and used for display.
		private static readonly Dictionary<IntPtr, string> ClassNames = new Dictionary<IntPtr, string>();

		private static IntPtr ClassOf(Il2CppObjectBase o)
		{
			try
			{
				if (o == null) return IntPtr.Zero;
				IntPtr p = o.Pointer;
				return p == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_object_get_class(p);
			}
			catch { return IntPtr.Zero; }
		}

		private static bool Readable(string n)
		{
			if (string.IsNullOrEmpty(n) || n.Length > 40 || n.IndexOf("Unique", StringComparison.Ordinal) >= 0) return false;
			for (int i = 0; i < n.Length; i++) if (n[i] > 127) return false;
			return true;
		}

		private static string NameForClass(IntPtr klass)
		{
			if (klass == IntPtr.Zero) return "?";
			if (ClassNames.TryGetValue(klass, out string n)) return n;
			string native = null;
			try { native = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(klass)); } catch { }
			n = Readable(native) ? native : "obf@" + klass.ToString("X");
			ClassNames[klass] = n;
			return n;
		}

		// A readable managed/interop name for a class whose il2cpp name is obfuscated.
		private static void Learn(IntPtr klass, string managed)
		{
			if (klass == IntPtr.Zero || !Readable(managed)) return;
			if (managed == "Component" || managed == "Behaviour" || managed == "MonoBehaviour" || managed == "Object" || managed == "Il2CppObjectBase") return;
			if (!ClassNames.TryGetValue(klass, out string cur) || cur.StartsWith("obf@", StringComparison.Ordinal)) ClassNames[klass] = managed;
		}

		private static string NameOf(Il2CppObjectBase c)
		{
			IntPtr klass = ClassOf(c);
			if (klass == IntPtr.Zero) { try { return c.GetType().Name; } catch { return "?"; } }
			string n = NameForClass(klass);
			if (n.StartsWith("obf@", StringComparison.Ordinal))
			{
				string m = null;
				try { m = c.GetType().Name; } catch { }
				Learn(klass, m);
				n = NameForClass(klass);
			}
			return n;
		}

		// Interop types whose il2cpp names are obfuscated on this build but which the mod knows by name:
		// their class pointers are taught to the cache up front, so the T0 snapshot already names them.
		private static readonly string[] SeedTypeNames =
		{
			"VRC.Player", "VRCPlayer", "VRCAvatarManager", "IkController", "VRCVrIkController", "VRCFbbIkController",
			"VRCMotionState", "VRC_AnimationController", "VRCGestureController", "PlayerNameplatePositioner",
			"AnimatorControllerManager", "EmotePlayer", "VRC.Networking.FlatBufferNetworkSerializer", "USpeaker",
		};

		private static void SeedClassNames(Assembly asm)
		{
			foreach (string name in SeedTypeNames)
			{
				try
				{
					Type t = asm.GetType(name);
					if (t == null) continue;
					Type store = typeof(Il2CppClassPointerStore<>).MakeGenericType(t);
					IntPtr k = IntPtr.Zero;
					var fi = store.GetField("NativeClassPtr", BindingFlags.Static | BindingFlags.Public);
					if (fi != null) k = (IntPtr)fi.GetValue(null);
					else
					{
						var pi = store.GetProperty("NativeClassPtr", BindingFlags.Static | BindingFlags.Public);
						if (pi != null) k = (IntPtr)pi.GetValue(null, null);
					}
					if (k != IntPtr.Zero) ClassNames[k] = t.Name;
				}
				catch { }
			}
		}

		private static bool IsIkName(string n)
		{
			if (string.IsNullOrEmpty(n)) return false;
			return n.IndexOf("IK", StringComparison.Ordinal) >= 0 || n.IndexOf("Ik", StringComparison.Ordinal) >= 0
				|| n.IndexOf("Grounder", StringComparison.Ordinal) >= 0 || n.IndexOf("PoseLocalUpdate", StringComparison.Ordinal) >= 0
				|| n.IndexOf("Tracking", StringComparison.Ordinal) >= 0 || n.IndexOf("Calibrat", StringComparison.Ordinal) >= 0;
		}

		private static string Who(Tracked t) => "Player=" + t.PlayerId + " '" + t.Name + "'";
		private static string Or(string s) => string.IsNullOrEmpty(s) ? "unavailable" : s;
		private static string Bool(bool b) => b ? "true" : "false";
		private static string V(Vector3 v) => "(" + v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00") + ")";

		private static string LocalPidText()
		{
			try { var a = PlayerRef.LocalApi(); return a != null ? a.playerId.ToString() : "unavailable"; }
			catch { return "unavailable"; }
		}

		private static string LocalUid()
		{
			try { return VaTagsModule.LocalUserId() ?? ""; }
			catch { return ""; }
		}
	}
}
