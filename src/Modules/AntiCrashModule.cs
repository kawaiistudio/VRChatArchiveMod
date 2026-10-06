using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Avatar anti-crash: scans loaded avatars and switches off components that are in
	// genuine "crasher" territory (particle bombs, thousands of lights / audio
	// sources / cloth, PhysBone / Contact floods, trail / constraint spam) and
	// caps what the survivors may do (per-system particle counts, polygon and
	// material budgets, avatar cameras / projectors). Heavy-but-legit avatars are
	// left untouched — thresholds sit far above VRChat's own performance ratings.
	// Every vector is its own toggle, counts what it neutralised, and runs under
	// its own guard so one failing vector never shields the rest of the avatar.
	//
	// NOTHING HERE IS PERMANENT. Every clamp is written to an undo journal with the
	// value it replaced (enabled flag, maxParticles, emission rate, playing state,
	// activeSelf). Switching the master — or one vector — off walks that journal and
	// puts the avatar back exactly as it was; switching it on rescans everyone. While
	// the master is on the journal is re-asserted every two seconds, because an
	// avatar's animator is perfectly able to flip a component we switched off back on.
	// Components are DISABLED, not destroyed: Destroy is the last resort for a type
	// with no enabled flag at all, and such an entry is journaled as irreversible.
	//
	// Trigger is a throttled poll over VRCAvatarDescriptor (a name-stable SDK3
	// type) rather than a Harmony hook on an obfuscated method that rotates every
	// game build — so it keeps working across VRChat updates with no re-mapping.
	public class AntiCrashModule : IModule
	{
		public override string Name => "AntiCrash";

		private int _frame;
		private static readonly HashSet<int> _processed = new HashSet<int>();

		// --- live stats for the menu / client sync ---
		public static int AvatarsScanned => _processed.Count;
		public static int NeutralizedTotal { get; private set; }
		// Name of the last avatar that had something neutralised ("" until one trips).
		public static string LastAvatar = "";
		// Clamps currently held in the journal (what "master OFF" would undo right now).
		public static int Clamped => _journal.Count;
		// Components that had to be destroyed because nothing on them could be switched off.
		public static int IrreversibleTotal { get; private set; }
		public static string Status
		{
			get
			{
				bool on;
				try { on = ModConfig.AntiCrashEnabled.Value; } catch { on = false; }
				int n = _journal.Count;
				if (!on) return n == 0 ? "off — nothing clamped" : $"off — {n} clamp(s) about to be released";
				string s = n == 0 ? "on — nothing clamped" : $"on — {n} clamp(s) held";
				s += $", {_processed.Count} avatar(s) scanned";
				if (IrreversibleTotal > 0) s += $", {IrreversibleTotal} destroyed (irreversible)";
				return s;
			}
		}

		// Client "antiCrashRescan" action (dispatched through OnMain by ModControlModule).
		// Same contract as the IMGUI/QuickMenu rescan button: consumed once in OnUpdate.
		private static volatile bool _rescanRequested;
		public static void RequestRescan() { _rescanRequested = true; }

		// ===================================================================================
		// UNDO JOURNAL
		// ===================================================================================
		// One entry per mutation. Vector is the toggle it belongs to (so a single sub-toggle
		// can release only its own clamps), Kind tells two mutations on the same component
		// apart (a ParticleSystem can carry "max", "rate" and "stop" at once). Undo puts the
		// captured original back; Reapply re-imposes the clamp and reports whether it had to
		// change anything. Both are null for an irreversible (destroyed) entry.
		private sealed class Clamp
		{
			public readonly string Vector;
			public readonly string Kind;
			public readonly UnityEngine.Object Target;
			public readonly (string, string, int) Key;
			public readonly Action Undo;
			public readonly Func<bool> Reapply;
			public bool Irreversible => Undo == null;

			public Clamp(string vector, string kind, UnityEngine.Object target, (string, string, int) key, Action undo, Func<bool> reapply)
			{
				Vector = vector; Kind = kind; Target = target; Key = key; Undo = undo; Reapply = reapply;
			}
		}

		private static readonly List<Clamp> _journal = new List<Clamp>();
		// (vector, kind, instanceId) of every live entry. A rescan finds the components we
		// already switched off and would journal them AGAIN with "was = disabled" — the dedup
		// keeps the first entry, the one that remembers the real original state.
		private static readonly HashSet<(string, string, int)> _journaled = new HashSet<(string, string, int)>();

		private const float ReassertSeconds = 2f;
		private static float _nextReassert;

		// Vector tags (journal + per-toggle edge detection).
		private const string VParticles = "particles", VLights = "lights", VAudio = "audio", VCloth = "cloth",
			VPhysBones = "physbones", VContacts = "contacts", VParticleCounts = "particleCounts", VMeshes = "meshes",
			VMaterials = "materials", VCameras = "cameras", VTrails = "trails", VConstraints = "constraints", VHide = "hide";

		// ===================================================================================
		// TOGGLE EDGE TRACKING (master + the 13 vectors; BundleGuard lives in AssetBundlePatch)
		// ===================================================================================
		private sealed class VectorToggle
		{
			public readonly string Tag;
			public readonly ConfigEntry<bool> Entry;
			public bool Last;
			public VectorToggle(string tag, ConfigEntry<bool> entry) { Tag = tag; Entry = entry; Last = entry.Value; }
		}
		private static VectorToggle[] _vectors;
		private static bool _lastMaster;

		private static void EnsureToggleTable()
		{
			if (_vectors != null) return;
			_vectors = new[]
			{
				new VectorToggle(VParticles,      ModConfig.ClampParticles),
				new VectorToggle(VLights,         ModConfig.ClampLights),
				new VectorToggle(VAudio,          ModConfig.ClampAudioSources),
				new VectorToggle(VCloth,          ModConfig.ClampCloth),
				new VectorToggle(VPhysBones,      ModConfig.ClampPhysBones),
				new VectorToggle(VContacts,       ModConfig.ClampContacts),
				new VectorToggle(VParticleCounts, ModConfig.ClampParticleCounts),
				new VectorToggle(VMeshes,         ModConfig.ClampMeshes),
				new VectorToggle(VMaterials,      ModConfig.ClampMaterials),
				new VectorToggle(VCameras,        ModConfig.DisableAvatarCameras),
				new VectorToggle(VTrails,         ModConfig.ClampTrails),
				new VectorToggle(VConstraints,    ModConfig.ClampConstraints),
				new VectorToggle(VHide,           ModConfig.HideAvatarOverBudget),
			};
			_lastMaster = ModConfig.AntiCrashEnabled.Value;
		}

		public override void OnInitialize()
		{
			EnsureToggleTable();
			// NO ENGINE HOOK. Patching AssetBundle.LoadAsset / AssetBundleRequest.asset (Unity icalls) killed
			// the game at plugin load on 2026-09-04 17:25 — the log ends right after "loading…". The
			// pre-load path stays in the file, disabled, until a hookable VRChat-side load event is found;
			// the poll below runs every few frames instead (see ScanIntervalFrames).
			_assetHookInfo = "disabled (engine icall detour crashed the game at load)";
			if (!ModConfig.AntiCrashEnabled.Value)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] disabled by config (switch it on to start clamping; switching off again releases every clamp).");
				return;
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] armed (VRCAvatarDescriptor poll every " + ModConfig.ScanIntervalFrames.Value + " frame(s), undo journal on; pre-load hook " + _assetHookInfo + ").");
		}

		// ===================================================================================
		// PRE-LOAD CLAMP. The poll below sees an avatar only after it has been instantiated — and
		// a shader bomb can hang the GPU on its very first frame, before any poll runs. So the
		// prefab is clamped the moment VRChat pulls it out of the bundle: postfixes on the STABLE
		// engine methods AssetBundle.LoadAsset(...) and AssetBundleRequest.asset (Il2CppInterop
		// detours the native method, so VRChat's own calls land here). Any loaded GameObject that
		// carries a VRCAvatarDescriptor gets the full ScanAndClamp before Instantiate copies it,
		// so the instance is born already trimmed. The poll stays as the safety net.
		// ===================================================================================
		private static bool _assetHooked;
		private static string _assetHookInfo = "not attached";

		private static void HookAssetLoads()
		{
			if (_assetHooked) return;
			int patched = 0;
			var notes = new System.Text.StringBuilder();
			try
			{
				var post = new HarmonyMethod(typeof(AntiCrashModule).GetMethod(nameof(OnAssetLoaded), BindingFlags.Static | BindingFlags.NonPublic));
				try
				{
					var getter = typeof(AssetBundleRequest).GetProperty("asset", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod();
					if (getter != null) { VRChatArchiveModPlugin.HarmonyInstance.Patch(getter, postfix: post); patched++; notes.Append("AssetBundleRequest.asset "); }
				}
				catch (Exception e) { notes.Append("[asset getter failed: ").Append(e.Message).Append("] "); }
				foreach (var m in typeof(AssetBundle).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
				{
					if (m.Name != "LoadAsset" || m.IsGenericMethod) continue;
					if (m.ReturnType != typeof(UnityEngine.Object)) continue;
					try { VRChatArchiveModPlugin.HarmonyInstance.Patch(m, postfix: post); patched++; notes.Append("AssetBundle.LoadAsset/").Append(m.GetParameters().Length).Append(' '); }
					catch (Exception e) { notes.Append("[LoadAsset failed: ").Append(e.Message).Append("] "); }
				}
			}
			catch (Exception e) { notes.Append("[hook failed: ").Append(e.Message).Append("] "); }
			_assetHooked = patched > 0;
			_assetHookInfo = patched > 0 ? "postfix on " + notes.ToString().Trim() : "no method patched " + notes;
			VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] pre-load hook: " + _assetHookInfo);
		}

		// One cheap check per loaded asset: only GameObjects that carry an avatar descriptor go on.
		private static void OnAssetLoaded(UnityEngine.Object __result)
		{
			try
			{
				if (__result == null || !ModConfig.AntiCrashEnabled.Value) return;
				var go = __result.TryCast<GameObject>();
				if (go == null || !Live(go)) return;
				VRCAvatarDescriptor desc = null;
				try { desc = go.GetComponentInChildren<VRCAvatarDescriptor>(true); } catch { }
				if (desc == null) return;
				int id;
				try { id = go.GetInstanceID(); } catch { return; }
				if (!_processed.Add(id)) return;   // the same asset is read back several times
				ScanAndClamp(go, prefab: true);
				VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] pre-load: clamped prefab '" + SafeName(go) + "' before its first frame.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[AntiCrash] pre-load hook: " + e.Message); }
		}

		public override void OnUpdate()
		{
			EnsureToggleTable();

			// Menu "Rescan" button or client action. Consumed BEFORE the enabled check so a request
			// that lands while the master is off is not stranded until the next one; the client
			// flag is cleared here (not in RequestRescan) so a request that lands mid-pass still counts.
			bool rescan = Menu.ConsumeRescanRequest();
			if (_rescanRequested) { _rescanRequested = false; rescan = true; }

			// ---- master edge ----
			bool master = false;
			try { master = ModConfig.AntiCrashEnabled.Value; } catch { }
			if (master != _lastMaster)
			{
				_lastMaster = master;
				if (!master)
				{
					int undone = RestoreAll(out int irreversible);
					_processed.Clear();
					VRChatArchiveModPlugin.Logger.LogInfo($"[AntiCrash] master OFF — {undone} clamp(s) undone"
						+ (irreversible > 0 ? $" ({irreversible} destroyed component(s) cannot come back until the avatar reloads)" : "") + ".");
				}
				else
				{
					_processed.Clear();
					VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] master ON — rescanning.");
				}
			}

			// ---- per-vector edges (13 bool compares) ----
			for (int i = 0; i < _vectors.Length; i++)
			{
				var vt = _vectors[i];
				bool v;
				try { v = vt.Entry.Value; } catch { continue; }
				if (v == vt.Last) continue;
				vt.Last = v;
				if (!v)
				{
					int undone = RestoreVector(vt.Tag, out int irreversible);
					VRChatArchiveModPlugin.Logger.LogInfo($"[AntiCrash] {vt.Tag} OFF — {undone} clamp(s) undone"
						+ (irreversible > 0 ? $" ({irreversible} irreversible)" : "") + ".");
				}
				else
				{
					_processed.Clear();
					VRChatArchiveModPlugin.Logger.LogInfo($"[AntiCrash] {vt.Tag} ON — " + (master ? "rescanning." : "will apply once the master is on."));
				}
			}

			if (rescan)
			{
				_processed.Clear();
				VRChatArchiveModPlugin.Logger.LogInfo("[AntiCrash] rescan requested — re-checking all loaded avatars" + (master ? "." : " once the master is on."));
			}

			if (!master) return;

			// ---- re-assert held clamps ----
			// Avatar animators toggle components freely; one we switched off can be switched back on
			// by the next animation state. Every two seconds the journal is walked and re-imposed.
			try
			{
				float now = VaClock.Now;
				if (now >= _nextReassert)
				{
					_nextReassert = now + ReassertSeconds;
					Reassert();
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] re-assert pass threw: {e.Message}");
			}

			int interval = ModConfig.ScanIntervalFrames.Value;
			if (interval < 1) interval = 1;
			if (++_frame < interval) return;
			_frame = 0;

			try
			{
				// Walk the PLAYERS, not the whole loaded-object table. Resources.FindObjectsOfTypeAll
				// sweeps every loaded object (inactive ones and unloaded-scene assets included) and
				// allocates a fresh array each pass — in a busy public instance that is a multi-ms
				// native stall paid twice a second, forever, even when nothing new has spawned.
				// Every avatar we care about hangs off a player, so bound the work by player count.
				var players = VRChatArchiveMod.Core.VaPlayers.All();
				if (players == null) return;

				for (int i = 0; i < players.Count; i++)
				{
					VRCAvatarDescriptor desc = null;
					try
					{
						var api = players[i];
						// A null check is not a liveness check: VRCPlayerApi is not a UnityEngine.Object, so `== null` is
						// the plain managed test and says nothing about the il2cpp object behind the handle. Reading any
						// member off a stale one is an access violation inside the proxy, which no try/catch can stop.
						if (api == null || !Core.NativeGuard.Alive(api)) continue;
						var go = api.gameObject;
						if (go == null || !Core.NativeGuard.Alive(go)) continue;
						desc = go.GetComponentInChildren<VRCAvatarDescriptor>(true);
					}
					catch { continue; }
					if (desc == null) continue;

					GameObject root;
					try { root = desc.gameObject; } catch { continue; }
					if (root == null) continue;

					int id;
					try { id = root.GetInstanceID(); } catch { continue; }
					if (!_processed.Add(id)) continue; // already handled this avatar instance

					ScanAndClamp(root);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] scan pass threw: {e}");
			}
		}

		// Avatar instance ids are per-instance; carrying them across worlds would slowly grow the set
		// and (worse) let a recycled id skip a real scan.
		//
		// THE JOURNAL IS NOW UNDONE FIRST (2026-09-13). It used to be dropped without undo, on the
		// reasoning that its targets left with the old world and prodding a torn-down proxy is an
		// access violation. The first half is not always true — OnSceneLoaded also fires for
		// additive and UI loads, and the local player's own avatar is scanned by the same pass and
		// survives every world change — so anything clamped that lived on kept its disabled
		// renderers, lights and particle systems for the rest of the session with no journal entry
		// left to release them. The second half is already handled: RestoreAll() proves liveness
		// with Live() (NativeGuard.Alive) before every undo and simply skips what really is gone, so
		// calling it here is safe AND is the only thing that unclamps a survivor.
		public override void OnSceneLoaded(int buildIndex)
		{
			_processed.Clear();
			int n = _journal.Count;
			int undone = RestoreAll(out int irreversible);   // clears _journal / _journaled itself
			if (n > 0)
				VRChatArchiveModPlugin.Logger.LogInfo($"[AntiCrash] scene changed — {undone} clamp(s) undone on survivors, {n - undone} entr{(n - undone == 1 ? "y" : "ies")} released with the old world.");
		}

		// The game is going down (or the plugin is): leave every avatar the way we found it.
		public override void OnShutdown()
		{
			try
			{
				int undone = RestoreAll(out int irreversible);
				if (undone > 0 || irreversible > 0)
					VRChatArchiveModPlugin.Logger.LogInfo($"[AntiCrash] shutdown — {undone} clamp(s) undone"
						+ (irreversible > 0 ? $" ({irreversible} irreversible)" : "") + ".");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] shutdown restore threw: {e.Message}");
			}
		}

		// ===================================================================================
		// JOURNAL OPERATIONS
		// ===================================================================================

		// A destroyed Unity object keeps a live il2cpp wrapper, so NativeGuard alone says "alive";
		// the overloaded != null asks Unity whether the native side is still there. NativeGuard
		// goes first because that comparison is itself a native field read.
		private static bool Live(UnityEngine.Object o)
		{
			try { return NativeGuard.Alive(o) && o != null; }
			catch { return false; }
		}

		// Records one clamp. Returns false when an identical clamp is already held (rescan) —
		// the mutation has been re-applied by the caller, but the ORIGINAL entry keeps its "was".
		private static bool Journal(string vector, string kind, UnityEngine.Object target, Action undo, Func<bool> reapply)
		{
			int id = 0;
			try { id = target.GetInstanceID(); } catch { }
			var key = (vector, kind, id);
			if (id != 0 && !_journaled.Add(key)) return false;
			_journal.Add(new Clamp(vector, kind, target, key, undo, reapply));
			return true;
		}

		private static void Forget(int index)
		{
			var c = _journal[index];
			_journal.RemoveAt(index);
			_journaled.Remove(c.Key);
		}

		// Undo EVERY entry (alive targets only) and empty the journal. Walked newest-first: two
		// vectors can disable the same renderer, and the second one captured "was = off" — only
		// LIFO order lands on the true original.
		private static int RestoreAll(out int irreversible)
		{
			irreversible = 0;
			int undone = 0;
			for (int i = _journal.Count - 1; i >= 0; i--)
			{
				var c = _journal[i];
				if (c.Irreversible) { irreversible++; continue; }
				if (!Live(c.Target)) continue;
				try { c.Undo(); undone++; }
				catch (Exception e) { VRChatArchiveModPlugin.Logger.LogDebug($"[AntiCrash] undo {c.Vector}/{c.Kind} threw: {e.Message}"); }
			}
			_journal.Clear();
			_journaled.Clear();
			return undone;
		}

		// Undo and drop only one vector's entries (a sub-toggle went off while the master stays on).
		private static int RestoreVector(string vector, out int irreversible)
		{
			irreversible = 0;
			int undone = 0;
			for (int i = _journal.Count - 1; i >= 0; i--)
			{
				var c = _journal[i];
				if (c.Vector != vector) continue;
				if (c.Irreversible) irreversible++;
				else if (Live(c.Target))
				{
					try { c.Undo(); undone++; }
					catch (Exception e) { VRChatArchiveModPlugin.Logger.LogDebug($"[AntiCrash] undo {c.Vector}/{c.Kind} threw: {e.Message}"); }
				}
				Forget(i);
			}
			return undone;
		}

		// Re-impose every held clamp whose target is still alive; drop the rest.
		private static void Reassert()
		{
			int reapplied = 0, dropped = 0;
			for (int i = _journal.Count - 1; i >= 0; i--)
			{
				var c = _journal[i];
				if (!Live(c.Target)) { Forget(i); dropped++; continue; }
				if (c.Reapply == null) continue;
				try { if (c.Reapply()) reapplied++; }
				catch { }
			}
			if (reapplied > 0 || dropped > 0)
				VRChatArchiveModPlugin.Logger.LogDebug($"[AntiCrash] re-assert: {reapplied} clamp(s) re-imposed, {dropped} dead entr{(dropped == 1 ? "y" : "ies")} dropped, {_journal.Count} held.");
		}

		// ---- the reversible mutations (each captures its original before writing) ----

		private static void DisableRenderer(Renderer r, string vector)
		{
			bool was = r.enabled;
			r.enabled = false;
			Journal(vector, "enabled", r,
				() => { r.enabled = was; },
				() => { if (!r.enabled) return false; r.enabled = false; return true; });
		}

		private static void DisableBehaviour(Behaviour b, string vector)
		{
			bool was = b.enabled;
			b.enabled = false;
			Journal(vector, "enabled", b,
				() => { b.enabled = was; },
				() => { if (!b.enabled) return false; b.enabled = false; return true; });
		}

		// Cloth is a Component, not a Behaviour, but carries its own enabled flag.
		private static void DisableCloth(Cloth c, string vector)
		{
			bool was = c.enabled;
			c.enabled = false;
			Journal(vector, "enabled", c,
				() => { c.enabled = was; },
				() => { if (!c.enabled) return false; c.enabled = false; return true; });
		}

		// Stop + clear + emission module off. Undo re-enables the module and, if the system was
		// playing when we found it, plays it again. Re-assert only acts when something turned the
		// module back on (an animator), and clears whatever it managed to emit in the meantime.
		private static void StopParticles(ParticleSystem ps, string vector)
		{
			var em = ps.emission;
			bool emWas = em.enabled;
			bool wasPlaying = ps.isPlaying;
			ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			em.enabled = false;
			Journal(vector, "stop", ps,
				() =>
				{
					var e = ps.emission;
					e.enabled = emWas;
					if (wasPlaying) ps.Play(true);
				},
				() =>
				{
					var e = ps.emission;
					if (!e.enabled) return false;
					e.enabled = false;
					ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
					return true;
				});
		}

		// Single choke point for the count vectors. Switches the component off through whatever
		// enabled flag its type has; Destroy is the fallback for a type that has none, and that
		// entry is journaled as irreversible. Returns true when the clamp is reversible.
		private static bool Neutralize(UnityEngine.Object c, string vector)
		{
			var ps = c.TryCast<ParticleSystem>();
			if (ps != null) { StopParticles(ps, vector); return true; }

			var r = c.TryCast<Renderer>();
			if (r != null) { DisableRenderer(r, vector); return true; }

			var cloth = c.TryCast<Cloth>();
			if (cloth != null) { DisableCloth(cloth, vector); return true; }

			var b = c.TryCast<Behaviour>();
			if (b != null) { DisableBehaviour(b, vector); return true; }

			UnityEngine.Object.Destroy(c);
			IrreversibleTotal++;
			Journal(vector, "destroyed", c, null, null);
			return false;
		}

		// ===================================================================================
		// SCAN
		// ===================================================================================

		// Scans a loaded avatar root and neutralizes crasher-tier components. prefab = the bundle asset
		// itself (pre-load hook): everything is clamped there too, except HIDE — an inactive prefab root
		// would hand VRChat's avatar setup an inactive instance; the poll hides the instance if needed.
		public static void ScanAndClamp(GameObject avatarRoot, bool prefab = false)
		{
			if (avatarRoot == null || !ModConfig.AntiCrashEnabled.Value) return;

			string avatar = SafeName(avatarRoot);
			int removed = 0;
			// Categories that neutralised something on this avatar. Feeds the over-budget
			// decision: an avatar tripping many vectors at once is a crasher, not a heavy avatar.
			var tripped = new List<string>(16);

			try
			{
				// ---- count clamps (switch off past N of a kind) ----
				// Each vector runs in its own guard (Pass) so one interop failure — a component
				// type whose class pointer fails to resolve, a torn-down proxy — never leaves the
				// remaining vectors unchecked on that avatar.

				if (ModConfig.ClampParticles.Value)
					removed += Pass(avatar, "ParticleSystem", tripped, () =>
						ClampByCount<ParticleSystem>(avatarRoot.GetComponentsInChildren<ParticleSystem>(true), ModConfig.MaxParticleSystems.Value, "ParticleSystem", VParticles, avatar));

				if (ModConfig.ClampLights.Value)
					removed += Pass(avatar, "Light", tripped, () =>
						ClampByCount<Light>(avatarRoot.GetComponentsInChildren<Light>(true), ModConfig.MaxLights.Value, "Light", VLights, avatar));

				if (ModConfig.ClampAudioSources.Value)
					removed += Pass(avatar, "AudioSource", tripped, () =>
						ClampByCount<AudioSource>(avatarRoot.GetComponentsInChildren<AudioSource>(true), ModConfig.MaxAudioSources.Value, "AudioSource", VAudio, avatar));

				if (ModConfig.ClampCloth.Value)
					removed += Pass(avatar, "Cloth", tripped, () =>
						ClampByCount<Cloth>(avatarRoot.GetComponentsInChildren<Cloth>(true), ModConfig.MaxCloth.Value, "Cloth", VCloth, avatar));

				if (ModConfig.ClampPhysBones.Value)
					removed += Pass(avatar, "VRCPhysBone", tripped, () =>
						ClampByCount<VRCPhysBone>(avatarRoot.GetComponentsInChildren<VRCPhysBone>(true), ModConfig.MaxPhysBones.Value, "VRCPhysBone", VPhysBones, avatar));

				if (ModConfig.ClampContacts.Value)
					removed += Pass(avatar, "VRCContact", tripped, () =>
					{
						int half = ModConfig.MaxContacts.Value / 2;
						return ClampByCount<VRCContactReceiver>(avatarRoot.GetComponentsInChildren<VRCContactReceiver>(true), half, "VRCContactReceiver", VContacts, avatar)
							 + ClampByCount<VRCContactSender>(avatarRoot.GetComponentsInChildren<VRCContactSender>(true), half, "VRCContactSender", VContacts, avatar);
					});

				if (ModConfig.ClampTrails.Value)
					removed += Pass(avatar, "Trail/LineRenderer", tripped, () => ClampTrails(avatarRoot, avatar));

				if (ModConfig.ClampConstraints.Value)
					removed += Pass(avatar, "Constraint", tripped, () => ClampConstraints(avatarRoot, avatar));

				// ---- content clamps (cap what a component is allowed to do) ----

				if (ModConfig.ClampParticleCounts.Value)
					removed += Pass(avatar, "ParticleCount", tripped, () => ClampParticleCounts(avatarRoot, avatar));

				if (ModConfig.ClampMeshes.Value)
					removed += Pass(avatar, "Mesh", tripped, () => ClampMeshes(avatarRoot, avatar));

				if (ModConfig.ClampMaterials.Value)
					removed += Pass(avatar, "Material", tripped, () => ClampMaterials(avatarRoot, avatar));

				if (ModConfig.DisableAvatarCameras.Value)
					removed += Pass(avatar, "Camera/Projector", tripped, () => DisableCameras(avatarRoot, avatar));

				if (removed > 0)
				{
					NeutralizedTotal += removed;
					LastAvatar = avatar;
					VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] neutralized {removed} crasher-tier item(s) on '{avatar}' ({tripped.Count} categor{(tripped.Count == 1 ? "y" : "ies")}: {string.Join(", ", tripped)}).");
				}

				// ---- over budget: hide the whole avatar ----
				// Trimming makes a crasher harmless but leaves the wreck visible (and its remaining
				// components running). Past HideAvatarTrips categories the avatar is not "heavy",
				// it is hostile — so hide it entirely. Off by default: a false positive here costs
				// the user a whole avatar instead of a few components. Journaled like everything
				// else, so master OFF (or the hide toggle going off) shows it again.
				if (!prefab && ModConfig.HideAvatarOverBudget.Value && tripped.Count > ModConfig.HideAvatarTrips.Value)
				{
					try
					{
						var root = avatarRoot;
						bool wasActive = root.activeSelf;
						root.SetActive(false);
						Journal(VHide, "active", root,
							() => { root.SetActive(wasActive); },
							() => { if (!root.activeSelf) return false; root.SetActive(false); return true; });
						LastAvatar = avatar;
						VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] HIDDEN '{avatar}': {tripped.Count} categories tripped (limit {ModConfig.HideAvatarTrips.Value}) — {string.Join(", ", tripped)}.");
					}
					catch (Exception e)
					{
						VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] could not hide '{avatar}': {e.Message}");
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] ScanAndClamp threw on '{avatar}': {e}");
			}
		}

		// Runs one clamp vector under its own guard. A vector that throws is logged and counts
		// as nothing neutralised; a vector that neutralised anything is recorded as "tripped".
		private static int Pass(string avatar, string category, List<string> tripped, Func<int> vector)
		{
			int n;
			try { n = vector(); }
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[AntiCrash] {category} pass threw on '{avatar}': {e.Message}");
				return 0;
			}
			if (n > 0) tripped.Add(category);
			return n;
		}

		// Switches off every component past 'max' (see Neutralize). Returns how many were clamped.
		private static int ClampByCount<T>(Il2CppArrayBase<T> comps, int max, string label, string vector, string avatar)
			where T : UnityEngine.Object
		{
			if (comps == null) return 0;
			int total = comps.Length;
			if (total <= max) return 0;

			int disabled = 0, destroyed = 0;
			for (int i = max; i < total; i++)
			{
				try
				{
					var c = comps[i];
					if (c == null) continue;
					if (Neutralize(c, vector)) disabled++; else destroyed++;
				}
				catch { }
			}
			int n = disabled + destroyed;
			if (n > 0)
				VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' {label}: {total} found, switched off {disabled} over limit {max}"
					+ (destroyed > 0 ? $" ({destroyed} had no enabled flag and were destroyed — irreversible)" : "") + ".");
			return n;
		}

		// ClampByCount over several component types that share ONE budget: each type consumes
		// what it keeps, the next type only gets what is left. Returns how many were clamped.
		private static int ClampShared<T>(Il2CppArrayBase<T> comps, ref int budget, string label, string vector, string avatar)
			where T : UnityEngine.Object
		{
			if (comps == null) return 0;
			int n = comps.Length;
			int allowed = budget < 0 ? 0 : budget;
			int clamped = ClampByCount<T>(comps, allowed, label, vector, avatar);
			budget -= (n - clamped);
			if (budget < 0) budget = 0;
			return clamped;
		}

		// Trail + Line renderer spam: each one is a dynamic mesh rebuilt every frame; hundreds of
		// them stall the render thread. Counted together against MaxTrailRenderers.
		private static int ClampTrails(GameObject root, string avatar)
		{
			int budget = ModConfig.MaxTrailRenderers.Value;
			int removed = 0;
			removed += ClampShared<TrailRenderer>(root.GetComponentsInChildren<TrailRenderer>(true), ref budget, "TrailRenderer", VTrails, avatar);
			removed += ClampShared<LineRenderer>(root.GetComponentsInChildren<LineRenderer>(true), ref budget, "LineRenderer", VTrails, avatar);
			return removed;
		}

		// Constraint chains: thousands of Unity / VRC constraints stall the animation thread
		// (each one resolves against its sources every frame, chains resolve serially). Unity's
		// six constraint types and every VRC constraint (VRCConstraintBase, VRC.Dynamics — the
		// SDK3 Parent/Position/... components all derive from it) share MaxConstraints.
		private static int ClampConstraints(GameObject root, string avatar)
		{
			int budget = ModConfig.MaxConstraints.Value;
			int removed = 0;
			removed += ClampShared<VRC.Dynamics.VRCConstraintBase>(root.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true), ref budget, "VRCConstraint", VConstraints, avatar);
			removed += ClampShared<ParentConstraint>(root.GetComponentsInChildren<ParentConstraint>(true), ref budget, "ParentConstraint", VConstraints, avatar);
			removed += ClampShared<PositionConstraint>(root.GetComponentsInChildren<PositionConstraint>(true), ref budget, "PositionConstraint", VConstraints, avatar);
			removed += ClampShared<RotationConstraint>(root.GetComponentsInChildren<RotationConstraint>(true), ref budget, "RotationConstraint", VConstraints, avatar);
			removed += ClampShared<ScaleConstraint>(root.GetComponentsInChildren<ScaleConstraint>(true), ref budget, "ScaleConstraint", VConstraints, avatar);
			removed += ClampShared<AimConstraint>(root.GetComponentsInChildren<AimConstraint>(true), ref budget, "AimConstraint", VConstraints, avatar);
			removed += ClampShared<LookAtConstraint>(root.GetComponentsInChildren<LookAtConstraint>(true), ref budget, "LookAtConstraint", VConstraints, avatar);
			return removed;
		}

		// Particle bombs that pass the system-count clamp: a handful of systems each allowed
		// millions of particles, or an emission rate that fills them in one frame. maxParticles
		// is a hard cap on live particles, so capping it (and rateOverTime) bounds the CPU and
		// GPU cost per system; the running sum of maxParticles is the avatar's worst-case live
		// particle count, and systems past MaxTotalParticles are stopped and muted (emission
		// module off — the component stays, nothing else on the avatar is touched).
		// ps.main / ps.emission are module handles: their setters write straight through to the
		// native system, so a local copy is enough. The original maxParticles and rateOverTime
		// are journaled before they are overwritten.
		private static int ClampParticleCounts(GameObject root, string avatar)
		{
			var systems = root.GetComponentsInChildren<ParticleSystem>(true);
			if (systems == null || systems.Length == 0) return 0;

			int perSystemCap = ModConfig.MaxParticlesPerSystem.Value;
			int rateCap = ModConfig.MaxEmissionRate.Value;
			int totalCap = ModConfig.MaxTotalParticles.Value;
			int capped = 0, rateCapped = 0, stopped = 0;
			long total = 0;

			for (int i = 0; i < systems.Length; i++)
			{
				ParticleSystem ps;
				try { ps = systems[i]; } catch { continue; }
				if (ps == null) continue;
				try
				{
					var main = ps.main;
					int max = main.maxParticles;
					if (max > perSystemCap)
					{
						int origMax = max;
						int cap = perSystemCap;
						main.maxParticles = cap; max = cap; capped++;
						Journal(VParticleCounts, "max", ps,
							() => { var m = ps.main; m.maxParticles = origMax; },
							() => { var m = ps.main; if (m.maxParticles <= cap) return false; m.maxParticles = cap; return true; });
					}

					var em = ps.emission;
					var rate = em.rateOverTime;
					if (rate.constantMax > rateCap)
					{
						var origRate = rate;   // copy of the MinMaxCurve BEFORE it is replaced
						float capF = rateCap;
						em.rateOverTime = new ParticleSystem.MinMaxCurve(capF);
						rateCapped++;
						Journal(VParticleCounts, "rate", ps,
							() => { var e = ps.emission; e.rateOverTime = origRate; },
							() =>
							{
								var e = ps.emission;
								if (e.rateOverTime.constantMax <= capF) return false;
								e.rateOverTime = new ParticleSystem.MinMaxCurve(capF);
								return true;
							});
					}

					total += max;
					if (total > totalCap)
					{
						StopParticles(ps, VParticleCounts);
						stopped++;
					}
				}
				catch { }
			}

			int n = capped + rateCapped + stopped;
			if (n > 0)
				VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' particles: {systems.Length} systems, {total} max particles total — capped {capped} over {perSystemCap}/system, {rateCapped} over rate {rateCap}, stopped {stopped} past budget {totalCap}.");
			return n;
		}

		// Triangle count without touching mesh.triangles (that call copies the whole index
		// buffer into a managed array — on a 10M-triangle crasher that IS the crash).
		private static long TriangleCount(Mesh mesh)
		{
			if (mesh == null) return 0;
			long n = 0;
			int subs = mesh.subMeshCount;
			for (int i = 0; i < subs; i++)
			{
				try { n += mesh.GetIndexCount(i) / 3; } catch { }
			}
			return n;
		}

		// AMPLIFIED MESH: far more triangles than that many vertices can honestly produce.
		//
		// Measured on the crasher supplied on 2026-09-07 ("NEED PATCH VRCA.vrca"):
		//     Mesh 'Plane'  vertexCount=26  submeshes=1  indexCount=30426  ->  10142 triangles
		// 390 triangles per vertex. Real geometry cannot do that — indices point AT vertices, so a
		// normal mesh sits near 2 triangles per vertex and even the ugliest fan stays in single
		// digits. Far above that is an index buffer written by hand to draw the same 26 points
		// thousands of times, every triangle stacked on the same plane: pure overdraw, invisible to
		// every budget this module already had. That avatar came to 10142 triangles (4% of
		// MaxTrianglesPerMesh) across 116 material slots (cap 120) — it slipped under BOTH, and only
		// the slots-vs-sub-meshes and tiny-mesh-shader rules caught it.
		//
		// The ratio is the honest signal and it is cheap: GetIndexCount plus vertexCount, no
		// allocation. The absolute floor keeps a 12-triangle debug quad from ever tripping it.
		private const int AmplifyMinTriangles = 5000;

		private static string MeshName(UnityEngine.Object o)
		{
			try { return o == null ? "?" : (o.name ?? "?"); } catch { return "?"; }
		}

		private static bool Amplified(Mesh mesh, long tris, out int verts, out long ratio)
		{
			verts = 0; ratio = 0;
			try
			{
				if (mesh == null) return false;
				verts = mesh.vertexCount;
				if (verts <= 0 || tris < AmplifyMinTriangles) return false;
				ratio = tris / verts;
				int max = ModConfig.MaxTrianglesPerVertex.Value;
				return max > 0 && ratio > max;
			}
			catch { return false; }
		}

		// Polygon crashers: one mesh with tens of millions of triangles, or hundreds of meshes
		// that add up to it. Renderers are disabled, never destroyed — the mesh and its bones
		// stay intact, so the avatar keeps animating, it just does not draw the bomb.
		private static int ClampMeshes(GameObject root, string avatar)
		{
			int perMesh = ModConfig.MaxTrianglesPerMesh.Value;
			int budget = ModConfig.MaxTotalTriangles.Value;
			long total = 0;
			int overMesh = 0, overBudget = 0, meshes = 0, amplified = 0;

			var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
			if (skinned != null)
			{
				for (int i = 0; i < skinned.Length; i++)
				{
					try
					{
						var smr = skinned[i];
						if (smr == null) continue;
						long tris = TriangleCount(smr.sharedMesh);
						meshes++;
						total += tris;
						if (Amplified(smr.sharedMesh, tris, out int sv, out long sr))
						{
							VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' amplified mesh '{MeshName(smr.sharedMesh)}': {tris} triangles from {sv} vertices ({sr}:1) — renderer disabled.");
							DisableRenderer(smr, VMeshes); amplified++;
						}
						else if (tris > perMesh) { DisableRenderer(smr, VMeshes); overMesh++; }
						else if (total > budget) { DisableRenderer(smr, VMeshes); overBudget++; }
					}
					catch { }
				}
			}

			var filters = root.GetComponentsInChildren<MeshFilter>(true);
			if (filters != null)
			{
				for (int i = 0; i < filters.Length; i++)
				{
					try
					{
						var mf = filters[i];
						if (mf == null) continue;
						var r = mf.GetComponent<Renderer>();
						if (r == null) continue;  // a filter with no renderer draws nothing
						long tris = TriangleCount(mf.sharedMesh);
						meshes++;
						total += tris;
						if (Amplified(mf.sharedMesh, tris, out int mv, out long mr))
						{
							VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' amplified mesh '{MeshName(mf.sharedMesh)}': {tris} triangles from {mv} vertices ({mr}:1) — renderer disabled.");
							DisableRenderer(r, VMeshes); amplified++;
						}
						else if (tris > perMesh) { DisableRenderer(r, VMeshes); overMesh++; }
						else if (total > budget) { DisableRenderer(r, VMeshes); overBudget++; }
					}
					catch { }
				}
			}

			int n = overMesh + overBudget + amplified;
			if (n > 0)
				VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' meshes: {meshes} meshes, {total} triangles — disabled {overMesh} over {perMesh}/mesh, {overBudget} past budget {budget}, {amplified} amplified (triangles ≫ vertices).");
			return n;
		}

		// Shader / material bombs. Three rules, all reversible:
		//  1. MORE SLOTS THAN SUB-MESHES. Unity draws the LAST sub-mesh once more for every material
		//     slot past the mesh's sub-mesh count, so a 26-vertex plane with 29 slots of one shader is
		//     that shader run 29 times per frame (the "Cloner" crasher: 4 planes x 29 slots of a
		//     geometry shader that clones the mesh in a loop -> GPU hang). No legitimate avatar has
		//     more slots than sub-meshes (Unity itself warns), so the array is cut to the sub-mesh
		//     count and the original kept for the undo.
		//  2. TINY MESH + UNKNOWN SHADER. A quad or a plane (<= ShaderBombMaxVerts vertices) drawn
		//     with a shader that is not one of the known families is the shape of every shader
		//     crasher (geometry-shader cloners, infinite-loop fragment shaders, screen-space bombs):
		//     the mesh is nothing, the shader is everything. Its materials are swapped for a plain
		//     diffuse fallback - the quad stays, harmlessly grey - and the originals are kept.
		//  3. SLOT BUDGET. Renderers past MaxMaterialSlots slots for the whole avatar are disabled
		//     (the first slots keep drawing as-is).
		private const int ShaderBombMaxVerts = 64;
		private static Material _fallbackMat;

		private static int ClampMaterials(GameObject root, string avatar)
		{
			int cap = ModConfig.MaxMaterialSlots.Value;
			var rends = root.GetComponentsInChildren<Renderer>(true);
			if (rends == null) return 0;

			int slots = 0, off = 0, trimmed = 0, swapped = 0;
			for (int i = 0; i < rends.Length; i++)
			{
				try
				{
					var r = rends[i];
					if (r == null || !Live(r)) continue;
					var mats = r.sharedMaterials;
					int count = mats == null ? 0 : mats.Length;

					Mesh mesh = MeshOf(r);
					int sub = 0, verts = 0;
					if (mesh != null) { try { sub = mesh.subMeshCount; verts = mesh.vertexCount; } catch { sub = 0; } }

					// rule 1
					if (sub > 0 && count > sub && mats != null)
					{
						if (TrimSlots(r, mats, sub)) trimmed++;
						count = sub;
					}
					// rule 2
					if (mesh != null && verts > 0 && verts <= ShaderBombMaxVerts && count > 0 && HasUnknownShader(r.sharedMaterials))
					{
						if (SwapToFallback(r)) swapped++;
					}
					// rule 3
					slots += count;
					if (slots > cap) { DisableRenderer(r, VMaterials); off++; }
				}
				catch { }
			}

			int n = off + trimmed + swapped;
			if (n > 0)
				VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' materials: {rends.Length} renderers, {slots} slots — trimmed {trimmed} renderer(s) with more slots than sub-meshes, swapped {swapped} tiny-mesh renderer(s) off an unknown shader, disabled {off} past budget {cap}.");
			return n;
		}

		private static Mesh MeshOf(Renderer r)
		{
			try
			{
				var smr = r.TryCast<SkinnedMeshRenderer>();
				if (smr != null) return smr.sharedMesh;
				var mf = r.GetComponent<MeshFilter>();
				return mf != null ? mf.sharedMesh : null;
			}
			catch { return null; }
		}

		private static bool TrimSlots(Renderer r, Il2CppReferenceArray<Material> orig, int sub)
		{
			var cut = new Il2CppReferenceArray<Material>(sub);
			for (int k = 0; k < sub; k++) cut[k] = orig[k];
			r.sharedMaterials = cut;
			Journal(VMaterials, "slots", r,
				() => { r.sharedMaterials = orig; },
				() => { var cur = r.sharedMaterials; if (cur == null || cur.Length <= sub) return false; r.sharedMaterials = cut; return true; });
			return true;
		}

		private static bool SwapToFallback(Renderer r)
		{
			var fb = FallbackMaterial();
			if (fb == null) return false;
			var orig = r.sharedMaterials;
			if (orig == null || orig.Length == 0) return false;
			int fbId = fb.GetInstanceID();
			var arr = new Il2CppReferenceArray<Material>(orig.Length);
			for (int k = 0; k < arr.Length; k++) arr[k] = fb;
			r.sharedMaterials = arr;
			Journal(VMaterials, "shader", r,
				() => { r.sharedMaterials = orig; },
				() =>
				{
					// an animation can swap the material back: put the fallback back
					var cur = r.sharedMaterials;
					try { if (cur != null && cur.Length > 0 && cur[0] != null && cur[0].GetInstanceID() == fbId) return false; } catch { }
					r.sharedMaterials = arr; return true;
				});
			return true;
		}

		private static Material FallbackMaterial()
		{
			try
			{
				if (_fallbackMat != null && Live(_fallbackMat)) return _fallbackMat;
				var sh = Shader.Find("VRChat/Mobile/Diffuse") ?? Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
				if (sh == null) return null;
				_fallbackMat = new Material(sh) { name = "VA_AntiCrash_Fallback", color = new Color(0.55f, 0.55f, 0.55f, 1f) };
				_fallbackMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
				return _fallbackMat;
			}
			catch { return null; }
		}

		// Shader families a tiny mesh is allowed to keep. Anything else on a quad/plane is treated
		// as a shader bomb (rule 2). Matched case-insensitively on the shader's name.
		private static readonly string[] SafeShaderFamilies =
		{
			"standard", "vrchat/", "legacy shaders/", "unlit/", "mobile/", "particles/", "sprites/", "ui/", "hidden/",
			"textmeshpro", "skybox/", "nature/", "fx/", "universal render pipeline/", "autodesk", "toon", "poiyomi",
			"liltoon", "unitychantoon", "silent", "xiexe", "mochie", "orels", "sunao", "arktoon", "filamented", "gui/",
		};

		private static bool HasUnknownShader(Il2CppReferenceArray<Material> mats)
		{
			if (mats == null) return false;
			for (int k = 0; k < mats.Length; k++)
			{
				try
				{
					var m = mats[k];
					if (m == null || !Live(m)) continue;
					var sh = m.shader;
					string n = sh != null && Live(sh) ? (sh.name ?? "") : "";
					if (n.Length == 0) return true;   // no shader at all is not a real material
					string low = n.ToLowerInvariant();
					bool safe = false;
					for (int f = 0; f < SafeShaderFamilies.Length && !safe; f++) if (low.Contains(SafeShaderFamilies[f])) safe = true;
					if (!safe) return true;
				}
				catch { }
			}
			return false;
		}

		// Camera / Projector on an avatar: a Camera renders the whole scene again per frame (or
		// hijacks the player's view when it targets the display), a Projector re-renders every
		// receiver it touches. Neither has a legitimate use on a remote avatar, so both are
		// switched off — components stay, so a genuine "mirror camera" gimmick is just inert.
		private static int DisableCameras(GameObject root, string avatar)
		{
			int cams = 0, projs = 0;

			var cameras = root.GetComponentsInChildren<Camera>(true);
			if (cameras != null)
			{
				for (int i = 0; i < cameras.Length; i++)
				{
					try
					{
						var c = cameras[i];
						if (c == null || !c.enabled) continue;
						DisableBehaviour(c, VCameras); cams++;
					}
					catch { }
				}
			}

			var projectors = root.GetComponentsInChildren<Projector>(true);
			if (projectors != null)
			{
				for (int i = 0; i < projectors.Length; i++)
				{
					try
					{
						var p = projectors[i];
						if (p == null || !p.enabled) continue;
						DisableBehaviour(p, VCameras); projs++;
					}
					catch { }
				}
			}

			int n = cams + projs;
			if (n > 0)
				VRChatArchiveModPlugin.Logger.LogWarning($"[AntiCrash] '{avatar}' cameras: disabled {cams} Camera(s) and {projs} Projector(s).");
			return n;
		}

		private static string SafeName(GameObject go)
		{
			try { return go.name; } catch { return "?"; }
		}
	}
}
