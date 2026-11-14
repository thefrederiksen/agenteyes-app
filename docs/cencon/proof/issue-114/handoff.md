# Issue #114 - Developer handoff

macOS: a screen+mic recording keeps about 12% of its audio and runs at 13 fps instead of 30.

Branch: `issue-114-macos-audio-loss-and-frame-rate`. I believe this is finished, with two
exceptions stated plainly below: acceptance criterion 1 is PARTLY met and cannot be fully met,
and a PRE-EXISTING defect that my fix un-masks now blocks the DEFAULT mic settings. Read
"What I could not make true" before verifying.

---

## The leading hypothesis in the issue is WRONG, and here is what the cause actually is

The issue flagged its hypothesis as an assumption and asked for it to be tested first. It was,
and it does not hold. For the record, because the wrong answer is instructive:

| The issue's claim | What I measured |
|---|---|
| `-framerate 30` was "not accepted" by the device | It IS accepted. Ask for 10 fps and you get **exactly 10.04 fps**. The "Configuration of video device failed, falling back to default" line is `configure_video_device` refusing an `AVCaptureScreenInput`, which is not an `AVCaptureDevice`; the screen input's frame rate is set separately and works. |
| The encoder could not keep up, and a slow encoder dropped the audio | Not the encoder. With a 320x240 output (a nearly free encode, `speed=0.997x`) the capture STILL ran at 12.66 fps and STILL lost 85% of the audio. |
| The machine was being driven too hard | Not the CPU. During a lossy screen+mic capture: **87.92% CPU idle**, ffmpeg using half a core of ten. |
| `-pix_fmt yuv420p` was being applied to the input | Nothing of ours was. `-pixel_format`'s own default IN THE AVFOUNDATION DEMUXER is `yuv420p` (`ffmpeg -h demuxer=avfoundation`), which no display supports, so every capture logged it. |

### The actual root cause: macOS was running the recorder as throttled background work

The recorder is a launchd user agent. Its plist set no `ProcessType`, so macOS treated it as
background work - deprioritized CPU, deprioritized I/O, coalesced timers. ffmpeg's AVFoundation
demuxer holds **exactly one sample buffer per device** and silently discards whatever arrives
before the reader takes it. Throttle the reader and the samples are simply gone, with nothing
logged anywhere.

The same ffmpeg command line, same machine, same device, run twice, differing only in QoS:

```
normal QoS                      -> 1,920,000 bytes = 20.00 s of a 20 s capture.  No loss.
background QoS (taskpolicy -b)  ->   319,392 bytes =  3.33 s of a 20 s capture.  83% GONE.
```

That second number is this issue: a recording that keeps a small fraction of its audio, and a
screen capture that runs at a fraction of its frame rate.

**A correction I owe the record:** on the way to this I concluded the loss was coupling between
two AVFoundation inputs inside one ffmpeg process, because a mic captured by a SEPARATE ffmpeg
during the same screen recording kept every sample. That experiment was confounded - the sidecar
ran from a shell, at normal QoS. It was the QoS that differed, not the process boundary. I have
kept the separate-process topology anyway, on its own merits (below), but it is not the fix for
the loss and must not be read as one.

---

## What changed

### 1. `scripts/mac/com.agenteyes.recorder.plist` - `ProcessType Interactive` (THE FIX)

Plus `LowPriorityIO false`: the recorder writes a video stream to disk while it captures.
Interactive is the honest classification, not a tuning preference - an always-on recorder is
latency-sensitive work whose output is WRONG, not merely late, when it is descheduled.

### 2. `FfmpegArgs.Mac.cs` - the microphone is no longer an input of the screen capture

`AvfVideoCapture` has **no microphone parameter at all** now, by construction, and
`FfmpegArgs.AvfAudioInput` is deleted. The mic is captured by its own ffmpeg (`AvfStream`, which
is what the level meter already ran) into `mic_native.wav`, and the deferred mux puts it into the
recording - the same shape the system-audio path has always had.

Why keep this if QoS was the cause? Two independent reasons, both measured:
* A single AVFoundation input per process is the topology that survives; the one-sample-buffer
  demuxer has one fewer way to drop things, and the mic's own ffmpeg now carries its own deep
  input queue (item 4).
* It is what makes the loss VISIBLE. `mic_native.wav` is a file on disk whose length can be
  compared with the take, which is how item 5's warning works. Audio buried in the video file
  cannot be checked that way.

