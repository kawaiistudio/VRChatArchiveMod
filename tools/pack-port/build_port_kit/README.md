# build_port_kit — the export remap for one VRChat build

Inputs and outputs of the il2cpp export remap for VRChat build **25686233** (Unity 6000.0.67f1), and the
generator that rebuilds them for the next build. The procedure is in [`../README.md`](../README.md);
the reasoning is in [`docs/UNITY6-PORT.md`](../../../docs/UNITY6-PORT.md) §2.

| File | What |
|---|---|
| `make_remap.py` | The generator. `--header` is `il2cpp-api-functions.h` of the exact Unity version (fetch it from `js6pak/libil2cpp-archive` by tag — it is not committed here), `--lea` the ordered export list from `il2cpp_tables.py`, `--check` a map already proven in-game that the new one must agree with, `--hpp` an optional C++ header output for the engine. |
| `lea.25686233.json` | The 234 obfuscated export names of this build, in canonical order. Entry 0 is `il2cpp_init`. |
| `remap.25686233.json` | Standard name → obfuscated name, complete for this build. |
| `remap.validated-184.25686233.json` | The 184 entries proven in-game (BepInEx booted, chainloader complete, every bound function behaved). Use as `--check` for the next build. |

The header declares 241 functions and the binary exports 234: the seven missing are the profiler block,
`il2cpp_profiler_install` … `il2cpp_profiler_install_thread` (header positions 151–157), which release
builds do not export. `make_remap.py` skips exactly that block and refuses to emit if the counts do not
reconcile.
