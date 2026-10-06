using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Avatars.Components;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// EVENT 33 → PLAYER REPRESENTATION TRACE. READ-ONLY, like BlockObserverModule next to it.
	//
	// THE QUESTION IT ANSWERS: when Photon event 33 (moderation) arrives, WHICH LAYER of a remote
	// player's local representation changes, in WHAT ORDER, HOW LONG after the event, and FROM WHERE
	// in VRChat's code — as opposed to "the avatar went away". The layers, sampled separately:
	//   STATE-MODERATION  VRC.ModerationManager (per-player tuple / sets / lists, by name)
	//   STATE-AVATARMGR   VRCAvatarManager: visibility enum, avatar-kind enum (Custom/Blocked/...), flags
	//   STATE-PLAYER      VRC.Player and VRCPlayer flags
	//   ROOT / AVATAR / RENDERERS / ANIMATOR / IK / NAMEPLATE / PLAYER-UI / AUDIO / TRANSFORM
	// Two instruments:
	//   1. PER-FRAME SAMPLING during a window of BlockDebug/TraceSeconds after each event 33, of every
	//      remote player, diffed frame to frame, so the first change of each layer gets a millisecond
	//      timestamp relative to the event. A full snapshot is also taken INSIDE the OnEvent handler,
	//      before and after VRChat's own code runs, so a synchronous change is told apart from one
	//      made frames later.
	//   2. HARMONY PREFIXES that only OBSERVE: on Unity's Object.Destroy / GameObject.SetActive /
	//      Renderer.enabled / Renderer.forceRenderingOff / Behaviour.enabled / Object.Instantiate, and
	//      on a curated list of VRCAvatarManager, VRC.ModerationManager, VRCNetworkingClient, VRC.Player,
	//      VRCPlayer and PlayerNameplate methods. Each hit is stamped, attributed to a player by object
	//      pointer, and comes with the NATIVE CALL STACK symbolized against the interop (Core/NativeStack)
	//      — that is what names the VRChat method that performed each change. No prefix ever returns
	//      false, alters an argument or a result, or writes to any game object.
	// Nothing about the network or the moderation state is modified. BlockDebug/TraceHooks=false keeps
	// the sampling and drops every Harmony patch.
	public class Event33TraceModule : IModule
	{
		public override string Name => "Ev33Trace";

		private static readonly string Tag = "[EV33-TRACE] ";
		private static readonly string HealthId = "BlockDebug/Trace";
		private const int MaxRend = 512;
		private const int MaxLinesPerPlayer = 140;
		private const int MaxHookLines = 400;
		private const int MutePerSecond = 25;
		private const float RefreshSec = 0.5f;
		private const float IdleSampleSec = 0.5f;
		private const int FullEveryFrameMaxPlayers = 12;

		// ---------------------------------------------------------------- tracked players

		private sealed class TP
		{
			public int Pid; public string Name = "?", Uid = "";
			public VRCPlayerApi Api;
			public GameObject Root; public int RootId; public IntPtr RootPtr;
			public VRC.Player Player; public VRCPlayer VP; public VRCAvatarManager Mgr;
			public GameObject Avatar; public int AvatarId; public string AvatarName = ""; public IntPtr AvatarPtr;
			public Animator Anim;
			public readonly List<Renderer> Rend = new List<Renderer>(); public float RendAt;
			public GameObject NpContainer; public PlayerNameplate NP; public GameObject NpGo; public Behaviour Positioner; public GameObject Bubble;
			public readonly List<CanvasGroup> NpGroups = new List<CanvasGroup>();
			public readonly List<GameObject> NpChildren = new List<GameObject>(); public float NpAt; public bool NpPathLogged;
			public readonly List<Behaviour> Ik = new List<Behaviour>();
			public readonly List<AudioSource> Audio = new List<AudioSource>(); public Behaviour USpeaker;
			public Dictionary<string, string> Last;
			public float LastFullAt;
			// per-window
			public bool Hot; public int Lines; public readonly Dictionary<string, double> First = new Dictionary<string, double>(StringComparer.Ordinal);
			public readonly List<string> Seq = new List<string>();
			public readonly Dictionary<string, KeyValuePair<string, string>> Changed = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
			public bool HasPos, HasNet, HasHead; public Vector3 LastPos, LastNet, LastHeadRel; public float MovedRoot, MovedNet, MovedHead;
			public string AvatarAtStart = "";
		}

		private readonly Dictionary<int, TP> _tp = new Dictionary<int, TP>();
		private static Event33TraceModule _inst;
		private static volatile bool _armed;
		private static int _mainThread;
		private static bool _saidGated;
		// field_* members are read only when FieldOffsetFix has verified the FieldInfo offset slot (an
		// unrepaired offset is an unmapped read = process death); prop_* members are plain calls.
		private static bool _fields;
		private bool _wasOn;
		// Last asserted state of BlockDebugTraceHooks; null = not asserted yet, so the next pass
		// installs or removes to match. Lets that switch act on both edges instead of once.
		private bool? _lastHooks;
		private float _lastRefresh, _lastIdle, _lastHealth, _capDrain, _lastMuteReport;
		private int _cursor;

		// Object pointer → player / layer, rebuilt on every refresh and swapped in whole (hooks may
		// read them from another thread; a reference swap is atomic, in-place mutation is not).
		private static Dictionary<IntPtr, int> _ptrOwner = new Dictionary<IntPtr, int>();
		private static Dictionary<IntPtr, string> _ptrLayer = new Dictionary<IntPtr, string>();
		private static Dictionary<int, int> _rootOwner = new Dictionary<int, int>();
		private static Dictionary<int, string> _names = new Dictionary<int, string>();
		private static Dictionary<int, string> _prefabSlots = new Dictionary<int, string>();

		// ---------------------------------------------------------------- window

		private sealed class HookRec { public int Pid; public string Layer; public double Ms; public string Text; public bool Sync; }

		private sealed class Window
		{
			public int Seq; public long T0Ticks; public float T0Rt, EndRt; public string Clock, Payload; public int Sender; public double HandlerMs;
			public readonly List<HookRec> Hooks = new List<HookRec>(); public int HookLines, Suppressed; public bool Ended;
			public List<string> SyncChanges; public int Events = 1; public string Extra = "";
		}
		private static Window _w;
		private static readonly object WGate = new object();
		private static long _lastEvTicks; private static int _lastEvSeq;

		// Snapshots taken inside the OnEvent handler (prefix / postfix), main thread only.
		private static Dictionary<int, Dictionary<string, string>> _pre, _post;
		private static int _preSeq, _postSeq; private static List<string> _sync;

		// ---------------------------------------------------------------- lifecycle

		public override void OnInitialize()
		{
			_inst = this;
			NetworkLogModule.WatchedEnter = OnWatchedEnter;
			NetworkLogModule.WatchedExit = OnWatchedExit;
		}

		public override void OnLateUpdate()
		{
			try
			{
				// The Unity main thread is the one running this callback — NOT the one that ran
				// OnInitialize (the chainloader's). 3.9.78 took the id there, every "main thread" guard in the
				// hooks and in the handler snapshots then refused, and the run logged no hook at all.
				if (_mainThread == 0) _mainThread = Environment.CurrentManagedThreadId;
				bool on = false;
				try { on = ModConfig.BlockDebugEnabled != null && ModConfig.BlockDebugEnabled.Value; } catch { }
				if (!on)
				{
					if (_wasOn) { _wasOn = false; _armed = false; _lastHooks = null; Reset("switched off"); RemoveHooks(); FeatureHealth.Idle(HealthId, "off"); }

					// SAY SO WHEN THE SUB-SWITCHES ARE ON AND THE MASTER IS NOT.
					//
					// TraceHooks and DecodeEvent33 are options OF this module, so turning them on while
					// BlockDebug/Enabled stays off does exactly nothing -- silently. The owner switched
					// both on, entered an instance, got not one [EV33-TRACE] line, and reasonably
					// concluded the feature was broken. A module that is gated off owes the log a reason.
					try
					{
						if (!_saidGated)
						{
							bool subs = (ModConfig.BlockDebugTraceHooks != null && ModConfig.BlockDebugTraceHooks.Value)
							         || (ModConfig.BlockDebugDecodeEvent33 != null && ModConfig.BlockDebugDecodeEvent33.Value);
							if (subs)
							{
								_saidGated = true;
								VRChatArchiveModPlugin.Logger.LogWarning(
									"[EV33-TRACE] TraceHooks/DecodeEvent33 sont ACTIFS mais BlockDebug/Enabled est FALSE — "
									+ "rien ne sera trace. C'est l'interrupteur maitre : active BlockDebug/Enabled pour voir "
									+ "les event 33 (qui t'a bloque) en entrant dans une instance.");
							}
						}
					}
					catch { }
					return;
				}
				_saidGated = false;   // re-armed: a later switch-off must be able to say it again
				float now = VaClock.Now;
				if (!_wasOn)
				{
					_wasOn = true;
					try { NetworkLogModule.WatchCode(33); } catch { }
					Announce();
					_armed = true;
					_capDrain = now;
					_lastHooks = null;   // force the hook state to be asserted below on this frame
				}

				// THE HOOKS SWITCH IS ITS OWN TOGGLE, WATCHED EVERY PASS (2026-09-13).
				//
				// BlockDebugTraceHooks used to be sampled exactly once, inside the block above — i.e.
				// only on a BlockDebugEnabled OFF->ON edge. Turning the hooks off afterwards left them
				// patched into VRChat for the rest of the session, and turning them on never installed
				// anything: a switch that did nothing in either direction unless you also cycled the
				// parent. Comparing against the last asserted value makes both edges act immediately,
				// and costs one bool compare per pass when nothing changes.
				bool wantHooks = true;
				try { wantHooks = ModConfig.BlockDebugTraceHooks.Value; } catch { }
				if (_lastHooks != wantHooks)
				{
					_lastHooks = wantHooks;
					if (wantHooks) EnsureHooks();
					else RemoveHooks();
				}

				// 1. Roster / object registry.
				if (now - _lastRefresh >= RefreshSec || _tp.Count == 0) { _lastRefresh = now; Refresh(now); }

				// 2. New event 33? Open (or extend) the window.
				var caps = NetworkLogModule.CapturesSince(_capDrain);
				_capDrain = now;
				for (int i = 0; i < caps.Count; i++) OnCapture(caps[i], now);

				// 3. Aggregated renderer/behaviour flips from the hooks, flushed once per frame.
				FlushAggregates();

				// 4. Sampling: every frame inside a window, idle rate outside (keeps "previous" fresh).
				if (_w != null && !_w.Ended)
				{
					SampleAll(now, true, true);
					if (now >= _w.EndRt) Finish(now);
				}
				else if (now - _lastIdle >= IdleSampleSec)
				{
					_lastIdle = now;
					SampleAll(now, true, false);
				}

				if (now - _lastHealth >= 1f)
				{
					_lastHealth = now;
					FeatureHealth.Ok(HealthId, "on — " + _tp.Count + " remote player(s), " + (_w != null && !_w.Ended ? "window open (event 33 #" + _w.Seq + ")" : "waiting for event 33")
						+ ", hooks " + _hookOk + " ok/" + _hookFail + " failed, stack table " + NativeStack.Status);
				}
			}
			catch (Exception e) { Log("update threw: " + Unwrap.Describe(e)); }
		}

		public override void OnSceneLoaded(int buildIndex) { if (_wasOn) Reset("world changed"); }
		public override void OnShutdown() { _armed = false; RemoveHooks(); }

		private void Announce()
		{
			ResolveTypes();
			NativeStack.Build(StackTypes());
			bool verified = false; float secs = 3.5f; bool decode = true, hooks = true;
			try { verified = FieldOffsetFix.Verified; } catch { }
			_fields = verified;
			try { secs = ModConfig.BlockDebugTraceSeconds.Value; } catch { }
			try { decode = ModConfig.BlockDebugDecodeEvent33.Value; } catch { }
			try { hooks = ModConfig.BlockDebugTraceHooks.Value; } catch { }
			Log("trace ON (mod " + PluginInfo.Version + ") — READ-ONLY. Layers sampled every frame for " + secs.ToString("0.0") + " s after each event 33; hooks=" + (hooks ? "on" : "OFF")
				+ "; payload decode=" + (decode ? "on" : "off") + "; FieldOffsetFix.Verified=" + verified + "; stack table: " + NativeStack.Status);
			Log("legend: '+N ms' = milliseconds after the LAST event 33 received; fN = Unity frame; [LAYER] = which part of the player's representation; '← ...' = native callers (VRChat's own methods, interop names)");
		}

		private void Reset(string why)
		{
			if (_w != null && !_w.Ended) Log("window for event 33 #" + _w.Seq + " dropped (" + why + ")");
			_w = null;
			_tp.Clear();
			_ptrOwner = new Dictionary<IntPtr, int>(); _ptrLayer = new Dictionary<IntPtr, string>(); _rootOwner = new Dictionary<int, int>(); _names = new Dictionary<int, string>();
			_pre = null; _post = null; _sync = null;
		}

		// ---------------------------------------------------------------- roster & object registry

		private void Refresh(float now)
		{
			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return;
			int count; try { count = players.Count; } catch { return; }
			var present = new HashSet<int>();
			for (int i = 0; i < count; i++)
			{
				VRCPlayerApi api; try { api = players[i]; } catch { continue; }
				if (api == null) continue;
				bool local; try { local = api.isLocal; } catch { continue; }
				if (local) continue;
				int pid; try { pid = api.playerId; } catch { continue; }
				present.Add(pid);
				TP t;
				if (!_tp.TryGetValue(pid, out t))
				{
					t = new TP { Pid = pid };
					try { if (NativeGuard.Alive(api)) t.Name = api.displayName ?? "?"; } catch { }
					_tp[pid] = t;
				}
				t.Api = api;
				GameObject root = null; try { root = api.gameObject; } catch { }
				if (root == null) continue;
				int rid = 0; try { rid = root.GetInstanceID(); } catch { }
				if (t.Root is null || t.RootId != rid || t.Root == null) Acquire(t, root, rid);
				RefreshAvatar(t, now, false);
				if (now - t.NpAt >= 2f) RefreshNameplate(t, now);
			}
			List<int> gone = null;
			foreach (var kv in _tp) if (!present.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
			if (gone != null) foreach (int pid in gone) { Log("Player=" + pid + " '" + _tp[pid].Name + "' left the instance — no longer tracked."); _tp.Remove(pid); }
			RebuildRegistry();
		}

		private void Acquire(TP t, GameObject root, int rid)
		{
			t.Root = root; t.RootId = rid; t.RootPtr = Ptr(root);
			t.Player = null; t.VP = null; t.Mgr = null; t.Avatar = null; t.AvatarId = 0; t.Rend.Clear(); t.Anim = null; t.Ik.Clear(); t.Audio.Clear(); t.USpeaker = null; t.NpAt = 0f;
			try { var p = root.GetComponent<VRC.Player>(); if (p != null && NativeGuard.Alive(p)) t.Player = p; } catch { }
			if (t.Player != null)
			{
				if (_fields) { try { var vp = t.Player._vrcplayer; if (vp != null && NativeGuard.Alive(vp)) t.VP = vp; } catch { } }
				if (t.VP == null) { try { var vp = t.Player.prop_VRCPlayer_0; if (vp != null && NativeGuard.Alive(vp)) t.VP = vp; } catch { } }
				try { string uid = FewTagsModule.UserIdOf(t.Player); if (!string.IsNullOrEmpty(uid)) t.Uid = uid; } catch { }
				if (_fields) { try { var us = t.Player._USpeaker; if (us != null && NativeGuard.Alive(us)) t.USpeaker = us.TryCast<Behaviour>(); } catch { } }
				if (t.USpeaker == null) { try { var us = t.Player.prop_MonoBehaviourPublicStAcObSiInSiBoInObAuUnique_0; if (us != null && NativeGuard.Alive(us)) t.USpeaker = us.TryCast<Behaviour>(); } catch { } }
			}
			if (t.VP != null)
			{
				if (_fields) { try { var m = t.VP.field_Private_VRCAvatarManager_0; if (m != null && NativeGuard.Alive(m)) t.Mgr = m; } catch { } }
				if (t.Mgr == null) { try { var m = t.VP.prop_VRCAvatarManager_0; if (m != null && NativeGuard.Alive(m)) t.Mgr = m; } catch { } }
			}
			// Non-generic lookups with Il2CppType.Of: the generic GetComponent<T> came back null for these
			// interop types in 3.9.78 ("IK 0/3"), the same way FindObjectsOfType<T> does on this build.
			AddBehaviour(t.Ik, root, Il2CppType.Of<IkController>(), "IkController");
			AddBehaviour(t.Ik, root, Il2CppType.Of<VRCVrIkController>(), "VRCVrIkController");
			AddBehaviour(t.Ik, root, Il2CppType.Of<VRCFbbIkController>(), "VRCFbbIkController");
			if (t.Mgr != null && _prefabSlots.Count == 0) LogPrefabs(t.Mgr);
			Log("tracking Player=" + t.Pid + " '" + t.Name + "' userId=" + Or(t.Uid) + " root #" + rid + " | VRC.Player " + (t.Player != null ? "yes" : "NO") + " | VRCPlayer " + (t.VP != null ? "yes" : "NO")
				+ " | VRCAvatarManager " + (t.Mgr != null ? "yes" : "NO") + " | IK " + t.Ik.Count + "/3 | USpeaker " + (t.USpeaker != null ? "yes" : "no"));
		}

		private static void RefreshAvatar(TP t, float now, bool force)
		{
			GameObject go = null;
			if (_fields) { try { if (t.VP != null) go = t.VP.field_Internal_GameObject_0; } catch { go = null; } }
			if (go == null) { try { if (t.Mgr != null) go = t.Mgr.prop_GameObject_0; } catch { go = null; } }
			if (go == null && _fields) { try { if (t.Mgr != null) go = t.Mgr.field_Private_GameObject_0; } catch { go = null; } }
			if (go == null && t.Root != null) go = FindAvatarRoot(t.Root);
			int id = 0; try { if (go != null) id = go.GetInstanceID(); } catch { go = null; }
			bool changed = id != t.AvatarId || (t.Avatar is null) != (go is null);
			if (!changed && !force && now - t.RendAt < 5f) return;
			t.Avatar = go; t.AvatarId = id; t.AvatarPtr = go != null ? Ptr(go) : IntPtr.Zero;
			try { t.AvatarName = go != null ? (go.name ?? "") : ""; } catch { t.AvatarName = "?"; }
			t.Rend.Clear(); t.RendAt = now; t.Anim = null; t.Audio.Clear();
			GameObject scope = go ?? t.Root;
			if (scope == null) return;
			try
			{
				var rs = scope.GetComponentsInChildren<Renderer>(true);
				if (rs != null) for (int i = 0; i < rs.Length && i < MaxRend; i++) if (rs[i] != null) t.Rend.Add(rs[i]);
			}
			catch { }
			// A renderer-less avatar instance is the interesting one (Blocked / invisible): describe it once.
			if (changed && go != null && t.Rend.Count == 0 && _anatomyDone.Count < 64 && _anatomyDone.Add(id))
				Log("[AVATAR] Player=" + t.Pid + " '" + t.Name + "' renderer-less avatar instance #" + id + " '" + t.AvatarName + "': " + Anatomy(go, 14));
			if (_fields) { try { if (t.VP != null) t.Anim = t.VP.field_Internal_Animator_0; } catch { t.Anim = null; } }
			if (t.Anim == null) { try { t.Anim = scope.GetComponentInChildren<Animator>(true); } catch { } }
			try
			{
				var au = t.Root.GetComponentsInChildren<AudioSource>(true);
				if (au != null) for (int i = 0; i < au.Length && i < 8; i++) if (au[i] != null) t.Audio.Add(au[i]);
			}
			catch { }
		}

		private static void RefreshNameplate(TP t, float now)
		{
			t.NpAt = now;
			GameObject c = null;
			if (_fields) { try { if (t.VP != null) { var g = t.VP.field_Public_GameObject_0; if (g != null && g.name == "NameplateContainer") c = g; } } catch { } }
			if (c == null && t.Player != null) { try { c = FewTagsModule.FindNameplateContainer(t.Player); } catch { } }
			t.NpContainer = c; t.NP = null; t.NpGo = null; t.NpGroups.Clear(); t.NpChildren.Clear(); t.Positioner = null; t.Bubble = null;
			if (_fields)
			{
				try { if (t.VP != null) { var p = t.VP.field_Public_PlayerNameplatePositioner_0; if (p != null) t.Positioner = p.TryCast<Behaviour>(); } } catch { }
				try { if (t.VP != null) { var b = t.VP.field_Public_ChatBubbleDisplay_0; if (b != null) t.Bubble = b.gameObject; } } catch { }
			}
			if (c == null) return;
			try { var comp = c.GetComponentInChildrenSafe(Il2CppType.Of<PlayerNameplate>(), true); if (comp != null) t.NP = comp.TryCast<PlayerNameplate>(); } catch { }
			if (t.NP != null)
			{
				try { t.NpGo = t.NP.gameObject; } catch { }
				if (_fields)
				{
					try { var g = t.NP.field_Public_CanvasGroup_0; if (g != null) t.NpGroups.Add(g); } catch { }
					try { var g = t.NP.field_Public_CanvasGroup_1; if (g != null) t.NpGroups.Add(g); } catch { }
					try { var g = t.NP.field_Public_CanvasGroup_2; if (g != null) t.NpGroups.Add(g); } catch { }
				}
			}
			if (t.NpGroups.Count == 0)
			{
				try
				{
					var gs = c.GetComponentsInChildrenSafe(Il2CppType.Of<CanvasGroup>(), true);
					if (gs != null) for (int i = 0; i < gs.Length && t.NpGroups.Count < 3; i++) { var g = gs[i] != null ? gs[i].TryCast<CanvasGroup>() : null; if (g != null) t.NpGroups.Add(g); }
				}
				catch { }
			}
			try
			{
				var trs = c.GetComponentsInChildren<Transform>(true);
				if (trs != null) for (int i = 0; i < trs.Length && i < 96; i++) { var tr = trs[i]; if (tr != null && tr.gameObject != null) t.NpChildren.Add(tr.gameObject); }
			}
			catch { }
			if (!t.NpPathLogged)
			{
				t.NpPathLogged = true;
				if (_fields && !_prefabAnatomyLogged && t.Mgr != null) LogPrefabAnatomy(t.Mgr);
				var sb = new StringBuilder(); int depth = 0; bool underRoot = false;
				try
				{
					for (Transform tr = c.transform; tr != null && depth < 8; tr = tr.parent, depth++)
					{
						if (depth > 0) sb.Append(" ← ");
						sb.Append(tr.name);
						if (t.Root != null && tr.gameObject.GetInstanceID() == t.RootId) underRoot = true;
					}
				}
				catch { }
				Log("[NAMEPLATE] Player=" + t.Pid + " '" + t.Name + "' container #" + Id(c) + " path: " + sb + " | under the player's root object: " + (underRoot ? "YES (shares its lifecycle)" : "NO (own hierarchy)")
					+ " | PlayerNameplate " + (t.NP != null ? "#" + Id(t.NP) + " on '" + Nm(t.NpGo) + "'" : "NOT FOUND") + " | CanvasGroups " + t.NpGroups.Count + " | positioner " + (t.Positioner != null ? "yes" : "no") + " | chat bubble " + (t.Bubble != null ? "yes" : "no"));
			}
		}

		private void RebuildRegistry()
		{
			var po = new Dictionary<IntPtr, int>(); var pl = new Dictionary<IntPtr, string>(); var ro = new Dictionary<int, int>(); var nm = new Dictionary<int, string>();
			foreach (var t in _tp.Values)
			{
				nm[t.Pid] = t.Name;
				if (t.RootId != 0) ro[t.RootId] = t.Pid;
				Reg(po, pl, t.RootPtr, t.Pid, "ROOT");
				Reg(po, pl, t.AvatarPtr, t.Pid, "AVATAR");
				Reg(po, pl, Ptr(t.Player), t.Pid, "STATE-PLAYER"); Reg(po, pl, Ptr(t.VP), t.Pid, "STATE-PLAYER"); Reg(po, pl, Ptr(t.Mgr), t.Pid, "STATE-AVATARMGR");
				Reg(po, pl, Ptr(t.Anim), t.Pid, "ANIMATOR");
				Reg(po, pl, Ptr(t.NpContainer), t.Pid, "NAMEPLATE"); Reg(po, pl, Ptr(t.NP), t.Pid, "NAMEPLATE"); Reg(po, pl, Ptr(t.NpGo), t.Pid, "NAMEPLATE"); Reg(po, pl, Ptr(t.Positioner), t.Pid, "NAMEPLATE");
				Reg(po, pl, Ptr(t.Bubble), t.Pid, "PLAYER-UI"); Reg(po, pl, Ptr(t.USpeaker), t.Pid, "AUDIO");
				foreach (var g in t.NpChildren) Reg(po, pl, Ptr(g), t.Pid, "PLAYER-UI");
				foreach (var b in t.Ik) Reg(po, pl, Ptr(b), t.Pid, "IK");
				foreach (var a in t.Audio) Reg(po, pl, Ptr(a), t.Pid, "AUDIO");
				foreach (var r in t.Rend) Reg(po, pl, Ptr(r), t.Pid, "RENDERERS");
			}
			_ptrOwner = po; _ptrLayer = pl; _rootOwner = ro; _names = nm;
		}

		private static void Reg(Dictionary<IntPtr, int> po, Dictionary<IntPtr, string> pl, IntPtr p, int pid, string layer)
		{
			if (p == IntPtr.Zero) return;
			po[p] = pid; pl[p] = layer;
		}

		private static void AddBehaviour(List<Behaviour> into, GameObject root, Il2CppSystem.Type type, string label)
		{
			try
			{
				if (type == null) return;
				var c = root.GetComponentInChildren(type, true);
				if (c == null) return;
				var b = c.TryCast<Behaviour>();
				if (b == null) return;
				into.Add(b);
				// Teach the class-name cache the interop name: these classes are obfuscated on this build.
				IntPtr k = IL2CPP.il2cpp_object_get_class(b.Pointer);
				lock (ClassNames) { string cur; if (!ClassNames.TryGetValue(k, out cur) || cur.StartsWith("obf@", StringComparison.Ordinal)) ClassNames[k] = label; }
			}
			catch { }
		}

		private static IntPtr Ptr(object o)
		{
			try { var b = o as Il2CppObjectBase; return b == null ? IntPtr.Zero : b.Pointer; } catch { return IntPtr.Zero; }
		}

		// ANATOMY: what an object is made of — child names and component classes — so the empty
		// "Blocked" instance can be matched to the prefab it was cloned from without an Instantiate
		// hook (VRChat's instantiation did not go through Internal_CloneSingle on this build), and so
		// "does the shell carry a rig / an Animator?" is answered from data.
		private static bool _prefabAnatomyLogged;
		private static readonly HashSet<int> _anatomyDone = new HashSet<int>();

		private static string Anatomy(GameObject go, int maxChildren)
		{
			var sb = new StringBuilder();
			try
			{
				var comps = go.GetComponents<Component>();
				sb.Append("components[");
				if (comps != null) for (int i = 0; i < comps.Length && i < 12; i++) { if (i > 0) sb.Append(','); sb.Append(comps[i] == null ? "null" : NameOfClass(comps[i])); }
				sb.Append("] ");
				var trs = go.GetComponentsInChildren<Transform>(true);
				int n = trs != null ? trs.Length - 1 : 0;
				sb.Append("descendants=").Append(n < 0 ? 0 : n);
				int anims = 0, rends = 0;
				try { var a = go.GetComponentsInChildren<Animator>(true); anims = a != null ? a.Length : 0; } catch { }
				try { var r = go.GetComponentsInChildren<Renderer>(true); rends = r != null ? r.Length : 0; } catch { }
				sb.Append(" animators=").Append(anims).Append(" renderers=").Append(rends);
				if (trs != null && n > 0)
				{
					sb.Append(" children{");
					int shown = 0;
					for (int i = 0; i < trs.Length && shown < maxChildren; i++)
					{
						var tr = trs[i];
						if (tr == null || tr.gameObject == null || tr.gameObject.GetInstanceID() == go.GetInstanceID()) continue;
						if (shown++ > 0) sb.Append(',');
						sb.Append(tr.name);
					}
					if (n > shown) sb.Append(",…+").Append(n - shown);
					sb.Append('}');
				}
			}
			catch (Exception e) { sb.Append(" (anatomy failed: ").Append(Unwrap.Root(e).GetType().Name).Append(')'); }
			return sb.ToString();
		}

		private static void LogPrefabAnatomy(VRCAvatarManager mgr)
		{
			_prefabAnatomyLogged = true;
			try
			{
				Type ty = typeof(VRCAvatarManager);
				for (int i = 0; i <= 7; i++)
				{
					string n = "field_Public_GameObject_" + i;
					GameObject go = null;
					try { go = ty.GetProperty(n)?.GetValue(mgr) as GameObject; } catch { }
					if (go == null) continue;
					Log("[STATE-AVATARMGR] prefab " + n + " '" + Nm(go) + "' #" + Id(go) + ": " + Anatomy(go, 14));
				}
			}
			catch (Exception e) { Log("prefab anatomy failed: " + Unwrap.Describe(e)); }
		}

		private static void LogPrefabs(VRCAvatarManager mgr)
		{
			if (!_fields) { Log("[STATE-AVATARMGR] placeholder prefab listing skipped (FieldOffsetFix not verified)."); _prefabSlots[0] = "skipped"; return; }
			try
			{
				var sb = new StringBuilder("[STATE-AVATARMGR] placeholder avatar prefabs referenced by VRCAvatarManager (which one gets instantiated for Blocked / Safety / Loading is what the Instantiate hook will name): ");
				Type ty = typeof(VRCAvatarManager);
				for (int i = 0; i <= 7; i++)
				{
					string n = "field_Public_GameObject_" + i;
					try
					{
						var pi = ty.GetProperty(n);
						var go = pi?.GetValue(mgr) as GameObject;
						if (go == null) { sb.Append(n).Append("=null "); continue; }
						int id = go.GetInstanceID();
						_prefabSlots[id] = n;
						sb.Append(n).Append("='").Append(go.name).Append("'#").Append(id).Append(' ');
					}
					catch { sb.Append(n).Append("=? "); }
				}
				try
				{
					var arr = mgr.field_Public_Il2CppReferenceArray_1_GameObject_0;
					if (arr != null) { sb.Append("| array[").Append(arr.Length).Append("]: "); for (int i = 0; i < arr.Length && i < 8; i++) { var go = arr[i]; if (go == null) { sb.Append("null "); continue; } int id = go.GetInstanceID(); _prefabSlots[id] = "array[" + i + "]"; sb.Append('\'').Append(go.name).Append("'#").Append(id).Append(' '); } }
				}
				catch { }
				Log(sb.ToString());
			}
			catch (Exception e) { Log("prefab listing failed: " + Unwrap.Describe(e)); }
		}

		// ---------------------------------------------------------------- windows

		private void OnCapture(NetworkLogModule.Cap c, float now)
		{
			float secs = 3.5f; try { secs = ModConfig.BlockDebugTraceSeconds.Value; } catch { }
			if (secs < 1f) secs = 1f; if (secs > 10f) secs = 10f;
			_lastEvTicks = c.Ticks; _lastEvSeq = c.Seq;
			if (_w != null && !_w.Ended)
			{
				_w.Events++;
				_w.EndRt = Math.Max(_w.EndRt, c.Time + secs);
				Log("+" + ((c.Time - _w.T0Rt) * 1000f).ToString("0") + " ms  another event 33 (#" + c.Seq + ", actor " + c.Sender + ") inside the open window — the window is extended; '+ms' below stay relative to THIS event (#" + c.Seq + "). payload " + c.Payload);
				_w.Extra += " | event 33 #" + c.Seq + " at +" + ((c.Time - _w.T0Rt) * 1000f).ToString("0") + " ms: " + c.Payload;
				return;
			}
			var w = new Window { Seq = c.Seq, T0Ticks = c.Ticks, T0Rt = c.Time, EndRt = c.Time + secs, Clock = c.Clock, Payload = c.Payload, Sender = c.Sender, HandlerMs = c.HandlerMs };
			w.SyncChanges = _postSeq == c.Seq ? _sync : null;
			_w = w;
			foreach (var t in _tp.Values) { t.Hot = false; t.Lines = 0; t.First.Clear(); t.Seq.Clear(); t.Changed.Clear(); t.MovedRoot = t.MovedNet = t.MovedHead = 0f; t.HasPos = t.HasNet = t.HasHead = false; t.AvatarAtStart = AvatarDesc(t); }
			Log("================ EVENT 33 #" + c.Seq + " at " + c.Clock + " (rt " + c.Time.ToString("F3") + ", f" + c.Frame + ") from actor " + c.Sender + " — window " + secs.ToString("0.0") + " s ================");
			Log("payload: " + c.Payload);
			Log("handler: VRCNetworkingClient.OnEvent(EventData) " + (c.HandlerMs >= 0 ? "returned after " + c.HandlerMs.ToString("0.00") + " ms" : "duration unavailable (prefix not armed)")
				+ " | synchronous representation/state changes INSIDE the handler: " + (w.SyncChanges == null ? "unknown (no pre/post snapshot for this event)" : w.SyncChanges.Count == 0 ? "NONE" : string.Join("; ", w.SyncChanges.ToArray())));
			// Baseline: the snapshot taken as the handler returned, else a fresh one now.
			if (_post != null && _postSeq == c.Seq)
				foreach (var t in _tp.Values) { Dictionary<string, string> s; if (_post.TryGetValue(t.Pid, out s)) t.Last = s; }
			string affected = AttributeFromPayload(c.Payload);
			if (affected != null) Log("payload names a user id that is in this instance: " + affected);
		}

		private string AttributeFromPayload(string payload)
		{
			if (string.IsNullOrEmpty(payload)) return null;
			foreach (var t in _tp.Values)
				if (!string.IsNullOrEmpty(t.Uid) && payload.IndexOf(t.Uid, StringComparison.OrdinalIgnoreCase) >= 0) return "Player=" + t.Pid + " '" + t.Name + "' (" + t.Uid + ")";
			string luid = LocalUid();
			if (!string.IsNullOrEmpty(luid) && payload.IndexOf(luid, StringComparison.OrdinalIgnoreCase) >= 0) return "the LOCAL user (" + luid + ")";
			return null;
		}

		private void Finish(float now)
		{
			var w = _w; if (w == null) return;
			w.Ended = true;
			try { Summary(w, now); }
			catch (Exception e) { Log("summary threw: " + Unwrap.Describe(e)); }
			_w = null;
		}

		// ---------------------------------------------------------------- handler-side snapshots

		private static bool _threadLogged;

		private static void OnWatchedEnter(byte code, int seq)
		{
			if (!_armed || code != 33 || _inst == null) return;
			bool main = Environment.CurrentManagedThreadId == _mainThread;
			if (!_threadLogged) { _threadLogged = true; Log("OnEvent(33) runs on managed thread " + Environment.CurrentManagedThreadId + (main ? " = the Unity main thread" : " ≠ the Unity main thread (" + _mainThread + ") — handler snapshots are STATE-only (no Unity object read off the main thread)")); }
			// The '+ms' origin is set HERE, on arrival — before VRChat's handler and before the LateUpdate
			// drain — so a change made synchronously inside the handler is stamped against THIS event.
			_lastEvTicks = Stopwatch.GetTimestamp(); _lastEvSeq = seq;
			try { _pre = _inst.SnapAll(main); _preSeq = seq; }
			catch (Exception e) { _pre = null; Log("pre-handler snapshot threw: " + Unwrap.Describe(e)); }
		}

		private static void OnWatchedExit(byte code, int seq)
		{
			if (!_armed || code != 33 || _inst == null) return;
			bool main = Environment.CurrentManagedThreadId == _mainThread;
			try { _post = _inst.SnapAll(main); }
			catch (Exception e) { _post = null; Log("post-handler snapshot threw: " + Unwrap.Describe(e)); }
			_postSeq = seq;
			var changes = new List<string>();
			try
			{
				if (_post == null) changes.Add("post snapshot unavailable");
				else if (_pre == null || _preSeq != seq) changes.Add("pre snapshot unavailable (seq " + _preSeq + " vs " + seq + ")");
				else
				{
					foreach (var kv in _post)
					{
						Dictionary<string, string> before;
						if (!_pre.TryGetValue(kv.Key, out before)) continue;
						foreach (var e in kv.Value)
						{
							string old;
							if (before.TryGetValue(e.Key, out old) && old != e.Value && changes.Count < 40)
								changes.Add("Player=" + kv.Key + " " + e.Key + ": " + old + " -> " + e.Value);
						}
					}
				}
			}
			catch (Exception e) { changes.Add("diff threw: " + Unwrap.Describe(e)); }
			_sync = changes;
		}

		// unityOk=false: off the main thread, only VRChat's own state members are read (plain il2cpp
		// field/property reads); no Unity object (active flags, renderers, names) is touched.
		private Dictionary<int, Dictionary<string, string>> SnapAll(bool unityOk)
		{
			var d = new Dictionary<int, Dictionary<string, string>>();
			foreach (var t in _tp.Values) { try { d[t.Pid] = unityOk ? Snap(t, true) : SnapStateOnly(t); } catch { } }
			return d;
		}

		private Dictionary<string, string> SnapStateOnly(TP t)
		{
			var d = new Dictionary<string, string>(160, StringComparer.Ordinal);
			if (t.Mgr != null)
			{
				if (_fields)
				{
					try { d["STATE-AVATARMGR.visibility"] = t.Mgr.field_Private_EnumNPrivateSealedvaViIn3vUnique_0.ToString(); } catch { }
					try { d["STATE-AVATARMGR.avatarKind"] = t.Mgr.field_Private_EnumNPublicSealedvaUnLoErBlSaPeSuFaCuUnique_0.ToString(); } catch { }
				}
				ProbeMembers(d, "STATE-AVATARMGR", t.Mgr, typeof(VRCAvatarManager));
			}
			if (t.Player != null) ProbeMembers(d, "STATE-PLAYER.VRC.Player", t.Player, typeof(VRC.Player));
			if (t.VP != null) ProbeMembers(d, "STATE-PLAYER.VRCPlayer", t.VP, typeof(VRCPlayer));
			ProbeModeration(d, t);
			return d;
		}

		// ---------------------------------------------------------------- sampling

		private void SampleAll(float now, bool full, bool logDiffs)
		{
			int n = _tp.Count;
			bool everyFull = n <= FullEveryFrameMaxPlayers;
			int k = 0;
			foreach (var t in _tp.Values)
			{
				k++;
				bool doFull = full && (everyFull || t.Hot || ((_cursor + k) % 4 == 0) || now - t.LastFullAt > 0.5f);
				try { Sample(t, now, doFull, logDiffs); }
				catch (Exception e) { if (t.Lines++ < MaxLinesPerPlayer) Log("sample of Player=" + t.Pid + " threw: " + Unwrap.Describe(e)); }
			}
			_cursor++;
		}

		private void Sample(TP t, float now, bool full, bool logDiffs)
		{
			if (t.Root == null) return;
			RefreshAvatar(t, now, false);
			if (full) t.LastFullAt = now;
			var s = Snap(t, full);
			if (logDiffs && t.Last != null)
			{
				double ms = MsSinceEvent();
				foreach (var kv in s)
				{
					string old;
					if (!t.Last.TryGetValue(kv.Key, out old) || old == kv.Value) continue;
					string layer = LayerOf(kv.Key);
					t.Hot = true;
					if (!t.First.ContainsKey(layer)) t.First[layer] = ms;
					KeyValuePair<string, string> prev;
					t.Changed[kv.Key] = t.Changed.TryGetValue(kv.Key, out prev) ? new KeyValuePair<string, string>(prev.Key, kv.Value) : new KeyValuePair<string, string>(old, kv.Value);
					string line = "+" + ms.ToString("0.0") + " ms f" + Time.frameCount + "  Player=" + t.Pid + " '" + t.Name + "'  [" + layer + "]  " + kv.Key + ": " + old + " -> " + kv.Value;
					if (t.Lines++ < MaxLinesPerPlayer) Log(line); else if (t.Lines == MaxLinesPerPlayer + 1) Log("Player=" + t.Pid + ": further sampled changes suppressed (cap " + MaxLinesPerPlayer + ")");
					t.Seq.Add(ms.ToString("+0.0") + " ms  [" + layer + "] " + kv.Key + ": " + old + " -> " + kv.Value);
				}
				// Motion (never diffed as text — accumulated).
				try { Vector3 p = t.Root.transform.position; if (t.HasPos) t.MovedRoot += (p - t.LastPos).magnitude; t.LastPos = p; t.HasPos = true; } catch { }
				try { if (t.Api != null) { Vector3 np = t.Api.GetPosition(); if (t.HasNet) t.MovedNet += (np - t.LastNet).magnitude; t.LastNet = np; t.HasNet = true; } } catch { }
				try { if (t.Api != null && t.HasPos) { Vector3 h = t.Api.GetBonePosition(HumanBodyBones.Head); if (h != Vector3.zero) { Vector3 rel = h - t.LastPos; if (t.HasHead) t.MovedHead += (rel - t.LastHeadRel).magnitude; t.LastHeadRel = rel; t.HasHead = true; } } } catch { }
			}
			if (t.Last == null || full) t.Last = s;
			else foreach (var kv in s) t.Last[kv.Key] = kv.Value;
		}

		private static string LayerOf(string key)
		{
			int i = key.IndexOf('.');
			return i > 0 ? key.Substring(0, i) : key;
		}

		// One player's representation, as name → value. Only bool/int/enum members are read from
		// VRChat's own classes (field_* only when FieldOffsetFix verified); no il2cpp string is read
		// off a player object here.
		private Dictionary<string, string> Snap(TP t, bool full)
		{
			var d = new Dictionary<string, string>(full ? 256 : 40, StringComparer.Ordinal);
			GameObject root = t.Root;
			try { d["ROOT.activeSelf"] = B(root.activeSelf); d["ROOT.activeInHierarchy"] = B(root.activeInHierarchy); } catch { d["ROOT.exists"] = "false"; }
			// avatar
			GameObject av = t.Avatar;
			bool avAlive = !(av is null) && av != null;
			d["AVATAR.object"] = avAlive ? "#" + t.AvatarId + " '" + t.AvatarName + "'" : (t.AvatarId != 0 ? "NONE (was #" + t.AvatarId + ")" : "NONE");
			if (avAlive)
			{
				try { d["AVATAR.activeSelf"] = B(av.activeSelf); d["AVATAR.activeInHierarchy"] = B(av.activeInHierarchy); } catch { }
				try { d["AVATAR.descriptor"] = av.GetComponent<VRCAvatarDescriptor>() != null ? "SDK3 descriptor" : "NONE"; } catch { }
				if (full) { try { var cs = av.GetComponents<Component>(); d["AVATAR.components"] = (cs != null ? cs.Length : 0).ToString(); } catch { } }
			}
			if (_fields)
			{
				try { var g = t.VP != null ? t.VP.field_Internal_GameObject_0 : null; d["AVATAR.VRCPlayer.field_Internal_GameObject_0"] = g != null ? "#" + g.GetInstanceID() : "null"; } catch { }
				try { var g = t.Mgr != null ? t.Mgr.field_Private_GameObject_0 : null; d["AVATAR.VRCAvatarManager.field_Private_GameObject_0"] = g != null ? "#" + g.GetInstanceID() : "null"; } catch { }
			}
			try { var g = t.Mgr != null ? t.Mgr.prop_GameObject_0 : null; d["AVATAR.VRCAvatarManager.prop_GameObject_0"] = g != null ? "#" + g.GetInstanceID() : "null"; } catch { }
			// renderers (cached list; cheap per frame)
			int total = 0, en = 0, fo = 0, ga = 0, dr = 0, vis = 0, dead = 0;
			for (int i = 0; i < t.Rend.Count; i++)
			{
				var r = t.Rend[i];
				try
				{
					if (r == null) { dead++; continue; }
					total++;
					bool e = r.enabled, f = r.forceRenderingOff, a = r.gameObject.activeInHierarchy;
					if (e) en++; if (f) fo++; if (a) ga++; if (e && !f && a) dr++;
					if (full && r.isVisible) vis++;
				}
				catch { }
			}
			d["RENDERERS.total"] = total.ToString(); d["RENDERERS.enabled"] = en.ToString(); d["RENDERERS.forceRenderingOff"] = fo.ToString(); d["RENDERERS.gameObjectActive"] = ga.ToString(); d["RENDERERS.drawable"] = dr.ToString();
			if (dead > 0) d["RENDERERS.destroyed"] = dead.ToString();
			if (full) d["RENDERERS.isVisible"] = vis.ToString();
			// animator
			try
			{
				var an = t.Anim;
				if (an == null) d["ANIMATOR.object"] = "NONE";
				else { d["ANIMATOR.object"] = "#" + an.GetInstanceID(); d["ANIMATOR.enabled"] = B(an.enabled); d["ANIMATOR.isActiveAndEnabled"] = B(an.isActiveAndEnabled); if (full) { d["ANIMATOR.controller"] = B(an.runtimeAnimatorController != null); d["ANIMATOR.avatar"] = B(an.avatar != null); } }
			}
			catch { d["ANIMATOR.object"] = "err"; }
			// IK
			for (int i = 0; i < t.Ik.Count; i++) { try { var b = t.Ik[i]; if (b == null) continue; d["IK." + NameOfClass(b) + ".enabled"] = B(b.enabled); } catch { } }
			// nameplate & UI
			try
			{
				var c = t.NpContainer;
				if (c == null) d["NAMEPLATE.container"] = "NOT FOUND";
				else { d["NAMEPLATE.container.activeSelf"] = B(c.activeSelf); d["NAMEPLATE.container.activeInHierarchy"] = B(c.activeInHierarchy); }
				if (t.NP != null)
				{
					d["NAMEPLATE.PlayerNameplate.enabled"] = B(t.NP.enabled);
					if (t.NpGo != null) d["NAMEPLATE.PlayerNameplate.gameObject.activeInHierarchy"] = B(t.NpGo.activeInHierarchy);
					for (int i = 0; i < t.NpGroups.Count; i++) { var g = t.NpGroups[i]; if (g != null) d["NAMEPLATE.CanvasGroup_" + i + ".alpha"] = g.alpha.ToString("0.00"); }
					if (full) ProbeMembers(d, "NAMEPLATE.PlayerNameplate", t.NP, typeof(PlayerNameplate));
				}
				if (t.Positioner != null) d["NAMEPLATE.positioner.enabled"] = B(t.Positioner.enabled);
				int act = 0, tot = 0;
				for (int i = 0; i < t.NpChildren.Count; i++) { var g = t.NpChildren[i]; try { if (g == null) continue; tot++; if (g.activeSelf) act++; } catch { } }
				d["PLAYER-UI.nameplateChildrenActive"] = act + "/" + tot;
				if (t.Bubble != null) d["PLAYER-UI.chatBubble.activeInHierarchy"] = B(t.Bubble.activeInHierarchy);
			}
			catch { }
			// audio
			int ac = 0, ae = 0, ap = 0, am = 0;
			for (int i = 0; i < t.Audio.Count; i++) { var a = t.Audio[i]; try { if (a == null) continue; ac++; if (a.enabled) ae++; if (a.isPlaying) ap++; if (a.mute) am++; } catch { } }
			d["AUDIO.sources"] = ac + " (enabled " + ae + ", playing " + ap + ", muted " + am + ")";
			try { if (t.USpeaker != null) d["AUDIO.USpeaker.enabled"] = B(t.USpeaker.enabled); } catch { }
			if (!full) return d;
			// VRChat's own state, by member name
			if (t.Mgr != null)
			{
				if (_fields)
				{
					try { d["STATE-AVATARMGR.visibility"] = t.Mgr.field_Private_EnumNPrivateSealedvaViIn3vUnique_0.ToString(); } catch { }
					try { d["STATE-AVATARMGR.avatarKind"] = t.Mgr.field_Private_EnumNPublicSealedvaUnLoErBlSaPeSuFaCuUnique_0.ToString(); } catch { }
					try { d["STATE-AVATARMGR.avatarKind_1"] = t.Mgr.field_Private_EnumNPublicSealedvaUnLoErBlSaPeSuFaCuUnique_1.ToString(); } catch { }
					try { d["STATE-AVATARMGR.blockReason"] = t.Mgr.field_Private_EnumNPublicSealedvaUnPlSiPeSeInCa8vUnique_0.ToString(); } catch { }
				}
				else
				{
					try { d["STATE-AVATARMGR.avatarKind"] = t.Mgr.prop_EnumNPublicSealedvaUnLoErBlSaPeSuFaCuUnique_0.ToString(); } catch { }
					try { d["STATE-AVATARMGR.avatarKind_1"] = t.Mgr.prop_EnumNPublicSealedvaUnLoErBlSaPeSuFaCuUnique_1.ToString(); } catch { }
					try { d["STATE-AVATARMGR.blockReason"] = t.Mgr.prop_EnumNPublicSealedvaUnPlSiPeSeInCa8vUnique_0.ToString(); } catch { }
				}
				ProbeMembers(d, "STATE-AVATARMGR", t.Mgr, typeof(VRCAvatarManager));
			}
			if (t.Player != null) ProbeMembers(d, "STATE-PLAYER.VRC.Player", t.Player, typeof(VRC.Player));
			if (t.VP != null) ProbeMembers(d, "STATE-PLAYER.VRCPlayer", t.VP, typeof(VRCPlayer));
			ProbeModeration(d, t);
			return d;
		}

		private static void ProbeModeration(Dictionary<string, string> d, TP t)
		{
			try
			{
				var mm = VRC.ModerationManager.prop_ModerationManager_0;
				if (mm == null) { d["STATE-MODERATION.manager"] = "null"; return; }
				if (_fields)
				{
					try { d["STATE-MODERATION.playerIdInHashSet<int>"] = B(mm.field_Private_HashSet_1_Int32_0.Contains(t.Pid)); } catch { }
					try
					{
						Il2CppSystem.ValueTuple<bool, bool> tup;
						var dict = mm.field_Private_Dictionary_2_Int32_ValueTuple_2_Boolean_Boolean_0;
						d["STATE-MODERATION.playerTuple<int,(bool,bool)>"] = dict != null && dict.TryGetValue(t.Pid, out tup) ? "(" + tup.Item1 + "," + tup.Item2 + ")" : "absent";
					}
					catch { }
					if (!string.IsNullOrEmpty(t.Uid))
					{
						try { d["STATE-MODERATION.userIdInHashSet<string>"] = B(mm.field_Private_HashSet_1_String_0.Contains(t.Uid)); } catch { }
						try
						{
							Il2CppSystem.Collections.Generic.List<VRC.Core.ApiPlayerModeration> list;
							var pm = mm.field_Private_Dictionary_2_String_List_1_ApiPlayerModeration_0;
							d["STATE-MODERATION.userPlayerModerations"] = pm != null && pm.TryGetValue(t.Uid, out list) && list != null ? "list(" + list.Count + ")" : "none";
						}
						catch { }
					}
				}
				ProbeMembers(d, "STATE-MODERATION.ModerationManager", mm, typeof(VRC.ModerationManager));
				var pmgr = PlayerManager.prop_PlayerManager_0;
				if (pmgr != null)
				{
					try { d["STATE-PLAYERMGR.prop_Int32_0"] = pmgr.prop_Int32_0.ToString(); d["STATE-PLAYERMGR.prop_Int32_1"] = pmgr.prop_Int32_1.ToString(); } catch { }
					if (!string.IsNullOrEmpty(t.Uid))
					{
						try { d["STATE-PLAYERMGR.prop_HashSet_1_String_0.has"] = B(pmgr.prop_HashSet_1_String_0.Contains(t.Uid)); } catch { }
						try { d["STATE-PLAYERMGR.prop_HashSet_1_String_1.has"] = B(pmgr.prop_HashSet_1_String_1.Contains(t.Uid)); } catch { }
						if (_fields)
						{
							try { d["STATE-PLAYERMGR.field_Private_HashSet_1_String_0.has"] = B(pmgr.field_Private_HashSet_1_String_0.Contains(t.Uid)); } catch { }
							try { d["STATE-PLAYERMGR.field_Private_HashSet_1_String_1.has"] = B(pmgr.field_Private_HashSet_1_String_1.Contains(t.Uid)); } catch { }
							try { d["STATE-PLAYERMGR.field_Private_HashSet_1_String_2.has"] = B(pmgr.field_Private_HashSet_1_String_2.Contains(t.Uid)); } catch { }
						}
					}
				}
			}
			catch (Exception e) { d["STATE-MODERATION.error"] = Unwrap.Root(e).GetType().Name; }
		}

		// bool / int / enum members named prop_* or field_* (field_* only when FieldOffsetFix verified).
		private static readonly Dictionary<Type, PropertyInfo[]> MemberCache = new Dictionary<Type, PropertyInfo[]>();
		private static readonly Dictionary<Type, PropertyInfo> ValueProp = new Dictionary<Type, PropertyInfo>();

		private static PropertyInfo[] Members(Type ty)
		{
			PropertyInfo[] arr;
			if (MemberCache.TryGetValue(ty, out arr)) return arr;
			bool fields = false; try { fields = FieldOffsetFix.Verified; } catch { }
			var list = new List<PropertyInfo>();
			try
			{
				foreach (var p in ty.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					string n = p.Name;
					bool isField = n.StartsWith("field_", StringComparison.Ordinal);
					if (!isField && !n.StartsWith("prop_", StringComparison.Ordinal)) continue;
					if (isField && !fields) continue;
					if (!p.CanRead) continue;
					try { if (p.GetIndexParameters().Length > 0) continue; } catch { continue; }
					Type pt = p.PropertyType;
					bool ok = pt == typeof(bool) || pt == typeof(int) || pt == typeof(byte) || pt == typeof(short) || pt == typeof(uint) || pt.IsEnum
						|| (pt.IsGenericType && pt.Name.StartsWith("ReactiveProperty", StringComparison.Ordinal) && pt.GetGenericArguments().Length == 1 && (pt.GetGenericArguments()[0] == typeof(bool) || pt.GetGenericArguments()[0].IsEnum));
					if (ok) list.Add(p);
					if (list.Count >= 160) break;
				}
			}
			catch { }
			arr = list.ToArray();
			MemberCache[ty] = arr;
			return arr;
		}

		private static void ProbeMembers(Dictionary<string, string> d, string prefix, object obj, Type ty)
		{
			var ms = Members(ty);
			for (int i = 0; i < ms.Length; i++)
			{
				var p = ms[i];
				try
				{
					object v = p.GetValue(obj, null);
					if (v == null) { d[prefix + "." + p.Name] = "null"; continue; }
					Type pt = p.PropertyType;
					if (pt.IsGenericType && pt.Name.StartsWith("ReactiveProperty", StringComparison.Ordinal))
					{
						PropertyInfo vp;
						if (!ValueProp.TryGetValue(pt, out vp)) { vp = pt.GetProperty("Value"); ValueProp[pt] = vp; }
						object rv = vp != null ? vp.GetValue(v, null) : null;
						d[prefix + "." + p.Name + ".Value"] = rv == null ? "null" : rv.ToString();
					}
					else d[prefix + "." + p.Name] = v.ToString();
				}
				catch (Exception e) { d[prefix + "." + p.Name] = "err:" + Unwrap.Root(e).GetType().Name; }
			}
		}

		// ---------------------------------------------------------------- hooks (observe only)

		private static bool _hooksInstalled; private static int _hookOk, _hookFail;
		private static readonly List<string> _hookFailed = new List<string>();
		// Every patch we installed, so switching the trace OFF can actually take them back out. Without
		// this the toggle was half a switch: the prefixes early-returned on !_armed, but the detour
		// itself stayed on Object.Destroy / GameObject.SetActive / Renderer.enabled / Behaviour.enabled
		// — four of the hottest setters in Unity, called by the whole game thousands of times a second
		// — until VRChat was restarted. Unpatched by the EXACT patch method, never by harmony id: the
		// id is shared with every other module, and unpatching by it would tear out their hooks too.
		private static readonly List<KeyValuePair<MethodBase, MethodInfo>> _patched = new List<KeyValuePair<MethodBase, MethodInfo>>();

		private void EnsureHooks()
		{
			if (_hooksInstalled) return;
			_hooksInstalled = true;
			try
			{
				const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
				Type uo = typeof(UnityEngine.Object);
				Hook(uo.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object), typeof(float) }, null), nameof(DestroyPre2), null);
				Hook(uo.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object) }, null), nameof(DestroyPre1), null);
				Hook(uo.GetMethod("DestroyImmediate", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object), typeof(bool) }, null), nameof(DestroyImmPre2), null);
				Hook(uo.GetMethod("DestroyImmediate", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object) }, null), nameof(DestroyImmPre1), null);
				Hook(uo.GetMethod("Internal_CloneSingle", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object) }, null), null, nameof(ClonePost1));
				Hook(uo.GetMethod("Internal_CloneSingleWithParent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object), typeof(Transform), typeof(bool) }, null), null, nameof(ClonePost3));
				Hook(typeof(GameObject).GetMethod("SetActive", new[] { typeof(bool) }), nameof(SetActivePre), null);
				Hook(typeof(Renderer).GetProperty("enabled")?.GetSetMethod(), nameof(RendererEnabledPre), null);
				Hook(typeof(Renderer).GetProperty("forceRenderingOff")?.GetSetMethod(), nameof(RendererForceOffPre), null);
				Hook(typeof(Behaviour).GetProperty("enabled")?.GetSetMethod(), nameof(BehaviourEnabledPre), null);
				int unity = _hookOk;

				// VRChat-method hooks are OPT-IN since the 3.9.77 crash: on this build the linker FOLDS
				// identical function bodies (ICF), so an obfuscated setter such as
				// VRC.Player.Method_Public_Void_APIUser_0 shares its machine code with unrelated methods
				// all over the game — the detour then fires from network/crypto threads with a `this`
				// that is not an object, and the interop stub dies building a proxy from it. HookNamed now
				// refuses static methods, bodies without a .pdata entry or shorter than 0x80 bytes, and
				// any address shared by two table entries; but the only fully safe default is OFF.
				bool vrc = false;
				try { vrc = ModConfig.BlockDebugHookVrcMethods.Value; } catch { }
				if (!vrc) Log("VRChat-method hooks are OFF (BlockDebug/HookVrcMethods=false) — the callers of each change are named by the native stack instead. Unity hooks only.");
				else
				{
				HookNamed(typeof(VRCAvatarManager), new[]
				{
					"Method_Private_Void_EnumNPrivateSealedvaViIn3vUnique_PDM_0", "Method_Private_Void_EnumNPrivateSealedvaSuStSu4vUnique_0",
					"Method_Private_Void_GameObject_0", "Method_Private_Void_GameObject_1", "Method_Private_Void_GameObject_2", "Method_Private_Void_GameObject_3", "Method_Private_Void_GameObject_4",
					"Method_Public_Void_GameObject_0", "Method_Internal_Static_Void_GameObject_0", "Method_Internal_Static_Void_GameObject_PDM_0",
					"Method_Private_Void_Boolean_0", "Method_Private_Void_Boolean_1", "Method_Private_Void_Boolean_2", "Method_Private_Void_Boolean_3",
					"Method_Private_Void_Boolean_PDM_0", "Method_Private_Void_Boolean_PDM_1", "Method_Private_Void_Boolean_PDM_2",
					"Method_Public_Void_Boolean_PDM_0", "Method_Public_Void_Boolean_PDM_1", "Method_Private_Void_Boolean_Boolean_0",
					"Method_Public_UniTaskVoid_ApiAvatar_0", "Method_Private_Void_String_0",
					"Method_Private_AvatarClone_String_EnumPublicSealedva4v0_EnumNPublicSealedvaUnLoErBlSaPeSuFaCuUnique_GameObject_0",
					"Method_Public_Void_Animator_Boolean_0", "Method_Private_Boolean_Component_Boolean_Boolean_0", "Method_Internal_Void_Single_Boolean_Boolean_0",
					"Method_Public_Static_Void_ApiAvatar_GameObject_Boolean_Boolean_AvatarVariant_0", "Method_Private_Void_byref_Boolean_0", "OnDestroy",
				});
				HookNamed(typeof(VRC.ModerationManager), new[]
				{
					"Method_Public_Void_Dictionary_2_Byte_Object_PDM_0", "Method_Private_UniTaskVoid_Dictionary_2_Byte_Object_PDM_0",
					"Method_Private_Void_String_Byte_Dictionary_2_Byte_Object_0", "Method_Private_Void_String_Byte_Object_0",
					"Method_Public_Virtual_Final_New_Void_String_Boolean_0", "Method_Public_Void_String_ModerationType_0", "Method_Public_Void_String_ModerationType_1",
					"Method_Private_Void_String_ModerationType_PDM_0", "Method_Private_Void_String_ModerationType_PDM_1", "Method_Private_Void_String_ModerationType_0",
					"Method_Private_Void_String_0", "Method_Private_Void_String_1", "Method_Private_Void_String_2", "Method_Public_Void_String_PDM_0",
					"Method_Public_Void_APIUser_Boolean_0", "Method_Public_Void_APIUser_Boolean_1", "Method_Public_Void_APIUser_Boolean_2", "Method_Public_Virtual_Final_New_Void_APIUser_Boolean_0",
					"Method_Public_Void_APIUser_0", "Method_Public_Void_APIUser_1", "Method_Public_Void_APIUser_2", "Method_Public_Void_APIUser_3", "Method_Public_Void_APIUser_4", "Method_Public_Void_APIUser_5", "Method_Public_Void_APIUser_6",
					"Method_Private_Void_APIUser_0", "Method_Private_Void_APIUser_String_0", "Method_Private_Void_String_Single_PDM_0",
					"Method_Public_Void_0", "Method_Public_Void_1", "Method_Public_Void_2", "Method_Public_Void_3", "Method_Private_Void_0",
				});
				HookNamed(typeof(VRCNetworkingClient), new[] { "Method_Public_Virtual_Final_New_Void_Dictionary_2_Byte_Object_0", "Method_Public_Virtual_Final_New_Void_Dictionary_2_Byte_Object_1" });
				HookNamed(typeof(VRC.Player), new[] { "Method_Public_Void_Boolean_0", "Method_Public_Void_Boolean_1", "Method_Public_Void_Boolean_2", "Method_Internal_Void_Boolean_0", "Method_Private_Void_0", "Method_Private_Void_1", "Method_Public_Void_APIUser_0" });
				HookNamed(typeof(VRCPlayer), new[] { "ReloadAvatarNetworkedRPC", "Method_Public_Void_Boolean_PDM_0", "Method_Public_Void_Boolean_PDM_1", "Method_Public_Void_Boolean_0", "Method_Private_Void_Boolean_Boolean_0", "Method_Public_Void_Boolean_Boolean_PDM_0", "Method_Public_Void_Single_Boolean_Boolean_0", "Method_Private_IEnumerator_Boolean_PDM_0", "Method_Public_Void_Boolean_String_String_Boolean_PDM_0" });
				HookNamed(typeof(PlayerNameplate), new[] { "Method_Private_Void_Boolean_PDM_0", "Method_Private_Void_Boolean_Boolean_PDM_0", "Method_Private_Void_Single_Boolean_0", "Method_Public_Virtual_Final_New_Void_Single_Single_0", "Method_Private_Void_Player_PDM_0", "Method_Public_Void_PDM_0", "Method_Private_Void_String_PDM_0", "Method_Private_Void_String_PDM_1", "Method_Private_Void_Int32_Int32_PDM_0", "Method_Private_Void_Int32_Int32_PDM_1", "OnDestroy", "Dispose" });
				}

				Log("hooks: " + _hookOk + " installed (" + unity + " Unity, " + (_hookOk - unity) + " VRChat), " + _hookFail + " failed" + (_hookFailed.Count > 0 ? ": " + string.Join(" | ", _hookFailed.ToArray()) : "") + ". Every one is an observer: no argument, result or object is ever changed.");
			}
			catch (Exception e) { Log("hook installation threw: " + Unwrap.Describe(e)); }
		}

		private static void HookNamed(Type ty, string[] names)
		{
			const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
			foreach (string n in names)
			{
				MethodInfo m = null;
				try { m = ty.GetMethod(n, F); } catch (AmbiguousMatchException) { try { foreach (var c in ty.GetMethods(F)) if (c.Name == n) { m = c; break; } } catch { } } catch { }
				if (m == null) { _hookFail++; _hookFailed.Add(ty.Name + "." + n + ": not found on this build"); continue; }
				if (m.IsStatic) { _hookFail++; _hookFailed.Add(ty.Name + "." + n + ": static (no `this` to validate)"); continue; }
				// Folding filter: the native body must be a real, unshared function (see EnsureHooks).
				IntPtr fn; int shared;
				if (!NativeStack.TryFind(ty.Name, n, out fn, out shared)) { _hookFail++; _hookFailed.Add(ty.Name + "." + n + ": no native address in the table"); continue; }
				if (shared > 1) { _hookFail++; _hookFailed.Add(ty.Name + "." + n + ": FOLDED (code shared by " + shared + " methods)"); continue; }
				int size = NativeStack.FunctionSize(fn);
				if (size < 0x80) { _hookFail++; _hookFailed.Add(ty.Name + "." + n + ": " + (size == 0 ? "no .pdata entry (leaf/trivial)" : size + "-byte body") + " — foldable, refused"); continue; }
				int pc; try { pc = m.GetParameters().Length; } catch { pc = 99; }
				// nameof(), not string building: the release obfuscator renames private methods whose
				// names never appear as a string literal, and a renamed prefix is a hook that silently fails.
				string pre = PrefixName(m.IsStatic, pc);
				if (pre == null) { _hookFail++; _hookFailed.Add(ty.Name + "." + n + ": " + pc + " params (no generic prefix)"); continue; }
				Hook(m, pre, null);
			}
		}

		private static string PrefixName(bool isStatic, int pc)
		{
			if (isStatic)
				switch (pc) { case 0: return nameof(GenS0); case 1: return nameof(GenS1); case 2: return nameof(GenS2); case 3: return nameof(GenS3); case 4: return nameof(GenS4); case 5: return nameof(GenS5); default: return null; }
			switch (pc) { case 0: return nameof(Gen0); case 1: return nameof(Gen1); case 2: return nameof(Gen2); case 3: return nameof(Gen3); case 4: return nameof(Gen4); default: return null; }
		}

		private static void Hook(MethodBase target, string prefix, string postfix)
		{
			if (target == null) { _hookFail++; _hookFailed.Add((prefix ?? postfix) + ": target not found"); return; }
			try
			{
				MethodInfo preM = prefix != null ? typeof(Event33TraceModule).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic) : null;
				MethodInfo postM = postfix != null ? typeof(Event33TraceModule).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic) : null;
				var pre = preM != null ? new HarmonyMethod(preM) : null;
				var post = postM != null ? new HarmonyMethod(postM) : null;
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: pre, postfix: post);
				if (preM != null) _patched.Add(new KeyValuePair<MethodBase, MethodInfo>(target, preM));
				if (postM != null) _patched.Add(new KeyValuePair<MethodBase, MethodInfo>(target, postM));
				_hookOk++;
			}
			catch (Exception e) { _hookFail++; _hookFailed.Add(target.DeclaringType?.Name + "." + target.Name + ": " + Unwrap.Root(e).Message); }
		}

		// Take the detours back off. Called when the trace is switched off and on shutdown, so leaving
		// the switch off costs exactly nothing — no detour, no early-return, no per-call transition.
		private static void RemoveHooks()
		{
			if (!_hooksInstalled) return;
			int removed = 0, failed = 0;
			for (int i = 0; i < _patched.Count; i++)
			{
				try { VRChatArchiveModPlugin.HarmonyInstance.Unpatch(_patched[i].Key, _patched[i].Value); removed++; }
				catch { failed++; }
			}
			_patched.Clear();
			_hooksInstalled = false; _hookOk = 0; _hookFail = 0; _hookFailed.Clear();
			Log("hooks removed: " + removed + " detour(s) taken back off" + (failed > 0 ? ", " + failed + " could not be removed (they stay until VRChat restarts)" : "") + ".");
		}

		// --- Unity object lifecycle (main thread) ---
		private static IntPtr _lastDestroyPtr; private static int _lastDestroyFrame;

		private static void DestroyPre2(UnityEngine.Object __0, float __1) { OnDestroyCall(__0, "Object.Destroy(obj, " + __1.ToString("0.##") + "s)"); }
		private static void DestroyPre1(UnityEngine.Object __0) { OnDestroyCall(__0, "Object.Destroy(obj)"); }
		private static void DestroyImmPre2(UnityEngine.Object __0, bool __1) { OnDestroyCall(__0, "Object.DestroyImmediate(obj, " + __1 + ")"); }
		private static void DestroyImmPre1(UnityEngine.Object __0) { OnDestroyCall(__0, "Object.DestroyImmediate(obj)"); }

		private static void OnDestroyCall(UnityEngine.Object obj, string what)
		{
			try
			{
				if (!_armed || obj == null || Environment.CurrentManagedThreadId != _mainThread) return;
				IntPtr p = Ptr(obj);
				if (p == IntPtr.Zero) return;
				int frame = Time.frameCount;
				if (p == _lastDestroyPtr && frame == _lastDestroyFrame) return;   // Destroy(obj) → Destroy(obj, 0) : one call, two prefixes
				int pid; string layer;
				var po = _ptrOwner;
				if (po.TryGetValue(p, out pid)) { if (!_ptrLayer.TryGetValue(p, out layer)) layer = "?"; }
				else
				{
					Transform root = null; bool isRend = false;
					var go = obj.TryCast<GameObject>();
					if (go != null) root = go.transform.root;
					else
					{
						var c = obj.TryCast<Component>();
						if (c == null) return;
						root = c.transform.root; isRend = obj.TryCast<Renderer>() != null;
					}
					if (root == null || !_rootOwner.TryGetValue(root.gameObject.GetInstanceID(), out pid)) return;
					layer = isRend ? "RENDERERS" : "AVATAR-CHILD";
				}
				_lastDestroyPtr = p; _lastDestroyFrame = frame;
				Record(pid, layer, what + " on " + Describe(obj), true);
			}
			catch { }
		}

		private static void SetActivePre(GameObject __instance, bool __0)
		{
			try
			{
				if (!_armed || __instance == null || Environment.CurrentManagedThreadId != _mainThread) return;
				IntPtr p = Ptr(__instance);
				int pid; string layer;
				if (_ptrOwner.TryGetValue(p, out pid)) { if (!_ptrLayer.TryGetValue(p, out layer)) layer = "?"; }
				else
				{
					if (_w == null || _w.Ended) return;
					var root = __instance.transform.root;
					if (root == null || !_rootOwner.TryGetValue(root.gameObject.GetInstanceID(), out pid)) return;
					layer = "AVATAR-CHILD";
				}
				Record(pid, layer, "GameObject.SetActive(" + B(__0) + ") on " + Describe(__instance), true);
			}
			catch { }
		}

		private static void RendererEnabledPre(Renderer __instance, bool __0) { Flip(__instance, "Renderer.enabled = " + B(__0), "RENDERERS"); }
		private static void RendererForceOffPre(Renderer __instance, bool __0) { Flip(__instance, "Renderer.forceRenderingOff = " + B(__0), "RENDERERS"); }
		private static void BehaviourEnabledPre(Behaviour __instance, bool __0) { Flip(__instance, "Behaviour.enabled = " + B(__0), null); }

		// Renderer/behaviour flips come in bursts (all 14 renderers of an avatar in one frame): the
		// first of each (player, operation) per frame is recorded with its stack, the rest counted and
		// flushed as one line at the end of the frame.
		private sealed class Agg { public int Pid; public string Layer, Op, First; public int Count, Frame; public double Ms; public bool Sync; public string Stack; }
		private static readonly Dictionary<string, Agg> _agg = new Dictionary<string, Agg>(StringComparer.Ordinal);
		private static readonly object AggGate = new object();

		private static void Flip(Component c, string op, string layer)
		{
			try
			{
				if (!_armed || c == null || Environment.CurrentManagedThreadId != _mainThread) return;
				IntPtr p = Ptr(c);
				int pid;
				if (!_ptrOwner.TryGetValue(p, out pid)) return;
				if (layer == null && !_ptrLayer.TryGetValue(p, out layer)) layer = "?";
				string key = pid + "|" + op;
				lock (AggGate)
				{
					Agg a;
					if (_agg.TryGetValue(key, out a) && a.Frame == Time.frameCount) { a.Count++; return; }
					if (a != null) FlushOne(a);
					_agg[key] = new Agg { Pid = pid, Layer = layer, Op = op, First = Describe(c), Count = 1, Frame = Time.frameCount, Ms = MsSinceEvent(), Sync = NetworkLogModule.InsideWatchedCode == 33, Stack = NativeStack.Capture() };
				}
			}
			catch { }
		}

		private static void FlushAggregates()
		{
			lock (AggGate)
			{
				if (_agg.Count == 0) return;
				var done = new List<string>();
				int frame = Time.frameCount;
				foreach (var kv in _agg) if (kv.Value.Frame != frame) { FlushOne(kv.Value); done.Add(kv.Key); }
				foreach (var k in done) _agg.Remove(k);
			}
		}

		private static void FlushOne(Agg a)
		{
			Record(a.Pid, a.Layer, a.Op + " ×" + a.Count + " (first: " + a.First + ")" + (a.Sync ? " [SYNC inside OnEvent(33)]" : ""), a.Ms, a.Stack, a.Frame, a.Sync);
		}

		private static void ClonePost1(UnityEngine.Object __0, UnityEngine.Object __result) { OnClone(__0, __result, null); }
		private static void ClonePost3(UnityEngine.Object __0, Transform __1, bool __2, UnityEngine.Object __result) { OnClone(__0, __result, __1); }

		private static void OnClone(UnityEngine.Object prefab, UnityEngine.Object result, Transform parent)
		{
			try
			{
				if (!_armed || result == null || Environment.CurrentManagedThreadId != _mainThread) return;
				var go = result.TryCast<GameObject>();
				Transform root = go != null ? go.transform.root : (parent != null ? parent.root : null);
				if (root == null) return;
				int pid;
				if (!_rootOwner.TryGetValue(root.gameObject.GetInstanceID(), out pid)) return;
				string slot = null; int prefabId = 0;
				try { if (prefab != null) { prefabId = prefab.GetInstanceID(); _prefabSlots.TryGetValue(prefabId, out slot); } } catch { }
				Record(pid, "AVATAR", "Object.Instantiate → new " + Describe(result) + " from prefab " + Describe(prefab) + (slot != null ? " = VRCAvatarManager." + slot : "") + (parent != null ? " under '" + Nm(parent.gameObject) + "'" : ""), true);
			}
			catch { }
		}

		// --- generic prefixes for the curated VRChat methods (any thread) ---
		private static void Gen0(object __instance, MethodBase __originalMethod) { Hit(__instance, __originalMethod, null); }
		private static void Gen1(object __instance, MethodBase __originalMethod, object __0) { Hit(__instance, __originalMethod, new[] { __0 }); }
		private static void Gen2(object __instance, MethodBase __originalMethod, object __0, object __1) { Hit(__instance, __originalMethod, new[] { __0, __1 }); }
		private static void Gen3(object __instance, MethodBase __originalMethod, object __0, object __1, object __2) { Hit(__instance, __originalMethod, new[] { __0, __1, __2 }); }
		private static void Gen4(object __instance, MethodBase __originalMethod, object __0, object __1, object __2, object __3) { Hit(__instance, __originalMethod, new[] { __0, __1, __2, __3 }); }
		private static void GenS0(MethodBase __originalMethod) { Hit(null, __originalMethod, null); }
		private static void GenS1(MethodBase __originalMethod, object __0) { Hit(null, __originalMethod, new[] { __0 }); }
		private static void GenS2(MethodBase __originalMethod, object __0, object __1) { Hit(null, __originalMethod, new[] { __0, __1 }); }
		private static void GenS3(MethodBase __originalMethod, object __0, object __1, object __2) { Hit(null, __originalMethod, new[] { __0, __1, __2 }); }
		private static void GenS4(MethodBase __originalMethod, object __0, object __1, object __2, object __3) { Hit(null, __originalMethod, new[] { __0, __1, __2, __3 }); }
		private static void GenS5(MethodBase __originalMethod, object __0, object __1, object __2, object __3, object __4) { Hit(null, __originalMethod, new[] { __0, __1, __2, __3, __4 }); }

		private sealed class Rate { public int Second, Count, NoisySeconds; public bool MutedLogged, Dead; }
		private static readonly Dictionary<string, Rate> _rates = new Dictionary<string, Rate>(StringComparer.Ordinal);
		private static readonly object RateGate = new object();

		private static void Hit(object inst, MethodBase m, object[] args)
		{
			try
			{
				if (!_armed || m == null) return;
				string declName = m.DeclaringType != null ? m.DeclaringType.Name : "?";
				string key = declName + "." + m.Name;
				int sec = Environment.TickCount / 1000;
				Rate r;
				lock (RateGate)
				{
					if (!_rates.TryGetValue(key, out r)) _rates[key] = r = new Rate { Second = sec };
					if (r.Dead) return;
					if (r.Second != sec) { r.Second = sec; r.Count = 0; r.MutedLogged = false; }
					if (++r.Count > MutePerSecond)
					{
						if (!r.MutedLogged)
						{
							r.MutedLogged = true;
							if (++r.NoisySeconds >= 3) { r.Dead = true; Log(key + " fires more than " + MutePerSecond + "×/s for the 3rd second — muted for the rest of the session (a per-frame method, not an event)."); }
							else Log(key + " fires more than " + MutePerSecond + "×/s — further calls this second are not logged.");
						}
						return;
					}
				}
				// Runtime folding check: a call whose `this` is not an instance of the declaring class is
				// another method's body sharing this address. Never trust it again.
				var ib = inst as Il2CppObjectBase;
				if (ib != null)
				{
					string cn = NameOfClass(ib);
					if (!cn.StartsWith("obf@", StringComparison.Ordinal) && cn != "?" && cn != declName && cn.IndexOf(declName, StringComparison.Ordinal) < 0)
					{
						lock (RateGate) r.Dead = true;
						Log(key + " was called on a " + cn + " — its code is SHARED with another method (linker folding); muted for the rest of the session.");
						return;
					}
				}
				bool main = Environment.CurrentManagedThreadId == _mainThread;
				int pid = 0; string layer = "CALL";
				IntPtr p = Ptr(inst);
				if (p != IntPtr.Zero && _ptrOwner.TryGetValue(p, out pid)) { string l; if (_ptrLayer.TryGetValue(p, out l)) layer = "CALL/" + l; }
				else if (m.DeclaringType == typeof(VRC.ModerationManager) || m.DeclaringType == typeof(VRCNetworkingClient)) layer = "CALL/STATE-MODERATION";
				bool sync = NetworkLogModule.InsideWatchedCode == 33;
				string text = key + "(" + FormatArgs(args, main) + ")" + (sync ? " [SYNC inside OnEvent(33)]" : "") + (main ? "" : " [thread " + Environment.CurrentManagedThreadId + "]");
				Record(pid, layer, text, MsSinceEvent(), NativeStack.Capture(), main ? Time.frameCount : -1, sync);
			}
			catch { }
		}

		private static string FormatArgs(object[] args, bool main)
		{
			if (args == null || args.Length == 0) return "";
			var sb = new StringBuilder();
			for (int i = 0; i < args.Length; i++)
			{
				if (i > 0) sb.Append(", ");
				object a = args[i];
				try
				{
					if (a == null) { sb.Append("null"); continue; }
					if (a is string s) { sb.Append('"').Append(s.Length > 64 ? s.Substring(0, 64) + "…" : s).Append('"'); continue; }
					if (a is bool || a is int || a is byte || a is float || a is uint || a is short || a is long || a is double || a.GetType().IsEnum) { sb.Append(a); if (a.GetType().IsEnum) sb.Append('(').Append(Convert.ToInt64(a)).Append(')'); continue; }
					var uo = a as UnityEngine.Object;
					if (uo != null) { sb.Append(main && NativeGuard.Alive(uo) ? Describe(uo) : "<UnityObject>"); continue; }
					var b = a as Il2CppObjectBase;
					if (b != null)
					{
						string cn = NameOfClass(b);
						if (cn.IndexOf("APIUser", StringComparison.Ordinal) >= 0 && NativeGuard.Alive(b))
						{
							string id = null;
							try { var pi = a.GetType().GetProperty("id"); id = pi?.GetValue(a, null) as string; } catch { }
							sb.Append("APIUser").Append(id != null ? "(" + id + ")" : ""); continue;
						}
						if (cn.StartsWith("Dictionary", StringComparison.Ordinal))
						{
							// DECODED ONLY WHEN THE SWITCH SAYS SO -- and never off the main thread.
							//
							// I briefly made this decode unconditionally so the moderation lists could feed the [B]
							// marker. That crashed the game three times: 0xc0000005 in coreclr.dll at fault offset 0x0,
							// one second after each event 33. The reason is printed a few lines above in this very
							// module -- OnEvent(33) runs on managed thread 47, NOT the Unity main thread -- and walking
							// an il2cpp dictionary from there is exactly what the module already refused to do.
							//
							// It was also unnecessary: the switch was already on, so the payload was being decoded anyway.
							// The moderation parse is fed from the SAME guarded string, so it inherits that safety instead
							// of bypassing it.
							string dec = "";
							try
							{
								if (ModConfig.BlockDebugDecodeEvent33.Value)
								{
									string full = Event33Payload.Describe(b.Pointer);
									if (full != null) { dec = " " + full; try { Event33Moderation.Consume(full); } catch { } }
								}
							}
							catch { }
							sb.Append("Dictionary").Append(dec); continue;
						}
						sb.Append('<').Append(cn).Append('>'); continue;
					}
					sb.Append(a.GetType().Name);
				}
				catch { sb.Append('?'); }
			}
			return sb.ToString();
		}

		// ---------------------------------------------------------------- records

		private static void Record(int pid, string layer, string text, bool main)
		{
			Record(pid, layer, text, MsSinceEvent(), NativeStack.Capture(), main ? Time.frameCount : -1, NetworkLogModule.InsideWatchedCode == 33);
		}

		private static void Record(int pid, string layer, string text, double ms, string stack, int frame, bool sync)
		{
			string who = pid != 0 ? "Player=" + pid + " '" + NameOf(pid) + "'" : "(global)";
			string when = _lastEvSeq > 0 ? "+" + ms.ToString("0.0") + " ms" : "(no event 33 yet)";
			var w = _w;
			bool inWindow = w != null && !w.Ended;
			if (!inWindow && _lastEvSeq > 0 && ms > 2500) when += " [no event 33 in the last 2.5 s]";
			Log(when + " f" + frame + "  " + who + "  [" + layer + "]  " + text + "  ← " + stack);
			if (inWindow)
			{
				lock (WGate)
				{
					if (w.HookLines++ < MaxHookLines) w.Hooks.Add(new HookRec { Pid = pid, Layer = layer, Ms = ms, Text = text + "  ← " + stack, Sync = sync });
					else w.Suppressed++;
				}
				if (pid != 0 && _inst != null)
				{
					TP t;
					if (_inst._tp.TryGetValue(pid, out t))
					{
						t.Hot = true;
						string l = layer.StartsWith("CALL/", StringComparison.Ordinal) ? layer.Substring(5) : layer;
						if (!layer.StartsWith("CALL", StringComparison.Ordinal) && !t.First.ContainsKey(l)) t.First[l] = ms;
						t.Seq.Add(ms.ToString("+0.0") + " ms  [" + layer + "] " + text + "  ← " + stack);
					}
				}
			}
		}

		private static double MsSinceEvent()
		{
			long ev = _lastEvTicks;
			if (ev == 0) return 0;
			return (Stopwatch.GetTimestamp() - ev) * 1000.0 / Stopwatch.Frequency;
		}

		// ---------------------------------------------------------------- summary

		private void Summary(Window w, float now)
		{
			var sb = new StringBuilder(4096);
			int hot = 0;
			foreach (var t in _tp.Values) if (t.Hot) hot++;
			sb.Append("\n========== EVENT 33 #").Append(w.Seq).Append(" TRACE SUMMARY (").Append(w.Clock).Append(", actor ").Append(w.Sender).Append(", ").Append(w.Events).Append(" event(s) in window, ").Append(hot).Append(" player(s) affected) ==========\n");
			sb.Append("[EVENT33]\n  handler = VRCNetworkingClient.OnEvent(EventData)   synchronous duration = ").Append(w.HandlerMs >= 0 ? w.HandlerMs.ToString("0.00") + " ms" : "n/a").Append('\n');
			sb.Append("  payload = ").Append(w.Payload).Append(w.Extra).Append('\n');
			sb.Append("  changes made INSIDE the handler (before it returned) = ").Append(w.SyncChanges == null ? "unknown" : w.SyncChanges.Count == 0 ? "NONE" : string.Join("; ", w.SyncChanges.ToArray())).Append('\n');
			int syncHooks = 0; foreach (var h in w.Hooks) if (h.Sync) syncHooks++;
			sb.Append("  hooked calls fired INSIDE the handler = ").Append(syncHooks).Append('\n');
			if (hot == 0)
			{
				sb.Append("  no tracked player's representation or state changed in the window: this event 33 did not concern a remote player's avatar (or concerned the local user / a list only).\n");
				// global calls still tell what the handler did
				int shown = 0;
				foreach (var h in w.Hooks) { if (h.Pid != 0) continue; if (shown++ == 0) sb.Append("[SEQUENCE] (global calls)\n"); if (shown <= 24) sb.Append("  ").Append(h.Ms.ToString("+0.0")).Append(" ms  [").Append(h.Layer).Append("] ").Append(h.Text).Append('\n'); }
				sb.Append("==========================================");
				Log(sb.ToString());
				return;
			}
			foreach (var t in _tp.Values)
			{
				if (!t.Hot) continue;
				var last = t.Last ?? new Dictionary<string, string>();
				sb.Append("\n---------- Player=").Append(t.Pid).Append(" '").Append(t.Name).Append("' userId=").Append(Or(t.Uid)).Append(" ----------\n");
				string kind0 = Get(last, "STATE-AVATARMGR.avatarKind"), vis0 = Get(last, "STATE-AVATARMGR.visibility");
				string kindBefore = ChangedFrom(t, "STATE-AVATARMGR.avatarKind") ?? kind0, visBefore = ChangedFrom(t, "STATE-AVATARMGR.visibility") ?? vis0;
				string dir = kind0 == "Blocked" && kindBefore != "Blocked" ? "A→B  VISIBLE → HIDDEN (avatar kind became Blocked)"
					: kindBefore == "Blocked" && kind0 != "Blocked" ? "B→A  HIDDEN → VISIBLE (avatar kind left Blocked)"
					: (kindBefore != kind0 ? "OTHER (avatar kind " + kindBefore + " → " + kind0 + ")" : "no avatar-kind change (" + kind0 + ")");
				sb.Append("[EVENT33]\n  playerId = ").Append(t.Pid).Append("  (attribution: ").Append(!string.IsNullOrEmpty(t.Uid) && w.Payload != null && w.Payload.IndexOf(t.Uid, StringComparison.OrdinalIgnoreCase) >= 0 ? "payload names this userId" : "avatar/state side — this player's representation changed after the event").Append(")\n");
				sb.Append("  direction = ").Append(dir).Append('\n');
				// Every avatar-kind transition in order, with its time: a block+unblock in one window reads
				// "Custom → Blocked (+26 ms) → Loading (+3022 ms) → Custom (+…)" instead of "no change".
				var kinds = new StringBuilder();
				foreach (string s in t.Seq)
				{
					int k = s.IndexOf("STATE-AVATARMGR.avatarKind: ", StringComparison.Ordinal);
					if (k < 0) continue;
					int sp = s.IndexOf(" ms", StringComparison.Ordinal);
					string when = sp > 0 ? s.Substring(0, sp).Trim() : "?";
					string tr = s.Substring(k + "STATE-AVATARMGR.avatarKind: ".Length);
					int arrow = tr.IndexOf(" -> ", StringComparison.Ordinal);
					if (kinds.Length == 0 && arrow > 0) kinds.Append(tr.Substring(0, arrow));
					if (arrow > 0) kinds.Append(" → ").Append(tr.Substring(arrow + 4)).Append(" (").Append(when).Append(" ms)");
				}
				if (kinds.Length > 0) sb.Append("  avatar kind transitions = ").Append(kinds).Append('\n');
				sb.Append("[PLAYER STATE]\n");
				sb.Append("  VRCAvatarManager: kind ").Append(kindBefore).Append(" → ").Append(kind0).Append(" | visibility ").Append(visBefore).Append(" → ").Append(vis0).Append(" | blockReason ").Append(Get(last, "STATE-AVATARMGR.blockReason")).Append('\n');
				// Current moderation-side values, even when nothing changed: tells "not tracked" from "unchanged".
				sb.Append("  moderation now:");
				int modNow = 0;
				foreach (var kv in last) if (kv.Key.StartsWith("STATE-MODERATION.", StringComparison.Ordinal) && !kv.Key.StartsWith("STATE-MODERATION.ModerationManager.", StringComparison.Ordinal)) { sb.Append(' ').Append(kv.Key.Substring(17)).Append('=').Append(kv.Value); modNow++; }
				if (modNow == 0) sb.Append(" (no per-player moderation key sampled — FieldOffsetFix not verified, or the manager was null)");
				sb.Append('\n');
				AppendChanges(sb, t, "STATE-MODERATION", "  moderation (VRC.ModerationManager / PlayerManager)");
				AppendChanges(sb, t, "STATE-PLAYERMGR", "  PlayerManager");
				AppendChanges(sb, t, "STATE-AVATARMGR", "  VRCAvatarManager flags");
				AppendChanges(sb, t, "STATE-PLAYER", "  VRC.Player / VRCPlayer flags");
				sb.Append("  previous = ").Append(kindBefore).Append('/').Append(visBefore).Append("   current = ").Append(kind0).Append('/').Append(vis0).Append('\n');
				sb.Append("[AVATAR]\n  GameObject = ").Append(Get(last, "AVATAR.object")).Append("   (at window start: ").Append(t.AvatarAtStart).Append(")\n");
				sb.Append("  activeSelf = ").Append(Get(last, "AVATAR.activeSelf")).Append("   activeInHierarchy = ").Append(Get(last, "AVATAR.activeInHierarchy")).Append("   descriptor = ").Append(Get(last, "AVATAR.descriptor")).Append("   components = ").Append(Get(last, "AVATAR.components")).Append("   animator = ").Append(Get(last, "ANIMATOR.object")).Append('\n');
				sb.Append("[RENDERERS]\n  total = ").Append(Get(last, "RENDERERS.total")).Append("   enabled = ").Append(Get(last, "RENDERERS.enabled")).Append("   forceRenderingOff = ").Append(Get(last, "RENDERERS.forceRenderingOff")).Append("   gameObjectActive = ").Append(Get(last, "RENDERERS.gameObjectActive")).Append("   drawable = ").Append(Get(last, "RENDERERS.drawable")).Append("   destroyed-in-window = ").Append(Get(last, "RENDERERS.destroyed", "0")).Append('\n');
				AppendChanges(sb, t, "RENDERERS", "  renderer changes");
				sb.Append("[NAMEPLATE]\n  container active = ").Append(Get(last, "NAMEPLATE.container.activeSelf")).Append('/').Append(Get(last, "NAMEPLATE.container.activeInHierarchy")).Append("   PlayerNameplate.enabled = ").Append(Get(last, "NAMEPLATE.PlayerNameplate.enabled")).Append("   alpha = ").Append(Get(last, "NAMEPLATE.CanvasGroup_0.alpha")).Append('/').Append(Get(last, "NAMEPLATE.CanvasGroup_1.alpha")).Append('/').Append(Get(last, "NAMEPLATE.CanvasGroup_2.alpha")).Append("   positioner = ").Append(Get(last, "NAMEPLATE.positioner.enabled")).Append('\n');
				AppendChanges(sb, t, "NAMEPLATE", "  nameplate changes");
				sb.Append("[PLAYER UI]\n  nameplate children active = ").Append(Get(last, "PLAYER-UI.nameplateChildrenActive")).Append("   chat bubble = ").Append(Get(last, "PLAYER-UI.chatBubble.activeInHierarchy")).Append('\n');
				AppendChanges(sb, t, "PLAYER-UI", "  UI changes");
				sb.Append("[TRANSFORM]\n  position = ").Append(t.HasPos ? V(t.LastPos) : "n/a").Append("   netPos = ").Append(t.HasNet ? V(t.LastNet) : "n/a").Append("   moved in window: root ").Append(t.MovedRoot.ToString("0.00")).Append(" m, net ").Append(t.MovedNet.ToString("0.00")).Append(" m, head-vs-root ").Append(t.MovedHead.ToString("0.00")).Append(" m\n");
				sb.Append("[IK] ");
				foreach (var kv in last) if (kv.Key.StartsWith("IK.", StringComparison.Ordinal)) sb.Append(kv.Key.Substring(3)).Append('=').Append(kv.Value).Append("  ");
				sb.Append('\n');
				sb.Append("[AUDIO] ").Append(Get(last, "AUDIO.sources")).Append("   USpeaker.enabled = ").Append(Get(last, "AUDIO.USpeaker.enabled")).Append('\n');
				AppendChanges(sb, t, "AUDIO", "  audio changes");
				// timing
				sb.Append("[TIMING]\n  Event33 → handler return = ").Append(w.HandlerMs >= 0 ? w.HandlerMs.ToString("0.00") + " ms" : "n/a").Append('\n');
				string[] order = { "STATE-MODERATION", "STATE-PLAYERMGR", "STATE-AVATARMGR", "STATE-PLAYER", "RENDERERS", "AVATAR", "AVATAR-CHILD", "ANIMATOR", "IK", "NAMEPLATE", "PLAYER-UI", "AUDIO", "ROOT" };
				foreach (string l in order) { double ms; sb.Append("  Event33 → first ").Append(l).Append(" change = ").Append(t.First.TryGetValue(l, out ms) ? ms.ToString("0") + " ms" : "none").Append('\n'); }
				double stateMs = FirstOf(t, "STATE-AVATARMGR", "STATE-PLAYER", "STATE-MODERATION"), rendMs = FirstOf(t, "RENDERERS"), avMs = FirstOf(t, "AVATAR"), npMs = FirstOf(t, "NAMEPLATE", "PLAYER-UI");
				if (stateMs >= 0 && rendMs >= 0) sb.Append("  state change → renderer change = ").Append((rendMs - stateMs).ToString("0")).Append(" ms\n");
				if (rendMs >= 0 && avMs >= 0) sb.Append("  renderer change → avatar object change = ").Append((avMs - rendMs).ToString("0")).Append(" ms\n");
				if (avMs >= 0 && npMs >= 0) sb.Append("  avatar object change → nameplate/UI change = ").Append((npMs - avMs).ToString("0")).Append(" ms\n");
				// sequence
				sb.Append("[SEQUENCE]\n  +0.0 ms  event 33 received from actor ").Append(w.Sender).Append('\n');
				if (w.HandlerMs >= 0) sb.Append("  +").Append(w.HandlerMs.ToString("0.0")).Append(" ms  OnEvent handler returned\n");
				var seq = new List<KeyValuePair<double, string>>();
				foreach (string s in t.Seq) { double ms; int sp = s.IndexOf(" ms", StringComparison.Ordinal); if (sp > 0 && double.TryParse(s.Substring(0, sp).Replace("+", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ms)) seq.Add(new KeyValuePair<double, string>(ms, s)); else seq.Add(new KeyValuePair<double, string>(0, s)); }
				foreach (var h in w.Hooks) if (h.Pid == 0 && h.Layer.StartsWith("CALL", StringComparison.Ordinal)) seq.Add(new KeyValuePair<double, string>(h.Ms, h.Ms.ToString("+0.0") + " ms  [" + h.Layer + "] " + h.Text));
				seq.Sort((a, b) => a.Key.CompareTo(b.Key));
				int lines = 0;
				foreach (var e in seq) { if (lines++ >= 60) { sb.Append("  … ").Append(seq.Count - 60).Append(" more\n"); break; } sb.Append("  ").Append(e.Value).Append('\n'); }
				// verdict
				sb.Append("[VERDICT]\n");
				int syncHere = 0; foreach (var h in w.Hooks) if (h.Sync && (h.Pid == t.Pid)) syncHere++;
				bool syncState = false; if (w.SyncChanges != null) foreach (string s in w.SyncChanges) if (s.StartsWith("Player=" + t.Pid + " ", StringComparison.Ordinal)) syncState = true;
				if (syncHere > 0 || syncState)
					sb.Append("  DIRECT: the OnEvent(33) handler itself touched this player before returning (").Append(syncHere).Append(" hooked call(s)").Append(syncState ? ", state fields changed inside it" : "").Append("); see the [SYNC] entries above.\n");
				else
				{
					double firstAny = -1; string firstLayer = "?";
					foreach (var kv in t.First) if (firstAny < 0 || kv.Value < firstAny) { firstAny = kv.Value; firstLayer = kv.Key; }
					sb.Append("  INDIRECT: nothing about this player changed while the handler ran; the first change (").Append(firstLayer).Append(") came ").Append(firstAny >= 0 ? firstAny.ToString("0") + " ms" : "?").Append(" later, from the callers shown in the [SEQUENCE] stacks — i.e. event 33 → moderation state → VRCAvatarManager re-selection → avatar switch pipeline, not a hide performed by the event handler.\n");
				}
				if (!t.First.ContainsKey("NAMEPLATE") && !t.First.ContainsKey("PLAYER-UI")) sb.Append("  NAMEPLATE/UI: unchanged through the window — it has its own lifecycle (NameplateManager side), it is not torn down with the avatar.\n");
				else sb.Append("  NAMEPLATE/UI: changed at +").Append(FirstOf(t, "NAMEPLATE", "PLAYER-UI").ToString("0")).Append(" ms — see [NAMEPLATE]/[PLAYER UI] above.\n");
				var survive = new List<string>();
				if (Get(last, "ROOT.activeInHierarchy") == "true") survive.Add("player root (active)");
				if (t.Player != null) survive.Add("VRC.Player"); if (t.VP != null) survive.Add("VRCPlayer"); if (t.Mgr != null) survive.Add("VRCAvatarManager");
				if (Get(last, "NAMEPLATE.container.activeInHierarchy") == "true") survive.Add("nameplate container");
				foreach (var kv in last) if (kv.Key.StartsWith("IK.", StringComparison.Ordinal) && kv.Value == "true") survive.Add(kv.Key.Substring(3).Replace(".enabled", ""));
				if (Get(last, "AUDIO.USpeaker.enabled") == "true") survive.Add("USpeaker");
				if (t.MovedNet > 0.02f) survive.Add("network position updates (" + t.MovedNet.ToString("0.00") + " m)");
				sb.Append("  STILL PRESENT at the end of the window: ").Append(survive.Count > 0 ? string.Join(", ", survive.ToArray()) : "nothing tracked").Append('\n');
				sb.Append("  avatar at end: ").Append(Get(last, "AVATAR.object")).Append(", renderers ").Append(Get(last, "RENDERERS.total")).Append(" (drawable ").Append(Get(last, "RENDERERS.drawable")).Append(")\n");
			}
			if (w.Suppressed > 0) sb.Append('\n').Append(w.Suppressed).Append(" hook line(s) were suppressed by the per-window cap.\n");
			sb.Append("==========================================");
			Log(sb.ToString());
		}

		private static string Get(Dictionary<string, string> d, string k, string dflt = "n/a") { string v; return d.TryGetValue(k, out v) ? v : dflt; }
		private static string ChangedFrom(TP t, string key) { KeyValuePair<string, string> c; return t.Changed.TryGetValue(key, out c) ? c.Key : null; }
		private static double FirstOf(TP t, params string[] layers) { double best = -1; foreach (string l in layers) { double ms; if (t.First.TryGetValue(l, out ms) && (best < 0 || ms < best)) best = ms; } return best; }

		private static void AppendChanges(StringBuilder sb, TP t, string layerPrefix, string title)
		{
			int n = 0;
			int back = 0;
			foreach (var kv in t.Changed)
			{
				if (!kv.Key.StartsWith(layerPrefix, StringComparison.Ordinal)) continue;
				if (kv.Value.Key == kv.Value.Value) { back++; continue; }   // flipped and came back inside the window: the [SEQUENCE] has it
				if (n++ == 0) sb.Append(title).Append(" changed in window:\n");
				if (n <= 30) sb.Append("    ").Append(kv.Key).Append(": ").Append(kv.Value.Key).Append(" → ").Append(kv.Value.Value).Append('\n');
			}
			if (n > 30) sb.Append("    … ").Append(n - 30).Append(" more\n");
			if (n == 0) sb.Append(title).Append(": no net change in window").Append(back > 0 ? " (" + back + " member(s) flipped and came back — see [SEQUENCE])" : "").Append('\n');
			else if (back > 0) sb.Append("    (+ ").Append(back).Append(" member(s) that flipped and came back — see [SEQUENCE])\n");
		}

		// ---------------------------------------------------------------- helpers

		private static string AvatarDesc(TP t) { return t.Avatar != null ? "#" + t.AvatarId + " '" + t.AvatarName + "' (" + t.Rend.Count + " renderers)" : "NONE"; }
		private static string NameOf(int pid) { string n; return _names.TryGetValue(pid, out n) ? n : "?"; }
		private static string Or(string s) => string.IsNullOrEmpty(s) ? "unavailable" : s;
		private static string B(bool b) => b ? "true" : "false";
		private static string V(Vector3 v) => "(" + v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00") + ")";
		private static int Id(UnityEngine.Object o) { try { return o != null ? o.GetInstanceID() : 0; } catch { return 0; } }
		private static string Nm(GameObject g) { try { return g != null ? (g.name ?? "") : "null"; } catch { return "?"; } }
		private static string LocalUid() { try { return VaTagsModule.LocalUserId() ?? ""; } catch { return ""; } }

		private static string Describe(UnityEngine.Object o)
		{
			try
			{
				if (o == null) return "null";
				string n = o.name ?? ""; if (n.Length > 40) n = n.Substring(0, 40);
				var go = o.TryCast<GameObject>();
				if (go != null) return "GameObject '" + n + "' #" + go.GetInstanceID() + (go.activeSelf ? "" : " (inactive)");
				var c = o.TryCast<Component>();
				if (c != null) return NameOfClass(c) + " '" + n + "' #" + c.GetInstanceID();
				return NameOfClass(o) + " '" + n + "' #" + o.GetInstanceID();
			}
			catch { return "<object>"; }
		}

		private static readonly Dictionary<IntPtr, string> ClassNames = new Dictionary<IntPtr, string>();
		private static string NameOfClass(Il2CppObjectBase o)
		{
			try
			{
				IntPtr p = o.Pointer;
				if (p == IntPtr.Zero || !NativeGuard.IsReadable(p, 8)) return o.GetType().Name;
				IntPtr k = IL2CPP.il2cpp_object_get_class(p);
				string n;
				lock (ClassNames)
				{
					if (ClassNames.TryGetValue(k, out n)) return n;
					n = null;
					try { n = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(k)); } catch { }
					bool readable = !string.IsNullOrEmpty(n) && n.Length <= 40;
					if (readable) for (int i = 0; i < n.Length; i++) if (n[i] > 127) { readable = false; break; }
					if (!readable) { string m = o.GetType().Name; n = (m == "Component" || m == "Behaviour" || m == "MonoBehaviour" || m == "Object") ? "obf@" + k.ToString("X") : m; }
					ClassNames[k] = n;
				}
				return n;
			}
			catch { return "?"; }
		}

		private static GameObject FindAvatarRoot(GameObject root)
		{
			try { var d = root.GetComponentInChildren<VRCAvatarDescriptor>(true); if (d != null) return d.gameObject; } catch { }
			return null;
		}

		private static void ResolveTypes() { }

		private static IEnumerable<Type> StackTypes()
		{
			var list = new List<Type>
			{
				typeof(VRCNetworkingClient), typeof(VRC.ModerationManager), typeof(PlayerManager), typeof(VRC.Player), typeof(VRCPlayer), typeof(VRCAvatarManager),
				typeof(PlayerNameplate), typeof(PlayerNameplatePositioner), typeof(NameplateContainer), typeof(IkController), typeof(VRCVrIkController), typeof(VRCFbbIkController),
				typeof(VRCPlayerApi), typeof(VRC.SDKBase.Networking), typeof(VRC.Core.ApiPlayerModeration), typeof(VRC.Core.APIUser), typeof(VRC.Core.ApiAvatar),
				typeof(UnityEngine.Object), typeof(GameObject), typeof(Component), typeof(Behaviour), typeof(MonoBehaviour), typeof(Renderer), typeof(Transform), typeof(Animator),
			};
			string[] extra = { "Photon.Realtime.LoadBalancingClient", "Photon.Client.PhotonPeer", "Photon.Client.PeerBase", "Photon.Client.EnetPeer", "VRC.Networking.NetworkManager", "VRC.Networking.FlatBufferNetworkSerializer",
				"USpeaker", "AvatarPlayableController", "AnimatorControllerManager", "VRC_AnimationController", "VRCMotionState", "ChatBubbleDisplay", "AssetBundleDownloadManager", "VRC.VRCPerformanceManager", "VRCGhostAvatar", "AvatarClone", "NameplatePositioner", "PlayerAudioManager", "VRC.Core.AssetBundleDownload", "VRC.Core.APIUser" };
			foreach (string n in extra)
			{
				Type t = null;
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { t = asm.GetType(n, false); } catch { t = null; } if (t != null) break; }
				if (t != null) list.Add(t);
			}
			return list;
		}

		private static void Log(string s) { try { VRChatArchiveModPlugin.Logger.LogInfo(Tag + s); } catch { } }
	}
}
