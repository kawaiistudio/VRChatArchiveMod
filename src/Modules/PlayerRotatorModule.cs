using System;
using System.Reflection;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PLAYER ROTATOR — turn your OWN capsule, and take the view with it.
	//
	// WHY THE OLD ROTATOR DID NOT DO THIS. MovementModule's arrow-key turning is YAW ONLY, and says
	// so in its own comment: "Pitch is not offered: VRChat keeps players upright, so tilting leaves
	// the rig crooked." That is the whole feature the owner asked for — head down, with the view
	// following — so the refusal had to be undone properly rather than by widening the old code.
	//
	// IT IS TWO DIFFERENT LEVERS, AND THAT IS THE POINT. Writing the rig's rotation moves the BODY.
	// It does not move the desktop camera: VRChat rebuilds the view every frame from the mouse-look
	// component, so a rig you tilted looks tilted to everyone including you-in-a-mirror, while your
	// camera stays stubbornly level. MovementModule already found this the hard way — its method #4
	// is documented as spinning "only the avatar MESH", with "the player and the desktop view keep
	// facing the old way".
	//
	//   BODY  →  the rig transform, rewritten every LateUpdate so VRChat's own write cannot undo it.
	//   VIEW  →  NeckMouseRotator.field_Public_NeckRange_0, the CLAMP on how far mouse-look may pitch.
	//            Widen it and the mouse itself carries you past vertical and all the way over.
	//
	// The view half is not invented here: EvilEye's HeadFlipper does exactly this one write —
	// NeckRange(float.MinValue, float.MaxValue, 0) — and that mod ships working. This uses a large
	// FINITE limit instead, because a clamp of ±3.4e38 feeding a lerp is one NaN away from a camera
	// that never comes back, and ±180° is already all the way round.
	//
	// VERIFIED BEFORE IT WAS WRITTEN, not after. Both members exist in this build's interop
	// assemblies: LocomotionInputController.field_Protected_NeckMouseRotator_0 (and
	// GamelikeInputController derives from it, so the desktop controller carries it), and
	// NeckMouseRotator.field_Public_NeckRange_0 of type VRC.DataModel.NeckRange — a three-float
	// struct at fixed offsets 0/4/8. That check is the lesson of the custom-username crash, which
	// shipped on a grep of a name and took the game down.
	//
	// WHAT IT PROBES RATHER THAN ASSUMES. Two things are genuinely unknown until the game is running,
	// so neither is guessed: which of NeckRange's three floats is the min and which the max (the
	// ORIGINAL values are read and logged, and only the two that look like a symmetric clamp are
	// widened), and whether the rig rotation actually holds (it is read back next frame and the
	// result is logged once). If either turns out otherwise, the log says so and the feature reports
	// itself broken instead of pretending.
	//
	// LOCAL, AND HONEST ABOUT IT. The rig transform is the local player's own; VRChat serialises what
	// it serialises, so other people may or may not see the tilt. Nothing is sent by hand, no ApiModel
	// is touched, and turning it off puts the neck clamp and the rig back exactly as they were.
	public class PlayerRotatorModule : IModule
	{
		public override string Name => "PlayerRotator";

		public static string Status = "off";
		public static bool Active { get; private set; }

		/// <summary>Body tilt held by the module. Yaw stays VRChat's.</summary>
		// The tilt away from upright, as a rotation. Kept as a quaternion precisely so that pitch
		// and roll cannot collapse into each other the way two euler angles do past vertical.
		private static Quaternion _tilt = Quaternion.identity;

		/// <summary>Turn by `deg` about an axis expressed in the BODY's own frame.</summary>
		private static void Turn(Vector3 bodyAxis, float deg)
		{
			if (Mathf.Abs(deg) < 0.0001f) return;
			_tilt = _tilt * Quaternion.AngleAxis(deg, bodyAxis);
		}

		// ------------------------------------------------------------------ the neck clamp
		private static object _neck;                 // NeckMouseRotator proxy
		private static PropertyInfo _rangeProp;      // its field_Public_NeckRange_0
		private static object _rangeOriginal;        // boxed NeckRange, exactly as we found it
		private static bool _neckWidened;
		private static bool _neckResolved;
		private static string _neckWhy = "not tried";

		// ------------------------------------------------------------------ the body
		private static Transform _rig;
		private static float _rigCheckedAt;
		private static Quaternion _applied = Quaternion.identity;
		private static float _yaw;                   // VRChat's heading, tracked so tilt composes on top
		private static bool _holdProbed;
		// Set when an OFF could not reach the rig; OnLateUpdate keeps retrying the un-tilt until it
		// succeeds, so a transient failure can never leave the body rotated with the switch off.
		private static bool _restorePending;
		// Last asserted state of RotatorFreeLook, so that switch can widen/restore the neck clamp on
		// its own edge rather than only when the rotator itself is armed.
		private static bool? _lastFreeLook;

		public static void Toggle() { Set(!Active); }

		public static void Set(bool on)
		{
			if (on == Active) return;
			Active = on;
			try { if (ModConfig.RotatorEnabled != null) ModConfig.RotatorEnabled.Value = on; } catch { }

			if (on)
			{
				_tilt = Quaternion.identity;
				_rig = null; _holdProbed = false; _animCached = null;
				WidenNeck();
				Status = "on — arrows tilt, PgUp/PgDn roll, RShift+F flips, RShift+Backspace resets"
					+ (_neckWidened ? "" : " (view clamp unchanged: " + _neckWhy + ")");
			}
			else
			{
				RestoreNeck();
				RestoreBody();
				HoldGravity(false);
				Status = "off";
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[Rotator] " + Status);
			Toast.Show(on ? "Player rotator ON — " + Status : "Player rotator OFF — upright again");
		}

		/// <summary>Straight to upside down and straight back — the thing you actually want a hotkey
		/// for, rather than holding a key for a second and a half. Arms the rotator if it was off, so
		/// one button from the client is enough.</summary>
		public static void Flip()
		{
			if (!Active) Set(true);
			bool wasOver = Quaternion.Angle(_tilt, Quaternion.identity) > 90f;
			_tilt = wasOver ? Quaternion.identity : Quaternion.AngleAxis(180f, Vector3.forward);
			Status = wasOver ? "upright" : "upside down";
			Toast.Show("Rotator — " + Status);
		}

		/// <summary>Back to level WITHOUT switching the rotator off, so the keys stay live.</summary>
		public static void ResetUpright()
		{
			_tilt = Quaternion.identity;
			Status = Active ? "upright — arrows tilt, PgUp/PgDn roll, RShift+F flips" : "off";
			if (!Active) RestoreBody();
			Toast.Show("Rotator — upright");
		}

		public override void OnUpdate()
		{
			try
			{
				// RShift+R arms it. Same family as the mod's other RShift hotkeys, and R was free.
				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.R)) { Toggle(); return; }

				// THE CONFIG TOGGLE ACTUALLY ARMS THE ROTATOR (2026-09-13). RotatorEnabled was only
				// ever WRITTEN, by Set(); nothing in the mod read it back, so the persisted switch in
				// the menu and the config file neither armed nor disarmed anything — the state lived
				// purely in the static Active. Reading it on the edge makes the switch real, and
				// because Set() writes it too, flipping it from either side stays consistent.
				try
				{
					if (ModConfig.RotatorEnabled != null)
					{
						bool wantActive = ModConfig.RotatorEnabled.Value;
						if (wantActive != Active) Set(wantActive);
					}
				}
				catch { }

				// FREE LOOK IS ITS OWN TOGGLE, WATCHED EVERY PASS. WidenNeck() is called only from
				// Set(true) and OnSceneLoaded, and the switch was read INSIDE it — so turning free
				// look off while the rotator was already on never reached RestoreNeck() and the neck
				// clamp stayed widened, while turning it on did nothing until the rotator was cycled.
				if (Active)
				{
					bool wantFree = false;
					try { wantFree = ModConfig.RotatorFreeLook != null && ModConfig.RotatorFreeLook.Value; } catch { }
					if (_lastFreeLook != wantFree)
					{
						_lastFreeLook = wantFree;
						if (wantFree) WidenNeck();
						else RestoreNeck();
					}
				}
				else if (_lastFreeLook != null)
				{
					// Rotator off: the neck is restored by Set(false); forget the asserted state so a
					// later arm re-applies free look from scratch.
					_lastFreeLook = null;
				}

				if (!Active) return;

				float step = VaClock.Delta * Speed();

				// EACH KEY TURNS YOU AROUND YOUR OWN AXIS, NOT AROUND A FIXED ONE.
				//
				// Pitch and roll used to be two angles fed to Quaternion.Euler. That composition is
				// degenerate: once pitch passes about ±90° the pitch and roll axes line up, roll
				// stops doing anything recognisable and the keys fight each other — which is the
				// "not all the arrows work properly" you get after the first somersault. It is
				// gimbal lock, and no amount of clamping fixes it because the representation is
				// what is wrong.
				//
				// SDraw's rotator has no such state: every key is an incremental turn about an axis
				// taken from the body ITSELF that frame —
				//     RotateAround(origin, usePlayerAxis ? playerTransform.right : origin.right, …)
				// — so up is always "nose down from where you are now", whatever way up you are.
				// The tilt is therefore kept as a quaternion and multiplied, never as euler angles.
				if (Input.GetKey(KeyCode.UpArrow)) Turn(Vector3.right, -step);
				if (Input.GetKey(KeyCode.DownArrow)) Turn(Vector3.right, step);

				// PageUp/PageDown = roll. Nothing in the mod or in VRChat desktop uses them.
				if (Input.GetKey(KeyCode.PageUp)) Turn(Vector3.forward, step);
				if (Input.GetKey(KeyCode.PageDown)) Turn(Vector3.forward, -step);

				if (Input.GetKey(KeyCode.RightShift))
				{
					if (Input.GetKeyDown(KeyCode.F)) Flip();
					if (Input.GetKeyDown(KeyCode.Backspace)) ResetUpright();
				}
			}
			catch (Exception e) { Status = "rotator: " + e.Message; }
		}

		// APPLIED IN LateUpdate, on purpose: VRChat's locomotion writes the rig during its own
		// Update, so a write from OnUpdate is overwritten before the frame is drawn. Whether VRChat
		// also writes it in LateUpdate is exactly what the probe below measures.
		public override void OnLateUpdate()
		{
			// A PENDING RESTORE IS RETRIED UNTIL IT LANDS (2026-09-13). RestoreBody() opens with
			// `if (rig == null) return;` and OnLateUpdate used to bail on `!Active` before anything
			// else, so if the rig happened to be unresolvable at the exact moment the rotator was
			// switched off — around a respawn or an avatar load, which is when a rig IS unresolvable —
			// the body stayed tilted permanently with the switch reading OFF and nothing left to try
			// again. The flag is set by Set(false) and cleared by the first restore that succeeds.
			if (_restorePending)
			{
				try
				{
					Transform r0 = Rig();
					if (r0 != null)
					{
						r0.rotation = Quaternion.AngleAxis(_yaw, Vector3.up);
						_applied = r0.rotation;
						_restorePending = false;
					}
				}
				catch { }
			}
			if (!Active) return;
			try
			{
				Transform rig = Rig();
				if (rig == null) return;

				Quaternion current = rig.rotation;

				// Did VRChat rewrite the rig since our last write? If it did, its value is a fresh
				// heading and we adopt it; if it did not, the rotation we are reading is our own
				// composed one and re-deriving a yaw from it would drift (and is degenerate at
				// pitch ±90 anyway).
				if (Quaternion.Angle(current, _applied) > 0.05f)
				{
					Vector3 f = current * Vector3.forward;
					f.y = 0f;
					if (f.sqrMagnitude > 0.0001f) _yaw = Quaternion.LookRotation(f.normalized, Vector3.up).eulerAngles.y;
				}
				else if (!_holdProbed)
				{
					// One line, once: the tilt survived a full frame, so the rig is ours to hold.
					_holdProbed = true;
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[Rotator] rig rotation holds between frames — body tilt is applied to "
						+ rig.name + ".");
				}

				Quaternion want = Quaternion.AngleAxis(_yaw, Vector3.up) * _tilt;

				// GRAVITY FOLLOWS THE TILT, NOT THE SWITCH.
				//
				// The first version took the gravity hold the moment the rotator was armed, which
				// meant arming it upright — the normal state, most of the time — silently switched
				// your gravity off for no reason at all. Gravity is only in the way while you are
				// actually leaning: upright, VRChat standing you up is exactly what you want.
				bool tilted = Quaternion.Angle(_tilt, Quaternion.identity) > 0.5f;
				HoldGravity(tilted && HoldGravityWanted);

				// ROTATE AROUND THE BODY, NOT AROUND THE FEET.
				//
				// Writing rig.rotation turns the transform about its own origin, which sits at the
				// player's feet — the body swings like a hinged plank and the viewpoint is carried
				// off sideways instead of staying where your head is. Psychloor's PlayerRotater does
				// the one thing this was missing:
				//
				//     playerTransform.RotateAround(originTransform.position, axis, angle)
				//
				// RotateAround moves POSITION as well as rotation, pivoting about a bone — the hips,
				// or the viewpoint. That is what makes the view come with you rather than needing a
				// second lever to drag it along.
				//
				// The absolute target is kept (Flip and ResetUpright need it), so the delta between
				// where the rig is and where it should be is what gets pivoted.
				Quaternion delta = want * Quaternion.Inverse(rig.rotation);
				float angle; Vector3 axis;
				delta.ToAngleAxis(out angle, out axis);
				if (angle > 180f) angle -= 360f;
				if (Mathf.Abs(angle) > 0.01f && axis.sqrMagnitude > 0.0001f)
					rig.RotateAround(Pivot(rig), axis.normalized, angle);
				else
					rig.rotation = want;               // nothing to pivot; keep the heading exact

				_applied = rig.rotation;
			}
			catch (Exception e) { Status = "rotator body: " + e.Message; }
		}

		// GRAVITY, THE PIECE THAT WAS MISSING ENTIRELY.
		//
		// Psychloor's PlayerRotater zeroes gravity for as long as it is rotating, and that is not a
		// detail: VRChat pulls the player down and stands them back up every frame, so a tilt that
		// is not held against gravity is fought to a standstill — which is exactly "nothing happens".
		//
		// It zeroes the GLOBAL Physics.gravity. We do not: GravityModule already owns that value,
		// saves the world's own figure and restores it, and a second writer is how one module ends
		// up recording the other's zero as "the original" — the orphan pattern this code base has
		// already been bitten by. VRCPlayerApi.SetGravityStrength is the per-player equivalent, is
		// the call worlds themselves use for low-gravity rooms, and touches nothing but you.
		private static bool _gravityHeld;
		private static float _gravityOriginal = 1f;

		private static bool HoldGravityWanted
		{
			get { try { return ModConfig.RotatorHoldGravity == null || ModConfig.RotatorHoldGravity.Value; } catch { return true; } }
		}

		private static void HoldGravity(bool hold)
		{
			try
			{
				var api = PlayerRef.LocalApi();
				if (api == null)
				{
					// No local player yet (arming during a load). Nothing was changed, so nothing is
					// owed back — but say so, because a rotator without this fights VRChat and loses.
					if (hold) VRChatArchiveModPlugin.Logger.LogInfo(
						"[Rotator] no local player yet — gravity left alone; tilt may be fought until you respawn.");
					return;
				}

				if (hold && !_gravityHeld)
				{
					try { _gravityOriginal = api.GetGravityStrength(); } catch { _gravityOriginal = 1f; }
					if (_gravityOriginal <= 0.001f) _gravityOriginal = 1f;   // never "restore" to a zero we set
					api.SetGravityStrength(0f);
					_gravityHeld = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[Rotator] your gravity held at 0 while tilted (world untouched).");
				}
				else if (!hold && _gravityHeld)
				{
					api.SetGravityStrength(_gravityOriginal);
					_gravityHeld = false;
					VRChatArchiveModPlugin.Logger.LogInfo("[Rotator] your gravity put back to " + _gravityOriginal + ".");
				}
			}
			catch (Exception e)
			{
				try { VRChatArchiveModPlugin.Logger.LogWarning("[Rotator] gravity: " + e.Message); } catch { }
			}
		}

		// What the tilt turns about. The hips are the reference implementation's default origin and
		// the one that feels like turning yourself rather than being swung on a rope; the head is
		// the fallback, and the rig's own position plus a metre is the last resort so a missing
		// Animator degrades instead of throwing.
		private static Transform _animCached;
		private static float _animCheckedAt;

		private static Vector3 Pivot(Transform rig)
		{
			try
			{
				float now = VaClock.Now;
				Animator anim = null;
				if (_animCached != null && now - _animCheckedAt < 1f && NativeGuard.Alive(_animCached))
					anim = _animCached.GetComponent<Animator>();
				if (anim == null)
				{
					_animCheckedAt = now;
					anim = rig.GetComponentInChildren<Animator>(true);
					_animCached = anim != null ? anim.transform : null;
				}
				if (anim != null && anim.isHuman)
				{
					var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
					if (hips != null) return hips.position;
					var head = anim.GetBoneTransform(HumanBodyBones.Head);
					if (head != null) return head.position;
				}
			}
			catch { }
			return rig.position + Vector3.up;
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// A new world rebuilds the player: every cached proxy here is stale, and the fresh neck
			// rotator comes back with the stock clamp.
			_rig = null; _neck = null; _rangeProp = null; _rangeOriginal = null;
			_neckResolved = false; _neckWidened = false; _holdProbed = false;
			_applied = Quaternion.identity;
			_animCached = null;

			// The new world rebuilds the player at ITS own gravity, so the figure we saved belonged
			// to the old one — keeping it would mean "restoring" a number from somewhere else, the
			// mistake GravityModule documents on this exact value. Forget it, then take the hold
			// again on the fresh player if we are still armed.
			_gravityHeld = false;
			_gravityOriginal = 1f;

			if (Active) { HoldGravity(true); WidenNeck(); }
		}

		public override void OnShutdown()
		{
			try { RestoreNeck(); } catch { }
			try { HoldGravity(false); } catch { }
		}

		// ------------------------------------------------------------------ body

		private static float Speed()
		{
			try { return ModConfig.RotatorSpeed != null ? Mathf.Clamp(ModConfig.RotatorSpeed.Value, 5f, 720f) : 120f; }
			catch { return 120f; }
		}

		private static float Wrap(float a)
		{
			while (a >= 360f) a -= 360f;
			while (a < 0f) a += 360f;
			return a;
		}

		// The topmost transform of the local player rig. Cached, and revalidated a few times a
		// second rather than every frame — NativeGuard.Alive is two VirtualQuery syscalls, and this
		// runs in LateUpdate.
		private static Transform Rig()
		{
			float now = VaClock.Now;
			if (_rig != null)
			{
				if (now - _rigCheckedAt < 0.25f) return _rig;
				_rigCheckedAt = now;
				if (NativeGuard.Alive(_rig)) return _rig;
				_rig = null;
			}
			_rigCheckedAt = now;
			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local == null) return null;
				Transform t = local.transform;
				int guard = 0;
				while (t.parent != null && guard++ < 32) t = t.parent;
				_rig = t;
			}
			catch { _rig = null; }
			return _rig;
		}

		private static void RestoreBody()
		{
			try
			{
				Transform rig = Rig();
				// Unresolvable right now: leave the request standing so OnLateUpdate retries it every
				// frame until it lands. Dropping it here is what left players permanently tilted.
				if (rig == null) { _restorePending = true; return; }
				rig.rotation = Quaternion.AngleAxis(_yaw, Vector3.up);
				_applied = rig.rotation;
				_restorePending = false;
			}
			catch { _restorePending = true; }
		}

		// ------------------------------------------------------------------ view (the neck clamp)

		// Reaching NeckMouseRotator: the same route MovementModule already proved on this build —
		// the local player's Behaviours, matched by il2cpp class name (never by the generated member
		// names, which change every update), then the controller member whose TYPE is
		// NeckMouseRotator. GamelikeInputController derives from LocomotionInputController, so the
		// inherited field_Protected_NeckMouseRotator_0 is right there on it.
		private static void ResolveNeck()
		{
			if (_neckResolved) return;
			_neckResolved = true;

			// field_* members are il2cpp FIELD reads, and on this build Il2CppInterop computes field
			// offsets from the wrong slot until FieldOffsetFix has repaired it. An unrepaired read
			// lands outside the object: an access violation .NET cannot catch. So this does not run
			// at all until the repair is confirmed.
			if (!FieldOffsetFix.Verified)
			{
				_neckWhy = "field offsets not repaired on this build";
				_neckResolved = false;   // try again later; FieldOffsetFix installs during startup
				return;
			}

			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local == null) { _neckWhy = "no local player yet"; _neckResolved = false; return; }

				Transform root = local.transform;
				int guard = 0;
				while (root.parent != null && guard++ < 32) root = root.parent;

				// FOUND BY SHAPE, NOT BY NAME.
				//
				// This used to insist the component be called GamelikeInputController or
				// LocomotionInputController. Those names are obfuscated afresh on every VRChat
				// build, so on this one the match never happened and the module reported, every
				// single time, "view clamp unchanged: no desktop locomotion controller (VR, or
				// renamed this build)" — the body tilted and the camera never followed, which is
				// exactly the half-armed feature this file's own header warns about.
				//
				// What cannot be renamed away is the SHAPE: the controller is whichever component
				// holds a NeckMouseRotator. VRC.DataModel types are hand-written and the obfuscator
				// leaves them alone — the same anchor ApiAvatar provides elsewhere in this mod.
				object ctl = null;
				PropertyInfo neckProp = null;

				foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
				{
					if (b == null) continue;
					string n;
					try { n = MenuCard.Il2CppNameOf(b); } catch { continue; }
					if (string.IsNullOrEmpty(n)) continue;

					Type ct = null;
					foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						try { ct = asm.GetType(n, false); } catch { }
						if (ct != null) break;
					}
					if (ct == null) continue;

					PropertyInfo found = null;
					try
					{
						foreach (var pi in ct.GetProperties(
							BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy))
						{
							if (pi.PropertyType.Name != "NeckMouseRotator") continue;
							found = pi; break;
						}
					}
					catch { continue; }
					if (found == null) continue;

					try
					{
						var tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
							.GetMethod("TryCast").MakeGenericMethod(ct);
						object cast = tryCast.Invoke(b, null);
						if (cast == null) continue;
						ctl = cast; neckProp = found;
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[Rotator] view controller found by shape: '" + n + "' holds a NeckMouseRotator.");
						break;
					}
					catch { }
				}

				if (ctl != null && neckProp != null)
				{
					try { _neck = neckProp.GetValue(ctl); }
					catch (Exception e) { _neckWhy = "neck read threw: " + e.Message; return; }
				}

				// STOP LOOKING FOR THE OWNER, LOOK FOR THE THING.
				//
				// Two searches have now failed on this build, and both were looking for the wrong
				// object: first the controller by NAME (obfuscated, never matched), then any
				// component under the PLAYER holding one (it is not under the player). Measured
				// both times in the log — "no desktop locomotion controller", then "nothing on the
				// player holds a NeckMouseRotator".
				//
				// But the controller was never the goal; the NeckMouseRotator is. It is a component
				// like any other, it lives in Assembly-CSharp under its own hand-written name, and
				// Unity can be asked for every loaded instance of a type regardless of where in the
				// scene VRChat parented it. That is the same lookup UserMenuModule uses to find the
				// per-user page after the path-based searches failed there too.
				//
				// FindObjectsOfTypeAll, not FindObjectsOfType: it is on an object that may be
				// inactive, and the active-only query would not see it.
				if (_neck == null)
				{
					try
					{
						Type nmr = null;
						foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
						{
							try { nmr = asm.GetType("NeckMouseRotator", false); } catch { }
							if (nmr != null) break;
						}
						if (nmr == null) { _neckWhy = "type NeckMouseRotator not present on this build"; return; }

						var il2 = Il2CppInterop.Runtime.Il2CppType.From(nmr);
						var all = Resources.FindObjectsOfTypeAll(il2);

						// CAST, don't just take the reference. FindObjectsOfTypeAll hands back
						// UnityEngine.Object wrappers: reflecting on one of those finds Object's
						// members, not NeckMouseRotator's, and the NeckRange lookup below would
						// come up empty while the real member sat there the whole time.
						var tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
							.GetMethod("TryCast").MakeGenericMethod(nmr);

						for (int i = 0; all != null && i < all.Length; i++)
						{
							if (all[i] == null) continue;
							object cast = null;
							try { cast = tryCast.Invoke(all[i], null); } catch { }
							if (cast == null) continue;
							_neck = cast;
							VRChatArchiveModPlugin.Logger.LogInfo(
								"[Rotator] NeckMouseRotator found by type, anywhere in the scene ("
								+ all.Length + " instance(s)).");
							break;
						}
					}
					catch (Exception e) { _neckWhy = "type search threw: " + e.Message; return; }
				}

				if (_neck == null) { _neckWhy = "no NeckMouseRotator instance loaded (VR, or not built yet)"; return; }

				foreach (var pi in _neck.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (pi.PropertyType.Name != "NeckRange") continue;
					_rangeProp = pi; break;
				}
				if (_rangeProp == null) { _neckWhy = "NeckMouseRotator has no NeckRange member"; return; }

				_neckWhy = "ready";
			}
			catch (Exception e) { _neckWhy = "resolve threw: " + e.Message; }
		}

		private static void WidenNeck()
		{
			try
			{
				if (!ModConfig.RotatorFreeLook.Value) { _neckWhy = "free look switched off"; return; }
				ResolveNeck();
				if (_neck == null || _rangeProp == null) return;
				if (_neckWidened) return;

				object range;
				try { range = _rangeProp.GetValue(_neck); }
				catch (Exception e) { _neckWhy = "range read threw: " + e.Message; return; }
				if (range == null) { _neckWhy = "range read null"; return; }

				FieldInfo[] fs = Floats(range.GetType());
				if (fs == null) { _neckWhy = "NeckRange is not the expected three floats"; return; }

				float a = (float)fs[0].GetValue(range);
				float b = (float)fs[1].GetValue(range);
				float c = (float)fs[2].GetValue(range);
				_rangeOriginal = range;

				// WHICH TWO ARE THE CLAMP. VRChat's stock desktop neck is a symmetric-ish pitch limit
				// (something near ±70°), so the pair to widen is the one that straddles zero. Reading
				// it rather than assuming the ctor's argument order is the whole reason this is safe
				// to ship: if the layout is not what the decompile suggested, the log says so and
				// nothing is written.
				float lim = Mathf.Clamp(ModConfig.RotatorNeckLimit.Value, 90f, 1800f);
				object widened = Activator.CreateInstance(range.GetType());
				fs[0].SetValue(widened, a); fs[1].SetValue(widened, b); fs[2].SetValue(widened, c);

				bool ok = false;
				if (a < 0f && b > 0f) { fs[0].SetValue(widened, -lim); fs[1].SetValue(widened, lim); ok = true; }
				else if (b < 0f && c > 0f) { fs[1].SetValue(widened, -lim); fs[2].SetValue(widened, lim); ok = true; }

				VRChatArchiveModPlugin.Logger.LogInfo(
					"[Rotator] NeckRange as found: (" + a.ToString("F1") + ", " + b.ToString("F1") + ", "
					+ c.ToString("F1") + ")" + (ok ? " -> widened to ±" + lim.ToString("F0") + "°"
					: " — no pair straddles zero, so the clamp was left alone"));

				if (!ok) { _neckWhy = "could not tell which floats are the clamp"; return; }

				try { _rangeProp.SetValue(_neck, widened); }
				catch (Exception e) { _neckWhy = "range write threw: " + e.Message; return; }

				// Read back. A write that did not take is worse than no write, because everything
				// downstream would report a feature that is not there.
				try
				{
					object after = _rangeProp.GetValue(_neck);
					float a2 = (float)fs[0].GetValue(after);
					float b2 = (float)fs[1].GetValue(after);
					float c2 = (float)fs[2].GetValue(after);
					_neckWidened = Mathf.Abs(a2 - a) > 1f || Mathf.Abs(b2 - b) > 1f || Mathf.Abs(c2 - c) > 1f;
					_neckWhy = _neckWidened ? "widened" : "write did not stick";
				}
				catch { _neckWidened = true; _neckWhy = "widened (read-back unavailable)"; }

				VRChatArchiveModPlugin.Logger.LogInfo("[Rotator] free look: " + _neckWhy
					+ ". Mouse pitch is unclamped — look up and keep going to end up head-down.");
			}
			catch (Exception e) { _neckWhy = "widen threw: " + e.Message; }
		}

		private static void RestoreNeck()
		{
			try
			{
				if (!_neckWidened || _neck == null || _rangeProp == null || _rangeOriginal == null) return;
				_rangeProp.SetValue(_neck, _rangeOriginal);
				_neckWidened = false;
				VRChatArchiveModPlugin.Logger.LogInfo("[Rotator] neck clamp put back the way it was.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Rotator] could not restore the neck clamp: " + e.Message); }
		}

		// NeckRange's three floats sit at fixed managed offsets (0/4/8) rather than behind the
		// il2cpp offset lookup, so these are ordinary field reads — but the ORDER is still taken
		// from the type, not hardcoded.
		private static FieldInfo[] Floats(Type t)
		{
			try
			{
				var all = t.GetFields(BindingFlags.Instance | BindingFlags.Public);
				int n = 0;
				for (int i = 0; i < all.Length; i++) if (all[i].FieldType == typeof(float)) n++;
				if (n != 3) return null;
				var outp = new FieldInfo[3];
				int k = 0;
				for (int i = 0; i < all.Length; i++) if (all[i].FieldType == typeof(float)) outp[k++] = all[i];
				return outp;
			}
			catch { return null; }
		}
	}
}
