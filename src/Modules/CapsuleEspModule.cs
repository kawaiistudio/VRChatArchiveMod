using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// CAPSULE ESP — a real 3D capsule around each player, lit by VRChat's own glow.
	//
	// Not drawn. Every screen-space attempt at this fails for the same reason: a shape computed from
	// projected points is a shape on your screen, so it does not turn with the player, does not sit
	// in the depth buffer, and turns into a circle the moment the maths degenerates.
	//
	// The way this is actually done — read out of TevvezClientQM.dll, which does it correctly:
	//
	//   1. Every VRChat player carries a "SelectRegion" child — the capsule the game itself uses to
	//      decide you clicked on someone. It is already the right shape, the right size and in the
	//      right place, and it follows the avatar. Nothing has to be measured or guessed.
	//   2. Clone its MESH into a GameObject of our own (falling back to Unity's primitive capsule if
	//      the region has no MeshFilter), with a fully transparent material, so nothing is added to
	//      the picture on its own.
	//   3. Hand that renderer to HighlightsFX — the same post-effect VRChat uses for "Hold to Grab".
	//      The effect draws the OUTLINE of the geometry it is given, which is where the neon comes
	//      from. An invisible mesh plus an outline effect equals a glowing capsule.
	//
	// So the glow is real geometry: it turns, scales and occludes like the avatar, because it IS in
	// the scene rather than painted over it.
	public class CapsuleEspModule : IModule
	{
		public override string Name => "CapsuleEsp";

		private const string CapsuleName = "VA_CapsuleESP";

		// EVERY CAPSULE WE EVER BUILT, so OFF can destroy the ones _caps no longer knows about.
		//
		// _caps is keyed by player and is rebuilt as people come and go; a capsule can leave that
		// dictionary while its GameObject is still in the scene (a rescan that re-keys, a scene load
		// mid-build, an exception between creating the object and recording it). ClearAll() only ever
		// walked _caps, so such a capsule was never destroyed and kept glowing with the switch OFF —
		// the "the ESP toggle does nothing" report. This list is the authority for teardown: it holds
		// a reference to everything we made, so nothing can be orphaned out of reach.
		private static readonly List<GameObject> _made = new List<GameObject>();

		private static void Remember(GameObject go)
		{
			if (go == null) return;
			try { _made.Add(go); } catch { }
		}

		/// <summary>Destroys every capsule this module ever created, tracked or not, and forgets them.
		/// Safe to call repeatedly: entries already gone are simply skipped.</summary>
		private static void DestroyAllMade()
		{
			for (int i = 0; i < _made.Count; i++)
			{
				var go = _made[i];
				try { if (go != null && Core.NativeGuard.Alive(go)) UnityEngine.Object.Destroy(go); } catch { }
			}
			_made.Clear();
		}
		private const int RescanFrames = 60;

		private sealed class Cap
		{
			public bool Rainbow;
			public bool Improvised;   // built without SelectRegion; upgrade when it appears
			public Transform Region;      // the player's own SelectRegion
			public GameObject Go;         // our clone
			public MeshRenderer Rend;
			public Color Col;
			public object Fx;             // the HighlightsFX instance the renderer was lit on (null = never lit)
		}

		private readonly Dictionary<int, Cap> _caps = new Dictionary<int, Cap>();
		private int _frame;
		private bool _wasOn;
		private bool? _lastThrough;   // ThroughWalls as last applied to the live capsules

		public override void OnUpdate()
		{
			try
			{
				// SELF-GATED: this switch and only this switch. OFF MEANS OFF, THIS FRAME — every
				// capsule is un-lit and destroyed the moment the switch flips, and again on any later
				// frame that still finds one (a removal that failed once is retried, not forgotten).
				bool on = false;
				try { on = ModConfig.EspCapsule.Value; } catch { }
				if (!on)
				{
					// _made, not just _caps: an orphaned capsule leaves _caps empty while the object is
					// still in the scene, and keying the cleanup off _caps alone meant OFF skipped it.
					if (_caps.Count > 0 || _made.Count > 0) ClearAll();
					// AND HAND THE CAMERAS BACK. EspCameraGuard strips our layer from every other
					// camera and forces it onto Camera.main; with the capsules gone nothing was
					// undoing that, because only HighlightEsp's tick reaches the guard and it may be
					// off too. Restore only when NO ESP consumer still needs the guard, or turning
					// capsules off would un-hide the glow ESP from the photo camera.
					if (_wasOn)
					{
						bool otherEspWantsGuard = false;
						try
						{
							otherEspWantsGuard = ModConfig.EspHighlight.Value
								|| ModConfig.EspPortals.Value
								|| ModConfig.EspItems.Value;
						}
						catch { }
						if (!otherEspWantsGuard) EspCameraGuard.Restore();
					}
					_wasOn = false;
					FeatureHealth.Idle("ESP/Capsule", "off");
					return;
				}
				// AN ENABLE IS INSTANT TOO: the first rescan runs on the frame the switch goes on
				// rather than up to a second later.
				if (!_wasOn) { _wasOn = true; _frame = RescanFrames; }

				// THROUGH-WALLS IS LIVE. It used to be baked into the material at build time, so
				// flipping it did nothing to the capsules already on screen until they were rebuilt.
				bool through = true;
				try { through = ModConfig.EspThroughWalls.Value; } catch { }
				if (_lastThrough != through)
				{
					_lastThrough = through;
					foreach (var c in _caps.Values)
					{
						try { if (c?.Rend != null && Core.NativeGuard.Alive(c.Rend)) ApplyDepth(c.Rend.material, through); } catch { }
					}
				}

				// Keep the ESP out of your own camera / stream / mirrors (rate-limited inside).
				EspCameraGuard.Tick();

				// Position every frame — the capsule has to sit on the player, not near them.
				foreach (var c in _caps.Values) Track(c);

				// A GRADIENT HAS TO MOVE — AND ADDING A LIT RENDERER AGAIN DOES NOT MOVE IT. HighlightsFX
				// takes a renderer's colour on its FIRST add and ignores every later add of the same
				// renderer, so this loop used to call add() 60 times a second per rainbow capsule (the
				// HighlightEsp=100 ms/s in the Perf log) and the glow never changed colour: a Legendary's
				// box cycled while the capsule sat on one colour. The only thing that changes a glow's
				// colour is un-light then re-light (Repaint), at ~1 ms the pair — so it runs every ten
				// frames, ~6 Hz, which a glow reads as a sweep. Only rainbow capsules pay for this.
				// CADENCED IN TIME, NOT IN FRAMES — that is what made it stutter next to the box.
				//
				// Ten frames is ~6 Hz at 60 fps, and a sweep at 6 Hz reads as steps. Worse, it is not even a
				// fixed rate: at 30 fps it drops to 3 Hz, at 200 fps it climbs to 20 -- so the smoothness
				// changed with the framerate. The box looks perfect because it is redrawn every frame with a
				// freshly computed colour; a glow cannot be, since only un-light + re-light changes it and
				// that pair costs about a millisecond.
				//
				// 20 Hz is the compromise: three times smoother than before, identical at any framerate, and
				// still only ~10 ms/s per rainbow capsule. Only rainbow capsules pay it at all.
				float nowT; try { nowT = VaClock.Now; } catch { nowT = 0f; }
				if (nowT - _rainbowAt >= RainbowInterval)
				{
					_rainbowAt = nowT;
					Color now = TrustKit.Spectrum();
					foreach (var c in _caps.Values)
					{
						if (c == null || !c.Rainbow || c.Rend == null) continue;
						Repaint(c, now);
					}
				}

				if (++_frame < RescanFrames) return;
				_frame = 0;
				Rescan();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[CapsuleEsp] " + e.Message); }
		}

		public override void OnSceneLoaded(int buildIndex) { ClearAll(); EspCameraGuard.Reset(); }
		public override void OnShutdown() { ClearAll(); EspCameraGuard.Restore(); }

		private static bool _reported;

		private void Rescan()
		{
			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return;

			var seen = new HashSet<int>();
			float maxDist = ModConfig.EspMaxDistance.Value;
			Vector3 me = Vector3.zero;
			try { var t = PlayerRef.LocalTransform(); if (t != null) me = t.position; } catch { }

			for (int i = 0; i < players.Count; i++)
			{
				try
				{
					var api = players[i];
					// A NULL CHECK IS NOT A LIVENESS CHECK. VRCPlayerApi is not a UnityEngine.Object, so
					// `== null` is the plain managed test and says nothing about the il2cpp object behind
					// the handle -- and reading isLocal off a stale one is an access violation inside the
					// proxy, which no try/catch around it can stop.
					if (api == null || !NativeGuard.Alive(api)) continue;
					// Never yourself: your own capsule would sit in the middle of your view and tell
					// you nothing. A rainbow id is about how OTHER people's clients draw YOU.
					if (api.isLocal) continue;
					// 0 (or less) = UNLIMITED distance, same rule as every other ESP type.
					if (maxDist > 0f && Vector3.Distance(me, api.GetPosition()) > maxDist) continue;

					int id = api.playerId;
					seen.Add(id);

					// An improvised capsule is replaced as soon as VRChat's own region exists, so a
					// player who joined mid-load ends up with the exact shape like everyone else.
					if (_caps.TryGetValue(id, out var existing) && existing != null && existing.Improvised)
					{
						try
						{
							var root2 = api.gameObject != null ? api.gameObject.transform : null;
							if (root2 != null && root2.Find("SelectRegion") != null) { Remove(id); }
						}
						catch { }
					}

					if (!_caps.TryGetValue(id, out var cap) || cap == null || cap.Go == null)
					{
						cap = Build(api);
						if (cap == null) continue;
						_caps[id] = cap;
					}

					// Trust colour, re-applied on each rescan: a rank can resolve after the player
					// has already spawned, and a capsule stuck on the Visitor grey is misleading.
					// A rainbow capsule is re-lit every pass rather than only when the colour
					// CHANGES — the whole point is that it is always changing.
					if (TrustKit.IsRainbowFor(api) || TrustKit.IsRainbow(Core.ApiUsers.Get(api)))
					{
						// The throttled loop in OnUpdate paints rainbow capsules; here only the state
						// flips, plus one immediate repaint on the flip so it does not sit on the old
						// solid colour for up to ten frames.
						if (!cap.Rainbow) { cap.Rainbow = true; Repaint(cap, TrustKit.Spectrum()); }
					}
					else
					{
						// "I DO NOT KNOW" IS NOT "NO", AND TREATING IT AS NO IS THE WHOLE BUG.
						//
						// The owner described it exactly: the rainbow worked for a while, then the trust colour came
						// back over it. ApiUsers.Get() answers from a one-second cache and returns NULL whenever the
						// lookup misses -- which on 1903 is most of the time, since VRC.Player's members cannot be
						// placed. A null made IsRainbow false, which dropped into this branch, which REPAINTED the
						// capsule with the plain trust colour. The box never showed it because the ESP caches the
						// rainbow verdict; the capsule re-decided every pass and flickered.
						//
						// So a pass that learned nothing now changes nothing: the capsule keeps whatever it had.
						var who = Core.ApiUsers.Get(api);
						if (who == null && string.IsNullOrEmpty(TrustKit.UidOf(api)))
						{
							if (cap.Fx == null) Light(cap, true);   // still worth retrying a capsule that never lit
						}
						else
						{
						bool wasRainbow = cap.Rainbow;
						cap.Rainbow = false;
						Color col = TrustKit.ColorOf(who);
						// A changed colour has to be REPAINTED, not re-added: add() on a lit renderer is
						// ignored, which is why a capsule built on the Visitor grey never took its rank
						// colour once the rank resolved.
						if (wasRainbow || col != cap.Col) Repaint(cap, col);
						// NEVER LIT IS NOT THE SAME AS LIT. A capsule built while the effect was still
						// absent (world still loading) had no glow and, its colour never changing
						// again, never got one. Retry on every rescan until it takes.
						else if (cap.Fx == null) Light(cap, true);
						}
					}
				}
				catch { }
			}

			// Anyone who left or went out of range: take the glow off and destroy our object.
			if (_caps.Count > 0)
			{
				var gone = new List<int>();
				foreach (var kv in _caps) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
				foreach (int id in gone) Remove(id);
			}

			// THE COUNT IS THE PROOF, and "the effect is missing" is a different report from "nobody
			// is in range". HighlightEspModule owns the effect; ask it.
			if (!HighlightEspModule.EffectReady())
				FeatureHealth.Broken("ESP/Capsule", "on, but VRChat's highlight effect is not available yet — capsules are built but will not glow");
			else
				FeatureHealth.Ok("ESP/Capsule", _caps.Count == 0
					? "on — no other players in range"
					: "on — " + _caps.Count + " player capsule(s)");

			// One-time headline: players in range vs capsules held. "seen 6, capsules 0" points at
			// Build; "seen 0" at the roster; "capsules 6" with a blank screen at the glow, which
			// HighlightEsp now reports on separately.
			if (!_reported && seen.Count > 0)
			{
				_reported = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[CapsuleEsp] seen " + seen.Count + " player(s) in range, " + _caps.Count + " capsule(s) built.");
			}
		}

		private Cap Build(VRC.SDKBase.VRCPlayerApi api)
		{
			try
			{
				Transform root = api.gameObject != null ? api.gameObject.transform : null;
				if (root == null) return null;

				// SelectRegion is VRChat's own player capsule — already the right shape and size.
				Transform region = root.Find("SelectRegion");
				if (region == null)
				{
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (t == null) continue;
						if (!string.Equals(t.name, "SelectRegion", StringComparison.OrdinalIgnoreCase)) continue;
						region = t; break;
					}
				}
				// NO SelectRegion, STILL A CAPSULE. Returning null here is why the capsule appeared
				// on some players and not others: that object is VRChat's own click target and it is
				// not always there — not before the avatar finishes loading, and not on every rig.
				// Giving up meant those players silently had no ESP at all, which is worse than a
				// capsule of approximate size.
				//
				// The fallback hangs off the player root instead, at a body's height and width. It
				// is replaced by the real thing on a later rescan, because a capsule built this way
				// is rebuilt whenever SelectRegion turns up.
				bool improvised = region == null;
				if (improvised) region = root;

				// Parented to SelectRegion ITSELF with an identity local transform, so our capsule is
				// that region exactly — same place, same rotation, same size, for free and forever.
				//
				// The first version parented to the player root and copied SelectRegion.lossyScale
				// into localScale. lossyScale is already the WORLD scale, so any scale on the root
				// multiplied it a second time and the capsule swallowed the screen.
				var go = new GameObject(CapsuleName);
				Remember(go);                         // teardown authority: OFF destroys this even if _caps loses it
				go.layer = EspCameraGuard.EspLayer;   // rendered by your view only (see EspCameraGuard)
				go.transform.SetParent(region, false);
				go.transform.localPosition = Vector3.zero;
				go.transform.localRotation = Quaternion.identity;
				go.transform.localScale = Vector3.one;

				var mf = go.AddComponent<MeshFilter>();
				var src = region.GetComponent<MeshFilter>();
				if (src != null && src.sharedMesh != null)
				{
					mf.sharedMesh = src.sharedMesh;
				}
				else
				{
					// SelectRegion is often a bare CapsuleCollider with no mesh at all. Unity's
					// primitive capsule is a FIXED radius 0.5 / height 2 — dropping that in at
					// identity scale gives a capsule that has nothing to do with the player it is
					// supposed to be around. So when there is no mesh, take the collider's own
					// radius, height and axis and scale the primitive to match it exactly.
					var prim = GameObject.CreatePrimitive(PrimitiveType.Capsule);
					mf.sharedMesh = prim.GetComponent<MeshFilter>().sharedMesh;
					UnityEngine.Object.Destroy(prim);

					var col = improvised ? null : region.GetComponent<CapsuleCollider>();
					if (col != null)
					{
						// Primitive: radius 0.5, total height 2, along Y.
						float r = Mathf.Max(0.01f, col.radius);
						float h = Mathf.Max(2f * r, col.height);

						// NOT CLAMPED. A capsule that reaches the ceiling looks like a bug and is
						// not one — some avatars really are that tall, and the capsule is meant to be
						// the player's actual size. Capping it "sensibly" would shrink a legitimate
						// avatar to hide a problem that lives somewhere else entirely: what reads as
						// wrong in a crowd is the colours blending, not the height.
						var s = new Vector3(r / 0.5f, h / 2f, r / 0.5f);
						// direction: 0 = X, 1 = Y, 2 = Z. Rotate the primitive if the collider is
						// not upright, rather than producing a capsule lying across the player.
						if (col.direction == 0) { go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); }
						else if (col.direction == 2) { go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); }
						go.transform.localScale = s;
						go.transform.localPosition = col.center;
					}
					else
					{
						// Neither a mesh nor a capsule collider: fall back to a body-sized capsule
						// rather than Unity's 2m default, which reads as a giant bubble.
						go.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
						// On the player root the origin is at the FEET, so an unshifted capsule sits
						// half-buried in the floor.
						if (improvised) go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
					}
				}

				var rend = go.AddComponent<MeshRenderer>();
				// Fully transparent: we contribute NO pixels of our own. Everything you see is the
				// outline the highlight effect draws around this geometry.
				var mat = new Material(Shader.Find("GUI/Text Shader")) { color = new Color(0f, 0f, 0f, 0f) };

				// DRAWN THROUGH WALLS. An ESP that a wall can hide is an ESP that fails exactly when
				// it is wanted: the whole point is the player you cannot see. The depth test is
				// switched off so this geometry is never rejected for being behind something, and it
				// is pushed past the opaque queue so it is submitted after the world is drawn.
				try
				{
					// RENDER LAST, AND STACK PROPERLY.
					//
					// Two separate things were being confused. Ignoring the world's depth is what
					// lets a capsule show through a wall — that part was right. But ZWrite was off,
					// so the capsules did not write depth EITHER, and every one of them composited
					// on top of every other: five players in a line gave one smear of mixed colour
					// instead of five capsules.
					//
					// Writing depth while still ignoring the world's fixes exactly that. At queue
					// 9999 Unity sorts these back-to-front like transparent geometry, so the nearer
					// capsule draws last and COVERS the farther one instead of blending with it —
					// they superimpose, each keeping its own colour, and all of them still show
					// through the level.
					bool through = true;
					try { through = ModConfig.EspThroughWalls.Value; } catch { }

					ApplyDepth(mat, through);
				}
				catch { }

				rend.material = mat;
				rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				rend.receiveShadows = false;

				var cap = new Cap { Region = region, Go = go, Rend = rend, Col = Color.white, Improvised = improvised };
				Track(cap);
				Light(cap, true);
				return cap;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[CapsuleEsp] build: " + e.Message);
				return null;
			}
		}

		// Nothing to do per frame any more: being a child of SelectRegion with an identity local
		// transform means it follows on its own. Kept only to put the identity back if something in
		// the world ever moves our object.
		private static void Track(Cap c)
		{
			try
			{
				if (c?.Region == null || c.Go == null) return;
				// Nothing to re-assert when the mesh came from SelectRegion itself: identity local
				// TRS on a child of the region is already exact. The collider-derived case owns its
				// own scale and offset, so leave those alone too.
				if (c.Go.transform.parent != c.Region) c.Go.transform.SetParent(c.Region, false);
			}
			catch { }
		}

		// Depth behaviour of a capsule material: drawn over the world (walls never hide it — and
		// capsules no longer hide each other either) or in the scene like ordinary geometry.
		// ZWrite stays on either way: it is what stops a crowd's capsules smearing into one colour.
		private static void ApplyDepth(Material mat, bool through)
		{
			try
			{
				if (mat == null) return;
				mat.SetInt("_ZTest", (int)(through
					? UnityEngine.Rendering.CompareFunction.Always
					: UnityEngine.Rendering.CompareFunction.LessEqual));
				mat.SetInt("_ZWrite", 1);
				mat.renderQueue = through ? 9999 : 2500;   // last of everything, or with the world
			}
			catch { }
		}

		// HighlightsFX is reached through HighlightEspModule, which already resolves it by
		// reflection and copes with the builds where it is missing. The instance a renderer was lit
		// on is remembered, so the un-light goes to the effect that actually holds it.
		// Rainbow capsules are repainted every this many frames (~6 Hz at 60 fps). Each repaint is an
		// un-light plus a light, about a millisecond together; per frame that would be 60 ms/s per
		// capsule, which is the budget this whole module used to burn for no visible change.
		private const float RainbowInterval = 0.05f;   // 20 Hz, framerate-independent
		private float _rainbowAt;

		// THE ONLY WAY TO CHANGE A GLOW'S COLOUR. HighlightsFX honours add(renderer, colour, true)
		// once per renderer and ignores it afterwards; add(renderer, _, false) removes it. So a new
		// colour is: remove, then add with the new colour.
		private static void Repaint(Cap c, Color col)
		{
			if (c?.Rend == null) return;
			Light(c, false);
			c.Col = col;
			Light(c, true);
		}

		private static void Light(Cap c, bool on)
		{
			try
			{
				if (c?.Rend == null) return;
				if (on)
				{
					if (HighlightEspModule.Highlight(c.Rend, c.Col, out object fx) && fx != null) c.Fx = fx;
				}
				else HighlightEspModule.Unhighlight(c.Rend, c.Fx);
			}
			catch { }
		}

		private void Remove(int id)
		{
			if (!_caps.TryGetValue(id, out var c)) return;
			_caps.Remove(id);
			try { Light(c, false); } catch { }
			// The GameObject goes whether or not the effect was still there to un-light it: a
			// destroyed renderer cannot glow, so this alone makes the capsule vanish.
			try { if (Core.NativeGuard.Alive(c?.Go) && c.Go != null) UnityEngine.Object.Destroy(c.Go); } catch { }
		}

		// Everything, each on its own guard, and the table is emptied even if a removal threw.
		// The menus call this after changing an ESP setting. This module already notices a setting
		// change by itself, so all this does is drop what is drawn and make the next frame rescan
		// rather than waiting out the interval.
		public static void TriggerRelight()
		{
			try
			{
				var mod = ModuleManager.Get<CapsuleEspModule>();
				if (mod == null) return;
				mod.ClearAll();
				mod._frame = RescanFrames;
			}
			catch { }
		}

		private void ClearAll()
		{
			var ids = new List<int>(_caps.Keys);
			foreach (int id in ids) { try { Remove(id); } catch { } }
			_caps.Clear();
			// THEN the ones _caps no longer knows about. Without this an orphaned capsule survived
			// every OFF and glowed for the rest of the session.
			DestroyAllMade();
		}
	}

	// ESP OUT OF YOUR OWN CAMERA. Two things can leak the ESP into a photo, a stream or a mirror: the
	// capsules (real objects, any camera renders them) and the glow (HighlightsFX is a per-camera post
	// effect). So: the capsules live on a layer only Camera.main renders — the bit is forced into the
	// main camera's culling mask and stripped from every other camera's — and any HighlightsFX that sits
	// on a camera other than the one the glow ESP uses is switched off (put back when the option or the
	// mod goes off). The 2D screen ESP is IMGUI: it never enters any camera, nothing to do there.
	internal static class EspCameraGuard
	{
		// Layer 7 is one of Unity's unnamed built-in layers: no world uses it, every camera mask has a
		// bit for it, and nothing else in VRChat is on it.
		public const int EspLayer = 7;
		private const int Bit = 1 << EspLayer;
		private const int TickEveryFrames = 10;   // cameras appear on a human timescale
		private const float RescanSec = 3f;       // a same-count swap (one mirror for another) is caught here
		private static int _frame;
		private static int _lastCount = -1;
		private static float _nextRescan;
		private static Il2CppSystem.Type _fxIl2;
		// The enabled cameras other than the main one, as of the last rescan. Per tick only their
		// culling mask is read — one icall each — instead of allocating Camera.allCameras, wrapping
		// every entry, VirtualQuery-ing it and GetComponent-ing it: that was 8-26 ms every 10 frames in
		// the 2026-09-04 log, i.e. a stutter the whole time any glow was on.
		private static readonly List<Camera> _cams = new List<Camera>();
		private static readonly List<Behaviour> _fxOff = new List<Behaviour>();

		internal static void Tick()
		{
			try
			{
				bool on = true;
				try { on = ModConfig.EspHideFromCamera.Value; } catch { }
				if (!on) { if (_fxOff.Count > 0) Restore(); return; }
				if (++_frame < TickEveryFrames) return;
				_frame = 0;

				var main = Camera.main;
				int mainId = 0;
				if (main != null)
				{
					try { mainId = main.GetInstanceID(); if ((main.cullingMask & Bit) == 0) main.cullingMask |= Bit; } catch { }
				}

				int count = -1;
				try { count = Camera.allCamerasCount; } catch { }
				float now = VaClock.Now;
				if (count != _lastCount || now >= _nextRescan)
				{
					_lastCount = count;
					_nextRescan = now + RescanSec;
					Rescan(mainId);
				}

				for (int i = 0; i < _cams.Count; i++)
				{
					try
					{
						var cam = _cams[i];
						if (cam == null) continue;   // destroyed since the rescan: Unity's == says so, no read is made
						if ((cam.cullingMask & Bit) != 0) cam.cullingMask &= ~Bit;
					}
					catch { }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[EspCameraGuard] " + e.Message); }
		}

		// Rebuilds the camera list and switches off any HighlightsFX copy that is not the one the glow
		// ESP draws with (a copy on the photo camera would put the glow into every picture).
		private static void Rescan(int mainId)
		{
			_cams.Clear();
			Camera[] cams = null;
			try { cams = Camera.allCameras; } catch { }
			if (cams == null) return;
			if (_fxIl2 == null) _fxIl2 = HighlightEspModule.FxIl2;
			IntPtr ours = HighlightEspModule.FxPtr;
			for (int i = 0; i < cams.Length; i++)
			{
				var cam = cams[i];
				try
				{
					if (cam == null || !Core.NativeGuard.Alive(cam)) continue;
					if (cam.GetInstanceID() == mainId) continue;
					_cams.Add(cam);
					if (_fxIl2 == null) continue;
					var comp = cam.GetComponentSafe(_fxIl2);
					if (comp == null) continue;
					if (ours != IntPtr.Zero && comp.Pointer == ours) continue;   // the instance the glow ESP itself uses
					var b = comp.TryCast<Behaviour>();
					if (b == null) { try { b = new Behaviour(comp.Pointer); } catch { b = null; } }
					if (b == null || !b.enabled) continue;   // already off (by us, or by VRChat)
					b.enabled = false;
					_fxOff.Add(b);
				}
				catch { }
			}
		}

		internal static void Restore()
		{
			for (int i = 0; i < _fxOff.Count; i++)
			{
				try { var b = _fxOff[i]; if (b != null && Core.NativeGuard.Alive(b)) b.enabled = true; } catch { }
			}
			// THE CULLING MASKS GO BACK TOO (2026-09-13). Restore() only ever re-enabled the
			// HighlightsFX copies it had switched off; the layer bit this guard strips from every
			// other camera (line ~535) and forces onto Camera.main (line ~516) was never reversed, so
			// after the option went off every mirror, photo and stream camera kept layer 7 culled for
			// the rest of the session — a permanent, invisible edit to cameras that belong to the
			// world, made by a switch the user had already turned off.
			for (int i = 0; i < _cams.Count; i++)
			{
				try
				{
					var cam = _cams[i];
					if (cam == null) continue;   // destroyed: Unity's == answers without a native read
					if ((cam.cullingMask & Bit) == 0) cam.cullingMask |= Bit;
				}
				catch { }
			}
			try
			{
				var main = Camera.main;
				if (main != null && (main.cullingMask & Bit) != 0) main.cullingMask &= ~Bit;
			}
			catch { }
			_fxOff.Clear();
			_cams.Clear();
			_lastCount = -1;
		}

		internal static void Reset()   // the cameras left with the world
		{
			_fxOff.Clear();
			_cams.Clear();
			_lastCount = -1;
			_fxIl2 = null;
		}
	}
}
