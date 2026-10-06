using System;
using System.Threading.Tasks;
using UnityEngine;
using VRChatArchiveMod.Modules;

namespace VRChatArchiveMod.Core
{
	// Full on-screen interface (IMGUI). Draggable window, animated accent chrome, a left
	// tab rail (Dashboard / Anti-Crash / Archive / Settings / Info), stat cards, ON/OFF
	// toggles and live sliders — all drawn by us (see GuiKit). It never parents into
	// VRChat's own (obfuscated "Voyager") menu, so it stays reliable across game updates.
	// TAB toggles it. Absolute layout (no GUILayout) for pixel-precise grids.
	public static class Menu
	{
		// THE TAB MENU IS RETIRED — 2026-08-26.
		//
		// Everything it did lives in the desktop client now: MOD SETTINGS, PLAYERS and FAVORIS,
		// over the loopback bridge. Unbinding the TAB key was not enough, because several places
		// still ASKED for it — the QuickMenu's "Archive Menu" tile, the per-user menu, the wing
		// roster. Rather than hunt every caller and hope none comes back, the switch itself is
		// sealed: nothing can turn this on.
		//
		// Kept as a readable property because a dozen features gate on "is the mod menu in the
		// way" (movement, force grab, the Udon overlay, cursor capture) and they all still want
		// the honest answer, which is now permanently no.
		private static bool _visible;

		public static bool Visible
		{
			get => _visible;
			set { if (!value) _visible = false; }   // it can be closed, never opened
		}

		// TABS GROUPED BY INTENT, not by which module happens to implement them.
		//
		// The old list was twelve tabs named after internals — ESP, UDON, FEWTAGS — where one
		// setting could appear on two of them under two different names, and where the tools a
		// normal user needs sat next to reverse-engineering dumps. These nine answer questions a
		// player actually asks, and the developer dumps live behind DEV, which only appears when
		// Debug/Enabled is on.
		//
		// Tabs that absorbed another page carry a segmented sub-selector at the top rather than
		// stacking two unrelated screens: SCREEN is HUD/ESP/Tags, PLAY is Movement/Fun.
		private enum Tab { Home, Protection, Players, Screen, Avatars, Play, World, Settings, About }
		private static Tab _tab = Tab.Home;
		private static readonly string[] TabNames =
			{ "HOME", "PROTECTION", "PLAYERS", "ON SCREEN", "FAVORIS", "MOVE & PLAY", "WORLD", "SETTINGS", "ABOUT" };

		// Sub-page selection inside the merged tabs.
		private static int _screenSub;   // 0 = on-screen HUD, 1 = ESP detail, 2 = nameplate tags
		private static int _playSub;     // 0 = movement, 1 = fun
		// FAVORIS holds every favourite type, not just avatars: 0 = users, 1 = avatars,
		// 2 = worlds, 3 = groups. Avatars is the default because it is the one with actions.
		private static int _favKind = 1;
		private static Vector2 _kindScroll;

		// DEV is the last tab. It has its OWN setting rather than riding on DebugMode: that one also
		// turns on the profiler, verbose logging and a 5-second health snapshot, so tying the tab to
		// it would mean you cannot look at the dump tools without paying for instrumentation you
		// did not ask for. Debug still forces it on, because a debug session wants both.
		private static int VisibleTabCount
		{
			get
			{
				return TabNames.Length;
			}
		}

		// 16:9 to match the background image (1920×1080) so it fills the window uncropped.
		// Sized on first open to ~86% of the screen (see HandleInput) so everything is
		// comfortably readable; this is only the fallback for tiny displays.
		private static Rect _win = new Rect(120f, 70f, 1440f, 810f);
		private static bool _dragging;
		private static Vector2 _dragOffset;

		private static readonly Color DarkBg = new Color(0.012f, 0.014f, 0.02f, 0.986f);

		// ---- redesign palette ------------------------------------------------------------
		// Violet-biased neutrals over the kept wallpaper, with the Archive violet→pink as the one
		// accent. Deliberately NOT derived from GuiKit.Accent: the chrome is the brand and stays
		// violet whatever accent preset the user picks for the controls inside a page.
		private static readonly Color Violet     = new Color(0.545f, 0.361f, 0.965f, 1f);   // #8B5CF6
		private static readonly Color VioletSoft = new Color(0.545f, 0.361f, 0.965f, 0.16f);
		private static readonly Color VioletRim  = new Color(0.545f, 0.361f, 0.965f, 0.35f);
		private static readonly Color Pink       = new Color(0.925f, 0.282f, 0.600f, 1f);   // #EC4899
		private static readonly Color Line       = new Color(1f, 1f, 1f, 0.07f);
		private static readonly Color Hover      = new Color(1f, 1f, 1f, 0.035f);
		private static readonly Color Clear      = new Color(0f, 0f, 0f, 0f);
		private static readonly Color TextMuted  = new Color(0.663f, 0.624f, 0.769f, 1f);   // #A99FC4
		private static readonly Color TextFaint  = new Color(0.431f, 0.400f, 0.565f, 1f);   // #6E6690

		// Layout constants for the chrome, so header/rail/content can never drift apart.
		private const float RailPad = 26f;    // window edge → rail
		private const float RailW   = 168f;   // tab rail width
		private const float TabH    = 38f;    // one tab row
		private const float HeaderH = 74f;    // header band height

		// (The accent presets went with ModConfig.UiAccent, 2026-09-01: GuiKit.Accent keeps its
		// default, and nothing here is drawn any more that would show a preset.)

		// FPS readout (updated once per frame from HandleInput).
		private static float _fps, _fpsAccum;
		private static int _fpsFrames;

		private static GUIStyle _rowTagRight;
		private static GUIStyle _statusPill;
		private static GUIStyle _pageTitle, _railFoot;
		private static GUIStyle _title, _subtitle, _tabStyle, _tabActiveStyle, _header, _dim,
			_zone, _cardLabel, _cardValue, _status, _swatch, _rowName, _rowTag, _bigName, _previewBig;
		// Favourite cards: one line each, clipped rather than wrapped — a long avatar name must not
		// grow the card and push the buttons out of it.
		private static GUIStyle _favName, _favSub, _favMid;
		private static bool _styles;

		// Cursor state we took over, so it can be handed back exactly as it was.
		private static bool _cursorTaken;
		private static CursorLockMode _prevLock;
		private static bool _prevVisible;

		// Components we switched off for the duration, restored one-for-one on close.
		private static readonly System.Collections.Generic.List<Behaviour> _suspended =
			new System.Collections.Generic.List<Behaviour>();
		private static System.Type _playerInputType, _uiInputType;
		private static bool _inputTypesResolved;

		// While the menu is open, keyboard and mouse belong to the mod — not to VRChat.
		// Three reversible layers, all on stable Unity APIs (VRChat's own input flags are
		// obfuscated booleans, so guessing at them would be reckless):
		//   1. free the cursor            → no mouselook, pointer usable
		//   2. disable the EventSystem    → VRChat's uGUI gets no clicks/keys, so its menu
		//                                    can't open or react underneath ours
		//   3. suspend VRChat's input components + zero the axes → no walking/turning
		// The game re-asserts cursor state every frame, so this runs from Update AND
		// LateUpdate rather than once on toggle.
		// FREE-CURSOR HOLD (Left or Right Alt, while AltFreeCursor is on). True for exactly as long
		// as Alt is held: the pointer detaches from VRChat for free mouse AND free movement, and
		// the moment the key is released the game takes it straight back. HOLD rather than
		// toggle because Alt is shared with ALT+TAB — a toggle flipped on every window switch and
		// came back in a state nobody could explain; a held key cannot be left stuck. While it
		// is on we deliberately do NOT capture/pin — walking around is the whole point.
		public static bool FreeCursor;
		private static bool _captured;   // did we SuspendGameInput()? tracked apart from _cursorTaken

		public static void EnforceCursor()
		{
			try
			{
				// The cursor should be free whenever the menu is open OR the Alt toggle is on.
				bool wantFree = Visible || FreeCursor;
				// Capture (freeze movement, eat clicks) only for a menu opened WITHOUT free-move.
				// FreeCursor is an explicit "let me move", so it suppresses capture even mid-menu.
				// (UI/CaptureInput used to gate this too; it went 2026-09-01 — with the menu sealed,
				// Visible is never true and the switch could not change anything.)
				bool capture = Visible && !FreeCursor;

				if (wantFree)
				{
					if (!_cursorTaken)
					{
						_prevLock = Cursor.lockState;
						_prevVisible = Cursor.visible;
						_cursorTaken = true;
					}

					// Turn capture on/off live, so pressing Alt while the menu is open unfreezes you
					// and pressing it again re-freezes — without closing the menu.
					if (capture && !_captured) { SuspendGameInput(); _captured = true; }
					else if (!capture && _captured) { ResumeGameInput(); _captured = false; _frozen = false; }

					if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
					if (!Cursor.visible) Cursor.visible = true;
					// Give the OS IME a chance to talk to us while a field is focused. It is not
					// enough on its own for emoji (IMGUI still delivers one char at a time, and an
					// emoji is a surrogate pair) — the PASTE button and the emoji strip are the
					// routes that actually work — but it costs nothing and fixes accented input.
					try { if (Input.imeCompositionMode != IMECompositionMode.On) Input.imeCompositionMode = IMECompositionMode.On; } catch { }

					if (capture)
					{
						// Zero movement axes every frame so held keys can't drive the player.
						try { Input.ResetInputAxes(); } catch { }
						// Anything re-enabled by the game mid-menu gets switched off again.
						for (int i = 0; i < _suspended.Count; i++)
						{
							try { if (_suspended[i] != null && _suspended[i].enabled) _suspended[i].enabled = false; } catch { }
						}

						// Freeze the body: held movement keys / arrow-turn can't walk you off while
						// you use the mod. This is what actually makes VRChat ignore your input.
						PinPlayer();
					}
				}
				else if (_cursorTaken)
				{
					_cursorTaken = false;
					_frozen = false;
					_dragging = false;
					if (_captured) { ResumeGameInput(); _captured = false; }
					Cursor.lockState = _prevLock;
					Cursor.visible = _prevVisible;
				}
			}
			catch { }
		}

		private static bool _frozen;
		private static Vector3 _freezePos;
		// Pin the local player where the menu opened, so movement input can’t move you.
		// Skipped while flying (MovementModule owns the transform then).
		private static void PinPlayer()
		{
			try
			{
				if (ModConfig.FlyEnabled.Value) { _frozen = false; return; }
				var t = PlayerRef.LocalTransform();
				if (t == null) { _frozen = false; return; }
				if (!_frozen) { _freezePos = t.position; _frozen = true; }
				else
				{
					// If we were moved by something OTHER than the pin (a teleport, a station,
					// a world respawn), follow it instead of yanking the player back. Without
					// this the pin silently reverted the PLAYERS-tab TP button every frame.
					if ((t.position - _freezePos).sqrMagnitude > 0.25f) _freezePos = t.position;
					else t.position = _freezePos;
				}
				PlayerRef.ZeroVelocity(PlayerRef.LocalPlayer());
			}
			catch { }
		}

		// Called after a deliberate relocation (TP) so the pin re-anchors instead of fighting it.
		public static void NotifyTeleported()
		{
			_frozen = false;
		}

		// Switch off the EventSystem and VRChat's input behaviours, remembering each one.
		private static void SuspendGameInput()
		{
			// NEVER lose the ledger. This used to Clear() unconditionally, so a second Suspend
			// without a Resume in between forgot everything it had already switched off — and those
			// components (VRChat's UIInputManager among them) stayed disabled with nothing left
			// that knew how to turn them back on. That is how VRChat's own menu ended up dead:
			// Escape and the menu button reached a handler no one was ever going to re-enable.
			if (_suspended.Count > 0) ResumeGameInput();
			_suspended.Clear();
			try
			{
				var es = UnityEngine.EventSystems.EventSystem.current;
				if (es != null && es.enabled) { es.enabled = false; _suspended.Add(es); }
			}
			catch { }

			if (!_inputTypesResolved)
			{
				_inputTypesResolved = true;
				try
				{
					var asm = System.Reflection.Assembly.Load("Assembly-CSharp");
					_playerInputType = asm.GetType("PlayerInput");
					_uiInputType = asm.GetType("UIInputManager");
				}
				catch { }
			}

			SuspendAllOfType(_playerInputType);
			SuspendAllOfType(_uiInputType);

			VRChatArchiveModPlugin.Logger?.LogInfo($"[Menu] input captured — {_suspended.Count} component(s) suspended.");
		}

		private static void SuspendAllOfType(System.Type t)
		{
			if (t == null) return;
			try
			{
				var il2 = Il2CppInterop.Runtime.Il2CppType.From(t);
				var found = VRChatArchiveMod.Core.Live.AllOfType(il2);
				if (found == null) return;
				for (int i = 0; i < found.Length; i++)
				{
					var b = found[i]?.TryCast<Behaviour>();
					if (b != null && b.enabled) { b.enabled = false; _suspended.Add(b); }
				}
			}
			catch { }
		}

		private static void ResumeGameInput()
		{
			int restored = 0;
			for (int i = 0; i < _suspended.Count; i++)
			{
				try { if (_suspended[i] != null) { _suspended[i].enabled = true; restored++; } } catch { }
			}
			_suspended.Clear();
			if (restored > 0) VRChatArchiveModPlugin.Logger?.LogInfo($"[Menu] input released — {restored} component(s) restored.");
		}

		public static void HandleInput()
		{
			try
			{
				// TAB toggles the menu. While open the cursor detaches from VRChat (EnforceCursor
				// frees it + suspends the game's input) so mouse/keyboard drive the mod only;
				// pressing TAB again hands both back to VRChat.
				// ESCAPE HATCH. While our menu is open we switch VRChat's UIInputManager off — the
				// component that opens ITS menu. So if our menu is ever open when the user does not
				// think it is, Escape appears to be broken: the key reaches a disabled handler.
				// Escape therefore always closes ours first, which hands input straight back.
				if (Visible && Input.GetKeyDown(KeyCode.Escape))
				{
					Visible = false;
					EnforceCursor();          // release now, not on the next frame
				}

				// THE TAB MENU IS GONE — removed 2026-08-26.
				//
				// Every one of its pages now lives in the desktop client, which is a far better
				// place for them: real scrolling, real text fields, sliders you can type an exact
				// value into, and no need to suspend VRChat's input to use any of it. Settings go
				// through MOD SETTINGS, the instance roster and tags through PLAYERS, favourites
				// through FAVORIS. The QuickMenu integrations stay — those belong in the headset.
				//
				// The key handler is what is removed, not the drawing code: `Visible` is still
				// referenced elsewhere and Draw() is still wired (it now carries the BlockAll
				// banner and the status toasts, which are drawn whether the menu is open or not),
				// so leaving the rest alone keeps this a one-line change to bring back if it is
				// ever wanted. Nothing sets Visible any more, so none of the page code runs.

				// FREE-CURSOR (HOLD Left Alt). While Alt is held the pointer detaches from VRChat:
				// move the mouse freely, and keep walking. Let go and the game takes it straight
				// back.
				//
				// HOLD, not toggle, and that is the whole design. As a toggle this shared its key
				// with ALT+TAB — switching windows flipped the cursor AND (with Tab) the menu, so
				// you came back to a state nobody could explain. A held key cannot be left stuck:
				// the moment you release it, or the moment the window loses focus, it is over.
				bool holdFree = ModConfig.AltFreeCursor.Value
					&& (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
				if (holdFree != FreeCursor)
				{
					FreeCursor = holdFree;
					EnforceCursor();   // apply this frame, not next
				}

				_fpsAccum += VaClock.Delta;
				_fpsFrames++;
				if (_fpsAccum >= 0.5f)
				{
					_fps = _fpsFrames / _fpsAccum;
					_fpsAccum = 0f; _fpsFrames = 0;
				}
			}
			catch { }
		}

		// A LOUD, PERMANENT WARNING WHILE WORLD SCRIPTS ARE BLOCKED.
		//
		// This exists because of a real support thread: a user reported "mirrors don't work", and
		// nobody — including them — could work out why. The cause was this mod: blocking world
		// scripts kills the Udon behind mirrors, doors, pens, video players and pickups. The switch
		// was on, the effect was invisible, and the only explanation lived inside a menu the user
		// had no reason to open. "idk where to put or do that" / "where is that".
		//
		// So the state now announces itself on screen, for as long as it is on, and says exactly
		// where to turn it off. A setting that silently breaks the game has to be impossible to
		// forget you enabled.
		private static void DrawBlockedWarning()
		{
			try
			{
				float w = 560f, h = 46f;
				var r = new Rect((Screen.width - w) / 2f, 10f, w, h);
				GuiKit.RoundedFill(r, new Color(0.22f, 0.05f, 0.07f, 0.92f), 10f);
				GuiKit.RoundedBorder(r, Clear, new Color(0.97f, 0.35f, 0.35f, 0.95f), 10f, 1.6f);

				var prev = GUI.color;
				GUI.color = new Color(1f, 0.72f, 0.72f, 1f);
				VRChatArchiveMod.Core.GuiCompat.Label(new Rect(r.x + 16f, r.y + 5f, r.width - 26f, 20f),
					"WORLD SCRIPTS ARE BLOCKED — mirrors, doors, pens and videos will not work");
				GUI.color = new Color(0.92f, 0.78f, 0.80f, 1f);
				GUI.Label(new Rect(r.x + 16f, r.y + 24f, r.width - 26f, 18f),
					// TAB opened a menu that is sealed now, so it was an escape route that did not
					// exist. The switch lives in the desktop client, and so must the way out of it.
					// Named after the switch as the client actually labels it (the BlockAll entry,
					// described as PANIC), so the reader finds it instead of hunting for a label
					// that exists nowhere.
					"Desktop client → MOD SETTINGS → PROTECTION → switch off BlockAll (the PANIC toggle)");
				GUI.color = prev;
			}
			catch { }
		}

		// A quiet pill so you can tell the Alt free-cursor is on while the menu is closed.
		private static void DrawFreeCursorHint()
		{
			try
			{
				float w = 208f, h = 28f;
				var r = new Rect((Screen.width - w) / 2f, 12f, w, h);
				GuiKit.RoundedFill(r, new Color(0.10f, 0.07f, 0.16f, 0.82f), 9f);
				GuiKit.RoundedBorder(r, new Color(0f, 0f, 0f, 0f), new Color(0.55f, 0.36f, 0.98f, 0.9f), 9f, 1.5f);
				var prev = GUI.color;
				GUI.color = new Color(0.86f, 0.80f, 1f, 1f);
				VRChatArchiveMod.Core.GuiCompat.Label(new Rect(r.x + 14f, r.y + 4f, r.width - 24f, 20f), "FREE CURSOR  ·  release Alt to lock");
				GUI.color = prev;
			}
			catch { }
		}

		// The last status strings already turned into a toast, so a message pops ONCE, when it
		// changes — not on every frame it happens to still be set.
		private static string _toastVa = "", _toastFav = "";

		public static void Draw()
		{
			// Drawn whether or not our menu is open — especially when it is closed, which is when
			// the user is standing in front of a mirror wondering why it is blank.
			bool blocked = false;
			try { blocked = ModConfig.UdonBlockAll.Value; } catch { }
			if (blocked) { EnsureStyles(); DrawBlockedWarning(); }

			// STATUS TOASTS, also before the Visible gate, on purpose. The menu is sealed, so the
			// status pill on its PLAYERS tab (DrawStatus) is drawn for nobody — a QuickMenu card
			// or a client command that refused ("no player selected", "tag lock enabled") wrote its
			// reason into a string nobody could read, and the button simply looked dead. Diffing
			// here means every module that sets LastStatus / Status gets shown without having to
			// know about the toast.
			try
			{
				string va = VaTagsModule.LastStatus ?? "";
				if (!string.Equals(va, _toastVa, StringComparison.Ordinal)) { _toastVa = va; Toast.Show(va); }
				string fav = ArchiveFavButtonModule.Status ?? "";
				if (!string.Equals(fav, _toastFav, StringComparison.Ordinal)) { _toastFav = fav; Toast.Show(fav); }
			}
			catch { }
			Toast.Draw();

			if (!Visible) { if (FreeCursor) DrawFreeCursorHint(); return; }

			Matrix4x4 oldMat = GUI.matrix;
			try
			{
				EnsureStyles();
				// The chrome does not follow GuiKit.Accent: header labels and eyebrows are part of
				// the brand and stay violet-neutral. (The accent preset and the menu scale that used
				// to be applied here went with UI/AccentPreset and UI/Scale, 2026-09-01.)

				HandleDrag();

				// Soft rounded window (glow + rounded panel — no hard brackets/grid/corners).
				DrawBackground();

				// ---------------- HEADER ----------------
				// The brand mark: the real Archive logo in a rounded violet tile, then the wordmark.
				var mark = new Rect(_win.x + RailPad, _win.y + 22f, 38f, 38f);
				GuiKit.RoundedFill(mark, Violet, 11f);
				var logo = AssetLoader.ArchiveLogo;
				if (logo != null)
				{
					// Inset by a pixel so the artwork sits inside the rounded tile, not over its corners.
					GUI.DrawTexture(new Rect(mark.x + 1f, mark.y + 1f, mark.width - 2f, mark.height - 2f),
						logo, ScaleMode.ScaleAndCrop);
				}
				GUI.Label(new Rect(mark.xMax + 12f, _win.y + 22f, 300f, 22f), "VRCHAT ARCHIVE", _title);
				GUI.Label(new Rect(mark.xMax + 13f, _win.y + 43f, 300f, 16f), "MOD MENU", _subtitle);

				// Right-hand status readout — quiet, aligned, no barcode/zone chrome.
				string protOn = ModConfig.AntiCrashEnabled.Value
					? "<color=#34D399>ACTIVE</color>" : "<color=#F87171>OFF</color>";
				GUI.Label(new Rect(_win.xMax - 252f, _win.y + 24f, 220f, 18f), $"SHIELD  {protOn}", _status);
				GUI.Label(new Rect(_win.xMax - 252f, _win.y + 43f, 220f, 18f),
					$"FPS  <color=#F3EEFF>{Mathf.RoundToInt(_fps)}</color>"
					+ (FreeCursor ? "   <color=#C4B5FD>FREE CURSOR</color>" : ""), _status);

				// Hairline under the header, full width — separates chrome from content.
				GuiKit.Fill(new Rect(_win.x + RailPad, _win.y + HeaderH, _win.width - RailPad * 2f, 1f), Line);

				// ---------------- TAB RAIL ----------------
				float railX = _win.x + RailPad;
				float railTop = _win.y + HeaderH + 18f;
				for (int i = 0; i < VisibleTabCount; i++)
				{
					var r = new Rect(railX, railTop + i * TabH, RailW, TabH - 5f);
					bool active = (int)_tab == i;
					bool hover = r.Contains(Event.current.mousePosition);

					if (active)
					{
						// Violet pill + a glowing bar on its left edge, exactly like the mockup.
						GuiKit.RoundedFill(r, VioletSoft, 11f);
						GuiKit.RoundedBorder(r, Clear, VioletRim, 11f, 1f);
						GuiKit.RoundedFill(new Rect(r.x - 6f, r.y + 8f, 3f, r.height - 16f), Violet, 2f);
					}
					else if (hover) GuiKit.RoundedFill(r, Hover, 11f);

					if (GUI.Button(new Rect(r.x + 14f, r.y, r.width - 14f, r.height),
						TabNames[i], active ? _tabActiveStyle : _tabStyle))
						_tab = (Tab)i;
				}

				// Rail footer: version + how to close, where the mockup puts it.
				GUI.Label(new Rect(railX + 2f, _win.yMax - 52f, RailW, 16f),
					"v3.3.0 · TAB to close", _railFoot);

				// Vertical hairline between rail and content.
				GuiKit.Fill(new Rect(railX + RailW + 14f, railTop, 1f, _win.height - HeaderH - 78f), Line);

				// ---------------- CONTENT ----------------
				var content = new Rect(railX + RailW + 34f, railTop,
					_win.xMax - (railX + RailW + 34f) - RailPad, _win.height - HeaderH - 78f);

				// The page title sits above the content, replacing the old "ZONE A-1 //" chrome.
				GUI.Label(new Rect(content.x, content.y - 2f, 420f, 22f), TabNames[(int)_tab], _pageTitle);

				var inner = new Rect(content.x, content.y + 30f, content.width, content.height - 34f);
				switch (_tab)
				{
					case Tab.Home:       DrawDashboard(inner); break;
					case Tab.Protection: DrawAntiCrash(inner); break;
					case Tab.Players:    DrawPlayers(inner); break;
					case Tab.Screen:     DrawScreenTab(inner); break;
					case Tab.Avatars:    DrawFavorites(inner); break;
					case Tab.Play:       DrawPlayTab(inner); break;
					case Tab.World:      DrawUdon(inner); break;
					case Tab.Settings:   DrawSettings(inner); break;
					case Tab.About:      DrawCredits(inner); break;
				}

				// Footer + custom cursor.
				GUI.Label(new Rect(_win.x + 32f, _win.yMax - 30f, _win.width - 60f, 20f),
					"TAB: toggle   ·   F8: dump VRChat UI   ·   drag the header to move", _dim);

				Vector2 mp = Event.current.mousePosition;
				GUI.color = GuiKit.Accent;
				GUI.DrawTexture(new Rect(mp.x - 3f, mp.y - 3f, 6f, 6f), GuiKit.Pixel);
				GUI.color = Color.white;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Menu] draw threw: {e}");
			}
			finally
			{
				GUI.matrix = oldMat;
			}
		}

