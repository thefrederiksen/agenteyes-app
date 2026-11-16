# Issue #161 - Developer handoff: a supported scratch library for a REAL process

**Branch:** `issue-161-scratch-library-root` (from `main` 17c244d). **Developer seat:** macOS
(devthrottle-mac-mini). **I believe this is finished** for what can be built and measured on a Mac.
Windows and Linux measurement is QA's (criterion 5); what I could NOT measure is listed plainly below.

`<scratch>` in every quoted path is this session's private temp folder on the Mac.

## What was built

Two environment variables, both or neither, read ONCE at the very top of every entry point
(`agenteyes` Program.Main - so every command: serve, package, record, import, the repair pass inside
serve - `AgentEyesApp` OnStartup, and the Linux app's Main):

| Variable | Stand-in for | Root |
|----------|--------------|------|
| `AGENTEYES_REDIRECT_DATA` | local app-data folder | `<it>/AgentEyes` - config, LOG, credential, models, ffmpeg registry |
| `AGENTEYES_REDIRECT_VIDEOS` | Videos / Movies folder | `<it>/AgentEyes` - recordings |

They go through the existing `AppDataPaths.RedirectForThisProcess` (unchanged; its doc now names its
one product caller), so a redirected process lays out its folders exactly like a redirected test host.
New code: `src/AgentEyes.Core/LibraryRedirect.cs`.

### Decision: environment variable, not a CLI option - why

1. **It reaches every entry point and every child.** The CLI, the WPF tray app and the Linux app all
   need it; a process started by a redirected process (selftest re-runs the exe) inherits the
   environment but not the command line. A flag would have to be declared on every command in
   `CliCommandTable` and threaded into every child spawn - one missed site silently runs on the real
   library, which is the defect being fixed.
2. **It must apply before anything resolves a path.** `StorageMigration.Run` and the first log line run
   before the dispatcher parses flags. An environment variable is readable at the first instruction of
   Main; a flag would need a pre-parser ahead of the dispatcher.
3. **The risk of an env var is that it is invisible** - so it is made loud (below), and a typo'd
   `AGENTEYES_REDIRECT_*` name is refused rather than ignored.
   Named `AGENTEYES_REDIRECT_*` so it cannot be confused with the installer's `AGENTEYES_ROOT`
   (a test pins that `AGENTEYES_ROOT` neither triggers nor blocks a redirect).

### Refusals (start refused, nothing written; CLI exit 5)

- partial: one variable without the other (names the missing one);
- a non-absolute value;
- any other `AGENTEYES_REDIRECT_*` variable (both real names misspelt would otherwise run on the real library);
- the two redirected roots overlap each other;
- either redirected root is, contains, or is inside the REAL state or recordings root - with symbolic
  links resolved, so a scratch `videos/AgentEyes` that is a link to `~/Movies/AgentEyes` is refused.

### Gaps found while building it, and closed (these were NOT in the issue)

- **A redirected `agenteyes record start` would have started a REAL recording in the real library.**
  The CLI client finds the service through `Port` in `<state root>/config.json`; a fresh scratch config
  has none, so it defaults to 7882 - the person's own service. Now every `record start|stop|status`
  first reads the service's `/status` roots and refuses, sending nothing else, when a redirected client
  meets a different library (or a pre-#161 service that cannot say), or an unredirected client meets a
  redirected service. Two UNredirected processes are deliberately not compared (Linux systemd vs shell
  `XDG_DATA_HOME` can differ; refusing would break a CLI that works today) - tested.
- **A redirected service must not take port 7882** (`serve` and the tray app refuse; exit 1 / dialog):
  every client of the real service would otherwise drive the scratch library, or the reverse.
- **The Linux app hard-codes `http://127.0.0.1:7882`**, so redirected it would keep scratch settings
  and log while driving the REAL service. A redirected Linux app is refused (exit 5) with the fix.

## Acceptance criteria - implementation, macOS measurement, and how QA verifies

Full transcript: `macos-proof-transcript.txt` (script: `macos-proof.sh`). Every `agenteyes` run in it
was under `sandbox-exec` with read AND write of `~/Movies/AgentEyes` and
`~/Library/Application Support/AgentEyes` denied by the kernel.

### Section 0 (the control that protects everything below): the sandbox blocks

```
ls    '/Users/<owner account>/Movies/AgentEyes' -> exit 1 : ls: /Users/<owner account>/Movies/AgentEyes: Operation not permitted
touch '/Users/<owner account>/Movies/AgentEyes/.agenteyes161-sandbox-probe' -> exit 1 : ... Operation not permitted
ls    '/Users/<owner account>/Library/Application Support/AgentEyes' -> exit 1 : ... Operation not permitted
touch '/Users/<owner account>/Library/Application Support/AgentEyes/.agenteyes161-sandbox-probe' -> exit 1 : ... Operation not permitted
touch scratch -> exit 0
```

Probe files absent afterwards (checked); the kernel logged each of those denials
(`macos-kernel-denials.txt`, `ls(...)`/`touch(...)` lines) - so the denial instrument is proven to fire.

### AC1 - a real service, redirected, does not read the real library

**Built:** the redirect moves both roots before anything runs; the repair pass, housekeeper and orphan
reap all build their paths from those roots.

**Measured (macOS):** real library present and non-empty: `recording folders in /Users/<owner account>/Movies/AgentEyes: 79`.
Scratch library: two imports of a 3 s ffmpeg-lavfi clip this script generated. The redirected
`agenteyes serve --port 17961`'s OWN log (`macos-redirected-serve-log.txt`):

```
[FfmpegChildRegistry] ReapOrphans: folder=<scratch>/p161/run/data/AgentEyes/run/ffmpeg
[RepairService] ResumeUnfinishedAsync: root=<scratch>/p161/run/videos/AgentEyes
[RepairService] ResumeAsync: 2 recording(s) with unfinished post-processing (hostedWork=False)
[PostRecording] Resume: dir=<scratch>/p161/run/videos/AgentEyes/2026-11-16_..._clip hostedWork=False   (x2)
[Housekeeper] Run: trigger=repair-pass, reportOnly=True, root=<scratch>/p161/run/videos/AgentEyes
[Housekeeper] Run: trigger=repair-pass done - 2 recording(s), 0 action(s), would reclaim 0 MB
```

**Absence, with its positive control (same instrument both ways):**
- kernel denials for the redirected serve pid: **0**; for every other redirected run (2 imports,
  package, record status, 4 refusals): **0**, except the two refusals named under "found" below.
- **Positive control:** the SAME binary, unredirected (`HOME=<scratch>/home`), same sandbox: the kernel
  denied it on exactly the real roots - `/Users/<owner account>/Movies/AgentEyes` and
  `/Users/<owner account>/Library/Application Support/AgentEyes/logs` (6-9 lines per run). So the check can fail,
  and did, for the process that should fail it.
- `find ~/Movies/AgentEyes -newer <T0>` after the runs: empty. Folder count still 79. The owner's
  service (pid 50642) still idle, `PendingTranscriptions` 66 before and after.

**QA on Windows / Linux:** follow `docs/cencon/ISOLATING_A_REAL_PROCESS.md`. Build a scratch library
(`agenteyes import <clip>` under the redirect), start `agenteyes serve --port N` redirected, wait 25 s
for the first repair pass (StartupDelay 20 s), and grep the scratch log for `ReapOrphans: folder=`,
`ResumeUnfinishedAsync: root=`, `Housekeeper] Run: ... root=`. Absence on the real library: Linux -
`strace -f -e trace=file` (or `inotifywait -m -r` on both real roots) shows no access to them; Windows -
Process Monitor filtered on the process and the two real roots. Positive control on each: the same
tool, same filter, on an UNredirected `agenteyes --help` (it touches the real log) must show hits.

### AC2 - the redirect is visible: startup log and /status

**Measured (macOS),** startup log (`macos-redirected-serve-log.txt`):

```
[WARN] [LibraryRedirect] ApplyFromEnvironment: LIBRARY REDIRECTED by AGENTEYES_REDIRECT_DATA/AGENTEYES_REDIRECT_VIDEOS: state -> <scratch>/p161/run/data/AgentEyes ; recordings -> <scratch>/p161/run/videos/AgentEyes ; log -> <scratch>/p161/run/data/AgentEyes/logs (pid ...)
[INFO] [CliDispatcher] Run: args=3 command=serve state=<scratch>/.../data/AgentEyes recordings=<scratch>/.../videos/AgentEyes redirect=environment (AGENTEYES_REDIRECT_DATA, AGENTEYES_REDIRECT_VIDEOS)
[INFO] [HeadlessHost] Run: starting, platform=macos port=17961 ... data=... recordings=... redirect=environment (...)
[WARN] [HeadlessHost] Run: LIBRARY REDIRECTED by ...
```

`GET /status` (`macos-status-redirected.json`):

```
"DataRoot": "<scratch>/p161/run/data/AgentEyes",
"RecordingsRoot": "<scratch>/p161/run/videos/AgentEyes",
"LibraryRedirected": true,
"LibraryRedirect": "environment (AGENTEYES_REDIRECT_DATA, AGENTEYES_REDIRECT_VIDEOS)",
```

Plus stderr `[redirect] LIBRARY REDIRECTED ...` on every redirected command, and serve's stdout
`[redirect] ... this is NOT the person's real library`. `/status` carries `DataRoot`/`RecordingsRoot`
ALWAYS (an unredirected process reports its real roots - see AC4), on both the headless server and
the tray app's RestServer (same `RecordStatus` type).

**QA:** same four quotes on Windows (agenteyes.exe serve, and AgentEyesApp.exe redirected with a
non-7882 Port in the scratch config.json - check `/status` on that port) and on Linux.

### AC3 - a partial redirect is refused, naming what is missing

**Measured (macOS),** exit 5, and the scratch folder it named was NOT created:

```
[error] library redirect refused: partial library redirect: AGENTEYES_REDIRECT_DATA is set but AGENTEYES_REDIRECT_VIDEOS is not. Both roots move together or not at all - a scratch state root with the REAL recordings root would mix the two libraries. Fix: set AGENTEYES_REDIRECT_VIDEOS as well, or unset AGENTEYES_REDIRECT_DATA.
```

and the reverse (VIDEOS only), and the disguised partial (`AGENTEYES_REDIRECT_VIDEOS=~/Movies`):
`... puts the redirected recordings root at /Users/<owner account>/Movies/AgentEyes, which overlaps the REAL recordings root ...`.
Also refused: the symlinked scratch root (`... (which resolves to /Users/<owner account>/Movies/AgentEyes) ...`),
a typo'd variable, a redirected serve on 7882. All in the transcript, Section 4.

**QA:** run the same five on Windows (PowerShell `$env:`) and Linux; for the tray app, a partial set
must show the "AgentEyes did not start" dialog (exit code 5) and write NOTHING to either log.

### AC4 - the macOS HOME trap, measured

1. **Runtime, touching nothing** (standalone probe, `home-trap-probe/`; .NET 10.0.15 roll-forward):
   ```
   HOME=<scratch>/home -> UserProfile=<scratch>/home
                          MyVideos=/Users/<owner account>/Movies
                          LocalApplicationData=/Users/<owner account>/Library/Application Support
   ```
2. **The product** (`HOME=<scratch>/home agenteyes serve --port 17962`, NO redirect, under the sandbox):
   its own `/status` (`macos-status-control-unredirected-HOME-scratch.json`) reported
   `"RecordingsRoot": "/Users/<owner account>/Movies/AgentEyes"`, `"DataRoot": "/Users/<owner account>/Library/Application Support/AgentEyes"`,
   `"LibraryRedirected": false`; its stdout said `recordings -> /Users/<owner account>/Movies/AgentEyes`; and
   the kernel denied it on those two real roots. That is "the service resolves the real library", proven
   by the kernel blocking it rather than by trusting either of us.

   Which line is which: the `/status` and stdout lines are the PRODUCT's resolution (it ran); the
   denials are the KERNEL blocking it; the control serve exited 0 on SIGTERM and printed nothing on
   stderr - it did not crash. Its `PendingTranscriptions: 0` is the blocked read of the real
   library (it saw nothing), NOT an empty library: the real library holds 79 folders.

### AC5 - all three platforms, each on its own machine

**macOS: measured here (above).** Linux and Windows: NOT measured by me. Running
`tests/AgentEyes.Tests.Linux` on this Mac is NOT a Linux run and is not reported as one.

### AC6 - is the log separable? YES, with two exceptions stated

- The log lives under the state root, so a redirected process logs ONLY into
  `<DATA>/AgentEyes/logs`: the redirected serve's 81 lines are in the scratch log, and no redirected
  process was denied on the real `logs` folder (the unredirected control WAS - it tried to append to
  `~/Library/Application Support/AgentEyes/logs`, the side effect that happened three times on 16 Nov).
- **A refused start logs nowhere** - stderr (or the tray dialog) only, by design: the only log it
  could reach is the real one.
- **Not redirected:** the tray app's last-resort crash file `%TEMP%\AgentEyes-crash.log`.
- **Found, pre-existing, not changed:** when the log folder is unwritable, `Log.Write` swallows the
  failure ("logging must never throw"), so the sandboxed control's log went NOWHERE - no file anywhere,
  no error. A seat that sandboxes an unredirected process loses its log silently.

### AC7 - gates

| Gate | Where | Result |
|------|-------|--------|
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | this Mac, SDK 10.0.403, runtime 10.0.15 via `DOTNET_ROLL_FORWARD=Major` (no .NET 8 here) | **327 total, 326 passed, 1 skipped (the #151 opt-in), 0 failed**. 43 new (284 before, as on CI). |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | ON THIS MAC - not a Linux run | 335 total, 333 passed, 2 skipped (#151 opt-in; `MachineLocalAppData_OnLinux_IsTheXdgDataHome` skips off Linux), 0 failed |
| engine build `-p:AgentEyesUnixPlatform=mac` / `linux`; `src/AgentEyes.LinuxApp` | this Mac | 0 errors; the 1 mac warning (CS8602 AlwaysOnEngine.cs:901) is pre-existing on main |
| `dotnet build AgentEyes.sln -c Release` + `dotnet test AgentEyes.sln -c Release` | Windows | **NOT RUN - cannot build on a Mac.** QA must run it. |
| CI across more than one run | GitHub | not yet - after the PR's CI runs |

**Windows-blind code - QA, please compile and look hard:** `src/AgentEyes.App/App.xaml.cs` (redirect
at the top of OnStartup after the not-the-application guard; refusal dialog + `Shutdown(5)`; OnExit
writes nothing after a refusal; port refusal inside the startup try) was never compiled by me.
`tests/AgentEyes.Tests/TestIsolationTests.cs` pins changed deliberately (the review moment the guard is
built for): `get_Videos -> MyVideos` moved to the new `get_MachineVideos`; two new env reads
`LibraryRedirect::ApplyFromEnvironment -> AGENTEYES_REDIRECT_DATA/_VIDEOS x1`; one new
`MachineLocalAppData` reader in the same method. I ran the guard's OWN scanner (`CompiledCode.cs`)
over the macOS-built `agenteyes.dll` and its inventory matches those pins line for line (the
`CaptureService::ScreenshotsKnownFolder` line it also prints is macOS-only). The Windows build differs
by `#if`, not in these classes - but that is reasoning, not a Windows run. On Windows the 3 symlink
tests report SKIPPED (unprivileged symlinks need Developer Mode); the pure resolver test covers the rule.

## Heavy smokes

No WPF UI changed beyond startup. `api-smoke.ps1` touches `/status` (two new fields added; nothing
removed or renamed) - QA's call. No audio/screen path changed.

## CenCon impact

Posture-relevant, documented: `docs/cencon/ISOLATING_A_REAL_PROCESS.md` (new - how to isolate on each
platform, the HOME trap stated, what is NOT covered); `docs/cencon/INDEX.md` (`/status` roots, the
variables, posture); `CLAUDE.md` (a third AgentEyes trap pointing at the doc). Visible/controllable:
a redirect can never be silent (stderr, WARN log, `/status`, serve stdout) and cannot point into the
real library.

## Issue #169 (relative recordings root when there is no ~/Videos)

Not fixed here. Interplay: easier, slightly. `AppDataPaths.MachineVideos` is now the one place the
real Videos folder is read, so #169's fix goes there. The redirect itself is immune (both values must
be absolute), so a seat on a machine with no Videos folder is fully isolated by it. The real-root
overlap check skips a relative/empty real Videos rather than resolve it against the working directory
(tested). And `ApplyFromEnvironment` reads the machine folders ONLY when an `AGENTEYES_REDIRECT_*`
variable exists - so if #169's fix makes the lookup throw, normal starts are unaffected.

## The "in practice this cannot happen" list - written down instead of assumed away

1. **Not a full proof of "never reads":** the kernel deny log is the instrument; macOS may rate-limit
   repeated denials, but the first attempt per process is logged and the control proved it fires.
   It covers only the two real ROOTS; everything else on the machine is outside it.
2. **Two refused starts DID stat the real root folder** (denied, 1 line each): a `VIDEOS=~/Movies`
   refusal and the symlink refusal - link resolution of the REDIRECTED root follows it into the real
   root. A metadata read of the folder entry, on a start that is then refused. Normal redirected starts
   do not (only the real roots' PARENTS are resolved - `~/Movies`, `~/Library/Application Support`).
3. **Consequence of 2:** if `~/Movies/AgentEyes` is itself a symlink (library moved to another disk), a
   scratch path pointing at that link's TARGET is not detected.
4. **The single-instance mutex and the Linux app socket are machine-wide**, not per library. A redirected
   tray app cannot run beside the person's own; use `agenteyes serve --port N`.
5. **Any ffmpeg on the machine is visible** to a redirected process (reap only acts on its own registry,
   which is now scratch) - never kill an ffmpeg you did not start.
6. **A redirected process is signed out** (credential per state root): hosted work is skipped. Safer,
   but a seat verifying transcription must `agenteyes signin` under the same redirect.
7. **An env var set in a shell persists** for every later command in that shell, including a seat's
   "quick look" at the real library - the banner on stderr is the tell. The doc says `unset` after.
8. **The record client now makes one extra `GET /status`** per call (the service computes
   `PendingTranscriptions` on each), a small cost on large libraries.
9. **Proof incident (mine, scratch only):** the first proof run started serve through a backgrounded
   shell function, so `$!` was a subshell and my `kill` missed the service; it kept running on the
   SCRATCH roots and port 17961 until I found it in the leftover check and stopped it (SIGTERM, clean
   exit). Transcript kept: `macos-proof-run1-pid-defect.txt`. Fixed in the script; documented in the
   isolation doc.
10. **What I read of the real library, all read-only:** a directory-entry count of `~/Movies/AgentEyes`
    (`find -maxdepth 1`) and `find -newer` over it, before/after each proof run; `GET /status` on the
    owner's 7882 service; the denied `ls`/`touch` probes. No file content was read; nothing was written.
    The owner's serve (pid 50642) and the launchd agent were not touched.

---

# Pass 2 (Linux seat, devlinux) - the guard derives the real library from the USER

Spec for this pass: the Lead's ruling on the QA defect (issue comment 6924190378) and the Lead's brief.
The QA seat's own defect comment is not on the issue or the PR; the ruling quotes its finding, and that
is what this pass fixes. Evidence: `linux-pass2/` (transcript, mutation, gates, and the Landlock
harness source `lldeny.c` - the #161 QA Linux seat's, rebuilt from source and re-proved, section 0).

## 1. The defect, and the fix

**Defect (QA, upheld).** The overlap guard asked .NET for the "real" roots, and on Linux .NET answers
from this process's `HOME` / `XDG_DATA_HOME`. `HOME=<scratch> AGENTEYES_REDIRECT_DATA=~/.local/share
AGENTEYES_REDIRECT_VIDEOS=<scratch> agenteyes serve` was ACCEPTED, printed "this is NOT the person's real
library", and opened the owner's credential, ffmpeg registry and live log. The environment was the
authority on what the environment may redirect.

**Fix.** `src/AgentEyes.Core/RealLibrary.cs` (new). The protected roots are the UNION of:

| Platform | From the ACCOUNT: passwd entry of the EFFECTIVE uid (`getpwuid_r`), never `$HOME` | Plus (pass 1's answer, kept) |
|----------|------------------------------------------------------------------|------|
| Linux | state `<home>/.local/share` (`$XDG_DATA_HOME` not read); recordings `XDG_VIDEOS_DIR` from `<home>/.config/user-dirs.dirs` (`$XDG_CONFIG_HOME` not read), else `<home>/Videos` | .NET's folders for this process's environment |
| macOS | state `<home>/Library/Application Support`; recordings `<home>/Movies` | the same |
| Windows | nothing new - known-folder lookup, as before | the known folders |

- Why a union and not a replacement: a union can only ADD refusals. A spoofed environment can add a
  scratch folder to the protected list; it cannot remove the account's. The environment half stays
  because a person may legitimately run with a relocated `XDG_DATA_HOME`, and then that is their library.
- The symlink resolution, the overlap rule and every refusal message shape are pass 1's, now compared
  against the right answer. Each refusal names the root it hit and where it was derived from.
- If the account cannot be looked up (no passwd entry, relative home, unreadable `user-dirs.dirs`,
  libc missing) the redirect is REFUSED (exit 5) with the reason - nothing to compare against is a
  reason to stop, not to guess a home.
- `RealLibrary` does not log, deliberately: it runs before the redirect, when the only log is the real
  one. What it derived is reported by the banner (into the redirected log) or the refusal (stderr).

**Measured (Linux, under Landlock with both real roots denied, strace -f; `linux-pass2-transcript.txt`):**

| Run | pass 1 | pass 2 | opens of a real root |
|-----|--------|--------|----------------------|
| 1. QA's attack exactly (spoofed HOME, DATA=`~/.local/share`) | ACCEPTED | exit 5, "overlaps the REAL state root /home/<owner>/.local/share/AgentEyes (derived from: passwd entry for uid 1000, home /home/<owner>, XDG default)" | 0 |
| 2. control: same values, real HOME | exit 5 | exit 5 (now naming both derivations) | 0 |
| 3. spoofed HOME, VIDEOS=`~/Videos` | (accepted by the same defect) | exit 5, "XDG_VIDEOS_DIR in /home/<owner>/.config/user-dirs.dirs" | 0 |
| 4. spoofed HOME, both on existing scratch folders, `serve` 35 s | - | accepted; orphan reap, repair pass, housekeeper all on scratch; `/status` `LibraryRedirected: true` (transcript section 6) | 0, and 0 syscalls of any kind on a real root |

**QA's own regression matrix** (`qa-linux/harness/matrix.sh` x1-x4, transcript section 9): at c595c41
x2 (empty-scratch HOME: .NET answers "" for a folder that does not exist, and the #169 skip dropped the
real root from the check - QA's mechanism (a)), x3 (`XDG_DATA_HOME=<scratch>`, mechanism (b)) and x4 were
ACCEPTED. At this head all four are exit 5 with 0 opens; x2 and x3 are refused by the account half
alone. QA asked to "refuse, rather than skip, when a real root cannot be determined": on Linux and macOS
the account half is never skipped (no passwd entry -> refused). The environment half still skips a ""
answer (issue #169's shape), which is now harmless there because the account half is always present. On
Windows the known folders are the only source and the #169 skip is unchanged - stated, not fixed here.

The 3 non-open syscalls in runs 1-3 are pass 1's symlink resolution of the REQUESTED path (stat,
readlink) - metadata, on a start that is then refused (pass 1 list item 2 below, unchanged).

## 2. The test that pins it, and the mutation that breaks it

- `LibraryRedirectTests.RealProcess_SpoofedHomeRedirectOntoTheAccountsRealVideosFolder_RefusedWithExit5`
  (Unix; Linux and macOS suites): a REAL child with `HOME` and every `XDG_*` base on scratch, and
  `AGENTEYES_REDIRECT_VIDEOS` on the account's real Videos folder. Only the passwd derivation can refuse
  it. It aims the VIDEOS variable, not DATA, on purpose: if the guard regresses, that child's state and
  log stay in scratch, and the mutation run below MEASURED what it then does to the real roots - zero
  opens, only stat/readlink/access of `~/Videos`, `~/Videos/AgentEyes` and `~/Videos/qa-record`
  (StorageMigration). A test aimed at the real state folder would write into the person's live log
  from a test the day it fails.
- Pure: `Decide_SpoofedHomeRedirectOntoTheAccountHomeStateFolder_Refused` (QA's exact shape),
  `..._VideosFolder_Refused`, `Derive_SpoofedHome_StillProtectsTheAccountHomeLibrary`, the macOS layout,
  user-dirs parsing (6 cases + absolute), dedupe, relative passwd home throws, and
  `AccountHome_ThisProcess_IsAnAbsoluteFolderThatExists` (the live `getpwuid_r` on the platform's
  struct layout - the macOS CI run is what checks the macOS offset).
- **Mutation** (`linux-pass2-mutation.txt`): `accountHome = Environment.GetEnvironmentVariable("HOME")`
  in `DeriveForThisProcess` (final pass-2 code) -> rebuilt -> exactly that test FAILS (1 of 69 redirect tests), "a spoofed
  HOME let a redirect onto the real Videos folder through (exit 0)". Its banner, quoted there, lists
  SCRATCH folders as "the REAL ... root" - which is item 3 working: the lie is now on the page.
  Restored from the saved file, rebuilt, green.

## 3. The merge, and the desktop app on the MERGED tree

Branch was cut from 17c244d; merged `origin/main` 2d60f77 (#116) as its own commit, da51c35. No
conflicts; the staged merge diff was 110 files, +12722/-216 - identical to main's own diff since the
branch point, so nothing of either side was rewritten by the merge.

Verified on the merged tree, not the sides:
- `src/AgentEyes.LinuxApp` no longer exists; `src/AgentEyes.DesktopApp/Program.cs:37` calls
  `LibraryRedirect.ApplyFromEnvironment` FIRST in `Main` (nothing before it resolves a path or logs:
  the class has only a const and auto-properties), refuses a bad redirect, and **refuses a good one**
  (exit 5) because the app always drives the service at `ControlClient.BaseUrl` = `http://127.0.0.1:7882`
  (`Services/ControlClient.cs:41`, hard-coded on main). So the 7882 rule applies to the desktop app by
  refusal: it never runs redirected, so it can never be a half-redirect. The rename carried pass 1's
  change across; I did not have to re-wire it.
- Changed in that block: wording only - "the Linux app" -> "the desktop app", because since #116 the
  same binary is the Windows and macOS desktop app too, and the message was false there.
- New `tests/Shared/DesktopAppRedirectTests.cs`, linked into `AgentEyes.Tests.Linux` (Linux CI) and
  `AgentEyes.Tests.DesktopApp` (Windows gate): the BUILT `AgentEyesDesktop` as a child, redirected ->
  exit 5 with the refusal; partial -> exit 5, nothing created. A lost refusal shows as a 60 s timeout
  failure on the scratch library, never a run on the real one. NOT in the macOS suite (it does not
  reference the desktop app) - macOS desktop-app redirect is not gated.
- Measured on the built binary (transcript sections 7-8): redirected -> exit 5; spoofed HOME onto the
  real state folder -> exit 5 at the guard; 0 opens of a real root.

## 4. The banner shows its work

- stderr / `[WARN] [LibraryRedirect]` log line now end with `real library kept out (each root with where
  it was derived from): the REAL state root <path> [<derivation>] ; the REAL recordings root <path>
  [<derivation>]`. The path is the one COMPARED: when a parent is a symlink it prints
  `<path> (which resolves to <target>)`.
- `serve` stdout: "this is NOT the person's real library, which this process derived as:" followed by
  one line per root with its derivation (transcript section 4, quoted).
- Pinned by `RealProcess_FullRedirect_BannerNamesTheRealRootsItDerived`.
- `/status` is unchanged (not asked for; its four fields are as QA verified them).

## 5. macOS and Windows - stated, not inherited

- **macOS, pass 1: correct by accident of the platform.** .NET on macOS resolves `MyVideos` /
  `LocalApplicationData` through the platform's own home lookup, not `$HOME` - pass 1's AC4 measured
  `HOME=<scratch>` still giving `/Users/<owner>/Movies/AgentEyes`. The spoof that broke Linux therefore
  did not break macOS: the guard asked the environment, and the platform declined to listen to it.
- **macOS, pass 2: correct by construction.** The account half applies on macOS too (passwd entry,
  `struct passwd` `pw_dir` at offset 48), so it no longer depends on that .NET behaviour.
- **NOT measured on macOS by me** - this is a Linux seat. Compiled the macOS suite on Linux (Build
  succeeded, 0 errors); it RUNS on the macOS CI job. If the struct offset were wrong, `AccountHome`
  returns a non-path and every macOS redirect is REFUSED - it fails closed, and the
  `AccountHome_ThisProcess_...` test fails loudly first.
- **Windows: no derivation change.** The known-folder lookup does not follow this process's
  `USERPROFILE`/`LOCALAPPDATA` variables as far as I know - NOT measured; nobody has spoofed it. The one
  Windows-side change is the `TestIsolationTests` pin of `MachineLocalAppData` readers: the reader moved
  from `LibraryRedirect::ApplyFromEnvironment` to `RealLibrary::DeriveForThisProcess`. This machine's
  SDK has no WindowsDesktop targets, so the Windows build and tests are unrun here - the CI windows job
  is their first run.

## 6. Untouched, by diff

`git diff da51c35` (the pass-2 change alone): no line of `ServicePortRefusal`, `ServiceLibraryRefusal`,
`HeadlessCli.cs`, `RecordingService.cs` (/status), `App.xaml.cs`, `Program.cs`, `CliDispatcher.cs` or
`AppDataPaths.cs` changes, and `git diff --stat c595c41 -- <those files>` is empty across the merge
too. In `LibraryRedirect.Decide`, the unknown-name, partial, relative and same-folder rules are
untouched (the old signature remains as a thin overload, so every pass-1 test calls it unchanged). The
only line removed from the pass-1 tests is the `RunAgentEyes` signature, which gained an optional
parameter.

## 7. Gates (`linux-pass2/linux-pass2-gates.txt`)

- `dotnet test tests/AgentEyes.Tests.Linux -c Release`: **387 passed, 0 failed, 1 skipped (the #151
  opt-in), 388 total**, under Landlock.
- `dotnet test tests/AgentEyes.Tests.DesktopApp -c Release` (net8.0, Linux): **17 passed, 0 failed**.
- `tests/AgentEyes.Tests.Mac`: compiled here; RUN on CI. Windows `.sln`: not buildable here; RUN on CI.
  Results in section 11.
- Heavy smokes: none. No capture, audio or UI change.

## 8. Cases I was tempted to assume away (pass 2)

1. **A relocated library under a spoofed HOME.** If the person really keeps their state under a
   non-default `XDG_DATA_HOME` (or user-dirs under a non-default `XDG_CONFIG_HOME`), the account half
   does not see it, and a seat that also spoofs those variables removes the environment half. That
   library is then unprotected. Not covered. The account half reads only the XDG defaults under the
   passwd home, as the ruling specified.
2. **Another account, or root without sudo.** The derivation is for the EFFECTIVE uid (plus the
   `SUDO_UID` account under sudo - section 10). A process started as root by other means (`su -`, a root
   service) protects root's folders, not the owner's. Not covered.
3. **The real-process test trusts its own host.** It takes "the real Videos folder" from the TEST
   process's unspoofed environment (CI and dev runs). A test host that itself runs under a fake HOME
   would point the child at a fake folder and pass without proving anything.
4. **No passwd entry** (some containers): every redirect is refused. Safe, but it means such a seat
   cannot use the redirect at all.
5. **`user-dirs.dirs` forms other than `"$HOME/..."` and absolute** (e.g. `${HOME}`) are not parsed and
   fall to `<home>/Videos`. xdg-user-dirs writes only those two forms.
6. **The banner is still the binary's own word.** It now names what it compared against, and the
   mutation shows a broken derivation is visible in it - but a reader still has to read the paths.
   Kernel confinement (below) remains the standard for anything risky.
7. **My own footprint on this machine.** Every product binary and test run in this pass ran under the
   Landlock deny on both real roots, control proven first. No probe cleared HOME (the #169 trap the
   Lead flagged writes beside the cwd): every spoofed HOME was an existing scratch folder, and the clone
   had no stray file afterwards. Outside it, unconfined: one `dotnet build` of
   AgentEyes.Core before I had wrapped commands (compile only - the project has no Exec target, no
   product code ran); `getent passwd` and a read of `~/.config/user-dirs.dirs` (configuration, not
   library); `stat` of the real roots, the live log and the protected evidence recording before and
   after - all unchanged except the live log, which the owner's own service (pid 6560) writes
   continuously, so its mtime proves nothing either way; Landlock is the evidence. The owner's service,
   tray app and the foreign go2rtc ffmpeg were identified by command line and not touched.

## 9. Ruling N1: a missing or unwritable redirect folder is REFUSED at startup

Added after the brief (Lead's message, from the full #161 Linux QA report): pass 1 accepted a value that
did not exist (the process then created it) or could not be written.

**Implemented as ruled.** After every other rule has passed, each value must be an existing folder in
which this process can really create, write, flush and delete a probe file
(`LibraryRedirect.UsableFolderProblem`). Otherwise exit 5, naming the variable, the path and why:
`AGENTEYES_REDIRECT_DATA=<path> does not exist. ... Fix: create it (mkdir -p '<path>') or correct the
path.` / `AGENTEYES_REDIRECT_VIDEOS=<path> is not writable: a test write of <probe> failed (...)`.

**Why, in my words.** The issue's contract is that both roots move together or the start fails; a path
that cannot be written is not a root. A mistyped path is the case the operator most needs told about in
the first second - auto-creating it makes a new, empty library where nobody looks, and their recordings
"disappear": the fallback CLAUDE.md forbids. I do not argue for auto-create. The one thing auto-create
would save - typing `mkdir -p` - is printed in the refusal.

**Why a real write and not access().** access() and mode bits can say writable where a write then fails
(read-only bind mounts, ACLs, quotas, some network file systems). The probe is the operation that matters.

**The order is a safety property.** The probe WRITES a file, so it runs LAST - only once the overlap rules
have passed. Run first, `AGENTEYES_REDIRECT_VIDEOS=~/Videos` would drop a probe file into the person's
Videos folder before being refused. Pinned by
`Decide_UsableFolderCheck_RunsOnlyAfterEveryOverlapRuleSoItNeverProbesTheRealLibrary`.

**Measured** (transcript sections 4-6): typo'd data folder -> exit 5, the folder NOT created; 0555 videos
folder -> exit 5, strace shows the data folder's probe `openat(O_CREAT|O_EXCL)` = fd then `unlink` = 0,
and the videos folder's `openat` = `-1 EACCES`; nothing left behind in either folder. Positive control:
the same shape with existing writable folders starts and serves (section 6).

**Tests:** `UsableFolderProblem_*` (writable -> null and no probe left; missing; a file; 0555 by a real
write), `Decide_UsableFolderCheckFails_RefusedNamingTheVariableAndThePath`, the ordering test above,
`RealProcess_DataFolderDoesNotExist_RefusedWithExit5NamingItAndCreatesNothing`,
`RealProcess_VideosFolderNotWritable_RefusedWithExit5NamingIt`. Pass-1 real-process tests that expect a
redirect to be ACCEPTED now create their folders first (`Made(...)`) - the only change to their bodies.
The 0555 tests assert they are not running as root (root ignores mode bits) instead of passing vacuously.

**This changes the operator's recipe**: `mkdir -p` both folders before starting. The isolation doc's
examples already create them; the refusal says so if not.

## 10. Code review (medium) on the pass-2 diff, and what I did with it

Fixed:
- `DllImport("libc")` on glibc probes `libc.so`, a linker script from the C dev package - absent on a
  runtime-only machine, which would refuse EVERY redirect there. Now bound to `libc.so.6` on Linux and
  `/usr/lib/libSystem.B.dylib` on macOS. The tests' own `geteuid` import went the same way.
- `sudo`: the effective uid is root's and so is HOME, so neither half named the person's library. When
  euid is 0 and `SUDO_UID` is set, that account's roots are ADDED (environment used only to add refusals;
  spoofing it gains nothing). New pinned env read in `TestIsolationTests`. Not measured (I do not run
  sudo on the owner's machine) - item 2 in section 8 is narrowed to "root without sudo".
- `ArgumentException` / `NotSupportedException` from a malformed `user-dirs.dirs` value or passwd home now
  become a refusal with the reason instead of an unhandled crash.
- The banner said "derived from the account, not from HOME" even where a root came only from the
  environment (always, on Windows). It now says "each root with where it was derived from", and every
  root carries its own derivation.
- De-duplication compared case-insensitively on every OS, so two Linux folders differing only in case
  merged and one lost its link resolution. Now ordinal.
- The banner printed the unresolved path while the guard compared the resolved one. It prints both.
- `Normalize` was re-implemented in `RealLibrary`; it now uses `LibraryRedirect.Normalize`.
- `DesktopAppRedirectTests` left its scratch folder in TEMP; it now deletes it.
- Non-ASCII ellipsis copied from xunit names into the gates file; regenerated, ASCII.

Not changed, deliberately (CLAUDE.md deviations, stated):
- `RealLibrary` does not log (rule 2). It runs before the redirect; the only log is the person's real one,
  and Log would create the real state folder. Its result is logged by the banner into the redirected log.
- The try/catch that turns a failed derivation into a refusal sits in `ApplyFromEnvironment` (rule 4). It
  is the entry points' one shared decision function and returns `Refused` with the reason, which each
  entry point prints - one place instead of four copies.

## 11. CI at this head, and two defects CI found that my local gates could not

| Run | Head | linux | windows | macos |
|-----|------|-------|---------|-------|
| 43925309084 | 6ba5165 | 387/0 default; opt-in FAILED 1 (see b) | FAILED 2 (see a) | CANCELLED - no runner in 15 min, 0 steps |
| 43928220679 | f063cb7 | FAILED 1 default (see b) | 2436/0 (22 skip); opt-in 2455/0 (7 skip); DesktopApp 17/17 | **351 passed, 0 failed, 1 skipped** |
| 43929135704 attempt 1 | b780cd8 | 387/0 (1 skip); opt-in 388/0 | 2436/0 (22 skip); opt-in 2455/0 (7 skip); DesktopApp 17/17 x2 | **351 passed, 0 failed, 1 skipped** |
| 43929135704 attempt 2 | b780cd8 | 387/0 (1 skip); opt-in 388/0 | 2436/0 (22 skip); opt-in 2455/0 (7 skip); DesktopApp 17/17 x2 | **351 passed, 0 failed, 1 skipped** |

macOS: the job has now RUN at this branch's head, green. (The macOS suite links LibraryRedirectTests, so
the passwd lookup on the macOS struct layout and the spoofed-HOME child ran there; the CI log at default
verbosity does not list passing tests by name, so that is by linkage, not by a quoted line.)

**(a) Windows - mine: the #155 IL inventory (`ManifestWriterIlTests`) pins every file write and native
import in the product.** Pass 2 added the N1 write probe (`FileStream` + `File.Delete` in
`LibraryRedirect.UsableFolderProblem`) and the four passwd imports. FOUND minus PINNED in the CI log was
exactly those six lines and nothing else; pinned with what each does (f063cb7). This machine cannot build
the Windows suite, which is why it surfaced on CI.

**(b) Linux - introduced by #161, fixed: a port collision between test classes.**
`HeadlessControlServerFramingTests` (#123) and `LibraryRedirectTests` (#161 pass 1 added it to the Linux
suite) both start a `HeadlessControlServer` on a probed-then-released port. Run in parallel they can be
handed the same port, the managed HttpListener shares it, and the second Dispose fails with
"Address already in use" - once in each of two consecutive runs, different cases. Both classes now join
one xunit collection (b780cd8), so they never run at the same time. I never reproduced the race locally
(5 default + 2 opt-in runs were green before and after), so the evidence for the fix is the CI runs
above, not my machine.

## Linux: let the kernel enforce it (the macOS sandbox-exec equivalent)

`linux-pass2/lldeny.c` (gcc it; Landlock ABI >= 1): `lldeny <real-state-root> <real-recordings-root> --
<cmd>`. PROVE it first: `lldeny R1 R2 -- ls R1` must say Permission denied (transcript section 0).
Landlock does not refuse `stat` - pair it with `strace -f -e trace=%file,%desc` to see metadata reads.

I believe this pass is finished.

# Pass 3 (Linux seat, devlinux) - a link inside the root, a library known by its content, the unpinned half, the merge

Spec for this pass: the Lead's brief (F2, F1, M-A, the merge, the stale banner quote), then the Architect's
ADDITION while I was mid-pass (fleet message, 19:03Z): implement QA's rule 2 in full and the cheap half of
rule 1 (pass-2 QA report, section 1.4), keep the honest banner, and measure the cost of rule 2. Built on
f9aba6a. Evidence in `linux-pass3/`. Every measurement ran under `lldeny` on BOTH real roots, with the
denial control proven first (`linux-pass3-landlock-control.txt`). The one exception is the service-wiring
test, which ran in a `--network none` container that mounts nothing of the owner's library
(`incontainer.sh`). `<scratchpad>` is this session's private temp folder; `<home>` the account's home.

## 1. F2 - a link INSIDE a redirected root escaped the guard. Fixed: refuse a root that contains one

**Which of the two, and why.** I chose "refuse a root that contains a link escaping it", not "resolve
what is under the root":

- *Resolving under the root* is a rule about every file open the product makes, for the whole life of the
  process: the log, config, manifests, the mux and the housekeeper. It would also have to cover ffmpeg, a
  separate process that writes the recordings by path and that no resolver in our process can govern. The
  rule would have to be threaded through every call site that builds a path, and one missed site is a
  silent escape. A start-up check sits in one place, before anything has been opened, which is where every
  other rule of this guard already sits.
- *Escaping the root*, not *escaping into the real library*: the guard cannot know every place a real
  library lives (F1). "Does this link leave the scratch root?" is the one test whose answer does not depend
  on knowing that.

**What it does** (`LibraryRedirect.FirstEscapingLink` / `LinkEscape`, the `escapingLink` step of `Decide`).
It walks each redirected root's tree and never follows a link. Each link's target is judged AS WRITTEN:
absolute, or taken against the link's own folder. Inside means under the root, spelled either as resolved
or as the person wrote it (the code review's macOS `/var` -> `/private/var` finding). A target with a `..`
segment is refused. That rule is what makes judging the written form sound: `sub/../x`, where `sub` is
itself a link to the root, is inside as written but outside to the kernel. Hidden entries are walked.
.NET skips them by default, and on Unix every dot-file counts as hidden. A folder it cannot list, or a link
it cannot read, throws. `ApplyFromEnvironment` then refuses the start with the reason; it never assumes the
root is clean. The legacy `qa-record` folder needs no walk: the marker write (section 2) creates
`<it>/AgentEyes` before `StorageMigration` runs, so the migration never adopts `qa-record`. This is pinned
by `RealProcess_LegacyQaRecordLinkBesideAFreshScratchRoot_NeverAdopted`.

**Measured** (`linux-pass3-live.txt`), QA's attack on a REAL process: `<scratch>/data/AgentEyes/logs ->
<home>/.local/share/AgentEyes/logs`.

| Run | Exit | `openat` of a real root | `openat` through the link |
|-----|------|-------------------------|---------------------------|
| A - QA's exact shape (a root holding only the link, no marker) | 5, refused by rule 2 | 0 | 0 |
| B - the same link in a MARKED root | 5, refused by the walk | 0 | 0 |
| **BC - positive control**: B with the walk removed (mutation F2-apply) | 0 | 0 | **5**: `AgentEyes-20261116.log` O_WRONLY/O_CREAT, each EACCES from Landlock |

BC reproduces QA's five opens of the owner's live log, so B's zero is a measurement and not a blind
instrument. **One thing I will not round to zero:** listing a scratch folder that contains a link costs one
FOLLOWING `stat` of that link (A and B: 1). .NET's directory enumerator classifies each entry by its target.
That is a metadata read of the owner's logs folder. It opens nothing, and Landlock does not govern `stat`.
The isolation doc now says so. My own first count, "0 syscalls naming a real root", was the instrument
failing open: `strace` prints the LINK's path, not the target's. I caught it before quoting it.

**Pinned:**
- `RealProcess_LinkInsideTheScratchStateRootLeadsOut_RefusedWithExit5AndTheTargetUntouched` (Unix). It is
  QA's shape on a REAL child, in a MARKED root, so the walk and not rule 2 is what refuses. The link points
  at a stand-in for the real logs folder, inside the test's scratch, which is left empty.
- Pure tests on every platform: `LinkEscape_*` x7 (down/relative, inside/absolute, the root itself, through
  the written alias, outside, a sibling that shares the root's name as a prefix, `..`) and `Decide_*` x3
  (the refusal text; both roots asked, resolved and as written; never asked once an overlap rule refused).
- On disk (Unix): QA's shape, a deep hidden file, links that stay inside, the root reached through an
  alias, and a missing folder.

## 2. F1 - a relocated library: rule 2 and half of rule 1 BUILT, the wording fixed, the residue written down

**The Lead's original reasoning, which still stands for the PATH rules.** If `XDG_DATA_HOME` relocates a
person's library, the passwd entry cannot say where it went, because that location exists only in an
environment variable we have decided not to trust. Refusing every redirect inside the derived home would
break the ordinary scratch-under-home case, and would still miss a library relocated outside the home. The
Architect's addition goes past the path rules:

**Rule 2 - a library recognised by its CONTENT** (`UnmarkedLibraryProblem`, `WriteScratchMarker`,
`ScratchMarker = ".agenteyes-scratch-library"`). An existing, NON-EMPTY `<value>/AgentEyes` without the
marker is refused. The first accepted redirected start writes the marker into both roots, after `Decide`
has accepted and before anything else touches the roots. A fresh scratch root, including one under the
home, is empty, so it passes and gets marked. A real library holds files and no marker, so it is refused
wherever it lives. The check costs one listing of one folder, stopped at its first entry. Hidden entries
count.

**Rule 1, the cheap half - ask the running service** (`RealLibrary.AskRunningService` / `FromServiceStatus`).
The guard sends `GET http://127.0.0.1:7882/status` with a 2 s timeout. The service's `DataRoot` /
`RecordingsRoot` join the union through `RealLibrary.Distinct`, the same dedupe `Derive` uses, now
factored out unchanged. If nothing answers, or a service older than #161 names no roots, nothing is added,
and the banner says which. A root that is not an absolute path ending in `AgentEyes` cannot be compared, so
the redirect is refused. NOT built, per the ruling: the service's `/proc/<pid>/environ` and the systemd user
manager's environment. Both are written into the doc as residue.

**Order, and why it is now a pattern** (a comment in `Decide` says so). Every PATH rule runs first. Then
come the steps that touch the disk, cheapest first: the library check reads one folder, the link walk reads
a tree, and N1's write probe writes a file and stays LAST. Run before the overlap rules, each of these
would read or write the real library itself whenever a person named it. The `Decide_*Runs*After*` tests pin
this ordering.

**The banner** still states what it compared, never a certainty. stderr/log: `... ; compared against these
real roots and overlaps none (...): <each root [derivation]> ; <what the service on 7882 said> ; NOT CHECKED
by path: a library kept anywhere else (...) is not in this list - it is refused only by its content (an
existing, non-empty AgentEyes folder without .agenteyes-scratch-library) - see ...ISOLATING_A_REAL_PROCESS.md`.
`serve` prints the same as separate lines. "real library kept out" and "this is NOT the person's real
library" are gone. The banner test asserts both are absent, and that the limit and the 7882 statement are
present. The stale banner quote at line 69 of the isolation doc is fixed, and so is the `serve` quote just
below it, which carried the same retired claim.

**Refused BY CONSTRUCTION now** (each one measured or pinned):

| Shape | Refused by | Evidence |
|-------|-----------|----------|
| QA's `za`: the library relocated by XDG_DATA_HOME (it holds a file), redirect run with XDG_DATA_HOME spoofed. At f9aba6a this was **exit 0, and the log landed IN that library** | rule 2 | live D: exit 5, the library still holds only `MARKER`; `RealProcess_RelocatedLibraryUnderASpoofedEnvironment_RefusedByItsContent` |
| a relocated library, EVEN EMPTY, while the person's post-#161 service runs on 7882 | rule 1 | `RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_RedirectOntoItRefused` (container) |
| a relocated library named by this process's own environment | the union's environment half | `RealProcess_RedirectOntoTheLibraryTheEnvironmentRelocated_RefusedWithExit5` (section 3) |
| a scratch root populated BY HAND (deliberately) | rule 2 | live C1, below |
| a link inside a root leading out | the walk | live B, section 1 |

**Documented residue** (`ISOLATING_A_REAL_PROCESS.md`, "A relocated library"):
- a relocated library folder that is empty or not yet created, set only in an environment this process does
  not share, with no service on 7882;
- a folder somebody marked by hand;
- a service on a non-default port (its port is in the REAL config.json, which is never read) or one older
  than #161 (names no roots). **The service installed on devlinux today is one of these:** live E's banner
  says `the AgentEyes service answering on 127.0.0.1:7882 names no library on /status (it predates issue
  #161), so it added nothing`;
- `/proc/<pid>/environ` and the systemd environment, not read by ruling;
- hard links and bind mounts, which are not symbolic links;
- links created after start-up.

**The cost of rule 2, measured, as the Architect asked** (live C1/C2). A seat fills its scratch root with a
fixture (`config.json`) and starts:

> library redirect refused: AGENTEYES_REDIRECT_DATA puts the redirected state root at
> <scratchpad>/live3/c/data/AgentEyes, which already holds an AgentEyes library that was not created as a
> scratch library (it contains 'config.json' and no .agenteyes-scratch-library). A redirect never adopts an
> existing library: it may be the person's REAL one, relocated somewhere the path rules cannot see. Fix:
> point AGENTEYES_REDIRECT_DATA at a folder whose AgentEyes folder is EMPTY or absent (a redirected start
> marks it), or - if this IS a scratch library you populated yourself - create the marker: touch
> '<scratchpad>/live3/c/data/AgentEyes/.agenteyes-scratch-library'.

After that `touch`, the same start is accepted (C2: exit 0, 0 opens of a real root). **This is a deliberate
trade: a legitimate pre-populated scratch root is refused so that a real library never is.** Some seat will
hit it. The message tells them how to proceed. The isolation doc's own recipe wrote `config.json` before the
first start, so I updated it to touch the marker (bash and PowerShell). In the suite, the record-status
test's fixture now sits in a marked root.

**A stronger rule than the two built:** none that I would add on this pass. The two residues worth closing
next are the non-default-port service and the systemd environment; both are routing decisions for the Lead.

## 3. M-A - the environment half of the union, pinned

The guard protects the passwd-derived roots UNION what this process's environment names. QA dropped the
environment half and all redirect tests stayed green. There are two places it can be dropped:

- **MA-deriveForThisProcess**: `DeriveForThisProcess` passes `"", ""` instead of the machine folders. This is
  the shape that left pass 2 green; the in-`Derive` shape already broke three pass-2 tests. It now fails
  exactly `RealProcess_RedirectOntoTheLibraryTheEnvironmentRelocated_RefusedWithExit5`: *"a redirect onto
  the library XDG_DATA_HOME relocated to .../m-a/relocated-share was accepted (exit 0): [redirect] LIBRARY
  REDIRECTED ... compared against these real roots and overlaps none ...: the REAL state root
  <home>/.local/share/AgentEyes [passwd entry for uid 1000, home <home>, XDG default] ..."*. The relocated
  folder is missing from the list it compared against.
- In-`Derive` removal: the two new pure tests (`Derive_EnvironmentNamesFoldersTheAccountDoesNot_...`,
  `Derive_NoAccount_TheEnvironmentHalfAloneIsProtected`) also fail; they were measured at the first
  pass-3 build.

The real-process test runs on Linux only (`LinuxOnlyRedirectFact`; it is reported SKIPPED elsewhere,
because XDG_DATA_HOME moves nothing on macOS or Windows). Its relocated folder has no AgentEyes folder yet,
so rule 2 cannot be what refuses it.

## 4. Mutations (`linux-pass3-mutations.txt`; each applied to the FINAL code, built, run, reverted)

| Mutation | Fails (of 104 redirect tests) |
|----------|-------------------------------|
| F2-apply: `ApplyFromEnvironment` passes `null` for the walk | `RealProcess_LinkInsideTheScratchStateRootLeadsOut_...` |
| F2-dotdot: the `..` rule off | `LinkEscape_DotDotInTheTarget_...` |
| F2-hidden: the walk skips Hidden (the .NET default) | `FirstEscapingLink_DeepHiddenFileLinkedOut_Reported` |
| F2-alias: the written spelling of the root dropped | `LinkEscape_AbsoluteTargetThroughTheRootAsWritten_...`, `FirstEscapingLink_RootReachedThroughAnAlias_...` |
| R2-apply: `null` for the library check | `RealProcess_RelocatedLibraryUnderASpoofedEnvironment_...`, `RealProcess_ScratchRootPopulatedByHand_...` |
| R2-hidden: the library check skips Hidden | `UnmarkedLibraryProblem_OnlyAHiddenFile_StillContent` |
| R2-marker-write: the marker never written | `RealProcess_FullRedirect_MarksBothRootsAsScratch`, `RealProcess_LegacyQaRecordLinkBesideAFreshScratchRoot_NeverAdopted` |
| R1-service-roots: the service's roots not added to the union (in the container: 7882 free) | `RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_...` |
| MA-deriveForThisProcess | `RealProcess_RedirectOntoTheLibraryTheEnvironmentRelocated_...` |

**The service-wiring test runs only where 7882 is free** (`DefaultPortFreeFact`). It plays the person's
service on the default port. On devlinux the owner's service holds that port, so the test is reported
SKIPPED here and runs in the container and on CI. It only ever tries to bind the port; it never touches
whatever holds it. **Every redirected real-process test in the suite now sends one `GET /status` to
127.0.0.1:7882, which is the owner's service on this machine.** That is a read, and it is what the ruling
asks the feature itself to do; I state it so nobody discovers it later.

## 5. The merge of main 277a9bc (#118 + #156), 57806ae

Merge base 2d60f77. There were five add/add conflicts, each from both sides appending to the same list.
Each is resolved as the UNION and verified to be exactly main's list plus this branch's additions, with no
duplicates:

| File | This branch added | main added | Resolution |
|------|-------------------|------------|------------|
| `tests/AgentEyes.Tests/AgentEyes.Tests.csproj` | `LibraryRedirectTests.cs` | #156: `CaptureOwnershipTests`, `CaptureWriterReapTests`, `ProcessIdentityTests` | both |
| `tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj` | `DesktopAppRedirectTests.cs`, `LibraryRedirectTests.cs` | #118: `MacBundleTests`; #156: the three above | both; `MuxLockTests.cs` was on BOTH sides of the hunk and is kept once |
| `tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj` | `LibraryRedirectTests.cs` | #156: the three | both |
| `tests/AgentEyes.Tests.DesktopApp/AgentEyes.Tests.DesktopApp.csproj` | `DesktopAppRedirectTests.cs` | #118: `MacBundleTests` | both |
| `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` `PinnedNativeImports` | `libc.so.6!geteuid`, `libc.so.6!getpwuid_r` | #156: `libc!kill`, `libc!geteuid`, `libc!getuid`, `libc!statx`, `libc!sysctl`, `libSystem.Native!SystemNative_Stat` | all eight. The keys are `<library>!<entry>`, so #161's `libc.so.6!geteuid` and #156's `libc!geteuid` are two DIFFERENT imports, both in the product. The list is sorted before comparing, so order does not matter |

Semantic checks beyond the text:
- `git diff f9aba6a 57806ae` over the redirect code is EMPTY (`linux-pass3-untouched.txt`, part 1).
- Main adds no entry point that could skip the guard: all three still call `ApplyFromEnvironment` first.
- Merged tree under lldeny: Linux 494 passed / 1 failed / 2 skipped, and DesktopApp 25/25. These are QA's
  numbers. The one failure is #156's `ReadLinuxProcOwner_LiveSubjectWhoseUidAndGidDiffer_ReadsUidNotGid`
  ("sg adm did not start the subject": `sg` cannot gain a group under Landlock's `no_new_privs`). It PASSES
  outside Landlock on the same build. It is not this issue's.

Noticed by the code review, not changed: after the merge, Core has two `geteuid` imports (#161 binds
`libc.so.6`, #156 binds `libc`). That is worth one shared declaration in a follow-up. Changing pass 2's
binding was not in this pass.

## 6. Code review (medium) on the pass-3 diff, and what I did with it

The review ran on the first pass-3 build, before the Architect's addition. Eight findings:
- **Fixed:**
  - `LinkTarget` could throw outside the `try` and crash the process instead of exiting 5. The walk no
    longer catches; `ApplyFromEnvironment` refuses on IOException/UnauthorizedAccessException. That also
    clears the helper-try/catch finding.
  - The macOS alias false refusal. Both spellings of the root now count, and two tests plus a mutation pin it.
  - A `qa-record` walk that could refuse a folder the process never adopts. That walk is removed, made
    unnecessary by the marker, and the `StorageMigration` edit is reverted.
  - The banner could list roots `Decide` did not compare. It now lists only what was compared.
- **Documented, not fixed:**
  - Hard links. Detecting them needs `st_nlink`, which means new native surface, and the doc already said so.
  - The walk's cost on a large scratch library and on the tray app's start-up thread. It is now stated in
    the doc.
  - The duplicate `geteuid` (section 5).

## 7. Untouched, by diff (`linux-pass3-untouched.txt`)

Every line pass 3 removed or rewrote is listed there:
- `Decide`'s signature (two optional parameters);
- its call in `ApplyFromEnvironment` (now wrapped, and given the two checks);
- the banner wording;
- `Derive`'s dedupe block, moved unchanged into `Distinct`;
- the `serve` banner line.

So these are untouched: the two variables and both-or-neither, absolute paths, unknown variables, the
overlap rules, the passwd derivation (no removed line), N1's refusal with its write probe still LAST, the
`/status` loudness fields, the three port guards, and the DesktopApp's refusal (no diff). In the tests, two
existing lines changed: the pass-2 banner assertion (F1), and the record-status fixture's root, now marked.
The marker write is pinned in `ManifestWriterIlTests.PinnedFileWrites` (Windows suite, so CI checks it).

## 8. Gates (`linux-pass3-gates.txt`)

- Under lldeny:
  - `dotnet build` of the Linux, DesktopApp and Mac test projects: 0 errors, Mac 0 warnings from this code.
    The Mac build showed one nullable warning of mine; I fixed it and re-ran.
  - **Linux suite: 530 passed / 1 failed / 3 skipped, 534** (the merged tree's 497 + 37 new). Opt-in
    `AGENTEYES_FFMPEG_TESTS=1`: 531 / 1 / 2.
  - The 1 failure is #156's `sg` test, as above.
  - The 3 skips: #156's two opt-in or sandbox tests, and the 7882 test, because the owner's service holds
    the port.
  - DesktopApp: 25 / 25.
- In the container (7882 free): the 7882 test passes.
- CI: section 10.

## 9. What I did NOT do

- **Windows is not measured, and I wrote no Windows-specific code.** The walk, the library check and the
  service query are plain .NET. On Windows, `LinkTarget` also reports junctions. The pure tests and the
  content and service tests run in the Windows suite; the on-disk link tests are SKIPPED there
  (`UnixOnlyFact`).
- I left the owner's service, tray app and the go2rtc ffmpeg alone, identifying each by command line. Every
  service I started ran on a scratch port and was stopped by its own pid or its own timeout. Nothing was
  reinstalled. The owner's library was never opened: every live run counted 0 opens of a real root.
- **Noticed, not changed:** the isolation doc's macOS HOME-trap measurement (pass 1) quotes
  `/Users/<the owner's account name>/...` paths, and so does this handoff's pass-1 sandbox transcript
  (around line 71). This is the #173 class, and worth a scrub by whoever owns #173. Every line pass 3 added
  was grepped for the account name, email domains and vendor names, with no hit.

## 10. CI

First run, head b7b0d7a (run 43940190095): **linux green; macos and windows each failed ONE new test, both
test defects of mine, fixed in the next commit:**
- macos: `RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_RedirectOntoItRefused` asserted the relocated
  root unresolved (`/var/folders/...`); the refusal names it resolved (`/private/var/...`), as every other refusal
  does. Fixed to assert the resolved path. (The test is SKIPPED on devlinux - 7882 is the owner's - and ran in my
  container on Linux, where /tmp is not a link; CI's macOS runner is the first place it met a linked temp folder.)
- windows: `AskRunningService_NothingListening_Throws` expected `HttpRequestException`; Windows retries a connect to
  a closed loopback port until the 2 s timeout and throws `TaskCanceledException`. `ApplyFromEnvironment` already
  treats both as "nothing answered", so the product was right and the test now accepts either. **The product
  consequence, now in the isolation doc:** on Windows a redirected start with no service on 7882 is 2 s slower.
  Everything else on windows passed, including `ManifestWriterIlTests` with the new marker-write pin.

Second run, head c86081c (run 43940975137): **all three green.**
- linux: 532 passed / 0 failed / 2 skipped (534); opt-in 533 / 0 / 1. The 7882 service-wiring test RAN here
  (the port is free on the runner), and #156's `sg` test passes outside Landlock, as on devlinux.
- macos: 488 / 0 / 2 (490).
- windows: AgentEyes.Tests 2565 / 0 / 31 (2596), opt-in 2584 / 0 / 16 (2600); DesktopApp 25 / 25 twice.

I believe this pass is finished.

---

# Pass 4 (Developer Agent, Linux seat) - the probe capped, one geteuid, the account name scrubbed

The Lead's three items and nothing else. Evidence: `linux-pass4/`. Everything ran on devlinux under `lldeny`
on BOTH real roots, with the denial control proven first (`linux-pass4-landlock-control.txt`: `ls` and `touch`
of each real root EACCES, a scratch `touch` exit 0). Anything that asks port 7882 on purpose ran in a
`--network none` container, as in pass 3, because 7882 on devlinux is the owner's service.

## 1. The probe: 500 ms, on 127.0.0.1, pinned

**What the 2 s was.** The probe already addressed `127.0.0.1` (pass 3 wrote it that way), so it was not a
`localhost` name falling back from IPv6. It was the client timeout itself: `ServiceTimeout` was 2 s, and on
Windows a connect to a closed loopback port is retried until that timeout runs out. CI 43940190095 shows this:
`TaskCanceledException`, not a refusal. Linux and macOS refuse at once, so the cost was Windows-only. It was
still the cost of an unbounded-feeling constant, and it now has a bound.

**The change** (`RealLibrary.cs`, two hunks):
- `ServiceTimeout = TimeSpan.FromMilliseconds(500)` (was `FromSeconds(2)`).
- The address is now `RealLibrary.StatusUri(port)`, which is the literal `http://127.0.0.1:{port}/status`, so a
  test can pin it. The service listens on `127.0.0.1` only (`HeadlessControlServer.Url`).

**Why 500 ms.**
- On devlinux, a cold redirected start whose probe gets an answer costs about 20 ms more than one refused at
  once (204 vs 188 ms before, 206 vs 184 ms after; table below). That 20 ms is the whole client side:
  JIT, connect, request and parse.
- A real service's `/status` does one thing that grows with the library: `TranscriptionBacklog.FindPending`
  scans the recordings root (`RecordingService.Status`). I cannot measure that on the owner's library without
  touching it, so the bound keeps headroom: 500 ms is 25x the measured probe.
- 500 ms is the top of "a few hundred". I took the top because the cost of a bound that is too tight is
  a safety loss, not a slow start. A service slower than the bound counts as having named nothing. The banner
  then says so, quoting the .NET message ("...configured HttpClient.Timeout of 0.5 seconds elapsing"). A
  library that only that service could name is then refused by content alone (rule 2), which means not at all
  if that folder is empty or absent. `ISOLATING_A_REAL_PROCESS.md` now says this.

**Measured on Linux, redirected start, 10 runs each, in the container** (`linux-pass4-start-times-*.txt`,
script `measure.sh` / `run-measure.sh`). The "silent" row is a stand-in that accepts the connection and never
answers. It is the shape of Windows' closed-port retry, and of a hung service, reproduced on Linux:

| 127.0.0.1:7882 in the container | before (71dc345, 2 s) | after (500 ms) |
|---|---|---|
| nothing listening (refused) | mean 188 ms (169-212) | mean 184 ms (171-201) |
| a service answering /status (stand-in naming a library; 10/10 banners list its roots) | mean 204 ms (186-214) | mean 206 ms (191-218) |
| accepts, never answers | **mean 2146 ms (2135-2162)** | **mean 645 ms (636-656)** |

So on Linux, the case the Lead named ("no service listening") was already about 190 ms and stays there. The
2-second penalty reproduces exactly on the row that stands in for Windows, and pass 4 cuts it to the bound.
The stand-in on the "answering" row is `standin.py`, a 12-line server that returns a post-#161 `/status`
body. The product's own `serve` could not run in a network-less container: it needs an X display and `xrandr`.

**Windows: NOT measured.** I have no Windows machine from this seat and did not try to reach one. By
construction, a Windows redirected start with nothing on 7882 should now pay about 0.5 s instead of 2 s, but
that is a prediction, not a measurement. The Lead is routing the Windows number separately.

**Pinned** (`LibraryRedirectTests`, in the shared suite, so it runs on Linux, macOS and Windows CI):
- `ServiceTimeout_IsAFewHundredMilliseconds`: 100 ms <= bound <= 500 ms.
- `StatusUri_AnyPort_IsTheIPv4LoopbackLiteralNotAName`: exactly `http://127.0.0.1:7882/status`, host type IPv4.
- `AskRunningService_AServiceThatAcceptsButNeverAnswers_GivesUpWithinTheTimeout`: a listener that accepts and
  never answers. The probe must throw `TaskCanceledException` within the bound + 1 s. An outer 10 s wait turns
  an unbounded client into a failure instead of a hung run. Measured: 520 ms.

**Mutations** (`linux-pass4-mutations.txt`). Each fails exactly its own test, and the file is restored after:
- M1 `FromMilliseconds(500)` -> `FromSeconds(2)` (pass 3's value): fails `ServiceTimeout_IsAFewHundredMilliseconds`.
- M2 the client's `Timeout = ServiceTimeout` -> `InfiniteTimeSpan`: fails `..._GivesUpWithinTheTimeout` (10 s).
- M3 `127.0.0.1` -> `localhost`: fails `StatusUri_..._IsTheIPv4LoopbackLiteralNotAName`.

## 2. One geteuid import

After the merge, Core had two: #161's `libc.so.6!geteuid` (`RealLibrary.Native`) and #156's `libc!geteuid`
(`FfmpegChildRegistry`). #156's import is removed. `OwnUidsOnLinux` now takes the effective uid from
`RealLibrary.EffectiveUid()`, which is the same syscall bound by the real library name. `getuid` stays where
it was. The import pin in `ManifestWriterIlTests.PinnedNativeImports` loses `libc!geteuid`, so a second
import coming back fails that test (Windows suite: CI runs it). #156's process-identity tests pass under
Landlock. The one that cannot (`ReadLinuxProcOwner_LiveSubjectWhoseUidAndGidDiffer_ReadsUidNotGid`: `sg`
under `no_new_privs`) passes when run alone outside Landlock. That test reads only `/proc`, `id` and `stat`,
and no library path (`linux-pass4-gates.txt`).

## 3. Identity scrubbed: the macOS account name

Convention: `main`'s. The owner's account reads `<owner account>`, and a harness temp path reads `<stage>`
(see `main`'s `docs/cencon/proof/issue-116/handoff.md`, "Post-QA redaction", checked there against its
unredacted ancestor).

**Scope: only what THIS branch adds.** The file list is `git diff 277a9bc HEAD` (277a9bc is the merge-base
with `main`). My first sweep used `git grep HEAD` and also matched 39 files from other issues that `main`
already carries (the issue #173 backlog). I reverted those before committing; they are #173's.

| File (all added by #161; none exist on `main`, and `main`'s copy of the isolation doc has 0) | lines `/Users/<name>` -> `/Users/<owner account>` |
|---|---|
| `docs/cencon/ISOLATING_A_REAL_PROCESS.md` | 3 |
| `docs/cencon/proof/issue-161/handoff.md` (pass 1 sections) | 13 |
| `docs/cencon/proof/issue-161/macos-kernel-denials.txt` | 13 |
| `docs/cencon/proof/issue-161/macos-proof-run1-pid-defect.txt` | 22 |
| `docs/cencon/proof/issue-161/macos-proof-transcript.txt` | 26 |
| `docs/cencon/proof/issue-161/macos-proof.sh` | 5 |
| `docs/cencon/proof/issue-161/macos-status-control-unredirected-HOME-scratch.json` | 2 |

Plus one `<stage>` redaction: `qa-linux-pass2/03-n1-missing-unwritable-order.txt` line 24 ended `log -> ` followed
by the first characters of a harness scratch folder under `/tmp` (QA's own output was cut off mid-path there).
It now ends `log -> <stage>`.

Example (handoff section 0, pass 1):
```
before: ls    '/Users/<the account name>/Movies/AgentEyes' -> exit 1 : ... Operation not permitted
after:  ls    '/Users/<owner account>/Movies/AgentEyes' -> exit 1 : ... Operation not permitted
```

**The evidence still says what it said.** For each of the six macOS proof files, the file at HEAD with only
the account name substituted is byte-identical to the redacted file (`diff` empty). The JSON still parses.
The rest of each path stays (`Movies/AgentEyes`, `Library/Application Support/AgentEyes`), because which real
root was denied or refused IS the evidence.

**Searched by shape over every line this branch adds** (`git diff -U0 277a9bc`, `+` lines, about 7200):
- Email, strict pattern: 0.
- `soren|frederiksen|mindzie|centerconsulting` outside the redacted paths: only `thefrederiksen/agenteyes-app`
  (the repository's own name) and `CLAUDE.md`'s policy text, which is the repository's, not a path.
- `/home/<name>`: 0, because Linux proof already used `<home>`.
- The positive control is the same pattern before the redaction: 84 hits in the 7 files.
- Temp paths: `/tmp/agenteyes-161-tests` (a fixed test folder name) and container-internal `/tmp/h`, `/tmp/s`,
  `/tmp/r`. None of these is identity.

## 4. Untouched, by diff (`linux-pass4-untouched.txt`)

`git diff 71dc345` over `src/` touches two files, and only these lines are removed:
- `RealLibrary.cs`: the timeout's line and its summary, and the GET's URL string (now `StatusUri`).
- `FfmpegChildRegistry.cs`: the `GetEuid` import and its one call.

NO diff in any of these:
- `LibraryRedirect.cs`: both-or-neither, the overlap rules, N1 with its write probe LAST, rule 2's marker and
  unmarked-root refusal, F2's link judgement, the banner and its comparison list, the service call site.
- `AppDataPaths.cs`, `LocalAppConfig.cs`, `src/AgentEyes.Core/Headless` (the port guards).
- `src/AgentEyes.App`.
- `src/AgentEyes.DesktopApp` (its refusal to run redirected).
- `RecordingService.cs` (the `/status` fields).

Inside `RealLibrary.cs`, no line of `Derive`, `Distinct`, `FromServiceStatus`, `VideosFromUserDirs`,
`AccountHome` or `Native` is added or removed (the passwd derivation and its union). In the tests, no
assertion is removed: the only removed lines are the `libc!geteuid` pin, a pin comment and a test comment.
The merge resolution against 277a9bc is not revisited.

## 5. Gates (`linux-pass4-gates.txt`)

- Under lldeny:
  - Build: Linux, DesktopApp and Mac test projects, 0 errors, no warning from a file pass 4 touched.
  - **Linux suite: 533 passed / 1 failed / 3 skipped (537)**. That is pass 3's 530/1/3 plus the 3 new tests,
    and the failure and skips are the same ones. Opt-in `AGENTEYES_FFMPEG_TESTS=1`: 534 / 1 / 2.
  - The 1 is #156's `sg` test under `no_new_privs`. It passes alone outside Landlock (section 2).
  - DesktopApp: 25 / 25.
- In the container (7882 free): the 7882 wiring test
  `RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_RedirectOntoItRefused` PASSES at the new 500 ms.
  A real child process, starting cold, gets the stand-in's answer inside the bound. The 3 new tests and the
  pass-3 probe tests pass there too (6/6).
- I also ran the whole Linux suite once in that container. 3 tests fail there, all for container reasons, so
  this run is not a gate: no `python3` (`CaptureOwnershipTests`), the passwd home `/home/...` does not exist
  inside it (`RealProcess_SpoofedHome...`), and no `~/.local/share` (`LinuxPathsTests`).
- CI: runs on the push. The Windows suite is where the `ManifestWriterIlTests` pin and the Windows timing of
  `..._GivesUpWithinTheTimeout` are checked.

## 6. The owner's library and the hazards

- No process of mine opened either real root. Every host command ran under lldeny, except the single `sg`
  test above.
- **Reported, not hidden:** each host run of the Linux suite sends about 18 `GET /status` to the owner's
  service on 7882. These come from the suite's real-process redirected children, which ask 7882 as every
  redirected start does. The owner's service writes its own three log lines per request into its own log.
  Read-only count (`owner-service-status-per-minute.txt`): 60/min in steady state from a client already
  polling it, 78 at 14:48 and 14:53 (my two suite runs), and the same pattern during pass 3 (14:21-14:35).
  Only `GET /status` appears in those minutes. This is the condition the Lead accepted in pass 3; it is the
  cost of running the real-process tests on a machine whose 7882 is live.
- I did not touch the go2rtc ffmpeg or any other foreign process. I started no service on the host and
  reinstalled nothing. Every stand-in ran inside a `--network none` container and ended with it.

## 7. CI - one red run, what it says about the 500 ms, and the test fix

**aa2d3e5, the push run (43944611944): all three green.** The pull_request run of the same code
(43944621928) failed on macOS and in Linux's opt-in job, in three probe tests:
- macOS, `AskRunningService_AServiceAnswering_ItsRootsComeBack`: the in-test stand-in had not answered when
  the 500 ms timeout fired.
- Linux opt-in, `RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_RedirectOntoItRefused`: the stand-in
  wrote its answer into a broken pipe. The cold child had already given up.
- Linux opt-in, `..._GivesUpWithinTheTimeout`: **"the probe took 2354 ms against a 500 ms bound"**.

**My reading, which is an inference:** this was thread-pool starvation in the parallel test host, not slowness
in the probe. Three things support it:
- A 500 ms client timeout cannot fire at 2354 ms unless its own timer callback was starved.
- Linux's default job passed on the same code and runner. Only the opt-in job failed, and that job adds real
  ffmpeg tests running in parallel.
- The stand-ins ran on the same starved pool (`Task.Run`).

I could NOT reproduce it on devlinux. I tried the whole suite in the container at 1 CPU (x3) and at 0.25 CPU
(x2), and the opt-in suite on the host pinned to one core (x3). Every probe test passed every time
(`linux-pass4b-stress-*.txt`).

**Fix, tests only (88d9a95):**
- `LoopbackServerCollection` is now `DisableParallelization = true`, so the loopback-server classes run alone,
  after the parallel collections. The Linux suite on devlinux goes from 5 s to 8 s.
- The three stand-ins serve from their own threads, never the pool. A stand-in's exception is rethrown on
  `Join`, as `Task.Wait` did, instead of crashing the test host.
- M2 (unbounded client) still fails `..._GivesUpWithinTheTimeout` (`linux-pass4b-mutations-b.txt`).
- Gates under lldeny are unchanged: 533/1/3, opt-in 534/1/2, DesktopApp 25/25. In the container, the 7882
  wiring test and the probe tests pass 6/6.

**88d9a95, both runs green.** Push 43946693841 and pull_request 43946705739, each:

| Job | Result |
|---|---|
| linux | 535 / 0 / 2 (537); opt-in 536 / 0 / 1 |
| macos | 491 / 0 / 2 (493) |
| windows AgentEyes.Tests | 2568 / 0 / 31 (2599); opt-in 2587 / 0 / 16 (2603) |
| windows DesktopApp | 25 / 25, twice |

Windows ran `..._GivesUpWithinTheTimeout`, so the bound holds against a listener that never answers there.
It also ran the `ManifestWriterIlTests` pin without `libc!geteuid`. Neither is a Windows start-time
measurement.

**For the Lead, because it bears on the number you set:** the product probe runs in a fresh process with an
idle pool, and asks another process. A starved test host is not that. But if a whole machine stalls for more
than 500 ms during a redirected start, the service's roots drop out of the union for that start. The banner
says so, and rule 2 still refuses a non-empty unmarked library. There is a refinement that keeps "costs
nothing when nothing is there" without making a slow live service count as absent: a short CONNECT timeout
(Windows' closed-port retry happens before a connection exists), with a longer bound once a service has
accepted. I did not build it, because pass 4 is three items and no new design. If you want it, it is small.

---

# Pass 5 (Developer Agent, Linux seat) - the timeout split, the probe's port injectable

The Lead's two items and nothing else. Evidence: `linux-pass5/`. Everything on devlinux ran under `lldeny` on
BOTH real roots, with the denial control proven first (`linux-pass5-landlock-control.txt`: `ls` and `touch` of
each real root EACCES, a scratch `touch` exit 0). Anything that asks port 7882 on purpose ran in a
`--network none` container (its 7882 is its own). After item 2, only one test does that, and only one
mutation run needs the container (section 4).

## 1. Two bounds instead of one

`RealLibrary.AskRunningService` now uses a `SocketsHttpHandler` with a CONNECT bound and an `HttpClient` with a
RESPONSE bound. `ServiceTimeout` is gone.

| Bound | Value | Question it answers | Who pays it |
|---|---|---|---|
| `ConnectTimeout` | **500 ms** | is anything listening? | Windows, every redirected start where nothing listens (Linux and macOS refuse at once) |
| `ResponseTimeout` | **5 s** | is what listens answering? | only a service that accepts and then never answers |

**Why 500 ms to connect.**
- A loopback connect to a LISTENING socket is completed by the kernel's handshake. It does not wait for the
  service to accept, let alone answer, so a busy service does not make it slower.
- What the bound can catch is a stall of the probing process itself. A connect that the kernel has already
  completed is queued ahead of the timer that fires 500 ms later.
- 500 ms keeps the Windows nothing-listening cost exactly where pass 4 measured it and the Lead accepted it.
  A lower number would buy Windows less than half a second and give a stalled machine less room.

**Why 5 s for the response.**
- A real service's `/status` scans its recordings root for pending transcriptions, the one part that grows with
  the library.
- An answered probe costs about 20 ms on devlinux (pass 4), so 5 s is about 250x that.
- 5 s is twice the worst stall CI has shown: a 500 ms timer that fired at 2354 ms (43944621928).
- The upper end, pinned at 10 s, keeps a hung service from holding a redirected start for longer than that.

## 2. The three shapes, re-measured (plus two)

A redirected start of the real `agenteyes.dll`, 10 runs each, in a `--network none` container. Before is pass
4's tree (6ee962d, built in a scratch worktree), measured today alongside after. Files:
`linux-pass5-start-times-{before,after}.txt` and `linux-pass5-slow-service.txt`. Scripts: `measure.sh`,
`measure-slow.sh`, `fullq.py`, `slow.py` (the stand-ins from pass 4 are in `../linux-pass4/`).

| 7882 in the container | pass 4 (one 500 ms bound) | pass 5 (500 ms connect, 5 s response) | what the banner says (pass 5) |
|---|---|---|---|
| nothing listening | 183 ms | 193 ms | named none: Connection refused |
| a service answering | 204 ms | 215 ms | its roots are in the list (`/status DataRoot`) |
| accepts, never answers (a hung service) | 641 ms | **5143 ms** | named none: HttpClient.Timeout of 5 seconds |
| connect never completes (full accept queue; the Windows closed-port retry, on Linux) | 650 ms | **710 ms** | named none: The operation was canceled |
| **a service that answers after 1.5 s** | 640 ms, **roots DROPPED** (10 of 10) | 1715 ms, **roots ADDED** (10 of 10) | its roots are in the list |

- The last row is the point of the ruling. Under pass 4 a listening but slow service did not count. Under pass
  5 it does, every time.
- The price is the third row: a service that accepts and hangs now holds a redirected start for 5 s instead of
  0.5 s. It is a broken service, and the banner names it.
- The fourth row shows that the connect bound, not the response bound, ends a connect that never completes.
- The connect-bound message is .NET's generic "The operation was canceled." I left the text alone: changing the
  note's wording is not one of the two items.
- **Windows: not measured.** Still owed, and routed separately as the Lead said.

## 3. The test serialisation: HALF came out, and the other half shows something else

Pass 4 added two things. One came out. The other could not, and the reason is not the probe.

**`DisableParallelization`: OUT.** `LoopbackServerCollection` is back to pass 3's exact declaration, with no
`[CollectionDefinition]`.
- Its job was to keep a loaded runner from making the probe's 500 ms bound miss.
- The bound that has to survive a stall is now 5 s, so that cause is gone.
- Host gates under lldeny, parallel again: Linux 553 / 1 / 3, opt-in 554 / 1 / 2. The 1 is #156's `sg` test,
  which cannot run under Landlock and passes alone outside it. DesktopApp 25 / 25 (`linux-pass5-gates.txt`).
- Pass 4's container stress, repeated with the whole opt-in suite at `--cpus=1` x2 and `--cpus=0.25` x2: every
  probe test passed every time (`linux-pass5-stress-container.txt`). The four failures there are the same
  container-only ones as pass 4's baseline (`../linux-pass4/linux-pass4b-stress-before.txt`): spoofed HOME, XDG
  path, ffmpeg kill, claim. None is a probe test.
- CI on the final commit is in section 7.

**The stand-ins' own threads: could NOT come out.** I removed them in 3ca8775/f44fc60, with the stand-ins back on
`Task.Run`. Then the Windows opt-in job of pull_request run 43952934588 failed
`AskRunningService_AServiceSlowerThanTheConnectTimeout_IsStillHeard`:
- It took **5 s**: the probe ran into its full response bound.
- What was reported was the stand-in's exception, "Not listening", thrown by `AcceptTcpClient` on what was its
  FIRST accept. It ran after `Dispose` had already stopped the listener.
- Reading: the pool-hosted stand-in did not get a thread for more than 5 s. The probe was not too tight. **The
  Windows opt-in test host starves its own worker pool for seconds.** That is something else going on, and it is
  outside this issue. The build also warns `xUnit1031` (blocking task operations in test methods, e.g.
  `PostRecordingQueueTests`), which is one candidate. I did not chase it.
- The push run of the same commit (43952922286) was green on all three OSes, so it is intermittent, as pass 4's
  was.

**Fix, tests only.**
- `StatusStandIn` and the never-answering acceptor serve from pass 4's `StandIn` thread again. The `StandIn`
  helper is byte-identical to pass 4's.
- They also catch the `InvalidOperationException` that Windows throws when a listener is stopped before an accept
  begins. A late stand-in can no longer replace the test's real failure with its own.

## 3a. The full-queue test does not run on macOS

`AskRunningService_AConnectThatNeverCompletes_GivesUpAtTheConnectTimeout` failed on macOS in CI 43952024044 with
its own instrument message, "the listener's accept queue never filled, so no connect hung: this test measured
nothing". 64 connects to a `Listen(0)` socket all completed in 20 ms there. Linux and Windows passed it.
- It is now `[ConnectCanHangFact]`: reported SKIPPED on macOS with that reason, never silently passed.
- macOS refuses a closed loopback port at once, so its probe never waits on the connect bound in the shape that
  matters.
- `ConnectTimeout_IsAFewHundredMilliseconds` pins the value everywhere, and M1 fails the full-queue test on Linux.

## 4. The probe's port is injectable; no test asks the owner's service

**The product.**
- `LibraryRedirect.ServiceProbePortVariable = "AGENTEYES_SERVICE_PROBE_PORT"`. Unset means 7882, as before.
  Set means the probe asks `127.0.0.1:<that port>`. A value that is not a port in 1-65535 REFUSES the start
  (`ServiceProbePort`, pure). It is never quietly sent to 7882 instead.
- It sits deliberately OUTSIDE the `AGENTEYES_REDIRECT_` prefix, so the two-variable rule and its unknown-variable
  refusal are untouched.
- **The trade-off, stated for the Lead.** The variable can only take the service half of the union away. The
  passwd and environment halves still apply, and so does rule 2. It is therefore never silent: every banner of a
  process that honored it begins the service note with "the service probe was pointed at 127.0.0.1:N by
  AGENTEYES_SERVICE_PROBE_PORT, so the default port 7882 was NOT asked" (`ServiceProbeNote`, pure).
- A child process can only be told a port through its environment. An in-process seam would not have reached
  the ~18 real children that sent the GETs.
- `TestIsolationTests.PinnedEnvironmentReads` gains the one line for it (Windows-only test; CI runs it).

**The tests.**
- Every child the suite starts gets a port of its own, a fresh closed one unless the test passes a stand-in's.
  This covers `LibraryRedirectTests.ChildStartInfo` and `DesktopAppRedirectTests.RunDesktopApp`.
- **Pin:** `ChildStartInfo_EveryRedirectedChild_ProbesAPortOfItsOwnNeverTheDefault`.
- `RealProcess_ServiceOnTheInjectedPortNamesARelocatedLibrary_RedirectOntoItRefused` is the rule-1 wiring test on
  EVERY machine. A real child asks the stand-in's port and is refused by the library the stand-in names, and the
  stand-in records exactly one request, `GET /status HTTP/1.1`.
- The default-port twin stays as `[DefaultPortFreeFact]`. It is the one test that still plays the service on
  7882, and it does so only where 7882 is free. It pins the production default.
- Also new: `ServiceProbePort_*` (unset, three ports, eight non-ports), `ServiceProbeNote_*` (2), and
  `RealProcess_ServiceProbePortNotAPort_RefusedWithExit5AndWritesNothing`. The banner test now asserts the
  "NOT asked" note with its port.

**Measured: zero.** Each gate ran under `strace -f -e trace=connect`:

| Run | connects to 127.0.0.1 | to port 7882 |
|---|---|---|
| Linux default | 49 | **0** |
| Linux opt-in | 49 | **0** |
| DesktopApp | 3 | **0** |

- Pass 4 counted about 18 `GET /status` per host suite run in the owner's service log.
- **Instrument control first** (`linux-pass5-trace-control.txt`). One real child pointed at a closed port P
  shows exactly one `port=htons(P)` connect. It appears in the dual-mode form `sin6_port=htons(P) ...
  ::ffff:127.0.0.1`. My first grep (`sin_port=`) saw only 2 of the 49 connects and reported 0 for 7882 vacuously.
  I caught that, corrected the pattern, and every count above uses the corrected one.
- I did not re-run pass 4's suite on the host for a fresh "before": that would have sent the owner's service
  another 18 GETs.

**The container is no longer needed for the tests.** It is needed only to run the default-port test on a machine
whose 7882 is taken, and for mutation M5 below.

## 5. Regression tests and mutations (`linux-pass5-mutations.txt`)

New timeout tests:
- `ConnectTimeout_IsAFewHundredMilliseconds`: 100 to 500 ms.
- `ResponseTimeout_IsSeconds`: 5 to 10 s.
- `AskRunningService_AServiceSlowerThanTheConnectTimeout_IsStillHeard`: the stand-in answers after
  ConnectTimeout + 1 s.
- `AskRunningService_AConnectThatNeverCompletes_GivesUpAtTheConnectTimeout`: a full accept queue, on Linux and
  Windows (section 3a). It FAILS, rather than passes, if the queue never fills or a connect is refused at once.
- `..._AcceptsButNeverAnswers_GivesUpAtTheResponseTimeout`: now also asserts it was NOT cut short at the connect
  bound.

| Mutation | Fails |
|---|---|
| M1 no `ConnectTimeout` | `..._AConnectThatNeverCompletes_...` (5 s) |
| M2 response bound = connect bound (pass 4's single 500 ms) | `..._SlowerThanTheConnectTimeout_IsStillHeard`, `..._AcceptsButNeverAnswers_...` |
| M3 unbounded response | `..._AcceptsButNeverAnswers_...` (15 s) |
| M4 the test launcher sets no probe port | `ChildStartInfo_..._NeverTheDefault` (no child started) |
| M5 the product ignores the variable (container: its children ask 7882) | `..._InjectedPort...`, `..._ServiceProbePortNotAPort_...`, `..._BannerNames...`; the default-port test still passes |

## 6. Untouched, by diff (`linux-pass5-untouched.txt`)

- Changed: `LibraryRedirect.cs`, `RealLibrary.cs`, three test files.
- (This file was produced before the last two test-only commits, which touch only `LibraryRedirectTests.cs`.)
- Every hunk in the two source files is in the probe: the new constant, the probe block of
  `ApplyFromEnvironment`, the two new pure helpers, the timeouts and `AskRunningService`.
- No diff: `AppDataPaths`, `LocalAppConfig`, `Program` (CLI), `HeadlessHost`, `App`, `DesktopApp/Program`,
  `RecordingService`.
- Byte-identical bodies at 6ee962d and now: `Decide`, `UsableFolderProblem` (N1, write probe last),
  `FirstEscapingLink` (F2), `UnmarkedLibraryProblem` and `WriteScratchMarker` (the marker rule), `Banner`,
  `ComparisonLimit`, `RealRootsStatement`, `ServicePortRefusal`, `ResolveLinks`, `DeriveForThisProcess`,
  `Derive`, `Distinct`, `FromServiceStatus`, `VideosFromUserDirs`, `AccountHome`, `StatusUri`, `Native`.

## 7. CI

| Commit | Run | Result |
|---|---|---|
| 64bee6f | push 43952013771, pull_request 43952024044 | linux and windows green; **macos red**: the full-queue test, which fails closed (section 3a) |
| f44fc60 (macOS skip) | push 43952922286 | **green on all three** |
| f44fc60 | pull_request 43952934588 | linux and macos green; **windows opt-in red**: a pool-hosted stand-in starved for over 5 s (section 3) |
| final | see the issue comment | |

## The owner's library and service

- No process of mine opened either real root. Every host build and test ran under lldeny. The one exception
  is #156's `sg` test, run alone outside Landlock; it reads only `/proc`.
- **One read-only look, reported.** Right after the control, I ran `ls` OUTSIDE Landlock on the two control
  paths (`<real state root>/logs/.p5-probe`, `<real recordings root>/.p5-probe`) to confirm the control had
  created nothing. Both are "No such file or directory". It was a lookup only; nothing was opened or written.
- No request of mine reached the owner's service. Zero connects to 7882 is traced above.
- Foreign processes, left alone and identified by command line: the owner's service (`agenteyes serve`), the
  installed `AgentEyesLinux --tray`, the camera wall (`go2rtc` and its ffmpeg), the `agenteyes-161fix` seat's
  test host and its two ffmpeg (x11grab, pulse), and a root `dotnet publish` of another product.

I believe this pass is finished, pending CI.
