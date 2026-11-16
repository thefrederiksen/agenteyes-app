# Issue #156 - QA report, Linux, pass 2 (delta re-QA, real screen captures)

**Verdict: VERIFIED -> `flow:ready-gate`.** All six delta items pass on this head, each with a
positive control in the same session. D1, D2 and G1 behave as claimed on real `x11grab` + `pulse`
captures in plain MP4, the container that failed last round. The pre-fix build loses the same crashed
recording on the same harness. No defect was found in this change. One product hazard outside #156
is reported under "Other findings" (F1) for the lead to file.

This is a DELTA. Pass 1's verification (`qa-report-linux.md`, head `d245037`) of the live-claim arms
is carried forward because item 1 below shows the lock, the owner file, the four decline sites and the
attempt-counting order did not change. Those arms were not re-run as a matrix.

## Machine, head and build

| | |
|---|---|
| Machine | `devlinux`, Ubuntu, kernel 6.8.0-149-generic, X11 `DISPLAY=:0`, PipeWire-pulse; ffmpeg 6.1.1 |
| Runtime | .NET SDK 8.0.134 / runtime 8.0.34, native net8.0 |
| Head tested | `2594cfc733eb58103a36055ec7b7d1295fe4d8c5` (`issue-156-capture-ownership`, PR #162; code `a2f8103` + `31ed721`) |
| Embedded commit of the binary run | `1.11.11+2594cfc733eb58103a36055ec7b7d1295fe4d8c5`, read out of `src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes.dll` and printed again at the top of every transcript |
| Pre-fix control binary | `d245037` built in a scratch worktree: `1.11.11+d24503771c63b5da92dcb4e9dad42dca397121cf` |
| Build | `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64` -> **0 Error(s)**, 1 existing warning |
| Suite (real Linux, this seat) | `dotnet test tests/AgentEyes.Tests.Linux -c Release` -> **334 passed / 0 failed / 0 skipped**. Filtered to `CaptureWriterReap` + `CaptureOwnership`: **48 / 0** - the new tests ran inside that count |
| CI at `2594cfc` itself, two runs | 43877789630 (push), 43877806543 (pull_request), counts read from the logs: each **windows 2429 passed / 4 skipped / 0 failed, macos 326 / 0, linux 334 / 0** |
| Windows `AgentEyes.sln` | not buildable on this machine; CI is the only Windows evidence |

## Safety

- **Isolation (#161), proven before any service ran in every run.** A report-only `agenteyes housekeep`
  (it changes nothing without `--apply`) printed the recordings root for this host and for the other
  host. Both had to be inside the run's scratch root. **Positive control:** the same probe under a second
  scratch env must report that env's root instead, so the probe can tell the difference. The first run
  aborted on that control, before any service existed (`run-crash-fix-aborted-isolation.txt`). The
  cause is F1 below, not a leak. Every `serve` was then SIGKILLed at once unless its own `recordings ->`
  line was inside scratch. That never fired.
- **After the fact:** 99 distinct pids logged to my scratch logs. **0** of them appear in the owner's real
  log (control: the owner's service pid 6560 appears 26018 times there). The owner's real library
  (82 entries, `2026-11-14_102532_video` included) has **0** entries modified after 08:42 local. The
  control is that the same `find` on a scratch library returns 10.
- **Foreign ffmpeg:** go2rtc camera wall, pid **72110**, re-identified by its command line
  (`user_agent go2rtc/ffmpeg`) at the start and end of every run, alive, never signalled. Only pids
  these scripts started were ever signalled.
- **Not touched:** the owner's service (6560), the real library, and the audio settings. The mic was
  named per request, as pass 1 did, and nothing was configured. The tray app changed pid during my
  session (6006 -> 1298614), and **I did not cause it**. The owner's real log, read only, shows 6006 logging
  `[App] Quit (the recorder service keeps running)` at 08:46:01, 34 s before my first run (08:46:35).
  1298614 was started at 08:52:57 under `systemd --user`, consistent with the #116 desktop-app seat on
  this machine.

## (1) Nothing else moved - PASS

`git diff d245037 2594cfc -- src tests`: 15 files, +773 / -32. **Every removed product line** is one of:

- `CaptureLock.cs`: the one `NoteWriter` line that now calls `RegisterCaptureWriter` instead of `Register` (D1).
- `RepairService.cs`: the synchronous reap and its try/catch in `Start`, moved to `Task.Run` (D1, below).
- `FfmpegChildRegistry.cs`: the old kill-only `ReapOrphans` body and signatures (D1).

Everything else is an addition: `OwnerForManifest` and `ClearStaleClaim` (`CaptureLock.cs`),
`CaptureOwner` (`Manifest.cs`, `RecoveryManifest.cs`, `RecordingService.cs:1340`), and one
`ClearStaleClaim` call AFTER each decline site (`Package.cs:125`, `PostRecording.cs:381`,
`RecordingService.cs:1093`). `TryAcquire`, `Inspect`, `ThrowIfLive`, `FreeLockLiveness`, `Dispose`,
`ReadOwner`, `OpenShared`, `MuxCoordinator` and `PostRecordingState` have **no hunk at all**. On the test side, the only edits to existing tests are the
reaper's new signature (`FfmpegChildRegistryTests.cs`, 4 call sites, now with a graceful request that
throws if a post-processing child is ever asked) and the IL pins (`ManifestWriterIlTests.cs`: Delete
x2 -> x7, `Register` -> `WriteRecord`, `+ClearStaleClaim -> File::Delete x1`, `+libc!kill`). Both
Windows fixes the handoff names (`RepairServiceTests`, `ManifestWriterTests`) were fixed in product
code (await placement, a log string), not by editing a test.

The one behavioural change outside the three items is that **the start-up reap now runs in the
background**. Before, nothing could run until it had finished. I traced every entry point that could reach
a recording before it ends. `RunAsync` (timer, main window, recording completed) and `HousekeepAsync`
(the API's run-now) both await `_reap`. `ResumeUnfinishedAsync`, the thumbnail, title and footprint
passes are reached only through `RunAsync`. `PostRecordingQueue` only finalizes this process's own
just-stopped recordings. No path skips the wait. Measured: in every crash run the first pass ran
after the reap had logged its outcome.

**Conclusion: pass 1's verification of the live-claim arms still covers this head.**

## (2) D1 with my own real capture, and its positive control - PASS

Harness `qa-linux-pass2/harness/qa2.sh <fix|pre> <port> crash`, one script for both binaries. S1
`serve` -> `POST /record/start` (x11grab + pulse, plain MP4) -> 40 s -> **SIGKILL S1** -> the orphan keeps
writing -> the other host's `package` -> **S2** starts (the next service start) -> its first repair pass.

| Step | FIX `2594cfc` (`run-crash-fix.txt`) | PRE-FIX `d245037` (`run-crash-pre.txt`) |
|---|---|---|
| orphan after S1's SIGKILL | ppid 2221, still writing: 6.29 -> 7.34 MB in 4 s | same: 6.29 -> 7.08 MB in 4 s |
| S2's start-up reap | `SIGTERM -> pid 1280852` ... `SIGTERM, exited after 0.10s (graceful: never killed)` | `ORPHAN (owner pid 1289152 gone) - killed` |
| `raw.mp4` after the reap | probes: video + audio, 45.67 s | `moov atom not found` |
| S2's first pass (manifest `PostProcessing.mux`, not `/status`) | `PendingMux` cleared, **State=done, Attempts=1**, by S2 | **State=failed, Attempts=1**, `could not read the audio channel count` |
| the file | `recording.mp4` **45.67 s, full decode exit 0, 0 stderr lines, 30 video frames decoded from the last second, last audio packet read; streams h264 + aac** | none - the recording is lost |
| `capture.owner.json` | **absent** (`ClearStaleClaim ... (post-recording) - CLEARED ... (lock held shared)`) | present |

The FIX arm also repeated under load and with two orphans (item 3), recovered each time. The
near-end frame check has its own control: a truncated copy of the same file gives `moov atom not
found`, with no frame decoded.

## (3) The bound, and a survivor - PASS

**A survivor is still killed** (`run-crashstop-fix.txt`). As in (2), but the real orphaned ffmpeg is
SIGSTOPped before S2 starts, so it cannot act on SIGTERM. S2 logs `RequestGracefulExit: SIGTERM ->
pid 1294229` at 08:51:38.234, then `KillTree: SIGKILL -> pid 1294229` at 08:51:48.261 (**10.03 s**), then
`SIGTERM, did NOT exit within 10s - SIGKILLED; its recording may not be recoverable`. S2's `/health`
answered while the reap waited. The reap is off the start path, so a writer that will not exit does
not strand the service. The file is lost, which is correct for a writer that cannot finish.

**One shared bound, mixed paths, one reap** (`run-two-fix.txt`). Two services capture at once on one data
root and both are SIGKILLed. One orphan is left writing, the other SIGSTOPped. S2 signals both within 1 ms.
The writing one exits after **0.13 s**, graceful, and is recovered: Attempts 1, 34.73 s, decodes, audio.
The stopped one is SIGKILLed **10.02 s** after its SIGTERM. Both are gone 10.36 s after S2 starts,
which is one bound, not two.

**Headroom - same order of magnitude as the developer's numbers.** My in-product SIGTERM -> exit times:

| Arm | Exit after SIGTERM | Developer's figure |
|---|---|---|
| idle, 45 s capture | 0.10 s | 0.08-0.15 s idle, 0.13 s in-product |
| two orphans in one reap | 0.13 s | - |
| every core busy (32 busy loops across S2's start, `run-crashload-fix.txt`) | **0.26 s** | 0.25-0.27 s |

Against the 10 s bound that is about 38x headroom under load. I did not repeat the 30-minute
standalone series. My captures were 35-49 s long, so "exit time does not grow with length" is the
developer's measurement, not mine.

**The order pin can fail - mutation check, run by me** in a scratch worktree, reverted after. With a capture
writer killed BEFORE its SIGTERM, **6 of 9** `CaptureWriterReapTests` fail. They include
`ReapOrphans_CaptureWriterIgnoresSigterm_SigtermThenWaitThenSigkill` and both real-process tests.

## (4) D2 - PASS, including the concurrency it worried about

**The clear happens, and another host then accepts the finished recording** (`run-crash-fix.txt`; other
host = `LD_PRELOAD` hostname shim + its own data root and host id, the same library; `harness/fakehost.c`;
the shim's own control: `hostname` prints `otherhost` with it, `devlinux` without):

| | FIX | PRE-FIX (control) |
|---|---|---|
| other host's `package` BEFORE recovery | exit 1 `... video ffmpeg pid 1280852 ran on host devlinux and cannot be checked from otherhost`, `mux: none` | same |
| other host's `package` AFTER recovery | **past the claim**, stops only at `Not signed in to DevThrottle` | still refused, same text |
| the other host's own service pass (S3, real, `trigger=timer`) | `Execute: ... capture claim: no capture holds it`, then its stages run | `FindUnfinished: skipping ... cannot be checked from otherhost`, and `Thumbnails NeedsThumb: skipping` |

**Mutation check, run by me:** with the `ClearStaleClaim` call in `PostRecording.Execute` removed,
`Resume_CrashedCaptureRecovered_AnotherHostThenAcceptsTheRecording` fails, 1 of 48.

**Concurrency** (`run-conc-d2.txt`, `harness/conc.sh`). There were 16 working copies of the two REAL crashed
recordings above, copied after the reap SIGTERMed the orphan and before any pass touched them: complete
`raw.mp4`, `PendingMux` pending, owner record naming a dead owner and writer, `capture.lock` present.

- **Arm 1, the exact kernel lock `ClearStaleClaim` takes, held across a WHOLE counted finalize.** An external
  `flock -s capture.lock` (`/proc/locks`: `FLOCK ADVISORY READ`) was held for 25 s while
  `package` ran. Result: no decline, **done, Attempts 1, one `AttemptedBy`**, owner record cleared, decode
  exit 0. Because the hold spans the attempt from count to finish, it covers the backstop
  `ThrowIfLive` in `FinalizePending` that runs after the count.
  **Positive control: the exclusive hold** (a live capture, or the rejected first design),
  `flock -o -x`, `/proc/locks`: `WRITE`. `package` is refused (`it is being captured by ...`), with `mux: none`,
  nothing counted. After release it gives done, Attempts 1, decode exit 0. My first try at this control kept
  the lock past "release" because `flock -x ... sleep` hands the fd to `sleep`. I reran it with `-o`.
  Both runs are in the transcript.
- **Arm 2, which lock it really takes** (`strace-package-f3.txt`). Every flock on `capture.lock` in a real
  `package` is `LOCK_SH|LOCK_NB`, **0 `LOCK_EX`**. The owner record is unlinked while fd 57 holds `LOCK_SH`,
  then `LOCK_UN`. strace's control: `flock -x` on the same file shows `LOCK_EX`.
- **Arm 3, stress.** 13 recordings were each hit at the same instant by 3 same-host packages and 1
  other-host package, 52 processes. **13 of 13**: done, **Attempts=1, exactly one `AttemptedBy`**, owner record
  gone, full decode exit 0 with audio. Losers were refused by `mux.lock` before counting. **0 `NoteFailed`**
  lines in the run's logs; the control is the pre-fix crash run's log, which has 1. Timeline for f4, from the logs:
  the other host declined (.394) -> pid 1307076 CLEARED under the shared hold (.405) -> pid 1307079 found no
  record, took `mux.lock` and counted (.507), its backstop passed (.577) -> the other two refused by `mux.lock`.
- **Two hosts cannot both clear and finalize.** On the other host, 3 `ClearStaleClaim` calls ran and **0 cleared**.
  A record naming another host's writer reads live there, so it can only decline. Of its 13 packages,
  10 declined uncounted. 3 arrived after this host's clear, got past the claim and were refused by
  `mux.lock`, also uncounted. A cleared record is never re-created on a finished recording, so the
  sequence is monotonic: an other-host finalizer either declines before counting or meets `mux.lock`.

**What arm 3 did not show:** the exact interleaving the self-review feared, where process X clears after
process Y has already counted, occurred in 0 of 13. Every finalizing path clears BEFORE it counts, so
the first arrival clears well before anyone holds `mux.lock`. That interleaving is covered by arm 1,
at the kernel level, rather than observed.

## (5) G1 and its durability - PASS

| Point in the lifecycle | `manifest.json` `CaptureOwner` | Run |
|---|---|---|
| START manifest, 3 s into a capture | `{"Host":"devlinux","Pid":1302391,"ProcessStartUtc":"2026-11-16T13:54:47.9020995Z"}`; S1's `/proc` start 08:54:47 EST = 13:54:47Z | `run-clean-fix.txt` |
| after a normal stop and mux (done, Attempts 1, 21.47 s, decodes) | unchanged | `run-clean-fix.txt` |
| after a recovered crash, with the owner record deleted | names S1 = pid 1280743, the SIGKILLed one; recovery `AttemptedBy` pid 1283812 | `run-crash-fix.txt` |
| a survivor whose recording was lost, and both two-orphan recordings | present, naming each one's SIGKILLed owner | `run-crashstop-fix.txt`, `run-two-fix.txt` |
| PRE-FIX control | `<<field absent>>` | `run-crash-pre.txt` |

`ProcessStartUtc` equals the owner record's `ProcessStartUtc` byte for byte. The refusal text prints the
claim's `StartedUtc`, a different field, so the two timestamps in the logs legitimately differ. No code
path rebuilds a manifest field by field except `RecoveryManifest`, which now carries it.

## (6) D3 is not fixed, and no #113 code is here - PASS

The handoff's references all check out against PR #137's head `ad21557`. `Video/CaptureWatchdog.cs:14`
declares the second `CaptureLiveness`. `RecordingService.cs:162` (`IScreenRecorder? _video`), `:225`
(`CaptureLiveness? _liveness`), `:430` (`live?.State`) and `:433` (`live?.LastGrowthUtc`) are as quoted.
`Video/IScreenRecorder.cs:15` declares the interface, which has **0** `ProcessId`. On this branch:
`CaptureLock.cs:10` and `RecordingService.cs:693` (`NoteCaptureWriter("video", _video.ProcessId)`),
plus `FfmpegRecorder.cs:78`. Both conflicts are named precisely.

No #113 code is on this branch, each check with a control:

- **Markers in `src`:** `CaptureWatchdog` 0 / `IScreenRecorder` 0 / `frag_duration` 0 / `FragmentedOutput` 0
  on HEAD, against 21 / 6 / 2 / 6 on `ad21557`.
- **The binary:** `CaptureWatchdog` has 0 UTF-16 strings in the binary I ran.
- **History:** `ad21557` is not an ancestor. **0 of #113's 13 commits** have an equivalent patch here
  (`git cherry`). The two non-#156 commits on the branch (`497e9d4`, `f9ef230`) are both on `origin/main`;
  they are the base.

## Other findings (not defects of this change)

- **F1 - with no `~/Videos` directory, the recordings root is a RELATIVE path.**
  `src/AgentEyes.Core/AppDataPaths.cs:55` returns `Environment.GetFolderPath(MyVideos)` unchecked. On Linux,
  .NET returns `""` when the XDG videos directory does not exist, so the root becomes `AgentEyes`, which
  resolves against whatever the process's cwd is. Observed: `housekeep` printed `root: AgentEyes` and
  `the recordings root does not exist: AgentEyes`. A Linux user without `~/Videos` (a server or a minimal
  install) gets recordings scattered by cwd, and two hosts with different cwds stop sharing a library at all.
  This is not #156's. It sits next to #161 and needs its own issue; this repo's "fail explicitly" rule says
  that empty result should be an error, not a path.
- The survivor's recording (item 3) is genuinely destroyed (`moov atom not found`), and the reap said so
  (`its recording may not be recoverable`). With its stale claim cleared, the rest of that run spent its
  whole budget on it: attempt 1 by S2 (pid 1295893, devlinux), then attempts 2 and 3 by the other host's
  `package` (pid 1296551) and its service pass (pid 1296620), both `on otherhost`. The result is
  `failed 3`, and it is not retried again. That is the bound working on a defective file, and
  `AttemptedBy` names every consumer, which is criterion 5's purpose. It is not a strand and not a stolen
  budget, because there was nothing left to save.

## What this report does NOT cover

- **Windows:** no build or run by this seat; CI only. Windows still kills an orphaned capture writer at once,
  by the lead's ruling, with an honest log line (issue #166). Not exercised.
- **macOS:** the finalize time of an avfoundation capture after SIGTERM. CI runs the real-process SIGTERM
  tests there; nobody has measured the time.
- **Two real machines on one library**, and lock semantics over a network file system. The other host is the shim.
- **systemd `KillMode=mixed`**, where systemd SIGKILLs the orphan before any reaper runs (issue #165). My
  services were hand-run `serve` processes, the host shape D1 protects.
- **The tray app as the capturing host**, camera writers, system or mixed audio sources, the CLI `video` path.
- **The 30-minute capture length** and the full standalone SIGTERM series. My captures were 21-49 s.
- **The exact X-clears-after-Y-counted interleaving**, observed 0 of 13 times. It is covered by arm 1 and
  the strace, not by observation.
- **Pass 1's live-claim matrix** (four binaries, MERGED, MAINFRAG) was not re-run. Item 1 is why it still applies.

## Artefacts

`docs/cencon/proof/issue-156/qa-linux-pass2/`: `run-crash-fix.txt`, `run-crash-pre.txt`,
`run-crashstop-fix.txt`, `run-crashload-fix.txt`, `run-two-fix.txt`, `run-clean-fix.txt`,
`run-conc-d2.txt`, `strace-package-f3.txt`, `run-crash-fix-aborted-isolation.txt`. The harness is in
`harness/` (`qa2.sh`, `two.sh`, `conc.sh`, `fakehost.c`). The recordings are kept in scratch,
outside the repository.
