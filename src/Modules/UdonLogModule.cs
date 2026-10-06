using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// UDON CONSOLE — a live log of the Udon events happening around you.
	//
	// Every Udon entry point in the game funnels through UdonBehaviour.RunProgram(string),
	// so a single Harmony postfix there sees ALL of it: interactions, pickups, custom
	// network events, serialization, world scripts. We attribute each event to a player
	// with VRChat's own Networking.GetOwner(), which is the documented way to ask who owns
	// a networked object — usually the person whose action produced the event.
	//
	// This only OBSERVES events your client already receives and executes; it never sends,
	// triggers, blocks or alters a single one. Same nature as the ESP and radar modules.
	//
	// Performance matters here: RunProgram fires constantly (_update alone runs per Udon
	// object per frame). The postfix therefore does the cheapest possible work — an enabled
	// check and a HashSet lookup — before anything else, and per-frame events are dropped
	// unless you explicitly ask for them.
	public class UdonLogModule : IModule
	{
		public override string Name => "UdonLog";

		public const int Capacity = 400;          // ring buffer size

		public sealed class Entry
		{
			public float Time;
			public string Clock;
			public string User;
			public string UserColor;
			public string Obj;
			public string Event;
			public int Repeats = 1;
		}

		// Ring buffer, newest last. Read by the menu on the main thread.
		private static readonly List<Entry> Log = new List<Entry>(Capacity);
		public static IReadOnlyList<Entry> Entries => Log;
		public static bool Paused;
		public static string Filter = "";
		public static int TotalSeen;
		private static float _rateWindow;
		private static int _rateCount;
		public static int PerSecond;

		// Events that fire every frame for every behaviour. Logging these would bury the
		// interesting ones and cost real performance, so they are off unless asked for.
		private static readonly HashSet<string> FrameEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"_update", "_lateUpdate", "_fixedUpdate", "_postLateUpdate",
			"_onPreRender", "_onPreCull", "_onPostRender", "_onRenderObject",
			"_onWillRenderObject", "_onAnimatorIK", "_onAnimatorMove",
			"_onBecameVisible", "_onBecameInvisible", "_onAudioFilterRead",
			"_onTriggerStay", "_onTriggerStay2D", "_onCollisionStay", "_onCollisionStay2D",
			"_onDrawGizmos", "_onDrawGizmosSelected", "_onValidate",
		};

		// Pure polling noise: property reads and a few per-frame custom pumps that flood the
		// log without telling you anything a human cares about.
		private static readonly HashSet<string> NoiseEvents = new HashSet<string>(StringComparer.Ordinal)
		{
			"UpdateProcess", "Update", "LateUpdate", "FixedUpdate", "Process",
			"_update", "_lateUpdate", "_fixedUpdate", "Tick", "OnTick", "Refresh",
		};

		// Kept when "interesting only" is on. VRChat SDK events start with '_' (_interact,
		// _onPickup, _onPlayerJoined, custom-network events, serialization); world scripts use
		// readable custom names. Property accessors and numeric internal jumps are dropped.
		private static bool IsInteresting(string ev)
		{
			if (ev.Length <= 2) return false;                                            // "6", "7", cryptic jumps
			if (ev.StartsWith("get_", StringComparison.Ordinal)) return false;
			if (ev.StartsWith("set_", StringComparison.Ordinal)) return false;
			if (ev.StartsWith("Get", StringComparison.Ordinal) && ev.Length > 3 && char.IsUpper(ev[3])) return false;
			if (NoiseEvents.Contains(ev)) return false;

			// SUFFIX rule. The old check kept anything starting with '_' because that is the VRChat
			// SDK convention — but world scripts name their per-frame pumps the same way, so
			// _FixedTick, _ClockUpdate and checkDistanceLoop poured in at ~500 events a second and
			// buried every real interaction. A name ending in Tick/Update/Loop/Poll/Sync/Refresh is
			// a pump whoever wrote it, leading underscore or not.
			if (EndsWithAny(ev, "Tick", "Update", "Loop", "Poll", "Sync", "Refresh", "Timer")) return false;

			if (ev[0] == '_') return true;                                               // real SDK lifecycle / interaction
			for (int i = 0; i < ev.Length; i++) if (!char.IsDigit(ev[i])) return true;   // has a letter -> a real name
			return false;                                                                // all-digit = internal jump
		}

		private static bool EndsWithAny(string s, params string[] suffixes)
		{
			foreach (string suf in suffixes)
				if (s.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		// ADAPTIVE MUTE. Name heuristics cannot know every world's pump, so anything that fires
		// more than this many times in a second is muted for a while and reported once. A human
		// reading a console cannot use 300 lines/second of the same event anyway.
		private const int FloodPerSecond = 25;
		private static readonly Dictionary<string, int> _burst = new Dictionary<string, int>(StringComparer.Ordinal);
		private static readonly Dictionary<string, float> _muted = new Dictionary<string, float>(StringComparer.Ordinal);
		private static float _burstWindow;

		private static bool Flooding(string ev, float now)
		{
			if (_muted.TryGetValue(ev, out float until))
			{
				if (now < until) return true;
				_muted.Remove(ev);
			}

			if (now - _burstWindow >= 1f) { _burstWindow = now; _burst.Clear(); }
			_burst.TryGetValue(ev, out int n);
			n++;
			_burst[ev] = n;
			if (n < FloodPerSecond) return false;

			_muted[ev] = now + 30f;                    // muted for half a minute, then re-evaluated
			VRChatArchiveModPlugin.Logger.LogInfo($"[UdonLog] '{ev}' fired {n}x in a second — muted for 30s (per-frame pump).");
			return true;
		}

		private static bool _hooked;

		// HOOKING IS NOT A ONE-SHOT. OnInitialize runs before VRChat's own assemblies are
		// guaranteed loaded, and this game's own updates have renamed and relocated types under us
		// more than once. So a failed attach is not the end: OnUpdate keeps retrying on a slow
		// cadence until the patch sticks, and says so ONCE either way instead of dying in silence —
		// a dead RunProgram hook takes the crasher guard, the console AND the Udon Manager's event
		// feed down with it, so "it silently stopped working" was the exact failure to design out.
		private static int _hookAttempts;
		private static float _nextHookTry;
		private static bool _gaveUpLogged;
		private const int MaxHookAttempts = 40;   // ~2 min at the 3s cadence, then it stops trying

		// Read by the diagnostics health check: whether the RunProgram patch actually installed.
		// An empty console is NOT a failure — a world can simply contain no UdonBehaviours.
		public static bool HookAttached => _hooked;

		public override void OnInitialize()
		{
			try
			{
				Hook();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[UdonLog] init failed: {e}"); }
		}

		private static void Hook()
		{
			if (_hooked) return;
			int patched = 0;
			try
			{
				Type ub = Type.GetType("VRC.Udon.UdonBehaviour, VRC.Udon", false)
				          ?? FindType("VRC.Udon.UdonBehaviour");
				if (ub == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[UdonLog] UdonBehaviour type not found — the console will stay empty (report so we can re-target).");
					return;
				}

				var pre  = new HarmonyMethod(typeof(UdonLogModule).GetMethod(nameof(RunProgramPrefix), BindingFlags.Static | BindingFlags.NonPublic));
				var post = new HarmonyMethod(typeof(UdonLogModule).GetMethod(nameof(RunProgramPostfix), BindingFlags.Static | BindingFlags.NonPublic));
				var preU = new HarmonyMethod(typeof(UdonLogModule).GetMethod(nameof(RunProgramUintPrefix), BindingFlags.Static | BindingFlags.NonPublic));

				// NonPublic AND the base hierarchy. RunProgram(string) is the one entry point every Udon
				// event funnels through, but which type in the hierarchy declares it, and whether it is
				// public, has not been constant across game builds. Missing it means the whole subsystem
				// silently does nothing, so the search is deliberately wider than "public on this type".
				//
				// TWO OVERLOADS, BOTH PATCHED. RunProgram(string) resolves a name to an entry address and
				// then calls RunProgram(uint) — but the game also calls the uint overload DIRECTLY for hot
				// lifecycle events and for entry points it has already resolved and cached. Those calls
				// never carry a name, so the string prefix never sees them, and for a long time PANIC
				// (BlockAll) "stopped everything" except the very events a crasher rides on. The uint
				// prefix has no name to filter on, so it does exactly one thing: under BlockAll it drops
				// the call. The console and the name/rate guards stay on the string overload, where the
				// name is.
				const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
				var seen = new HashSet<string>(StringComparer.Ordinal);
				int patchedUint = 0;
				for (Type t = ub; t != null && t != typeof(object); t = t.BaseType)
				{
					MethodInfo[] methods;
					try { methods = t.GetMethods(Flags); } catch { continue; }
					foreach (var m in methods)
					{
						if (m.Name != "RunProgram") continue;
						var ps = m.GetParameters();
						if (ps.Length != 1) continue;
						if (!seen.Add(m.DeclaringType?.FullName + "::" + m.ToString())) continue;   // one type can surface a base method twice
						if (ps[0].ParameterType == typeof(string))
						{
							try { VRChatArchiveModPlugin.HarmonyInstance.Patch(m, prefix: pre, postfix: post); patched++; }
							catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[UdonLog] could not patch RunProgram(string): {e.Message}"); }
						}
						else if (ps[0].ParameterType == typeof(uint))
						{
							try { VRChatArchiveModPlugin.HarmonyInstance.Patch(m, prefix: preU); patchedUint++; }
							catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[UdonLog] could not patch RunProgram(uint): {e.Message}"); }
						}
					}
				}
				// Armed means the STRING overload is in: that is where the console, the rate guard and the
				// name list live. The uint overload only matters for PANIC and is reported on its own so a
				// build where it went missing is visible in the log instead of silently weakening BlockAll.
				_hooked = patched > 0;
				VRChatArchiveModPlugin.Logger.LogInfo(_hooked
					? $"[UdonLog] armed — hooked {patched} RunProgram(string) and {patchedUint} RunProgram(uint) entry point(s)."
					: $"[UdonLog] no RunProgram(string) entry point patched ({patchedUint} uint) — the console will stay empty.");
				if (_hooked && patchedUint == 0)
					VRChatArchiveModPlugin.Logger.LogWarning("[UdonLog] RunProgram(uint) not found — PANIC (BlockAll) will not stop cached/lifecycle entry points on this build.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[UdonLog] hook install failed: {e}"); }
		}

		// ---------------------------------------------------------------- anti-udon

		// Blocked events, and why: this is CLIENT-SIDE SELF-DEFENCE. Returning false from the
		// prefix makes our own client skip running that Udon event; it changes nothing for anybody
		// else and sends nothing. It is the same idea as the avatar anti-crash, applied to world
		// scripts.
		//
		// Honest trade-off, which is why the blanket switch is OFF by default: Udon IS the world.
		// Block everything and doors, pens, video players and seats stop working FOR YOU, and you
		// desync from what everyone else's client is running. The useful protection is the rate
		// guard below, which only stops events behaving like a crasher.
		public static int BlockedTotal { get; private set; }
		public static string LastBlocked = "";

		// Configurable, because "far above any legitimate pump" is not true of every world: a busy
		// video player polls get_VideoPlayerType at exactly this kind of rate, and suspending it just
		// breaks the video for the person running the guard. See ModConfig.UdonCrasherPerSecond.
		private const int CrasherPerSecondDefault = 400;
		private static int CrasherPerSecond
		{
			get
			{
				try { return Mathf.Max(50, ModConfig.UdonCrasherPerSecond.Value); }
				catch { return CrasherPerSecondDefault; }
			}
		}
		private static readonly Dictionary<string, int> _guardCount = new Dictionary<string, int>(StringComparer.Ordinal);
		private static readonly Dictionary<string, float> _guardUntil = new Dictionary<string, float>(StringComparer.Ordinal);
		private static float _guardWindow;

		// THE GLOBAL NET. The per-event guard above counts each event NAME on its own, so a crasher
		// that sprays its calls across a hundred different names never makes any single counter reach
		// the crasher tier — every name looks merely busy. This counts the TOTAL instead: when more
		// Udon events fire in one second than any real world produces, the whole dispatch is treated
		// as hostile and every non-lifecycle event is suspended for a few seconds. Lifecycle events
		// (join/leave/start) still run so the world does not wedge.
		private static int _floodCount;
		private static float _floodWindow;
		private static float _floodUntil;

		private static int FloodCeiling
		{
			get
			{
				try { int v = ModConfig.UdonFloodPerSecond.Value; return v <= 0 ? 0 : Mathf.Max(500, v); }
				catch { return 2500; }
			}
		}

		// Lifecycle events that must keep running even under the blanket block, otherwise the world
		// cannot even tell you joined and some scripts wedge permanently.
		private static readonly HashSet<string> NeverBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"_onPlayerJoined", "_onPlayerLeft", "_start", "_onEnable", "_onDisable", "_onDestroy",
		};

		// NAME LIST. The rate guard only catches an event that behaves like a crasher; a world
		// script that hurts you at a sane rate (a forced teleport, a seat grab, a screen-blanking
		// toggle) never trips it. This is the surgical answer: block exactly the names you saw in
		// the EVENTS console, nothing else. Parsed ONCE from ModConfig.UdonBlockNames and re-parsed
		// only when that string changes (checked once per frame in RefreshFlags, never per event).
		// NeverBlock still wins — a name on the list cannot wedge the world's join/leave/start.
		private static readonly HashSet<string> BlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static string _blockNamesRaw;
		private static bool _fNames;    // BlockNames.Count > 0, cached so the hot path reads one bool

		private static void RefreshBlockNames()
		{
			string raw;
			try { raw = ModConfig.UdonBlockNames.Value ?? ""; } catch { return; }
			if (string.Equals(raw, _blockNamesRaw, StringComparison.Ordinal)) return;
			_blockNamesRaw = raw;
			BlockNames.Clear();
			foreach (string part in raw.Split(','))
			{
				string n = part.Trim();
				if (n.Length > 0) BlockNames.Add(n);
			}
			_fNames = BlockNames.Count > 0;
			VRChatArchiveModPlugin.Logger.LogInfo($"[UdonLog] block list: {BlockNames.Count} event name(s).");
		}

		private static bool ShouldBlock(string ev)
		{
			try
			{
				if (string.IsNullOrEmpty(ev)) return false;
				if (NeverBlock.Contains(ev)) return false;

				if (ModConfig.UdonBlockAll.Value)
				{
					Blocked(ev);
					return true;
				}

				// Explicit name list: independent of the crasher guard, so you can keep the rate
				// logic off and still pin a single hostile event.
				if (_fNames && BlockNames.Contains(ev))
				{
					Blocked(ev);
					return true;
				}

				if (!ModConfig.UdonBlockCrashers.Value) return false;

				float now = VaClock.Now;

				// Global flood net first: while a flood is being ridden out, every non-lifecycle event
				// is suspended regardless of its own per-name rate.
				int ceiling = FloodCeiling;
				if (ceiling > 0)
				{
					if (now < _floodUntil) { Blocked(ev); return true; }
					if (now - _floodWindow >= 1f) { _floodWindow = now; _floodCount = 0; }
					if (++_floodCount >= ceiling)
					{
						_floodUntil = now + 5f;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[UdonLog] GLOBAL FLOOD — " + _floodCount + " Udon events in one second (ceiling " + ceiling
							+ "). Suspending non-lifecycle events for 5s.");
						Blocked(ev);
						return true;
					}
				}

				if (_guardUntil.TryGetValue(ev, out float until))
				{
					if (now < until) { Blocked(ev); return true; }
					_guardUntil.Remove(ev);
				}

				if (now - _guardWindow >= 1f) { _guardWindow = now; _guardCount.Clear(); }
				_guardCount.TryGetValue(ev, out int n);
				n++;
				_guardCount[ev] = n;
				if (n < CrasherPerSecond) return false;

				// Crasher-tier rate: suspend this ONE event for a while, not the whole world.
				_guardUntil[ev] = now + 10f;
				VRChatArchiveModPlugin.Logger.LogWarning(
					$"[UdonLog] BLOCKED '{ev}' — {n} calls in one second (crasher-tier). Suspended 10s.");
				Blocked(ev);
				return true;
			}
			catch { return false; }
		}

		private static void Blocked(string ev)
		{
			BlockedTotal++;
			LastBlocked = ev;
		}

		// Harmony PREFIX: returning false skips VRChat's own RunProgram for this call, i.e. our
		// client does not execute that Udon event. Kept as cheap as the postfix.
		private static bool RunProgramPrefix(string __0)
		{
			try
			{
				if (!_fNames && !_fBlockAll && !_fBlockCrashers) return true;   // cached in RefreshFlags
				return !ShouldBlock(__0);
			}
			catch { return true; }   // never let the guard itself break the world
		}

		// Harmony PREFIX on RunProgram(uint). No name arrives here — only an already-resolved entry
		// address — so there is nothing for the name list or the rate guard to key on, and NeverBlock
		// cannot be honoured either. It therefore does the one thing PANIC promises: under BlockAll
		// NOTHING runs, including the cached lifecycle entry points that used to slip past the string
		// prefix. Counted in BlockedTotal; LastBlocked is left to the string path, which knows names.
		private static bool RunProgramUintPrefix()
		{
			try
			{
				if (!_fBlockAll) return true;   // cached in RefreshFlags; see the note on _fBlockAll
				BlockedTotal++;
				return false;
			}
			catch { return true; }   // never let the guard itself break the world
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { var t = asm.GetType(full, false); if (t != null) return t; } catch { }
			}
			return null;
		}

		// Harmony postfix. Runs on the main thread inside VRChat's own Udon dispatch, so it
		// must stay extremely cheap: bail out before doing anything measurable.
		// Cached once per frame from OnUpdate. This postfix runs for EVERY Udon event in the world —
		// _update on every behaviour every frame — so reading a ConfigEntry (a dictionary lookup
		// behind a property) here was paid thousands of times a second. A plain bool is a field read.
		private static bool _fLog, _fFrame;
		// Same reasoning as _fLog / _fFrame, applied to the two BLOCK switches (2026-09-13). They are
		// read by the Udon prefixes, which run for every Udon event in the world, and a ConfigEntry
		// read is a dictionary lookup behind a property: with both switches off that was still two of
		// them per event, thousands of times a second, to decide to do nothing. Cached here once a
		// frame; the prefixes read plain static bools.
		private static bool _fBlockAll, _fBlockCrashers;
		public static void RefreshFlags()
		{
			try { _fLog = ModConfig.UdonLogEnabled.Value; _fFrame = ModConfig.UdonLogFrameEvents.Value; }
			catch { }
			try { _fBlockAll = ModConfig.UdonBlockAll.Value; _fBlockCrashers = ModConfig.UdonBlockCrashers.Value; }
			catch { }
			RefreshBlockNames();   // once per frame: a string compare, and a re-parse only when it changed
		}

		private static void RunProgramPostfix(object __instance, string __0)
		{
			try
			{
				// The cheapest possible early-out, first: when nothing wants Udon events the hot
				// path is one bool and a return.
				if (!_fLog || Paused) return;
				string ev = __0;
				if (string.IsNullOrEmpty(ev)) return;
				// Noise is dropped UNCONDITIONALLY now: property reads (get_/set_), UpdateProcess
				// pumps and numeric jumps are never what a human wants, and a stuck toggle used
				// to let all of it through. Frame events stay behind their own opt-in.
				if (!_fFrame && FrameEvents.Contains(ev)) return;
				if (!IsInteresting(ev)) return;

				TotalSeen++;
				_rateCount++;

				var comp = __instance as Component;
				string obj = "?";
				string user = "";
				if (comp != null)
				{
					GameObject go = null;
					try { go = comp.gameObject; } catch { }
					if (go != null)
					{
						try { obj = go.name; } catch { }
						user = OwnerOf(go);
					}
				}
				// The trust colour is resolved at DRAW time from the roster the PLAYERS tab
				// already maintains: doing reflection per event would tax every Udon call.
				Push(user, obj, ev);
			}
			catch { /* a logger must never break the world it is watching */ }
		}

		// Networking.GetOwner is a networked lookup; spammy objects would hammer it, so the
		// answer is cached briefly per GameObject. Ownership rarely changes inside a second.
		private sealed class OwnerCache { public string Name; public float At; }
		private static readonly Dictionary<int, OwnerCache> Owners = new Dictionary<int, OwnerCache>();

		private static string OwnerOf(GameObject go)
		{
			try
			{
				int id = go.GetInstanceID();
				float now = VaClock.Now;
				if (Owners.TryGetValue(id, out OwnerCache c) && now - c.At < 1f) return c.Name;

				string name = "";
				try
				{
					VRCPlayerApi owner = Networking.GetOwner(go);
					if (owner != null) name = owner.displayName ?? "";
				}
				catch { }

				if (Owners.Count > 512) Owners.Clear();     // bounded: worlds can hold thousands of objects
				Owners[id] = new OwnerCache { Name = name, At = now };
				return name;
			}
			catch { return ""; }
		}

		private static void Push(string user, string obj, string ev)
		{
			// Collapse an identical event repeating back-to-back (Udon loves to spam).
			if (Log.Count > 0)
			{
				Entry last = Log[Log.Count - 1];
				if (last.Event == ev && last.Obj == obj && last.User == user)
				{
					last.Repeats++;
					last.Time = VaClock.Now;
					return;
				}
			}
			if (Log.Count >= Capacity) Log.RemoveAt(0);
			Log.Add(new Entry
			{
				Time = VaClock.Now,
				Clock = DateTime.Now.ToString("HH:mm:ss"),
				User = user,
				Obj = obj,
				Event = ev,
			});
		}

		public override void OnUpdate()
		{
			RefreshFlags();   // hoist the config reads out of the per-event hot path

			// Keep trying to attach until it sticks — the game's assemblies may not have been ready
			// when OnInitialize ran, and a scene later they are.
			if (!_hooked && _hookAttempts < MaxHookAttempts)
			{
				float t = VaClock.Now;
				if (t >= _nextHookTry)
				{
					_nextHookTry = t + 3f;
					_hookAttempts++;
					Hook();
					if (!_hooked && _hookAttempts >= MaxHookAttempts && !_gaveUpLogged)
					{
						_gaveUpLogged = true;
						VRChatArchiveModPlugin.Logger.LogError(
							"[UdonLog] RunProgram never attached after " + MaxHookAttempts + " tries — the Udon "
							+ "console, crasher guard and Udon Manager events are all inert on this build. The "
							+ "method was renamed or moved; report so we can re-target it.");
					}
				}
			}

			// events/second readout
			float now = VaClock.Now;
			if (now - _rateWindow >= 1f)
			{
				_rateWindow = now;
				PerSecond = _rateCount;
				_rateCount = 0;
			}
		}

		// ------------------------------------------------------------ in-world overlay
		// Top-left, chromeless (text + shadow) to match the instance panels. Purely local:
		// this is IMGUI drawn on YOUR client, nobody else can see a single line of it.
		private GUIStyle _ovTitle, _ovMono, _ovDim, _ovRight;

		public override void OnGui()
		{
			try
			{
				if (!ModConfig.UdonLogEnabled.Value || !ModConfig.UdonLogOverlay.Value) return;
				if (Event.current.type != EventType.Repaint) return;
				if (Core.Menu.Visible) return;              // the menu already shows the full console
				EnsureStyles();

				var rows = MergedRows();
				int max = Mathf.Clamp(ModConfig.UdonLogOverlayLines.Value, 3, 40);

				float pad = Core.Hud.S(8f), colHead = Core.Hud.S(15f), rowH = Core.Hud.S(16f);
				float margin = Core.Hud.S(16f);
				// Same width as the player list, the instance log and the radar — see Hud.SideWidth.
				// This panel used to size itself from Screen.width and came out roughly twice as wide
				// as the other three corners of the HUD.
				float x = margin, w = Core.Hud.SideWidth;

				// BOTTOM-LEFT corner of the four-corner HUD. It grows UPWARD from the bottom, so
				// the newest line is always at the same place instead of the panel's height moving
				// the whole list around. Height is capped to the lower half so it never reaches the
				// players list in the top-left corner.
				// SAME BLOCK AS THE RADAR. The radar panel is its square map plus a header
				// (SideWidth + HeaderH + 4), and this sits in the opposite bottom corner, so the two
				// are given identical outer dimensions and the bottom of the HUD reads as one row.
				// Fixed rather than content-driven: a panel that resizes itself every time an event
				// arrives cannot match a panel that never does.
				float panelH = Core.Hud.HeaderH + Core.Hud.SideWidth + Core.Hud.S(4f);
				int fit = Mathf.Max(1, Mathf.FloorToInt((panelH - Core.Hud.HeaderH - colHead - pad) / rowH));
				// Fill the panel. The configured line count used to decide the panel's HEIGHT, so a
				// small value simply made a shorter panel; now that the height is pinned to the
				// radar's, honouring it would only leave dead space that can never be used.
				max = fit;
				int start = Mathf.Max(0, rows.Count - max);
				int shown = Mathf.Max(1, rows.Count - start);

				float h = panelH;
				float y = Screen.height - margin - h;      // grows upward from the bottom edge
				var panel = new Rect(x, y, w, h);

				string right = $"udon {PerSecond}/s"
					+ (NetworkLogModule.HookAttached ? $"  ·  net {NetworkLogModule.PerSecond}/s" : "")
					+ (Paused ? "  [PAUSED]" : "");
				var body = Core.Hud.Panel(panel, "EVENTS", right);

				// Columns as FRACTIONS of the panel, not fixed design pixels. They used to be absolute
				// (+64, +194, +350), which was fine only at the width this panel happened to be: once
				// it was narrowed to match the rest of the HUD, +350 landed outside the panel and the
				// EVENT column would have been clipped away entirely. Proportions survive any width.
				float bw = body.width;
				float xTime = body.x + Core.Hud.S(6f);
				float xWho = body.x + bw * 0.20f;
				float xObj = body.x + bw * 0.42f;
				float xEv = body.x + bw * 0.66f;
				float evW = body.xMax - xEv - Core.Hud.S(6f);
				// Each column is exactly as wide as the gap to the next one. These used to be three
				// hard-coded numbers (54 / 126 / 152) that only happened to fit the old width; at the
				// narrower size WHO would have run 33 px into OBJECT and OBJECT 50 px into EVENT.
				float gap = Core.Hud.S(4f);
				float wTime = xWho - xTime - gap;
				float wWho = xObj - xWho - gap;
				float wObj = xEv - xObj - gap;
				// How many characters actually fit, rather than a fixed count: the truncation limits
				// were sized for the wide panel, so at this width the text was clipped mid-glyph by
				// the label rect instead of ending in an ellipsis.
				float chW = 12f * 0.55f;   // _ovMono is 12 px; ~0.55 em per character in a proportional face
				int cWho = Mathf.Max(4, (int)(wWho / chW));
				int cObj = Mathf.Max(4, (int)(wObj / chW));
				int cEv = Mathf.Max(6, (int)(evW / chW));

				// header row
				
				// column labels
				float cy = body.y;
				_ovDim.normal.textColor = new Color(0.45f, 0.53f, 0.63f);
				GUI.Label(new Rect(xTime, cy, wTime, 14f), "TIME", _ovDim);
				GUI.Label(new Rect(xWho, cy, wWho, 14f), "WHO", _ovDim);
				GUI.Label(new Rect(xObj, cy, wObj, 14f), "OBJECT", _ovDim);
				GUI.Label(new Rect(xEv, cy, evW, 14f), "EVENT", _ovDim);

				// rows, zebra-striped, colour-coded per column.
				// Straight down from under the column headings. Bottom-anchoring was tried once the
				// height became fixed — it keeps the newest line at a constant spot — but with a log
				// that has only just started it pushed the handful of entries to the floor and left a
				// tall gap under the headings, which reads as a broken panel. A console fills from the
				// top; the empty space belongs at the bottom, and it disappears as events arrive.
				float ry = cy + colHead;
				for (int i = start; i < rows.Count; i++)
				{
					int line = i - start;
					Core.Hud.Stripe(new Rect(body.x + 2f, ry, body.width - 4f, rowH), line);
					var e = rows[i];

					_ovMono.normal.textColor = new Color(0.44f, 0.52f, 0.62f);
					GUI.Label(new Rect(xTime, ry, wTime, rowH), e.Clock, _ovMono);
					_ovMono.normal.textColor = e.Net ? new Color(0.50f, 0.69f, 1.00f)
						: string.IsNullOrEmpty(e.User) ? new Color(0.5f, 0.6f, 0.72f) : ColorFor(e.User);
					GUI.Label(new Rect(xWho, ry, wWho, rowH), string.IsNullOrEmpty(e.User) ? "world" : Trunc(e.User, cWho), _ovMono);
					_ovMono.normal.textColor = new Color(0.78f, 0.6f, 0.98f);
					GUI.Label(new Rect(xObj, ry, wObj, rowH), Trunc(e.Obj, cObj), _ovMono);
					// Network rows in blue, Udon rows in green: at a glance you know whether you are
					// looking at a world script or at traffic off the wire.
					_ovMono.normal.textColor = e.Net ? new Color(0.45f, 0.75f, 1.00f) : new Color(0.56f, 0.93f, 0.70f);
					GUI.Label(new Rect(xEv, ry, evW, rowH), Trunc(e.Ev, cEv) + (e.Repeats > 1 ? "  x" + e.Repeats : ""), _ovMono);
					ry += rowH;
				}
				if (rows.Count == 0)
				{
					_ovMono.normal.textColor = new Color(0.45f, 0.53f, 0.63f);
					GUI.Label(new Rect(xTime, ry, w - 24f, rowH), "no event yet — join a world with interactive objects", _ovMono);
				}
				GUI.color = Color.white;
			}
			catch { }
		}

		private void EnsureStyles()
		{
			if (_ovMono != null) return;
			_ovTitle = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
			_ovMono  = new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleLeft };
			_ovDim   = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
			_ovRight = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleRight };
		}

		// Trust colour for a name, from the roster the PLAYERS tab maintains.
		private static Color ColorFor(string name)
		{
			foreach (var p in VaTagsModule.Roster)
				if (p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					if (p.IsLocal) return new Color(0.49f, 1f, 0.62f);
					if (ColorUtility.TryParseHtmlString(p.TrustColor ?? "#CCCCCC", out Color c)) return c;
				}
			return new Color(0.56f, 0.64f, 0.72f);
		}


		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, Mathf.Max(1, n - 1)) + "…");

		public static void Clear()
		{
			Log.Clear();
			TotalSeen = 0;
		}

		// Everything currently passing the text filter, oldest first.
		// ONE console, two sources. A "row" is whatever the EVENTS panel draws \u2014 an Udon event or a
		// received Photon event \u2014 interleaved by capture time so the panel reads as a single feed
		// instead of two lists the user has to correlate by eye.
		public struct Row
		{
			public float Time;
			public string Clock, User, Obj, Ev;
			public int Repeats;
			public bool Net;          // drawn in the network colour, so the two are never confused
		}

		public static List<Row> MergedRows()
		{
			var rows = new List<Row>(Capacity);

			// The feed has two SOURCES and the console lets you pick either or both. Recording keeps
			// running underneath regardless, so switching Udon off to read the network traffic does
			// not throw away the Udon history you came back for.
			bool udon;
			try { udon = ModConfig.EventsShowUdon.Value; } catch { udon = true; }
			if (udon)
				foreach (var e in Visible())
					rows.Add(new Row { Time = e.Time, Clock = e.Clock, User = e.User, Obj = e.Obj, Ev = e.Event, Repeats = e.Repeats, Net = false });

			bool net;
			try { net = ModConfig.EventsShowNetwork.Value && ModConfig.NetworkLogEnabled.Value; }
			catch { net = false; }
			if (net)
			{
				string f = (Filter ?? "").Trim();
				foreach (var n in NetworkLogModule.VisibleRows())
				{
					// The sender is Photon's actor number for this room, NOT the VRChat player id shown
					// in the roster, and the two are not interchangeable \u2014 so it is labelled for what it
					// is rather than passed off as a player.
					string who = n.Sender > 0 ? "actor " + n.Sender : "server";
					string ev = NetworkLogModule.Label(n.Code);
					string obj = NetworkLogModule.ShapeOf(n.Code);
					if (f.Length > 0
						&& ev.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0
						&& who.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0
						&& (obj == null || obj.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)) continue;
					rows.Add(new Row { Time = n.Time, Clock = n.Clock, User = who, Obj = obj, Ev = ev, Repeats = n.Repeats, Net = true });
				}
				rows.Sort((x, y) => x.Time.CompareTo(y.Time));
				if (rows.Count > Capacity) rows.RemoveRange(0, rows.Count - Capacity);
			}
			return rows;
		}

		public static List<Entry> Visible()
		{
			string f = (Filter ?? "").Trim();
			if (f.Length == 0) return new List<Entry>(Log);
			var outp = new List<Entry>();
			foreach (var e in Log)
			{
				if ((e.Event != null && e.Event.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ||
					(e.Obj != null && e.Obj.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ||
					(e.User != null && e.User.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
					outp.Add(e);
			}
			return outp;
		}
	}
}
