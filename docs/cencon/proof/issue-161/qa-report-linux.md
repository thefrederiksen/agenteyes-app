# Issue #161 / PR #175 - QA report, LINUX HALF ONLY

**Verdict: FAIL -> flow:qa-failed.** One blocking defect (D1): on Linux, the guard that refuses a
redirect into the REAL library can be defeated by the process's own `HOME` or `XDG_DATA_HOME`.
When that happens, the process goes into the person's real state root (credential, ffmpeg registry,
live log), and it announces "this is NOT the person's real library" while doing so. Everything else
measured on Linux holds: refusals, the redirect itself, the kernel-proven untouched real library,
all three port guards, loudness, the log, the diff scope, the IL pins and the gates.

- **Seat:** QA, Linux half only. The developer measured macOS. Windows is queued for another seat
  and is NOT covered here.
- **Machine:** devlinux, kernel 6.8.0-149 (Landlock ABI 4), .NET SDK 8.0.134, runtime 8.0.34.
- **Head tested:** `c595c41077f9ebedc3c8037989baeeae1acca584` (branch `issue-161-scratch-library-root`,
  merge-base with main `17c244d`).
- **Binary run:** `src/AgentEyes.Core/bin/Release/net8.0/agenteyes(.dll)` built here from that head.
  Its embedded version is `1.11.11+c595c41077f9ebedc3c8037989baeeae1acca584`, read out of the dll
  that was run.
- Artefacts: `qa-linux/` next to this file. Paths are scrubbed: `<home>` is the owner's home,
  `<scratch>` is this seat's private temp folder, `<repo>` is the checkout.

## Section 0 - the instrument, proven BEFORE it was relied on

The sandbox is `qa-linux/harness/lldeny.c`, a Landlock wrapper written for this run. Landlock only
grants, so it grants everything EXCEPT the two real roots: at each ancestor it grants every sibling
and leaves out the path to `<home>/Videos/AgentEyes` and `<home>/.local/share/AgentEyes`. It then
execs the command with the same pid, so the process and every child are confined. Every
`agenteyes` run below (except the one incident in Section 9) ran as
`strace -f -e trace=%file,%desc lldeny <R1> <R2> -- agenteyes ...`.

`qa-linux/00-landlock-control.txt`, run first:

```
ls    <home>/Videos/AgentEyes -> exit 2 : ... Permission denied
touch <home>/Videos/AgentEyes/.qa161-landlock-probe -> exit 1 : ... Permission denied
ls    <home>/.local/share/AgentEyes -> exit 2 : ... Permission denied
touch <home>/.local/share/AgentEyes/.qa161-landlock-probe -> exit 1 : ... Permission denied
ls/touch <home>/.local/share/AgentEyes/logs -> exit 2 / 1 : Permission denied
cat   <home>/.local/share/AgentEyes/linux-app.json -> exit 1 : Permission denied
touch scratch -> exit 0          ls ~/ReposFred (sibling) -> exit 0
probe files afterwards: No such file or directory (x3)
```

**What it cannot see:** Landlock has no `stat` right. A metadata lookup of a path name (stat,
readlink, access) is NOT refused, so it is reported from the strace instead. It covers only the two
real roots. Everything else on the machine is outside it.

## 1. The refusal matrix - FAIL (D1); two of the brief's cases are not refused (N1)

Script: `qa-linux/harness/matrix.sh`. Output: `qa-linux/01-matrix.txt` (second, scripted run; the
first exploratory run gave identical exit codes).

| Case | Exit | Real-root syscalls | Written |
|------|------|--------------------|---------|
| a DATA only / b VIDEOS only / n DATA="" | 5 / 5 / 5 | 0 | nothing |
| c DATA relative / c2 VIDEOS `./` relative | 5 / 5 | 0 | nothing |
| j typo `AGENTEYES_REDIRECT_VIDOES` | 5 | 0 | nothing |
| k both roots the same folder | 5 | 0 | nothing |
| f DATA=`<home>/.local/share` / g VIDEOS=`<home>/Videos` / g2 inside real | 5 / 5 / 5 | 3 / 3 / 6 stat+readlink | nothing |
| h scratch link -> `<home>/Videos`; i `videos/AgentEyes` -> real root; i2 `data/AgentEyes` -> real state root | 5 / 5 / 5 | 3-4 stat+readlink | nothing |
| **p POSITIVE CONTROL**, correct redirect | **0** | 0 | scratch log only |
| d paths that do not exist | **0** | 0 | creates `<d>/data/AgentEyes/logs/...` |
| e paths that exist but are unwritable (chmod 555) | **0** | 0 | nothing; the log goes nowhere |
| x1 CONTROL: DATA=`<home>/.local/share`, real HOME | **5** | 3 | nothing |
| **x2 same DATA, `HOME=<scratch>/x2/home`** | **0** | 18, **5 EACCES on the owner's live log** | - |
| **x3 same DATA, `XDG_DATA_HOME=<scratch>/x3/xdg`** | **0** | 18, **5 EACCES on the owner's live log** | - |
| **x4 VIDEOS=`<home>/Videos`, `HOME=<scratch>/x4/home`** | **0** | 3 | recordings root = the REAL one |

