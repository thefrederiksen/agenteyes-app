# Issue #156 - QA report, Linux (real screen captures)

**Verdict: QA-FAILED -> `flow:qa-failed`.** The cross-process claim itself works on Linux with real
captures, in every arm, with a pre-fix positive control in the same harness. Criterion 4 fails: a
genuinely crashed recording is NOT recovered on this branch on Linux, and on the hosts not run by
systemd it is this change's own reaper that destroys it (D1). Two further defects (D2, D3) belong in
the same round.

This is the only real Linux run in this issue's evidence: the developer's "Linux suite" ran on macOS,
and no earlier evidence involved a real screen capture.

## Machine and build

| | |
|---|---|
| Machine | `devlinux`, Ubuntu, kernel 6.8.0-149-generic, X11 session `DISPLAY=:0`, 1920x1080 HDMI-0, PipeWire-pulse |
| Runtime | .NET SDK 8.0.134, runtime 8.0.34 (native net8.0, no roll-forward) |
| ffmpeg | 6.1.1-3ubuntu5 (`/usr/bin/ffmpeg`), the one the Linux service uses |
| Head tested | `d24503771c63b5da92dcb4e9dad42dca397121cf` (`issue-156-capture-ownership`, PR #162) |
| Embedded commit of the binary run | `1.11.11+d24503771c63b5da92dcb4e9dad42dca397121cf`, read out of `src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes.dll` - the binary every FIX run executed |
| Build | `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64` -> **0 Error(s)**, 1 warning (existing CS8602 at `AlwaysOnEngine.cs:901`) |
| Suite (real Linux) | `dotnet test tests/AgentEyes.Tests.Linux -c Release` -> **Passed 315, Failed 0, Skipped 0**. Filtered to `CaptureOwnership`: **29 / 0** - the new tests did run inside that count |
| CI at `d245037` | runs 43864051170 and 43864056979, counts read from the logs: each **windows 2410 passed / 4 skipped / 0 failed, linux 315 / 0, macos 307 / 0** |
| Windows `AgentEyes.sln` | not run by this seat (Linux machine); CI above is the only Windows evidence |

### The four binaries (build identity by marker, not by dll hash)

Every scenario ran on all of these with the same harness. A dll sha256 is not used as identity: it embeds
the absolute source path. Markers were counted in the dll's UTF-16 strings and discriminate both ways.

| Tag | Source | Embedded version | `[CaptureLock]` | `frag_duration` | `[CaptureWatchdog]` |
|---|---|---|---|---|---|
| FIX | this branch `d245037` | `+d245037...` | 9 | 0 | 0 |
| MAIN | `main` `497e9d4` (pre-fix) | `+497e9d4...` | 0 | 0 | 0 |
| MAINFRAG | PR #137 head `ad21557` - the Review Gate's binary, fragmented, pre-fix | `+ad21557...` | 0 | 1 | 1 |
| MERGED | `d245037` merged with `ad21557` (#156 + #113), scratch worktree, commit `1855af6`, plus the 3-file scratch patch in D3 | `+1855af6...` | 9 | 1 | 1 |

MERGED is the fragmented arm (test 5). It is the configuration that exists once both PRs land, and it is
the only one where "the watchdog raises nothing" can fail at all. The branch on its own writes plain
MP4 and has no watchdog. The patch is `qa-linux/harness/merged-qa-edits.patch`. It was never
committed to any branch, and building it is how D3 was found.

## How the runs were made safe, and what proves it

- **Scratch library, proven before any service ran.** An independent net8.0 probe printed
  `GetFolderPath(LocalApplicationData / MyVideos)` under the real env (`/home/<user>/.local/share`,
  `/home/<user>/Videos`) and under the scratch env (both inside the scratch root). The positive control
  is in the same run, so the probe can tell the two apart. Every `agenteyes serve` was then SIGKILLed on the spot
  unless its own `recordings ->` line was inside its scratch root. Every service in every run printed
  a scratch root, so the abort never fired.
- **Not touched:** the owner's installed `agenteyes serve` (pid 6560, port 7882), the tray app (pid 6006),
  the real library (0 entries modified during the session; `2026-11-14_102532_video` intact), and the
  owner's audio settings (the mic was named explicitly per request; nothing was configured).
- **The foreign ffmpeg is pid 72110 on this machine today**, not 2197930 as briefed. It is the same go2rtc
  RTSP camera-wall command. It was checked alive at the start and end of every run and never signalled.
  Every leftover-process sweep matched `comm == ffmpeg` AND a scratch path in the command line.
- One self-inflicted incident, harmless: an early `pgrep -f` cleanup pattern matched my own tool shell,
  which SIGTERMed itself. Nothing else matched. The sweep was rewritten as above before any further run.
- **Real captures.** Every recording is a real `x11grab` + `pulse` capture started through
  `POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}`. The writer's command
  line is in the transcripts. A frame of a finished recording was viewed (real screen content, luma
  stable at ~100 across the take, not blank). It is not attached because it shows the owner's screen.
- Completion was judged from **manifest `PostProcessing.mux.State`**, never from `/status` (#159).

## The scenario (one run per binary covers tests 1-4)

`qa-linux/harness/combo2.sh <bin> <port>` - all in one run, one library:

1. **B and C**: two real captures (S0, S0c), whose owner processes are then **SIGKILLed mid-capture**.
2. A foreign `agenteyes package <B> --no-frames` runs **twice while B's orphaned writer is still writing** (test 4).
3. **S1** starts. This is "the next service start", and its `RepairService.Start` reaps. S1 then captures **A** live for the rest of the run.
4. A foreign `package <A> --no-frames` runs three times (test 1). Then `package <C>`: the CLI positive control.
5. **S2**, a second service, starts. Its reap and its first real repair pass (`trigger=timer`, +20 s) meet live A and crashed B in the same pass (tests 2 + 3).
6. A fourth foreign `package <A>`, then A is held 60 s more, which is past #113's 45 s stall limit.
7. The owner stops A, and it is followed to `mux.State`, then fully decoded.

## Results, Expected vs Actual

### Test 1 - a real live capture, touched by a second process driving the finalize directly

| | Expected | MAIN (pre-fix control) | MAINFRAG (pre-fix control) | FIX | MERGED |
|---|---|---|---|---|---|
| foreign `package` on live A | declines, names the holder | exit 1 `could not read the audio channel count` x3 | **muxed and published** | exit 1 `refusing to package ... being captured by the capture session of pid 1114760 on host devlinux` x4 | same decline, pid 1147866, x4 |
| attempts counted | none | **1 -> 2 -> 3**, then `failed 3 time(s) and is not retried` | 1 (prefix published) | none | none |
| `raw.mp4` | at its path, growing | in place, growing | **renamed to `recording.original.mp4` while still growing (2.47 MB -> 10.3 MB)** | in place, growing every check | in place, growing every check |
| `recording.mp4` while capturing | absent | absent | **12.4 s prefix, `PendingMux` cleared, mux `done`** | absent | absent |
| stall raised | none | n/a | **`capture stalled ... has not grown for 46s`, capture killed (exit 137) at 61.3 s** | n/a (no watchdog) | none; `CaptureLiveness=growing` for 118 s |
| owner finalize after stop | Attempts 1, plays | **refused: mux failed, budget gone** | only 12.4 s of ~61 s exists | `done`, **Attempts 1**, 107.6 s, 3228 frames, full decode exit 0, video+audio | `done`, **Attempts 1**, 119.2 s, 3574 frames, full decode exit 0, video+audio |
| claim after stop | released | - | - | `capture.owner.json` removed, kernel lock gone | same |

The kernel confirms the claim, independently of AgentEyes. `/proc/locks` shows
`FLOCK ADVISORY WRITE <service pid>` on `capture.lock` during the capture and nothing after it. An
independent `flock -n -s capture.lock` is REFUSED during the capture and SUCCEEDS once the owner dies
or releases. The shared probe and the exclusive capture lock map onto flock as the design assumes.

**PASS.** Artefacts: `run-fix-plain.txt`, `run-merged-frag.txt`; positive controls
`run-main-plain.txt`, `run-mainfrag.txt`. MAINFRAG is the Review Gate's reproduction, now measured on
Linux with a real capture, including the watchdog kill the gate inferred.

### Test 2 - the repair-pass arm: a second service instance's real pass meets the live capture

| | Expected | MAIN | FIX | MERGED |
|---|---|---|---|---|
| S2's start-up reap vs A's writer | left alone | (no capture records) | `owner pid 1114760 is alive - left alone`, writer kept writing | same, pid 1147866 |
| S2's real pass (`trigger=timer`) on A | declines, names the holder, no attempt | (A already exhausted by the CLI; B and C `admitted for resume ... (no claim)`, attempt counted, failed) | `FindUnfinished: skipping ... being captured by the capture session of pid 1114760` | same |
| the same pass on crashed B | repairs it | n/a | admitted, `stale capture claim taken over`, attempt by S2 (fails: D1) | admitted, taken over, **`mux done`, Attempts 1 by S2, 25.07 s, 750 frames, decodes** |

**PASS.** The positive control sits in the same pass: the pass that declined A did take over B. Note:
on Linux the real pass declines at `PostRecordingPlan.FindUnfinished`. The guard in
`PostRecording.Execute`, and its `DECLINED by pid` log line, is reached only if a capture starts
between the scan and the step. No real run hits that window; it is covered by the unit tests alone.

### Test 3 - the positive control: a recording orphaned by a dead owner must still be repaired

| | Expected | FIX (plain MP4) | MERGED (fragmented) | FIX under systemd (installed-unit settings) |
|---|---|---|---|---|
| claim judged stale | yes | yes - `stale capture claim taken over: its owner ... is gone and its 1 writer(s) have exited` | yes | yes |
| guard refuses it? | no | no - the attempt is made | no | no |
| recording recovered, manifest proves it | yes | **NO** - `could not read the audio channel count`; `raw.mp4`: `moov atom not found` | **YES** - `PendingMux` cleared, mux `done`, Attempts 1 by the new service, plays | **NO** - same failure, `moov atom not found` |

**FAIL on this branch (D1).** The takeover logic is right. The guard does not refuse both arms: the
fragmented arm recovers, and the CLI control `package <C>` recovered C in MERGED (22.7 s, decodes).
The data is gone, though, and on plain MP4 a real crash is not recovered by any host shape I measured.

The developer's "dead-owner" evidence does not show a crash. Its writer is stopped with `q` before
the owner is SIGKILLed, which writes the moov atom. No real crash does that.

### Test 4 - an orphaned capture ffmpeg that keeps writing after its owner is SIGKILLed

The Mac measurement holds on Linux. After the owner's SIGKILL, the writer is reparented (ppid 2221,
the user systemd) and keeps writing: B grew 2.6 MB -> 3.7 MB in 3 s with the kernel lock already free.
Under strace it wrote for 10 s+. SIGPIPE is ignored, so every stderr write fails with EPIPE and
ffmpeg carries on (`run-orphan-strace.txt`).

| | Expected | MAIN | MAINFRAG | FIX | MERGED |
|---|---|---|---|---|---|
| foreign `package` while the orphan writes | declines | attempt counted x2, fails | **publishes an 18.7 s prefix and renames the file the orphan is still writing** | `refusing to package ... video ffmpeg pid 1113392 is STILL RUNNING and writing into it` x2, nothing counted, writer kept writing | same, pid 1145426 |
| the orphan afterwards | reaped at the next service start | never reaped - recorded the screen for 2+ min until my cleanup | never reaped | reaped by S1's start (`ORPHAN (owner pid ... gone) - killed`) | same |

**PASS**, with two Linux facts the handoff does not have:
- **Under the installed unit's settings** (`KillMode=mixed`, `Restart=on-failure`, `RestartSec=10`,
  `TimeoutStopSec=120`, measured with a transient user unit), systemd SIGKILLs the orphan itself within
  2-4 s of the main pid's death. For the service host the orphan never survives, and the plain file is
  lost before #156's reaper runs (`run-systemd-fix.txt`).
- The orphan survives only on a host systemd does not supervise: the tray app, the CLI, or a hand-run
  serve. There, #156's reaper is what ends it (D1).

### Test 5 - both container arms

Covered above. Plain MP4 is FIX and MAIN. Fragmented is MERGED, with MAINFRAG as its pre-fix
control. The fragmented output was not constructed with a stand-in: it is #113's own
`FragmentedOutput()` from PR #137 on a real capture, merged with this branch. The bare patch arm
(`fixfrag-frag.patch` on `d245037`) was also built, but its verdicts were not used because it has no
watchdog.

### First ordering run, kept for honesty: `run-fix-first-order.txt`

That run SIGKILLed one owner and then started S1 before packaging B. S1's start-up reap killed B's
orphan, so the foreign `package` met a dead writer and took the claim over. This is correct, but it
was not test 4. The scenario was reordered (v2) so that the CLI meets a still-writing orphan.

## Acceptance criteria

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Race reproduced deterministically before the fix | **PASS** | MAIN, real capture: foreign `package` drives A's mux Attempts 1 -> 2 -> 3, then the healthy recording is refused forever. The repair arm on MAIN logs `admitted for resume ... (no claim)` and counts an attempt |
| 2 | Same scenario after the fix: attempt count 1, decline names the holder | **PASS** | FIX and MERGED: 4 CLI declines + 1 repair-pass decline, each naming pid and host; owner finalizes at Attempts 1 |
| 3 | A foreign process cannot consume the budget | **PASS** | same runs: five foreign passes, `mux: none` throughout, then Attempts 1 |
| 4 | A stale claim does not strand a recording; a crashed host's recording is recovered | **FAIL** | takeover correct, but no real crash recovered on this branch on Linux (D1). Recovered only in MERGED |
| 5 | The manifest says who consumed an attempt | **PASS, with a gap (G1)** | `AttemptedBy` lists every attempt with pid and host, e.g. C: three attempts by pids 1116788 (CLI), 1116859 (S2), 1114760 (S1) |
| 6 | Both hosts still work side by side | **PASS on one machine; FAIL across machines (D2)** | while a real capture ran: REST rename of another recording -> `DisplayName` written; `package` of it -> passes the guard; the live one still declined (`run-side-by-side.txt`). `translate` not run (needs sign-in) |
| 7 | Gates green on all three platforms, CI across more than one run | **PASS** (Linux locally; Windows/macOS by CI only) | 315/0/0 here; two CI runs at `d245037` green on all three |

## Defects

### D1 (blocking, criterion 4) - the orphan reaper SIGKILLs a capture writer, destroying a recording a graceful stop would have saved

- **Where:** `src/AgentEyes.Core/CaptureLock.cs:247` (`NoteWriter` registers every capture ffmpeg with
  the reaper; new in this PR) -> `src/AgentEyes.Core/Video/FfmpegChildRegistry.cs:114` ->
  `KillTree`, `:145-148` (`p.Kill(entireProcessTree: true)`, i.e. SIGKILL).
- **Reproduction (FIX, real capture, `run-fix-plain.txt`):** capture; SIGKILL the owning service; the
  writer keeps writing (measured); start a service -> `ReapOrphans: pid <writer> [capture video ...]:
  ORPHAN ... killed` -> `raw.mp4`: `moov atom not found` -> its repair: `stale capture claim taken over` ->
  `could not read the audio channel count` -> mux `failed`. Repeats every pass until the budget is gone.
- **Same run shape, writer ended by SIGTERM instead (MAIN's orphans, ended by my cleanup):** `raw.mp4`
  probes at **142.5 s with 2-channel audio**, a complete recording that the takeover would then repair.
  strace shows ffmpeg answering SIGTERM with `Exiting normally, received signal`. The choice of SIGKILL
  is the whole difference.
- **Expected:** a crashed host's recording is recovered (criterion 4, and the handoff's own claim
  "a crashed host's recording is recovered after the next service start").
- **Actual:** on the container this branch ships it is destroyed by this branch's reaper on every host
  systemd does not supervise.
- **Fix direction:** end a reaped capture writer the way the product ends one, with SIGINT/SIGTERM, so
  ffmpeg writes its trailer, and wait for it. A writer that does not exit is a logged failure, not a
  silent SIGKILL. The post-processing children #128 reaps can keep `Kill`.
- **What this fix cannot reach, for the lead to decide:** under the installed unit's
  `KillMode=mixed`, systemd SIGKILLs the orphan itself within 2-4 s. So for the service host, a crashed
  plain-MP4 recording is lost whatever #156 does. Only #113's fragmented container or a unit change
  recovers it. If the ruling is that criterion 4 may lean on #113 for that host, D1 still stands for
  the tray app and the CLI.

### D2 (criterion 6 across machines; the handoff's shared-library claim) - a recovered crash leaves its owner record behind forever, and every other host then refuses the finished recording

- **Where:** `src/AgentEyes.Core/CaptureLock.cs:386` - a stale claim is "taken over" but
  `capture.owner.json` is never removed. Only the owner's own `Dispose` deletes it. The foreign-host
  branch at `:373-376` then reports such a record LIVE.
- **Reproduction (`run-foreign-owner-record.txt`):** take MERGED's crashed-and-repaired B (`PendingMux`
  cleared, mux `done`, `capture.owner.json` still present). Copy it twice, and in one copy change the
  record's `Host`/`HostId` to another machine's, as a moved or shared library would present it.
  `agenteyes package` on the same-host copy passes the guard (the positive control).
  On the other-host copy it gives exit 1: `refusing to package ...: its capture owner ... is gone, but its
  video ffmpeg pid 1145426 ran on host otherhost and cannot be checked from devlinux.` The same `Inspect`
  makes every other host's repair pass and thumbnail repair skip it.
