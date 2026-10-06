using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Local, self-only movement: desktop fly + noclip with arrow-key rotation. This only
	// moves the player's OWN avatar client-side — it does not touch, target, or affect
	// anyone else.
	//
	// Fly: Ctrl+F toggles it. Noclip: while flying, HOLD right-mouse to pass through walls
	// (release = solid). Float freely with:
	//   WASD + E/Q move relative to the camera, Shift = faster,
	//   arrow keys rotate (Left/Right = yaw, Up/Down = pitch). While flying the player holds
	//   position when no key is pressed instead of falling.
	//
	// Rather than editing global Physics.gravity (which would disturb the whole world's
	// physics and gets fought by VRChat), fly simply pins the transform each frame: velocity
	// is zeroed and, with no movement input, the position is re-asserted. Reflection-based so
	// it isn't bound to obfuscated signatures that rotate every VRChat build.
	public class MovementModule : IModule
	{
		public override string Name => "Movement";

		private bool _flying;
		private bool _noclip;
		private bool _flyHotkeyWasDown;
		private bool _flyWas;            // edge-detect fly so noclip can follow it on/off
		private readonly List<Collider> _disabled = new List<Collider>();
		private Vector3 _holdPos;
		private bool _hasHold;
		private Transform _vrcPlayerT;   // the real player root (capsule + camera live here)
		private Transform _capsuleT;     // the CharacterController's transform
		private bool _loggedHierarchy;
		private static Transform _cachedVrcPlayerLocal;
		private float _storedWalk = 2f, _storedStrafe = 2f, _storedRun = 4f;
		private bool _speedsSuppressed;

		public override void OnSceneLoaded(int buildIndex)
		{
			_cachedVrcPlayerLocal = null;
			_hasHold = false;
			_speedsSuppressed = false;
		}

		public override void OnUpdate()
		{
			try
			{
				bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand);
				bool menuOpen = Menu.Visible;   // the mod menu owns input while it's open

				// Ctrl+F edge-toggles FLY on/off (ignored while the menu owns input).
				bool flyHotkey = !menuOpen && ctrl && Input.GetKey(KeyCode.F);
				if (flyHotkey && !_flyHotkeyWasDown)
					ModConfig.FlyEnabled.Value = !ModConfig.FlyEnabled.Value;
				_flyHotkeyWasDown = flyHotkey;

				// FLY and NOCLIP travel together: turning fly on turns noclip on automatically, and
				// turning fly off drops it. The menu still exposes a noclip switch so you can fly
				// with collision back on, but the moment fly is (re)enabled noclip follows it.
				if (ModConfig.FlyEnabled.Value != _flyWas)
				{
					_flyWas = ModConfig.FlyEnabled.Value;
					ModConfig.NoclipEnabled.Value = _flyWas;
				}
				// Noclip without fly would just drop you through the floor.
				if (ModConfig.NoclipEnabled.Value && !ModConfig.FlyEnabled.Value)
					ModConfig.FlyEnabled.Value = _flyWas = true;

				// Click-teleport: hold right mouse + press left = teleport to the aimed surface.
				// Skipped while the mod menu is open so clicking buttons doesn't warp you.
				if (ModConfig.ClickTpEnabled.Value && !Menu.Visible
					&& Input.GetMouseButton(1) && Input.GetMouseButtonDown(0))
					ClickTeleport();

				// Apply state changes (edges).
				if (ModConfig.FlyEnabled.Value != _flying)
					SetFlying(ModConfig.FlyEnabled.Value);
				if (ModConfig.NoclipEnabled.Value != _noclip)
					SetNoclip(ModConfig.NoclipEnabled.Value);

				// Perform fly translation in Update so Unity's Animator and VRIK evaluate for the
				// current frame at the NEW position, eliminating any 1-frame avatar IK lag/clipping.
				if (_flying)
				{
					UpdateFly();

					// Keep locomotion speeds zeroed so VRChat's animator does not play strafe walk/run
					// animations that twist the spine and push the neck collar into the 1st-person camera.
					if (_speedsSuppressed)
					{
						var api = PlayerRef.LocalApi();
						if (api != null)
						{
							try
							{
								if (api.GetWalkSpeed() > 0.01f || api.GetStrafeSpeed() > 0.01f || api.GetRunSpeed() > 0.01f)
								{
									api.SetWalkSpeed(0f);
									api.SetStrafeSpeed(0f);
									api.SetRunSpeed(0f);
								}
							}
							catch { }
						}
					}
				}

				// Re-assert noclip every frame: VRChat re-enables the player collider on avatar
				// loads and locomotion resets, which would silently restore collision. If our
				// captured list has gone stale (avatar swap), re-scan.
				if (_noclip)
				{
					bool stale = _disabled.Count == 0;
					for (int i = 0; i < _disabled.Count; i++)
					{
						try { if (_disabled[i] == null) { stale = true; break; } if (_disabled[i].enabled) _disabled[i].enabled = false; }
						catch { stale = true; break; }
					}
					if (stale)
					{
						// RE-ENABLE THE SURVIVORS BEFORE REBUILDING THE LIST (2026-09-13).
						//
						// The loop above BREAKS on the first dead or throwing collider, so every
						// still-live collider AFTER it was left enabled = false and then dropped by
						// the Clear(): _disabled is the only ledger SetNoclip(false) walks, so those
						// colliders stayed disabled for the rest of the session and the player kept
						// passing through the world with noclip switched off. Putting them back first
						// costs nothing — they are re-collected and re-disabled immediately if they
						// are still ours — and is the whole difference between a rescan and a leak.
						for (int i = 0; i < _disabled.Count; i++)
						{
							try { var c = _disabled[i]; if (c != null && Core.NativeGuard.Alive(c)) c.enabled = true; } catch { }
						}
						_disabled.Clear(); CollectPlayerColliders(_disabled); DisableAll(_disabled);
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Movement] update threw: {e}");
			}
		}

		// Fly position pinning and rotation run in LateUpdate.
		public override void OnLateUpdate()
		{
			try
			{
				if (_flying && _hasHold)
				{
					// Re-assert position if no movement keys are currently held down so external
					// forces/gravity cannot drift the player.
					var root = GetVrcPlayerLocalTransform();
					if (root != null && !IsFlyMoving()) root.position = _holdPos;
				}
				UpdateRotate();   // arrow-key turning works on the ground too
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Movement] late update threw: {e}");
			}
		}

		public override void OnShutdown()
		{
			try { if (_noclip) SetNoclip(false); } catch { }
			try { if (_flying) SetFlying(false); } catch { }
		}

		// Raycasts from the camera and drops the player on the first solid surface hit. Desktop
		// VRChat locks the cursor to look, so we aim straight down the camera's forward (the
		// crosshair) rather than the mouse position. One-shot: sets position, zeroes velocity.
		private void ClickTeleport()
		{
			try
			{
				var cam = Camera.main;
				var player = PlayerRef.LocalPlayer();
				if (cam == null || player == null) return;

				Vector3 origin = cam.transform.position;
				Vector3 dir = cam.transform.forward;
				if (Physics.Raycast(origin, dir, out RaycastHit hit, ModConfig.ClickTpMaxDistance.Value))
				{
					// Stand slightly above the surface so we don't clip into it.
					Vector3 dest = hit.point + Vector3.up * 0.15f;
					var root = GetVrcPlayerLocalTransform();
					if (root != null) root.position = dest;
					else player.transform.position = dest;
					if (_flying) { _holdPos = dest; _hasHold = true; }
					PlayerRef.ZeroVelocity(player);
					VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] click-teleport to {dest} ({hit.distance:F1}m).");
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] click-teleport failed: {e.Message}");
			}
		}

		// Arrow-key turning. Four ways to rotate a player, tried in order, because
		// transform.rotation alone only spins the avatar MESH — the player and the desktop
		// view keep facing the old way, which is exactly why turning did nothing:
		//   0. TeleportTo(pos, rot, AlignPlayerWithSpawnPoint, lerpOnRemote: true) — the call
		//      OrbitModule turns the player with every frame, and the one that visibly works: the
		//      spawn-point orientation makes VRChat align the VIEW (desktop camera yaw) with the
		//      rotation, and lerpOnRemote keeps other clients from snapping. The plain 2-argument
		//      overload below is a SNAP by contract and stopped turning the desktop view on this
		//      build (it changed GetRotation() for one frame and the camera took it straight back).
		//   1. NeckMouseRotator, the desktop MOUSE-LOOK component itself (found on the local
		//      VRCPlayer through GamelikeInputController, the way EvilEye's HeadFlipper reaches it).
		//      It keeps its own yaw and re-applies it every frame, which is why an outside rotation
		//      can be "taken back"; its public (Vector3) method is fed the yaw delta like a mouse move.
		//   2. VRCPlayerApi.TeleportTo(pos, rot)  — the old sanctioned setter, kept as fallback
		//   3. VRCPlayer's own (Vector3, Quaternion) teleport
		//   4. transform rotation on the whole rig (last resort)
		// Whichever moves GetRotation() is remembered and used from then on. Pitch is not
		// offered: VRChat keeps players upright, so tilting leaves the rig crooked.
		private int _rotMethod = -1;      // -1 = not chosen yet
		private float _nextRotProbe;
		private bool _rotLogged;

		private void UpdateRotate()
		{
			if (!ModConfig.ArrowRotateEnabled.Value) return;

			float step = VaClock.Delta * ModConfig.FlyRotateSpeed.Value;
			float yaw = 0f;
			if (Input.GetKey(KeyCode.LeftArrow)) yaw -= step;
			if (Input.GetKey(KeyCode.RightArrow)) yaw += step;
			if (yaw == 0f) return;

			var api = PlayerRef.LocalApi();
			if (api == null)
			{
				if (!_rotLogged) { _rotLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[Movement] rotate: no local VRCPlayerApi — turning unavailable."); }
				return;
			}

			Quaternion before;
			Vector3 pos;
			try { before = api.GetRotation(); pos = api.GetPosition(); }
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] rotate: read failed: {e.Message}"); return; }

			// pure heading change: drop any pitch/roll the rig carries
			Vector3 fwd = before * Vector3.forward;
			fwd.y = 0f;
			if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
			Quaternion target = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.LookRotation(fwd.normalized, Vector3.up);

			int first = _rotMethod >= 0 ? _rotMethod : 0;
			int last = _rotMethod >= 0 ? _rotMethod : 4;
			for (int m = first; m <= last; m++)
			{
				if (!TryRotate(m, api, pos, target)) continue;

				// Did it take? Compare headings, not raw quaternions.
				Quaternion after;
				try { after = api.GetRotation(); } catch { after = before; }
				bool moved = Quaternion.Angle(after, before) > 0.01f;
				float nowP = VaClock.Now;
				if (nowP >= _nextRotProbe)
				{
					_nextRotProbe = nowP + 1f;
					VRChatArchiveModPlugin.Logger.LogInfo("[Movement] rotate probe: method #" + m + " asked " + yaw.ToString("F2") + " deg, heading " + before.eulerAngles.y.ToString("F1") + " -> " + after.eulerAngles.y.ToString("F1") + (moved ? "" : " (NO CHANGE)"));
				}
				if (moved || _rotMethod >= 0)
				{
					if (_rotMethod < 0)
					{
						_rotMethod = m;
						VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] rotate: using method #{m}.");
					}
					_holdPos = pos; _hasHold = true;
					return;
				}
			}

			if (_rotMethod < 0 && !_rotLogged)
			{
				_rotLogged = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[Movement] rotate: none of the 5 methods changed GetRotation() — turning unavailable on this build.");
			}
		}

		private bool TryRotate(int method, VRC.SDKBase.VRCPlayerApi api, Vector3 pos, Quaternion target)
		{
			try
			{
				switch (method)
				{
					case 0:
						api.TeleportTo(pos, target, VRC.SDKBase.VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, true);
						return true;
					case 1:
					{
						// Signed yaw delta from the current heading to the target heading, fed like a mouse move.
						Quaternion cur = api.GetRotation();
						Vector3 a = cur * Vector3.forward, b = target * Vector3.forward; a.y = 0f; b.y = 0f;
						float delta = Vector3.SignedAngle(a.normalized, b.normalized, Vector3.up);
						return NeckRotate(delta);
					}
					case 2:
						api.TeleportTo(pos, target);
						return true;
					case 3:
					{
						var local = PlayerRef.LocalPlayer();
						object vp = local == null ? null : FewTagsModule.GetMemberByTypeName(local, "VRCPlayer", "_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
						if (vp == null) return false;
						foreach (var mi in vp.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
						{
							var ps = mi.GetParameters();
							if (ps.Length != 2 || ps[0].ParameterType != typeof(Vector3) || ps[1].ParameterType != typeof(Quaternion)) continue;
							mi.Invoke(vp, new object[] { pos, target });
							return true;
						}
						return false;
					}
					default:
					{
						var local = PlayerRef.LocalPlayer();
						if (local == null) return false;
						Transform root = local.transform;
						int guard = 0;
						while (root.parent != null && guard++ < 32) root = root.parent;
						root.rotation = target;
						local.transform.rotation = target;
						return true;
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] rotate method #{method} threw: {e.Message}");
				return false;
			}
		}

		// THE MOUSE-LOOK PATH. GamelikeInputController (desktop locomotion) holds a NeckMouseRotator,
		// the component the mouse drives. Everything is resolved by TYPE NAME, never by the generated
		// member names, which change per build: the controller is found among the local player's
		// Behaviours by il2cpp class name, the rotator is the controller's member whose type is
		// NeckMouseRotator, and the call is its public method with exactly one Vector3 parameter
		// (yaw goes in .y, the axis the mouse X moves). Semantics are probed, not assumed: the
		// caller checks GetRotation() moved, and the once-a-second probe logs the headings.
		private static object _neck;                      // the NeckMouseRotator proxy
		private static System.Reflection.MethodInfo _neckTurn;
		private static bool _neckResolved;
		private static bool NeckRotate(float yawDegrees)
		{
			try
			{
				if (!_neckResolved)
				{
					_neckResolved = true;
					var local = PlayerRef.LocalPlayer();
					if (local == null) return false;
					Transform root = local.transform; int guard = 0;
					while (root.parent != null && guard++ < 32) root = root.parent;
					object ctl = null;
					foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
					{
						if (b == null) continue;
						string n; try { n = MenuCard.Il2CppNameOf(b); } catch { continue; }
						if (n != "GamelikeInputController") continue;
						Type ct = null;
						foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { ct = asm.GetType("GamelikeInputController", false); } catch { } if (ct != null) break; }
						if (ct == null) break;
						var tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(ct);
						ctl = tryCast.Invoke(b, null);
						break;
					}
					if (ctl == null) { VRChatArchiveModPlugin.Logger.LogInfo("[Movement] rotate: no GamelikeInputController on the local player (VR, or renamed)."); return false; }
					foreach (var pi in ctl.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
					{
						if (pi.PropertyType.Name != "NeckMouseRotator") continue;
						try { _neck = pi.GetValue(ctl); } catch { }
						if (_neck != null) break;
					}
					if (_neck == null) { VRChatArchiveModPlugin.Logger.LogInfo("[Movement] rotate: GamelikeInputController has no NeckMouseRotator member."); return false; }
					foreach (var mi in _neck.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
					{
						var ps = mi.GetParameters();
						if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3) && mi.ReturnType == typeof(void) && !mi.Name.Contains("PDM")) { _neckTurn = mi; break; }
					}
					if (_neckTurn == null)
						foreach (var mi in _neck.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
						{
							var ps = mi.GetParameters();
							if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3) && mi.ReturnType == typeof(void)) { _neckTurn = mi; break; }
						}
					VRChatArchiveModPlugin.Logger.LogInfo("[Movement] rotate: NeckMouseRotator " + (_neckTurn != null ? "reached via " + _neckTurn.Name : "has no (Vector3) method"));
				}
				if (_neck == null || _neckTurn == null) return false;
				_neckTurn.Invoke(_neck, new object[] { new Vector3(0f, yawDegrees, 0f) });
				return true;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Movement] rotate: neck path threw: " + e.Message);
				_neckTurn = null;
				return false;
			}
		}

		public static Transform GetVrcPlayerLocalTransform()
		{
			if (_cachedVrcPlayerLocal != null && NativeGuard.Alive(_cachedVrcPlayerLocal))
				return _cachedVrcPlayerLocal;

			try
			{
				var p = PlayerRef.LocalPlayer();
				if (p != null && NativeGuard.Alive(p))
				{
					var r = p.transform.root;
					if (r != null && r.name.StartsWith("VRCPlayer[Local]"))
					{
						_cachedVrcPlayerLocal = r;
						VRChatArchiveModPlugin.Logger.LogInfo("[Movement] Resolved VRCPlayer[Local] from LocalPlayer root: " + r.name);
						return _cachedVrcPlayerLocal;
					}
				}
			}
			catch { }

			try
			{
				var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
				if (scene.isLoaded)
				{
					var roots = scene.GetRootGameObjects();
					if (roots != null)
					{
						for (int i = 0; i < roots.Length; i++)
						{
							var r = roots[i];
							if (r != null && r.name.StartsWith("VRCPlayer[Local]"))
							{
								_cachedVrcPlayerLocal = r.transform;
								VRChatArchiveModPlugin.Logger.LogInfo("[Movement] Resolved VRCPlayer[Local] from active scene roots: " + r.name);
								return _cachedVrcPlayerLocal;
							}
						}
					}
				}
			}
			catch { }

			try
			{
				var all = Resources.FindObjectsOfTypeAll<GameObject>();
				if (all != null)
				{
					for (int i = 0; i < all.Count; i++)
					{
						var go = all[i];
						if (go != null && go.activeInHierarchy && go.name.StartsWith("VRCPlayer[Local]"))
						{
							_cachedVrcPlayerLocal = go.transform;
							VRChatArchiveModPlugin.Logger.LogInfo("[Movement] Resolved VRCPlayer[Local] from Resources scan: " + go.name);
							return _cachedVrcPlayerLocal;
						}
					}
				}
			}
			catch { }

			var fallback = PlayerRef.LocalTransform();
			if (fallback != null)
			{
				Transform r = fallback;
				int guard = 0;
				while (r.parent != null && guard++ < 32) r = r.parent;
				return r;
			}
			return null;
		}

		private static bool IsFlyMoving()
		{
			return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) ||
			       Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.A) ||
			       Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Q);
		}

		private void UpdateFly()
		{
			var root = GetVrcPlayerLocalTransform();
			var cam = Camera.main;
			if (root == null || cam == null) return;

			var player = PlayerRef.LocalPlayer();
			if (player != null) PlayerRef.ZeroVelocity(player);

			Transform ct = cam.transform;

			// Translation relative to the camera directly applied to VRCPlayer[Local].
			float speed = VaClock.Delta * (Input.GetKey(KeyCode.LeftShift) ? ModConfig.FlyBoostSpeed.Value : ModConfig.FlySpeed.Value);
			Vector3 move = Vector3.zero;
			if (Input.GetKey(KeyCode.W)) move += ct.forward;
			if (Input.GetKey(KeyCode.S)) move -= ct.forward;
			if (Input.GetKey(KeyCode.D)) move += ct.right;
			if (Input.GetKey(KeyCode.A)) move -= ct.right;
			if (Input.GetKey(KeyCode.E)) move += Vector3.up;
			if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

			if (move != Vector3.zero)
			{
				root.position += move * speed;
				_holdPos = root.position;   // moved this frame → new anchor
				_hasHold = true;
			}
			else if (_hasHold)
			{
				// No input: re-assert last position so gravity/velocity can't drift us down.
				root.position = _holdPos;
			}
			else
			{
				_holdPos = root.position;
				_hasHold = true;
			}
		}

		// Fly on/off. Enabling captures the current position as the hold anchor so we float
		// in place instead of falling.
		private void SetFlying(bool value)
		{
			_flying = value;
			if (value)
			{
				var root = GetVrcPlayerLocalTransform();
				if (root != null) { _holdPos = root.position; _hasHold = true; }
				var api = PlayerRef.LocalApi();
				if (api != null)
				{
					try
					{
						_storedWalk = api.GetWalkSpeed();
						_storedStrafe = api.GetStrafeSpeed();
						_storedRun = api.GetRunSpeed();
						api.SetWalkSpeed(0f);
						api.SetStrafeSpeed(0f);
						api.SetRunSpeed(0f);
						_speedsSuppressed = true;
					}
					catch { }
				}
			}
			else
			{
				_hasHold = false;
				if (_speedsSuppressed)
				{
					_speedsSuppressed = false;
					var api = PlayerRef.LocalApi();
					if (api != null)
					{
						try
						{
							api.SetWalkSpeed(_storedWalk > 0.1f ? _storedWalk : 2f);
							api.SetStrafeSpeed(_storedStrafe > 0.1f ? _storedStrafe : 2f);
							api.SetRunSpeed(_storedRun > 0.1f ? _storedRun : 4f);
						}
						catch { }
					}
				}
				// Leaving fly must also drop noclip so collision comes back.
				if (_noclip) SetNoclip(false);
			}
			VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] fly {(value ? "ON" : "OFF")}.");
		}

		// Noclip on/off: disables the local player's solid colliders so you pass through world
		// geometry (CharacterController derives from Collider, so one scan covers the capsule
		// and the controller). They live on the locomotion rig, not on the VRC.Player object,
		// so we scan the whole local-player hierarchy.
		private void SetNoclip(bool value)
		{
			try
			{
				if (value)
				{
					_disabled.Clear();
					CollectPlayerColliders(_disabled);
					DisableAll(_disabled);
					VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] noclip ON — disabled {_disabled.Count} collider(s)"
						+ (_disabled.Count == 0 ? " (NONE FOUND — noclip won't work; tell me)" : "") + ".");
				}
				else
				{
					int restored = 0;
					for (int i = 0; i < _disabled.Count; i++)
					{
						try
						{
							var col = _disabled[i];
							if (col != null)
							{
								if (col is CharacterController cc)
								{
									try { cc.detectCollisions = true; } catch { }
								}
								col.enabled = true;
								restored++;
							}
						}
						catch { }
					}
					_disabled.Clear();
					VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] noclip OFF — restored {restored} collider(s).");
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] noclip toggle failed: {e.Message}");
			}

			_noclip = value;
		}

		// The avatar mesh and the player's collision capsule/camera are DIFFERENT transforms —
		// rotating VRC.Player's transform only spun the avatar. Apply the rotation to every
		// relevant transform (avatar root, the VRCPlayer root, and the CharacterController's
		// object) so the actual player — capsule and view — turns, not just the mesh.
		private void ApplyRotationToPlayer(Component player, Quaternion rot)
		{
			try { player.transform.rotation = rot; } catch { }

			if (_vrcPlayerT == null) _vrcPlayerT = ResolveVrcPlayerTransform(player);
			try { if (_vrcPlayerT != null) _vrcPlayerT.rotation = rot; } catch { _vrcPlayerT = null; }

			if (_capsuleT == null) { var cc = FindLocalCharacterController(); if (cc != null) _capsuleT = cc.transform; }
			try { if (_capsuleT != null) _capsuleT.rotation = rot; } catch { _capsuleT = null; }

			if (!_loggedHierarchy) { _loggedHierarchy = true; LogHierarchy(player); }
		}

		// VRC.Player._vrcplayer → the VRCPlayer MonoBehaviour, whose transform is the player root.
		private static Transform ResolveVrcPlayerTransform(Component player)
		{
			try
			{
				var t = player.GetType();
				var vp = (t.GetProperty("_vrcplayer") ?? t.GetProperty("prop_VRCPlayer_0") ?? t.GetProperty("prop_VRCPlayer_1"))?.GetValue(player);
				if (vp is Component c) return c.transform;
			}
			catch { }
			return null;
		}

		private static CharacterController FindLocalCharacterController()
		{
			try
			{
				var root = GetVrcPlayerLocalTransform();
				if (root == null) return null;
				return root.GetComponentInChildren<CharacterController>(true);
			}
			catch { return null; }
		}

		// One-time diagnostic so we can see the real player rig if rotation still looks wrong.
		private static void LogHierarchy(Component player)
		{
			try
			{
				var root = GetVrcPlayerLocalTransform();
				if (root == null) return;
				var sb = new System.Text.StringBuilder("[Movement] local player rig from root:\n");
				Walk(root, 0, sb);
				VRChatArchiveModPlugin.Logger.LogInfo(sb.ToString());
			}
			catch { }
		}

		private static void Walk(Transform t, int depth, System.Text.StringBuilder sb)
		{
			if (t == null || depth > 6) return;
			try
			{
				string flags = "";
				if (t.GetComponent<CharacterController>() != null) flags += " [CC]";
				if (t.GetComponent<Camera>() != null) flags += " [CAM]";
				sb.Append(' ', depth * 2).Append(t.name).Append(flags).Append('\n');
				for (int i = 0; i < t.childCount && i < 12; i++) Walk(t.GetChild(i), depth + 1, sb);
			}
			catch { }
		}

		private static void DisableAll(List<Collider> cols)
		{
			for (int i = 0; i < cols.Count; i++)
			{
				try
				{
					var c = cols[i];
					if (c != null)
					{
						if (c is CharacterController cc)
						{
							try { cc.detectCollisions = false; } catch { }
						}
						c.enabled = false;
					}
				}
				catch { }
			}
		}

		// Every solid (non-trigger) collider on the local player's rig, scanned from the
		// top-most parent so nothing on the locomotion hierarchy is missed.
		private static void CollectPlayerColliders(List<Collider> into)
		{
			try
			{
				var root = GetVrcPlayerLocalTransform();
				if (root == null) return;

				var cols = root.GetComponentsInChildren<Collider>(true);
				if (cols == null) return;
				for (int i = 0; i < cols.Length; i++)
				{
					var c = cols[i];
					if (c == null) continue;
					try { if (c.isTrigger) continue; } catch { }   // keep trigger zones intact
					into.Add(c);
				}
			}
			catch { }
		}
	}
}
