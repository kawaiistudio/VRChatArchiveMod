using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ELEVATOR — the exact same trick as OBJECT ORBIT, but instead of spinning the world's loose
	// props in a RING around someone, it gathers them into a PLATFORM under them and drives that
	// platform straight UP the Y axis. A rising floor. An elevator.
	//
	// IT ONLY EVER MOVES OBJECTS, never a player. This is the whole point and the reason it is safe:
	// VRChat lets no client move another player's avatar, and this does not try to. It owns loose
	// pickups (the SDK's own Networking.SetOwner, exactly like Object Orbit's synced mode) and moves
	// THEM. Whoever is standing on the platform rides up the way they ride any world elevator — their
	// OWN client's physics carries them. Nothing about the local or remote player is written.
	//
	// TWO MODES, same line as Object Orbit:
	//   LOCAL (default) — the platform moves on YOUR screen only. You ride your own; nobody else sees
	//     it and no one else can be carried.
	//   SYNCED — takes ownership of real VRC_Pickup objects and lets VRChat broadcast their position,
	//     so everyone sees the platform and anyone standing on it is carried by their own physics.
	//     Fenced exactly like Object Orbit: pickups only (never world geometry / doors / seats),
	//     nothing held by someone else, capped low because each owned object is its own ObjectSync
	//     stream, and every object is put back on stop.
	//
	// Everything taken is restored (position, rotation, parent, rigidbody state) on stop, on world
	// change and on shutdown — same contract as Object Orbit.
	public class ElevatorModule : IModule
	{
		public override string Name => "Elevator";

		private sealed class Slab
		{
			public Transform T;
			public GameObject Go;
			public Vector3 Pos;
			public Quaternion Rot;
			public Transform Parent;
			public float Ox, Oz;     // its fixed spot on the platform disc
			public Rigidbody Body;
			public bool WasKinematic;
			// The platform sets useGravity = false so a slab cannot fall out of the shaft. It was
			// never recorded, so Stop() could not put it back and every world prop that already had
			// a Rigidbody stayed floating after the elevator was dismissed.
			public bool WasGravity;
			public bool AddedBody;   // we created the Rigidbody (destroy it on restore)
			public RigidbodyInterpolation WasInterp;
			public Collider Col;     // the collider we forced solid so the box can stand on it
			public bool WasTrigger;
			public bool ColWasEnabled;
		}

		private static readonly List<Slab> Slabs = new List<Slab>();
		private static Transform _center;      // the shaft is centred on this (self or a player)
		private static Vector3 _base;          // X/Z of the shaft, tracked to the rider every step
		private static float _platformY;       // ABSOLUTE world Y of the platform plane right now
		private static float _startY;          // the ground the ride began from (for the height cap)
		private static Vector3 _prevC;         // rider position last physics step (for velocity/prediction)
		private static bool _prevValid;
		private static float _startTime;       // when the ride began (for the settle-before-rise delay)
		private static bool _synced;
		private static float _nextOwn;

		public static bool Active { get; private set; }
		public static string CenterName { get; private set; } = "";
		public static int Count => Slabs.Count;

		// Live overrides typed from the client's ELEVATOR number boxes. 0 = "use the configured default".
		public static float SpeedOverride;
		public static float HeightOverride;

		private bool _hotkeyWasDown;

		// The platform under the LOCAL player — ride your own elevator.
		public static void ToggleOnSelf()
		{
			if (Active && CenterName == "you") { Stop("platform put back"); return; }
			var t = PlayerRef.LocalTransform();
			if (t == null) { VaTagsModule.LastStatus = "you are not in a world yet"; return; }
			Start(t, "you");
		}

		// The platform under another player — they ride it (their own physics carries them; nothing
		// about their avatar is written). Mirrors ObjectOrbit.ToggleOnPlayer.
		public static void ToggleOnPlayer(VaTagsModule.PlayerEntry entry)
		{
			if (Active && entry != null && CenterName == entry.Name) { Stop("platform put back"); return; }
			if (entry?.Transform == null) { VaTagsModule.LastStatus = "can't reach that player yet"; return; }
			Start(entry.Transform, entry.Name);
		}

		internal static void Start(Transform center, string label)
		{
			Stop(null);
			_center = center;
			CenterName = label ?? "";

			try { _synced = ModConfig.ElevatorSynced.Value; } catch { _synced = false; }

			// HOW MANY. 0 = ALL (default), exactly like Object Orbit: a local platform can be as dense
			// as the world allows; a SYNCED one is capped low because every owned object is its own
			// ObjectSync stream (~10 Hz while it moves) and a big synced set once timed the connection
			// out — the same ceiling and the same reason as the ring.
			int cfgCount = 0;
			try { cfgCount = ModConfig.ElevatorCount.Value; } catch { }
			int want = _synced
				? Mathf.Clamp(cfgCount <= 0 ? 40 : cfgCount, 1, 40)
				: Mathf.Clamp(cfgCount <= 0 ? 400 : cfgCount, 1, 400);

			float range;
			try { range = ModConfig.ElevatorRange.Value <= 0f ? 100000f : Mathf.Max(2f, ModConfig.ElevatorRange.Value); }
			catch { range = 100000f; }

			// Same collector as the ring — loose props only, never load-bearing geometry, never a
			// player's avatar, never something held. Synced mode restricts to networkable pickups.
			var picked = ObjectOrbitModule.Collect(center.position, range, want, _synced);
			if (picked.Count == 0)
			{
				VaTagsModule.LastStatus = "no loose objects within " + (int)range + "m to build a platform from";
				_center = null;
				return;
			}

			float radius = PlatformRadius;
			int owned = 0;
			for (int i = 0; i < picked.Count; i++)
			{
				var t = picked[i];
				// A TINY FLAT disc, not a wide one and not a single stacked point. Wide left gaps you
				// fell through; a single point is not flat enough to stand on. A small radius with the
				// props sunflower-packed at the SAME height gives a compact, LEVEL pad of overlapping
				// colliders right under the feet. radius 0 collapses to one point.
				float rr = radius <= 0f ? 0f : radius * Mathf.Sqrt((i + 0.5f) / picked.Count);
				float ang = i * 2.399963f;   // golden angle
				var s = new Slab
				{
					T = t,
					Go = t.gameObject,
					Pos = t.position,
					Rot = t.rotation,
					Parent = t.parent,
					Ox = Mathf.Cos(ang) * rr,
					Oz = Mathf.Sin(ang) * rr,
				};
				// Freeze the body kinematic (so gravity/collisions stop fighting our writes) but keep
				// it INTERPOLATED, so MovePosition below reports a real upward velocity — that velocity
				// is what VRChat sends and what makes a standing player get carried instead of clipped.
				try
				{
					// FORCE a kinematic Rigidbody. If the prop has none, moving its transform is not a
					// physics sweep and pushes nothing — so we add one. Kinematic + interpolated is the
					// moving-platform body; MovePosition on it carries whatever stands on it.
					s.Body = t.GetComponent<Rigidbody>() ?? t.GetComponentInChildren<Rigidbody>();
					if (s.Body == null)
					{
						try { s.Body = t.gameObject.AddComponent<Rigidbody>(); s.AddedBody = true; } catch { }
					}
					if (s.Body != null)
					{
						s.WasKinematic = s.Body.isKinematic; s.Body.isKinematic = true;
						s.WasInterp = s.Body.interpolation; s.Body.interpolation = RigidbodyInterpolation.Interpolate;
						// Captured, not just written: Stop() has to be able to give gravity back.
						try { s.WasGravity = s.Body.useGravity; s.Body.useGravity = false; } catch { }
					}
					// And a SOLID collider — enabled and not a trigger — or there is nothing to stand on.
					var col = t.GetComponent<Collider>() ?? t.GetComponentInChildren<Collider>();
					if (col != null)
					{
						s.Col = col;
						s.ColWasEnabled = col.enabled; if (!col.enabled) col.enabled = true;
						s.WasTrigger = col.isTrigger; if (col.isTrigger) col.isTrigger = false;
					}
				}
				catch { }
				if (_synced && ObjectOrbitModule.TakeOwnershipCounted(s.Go)) owned++;
				Slabs.Add(s);
			}

			_base = center.position;
			_startY = center.position.y;
			_platformY = _startY - StartDepth;   // begins below the feet, then rises up into them
			_prevC = center.position;
			_prevValid = false;
			_startTime = VaClock.Now;
			Active = true;

			// HONEST wording: "sees it" only, never "and can ride it" — whether a moving platform
			// actually carries a remote player is up to VRChat's physics, not something to promise.
			VaTagsModule.LastStatus = _synced && owned > 0
				? $"{Slabs.Count}-object platform under {CenterName} — SYNCED, everyone in the room sees it"
				: _synced
					? $"{Slabs.Count}-object platform under {CenterName} — SYNCED, but none of these objects are networkable, so only YOU see it"
					: $"{Slabs.Count}-object platform under {CenterName} — LOCAL, only YOU see it";
			VRChatArchiveModPlugin.Logger.LogInfo(
				$"[Elevator] {Slabs.Count} object(s) under {CenterName} ({(_synced ? "SYNCED" : "local only")}).");
		}

		/// <summary>Puts ONE slab back exactly as the world had it: parent, pose, collider, rigidbody
		/// (destroyed when we added it, otherwise kinematic/interpolation/gravity restored). Never
		/// throws, and no-ops on an object that is already gone — so it is safe to call both from the
		/// orderly Stop() and from the per-frame failure path, which is what stops a transient error
		/// from stranding a prop outside every teardown ledger.</summary>
		private static void RestoreSlab(Slab s)
		{
			try
			{
				if (s == null || s.Go == null || s.T == null) return;
				if (s.Parent != null) s.T.SetParent(s.Parent, true);
				s.T.position = s.Pos;
				s.T.rotation = s.Rot;
				if (s.Col != null)
				{
					try { s.Col.isTrigger = s.WasTrigger; s.Col.enabled = s.ColWasEnabled; } catch { }
				}
				if (s.Body != null)
				{
					try
					{
						if (s.AddedBody)
						{
							UnityEngine.Object.Destroy(s.Body);   // we created it — take it back off
						}
						else
						{
							s.Body.interpolation = s.WasInterp;
							s.Body.isKinematic = s.WasKinematic;
							// Gravity back the way the world had it, or the prop stays hanging in
							// mid-air with the elevator gone.
							s.Body.useGravity = s.WasGravity;
							if (!s.Body.isKinematic) { s.Body.velocity = Vector3.zero; s.Body.angularVelocity = Vector3.zero; }
						}
					}
					catch { }
				}
			}
			catch { }
		}

		public static void Stop(string why)
		{
			for (int i = 0; i < Slabs.Count; i++) RestoreSlab(Slabs[i]);
			Slabs.Clear();
			Active = false;
			_center = null;
			CenterName = "";
			_platformY = 0f;
			_startY = 0f;
			if (!string.IsNullOrEmpty(why)) VaTagsModule.LastStatus = why;
		}

		public override void OnUpdate()
		{
			// RightShift+L rides your own elevator, the same edge-detected shape as the ring's RShift+O.
			try
			{
				bool combo = Input.GetKey(KeyCode.RightShift) && Input.GetKey(KeyCode.L);
				if (combo && !_hotkeyWasDown) ToggleOnSelf();
				_hotkeyWasDown = combo;
			}
			catch { }
		}

		// THE RISE HAPPENS IN THE PHYSICS STEP, on purpose. The whole idea is that the rising colliders
		// PUSH the player's box up — and a kinematic body only pushes what it collides with when it is
		// moved inside the physics step. A MovePosition issued from OnUpdate (variable frame time) is
		// applied between physics ticks and collision resolution never "sees" the sweep, so a player
		// standing on it is left behind or clipped through. Moved from OnFixedUpdate it is the ordinary
		// moving-platform path VRChat worlds use, so whoever stands on the platform rides up with it.
		public override void OnFixedUpdate()
		{
			if (!Active) return;
			try
			{
				if (_center == null) { Stop("stopped — the centre is gone"); return; }

				float dt = VaClock.FixedDelta;
				float climb = ClimbSpeed;
				float maxH = MaxHeight;              // 0 = unlimited

				// AUTO-CORRECT TO THE LIVE PLAYER POSITION. We know where the rider actually is every
				// step (their transform / roster position), so the shaft is kept locked under them in
				// X/Z, and — crucially — if they slip off or the platform out-climbs them, the plane is
				// snapped back to just under their current feet so it CATCHES them again and keeps
				// lifting. Without this the platform rises away from a rider it failed to carry; with it
				// the elevator is self-healing.
				Vector3 c;
				try { c = _center.position; } catch { Stop("stopped — the centre is gone"); return; }
				_base.x = c.x; _base.z = c.z;
				float feetY = c.y;

				// SETTLE FIRST. For the first SettleSeconds the platform just sits under the rider and
				// does NOT climb — that pause is what lets VRChat sync the objects' ownership and
				// position out to everyone (and the rider land on them) before the lift starts, instead
				// of the platform racing upward while the network is still catching up.
				if (VaClock.Now - _startTime < SettleSeconds)
				{
					_platformY = feetY - StartDepth;
				}
				else if (AutoCorrect)
				{
					// PREDICTIVE POWER GLUE. Weak correction lost to lag: by the time the plane reached
					// where the rider was, they (and their ping-delayed sync) had moved on. So we PREDICT
					// where they are heading — velocity from the position delta (works for the local
					// player or a remote one read off the roster) times a Lead that stands in for the
					// round-trip delay — and drive the plane a strong overshoot ABOVE those predicted feet
					// so it is always pushing the box up. If the rider drops below it (slipped), it
					// follows straight down the SAME tick to re-hug them. Rise is capped to ClimbSpeed so
					// it lifts rather than flinging.
					Vector3 vel = _prevValid && dt > 0.0001f ? (c - _prevC) / dt : Vector3.zero;
					if (vel.sqrMagnitude > 900f) vel = vel.normalized * 30f;   // ignore teleport spikes
					Vector3 pred = c + vel * Lead;
					_base.x = pred.x; _base.z = pred.z;      // lead the horizontal too
					float predFeetY = pred.y;

					const float power = 0.3f;                // how hard the plane noses above the feet
					bool atCap = maxH > 0f && predFeetY - _startY >= maxH;
					float want = predFeetY + (atCap ? -StartDepth : power);
					if (want < _platformY) _platformY = want;                                    // catch a fall NOW
					else if (!atCap) _platformY = Mathf.Min(want, _platformY + climb * dt);       // push up at climb speed
				}
				else
				{
					// NO auto-correct (default): the platform just rises steadily from where it started
					// and never chases the rider in Y — it gives them the time to settle and network-sync
					// onto it. X/Z still track so it stays under them.
					_platformY += climb * dt;
					if (maxH > 0f && _platformY > _startY + maxH) _platformY = _startY + maxH;
				}

				_prevC = c; _prevValid = true;

				bool reassertOwner = false;
				if (_synced)
				{
					float nowT = VaClock.Now;
					if (nowT >= _nextOwn) { _nextOwn = nowT + 2f; reassertOwner = true; }
				}

				float planeY = _platformY;

				int alive = 0;
				for (int i = Slabs.Count - 1; i >= 0; i--)
				{
					var s = Slabs[i];

					// Same per-frame liveness proof as Object Orbit: a held object can be destroyed
					// under us (avatar swap, chunk unload) and the managed wrapper outlives the native
					// object, so touching a dead one is an uncatchable access violation. Prove it lives
					// inside the guard, drop anything that fails.
					bool ok = false;
					try { ok = s != null && s.Go != null && s.T != null && s.Go.transform != null; }
					catch { ok = false; }
					if (!ok) { Slabs.RemoveAt(i); continue; }

					try
					{
						if (_synced && reassertOwner) ObjectOrbitModule.TakeOwnershipCounted(s.Go);

						var target = new Vector3(_base.x + s.Ox, planeY, _base.z + s.Oz);
						if (s.Body != null) s.Body.MovePosition(target);   // carries a real upward velocity
						else s.T.position = target;
						alive++;
					}
					catch
					{
						// RESTORE BEFORE FORGETTING (2026-09-13). A throw here is usually transient (a
						// contended write, a one-frame ownership hiccup), but dropping the slab from
						// Slabs — the ONLY teardown ledger — left a live world prop permanently
						// kinematic, gravity-less, its collider forced solid, and with the Rigidbody we
						// may have added still on it, unreachable by Stop(). Undo this one's changes
						// first; if it really was destroyed, RestoreSlab's guards no-op harmlessly.
						RestoreSlab(s);
						Slabs.RemoveAt(i);
					}
				}

				if (alive == 0) Stop("stopped — every object was destroyed");
			}
			catch (Exception e)
			{
				Stop("stopped — " + e.Message);
			}
		}

		public override void OnSceneLoaded(int buildIndex) => Stop(null);
		public override void OnShutdown() => Stop(null);

		// ---------------------------------------------------------------- config (guarded)

		private static float ClimbSpeed
		{
			// The client's number box wins when set (> 0); 0 there means "use the configured default".
			get
			{
				if (SpeedOverride > 0f) return Mathf.Clamp(SpeedOverride, 0.1f, 40f);
				try { return Mathf.Clamp(ModConfig.ElevatorClimbSpeed.Value, 0.1f, 40f); } catch { return 1.5f; }
			}
		}
		private static float MaxHeight
		{
			get
			{
				if (HeightOverride > 0f) return HeightOverride;
				try { return Mathf.Max(0f, ModConfig.ElevatorMaxHeight.Value); } catch { return 15f; }
			}
		}
		private static float PlatformRadius
		{
			get { try { return Mathf.Clamp(ModConfig.ElevatorPlatformRadius.Value, 0f, 12f); } catch { return 0f; } }
		}
		private static float StartDepth
		{
			get { try { return Mathf.Clamp(ModConfig.ElevatorStartDepth.Value, 0f, 5f); } catch { return 0.1f; } }
		}
		private static bool AutoCorrect
		{
			get { try { return ModConfig.ElevatorAutoCorrect.Value; } catch { return false; } }
		}
		private static float Lead
		{
			get { try { return Mathf.Clamp(ModConfig.ElevatorLead.Value, 0f, 1f); } catch { return 0.2f; } }
		}
		private static float SettleSeconds
		{
			get { try { return Mathf.Clamp(ModConfig.ElevatorSettleSeconds.Value, 0f, 10f); } catch { return 2f; } }
		}
	}
}
