using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// THE INSTANCE LOG, BESIDE THE QUICKMENU'S RIGHT WING — the mirror of WingPlayers, which owns the
	// left. Players left, log right.
	//
	// Joins, leaves, world and avatar changes, rendered as real menu text on a panel attached to the
	// QuickMenu instead of an IMGUI window floating over the world — so it follows the menu, which is
	// what makes it usable in VR.
	//
	// The feed is InstancePanelsModule.FeedRows: the SAME events the IMGUI panel draws, appended in
	// the same place from the same values, so the two can never disagree about what happened. The
	// structured list rather than the pre-formatted one, because this panel lays its rows out in real
	// columns and a column cannot align text that has already been glued together with spaces.
	//
	// See WingPlayersModule for why none of this clones a VRChat page any more: the clone brought a
	// UIPage, and that stopped the QuickMenu from opening at all.
	public class WingLogModule : IModule
	{
		public override string Name => "WingLog";

		private PanelSkin.Panel _panel;
		private float _nextTry;
		private int _fails;
		private int _shownCount = -1;

		public override void OnUpdate()
		{
			try
			{
				// ITS OWN SWITCH, NOT THE ON-SCREEN ONE (2026-09-21). This panel lives INSIDE VRChat's
				// menu, beside the wing — it is not drawn over the game. Sharing InstancePanelsEnabled
				// with the floating RShift+L panels meant the HUD master switch took this one down too,
				// and the whole point of that switch is to clear the screen while the menus keep working.
				if (!ModConfig.WingLogEnabled.Value) { Drop(); return; }

				if (_panel == null || !_panel.Alive)
				{
					_panel = null;
					float now = VaClock.Now;
					if (now < _nextTry) return;
					_nextTry = now + 3f;
					if (_fails > 20) return;
					if (!TryBuild()) { _fails++; return; }
					_fails = 0;
					_shownCount = -1;
				}

				// Every frame, deliberately: this tracks the wing's retract animation, and it is one
				// float compare that writes nothing unless the wing actually moved.
				_panel.Follow();

				// The feed only changes on a join, a leave or a world change — never per frame.
				int n = 0;
				try { n = InstancePanelsModule.FeedRows.Count; } catch { }
				if (n == _shownCount) return;
				_shownCount = n;
				Refresh();
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			if (_panel == null || !_panel.Alive) { _panel = null; _fails = 0; _nextTry = 0f; }
			_shownCount = -1;
		}

		public override void OnShutdown() { Drop(); }

		private void Drop()
		{
			try { if (_panel != null && _panel.Root != null) UnityEngine.Object.Destroy(_panel.Root.gameObject); } catch { }
			_panel = null;
			_shownCount = -1;
		}

		// ---------------------------------------------------------------- build

		private bool TryBuild()
		{
			var p = Core.SidePanel.Build(false, "VA_LogPanel", "INSTANCE LOG", "va_panel_log.jpg");
			if (p == null) return false;

			// No X Y Z column here — the log has nothing to put in it, and a heading over an always
			// empty column reads as a bug. Null hides it.
			PanelSkin.SetHeadings(p, "TIME", "EVENT", null, "TYPE");

			_panel = p;
			VRChatArchiveModPlugin.Logger.LogInfo("[WingLog] panel built beside the right wing.");
			return true;
		}

		// ---------------------------------------------------------------- rows

		private void Refresh()
		{
			if (_panel == null) return;

			List<InstancePanelsModule.FeedRow> feed;
			try { feed = InstancePanelsModule.FeedRows; } catch { return; }

			// Tail, newest at the bottom — the same order the IMGUI panel uses, so looking at one and
			// then the other does not require re-learning which end is recent.
			int cap = _panel.Capacity;
			int want = Mathf.Min(feed.Count, cap);
			int start = Mathf.Max(0, feed.Count - want);

			var rows = new List<PanelSkin.Row>(want);
			for (int i = 0; i < want; i++)
			{
				var e = feed[start + i];
				rows.Add(new PanelSkin.Row
				{
					// Time in the FIRST column, under its own "TIME" heading. Every string is tagged,
					// deliberately: MenuThemeModule repaints every TMP under the QuickMenu every
					// 0.35 s, and a rich-text tag is the one colour it cannot take.
					Id = PanelSkin.Tag(PanelSkin.HexDim, e.Time ?? ""),
					Name = PanelSkin.Tag(PanelSkin.HexText, e.Name ?? ""),
					Pos = "",
					Badge = "<color=" + (e.Color ?? "#FFFFFF") + "><b>" + (e.Badge ?? "") + "</b></color>",
				});
			}

			_panel.SetRows(rows);
			_panel.SetCount(feed.Count.ToString());
		}
	}
}
