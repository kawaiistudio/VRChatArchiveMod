using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// A SMALL, SHORT-LIVED STATUS PILL AT THE TOP OF THE SCREEN.
	//
	// The TAB menu is sealed, and with it went the only place the mod's status lines were drawn
	// (Menu.DrawStatus, on a tab nothing opens any more). A QuickMenu card or a client command
	// that refused — "no player selected", "this user has tag lock enabled", "favourite saved" —
	// wrote its reason into a string nobody could see, so every refusal looked like a button that
	// did nothing. This is that line, drawn for a few seconds where the user is looking, then gone.
	//
	// Show() only stores the string, so it is safe from ANY thread (ModControlModule applies
	// client commands on a pool continuation). The clock is read on the main thread, in Draw(),
	// the first time the message is painted — Time.* off the main thread is nothing to rely on.
	internal static class Toast
	{
		private const float LifeSeconds = 3f;

		private static readonly object _gate = new object();
		private static string _msg = "";
		private static bool _fresh;      // set by Show(), consumed by the first Draw() that sees it
		private static float _shownAt;
		private static GUIStyle _style;

		internal static void Show(string msg)
		{
			if (string.IsNullOrEmpty(msg)) return;
			lock (_gate) { _msg = msg; _fresh = true; }
		}

		// Called from Menu.Draw() before its Visible gate, i.e. from OnGUI every frame, menu or not.
		internal static void Draw()
		{
			try
			{
				string msg; bool fresh;
				lock (_gate) { msg = _msg; fresh = _fresh; _fresh = false; }
				if (string.IsNullOrEmpty(msg)) return;

				float now = VaClock.Now;
				if (fresh) _shownAt = now;
				float age = now - _shownAt;
				if (age > LifeSeconds)
				{
					// Expired. Clear it unless a newer Show() landed since we copied it out.
					lock (_gate) { if (!_fresh) _msg = ""; }
					return;
				}

				if (_style == null)
					_style = new GUIStyle
					{
						fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
						clipping = TextClipping.Clip, normal = { textColor = new Color(0.92f, 0.94f, 0.97f, 1f) },
					};

				// Same reading of the message as Menu.DrawStatus: the dot says what KIND of line
				// this is, so a success and a refusal are told apart before the words are read.
				Color dot; string text = msg;
				if (msg.StartsWith("✓", StringComparison.Ordinal))
				{ dot = new Color(0.49f, 1f, 0.62f); text = msg.Substring(1).TrimStart(); }
				else if (Mentions(msg, "fail", "error", "unable", "invalid", "could not", "cannot", "gone", "not a usr_", "does not"))
					dot = new Color(1f, 0.45f, 0.45f);
				else if (Mentions(msg, "login required", "lock", "mandatory", "limit", "no player", "select"))
					dot = new Color(1f, 0.72f, 0.32f);
				else
					dot = new Color(0.55f, 0.36f, 0.98f);
				if (text.Length > 110) text = text.Substring(0, 109) + "…";

				// Fade over the last half second, so it leaves rather than blinks off.
				float a = Mathf.Clamp01((LifeSeconds - age) / 0.5f);
				// GUIContent/CalcSize are mis-bound on this build (fatal AV) — size is estimated.
				Vector2 size = Core.GuiCompat.TextSize(text, 14f);
				float h = 30f;
				float w = Mathf.Min(size.x + 44f, Screen.width - 40f);
				// Sits BELOW the BlockAll banner (y 10..56) and the free-cursor hint (y 12..40):
				// those are permanent state and must never be covered by a passing message.
				var r = new Rect((Screen.width - w) / 2f, 64f, w, h);

				GuiKit.RoundedFill(r, new Color(0.10f, 0.07f, 0.16f, 0.90f * a), h * 0.5f);
				GuiKit.RoundedBorder(r, new Color(0f, 0f, 0f, 0f), new Color(0.55f, 0.36f, 0.98f, 0.9f * a), h * 0.5f, 1.5f);
				GuiKit.RoundedFill(new Rect(r.x + 14f, r.y + h * 0.5f - 4f, 8f, 8f), new Color(dot.r, dot.g, dot.b, a), 4f);
				_style.normal.textColor = new Color(0.92f, 0.94f, 0.97f, a);
				GUI.Label(new Rect(r.x + 30f, r.y, r.width - 40f, h), text, _style);
			}
			catch { }
		}

		private static bool Mentions(string s, params string[] needles)
		{
			foreach (string n in needles)
				if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}
	}
}