Also in this list: `-pixel_format nv12` (4:2:0 already, so the per-frame conversion to the
encoder's `yuv420p` becomes a plane split instead of a resample, and the misleading log lines go
away - a display that does not offer nv12 is overridden exactly as before), and `-r <fps>` on
the output. See "What I could not make true" for what `-r` does and does NOT do.

### 3. `FfmpegRecorder` - the drain compared two different clocks

`Stop()` waited for ffmpeg's MEDIA time to reach OUR stopwatch's elapsed time. Those zeros are
not the same instant: the stopwatch starts when the object is constructed, media time 0 is the
first frame the device delivered. The target was therefore always past the end of the take, the
drain ran to its full 5 s timeout EVERY time with ffmpeg still capturing, and that is most of why
a 30 s ask came back as 35.3 s. It now tracks where media time 0 sits on our clock (the smallest
observed `wall - media`, since reported media time always trails) and waits for that position.

Measured: drain went from **2724 ms (hit the timeout)** to **63-229 ms**.

### 4. `AvfStream` - a deep input queue, and the capture clock

* `-thread_queue_size 1024` on the mic input. The screen capture has carried this guard from the
  start; this input never had it. It keeps the demuxer draining the device while this end of the
  pipe is busy. Measured with the service transcribing its backlog: a 60.05 s capture kept
  25.15 s without it.
* `-loglevel info` (was `warning`) with `-nostats`. ffmpeg prints the input dump - the line
  carrying this capture's start time on the machine's shared capture clock - at info level, and
  that timestamp is the only thing that can align this WAV with a screen recording made by a
  different ffmpeg process. `-nostats` keeps the cost to one fixed ~15-line block.
* The mic ffmpeg's own closing report is now logged, so "the device gave us less" can be told
  apart from "we read less".

### 5. `AudioCapture` (macOS) - a check that fails closed

Stop used to log a byte count, which cannot be wrong and so never reported anything: the capture
that kept 12% and the one that kept everything wrote the same shape of line. It now states the
arithmetic and compares it with the wall clock, and WARNS loudly when more than a second is
missing. A near-silent recording that nothing complains about is the defect this issue is about.

### 6. `RecordingService` - the recording's clock, the mic writer, and the alignment

* **The clock starts when the recording starts.** `_sw.Restart()` was after `StartSession`, i.e.
  after every writer was up and warmed - one to two seconds after capture began. The files came
  out longer than the duration reported for them by exactly that setup, with nothing wrong with
  either number except that they were measured from different instants.
* **The mic writer is REQUIRED on macOS, not auxiliary.** Where the screen recorder captures the
  mic itself this step is a monitor-only level meter whose failure is logged and tolerated. On
  macOS it is the recording's only microphone, so swallowing its failure would hand back a silent
  video and call it a success. It is a required step there and a failure fails the start.
* **The alignment is measured, not assumed.** `micStart - videoStart` from the capture clock both
  ffmpegs report, logged on every recording and stored in the manifest as
  `PendingMux.MicOffsetSeconds`. Measured on the proof runs: **0.465 s to 0.564 s**.
* `CaptureTopology` (new, `src/AgentEyes.Core/Video/CaptureTopology.cs`) names the one platform
  difference in one place, so `RecordingService` stays free of platform conditionals - it has no
  `#if` in it and now still has none.
* **Mixed audio on macOS was going to break, and does not.** Also found in my own review:
  `separateMic` covers Mixed as well as Mic, and the existing `MuxVideoMixed` mixes the system WAV
  with the mic track INSIDE the video - which a macOS `raw.mp4` no longer has, so the mux would
  have failed. There is now a three-input form (`MuxVideoAddMicAndSystem` /
  `MuxVideoMicAndSystemWavs`) and `FinalizePending` picks it when the mic is a separate file.
  **Its argument list is unit-tested but it is NOT exercised end to end**: macOS cannot record
  what it is playing without a virtual audio device, this machine has none, and so a mixed
  recording cannot be started on it at all. Flagged rather than claimed.

### 7. Asking the macOS screen recorder for a microphone is REFUSED, not ignored

