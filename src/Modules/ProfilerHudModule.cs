using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE PROFILER, WHERE YOU CAN ACTUALLY READ IT.
	//
	// ModuleManager has measured every module's frame cost for a while, but the only way to see the
	// numbers was a panel inside the mod menu — and opening that menu changes what is being measured
	// and stops you playing the scene that was dropping frames. A performance readout you have to
	// stop playing to read cannot answer "why did it drop to 14 fps just now".
	//
	// So the same numbers are drawn on the HUD, in the top-right corner, over the running game. It
	// shows ms PER SECOND OF WALL CLOCK, which is the figure that matters: at 60 fps a frame is
	// 16.6 ms, so a module reading 16.6 ms/s is eating one whole frame every second. Anything in
	// double digits is worth looking at; anything near your frame budget is the answer.
	//
	// Toggle it with RIGHT-SHIFT + P. Turning it on turns the measurement on, and turning it off
	// turns the measurement back off, so it costs nothing when you are not looking at it.
	public class ProfilerHudModule : IModule
	{
		public override string Name => "ProfilerHud";

		private static bool _on;
		public static bool Active => _on;
		public static void Toggle()
		{
			_on = !_on;
			if (_on) { _profilerWasOn = ModuleManager.Profiling; ModuleManager.Profiling = true; }
			else ModuleManager.Profiling = _profilerWasOn;
		}
		private static bool _profilerWasOn;
		private static GUIStyle _mono, _dim, _hot;
		private static float _fps, _worstFrameMs;
		private static float _fpsAt;

		public override void OnInitialize()
			=> VRChatArchiveModPlugin.Logger.LogInfo("[ProfilerHud] ready — Right-Shift+P shows per-module frame cost.");

		public override void OnUpdate()
		{
			try
			{
				if ((Input.GetKey(KeyCode.RightShift) || Input.GetKey(KeyCode.LeftShift))
					&& Input.GetKeyDown(KeyCode.P))
				{
					_on = !_on;
					if (_on) { _profilerWasOn = ModuleManager.Profiling; ModuleManager.Profiling = true; }
					else ModuleManager.Profiling = _profilerWasOn;
				}
				if (!_on) return;

				// Frame time is measured here rather than taken from VaClock.Delta alone, because the
				// worst frame in the last second is what you felt, and the average is what hides it.
				float dt = VaClock.Delta;
				if (dt > 0f) _fps = _fps <= 0f ? 1f / dt : Mathf.Lerp(_fps, 1f / dt, 0.05f);
				float ms = dt * 1000f;
				float now = VaClock.Now;
				if (ms > _worstFrameMs) _worstFrameMs = ms;
				if (now - _fpsAt > 1f) { _fpsAt = now; _worstFrameMs = ms; }
			}
			catch { }
		}

		public override void OnGui()
		{
			try
			{
				if (!_on || Event.current.type != EventType.Repaint) return;
				EnsureStyles();

				var rep = ModuleManager.ProfileReport();
				int rows = Mathf.Min(rep.Count, 12);

				float pad = Hud.S(8f), rowH = Hud.S(15f), colHead = Hud.S(14f);
				float w = Mathf.Min(Screen.width * 0.30f, Hud.S(420f));
				float h = Hud.HeaderH + colHead + Mathf.Max(1, rows) * rowH + pad;
				float margin = Hud.S(16f);
				var panel = new Rect(Screen.width - margin - w, margin, w, h);

				// The budget is what turns a millisecond count into a verdict: 8 ms/s means nothing
				// on its own, and everything next to a 16.6 ms frame.
				float budget = 1000f / Mathf.Max(1f, _fps);
				var body = Hud.Panel(panel, "MOD FRAME COST",
					$"{_fps:F0} fps · {budget:F1} ms/frame · worst {_worstFrameMs:F0} ms");

				float x1 = body.x + Hud.S(4f), x2 = body.xMax - Hud.S(76f), x3 = body.xMax - Hud.S(4f);
				GUI.Label(new Rect(x1, body.y, w, 13f), "MODULE", _dim);
				GUI.Label(new Rect(x2, body.y, Hud.S(72f), 13f), "ms / second", _dim);

				float y = body.y + colHead;
				if (rep.Count == 0)
				{
					GUI.Label(new Rect(x1, y, w, rowH), "measuring… (one second window)", _dim);
					return;
				}

				for (int i = 0; i < rows; i++)
				{
					var kv = rep[i];
					// Red once a module costs more than a fifth of the frame budget: at that point it
					// is not overhead any more, it is the reason the frame is late.
					bool heavy = kv.Value > budget * 0.2f;
					GUI.Label(new Rect(x1, y, w - Hud.S(84f), rowH), kv.Key, heavy ? _hot : _mono);
					GUI.Label(new Rect(x2, y, Hud.S(72f), rowH), kv.Value.ToString("F2"), heavy ? _hot : _mono);
					y += rowH;
				}
			}
			catch { }
		}

		private static void EnsureStyles()
		{
			if (_mono != null) return;
			_mono = Core.GuiCompat.BaseStyle() ?? new GUIStyle();
			try { _mono.fontSize = Mathf.RoundToInt(Hud.S(11f)); } catch { }
			try { _mono.richText = false; } catch { }
			_mono.normal.textColor = new Color(0.86f, 0.90f, 0.96f);
			_dim = new GUIStyle(_mono); _dim.normal.textColor = new Color(0.45f, 0.53f, 0.63f);
			_hot = new GUIStyle(_mono); _hot.normal.textColor = new Color(1f, 0.48f, 0.48f);
		}
	}
}
