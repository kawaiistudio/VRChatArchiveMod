# Contributing to VRChat Archive Mod

Thank you for looking. Before anything else, the one thing that surprises everybody:

> **A fresh clone of this repository does not build, and cannot be made to build by `dotnet restore`.**
> Two directories the compiler needs — `libs/` and `ressources/` — are deliberately absent, because
> their contents are not ours to redistribute. You have to supply them from your own machine.
> [Before you can build](#before-you-can-build) explains exactly what goes where.

This is a client-side **BepInEx 6 (IL2CPP)** plugin for VRChat, targeting **.NET 6**. It is unofficial:
not affiliated with, endorsed by, or supported by VRChat Inc. Loading third-party code into the VRChat
client can breach VRChat's Terms of Service, and that risk is yours — see the Disclaimer in
[README.md](README.md).

Contributions are welcome. Bug reports, documentation, the Python tools under `tools/`, and code all
help. Because of the build wall above, please read [Good first contributions](#good-first-contributions)
if you do not have a VRChat install to generate interop assemblies from — there is real work there that
needs no game assemblies at all.

**Contents** — [Before you can build](#before-you-can-build) · [Why CI cannot build this](#why-ci-cannot-build-this) · [How the code is organized](#how-the-code-is-organized) · [House rules for changes](#house-rules-for-changes) · [Commits and pull requests](#commits-and-pull-requests) · [Good first contributions](#good-first-contributions)

---

## Before you can build

You need three things: the .NET 6 SDK, a populated `libs/`, and either a populated `ressources/` or a
`.csproj` with the embedded-resource lines removed.

### Prerequisites

- **.NET 6 SDK** or newer. The project targets `net6.0` (`VRChatArchiveMod.csproj`).
- **BepInEx 6 (IL2CPP)** installed against a VRChat installation, and **launched at least once** so it
  generates its interop assemblies. This is what produces the contents of `libs/interop/`; there is no
  other source for them.

### `libs/bepinex/` — eight framework assemblies

Copy these eight files out of your BepInEx install's `core/` directory into `libs/bepinex/`:

```
libs/bepinex/
  0Harmony.dll
  BepInEx.Core.dll
  BepInEx.Preloader.Core.dll
  BepInEx.Unity.Common.dll
  BepInEx.Unity.IL2CPP.dll
  Il2CppInterop.Common.dll
  Il2CppInterop.Runtime.dll
  Mono.Cecil.dll
```

`core/` holds considerably more than eight files (Cpp2IL, AsmResolver, MonoMod and so on). Only these
eight are referenced; copying the rest does no harm but buys nothing.

The `.csproj` references the whole directory with a wildcard and marks it `<Private>false</Private>`:

```xml
<Reference Include="$(MSBuildProjectDirectory)\libs\bepinex\*.dll">
  <Private>false</Private>
</Reference>
```

`<Private>false</Private>` means "compile against it, do not copy it to the output". That is correct
and deliberate: at runtime the plugin is loaded *by* BepInEx, so BepInEx's own assemblies are already
in the process. Shipping a second copy of `0Harmony.dll` next to the plugin is how you get two Harmony
instances patching the same methods. The same reasoning is why `CopyLocalLockFileAssemblies` is
`false` in the `PropertyGroup`.

### `libs/interop/` — the interop assemblies (about 248 files)

When BepInEx 6 first runs against a VRChat install, Il2CppInterop generates a managed proxy assembly
for every IL2CPP assembly in the game. They land in your BepInEx install's **`interop/`** directory
(on older BepInEx 6 builds this directory was called `unhollowed/`). Copy the `.dll` files from there
into `libs/interop/`.

The author's `libs/interop/` currently holds **248** DLLs. You are in the right folder if you can see:

```
libs/interop/
  Assembly-CSharp.dll                 the game's own script assembly
  Assembly-CSharp-firstpass.dll
  VRC*.dll                            54 of them: VRC.Udon.dll, VRC.UI.Elements.dll,
                                      VRC.Dynamics.dll, VRCCore-Standalone.dll, VRCSDKBase.dll, ...
  UnityEngine.*.dll                   77 of them: UnityEngine.CoreModule.dll, UnityEngine.UI.dll, ...
  Il2Cpp*.dll                         29 of them: proxies for the game's bundled managed libraries
```

The exact count will not match 248 on your machine, and does not need to. It varies with the VRChat
build you generated against — that is also why the mod carries `FieldOffsetFix`, `NestedTypeFix` and
`NativeGuard`: the layout underneath these proxies changes when VRChat updates.

### Why neither directory is committed

Not an oversight, and not something a pull request can fix. `libs/bepinex/` is BepInEx's redistributed
binaries; `libs/interop/` is generated **from a VRChat installation** and is derived from Unity's and
VRChat's property. Committing either would be redistributing code we have no right to redistribute.
`.gitignore` states this at the point of exclusion, and the rule is not negotiable — a pull request
that adds files under `libs/` will be closed.

### `ressources/` — 22 embedded files, about 20.9 MiB

The `.csproj` embeds 22 files from `ressources/` into the DLL, each with an explicit `<LogicalName>`
that the code looks up at runtime (`Core/AssetLoader.cs` and callers of
`Assembly.GetManifestResourceStream`).

| File | Size | Used by |
|---|---|---|
| `badapple.wav` | 9.2 MiB | `Core/BadAppleAudio.cs` — the object show's soundtrack, and its clock |
| `tokens-1886.tsv.gz` | 3.4 MiB | `Core/TokenShiftFix.cs` — metadata token → member name table read out of the Il2CppInterop assemblies of the build they were generated for (`tools/pack-port/tokmap` produces it); lets the fix rebind by name when the tokens shift |
| `spawn_darksquad.wav` | 2.1 MiB | `Modules/SpawnSoundModule.cs` — local spawn stinger |
| `badapple_hd.frames.gz` | 1.9 MiB | `Modules/MarkModule.cs` — 64×48 object-art frames |
| `spawn_te_op.wav` | 948 KiB | `Modules/SignatureSoundModule.cs` — one person's arrival clip; third-party music, supplied by the author |
| `badapple.frames` | 775 KiB | `Modules/BadAppleModule.cs` — baked chatbox frames |
| `rue\mcs.dll.gz` | 478 KiB | `Modules/RuntimeEditorHost.cs` — the Mono C# compiler RuntimeUnityEditor's REPL uses, gzipped; third-party, see NOTICE |
| `menu_bg.png` | 455 KiB | `Core/AssetLoader.cs` — QuickMenu wallpaper |
| `background.jpg` | 341 KiB | `Core/AssetLoader.cs` — overlay background |
| `sb_scary.wav` | 218 KiB | `Modules/SoundboardModule.cs` |
| `va_panel_players.jpg` | 213 KiB | `Modules/WingPlayersModule.cs` — left wing panel art |
| `sb_gay.wav` | 184 KiB | `Modules/SoundboardModule.cs` |
| `va_panel_log.jpg` | 178 KiB | `Modules/WingLogModule.cs` — right wing panel art |
| `rue\RuntimeUnityEditor.Core.IL2CPP.dll.gz` | 177 KiB | `Modules/RuntimeEditorHost.cs` — RuntimeUnityEditor (IL2CPP build) by ManlyMarco, gzipped, loaded on demand; **GPL-3.0**, see NOTICE |
| `watch_join.wav` | 171 KiB | `Modules/WatchlistModule.cs` — watchlist join alert |
| `sb_respawn.wav` | 120 KiB | `Modules/SoundboardModule.cs` |
| `sb_mambo.png` | 86 KiB | `Modules/SoundboardModule.cs` — clip artwork |
| `sb_mambo.wav` | 35 KiB | `Modules/SoundboardModule.cs` |
| `5560-heart-rem.png` | 30 KiB | `Core/AssetLoader.cs`, soundboard default artwork |
| `kawaii_logo.png` | 28 KiB | `Modules/LaunchpadConsoleModule.cs` — console header mark |
| `logo_archive.jpg` | 4.8 KiB | `Core/AssetLoader.cs` |
| `badapple.charset` | 97 B | `Modules/BadAppleModule.cs` — the hanzi grey ramp |

Total: **21 962 657 bytes, 20.9 MiB**, of which `badapple.wav` alone is 9 663 054 bytes. It is that
large because this IL2CPP build has no MP3 decoder at all (see `Core/WavAudio.cs`), so the clip has to
travel as mono 16-bit PCM WAV.

**A missing embedded resource is a hard MSBuild error, not a warning.** The build stops; you do not get
a DLL with a blank texture in it. So you have exactly two ways forward:

1. **Supply your own equivalents.** Same filenames, same formats, in `ressources/`. `badapple.wav` must
   be mono 16-bit PCM. `badapple.frames` and `badapple.charset` are regenerated by
   `tools/bake_badapple.py` and `tools/pick_charset.py` from a source video you provide.
2. **Delete the matching `<EmbeddedResource>` lines** from `VRChatArchiveMod.csproj`. This is the
   faster route if you are working on, say, ESP or the Udon tools and do not care about the soundboard.
   Every resource read sits behind a `try`/`catch` and a null check, so a feature whose resource is gone
   logs a warning and does nothing rather than taking anything down. Do not commit a `.csproj` with the
   lines stripped, though — that would break the author's build.

The media is third-party (see Credits in [README.md](README.md)) and is not redistributed here for the
same licensing reason as `libs/`.

### Build

```bash
dotnet build -c Release
```

The output is:

```
bin/Release/VRChatArchiveMod.dll
```

Note there is **no `net6.0/` subfolder** — `AppendTargetFrameworkToOutputPath` is `false` in the
`.csproj`, so the DLL sits directly in `bin/Release/`. `DebugType` is `embedded`, so there is no
separate `.pdb` to carry around either.

### Install

Copy that DLL into your BepInEx plugins directory and start VRChat through BepInEx:

```
<VRChat>/BepInEx/plugins/VRChatArchiveMod.dll
```

**VRChat must be closed while you copy** — the plugin DLL is locked for as long as the game holds it.
The plugin writes to the BepInEx console and `LogOutput.log`; if you do not see
`VRCHAT ARCHIVE MOD v3.9.20 loading (BepInEx IL2CPP)...` there, the plugin never loaded and nothing
below it will work. Its runtime output (crash trails, diagnostics, network logs, cached thumbnails)
goes to `BepInEx/VRChatArchiveMod/`, outside the repository.

---

## Why CI cannot build this

There is no build badge in the README, and there will not be one. Please do not open an issue asking
for one. The two blockers above are permanent and deliberate:

1. **The reference assemblies do not exist on a runner.** `libs/bepinex/` and `libs/interop/` are
   gitignored because they are not redistributable. A GitHub runner clones the public tree, finds both
   wildcard `<Reference>` globs empty, and fails on the first `using BepInEx;`.
2. **The embedded resources do not exist on a runner.** `ressources/` is gitignored for licensing
   reasons, and a missing `<EmbeddedResource>` is a hard MSBuild error.

Neither is fixable without publishing files we have no right to publish. A workflow that ran
`dotnet build` here would be red on every single run, forever, which trains everyone to ignore the one
signal CI is for.

**What CI does check** — everything that is meaningful on a fresh public clone with no `libs/` and no
`ressources/`:

- **Repository hygiene.** That nothing under `libs/`, `ressources/`, `captures/`, `bin/` or `obj/` has
  been committed; that no binary or archive is tracked; that no credential-shaped literal, private IP,
  personal email address or absolute Windows drive path has crept into a tracked file; that no real
  VRChat identifier (`usr_`, `avtr_`, `wrld_`) is committed.
- **C# syntax parsing.** Every `.cs` file is parsed for syntactic validity. This catches a stray brace
  or a truncated file. It is not a compile: it cannot see a missing type, a wrong overload, or an
  interop signature that changed.
- **Python tool compilation.** `tools/*.py` are byte-compiled, which catches syntax errors in every script under `tools/`. They have no third-party imports beyond what each script documents.

**Semantic correctness is verified by the maintainer building locally and running the result in
VRChat.** There is no substitute, and no automation that can stand in for it. That is the single most
important thing to understand about reviewing here, and it shapes what a useful pull request looks
like — see [Commits and pull requests](#commits-and-pull-requests).

---

## How the code is organized

315 files are tracked, 174 of them C#:

```
src/Plugin.cs            entry point — 1 file
src/Core/                infrastructure — 76 files
src/Modules/             one feature per file — 88 files
cpp/native/              the native (C++) engine — 43 .cpp, 46 .hpp, one .vcxproj (see cpp/README.md)
cpp/loader/              the one-file BepInEx loader that carries the engine
tools/pack-port/         the Unity 6 port kit — 5 Python tools, the StructFix patcher, probes (see tools/pack-port/README.md)
tools/*.py               3 offline tools for the chatbox art, no game dependency
```

[README.md's Architecture section](README.md#architecture) describes the runtime in full — the plugin
entry, the module lifecycle, the configuration model and the IL2CPP safety patterns. It is not
repeated here. What follows is only what you need in order to *change* the code.

### Adding a module

Three steps, and none of them is a scan or an attribute — registration is explicit and ordered.

**1. Subclass `IModule`.** Despite the name, `Core/IModule.cs` declares an **abstract class**, not an
interface, so you write `override`, not `implement`:

```csharp
namespace VRChatArchiveMod.Modules
{
	public class ExampleModule : IModule
	{
		public override string Name => "Example";   // the only abstract member

		public override void OnUiReady() { }
		public override void OnUpdate() { }
	}
}
```

Every other member is `virtual` with an empty body, so override only the callbacks you need:
`OnInitialize` (once, after Harmony is ready), `OnUiReady` (once, first scene fully up),
`OnUpdate` / `OnLateUpdate` / `OnFixedUpdate`, `OnGui` (IMGUI), `OnSceneLoaded(int buildIndex)`, and
`OnShutdown`.

**2. Register it in `Plugin.cs`.** `RegisterModules()` is a hand-written list of
`ModuleManager.Register(new XModule());` calls, and it is the authoritative statement of what is live.

```csharp
ModuleManager.Register(new ExampleModule());
```

Two things follow from that being a list rather than a scan. First, **registration order is dispatch
order**, and at least one pair depends on it — `CapsuleEspModule` is registered before
`HighlightEspModule` on purpose, with the reason written above the line. Put a new module where its
dependencies are already registered, and if the position matters, say why in a comment. Second, a
module that is not registered *does nothing at all*, including its own `OnUpdate` — a mistake made
before, and now documented above `UiTreeDumpModule`'s registration.

You will also find registrations commented out with a paragraph explaining the removal. Those are
deliberate records, not dead lines waiting to be tidied. Leave them alone.

**3. Add its settings to `ModConfig`.** A public static `ConfigEntry<T>` field, plus a `cfg.Bind(...)`
call in `Init`, with a description written for a human:

```csharp
public static ConfigEntry<bool> ExampleEnabled;

// in Init(ConfigFile cfg):
ExampleEnabled = cfg.Bind("Example", "Enabled", false,
	"What this does, and what it costs. This text is what the user reads.");
```

`Core/ConfigRegistry.cs` finds settings by **reflection over `ModConfig`'s public static fields**, so a
new `ConfigEntry` appears in the desktop client's settings page automatically. There is no second list
to update, and no way for one to drift out of step. `ConfigWatch.Watch(entry)` additionally logs *who*
changed a value; use it on settings whose surprising state is worth attributing.

If your feature can be switched on and still do nothing visible — the effect was lost on a world
change, a second setting gates it, the component was not found — report that through
`Core/FeatureHealth.cs`, keyed by the same `"Section/Key"` id the client already knows:

```csharp
FeatureHealth.Broken("Example/Enabled", "on — the effect was not found in this world, nothing will show");
```

A silent no-op is the most expensive kind of bug in this codebase. One honest sentence prevents it.

---

## House rules for changes

### IL2CPP is hostile, and a crash is not an exception

The most important sentence in the codebase is at the top of `Core/NativeGuard.cs`:

> `AN ACCESS VIOLATION IS NOT AN EXCEPTION YOU CAN CATCH.`

`ModuleManager` wraps every dispatch in a `try`/`catch`, and that catches managed exceptions only. An
il2cpp field read through a dead proxy is `*(void**)(objectPointer + fieldOffset)` on unmapped memory;
.NET 6 prints `Fatal error. AccessViolationException` and ends the process. Your `catch` never runs.
The user loses their session.

So:

- **Verify before you dereference.** `NativeGuard.IsReadable(pointer)` asks Windows through
  `VirtualQuery` — which never faults, whatever it is handed — whether the memory is committed and
  readable. Use it before touching anything the game might hand you early, dead, or recycled.
  `VRC.Player.prop_Player_0` is readable from the first frame and is not a live object then; that is
  the crash the guard was written for.
- **Do not assume interop got the layout right.** `Core/FieldOffsetFix.cs` exists because
  Il2CppInterop reads the field offset from `FieldInfo+0x08`, where this build keeps the metadata
  token, so every `field_*` read landed on garbage. Note *how* it is written: the slot is **found and
  verified against four known `System.Delegate` fields**, and the patch is not installed unless all
  four agree. Match that standard. A wrong offset corrupts every field access in the process, so
  "probably right" is not good enough.
- **Degrade to "unavailable", never take the game down.** `Core/Il2CppDelegates.TryConvert` returns
  `null` when the delegate bridge is unsafe, so each of its six callers loses one feature instead of
  the process. That is the shape every risky path should have.
- **Wrap genuinely dangerous native work in a `CrashTrail`.** `Core/CrashTrail.cs` flushes a breadcrumb
  to disk *before* each risky call, so an access violation still leaves a file naming what died. Open
  one around a single dangerous operation and close it immediately — it forces a disk write per step,
  so it is not something to leave running.
- **Prefer `prop_*` over `field_*`** where the game offers both. A property is a method call and
  resolves normally; a field goes through the offset machinery above.

### Local-only is the invariant, and instance-visible is the exception

The mod does not open a listening port, does not exfiltrate your data, and never speaks to the other
clients in your instance on its own initiative. Visualization, movement, filtering and diagnostics are
screen-local by construction. Keep it that way:

- A change must not make the mod send anything to other clients in the instance as a side effect of a
  feature that reads as local.
- Anything that *is* visible to other players — the networked half of `MarkModule`, `ObjectOrbitModule`
  taking ownership of real pickups, `VideoUrlModule`'s synced path, `VoiceMimicModule`, cooperative
  `PlayerGrabModule` — must be **off by default**, **behind its own setting**, and **labelled as such
  in the setting's description**. The existing ones say "Everyone sees it" in as many words. That
  honesty is the rule, not the decoration.
- Nothing in this project is for affecting other people's sessions against their wishes. A pull request
  that adds a way to disrupt, crash, impersonate or harass another user will be closed regardless of
  how it is framed.
- Network calls go to `127.0.0.1:8791` (the desktop client's bridge) or to the project's own API. Do
  not add a third-party endpoint without saying, in the pull request, what it is and what leaves the
  machine.

### Keep the comment culture

19% of the lines in `src/` are comment-only — 8103 of them. That is not clutter to clean up; it is the
most valuable thing in the repository, and it has a specific character worth matching:

- **Explain why, not what.** `// increment the counter` is noise. `PhotonGuardModule`'s
  `THE ATTACK. / THE GUARD. / WHAT IT NEVER TOUCHES.` header is the reason anyone can maintain it.
- **Quote your measurements.** "the profiler had this module at 209 ms/s, a fifth of every second" is
  worth more than "this was slow".
- **Record what failed.** `MenuCard.cs` explains why `Transition.None` was abandoned; `SelfHideModule`
  explains why the once-a-second pass did not work. Deleting a failed approach means the next person
  tries it again.
- **Date decisions that were made against evidence** — "disarmed 2026-08-26", "validated in-game
  2026-08-20".
- Line comments (`//`) throughout; `///` XML docs only for cross-module contracts. No `#region`.

If your change invalidates a comment, update the comment in the same commit. A confidently wrong
comment is worse than none.

### Code style

There is no formatter configured, and the existing style is consistent enough not to need one. Match
what is around you:

- **Tabs** for indentation, 4-wide, one extra tab for continuations. Some lines pad with spaces after
  tabs to align a `??`, a `:` or a trailing comment — that alignment is intentional; do not let a
  "format document" command reflow it.
- **Allman braces**, without exception. Short single-statement bodies on the control line
  (`if (x) return;`, `try { ... } catch { }`) are idiomatic here and used heavily.
- `using` directives **outside** the namespace, `System` first. **Block-scoped namespaces** — no
  file-scoped ones, despite `LangVersion=latest` allowing them.
- Private fields `_camelCase` (statics included — never `s_`); constants and immutable static tables
  PascalCase; public members PascalCase. Predefined keywords always (`string`, `int`, `bool`, `float`),
  never `String`/`Int32`. No `this.` anywhere.
- `var` when the type is apparent or unwieldy; the primitive keyword for builtins.
- Soft target of about 100 columns. Deliberate overruns exist — an unwrapped `ConfigDescription` string
  is easier to read on one line than folded — so this is a target, not a limit.
- Lines end with **LF**, files end with a newline, and there is no trailing whitespace. Please keep all
  three; the tree is currently clean on every one of them.
- **Do not add or strip a UTF-8 BOM.** Files in `src/` are split roughly half and half, and flipping
  one turns a two-line change into a whole-file diff. Leave a file's existing encoding as you found it.

### Never commit

- Anything under `libs/`, `ressources/`, `captures/`, `bin/` or `obj/`. All are gitignored, with the
  reason written next to each rule in `.gitignore`.
- Binaries or archives of any kind — `.dll`, `.exe`, `.pdb`, `.zip`, `.7z`, `.rar`, audio, video.
- Credentials, API keys, session cookies, or a `.env`. The mod holds its session in memory only and
  never writes it to disk (`Core/VaAuth.cs`); keep it that way.
- Absolute paths from your own machine. A deploy script with hardcoded drive letters was published
  once and removed; do not reintroduce one — that is what `DEPLOY.bat` is doing in `.gitignore`.
- Runtime capture dumps. They contain instance and user data.
- **Screenshots showing other players' real display names.** No automated check can see pixels, so this
  one is on you: capture in a private instance, or blur the names, the user ids and the join log before
  the image goes anywhere near `docs/img/`.

---

## Commits and pull requests

### Commit messages

Match the style already in the history. Sentence case, imperative mood, an optional `Scope: ` prefix,
and an em dash or semicolon introducing the second clause. Real examples from this repository:

```
Sync public source with v3.9.20
README: a landing page, and stop publishing the author's deploy script
Make LICENSE pure MIT; move third-party notes to NOTICE
README: brand it — logo, and the in-game shot instead of a placeholder
```

**This project does not use Conventional Commits.** No `feat:`, `fix:`, `chore:` or `BREAKING CHANGE:`
prefixes — they carry no information a human reading this history wants, and they would make the log
inconsistent with everything before them.

Write a body when the change deserves one. The best commits here explain the failure they fix in
enough detail that the reasoning survives: what was observed, what the cause turned out to be, and why
the fix is the right one.

### Pull requests

- **One concern per pull request.** A mixed pull request cannot be partly accepted, and here it cannot
  be partly verified either.
- **Say which VRChat build you tested against.** The IL2CPP layer is renamed and reordered every game
  update, so "works on the build I have" is a fact with a date on it. Include the BepInEx version too.
- **State that you built it and ran it in game**, and what you saw. Not "should work".
- **Understand what you are asking for.** CI cannot compile this project, so a maintainer merging your
  pull request has to reconstruct your change on their machine, build it, install it, launch VRChat and
  reproduce your scenario by hand. An untested pull request is not a contribution with a rough edge —
  it is a request for someone else to do the work. A small, tested, well-explained change is worth far
  more here than a large speculative one.
- **Explain the why in the pull request, then put it in the code.** If the reasoning only lives in the
  pull request description, it is lost the moment the branch is deleted.
- Fill in the pull request template — it is in `.github/` and asks for exactly the items above.
- If your change touches anything visible to other players in the instance, say so explicitly. That is
  the one thing a reviewer will always ask about.

### Reporting bugs

Open a GitHub issue. Useful reports include: the mod version (`3.9.20` at the time of writing, printed
in the log at load), the VRChat and BepInEx versions, the relevant portion of
`BepInEx/LogOutput.log`, and any `.trail` file from `BepInEx/VRChatArchiveMod/crash/` if the game died.
**Redact your log before posting it** — it can contain user ids and instance ids.

For a security issue, do not open a public issue. Report it privately through this repository's
security advisories, as described in [SECURITY.md](SECURITY.md).

---

## Good first contributions

All of these are doable on a machine with **no VRChat install, no `libs/` and no `ressources/`**. They
are real work, not busywork — the first two in particular are things the maintainer knows are wrong and
has not had time to fix.

- **Refresh the README's module list.** It was written for v3.5.0 and has not been regenerated since.
  It documents around 14 modules that no longer exist in the tree (the whole "Dev & Probe Tooling"
  section is stale apart from `DiagnosticsModule` and the Python tools), and none of the 18 modules
  added since — `MarkModule`, `PhotonGuardModule`, `GhostModule`, `PlayerGrabModule`,
  `LaunchpadConsoleModule` and the rest. `git ls-files src/Modules` and each file's header comment are
  everything you need. This is the single most valuable documentation contribution available.
- **The Python tools in `tools/`.** `bake_badapple.py`, `chatbox_probe.py` and `pick_charset.py` are
  ordinary Python with no game dependency. `chatbox_probe.py` needs only a running VRChat with OSC
  enabled to exercise (`BADAPPLE_TEST.bat` is a thin wrapper over its `play` command), and the other
  two run entirely offline against a source video. Argument handling, error messages, cross-platform
  paths — `pick_charset.py` currently hardcodes a Windows font path — are all fair game.
- **Documentation anywhere.** Clarify a confusing section, fix a broken link, correct a stale statement.
  If you found something here misleading, that is a bug worth reporting even if you do not fix it.
- **Formatting and hygiene within the rules above.** Trailing whitespace, a missing final newline, a
  mixed-line-ending file. Keep these separate from behavioural changes and small enough to eyeball —
  a repository-wide reformat is not welcome, because it would destroy the deliberate column alignment
  and bury every future `git blame`.
- **Typos and wording in user-facing strings**, especially `ConfigDescription` text in
  `Core/ModConfig.cs`. That text is what a user reads in the desktop client's settings page, and it is
  the difference between a switch someone understands and one they leave alone.

If you are not sure whether an idea fits, open an issue and ask before writing the code. That is
always cheaper than finding out in review.

---

Questions that are not bug reports are welcome as GitHub issues, or through
[vrchatarchive.org](https://vrchatarchive.org).