`FfmpegRecorder.Start` throws a `UsageException` naming the fix when a microphone is passed on
macOS. Found in my own review of this diff: `RecordingService` is not the only caller. The CLI's
video recording (`Commands.cs`) and the headless selftest also pass one, and with the microphone
quietly dropped from the capture they would have written a SILENT video and reported success -
which is a worse version of the defect this issue is about. Neither can capture a macOS screen
today anyway (TCC grants the permission to the launchd agent, not to a shell), so they now fail
with a message that says what to do instead; teaching them the separate-writer topology is their
own work and not this issue's.

### 8. `FfmpegArgs.MuxVideoAddMic` + `AudioMix.MuxVideoMicWav` - the mux

New methods; no existing builder's emitted arguments changed. `MicChain` gained a source-label
parameter whose DEFAULT is `[0:a]`, so every existing caller emits exactly what it emitted
before. The offset is applied with ComposeArgs.CameraInset's sign convention: positive means the
mic started late, so pad its head (`adelay`); negative means it was already running, so seek that
head off the input (`-ss`). The gate is calibrated from the WAV, which IS the mic capture here.

---

## What I could not make true, and you should not pass it as if I did

### Acceptance criterion 1 is PARTLY met

> "the input stream line reads `30 tbr` ... and the `Output #0` video line reads `30 fps`"

* `Output #0` reads **`30 fps`**. MET.
* The input line still reads **`1000k tbr`**. NOT MET, and not achievable with this ffmpeg.
  `tbr` is the demuxer's estimate of the input's rate, an AVFoundation screen input reports no
  rate of its own ("not enough frames to estimate rate"), and `avfoundation` is the ONLY screen
  backend this ffmpeg has (`ffmpeg -devices`). There is nothing to estimate and no option that
  supplies it. I did not chase the criterion by faking the number.

### Criterion 5 passes, but 30 fps of UNIQUE frames is NOT what the capture delivers

The output is now constant-rate at the rate asked for, which is what criterion 5 measures and
what the gdigrab and x11grab twins already produce - and it fixes a real malformation: without
`-r` the mp4 carried `1000k fps, 1000k tbn`, which x264 sized the file's level from and said so
(`MB rate (8160000000) > level limit (16711680)`, stamping level 6.2 on 1080p30 footage).

It does not create frames. Measured ceiling of AVFoundation screen delivery on this machine,
before the QoS fix:

| asked | delivered |
|---|---|
| 10 fps | 10.04 fps |
| 15 fps | 12.18 fps |
| 20 fps | 12.51 fps |
| 30 fps | 13.24 fps |
| 60 fps | 13.99 fps |

`-capture_cursor 0`, `-pixel_format nv12` and `-drop_late_frames false` were each measured and
each changed nothing. **After the QoS fix the 60 s proof runs deliver a true 30.00 fps**
(1858 frames in 61.93 s), so on this machine the ceiling was the throttling. Whether a busier
machine still hits a delivery ceiling is not something one Mac can tell you - if QA sees fewer
unique frames than 30/s under load, that is worth its own issue, not a re-open of this one.

### A PRE-EXISTING defect is now un-masked, and it BLOCKS the default mic settings

With default mic processing on (`denoise`/`level` true, the API default), the mux **hangs
forever**: ffmpeg consumes all input, reaches the end, and spins at ~99% CPU without finalizing.
Observed on recording `2026-11-14_101820_video` (mux "running" for 3m23s, output frozen at
exactly 25 MiB).

It is NOT caused by this change, and I proved that rather than assuming it:

```
[0:a]arnndn,speechnorm,volume[m];[m]alimiter[a]  on a file with the mic INSIDE it,
i.e. the shape the pre-existing FilterVideoMic already emits   -> STALLED
```

Bisected: `arnndn` alone ok (2 s). `speechnorm` alone ok. `alimiter` alone ok. `volume` alone ok.
`arnndn` + `speechnorm` **together** stall, on ~60 s of audio. It is shared code
(`FfmpegArgs.MicChain`) on all three platforms, and this issue's scope says Windows and Linux
emitted arguments must stay byte-identical, so I did not touch it. **It needs its own issue.**

Why nobody saw it before: the chain's stall is duration-dependent, and until this fix a 60 s
macOS recording contained about 4 s of audio. Fixing the loss is what made the input long enough
to hit it.

**So the proof runs below were made with `"denoise":false,"gate":false,"level":false`.** That is
the honest scope of what is verified end-to-end.

---

## How QA should exercise it

Gate on this machine - `AgentEyes.sln` contains the WPF app and CANNOT build on a Mac, so
`dotnet build AgentEyes.sln` / `dotnet test AgentEyes.sln` are Windows-only commands here:

