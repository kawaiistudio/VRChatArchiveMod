using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// VRChatArchive player tagging: community tags keyed by VRChat user id, stored in the
	// site's DATA/va_tags_db.jsonl and served through /api/va-tags on vrchatarchive.org.
	// Live instance roster (name + usr_ id + trust colour + avatar + live position), synced
	// tag DB, styled nameplate plates (FewTags technique — colour/bold/italic/underline plus
	// animated rainbow/blink/wave/glow), and the menu's PLAYERS tab (TP, clone-avatar, add/
	// remove tags with a style picker, add-by-user-id).
	//
	// Tags are VRChatArchive metadata: only mod users see them; VRChat's own profile/UI is
	// never touched. Each tag carries a structured style (never raw richText) — the site
	// validates plain fields and the mod composes the richText locally, so injection is
	// impossible. Writes go through the authenticated site API (shared key + rate limit).
	public class VaTagsModule : IModule
	{
		public override string Name => "VaTags";

		// A styled tag (mirrors the server's {text,color,b,i,u,fx}).
		public sealed class VaTag
		{
			public string Text = "";
			public string Color = "";   // "#rrggbb" or "" (none)
			public bool B, I, U;
			public string Fx = "none";  // none | rainbow | blink | wave | glow
		}

		public sealed class PlayerEntry
		{
			public string Name;
			public string UserId;
			public int PlayerId = -1;   // VRChat/photon actor number
			public string AvatarId;
			public string AvatarName;
			public bool IsLocal;
			public bool IsOwner;   // creator of the current instance (ApiWorldInstance.ownerId)
			// Instance MASTER \u2014 read from VRCPlayerApi.isMaster, which the SDK exposes directly, so
			// this is the game's own answer and not the usual "lowest player id wins" guess. The two
			// are different things: the owner created the instance, the master is whoever currently
			// holds it and changes when they leave.
			public bool IsMaster;
			public string TrustColor = "#CCCCCC";
			// Read once per roster refresh from the same APIUser (never per frame).
			public bool Plus;        // VRC+
			public bool Adult;       // 18+ (age verified)
			public string Platform = "";   // "PC" | "Quest"
			public bool ApiResolved;       // false until the async APIUser actually loaded; retried each pass
			// LIVE STATE, read off the avatar's own animator parameters (PlayerStatesModule).
			//
			// VRChat drives AFK, Seated, InStation and VRMode on every avatar and syncs them, so these
			// are the game's own answers about a REMOTE player rather than a guess from how they are
			// drawn. StateKnown stays false until an animator carrying those parameters has actually
			// been read: an avatar still loading, or a rig that exposes none of them, must not be
			// reported as "not AFK" when the truth is "not known yet".
			public bool StateKnown;
			// VR is a SEPARATE question from the animator states, and it needs its own known-flag.
			// StateKnown means "their avatar's animator answered"; VrKnown means "VRChat's own SDK
			// answered IsUserInVR". A PC player in a headset whose avatar declares no VRMode parameter
			// has StateKnown=false and VrKnown=true — one flag could not carry both.
			public bool VrKnown;
			public bool Afk;
			public bool Seated;
			public bool InStation;
			public bool InVR;
			public object Player;
			// RESOLVED ONCE, REUSED WHILE IT LIVES (2026-09-13). The roster pass used to re-resolve
			// the VRCPlayerApi and walk api -> gameObject -> transform for EVERY player on EVERY
			// pass: four il2cpp crossings each, 35 players, for two references that only change when
			// the player's avatar is rebuilt. Both are kept here and re-resolved only when the
			// cached one fails its liveness proof, which is exactly when it really did change.
			public object ApiObj;
			// How many times ReadApiFields has been retried for this player. The APIUser loads
			// asynchronously, so a retry is right — but it returned "unresolved" forever for anyone
			// whose profile never arrives, re-running the whole badge read every pass for the rest
			// of the session. Capped, so a player who never resolves stops costing anything.
			public int ApiTries;
			// Counts roster passes for this player, so the avatar re-read (the one uncached
			// per-player cost left in the roster) can run on one pass in three instead of every pass.
			public int AvatarPass;
			public string BlockedAvatarLogged;
			// VRChat's "this player is blocked / hidden from me" flag, READ IN THE ROSTER PASS
			// (2026-09-13). BlockedByProbe used to walk all 40 players a second time each second just
			// to read this one boolean, repeating the liveness syscalls the roster already paid; now
			// it is filled here and that module only copies it.
			public bool Blocked;
			public Transform Transform;
			// The position we actually SHOW. Transform.position when we have a live transform, else
			// VRCPlayerApi.GetPosition() \u2014 resolved in RefreshRoster so a player whose Transform never
			// comes back still reports where they are. HasPos is false only when even that failed.
			public Vector3 Pos;
			public bool HasPos;
			public Vector3 Position => HasPos ? Pos : (Transform != null ? Transform.position : Vector3.zero);
		}

		public static readonly List<PlayerEntry> Roster = new List<PlayerEntry>();

		// BUMPED ON EVERY RefreshRoster (2026-09-13). Readers that only FORMAT the roster —
		// InstancePanels rebuilds its rich-text rows every 20 frames — can compare this and skip the
		// rebuild when nothing has changed: the roster refreshes every 60 frames, so two polls in
		// three were re-formatting identical data. Main-thread only, like the roster itself.
		public static int RosterVersion;

		// Edge memory for "tags on AND show plates on", so that switch can tear down / apply on the
		// frame it flips instead of waiting out the 60-frame roster throttle.
		private bool _platesWere;

		// STABLE PER-PLAYER DATA IS COMPUTED ONCE, then reused every refresh.
		//
		// The Munchen lesson: a player's name, user id, trust colour, badges, actor number and
		// master flag do not change while they are in the instance, but RefreshRoster was re-reading
		// all of them — seven il2cpp reflection reads per player, every pass, which is most of the
		// module's 161 ms/s. They are keyed by the player's il2cpp pointer (stable for the object's
		// life) and only the volatile fields (transform, avatar, isLocal, owner) are refreshed.
		private static readonly Dictionary<IntPtr, PlayerEntry> _entryCache = new Dictionary<IntPtr, PlayerEntry>();
		public static string LastStatus = "";

		// --- VRChat Archive membership -------------------------------------------------
		// Every mod user self-tags with this on first sync. It is the badge that marks a
		// member across the whole ecosystem: pink→violet gradient name in the roster, a
		// gradient plate over their head, and an RGB banner when one joins your instance.
		public const string MemberTagText = "VRChat Archive Member";
		private const string MemberPink = "#FF6AD5";
		private const string MemberViolet = "#8143E6";
		private static bool _memberTagChecked;

		public static bool IsMember(string uid)
		{
			foreach (var t in TagsOf(uid))
				if (string.Equals(t.Text, MemberTagText, StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		// Per-character pink→violet gradient as TMP/IMGUI rich text.
		public static string Gradient(string text, string fromHex = MemberPink, string toHex = MemberViolet)
		{
			if (string.IsNullOrEmpty(text)) return "";
			Color a = ParseColor(fromHex, Color.magenta), b = ParseColor(toHex, Color.blue);
			string[] g = Glyphs(text);          // per glyph, so an emoji is not split in half
			var sb = new StringBuilder(g.Length * 20);
			for (int i = 0; i < g.Length; i++)
			{
				float k = g.Length == 1 ? 0f : (float)i / (g.Length - 1);
				sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Color.Lerp(a, b, k)))
				  .Append('>').Append(g[i]).Append("</color>");
			}
			return sb.ToString();
		}
		public static string LastFetchInfo = "not fetched yet";
		public static int RecordsLoaded;

		private static readonly HttpClient Http = CreateClient();
		private static Dictionary<string, VaTag[]> _db = new Dictionary<string, VaTag[]>(StringComparer.OrdinalIgnoreCase);
		private static volatile Dictionary<string, VaTag[]> _pendingDb;
		// Per-user tag lock (owner-only edit). Mirrors _db: a live copy + a pending swap.
		private static Dictionary<string, bool> _locks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		private static volatile Dictionary<string, bool> _pendingLocks;
		private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
		private static int _dbVersion;
		private float _nextFetchAt;
		private static bool _fetching;
		private int _frame;
		private float _nextAnim;

		// one plate per tag, so each can animate independently.
		// Glyphs and Fx are derived from the tag and never change while the plate lives, but the
		// animation pass was recomputing both on EVERY tick for EVERY plate — a string split into a
		// fresh array plus a ToLowerInvariant, fifteen times a second per tag on screen. Resolved
		// once, at build time, instead.
		// internal, not private: FewTagsModule animates ITS plates with this same engine rather
		// than growing a second, divergent copy of fourteen effects (see NewPlate/Animate below).
		internal sealed class Plate
		{
			public GameObject Go; public TextMeshProUGUI Tmp; public VaTag Tag; public bool StaticBuilt;
			public string[] G;      // per-glyph split of Tag.Text
			public string Fx;       // lowercased effect name
		}

		// The effects that actually RE-COMPOSE the label every tick. Everything else (none, grad,
		// and any unrecognised id landing in `default:`) writes its text once and is finished, so
		// the tick must skip it forever after. The old test was `Fx == "none"`, which let "grad" —
		// the member badge, i.e. EVERY mod user — plus every typo'd id back into the full per-glyph
		// pass fifteen times a second for zero visual change.
		private static readonly HashSet<string> AnimatedFx = new HashSet<string>(StringComparer.Ordinal)
		{
			"scroll", "cyln", "lbl", "rain", "rainbow", "sr", "pulse", "wave",
			"jump", "shake", "gt", "blink", "glitch", "glow",
		};

		internal static bool IsAnimated(string fx) => fx != null && AnimatedFx.Contains(fx);

		// One warning per failing effect id, not one per plate per tick.
		private static readonly HashSet<string> _animFailLogged = new HashSet<string>(StringComparer.Ordinal);

		// Build a plate for a label this module did not create (FewTags). Same shape, same engine.
		internal static Plate NewPlate(GameObject go, TextMeshProUGUI tmp, VaTag tag) => new Plate
		{
			Go = go, Tmp = tmp, Tag = tag,
			G = Glyphs(tag.Text ?? ""),
			Fx = (tag.Fx ?? "none").ToLowerInvariant(),
		};

		// One animation step for an externally-owned plate. `force` writes even when the plate is
		// static — used once right after the plate is built so it never shows a frame of raw,
		// unstyled text while waiting for the next tick.
		internal static void Animate(Plate p, float t, bool force = false)
		{
			if (p == null || p.Tmp == null || p.Tag == null) return;
			if (!force && p.StaticBuilt && !IsAnimated(p.Fx)) return;
			AnimatePlate(p, t);
		}
		// FewLines: how many rows FewTags occupied when this set was built. If FewTags later
		// adds or drops a plate, our stack has to be rebuilt or the two overlap again.
		private sealed class PlateSet { public int DbVersion; public int FewLines; public readonly List<Plate> Plates = new List<Plate>(); }
		private readonly Dictionary<string, PlateSet> _plates = new Dictionary<string, PlateSet>(StringComparer.OrdinalIgnoreCase);

		private static VaTagsModule Instance;
		public VaTagsModule() { Instance = this; }

		public override void OnInitialize()
			=> VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] armed — community tags via " + ModConfig.VaTagsApiBase.Value);

		public override void OnUpdate()
		{
			try
			{
				while (MainThreadQueue.TryDequeue(out Action act))
				{
					try { act(); } catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[VaTags] queued action threw: {e.Message}"); }
				}

				// THE ROSTER IS SHARED INFRASTRUCTURE, not a VaTags feature.
				//
				// InstancePanels (the player list + join/leave feed) now reads this roster instead
				// of rebuilding its own. So the roster has to keep refreshing even when VaTags' OWN
				// features (tags, plates) are switched off — otherwise turning tags off would empty
				// the player list, which is a different feature. Only the VaTags-specific work below
				// is gated on VaTagsEnabled; the roster is gated on "does ANYONE need it".
				bool vaOn = ModConfig.VaTagsEnabled.Value;
				bool rosterNeeded = vaOn
					|| ModConfig.InstancePanelsEnabled.Value
					// The wing panels read the same roster, and they are MENU panels: the HUD master
					// switch turns the on-screen ones off and leaves these on, so they would have been
					// left showing a roster nobody refreshed — a frozen player list is worse than none.
					|| ModConfig.WingPlayersEnabled.Value
					|| ModConfig.WingLogEnabled.Value
					|| ModConfig.JoinNotifierEnabled.Value
					// Player Grab finds its targets (and the holder) through this roster: with tags off
					// and no panels it was never refreshed, so there was nobody to grab.
					|| PlayerGrabModule.Active;

				if (!vaOn && _plates.Count > 0) RemoveAllPlates();

				// LOAD LOCAL from the client: outside the tags gate, it is not a tags feature.
				if (_localWant != null) PumpLocalWear();

				if (vaOn)
				{
					// A clone in flight is verified by watching our own avatar id actually change.
					if (_clone != null) PumpCloneAttempt();

					var pending = _pendingDb;
					if (pending != null) { _pendingDb = null; _db = pending; var pl = _pendingLocks; _pendingLocks = null; if (pl != null) _locks = pl; _dbVersion++; RecordsLoaded = _db.Count; }

					// Self-register as a VRChat Archive member once, as soon as the DB is loaded
					// and our own user id is readable.
					if (!_memberTagChecked && RecordsLoaded > 0) EnsureMemberTag();
					// The rank badge, on EVERY pass rather than once: the level can change while the
					// game is running (a subscription starts, a session ends, the client is signed out)
					// and the badge has to follow it both ways. It costs one integer compare unless
					// something actually moved.
					if (RecordsLoaded > 0) EnsureRankTag();

					float now = VaClock.Now;
					// VaAuth.Poll used to run here, which tied the auth probe to VaTags being ON:
					// switch tags off and the client bridge was never probed, so the control channel
					// never connected. ModControlModule owns the probe now, above its own gates.

					if (now >= _nextFetchAt)
					{
						_nextFetchAt = now + Mathf.Max(1, ModConfig.VaTagsUpdateMinutes.Value) * 60f;
						StartFetch();
					}
				}

				// THE PLATES SWITCH ACTS ON ITS OWN EDGE, ABOVE THE 60-FRAME THROTTLE (2026-09-13).
				//
				// Show-plates teardown used to live at the bottom of this method, behind the frame
				// counter, so turning plates off left every plate hanging over every player for up to
				// a second, and turning them on was just as late. Only the parent VaTagsEnabled path
				// was instant. Handling the edge here makes both directions immediate: OFF removes on
				// the very frame it flips, ON primes the counter so the apply pass runs this frame.
				bool platesOn = vaOn;
				try { platesOn = vaOn && ModConfig.VaTagsShowPlates.Value; } catch { }
				if (platesOn != _platesWere)
				{
					_platesWere = platesOn;
					if (!platesOn) { if (_plates.Count > 0) RemoveAllPlates(); }
					else _frame = 60;   // apply on this frame rather than up to 60 frames from now
				}

				if (!rosterNeeded) return;

				if (++_frame < 60) return;
				_frame = 0;
				// The roster pass is the most expensive single thing in the mod and was completely
				// opaque: the profiler said "VaTags = 236 ms/s" and nothing about what that bought.
				// Now a slow pass says how many players it walked and how many it could reuse from
				// cache, which is exactly the ratio that tells cheap from pathological.
				long tRoster = Core.PerfLog.Start();
				RefreshRoster();
				Core.PerfLog.Slow("VaTags/roster", tRoster, 8.0,
					Roster.Count + " player(s), " + _entryCache.Count + " cached, "
					+ _plates.Count + " plate set(s)");
				if (platesOn)
				{
					// MEASURED. The roster pass was timed and this one was not, so when the profiler
					// said "VaTags = 908 ms" the timed half honestly reported 8 ms and the cost had
					// nowhere to show. Anything expensive enough to matter gets its own stopwatch.
					// THE BREAKER GOES ON THE WHOLE PASS, not on a call inside it.
					//
					// Two attempts put a stopwatch around a specific call -- MakePlate, then
					// ResolveNameplate -- and neither fired while the pass still reported 11 100 ms.
					// Guessing which call is slow costs the owner a frozen play session per guess, and
					// it had already cost several. So this measures the thing the log ALREADY prints:
					// if the pass as a whole overruns, plates stop for the session, wherever the time
					// went. It cannot miss, because it is the same number that showed the problem.
					//
					// The first overrun is still felt once; every one after it is prevented. Tags are
					// decoration, and decoration never justifies freezing the game.
					long tPlates = Core.PerfLog.Start();
					var passClock = System.Diagnostics.Stopwatch.StartNew();
					ApplyPlates();
					double passMs = passClock.Elapsed.TotalMilliseconds;
					Core.PerfLog.Slow("VaTags/plates", tPlates, 8.0,
						_wantPlates + " wanted, " + _resolveFail + " unresolved, " + _plates.Count + " live set(s)");

					if (!_platesBroken && passMs > SlowPassMs)
					{
						_platesBroken = true;
						try { RemoveAllPlates(); } catch { }
						VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] COUPE-CIRCUIT : la passe des plaques a pris "
							+ ((int)passMs) + " ms (limite " + SlowPassMs + "). Les plaques de tags sont DESACTIVEES pour "
							+ "cette session — c'est ce qui figeait le jeu. Tout le reste du mod continue normalement.");
					}
				}
				else if (_plates.Count > 0) RemoveAllPlates();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[VaTags] update threw: {e}"); }
		}

		// Animate effect plates (rainbow/blink/wave/glow), throttled to 15 Hz.
		// An animated tag composes a fresh per-character rich-text string and hands TMP a changed
		// string, which forces a full text layout + mesh rebuild + vertex upload — per plate, per
		// frame. At 90 fps with a handful of animated plates that alone was milliseconds a frame.
		// 15 Hz is indistinguishable for these effects and cuts the work ~6x.
		public override void OnLateUpdate()
		{
			if (!ModConfig.VaTagsEnabled.Value || !ModConfig.VaTagsShowPlates.Value) return;
			float t = VaClock.Now;
			if (t < _nextAnim) return;
			_nextAnim = t + 0.0667f;
			foreach (var set in _plates.Values)
				foreach (var p in set.Plates)
				{
					if (p.Tmp == null || p.Tag == null) continue;
					// A tag with no effect has nothing to animate: once its text is written it never
					// changes again. Re-running the whole per-glyph pass on it fifteen times a second
					// was pure waste, and most tags in the database are static. Test the EFFECT, not
					// the literal "none": "grad" (the member badge on every mod user) and any typo'd
					// id are static too and used to be re-composed forever.
					if (p.StaticBuilt && !IsAnimated(p.Fx)) continue;
					// Never silent: a throw in one effect freezes that plate on its last string,
					// which looks exactly like a static tag. Logged ONCE per effect so a broken tag
					// is diagnosable without spamming 15 lines a second.
					try { AnimatePlate(p, t); }
					catch (Exception ex)
					{
						if (_animFailLogged.Add(p.Fx ?? "?"))
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[VaTags] effect '" + (p.Fx ?? "?") + "' threw, plate frozen: " + ex.Message);
					}
				}
		}

		public override void OnShutdown() => RemoveAllPlates();

		// ---------------------------------------------------------------- menu API

		public static VaTag[] TagsOf(string uid)
			=> !string.IsNullOrEmpty(uid) && _db.TryGetValue(uid, out var tags) ? tags : Array.Empty<VaTag>();

		public static List<KeyValuePair<string, int>> TagCounts()
		{
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var tags in _db.Values)
				foreach (var t in tags)
					counts[t.Text] = counts.TryGetValue(t.Text, out int n) ? n + 1 : 1;
			var list = new List<KeyValuePair<string, int>>(counts);
			list.Sort((a, b) => b.Value.CompareTo(a.Value));
			return list;
		}

		public static void RequestRefresh() { LastStatus = "refreshing…"; if (Instance != null) Instance._nextFetchAt = 0f; }

		public static bool LooksLikeUserId(string s)
			=> !string.IsNullOrEmpty(s) && s.StartsWith("usr_", StringComparison.OrdinalIgnoreCase) && s.Length == 40;

		// A TRUE teleport: set the local player's position to the target's, through the SDK.
		//
		// The old version wrote local.transform.position directly. That is a move the active locomotion
		// overwrites the very next frame — which is why it failed while flying / no-clip (the fly loop, or
		// VRChat's own controller, put you straight back). VRCPlayerApi.TeleportTo is an ABSOLUTE position
		// set that goes through VRChat's own teleport path (it resets the controller and cancels
		// momentum), so it LANDS regardless of what locomotion is running. Position1 -> position2, clean.
		public static void TeleportTo(PlayerEntry entry)
		{
			try
			{
				if (entry == null) { LastStatus = "TP failed: no target"; return; }

				// The target's AUTHORITATIVE pose from the SDK, not a transform that may be a frame stale.
				Vector3 pos; Quaternion rot = Quaternion.identity;
				var tapi = entry.ApiObj as VRC.SDKBase.VRCPlayerApi;
				if (tapi != null)
				{
					pos = tapi.GetPosition();
					try { rot = tapi.GetRotation(); } catch { }
				}
				else
				{
					Transform t = entry.Transform;
					if (t == null) { LastStatus = "TP failed: player is no longer in the instance"; return; }
					pos = t.position; rot = t.rotation;
				}

				// Land just beside them, not clipping inside: a step back along their facing, a touch up.
				Vector3 fwd = rot * Vector3.forward;
				Vector3 landing = pos - fwd * 1.0f + Vector3.up * 0.2f;

				var api = PlayerRef.LocalApi();
				if (api != null)
				{
					api.TeleportTo(landing, rot);                 // the real, absolute set
					try { PlayerRef.ZeroVelocity(PlayerRef.LocalPlayer()); } catch { }
					LastStatus = $"teleported to {entry.Name}";
					return;
				}

				// Fallback only if the SDK api is unreachable: the old transform write.
				var local = PlayerRef.LocalPlayer();
				if (local == null) { LastStatus = "TP failed: local player not found"; return; }
				local.transform.position = landing;
				PlayerRef.ZeroVelocity(local);
				LastStatus = $"teleported to {entry.Name} (repli transform)";
			}
			catch (Exception e) { LastStatus = "TP failed: " + Short(e.Message); }
		}

		// Real clone: switch the local player into the target's avatar, through VRChat's own
		// VRCAvatarManager entry point. Every step is tried several ways and logged, because
		// the interop names are obfuscated and rotate between game builds:
		//   local manager : VRCPlayer static self → PlayerRef → scene scan
		//   target avatar : VRC.Player → VRCPlayer → their VRCAvatarManager
		//   switch call   : every public instance method (ApiAvatar[, …]) → UniTask*, in order
		// If one candidate throws we simply try the next, so a single renamed member can't
		// take the feature down. The chosen method is cached once it succeeds.
		// A clone attempt in flight. An invoke that doesn't throw proves nothing — these
		// entry points are fire-and-forget (UniTaskVoid), so the only real proof is the
		// local player's avatar id actually becoming the target's. OnUpdate polls for that
		// and moves to the next candidate if the switch never lands.
		private sealed class CloneAttempt
		{
			public List<(string Label, Action Run)> Strategies;
			public int Index;
			public string TargetId;
			public string TargetName;
			public float DeadlineAt;
		}
		private static CloneAttempt _clone;
		// Generous: a real switch has to download and build the avatar, and cutting it short
		// would fire the next strategy on top of one that was actually working.
		private const float CloneTimeoutSec = 8f;

		public static void CloneAvatar(PlayerEntry entry)
		{
			try
			{
				_clone = null;
				if (entry?.Player == null) { LastStatus = "clone failed: player is no longer in the instance"; return; }
				if (entry.IsLocal) { LastStatus = "that's you — pick another player to clone"; return; }

				object targetApiAvatar = ResolveApiAvatarObject(entry.Player);
				ResolveAvatar(entry.Player, out string avId, out string avName);
				if (!string.IsNullOrEmpty(avId)) GUIUtility.systemCopyBuffer = avId;
				if (targetApiAvatar == null)
				{
					LastStatus = "clone failed: target's ApiAvatar not readable" + (string.IsNullOrEmpty(avId) ? "" : " (id copied)");
					VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: target ApiAvatar null for " + entry.Name);
					return;
				}

				string mine = LocalAvatarId();
				if (!string.IsNullOrEmpty(avId) && avId == mine)
				{ LastStatus = "you're already wearing that avatar"; return; }

				// Without a readable target id there is no way to tell a successful switch
				// from a failed one, and the state machine would fire EVERY strategy at the
				// local player in turn. Refuse instead of thrashing the avatar.
				if (string.IsNullOrEmpty(avId))
				{
					LastStatus = "clone failed: their avatar id isn't readable yet — wait for their avatar to load and retry";
					VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: target avatar id empty, refusing to fire blind.");
					return;
				}

				var strategies = BuildCloneStrategies(targetApiAvatar, avId);
				if (strategies.Count == 0)
				{
					LastStatus = "clone unavailable on this build (id copied)";
					VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: no strategy available.");
					return;
				}

				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[VaTags] clone: target={avId ?? "?"} ({avName}) mine={mine ?? "?"} — {strategies.Count} strategy(ies).");
				_clone = new CloneAttempt { Strategies = strategies, Index = -1, TargetId = avId, TargetName = avName };
				TryNextCloneCandidate();
			}
			catch (Exception e)
			{
				LastStatus = "clone failed: " + Short(e.Message);
				VRChatArchiveModPlugin.Logger.LogError("[VaTags] clone threw: " + e);
			}
		}

		// The ordered ways to change into an avatar, best first:
		//   1. PageAvatar's static "change into avatar" with a FRESH ApiAvatar built from the
		//      id — this is the path VRChat's own UI button takes, and building a new model
		//      instead of reusing the remote player's object is what makes it stick.
		//   2. the same call with the remote player's own ApiAvatar object
		//   3. VRCAvatarManager's switch methods (fire-and-forget UniTask entries)
		// Wear an avatar from its ID alone — no player in the room required.
		//
		// This is what the Archive favourites list needs: the ids come from the server, not from
		// somebody standing next to you. The 'PageAvatar+fresh' strategy already worked this way
		// (it builds a NEW ApiAvatar from the id rather than reusing a remote player's object), so
		// the same machinery, guards and status line are reused rather than duplicated.
		public static void WearById(string avatarId, string avatarName)
		{
			try
			{
				_clone = null;
				if (string.IsNullOrEmpty(avatarId) || !avatarId.StartsWith("avtr_", StringComparison.Ordinal))
				{
					LastStatus = "that does not look like an avatar id";
					return;
				}
				if (string.Equals(avatarId, LocalAvatarId(), StringComparison.OrdinalIgnoreCase))
				{
					LastStatus = "you're already wearing that avatar";
					return;
				}

				// null remote object: only the id-based strategies can build, which is exactly what
				// we want here. The others simply do not get added.
				var strategies = BuildCloneStrategies(null, avatarId);
				if (strategies.Count == 0)
				{
					GUIUtility.systemCopyBuffer = avatarId;
					LastStatus = "switching avatars is unavailable on this build (id copied)";
					return;
				}

				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[VaTags] wear: {avatarId} ({avatarName}) — {strategies.Count} strategy(ies).");
				_clone = new CloneAttempt { Strategies = strategies, Index = -1, TargetId = avatarId, TargetName = avatarName };
				TryNextCloneCandidate();
			}
			catch (Exception e)
			{
				LastStatus = "wear failed: " + Short(e.Message);
				VRChatArchiveModPlugin.Logger.LogError("[VaTags] wear threw: " + e);
			}
		}

		// ---------------------------------------------------------------- LOAD LOCAL (client v367)
		//
		// WEAR A LOCAL TEST AVATAR BY ITS FILE NAME, WITHOUT THE AVATAR MENU.
		//
		// The client's LOAD LOCAL writes a bundle into VRChat's test-avatar folder, and the game (started
		// with --watch-avatars) registers it as "local:sdk_<file name>" in the SDK's own store,
		// ApiContentModel<ApiAvatar>.localContent — in VRCCore, IsLocal is literally
		// localContent.ContainsKey(id). The menu row that lists those, SDK Test Avatars, is the row
		// ARCHIVE FAVORITES takes over, so the owner had no way to pick one. This wears it straight from
		// that store, with the same PageAvatar call as WEAR: the game's own record, nothing built by hand.
		// Registration follows the file write by a moment, so the lookup is retried for LocalWaitSec.
		private static string _localWant;
		private static string _localName;
		private static float _localUntil;
		private static float _localNextTry;
		private static bool _localStoreWarned;
		private const float LocalWaitSec = 30f;

		public static void WearLocal(string name)
		{
			name = (name ?? "").Trim();
			if (name.EndsWith(".vrca", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5).TrimEnd();
			if (name.Length == 0) { LastStatus = "no local avatar name given"; return; }
			_clone = null;
			_localName = name.StartsWith("local:", StringComparison.Ordinal) ? name.Substring(name.IndexOf('_') + 1) : name;
			_localWant = name.StartsWith("local:", StringComparison.Ordinal) ? name : "local:sdk_" + name;
			_localUntil = VaClock.Now + LocalWaitSec;
			_localNextTry = 0f;
			LastStatus = "loading local avatar " + _localName + "…";
			VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] wearLocal: " + _localWant);
		}

		// Polled from OnUpdate, whatever the tags switch says: this is not a tags feature.
		private static void PumpLocalWear()
		{
			if (_localWant == null) return;
			float now = VaClock.Now;
			if (now < _localNextTry) return;
			_localNextTry = now + 0.5f;

			VRC.Core.ApiAvatar av = FindLocalAvatar(_localWant, _localName);
			if (av == null)
			{
				if (now < _localUntil)
				{
					LastStatus = $"waiting for VRChat to list {_localName}… ({Mathf.CeilToInt(_localUntil - now)}s)";
					return;
				}
				LastStatus = "VRChat has not listed " + _localName + " — start VRChat from the Mods Loader (it watches the test-avatar folder) or restart the game once";
				VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] wearLocal: " + _localWant + " never appeared in localContent.");
				_localWant = null;
				return;
			}

			string id = _localWant, shown = _localName;
			_localWant = null;
			MethodInfo change = PageAvatarChange();
			if (change == null)
			{
				LastStatus = "switching avatars is unavailable on this build";
				return;
			}
			try
			{
				change.Invoke(null, new object[] { av, "" });
				LastStatus = "✓ wearing " + shown + " (local — only you see it)";
				VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] wearLocal: change requested into " + id);
			}
			catch (Exception e)
			{
				LastStatus = "local wear failed: " + Short(e.InnerException?.Message ?? e.Message);
				VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] wearLocal threw: " + e);
			}
		}

		private static VRC.Core.ApiAvatar FindLocalAvatar(string id, string name)
		{
			try
			{
				var store = VRC.Core.ApiContentModel<VRC.Core.ApiAvatar>.localContent;
				if (store == null) return null;
				if (store.ContainsKey(id)) return store[id];
				// The same file under another prefix, should a build ever change "sdk_".
				foreach (var kv in store)
				{
					string k = kv.Key ?? "";
					if (k.StartsWith("local:", StringComparison.Ordinal)
						&& k.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
				}
			}
			catch (Exception e)
			{
				if (!_localStoreWarned)
				{
					_localStoreWarned = true;
					VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] wearLocal: localContent unreadable: " + e.Message);
				}
			}
			return null;
		}

		// PageAvatar's static "change into avatar" (ApiAvatar, string) — the call VRChat's own button
		// makes. Resolved by shape, not by name: the method name is obfuscated and rotates per build.
		private static MethodInfo _pageAvatarChange;
		private static MethodInfo PageAvatarChange()
		{
			if (_pageAvatarChange != null) return _pageAvatarChange;
			try
			{
				Type pageAvatar = Assembly.Load("Assembly-CSharp").GetType("PageAvatar");
				if (pageAvatar == null) return null;
				foreach (var m in pageAvatar.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					var ps = m.GetParameters();
					if (m.ReturnType == typeof(void) && ps.Length == 2
						&& ps[0].ParameterType.Name == "ApiAvatar" && ps[1].ParameterType == typeof(string))
					{ _pageAvatarChange = m; break; }
				}
			}
			catch { }
			return _pageAvatarChange;
		}

		private static List<(string Label, Action Run)> BuildCloneStrategies(object remoteApiAvatar, string avatarId)
		{
			var list = new List<(string, Action)>();
			try
			{
				Type pageAvatar = Assembly.Load("Assembly-CSharp").GetType("PageAvatar");
				MethodInfo change = null;
				if (pageAvatar != null)
				{
					foreach (var m in pageAvatar.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
					{
						var ps = m.GetParameters();
						if (m.ReturnType == typeof(void) && ps.Length == 2
							&& ps[0].ParameterType.Name == "ApiAvatar" && ps[1].ParameterType == typeof(string))
						{ change = m; break; }
					}
				}

				if (change != null && !string.IsNullOrEmpty(avatarId))
					list.Add(("PageAvatar+fresh", () =>
					{
						object fresh = NewApiAvatar(avatarId);
						if (fresh == null) throw new Exception("could not build ApiAvatar");
						change.Invoke(null, new object[] { fresh, "" });
					}
					));

				// Fetch the full model from the API, then change into THAT. Needed when a
				// bare id isn't enough for the UI path (the object has no asset url yet).
				// ApiAvatar.Get is async; its success callback runs on the game's own
				// dispatcher, so calling PageAvatar from inside it is safe.
				if (change != null && !string.IsNullOrEmpty(avatarId))
					list.Add(("PageAvatar+fetched", () =>
					{
						object fresh = NewApiAvatar(avatarId);
						if (fresh == null) throw new Exception("could not build ApiAvatar");

						MethodInfo get = fresh.GetType().GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
						if (get == null) throw new Exception("ApiAvatar.Get not found");
						var gps = get.GetParameters();
						if (gps.Length != 5) throw new Exception("unexpected ApiAvatar.Get signature");

						Action<VRC.Core.ApiContainer> onOk = c =>
						{
							try
							{
								var fetched = c?.Model?.TryCast<VRC.Core.ApiAvatar>();
								if (fetched == null) { VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: fetch returned no avatar model."); return; }
								change.Invoke(null, new object[] { fetched, "" });
								VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] clone: fetched model, requested change.");
							}
							catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning($"[VaTags] clone: fetched-change threw: {Short(ex.Message)}"); }
						};
						Action<VRC.Core.ApiContainer> onErr = c =>
							VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: avatar fetch failed — it is probably private.");

						object priority = Activator.CreateInstance(gps[4].ParameterType);   // enum default
						get.Invoke(fresh, new object[]
						{
							ToIl2CppDelegate(gps[0].ParameterType, onOk),
							ToIl2CppDelegate(gps[1].ParameterType, onErr),
							null, false, priority,
						});
					}
					));

				if (change != null && remoteApiAvatar != null)
					list.Add(("PageAvatar+remote", () => change.Invoke(null, new object[] { remoteApiAvatar, "" })));

				if (remoteApiAvatar != null)
				{
					object mgr = ResolveLocalAvatarManager();
					if (mgr != null)
						foreach (MethodInfo m in SwitchCandidates(mgr.GetType()))
						{
							MethodInfo mm = m;   // capture
							list.Add(("AvatarManager." + mm.Name, () =>
							{
								var ps = mm.GetParameters();
								var args = new object[ps.Length];
								args[0] = remoteApiAvatar;
								for (int i = 1; i < ps.Length; i++) args[i] = DefaultArg(ps[i].ParameterType);
								mm.Invoke(mgr, args);
							}
							));
						}
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[VaTags] clone strategy build failed: {e.Message}"); }

			// FETCH BEFORE BLIND. 'PageAvatar+fresh' hands the game an ApiAvatar carrying nothing
			// but an id and asks it to wear it. That is fine for a public avatar, which the game
			// then loads itself — but for a PRIVATE one the record never becomes valid and the game
			// walks fields that were never filled, which takes the process down.
			// 'PageAvatar+fetched' asks the API first and has an error callback, so a private avatar
			// comes back as "probably private" instead of a crash. The blind path stays as the next
			// candidate, so nothing that used to work stops working.
			try
			{
				int fetched = list.FindIndex(x => x.Item1 == "PageAvatar+fetched");
				if (fetched > 0)
				{
					var item = list[fetched];
					list.RemoveAt(fetched);
					list.Insert(0, item);
				}
			}
			catch { }
			return list;
		}

		// Wraps a managed delegate as the Il2Cpp delegate type a game method expects
		// (Il2CppSystem.Action&lt;T&gt;), which is what makes API callbacks possible at all.
		private static object ToIl2CppDelegate(Type il2cppDelegateType, Delegate managed)
		{
			// Same fatal call, reached generically. The gate has to be checked here too or the crash
			// simply moves to whichever feature converts a delegate first.
			if (!Core.Il2CppDelegates.Available)
				throw new Exception("il2cpp delegates are disabled on this build ([Compatibility] AllowIl2CppDelegates)");

			foreach (var m in typeof(Il2CppInterop.Runtime.DelegateSupport).GetMethods(BindingFlags.Static | BindingFlags.Public))
			{
				if (m.Name != "ConvertDelegate" || !m.IsGenericMethodDefinition) continue;
				try { return m.MakeGenericMethod(il2cppDelegateType).Invoke(null, new object[] { managed }); }
				catch { }
			}
			throw new Exception("could not convert callback to an Il2Cpp delegate");
		}

		// A minimal ApiAvatar carrying just the id — VRChat fills the rest when it loads.
		private static object NewApiAvatar(string avatarId)
		{
			try
			{
				Type t = typeof(VRC.Core.ApiAvatar);
				object av = Activator.CreateInstance(t);
				if (av == null) return null;
				// 'id' is declared on the ApiModel base, so walk up until we find its setter.
				for (Type cur = t; cur != null; cur = cur.BaseType)
				{
					var p = cur.GetProperty("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					if (p?.SetMethod != null) { p.SetValue(av, avatarId); return av; }
				}
				VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: ApiAvatar has no writable 'id'.");
				return null;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[VaTags] clone: ApiAvatar build failed: {Short(e.Message)}");
				return null;
			}
		}

		// Fires the next strategy; called on start and whenever the previous one timed out.
		private static void TryNextCloneCandidate()
		{
			var c = _clone;
			if (c == null) return;
			while (true)
			{
				c.Index++;
				if (c.Index >= c.Strategies.Count)
				{
					LastStatus = "clone failed: nothing switched the avatar — it is probably private "
						+ "(only public/cloneable avatars can be cloned). Id copied.";
					VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] clone: all strategies exhausted without a swap.");
					_clone = null;
					return;
				}
				var (label, run) = c.Strategies[c.Index];
				try
				{
					run();
					c.DeadlineAt = VaClock.Now + CloneTimeoutSec;
					LastStatus = $"cloning{(string.IsNullOrEmpty(c.TargetName) ? "" : " " + c.TargetName)}… "
						+ $"(try {c.Index + 1}/{c.Strategies.Count})";
					VRChatArchiveModPlugin.Logger.LogInfo($"[VaTags] clone: ran '{label}', waiting for the swap…");
					return;
				}
				catch (Exception ie)
				{
					VRChatArchiveModPlugin.Logger.LogWarning(
						$"[VaTags] clone: '{label}' threw ({Short(ie.InnerException?.Message ?? ie.Message)}), next…");
				}
			}
		}

		// Polled from OnUpdate: did our avatar actually become the target's?
		private static void PumpCloneAttempt()
		{
			var c = _clone;
			if (c == null) return;
			string mine = LocalAvatarId();
			if (!string.IsNullOrEmpty(c.TargetId) && mine == c.TargetId)
			{
				LastStatus = $"✓ cloned{(string.IsNullOrEmpty(c.TargetName) ? "" : " " + c.TargetName)}";
				VRChatArchiveModPlugin.Logger.LogInfo($"[VaTags] clone: SUCCESS via '{c.Strategies[c.Index].Label}'.");
				_clone = null;
				return;
			}
			// COUNT DOWN OUT LOUD. Each attempt waits {CloneTimeoutSec}s for the avatar to change,
			// and with several strategies that is a long silence on a line that says "cloning…" —
			// indistinguishable from the mod having hung. Showing the seconds left says "still
			// trying" without adding a single second to the attempt.
			float left = c.DeadlineAt - VaClock.Now;
			if (left > 0f)
			{
				LastStatus = $"cloning{(string.IsNullOrEmpty(c.TargetName) ? "" : " " + c.TargetName)}… "
					+ $"(try {c.Index + 1}/{c.Strategies.Count}, {Mathf.CeilToInt(left)}s)";
				return;
			}

			VRChatArchiveModPlugin.Logger.LogWarning(
				$"[VaTags] clone: '{c.Strategies[c.Index].Label}' did not change the avatar in {CloneTimeoutSec}s, next…");
			TryNextCloneCandidate();
		}

		// The local player's currently-worn avatar id.
		// Adds the member badge to ourselves if it isn't there yet. Runs once per session.
		private static void EnsureMemberTag()
		{
			string me = LocalUserId();
			if (string.IsNullOrEmpty(me)) return;      // not spawned yet — try again next sync
			_memberTagChecked = true;
			if (IsMember(me)) return;
			VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] registering this account as a VRChat Archive member.");
			AddTag(me, new VaTag { Text = MemberTagText, Color = MemberPink, B = true, Fx = "grad" });
		}

		// ---------------------------------------------------------------- rank badge
		//
		// THE ARCHIVE TIER, WORN IN GAME, AND KEPT HONEST BY ITSELF.
		//
		// The member badge says "this person runs the mod". This one says WHICH TIER their archive
		// account holds — and it is not something anyone grants or takes away by hand: the desktop
		// client publishes the level over the local bridge (VaAuth.AccountLevel) and this compares it
		// to what is currently worn. Subscribe and the badge appears; let it lapse and it comes off on
		// its own, because a rank nobody can revoke is not a rank.
		//
		// The colours are the SITE's, value for value out of the tier classes in html/admin.html
		// (.access-tier-*), so a Premium is the same purple in game as on the website.
		//
		// EVERY LEVEL GETS ONE, including level 1: the ladder reads as a ladder only if its bottom
		// rung exists, and "Archive User" is what somebody with an account and no subscription is.
		public sealed class Rank
		{
			public readonly int Level; public readonly string Text, Color, Fx;
			public Rank(int level, string text, string color, string fx) { Level = level; Text = text; Color = color; Fx = fx; }
		}

		// Highest first: RankFor takes the first one this level reaches.
		private static readonly Rank[] Ranks =
		{
			// "sr", not "rain"/"rainbow": the site's .access-tier-legendary is a linear-gradient swept
			// by an animation, i.e. a SMOOTH flow through the spectrum — which is what sr draws. The
			// "rainbow" code is the chunky stepped CoD-clan-tag look and would not match it. The colour
			// is ignored by both (they write their own per-glyph spans); it is kept as the fallback for
			// anywhere the effect is stripped.
			new Rank(5, LegendaryRankText, "#FF5FCF", "sr"),
			new Rank(4, "Archive VIP",       "#FFD166", "glow"),     // --yellow
			new Rank(3, "Archive Premium",   "#A855F7", "glow"),     // --purple
			new Rank(2, "Archive Supporter", "#0066FF", "none"),     // --blue
			new Rank(1, "Archive User",      "#00FF41", "none"),     // --green (.access-tier-standard)
		};
		// Named once, used by the table above and by IsLegendary below, so the two can never
		// drift apart — a renamed badge that stops turning people rainbow would be a silent
		// failure, not a compile error.
		public const string LegendaryRankText = "Archive Legendary";
		private const string AdminRankText = "Archive Admin";
		private const string AdminRankColor = "#00E5FF";                // --cyan

		// THE NAMES THIS SYSTEM USED TO WRITE, cleaned off an account when the current name goes on —
		// otherwise the first build's badges stay stranded on everyone who already earned one.
		//
		// SPLIT IN TWO ON PURPOSE. "Legendary Subscriber" and "Premium Supporter" are ours beyond
		// doubt, so they count as rank badges everywhere, including the guard that refuses a manual
		// delete. Bare "VIP" and "Supporter" are words anybody might legitimately want as their own
		// tag: they are swept off YOUR OWN account during the rename and nowhere else, so nobody is
		// left unable to write — or delete — a tag that simply says VIP.
		private static readonly string[] LegacyRankTexts =
		{
			"Legendary Subscriber", "Premium Supporter",
		};
		private static readonly string[] LegacyLooseRankTexts =
		{
			"VIP", "Supporter",
		};

		/// <summary>True while this account carries the Archive Legendary rank badge. Read off the
		/// tag database, which is the only thing the mod knows about ANOTHER person's level — and it
		/// is the right source anyway: the badge is rewritten on every tag pass, so it goes away by
		/// itself when somebody stops being Legendary.</summary>
		public static bool IsLegendary(string uid)
		{
			if (string.IsNullOrEmpty(uid)) return false;
			foreach (var t in TagsOf(uid))
			{
				if (t == null || string.IsNullOrEmpty(t.Text)) continue;
				if (string.Equals(t.Text, LegendaryRankText, StringComparison.OrdinalIgnoreCase)) return true;
				// The name the first build wrote. Somebody who earned it then and has not been
				// through a tag pass since is still a Legendary.
				if (string.Equals(t.Text, "Legendary Subscriber", StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		public static Rank RankFor(int level)
		{
			for (int i = 0; i < Ranks.Length; i++) if (level >= Ranks[i].Level) return Ranks[i];
			return null;
		}

		/// <summary>Every text this system owns — used to recognise a rank badge on somebody, and to
		/// refuse to let one be taken off by hand.</summary>
		public static bool IsRankTag(string text)
		{
			if (string.IsNullOrEmpty(text)) return false;
			if (string.Equals(text, AdminRankText, StringComparison.OrdinalIgnoreCase)) return true;
			for (int i = 0; i < Ranks.Length; i++)
				if (string.Equals(text, Ranks[i].Text, StringComparison.OrdinalIgnoreCase)) return true;
			for (int i = 0; i < LegacyRankTexts.Length; i++)
				if (string.Equals(text, LegacyRankTexts[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>A word the first build used as a rank and that anyone else may legitimately want.
		/// Swept off your own account by the rename, and treated as an ordinary tag everywhere else.</summary>
		private static bool IsLooseLegacyRank(string text)
		{
			if (string.IsNullOrEmpty(text)) return false;
			for (int i = 0; i < LegacyLooseRankTexts.Length; i++)
				if (string.Equals(text, LegacyLooseRankTexts[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		// What was last put on, so the steady state costs one compare per sync instead of a round trip.
		private static string _rankApplied;
		private static int _rankLevelSeen = int.MinValue;

		private static void EnsureRankTag()
		{
			string me = LocalUserId();
			if (string.IsNullOrEmpty(me)) return;

			int level = VaAuth.AccountLevel;
			// -1 = the level is not knowable right now (no bridge, or a client too old to send it).
			// Leave everything exactly as it is: a badge must never vanish because the desktop client
			// happened to be closed — only because the account really lost the tier.
			if (level < 0) return;

			bool admin = VaAuth.AccountAdmin;
			Rank want = RankFor(level);
			string wantText = admin ? AdminRankText : want?.Text;
			if (level == _rankLevelSeen && string.Equals(wantText ?? "", _rankApplied ?? "", StringComparison.Ordinal)) return;
			_rankLevelSeen = level;

			// Take off every rank badge that is not the one owed. This is the "auto-removed" half, and
			// it is what makes a demotion, a lapsed subscription or a sign-out clean up after itself.
			// WriteTag directly, because RemoveTag refuses rank badges by design.
			foreach (var t in TagsOf(me))
			{
				if (!IsRankTag(t.Text) && !IsLooseLegacyRank(t.Text)) continue;
				if (wantText != null && string.Equals(t.Text, wantText, StringComparison.OrdinalIgnoreCase)) continue;
				VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] rank badge '" + t.Text + "' no longer earned (level " + level + ") — removing.");
				WriteTag("/api/va-tags/remove", me, new VaTag { Text = t.Text }, "rank badge removed");
			}

			if (wantText == null) { _rankApplied = null; return; }
			foreach (var t in TagsOf(me))
				if (string.Equals(t.Text, wantText, StringComparison.OrdinalIgnoreCase)) { _rankApplied = wantText; return; }

			VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] rank badge '" + wantText + "' earned (level " + level + (admin ? ", admin" : "") + ") — adding.");
			AddTag(me, new VaTag
			{
				Text = wantText,
				Color = admin ? AdminRankColor : want.Color,
				B = true,
				Fx = admin ? "glow" : want.Fx,
			});
			_rankApplied = wantText;
		}

		public static string LocalAvatarId()
		{
			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local != null)
				{
					ResolveAvatar(local, out string id, out _);
					if (!string.IsNullOrEmpty(id)) return id;
				}
				// FALLBACK: THE ACCOUNT RECORD, because the player object cannot be read on this build.
				//
				// ResolveAvatar goes VRC.Player -> ApiAvatar, and on VRChat 1903 VRC.Player's members
				// cannot be placed at all -- the startup log says so outright ("ses membres ne peuvent
				// pas etre places"). So this returned empty, and with it every button that needs to know
				// which avatar is on screen. The diagnostic caught it exactly:
				//
				//     id introuvable — CTA='Applied' avatar porte='' id porte=''
				//
				// The pane WAS correctly recognised as showing the worn avatar, and then the worn avatar
				// had no id. APIUser is readable here -- it already supplies displayName and the
				// VRC+/18+/platform badges -- and it carries currentAvatar, the same id VRChat prints at
				// login.
				return CurrentAvatarFromAccount();
			}
			catch { return null; }
		}

		// The worn avatar id straight off the local APIUser.
		private static string CurrentAvatarFromAccount()
		{
			try
			{
				object apiUser = LocalApiUser();
				if (apiUser == null) return null;
				object cur = FewTagsModule.GetMember(apiUser, "currentAvatar");
				// VRChat spells it either as the id itself or as the ApiAvatar behind it.
				string id = cur as string;
				if (string.IsNullOrEmpty(id) && cur != null) id = FewTagsModule.GetMember(cur, "id") as string;
				if (string.IsNullOrEmpty(id)) return null;
				return id.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase) ? id : null;
			}
			catch { return null; }
		}

		// The local player's APIUser, taken from the roster this module already maintains rather than
		// walking the players again.
		private static object LocalApiUser()
		{
			try
			{
				var roster = Roster;
				for (int i = 0; roster != null && i < roster.Count; i++)
				{
					var e = roster[i];
					if (e == null || !e.IsLocal || e.Player == null) continue;
					return FewTagsModule.GetMemberByTypeName(e.Player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				}
			}
			catch { }
			return null;
		}

		// The worn avatar's NAME, by the same route. The Archive fav button uses it to recognise "the
		// pane is showing the avatar I'm wearing" in any UI language (the "Applied" label is English).
		public static string LocalAvatarName()
		{
			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local == null) return null;
				ResolveAvatar(local, out _, out string name);
				return name;
			}
			catch { return null; }
		}

		private static object DefaultArg(Type t)
		{
			try
			{
				if (t == typeof(float)) return 0f;
				if (t == typeof(bool)) return false;
				if (t == typeof(int)) return 0;
				return t.IsValueType ? Activator.CreateInstance(t) : null;
			}
			catch { return null; }
		}

		public static void AddTag(string uid, VaTag tag) => WriteTag("/api/va-tags/add", uid, tag, "tag added");

		// The member badge is mandatory for everyone who runs the mod — it can never be removed.
		// Neither can a rank badge: it is not a decoration somebody chose, it is what the archive
		// account currently is, and the only thing allowed to take it off is EnsureRankTag noticing
		// that the level no longer earns it.
		public static void RemoveTag(string uid, string text)
		{
			if (string.Equals(text, MemberTagText, StringComparison.OrdinalIgnoreCase))
			{ LastStatus = "the VRChat Archive Member badge is mandatory — it can't be removed"; return; }
			if (IsRankTag(text))
			{ LastStatus = "'" + text + "' is your archive rank — it follows your account, it isn't removable by hand"; return; }
			WriteTag("/api/va-tags/remove", uid, new VaTag { Text = text }, "tag removed");
		}

		// Is this user's tag set locked (owner-only edits)?
		public static bool IsLocked(string uid)
			=> !string.IsNullOrEmpty(uid) && _locks.TryGetValue(uid, out bool v) && v;

		// Toggle the lock on YOUR OWN tags. The server only accepts actor == user_id.
		public static void SetLock(bool locked)
		{
			string me = LocalUserId();
			if (!LooksLikeUserId(me)) { LastStatus = "your user id isn't readable yet (join a world)"; return; }
			var payload = new Dictionary<string, object> { ["user_id"] = me, ["actor"] = me, ["locked"] = locked };
			string body = JsonSerializer.Serialize(payload);
			LastStatus = locked ? "locking your tags…" : "unlocking your tags…";
			Task.Run(async () =>
			{
				string status; bool? applied = null;
				var (ok, raw, code) = await VaAuth.SendWriteAsync("lock", body);
				try
				{
					using JsonDocument doc = JsonDocument.Parse(raw);
					if (ok)
					{
						applied = doc.RootElement.TryGetProperty("locked", out var lk) && lk.ValueKind == JsonValueKind.True;
						status = applied.Value ? "✓ tag lock ON — only you can edit your tags" : "✓ tag lock OFF";
					}
					else if (code == 401) status = "login required — connect your VRChat Archive account (your tag ▸ Connect)";
					else status = doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : $"server error {code}";
				}
				catch
				{
					// A successful write with an empty / non-JSON body must not read as a failure.
					if (ok) { applied = locked; status = locked ? "✓ tag lock ON — only you can edit your tags" : "✓ tag lock OFF"; }
					else status = "unable to contact the VRChatArchive tag server";
				}
				MainThreadQueue.Enqueue(() => { LastStatus = status; if (applied.HasValue) { _locks[me] = applied.Value; _dbVersion++; } });
			});
		}

		// ---------------------------------------------------------------- clone reflection

		// The local player's VRCAvatarManager. VRCPlayer exposes a STATIC self pointer
		// (field_Internal_Static_VRCPlayer_0), the most reliable route on this build;
		// PlayerRef and a component scan are the fallbacks.
		private static object ResolveLocalAvatarManager()
		{
			try
			{
				Type vrcPlayerType = Assembly.Load("Assembly-CSharp").GetType("VRCPlayer");
				if (vrcPlayerType != null)
				{
					object self = null;
					foreach (var p in vrcPlayerType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
					{
						if (p.PropertyType.Name != "VRCPlayer") continue;
						try { self = Core.ProxyGuard.GetValue(p, null); } catch { }
						if (self != null) break;
					}
					object mgr = FewTagsModule.GetMemberByTypeName(self, "VRCAvatarManager", "prop_VRCAvatarManager_0", "field_Private_VRCAvatarManager_0");
					if (mgr != null) return mgr;
				}
			}
			catch { }

			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local != null)
				{
					object vp = FewTagsModule.GetMemberByTypeName(local, "VRCPlayer", "_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
					object mgr = FewTagsModule.GetMemberByTypeName(vp, "VRCAvatarManager", "prop_VRCAvatarManager_0", "field_Private_VRCAvatarManager_0");
					if (mgr != null) return mgr;

					// the manager also lives as a component somewhere on the player rig
					Type mgrType = Assembly.Load("Assembly-CSharp").GetType("VRCAvatarManager");
					if (mgrType != null)
					{
						var il2 = Il2CppInterop.Runtime.Il2CppType.From(mgrType);
						var comp = local as Component;
						if (comp != null)
						{
							var found = comp.GetComponentInChildrenSafe(il2, true) ?? comp.GetComponentInParentSafe(il2);
							// GetComponent* hands back a plain Component wrapper: re-wrap it as
							// the concrete type, or reflection would scan Component and find
							// none of the switch methods.
							if (found != null) return AsConcrete(found, mgrType) ?? found;
						}
					}
				}
			}
			catch { }

			return null;
		}

		// Il2CppObjectBase.TryCast<T> — turns a generic Component wrapper into the concrete
		// interop type so its members are visible to reflection.
		private static object AsConcrete(object obj, Type concrete)
		{
			try
			{
				var tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
					.GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)
					?.MakeGenericMethod(concrete);
				return tryCast?.Invoke(obj, null);
			}
			catch { return null; }
		}

		// Every plausible "switch into this avatar" entry on VRCAvatarManager, best first:
		// public 1-param ApiAvatar → UniTask*, then more params, then private ones. These
		// are fire-and-forget, so the caller verifies the swap actually happened.
		private static List<MethodInfo> SwitchCandidates(Type mgrType)
		{
			var list = new List<MethodInfo>();
			try
			{
				var all = new List<MethodInfo>();
				foreach (var m in mgrType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					var ps = m.GetParameters();
					if (ps.Length == 0 || ps[0].ParameterType.Name != "ApiAvatar") continue;
					if (!m.ReturnType.Name.StartsWith("UniTask", StringComparison.Ordinal)) continue;
					if (m.Name.StartsWith("set_", StringComparison.Ordinal)) continue;
					bool fillable = true;
					for (int i = 1; i < ps.Length; i++)
						if (!ps[i].ParameterType.IsValueType) { fillable = false; break; }
					if (fillable && !list.Contains(m)) all.Add(m);
				}
				all.Sort((x, y) =>
				{
					int c = y.IsPublic.CompareTo(x.IsPublic);                 // public first
					if (c != 0) return c;
					c = x.GetParameters().Length.CompareTo(y.GetParameters().Length);  // fewest params
					if (c != 0) return c;
					return (y.ReturnType.Name == "UniTaskVoid").CompareTo(x.ReturnType.Name == "UniTaskVoid");
				});
				foreach (var m in all) if (!list.Contains(m)) list.Add(m);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[VaTags] clone candidate scan failed: {e.Message}"); }
			return list;
		}

		// The local player's own user id — the menu's YOUR TAGS panel needs it because you
		// never see your own nameplate, so your tags are invisible in-world.
		//
		// ASKED EVERY FRAME, BY MORE THAN ONE MODULE. Player grab calls this on every Update just to
		// decide whether to start listening, and the roster pass calls it three more times. Each miss
		// costs a local-player lookup plus three or four reflected reads across the il2cpp boundary --
		// and before you are logged in EVERY call is a miss, so the answer was recomputed from scratch
		// sixty times a second for nothing. The profiler put that one helper at 140 ms per second of
		// wall clock inside PlayerGrab alone, with no players in the room.
		//
		// The id does not change during a session, so it is remembered. A miss is remembered too, but
		// only briefly, so that logging in is picked up within half a second.
		private static string _uidCache = "";
		private static float _uidAt = -999f;

		public static string LocalUserId()
		{
			float now;
			try { now = VaClock.Now; } catch { now = 0f; }
			if (_uidCache.Length > 0 && now - _uidAt < 5f) return _uidCache;
			if (_uidCache.Length == 0 && now - _uidAt < 0.5f) return "";

			_uidAt = now;
			foreach (var e in Roster)
				if (e.IsLocal && !string.IsNullOrEmpty(e.UserId)) { _uidCache = e.UserId; return _uidCache; }
			try
			{
				var local = PlayerRef.LocalPlayer();
				object apiUser = FewTagsModule.GetMemberByTypeName(local, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				_uidCache = FewTagsModule.GetMember(apiUser, "id") as string ?? "";
			}
			catch { _uidCache = ""; }
			return _uidCache;
		}

		// A world change can hand you a different local player object; drop the memo rather than trust
		// an id that was read against the previous scene.
		internal static void ForgetLocalUserId() { _uidCache = ""; _uidAt = -999f; _ownerId = ""; _ownerAt = -999f; _plateMissAt.Clear(); }

		// ---------------------------------------------------------------- API sync

		private static HttpClient CreateClient()
		{
			var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
			// Browser-like UA: Cloudflare in front of vrchatarchive.org 403s non-browser
			// user agents, which would kill every tag fetch/write in-game.
			c.DefaultRequestHeaders.Add("User-Agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 VRChatArchiveMod/3.3");
			return c;
		}

		private void StartFetch()
		{
			if (_fetching) return;
			_fetching = true;
			string url = ModConfig.VaTagsApiBase.Value.TrimEnd('/') + "/api/va-tags";
			Task.Run(async () =>
			{
				try
				{
					string raw = await Http.GetStringAsync(url);
					var db = new Dictionary<string, VaTag[]>(StringComparer.OrdinalIgnoreCase);
					var locks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
					using (JsonDocument doc = JsonDocument.Parse(raw))
					{
						if (doc.RootElement.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
							foreach (var rec in recs.EnumerateArray())
							{
								string uid = rec.TryGetProperty("user_id", out var u) ? u.GetString() : null;
								if (string.IsNullOrEmpty(uid)) continue;
								if (rec.TryGetProperty("locked", out var lk) && lk.ValueKind == JsonValueKind.True) locks[uid] = true;
								if (!rec.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
								var list = new List<VaTag>();
								foreach (var t in tags.EnumerateArray()) { var vt = ParseTag(t); if (vt != null) list.Add(vt); }
								if (list.Count > 0) db[uid] = list.ToArray();
							}
					}
					_pendingDb = db; _pendingLocks = locks;
					LastFetchInfo = $"{db.Count} tagged user(s) @ {DateTime.Now:HH:mm:ss}";
					int tagTotal = 0; foreach (var v in db.Values) tagTotal += v.Length;
					MainThreadQueue.Enqueue(() => VRChatArchiveModPlugin.Logger.LogInfo(
						$"[VaTags] database loaded: {db.Count} tagged user(s), {tagTotal} tag(s) from the server."));
				}
				catch (Exception e)
				{
					// SAY IT OUT LOUD. This used to fail in complete silence: LastFetchInfo and the
					// status line are only visible to someone already looking for them, so a player
					// whose download was refused saw no tags, no error, and nothing in the log to
					// explain it — the failure was indistinguishable from "nobody nearby is tagged".
					// A refused download is the one case worth a warning, because it is never the
					// player's doing and it disables the whole feature.
					string why = Short(e.Message);
					bool refused = why.IndexOf("403", StringComparison.Ordinal) >= 0
						|| why.IndexOf("Forbidden", StringComparison.OrdinalIgnoreCase) >= 0;
					LastFetchInfo = "fetch failed: " + why;
					MainThreadQueue.Enqueue(() =>
					{
						LastStatus = refused
							? "the tag server refused the request — tags are unavailable this session"
							: "unable to contact the VRChatArchive tag server";
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[VaTags] tag database NOT loaded — " + why
							+ (refused
								? ". The server refused an anonymous read, so no tags can be shown."
								: ". Tags stay empty until the next refresh succeeds."));
					});
				}
				finally { _fetching = false; }
			});
		}

		private static VaTag ParseTag(JsonElement t)
		{
			try
			{
				if (t.ValueKind == JsonValueKind.String)
				{
					string s = t.GetString();
					return string.IsNullOrWhiteSpace(s) ? null : new VaTag { Text = s };
				}
				if (t.ValueKind != JsonValueKind.Object) return null;
				string text = t.TryGetProperty("text", out var te) ? te.GetString() : null;
				if (string.IsNullOrWhiteSpace(text)) return null;
				return new VaTag
				{
					Text = text,
					Color = t.TryGetProperty("color", out var c) ? (c.GetString() ?? "") : "",
					B = t.TryGetProperty("b", out var b) && b.ValueKind == JsonValueKind.True,
					I = t.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.True,
					U = t.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.True,
					Fx = t.TryGetProperty("fx", out var f) ? (f.GetString() ?? "none") : "none",
				};
			}
			catch { return null; }
		}

		private static void WriteTag(string endpoint, string uid, VaTag tag, string okVerb)
		{
			uid = (uid ?? "").Trim();
			string text = (tag?.Text ?? "").Trim();
			if (!LooksLikeUserId(uid)) { LastStatus = "invalid VRChat user id (usr_… expected)"; return; }
			if (string.IsNullOrEmpty(text)) { LastStatus = "empty tag"; return; }

			var payload = new Dictionary<string, object>
			{
				["user_id"] = uid, ["text"] = text, ["color"] = tag.Color ?? "",
				["b"] = tag.B, ["i"] = tag.I, ["u"] = tag.U, ["fx"] = tag.Fx ?? "none",
				["actor"] = LocalUserId(),
			};
			string body = JsonSerializer.Serialize(payload);
			string action = endpoint.Substring(endpoint.LastIndexOf('/') + 1);   // "add" | "remove"
			LastStatus = "saving…";
			Task.Run(async () =>
			{
				string status; List<VaTag> newTags = null;
				var (ok, raw, code) = await VaAuth.SendWriteAsync(action, body);
				try
				{
					using JsonDocument doc = JsonDocument.Parse(raw);
					if (ok)
					{
						status = $"✓ {okVerb}: {text}";
						if (doc.RootElement.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
						{
							newTags = new List<VaTag>();
							foreach (var e in tagsEl.EnumerateArray()) { var vt = ParseTag(e); if (vt != null) newTags.Add(vt); }
						}
					}
					else if (code == 401) status = "login required — connect your VRChat Archive account (your tag ▸ Connect)";
					else status = doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : $"server error {code}";
				}
				catch { status = ok ? $"✓ {okVerb}: {text}" : "unable to contact the VRChatArchive tag server"; }
				MainThreadQueue.Enqueue(() =>
				{
					LastStatus = status;
					if (newTags == null) return;
					if (newTags.Count > 0) _db[uid] = newTags.ToArray(); else _db.Remove(uid);
					RecordsLoaded = _db.Count; _dbVersion++;
				});
			});
		}

		private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 60 ? s : s.Substring(0, 60));

		// ---------------------------------------------------------------- roster

		private void RefreshRoster()
		{
			// Bumped BEFORE the rebuild: this and every reader run on the main thread in sequence, so
			// nobody can observe a half-built roster with the new version — a poll in a later frame
			// sees the finished list either way. Readers use it to skip re-formatting unchanged data.
			RosterVersion++;
			Roster.Clear();
			Transform localT = PlayerRef.LocalTransform();
			string ownerId = CurrentInstanceOwnerId();
			var live = new HashSet<IntPtr>();

			foreach (object player in FewTagsModule.EnumeratePlayers())
			{
				try
				{
					var b = player as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
					IntPtr key = b != null ? b.Pointer : IntPtr.Zero;
					if (key != IntPtr.Zero) live.Add(key);

					// Reuse the entry for a player we have already read in full.
					PlayerEntry entry = null;
					bool known = key != IntPtr.Zero && _entryCache.TryGetValue(key, out entry);
					if (!known)
					{
						entry = new PlayerEntry { Player = player };

						// --- STABLE FIELDS: read ONCE, on first sight of this player. ---
						entry.UserId = FewTagsModule.UserIdOf(player);
						try
						{
							object api = FewTagsModule.GetMemberByTypeName(player, "VRCPlayerApi", "prop_VRCPlayerApi_0", "field_Public_VRCPlayerApi_0");
							object pid = api != null ? FewTagsModule.GetMember(api, "playerId") : null;
							if (pid is int pi) entry.PlayerId = pi;
							object master = api != null ? FewTagsModule.GetMember(api, "isMaster") : null;
							if (master is bool mb) entry.IsMaster = mb;
						}
						catch { }

						entry.ApiResolved = ReadApiFields(entry, player);
						if (string.IsNullOrEmpty(entry.Name)) entry.Name = "?";

						if (key != IntPtr.Zero) _entryCache[key] = entry;
					}

					// --- VOLATILE FIELDS: refreshed every pass. ---
					// The APIUser loads asynchronously: a player seen before their profile arrived was
					// cached with empty badges/platform ("no data" rows). Keep re-reading until it
					// really resolves, then stop — so those rows fill in instead of staying blank.
					// Retried while it can still land, NOT forever. ReadApiFields only reports
					// "resolved" once the platform string arrives, so a player whose APIUser never
					// loads (out of range, still joining, a profile the client never fetches) kept
					// re-running the whole badge read — four il2cpp property reads plus the trust
					// colour — on every pass for the rest of the session. A few dozen passes is more
					// than enough for an async load; after that the entry keeps what it has.
					if (!entry.ApiResolved && entry.ApiTries < 40)
					{
						entry.ApiTries++;
						entry.ApiResolved = ReadApiFields(entry, player);
					}

					// THE POSITION SOURCE, fixed. It used to be (player as Component).transform \u2014 a base-cast
					// of the Player proxy that can hand back a wrapper whose native liveness check fails even
					// for a LIVE player, leaving Transform null and the whole X/Y/Z row blank. Those were the
					// "no data" players. The VRCPlayerApi carries the same gameObject EnumeratePlayers found
					// the player through, so it is the reliable source; the component cast (now a TryCast, not
					// a plain 'as') stays only as a fallback. A dead proxy's .transform is an uncatchable
					// access violation, so both paths gate on NativeGuard before touching it.
					// RESOLVED ONCE, REUSED WHILE ALIVE (2026-09-13). This whole block used to run for
					// every player on every pass: resolve the VRCPlayerApi by type name, read its
					// gameObject, cast it, prove it alive, then walk to .transform — four il2cpp
					// crossings each, thirty-five players, several times a minute, for two references
					// that only change when the player's avatar is rebuilt.
					//
					// Both are cached on the entry now and re-resolved ONLY when the cached one fails
					// its liveness proof — which is exactly the case the old code was re-resolving
					// for. Same references, same fallbacks, same result; the work is just not repeated
					// while nothing has changed.
					object apiV = entry.ApiObj;
					if (apiV != null && !Core.NativeGuard.Alive(apiV)) apiV = null;
					if (apiV == null)
					{
						try { apiV = FewTagsModule.GetMemberByTypeName(player, "VRCPlayerApi", "prop_VRCPlayerApi_0", "field_Public_VRCPlayerApi_0"); }
						catch { }
						entry.ApiObj = apiV;
					}

					// The cached transform is good until the object behind it dies (avatar swap,
					// player leaving), which is precisely what Alive() answers.
					Transform tr = entry.Transform;
					if (tr != null && !Core.NativeGuard.Alive(tr)) tr = null;
					if (tr == null)
					{
						if (apiV != null)
						{
							try
							{
								var goApi = (FewTagsModule.GetMember(apiV, "gameObject") as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<GameObject>();
								if (goApi != null && Core.NativeGuard.Alive(goApi)) tr = goApi.transform;
							}
							catch { }
						}
						if (tr == null)
						{
							var comp = (player as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Component>() ?? player as Component;
							tr = FewTagsModule.AliveOrNull(comp)?.transform;
						}
					}
					entry.Transform = tr;

					// POSITION FOR THE PANEL, resolved the reliable way. A few players never yield a usable
					// Transform even through the api's gameObject; VRCPlayerApi.GetPosition() is the SDK's own
					// getter and answers for any live player, so it fills the last "no data" rows.
					if (tr != null) { entry.Pos = tr.position; entry.HasPos = true; }
					else
					{
						entry.HasPos = false;
						try
						{
							var papi = (apiV as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<VRC.SDKBase.VRCPlayerApi>();
							if (papi != null && Core.NativeGuard.Alive(papi)) { entry.Pos = papi.GetPosition(); entry.HasPos = true; }
						}
						catch { }
					}
					entry.IsLocal = localT != null && entry.Transform == localT;
					entry.IsOwner = !string.IsNullOrEmpty(ownerId) && string.Equals(entry.UserId, ownerId, StringComparison.OrdinalIgnoreCase);

					// isMaster CAN change (the holder leaves), so re-read it — it is one cheap read,
					// unlike the six stable ones above.
					try
					{
						object master2 = apiV != null ? FewTagsModule.GetMember(apiV, "isMaster") : null;
						if (master2 is bool mb2) entry.IsMaster = mb2;
					}
					catch { }

					// THE AVATAR RE-READ IS THE ROSTER'S LAST UNCACHED COST (2026-09-13).
					//
					// ResolveAvatar walks player -> ApiAvatar (or player -> VRCPlayer -> ApiAvatar),
					// three GetMemberByTypeName calls, each an il2cpp property invoke gated by a
					// VirtualQuery liveness syscall. With everything else on the entry now cached,
					// this is what kept a fully-cached 35-player pass at ~265 ms (the [VaTags/roster]
					// timer proved it: "35 cached" and still hundreds of ms).
					//
					// An avatar does change while someone stands there, so it cannot be cached once
					// and forgotten — but it does not change between two passes a couple of seconds
					// apart. Re-read on first sight and then one pass in three: clone and metadata
					// still see a swap within ~3-6 s, and two thirds of the crossings are gone. The
					// entry keeps its last value on a skipped pass, which is the correct answer while
					// nothing changed.
					entry.AvatarPass++;
					if (entry.AvatarPass == 1 || (entry.AvatarPass % 3) == 0)
					{
						ResolveAvatar(player, out string avId, out string avName);
						// Someone who blocked you is swapped to a local invisible shell; keep the last real
						// id for the archive instead of letting that shell overwrite it.
						bool blockedMe = !entry.IsLocal && BlockedByProbeModule.BlockedMe.Contains(entry.UserId ?? "");
						if (blockedMe && !string.IsNullOrEmpty(entry.AvatarId) && avId != entry.AvatarId)
						{
							if (entry.BlockedAvatarLogged != avId)
							{
								entry.BlockedAvatarLogged = avId;
								VRChatArchiveModPlugin.Logger.LogInfo("[BlockedAvatar] " + entry.Name + " t'a bloque — avatar garde "
									+ entry.AvatarId + " (le jeu lit maintenant " + (avId ?? "rien") + ")");
							}
						}
						else { entry.AvatarId = avId; entry.AvatarName = avName; }
					}

					// FOLDED IN FROM BlockedByProbe (2026-09-13): read the block flag HERE, in the
					// pass that already walked this player and proved it live, so that module does not
					// repeat the entire 40-player walk — and its 40 VirtualQuery liveness syscalls —
					// a second time each second. It only copies entry.Blocked afterwards.
					try { BlockedByProbeModule.DumpBooleanProfile(player, entry.UserId, entry.IsLocal); } catch { }
					if (!entry.IsLocal) { try { BlockedByProbeModule.DumpPlayerLayout(player); } catch { } }
					if (!entry.IsLocal && BlockedByProbeModule.WantBlockProbe())
						entry.Blocked = BlockedByProbeModule.ReadBlockedFlag(player);

					Roster.Add(entry);
				}
				catch { }
			}

			// Drop cache entries for players who are gone, so it cannot grow across instances.
			if (_entryCache.Count > live.Count)
			{
				var dead = new List<IntPtr>();
				foreach (var k in _entryCache.Keys) if (!live.Contains(k)) dead.Add(k);
				foreach (var k in dead) _entryCache.Remove(k);
			}
			Roster.Sort((a, b) =>
			{
				if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
				if (a.IsOwner != b.IsOwner) return a.IsOwner ? -1 : 1;
				return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
			});
		}

		// Current instance's owner user id (creator of a private/friends/invite/group+ instance;
		// empty for public). RoomManager (static) → current ApiWorldInstance → ownerId.
		private static Type _roomMgrType;
		private static PropertyInfo _instanceProp;
		private static bool _rmResolved;
		private static string _ownerId = "";
		private static float _ownerAt = -999f;
		public static string CurrentInstanceOwnerId()
		{
			try
			{
				if (!_rmResolved)
				{
					_rmResolved = true;
					_roomMgrType = Assembly.Load("Assembly-CSharp").GetType("RoomManager");
					if (_roomMgrType != null)
						_instanceProp = _roomMgrType.GetProperty("prop_ApiWorldInstance_0", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
							?? _roomMgrType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
								.FirstOrDefault(p => p.PropertyType.Name == "ApiWorldInstance");
				}
				// MEMOISED, because the roster asks for it on every pass and the answer changes only
				// when the instance does. Both calls below go through the recovered RoomManager: a
				// property read on a class placed by member shape, then a member lookup by name on
				// ApiWorldInstance. That is the same 'expensive failing lookup, retried every frame'
				// shape already fixed for LocalUserId and the nameplate container -- here it was the
				// whole of the 254 ms roster pass that ran with ZERO players in it.
				float now = VaClock.Now;
				if (now - _ownerAt < (_ownerId.Length > 0 ? 5f : 1f)) return _ownerId;
				_ownerAt = now;
				object inst = Core.ProxyGuard.GetValue(_instanceProp, null);
				return _ownerId = FewTagsModule.GetMember(inst, "ownerId") as string ?? "";
			}
			catch { return ""; }
		}


		// Full instance metadata, from the same RoomManager -> ApiWorldInstance we already resolve
		// for the owner. Everything here is data the client already holds about the room you are
		// standing in; nothing is requested from the network.
		public struct InstanceInfo
		{
			public string WorldName;
			public string WorldId;      // wrld_...
			public string InstanceId;   // the number before the '~'
			public string Region;       // use / usw / eu / jp ...
			public string Access;       // public / friends / invite / group ...
			public int Capacity;
			public int Present;
		}

		private static InstanceInfo _instCache;
		private static float _instAt;

		public static InstanceInfo CurrentInstance()
		{
			// Refreshed at most once a second: this walks reflection, and the menu asks for it
			// every frame it is open.
			float now = VaClock.Now;
			if (now - _instAt < 1f) return _instCache;
			_instAt = now;

			var info = new InstanceInfo { WorldName = "", WorldId = "", InstanceId = "", Region = "", Access = "" };
			try
			{
				CurrentInstanceOwnerId();                   // makes sure the reflection is resolved
				object inst = _instraw();
				if (inst != null)
				{
					string id = FewTagsModule.GetMember(inst, "id") as string ?? "";
					// The id can arrive either bare ("50358~region(use)~private(usr_..)") or prefixed
					// with the world ("wrld_xxxx:54782~region(use)"). A metadata dump showed the
					// prefixed form, where taking everything before the '~' printed the whole world
					// id as the instance number.
					if (!string.IsNullOrEmpty(id))
					{
						string head = id;
						int tilde = head.IndexOf('~');
						if (tilde > 0) head = head.Substring(0, tilde);
						int colon = head.LastIndexOf(':');
						info.InstanceId = colon >= 0 ? head.Substring(colon + 1) : head;
						info.Region = Between(id, "region(", ")");
						foreach (string a in new[] { "public", "hidden", "friends", "private", "group" })
							if (id.IndexOf(a + "(", StringComparison.OrdinalIgnoreCase) >= 0
								|| id.IndexOf("~" + a, StringComparison.OrdinalIgnoreCase) >= 0) { info.Access = a; break; }
					}
					if (string.IsNullOrEmpty(info.Access)) info.Access = "public";

					object world = FewTagsModule.GetMember(inst, "world");
					if (world != null)
					{
						info.WorldName = FewTagsModule.GetMember(world, "name") as string ?? "";
						info.WorldId = FewTagsModule.GetMember(world, "id") as string ?? "";
						if (FewTagsModule.GetMember(world, "capacity") is int cap) info.Capacity = cap;
					}
					if (string.IsNullOrEmpty(info.WorldId))
						info.WorldId = FewTagsModule.GetMember(inst, "worldId") as string ?? "";
				}
			}
			catch { }

			// LAST RESORT: VRCHAT'S OWN LOG. RoomManager and ApiWorldInstance are obfuscated, and on a
			// build where they cannot be placed everything above returns empty — silently. The world id
			// is then missing from the sync, and every feature keyed on it (the client's Udon world
			// profiles, its preset packs) goes quiet with no message at all. VRChat prints the id in
			// plain text on every join, so that is read rather than leaving the field blank.
			try
			{
				Core.WorldFromLog.Arm();
				if (string.IsNullOrEmpty(info.WorldId) && Core.WorldFromLog.WorldId.Length > 0)
				{
					info.WorldId = Core.WorldFromLog.WorldId;
					if (string.IsNullOrEmpty(info.InstanceId)) info.InstanceId = Core.WorldFromLog.InstanceId;
				}
				if (string.IsNullOrEmpty(info.WorldName) && Core.WorldFromLog.WorldName.Length > 0)
					info.WorldName = Core.WorldFromLog.WorldName;
			}
			catch { }

			try { var ps = VRChatArchiveMod.Core.VaPlayers.All(); info.Present = ps != null ? ps.Count : 0; } catch { }
			_instCache = info;
			return info;
		}

		private static object _instraw()
		{
			// RoomManager is obfuscated and absent under its 1886 name here; its static getter through
			// the placeholder is an access violation. ProxyGuard answers null instead.
			try { return Core.ProxyGuard.GetValue(_instanceProp, null); } catch { return null; }
		}

		private static string Between(string s, string a, string b)
		{
			try
			{
				int i = s.IndexOf(a, StringComparison.OrdinalIgnoreCase);
				if (i < 0) return "";
				i += a.Length;
				int j = s.IndexOf(b, i, StringComparison.OrdinalIgnoreCase);
				return j > i ? s.Substring(i, j - i) : "";
			}
			catch { return ""; }
		}

		// Reads the ASYNC APIUser fields onto the entry. Returns true only once the profile has
		// really loaded (a platform is the reliable "it arrived" marker) so the caller can stop
		// retrying. Cheap to call every pass while unresolved — a few reflection reads for the
		// handful of players still loading, and nothing once they are in. This is the fix for
		// rows that showed no VRC+/platform badge because the player was seen before their APIUser
		// populated and the empty read was cached forever.
		private static bool ReadApiFields(PlayerEntry entry, object player)
		{
			try
			{
				object apiUser = FewTagsModule.GetMemberByTypeName(player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				if (apiUser == null) return false;
				string name = FewTagsModule.GetMember(apiUser, "displayName") as string;
				if (!string.IsNullOrEmpty(name) && name != "?") entry.Name = name;
				entry.TrustColor = TrustColor(apiUser);
				var badges = Core.ApiUsers.BadgesFrom(apiUser);
				entry.Plus = badges.Plus; entry.Adult = badges.Adult;
				if (!string.IsNullOrEmpty(badges.Platform)) entry.Platform = badges.Platform;
				return !string.IsNullOrEmpty(entry.Platform);
			}
			catch { return false; }
		}

		// Trust colour via the shared TrustKit (typed APIUser properties — the access ESP
		// has always used successfully; parsing the raw tags list was unreliable).
		private static string TrustColor(object apiUser) => TrustKit.HexOf(apiUser);

		// PUBLIC OR PRIVATE, read off the player instead of asked over the network.
		//
		// Whether an avatar can be cloned is a property of the avatar, and VRChat has already told
		// this client the answer: the remote player's ApiAvatar carries releaseStatus. Reading it
		// here costs nothing and is instant, where asking the API would be one request per player
		// per instance — rate limits, and a delay before the answer arrives.
		//
		// Returns "public", "private", or "" when the avatar has not loaded far enough to say.
		public static string ReleaseStatusOf(PlayerEntry entry)
		{
			try
			{
				if (entry == null) return "";
				object api = ResolveApiAvatarObject(entry.Player);
				if (api == null) return "";
				var a = (api as Il2CppSystem.Object)?.TryCast<VRC.Core.ApiAvatar>();
				// Reading a property off an ApiAvatar whose native object is gone is an access
				// violation, not an exception — the try below cannot save it. This is now called
				// twice a second to keep the Clone Avatar card's colour honest, so the gate matters
				// far more than it did when it ran once per click.
				if (a == null || !Core.NativeGuard.Alive(a)) return "";
				string rs = null;
				try { rs = a.releaseStatus; } catch { }
				return string.IsNullOrEmpty(rs) ? "" : rs.Trim().ToLowerInvariant();
			}
			catch { return ""; }
		}

		private static object ResolveApiAvatarObject(object player)
		{
			try
			{
				object av = FewTagsModule.GetMemberByTypeName(player, "ApiAvatar", "prop_ApiAvatar_0", "field_Private_ApiAvatar_0");
				if (av == null)
				{
					object vrcplayer = FewTagsModule.GetMemberByTypeName(player, "VRCPlayer", "_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
					av = FewTagsModule.GetMemberByTypeName(vrcplayer, "ApiAvatar", "prop_ApiAvatar_0", "field_Private_ApiAvatar_0");
				}
				return av;
			}
			catch { return null; }
		}

		private static void ResolveAvatar(object player, out string id, out string name)
		{
			id = null; name = null;
			try
			{
				object av = ResolveApiAvatarObject(player);
				if (av == null) return;
				id = FewTagsModule.GetMember(av, "id") as string;
				name = FewTagsModule.GetMember(av, "name") as string;
				if (id != null && !id.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase)) id = null;
			}
			catch { }
		}

		// ---------------------------------------------------------------- nameplate plates

		private static bool _emptyDbLogged;
		// The last (roster/want/fail/plates) shape reported, so the line repeats only on a change.
		private static string _lastShape = "";
		private int _wantPlates, _resolveFail;
		private bool _budgetSaid;
		// Once a single plate build has proven pathological, the feature stays off for the session.
		private bool _platesBroken;
		private int _slowStrikes;
		// RECALIBRATED AGAINST THE REAL COST, not against the bug.
		//
		// 250 ms was chosen while a scene-wide sweep made one resolve take 14 673 ms -- anything would
		// have caught that. With the sweep replaced by GameObject.Find the same resolve measures 303 ms,
		// almost all of it first-time setup (locating the manager, indexing the containers), and the old
		// ceiling then switched tag plates off for 303 ms of warm-up. A limit tuned to a fault it no
		// longer has is a limit that only punishes the healthy case.
		//
		// 1200 ms leaves room for that warm-up and still catches the pathology by a factor of twelve.
		private const double SlowPlateMs = 1200.0;
		// The whole-pass ceiling. Above this the frame is visibly hitching, whatever caused it.
		private const double SlowPassMs = 1200.0;

		// When each user's nameplate resolve last failed. Cleared on success and on scene change:
		// a new world rebuilds every nameplate, so nothing here survives it.
		private static readonly Dictionary<string, float> _plateMissAt = new Dictionary<string, float>(StringComparer.Ordinal);

		private void ApplyPlates()
		{
			_wantPlates = 0; _resolveFail = 0;
			// Tripped by the circuit breaker below: never rebuild, and never freeze again this session.
			if (_platesBroken) return;
			if (_db.Count == 0)
			{
				if (_plates.Count > 0) RemoveAllPlates();
				if (!_emptyDbLogged) { _emptyDbLogged = true; VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] no tags to show yet — database is empty (fetch not landed or 0 records)."); }
				return;
			}
			_emptyDbLogged = false;
			float spacing = FewTagsModule.EffectiveSpacing();
			float configBaseY = ModConfig.VaTagsPlateY.Value;

			// A HARD BUDGET, BECAUSE A PASS THAT OVERRUNS FREEZES THE GAME.
			//
			// Measured in production: "[VaTags/plates] pass took 11492.6 ms — 1 wanted". Eleven and a
			// half seconds inside ONE frame, to build ONE plate set. Windows greys an unresponsive
			// window, so the owner reported it as a crash -- and they were right to: an eleven second
			// freeze is a crash as far as playing is concerned.
			//
			// The root cause of that one slow build is worth finding, but it must never again be able
			// to hold the frame while we look for it. Work stops at the budget and resumes next pass;
			// plates appear a fraction of a second later at worst, which nobody can see. Correctness is
			// unaffected -- nothing here is all-or-nothing, each player's set is independent.
			var budget = System.Diagnostics.Stopwatch.StartNew();
			const double BudgetMs = 4.0;
			int built = 0;

			foreach (var entry in Roster)
			{
				if (budget.Elapsed.TotalMilliseconds > BudgetMs)
				{
					if (!_budgetSaid)
					{
						_budgetSaid = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[VaTags] budget de " + BudgetMs + " ms atteint apres "
							+ built + " plaque(s) — le reste passe a la frame suivante plutot que de figer le jeu.");
					}
					break;
				}

				// The local player gets plates too: they show up in mirrors and confirm your
				// own tag is really applied (VRChat hides your nameplate from yourself).
				if (string.IsNullOrEmpty(entry.UserId)) continue;
				if (!_db.TryGetValue(entry.UserId, out VaTag[] tags) || tags.Length == 0)
				{
					// A still-present player whose tags were all removed: tear down their
					// stale plate set instead of leaving it rendering/animating forever.
					DestroySet(entry.UserId);
					continue;
				}
				// FewTags draws its own stack on the SAME nameplate from its own baseline, so a
				// user with both ended up with the two sets written over each other. Start above
				// whatever FewTags is currently using for THIS user, and rebuild if that changes.
				int fewLines = FewTagsModule.LinesFor(entry.UserId);
				float baseY = Mathf.Max(configBaseY, FewTagsModule.EffectiveBaseY() + fewLines * spacing);

				if (_plates.TryGetValue(entry.UserId, out PlateSet ps) && ps.DbVersion == _dbVersion
					&& ps.FewLines == fewLines && SetAlive(ps)) continue;

				DestroySet(entry.UserId);

				// THE LOCAL PLAYER IS NOT SKIPPED. A previous attempt here assumed VRChat draws no
				// nameplate for you and skipped it — wrong: your own nameplate exists and you see it
				// in a mirror, which is where people look at their own tags. Skipping it removed a
				// feature that used to work.
				_wantPlates++;
				built++;

				// A RESOLVE THAT FAILED DOES NOT GET RETRIED EVERY PASS.
				//
				// A player whose plate set cannot be built is never cached, so the loop came back to
				// them on every pass and paid the full nameplate resolve again -- the same "expensive
				// failing lookup, retried for ever" defect already fixed for LocalUserId, the nameplate
				// container and CurrentInstanceOwnerId. With three tagged players in the room the
				// profiler read VaTags=908 ms/s. Two seconds is far tighter than a nameplate ever
				// appears, so a plate that becomes resolvable still lands promptly.
				float nowP = VaClock.Now;
				if (_plateMissAt.TryGetValue(entry.UserId, out float missAt) && nowP - missAt < 2f) { _resolveFail++; continue; }

				// TIMED, because the breaker around MakePlate did NOT fire and the pass STILL took
				// 11 100 ms. That rules MakePlate out and leaves this call: resolving a nameplate walks
				// a hierarchy, and on a heavy avatar that walk is the only thing here big enough to eat
				// eleven seconds. Measuring names the culprit instead of narrowing by halves across
				// another of the owner's play sessions.
				var rn = System.Diagnostics.Stopwatch.StartNew();
				bool resolved = FewTagsModule.ResolveNameplate(entry.Player, out GameObject quickStats, out Transform contents);
				double rnMs = rn.Elapsed.TotalMilliseconds;
					// ONE SLOW PLAYER IS NOT A BROKEN FEATURE.
					//
					// The first version of this breaker switched tag plates off for the whole session the
					// instant a single resolve ran long, and that is exactly what happened: one 13.6 s
					// outlier -- the scene-wide diagnostic probe, now disarmed -- and the owner lost plates
					// until a restart. Tripping on the first sample cannot tell an outlier from a fault, and
					// it costs the feature either way.
					//
					// So a slow resolve now benches THAT PLAYER for a minute and counts a strike. Plates keep
					// working for everyone else, and only a fault that keeps coming back is treated as one.
					if (rnMs > SlowPlateMs)
					{
						_slowStrikes++;
						_plateMissAt[entry.UserId] = nowP + 60f;
						VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] resoudre une nameplate a pris "
							+ ((int)rnMs) + " ms (limite " + SlowPlateMs + ") pour '" + (entry.UserId ?? "?")
							+ "' — ce joueur est mis de cote 60 s. Avertissement " + _slowStrikes + "/3.");
						if (_slowStrikes >= 3)
						{
							_platesBroken = true;
							VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] COUPE-CIRCUIT : trois resolutions lentes. "
								+ "Plaques de tags DESACTIVEES pour cette session.");
						}
						continue;
					}
				if (!resolved)
				{
					_resolveFail++;
					_plateMissAt[entry.UserId] = nowP;
					continue;
				}
				_plateMissAt.Remove(entry.UserId);

				var set = new PlateSet { DbVersion = _dbVersion, FewLines = fewLines };
				for (int i = 0; i < tags.Length; i++)
				{
					// A CIRCUIT BREAKER AROUND THE ONE CALL THAT FROZE THE GAME.
					//
					// The per-player budget above cannot help here: it is checked BETWEEN players, and
					// the measured 11 195 ms was spent inside a SINGLE build. So the cost is timed where
					// it happens, and if one plate is pathologically slow the feature stands down for the
					// session rather than freezing every frame that rebuilds it.
					//
					// Standing down is the right trade: tags are decoration, and a decoration costing
					// eleven seconds of frozen game is not worth showing. The log names the cost and the
					// tag, so the real cause can be found without the owner losing a session to it.
					var mk = System.Diagnostics.Stopwatch.StartNew();
					GameObject go = FewTagsModule.MakePlate(quickStats, contents, baseY + i * spacing, tags[i].Text);
					double mkMs = mk.Elapsed.TotalMilliseconds;
					if (mkMs > SlowPlateMs)
					{
						_platesBroken = true;
						VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] COUPE-CIRCUIT : construire UNE plaque a pris "
							+ ((int)mkMs) + " ms (limite " + SlowPlateMs + "). Plaques de tags DESACTIVEES pour cette "
							+ "session — elles figeaient le jeu. Tag concerne : '" + (tags[i].Text ?? "") + "'.");
						if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
						break;
					}
					if (go == null) continue;
					// The label MakePlate actually wrote to. Searching the clone again for
					// "Trust Text" was wrong on two counts: the anchor is named GroupName on the
					// current nameplate layout, and the fallback excluded inactive objects while
					// MakePlate deactivates every branch around the label. Both missed, so every
					// tag logged "no TMP label found" and lost its animation.
					var tmp = FewTagsModule.LastPlateLabel;
					if (tmp == null)
					{
						// Nothing to animate and nothing tracking this clone — destroy it rather
						// than leave an untracked plate behind on every pass.
						VRChatArchiveModPlugin.Logger.LogWarning("[VaTags] plate built but no TMP label found — tag will not animate.");
						UnityEngine.Object.Destroy(go);
						continue;
					}
					set.Plates.Add(new Plate
					{
						Go = go, Tmp = tmp, Tag = tags[i],
						G = Glyphs(tags[i].Text ?? ""),
						Fx = (tags[i].Fx ?? "none").ToLowerInvariant(),
					});
				}
				if (set.Plates.Count > 0) _plates[entry.UserId] = set;
			}

			// One-time snapshot so the next in-game run says plainly where tags stand: how many
			// tagged people are around you, how many nameplates we could attach to, how many plates
			// are live. "want 3, resolveFail 3" means the nameplate structure changed; "want 0" means
			// nobody tagged is in range; "plates N" with N>0 means it is working.
			// RE-SAID WHENEVER THE ANSWER CHANGES, not once per session.
			//
			// Written once, this line always describes the FIRST seconds of a session — which is
			// exactly when you are alone in the instance and nothing can be resolved. It then never
			// updates, so joining a full world where everything works still leaves a log saying
			// "roster=1, resolve-failed=1", and the only conclusion available from it is wrong.
			//
			// Keyed on the outcome, so a stable session stays quiet and any real change speaks up.
			if (_wantPlates > 0 || Roster.Count > 0)
			{
				string shape = Roster.Count + "/" + _wantPlates + "/" + _resolveFail + "/" + _plates.Count;
				if (shape != _lastShape)
				{
					_lastShape = shape;
					VRChatArchiveModPlugin.Logger.LogInfo($"[VaTags] plates: roster={Roster.Count}, db={_db.Count}, "
						+ $"tagged-in-range={_wantPlates}, nameplate-resolve-failed={_resolveFail}, "
						+ $"live-plate-sets={_plates.Count}. {FewTagsModule.LastResolveFailure}");
				}
			}
			// SAID IN THE CLIENT, not just in a log nobody reads mid-session. "Your own tags never
			// show in game" is the single most likely reason someone thinks this is broken.
			try
			{
				if (!ModConfig.VaTagsShowPlates.Value)
					Core.FeatureHealth.Idle("VaTags/ShowPlates", "off");
				else if (_plates.Count > 0)
					Core.FeatureHealth.Ok("VaTags/ShowPlates", "on — " + _plates.Count + " tagged player(s) showing");
				else if (_resolveFail > 0)
					Core.FeatureHealth.Broken("VaTags/ShowPlates",
						"on, but " + _resolveFail + " nameplate(s) could not be resolved — "
						+ (FewTagsModule.LastResolveFailure.Length > 0 ? FewTagsModule.LastResolveFailure : "reason unknown"));
				else
					Core.FeatureHealth.Ok("VaTags/ShowPlates", "on — nobody tagged is in range");
			}
			catch { }

			PruneDeadSets();
		}

		// The FewTags-style effect set. Each frame the plate's TMP text (and sometimes its
		// alpha/color) is recomposed from the tag's plain text — the stored tag never holds
		// markup, so effects can be swapped freely and nothing can be injected.
		private static void AnimatePlate(Plate p, float t)
		{
			var tag = p.Tag;
			string inner = tag.Text ?? "";
			if (inner.Length == 0) return;
			// Per-GLYPH, never per-char: an emoji is a UTF-16 surrogate pair, so indexing the
			// string directly would wrap each half in its own rich-text span and render mojibake.
			string[] g = p.G ?? Glyphs(inner);
			string fx = p.Fx ?? "none";

			// Effects that don't drive alpha/color themselves keep the plate neutral.
			if (fx != "blink" && fx != "glitch" && p.Tmp.alpha != 1f) p.Tmp.alpha = 1f;

			switch (fx)
			{
				// Scrolls the text left on a loop (supports coloring).
				case "scroll":
				{
					string[] pad = new string[g.Length + 3];
					Array.Copy(g, pad, g.Length);
					for (int k = g.Length; k < pad.Length; k++) pad[k] = " ";
					int off = (int)(t * 6f) % pad.Length;
					string s = Join(pad, off, pad.Length - off) + Join(pad, 0, off);
					SetText(p, tag, s, colored: true);
					break;
				}

				// A red line bounces back and forth across the text (one colour).
				case "cyln":
				{
					int n = g.Length;
					int pos = PingPong(t * 8f, n);
					// Every character carries its own span (the tag's colour for the normal
					// ones): leaving them bare made TMP fall back to plain white, so the
					// chosen colour silently disappeared in-game.
					string baseHex = string.IsNullOrEmpty(tag.Color) ? "#FFFFFF" : tag.Color;
					var sb = new StringBuilder(g.Length * 26);
					for (int i = 0; i < n; i++)
						sb.Append("<color=").Append(i == pos ? "#FF2A2A" : baseHex).Append('>').Append(g[i]).Append("</color>");
					SetText(p, tag, sb.ToString(), colored: true, alreadyHasColorSpans: true);
					break;
				}

				// Letter by letter: characters are removed one by one, then added back.
				case "lbl":
				{
					int n = g.Length;
					int keep = PingPong(t * 5f, n + 1);
					SetText(p, tag, Join(g, 0, Mathf.Clamp(keep, 0, n)), colored: true);
					break;
				}

				// Chunky stepped rainbow (old CoD:WaW modded clan tag look) — no coloring.
				case "rain":
				case "rainbow":
				{
					var sb = new StringBuilder(g.Length * 22);
					int step = (int)(t * 10f);
					for (int i = 0; i < g.Length; i++)
					{
						float h = Mathf.Repeat((step + i) / 7f, 1f);   // 7 discrete hues
						Color c = Color.HSVToRGB(h, 1f, 1f);
						sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>').Append(g[i]).Append("</color>");
					}
					SetText(p, tag, sb.ToString(), colored: false, alreadyHasColorSpans: true);
					break;
				}

				// Smooth rainbow — no coloring.
				case "sr":
				{
					var sb = new StringBuilder(g.Length * 22);
					for (int i = 0; i < g.Length; i++)
					{
						float h = Mathf.Repeat(t * 0.35f + i * 0.06f, 1f);
						Color c = Color.HSVToRGB(h, 0.85f, 1f);
						sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>').Append(g[i]).Append("</color>");
					}
					SetText(p, tag, sb.ToString(), colored: false, alreadyHasColorSpans: true);
					break;
				}

				// A size wave travels through the text (supports coloring).
				case "pulse":
				case "wave":
				{
					var sb = new StringBuilder(g.Length * 26);
					for (int i = 0; i < g.Length; i++)
					{
						float k = Mathf.Max(0f, Mathf.Sin(t * 5f - i * 0.7f));
						int pct = 100 + (int)(k * 60f);
						sb.Append("<size=").Append(pct).Append("%>").Append(g[i]).Append("</size>");
					}
					SetText(p, tag, sb.ToString(), colored: true);
					break;
				}

				// Text jumps up and down smoothly (supports coloring).
				case "jump":
				{
					var sb = new StringBuilder(g.Length * 26);
					for (int i = 0; i < g.Length; i++)
					{
						float off = Mathf.Max(0f, Mathf.Sin(t * 6f - i * 0.5f)) * 0.35f;
						sb.Append("<voffset=").Append(F(off)).Append("em>").Append(g[i]).Append("</voffset>");
					}
					SetText(p, tag, sb.ToString(), colored: true);
					break;
				}

				// Shakes side to side (partly supports coloring).
				case "shake":
				{
					var sb = new StringBuilder(g.Length * 28);
					for (int i = 0; i < g.Length; i++)
					{
						float off = Mathf.Sin(t * 30f + i * 2.3f) * 0.09f;
						sb.Append("<voffset=").Append(F(off * 0.5f)).Append("em><cspace=")
						  .Append(F(off)).Append("em>").Append(g[i]).Append("</cspace></voffset>");
					}
					SetText(p, tag, sb.ToString(), colored: true);
					break;
				}

				// Ghost trail: a dark fade sweeps across the text — no coloring.
				case "gt":
				{
					int n = g.Length;
					float head = Mathf.Repeat(t * 6f, n + 4f);
					var sb = new StringBuilder(g.Length * 24);
					for (int i = 0; i < n; i++)
					{
						float d = Mathf.Clamp01(1f - Mathf.Abs(head - i) / 2.5f);
						byte v = (byte)Mathf.Lerp(255f, 40f, d);
						sb.Append("<color=#").Append(v.ToString("X2")).Append(v.ToString("X2")).Append(v.ToString("X2"))
						  .Append('>').Append(g[i]).Append("</color>");
					}
					SetText(p, tag, sb.ToString(), colored: false, alreadyHasColorSpans: true);
					break;
				}

				// Blinks — no coloring nuance needed, alpha does the work.
				case "blink":
					if (!p.StaticBuilt) { SetText(p, tag, inner, colored: true, force: true); p.StaticBuilt = true; }
					p.Tmp.alpha = (Mathf.Repeat(t, 0.8f) < 0.4f) ? 1f : 0.1f;
					break;

				// Random glitch characters (partly supports coloring).
				case "glitch":
				{
					// No < or > in the junk: they would be parsed as markup by TMP.
					const string Junk = "!@#$%^&*?/\\|=+~";
					int bucket = (int)(t * 12f);
					string baseHex = string.IsNullOrEmpty(tag.Color) ? "#FFFFFF" : tag.Color;
					var sb = new StringBuilder(g.Length * 26);
					for (int i = 0; i < g.Length; i++)
					{
						// Deterministic per (bucket, index) so it doesn't strobe every frame.
						int h = (bucket * 73856093) ^ (i * 19349663);
						bool hit = ((h >> 5) & 7) == 0;
						// int.MinValue has no positive counterpart — mask instead of Abs.
						int pick = (h & 0x7FFFFFFF) % Junk.Length;
						if (hit) sb.Append("<color=#8CFF8C>").Append(Junk[pick]).Append("</color>");
						else sb.Append("<color=").Append(baseHex).Append('>').Append(g[i]).Append("</color>");
					}
					SetText(p, tag, sb.ToString(), colored: true, alreadyHasColorSpans: true);
					p.Tmp.alpha = (((bucket * 2654435761u) >> 7) & 15) == 0 ? 0.55f : 1f;
					break;
				}

				// VRChat Archive member badge: a fixed pink→violet gradient.
				case "grad":
				{
					if (!p.StaticBuilt) { SetText(p, tag, Gradient(inner), colored: true, alreadyHasColorSpans: true, force: true); p.StaticBuilt = true; }
					break;
				}

				// Soft glow pulse on the tag's own colour (legacy effect).
				case "glow":
				{
					if (!p.StaticBuilt) { SetText(p, tag, inner, colored: false, force: true); p.StaticBuilt = true; }
					Color baseC = ParseColor(tag.Color, Color.white);
					float k = (Mathf.Sin(t * 3f) + 1f) * 0.5f;
					// Scale RGB only: `Color * float` also scales ALPHA, which made the label
					// fade in and out instead of just pulsing brighter and dimmer.
					var dim = new Color(baseC.r * 0.55f, baseC.g * 0.55f, baseC.b * 0.55f, baseC.a);
					Color lit = Color.Lerp(dim, Color.white, k);
					lit.a = baseC.a;
					p.Tmp.color = lit;
					break;
				}

				default: // none / static
					if (!p.StaticBuilt) { SetText(p, tag, inner, colored: true, force: true); p.StaticBuilt = true; }
					break;
			}
		}

		// Splits text into RENDERABLE units instead of UTF-16 chars, so emoji survive the
		// per-character effects. An emoji is a surrogate PAIR (two chars); wrapping each half in
		// its own <color> span renders two broken boxes instead of one emoji. Variation selectors,
		// skin tones, combining marks and ZWJ sequences stay glued to the glyph they modify.
		public static string[] Glyphs(string s)
		{
			if (string.IsNullOrEmpty(s)) return System.Array.Empty<string>();
			var outp = new List<string>(s.Length);
			int i = 0;
			while (i < s.Length)
			{
				int len = Pair(s, i) ? 2 : 1;

				while (i + len < s.Length)
				{
					char c = s[i + len];

					if (c == ZeroWidthJoiner)                     // the NEXT glyph joins this one
					{
						int j = i + len + 1;
						if (j >= s.Length) break;
						len += 1 + (Pair(s, j) ? 2 : 1);
						continue;
					}

					if (c == VariationSelector16 || c == VariationSelector15 || IsMark(c)) { len++; continue; }

					// Skin-tone modifiers are their own surrogate pair (U+1F3FB..U+1F3FF).
					if (Pair(s, i + len))
					{
						int cp = char.ConvertToUtf32(c, s[i + len + 1]);
						if (cp >= 0x1F3FB && cp <= 0x1F3FF) { len += 2; continue; }
					}
					break;
				}

				outp.Add(s.Substring(i, len));
				i += len;
			}
			return outp.ToArray();
		}

		private const char ZeroWidthJoiner = (char)0x200D;
		private const char VariationSelector16 = (char)0xFE0F;
		private const char VariationSelector15 = (char)0xFE0E;

		private static bool Pair(string s, int i)
			=> i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]);

		private static bool IsMark(char c)
		{
			var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
			return cat == System.Globalization.UnicodeCategory.NonSpacingMark
			    || cat == System.Globalization.UnicodeCategory.EnclosingMark
			    || cat == System.Globalization.UnicodeCategory.SpacingCombiningMark;
		}

		private static string Join(string[] g, int start, int count)
		{
			if (g == null || count <= 0 || start >= g.Length) return "";
			if (start < 0) start = 0;
			if (start + count > g.Length) count = g.Length - start;
			var sb = new StringBuilder(count * 2);
			for (int i = 0; i < count; i++) sb.Append(g[start + i]);
			return sb.ToString();
		}

		// Applies bold/italic/underline (+ the tag colour unless the body already carries
		// per-character colour spans) and pushes the result, skipping identical rewrites.
		private static void SetText(Plate p, VaTag tag, string body, bool colored,
			bool alreadyHasColorSpans = false, bool force = false)
		{
			string s = Wrap(body, tag, includeColor: colored && !alreadyHasColorSpans);
			if (force || p.Tmp.text != s) p.Tmp.text = s;
		}

		private static string F(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

		// 0,1,…,n-1,n-2,…,0 — a bouncing index.
		private static int PingPong(float x, int n)
		{
			if (n <= 1) return 0;
			int period = (n - 1) * 2;
			int v = (int)Mathf.Repeat(x, period);
			return v < n ? v : period - v;
		}

		// Wrap inner text with bold/italic/underline (+ optional colour).
		// `imgui` drops <u>: Unity's IMGUI rich text has no underline tag and renders it
		// literally, which printed a stray "<u>" in the roster preview.
		private static string Wrap(string inner, VaTag tag, bool includeColor, bool imgui = false)
		{
			string s = inner;
			if (includeColor && !string.IsNullOrEmpty(tag.Color)) s = $"<color={tag.Color}>{s}</color>";
			if (tag.U && !imgui) s = $"<u>{s}</u>";
			if (tag.I) s = $"<i>{s}</i>";
			if (tag.B) s = $"<b>{s}</b>";
			return s;
		}

		// Static richText for menu chips (no animation there).
		public static string RichPreview(VaTag tag) => Wrap(tag.Text, tag, includeColor: true);

		// Animated preview for the IMGUI menu. Unity's IMGUI rich text supports only
		// <b>/<i>/<color>/<size=px> — NOT the TMP-only <voffset>/<cspace>/<size=%> the
		// nameplate effects use — so size/offset effects are approximated with what IMGUI
		// has. Returns markup safe for a GUIStyle with richText enabled.
		public static string ImguiPreview(VaTag tag, float t, int baseSize)
		{
			string inner = tag?.Text ?? "";
			if (inner.Length == 0) return "";
			string fx = (tag.Fx ?? "none").ToLowerInvariant();
			string col = string.IsNullOrEmpty(tag.Color) ? "#FFFFFF" : tag.Color;
			var sb = new StringBuilder(inner.Length * 26);

			switch (fx)
			{
				case "scroll":
				{
					string pad = inner + "   ";
					int off = (int)(t * 6f) % pad.Length;
					sb.Append("<color=").Append(col).Append('>').Append(pad.Substring(off)).Append(pad.Substring(0, off)).Append("</color>");
					break;
				}
				case "cyln":
					for (int i = 0; i < inner.Length; i++)
					{
						bool hot = i == PingPong(t * 8f, inner.Length);
						sb.Append("<color=").Append(hot ? "#FF2A2A" : col).Append('>').Append(inner[i]).Append("</color>");
					}
					break;
				case "lbl":
					sb.Append("<color=").Append(col).Append('>')
					  .Append(inner.Substring(0, Mathf.Clamp(PingPong(t * 5f, inner.Length + 1), 0, inner.Length)))
					  .Append("</color>");
					break;
				case "rain":
				case "rainbow":
				{
					int step = (int)(t * 10f);
					for (int i = 0; i < inner.Length; i++)
						sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(Mathf.Repeat((step + i) / 7f, 1f), 1f, 1f)))
						  .Append('>').Append(inner[i]).Append("</color>");
					break;
				}
				case "sr":
					for (int i = 0; i < inner.Length; i++)
						sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(Mathf.Repeat(t * 0.35f + i * 0.06f, 1f), 0.85f, 1f)))
						  .Append('>').Append(inner[i]).Append("</color>");
					break;
				case "pulse":
				case "wave":
					for (int i = 0; i < inner.Length; i++)
					{
						float k = Mathf.Max(0f, Mathf.Sin(t * 5f - i * 0.7f));
						sb.Append("<size=").Append(baseSize + (int)(k * baseSize * 0.6f)).Append("><color=")
						  .Append(col).Append('>').Append(inner[i]).Append("</color></size>");
					}
					break;
				case "jump":
					// IMGUI has no vertical offset: approximate the travelling hop with size.
					for (int i = 0; i < inner.Length; i++)
					{
						float k = Mathf.Max(0f, Mathf.Sin(t * 6f - i * 0.5f));
						sb.Append("<size=").Append(baseSize + (int)(k * baseSize * 0.35f)).Append("><color=")
						  .Append(col).Append('>').Append(inner[i]).Append("</color></size>");
					}
					break;
				case "shake":
					for (int i = 0; i < inner.Length; i++)
					{
						bool up = Mathf.Sin(t * 30f + i * 2.3f) > 0f;
						sb.Append("<size=").Append(up ? baseSize + 2 : baseSize - 1).Append("><color=")
						  .Append(col).Append('>').Append(inner[i]).Append("</color></size>");
					}
					break;
				case "gt":
				{
					float head = Mathf.Repeat(t * 6f, inner.Length + 4f);
					for (int i = 0; i < inner.Length; i++)
					{
						float d = Mathf.Clamp01(1f - Mathf.Abs(head - i) / 2.5f);
						byte v = (byte)Mathf.Lerp(255f, 60f, d);
						string hex = v.ToString("X2");
						sb.Append("<color=#").Append(hex).Append(hex).Append(hex).Append('>').Append(inner[i]).Append("</color>");
					}
					break;
				}
				case "blink":
					if (Mathf.Repeat(t, 0.8f) >= 0.4f) return "";   // off phase
					sb.Append("<color=").Append(col).Append('>').Append(inner).Append("</color>");
					break;
				case "glitch":
				{
					const string Junk = "!@#$%^&*?/|=+~";
					int bucket = (int)(t * 12f);
					for (int i = 0; i < inner.Length; i++)
					{
						int h = (bucket * 73856093) ^ (i * 19349663);
						int pick = (h & 0x7FFFFFFF) % Junk.Length;   // Abs(int.MinValue) throws
						if (((h >> 5) & 7) == 0) sb.Append("<color=#8CFF8C>").Append(Junk[pick]).Append("</color>");
						else sb.Append("<color=").Append(col).Append('>').Append(inner[i]).Append("</color>");
					}
					break;
				}
				case "grad":
					return Gradient(inner);

				case "glow":
				{
					float k = (Mathf.Sin(t * 3f) + 1f) * 0.5f;
					Color c = Color.Lerp(ParseColor(tag.Color, Color.white) * 0.6f, Color.white, k);
					sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>').Append(inner).Append("</color>");
					break;
				}
				default:
					sb.Append("<color=").Append(col).Append('>').Append(inner).Append("</color>");
					break;
			}

			string s = sb.ToString();
			if (tag.I) s = "<i>" + s + "</i>";
			if (tag.B) s = "<b>" + s + "</b>";
			return s;   // IMGUI has no <u>; underline shows on the nameplate plate only.
		}

		// Rich preview whose PLAIN text is clamped to maxTextLen BEFORE markup is composed,
		// so the result is always well-formed (never cut inside a <color>/<b>/<i>/<u> tag).
		public static string RichPreviewClamped(VaTag tag, int maxTextLen, bool imgui = true)
		{
			string txt = tag.Text ?? "";
			if (txt.Length > maxTextLen) txt = txt.Substring(0, Mathf.Max(1, maxTextLen - 1)) + "…";
			return Wrap(txt, tag, includeColor: true, imgui: imgui);
		}

		private static Color ParseColor(string hex, Color fallback)
			=> !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;

		// EVERY plate, not just the first (2026-09-13). This used to test Plates[0] alone, so a set
		// whose FIRST plate had been destroyed — VRChat rebuilding part of a nameplate — counted as
		// dead while plates 1..n were still parented and visible. PruneDeadSets then dropped the key
		// from _plates, the only ledger RemoveAllPlates() walks, and those plates floated above the
		// player for the rest of the session with tags switched off.
		private static bool SetAlive(PlateSet ps)
		{
			if (ps == null || ps.Plates.Count == 0) return false;
			for (int i = 0; i < ps.Plates.Count; i++)
			{
				try { if (ps.Plates[i].Go != null) return true; } catch { }
			}
			return false;
		}

		private void PruneDeadSets()
		{
			List<string> dead = null;
			foreach (var kv in _plates) if (!SetAlive(kv.Value)) (dead ??= new List<string>()).Add(kv.Key);
			if (dead == null) return;
			// DestroySet, not a bare Remove: a "dead" set can still hold live plates (see SetAlive),
			// and dropping the key without destroying them is exactly how plates get stranded.
			foreach (string uid in dead) DestroySet(uid);
		}

		private void DestroySet(string uid)
		{
			if (!_plates.TryGetValue(uid, out var set)) return;
			foreach (var p in set.Plates) { try { if (p.Go != null) UnityEngine.Object.Destroy(p.Go); } catch { } }
			_plates.Remove(uid);
		}

		private void RemoveAllPlates()
		{
			foreach (var set in _plates.Values)
				foreach (var p in set.Plates) { try { if (p.Go != null) UnityEngine.Object.Destroy(p.Go); } catch { } }
			_plates.Clear();
		}
	}
}
