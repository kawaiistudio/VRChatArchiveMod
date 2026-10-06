using BepInEx.Configuration;

namespace VRChatArchiveMod.Core
{
	// Central configuration, backed by BepInEx's config file
	// (BepInEx/config/org.vrchatarchive.mod.cfg). Default caps mirror the
	// Munchen client's anti-crash limits — high enough that heavy-but-legit
	// avatars are untouched, low enough to stop genuine crasher avatars.
	public static class ModConfig
	{
		// --- Anti-Crash: master + per-vector toggles ---
		public static ConfigEntry<bool> AntiCrashEnabled;
		public static ConfigEntry<bool> BundleGuardEnabled;
		public static ConfigEntry<bool> BundleGuardKeepValidation;
		public static ConfigEntry<int> BundleGuardMaxMb;
		// [open-source build] DexCapture config entries removed with the paid Dex module.
		public static ConfigEntry<bool> ClampParticles;
		public static ConfigEntry<bool> ClampLights;
		public static ConfigEntry<bool> ClampAudioSources;
		public static ConfigEntry<bool> ClampCloth;
		public static ConfigEntry<bool> ClampPhysBones;
		public static ConfigEntry<bool> ClampContacts;
		// Per-system caps, not just a component count: one ParticleSystem with maxParticles in the
		// millions is a crasher the count clamp above never sees.
		public static ConfigEntry<bool> ClampParticleCounts;
		public static ConfigEntry<bool> ClampMeshes;          // polygon crashers (one mesh, or the total)
		public static ConfigEntry<bool> ClampMaterials;       // shader / material bombs
		public static ConfigEntry<bool> DisableAvatarCameras; // screen hijack / render crashers
		public static ConfigEntry<bool> ClampTrails;          // Trail + Line renderer spam
		public static ConfigEntry<bool> ClampConstraints;     // constraint chains that stall the animation thread
		// Last resort: an avatar that trips MANY categories at once is a crasher by design, not a
		// heavy avatar, and trimming it piece by piece still leaves the rest of it running.
		public static ConfigEntry<bool> HideAvatarOverBudget;
		public static ConfigEntry<bool> SelfHide;             // your own avatar not drawn on your screen (SelfHideModule)
		public static ConfigEntry<bool> StoreOwnership;       // client-side store ownership check always true (Core/EcoPatcher)
		public static ConfigEntry<bool> VrcPlusSpoof;         // the client's own VRC+ flag reads true, locally (Core/VRCPlusSpoof)
		public static ConfigEntry<bool> FastSync;             // VRChat's own fast serialisation rate for you (FastSyncModule)
		public static ConfigEntry<string> UdonNameSpoof;      // name this client's Udon scripts read for you (SpoofModule)
		public static ConfigEntry<bool> FloatSyncedOnly;      // FLOAT OBJECTS: only pickups whose position is networked
		public static ConfigEntry<bool> NsfwFilter;           // hide keyword-named renderers on other players' avatars (NsfwFilterModule)
		public static ConfigEntry<string> NsfwKeywords;
		// BLOCK OBSERVER: read-only [BLOCK-DEBUG] lifecycle log around a remote avatar hide/show (BlockObserverModule).
		public static ConfigEntry<bool> BlockDebugEnabled;
		public static ConfigEntry<string> BlockDebugWatchName;
		public static ConfigEntry<bool> BlockDebugTraceHooks;      // Event33TraceModule: observe-only Harmony hooks
		public static ConfigEntry<float> BlockDebugTraceSeconds;   // per-frame sampling window after an event 33
		public static ConfigEntry<bool> BlockDebugDecodeEvent33;   // guarded raw decode of the event-33 dictionary
		public static ConfigEntry<bool> BlockDebugHookVrcMethods;  // OPT-IN: prefixes on curated VRChat methods (folding-filtered)
		// Passive record of INBOUND Photon events. Receive-side only — the module never sends.
		public static ConfigEntry<bool> NetworkLogEnabled;
		public static ConfigEntry<bool> NetworkLogToFile;
		public static ConfigEntry<bool> PhotonLogEnabled;   // FULL PHOTON LOG: every inbound event, decoded, to its own file
		public static ConfigEntry<bool> EventsShowUdon;      // DISPLAY gate, not recording
		public static ConfigEntry<bool> EventsShowNetwork;
		public static ConfigEntry<bool> NetworkInterestingOnly;
		public static ConfigEntry<int> NetworkBulkPerSecond;
		// Anti-Udon: client-side self-defence against world scripts behaving like crashers.
		public static ConfigEntry<bool> UdonBlockCrashers;
		public static ConfigEntry<bool> UdonBlockAll;
		public static ConfigEntry<int> UdonCrasherPerSecond;
		public static ConfigEntry<int> UdonFloodPerSecond;
		public static ConfigEntry<string> UdonBlockNames;   // always-block list, by event name
		public static ConfigEntry<bool> UdonLogEnabled;
		public static ConfigEntry<bool> UdonLogFrameEvents;
		public static ConfigEntry<bool> GlobalUdonInteract;
		// Photon Guard: drop INBOUND Photon events by code, and mute an actor that floods one code.
		// Receive-side only — a dropped event never reaches VRChat, and nothing is ever sent back.
		public static ConfigEntry<bool> PhotonGuardEnabled;
		public static ConfigEntry<bool> VoiceProbeEnabled;   // TEMPORARY voice-carrier probe
		public static ConfigEntry<int> VoiceMimicCode;       // Photon event code that carries voice (1 on this build)
		public static ConfigEntry<bool> VoiceMimicMuteSelf;  // drop our own outbound voice while relaying
		public static ConfigEntry<string> PhotonGuardBlockCodes;
		public static ConfigEntry<int> PhotonGuardRatePerSender;
		public static ConfigEntry<int> PhotonGuardSuspendSeconds;
		public static ConfigEntry<bool> PhotonGuardLogBlocked;
		public static ConfigEntry<bool> QMTabEnabled;   // native VRChat QuickMenu tab
		public static ConfigEntry<bool> UserMenuEnabled; // MOD FEATURES card on VRChat's per-user menu
		public static ConfigEntry<bool> DevToolsEnabled; // Enable and theme VRChat's built-in DevTools on selected user menu
		public static ConfigEntry<bool> WingPlayersEnabled; // instance roster inside VRChat's left wing
		public static ConfigEntry<bool> WingLogEnabled;    // instance log inside VRChat's right wing
		public static ConfigEntry<int> InspectPort;            // live scene-inspection server (0 = off)
		public static ConfigEntry<bool> RetryFailedPlugins;    // re-Load third-party plugins that died before our il2cpp repairs
		public static ConfigEntry<bool> ArchiveFavAvatarGrid;  // ARCHIVE FAVORITES row + grid in the avatars menu
		public static ConfigEntry<bool> RuntimeEditorEnabled;  // RuntimeUnityEditor, hosted by the mod
		public static ConfigEntry<string> RuntimeEditorKey;
		public static ConfigEntry<bool> MenuSkinEnabled;
		public static ConfigEntry<bool> MenuSkinClearVeil;
		public static ConfigEntry<bool> MenuThemeEnabled;
		public static ConfigEntry<string> QmTextColor;       // hex, VRChat menu text colour
		public static ConfigEntry<string> QmGradientStart;   // hex, card sweep top
		public static ConfigEntry<string> QmGradientEnd;     // hex, card sweep bottom
		public static ConfigEntry<bool> QmBackgroundSolid;   // solid colour instead of the wallpaper image
		public static ConfigEntry<string> QmBackgroundColor; // hex, used when Solid is on
		public static ConfigEntry<float> QmWallpaperDim;     // 0..1, darken the wallpaper
		public static ConfigEntry<string> QmCustomBackgroundImage; // custom wallpaper path, blank = Archive default
		// (ArchiveFavListEnabled removed — the Archive category is always on. See Init.)
		// (WorldFavListEnabled / UserFavListEnabled / AvatarFavListEnabled removed 2026-09-01 — the
		// modules they gated are unregistered, so the switches did nothing. See Init.)
		// The three below WERE ConfigEntries. They are constants now because no registered module
		// reads them, and a knob in the client for an experiment that cannot run is worse than
		// none; the values and their reasons are kept for the day the injectors are re-armed.
		// ForceNewSection: add our own sidebar row instead of borrowing an empty one. It arms a
		// watchdog because a previous attempt made every world list vanish — a diagnostic, never
		// the normal mode.
		public const bool ForceNewSection = false;
		// WHICH list to take over, by POSITION (0-based, sidebar order), not by displayed name:
		// VRChat only stores a name for lists you renamed yourself — the rest are worlds2/worlds3/
		// worlds4 internally and the menu derives the label. Two attempts at detecting an "empty"
		// one both stole a full one, because a list's contents are not in _favorites at the moment
		// we look — VRChat shows 25/100 from somewhere else entirely.
		public const int WorldListSlot = 3;    // 0 = Favorite Worlds 1 … 3 = Favorite Worlds 4
		public const int SocialListSlot = 2;   // 0 = Group 1, 1 = Group 2, 2 = Favorite Friends 3
		// (ArchiveAutoClean removed 2026-08-28 — always on. See Init.)
		public static ConfigEntry<string> ArchiveCategoryName;   // which shelf we borrow   // synthetic category in VRChat's own avatar menu
		// (MenuExclusive removed 2026-09-01 — it hid VRChat's QuickMenu behind a menu that is sealed.)
		public static ConfigEntry<bool> ModControlEnabled;   // let the desktop client drive the mod's settings
		// Soundboard: a member triggers a clip, every other mod plays it locally.
		public static ConfigEntry<bool> SoundboardEnabled;
		// Gravity: LOCAL only. There is no API to change anybody else's.
		public static ConfigEntry<bool> GravityPlayerOff;
		public static ConfigEntry<bool> GravityWorldOff;
		public static ConfigEntry<float> SoundboardVolume;
		public static ConfigEntry<float> SoundboardPollSeconds;
		public static ConfigEntry<bool> DebugMode;      // verbose diagnostics build behaviour
		public static ConfigEntry<bool> UdonLogOverlay;
		public static ConfigEntry<int> UdonLogOverlayLines;

		// --- Anti-Crash: crasher-territory thresholds (Munchen defaults) ---
		public static ConfigEntry<int> MaxParticleSystems;  // count of ParticleSystem components
		public static ConfigEntry<int> MaxLights;           // MaxLightSources = 8
		public static ConfigEntry<int> MaxAudioSources;     // MaxAudioSources = 150
		public static ConfigEntry<int> MaxCloth;            // MaxCloth = 75
		public static ConfigEntry<int> MaxPhysBones;        // VRCPhysBone count cap
		public static ConfigEntry<int> MaxContacts;         // VRCContact sender/receiver count cap
		public static ConfigEntry<int> MaxParticlesPerSystem;  // maxParticles cap on ONE system
		public static ConfigEntry<int> MaxEmissionRate;        // rateOverTime cap on ONE system
		public static ConfigEntry<int> MaxTotalParticles;      // particle budget for the whole avatar
		public static ConfigEntry<int> MaxTrianglesPerMesh;    // one mesh past this = renderer off
		public static ConfigEntry<int> MaxTotalTriangles;      // triangle budget for the whole avatar
		public static ConfigEntry<int> MaxTrianglesPerVertex;  // triangles per vertex: an index buffer written to amplify
		public static ConfigEntry<int> MaxMaterialSlots;       // material-slot budget for the whole avatar
		public static ConfigEntry<int> MaxTrailRenderers;      // Trail + Line renderers combined
		public static ConfigEntry<int> MaxConstraints;         // Unity + VRC constraints combined
		public static ConfigEntry<int> HideAvatarTrips;        // categories tripped before the avatar is hidden

		// --- Scan cadence ---
		public static ConfigEntry<int> ScanIntervalFrames;  // poll every N frames

		// --- AssetBundle archiving ---
		public static ConfigEntry<string> ArchiveFolder;     // empty => <BepInEx>/VRChatArchiveMod/Bundles

		// --- FewTags (community nameplate tags, database by Fewdys) ---
		public static ConfigEntry<bool> FewTagsEnabled;
		public static ConfigEntry<string> FewTagsDbUrl;
		public static ConfigEntry<int> FewTagsUpdateMinutes;
		public static ConfigEntry<int> FewTagsMaxTagsPerUser;
		public static ConfigEntry<bool> FewTagsShowHeader;
		public static ConfigEntry<bool> FewTagsShowBigPlates;
		public static ConfigEntry<bool> FewTagsFilterSlurs;   // hide abusive third-party tags locally
		public static ConfigEntry<float> FewTagsBaseY;      // first plate height above the nameplate
		public static ConfigEntry<float> FewTagsSpacing;    // vertical gap between stacked plates (legacy layout)
		public static ConfigEntry<float> FewTagsSpacingExpanded;   // ...and on the Fragments layout
		public static ConfigEntry<float> FewTagsBaseYExpanded;     // first-plate height, Fragments layout

