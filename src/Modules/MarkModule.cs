using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MARK — an anchor you place with your aim, then use for the world's loose objects: teleport them
	// around it, orbit them, arrange SHAPES, or play Bad Apple with the objects as pixels.
	//
	// TWO WAYS TO SHOW THE ART:
	//   local   — only you see it. Everything unlimited: every loose object (pickups and rigidbodies), the
	//             bake's full 30 fps, objects frozen kinematic and parked under the picture between uses.
	//   vrchat  — EVERYONE sees it, mod or not: the networkable pickups are owned and moved for real, and
	//             VRChat's own ObjectSync carries them. Rendered by a BUDGETED DIFF ("NetPix"), because the
	//             wire has one rule: an owned object at rest sleeps and sends nothing; one that moves is
	//             awake and streams ~10 events/s while it moves and for a settle window after. Hundreds of
	//             moves a frame therefore queue up server-side and play back at 1 fps for everyone else,
	//             while ~40 objects moving continuously (~400 ev/s) stay fluid. The cost is MOVES PER
	//             SECOND, so the renderer picks the finest grid whose mean move rate fits a budget, then
	//             each frame only fixes the cells that changed (a wrong cell's object goes to the nearest
	//             empty one: one move, two cells), paced by a credit that a once-a-second controller trims
	//             or grows from the measured send rate (NetSendModule). Fast scenes lag a little instead
	//             of flooding. Stopping is paced the same way.
	// One setting is kept: MarkArtSize (0 = auto). Object count and range are always unlimited.
	public class MarkModule : IModule
	{
		public override string Name => "Mark";

		public static bool HasMark => _marker != null;
		public static Vector3 MarkPos { get; private set; }
		/// <summary>True while the art plays OR while the network renderer is still putting objects back (the client shows STOP from it).</summary>
		public static bool ArtPlaying => _baPlaying || _restoring;
		public static string Mode { get; private set; } = "local";
		/// <summary>Which clip the object player is on: "badapple" (built in) or a local clip name.</summary>
		public static string CurrentClip { get; private set; } = "badapple";
		public static string Status = "";
		/// <summary>Telemetry of the network renderer, refreshed once a second; empty when vrchat mode is not playing.</summary>
		public static string NetInfo { get; private set; } = "";

		private static GameObject _marker;

		private sealed class Arranged
		{
			public Transform T; public GameObject Go; public Vector3 Pos; public Quaternion Rot; public Rigidbody Body; public bool WasKinematic; public bool WasGravity;
			public Vector3 Scale; public float Size;
			public RigidbodyInterpolation WasInterp; public bool InterpSet;   // vrchat mode: Interpolate, so MovePosition carries a velocity for ObjectSync
			public VRC.SDKBase.VRC_Pickup Pickup;                            // vrchat mode: to notice when somebody holds it
			public Vector3 Target; public bool HasTarget;                    // shapes in vrchat mode: where it must sit, re-sent once we own it
			public Behaviour Sync; public bool SyncWas;                      // local mode: the object's ObjectSync, switched off so nothing we do leaves this client
		}

		// LOCAL MEANS LOCAL. If you happen to own an object (the instance master owns every world object by
		// default), moving it "locally" still goes out through its ObjectSync — the owner reported that
		// others saw his LOCAL show. So in local mode the ObjectSync component of every object we arrange is
		// switched off for the duration and put back on restore: everyone else keeps it where it was.
		private static void MuteSync(Arranged a)
		{
			try
			{
				if (a == null || a.Go == null || !Core.NativeGuard.Alive(a.Go)) return;
				var comps = a.Go.GetComponents<Component>();
				if (comps == null) return;
				foreach (var c in comps)
				{
					if (c == null || !Core.NativeGuard.Alive(c)) continue;
					string n; try { n = Core.MenuCard.Il2CppNameOf(c) ?? ""; } catch { continue; }
					if (n.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) < 0) continue;
					var b = c.TryCast<Behaviour>();
					if (b == null) { try { b = new Behaviour(c.Pointer); } catch { b = null; } }
					if (b == null) continue;
					a.Sync = b; a.SyncWas = b.enabled;
					b.enabled = false;
					// ObjectSync owns the body's kinematic state while it runs; switching it off can hand the
					// body back to physics — so the freeze is re-asserted right after.
					if (a.Body != null && Core.NativeGuard.Alive(a.Body)) { a.Body.isKinematic = true; a.Body.velocity = Vector3.zero; a.Body.angularVelocity = Vector3.zero; }
					_syncMuted++;
					return;
				}
			}
			catch { }
		}
		private static int _syncMuted;
		private static readonly List<Arranged> _art = new List<Arranged>();

		// ---- Bad Apple (both modes)
		private static bool _baPlaying; private static float _baStart; private static int _baLastIdx = -1;
		private static byte[][] _baFrames; private static int _baW, _baH, _baInterval; private static bool _baSquareCells;
		private static Vector3 _baRight;
		// (the old single-clip _hd* cache is gone: clips are cached per name in _clipCache)
		private static byte[][] _txtFrames; private static int _txtW, _txtH, _txtInterval;
		private static int _baDown = 1, _baCW, _baCH;
		private static float[] _baCells; private static bool[] _want; private static int[] _cellObj, _objCell; private static bool[] _parked;
		private static readonly List<int> _free = new List<int>(), _newCells = new List<int>();
		private static float _autoCell = 0.3f;

		// ---- vrchat mode: the budgeted diff renderer
		private const int NetStep = 3;              // baked frames per art frame: 3 x 33 ms ≈ 10 fps
		private const int NetFrameCap = 12;         // hard cap on moves issued in one frame (bursts are what the wire punishes)
		private static int _peakWindow;             // highest NetSend.LastPerSec seen since the last controller tick
		private static int _calmSeconds, _hotSeconds;   // controller memory: consecutive calm seconds / seconds in the red this show
		private static bool _netMode;               // the show (or the restore) running now is the network renderer
		private static bool _restoring; private static int _restoreIdx;
		private static float _netBudget = 120f, _credit;
		private static float _learnedBudget = 120f; // session memory: the next show starts where the last one settled
		private static float _netBaseline = 20f;    // EMA of the send rate while no art plays (everything else we send)
		private static float _nextNetSecond, _nextNetLog, _nextBaselineAt, _nextReassert;
		private static float _netHoldUntil;         // the clock waits at frame 0 for the ownership hand-overs, up to this time
		private static int _reassertCursor = -1;    // -1 = no re-assert pass in progress
		private static int _movesThisSec, _movesLastSec;
		private static Vector3[] _curPos;           // where each owned object is (as far as we moved it): no native reads in the search
		private static bool[] _held, _dead, _inBox; // held by somebody / destroyed / home inside the picture (a wrong pixel)
		private static float _cell, _cellH; private static Vector3 _origin, _parkBase;
		private static readonly List<int> _wrong = new List<int>(), _empty = new List<int>();
		private static int _rotW, _rotE;
		// per-grid statistics of the bake, computed once per session (the frames never change)
		private sealed class GridStat { public int P85, Max; public float MovesPerSec; }
		private static readonly Dictionary<int, GridStat> _gridStats = new Dictionary<int, GridStat>();
		private static byte[][] _gridStatsFrames;
		private static float[] _scratchA, _scratchB;

		private static bool Networked => Mode == "vrchat";

		// The one setting: picture / shape width in metres. 0 = auto — objects at their natural size, edge to edge.
		private static float ArtSize
		{
			get { try { float v = ModConfig.MarkArtSize.Value; return v <= 0f ? 0f : Mathf.Clamp(v, 0.5f, 200f); } catch { return 0f; } }
		}

		// ownership hand-overs (vrchat mode), a few per tick so the hand-over itself is not one burst
		private static readonly Queue<GameObject> _ownQ = new Queue<GameObject>();
		private static float _nextOwnDrain;

		public override void OnUpdate()
		{
			try
			{
				// RightShift+K puts the mark, RightShift+T teleports the objects to it. Not B and N: B is
				// the chatbox Bad Apple's key and N opens the overlay menu — sharing them fired both.
				if (Input.GetKey(KeyCode.RightShift))
				{
					if (Input.GetKeyDown(KeyCode.K)) PutMark();
					if (Input.GetKeyDown(KeyCode.T)) TeleportObjectsToMark();
				}
				DrainOwnership();
				// THE MUSIC SWITCH IS LIVE (2026-09-13). BadAppleMusic was read once, in
				// StartShowAudio, and never again: switching it off mid-show left the soundtrack
				// playing until the show itself ended, and switching it on mid-show did nothing.
				// Asserted here on the edge, which is the only place that runs for the whole show.
				if (_baPlaying)
				{
					bool wantMusic = false;
					try { wantMusic = ModConfig.BadAppleMusic.Value; } catch { }
					if (!wantMusic && Core.BadAppleAudio.Playing) { try { Core.BadAppleAudio.Stop(); } catch { } }
					else if (wantMusic && !Core.BadAppleAudio.Playing) StartShowAudio();
				}
				if (_baPlaying) { if (_netMode) NetUpdate(); else BadAppleUpdate(); }
				else if (_restoring) RestoreUpdate();
				else { SampleBaseline(); ShapeKeepAlive(VaClock.Now); }
			}
			catch (Exception e) { Status = "failed: " + e.Message; }
		}

		// ---------------------------------------------------------------- the mark

		public static void PutMark()
		{
			var cam = Camera.main;
			if (cam == null) { Status = "no camera"; return; }
			Vector3 pos = cam.transform.position + cam.transform.forward * 3f;
			try
			{
				var ray = new Ray(cam.transform.position, cam.transform.forward);
				var hits = Physics.RaycastAll(ray, 80f, ~0, QueryTriggerInteraction.Ignore);
				float best = float.MaxValue;
				int playerLayer = LayerMask.NameToLayer("Player"), localLayer = LayerMask.NameToLayer("PlayerLocal");
				if (hits != null)
					foreach (var h in hits)
					{
						if (h.collider == null) continue;
						int layer = h.collider.gameObject.layer;
						if (layer == playerLayer || layer == localLayer) continue;
						if (h.distance < 0.3f || h.distance >= best) continue;
						best = h.distance; pos = h.point + h.normal * 0.05f;
					}
			}
			catch { }
			SetMark(pos);
			Status = "mark placed " + Vector3.Distance(cam.transform.position, pos).ToString("0.0") + "m ahead — TP / orbit / shapes / Bad Apple act around it";
		}

		private static void SetMark(Vector3 pos)
		{
			MarkPos = pos;
			EnsureMarker().transform.position = pos;
		}

		public static void Clear()
		{
			StopBadApple(restore: true);
			RestoreArt();
			_ownQ.Clear();
			if (ObjectOrbitModule.Active && _marker != null) ObjectOrbitModule.Stop("stopped — the mark was cleared");
			if (_marker != null) { try { UnityEngine.Object.Destroy(_marker); } catch { } _marker = null; }
			Status = "mark cleared";
		}

		private static GameObject EnsureMarker()
		{
			if (_marker != null) return _marker;
			var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
			go.name = "VA_Mark";
			try { var c = go.GetComponent<Collider>(); if (c != null) UnityEngine.Object.Destroy(c); } catch { }
			go.transform.localScale = Vector3.one * 0.12f;
			try
			{
				var r = go.GetComponent<Renderer>();
				var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				if (r != null && sh != null) { var m = new Material(sh); m.color = new Color(0.72f, 0.36f, 1f, 1f); r.material = m; }
				else if (r != null) r.material.color = new Color(0.72f, 0.36f, 1f, 1f);
			}
			catch { }
			_marker = go;
			return go;
		}

		// ---------------------------------------------------------------- objects

		// Everything, everywhere. In vrchat mode only the networkable objects — moving anything else would be
		// invisible to the people the mode exists for.
		private static List<Transform> Gather()
		{
			var me = PlayerRef.LocalTransform();
			Vector3 around = me != null ? me.position : MarkPos;
			return ObjectOrbitModule.Collect(around, 100000f, 5000, networkableOnly: Networked);
		}

		public static void TeleportObjectsToMark()
		{
			if (!HasMark) { Status = "put a mark first (RightShift+K)"; return; }
			var objs = Gather();
			if (objs.Count == 0) { Status = "no loose objects in this world"; return; }
			for (int i = 0; i < objs.Count; i++)
			{
				float a = i * 2.39996f;
				float r = 0.35f + 0.09f * Mathf.Sqrt(i);
				Place(objs[i], MarkPos + new Vector3(Mathf.Cos(a) * r, 0.15f + 0.12f * (i % 4), Mathf.Sin(a) * r), freeze: false);
			}
			Status = objs.Count + " object(s) teleported to the mark" + ModeTag();
		}

		public static void OrbitAroundMark()
		{
			if (!HasMark) { Status = "put a mark first (RightShift+K)"; return; }
			ObjectOrbitModule.Start(_marker.transform, "the mark");
			Status = "objects orbiting the mark";
		}

		public static void Shape(string kind)
		{
			if (!HasMark) { Status = "put a mark first (RightShift+K)"; return; }
			StopBadApple(restore: false);
			RestoreArt();
			var objs = Gather();
			if (objs.Count == 0) { Status = "no loose objects in this world"; return; }
			FacingAxes(out Vector3 right, out Vector3 up);
			float size = ArtSize > 0f ? ArtSize : Mathf.Max(1f, objs.Count * MedianSize(objs) * 1.05f / Mathf.PI);
			var pts = ShapePoints((kind ?? "ring").ToLowerInvariant(), objs.Count, size, right, up);
			for (int i = 0; i < objs.Count && i < pts.Count; i++) Place(objs[i], MarkPos + pts[i], freeze: true);
			Status = objs.Count + " object(s) → " + kind + ModeTag() + " — CLEAR MARK puts them back";
		}

		private static string ModeTag() => Mode == "vrchat" ? " (networked, everyone sees it)" : " (local)";

		// ---------------------------------------------------------------- shapes

		private static List<Vector3> ShapePoints(string kind, int n, float size, Vector3 right, Vector3 up)
		{
			var pts = new List<Vector3>(n);
			float R = size * 0.5f;
			Vector3 fwd = Vector3.Cross(up, right);
			switch (kind)
			{
				case "line":
					for (int i = 0; i < n; i++) pts.Add(right * ((i / (float)Math.Max(1, n - 1) - 0.5f) * size) + up * 0.3f);
					break;
				case "wall":
				{
					int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(n * 1.6f)));
					for (int i = 0; i < n; i++) { int c = i % cols, r = i / cols; pts.Add(right * ((c / (float)Math.Max(1, cols - 1) - 0.5f) * size) + up * (0.2f + r * (size / Mathf.Max(1, cols)) * 0.9f)); }
					break;
				}
				case "heart":
					for (int i = 0; i < n; i++)
					{
						float t = i / (float)n * Mathf.PI * 2f;
						float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
						float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
						pts.Add(right * (x / 17f * R) + up * (0.9f + y / 17f * R));
					}
					break;
				case "spiral":
					for (int i = 0; i < n; i++) { float t = i / (float)n; float a = t * Mathf.PI * 6f; pts.Add(right * (Mathf.Cos(a) * R * t) + fwd * (Mathf.Sin(a) * R * t) + up * (0.1f + t * size)); }
					break;
				case "cube":
				{
					int side = Mathf.Max(2, Mathf.CeilToInt(Mathf.Pow(n, 1f / 3f)));
					float s = size / Mathf.Max(1, side - 1);
					for (int i = 0; i < n; i++) { int x = i % side, y = (i / side) % side, z = i / (side * side); pts.Add(right * ((x - (side - 1) * 0.5f) * s) + up * (0.15f + y * s) + fwd * ((z - (side - 1) * 0.5f) * s)); }
					break;
				}
				case "star":
					for (int i = 0; i < n; i++) { float t = i / (float)n * Mathf.PI * 2f; float rr = R * (0.55f + 0.45f * Mathf.Abs(Mathf.Cos(t * 2.5f))); pts.Add(right * (Mathf.Cos(t) * rr) + up * (R + Mathf.Sin(t) * rr)); }
					break;
				default:
					for (int i = 0; i < n; i++) { float a = i / (float)n * Mathf.PI * 2f; pts.Add(right * (Mathf.Cos(a) * R) + fwd * (Mathf.Sin(a) * R) + up * 0.6f); }
					break;
			}
			return pts;
		}

		private static void FacingAxes(out Vector3 right, out Vector3 up)
		{
			up = Vector3.up; right = Vector3.right;
			try
			{
				var cam = Camera.main;
				if (cam != null) { Vector3 f = cam.transform.forward; f.y = 0f; if (f.sqrMagnitude > 0.001f) right = Vector3.Cross(Vector3.up, f.normalized).normalized * -1f; }
			}
			catch { }
		}

		// ---------------------------------------------------------------- Bad Apple

		/// <summary>Start (mode = local | vrchat; anything else is local) or, if playing or still putting objects back, stop.</summary>
		public static void ToggleBadApple(string mode) => ToggleBadApple(mode, CurrentClip);

		/// <summary>Start (mode = local | vrchat) or stop. clip = "badapple" for the built-in bake, or
		/// the name of a local file in ClipsDir.</summary>
		public static void ToggleBadApple(string mode, string clip)
		{
			if (_restoring)
			{
				// the paced restore is still running: the user wants out NOW, so snap the rest home
				StopBadApple(restore: true);
				Status = "object Bad Apple stopped — objects put back";
				return;
			}
			if (_baPlaying)
			{
				if (_netMode) { BeginPacedRestore(); Status = "object Bad Apple stopped — objects going home at the network's pace (STOP again snaps them back)"; }
				else { StopBadApple(restore: true); Status = "object Bad Apple stopped"; }
				return;
			}
			if (!HasMark) { Status = "put a mark first (RightShift+K)"; return; }
			Mode = string.Equals(mode, "vrchat", StringComparison.OrdinalIgnoreCase) ? "vrchat" : "local";
			CurrentClip = string.IsNullOrEmpty(clip) ? "badapple" : clip;
			if (!LoadFrames(CurrentClip)) return;
			if (Mode == "vrchat") StartNet(); else StartLocal();
		}

		private static bool LoadFrames(string clip)
		{
			if (TryLoadClip(clip, out _baFrames, out _baW, out _baH, out _baInterval)) { _baSquareCells = true; return true; }
			// The low-res chatbox bake is a fallback for BAD APPLE only — a missing local clip is just missing.
			if (!string.IsNullOrEmpty(clip) && !string.Equals(clip, "badapple", StringComparison.OrdinalIgnoreCase))
			{
				Status = "clip '" + clip + "' not found in " + ClipsDir;
				return false;
			}
			if (_txtFrames == null && BadAppleModule.TryGetFrames(out string[] text, out _txtW, out _txtH, out _txtInterval))
			{
				_txtFrames = new byte[text.Length][];
				for (int i = 0; i < text.Length; i++) _txtFrames[i] = ToLevels(text[i]);
			}
			if (_txtFrames != null && _txtFrames.Length > 0)
			{
				_baFrames = _txtFrames; _baW = _txtW; _baH = _txtH; _baInterval = _txtInterval; _baSquareCells = false;
				return true;
			}
			Status = "Bad Apple frames not available";
			return false;
		}

		// ---- LOCAL: every loose object, parked under the picture, the bake's full frame rate.

		private static bool StartLocal()
		{
			RestoreArt(); _syncMuted = 0;
			var objs = Gather();
			if (objs.Count < 8) { Status = "need at least 8 loose objects in this world (found " + objs.Count + ")"; return false; }
			FacingAxes(out _baRight, out _);
			foreach (var t in objs) Place(t, MarkPos - Vector3.up * 40f, freeze: true);   // park everything under the picture
			ChooseResolution(_art.Count);
			_autoCell = MedianSize(null) * 1.05f;
			_netMode = false; _restoring = false; NetInfo = "";
			_baPlaying = true; _baStart = VaClock.Now; _baLastIdx = -1;
			// LOCAL has no ownership hand-over to wait for: the objects are already parked and the
			// first frame draws on the next tick, so the song starts here.
			StartShowAudio();
			float width = ArtSize > 0f ? ArtSize : _autoCell * _baCW;
			Status = "object Bad Apple — " + _art.Count + " objects as pixels on a " + _baCW + "x" + _baCH + " grid, " + width.ToString("0.#") + " m wide" + ModeTag();
			VRChatArchiveModPlugin.Logger.LogInfo($"[Mark] Bad Apple (local): {_art.Count} objects, grid {_baCW}x{_baCH} (down x{_baDown}), {width:0.#} m; {_syncMuted} ObjectSync component(s) muted so nothing leaves this client.");
			return true;
		}

		// Immediate stop (both modes). The network renderer remembers the budget it settled on.
		private static void StopBadApple(bool restore)
		{
			if (!_baPlaying && !_restoring) return;
			if (_netMode) LearnBudget();
			_lastShowEnd = VaClock.Now;   // local moves count on the wire too when we own the objects
			_baPlaying = false; _restoring = false; _netMode = false; _restoreIdx = 0; NetInfo = "";
			Core.BadAppleAudio.Stop();
			if (restore) RestoreArt();
		}

		// THE SONG IS THE CLOCK, when there is one.
		//
		// Both modes used to count from VaClock.Now, and both drift from the music for
		// reasons built into the show: the networked mode holds frame 0 for as long as it takes to
		// own every object it will move (up to six seconds), and any hitch while objects load costs
		// frames nobody gets back. Reading the AudioSource's position instead makes the picture
		// follow the sample being played — it cannot drift from what you hear, and after a stall it
		// lands on the frame that belongs to the music instead of replaying the backlog.
		// Falls back to the wall clock when the audio could not be loaded, so the show still runs.
		private static float ShowSeconds()
		{
			float t = Core.BadAppleAudio.Time;
			return t >= 0f ? t : VaClock.Now - _baStart;
		}

		/// <summary>Starts the soundtrack and re-bases the fallback clock on the same instant, so the
		/// two agree even if the audio never comes up.</summary>
		private static void StartShowAudio()
		{
			try
			{
				if (!ModConfig.BadAppleMusic.Value) return;
				_baStart = VaClock.Now;
				Core.BadAppleAudio.Play(Mathf.Clamp01(ModConfig.BadAppleMusicVolume.Value), CurrentClip);
			}
			catch { }
		}

		private static void BadAppleUpdate()
		{
			// Nothing left to draw (the objects went away under us). The show is over, so the song is
			// over — otherwise the music plays on over a picture that no longer exists.
			if (_baFrames == null || _baFrames.Length == 0 || _art.Count == 0)
			{ Core.BadAppleAudio.Stop(); _baPlaying = false; return; }
			float elapsed = ShowSeconds() * 1000f;
			int idx = (int)(elapsed / Mathf.Max(1, _baInterval));
			if (idx >= _baFrames.Length) { StopBadApple(restore: true); Status = "object Bad Apple — done"; return; }
			if (idx == _baLastIdx) return;
			_baLastIdx = idx;

			byte[] f = _baFrames[idx];
			int n = _art.Count;
			if (_baCells == null || _baCells.Length != _baCW * _baCH || _cellObj == null || _objCell == null || _objCell.Length < n) ChooseResolution(n);
			ComputeWant(f, n);
			int cells = _baCells.Length;

			// STABLE ASSIGNMENT: an object stays on its cell while it is wanted; only the ones whose cell
			// went dark move, onto the cells that just lit up. That is what makes it read as a video.
			_free.Clear(); _newCells.Clear();
			for (int o = 0; o < n; o++)
			{
				int cc = _objCell[o];
				if (cc >= 0 && (cc >= cells || !_want[cc])) { if (cc < cells) _cellObj[cc] = -1; _objCell[o] = -1; cc = -1; }
				if (cc < 0) _free.Add(o);
			}
			for (int i = 0; i < cells; i++) if (_want[i] && _cellObj[i] < 0) _newCells.Add(i);

			float cell = ArtSize > 0f ? ArtSize / _baCW : _autoCell;
			float cellH = _baSquareCells ? cell : cell * 2f;
			float width = cell * _baCW;
			Vector3 origin = MarkPos - _baRight * (width * 0.5f) + Vector3.up * (0.3f + _baCH * cellH);
			int k = 0;
			for (; k < _newCells.Count && k < _free.Count; k++)
			{
				int o = _free[k], ci = _newCells[k];
				_cellObj[ci] = o; _objCell[o] = ci; _parked[o] = false;
				int c = ci % _baCW, r = ci / _baCW;
				MoveArranged(_art[o], origin + _baRight * ((c + 0.5f) * cell) - Vector3.up * ((r + 0.5f) * cellH));
			}
			for (; k < _free.Count; k++)
			{
				int o = _free[k];
				if (_parked[o]) continue;
				_parked[o] = true;
				MoveArranged(_art[o], MarkPos - Vector3.up * 40f + _baRight * ((o % 20) * 0.3f) + Vector3.forward * (((o / 20) % 20) * 0.3f));
			}
		}

		// Wanted cells = the figure (after the per-frame polarity choice), thinned evenly when there are more
		// of them than objects. Shared by both renderers.
		private static void ComputeWant(byte[] f, int n)
		{
			int figure = Coarsen(f, _baDown, _baCW, _baCH, _baCells, out bool invert);
			int cells = _baCells.Length;
			float stride = figure > n ? figure / (float)Mathf.Max(1, n) : 1f;
			float acc = 0f; int seen = 0;
			for (int i = 0; i < cells; i++)
			{
				bool on = invert ? _baCells[i] < 16f : _baCells[i] >= 16f;
				if (on && stride > 1f) { if (seen++ < acc) on = false; else acc += stride; }
				_want[i] = on;
			}
		}

		// ---- VRCHAT: owned pickups, a diff served through a move budget.

		private static bool StartNet()
		{
			RestoreArt();
			var objs = ObjectOrbitModule.Collect(MarkPos, 100000f, 5000, networkableOnly: true);   // nearest to the MARK
			if (objs.Count < 8) { Status = "need at least 8 networkable pickups in this world (found " + objs.Count + ")"; return false; }
			FacingAxes(out _baRight, out _);
			_netBudget = _learnedBudget;

			int d = ChooseNetDown(objs.Count);
			var st = StatsFor(d);
			int take = Mathf.Min(objs.Count, (int)(st.Max * 1.15f) + 10);
			for (int i = 0; i < take; i++) Own(objs[i]);
			int n = _art.Count;
			if (n < 8) { RestoreArt(); _ownQ.Clear(); Status = "could not take the objects (" + n + " usable)"; return false; }

			ApplyGrid(d, n);
			_autoCell = MedianSize(null) * 1.05f;
			// Geometry is fixed for the show: the diff only knows what it has already placed.
			_cell = ArtSize > 0f ? ArtSize / _baCW : _autoCell;
			_cellH = _baSquareCells ? _cell : _cell * 2f;
			float width = _cell * _baCW;
			_origin = MarkPos - _baRight * (width * 0.5f) + Vector3.up * (0.3f + _baCH * _cellH);
			// Parking 40 m under the picture, unless that is at the world's respawn height: an owned pickup
			// falling through RespawnHeightY is respawned by VRChat, and the pixel is gone.
			float respawnY = RespawnHeight();
			bool parkUp = MarkPos.y - 40f < respawnY + 5f;
			_parkBase = parkUp ? MarkPos + Vector3.up * 40f : MarkPos - Vector3.up * 40f;

			_curPos = new Vector3[n]; _held = new bool[n]; _dead = new bool[n]; _inBox = new bool[n];
			int inBox = 0;
			for (int o = 0; o < n; o++) { _curPos[o] = _art[o].Pos; _inBox[o] = InPictureBox(_art[o].Pos); if (_inBox[o]) inBox++; }
			_wrong.Clear(); _empty.Clear(); _rotW = 0; _rotE = 0;
			_credit = 0f; _movesThisSec = 0; _movesLastSec = 0;
			float now = VaClock.Now;
			_nextNetSecond = now + 1f; _nextNetLog = now + 5f; _nextReassert = now + 3f; _reassertCursor = -1;
			_netHoldUntil = now + 6f;
			_peakWindow = 0; _calmSeconds = 0; _hotSeconds = 0;
			_netMode = true; _restoring = false; _restoreIdx = 0;
			_baPlaying = true; _baStart = now; _baLastIdx = -1;

			Status = $"object Bad Apple — VRCHAT NETWORK: {_baCW}x{_baCH} grid, {n} objects owned, budget {_netBudget:0} moves/s (auto)";
			NetInfo = $"{_baCW}x{_baCH} · {n} objects · budget {_netBudget:0} moves/s · sent 0 · 0 ev/s · backlog 0";
			VRChatArchiveModPlugin.Logger.LogInfo(
				$"[Mark] Bad Apple (vrchat): {n}/{objs.Count} pickups owned, grid {_baCW}x{_baCH} (down x{d}: p85 {st.P85}, max {st.Max}, ~{st.MovesPerSec:0} moves/s), " +
				$"{width:0.#} m, budget {_netBudget:0}, {inBox} object(s) inside the picture to park, parking {(parkUp ? "above" : "below")}.");
			// CAN ANYONE ELSE SEE THESE? A VRC_Pickup alone does not sync its position — VRCObjectSync (or a
			// world script) does. Count the objects that carry one and name the components of the first two,
			// so "network mode is invisible" can be read off the log instead of guessed.
			try
			{
				int synced = 0; var sample = new System.Text.StringBuilder();
				for (int o = 0; o < n; o++)
				{
					var go = _art[o].Go; if (go == null || !Core.NativeGuard.Alive(go)) continue;
					bool has = false; var comps = go.GetComponents<Component>(); if (comps == null) continue;
					var names = o < 2 ? new System.Text.StringBuilder() : null;
					foreach (var c in comps)
					{
						if (c == null) continue;
						string nm; try { nm = Core.MenuCard.Il2CppNameOf(c) ?? ""; } catch { continue; }
						if (nm.Length == 0 || nm == "?") { try { nm = c.GetIl2CppType().Name ?? ""; } catch { } }
						if (nm.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0) has = true;
						if (names != null) { if (names.Length > 0) names.Append(", "); names.Append(nm); }
					}
					if (has) synced++;
					if (names != null) sample.Append(" | '").Append(go.name).Append("': ").Append(names);
				}
				VRChatArchiveModPlugin.Logger.LogInfo($"[Mark] net objects with an ObjectSync component: {synced}/{n}.{sample}");
				if (synced == 0) Status += " ⚠ none of these objects has ObjectSync: only you will see them move";
			}
			catch { }
			return true;
		}

		// Take an object: remember its state, freeze it, queue the ownership. It is NOT moved — it stays home
		// until a cell needs it, so the start costs nothing on the wire beyond the hand-overs.
		private static void Own(Transform t)
		{
			try
			{
				if (t == null || !Core.NativeGuard.Alive(t)) return;
				var body = t.GetComponent<Rigidbody>() ?? t.GetComponentInChildren<Rigidbody>();
				var a = new Arranged { T = t, Go = t.gameObject, Pos = t.position, Rot = t.rotation, Body = body, Scale = t.localScale, Size = MeasureSize(t), Pickup = FindPickup(t) };
				if (body != null)
				{
					a.WasKinematic = body.isKinematic; a.WasGravity = body.useGravity;
					try { a.WasInterp = body.interpolation; body.interpolation = RigidbodyInterpolation.Interpolate; a.InterpSet = true; } catch { }
					body.isKinematic = true;
				}
				_art.Add(a);
				_ownQ.Enqueue(a.Go);
			}
			catch { }
		}

		private static VRC.SDKBase.VRC_Pickup FindPickup(Transform t)
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

		private static float RespawnHeight()
		{
			try
			{
				var sd = VRC.SDKBase.VRC_SceneDescriptor.Instance;
				if (sd != null && Core.NativeGuard.Alive(sd)) return (float)sd.RespawnHeightY;
			}
			catch { }
			return -100f;
		}

		// Is a world position inside the picture's box (expanded by 0.5 m)? Such an object is a wrong pixel at home.
		private static bool InPictureBox(Vector3 p)
		{
			Vector3 rel = p - MarkPos;
			float x = Vector3.Dot(rel, _baRight), y = rel.y, z = Vector3.Dot(rel, Vector3.Cross(Vector3.up, _baRight));
			float halfW = _cell * _baCW * 0.5f + 0.5f, h = _cellH * _baCH;
			return Mathf.Abs(x) <= halfW && y >= 0.3f - 0.5f && y <= 0.3f + h + 0.5f && Mathf.Abs(z) <= 0.5f;
		}

		private static Vector3 CellPos(int ci)
		{
			int c = ci % _baCW, r = ci / _baCW;
			return _origin + _baRight * ((c + 0.5f) * _cell) - Vector3.up * ((r + 0.5f) * _cellH);
		}

		private static Vector3 ParkPos(int o) => _parkBase + _baRight * ((o % 20) * 0.3f) + Vector3.forward * (((o / 20) % 20) * 0.3f);

		// Statistics of the bake at one grid step, from every 4th art-frame pair: the figure-cell count (p85 and
		// max, for how many objects the picture needs) and the mean paired move rate max(newly lit, newly dark)
		// per second (what it costs on the wire when freed objects are re-used for the cells that lit up).
		private static GridStat StatsFor(int d)
		{
			if (!ReferenceEquals(_gridStatsFrames, _baFrames)) { _gridStats.Clear(); _gridStatsFrames = _baFrames; }
			if (_gridStats.TryGetValue(d, out var s)) return s;
			int cw = Mathf.Max(1, _baW / d), ch = Mathf.Max(1, _baH / d), cells = cw * ch;
			if (_scratchA == null || _scratchA.Length < cells) { _scratchA = new float[cells]; _scratchB = new float[cells]; }
			int artCount = _baFrames.Length / NetStep;
			var figures = new List<int>(artCount / 2 + 2);
			long movesSum = 0; int pairs = 0, maxFig = 0;
			for (int a = 0; a + 1 < artCount; a += 4)
			{
				int f0 = Coarsen(_baFrames[a * NetStep], d, cw, ch, _scratchA, out bool inv0);
				int f1 = Coarsen(_baFrames[(a + 1) * NetStep], d, cw, ch, _scratchB, out bool inv1);
				figures.Add(f0); figures.Add(f1);
				if (f0 > maxFig) maxFig = f0;
				if (f1 > maxFig) maxFig = f1;
				int lit = 0, dark = 0;
				for (int i = 0; i < cells; i++)
				{
					bool on0 = inv0 ? _scratchA[i] < 16f : _scratchA[i] >= 16f;
					bool on1 = inv1 ? _scratchB[i] < 16f : _scratchB[i] >= 16f;
					if (on1 && !on0) lit++; else if (on0 && !on1) dark++;
				}
				movesSum += Math.Max(lit, dark); pairs++;
			}
			figures.Sort();
			s = new GridStat
			{
				Max = maxFig,
				P85 = figures.Count > 0 ? figures[Mathf.Clamp((int)(figures.Count * 0.85f), 0, figures.Count - 1)] : 0,
				MovesPerSec = pairs > 0 ? (movesSum / (float)pairs) * (1000f / (NetStep * Mathf.Max(1, _baInterval))) : 0f,
			};
			_gridStats[d] = s;
			return s;
		}

		// The finest grid whose mean move rate fits the budget and whose p85 figure fits the objects; else the coarsest.
		// The finest grid whose mean move rate fits the budget and whose p85 figure fits the objects. The
		// budget never pushes below 16x12 (d = 4): coarser than that is not a picture any more, so past
		// d = 4 only the object count can force it.
		private static int ChooseNetDown(int available)
		{
			int coarsest = 1;
			for (int d = 1; d <= 8; d++)
			{
				int cw = _baW / d, ch = _baH / d;
				if (cw < 4 || ch < 3) break;
				coarsest = d;
				var s = StatsFor(d);
				bool budgetOk = d >= 4 || s.MovesPerSec <= _netBudget;
				if (budgetOk && s.P85 <= available) return d;
			}
			return coarsest;
		}

		private static void NetUpdate()
		{
			int n = _art.Count;
			if (_baFrames == null || _baFrames.Length == 0 || n == 0) { StopBadApple(restore: true); return; }
			float now = VaClock.Now;
			ReassertOwnership(now);

			// HAND-OVERS FIRST. A pixel moved before we own the object is snapped back by its current owner's
			// next update and then stays wrong (we believe it placed). So the clock is held at frame 0 until the
			// ownership queue is empty (20 per 100 ms: well under a second for a show's worth), with a 6 s cap.
			// …AND THE MUSIC WAITS WITH IT. Starting the song at the button press and the picture at
			// the end of the hand-overs is precisely how they came out of step: whatever the ownership
			// queue costs, the song was already that far ahead. So the track starts on the first frame
			// the picture actually draws, and from then on the picture follows the track.
			if (_ownQ.Count > 0 && now < _netHoldUntil) { _baStart = now; return; }
			if (!Core.BadAppleAudio.Playing && _baLastIdx < 0) StartShowAudio();

			int idx = (int)(ShowSeconds() * 1000f / (NetStep * Mathf.Max(1, _baInterval)));
			if (idx * NetStep >= _baFrames.Length) { BeginPacedRestore(); Status = "object Bad Apple — done, objects going home"; return; }
			if (idx != _baLastIdx) { _baLastIdx = idx; ComputeWant(_baFrames[idx * NetStep], Available()); }

			// SMOOTH, NOT BURSTY. The credit cap was a quarter of a second of budget and the frame cap 40:
			// together they let one frame fire 60+ moves, and the wire saw spikes of 500-700 ev/s while
			// the per-second average read fine. A tenth of a second of credit and 12 moves a frame keep
			// the send rate flat, which is what the receivers' queues care about.
			_credit = Mathf.Min(_credit + _netBudget * VaClock.Delta, _netBudget * 0.1f);
			BuildDiff();
			ServeMoves();
			int backlog = Backlog();

			// PEAK, NOT A SAMPLE. NetSend rolls once a second on its own clock; reading LastPerSec once a
			// second on ours caught stale or zero values ("0 ev/s" while the wire did 500) and the budget
			// climbed into the red. The highest LastPerSec seen since the last tick is the number that
			// counts — spikes are what queue up on the other side.
			{ int lp = NetSendModule.LastPerSec; if (lp > _peakWindow) _peakWindow = lp; }

			if (now >= _nextNetSecond)
			{
				_nextNetSecond = now + 1f;
				_movesLastSec = _movesThisSec; _movesThisSec = 0;
				int sent = _peakWindow; _peakWindow = 0;
				int rate = Mathf.Max(0, sent - Mathf.RoundToInt(_netBaseline));
				// The controller. Not during the first 3 s and not while hand-overs are still going out: those
				// events are not moves. A second in which the counter saw nothing at all never RAISES the budget
				// (a dead hook must not let it climb). Measured 2026-09-04: 500-700 ev/s peaks = the others
				// see a queue; ~400 (the orbit) is fine. So the ceiling is 380, growth is slow (+4 %) and only
				// after three calm seconds in a row, and the cap is 260 moves/s (the 21x16 grid's mean).
				if (now - _baStart >= 3f && _ownQ.Count == 0)
				{
					if (rate > 380) { _netBudget = Mathf.Max(80f, _netBudget * 0.8f); _calmSeconds = 0; _hotSeconds++; }
					else if (rate < 250 && backlog > 0 && sent > 0)
					{
						if (++_calmSeconds >= 3) { _netBudget = Mathf.Min(260f, _netBudget * 1.04f); _calmSeconds = 0; }
					}
					else _calmSeconds = 0;
				}
				NetInfo = $"{_baCW}x{_baCH} · {n} objects · budget {_netBudget:0} moves/s · sent {_movesLastSec} · peak {rate} ev/s · backlog {backlog}";
				if (now >= _nextNetLog) { _nextNetLog = now + 5f; VRChatArchiveModPlugin.Logger.LogInfo("[Mark] net: " + NetInfo); }
			}
		}

		private static int Available()
		{
			int n = _art.Count, avail = 0;
			for (int o = 0; o < n; o++) if (!_held[o] && !_dead[o]) avail++;
			return avail;
		}

		// Every 3 s, at most 20 objects per frame: is it alive, is somebody holding it, is it still ours.
		private static void ReassertOwnership(float now)
		{
			if (_reassertCursor < 0)
			{
				if (now < _nextReassert) return;
				_nextReassert = now + 3f; _reassertCursor = 0;
			}
			int n = _art.Count, end = Mathf.Min(n, _reassertCursor + 20);
			for (int o = _reassertCursor; o < end; o++) CheckObject(o);
			_reassertCursor = end >= n ? -1 : end;
		}

		private static void CheckObject(int o)
		{
			var a = _art[o];
			bool alive = false;
			try { alive = a != null && a.Go != null && Core.NativeGuard.Alive(a.Go) && a.T != null && a.Go.transform != null; } catch { alive = false; }
			if (!alive) { if (!_dead[o]) { _dead[o] = true; FreeCell(o); } return; }
			if (_dead[o]) return;

			bool held = false;
			try { if (a.Pickup != null && Core.NativeGuard.Alive(a.Pickup)) held = a.Pickup.IsHeld; } catch { held = false; }
			if (held)
			{
				// in somebody's hands: not a pixel until they let go
				if (!_held[o]) { _held[o] = true; FreeCell(o); }
				return;
			}
			if (_held[o])
			{
				// back from somebody's hands: it is wherever they left it, and probably not kinematic any more
				_held[o] = false; _parked[o] = false;
				try { _curPos[o] = a.T.position; if (a.Body != null) { a.Body.isKinematic = true; } } catch { }
				_inBox[o] = InPictureBox(_curPos[o]);
			}
			try
			{
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (me != null && Core.NativeGuard.Alive(me) && !VRC.SDKBase.Networking.IsOwner(me, a.Go)) ObjectOrbitModule.TakeOwnershipCounted(a.Go);
			}
			catch { }
		}

		private static void FreeCell(int o)
		{
			int cc = _objCell[o];
			if (cc >= 0 && cc < _cellObj.Length && _cellObj[cc] == o) _cellObj[cc] = -1;
			_objCell[o] = -1;
		}

		// WRONG = objects sitting on a cell that is not wanted (or at home inside the picture); EMPTY = wanted
		// cells with nobody on them. Rebuilt each frame from the maps: a few hundred cheap reads.
		private static void BuildDiff()
		{
			_wrong.Clear(); _empty.Clear();
			int n = _art.Count, cells = _want.Length;
			for (int o = 0; o < n; o++) if (IsWrong(o)) _wrong.Add(o);
			for (int i = 0; i < cells; i++) if (_want[i] && _cellObj[i] < 0) _empty.Add(i);
		}

		private static bool IsWrong(int o)
		{
			if (_held[o] || _dead[o]) return false;
			int cc = _objCell[o];
			if (cc >= 0) return cc >= _want.Length || !_want[cc];
			return !_parked[o] && _inBox[o];
		}

		private static int Backlog()
		{
			int b = 0;
			for (int k = 0; k < _wrong.Count; k++) if (IsWrong(_wrong[k])) b++;
			for (int k = 0; k < _empty.Count; k++) if (_cellObj[_empty[k]] < 0) b++;
			return b;
		}

		// Spend the credit. (1) a wrong object onto the nearest empty cell: one move fixes two cells; (2) an empty
		// cell filled by the nearest free object (parked or still at home); (3) a wrong object parked. Whatever
		// is left simply persists into the next frame's diff: the picture lags a little instead of flooding.
		private static void ServeMoves()
		{
			int wCount = _wrong.Count, eCount = _empty.Count;
			if (wCount == 0 && eCount == 0) return;
			_rotW += 7; _rotE += 7;   // a different starting point every frame, so no region is starved
			int wStart = wCount > 0 ? _rotW % wCount : 0, eStart = eCount > 0 ? _rotE % eCount : 0;
			int wi = 0, ei = 0;       // entries examined so far in each list
			bool wrongLeft = wCount > 0, emptyLeft = eCount > 0;
			int served = 0;
			while (_credit >= 1f && served < NetFrameCap && (wrongLeft || emptyLeft))
			{
				if (wrongLeft && emptyLeft)
				{
					int o = NextWrong(ref wi, wCount, wStart);
					if (o < 0) { wrongLeft = false; continue; }
					ObjCellSpace(o, out float ox, out float oy);
					int best = -1; float bestD = float.MaxValue;
					for (int k = 0; k < eCount; k++)
					{
						int ci = _empty[k];
						if (_cellObj[ci] >= 0) continue;
						float dx = (ci % _baCW) + 0.5f - ox, dy = (ci / _baCW) + 0.5f - oy;
						float dd = dx * dx + dy * dy;
						if (dd < bestD) { bestD = dd; best = ci; }
					}
					if (best < 0) { emptyLeft = false; wi--; continue; }   // nothing empty left: this one is parked below
					if (Assign(o, best)) served++;
				}
				else if (emptyLeft)
				{
					int ci = NextEmpty(ref ei, eCount, eStart);
					if (ci < 0) { emptyLeft = false; continue; }
					int o = NearestFree(CellPos(ci));
					if (o < 0) { emptyLeft = false; continue; }   // no free object (thinning should prevent this): the cell waits
					if (Assign(o, ci)) served++;
				}
				else
				{
					int o = NextWrong(ref wi, wCount, wStart);
					if (o < 0) { wrongLeft = false; continue; }
					if (Park(o)) served++;
				}
			}
		}

		private static int NextWrong(ref int wi, int wCount, int wStart)
		{
			while (wi < wCount) { int o = _wrong[(wStart + wi) % wCount]; wi++; if (IsWrong(o)) return o; }
			return -1;
		}

		private static int NextEmpty(ref int ei, int eCount, int eStart)
		{
			while (ei < eCount) { int ci = _empty[(eStart + ei) % eCount]; ei++; if (_cellObj[ci] < 0) return ci; }
			return -1;
		}

		// An object's position in cell units (column, row), for the nearest-empty-cell search.
		private static void ObjCellSpace(int o, out float cx, out float cy)
		{
			int cc = _objCell[o];
			if (cc >= 0) { cx = (cc % _baCW) + 0.5f; cy = (cc / _baCW) + 0.5f; return; }
			Vector3 rel = _curPos[o] - _origin;
			cx = Vector3.Dot(rel, _baRight) / Mathf.Max(0.001f, _cell);
			cy = -rel.y / Mathf.Max(0.001f, _cellH);
		}

		// Nearest object that is on no cell (parked or still at home), by where we know it to be.
		private static int NearestFree(Vector3 target)
		{
			int n = _art.Count, best = -1; float bestD = float.MaxValue;
			for (int o = 0; o < n; o++)
			{
				if (_held[o] || _dead[o] || _objCell[o] >= 0) continue;
				if (!_parked[o] && _inBox[o]) continue;   // that one is WRONG, not free
				float dd = (_curPos[o] - target).sqrMagnitude;
				if (dd < bestD) { bestD = dd; best = o; }
			}
			return best;
		}

		private static bool Assign(int o, int ci)
		{
			FreeCell(o);
			if (!AliveObj(o)) return false;
			_cellObj[ci] = o; _objCell[o] = ci; _parked[o] = false;
			Vector3 p = CellPos(ci);
			_curPos[o] = p;
			MoveArranged(_art[o], p);
			_credit -= 1f; _movesThisSec++;
			return true;
		}

		private static bool Park(int o)
		{
			FreeCell(o);
			if (!AliveObj(o)) return false;
			_parked[o] = true;
			Vector3 p = ParkPos(o);
			_curPos[o] = p;
			MoveArranged(_art[o], p);
			_credit -= 1f; _movesThisSec++;
			return true;
		}

		private static bool AliveObj(int o)
		{
			bool ok = false;
			try { var a = _art[o]; ok = a != null && a.Go != null && Core.NativeGuard.Alive(a.Go) && a.T != null; } catch { ok = false; }
			if (!ok) _dead[o] = true;
			return ok;
		}

		// ---- paced restore (vrchat mode): home at the same budget, so the stop is not one burst either.

		// What the next show starts from: the budget this one settled on — unless this one spent time in the
		// red, in which case a value that saturated must not be remembered as good (back to a safe 120).
		private static void LearnBudget()
		{
			_learnedBudget = Mathf.Max(100f, _hotSeconds > 2 ? Mathf.Min(_netBudget, 120f) : Mathf.Min(_netBudget, 260f));
			_lastShowEnd = VaClock.Now;
			VRChatArchiveModPlugin.Logger.LogInfo($"[Mark] net: show ended, budget {_netBudget:0}, {_hotSeconds} hot second(s) -> next show starts at {_learnedBudget:0} moves/s.");
			_hotSeconds = 0; _calmSeconds = 0;
		}

		private static void BeginPacedRestore()
		{
			// THE MUSIC STOPS HERE TOO. StopBadApple is only the LOCAL stop and the "snap everything
			// back" path; the networked mode ends here instead — both when the user presses stop
			// (ToggleBadApple) and when the show reaches its last frame (NetUpdate) — and it used to
			// clear _baPlaying while leaving the soundtrack running to the end of the track.
			Core.BadAppleAudio.Stop();
			_baPlaying = false; NetInfo = "";
			LearnBudget();
			if (_art.Count == 0) { _netMode = false; _restoring = false; return; }
			_restoring = true; _restoreIdx = 0;
		}

		private static void RestoreUpdate()
		{
			_credit = Mathf.Min(_credit + _netBudget * VaClock.Delta, _netBudget * 0.25f);
			int served = 0;
			while (_restoreIdx < _art.Count && served < NetFrameCap)
			{
				int o = _restoreIdx;
				var a = _art[o];
				// An object that never left home costs nothing to restore: only its flags come back.
				bool moved = a != null && _curPos != null && o < _curPos.Length && (_curPos[o] - a.Pos).sqrMagnitude > 1e-4f;
				if (moved) { if (_credit < 1f) break; _credit -= 1f; served++; }
				RestoreOne(a);
				_restoreIdx++;
			}
			if (_restoreIdx >= _art.Count) { _art.Clear(); _restoring = false; _netMode = false; _restoreIdx = 0; }
		}

		// THE INSTANCE'S OWN TRAFFIC. In a 1000-ball world the instance master (often the owner) already
		// owns every rolling ball, so ObjectSync runs at 200-600 ev/s with NO art playing. The baseline
		// used to be capped at 120: the controller then read our share as huge, cut the budget to 40 and
		// the next show came up 9x6. Sampled only when nothing of ours has moved for 3 s, capped at 800.
		private static float _lastShowEnd;
		private static void SampleBaseline()
		{
			float now = VaClock.Now;
			if (now < _nextBaselineAt) return;
			_nextBaselineAt = now + 1f;
			if (now - _lastShowEnd < 3f) return;
			_netBaseline = Mathf.Clamp(_netBaseline + 0.3f * (NetSendModule.LastPerSec - _netBaseline), 0f, 800f);
		}

		// ---------------------------------------------------------------- frames

		// CLIPS. "" / "badapple" is the HD bake embedded in the DLL. ANY OTHER NAME is a LOCAL file
		// dropped in <BepInEx>\VRChatArchiveMod\clips\<name>.frames.gz (or .frames) on this machine.
		// Nothing but Bad Apple ever ships inside the mod, so adding a clip is a file copy — no
		// rebuild, and the public DLL neither grows nor carries anyone else's material.
		private sealed class Clip { public byte[][] Frames; public int W, H, Interval; }
		private static readonly Dictionary<string, Clip> _clipCache =
			new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);

		internal static string ClipsDir
		{
			get
			{
				try { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "clips"); }
				catch { return "clips"; }
			}
		}

		/// <summary>Names of the local clips sitting beside the built-in Bad Apple.</summary>
		public static List<string> ListClips()
		{
			var names = new List<string>();
			try
			{
				if (!System.IO.Directory.Exists(ClipsDir)) return names;
				foreach (var p in System.IO.Directory.GetFiles(ClipsDir))
				{
					string n = System.IO.Path.GetFileName(p);
					if (n.EndsWith(".frames.gz", StringComparison.OrdinalIgnoreCase)) names.Add(n.Substring(0, n.Length - 10));
					else if (n.EndsWith(".frames", StringComparison.OrdinalIgnoreCase)) names.Add(n.Substring(0, n.Length - 7));
				}
			}
			catch { }
			return names;
		}

		private static bool TryLoadClip(string clip, out byte[][] frames, out int w, out int h, out int intervalMs)
		{
			frames = null; w = 0; h = 0; intervalMs = 33;
			string key = string.IsNullOrEmpty(clip) ? "badapple" : clip;
			if (_clipCache.TryGetValue(key, out var hit))
			{
				frames = hit.Frames; w = hit.W; h = hit.H; intervalMs = hit.Interval;
				return frames != null && frames.Length > 0;
			}
			var got = new Clip();
			_clipCache[key] = got;   // cache the failure too, so a missing clip is not re-read every toggle
			try
			{
				System.IO.Stream raw = null;
				bool gzipped = true;
				if (string.Equals(key, "badapple", StringComparison.OrdinalIgnoreCase))
					raw = typeof(MarkModule).Assembly.GetManifestResourceStream("badapple_hd.frames.gz");
				else
				{
					string gzPath = System.IO.Path.Combine(ClipsDir, key + ".frames.gz");
					string plain = System.IO.Path.Combine(ClipsDir, key + ".frames");
					if (System.IO.File.Exists(gzPath)) raw = System.IO.File.OpenRead(gzPath);
					else if (System.IO.File.Exists(plain)) { raw = System.IO.File.OpenRead(plain); gzipped = false; }
				}
				if (raw == null) return false;
				using (raw)
				using (var src = gzipped
					? (System.IO.Stream)new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress)
					: raw)
				using (var reader = new System.IO.StreamReader(src, System.Text.Encoding.ASCII))
				{
					string[] header = (reader.ReadLine() ?? "").Split(' ');
					got.W = int.Parse(header[0]); got.H = int.Parse(header[1]); got.Interval = int.Parse(header[2]);
					var list = new List<byte[]>(7000);
					string line;
					while ((line = reader.ReadLine()) != null) if (line.Length == got.W * got.H) list.Add(ToLevels(line));
					got.Frames = list.ToArray();
				}
				frames = got.Frames; w = got.W; h = got.H; intervalMs = got.Interval;
				VRChatArchiveModPlugin.Logger.LogInfo($"[Mark] clip '{key}': {got.Frames.Length} frames {got.W}x{got.H} @ {got.Interval} ms.");
				return got.Frames.Length > 0;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Mark] clip '{key}' unavailable: " + e.Message); return false; }
		}

		private static byte[] ToLevels(string digits)
		{
			var b = new byte[digits.Length];
			for (int i = 0; i < digits.Length; i++) { char d = digits[i]; b[i] = (byte)(d <= '9' ? d - '0' : d - 'a' + 10); }
			return b;
		}

		// ---------------------------------------------------------------- pixel grid

		// LOCAL: the finest grid whose 85th-percentile figure-cell count fits the objects (50 -> ~12x9, 1000 -> 64x48).
		private static void ChooseResolution(int objects)
		{
			int down = 8;
			for (int d = 1; d <= 8; d++)
			{
				int cw = _baW / d, ch = _baH / d;
				if (cw < 4 || ch < 3) break;
				var counts = new List<int>();
				int step = Mathf.Max(1, _baFrames.Length / 60);
				for (int fi = 0; fi < _baFrames.Length; fi += step) counts.Add(Coarsen(_baFrames[fi], d, cw, ch, null, out _));
				counts.Sort();
				int p85 = counts[Mathf.Clamp((int)(counts.Count * 0.85f), 0, counts.Count - 1)];
				if (p85 <= objects) { down = d; break; }
			}
			ApplyGrid(down, objects);
		}

		// The grid and the assignment maps for one downsampling step.
		private static void ApplyGrid(int down, int objects)
		{
			_baDown = down;
			_baCW = Mathf.Max(1, _baW / _baDown); _baCH = Mathf.Max(1, _baH / _baDown);
			_baCells = new float[_baCW * _baCH];
			_want = new bool[_baCW * _baCH];
			_cellObj = new int[_baCW * _baCH]; for (int i = 0; i < _cellObj.Length; i++) _cellObj[i] = -1;
			_objCell = new int[Mathf.Max(1, objects)]; for (int i = 0; i < _objCell.Length; i++) _objCell[i] = -1;
			_parked = new bool[Mathf.Max(1, objects)];
		}

		// Average level per coarse cell; returns the figure count after the per-frame polarity choice
		// (Bad Apple flips black-on-white / white-on-black: the figure is the minority side).
		private static int Coarsen(byte[] f, int d, int cw, int ch, float[] into, out bool invert)
		{
			int dark = 0, total = cw * ch;
			float inv = 1f / (d * d);
			for (int r = 0; r < ch; r++)
				for (int c = 0; c < cw; c++)
				{
					int sum = 0;
					for (int y = 0; y < d; y++) { int row = (r * d + y) * _baW + c * d; for (int x = 0; x < d; x++) sum += f[row + x]; }
					float avg = sum * inv;
					if (into != null) into[r * cw + c] = avg;
					if (avg >= 16f) dark++;
				}
			invert = dark * 2 > total;
			return invert ? total - dark : dark;
		}

		// ---------------------------------------------------------------- placing objects

		private static void Place(Transform t, Vector3 pos, bool freeze)
		{
			try
			{
				if (t == null) return;
				var body = t.GetComponent<Rigidbody>() ?? t.GetComponentInChildren<Rigidbody>();
				if (freeze)
				{
					Arranged known = null;
					for (int i = 0; i < _art.Count; i++) if (_art[i] != null && ReferenceEquals(_art[i].T, t)) { known = _art[i]; break; }
					if (known == null)
					{
						known = new Arranged { T = t, Go = t.gameObject, Pos = t.position, Rot = t.rotation, Body = body, Scale = t.localScale, Size = MeasureSize(t) };
						if (body != null) { known.WasKinematic = body.isKinematic; known.WasGravity = body.useGravity; body.isKinematic = true; }
						if (!Networked) MuteSync(known);
						_art.Add(known);
					}
					// Where it must sit. In vrchat mode the write below happens BEFORE we own the object, so the
					// previous owner's next update snaps it back; the target is re-sent once the hand-over lands.
					known.Target = pos; known.HasTarget = true;
				}
				if (Networked) { _ownQ.Enqueue(t.gameObject); _reapplyAfterDrain = true; }
				t.position = pos;
				if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
			}
			catch { }
		}

		private static void DrainOwnership()
		{
			if (_ownQ.Count == 0) return;
			float now = VaClock.Now;
			if (now < _nextOwnDrain) return;
			_nextOwnDrain = now + 0.1f;
			for (int i = 0; i < 20 && _ownQ.Count > 0; i++)
			{
				var go = _ownQ.Dequeue();
				try { if (go != null) ObjectOrbitModule.TakeOwnershipCounted(go); } catch { }
			}
			// The last hand-over of a batch just went out: put every shape object back on its target so
			// what WE now own is what everyone receives (the write before ownership was overridden).
			if (_ownQ.Count == 0 && _reapplyAfterDrain) { _reapplyAfterDrain = false; if (!_netMode) ReapplyTargets(); }
		}

		private static bool _reapplyAfterDrain;
		private static float _nextShapeReassert;

		private static void ReapplyTargets()
		{
			int n = 0;
			for (int i = 0; i < _art.Count; i++)
			{
				var a = _art[i];
				try
				{
					if (a == null || !a.HasTarget || a.T == null || !Core.NativeGuard.Alive(a.T)) continue;
					a.T.position = a.Target;
					if (a.Body != null && a.Body.isKinematic) a.Body.MovePosition(a.Target);
					n++;
				}
				catch { }
			}
			if (n > 0) VRChatArchiveModPlugin.Logger.LogInfo("[Mark] re-sent " + n + " shape position(s) now that the objects are ours.");
		}

		// SHAPES STAY OURS. Somebody grabbing an arranged object takes its ownership and VRChat then follows
		// their copy; every 3 s any object we no longer own is asked back and its target re-sent when the
		// hand-over lands. Only for standing arrangements (shapes) in vrchat mode — Bad Apple has its own.
		// A STANDING ARRANGEMENT HOLDS ITS POSE. Both modes: every second (local) or three (network) each
		// arranged object is re-frozen if physics got hold of it and put back on its target if it drifted
		// (a sync update, a collision, a world script). Network mode additionally asks back the ownership
		// of anything somebody took, and re-sends the pose once the hand-over lands. The owner reported
		// shapes "not staying in place" — this is the answer to whatever was moving them.
		private static void ShapeKeepAlive(float now)
		{
			if (_art.Count == 0 || _baPlaying || _restoring || now < _nextShapeReassert) return;
			_nextShapeReassert = now + (Networked ? 3f : 1f);
			try
			{
				VRC.SDKBase.VRCPlayerApi me = null;
				if (Networked) { try { me = VRC.SDKBase.Networking.LocalPlayer; if (me != null && !Core.NativeGuard.Alive(me)) me = null; } catch { me = null; } }
				int asked = 0, fixedPos = 0, refrozen = 0;
				for (int i = 0; i < _art.Count; i++)
				{
					var a = _art[i];
					try
					{
						if (a == null || !a.HasTarget || a.Go == null || !Core.NativeGuard.Alive(a.Go) || a.T == null) continue;
						if (a.Pickup != null && Core.NativeGuard.Alive(a.Pickup) && a.Pickup.IsHeld) continue;   // in someone's hands: theirs for now
						if (Networked && me != null && !VRC.SDKBase.Networking.IsOwner(me, a.Go)) { _ownQ.Enqueue(a.Go); asked++; continue; }
						if (a.Body != null && Core.NativeGuard.Alive(a.Body) && !a.Body.isKinematic) { a.Body.isKinematic = true; a.Body.velocity = Vector3.zero; a.Body.angularVelocity = Vector3.zero; refrozen++; }
						if ((a.T.position - a.Target).sqrMagnitude > 0.0025f)
						{
							a.T.position = a.Target;
							if (a.Body != null && Core.NativeGuard.Alive(a.Body) && a.Body.isKinematic) a.Body.MovePosition(a.Target);
							fixedPos++;
						}
					}
					catch { }
				}
				if (asked > 0) _reapplyAfterDrain = true;
				if (fixedPos + refrozen + asked > 0 && now >= _nextKeepAliveLog)
				{
					_nextKeepAliveLog = now + 10f;
					VRChatArchiveModPlugin.Logger.LogInfo($"[Mark] shape keep-alive ({Mode}): {fixedPos} put back on target, {refrozen} re-frozen, {asked} ownership(s) asked back.");
				}
			}
			catch { }
		}
		private static float _nextKeepAliveLog;

		/// <summary>Which mode shapes, TP and orbit use from now on: "vrchat" (owned and moved for everyone) or "local".</summary>
		public static void SetMode(string mode)
		{
			if (_baPlaying || _restoring) { Status = "stop the Bad Apple first — its mode is set by its own buttons"; return; }
			Mode = string.Equals(mode, "vrchat", StringComparison.OrdinalIgnoreCase) ? "vrchat" : "local";
			Status = Mode == "vrchat" ? "MARK is NETWORKED — shapes, TP and orbit are owned and moved for everyone" : "MARK is LOCAL — shapes, TP and orbit are only on your screen";
			VaTagsModule.LastStatus = Status;
		}

		private static void MoveArranged(Arranged a, Vector3 pos)
		{
			try
			{
				if (a == null || a.T == null) return;
				if (a.Body != null && a.Body.isKinematic) a.Body.MovePosition(pos); else a.T.position = pos;
			}
			catch { }
		}

		// Everything home at once (local stop, clear, scene change, shutdown, or a paced restore cut short).
		private static void RestoreArt()
		{
			for (int i = 0; i < _art.Count; i++) RestoreOne(_art[i]);
			_art.Clear();
			_restoring = false; _restoreIdx = 0;
		}

		private static void RestoreOne(Arranged a)
		{
			try
			{
				if (a == null || a.T == null) return;
				if (a.Go != null && !Core.NativeGuard.Alive(a.Go)) return;   // destroyed under us: nothing left to put back
				a.T.position = a.Pos; a.T.rotation = a.Rot;
				if (a.Sync != null) { try { if (Core.NativeGuard.Alive(a.Sync)) a.Sync.enabled = a.SyncWas; } catch { } a.Sync = null; }
				if (a.Scale != Vector3.zero) a.T.localScale = a.Scale;
				if (a.Body != null)
				{
					if (a.InterpSet) { try { a.Body.interpolation = a.WasInterp; } catch { } }
					a.Body.isKinematic = a.WasKinematic; a.Body.useGravity = a.WasGravity;
					if (!a.Body.isKinematic) { a.Body.velocity = Vector3.zero; a.Body.angularVelocity = Vector3.zero; }
				}
			}
			catch { }
		}

		// Median world size of the objects (the auto pixel pitch). Pass the transforms, or null for the arranged set.
		private static float MedianSize(List<Transform> objs)
		{
			var sizes = new List<float>();
			if (objs != null) { foreach (var t in objs) { float s = MeasureSize(t); if (s > 0.005f) sizes.Add(s); } }
			else { for (int i = 0; i < _art.Count; i++) if (_art[i] != null && _art[i].Size > 0.005f) sizes.Add(_art[i].Size); }
			if (sizes.Count == 0) return 0.3f;
			sizes.Sort();
			return Mathf.Clamp(sizes[sizes.Count / 2], 0.02f, 10f);
		}

		private static float MeasureSize(Transform t)
		{
			try
			{
				var r = t.GetComponentInChildren<Renderer>();
				if (r != null) { Vector3 b = r.bounds.size; float m = Mathf.Max(b.x, Mathf.Max(b.y, b.z)); if (m > 0.005f) return m; }
			}
			catch { }
			return 0.3f;
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			if (_netMode) LearnBudget();
			// A WORLD CHANGE ENDS THE SHOW — and the soundtrack has to be told. Its AudioSource sits on
			// a DontDestroyOnLoad object precisely so a scene load cannot cut the music mid-show, which
			// means nothing else will stop it: without this you leave the world and Bad Apple follows
			// you into the next one, with no picture and no way to switch it off.
			Core.BadAppleAudio.Stop();
			_baPlaying = false; _restoring = false; _netMode = false; _restoreIdx = 0; NetInfo = "";
			_art.Clear(); _ownQ.Clear();
			if (_marker != null) { try { UnityEngine.Object.Destroy(_marker); } catch { } _marker = null; }
		}

		public override void OnShutdown() { StopBadApple(restore: true); RestoreArt(); }
	}
}
