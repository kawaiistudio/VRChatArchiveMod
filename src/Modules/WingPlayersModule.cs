using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE PLAYERS PANEL, BESIDE THE QUICKMENU'S LEFT WING.
	//
	// The instance roster used to be a floating IMGUI window drawn over the world. This puts the same
	// data on a panel attached to the QuickMenu, so it follows the menu — which matters in VR, where
	// the menu rides the wrist and a HUD panel would stay planted in the world.
	//
	// FOUR ATTEMPTS GOT HERE, AND EACH FAILURE IS WORTH KEEPING:
	//   1. Rows appended into VRChat's own wing list. Two nested layout groups fought and a live dump
	//      showed the result: VA_PlayersSection {356x0 pos(194,-1368)} — zero height, far below a
	//      1024-tall wing, with our rows interleaved among the game's own entries.
	//   2. Hiding VRChat's rows to make room. Rejected outright: it takes the menu away from the user.
	//   3. A whole wing page cloned and left permanently visible. Each wing has its OWN
	//      MenuStateController and shows exactly one page at a time; an always-active clone is
	//      invisible to it and simply painted over whatever it was showing. The wings went blank.
	//   4. That clone moved beside the wings. It looked right — and it stopped the QuickMenu from
	//      OPENING, because the clone carried a UIPage and VRChat logged "Duplicate UIPage with same
	//      name: Root" then "InitializeUI canceled" (MenuDonor.CloneInert has the full evidence).
	//
	// So nothing here is cloned any more. PanelSkin BUILDS the panel from new GameObjects, which
	// sidesteps UIPage and StyleElement entirely and — the actual goal — makes it look like the mod's
	// own roster rather than a VRChat page wearing our text.
	//
	// Clicking a row selects that player in the DESKTOP CLIENT's PLAYERS page (the "wingSelect" field
	// of the 1 Hz sync); the in-game menu it used to open is sealed and never shows.
	public class WingPlayersModule : IModule
	{
		public override string Name => "WingPlayers";

		private PanelSkin.Panel _panel;
		private float _nextTry;
		private int _fails;
		private int _lastSignature = -1;
		// Bumped on every row click; travels with the selection so the client can consume it once.
		private static int _wingSeq;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.WingPlayersEnabled.Value) { Drop(); return; }

				if (_panel == null || !_panel.Alive)
				{
					_panel = null;
					float now = VaClock.Now;
					if (now < _nextTry) return;
					// Backing off rather than retrying every frame: a menu that does not exist yet must
					// not turn this into a per-frame hierarchy walk, and one that never appears must not
					// keep asking for ever.
					_nextTry = now + 3f;
					if (_fails > 40) return;
					if (!TryBuild()) { _fails++; return; }
					_fails = 0;
					_lastSignature = -1;
				}

				// Every frame, deliberately: this tracks the wing's retract animation, and it is one
				// float compare that writes nothing unless the wing actually moved.
				_panel.Follow();

				// Only touch the UI when the roster really changed.
				int sig = RosterSignature();
				if (sig == _lastSignature) return;
				_lastSignature = sig;
				Refresh();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[WingPlayers] update threw: {e.Message}");
				_fails++;
			}
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_lastSignature = -1;
			if (_panel == null || !_panel.Alive) { _panel = null; _fails = 0; _nextTry = 0f; }
		}

		public override void OnShutdown() { Drop(); }

		private void Drop()
		{
			try { if (_panel != null && _panel.Root != null) UnityEngine.Object.Destroy(_panel.Root.gameObject); } catch { }
			_panel = null;
			_lastSignature = -1;
		}

		// ---------------------------------------------------------------- build

		private bool TryBuild()
		{
			var p = Core.SidePanel.Build(true, "VA_PlayersPanel", "PLAYERS", "va_panel_players.jpg");
			if (p == null) return false;

			// Wired BEFORE the first SetRows: rows are pooled and are given their click target when
			// they are created, so a handler attached afterwards would miss every row already made.
			p.OnRow = OnRowClick;
			PanelSkin.SetHeadings(p, "ID", "NAME", PositionsOn() ? "X Y Z" : null, "PLATFORM");
			_headingsShowPos = PositionsOn();

			_panel = p;
			VRChatArchiveModPlugin.Logger.LogInfo("[WingPlayers] panel built beside the left wing.");
			return true;
		}

		// ---------------------------------------------------------------- rows

		// Cheap change detector: player count plus a hash of the ids and badges. Rebuilding the panel
		// every frame would be absurd for a list that changes on join and leave.
		private static int RosterSignature()
		{
			try
			{
				int h = 17;
				var r = VaTagsModule.Roster;
				h = h * 31 + r.Count;
				for (int i = 0; i < r.Count; i++)
				{
					var p = r[i];
					if (p == null) continue;
					h = h * 31 + (p.UserId != null ? p.UserId.GetHashCode() : 0);
					// SAME RULE, THIRD TIME (2026-09-18). [B] is drawn in the badge column, and it was
					// not in this hash -- so the wing list kept whatever it had built on join. The HUD
					// roster showed [B] the moment the block landed and this panel never did, which read
					// as "the marker does not work in the wings" when the marker was fine and the panel
					// simply never rebuilt. A block is not a join, so nothing else moved the signature.
					// BOTH directions hashed SEPARATELY: a flip from [B] to [b] (they unblock, you block)
					// leaves a single "either" bit unchanged and the panel would keep the wrong marker.
					bool bm = false, ib = false;
					try
					{
						if (!p.IsLocal && p.UserId != null)
						{
							bm = BlockedByProbeModule.BlockedMe.Contains(p.UserId);
							ib = BlockedByProbeModule.IBlocked.Contains(p.UserId);
						}
					}
					catch { }
					h = h * 31 + (p.Plus ? 1 : 0) + (p.Adult ? 2 : 0) + (bm ? 4 : 0) + (ib ? 8 : 0);
				}
				// The toggle is part of what is DRAWN, so it belongs in the signature: without it,
				// flipping the switch changed nothing until a player happened to join or leave.
				h = h * 31 + (PositionsOn() ? 1 : 0);
				// SAME RULE, SAME TRAP (2026-09-08). The custom username is drawn on the local row as
				// "real → custom", and it is not part of the roster data the hash above walks — so
				// setting one applied instantly, was verified in the log, and yet the panel kept the
				// old line until somebody happened to join. It is drawn, therefore it is in the
				// signature.
				try { h = h * 31 + (SpoofModule.Applied ?? "").GetHashCode(); } catch { }
				return h;
			}
			catch { return -1; }
		}

		/// <summary>Whether coordinates are shown, from the HUD's own switch. Read live rather than
		/// cached: the user flips it from the desktop client and expects the panel to follow without
		/// reopening the menu.</summary>
		private static bool PositionsOn()
		{
			try { return ModConfig.RosterPositions == null || ModConfig.RosterPositions.Value; }
			catch { return true; }
		}

		private bool _headingsShowPos = true;

		private void Refresh()
		{
			if (_panel == null) return;

			// A heading over a column that is now always empty reads as a bug, so it goes with it.
			bool on = PositionsOn();
			if (on != _headingsShowPos)
			{
				_headingsShowPos = on;
				PanelSkin.SetHeadings(_panel, "ID", "NAME", on ? "X Y Z" : null, "PLATFORM");
			}

			var roster = VaTagsModule.Roster;

			var rows = new List<PanelSkin.Row>(roster.Count);
			for (int i = 0; i < roster.Count; i++)
			{
				var p = roster[i];
				if (p == null) continue;
				rows.Add(RowOf(p));
			}

			int cap = _panel.Capacity;
			_panel.SetRows(rows);
			// The count reads "28/41" when the panel cannot show everyone, "41" when it can — the same
			// rule as the IMGUI panel, so a truncated list is never mistaken for a short one.
			_panel.SetCount(rows.Count > cap ? cap + "/" + rows.Count : rows.Count.ToString());
		}

		// Every string is wrapped in a colour tag, and that is load-bearing rather than decorative:
		// MenuThemeModule repaints every TMP_Text under the QuickMenu canvas every 0.35 s, and the
		// deployed theme paints them red. A rich-text tag is applied per character at layout time, so
		// tagged text wins that race by not entering it — untagged text is exactly why the old panel's
		// title came out red.
		private static PanelSkin.Row RowOf(VaTagsModule.PlayerEntry p)
		{
			var r = new PanelSkin.Row();

			r.Id = p.PlayerId >= 0 ? "[" + p.PlayerId + "]" : "";

			string col = string.IsNullOrEmpty(p.TrustColor) ? "#EAF6FF" : p.TrustColor;
			string name = "<color=" + col + "><b>" + Trunc(p.Name, 20) + "</b></color>";
			if (p.IsMaster) name = PanelSkin.Tag("FFC800", "♛") + " " + name;
			if (p.IsLocal) name += PanelSkin.Tag("FF4FD8", " (you)");
			// CUSTOM USERNAME, SHOWN BESIDE THE REAL ONE RATHER THAN REPLACING IT.
			//
			// This roster's Name comes from APIUser.displayName (VaTagsModule.ReadApiFields) — the API
			// record, the same source VRChat's own nameplate uses. The custom username writes
			// VRCPlayerApi.displayName, which is what UDON reads. Two different objects, so the spoof
			// cannot move this row, and that is correct rather than broken.
			//
			// Overwriting the row with the spoofed name would be a lie about the API truth, and writing
			// the spoof INTO APIUser is forbidden outright: those are ApiModel fields carrying
			// Save()/Put(), so a forged value can travel back to VRChat under this account. So the row
			// shows BOTH — the real name, then an arrow to what the worlds are being told. It doubles
			// as confirmation that the write landed, without a trip to the log.
			try
			{
				if (p.IsLocal && !string.IsNullOrEmpty(SpoofModule.Applied))
					name += PanelSkin.Tag("7CFF9E", " → " + Trunc(SpoofModule.Applied, 16));
			}
			catch { }
			if (p.IsOwner) name += PanelSkin.Tag("FFC800", " ★");
			r.Name = name;

			// Saturated rather than muted, and bold. The HUD's palette only looks electric because it
			// sits on flat black; ported onto a wallpaper the same hex values came out washed, so the
			// darker row plate does half the job and these do the other half.
			// The HUD's RosterPositions switch governs BOTH rosters. It read "in the PLAYERS panel",
			// and the QuickMenu panel IS a players panel — a switch that turns coordinates off in one
			// of two identical-looking lists is a switch that looks broken.
			r.Pos = !PositionsOn() ? ""
				: p.HasPos || p.Transform != null
				? PanelSkin.Tag("4DFFA6", string.Format(System.Globalization.CultureInfo.InvariantCulture,
					"{0:F1} {1:F1} {2:F1}", p.Position.x, p.Position.y, p.Position.z))
				: "";

			string badge = "";
			// Blocked — ALWAYS shown, never behind a switch, and in the BADGE column with the other
			// tags instead of glued in front of the name, where it shoved every marked row sideways.
			// Both directions, exactly as the HUD roster does it (InstancePanelsModule). Two lists of the
			// same players disagreeing about who is marked is worse than either rule on its own, and the
			// condition here must stay identical to the one hashed in RosterSignature above or the panel
			// goes back to not noticing.
			// Both directions, told apart, same rule as the HUD roster: [B] red = they blocked YOU,
			// [b] amber = you blocked them. The condition must stay identical to RosterSignature's hash
			// above or the panel stops repainting on a block.
			try
			{
				if (!p.IsLocal && !string.IsNullOrEmpty(p.UserId))
				{
					if (BlockedByProbeModule.BlockedMe.Contains(p.UserId))
						badge += PanelSkin.Tag("FF4B4B", "<b>[B]</b>") + " ";
					if (BlockedByProbeModule.IBlocked.Contains(p.UserId))
						badge += PanelSkin.Tag("FFC800", "<b>[b]</b>") + " ";
				}
			}
			catch { }
			if (p.Plus) badge += PanelSkin.Tag("FFC800", "<b>VRC+</b>") + " ";
			if (p.Adult) badge += PanelSkin.Tag("FF4FB0", "<b>18+</b>") + " ";
			badge += p.Platform == "Quest" ? PanelSkin.Tag("3BFF7A", "<b>Q</b>")
			       : p.Platform == "PC" ? PanelSkin.Tag("3FA9FF", "<b>PC</b>")
			       : PanelSkin.Tag("A8BCD4", "<b>" + Trunc(p.Platform, 3) + "</b>");
			// VR is a SEPARATE question from the build, and only drawn when VRChat answered it
			// (VrKnown). Silence when unknown — never the claim that somebody is on desktop.
			if (p.VrKnown && p.InVR) badge += PanelSkin.Tag("8FE9A8", "<b>VR</b>");
			r.Badge = badge;

			return r;
		}

		private void OnRowClick(int index)
		{
			try
			{
				var roster = VaTagsModule.Roster;
				if (index < 0 || index >= roster.Count) return;
				var p = roster[index];
				if (p == null || string.IsNullOrEmpty(p.UserId)) return;

				// HANDED TO THE DESKTOP CLIENT, not to the in-game menu. The old handler selected the
				// player in our IMGUI menu and "opened" its PLAYERS tab — but that menu is sealed
				// (Menu.Visible can only ever be false), so the click did nothing at all. The PLAYERS
				// page now lives in the client; ModControlModule.BuildSync emits this selection as
				// "wingSelect" on the next 1 Hz sync and the client jumps to that user. The seq is what
				// lets the client tell a NEW click from the same selection re-sent every second — it
				// consumes each seq once. A uGUI onClick fires on the main thread, so a plain field
				// write is enough here; BuildSync reads it on the main thread too (A1.3).
				ModControlModule.WingSelect = (p.UserId, p.Name, ++_wingSeq);
			}
			catch { }
		}

		// ---------------------------------------------------------------- helpers

		private static string Trunc(string s, int n) =>
			string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
	}
}