		// ================= TABS =================

		// The overlays, grouped by WHAT THEY SHOW and nested under their master. This was a flat
		// 3x3 grid whose only grouping was the invisible fact that each column happened to belong to
		// one feature — so "Radar names" sat beside "Udon overlay" with nothing saying that the
		// first does nothing unless the radar above it is on.
		private static void DrawHudOverlays(Rect a)
		{
			float gap = 14f;
			float colW = (a.width - gap * 2f) / 3f;
			float c0 = a.x, c1 = a.x + colW + gap, c2 = a.x + 2f * (colW + gap);

			// ---- column 1: people
			GUI.Label(new Rect(c0, a.y, colW, 16f), "PEOPLE", _header);
			ModConfig.InstancePanelsEnabled.Value = GuiKit.Toggle(new Rect(c0, a.y + 22f, colW, 38f),
				"Player list (RShift+L)", ModConfig.InstancePanelsEnabled.Value);
			ModConfig.WingPlayersEnabled.Value = GuiKit.Toggle(new Rect(c0, a.y + 64f, colW, 38f),
				"Player list in VRChat's menu", ModConfig.WingPlayersEnabled.Value);
			// THE 3D CAPSULE is what shows a player through a wall. The flat screen-space box this
			// used to drive is gone; every other ESP glow has its own switch in the ESP DETAIL tab.
			ModConfig.EspCapsule.Value = GuiKit.Toggle(new Rect(c0, a.y + 106f, colW, 38f),
				"Player capsules (see through walls)", ModConfig.EspCapsule.Value);
			GUI.Label(new Rect(c0 + 6f, a.y + 146f, colW - 6f, 16f),
				"<color=#6E6690>more ESP switches in the ESP DETAIL tab above</color>", _dim);

			// ---- column 2: radar, with its options dimmed when the radar itself is off
			GUI.Label(new Rect(c1, a.y, colW, 16f), "RADAR", _header);
			ModConfig.RadarEnabled.Value = GuiKit.Toggle(new Rect(c1, a.y + 22f, colW, 38f),
				"Radar (RShift+M)", ModConfig.RadarEnabled.Value);
			bool radar = ModConfig.RadarEnabled.Value;
			Sub(radar, () =>
			{
				ModConfig.RadarNames.Value = GuiKit.Toggle(new Rect(c1 + 14f, a.y + 64f, colW - 14f, 38f),
					"Show names", ModConfig.RadarNames.Value);
				ModConfig.RadarMap.Value = GuiKit.Toggle(new Rect(c1 + 14f, a.y + 106f, colW - 14f, 38f),
					"Draw the map (costs FPS)", ModConfig.RadarMap.Value);
			});
			if (!radar)
				GUI.Label(new Rect(c1 + 14f, a.y + 146f, colW - 14f, 16f),
					"<color=#6E6690>turn the radar on to use these</color>", _dim);

			// ---- column 3: world scripts
			GUI.Label(new Rect(c2, a.y, colW, 16f), "WORLD SCRIPTS", _header);
			ModConfig.UdonLogEnabled.Value = GuiKit.Toggle(new Rect(c2, a.y + 22f, colW, 38f),
				"Record what the world does", ModConfig.UdonLogEnabled.Value);
			bool rec = ModConfig.UdonLogEnabled.Value;
			Sub(rec, () =>
			{
				ModConfig.UdonLogOverlay.Value = GuiKit.Toggle(new Rect(c2 + 14f, a.y + 64f, colW - 14f, 38f),
					"Show it on screen", ModConfig.UdonLogOverlay.Value);

			});
			GUI.Label(new Rect(c2 + 14f, a.y + 146f, colW - 14f, 16f),
				rec ? "<color=#6E6690>the WORLD tab reads this recording</color>"
				    : "<color=#6E6690>off — nothing is recorded, the WORLD tab stays empty</color>", _dim);
		}

		// Draws a nested block dimmed when its master is off, so a sub-option never looks
		// independently usable. It stays clickable: greying out a control the user can still press
		// is a lie, so this only signals subordination.
		private static void Sub(bool masterOn, Action body)
		{
			Color keep = GUI.color;
			if (!masterOn) GUI.color = new Color(1f, 1f, 1f, 0.42f);
			try { body(); } finally { GUI.color = keep; }
		}

		// ================= DEV TOOLS =================

		private static int _devSub;
		private static bool _profilerWasOn;
		private static string _cfgFilter = "", _typeFilter = "", _selType = "";
		private static System.Collections.Generic.List<string> _typeMembers;
		private static Vector2 _devScroll, _devScroll2;

		// ON SCREEN — everything the mod draws over the game, in one place.
		//
		// These used to be three separate answers to one question: the master switches sat on the
		// dashboard, ESP's settings on their own tab, and the nameplate tags on a third — so you
		// turned ESP on in one place and tuned it in another, and nothing connected them.
		private static void DrawScreenTab(Rect a)
		{
			_screenSub = GuiKit.Segmented(new Rect(a.x, a.y, a.width, 36f),
				new[] { "Overlays", "ESP detail", "Nameplate tags" }, _screenSub);

			var body = new Rect(a.x, a.y + 44f, a.width, a.height - 44f);
			switch (_screenSub)
			{
				case 1: DrawEsp(body); break;
				case 2: DrawFewTags(body); break;
				default: DrawHudOverlays(body); break;
			}
		}

		// MOVE & PLAY — the things you do to yourself, and the toys.
		// Movement alone was seven controls and did not earn a tab of its own.
		private static void DrawPlayTab(Rect a)
		{
			_playSub = GuiKit.Segmented(new Rect(a.x, a.y, a.width, 36f),
				new[] { "Movement", "Fun" }, _playSub);

			var body = new Rect(a.x, a.y + 44f, a.width, a.height - 44f);
			if (_playSub == 1) DrawFun(body); else DrawMovement(body);
		}

		private static void DrawDashboard(Rect a)
		{
			float gap = 12f;
			float cardW = (a.width - gap * 2f) / 3f;
			float cardH = 78f;

			Card(a.x + 0 * (cardW + gap), a.y, cardW, cardH, "SHIELD",
				ModConfig.AntiCrashEnabled.Value ? "ON" : "OFF", ModConfig.AntiCrashEnabled.Value);
			Card(a.x + 1 * (cardW + gap), a.y, cardW, cardH, "PLAYERS",
				VaTagsModule.Roster.Count.ToString(), true);
			Card(a.x + 2 * (cardW + gap), a.y, cardW, cardH, "AVATARS",
				AntiCrashModule.AvatarsScanned.ToString(), true);

			float r2 = a.y + cardH + gap;
			Card(a.x + 0 * (cardW + gap), r2, cardW, cardH, "NEUTRALIZED",
				AntiCrashModule.NeutralizedTotal.ToString(), true);
			Card(a.x + 1 * (cardW + gap), r2, cardW, cardH, "TAGS DB",
				VaTagsModule.RecordsLoaded.ToString(), true);
			Card(a.x + 2 * (cardW + gap), r2, cardW, cardH, "UDON/s",
				UdonLogModule.PerSecond.ToString(), true);

			float hy = r2 + cardH + 22f;
			// INSTANCE — the room you are standing in, read from the client's own ApiWorldInstance
			// (no network request). Refreshed at most once a second inside CurrentInstance().
			var inst = VaTagsModule.CurrentInstance();
			GUI.Label(new Rect(a.x, hy, a.width, 16f), "INSTANCE", _header);
			float iy = hy + 22f;
			string worldLine = string.IsNullOrEmpty(inst.WorldName)
				? "<color=#6E6690>world not resolved yet</color>"
				: "<color=#FFFFFF>" + Trunc(inst.WorldName, 46) + "</color>"
				  + (inst.Present > 0 ? "   <color=#6E6690>present</color> <color=#7CFF9E>" + inst.Present + "</color>" : "")
				  + (inst.Capacity > 0 ? "<color=#6E6690>/" + inst.Capacity + "</color>" : "");
			GUI.Label(new Rect(a.x, iy, a.width, 18f), worldLine, _dim);

			string meta = "";
			if (!string.IsNullOrEmpty(inst.Region))     meta += "<color=#6E6690>region</color> <color=#FFB86B>" + inst.Region.ToUpperInvariant() + "</color>   ";
			if (!string.IsNullOrEmpty(inst.Access))     meta += "<color=#6E6690>access</color> <color=#7FB0FF>" + inst.Access + "</color>   ";
			if (!string.IsNullOrEmpty(inst.InstanceId)) meta += "<color=#6E6690>#</color><color=#FFFFFF>" + inst.InstanceId + "</color>";
			if (meta.Length > 0) GUI.Label(new Rect(a.x, iy + 20f, a.width, 18f), meta, _dim);
			if (!string.IsNullOrEmpty(inst.WorldId))
				GUI.Label(new Rect(a.x, iy + 40f, a.width, 18f),
					"<color=#6E6690>world</color> <color=#C58BFF>" + inst.WorldId + "</color>", _dim);

			// The overlay switches moved to the ON SCREEN tab, where their settings already
			// live. Keeping the masters here and the detail there is what made ESP switchable
			// on one page and tunable on another.
			hy = iy + 68f;

			// ---- QUICK ACTIONS ------------------------------------------------------------
			float qy = hy;
			GUI.Label(new Rect(a.x, qy, a.width, 16f), "QUICK ACTIONS", _header);

			float by = qy + 24f;
			// SAFE ACTIONS ONLY. "Dump VRChat UI" used to sit here as one of the two things the
			// home screen offered — a multi-hundred-KB reverse-engineering dump, styled exactly
			// like the protection button next to it. It lives on the DEV tab now.
			float bw = (a.width - gap) / 2f;
			if (GuiKit.Button(new Rect(a.x, by, bw, 42f), "Re-check everyone now")) _rescanRequested = true;
			if (GuiKit.Button(new Rect(a.x + bw + gap, by, bw, 42f), "Save a problem report"))
				Modules.DiagnosticsModule.SaveNow();

			GUI.Label(new Rect(a.x, by + 52f, a.width, 34f),
				"Protection and archiving run automatically. Individual clamp vectors live in ANTI-CRASH; "
				+ "ESP's box/name/skeleton detail lives in ESP.",
				_dim);
		}

		private static void DrawAntiCrash(Rect a)
		{
			float gap = 14f;
			float colW = (a.width - gap) / 2f;

			// WRITE ONLY ON CHANGE (same rule as the ANTI-UDON toggles below). Assigning a ConfigEntry
			// every frame rewrites the config file continuously, and AntiCrashModule now edge-detects
			// these values to undo / rescan \u2014 a constant stream of identical writes is harmless to
			// that, but a real change must be the only thing that ever reaches the entry.
			void CfgToggle(Rect r, string label, BepInEx.Configuration.ConfigEntry<bool> entry)
			{
				bool cur = entry.Value;
				bool v = GuiKit.Toggle(r, label, cur);
				if (v != cur) entry.Value = v;
			}

			CfgToggle(new Rect(a.x, a.y, a.width, 40f), "Anti-Crash Master", ModConfig.AntiCrashEnabled);

			// BUNDLE GUARD \u2014 the download side of the same protection. The clamps below act on what an
			// avatar DOES once it is loaded; this acts on whether a bundle is allowed to load at all.
			// Gated by the master like every other vector.
			CfgToggle(new Rect(a.x, a.y + 44f, a.width, 40f), "Asset-bundle guard (source + integrity)", ModConfig.BundleGuardEnabled);

			bool guardLive = ModConfig.AntiCrashEnabled.Value && ModConfig.BundleGuardEnabled.Value;
			GUI.Label(new Rect(a.x, a.y + 86f, a.width, 18f),
				guardLive
					? "<color=#7CFF9E>on</color> \u2014 bundles must come from VRChat's own delivery, the CRC check stays "
					  + "on, and a bad cache file is re-downloaded"
						// [open-source build] AssetBundlePatchModule (plaintext-cache decryptor) is not part of
						// this repository, so the "N refused" counter is omitted here.
					: ModConfig.BundleGuardEnabled.Value
						? "<color=#FFC08A>off</color> \u2014 the master is off, so the guard is off with it"
						: "<color=#FFC08A>off</color> \u2014 any source is accepted and a cached file is trusted as-is",
				_dim);

			float hy = a.y + 52f + 56f;
			GUI.Label(new Rect(a.x, hy, a.width, 16f), "CLAMP VECTORS", _header);

			float ty = hy + 24f;
			CfgToggle(new Rect(a.x, ty, colW, 40f), "Particles", ModConfig.ClampParticles);
			CfgToggle(new Rect(a.x + colW + gap, ty, colW, 40f), "Lights", ModConfig.ClampLights);
			CfgToggle(new Rect(a.x, ty + 48f, colW, 40f), "AudioSources", ModConfig.ClampAudioSources);
			CfgToggle(new Rect(a.x + colW + gap, ty + 48f, colW, 40f), "Cloth", ModConfig.ClampCloth);
			CfgToggle(new Rect(a.x, ty + 96f, colW, 40f), "PhysBones", ModConfig.ClampPhysBones);
			CfgToggle(new Rect(a.x + colW + gap, ty + 96f, colW, 40f), "Contacts", ModConfig.ClampContacts);

			float sy = ty + 96f + 56f;
			GUI.Label(new Rect(a.x, sy, a.width, 16f), "THRESHOLDS", _header);

			float s0 = sy + 26f;
			Slider(a.x, s0, colW, "Max Particles", ModConfig.MaxParticleSystems.Value, 16, 1024, v => ModConfig.MaxParticleSystems.Value = (int)v);
			Slider(a.x + colW + gap, s0, colW, "Max Lights", ModConfig.MaxLights.Value, 1, 64, v => ModConfig.MaxLights.Value = (int)v);
			Slider(a.x, s0 + 46f, colW, "Max AudioSrc", ModConfig.MaxAudioSources.Value, 8, 512, v => ModConfig.MaxAudioSources.Value = (int)v);
			Slider(a.x + colW + gap, s0 + 46f, colW, "Max Cloth", ModConfig.MaxCloth.Value, 8, 256, v => ModConfig.MaxCloth.Value = (int)v);
			Slider(a.x, s0 + 92f, colW, "Max PhysBones", ModConfig.MaxPhysBones.Value, 16, 1024, v => ModConfig.MaxPhysBones.Value = (int)v);
			Slider(a.x + colW + gap, s0 + 92f, colW, "Max Contacts", ModConfig.MaxContacts.Value, 16, 1024, v => ModConfig.MaxContacts.Value = (int)v);

			Slider(a.x, s0 + 138f, a.width, "Scan interval (frames)", ModConfig.ScanIntervalFrames.Value, 1, 120, v => ModConfig.ScanIntervalFrames.Value = (int)v);

			// ANTI-UDON lives on the UDON tab next to the console that explains it, but the console is
			// not where you go when a world is hammering you — this tab is. Same config entries, so the
			// two views can never disagree.
			float uy = s0 + 184f;
			GUI.Label(new Rect(a.x, uy, a.width, 16f), "ANTI-UDON (WORLD SCRIPTS)", _header);

			// WRITE ONLY ON CHANGE. This assigned the ConfigEntry every frame the panel drew, and
			// the same setting is drawn by a SECOND panel further down — two unconditional writers
			// for one value, which is how the anti-crasher ended up switched off with nobody having
			// touched it. BepInEx also persists a ConfigEntry on assignment, so this was rewriting
			// the config file continuously.
			bool crashHere = GuiKit.Toggle(new Rect(a.x, uy + 24f, colW, 40f),
				"Anti-crasher (rate guard)", ModConfig.UdonBlockCrashers.Value);
			if (crashHere != ModConfig.UdonBlockCrashers.Value)
				ModConfig.UdonBlockCrashers.Value = crashHere;
			bool panicHere = GuiKit.Toggle(new Rect(a.x + colW + gap, uy + 24f, colW * 0.46f, 40f),
				"PANIC: block all", ModConfig.UdonBlockAll.Value);
			if (panicHere != ModConfig.UdonBlockAll.Value)
			{
				ModConfig.UdonBlockAll.Value = panicHere;
				VaTagsModule.LastStatus = panicHere
					? "PANIC: world scripts are no longer run by your client \u2014 doors, pens and videos will stop working"
					: "\u2713 world scripts running normally again";
			}
			Slider(a.x + colW + gap + colW * 0.50f, uy + 26f, colW * 0.50f, "Crasher rate /s",
				ModConfig.UdonCrasherPerSecond.Value, 100, 5000, v => ModConfig.UdonCrasherPerSecond.Value = (int)v);

			GUI.Label(new Rect(a.x, uy + 70f, a.width, 18f),
				UdonLogModule.BlockedTotal > 0
					? $"<color=#FF7A7A>{UdonLogModule.BlockedTotal}</color> event(s) blocked   \u00b7   last: <color=#FFFFFF>{Trunc(UdonLogModule.LastBlocked, 32)}</color>"
					: "nothing blocked so far \u2014 the guard is watching, not interfering", _dim);
			GUI.Label(new Rect(a.x, uy + 92f, a.width, 34f),
				"Local self-defence: a blocked event is one YOUR client skips \u2014 nothing is sent and no other player is "
				+ "affected. If the same harmless-looking event keeps being blocked (a video player's get_VideoPlayerType, "
				+ "say), it is a false positive: raise the rate or switch the guard off for that world.", _dim);
		}

