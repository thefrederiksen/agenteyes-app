# Issue #113 - QA pass 5, LINUX half (criteria 1, 2, 5; the start offset; the early kill)

**Platform: Linux.** devlinux, Ubuntu (kernel 6.8.0-149), x86_64, 32 cores; ffmpeg 6.1.1-3ubuntu5
(`/usr/bin/ffmpeg`); .NET SDK 8.0.134 building net8.0, runtime 8.0.34. Real capture devices:
**x11grab** on `:0` (one 1920x1080 monitor, HDMI-0) and **pulse** microphone
`alsa_input.pci-0000_08_00.3.analog-stereo` ("Family 17h (Models 00h-0fh) HD Audio Controller Analog
Stereo"). The Windows half is `qa-report-windows.md`; macOS is being run by another seat.

**Under test:** PR #137, branch `issue-113-capture-watchdog`, checked out at `fc49b66` (= `2716e0f` +
the Windows QA docs commit; `git diff --name-only 00c9df9 fc49b66` lists only files under `docs/`, so
the code is `00c9df9`). Binary that ran: **`1.11.11+fc49b666c90d758b807b51b442bb1a3fc70df6f9`**.

Comparison builds, each built by me from its own worktree:

| name | commit | container | embedded version |
|---|---|---|---|
| **head** | `fc49b66` (code `00c9df9`) | fragmented, `+delay_moov` | `1.11.11+fc49b666c90d758b807b51b442bb1a3fc70df6f9` |
| **pre** | `9f45b33` (the commit before `3debd3d`, which added `+delay_moov`) | fragmented, no `delay_moov` | `1.11.11+9f45b33633cab897e2c4c81db23a5fbd5ecf0584` |
| **main** | `277a9bc` (`origin/main` today) | plain MP4, no `-movflags` | `1.11.11+277a9bcf71c7f82547353177299178efc1020327` |

The flags each build actually ran with were read from the live ffmpeg's `/proc/<pid>/cmdline` and
are quoted in each run's `scenario.log` (`qa-pass5-linux/runs/`).

## Verdicts

| Job | Verdict | Headline |
|---|---|---|
| Criterion 1 (frozen capture) | **PASS** | SIGSTOP (mine) -> state left `recording` **46.2 s** later, fault named **in the same poll**, never a healthy-looking stop; the product's `Abandon` killed the stopped ffmpeg in 459 ms |
| Criterion 2 (dead capture) | **PASS** | SIGKILL (mine) -> state left `recording` and fault named **2.1 s** later, `exited (code 137)` |
| Criterion 5 (amended) | **PASS** | After both faults, raw and final files: 500 counted frames, 16.67 s, full decode exit 0 with 0 error lines, a frame from the last second. Control: main killed at 10 s -> `moov atom not found` |
| Start offset, Linux | **head 0.000 / 0.000 at 30 and 5 fps; main 0.000 / 0.000 at 30 and 5 fps** | Linux was **not** carrying a silent offset on main. The un-fixed fragmented build (`pre`) is late by exactly two frames: 0.067 s at 30 fps, 0.400 s at 5 fps |
| Early kill | **Honest failure (head); false `done` (pre)** | Same as Windows. head: 0 bytes, mux `failed`. pre: 1241-byte zero-frame header muxed to a **262-byte stream-less `recording.mp4` marked `done`**. Linux's empty window is longer than Windows': 0 bytes at 3.0 s, frames at 4.0 s |
| Gate (Linux) | **PASS** | Core build (`AgentEyesUnixPlatform=linux`) 0 errors 0 warnings; `dotnet test tests/AgentEyes.Tests.Linux -c Release` **472 passed, 2 skipped, 0 failed** |

No defect found. Labels are left alone.

## How it was driven, and isolation

There is no supported way at this head to point a shipped binary at a scratch library (#161 is not
merged into this branch). So I ran the developer's pass 5 evidence host
(`pass5/evidence/harness/Program.cs`), **retargeted to net8.0 / Linux** (`qa-pass5-linux/scripts/`):
the **production** `RecordingService` with production seams (real `FfmpegRecorder`, real
`CaptureWatchdog`, real `CaptureLock`) behind the **production** `HeadlessControlServer`, on port
**7991**, after `AppDataPaths.RedirectForThisProcess`. That call refuses if any AgentEyes path has
already been resolved, so a failed redirect throws instead of looking isolated. I checked
`src/AgentEyes.Core` and found only one other place that asks the OS for a folder,
`CaptureService.cs:91` (Pictures, which these runs do not reach), so on Linux the redirect covers
recordings, the claim files, the ffmpeg child registry and the log. No `RepairService` runs. Every
request went through the Control API on that port. My driver is `qa-pass5-linux/scripts/drive.py`.

