# Porting VRChat Archive Mod to Unity 6 — what broke, what was fixed, what is still open

This is the engineering record of the port to VRChat build **25686233** (2 October 2026), the first
build on **Unity 6 (6000.0.67f1)**. It is written for a contributor who wants to pick up where we
stopped. Every statement below was observed on this build; where something is a guess it says so.

Contents — [1. The build](#1-the-build) · [2. Making BepInEx boot](#2-making-bepinex-boot) ·
[3. The metadata dead end](#3-the-metadata-dead-end) · [4. Making the C# mod load](#4-making-the-c-mod-load) ·
[5. What remains broken](#5-what-remains-broken-in-the-c-mod-by-impact) · [6. Identification rules on this build](#6-identification-rules-on-this-build) ·
[7. The native route](#7-the-native-route-cpp) · [8. Do not retry](#8-things-not-to-retry) · [9. Tools](#9-where-the-tools-are)

---

## 1. The build

| | Previous | 25686233 |
|---|---|---|
| Unity | 2022.3.22f2 | **6000.0.67f1** (read from `VRChat_Data/globalgamemanagers`) |
| `GameAssembly.dll` | ~221 MB | 243 301 376 bytes |
| il2cpp exports | 232, obfuscated | **234**, obfuscated again — a fresh set of names (Beebyte, per build); 13 names in common with the old set, all moved |
| `global-metadata.dat` | encrypted | encrypted with a **different** scheme (magic `0x404d457b`, "{EM@") |
| il2cpp struct layouts | measured for 2022.3 | changed — hard-coded offsets in the struct-layout patcher went stale |

Three unrelated breakages land at once when the Unity version changes. A VRChat update that stays on
the same Unity version normally changes only the export names and the obfuscated class names.

What VRChat also removed or changed in this build, found one by one while porting:

- `VRC.SDKBase.VRCPlayerApi` no longer exposes `get_isLocal`, `get_gameObject` or `get_displayName`
  (they resolve to null, nothing throws).
- Nameplates are **GPU-instanced**: `NameplateManager` is in the scene with no children, its component
  holds two `GraphicsBuffer`s, two `NativeArray<Vector4>`, an atlas, two materials and a slot pool. There
  is no nameplate GameObject anywhere in the 25 276-node UI dump. Cloning a TMP under a nameplate is dead.
- The **Box Drop** seam on `FlatBufferNetworkSerializer` is gone: not one method with a `Vector3`
  parameter is left on it, through the whole chain.
- `Mathf.Max/Min/Abs/Clamp` and friends, and **every** `UnityEngine.Component.GetComponent*`, are absent
  from the live metadata: il2cpp inlined them. (`GameObject.GetComponent*` survive — they are native externs.)
  Rule of thumb: *a trivial forwarder is inlinable, a native extern is not.*

---

## 2. Making BepInEx boot

### The symptom

The game and the pack ran, but BepInEx produced nothing: no `LogOutput.log`, `interop/assembly-hash.txt`
never regenerated, vanilla game. Doorstop is non-fatal, so nothing announced the failure.

### Cause 1 — BepInEx's Il2CppInterop predates Unity 6

BepInEx 6.0.0 ships Il2CppInterop 1.5.3, which cannot read Unity 6 il2cpp. Fix: move to the bleeding-edge
build **`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788`** (builds.bepinex.dev; #755 added metadata v23-106 /
Unity 6). Replace `core/`, `dotnet/` and doorstop; purge `interop/` and the cache.

### Cause 2 — doorstop looks `il2cpp_init` up by name

`winhttp.dll` (doorstop) hooks `il2cpp_init` **by its export name**, and VRChat obfuscates it, so the CLR
never starts. Fix: replace the 11-byte string `il2cpp_init` inside `winhttp.dll` with the build's
obfuscated name for it. `il2cpp_init` is **index 0** of the canonical export order, so it is the first
entry of the ordered export list (`tools/pack-port/build_port_kit/lea.25686233.json`). After this one
change `LogOutput.log` appears with *Running under Unity 6000.0.67f1*.

### Cause 3 — Il2CppInterop's P/Invokes name the standard exports

`be.788` is stock: `Il2CppInterop.Runtime.dll` P/Invokes `il2cpp_domain_get`, `il2cpp_register_log_callback`
and ~190 more by their real names → `EntryPointNotFoundException`, one after another.

**How to build the map.** The names change every build, but the **order** does not: `UnityPlayer.dll`
resolves GameAssembly's exports through one contiguous run of `lea reg,[rip+disp]` instructions whose
targets are the export-name strings, emitted in **header order**. `il2cpp_tables.py` reads that run
(the three names that ship in clear — `il2cpp_free`, `il2cpp_native_stack_trace`,
`il2cpp_gchandle_get_target` — act as anchors). The header order itself comes from
`il2cpp-api-functions.h` **of the exact Unity version**, which `js6pak/libil2cpp-archive` keeps tagged by
version (`6000.0.67`). Validation: `il2cpp_free` = 17, `native_stack_trace` = 84, `gchandle_get_target` = 123
in both lists.

The header declares **241** functions and the binary exports **234**. The seven missing ones are the
**profiler block**, `il2cpp_profiler_install` … `il2cpp_profiler_install_thread` (header positions
151–157), which release builds do not export. So: `header[k] → lea[k]` for `k ≤ 150`, gap, and
`header[k] → lea[k-7]` for `k ≥ 158`. Spot-checked against function bodies:
`il2cpp_method_get_name` (138 → 138) is `mov rax,[rcx+0x20]; ret`, `il2cpp_object_get_class` (163 → 156)
is `mov rax,[rcx]; ret`, `il2cpp_object_unbox` (167 → 160) is `lea rax,[rcx+0x10]; ret`.
`make_remap.py` builds the map, refuses to emit if it disagrees with anything already proven in-game
(`--check remap.validated-184.25686233.json`), and writes `remap.<build>.json`.

**How to apply it — the trap.** Do **not** overwrite the name bytes in the assembly's `#Strings` heap:
the P/Invoke entry-point string and the managed method's own name are the *same* string, so a byte
patch renames the managed method and BepInEx's `IL2CPP.il2cpp_X()` calls die with
`MissingMethodException`. Use **Mono.Cecil**: (a) set each `MethodDefinition.PInvokeInfo.EntryPoint` to the
obfuscated name, keeping the method name; (b) rewrite the `ldstr "il2cpp_X"` operands that both
`BepInEx.Unity.IL2CPP.dll` and `Il2CppInterop.Runtime.dll` pass to `NativeLibrary.GetExport`
(190 entry points + 9 `ldstr` in Il2CppInterop.Runtime, 2 `ldstr` in BepInEx.Unity.IL2CPP).
`tools/pack-port/repoint` is the entry-point repointer written for the previous build; the `ldstr`
rewrite is the part still to add to it.

**A false lead that cost a day.** Aligning old and new export lists by **signature similarity**
(Needleman-Wunsch over function bodies) "resolved" 87 names and *ran* — until `il2cpp_domain_get` turned
out to be bound to a different function (header position ~75, the alignment had put it at 13). No
crash, wrong function, subtle corruption later. Never ship a guessed map; the header order is the only
reliable reference.

**Result.** `Chainloader initialized`, no `EntryPointNotFound`, the per-invoke `method_get_name` hook
survives, and the game runs into a world with BepInEx active.

---

## 3. The metadata dead end

Regenerating the interop assemblies for Unity 6 needs a decrypted `global-metadata.dat`
(`BepInEx.cfg` already points `GlobalMetadataPath` at `BepInEx/global-metadata.rebuilt.dat`). We do
not have one for this build, and the paths tried are closed:

- **MetaDump** (our patcher that scans process memory for the decrypted header `FAB11BAF`+version)
  worked on 2022.3 and finds **nothing** on Unity 6 — a full 6 GB scan, game in a world, ~33 hits, all
  noise. The decrypted metadata is not kept as a contiguous `FAB11BAF` blob in memory any more.
- `dwgx/vrchat-il2cpp-re` publishes an **offline** decryptor (header XOR `key[i] = (i - 0x34) & 0xFF`,
  then seven sections XORed with keystreams derived from the header's size fields) — for the **May 2026**
  build (6000.0.60). On the October file the header keystream is not linear and no variant of that
  scheme matches: VRChat changed the encryption between May and October.
- The decryption routine cannot be located in `GameAssembly.dll` by string or by the `0xFAB11BAF` /
  `0x404d457b` immediates (Beebyte obfuscates strings, the magics are not literal). A call-graph walk from
  `il2cpp_init` with capstone reaches ~500 functions and misses it (indirect calls). It needs IDA or
  Ghidra with real cross-references, or a precise hook on the loader to capture the buffer right after
  decryption.

**And it turned out not to be necessary to make the mod load** — see the next section. It *is*
necessary for the real fix to items 1 and 2 of §5. If you take this on: the encrypted file is
`VRChat_Data/il2cpp_data/Metadata/global-metadata.dat` (~38 MB, magic `0x404d457b`); a decrypted one
starts with `0xFAB11BAF` and metadata version 31; drop it at `BepInEx/global-metadata.rebuilt.dat` and
set `UpdateInteropAssemblies = true` once.

---

## 4. Making the C# mod load

The route that works: **the interop assemblies of the last Unity 2022 build (kept frozen with
`UpdateInteropAssemblies = false`) plus the `VRChatStructFix` patcher**, with the mod's own adaptation
layer (`MissingTypeGuard`, `TokenShiftFix`, `MemberAlign`, `ProxyGuard`, `ObfuscatedClassFinder`) doing the
rebinding at startup. Result on 25686233: `Chainloader startup complete`, every module initialised,
`ErrorLog.log` empty.

### The seven bugs, each of which killed the process

1. **`VRChatStructFix`: `MethodInfo.flags` is the u16 at `0x4C`, not `0x4E`.** `il2cpp_method_get_flags`
   is `uint16 f(MethodInfo*, uint32* iflags)`: it *writes* `word[0x4E]` through the out-parameter and
   *returns* `word[0x4C]`. Taking the first `[rcx+disp]` of a function as its field offset is wrong as
   soon as the function has an out-parameter. `il2cpp_method_is_instance` (`byte[0x4C] >> 4 & 1`, inverted)
   settles it. With the 2-byte shift every static method read as non-static, so hundreds of correct
   members were rejected. Verified layout: token u32 @0x48, flags u16 @0x4C, iflags u16 @0x4E, slot u16
   @0x50, parameters_count u8 @0x52. The rest of StructFix (MethodInfo class @24, name @32, return_type
   @40; Il2CppClass name @152, parent @120, declaring @128, flags @280, instance_size @248; Il2CppImage
   name @0, assembly @24) matches the binary.
2. **`MemberAlign.CalibrateMethodPointer` measured itself on witnesses resolved *by the interop*** —
   circular on a broken build, so calibration failed and `Callable()` silently returned `true` for
   everything. Fixed by sampling *live* methods through `il2cpp_class_get_methods` on
   `Il2CppSystem.Object` / `String` / `UnityEngine.Object` and taking the majority offset (≥ 70 %).
3. **`TokenShiftFix.Rebind` read the owning class from the pointer it was repairing.** A stub pointer
   has `Class = 0` → the field was abandoned (1 331 "stubs"). Use
   `Il2CppClassPointerStore.GetNativeClassPointer(proxy)` instead. Stubs: 1 331 → ~460.
4. **Route by NAME** (the big win, ~4 400 members). VRChat obfuscates only *its* assemblies; engine types
   keep their names. The generated field `NativeMethodInfoPtr_<name>_<Access>_…` carries the real name, so
   index the live class by `name + arity + static`. `_ctor` / `_cctor` → `.ctor` / `.cctor`; arity for
   overloads comes from the proxy method (`ProxyGuard.NativeSlot` inverted).
5. **No real method may be "neutral".** `Unresolvable` used to return a harmless real method
   (`Time.get_realtimeSinceStartup`). The prefix knows only (class, token), never the caller's signature,
   which reads the return value per *its* declaration — a float handed to `get_unityVersion` is
   dereferenced as a string. Return the interop's own stub (a catchable exception) instead.
6. **`ResolveByProxySignature` was blind to primitives** (`ClassOf` only knew reference types):
   `Vector3(f,f,f)` vs `(f,f)`, `Max(float,float)` vs `Max(int,int)` were indistinguishable. Compare
   primitives by the name of their il2cpp class (`Single`, `Int32`, …). The same blindness covered engine
   **structs** (`Rect`, `Vector3`, `Color`) and `string`; fixing it took `UnityEngine.GUI` from
   "52 anchors, 0 usable" to 50/52 usable and removed the `GUI.Label(Rect, string)` crash.
7. **One validation gate.** Whatever the route, a pointer must satisfy `SlotMatchesProxy` (arity,
   staticness, nameable slots) before it is written; otherwise it is neutralised.

### What Unity 6 removed, and the shims

- `Mathf.*` → `VRChatArchiveMod.Mathf`, a fully managed shim in the mod's **root namespace**. C# resolves
  enclosing namespaces before `using` directives, so all 397 call sites switched without an edit — and
  397 il2cpp round-trips disappeared.
- `Component.GetComponent*` → `LiveComponents`: `*Safe` extensions that route any `Component` receiver
  through its `.gameObject` (whose externs survive).

### The trap that kills without a frame on the stack

`Il2CppType.Of<T>()` for a type absent from the live metadata returns a **corrupted** object — not
null, not an exception, a pointer into non-live memory (or `MissingTypeGuard`'s `System.Object`
placeholder). Passing it to any native API (`Resources.FindObjectsOfTypeAll`,
`Object.FindObjectsByType`, `GetComponent(Type)`) is an uncatchable access violation, **and querying il2cpp
about it crashes just as hard** (`il2cpp_class_from_system_type` died inside the guard). Validate with
`VirtualQuery` (`NativeGuard.IsReadable`): readable pointer → `Il2CppType*` at +0x10 →
`il2cpp_class_from_type` → walk parents up to `UnityEngine.Object`. That is `Live.UsableType`; every scene
search goes through `Live.*`.

A hook also requires that **the bridge can marshal every parameter**: Il2CppInterop converts each
argument to its proxy type *before* the prefix runs, so a parameter whose il2cpp class does not resolve
kills the process inside the bridge, with no mod frame on the stack. `ProxyGuard.PatchPrefix` refuses a
hook whose proxy parameters lack a real il2cpp class — six were refused (§5).

### The witness test

Without the plugin, launched directly from the pack folder, this build dies at ~35 s on the owner's
machine; with the plugin it stays up 61 s and 101 s, enters a world, captures Photon event 33, and
`ErrorLog.log` has no *Fatal error*. The mod now outlives the vanilla run in the same harness, which is
what proves the remaining exits are the environment. **Always run the witness before blaming the mod.**

---

## 5. What remains broken in the C# mod, by impact

1. **`UnityEngine.GUIStyle::set_fontSize` — "ICall was not resolved".** Catchable, but it takes down the
   whole IMGUI HUD (Watchlist, Instance Panels, Active-Features HUD). This is Il2CppInterop's icall
   resolution by signature name, not our token layer. The most valuable fix available.
2. **Generics.** `Il2CppSystem.Collections.Generic.List<T>` and `MethodInfoStoreGeneric` throw
   `TypeInitializationException` (catchable). Hence `VRCPlayerApi.AllPlayers` is empty, `ApiContentModel<T>`
   getters cannot be read, `StructWrapper<byte>` in Voice Mimic fails; its Photon watch is **disabled on
   purpose** for that reason (receive and send still work).
3. The real fix for 1 and 2 is **regenerating the interop for this build**, which brings §3 back.
4. **`VRCPlayerApi.get_isLocal / get_gameObject / get_displayName`** are gone. The routes that work (all
   verified in-game, all implemented in `cpp/`, none yet in `src/`):
   - local player: `VRCPlayer` has a **static field of its own type** holding the local `VRCPlayer`; the
     `VRC.Player` whose `VRCPlayer` field equals it holds the local `VRCPlayerApi` (field @144);
   - `isLocal`: compare against that;
   - a player's `GameObject`: go through the `VRC.Player` component (a real `UnityEngine.Component`);
   - `displayName`: a **field** named `displayName` on an object `VRC.Player` holds — find it by member
     name. A blank name is not cosmetic: the desktop client skips nameless rows everywhere, so a roster
     without names reads as "nobody here". That is what broke Force Clone;
   - the `avtr_` id: on the **`ApiAvatar`** object the player holds (its `get_id`). The field offset is **not
     fixed** (seen @328 on `VRCPlayer` and @48 on `VRC.Player`) and the object is only attached once the
     avatar has loaded, so a probe must **retry** (1 Hz, ~20 tries), never fire once during the join.
5. **Six refused hooks** (`ProxyGuard`): `ApiAvatar.SetApiFieldsFromJson` (`IReadOnlyDictionary`),
   the Photon hooks (`ParameterDictionary`, `RaiseEventOptions`), `UnityWebRequestAssetBundle.GetAssetBundle`
   (`Nullable`).
6. **Nameplates** (GPU-instanced) — FewTags / VaTags plates have nothing to clone. The C++ engine draws
   tags in the overlay above the head, through the ESP's world-to-screen projection.
7. **Box Drop** — no seam left on the serializer. Not worth re-hunting on this build.

---

## 6. Identification rules on this build

- **What C# read as a PROPERTY is often a FIELD here.** On an obfuscated class the member *name* often
  survives while the property does not. `FindMethod("get_X")` returns null and the feature declares
  itself dead while the field `X` is right there. Seen three times in one day: `UdonBehaviour._program`
  (`IUdonProgram`, field @104, no getter — blocked every Udon variable), `FlatBufferNetworkSerializer.RequireFastRate`
  (field @248, no property — Fast Sync inactive), the player's `ApiAvatar` (field, variable offset).
  **Reflex: when `get_X` fails on an obfuscated class, dump the fields before concluding.**
- **What looks like an array may not be one.** `GetPrograms` / `GetSymbols` return `ImmutableArray<T>`:
  a boxed **struct** whose only field is the real array, at +0x10. Measuring the box gives 0 — or a random
  length. Test the **class** (`ClassName` ends in `[]`), never the length.
- **Identify by shape and value, never by name**: `usr_`, `avtr_`, `file_`, `grp_`, `standalonewindows`,
  known trust ranks; classes by field types and method parameters.
- **Read the scene, not only the metadata.** The obfuscator renames code, never scene object names. A
  dump of the Unity tree with each component's il2cpp class recovers a renamed class from *where it is
  attached*, without an instance in hand. Prefer that proof to a shape fingerprint whenever a scene name
  exists — a fingerprint pointed at the wrong class once, and the tree dump is what showed nameplates
  no longer exist.
- **Deriving an offset from a disassembly:** never take the first `[rcx+disp]` of a function that has an
  out-parameter (see `MethodInfo.flags`).
- **A native detour must return what the original returns.** `Start()` on a `MonoBehaviour` can be a
  coroutine; handing back a made-up RAX crashes the engine *after* the detour returns.

---

## 7. The native route (`cpp/`)

The C# mod needs Il2CppInterop (frozen) and `VRChatStructFix` to exist at all, and both are the source
of every *Field X was not found on class Y* line in the log — remove `patchers/VRChatStructFix.dll` and
there are **zero** Il2CppInterop lines, because nothing forces the generated assemblies to bind. The
native engine needs neither: it talks to `GameAssembly.dll`'s exports directly, reads il2cpp structures
at its own measured offsets, resolves exports through the same remap table, and finds classes in the
*live* metadata — so a renamed or removed member resolves to **null**, not to a corrupted pointer. The
engine was verified loading, resolving the local player and installing its QuickMenu cards **without**
StructFix on this build.

What it still pays per build: the export remap (automated, §2) and, when the Unity version changes, the
struct offsets. What it does not get for free: the C# UI. Rebuilding the QuickMenu wings faithfully, from
the C# source, is open work (see `cpp/README.md`).

---

## 8. Things not to retry

- Signature / Needleman-Wunsch alignment of export lists, or the order of `MethodDef`s in the ImplMap
  (both non-monotone, both produced wrong bindings that ran).
- Byte-patching export names inside a managed assembly's string heap.
- MetaDump on Unity 6; the May-2026 metadata key on the October file; locating the decryption routine
  by string or immediate.
- Looking for the Box Drop seam; looking for nameplate GameObjects; cloning a TMP under a nameplate.
- Taking `Il2CppType.Of<T>()` at face value for a type that might be absent.
- Hooking through `Il2CppType.From` / `MakeGenericType` delegates (generic il2cpp delegates throw
  `OverflowException` on this build); hook through the native method pointer resolved by name.

---

## 9. Where the tools are

| Need | Tool |
|---|---|
| Ordered obfuscated export list of a build | `tools/pack-port/il2cpp_tables.py` |
| Standard → obfuscated export map, validated | `tools/pack-port/build_port_kit/make_remap.py` (+ `lea.*.json`, `remap.*.json`) |
| Repoint Il2CppInterop's P/Invokes | `tools/pack-port/repoint` |
| Diff the deployed P/Invoke table against a map | `tools/pack-port/implmap`, `tools/pack-port/exports` |
| Struct offsets from two builds' disassembly | `tools/pack-port/measure_offsets.py`, `MAPPING_NOTES.md` |
| The struct-layout patcher the C# mod needs | `tools/pack-port/VRChatStructFix` |
| Token → name table for `TokenShiftFix` | `tools/pack-port/tokmap` |
| In-game read-only probe plugin | `tools/pack-port/probe` |
| Read a crash minidump without a debugger | `tools/pack-port/dmpinfo` |
| Find a `MonoBehaviour` that declares `OnGUI` | `tools/pack-port/ongui` |

See [`tools/pack-port/README.md`](../tools/pack-port/README.md) for the per-build procedure.