- **Expected:** a recording that was finalized is not "being captured" anywhere.
- **Actual:** it is refused on every other machine, permanently, until a human deletes a file.
- **Fix direction:** remove `capture.owner.json` once a takeover has finalized the recording (or when
  `FreeLockLiveness` proves it stale and the finalizer holds `mux.lock`).

### D3 (blocks #113 landing on top of this) - #156 and #113 do not compile together

- `src/AgentEyes.Core/CaptureLock.cs:10` declares `AgentEyes.CaptureLiveness`. #113 already declares
  `AgentEyes.Video.CaptureLiveness` (`Video/CaptureWatchdog.cs:14` on `ad21557`). In the merge,
  `RecordingService` binds to #156's class, and #113's watchdog code fails: `CS1061 'CaptureLiveness' does not
  contain a definition for 'State' / 'Bytes' / 'LastGrowthUtc' / 'SecondsSinceGrowth'`
  (`RecordingService.cs:437-441` in the merge).
- `RecordingService.cs:693` calls `NoteCaptureWriter("video", _video.ProcessId)`. #113 makes `_video` an
  `IScreenRecorder` (`Video/IScreenRecorder.cs:15`), which has no `ProcessId`: `CS1061`.
- The textual conflict at the same line is trivial; the two semantic ones are not visible until it is built.
  The fix exists to make #113 safe, and the configuration it exists for had never been compiled.
- **Fix direction:** rename #156's type (for example `CaptureClaimLiveness`). Then whichever PR lands
  second adds `ProcessId` to `IScreenRecorder`. I made exactly these edits in scratch to build MERGED
  (`merged-qa-edits.patch`), and with them every test-5 arm above passes.

### G1 (not blocking, criterion 5) - the manifest never records who OWNED the capture

`AttemptedBy` says who consumed each attempt, but nothing in `manifest.json` names the capturing
process. The owner record is a separate file, deleted on a clean release. A post-mortem sees "three
attempts by three pids" and cannot tell from the manifest whether any of them was the owner. In C's
case none was (owner 1113713). Recording the capture owner's process, pid and host in the manifest at
`BeginSession` (`RecordingService.cs:1317`) closes it.

## Other findings (not defects of this change)

- **The harness guard does not cover the start-up reap.** `repair.Start()` (`HeadlessHost.cs:56-57`)
  reaps synchronously, and can create `host-id`, BEFORE the `recordings ->` line that
  `repro-cross-process.sh` waits for (`HeadlessHost.cs:71`). The guard protects the first pass at
  +20 s, not the reap. On Linux isolation itself works (proven above), so this mattered here only as a check.
- **`serve` started in the background of a non-interactive script ignores SIGINT** (inherited
  `SigIgn=0x1006`); SIGTERM, which systemd sends, stops it in ~2.5 s (`harness/sigtest.sh`). A harness
  property, not a product bug. My harness uses SIGTERM.
- The handoff's `both-arms.sh` / `repro-cross-process.sh` are macOS-only as written (`stat -f %z`,
  `date -j`); they do not run on Linux.

## What this report does NOT cover

- **Windows**: no build, no test, no real-app check by this seat. CI only.
- **The tray app** (`AgentEyesLinux --tray`): every capturing host here was `agenteyes serve`. The tray
  shares `RecordingService`, but it was not run.
- **The 15-minute period itself**: the passes measured are the real timer callback's first firing
  (`trigger=timer`, +20 s); no second firing was waited for.
- **Two machines on one shared library**: not measured. D2 is shown by presenting a record as another
  host's, not with a second machine.
- **`translate`** (needs a signed-in account; not used), **camera writers** (no camera on devlinux, so
  `NoteCaptureWriter("camera")` never ran), **audio-only** and **system/mixed** sources.
- **The `Execute`-level decline** in a real pass (see test 2): unit tests only.
- **#113's two-frame A/V offset** and its other gate notes: not this issue.

## Artefacts

`docs/cencon/proof/issue-156/qa-linux/`: `run-fix-plain.txt`, `run-main-plain.txt`, `run-mainfrag.txt`,
`run-merged-frag.txt`, `run-systemd-fix.txt`, `run-foreign-owner-record.txt`, `run-side-by-side.txt`,
`run-orphan-strace.txt`, `run-fix-first-order.txt`. The harness is in `qa-linux/harness/`
(`SCRATCH=<empty dir>`; it signals only pids it started, and aborts a service whose recordings root
is outside the scratch root). The recordings themselves are kept outside the repository.