```bash
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROLL_FORWARD=LatestMajor
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac
dotnet test  tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux
dotnet test  tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
```

All four green as of this handoff: mac build `0 Warning(s) 0 Error(s)`, **mac tests 103 passed,
0 failed**; linux build `1 Warning(s)` (the pre-existing `AlwaysOnEngine.cs:901` nullable warning
named in the issue) `0 Error(s)`, **linux tests 94 passed, 1 skipped, 0 failed**.

**Install before driving anything** - the plist change is a large part of the fix and only takes
effect on reinstall:

```bash
bash scripts/mac/install.sh
grep -A1 ProcessType ~/Library/LaunchAgents/com.agenteyes.recorder.plist   # must say Interactive
curl -s http://127.0.0.1:7882/health
```

Then drive every real capture through the Control API - only the launchd agent holds the Screen
Recording grant, a capture launched from a shell is refused (it HANGS rather than erroring):

```bash
API=http://127.0.0.1:7882
curl -s -X POST $API/record/start -H 'Content-Type: application/json' \
  -d '{"mode":"video","screen":1,"source":"mic","mic":"Yealink UH36","denoise":false,"gate":false,"level":false}'
# ... wait ...
curl -s -X POST $API/record/stop -H 'Content-Type: application/json' -d '{}'   # the body is required
```

### Per criterion

