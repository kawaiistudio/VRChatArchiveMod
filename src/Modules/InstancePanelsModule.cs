using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Two on-screen panels that flank the screen like the menu wings:
	//   LEFT  — live roster of everyone in the instance.
	//   RIGHT — a join/leave feed (green JOIN, red LEFT), newest at the bottom.
	// Styled to match the desktop client's Instance Log tab (mono font, same badge
	// colours, "instance@vrchat:~$" header, present count).
	//
	// Data comes from VRChatArchiveMod.Core.VaPlayers.All() (stable SDK API — same source as ESP), so it
	// survives VRChat updates. Join/leave are derived by diffing the roster each poll rather
	// than parsing the game log, so the feed is live and needs no file access. This only
	// DISPLAYS who is already in your instance (VRChat shows the same names on nameplates) —
	// it never targets, follows, or acts on anyone.
	//
	// Rendered as our own overlay rather than injected into VRChat's native Wing_Left/
	// Wing_Right: those are obfuscated, VRChat-managed containers rebuilt on every menu
	// navigation, so parenting into them breaks each game build. An anchored overlay is
	// reliable and lands in the same place on screen.
	public class InstancePanelsModule : IModule
	{
		public override string Name => "InstancePanels";

		private enum Kind { Join, Left, World, Avatar, Video }

		private struct Entry
		{
			public string Time;
			public Kind Kind;
			public string Name;
			public string Detail;   // avatar name / video url (Avatar, Video)
		}

		// Client Instance Log palette.
		private static readonly Color CTime  = Hex("#5A6B7A");
		private static readonly Color CJoin  = Hex("#2ED573");
		private static readonly Color CLeft  = Hex("#FF4757");
		private static readonly Color CWorld = Hex("#7FB0FF");
		private static readonly Color CAvatar = Hex("#C58BFF");
		private static readonly Color CVideo = Hex("#FFC24A");
		private static readonly Color CName  = Hex("#EAF6FF");
		private static readonly Color CDim   = Hex("#9FB0D8");
		private static readonly Color CPanel = new Color(0.047f, 0.027f, 0.078f, 0.92f); // #0C0714 @ ~0.92
		private static readonly Color CBar   = new Color(0.055f, 0.082f, 0.071f, 0.85f);
		private static readonly Color CBorder = new Color(0.5f, 0.9f, 0.63f, 0.28f);

		private const int MaxLog = 200;
		private const int PollFrames = 20;

		// key = user id (stable across renames); value = (display name, current avatar name)
		private readonly Dictionary<string, (string Name, string Avatar)> _present =
			new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
		// left panel rows: display label + trust-rank colour
		// Colour is resolved ONCE here, not re-parsed from hex per row per frame (that ran
		// ColorUtility.TryParseHtmlString ~40 times every frame just to draw the list).
		// Tags is no longer a tag LIST: the roster showed every tag a player carried, which on a
		// tagged member ran to three entries plus "+1" and buried the name it belonged to. The only
		// thing worth a line here is whether they are one of ours.
		private static readonly Color CMaster = new Color(1.00f, 0.82f, 0.29f);   // crown gold
		private string _ownerName = "";     // instance creator, shown on the panel's top line
		private static readonly Color CPos = new Color(0.42f, 0.74f, 0.60f);      // muted green, like the radar readout

		// Being a VRChat Archive member is a FLAG, not text. It used to print
		// "VRCHAT ARCHIVE MEMBER" on a second line under the name, which cost a whole row per member
		// to say something the name itself can say: members get a rainbow name and the Archive logo.
		private readonly List<(string Label, Color Col, string Badge, string Pid, bool Member, string Pos, bool Master)> _roster
			= new List<(string, Color, string, string, bool, string, bool)>();
		private readonly List<Entry> _log = new List<Entry>();
		private int _frame;
		private bool _primed;                 // first poll seeds the roster without spamming joins
		private bool _hotkeyWasDown;

		// video-player log hook: Unity's log callback fires off-thread, so entries are queued
		// and drained on the main thread. Passive listener — hooks nothing (see LogCapture).
		private Action<string, string, LogType> _logHandler;
		// Composed column strings and the signature they were built for. See the row loop.
		private string[] _colLeft, _colRight, _colPos;
		private int _colSig = -1;
		private int _rosterVer;
		private static float _diagNext;   // DIAGNOSTIC: throttle for the empty-platform log

		private readonly ConcurrentQueue<string> _videoUrls = new ConcurrentQueue<string>();
		private string _lastVideoUrl;
		private static readonly Regex VideoLine = new Regex(@"\[(Video Playback|USharpVideo|ProTV|AVProVideo)\]", RegexOptions.IgnoreCase);
		private static readonly Regex UrlInLine = new Regex(@"https?://[^\s'""]+", RegexOptions.IgnoreCase);

		// join notifier toasts (transient, drawn top-centre)
		private struct Toast { public string Text; public Color Color; public float Until; }
		private readonly List<Toast> _toasts = new List<Toast>();
		private GUIStyle _toastStyle;

		private GUIStyle _title, _mono, _monoBold, _monoRight, _dim;
		private bool _styles;

		public override void OnInitialize()
		{
			VRChatArchiveModPlugin.Logger.LogInfo("[InstancePanels] ready — Right-Shift+L toggles the player list / instance log.");
			try
			{
				// Log listener for the video-player URLs the game prints. Through Core.UnityLog, which
				// hooks Application.CallLogCallback, so no il2cpp delegate is created -- asking for one
				// is what kept this feature dark on 1903.
				_logHandler = OnLog;
				Core.UnityLog.Subscribe(_logHandler, "InstancePanels");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[InstancePanels] video log hook failed: {Core.Unwrap.Describe(e)}"); }
		}

		public override void OnShutdown()
		{
			try { Core.UnityLog.Unsubscribe(_logHandler); } catch { }
			_logHandler = null;
		}

		// Off-thread Unity log callback: pull the URL out of a video-player line, dedup, queue it.
		//
		// GATED, AND BOUNDED (2026-09-13). This runs for EVERY Unity log line, on the log thread, and
		// the hook is installed at OnInitialize for the whole session. Two problems, both fixed here
		// rather than by add/remove-ing the callback at runtime (installing an il2cpp delegate is the
		// risky operation, doing it repeatedly on a toggle edge more so):
		//   1. The regex ran even with both panels off. The switch is now the first thing tested, so
		//      an inactive feature costs one bool read per log line instead of a regex match.
		//   2. The drain in OnUpdate sits BELOW the enable check, so with the panels off nobody
		//      emptied this queue and it grew without bound for the whole session. It cannot grow
		//      now — nothing is enqueued while off — and OnUpdate also clears it on the off path.
		// Note this is the LOG thread: ModConfig reads are plain managed dictionary lookups, which is
		// safe here, and no Unity object is touched.
		private void OnLog(string condition, string stackTrace, LogType type)
		{
			try
			{
				bool wanted = false;
				try { wanted = ModConfig.InstancePanelsEnabled.Value || ModConfig.JoinNotifierEnabled.Value; } catch { }
				if (!wanted) return;
				if (string.IsNullOrEmpty(condition) || !VideoLine.IsMatch(condition)) return;
				var m = UrlInLine.Match(condition);
				if (!m.Success) return;
				_videoUrls.Enqueue(m.Value);
			}
			catch { }
		}

		public override void OnUpdate()
		{
			try
			{
				// Right-Shift + L toggles the panels.
				bool combo = Input.GetKey(KeyCode.RightShift) && Input.GetKey(KeyCode.L);
				if (combo && !_hotkeyWasDown)
					ModConfig.InstancePanelsEnabled.Value = !ModConfig.InstancePanelsEnabled.Value;
				_hotkeyWasDown = combo;

				// Poll runs if EITHER the panels OR the join notifier is on (both need the diff).
				if (!ModConfig.InstancePanelsEnabled.Value && !ModConfig.JoinNotifierEnabled.Value)
				{
					// Anything the log hook queued in the same frame the switch went off is dropped
					// here, so nothing is left waiting to be replayed when the panels come back.
					while (_videoUrls.TryDequeue(out _)) { }
					return;
				}

				// Drain queued video URLs (from the off-thread log hook) on the main thread.
				while (_videoUrls.TryDequeue(out string url))
				{
					if (url == _lastVideoUrl) continue;   // same URL logged several times
					_lastVideoUrl = url;
					Add(Kind.Video, "video", url);
				}

				if (++_frame < PollFrames) return;
				_frame = 0;
				Poll();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[InstancePanels] update threw: {e}");
			}
		}

		// Keyed by user id (stable across renames) so we can also detect avatar changes.
		// Enumerated through the same VRC.Player reflection path FewTags/VaTags use, which
		// exposes the display name AND the current avatar name per player.
		// SKIP THE REBUILD WHEN NOTHING CHANGED (2026-09-13). Poll() re-formats every row — rich-text
		// badges, position strings, member lookups, two fresh collections — every 20 frames, but the
		// roster it reads only refreshes every 60 frames: two polls in three rebuilt identical data
		// (22 ms/s in the profiler, plus the GC churn of the allocations). The roster's version and
		// the blocked-set size are the only inputs that can change between refreshes; when neither
		// has, the previous rows are still exactly right. The join/leave diff is safe to skip too: an
		// unchanged roster means nobody joined or left. Video URLs are drained before this in
		// OnUpdate and are unaffected.
		private static int _lastRosterVersion = -1;
		private static int _lastBlockedCount = -1;

		private void Poll()
		{
			int rv = VaTagsModule.RosterVersion;
			int bc = 0;
			try { bc = BlockedByProbeModule.BlockedMe.Count; } catch { }
			if (rv == _lastRosterVersion && bc == _lastBlockedCount) return;
			_lastRosterVersion = rv;
			_lastBlockedCount = bc;

			var current = new Dictionary<string, (string Name, string Avatar)>(StringComparer.OrdinalIgnoreCase);
			var order = new List<(string Label, Color Col, string Badge, string Pid, bool Member, string Pos, bool Master)>();
			_ownerName = "";
			var diagNoPlat = new System.Collections.Generic.List<string>();   // DIAGNOSTIC

			// READ THE SHARED ROSTER, don't rebuild it.
			//
			// This used to re-enumerate every player and re-read the display name, badges, user id
			// and actor number by reflection — the exact work VaTags already does and now caches —
			// AND ran a nested loop over VaTagsModule.Roster for each of them, which is O(n²) on the
			// player count. On a 34-player instance that was InstancePanels' 250 ms/s. VaTags'
			// Roster already holds all of this per player; here we just format it.
			try
			{
				foreach (var pe in VaTagsModule.Roster)
				{
					try
					{
						if (pe == null || string.IsNullOrEmpty(pe.Name)) continue;
						string uid = string.IsNullOrEmpty(pe.UserId) ? "name:" + pe.Name : pe.UserId;

						string badge = "";
						if (pe.Plus)  badge += "<color=#FFD24A>VRC+</color> ";
						if (pe.Adult) badge += "<color=#FF7AB8>18+</color> ";
						if (!string.IsNullOrEmpty(pe.Platform))
							badge += pe.Platform == "Quest" ? "<color=#7CFF9E>Q</color>"
							       : pe.Platform == "PC"    ? "<color=#7FB0FF>PC</color>"
							       : "<color=#8FA3B8>" + Trunc(pe.Platform, 3) + "</color>";
							if (pe.VrKnown && pe.InVR) badge += " <color=#8FE9A8>VR</color>";

						string pid = pe.PlayerId >= 0 ? pe.PlayerId.ToString() : "";
						bool member = false;
						try { member = VaTagsModule.IsMember(uid); } catch { }

						string posText = "";
						if (pe.HasPos)
						{
							Vector3 pv = pe.Pos;
							posText = $"{pv.x:F1} {pv.y:F1} {pv.z:F1}";
						}

						// DIAGNOSTIC: the overlay and the client read the SAME roster, so a player the
						// client shows with a platform but the overlay does not must have an empty
						// pe.Platform here at read time. Record who, log it throttled below.
						if (string.IsNullOrEmpty(pe.Platform)) diagNoPlat.Add(pe.Name ?? "?");

						if (pe.IsOwner) _ownerName = pe.Name;

						// BLOCKED goes in FRONT OF THE NAME, not in the badge column: that column is
						// sized to the character for "VRC+ 18+ PC" (badgeW = fs * 6.9f), and a seven
						// letter word would spill left over the name of every row that has one.
						string label = pe.IsLocal ? pe.Name + "  (you)" : pe.Name;
						// The custom username, beside the real one on your own row — the same thing the
						// wing PLAYERS panel shows. pe.Name is APIUser.displayName and stays the truth;
						// this is what the world's Udon scripts are told instead.
						try
						{
							if (pe.IsLocal && !string.IsNullOrEmpty(SpoofModule.Applied))
								label += " → " + SpoofModule.Applied;
						}
						catch { }
						// Blocked — ALWAYS shown, never behind a switch. The marker lives in the BADGE column
						// on the right with VRC+/18+/PC, NOT as a "[B] " prefix on the name: the prefix shoved
						// the name of every marked row sideways and read as part of it. The badge column is
						// its own rich-text run, so the red belongs to the tag alone (the name column is one
						// truncated single-<color> run, which is why an inline tag could never go there).
						// The whole row still turns red, which is what makes a blocked line findable at a glance.
						// BOTH DIRECTIONS, told apart. [B] red = they blocked YOU; [b] amber = you blocked
						// them. The row turns red only for the first — being blocked BY someone is the one
						// worth spotting across the panel; a person you blocked yourself is no surprise to you.
						bool blockedByThem = false, iBlockedThem = false;
						try
						{
							if (!pe.IsLocal)
							{
								blockedByThem = BlockedByProbeModule.BlockedMe.Contains(pe.UserId ?? "");
								iBlockedThem = BlockedByProbeModule.IBlocked.Contains(pe.UserId ?? "");
							}
						}
						catch { }
						if (iBlockedThem) badge = "<color=#FFC800>[b]</color> " + badge;
						if (blockedByThem) badge = "<color=#FF4B4B>[B]</color> " + badge;

						order.Add((label, blockedByThem ? new Color(1f, 0.29f, 0.29f) : Hex(pe.TrustColor), badge, pid, member, posText, pe.IsMaster));
						if (!pe.IsLocal) current[uid] = (pe.Name, pe.AvatarName ?? "");
					}
					catch { }
				}
			}
			catch { return; }

			if (order.Count == 0) return;   // you are always enumerable → empty = transient glitch

			// DIAGNOSTIC (temporary): who the overlay sees with no platform, at most every 8s.
			if (diagNoPlat.Count > 0)
			{
				float now = VaClock.Now;
				if (now >= _diagNext)
				{
					_diagNext = now + 8f;
					int take = Mathf.Min(14, diagNoPlat.Count);
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[InstancePanels] DIAG " + diagNoPlat.Count + "/" + order.Count + " players have NO platform in the roster the overlay reads: "
						+ string.Join(", ", diagNoPlat.GetRange(0, take)) + (diagNoPlat.Count > take ? ", \u2026" : ""));
				}
			}

			// World-change heuristic: previous and new REMOTE rosters share nobody.
			int overlap = 0;
			foreach (var k in current.Keys) if (_present.ContainsKey(k)) overlap++;
			bool worldChange = _primed && overlap == 0 && _present.Count >= 2 && current.Count >= 1;

			if (worldChange)
			{
				// Silent reset: clear the feed, drop a divider, adopt the new instance as
				// the baseline WITHOUT emitting a join for everyone already in it.
				_log.Clear();
				Add(Kind.World, "— new instance —");
			}
			else if (!_primed)
			{
				_primed = true;   // first poll: seed silently
			}
			else
			{
				foreach (var kv in current)
				{
					if (!_present.TryGetValue(kv.Key, out var prev)) { Add(Kind.Join, kv.Value.Name); continue; }
					// same player still here → did their avatar change?
					if (!string.IsNullOrEmpty(kv.Value.Avatar) && !string.IsNullOrEmpty(prev.Avatar)
						&& !string.Equals(kv.Value.Avatar, prev.Avatar, StringComparison.Ordinal))
						Add(Kind.Avatar, kv.Value.Name, kv.Value.Avatar);
				}
				foreach (var kv in _present)
					if (!current.ContainsKey(kv.Key)) Add(Kind.Left, kv.Value.Name);
			}

			_present.Clear();
			foreach (var kv in current) _present[kv.Key] = kv.Value;

			order.Sort((x, y) => string.Compare(x.Label, y.Label, StringComparison.OrdinalIgnoreCase));
			_roster.Clear();
			_roster.AddRange(order);
			_rosterVer++;      // invalidates the composed column strings
		}

		private static string ResolveAvatarName(object player)
		{
			try
			{
				object av = FewTagsModule.GetMemberByTypeName(player, "ApiAvatar", "prop_ApiAvatar_0", "field_Private_ApiAvatar_0");
				if (av == null)
				{
					object vp = FewTagsModule.GetMemberByTypeName(player, "VRCPlayer", "_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
					av = FewTagsModule.GetMemberByTypeName(vp, "ApiAvatar", "prop_ApiAvatar_0", "field_Private_ApiAvatar_0");
				}
				return FewTagsModule.GetMember(av, "name") as string;
			}
			catch { return null; }
		}

		// THE SAME EVENTS, READABLE FROM OUTSIDE.
		//
		// _log is this module's private list and is drawn in IMGUI. The wing panel wants the identical
		// feed rendered as real UI text in VRChat's own right wing, so the events are also kept here as
		// finished rich-text lines. A string list, not the Entry type: the wing has no business knowing
		// how this module models an event, and one formatting decision in one place means the wing and
		// the IMGUI panel can never disagree about what happened.
		public static readonly List<string> Feed = new List<string>();

		// …and the same events again, split into their parts. The single pre-formatted string above is
		// right for a one-label row, but the side panel lays its rows out in REAL columns, and a column
		// layout cannot align text that has already been glued together with double spaces. Both lists
		// are appended in the same place from the same values, so they still cannot disagree.
		public struct FeedRow
		{
			public string Time;    // HH:mm
			public string Badge;   // JOIN / LEFT / WORLD / AVATAR / VIDEO
			public string Color;   // that badge's colour, "#RRGGBB"
			public string Name;
		}

		public static readonly List<FeedRow> FeedRows = new List<FeedRow>();

		private static string FeedColor(Kind k)
		{
			switch (k)
			{
				case Kind.Join: return "#2ED573";
				case Kind.Left: return "#FF6B81";
				case Kind.World: return "#7FB6FF";
				case Kind.Avatar: return "#C39BFF";
				default: return "#FFC08A";
			}
		}

		private static string FeedBadge(Kind k)
		{
			switch (k)
			{
				case Kind.Join: return "JOIN";
				case Kind.Left: return "LEFT";
				case Kind.World: return "WORLD";
				case Kind.Avatar: return "AVATAR";
				default: return "VIDEO";
			}
		}

		private void Add(Kind kind, string name, string detail = null)
		{
			_log.Add(new Entry { Time = DateTime.Now.ToString("HH:mm:ss"), Kind = kind, Name = name, Detail = detail });
			if (_log.Count > MaxLog) _log.RemoveAt(0);

			try
			{
				string hm = DateTime.Now.ToString("HH:mm");
				Feed.Add("<color=#8FA3B8>" + hm + "</color>  <color="
					+ FeedColor(kind) + ">" + FeedBadge(kind) + "</color>  " + Trunc(name ?? "", 40));
				if (Feed.Count > MaxLog) Feed.RemoveAt(0);

				FeedRows.Add(new FeedRow
				{
					Time = hm,
					Badge = FeedBadge(kind),
					Color = FeedColor(kind),
					Name = Trunc(name ?? "", 40),
				});
				if (FeedRows.Count > MaxLog) FeedRows.RemoveAt(0);
			}
			catch { }

			// Join notifier toast (independent of the panel visibility).
			if (ModConfig.JoinNotifierEnabled.Value)
			{
				if (kind == Kind.Join)
					PushToast(name + "  joined", CJoin);
				else if (kind == Kind.Left && ModConfig.JoinNotifierShowLeave.Value)
					PushToast(name + "  left", CLeft);
			}
		}

		// Names can be 32-character hashes; clamp here so the pill never has to.
		private void PushToast(string text, Color color)
		{
			_toasts.Add(new Toast { Text = Trunc(text, 46), Color = color, Until = VaClock.Now + 4f });
			if (_toasts.Count > 6) _toasts.RemoveAt(0);
		}

		private void DrawToasts()
		{
			if (_toasts.Count == 0) return;
			float now = VaClock.Now;
			_toasts.RemoveAll(t => now > t.Until);
			if (_toasts.Count == 0) return;

			if (_toastStyle == null)
				_toastStyle = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleCenter };

			// BOTTOM of the screen: at the top they sat right over the nameplates of the very
			// people they were announcing. Newest at the bottom, older ones pushed upward.
			const float h = 34f, gapY = 6f, padX = 18f, dot = 8f;
			const float minW = 170f, maxW = 460f;
			float baseY = Screen.height * 0.86f;

			for (int i = 0; i < _toasts.Count; i++)
			{
				var t = _toasts[_toasts.Count - 1 - i];
				float life = Mathf.Clamp01(t.Until - now);
				float alpha = Mathf.Min(1f, life * 2f);

				// Width follows the TEXT. A fixed pill left a short name swimming in empty space
				// and squeezed a 32-character name against the edges.
				float textW;
				// GUIContent/CalcSize are mis-bound on this build (fatal AV) — width is estimated.
				textW = Core.GuiCompat.TextWidth(t.Text, 14f);
				if (textW <= 0f) textW = t.Text.Length * 8f;
				float w = Mathf.Clamp(textW + padX * 2f + dot + 10f, minW, maxW);

				var r = new Rect((Screen.width - w) * 0.5f, baseY - i * (h + gapY), w, h);
				float rad = h * 0.5f;

				GuiKit.RoundedFill(new Rect(r.x - 1f, r.y + 1.5f, r.width + 2f, r.height + 2f),
					new Color(0f, 0f, 0f, 0.35f * alpha), rad);
				GuiKit.RoundedFill(r, new Color(Hud.Body.r, Hud.Body.g, Hud.Body.b, 0.94f * alpha), rad);

				// status dot, vertically centred, then the text centred in the space left of it
				GuiKit.RoundedFill(new Rect(r.x + padX - 4f, r.y + h * 0.5f - dot * 0.5f, dot, dot),
					new Color(t.Color.r, t.Color.g, t.Color.b, alpha), dot * 0.5f);

				_toastStyle.normal.textColor = new Color(Hud.Text.r, Hud.Text.g, Hud.Text.b, alpha);
				GUI.Label(new Rect(r.x + padX + dot + 4f, r.y, r.width - (padX + dot + 4f) - padX, r.height),
					t.Text, _toastStyle);
			}
			GUI.color = Color.white;
		}

		public override void OnGui()
		{
			try
			{
				if (Event.current.type != EventType.Repaint) return;

				EnsureStyles();

				// Join notifier toasts draw even when the panel is hidden.
				if (ModConfig.JoinNotifierEnabled.Value) DrawToasts();

				if (!ModConfig.InstancePanelsEnabled.Value) return;

				// Four-corner HUD. Each panel owns one corner and grows from it, so nothing
				// overlaps and the middle of the screen stays clear:
				//   top-left  players   |   top-right  instance log
				//   bottom-left events  |   bottom-right radar
				// The panels are anchored at the TOP here and size themselves to their content
				// (see DrawRoster), rather than starting a third of the way down as before.
				float s = 1f;
				float margin = Hud.S(16f);
				// ONE interface: no per-panel knobs. The only thing that varies is the screen, and
				// Hud.Scale already handles that — the panels keep the same on-screen proportion at
				// 1080p, 1440p and 4K without anybody configuring anything.
				float baseW = Hud.SideWidth;   // same width as the radar — see Hud.SideWidth
				float top = margin;
				// Leave the bottom half free for the events logger and the radar.
				float h = Screen.height * 0.60f;

				DrawRoster(new Rect(margin, top, baseW, h));
				DrawLog(new Rect(Mathf.Max(margin, Screen.width - baseW - margin), top, baseW, h));

				GUI.color = Color.white;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[InstancePanels] draw threw: {e}");
			}
		}

		// Text drawn twice: a near-black copy offset by a pixel, then the real one on top.
		// Without it, light-coloured names vanish over bright worlds now that the dark
		// panel background is gone.
		private void Shadowed(Rect r, string text, GUIStyle style, Color color)
		{
			var prev = style.normal.textColor;
			style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
			GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, style);
			style.normal.textColor = color;
			GUI.Label(r, text, style);
			style.normal.textColor = prev;
		}

		// Same as Shadowed() minus the second pass. The shadow exists so light names stay legible
		// over bright worlds; the timestamp and the 4-letter badge are short, already strongly
		// coloured and never the readability problem — so they do not need to pay for a second
		// full text layout on every one of ~40 log rows, every frame.
		// Width of one monospace glyph in _mono at the current size. CalcSize is not free, so the
		// answer is cached and only re-measured when the font size actually changes.
		private float _monoCharW;
		private float _monoCharAt = -1f;

		private float MonoCharW(float fs)
		{
			if (_monoCharAt == fs && _monoCharW > 0f) return _monoCharW;
			// Measured from the font size, never through GUIContent/CalcSize: both are mis-bound on
			// this build and calling them is a fatal access violation (see GuiCompat.TextWidth).
			float w = Core.GuiCompat.TextWidth("0", fs);
			if (w <= 0.1f) w = fs * 0.54f;
			_monoCharW = w;
			_monoCharAt = fs;
			return w;
		}

		private void Plain(Rect r, string text, GUIStyle style, Color color)
		{
			// No save/restore: every GUI.Label drawn with these styles goes through Plain() or
			// Shadowed(), which set the colour first, so restoring the previous one was two extra
			// style.normal proxy fetches and two textColor calls per label per frame that nothing
			// ever read back.
			style.normal.textColor = color;
			GUI.Label(r, text, style);
		}

		// Same layout as the PLAYERS tab in the mod menu: [id] name in trust colour, badges on the
		// right, tags underneath. An opaque panel behind it means no shadow pass is needed, so this
		// richer list actually costs LESS text layout than the old flat one did.
		// Rows are VARIABLE height: a player with no tags takes one line, not two. In a 42-player
		// instance the old fixed two-line row spent half the panel printing "no tags" and only
		// ~16 people fitted; skipping that line roughly doubles what you can see.
		// Rows are VARIABLE height: a player with no tags takes one line, not two. In a 42-player
		// instance the old fixed two-line row spent half the panel printing "no tags".
		// All chrome comes from Core.Hud so this panel is identical to the other three.
		private void DrawRoster(Rect r)
		{
			if (_roster.Count == 0) return;

			float s = 1f;
			float baseFs = Mathf.Max(9f, _mono != null ? _mono.fontSize : 13f * s);

			// The owner line, when we know who it is, costs a row before anyone is listed.
			bool ownerLine = !string.IsNullOrEmpty(_ownerName);

			// FIT THE WHOLE INSTANCE. A 41-player room does not fit one column at reading size and
			// never will, so the panel adapts instead of truncating: shrink the rows first, and take
			// a second column only when shrinking alone is not enough. Truncating with "+6 more" was
			// the one outcome that made the list useless — the missing six are exactly the people
			// you were looking for.
			int want = _roster.Count;
			float fs = baseFs, lh = baseFs * 1.32f, headRow = baseFs * 1.22f;
			int cols = 1, perCol = 0;

			// READABILITY BEFORE COLUMN COUNT. The old loop shrank the font all the way to its 8px
			// floor while still on ONE column, and 69 names DO fit at 8px — so the second column was
			// never reached and the roster came out as a single unreadable ribbon. A second column
			// costs nothing but width, which the panel has; illegible text costs the whole feature.
			// So: try each column count at a size a person can actually read, and only fall back to
			// shrinking past that when even two columns cannot hold the room.
			const float ReadableFs = 11f;
			bool placed = false;

			for (int c = 1; c <= 2 && !placed; c++)
			{
				for (float t = baseFs; t >= ReadableFs; t -= 0.5f)
				{
					float tl = t * 1.32f;
					float th = t * 1.22f;
					float tAvail = r.height - Hud.HeaderH - 12f - (ownerLine ? tl : 0f) - th;
					int fit = Mathf.Max(0, (int)(tAvail / tl));
					if (fit * c >= want)
					{
						cols = c; fs = t; lh = tl; headRow = th; perCol = fit;
						placed = true;
						break;
					}
				}
			}

			// Last resort: two columns, shrinking as far as the old floor allowed.
			if (!placed)
			{
				cols = 2;
				for (float t = baseFs; t >= 8f; t -= 0.5f)
				{
					float tl = t * 1.32f;
					float th = t * 1.22f;
					float tAvail = r.height - Hud.HeaderH - 12f - (ownerLine ? tl : 0f) - th;
					int fit = Mathf.Max(0, (int)(tAvail / tl));
					fs = t; lh = tl; headRow = th; perCol = fit;
					if (fit * cols >= want) break;
				}
			}
			if (perCol <= 0) return;

			// THE STYLE HAS TO ACTUALLY USE THE SIZE WE FITTED FOR.
			//
			// The loop above picks `fs` and every rect is measured from it — but the GUIStyles kept
			// the fixed 13 px they were created with, so the text was drawn at one size inside boxes
			// laid out for another. That is why a shrunk list overlapped itself instead of getting
			// smaller, and why the padded id column did not line up with the NAME heading.
			int fsi = Mathf.Max(8, Mathf.RoundToInt(fs));
			try
			{
				if (_mono != null) _mono.fontSize = fsi;
				if (_monoBold != null) _monoBold.fontSize = fsi;
				if (_monoRight != null) _monoRight.fontSize = Mathf.Max(8, fsi - 2);
				if (_dim != null) _dim.fontSize = Mathf.Max(8, fsi - 1);
			}
			catch { }
			_monoCharAt = -1f;   // re-measure the glyph advance at the new size

			// A MULTI-LINE LABEL USES THE FONT'S OWN PITCH, so the row height has to BE that pitch
			// or the stripes and the logos drift away from the text they belong to. Read it back and
			// re-fit the row count to it.
			try
			{
				float real = _mono != null ? _mono.lineHeight : 0f;
				if (real > 1f)
				{
					lh = real;
					float avail = r.height - Hud.HeaderH - 12f - (ownerLine ? lh : 0f) - headRow;
					perCol = Mathf.Max(1, (int)(avail / lh));
				}
			}
			catch { }

			int shown = Mathf.Min(want, perCol * cols);
			int rows = Mathf.Min(perCol, Mathf.CeilToInt(shown / (float)cols));
			float used = rows * lh + headRow + (ownerLine ? lh : 0f);

			var panel = new Rect(r.x, r.y, r.width, Hud.HeaderH + used + 10f);
			var body = Hud.Panel(panel, "PLAYERS",
				shown < want ? shown + "/" + want : want.ToString());

			// Each column gets its own slice of the width, and every column offset below is derived
			// from it — the two-column case is the one-column case with a narrower body.
			float colGap = 10f;
			float colW = (body.width - colGap * (cols - 1)) / cols;

			// Sized for the widest badge the column can hold: "VRC+ 18+ PC".
			// The id column has to FIT "[34071]" in a monospace face — seven glyphs at roughly
			// 0.6 em each — plus a gap before the name. It was 3.4 em, so a five-digit id ran
			// straight into the name and the two columns read as one string.
			// 6.9 em fitted "VRC+ 18+ PC" exactly; the blocked tag now rides in the same column, so the
			// widest line is "[B] VRC+ 18+ PC" and the column has to grow with it or the leftmost tag
			// spills over the name of every blocked row.
			float badgeW = fs * 9.1f, pidW = fs * 5.1f;
			bool anyPos = false;
			try { anyPos = ModConfig.RosterPositions.Value; } catch { }
			// The position column is the first thing to go when space runs out: two columns of names
			// beats one column of names and coordinates.
			float posColW = (anyPos && cols == 1) ? fs * 7.6f : 0f;
			int maxChars = Mathf.Max(6, (int)((colW - 12f - pidW - badgeW - posColW) / (fs * 0.54f)));

			float headY = body.y;

			// Column headings per column, so [3004] and "9.9 -3.2 -0.7" are not left to be guessed at.
			for (int c = 0; c < cols; c++)
			{
				float cx = body.x + c * (colW + colGap);
				Plain(new Rect(cx + 6f, headY, pidW - 4f, headRow), "ID", _dim, Hud.Dim);
				Plain(new Rect(cx + 6f + pidW + fs * 0.4f, headY, colW - 12f - pidW - badgeW, headRow), "NAME", _dim, Hud.Dim);
				if (posColW > 0f)
					Plain(new Rect(cx + colW - badgeW - posColW - 12f, headY, posColW, headRow), "X Y Z", _monoRight, Hud.Dim);
				Plain(new Rect(cx + colW - badgeW - 4f, headY, badgeW, headRow), "PLATFORM", _monoRight, Hud.Dim);
				GuiKit.Fill(new Rect(cx + 4f, headY + headRow - 1f, colW - 8f, 1f), new Color(1f, 1f, 1f, 0.10f));
			}
			float y = headY + headRow;

			// Who owns this instance, stated once at the top instead of leaving the reader to hunt
			// for a crown somewhere down a 43-name list.
			if (ownerLine)
			{
				Plain(new Rect(body.x + 6f, y, fs * 5.2f, lh), "OWNER", _mono, CMaster);
				Plain(new Rect(body.x + 6f + fs * 5.4f, y, body.width - 12f - fs * 5.4f, lh),
					Trunc(_ownerName, Mathf.Max(maxChars, 24)), _mono, Hud.Text);
				GuiKit.Fill(new Rect(body.x + 4f, y + lh - 1f, body.width - 8f, 1f), new Color(1f, 1f, 1f, 0.07f));
				y += lh;
			}

			float rowsTop = y;

			// ONE LABEL PER COLUMN, NOT PER ROW.
			//
			// Immediate mode caches nothing: every GUI.Label is a full text layout, every frame. At
			// three labels a row and forty players that is a hundred and twenty layouts per frame,
			// and the profiler charged this module 251 ms/s for them. No amount of tightening the
			// row loop changes that — the call COUNT is the cost.
			//
			// The list is monospace with a fixed pitch, so a column of rows is just a multi-line
			// string: the lines land exactly where the per-row rects used to put them, and rich text
			// keeps every part its own colour. Three labels per COLUMN replace three per ROW.
			//
			// The strings are rebuilt only when something in them changes — the roster, the layout,
			// or the rainbow step — so a still list costs nothing but the draw.
			// The slot only matters when a rainbow name is IN the bulk string, i.e. when there is no
			// logo and members are not diverted to the per-row pass (line 663). With the logo present
			// the bulk strings never contain a rainbow, so ticking the signature 12x/s only rebuilt
			// byte-identical strings (and the undrawn _colRight/_colPos) twelve times a second.
			int rainbowSlot = Core.AssetLoader.ArchiveLogo != null ? 0 : (int)(VaClock.Now * 12f);
			int sig = _rosterVer * 397 ^ shown * 31 ^ cols * 17 ^ Mathf.RoundToInt(fs * 4f) ^ rainbowSlot;
			if (sig != _colSig || _colLeft == null || _colLeft.Length != cols)
			{
				_colSig = sig;
				_colLeft = new string[cols];
				_colRight = new string[cols];
				_colPos = new string[cols];

				float charW0 = MonoCharW(fs);
				var sbL = new System.Text.StringBuilder(2048);
				var sbR = new System.Text.StringBuilder(512);
				var sbP = new System.Text.StringBuilder(512);

				for (int c = 0; c < cols; c++)
				{
					sbL.Length = 0; sbR.Length = 0; sbP.Length = 0;
					float cx0 = body.x + c * (colW + colGap);
					float nameX0 = cx0 + 6f + pidW + fs * 0.4f;
					float nameW0 = colW - 12f - pidW - badgeW;
					int idCells0 = Mathf.Max(0, Mathf.RoundToInt((nameX0 - (cx0 + 6f)) / charW0));

					for (int rIdx = 0; rIdx < rows; rIdx++)
					{
						int i = c * rows + rIdx;
						if (rIdx > 0) { sbL.Append('\n'); sbR.Append('\n'); sbP.Append('\n'); }
						if (i >= shown) continue;
						var e0 = _roster[i];

						// MEMBERS ARE DRAWN PER ROW, not in this bulk string.
						//
						// The bulk string only lines up if characters sit on a grid — true for a
						// MONOSPACE font, but the roster font is proportional, so a logo and a
						// rainbow name cannot be placed by counting cells (which is why the logo kept
						// landing inside the name). Members are few, so their whole row is drawn
						// separately below with a measured logo position. Their badge and position
						// still ride the bulk columns, so those stay aligned.
						if (e0.Member && Core.AssetLoader.ArchiveLogo != null)
						{
							// Member rows are drawn ENTIRELY per-row below (name, badge AND position),
							// so their lines here stay blank in all three columns. Splitting a member
							// row between the bulk columns and the per-row pass is what left Faker and
							// COCO with a name but no badge or position.
							continue;
						}

						string id0 = "";
						if (!string.IsNullOrEmpty(e0.Pid))
							id0 = "<color=#" + ColorUtility.ToHtmlStringRGB(Hud.Dim) + ">[" + e0.Pid + "]</color>";
						int idChars0 = string.IsNullOrEmpty(e0.Pid) ? 0 : e0.Pid.Length + 2;
						if (idChars0 < idCells0) id0 += new string(' ', idCells0 - idChars0);

						string crown0 = e0.Master
							? "<color=#" + ColorUtility.ToHtmlStringRGB(CMaster) + ">\u265B</color> "
							: "";

						// The logo is a texture, so its width is BOOKED as blank cells here and the
						// texture itself is drawn over them in the per-row pass below.
						int logoCells0 = (e0.Member && Core.AssetLoader.ArchiveLogo != null)
							? Mathf.Max(1, Mathf.CeilToInt((fs + 4f) / charW0)) : 0;
						string logoPad0 = logoCells0 > 0 ? new string(' ', logoCells0) : "";

						bool showPos0 = posColW > 0f && !string.IsNullOrEmpty(e0.Pos);
						float posW0 = showPos0 ? posColW : 0f;
						float nameRoom0 = nameW0 - ((idCells0 + (e0.Master ? 2 : 0) + logoCells0) * charW0
							- (nameX0 - (cx0 + 6f))) - posW0;
						int room0 = Mathf.Max(6, (int)(nameRoom0 / charW0));
						string shown0 = Trunc(e0.Label, room0);

						sbL.Append(id0).Append(crown0).Append(logoPad0)
						   .Append(e0.Member
							   ? Rainbow(shown0, VaClock.Now)
							   : "<color=#" + ColorUtility.ToHtmlStringRGB(e0.Col) + ">" + shown0 + "</color>");

						if (!string.IsNullOrEmpty(e0.Badge)) sbR.Append(e0.Badge);
						if (showPos0) sbP.Append(e0.Pos);
					}
					_colLeft[c] = sbL.ToString();
					_colRight[c] = sbR.ToString();
					_colPos[c] = sbP.ToString();
				}
			}

			// Stripes for every row (a Fill is nearly free), then the full per-row draw for the
			// few member rows the bulk pass left blank.
			for (int i = 0; i < shown; i++)
			{
				int col = i / rows;
				int row = i % rows;
				float cx = body.x + col * (colW + colGap);
				y = rowsTop + row * lh;
				Hud.Stripe(new Rect(cx + 2f, y, colW - 4f, lh), row);

				var e = _roster[i];
				var logo = Core.AssetLoader.ArchiveLogo;
				bool showPos = posColW > 0f && !string.IsNullOrEmpty(e.Pos);

				// MEMBER EXTRAS, per-row: a logo (a texture the bulk name string cannot hold) and a
				// rainbow name. A non-member takes its name from the bulk _colLeft block below.
				if (e.Member && logo != null)
				{

				// MEASURED placement — the real rendered width of each part decides where the next
				// one starts, so it is correct on a proportional font as well as a monospace one.
				float x = cx + 6f;
				if (!string.IsNullOrEmpty(e.Pid))
				{
					string idTxt = "[" + e.Pid + "]";
					Plain(new Rect(cx + 6f, y, colW, lh),
						"<color=#" + ColorUtility.ToHtmlStringRGB(Hud.Dim) + ">" + idTxt + "</color>", _mono, Color.white);
					x += Core.GuiCompat.TextWidth(idTxt, fs) + fs * 0.35f;
				}
				if (e.Master)
				{
					Plain(new Rect(x, y, fs * 2f, lh),
						"<color=#" + ColorUtility.ToHtmlStringRGB(CMaster) + ">♛</color>", _mono, Color.white);
					x += Core.GuiCompat.TextWidth("♛", fs) + fs * 0.25f;
				}
				GUI.DrawTexture(new Rect(x, y + (lh - fs) * 0.5f, fs, fs), logo, ScaleMode.ScaleToFit);
				x += fs + 4f;

					float posW = showPos ? posColW : 0f;
					float room = colW - badgeW - (x - cx) - posW;
					int chars = Mathf.Max(4, (int)(room / (fs * 0.5f)));
					Plain(new Rect(x, y, room, lh), Rainbow(Trunc(e.Label, chars), VaClock.Now), _mono, Color.white);
				}

				// Badge and position for the member row, in the same columns the bulk rows use, so a
				// member reads identically to everyone else — just with a rainbow name and a logo.
				if (showPos)
					Plain(new Rect(cx + colW - badgeW - posColW - 12f, y, posColW, lh), e.Pos, _monoRight, CPos);
				if (!string.IsNullOrEmpty(e.Badge))
					Plain(new Rect(cx + colW - badgeW - 4f, y, badgeW, lh), e.Badge, _monoRight, Hud.Dim);
			}

			// THE THREE DRAWS. One per column part, for the whole column.
			for (int c = 0; c < cols; c++)
			{
				float cx = body.x + c * (colW + colGap);
				float colH = rows * lh;
				if (!string.IsNullOrEmpty(_colLeft[c]))
					Plain(new Rect(cx + 6f, rowsTop, colW - 12f - badgeW, colH), _colLeft[c], _mono, Color.white);
				// Badge and position are drawn per-row above (pinned to each row's y), NOT here as a
				// multi-line block \u2014 that block, in the smaller badge font, drifted off the bottom rows.
			}

			if (_roster.Count > shown)
				Plain(new Rect(body.x + 6f, rowsTop + rows * lh, body.width - 12f, lh),
					"+" + (_roster.Count - shown) + " more…", _dim, Hud.Dim);
		}

		// Per-character rainbow that FLOWS: the hue offset advances with time, so the colours travel
		// along the name rather than sitting still. Rich text, which the roster styles already allow.
		//
		// Built with a StringBuilder and only for members: this runs per visible row per repaint, and
		// string concatenation in that loop is exactly the kind of thing that shows up in a profiler.
		private static readonly System.Text.StringBuilder _rainbow = new System.Text.StringBuilder(256);

		// CACHED AND THROTTLED — this was the single most expensive thing the mod did.
		//
		// The version below rebuilt the whole tagged string on EVERY REPAINT: an HSVToRGB, a
		// ToHtmlStringRGB and ~18 characters of markup PER LETTER, per member, per frame. At 100 fps
		// with a few members on screen that is tens of thousands of colour conversions and a fresh
		// garbage string every frame, and the profiler put this module at 264 ms/s because of it.
		//
		// The animation does not need frame-rate resolution: 12 steps a second already reads as a
		// smooth flow. So the result is cached per name and only rebuilt when the step changes —
		// the draw loop then just hands back a string it already has.
		private const float RainbowFps = 12f;
		private static readonly Dictionary<string, string> _rainbowCache = new Dictionary<string, string>(StringComparer.Ordinal);
		private static int _rainbowSlot = -1;

		private static string Rainbow(string text, float t)
		{
			if (string.IsNullOrEmpty(text)) return text;

			int slot = (int)(t * RainbowFps);
			if (slot != _rainbowSlot)
			{
				_rainbowSlot = slot;
				_rainbowCache.Clear();        // every name's colours moved together
			}
			if (_rainbowCache.TryGetValue(text, out string cached)) return cached;

			// Quantised to the slot as well, so the colours a cache entry was built with match the
			// ones every other name is using this step.
			float qt = slot / RainbowFps;
			_rainbow.Length = 0;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				// Whitespace needs no tag pair; skipping it keeps the string shorter.
				if (c == ' ') { _rainbow.Append(' '); continue; }
				float hue = Mathf.Repeat(qt * 0.35f + i * 0.055f, 1f);
				Color col = Color.HSVToRGB(hue, 0.62f, 1f);
				_rainbow.Append("<color=#")
					.Append(ColorUtility.ToHtmlStringRGB(col))
					.Append('>')
					.Append(c)
					.Append("</color>");
			}
			string built = _rainbow.ToString();
			_rainbowCache[text] = built;
			return built;
		}

		private void DrawLog(Rect r)
		{
			float s = 1f;
						// Row pitch is DERIVED from the font, not chosen independently — the two used to be
			// computed from different scales, so on some resolutions the lines were pitched tighter
			// than the glyphs were tall and the log overlapped itself.
			float fs = Mathf.Max(9f, _mono != null ? _mono.fontSize : 13f * s);
			float lh = fs * 1.42f;
			float timeW = fs * 4.4f, badgeW = fs * 3.1f, textX = fs * 7.9f;

			// The panel is only as tall as the lines it actually draws. It used to take the whole
			// rect it was handed, so ten lines still reserved 60% of the screen.
			int rowsFit = Mathf.Max(1, (int)((r.height - Hud.HeaderH - 12f) / lh));
			int drawnCount = Mathf.Min(_log.Count, rowsFit);
			if (drawnCount <= 0) return;

			var panelRect = new Rect(r.x, r.y, r.width, Hud.HeaderH + drawnCount * lh + 10f);
			var body = Hud.Panel(panelRect, "INSTANCE LOG", _log.Count.ToString());

			int rows = drawnCount;
			int start = Mathf.Max(0, _log.Count - rows);  // tail: newest at the bottom
			int drawn = 0;
			int maxChars = Mathf.Max(16, (int)((body.width - textX) / (fs * 0.52f)));
			for (int i = start; i < _log.Count; i++)
			{
				var e = _log[i];
				float ly = body.y + drawn * lh;

				Plain(new Rect(body.x, ly, timeW, lh - 2f), e.Time, _mono, CTime);

				string badge; Color bc;
				switch (e.Kind)
				{
					case Kind.Join:   badge = "JOIN"; bc = CJoin; break;
					case Kind.Left:   badge = "LEFT"; bc = CLeft; break;
					case Kind.Avatar: badge = "AVTR"; bc = CAvatar; break;
					case Kind.Video:  badge = "VID";  bc = CVideo; break;
					default:          badge = "◆";    bc = CWorld; break;
				}
				Plain(new Rect(body.x + timeW + 2f, ly, badgeW, lh - 2f), badge, _monoBold, bc);

				// name/main + optional detail (avatar name or video url)
				string main = e.Kind == Kind.Video ? (e.Detail ?? "")
					: (e.Kind == Kind.Avatar && !string.IsNullOrEmpty(e.Detail) ? e.Name + " → " + e.Detail : e.Name);
				Color mc = e.Kind == Kind.World ? CWorld : (e.Kind == Kind.Avatar ? CAvatar : (e.Kind == Kind.Video ? CVideo : CName));
				Shadowed(new Rect(body.x + textX, ly, body.width - textX, lh - 2f), Trunc(main, maxChars), _mono, mc);
				drawn++;
			}
			if (_log.Count == 0)
				Shadowed(new Rect(body.x, body.y, body.width, lh), "waiting for join/leave…", _dim, CDim);
		}

		private float _styleScale = -1f;

		// Rebuilt whenever the configured scale changes, so font size tracks panel size.
		private void EnsureStyles()
		{
			// Font size carries the HUD scale, so the sliders visibly change the panels instead of
			// only moving their boxes around.
			// One interface: the font follows the SCREEN only, never a user setting.
			float s = Hud.Scale;
			if (_styles && Mathf.Abs(s - _styleScale) < 0.01f) return;
			_styleScale = s;
			_title    = new GUIStyle { fontSize = Mathf.RoundToInt(12f * s), fontStyle = FontStyle.Bold, richText = true, normal = { textColor = CName } };
			// wordWrap OFF: the roster is drawn as one multi-line string per column, and a wrapped
			// long name would push every row below it out of line with its stripe.
			_mono     = new GUIStyle { fontSize = Mathf.RoundToInt(13f * s), richText = true, wordWrap = false, normal = { textColor = CName } };
			_monoBold = new GUIStyle { fontSize = Mathf.RoundToInt(13f * s), fontStyle = FontStyle.Bold, richText = true, normal = { textColor = CName } };
			// UpperRight, not MiddleRight: a multi-line label centred vertically would sit half a
			// column off from the rows it labels.
			_monoRight = new GUIStyle { fontSize = Mathf.RoundToInt(11f * s), fontStyle = FontStyle.Bold, richText = true, wordWrap = false, alignment = TextAnchor.UpperRight, normal = { textColor = CName } };
			_dim      = new GUIStyle { fontSize = Mathf.RoundToInt(12f * s), fontStyle = FontStyle.Italic, richText = true, normal = { textColor = CDim } };
			_styles = true;
		}

		private static string Trunc(string s, int max)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
		}

		private static Color Hex(string hex)
		{
			return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.white;
		}
	}
}
