# Issue #113 - QA pass 5, macOS half: criteria 1, 2, 5, the start offset, and the early kill

**Platform: macOS.** Windows passed its seven items separately (`qa-report-windows.md`); Linux is run
by another seat in parallel. Nothing here is a Windows or Linux measurement.

**Verdict for this platform: PASS on criteria 1, 2 and 5. No defect found. Labels left alone.**

| | |
|---|---|
| Seat | independent QA, macOS. Did not write any of this code. |
| Machine | devthrottle-mac-mini, macOS 26.5.2 arm64, ffmpeg 9.0.2 (Homebrew) |
| PR / head tested | #137, head `fc49b66`. Code head `00c9df9`: `git diff --quiet 00c9df9 fc49b66 -- src` exits 0, so `fc49b66` (the Windows QA docs commit) carries the same product code as the `2716e0f` I was sent. |
| Binary that ran | `1.11.11+fc49b666c90d758b807b51b442bb1a3fc70df6f9`, read from the running process (`[harness] ... core=`) and from the built `agenteyes.dll` |
| Control builds | `main` `277a9bc` -> `1.11.11+277a9bcf71c7f82547353177299178efc1020327`; `00c9df9` with `+delay_moov` removed and nothing else -> `1.11.11+00c9df9-QA-delay_moov-removed` (built by me from `git archive`, one `sed` on `FfmpegArgs.cs:129`) |
| Flags actually used | read from each live ffmpeg's command line: head `-movflags +frag_keyframe+empty_moov+default_base_moof+delay_moov -frag_duration 2000000 -flush_packets 1`; main no movflags; control `+frag_keyframe+empty_moov+default_base_moof` |
| Toolchain | SDK 10.0.403 building `net8.0`. **There is no .NET 8 SDK on this Mac.** The recorder code ran on the installed bundle's own **.NET 8.0.33** runtime (see section 1); the unit tests ran on the .NET 10 runtime with roll-forward. |
| Screen | locked; every decoded frame is the wallpaper. Read: `ac1` last-second frame, a lake with rocks, not blank. |
| Mic | the only input present is `Microsoft Teams Audio` (a virtual device). |

## 1. How a real macOS screen capture was driven, and why it is isolated

A shell on this Mac has no Screen Recording grant, and I was told not to touch the owner's service
(`com.agenteyes.recorder`, pid 50642). Its installed binary is **`f9ef230`, pre-#156** (read from the
installed `agenteyes.dll`; `git merge-base --is-ancestor d0d0a27 f9ef230` is false), so it ignores
`capture.lock` - nothing of mine was put where its repair pass looks.

What ran instead (tools in `qa-pass5-macos/tools/`):

1. `EvidenceHost` - the developer's pass-5 harness shape, rebuilt for `net8.0`: the PRODUCTION
   `RecordingService` (real `FfmpegRecorder`, `CaptureWatchdog`, `CaptureLock`) behind the PRODUCTION
   `HeadlessControlServer`, with both roots moved by `AppDataPaths.RedirectForThisProcess` into scratch
   before any path resolves. No `RepairService`. Built three times: head, main, head-without-delay_moov.
2. `QaAlcHook` - a .NET startup hook that loads that harness in its **own AssemblyLoadContext** (so the
   installed `agenteyes.dll` is never used for product code), runs it, and calls `Environment.Exit`.
3. `job.py` - runs the **installed** `agenteyes` apphost as a one-shot launchd job
   (`com.agenteyes.qa113mac.<name>`, `Aqua`, `Interactive`) with the hook. The ffmpeg it starts is
   charged to the binary holding the grant, as in the #116 pass-3 method. Probe first:
   `screenGrant=True`. Ports 7913-7917, never 7882.

Every run's `job.out` shows `core=<sha>` and `data=` / `recordings=` under the scratch root. All five
labels booted out; `launchctl list | grep -c qa113mac` = 0 afterwards.

What this is NOT: it is not `agenteyes serve` and not an installed build. The startup path, the repair
pass and the menu-bar app did not run this code.

## 2. Criterion 1 - a frozen capture ends the recording (SIGSTOP)

Run `runs/head-b-ac1-sigstop.txt`, 30 fps, screen 1 + mic, load 2.2 on 10 cores. **I sent SIGSTOP and
nothing else** to pid 72103, whose parent I checked was my harness pid 71634 at the moment of the signal.