Every true refusal printed the reason with the fix on stderr, exited 5, and left nothing in the
scratch folders: the `created ... 0 new` line for each case in `01-matrix.txt`. None crashed.

The refusals that name a real root DO `stat`/`readlink` that root (metadata only, never opened).
This is the developer's own disclosed limit 2, reproduced on Linux.

### D1 (BLOCKING) - HOME / XDG_DATA_HOME defeats the real-root overlap guard

- **Where:** `src/AgentEyes.Core/LibraryRedirect.cs:239-240` passes `AppDataPaths.MachineLocalAppData`
  and `AppDataPaths.MachineVideos` as "the REAL" roots. Those are `Environment.GetFolderPath(...)`
  (`src/AgentEyes.Core/AppDataPaths.cs:39` and `:46`), which on Linux are computed from THIS
  process's `HOME` / `XDG_DATA_HOME` / user-dirs.
- **Two mechanisms, both measured:**
  - (a) `HOME=<empty scratch>`: .NET returns "" because the folder does not exist. The
    `IsPathFullyQualified` skip at `LibraryRedirect.cs:186` / `:188` (the deliberate #169 skip)
    then drops the real root from the check altogether. Cases x2 and x4.
  - (b) `XDG_DATA_HOME=<existing scratch>`: the "real" root becomes the scratch folder. Case x3.
- **Severity, measured on a real serve.** It ran under Landlock, so it could not touch anything
  (`qa-linux/05-home-defeated-serve.txt`, `04-status.txt`). The command was
  `HOME=<scratch>/homeatk/home AGENTEYES_REDIRECT_DATA=<home>/.local/share AGENTEYES_REDIRECT_VIDEOS=<scratch>/homeatk/videos agenteyes serve --port 17163`.
  It was ACCEPTED. Its stdout said `data -> <home>/.local/share/AgentEyes` and
  `[redirect] LIBRARY REDIRECTED ... - this is NOT the person's real library`. Its `/status` said
  `"DataRoot": "<home>/.local/share/AgentEyes", "LibraryRedirected": true`. Landlock then refused
  it: `devthrottle.cred` x2 (the owner's sign-in), `run/ffmpeg` x1 (the orphan reap of the owner's
  ffmpeg registry), and the owner's live log x26. Without a sandbox, that service would have been
  SIGNED IN as the owner. Its orphan reap would have run against the owner's service's ffmpeg
  registry, and every line would have gone into the owner's live log, under a banner saying the
  opposite. This is the #161 incident shape again, with the safety net reporting green.
- **Why it is reachable:** setting `HOME` to a scratch folder is exactly the isolation habit this
  issue documents as a trap. On Linux it DOES move both roots (`qa-linux/11-linux-home-moves-roots.txt`,
  measured: `state=<scratch>/hm2/home/.local/share/AgentEyes recordings=<scratch>/hm2/home/Videos/AgentEyes`).
  So a seat that sets `HOME` and then writes a real path into one variable by mistake gets no
  refusal.
- **Expected:** a redirect that lands on the account's real roots is refused (exit 5) whatever
  `HOME` / `XDG_*` this process was given. **Actual:** accepted, exit 0.
- **For the developer (not a prescription):** derive the real roots also from the account (the
  passwd home with the default XDG layout, not only this process's environment). Refuse, rather
  than skip, when a real root cannot be determined. Add a test that sets `HOME` / `XDG_DATA_HOME`
  on a real child process. The matrix in `qa-linux/harness/matrix.sh` (x1-x4) is a ready
  regression.

### N1 (not blocking by the issue's own criteria; a decision for the Lead) - non-existent and unwritable are accepted

The QA brief lists "a path that does not exist or is unwritable" as must-refuse. The issue's
criteria and the handoff do not, and the code (`LibraryRedirect.Decide`) never checks either. Measured:

- **d:** a non-existent path is created. A typo makes a new library. The banner names it, so the
  result is loud, not silent.
- **e:** an unwritable path is accepted. A redirected `serve --port 17164` on it STARTED, answered
  `/status` 200, and wrote no log anywhere, with no error (`qa-linux/10-unwritable-redirect.txt`).
  The WARN redirect line is lost; stderr, stdout and `/status` are still loud.

I am not redefining the brief's criterion. I report it as not met, and leave the call (defect or
accepted) to the Lead.

## 2. It really redirects; the real library is not touched - PASS

The scratch library was 2 recordings imported redirected under the sandbox (`import` exit 1:
"Not signed in" - a redirected process is signed out, disclosed). A redirected
`agenteyes serve --port 17161` (pid 1733556) ran under strace + Landlock from 12:33:46 to 12:38:30.
Its OWN log (`qa-linux/02-redirected-serve-log.txt`):

```
[FfmpegChildRegistry] ReapOrphans: folder=<scratch>/lib/data/AgentEyes/run/ffmpeg
[RepairService] ResumeUnfinishedAsync: root=<scratch>/lib/videos/AgentEyes
[RepairService] ResumeAsync: 2 recording(s) with unfinished post-processing (hostedWork=False)
[PostRecording] Resume: dir=<scratch>/lib/videos/AgentEyes/2026-11-16_123335_clip1 hostedWork=False
[PostRecording] Resume: dir=<scratch>/lib/videos/AgentEyes/2026-11-16_123338_clip2 hostedWork=False
[Housekeeper] Run: trigger=repair-pass, reportOnly=True, root=<scratch>/lib/videos/AgentEyes
[Housekeeper] Run: trigger=repair-pass done - 2 recording(s), 0 action(s), would reclaim 0 MB
```

**Absence, with its positive control on the same instrument** (`qa-linux/07-kernel-and-real-library.txt`):

- Redirected serve: 26402 file/desc syscalls, 2155 openat, **0** naming either real AgentEyes root.
  This covered the repair pass, the housekeeper, a 5 s recording and its post-processing.
  Attributed by strace line: the only real-root PARENT activity after exec is
  `access`/`newfstatat`/`readlink` of `<home>/Videos` and `<home>/.local/share` (the overlap
  check, metadata only). The two `openat(O_DIRECTORY)` on those parents are the sandbox's own walk,
  before exec.
- **Positive control:** UNredirected `agenteyes record status` in the same sandbox: **25** syscalls
  on the real roots, **8 EACCES** on the owner's live log. The instrument fires for the process
  that should fire it.
- The real library afterwards (QA, read-only, outside the sandbox): 82 recording folders (82
  before), `find -newer <t0>` gives 0 entries, `2026-11-14_102532_video` is present, the owner's
  service is idle, and 0 lines in the owner's log name the scratch folder.

## 3. The port guards - PASS, all three, each with a positive control

`qa-linux/06-port-guards.txt`. The owner's 7882 service is a pre-#161 build (no `DataRoot`).

| Probe | Exit | Result |
|-------|------|--------|
| A1/A2/A3 POSITIVE: redirected CLI, same library, Port 17161 in scratch config: status / start / stop | 0 / 0 / 0 | sent. The service's log shows `GET /status` then `POST /record/start` / `POST /record/stop`. The recording was saved in the scratch library. |
| B1/B2 redirected CLI, fresh scratch (no Port -> 7882) -> owner's service: `record status` / `record stop` | 1 / 1 | refused: "does not report which library ... predates issue #161 ... REAL library". The owner's log gained only `GET /status`, no POST. The owner's service stayed idle. |
| C redirected CLI -> port 17162, nothing listening | 1 | "not answering on http://127.0.0.1:17162/". Nothing sent. |
| D redirected CLI, a DIFFERENT scratch library -> my redirected 17161 | 1 | refused, "DIFFERENT library ... Nothing was sent". 17161's log: GET /status only. |
| E UNredirected CLI (`XDG_DATA_HOME` scratch config, Port 17161) -> redirected 17161 | 1 | refused, same message. 17161's log: GET /status only. |
| F POSITIVE: UNredirected CLI -> owner's unredirected 7882, `record status` | 0 | answered (idle, signedIn=True). The guard does not break the normal CLI. |
| S1 redirected `serve --port 7882` / S2 redirected `serve` with no port | 1 / 1 | "must not listen on the default port 7882". Refused BEFORE binding: the message is the redirect refusal, not "address in use". |
| LA1 redirected `AgentEyesLinux` / LA2 partial | 5 / 5 | refused. Its only `connect()` calls were the .NET runtime's lttng probes: no 7882, no handoff to the owner's running tray. LA1 writes its refusal into the SCRATCH log (redirect already applied). That is by design, not a real-log write. |

The mutating probe against the owner's service used `record stop` on an IDLE service, not
`record start`. Same `Call()` path, no risk of starting a real recording. Stated, not hidden.

**Not covered here:** the positive control for the Linux app (an unredirected second
`AgentEyesLinux` would hand "show" to the owner's running tray on their desktop). Its redirect
check is the first statement of `Main`, by review: `src/AgentEyes.LinuxApp/Program.cs:29-49`.

## 4. Loudness - PASS for a correct redirect; D1 makes it lie

The correct redirect is loud on every surface, and the values match what the process used:

- `/status` (`qa-linux/04-status.txt`): `"DataRoot": "<scratch>/lib/data/AgentEyes"`,
  `"RecordingsRoot": "<scratch>/lib/videos/AgentEyes"`, `"LibraryRedirected": true`,
  `"LibraryRedirect": "environment (AGENTEYES_REDIRECT_DATA, AGENTEYES_REDIRECT_VIDEOS)"`.
- These are the paths it actually wrote to: the recording `<scratch>/lib/videos/AgentEyes/2026-11-16_123447_video`
  and the log `<scratch>/lib/data/AgentEyes/logs/AgentEyes-20261116.log`.
- stderr `[redirect] LIBRARY REDIRECTED ...` on every redirected command. serve stdout prints
  `data ->` and `[redirect] ... NOT the person's real library`. The log carries two WARN lines
  (`LibraryRedirect`, `HeadlessHost`) (`qa-linux/03-*.txt`, `02-*.txt`).

Under D1 the same surfaces report `LibraryRedirected: true` and "NOT the person's real library"
for a process whose `DataRoot` IS the person's real one. A consumer is fooled exactly as the issue
says it must never be.

## 5. The log and the crash log on Linux - PASS

- The log moves with the state root (`Log.Dir = AppDataPaths.Root/logs`, `src/AgentEyes.Core/Log.cs:22`).
  It is the only file sink in Core and the Linux app. Every redirected run logged only into its
  scratch `logs/`, and the owner's log has 0 lines naming the scratch folder.
- **There is no TEMP crash log on Linux.** `Path.GetTempPath` appears in Core/LinuxApp only for
  `VideoFrame` temp frames and the Linux app's single-instance socket fallback, and the
  `UnhandledException` handler logs through `Log`. The `%TEMP%\AgentEyes-crash.log` gap is the
  Windows tray app's, outside this half.
- Refusals log nowhere (stderr only), confirmed by the empty scratch folders in `01-matrix.txt`.
- Caveat: an unwritable redirected log root makes the log vanish silently (N1 e). This is
  pre-existing `Log.Write` behaviour, disclosed by the developer.

## 6. Nothing else moved - PASS (merge-time finding M1 below)

- `git diff 17c244d..c595c41 --stat`: 28 files, all redirect / entry points / port guards / tests /
  docs / proof. Product code: `LibraryRedirect.cs` (new), `AppDataPaths.cs` (doc + `MachineVideos`
  extracted, `get_Videos` now calls it), `Program.cs`, `CliDispatcher.cs` (log line),
  `HeadlessHost.cs`, `HeadlessCli.cs`, `RecordingService.cs` (four `/status` fields),
  `LinuxApp/Program.cs`, `App/App.xaml.cs`.
- `AppDataPaths.RedirectForThisProcess`: the method body is byte-identical to the base (md5 of the
  extracted method, head = base = `fd702f0a...`).
- **TestIsolationTests IL guard.** `AgentEyes.Tests` is net8.0-windows and the distro SDK here has
  no WindowsDesktop targets, so it cannot be compiled on this machine. Two things instead:
  1. The guard's OWN scanner (`tests/AgentEyes.Tests/CompiledCode.cs`, linked unchanged into
     `qa-linux/harness/ilscan`) was run over the Linux-built `agenteyes.dll` at the head AND at
     base 17c244d (`qa-linux/08-il-guard-inventory.txt`). Head reports exactly the new pins:
     `get_MachineVideos -> MyVideos x1`, `ApplyFromEnvironment -> AGENTEYES_REDIRECT_DATA x1` /
     `_VIDEOS x1`, `ApplyFromEnvironment -> get_MachineLocalAppData x1`. **Mutation:** base vs head
     differ on exactly those four lines, so the head pins FAIL against the base dll. The guard
     sees the change. The Windows-only `FfmpegLocator::Find` lines do not exist in a Linux build.
  2. The real guard, compiled and run on Windows: CI run 43917991932, windows job, at this head:
     `Build succeeded. 0 Error(s)`, `AgentEyes.Tests.dll: Failed 0, Passed 2415, Skipped 18,
     Total 2433`. TestIsolationTests is not skippable, so its pins passed there.
- **M1 (merge-time; not a defect at this head):** main moved to 2d60f77 (#141, one desktop app)
  after this branch was cut. The PR merges textually clean, but
  `origin/main:src/AgentEyes.DesktopApp/Program.cs:32` `Main` has no `ApplyFromEnvironment` call,
  references AgentEyes.Core, and hard-codes 7882 (`Services/ControlClient.cs:41`). After a merge, a
  redirected DesktopApp would silently ignore the redirect: the half-redirect closed here for
  `AgentEyes.LinuxApp`. The branch needs a rebase and the same refusal in the new entry point.

## 7. Gates - PASS on Linux; CI incomplete

| Gate | Where | Result |
|------|-------|--------|
| `dotnet build src/AgentEyes.Core -c Release -f net8.0` | devlinux | 0 errors, 1 warning (CS8602 AlwaysOnEngine.cs:901, pre-existing) |
| `dotnet build src/AgentEyes.LinuxApp -c Release` | devlinux | 0 errors, 0 warnings |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | devlinux | **Total 335, Passed 334, Skipped 1 (#151 ffmpeg opt-in), Failed 0** |
| shared `LibraryRedirectTests` (filter) | devlinux | **43 / 43 passed, 0 skipped** (the 3 symlink tests really run on Linux) |
| CI run 43917991932 at c595c41 | GitHub | linux: 335/334/1 skip and opt-in 335/335; windows: 2433 (0 failed, 18 skipped) and opt-in 2437 (0 failed, 3 skipped); **macos: CANCELLED after 15 min in the queue, never got a runner** |

The issue's AC7 ("CI green across more than one run", all three platforms) is NOT yet met: one run,
and macOS has not run.

## 8. Incident (this seat) - reported to the Lead at once

At 12:29:58 I ran the PR build's `agenteyes --help` unsandboxed and unredirected, to read the
command list. It appended **3 lines** (pid 1725969: CliDispatcher Run / Dispatch / exit=0) to the
owner's live log (`qa-linux/09-qa-incident-owner-log.txt`). Nothing else: help makes no service
call and does not read or write any recording. The lines are left in place as the record. Every
run after it was sandboxed.

A second, smaller one: my first Linux `HOME` measurement (`11-*.txt`, run 1) used an EMPTY scratch
`HOME`. With no folders under it, the roots went relative and the process wrote a 3-line log into
`<repo>/AgentEyes/logs`, in the checkout. I saved that log as evidence and removed the folder. It
held only that file.

Also seen, not mine: pid 1668908 ran an unredirected `agenteyes package` into the owner's log at
12:08:09. Reported to the Lead.

## What I did NOT cover

- **Windows, entirely.** That includes App.xaml.cs (never compiled by me), the tray dialog and
  `Shutdown(5)`, the `%TEMP%` crash log, and Process Monitor evidence. Queued for the Windows seat.
- **macOS:** the developer's; not re-measured.
- TestIsolationTests run as a test: not on Linux (no WindowsDesktop SDK). Covered by the scanner
  over the Linux dll plus the Windows CI result above, not by a Windows dll on this machine.
- Landlock does not police `stat` / `readlink` / `access`. Those are counted from strace instead.
  Neither instrument covers paths other than the two real roots.
- A positive control for `AgentEyesLinux` running unredirected (it would surface the owner's tray
  window).
- `record start` against the owner's service (deliberately replaced by `record stop` on an idle
  service).
- The `AGENTEYES_FFMPEG_TESTS=1` opt-in Linux run (it kills ffmpeg it starts; a foreign go2rtc
  ffmpeg runs here). CI ran it: 335/335.
- The `package` command and signed-in hosted work under a redirect (a redirected process is
  signed out; I did not sign a scratch library in).
- Whether `XDG_CONFIG_HOME` (user-dirs.dirs) alone moves `MachineVideos`: not separately
  measured. D1 is already shown through `HOME`.

## Housekeeping

Every process I started is stopped: serve 17161 (pid 1733556, SIGTERM, clean), 17163, 17164,
and their ffmpeg children. The owner's service (pid 6560), the owner's Linux tray (pid 1298614)
and the go2rtc ffmpeg (pid 72110) were never touched. Nothing under the owner's real library was
written. The owner's audio settings and the installed recorder were not changed.