| # | Criterion | How to check it |
|---|---|---|
| 1 | device accepts the rate | `grep -E "Stream #0:0: Video\|Output #0" raw.mp4.ffmpeg.log`. Output must read `30 fps`. The input will read `1000k tbr` - see above; judge this one against that limit, not against the issue text. |
| 2 | audio spans a 10-minute recording | `ffprobe` audio vs video duration of `recording.mp4`, within 3.0 s. |
| 3 | same on 60 s | as above. |
| 4 | file length vs reported | `ffprobe` duration of `recording.mp4` vs the `DurationSeconds` in the `/record/stop` RESPONSE (not the manifest - the mux overwrites the manifest's value with the probed one), within 1.0 s. |
| 5 | frame count vs rate | `ffprobe -count_packets` video packets vs duration x 30, within 10%. |
| 6 | `audio_16k.wav` spans it | `ffprobe` its duration vs the video's, within 3.0 s. |
| 7 | encoder keeps up | last `frame=` line of the log, `speed=` at least 0.95x. |
| 8 | Windows and Linux untouched | `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs` pins the exact x11grab+pulse command line INCLUDING its mic input, and adds `CaptureTopology_OnLinux_TheScreenRecorderCapturesTheMicItself`. `tests/AgentEyes.Tests.Mac/MacMicAlignmentTests.cs` pins `FilterVideoMic`, `MixTwoWav` and `MuxVideoMixMicSystem` byte-for-byte, which are (verified by grep) exactly the three pre-existing callers that rely on `MicChain`'s `[0:a]` default; the only other callers are the two macOS builders this issue added, which pass `[1:a]` explicitly. It also pins `MuxVideoAddSystem`, which is NOT a `MicChain` caller - an earlier version of this row named it as one and called the two pins "every other caller", and both claims were wrong (QA finding F2, corrected on this branch; the `MixTwoWav` and `MuxVideoMixMicSystem` pins were added at the same time and passed first time against the unchanged builders). That suite runs on a Mac, so it is checked on the machine the change was made on. The Windows suite `tests/AgentEyes.Tests/` cannot run on a Mac; CI runs it on the PR. |
| 9 | gate | the four commands above. |
| 10 | regression test | `MacFfmpegArgsTests.AvfVideoCapture_NeverCapturesTheMicrophone_WhateverTheScreen` fails against the argument list that shipped (it appended an avfoundation audio input plus `-c:a aac -b:a 128k`), and `AvfStreamArguments_GiveTheDeviceADeepInputQueue` fails against the mic arguments that shipped. Both assert a PRESENCE first - that the list really is an AVFoundation capture - so an empty or throwing builder fails instead of passing for free. |

Smokes: `api-smoke.ps1` / `gui-smoke.ps1` are PowerShell and drive the WPF app; neither applies on
this machine. The area this change touches is the macOS capture path, exercised through the
Control API as above.

### What is worth looking at hardest

1. **Install first.** Without the reinstall the plist fix is not live and everything still loses
   audio. Check `ProcessType` is in the INSTALLED plist, not just the repo one.
2. **Read the `AudioCapture] Stop:` log line** on every recording you make. It states captured
   seconds vs capture seconds. If it WARNS, the capture lost samples and no probe of the final
   file will tell you as clearly.
3. **The default-settings hang.** Make one recording WITHOUT `denoise/gate/level` false and
   confirm the mux hangs, so the follow-up issue has a second observation. Do not let that hang
   be recorded as a defect in this change - the pre-existing-shape test above shows it is not.

---

## Proof runs I made myself

Two 60 s screen+mic recordings through the Control API on `devthrottle-mac-mini`, plus a
10-minute one. Numbers, not "it should now work":

A **10-minute** and a **60-second** screen+mic recording, both through the Control API on
`devthrottle-mac-mini`. Every number, and the command lines and log lines behind them, are in
`docs/cencon/proof/issue-114/ten-minute-run.md`. The summary:

**10 minutes** (`2026-11-14_103623_video`), asked 600 s, reported `DurationSeconds: 609.49`:

| Criterion | Expected | Actual | |
|---|---|---|---|
| 2 audio spans video | within 3.0 s | video 609.90 s, audio 609.13 s, **diff 0.77 s** | PASS |
| | | 28555 AAC frames x 1024 / 48000 = 609.17 s of samples | |
| 4 file vs reported | within 1.0 s | 609.90 s vs 609.49 s, **diff 0.41 s** | PASS |
| 5 frame count | 18297 +-10% | **18297 frames, 609.90 s, 30.00 fps** (`dup=4 drop=0`) | PASS |
| 6 `audio_16k.wav` | within 3.0 s | 609.13 s vs 609.90 s, **diff 0.77 s** | PASS |
| 7 encoder keeps up | >= 0.95x | **speed=1x** | PASS |

`dup=4 drop=0` over 18297 frames is the number that matters most here: the constant-rate output
repeated four frames in ten minutes, so this is a real 30 fps capture, not 13 fps padded up.

**60 seconds** (`2026-11-14_104905_video`), made with the FINAL binary of this branch, reported
`DurationSeconds: 61.36`:

| Criterion | Expected | Actual | |
|---|---|---|---|
| 3 audio spans video | within 3.0 s | video 61.77 s, audio 60.75 s, **diff 1.02 s** | PASS |
| | | 2849 AAC frames x 1024 / 48000 = 60.78 s of samples | |
| 4 file vs reported | within 1.0 s | 61.77 s vs 61.36 s, **diff 0.41 s** | PASS |
| 5 frame count | 1853 +-10% | **1853 frames, 61.77 s, 30.00 fps** | PASS |
| 6 `audio_16k.wav` | within 3.0 s | 60.75 s vs 61.77 s, **diff 1.02 s** | PASS |
| 7 encoder keeps up | >= 0.95x | **speed=0.999x** | PASS |

Mic capture, 10-minute run: `wrote 58432512 bytes of PCM = 608.67s of audio over a 609.10s
capture`. Alignment: `video first frame at 156552.689000s, mic first sample at 156553.149063s,
offset 0.460s`. Drain: `pipeline drained in 86ms` (was 2724 ms, hitting the 5 s timeout).

Before, from the issue: 30 s asked -> 35.30 s of video, 458 frames (12.97 fps), **4.03 s of
audio**, ffmpeg's own audio total **37 KiB**. Now, 600 s asked -> 609.90 s of video, 18297 frames
(30.00 fps), **609.17 s of audio**, 57063 KiB at the full 767.8 kbit/s.

Evidence recordings kept: `2026-11-14_103623_video` (10 minutes), `2026-11-14_104905_video`
(60 s, final binary), `2026-11-14_101820_video` (the default-settings mux hang),
`2026-11-14_100913_video` (a lossy capture under load, 25.15 s of 60.05 s, before the plist fix
was installed).

## CenCon impact

No drift. The component map is unchanged - no project gained or lost a responsibility, and the
macOS microphone moving into its own process is inside `AgentEyes.Core`. The privacy posture is
untouched: nothing was added or removed from what is captured, no new surface records anything,
and the visible/controllable stance is unaffected. `ProcessType Interactive` changes how macOS
schedules the recorder, not what it records.

## Risk

Medium, and concentrated in two places.

* `RecordingService` and `FfmpegRecorder` are SHARED with Windows and Linux. Two changes there
  affect all three: the drain now waits for the media position that corresponds to "now" instead
  of assuming the clocks share a zero, and the recording's clock starts before the writers rather
  than after. On Windows and Linux the media origin is a few hundred milliseconds, so both are
  small there - but they are not zero, and reported durations will shift by about that much.
  The Windows suite cannot run on this machine; CI must be green on the PR before this merges.
* The macOS mic is now a separate writer whose failure fails the start. That is deliberate (a
  silent video reported as a success is the defect), but it does mean a Mac whose microphone
  cannot be opened can no longer record screen+mic at all, where before it recorded silently.

## Follow-up pass: QA findings F1 and F2 applied (comments only)

A short pass on this branch after the review gate, applying the two findings QA raised. No
behaviour change: every line changed under `src/` is a comment line, verified by diffing with
comment lines filtered out, and the gate was re-run after.

**F1 - the disproved cause was still stated as fact in the source.** Corrected in three places.
`Platform/Mac/FfmpegArgs.Mac.cs` (`AvfVideoCapture`) now states the measured cause - no
`ProcessType` on the launchd agent, so macOS ran the recorder as throttled background work, and
ffmpeg's AVFoundation demuxer holds exactly one sample buffer per device and silently discards
whatever arrives before the reader takes it - with both QoS measurements and the explicit statement
that `ProcessType Interactive` is load-bearing and not removable tuning, including QA's result that
a deep input queue does not rescue a throttled capture. The retracted process-coupling hypothesis is
kept but labelled as the confounded experiment it was (the sidecar ran from a shell at normal QoS),
and the two independently valid reasons for the topology are kept as the reasons it stands: one
AVFoundation input per process has one fewer way to drop samples, and a mic WAV on disk makes the
loss visible and measurable against the take.

`Video/CaptureTopology.cs` carried the same wrong cause ("two AVFoundation inputs in one process ...
lose about 88%") and now points at the QoS cause and the plist line instead. `Video/FfmpegRecorder.cs`
(the macOS branch, ~line 110) asserted it a third time, in product code, and was corrected the same
way - QA named two sites; the third is the same defect and was found by grepping for the claim.
Two comments in the Mac test suite that repeated it were corrected too.

**F2 - "every other caller of `MicChain`" was wrong.** The caller list was re-derived by grep rather
than taken from the finding, and the finding is right. `MicChain` has five callers:
`MixTwoWav` (`FfmpegArgs.cs:411`), `MuxVideoMixMicSystem` (`:426`) and `FilterVideoMic` (`:443`) rely
on the `[0:a]` default; `MuxVideoAddMic` (`:486`) and `MuxVideoAddMicAndSystem` (`:523`) are this
issue's own macOS builders and pass `[1:a]` explicitly. `MuxVideoAddSystem` is NOT a caller - it has
no mic stage at all. The comment in `MacMicAlignmentTests.cs` and the criterion-8 row above now say
that, and record what the old sentence got wrong.

The gap the wrong sentence was claiming is also closed, since it was cheap: `MixTwoWav` and
`MuxVideoMixMicSystem` now have exact-equality pins in `MacMicAlignmentTests.cs`, in the same style
as the existing `FilterVideoMic` pin. All three default-source callers are now pinned byte-for-byte
on the machine the change was made on, so the criterion-8 enumeration is exhaustive rather than
nearly so. **Both new pins passed first time against the unchanged builders**, which is the
independent check that this pass changed no emitted argument.

### Gate re-run after this pass

`AgentEyes.sln` contains the WPF app and cannot build on a Mac, so the Windows gate commands are not
runnable here; CI covers that suite on the PR.

| Command | Result |
|---------|--------|
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac` | 0 Error(s), 1 pre-existing warning (`AlwaysOnEngine.cs:901` CS8602) |
| `dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release` | **105 passed**, 0 failed, 0 skipped (103 at branch head + the 2 new pins) |
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux` | 0 Error(s), same 1 warning |
| `dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release` | **94 passed**, 0 failed, 1 skipped (`MachineLocalAppData_OnLinux_IsTheXdgDataHome`) |

The Linux count is the branch-head count, not main's 268: this branch has deliberately not been
rebased, because the merge order is #128 first, then this rebased onto it.

F3 needed no code change - QA answered it in the report and the gate accepted that answer.
