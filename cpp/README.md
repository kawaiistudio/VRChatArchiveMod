# The native engine (C++)

The rewrite of VRChat Archive Mod as a **native engine**. It exists because the C# mod depends on
Il2CppInterop's generated assemblies, and on VRChat build 25686233 (Unity 6) those cannot be regenerated:
the mod runs on assemblies frozen on an older build, and every member Unity 6 inlined or VRChat renamed
has to be worked around by hand. A native engine reads il2cpp directly and does not have that problem —
see [`docs/UNITY6-PORT.md`](../docs/UNITY6-PORT.md) §7.

Status: boots on 25686233 with neither Il2CppInterop nor the struct-layout patcher; 35 modules registered,
a few verified in-game (see [`docs/MODULES.md`](../docs/MODULES.md#the-c-engine-cpp)). The QuickMenu
"wings" UI is not yet a faithful copy of the C# original. **Help wanted on both counts.**

---

## Layout

```
cpp/
  native/                      the engine — VRChatArchiveNative.dll
    VRCArchiveNative.vcxproj   Visual Studio 2022, toolset v143, C++20, x64 only
    include/*.hpp              one header per subsystem / module
    src/*.cpp
  loader/                      the managed half — VRChatArchiveMod.dll
    Loader.cs                  a BepInEx IL2CPP plugin of one class
    VRChatArchive.Loader.csproj
```

**One file is deployed.** The native DLL is embedded in the loader as a resource; at load the loader
writes it to `BepInEx/VRChatArchive/VRChatArchiveEngine.dll` (only when the bytes differ), `LoadLibrary`s
it and calls `VRCA_Start(dataDir, logDir)`. The engine does not go in `plugins/` because the desktop
client lists every `.dll` there as a mod, and the engine is not a mod — it is the other half of this one.

## Build

```bash
# 1. the engine
msbuild cpp/native/VRCArchiveNative.vcxproj -p:Configuration=Release -p:Platform=x64
#    → cpp/native/x64/Release/VRChatArchiveNative.dll

# 2. the loader (embeds the DLL built above; links against the BepInEx in your modded game folder)
dotnet build cpp/loader -c Release -p:GameDir="<your modded VRChat folder>"
#    or set VRCHAT_GAME_DIR instead of -p:GameDir
#    → cpp/loader/bin/Release/VRChatArchiveMod.dll
```

The loader references `BepInEx.Core`, `BepInEx.Unity.Common` and `BepInEx.Unity.IL2CPP` from
`<GameDir>/BepInEx/core/` — never from NuGet, because BepInEx 6 IL2CPP is not version-tolerant. If the
engine DLL is missing when the loader builds, the build still succeeds and the loader falls back to a
loose `VRChatArchiveEngine.dll` beside it — the developer loop.

## Deploy and run

1. Copy `VRChatArchiveMod.dll` to `<modded VRChat>/BepInEx/plugins/`. The game must be closed.
2. **Do not** deploy `BepInEx/patchers/VRChatStructFix.dll` with it, and do not deploy the C# mod's
   `VRChatArchiveMod.dll` next to it (same file name, and two bridges would fight for the client).
3. Start the game. Read `BepInEx/LogOutput/VRChatArchive.log`; the chainloader's own lines are in
   `BepInEx/LogOutput.log`, fatal errors in `BepInEx/ErrorLog.log`.

How to build the modded folder itself: [`docs/LOADER-AND-PACK.md`](../docs/LOADER-AND-PACK.md) §6.

## Boot sequence (`src/dllmain.cpp`)

`DllMain` does nothing but opt out of thread notifications; real work waits for `VRCA_Start`, called by
the loader *after* the chainloader has run and `GameAssembly.dll` is up.

1. `Log::Init` — the engine's own log file.
2. `Il2::Init` — finds `GameAssembly.dll`, resolves the `il2cpp_*` exports (standard name first, then the
   validated remap table for this build), attaches the thread. The one hard failure: if this fails, stop.
3. A smoke test: `FindClass("System.Object")` proves the live metadata is walkable.
4. `Hooks::Init` — resolves `DobbyHook` / `DobbyDestroy` from the `dobby.dll` BepInEx already loaded.
5. `ObfSelfTest` — proves the compile-time string hiding round-trips before any hidden target name is used.
6. `Recovery::RecoverCoreClasses` — re-finds VRChat's renamed classes (`VRC.Player`, `VRCPlayer`, …) by
   their **structure** and registers aliases, so every module can ask for them by their old names.
7. Module registration, then `Engine::Start` — the native frame pump, a detour on `EventSystem.Update`
   (a `void` method, never a coroutine, which is why it is safe to return from).

## Subsystems

| Header | Role |
|---|---|
| `il2cpp.hpp` | Talks to GameAssembly's exported API and reads il2cpp structures at measured offsets. `FindClass("Namespace.Name")` on the live metadata (namespace-qualified lookups never fall back to another namespace's short name — that is how SteamVR's `Player` gets mistaken for `VRC.Player`); `RegisterAlias` for recovered classes; method / field lookup by name, arity and staticness. |
| `recovery.hpp` | Class recovery by fingerprint: a class holding a field of each given SDK type; the `VRC.Player` / `VRCPlayer` pair found by mutual reference and the static self-typed field on `VRCPlayer`. |
| `hooks.hpp` | Inline hooks on dobby. `Create(target, detour, &original)`; a convenience that resolves an il2cpp method by name and detours its compiled entry point. Hooks only ever go through the native method pointer, never through il2cpp delegates. |
| `engine.hpp`, `module.hpp` | The module list and the pump. A `Module` is a name, an on/off state and a few callbacks (`OnUpdate`, `OnLateUpdate`, enable/disable, player join/leave). |
| `config.hpp` | The settings registry. A module *binds* its fields (section, key, type, description, range); the bridge builds the schema and the sync payload from the registry, so anything a module binds appears in the client automatically — exactly as the C# `ModConfig` did. |
| `bridge.hpp` | The HTTP client of the desktop app (`127.0.0.1:8791`): `/mod/schema`, long-polled `/mod/sync`, the action registry. The two decryptor / Dex sync fields of the private build are omitted here; see the note in `bridge.cpp`. |
| `player.hpp`, `world.hpp` | The local player and the roster (through the recovered classes); the current world and instance read from VRChat's own log file, shared-read. |
| `wings.hpp`, `screenui.hpp`, `quickmenu.hpp` | The UI: QuickMenu cards, the wing panels, overlay text. The wings are a port of the C# `PanelSkin` (generated nine-sliced sprites, the violet→pink border glow, the outline material, the donor font) and the port is **not finished** — compare against the C# source, value for value, not from memory. |
| `obf.hpp` | Compile-time hiding of sensitive target-name strings; `obf_selftest.cpp` proves it at boot. |

## Rules that are not optional on this build

- **An access violation is not catchable.** Anything that *might* touch an absent class or method is
  verified first (class present in the live metadata, pointer readable), never wrapped in a try/catch.
- **Members move from property to field** on an obfuscated class. If `FindMethod("get_X")` fails, dump
  the fields before declaring a feature dead.
- **A detour returns what the original returns.** `Start()` can be a coroutine; a made-up return value
  crashes the engine after the detour has returned.
- **Prefer scene proof to a fingerprint.** The obfuscator renames code, not scene object names; the tree
  dump (`UiDumpModule`) recovers a renamed class from where it is attached.
- **Every created UI object gets `localScale = (1,1,1)` and identity rotation.** The QuickMenu hierarchy
  has a negative X scale; inherit it and your text reads mirrored.
- **Stretch children, do not hard-size them**, or they measure zero and the backgrounds vanish.
- Run the **witness** (game without the plugin) before blaming the engine for an exit.

## Not in this repository

The plaintext-cache decryptor and the Dex patcher of the private build. Both are server-gated paid
features injected at runtime; their modules, sync fields and arming handshakes were removed before
publication (`dllmain.cpp`, `bridge.cpp`, the project file). The engine runs without them.
