# pack-port — the Unity 6 port kit

Everything needed to make BepInEx 6 IL2CPP, the C# mod and the C++ engine run on a new VRChat build,
with the data for build **25686233** (Unity 6000.0.67f1) already derived. The reasoning behind each
tool is in [`docs/UNITY6-PORT.md`](../../docs/UNITY6-PORT.md); the folder layout the tools expect is in
[`docs/LOADER-AND-PACK.md`](../../docs/LOADER-AND-PACK.md).

Nothing here downloads or redistributes anything of VRChat's or Unity's. The tools read *your* game
folder. Paths that used to point at the author's drives are placeholders now (`patch`, `patch.new`,
`$(GameDir)` / `VRCHAT_GAME_DIR`) — pass your own.

---

## What is here

| File / folder | What it does | Needs |
|---|---|---|
| `il2cpp_tables.py` | Reads `UnityPlayer.dll` and prints the **ordered** list of GameAssembly's il2cpp export names, obfuscated as they are, in canonical header order (the run of `lea reg,[rip+disp]` that resolves them by name). Read-only. | Python 3, `pefile` |
| `build_port_kit/make_remap.py` | Builds the **standard → obfuscated** export map for one build from that list and `il2cpp-api-functions.h` of the exact Unity version, skipping the profiler block release builds do not export. Refuses to emit if it disagrees with anything already proven in-game (`--check`). Writes `remap.<build>.json` and, optionally, a C++ header for the engine. | Python 3 |
| `build_port_kit/lea.25686233.json` | The ordered obfuscated export list of build 25686233 (234 entries). Entry 0 is `il2cpp_init` — the string to put into `winhttp.dll`. | — |
| `build_port_kit/remap.25686233.json` | The complete map for build 25686233. | — |
| `build_port_kit/remap.validated-184.25686233.json` | The 184 entries of that map proven in-game — the `--check` input for the next build. | — |
| `repoint/` | Repoints Il2CppInterop's P/Invoke **entry points** to the obfuscated names with Mono.Cecil, keeping the managed method names (byte-patching the string heap renames the methods too and breaks `IL2CPP.il2cpp_X()` calls). Written against the previous build's CSV map; the `ldstr "il2cpp_*"` operands that `NativeLibrary.GetExport` receives still have to be rewritten by hand or added here. | .NET 6, Mono.Cecil (NuGet) |
| `implmap/`, `exports/` | Diagnostics: read the **deployed** P/Invoke table out of an assembly and diff it against a map; dump a PE's export table and catch map entries that do not exist or that two names share (identical-code folding). Read-only. | .NET 6 |
| `measure_offsets.py`, `MAPPING_NOTES.md`, `final_mapping.csv` | Struct-layout derivation from disassembly, with the full notes for the build these were derived on (1903): `Il2CppClass` (non-stock: `name` at +0x98), `MethodInfo`, `FieldInfo`, `Il2CppImage`, with the function body that proves each offset. The **method** carries over; the offsets are per build. | Python 3, `pefile`, `capstone` |
| `VRChatStructFix/` | The BepInEx **patcher** the C# mod needs: corrects the struct offsets Il2CppInterop reads. Source recovered by decompiling the shipped DLL (the original was lost), then corrected for Unity 6 (`MethodInfo.flags` at `0x4C`, not `0x4E` — see the comment in the source). Build with `-p:GameDir=<modded folder>`. **Not needed by the C++ engine.** | .NET 6, a modded game folder |
| `tokmap/` | Reads, out of Il2CppInterop-generated assemblies, the metadata **token** each proxy method and field is bound to, and emits the token → name table that `src/Core/TokenShiftFix.cs` uses to rebind by name when VRChat reorders tokens (`ressources/tokens-<build>.tsv.gz`). | .NET 6 |
| `probe/` | A read-only BepInEx IL2CPP plugin that dumps live classes, fields and offsets from inside the game. Build with `-p:GameDir=<modded folder>`. | .NET 6, a modded game folder |
| `dmpinfo/` | Reads a Windows minidump without a debugger: exception record, faulting module, RIP/RSP, and a stack scan for return addresses inside loaded modules. | .NET 6 |
| `ongui/` | Finds `MonoBehaviour`s that declare `OnGUI()` in the interop assemblies — the cheapest one to attach restarts Unity's IMGUI loop when nothing in the scene declares it. | .NET 6 |
| `pe_exports.py`, `remap_names.py` | Smaller helpers: dump a PE export table; the first-generation remapper that mapped exports by position between two builds (superseded by `make_remap.py`, kept for reference). | Python 3, `pefile` |

