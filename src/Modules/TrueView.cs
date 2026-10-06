using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// TRUEVIEW — keep a remote player looking like themselves after VRChat swaps their avatar
	// for the fallback proxy.
	//
	// This REPLACES the old AntiBlockModule (kept as AntiBlock.cs.bak-trueview). That one
	// Harmony-patched every bool(string) method on ModerationManager to return false, so VRChat
	// never decided to swap the avatar at all. It is one patch and it is blunt: it patches every
	// matching method on the type, including ones that answer a different question, and a build
	// where the shape of the check changes silently patches the wrong thing.
	//
	// TrueView never patches moderation. It watches the player's own hierarchy and reacts:
	//   * the moment "ForwardDirection/AvatarProxy" (the skeleton-less fallback robot) turns on,
	//     it is switched back off, so the robot is never what you see;
	//   * while the real avatar is healthy, ONE inactive clone of it is cached under the player;
	//   * when the real avatar is emptied, the cached clone is shown instead, so the player keeps
	//     their own appearance instead of becoming a robot or vanishing;
	//   * with no clone cached (you joined after the swap), VRCAvatarManager is asked once every
	//     30 s to re-download the real avatar, which restores the full bone rig and animation.
	// Nameplates, the SelectRegion laser hitbox, USpeak audio and animator culling are restored
	// alongside, each behind its own switch.
	//
	// COST. The standalone this came from ran GameObject.FindGameObjectsWithTag("Player") plus a
	// GetComponentsInChildren sweep per player EVERY frame. That is the il2cpp crossing cost that
	// this code base has already paid for once. Here the survey walks VRChatArchiveMod.Core.VaPlayers.All() with
	// a round-robin budget so each player is inspected ~4x/second, and the only per-frame work is
	// one Find on the small set of players currently known to be proxied.
	//
	// TEARDOWN. Everything we change is written to a ledger keyed by player id BEFORE it is
	// changed, and OFF walks the LEDGER, not the live player list — a player who left, or a
	// world that reloaded, must not leave a hidden AvatarProxy, a disabled positioner or an
	// orphan clone behind. See the ORPHELIN pattern in the toggle rules.
	public class TrueViewModule : IModule
	{
		public override string Name => "TrueView";

		public static bool Active { get; set; }
		public static string Status = "";

		private const float SampleHz = 4f;                 // each player inspected this often
		private const float LightTick = 1f / SampleHz;
		private const float ReloadCooldown = 30f;          // per player, between avatar re-downloads
		private const string CloneName = "Avatar_TrueViewBackup";
		private const string HealthId = "trueview";

		// What we changed on one player, so OFF can put it back. Recorded BEFORE the change.
		private sealed class Touched
		{
			public GameObject Clone;                       // we created it      -> destroy
			public Behaviour Positioner;                   // we disabled it     -> re-enable
			public GameObject Proxy;                       // we deactivated it  -> re-activate
			public readonly List<Animator> Animators = new List<Animator>();
			public readonly List<AnimatorCullingMode> Culling = new List<AnimatorCullingMode>();
			public float LastReload;
			public bool Proxied;                           // currently showing the fallback
			public bool SaidProxied, SaidShown;            // log edges, so the log stays readable
			public Vector3 PlateWant; public bool PlateWritten, SaidPlate;
			// Resolved once per player: the deep name search below is far too expensive to repeat
			// four times a second for everyone in the instance.
			public Transform Region; public bool RegionSearched, RegionFound;
			// Rig: what the per-frame pose pass reuses so it never searches the instance.
			// PlayerRoot and IKRoot belong to the player (read only); Backup is the cached copy
			// currently on screen, whose bones we write — it dies with Clone on OFF, taking the
			// pose with it, so there is nothing extra to restore.
			public Transform PlayerRoot, IKRoot, Backup;
			public Animator BackupAnimator;
		}

		private static readonly Dictionary<int, Touched> _ledger = new Dictionary<int, Touched>();
		private static readonly List<int> _gone = new List<int>();
		private static int _cursor;
		private static bool _wasOn;
		private static float _lastPrune;
		private const float PruneEvery = 10f;

		// ------------------------------------------------------------------ lifecycle

		public override void OnInitialize()
		{
			try { if (ModConfig.TrueViewEnabled != null) Active = ModConfig.TrueViewEnabled.Value; }
			catch { }
			Status = Active ? "ON" : "OFF";
		}

		public override void OnLateUpdate()
		{
			// RULE 1 — OFF is decided at the top, before any expensive work, and acts this frame.
			if (!Active)
			{
				// RULE 5 — teardown is never skipped by an early return.
				if (_wasOn)
				{
					_wasOn = false;
					RestoreAll("switched off");
					try { FeatureHealth.Idle(HealthId, "off"); } catch { }
				}
				return;                                    // RULE 4 — zero cost while off
			}

			// RULE 3 — OFF->ON edge: start the sweep on this frame instead of waiting for a tick.
			if (!_wasOn)
			{
				_wasOn = true;
				_cursor = 0;
			}

			float now = VaClock.Now;

			// 1. Per-frame, but only over players we already know are showing the fallback. That
			//    set is normally empty, and the robot must die the frame it appears.
			if (_ledger.Count > 0)
			{
				SuppressKnownProxies();
				// The copy's bones must be written every frame or it jitters at the 4 Hz survey
				// rate. LateUpdate runs after Unity's Animator, so this pose is the one drawn.
				// Free while nobody is proxied: every healthy entry is skipped on one bool.
				if (Cfg(ModConfig.TrueViewRig)) DriveKnownProxyRigs();
			}

			// 2. Round-robin survey: a slice of the instance per frame.
			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return;

			int count;
			try { count = players.Count; } catch { return; }
			if (count <= 0) return;

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
				try { if (api.isLocal) continue; } catch { continue; }
				Inspect(api, now);
			}

			// 3. Drop players who left. Without this the ledger grows for the whole session and
			//    SuppressKnownProxies walks destroyed objects every frame — in a public instance
			//    with people cycling through, that is hundreds of dead entries touched per frame.
			if (now - _lastPrune > PruneEvery)
			{
				_lastPrune = now;
				Prune(players, count);
			}
		}

		// Restore BEFORE forgetting: a player who left still had our changes applied, and an entry
		// dropped without restoring is exactly the ORPHELIN the toggle rules warn about.
		private static void Prune(System.Collections.Generic.List<VRCPlayerApi> players, int count)
		{
			_gone.Clear();
			foreach (var kv in _ledger)
			{
				bool present = false;
				for (int i = 0; i < count; i++)
				{
					VRCPlayerApi api;
					try { api = players[i]; } catch { continue; }
					if (api == null) continue;
					int pid;
					try { pid = api.playerId; } catch { continue; }
					if (pid == kv.Key) { present = true; break; }
				}
				if (!present) _gone.Add(kv.Key);
			}

			for (int i = 0; i < _gone.Count; i++)
			{
				Touched t;
				if (!_ledger.TryGetValue(_gone[i], out t)) continue;
				Undo(t);
				_ledger.Remove(_gone[i]);
			}
			_gone.Clear();
		}

		// A world change destroys every player object we touched. Restore what still exists,
		// THEN forget — never just clear, or the next OFF has nothing left to put back.
		public override void OnSceneLoaded(int buildIndex)
		{
			RestoreAll("scene load");
		}

		public override void OnShutdown()
		{
			RestoreAll("shutdown");
		}

		// ------------------------------------------------------------------ per-player work

		private static void Inspect(VRCPlayerApi api, float now)
		{
			int pid;
			try { pid = api.playerId; } catch { return; }

			GameObject go;
			try { go = api.gameObject; } catch { return; }
			if (go == null) return;

			Transform root;
			try { root = go.transform; if (!go.activeInHierarchy) return; } catch { return; }

			Touched t = Ledger(pid);
			t.PlayerRoot = root;

			// The avatar pass is the one that decides whether this player is proxied at all, and it
			// is cheap: a couple of Find calls. Everything below keys off its answer.
			if (Cfg(ModConfig.TrueViewAvatar)) RestoreAvatar(root, t, api, now, pid);

			// Cheap: one Find each, and they are the repairs that matter even mid-transition.
			if (Cfg(ModConfig.TrueViewSelectRegion)) RestoreSelectRegion(root, t);
			if (Cfg(ModConfig.TrueViewUnmute)) RestoreAudio(root);

			// EXPENSIVE — GetComponentsInChildren over the whole player and over the nameplate
			// subtree. Running these on every player in a 30-person instance four times a second
			// is what put TrueView at 15-25 ms/s in the profiler while it had nothing to restore.
			// A healthy player needs neither: VRChat is already drawing them correctly.
			bool needsWork = t.Proxied || t.Positioner != null || t.Animators.Count > 0;
			if (!needsWork) return;

			if (Cfg(ModConfig.TrueViewAnimators)) ForceAlwaysAnimate(go, t);
			if (Cfg(ModConfig.TrueViewNameplates)) RestoreNameplate(go, root, t);
		}

		// One line per real state change, per player — never per pass. Without this the log says
		// nothing about whether a clone was ever shown, which is exactly the question that matters
		// when someone reports "it looks wrong": there is no way to tell our restore from VRChat's
		// own rendering, or from another module's overlay.
		private static void Note(int pid, ref bool flag, bool now, string on, string off)
		{
			if (flag == now) return;
			flag = now;
			try { VRChatArchiveModPlugin.Logger.LogInfo("[TrueView] player " + pid + ": " + (now ? on : off)); }
			catch { }
		}

		// The fallback robot has to disappear the frame it appears, so this one check runs every
		// frame — but only for players already in the ledger, not the whole instance.
		private static void SuppressKnownProxies()
		{
			foreach (var kv in _ledger)
			{
				Touched t = kv.Value;
				GameObject proxy = t.Proxy;
				if (proxy == null) continue;
				try { if (proxy.activeSelf) proxy.SetActive(false); }
				catch { }
			}
		}

		// POSE THE COPY. A cached copy shown in place of a stripped avatar has no controller
		// running on it, so left alone it stands frozen in whatever pose it was cloned in. VRChat
		// still moves the PLAYER's IK effectors, though — they hang off the player rig, not the
		// avatar, so they outlive the swap — and they are exactly what the real avatar would have
		// tracked. Copying them onto the copy's humanoid bones makes it walk, turn, reach and sit
		// with the player. Ported from TrueView Standalone 1.4.2 (its "Dynamic IK Rig"), which ran
		// this over every player found by tag, every frame; here it runs over the ledger only, and
		// only for players currently showing a copy.
		private static void DriveKnownProxyRigs()
		{
			foreach (var kv in _ledger)
			{
				Touched t = kv.Value;
				if (!t.Proxied || t.Backup == null || t.PlayerRoot == null) continue;
				try { DriveRig(t); } catch { }
			}
		}

		private static void DriveRig(Touched t)
		{
			Transform root = t.PlayerRoot;
			Transform copy = t.Backup;

			Transform ik = t.IKRoot;
			if (ik == null) { ik = root.Find("AnimationController/HeadAndHandIK"); t.IKRoot = ik; }
			if (ik == null) return;

			Transform hip = ik.Find("HipTarget");
			Animator anim = t.BackupAnimator;

			// Non-humanoid copy: there are no bones to solve, so at least keep it standing where
			// the player is instead of where it was cloned.
			if (anim == null || !anim.isHuman)
			{
				Transform hips = copy.Find("Armature/Hips");
				if (hips != null && hip != null) { hips.position = hip.position; hips.rotation = hip.rotation; }
				return;
			}

			if (!anim.enabled) anim.enabled = true;
			if (anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
				anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

			Transform b = Bone(anim, HumanBodyBones.Hips);
			if (b != null && hip != null) { b.position = hip.position; b.rotation = hip.rotation; }

			Transform chest = ik.Find("ChestTarget");
			if (chest != null)
			{
				b = Bone(anim, HumanBodyBones.Chest) ?? Bone(anim, HumanBodyBones.Spine);
				if (b != null) b.rotation = chest.rotation;
			}

			Transform head = ik.Find("HeadEffector");
			if (head != null) { b = Bone(anim, HumanBodyBones.Head); if (b != null) b.rotation = head.rotation; }

			Place(anim, HumanBodyBones.LeftHand, ik.Find("LeftEffector"));
			Place(anim, HumanBodyBones.RightHand, ik.Find("RightEffector"));

			// Hips riding low over the player root means seated or crouched. Fold the legs into a
			// sit and leave the feet alone: foot IK under a seated body only fights the pose.
			float hipRel = hip != null ? hip.position.y - root.position.y : 1f;
			if (hipRel < 0.7f)
			{
				Transform lUp = Bone(anim, HumanBodyBones.LeftUpperLeg), rUp = Bone(anim, HumanBodyBones.RightUpperLeg);
				Transform lLo = Bone(anim, HumanBodyBones.LeftLowerLeg), rLo = Bone(anim, HumanBodyBones.RightLowerLeg);
				if (lUp != null && rUp != null)
				{
					lUp.localRotation = Quaternion.Euler(75f, -5f, 0f);
					rUp.localRotation = Quaternion.Euler(75f, 5f, 0f);
				}
				if (lLo != null && rLo != null)
				{
					lLo.localRotation = Quaternion.Euler(-80f, 0f, 0f);
					rLo.localRotation = Quaternion.Euler(-80f, 0f, 0f);
				}
				return;
			}

			Place(anim, HumanBodyBones.LeftFoot, ik.Find("LeftFootTarget"));
			Place(anim, HumanBodyBones.RightFoot, ik.Find("RightFootTarget"));
		}

		// Bone takes the effector's position AND rotation.
		private static void Place(Animator anim, HumanBodyBones bone, Transform target)
		{
			if (target == null) return;
			Transform b = Bone(anim, bone);
			if (b == null) return;
			b.position = target.position;
			b.rotation = target.rotation;
		}

		private static Transform Bone(Animator a, HumanBodyBones bone)
		{
			try { return a.GetBoneTransform(bone); } catch { return null; }
		}

		private static void RestoreAvatar(Transform root, Touched t, VRCPlayerApi api, float now, int pid)
		{
			try
			{
				Transform fwd = root.Find("ForwardDirection");
				if (fwd == null) return;

				// The fallback robot: remember it once (so OFF can bring it back), then keep it off.
				Transform proxy = fwd.Find("AvatarProxy");
				if (proxy != null)
				{
					if (t.Proxy == null) t.Proxy = proxy.gameObject;
					try { if (proxy.gameObject.activeSelf) proxy.gameObject.SetActive(false); } catch { }
				}

				Transform avatar = fwd.Find("Avatar");
				Transform backup = fwd.Find(CloneName);

				// A clone taken mid-download is a beam placeholder, not the player. Drop it.
				if (backup != null && backup.Find("part_Beams") != null)
				{
					try { UnityEngine.Object.Destroy(backup.gameObject); } catch { }
					if (t.Clone == backup.gameObject) t.Clone = null;
					backup = null;
				}

				if (avatar == null) return;

				bool placeholder = avatar.Find("part_Beams") != null;
				bool healthy = avatar.childCount > 0 && !placeholder && LooksReal(avatar);

				if (healthy)
				{
					t.Proxied = false;
					t.Backup = null; t.BackupAnimator = null;   // the real avatar animates itself
					t.SaidShown = false;                   // a later swap gets its own line
					t.PlateWritten = false; t.SaidPlate = false;
					Note(pid, ref t.SaidProxied, false, "", "real avatar is back");

					if (!avatar.gameObject.activeSelf) avatar.gameObject.SetActive(true);
					ShowRenderers(avatar);

					if (backup != null && backup.gameObject.activeSelf)
						backup.gameObject.SetActive(false);

					// Cache exactly one inactive copy of the real avatar.
					if (backup == null && t.Clone == null)
					{
						try
						{
							GameObject clone = UnityEngine.Object.Instantiate(avatar.gameObject, fwd);
							clone.name = CloneName;
							clone.transform.localPosition = Vector3.zero;
							clone.transform.localRotation = Quaternion.identity;
							clone.SetActive(false);
							t.Clone = clone;                       // recorded the moment it exists
						}
						catch { }
					}
					return;
				}

				// The real avatar is empty or still a placeholder.
				t.Proxied = true;
				Note(pid, ref t.SaidProxied, true, "avatar replaced by the fallback", "");

				// Is the cached copy still drawable? Measured answer, from a live instance: NO.
				//
				//     player 291: showing the cached copy — 72 renderer(s), 93 dead material(s)
				//
				// 72 renderers is a real avatar, cached correctly while it was healthy. Every one
				// of its 93 materials was destroyed by the time it was needed: VRChat does not
				// merely hide the avatar, it unloads the assets behind it, and a clone holds
				// references, not copies. Showing it anyway is what painted people magenta.
				//
				// So a copy that can no longer be drawn is dropped instead of shown. VRChat's own
				// stand-in is not what you wanted to see, but it beats a magenta silhouette, and
				// the re-request below is the path that can actually bring the person back.
				if (backup != null && !LooksReal(backup))
				{
					try { UnityEngine.Object.Destroy(backup.gameObject); } catch { }
					if (t.Clone == backup.gameObject) t.Clone = null;
					backup = null;
					t.Backup = null; t.BackupAnimator = null;
					Note(pid, ref t.SaidShown, true,
						"cached copy dropped — VRChat unloaded its materials, it would only draw magenta", "");
				}

				if (backup != null && !backup.gameObject.activeSelf)
				{
					backup.gameObject.SetActive(true);
					backup.localPosition = Vector3.zero;
					backup.localRotation = Quaternion.identity;
					ShowRenderers(backup);
					try
					{
						var anim = backup.GetComponent<Animator>();
						if (anim != null)
						{
							anim.enabled = true;
							anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
						}
					}
					catch { }
					RepairShaders(backup, pid, ref t.SaidShown);
				}

				// Hand the copy on screen to the per-frame pose pass. Its Animator is resolved
				// here, at survey rate, so the pose pass never pays a GetComponent per frame.
				if (backup != null && backup.gameObject.activeSelf && t.Backup != backup)
				{
					t.Backup = backup;
					t.BackupAnimator = null;
					try { t.BackupAnimator = backup.GetComponent<Animator>(); } catch { }
				}

				// ALWAYS ask for the real avatar back, cached copy or not.
				//
				// The first build only asked when there was no clone — so the clone always won, and
				// the clone is a copy of a hierarchy whose materials VRChat unloads when it swaps
				// the avatar. A material whose shader has been unloaded draws with Unity's error
				// shader: the player becomes a magenta silhouette, which is not "seeing them" in
				// any useful sense. The clone is a stopgap for the first seconds; the real avatar
				// coming back is the actual goal, and once it does the branch above hides the clone.
				if (now - t.LastReload <= ReloadCooldown) return;
				t.LastReload = now;
				ReloadAvatar(api);
			}
			catch { }
		}

		// Ask VRChat to load this player's REAL avatar onto them.
		//
		// This is the path that covers the case a cached clone cannot: you joined an instance where
		// the swap had already happened, so there was never a healthy avatar to copy. It works
		// because the avatar record survives the swap — the mod's own Force Clone reads the same
		// record off the same player and switches YOU into their avatar, which is only possible if
		// the id and the ApiAvatar are still there. Same record, asked to go the other way.
		//
		// Resolution is by TYPE and by SHAPE, never by obfuscated name: the field that holds the
		// record and the method that consumes it are renamed on every VRChat build, which is why
		// FewTagsModule's resolvers exist and why the method is picked by its signature instead.
		private static MethodInfo _loadMethod;
		private static bool _loadResolved;
		private static bool _saidNoRecord, _saidNoManager, _saidNoMethod;

		private static void ReloadAvatar(VRCPlayerApi api)
		{
			try
			{
				GameObject go = api.gameObject;
				if (go == null) return;

				// Read the record off the SAME object Force Clone reads it from.
				//
				// Two earlier attempts failed here, and the log said why: ProxyGuard refuses member
				// access on VRCPlayer for this build ("ses membres ne peuvent pas etre places sur ce
				// build"), so anything hanging off that type comes back null. Force Clone never
				// touches VRCPlayer — it works on the objects FewTagsModule.EnumeratePlayers yields,
				// and it demonstrably succeeds on a player who has blocked you. Same object, same
				// resolver, matched to this player by id.
				object player = PlayerObjectFor(api);
				if (player == null)
				{
					Once(ref _saidNoRecord, "could not match this player to VRChat's own player list — "
						+ "cannot ask for their avatar");
					return;
				}

				object record = FewTagsModule.GetMemberByTypeName(player, "ApiAvatar",
					"prop_ApiAvatar_0", "field_Private_ApiAvatar_0");
				if (record == null)
				{
					object vp2 = FewTagsModule.GetMemberByTypeName(player, "VRCPlayer",
						"_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
					if (vp2 != null)
						record = FewTagsModule.GetMemberByTypeName(vp2, "ApiAvatar",
							"prop_ApiAvatar_0", "field_Private_ApiAvatar_0");
				}
				if (record == null)
				{
					Once(ref _saidNoRecord, "their avatar record is not readable on this build — "
						+ "nothing to re-request (the cached copy still covers a swap you witnessed)");
					return;
				}

				string id = null;
				try { id = FewTagsModule.GetMember(record, "id") as string; } catch { }

				object manager = FewTagsModule.GetMemberByTypeName(player, "VRCAvatarManager",
					"prop_VRCAvatarManager_0", "field_Private_VRCAvatarManager_0");
				if (manager == null)
				{
					Once(ref _saidNoManager, "their VRCAvatarManager is not readable on this build");
					return;
				}

				MethodInfo load = LoadMethod(manager.GetType());
				if (load == null)
				{
					Once(ref _saidNoMethod, "no avatar-load entry point found on VRCAvatarManager");
					return;
				}

				load.Invoke(manager, new object[] { record });
				try
				{
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[TrueView] asked VRChat to load the real avatar " + (id ?? "(id unreadable)")
						+ " for player " + api.playerId);
				}
				catch { }
			}
			catch { }
		}

		// VRChat's own player object for this VRCPlayerApi, matched by player id. Walked on demand
		// only — this runs behind the 30 s reload throttle, never on the survey path.
		private static object PlayerObjectFor(VRCPlayerApi api)
		{
			int want;
			try { want = api.playerId; } catch { return null; }
			try
			{
				foreach (object p in FewTagsModule.EnumeratePlayers())
				{
					if (p == null) continue;
					object papi = FewTagsModule.GetMemberByTypeName(p, "VRCPlayerApi",
						"prop_VRCPlayerApi_0", "field_Public_VRCPlayerApi_0");
					if (papi == null) continue;
					object pid = FewTagsModule.GetMember(papi, "playerId");
					if (pid is int got && got == want) return p;
				}
			}
			catch { }
			return null;
		}

		// The one instance method that takes a single ApiAvatar. VRChat's own avatar-switch entry.
		private static MethodInfo LoadMethod(Type managerType)
		{
			if (_loadResolved) return _loadMethod;
			_loadResolved = true;
			try
			{
				const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
				MethodInfo fallback = null;
				foreach (var m in managerType.GetMethods(F))
				{
					var ps = m.GetParameters();
					if (ps.Length != 1) continue;
					string pt = ps[0].ParameterType.Name;
					if (pt.IndexOf("ApiAvatar", StringComparison.Ordinal) < 0) continue;

					// Prefer the fire-and-forget UniTaskVoid entry — the one VRChat itself uses to
					// put an avatar on a player. Anything else of the same shape is the backup.
					if (m.ReturnType.Name.IndexOf("UniTaskVoid", StringComparison.Ordinal) >= 0)
					{
						_loadMethod = m;
						break;
					}
					if (fallback == null) fallback = m;
				}
				if (_loadMethod == null) _loadMethod = fallback;
			}
			catch { _loadMethod = null; }
			return _loadMethod;
		}

		// Say a structural problem once, not four times a second.
		private static void Once(ref bool said, string msg)
		{
			if (said) return;
			said = true;
			try { VRChatArchiveModPlugin.Logger.LogWarning("[TrueView] " + msg); } catch { }
		}

		// A clone copies component references, not assets. When VRChat swaps the avatar it unloads
		// the bundle behind it, and a Material whose Shader has gone draws with Unity's error
		// shader — the magenta silhouette. Nothing can bring the original shading back once the
		// asset is gone, so this only stops it being magenta: the material is rebuilt on Standard,
		// carrying over the texture and colour if those survived. It is a stopgap with a honest
		// ceiling, which is why the real avatar is re-requested in parallel.
		private static void RepairShaders(Transform clone, int pid, ref bool said)
		{
			try
			{
				var rs = clone.GetComponentsInChildren<Renderer>(true);
				if (rs == null) return;

				Shader std = null;
				int broken = 0, rebuilt = 0;

				for (int i = 0; i < rs.Length; i++)
				{
					var r = rs[i];
					if (r == null) continue;

					Material[] mats;
					try { mats = r.sharedMaterials; } catch { continue; }
					if (mats == null) continue;

					bool touched = false;
					for (int m = 0; m < mats.Length; m++)
					{
						var mat = mats[m];
						if (mat == null) { broken++; continue; }

						Shader sh = null;
						try { sh = mat.shader; } catch { }
						bool dead = sh == null;
						if (!dead)
						{
							string n = null;
							try { n = sh.name; } catch { }
							dead = string.IsNullOrEmpty(n) || n.IndexOf("InternalError", StringComparison.Ordinal) >= 0;
						}
						if (!dead) continue;

						broken++;
						if (std == null) { try { std = Shader.Find("Standard"); } catch { } }
						if (std == null) continue;

						try
						{
							// A fresh material, so the dead shared asset is never written to.
							var fix = new Material(std);
							try { if (mat.mainTexture != null) fix.mainTexture = mat.mainTexture; } catch { }
							try { fix.color = mat.color; } catch { }
							mats[m] = fix;
							touched = true;
							rebuilt++;
						}
						catch { }
					}
					if (touched) { try { r.materials = mats; } catch { } }
				}

				if (!said)
				{
					said = true;
					try
					{
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[TrueView] player " + pid + ": showing the cached copy — "
							+ rs.Length + " renderer(s), " + broken + " dead material(s), " + rebuilt + " rebuilt");
					}
					catch { }
					DumpOnce(clone, pid);
				}
			}
			catch { }
		}

		// ONE detailed dump per session, for the first restored player. Two rounds of reasoning
		// about the magenta from screenshots produced two wrong answers; this prints what is
		// actually on the renderers so the next round is decided by data instead.
		private static bool _dumped;

		private static void DumpOnce(Transform clone, int pid)
		{
			if (_dumped) return;
			_dumped = true;
			try
			{
				Shader std = null;
				try { std = Shader.Find("Standard"); } catch { }
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[TrueView] shader probe for player " + pid + " — Shader.Find(\"Standard\") = "
					+ (std == null ? "NULL (stripped from this build: nothing to rebuild onto)" : std.name));

				var rs = clone.GetComponentsInChildren<Renderer>(true);
				if (rs == null) return;
				int shown = 0;
				for (int i = 0; i < rs.Length && shown < 8; i++)
				{
					var r = rs[i];
					if (r == null) continue;
					string line = "    " + r.GetIl2CppType().Name + " '" + r.name + "' enabled=" + r.enabled;
					Material[] mats = null;
					try { mats = r.sharedMaterials; } catch { }
					if (mats == null) { line += " materials=<unreadable>"; }
					else
					{
						line += " mats=" + mats.Length + " [";
						for (int m = 0; m < mats.Length && m < 4; m++)
						{
							var mat = mats[m];
							if (m > 0) line += ", ";
							if (mat == null) { line += "<destroyed material>"; continue; }
							Shader sh = null;
							try { sh = mat.shader; } catch { }
							if (sh == null) line += "<destroyed shader>";
							else { try { line += sh.name; } catch { line += "<unreadable>"; } }
						}
						line += "]";
					}
					VRChatArchiveModPlugin.Logger.LogInfo(line);
					shown++;
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[TrueView] (" + rs.Length + " renderer(s) total)");
			}
			catch (Exception e)
			{
				try { VRChatArchiveModPlugin.Logger.LogWarning("[TrueView] shader probe failed: " + e.Message); } catch { }
			}
		}

		// Is this subtree a REAL loaded avatar, or VRChat's stand-in for one?
		//
		// This is the test the first build did not have, and its absence is what produced the
		// magenta. "childCount > 0 and no loading beams" is true of the stand-in too, so the
		// stand-in was cached AS the real avatar and later shown back — a single SkinnedMeshRenderer
		// named Body whose material VRChat had already unloaded, which is exactly what the probe
		// found: "1 renderer(s), [<destroyed material>]", and exactly what a magenta blob is.
		//
		// A genuinely loaded avatar has at least one renderer holding a live material on a live
		// shader. That is not a heuristic about names or counts — it is the thing that makes the
		// difference between something that can be drawn and something that cannot.
		private static bool LooksReal(Transform avatar)
		{
			try
			{
				var rs = avatar.GetComponentsInChildren<Renderer>(true);
				if (rs == null || rs.Length == 0) return false;

				for (int i = 0; i < rs.Length; i++)
				{
					var r = rs[i];
					if (r == null) continue;
					Material[] mats;
					try { mats = r.sharedMaterials; } catch { continue; }
					if (mats == null) continue;
					for (int m = 0; m < mats.Length; m++)
					{
						var mat = mats[m];
						if (mat == null) continue;
						Shader sh = null;
						try { sh = mat.shader; } catch { }
						if (sh == null) continue;
						string n = null;
						try { n = sh.name; } catch { }
						if (string.IsNullOrEmpty(n)) continue;
						if (n.IndexOf("InternalError", StringComparison.Ordinal) >= 0) continue;
						return true;                       // one drawable surface is enough
					}
				}
			}
			catch { }
			return false;
		}

		private static void ShowRenderers(Transform t)
		{
			try
			{
				var rs = t.GetComponentsInChildren<Renderer>(true);
				if (rs == null) return;
				for (int i = 0; i < rs.Length; i++)
				{
					var r = rs[i];
					if (r == null) continue;
					if (!r.enabled) r.enabled = true;
					if (r.forceRenderingOff) r.forceRenderingOff = false;
				}
			}
			catch { }
		}

		// CLICKING THEM BACK ON. SelectRegion is the capsule VRChat raycasts to decide whether your
		// laser or your cursor is pointing at a player; with it gone you cannot select a proxied
		// player at all, which takes the user menu, the mute slider and every per-user action with it.
		//
		// Three things were wrong with the first version and all three are why it did nothing:
		// it looked for SelectRegion as a DIRECT child only (one Find, one depth), it enabled the
		// FIRST collider and ignored the rest, and it said nothing when it found nothing — so a
		// path that moved on this build was indistinguishable from a feature that was working.
		private static bool _saidNoRegion, _saidRegion;

		private static void RestoreSelectRegion(Transform root, Touched t)
		{
			try
			{
				// Remembered from last time if it is still alive.
				Transform sr = t.Region;
				if (sr == null)
				{
					// A region we HAD and that has since been destroyed (an avatar reload takes it with
					// it) must be looked for again. Only a search that genuinely found nothing is allowed
					// to stop future searches — otherwise one unlucky moment makes the player permanently
					// unclickable and nothing ever tries again.
					if (t.RegionFound) { t.RegionFound = false; t.RegionSearched = false; }
					else if (t.RegionSearched) return;
					sr = root.Find("SelectRegion");
				}
				if (sr == null)
				{
					// Then anywhere below, by name. The capsule is parented differently depending on
					// what VRChat has torn down around it.
					var kids = root.GetComponentsInChildren<Transform>(true);
					for (int i = 0; kids != null && i < kids.Length; i++)
					{
						var k = kids[i];
						if (k == null) continue;
						string n = k.name;
						if (string.IsNullOrEmpty(n)) continue;
						if (n.IndexOf("SelectRegion", StringComparison.OrdinalIgnoreCase) < 0) continue;
						sr = k; break;
					}
				}

				t.RegionSearched = true;
				if (sr == null)
				{
					if (!_saidNoRegion)
					{
						_saidNoRegion = true;
						try
						{
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[TrueView] no SelectRegion under a remote player on this build — "
								+ "clicking a proxied player cannot be restored by this route.");
						}
						catch { }
					}
					return;
				}

				t.Region = sr; t.RegionFound = true;
				if (!sr.gameObject.activeSelf) sr.gameObject.SetActive(true);

				// EVERY collider on it, not the first. A capsule plus a trigger is normal, and
				// enabling one of the two leaves the player just as unclickable as before.
				int on = 0;
				var cols = sr.GetComponentsInChildren<Collider>(true);
				for (int i = 0; cols != null && i < cols.Length; i++)
				{
					var c = cols[i];
					if (c == null) continue;
					if (!c.enabled) { c.enabled = true; on++; }
					if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
				}

				if (!_saidRegion)
				{
					_saidRegion = true;
					try
					{
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[TrueView] SelectRegion kept clickable on '" + sr.name + "' ("
							+ (cols == null ? 0 : cols.Length) + " collider(s), " + on + " re-enabled).");
					}
					catch { }
				}
			}
			catch { }
		}

		private static void RestoreAudio(Transform root)
		{
			try
			{
				Transform us = root.Find("AnimationController/HeadAndHandIK/HeadEffector/USpeak");
				if (us == null) return;
				var a = us.GetComponent<AudioSource>();
				if (a != null && a.mute) a.mute = false;
			}
			catch { }
		}

		// AlwaysAnimate keeps a proxied player's pose moving instead of frozen. It also costs CPU
		// for as long as it is set, so the previous mode is recorded and restored on OFF.
		private static void ForceAlwaysAnimate(GameObject go, Touched t)
		{
			try
			{
				var anims = go.GetComponentsInChildren<Animator>(true);
				if (anims == null) return;
				for (int i = 0; i < anims.Length; i++)
				{
					var a = anims[i];
					if (a == null) continue;
					if (!a.enabled) a.enabled = true;
					if (a.cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
					t.Animators.Add(a);                            // record before changing
					t.Culling.Add(a.cullingMode);
					a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
				}
			}
			catch { }
		}

		private static void RestoreNameplate(GameObject go, Transform root, Touched t)
		{
			try
			{
				GameObject container = NameplateContainer(go, out Behaviour positioner);
				if (container == null) return;

				if (!container.activeSelf) container.SetActive(true);

				Transform plate = container.transform.Find("PlayerNameplate");
				if (plate != null && !plate.gameObject.activeSelf) plate.gameObject.SetActive(true);

				// THE POSITIONER STAYS ON. THE POSITION IS CORRECTED AFTER IT.
				//
				// Two failures taught this, in order. The port first parked VRChat's positioner and
				// placed the plate itself — and produced GIANT nameplates, because the positioner is
				// what SCALES a plate by its distance so its apparent size stays constant. Park it
				// and the scale freezes at whatever distance it last saw: a plate sized for
				// seventeen metres, drawn at two.
				//
				// Removing the placement altogether fixed the size and broke the other half: with no
				// avatar there is no head bone for the positioner to sit above, so the plate stops
				// following the player.
				//
				// Both are satisfied by never touching `enabled` and never touching scale: let the
				// positioner run and keep owning the size and the billboarding, then correct only
				// the WORLD POSITION afterwards. Scale is VRChat's, placement is ours, and nothing
				// is left in a state that needs restoring.
				if (t.Positioner != null)
				{
					try { t.Positioner.enabled = true; } catch { }   // undo the old build's parking
					t.Positioner = null;
				}

				if (t.Proxied)
				{
					Transform moved = plate != null ? plate : container.transform;
					Vector3 p = root.position;

					// Head height from whatever is left. The head bone goes with the avatar, so the
					// fallback is the player's own capsule height rather than a fixed 1.75 m that
					// would float over a short avatar and sit inside a tall one.
					float y;
					Transform head = root.Find("AnimationController/HeadAndHandIK/HeadEffector");
					if (head != null) y = head.position.y + 0.35f;
					else
					{
						float h = 1.6f;
						try
						{
							var cc = go.GetComponent<CharacterController>();
							if (cc != null && cc.height > 0.2f) h = cc.height;
						}
						catch { }
						y = p.y + h + 0.25f;
					}

					// DID OUR WRITE SURVIVE THE FRAME? Script execution order between this module and
					// VRChat's own positioner is not ours to set: if theirs runs after ours, our position
					// is overwritten and the plate still will not follow. Rather than ship that as an
					// unknown, the previous frame's target is compared against where the plate actually
					// is, and a loss is said ONCE — the same read-back the rotator does for the rig.
					if (t.PlateWritten && !t.SaidPlate
						&& (moved.position - t.PlateWant).sqrMagnitude > 0.04f)
					{
						t.SaidPlate = true;
						try
						{
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[TrueView] nameplate write does not hold — VRChat's positioner runs after us "
								+ "and puts it back; the plate cannot be made to follow from LateUpdate on this build.");
						}
						catch { }
					}

					Vector3 target = new Vector3(p.x, y, p.z);
					moved.position = target;
					t.PlateWant = target; t.PlateWritten = true;
				}

				ForceVisible(container);
			}
			catch { }
		}

		private static void ForceVisible(GameObject container)
		{
			try
			{
				var groups = container.GetComponentsInChildren<CanvasGroup>(true);
				if (groups != null)
				{
					for (int i = 0; i < groups.Length; i++)
					{
						var cg = groups[i];
						if (cg == null) continue;
						if (cg.alpha < 1f) cg.alpha = 1f;
						cg.blocksRaycasts = true;
						cg.interactable = true;
					}
				}
				var canvases = container.GetComponentsInChildren<Canvas>(true);
				if (canvases != null)
				{
					for (int i = 0; i < canvases.Length; i++)
					{
						var c = canvases[i];
						if (c != null && !c.enabled) c.enabled = true;
					}
				}
			}
			catch { }
		}

		// The positioner field is obfuscated and renamed on VRChat builds, so the direct field is
		// tried first and a name search is the fallback — the same defence FewTagsModule uses.
		private static GameObject NameplateContainer(GameObject go, out Behaviour positioner)
		{
			positioner = null;
			try
			{
				var vp = go.GetComponent<VRCPlayer>();
				if (vp != null)
				{
					try
					{
						var p = vp.field_Public_PlayerNameplatePositioner_0;
						if (p != null && p.gameObject != null)
						{
							positioner = p;
							return p.gameObject;
						}
					}
					catch { }
				}
			}
			catch { }

			try
			{
				var kids = go.GetComponentsInChildren<Transform>(true);
				if (kids == null) return null;
				for (int i = 0; i < kids.Length; i++)
				{
					var k = kids[i];
					if (k == null) continue;
					string n = k.name;
					if (!string.IsNullOrEmpty(n) && n.Contains("Nameplate")) return k.gameObject;
				}
			}
			catch { }
			return null;
		}

		// ------------------------------------------------------------------ ledger / teardown

		private static Touched Ledger(int pid)
		{
			Touched t;
			if (!_ledger.TryGetValue(pid, out t))
			{
				t = new Touched();
				_ledger[pid] = t;
			}
			return t;
		}

		// Walks the LEDGER, not the live players: someone who left mid-session still has our
		// clone parented under a hierarchy Unity has not torn down yet.
		// Put one player's entry back the way it was. Every step is independently guarded: a
		// destroyed object must not stop the remaining steps from running.
		private static void Undo(Touched t)
		{
			if (t == null) return;

			if (t.Clone != null)
			{
				try { UnityEngine.Object.Destroy(t.Clone); } catch { }
				t.Clone = null;
			}
			if (t.Positioner != null)
			{
				try { t.Positioner.enabled = true; } catch { }
				t.Positioner = null;
			}
			if (t.Proxy != null)
			{
				try { if (!t.Proxy.activeSelf) t.Proxy.SetActive(true); } catch { }
				t.Proxy = null;
			}
			for (int i = 0; i < t.Animators.Count; i++)
			{
				var a = t.Animators[i];
				if (a == null) continue;
				try { a.cullingMode = t.Culling[i]; } catch { }
			}
			t.Animators.Clear();
			t.Culling.Clear();
		}

		private static void RestoreAll(string why)
		{
			int n = _ledger.Count;
			foreach (var kv in _ledger) Undo(kv.Value);

			_ledger.Clear();
			_cursor = 0;

			try
			{
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[TrueView] restored (" + why + "): " + n + " player entry/entries put back");
			}
			catch { }
		}

		// ------------------------------------------------------------------ toggle

		public static void Toggle()
		{
			if (ModConfig.TrueViewEnabled != null)
			{
				ModConfig.TrueViewEnabled.Value = !ModConfig.TrueViewEnabled.Value;
				Active = ModConfig.TrueViewEnabled.Value;
			}
			else
			{
				Active = !Active;
			}

			Status = Active ? "ON — real avatars preserved" : "OFF";
			try { VRChatArchiveModPlugin.Logger.LogInfo("[TrueView] " + Status); } catch { }
		}

		private static bool Cfg(BepInEx.Configuration.ConfigEntry<bool> e)
		{
			try { return e == null || e.Value; } catch { return true; }
		}
	}
}
