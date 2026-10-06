using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// FIND THE DEAD IMGUI CALL BEFORE IT COSTS A SESSION.
	//
	// A mis-bound il2cpp method is not an exception. `catch` never sees it: the process dies inside
	// il2cpp_runtime_invoke with an access violation, and the only trace is whatever reached disk
	// before the fault. Every IMGUI breakage on this build was therefore found the same expensive
	// way -- play, crash, read the stack in ErrorLog, patch that one call, repeat: GUI.Label, then
	// GUIContent..ctor, then GUIStyleState.set_textColor, then GUISkin.get_label, four sessions for
	// four calls, with no way to know how many were left.
	//
	// So the primitives are exercised ON PURPOSE, once, inside the first real OnGUI, each preceded
	// by a force-flushed breadcrumb. If one of them is still mis-bound the game dies exactly as it
	// would have later, but driver.log now ENDS on the name of the call that did it -- the answer
	// arrives in one boot instead of one play session, and nothing has to be reproduced. When they
	// all survive, the summary line says so and the mod has a measured, build-specific statement
	// that its drawing surface is sound.
	//
	// Everything is drawn far off-screen with GUI.color fully transparent, so a user never sees the
	// test; it runs on one frame and never again.
	internal static class GuiSelfTest
	{
		private static bool _done;

		// Off the top-left corner by a screenful: inside the IMGUI clip rect's coordinate space but
		// nowhere a monitor can show.
		private static readonly Rect Away = new Rect(-4000f, -4000f, 120f, 20f);

		internal static void RunOnce()
		{
			if (_done) return;
			_done = true;

			int ok = 0;
			string stage = "(debut)";
			try
			{
				Color oldColor;
				stage = "GUI.get_color";
				VRChatArchiveModPlugin.Step("imgui: " + stage);
				oldColor = GUI.color; ok++;

				stage = "GUI.set_color";
				VRChatArchiveModPlugin.Step("imgui: " + stage);
				GUI.color = new Color(1f, 1f, 1f, 0f); ok++;

				try
				{
					stage = "new GUIStyle()";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					GUIStyle s = new GUIStyle(); ok++;

					stage = "GUIStyle.set_fontSize";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					s.fontSize = 12; ok++;

					stage = "GUIStyle.get_fontSize";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					int fs = s.fontSize; ok++;

					stage = "GUIStyle.set_alignment";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					s.alignment = TextAnchor.MiddleCenter; ok++;

					stage = "GUIStyle.set_richText";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					s.richText = true; ok++;

					stage = "GUIStyle.get_normal";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					GUIStyleState st = s.normal; ok++;

					stage = "GUIStyleState.set_textColor";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					if (st != null) st.textColor = new Color(0.9f, 0.9f, 1f, 1f);
					ok++;

					stage = "GUIStyleState.get_textColor";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					if (st != null) { Color c = st.textColor; }
					ok++;

					// The one that killed the menu: GUISkin's getters. Tested LAST of the style
					// group so everything cheaper is already recorded if this is still the fatal one.
					stage = "GUISkin.get_label (via GUI.skin)";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					GUIStyle lbl = GUI.skin != null ? GUI.skin.label : null;
					VRChatArchiveModPlugin.Step("imgui: GUI.skin.label = " + (lbl == null ? "null" : "ok"));
					ok++;

					stage = "new GUIContent(string)";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					GUIContent gc = new GUIContent("va"); ok++;

					stage = "GUIStyle.CalcSize";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					Vector2 sz = s.CalcSize(gc); ok++;

					stage = "GUI.Label(Rect,string,GUIStyle)";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					GUI.Label(Away, "va", s); ok++;

					stage = "GUI.Label(Rect,string)";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					GUI.Label(Away, "va"); ok++;

					stage = "GUI.Button(Rect,string,GUIStyle)";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					bool hit = GUI.Button(Away, "va", s); ok++;

					stage = "GUI.DrawTexture";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					Texture2D tex = Texture2D.whiteTexture;
					if (tex != null) GUI.DrawTexture(Away, tex);
					ok++;

					stage = "GUI.get_matrix / set_matrix";
					VRChatArchiveModPlugin.Step("imgui: " + stage);
					Matrix4x4 mtx = GUI.matrix; GUI.matrix = mtx; ok++;
				}
				finally
				{
					try { GUI.color = oldColor; } catch { }
				}

				VRChatArchiveModPlugin.Step("imgui: AUTOTEST OK (" + ok + " primitives)");
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[GuiSelfTest] " + ok + " primitives IMGUI testees en vol, toutes repondent — le dessin du mod est sain sur ce build.");
			}
			catch (Exception e)
			{
				// An EXCEPTION here is the survivable kind (an unresolved ICall, a stripped member):
				// the primitive is dead but the process lives, and GuiCompat neutralises it. The
				// fatal kind never reaches this line -- it ends driver.log on `stage` instead.
				VRChatArchiveModPlugin.Step("imgui: AUTOTEST arrete a " + stage + " (" + e.GetType().Name + ")");
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[GuiSelfTest] " + ok + " primitives IMGUI valides puis « " + stage + " » a echoue : "
					+ e.GetType().Name + " — ce que le mod dessine avec cette primitive gardera le defaut d'Unity.");
			}
		}
	}
}
