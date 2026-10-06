# Changelog

All notable changes to **VRChat Archive Mod** are recorded here.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this project
aims to follow [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## Scope of this file, and what it cannot tell you

The public repository was opened at **3.5.0**. Everything before that — and every version between 3.5.0
and 3.9.20 — was developed in a private tree, and none of it left a commit, a tag or a release here.
There are, as of this file, **no git tags and no GitHub releases** in this repository, so there is
nothing to reconstruct an intermediate history from. Rather than invent one, this file documents the two
versions that public history actually evidences, and says plainly where the gap is.

Two consequences worth stating up front:

- Dates below are **commit dates**, not publication dates. No release process produced anything datable.
- The 3.9.20 entry is a **description of the feature surface the public tree contains at that version**,
  not a change-by-change diff against the version immediately before it. Where a genuine 3.5.0 → 3.9.20
  delta is verifiable from the repository itself (files added, files removed, fixes named in the sync
  commit), it is marked as such.

---

## [Unreleased]

### Added — this repository becomes the open-source pack (2026-10-06)

VRChat build 25686233 (2 October 2026) moved the client to Unity 6 and broke the mod. Rather than
keep fighting it in private, the whole effort now lives here, with a call for help in the README:

- `cpp/` — the native (C++) rewrite of the mod: a BepInEx IL2CPP plugin that embeds a native
  engine talking to il2cpp directly, with neither Il2CppInterop nor the struct-layout patcher.
- `tools/pack-port/` — the Unity 6 port kit: the il2cpp export remap for build 25686233, the
  `VRChatStructFix` patcher source, the il2cpp struct-layout notes and the probes used to derive them.
- `docs/UNITY6-PORT.md`, `docs/LOADER-AND-PACK.md`, `docs/MODULES.md` — what Unity 6 broke and how
  far each fix got, how the pack and the loader work, and the module catalogue moved out of the README.
- `src/` is synced with the current private tree (`<Version>` 3.9.347), which loads on Unity 6 but
  with many features broken; the README lists them.

### Removed

- `DexCapture*` (the Dex patcher) and every reference to `AssetBundlePatchModule` (the plaintext-cache
  decryptor). Both are server-gated paid features of the private build and are not part of this
  repository. The call sites are stubbed and marked `[open-source build]`; nothing else depends on them.
- `AntiBlockModule` (replaced by `TrueViewModule` at 3.9.245) and `EraLoadingModule` with its seven era resources, which the current private tree no longer carries.

### Changed — `AntiBlockModule` replaced by `TrueViewModule` (3.9.245)

The old module answered the problem by lying to the game: a Harmony prefix on *every* `bool(string)`
method of `ModerationManager`, forced to return `false`, so VRChat never reached the decision to swap
the avatar. That is one patch standing on a type whose shape is obfuscated afresh on every build — it
patches each matching method without knowing which question that method actually answers, and the
build where the check changes shape is the build where it silently patches the wrong thing.

`TrueViewModule` patches nothing. It watches the player's own hierarchy and reacts to what is there:

- the fallback `ForwardDirection/AvatarProxy` — the skeleton-less robot — is switched back off the
  frame it appears, so it is never what you see;
- while the real avatar is healthy, exactly one inactive clone of it is cached under the player;
- when the real avatar is emptied, that clone is shown in its place, so the player keeps their own
  appearance instead of becoming a robot or vanishing;
- with nothing cached (you arrived after the swap), `VRCAvatarManager` is asked at most once every
  30 s to re-download the real avatar, which brings the bone rig and animation back with it.

Nameplate visibility and head-height placement, the `SelectRegion` laser hitbox, the local USpeak
mute and animator culling are restored alongside, each behind its own config switch under
`Moderation`. On by default.

Two things were deliberately not carried over from the standalone this came from. Its survey ran
`FindGameObjectsWithTag("Player")` plus a `GetComponentsInChildren` sweep per player on *every*
frame; here the survey walks `VRCPlayerApi.AllPlayers` with a round-robin budget so each player is
inspected ~4×/second, and the only per-frame work is one check over the players already known to be
proxied. And every change is written to a ledger keyed by player id *before* it is made — disable,
scene change and shutdown replay that ledger in reverse, so a player who left, or a world that
reloaded, cannot leave a hidden fallback object, a parked nameplate positioner, an orphan clone or a
raised culling mode behind.

The config key is new (`Moderation/TrueView`) rather than inherited: the old `Moderation/AntiBlock`
value was a decision about a different implementation, so an existing setting is not carried over.
`AntiBlock.cs` is kept as `AntiBlock.cs.bak-trueview`.

### Fixed — the favourites grid was built inside a switched-off panel (3.9.140)

`ARCHIVE FAVORITES` took over its sidebar row, renamed the heading, answered the click and reported
`populated 34 cell(s) for 34 favourite(s)` — on an empty screen. The cards were real; the column
they were parented to was not on:

```
[FavGrid:avatar] grille posee sur la colonne
    Panel_MM_DynamicSidePanel/Main/Panel_MM_AvatarLooks/.../Contents
```

Build 1903 parks four complete content panels side by side under `Main` — `Panel_My_Current_Avatar`,
`Panel_MM_AvatarLooks`, `Panel_MM_Accessories`, `Panel_MM_Avatars` — each carrying an identically
named `Panel_MM_ScrollRect/Viewport/VerticalLayoutGroup/Contents`, and leaves exactly **one** active.
Resolving the container by name and taking the first match therefore picked a dormant one.
This is the inactive-prefab-row trap, two levels higher up the tree.

The name search now sweeps for an `activeInHierarchy` candidate first and only falls back to a
dormant one when the page has nothing live at all; `Show()` additionally *proves* the overlay is on
screen before claiming to have shown it, and rebuilds against the live column if VRChat has since
switched pages. The log line now states the outcome (`[vivante]` / `[DORMANTE]`) rather than the
intent — a log that says "placed" about a dead object is a log that lies.

### Fixed — the menu theme cost 425 ms/s and dropped the game to 8 fps (3.9.140)

```
8 fps — mod costs 518.8 ms/s [menu OPEN, 0 player(s)]
Worst: MenuTheme=425.4ms  MenuSkin=80.4ms  ArchiveFavGrid=1.6ms
```

The repaint, not the features. `MenuThemeModule` re-asserted its colours across **every** themed
text and card three times a second; each element costs several il2cpp crossings (`TMP_Text.color`,
`Selectable.colors` returning a `ColorBlock`, and Unity's null check, itself a native call), and
`Canvas_MainMenu` holds thousands of texts.

Nothing on screen needed that rate: VRChat restyles a page when it *shows* it, and a page is shown
because something was pressed. `Button.Press` is already hooked for every button in the game, so
`UiClick.LastPressAt` is now stamped there — before the early-out, since the press we do not handle
is exactly the one that changes the page. The full sweep runs for 1.25 s after a press (and after a
rescan); otherwise a bounded 192-item slice per canvas advances a rolling cursor.

Three smaller cuts in the same pass: `TextCol()` was parsing a hex string **once per text** and is
now hoisted; `IsEditable` and `IsOurButton` walked four ancestors calling `GetComponent` twice at
each — eight interop calls per text on every scan — for an answer that cannot change, and are now
skipped for any text already in `_seen`.

### Fixed — the inspection socket reported a timeout it had caused (3.9.140)

A scene-wide query runs on the frame, and the frame was 125 ms with the menu open, so the walk
outlived its 3 s deadline and the socket answered `le thread principal n'a pas repondu` about a
main thread that was answering, slowly. Raised to 20 s: this is a loopback diagnostic socket, off
by default, where waiting is free and a wrong answer is not.

### Fixed — two access violations that ended the process (3.9.117, 3.9.119)

Opening the ARCHIVE FAVORITE category killed VRChat outright. The trace named it:

```
System.AccessViolationException
  at Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke
  at Il2CppSystem.Collections.Generic.List`1.get_Item(Int32)
  at ArchiveHijackModule.PickTarget()
```

`live[i]` on an il2cpp `List<T>` compiles to `get_Item`, and on 1903 that method is **mis-bound** — the
mod says so at startup and nobody had read it as a crash warning: *"le token 0x060035A6 tombe sur une
methode de forme 'o5' au lieu de 'g5'"*. `'g5'` is "returns the generic parameter, takes an int", which
is `get_Item`; invoking it jumps into the wrong method. Those `MemberAlign … appel refuse` lines are
future crashes, not noise.

`Il2CppSeq.Items` replaces the indexer by reading the list's `_items`/`_size` **fields** — offset reads
repaired by FieldOffsetFix, never a `runtime_invoke`. The first version then crashed in
`il2cpp_array_length` because `_items` itself came back pointing at memory that is not a live array, and
a try/catch cannot catch a native access violation. Every pointer is now proven with
`NativeGuard.IsLiveObject` before it is dereferenced: the list, the backing array, then each element. A
bad pointer yields an empty list; it can no longer end the process. Applied to all four indexing sites
(`ArchiveHijackModule.PickTarget`, `ArchiveCategoryModule` ×3).

### Fixed — the mod accused everyone in the room of blocking you (3.9.121)

The `[B]` marker fired on every remote player at once. It reads `prop_Boolean_17` — "the 17th boolean",
a **1886 index**. The obfuscator reorders members, so on 1903 that name lands on a different boolean,
one that is true for any loaded player. This is not a cosmetic bug: the mod was stating a falsehood
about real people.

Two guards. A trustworthiness gate (the flag is only read when the class's members can actually be
placed), and — the one that matters, because it is build-agnostic — a plausibility gate: **"everyone
blocked you" is never true**. When the flag marks every remote player present (≥2), it is the flag that
is wrong, not the room; the marker disarms for the session and shows nothing. A real block is rare and
individual and cannot trip it.

### Fixed — MenuTheme burned 700 ms/s of every second (3.9.118)

With the menu open the profiler read `MenuTheme=700.4ms`. The "nothing found yet, keep looking" clause
tested only the IMAGE target lists, but themed TEXT goes into `_live` — so on a canvas with text and no
themed backgrounds both image lists read 0 while `_live` was full, and a complete
`GetComponentsInChildren` sweep of `Canvas_MainMenu` ran **three times a second**. The emptiness test
now counts text, and the bootstrap rescan is rate-limited instead of firing every tick. Steady state
with the menu open is now ~25 ms/s.

`ArchiveFavButtonModule` was separately caught at 990 ms/s: its lookup used `GameObject.Find`, a
by-name sweep of every active object in the game, and its back-off was armed *before* the pass rather
than after — so a pass as long as its own wait ran at 100% duty. It now descends from the cached menu
root and re-arms in a `finally`.

### Fixed — nameplate tags, by the name written on the plate (3.9.116)

Two earlier approaches failed and both looked right. The plate's own fields: 207 references indexed off
the live containers, not one of them the player. Its position: every container reads `(0,0,0)`, which
was taken as proof that VRChat draws no nameplate for the local player — until a screenshot showed the
plate, drawn, with the owner's name on it. The container is laid out inside a canvas and placed at
render time, so `Transform.position` says nothing about where it appears.

Plates are now matched by the **display name they show**, compared against the raw text and against the
text with rich-text tags stripped (VRChat colours the name by trust rank). Two plates with the same name
are refused rather than tagging the wrong player.

### Fixed — VRCPlayer no longer carries the nameplate container (3.9.111)

`FindNameplateContainer` fell through a chain that reads every proxy property and *invokes* every
parameterless `GameObject` getter across 271 methods, then walks the whole field table — every two
seconds, per player. The field dump proves the container is not on VRCPlayer for anyone on this build,
so a failure on one player can never become a success on the next. Three complete failures now retire
the chain for the session: `VaTags` went from **938.8 ms/s to 21.4 ms/s**.

`CurrentInstanceOwnerId()` is memoised — third instance of "expensive failing lookup retried every
frame", after `LocalUserId()` and the nameplate container.

### Fixed — features that were dark because of the il2cpp delegate bridge (3.9.115, 3.9.116)

The instance log's video URLs, the diagnostics report's Unity errors and the world-favourites watchdog
all asked for an `Application.LogCallback`, and `ConvertDelegate` ends the process on 1903. `Core.UnityLog`
hooks `Application.CallLogCallback` — the static method Unity's native logger calls for every line — so
no delegate is created at all. One hook, shared. Harmony binds injected parameters by NAME and an
Il2CppInterop proxy does not keep Unity's, so the postfix takes `__0/__1/__2` positionally.

### Added — RuntimeUnityEditor, hosted by the mod (3.9.113)

RUE's own BepInEx loader does one thing the mod cannot survive: `AddComponent<RuntimeUnityEditorHelper>()`,
i.e. `ClassInjector`, which ends the process on 1903. It is not shipped. The core is constructed directly
— the path RUE itself recommends — and driven from `IModule`'s `OnUpdate`/`OnLateUpdate`/`OnGui` through
FramePump's Harmony anchors, so no type is injected anywhere. The assembly rides inside the mod as a
gzipped embedded resource (+680 KB) and is served from memory, and a crash marker written before the
constructor disarms it for the session if a boot ever kills the game. `F12`.

### Changed — silent failures now say what they saw

A miss that says nothing is a miss nobody can fix, and this build was full of them. `ArchiveFavButtonModule`
returned on a null panel without a word; the answer, once it spoke, was that VRChat renamed the pane to
**`Panel_AvatarDetailsCompact`** on 1903. The category retitle searched two paths that no longer exist and
failed inside an empty `catch`; it now renames by the label's **text**, which cannot rot. `ArchiveHijack`'s
`PickTarget` returned false from three branches in silence. `FindPanel` cached the first scene-valid panel
even when it held no categories.

`BuildRow` cloned the **inactive prefab template** rather than a live row, because
`GetComponentsInChildren(true)` walks inactive objects — so the row was created in the template's container
and never appeared, while the log honestly reported it added. Donors must now be `activeInHierarchy`, and
both the row and grid logs name the container path: "built" is true even when it is built in the wrong place.

### Changed — Archive avatar favourites get their own grid (3.9.123)

`ArchiveHijackModule` cannot borrow VRChat's category on 1903: the obfuscated panel TYPE it targets was
reassigned, so `Il2CppType.Of<Panel>()` resolves a stranger whose list reads empty, and re-deriving
Panel/Category/Section by shape would rot again next build. `ArchiveFavGridModule` — the own-grid path
already proven for worlds and social — is re-armed for **avatars only**, fed straight from the 34
favourites the bridge already holds. Cards are built from scratch and a click wears the avatar through
`VaTagsModule.WearById`, wired by `UiClick`. No obfuscated VRChat type is touched.


### Fixed — the mod cost 938 ms/s looking for a nameplate that is not there (3.9.111)

`FindNameplateContainer` looks for the plate's host **on VRCPlayer**: every proxy property read, every
parameterless `GameObject` getter *invoked* across 271 methods, then the whole field table walked. On
1903 the field dump proves the container is not on VRCPlayer for anyone — it moved under the global
`NameplateManager` — so a failure on one player can never become a success on the next. The miss was
nevertheless retried every two seconds, per player: with the menu open the profiler read
`VaTags=938.8ms` and VRChat ran at 0 fps. Three complete failures now retire the chain for the session;
the manager path stays, and a build that still carries the field returns before reaching it.

`VaTagsModule.CurrentInstanceOwnerId()` is memoised (5 s hit / 1 s miss). It reads a property off the
recovered `RoomManager` and then a member by name off `ApiWorldInstance`, and the roster called it on
every pass — which is the whole of the *254 ms roster pass with zero players in it*. Third instance of
the same defect, after `LocalUserId()` and the nameplate container.

### Changed — two silent failures now say what they found

`ArchiveFavButtonModule.Build()` returned on a null `Avatar_Marketplace_Panel` without a word, so a
whole session of opening the avatar pane produced not one `[ArchiveFavBtn]` line and "the button never
appears" was indistinguishable from "VRChat renamed the panel". It now lists the active
`Avatar*Panel`/`CTA`/`Marketplace` objects, once every 30 s, only while the main menu is open and the
row is missing.

When every nameplate container sits on the origin, the log says so in plain words instead of printing
distances: VRChat has drawn no nameplate, and it never draws one for the local player, so tags have
nowhere to go until somebody else is in the instance.

### Fixed — VRChat build 1903

The interop assemblies this mod is built against were generated from VRChat build **1886**. On **1903**
almost nothing reached what it was aiming at: a proxy binds each method by metadata token and each field
by name, and the new build shifted the tokens and re-randomised VRChat's own class and member names. The
console carried **359 `Field … was not found` errors**, fourteen types were reported missing, and every
feature built on them was dark.

The repair does not regenerate the interop; it re-identifies the members at runtime.

- **Member shapes.** `vrchat-pack-tools/tokmap --table` now records, for every type of the 1886 interop,
  the shape of each member in declaration order — arity, staticness, the kind of every parameter, and
  the names of the types an obfuscator cannot rename. `Core/MemberAlign` reads the same sequence off the
  live class and lines the two up. **544 methods and 137 fields** are placed this way.
- **Classes are identified by their members, not by counting them.** Two numbers agreeing is weak
  evidence: HighlightsFX was recovered as an unrelated class with the same 19 methods and 8 fields,
  sharing five signatures out of nineteen. The member fingerprint now overrules the count, sees past a
  wrongly recovered parent, and is given the nearest candidates rather than the first hundred and sixty
  the image happens to list.
- **Types still missing on 1903: 14 → 1.** Portal glow, the ESP highlight, the whole Photon stack
  (guard, network log, voice mimic in both directions), VRC.Player, VRCPlayer, PlayerManager and
  LoadBalancingClient all resolve again. **Console errors: 0.**
- **A member that cannot be identified is refused, not guessed.** Every token translation is checked
  against the member's recorded shape; a method VRChat stripped (a healthy `MethodInfo` with a null entry
  point) is never handed out; and a hook is refused on any member placed only by a shape it shares with a
  neighbour — a detour there hands VRChat's real arguments to a prefix written for different ones, which
  is an access violation inside the trampoline before any of the mod's own code runs.

### Fixed — performance

- `VaTagsModule.LocalUserId()` was recomputed every frame by player grab and three more times per roster
  pass, at four il2cpp crossings per miss — and before you are logged in, every call is a miss. Memoised.
  **Mod cost with the menu open: 744 ms/s → 29 ms/s.**
- Several player walks treated `api == null` as a liveness check. `VRCPlayerApi` is not a
  `UnityEngine.Object`, so that test says nothing about the object behind the handle, and reading one
  member off a stale entry is an access violation inside the proxy that no `try`/`catch` can stop.

---

## [3.9.58] — 2026-09-08

### Added

- **`src/Modules/EraLoadingModule.cs` is back** — VRChat's 2017 loading screen, painted over the modern
  one while a world loads, with the era track playing under it. It was removed at the 3.9.20 sync and
  filed in this changelog as one of "the dead probe and capture modules"; it was neither dead nor a
  probe. Restored from `5f08293` with the four changes below, and its seven artwork and audio files are
  embedded again.

  Nothing of VRChat's is hooked, patched or hidden. Detection is passive — objects named `LoadingPopup`
  found through `Resources.FindObjectsOfTypeAll<Transform>()`, then read only for `activeInHierarchy` —
  the screen is drawn on top in IMGUI, and the game's own loading audio is muted only while our track is
  actually playing, restored the moment it stops. There is no Harmony patch, no AssetBundle and no
  il2cpp field read anywhere in it.

- Loading-screen artwork can be **overridden from disk**: a PNG in
  `BepInEx\VRChatArchiveMod\loading\<key>.png` replaces that piece of the 2017 art with no rebuild, and
  an empty folder leaves the original in place (`Core/AssetLoader.EraTexture`). Keys are `l17_ringglow`,
  `l17_midring`, `l17_dashring`, `l17_diamond`, `l17_wave` and `logo`. Everything except the diamond and
  the logo is drawn `StretchToFill` against sizes transcribed from the original canvas, so art of a
  different aspect ratio is distorted rather than letterboxed.
- Config section `[LoadingScreen]` — `Enabled`, `Music`, `MusicVolume`.

### Fixed

- **Turning the loading screen off no longer switches off a different feature.** `OnUpdate` returned
  early when the toggle was off, so detection never ran and `IsLoading` stayed `false` for the whole
  session — and `SpawnSoundModule` rode on that flag, so its spawn stinger stopped working while its own
  toggle still read ON. Detection now always runs and the toggle decides only whether the screen is
  drawn and whether the music plays; switching off mid-load still restores the game's audio at once.
  This is very likely why the module was deleted rather than fixed, and it is the reason the restored
  version is not a straight revert.
- The popup scan swallowed an exception instead of skipping the object it came from
  (`catch { }` where `AtLoginScreen` twelve lines away correctly writes `catch { continue; }`). Three
  objects in this build are named `LoadingPopup` — one live in `level1`, plus two prefab copies in
  `resources.assets` — and the `scene.IsValid()` test that guard protects is the only thing separating
  them, so swallowing it is exactly how a prefab copy becomes a false positive.

### Removed

- `Rotated()` in `EraLoadingModule`, which had no callers.

---

## [3.9.57] — 2026-09-08

Set by the sync commit that brought the public tree up to the build shipping that day. `<Version>` in
`VRChatArchiveMod.csproj` and `PluginInfo.Version` in `src/Plugin.cs` both read `3.9.57`.

**This is one squashed sync, not a release.** Versions 3.9.21 through 3.9.56 were built in the private
tree and left no commit, tag or release here, so the per-version history between 3.9.20 and this point
is **unknown** to this repository. What follows is what the diff against `0153028` actually contains:
29 files, +2 823 / −40, of which 9 are new. `src/` goes from 100 to 110 tracked files.

### Added

- **`src/Modules/SignatureSoundModule.cs`** — a sound that belongs to a *person*, played on arrival.
  Detection is local and unnetworked on purpose: every client in the instance sees the same arrival and
  plays the clip for itself, so nothing can be spoofed by faking a trigger, no account or server is
  needed, and — unlike the soundboard, where the sender is the one client that never hears what it just
  fired — the person it belongs to hears their own. The first pass after a scene load seeds the roster
  silently, so walking into a full world is not twenty arrivals; the local player is the deliberate
  exception, because their own appearance in the roster *is* the moment they finished loading in.
  The table of ids lives outside the repository (see *Security* below), so the public tree compiles it
  empty.
- **`src/Modules/PlayerStatesModule.cs`** — live AFK / seated / in-station state for remote players,
  read off the avatar's own synced animator parameters, plus VR detection through
  `VRCPlayerApi.IsUserInVR()`. Both carry an explicit *known* flag: an avatar that implements none of
  those parameters reports "not known yet" rather than "not AFK", and a rig that never answers the VR
  question produces silence rather than the claim that somebody is on desktop.
- **`src/Modules/ChatMimicModule.cs`** — mirrors a chosen player's chatbox. Resolving whose bubble is
  whose is the whole difficulty: VRChat keeps chat bubbles under a single global `NameplateManager`
  rather than under the player they belong to, so the owning player is matched through the nameplate
  container, with a rate-limited scene scan as the fallback.
- **`src/Modules/FastSyncModule.cs`** — asks VRChat to serialise the local player at its own fast rate.
  It is the game's own flag on the game's own serialiser, not a hand-rolled change to the Photon send
  rate.
- **`src/Modules/PlayerRotatorModule.cs`** — tilts the local capsule, including fully upside down, with
  the neck clamp widened so the view follows instead of fighting it.
- **`src/Core/Il2CppStr.cs`** — a *validated* il2cpp string reader. `Il2CppStringToManaged` takes a
  pointer, reads the length out of the string header and memmoves; a stale pointer therefore faults
  inside `memmove`, and an `AccessViolationException` is uncatchable on .NET 6, so a `try`/`catch`
  around the call is decorative. This checks the object before reading it and counts what it rejects.
- **`src/Core/VrcPlusItems.cs`**, **`src/Core/VRCPlusSpoof.cs`** — the VRC+ cosmetic gate, and what it
  does and does not do. It changes what the client believes about an item it has **already
  downloaded**; nothing is sent, no `ApiModel` is written, and anything VRChat validates server-side is
  unaffected.
- **`src/Core/EcoPatcher.cs`**.
- Per-player Udon actions resolved **by variable** (`UdonManagerModule`, +145). A world that keeps one
  script per person under a single parent — Among Us and its `Player Nodes/Player Node (N)` — cannot be
  resolved geometrically: "the node nearest the nameplate" returns an arbitrary node *and reports
  success*. The third mode reads a named variable (`playerID`) off each live candidate and fires on the
  one whose value matches the player's actor number, or refuses explicitly if nobody claims them.
- Platform tags read **PC / VR / Quest** in the wing player list, the instance HUD and the QuickMenu
  (`WingPlayersModule`, `InstancePanelsModule`, `Core/Menu`). The build and the headset are two
  different questions: the build tag is always drawn, and `VR` is added only when VRChat itself
  answered — a headset on a PC is PC + VR.

### Changed

- **The rainbow ESP follows a rank, not a list** (`Core/TrustKit`, `VaTagsModule`). It was a
  hand-maintained set of user ids in `ESP/RainbowUsers`; anyone holding **Archive Legendary** is now
  drawn with it automatically. The rank badge is rewritten on every tag pass and removed the moment the
  level drops, so the rainbow lasts exactly as long as the rank does — nothing to grant by hand and
  nothing to clean up after a demotion. The hand-kept list still works beside it. New toggle
  `ESP/LegendaryRainbow` (on by default). The rule lives in `IsRainbow` and nowhere else, because there
  are three ESPs and a rule written three times is a rule that will be right in two of them.
- A signature sound suppresses the generic spawn stinger for the person who has one, instead of the two
  playing over each other (`SpawnSoundModule`).

### Fixed

- **`SpoofModule` no longer faults reading a display name** (+97). It read the name through a raw
  il2cpp string pointer; the pointer can go stale, and the fault lands inside `memmove` where nothing
  can catch it. It goes through `Il2CppStr` now, and a written name is kept rooted so the il2cpp GC
  cannot collect it out from under the object.
- **`BlockedByProbeModule` no longer faults walking the moderation list** (+257 / −8). `TryCast<T>()`
  dereferences two pointers — the target class and the object's own class — and either can be rotten,
  which is why a stack full of `System.__Canon` cannot say which call it was. The list is walked
  through its indexer instead, which removes the per-row cast entirely, and class pointers are
  validated before use. The module was **not** disabled to achieve this.
- Chat bubbles are found through the nameplate container rather than by assuming they hang under the
  player, and the failure path is logged, not only the success path.

### Security

- **`*.local.cs` is gitignored.** Source that keys behaviour to a real VRChat user id — the signature
  sound table — is kept out of the repository, for the same reason `ressources/` is. The audit before
  the first push found real `usr_` ids in `ModConfig` defaults and blanked them; this makes the rule
  structural rather than something to remember. The partial method those files implement has no body in
  the public tree, so it compiles to an empty table, exactly as the project already compiles without the
  media it embeds.

---

## [3.9.20] — 2026-09-07

Set by the commit *Sync public source with v3.9.20* (`0153028`), which brought the public tree up to the
build that shipped that day. `<Version>` in `VRChatArchiveMod.csproj` and `PluginInfo.Version` in
`src/Plugin.cs` both read `3.9.20`.

This is one squashed sync, not a release. The section below therefore describes **what the tree contains
at 3.9.20**, grouped by subsystem, and then lists the parts of the 3.5.0 → 3.9.20 change that are
verifiable from the repository.

### The feature surface at 3.9.20

The tree holds 114 tracked files: 100 under `src/` (`Plugin.cs`, 36 files in `src/Core/`, 63 in
`src/Modules/`), plus `AssetBundlePatch.cs`, three Python tools in `tools/`, and documentation.
`src/Plugin.cs` → `RegisterModules()` is the authority on what is *live*: a number of the archive
injection modules are present in source but registered commented-out, each with a paragraph explaining
why, and this file does not claim they are active.

**Archiving and favourites** — `FavoritesModule`, `KindFavoritesModule`, `UserFavoritesModule`,
`WorldFavoritesModule`, `ArchiveCategoryModule`, `ArchiveHijackModule`, `ArchiveFavListModule`,
`ArchiveFavGridModule`, `ArchiveFavButtonModule`, `WorldFavListModule`, `CacheWatchModule`,
`AssetBundlePatchModule`, `Core/ArchiveFeed`. Unlimited local favourites in three layers (ids, metadata,
disk-cached thumbnails); several competing techniques for grafting a real "VRCHAT ARCHIVE" presence into
VRChat's own menus, because the newer Voyager pages cannot be injected into and need the mod's own
overlay grid instead; Save/Remove-to-Archive buttons on the avatar detail pane. The Harmony prefix on
`UnityWebRequestAssetBundle.GetAssetBundle` that clears the cache encryption key is what makes local
archiving possible at all.

**QuickMenu UI and theming** — `QuickMenuTabModule`, `UserMenuModule`, `WingPlayersModule`,
`WingLogModule`, `InstancePanelsModule`, `LaunchpadConsoleModule`, `MenuSkinModule`, `MenuThemeModule`,
`OverlayMenuModule`, `VrcPlusBackgroundsModule`, and `Core/PanelSkin`, `Core/SidePanel`,
`Core/MenuDonor`, `Core/MenuCard`, `Core/Toast`. VRChat's unused DevTools tab re-purposed into a native
"VRChat Archive" tab; the live roster on the left wing and the instance log on the right, as panels
**built** beside the wings rather than cloned from a VRChat page — a cloned page carries a `UIPage`, and
VRChat then refuses to open the QuickMenu at all; the Launch Pad's 1024x280 promo-carousel slot
re-purposed into the archiver and cache console; wallpaper and gradient theming with exact restore when
switched off.

**Safety, anti-crash and anti-abuse** — `AntiCrashModule`, `PhotonGuardModule`, `TrueViewModule`,
`NsfwFilterModule`, `WatchlistModule`, `BlockedByProbeModule`. The anti-crash pass clamps particle,
light, audio, cloth, PhysBone, contact, polygon, material and shader bombs above thresholds set far over
VRChat's own ratings, and journals every clamp so switching it off restores the avatar exactly. Photon
Guard drops hostile **inbound** events (per-code block list, per-actor-and-code rolling rate limit)
before VRChat dispatches them, and never sends, raises, edits or replays anything. TrueView locally
re-reveals avatars VRChat force-hides after a block, re-enabling only the exact objects it switched off.
`BlockedByProbeModule` reads `ApiPlayerModeration.FetchAllAgainstMe` to fill the "who blocked me" sets
and draws nothing.

**Visualization and ESP, screen-only** — `EspModule`, `CapsuleEspModule`, `HighlightEspModule`,
`RadarModule`, `RadarMapCamera`, `ProfilerHudModule`, and `Core/GuiKit`, `Core/Hud`, `Core/Overlay`,
`Core/TrustKit`. Screen-space box, skeleton, name and distance ESP with frustum culling; glowing
capsules cloned from VRChat's own SelectRegion mesh fed to `HighlightsFX`; real-geometry highlights for
players, portals and pickups; a trust-rank-coloured radar over a hand-rendered orthographic map camera;
a per-module frame-cost HUD. None of this leaves the local screen.

**Movement, objects and body** — `MovementModule`, `SpeedModule`, `GravityModule`, `ObjectGravityModule`,
`ForceJumpModule`, `ForceGrabModule`, `ForcePickupModule`, `PlayerGrabModule`, `OrbitModule`,
`ObjectOrbitModule`, `GhostModule`, `SelfHideModule`, `MimicPoseModule`, `VoiceMimicModule`,
`VoiceProbeModule`, and `Core/ForceJoin`, `Core/PlayerRef`. Fly and noclip, locomotion overrides
re-applied ten times a second with captured originals restored on off, self and world gravity, a one-shot
`SetVelocity` launch, Force Pickup (which unlocks a world's locked pickups so your own hands take them,
as opposed to Force Grab driving a transform), cooperative player grab and throw where the held player's
own client moves itself, orbit rings, Ghost (disables the local `FlatBufferNetworkSerializer` so nothing
about your body is serialized outbound), Self Hide (renderers off, bundle still loaded), and pose
mimicry through `HumanBodyBones` in `LateUpdate`. `ForceJoin` navigates by id through
`VRC.SDKBase.Networking.GoToRoom` — the same call a world's Udon portal makes; the instance still
decides whether it will have you.

**Media and the object show** — `BadAppleModule`, `MarkModule`, `SoundboardModule`, `SpawnSoundModule`,
`VideoModule`, `VideoUrlModule`, and `Core/BadAppleAudio`, `Core/WavAudio`, `Core/AssetLoader`, plus
`tools/bake_badapple.py`, `tools/chatbox_probe.py` and `tools/pick_charset.py`. *Bad Apple!!* as chatbox
shadow art over OSC from pre-baked embedded frames; `MarkModule` turning the world's loose pickups into
the pixels for the same frames, with the soundtrack as the clock so the picture cannot drift while the
networked mode waits on object ownership; a networked soundboard that broadcasts only a *key* and plays
the matching embedded WAV locally; and two video paths that are deliberately not interchangeable —
`VideoModule` points a world's player at a URL on **your screen only**, through VRChat's own
`TryCreateAllowlistedVRCUrl` so the allowlist applies exactly as it does for a world author, while
`VideoUrlModule` takes ownership and writes the script's **synced** variable, which the world's own
sync then carries to the whole instance.

**Udon and network observation** — `UdonLogModule`, `UdonManagerModule`, `NetworkLogModule`,
`NetSendModule`, and `Core/UdonSymbols`, `Core/Il2CppSeq`. A passive Harmony hook on
`UdonBehaviour.RunProgram(string)` with adaptive flood muting and an off-by-default client-side crasher
guard; per-client Udon behaviour on/off, entry-point and public-variable inspection and editing, local
event execution with a deliberately separate explicit global broadcast path, and a RESTORE that undoes
only its own changes; a receive-side postfix on `VRCNetworkingClient.OnEvent` that records inbound Photon
events without transmitting, raising or replaying anything; and a send-side counter that reports what
this client pushes onto the wire, per second and per event code.

**Diagnostics, IL2CPP survival and desktop-client integration** — `DiagnosticsModule`,
`UiTreeDumpModule`, `ModControlModule`, and `Core/NativeGuard`, `Core/FieldOffsetFix`,
`Core/NestedTypeFix`, `Core/Il2CppDelegates`, `Core/UiClick`, `Core/CrashTrail`, `Core/FeatureHealth`,
`Core/ConfigWatch`, `Core/ConfigRegistry`, `Core/Redact`, `Core/Unwrap`, `Core/ModConfig`,
`Core/ModuleManager`, `Core/ModRunner`, `Core/IModule`, `Core/VaAuth`, `Core/ApiUsers`,
`Core/QuickMenu`. `NativeGuard` `VirtualQuery`-checks pointers before dereference; `FieldOffsetFix`
Harmony-patches `il2cpp_field_get_offset`; `NestedTypeFix` walks `Il2CppClass` directly instead of
calling VRChat's renamed export; one gate sits in front of Il2CppInterop's `ConvertDelegate` so a feature
degrades to "unavailable" rather than killing the process; `CrashTrail` flushes breadcrumbs to disk
before each risky il2cpp call, so an access violation — which throws nothing and unwinds nothing — still
leaves a file whose last line names what died. `ModControlModule` polls the desktop client's LocalBridge
on `127.0.0.1:8791` and applies commands on the Unity main thread, with `ConfigRegistry` as the schema.

**Community tagging** — `VaTagsModule`, `FewTagsModule`, `Core/VaAuth`. The VRChat Archive tagging
system (live roster, styled tags keyed by VRChat user id, animated nameplate plates, the PLAYERS tab
actions) and the FewTags database rendered as plates cloned from the game's own nameplate popup, with
slurs filtered locally before drawing. FewTags is a close derivation of Fewdys's mod; attribution is in
`NOTICE` and in the README's Credits section.

### What other players can see

Most of the surface above is local to your screen, but not all of it, and this file will not claim
otherwise. Opt-in features that act on the shared instance rather than on your own view include
`MarkModule` and `ObjectOrbitModule` in their networked modes (both take SDK ownership of real pickups
and move them for real), `VideoUrlModule` (plays a URL synced to the whole instance),
`VoiceMimicModule`, `BadAppleModule`'s chatbox output (VRChat broadcasts the chatbox to everyone),
`GhostModule` (suppresses your outbound serialization, which changes what others see of you), and
`SoundboardModule` (sends a key and your VRChat display name to this project's own API so other mod
users can play the clip). The module headers in `src/` state this per feature, in several cases in
capitals.

### Added since 3.5.0 (verifiable from the tree)

`src/Modules/` went from 59 files to 63: 18 added, 14 removed.

- New modules: `BlockedByProbeModule`, `CacheWatchModule`, `ForcePickupModule`, `GhostModule`,
  `LaunchpadConsoleModule`, `MarkModule`, `MimicPoseModule`, `NetSendModule`, `NsfwFilterModule`,
  `ObjectGravityModule`, `PhotonGuardModule`, `PlayerGrabModule`, `SelfHideModule`, `UiTreeDumpModule`,
  `VoiceMimicModule`, `VoiceProbeModule`, `VrcPlusBackgroundsModule`, `WingLogModule`.
- New core files (`src/Core/` went from 26 to 36): `ArchiveFeed`, `BadAppleAudio`, `ConfigRegistry`,
  `CrashTrail`, `ForceJoin`, `Il2CppSeq`, `MenuDonor`, `PanelSkin`, `SidePanel`, `Toast`,
  `UdonSymbols`.

### Changed since 3.5.0

- The QuickMenu side panels are **built** rather than cloned. Three earlier attempts cloned one of
  VRChat's own wing pages; each failed for its own measured reason, and a cloned page carries a `UIPage`
  that stops the QuickMenu opening.
- The Bad Apple object show is clocked off the soundtrack's playback position (`Core/BadAppleAudio`)
  instead of `Time.realtimeSinceStartup`, because the networked mode holds on frame 0 while it takes
  ownership of objects and a wall-clock counter drifts away from the music during that wait.
- `Core/ConfigRegistry` was lifted out of the deleted `DevToolsModule` into its own file, and is now the
  backbone the desktop client's settings page walks.
- `Core/Menu` lost its DEV tab along with `DevToolsModule` — the `Tab` enum went from ten entries to
  nine — and shed 433 lines against 132 added. The IMGUI menu itself was already retired before the
  source was published (`Visible` can be set false and never true, so nothing can open it); its pages
  live in the desktop client, and what still runs in that file is cursor capture, the Alt free-cursor
  hold and the "world scripts are blocked" warning.

### Removed since 3.5.0

- The dead probe and capture modules: `ArchiveSidebarRowModule`, `AudioWatchModule`, `AutoProbe`,
  `AvatarListProbe`, `CaptureModule`, `DevToolsModule`, `EraLoadingModule`, `LogCaptureModule`,
  `MenuCaptureModule`, `MenuExclusiveModule`, `MenuPanelProbe`, `MenuRecorderModule`,
  `QuickMenuConsoleModule`, `UiProbeModule`, plus `src/Core/DelegateProbe`.

### Fixed since 3.5.0

Only the three fixes the sync commit names have public evidence; there is no way to attribute any other
fix to a version.

- **MenuCard** — "the label holder" is the TMP's parent, which on cards that have no `TextLayoutParent`
  *is the card*. `LayoutCard` then pulled the card out of its grid and stretched it into a full-width
  plate over the page.
- **UserMenuModule** — donor buttons must be judged on their own state, not on `activeInHierarchy`,
  which is false for every child of a hidden page. The picker kept falling back to VRChat's disabled
  button, so every card came out grey.
- **AntiCrash** — a new amplified-mesh vector: 10142 triangles from 26 vertices is an index buffer
  written to redraw the same points thousands of times, and it slipped under every triangle and material
  budget.

---

## Versions between 3.5.0 and 3.9.20

**Not documented, and not recoverable.** Public history steps straight from 3.5.0 to 3.9.20 in a single
squashed sync commit. Whatever point releases existed in between were built before the source was
published; they left no commit, no tag, no release and no artefact in this repository, so their count,
their numbering, their dates and their contents cannot be established from it. No entry is written for
them, because any such entry would be fabricated.

---

## [3.5.0] — 2026-08-30

Initial public release (`5f08293`), 96 tracked files: `.gitignore`, `LICENSE`, `README.md`,
`VRChatArchiveMod.csproj`, `AssetBundlePatch.cs`, `BADAPPLE_TEST.bat`, `DEPLOY.bat`, the three Python
tools in `tools/`, and `src/` with `Plugin.cs`, 26 files in `src/Core/` and 59 in `src/Modules/`.

### Added

- The module system the project is built on: `IModule` as the abstract lifecycle contract, and
  `ModuleManager` as the registry and per-frame dispatcher that wraps every callback in an exception
  guard with rate-limited error logging.
- `ModConfig` as the single source of truth for settings, every one of them a BepInEx `ConfigEntry`
  bound and documented in one place.
- The Il2CppInterop safety layer: `NativeGuard` and `FieldOffsetFix`, plus `NestedTypeFix` and the
  delegate-conversion gate.
- `libs/` and `ressources/` deliberately excluded from the repository, with the reason recorded in
  `.gitignore` and the README. They are not the project's to redistribute.

### Note on the version number in this release

At `5f08293` the two version strings disagreed: `VRChatArchiveMod.csproj` said `3.5.0` while
`src/Plugin.cs` still had `PluginInfo.Version = "3.4.0"`, so a build from that commit would have printed
*v3.4.0* in the BepInEx log. The commit message and the csproj agree on 3.5.0, and this file follows
them. `0153028` aligned both strings to 3.9.20. There was no 3.4.0 public release.

### Changed

- `c0dab30`, the same day: GitHub was detecting the licence as "Other" because clarifying text followed
  the MIT body. `LICENSE` is now pure MIT and the third-party notes moved into `NOTICE`. No code change.

---

## How entries are cut from here on

Every release from now on gets its own entry.

- Work lands under **[Unreleased]** as it is merged.
- When a release is ready, the version is bumped in **both** places that carry it —
  `<Version>` in `VRChatArchiveMod.csproj` and `PluginInfo.Version` in `src/Plugin.cs` — and they must
  match. The 3.5.0 note above is what happens when they do not.
- The `[Unreleased]` block is renamed to the new version with the release date, and a fresh empty
  `[Unreleased]` is opened above it.
- The release is then cut by pushing an annotated git tag of the form **`vX.Y.Z`** (for example
  `v3.9.21`), which is what the release workflow keys off. A version with no `vX.Y.Z` tag is not a
  release, and does not get an entry here.

Two things this file will keep doing:

- Grouping changes under Keep a Changelog's headings — Added, Changed, Deprecated, Removed, Fixed,
  Security — rather than by module.
- Saying "unknown" where something is unknown. The gap above is the reason this file exists in the shape
  it does.

Note for anyone reading this expecting downloadable builds: there are none here. The project cannot be
built from a fresh clone of this repository, by design — `libs/` (proprietary VRChat, Unity and BepInEx
reference assemblies) and `ressources/` (the 18 embedded media files the `.csproj` lists) are not
redistributable and are not included. See the README's Building section.

[Unreleased]: https://github.com/kawaiistudio/VRChatArchiveMod/compare/v3.9.58...main
[3.9.58]: https://github.com/kawaiistudio/VRChatArchiveMod/compare/v3.9.57...v3.9.58
[3.9.57]: https://github.com/kawaiistudio/VRChatArchiveMod/compare/0153028...v3.9.57
[3.9.20]: https://github.com/kawaiistudio/VRChatArchiveMod/commit/0153028b767c5a1845c0729031011bceeb077cea
[3.5.0]: https://github.com/kawaiistudio/VRChatArchiveMod/commit/5f0829345e3376f3de1fe8cd5f0fbd56036a7b09