| Clock | Since SIGSTOP | `/status` |
|---|---|---|
| 15:25:00.975 | - | `recording`, `growing`, CaptureBytes 3290650 |
| 15:25:09.227 | - | `recording`, `growing`, CaptureBytes 7941728 |
| 15:25:13.369 | 0 | SIGSTOP -> 72103 |
| 15:25:15.498 | 2.13 s | `recording`, **`not-growing`**, 10465772, SecondsSinceGrowth 2 |
| 15:25:24.007 | 10.64 s | `recording`, `not-growing`, 10465772, SecondsSinceGrowth 10 |
| 15:25:57.980 | 44.61 s | `recording`, `not-growing`, 10465772, SecondsSinceGrowth 44 |
| 15:25:59.037 | **45.67 s** | **`finalizing`, `faulted`, LastStopFailed true, "capture watchdog: capture stalled: raw.mp4 stopped growing at 10465772 bytes and has not grown for 46s (limit 45s); last growth 2026-11-16T20:25:12.703Z"** |
| 15:26:00.123 | 46.75 s | `idle`, LastStopFailed true, the same error |

- **Expected:** not-`recording` within 60 s, the fault named, and `/status` not resembling a healthy stop
  in between (amended criterion 1: state and name reported separately).
- **Actual:** state flipped at **<= 45.67 s** (the poll before, at 44.61 s, was still `recording`); the
  fault NAME appeared **in the same poll**, so both bounds are 45.67 s. Between injection and verdict
  every poll read `not-growing` with a rising `CaptureSecondsSinceGrowth`; after it,
  `LastStopFailed=true` with the reason. Never indistinguishable from a healthy stop. Log at detection:
  `15:25:58.747 [ERROR] [RecordingService] CAPTURE FAULT at elapsed=67.1s (detected 2026-11-16T20:25:58.747Z) ... capture stalled` =
  **45.38 s after SIGSTOP, 46.04 s after the last growth.**
- **Who killed the frozen child: the product.** `15:25:58.747 [WARN] [FfmpegRecorder] Abandon: ... killing the capture process`,
  then `ended in 17ms (killed, exit 137, last reported position 19.80s)`. My driver only ever sends the
  one signal it is told to; in STOP mode that is SIGSTOP.
- Manifest: `CaptureFault: stalled`, `CaptureFaultDetectedUtc: 2026-11-16T20:25:58.747487Z`, mux `done`.
- **PASS.**

## 3. Criterion 2 - a dead capture ends the recording (SIGKILL)

Run `runs/head-b-ac2-sigkill.txt`, 30 fps, load 4.1. SIGKILL (mine) -> pid 73712 (parent 71634) at
15:27:06.389.

| Clock | Since SIGKILL | `/status` |
|---|---|---|
| 15:26:58.068 | - | `recording`, `growing`, 6600056 |
| 15:27:07.477 | 1.09 s | `recording`, `growing`, 11778284 (child already gone) |
| 15:27:08.565 | **2.18 s** | **`idle`, LastStopFailed true, "capture watchdog: capture exited: the capture process exited (code 137) while recording; raw.mp4 holds 11778284 bytes"** |

- **Expected:** not-`recording` within 60 s and the fault named. **Actual:** detection logged at
  15:27:07.581 = **1.19 s**; `/status` not-`recording` with the fault named by 2.18 s. The product
  killed nothing (the child had already exited). Manifest `CaptureFault: exited`,
  `CaptureFaultDetectedUtc: 2026-11-16T20:27:07.580756Z`. **PASS.**
- **Control, same injection on `main` `277a9bc`** (`runs/main-b-ctl-sigkill.txt`): after SIGKILL at
  15:28:22.850, `/status` said **`recording` with `LastStopFailed: false` for 396 s** until I stopped
  it - the original defect, on this platform, in the same session.

## 4. Criterion 5 - an unclean end still leaves a playable file (amended form)

Amended: a full decode to exit 0, a frame count, and a frame read from near the end. My `verify.sh`
counts frames (`-count_frames`), decodes every stream to null and counts error lines, then decodes one
video frame from the video stream's last second (`-ss <videoDuration-1>`).

| File | Video frames / duration | Audio | Full decode | Frame from last second |
|---|---|---|---|---|
| AC1 raw (`recording.original.mp4`, 10465772 B) | **560 / 18.667 s** | - | **exit 0, 0 error lines** | at 17.667 s: exit 0, 1620902-byte PNG; highest pts 18.633 |
| AC1 final `recording.mp4` | 560 / 18.667 s | 54.43 s | exit 0, 0 lines | at 17.667 s: exit 0 |
| AC2 raw (11778284 B) | **560 / 18.667 s** | - | **exit 0, 0 error lines** | at 17.667 s: exit 0, 1588616-byte PNG; highest pts 18.633 |
| AC2 final `recording.mp4` | 560 / 18.667 s | 17.59 s | exit 0, 0 lines | at 17.667 s: exit 0 |
| **Control, `main`, SIGKILL** | `raw.mp4` 6553648 B: **`moov atom not found`** | - | unreadable | - |