Checks with their positive controls:
- **Each recording landed in scratch:** the `/status` `Dir` and the manifest are under
  `<scratch>/ev/<arm>/Videos/AgentEyes/`. That is the presence.
- **The owner's real library was not touched:** after all 20 runs, `~/.local/share/AgentEyes/logs/AgentEyes-20261116.log`
  had **0** lines naming port 7991 or my scratch path. The positive control is that the scratch
  path appears 8 times in a single run's own log. No new directory in `~/Videos/AgentEyes` since
  before I started (newest is `2026-11-15_143441_video`).
- **Nothing installed was used or changed.** The installed service (`~/.local/lib/agenteyes/agenteyes serve`,
  pid 6560, port 7882) is `1.11.11+89e15934278fdf106d30d6ae2cfd13c22e9972c2`, which is
  **pre-#156** (a build of an unmerged branch, as recorded on #116). I sent it only two read-only
  GETs (`/health`, `/devices`, to learn the mic name) and one `/status`. I did not reinstall,
  stop or reconfigure it. `AgentEyesLinux --tray` (pid 1298614) was not touched.
- **Every signal was sent only to an ffmpeg this arm's host started.** Before each signal the driver
  checked that `/proc/<pid>/stat` ppid == the host pid I had just spawned AND that the command line
  names this arm's scratch folder. Otherwise it aborts (it never aborted). The foreign go2rtc
  camera-wall ffmpeg (**pid 72110**, `-user_agent go2rtc/ffmpeg ... rtsp://127.0.0.1:8554/lake`) was
  identified by its command line before I started and was still running, untouched, at the end.
- **Who killed what:** every SIGSTOP and SIGKILL in this report is **MINE** (the driver logs
  `INJECT: ... by THIS SCRIPT`). The one kill by the **product** is the watchdog's `Abandon` of the
  SIGSTOPped capture in criterion 1 (`Abandon ... ended in 459ms (killed, exit 137)`). In criterion
  2 and in every early-kill run, `Abandon` logged `ended in 0ms (had already exited, exit 137)`:
  the product killed nothing there.
- Completion was read from the manifest's `PostProcessing.mux.State`, never from `/status` idle (#159).

All captures were screen 1 + mic, `/record/start {"mode":"video","screen":1,"source":"mic","mic":...,"fps":N}`.

## Criterion 1 - a frozen capture ends the recording (amended form)

Expected: within 60 s `/status` is not `recording` and names the fault. The fault-name time is
reported separately, and `/status` must not look like a healthy stop in between.

Actual (`runs/head-c1-sigstop.scenario.log`, 30 fps, writer pid 2087757, parent = host 2087651):

```
20:23:23.699Z /status [healthy 1] State=recording CaptureLiveness=growing CaptureBytes=1147447 LastGrowth 20:23:23.646Z
20:23:33.701Z /status [healthy 2] State=recording CaptureLiveness=growing CaptureBytes=2842321 LastGrowth 20:23:33.648Z
20:23:33.702Z INJECT: SIGSTOP(pid 2087757) by THIS SCRIPT; proc state now R   (state T from the next poll on)
20:23:35.711Z [+2.0s]  State=recording CaptureLiveness=not-growing CaptureBytes=2842321 SecondsSinceGrowth=2
   ... every poll 0.5 s apart, logged on change and every 5 s: State=recording, not-growing, bytes frozen ...
20:24:15.858Z [+42.2s] State=recording CaptureLiveness=not-growing CaptureBytes=2842321 SecondsSinceGrowth=42
20:24:19.873Z [+46.2s] State=finalizing CaptureLiveness=faulted LastStopFailed=true
                       LastCaptureFault="capture stalled: raw.mp4 stopped growing at 2842321 bytes and has not
                       grown for 46s (limit 45s); last growth 2026-11-16T20:23:33.648Z"
20:24:20.375Z [+46.7s] State=idle LastStopFailed=true LastCaptureFault=(same) writer=gone
```

