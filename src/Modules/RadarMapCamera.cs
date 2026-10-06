using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// RADAR MAP — an optional top-down view of the world, drawn underneath the radar blips.
	//
	// A second Camera is parked above the local player looking straight down and rendered into a
	// RenderTexture. Three things keep that from costing what a second camera usually costs:
	//
	//   * it is DISABLED and rendered by hand (camera.Render()) on our own cadence, so Unity never
	//     draws it as part of the normal frame
	//   * it renders at a small square resolution, because the result is shown inside a radar a few
	//     hundred pixels wide
	//   * its culling mask keeps world geometry and drops players, UI and mirrors — avatars are
	//     already drawn as blips, and a mirror in view would render the world twice
	//
	// OFF by default. It is the one feature here that genuinely costs frames, so it is the user's
	// choice to spend them, and the menu says so.
	public static class RadarMapCamera
	{
		private static Camera _cam;
		private static RenderTexture _rt;
		private static GameObject _go;
		private static int _frame;
		private static int _res;
		private static bool _failed;

		public static bool Ready => _cam != null && _rt != null;
		public static Texture Texture => _rt;

		// Where and how the cached picture was taken, so RadarModule can slide and turn it to the
		// player's CURRENT position and heading on every frame in between (the render itself stays
		// at 10 Hz; that is what kept the radar cheap, and what made it step instead of flow).
		public const float Overscan = 1.15f;   // margin for the slide between two renders (24/s): a few metres at most
		public static Vector3 RenderPos;
		public static float RenderYaw;
		public static bool HasRender;

		// Everything except players, UI and mirrors. Layer numbers are VRChat's own and stable
		// across builds; naming them here beats a magic number.
		private const int LayerDefault = 0, LayerWater = 4, LayerUI = 5, LayerInteractive = 8;
		private const int LayerPlayer = 9, LayerPlayerLocal = 10, LayerEnvironment = 11;
		private const int LayerUiMenu = 12, LayerPickup = 13, LayerPickupNoEnv = 14;
		private const int LayerWalkthrough = 17, LayerMirrorReflection = 18;

		private static int Mask()
		{
			int m = 0;
			foreach (int l in new[] { LayerDefault, LayerWater, LayerInteractive, LayerEnvironment,
									  LayerPickup, LayerPickupNoEnv, LayerWalkthrough })
				m |= 1 << l;
			// Explicitly OFF, so a future edit to the include list cannot let them back in.
			m &= ~(1 << LayerUI);
			m &= ~(1 << LayerUiMenu);
			m &= ~(1 << LayerPlayer);
			m &= ~(1 << LayerPlayerLocal);
			m &= ~(1 << LayerMirrorReflection);
			return m;
		}

		// Called from the radar's OnGui. Returns false when there is nothing to draw.
		public static bool Tick(float rangeMeters, Camera playerCam)
		{
			try
			{
				// OFF RE-ARMS THE FEATURE (2026-09-13). _failed latches on the first render or create
				// error and nothing ever cleared it, so a single bad frame disabled the map for the
				// rest of the session and switching it off and back on could not recover it — a dead
				// switch. The OFF state is the natural place to clear the latch: the next ON gets a
				// real retry, and a genuinely broken camera simply latches again on its first frame.
				if (!ModConfig.RadarMap.Value) { _failed = false; Release(); return false; }
				if (_failed) { Release(); return false; }
				if (playerCam == null) return false;

				var local = PlayerRef.LocalTransform();
				if (local == null) return false;

				int want = Mathf.Clamp(ModConfig.RadarMapResolution.Value, 96, 1024);
				if (_rt != null && _res != want) Release();
				if (!Ensure(want)) return false;

				// Follow the player, and share the player camera's HEADING so the map turns with the
				// blips. The radar is camera-relative: a map that stayed world-aligned would have
				// every dot sliding across a picture that never moved with them.
				float yaw = 0f;
				try { yaw = playerCam.transform.eulerAngles.y; } catch { }
				float height = Mathf.Max(10f, ModConfig.RadarMapHeight.Value);
				Vector3 pos = local.position;
				float size = Mathf.Max(2f, rangeMeters) * Overscan;
				float far = height + Mathf.Max(50f, rangeMeters) * Overscan;

				// Rendered on OUR cadence, not the frame's.
				int every = Mathf.Clamp(ModConfig.RadarMapEveryFrames.Value, 1, 30);
				_frame++;
				bool due = _frame >= every;
				// SAME POSE = SAME PICTURE. A camera that has neither moved (5 cm) nor turned (0.3 deg)
				// since the last render would draw the identical image, so that render is skipped:
				// standing in a crowd -- exactly where fps is lowest -- costs a few renders a second
				// instead of one per frame, and the first real step or turn renders on that very frame,
				// as before. The 15-frame ceiling keeps animated scenery (water, platforms) ticking
				// while you stand still and bounds how long a previous world's picture can sit under
				// the blips. The size/far test covers two things at once: a range or height change
				// re-renders at once (the blit scales by the CURRENT range), and a camera that Ensure()
				// just rebuilt (map toggled on, resolution changed) still carries Unity's defaults
				// (5 / 1000) rather than these values, so a fresh, never-rendered texture is drawn on
				// its first tick instead of showing uncleared VRAM -- HasRender alone cannot tell,
				// since Release() leaves it set.
				if (due && HasRender && _frame < 15 && VaClock.Now > 2f
					&& (pos - RenderPos).sqrMagnitude < 0.0025f
					&& Mathf.Abs(Mathf.DeltaAngle(yaw, RenderYaw)) < 0.3f
					&& Mathf.Abs(_cam.orthographicSize - size) < 0.01f
					&& Mathf.Abs(_cam.farClipPlane - far) < 0.01f)
					due = false;
				// COST-BASED BRAKE, not a frame counter.
				//
				// This render is a COMPLETE second pass of the world, and its price is set by the WORLD,
				// not by anything we do. Measured in one session, same 35-player roster, across a world
				// change and nothing else:
				//     light world: Radar =  30.2 ms/s at 27 fps  ->  1.1 ms per frame
				//     heavy world: Radar = 469.2 ms/s at  9 fps  ->   52 ms per frame
				// A 45x swing at identical workload. So a fixed cadence is the wrong instrument: any
				// value cheap enough for the heavy world throws away the light world, which is exactly
				// why the old fixed throttle was removed ("100% fluide, nvm the cost", 2026-09-04).
				//
				// Instead the map is given a BUDGET — a share of wall-clock time — and it renders as
				// often as that budget allows. At 1 ms a render it may go again after ~12 ms, i.e. every
				// frame, which is what the owner asked for and what the light world gets. At 52 ms it
				// waits ~650 ms, so a world that is already at 9 fps stops paying half its frame for a
				// minimap. Nothing disappears in between: OnGui keeps blitting the cached texture, and
				// the blit pans and rotates it to follow the player.
				if (due && VaClock.Now < _nextAllowed) due = false;

				if (due)
				{
					_frame = 0;
					_go.transform.position = pos + Vector3.up * height;
					_go.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
					// OVERSCAN: the picture covers more than the radar shows, so the blit can pan AND rotate
					// the cached frame to follow the player between two renders without exposing its corners.
					_cam.orthographicSize = size;
					_cam.farClipPlane = far;
					RenderPos = pos; RenderYaw = yaw; HasRender = true;
					long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
					try { _cam.Render(); }
					catch (Exception e)
					{
						// One failure is enough: a camera that throws will throw every frame, and a
						// radar is not worth an exception per repaint.
						_failed = true;
						VRChatArchiveModPlugin.Logger.LogWarning($"[RadarMap] render failed, map disabled: {e.Message}");
						Release();
						return false;
					}

					// What it actually cost, smoothed so one hitching frame does not park the map for a
					// second. The budget is a fraction of wall time: interval = cost / budget, so a
					// 1 ms render may repeat after 12 ms and a 52 ms one waits ~650 ms.
					float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
					LastRenderMs = LastRenderMs <= 0f ? ms : LastRenderMs * 0.7f + ms * 0.3f;
					_nextAllowed = VaClock.Now + Mathf.Clamp(LastRenderMs / 1000f / Budget, 0f, 1f);
				}
				return true;
			}
			catch { return false; }
		}

		/// <summary>Share of wall-clock time the map render may consume. 0.08 = at most ~80 ms per
		/// second, whatever the world costs.</summary>
		private const float Budget = 0.08f;

		/// <summary>Smoothed cost of one map render, in milliseconds. Shown in the diagnostics panel:
		/// a big number here means the WORLD is expensive, not the radar's blips.</summary>
		public static float LastRenderMs;

		private static float _nextAllowed;

		private static bool Ensure(int res)
		{
			if (_cam != null && _rt != null) return true;
			try
			{
				_res = res;
				_rt = new RenderTexture(res, res, 16, RenderTextureFormat.Default)
				{
					name = "ArchiveRadarMapRT",
					hideFlags = HideFlags.HideAndDontSave,
					antiAliasing = 1,
					filterMode = FilterMode.Bilinear,
					useMipMap = false,
				};
				_rt.Create();

				_go = new GameObject("ArchiveRadarMapCam");
				_go.hideFlags = HideFlags.HideAndDontSave;
				UnityEngine.Object.DontDestroyOnLoad(_go);

				_cam = _go.AddComponent<Camera>();
				_cam.orthographic = true;
				_cam.clearFlags = CameraClearFlags.SolidColor;
				_cam.backgroundColor = new Color(0.03f, 0.03f, 0.05f, 1f);
				_cam.cullingMask = Mask();
				_cam.targetTexture = _rt;      // never draws to the screen
				_cam.depth = -100;             // and never competes with the game's cameras
				_cam.allowHDR = false;
				_cam.allowMSAA = false;
				_cam.useOcclusionCulling = false;
				_cam.nearClipPlane = 0.3f;
				_cam.enabled = false;          // we call Render() ourselves

				VRChatArchiveModPlugin.Logger.LogInfo($"[RadarMap] camera ready ({res}x{res}).");
				return true;
			}
			catch (Exception e)
			{
				_failed = true;
				VRChatArchiveModPlugin.Logger.LogWarning($"[RadarMap] could not create the camera: {e.Message}");
				Release();
				return false;
			}
		}

		public static void Release()
		{
			try { if (_cam != null) _cam.targetTexture = null; } catch { }
			try { if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); } } catch { }
			try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
			_cam = null; _rt = null; _go = null; _res = 0;
		}
	}
}
