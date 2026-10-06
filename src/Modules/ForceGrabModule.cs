using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FORCE GRAB — aim at an object, take it, keep it in your hands, use it.
	//
	// This removes the WALK, not the permission. A VRC_Pickup is an object the world author marked
	// grabbable by anyone, and ownership is taken through the SDK's own Networking.SetOwner — the
	// same call that runs when you pick something up by hand.
	//
	// It has to hold the object ITSELF. VRCSDKBase exposes only getters on VRC_Pickup — IsHeld,
	// currentPlayer — and no "grab this" method; the real hold lives inside the game's own pickup
	// system. So while it is held the object is carried in front of the camera every frame, and
	// released on a second press.
	//
	// One guard, and it is the one that matters: NOTHING HELD BY SOMEONE ELSE. That is the whole
	// difference between fetching a prop and yanking it out of another player's hands.
	public class ForceGrabModule : IModule
	{
		public override string Name => "ForceGrab";

		public static string Status = "";
		// Read by ModControlModule.BuildSync (main thread) and sent to the client as
		// "forceGrab" / "forceGrabName", which is how the client's button knows to say DROP <name>.
		// Both are Unity-null aware: a held object destroyed by the world reads as not held.
		public static bool Holding => _held != null || _heldRaw != null;
		public static string HeldName = "";

		private static VRC.SDKBase.VRC_Pickup _held;
		private static Rigidbody _heldBody;
		private static bool _wasKinematic;
		private bool _wasDown, _useWasDown;

		// RAW OBJECTS \u2014 the "even if it's locked" path. A VRC_Pickup is only what a world MARKED
		// grabbable; a locked prop is a mesh bolted in place with no pickup component at all, so the
		// pickup search never sees it. When that search comes up empty and ForceGrabAnyObject is on,
		// the object under the crosshair is carried by its transform directly.
		//
		// LOCAL, and that is the whole point of it being allowed: no ownership is taken and nothing is
		// networked on this path, so a locked prop moves on YOUR screen and world state is untouched.
		// A size cap keeps it from grabbing the floor and dragging the room across your view.
		private static Transform _heldRaw;
		private static Rigidbody _rawBody;
		private static bool _rawWasKinematic;

		// Udon's own pickup event names. Sending them is what makes a world's gun, tool or button
		// respond the way it does for a normal holder — the world then networks its own reaction,
		// exactly as if you had walked over and used it.
		private const string EvPickup = "_onPickup";
		private const string EvDrop = "_onDrop";
		private const string EvUseDown = "_onPickupUseDown";
		private const string EvUseUp = "_onPickupUseUp";

		public override void OnUpdate()
		{
			try
			{
				// RightShift+G now belongs to FORCE PICKUP, which is what people actually want from a
				// 'grab' key: it unlocks the world's pickups so you hold them for real. This older
				// teleport-it-in-front-of-you carry has NO hotkey: the client's FUN → Force Grab
				// button sends "forceGrab" (ModControlModule → Toggle()) and reads Holding/HeldName
				// back from the sync to label itself GRAB or DROP. The combo below is kept false on
				// purpose so nobody re-binds it onto a key another module already owns.
				bool combo = false;
				if (combo && !_wasDown) Toggle();
				_wasDown = combo;

				if (_held == null) return;

				// USE. Left click while holding, the same button that uses a pickup normally. The
				// old `!Menu.Visible` guard is gone: that IMGUI menu is sealed and can never be
				// visible, so the check was always true and only read as if a menu could still
				// claim the mouse. The grab/drop button itself lives in the desktop client (FUN tab).
				bool use = Input.GetMouseButton(0);
				if (use && !_useWasDown) Fire(EvUseDown);
				else if (!use && _useWasDown) Fire(EvUseUp);
				_useWasDown = use;
			}
			catch { }
		}

		// Carried in LateUpdate so it lands after the camera has moved for this frame — doing it in
		// Update leaves the object one frame behind your view, which reads as rubber-banding.
		public override void OnLateUpdate()
		{
			try
			{
				// Raw carry: drive its transform to the same spot in front of the camera.
				if (_heldRaw != null)
				{
					var rcam = Camera.main; if (rcam == null) return;
					var rct = rcam.transform;
					_heldRaw.position = rct.position + rct.forward * 0.75f - rct.up * 0.15f;
					_heldRaw.rotation = rct.rotation;
					if (_rawBody != null) { _rawBody.velocity = Vector3.zero; _rawBody.angularVelocity = Vector3.zero; }
					return;
				}

				if (_held == null) return;
				var t = _held.transform;
				if (t == null) { Drop(); return; }

				var cam = Camera.main;
				if (cam == null) return;
				var ct = cam.transform;

				t.position = ct.position + ct.forward * 0.75f - ct.up * 0.15f;
				t.rotation = ct.rotation;

				// A physics pickup would otherwise fight us every frame and jitter.
				if (_heldBody != null)
				{
					_heldBody.velocity = Vector3.zero;
					_heldBody.angularVelocity = Vector3.zero;
				}
			}
			catch { }
		}

		// DROP, DON'T JUST FORGET (2026-09-13). This used to null the references outright. When the
		// held object survived the load — additive and UI scene loads fire this too — it kept the
		// isKinematic = true we forced on it and never received the _onDrop event, so the world's own
		// Udon still believed the prop was in someone's hand, permanently. Drop() performs the real
		// release and is already safe on a destroyed target (it proves liveness before every write),
		// so it is the correct call in both cases.
		public override void OnSceneLoaded(int buildIndex)
		{
			// A held object that SURVIVED the load must be really released — physics handed back and
			// _onDrop fired — or the world's Udon goes on believing it is in someone's hand forever.
			// One that did NOT survive has nothing to restore, and every write in Drop() is guarded
			// by a liveness proof anyway, so this is safe either way.
			Drop();
			_held = null; _heldBody = null; _heldRaw = null; _rawBody = null; HeldName = "";
		}
		public override void OnShutdown() => Drop();

		// ------------------------------------------------------------------ grab / drop

		public static void Toggle()
		{
			if (_held != null || _heldRaw != null) { Drop(); return; }
			Grab();
		}

		// AIMED, not nearest. You look at the thing you want; picking the closest one instead is
		// what made the first version useless in a room with several objects.
		public static void Grab()
		{
			try
			{
				var cam = Camera.main;
				if (cam == null) { Status = "no camera"; return; }

				float range = Mathf.Max(1f, ModConfig.ForceGrabRange.Value);
				var ray = new Ray(cam.transform.position, cam.transform.forward);

				// ALL LAYERS, AND TRIGGERS INCLUDED. The default overload skips both, and a great
				// many pickups are trigger colliders on layers the default mask drops — so the ray
				// went straight through the thing you were looking at and reported nothing, without
				// logging a word. That is why this looked dead rather than broken.
				var hits = Physics.RaycastAll(ray, range, ~0, QueryTriggerInteraction.Collide);
				VRC.SDKBase.VRC_Pickup best = null;
				float bestD = float.MaxValue;

				if (hits != null)
					foreach (var h in hits)
					{
						try
						{
							if (h.collider == null) continue;
							var p = Find(h.collider.transform);
							if (p == null) continue;
							bool held = false;
							try { held = p.IsHeld; } catch { }
							if (held) continue;                      // in someone's hands: leave it
							if (h.distance >= bestD) continue;
							bestD = h.distance; best = p;
						}
						catch { }
					}

				// Still nothing: fall back to the pickup CLOSEST TO WHERE YOU ARE LOOKING, by angle.
				// A ray is a line with no thickness, so a small object needs pixel-perfect aim; this
				// forgives a few degrees, which is what "I'm looking right at it" actually means.
				if (best == null)
				{
					const float maxAngle = 12f;
					float bestAngle = maxAngle;
					try
					{
						// Non-generic enumeration: FindObjectsOfType<VRC_Pickup>() is empty on this build (see ForcePickup).
						var il2g = Il2CppInterop.Runtime.Il2CppType.Of<VRC.SDKBase.VRC_Pickup>();
						var foundG = VRChatArchiveMod.Core.Live.AllOfType(il2g);
						for (int gi = 0; foundG != null && gi < foundG.Length; gi++)
						{
							var p = foundG[gi] != null ? foundG[gi].TryCast<VRC.SDKBase.VRC_Pickup>() : null;
							if (p == null || p.transform == null) continue;
							bool held = false;
							try { held = p.IsHeld; } catch { }
							if (held) continue;
							Vector3 to = p.transform.position - ray.origin;
							float d = to.magnitude;
							if (d > range || d < 0.01f) continue;
							float ang = Vector3.Angle(ray.direction, to);
							if (ang >= bestAngle) continue;
							bestAngle = ang; bestD = d; best = p;
						}
					}
					catch { }
					if (best != null)
						VRChatArchiveModPlugin.Logger.LogInfo($"[ForceGrab] no ray hit; nearest to crosshair at {bestAngle:F1}deg.");
				}

				if (best == null)
				{
					// LOCKED / NON-PICKUP fallback: no VRC_Pickup here, so carry the raw object if allowed.
					bool anyObj = true;
					try { anyObj = ModConfig.ForceGrabAnyObject.Value; } catch { }
					if (anyObj && GrabRaw(ray, range)) return;

					Status = "nothing grabbable under your crosshair (within " + range.ToString("F0") + "m)";
					VRChatArchiveModPlugin.Logger.LogInfo(
						$"[ForceGrab] miss — {(hits == null ? 0 : hits.Length)} collider(s) on the ray, no VRC_Pickup within {range:F0}m of the crosshair.");
					return;
				}

				var go = best.gameObject;
				try
				{
					var lp = VRC.SDKBase.Networking.LocalPlayer;
					if (lp != null && !VRC.SDKBase.Networking.IsOwner(lp, go))
						VRC.SDKBase.Networking.SetOwner(lp, go);
				}
				catch (Exception oe) { VRChatArchiveModPlugin.Logger.LogWarning("[ForceGrab] ownership: " + oe.Message); }

				_held = best;
				HeldName = go.name;
				_heldBody = null;
				try
				{
					_heldBody = go.GetComponent<Rigidbody>();
					if (_heldBody != null)
					{
						_wasKinematic = _heldBody.isKinematic;
						_heldBody.isKinematic = true;    // we drive the transform while it is held
					}
				}
				catch { }

				Fire(EvPickup);
				Status = "holding " + Trunc(HeldName, 30) + "  ·  click to use, press the client button to drop";
				VRChatArchiveModPlugin.Logger.LogInfo($"[ForceGrab] grabbed {HeldName} at {bestD:F1}m");
			}
			catch (Exception e)
			{
				Status = "grab failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ForceGrab] " + e.Message);
			}
		}

		// The raw carry. Nearest collider under the ray, its rigidbody root if it has one (so aiming
		// at a prop does not walk up and grab the whole rig it hangs under), with a size sanity cap.
		private static bool GrabRaw(Ray ray, float range)
		{
			try
			{
				var hits = Physics.RaycastAll(ray, range, ~0, QueryTriggerInteraction.Collide);
				if (hits == null || hits.Length == 0) return false;

				RaycastHit chosen = default; float bestD = float.MaxValue; bool found = false;
				foreach (var h in hits)
				{
					if (h.collider == null || h.distance >= bestD) continue;
					bestD = h.distance; chosen = h; found = true;
				}
				if (!found || chosen.collider == null) return false;

				Transform t = chosen.collider.attachedRigidbody != null
					? chosen.collider.attachedRigidbody.transform
					: chosen.collider.transform;
				if (t == null) return false;

				// SIZE SANITY. Grabbing the floor or a room shell would drag the whole world across your
				// view. Anything whose visible bounds span more than this is structure, not a prop.
				try
				{
					Bounds b = default; bool has = false;
					foreach (var r in t.GetComponentsInChildren<Renderer>())
					{
						if (r == null) continue;
						if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
					}
					if (has && b.size.magnitude > 8f) { Status = "that is world structure, too big to grab"; return false; }
				}
				catch { }

				_heldRaw = t;
				HeldName = t.name ?? "?";
				_rawBody = chosen.collider.attachedRigidbody;
				if (_rawBody != null) { _rawWasKinematic = _rawBody.isKinematic; _rawBody.isKinematic = true; }
				Status = "holding " + Trunc(HeldName, 30) + " (locked \u2014 local carry)  \u00b7  press the client button to drop";
				VRChatArchiveModPlugin.Logger.LogInfo($"[ForceGrab] raw-grabbed {HeldName} at {bestD:F1}m (local carry, no networking).");
				return true;
			}
			catch (Exception e) { Status = "raw grab failed: " + e.Message; return false; }
		}

		public static void Drop()
		{
			// Raw carry first: restore its physics and let go, no events, nothing networked.
			if (_heldRaw != null)
			{
				try { if (_rawBody != null) { _rawBody.isKinematic = _rawWasKinematic; _rawBody.velocity = Vector3.zero; } } catch { }
				Status = "dropped " + Trunc(HeldName, 30);
				VRChatArchiveModPlugin.Logger.LogInfo("[ForceGrab] dropped (raw) " + HeldName);
				_heldRaw = null; _rawBody = null; HeldName = "";
				return;
			}
			try
			{
				if (_held == null) return;
				Fire(EvDrop);

				// Physics handed back the way we found it: leaving a world's object kinematic after
				// letting go would quietly break it for everyone until the instance restarts.
				if (_heldBody != null)
				{
					try
					{
						_heldBody.isKinematic = _wasKinematic;
						_heldBody.velocity = Vector3.zero;
						_heldBody.angularVelocity = Vector3.zero;
					}
					catch { }
				}
				Status = "dropped " + Trunc(HeldName, 30);
				VRChatArchiveModPlugin.Logger.LogInfo("[ForceGrab] dropped " + HeldName);
			}
			catch { }
			finally { _held = null; _heldBody = null; HeldName = ""; }
		}

		// ------------------------------------------------------------------ events

		// Sends a pickup event to whatever Udon sits on the object, on THIS client. The world's own
		// script decides what to do with it and networks its own response, which is what happens
		// when any player uses the item.
		private static void Fire(string ev)
		{
			try
			{
				if (_held == null) return;
				// LIVENESS BEFORE THE TOUCH. A destroyed il2cpp object keeps a non-null managed
				// wrapper, and reading .gameObject off it is an access violation .NET cannot catch —
				// it ends the process. Reached from OnSceneLoaded, where the held object may well
				// have just been destroyed, so the guard is load-bearing, not decorative.
				if (!Core.NativeGuard.Alive(_held)) return;
				var go = _held.gameObject;
				if (go == null || !Core.NativeGuard.Alive(go)) return;

				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var mi = ub?.GetMethod("SendCustomEvent", new[] { typeof(string) });
				if (mi == null) return;

				var il2 = Il2CppInterop.Runtime.Il2CppType.From(ub);
				var comps = go.GetComponentsSafe(il2);
				if (comps == null) return;
				for (int i = 0; i < comps.Length; i++)
				{
					try { mi.Invoke(comps[i], new object[] { ev }); }
					catch { }
				}
			}
			catch { }
		}

		// The pickup can be on the collider, or anywhere above it — a pickup's colliders usually
		// live on children.
		private static VRC.SDKBase.VRC_Pickup Find(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					var pick = p.GetComponent<VRC.SDKBase.VRC_Pickup>();
					if (pick != null) return pick;
				}
			}
			catch { }
			return null;
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			return null;
		}

		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
	}
}
