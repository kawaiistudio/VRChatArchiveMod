<p align="center">
  <img src="docs/img/logo.png" alt="Kawaii Studio" width="180">
</p>

<h1 align="center">VRChat Archive Mod</h1>

<p align="center"><em>by <a href="https://kawaiistudio.org">Kawaii Studio</a> · <a href="https://vrchatarchive.org">vrchatarchive.org</a></em></p>

<p align="center">
  An <strong>open-source, local-only BepInEx 6 (IL2CPP) mod for VRChat</strong>: the original C# mod,
  its native C++ rewrite, and the toolkit that keeps either one alive when VRChat updates.
</p>

<p align="center">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-ff5aa0">
  <img alt="Open source" src="https://img.shields.io/badge/open%20source-yes-8143e6">
  <img alt="BepInEx 6 IL2CPP" src="https://img.shields.io/badge/BepInEx-6%20(IL2CPP)-512bd4">
  <img alt="VRChat build 25686233 / Unity 6" src="https://img.shields.io/badge/VRChat%20build-25686233%20(Unity%206)-f0a000">
  <img alt="Status: help wanted" src="https://img.shields.io/badge/status-help%20wanted-d7263d">
</p>

> **Unofficial** and **local-only**. Not affiliated with VRChat Inc. Loading third-party code into VRChat may violate its Terms of Service — see [Disclaimer](#disclaimer).

---

## 🚨 We need your help

**This mod is broken on the current VRChat build, and we cannot fix it alone.**

On **2 October 2026** VRChat shipped build **25686233** and moved the client from Unity 2022.3 to **Unity 6 (6000.0.67f1)**. That single update did three things at once: every il2cpp export got a fresh set of obfuscated names, the il2cpp struct layouts changed, and a long list of members the mod relied on were inlined, renamed or removed. The mod our community used every day stopped loading.

Here is where things stand, honestly:

- **The C# mod (`src/`)** — the original, with the in-game UI people love — loads again on Unity 6 after weeks of patching, but it runs on interop assemblies frozen on an older build. Every member Unity 6 inlined or VRChat renamed has to be worked around by hand, and many features are dead or half-working. The list is [below](#status-on-vrchat-build-25686233-unity-6).
- **The C++ rewrite (`cpp/`)** boots natively — without Il2CppInterop and without the struct-layout patcher — resolves the obfuscated build on its own and registers 35 modules. But it is young: not everything is verified in-game, and its UI does not yet match the original.
- **We are a very small team** maintaining a website, a desktop client, a server and this mod. We do not have the hours this port needs.

**If you know BepInEx 6 / IL2CPP internals, Unity 6, native hooking — or you simply know C# well and care about VRChat — please help us bring the old mod back.** Everything we learned is written down in this repository so you can start today instead of rediscovering it.

### What would help most

| Area | What is needed | Start here |
|---|---|---|
| **Il2CppInterop on Unity 6** | Regenerating usable interop assemblies for build 25686233, or fixing the remaining icall / generic resolution failures (`GUIStyle.set_fontSize`, `List<T>`) so the C# HUD and player lists work again | [`docs/UNITY6-PORT.md`](docs/UNITY6-PORT.md) |
| **Members that moved** | Finding the Unity 6 replacements for `VRCPlayerApi.get_isLocal / get_gameObject / get_displayName`, the Box Drop serializer seam, nameplates (now GPU-instanced) | [`docs/UNITY6-PORT.md`](docs/UNITY6-PORT.md) → *What remains broken* |
| **The native engine** | Porting the remaining C# modules to `cpp/`, verifying each one in-game, and rebuilding the QuickMenu "wings" UI faithfully | [`cpp/README.md`](cpp/README.md) · [`docs/MODULES.md`](docs/MODULES.md) |
| **Per-build tooling** | Making the il2cpp export remap and the struct-offset derivation fully automatic for the next VRChat build | [`tools/pack-port/README.md`](tools/pack-port/README.md) |
| **Testing** | Running the witness test (the game with and without the plugin) on your machine and reporting `ErrorLog.log` | [Quick start](#quick-start-for-contributors) |

### How to help

1. Read [`CONTRIBUTING.md`](CONTRIBUTING.md). It is short, and it explains why this repository cannot build in CI and how changes are verified instead.
2. Pick an item above, or open an issue saying what you want to take on, so nobody duplicates work.
3. Send a pull request: small, verified in-game, with the log lines that prove it. That is the whole review standard here.

Questions, findings, half-finished experiments: open an issue. A negative result ("I tried X, it crashes because Y") is a contribution too — several are already recorded in the docs so nobody repeats them.

---

**Contents** — [What is in this repository](#what-is-in-this-repository) · [Status](#status-on-vrchat-build-25686233-unity-6) · [What is not here](#what-is-deliberately-not-in-this-repository) · [Quick start](#quick-start-for-contributors) · [The Unity 6 problem](#the-unity-6-problem-in-brief) · [The pack and the loader](#the-pack-and-the-loader) · [Features](#features) · [Screenshots](#screenshots) · [Architecture](#architecture) · [Credits](#credits--third-party) · [Disclaimer](#disclaimer) · [License](#license)

---

## What is in this repository

| Path | What it is | State |
|---|---|---|
| `src/` | **The original C# mod** — a BepInEx 6 IL2CPP plugin, one feature per module, driven by the companion desktop client over loopback | Loads on Unity 6; many features broken |
| `cpp/` | **The native rewrite** — a one-file BepInEx plugin (`cpp/loader`) that carries a native engine (`cpp/native`) talking to il2cpp directly | Boots, 35 modules registered; in progress |
| `tools/pack-port/` | **The Unity 6 port kit** — the il2cpp export remap for build 25686233, the `VRChatStructFix` patcher source, struct-layout notes, probes and diagnostics | Working; needs automation |
| `tools/*.py` | Offline tools that bake the chatbox art assets | Working |
| `docs/` | [`UNITY6-PORT.md`](docs/UNITY6-PORT.md) · [`LOADER-AND-PACK.md`](docs/LOADER-AND-PACK.md) · [`MODULES.md`](docs/MODULES.md) | — |

Both plugins build to a file named `VRChatArchiveMod.dll`. **Install one or the other, never both.**

---

## Status on VRChat build 25686233 (Unity 6)

Everything below was observed in-game on this build. *Works* means verified; *broken* means verified broken, with the cause where it is known.

### C# mod (`src/`)

| | |
|---|---|
| **Loads** | Yes — `Chainloader startup complete`, every module initialises, `ErrorLog.log` stays empty, and in the same harness the game lives *longer* with the plugin than without it. Requires the `VRChatStructFix` patcher and the interop assemblies of the last Unity 2022 build, with `UpdateInteropAssemblies = false`. |
| **Confirmed working** | The module system, configuration, logging and the client bridge (`127.0.0.1:8791`). Beyond that, treat every individual feature as **unverified on this build** until someone runs it and reports — one of the easiest ways to help. |
| **Broken — HUD** | `UnityEngine.GUIStyle::set_fontSize` → *ICall was not resolved*. Takes down the Watchlist banners, the Instance Panels and the Active-Features HUD. This is Il2CppInterop's icall resolution, not the mod's token layer. **Highest-value fix.** |
| **Broken — generics** | `Il2CppSystem.Collections.Generic.List<T>` and `MethodInfoStoreGeneric` throw `TypeInitializationException`. Consequences: `VRCPlayerApi.AllPlayers` is empty, `ApiContentModel<T>` getters are unreadable, Voice Mimic's Photon watch is disabled on purpose. |
| **Broken — moved members** | `VRCPlayerApi.get_isLocal`, `get_gameObject` and `get_displayName` resolve to null, so Fly, Movement, Orbit, Self-Hide and TrueView become silently inert, and the client's Force Clone list shows no names. Replacement routes exist and are implemented in `cpp/`, not yet in `src/`. |
| **Broken — gone from the build** | Nameplates are GPU-instanced (no GameObject to clone → the FewTags / VaTags plates); the Box Drop serializer seam has no `Vector3` method left; `Mathf.*` and every `Component.GetComponent*` are inlined (shimmed in `src/Core`). |
| **Refused hooks** | Six Harmony hooks are refused by `ProxyGuard` because a parameter type cannot be marshalled on this interop: `ApiAvatar.SetApiFieldsFromJson`, the Photon `ParameterDictionary` / `RaiseEventOptions` hooks, `UnityWebRequestAssetBundle.GetAssetBundle`. |

### C++ engine (`cpp/`)

| | |
|---|---|
| **Boots** | Yes, with neither Il2CppInterop nor StructFix: resolves the obfuscated il2cpp exports through the validated remap table, recovers `VRC.Player` / `VRCPlayer` by their structure, installs a native frame pump on `EventSystem.Update`, and hooks through the `dobby.dll` BepInEx already ships. Zero Il2CppInterop errors in the log. |
| **Verified in-game** | Local-player resolution (the static self-typed field on `VRCPlayer`), Fly, the QuickMenu integration (21 cards), the client bridge transport (`/mod/schema`, `/mod/sync`). |
| **Ported, not all verified** | 35 modules are registered — see [`docs/MODULES.md`](docs/MODULES.md#the-c-engine-cpp). Several bridge actions the client expects are still to be ported. |
| **Not there yet** | The QuickMenu "wings" UI does not match the C# original; avatar id, name and public/private status are resolved by the client rather than by the engine; nameplate tags are drawn in the overlay instead of on nameplates. |

---

## What is deliberately NOT in this repository

- **VRChat's binaries, BepInEx's compiled core and the generated interop assemblies** (`libs/`). They are Unity's, VRChat's and BepInEx's — not ours to redistribute. [`CONTRIBUTING.md`](CONTRIBUTING.md) explains how to produce `libs/` from your own install; [`docs/LOADER-AND-PACK.md`](docs/LOADER-AND-PACK.md) explains how to assemble a modded game folder.
- **The embedded media** (`ressources/`): third-party audio, images and baked frames. Listed file by file in `CONTRIBUTING.md`; supply your own, or drop the matching `<EmbeddedResource>` lines.
- **Two features of the private build: the plaintext-cache decryptor and the Dex patcher.** They are server-gated, paid features delivered to entitled accounts at runtime. They were removed from both code bases before publication; the call sites are stubbed and marked `[open-source build]`, and nothing else depends on them. Pull requests adding them back will be closed.
- **The pack itself** — the ~465 MB modded VRChat folder the desktop client downloads. It contains the game. The documentation describes its layout, its manifest and how to build an equivalent test folder locally.

---

## Quick start for contributors

### C# mod

```bash
# 1. Supply libs/ (BepInEx 6 IL2CPP core + the interop assemblies from your modded install)
#    and ressources/ — see CONTRIBUTING.md for the exact lists.
# 2. Build
dotnet build -c Release
# 3. Deploy: copy bin/Release/VRChatArchiveMod.dll into <modded VRChat>/BepInEx/plugins/
#    The game must be CLOSED while you copy: the DLL is locked while VRChat runs.
```

On the game side the C# mod needs BepInEx 6 IL2CPP `be.788`+ with the export remap applied (see [`docs/LOADER-AND-PACK.md`](docs/LOADER-AND-PACK.md)), `BepInEx/patchers/VRChatStructFix.dll` (source in `tools/pack-port/VRChatStructFix`), and `UpdateInteropAssemblies = false` in `BepInEx.cfg`.

### C++ engine

```bash
# 1. Native engine — Visual Studio 2022, toolset v143, C++20
msbuild cpp/native/VRCArchiveNative.vcxproj -p:Configuration=Release -p:Platform=x64
# 2. The one-file loader, which embeds the engine it just built
dotnet build cpp/loader -c Release -p:GameDir="<your modded VRChat folder>"
# 3. Deploy: copy cpp/loader/bin/Release/VRChatArchiveMod.dll into <modded VRChat>/BepInEx/plugins/
#    Do NOT ship patchers/VRChatStructFix.dll alongside it: the engine does not need it, and it only adds log noise.
```

### Test like we do

Run the game **without** the plugin first and note how long it lives; then **with** it. That witness run is what tells a mod crash from an environment crash. Then read:

- `BepInEx/ErrorLog.log` — empty means no fatal error;
- `BepInEx/LogOutput.log` — the chainloader and, for the C# mod, its own lines;
- `BepInEx/LogOutput/VRChatArchive.log` — the C++ engine's log.

Conventions and the review standard: [`CONTRIBUTING.md`](CONTRIBUTING.md). Security reports: [`SECURITY.md`](SECURITY.md).

---

## The Unity 6 problem, in brief

The full write-up is [`docs/UNITY6-PORT.md`](docs/UNITY6-PORT.md). The short version:

1. **BepInEx did not even start.** Unity 6 needs BepInEx `be.788` or newer, and its doorstop finds `il2cpp_init` **by name** — a name VRChat obfuscates. The fix is to rename that one string in `winhttp.dll` to the obfuscated export (it is index 0 of the export run), then repoint about 184 P/Invoke entry points in `Il2CppInterop.Runtime.dll` and `BepInEx.Unity.IL2CPP.dll` with Mono.Cecil. The canonical order comes from `il2cpp-api-functions.h` of the exact Unity version (`js6pak/libil2cpp-archive`, tag `6000.0.67`); the only gap is the seven profiler functions that release builds do not export.
2. **Regenerating interop needs the metadata, and the metadata is encrypted** with a scheme that changed between the May and the October 2026 builds. A dead end for now: the mod runs on the previous build's interop plus `VRChatStructFix`, whose `MethodInfo.flags` offset had to be corrected (`0x4C`, not `0x4E`).
3. **Unity 6 inlined things and VRChat renamed things.** `Mathf.*`, every `Component.GetComponent*`, `VRCPlayerApi.get_isLocal / get_gameObject / get_displayName`, the Box Drop seam, the nameplates. Each needs a replacement route, and the recurring trap is this: **what C# read as a property is often a plain field on this build** — `FindMethod("get_X")` returns null while the field `X` is right there.
4. **The native route avoids half of it.** A C++ engine that reads il2cpp structures at measured offsets needs neither interop nor StructFix, so a renamed member resolves to null instead of a corrupted pointer. That is why `cpp/` exists.

---

## The pack and the loader

The full write-up is [`docs/LOADER-AND-PACK.md`](docs/LOADER-AND-PACK.md).

The companion desktop client has a **LOADER** tab. It downloads a *pack* — a complete, self-contained modded VRChat folder: a pinned build, BepInEx 6 IL2CPP with the export remap applied, and doorstop — verifies its SHA-256 against a small JSON manifest, extracts it into its own folder (the Steam install is never touched), installs the mod into `BepInEx/plugins/` from a catalogue, and launches `VRChat.exe` from there. The current pack is **v17**, built on build 25686233; the next one is v18. The C# mod and the C++ engine are both installed by the client as `VRChatArchiveMod.dll`.

The pack is not in this repository, because it contains the game. The document above gives its layout, the manifest format, the exact steps to build an equivalent folder from your own Steam install, and the per-build update procedure.

---

## Features

The complete catalogue — every C# module with a one-line description, plus the C++ module list — is in [`docs/MODULES.md`](docs/MODULES.md). In one paragraph:

**Archives avatars** (unlimited local favourites grafted into VRChat's own menus, with metadata and thumbnails through the desktop client) · **puts the data where you look** (instance roster, event log and archiver console as panels on the QuickMenu's wings) · **refuses to crash** (anti-crash clamps for particle, light, cloth, PhysBone, polygon, material and shader bombs — every clamp journaled and reversible) · **local visualisation** (ESP, radar, instance panels, menu theme) · **self-only movement and fun** (fly, speed, gravity, orbit, object orbit, soundboard, *Bad Apple!!* in the chatbox and with the world's own pickups) · **Udon tools** (passive event log, per-client Udon manager, network log) · **player tags** (VRChat Archive tags, FewTags) · **dev probes** (read-only dumpers of the obfuscated UI and structures).

Everything runs on your machine and changes only your view. Network traffic is limited to the desktop client on loopback and the project's own API.

---

## Screenshots

<p align="center">
  <img src="docs/img/mod-ingame.webp" alt="The mod running in VRChat: instance roster, event feed, instance log, radar and the themed QuickMenu">
</p>

<p align="center"><em>Everything the C# mod draws, in one instance — the <strong>PLAYERS</strong> roster and
<strong>EVENTS</strong> feed at the left, the <strong>INSTANCE LOG</strong> and <strong>RADAR</strong> at the
right, and the QuickMenu wearing the mod's own theme. All of it is local: nobody else in the instance
sees any of it. This is the UI the C++ engine is being rebuilt to match.</em></p>

### Bad Apple!!, played with the world's own objects

<p align="center">
  <a href="https://youtu.be/gdFYaCBWtog">
    <img src="docs/img/badapple-thumb.jpg" alt="Bad Apple!! rendered in VRChat out of the world's pickups — click to watch" width="640">
  </a>
</p>

<p align="center"><em><strong><a href="https://youtu.be/gdFYaCBWtog">▶ BAD APPLE VRCHAT MOD</a></strong> — every loose
object in the world becomes a pixel. The shadow art is played back frame by frame onto them, and the
soundtrack is the <strong>clock</strong>: the picture is driven off the audio's playback position, so it cannot
drift from the music while the networked mode waits to take ownership of the objects.</em></p>

---

## Architecture

### The C# mod (`src/`)

A standard BepInEx 6 IL2CPP plugin. `VRChatArchiveModPlugin` is the entry point: on load it binds the configuration, installs the IL2CPP compatibility fixes, registers every feature module, and injects a persistent runtime driver into the scene.

- **Modules.** `IModule` (an abstract class, despite the name) is the lifecycle contract every feature implements — initialise, UI-ready, per-frame `Update` / `LateUpdate` / `FixedUpdate`, `OnGUI`, scene-loaded, shutdown. `ModuleManager` is the registry and per-frame dispatcher: every callback runs inside an exception guard with rate-limited error logging, and an optional per-module stopwatch measures cost. `ModRunner` is the injected `MonoBehaviour` that pumps it.
- **Configuration.** `ModConfig` is the single source of truth: every toggle, threshold and slider is a BepInEx `ConfigEntry`, bound and documented in one place, which is what lets the desktop client drive the whole feature surface through the bridge. `ConfigWatch` logs who changed a value, and when.
- **IL2CPP safety.** VRChat ships an obfuscated IL2CPP runtime, so several layers sit under everything: `NativeGuard` (`VirtualQuery` before any native dereference), `FieldOffsetFix` and `NestedTypeFix` (corrections to what Il2CppInterop reads), `Il2CppDelegates` / `UiClick` (one gate in front of the crash-prone delegate bridge), and on Unity 6 the adaptation layer that keeps the mod alive on frozen interop: `MissingTypeGuard`, `TokenShiftFix`, `MemberAlign`, `ProxyGuard`, `ObfuscatedClassFinder`, `Live.UsableType`. When the runtime is unsafe a feature degrades to *unavailable* instead of taking the process down.
- **The bridge.** `ModControlModule` polls the client's local server on `127.0.0.1:8791`, sends schema, settings, roster, events and Udon payloads, and applies queued commands on the Unity main thread.

### The native engine (`cpp/`)

- **`il2cpp.hpp`** talks to GameAssembly's exported `il2cpp_*` API directly and reads il2cpp structures at measured offsets. Exports are resolved by standard name first, then through the validated remap table for the build. Classes are found by name in the live metadata; when VRChat renamed the *name* too, `recovery.cpp` re-finds the class by its shape and registers an alias.
- **`hooks.hpp`** detours through `dobby.dll`, the hook backend BepInEx IL2CPP already loads — no vendored disassembler.
- **`engine.hpp`** owns the modules and ticks them from a native pump detoured onto `EventSystem.Update`.
- **`config.hpp`** is the settings registry the bridge publishes to the client: a module binds its fields, and schema and sync are generated from the registry, exactly as the C# bridge did.
- **`cpp/loader/Loader.cs`** is the only managed code: a BepInEx plugin that writes the embedded engine beside itself and calls `VRCA_Start`. One file to deploy.

Details and the boot sequence: [`cpp/README.md`](cpp/README.md).

---

## Credits & Third-party

This project stands on other people's work. Attribution and licensing notes (see also [`NOTICE`](NOTICE)):

- **FewTags** by **Fewdys** — `FewTagsModule` is a close derivation of the community FewTags plate technique and fetches its tag database at runtime. Keep this attribution if you redistribute a build, and check FewTags' own licence.
- **RuntimeUnityEditor** by **ManlyMarco** — **GPL-3.0**. `RuntimeEditorHost` can load an IL2CPP build of it from an embedded, gzipped copy that is *not* in this repository (`ressources/rue/`). A binary that embeds it must comply with the GPL-3.0; drop the two `<EmbeddedResource>` lines to remove the dependency.
- **Mono `mcs`** — the C# compiler RuntimeUnityEditor's REPL uses, loaded the same way.
- **BepInEx** (LGPL-2.1), **HarmonyX** (MIT), **Il2CppInterop** (LGPL-3.0) — the loader and interop foundation of the C# mod.
- **Dobby** (Apache-2.0) — the inline-hook backend the C++ engine uses through BepInEx's own `dobby.dll`.
- **js6pak/libil2cpp-archive** — the per-Unity-version `il2cpp-api-functions.h` the export remap is aligned against; fetched by tag, not committed.
- **dwgx/vrchat-il2cpp-re** — published research on VRChat's metadata encryption and Unity 6 layouts, used as a reference. No code copied.
- **Íñigo Quílez** — a retired overlay in `MenuSkinModule` is a C# port of his domain-warping fbm shader ([iquilezles.org/articles/warp](https://iquilezles.org/articles/warp)). Dead code, kept credited.
- **Reverse-engineering references** — a few modules document techniques observed in other VRChat tools, for maintainability only; no third-party code is copied there.
- **Embedded media** — soundboard and spawn clips, panel artwork, and the *Bad Apple!!* soundtrack and frames (Team Shanghai Alice and the respective artists) are the property of their owners and are **not distributed** here. `badapple.wav` in particular must be supplied as **mono 16-bit PCM WAV** — this IL2CPP build has no MP3 decoder — or its `<EmbeddedResource>` line dropped, in which case the object show runs silently on its own clock.

---

## Disclaimer

- **Unofficial.** This project is not affiliated with, endorsed by, or supported by VRChat Inc. "VRChat" and related marks belong to their owners.
- **Modding may violate the VRChat Terms of Service.** Loading third-party code into the VRChat client can breach VRChat's ToS and could put your account at risk. Use it with that understanding; you are responsible for how you run it.
- **Local-only and archival by intent.** The mod runs entirely on your own machine for preservation and quality-of-life purposes. It transmits nothing to other players in your instance — visualisations, movement and diagnostics are client-side only, and its network traffic is limited to the companion desktop client on loopback and the project's own API. It is not a tool for affecting other users' sessions, and must not be used as one.
- **No warranty.** Provided as-is. VRChat updates its obfuscated IL2CPP runtime frequently; the compatibility fixes are best-effort and will need updating when the game changes — which is exactly what this repository asks for help with.

---

## License

Released under the [MIT License](LICENSE). © 2026 KaichiSama / Kawaii Studio.

Third-party components and embedded media remain under their own licences — see [Credits & Third-party](#credits--third-party) and [`NOTICE`](NOTICE).