		// --- VA Tags (VRChatArchive player tagging — PLAYERS tab, site API) ---
		public static ConfigEntry<bool> VaTagsEnabled;
		public static ConfigEntry<string> VaTagsApiBase;
		public static ConfigEntry<int> VaTagsUpdateMinutes;
		public static ConfigEntry<bool> VaTagsShowPlates;
		public static ConfigEntry<float> VaTagsPlateY;

		// --- Freeze Badge (snowflake slot under avatar icon on nameplate) ---
		public static ConfigEntry<bool> FreezeBadgeEnabled;
		public static ConfigEntry<float> FreezeBadgeX;
		public static ConfigEntry<float> FreezeBadgeY;
		public static ConfigEntry<float> FreezeBadgeSize;

		// --- Watchlist (special users → super-RGB ESP box + join notification) ---
		public static ConfigEntry<bool> WatchlistEnabled;
		public static ConfigEntry<string> WatchlistUserIds;   // comma-separated usr_ ids
		// Member-join banner: clean pink→violet pill by default; RGB = animated rainbow (for testing).
		public static ConfigEntry<bool> MemberNotifyRgb;
		// Players whose capsule cycles the spectrum instead of showing a trust colour.
		public static ConfigEntry<string> RainbowUserIds;
		public static ConfigEntry<float> RainbowSpeed;
		public static ConfigEntry<bool> LegendaryRainbow;

		// --- Instance panels (player list + join/leave log) ---
		// HUD master: one switch over everything drawn on screen (Core/HudMaster).
		public static ConfigEntry<bool> HudEnabled;
		public static ConfigEntry<string> HudSaved;
		public static ConfigEntry<bool> InstancePanelsEnabled;

		// --- Join notifier (transient on-screen toast on join/leave) ---
		public static ConfigEntry<bool> JoinNotifierEnabled;
		public static ConfigEntry<bool> JoinNotifierShowLeave;

		// --- Radar (top-down player map, same position data as ESP) ---
		public static ConfigEntry<bool> RadarEnabled;
		public static ConfigEntry<bool> RosterBottom;
		public static ConfigEntry<bool> ActiveFeaturesHudEnabled;
		public static ConfigEntry<int> ActiveFeaturesFontSize;
		public static ConfigEntry<int> EspPlayerColorIndex; // 0 = Trust Rank, 1+ = Custom palette index
		public static ConfigEntry<int> EspItemColorIndex;   // index into ColorPalette.EspItemColors
		public static ConfigEntry<int> EspPortalColorIndex; // index into ColorPalette.EspPortalColors

		public static ConfigEntry<int> HudMovementColorIndex; // index into ColorPalette.HudColors
		public static ConfigEntry<int> HudEspColorIndex;
		public static ConfigEntry<int> HudUtilityColorIndex;
		public static ConfigEntry<int> HudSecurityColorIndex;
		public static ConfigEntry<int> HudThemeIndex;
		public static ConfigEntry<float> RadarRange;   // metres shown edge-to-centre
		public static ConfigEntry<float> RadarSize;    // on-screen diameter (px)
		public static ConfigEntry<bool> RadarNames;    // player name beside each blip
		// Optional top-down camera that draws the actual world under the radar blips.
		public static ConfigEntry<bool> RadarMap;
		public static ConfigEntry<int> RadarMapResolution;
		public static ConfigEntry<int> RadarMapEveryFrames;
		public static ConfigEntry<float> RadarMapHeight;
		public static ConfigEntry<float> RadarMapOpacity;
		public static ConfigEntry<bool> RosterPositions;   // live X/Y/Z per player in the HUD roster
		// --- Orbit / Sit (self-movement relative to another player) ---
		public static ConfigEntry<float> OrbitRadius;
		public static ConfigEntry<float> OrbitSpeed;
		public static ConfigEntry<float> OrbitHeight;
		public static ConfigEntry<float> SitHeight;
		// Object orbit: the world's loose props spun around a player. Local-only gag.
		public static ConfigEntry<int> ObjOrbitCount;
		public static ConfigEntry<float> ObjOrbitRange;
		public static ConfigEntry<float> ObjOrbitRadius;
		public static ConfigEntry<float> ObjOrbitSpeed;
		public static ConfigEntry<float> ObjOrbitHeight;
		public static ConfigEntry<float> ObjOrbitMaxSize;
		public static ConfigEntry<bool> ObjOrbitSpin;
		public static ConfigEntry<bool> ObjOrbitSynced;
		// Elevator: the same loose props gathered into a platform that rises up the Y axis.
		public static ConfigEntry<int> ElevatorCount;
		public static ConfigEntry<float> ElevatorRange;
		public static ConfigEntry<float> ElevatorClimbSpeed;
		public static ConfigEntry<float> ElevatorMaxHeight;
		public static ConfigEntry<float> ElevatorPlatformRadius;
		public static ConfigEntry<float> ElevatorStartDepth;
		public static ConfigEntry<bool> ElevatorAutoCorrect;
		public static ConfigEntry<float> ElevatorLead;
		public static ConfigEntry<float> ElevatorSettleSeconds;
		public static ConfigEntry<bool> ElevatorSynced;
		public static ConfigEntry<bool> BadAppleMusic;          // play the embedded soundtrack with the object show
		public static ConfigEntry<float> BadAppleMusicVolume;   // and how loud, locally

		// --- Full game log capture (passive Unity log sink → file; never touches Photon) ---
		public static ConfigEntry<bool> AllowIl2CppDelegates;

		// --- Bad Apple chatbox player (OSC) ---
		public static ConfigEntry<bool> BadAppleLoop;
		public static ConfigEntry<int> BadAppleIntervalMs;
		public static ConfigEntry<string> BadAppleOscHost;
		public static ConfigEntry<int> BadAppleOscPort;
		public static ConfigEntry<string> BadAppleCharset;

		// --- Free cursor ---
		public static ConfigEntry<bool> AltFreeCursor;      // HOLD Alt = free the cursor + movement

		// --- Movement (local, self-only) ---
		public static ConfigEntry<bool> FlyEnabled;
		// Locomotion, through VRChat's own per-player setters. Local only.
		public static ConfigEntry<bool> SpeedEnabled;
		// One switch per kind of movement. A single master meant turning on "custom speed" to change
		// walking also took over running and jumping, which is not what anybody wants.
		public static ConfigEntry<bool> WalkMod;
		public static ConfigEntry<bool> RunMod;
		public static ConfigEntry<bool> JumpMod;
		public static ConfigEntry<float> WalkSpeed;
		public static ConfigEntry<float> RunSpeed;
		public static ConfigEntry<float> StrafeSpeed;
		public static ConfigEntry<float> JumpImpulse;
		public static ConfigEntry<bool> NoclipEnabled;     // pass through walls (collision off)
		public static ConfigEntry<bool> ClickTpEnabled;    // right-hold + left-click = teleport to aim
		public static ConfigEntry<float> ClickTpMaxDistance;
		public static ConfigEntry<float> FlySpeed;
		public static ConfigEntry<float> FlyBoostSpeed;
		public static ConfigEntry<float> FlyRotateSpeed;
		public static ConfigEntry<bool> ArrowRotateEnabled;   // arrow-key rotation while flying (deg/sec)
		public static ConfigEntry<bool> GhostSavePosition;

		// --- Player rotator: tilt your own capsule, and unclamp the view so it can follow ---
		// WHO BLOCKED ME. A switch exists at all because this module asks VRChat's API through the
		// il2cpp delegate bridge and then walks a native collection — the riskiest thing the mod does
		// — and until now there was no way to turn it off short of deleting the mod.
		public static ConfigEntry<bool> BlockedByProbe;
		// TRUEVIEW: keep a remote player's real avatar on screen when VRChat swaps in the fallback.
		public static ConfigEntry<bool> TrueViewEnabled;
		public static ConfigEntry<bool> TrueViewAvatar;        // cache + restore the real avatar
		public static ConfigEntry<bool> TrueViewNameplates;    // keep the plate visible and at head height
		public static ConfigEntry<bool> TrueViewSelectRegion;  // keep the laser/click hitbox alive
		public static ConfigEntry<bool> TrueViewUnmute;        // undo the local USpeak mute
		public static ConfigEntry<bool> TrueViewAnimators;     // AlwaysAnimate so the pose keeps moving
		public static ConfigEntry<bool> TrueViewRig;           // pose the cached copy from the player's IK

		public static ConfigEntry<bool> RotatorEnabled;
		public static ConfigEntry<bool> RotatorFreeLook;      // widen the neck clamp so mouse pitch goes past vertical
		public static ConfigEntry<bool> RotatorHoldGravity;   // hold YOUR gravity at 0 while actually leaning
		public static ConfigEntry<float> RotatorSpeed;        // degrees per second for the tilt keys
		public static ConfigEntry<float> RotatorNeckLimit;    // how far the widened neck clamp reaches, in degrees

		// --- Infinite Portal (dropped portals stop expiring; cooldown between drops removed) ---
		public static ConfigEntry<bool> PortalInfiniteEnabled;

		// --- Box Drop (reverse GoGoLoco: offset the OUTBOUND position, the local player untouched) ---
		public static ConfigEntry<bool> BoxDropEnabled;
		public static ConfigEntry<float> BoxDropX;
		public static ConfigEntry<float> BoxDropY;
		public static ConfigEntry<float> BoxDropZ;

		// --- Network diagnostics ---
		public static ConfigEntry<bool> DumpOutbound;

		// --- Spawn stinger (plays once when you finish loading into an instance) ---
		public static ConfigEntry<bool> SpawnSoundEnabled;
		public static ConfigEntry<float> SpawnSoundVolume;

		// --- Signature sounds (a clip that belongs to a person, on arrival) ---
		public static ConfigEntry<bool> SignatureSoundEnabled;
		public static ConfigEntry<float> SignatureSoundVolume;

		// --- Custom nameplates (2018-style frame drawn over the world) ---

		// --- ESP (visualization only) — every switch is independent of the others ---
		// (The screen-space box / skeleton / name / distance overlay is gone: the glows and the
		// 3D capsule are the ESP now, and each one is its own toggle. Fields are further down.)
		public static ConfigEntry<float> ForceGrabRange;
		public static ConfigEntry<float> ForceJumpForce;
		public static ConfigEntry<bool> ForceGrabAnyObject;
		public static ConfigEntry<float> ForcePickupRange;
		public static ConfigEntry<bool> ForcePickupAllowTheft;
		// PLAYER GRAB (mod users grab / throw each other) + MARK (aim-placed anchor for object TP / shapes / art).
		public static ConfigEntry<float> PlayerGrabFollow, PlayerGrabThrowForce, PlayerGrabInertia, PlayerGrabReach, PlayerGrabAimRange;
		public static ConfigEntry<bool> PlayerGrabAllowEscape;
		public static ConfigEntry<float> MarkArtSize;
		public static ConfigEntry<bool> EspEnabled;        // 2D screen box ESP (box / name / distance / skeleton) — its own switch
		public static ConfigEntry<bool> EspBox;
		public static ConfigEntry<bool> EspSkeleton;       // bone-to-bone wireframe instead of a flat box
		public static ConfigEntry<bool> EspName;
		public static ConfigEntry<bool> EspDistance;
		public static ConfigEntry<bool> EspCapsule;        // glowing 3D capsule around each player
		public static ConfigEntry<bool> EspHighlight;      // outline glow on the avatar mesh (HighlightsFX)
		public static ConfigEntry<bool> EspPortals;        // glow on world portals
		public static ConfigEntry<bool> EspItems;          // glow on grabbable pickups
		public static ConfigEntry<bool> EspThroughWalls;   // capsules drawn over the world geometry
		public static ConfigEntry<float> EspMaxDistance;   // capsule range; 0 = unlimited
		public static ConfigEntry<bool> EspHideFromCamera; // ESP kept out of your own camera / stream / mirrors
		public static ConfigEntry<bool> EspShowSelf;       // show ESP on yourself when alone or in 3rd person
		public static ConfigEntry<bool> NameplateEsp;      // see player nameplates through walls (ESP mode)
		public static ConfigFile ConfigFile { get; private set; }

