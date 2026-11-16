# Issue #156 - QA report, Linux, pass 4 (the Unknown path, real captures)

**Verdict: DEFECT -> `flow:qa-failed`.** One defect, on the platform this seat stands on, at the row
the lead put in this seat's scope (comment 6922629027: "If it does, that is a defect of this PR rather
than a follow-up"):

> **D-L1. On Linux, `kill(pid, 0)` = EPERM still answers NotMine ("belongs to ANOTHER USER") for a LIVE
> process of THIS user.** `src/AgentEyes.Core/Video/FfmpegChildRegistry.cs:463` (head `9abbb01`;
> `if (errno == Eperm && !isMac) return ProcessAnswer.NotMine(...)`), pinned as correct by
> `tests/Shared/ProcessIdentityTests.cs:313` (`ClassifyUnreadable_KillEpermOnLinux_NotMineAnotherUser`).
> Measured: a prober refused the writer's start time AND refused `kill(2)` with EPERM - the errno a
> signal-scoping sandbox returns - admits a live capture, deletes `capture.owner.json` and spends
> Attempts=1 while the writer keeps writing. Same hole round 2 closed on macOS. Details in "The defect".

Everything else this pass was asked to establish holds, on real captures, with controls in the same run:
the tri-state is total and every caller treats Unknown as do-not-touch (real probe, no seam); a crashed
recording is still recovered; a live capture is still declined; nothing outside the tri-state moved.
The pid-namespace question (#172) is measured and is NOT part of this verdict.

## Heads, binaries, and the head moving under this seat

| | |
|---|---|
| Machine | `devlinux`, Ubuntu 24.04, kernel 6.8.0-149-generic (Landlock ABI 4), X11 `:0`, PipeWire-pulse, ffmpeg 6.1.1 |
| Runtime | .NET SDK 8.0.134 / runtime 8.0.34, native net8.0 |
| Head in my brief | `a70fd907ba0261f165c95225ae42b975644bd442` - binary embedded `1.11.11+a70fd907ba0261f165c95225ae42b975644bd442` |
| **Head at verdict** | **`9abbb01ae8f5d57c340a1f335f5374efe7adbce5`** - binary embedded `1.11.11+9abbb01ae8f5d57c340a1f335f5374efe7adbce5` |
| Controls (scratch worktrees) | pre-fix `d245037` (pass 1 head) `1.11.11+d24503771c63...`; pass 3 `1927c62` `1.11.11+1927c621f06e...`; `main` `17c244d` `1.11.11+17c244df603f...` |
| Build | `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64` -> 0 Error(s), every build |

The developer pushed `57259ea` (code) and `9abbb01` (docs) at 16:30 and 16:36 UTC, after this seat
started on `a70fd90`. `57259ea` changes the predicate's EPERM row on **macOS only**
(`errno == Eperm && !isMac` keeps the old answer on Linux) and adds uid fields to `MacProcessState`.
I ran jobs 2-6 on `a70fd90` first, then repeated every Linux-relevant arm on `9abbb01`: the Unknown
caller matrix, the crash positive control, the live arm, the pid-namespace probe, both suite arms and
CI. Results are identical across the two heads, as the code says they must be on Linux. Each
transcript names the commit embedded in the binary it ran.

## Safety (#161, the foreign ffmpeg, the evidence)

- **Isolation, proven before every service or CLI run.** A report-only `agenteyes housekeep` printed the
  recordings root under the run's env, and the run aborted unless it was inside that run's scratch.
  **Positive control** in every real-capture run: the same probe under a second scratch env reports
  that env's root, so the check can fail. Every `serve` (plain and Landlock-confined) was SIGKILLed at
  once unless its own `recordings ->` line was inside scratch. That never fired. The container arms
  ran the same check inside the container.
- **After the fact:** 92 distinct pids in my scratch logs, **0** of them in the owner's real log
  (control: the owner's service pid 6560 has 56,798 lines there). The real library has **0** entries
  modified since my start (16:28:34Z), still 82 entries, `2026-11-14_102532_video` present. Control:
  the same `find` over my scratch runs returns 807.
- **Foreign ffmpeg:** the go2rtc camera wall, pid 72110, was re-identified by its command line
  (`user_agent go2rtc/ffmpeg`) at the start and end of every run and never signalled. Only pids my
  scripts started were signalled. One cleanup `pgrep` of mine matched its own shell's command line
  and killed that shell. Nothing else matched it, and 72110 was alive after.
- **Not touched:** the owner's service (6560), the tray (1298614), the installed recorder, audio
  settings (the mic is named per request, as passes 1 and 2 did). Docker: the public `ubuntu:24.04`
  image was pulled. Every container was `--rm` and named `qa156-*`, and none remain.

## How Unknown was produced on Linux, honestly

The gate's macOS case is a prober refused the live writer's start time while `kill(pid, 0)` succeeds.
The Linux analogue is **Landlock**, enabled on this kernel (`lsm=...,landlock,...`, ABI 4).
`harness/llprobe.c` builds a ruleset that handles only `READ_FILE|READ_DIR`. It allows every top-level
path except `/proc`, and inside `/proc` every non-pid entry plus the prober's own pid. So reading
**another process's `/proc/<pid>`** is refused, and nothing else is: no writes, no signals (ABI 4 has
no signal scoping). Syscall-level control (same session): a plain prober reads `/proc/<sleep>/stat`, rc 0;
the confined prober gets `Permission denied`, while `kill -0` still returns 0. .NET reads `StartTime`
from `/proc/<pid>/stat`, so it throws `Win32Exception` (13), `kill(pid, 0)` returns 0, and the head's
`ClassifyUnreadable` answers **Unknown** on its `!isMac` row. No test seam is replaced anywhere below.

Two delivery routes:

- **In-process, every caller** (`harness/Program.cs` + `callers.sh`). A scratch console whose assembly
  is named `AgentEyes.Tests.Linux`, one of the core's `InternalsVisibleTo` names. That is the only
  reason for the name: it calls the internal callers directly. It references the BUILT product dll,
  checked by embedded commit. A child process takes a real claim (`TryAcquire` + `NoteWriter` on a live
  ffmpeg I started) and SIGKILLs itself: a crashed owner. The prober then runs every caller on a worker
  thread that confines ITSELF with Landlock (`libllprobe.so`, per-thread on 6.8) in `unknown` mode only.
  The release claim is recorded before that, unconfined, as a real capture does.
- **End to end, real captures** (`qa4.sh`): the real CLI and the real service, exec'd under `llprobe`.

## (1) Nothing else moved - PASS

`git diff 1927c62 a70fd90 -- src tests`: 13 files. Product hunks:

- `Video/ProcessIdentity.cs` (new): `ProcessIdentity {Unknown=0, Mine, NotMine}`, `ProcessAnswer`, `MacProcessState`.
- `Video/FfmpegChildRegistry.cs`: `IsSameProcess` -> `IdentifyProcess`; `DescribeUnreadable` -> `ClassifyUnreadable` (+ macOS `sysctl` read); `ReapOrphans` takes the tri-state, with three new Unknown branches (owner, child, at the deadline) that neither signal nor kill and keep the record.
- `CaptureLock.cs`: `CaptureLiveness.Undetermined`, the seam's type, the `ThrowIfLive` log word, `Release` keeps Unknown writers, `FreeLockLiveness` returns UNDETERMINED. **`ClearStaleClaim`'s body has no hunk**: Unknown reaches it as `Live=true` and is KEPT on the existing branch.
- `PostRecording.cs` / `Thumbnails.cs`: log level and wording only, same returns. `PostRecordingPlan.cs`: an `Undetermined` branch that `continue`s, as the `Live` one already did, plus a counter in the summary line.

**Zero diff lines** in `Package.cs`, `RecordingService.cs`, `MuxCoordinator.cs`, `PostRecordingState.cs`,
`Manifest.cs`, `RecoveryManifest.cs`, `RepairService.cs`. No hunk touches `TryAcquire`, `NoteWriter`,
`OpenShared`, `WriteOwner`, `CaptureWriterGrace`, `RequestGracefulExit` or `KillTree`. The reaper's
deadline loop changed from `if (isSameProcess) continue` to `if (!IsNotMine) continue`: Mine waits as
before, and Unknown also waits and is then not killed. Tests: four reaper call sites wrapped in
`TestIdentity.From`, one IL pin (`libc!sysctl`), the test helper `KillOwned` learning Unknown, and new
tests in `ProcessIdentityTests`, `CaptureOwnershipTests`, `CaptureWriterReapTests` and `OwnedFfmpegTests`.
`a70fd90..9abbb01` adds the macOS EPERM/uid change (above), and nothing else in `src`.

## (2) The Unknown path is do-not-touch - PASS for Unknown; see D-L1 for the EPERM route to a wrong NotMine

**Matrix, real probe, every caller.** The same fixture each time: a crashed owner and a live ffmpeg
writer, confirmed growing before the probes, plus a healthy unclaimed sibling. Results are identical
on `a70fd90` and `9abbb01` (`callers-<head>-<mode>.txt`).

| Caller | Unknown (Landlock-confined thread) | Mine (control: plain, writer live) | NotMine (control: writer ended) | pass 3 `1927c62`, same Landlock thread (BEFORE) |
|---|---|---|---|---|
| `IdentifyProcess` | **Unknown** - "exists and is this user's, but its start time is refused (13...)" | Mine | NotMine "no process exists" | `IsSameProcess` -> **False** |
| `Inspect` | **Live=True, Undetermined=True**, reason `UNDETERMINED: ... CANNOT BE ESTABLISHED from this process`, names the pid and the fix | Live=True, STILL RUNNING | Live=False, stale | **Live=False, "writer(s) have exited"** |
| `ThrowIfLive` | **refused**; log `ThrowIfLive DECLINED (undetermined)` | refused | admitted | **admitted** |
| `ClearStaleClaim` | **False, `capture.owner.json` PRESENT**; log `KEPT: UNDETERMINED` | False, present | True, ABSENT | **True, ABSENT while the writer wrote** |
| sweep `FindUnfinished` | **not admitted**; `WARN NOT REPAIRED`; summary `UNDETERMINED (...)=1`; healthy sibling still admitted | not admitted | admitted | **admitted** |
| sweep `FindMissing` (thumbnail fixture: no PendingMux, finished file, same claim) | **not listed**; `WARN NOT GENERATED` | not listed | **listed** | **listed** |
| reaper `ReapOrphansIn` | **"UNKNOWN whether pid N is still this ffmpeg - not signalled, left alone, record kept"**; writer alive | **SIGTERM, exited after 0.15 s (graceful)** | "already exited - record removed" | **"already exited - record removed" for a LIVE writer** |
| `Release` (own claim, own live writer) | **record KEPT**; `WARN ... could not be proved exited` | record KEPT ("STILL RUNNING") | record removed | **record removed** |
| writer `raw.mp4` across the probes | 395,900 -> 616,468 bytes, ALIVE | (signalled by the reaper, as Mine should be) | ended by me | 395,900 -> 620,163 bytes, ALIVE |

Each column is the control for the others: the same instrument reports admission, clearing, listing
and removal when the state calls for it. In the pass-3 column it reports all four against a live writer.
The thumbnail fixture was added after a first run in which `FindMissing` returned false in every mode
(the recording had `PendingMux`, which excludes it anyway), because a check that cannot return true
proves nothing.

**End to end on real captures** (`crash-<head>.txt`). S1 `serve` captures x11grab + pulse, plain MP4,
for about 40 s and is SIGKILLed. The orphan keeps writing (+1.0-1.3 MB in 4 s). Then:

| Step | `a70fd90` and `9abbb01` (identical) | `1927c62` pass 3 (BEFORE) | `d245037` pass 1 (pre-fix) |
|---|---|---|---|
| U1: Landlock-confined `package --no-frames` | exit 1 `refusing to package ...: UNDETERMINED: ...`; `mux: none`; claim PRESENT; writer alive | exit 1 after **admitting**: `mux State=failed Attempts=1`, **claim ABSENT** | exit 3, framework message |
| U2: Landlock-confined `serve` (its reap + first pass) | reap: `UNKNOWN ... not signalled, left alone, record kept`; sweep: `NOT REPAIRED`, `NOT GENERATED`, `UNDETERMINED=1`; `/health` ok; writer still writing (+0.5 MB in 2 s) | reap: **"already exited - record removed"** (live writer); pass: admitted, **Attempts=2** | `reap FAILED`, `RunAsync FAILED` |
| S2: ordinary `serve` | **SIGTERM -> exited after 0.15 s / 0.13 s (graceful)**; recovered (job 3) | no record left, so the orphan is **never ended** (alive 20 s after S2 starts); **Attempts=3, failed** | SIGKILL; **lost** (`failed`, audio unreadable) |

**Finding for the record (not a defect of this head):** pass 3's defect was not macOS-only. On Linux,
under a Landlock prober, `1927c62` spent the WHOLE bounded budget (Attempts 3, failed) on a capture
that was live the whole time, and destroyed the reap record that would have ended its orphan. Pass 4
closes that on the same harness.

## (3) The positive control that failed pass 1 - PASS

Same runs as above, the S2 step. Followed on `manifest.json` `PostProcessing.mux`, never `/status` (#159):

| | `a70fd90` | `9abbb01` | `d245037` pre-fix, same harness |
|---|---|---|---|
| reap | `SIGTERM -> pid 1607625`, exited after 0.15 s, never killed | exited after 0.13 s | `ORPHAN - killed` |
| mux | **done, Attempts=1**, `AttemptedBy` = S2 pid on devlinux | **done, Attempts=1** | **failed, Attempts=1**, `could not read the audio channel count` |
| file | `recording.mp4` 80.30 s, **full decode exit 0**, 0 stderr lines, **30 video frames decoded from the last second**, last audio pts read, streams h264 + aac | 80.33 s, exit 0, 30 frames, h264 + aac | none |
| claim | `ClearStaleClaim (post-recording) - CLEARED ... (lock held shared)` | same | present |

The recovery happened in the same run in which, minutes earlier, U1 and U2 refused to touch the same
recording. The guard declines the undecidable case and admits the decided one.

## (4) The live arm - PASS

`live-<head>.txt`. S1 captures. 12 s in, a second process runs `agenteyes package <dir> --no-frames`,
plain and then Landlock-confined:

| | `a70fd90` / `9abbb01` | `main 17c244d` (control, same harness) |
|---|---|---|
| plain `package` | exit 1 `refusing to package ...: it is being captured by the capture session of pid <S1> on host devlinux (started ...). Nothing was muxed, renamed or counted as an attempt` | exit 1 after **admission**: `mux State=failed Attempts=1` |
| confined `package` | same refusal, naming the holder (the exclusive lock declines before any pid is probed) | admitted again: **Attempts=2** |
| directory | listing **identical** before and after; `raw.mp4` still there and growing; no `recording.mp4`; `mux: none` | `mux.lock` appeared |
| after `POST /record/stop` | **done, Attempts=1** (by S1), 22.0 s / 21.97 s, decode exit 0, 30 frames in the last second, h264 + aac | done only at **Attempts=3** - survived by luck, the issue's defect |

Instrument slip, stated: in the `a70fd90` run my "still growing" line sampled before the writer's next
256 KB flush and printed `NO`. The state line 40 ms later shows 3,407,920 -> 3,670,064 bytes, and the
finished 22 s file is the real proof of continuity. The `9abbb01` run reads `yes`.

## (5) The pid-namespace question (#172) - MEASURED, confirms #172, NOT part of this verdict

Unprivileged `unshare -U -p` and `bwrap` are refused here (`apparmor_restrict_unprivileged_userns=1`:
`write failed /proc/self/uid_map: Operation not permitted`). So the prober ran in a **Docker container**:
its own pid namespace, uid 1000, the **same user namespace** as the host, the scratch state root
bind-mounted (so the host-id file is shared), and the host's .NET 8.0.34 bind-mounted read-only.
`pidns-172-<head>.txt`, identical on both heads:

| Arm | pid ns | user ns | hostname | answer for the host's LIVE writer | `Inspect` |
|---|---|---|---|---|---|
| H: host, plain (control) | `pid:[4026531836]` | `user:[4026531837]` | devlinux | **Mine** | STILL RUNNING |
| C2: container `--pid=host` (control) | `pid:[4026531836]` | `user:[4026531837]` | `747d56780181` | **Mine** | STILL RUNNING |
| **C1: container, own pid ns** | **`pid:[4026533019]`** | `user:[4026531837]` | `5b6bb90f9e2f` | **NotMine - "no process 1624653 exists"** (`kill -0` inside: No such process) | **Live=False, stale** |
| **C3: real `package --no-frames` from C1's namespace** | | | | | **admitted: `ClearStaleClaim ... CLEARED`, `NoteStarted ... by agenteyes pid 1 on <container>`, `failed Attempts=1`, claim ABSENT, writer ALIVE, `raw.mp4` 419,613 -> 725,866 bytes** |

The host id alone made the container "this host": `IsThisHost` returns true on a HostId match, even
with a different hostname. That confirms the expected defect in #172 exactly as the lead narrowed it
(a prober that shares the state root but not the namespace). C3 failed at ffprobe only because the
image has none, after the claim was already deleted and the attempt counted. The ids and commands
will be posted on #172.

## (6) Suite and gates - PASS

| | default | `AGENTEYES_FFMPEG_TESTS=1` |
|---|---|---|
| Linux, this seat, `a70fd90` | 377 total, **375 passed, 2 skipped, 0 failed** | **376 passed, 1 skipped, 0 failed** |
| Linux, this seat, `9abbb01` | 384 total, **382 passed, 2 skipped, 0 failed** | **383 passed, 1 skipped, 0 failed** |

The skips are named in `suite-linux.txt`: `KillOwned_FailurePath_...` (opt-in, runs in the second arm)
and `PackageCli_SandboxedProberLiveWriter_...`, which is macOS-only (sandbox-exec) in both arms. So
**the Linux suite has no real-probe Unknown test at all**; the matrix above is the only real-probe
Unknown evidence on Linux. At `9abbb01` there are 40 `ProcessIdentityTests` (33 at `a70fd90`).

**CI at `9abbb01`**, both runs green, counts read from the logs: push 43909375423 and pull_request
43909388902, each **linux 382/2 and 383/1** (matching mine exactly), **macos 375/1**, **windows
2466/16 and 2485/1**, 0 failed.

**Mutations on Linux** (`mutations-linux-a70fd90.txt`): the developer's 18, re-targeted at the Linux
test project, in a scratch worktree left clean. **17 of 18 fail at least one test.** M10 (wrong SZOMB
constant) survives on Linux, as expected: the zombie proof is macOS-only and its real-probe test runs
there. **M5 ("EPERM -> Unknown") FAILS a Linux test**, which is D-L1 seen from the other side: the
suite pins the defective answer. I did not re-run mutations at `9abbb01`; its 22 are the developer's
macOS measurement.

## The defect (D-L1), reproducible

`eperm-DEFECT-9abbb01.txt`, harness `eperm.sh`, head `9abbb01`.

1. A recording with a pending mux, a crashed owner's claim (real `TryAcquire` + `NoteWriter`, then
   SIGKILL), and its writer a LIVE ffmpeg of uid 1000, writing.
2. Probe it from a process that is refused `/proc/<writer>/stat` (Landlock, as above) AND gets EPERM
   from `kill(2)` (`llprobe --kill-eperm`: a seccomp filter returning `ERRNO(EPERM)` for `kill`). The
   syscall control in the same session: Landlock alone gives `kill -0` rc 0; with the filter,
   `Operation not permitted`.

| Arm | `IdentifyProcess` | `Inspect` |
|---|---|---|
| P plain (control) | Mine | STILL RUNNING |
| L Landlock only (control) | **Unknown** | **UNDETERMINED** |
| **E Landlock + kill EPERM** | **NotMine - "pid 1631594 belongs to ANOTHER USER (kill(pid, 0) errno 1)"** (uid 1000, probed by uid 1000) | **Live=False, stale** |
| **E2 real `package --no-frames`, same confinement** | (product log) `answered NotMine: ... ANOTHER USER` | **`ClearStaleClaim ... CLEARED`, `NoteStarted ... pid 1631909`, `failed Attempts=1`, `capture.owner.json` ABSENT; writer ALIVE, `raw.mp4` 259,590 -> 505,857 bytes** |

**Expected** (the lead's pass-4 rule "only facts map to NotMine", and round 2's own macOS reasoning):
EPERM is not proof of another user when the target is this user's. The answer must be Unknown, so the
claim is kept and nothing is counted. **Actual:** NotMine, so the live capture's claim is deleted and an
attempt spent - criteria 2 and 3 fail in this configuration, as the gate's pass-3 rejection did on
macOS. The code knows: `FfmpegChildRegistry.cs:430-432` names this exact premise as "NOT measured
there". It is now measured.

**How real the route is, stated plainly.** EPERM from `kill(2)` for a same-user target is what
Landlock **ABI 6** signal scoping (`LANDLOCK_SCOPE_SIGNAL`, Linux 6.12+) returns, and a Landlock policy
that scopes signals and does not allow `/proc` reads is one ruleset. This kernel is ABI 4, so I
produced the errno with seccomp instead (my filter answers EPERM for every `kill`, even a missing pid,
where a scoping LSM lets ESRCH through - irrelevant for a live target). I did not measure any named
sandbox product. AppArmor and SELinux signal denials return EACCES, not EPERM, which the head maps to
Unknown (read, not measured). This is the same standard as the gate's two-rule `sandbox-exec` profile,
which was grounds in pass 3.

**A measured fact for the fix, not a choice of fix:** under the same confinement, `stat("/proc/<pid>")`
still succeeds and gives the owner uid (1000 for the writer, 0 for pid 1), while reading
`/proc/<pid>/status` is refused. That is the Linux counterpart of the `p_ruid` read round 2 uses. Caveat
for whoever uses it: a non-dumpable process's `/proc/<pid>` is owned by root.

## What this report does NOT cover

- **macOS and Windows at `9abbb01`**: CI only. Round 2's macOS EPERM/uid fix, its `kinfo_proc` offsets,
  and Intel macOS (the code says unmeasured) were not touched by this seat.
- **Mutations at `9abbb01`** (22): not run on Linux; mine are the 18 at `a70fd90`.
- **A real signal-scoping LSM** (Landlock ABI 6): this kernel cannot. D-L1 used seccomp for the errno.
- **Any named sandbox or container product** beyond plain Docker; no bubblewrap or flatpak (blocked
  here by the AppArmor userns restriction), no rootless podman.
- **A Landlock-confined CAPTURING host.** `NoteWriter` (unchanged since pass 2) reads the writer's start
  time and catches only `ArgumentException` / `InvalidOperationException`, so a confined capturer
  would presumably throw `Win32Exception` at capture start. That is read, not run, and outside #156's
  callers.
- **Release under Unknown in a real service**: proved in-process with the real probe (`callers`), not
  in a running `serve`, because a real capture's writer has always exited or been reaped by the time
  its own release runs.
- **The reaper's "Unknown at the deadline" branch** on a real process (signalled while Mine, Unknown
  after): suite and mutation M16 only.
