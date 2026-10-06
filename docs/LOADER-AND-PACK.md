# The pack and the loader — how the mod reaches a player's machine

The companion desktop client (VRChat Archive / Kawaii Studio app) has a **LOADER** tab. This document
explains what it installs, where, how it decides what to download, and how to build an equivalent
folder yourself for development. The pack itself is **not** in this repository: it contains the game.

Contents — [1. What the pack is](#1-what-the-pack-is) · [2. Layout](#2-layout-of-a-pack) · [3. The manifest](#3-the-manifest) ·
[4. What the LOADER tab does](#4-what-the-loader-tab-does) · [5. Installing the mod](#5-installing-the-mod-into-a-pack) ·
[6. Build your own](#6-build-your-own-test-folder) · [7. Updating for a new VRChat build](#7-updating-the-pack-for-a-new-vrchat-build) · [8. Anti-cheat](#8-a-word-on-anti-cheat)

---

## 1. What the pack is

A pack is a **complete, self-contained modded VRChat folder**: a pinned VRChat build, BepInEx 6 IL2CPP
pre-installed and already adapted to that build (export remap, patchers), doorstop, and the .NET runtime
BepInEx needs. It is served as a single `.7z` (LZMA2, solid, ~440–470 MB) by the project's site to signed-in
users, together with a tiny JSON manifest.

It is **the game plus the loader**, never the mod: `BepInEx/plugins/` is empty in every pack. The client
installs and updates mods separately from a catalogue (§4), so a pack does not have to be rebuilt when
a plugin changes.

Why ship the whole game instead of overlaying BepInEx onto the Steam install? Because Steam auto-updates
its copy. A new VRChat build silently overwrites a modded one, and until the remap and the patchers are
redone for that build the result is a half-modded game that does not load. Keeping the modded game in its
**own folder** means the official install stays untouched and playable, and the modded one stays exactly
as shipped until the project ships the next pack.

Versions so far: v14 (Aug 2026) … v16 (Sep) on Unity 2022.3 builds; **v17** (4 Oct 2026) on build
**25686233**, the first Unity 6 build. The next pack is **v18**.

---

## 2. Layout of a pack

```
<pack>/
  VRChat.exe, GameAssembly.dll, UnityPlayer.dll, UnityCrashHandler64.exe, baselib.dll   ← the pinned build
  VRChat_Data/                                 ← the build's data, including
    il2cpp_data/Metadata/global-metadata.dat   ← encrypted by VRChat (see docs/UNITY6-PORT.md §3)
  winhttp.dll                                  ← doorstop; the il2cpp_init string renamed to the build's obfuscated export
  doorstop_config.ini
  dotnet/                                      ← the runtime BepInEx IL2CPP needs
  BepInEx/
    core/                                      ← BepInEx 6 IL2CPP be.788; Il2CppInterop.Runtime.dll and BepInEx.Unity.IL2CPP.dll
                                                 with their il2cpp_* P/Invokes repointed to this build's export names
    config/BepInEx.cfg                         ← UpdateInteropAssemblies = false (interop is frozen on purpose)
    interop/                                   ← the generated interop assemblies of the last build that could generate them
    patchers/VRChatStructFix.dll               ← struct-layout patcher; needed by the C# mod, NOT by the C++ engine
    plugins/                                   ← EMPTY in the pack — the client installs the mod here
```

The pack's "signature" for the client is `winhttp.dll` + `BepInEx/` at the root: no vanilla install has
them, every pack does. An extracted folder lacking either is rejected as not-a-pack.

---

## 3. The manifest

A small JSON file next to the archive. The client polls it (authenticated, as the signed-in user) and
compares `version` with what it has installed:

```json
{
  "version": "17",
  "url": "/download/vrchat-pack.7z?v=17",
  "size": 465219960,
  "sha256": "<hex SHA-256 of the archive>",
  "notes": "v17: rebuilt on VRChat build 25686233. Engine binaries and il2cpp global-metadata swapped to the new build; the struct-layout fix re-adapts at runtime. Mod is not bundled: the client installs and updates it on its own."
}
```

- `version` is a plain integer string, compared as a number. `url` is relative to the site; the `?v=`
  only defeats caches.
- `size` and `sha256` are verified **before** extraction. A hash mismatch is a hard failure: the archive
  replaces the game's binaries, so nothing unverified is ever unpacked.
- **The file must be UTF-8 without a BOM.** The server returns it byte-for-byte; a BOM makes the JSON
  unparseable and the client silently keeps showing the previous pack. PowerShell's `Out-File`,
  `>` and `Set-Content` add a BOM by default on Windows — write the manifest from Python
  (`open(path, "wb").write(json.dumps(...).encode("utf-8"))`) or any tool you have checked.

---

## 4. What the LOADER tab does

**INSTALL / UPDATE** — reads the manifest; downloads the archive (resumable, HTTP range); verifies
`sha256`; extracts it into the modded-game folder (`<client data>/patch` by default, overridable so the
~1.2 GB can live elsewhere); records what it wrote so **UNINSTALL** can remove exactly that. The Steam
install is never read from or written to.

**Mods** — the client keeps a catalogue of mods (name, file, `sha256`, kind: single DLL, zip or 7z of a
plugin folder). Installing one downloads it, verifies the published `sha256`, and places it under
`BepInEx/plugins/` (a folder-kind mod lands in `plugins/<folder>/`). The installed hash is kept so an
update is detected by hash, not by version string. **ADD CUSTOM PLUGIN** drops a user-supplied DLL into
`plugins/` the same way and records it.

**START** — launches `VRChat.exe` from the modded folder. The client also drives the mod while the game
runs: the mod is an HTTP *client* that polls the desktop app's local server on `127.0.0.1:8791`
(`/mod/schema`, `/mod/sync`), which is how settings, roster and events reach the client's pages.

---

## 5. Installing the mod into a pack

| | C# mod (`src/`) | C++ engine (`cpp/`) |
|---|---|---|
| File in `BepInEx/plugins/` | `VRChatArchiveMod.dll` (the plugin, ~25 MB with embedded media) | `VRChatArchiveMod.dll` (the one-file loader with the engine embedded, ~1 MB) |
| Also needs | `BepInEx/patchers/VRChatStructFix.dll`, `BepInEx/interop/` from the last Unity 2022 build, `UpdateInteropAssemblies = false` | nothing else; **remove** `patchers/VRChatStructFix.dll`, it only produces interop noise |
| Writes at runtime | BepInEx config in `BepInEx/config/`, its data under `BepInEx/VRChatArchiveMod/` | the engine to `BepInEx/VRChatArchive/VRChatArchiveEngine.dll` (re-extracted only when the bytes differ) |
| Log | `BepInEx/LogOutput.log` | `BepInEx/LogOutput/VRChatArchive.log` (+ the chainloader lines in `LogOutput.log`) |
| BepInEx GUID | `org.vrchatarchive.mod` | `org.vrchatarchive.mod.cpp` |

**They share a file name. Never put both in `plugins/`.** The client's loader lists every `.dll` in
`plugins/` as a mod, which is why the native engine is extracted *beside* `plugins/`, not into it: the
user sees exactly one entry.

The game must be closed while you copy: BepInEx holds plugin DLLs open.

---

## 6. Build your own test folder

You need a legitimate Steam copy of VRChat. Nothing here is downloaded from this project.

1. **Copy, do not modify.** Copy your whole Steam VRChat folder (`steamapps/common/VRChat`) to a new
   folder — that is your pack. Never mod the Steam copy; Steam will overwrite it.
2. **Install BepInEx.** Extract `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788` (or newer, from
   builds.bepinex.dev) over the folder. Start the game once with `--no-vr` and stop it: on a build whose
   exports are *not* obfuscated you would now have `BepInEx/LogOutput.log` and a generated `interop/`.
   On a VRChat build you will have nothing, because of the next step.
3. **Apply the export remap for your build** (see [`tools/pack-port/README.md`](../tools/pack-port/README.md)):
   - get the ordered export list: `python tools/pack-port/il2cpp_tables.py <pack>/UnityPlayer.dll > lea.<build>.json`;
   - fetch `il2cpp-api-functions.h` for the exact Unity version from `js6pak/libil2cpp-archive` (the git
     tag is the version) and run `make_remap.py` → `remap.<build>.json`. For build 25686233 both files
     are already in `tools/pack-port/build_port_kit/`;
   - in `winhttp.dll`, replace the string `il2cpp_init` with the first entry of `lea.<build>.json`
     (same length, 11 bytes, no padding needed);
   - repoint the `il2cpp_*` P/Invoke entry points **and** the `ldstr "il2cpp_*"` operands in
     `BepInEx/core/Il2CppInterop.Runtime.dll` and `BepInEx/core/BepInEx.Unity.IL2CPP.dll` with Mono.Cecil
     (`tools/pack-port/repoint` does the entry points).
   Start the game: `LogOutput.log` must now say *Running under Unity 6000.0.67f1* and reach
   *Chainloader initialized*.
4. **For the C# mod only:** set `[IL2CPP] UpdateInteropAssemblies = false` in `BepInEx/config/BepInEx.cfg`;
   copy `BepInEx/interop/` from an install of the last Unity 2022 build (the project's v16 pack had one);
   build `tools/pack-port/VRChatStructFix` against the pack (`-p:GameDir=<pack>`) and drop the DLL in
   `BepInEx/patchers/`. Expect `Field X was not found on class Y` lines from Il2CppInterop — that is the
   frozen interop binding against the live metadata, and it is the price of this route.
5. **For the C++ engine:** nothing more. Build it (see `cpp/README.md`) and drop the loader in `plugins/`.
6. **Run.** `VRChat.exe --no-vr` from the pack folder. Read `BepInEx/ErrorLog.log` (empty = no fatal
   error) and the logs listed in §5. Run the witness (no plugin) before blaming the mod.

Memory matters on repeated launches: VRChat reserves ~4 GB of **commit** at start-up, and repeated test
launches on a machine with a small pagefile exhaust commit (not RAM — Task Manager's "75.1/77.6 GB" is
commit) and take the GPU/audio drivers down with it. Check free commit before a run, and stop launching
below ~3 GB.

---

## 7. Updating the pack for a new VRChat build

What breaks on each kind of update, and what repairs it:

| Change | Symptom | Repair |
|---|---|---|
| il2cpp export names (every build, Beebyte) | BepInEx silent / `EntryPointNotFound`; the C++ engine logs *GameAssembly is not loaded or is missing its il2cpp exports* | regenerate the remap (`il2cpp_tables.py` + `make_remap.py`), re-apply it to `winhttp.dll` and the two core DLLs |
| VRChat class names (`VRC.Player`, `VRCPlayer`, …) | classes "not found", access violations in `il2cpp_runtime_class_init` | nothing to rebuild: the C++ engine recovers them by structure (`recovery.cpp`); the C# mod relies on its adaptation layer |
| il2cpp struct layouts (only when the **Unity version** changes) | wrong field reads, every static method reads as non-static, forms lose their `@` | re-derive the offsets (`measure_offsets.py`, `MAPPING_NOTES.md`), rebuild `VRChatStructFix`, update the C++ engine's offsets |

The engine overlay itself is additive: copy the new build's root binaries (`GameAssembly.dll`,
`UnityPlayer.dll`, `UnityCrashHandler64.exe`, `VRChat.exe`, `baselib.dll`) and `VRChat_Data/` over the
previous pack, keep everything under `BepInEx/`, `dotnet/`, `winhttp.dll` and `doorstop_config.ini`, then
redo the remap. Package only after a clean in-game start, with `7z a -t7z -mx=9 -md=16m -ms=on`, and
publish archive + manifest together (keep the previous archive as `vrchat-pack.v<N-1>.7z` for rollback).

---

## 8. A word on anti-cheat

VRChat ships Easy Anti-Cheat, and BepInEx cannot load under it. How a launcher starts the game is
outside the scope of this repository: nothing here helps with it, nothing here is required for it, and
running a modded client is against VRChat's Terms of Service — see the README's Disclaimer. This
repository is about what the mod does once it is loaded, and about keeping that working across Unity
versions.
