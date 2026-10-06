using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE CONTROL CHANNEL — the desktop client drives the mod.
	//
	// The mod's own TAB menu is being retired: an IMGUI panel drawn inside VRChat can never be as
	// good as a real desktop window, and every setting already lives in a BepInEx ConfigEntry that
	// something re-reads every frame. So the client becomes the place you change things, and this is
	// the wire between them.
	//
	// DIRECTION MATTERS. The desktop app is the SERVER (LocalBridge listens on 127.0.0.1:8791) and
	// the mod is an HTTP CLIENT — the game process opens no port of its own. So control cannot be
	// pushed to us: we POLL, the same way the soundboard already does. Each sync sends what the mod
	// currently is (settings + who is in the instance) and receives whatever the client wants
	// changed since last time.
	//
	// ITS OWN MODULE, deliberately — and this module OWNS the VaAuth probe (VaTags no longer does).
	// VaAuth.Poll used to be driven from VaTagsModule.OnUpdate, below an early-out on VaTagsEnabled,
	// so switching VA tags off silently killed the client's control of the mod. It is now called
	// from OnUpdate here, above every gate.
	public class ModControlModule : IModule
	{
		public override string Name => "ModControl";

		public static string Status = "idle";
		public static bool Linked;              // the client answered our last sync
		private static int _applied;            // settings changed by the client this session

		// THE LAST WING ROW CLICKED, set by WingPlayersModule.OnRowClick (main thread, uGUI click)
		// and emitted by BuildSync as "wingSelect" so the CLIENT selects that player on its PLAYERS
		// page. seq climbs on every click; the client acts once per new seq. Zero = nothing yet.
		internal static (string userId, string name, int seq) WingSelect;

		private float _next;
		private long _seq;                      // highest sequence seen, for reporting only
		private int _failed;                    // commands from the client we could not apply

		// Bounded memory of what has actually been applied. 256 covers far more than one poll can
		// ever carry, and unlike a high-water mark it does not care if the numbering restarts.
		private readonly System.Collections.Generic.HashSet<long> _recent = new System.Collections.Generic.HashSet<long>();
		private readonly System.Collections.Generic.Queue<long> _recentOrder = new System.Collections.Generic.Queue<long>();

		private void Remember(long seq)
		{
			if (seq == 0 || !_recent.Add(seq)) return;
			_recentOrder.Enqueue(seq);
			while (_recentOrder.Count > 256) _recent.Remove(_recentOrder.Dequeue());
		}
		private bool _schemaSent;
		private bool _busy;

		// ------------------------------------------------------------------ main-thread work
		//
		// WHERE A COMMAND ARRIVES IS NOT WHERE IT CAN RUN. Apply() executes in the continuation of an
		// awaited HTTP call, and this process has no Unity synchronisation context — so that
		// continuation is a thread-pool thread. The Udon manager cannot live there: a scan is
		// Resources.FindObjectsOfTypeAll over every loaded object, and running an event calls straight
		// into the world's script. Unity refuses both off its main thread, and not always by throwing
		// something a catch can see.
		//
		// So a Udon command does not act when it arrives. It queues here, and OnUpdate — which IS the
		// main thread — runs it on the next frame.
		private static readonly Queue<Action> MainWork = new Queue<Action>();

		// THE FAST LANE (2026-09-13): TOGGLES NEVER WAIT BEHIND A BATCH.
		//
		// MainWork is PACED — one step per 10 ms — because bulk networked work (a RUN ALL of a
		// hundred udon events) must not burst. That pacing is right for bulk and wrong for a toggle:
		// a user flipping forceJump while a 1137-step udonOwn batch was queued waited up to 40 s,
		// and TWICE, because ApplyAction's toggle cases re-enter OnMain from inside the queued work.
		// A toggle that lands seconds late is indistinguishable from a broken one.
		//
		// So actions that are LOCAL, cheap and cannot emit a burst — every "*Stop", forceJump, ghost,
		// the rotator/boxDrop/elevator setters, menu switches — go on a second queue that PumpMain
		// drains COMPLETELY every frame, ahead of the paced lane. _fastContext is set while that
		// drain runs, so an OnMain() re-entered from inside a fast job (the second hop above) lands
		// on the fast lane too and executes in the same frame: a toggle is applied end to end on the
		// frame it arrives. Main-thread only, like everything in this queue.
		private static readonly Queue<Action> MainWorkFast = new Queue<Action>();
		private static bool _fastContext;
		private const int FastLaneCap = 512;   // should never be approached: fast jobs are single steps

		private static readonly HashSet<string> ToggleClassActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			// stopping something must be instant above all
			"orbitStop", "objectOrbitStop", "elevatorPlayerStop", "chatMimicStop", "mimicStop", "voiceMimicStop",
			"resetMovement",
			// local switches
			"forceJump", "ghost", "fastSync", "rotator", "rotatorFlip", "rotatorReset", "trueView",
			"boxDrop", "boxDropX", "boxDropY", "boxDropZ", "portalInfinite",
			"menuBackgrounds", "launchpadConsole", "archiveLogToggle",
			"playerGrab", "playerGrabAim", "forcePickup", "forceGrab",
			// setters that only change a parameter
			"elevatorSpeed", "elevatorHeight", "elevatorAutoCorrect", "mimicMirror",
			"markPut", "markClear", "markShape", "markMode",
			// local one-shots with no network burst
			"antiCrashRescan", "photonGuardReset", "refreshTags", "refreshFavs", "sbPlay", "sbPreview", "whoBlockedMe",
		};

		private static bool IsToggleClassAction(string id) => !string.IsNullOrEmpty(id) && ToggleClassActions.Contains(id);

		private static void OnMainFast(Action work)
		{
			if (work == null) return;
			lock (MainWorkFast)
			{
				if (MainWorkFast.Count >= FastLaneCap)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[ModControl] fast lane full, command dropped.");
					return;
				}
				MainWorkFast.Enqueue(work);
			}
		}

		private static void OnMain(Action work)
		{
			if (work == null) return;
			// Re-entered from a fast job (a toggle case doing its own OnMain): stay on the fast lane so
			// the second hop runs this frame instead of joining the paced queue behind a batch.
			if (_fastContext) { OnMainFast(work); return; }
			lock (MainWork)
			{
				// A backstop, not a policy: nothing here should ever queue 64 deep, and a queue that grew
				// without bound would mean a frame that never comes. Said out loud rather than dropped
				// quietly — the client clears its own queue the moment it hands a command over, so a
				// silent drop here would be the last trace the request ever existed.
				// 4096, not 64: the client's bulk actions (RUN ALL / TAKE OWNERSHIP ALL) hand over up to a
				// hundred steps per sync at 0.01 s pacing, and PumpMain below now spends them at that same
				// pace instead of in one frame — so between two syncs a few hundred can legitimately wait
				// here. 64 dropped a third of every bulk batch with nothing but a log line to show for it.
				if (MainWork.Count >= 4096)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[ModControl] main-thread queue full, command dropped.");
					return;
				}
				MainWork.Enqueue(work);
			}
		}

		// PACED, NOT DRAINED. This used to run everything a sync had brought in ONE frame, so a bulk
		// "run all" of a hundred events was a single-frame burst whatever spacing the client had used
		// to hand them over — and a burst of networked events is exactly what desyncs or disconnects
		// you. Now it spends one step per 10 ms of elapsed time: at 60 fps that is 1-2 per frame, i.e.
		// the client's 0.01 s pacing becomes 0.01 s pacing IN GAME. Two guarantees: at least one step
		// per frame (a lone toggle is never delayed), and the banked credit is capped at 80 ms, so a
		// frame hitch cannot turn into a burst of its own. Credit resets when the queue is empty, so
		// idle time never banks up a burst for the next batch.
		private static readonly System.Diagnostics.Stopwatch MainClock = System.Diagnostics.Stopwatch.StartNew();
		private static long _mainLastMs, _mainCreditMs;

		private static void PumpMain()
		{
			// FAST LANE FIRST, DRAINED TO EMPTY, NOT PACED. Each job is a single local step (a
			// toggle, a stop, a setter), so running all of them is microseconds; and the loop keeps
			// going until the queue is empty so a job enqueued BY a job — the toggle cases' inner
			// OnMain, redirected here by _fastContext — runs in this same frame.
			for (int guard = 0; guard < FastLaneCap * 2; guard++)
			{
				Action fast;
				lock (MainWorkFast)
				{
					if (MainWorkFast.Count == 0) break;
					fast = MainWorkFast.Dequeue();
				}
				_fastContext = true;
				try { fast(); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ModControl] fast command failed: " + e.Message); }
				finally { _fastContext = false; }
			}

			long now = MainClock.ElapsedMilliseconds;
			long elapsed = now - _mainLastMs;
			_mainLastMs = now;
			lock (MainWork) { if (MainWork.Count == 0) { _mainCreditMs = 0; return; } }
			_mainCreditMs = Math.Min(_mainCreditMs + elapsed, 80);
			int ran = 0;
			while (true)
			{
				if (ran > 0 && _mainCreditMs < 10) return;
				Action work;
				lock (MainWork)
				{
					if (MainWork.Count == 0) return;
					work = MainWork.Dequeue();
				}
				_mainCreditMs = Math.Max(0, _mainCreditMs - 10);
				ran++;
				try { work(); }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ModControl] queued command failed: " + e.Message); }
			}
		}

		// THE UDON PAGE'S DATA, always built on the main thread and held until a sync takes it.
		//
		// It deliberately does NOT ride every sync. The list only changes when the client asks for
		// something — a scan, a switch, a restore — and a world can hold a thousand behaviours, so
		// sending it once a second would be a hundred kilobytes a second describing something that did
		// not move. Built once when it changes, taken once by the next sync.
		// Which clip icons the client already has. Icons are big next to the rest of a sync, so each
		// goes over exactly once; a fresh schema announce (new client session) clears this.
		private static readonly HashSet<string> _iconSent = new HashSet<string>(StringComparer.Ordinal);

		private static string _udonPayload;

		// THE ENTRY POINTS OF THE LAST BEHAVIOUR ASKED ABOUT, kept rather than consumed. Clearing
		// these once they had been sent looked tidier and lost the answer: click a row (which asks
		// for its events), flip a switch before the next sync, and the switch's own rebuild
		// overwrote the payload with one that no longer carried them — leaving the client's detail
		// panel waiting for an answer it had already been given. They are small, the client keys
		// them by id, and re-sending the same list costs nothing.
		private static int _udonEventsId;
		private static List<string> _udonEventList;
		private static int _udonVarsId;
		private static List<UdonManagerModule.Var> _udonVarList;
		// Every script's entry points in one answer (client action udonEventsAll), so the client's list
		// can be filtered by LOCAL / GLOBAL without 300 round trips. Built on demand, sent once.
		private static string _udonAllEvents;
		// (The mod-side "udonDump" walker of v236-v238 is gone: DUMP ALL reads the world FILE in the
		// client now, and the live walk was never cheap enough for a running game.)
		// The last "run this event and tell me what changed" result, kept like the events/vars blocks.
		private static int _udonDiffId;
		private static string _udonDiffEvent;
		private static List<UdonManagerModule.VarChange> _udonDiffList;
		private static float _worldWatch;
		private static string _lastWorldId = "";
		private const int MaxUdonRows = 1500;

		// ADAPTIVE. A flat one-second poll meant a switch could take two seconds to take effect —
		// queued just after one sync, applied on the next. The client raises a "hot" flag while
		// somebody is actually using a mod page, and we answer five times a second for as long as
		// that lasts. Idle, it costs exactly what it did before.
		private const float IdleSec = 1f;
		private const float HotSec = 0.2f;
		private const float RepollSec = 0.03f;   // when the client long-polls, just re-ask; the client waits
		private float _interval = IdleSec;

		// A CHANGE MADE IN GAME MUST NOT WAIT FOR THE NEXT POLL.
		//
		// "hot" only ever came from the CLIENT: it raises the flag while someone is using its window,
		// and the mod then polls five times a second. Nothing did the mirror image. So flipping a
		// tile in VRChat — where the client is by definition idle — sat until the next one-second
		// poll before the client heard about it, which is the direction that felt broken.
		//
		// BepInEx raises SettingChanged on every REAL change, so there is nothing to poll for: the
		// event arms an immediate sync. Subscribing costs one handler; hashing every setting each
		// frame to notice the same thing would cost far more.
		private static bool _dirty;
		private bool _subscribed;

		private void SubscribeOnce()
		{
			if (_subscribed) return;
			_subscribed = true;
			try
			{
				var file = ModConfig.EspCapsule?.ConfigFile;   // any entry: they all share one file
				if (file != null) file.SettingChanged += (_, __) => _dirty = true;
			}
			catch { }
		}

		public override void OnUpdate()
		{
			try
			{
				// THE PROBE RUNS FIRST, ABOVE EVERY GATE. VaAuth.Poll is the only thing that ever sets
				// InsideClient, and it used to be called from VaTagsModule below its own VaTagsEnabled
				// early-out — so switching VA tags off silently killed this whole channel. It lives here
				// now, before the ModControl switch too, so the mod always knows whether the desktop
				// client is there. Poll throttles itself, so a second caller would be harmless.
				VaAuth.Poll(VaClock.Now);

				// Anything the client asked for that has to touch Unity runs HERE, on the frame — not
				// on the socket continuation that received it. Ahead of the link check on purpose, so
				// work already accepted still completes if the client goes away mid-flight.
				PumpMain();

				if (!ModConfig.ModControlEnabled.Value) return;

				if (!VaAuth.InsideClient) { Linked = false; Status = "waiting for the desktop client"; return; }

				SubscribeOnce();

				float now = VaClock.Now;

				// THE WORLD ID ARRIVES LATE, and something has to notice.
				//
				// On a scene change the mod pushes an empty Udon list straight away, but RoomManager
				// does not know the new world for another second or two — so that push carries no
				// world id, and a client waiting to apply a per-world profile would wait for ever,
				// because nothing else pushes until somebody presses a button. Watching costs almost
				// nothing (CurrentInstance caches for a second of its own) and this only rebuilds the
				// payload when the answer actually changed.
				if (now >= _worldWatch)
				{
					_worldWatch = now + 2f;
					string wid = "";
					try { wid = VaTagsModule.CurrentInstance().WorldId ?? ""; } catch { }
					if (wid != _lastWorldId)
					{
						_lastWorldId = wid;
						_udonPayload = BuildUdon();
					}
				}

				// A pending change jumps the queue rather than waiting out the interval.
				if (_dirty && !_busy) { _dirty = false; _next = 0f; }
				if (now < _next || _busy) return;
				_next = now + _interval;
				_busy = true;
				_ = SyncAsync();
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		// A world change destroys every Udon behaviour we knew about, and UdonManagerModule clears
		// its own list on the same event. Rebuilding the payload through the queue rather than here
		// means it is built on the NEXT frame — after every module has seen the scene change — so it
		// cannot depend on which of us was registered first.
		public override void OnSceneLoaded(int buildIndex)
		{
			_schemaSent = false;
			// Forget the world we were in, so the 2 s watch in OnUpdate re-pushes the udon block even
			// when the new scene is the SAME world (new instance, rejoin after a crash). Without this
			// the id compares equal, nothing is rebuilt, and the client never re-applies its per-world
			// profile.
			_lastWorldId = "";
			OnMain(() =>
			{
				_udonEventList = null;   // the behaviour they belonged to no longer exists
				_udonVarList = null;
				_udonPayload = BuildUdon();
			});
		}

		private async System.Threading.Tasks.Task SyncAsync()
		{
			try
			{
				// THE SYNC BODY IS BUILT FIRST, before any await. SyncAsync is entered synchronously
				// from OnUpdate, so this line runs on the main thread — and it has to: BuildSync reads
				// the roster, OrbitModule, ForceGrab's live pickup, and BuildEvents walks Unity/IL2CPP
				// state. It used to sit AFTER the schema POST below, which meant that on every
				// schema-first sync (every world change) it ran in the awaited continuation — a
				// thread-pool thread with no IL2CPP GC attachment — which is exactly the kind of
				// off-thread native read that takes the process down without a catchable exception.
				// Built exactly once per SyncAsync, too: BuildSync is side-effecting (it TAKES the
				// udon payload, advances the event high-water mark, trims the release cache).
				string syncBody = BuildSync();

				// The schema goes once per session: it is the list of every setting with its type, so
				// the client can build real controls instead of guessing. Values ride on every sync.
				if (!_schemaSent)
				{
					lock (_iconSent) _iconSent.Clear();   // new client session: it has no icons yet
					var (okS, _, _) = await VaAuth.PostBridgeAsync("/mod/schema", BuildSchema());
					if (okS) _schemaSent = true;
				}

				var (ok, raw, code) = await VaAuth.PostBridgeAsync("/mod/sync", syncBody);
				if (!ok)
				{
					Linked = false;
					Status = "client did not answer (" + code + ")";
					// An old client has no /mod route at all. Re-announcing the schema on the next
					// success is cheap, and stops a stale schema from outliving a client restart.
					_schemaSent = false;
					return;
				}

				Linked = true;
				Apply(raw);

				// If the client long-polls (lp:true), IT does the waiting: it holds the request open
				// until a toggle is flipped, so a flip comes back the instant it happens. We just poll
				// again promptly and let the client's hold set the real pace - an idle poll blocks on
				// the client for ~0.8s rather than spinning. An older client that answers instantly has
				// no lp flag, so we fall back to the timer instead of hammering it.
				bool longPoll = raw != null && raw.IndexOf("\"lp\":true", StringComparison.Ordinal) >= 0;
				if (longPoll) _interval = RepollSec;
				else _interval = raw != null && raw.IndexOf("\"hot\":true", StringComparison.Ordinal) >= 0
					? HotSec : IdleSec;
			}
			catch (Exception e) { Linked = false; Status = "sync failed: " + e.Message; }
			finally { _busy = false; }
		}

		// ------------------------------------------------------------------ outgoing
		// SETTINGS THE CLIENT MUST NOT OFFER. Left out of the schema so no control is drawn, AND
		// refused on the way in, so an older client that still remembers one cannot set it either.
		// Two things land here. Values dictated by what the game actually contains, where a free
		// text box can only produce a typo that silently falls back to something else — and the
		// SIGNATURE lists: the user ids this mod announces on everyone else's client. Handing those
		// to the user turns "who am I told about" into "who do I get to follow", and lets them
		// delete the very announcement they are the audience for. Not theirs to edit.
		// The chatbox lines seen since the last sync, drained so each is delivered exactly once.
		// One line about the decryptor for the client's sync: what AssetBundlePatch is deciding right
		// now and what it has done since launch. Never throws — the sync must not die on a counter.
		private static string DecryptorStatus()
		{
			try
			{
				// The decryptor is no longer compiled into the mod: it is delivered by the server only to
				// an entitled account and armed at runtime. So the honest status is the ARM STATE the
				// module reports (fetching / ON / not entitled), plus how many bundles it has decrypted.
				// [open-source build] The plaintext-cache decryptor (AssetBundlePatchModule) is not part of
				// this repository; it is a server-gated paid feature injected at runtime in the private
				// build. With nothing to arm, the honest status is empty.
				return "";
			}
			catch { return ""; }
		}

		private static string ChatMimicLinesJson()
		{
			try
			{
				var lines = ChatMimicModule.Drain();
				if (lines == null || lines.Count == 0) return "[]";
				var sb = new StringBuilder("[");
				for (int i = 0; i < lines.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append(Json(lines[i]));
				}
				return sb.Append(']').ToString();
			}
			catch { return "[]"; }
		}

		private static bool IsInternal(string section, string key)
			=> (string.Equals(section, "Favorites", StringComparison.OrdinalIgnoreCase)
			    && string.Equals(key, "CategoryToBorrow", StringComparison.OrdinalIgnoreCase))
			   // The whole Watchlist section: the ids watched, whether the watch runs at all, and
			   // the banner style that comes with it.
			   || string.Equals(section, "Watchlist", StringComparison.OrdinalIgnoreCase)
			   // The other half of the same signature: who gets the rainbow capsule.
			   || (string.Equals(section, "ESP", StringComparison.OrdinalIgnoreCase)
			       && string.Equals(key, "RainbowUsers", StringComparison.OrdinalIgnoreCase));

		private static string BuildSchema()
		{
			var sb = new StringBuilder(16 * 1024);
			sb.Append("{\"schema\":[");
			bool first = true;
			foreach (var e in Core.ConfigRegistry.Config())
			{
				if (e == null || e.Raw == null) continue;
				if (IsInternal(e.Section, e.Key)) continue;   // not offered to the client at all
				if (!first) sb.Append(',');
				first = false;
				sb.Append("{\"section\":").Append(Json(e.Section))
				  .Append(",\"key\":").Append(Json(e.Key))
				  .Append(",\"type\":").Append(Json(e.Type))
				  .Append(",\"desc\":").Append(Json(Describe(e)));

				// A numeric setting carries its range so the client can draw a slider with the same
				// bounds the mod enforces, rather than an unbounded text box.
				try
				{
					var range = e.Raw.Description?.AcceptableValues as BepInEx.Configuration.AcceptableValueBase;
					if (range != null) sb.Append(",\"range\":").Append(Json(range.ToDescriptionString()));
				}
				catch { }
				sb.Append('}');
			}
			sb.Append("]}");
			return sb.ToString();
		}

		private static string Describe(Core.ConfigRegistry.Entry e)
		{
			try { return e.Raw.Description?.Description ?? ""; } catch { return ""; }
		}

		private static string BuildSync()
		{
			var sb = new StringBuilder(16 * 1024);
			sb.Append("{\"values\":{");
			bool first = true;
			foreach (var e in Core.ConfigRegistry.Config())
			{
				if (e == null || e.Raw == null) continue;
				if (!first) sb.Append(',');
				first = false;
				sb.Append(Json(e.Section + "/" + e.Key)).Append(':').Append(Json(Core.ConfigRegistry.ValueOf(e)));
			}
			sb.Append('}');

			// WHAT EACH SETTING IS ACTUALLY DOING, beside what it is set to. Small — only the
			// features that have something to say appear — and it is the half that was missing every
			// time a toggle "did not work": the value was right and nothing said the effect was not.
			sb.Append(",\"health\":");
			Core.FeatureHealth.AppendJson(sb);

			sb.Append(",\"roster\":[");

			// WHO IS IN THE INSTANCE, with the avatar id read from the game itself. This is what lets
			// the client's Force Clone work off the mod instead of parsing logs: an id read from the
			// live player is exact, and it exists even for avatars no database has ever indexed.
			try
			{
				var roster = VaTagsModule.Roster;
				bool f2 = true;
				lock (roster)
				{
					foreach (var p in roster)
					{
						if (p == null) continue;
						if (!f2) sb.Append(',');
						f2 = false;
						sb.Append("{\"name\":").Append(Json(p.Name))
						  .Append(",\"userId\":").Append(Json(p.UserId))
						  .Append(",\"avatarId\":").Append(Json(p.AvatarId))
						  .Append(",\"avatarName\":").Append(Json(p.AvatarName))
						  .Append(",\"platform\":").Append(Json(p.Platform))
						  .Append(",\"release\":").Append(Json(ReleaseCached(p)))
						  .Append(",\"trust\":").Append(Json(p.TrustColor))
						  .Append(",\"plus\":").Append(p.Plus ? "true" : "false")
						  .Append(",\"adult\":").Append(p.Adult ? "true" : "false")
						  // Who blocked whom. THIS is the payload the client's PLAYERS tab reads (short
						  // keys); the detailed per-player dump further down carries the same two facts
						  // under longer names. Both are needed — they are different messages, and only
						  // this one feeds the list.
						  .Append(",\"blockedMe\":").Append(BlockedByProbeModule.BlockedMe.Contains(p.UserId ?? "") ? "true" : "false")
						  .Append(",\"blockedByMe\":").Append(BlockedByProbeModule.IBlocked.Contains(p.UserId ?? "") ? "true" : "false")
						  .Append(",\"playerId\":").Append(p.PlayerId)
						  .Append(",\"isLocal\":").Append(p.IsLocal ? "true" : "false")
						  // CUSTOM USERNAME, and only ever on the local row. "name" above comes from
						  // APIUser.displayName and stays the truth; this is what the world's Udon
						  // scripts are being told instead, so the client can draw "real -> custom"
						  // the way the in-game PLAYERS panel does. Empty for everyone else, and
						  // empty when the spoof is off.
						  .Append(",\"udonName\":").Append(Json(p.IsLocal ? SpoofModule.Applied : ""))
						  .Append(",\"isOwner\":").Append(p.IsOwner ? "true" : "false")
						  .Append(",\"isMaster\":").Append(p.IsMaster ? "true" : "false")
						  // LIVE STATE from the avatar animator (PlayerStatesModule). stateKnown is
						  // what stops the client drawing "not AFK" for an avatar that has simply not
						  // loaded yet — without it, "unknown" and "false" look identical on the row.
						  .Append(",\"stateKnown\":").Append(p.StateKnown ? "true" : "false")
						  .Append(",\"afk\":").Append(p.Afk ? "true" : "false")
						  .Append(",\"seated\":").Append(p.Seated ? "true" : "false")
						  .Append(",\"inStation\":").Append(p.InStation ? "true" : "false")
						  .Append(",\"inVR\":").Append(p.InVR ? "true" : "false")
						  // SEPARATE from stateKnown: the SDK answers IsUserInVR for every player, while
						  // stateKnown only says whether their AVATAR's animator answered.
						  .Append(",\"vrKnown\":").Append(p.VrKnown ? "true" : "false")
						  // Live position for the client's PLAYERS card. hasPos is false when even the api
						  // fallback could not place them; coords sanitised so a broken NaN transform cannot
						  // make the whole sync invalid JSON.
						  .Append(",\"hasPos\":").Append(p.HasPos ? "true" : "false")
						  .Append(",\"px\":").Append(SafeF(p.Pos.x))
						  .Append(",\"py\":").Append(SafeF(p.Pos.y))
						  .Append(",\"pz\":").Append(SafeF(p.Pos.z));

						// The tags this player carries, so the client can show and edit them without
						// a second round trip to the server for data the mod already holds.
						sb.Append(",\"tags\":[");
						try
						{
							var tags = VaTagsModule.TagsOf(p.UserId);
							if (tags != null)
							{
								for (int t = 0; t < tags.Length; t++)
								{
									if (t > 0) sb.Append(',');
									var tg = tags[t];
									sb.Append("{\"text\":").Append(Json(tg.Text))
									  .Append(",\"color\":").Append(Json(tg.Color))
									  .Append(",\"b\":").Append(tg.B ? "true" : "false")
									  .Append(",\"i\":").Append(tg.I ? "true" : "false")
									  .Append(",\"u\":").Append(tg.U ? "true" : "false")
									  .Append(",\"fx\":").Append(Json(tg.Fx)).Append('}');
								}
							}
						}
						catch { }
						sb.Append("]}");
					}
				}
			}
			catch { }

			// Live state the client needs to draw its toggles in the right position — an orbit
			// switch that does not know it is already on is just a button that lies.
			string orbitMode = "off", orbitTarget = "";
			string mimicTarget = "";
			bool mimicMirror = false;
			string voiceMimicTarget = "";
			try
			{
				orbitMode = OrbitModule.Current.ToString().ToLowerInvariant();
				orbitTarget = OrbitModule.TargetUid ?? "";
				mimicTarget = MimicPoseModule.TargetUid ?? "";
				mimicMirror = MimicPoseModule.Mirror;
				voiceMimicTarget = VoiceMimicModule.TargetUid ?? "";
			}
			catch { }

			// Same for the object ring, so the client's toggle shows the state that is really on.
			bool objOrbit = false; string objOrbitCenter = ""; int objOrbitCount = 0;
			try
			{
				objOrbit = ObjectOrbitModule.Active;
				objOrbitCenter = ObjectOrbitModule.CenterName ?? "";
				objOrbitCount = ObjectOrbitModule.Count;
			}
			catch { }

			// Elevator, same shape as the ring so the client's switch shows the real state.
			bool elevator = false; string elevatorCenter = ""; int elevatorCount = 0; bool elevatorAuto = false;
			try
			{
				elevator = ElevatorModule.Active;
				elevatorCenter = ElevatorModule.CenterName ?? "";
				elevatorCount = ElevatorModule.Count;
				elevatorAuto = ModConfig.ElevatorAutoCorrect.Value;
			}
			catch { }

			// And Force Grab, read once here so a pickup dropped mid-build cannot split the pair.
			bool forceGrab = false; string forceGrabName = "";
			try
			{
				forceGrab = ForceGrabModule.Holding;
				forceGrabName = forceGrab ? (ForceGrabModule.HeldName ?? "") : "";
			}
			catch { }

			sb.Append(']');

			// THE WING ROW THAT WAS CLICKED. A click on a wing player row used to open the sealed
			// in-game menu on the PLAYERS tab — a no-op since the menu was retired — so the row now
			// hands the user to the CLIENT's PLAYERS page through this block. Seq-based, not
			// consumed: it rides every sync once set, and the client acts on it once per new seq, so a
			// sync lost to a timeout cannot lose the click. Not sent through QueueResult on purpose —
			// PlayersPage.DrainDumps drains that queue wholesale onto the clipboard.
			try
			{
				var ws = WingSelect;
				if (ws.seq > 0 && !string.IsNullOrEmpty(ws.userId))
					sb.Append(",\"wingSelect\":{\"seq\":").Append(ws.seq)
					  .Append(",\"userId\":").Append(Json(ws.userId))
					  .Append(",\"name\":").Append(Json(ws.name ?? "")).Append('}');
			}
			catch { }

			sb.Append(",\"events\":[").Append(BuildEvents()).Append(']')
			  .Append(",\"results\":[").Append(TakeResults()).Append(']')
			  .Append(",\"seq\":").Append(0)
			  .Append(",\"applied\":").Append(_applied)
			  .Append(",\"world\":").Append(Json(SafeWorld()))
			  .Append(",\"orbitMode\":").Append(Json(orbitMode))
			  .Append(",\"orbitTarget\":").Append(Json(orbitTarget))
			  .Append(",\"mimicTarget\":").Append(Json(mimicTarget))
			  .Append(",\"mimicMirror\":").Append(mimicMirror ? "true" : "false")
			  .Append(",\"voiceMimicTarget\":").Append(Json(voiceMimicTarget))
			  // MIMIC CHATBOX. The target so the toggle can show its state, and the LINES SEEN
			  // SINCE THE LAST SYNC as an array — a single slot would drop the first of two
			  // messages typed inside one poll interval, and people do type twice in a second.
			  .Append(",\"chatMimicTarget\":").Append(Json(ChatMimicModule.TargetUid ?? ""))
			  .Append(",\"chatMimicArmed\":").Append(ChatMimicModule.Armed ? "true" : "false")
			  .Append(",\"chatMimicLines\":").Append(ChatMimicLinesJson())
			  .Append(",\"objectOrbit\":").Append(objOrbit ? "true" : "false")
			  .Append(",\"objectOrbitCenter\":").Append(Json(objOrbitCenter))
			  .Append(",\"objectOrbitCount\":").Append(objOrbitCount)
			  .Append(",\"elevator\":").Append(elevator ? "true" : "false")
			  .Append(",\"elevatorCenter\":").Append(Json(elevatorCenter))
			  .Append(",\"elevatorCount\":").Append(elevatorCount)
			  .Append(",\"elevatorAutoCorrect\":").Append(elevatorAuto ? "true" : "false")
			  // The soundboard: its clip list (so the client can draw a button per clip without
			  // hardcoding them) and the recent who-played-what feed. Both are a few dozen bytes.
			  .Append(",\"soundboard\":").Append(BuildSoundboard())
			  // Whether the chatbox animation is running, so the client's button can read TOGGLED
			  // rather than pretending it started something it cannot see.
			  .Append(",\"badApple\":").Append(BadAppleModule.Playing ? "true" : "false")
			  // WHICH BUILD IS TALKING, so the client can tell "the mod is connected" from "the
			  // right mod is connected" without re-hashing the DLL.
			  .Append(",\"modVersion\":").Append(Json(PluginInfo.Version))
			  // THE IN-GAME DECRYPTOR (Premium+ since 2026-09-09). on/off is the live decision the
			  // AssetBundle prefix is making right now, from the tier the client's bridge reports;
			  // the status line carries the level seen, the level needed and the counts since
			  // launch, so a Free user is told why the cache stays encrypted instead of filing
			  // "the auto archiver finds nothing".
			  // [open-source build] decryptor removed (paid feature); always reported off here.
			  .Append(",\"decryptor\":false")
			  .Append(",\"decryptorStatus\":").Append(Json(DecryptorStatus()))
			  // Force Pickup is a state, so the client's toggle can show what is really on.
			  .Append(",\"forceJump\":").Append(ForceJumpModule.Active ? "true" : "false")
			  // GHOST: the local player's network serializer held off (others see you frozen).
			  .Append(",\"ghost\":").Append(GhostModule.Active ? "true" : "false")
			  // FLOAT OBJECTS: gravity removed from every pickup's body (ObjectGravityModule), and how many.
			  .Append(",\"floatObjects\":").Append(ObjectGravityModule.Active ? "true" : "false")
			  .Append(",\"floatObjectsCount\":").Append(ObjectGravityModule.Count)
			  // FAST SYNC: VRChat's own fast serialisation rate for the local player. Applied is the
			  // READ-BACK, not the wish — the button shows what actually took, not what was asked.
			  .Append(",\"fastSync\":").Append(FastSyncModule.Applied ? "true" : "false")
			  .Append(",\"fastSyncStatus\":").Append(Json(FastSyncModule.Status ?? ""))
			  // PLAYER ROTATOR: tilt held on your own capsule, with the neck clamp widened so the
			  // view can follow. The status line carries WHY when the view half could not be armed.
			  .Append(",\"rotator\":").Append(PlayerRotatorModule.Active ? "true" : "false")
			  .Append(",\"rotatorStatus\":").Append(Json(PlayerRotatorModule.Status ?? ""))
			  .Append(",\"boxDrop\":").Append(BoxDropModule.Active ? "true" : "false")
			  .Append(",\"boxDropStatus\":").Append(Json(BoxDropModule.Status ?? ""))
			  .Append(",\"portalInfinite\":").Append(PortalInfiniteModule.Active ? "true" : "false")
			  .Append(",\"portalInfiniteStatus\":").Append(Json(PortalInfiniteModule.Status ?? ""))
			  // MENU BACKGROUNDS: state and how many options were unlocked, so the client's switch
			  // reflects reality instead of whatever it was last clicked to.
			  .Append(",\"forceJoinStatus\":").Append(Json(Core.ForceJoin.LastStatus ?? ""))
			  .Append(",\"launchpadConsole\":").Append(LaunchpadConsoleModule.Active ? "true" : "false")
			  // Which feed the in-game console is showing, so the client's button can name the one it
			  // is about to switch TO instead of always toasting the same generic line.
			  .Append(",\"archiveFeed\":").Append(Json(Core.ArchiveFeed.Showing == Core.ArchiveFeed.Kind.Cache ? "cache" : "archiver"))
			  .Append(",\"menuBackgrounds\":").Append(VrcPlusBackgroundsModule.Active ? "true" : "false")
			  .Append(",\"menuBackgroundsCount\":").Append(VrcPlusBackgroundsModule.Count)
			  .Append(",\"menuBackgroundsStatus\":").Append(Json(VrcPlusBackgroundsModule.Status ?? ""))
			  .Append(",\"ghostStatus\":").Append(Json(GhostModule.Status ?? ""))
			  .Append(",\"playerGrab\":").Append(PlayerGrabModule.Active ? "true" : "false")
			  .Append(",\"playerGrabState\":").Append(Json(PlayerGrabModule.StateText ?? ""))
			  .Append(",\"mark\":").Append(MarkModule.HasMark ? "true" : "false")
			  .Append(",\"markArt\":").Append(MarkModule.ArtPlaying ? "true" : "false")
			  .Append(",\"markMode\":").Append(Json(MarkModule.Mode ?? "local"))
			  .Append(",\"markClip\":").Append(Json(MarkModule.CurrentClip ?? "badapple"))
			  .Append(",\"markClips\":").Append(MarkClipsJson())
			  // VRCHAT NETWORK telemetry (grid, objects owned, move budget, outbound events/s, backlog) so
			  // the client can show what the network mode is really doing; "" when it is not running.
			  .Append(",\"markNet\":").Append(Json(MarkModule.NetInfo ?? ""))
			  .Append(",\"forcePickup\":").Append(ForcePickupModule.Active ? "true" : "false")
			  .Append(",\"forcePickupCount\":").Append(ForcePickupModule.Unlocked)
			  // Force Grab too: the client's FUN button reads "DROP <name>" while something is held
			  // and "GRAB WHAT YOU AIM AT" otherwise. Read here on the main thread (BuildSync always
			  // is now) — Holding looks at live pickup references.
			  .Append(",\"forceGrab\":").Append(forceGrab ? "true" : "false")
			  .Append(",\"forceGrabName\":").Append(Json(forceGrabName))
			  // WHAT THE MOD LAST SAID, so a refusal or a result surfaces in the client. Every action
			  // is "applied" the moment it is queued (see Apply), so these two plain strings are the
			  // only channel a false return, a missing player or an Archive-button outcome has left.
			  // Always present, "" when there is nothing to say — the client diffs them.
			  .Append(",\"lastStatus\":").Append(Json(VaTagsModule.LastStatus ?? ""))
			  .Append(",\"favBtnStatus\":").Append(Json(ArchiveFavButtonModule.Status ?? ""))
			  // WHAT THE GUARDS HAVE STOPPED, so the client's protection page shows the mod is actually
			  // doing something and not just ticking. Four guards, one counter and one "last" each:
			  // bundle (refused AssetBundle loads), photon (dropped/suspended Photon events), udon
			  // (blocked Udon events), avatar (anti-crash scans and neutralisations). Counters only,
			  // never a list — the per-event detail lives in the LOGGING console, and this block rides
			  // every sync. Each read is guarded separately: a guard that is not loaded must not cost
			  // the client the rest of the sync.
			  .Append(",\"protection\":").Append(BuildProtection())
			  // TAKEN, not copied: whoever gets this sync gets the Udon list, and the next sync carries
			  // null instead of repeating it. Interlocked stays as belt and braces: BuildSync is now
			  // ALWAYS entered on the main thread (SyncAsync builds the body before its first await),
			  // but OnMain-queued udon work writes this field from the same thread and an exchange is
			  // the cheapest way to guarantee one sync takes it exactly once.
			  .Append(",\"udon\":").Append(System.Threading.Interlocked.Exchange(ref _udonPayload, null) ?? "null")
			  .Append('}');
			return sb.ToString();
		}

		// THE PROTECTION BLOCK, always present with every key, so the client never has to guess
		// whether a missing field means "zero" or "the mod is older than the page". Plain statics
		// only — no Unity object is touched, so this is safe from any thread, though BuildSync
		// calls it on the main one anyway. A guard whose read throws (module not started, static
		// not initialised) contributes its zero/"" and the others still report.
		private static string BuildProtection()
		{
			int bundleBlocked = 0, photonBlocked = 0, photonSuspended = 0, udonBlocked = 0, avatarsScanned = 0, avatarNeutralized = 0;
			string bundleLast = "", photonLast = "", udonLast = "", avatarLast = "";
			// [open-source build] AssetBundlePatchModule (plaintext-cache decryptor) removed; counters stay 0.
			try { photonBlocked = PhotonGuardModule.Blocked; photonSuspended = PhotonGuardModule.SuspendedCount; photonLast = PhotonGuardModule.LastBlocked ?? ""; } catch { }
			try { udonBlocked = UdonLogModule.BlockedTotal; udonLast = UdonLogModule.LastBlocked ?? ""; } catch { }
			try { avatarsScanned = AntiCrashModule.AvatarsScanned; avatarNeutralized = AntiCrashModule.NeutralizedTotal; avatarLast = AntiCrashModule.LastAvatar ?? ""; } catch { }
			var sb = new StringBuilder(256);
			sb.Append("{\"bundleBlocked\":").Append(bundleBlocked)
			  .Append(",\"bundleLast\":").Append(Json(bundleLast))
			  .Append(",\"photonBlocked\":").Append(photonBlocked)
			  .Append(",\"photonSuspended\":").Append(photonSuspended)
			  .Append(",\"photonLast\":").Append(Json(photonLast))
			  .Append(",\"udonBlocked\":").Append(udonBlocked)
			  .Append(",\"udonLast\":").Append(Json(udonLast))
			  .Append(",\"avatarsScanned\":").Append(avatarsScanned)
			  .Append(",\"avatarNeutralized\":").Append(avatarNeutralized)
			  .Append(",\"avatarLast\":").Append(Json(avatarLast))
			  .Append('}');
			return sb.ToString();
		}

		// UDON AND NETWORK EVENTS, forwarded to the client's console.
		//
		// ONLY WHAT IS NEW. The mod keeps a rolling log; sending all of it once a second would push
		// the same hundreds of lines over and over and the console would show each one repeatedly.
		// A high-water mark on the event clock means each line crosses exactly once.
		private static float _lastEventAt;
		private const int MaxEventsPerSync = 60;

		// ANSWERS TO ONE-SHOT REQUESTS.
		//
		// An action normally just happens — wear this, teleport there. A metadata dump is different:
		// it has an ANSWER, and the answer has to get back across a channel where only the mod ever
		// speaks first. So results are queued here and ride out on the next sync, exactly like
		// events do.
		private static readonly Queue<string> Results = new Queue<string>();

		private static void QueueResult(string kind, string id, string json)
		{
			lock (Results)
			{
				Results.Enqueue("{\"kind\":" + Json(kind) + ",\"id\":" + Json(id) + ",\"json\":" + Json(json) + "}");
				while (Results.Count > 8) Results.Dequeue();   // nobody needs a backlog of dumps
			}
		}

		private static string TakeResults()
		{
			lock (Results)
			{
				if (Results.Count == 0) return "";
				var sb = new StringBuilder(4096);
				bool first = true;
				while (Results.Count > 0)
				{
					if (!first) sb.Append(',');
					first = false;
					sb.Append(Results.Dequeue());
				}
				return sb.ToString();
			}
		}

		// THE WORLD'S SCRIPTS, as the client's UDON MANAGER page draws them.
		//
		// Reads .enabled live off each behaviour rather than trusting what the last scan recorded:
		// a world switches its own scripts on and off constantly, and a row showing OFF because we
		// once saw it OFF is a row that lies. MUST run on the main thread — every caller queues.
		// withItems=false: a per-script answer (events / vars of ONE behaviour) — the list itself has
		// not changed, so it is not rebuilt nor resent. The client only replaces its list when "items"
		// is present, so leaving the key out is safe. This was the dump's lag: rebuilding and resending
		// every row, with an il2cpp .enabled read per row, twice per script on the main thread.
		private static string BuildUdon(bool withItems = true)
		{
			var sb = new StringBuilder(16 * 1024);
			var rows = UdonManagerModule.Snapshot();

			// THE WORLD'S OWN ID, not the scene name the rest of the sync carries. The client keys
			// its per-world profiles on this: two worlds can ship a scene called the same thing, and
			// a profile that fires in the wrong world is worse than no profile at all.
			string worldId = "", worldName = "";
			try
			{
				var inst = VaTagsModule.CurrentInstance();
				worldId = inst.WorldId ?? "";
				worldName = inst.WorldName ?? "";
			}
			catch { }

			sb.Append("{\"status\":").Append(Json(UdonManagerModule.Status))
			  .Append(",\"worldId\":").Append(Json(worldId))
			  .Append(",\"worldName\":").Append(Json(worldName))
			  .Append(",\"total\":").Append(rows.Count)
			  .Append(",\"off\":").Append(UdonManagerModule.DisabledByUs);

			int n = 0;
			if (withItems)
			{
			sb.Append(",\"items\":[");
			for (int i = 0; i < rows.Count && n < MaxUdonRows; i++)
			{
				var e = rows[i];
				if (e == null || e.B == null) continue;
				bool on;
				try { on = e.B.enabled; } catch { continue; }   // destroyed since the scan

				// A broken world can leave a transform at NaN, and "NaN" is not a JSON number — it
				// would take the whole sync down with it rather than just this row.
				float d = e.Dist;
				if (float.IsNaN(d) || float.IsInfinity(d)) d = 0f;
				if (n > 0) sb.Append(',');
				n++;
				sb.Append("{\"id\":").Append(e.Id)
				  .Append(",\"n\":").Append(Json(e.Short))
				  .Append(",\"p\":").Append(Json(e.Path))
				  .Append(",\"d\":").Append(d.ToString("F1", CultureInfo.InvariantCulture))
				  .Append(",\"on\":").Append(on ? "true" : "false")
				  .Append(",\"ours\":").Append(e.OffByUs ? "true" : "false")
				  .Append(",\"o\":").Append(Json(e.Owner ?? ""))
				  .Append(",\"m\":").Append(e.Mine ? "true" : "false").Append('}');
			}
			// NO SILENT CAP. If a world holds more behaviours than one message should carry, the
			// client is told how many it is actually looking at rather than left to assume the list
			// is complete.
			sb.Append("],\"shown\":").Append(n);
			}

			// The entry points of one behaviour, when the client asked for them. Read once per
			// selection and never during a scan: it costs a reflected call per behaviour.
			if (_udonEventList != null)
			{
				sb.Append(",\"events\":{\"id\":").Append(_udonEventsId).Append(",\"list\":[");
				for (int i = 0; i < _udonEventList.Count; i++)
				{
					if (i > 0) sb.Append(',');
					string ev = _udonEventList[i];
					// g = VRChat would let this one be networked. A '_' event never can be.
					sb.Append("{\"n\":").Append(Json(ev))
					  .Append(",\"g\":").Append(UdonManagerModule.IsGlobalEvent(null, ev) ? "true" : "false").Append('}');
				}
				sb.Append("]}");
			}

			// Every script's events, once, when the client asked for them. g = network-eligible names,
			// l = local-only (underscore) names. Consumed here so it is not resent every sync.
			if (_udonAllEvents != null)
			{
				sb.Append(",\"eventsAll\":").Append(_udonAllEvents);
				_udonAllEvents = null;
			}

			// The variables of one behaviour, on the same terms as its events: only when asked for,
			// and kept afterwards so an unrelated rebuild cannot swallow the answer. canWrite is the
			// mod's own verdict after resolving the setter — the client draws no SET button without
			// it, because a button that silently does nothing is worse than none.
			if (_udonVarList != null)
			{
				sb.Append(",\"vars\":{\"id\":").Append(_udonVarsId)
				  .Append(",\"canWrite\":").Append(UdonManagerModule.CanWrite ? "true" : "false")
				  .Append(",\"list\":[");
				for (int i = 0; i < _udonVarList.Count; i++)
				{
					if (i > 0) sb.Append(',');
					var v = _udonVarList[i];
					sb.Append("{\"n\":").Append(Json(v.Name))
					  .Append(",\"t\":").Append(Json(v.Type))
					  .Append(",\"v\":").Append(Json(v.Value))
					  .Append(",\"e\":").Append(v.Editable ? "true" : "false")
					  .Append(",\"s\":").Append(v.Synced ? "true" : "false").Append('}');
				}
				sb.Append("]}");
			}

			// WHAT THE LAST RUN CHANGED. Sent once after a udonRunDiff and kept like events/vars so a
			// later rebuild cannot swallow the answer; the client keys it by id+event.
			if (_udonDiffList != null)
			{
				sb.Append(",\"runDiff\":{\"id\":").Append(_udonDiffId)
				  .Append(",\"event\":").Append(Json(_udonDiffEvent ?? ""))
				  .Append(",\"changes\":[");
				for (int i = 0; i < _udonDiffList.Count; i++)
				{
					if (i > 0) sb.Append(',');
					var c = _udonDiffList[i];
					sb.Append("{\"n\":").Append(Json(c.Name))
					  .Append(",\"b\":").Append(Json(c.Before))
					  .Append(",\"a\":").Append(Json(c.After)).Append('}');
				}
				sb.Append("]}");
			}

			return sb.Append('}').ToString();
		}

        // EVERYTHING VRCHAT HOLDS about a player, read off the live APIUser. This is the record the
        // game itself is using, so it carries things no public page shows — the trust flags, the
        // platform, the current avatar's image — without a single API call.
		private static string DumpUser(VaTagsModule.PlayerEntry p)
		{
			var sb = new StringBuilder(2048).Append("{\n");
			void F(string k, string v) { sb.Append("  ").Append(Json(k)).Append(": ").Append(Json(v ?? "")).Append(",\n"); }

			F("displayName", p.Name);
			F("userId", p.UserId);
			F("avatarId", p.AvatarId);
			F("avatarName", p.AvatarName);
			F("platform", p.Platform);
			F("trustColor", p.TrustColor);
			sb.Append("  \"playerId\": ").Append(p.PlayerId).Append(",\n");
			sb.Append("  \"isLocal\": ").Append(p.IsLocal ? "true" : "false").Append(",\n");
			sb.Append("  \"isMaster\": ").Append(p.IsMaster ? "true" : "false").Append(",\n");
			sb.Append("  \"isOwner\": ").Append(p.IsOwner ? "true" : "false").Append(",\n");
			sb.Append("  \"vrcPlus\": ").Append(p.Plus ? "true" : "false").Append(",\n");
			sb.Append("  \"ageVerified\": ").Append(p.Adult ? "true" : "false").Append(",\n");
			// Block relationship, both directions, from the account's own moderation lists — NOT from
			// how the avatar is drawn. "blockedMe" is the one worth seeing: VRChat never says it out
			// loud, and the client draws such a player as a plain capsule with no explanation.
			try
			{
				string uid = p.UserId ?? "";
				sb.Append("  \"blockedMe\": ").Append(BlockedByProbeModule.BlockedMe.Contains(uid) ? "true" : "false").Append(",\n");
				sb.Append("  \"blockedByMe\": ").Append(BlockedByProbeModule.IBlocked.Contains(uid) ? "true" : "false").Append(",\n");
			}
			catch { }

			try
			{
				object apiUser = FewTagsModule.GetMemberByTypeName(p.Player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				var u = (apiUser as Il2CppSystem.Object)?.TryCast<VRC.Core.APIUser>();
				// Same dead-proxy rule as DumpAvatar: a property read on a non-live APIUser is an
				// access violation the try/catch cannot catch. Gate before reading anything.
				if (u != null && Core.NativeGuard.Alive(u))
				{
					try { F("bio", u.bio); } catch { }
					try { F("statusDescription", u.statusDescription); } catch { }
					try { F("status", u.status.ToString()); } catch { }
					try { F("currentAvatarImageUrl", u.currentAvatarImageUrl); } catch { }
					try { F("currentAvatarThumbnailImageUrl", u.currentAvatarThumbnailImageUrl); } catch { }
					try { F("profilePicOverride", u.profilePicOverride); } catch { }
					try { F("userIcon", u.userIcon); } catch { }
					try { F("last_platform", u.last_platform); } catch { }
					try
					{
						var tags = u.tags;
						sb.Append("  \"tags\": [");
						if (tags != null)
							for (int i = 0; i < tags.Count; i++)
							{
								if (i > 0) sb.Append(", ");
								sb.Append(Json(tags[i]));
							}
						sb.Append("],\n");
					}
					catch { }
				}
			}
			catch { }

			// The VA tags we hold for them, since the point of this dump is "everything known".
			try
			{
				var t = VaTagsModule.TagsOf(p.UserId);
				sb.Append("  \"vaTags\": [");
				if (t != null)
					for (int i = 0; i < t.Length; i++)
					{
						if (i > 0) sb.Append(", ");
						sb.Append(Json(t[i].Text));
					}
				sb.Append("]\n");
			}
			catch { sb.Append("  \"vaTags\": []\n"); }

			return sb.Append('}').ToString();
		}

		// The avatar record, from VRChat's own cache. Not fetched here: if the game has not loaded
		// it the honest answer is to say so rather than block the game on a web request.
		private static string DumpAvatar(string avatarId)
		{
			var sb = new StringBuilder(2048).Append("{\n");
			void F(string k, string v) { sb.Append("  ").Append(Json(k)).Append(": ").Append(Json(v ?? "")).Append(",\n"); }
			F("id", avatarId);
			try
			{
				var a = VRC.Core.API.FromCacheOrNew<VRC.Core.ApiAvatar>(avatarId);

				// A DEAD PROXY IS AN ACCESS VIOLATION, NOT AN EXCEPTION. Reading any property of an
				// ApiAvatar whose native object is not live crashes the whole process — the try/catch
				// around each read below cannot save it (see the dead-proxy note in the mod's memory).
				// So the object is checked for liveness BEFORE anything is read off it, and .Populated
				// itself is a read, so it must come after this gate too.
				if (a == null || !Core.NativeGuard.Alive(a))
				{
					sb.Append("  \"note\": \"private avatar - no metadata available\"\n");
					return sb.Append('}').ToString();
				}

				bool done = false; try { done = a.Populated; } catch { }
				if (!done)
				{
					sb.Append("  \"note\": \"private avatar - no metadata available\"\n");
					return sb.Append('}').ToString();
				}
				// STRING PROPERTIES ONLY. ArchiveHijack reads exactly these off an ApiAvatar every
				// day without crashing, which is the proof they are safe. The ones that USED to be
				// here and are gone — version, created_at, updated_at, tags — each read a nested
				// il2cpp value (a struct, a DateTime, an Il2Cpp list) whose own liveness the
				// NativeGuard.Alive(a) gate does NOT cover, and reading a dead nested object is the
				// same uncatchable access violation. They are not worth crashing the game for a
				// metadata dump; the fields that matter (name, author, release, image) stay.
				try { F("name", a.name); } catch { }
				try { F("authorName", a.authorName); } catch { }
				try { F("authorId", a.authorId); } catch { }
				try { F("releaseStatus", a.releaseStatus); } catch { }
				try { F("description", a.description); } catch { }
				try { F("imageUrl", a.imageUrl); } catch { }
				try { F("thumbnailImageUrl", a.thumbnailImageUrl); } catch { }
				// close the JSON with a stable last field so the trailing comma above is valid.
				sb.Append("  \"source\": \"mod\"\n");
			}
			catch (Exception e) { sb.Append("  \"error\": ").Append(Json(e.Message)).Append('\n'); }
			return sb.Append('}').ToString();
		}

		private static string BuildEvents()
		{
			var sb = new StringBuilder(4096);
			float newest = _lastEventAt;
			int n = 0;
			try
			{
				if (ModConfig.UdonLogEnabled.Value)
				{
					foreach (var e in UdonLogModule.Entries)
					{
						if (e == null || e.Time <= _lastEventAt) continue;
						if (e.Time > newest) newest = e.Time;
						if (n >= MaxEventsPerSync) continue;
						if (n++ > 0) sb.Append(',');
						sb.Append("{\"k\":\"udon\",\"t\":").Append(Json(e.Clock))
						  .Append(",\"who\":").Append(Json(e.User))
						  .Append(",\"col\":").Append(Json(e.UserColor))
						  .Append(",\"obj\":").Append(Json(e.Obj))
						  .Append(",\"ev\":").Append(Json(e.Event))
						  .Append(",\"rep\":").Append(e.Repeats).Append('}');
					}
				}

				if (ModConfig.NetworkLogEnabled.Value)
				{
					foreach (var e in NetworkLogModule.Snapshot())
					{
						if (e == null || e.Time <= _lastEventAt) continue;
						if (e.Time > newest) newest = e.Time;
						if (n >= MaxEventsPerSync) continue;
						if (n++ > 0) sb.Append(',');
						sb.Append("{\"k\":\"net\",\"t\":").Append(Json(e.Clock))
						  .Append(",\"code\":").Append(e.Code)
						  .Append(",\"sender\":").Append(e.Sender)
						  .Append(",\"rep\":").Append(e.Repeats).Append('}');
					}
				}
			}
			catch { }
			_lastEventAt = newest;
			return sb.ToString();
		}

		private static string SafeWorld()
		{
			try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
			catch { return ""; }
		}

		// ------------------------------------------------------------------ incoming
		//
		// The client answers a sync with the commands it has queued since our last one. Each carries
		// a sequence number so a reply that arrives twice cannot apply the same change twice.
		private void Apply(string raw)
		{
			try
			{
				if (string.IsNullOrEmpty(raw)) return;
				using var doc = JsonDocument.Parse(raw);
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object) return;

				if (!root.TryGetProperty("commands", out var cmds) || cmds.ValueKind != JsonValueKind.Array)
				{ Status = Linked ? "linked" : Status; return; }

				int done = 0;
				foreach (var c in cmds.EnumerateArray())
				{
					if (c.ValueKind != JsonValueKind.Object) continue;

					long seq = 0;
					try { if (c.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number) seq = s.GetInt64(); } catch { }

					// NOT a high-water mark. The client's counter restarts at zero every time the client
					// restarts, while ours lives as long as the VRChat session -- so after a client restart
					// every new command arrives numbered BELOW what we had already seen and a "seq <= _seq"
					// test drops them all, silently, until the counter climbs back. That is what made the
					// toggles feel unreliable: the client shows the new value while the mod never got it.
					// A bounded set of recently applied numbers still catches a genuine repeat and is immune
					// to the counter going backwards.
					if (seq != 0 && _recent.Contains(seq)) continue;

					string kind = Str(c, "kind");
					string cid = Str(c, "id");
					string cval = Str(c, "value");
					bool applied;
					if (kind == "set")
					{
						// A config write is managed-only and safe on this thread.
						Core.ConfigWatch.ApplyingFrom = "from the client";
						try { applied = ApplySet(cid, cval); }
						finally { Core.ConfigWatch.ApplyingFrom = null; }
					}
					else if (kind == "action")
					{
						// PURE-MANAGED ACTIONS SKIP THE FRAME QUEUE ENTIRELY.
						//
						// The queue below is PACED at one step per 10 ms so bulk networked actions cannot
						// burst. archiveLog is not a networked action at all \u2014 it appends one string to a
						// locked ring buffer (ArchiveFeed.Add) and touches no Unity/IL2CPP object \u2014 yet it
						// was paying the same 10 ms toll. The uploader emits a line per file, several per
						// second, so the console fell up to 42 s behind and, far worse, 4241 log lines in one
						// session filled the 4096-deep queue and made it DROP whatever landed next: 1119
						// commands lost, which is exactly what a user sees as "the toggle does nothing".
						// Running it inline (like "set" above, for the same reason) keeps the queue for the
						// things that genuinely need a frame.
						if (IsThreadSafeAction(cid))
						{
							applied = ApplyAction(cid, cval);
						}
						else
						{
							// MAIN THREAD ONLY. Apply() runs in the continuation of an awaited HTTP call \u2014 a
							// thread-pool thread with no IL2CPP GC attachment. An action touches Unity/IL2CPP, and
							// ObjectOrbit's Collect() alone does Resources.FindObjectsOfTypeAll and allocates
							// thousands of proxies; doing that here is a "Fatal error in GC: Collecting from unknown
							// thread". So EVERY action is queued onto the frame (the udon/force cases already do this
							// internally; this covers the older clone/teleport/orbit/objectOrbit/video ones too).
							// "Applied" therefore means ACCEPTED \u2014 the action runs on the next frame.
							// Toggle-class (local, single-step, no burst) → fast lane: applied THIS frame,
							// never queued behind a paced batch. Everything else keeps its 10 ms pacing.
							if (IsToggleClassAction(cid)) OnMainFast(() => ApplyAction(cid, cval));
							else OnMain(() => ApplyAction(cid, cval));
							applied = true;
						}
					}
					else applied = false;

					if (applied)
					{
						done++;
						// Everything EXCEPT the uploader's per-file log firehose gets a line: settings and
						// one-shot actions are rare and worth tracing, archiveLog is neither.
						if (!IsNoisyAction(kind, cid))
							VRChatArchiveModPlugin.Logger.LogInfo("[ModControl] applied " + kind + " " + cid + " = " + Str(c, "value"));
						Remember(seq);
					}
					else
					{
						// Deliberately NOT remembered: a command we could not apply must stay eligible.
						// And it is said out loud -- the client clears its queue the moment it hands a
						// command over, so a failure here is the last trace the change ever existed.
						_failed++;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[ModControl] could not apply " + kind + " '" + cid + "' from the client"
							+ (kind == "set" ? " (unknown setting, or the value did not fit its type)" : "") + ".");
					}

					if (seq > _seq) _seq = seq;
				}

				if (done > 0) { _applied += done; Status = "linked — " + _applied + " change(s) from the client" + (_failed > 0 ? ", " + _failed + " refused" : ""); }
				else if (Linked) Status = "linked";
			}
			catch (Exception e) { Status = "reply not understood: " + e.Message; }
		}

		/// <summary>Actions that touch NOTHING in Unity/IL2CPP and are safe to run straight on the
		/// HTTP continuation thread, so they never consume a slot in the paced frame queue.
		///
		/// Keep this list tiny and provable: an action qualifies only if every path it can take is
		/// pure managed code with its own locking. archiveLog is the one that matters — it is a
		/// per-file firehose from the uploader, and routing it through the frame queue is what
		/// saturated that queue and silently dropped real commands.</summary>
		private static bool IsThreadSafeAction(string id)
		{
			return string.Equals(id, "archiveLog", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>True for commands that arrive constantly and must not write one log line each.
		/// The uploader sends a line per file; logging every one of them cost 4241 disk writes in a
		/// single session and buried everything else in the log.</summary>
		private static bool IsNoisyAction(string kind, string id)
		{
			return string.Equals(kind, "action", StringComparison.Ordinal)
				&& string.Equals(id, "archiveLog", StringComparison.OrdinalIgnoreCase);
		}

		// id is "Section/Key". The value arrives as text and is converted to the setting's real type
		// — BoxedValue rejects a string handed to a float setting, which would silently do nothing.
		private static bool ApplySet(string id, string value)
		{
			try
			{
				if (string.IsNullOrEmpty(id)) return false;
				int slash = id.IndexOf('/');
				if (slash <= 0) return false;
				string section = id.Substring(0, slash), key = id.Substring(slash + 1);
				if (IsInternal(section, key)) return false;   // never settable from outside the game

				foreach (var e in Core.ConfigRegistry.Config())
				{
					if (e?.Raw == null) continue;
					if (!string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
					if (!string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) continue;

					object boxed = Convert(e.Raw.SettingType, value);
					if (boxed == null) return false;
					e.Raw.BoxedValue = boxed;
					return true;
				}
			}
			catch { }
			return false;
		}

		private static object Convert(Type t, string v)
		{
			try
			{
				if (t == null) return null;
				if (t == typeof(bool))
					return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
				if (t == typeof(float))
					return float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
				if (t == typeof(int))
					return int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
				if (t == typeof(string)) return v;
				if (t.IsEnum) return Enum.Parse(t, v, true);
			}
			catch { }
			return null;
		}

		// A REFUSAL THE CLIENT CAN SEE. ApplyAction runs from the main-thread queue, AFTER Apply()
		// has already logged the command as "applied" (applied means accepted there) — so a false
		// return from here reaches nobody. The status string rides the next sync as "lastStatus"
		// and the client paints it, which is the only way an empty id or a player who already left
		// stops looking like a button that does nothing.
		private static bool Refuse()
		{
			VaTagsModule.LastStatus = "mod could not find that player/id";
			return false;
		}

		// The object player's clip list: the built-in bake first, then whatever local clip files sit in
		// the mod's clips folder, so the client can offer them without knowing where they live.
		private static string MarkClipsJson()
		{
			var cb = new System.Text.StringBuilder("[\"badapple\"");
			try
			{
				foreach (var c in MarkModule.ListClips())
					if (!string.Equals(c, "badapple", StringComparison.OrdinalIgnoreCase))
						cb.Append(',').Append(Json(c));
			}
			catch { }
			return cb.Append(']').ToString();
		}

		// A number typed into a client box. Empty or unparseable = 0, which every caller reads as
		// "use the default". Invariant culture so a dot is always the decimal point.
		private static float ParseFloat(string value)
		{
			if (string.IsNullOrWhiteSpace(value)) return 0f;
			return float.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
		}

		// One-shot things that are not a setting: wear an avatar, clone a player, reset movement.
		private static bool ApplyAction(string id, string value)
		{
			try
			{
				switch (id)
				{
					case "wear":
						if (string.IsNullOrEmpty(value)) return Refuse();
						VaTagsModule.WearById(value, "");
						return true;

					// LOAD LOCAL (client v367): a .vrca the client just put in the test-avatar folder,
					// named by its file name. See VaTagsModule.WearLocal.
					case "wearLocal":
						if (string.IsNullOrEmpty(value)) return Refuse();
						VaTagsModule.WearLocal(value);
						return true;

					// Everything that acts ON A PLAYER takes their user id and looks the live entry up
					// here, rather than the client trying to describe a player it cannot hold.
					case "clone":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							VaTagsModule.CloneAvatar(p);
							return true;
						}

					case "teleport":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							VaTagsModule.TeleportTo(p);
							return true;
						}

					case "orbit":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							OrbitModule.Toggle(OrbitModule.Mode.Orbit, p);
							return true;
						}

					case "sit":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							OrbitModule.Toggle(OrbitModule.Mode.Sit, p);
							return true;
						}

					// Mimic: copy somebody's pose onto your own avatar (MimicPoseModule). A toggle on
					// the same player stops it; a different player switches the target.
					// MIMIC CHATBOX: repeat what they type into YOUR chatbox. Read-only in game — the
					// text is handed to the desktop client, which sends it over your own OSC.
					case "chatMimic":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							ChatMimicModule.Toggle(p);
							return true;
						}
					case "chatMimicStop":
						ChatMimicModule.Stop("stopped from the client");
						return true;

					case "mimic":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							MimicPoseModule.Toggle(p);
							return true;
						}
					case "mimicStop":
						MimicPoseModule.Stop("stopped from the client");
						return true;
					case "mimicMirror":   // "1" / "0": mirror the copied pose (their left = your right)
						MimicPoseModule.SetMirror(value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
						return true;

					// Voice mimic: park at somebody's head so your spatial voice seems to come from
					// them (VoiceMimicModule). Same toggle shape as mimic: same player stops it, a
					// different player switches the target.
					case "voiceMimic":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							VoiceMimicModule.Toggle(p);
							return true;
						}
					case "voiceMimicStop":
						VoiceMimicModule.Stop("stopped from the client");
						return true;

					// Objects orbiting a player — the ring feature that already existed in the
					// in-game menu but had no way in from the client.
					case "objectOrbit":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							ObjectOrbitModule.ToggleOnPlayer(p);
							return true;
						}

					case "objectOrbitStop":
						ObjectOrbitModule.Stop("stopped from the client");
						return true;

					// ELEVATOR — the world's loose props gathered into a platform UNDER the selected
					// player that rises up the Y axis. Same class of action as the ring: it only ever
					// moves objects (owned pickups when synced), never the player; whoever stands on the
					// platform is carried by their own physics, the way any world elevator works.
					case "elevatorPlayer":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							ElevatorModule.ToggleOnPlayer(p);
							return true;
						}

					case "elevatorPlayerStop":
						ElevatorModule.Stop("stopped from the client");
						return true;

					// ELEVATOR number boxes from the client. value = the typed number; 0 (or empty/bad)
					// means "use the configured default". Applied live, so a change takes effect on the
					// current ride without restarting it.
					case "elevatorSpeed":
						ElevatorModule.SpeedOverride = ParseFloat(value);
						return true;

					case "elevatorHeight":
						ElevatorModule.HeightOverride = ParseFloat(value);
						return true;

					// ELEVATOR auto-correct toggle from the client switch. value = 1/true (on) or 0/false.
					// Written straight to the config so it persists and Mod Settings shows the same state.
					case "elevatorAutoCorrect":
						try
						{
							string v = (value ?? "").Trim().ToLowerInvariant();
							ModConfig.ElevatorAutoCorrect.Value = (v == "1" || v == "true" || v == "on" || v == "yes");
						}
						catch { }
						return true;

					// (selfLift / selfLiftStop — the old "Probe Lifter" that raised YOU via ProbPusherModule
					// — were removed; the client's renamed ELEVATOR switch drives elevatorPlayer above.)

					// Push a URL into every video player in the world. value = the URL.
					//
					// MUST run on the Unity main thread. ApplyAction runs on a THREAD POOL thread (the
					// await continuation in SyncAsync has no Unity SynchronizationContext), and Inject
					// calls FindObjectsOfType / LoadURL / SetProgramVariable / SetOwner -- native Unity
					// calls that Unity refuses off the main thread, "not always by a catchable exception"
					// (see the channel-traps note). That off-thread native call IS the intermittent crash:
					// it happened to work when BuildSync had put us on main, and took the game down when
					// it did not. Queued like every udon command, so the real result comes back in the
					// next sync's status line ("applied" here just means accepted).
					case "videoUrl":
						{
							string vurl = value;
							OnMain(() =>
							{
								// NO QueueResult here: the `results` queue is a SHARED channel that
								// PlayersPage.DrainDumps() drains wholesale and copies to the clipboard as
								// "Avatar metadata copied" (channel-traps PIÈGE 2). Routing the video status
								// through it produced that nonsensical toast. The status lives in the mod
								// log (VideoUrlModule.LastStatus); the FUN page shows its own confirmation.
								try { VideoUrlModule.Inject(vurl); }
								catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] inject threw: " + ex.Message); }
							});
							return true;   // accepted; runs on the next main-thread pump
						}

					case "orbitStop":
						OrbitModule.Stop("stopped from the client");
						return true;

					case "resetMovement":
						SpeedModule.ResetToWorld();
						return true;

					// Force jump — a one-shot upward launch of the local player. value = optional force
					// override (metres/second); empty means use the configured default. Ordinary local
					// movement, queued to the main thread like everything that touches the player.
					// FORCE JUMP: a TOGGLE, not a one-shot. While on, it holds your jump impulse open so you
					// can jump (Space) at the configured strength EVEN in worlds that disabled jumping;
					// toggling off restores the world's own jump. value is ignored — strength is the slider.
					case "forceJump":
						OnMain(() => ForceJumpModule.Toggle());
						return true;

					// HUD: one switch over everything drawn on screen (radar, panels, glows, Udon overlay).
					// Off remembers what was on; on puts exactly that back. Menus are not touched.
					case "hud":
						OnMain(() =>
						{
							if (value == "on") Core.HudMaster.Set(true);
							else if (value == "off") Core.HudMaster.Set(false);
							else Core.HudMaster.Toggle();
						});
						return true;

					// TRUEVIEW: a proxied player keeps their real avatar, nameplate and hitbox.
					case "trueView":
						OnMain(() => TrueViewModule.Toggle());
						return true;

					// GHOST: toggle the local player's FlatBufferNetworkSerializer — frozen for everyone
					// else, moving for yourself. Exactly the UnityExplorer gesture, as one button.
					case "ghost":
						OnMain(() => GhostModule.Toggle());
						return true;

					// FAST SYNC. It had NO switch anywhere — not here, not in the mod's menu, not in the
					// client — so the only way to turn it on was hand-editing the BepInEx config, and it
					// sat at its default of false forever while its log line said "armed". A module that
					// is registered, resolves cleanly and cannot be reached is the same failure as
					// WhoBlockedMe before it: shipped, and unreachable.
					case "fastSync":
						OnMain(() =>
						{
							try
							{
								if (ModConfig.FastSync == null) return;
								ModConfig.FastSync.Value = !ModConfig.FastSync.Value;
							}
							catch { }
						});
						return true;

					// PLAYER ROTATOR: tilt your own capsule (arrows / PageUp / PageDown) with the neck
					// clamp widened so mouse-look can follow you past vertical. "flip" is the one-shot
					// upside down, "reset" puts you back upright without switching the rotator off.
					case "rotator":
						OnMain(() => PlayerRotatorModule.Toggle());
						return true;
					case "rotatorFlip":
						OnMain(() => PlayerRotatorModule.Flip());
						return true;
					case "rotatorReset":
						OnMain(() => PlayerRotatorModule.ResetUpright());
						return true;
					case "boxDrop":
						OnMain(() => BoxDropModule.Toggle());
						return true;
					case "portalInfinite":
						OnMain(() => PortalInfiniteModule.Toggle());
						return true;
					case "boxDropX":
						OnMain(() => BoxDropModule.SetAxis('x', ParseFloat(value)));
						return true;
					case "boxDropY":
						OnMain(() => BoxDropModule.SetAxis('y', ParseFloat(value)));
						return true;
					case "boxDropZ":
						OnMain(() => BoxDropModule.SetAxis('z', ParseFloat(value)));
						return true;
					// FLOAT OBJECTS: useGravity = false on every pickup's Rigidbody (yours float for everyone).
					case "floatObjects":
						OnMain(() => ObjectGravityModule.Toggle());
						return true;

					// MENU BACKGROUNDS: the VRC+ menu backgrounds, animated ones included, shown
					// locally. Without a command the module was registered and could never be switched
					// on — it shipped as dead code.
					case "menuBackgrounds":
						OnMain(() => VrcPlusBackgroundsModule.Toggle());
						return true;

					// LAUNCH PAD CONSOLE: the event console in place of VRChat's promo carousel.
					case "launchpadConsole":
						OnMain(() => LaunchpadConsoleModule.Toggle());
						return true;

					// ARCHIVE FEED: lines pushed by the desktop client — what the AUTO ARCHIVER is
					// uploading and what the CACHE VIEWER is recording — so the in-game console can
					// show the work the Archive is actually doing while you play.
					// Payload is "<archiver|cache>|<text>"; the text may itself contain '|', so the
					// split takes only the FIRST separator.
					case "archiveLog":
					{
						string payload = value ?? "";
						int cut = payload.IndexOf('|');
						string kindStr = cut >= 0 ? payload.Substring(0, cut) : "archiver";
						string text = cut >= 0 ? payload.Substring(cut + 1) : payload;
						var kind = string.Equals(kindStr, "cache", StringComparison.OrdinalIgnoreCase)
							? Core.ArchiveFeed.Kind.Cache : Core.ArchiveFeed.Kind.Archiver;
						// No OnMain: this only appends to a list, and the console reads it on its own
						// tick. Hopping to the main thread for a string would serialise the uploader.
						Core.ArchiveFeed.Add(kind, text);
						return true;
					}

					// Switches the console between ARCHIVER LOGS and CACHE LOGS.
					case "archiveLogToggle":
						OnMain(() => Core.ArchiveFeed.Toggle());
						return true;

					// FORCE JOIN: go to a world or instance by id. OnMain because GoToRoom starts a room
					// transition, which is main-thread work.
					case "forceJoin":
					{
						string room = value ?? "";
						OnMain(() => Core.ForceJoin.Go(room));
						return true;
					}

					// WHO BLOCKED ME: asked ONLY here, never on a timer. The fetch needs the il2cpp
					// delegate bridge, which this mod documents as able to kill the process, so it is
					// something the user chooses to do — not something that happens to them.
					case "whoBlockedMe":
						OnMain(() => BlockedByProbeModule.RequestFetch());
						return true;

					// DUMP MENU TREE: writes VRChat's live menu hierarchy to a text file. Read-only, and
					// the prerequisite for building our UI out of CLONES of VRChat's own buttons rather
					// than hand-made GameObjects — a clone needs the PATH of what it clones, and those
					// paths move with every VRChat release (Page_DevTools, which a reference client
					// clones for its tab, no longer exists on this build; it is Page_Launchpad now).
					case "dumpUi":
						OnMain(() => UiTreeDumpModule.Request());
						return true;

					// PLAYER GRAB (mod users grab each other): the toggle, and the desktop aim grab / release.
					case "playerGrab":
						OnMain(() => PlayerGrabModule.Toggle());
						return true;
					case "playerGrabAim":
						OnMain(() => PlayerGrabModule.GrabOrReleaseAimed());
						return true;

					// MARK: an aim-placed anchor for the world's loose objects (TP / orbit / shapes / object art).
					case "markPut": OnMain(() => MarkModule.PutMark()); return true;
					case "markClear": OnMain(() => MarkModule.Clear()); return true;
					case "markTp": OnMain(() => MarkModule.TeleportObjectsToMark()); return true;
					case "markOrbit": OnMain(() => MarkModule.OrbitAroundMark()); return true;
					case "markShape": { string shp = value; OnMain(() => MarkModule.Shape(shp)); return true; }
					case "markMode": { string mm = value; OnMain(() => MarkModule.SetMode(mm)); return true; }   // local | vrchat for shapes / TP / orbit
					// value = "local" | "vrchat", optionally "<mode>:<clip>" to pick which clip to play
					// ("badapple" = the built-in bake, anything else = a local file in the clips folder).
					case "markBadApple":
						{
							string raw = value ?? "";
							int cut = raw.IndexOf(':');
							string md = cut >= 0 ? raw.Substring(0, cut) : raw;
							string clip = cut >= 0 ? raw.Substring(cut + 1) : "";
							OnMain(() => MarkModule.ToggleBadApple(md, clip));
							return true;
						}

					// FORCE PICKUP: a toggle, not a one-shot. Unlocks the world's locked pickups so you
					// grab them with your own hands; toggling off puts every one back.
					case "forcePickup":
						OnMain(() => ForcePickupModule.Toggle());
						return true;

					// FORCE GRAB: toggle grab/drop of whatever the crosshair is on. Aim happens in the
					// game, so the client's button is really a grab / drop \u2014 the reach and the lock
					// override live in the mod (Fun/ForceGrab*). This is the ONLY way in: the in-game
					// hotkey is gone, and the client draws its button from the forceGrab/forceGrabName
					// pair BuildSync sends back. Queued to the main thread; it raycasts and drives transforms.
					case "forceGrab":
						OnMain(() => ForceGrabModule.Toggle());
						return true;

					// The client just added or removed a favourite. Without this the in-game
					// sections would show the old list until their own slow refresh came round,
					// which reads as the change not having worked.
					// Metadata dumps. The answer goes back through the results queue, which the
					// client copies to the clipboard.
					case "dumpUser":
						{
							var p = FindPlayer(value);
							if (p == null) return Refuse();
							QueueResult("user", value, DumpUser(p));
							return true;
						}

					case "dumpAvatar":
						{
							if (string.IsNullOrEmpty(value)) return false;
							QueueResult("avatar", value, DumpAvatar(value));
							return true;
						}

					// The client just wrote a tag. Without this the game keeps showing the old set
					// until the five-minute refresh, which reads as the write not having worked.
					case "refreshTags":
						VaTagsModule.RequestRefresh();
						return true;

					case "refreshFavs":
						// Only the AVATAR list is refreshed: world/user favourites are the client's job
						// (Plugin.cs:75-88 — WorldFavoritesModule / UserFavoritesModule are not registered,
						// so refreshing them was two POSTs per favourite change that nothing ever read).
						_ = FavoritesModule.RefreshAsync();
						return true;

					// ---- UDON MANAGER ------------------------------------------------------------
					//
					// The page lives in the desktop client; the work happens here. Every one of these is
					// QUEUED for the main thread rather than performed now — see OnMain. "Applied"
					// therefore means accepted; what the behaviour actually did comes back as the status
					// line in the next payload, which is the half the client shows.
					case "udonScan":
						OnMain(() => { UdonManagerModule.Rescan(); _udonPayload = BuildUdon(); });
						return true;

					case "udonRestore":
						OnMain(() => { UdonManagerModule.RestoreAll(); _udonPayload = BuildUdon(); });
						return true;

					// "<instanceId>:1" — switch one script on or off in THIS client.
					case "udonToggle":
						{
							int c = (value ?? "").LastIndexOf(':');
							if (c <= 0 || !int.TryParse(value.Substring(0, c), out int bid)) return false;
							bool on = value.Substring(c + 1) == "1";
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								if (e != null) UdonManagerModule.SetEnabled(e, on);
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "1:<id>,<id>,…" — the bulk switch behind DISABLE / ENABLE ALL SHOWN. The client
					// sends the ids it is showing, because the filter that produced them lives there.
					case "udonMany":
						{
							int c = (value ?? "").IndexOf(':');
							if (c <= 0) return false;
							bool on = value.Substring(0, c) == "1";
							string[] parts = value.Substring(c + 1).Split(',');
							OnMain(() =>
							{
								var batch = new List<UdonManagerModule.Entry>(parts.Length);
								foreach (string s in parts)
									if (int.TryParse(s, out int one))
									{
										var e = UdonManagerModule.ById(one);
										if (e != null) batch.Add(e);
									}
								UdonManagerModule.SetMany(batch, on);
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// SOUNDBOARD. "sbPlay" broadcasts a clip KEY to the archive's feed, which every mod
					// client polls and plays locally with its own volume \u2014 no audio ever crosses the wire.
					// "sbPreview" plays it for you alone and announces nothing.
					// BAD APPLE in the chatbox. Toggles the OSC playback thread; VRChat's own OSC input
					// must be enabled in the radial menu for anything to appear.
					case "badApple":
						OnMain(() => BadAppleModule.RequestToggle());
						return true;

					// ---- PROTECTION ---------------------------------------------------------------
					//
					// Both are one-shots against guard state, not settings, so they live here and not
					// in ApplySet. Queued like everything else: the anti-crash rescan walks live
					// avatar hierarchies, which is main-thread work. Apply() already logged the
					// command as accepted; the line inside the lambda is the trace that it actually
					// ran on the frame, which is the half a "button that does nothing" report needs.
					//
					// RESCAN: forget which avatars were already processed and run the scan again, so a
					// threshold changed in the client applies to avatars already in the room instead of
					// only to the next one to load.
					case "antiCrashRescan":
						OnMain(() =>
						{
							AntiCrashModule.RequestRescan();
							VRChatArchiveModPlugin.Logger.LogInfo("[ModControl] applied action antiCrashRescan");
						});
						return true;

					// RESET: lift every (actor, code) suspension and zero the counters. A player muted
					// for a flood that has stopped gets their events back at once instead of waiting
					// out SuspendSeconds; the block list from config is NOT touched.
					case "photonGuardReset":
						OnMain(() =>
						{
							PhotonGuardModule.Reset();
							VRChatArchiveModPlugin.Logger.LogInfo("[ModControl] applied action photonGuardReset");
						});
						return true;

					case "sbPlay":
					case "sbPreview":
						{
							if (string.IsNullOrEmpty(value)) return false;
							bool preview = id == "sbPreview";
							OnMain(() =>
							{
								foreach (var c in SoundboardModule.Clips)
								{
									if (c == null || !string.Equals(c.Key, value, StringComparison.OrdinalIgnoreCase)) continue;
									if (preview) SoundboardModule.Preview(c); else SoundboardModule.Send(c);
									break;
								}
							});
							return true;
						}

					// PRESS a script's interact button on the main thread. The world's _interact handler
					// runs and networks its own effect \u2014 the "global interact".
					case "udonInteract":
						{
							if (!int.TryParse(value, out int bid)) return false;
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								// A miss must SAY so: the client's INTERACT toast only asserts "queued" and then
								// shows the mod's status line, so a silent no-op here read as a broken button.
								if (e != null) UdonManagerModule.Interact(e);
								else UdonManagerModule.Status = "interact: script " + bid + " is gone — press SCAN";
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// The entry points of one script, answered on the next sync.
					case "udonEvents":
						{
							if (!int.TryParse(value, out int bid)) return false;
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								_udonEventsId = bid;
								_udonEventList = e != null ? UdonManagerModule.EntryPoints(e) : new List<string>();
								_udonPayload = BuildUdon(false);
							});
							return true;
						}

					// The variables of one script, answered on the next sync.
					// Entry points of EVERY listed script in one payload. One reflected call per behaviour,
					// so a 300-script world costs a few hundred milliseconds ONCE, on demand, never on a timer.
					case "udonEventsAll":
						OnMain(() =>
						{
							var rows = UdonManagerModule.Snapshot();
							var sbA = new StringBuilder(64 * 1024);
							sbA.Append('[');
							int nA = 0;
							foreach (var e in rows)
							{
								if (e == null || e.B == null) continue;
								List<string> eps;
								try { eps = UdonManagerModule.EntryPoints(e); } catch { continue; }
								if (eps == null || eps.Count == 0) continue;
								if (nA++ > 0) sbA.Append(',');
								sbA.Append("{\"id\":").Append(e.Id).Append(",\"g\":[");
								int g = 0, l = 0;
								foreach (var ev in eps) if (UdonManagerModule.IsGlobalEvent(null, ev)) { if (g++ > 0) sbA.Append(','); sbA.Append(Json(ev)); }
								sbA.Append("],\"l\":[");
								foreach (var ev in eps) if (!UdonManagerModule.IsGlobalEvent(null, ev)) { if (l++ > 0) sbA.Append(','); sbA.Append(Json(ev)); }
								sbA.Append("]}");
							}
							sbA.Append(']');
							_udonAllEvents = sbA.ToString();
							VRChatArchiveModPlugin.Logger.LogInfo("[UdonManager] events of " + nA + " script(s) sent to the client in one answer.");
							_udonPayload = BuildUdon();
						});
						return true;

					case "udonVars":
						{
							if (!int.TryParse(value, out int bid)) return false;
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								_udonVarsId = bid;
								_udonVarList = e != null
									? UdonManagerModule.Variables(e)
									: new List<UdonManagerModule.Var>();
								_udonPayload = BuildUdon(false);
							});
							return true;
						}

					// "<id>|<name>|<value>". The VALUE may itself contain a '|' — a world's string variable
					// can hold anything — so only the first two separators are separators.
					// "<id>". Networking.SetOwner(localPlayer, object): after it our synced writes are the ones
					// VRChat keeps and RUN ON OWNER runs on us. Refused by the mod on an object with no network
					// state (SetOwner there crashes the game), and the status line says so.
					case "udonOwn":
						{
							if (!int.TryParse(value, out int obid)) return false;
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(obid);
								if (e != null) UdonManagerModule.TakeOwnership(e);
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// udonSetVarSync: same payload, but the mod takes ownership first and requests serialization
					// after, so the value reaches everyone instead of snapping back on the owner's next tick.
					case "udonSetVar":
					case "udonSetVarSync":
						{
							string raw = value ?? "";
							int p1 = raw.IndexOf('|');
							if (p1 <= 0) return false;
							int p2 = raw.IndexOf('|', p1 + 1);
							if (p2 < 0) return false;
							if (!int.TryParse(raw.Substring(0, p1), out int bid)) return false;
							string vname = raw.Substring(p1 + 1, p2 - p1 - 1);
							string vtext = raw.Substring(p2 + 1);
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								if (e != null)
								{
									UdonManagerModule.SetVariable(e, vname, vtext, id == "udonSetVarSync");
									// RE-READ IMMEDIATELY. The point of writing is seeing whether it took, and a
									// synced variable owned by somebody else can already have snapped back to
									// theirs by now. Showing the value we asked for would hide exactly that.
									_udonVarsId = bid;
									_udonVarList = UdonManagerModule.Variables(e);
								}
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "<id>|<event>". TWO COMMANDS, not one with a flag: udonRun executes the event in
					// this client and nothing leaves the process, while udonRunGlobal broadcasts it to
					// everyone in the instance over VRChat's own networking. A mistyped value must never
					// be able to turn the first into the second.
					case "udonRun":
					case "udonRunOwner":     // "<id>|<event>" -> the object's OWNER only
					case "udonRunGlobal":
						{
							int bar = (value ?? "").IndexOf('|');
							if (bar <= 0 || !int.TryParse(value.Substring(0, bar), out int bid)) return false;
							string ev = value.Substring(bar + 1);
							bool global = id == "udonRunGlobal";
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(bid);
								if (e != null)
								{
									if (id == "udonRunOwner") UdonManagerModule.RunOwner(e, ev);
									else if (global) UdonManagerModule.RunGlobal(e, ev);
									else UdonManagerModule.RunLocal(e, ev);
								}
								_udonPayload = BuildUdon();
							});
							return true;
						}

					// "<id>|<event>". Runs the event LOCALLY and reports which of the behaviour's
					// variables changed -- the "what does this event do" answer. Never networked.
					// PRESET: fire one event on EVERY script whose object name matches a pattern. The client's
					// world presets (Murder / Prison / Among Us) are all this one command. Value is
					// "<scope>|<match>|<event>|<pattern>" — scope global|local|owner|interact, match
					// exact|prefix|contains. The event may be empty for interact. The pattern is last so it
					// can itself contain no separators we need past the third '|'.
					case "udonRunMatch":
						{
							string raw = value ?? "";
							string[] parts = raw.Split(new[] { '|' }, 4);
							if (parts.Length < 4) return false;
							string scope = parts[0], match = parts[1], ev = parts[2], pattern = parts[3];
							OnMain(() =>
							{
								// UNKNOWN SCOPE IS REFUSED, never turned into a global broadcast.
								if (!UdonManagerModule.IsScope(scope))
								{
									UdonManagerModule.Status = "unknown scope '" + scope + "' — nothing sent (use global/local/owner/interact)";
									_udonPayload = BuildUdon();
									return;
								}
								var hits = UdonManagerModule.MatchEntries(pattern, match);
								string what = string.Equals(scope, "interact", StringComparison.OrdinalIgnoreCase) ? "interact" : ev;
								UdonManagerModule.MatchBegin(hits.Count, what, pattern);
								_udonPayload = BuildUdon();
								// ONE PACED STEP PER OBJECT. PumpMain spends ~1-2 of these per frame, so even a
								// preset that matches hundreds of objects goes out at the same cadence a single
								// udonRunGlobal would — no burst, no disconnect.
								foreach (var entry in hits)
								{
									var e = entry;
									OnMain(() => { UdonManagerModule.RunOneTracked(scope, ev, e); _udonPayload = BuildUdon(); });
								}
							});
							return true;
						}

					// PER-PLAYER preset: fire one world event aimed at ONE player. Value is
					// "<userId>|<scope>|<match>|<event>|<by>|<pattern>" — by = near (the matching Udon
					// object closest to that player) or label (the object nearest the world-UI text that
					// shows their name). The PATTERN IS LAST, like udonRunMatch: it is the one free-text
					// field, so it may itself contain '|' and still arrive whole. The client's PLAYERS page
					// drives this from its per-player presets.
					case "udonPlayerEvent":
						{
							// SEVEN FIELDS NOW, AND SIX STILL WORK. by:var needs the NAME of the variable
							// that identifies the player (byVar), so it rides between `by` and `pattern` —
							// pattern stays last because a world object's name may itself contain a '|'.
							// A client that has not been updated sends six and lands in the old shape with
							// no byVar, which is right: the two geometric modes never needed one.
							string raw = value ?? "";
							string[] parts = raw.Split(new[] { '|' }, 7);
							if (parts.Length < 6) return false;
							string uid = parts[0], scope = parts[1], match = parts[2], ev = parts[3], by = parts[4];
							string byVar = parts.Length >= 7 ? parts[5] : "";
							string pattern = parts.Length >= 7 ? parts[6] : parts[5];
							OnMain(() =>
							{
								if (!UdonManagerModule.IsScope(scope))
								{
									UdonManagerModule.Status = "unknown scope '" + scope + "' — nothing sent";
									_udonPayload = BuildUdon();
									return;
								}
								var p = FindPlayer(uid);
								if (p == null) { UdonManagerModule.Status = "that player is no longer in the instance"; _udonPayload = BuildUdon(); return; }
								UdonManagerModule.RunOnPlayer(p.Name, p.Position, p.HasPos, scope, ev, pattern, match, by, byVar, p.PlayerId);
								_udonPayload = BuildUdon();
							});
							return true;
						}

					case "udonRunDiff":
						{
							int bar = (value ?? "").IndexOf('|');
							if (bar <= 0 || !int.TryParse(value.Substring(0, bar), out int dbid)) return false;
							string ev = value.Substring(bar + 1);
							OnMain(() =>
							{
								var e = UdonManagerModule.ById(dbid);
								_udonDiffId = dbid;
								_udonDiffEvent = ev;
								_udonDiffList = e != null ? UdonManagerModule.RunAndDiff(e, ev) : new List<UdonManagerModule.VarChange>();
								if (e != null) { _udonVarsId = dbid; _udonVarList = UdonManagerModule.Variables(e); }
								_udonPayload = BuildUdon();
							});
							return true;
						}


				}
			}
			catch { }
			return false;
		}

		// ONE LOOKUP PER AVATAR, not per sync. The release status only changes when somebody
		// switches avatar, so it is cached against the avatar id and re-read when that changes —
		// otherwise every player would be re-resolved once a second for an answer that is stable.
		private static readonly Dictionary<string, string> ReleaseCache = new Dictionary<string, string>(StringComparer.Ordinal);

		private static string ReleaseCached(VaTagsModule.PlayerEntry p)
		{
			try
			{
				string key = (p.UserId ?? "") + "|" + (p.AvatarId ?? "");
				if (string.IsNullOrEmpty(p.AvatarId)) return "";
				if (ReleaseCache.TryGetValue(key, out string cached) && !string.IsNullOrEmpty(cached)) return cached;
				string rs = VaTagsModule.ReleaseStatusOf(p);
				if (!string.IsNullOrEmpty(rs))
				{
					// Bounded: an instance turns over, and this must not grow for a whole session.
					if (ReleaseCache.Count > 400) ReleaseCache.Clear();
					ReleaseCache[key] = rs;
				}
				return rs;
			}
			catch { return ""; }
		}

		private static VaTagsModule.PlayerEntry FindPlayer(string userId)
		{
			try
			{
				if (string.IsNullOrEmpty(userId)) return null;
				var roster = VaTagsModule.Roster;
				lock (roster)
				{
					foreach (var p in roster)
					{
						if (p == null) continue;
						if (string.Equals(p.UserId, userId, StringComparison.Ordinal)) return p;
					}
				}
			}
			catch { }
			return null;
		}

		private static string Str(JsonElement e, string prop)
			=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

		// Minimal JSON string encoder — the mod builds its payloads by hand everywhere else too.
		// THE SOUNDBOARD, as the client's FUN page draws it: what can be played, and who played what.
		// The history is the mod's own rolling log, so the console shows every clip the instance heard,
		// not just the ones this client sent.
		private static string BuildSoundboard()
		{
			var sb = new StringBuilder(1024);
			sb.Append("{\"clips\":[");
			try
			{
				var clips = SoundboardModule.Clips;
				for (int i = 0; i < clips.Length; i++)
				{
					if (clips[i] == null) continue;
					if (i > 0) sb.Append(',');
					sb.Append("{\"key\":").Append(Json(clips[i].Key))
					  .Append(",\"label\":").Append(Json(clips[i].Label));

					// THE ICON, inline as base64. The client cannot read a Texture2D out of the game, and
					// shipping the PNGs beside it would mean two copies to keep in step \u2014 so the picture
					// travels with the clip list. Sent ONCE per clip: after that the client has it and we
					// send only the name, because these ride every sync.
					try
					{
						string res = string.IsNullOrEmpty(clips[i].Image) ? "5560-heart-rem.png" : clips[i].Image;
						if (_iconSent.Add(clips[i].Key))
						{
							byte[] png = Core.AssetLoader.RawBytes(res);
							if (png != null && png.Length > 0 && png.Length < 512 * 1024)
								sb.Append(",\"icon\":").Append(Json(System.Convert.ToBase64String(png)));
						}
					}
					catch { }
					sb.Append('}');
				}
			}
			catch { }
			sb.Append("],\"recent\":[");
			try
			{
				var hist = SoundboardModule.Recent();
				// Newest last in the mod's list; the client renders it as a console.
				for (int i = 0; i < hist.Count; i++)
				{
					var h = hist[i];
					if (h == null) continue;
					if (i > 0) sb.Append(',');
					sb.Append("{\"t\":").Append(Json(h.When))
					  .Append(",\"who\":").Append(Json(h.Who))
					  .Append(",\"what\":").Append(Json(h.What)).Append('}');
				}
			}
			catch { }
			sb.Append("],\"status\":").Append(Json(SoundboardModule.LastStatus ?? ""));
			return sb.Append('}').ToString();
		}

		// A float for JSON: invariant culture (never a comma decimal) and NaN/Infinity flattened to 0
		// so one broken transform cannot corrupt the whole payload.
		private static string SafeF(float v)
		{
			if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
			return v.ToString("F2", CultureInfo.InvariantCulture);
		}

		private static string Json(string s)
		{
			if (s == null) return "\"\"";
			var sb = new StringBuilder(s.Length + 2);
			sb.Append('"');
			foreach (char c in s)
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
						else sb.Append(c);
						break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}
}
