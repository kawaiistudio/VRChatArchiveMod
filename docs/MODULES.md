# Module catalogue

Two code bases, one feature list. The C# catalogue below was written when `src/Modules/` held 63 files;
it now holds 88, so the modules added since (`TrueViewModule`, `PhotonGuardModule`, `NsfwFilterModule`,
`VoiceMimicModule`, `ChatMimicModule`, `FastSyncModule`, `PlayerRotatorModule`, `SignatureSoundModule`,
`PlayerStatesModule`, `ActionMenuModule`, `MainMenuTabModule`, `RuntimeEditorModule`, and others) are
described only by the header comment in their own file. Bringing this page back in step with
`src/Modules/` is a welcome first contribution.

Contents — [The C# mod](#the-c-mod-src) · [The C++ engine](#the-c-engine-cpp)

---

## The C# mod (`src/`)

### Core

Foundation modules — the plugin entry, lifecycle, configuration, IL2CPP safety, shared UI toolkits, and the control channel to the desktop client.

- **VRChatArchiveModPlugin** — BepInEx 6 IL2CPP entry point; initializes config, installs the field-offset/nested-type fixes, registers all modules, and injects the runtime driver GameObject.
- **IModule** — abstract lifecycle contract (initialize, UI-ready, update/late/fixed, GUI, scene-loaded, shutdown) every feature implements.
- **ModuleManager** — central registry and per-frame dispatcher, with per-call exception guards, rate-limited error logging, and an optional per-module `Stopwatch` profiler.
- **ModRunner** — IL2CPP-injected `MonoBehaviour` on a persistent GameObject that pumps the module update loop and re-asserts cursor state.
- **ModConfig** — central BepInEx-backed configuration binding and documenting every setting (anti-crash thresholds, ESP/radar, movement, tags, favourites, UI, etc.).
- **NativeGuard** — `VirtualQuery`-checks a native/IL2CPP pointer is mapped and readable before dereference, preventing uncatchable access-violation crashes on dead proxies.
- **FieldOffsetFix** — finds the correct `FieldInfo` offset slot (verified against four `System.Delegate` fields) and Harmony-patches `il2cpp_field_get_offset`.
- **NestedTypeFix** — patches nested-type resolution to walk the `Il2CppClass` struct directly instead of calling VRChat's renamed export.
- **Il2CppDelegates** — single gate in front of Il2CppInterop's crash-prone `ConvertDelegate`, returning null when the bridge is unsafe so dependent features degrade to "missing".
- **UiClick** — funnels all uGUI button/slider listener wiring through one guarded delegate-conversion point, refusing to wire when the delegate bridge is unavailable.
- **PlayerRef** — reflection-based, aggressively cached access to the local player `Component`, `VRCPlayerApi`, and its velocity setter, guarded against dead proxies.
- **ApiUsers** — cached resolver mapping a `VRCPlayerApi` to its `APIUser` (and VRC+/18+/platform badges), cached per player for one second to avoid per-frame reflection cost.
- **QuickMenu** — caches VRChat's QuickMenu and Main Menu canvases so modules find them once instead of scanning every `Transform` each frame.
- **TrustKit** — single source of truth for VRChat trust-rank colours (plus the rainbow signature-user override), shared by ESP, panels, and the PLAYERS tab.
- **FeatureHealth** — thread-safe registry where a module reports its live state (ok/idle/broken) keyed by setting id, serialized into the client sync so a switch can explain why it appears to do nothing.
- **ConfigWatch** — subscribes to BepInEx `SettingChanged` and logs who changed a config value (client, menu/code, or unknown) and when.
- **Redact** — strips secrets (key/password/token/session/licence args and long base64 blobs) out of logs and diagnostics before a user shares them.
- **Unwrap** — unwraps nested `TargetInvocationException`/type-initializer chains to report the real innermost cause and its originating frame.
- **AssetLoader** — loads and caches the mod's branding/icon images (embedded DLL resources) into `Texture2D` for the IMGUI menu, degrading to null on a bad resource.
- **WavAudio** — minimal 16-bit PCM WAV → Unity `AudioClip` decoder shared by loading-screen music and the spawn stinger.
- **GuiKit** — immediate-mode (IMGUI) drawing kit (rounded panels, glows, toggles, sliders, segmented controls) that renders the mod's overlay without touching VRChat's UI.
- **Hud** — shared HUD chrome (palette, resolution-based scaling, panel/header/accent-bar drawing) so every corner HUD renders consistently.
- **MenuCard** — recipe for turning a cloned VRChat menu card into a mod button/toggle: strip foreign root components, rewire the click, paint aura/rim/label, lay out icon and label.
- **Menu** — full IMGUI mod menu (draggable window, tab rail, stat cards, toggles/sliders/credits), now permanently sealed since its pages moved to the desktop client, while still owning cursor capture, the Alt free-cursor toggle, and the "world scripts blocked" warning.
- **Overlay** — declarative IMGUI menu system where a page is a list of foldouts and widgets each bound directly to a `ConfigEntry`, drawn per frame with no GameObjects.
- **MenuExclusiveModule** — makes the mod's IMGUI menu and VRChat's QuickMenu mutually exclusive, deactivating the QuickMenu canvas while the mod menu is open and restoring it exactly on close.
- **VaAuth** — resolves how VA tag/favourite/soundboard writes authenticate (desktop-client loopback bridge, in-mod login, or none) and performs those authenticated HTTP calls.
- **ModControlModule** — the polling HTTP control channel: the mod polls the client's LocalBridge on `127.0.0.1:8791`, sends its schema/settings/roster/events/Udon-manager payloads, and applies queued commands on the Unity main thread.
- **Unity 6 adaptation layer** — `MissingTypeGuard`, `TokenShiftFix`, `MemberAlign`, `ProxyGuard`, `ObfuscatedClassFinder`, `Live` (`UsableType`), `LiveComponents`, and the managed `Mathf` shim: what keeps the mod alive on interop assemblies frozen on an older build. See [`UNITY6-PORT.md`](UNITY6-PORT.md) §4.

### Favorites & Archive

Unlimited local avatar favourites plus the machinery that grafts a real "VRCHAT ARCHIVE" presence into VRChat's own menus. Because VRChat's newer Voyager world/social pages cannot be injected into, some of these render the mod's own overlay grids instead.

- **FavoritesModule** — maintains unlimited avatar favourites in three layers (ids, metadata, disk-cached thumbnails) through the desktop client's bridge, with optimistic local add/remove.
- **KindFavoritesModule** — generalises the favourites pipeline for worlds, users, and groups via a parameterised provider so the FAVORIS tab shows every type with the same cards.
- **UserFavoritesModule** — holds the user-id list behind the social ARCHIVE FAVORITES, refreshed via the client bridge by shape-agnostically scraping `usr_` ids out of the response.
- **WorldFavoritesModule** — holds the world-id list behind the world ARCHIVE FAVORITES, refreshed the same way by scraping `wrld_` ids.
- **ArchiveCategoryModule** — publishes a real "VRCHAT ARCHIVE" category into VRChat's avatar menu by reassigning the collections panel's observable category list, filling its native grid with Archive favourites as native avatar cards.
- **ArchiveHijackModule** — takes over an existing non-VRC+ category by renaming its sidebar row/header and swapping the grid to Archive favourites, feeding ids VRChat fetches itself (with dead-favourite auto-cleanup).
- **ArchiveFavListModule** — injects a synthetic "VRCHAT ARCHIVE" `FavoriteListModel` into `VRC.Core.API.Favorites._avatars`, populated from the Archive entirely in memory with no network request.
- **ArchiveFavGridModule** — renders Archive world and social favourites as the mod's own grafted overlay grid (cloned sidebar row toggling an overlay of cells), since Voyager pages cannot be injected.
- **ArchiveFavButtonModule** — adds cloned "Save/Remove to Archive" and "Get metadata" buttons to the avatar detail pane, and augments Apply to wear-by-id when the client declines cloning.
- **ArchiveSidebarRowModule** — clones a sidebar item as a fallback "VRCHAT ARCHIVE" row opening the mod's FAVORITES tab, removing itself once the native category is published.
- **WorldFavListModule** — defines the `FavListInjector` base plus World/User/Avatar subclasses that borrow a real `FavoriteListModel` by sidebar slot, rename it, and fill it with Archive ids.

### ESP & Visualization

Local, screen-only overlays for reading a room — player highlights, a radar, instance panels, and the mod's menu theming. None of it is visible to anyone else.

- **EspModule** — screen-space player ESP drawing a trust-rank-coloured box, bone skeleton, name, and distance via IMGUI, with frustum culling and per-frame caching.
- **CapsuleEspModule** — draws a glowing 3D capsule around each remote player by cloning VRChat's own SelectRegion mesh (or a scaled primitive fallback) into a transparent object fed to VRChat's `HighlightsFX`, tinted by trust rank.
- **HighlightEspModule** — glows the real geometry of remote players, portals, and pickups using VRChat's own `HighlightsFX` post-effect, resolved and driven entirely by reflection with per-feature self-gating.
- **RadarModule** — corner minimap plotting a trust-rank-coloured dot per nearby player oriented to the local view (forward = up), optionally over the world map with names and an X/Y/Z readout; toggled with Right-Shift+M.
- **RadarMapCamera** — static helper maintaining a disabled, hand-rendered top-down orthographic camera (world geometry only) drawing into a `RenderTexture` for the radar's map background.
- **InstancePanelsModule** — two anchored overlay panels showing the live instance roster (left) and a join/leave/avatar-change/video-URL feed (right) diffed from the shared roster, with optional join-notifier toasts.
- **WingPlayersModule** — appends a live player-roster section into VRChat's native left QuickMenu wing by cloning, stripping, and pooling a styled row per player, each clickable to open that player.
- **MenuSkinModule** — replaces the QuickMenu wallpaper with the Archive image (both crossfade halves) and optionally hides VRChat's translucent veil layers so the picture shows.
- **MenuThemeModule** — recolours the QuickMenu and MainMenu into the Archive's gradient with a throttled timer, adds an inner-glow rim to cards, and restores VRChat's original colours on toggle-off.
- **OverlayMenuModule** — declares the mod's IMGUI overlay-menu pages (Movement, Visuals, Status) once at startup with controls bound directly to their `ConfigEntry`s; toggled with Right-Shift+M.
- **ProfilerHudModule** — top-right HUD listing per-module frame cost (ms/s) alongside fps and worst-frame time, enabling measurement only while shown; toggled with Right-Shift+P.

### Movement & Fun

Self-only locomotion and novelty features. Every movement tool moves *you* through VRChat's own sanctioned player APIs, restoring captured original values when switched off; the fun tools play locally and send nothing to other players (unless a feature explicitly offers a global path).

- **MovementModule** — local desktop fly plus noclip with click-teleport and arrow-key rotation, pinning the player transform each frame and disabling the local player's solid colliders.
- **SpeedModule** — overrides local walk/run/strafe/jump locomotion through VRChat's per-player setters, re-applying ten times a second and restoring captured originals on off.
- **GravityModule** — reversibly disables gravity for yourself (`SetGravityStrength`) and/or the whole world (`Physics.gravity`), re-asserting after respawns and restoring on shutdown, client-side only.
- **ForceJumpModule** — one-shot upward-velocity launch (RightShift+J or a client-supplied force) through `VRCPlayerApi.SetVelocity`, preserving horizontal momentum.
- **ForceGrabModule** — raycasts at a `VRC_Pickup` (or any locked/non-pickup object when enabled) under the crosshair, takes SDK ownership, carries it in front of the camera, and forwards Udon pickup/use events — never grabbing anything held by another player.
- **OrbitModule** — circles the local player around or perches them on top of another player each frame via `TeleportTo`/transform writes (and hotkeys the object-orbit gag), moving only yourself.
- **ObjectOrbitModule** — pulls loose world props into a spinning ring around you or another player, locally by default or synced via `VRC_Pickup` ownership, restoring every object's original state on stop/world change/shutdown.
- **SoundboardModule** — networked soundboard that broadcasts only a *key* to a server feed every mod client polls, then plays the matching embedded WAV locally with the listener's own volume/master toggle and display-name attribution.
- **SpawnSoundModule** — plays an embedded "spawn stinger" WAV once locally each time you finish loading into an instance, as a 2D `AudioSource` that sends nothing.
- **VideoModule** — finds the world's Udon video players and points one at a chosen URL on *your* client only (validated through VRChat's `TryCreateAllowlistedVRCUrl`), never taking ownership or syncing.
- **VideoUrlModule** — injects a URL into the world's video players and plays it *synced* to the whole instance, driving `BaseVRCVideoPlayer.LoadURL` plus the Udon `VRCUrl` variable and play events, taking ownership only on genuinely networked objects.
- **BadAppleModule** — plays *Bad Apple!!* in the VRChat chatbox as shadow-art over OSC (`/chatbox/input`) from DLL-embedded pre-baked frame data, on a background thread with configurable cadence. The same baked frames drive `MarkModule`'s object show, where the world's own pickups become the pixels — [see it running](https://youtu.be/gdFYaCBWtog).

### Udon Tools & Anti-Crash

Passive Udon observation, per-client Udon control, and defensive anti-crash and avatar-restoration features. The network-log and Udon-log tools only observe; the anti-crash and TrueView tools only affect what *you* see and never touch anything networked.

- **AntiCrashModule** — throttled-polls loaded avatars via `VRCAvatarDescriptor` and neutralises crasher-tier components (excess particle systems, lights, audio sources, cloth, PhysBones, contacts) above configurable thresholds set far over VRChat's own ratings; every clamp is journaled and reversed when the toggle or the master goes off.
- **TrueViewModule** — keeps a remote player looking like themselves when VRChat replaces their avatar with the skeleton-less fallback: the fallback object is suppressed, a copy of their real avatar cached while it was healthy is shown in its place, and the real one is re-requested when nothing was cached. Nameplate height, the laser hitbox, local audio and animator culling are restored alongside, each behind its own switch. Patches nothing, touches nothing networked, and every change it makes is written to a ledger that is replayed in reverse on disable, scene change or shutdown.
- **UdonLogModule** — Harmony-hooks `UdonBehaviour.RunProgram(string)` to passively log all nearby Udon events in a live ring-buffer console with adaptive flood muting, plus an optional off-by-default client-side crasher guard whose prefix can skip abusive/flooding events on your own client only.
- **UdonManagerModule** — switches a world's Udon behaviours on/off one at a time on your own client, inspects each behaviour's entry points and public variables, edits variables, and runs events locally (with a separate, deliberately explicit global broadcast path), remembering exactly what it disabled so RESTORE undoes only its own changes.
- **NetworkLogModule** — a single receive-side Harmony postfix on `VRCNetworkingClient.OnEvent` that passively records inbound Photon events (code, sender, sampled payload shape) into a console and optional file, snapshotting values immediately and never transmitting, raising, or replaying anything.
- **PhotonGuardModule** — observed-attack mitigations on the receive side (rate-classed event codes), best-effort and local.
- **WatchlistModule** — flags configured VRChat user ids with an animated rainbow ESP box and join banner, and independently announces arriving VRChat Archive members with a gradient banner — purely local visualization/alerts built on data the client already receives.

### Tags & Players

Community player-tagging systems and native QuickMenu integration for acting on the player you have selected.

- **VaTagsModule** — the VRChatArchive community player-tagging system: keeps a live instance roster, syncs styled tags keyed by VRChat user id from the site's authenticated `/api/va-tags` endpoint, draws animated nameplate plates, and powers the PLAYERS tab (teleport, clone/wear avatar, add/remove tags). *Nameplate plates have nothing to attach to on build 25686233 — see UNITY6-PORT.md.*
- **FewTagsModule** — downloads the community FewTags user-tag database and renders each tagged user's tags as plates cloned from the game's own nameplate popup, filtering slurs locally before drawing. *(Derived from Fewdys's FewTags — see the README's Credits. Same nameplate caveat.)*
- **UserMenuModule** — adds "Mod Features", "Clone Avatar", and "Copy Avatar Id" cards to VRChat's per-user QuickMenu page, plus an IMGUI submenu (orbit/sit/ring), all acting on the currently selected user.
- **QuickMenuTabModule** — re-purposes VRChat's unused DevTools tab into a native "VRChat Archive" QuickMenu tab with cloned, Launchpad-styled button tiles, navigable sub-pages, and hand-built native-style sliders.
- **QuickMenuConsoleModule** — clones VRChat's FPS/ping debug panel into the Launchpad page to render a themed, native-looking Udon-events console.

### Dev & Probe Tooling

Read-only instrumentation used to reverse-engineer VRChat's obfuscated structures and diagnose issues. These probes create, modify, or raise nothing in-game — they observe and dump. Also included are the offline Python tools used to bake the chatbox art assets.

- **DevToolsModule** — dev instrument providing a worst-frame spike hunter, a live searchable/editable `ModConfig` editor, and an IL2CPP type/member finder, all read-only toward the game.
- **DiagnosticsModule** — writes a compact, size-capped, credential-redacted diagnostics report (machine/settings header, the plugin's own log lines, Unity errors, health snapshots, explicit PROBLEM lines) small enough for a tester to send back.
- **CaptureModule** — on-demand, strictly read-only dumper that writes rich snapshots of live VRChat objects (menu tree, loading popup, audio, Udon programs/entry points, favourites, instance/player metadata, UI API objects) to disk, sending nothing.
- **DelegateProbe** — read-only diagnostic dumping `System.Delegate`'s `FieldInfo` words to compare Il2CppInterop's reported offset against IL2CPP's real one, so a crash can be attributed without writing anything.
- **AutoProbe** — read-only, stopwatch-budgeted reflection probe that gathers the live `FavoriteArea` data layer and the real network-callable Udon event surface in one bounded run.
- **AvatarListProbe** — read-only runtime probe dumping the avatar-menu content sections, item shapes, `DataModel<ApiAvatar>` candidates, the live `FavoriteArea`, and the menu tree so injection code can target VRChat's real (obfuscated) structures.
- **MenuPanelProbe** — read-only, budget-limited probe dumping the live content panels/grids/lists of whatever menu is open, resolving obfuscated component types to find injection anchors.
- **MenuCaptureModule** — automatically and continuously records the structure and cell templates of interesting menu pages as you open them, bounded (node/depth/file caps) and read-only.
- **MenuRecorderModule** — off-by-default module that continuously walks the live menu canvases while browsing and writes each newly-seen active object (path, real component types, rect, TMP label, sprite) exactly once.
- **UiProbeModule** — on-demand (F8) read-only UI-tree dumper listing interactable controls and full canvas trees to locate native VRChat (Voyager) buttons/tabs to clone, plus an F9 panel-probe hotkey.
- **LogCaptureModule** — passive Unity log sink mirroring every game/plugin log line (redacted) to rotated files plus a small filtered menu-only companion log, hooking nothing.
- **AudioWatchModule** — purely observational recorder polling `AudioSource`s for a rising `isPlaying` edge and logging which clips actually played, to identify VRChat's real UI click sound, never playing/stopping/muting.
- **RuntimeEditorModule / RuntimeEditorHost** — hosts RuntimeUnityEditor (ManlyMarco, GPL-3.0; loaded on demand from an embedded gzipped copy that is not in this repository) as an in-game inspector. See NOTICE.
- **tools/bake_badapple.py** — CLI tool sampling a video into a compact base-36 text-frame grid sized to the measured chatbox constraints.
- **tools/chatbox_probe.py** — CLI tool sending OSC measurement patterns and baked frames to VRChat's chatbox input (UDP `127.0.0.1:9000`) to size the bubble and test cadence.
- **tools/pick_charset.py** — CLI tool building a grayscale glyph ramp by measuring each candidate glyph's ink coverage in a CJK font.

---

## The C++ engine (`cpp/`)

Thirty-five modules are registered in `cpp/native/src/dllmain.cpp` (plus a one-second heartbeat that
proves the frame pump is alive). Each is the native counterpart of the C# module of the same or similar
name; where a C# description above applies, it applies here. **Only the items marked ✔ have been
verified in-game on build 25686233**; everything else is ported and awaiting a tester.

| Module | Counterpart / purpose | |
|---|---|---|
| `QuickMenuModule` | the QuickMenu integration — the mod's cards in VRChat's own menu (21 cards) | ✔ |
| `WingsModule` | the PLAYERS / LOG panels on the QuickMenu wings (`WingPlayersModule`, `WingLogModule`); faithful port of the C# `PanelSkin` in progress | |
| `ClickTpModule` | click-to-teleport | |
| `VaTagsModule` | VRChat Archive player tags; drawn in the overlay since nameplates are GPU-instanced | |
| `BridgeModule` | the client bridge — HTTP client of `127.0.0.1:8791`, schema + sync from the config registry | ✔ transport |
| `MovementModule`, `FlyModule` | desktop movement and fly, through the recovered local player | ✔ fly |
| `SelfHideModule` | hide your own avatar locally | |
| `GravityModule` | self / world gravity, reversible | |
| `GhostModule` | hold back the local player's network serializer (others see you frozen) | |
| `FastSyncModule` | the `RequireFastRate` field on the serializer | |
| `AntiCrashModule` | avatar component clamps, journaled | |
| `OrbitModule`, `ObjectOrbitModule`, `RotatorModule`, `ElevatorModule`, `FloatObjectsModule` | self-only movement gags and prop manipulation (`OrbitModule`, `ObjectOrbitModule`, `PlayerRotatorModule`) | |
| `AvatarModule` | avatar actions for the bridge (wear / clone by id, dump) | |
| `MarkModule`, `BadAppleModule` | the object show and the chatbox show | |
| `MimicModule` | voice / chat mimic (`VoiceMimicModule`, `ChatMimicModule`) | |
| `PortalModule` | portal handling | |
| `BackgroundsModule` | `VrcPlusBackgroundsModule` | |
| `VideoUrlModule` | synced video URL injection | |
| `BlockedByModule` | `BlockedByProbeModule` — who blocked me, asked once | |
| `BoxDropModule` | the Box Drop gag — **seam gone on this build**, kept for the next one | |
| `SoundboardModule` | the networked soundboard | |
| `PhotonModule` | receive-side Photon observation and guard (`NetworkLogModule`, `PhotonGuardModule`) | |
| `ForcePickupModule`, `ForceGrabModule` | pickup state and force-grab | |
| `UdonModule` | the Udon manager and log (`UdonManagerModule`, `UdonLogModule`) — `_program` is a field here | |
| `EspModule`, `GlowEspModule` | box ESP and `HighlightsFX` glow (`EspModule`, `HighlightEspModule`) | |
| `UiDumpModule` | read-only Unity tree / named-object dumper (how a renamed class is found by where it is attached) | |
| `TrueViewModule` | `TrueViewModule` | |

Shared infrastructure (not modules): `il2cpp.cpp` (exports, classes, methods, fields), `recovery.cpp`
(classes recovered by structure), `hooks.cpp` (dobby), `engine.cpp` (pump), `config.cpp` (registry),
`player.cpp` (local player and roster), `world.cpp` (world/instance from the game's log), `http.cpp`,
`screenui.cpp` (overlay text), `feed.cpp`, `log.cpp`, `obf_selftest.cpp`.
