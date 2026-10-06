# Security Policy

This project is a client-side BepInEx 6 (IL2CPP) plugin that runs inside the VRChat process, on your
own machine. That makes "security" mean several different things at once, and this document separates
them, because they have different answers and different places to go:

1. **A vulnerability in this mod** — something that lets a hostile world, avatar, instance peer or
   third-party data feed harm the person who is running the mod. **In scope.** Report it privately,
   see [Reporting a vulnerability](#reporting-a-vulnerability).
2. **A vulnerability in VRChat itself** — **out of scope here.** It belongs to VRChat Inc., not to
   this repository. See [Out of scope](#out-of-scope).
3. **"This mod can be used to do things VRChat's rules disallow"** — not a security vulnerability.
   The [Disclaimer](README.md#disclaimer) already states the position. See [Out of scope](#out-of-scope).

**Contents** — [Supported versions](#supported-versions) · [In scope](#in-scope) ·
[Out of scope](#out-of-scope) · [Reporting a vulnerability](#reporting-a-vulnerability) ·
[What a good report contains](#what-a-good-report-contains) ·
[What to expect](#what-to-expect) · [Coordinated disclosure](#coordinated-disclosure) ·
[What this mod does with your data](#what-this-mod-does-with-your-data)

---

## Supported versions

There are **no tagged releases and no published GitHub releases** in this repository yet. There is one
line of development, `main`, and one version number, which lives in two places that are kept in step:
`<Version>` in `VRChatArchiveMod.csproj` and `PluginInfo.Version` in `src/Plugin.cs`. The plugin prints
it to the BepInEx log on load:

```
VRCHAT ARCHIVE MOD v3.9.20 loading (BepInEx IL2CPP)...
```

| Version | Supported |
|---|---|
| 3.9.20 — current source on `main` | Yes |
| Anything older | No |

Only the current source is supported, and there is no backporting: a fix lands on `main` and that is
the fix. If you are running an older build, reproduce against the current source before reporting —
the game's IL2CPP layer is renamed every VRChat update, and a fair number of "bugs" against an old
build are simply that build being stale.

---

## In scope

A report is in scope when the harm lands on **the person running the mod**, and the trigger is
something they do not control. Concretely:

- **Remote code execution or arbitrary file write** reachable from untrusted input: a world's Udon
  programs, an avatar, an inbound Photon event, or the third-party FewTags JSON database the mod
  re-downloads every ten minutes by default (`src/Modules/FewTagsModule.cs`, `FewTags/UpdateMinutes`).
- **A crash, freeze or hang another client can trigger.** This matters more than usual here, because
  two modules exist specifically to stop it: `PhotonGuardModule` drops hostile inbound Photon events
  before VRChat dispatches them, and `AntiCrashModule` clamps crasher-tier avatar components. A way to
  get past either of them, or a way to weaponize either of them against the user running it, is a
  valid report.
- **Data exfiltration** — any path where the mod sends more than
  [what is documented below](#what-this-mod-does-with-your-data), or sends it somewhere else.
- **Credential exposure** — anything that puts a VRChat Archive session cookie, the loader key, or a
  VRChat session token into a file, a log, an HTTP request or the on-screen UI. `src/Core/Redact.cs`
  exists because this already happened once: VRChat prints its own command line at startup, the
  loader carries the user's key on that command line, and the mod's log capture wrote it verbatim into
  the file people paste into a chat when they ask for help. **A redaction bypass is a valid report.**
- **Unsafe file handling** in what the mod writes — path traversal from a name the mod does not
  control (a display name, a world name, an avatar name, a FewTags record) into a filename under
  `<BepInEx>/VRChatArchiveMod/`.
- **Memory-safety bugs in the IL2CPP interop layer** that untrusted data can reach —
  `Core/NativeGuard.cs`, `Core/FieldOffsetFix.cs`, `Core/NestedTypeFix.cs`. An access violation in a
  native call is not a catchable exception; it ends the process. Those three files are the mitigation,
  so a hole in them is worth reporting even if you cannot show a full exploit.
- **A default that is more dangerous than its description claims.** Every setting is a documented
  BepInEx `ConfigEntry` in `src/Core/ModConfig.cs`; if a description understates what a switch does,
  say so.

### What these protections are not

`PhotonGuardModule`, `AntiCrashModule`, `TrueViewModule` and `NsfwFilterModule` are best-effort
mitigations written against attacks that were actually observed, not a security boundary. This is a
game mod, not a security product: it runs in-process with your full user privileges, it deliberately
patches parts of the game at runtime, and it loads a third-party
tag database off the network. Running it is a decision you make with those facts in hand. "The mod did
not stop a crasher" is a bug report worth filing; it is not a breach of a guarantee that was never
made.

---

## Out of scope

**Vulnerabilities in VRChat itself.** If you have found a flaw in the VRChat client, its networking,
or its backend, this repository is the wrong place and cannot act on it. Report it to VRChat Inc.
through their own channels — they publish a security contact and a bug-reporting process on their
website and in their official community documentation. Please use those rather than a public issue
here: a VRChat flaw described in this repository is a flaw described in public, and that helps nobody.
No details, no proof of concept, no "here is how far I got" — just take it to them.

**"The mod can do things VRChat's Terms of Service disallow."** That is the nature of a client-side
mod, it is stated plainly in the README's [Disclaimer](README.md#disclaimer), and it is not a
vulnerability report. The boundary this policy uses: a report is in scope when the harm reaches the
person running the mod, and out of scope when the objection is that somebody chose to run it. Reports
about how another individual is using this software are a matter for VRChat's own moderation, which
has the tools and the standing to act; this repository has neither.

**Compatibility breakage after a VRChat update.** When VRChat ships a new build the IL2CPP exports and
field layouts move, and features degrade or stop. That is expected, it is what the guards in
`src/Core/` are for, and it belongs in a normal public issue rather than a private advisory.

**Mismatched `libs/`.** Interop assemblies generated against a different VRChat build will produce
crashes that look dramatic and are not vulnerabilities. Check the log for the field-offset and
nested-type fixes reporting success first.

**Content in the FewTags database.** Abusive tag text is a matter for the
[FewTags](https://github.com/Fewdys/FewTags) project. A parsing or rendering bug in *this* mod's
handling of that data is in scope — it is untrusted third-party input, and `FewTags/FilterSlurs`
filters only what your own client displays.

**The website and API.** `vrchatarchive.org` is a separate system and is not in this repository. It is
run by the same maintainer, so a private report sent here will reach the right person, but expect it
to be handled outside this repository and outside this repository's history.

---

## Reporting a vulnerability

**Use GitHub's private vulnerability reporting.** On this repository: the **Security** tab →
**Report a vulnerability**. That opens a GitHub Security Advisory visible only to you and the
maintainer, it lets us discuss a fix before anything is public, and it is the only private channel
this project offers.

**This project publishes no email address, and there is no Discord or other contact channel for
security reports.** If somebody gives you one claiming to speak for this project, it is not from here.

If private reporting is unavailable to you for any reason, open a **Bug report** issue — blank
issues are disabled on this repository, so that form is the way in — containing **only** "I have a
security report to make privately" and nothing else: no reproduction, no payload, no affected file.
The maintainer will open an advisory to continue in. Do not put the details in a public issue, a
pull request, a commit message or a comment.

Please do not publish working exploit code aimed at other people's clients anywhere, including after a
fix ships. A crasher that works on this mod's users almost certainly works on VRChat users who do not
run it.

---

## What a good report contains

The more of this you include, the faster it moves:

- **VRChat build** — the client version string, from the launcher or the top of the game log.
- **BepInEx version** — it must be a BepInEx 6 IL2CPP build; say which one.
- **Mod version** — the `v…` in the startup line quoted above, or the `<Version>` from the `.csproj`
  if you built from source. Say which, and name the commit if you built from `main`.
- **How the mod is running** — standalone under your own BepInEx install, or inside the VRChat Archive
  desktop client. This changes which network paths are live: with the client running, writes go
  through the loopback bridge and the mod never holds an account cookie at all; standalone, the
  in-mod login path is the one in use (`src/Core/VaAuth.cs`).
- **Which settings are on.** Several of the features that touch the network are off by default; a
  report is much easier to reproduce when it says what was switched on.
- **Reproduction** — steps, and what the attacker controls. "A world can do X" and "another player in
  the instance can do X" are very different severities; say which one you have.
- **Logs**, with personal identifiers removed:
  - `BepInEx/LogOutput.log` — the game and plugin log.
  - `<BepInEx>/VRChatArchiveMod/diagnostics/va-report_<timestamp>.log` — the mod's own diagnostics
    report, capped at 3 MB so it stays attachable.
  - `<BepInEx>/VRChatArchiveMod/crash/` — for a crash, the crash trail. It is flushed before each
    risky IL2CPP call, so it names what the process died on even though the access violation left no
    exception behind.

### Redaction: what the mod does for you, and what it does not

`src/Core/Redact.cs` runs on the **diagnostics report only** — `Redact.Block`, called from
`DiagnosticsModule`, which is its one call site in the tracked tree. (The file's own header still
describes a second, log-wide pass; `Redact.Line` has no caller today, so read the header as history
rather than as behaviour.) Within that report it masks:

- Arguments of the form `key=`, `password=`, `passwd=`, `pwd=`, `token=`, `secret=`, `auth=`,
  `session=`, `licence=`/`license=` and `main=` — with or without leading dashes, quoted or not,
  `=` or `:`.
- Long base64-shaped blobs (28 characters or more), for a key pasted with no name in front of it.

It does **not** remove display names, VRChat user/avatar/world/instance ids, IP addresses, file paths
containing your Windows username, or anything in `BepInEx/LogOutput.log`, which the mod does not filter
at all. Read what you are about to attach and redact those yourself. If you find something the report
*should* have masked and did not, that is itself a report worth making — see
[In scope](#in-scope).

---

## What to expect

This is a **one-maintainer hobby project**. Set your expectations accordingly, and read the following
as intent rather than as a commitment:

- **Acknowledgement**: when the maintainer next sits down with the repository. Days, sometimes longer.
  There is no on-call rotation and no service level.
- **Assessment**: a plain answer on whether it is in scope, whether it reproduces, and how bad it
  looks. If the answer is "this is a bug but not a vulnerability", you will be told that rather than
  left waiting.
- **Fix**: when it is written and **tested in-game**. Nothing here is verified by a build. The
  workflows in this repository check repository hygiene, C# syntax and release metadata; none of them
  compiles the mod, and none ever will, because the project does not build without proprietary
  reference assemblies and embedded media that are not, and will not be, in this repository. So a
  green tick means the source parses, not that the fix works — every fix is confirmed by a human
  running VRChat.
- **No bounty.** There is no money in this project. Credit in the advisory if you want it, and none
  if you would rather stay anonymous.

---

## Coordinated disclosure

Please give the fix a chance to exist before the details do. The default request is **90 days** from
acknowledgement, or until a fix ships, whichever comes first, and that number is a starting point for
a conversation rather than a rule imposed on you. If something is being actively exploited against
users right now, say so in the report and it moves to the front of the queue.

When a fix ships, the advisory is published with the details and — unless you asked otherwise — your
name on it.

---

## What this mod does with your data

This section is grounded in the tracked source, and where it and the README's short summary disagree,
**this section is the precise one**.

### On your disk

Almost everything the mod writes goes under `<BepInEx>/VRChatArchiveMod/`, outside this repository
and outside the game's own data:

| Path | Written by | Contents |
|---|---|---|
| `crash/` | `Core/CrashTrail.cs` | breadcrumbs flushed before risky IL2CPP calls |
| `diagnostics/` | `Modules/DiagnosticsModule.cs` | the redacted, 3 MB-capped report |
| `network/` | `Modules/NetworkLogModule.cs` | inbound Photon event log, when explicitly enabled |
| `thumbs/` | favourites modules | cached avatar/world thumbnails |
| `DEBUG` | read, never written | a marker file you create to turn on debug behaviour |

Three files land outside that folder, and they are named here rather than tidied away, because "it
all lives in one folder" is only a useful answer if it is true:

| Path | Written by | Contents |
|---|---|---|
| `<BepInEx>/qm_sprites.txt` | `Modules/QuickMenuTabModule.cs` | the names of VRChat's loaded UI sprites, dumped once a session when the tab first needs an icon. Names only — no user data |
| `<VRChat>/VRChatArchive-ui-tree.txt` | `Modules/UiTreeDumpModule.cs` | the menu hierarchy, written into the game's working directory only when the desktop client's DUMP MENU TREE action asks for it. It is a capture of *your* UI, so it can contain your own display name — read it before you attach it |
| `BepInEx/config/org.vrchatarchive.mod.cfg` | BepInEx | your settings. Every `ConfigEntry` in `Core/ModConfig.cs` is persisted here by BepInEx itself, in the ordinary way |

None of it is uploaded anywhere by the mod. Deleting `<BepInEx>/VRChatArchiveMod/` loses caches and
reports, nothing else; deleting the `.cfg` resets every setting to its default.

### On the network

**Loopback — the desktop client.** `http://127.0.0.1:8791`, the VRChat Archive desktop client's local
bridge. Direction matters: the **client** is the server and the mod is an HTTP client that polls it.
The game process opens no listening port of its own. Routes used: `GET /status`, `POST /va-tag`,
`POST /va-post`, `POST /va-fav`, `POST /mod/schema`, `POST /mod/sync`. The sync payload carries the
mod's settings, its live instance roster and its event feed — to loopback, to a program already
running on your machine.

**Loopback — OSC.** UDP `127.0.0.1:9000` by default, VRChat's own OSC input, used by the chatbox
feature. Host and port are configurable.

**This project's own API.** Base `https://vrchatarchive.org`, configurable (`VaTags/ApiBase`):
`POST /api/login`, `GET /api/va-tags`, `POST /api/va-tags/add`, `POST /api/va-tags/remove`,
`POST /api/mod-sound`, `GET /api/mod-sound/feed`, `POST /api/grab/send`, `POST /api/grab/poll`.
That is the complete list in the tracked source; `VaAuth.PostAsync` is the generic authenticated
`POST` helper behind the last two.

**One third party.** `https://raw.githubusercontent.com/Fewdys/FewTags/main/FewTags.json`, a plain
periodic `GET` for the community tag database, identifying itself as `VRChatArchiveMod-FewTags/1.0`.
Nothing about you is sent with it.

**What happens with no setting touched.** A fresh install is not silent. Five things run on
defaults:

- `GET /status` against the loopback bridge. It sits *above* the `Client/ModControl` gate
  (`ModControlModule.OnUpdate`), because the mod has to know whether the desktop client is there
  before it can decide anything else — turning `Client/ModControl` off stops the `/mod/schema` and
  `/mod/sync` payloads, not the probe. It never leaves `127.0.0.1`.
- `GET /api/va-tags` every five minutes (`VaTags/Enabled`, `VaTags/UpdateMinutes`).
- `GET /api/mod-sound/feed` roughly every three seconds (`Soundboard/Enabled`,
  `Soundboard/PollSeconds`).
- The FewTags JSON every ten minutes (`FewTags/Enabled`).
- `POST /api/grab/poll`, a continuous four-second long-poll, **carrying your VRChat user id and with
  no setting that disables it.** It is the mod's inbound event bus rather than a player-grab
  feature, so it listens from the moment your id is known. See
  [the "local only" badge, qualified](#the-local-only-badge-qualified) — this is the one thing about
  you that leaves the machine unprompted, and it is set out there in full.

The three plain reads carry nothing about you but your IP address and the `User-Agent` below, and
switching off the section that owns one stops its traffic.

**VRChat's own backend.** Where the mod acts on VRChat's API it rides the session the game already
holds and forges no credential of its own; the asset-bundle patch only touches URLs the game itself
requested. No VRChat login is ever entered into, or handled by, this mod.

The three HTTP clients that talk to `vrchatarchive.org` present a Chrome `User-Agent` string with a
`VRChatArchiveMod/…` token appended, because the Cloudflare edge in front of that site rejects
non-browser agents. That is a workaround for a 403, not an attempt to look like a browser to you.
The version in that token is hardcoded per client (`VaAuth.cs`, `SoundboardModule.cs`,
`VaTagsModule.cs`) and has drifted behind the mod's real version; it identifies the client, it is
not a version report, and nothing should be inferred from it.

### Credentials

- Inside the desktop client (**ClientBridge** mode): the account cookie **never reaches the mod**. The
  mod hands the write to the local bridge and the client performs it with its own session.
- Standalone (**ModLogin** mode): the VRChat Archive session cookie is held **in memory only** and is
  never written to disk. Logging out or closing the game loses it.
- With neither (**None**): writes are refused; public reads still work.

The mod persists no credential of any kind, and this repository contains none.

### Endpoints that accept unauthenticated requests, by design

Stated plainly because the source states it plainly:

- `POST /api/mod-sound` needs no account — anyone may fire a soundboard clip, and the server
  rate-limits by IP. The `name` field it carries is your VRChat **display name**, sent so the feed can
  say who played a clip. It is a label, never an identity: it is chosen client-side and nothing should
  ever be authorized on the strength of it.
- `POST /api/assetbundle-request` on the same server is **not sent by this mod** — the in-game submit
  button moved to the desktop client, and no module in the tracked tree posts to that path. What
  remains here is the note recorded at `src/Core/VaAuth.cs:206`: the session that helper attaches is
  not verified server-side, so the check is an **interface gate** — it stops a caller submitting on
  behalf of somebody who is not signed in; it cannot stop a direct request. It is left in this
  document because the endpoint is real and the note is public either way, and a reader deserves to
  know which half of it this repository is responsible for.

Both are server-side matters. Report them privately as described above rather than in a public issue.

### The "local only" badge, qualified

The precise claim, and the one this policy will defend:

> The mod opens no listening port, sends nothing to any third party except the FewTags read above,
> collects no analytics or usage metrics, and relays nothing about you to other clients in your
> instance as a side effect of being installed.

It deliberately does not say "nothing about you leaves your machine". One thing does, with no
setting to stop it: **the event relay long-poll**. `PlayerGrabModule.EnsurePolling` starts as soon
as the mod knows your VRChat user id and then holds a four-second long-poll on
`POST /api/grab/poll` for the rest of the session, with your `usr_…` id in the body. It is the
mod's whole inbound event bus — grab handshakes and the Mark's art broadcasts both arrive on it —
which is why it listens even when player grab itself is off (`PlayerGrabModule.cs:109-111` says so
in as many words). The `Fun/PlayerGrab*` settings are tuning values; none of them disables the
poll. So this project's server can see, continuously, that a given VRChat account is running the
mod. Nothing else is in that request, and it is a first-party server rather than a third one, but it
is presence data about you and this document is not going to pretend otherwise.

What the claim also does **not** mean is that nothing the mod does is observable by other players.
Several features act on the shared instance on purpose. Each of them either needs a switch turned on
or a button pressed, each says what it does in its own setting description, and the one exception to
"off by default" is called out below:

- **`ObjectOrbitModule` in synced mode** and **`MarkModule` in `vrchat` mode** take SDK ownership of
  real world pickups and move them for real. Everyone in the instance sees the objects move. Both are
  off by default; `ObjectOrbit/Synced` opens its description with "EVERYONE SEES IT".
- **`VideoUrlModule`** drives the world's video player synced to the whole instance.
  (`VideoModule` is the local-only counterpart that changes only your screen.)
- **`BadAppleModule`** writes to the VRChat chatbox over OSC, and VRChat broadcasts the chatbox to
  everyone nearby.
- **`SoundboardModule`** posts a clip key and your display name to this project's server, and every
  other mod user polling the feed plays the clip. **This is the exception: `Soundboard/Enabled`
  defaults to on**, which does not send anything on your behalf — sending is always a button press —
  but it does mean the feed is polled from the start and that another member can make sound come out
  of your headset until you switch it off.
- **`VoiceMimicModule`** relays voice packets through Photon's `OpRaiseEvent`.
- **`PlayerGrabModule`** is a two-sided handshake between two consenting mod users, relayed through
  this project's API: a grab that reaches somebody who is not running the mod with the toggle on
  simply goes unanswered. Its *listening* half is not opt-in, as described above.
- **`GhostModule`** suppresses your own outbound serialization, which by definition changes what
  everybody else sees of you.

Everything in the ESP, radar, panel, theming, favourites and diagnostics families is genuinely
screen-local and changes nothing anyone else can observe.

---

## A last note on trust

The most honest thing this document can say is that a mod loaded into a game client is, by
construction, code running with your privileges inside a process handling untrusted input from
strangers. The defences here are real, they were written against attacks that actually happened, and
they are still a mod's defences and not a sandbox. Read the source — that is why it is public — and
keep the switches you do not need switched off.
