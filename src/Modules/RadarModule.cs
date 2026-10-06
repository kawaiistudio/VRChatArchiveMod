using System;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Top-down radar: a circular minimap in the corner with a dot for every player around
	// you, oriented to your view (forward = up), coloured by VRChat trust rank like the ESP.
	//
	// Same nature as ESP — it VISUALISES positions your client already receives via the
	// stable VRChatArchiveMod.Core.VaPlayers.All() SDK API. It never targets, follows, or acts on anyone,
	// hooks nothing, and touches no networking.
	public class RadarModule : IModule
	{
		public override string Name => "Radar";

		private static readonly Color CVisitor  = new Color(0.84f, 0.84f, 0.88f); // #D6D6E0
		private static readonly Color CNewUser  = new Color(0.09f, 0.47f, 1.00f); // #1778FF
		private static readonly Color CUser     = new Color(0.17f, 0.81f, 0.36f); // #2BCF5C
		private static readonly Color CKnown    = new Color(1.00f, 0.48f, 0.26f); // #FF7B42
		private static readonly Color CTrusted  = new Color(0.51f, 0.26f, 0.90f); // #8143E6
		private static readonly Color CNuisance = new Color(0.55f, 0.00f, 0.00f); // #8B0000
		private static readonly Color CVrcTeam  = new Color(1.00f, 0.12f, 0.12f); // #FF1F1F VRChat Team
		private static readonly Color CLocal    = new Color(0.30f, 0.95f, 1.00f);

		private System.Reflection.PropertyInfo _mApiUser;
		private System.Reflection.MethodInfo _tryCastPlayer;
		private Type _playerType;
		private bool _resolved;
		private bool _hotkeyWasDown;
		private float _nextMapAt;

		public override void OnUpdate()
		{
			try
			{
				bool combo = Input.GetKey(KeyCode.RightShift) && Input.GetKey(KeyCode.M);
				if (combo && !_hotkeyWasDown)
					ModConfig.RadarEnabled.Value = !ModConfig.RadarEnabled.Value;
				_hotkeyWasDown = combo;
			}
			catch { }
		}

		// The map camera renders HERE, once per frame at most, instead of inside OnGui where a
		// repaint could fire it several times a frame. OnGui only blits the result.
		public override void OnLateUpdate()
		{
			try
			{
				if (!ModConfig.RadarEnabled.Value || !ModConfig.RadarMap.Value) { RadarMapCamera.Release(); return; }
				var cam = Camera.main;
				if (cam == null) return;
				float range = Mathf.Max(5f, ModConfig.RadarRange.Value);
				// THROTTLE THE MAP RENDER. RadarMapCamera.Tick renders a full second-camera pass of the
				// whole scene; at 30+ players in a heavy world that was ~340 ms/s on its own and a prime
				// suspect in the freeze-then-dropped-to-home disconnects. A top-down minimap does not need
				// a fresh render every frame -- 10 Hz is indistinguishable, and OnGui still BLITS the cached
				// texture every frame so the map itself never flickers.
				// EVERY FRAME (the owner's call, 2026-09-04 evening: "100% fluide, nvm the cost"). The
				// throttle above is history; MapEveryFrames in the config is the only brake left.
				RadarMapCamera.Tick(range, cam);
			}
			catch { }
		}

		private static GUIStyle _posStyle;
		private static void EnsurePosStyle()
		{
			if (_posStyle != null) return;
			_posStyle = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true };
		}

		// Name labels scale with the HUD like everything else, so they stay legible on a 1440p or
		// 4K screen instead of shrinking into the blips.
		private static GUIStyle _nameStyle;
		private static float _nameScale = -1f;
		private static void EnsureNameStyle()
		{
			float sc = Core.Hud.Scale;
			if (_nameStyle != null && Mathf.Abs(sc - _nameScale) < 0.01f) return;
			_nameScale = sc;
			_nameStyle = new GUIStyle
			{
				fontSize = Mathf.Max(9, Mathf.RoundToInt(10f * sc)),
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleLeft,
				clipping = TextClipping.Overflow,
			};
		}

		private static string Short(string s, int n)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Length <= n ? s : s.Substring(0, n - 1) + "\u2026";
		}

		private System.Collections.Generic.List<VRCPlayerApi> _players;
		private float _playersAt;

		public override void OnGui()
		{
			try
			{
				// Radar off means the map camera has no reason to exist: hand the RenderTexture back
				// instead of leaving a second camera and a few MB of VRAM alive for nothing.
				if (!ModConfig.RadarEnabled.Value) { RadarMapCamera.Release(); return; }
				if (Event.current.type != EventType.Repaint) return;

				var cam = Camera.main;
				// CACHED ROSTER. VRChatArchiveMod.Core.VaPlayers.All() marshals a fresh il2cpp list across the
				// interop boundary on every call, and this ran once per repaint — a hundred-plus
				// list rebuilds a second in a 43-player instance, which the profiler charged to the
				// radar at up to 136 ms/s. Who is in the room changes on joins and leaves, not on
				// frames; their POSITIONS are read live below, so the blips stay perfectly smooth.
				float nowT = VaClock.Now;
				if (_players == null || nowT >= _playersAt + 0.5f)
				{
					try { _players = VRChatArchiveMod.Core.VaPlayers.All(); } catch { _players = null; }
					_playersAt = nowT;
				}
				var players = _players;
				if (cam == null || players == null) return;

				// The configured size is a 1080p design value; on a taller screen the map grows with
				// it instead of shrinking into the corner. Also floored well above the old 120px.
				// Fixed proportion of the screen — one interface, no size setting.
				float size = Core.Hud.SideWidth;   // shared with the player list and the instance log
				float range = Mathf.Max(5f, ModConfig.RadarRange.Value);
				float radius = size * 0.5f;
				// BOTTOM-RIGHT corner, MOBA-minimap style. The four-corner HUD gives the top
				// corners to the player list and the instance log.
				float margin = Core.Hud.S(16f);
				var rect = new Rect(Screen.width - size - margin, Screen.height - size - margin - Core.Hud.S(24f), size, size);
				Vector2 center = new Vector2(rect.x + radius, rect.y + radius);

				var body = Core.Hud.Panel(new Rect(rect.x, rect.y - Core.Hud.HeaderH - 4f, rect.width, rect.height + Core.Hud.HeaderH + Core.Hud.S(4f)),
					"RADAR", Mathf.RoundToInt(range) + "m");
				rect = new Rect(body.x, body.y, body.width, body.height);
				center = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
				radius = Mathf.Min(rect.width, rect.height) * 0.5f;
				DrawBackground(rect, center, radius);

				// The world, under everything else. Drawn before the grid and the blips so those
				// stay the loudest things on the radar rather than competing with the scenery.
				//
				// The camera RENDER moved to OnLateUpdate (below): calling _cam.Render() from inside
				// an OnGui repaint meant a full scene render several times per frame, which was ~31
				// ms/s on its own. Here we only BLIT the texture the render already produced.
				// Orient the radar to the camera's horizontal facing (forward = up).
				Vector3 fwd = cam.transform.forward; fwd.y = 0f;
				if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
				fwd.Normalize();
				Vector3 right = new Vector3(fwd.z, 0f, -fwd.x); // 90° clockwise on the horizontal plane

				Vector3 localPos = cam.transform.position;

				if (ModConfig.RadarMap.Value && RadarMapCamera.Texture != null)
				{
					var prev = GUI.color;
					GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(ModConfig.RadarMapOpacity.Value));
					// CONTINUOUS MAP BETWEEN RENDERS. The cached picture was taken at RenderPos covering
					// range x Overscan; every frame it is slid to where RenderPos now sits on the radar,
					// clipped to the panel, so walking never steps. No rotation compensation: GUI clipping
					// does not follow a rotated matrix (the turned picture stuck out of the panel), and at
					// 30 renders/s the heading is at most ~33 ms behind — invisible.
					float ov = RadarMapCamera.Overscan;
					if (RadarMapCamera.HasRender)
					{
						Vector3 me = localPos;
						try { var lt = PlayerRef.LocalTransform(); if (lt != null) me = lt.position; } catch { }
						Vector3 back = RadarMapCamera.RenderPos - me;           // where the picture's centre is now
						float ox = Vector3.Dot(back, right) / range * radius;
						float oz = Vector3.Dot(back, fwd) / range * radius;
						float half = rect.width * 0.5f * ov;
						GUI.BeginGroup(rect);
						GUI.DrawTexture(new Rect(rect.width * 0.5f + ox - half, rect.height * 0.5f - oz - half, half * 2f, half * 2f), RadarMapCamera.Texture, ScaleMode.StretchToFill, false);
						GUI.EndGroup();
					}
					else
					{
						float half = rect.width * 0.5f * ov;
						GUI.BeginGroup(rect);
						GUI.DrawTexture(new Rect(rect.width * 0.5f - half, rect.height * 0.5f - half, half * 2f, half * 2f), RadarMapCamera.Texture, ScaleMode.StretchToFill, false);
						GUI.EndGroup();
					}
					GUI.color = prev;
				}

				ResolveReflection();
				int count = players.Count;
				for (int i = 0; i < count; i++)
				{
					try
					{
						VRCPlayerApi api = players[i];
						if (api == null) continue;
						if (api.isLocal) continue;

						Vector3 delta = api.GetPosition() - localPos;
						float dx = Vector3.Dot(delta, right);
						float dz = Vector3.Dot(delta, fwd);

						// Map metres → radar pixels; clamp to the rim so far players sit on the edge.
						float px = dx / range * radius;
						float py = dz / range * radius;
						float lim = radius - 5f;
						px = Mathf.Clamp(px, -lim, lim);
						py = Mathf.Clamp(py, -lim, lim);

						Vector2 dot = new Vector2(center.x + px, center.y - py); // up = forward
						var apiUser = Core.ApiUsers.Get(api);
						Color col = TrustColor(apiUser);
						Dot(dot, 4.5f, col);

						// Name beside the blip. Skipped for anyone clamped to the rim: those blips are
						// piled on the edge and their labels would overlap into an unreadable smear,
						// which is worse than no label at all.
						if (!ModConfig.RadarNames.Value) continue;
						float rawX = dx / range * radius, rawY = dz / range * radius;
						if (Mathf.Abs(rawX) > lim || Mathf.Abs(rawY) > lim) continue;

						string nm = null;
						try { nm = api.displayName; } catch { }
						if (string.IsNullOrEmpty(nm)) continue;
						nm = Short(nm, 14);

						EnsureNameStyle();
						var nr = new Rect(dot.x + Core.Hud.S(7f), dot.y - Core.Hud.S(7f), Core.Hud.S(120f), Core.Hud.S(14f));
						// Cheap 1px shadow so a name stays readable over a bright radar background.
						_nameStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
						GUI.Label(new Rect(nr.x + 1f, nr.y + 1f, nr.width, nr.height), nm, _nameStyle);
						_nameStyle.normal.textColor = col;
						GUI.Label(nr, nm, _nameStyle);
					}
					catch { }
				}

				// You: a bright dot with a facing tick pointing up (the map is camera-relative).
				GuiKit.Fill(new Rect(center.x - 1f, center.y - 12f, 2f, 12f), CLocal);
				Dot(center, 4f, new Color(0f, 0f, 0f, 0.8f));
				Dot(center, 3f, CLocal);

				// World-position readout, just under the radar.
				EnsurePosStyle();
				var pr = new Rect(rect.x, rect.yMax + 3f, rect.width, 19f);
				GuiKit.RoundedFill(pr, new Color(0.04f, 0.03f, 0.06f, 0.78f), 5f);
				_posStyle.normal.textColor = new Color(0.62f, 0.95f, 0.72f, 1f);
				GUI.Label(pr, $"X {localPos.x:F1}   Y {localPos.y:F1}   Z {localPos.z:F1}", _posStyle);

				GUI.color = Color.white;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Radar] draw threw: {e}");
			}
		}

		// A square MOBA-style minimap: dark board, a grid, a brighter frame, and a centre tick.
		// A square reads distances better than a disc for a top-down map, and it is cheaper —
		// straight quads instead of rounded-rect draws.
		private static void DrawBackground(Rect rect, Vector2 center, float radius)
		{
			GuiKit.RoundedFill(rect, new Color(0.031f, 0.031f, 0.055f, 0.92f), 6f);

			// grid: three lines each way, so relative distance is readable at a glance
			GUI.color = new Color(1f, 1f, 1f, 0.055f);
			for (int i = 1; i <= 3; i++)
			{
				float fx = rect.x + rect.width * (i / 4f);
				float fy = rect.y + rect.height * (i / 4f);
				GUI.DrawTexture(new Rect(fx, rect.y + 4f, 1f, rect.height - 8f), GuiKit.Pixel);
				GUI.DrawTexture(new Rect(rect.x + 4f, fy, rect.width - 8f, 1f), GuiKit.Pixel);
			}

			// frame
			GUI.color = new Color(Core.Hud.Violet.r, Core.Hud.Violet.g, Core.Hud.Violet.b, 0.55f);
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1.5f), GuiKit.Pixel);
			GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1.5f, rect.width, 1.5f), GuiKit.Pixel);
			GUI.DrawTexture(new Rect(rect.x, rect.y, 1.5f, rect.height), GuiKit.Pixel);
			GUI.DrawTexture(new Rect(rect.xMax - 1.5f, rect.y, 1.5f, rect.height), GuiKit.Pixel);
			GUI.color = Color.white;
		}

		private static void Dot(Vector2 c, float r, Color col)
		{
			GuiKit.RoundedFill(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), col, r);
		}

		// --- trust rank (mirrors EspModule) ---

		// CACHED. TrustColor reads up to nine APIUser properties, each an il2cpp call that scans the
		// user's tag list -- for every player, on every repaint. A rank does not change mid-second,
		// so the answer is kept per APIUser (keyed by its native pointer, which is stable for the
		// object's life) and re-read once a second; the table is dropped every 30 s so players who
		// left cannot accumulate. The reads themselves are untouched: same properties, same order.
		private struct TrustEntry { public Color Col; public float At; }
		private static readonly System.Collections.Generic.Dictionary<IntPtr, TrustEntry> TrustCache =
			new System.Collections.Generic.Dictionary<IntPtr, TrustEntry>();
		private static float _trustDropAt;

		private static Color TrustColor(APIUser u)
		{
			try
			{
				if (u == null) return CVisitor;
				IntPtr key = u.Pointer;
				float now = VaClock.Now;
				if (now >= _trustDropAt) { TrustCache.Clear(); _trustDropAt = now + 30f; }
				if (TrustCache.TryGetValue(key, out TrustEntry e) && now - e.At < 1f) return e.Col;
				Color col = TrustColorUncached(u);
				TrustCache[key] = new TrustEntry { Col = col, At = now };
				return col;
			}
			catch { return CVisitor; }
		}

		private static Color TrustColorUncached(APIUser u)
		{
			try
			{
				if (IsVrcTeam(u)) return CVrcTeam;
				if (u.hasVeryNegativeTrustLevel || u.hasNegativeTrustLevel) return CNuisance;
				if (u.hasLegendTrustLevel || u.hasVeteranTrustLevel) return CTrusted;
				if (u.hasTrustedTrustLevel) return CKnown;
				if (u.hasKnownTrustLevel) return CUser;
				if (u.hasBasicTrustLevel) return CNewUser;
				return CVisitor;
			}
			catch { return CVisitor; }
		}

		private static bool IsVrcTeam(APIUser u)
		{
			try { if (u.hasSuperPowers) return true; } catch { }
			try { if ((int)u.developerType >= 2) return true; } catch { }
			return false;
		}

		private void ResolveReflection()
		{
			if (_resolved) return;
			_resolved = true;
			try
			{
				_playerType = System.Reflection.Assembly.Load("Assembly-CSharp").GetType("VRC.Player");
				if (_playerType != null)
				{
					foreach (var p in _playerType.GetProperties())
						if (p.PropertyType == typeof(APIUser)) { _mApiUser = p; break; }
					_tryCastPlayer = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
						.GetMethod("TryCast", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
						?.MakeGenericMethod(_playerType);
				}
			}
			catch { }
		}

		// Kept for callers; the actual resolution + caching lives in Core.ApiUsers so ESP,
		// the radar and the watchlist share one cached lookup instead of three uncached ones.
		private APIUser GetApiUser(VRCPlayerApi api) => Core.ApiUsers.Get(api);
	}
}