- **State flip: 46.2 s** after the injection. **Fault named: 46.2 s**, in the same poll as the flip.
  Polling every 0.5 s found no poll where `State` had left `recording` without the fault. The first
  non-`recording` body already says `finalizing`, `faulted`, `LastStopFailed=true` and gives the
  reason, so at no point did it look like a healthy stop. **PASS.**
- Criterion 3's signal, as a side observation: `CaptureBytes` advanced between the two healthy polls
  10 s apart (1147447 -> 2842321) and stopped advancing after the stall (2842321 frozen), while
  `ElapsedSeconds` kept moving.
- Product log at detection (criterion 7's shape, from the redirected log):
  `15:24:19.661 [pid 2087651] [ERROR] [RecordingService] CAPTURE FAULT at elapsed=65.2s (detected 2026-11-16T20:24:19.659Z) ...`
  and `[FfmpegRecorder] Abandon: ... ended in 459ms (killed, exit 137, last reported position 18.20s)`.
- Manifest: `CaptureFault: "stalled"`, `CaptureFaultDetectedUtc: "2026-11-16T20:24:19.6590169Z"`,
  `CaptureFaultElapsedSeconds: 64`, `DurationSeconds: 16.67`, mux `done`, `Attempts: 1`.

## Criterion 2 - a dead capture ends the recording

Actual (`runs/head-c2-sigkill.scenario.log`, 30 fps, writer pid 2092273, parent = host 2092164):

```
20:25:08.112Z /status [healthy 1] State=recording growing CaptureBytes=1032827
20:25:18.127Z /status [healthy 2] State=recording growing CaptureBytes=2287271
20:25:18.129Z INJECT: SIGKILL(pid 2092273) by THIS SCRIPT
20:25:18.631Z [+0.5s] State=recording CaptureLiveness=growing (the watchdog has not ticked yet)
20:25:20.183Z [+2.1s] State=finalizing CaptureLiveness=faulted LastStopFailed=true
                      LastCaptureFault="capture exited: the capture process exited (code 137) while recording;
                      raw.mp4 holds 2287271 bytes"
20:25:20.687Z [+2.6s] State=idle (fault kept)
```

**State flip and fault named: 2.1 s**, in the same poll. **PASS.** Manifest: `CaptureFault: "exited"`,
`CaptureFaultDetectedUtc: "2026-11-16T20:25:20.0700974Z"`, mux `done`.

**Control (main, `runs/main-early10.scenario.log`):** main has no watchdog. After the same SIGKILL at
10 s, `/status` still said `State: recording` 3 s later with no fault, and only my `/record/stop`
ended it. That is the defect this issue exists for, still present on main on Linux.

## Criterion 5 (amended) - an unclean end still leaves a playable file

Expected: after criterion 1 and after criterion 2, a file in the recording directory that decodes
**in full to exit 0** and yields a **frame read from near the end**, with a real duration and a
counted frame total covering what was captured. A moov atom alone is not enough.

Actual (`runs/head-c1-sigstop.verify.txt`, `runs/head-c2-sigkill.verify.txt`; frames counted with
`ffprobe -count_frames`, decode `ffmpeg -v error -i f -f null -`, end frame `ffmpeg -sseof -1 ... -frames:v 1`):

| file | video start / dur / counted frames | audio start / dur | full decode | last-second frame |
|---|---|---|---|---|
| c1 SIGSTOP `recording.original.mp4` (the raw capture) | 0.000 / 16.667 s / **500** | 0.000 / 16.627 s | **exit 0, 0 error lines** | exit 0, 3.9 MB png |
| c1 SIGSTOP `recording.mp4` | 0.000 / 16.667 s / **500** | 0.000 / 16.580 s | **exit 0, 0 error lines** | exit 0 |
| c2 SIGKILL `recording.original.mp4` | 0.000 / 16.667 s / **500** | 0.000 / 16.640 s | **exit 0, 0 error lines** | exit 0 |
| c2 SIGKILL `recording.mp4` | 0.000 / 16.667 s / **500** | 0.000 / 16.580 s | **exit 0, 0 error lines** | exit 0 |

Video packet pts run 0.000 -> 16.633 s. **PASS.**

How much was kept: the capture was stopped or killed about 18 s in, and ffmpeg's last reported
position was 18.20 s (c1) and 18.26 s (c2). The file keeps **16.67 s**. That is the last completed
2-second fragment; the encoder's in-flight frames after it are lost. The issue's text promises "at
most about two seconds", and 1.5-1.6 s here fits it.

**Control:** main's plain MP4, SIGKILLed at 10 s with 1,310,768 bytes on disk
(`runs/main-early10.verify.txt`): `moov atom not found ... Invalid data found when processing input`,
and the mux failed. The same kill on head keeps the file.

## Job 2 - the start offset on Linux, before and after

Healthy 20 s screen+mic captures, ended by `POST /record/stop`. These are container `start_time`s from
`ffprobe` (`runs/*-off*.verify.txt`). `recording.original.mp4` is the raw capture after the
product's rename; `recording.mp4` is the final file.

| build | fps | raw: video / audio | final: video / audio |
|---|---|---|---|
| **main** `277a9bc` (plain MP4) | 30 | **0.000 / 0.000** | **0.000 / 0.000** |
| **main** `277a9bc` (plain MP4) | 5 | **0.000 / 0.000** | **0.000 / 0.000** |
| **head** `fc49b66` (`+delay_moov`) | 30 | **0.000 / 0.000** | **0.000 / 0.000** |
| **head** `fc49b66` (`+delay_moov`) | 5 | **0.000 / 0.000** | **0.000 / 0.000** |
| pre `9f45b33` (fragmented, no `delay_moov`) | 30 | 0.066667 / 0.000 | 0.066016 / 0.000 |
| pre `9f45b33` (fragmented, no `delay_moov`) | 5 | 0.400000 / 0.000 | 0.400000 / 0.000 |

**The four numbers asked for (video start, audio start = 0.000 in every case):** main 30 fps **0.000**,
main 5 fps **0.000**, head 30 fps **0.000**, head 5 fps **0.000**.

What this says about x11grab: **Linux was not carrying a silent offset on main.** Plain MP4's edit
list already cancelled the B-frame delay. Without `delay_moov`, the fragmented container shows the
delay at its pure size of exactly **two frame intervals** (2/30 = 0.0667 s, 2/5 = 0.400 s). It is
not stretched the way it was on Windows (0.80-0.83 s), where gdigrab delivers its second frame about
0.7 s after its first. So on Linux the synthetic 0.067 s happens to equal the real figure at 30 fps.
That holds only for this backend. At 5 fps the un-fixed build would have shown the video 0.4 s
late. `+delay_moov` brings both rates to 0.000.

## Job 3 - the early kill, both ways

SIGKILL (mine) of the capture ffmpeg at a set age, measured from **that process's own start time**
(`/proc/<pid>/stat`), 30 fps. Each run's result was read from the manifest (`runs/*-early*.scenario.log`,
`*.verify.txt`).