---

## Procedure for a new VRChat build

### 1. Update the engine files

Overlay the new build's root binaries and `VRChat_Data/` onto a copy of the previous modded folder; keep
`BepInEx/`, `dotnet/`, `winhttp.dll`, `doorstop_config.ini`. Details and the manifest in
[`docs/LOADER-AND-PACK.md`](../../docs/LOADER-AND-PACK.md) §7.

### 2. Regenerate the export remap

```bash
# a) the ordered obfuscated export names of the new build
python tools/pack-port/il2cpp_tables.py "<pack>/UnityPlayer.dll" > lea.<build>.json

# b) the header of the EXACT Unity version (the git tag is the version)
gh api "repos/js6pak/libil2cpp-archive/contents/il2cpp-api-functions.h?ref=6000.0.67" -q .content | base64 -d > header.6000.0.67.h

# c) build, verify against what was proven on the previous build, emit
python tools/pack-port/build_port_kit/make_remap.py --header header.6000.0.67.h --lea lea.<build>.json --tag <build> \
    --check tools/pack-port/build_port_kit/remap.validated-184.25686233.json
```

If the header has more entries than the binary exports, the gap must be the profiler block
(`il2cpp_profiler_install` … `il2cpp_profiler_install_thread`). If the counts do not reconcile, do **not**
ship: compare a few disassembled function bodies (`il2cpp_object_get_class` is `mov rax,[rcx]; ret`,
`il2cpp_object_unbox` is `lea rax,[rcx+0x10]; ret`) to find the real gap. Never align by signature
similarity — it produced a map that *ran* with `il2cpp_domain_get` bound to the wrong function.

### 3. Apply it

- `winhttp.dll`: replace the string `il2cpp_init` with entry 0 of `lea.<build>.json` (11 bytes each).
- `BepInEx/core/Il2CppInterop.Runtime.dll` and `BepInEx/core/BepInEx.Unity.IL2CPP.dll`: repoint every
  `il2cpp_*` P/Invoke entry point *and* rewrite the `ldstr "il2cpp_*"` operands, with Mono.Cecil
  (`repoint/`, plus the `ldstr` pass). Verify with `implmap/` and `exports/`.
- The C++ engine: `make_remap.py --hpp <path>` writes the same table as a header; rebuild.

### 4. If the Unity version changed too

Re-derive the struct offsets (`measure_offsets.py` on the old and new `GameAssembly.dll`, following
`MAPPING_NOTES.md`), fix `VRChatStructFix/` and the engine's offsets, rebuild both. Remember the
out-parameter trap: the first `[rcx+disp]` of a function is not its field when the function writes
through a pointer argument.

### 5. Verify in-game, then ship

Start the game from the pack folder. `LogOutput.log` must reach *Chainloader initialized* with no
`EntryPointNotFound`. Then the witness run (no plugin), then the plugin. Only after a clean start does the
folder become a pack.

---

## Requirements

- Python 3.10+ with `pip install pefile capstone` (capstone only for `measure_offsets.py`).
- .NET 6 SDK for the C# tools; Mono.Cecil comes from NuGet for `repoint/`.
- A modded VRChat folder for `VRChatStructFix/` and `probe/` (`-p:GameDir=` or `VRCHAT_GAME_DIR`).
- `gh` (or any HTTP client) to fetch the il2cpp header by tag. The header is Unity's and is not committed.