		private static void DrawFewTags(Rect a)
		{
			float gap = 14f;
			float colW = (a.width - gap) / 2f;

			ModConfig.FewTagsEnabled.Value = GuiKit.Toggle(new Rect(a.x, a.y, a.width, 40f),
				"FewTags Master", ModConfig.FewTagsEnabled.Value);

			float hy = a.y + 52f;
			GUI.Label(new Rect(a.x, hy, a.width, 16f), "DISPLAY", _header);

			float ty = hy + 24f;
			ModConfig.FewTagsShowHeader.Value    = GuiKit.Toggle(new Rect(a.x, ty, colW, 40f), "Header plate", ModConfig.FewTagsShowHeader.Value);
			ModConfig.FewTagsShowBigPlates.Value = GuiKit.Toggle(new Rect(a.x + colW + gap, ty, colW, 40f), "Big plates", ModConfig.FewTagsShowBigPlates.Value);

			float sy = ty + 56f;
			Slider(a.x, sy, colW, "Max tags per user", ModConfig.FewTagsMaxTagsPerUser.Value, 1, 100, v => ModConfig.FewTagsMaxTagsPerUser.Value = (int)v);
			Slider(a.x + colW + gap, sy, colW, "DB refresh (minutes)", ModConfig.FewTagsUpdateMinutes.Value, 1, 60, v => ModConfig.FewTagsUpdateMinutes.Value = (int)v);

			float dy = sy + 62f;
			GUI.Label(new Rect(a.x, dy, a.width, 16f), "DATABASE", _header);

			float cy = dy + 24f;
			float cardW = (a.width - gap) / 2f;
			Card(a.x, cy, cardW, 74f, "RECORDS", FewTagsModule.RecordsLoaded.ToString(), FewTagsModule.RecordsLoaded > 0);
			Card(a.x + cardW + gap, cy, cardW, 74f, "TAGGED HERE", FewTagsModule.TaggedHere.ToString(), FewTagsModule.TaggedHere > 0);

			float by = cy + 74f + 16f;
			if (GuiKit.Button(new Rect(a.x, by, a.width, 42f), "Update DB now")) _fewTagsRefreshRequested = true;

			GUI.Label(new Rect(a.x, by + 52f, a.width, 18f),
				"Last fetch: <color=#FFFFFF>" + FewTagsModule.LastFetchInfo + "</color>", _dim);
			GUI.Label(new Rect(a.x, by + 74f, a.width, 34f),
				"Community tag database by Fewdys (github.com/Fewdys/FewTags). Tags render above player nameplates.", _dim);
		}

		// Laid out as ONE top-to-bottom flow with no back-tracking. The previous version put the
		// ORBIT block at a fixed y that happened to be where CONTROLS already was, so the two drew
		// on top of each other. Every block below starts from the previous block's end.
		// ARCHIVE FAVOURITES — your unlimited favourites, wearable from in game.
		//
		// This is the fallback view. Injecting a category into VRChat's OWN avatar menu is still
		// unresolved (the list of categories lives on a VRC.Core.FavoriteArea instance that nothing
		// in the game appears to hold), so rather than block the feature on that, the list lives
		// here. Wearing works the same either way: it goes through the id-based clone strategy,
		// which is the one VRChat's own "change into avatar" button uses.
		private static Vector2 _favScroll;
		private static string _favFilter = "";

		// FAVORIS — every favourite type the account has, not just avatars.
		// The four kinds live behind one segmented selector rather than four tabs: they are the same
		// idea with a different noun, and the account holds all of them.
		private static void DrawFavorites(Rect a)
		{
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "VRCHAT ARCHIVE FAVOURITES", _header);

			if (!VaAuth.InsideClient)
			{
				GUI.Label(new Rect(a.x, a.y + 26f, a.width, 40f),
					"<color=#FFB86B>The VRChat Archive desktop client must be running and signed in.</color>" + "\n"
					+ "<color=#6E6690>Favourites are account data, so they are read through the client — the mod "
					+ "never holds your session.</color>", _dim);
				return;
			}

			_favKind = GuiKit.Segmented(new Rect(a.x, a.y + 22f, a.width, 32f),
				new[] { "Users", "Avatars", "Worlds", "Groups" }, _favKind);
			var sub = new Rect(a.x, a.y + 60f, a.width, a.height - 60f);

			switch (_favKind)
			{
				case 0: DrawKindFavorites(sub, Modules.KindFavoritesModule.Users, "user"); return;
				case 2: DrawKindFavorites(sub, Modules.KindFavoritesModule.Worlds, "world"); return;
				case 3: DrawKindFavorites(sub, Modules.KindFavoritesModule.Groups, "group"); return;
			}
			DrawAvatarFavorites(sub);
		}

		// Worlds / users / groups: the same card grid as avatars, fed by KindFavoritesModule.
		private static void DrawKindFavorites(Rect a, Modules.KindFavoritesModule.Provider p, string noun)
		{
			p.Touch();   // tells the provider this section is being looked at

			var rows = p.Items();
			string filt = (_favFilter ?? "").Trim();
			if (filt.Length > 0)
			{
				var keep = new System.Collections.Generic.List<Modules.KindFavoritesModule.Item>();
				foreach (var it in rows)
				{
					if (it == null) continue;
					if ((it.Name ?? "").IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0
						|| (it.Id ?? "").IndexOf(filt, StringComparison.OrdinalIgnoreCase) >= 0) keep.Add(it);
				}
				rows = keep;
			}

			GUI.Label(new Rect(a.x, a.y, 60f, 20f), "FILTER", _header);
			_favFilter = GUI.TextField(new Rect(a.x + 58f, a.y - 4f, a.width * 0.30f, 26f), _favFilter ?? "", 40);

			int pending = p.MetaPending;
			GUI.Label(new Rect(a.x + a.width - 320f, a.y, 320f, 20f),
				"<color=#6E6690>" + rows.Count + " / " + p.Count
				+ (pending > 0 ? "   ·   loading " + pending + " card(s)…" : "") + "</color>", _rowTagRight);

			float ly = a.y + 32f;
			var view = new Rect(a.x, ly, a.width, a.height - (ly - a.y) - 6f);
			GuiKit.RoundedFill(view, new Color(0.03f, 0.04f, 0.06f, 0.92f), 8f);

			const float cardW = 214f, cardH = 176f, pad = 10f;
			float inner = view.width - 16f;
			int cols = Mathf.Max(1, Mathf.FloorToInt((inner - pad) / (cardW + pad)));
			float step = cardW + pad;
			float left = Mathf.Max(pad, (inner - (cols * step - pad)) * 0.5f);
			int lines = (rows.Count + cols - 1) / cols;

			var content = new Rect(0f, 0f, inner, Mathf.Max(view.height, lines * (cardH + pad) + pad));
			_kindScroll = GUI.BeginScrollView(view, _kindScroll, content);

			if (rows.Count == 0)
				GUI.Label(new Rect(12f, 10f, content.width - 24f, 40f),
					p.Count == 0
						? "<color=#6E6690>no " + noun + " favourites yet — add some on the website</color>"
						: "<color=#6E6690>nothing matches that filter</color>", _dim);

			for (int i = 0; i < rows.Count; i++)
			{
				float rx = left + (i % cols) * step;
				float ry = pad + (i / cols) * (cardH + pad);
				if (ry + cardH < _kindScroll.y || ry > _kindScroll.y + view.height) continue;
				DrawKindCard(new Rect(rx, ry, cardW, cardH), rows[i]);
			}
			GUI.EndScrollView();
		}

		private static void DrawKindCard(Rect r, Modules.KindFavoritesModule.Item it)
		{
			GuiKit.RoundedBorder(r, new Color(0.055f, 0.075f, 0.105f, 0.96f),
				new Color(GuiKit.Accent.r, GuiKit.Accent.g, GuiKit.Accent.b, 0.20f), 8f, 1f);

			var img = new Rect(r.x + 8f, r.y + 8f, r.width - 16f, 108f);
			GuiKit.Fill(img, new Color(0.02f, 0.03f, 0.045f, 1f));
			if (it.ThumbState == 2 && it.Thumb != null)
				GUI.DrawTexture(img, it.Thumb, ScaleMode.ScaleAndCrop, false);
			else
				GUI.Label(img, it.ThumbState == 3 ? "no preview" : "…", _favMid);

			GUI.Label(new Rect(r.x + 8f, r.y + 120f, r.width - 16f, 18f),
				Trunc(string.IsNullOrEmpty(it.Name) ? it.Id : it.Name, 30), _favName);
			GUI.Label(new Rect(r.x + 8f, r.y + 138f, r.width - 16f, 16f),
				string.IsNullOrEmpty(it.Author) ? "<color=#33404F>" + Trunc(it.Id, 26) + "</color>"
												: "by " + Trunc(it.Author, 26), _favSub);

			if (GuiKit.Button(new Rect(r.x + 8f, r.y + r.height - 34f, r.width - 16f, 28f), "COPY ID"))
				GUIUtility.systemCopyBuffer = it.Id;
		}