| build | kill at | `raw.mp4` on disk | product result | final file |
|---|---|---|---|---|
| **head** | 1.0 s | **0 bytes** | mux **`failed`** ("could not read the audio channel count ..."), `PendingMux` kept, `/status` `LastStopFailed=true`, fault `exited (code 137) ... raw.mp4 holds 0 bytes` | none |
| **head** | 1.8 s | **0 bytes** | mux **`failed`**, same | none |
| **head** | 3.0 s | **0 bytes** | mux **`failed`**, same | none |
| head (positive control) | 4.0 s | 662,123 | mux `done` | 60 frames, 2.0 s, decode exit 0, last-second frame |
| head (positive control) | 5.0 s | 727,026 | mux `done` | 60 frames, 2.0 s, decode exit 0 |
| head (positive control) | 6.0 s | 1,054,729 | mux `done` | 120 frames, 4.0 s, decode exit 0 |
| **pre** | 1.0 s | 1,241 (header, no samples) | mux **`done`**, `DurationSeconds: 3.17` | **262-byte `recording.mp4` with NO streams** (decode exit 234 "Output file does not contain any stream") |
| **pre** | 1.8 s | 1,241 | mux **`done`** | **262-byte stream-less `recording.mp4`** |
| **pre** | 3.0 s | 1,241 | mux **`done`** | **262-byte stream-less `recording.mp4`** |
| pre (control) | 4.0 s | 659,635 | mux `done` | 60 frames, 2.0 s, decode exit 0 (video start 0.067) |
| main (plain) | 1.0 s | 48 | mux `failed`, but `/status` still `recording` until my stop (no watchdog) | none |

