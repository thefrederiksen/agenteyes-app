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
