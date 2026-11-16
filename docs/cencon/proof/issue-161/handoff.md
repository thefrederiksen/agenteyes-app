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
ls    '/Users/soren/Movies/AgentEyes' -> exit 1 : ls: /Users/soren/Movies/AgentEyes: Operation not permitted
touch '/Users/soren/Movies/AgentEyes/.agenteyes161-sandbox-probe' -> exit 1 : ... Operation not permitted
ls    '/Users/soren/Library/Application Support/AgentEyes' -> exit 1 : ... Operation not permitted
touch '/Users/soren/Library/Application Support/AgentEyes/.agenteyes161-sandbox-probe' -> exit 1 : ... Operation not permitted
touch scratch -> exit 0
```

Probe files absent afterwards (checked); the kernel logged each of those denials
(`macos-kernel-denials.txt`, `ls(...)`/`touch(...)` lines) - so the denial instrument is proven to fire.

### AC1 - a real service, redirected, does not read the real library

**Built:** the redirect moves both roots before anything runs; the repair pass, housekeeper and orphan
reap all build their paths from those roots.

**Measured (macOS):** real library present and non-empty: `recording folders in /Users/soren/Movies/AgentEyes: 79`.
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
  denied it on exactly the real roots - `/Users/soren/Movies/AgentEyes` and
  `/Users/soren/Library/Application Support/AgentEyes/logs` (6-9 lines per run). So the check can fail,
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
`... puts the redirected recordings root at /Users/soren/Movies/AgentEyes, which overlaps the REAL recordings root ...`.
Also refused: the symlinked scratch root (`... (which resolves to /Users/soren/Movies/AgentEyes) ...`),
a typo'd variable, a redirected serve on 7882. All in the transcript, Section 4.

**QA:** run the same five on Windows (PowerShell `$env:`) and Linux; for the tray app, a partial set
must show the "AgentEyes did not start" dialog (exit code 5) and write NOTHING to either log.

### AC4 - the macOS HOME trap, measured

1. **Runtime, touching nothing** (standalone probe, `home-trap-probe/`; .NET 10.0.15 roll-forward):
   ```
   HOME=<scratch>/home -> UserProfile=<scratch>/home
                          MyVideos=/Users/soren/Movies
                          LocalApplicationData=/Users/soren/Library/Application Support
   ```
2. **The product** (`HOME=<scratch>/home agenteyes serve --port 17962`, NO redirect, under the sandbox):
   its own `/status` (`macos-status-control-unredirected-HOME-scratch.json`) reported
   `"RecordingsRoot": "/Users/soren/Movies/AgentEyes"`, `"DataRoot": "/Users/soren/Library/Application Support/AgentEyes"`,
   `"LibraryRedirected": false`; its stdout said `recordings -> /Users/soren/Movies/AgentEyes`; and
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
