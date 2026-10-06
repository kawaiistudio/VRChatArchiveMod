using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace VRChatArchiveMod.Core
{
	// THE PER-FRAME PUMP, WITHOUT INJECTING A TYPE INTO IL2CPP.
	//
	// ModRunner is a MonoBehaviour registered through ClassInjector, and on this VRChat build the
	// process ends inside AddComponent<ModRunner>() -- with or without the modules loaded, with or
	// without OnGUI declared. Class injection builds an Il2CppClass by hand, and this build's class
	// layout is shuffled beyond what the struct handlers repair, so the object il2cpp instantiates is
	// one it cannot survive. The delegate bridge dies the same way, on its own injected type.
	//
	// Nothing about the pump needs a component of its own. Unity already calls a handful of managed
	// methods from native every frame, by name, and Harmony detours those natively -- the same
	// mechanism every game hook in this mod relies on. So the pump rides on them instead:
	//
	//   Update       EventSystem.Update            the UI event system ticks every frame
	//   LateUpdate   Canvas.SendWillRenderCanvases  once per frame, after every LateUpdate, before render
	//                Camera.FireOnPreRender         same moment, per camera -- whichever fires first wins
	//   FixedUpdate  accumulated from Update against VaClock.FixedDelta, capped at 4 steps a frame
	//   OnGUI        GUIUtility.BeginGUI            native calls it right before a behaviour's OnGUI,
	//                                              so a postfix runs INSIDE the IMGUI context
	//
	// Each callback is idempotent per frame (Time.frameCount), so several cameras or several UI
	// behaviours do not multiply the work, and Update falls back to the render anchor if no
	// EventSystem is enabled. OnGUI needs at least one game behaviour that declares OnGUI; if none
	// does, IMGUI features stay dark and it is said so once, rather than crashing anything.
	internal static class FramePump
	{
		private static int _updFrame = -1, _lateFrame = -1, _lastScene = -1, _guiOwner, _guiFrame = -1, _guiTick;
		private static float _fixedAcc;
		private static bool _first = true, _firstGui = true, _guiWarned, _guiHostUp;
		private static int _anchors;

		internal static bool Installed => _anchors > 0;

		internal static void Install()
		{
			Hook(typeof(EventSystem), "Update", nameof(OnUpdateAnchor), new Type[0]);
			Hook(typeof(Canvas), "SendWillRenderCanvases", nameof(OnRenderAnchor), new Type[0]);
			Hook(typeof(Camera), "FireOnPreRender", nameof(OnRenderAnchor), new[] { typeof(Camera) });
			Hook(typeof(GUIUtility), "BeginGUI", nameof(OnGuiAnchor), new[] { typeof(int), typeof(int), typeof(int) });

			// Never leave the game with its input suspended: same contract ModRunner.OnDestroy kept.
			AppDomain.CurrentDomain.ProcessExit += (_, __) =>
			{
				try { Menu.Visible = false; Menu.EnforceCursor(); } catch { }
				try { ModuleManager.ShutdownAll(); } catch { }
			};

			VRChatArchiveModPlugin.Logger.LogInfo("[FramePump] " + _anchors + "/4 ancres posees -- le pump tourne sans type injecte.");
		}

		private static void Hook(Type t, string method, string postfix, Type[] args)
		{
			try
			{
				var target = AccessTools.Method(t, method, args);
				if (target == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[FramePump] " + t.Name + "." + method + " absent de l'interop -- ancre ignoree.");
					return;
				}
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, postfix: new HarmonyMethod(
					typeof(FramePump).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
				_anchors++;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[FramePump] " + t.Name + "." + method + " : " + e.Message);
			}
		}

		// ---- anchors -----------------------------------------------------------------------------

		private static void OnUpdateAnchor() { RunUpdate(); TickGuiHost(); }

		private static void OnRenderAnchor()
		{
			RunUpdate();          // no-op when EventSystem.Update already ran this frame
			TickGuiHost();        // and the render anchor keeps feeding the host search when Update stalls
			int f = Time.frameCount;
			if (f == _lateFrame) return;
			_lateFrame = f;
			try
			{
				Menu.EnforceCursor();
				ModuleManager.LateUpdate();
			}
			catch (Exception e) { Once("LateUpdate", e); }
		}

		private static void OnGuiAnchor(int instanceID)
		{
			// One behaviour per frame owns the drawing, so two game objects with OnGUI do not draw
			// the menu twice or feed one click to two buttons.
			int f = Time.frameCount;
			if (f != _guiFrame) { _guiFrame = f; _guiOwner = instanceID; }
			else if (instanceID != _guiOwner) return;

			if (_firstGui)
			{
				_firstGui = false;
				VRChatArchiveModPlugin.Step("pump: premier OnGUI (behaviour " + instanceID + ")");
			}
			try
			{
				// Before anything of the mod's draws: exercise the IMGUI primitives once, inside a
				// real OnGUI, so a mis-bound one is named in driver.log instead of killing the game
				// later from whichever panel happened to touch it first (see GuiSelfTest).
				GuiSelfTest.RunOnce();

				var ev = Event.current;
				if (ev != null && ev.type == EventType.Repaint) Menu.EnforceCursor();
				ModuleManager.OnGui();
				Menu.Draw();
			}
			catch (Exception e) { Once("OnGUI", e); }
		}

		// ---- update ------------------------------------------------------------------------------

		private static void RunUpdate()
		{
			int f = Time.frameCount;
			if (f == _updFrame) return;
			_updFrame = f;

			VaClock.Tick();   // advance the managed frame-delta clock once per frame (see VaClock)

			// THE INSPECT QUERIES RUN HERE, and only here: reading a Transform from the socket thread
			// is an access violation. Bounded per frame, and a no-op while the server is shut.
			try { InspectServer.Pump(); } catch { }

			if (_first)
			{
				_first = false;
				VRChatArchiveModPlugin.Step("pump: premier Update (frame " + f + ")");
				VRChatArchiveModPlugin.Logger.LogInfo("[FramePump] premier tick, frame " + f + ".");
			}

			try
			{
				int idx = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
				if (idx != _lastScene) { _lastScene = idx; ModuleManager.SceneLoaded(idx); }
			}
			catch { }

			try
			{
				Menu.HandleInput();
				Menu.EnforceCursor();
				ModuleManager.ProfileTick();
				ModuleManager.Update();
			}
			catch (Exception e) { Once("Update", e); }

			// Physics-rate modules get their steps from the frame clock. Capped so a hitch does not
			// turn into a burst of catch-up steps, which is the spiral the orbit modules already
			// learned to avoid.
			try
			{
				float fdt = VaClock.FixedDelta;
				if (fdt > 0f)
				{
					_fixedAcc += Mathf.Min(VaClock.Delta, 0.25f);
					int steps = 0;
					while (_fixedAcc >= fdt && steps++ < 4) { _fixedAcc -= fdt; ModuleManager.FixedUpdate(); }
					if (_fixedAcc > fdt * 4f) _fixedAcc = 0f;
				}
			}
			catch (Exception e) { Once("FixedUpdate", e); }

			// The IMGUI host is sought by TickGuiHost, driven off every raw anchor fire rather than off
			// this frame clock -- see there for why the frame count cannot be trusted to advance here.
		}

		// ---- IMGUI host --------------------------------------------------------------------------

		// UNITY ONLY RUNS ITS IMGUI LOOP IF SOME ENABLED MonoBehaviour DECLARES OnGUI.
		//
		// The mod used to be that behaviour: ModRunner declared OnGUI and everything drew inside it.
		// Class injection is dead on this build, and a scan of the interop shows nothing VRChat itself
		// runs declares OnGUI either -- so GUIUtility.BeginGUI never fires, the postfix above never
		// runs, and the menu, radar, ESP text and HUD have no context to draw in.
		//
		// They do not need OUR behaviour, only SOME behaviour. Three types in the shipped assemblies
		// declare OnGUI and declare no other Unity callback at all, so attaching one starts the whole
		// IMGUI pipeline -- input included -- and costs one empty call a frame. They are third-party
		// library types (UniTask, FinalIK, Unity Purchasing), so unlike VRChat's own classes their
		// names are plain text and survive a game update.
		//
		// Ordered by how little they do: AsyncGUITrigger is a passive trigger that raises an event
		// nobody is awaiting; DemoGUIMessage draws an empty label; the store window is the last resort.
		private static readonly string[][] GuiHosts =
		{
			new[] { "UniTask.dll", "Cysharp.Threading.Tasks.Triggers", "AsyncGUITrigger" },
			new[] { "Assembly-CSharp-firstpass.dll", "RootMotion", "DemoGUIMessage" },
			new[] { "UnityEngine.Purchasing.Stores.dll", "UnityEngine.Purchasing", "UIFakeStoreWindow" },
		};

		private static GameObject _guiHost;
		private static readonly System.Collections.Generic.HashSet<string> _hostLogged = new System.Collections.Generic.HashSet<string>();

		// DRIVEN OFF EVERY RAW ANCHOR FIRE, not off Time.frameCount.
		//
		// The old code called EnsureGuiHost once, gated behind frame > 120 of the Update pump. On this
		// build that gate never opened: the Update pump rides on the loading-screen EventSystem, which
		// is torn down a frame or two in, so the frame clock the gate read stopped advancing before it
		// reached 120 -- the host was never attached, GUIUtility.BeginGUI never fired, and the menu,
		// radar and HUD never drew (no "premier OnGUI" in driver.log). The render anchors keep firing
		// regardless, so the host search now rides on a plain count of anchor callbacks and retries
		// until one host sticks, instead of a single shot behind a clock that may be frozen.
		private static void TickGuiHost()
		{
			if (_guiHostUp || !_firstGui) return;
			_guiTick++;
			if (_guiTick < 4) return;            // a brief warmup so the library assemblies are loaded
			if ((_guiTick & 3) != 0) return;     // then an attempt roughly every fourth fire, not every one
			if (_guiTick > 1200)
			{
				if (!_guiWarned)
				{
					_guiWarned = true;
					VRChatArchiveModPlugin.Logger.LogWarning("[FramePump] aucun hote IMGUI n'a pu etre attache — menu, radar et HUD resteront eteints.");
				}
				return;
			}
			EnsureGuiHost();
		}

		private static void EnsureGuiHost()
		{
			foreach (var c in GuiHosts)
			{
				try
				{
					IntPtr k = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(c[0], c[1], c[2]);
					if (k == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(k))
					{
						if (_hostLogged.Add(c[2]))
							VRChatArchiveModPlugin.Logger.LogInfo($"[FramePump] hote IMGUI {c[2]} absent de ce build, candidat suivant.");
						continue;
					}
					if (_guiHost == null)
					{
						_guiHost = new GameObject("VRChatArchiveMod.ImguiHost");
						UnityEngine.Object.DontDestroyOnLoad(_guiHost);
					}
					var comp = _guiHost.AddComponent(Il2CppInterop.Runtime.Il2CppType.TypeFromPointer(k));
					if (comp == null)
					{
						if (_hostLogged.Add(c[2] + "#null"))
							VRChatArchiveModPlugin.Logger.LogWarning($"[FramePump] AddComponent({c[2]}) a rendu null.");
						continue;
					}
					_guiHostUp = true;
					VRChatArchiveModPlugin.Logger.LogInfo($"[FramePump] boucle IMGUI relancee via {c[1]}.{c[2]} — menu, radar et HUD peuvent dessiner.");
					return;
				}
				catch (Exception e)
				{
					if (_hostLogged.Add(c[2] + "#ex"))
						VRChatArchiveModPlugin.Logger.LogWarning($"[FramePump] hote IMGUI {c[2]} : {e.GetType().Name} {e.Message}");
				}
			}
			// No candidate took this pass; TickGuiHost will call again on a later anchor fire.
		}

		private static int _onceCount;
		private static void Once(string where, Exception e)
		{
			if (_onceCount++ > 20) return;
			VRChatArchiveModPlugin.Logger.LogWarning("[FramePump] " + where + " : " + e.GetType().Name + " : " + e.Message);
		}
	}
}