		private static void DrawAvatarFavorites(Rect a)
		{

			// No toggle: the native category is on by default now. A switch whose only useful
			// position is ON is a step in the way, and it was drawing where it was easy to miss.
			// The status still reports it, because that is the part worth looking at.
			string favStatus = Trunc(Modules.FavoritesModule.LastStatus, 70);
			string nativeStatus = Modules.ArchiveCategoryModule.Status;
			if (!string.IsNullOrEmpty(nativeStatus) && nativeStatus != "off")
				favStatus += "   ·   <color=#C58BFF>" + Trunc(nativeStatus, 60) + "</color>";
			GUI.Label(new Rect(a.x, a.y, a.width - 320f, 18f), "<color=#6E6690>" + favStatus + "</color>", _dim);

			float cy = a.y + 24f;
			GUI.Label(new Rect(a.x, cy, 60f, 20f), "FILTER", _header);
			GUI.SetNextControlName("fav_filter");
			_favFilter = GUI.TextField(new Rect(a.x + 58f, cy - 4f, a.width * 0.30f, 26f), _favFilter ?? "", 40);
			if (GuiKit.Button(new Rect(a.x + a.width * 0.34f, cy - 4f, 130f, 26f), "REFRESH"))
				_ = Modules.FavoritesModule.RefreshAsync();

			var all = Modules.FavoritesModule.Favourites();
			string f = (_favFilter ?? "").Trim();
			var rows = new System.Collections.Generic.List<Modules.FavoritesModule.Fav>();
			foreach (var it in all)
			{
				if (it == null) continue;
				if (f.Length > 0
					&& (it.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0
					&& (it.Author ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0
					&& (it.Id ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
				rows.Add(it);
			}

			int pending = Modules.FavoritesModule.MetaPending;
			GUI.Label(new Rect(a.x + a.width - 300f, cy, 300f, 20f),
				"<color=#6E6690>" + rows.Count + " / " + all.Count
				+ (pending > 0 ? "   ·   loading " + pending + " card(s)…" : "") + "</color>", _rowTagRight);

			float ly = cy + 32f;
			var view = new Rect(a.x, ly, a.width, a.height - (ly - a.y) - 6f);
			GuiKit.RoundedFill(view, new Color(0.03f, 0.04f, 0.06f, 0.92f), 8f);

			// A grid of cards, not a list of ids: the point of a favourite is the avatar, so the
			// card shows the avatar. Column count follows the panel width instead of being fixed,
			// because the menu is resized to the screen on open.
			const float cardW = 214f, cardH = 202f, pad = 10f;
			float inner = view.width - 16f;
			int cols = Mathf.Max(1, Mathf.FloorToInt((inner - pad) / (cardW + pad)));
			float step = cardW + pad;
			float left = Mathf.Max(pad, (inner - (cols * step - pad)) * 0.5f);
			int lines = (rows.Count + cols - 1) / cols;

			var content = new Rect(0f, 0f, inner, Mathf.Max(view.height, lines * (cardH + pad) + pad));
			_favScroll = GUI.BeginScrollView(view, _favScroll, content);

			if (rows.Count == 0)
				GUI.Label(new Rect(12f, 10f, content.width - 24f, 20f),
					all.Count == 0
						? "<color=#6E6690>no Archive favourites yet — add some on the website</color>"
						: "<color=#6E6690>nothing matches that filter</color>", _dim);

			for (int i = 0; i < rows.Count; i++)
			{
				float rx = left + (i % cols) * step;
				float ry = pad + (i / cols) * (cardH + pad);
				// Cull off-screen cards: this is also what keeps the thumbnail fetches to what is
				// actually being looked at, since a card only asks for its image while it draws.
				if (ry + cardH < _favScroll.y || ry > _favScroll.y + view.height) continue;
				DrawFavCard(new Rect(rx, ry, cardW, cardH), rows[i]);
			}
			GUI.EndScrollView();
		}

		private static void DrawFavCard(Rect r, Modules.FavoritesModule.Fav it)
		{
			GuiKit.RoundedBorder(r, new Color(0.055f, 0.075f, 0.105f, 0.96f),
				new Color(GuiKit.Accent.r, GuiKit.Accent.g, GuiKit.Accent.b, 0.20f), 8f, 1f);

			// ---- thumbnail
			var img = new Rect(r.x + 8f, r.y + 8f, r.width - 16f, 108f);
			GuiKit.Fill(img, new Color(0.02f, 0.03f, 0.045f, 1f));
			Modules.FavoritesModule.RequestThumb(it);
			if (it.ThumbState == 2 && it.Thumb != null)
				GUI.DrawTexture(img, it.Thumb, ScaleMode.ScaleAndCrop, false);
			else
				GUI.Label(img, it.ThumbState == 3 ? "no preview" : "…", _favMid);

			// ---- name / author
			GUI.Label(new Rect(r.x + 8f, r.y + 120f, r.width - 16f, 18f), Trunc(it.Name, 30), _favName);
			GUI.Label(new Rect(r.x + 8f, r.y + 138f, r.width - 16f, 16f),
				string.IsNullOrEmpty(it.Author) ? "<color=#33404F>" + Trunc(it.Id, 26) + "</color>"
											    : "by " + Trunc(it.Author, 26), _favSub);

			// ---- actions
			float by = r.y + r.height - 38f;
			if (GuiKit.Button(new Rect(r.x + 8f, by, 92f, 30f), "WEAR"))
				VaTagsModule.WearById(it.Id, it.Name);
			if (GuiKit.Button(new Rect(r.x + 104f, by, 48f, 30f), "ID"))
				GUIUtility.systemCopyBuffer = it.Id;
			if (GuiKit.Button(new Rect(r.x + 156f, by, r.width - 164f, 30f), "REMOVE"))
				_ = Modules.FavoritesModule.RemoveAsync(it.Id);
		}

		private static void DrawMovement(Rect a)
		{
			float gap = 14f;
			float colW = (a.width - gap) / 2f;
			float rx = a.x + colW + gap;      // right column

			// ---- FLY ----------------------------------------------------------------------
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "FLY", _header);
			ModConfig.FlyEnabled.Value = GuiKit.Toggle(new Rect(a.x, a.y + 24f, colW, 40f),
				"Fly (Ctrl+F)", ModConfig.FlyEnabled.Value);
			// Noclip: own toggle, off by default. Switching it on turns fly on too (MovementModule),
			// since without fly you'd just fall through the floor.
			ModConfig.NoclipEnabled.Value = GuiKit.Toggle(new Rect(rx, a.y + 24f, colW, 40f),
				"Noclip (auto with fly)", ModConfig.NoclipEnabled.Value);

			ModConfig.ClickTpEnabled.Value = GuiKit.Toggle(new Rect(a.x, a.y + 72f, a.width, 40f),
				"Click-Teleport (hold RMB + LMB)", ModConfig.ClickTpEnabled.Value);

			Slider(a.x, a.y + 118f, colW, "Fly Speed", ModConfig.FlySpeed.Value, 2, 40, v => ModConfig.FlySpeed.Value = v, "F1");
			Slider(rx,   a.y + 118f, colW, "Boost Speed (Shift)", ModConfig.FlyBoostSpeed.Value, 4, 80, v => ModConfig.FlyBoostSpeed.Value = v, "F1");
			Slider(a.x, a.y + 164f, colW, "Rotate Speed (arrows)", ModConfig.FlyRotateSpeed.Value, 15, 360, v => ModConfig.FlyRotateSpeed.Value = v, "F0");
			Slider(rx,   a.y + 164f, colW, "TP Max Dist (m)", ModConfig.ClickTpMaxDistance.Value, 10, 300, v => ModConfig.ClickTpMaxDistance.Value = v, "F0");

			// ---- ROTATOR ------------------------------------------------------------------
			// The two axes the arrow-key turning above deliberately refuses. Switching it ON also
			// widens VRChat's neck clamp, and that is the half that makes upside-down usable rather
			// than merely visible in a mirror: without it the body tilts and the camera stays level.
			float ry = a.y + 210f;
			GUI.Label(new Rect(a.x, ry, a.width, 16f), "ROTATOR — TILT YOURSELF", _header);
			bool rotWas = Modules.PlayerRotatorModule.Active;
			if (GuiKit.Toggle(new Rect(a.x, ry + 24f, colW, 40f),
				rotWas ? "Rotator ON (RShift+R)" : "Rotator (RShift+R)", rotWas) != rotWas)
				Modules.PlayerRotatorModule.Toggle();
			if (GuiKit.Button(new Rect(rx, ry + 24f, colW, 40f), "FLIP UPSIDE DOWN  (RShift+F)"))
				Modules.PlayerRotatorModule.Flip();

			ModConfig.RotatorFreeLook.Value = GuiKit.Toggle(new Rect(a.x, ry + 70f, colW, 40f),
				"Free look (unclamp mouse pitch)", ModConfig.RotatorFreeLook.Value);
			if (GuiKit.Button(new Rect(rx, ry + 70f, colW, 40f), "BACK UPRIGHT  (RShift+Backspace)"))
				Modules.PlayerRotatorModule.ResetUpright();

			Slider(a.x, ry + 116f, colW, "Tilt Speed", ModConfig.RotatorSpeed.Value, 5, 720, v => ModConfig.RotatorSpeed.Value = v, "F0");
			Slider(rx,   ry + 116f, colW, "Free-look reach (°)", ModConfig.RotatorNeckLimit.Value, 90, 720, v => ModConfig.RotatorNeckLimit.Value = v, "F0");

			GUI.Label(new Rect(a.x, ry + 158f, a.width, 20f),
				"Tilt: <color=#FFFFFF>↑ ↓</color> pitch · <color=#FFFFFF>PgUp/PgDn</color> roll · the neck clamp goes back exactly as found when you switch it off", _dim);
			GUI.Label(new Rect(a.x, ry + 178f, a.width, 20f),
				"<color=#FFFFFF>" + Modules.PlayerRotatorModule.Status + "</color>", _dim);

			// ---- CONTROLS -----------------------------------------------------------------
			float cy = a.y + 410f;
			GUI.Label(new Rect(a.x, cy, a.width, 16f), "CONTROLS", _header);
			GUI.Label(new Rect(a.x, cy + 20f, a.width, 20f), "Fly: <color=#FFFFFF>Ctrl+F</color> toggles it · noclip switches on with it automatically (toggle it off above to keep collision)", _dim);
			GUI.Label(new Rect(a.x, cy + 40f, a.width, 20f), "Move: <color=#FFFFFF>WASD</color> + <color=#FFFFFF>E/Q</color> · <color=#FFFFFF>Shift</color> boost · Rotate <color=#FFFFFF>← → ↑ ↓</color> · Teleport: <color=#FFFFFF>RMB+LMB</color>", _dim);
			GUI.Label(new Rect(a.x, cy + 60f, a.width, 20f), "Local movement only — affects your own avatar, no one else.", _dim);

		}

		private static void DrawEsp(Rect a)
		{
			float gap = 14f;
			float colW = (a.width - gap) / 2f;

			// EVERY SWITCH HERE IS ITS OWN THING. There is no master ESP switch: each glow below turns
			// on and off by itself, and off means off — the modules un-light what they lit on the same
			// frame the switch flips. The 2D screen box (box / name / distance) is one more independent
			// switch, back by the owner's request.
			GUI.Label(new Rect(a.x, a.y, a.width, 18f),
				"<color=#6E6690>Each switch is independent — one thing, one switch. "
				+ "The radar has its own switch in the <color=#FFFFFF>OVERLAYS</color> tab.</color>", _dim);

			float hy = a.y + 30f;
			GUI.Label(new Rect(a.x, hy, a.width, 16f), "GLOWS", _header);
			float ty = hy + 24f;
			ModConfig.EspCapsule.Value = GuiKit.Toggle(new Rect(a.x, ty, colW, 40f),
				"Player capsules (3D glow)", ModConfig.EspCapsule.Value);
			ModConfig.EspHighlight.Value = GuiKit.Toggle(new Rect(a.x + colW + gap, ty, colW, 40f),
				"Mesh glow (avatar outline)", ModConfig.EspHighlight.Value);
			ModConfig.EspPortals.Value = GuiKit.Toggle(new Rect(a.x, ty + 48f, colW, 40f),
				"Portals", ModConfig.EspPortals.Value);
			ModConfig.EspItems.Value = GuiKit.Toggle(new Rect(a.x + colW + gap, ty + 48f, colW, 40f),
				"Items (pickups)", ModConfig.EspItems.Value);
			// Capsule-only: whether walls hide the capsules. CapsuleEspModule re-applies it to the
			// capsules already built, so it takes effect on the frame it is flipped.
			ModConfig.EspThroughWalls.Value = GuiKit.Toggle(new Rect(a.x, ty + 96f, colW, 40f),
				"Capsules through walls", ModConfig.EspThroughWalls.Value);
			// The 2D screen ESP: a box around each player with name and distance, drawn on screen.
			ModConfig.EspEnabled.Value = GuiKit.Toggle(new Rect(a.x, ty + 144f, colW, 40f),
				"Screen box (2D box · name · distance)", ModConfig.EspEnabled.Value);
			ModConfig.EspSkeleton.Value = GuiKit.Toggle(new Rect(a.x + colW + gap, ty + 144f, colW, 40f),
				"Skeleton instead of box", ModConfig.EspSkeleton.Value);

			float sy = ty + 202f;
			Slider(a.x, sy, colW, "Capsule range (m)", ModConfig.EspMaxDistance.Value, 10, 200, v => ModConfig.EspMaxDistance.Value = v, "F0");
			Slider(a.x + colW + gap, sy, colW, "Radar range (m)", ModConfig.RadarRange.Value, 10, 200, v => ModConfig.RadarRange.Value = v, "F0");
			if (ModConfig.RadarMap.Value)
				Slider(a.x, sy + 46f, colW, "Map opacity", ModConfig.RadarMapOpacity.Value, 0.1f, 1f,
					v => ModConfig.RadarMapOpacity.Value = v, "F2");

			float ly = sy + 98f;
			GUI.Label(new Rect(a.x, ly, a.width, 16f), "TRUST COLORS", _header);

			// Two columns kept CLOSE together: at half the panel's width they read as two unrelated
			// lists with a field of nothing between them. 250px is enough for the longest label.
			float legX2 = a.x + 250f;
			LegendRow(a.x,     ly + 24f, new Color(0.84f, 0.84f, 0.88f), "Visitor");
			LegendRow(a.x,     ly + 44f, new Color(0.09f, 0.47f, 1.00f), "New User");
			LegendRow(a.x,     ly + 64f, new Color(0.17f, 0.81f, 0.36f), "User");
			LegendRow(a.x,     ly + 84f, new Color(1.00f, 0.48f, 0.26f), "Known User");
			LegendRow(legX2,   ly + 24f, new Color(0.51f, 0.26f, 0.90f), "Trusted User");
			LegendRow(legX2,   ly + 44f, new Color(1.00f, 0.12f, 0.12f), "VRChat Team");
			LegendRow(legX2,   ly + 64f, new Color(0.55f, 0.00f, 0.00f), "Nuisance");

			// A legend row occupies its y to y+16, so the LAST one ends at ly+100. The note used to
			// sit at ly+96 and printed straight through "Known User".
			GUI.Label(new Rect(a.x, ly + 112f, a.width, 34f), "Visualization only — glows on what your client already renders, no targeting or tracking.", _dim);
		}

		private static void LegendRow(float x, float y, Color c, string label)
		{
			GuiKit.Fill(new Rect(x, y + 3f, 12f, 12f), c);
			GUI.Label(new Rect(x + 20f, y, 220f, 16f), label, _dim);
		}

		// UDON CONSOLE — live feed of the Udon events happening around you.
		private static Vector2 _udonScroll;
		private static bool _udonStick = true;
		private static bool _udonManager;
		private static string _umFilter = "";
		private static Vector2 _umScroll;
		private static Modules.UdonManagerModule.Entry _umSel;
		private static System.Collections.Generic.List<string> _umEvents;

		// UDON MANAGER — switch a world's scripts off on YOUR client, and run their events locally.
		private static float _umAutoScan;

		private static void DrawUdonManager(Rect a)
		{
			float gap = 12f;

			// SCANS ITSELF. It used to open empty and stay empty until you found the SCAN button,
			// which reads as "this feature is broken" rather than "press that". It fills on open and
			// refreshes every 10s while you are looking at it; SCAN is still there for right-now.
			if (Modules.UdonManagerModule.Count == 0 && VaClock.Now > _umAutoScan)
			{
				_umAutoScan = VaClock.Now + 10f;
				Modules.UdonManagerModule.Rescan();
			}

			GUI.Label(new Rect(a.x, a.y, 60f, 20f), "FILTER", _header);
			GUI.SetNextControlName("um_filter");
			_umFilter = GUI.TextField(new Rect(a.x + 58f, a.y - 4f, a.width * 0.34f, 26f), _umFilter ?? "", 48);
			if (GuiKit.Button(new Rect(a.x + a.width * 0.38f, a.y - 4f, 120f, 26f), "SCAN"))
				Modules.UdonManagerModule.Rescan();
			if (GuiKit.Button(new Rect(a.x + a.width * 0.38f + 128f, a.y - 4f, 150f, 26f), "RESTORE ALL"))
				Modules.UdonManagerModule.RestoreAll();

			GUI.Label(new Rect(a.x + a.width - 340f, a.y, 340f, 20f),
				"<color=#6E6690>" + Trunc(Modules.UdonManagerModule.Status, 60)
				+ (Modules.UdonManagerModule.DisabledByUs > 0
					? "   ·   <color=#FF7A7A>" + Modules.UdonManagerModule.DisabledByUs + " off</color>"
					: "") + "</color>", _rowTagRight);

			var all = Modules.UdonManagerModule.Snapshot();
			string f = (_umFilter ?? "").Trim();
			var rows = new System.Collections.Generic.List<Modules.UdonManagerModule.Entry>();
			foreach (var e in all)
			{
				if (e == null || e.B == null) continue;
				if (f.Length > 0 && (e.Path ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
				rows.Add(e);
			}

			float by = a.y + 30f;
			if (GuiKit.Button(new Rect(a.x, by, 200f, 26f), "DISABLE ALL SHOWN"))
				Modules.UdonManagerModule.SetMany(rows, false);
			if (GuiKit.Button(new Rect(a.x + 208f, by, 200f, 26f), "ENABLE ALL SHOWN"))
				Modules.UdonManagerModule.SetMany(rows, true);
			GUI.Label(new Rect(a.x + 420f, by + 3f, a.width - 420f, 20f),
				"<color=#6E6690>" + rows.Count + " / " + all.Count
				+ " — local only: switching a script off stops it in YOUR client and can desync you</color>", _dim);

			// ---- list (left) + detail (right)
			float ly = by + 34f;
			float listW = a.width * 0.58f;
			var view = new Rect(a.x, ly, listW, a.height - (ly - a.y) - 6f);
			GuiKit.RoundedFill(view, new Color(0.03f, 0.04f, 0.06f, 0.92f), 8f);

			const float rowH = 24f;
			var content = new Rect(0f, 0f, view.width - 16f, Mathf.Max(view.height, rows.Count * rowH + 6f));
			_umScroll = GUI.BeginScrollView(view, _umScroll, content);
			for (int i = 0; i < rows.Count; i++)
			{
				var e = rows[i];
				float ry = 3f + i * rowH;
				if (ry + rowH < _umScroll.y || ry > _umScroll.y + view.height) continue;
				if (_umSel == e) GuiKit.Fill(new Rect(2f, ry - 1f, content.width - 4f, rowH - 2f), new Color(0.36f, 0.20f, 0.62f, 0.55f));
				else if ((i & 1) == 0) GuiKit.Fill(new Rect(2f, ry - 1f, content.width - 4f, rowH - 2f), new Color(1f, 1f, 1f, 0.028f));

				bool on = true;
				try { on = e.B != null && e.B.enabled; } catch { }

				if (GuiKit.Button(new Rect(6f, ry + 1f, 54f, 20f), on ? "ON" : "OFF"))
					Modules.UdonManagerModule.SetEnabled(e, !on);

				if (GUI.Button(new Rect(64f, ry, content.width - 130f, rowH), GUIContent.none, GUIStyle.none))
				{
					_umSel = e;
					_umEvents = Modules.UdonManagerModule.EntryPoints(e);
				}
				GUI.Label(new Rect(66f, ry + 2f, content.width - 132f, 20f),
					(on ? "<color=#EAF6FF>" : "<color=#FF7A7A>") + Trunc(e.Short, 44) + "</color>", _rowTag);
				GUI.Label(new Rect(content.width - 62f, ry + 2f, 56f, 20f),
					"<color=#6E6690>" + e.Dist.ToString("F0") + "m</color>", _rowTagRight);
			}
			GUI.EndScrollView();

			// ---- detail
			float dx = a.x + listW + gap;
			float dw = a.width - listW - gap;
			var dv = new Rect(dx, ly, dw, view.height);
			GuiKit.RoundedFill(dv, new Color(0.03f, 0.04f, 0.06f, 0.92f), 8f);

			if (_umSel == null || _umSel.B == null)
			{
				GUI.Label(new Rect(dx + 12f, ly + 12f, dw - 24f, 60f),
					"<color=#EAF6FF>Click an object on the left.</color>\n"
					+ "<color=#6E6690>You get every event its script exposes, and a RUN button for each one. "
					+ "ON/OFF switches that script off in your client.</color>", _dim);
				return;
			}

			GUI.Label(new Rect(dx + 12f, ly + 10f, dw - 24f, 36f),
				"<color=#EAF6FF>" + Trunc(_umSel.Path, 150) + "</color>", _dim);

			GUI.Label(new Rect(dx + 12f, ly + 52f, dw - 24f, 16f), "EVENTS", _header);
			GUI.Label(new Rect(dx + 12f, ly + 70f, dw - 24f, 34f),
				"<color=#6E6690>RUN fires the event on <color=#FFFFFF>your client only</color>. Nothing is sent to "
				+ "the instance — other players' worlds do not move.</color>", _dim);

			if (_umEvents == null || _umEvents.Count == 0)
			{
				GUI.Label(new Rect(dx + 12f, ly + 108f, dw - 24f, 20f), "<color=#6E6690>no entry points reported</color>", _dim);
				return;
			}

			float ey = ly + 110f;
			for (int i = 0; i < _umEvents.Count && ey < dv.yMax - 26f; i++, ey += 26f)
			{
				string ev = _umEvents[i];
				bool global = Modules.UdonManagerModule.IsGlobalEvent(_umSel, ev);

				// RUN is always local — nothing leaves this client. RUN GLB appears ONLY for
				// network-eligible (non-underscore) events and broadcasts to EVERYONE via VRChat's
				// own networking; it is coloured apart so the instance-wide one is never a misclick.
				float bx = dx + 12f;
				if (GuiKit.Button(new Rect(bx, ey, 50f, 22f), "RUN"))
					Modules.UdonManagerModule.RunLocal(_umSel, ev);
				bx += 54f;
				if (global)
				{
					if (GuiKit.Button(new Rect(bx, ey, 68f, 22f), "<color=#FFB4E6>RUN GLB</color>"))
						Modules.UdonManagerModule.RunGlobal(_umSel, ev);
					bx += 72f;
				}

				GUI.Label(new Rect(bx, ey + 1f, 60f, 20f),
					global ? "<color=#FF7AD5>GLOBAL</color>" : "<color=#8B85A8>LOCAL</color>", _dim);
				GUI.Label(new Rect(bx + 62f, ey + 1f, dw - (bx - dx) - 74f, 20f),
					"<color=#A99FC4>" + Trunc(ev, 46) + "</color>", _dim);
			}
		}

		private static void DrawUdon(Rect a)
		{
			float gap = 14f, colW = (a.width - gap) / 2f;

			// TWO MODES, said once and plainly. The old header was seven toggles in a row where
			// PANIC — which stops every script in the world — carried the same visual weight as
			// "Follow". Nothing told you what this tab was for, or that one of those switches was
			// destructive.
			//
			//   OBJECTS  — what is in this world, what events it exposes, and switching it off
			//   EVENTS   — what it is doing right now, live
			// A SEGMENTED SELECTOR, not two toggles. As toggles these read as two unrelated
			// switches — "OBJECTS [OFF] / EVENTS [ON]" says nothing about them being alternatives,
			// and nothing hinted that turning one on turns the other off.
			_udonManager = GuiKit.Segmented(new Rect(a.x, a.y, a.width, 40f),
				new[] { "World scripts", "Live activity" }, _udonManager ? 0 : 1) == 0;

			// PROTECTION, on its own line and labelled as such. PANIC shouts when it is on, because
			// with it on nothing in any world works and that is very easy to forget you did.
			float py = a.y + 48f;
			bool panicOn = ModConfig.UdonBlockAll.Value;
			GUI.Label(new Rect(a.x, py + 8f, 92f, 20f), "PROTECTION", _header);
			// Same rule as the other panel: only write when the user actually flipped it.
			bool crashHere2 = GuiKit.Toggle(new Rect(a.x + 96f, py, 220f, 34f),
				"Anti-crasher", ModConfig.UdonBlockCrashers.Value);
			if (crashHere2 != ModConfig.UdonBlockCrashers.Value)
				ModConfig.UdonBlockCrashers.Value = crashHere2;
			// Danger-styled: a switch that stops every script in the world must not look like the
			// one next to it that only blocks crashers.
			bool panic = GuiKit.DangerToggle(new Rect(a.x + 324f, py, 230f, 34f), "Stop ALL world scripts", panicOn);
			if (panic != panicOn)
			{
				ModConfig.UdonBlockAll.Value = panic;
				VaTagsModule.LastStatus = panic
					? "PANIC: world scripts are no longer run by your client — doors, pens and videos will stop working"
					: "✓ world scripts running normally again";
			}
			if (panicOn)
				GUI.Label(new Rect(a.x + 536f, py + 8f, a.width - 536f, 20f),
					"<color=#FF7A7A>PANIC is ON — every world script is dead for you ("
					+ UdonLogModule.BlockedTotal + " blocked). Pickups, doors and videos will not respond.</color>", _dim);

			if (_udonManager) { DrawUdonManager(new Rect(a.x, py + 44f, a.width, a.height - (py + 44f - a.y))); return; }

			// ---- EVENTS mode: two clean rows, no overlap.
			//
			// The old header stacked a three-way source selector and a per-frame toggle in the same
			// slot, so "NETWORK" was clipped and "PER-FRAME" drew on top of it. Row 1 is what to
			// RECORD; row 2 is which SOURCE to SHOW. Kept apart because they are different questions.
			float r1 = py + 44f, r2 = py + 82f;
			float third = (a.width - gap * 2f) / 3f;

			ModConfig.UdonLogEnabled.Value = GuiKit.Toggle(new Rect(a.x, r1, third, 34f),
				"Record activity", ModConfig.UdonLogEnabled.Value);
			ModConfig.UdonLogFrameEvents.Value = GuiKit.Toggle(new Rect(a.x + third + gap, r1, third, 34f),
				"Include every-frame spam", ModConfig.UdonLogFrameEvents.Value);
			ModConfig.UdonLogOverlay.Value = GuiKit.Toggle(new Rect(a.x + 2f * (third + gap), r1, third, 34f),
				"Show on screen", ModConfig.UdonLogOverlay.Value);

			// SOURCE: one three-way choice, drawn as one control. Three separate ON/OFF toggles made
			// "both" look like an accident and "network only" impossible to find.
			GUI.Label(new Rect(a.x, r2 + 7f, 58f, 20f), "SHOW", _header);
			bool su = ModConfig.EventsShowUdon.Value, sn = ModConfig.EventsShowNetwork.Value;
			int src = (su && sn) ? 2 : (sn ? 1 : 0);
			int pick = GuiKit.Segmented(new Rect(a.x + 62f, r2, a.width - 62f, 34f),
				new[] { "World scripts", "Networking", "Both" }, src);
			if (pick != src)
			{
				ModConfig.EventsShowUdon.Value = pick == 0 || pick == 2;
				ModConfig.EventsShowNetwork.Value = pick == 1 || pick == 2;
			}

			float y = py + 126f;
			var cs = NetworkLogModule.CodeStats();
			GUI.Label(new Rect(a.x, y, a.width, 16f),
				$"udon <color=#7CFF9E>{UdonLogModule.PerSecond}</color>/s   ·   <color=#7FB0FF>{UdonLogModule.TotalSeen}</color> seen"
				+ (NetworkLogModule.HookAttached
					? $"   ·   net <color=#7FD4FF>{NetworkLogModule.PerSecond}</color>/s over <color=#7FD4FF>{cs.codes}</color> code(s), <color=#8FA3B8>{cs.bulk}</color> bulk"
					: "   ·   <color=#8FA3B8>network hook not attached</color>")
				+ (UdonLogModule.BlockedTotal > 0
					? $"   ·   <color=#FF7A7A>{UdonLogModule.BlockedTotal}</color> blocked (last: {Trunc(UdonLogModule.LastBlocked, 24)})"
					: ""), _dim);

			// controls
			float cy = y + 22f;
			GUI.Label(new Rect(a.x, cy, 60f, 20f), "FILTER", _header);
			GUI.SetNextControlName("udon_filter");
			UdonLogModule.Filter = GUI.TextField(new Rect(a.x + 58f, cy - 4f, colW - 58f, 26f), UdonLogModule.Filter ?? "", 40);
			if (string.IsNullOrEmpty(UdonLogModule.Filter))
				GUI.Label(new Rect(a.x + 66f, cy, colW - 70f, 20f), "<color=#4A5568>event, object or player…</color>", _dim);

			float bx = a.x + colW + gap;
			float bw = (colW - 16f) / 3f;
			if (GuiKit.Button(new Rect(bx, cy - 4f, bw, 26f), UdonLogModule.Paused ? "RESUME" : "PAUSE")) UdonLogModule.Paused = !UdonLogModule.Paused;
			_udonStick = GuiKit.Toggle(new Rect(bx + bw + 8f, cy - 4f, bw, 26f), "Auto-scroll", _udonStick);
			if (GuiKit.Button(new Rect(bx + (bw + 8f) * 2f, cy - 4f, bw, 26f), "CLEAR")) { UdonLogModule.Clear(); NetworkLogModule.Clear(); }

			// The rate filter. VRChat publishes no meaning for its Photon event codes and the shipped
			// assemblies carry no enum for them, so the console classifies by MEASURED RATE instead of
			// by a guessed name: continuous serialisation runs at hundreds a second, a real event is
			// rare. Self-calibrating per world, and it cannot mislabel an event it does not understand.
			ModConfig.NetworkInterestingOnly.Value = GuiKit.Toggle(new Rect(a.x, cy + 26f, colW * 0.62f, 28f),
				"Hide constant chatter", ModConfig.NetworkInterestingOnly.Value);
			Slider(a.x + colW * 0.66f, cy + 28f, colW * 0.34f, "Chatter threshold /s",
				ModConfig.NetworkBulkPerSecond.Value, 5, 200, v => ModConfig.NetworkBulkPerSecond.Value = (int)v);
			GUI.Label(new Rect(bx, cy + 30f, colW, 20f),
				"<color=#6E6690>codes are not published — rows show the code, its payload and the sender</color>", _dim);

			// log view — crisp dark console: fixed column header band + zebra-striped rows
			float ly = cy + 62f;
			var view = new Rect(a.x, ly, a.width, a.height - (ly - a.y) - 6f);
			GuiKit.RoundedFill(view, new Color(0.03f, 0.04f, 0.06f, 0.92f), 8f);

			// shared column geometry (header band and every row line up on these)
			const float head = 23f, rowH = 20f;
			float xTime = 12f, xWho = 74f, xObj = 232f, xEv = 410f;
			float innerW = view.width - 16f;

			// fixed header band — stays put while the rows scroll under it
			GuiKit.Fill(new Rect(view.x + 1f, view.y + 1f, view.width - 2f, head), new Color(1f, 1f, 1f, 0.05f));
			GuiKit.Fill(new Rect(view.x + 6f, view.y + head, view.width - 12f, 1f), new Color(1f, 1f, 1f, 0.09f));
			GUI.Label(new Rect(view.x + xTime, view.y + 4f, 60f,  15f), "<color=#48555F>TIME</color>",   _dim);
			GUI.Label(new Rect(view.x + xWho,  view.y + 4f, 150f, 15f), "<color=#48555F>WHO</color>",    _dim);
			GUI.Label(new Rect(view.x + xObj,  view.y + 4f, 170f, 15f), "<color=#48555F>OBJECT</color>", _dim);
			GUI.Label(new Rect(view.x + xEv,   view.y + 4f, 120f, 15f), "<color=#48555F>EVENT</color>",  _dim);

			var rows = UdonLogModule.MergedRows();
			var body = new Rect(view.x, view.y + head + 1f, view.width, view.height - head - 2f);
			var content = new Rect(0f, 0f, body.width - 16f, Mathf.Max(body.height, rows.Count * rowH + 6f));
			if (_udonStick && !UdonLogModule.Paused) _udonScroll.y = Mathf.Max(0f, content.height - body.height);
			_udonScroll = GUI.BeginScrollView(body, _udonScroll, content);

			if (rows.Count == 0)
				GUI.Label(new Rect(xTime, 8f, content.width - 20f, 20f),
					ModConfig.UdonLogEnabled.Value
						? "<color=#6E6690>no event yet — join a world with interactive objects, or switch on Network events</color>"
						: "<color=#6E6690>console is off</color>", _dim);

			for (int i = 0; i < rows.Count; i++)
			{
				var e = rows[i];
				float ry = 3f + i * rowH;
				if (ry + rowH < _udonScroll.y || ry > _udonScroll.y + body.height) continue;   // cull offscreen rows
				if ((i & 1) == 0) GuiKit.Fill(new Rect(2f, ry - 1f, content.width - 4f, rowH), new Color(1f, 1f, 1f, 0.028f));

				GUI.Label(new Rect(xTime, ry, 58f, 18f), "<color=#566677>" + e.Clock + "</color>", _rowTag);
				string who = string.IsNullOrEmpty(e.User) ? "<color=#6E6690>world</color>"
					: e.Net ? "<color=#7FB0FF>" + Trunc(e.User, 20) + "</color>"
					: "<color=" + RosterColor(e.User) + ">" + Trunc(e.User, 20) + "</color>";
				GUI.Label(new Rect(xWho, ry, 154f, 18f), who, _rowTag);
				GUI.Label(new Rect(xObj, ry, 174f, 18f), "<color=#C58BFF>" + Trunc(e.Obj, 24) + "</color>", _rowTag);
				// Network rows blue, Udon rows green: at a glance you know whether you are looking at
				// a world script or at traffic off the wire.
				string ev = (e.Net ? "<color=#73BEFF>" : "<color=#7CFF9E>") + Trunc(e.Ev, 46) + "</color>";
				if (e.Repeats > 1) ev += " <color=#FFB86B>×" + e.Repeats + "</color>";
				GUI.Label(new Rect(xEv, ry, content.width - xEv - 6f, 18f), ev, _rowTag);
			}
			GUI.EndScrollView();
		}

		// Trust colour for a display name, taken from the roster the PLAYERS tab maintains
		// (so the console costs nothing per event to colour its rows).
		//
		// ONE DICTIONARY HIT PER ROW, NOT A FULL ROSTER SCAN (2026-09-13).
		//
		// This walked all 35 roster entries for EVERY console row. The Udon/Network console draws
		// dozens of rows, IMGUI repaints several times per frame, and the comparison is a
		// case-insensitive string compare — so colouring the console cost tens of thousands of
		// string compares a second in a full instance. That is quadratic work for a lookup.
		//
		// The map is rebuilt only when the roster actually changed (RosterVersion is bumped by
		// RefreshRoster), so the answer is byte-for-byte what the scan returned — first match wins,
		// same as the old loop — and the per-row cost becomes a hash lookup.
		private static readonly System.Collections.Generic.Dictionary<string, string> _rosterColors =
			new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private static int _rosterColorsVersion = -1;

		private static string RosterColor(string name)
		{
			if (string.IsNullOrEmpty(name)) return "#8FA3B8";
			int v = VaTagsModule.RosterVersion;
			if (v != _rosterColorsVersion)
			{
				_rosterColorsVersion = v;
				_rosterColors.Clear();
				foreach (var p in VaTagsModule.Roster)
				{
					if (p == null || string.IsNullOrEmpty(p.Name)) continue;
					// FIRST match wins, exactly like the loop this replaces: two players can share a
					// display name, and the old code returned the earlier one.
					if (_rosterColors.ContainsKey(p.Name)) continue;
					_rosterColors[p.Name] = p.IsLocal ? "#7CFF9E" : (p.TrustColor ?? "#CCCCCC");
				}
			}
			return _rosterColors.TryGetValue(name, out string c) ? c : "#8FA3B8";
		}


		private static void DrawSettings(Rect a)
		{
			// The ACCENT COLOR swatches and MENU SCALE slider that opened this page are gone with
			// their settings (UI/AccentPreset, UI/Scale, removed 2026-09-01): they only styled this
			// sealed menu, and the desktop client was showing them as live controls.
			// AUDIO — spawn stinger played once when you finish loading into an instance
			float au = a.y;
			GUI.Label(new Rect(a.x, au, a.width, 16f), "SPAWN SOUND", _header);
			float half = (a.width - 8f) / 2f;
			bool wantSpawn = GuiKit.Toggle(new Rect(a.x, au + 22f, half, 36f),
				"The Spawn Dark Squad (on join)", ModConfig.SpawnSoundEnabled.Value);
			if (!wantSpawn && ModConfig.SpawnSoundEnabled.Value) Modules.SpawnSoundModule.StopNow();
			ModConfig.SpawnSoundEnabled.Value = wantSpawn;
			Slider(a.x + half + 8f, au + 26f, half - 66f, "Volume", ModConfig.SpawnSoundVolume.Value, 0f, 1f,
				v => ModConfig.SpawnSoundVolume.Value = Mathf.Round(v * 20f) / 20f, "F2");
			if (GuiKit.Button(new Rect(a.x + a.width - 58f, au + 22f, 58f, 30f),
				Modules.SpawnSoundModule.IsPlaying ? "Stop" : "Test"))
			{
				if (Modules.SpawnSoundModule.IsPlaying) Modules.SpawnSoundModule.StopNow();
				else Modules.SpawnSoundModule.PlayNow();
			}

			// MEMBER JOIN BANNER — style + a Test button that fires the notification on demand
			float ny = au + 66f;
			GUI.Label(new Rect(a.x, ny, a.width, 16f), "MEMBER JOIN BANNER", _header);
			ModConfig.MemberNotifyRgb.Value = GuiKit.Toggle(new Rect(a.x, ny + 22f, half, 36f),
				ModConfig.MemberNotifyRgb.Value ? "Style: RGB rainbow" : "Style: pink→violet", ModConfig.MemberNotifyRgb.Value);
			if (GuiKit.Button(new Rect(a.x + half + 8f, ny + 24f, half, 32f), "Test notification"))
				Modules.WatchlistModule.TestMemberNotification(null);

			float ay = ny + 66f;
			GUI.Label(new Rect(a.x, ay, a.width, 16f), "ACTIONS", _header);
			float bw = (a.width - 12f) / 2f;
			if (GuiKit.Button(new Rect(a.x, ay + 24f, bw, 42f), "Reset thresholds")) ResetThresholds();
			if (GuiKit.Button(new Rect(a.x + bw + 12f, ay + 24f, bw, 42f), "Close menu")) Visible = false;
		}

		// ---------------------------------------------------------------- credits

		// WHO BUILT WHAT. One entry per contribution; add a line here and it shows up in game.
		// Keep `Who` to the name the person actually wants to be known by, and `What` to the thing
		// they did rather than a job title — this is the page someone reads to find out whether
		// their work was acknowledged, so vagueness is the one thing it must not have.
		//
		// Third-party work belongs here too, not only people: a credit is owed the same way whether
		// the contribution came as a pull request or as a library we depend on.
		private readonly struct Credit
		{
			public readonly string Who, What, Link;
			public Credit(string who, string what, string link = "")
			{
				Who = who; What = what; Link = link;
			}
		}

		private static readonly Credit[] Devs =
		{
			new Credit("KaichiSama", "main dev — the mod, the client, the archive and everything wired between them", "vrchatarchive.org"),
			new Credit("VRCXGOLD", "TrueView, and the variant builds", ""),
		};

		private static readonly Credit[] Contributors =
		{
			new Credit("Jan — Tevvez Cheats", "the mods loader this mod is launched through", ""),
			// Add contributors here as they land features. Example of the shape:
			// new Credit("SomeDev", "wrote the X module", "github.com/somedev"),
		};

		private static readonly Credit[] ThirdParty =
		{
			new Credit("Fewdys", "FewTags \u2014 the community nameplate tag database", "github.com/Fewdys/FewTags"),
			new Credit("BepInEx", "the IL2CPP loader the mod runs on", "github.com/BepInEx"),
			new Credit("HarmonyX", "the runtime patching this mod hooks with", "github.com/BepInEx/HarmonyX"),
			new Credit("Il2CppInterop", "the managed bridge into IL2CPP types", "github.com/BepInEx/Il2CppInterop"),
		};

		private static Vector2 _creditScroll;

		private static void DrawCredits(Rect a)
		{
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "CREDITS", _header);
			GUI.Label(new Rect(a.x, a.y + 22f, a.width, 20f),
				"Everyone whose work is in this mod. If you contributed and you are not on this list, "
				+ "that is a bug \u2014 tell me and it gets fixed.", _dim);

			var view = new Rect(a.x, a.y + 52f, a.width, a.height - 52f);
			float rowH = 44f, headH = 30f;
			float contentH = headH * 3f
				+ rowH * (Devs.Length + Contributors.Length + ThirdParty.Length)
				+ (Contributors.Length == 0 ? 34f : 0f) + 24f;

			var content = new Rect(0f, 0f, view.width - 16f, Mathf.Max(view.height, contentH));
			_creditScroll = GUI.BeginScrollView(view, _creditScroll, content);

			float y = 4f;
			y = CreditBlock(content.width, y, "CREDIT FROM DEVS", Devs, rowH, headH);

			GUI.Label(new Rect(0f, y, content.width, 18f), "CONTRIBUTORS", _header);
			y += headH;
			if (Contributors.Length == 0)
			{
				GUI.Label(new Rect(8f, y, content.width - 16f, 30f),
					"<color=#6E6690>nobody yet \u2014 open a PR and this is where your name goes</color>", _dim);
				y += 34f;
			}
			else
			{
				y = CreditRows(content.width, y, Contributors, rowH);
			}

			y += 6f;
			y = CreditBlock(content.width, y, "STANDING ON", ThirdParty, rowH, headH);

			GUI.EndScrollView();
		}

		private static float CreditBlock(float width, float y, string title, Credit[] items, float rowH, float headH)
		{
			GUI.Label(new Rect(0f, y, width, 18f), title, _header);
			return CreditRows(width, y + headH, items, rowH);
		}

		private static float CreditRows(float width, float y, Credit[] items, float rowH)
		{
			for (int i = 0; i < items.Length; i++)
			{
				var c = items[i];
				var row = new Rect(0f, y, width, rowH - 6f);
				GuiKit.RoundedFill(row, new Color(1f, 1f, 1f, (i & 1) == 0 ? 0.045f : 0.025f), 6f);
				// A pink tick down the left edge, the same accent the HUD panels use.
				GuiKit.RoundedFill(new Rect(row.x + 6f, row.y + 7f, 3f, row.height - 14f), Hud.Pink, 1.5f);

				GUI.Label(new Rect(row.x + 18f, row.y + 3f, 220f, 18f),
					"<color=#FFFFFF>" + Trunc(c.Who, 26) + "</color>", _rowTag);
				GUI.Label(new Rect(row.x + 18f, row.y + 19f, width - 36f, 16f),
					"<color=#A99FC4>" + Trunc(c.What, 96) + "</color>", _dim);
				if (!string.IsNullOrEmpty(c.Link))
				{
					GUI.Label(new Rect(row.x + 244f, row.y + 3f, width - 260f, 18f),
						"<color=#7FB0FF>" + Trunc(c.Link, 46) + "</color>", _rowTag);
				}
				y += rowH;
			}
			return y;
		}

		private static void DrawInfo(Rect a)
		{
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "VRCHAT ARCHIVE MOD", _header);
			GUI.Label(new Rect(a.x, a.y + 22f, a.width, 20f), "BepInEx 6 (IL2CPP) — protection + archiving.", _dim);

			// --- diagnostics report: what a tester sends back when something misbehaves ---
			GUI.Label(new Rect(a.x, a.y + 46f, a.width, 16f), "DIAGNOSTICS", _header);
			if (GuiKit.Button(new Rect(a.x, a.y + 66f, 210f, 30f), "SAVE REPORT"))
				_diagPath = DiagnosticsModule.SaveNow();
			if (GuiKit.Button(new Rect(a.x + 218f, a.y + 66f, 210f, 30f), "OPEN FOLDER"))
			{
				try
				{
					string dir = System.IO.Path.GetDirectoryName(DiagnosticsModule.ReportPath);
					if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("explorer.exe", dir);
				}
				catch { }
			}
			GUI.Label(new Rect(a.x, a.y + 104f, a.width, 18f),
				DiagnosticsModule.Problems > 0
					? $"<color=#FF6A6A>{DiagnosticsModule.Problems} distinct problem(s)</color> <color=#6E6690>({DiagnosticsModule.Occurrences} occurrences)</color> — send the file below"
					: "<color=#7CFF9E>no problem recorded so far</color>", _dim);
			GUI.Label(new Rect(a.x, a.y + 124f, a.width, 18f),
				"<color=#6E6690>" + Trunc(string.IsNullOrEmpty(_diagPath) ? DiagnosticsModule.ReportPath : _diagPath, 92) + "</color>", _dim);

			// --- modules + hotkeys (single flowing column, no overlap) ---
			float my = a.y + 156f;
			GUI.Label(new Rect(a.x, my, a.width, 16f), "MODULES", _header);
			GUI.Label(new Rect(a.x, my + 22f, a.width, 18f), "• AntiCrash · ESP · Radar · Movement", _dim);
			GUI.Label(new Rect(a.x, my + 42f, a.width, 18f), "• Tags · FewTags · Watchlist · Udon console", _dim);
			GUI.Label(new Rect(a.x, my + 62f, a.width, 18f), "• FewTags — community nameplate tags (DB by Fewdys).", _dim);

			// --- PERFORMANCE: measured per-module cost, so "the mod lags" becomes a number ---
			float py = my + 92f;
			GUI.Label(new Rect(a.x, py, a.width, 16f), "PERFORMANCE (per module, ms per second)", _header);
			bool prof = GuiKit.Toggle(new Rect(a.x, py + 20f, 200f, 30f),
				ModuleManager.Profiling ? "Profiler: ON" : "Profiler: OFF", ModuleManager.Profiling);
			ModuleManager.Profiling = prof;
			if (prof)
			{
				var rep = ModuleManager.ProfileReport();
				GUI.Label(new Rect(a.x + 212f, py + 26f, 320f, 18f),
					$"total <color=#FFFFFF>{ModuleManager.ProfileTotalMs():F2}</color> ms/s  " +
					$"<color=#6E6690>(16.6 = one frame per second @60fps)</color>", _dim);
				int shown = Mathf.Min(rep.Count, 8);
				for (int i = 0; i < shown; i++)
				{
					float ms = rep[i].Value;
					string col = ms >= 4f ? "#FF6A6A" : ms >= 1f ? "#FFB86B" : "#7CFF9E";
					GUI.Label(new Rect(a.x, py + 54f + i * 18f, 300f, 18f),
						$"<color={col}>{ms,7:F2}</color> ms/s   {rep[i].Key}", _dim);
				}
				if (rep.Count == 0)
					GUI.Label(new Rect(a.x, py + 54f, 400f, 18f), "<color=#6E6690>measuring… (first sample after 1s)</color>", _dim);
			}
			else
			{
				GUI.Label(new Rect(a.x + 212f, py + 26f, 420f, 18f),
					"<color=#6E6690>turn on to measure which module actually costs frame time</color>", _dim);
			}

			float ky = py + (prof ? 210f : 60f);
			GUI.Label(new Rect(a.x, ky, a.width, 16f), "HOTKEYS", _header);
			GUI.Label(new Rect(a.x, ky + 22f, a.width, 18f), "TAB — toggle this menu (mouse + keyboard go to the mod while it is open).", _dim);
			GUI.Label(new Rect(a.x, ky + 42f, a.width, 18f), "Ctrl+F — fly · F8 — dump the live VRChat UI tree to disk.   F9 — capture the open VRChat menu (Worlds / Social).", _dim);
		}

		// Ctrl+V into the tag field. Two things make this necessary rather than decorative:
		// typing cannot deliver an emoji (IMGUI receives one char at a time, an emoji is two), and
		// while a TextField holds keyboard focus IMGUI's internal TextEditor keeps its OWN copy of
		// the string and writes it back over ours next frame — so the paste has to drop focus to
		// stick. No buttons, no palette: it just behaves the way a text box is expected to.
		private static string PasteInto(string text)
		{
			try
			{
				var e = Event.current;
				if (e == null || e.type != EventType.KeyDown) return text;
				if (!(e.control || e.command) || e.keyCode != KeyCode.V) return text;

				string clip = "";
				try { clip = GUIUtility.systemCopyBuffer ?? ""; } catch { }
				if (string.IsNullOrEmpty(clip)) return text;

				e.Use();
				GUIUtility.keyboardControl = 0;
				return (text ?? "") + clip;
			}
			catch { }
			return text;
		}

		// Entry points for VRChat's own per-user menu (UserMenuModule): jump straight to the
		// PLAYERS tab with a given player selected, so "Mod Features" on someone's card lands on
		// the tools for THAT person.
		public static void SelectPlayer(string uid, string name)
		{
			_selectedUid = uid;
			_playerName = name ?? "";
			_newTag = "";
			CancelEdit();
		}

		// OpenPlayers() / OpenFavorites() are GONE (2026-09-01). They set Visible = true on a menu
		// that is sealed: a wing-row click "opened" it, nothing was drawn, and the click looked
		// dead. A player picked in VRChat's wing now goes to the desktop client's PLAYERS page
		// over the control channel (WingPlayersModule → ModControlModule.WingSelect) instead.

		// The tag system's status line. It used to be a bare label, which made a refusal
		// ("this user has tag lock enabled") read like stray text floating on the panel. Now it is
		// a pill whose colour states what KIND of message it is, so a refusal, a failure and a
		// success are told apart at a glance instead of by reading.
		private static void DrawStatus(Rect r)
		{
			string msg = VaTagsModule.LastStatus;
			bool idle = string.IsNullOrEmpty(msg);
			if (idle) msg = VaTagsModule.LastFetchInfo ?? "";
			if (string.IsNullOrEmpty(msg)) return;

			Color accent; string icon;
			if (idle)                                   { accent = new Color(0.35f, 0.42f, 0.50f); icon = "·"; }
			else if (msg.StartsWith("✓"))               { accent = new Color(0.49f, 1f, 0.62f);    icon = "✓"; msg = msg.Substring(1).TrimStart(); }
			else if (Mentions(msg, "login required", "lock", "mandatory", "only lock", "limit"))
			                                            { accent = new Color(1f, 0.72f, 0.32f);    icon = "!"; }
			else if (Mentions(msg, "fail", "error", "unable", "invalid", "not a usr_", "does not"))
			                                            { accent = new Color(1f, 0.45f, 0.45f);    icon = "×"; }
			else if (Mentions(msg, "saving", "locking", "unlocking", "…"))
			                                            { accent = new Color(0.49f, 0.69f, 1f);    icon = "…"; }
			else                                        { accent = new Color(0.55f, 0.62f, 0.72f); icon = "·"; }

			float rad = Mathf.Min(r.height, 30f) * 0.5f;
			GuiKit.RoundedFill(r, new Color(0.05f, 0.055f, 0.075f, 0.92f), rad);
			GuiKit.RoundedFill(new Rect(r.x + 10f, r.y + r.height * 0.5f - 4f, 8f, 8f), accent, 4f);

			_statusPill.normal.textColor = idle ? new Color(0.55f, 0.60f, 0.68f) : new Color(0.92f, 0.94f, 0.97f);
			GUI.Label(new Rect(r.x + 26f, r.y, r.width - 34f, r.height), Trunc(msg, 90), _statusPill);
		}

		private static bool Mentions(string s, params string[] needles)
		{
			foreach (string n in needles)
				if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		// ================= widgets =================

		private static void Card(float x, float y, float w, float h, string label, string value, bool active)
		{
			var r = new Rect(x, y, w, h);
			GuiKit.Box(r, active);
			GUI.Label(new Rect(r.x + 12f, r.y + 10f, r.width - 20f, 16f), label, _cardLabel);
			GUI.Label(new Rect(r.x, r.y + 28f, r.width, r.height - 34f), value, _cardValue);
		}

		// PLAYERS tab — VRChatArchive player tagging (VaTagsModule).
		// Left: live instance roster with per-player tags. Select one → right pane shows
		// actions (CLONE AVATAR / TP / ADD TAG) and its tag chips with [x] remove. Bottom
		// row: add a tag by raw user id, and a tag-frequency readout.
		private static string _selectedUid;
		private static string _playerName = "";
		private static string _newTag = "";
		private static string _manualUid = "";
		private static string _manualTag = "";
		// Tag edit-in-place: while set, the NEW TAG editor is editing an existing tag instead of
		// adding one. _editOrig is the original text, so a text change becomes remove-old + add-new.
		private static string _editUid;
		private static string _editOrig;
		private static Vector2 _rosterScroll;
		// VA account login popup state (writing tags needs an account; reading never does)
		private static bool _vaLoginOpen;
		private static bool _vaJustLoggedIn;
		private static string _vaUser = "";
		private static string _vaPass = "";

		// tag style picker state (shared by the selected-player add and the by-id add)
		private static int _tagColorIdx;   // index into TagColors
		private static bool _tagB, _tagI, _tagU;
		private static int _tagFxIdx;      // index into TagFx
		private static readonly (string name, string hex)[] TagColors =
		{
			("none", ""), ("red", "#FF4757"), ("orange", "#FF7B42"), ("yellow", "#FFDD00"),
			("green", "#2BCF5C"), ("cyan", "#22C7F5"), ("blue", "#1778FF"), ("purple", "#8143E6"),
			("pink", "#FF6AD5"), ("white", "#FFFFFF"),
		};
		// Full FewTags-style effect set: id, menu label, what it does.
		private static readonly (string id, string label, string desc)[] TagFxList =
		{
			("none",    "None",            "Plain static text — colour and B/I/U only."),
			("sr",      "SR · Smooth Rainbow", "Smooth flowing rainbow. (colour ignored)"),
			("rain",    "RAIN · Rainbow",  "Chunky stepped rainbow, CoD:WaW clan-tag style. (colour ignored)"),
			("scroll",  "Scroll",          "Text scrolls to the left on loop. (keeps your colour)"),
			("pulse",   "Pulse",           "A size wave travels through the letters. (keeps your colour)"),
			("jump",    "Jump",            "Letters hop up and down in sequence. (keeps your colour)"),
			("shake",   "Shake",           "Text shakes side to side. (keeps your colour)"),
			("blink",   "Blink",           "Text blinks on and off. (colour kept, fades out)"),
			("glitch",  "Glitch",          "Random characters glitch through the text."),
			("cyln",    "CYLN · Cylon",    "A red line bounces back and forth across the text."),
			("lbl",     "LBL · Letter by letter", "Letters are removed one by one, then added back."),
			("gt",      "GT · Ghost Trail","A dark fade sweeps across the text. (colour ignored)"),
			("glow",    "Glow",            "Your colour pulses brighter and dimmer."),
			("grad",    "VA Gradient",     "Pink→violet gradient — the VRChat Archive member badge."),
		};
		private static readonly string[] TagFx = BuildFxIds();
		private static string[] BuildFxIds()
		{
			var a = new string[TagFxList.Length];
			for (int i = 0; i < a.Length; i++) a[i] = TagFxList[i].id;
			return a;
		}
		private static Vector2 _fxScroll;
		private static string _diagPath = "";

		// A colour / effect the PICKER CANNOT REPRESENT, carried over from the tag being edited.
		// The picker only knows 10 swatches and 14 effect ids, but tags also come from the website
		// (full colour picker) and from older saves (alias ids). Before this, editing such a tag
		// silently rewrote it: no swatch matched -> index 0 -> colour "" (white), no effect matched
		// -> index 0 -> "none". Pressing EDIT then SAVE quietly stripped what the user had chosen.
		// Kept here and written back unless the user actually clicks a swatch / an effect row.
		private static string _tagColorCustom = "";
		private static string _tagFxCustom = "";

		private static VaTagsModule.VaTag BuildTagFromPicker(string text) => new VaTagsModule.VaTag
		{
			Text = text,
			Color = string.IsNullOrEmpty(_tagColorCustom)
				? TagColors[Mathf.Clamp(_tagColorIdx, 0, TagColors.Length - 1)].hex
				: _tagColorCustom,
			B = _tagB, I = _tagI, U = _tagU,
			Fx = string.IsNullOrEmpty(_tagFxCustom)
				? TagFx[Mathf.Clamp(_tagFxIdx, 0, TagFx.Length - 1)]
				: _tagFxCustom,
		};

		// Load an existing tag into the NEW TAG editor for edit-in-place: select its owner so the
		// editor is visible, fill the text + style picker, and remember the original text so that a
		// text change becomes remove-old + add-new (a same-text save just restyles it).
		private static void LoadTagIntoEditor(string ownerUid, VaTagsModule.VaTag tag)
		{
			_selectedUid = ownerUid;
			if (string.Equals(ownerUid, VaTagsModule.LocalUserId(), StringComparison.OrdinalIgnoreCase)) _playerName = "you";
			_newTag = tag.Text ?? "";
			_editUid = ownerUid;
			_editOrig = tag.Text ?? "";

			// COLOUR. A tag styled on the website can carry any hex, not just our 10 swatches, so a
			// miss must KEEP the hex instead of falling back to swatch 0 (which is "", i.e. white).
			_tagColorIdx = 0;
			_tagColorCustom = "";
			bool colorMatched = false;
			for (int i = 0; i < TagColors.Length; i++)
				if (!string.IsNullOrEmpty(tag.Color) && string.Equals(TagColors[i].hex, tag.Color, StringComparison.OrdinalIgnoreCase)) { _tagColorIdx = i; colorMatched = true; break; }
			if (!colorMatched && !string.IsNullOrEmpty(tag.Color)) _tagColorCustom = tag.Color;

			_tagB = tag.B; _tagI = tag.I; _tagU = tag.U;

			// EFFECT. Two ids are implemented but absent from the picker: "rainbow" and "wave".
			// They are ALIASES — AnimatePlate handles them in the same switch arm as "rain" and
			// "pulse" respectively — so mapping them onto the picker is lossless and lets the row
			// highlight correctly. Anything else unknown is preserved verbatim rather than reset.
			_tagFxIdx = 0;
			_tagFxCustom = "";
			string fx = string.IsNullOrEmpty(tag.Fx) ? "none" : tag.Fx;
			if (string.Equals(fx, "rainbow", StringComparison.OrdinalIgnoreCase)) fx = "rain";
			else if (string.Equals(fx, "wave", StringComparison.OrdinalIgnoreCase)) fx = "pulse";
			bool fxMatched = false;
			for (int i = 0; i < TagFx.Length; i++)
				if (string.Equals(TagFx[i], fx, StringComparison.OrdinalIgnoreCase)) { _tagFxIdx = i; fxMatched = true; break; }
			if (!fxMatched) _tagFxCustom = tag.Fx;
		}

		// Clearing the carried-over custom style matters here: leaving it set would silently apply
		// the edited tag's colour/effect to whatever tag is composed next.
		private static void CancelEdit() { _editUid = null; _editOrig = null; _newTag = ""; _tagColorCustom = ""; _tagFxCustom = ""; }

		// Style picker: colour swatches, B/I/U, and the scrollable effect list with a live
		// animated sample of every effect so the choice is obvious before you commit.
		private static void DrawStylePicker(Rect a, string sampleText)
		{
			float t = VaClock.Now;
			GUI.Label(new Rect(a.x, a.y, a.width, 18f), "TAG STYLE", _header);

			// --- colours ---
			float sw = 30f, sy = a.y + 24f;
			for (int i = 0; i < TagColors.Length; i++)
			{
				var r = new Rect(a.x + i * (sw + 5f), sy, sw, 26f);
				Color c = i == 0 ? new Color(0.18f, 0.18f, 0.22f) : (ColorUtility.TryParseHtmlString(TagColors[i].hex, out Color cc) ? cc : Color.white);
				GuiKit.RoundedFill(r, c, 5f);
				if (_tagColorIdx == i) { GUI.color = Color.white; GuiKit.Corners(r, 5f, 3f, 0f); GUI.color = Color.white; }
				if (i == 0) GUI.Label(new Rect(r.x + 9f, r.y + 3f, sw, 22f), "∅", _rowName);
				// An explicit click is the user overriding a carried-over custom colour, so drop it
				// — otherwise a website-styled tag could never be recoloured from here.
				if (GUI.Button(r, "", _tabStyle)) { _tagColorIdx = i; _tagColorCustom = ""; }
			}

			// --- B / I / U ---
			float by = sy + 34f;
			_tagB = GuiKit.Toggle(new Rect(a.x, by, 66f, 30f), "<b>B</b>", _tagB);
			_tagI = GuiKit.Toggle(new Rect(a.x + 72f, by, 66f, 30f), "<i>I</i>", _tagI);
			_tagU = GuiKit.Toggle(new Rect(a.x + 144f, by, 66f, 30f), "U", _tagU);

			// --- big live preview of the tag you are composing ---
			float py = by + 38f;
			GUI.Label(new Rect(a.x, py, a.width, 14f), "LIVE PREVIEW", _dim);
			var prev = new Rect(a.x, py + 16f, a.width, 46f);
			GuiKit.RoundedFill(prev, new Color(0.02f, 0.03f, 0.05f, 0.9f), 8f);
			var sample = BuildTagFromPicker(string.IsNullOrWhiteSpace(sampleText) ? "type your tag →" : sampleText.Trim());
			GUI.Label(new Rect(prev.x + 12f, prev.y + 6f, prev.width - 24f, 34f),
				VaTagsModule.ImguiPreview(sample, t, 20), _previewBig);

			// --- effect list with descriptions + per-row live sample ---
			float ly = py + 70f;
			GUI.Label(new Rect(a.x, ly, a.width, 16f), "EFFECT", _header);
			var listRect = new Rect(a.x, ly + 20f, a.width, Mathf.Max(120f, a.yMax - (ly + 20f)));
			GuiKit.RoundedFill(listRect, new Color(1f, 1f, 1f, 0.03f), 8f);
			float rowH = 44f;
			var view = new Rect(0, 0, listRect.width - 18f, TagFxList.Length * rowH);
			_fxScroll = GUI.BeginScrollView(listRect, _fxScroll, view);
			for (int i = 0; i < TagFxList.Length; i++)
			{
				var row = new Rect(4f, i * rowH, view.width - 8f, rowH - 4f);
				if (_tagFxIdx == i) GuiKit.RoundedFill(row, new Color(GuiKit.Accent.r, GuiKit.Accent.g, GuiKit.Accent.b, 0.20f), 6f);
				if (GUI.Button(row, "", _tabStyle)) { _tagFxIdx = i; _tagFxCustom = ""; }

				GUI.Label(new Rect(row.x + 8f, row.y + 2f, row.width * 0.52f, 20f),
					(_tagFxIdx == i ? "▸ " : "") + TagFxList[i].label, _rowName);
				// live sample rendered with THIS effect, so the list demos itself
				var demo = new VaTagsModule.VaTag
				{
					Text = "SAMPLE",
					Color = TagColors[Mathf.Clamp(_tagColorIdx, 0, TagColors.Length - 1)].hex,
					Fx = TagFxList[i].id,
				};
				GUI.Label(new Rect(row.x + row.width * 0.56f, row.y + 2f, row.width * 0.44f - 8f, 20f),
					VaTagsModule.ImguiPreview(demo, t, 14), _rowTag);
				GUI.Label(new Rect(row.x + 8f, row.y + 21f, row.width - 16f, 18f), TagFxList[i].desc, _dim);
			}
			GUI.EndScrollView();
		}

		private static void DrawPlayers(Rect a)
		{
			if (!ModConfig.VaTagsEnabled.Value)
			{
				GUI.Label(new Rect(a.x, a.y, a.width, 40f),
					"Player tagging is disabled. Enable VaTags in the config to use this tab.", _dim);
				return;
			}

			// ── AUTH : reading tags is public, but ADDING a tag needs a VRChat Archive account.
			// The strip auto-detects the desktop client (LocalBridge) or offers an in-mod login.
			DrawTagAuthStrip(a);
			a = new Rect(a.x, a.y + 32f, a.width, a.height - 32f);

			// Three columns: roster | selected player + your tags | style picker.
			// IMPORTANT: every GUI.TextField is drawn BEFORE the roster scroll list at the
			// end of this method. IMGUI assigns keyboard-focus control ids by DRAW ORDER, so
			// if the variable-length roster (refreshed ~1/s as players join/leave) were drawn
			// first, its changing control count would shift the fields' ids and drop focus
			// mid-typing. Roster drawn last = stable ids.
			float gap = 16f;
			float colW = (a.width - gap * 2f) / 3f;
			float midX = a.x + colW + gap;
			float rightX = midX + colW + gap;
			float t = VaClock.Now;

			GUI.Label(new Rect(a.x, a.y, colW, 18f), "INSTANCE · " + VaTagsModule.Roster.Count + " PLAYERS", _header);
			if (GuiKit.Button(new Rect(a.x + colW - 84f, a.y - 4f, 84f, 24f), "↻ tags")) VaTagsModule.RequestRefresh();

			// ================= MIDDLE: selected player =================
			var sel2 = FindSelected();
			float my = a.y;
			if (_selectedUid == null)
			{
				GUI.Label(new Rect(midX, my, colW, 20f), "NO PLAYER SELECTED", _header);
				GUI.Label(new Rect(midX, my + 24f, colW, 48f),
					"Click a player in the list on the left to tag them, teleport to them, or clone their avatar.", _dim);
				my += 78f;
			}
			else
			{
				string uid = _selectedUid;
				string dispName = sel2 != null ? sel2.Name : _playerName;
				string tcol = sel2 != null && !string.IsNullOrEmpty(sel2.TrustColor) ? sel2.TrustColor : "#EAF6FF";
				GUI.Label(new Rect(midX, my, colW, 24f),
					(sel2 != null && sel2.IsOwner ? "<color=#FFD24A>♛</color> " : "") + "<color=" + tcol + ">" + Trunc(dispName, 26) + "</color>", _bigName);
				GUI.Label(new Rect(midX, my + 26f, colW, 16f),
					uid != null ? "<color=#6E6690>" + uid + "</color>" : "<color=#FF6A6A>user id not available</color>", _dim);

				if (sel2 != null)
				{
					Vector3 pos = sel2.Position;
					GUI.Label(new Rect(midX, my + 44f, colW, 16f),
						$"<color=#7FB0FF>pos</color>  X {pos.x:F1}   Y {pos.y:F1}   Z {pos.z:F1}", _dim);
					if (!string.IsNullOrEmpty(sel2.AvatarName))
						GUI.Label(new Rect(midX, my + 62f, colW, 16f),
							"<color=#C58BFF>avatar</color>  " + Trunc(sel2.AvatarName, 30), _dim);
				}

				float by = my + 84f;
				float bw = (colW - gap) / 2f;
				bool inInstance = sel2 != null;
				if (GuiKit.Button(new Rect(midX, by, bw, 38f), inInstance ? "CLONE AVATAR" : "CLONE (offline)") && inInstance) VaTagsModule.CloneAvatar(sel2);
				if (GuiKit.Button(new Rect(midX + bw + gap, by, bw, 38f), inInstance ? "TP" : "TP (offline)") && inInstance)
				{ VaTagsModule.TeleportTo(sel2); NotifyTeleported(); }

				// ORBIT / SIT \u2014 self-movement locked to this player. Both light up while running and
				// the same button stops them, so there is always an obvious way out.
				bool orbiting = Modules.OrbitModule.Current == Modules.OrbitModule.Mode.Orbit
					&& string.Equals(Modules.OrbitModule.TargetUid, uid, StringComparison.OrdinalIgnoreCase);
				bool sitting = Modules.OrbitModule.Current == Modules.OrbitModule.Mode.Sit
					&& string.Equals(Modules.OrbitModule.TargetUid, uid, StringComparison.OrdinalIgnoreCase);
				float oy = by + 44f;
				if (GuiKit.Toggle(new Rect(midX, oy, bw, 34f), orbiting ? "ORBIT (stop)" : "ORBIT", orbiting) != orbiting && inInstance)
					Modules.OrbitModule.Toggle(Modules.OrbitModule.Mode.Orbit, sel2);
				if (GuiKit.Toggle(new Rect(midX + bw + gap, oy, bw, 34f), sitting ? "SIT (stop)" : "SIT ON MESH", sitting) != sitting && inInstance)
					Modules.OrbitModule.Toggle(Modules.OrbitModule.Mode.Sit, sel2);

				// The prop ring, centred on THEM. Local only, like everything ObjectOrbit does.
				bool ringHere = Modules.ObjectOrbitModule.Active && sel2 != null
					&& Modules.ObjectOrbitModule.CenterName == sel2.Name;
				if (GuiKit.Toggle(new Rect(midX, oy + 38f, colW, 32f),
						ringHere ? "SPIN OBJECTS AROUND THEM (stop)" : "SPIN OBJECTS AROUND THEM", ringHere) != ringHere
					&& inInstance)
					Modules.ObjectOrbitModule.ToggleOnPlayer(sel2);

				float ty = by + 128f;
				GUI.Label(new Rect(midX, ty, colW, 18f), "THEIR TAGS", _header);
				ty += 22f;
				var tags = VaTagsModule.TagsOf(uid);
				for (int i = 0; i < tags.Length && i < 6; i++)
				{
					var tr = new Rect(midX, ty + i * 30f, colW, 26f);
					GuiKit.RoundedFill(tr, new Color(1f, 1f, 1f, 0.06f), 6f);
					GUI.Label(new Rect(tr.x + 8f, tr.y + 3f, tr.width - 124f, 20f), VaTagsModule.ImguiPreview(tags[i], t, 15), _rowTag);
					if (!string.Equals(tags[i].Text, VaTagsModule.MemberTagText, StringComparison.OrdinalIgnoreCase))
					{
						if (GuiKit.Button(new Rect(tr.xMax - 116f, tr.y, 54f, 26f), "EDIT")) LoadTagIntoEditor(uid, tags[i]);
						if (GuiKit.Button(new Rect(tr.xMax - 56f, tr.y, 56f, 26f), "REMOVE")) VaTagsModule.RemoveTag(uid, tags[i].Text);
					}
				}
				if (tags.Length == 0) GUI.Label(new Rect(midX, ty, colW, 20f), "no tags yet — add one below", _dim);

				float ay = ty + Mathf.Max(1, Mathf.Min(tags.Length, 6)) * 30f + 10f;
				bool editing = _editOrig != null && string.Equals(_editUid, uid, StringComparison.OrdinalIgnoreCase);
				GUI.Label(new Rect(midX, ay, colW, 16f),
					editing ? "EDIT TAG — change the text or style, then SAVE" : "NEW TAG TEXT (style from the right panel)", _header);
				float fieldW = editing ? colW - 148f : colW - 92f;
				GUI.SetNextControlName("va_newtag");
				_newTag = GUI.TextField(new Rect(midX, ay + 20f, fieldW, 30f), _newTag ?? "", 20000);
				// Ctrl+V is handled explicitly for this field (see PasteInto): Unity hands IMGUI
				// typed input one `char` at a time and an emoji is a surrogate PAIR, so typing one
				// — or using Windows' emoji panel — drops half of it and nothing arrives. Pasting
				// takes the whole string at once, so 🦭Kaichi🦭 goes in as-is.
				_newTag = PasteInto(_newTag);
				if (editing && GuiKit.Button(new Rect(midX + fieldW + 4f, ay + 20f, 48f, 30f), "✕ cancel")) CancelEdit();
				if (GuiKit.Button(new Rect(midX + colW - 88f, ay + 20f, 88f, 30f), editing ? "SAVE" : "ADD TAG") && !string.IsNullOrWhiteSpace(_newTag))
				{
					string txt = _newTag.Trim();
					// A text change is a rename: drop the old tag, add the new one. Same text = restyle.
					if (editing && !string.Equals(txt, _editOrig, StringComparison.Ordinal))
						VaTagsModule.RemoveTag(uid, _editOrig);
					VaTagsModule.AddTag(uid, BuildTagFromPicker(txt));
					// CancelEdit() rather than clearing the three edit fields by hand: it also drops
					// the carried-over custom colour/effect, which would otherwise be applied to the
					// next tag composed in this panel.
					CancelEdit();
				}
				if (!string.IsNullOrWhiteSpace(_newTag))
					GUI.Label(new Rect(midX, ay + 54f, colW, 22f),
						"<color=#6E6690>preview:</color>  " + VaTagsModule.ImguiPreview(BuildTagFromPicker(_newTag.Trim()), t, 16), _rowTag);
				my = ay + (string.IsNullOrWhiteSpace(_newTag) ? 58f : 82f);
			}

			// ---- YOUR OWN TAGS (you never see your own nameplate, so they live here) ----
			string myUid = VaTagsModule.LocalUserId();
			// Rows are capped to the space actually left above the add-by-id row, which on
			// shorter screens used to be overdrawn by the tag list.
			float bottomY = a.y + a.height - 58f;
			GUI.Label(new Rect(midX, my, colW, 18f), "YOUR TAGS", _header);
			{
				bool locked = !string.IsNullOrEmpty(myUid) && VaTagsModule.IsLocked(myUid);
				if (!string.IsNullOrEmpty(myUid) &&
					GuiKit.Button(new Rect(midX + colW - 134f, my - 3f, 134f, 22f), locked ? "TAG LOCK: ON" : "TAG LOCK: OFF"))
					VaTagsModule.SetLock(!locked);
			}
			my += 22f;
			if (string.IsNullOrEmpty(myUid))
			{
				GUI.Label(new Rect(midX, my, colW, 20f), "your user id isn't readable yet (join a world)", _dim);
				my += 24f;
			}
			else
			{
				var mine = VaTagsModule.TagsOf(myUid);
				if (mine.Length == 0)
				{
					GUI.Label(new Rect(midX, my, colW, 20f), "you have no tags yet — select yourself (★) to add one", _dim);
					my += 24f;
				}
				int room = Mathf.Max(0, Mathf.FloorToInt((bottomY - 8f - my) / 30f));
				for (int i = 0; i < mine.Length && i < Mathf.Min(4, room); i++)
				{
					var tr = new Rect(midX, my + i * 30f, colW, 26f);
					GuiKit.RoundedFill(tr, new Color(0.13f, 0.78f, 0.96f, 0.10f), 6f);
					GUI.Label(new Rect(tr.x + 8f, tr.y + 3f, tr.width - 124f, 20f), VaTagsModule.ImguiPreview(mine[i], t, 15), _rowTag);
					if (!string.Equals(mine[i].Text, VaTagsModule.MemberTagText, StringComparison.OrdinalIgnoreCase))
					{
						if (GuiKit.Button(new Rect(tr.xMax - 116f, tr.y, 54f, 26f), "EDIT")) LoadTagIntoEditor(myUid, mine[i]);
						if (GuiKit.Button(new Rect(tr.xMax - 56f, tr.y, 56f, 26f), "REMOVE")) VaTagsModule.RemoveTag(myUid, mine[i].Text);
					}
				}
				my += Mathf.Min(mine.Length, Mathf.Min(4, room)) * 30f;
			}

			// ---- add by raw user id ----
			GUI.Label(new Rect(a.x, bottomY, a.width, 16f),
				"TAG SOMEONE NOT IN YOUR INSTANCE — paste their VRChat user id, type the tag, pick a style on the right", _header);

			// Two labelled fields. Empty fields show a greyed example inside them, because a
			// pair of blank boxes gives no clue which one wants the id and which one the tag.
			float uidW = a.width * 0.34f, tagW = a.width * 0.22f;
			float fieldY = bottomY + 34f;
			float labelY = bottomY + 19f;

			bool uidOk = VaTagsModule.LooksLikeUserId((_manualUid ?? "").Trim());
			bool uidEmpty = string.IsNullOrEmpty(_manualUid);
			string uidMark = uidEmpty ? "" : (uidOk ? "  <color=#7CFF9E>valid</color>" : "  <color=#FF6A6A>not a usr_ id</color>");
			GUI.Label(new Rect(a.x, labelY, uidW, 14f), "1 · VRCHAT USER ID" + uidMark, _dim);
			GUI.SetNextControlName("va_manualuid");
			_manualUid = GUI.TextField(new Rect(a.x, fieldY, uidW, 30f), _manualUid ?? "", 40);
			if (uidEmpty)
				GUI.Label(new Rect(a.x + 8f, fieldY + 6f, uidW - 12f, 20f),
					"<color=#4A5568>usr_00000000-0000-0000-0000-000000000000</color>", _dim);

			float tagX = a.x + uidW + 8f;
			GUI.Label(new Rect(tagX, labelY, tagW, 14f), "2 · TAG TEXT", _dim);
			GUI.SetNextControlName("va_manualtag");
			_manualTag = GUI.TextField(new Rect(tagX, fieldY, tagW, 30f), _manualTag ?? "", 20000);
			_manualTag = PasteInto(_manualTag);
			if (string.IsNullOrEmpty(_manualTag))
				GUI.Label(new Rect(tagX + 8f, fieldY + 6f, tagW - 12f, 20f), "<color=#4A5568>e.g. Creator</color>", _dim);

			float addX = a.x + uidW + tagW + 16f;
			GUI.Label(new Rect(addX, labelY, 92f, 14f), "3 ·", _dim);
			if (GuiKit.Button(new Rect(addX, fieldY, 92f, 30f), "ADD"))
			{
				if (!uidOk) VaTagsModule.LastStatus = "paste a VRChat user id in field 1 (it starts with usr_)";
				else if (string.IsNullOrWhiteSpace(_manualTag)) VaTagsModule.LastStatus = "type the tag text in field 2";
				else { VaTagsModule.AddTag(_manualUid.Trim(), BuildTagFromPicker(_manualTag.Trim())); _manualTag = ""; }
			}
			DrawStatus(new Rect(addX + 100f, fieldY - 2f, a.width - (addX - a.x) - 100f, 30f));

			// ================= RIGHT: style picker (draws its own scroll view) =================
			string activeTagText = !string.IsNullOrWhiteSpace(_manualTag) ? _manualTag
				: (!string.IsNullOrWhiteSpace(_newTag) ? _newTag : "");
			DrawStylePicker(new Rect(rightX, a.y, colW, a.height - 74f), activeTagText);

			// ================= LEFT: roster — DRAWN LAST (control-id note above) =================
			// Roster skipped while the login popup is open: each row allocates an IMGUI control id
			// and the roster count changes as players join/leave, which would shift the popup's
			// text-field ids and drop keyboard focus mid-typing.
			if (!_vaLoginOpen)
			{
			var listRect = new Rect(a.x, a.y + 26f, colW, a.height - 100f);
			GuiKit.RoundedFill(listRect, new Color(1f, 1f, 1f, 0.03f), 10f);
			var roster = VaTagsModule.Roster;
			float rowH = 50f;
			var view = new Rect(0, 0, colW - 18f, Mathf.Max(listRect.height, roster.Count * rowH));
			_rosterScroll = GUI.BeginScrollView(listRect, _rosterScroll, view);
			for (int i = 0; i < roster.Count; i++)
			{
				var p = roster[i];
				var row = new Rect(4f, i * rowH, view.width - 8f, rowH - 4f);
				bool sel = p.UserId != null && p.UserId == _selectedUid;
				if (sel) GuiKit.RoundedFill(row, new Color(GuiKit.Accent.r, GuiKit.Accent.g, GuiKit.Accent.b, 0.20f), 8f);
				if (GUI.Button(row, "", _tabStyle)) { _selectedUid = p.UserId; _playerName = p.Name; _newTag = ""; }

				// OFFSCREEN ROWS COST NOTHING BELOW THIS LINE (2026-09-13).
				//
				// The list draws one 50px row per player with no culling, so a 35-player instance
				// built and laid out ~1750px of rich text — name gradient, trust colour, badge
				// string concatenation, several GUI.Labels each — every repaint, and IMGUI repaints
				// more than once a frame. Only a handful of rows are ever visible in the scroll view.
				//
				// The GUI.Button ABOVE is deliberately left outside this test: it is the only control
				// here that allocates an IMGUI control id, and this panel is id-sensitive (see the
				// login-popup note above). Keeping it unconditional means the id sequence is exactly
				// what it was, so nothing can lose keyboard focus — only the drawing is skipped.
				if (row.yMax < _rosterScroll.y || row.y > _rosterScroll.y + listRect.height) continue;

				string col = string.IsNullOrEmpty(p.TrustColor) ? "#EAF6FF" : p.TrustColor;
				string prefix = p.IsLocal ? "<color=#7CFF9E>★</color> " : (p.IsOwner ? "<color=#FFD24A>♛</color> " : "");
				bool vaMember = VaTagsModule.IsMember(p.UserId);
				if (vaMember) prefix = "<color=#FF6AD5>◆</color> " + prefix;
				// VRChat Archive members are shown in the pink→violet member gradient
				// instead of their trust colour, so mod users spot each other instantly.
				string nameTxt = Trunc(p.Name, p.IsOwner ? 24 : 26);
				if (p.PlayerId >= 0) prefix += "<color=#6E6690>[" + p.PlayerId + "]</color> ";
				GUI.Label(new Rect(row.x + 10f, row.y + 4f, row.width - 20f, 20f),
					prefix + (vaMember ? VaTagsModule.Gradient(nameTxt) : "<color=" + col + ">" + nameTxt + "</color>"), _rowName);
				// Badges, read from the VRChat API once per roster refresh: VRC+, 18+ and the
				// platform the account last logged in from.
				string badges = "";
				if (p.Plus)  badges += "<color=#FFD24A>VRC+</color> ";
				if (p.Adult) badges += "<color=#FF7AB8>18+</color> ";
				if (!string.IsNullOrEmpty(p.Platform))
					badges += p.Platform == "Quest"
						? "<color=#7CFF9E>Quest</color>"
						: "<color=#7FB0FF>" + p.Platform + "</color>";
					if (p.VrKnown && p.InVR) badges += " <color=#8FE9A8>VR</color>";
				if (badges.Length > 0)
					GUI.Label(new Rect(row.xMax - 148f, row.y + 4f, 140f, 18f), badges, _rowTagRight);

				var ptags = VaTagsModule.TagsOf(p.UserId);
				GUI.Label(new Rect(row.x + 10f, row.y + 25f, row.width - 20f, 18f),
					ptags.Length > 0 ? TagsPreview(ptags) : "<color=#6E6690>no tags</color>", _rowTag);
			}
			GUI.EndScrollView();
			}

			if (_vaLoginOpen) DrawVaLoginPopup(a);
		}

		// Auth status strip at the top of the tagging tab. Green = you can write tags
		// (client bridge or a mod login); amber = reading only until you connect.
		private static void DrawTagAuthStrip(Rect a)
		{
			var mode = VaAuth.Current;
			bool authed = mode != VaAuth.Mode.None;
			var bar = new Rect(a.x, a.y, a.width, 26f);
			GuiKit.RoundedFill(bar, new Color(0.05f, 0.06f, 0.08f, 0.92f), 6f);
			GUI.color = authed ? new Color(0.42f, 0.95f, 0.55f) : new Color(1f, 0.72f, 0.32f);
			GUI.DrawTexture(new Rect(bar.x + 11f, bar.y + 10f, 7f, 7f), GuiKit.Pixel);
			GUI.color = Color.white;

			string label = mode == VaAuth.Mode.ClientBridge
				? "<color=#8CFFB0>VRChat Archive client connected</color>  <color=#6E6690>·</color>  " + Trunc(VaAuth.AccountName, 22) + "  <color=#6E6690>· writing enabled</color>"
				: mode == VaAuth.Mode.ModLogin
				? "<color=#8CFFB0>logged in</color>  <color=#6E6690>·</color>  " + Trunc(VaAuth.AccountName, 22) + "  <color=#6E6690>· writing enabled</color>"
				: "<color=#FFB86B>account required to add tags</color>  <color=#6E6690>· reading is open to everyone</color>";
			GUI.Label(new Rect(bar.x + 28f, bar.y + 4f, bar.width - 150f, 18f), label, _rowTag);

			var btn = new Rect(bar.x + bar.width - 106f, bar.y + 3f, 100f, 20f);
			if (mode == VaAuth.Mode.ClientBridge)
				GUI.Label(new Rect(btn.x + 6f, btn.y + 1f, btn.width, 18f), "<color=#6E6690>via client</color>", _rowTag);
			else if (mode == VaAuth.Mode.ModLogin)
			{ if (GuiKit.Button(btn, "Log out")) VaAuth.Logout(); }
			else { if (GuiKit.Button(btn, "Connect")) { _vaLoginOpen = true; _vaJustLoggedIn = false; VaAuth.LastError = ""; } }
		}

		// In-mod "Connect to VRChat Archive" popup (fallback when not running inside the client,
		// e.g. a third-party launcher). The session cookie is held in memory only.
		private static void DrawVaLoginPopup(Rect area)
		{
			if (_vaJustLoggedIn) { _vaJustLoggedIn = false; _vaLoginOpen = false; _vaPass = ""; return; }

			GUI.color = new Color(0f, 0f, 0f, 0.6f);
			GUI.DrawTexture(area, GuiKit.Pixel);
			GUI.color = Color.white;

			float w = 380f, h = 236f;
			var box = new Rect(area.x + (area.width - w) / 2f, area.y + (area.height - h) / 2f, w, h);
			GuiKit.RoundedFill(box, new Color(0.08f, 0.09f, 0.12f, 1f), 10f);
			GUI.color = GuiKit.Accent; GuiKit.Corners(box, 12f, 2f, 0f); GUI.color = Color.white;

			GUI.Label(new Rect(box.x + 18f, box.y + 14f, w - 36f, 22f), "CONNECT TO VRCHAT ARCHIVE", _header);
			GUI.Label(new Rect(box.x + 18f, box.y + 38f, w - 36f, 16f),
				"<color=#7A8794>Your account lets you add tags. Reading tags never needs one.</color>", _dim);

			GUI.Label(new Rect(box.x + 18f, box.y + 66f, 84f, 22f), "Username", _rowTag);
			GUI.SetNextControlName("va_user");
			_vaUser = GUI.TextField(new Rect(box.x + 104f, box.y + 64f, w - 122f, 26f), _vaUser ?? "", 64);

			GUI.Label(new Rect(box.x + 18f, box.y + 100f, 84f, 22f), "Password", _rowTag);
			GUI.SetNextControlName("va_pass");
			_vaPass = GUI.PasswordField(new Rect(box.x + 104f, box.y + 98f, w - 122f, 26f), _vaPass ?? "", '•', 128);

			if (!string.IsNullOrEmpty(VaAuth.LastError))
				GUI.Label(new Rect(box.x + 18f, box.y + 130f, w - 36f, 34f), "<color=#FF7A7A>" + VaAuth.LastError + "</color>", _dim);

			float by = box.y + h - 46f;
			float bw = (w - 36f - 10f) / 2f;
			bool busy = VaAuth.Busy;
			if (GuiKit.Button(new Rect(box.x + 18f, by, bw, 32f), busy ? "connecting…" : "Log in")
				&& !busy && !string.IsNullOrWhiteSpace(_vaUser) && !string.IsNullOrEmpty(_vaPass))
			{
				string u = _vaUser, p = _vaPass;
				Task.Run(async () => { bool ok = await VaAuth.LoginAsync(u, p); if (ok) _vaJustLoggedIn = true; });
			}
			if (GuiKit.Button(new Rect(box.x + 18f + bw + 10f, by, bw, 32f), "Cancel"))
			{ _vaLoginOpen = false; _vaJustLoggedIn = false; VaAuth.LastError = ""; }
		}

		// Compact preview of up to 3 tags for the roster row. Each tag's plain text is
		// clamped BEFORE markup, so the composed richText is always well-formed (never Trunc'd).
		private static string TagsPreview(VaTagsModule.VaTag[] tags)
		{
			var sb = new System.Text.StringBuilder();
			int show = Mathf.Min(tags.Length, 3);
			for (int i = 0; i < show; i++)
			{
				if (i > 0) sb.Append("<color=#6E6690>, </color>");
				sb.Append(VaTagsModule.RichPreviewClamped(tags[i], 10));
			}
			if (tags.Length > show) sb.Append("<color=#6E6690> +").Append(tags.Length - show).Append("</color>");
			return sb.ToString();
		}

		private static string Trunc(string s, int max)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
		}

		private static VaTagsModule.PlayerEntry FindSelected()
		{
			if (_selectedUid == null) return null;
			var r = VaTagsModule.Roster;
			for (int i = 0; i < r.Count; i++)
				if (r[i].UserId == _selectedUid) return r[i];
			return null;
		}

		// FUN — two columns, because everything in here is a toy and they should be reachable at a
		// glance rather than stacked into a scroll. Left: the chatbox player and the orbit toys.
		// Right: the soundboard. Each column flows top-to-bottom from its own origin, so adding a
		// control to one can never push the other off the panel.
		private static void DrawFun(Rect a)
		{
			float gap = 18f;
			float colW = (a.width - gap) / 2f;
			float rx = a.x + colW + gap;

			// Left column: the chatbox toys. Right column: soundboard on top, video below it.
			DrawFunLeft(new Rect(a.x, a.y, colW, a.height));
			// A rule between the columns instead of relying on the gap alone.
			GuiKit.Fill(new Rect(a.x + colW + gap * 0.5f, a.y, 1f, a.height - 8f), new Color(1f, 1f, 1f, 0.06f));

			float half = (a.height - gap) * 0.52f;
			DrawSoundboard(new Rect(rx, a.y, colW, half));
			GuiKit.Fill(new Rect(rx, a.y + half + 6f, colW, 1f), new Color(1f, 1f, 1f, 0.06f));
			DrawVideo(new Rect(rx, a.y + half + 18f, colW, a.height - half - 18f));
		}

		private static int _vidSel = -1;

		// VIDEO — point a world's player at a URL, on your own client.
		private static void DrawVideo(Rect a)
		{
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "VIDEO PLAYER", _header);
			GUI.Label(new Rect(a.x, a.y + 18f, a.width, 30f),
				"<color=#6E6690>Plays on YOUR client only — nobody else's screen changes. VRChat's own "
				+ "URL allowlist applies.</color>", _dim);

			float y = a.y + 52f;
			if (GuiKit.Button(new Rect(a.x, y, 140f, 28f), "Find players"))
				Modules.VideoModule.Rescan();

			var list = Modules.VideoModule.Snapshot();
			GUI.Label(new Rect(a.x + 150f, y + 5f, a.width - 150f, 20f),
				"<color=#9FB0C8>" + Trunc(Modules.VideoModule.Status, 60) + "</color>", _dim);

			y += 36f;
			GUI.Label(new Rect(a.x, y + 4f, 40f, 20f), "URL", _header);
			Modules.VideoModule.Url = GUI.TextField(new Rect(a.x + 42f, y, a.width - 42f, 26f),
				Modules.VideoModule.Url ?? "", 300);
			if (string.IsNullOrEmpty(Modules.VideoModule.Url))
				GUI.Label(new Rect(a.x + 50f, y + 4f, a.width - 60f, 20f),
					"<color=#4A4266>https://youtu.be/…</color>", _dim);

			y += 34f;
			for (int i = 0; i < list.Count && i < 6; i++, y += 26f)
			{
				var p = list[i];
				bool sel = i == _vidSel;
				if (GuiKit.Button(new Rect(a.x, y, 90f, 22f), sel ? "SELECTED" : "select")) _vidSel = i;
				GUI.Label(new Rect(a.x + 98f, y + 1f, a.width - 98f, 20f),
					"<color=#EAE4FF>" + Trunc(p.Short, 26) + "</color>  <color=#6E6690>"
					+ p.Dist.ToString("F0") + "m · " + p.UrlVar + "</color>", _dim);
			}

			if (list.Count == 0)
				GUI.Label(new Rect(a.x, y, a.width, 20f),
					"<color=#6E6690>press Find players — a video player is any world script with a URL variable</color>", _dim);
			else
			{
				y += 6f;
				if (GuiKit.Button(new Rect(a.x, y, a.width, 34f), "Play it here (local only)"))
				{
					if (_vidSel < 0 || _vidSel >= list.Count) Modules.VideoModule.Status = "select a player first";
					else Modules.VideoModule.PlayLocal(list[_vidSel], Modules.VideoModule.Url);
				}
			}
		}

		private static void DrawFunLeft(Rect a)
		{
			// ================= BAD APPLE =================
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "BAD APPLE CHAT BOX", _header);

			bool playing = BadAppleModule.Playing;
			if (GuiKit.Button(new Rect(a.x, a.y + 22f, a.width, 42f),
					playing ? "STOP  Bad Apple Chat Box" : "PLAY  Bad Apple Chat Box  (RShift+B)"))
				BadAppleModule.RequestToggle();

			ModConfig.BadAppleLoop.Value = GuiKit.Toggle(new Rect(a.x, a.y + 70f, a.width * 0.48f, 32f),
				"Loop", ModConfig.BadAppleLoop.Value);
			GUI.Label(new Rect(a.x + a.width * 0.52f, a.y + 76f, a.width * 0.48f, 20f),
				playing ? "STATUS: <color=#7CFF9E>PLAYING</color>" : "STATUS: <color=#8B949E>IDLE</color>", _status);

			int cadence = ModConfig.BadAppleIntervalMs.Value > 0 ? ModConfig.BadAppleIntervalMs.Value : 500;
			Slider(a.x, a.y + 110f, a.width, "Cadence (ms per frame)", cadence, 100, 1500,
				v => ModConfig.BadAppleIntervalMs.Value = (int)v);

			GUI.Label(new Rect(a.x, a.y + 152f, a.width, 34f),
				"15x12 hanzi shadow-art in your chatbox over OSC (radial menu > Options > OSC). ~500 ms "
				+ "is what survives VRChat's chatbox spam filter.", _dim);

			GuiKit.Fill(new Rect(a.x, a.y + 196f, a.width, 1f), new Color(1f, 1f, 1f, 0.07f));

			// ================= ORBIT =================
			// Lives here rather than under MOVEMENT: none of it is about getting somewhere.
			float oy = a.y + 212f;
			GUI.Label(new Rect(a.x, oy, a.width, 16f), "ORBIT", _header);
			GUI.Label(new Rect(a.x, oy + 18f, a.width, 18f),
				"<color=#6E6690>you around a player (PLAYERS tab), or the map's props around you</color>", _dim);

			// 0.5–8, not 0.5–20: the useful setting is 2m and on a 20m track that sat at 8% of the
			// width, so it was easy to drag far away and hard to get back.
			Slider(a.x, oy + 42f, a.width * 0.48f, "Your orbit radius (m)", ModConfig.OrbitRadius.Value, 0.5f, 8f,
				v => ModConfig.OrbitRadius.Value = v, "F1");
			Slider(a.x + a.width * 0.52f, oy + 42f, a.width * 0.48f, "Your orbit speed", ModConfig.OrbitSpeed.Value, -240f, 240f,
				v => ModConfig.OrbitSpeed.Value = v, "F0");

			bool ring = Modules.ObjectOrbitModule.Active;
			if (GuiKit.Button(new Rect(a.x, oy + 88f, a.width, 38f),
				ring ? "PUT THE OBJECTS BACK" : "SPIN THE WORLD AROUND ME  (RShift+O)"))
				Modules.ObjectOrbitModule.ToggleOnSelf();

			GUI.Label(new Rect(a.x, oy + 130f, a.width, 18f),
				ring
					? "<color=#7CFF9E>" + Modules.ObjectOrbitModule.Count + " object(s) around "
						+ Trunc(Modules.ObjectOrbitModule.CenterName, 18) + "</color>"
					: "<color=#6E6690>LOCAL ONLY — the props move on your screen, nobody else sees it</color>", _dim);

			Slider(a.x, oy + 152f, a.width * 0.48f, "Ring radius (m)", ModConfig.ObjOrbitRadius.Value, 1f, 15f,
				v => ModConfig.ObjOrbitRadius.Value = v, "F1");
			Slider(a.x + a.width * 0.52f, oy + 152f, a.width * 0.48f, "Ring speed", ModConfig.ObjOrbitSpeed.Value, -360f, 360f,
				v => ModConfig.ObjOrbitSpeed.Value = v, "F0");
			Slider(a.x, oy + 194f, a.width * 0.48f, "How many objects", ModConfig.ObjOrbitCount.Value, 1, 120,
				v => ModConfig.ObjOrbitCount.Value = (int)v);
			Slider(a.x + a.width * 0.52f, oy + 194f, a.width * 0.48f, "Search range (m)", ModConfig.ObjOrbitRange.Value, 5f, 80f,
				v => ModConfig.ObjOrbitRange.Value = v, "F0");
			ModConfig.ObjOrbitSpin.Value = GuiKit.Toggle(new Rect(a.x, oy + 236f, a.width * 0.48f, 32f),
				"Tumble the objects", ModConfig.ObjOrbitSpin.Value);
			ModConfig.ObjOrbitSynced.Value = GuiKit.Toggle(new Rect(a.x + a.width * 0.52f, oy + 236f, a.width * 0.48f, 32f),
				"Everyone sees it", ModConfig.ObjOrbitSynced.Value);

			// ================= FORCE GRAB =================
			float fg = oy + 280f;
			GUI.Label(new Rect(a.x, fg, a.width, 16f), "FORCE GRAB", _header);
			bool holding = Modules.ForceGrabModule.Holding;
			if (GuiKit.Button(new Rect(a.x, fg + 22f, a.width * 0.48f, 34f),
					holding ? "DROP  (RShift+G)" : "GRAB WHAT YOU AIM AT  (RShift+G)"))
				Modules.ForceGrabModule.Toggle();
			Slider(a.x + a.width * 0.52f, fg + 24f, a.width * 0.48f, "Reach (m)",
				ModConfig.ForceGrabRange.Value, 5f, 80f, v => ModConfig.ForceGrabRange.Value = v, "F0");
			GUI.Label(new Rect(a.x, fg + 60f, a.width, 32f),
				string.IsNullOrEmpty(Modules.ForceGrabModule.Status)
					? "<color=#6E6690>Look at a pickup and press it: the object comes to your hands and stays "
					  + "there. Left-click uses it, RShift+G drops it. Anything another player is holding is "
					  + "skipped.</color>"
					: "<color=#A99FC4>" + Trunc(Modules.ForceGrabModule.Status, 80) + "</color>", _dim);

			// ================= GRAVITY =================
			float gy = fg + 96f;
			GUI.Label(new Rect(a.x, gy, a.width, 16f), "GRAVITY", _header);
			ModConfig.GravityPlayerOff.Value = GuiKit.Toggle(new Rect(a.x, gy + 20f, a.width * 0.48f, 32f),
				"Float (you)", ModConfig.GravityPlayerOff.Value);
			ModConfig.GravityWorldOff.Value = GuiKit.Toggle(new Rect(a.x + a.width * 0.52f, gy + 20f, a.width * 0.48f, 32f),
				"Objects too", ModConfig.GravityWorldOff.Value);
			GUI.Label(new Rect(a.x, gy + 56f, a.width, 32f),
				"<color=#6E6690>LOCAL ONLY — gravity is simulated per client. Others still see you fall normally, and 'Objects too' can break a world's lifts and puzzles for you.</color>", _dim);
		}

		// SOUNDBOARD — one row per clip: the big button broadcasts, the small one previews.
		// Firing a noise into other people's headsets to find out what it sounds like is exactly
		// the thing worth making unnecessary.
		private static void DrawSoundboard(Rect a)
		{
			GUI.Label(new Rect(a.x, a.y, a.width, 16f), "SOUNDBOARD", _header);
			GUI.Label(new Rect(a.x, a.y + 18f, a.width, 18f),
				"<color=#6E6690>everyone running the mod hears it — a key is sent, never audio</color>", _dim);

			var clips = Modules.SoundboardModule.Clips;
			var heart = AssetLoader.HeartIcon;
			float y = a.y + 44f;

			for (int i = 0; i < clips.Length; i++)
			{
				var c = clips[i];
				var r = new Rect(a.x, y, a.width, 60f);
				if (GuiKit.Button(r, "")) Modules.SoundboardModule.Send(c);

				// The icon rides ON the button rather than replacing the label: identical icons
				// and no words would be a guessing game. A clip with its own image shows that
				// instead of the shared heart, so the board reads at a glance.
				var icon = AssetLoader.Icon(c.Image) ?? heart;
				float tx = r.x + 14f;
				if (icon != null)
				{
					GUI.DrawTexture(new Rect(r.x + 14f, r.y + 12f, 36f, 36f), icon, ScaleMode.ScaleToFit);
					tx = r.x + 60f;
				}
				GUI.Label(new Rect(tx, r.y, r.width - (tx - r.x) - 10f, 60f), c.Label, _bigName);

				if (GuiKit.Button(new Rect(a.x, y + 64f, a.width, 26f), "preview — only you"))
					Modules.SoundboardModule.Preview(c);

				y += 100f;
			}

			ModConfig.SoundboardEnabled.Value = GuiKit.Toggle(new Rect(a.x, y + 8f, a.width * 0.48f, 34f),
				"Hear others", ModConfig.SoundboardEnabled.Value);
			Slider(a.x + a.width * 0.52f, y + 10f, a.width * 0.48f, "Incoming volume",
				ModConfig.SoundboardVolume.Value, 0f, 1f, v => ModConfig.SoundboardVolume.Value = v, "F2");

			GUI.Label(new Rect(a.x, y + 54f, a.width, 34f),
				string.IsNullOrEmpty(Modules.SoundboardModule.LastStatus)
					? (VaAuth.IsAuthed
						? "<color=#6E6690>ready — press a clip and every mod hears it, under your name</color>"
						: "<color=#6E6690>ready — press a clip and every mod hears it (you show as 'guest')</color>")
					: "<color=#A99FC4>" + Trunc(Modules.SoundboardModule.LastStatus, 90) + "</color>", _dim);

			// WHO PLAYED WHAT, AND WHEN. "last heard" alone could not tell one person spamming from
			// six people playing along, which is the only thing worth knowing on a shared board.
			float hy = y + 92f;
			GUI.Label(new Rect(a.x, hy, a.width, 16f), "ACTIVITY", _header);
			var hist = Modules.SoundboardModule.Recent();
			if (hist.Count == 0)
				GUI.Label(new Rect(a.x, hy + 20f, a.width, 18f), "<color=#6E6690>nothing heard yet</color>", _dim);

			float rowsTop = hy + 20f;
			float avail = Mathf.Max(0f, a.height - (rowsTop - a.y) - 6f);
			int fits = Mathf.Max(0, Mathf.FloorToInt(avail / 18f));
			for (int i = 0; i < hist.Count && i < fits; i++)
			{
				var e = hist[i];
				GUI.Label(new Rect(a.x, rowsTop + i * 18f, a.width, 18f),
					"<color=#4A5568>" + e.When + "</color>   "
					+ "<color=#EAF6FF>" + Trunc(e.Who, 22) + "</color>   "
					+ "<color=#FF6AD5>" + Trunc(e.What, 26) + "</color>", _dim);
			}
		}

		private static void Slider(float x, float y, float w, string label, float value, float min, float max, Action<float> setter, string fmt = "F0")
		{
			var lbl = new Rect(x, y, w, 16f);
			var bar = new Rect(x, y + 18f, w, 20f);
			float nv = GuiKit.Slider(lbl, bar, label, value, min, max, fmt);
			if (Mathf.Abs(nv - value) > (fmt == "F0" ? 0.5f : 0.001f)) setter(nv);
		}

		private static void ResetThresholds()
		{
			ModConfig.MaxParticleSystems.Value = 256;
			ModConfig.MaxLights.Value = 8;
			ModConfig.MaxAudioSources.Value = 150;
			ModConfig.MaxCloth.Value = 75;
			ModConfig.MaxPhysBones.Value = 256;
			ModConfig.MaxContacts.Value = 256;
			ModConfig.ScanIntervalFrames.Value = 30;
		}

		// ================= module signals =================

		private static bool _rescanRequested;

		// Asked for from the QuickMenu's Protection sub-page, which has no access to the private
		// flag the IMGUI button sets.
		public static void RequestRescan() { _rescanRequested = true; }
		public static bool ConsumeRescanRequest()
		{
			if (!_rescanRequested) return false;
			_rescanRequested = false;
			return true;
		}

		private static bool _uiDumpRequested;
		public static bool ConsumeUiDumpRequest()
		{
			if (!_uiDumpRequested) return false;
			_uiDumpRequested = false;
			return true;
		}

		private static bool _fewTagsRefreshRequested;
		public static bool ConsumeFewTagsRefreshRequest()
		{
			if (!_fewTagsRefreshRequested) return false;
			_fewTagsRefreshRequested = false;
			return true;
		}

		// ================= chrome helpers =================

		private static void DrawBarcode(Rect r)
		{
			GUI.color = new Color(1f, 1f, 1f, 0.5f);
			float x = r.x;
			for (int i = 0; i < 60 && x + 2f <= r.xMax; i++)
			{
				float w = (i % 6 == 0) ? 6f : 2f;
				if (x + w > r.xMax) break;
				GUI.DrawTexture(new Rect(x, r.y, w, r.height), GuiKit.Pixel);
				x += w + 2f;
			}
			GUI.color = Color.white;
		}

		// Background image (embedded) with a dark overlay for readability; falls back to a
		// flat dark fill if the image isn't available.
		private static void DrawBackground()
		{
			const float wr = 22f;

			// Soft outer glow so the window feels like it floats. Violet, not the accent preset:
			// the window frame is brand chrome and stays the Archive colour.
			GuiKit.SoftGlow(_win, Violet, wr, 0.12f, 9, 3.2f);

			// WALLPAPER FIRST, then the body tint OVER it. The old order drew the photo last and
			// inset, so it sat as a bright rectangle on top of the panel; painting it underneath a
			// translucent violet-black body is what makes it read as a background instead.
			var bg = AssetLoader.Background;
			if (bg != null)
			{
				// Clip the photo to the window's rounded shape by drawing it into the same rounded
				// rect the body uses, so no square corners poke out of the panel.
				GUI.color = new Color(1f, 1f, 1f, 0.55f);
				GUI.DrawTexture(_win, bg, ScaleMode.ScaleAndCrop, true, 0f,
					new Color(1f, 1f, 1f, 0.55f), Vector4.zero, new Vector4(wr, wr, wr, wr));
				GUI.color = Color.white;
			}

			// Violet-black glass over the photo: dark enough to read white text on, sheer enough
			// that the wallpaper still shows through.
			Color body = new Color(0.055f, 0.043f, 0.086f, bg != null ? 0.88f : 0.985f);
			GuiKit.RoundedBorder(_win, body, VioletRim, wr, 1.5f);
		}

		// Brand watermark intentionally left blank (logo removed).
		private static void DrawWatermark()
		{
		}

		private static void HandleDrag()
		{
			// A mouse-up outside the window never reaches the GUI event stream, which used
			// to leave the window glued to the cursor.
			if (_dragging && !Input.GetMouseButton(0)) _dragging = false;
			var e = Event.current;
			// Drag by the HEADER BAND only. It used to be 118px — taller than the redesigned header —
			// so grabbing the first tabs moved the window instead of switching page.
			var titleBar = new Rect(_win.x, _win.y, _win.width, HeaderH);
			switch (e.type)
			{
				case EventType.MouseDown:
					if (titleBar.Contains(e.mousePosition)) { _dragging = true; _dragOffset = e.mousePosition - new Vector2(_win.x, _win.y); e.Use(); }
					break;
				case EventType.MouseDrag:
					if (_dragging) { _win.x = e.mousePosition.x - _dragOffset.x; _win.y = e.mousePosition.y - _dragOffset.y; e.Use(); }
					break;
				case EventType.MouseUp:
					if (_dragging) { _dragging = false; e.Use(); }
					break;
			}
		}

		// ================= styles =================

		// IMGUI's built-in font has NO emoji glyphs, which is why an emoji typed or pasted into a
		// tag showed up as nothing in the field, nothing in the preview — yet rendered perfectly
		// above the nameplate, because that path uses VRChat's TMP font instead. Building a dynamic
		// font from the OS with an emoji fallback gives the menu the missing glyphs. Windows ships
		// Segoe UI Emoji; if the call is unavailable the styles simply keep the default font and
		// nothing else changes.
		private static Font _emojiFont;
		private static bool _emojiFontTried;

		// Getting an OS font out of this build takes more than the obvious call. A metadata dump of
		// the shipped UnityEngine.TextRenderingModule (captures, 2026-08-24) shows UnityEngine.Font
		// keeps a native method pointer for the PARAMETERLESS constructor only — the string and
		// string[] constructors survive as empty stubs, so both CreateDynamicFontFromOSFont
		// overloads (which route through them) throw "Method not found". That is the warning this
		// used to print on every launch.
		//
		// Internal_CreateFont(Font, string) DOES have a native pointer, and it is what Unity's own
		// Font(string) constructor calls. So the working path is: parameterless ctor, then bind a
		// name through that private static. Every route is tried in turn and the one that worked is
		// logged, because "the font failed" is useless when four things could have produced it.
		private static readonly string[] EmojiFontNames =
		{
			"Segoe UI Emoji", "Segoe UI Symbol", "Segoe UI", "Arial Unicode MS", "Arial",
		};

		// A Font object can exist and still draw NOTHING. The parameterless-constructor route below
		// produces exactly that: an object with no character data, and every style handed it renders
		// blank — which is how whole menu sections (credits, the trust legend) went invisible while
		// their coloured swatches and headers kept drawing. So nothing is adopted until it proves it
		// can supply an ordinary glyph.
		private static bool Usable(Font f)
		{
			try
			{
				if (f == null) return false;
				if (f.material == null) return false;
				return f.HasCharacter('A');
			}
			catch { return false; }
		}

		private static Font EmojiFont()
		{
			if (_emojiFontTried) return _emojiFont;
			_emojiFontTried = true;

			// 1. The single-string overload — cheapest, and independent of the array ctor.
			foreach (string name in EmojiFontNames)
			{
				try
				{
					var f = Font.CreateDynamicFontFromOSFont(name, 16);
					if (Usable(f))
					{
						_emojiFont = f;
						VRChatArchiveModPlugin.Logger.LogInfo($"[Menu] emoji font '{name}' loaded (OS font, single name).");
						return _emojiFont;
					}
				}
				catch { }
			}

			// 2. The array overload, in case a future build restores it.
			try
			{
				var f = Font.CreateDynamicFontFromOSFont(EmojiFontNames, 16);
				if (Usable(f))
				{
					_emojiFont = f;
					VRChatArchiveModPlugin.Logger.LogInfo("[Menu] emoji font loaded (OS font, fallback chain).");
					return _emojiFont;
				}
			}
			catch { }

			// 3. Parameterless ctor + Internal_CreateFont, the one route the dump proves is present.
			try
			{
				var mi = typeof(Font).GetMethod("Internal_CreateFont",
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Public);
				if (mi != null)
				{
					foreach (string name in EmojiFontNames)
					{
						try
						{
							var f = new Font();
							mi.Invoke(null, new object[] { f, name });
							if (Usable(f))
							{
								_emojiFont = f;
								VRChatArchiveModPlugin.Logger.LogInfo($"[Menu] emoji font '{name}' loaded (Internal_CreateFont).");
								return _emojiFont;
							}
						}
						catch { }
					}
				}
			}
			catch { }

			// Not fatal: the menu keeps IMGUI's default font. Emoji still render above nameplates,
			// which goes through VRChat's TMP font and never touched any of this.
			VRChatArchiveModPlugin.Logger.LogWarning(
				"[Menu] no OS font could be created on this build — emoji stay invisible in the menu "
				+ "(they still render in-world and are still saved correctly).");
			return _emojiFont;
		}

		// Applied to the styles that actually show tag TEXT: the editor field, the preview and the
		// tag rows. Everything else keeps IMGUI's default font.
		private static void ApplyEmojiFont()
		{
			var f = EmojiFont();
			if (f == null) return;
			try
			{
				if (_rowTag != null) _rowTag.font = f;
				if (_rowTagRight != null) _rowTagRight.font = f;
				if (_previewBig != null) _previewBig.font = f;
				if (_bigName != null) _bigName.font = f;
				if (_dim != null) _dim.font = f;
				// GUI.skin is NOT read here: GUISkin's getters are mis-bound on this build and reading
				// one is a fatal access violation, not a catchable error (see GuiCompat.BaseStyle). The
				// mod's own text-field style carries the emoji font instead.
			}
			catch { }
		}

		private static void EnsureStyles()
		{
			if (_styles) return;

			// Wordmark + "MOD MENU" eyebrow: compact, so the header is a band and not a billboard.
			_title    = new GUIStyle { fontSize = 19, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
			_subtitle = new GUIStyle { fontSize = 9, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = TextFaint } };
			_tabStyle = new GUIStyle { alignment = TextAnchor.MiddleLeft, fontSize = 13, fontStyle = FontStyle.Bold, richText = true, normal = { textColor = TextMuted } };
			_tabActiveStyle = new GUIStyle(_tabStyle) { normal = { textColor = Color.white } };
			_pageTitle = new GUIStyle { fontSize = 17, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
			_railFoot = new GUIStyle { fontSize = 9, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = TextFaint } };
			// Section headers read as quiet uppercase labels, not accent-coloured shouting.
			_header   = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, richText = true, normal = { textColor = TextFaint } };
			_dim      = new GUIStyle { fontSize = 11, richText = true, wordWrap = true, normal = { textColor = TextMuted } };
			_zone     = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = TextFaint } };
			_cardLabel = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = TextFaint } };
			_cardValue = new GUIStyle { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true, normal = { textColor = Color.white } };
			_status   = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(0.75f, 0.77f, 0.82f) } };
			_swatch   = new GUIStyle { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter, normal = { textColor = Color.white } };
			_rowName  = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.92f, 0.96f, 1f) } };
			_statusPill = new GUIStyle { fontSize = 12, richText = true, wordWrap = false, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
			_rowTagRight = new GUIStyle { fontSize = 12, richText = true, alignment = TextAnchor.MiddleRight, normal = { textColor = Color.white } };
			_rowTag   = new GUIStyle { fontSize = 13, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
			_bigName  = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
			_previewBig = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
			_favName  = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, richText = true, wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.93f, 0.97f, 1f) } };
			_favSub   = new GUIStyle { fontSize = 11, richText = true, wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.55f, 0.6f, 0.7f) } };
			_favMid   = new GUIStyle { fontSize = 11, richText = true, wordWrap = false, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.35f, 0.42f, 0.52f) } };

			ApplyEmojiFont();
			_styles = true;
		}

		private static string FormatBytes(long b)
		{
			if (b <= 0) return "0";
			string[] u = { "B", "KB", "MB", "GB" };
			double v = b; int i = 0;
			while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
			return v.ToString("0.#") + u[i];
		}
	}
}