Coverage of the frames before the fault: the product's own log puts the loss at about one fragment -
`stop: abandoned capture - raw.mp4 probes at 18.67s (ffmpeg last reported 19.80s)`. That is the
designed bound ("at most about two seconds"), not a defect. (Both files are 560 frames because both
faults landed about 21.6 s after `/record/start`; the byte sizes differ.)

**PASS** - both faults leave a file that decodes end to end, with the positive control showing the
plain-MP4 outcome the criterion calls a FAIL.

## 5. The start offset on macOS, before and after - the four numbers

`start_time` from `ffprobe`; files in `runs/offsets.txt`. "raw" is the capture file after the mux
renamed it (`recording.original.mp4`). On macOS the raw file holds **video only** - the mic is captured
to a separate `mic_native.wav`, which has no container start time - so the audio figures are the final
file's.

| Build | fps | raw video | final video | final audio |
|---|---|---|---|---|
| **head `fc49b66`** | **30** | **0.000** (edit list present) | **0.000** | 0.000 |
| **head `fc49b66`** | **5** | **0.000** (edit list present) | **0.000** | 0.000 |
| **main `277a9bc`** | **30** | **0.000** (edit list present) | **0.000** | 0.000 |
| **main `277a9bc`** | **5** | **0.000** (edit list present) | **0.000** | 0.000 |
| extra: head minus `+delay_moov` | 30 | **0.0667** (no edit list) | 0.000 | 0.000 |
| extra: head minus `+delay_moov` | 5 | **0.400** (no edit list) | 0.000 | 0.000 |

What this says:

- **The four numbers: 0.000 everywhere, at this head and on main, at 30 and 5 fps.** `+delay_moov`
  restores the edit list on macOS exactly as on Windows.
- **macOS's own magnitude is two frames by COUNT, not the Windows ~0.8 s.** Without the flag the raw
  video starts 0.0667 s late at 30 fps and 0.400 s at 5 fps = exactly 2/fps. The first packets read
  `pts 0.0667 / dts 0.000` (30 fps) and `pts 0.400 / dts 0.000` (5 fps). The mac capture uses
  `-r <fps>`, so timestamps are constant-rate whatever avfoundation delivers.
- **macOS was not carrying a silent offset into the final file, even without the flag.** The final
  `recording.mp4` measured 0.000 / 0.000 in the no-flag build too. My reading of why, NOT measured
  separately: the mac mux takes the video from `raw.mp4` and the audio from a separate WAV
  (`-i raw.mp4 -i audio_processed.wav -map 0:v -c:v copy ...`, quoted from the log), and ffmpeg
  shifts each input by its own start time, so the late video start is cut away. On Windows the audio
  sits inside the same raw file, which would explain why the offset survived there.
- **What a container start time does not tell you**: whether the first video frame and the first mic
  sample were captured at the same wall-clock instant. That is #114's `MicOffsetSeconds` (0.365 s and
  0.371 s in the early-kill manifests) and is not measured here - see section 8.

## 6. The early kill - what macOS does, both ways

SIGKILL (mine) on the screen ffmpeg in the first seconds, through the product. Files in `runs/files.txt`.

| Build | Killed at elapsed | `raw.mp4` | What the product did |
|---|---|---|---|
| **head (with `+delay_moov`)** | 1.11 s | **0 bytes** | mux **failed honestly**: `ffmpeg failed during mux video+mic (exit 183) ... moov atom not found`; `PendingMux` kept; `LastStopFailed` true; no `recording.mp4`; `mic_native.wav` 2.03 s left on disk |
| **head** | 3.10 s | **0 bytes** (ffmpeg had reported media position 1.53 s) | the same: mux failed, `PendingMux` kept, `mic_native.wav` 3.62 s on disk |
| head, positive control | 5.20 s | 1011267 bytes | mux `done`: `recording.mp4` 60 video frames / 2.0 s + 5.57 s audio |
| **before: head minus `+delay_moov`** | 1.05 s | **761 bytes, a header with no frames** (decode exit 183, "Cannot determine format of input 0:0 after EOF") | **muxed into a 1765-byte `recording.mp4` with ONE stream, audio only, 2.42 s, NO video stream - marked `mux: done`**; thumbnail failed |

So macOS shows **the same direction as Windows - an honest failure after, a file falsely marked done
before** - with two macOS-specific differences the lead should have:

