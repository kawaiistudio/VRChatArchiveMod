using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// OBJECT ORBIT — rips the loose props out of the world and spins them in a ring around you, or
	// around somebody else. A gag, nothing more.
	//
	// TWO MODES, and the difference is who can see it.
	//
	// LOCAL (default) — writes the objects' Transforms on this client only. Nobody else sees a
	// thing. Works on any prop in the world, because nothing is being synchronised.
	//
	// SYNCED — only touches real VRC_Pickup objects, takes ownership through the SDK's own
	// Networking.SetOwner, and lets VRChat broadcast the position the way it already does when a
	// player carries something. Everyone sees it.
	//
	// The line between "automating what a player can do by hand" and griefing is thin, so synced
	// mode is fenced in deliberately:
	//   * pickups ONLY — objects the world author made grabbable. World geometry, doors, seats and
	//     anything else load-bearing is never touched, whatever its size.
	//   * every object's original position is restored on stop, so the room is left as it was found.
	//   * it is OFF by default and the menu states plainly that other people will see it.
	// Objects held by somebody else are skipped: taking something out of a person's hands is not
	// the same joke.
	//
	// Everything taken is put back: the original position, rotation and parent of every object are
	// recorded on pickup and restored on stop, on world change, and on shutdown. Without that, a
	// session that ended mid-spin would leave the world visibly wrong until you rejoined.
	public class ObjectOrbitModule : IModule
	{
		public override string Name => "ObjectOrbit";

		private sealed class Held
		{
			public Transform T;
			public GameObject Go;      // kept so liveness can be checked without touching T
			public Vector3 Pos;
			public Quaternion Rot;
			public Transform Parent;
			public float Phase;      // where it sits in the ring
			public float Tilt;       // its own height in the ring
			public Vector3 Spin;     // per-object tumble, so the ring is not a carousel of statues
			public Rigidbody Body;   // frozen kinematic while it orbits so physics stops fighting us
			public bool WasKinematic;
			public RigidbodyInterpolation WasInterp;
		}

		private static readonly List<Held> Held0 = new List<Held>();
		private static Transform _center;
		private static float _angle;
		private static bool _synced;          // captured at Start: changing it mid-spin would leave
		                                      // half the ring owned and half not
		private static float _nextOwn;

		public static bool Active { get; private set; }
		public static string CenterName { get; private set; } = "";
		public static int Count => Held0.Count;

		// Around the local player.
		public static void ToggleOnSelf()
		{
			if (Active) { Stop("objects put back"); return; }
			var t = PlayerRef.LocalTransform();
			if (t == null) { VaTagsModule.LastStatus = "you are not in a world yet"; return; }
			Start(t, "you");
		}

		public static void ToggleOnPlayer(VaTagsModule.PlayerEntry entry)
		{
			if (Active && entry != null && CenterName == entry.Name) { Stop("objects put back"); return; }
			if (entry?.Transform == null) { VaTagsModule.LastStatus = "can't reach that player yet"; return; }
			Start(entry.Transform, entry.Name);
		}

		internal static void Start(Transform center, string label)
		{
			Stop(null);
			_center = center;
			CenterName = label ?? "";

			try { _synced = ModConfig.ObjOrbitSynced.Value; } catch { _synced = false; }

			int owned = 0;
			// HOW MANY. Count <= 0 means ALL. But synced grabs are a NETWORK cost — every object is
			// owned and its position broadcast to everyone, so an uncapped synced ring floods the
			// instance (the pickup-spam grief). Local grabs cost nothing: they move only on your screen.
			// So LOCAL is effectively unlimited; SYNCED is capped to protect the room.
			int cfgCount = 0;
			try { cfgCount = ModConfig.ObjOrbitCount.Value; } catch { }
			// NOT capped low. A 4-object cap was tried on 2026-09-02 on the theory that a synced ring
			// starved the send queue before a ServerTimeout; EvilEye's ItemOrbit owns and moves EVERY
			// SYNCED IS BOUNDED, LOCAL IS NOT. NetSend proved it on 2026-09-02: a synced ring of many
			// objects sent ~240 events/s outbound (each owned object is its own VRC_ObjectSync stream)
			// and timed the connection out to the home world. EvilEye orbits hundreds, but LOCALLY -- no
			// ownership, no broadcast. So synced is capped low and throttled (see OnUpdate); local stays
			// unlimited because it costs no network at all.
			// 0 = ALL (the owner's default): local takes every loose object it can find (up to 5000);
			// synced takes the network ceiling of 40 — each owned object is its own ObjectSync stream at
			// ~10 Hz, and 24 of them once timed the connection out, so that ceiling is not a preference.
			int want = _synced
				? Mathf.Clamp(cfgCount <= 0 ? 40 : cfgCount, 1, 40)
				: Mathf.Clamp(cfgCount <= 0 ? 5000 : cfgCount, 1, 5000);
			// 0 = unlimited (whole world); otherwise at least 2 m so the ring is not just what you stand on.
			float range = ModConfig.ObjOrbitRange.Value <= 0f ? 100000f : Mathf.Max(2f, ModConfig.ObjOrbitRange.Value);
			var picked = Collect(center.position, range, want);
			if (picked.Count == 0)
			{
				VaTagsModule.LastStatus = "no loose objects within " + (int)range + "m to play with";
				_center = null;
				return;
			}

			for (int i = 0; i < picked.Count; i++)
			{
				var t = picked[i];
				var h = new Held
				{
					T = t,
					Go = t.gameObject,
					Pos = t.position,
					Rot = t.rotation,
					Parent = t.parent,
					Phase = (360f / picked.Count) * i,
					// Spread them over a few heights so it reads as a swarm, not a flat disc.
					Tilt = ((i % 5) - 2) * 0.45f,
					Spin = new Vector3(23f + (i * 7 % 40), 41f + (i * 11 % 60), 17f + (i * 5 % 30)),
				};
				// A loose prop's Rigidbody keeps obeying gravity and collisions, so it fights our
				// per-frame transform writes and jitters. Freeze it while it orbits; Stop restores it.
				try
				{
					h.Body = t.GetComponent<Rigidbody>() ?? t.GetComponentInChildren<Rigidbody>();
					if (h.Body != null)
					{
						h.WasKinematic = h.Body.isKinematic; h.Body.isKinematic = true;
						// Interpolated, so the body reports a velocity as it is moved with MovePosition
						// below; that velocity is what ObjectSync sends and what lets other clients
						// dead-reckon a smooth arc instead of stepping between samples.
						h.WasInterp = h.Body.interpolation; h.Body.interpolation = RigidbodyInterpolation.Interpolate;
					}
				}
				catch { }
				if (_synced && TakeOwnershipCounted(h.Go)) owned++;
				Held0.Add(h);
			}

			Active = true;
			_angle = 0f;
			// Say what is REALLY shared. With "Synced" on, only the objects that are actually
			// networked can be seen by anyone else — scenery cannot be, whatever the setting says.
			VaTagsModule.LastStatus = _synced && owned > 0
				? $"{Held0.Count} object(s) orbiting {CenterName} — {owned} of them EVERYONE sees"
				: _synced
					? $"{Held0.Count} object(s) orbiting {CenterName} — none are networked, so only YOU see this"
					: $"{Held0.Count} object(s) orbiting {CenterName} — only YOU can see this";
			VRChatArchiveModPlugin.Logger.LogInfo(
				$"[ObjectOrbit] {Held0.Count} object(s) around {CenterName} ({(_synced ? "SYNCED" : "local only")}).");
		}

		/// <summary>Puts ONE prop back where the world had it: parent, pose, and the rigidbody flags
		/// the ring forced (kinematic + interpolation). Never throws and no-ops on a destroyed object,
		/// so it is safe both from the orderly Stop() and from the per-frame failure path — which is
		/// what keeps a transient error from stranding a prop outside every teardown ledger.</summary>
		private static void RestoreHeld(Held h)
		{
			try
			{
				if (h == null || h.Go == null || h.T == null) return;
				if (h.Parent != null) h.T.SetParent(h.Parent, true);
				h.T.position = h.Pos;
				h.T.rotation = h.Rot;
				if (h.Body != null)
				{
					try
					{
						h.Body.interpolation = h.WasInterp;
						h.Body.isKinematic = h.WasKinematic;
						if (!h.Body.isKinematic) { h.Body.velocity = Vector3.zero; h.Body.angularVelocity = Vector3.zero; }
					}
					catch { }
				}
			}
			catch { }
		}

		public static void Stop(string why)
		{
			for (int i = 0; i < Held0.Count; i++) RestoreHeld(Held0[i]);
			Held0.Clear();
			Active = false;
			_center = null;
			CenterName = "";
			if (!string.IsNullOrEmpty(why)) VaTagsModule.LastStatus = why;
		}

		public override void OnUpdate()
		{
			if (!Active) return;
			try
			{
				if (_center == null) { Stop("stopped — the centre is gone"); return; }

				_angle += ModConfig.ObjOrbitSpeed.Value * VaClock.Delta;
				if (_angle >= 360f) _angle -= 360f;

				float r = Mathf.Max(0.5f, ModConfig.ObjOrbitRadius.Value);
				float h0 = ModConfig.ObjOrbitHeight.Value;
				Vector3 c = _center.position;
				bool spin = ModConfig.ObjOrbitSpin.Value;
				float dt = VaClock.Delta;

				bool reassertOwner = false;
				if (_synced)
				{
					float nowT = VaClock.Now;
					if (nowT >= _nextOwn) { _nextOwn = nowT + 2f; reassertOwner = true; }
				}
				// NO MOVE GATE. A ~15 Hz throttle used to sit here on the theory that moving fewer times
				// sends fewer events. It does not: VRC_ObjectSync serialises at ITS OWN tick (~10 Hz per
				// owned object -- the 240 events/s measured on 2026-09-02 was 24 objects x 10 Hz), so a
				// 15 Hz move still hands every tick a fresh position and the event count is identical.
				// What the gate DID do was hold each body still for ~4 frames and then jump it: a 15 fps
				// stutter for the owner, and a saw-tooth velocity (jump, zero, zero, zero) that remote
				// clients extrapolate into the same stutter. The network is protected by the synced
				// object CAP in Start(), not by this. Moving every frame is the moving-platform path:
				// MovePosition on an interpolated kinematic body every frame = smooth here, and a steady
				// tangential velocity for everyone else to dead-reckon.

				int alive = 0;
				for (int i = Held0.Count - 1; i >= 0; i--)
				{
					var h = Held0[i];

					// A held object can be DESTROYED under us at any moment — an avatar swapping, a
					// pickup despawning, a world unloading a chunk. On the managed side the wrapper
					// can outlive the native object, so writing its Transform then writes freed
					// memory: that is an access violation reading address 0, and it is what took the
					// game down while 24 objects were spinning and avatars were being cloned.
					//
					// So liveness is PROVEN each frame by touching the object inside a guard, and
					// anything that fails is dropped from the ring instead of written to again.
					bool ok = false;
					try { ok = h != null && h.Go != null && h.T != null && h.Go.transform != null; }
					catch { ok = false; }
					if (!ok) { Held0.RemoveAt(i); continue; }

					try
					{
						// Ownership can be taken back by anyone who grabs the object, so it is
						// re-asserted on a slow timer. Without this the ring silently stops being
						// visible to others the moment somebody touches one.
						if (_synced && reassertOwner) TakeOwnershipCounted(h.Go);

						float rad = (_angle + h.Phase) * Mathf.Deg2Rad;
						// MOVE THROUGH THE RIGIDBODY when there is one. Writing transform.position on a
						// kinematic body leaves its velocity at zero, and VRC_ObjectSync serialises that
						// zero: remote clients then have nothing to extrapolate with and draw the ring
						// as a stutter of samples. MovePosition on an interpolated kinematic body is the
						// moving-platform path: the body carries the velocity of the move.
						var target = new Vector3(c.x + Mathf.Cos(rad) * r, c.y + h0 + h.Tilt, c.z + Mathf.Sin(rad) * r);
						if (h.Body != null)
						{
							h.Body.MovePosition(target);
							if (spin) h.Body.MoveRotation(h.Body.rotation * Quaternion.Euler(h.Spin * dt));
						}
						else
						{
							h.T.position = target;
							if (spin) h.T.Rotate(h.Spin * dt, Space.Self);
						}
						alive++;
					}
					catch
					{
						// RESTORE BEFORE FORGETTING (2026-09-13). Dropping the entry on a merely
						// transient failure (a contended write, a one-frame ownership hiccup) left a
						// live world prop permanently isKinematic with our interpolation setting, at
						// whatever position the ring had moved it to, and its parent never restored —
						// Held0 is the only ledger Stop() walks, so nothing could ever put it back.
						RestoreHeld(h);
						Held0.RemoveAt(i);
					}
				}

				if (alive == 0) Stop("stopped — every object was destroyed");
			}
			catch (Exception e)
			{
				Stop("stopped — " + e.Message);
			}
		}

		// Put everything back when the world changes: the Transforms we are holding belong to a
		// scene that is about to be torn down, and a half-restored world is worse than none.
		public override void OnSceneLoaded(int buildIndex) => Stop(null);
		public override void OnShutdown() => Stop(null);

		// Which props are fair game.
		//
		// The filters are all about NOT grabbing something load-bearing: the floor, a wall, the
		// skybox, a player's avatar or our own UI. Anything that is too big to be a prop, attached
		// to a player, or has no renderer, is left where it is.
		internal static List<Transform> Collect(Vector3 around, float range, int want) => Collect(around, range, want, _synced);

		// networkableOnly: a SYNCED ring must only take what it can actually network (grabbable pickups
		// nobody holds). The Mark's art passes false: it wants EVERY loose object — the non-networkable
		// ones simply stay local (TakeOwnershipCounted refuses anything that is not ownable anyway).
		internal static List<Transform> Collect(Vector3 around, float range, int want, bool networkableOnly)
		{
			var outp = new List<Transform>(want);
			var seen = new HashSet<int>();
			try
			{
				float r2 = range * range;
				float maxSize = ModConfig.ObjOrbitMaxSize.Value;
				var cands = new List<(float d, Transform t)>();

				// One candidate's transform, filtered: in range, not on a player, not our own UI, and small
				// enough to be a prop rather than the room. A dead wrapper's members are an access violation
				// the catch cannot save, so NativeGuard gates before every native read.
				void Consider(Transform t)
				{
					try
					{
						if (t == null || !Core.NativeGuard.Alive(t)) return;
						float d2 = (t.position - around).sqrMagnitude;
						if (d2 > r2 || d2 < 0.04f) return;
						if (IsPlayerOwned(t) || IsOurs(t)) return;

						// Size sanity when the item has a renderer: a 30-metre bound is a building, not a prop.
						try
						{
							var rnd = t.GetComponentInChildren<Renderer>();
							if (rnd != null && Core.NativeGuard.Alive(rnd))
							{
								Vector3 size = rnd.bounds.size;
								if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > maxSize) return;
							}
						}
						catch { }

						int id = t.GetInstanceID();
						if (!seen.Add(id)) return;
						cands.Add((d2, t));
					}
					catch { }
				}

				// REAL ITEMS ONLY, not every mesh. Collecting every Renderer meant a detailed world (Late
				// Night and its like) handed back THOUSANDS of static meshes \u2014 furniture, walls, decor \u2014
				// so the ring grabbed the room itself and orbiting all of it lagged the game. What a player
				// actually calls an "item" has physics or is grabbable: VRC_Pickup (author-grabbable) and
				// Rigidbody (loose props). Far fewer objects, and the right ones \u2014 and FindObjectsOfType
				// over those is a fraction of the cost of walking every renderer in the scene.
				try
				{
					// NON-GENERIC on purpose: FindObjectsOfType<VRC_Pickup>() returns nothing on this build
					// (Il2CppInterop's generic type resolution misses the class — the same gap that had Force
					// Pickup unlocking 0). Resolve the il2cpp type once and TryCast each result back.
					var il2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.SDKBase.VRC_Pickup>();
					var found = VRChatArchiveMod.Core.Live.AllOfType(il2);
					if (found != null)
						for (int fi = 0; fi < found.Length; fi++)
						{
							var pk = found[fi] != null ? found[fi].TryCast<VRC.SDKBase.VRC_Pickup>() : null;
							if (pk == null || !Core.NativeGuard.Alive(pk)) continue;
							// Synced mode networks each object, so it only ever touches things the world made
							// grabbable AND that nobody else is holding.
							if (networkableOnly && !IsGrabbablePickup(pk.transform)) continue;
							Consider(pk.transform);
						}
				}
				catch { }

				// Rigidbody props are LOCAL only: a non-pickup rigidbody is not networkable, so a synced ring
				// could not move it for anyone else \u2014 it would only desync you.
				if (!networkableOnly)
				{
					try
					{
						var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
						if (bodies != null)
							foreach (var rb in bodies)
							{
								if (rb == null || !Core.NativeGuard.Alive(rb)) continue;
								Consider(rb.transform);
							}
					}
					catch { }
				}

				cands.Sort((a, b) => a.d.CompareTo(b.d));
				for (int i = 0; i < cands.Count && outp.Count < want; i++) outp.Add(cands[i].t);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[ObjectOrbit] collect failed: {e.Message}");
			}
			return outp;
		}

		// Anything hanging off a player is left alone, and this is a SAFETY check as much as a
		// cosmetic one. Avatar hierarchies are destroyed and rebuilt constantly — every avatar
		// change, every clone — so holding one of their Transforms and writing it each frame is a
		// use-after-free waiting for the next swap. That is what crashed the game.
		//
		// Matching on names ("Player", "Avatar") was never enough: avatar meshes are called Body,
		// Armature, whatever the author chose. The roster already knows each player's real root
		// Transform, so ancestry is compared against those directly.
		private static bool IsPlayerOwned(Transform t)
		{
			try
			{
				var roots = new List<Transform>();
				try
				{
					foreach (var pe in VaTagsModule.Roster)
						if (pe?.Transform != null) roots.Add(pe.Transform);
				}
				catch { }
				try { var lt = PlayerRef.LocalTransform(); if (lt != null) roots.Add(lt); }
				catch { }

				for (Transform p = t; p != null; p = p.parent)
				{
					for (int i = 0; i < roots.Count; i++)
						if (ReferenceEquals(p, roots[i]) || p == roots[i]) return true;

					string n = p.name ?? "";
					if (n.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0) return true;
					if (n.IndexOf("Avatar", StringComparison.OrdinalIgnoreCase) >= 0) return true;
					if (n.StartsWith("usr_", StringComparison.OrdinalIgnoreCase)) return true;
				}
			}
			catch { }
			return false;
		}

		// A real VRC_Pickup somewhere at or above this transform, and not already in somebody's
		// hands. Taking an object out of a person's grip is a different joke from spinning the
		// furniture, and not one this does.
		private static bool IsGrabbablePickup(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					var pick = p.GetComponent<VRC.SDKBase.VRC_Pickup>();
					if (pick == null) continue;
					try { if (pick.IsHeld) return false; } catch { }
					return true;
				}
			}
			catch { }
			return false;
		}

		// Networking.SetOwner is the SDK's own call — the same one a world uses to hand an object to
		// whoever grabbed it. Once we own it, VRChat broadcasts the transform for us; without it the
		// writes stay on this client no matter how correct they look here.
		// NETWORKED OBJECTS ONLY.
		//
		// Collect picks candidates by RENDERER — any visible mesh in range — and most of those are
		// plain world geometry with no networking behind them at all. Networking.SetOwner on one of
		// those makes VRChat dereference network state that was never allocated, and the process
		// dies: an access violation inside the game, which no try/catch here can intercept. That is
		// the crash you get the moment the ring starts with "Synced" on.
		//
		// So the object must prove it is networked BEFORE either Networking call is made. VRC_Pickup
		// and VRCObjectSync are the components that make an object ownable; anything else is
		// orbited locally, which is all a wall could ever have been anyway.
		private static bool IsNetworked(GameObject go)
		{
			try
			{
				if (go == null) return false;
				var comps = go.GetComponents<Component>();
				if (comps == null) return false;
				foreach (var c in comps)
				{
					if (c == null || !Core.NativeGuard.Alive(c)) continue;
					string n;
					try { n = Core.MenuCard.Il2CppNameOf(c); }
					catch { continue; }
					if (string.IsNullOrEmpty(n)) continue;
					if (n.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0)
						return true;
				}
			}
			catch { }
			return false;
		}

		internal static bool TakeOwnershipCounted(GameObject go)
		{
			try
			{
				if (go == null) return false;
				// Liveness FIRST: IsNetworked walks the object's components and reads each type name, so
				// it must not run on a dead proxy \u2014 that read is itself an uncatchable access violation.
				if (!Core.NativeGuard.Alive(go)) return false;
				if (!IsNetworked(go)) return false;    // the gate that stops the crash
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (me == null || !Core.NativeGuard.Alive(me)) return false;
				if (VRC.SDKBase.Networking.IsOwner(me, go)) return true;
				VRC.SDKBase.Networking.SetOwner(me, go);
				return true;
			}
			catch { return false; }
		}

		private static bool IsOurs(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					string n = p.name ?? "";
					if (n.StartsWith("Archive", StringComparison.Ordinal)) return true;
					if (n.IndexOf("Canvas_QuickMenu", StringComparison.OrdinalIgnoreCase) >= 0) return true;
					if (n.IndexOf("UserInterface", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
		}
	}
}