		public static void Init(ConfigFile cfg)
		{
			ConfigFile = cfg;
			AntiCrashEnabled = cfg.Bind("AntiCrash", "Enabled", true,
				"Master switch for the avatar anti-crash protection. OFF puts every clamped avatar back the way it was (components re-enabled, particle limits restored, hidden avatars shown) and also turns BundleGuard off. ON rescans everyone.");

			BundleGuardEnabled = cfg.Bind("AntiCrash", "BundleGuard", true,
				"Asset-bundle protection: refuse to load a bundle whose SOURCE is not VRChat's own content delivery, and never serve a local cache file that is empty or absurdly large. This guards the download side; the clamps above guard what a loaded avatar then does. Needs the master switch: with Anti-Crash OFF this guard is off too.");
			BundleGuardKeepValidation = cfg.Bind("AntiCrash", "BundleKeepValidation", true,
				"Keep Unity's CRC integrity check on every bundle. VRChat passes a CRC so a truncated or altered download is rejected instead of parsed; leaving this on is what makes a genuinely CORRUPTED bundle fail safely rather than crash the loader.");
			BundleGuardMaxMb = cfg.Bind("AntiCrash", "BundleMaxMb", 600,
				"Refuse to serve a locally cached bundle larger than this many megabytes. A real avatar is a few dozen MB; a file far past that is a decompression bomb or a broken cache entry. 0 disables the size check.");

			DexCaptureEnabled = cfg.Bind("DexCapture", "Enabled", true,
				"Detect DexProtect-scrambled avatars in-game and, once VRChat has unlocked them at runtime, capture the DECODED mesh (SkinnedMeshRenderer.BakeMesh), the animator's live parameter values, and the bone unlock poses. Paired with the locked .vrca the archiver already keeps, an offline tool reconstructs a clean bundle. Writes to BepInEx/VRChatArchiveMod/dex_captures/.");
			DexCaptureLocalOnly = cfg.Bind("DexCapture", "LocalOnly", false,
				"Only capture your own avatar. OFF also captures other players' Dex avatars once they unlock in your client.");
			DexAutoRepairLocal = cfg.Bind("DexCapture", "AutoRepairLocal", true,
				"When YOUR OWN avatar is captured (you are wearing it, so your key is live), unlock it on the spot: the mod runs the patcher (data/tools/DexPatch/dex_unlock.exe) against the locked bundle and writes the clean unlocked bundle to data/DEX_PATCHED/<id>.vrca — no UNLOCK DEX click. Only YOUR worn avatar; other players' Dex avatars are still captured but patched manually. If the patcher or the locked bundle is not found, the capture is simply kept for the manual button.");
			DexProbe = cfg.Bind("DexCapture", "Probe", false,
				"Publish a live unlock score for YOUR OWN avatar to dex_probe.json, so an outside tool can push candidate parameter values in over VRChat's own OSC input and see whether they helped. DexProtect's key is not stored in the bundle, but the .key tool merely sends parameter values, so they can be searched for with the game itself as the judge - it skins the avatar for real and cannot be fooled the way an offline measure can. Costs a few bakes a second, so leave it off unless you are searching.");
			DexSearch = cfg.Bind("DexCapture", "KeySearch", false,
				"Search for a DexProtect avatar's unlock values by changing YOUR OWN avatar's parameters one at a time and keeping whatever makes more of the mesh come back to human size. DexProtect's key is not in the bundle, but it is only a set of parameter values, so the game itself can be asked. EXPECT HEAVY STUTTER while it runs, and note that synced parameters replicate - other players will see your avatar change, so use a private instance.");
			DexSearchFast = cfg.Bind("DexCapture", "KeySearchFast", true,
				"Score each guess on the biggest mesh only instead of every drawn mesh - roughly seven times less work per guess. The full check still runs whenever a guess looks like it helped.");
			DexProbeInterval = cfg.Bind("DexCapture", "ProbeInterval", 0.25f,
				"Seconds between unlock-score samples while Probe is on. Lower reacts faster to an injected value but bakes more often.");

			// Every clamp below is reversible: the component is switched off (never destroyed) and the
			// original state is journaled, so turning the toggle or the master off puts it back.
			ClampParticles = cfg.Bind("AntiCrash", "ClampParticles", true,
				"Neutralize particle-bomb avatars: particle systems past MaxParticleSystems are stopped and their emission switched off (nothing is destroyed). They come back when this toggle or the master goes off.");
			ClampLights = cfg.Bind("AntiCrash", "ClampLights", true,
				"Switch off real-time Lights past MaxLights (light-spam crashers). Nothing is destroyed; they come back when this toggle or the master goes off.");
			ClampAudioSources = cfg.Bind("AntiCrash", "ClampAudioSources", true,
				"Switch off AudioSources past MaxAudioSources (audio-spam crashers). Nothing is destroyed; they come back when this toggle or the master goes off.");
			ClampCloth = cfg.Bind("AntiCrash", "ClampCloth", true,
				"Switch off Cloth components past MaxCloth. Nothing is destroyed; they come back when this toggle or the master goes off.");
			ClampPhysBones = cfg.Bind("AntiCrash", "ClampPhysBones", true,
				"Switch off VRCPhysBone components past MaxPhysBones (CPU-spike crashers). Nothing is destroyed; they come back when this toggle or the master goes off.");
			ClampContacts = cfg.Bind("AntiCrash", "ClampContacts", true,
				"Switch off VRCContact senders/receivers past MaxContacts (contact-flood crashers). Nothing is destroyed; they come back when this toggle or the master goes off.");
			// The count clamps above miss the other half of a particle bomb: a handful of systems,
			// each told to emit a million particles. These clamp what each system is ALLOWED to do.
			ClampParticleCounts = cfg.Bind("AntiCrash", "ClampParticleCounts", true,
				"Cap maxParticles and emission rate on every particle system, and stop systems past the total particle budget. The original values are remembered and restored when this toggle or the master goes off.");
			ClampMeshes = cfg.Bind("AntiCrash", "ClampMeshes", true,
				"Disable renderers whose mesh exceeds the triangle cap, and renderers past the total triangle budget (polygon crashers). They are re-enabled when this toggle or the master goes off.");
			ClampMaterials = cfg.Bind("AntiCrash", "ClampMaterials", true,
				"Shader / material bombs, three rules: a renderer with MORE material slots than its mesh has sub-meshes is cut back to the sub-mesh count (Unity redraws the mesh once per extra slot - the 'Cloner' geometry-shader crasher is a 26-vertex plane with 29 slots); a tiny mesh (64 vertices or fewer, a quad or a plane) drawn with a shader outside the known families gets a plain grey fallback material instead (the shape of every shader crasher); and renderers past the material-slot budget are disabled. Everything is put back when this toggle or the master goes off.");
			DisableAvatarCameras = cfg.Bind("AntiCrash", "DisableAvatarCameras", true,
				"Disable Camera and Projector components on avatars (screen hijack / render crashers). They are re-enabled when this toggle or the master goes off.");
			ClampTrails = cfg.Bind("AntiCrash", "ClampTrails", true,
				"Switch off Trail and Line renderers past the trail budget (trail-spam crashers that fill the screen with overdraw). They come back when this toggle or the master goes off.");
			ClampConstraints = cfg.Bind("AntiCrash", "ClampConstraints", true,
				"Switch off constraints past the constraint budget (constraint chains that stall the animation thread every frame). They come back when this toggle or the master goes off.");
			// OFF by default: hiding is a bigger hammer than trimming, and a heavy-but-legit avatar
			// that trips two categories should still be seen. Only a many-category crasher is hidden.
			NsfwFilter = cfg.Bind("AntiCrash", "NsfwFilter", false,
				"NSFW FILTER: on OTHER players' avatars, every renderer whose object (or a parent) is named after one of the NsfwKeywords is switched off on your screen. Nothing is destroyed or sent; OFF (or the master OFF) turns them back on. Needs the anti-crash master ON.");
			NsfwKeywords = cfg.Bind("AntiCrash", "NsfwKeywords", "nsfw, penis, dick, cock, vagina, pussy, vulva, genital, anus, nipple, areola, dph, sps, orifice, cum, condom, dildo, buttplug, plug_",
				"NSFW FILTER: comma-separated words matched (case-insensitively) against object names on other avatars. Add or remove words freely; each must be 3+ characters.");
			SelfHide = cfg.Bind("AntiCrash", "SelfHide", false,
				"SELF HIDE: your own avatar is not drawn on YOUR screen (every renderer under it switched off - no mesh, no mirror reflection, no first-person hands). IK, camera, animator and what everyone else sees are untouched. Follows avatar changes and is re-asserted twice a second; OFF puts everything back.");
			StoreOwnership = cfg.Bind("Spoof", "StoreOwnership", true,
				"STORE OWNERSHIP: the client's own \"does this player have this product\" check answers yes, on THIS machine. Meant for walking your own published worlds' paid gates without buying your own product back to test one. Client-side only - anything VRChat validates on its servers is unaffected, so this makes the UI behave as owned, it does not obtain anything. Read live, so switching it off restores the game's answer at once.");
			VrcPlusSpoof = cfg.Bind("Spoof", "VRCPlus", true,
				"VRC+ STATUS: the client's own subscriber flag reads true on THIS machine, so the features the UI gates behind it open up. Local only - it postfixes the getter of the reactive property the menu binds to, and never APIUser, whose fields carry Save()/Put() and could travel back to VRChat under your account. Anything VRChat checks server-side is unaffected.");
			FastSync = cfg.Bind("Network", "FastSync", false,
				"FAST SYNC: asks VRChat to serialise YOU at its fast rate (FlatBufferNetworkSerializer.RequireFastRate). Your position, rotation and pose reach the other clients more often, so you look smoother to them and what you carry tracks your hands more closely. This is the GAME'S own flag on the GAME'S own serialiser, so its throttling and batching still apply - it is not a hand-rolled increase of the Photon send rate. It costs outbound bandwidth, which is why the game does not do it for everyone all the time. Re-applied on every world change, because joining rebuilds the serialiser at the default rate.");
			UdonNameSpoof = cfg.Bind("Spoof", "UdonName", "",
				"CUSTOM USERNAME: the name the world's Udon scripts read for you ON THIS CLIENT. Empty = off. Written to VRCPlayerApi.displayName and verified by reading it back; re-applied on every world change. Your nameplate and VRChat's own player list keep your real name (they come from the API, not from Udon), but a world that copies the name it read into a synced variable - a leaderboard, a name sign - sends this one to everyone. Worlds that key on your real name (allowlists, saved progress) stop recognising you while it is set. Refuses to arm if the il2cpp field-offset repair did not verify.");
			FloatSyncedOnly = cfg.Bind("AntiCrash", "FloatSyncedOnly", true,
				"FLOAT OBJECTS: only take pickups whose position is actually networked (they carry a VRCObjectSync), which are the ones that float for EVERYONE. A plain VRC_Pickup floats on your screen alone, and in a prop-heavy world those are most of them - skipping them is what keeps the sweep from stalling the game. Switch off to float every pickup, including the ones only you will see.");
			HideAvatarOverBudget = cfg.Bind("AntiCrash", "HideAvatarOverBudget", false,
				"When an avatar trips more than HideAvatarTrips categories at once, hide the whole avatar instead of trimming it. The avatar is shown again when this toggle or the master goes off.");

			MaxParticleSystems = cfg.Bind("AntiCrash.Thresholds", "MaxParticleSystems", 256,
				"Maximum ParticleSystem components allowed on one avatar.");
			MaxLights = cfg.Bind("AntiCrash.Thresholds", "MaxLights", 8,
				"Maximum real-time Light components allowed on one avatar (Munchen default: 8).");
			MaxAudioSources = cfg.Bind("AntiCrash.Thresholds", "MaxAudioSources", 150,
				"Maximum AudioSource components allowed on one avatar (Munchen default: 150).");
			MaxCloth = cfg.Bind("AntiCrash.Thresholds", "MaxCloth", 75,
				"Maximum Cloth components allowed on one avatar (Munchen default: 75).");
			MaxPhysBones = cfg.Bind("AntiCrash.Thresholds", "MaxPhysBones", 256,
				"Maximum VRCPhysBone components allowed on one avatar.");
			MaxContacts = cfg.Bind("AntiCrash.Thresholds", "MaxContacts", 256,
				"Maximum VRCContact sender+receiver components allowed on one avatar.");
			MaxParticlesPerSystem = cfg.Bind("AntiCrash.Thresholds", "MaxParticlesPerSystem", 5000,
				"Maximum maxParticles allowed on ONE ParticleSystem; a system asking for more is clamped down to this value.");
			MaxEmissionRate = cfg.Bind("AntiCrash.Thresholds", "MaxEmissionRate", 1000,
				"Maximum emission rate (particles per second) allowed on ONE ParticleSystem; higher rates are clamped down to this value.");
			MaxTotalParticles = cfg.Bind("AntiCrash.Thresholds", "MaxTotalParticles", 60000,
				"Total particle budget across every ParticleSystem on one avatar; systems past the budget are disabled.");
			MaxTrianglesPerMesh = cfg.Bind("AntiCrash.Thresholds", "MaxTrianglesPerMesh", 250000,
				"Maximum triangles in ONE mesh; a renderer whose mesh exceeds this is disabled.");
			MaxTotalTriangles = cfg.Bind("AntiCrash.Thresholds", "MaxTotalTriangles", 2500000,
				"Total triangle budget across every renderer on one avatar; renderers past the budget are disabled.");
			MaxTrianglesPerVertex = cfg.Bind("AntiCrash.Thresholds", "MaxTrianglesPerVertex", 32,
				"Triangles per VERTEX a mesh may have before its renderer is disabled. Indices point at vertices, so a real "
				+ "mesh sits near 2:1 and even a bad triangle fan stays in single digits; a measured crasher drew 10142 "
				+ "triangles from 26 vertices (390:1) — the same points redrawn thousands of times on one plane, which is "
				+ "pure overdraw and slips under every triangle and material budget. Only applies past 5000 triangles. "
				+ "0 disables the check.");
			MaxMaterialSlots = cfg.Bind("AntiCrash.Thresholds", "MaxMaterialSlots", 120,
				"Total material-slot budget across every renderer on one avatar; renderers past the budget are disabled.");
			MaxTrailRenderers = cfg.Bind("AntiCrash.Thresholds", "MaxTrailRenderers", 8,
				"Maximum TrailRenderer and LineRenderer components allowed on one avatar. Trail + Line renderers combined.");
			MaxConstraints = cfg.Bind("AntiCrash.Thresholds", "MaxConstraints", 100,
				"Maximum constraints allowed on one avatar. Unity constraints (Parent/Position/Rotation/Scale/Aim/LookAt) and VRC constraints combined.");
			HideAvatarTrips = cfg.Bind("AntiCrash.Thresholds", "HideAvatarTrips", 3,
				"How many categories an avatar must trip at once before HideAvatarOverBudget hides the whole avatar instead of trimming it.");

			ScanIntervalFrames = cfg.Bind("AntiCrash", "ScanIntervalFrames", 3,
				"How often (in frames) the poll looks for newly-loaded avatars and clamps them. 3 = within ~50 ms of an avatar appearing; the pass is bounded by the player count, so it stays cheap. Lower = faster reaction, higher cost.");

			ArchiveFolder = cfg.Bind("BundleArchive", "Folder", "",
				"Destination folder for archived bundles. Leave blank for <BepInEx>/VRChatArchiveMod/Bundles.");

			FewTagsEnabled = cfg.Bind("FewTags", "Enabled", true,
				"Show FewTags community nameplate tags (database by Fewdys, github.com/Fewdys/FewTags).");
			FewTagsDbUrl = cfg.Bind("FewTags", "DatabaseUrl", "https://raw.githubusercontent.com/Fewdys/FewTags/main/FewTags.json",
				"URL of the FewTags JSON database.");
			FewTagsUpdateMinutes = cfg.Bind("FewTags", "UpdateMinutes", 10,
				"How often (minutes) to re-download the tag database.");
			FewTagsMaxTagsPerUser = cfg.Bind("FewTags", "MaxTagsPerUser", 5,
				new ConfigDescription("Maximum tag lines shown above one player's nameplate. No hard ceiling in the mod any more - set it as high as you like.", new AcceptableValueRange<int>(1, 500)));
			FewTagsShowHeader = cfg.Bind("FewTags", "ShowHeader", true,
				"Show the rainbow '- FewTags -' header plate (red 'Malicious User' for flagged accounts).");
			FewTagsShowBigPlates = cfg.Bind("FewTags", "ShowBigPlates", true,
				"Show the large free-text plate some users have above their tags.");
			FewTagsFilterSlurs = cfg.Bind("FewTags", "FilterSlurs", true,
				"Hide FewTags entries containing slurs. The FewTags database is third-party and anyone can "
				+ "write to it, so this filters what YOUR client displays; it does not change the database.");
			FewTagsBaseY = cfg.Bind("FewTags", "BaseY", 119.05f,
				new ConfigDescription("Height of the first tag plate above the nameplate. Raise if tags overlap the name.", new AcceptableValueRange<float>(0f, 5000f)));
			FewTagsSpacing = cfg.Bind("FewTags", "Spacing", 0f,
				new ConfigDescription("Vertical gap between stacked tag plates on the LEGACY nameplate layout (Quick Stats). 0 = TIGHT (auto: lines stacked like text, measured from the label on screen). A number forces a bigger gap.", new AcceptableValueRange<float>(0f, 2000f)));
			FewTagsBaseYExpanded = cfg.Bind("FewTags", "BaseYExpanded", 205f,
				new ConfigDescription("Height of the FIRST tag plate on the CURRENT nameplate layout "
				+ "(NameplateFragment/ExpandedInfo). That plate is much taller than on the old "
				+ "layout, so the legacy 119 started the stack INSIDE the nameplate instead of "
				+ "above it. Raise to push the whole stack further up.", new AcceptableValueRange<float>(0f, 5000f)));
			FewTagsSpacingExpanded = cfg.Bind("FewTags", "SpacingExpanded", 0f,
				new ConfigDescription("Vertical gap between stacked tag plates on the CURRENT nameplate layout "
				+ "(NameplateFragment/ExpandedInfo). 0 = TIGHT (auto: lines stacked like text, measured from the label on screen, never overlapping). "
				+ "A number forces a bigger gap.", new AcceptableValueRange<float>(0f, 2000f)));

			VaTagsEnabled = cfg.Bind("VaTags", "Enabled", true,
				"VRChatArchive community player tags: PLAYERS tab, nameplate plates, tag DB sync. "
				+ "Tags are VRChatArchive metadata — only mod users see them; VRChat itself is never touched.");
			VaTagsApiBase = cfg.Bind("VaTags", "ApiBase", "https://vrchatarchive.org",
				"Base URL of the VRChatArchive tag API (owner/local testing: http://127.0.0.1:8081).");
			VaTagsUpdateMinutes = cfg.Bind("VaTags", "UpdateMinutes", 5,
				"How often (minutes) to re-download the shared tag database.");
			VaTagsShowPlates = cfg.Bind("VaTags", "ShowPlates", true,
				"Render each tagged user's VA tags on a plate above their nameplate.");
			VaTagsPlateY = cfg.Bind("VaTags", "PlateY", 91.05f,
				"Height of the VA tag plate above the nameplate (sits below the FewTags stack).");

			FreezeBadgeEnabled = cfg.Bind("Nameplate", "FreezeBadgeEnabled", true,
				"Show snowflake freeze indicator above player nameplate when frozen.");
			FreezeBadgeX = cfg.Bind("Nameplate", "FreezeBadgeX", 0f,
				"Horizontal offset for freeze badge above nameplate (0 = centered).");
			FreezeBadgeY = cfg.Bind("Nameplate", "FreezeBadgeY", 385f,
				"Vertical offset for freeze badge above nameplate in NameplateFragment coordinates (385 = rests neatly right above any group banner).");
			FreezeBadgeSize = cfg.Bind("Nameplate", "FreezeBadgeSize", 90.0f,
				"Size (width and height) of freeze badge icon.");

			WatchlistEnabled = cfg.Bind("Watchlist", "Enabled", true,
				"Highlight specific users with an animated rainbow ESP box and pop a notification when they join your instance.");
			RainbowUserIds = cfg.Bind("ESP", "RainbowUsers", "",
				"Comma-separated usr_ ids drawn with a cycling rainbow capsule instead of a trust "
				+ "colour, on every client running this mod. It is a signature: it changes how OTHER "
				+ "people see that player, not how that player sees themselves — your own capsule is "
				+ "never drawn for you. People without the mod see nothing either way.");
			RainbowSpeed = cfg.Bind("ESP", "RainbowSpeed", 0.35f,
				"How fast the rainbow cycles, in full loops per second.");
			LegendaryRainbow = cfg.Bind("ESP", "LegendaryRainbow", true,
				"Draw anyone holding the Archive Legendary rank with the cycling rainbow, without listing their id above. The rank is granted and revoked by the tag system itself, so the rainbow follows it: it appears when they reach Legendary and goes away if they stop being one. The site draws that tier as a swept gradient and this is the same idea in game.");

			WatchlistUserIds = cfg.Bind("Watchlist", "UserIds", "",
				"Comma-separated VRChat user ids to watch.");
			MemberNotifyRgb = cfg.Bind("Watchlist", "MemberNotifyRgb", false,
				"Member-join banner style: false = clean pink→violet pill, true = animated RGB rainbow.");

			InstancePanelsEnabled = cfg.Bind("InstancePanels", "Enabled", true,
				"Show the instance player list (left) and join/leave log (right). Right-Shift+L toggles.");
			// InstancePanels/Scale is GONE (2026-09-01): the panels size themselves from the screen
			// (Hud.Scale) on purpose, and nothing ever read this value.

			JoinNotifierEnabled = cfg.Bind("JoinNotifier", "Enabled", false,
				"Transient join/leave toast in the middle of the screen. OFF by default: the INSTANCE LOG "
				+ "panel already lists every join and leave with a timestamp, so the toast only repeated it "
				+ "over the world. Switch it on if you want the popup as well.");
			JoinNotifierShowLeave = cfg.Bind("JoinNotifier", "ShowLeave", true,
				"Also toast on leaves (off = joins only).");

			RadarEnabled = cfg.Bind("Radar", "Enabled", true,
				"Top-down radar showing every player around you (same position data as ESP). Right-Shift+M toggles.");

			RosterBottom = cfg.Bind("InstancePanels", "RosterBottom", true,
				"Position the player list HUD at the bottom-left of the screen instead of top-left.");
			ActiveFeaturesHudEnabled = cfg.Bind("HUD", "ActiveFeaturesHud", true,
				"Show active enabled features as colored plain text in top-left corner.");
			ActiveFeaturesFontSize = cfg.Bind("HUD", "ActiveFeaturesFontSize", 11,
				"Font size for the active features list in top-left (default 11).");

			EspPlayerColorIndex = cfg.Bind("ESP", "PlayerColorIndex", 0,
				"Color index for player outlines/capsules (0 = Trust Rank color, 1+ = custom neon color palette).");
			EspItemColorIndex = cfg.Bind("ESP", "ItemColorIndex", 0,
				"Color index for pickups/items ESP (0 = Gold, 1 = Cyan, 2 = Lime Green...).");
			EspPortalColorIndex = cfg.Bind("ESP", "PortalColorIndex", 0,
				"Color index for portals ESP (0 = Purple, 1 = Magenta, 2 = Cyan...).");

			HudMovementColorIndex = cfg.Bind("HUD", "MovementColorIndex", 0,
				"Color index for movement features in Active Features HUD (0 = Amber Yellow).");
			HudEspColorIndex = cfg.Bind("HUD", "EspColorIndex", 1,
				"Color index for ESP features in Active Features HUD (1 = Neon Cyan).");
			HudUtilityColorIndex = cfg.Bind("HUD", "UtilityColorIndex", 2,
				"Color index for utility/fun features in Active Features HUD (2 = Lime Green).");
			HudSecurityColorIndex = cfg.Bind("HUD", "SecurityColorIndex", 6,
				"Color index for security/anti-crash features in Active Features HUD (6 = Coral Red).");
			HudThemeIndex = cfg.Bind("HUD", "ThemeIndex", 0,
				"Theme preset for Active Features HUD (0 = Classic Vibrant).");


			// BLOCK OBSERVER — debug only, and READ-ONLY. It measures what VRChat actually does to the
			// local player/avatar objects when a remote avatar stops being drawn. It writes to nothing.
			BlockDebugEnabled = cfg.Bind("BlockDebug", "Enabled", false,
				"BLOCK OBSERVER (debug only, READ-ONLY). When a remote player's avatar goes from drawn to hidden, or back, "
				+ "log [BLOCK-DEBUG] snapshots of the LOCAL objects — player/avatar GameObjects, every renderer, animator, IK, "
				+ "transforms, VRChat's own flags read by name — at T-2s, T-1s, T0, +0.1, +0.25, +0.5, +1, +2, +5s, then a "
				+ "summary saying whether the avatar was destroyed, deactivated, its renderers disabled, or something else. "
				+ "Changes NOTHING: no block state, no visibility, no network. Costs a few ms/s in a full instance. "
				+ "Switch NsfwFilter and HideAvatarOverBudget OFF while measuring — they write the very state this observes.");
			Core.ConfigWatch.Watch(BlockDebugEnabled);
			BlockDebugWatchName = cfg.Bind("BlockDebug", "WatchName", "",
				"Optional: display name (or usr_ id) of the person doing the test with you. That one player is sampled every "
				+ "frame instead of 4x/s (frame-exact T0, dense T-2..T0 history) and gets a BASELINE snapshot as soon as they are seen.");
			// EVENT 33 TRACE (Event33TraceModule) — shares the BlockDebug/Enabled switch above.
			BlockDebugTraceHooks = cfg.Bind("BlockDebug", "TraceHooks", true,
				"With BlockDebug/Enabled: install OBSERVE-ONLY Harmony hooks (Object.Destroy, GameObject.SetActive, Renderer/Behaviour.enabled, "
				+ "Renderer.forceRenderingOff, Object.Instantiate, and a curated list of VRCAvatarManager / ModerationManager / VRC.Player / "
				+ "VRCPlayer / PlayerNameplate methods) that stamp each call, attribute it to a player and print the native call stack "
				+ "(VRChat's own method names). They never alter a call. Off = per-frame sampling only.");
			BlockDebugTraceSeconds = cfg.Bind("BlockDebug", "TraceSeconds", 3.5f,
				"How long after each event 33 every remote player's representation is sampled EVERY FRAME and diffed (1..10 s).");
			BlockDebugDecodeEvent33 = cfg.Bind("BlockDebug", "DecodeEvent33", true,
				"Decode the event-33 dictionary (moderation type, user ids...) by a guarded raw read of its entries — no il2cpp wrapper is built for any value.");
			BlockDebugHookVrcMethods = cfg.Bind("BlockDebug", "HookVrcMethods", false,
				"OPT-IN, off by default after the 3.9.77 crash: also prefix a curated list of VRChat methods (VRCAvatarManager, ModerationManager, "
				+ "VRC.Player, VRCPlayer, PlayerNameplate). This build's linker folds identical function bodies, so a trivial obfuscated method can "
				+ "share its code with unrelated methods game-wide and a hook on it fires with a foreign `this` (= crash). Static, trivial, "
				+ "pdata-less and shared bodies are refused, but leave this off unless you want that extra risk; the native stacks name the callers anyway.");

			UdonBlockCrashers = cfg.Bind("AntiUdon", "BlockCrashers", true,
				"Suspend a single Udon event for 10s when it fires at crasher-tier rate (400+ calls in one "
				+ "second). Local only: your client stops running that ONE event; nothing is sent and no "
				+ "other player is affected.");
			UdonCrasherPerSecond = cfg.Bind("AntiUdon", "CrasherPerSecond", 400,
				"Calls-per-second at which a single Udon event is treated as a crasher and suspended. "
				+ "Raise it when a legitimate world pump trips the guard \u2014 a busy video player can poll "
				+ "get_VideoPlayerType hundreds of times a second, and suspending it breaks the video FOR YOU.");
			UdonFloodPerSecond = cfg.Bind("AntiUdon", "FloodPerSecond", 2500,
				"Global flood ceiling: the TOTAL Udon events per second, across every script, above which "
				+ "the whole dispatch is treated as a crasher and non-lifecycle events are suspended for a "
				+ "few seconds. This is the net the per-event guard cannot be: a crasher that spreads its "
				+ "calls over many event NAMES never trips any single per-name counter, but it cannot hide "
				+ "from the total. Set well above a busy world (a full instance idles a few hundred/s), so "
				+ "only a genuine flood crosses it. 0 disables the global net.");
			UdonBlockAll = cfg.Bind("AntiUdon", "BlockAll", false,
				"PANIC: stop running world Udon events entirely. OFF by default because Udon IS the world — "
				+ "doors, pens, video players and seats stop working for you and you desync from what everyone "
				+ "else runs. Join/leave and start/enable events are still allowed so worlds do not wedge.");
			// The rate guards react AFTER a burst; this is the list for events you already know are
			// hostile. Lifecycle events are exempt so a typo here cannot wedge every world.
			UdonBlockNames = cfg.Bind("AntiUdon", "BlockNames", "",
				"Udon event names to always block, comma separated (case-insensitive). Lifecycle events "
				+ "(_start, _update, _onPlayerJoined...) are never blocked by name.");
			GlobalUdonInteract = cfg.Bind("Udon", "GlobalUdonInteract", false,
				"GLOBAL UDON: When enabled, interacting with any world button or interactable broadcasts its event globally to all players via SendCustomNetworkEvent and claims ownership.");

			// PHOTON GUARD. The Udon guards above only cover world scripts; a crasher that arrives as a
			// raw Photon event (the network layer underneath) never runs Udon at all. This sits on the
			// RECEIVE side: an event dropped here is never handed to VRChat, so the game cannot act on
			// it. Nothing is sent, nothing is replayed, and other players are never affected.
			VoiceMimicCode = cfg.Bind("VoiceMimic", "PhotonCode", 1,
				"Photon event code that carries player voice. Confirmed as 1 on the 2026 build (voice is code 1, not the old 7).");
			VoiceMimicMuteSelf = cfg.Bind("VoiceMimic", "MuteSelfWhileRelaying", false,
				"Drop your own outgoing voice while voice mimic runs, so others hear ONLY the copied stream. Off = stay muted yourself for the same effect without touching your own send path.");
			VoiceProbeEnabled = cfg.Bind("VoiceProbe", "Enabled", false,
				"TEMPORARY. Logs which inbound Photon event code carries player voice (shout with a friend and read the log). Turn off once identified.");
			PhotonGuardEnabled = cfg.Bind("PhotonGuard", "Enabled", true,
				"Drop incoming Photon events by code, and mute any player (Photon actor) who floods one "
				+ "event code. VRChat never sees a dropped event.");
			PhotonGuardBlockCodes = cfg.Bind("PhotonGuard", "BlockCodes", "",
				"Event codes to drop unconditionally, comma separated (e.g. 1,9). VRChat publishes no event "
				+ "names, so the EVENTS console (LOGGING) is the way to see what each code carries before "
				+ "blocking it.");
			PhotonGuardRatePerSender = cfg.Bind("PhotonGuard", "RateLimitPerSender", 500,
				"Per second, per actor, per event code: above this the (actor, code) pair is suspended. "
				+ "A full instance sends normal voice/IK sync at ~120/s PER PLAYER, so a limit near that "
				+ "muted innocents; a real flood is thousands/s, an order of magnitude above 500, still caught.");
			PhotonGuardSuspendSeconds = cfg.Bind("PhotonGuard", "SuspendSeconds", 10,
				"How long a flooding (actor, code) pair stays muted.");
			PhotonGuardLogBlocked = cfg.Bind("PhotonGuard", "LogBlocked", true,
				"Write one log line per suspension and one per 200 dropped events (never one per event).");
			UdonLogEnabled = cfg.Bind("UdonLog", "Enabled", true,
				"UDON tab: live console of the Udon events happening around you (who did what). Read-only — it observes events your client already runs and never sends or blocks any.");
			UdonLogFrameEvents = cfg.Bind("UdonLog", "ShowFrameEvents", false,
				"Also log per-frame events (_update, _lateUpdate, …). Very noisy and costly — off by default.");
			// UdonLog/InterestingOnly is GONE (2026-09-01): the get_*/set_* noise filter is
			// unconditional in UdonLogModule by design, so the switch was shown and never read.

			QMTabEnabled = cfg.Bind("QuickMenu", "NativeTab", true,
				"Add a VRChat Archive tab to VRChat's own QuickMenu tab strip (clones a disabled built-in tab; never modifies the game's own objects).");
			UserMenuEnabled = cfg.Bind("QuickMenu", "UserMenuCard", true,
				"Add Mod Features (orbit / sit / ring around them), Clone Avatar and Copy Avatar Id cards to the player's page in VRChat's QuickMenu.");
			DevToolsEnabled = cfg.Bind("QuickMenu", "DevToolsEnabled", false,
				"Keep VRChat's built-in Buttons_DevTools active and themed in the selected user menu.");
			WingPlayersEnabled = cfg.Bind("QuickMenu", "WingPlayers", true,
				"Show the instance roster inside VRChat's own left wing menu instead of a floating overlay window.");
			WingLogEnabled = cfg.Bind("QuickMenu", "WingLog", true,
				"Show the instance log inside VRChat's own right wing menu. This is a MENU panel, not an "
				+ "on-screen overlay, so the HUD master switch leaves it alone.");
			InspectPort = cfg.Bind("Diagnostics", "InspectPort", 0,
				"Open a read-only inspection server on 127.0.0.1 at this port so the scene can be queried live "
				+ "(/q?op=find&name=... , op=tree|comps|texts). Answers are produced on the main thread, so it cannot "
				+ "crash the game the way an off-thread read would. 0 = off, which is the default: a port that can read "
				+ "the scene has no business being open in a shipped build. 8792 is a good choice while debugging.");
			RetryFailedPlugins = cfg.Bind("Compatibility", "RetryFailedPlugins", true,
				"Other BepInEx plugins load BEFORE this mod repairs il2cpp, so one that touches a mis-bound "
				+ "Unity method dies on this VRChat build (UnityExplorer is the usual casualty). When this is on, "
				+ "any plugin BepInEx reported as failed is asked to load once more, after the repairs are armed. "
				+ "Nothing is patched and a plugin that loaded fine is never touched.");
			ArchiveFavAvatarGrid = cfg.Bind("Favorites", "AvatarGrid", true,
				"Add an ARCHIVE FAVORITES row to the avatars menu sidebar, showing your Archive favourites as a grid; "
				+ "clicking a card wears that avatar. Switch it off if it ever disturbs VRChat's own sidebar layout.");
			RuntimeEditorEnabled = cfg.Bind("RuntimeEditor", "Enabled", true,
				"Start RuntimeUnityEditor (GPL-3, by ManlyMarco), hosted by the mod itself: a live inspector for the "
				+ "scene hierarchy, every component and its fields, plus a C# REPL. Off by default -- it is a developer "
				+ "tool, but it stays hidden until the key below is pressed and it disarms itself if a start ever kills the game.");
			RuntimeEditorKey = cfg.Bind("RuntimeEditor", "Key", "F12",
				"Which key opens and closes RuntimeUnityEditor. Any UnityEngine.KeyCode name.");
			NetworkLogEnabled = cfg.Bind("NetworkLog", "Enabled", true,
				"Record the Photon network events this client RECEIVES. Listen-only: the mod never sends, "
				+ "raises or replays an event.");
			NetworkLogToFile = cfg.Bind("NetworkLog", "ToFile", false,
				"Also write every received event to BepInEx/VRChatArchiveMod/network/. Always on in debug mode.");
			PhotonLogEnabled = cfg.Bind("NetworkLog", "FullPhotonLog", false,
				"FULL PHOTON LOG (off by default). Records ABSOLUTELY EVERY inbound Photon event to its own file in "
				+ "BepInEx/VRChatArchiveMod/photon/ — clock, frame, code and its best-known name, sending actor, and the "
				+ "DECODED payload: byte arrays as hex plus the readable text inside them, dictionaries key by key. "
				+ "Listen-only, like the console above: nothing is ever sent, dropped or altered. This decodes every packet "
				+ "on a hundreds-per-second stream, so expect it to cost frames while it runs — the hook is not even "
				+ "installed until you switch this on, and it is removed the moment you switch it off.");
			Core.ConfigWatch.Watch(PhotonLogEnabled);
			// DISPLAY, not recording. UdonLogEnabled controls whether events are captured at all;
			// these two decide which of the two SOURCES the console shows, so the feed can be Udon
			// only, network only, or both, without losing what is being recorded underneath.
			EventsShowUdon = cfg.Bind("Udon", "ShowUdonRows", true,
				"Show Udon events in the console feed.");
			EventsShowNetwork = cfg.Bind("NetworkLog", "ShowInEventsConsole", true,
				"Interleave the received Photon events into the EVENTS console next to the Udon events, "
				+ "so one panel shows everything happening around you.");
			NetworkInterestingOnly = cfg.Bind("NetworkLog", "InterestingOnly", true,
				"Hide the bulk traffic and keep the rare events. VRChat does not publish what its event "
				+ "codes mean, so this does NOT filter by a guessed name \u2014 it filters by measured RATE: a code "
				+ "that has ever exceeded BulkPerSecond is continuous sync and gets hidden, everything else is "
				+ "kept. Self-calibrating, and it cannot mislabel an event it does not understand.");
			NetworkBulkPerSecond = cfg.Bind("NetworkLog", "BulkPerSecond", 20,
				"Peak events-per-second above which a Photon event code counts as bulk sync traffic and is "
				+ "hidden by InterestingOnly.");
			MenuSkinEnabled = cfg.Bind("QuickMenu", "SkinBackground", true,
				"Replace VRChat's QuickMenu backdrop with the VRChat Archive image.");
			// Two crossing sheets of scrolling noise over the wallpaper. Not a refraction shader
			// (those cannot be loaded into VRChat Il2Cpp UI) but the same flowing-caustics look, at
			// the cost of a couple of uvRect writes per frame while the menu is open.
			MenuSkinClearVeil = cfg.Bind("QuickMenu", "ClearBackgroundVeil", true,
				"Hide the translucent full-panel layers VRChat draws OVER the menu wallpaper, which is "
				+ "what washes the image out. Only flat see-through fills covering nearly the whole panel "
				+ "are touched; every one is named in the log and put back when this is switched off.");
			// ARCHIVE FAVOURITES IS NOT A SETTING ANY MORE.
			//
			// It was a ConfigEntry kept "so it can still be turned off by editing this file if a
			// VRChat update ever makes it misbehave". That escape hatch is exactly what cost the
			// feature: the value ended up false — reachable from the desktop client's settings page
			// like any other switch — and the whole thing vanished with no error anywhere, because
			// every module gated on it returns silently. Hours were spent looking for a bug in code
			// that was simply switched off.
			//
			// The Archive category IS the favourites feature. A switch whose only correct position
			// is on is not a choice, it is a way to break the mod by accident.

			// WHICH CATEGORY WE TAKE OVER, and it is not a cosmetic choice. "SDK Test Avatars" holds
			// LOCAL SDK builds, so VRChat's Apply takes a different route for anything sitting in it —
			// which is why Apply behaved oddly there. A VRC+ favourites slot has exactly the semantics
			// we want: "avatars I saved in order to wear them".
			ArchiveCategoryName = cfg.Bind("Favorites", "CategoryToBorrow", "SDK Test Avatars",
				"Name of the avatar-menu category the Archive list takes over. Its own contents are "
				+ "hidden while the mod runs and come back when it is off. Falls back to SDK Test Avatars, "
				+ "then to the last category, if this name is not present. VRC+ slots are never borrowed.");

			// THE WORLD / SOCIAL / AVATAR LIST SWITCHES ARE GONE (2026-09-01).
			//
			// Favorites/WorldList, SocialList, AvatarList, ForceNewSection, WorldListSlot and
			// SocialListSlot all gated modules that are no longer registered (Plugin.cs: the grafted
			// world/social grids were disarmed 2026-08-26/28, worlds and users are the desktop
			// client's FAVORIS page now; AvatarFavList is off with them). The client's MOD SETTINGS
			// page is built by reflection over these fields, so every one of them showed up as a
			// working switch that switched nothing. The slot numbers and the ForceNewSection
			// experiment survive as constants at the top of this class, for the day the injectors
			// come back — the WHY next to them is the part worth keeping.

			// AUTO-CLEAN IS ALWAYS ON, AND NOT A SETTING ANY MORE.
			//
			// Same reasoning as NativeCategory: a broken favourite is what the feature exists to
			// repair, and a switch whose only correct position is "on" is only ever a way to break
			// the mod by accident. Removing the ConfigEntry also removes the toggle from the
			// desktop client automatically (its MOD SETTINGS page is built by reflection over
			// ModConfig's fields).

			QmTextColor = cfg.Bind("QuickMenu", "TextColor", "#D3A4FF",
				"Hex colour for VRChat's QuickMenu TEXT while the Archive theme is on (e.g. #D3A4FF). Needs ArchiveTheme on.");
			QmGradientStart = cfg.Bind("QuickMenu", "GradientStart", "#FF6AD5",
				"Hex colour at the TOP of the card sweep (e.g. #FF6AD5).");
			QmGradientEnd = cfg.Bind("QuickMenu", "GradientEnd", "#8143E6",
				"Hex colour at the BOTTOM of the card sweep (e.g. #8143E6).");
			QmBackgroundSolid = cfg.Bind("QuickMenu", "BackgroundSolid", false,
				"Use a SOLID colour for the QuickMenu background instead of the Archive wallpaper image. Needs SkinBackground on.");
			QmBackgroundColor = cfg.Bind("QuickMenu", "BackgroundColor", "#0B0714",
				"Hex colour of the solid background (used when BackgroundSolid is on), e.g. #0B0714.");
			QmWallpaperDim = cfg.Bind("QuickMenu", "WallpaperDim", 0f,
				"Darken the QuickMenu wallpaper, 0 = untouched, 1 = black. Applies to the image background.");
			QmCustomBackgroundImage = cfg.Bind("QuickMenu", "CustomBackgroundImage", "",
				"Absolute path to a PNG/JPG used as the QuickMenu wallpaper. Empty = the Archive image. The client sets this with its Choose-image button.");
			MenuThemeEnabled = cfg.Bind("QuickMenu", "ArchiveTheme", true,
				"Repaint VRChat's QuickMenu in the Archive's pink/violet instead of its stock teal, and "
				+ "turn its text violet. Cards are tinted by where they sit on screen so a page sweeps "
				+ "pink to violet — a single UI image cannot hold a gradient on its own. Reversible: "
				+ "switching this off restores VRChat's own colours without a restart.");
			// QuickMenu/OneMenuAtATime is GONE (2026-09-01): it hid VRChat's QuickMenu while the
			// mod's TAB menu was open, and that menu is sealed — the switch could never do anything.
			// OFF by default. This walks a menu page and writes a dump file, on the main thread —
			// a visible hitch every time you open a page you have not opened before. It is a tool
			// for working out what VRChat's menus contain, not something a normal session should
			// be paying for, and it shipped switched on.
			ModControlEnabled = cfg.Bind("Client", "ModControl", true,
				"Let the VRChat Archive desktop client read and change these settings while the game "
				+ "runs. The mod polls the client's loopback bridge once a second and applies whatever you "
				+ "changed there — the game process never opens a port of its own. This is what makes the "
				+ "client's MOD SETTINGS page work; with it off the mod is controlled only from in game.");
			SoundboardEnabled = cfg.Bind("Soundboard", "Enabled", true,
				"HEAR the soundboard. Switch this off and other members can no longer make sound come out of your headset — you can still trigger clips yourself.");
			SoundboardVolume = cfg.Bind("Soundboard", "Volume", 0.5f,
				"How loud incoming clips are. YOURS, not the sender's: nobody else can turn this up.");
			SoundboardPollSeconds = cfg.Bind("Soundboard", "PollSeconds", 3f,
				"How often the mod asks the server what was triggered. Lower is snappier and chattier.");
			GravityPlayerOff = cfg.Bind("Gravity", "PlayerOff", false,
				"Float: sets YOUR gravity to zero through the SDK's own SetGravityStrength. Local — gravity is simulated per client, so nobody else is affected and there is no API to change theirs.");
			GravityWorldOff = cfg.Bind("Gravity", "WorldOff", false,
				"Also zero Physics.gravity, so loose objects float. Local only, and it can genuinely break a world FOR YOU — lifts, physics puzzles and anything that relies on falling stop working.");

			DebugMode = cfg.Bind("Debug", "Enabled", false,
				"Debug build behaviour: per-module profiler on, health snapshot every 5s instead of 30s, verbose "
				+ "module logging, and Unity warnings captured too. Also switches on automatically when a file "
				+ "named DEBUG exists in BepInEx/VRChatArchiveMod/.");
			UdonLogOverlay = cfg.Bind("UdonLog", "Overlay", true,
				"Draw the Udon console in-world (top-left) as well as in the menu. Local overlay only — nobody else sees it.");
			UdonLogOverlayLines = cfg.Bind("UdonLog", "OverlayLines", 12,
				"How many recent Udon events the in-world overlay shows.");
			RadarRange = cfg.Bind("Radar", "RangeMeters", 50f,
				"Distance from the radar centre to its edge, in metres.");
			RadarSize = cfg.Bind("Radar", "SizePixels", 380f,
				"Radar size on screen, in design pixels for a 1080p screen — it is multiplied by the "
				+ "HUD scale, so it keeps the same on-screen proportion at 1440p and 4K.");
			RadarNames = cfg.Bind("Radar", "ShowNames", true,
				"Write each player's name next to their blip, in their trust colour. Only the players "
				+ "actually inside the radar range are labelled \u2014 the ones clamped to the rim would just "
				+ "pile their names on top of each other.");
			RadarMap = cfg.Bind("Radar", "ShowMap", false,
				"Draw the WORLD under the radar blips, from a camera parked above you. OFF by default: this is the one option here that genuinely costs frames, since it renders the scene a second time.");
			RadarMapResolution = cfg.Bind("Radar", "MapResolution", 256,
				"Size of the map render, in pixels. It is shown inside a small radar, so bigger mostly buys cost rather than detail.");
			RadarMapEveryFrames = cfg.Bind("Radar", "MapEveryFrames", 1,
				"Render the map picture every N map ticks (the map is rendered every frame by default (1); 2 = every other frame, and so on). Between renders the picture slides with you, so it stays continuous either way; raise this only to save GPU in a heavy world.");
			RadarMapHeight = cfg.Bind("Radar", "MapCameraHeight", 60f,
				"How high above you the map camera sits, in metres. Too low and a ceiling is all you see.");
			RadarMapOpacity = cfg.Bind("Radar", "MapOpacity", 0.75f,
				"How strongly the map shows through. Lower keeps the blips the loudest thing on the radar.");
			RosterPositions = cfg.Bind("Hud", "RosterPositions", true,
				"Show each player's live X/Y/Z under their name in the PLAYERS panel.");
			HudEnabled = cfg.Bind("Hud", "Enabled", true,
				"Master switch for everything the mod draws on your screen: the radar, the player list and "
				+ "instance log panels, the glows, the Udon overlay. Off hides them all in one press and "
				+ "remembers which ones were on; back on puts exactly those back. The mod's menus are not "
				+ "affected — only what is drawn over the game.");
			HudSaved = cfg.Bind("Hud", "Saved", "",
				"What was on when the HUD was switched off, so switching it back on restores that and not a "
				+ "default. Written by the mod; there is no reason to edit it.");
			OrbitRadius = cfg.Bind("Orbit", "RadiusMeters", 2.0f,
				"How far from the player you circle, in metres.");
			OrbitSpeed = cfg.Bind("Orbit", "DegreesPerSecond", 60f,
				"Orbit speed. Negative values circle the other way.");
			OrbitHeight = cfg.Bind("Orbit", "HeightOffset", 0.5f,
				"Height above the player's feet while orbiting, in metres.");
			SitHeight = cfg.Bind("Orbit", "SitHeightOffset", 0.1f,
				"Clearance above the top of the target's avatar mesh when sitting on them.");
			ObjOrbitCount = cfg.Bind("ObjectOrbit", "Count", 0,
				new ConfigDescription("Object Orbit: MAX objects in the ring. 0 = ALL of them (default). Local rings can take up to 5000; synced (networked) rings are always capped at 40 - every owned object is its own ObjectSync stream and 24 of them once disconnected you.", new AcceptableValueRange<int>(0, 5000)));
			ObjOrbitRange = cfg.Bind("ObjectOrbit", "SearchRange", 0f,
				new ConfigDescription("Object Orbit: how far around the centre the props are collected, in metres. 0 = UNLIMITED (the whole world, default); set a number to cap it.", new AcceptableValueRange<float>(0f, 5000f)));
			ObjOrbitRadius = cfg.Bind("ObjectOrbit", "RingRadius", 3f,
				"Radius of the ring the objects fly in, in metres.");
			ObjOrbitSpeed = cfg.Bind("ObjectOrbit", "DegreesPerSecond", 70f,
				"How fast the ring turns. Negative spins the other way.");
			ObjOrbitHeight = cfg.Bind("ObjectOrbit", "HeightOffset", 1.2f,
				"Height of the ring above the centre's feet, in metres.");
			ObjOrbitMaxSize = cfg.Bind("ObjectOrbit", "MaxObjectSize", 4f,
				"Largest object, in metres, that counts as a prop. This is the guard that stops the "
				+ "floor, the walls and the skybox being torn out of the world along with the furniture.");
			ObjOrbitSpin = cfg.Bind("ObjectOrbit", "Tumble", true,
				"Also tumble each object on its own axis while it orbits.");
			BadAppleMusic = cfg.Bind("Mark", "BadAppleMusic", true,
				"Play the Bad Apple!! soundtrack (embedded in the mod) with the object show, and drive the "
				+ "picture off the music's own playback position so the two cannot drift — the networked mode "
				+ "holds frame 0 while it takes ownership of the objects, and starting the song at the button "
				+ "press instead left it that far ahead. Off = the show runs silently on a wall clock.");
			BadAppleMusicVolume = cfg.Bind("Mark", "BadAppleMusicVolume", 0.7f,
				new ConfigDescription("Volume of the Bad Apple soundtrack, 0..1. It plays locally, in your own "
					+ "ears only — nobody else in the instance hears it.", new AcceptableValueRange<float>(0f, 1f)));

			ObjOrbitSynced = cfg.Bind("ObjectOrbit", "Synced", false,
				"EVERYONE SEES IT. Takes ownership of real pickups through the SDK's Networking.SetOwner and lets VRChat broadcast their position, the way it already does when a player carries "
				+ "something. Restricted to VRC_Pickup objects that nobody is holding — world geometry, doors and seats are never touched — and every position is restored on stop. OFF by default, because unlike the local mode this one is other people's business too.");

			// --- Elevator (a rising platform of the world's loose props — same trick as Object Orbit) ---
			ElevatorCount = cfg.Bind("Elevator", "Count", 0,
				new ConfigDescription("Elevator: MAX objects in the platform. 0 = ALL of them (default). Local platforms take up to 400; synced (networked) ones are capped at 40 — each owned object is its own ObjectSync stream and too many once disconnected you.", new AcceptableValueRange<int>(0, 400)));
			ElevatorRange = cfg.Bind("Elevator", "SearchRange", 0f,
				new ConfigDescription("Elevator: how far around the rider the props are collected, in metres. 0 = UNLIMITED (the whole world, default); set a number to cap it.", new AcceptableValueRange<float>(0f, 5000f)));
			ElevatorClimbSpeed = cfg.Bind("Elevator", "ClimbSpeed", 1.5f,
				new ConfigDescription("How fast the platform rises, in metres per second. Too fast and the rising colliders clip through the rider instead of carrying them up; slower is safer.", new AcceptableValueRange<float>(0.1f, 20f)));
			ElevatorMaxHeight = cfg.Bind("Elevator", "MaxHeight", 15f,
				new ConfigDescription("How high the platform climbs above where it started, in metres. 0 = keep rising for as long as it is on.", new AcceptableValueRange<float>(0f, 500f)));
			ElevatorPlatformRadius = cfg.Bind("Elevator", "PlatformRadius", 0f,
				new ConfigDescription("Radius of the disc the props tile into, in metres. 0 = ALL AT ONE POINT (default) — every prop stacked on the exact same spot so their colliders fuse into one solid block right under the feet; the auto-correct keeps that point locked under the rider. Raise it only if you want a wider floor.", new AcceptableValueRange<float>(0f, 12f)));
			ElevatorStartDepth = cfg.Bind("Elevator", "StartDepth", 0.1f,
				new ConfigDescription("The catch gap: how far below the rider's feet the platform starts and re-catches, in metres. Small on purpose (owner, 2026-09-13: 0.25 left too much air under the feet before the platform pushed) so the plate spawns right under the feet and lifts immediately. Bigger = looser, the platform sits lower and takes longer to make contact.", new AcceptableValueRange<float>(0f, 5f)));
			ElevatorAutoCorrect = cfg.Bind("Elevator", "AutoCorrect", false,
				"Re-snap the platform under the rider every physics tick when they slip off it. OFF (default) lets the platform just rise steadily, giving the rider time to settle and network-sync onto it instead of the plane chasing them. ON = a powerful predictive glue that leads their movement and drives the box up hard.");
			ElevatorLead = cfg.Bind("Elevator", "Lead", 0.2f,
				new ConfigDescription("Auto-correct PREDICTION LEAD, in seconds. The glue aims the platform where the rider is heading (their velocity x this) instead of where they were, to beat their ping / sync delay. Bigger = leads further ahead; 0 = no prediction.", new AcceptableValueRange<float>(0f, 1f)));
			ElevatorSettleSeconds = cfg.Bind("Elevator", "SettleSeconds", 2f,
				new ConfigDescription("Seconds the platform sits STILL under the rider before it starts rising, so VRChat has time to sync the objects out to everyone (and the rider can land on them) first. 0 = rise immediately.", new AcceptableValueRange<float>(0f, 10f)));
			ElevatorSynced = cfg.Bind("Elevator", "Synced", true,
				"EVERYONE SEES IT. Takes ownership of real pickups through the SDK's Networking.SetOwner and lets VRChat broadcast their position — exactly like Object Orbit's synced mode. ON by default here because the whole point is to be seen by the room; local mode only ever shows on your own screen. NOTE: syncing makes the platform VISIBLE to everyone, but VRChat will not reliably carry a player standing on moving pickups (object SCALE is not networked, so a few small props never form a solid floor on the other client) — the person who actually rises is the one whose own client lifts them. Restricted to VRC_Pickup objects nobody is holding; every position is restored on stop.");

			AllowIl2CppDelegates = cfg.Bind("Compatibility", "AllowIl2CppDelegates", false,
				"Let features build il2cpp delegates through Il2CppInterop. OFF because on this VRChat "
				+ "build that call takes the whole game down with an access violation it is not possible "
				+ "to catch. Turning it on costs full log capture, the diagnostics Unity feed, video-URL "
				+ "detection and avatar-card clicks -- but only turn it on if the crash is fixed.");


			BadAppleLoop = cfg.Bind("BadApple", "Loop", true,
				"Restart Bad Apple from the top when it finishes instead of clearing the chatbox.");
			BadAppleIntervalMs = cfg.Bind("BadApple", "IntervalMs", 0,
				"Milliseconds per chatbox message. 0 = default (200). Frames are baked every 50 ms "
				+ "and sampled, so any cadence keeps the full 3:39 runtime. The spam filter is "
				+ "enforced by the network/receiving clients — if viewers' bubbles freeze or mute, "
				+ "raise this. Experimental floor: 100.");
			BadAppleOscHost = cfg.Bind("BadApple", "OscHost", "127.0.0.1",
				"Host receiving the OSC chatbox messages (VRChat's OSC input).");
			BadAppleOscPort = cfg.Bind("BadApple", "OscPort", 9000,
				"UDP port of VRChat's OSC input (default 9000).");
			BadAppleCharset = cfg.Bind("BadApple", "Charset", "",
				"Glyph ramp used as pixels, lightest to darkest, any length (levels are rescaled). "
				+ "Empty = the baked 32-hanzi ramp measured by tools/pick_charset.py.");

			// UI/CaptureInput, UI/AccentPreset and UI/Scale are GONE (2026-09-01). All three only
			// ever acted on the sealed TAB menu — nothing sets Menu.Visible any more — so the
			// desktop client was showing switches that did nothing at all. A ConfigEntry with no
			// reader is a lie in the settings page; removing the entry removes the row.
			AltFreeCursor = cfg.Bind("UI", "AltFreeCursor", true,
				"Hold Left Alt (or Right Alt) to detach the mouse from VRChat and move the cursor — and "
				+ "walk — freely; release it and the game takes control back.");

			SpeedEnabled = cfg.Bind("Movement", "CustomSpeed", false,
				"Override your own walk / run / strafe / jump through VRChat's own per-player setters "
				+ "(the same ones a world's Udon uses). LOCAL only — there is no API to change anybody "
				+ "else's, and nothing is sent. Switching it off restores the world's own values.");

			WalkMod = cfg.Bind("Movement", "WalkMod", false,
				"Take over WALKING speed. Off leaves the world's own value alone.");
			RunMod = cfg.Bind("Movement", "RunMod", false,
				"Take over RUNNING speed (shift).");
			JumpMod = cfg.Bind("Movement", "JumpMod", false,
				"Take over JUMP strength. Some worlds set jump to 0 on purpose — this overrides that.");

			WalkSpeed = cfg.Bind("Movement", "WalkSpeed", 2f,
				"Walking speed. VRChat's default is 2.");
			RunSpeed = cfg.Bind("Movement", "RunSpeed", 4f,
				"Running speed (shift). VRChat's default is 4.");
			StrafeSpeed = cfg.Bind("Movement", "StrafeSpeed", 2f,
				"Sideways speed. VRChat's default is 2.");
			JumpImpulse = cfg.Bind("Movement", "JumpImpulse", 3f,
				"Jump strength. VRChat's default is 3; 0 in worlds where jumping is disabled.");

			FlyEnabled = cfg.Bind("Movement", "Fly", false,
				"Local desktop fly. Left-Ctrl+F toggles. Move: WASD + E/Q (up/down), Shift = faster.");
			NoclipEnabled = cfg.Bind("Movement", "Noclip", false,
				"Pass through walls while flying (disables your collider). Left-Ctrl+N toggles.");
			ClickTpEnabled = cfg.Bind("Movement", "ClickTeleport", false,
				"Hold right mouse + left click to teleport to the surface you're aiming at. Local, self-only.");
			ClickTpMaxDistance = cfg.Bind("Movement", "ClickTeleportMaxDistance", 120f,
				"Maximum click-teleport distance (metres).");
			FlySpeed = cfg.Bind("Movement", "FlySpeed", 10f,
				"Fly speed (units per second).");
			FlyBoostSpeed = cfg.Bind("Movement", "FlyBoostSpeed", 18f,
				"Fly speed while holding Shift.");
			FlyRotateSpeed = cfg.Bind("Movement", "FlyRotateSpeed", 90f,
				"Arrow-key rotation speed while flying (degrees per second). Left/Right = turn, Up/Down = tilt.");
			ArrowRotateEnabled = cfg.Bind("Movement", "ArrowRotate", true,
				"Left/Right arrow keys turn your player (works on the ground, not just while flying). "
				+ "Speed follows RotateSpeed.");
			GhostSavePosition = cfg.Bind("Movement", "GhostSavePosition", true,
				"When turning Ghost mode off, return to the position where Ghost was activated.");

			// PLAYER ROTATOR. Off by default: it takes the arrow and page keys while it is on, and it
			// widens VRChat's neck clamp, so it is not something to be holding quietly in the
			// background.
			RotatorEnabled = cfg.Bind("Rotator", "Enabled", false,
				"Tilt your own capsule and take the view with it (RightShift+R). Up/Down arrows pitch, "
				+ "PageUp/PageDown roll, RightShift+F flips you upside down, RightShift+Backspace resets. "
				+ "Local: nothing is sent by hand.");
			RotatorFreeLook = cfg.Bind("Rotator", "FreeLook", true,
				"While the rotator is on, remove VRChat's limit on how far mouse-look may pitch, so the "
				+ "view can follow you all the way over instead of stopping at the neck's stock range. "
				+ "Put back exactly as found when the rotator is switched off.");
			RotatorHoldGravity = cfg.Bind("Rotator", "HoldGravity", true,
				"Hold YOUR OWN gravity at zero while you are actually leaning, so VRChat does not stand "
				+ "you straight back up and fight the tilt to a standstill. It follows the LEAN, not the "
				+ "switch: armed and upright, your gravity is untouched, and it is handed back the moment "
				+ "you are level again. Only you are affected — the world's gravity is never written "
				+ "(that is the GRAVITY feature's own switch, and two owners of one global is how a "
				+ "restore writes back the wrong number). Turn this off if you would rather fall while tilted.");
			RotatorSpeed = cfg.Bind("Rotator", "Speed", 120f,
				"Tilt speed in degrees per second (5 to 720).");
			BlockedByProbe = cfg.Bind("Probe", "BlockedBy", true,
				"Ask VRChat once per session who has blocked you (ApiPlayerModeration.FetchAllAgainstMe), so the "
				+ "player lists can show BLOCKED. It is the only thing the mod does that both crosses the il2cpp "
				+ "delegate bridge and walks a native collection, so it gets its own off switch. Turning it off "
				+ "costs you the BLOCKED tag and nothing else. The mod also disables it BY ITSELF for one session "
				+ "if the previous one died inside the probe.");
			// Fresh key on purpose: the old "AntiBlock" entry switched a completely different
			// implementation (a ModerationManager patch), so carrying its saved value over would
			// mean honouring a setting the user made about something else.
			TrueViewEnabled = cfg.Bind("Moderation", "TrueView", true,
				"Keep a remote player looking like themselves when VRChat replaces their avatar with the "
				+ "skeleton-less fallback: the fallback is suppressed, a copy of their real avatar is cached "
				+ "while it is healthy and shown in its place, and the real one is re-requested when nothing "
				+ "was cached. Nameplate, laser hitbox, local audio and animation are restored alongside.");
			TrueViewAvatar = cfg.Bind("Moderation", "TrueViewAvatar", true,
				"TrueView: suppress the fallback avatar and put the player's real one back. This is the "
				+ "part that does the actual work; the rest are cosmetic repairs around it.");
			TrueViewNameplates = cfg.Bind("Moderation", "TrueViewNameplates", true,
				"TrueView: keep the nameplate and chat bubble visible, and hold them at head height while "
				+ "the avatar is missing (VRChat's own positioner is parked meanwhile and handed back after).");
			TrueViewSelectRegion = cfg.Bind("Moderation", "TrueViewSelectRegion", true,
				"TrueView: keep the SelectRegion hitbox enabled so the player can still be laser-targeted.");
			TrueViewUnmute = cfg.Bind("Moderation", "TrueViewUnmute", true,
				"TrueView: undo the local mute on the player's USpeak AudioSource.");
			TrueViewAnimators = cfg.Bind("Moderation", "TrueViewAnimators", true,
				"TrueView: force AlwaysAnimate culling so a restored avatar keeps moving instead of freezing. "
				+ "Costs CPU while on; the previous culling mode is restored when TrueView is switched off.");
			TrueViewRig = cfg.Bind("Moderation", "TrueViewRig", true,
				"TrueView: while the cached copy stands in for a stripped avatar, drive its humanoid bones "
				+ "from the player's own IK targets so it moves and poses with them instead of freezing. "
				+ "Runs only for a player currently showing the copy; a non-humanoid copy just has its "
				+ "root kept over the player's hips.");

			RotatorNeckLimit = cfg.Bind("Rotator", "NeckLimit", 180f,
				"How far the widened neck clamp reaches, in degrees (90 to 1800). 180 is already all the "
				+ "way round; a huge value risks feeding infinities into VRChat's own smoothing.");

			PortalInfiniteEnabled = cfg.Bind("Fun", "InfinitePortal", false,
				"Portals you drop with VRChat's own Create Portal stop closing, and the wait between drops "
				+ "is removed, so you can lay several down (to different worlds) for RP without them expiring. "
				+ "These portals are networked: everyone in the instance sees them and can use them. Off by "
				+ "default. It never spawns a portal itself and never touches anyone else's.");

			BoxDropEnabled = cfg.Bind("BoxDrop", "Enabled", false,
				"Reverse GoGoLoco: your local player is not moved, but the position written into your outbound pose is offset, so others see your box elsewhere. Off by default.");
			BoxDropX = cfg.Bind("BoxDrop", "OffsetX", 0f, "Sideways offset of the sent box, in metres.");
			BoxDropY = cfg.Bind("BoxDrop", "OffsetY", -6f, "Vertical offset of the sent box, in metres. Negative drops it below you / under the floor.");
			BoxDropZ = cfg.Bind("BoxDrop", "OffsetZ", 0f, "Forward offset of the sent box, in metres.");


			DumpOutbound = cfg.Bind("Network", "DumpOutbound", false,
				"Diagnostic. Write the next few outbound Photon events to the log — event code, payload type and the first 64 bytes — then stop by itself. Read-only: nothing is blocked, delayed or altered. Use it to find out which event carries the player pose and what is inside it.");


			DumpOutbound = cfg.Bind("Network", "DumpOutbound", false,
				"Diagnostic. Write the next few outbound Photon events to the log — event code, payload type and the first 64 bytes — then stop by itself. Read-only: nothing is blocked, delayed or altered. Use it to find out which event carries the player pose and what is inside it.");

			SpawnSoundEnabled = cfg.Bind("SpawnSound", "Enabled", false,
				"Play a short stinger ('The Spawn Dark Squad') once each time you finish loading into an instance.");
			SpawnSoundVolume = cfg.Bind("SpawnSound", "Volume", 0.6f,
				"Volume of the spawn stinger, 0 to 1.");

			SignatureSoundEnabled = cfg.Bind("SignatureSound", "Enabled", false,
				"Play a person's own signature clip when they arrive in your instance. Nothing is sent over the network: every client sees the same arrival and plays it for itself, so the person it belongs to hears it too.");
			SignatureSoundVolume = cfg.Bind("SignatureSound", "Volume", 0.6f,
				"Volume of signature sounds, 0 to 1.");

			// Bounded on purpose: the point is to save the walk across a room, not to let one key
			// collect every loose object in a world from the spawn point.
			ForceGrabRange = cfg.Bind("Fun", "ForceGrabRange", 30f,
				"How far Force Grab (the client's FUN → Force Grab button) will reach for a pickup, in "
				+ "metres. Objects held by another player are never taken.");
			ForceJumpForce = cfg.Bind("Fun", "ForceJumpForce", 8f,
				"Force Jump (RightShift+J, or the client's FUN button): upward launch speed in metres/second. "
				+ "A normal VRChat jump is about 3. Ordinary movement \u2014 others see you jump, nobody else's "
				+ "client is touched. Clamped 1\u201350.");
			ForcePickupRange = cfg.Bind("Fun", "ForcePickupRange", 100f,
				"Force Pickup: the grab range, in metres, forced onto a world's locked pickups. A world that sets a tiny proximity is what stops you grabbing something you are standing next to; this raises it so VRChat offers the grab normally.");
			PlayerGrabFollow = cfg.Bind("Fun", "PlayerGrabFollow", 1f,
				new ConfigDescription("Player Grab: how tightly a held player follows the grabber's hand (1 = exactly, lower = laggier, higher = snappier).", new AcceptableValueRange<float>(0.2f, 3f)));
			PlayerGrabThrowForce = cfg.Bind("Fun", "PlayerGrabThrowForce", 3f,
				new ConfigDescription("Player Grab: throw strength - multiplies the hand's velocity when the grabber lets go (0 = just drop them).", new AcceptableValueRange<float>(0f, 8f)));
			PlayerGrabInertia = cfg.Bind("Fun", "PlayerGrabInertia", 0.8f,
				new ConfigDescription("Player Grab: seconds a thrown player keeps their horizontal momentum before ground friction takes over (0 = none).", new AcceptableValueRange<float>(0f, 4f)));
			PlayerGrabReach = cfg.Bind("Fun", "PlayerGrabReach", 0.35f,
				new ConfigDescription("Player Grab (VR): how close your hand must be to any bone of the other player for a grip to grab them, in metres.", new AcceptableValueRange<float>(0.1f, 1.5f)));
			PlayerGrabAimRange = cfg.Bind("Fun", "PlayerGrabAimRange", 12f,
				new ConfigDescription("Player Grab (desktop): how far GRAB PLAYER YOU AIM AT reaches, in metres.", new AcceptableValueRange<float>(2f, 40f)));
			PlayerGrabAllowEscape = cfg.Bind("Fun", "PlayerGrabAllowEscape", true,
				"Player Grab: a held player breaks free by moving (WASD / stick). Keep ON - it is the held player's way out.");
			MarkArtSize = cfg.Bind("Mark", "MarkArtSize", 0f,
				new ConfigDescription("MARK & ART: width of the object Bad Apple picture / the shapes, in metres. 0 = AUTO (objects at their natural size, edge to edge). Bigger = more space between the objects; the objects themselves are never resized. This is the only setting - object count and range are always unlimited, and the two Bad Apple buttons choose who sees it: LOCAL (only you, everything unlimited) or VRCHAT NETWORK (everyone; the grid and the move rate are chosen automatically to stay under VRChat's network limit).", new AcceptableValueRange<float>(0f, 200f)));
			ForcePickupAllowTheft = cfg.Bind("Fun", "ForcePickupAllowTheft", false,
				"Also clear DisallowTheft, which lets you take a pickup somebody else is holding. OFF by default: pulling an object out of another player's hands is the one part of this that affects them rather than you.");
			ForceGrabAnyObject = cfg.Bind("Fun", "ForceGrabAnyObject", true,
				"Force Grab also takes objects a world locked or never made pickups \u2014 a prop bolted in place, "
				+ "a mesh with no VRC_Pickup. That path is a LOCAL carry: it moves the object on your own screen "
				+ "only, takes no ownership and sends nothing, so it cannot be taken from anyone or change world "
				+ "state. Off restores the old behaviour (real pickups only).");

			// ESP — FOUR INDEPENDENT GLOWS AND TWO KNOBS. Each switch turns one thing on or off and
			// nothing else; there is no master switch. All of them use VRChat's own highlight effect
			// (HighlightsFX, the glow the game puts on a grabbable), so nothing is painted over the
			// screen: the glow is real geometry that turns and occludes like the thing it marks.
			// The 2D screen ESP (box / name / distance / skeleton drawn on screen). Back by the owner's
			// request 2026-09-04 - "la box est vraiment utile" - as an independent switch beside the glows.
			EspEnabled = cfg.Bind("ESP", "Enabled", false,
				"2D screen ESP: a box around each remote player, coloured by trust rank, with the name and distance below. Independent toggle (the glows below have their own).");
			Core.ConfigWatch.Watch(EspEnabled);
			EspBox = cfg.Bind("ESP", "Box", true, "2D screen ESP: draw the player box.");
			EspSkeleton = cfg.Bind("ESP", "Skeleton", false,
				"2D screen ESP: draw a bone-to-bone skeleton over players (humanoid avatars only) instead of just the flat box.");
			EspName = cfg.Bind("ESP", "Name", true, "2D screen ESP: show the player's display name under the box.");
			EspDistance = cfg.Bind("ESP", "Distance", true, "2D screen ESP: show the distance in metres under the box.");
			EspCapsule = cfg.Bind("ESP", "Capsule", false,
				"Glowing 3D capsule around each player, in their trust colour. Independent toggle.");
			Core.ConfigWatch.Watch(EspCapsule);
			EspHighlight = cfg.Bind("ESP", "Highlight", false,
				"Glow along the outline of each player's actual avatar mesh, in their trust colour. Independent toggle.");
			Core.ConfigWatch.Watch(EspHighlight);
			EspPortals = cfg.Bind("ESP", "Portals", false,
				"Glow on every open portal in the world. Independent toggle; no label is drawn.");
			Core.ConfigWatch.Watch(EspPortals);
			EspItems = cfg.Bind("ESP", "Items", false,
				"Glow on every grabbable pickup in the world. Independent toggle; no label is drawn.");
			Core.ConfigWatch.Watch(EspItems);
			EspThroughWalls = cfg.Bind("ESP", "ThroughWalls", true,
				"Player capsules are drawn over the world so walls never hide them. Off: capsules sit in the scene and occlude each other, which keeps colours separate in a crowd. Only affects the capsule.");
			Core.ConfigWatch.Watch(EspThroughWalls);
			EspHideFromCamera = cfg.Bind("ESP", "HideFromCamera", false,
				"Keep the ESP out of your own camera, stream and mirrors: the player capsules are drawn by your view only, and the glow effect is switched off on every other camera (photo camera, stream camera, mirror cameras). The 2D screen box never enters a camera anyway.");
			EspMaxDistance = cfg.Bind("ESP", "MaxDistance", 0f,
				"How far away a player still gets a capsule, in metres. 0 = unlimited. Only affects the capsule.");
			EspShowSelf = cfg.Bind("ESP", "ShowSelf", true,
				"Render ESP on your own avatar/capsule when alone in the instance or in third person view.");
			Core.ConfigWatch.Watch(EspShowSelf);
			NameplateEsp = cfg.Bind("ESP", "NameplateEsp", false,
				"Render player nameplates through walls (ESP mode). Off: nameplates are occluded by world geometry normally.");
			Core.ConfigWatch.Watch(NameplateEsp);
		}
	}
}