1. **The "before" file was not stream-less on macOS: it held the mic audio.** Because the mic is a
   separate capture here, the old behaviour published 2.4 s of audio as a `done` recording that had no
   video in it. The new behaviour publishes nothing; the same audio stays on disk in `mic_native.wav`
   and the manifest keeps `PendingMux`. Nothing is deleted, but **the first seconds of audio are no
   longer in any published recording.** No VIDEO is lost by the flag: a kill before the first fragment
   left zero frames in both builds.
2. **The window is longer on macOS than "about 2 s".** At 3.10 s elapsed the file was still 0 bytes.
   Healthy runs show 0 bytes at 3.2 s and 1.3 MB at 5.27 s, so the first fragment lands **between about
   3 and 5 s** after `/record/start`; avfoundation takes about a second to deliver its first frame. Still
   well inside the watchdog's 45 s start-up window, so only a crash can land there, never a watchdog
   kill.

**My judgement:** not a defect. Publishing an audio-only file as `done` for a video recording is the
false success the lead described; an honest failure is better. Whether a retry of `PendingMux` should
publish the audio alone is a product decision, not this PR's, and I did not measure what the repair
pass does with these two directories.

## 7. Incidental, not filed

- **Audio shorter than video on every run, on `main` too**: 31.76 s audio for 41.13 s video (head),
  23.40 s for 30.47 s (main). Pre-existing, not this PR. The device was the `Microsoft Teams Audio`
  virtual input, not the hardware mic earlier rounds used. #114 territory.
- macOS suite at this head: `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac`
  -> `Build succeeded.` `0 Error(s)`; `dotnet test tests/AgentEyes.Tests.Mac` -> **Failed 0, Passed
  430, Skipped 2, Total 432**, on the .NET 10 runtime (no .NET 8 here). Skipped:
  `OwnedFfmpegTests.KillOwned_FailurePath_...` and `ProcessIdentityTests.PackageCli_SandboxedProber...`.
  CI's macOS figure for `2716e0f` is 431 / 1 skipped, so this Mac skips one test CI runs.

## 8. What this platform could not tell me, and what I did not cover

- **Acoustic and visual A/V sync.** The screen is locked (every frame is the same wallpaper) and the only
  mic is a virtual device. So I cannot show that a sound and an on-screen event line up. Section 5 is
  container timestamps only. The wall-clock alignment of screen vs mic is not measured.
- **Not the shipped startup path.** The code ran as production classes in a harness, loaded into the
  installed `f9ef230` apphost by a startup hook, not as an installed `agenteyes serve`. So
  `RepairService`, the menu-bar app and the installer did not run `00c9df9` here.
- **What a retry of the early-kill directories does.** The repair pass would retry them (`PendingMux`
  kept). I did not run it.
- **Out of scope and not re-run here:** criterion 4 (10-minute run), the gate's two-process scenario,
  the fail-open re-arm, Linux. The evidence for criteria 3, 6 and 7 is in the AC1 / AC2 transcripts and
  log lines, but I did not judge them.
- **One-run figures.** Each fault was injected once at this head. Timings are one sample at the load
  stated.

## 9. What I touched outside scratch

- **Two read-only `GET /status` calls to the owner service on 7882** (about 15:16 and 15:35:07), to
  confirm it was idle. Each wrote two INFO lines into the owner's log. Reported to the lead at once.
  Checked afterwards: my five harness pids (69340, 70669, 71053, 71634, 74253) and my scratch session
  path appear **0 times** in that log, as a pattern that DOES match other seats' scratch paths in the
  same log. No new directory in the owner's library since 14:29. The owner's 15:25 repair pass scanned
  only its own root.
- `com.agenteyes.recorder` was not touched, re-registered, stopped or signalled. No ffmpeg I did not
  start was signalled: every signal was preceded by a parent-pid check against my own harness.
- No recording was deleted. Scratch recordings stay in my scratchpad; only scrubbed text is committed.

## Artefacts (`qa-pass5-macos/`)

- `runs/head-b-ac1-sigstop.txt`, `runs/head-b-ac2-sigkill.txt`, `runs/main-b-ctl-sigkill.txt` - every
  `/status` poll with clock times
- `runs/head-b-log-lines.txt` - CAPTURE FAULT / Abandon / EndFaultedCapture lines at detection
- `runs/offsets.txt` - section 5; `runs/files.txt` - every directory's files, manifest fault fields,
  mux state and the criterion-5 verification
- `runs/*-healthy*.txt`, `runs/*earlykill*.txt`, `runs/*-job.out` (build identity per run)
- `tools/` - `EvidenceHost`, `QaAlcHook`, `job.py`, `drive.py`, `offset.sh`, `verify.sh`

Scratch paths are replaced with `<scratch>` and the home directory with `~`.