- **Two machines on one library**, NFS/SMB, `KillMode=mixed` (#165), camera/system-audio writers, the
  tray app as the capturing host, captures longer than 80 s.
- **Pass 1/2 matrices** (four binaries, the 52-process concurrency soak, strace lock modes, G1): not
  re-run. Job 1 shows their code is unchanged.

## Artefacts

`docs/cencon/proof/issue-156/qa-linux-pass4/`: `callers-{a70fd90,9abbb01}-{unknown,mine,notmine}.txt`,
`callers-1927c62-unknown-BEFORE.txt`, `crash-{a70fd90,9abbb01}.txt`, `crash-1927c62-PASS3.txt`,
`crash-d245037-PREFIX.txt`, `live-{a70fd90,9abbb01}.txt`, `live-17c244d-MAIN.txt`,
`pidns-172-{a70fd90,9abbb01}.txt`, `eperm-DEFECT-9abbb01.txt`, `suite-linux.txt`,
`mutations-linux-a70fd90.txt`. Harness in `harness/`: `llprobe.c` (Landlock, and seccomp for
`--kill-eperm`), `Program.cs` + `Harness.csproj` (caller harness), `callers.sh`, `qa4.sh` (from pass 2's
`qa2.sh`), `pidns.sh`, `eperm.sh`, `mutate-linux.py` (the developer's `mutate.py`, re-targeted at the
Linux project). Scratch paths are written `$SCRATCH`. The recordings stay in scratch, outside the
repository.

No product code changed, nothing merged, `main` untouched.
