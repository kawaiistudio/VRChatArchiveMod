using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Highlight ESP: glows the outline of each remote player's actual avatar mesh, plus the world's
	// portals and grabbable pickups, using VRChat's OWN highlight post-effect (HighlightsFX, the one
	// the game uses for "Hold to Grab").
	//
	// Why this beats a drawn box: the glow is the real geometry, so it follows the object's rotation,
	// pose and shape exactly instead of a screen-aligned rectangle that looks wrong the moment
	// someone turns. It is still pure visualisation of things your client already renders — nothing
	// is targeted, tracked, or sent anywhere.
	//
	// THREE INDEPENDENT SWITCHES (ESP/Highlight, ESP/Portals, ESP/Items) and one rule for all of
	// them: OFF MEANS OFF, THIS FRAME. Every renderer this module lights is remembered together with
	// the effect instance it was lit on, and a switch going off un-lights exactly that category
	// before anything else in the update can return early. That is the whole defence against the
	// "ghost glow" — a portal or a prop that stayed lit after its switch was turned off.
	//
	// Everything is reflection-resolved and guarded: on a build where the effect is missing the
	// module says so through FeatureHealth instead of throwing.
	public class HighlightEspModule : IModule
	{
		public override string Name => "HighlightEsp";

		private const int ScanIntervalFrames = 90;      // players: avatars swap, people come and go (~1.3 s at 70 fps)
		private const float WorldIntervalSec = 4f;      // pickups and portals: two FindObjectsOfType calls + liveness sweep
		private static float _nextSlowLog;              // rate limit for the "pass took N ms" warning
		private const int MaxRenderersPerPlayer = 24;   // avatars can have dozens; cap the cost
		private const int MaxRenderersPerObject = 8;

		private static readonly Color PortalGlow = new Color(0.66f, 0.36f, 1f);
		private static readonly Color ItemGlow = new Color(1f, 0.72f, 0.25f);

		// ---- the effect (static: CapsuleEspModule lights its capsules through the same path)
		private static Type _fxType;

		// For EspCameraGuard: the HighlightsFX il2cpp type (to find copies on other cameras) and the
		// pointer of the instance THIS module lights through (never to be switched off).
		internal static Il2CppSystem.Type FxIl2 { get { try { return _fxType != null ? Il2CppType.From(_fxType) : null; } catch { return null; } } }
		internal static IntPtr FxPtr { get { try { return _fx is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b ? b.Pointer : IntPtr.Zero; } catch { return IntPtr.Zero; } } }
		private static MethodInfo _addWithColor;   // (Renderer, Color, bool)
		private static MethodInfo _removeRenderer; // a method whose NAME says Remove — (Renderer, bool) or (Renderer); may stay null
		private static bool _resolved;
		private static object _fx;                 // the live HighlightsFX instance
		private static string _how = "?";          // which lookup found it, for diagnosing other machines

		// ONE LIT THING: the renderers we registered and the effect instance they were registered
		// with. A world change can replace the effect; un-lighting has to talk to the instance that
		// actually holds the renderer, not to whichever one happens to be current.
		private sealed class Lit
		{
			public object Fx;
			public Transform Root;                                     // the object we glow
			public readonly List<Renderer> Renderers = new List<Renderer>();
		}

		// Three tables, one per switch, so a switch only ever clears its own entries and the health
		// report counts each category on its own (portals used to be counted as items).
		private readonly Dictionary<string, Lit> _players = new Dictionary<string, Lit>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<int, Lit> _portals = new Dictionary<int, Lit>();
		private readonly Dictionary<int, Lit> _items = new Dictionary<int, Lit>();

		private int _frame;
		private float _nextWorld;                                  // realtime of the next world pass
		private bool _wasPlayers, _wasPortals, _wasItems;          // last frame's switch states

		public override void OnInitialize()
		{
			try
			{
				if (ModConfig.EspHighlight.Value || ModConfig.EspPortals.Value || ModConfig.EspItems.Value)
					VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] armed — glows via VRChat's own HighlightsFX.");
			}
			catch { }
		}

		public override void OnUpdate()
		{
			try
			{
				bool wantPlayers = false, wantPortals = false, wantItems = false;
				try { wantPlayers = ModConfig.EspHighlight.Value; } catch { }
				try { wantPortals = ModConfig.EspPortals.Value; } catch { }
				try { wantItems = ModConfig.EspItems.Value; } catch { }

				// OFF FIRST, AND BEFORE ANY EARLY RETURN. This used to sit below the Resolve() gate,
				// so while the effect was momentarily unavailable the clear never ran and the glow
				// stayed — a switch reading OFF over a portal still lit. Each category clears only
				// its own entries, on the very frame its switch flips, and on every later frame it
				// is off and still holds something (a removal that failed once is retried).
				if (!wantPlayers) { if (_players.Count > 0) ClearPlayers(); FeatureHealth.Idle("ESP/Highlight", "off"); }
				if (!wantPortals) { if (_portals.Count > 0) ClearWorld(_portals); FeatureHealth.Idle("ESP/Portals", "off"); }
				if (!wantItems) { if (_items.Count > 0) ClearWorld(_items); FeatureHealth.Idle("ESP/Items", "off"); }

				// AN ENABLE MUST BE INSTANT TOO. Flipping a glow on used to do nothing until the next
				// 45-frame tick (and up to the world interval), which reads as a dead switch. On an
				// off->on edge the relevant pass runs this frame.
				bool edgePlayers = wantPlayers && !_wasPlayers;
				bool edgeWorld = (wantPortals && !_wasPortals) || (wantItems && !_wasItems);
				_wasPlayers = wantPlayers; _wasPortals = wantPortals; _wasItems = wantItems;

				// Glows on: keep them out of your own camera / mirrors (rate-limited inside).
				if (wantPlayers || wantPortals || wantItems)
				{
					long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
					EspCameraGuard.Tick();
					double msG = (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
					if (msG > 8.0 && VaClock.Now >= _nextSlowLog)
					{
						_nextSlowLog = VaClock.Now + 10f;
						VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] EspCameraGuard.Tick took {msG:0.0} ms this frame.");
					}
				}

				if (!wantPlayers && !wantPortals && !wantItems) return;

				// THE ANSWER TO "I TURNED IT ON AND NOTHING HAPPENED". Without HighlightsFX every call
				// below succeeds and draws nothing, which is exactly the failure that reads as a
				// broken toggle. Said once, in a sentence the client can put under the switch.
				long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
				bool resolved = Resolve();
				double msR = (System.Diagnostics.Stopwatch.GetTimestamp() - r0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				if (msR > 8.0 && VaClock.Now >= _nextSlowLog)
				{
					_nextSlowLog = VaClock.Now + 10f;
					VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] Resolve() took {msR:0.0} ms this frame (fx {(resolved ? "ok" : "missing")}, via {_how}).");
				}
				if (!resolved)
				{
					const string why = "on, but VRChat's highlight effect is not available yet — nothing will glow";
					if (wantPlayers) FeatureHealth.Broken("ESP/Highlight", why);
					if (wantPortals) FeatureHealth.Broken("ESP/Portals", why);
					if (wantItems) FeatureHealth.Broken("ESP/Items", why);
					return;
				}

				if (edgePlayers) _frame = ScanIntervalFrames;
				if (edgeWorld) { _nextWorld = 0f; _frame = ScanIntervalFrames; }
				if (edgeWorld) RequestItemEnumerate();
				if (wantItems) ItemStep();

				if (++_frame < ScanIntervalFrames) return;
				_frame = 0;

				if (wantPlayers) PlayerPass();   // times and reports itself when it costs more than 20 ms

				// WORLD PASS ON A REAL CLOCK, not the player frame counter (that once made "every 600"
				// mean every 600*45 frames — seven minutes). Pickups and portals change on a human
				// timescale; the scan is two FindObjectsOfType calls plus a liveness sweep of what glows.
				float rt = VaClock.Now;
				if ((wantPortals || wantItems) && rt >= _nextWorld)
				{
					_nextWorld = rt + WorldIntervalSec;
					long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
					WorldPass(wantPortals, wantItems);
					double msWorld = (System.Diagnostics.Stopwatch.GetTimestamp() - w0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
					if (msWorld > 20.0 && VaClock.Now >= _nextSlowLog)
					{
						_nextSlowLog = VaClock.Now + 10f;
						VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] world pass took {msWorld:0} ms ({_items.Count} pickup(s), {_portals.Count} portal(s) glowing).");
					}
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[HighlightEsp] update threw: {e}"); }
		}

		public override void OnShutdown() => ClearAll();

		// A new world: un-light everything we can still reach (best effort — the old scene may be
		// half gone already), THEN forget it, THEN drop the effect. The order matters: forgetting
		// first would leave the old effect holding our renderers with nobody left who knows.
		//
		// _fx is a plain object reference, so it stays non-null after Unity destroys the thing
		// behind it — and Resolve() would answer "already have it" on that basis. Dropping it here
		// forces an honest re-resolve in the new world. (CapsuleEspModule is registered BEFORE this
		// module so its own OnSceneLoaded still finds the effect alive.)
		public override void OnSceneLoaded(int buildIndex)
		{
			try { ClearAll(); } catch { }
			_players.Clear();
			_portals.Clear();
			_items.Clear();
			_uidCache.Clear();
			_nextWorld = 0f;               // force a world pass immediately in the new world
			_frame = ScanIntervalFrames;

			_fx = null;
			_how = null;
			_nextFxScan = 0f;
			_firstFxTry = 0f;
			_warnedNoFx = false;
		}

		// ------------------------------------------------------------------ players

		// WHY THE STABLE PATH MUST BE FREE. Every AddRenderer / RemoveRenderer on VRChat's HighlightsFX
		// costs about half a millisecond (the 2026-09-04 log: 10 pickups lit in 6.3 ms). Relighting one
		// avatar is 24 renderers un-lit twice and lit once — ~36 ms — and the old pass relit an avatar
		// whenever ANY of its stored renderers had died, which an avatar that spawns and destroys a
		// prop does forever: that was the 30-120 ms hitch every 90 frames with three avatars. Now a
		// dead renderer is simply forgotten and the avatar is relit only when they ALL died (a swap).
		private static readonly Dictionary<int, string> _uidCache = new Dictionary<int, string>();
		private static float _nextPlayerCostLog;

		private void PlayerPass()
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Transform localT = PlayerRef.LocalTransform();
			int kept = 0, relit = 0, relitRenderers = 0, pruned = 0;
			foreach (object player in FewTagsModule.EnumeratePlayers())
			{
				try
				{
					var comp = player as Component;
					if (comp == null || !NativeGuard.Alive(comp)) continue;

					// skip ourselves — highlighting your own avatar just fogs your view
					if (localT != null && comp.transform == localT) continue;

					string uid = UidOf(player, comp);
					seen.Add(uid);
					if (_players.TryGetValue(uid, out var had))
					{
						int before = had.Renderers.Count;
						int live = PruneDead(had);
						pruned += before - live;
						if (live > 0) { kept++; continue; }   // still glowing: nothing to do
						DropPlayer(uid);                      // every renderer died: the avatar was swapped
					}

					object apiUser = FewTagsModule.GetMemberByTypeName(player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
					Color col = TrustKit.ColorOf(apiUser);

					var lit = new Lit { Fx = _fx, Root = comp.transform };
					var renderers = comp.GetComponentsInChildren<Renderer>(false);
					if (renderers != null)
					{
						for (int i = 0; i < renderers.Length && lit.Renderers.Count < MaxRenderersPerPlayer; i++)
						{
							var rnd = renderers[i];
							if (rnd == null) continue;
							try { _addWithColor.Invoke(_fx, new object[] { rnd, col, true }); lit.Renderers.Add(rnd); }
							catch { }
						}
					}
					if (lit.Renderers.Count > 0) { _players[uid] = lit; relit++; relitRenderers += lit.Renderers.Count; }
				}
				catch { }
			}

			// players who left: drop their highlights
			List<string> gone = null;
			foreach (var kv in _players) if (!seen.Contains(kv.Key)) (gone ??= new List<string>()).Add(kv.Key);
			if (gone != null) foreach (string uid in gone) DropPlayer(uid);

			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (ms > 20.0 && VaClock.Now >= _nextPlayerCostLog)
			{
				_nextPlayerCostLog = VaClock.Now + 10f;
				VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] player pass {ms:0} ms: {kept} avatar(s) kept, {relit} relit ({relitRenderers} renderers), {pruned} dead renderer(s) forgotten, {(gone != null ? gone.Count : 0)} left.");
			}

			FeatureHealth.Ok("ESP/Highlight", _players.Count == 0
				? "on — no other players to outline"
				: "on — " + _players.Count + " avatar(s) outlined");
		}

		// The user id behind a player object, read through reflection ONCE per player object (the
		// instance id is stable for as long as the object lives, and Unity never reuses one).
		private static string UidOf(object player, Component comp)
		{
			int iid;
			try { iid = comp.GetInstanceID(); } catch { return FewTagsModule.UserIdOf(player) ?? "?"; }
			if (_uidCache.TryGetValue(iid, out string cached)) return cached;
			string uid = FewTagsModule.UserIdOf(player);
			if (string.IsNullOrEmpty(uid)) return iid.ToString();   // not resolvable yet: try again next pass
			if (_uidCache.Count > 512) _uidCache.Clear();
			_uidCache[iid] = uid;
			return uid;
		}

		private void DropPlayer(string uid)
		{
			if (!_players.TryGetValue(uid, out var lit)) return;
			_players.Remove(uid);
			UnlightAll(lit);
		}

		// Just the avatar glow, leaving portals and pickups alone.
		// Same entry point for the highlight pass, and it pokes the capsule one too: the menus treat
		// them as one ESP, so a setting change has to refresh both or the two disagree on screen.
		public static void TriggerRelight()
		{
			try
			{
				var mod = ModuleManager.Get<HighlightEspModule>();
				if (mod != null)
				{
					mod.ClearPlayers();
					mod.ClearWorld(mod._portals);
					mod.ClearWorld(mod._items);
					mod._frame = ScanIntervalFrames;
					mod._nextWorld = 0f;
				}
				CapsuleEspModule.TriggerRelight();
			}
			catch { }
		}

		private void ClearPlayers()
		{
			var keys = new List<string>(_players.Keys);
			foreach (string uid in keys) { try { DropPlayer(uid); } catch { } }
			_players.Clear();
		}

		// ------------------------------------------------------------------ portals and pickups
		//
		// The same glow, on the object itself. These used to be screen-space diamonds with a name
		// and a distance: in a real world that is dozens of labels stacked over each other and over
		// the players. Lighting the object's own geometry says "that thing, there" with no text at
		// all, and it is exactly what VRChat itself does for a grabbable ("Hold to Grab").

		private static Il2CppSystem.Type _pickupIl2, _rigidbodyIl2;

		private void WorldPass(bool wantPortals, bool wantItems)
		{
			try
			{
				// LIVENESS FIRST, independent of what the scan finds: anything destroyed, deactivated,
				// or whose renderers died is un-lit and forgotten before the scan can "see" it again.
				Validate(_portals);

				if (wantPortals) PortalPass();   // items run incrementally from ItemStep every frame
			}
			catch (Exception e) { Warn("world", "world pass: " + e.Message); }
		}

		// ---- ITEMS, INCREMENTAL. The old pass enumerated every pickup, walked its renderers, lit them
		// and swept them for liveness in ONE go: 2.9 s per pass in a 1000-pickup world, 7 fps. Nothing
		// about "nearest N" — the owner wants every pickup marked; the fix is the technique. Now the
		// pickups are enumerated every ItemEnumerateSec (they do not appear or vanish often), and the
		// lighting and the liveness checks are spread over frames, a slice each, so the cost per frame
		// is bounded and the same 1000 pickups light up over about a second with no spike.
		private const float ItemEnumerateSec = 10f;

		// ADAPTIVE ENUMERATE INTERVAL (2026-09-13). ItemEnumerate() is the one unbudgetable burst in
		// this module: FindObjectsByType walks the entire scene in a single frame — 113 ms measured,
		// the spike behind "[HighlightEsp] item step 119.0 ms". It cannot be sliced (the walk is
		// atomic), but it CAN run less often: a world's set of pickups almost never changes once it
		// has loaded. So each pass fingerprints what it found (count + order-free XOR of instance
		// ids, so an unsorted walk still compares equal) and, when the set is identical to last
		// time, the wait before the next walk doubles — 10 s, 20 s, 40 s, capped at 60 s. Any change
		// snaps it back to 10 s, and an explicit request (toggle on, scene load) resets it too. In a
		// stable world that is six times fewer 113 ms frames, and nothing anyone sees is different:
		// a pickup that does appear is picked up on the next walk exactly as before.
		private const float ItemEnumerateMaxSec = 60f;
		private static float _itemEnumInterval = ItemEnumerateSec;
		private static long _itemEnumSig = long.MinValue;
		// VALIDATE IS THE EXPENSIVE HALF. IsLive = a VirtualQuery per renderer of the pickup (PruneDead),
		// ~0.9 ms per item: 10 per frame, every frame, was 9.4 ms/frame = 552 ms/s on Murder 4 (the mod's
		// own [Perf] line, owner at 20 fps). Pickups do not die 60 times a second — 2 items every 0.1 s
		// cycles a 50-pickup world in ~2.5 s and costs ~18 ms/s, and a glow outliving its pickup by two
		// seconds is invisible (the renderer is gone).
		private const int ItemLightPerFrame = 6, ItemValidatePerFrame = 2;   // 6 pickups ≈ 3 ms: the FX add is ~0.5 ms each
		private const float ItemValidateEverySec = 0.1f;
		private static float _nextItemValidate;
		private static readonly List<Transform> _itemCands = new List<Transform>();
		private static readonly HashSet<int> _itemEnumIds = new HashSet<int>();
		private static int _itemCursor = int.MaxValue, _itemValidateCursor;
		private static float _nextItemEnumerate;
		private static List<int> _itemKeys;
		private static bool _itemReport;

		// An explicit request (toggle on, scene load) also drops the adaptive back-off: a new world
		// or a fresh enable must be walked at the fast cadence, not inherit a 60 s wait.
		internal static void RequestItemEnumerate()
		{
			_nextItemEnumerate = 0f;
			_itemEnumInterval = ItemEnumerateSec;
			_itemEnumSig = long.MinValue;
		}

		private static float _nextItemCostLog;
		private void ItemStep()
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			float now = VaClock.Now;
			// Interval is adaptive (see _itemEnumInterval): 10 s while the set keeps changing, up to
			// 60 s once it has settled, so the full-scene walk stops landing every ten seconds.
			if (now >= _nextItemEnumerate) { _nextItemEnumerate = now + _itemEnumInterval; ItemEnumerate(); }
			long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

			// light a slice of the candidates
			int n = 0, litNow = 0;
			while (_itemCursor < _itemCands.Count && n < ItemLightPerFrame)
			{
				var t = _itemCands[_itemCursor++]; n++;
				try
				{
					if (t == null || !NativeGuard.Alive(t)) continue;
					if (!t.gameObject.activeInHierarchy) continue;
					int before = _items.Count;
					Glow(_items, t, ItemGlow, _itemEnumIds);
					if (_items.Count > before) litNow++;
				}
				catch { }
			}
			if (_itemCursor >= _itemCands.Count && _itemReport)
			{
				_itemReport = false;
				FeatureHealth.Ok("ESP/Items", _items.Count == 0
					? "on — no grabbable pickups in this world"
					: "on — " + _items.Count + " pickup(s) glowing");
			}
			long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

			// validate a slice of what glows (destroyed / deactivated / dead renderers -> un-lit) —
			// rate-limited to ItemValidateEverySec, not every frame (see ItemValidatePerFrame).
			int v = 0;
			if (_items.Count > 0 && now >= _nextItemValidate)
			{
				_nextItemValidate = now + ItemValidateEverySec;
				if (_itemKeys == null || _itemValidateCursor >= _itemKeys.Count) { _itemKeys = new List<int>(_items.Keys); _itemValidateCursor = 0; }
				while (_itemValidateCursor < _itemKeys.Count && v < ItemValidatePerFrame)
				{
					int id = _itemKeys[_itemValidateCursor++]; v++;
					if (_items.TryGetValue(id, out var lit) && !IsLive(lit)) DropWorld(_items, id);
				}
			}
			long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

			// COST BREAKDOWN when a frame is expensive: the profiler blamed this module for 700 ms/s with
			// 62 pickups lit and no pass above 30 ms, so the per-frame work is the suspect and this names
			// the step (enumerate / light / validate) and its sizes.
			double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			double msE = (t1 - t0) * f, msL = (t2 - t1) * f, msV = (t3 - t2) * f;
			if (msE + msL + msV > 8.0 && now >= _nextItemCostLog)
			{
				_nextItemCostLog = now + 5f;
				VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] item step {msE + msL + msV:0.0} ms this frame: enumerate {msE:0.0} ms, light {msL:0.0} ms ({n} candidate(s) seen, {litNow} newly lit, cursor {_itemCursor}/{_itemCands.Count}), validate {msV:0.0} ms ({v} checked, {_items.Count} glowing).");
			}
		}

		private void ItemEnumerate()
		{
			_itemCands.Clear(); _itemEnumIds.Clear();
			try
			{
				// NON-GENERIC on purpose: FindObjectsOfType<VRC_Pickup>() returns NOTHING on this
				// build (Il2CppInterop's generic type resolution misses the class — the same gap that
				// had Force Pickup unlocking 0). Resolve the il2cpp type once and TryCast each result.
				if (_pickupIl2 == null) _pickupIl2 = Il2CppType.Of<VRC_Pickup>();
				// UNSORTED. FindObjectsOfType sorts its result by instance id; FindObjectsByType(None) is the
				// same walk without the sort, and the 2026-09-04 log put the sorted call at 70 ms for 63 pickups.
				var found = VRChatArchiveMod.Core.Live.AllOfType(_pickupIl2, FindObjectsSortMode.None);
				if (found != null)
				{
					for (int i = 0; i < found.Length; i++)
					{
						var p = found[i] != null ? found[i].TryCast<VRC_Pickup>() : null;
						if (p == null || !NativeGuard.Alive(p)) continue;
						Transform t;
						try { t = p.transform; } catch { continue; }
						if (t == null) continue;
						// YOUR OWN CAMERA IS A PICKUP. VRChat's handheld camera (UserCamera / PhotoCamera and
						// its viewfinder) is a VRC_Pickup, so it lit up yellow in your hand and in every photo
						// frame. Anything named like a camera, itself or in its parents, is never marked.
						if (IsCameraObject(t)) continue;
						_itemCands.Add(t);
						try { _itemEnumIds.Add(t.GetInstanceID()); } catch { }
					}
				}
			}
			catch (Exception e) { Warn("items", "pickup scan: " + e.Message); }
			// anything lit that the world no longer has stops glowing
			DropUnseen(_items, _itemEnumIds);
			_itemCursor = 0; _itemReport = true;

			// FINGERPRINT THE SET AND PACE THE NEXT WALK. XOR is order-independent, so an unsorted
			// FindObjectsByType that hands the same pickups back in a different order still matches.
			int x = 0;
			foreach (int id in _itemEnumIds) x ^= id;
			long sig = ((long)_itemEnumIds.Count << 32) ^ (uint)x;
			if (sig == _itemEnumSig)
				_itemEnumInterval = Math.Min(_itemEnumInterval * 2f, ItemEnumerateMaxSec);   // settled: back off
			else
				_itemEnumInterval = ItemEnumerateSec;                                           // changed: watch closely
			_itemEnumSig = sig;
		}

		// VRChat's own camera rig: "UserCamera", "PhotoCamera", "ViewFinder", "CameraObject"… — checked on
		// the pickup and up to 6 parents, case-insensitively on "camera" / "viewfinder".
		private static bool IsCameraObject(Transform t)
		{
			try
			{
				int up = 0;
				for (Transform p = t; p != null && up < 6; p = p.parent, up++)
				{
					string n; try { n = p.name ?? ""; } catch { break; }
					if (n.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.IndexOf("viewfinder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
		}

		private void PortalPass()
		{
			var type = ResolvePortalType();
			if (type == null || _portalIl2 == null)
			{
				// NO NAME MATCHING. The old rule — any Transform called *Portal* — is what kept a
				// chain prop named "PortalDrop_Chain" lit forever with no portal in the world. Without
				// the component type there is nothing honest to mark, so nothing is marked, and the
				// report says so instead of guessing.
				if (_portals.Count > 0) ClearWorld(_portals);
				FeatureHealth.Broken("ESP/Portals", "portal component not found on this build — nothing marked");
				return;
			}

			var seen = new HashSet<int>();
			try
			{
				var found = VRChatArchiveMod.Core.Live.AllOfType(_portalIl2);
				if (found != null)
				{
					for (int i = 0; i < found.Length; i++)
					{
						var o = found[i];
						if (o == null || !NativeGuard.Alive(o)) continue;
						var comp = o.TryCast<Component>();
						if (comp == null) continue;
						Transform t;
						try { t = comp.transform; } catch { continue; }
						if (t == null) continue;
						// A portal is never a physics prop or a pickup. Anything that is one, or sits
						// under one, is a world object wearing the component and is left alone.
						if (HasPickupOrRigidbodyAbove(t)) continue;
						Glow(_portals, t, PortalGlow, seen);
					}
				}
			}
			catch (Exception e) { Warn("portals", "portal scan: " + e.Message); }

			DropUnseen(_portals, seen);
			FeatureHealth.Ok("ESP/Portals", _portals.Count == 0
				? "on — no portals right now"
				: "on — " + _portals.Count + " portal(s) glowing");
		}

		private static bool HasPickupOrRigidbodyAbove(Transform t)
		{
			try
			{
				if (_rigidbodyIl2 == null) _rigidbodyIl2 = Il2CppType.Of<Rigidbody>();
				if (_rigidbodyIl2 != null && t.GetComponentInParentSafe(_rigidbodyIl2) != null) return true;
				if (_pickupIl2 == null) _pickupIl2 = Il2CppType.Of<VRC_Pickup>();
				if (_pickupIl2 != null && t.GetComponentInParentSafe(_pickupIl2) != null) return true;
			}
			catch { }
			return false;
		}

		// THE PORTAL IS THE COMPONENT, not a name. VRChat's portal prefab carries PortalInternal (a
		// dynamic variant exists on some builds); it is looked up once, Assembly-CSharp first and
		// then every loaded assembly, and never guessed at from an object's name.
		private static Type _portalType;
		private static Il2CppSystem.Type _portalIl2;
		private static bool _portalResolved;

		private static Type ResolvePortalType()
		{
			if (_portalResolved) return _portalType;
			_portalResolved = true;
			string[] names = { "PortalInternal", "VRC.PortalInternal", "PortalInternalDynamic" };
			string how = null;
			try
			{
				Assembly asm = null;
				try { asm = Assembly.Load("Assembly-CSharp"); } catch { }
				if (asm != null)
				{
					foreach (var n in names)
					{
						Type t = null;
						try { t = asm.GetType(n); } catch { }
						if (t != null) { _portalType = t; how = "Assembly-CSharp " + n; break; }
					}
				}
				if (_portalType == null)
				{
					foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
					{
						Type[] types;
						try { types = a.GetTypes(); }
						catch (ReflectionTypeLoadException e) { types = e.Types; }
						catch { continue; }
						if (types == null) continue;
						foreach (var t in types)
						{
							if (t == null || t.Name != "PortalInternal") continue;
							_portalType = t; how = a.GetName().Name + " " + (t.FullName ?? t.Name); break;
						}
						if (_portalType != null) break;
					}
				}
				if (_portalType != null)
				{
					try { _portalIl2 = Il2CppType.From(_portalType); }
					catch (Exception e)
					{
						VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] portals: " + how + " is not an il2cpp type (" + e.Message + ") — portal glow marks nothing.");
						_portalType = null; _portalIl2 = null;
					}
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] portals: type lookup threw: " + e.Message); }

			if (_portalType != null)
				VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] portals: component type resolved (" + how + ").");
			else
				VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] portals: no PortalInternal / VRC.PortalInternal / PortalInternalDynamic type on this build — portal glow marks nothing (no name matching).");
			return _portalType;
		}

		private void Glow(Dictionary<int, Lit> dict, Transform t, Color col, HashSet<int> seen)
		{
			try
			{
				if (t == null) return;
				int id = t.GetInstanceID();
				if (dict.ContainsKey(id)) { seen.Add(id); return; }      // already glowing, validated live this pass
				try { if (!t.gameObject.activeInHierarchy) return; } catch { return; }

				var lit = new Lit { Fx = _fx, Root = t };
				var rs = t.GetComponentsInChildren<Renderer>(false);
				if (rs == null) return;
				for (int i = 0; i < rs.Length && lit.Renderers.Count < MaxRenderersPerObject; i++)
				{
					var rnd = rs[i];
					if (rnd == null) continue;
					try { _addWithColor.Invoke(_fx, new object[] { rnd, col, true }); lit.Renderers.Add(rnd); }
					catch { }
				}
				if (lit.Renderers.Count > 0) { dict[id] = lit; seen.Add(id); }
			}
			catch { }
		}

		// Anything that left the world (a portal closed, a pickup destroyed) stops glowing.
		private void DropUnseen(Dictionary<int, Lit> dict, HashSet<int> seen)
		{
			if (dict.Count == 0) return;
			List<int> gone = null;
			foreach (var kv in dict) if (!seen.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
			if (gone != null) foreach (int id in gone) DropWorld(dict, id);
		}

		// Every pass, regardless of the scan: an entry whose object is destroyed, inactive, or whose
		// renderers died is un-lit and forgotten. A stale entry is exactly how a ghost glow survives.
		private void Validate(Dictionary<int, Lit> dict)
		{
			if (dict.Count == 0) return;
			List<int> gone = null;
			foreach (var kv in dict) if (!IsLive(kv.Value)) (gone ??= new List<int>()).Add(kv.Key);
			if (gone != null) foreach (int id in gone) DropWorld(dict, id);
		}

		private static bool IsLive(Lit lit)
		{
			try
			{
				if (lit == null) return false;
				var t = lit.Root;
				if (!NativeGuard.Alive(t) || t == null) return false;
				if (!t.gameObject.activeInHierarchy) return false;
				return PruneDead(lit) > 0;
			}
			catch { return false; }
		}

		// Forgets the renderers that died (a prop the avatar destroyed, a pickup that lost a part) and
		// answers how many are still there. Nothing is un-lit for a dead renderer: there is nothing
		// left to un-light, and touching it is what the guard exists to prevent.
		private static int PruneDead(Lit lit)
		{
			if (lit == null) return 0;
			var list = lit.Renderers;
			// ONE PROBE BEFORE THE SWEEP. Every Alive() is two VirtualQuery syscalls (~0.1 ms here), and the
			// sweep is 24 of them per avatar and 8 per pickup on EVERY pass — ~100 ms per player pass in a
			// full instance, and most of the validate cost per item. A swap or a destroy takes every renderer
			// of the object down at once, so while the first stored renderer is still there the object still
			// is: answer without sweeping. A prop that died further down the list stays listed until the
			// probe dies — nothing reads it (Unhighlight guards each renderer on its own).
			if (list.Count > 0)
			{
				bool probeLive = false;
				try { var r0 = list[0]; probeLive = NativeGuard.Alive(r0) && r0 != null; } catch { }
				if (probeLive) return list.Count;
			}
			for (int i = list.Count - 1; i >= 0; i--)
			{
				bool dead;
				try { var r = list[i]; dead = !NativeGuard.Alive(r) || r == null; }
				catch { dead = true; }
				if (dead) list.RemoveAt(i);
			}
			return list.Count;
		}

		private void DropWorld(Dictionary<int, Lit> dict, int id)
		{
			if (!dict.TryGetValue(id, out var lit)) return;
			dict.Remove(id);
			UnlightAll(lit);
		}

		private void ClearWorld(Dictionary<int, Lit> dict)
		{
			var ids = new List<int>(dict.Keys);
			foreach (int id in ids) { try { DropWorld(dict, id); } catch { } }
			dict.Clear();
		}

		// EVERYTHING this module ever lit, players AND world.
		private void ClearAll()
		{
			ClearPlayers();
			ClearWorld(_portals);
			ClearWorld(_items);
		}

		private static void UnlightAll(Lit lit)
		{
			if (lit == null) return;
			for (int i = 0; i < lit.Renderers.Count; i++)
			{
				try { Unhighlight(lit.Renderers[i], lit.Fx); } catch { }
			}
		}

		// ------------------------------------------------------------------ the effect
		//
		// Shared with CapsuleEspModule, which lights an invisible capsule mesh of its own. The
		// resolution of HighlightsFX and its obfuscated methods lives here and only here.

		private static bool _litOnce;
		private static string _lastBlame;
		private static void Blame(string why)
		{
			if (why == _lastBlame) return;
			_lastBlame = why;
			// Said once per distinct reason: this runs per renderer per frame, so anything louder
			// would bury the log. It is the difference between "the glow is off" and "the glow tried
			// and failed", which need different fixes.
			VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] glow not applied: " + why + ".");
		}

		private static readonly Dictionary<string, string> _lastWarn = new Dictionary<string, string>(StringComparer.Ordinal);
		private static void Warn(string key, string msg)
		{
			if (_lastWarn.TryGetValue(key, out var prev) && prev == msg) return;
			_lastWarn[key] = msg;
			VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] " + msg);
		}

		// Is the effect there right now? (CapsuleEspModule's health report asks.)
		internal static bool EffectReady()
		{
			try { return Resolve(); } catch { return false; }
		}

		public static bool Highlight(Renderer rnd, Color col) => Highlight(rnd, col, out _);

		// litWith receives the effect instance the renderer was registered with, so the caller can
		// hand it back to Unhighlight() and reach the right instance even after the effect changed.
		public static bool Highlight(Renderer rnd, Color col, out object litWith)
		{
			litWith = null;
			try
			{
				if (rnd == null) return false;
				if (!Resolve()) { Blame("HighlightsFX did not resolve"); return false; }
				_addWithColor.Invoke(_fx, new object[] { rnd, col, true });
				litWith = _fx;
				if (!_litOnce) { _litOnce = true; VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] first renderer lit — the glow path works."); }
				return true;
			}
			catch (Exception e) { Blame("add-renderer threw: " + Core.Unwrap.Describe(e)); return false; }
		}

		public static void Unhighlight(Renderer rnd) => Unhighlight(rnd, null);

		// UNCONDITIONAL. Every removal path the effect offers is tried, each on its own guard,
		// against the instance the renderer was lit on (when it is still alive) — and against the
		// current instance as well when that is a different, live one. If lighting worked,
		// un-lighting works too: the fallback is the very call that lit it, with the effect's
		// on/off flag set to FALSE.
		public static void Unhighlight(Renderer rnd, object litWith)
		{
			try
			{
				if (!NativeGuard.Alive(rnd) || rnd == null) return;   // destroyed: nothing left to glow
				if (_fx == null) { try { Resolve(); } catch { } }
				object primary = FxAlive(litWith) ? litWith : null;
				object current = FxAlive(_fx) ? _fx : null;
				if (primary != null) UnhighlightOn(primary, rnd);
				if (current != null && !SameFx(current, primary)) UnhighlightOn(current, rnd);
			}
			catch { }
		}

		private static void UnhighlightOn(object fx, Renderer rnd)
		{
			if (_removeRenderer != null)
			{
				try
				{
					var ps = _removeRenderer.GetParameters();
					_removeRenderer.Invoke(fx, ps.Length == 2 ? new object[] { rnd, false } : new object[] { rnd });
				}
				catch { }
			}
			if (_addWithColor != null)
			{
				try { _addWithColor.Invoke(fx, new object[] { rnd, Color.clear, false }); } catch { }
			}
		}

		// A CACHED EFFECT IS ONLY GOOD WHILE IT IS ALIVE. Unity destroying it does not null the
		// reference, so the liveness is asked of the object rather than of the pointer.
		private static bool FxAlive(object fx)
		{
			if (fx == null) return false;
			try
			{
				var b = fx as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (b == null || !NativeGuard.Alive(b)) return false;
				var beh = b.TryCast<Behaviour>();
				return beh != null;   // Unity's ==: a destroyed object compares equal to null
			}
			catch { return false; }
		}

		private static bool SameFx(object a, object b)
		{
			if (ReferenceEquals(a, b)) return true;
			if (a == null || b == null) return false;
			try
			{
				var x = a as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				var y = b as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				return x != null && y != null && x.Pointer == y.Pointer;
			}
			catch { return false; }
		}

		// HighlightsFX: static instance getter first, then the one on the main camera,
		// then a scene scan; as a last resort we attach HighlightsFXStandalone ourselves
		// (the approach other clients use).
		// Rate-limit + grace period for the instance hunt.
		private static float _nextFxScan, _firstFxTry;
		private static bool _warnedNoFx;
		private const float GiveUpAfter = 30f;

		// THE EFFECT'S LIVENESS IS ASKED A FEW TIMES A SECOND, NOT EVERY CALL. FxAlive is a VirtualQuery
		// pair plus a TryCast (a fresh proxy and a GC handle) plus Unity's null compare, and Resolve()
		// runs on every OnUpdate here and once per rainbow capsule per frame in CapsuleEsp. The effect
		// only changes on a world change, where OnSceneLoaded already drops _fx; between checks the
		// proxy's own handle keeps the object readable, so a stale answer costs at most one add call the
		// effect refuses (caught) — never a bad read. Same rhythm as PlayerRef.RevalidateSec.
		private static float _fxCheckedAt;
		private const float FxRevalidateSec = 0.25f;
		private static bool Resolve()
		{
			if (_fx != null && _addWithColor != null)
			{
				float tNow = VaClock.Now;
				if (tNow - _fxCheckedAt < FxRevalidateSec) return true;   // trust the last answer between checks
				_fxCheckedAt = tNow;
				if (FxAlive(_fx)) return true;
				_fx = null;   // destroyed or no longer castable: hunt for it again
			}
			if (!_resolved)
			{
				_resolved = true;
				try
				{
					var asm = Assembly.Load("Assembly-CSharp");
					_fxType = asm.GetType("HighlightsFX");
					if (_fxType == null) { VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] HighlightsFX type not found on this build."); return false; }

					var methods = _fxType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					foreach (var m in methods)
					{
						var ps = m.GetParameters();
						if (_addWithColor == null && ps.Length == 3
							&& ps[0].ParameterType.Name == "Renderer" && ps[1].ParameterType.Name == "Color" && ps[2].ParameterType == typeof(bool))
							_addWithColor = m;
					}

					// THE REMOVE METHOD IS BOUND BY NAME OR NOT AT ALL. This used to fall back to
					// "any void method taking one Renderer", which on an obfuscated build is as
					// likely to be an Add overload as a Remove — and re-lighting a renderer from
					// inside Unhighlight() is the one ghost glow no amount of clearing can fix.
					// Without a Remove-named method the un-light goes through the add call with its
					// on/off flag FALSE, which is the same path that lit it.
					foreach (var m in methods)
					{
						if (m.ReturnType != typeof(void)) continue;
						if (m.Name.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) < 0) continue;
						var ps = m.GetParameters();
						if (ps.Length == 0 || ps[0].ParameterType.Name != "Renderer") continue;
						if (ps.Length == 2 && ps[1].ParameterType == typeof(bool)) { _removeRenderer = m; break; }
						if (ps.Length == 1 && _removeRenderer == null) _removeRenderer = m;
					}

					if (_addWithColor == null)
						VRChatArchiveModPlugin.Logger.LogWarning("[HighlightEsp] no (Renderer, Color, bool) highlight method — outline ESP unavailable.");
					else
						VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] HighlightsFX methods: add=" + _addWithColor.Name
							+ ", remove=" + (_removeRenderer != null ? _removeRenderer.Name : "none (un-light goes through the add call with enabled=false)") + ".");
				}
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] resolve failed: {e.Message}"); }
			}
			if (_fxType == null || _addWithColor == null) return false;

			// NOT YET IS NOT THE SAME AS NEVER. HighlightsFX simply does not exist during loading,
			// so the early misses are normal. The hunt is rate-limited to once a second while it
			// fails (the scan is not free), and the warning waits until it has genuinely been
			// failing for a while before saying anything at all.
			float now = VaClock.Now;
			if (_firstFxTry <= 0f) _firstFxTry = now;
			if (now < _nextFxScan) return false;
			_nextFxScan = now + 1f;

			try
			{
				// 1) static instance getter
				foreach (var p in _fxType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (p.GetParameters().Length != 0 || p.ReturnType.Name != "HighlightsFX") continue;
					// Through ProxyGuard: HighlightsFX's members cannot be placed on this build, and
					// invoking this getter directly is an access violation. Refused here, the scan simply
					// falls through to finding the effect on the main camera, which works.
					try { _fx = Core.ProxyGuard.Invoke(p, null, null); } catch { }
					if (_fx != null) { _how = "static getter"; break; }
				}
				// 2) on the main camera / anywhere in the scene
				if (_fx == null)
				{
					var il2 = Il2CppType.From(_fxType);
					var cam = Camera.main;
					if (cam != null) { _fx = cam.GetComponentSafe(il2) ?? cam.GetComponentInChildrenSafe(il2, true); if (_fx != null) _how = "main camera"; }
					if (_fx == null) { _fx = VRChatArchiveMod.Core.Live.FirstOfType(il2); if (_fx != null) _how = "scene scan"; }
					// 3) attach one ourselves
					if (_fx == null && cam != null)
					{
						var standalone = Assembly.Load("Assembly-CSharp").GetType("HighlightsFXStandalone");
						if (standalone != null) { _fx = cam.gameObject.AddComponentSafe(Il2CppType.From(standalone)); if (_fx != null) _how = "standalone attached by us"; }
					}
				}
				if (_fx != null)
				{
					// FOUND IS NOT THE SAME AS RUNNING. HighlightsFX can be present but DISABLED —
					// VRChat switches it off with certain graphics settings, and a disabled
					// post-effect draws nothing while every call we make into it still succeeds
					// silently. So it is switched on explicitly, and the route is logged.
					try
					{
						var beh = (_fx as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Behaviour>();
						if (beh != null && !beh.enabled)
						{
							beh.enabled = true;
							VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] the effect was disabled — switched on.");
						}
						var go2 = beh != null ? beh.gameObject : null;
						if (go2 != null && !go2.activeInHierarchy)
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[HighlightEsp] the effect's object is inactive — the glow will not draw.");
					}
					catch { }

					VRChatArchiveModPlugin.Logger.LogInfo("[HighlightEsp] highlight effect resolved via " + _how + ".");
				}
				else if (!_warnedNoFx && now - _firstFxTry >= GiveUpAfter)
				{
					// Once, and only once it has really been missing for half a minute — by which
					// point a world is long since loaded and "not yet" is no longer an explanation.
					_warnedNoFx = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[HighlightEsp] HighlightsFX still not found after " + (int)GiveUpAfter + "s — capsule and "
						+ "item glow will do nothing on this machine. Type found: " + (_fxType != null)
						+ ", add method: " + (_addWithColor != null) + ". Still retrying once a second.");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[HighlightEsp] instance lookup failed: {e.Message}"); }

			return _fx != null;
		}
	}
}
