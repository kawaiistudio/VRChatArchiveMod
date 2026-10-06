using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using VRChatArchiveMod.Core;
using VRChatArchiveMod.Modules;

namespace VRChatArchiveMod
{
	// Entry point for VRCHAT ARCHIVE MOD as a BepInEx 6 (IL2CPP) plugin.
	// Ported from the MelonLoader-based Munchen client to run on current VRChat.
	[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
	public class VRChatArchiveModPlugin : BasePlugin
	{
		internal static ManualLogSource Logger;
		internal static Harmony HarmonyInstance;

		public override void Load()
		{
			Logger = base.Log;
			// FIRST: silence the benign Il2CppInterop red-error spam (stripped constants, absent
			// assemblies) so the real log is readable. Never fails loudly — see LogNoiseFilter.
			Core.LogNoiseFilter.Install();
			// Ownership banner — printed in clear ON PURPOSE. Establishes provenance in the log and
			// is the first thing anyone (or any AI) inspecting a running instance reads.
			Logger.LogMessage("==================================================================");
			Logger.LogMessage("  VRCHAT ARCHIVE MOD  (c) 2026 Kawaii Studio. All rights reserved.");
			Logger.LogMessage("  PROPRIETARY & LICENSE-PROTECTED. Reverse engineering, deobfusca-");
			Logger.LogMessage("  tion, patching or circumvention is prohibited (DMCA 17 USC 1201).");
			Logger.LogMessage("  Licensed only via the official VRChat Archive Client. vrchatarchive.org");
			Logger.LogMessage("==================================================================");
			Logger.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loading (BepInEx IL2CPP)...");

			ModConfig.Init(Config);
			HarmonyInstance = new Harmony(PluginInfo.Guid);

			// Before ANY module runs: without this, every type initializer that needs a nested
			// il2cpp type throws, and a type initializer only ever throws once � the failure is
			// cached for the life of the process, so repairing it later would be too late.
			// FIRST: every field access in the process reads through this. Until it is repaired,
			// anything touching a field_* member reads unmapped memory and ends the process.
			// FIRST OF ALL: this build no longer has every type the interop assemblies were generated
			// against, and Il2CppInterop hands a missing type's null class pointer straight to
			// il2cpp_runtime_class_init, which ends the process. Nothing below can run until that is
			// neutralised, because a type initializer fires the moment a module class is touched.
			MissingTypeGuard.Install();

			// SECOND: the interop binds methods by 1886 token, and on this build every token in a
			// changed assembly names the NEXT method. Undone here, before any proxy the modules use is
			// initialised -- and the core proxies already initialised by BepInEx are re-bound inside.
			TokenShiftFix.Install();

			// THIRD: a class VRChat renamed is found again by its shape, but that only gives back its
			// identity -- its members still answer to 1886 tokens and 1886 names, both meaningless here.
			// Lining its whole member sequence up against the table by SHAPE (arity, staticness, the kind
			// of each parameter, none of which a rename can touch) rebuilds the binding table for the
			// class and, with it, every feature that had to call into one.
			MemberAlign.Install();

			// A GATE ON THE CALL ITSELF WAS TRIED HERE AND TAKEN BACK OUT.
			//
			// Patching il2cpp_runtime_invoke does catch what nothing else can -- a call on a member with
			// no code behind it, or on an object of the wrong type -- and it did remove that crash. But it
			// sits on the single hottest function in the whole interop, and skipping a call there leaves
			// Il2CppInterop's own bookkeeping half-done: the first attempt sent the exception path into
			// infinite recursion, and the second took the object pool down with it. The checks it made are
			// worth keeping, so they now happen at RESOLUTION time instead (MemberAlign.Callable and
			// ShapeOk), where being wrong costs a feature rather than the process.
			// InvokeGuard.Install();

			// FOURTH: with missing members now resolving to a stub instead of poisoning their type, a
			// stub must not be patched or invoked by mistake — that guard is an ordinary method, so it
			// refuses in a way the modules can catch.
			ProxyGuard.Install();

			// Watches VRChat parse avatars, so the id of whatever the menu shows comes from the API
			// instead of being hunted for in an obfuscated UI subtree. Installed with the other
			// il2cpp repairs, before any menu exists: it must already be listening for the FIRST
			// list the client loads, or the avatars fetched during login are never indexed.
			Core.AvatarIndex.Install();

			FieldOffsetFix.Install();

			NestedTypeFix.Install();

			// Re-aim the interop's IMGUI methods (GUIStyle/GUIStyleState/GUIContent/GUI/...) at the
			// REAL native methods by NAME from live metadata — TokenShiftFix's by-token re-aim lands on
			// the wrong neighbour for these, which is why their setters/ctors crashed or dropped values.
			// Runs before any drawing so styles apply their colours and sizes for real.
			Core.GuiRebind.Install();


			// Register feature modules here as each wave is ported.
			if (System.Environment.GetEnvironmentVariable("VA_NO_MODULES") != "1") RegisterModules();
			// Fallback for any IMGUI setter GuiRebind could not re-aim (ambiguous overload): neutralise
			// it so a style still builds. After GuiRebind most setters pass the probe and are left alone.
			Core.GuiCompat.Install();
			if (System.Environment.GetEnvironmentVariable("VA_NO_MODULES") != "1") ModuleManager.InitializeAll();

			// Copy the decrypted 1903 metadata out, once, on a background thread. It replaces the whole
			// probe-relaunch-read-the-log loop with an offline lookup, and it cannot stutter a frame
			// because it never touches il2cpp nor the main thread.
			// DESACTIVE : le bloc en clair n'est pas en memoire sur ce build (0 magic dans 4134 Mo,
			// 4465 regions, mesure le 2026-09-18). VRChat ne ship pas un il2cpp standard, donc rien ne
			// leur impose de garder le sanity 0xFAB11BAF. Et meme trouve, il ne rendrait pas les vrais
			// noms : l'obfuscateur les REMPLACE, la metadata ne contient que les noms brouilles.
			// Core.MetadataDump.StartOnce();

			// Spin up the persistent runtime driver (per-frame update pump).
			if (System.Environment.GetEnvironmentVariable("VA_NO_DRIVER") != "1") InjectRuntimeDriver();

			// The repair list for this VRChat build: every type the mod asks for that 1903 no longer
			// has. Printed once, after registration, so a future interop regeneration can be checked
			// against it.
			if (MissingTypeGuard.Armed)
			{
				var missing = MissingTypeGuard.Missing;
				if (missing.Count == 0)
					Logger.LogInfo("[MissingTypeGuard] aucun type manquant — l'interop colle a ce build.");
				else
				{
					Logger.LogWarning($"[MissingTypeGuard] {missing.Count} type(s) absent(s) de ce build de VRChat :");
					foreach (var m in missing) Logger.LogWarning("    " + m);
				}
			}

			if (TokenShiftFix.Armed) Logger.LogInfo("[TokenShiftFix] " + TokenShiftFix.Summary);
			if (MemberAlign.Armed) Logger.LogInfo("[MemberAlign] " + MemberAlign.Summary);

			Logger.LogInfo($"{PluginInfo.Name} loaded. Protections & QoL port in progress.");
		}


		// Flushed trace of module CONSTRUCTION. InitializeAll() is never reached, so the fault is in
		// a module constructor or a static initializer -- and a native access violation kills the
		// process before any buffered log reaches disk. Whatever this file ends on is the culprit.
		private static int _vaCount;

		private static bool VaAllow(string name)
		{
			int max = 0;
			try { int.TryParse(System.Environment.GetEnvironmentVariable("VA_MAX_MODULES"), out max); } catch { }
			if (max > 0 && _vaCount >= max) return false;
			_vaCount++;
			try
			{
				string f = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "module-ctor.log");
				using var fs = new System.IO.FileStream(f, System.IO.FileMode.Append,
					System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
				var b = System.Text.Encoding.UTF8.GetBytes(
					System.DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + _vaCount + "] ctor -> " + name + System.Environment.NewLine);
				fs.Write(b, 0, b.Length);
				fs.Flush(true);
			}
			catch { }
			return true;
		}

		private static void RegisterModules()
		{
			// Registered first: it subscribes to the log so it captures every module below.
			if (VaAllow("DiagnosticsModule")) ModuleManager.Register(new DiagnosticsModule());
			// Instruments only � the spike hunter costs one float compare per frame when idle.
			if (VaAllow("AntiCrashModule")) ModuleManager.Register(new AntiCrashModule());
			// SELF HIDE: your own avatar not drawn on your screen (AntiCrash/SelfHide).
			if (VaAllow("SelfHideModule")) ModuleManager.Register(new SelfHideModule());
			// FLOAT OBJECTS: gravity off on every pickup body (FUN toggle).
			if (VaAllow("ObjectGravityModule")) ModuleManager.Register(new ObjectGravityModule());
			// CUSTOM USERNAME: what the world's Udon scripts are told your name is (local write).
			if (VaAllow("SpoofModule")) ModuleManager.Register(new SpoofModule());
			// AFK / SEATED / IN STATION / VR, from each avatar's own animator parameters.
			if (VaAllow("PlayerStatesModule")) ModuleManager.Register(new PlayerStatesModule());
			// MIMIC CHATBOX: repeat a chosen player's chatbox text into yours (read only).
			if (VaAllow("ChatMimicModule")) ModuleManager.Register(new ChatMimicModule());
			// FAST SYNC: VRChat's own fast serialisation rate for the local player.
			if (VaAllow("FastSyncModule")) ModuleManager.Register(new FastSyncModule());
			// VRC+ status, patched once. Gated by Spoof/VRCPlus at read time, so arming it
			// here costs nothing while the setting is off.
			if (VaAllow("VRCPlusSpoof.Patch")) { try { Core.VRCPlusSpoof.Patch(); } catch { } }
			// Store ownership, patched once. Gated by Spoof/StoreOwnership at read time.
			if (VaAllow("EcoPatcher.Patch")) { try { Core.EcoPatcher.Patch(); } catch { } }
			// VRC+ cosmetics (loading screens, props, emojis...): resolved at runtime, not
			// by a hardcoded class name, because every type here is re-obfuscated per build.
			if (VaAllow("VrcPlusItems.Patch")) { try { Core.VrcPlusItems.Patch(); } catch { } }
			// MENU BACKGROUNDS: the VRC+ backgrounds (parallax included) shown locally. Flips one bool
			// on the game's own BackgroundOption assets — no API model is touched, nothing is sent.
			if (VaAllow("VrcPlusBackgroundsModule")) ModuleManager.Register(new VrcPlusBackgroundsModule());
			if (VaAllow("LaunchpadConsoleModule")) ModuleManager.Register(new LaunchpadConsoleModule());
			if (VaAllow("CacheWatchModule")) ModuleManager.Register(new CacheWatchModule());
			// [open-source build] DEX CAPTURE is not part of this repository. It was a paid feature
			// (detect DexProtect avatars, grab the decoded mesh + runtime unlock params). Removed here.
			// if (VaAllow("DexCaptureModule")) ModuleManager.Register(new DexCaptureModule());
			// WHO BLOCKED ME: fills the sets the player lists tag with. Draws nothing, asks once.
			if (VaAllow("BlockedByProbeModule")) ModuleManager.Register(new BlockedByProbeModule());
			// NSFW FILTER: keyword-named renderers on other avatars switched off (PROTECTION toggle).
			if (VaAllow("NsfwFilterModule")) ModuleManager.Register(new NsfwFilterModule());
			// Receive-side Photon filter: drops crasher event codes before VRChat ever sees them.
			if (VaAllow("PhotonGuardModule")) ModuleManager.Register(new PhotonGuardModule());
			// Send-side counter: what THIS client pushes per second, printed on every scene change.
			if (VaAllow("NetSendModule")) ModuleManager.Register(new NetSendModule());
			// TEMPORARY: find what carries player voice on this build. Remove once identified.
			if (VaAllow("VoiceProbeModule")) ModuleManager.Register(new VoiceProbeModule());
			if (VaAllow("FewTagsModule")) ModuleManager.Register(new FewTagsModule());
			if (VaAllow("NameplateEspModule")) ModuleManager.Register(new NameplateEspModule());
			if (VaAllow("MovementModule")) ModuleManager.Register(new MovementModule());
			// PLAYER ROTATOR: tilt your own capsule, and unclamp mouse-look so the view follows you
			// past vertical. MovementModule turns yaw only, and says so in its own comment.
			if (VaAllow("PlayerRotatorModule")) ModuleManager.Register(new PlayerRotatorModule());
			if (VaAllow("BoxDropModule")) ModuleManager.Register(new BoxDropModule());
			if (VaAllow("PortalInfiniteModule")) ModuleManager.Register(new PortalInfiniteModule());
			if (VaAllow("LayoutProbeModule")) ModuleManager.Register(new LayoutProbeModule());
			if (VaAllow("SpeedModule")) ModuleManager.Register(new SpeedModule());
			if (VaAllow("VideoModule")) ModuleManager.Register(new VideoModule());
			if (VaAllow("OrbitModule")) ModuleManager.Register(new OrbitModule());
			if (VaAllow("ObjectOrbitModule")) ModuleManager.Register(new ObjectOrbitModule());
			// ELEVATOR: the same loose props, gathered into a platform that rises up the Y axis. Only
			// ever moves objects (never a player); whoever stands on it is carried by their own physics.
			if (VaAllow("ElevatorModule")) ModuleManager.Register(new ElevatorModule());
			// Wear another player's pose (bone rotations + gestures), local VRIK paused so it is what gets sent.
			if (VaAllow("MimicPoseModule")) ModuleManager.Register(new MimicPoseModule());
			// Relay another player's voice packets as your own outgoing voice (nothing moves).
			if (VaAllow("VoiceMimicModule")) ModuleManager.Register(new VoiceMimicModule());
			if (VaAllow("ForceGrabModule")) ModuleManager.Register(new ForceGrabModule());
			if (VaAllow("ForceJumpModule")) ModuleManager.Register(new ForceJumpModule());
			// GHOST: hold the local player's network serializer off (frozen for everyone else).
			if (VaAllow("GhostModule")) ModuleManager.Register(new GhostModule());
			if (VaAllow("ForcePickupModule")) ModuleManager.Register(new ForcePickupModule());
			if (VaAllow("PlayerGrabModule")) ModuleManager.Register(new PlayerGrabModule());
			if (VaAllow("MarkModule")) ModuleManager.Register(new MarkModule());
			if (VaAllow("MenuThemeModule")) ModuleManager.Register(new MenuThemeModule());
			// (MenuExclusiveModule deleted 2026-09-01: it hid VRChat's QuickMenu behind the mod's
			// TAB menu, and that menu is sealed � there was nothing left for it to do.)
			if (VaAllow("SoundboardModule")) ModuleManager.Register(new SoundboardModule());
			if (VaAllow("GravityModule")) ModuleManager.Register(new GravityModule());
			if (VaAllow("FavoritesModule")) ModuleManager.Register(new FavoritesModule());
			// SUPERSEDED by AvatarFavListModule. Two modules writing the same _avatars collection
			// raced each other, and this one injected while the collection was still EMPTY � before
			// VRChat had built its own lists � which is the most likely reason a forced section made
			// every list vanish.
			// ModuleManager.Register(new ArchiveFavListModule());
			// Worlds: an EXTRA list rather than a borrowed slot � the worlds sidebar renders
			// straight from _worlds, so a new entry there actually shows up.
			// WORLD AND USER FAVOURITES ARE THE CLIENT'S JOB NOW � disarmed 2026-08-26.
			//
			// The desktop client's FAVORIS page does all four kinds properly: real cards, filters,
			// thumbnails cached on disk, JOIN / GET VRCW / VRCX / website. Grafting a second, worse
			// version of that into VRChat's own menus earned nothing and cost a grid to keep
			// working against a menu that fights back. The MOD keeps AVATARS only, because wearing
			// one is something only the game can do.
			//
			// These three fed the removed pieces and were left polling the bridge for nobody:
			//   WorldFavoritesModule / UserFavoritesModule -> ids for the grafted grids
			//   KindFavoritesModule                        -> cards for the old TAB FAVORIS tab
			// ModuleManager.Register(new WorldFavoritesModule());
			// ModuleManager.Register(new UserFavoritesModule());
			// ModuleManager.Register(new KindFavoritesModule());
			// DEAD PATH, disarmed 2026-08-26. WorldFavListModule / UserFavListModule inject ids into
			// VRChat's FavoriteArea (ReplaceFavoritesIndexed). The data lands � the log said "filled
			// with 54 member(s)" � but the Voyager worlds/social pages rebuild their grid from a
			// server fetch on selection, so the screen still shows (0). Proven not to render. The
			// working replacement is ArchiveFavGridModule below, which builds our OWN grid (FavCat).
			// WorldFavoritesModule / UserFavoritesModule stay: they are the id SOURCE the grid reads.
			// ModuleManager.Register(new WorldFavListModule());
			// ModuleManager.Register(new UserFavListModule());
			// DISARMED 2026-09-01 with its Favorites/AvatarList switch. It was off by default and
			// never seen to render (the avatar sidebar is composed elsewhere); the AVATARS tab's
			// borrowed category (ArchiveHijackModule) is the working path. This line is now the
			// on/off switch � the module's Enabled reads true.
			// ModuleManager.Register(new AvatarFavListModule());
			// ARCHIVE FAVORITES for worlds + social. This is the WORKING path the note above points at:
			// our own grid, rather than pushing ids into a page that rebuilds itself from the server.
			// It had been commented out along with the dead injectors it replaces, which left the
			// feature with no implementation at all.
			// DISARMED 2026-08-28 (owner) : plus de sections WORLD ni USER dans ARCHIVE FAVORITES.
			// Le mod garde uniquement les FAVORIS AVATARS (via ArchiveHijackModule, plus haut) ;
			// worlds/users sont pris en charge par la page FAVORIS du client desktop.
			// RE-ARMED 2026-09-17 for AVATARS ONLY (worlds/users panes stay Enabled=false inside).
			// ArchiveHijackModule cannot borrow VRChat's category on 1903 -- the panel TYPE it targets
			// was reassigned by the obfuscator, so its list reads empty -- and re-deriving Panel/Category/
			// Section by shape would rot again next build. This grid touches no obfuscated type at all.
			if (VaAllow("ArchiveFavGridModule")) ModuleManager.Register(new ArchiveFavGridModule());
			if (VaAllow("ArchiveFavButtonModule")) ModuleManager.Register(new ArchiveFavButtonModule());
			// DISARMED 2026-08-25 � hard crash of the game on opening the avatar menu, with no
			// [ArchiveCat] line in the log at all, i.e. the process died before the module could
			// report anything. That points at the native side: constructing the game's generic
			// Il2Cpp observables/fetchables by hand, or the Harmony patch on the obfuscated
			// selection handler. A crash on opening a menu is not a bug to iterate on live, so it
			// stays off until the cause is identified from the dump.
			// ModuleManager.Register(new ArchiveCategoryModule());
			// Takes over an existing category instead of inventing one � see the file header for
			// why that difference is the whole point.
			if (VaAllow("ArchiveHijackModule")) ModuleManager.Register(new ArchiveHijackModule());
			if (VaAllow("UdonManagerModule")) ModuleManager.Register(new UdonManagerModule());
			if (VaAllow("GlobalUdonModule")) ModuleManager.Register(new GlobalUdonModule());
			// The desktop client drives the mod. Its OWN module on purpose: hanging it off another
			// module's update would mean disabling that feature silently killed client control.
			if (VaAllow("ModControlModule")) ModuleManager.Register(new ModControlModule());
			// (EspModule, the screen-space box/skeleton/name overlay, was removed 2026-09-04: the
			// glows below and the 3D capsule are the ESP now, each with its own switch.)
			// CAPSULE BEFORE HIGHLIGHT, on purpose: OnSceneLoaded runs in registration order, and
			// the capsule module needs HighlightEspModule's effect still resolved to un-light its
			// capsules before that module forgets the effect for the new world.
			// 2D screen ESP (box / name / distance / skeleton): back on 2026-09-04, its own switch (ESP/Enabled).
			if (VaAllow("EspModule")) ModuleManager.Register(new EspModule());
			if (VaAllow("CapsuleEspModule")) ModuleManager.Register(new CapsuleEspModule());
			if (VaAllow("HighlightEspModule")) ModuleManager.Register(new HighlightEspModule());
			// SpawnSound and SignatureSound permanently disabled per user request
			// if (VaAllow("SpawnSoundModule")) ModuleManager.Register(new SpawnSoundModule());
			// if (VaAllow("SignatureSoundModule")) ModuleManager.Register(new SignatureSoundModule());
			if (VaAllow("RadarModule")) ModuleManager.Register(new RadarModule());
			if (VaAllow("InstancePanelsModule")) ModuleManager.Register(new InstancePanelsModule());
			if (VaAllow("VaTagsModule")) ModuleManager.Register(new VaTagsModule());
			if (VaAllow("VideoUrlModule")) ModuleManager.Register(new VideoUrlModule());
			if (VaAllow("WatchlistModule")) ModuleManager.Register(new WatchlistModule());
			// READ-ONLY block observer: measures what VRChat does to a remote player's local objects when
			// their avatar stops being drawn (and when it comes back). Samples in LateUpdate, writes to
			// nothing.
			if (VaAllow("BlockObserverModule")) ModuleManager.Register(new BlockObserverModule());
			// EVENT 33 TRACE (read-only): per-layer, per-frame timeline of what changes in a remote player's
			// representation after a moderation event, with the native callers of each change. Same switch
			// as the observer (BlockDebug/Enabled).
			if (VaAllow("Event33TraceModule")) ModuleManager.Register(new Event33TraceModule());
			// TRUEVIEW: keep a remote player's real avatar on screen when VRChat swaps in the
			// skeleton-less fallback. Replaces AntiBlockModule, which patched every bool(string)
			// method on ModerationManager — one blunt patch on a type whose shape changes per build.
			// TrueView patches nothing: it suppresses the fallback object and swaps in a clone of
			// the real avatar it cached earlier. See src/Modules/TrueView.cs.
			if (VaAllow("TrueViewModule")) ModuleManager.Register(new TrueViewModule());
			// (PushProbeModule / ProbPusherModule — the old "Probe Lifter" self-lift — were removed;
			// the rising ELEVATOR platform (ElevatorModule, above) replaces that feature.)
			if (VaAllow("PlayerStateProbeModule")) ModuleManager.Register(new PlayerStateProbeModule());
			if (VaAllow("UdonLogModule")) ModuleManager.Register(new UdonLogModule());
			if (VaAllow("NetworkLogModule")) ModuleManager.Register(new NetworkLogModule());
			// FULL PHOTON LOG (NetworkLog/FullPhotonLog, off by default): every inbound event, decoded,
			// into its own file. Its hook is only installed while the switch is on, so leaving it off
			// costs nothing — not even a detour on a method that fires hundreds of times a second.
			if (VaAllow("PhotonLogModule")) ModuleManager.Register(new PhotonLogModule());
			// [open-source build] The plaintext-cache decryptor (AssetBundlePatchModule) is not part of
			// this repository. It was a server-gated paid feature injected at runtime. Removed here.
			// if (VaAllow("AssetBundlePatchModule")) ModuleManager.Register(new AssetBundlePatchModule());
			// Immediate-mode menu: declared once, drawn per frame, no GameObjects.
			if (VaAllow("OverlayMenuModule")) ModuleManager.Register(new OverlayMenuModule());
			// The Archive tab grafted into VRChat's own QuickMenu. It stays: a tab you can reach
			// inside the headset is the one piece of UI the desktop client cannot replace.
			if (VaAllow("QuickMenuTabModule")) ModuleManager.Register(new QuickMenuTabModule());
			if (VaAllow("UserMenuModule")) ModuleManager.Register(new UserMenuModule());
			if (VaAllow("PlayerFreezeModule")) ModuleManager.Register(new PlayerFreezeModule());
			if (VaAllow("WingPlayersModule")) ModuleManager.Register(new WingPlayersModule());
			// The other half of the pair: players in the LEFT wing, the instance log in the RIGHT one.
			if (VaAllow("WingLogModule")) ModuleManager.Register(new WingLogModule());
			// DUMP MENU TREE. Registered because it MUST be: its Request() only raises a flag, and the
			// walk happens in OnUpdate — which the manager never calls for a module it does not hold.
			// Unregistered, the button reported success (the client toasts on send, not on completion)
			// and nothing whatsoever happened.
			if (VaAllow("UiTreeDumpModule")) ModuleManager.Register(new UiTreeDumpModule());
			if (VaAllow("RuntimeEditorModule")) ModuleManager.Register(new RuntimeEditorModule());
			if (VaAllow("PluginRetryModule")) ModuleManager.Register(new PluginRetryModule());

			// LIVE SCENE INSPECTION, off unless a port is configured. Started after the modules are
			// registered so the frame pump -- which is what actually answers the queries -- is already
			// running by the time anything can ask.
			try
			{
				int insp = ModConfig.InspectPort != null ? ModConfig.InspectPort.Value : 0;
				if (insp > 0) Core.InspectServer.Start(insp);
			}
			catch (System.Exception e) { Logger.LogWarning("[Inspect] " + e.Message); }
			if (VaAllow("MenuSkinModule")) ModuleManager.Register(new MenuSkinModule());
			if (VaAllow("ProfilerHudModule")) ModuleManager.Register(new ProfilerHudModule());
			if (VaAllow("BadAppleModule")) ModuleManager.Register(new BadAppleModule());
			if (VaAllow("ActiveFeaturesHudModule")) ModuleManager.Register(new ActiveFeaturesHudModule());
			if (VaAllow("MainMenuTabModule")) ModuleManager.Register(new MainMenuTabModule());
			if (VaAllow("UnityMcpBridgeModule")) ModuleManager.Register(new UnityMcpBridgeModule());
			if (VaAllow("ActionMenuModule")) ModuleManager.Register(new ActionMenuModule());
		}

		// Flushed before each step, because injecting a managed type into il2cpp is the one part of
		// startup that can fail with an access violation rather than an exception -- and a buffered
		// log line would die with the process. Whatever driver.log ends on is the step that faulted.
		internal static void Step(string s)
		{
			try
			{
				using var fs = new System.IO.FileStream(
					System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "driver.log"),
					System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
				var b = System.Text.Encoding.UTF8.GetBytes(
					System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + System.Environment.NewLine);
				fs.Write(b, 0, b.Length);
				fs.Flush(true);
			}
			catch { }
		}

		private static void InjectRuntimeDriver()
		{
			// Default: the pump rides on Unity's own per-frame calls (FramePump). The injected
			// MonoBehaviour below is what this used to be and what it should go back to once class
			// injection survives on this build -- VA_RUNNER=inject selects it for that test.
			if (System.Environment.GetEnvironmentVariable("VA_RUNNER") != "inject")
			{
				try { Core.FramePump.Install(); }
				catch (System.Exception e) { Logger.LogError($"FramePump: {e}"); }
				return;
			}
			try
			{
				Step("1 RegisterTypeInIl2Cpp<ModRunner>");
				ClassInjector.RegisterTypeInIl2Cpp<ModRunner>();
				Step("2 new GameObject");
				var host = new GameObject("VRChatArchiveMod");
				Step("3 hideFlags");
				host.hideFlags = HideFlags.HideAndDontSave;
				Step("4 DontDestroyOnLoad");
				Object.DontDestroyOnLoad(host);
				Step("5 AddComponent<ModRunner>");
				host.AddComponent<ModRunner>();
				Step("6 injection terminee");
				Logger.LogInfo("Runtime driver injected.");
			}
			catch (System.Exception e)
			{
				Logger.LogError($"Failed to inject runtime driver: {e}");
			}
		}
	}

	internal static class PluginInfo
	{
		public const string Guid = "org.vrchatarchive.mod";
		public const string Name = "VRCHAT ARCHIVE MOD";
		public const string Version = "3.9.311";
	}
}