**Which outcome Linux gives, both ways: the same as Windows.** After the fix, an early kill leaves
0 bytes and the mux **fails honestly**, with the fault named on `/status` and in the manifest.
Before the fix, the zero-frame header was muxed into a **stream-less `recording.mp4` marked `done`**,
which is the defect. Nothing recoverable is lost by the fix: at 4.0 s both builds hold exactly 60
frames, so the first real samples reach the disk at the same moment either way. The fix only stops
an empty stub from being published as done.

**One Linux-specific number:** the empty window is longer here than on Windows. Windows QA's 3.00 s
positive control kept 14 frames; on this machine a 3.0 s kill leaves 0 bytes on **both** builds,
and the first fragment is on disk somewhere between 3.0 s and 4.0 s of ffmpeg process age (ffmpeg
start-up plus the 2 s first fragment plus encoder lookahead). The comment at
`src/AgentEyes.Core/Video/FfmpegArgs.cs:121-122` ("the file stays empty until the first fragment
(about two seconds)") counts media time, so it understates the wall-clock window on this platform.
That is not a defect: the window is still well inside the watchdog's 45 s start-up window, so a
watchdog kill never lands in it, and the pre build holds nothing playable there either.

## Gate on this machine

`qa-pass5-linux/gate-linux.txt`:
- `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux`
  -> `Build succeeded.`, `0 Warning(s)`, `0 Error(s)`.
- `dotnet test tests/AgentEyes.Tests.Linux -c Release` -> **Failed: 0, Passed: 472, Skipped: 2,
  Total: 474** (the two skips are `OwnedFfmpegTests.KillOwned_FailurePath...` and
  `ProcessIdentityTests.PackageCli_SandboxedProberLiveWriter...`, both opt-in). This matches CI's
  Linux 472/2 on the merge ref.
- The Linux command line is pinned exactly, `delay_moov` included, by
  `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs:25`, and the fragment-before-output order by
  `AssertFragmentedBefore` (`:53`).

## What this report does NOT cover (Linux)

- **Not end-to-end A/V sync.** The offsets above are the container's presentation timestamps (where
  the first video and audio samples sit in the timeline). They are not a measurement of how long
  x11grab and pulse each take, in the real world, to deliver their first sample. Measuring that
  needs a visible-and-audible marker (a flash plus a click) recorded on both inputs, which this
  seat did not set up. The mic in these runs was picking up near-silence (levels about 0.001).
- **The timeline guard test does not run on Linux.** `FragmentedOutputTimelineTests` (the real
  ffmpeg test that fails without `+delay_moov`) is in `tests/AgentEyes.Tests`, the Windows suite.
  The Linux suite pins the command-line string, not the resulting timeline. The Linux timeline is
  covered by the runtime measurements in this report, not by a Linux test.
- **The shipped hosts did not run this code.** Neither `agenteyes serve` nor `AgentEyesLinux` ran this
  code. The production `RecordingService` and `HeadlessControlServer` did, inside an evidence host
  with redirected roots (#161). The installed service is pre-#156 `89e1593`; I did not reinstall it.
- **No macOS or Windows.** No macOS runtime was run here, and Windows is reported separately. A
  suite that passed on another platform is not counted here as a run on this one.
- **Criteria 3, 4, 6, 7, 8 were not re-run as their own items.** Criteria 3, 6 and 7 can be read from
  the criterion 1 and 2 logs quoted above. The 10-minute healthy run (criterion 4) was not repeated
  this pass.
- **The gate's two-process scenario was not re-run on Linux.** Windows covers it with both arms. It
  was not in this seat's brief.
- **The empty early-kill capture is left for a retry.** After an early kill on head the manifest keeps
  `PendingMux` for a 0-byte `raw.mp4`, so a later repair pass will try it again. Windows QA noted the
  same. The failure stays honest; I am recording it here, not filing it.
- **The machine was shared.** Load average rose to about 9 on 32 cores during these runs (other seats'
  containerised test runs). No `speed=` collapse or frame drop beyond ffmpeg's normal start-up
  `dup=6 drop=4` was seen. The runs were one at a time, not concurrent with each other.
- **One device per input.** These are single runs per arm on one monitor (1920x1080) and one mic. I
  did not vary resolution, region capture, camera or system audio.

Recordings and PNGs are kept in my scratch folder, not in the repository. Only scrubbed text is
committed (`qa-pass5-linux/`). Nothing was deleted from any library.
