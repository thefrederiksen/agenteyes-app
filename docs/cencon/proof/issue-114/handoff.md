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

---

# ADDENDUM: rebased onto `main` (2026-11-15)

Everything above is the pre-rebase record and is left as it stands. This section is what changed
when the branch was rebased onto `main` after #122, #123, #128 and #136 landed, what was re-run on
the rebased code, and what was carried forward. Where a statement above is no longer true of the
code, it is corrected here rather than edited there.

`main` moved from `f29c8c2` to `1b1b266` under this branch. Rebased head: the branch now contains
`main`'s head (`git merge-base --is-ancestor origin/main HEAD` -> yes).

## What conflicted: exactly one hunk

`src/AgentEyes.Core/Video/FfmpegArgs.cs`, in `MicChain`. #128 round two added three stages to that
method in the same lines this branch was editing - the NaN sanitizer (`FiniteSamples`, D3), the
arnndn framing (`asetnsamples=n=480:p=1`, D3) and the layout pin (`aformat=channel_layouts=...`,
D7) - while this branch was parameterizing the method's SOURCE LABEL so the macOS mux could read
the mic from `[1:a]`.

Everything else merged with no conflict, including all 142 lines of `RecordingService.cs`: the
clock move, the `separateMic` topology branch, the mic writer, the alignment measurement and the two
new mux branches all landed cleanly beside #128's `MuxLock`, its missing-input refusal and #136's
two-pass runner. The `micWav` input this branch adds to `PendingMux` is already covered by #128
round three's pre-flight check, which refuses a mux whose input is missing before any ffmpeg starts.

## The deliberate conflict: #136's shape guard, and how the two builders were split

`tests/Shared/FfmpegMuxShapeTests.cs` enforces that no argument list in this engine pairs a copied
video stream with a filter graph, because that single command deadlocks on macOS. This branch's
`MuxVideoAddMic` and `MuxVideoAddMicAndSystem` were exactly that shape. They are **gone**, split per
the recipe in `docs/cencon/proof/issue-136/handoff.md`:

| was | is now |
|---|---|
| `MuxVideoAddMic(raw, mic, out, o, offset)` | `FilterMicToWav(mic, intermediate, o, offset)` then `MuxVideoWithProcessedAudio(raw, intermediate, out)` |
| `MuxVideoAddMicAndSystem(raw, mic, sys, out, o, offset)` | `MixMicSystemToWav(mic, sys, intermediate, o, offset)` then the same pass 2 |

The guard was NOT weakened, exempted or skipped, and no builder was added to any exemption list -
there is no such list.

**The alignment moved into pass 1 with the mic.** `MicDelayStage` builds the `adelay` for a mic that
started late and `MicInputSeek` builds the `-ss` for a mic that started early; both are applied to
the mic's own pass-1 command. The arithmetic, the sign convention and the millisecond rounding are
unchanged from what is described above.

**Three consequences worth stating, because two of them improve the change:**

1. **`MicChain` no longer takes a source label, and is now byte-identical to `main`'s.** After the
   split there is no video stream in a pass-1 command, so the mic is input 0 in every caller and the
   parameter has nothing left to express. `git diff origin/main..HEAD -- src/AgentEyes.Core/Video/FfmpegArgs.cs`
   does not mention `MicChain` at all. This retires the whole F2 question: there is no longer a
   "which callers rely on the `[0:a]` default" enumeration to get right, because the method the
   enumeration was about is unchanged.
2. **Both macOS muxes now run through `AudioMix.MuxInTwoPasses` rather than `Ffmpeg.Run`.** They
   therefore pick up #128's stall watchdog, its partial-output cleanup, the stale-temp sweep and
   #136's atomic rename into the final path - none of which the pre-rebase branch had, because it
   predates all of it.
3. **What #114 now adds to the two shared pass-1 builders is one optional parameter**,
   `double micOffsetSeconds = 0.0`. At its default both builders emit the list they emitted before
   this branch existed, byte for byte. That is the criterion-8 guarantee restated for the new shape,
   and it is pinned by exact equality rather than argued (below).

## Criteria RE-RUN on the rebased code

Two fresh recordings through the Control API on `devthrottle-mac-mini`, after
`bash scripts/mac/install.sh` from this branch. Both were made with **DEFAULT mic processing**
(`denoise`, `gate` and `level` all on) - not the `denoise:false,gate:false,level:false` scope the
pre-rebase proof runs were honestly limited to. That limit existed because of #128 and #136, and
both are now in `main`, so the shipped configuration is what is measured here.

What was answering: installed plist `~/Library/LaunchAgents/com.agenteyes.recorder.plist` carries
`<key>ProcessType</key><string>Interactive</string>` and `LowPriorityIO false`; `agenteyes.dll`
sha256 `ddb8b6e918bf55db...`, mtime 11:17:16; service **pid 82638**. Every log line quoted below
carries `[pid 82638]`.

| Recording | asked | `/record/stop` reported |
|---|---|---|
| `~/Movies/AgentEyes/2026-11-15_111822_video` | 600 s | `DurationSeconds: 601.16` |
| `~/Movies/AgentEyes/2026-11-15_112903_video` | 60 s | `DurationSeconds: 61.23` |

| # | Expected | 10-minute Actual | 60-second Actual | |
|---|---|---|---|---|
| 1 | `Output #0` reads the requested fps, delivered rate matches (as amended) | `... 1920x1080, q=2-31, 30 fps, 15360 tbn`; 18054 frames / 601.800 s = **30.0000 fps** | same `Output #0` line; 1843 / 61.433 s = **30.0000 fps** | PASS |
| 2 | 10-min audio within 3.0 s of video | video 601.800 s vs audio 601.104 s = **0.696 s**; 28178 x 1024 / 48000 = 601.131 s | - | PASS |
| 3 | 60-s audio within 3.0 s | - | video 61.433 s vs audio 60.830 s = **0.603 s**; 2853 x 1024 / 48000 = 60.864 s | PASS |
| 4 | file within 1.0 s of reported | 601.800 vs 601.16 = **0.640 s** | 61.433 vs 61.23 = **0.203 s** | PASS |
| 5 | frames within 10% of duration x fps | 18054 vs 18054.0 = **0.0000%** | 1843 vs 1843.0 = **0.0000%** | PASS |
| 6 | `audio_16k.wav` within 3.0 s of video | 601.104 s -> **0.696 s** | 60.830 s -> **0.603 s** | PASS |
| 7 | final progress line `speed=` >= 0.95x | `frame=18054 fps= 30 q=-1.0 Lsize= 313198KiB time=00:10:01.73 bitrate=4263.9kbits/s speed=   1x` | `frame= 1843 fps= 30 q=-1.0 Lsize= 28288KiB time=00:01:01.36 bitrate=3776.2kbits/s speed=0.999x` | PASS |
| 8 | Windows and Linux untouched | re-derived for the new shape - see below | | PASS |
| 9 | both build+test pairs | mac 0 Error(s) / **274 passed, 0 failed**; linux 0 Error(s) / **281 passed, 1 skipped, 0 failed** | | PASS |
| 10 | a regression test that fires against the shipped arguments | the alignment pins were rewritten for the split and re-run; the shape guard was proved to FIRE by mutation - see below | | PASS |

`FfmpegCommand` from both manifests (identical apart from the output path):

```
"/opt/homebrew/bin/ffmpeg" -y -f avfoundation -thread_queue_size 1024 -capture_cursor 1
  -pixel_format nv12 -framerate 30 -i 0:none -c:v libx264 -preset veryfast -pix_fmt yuv420p
  -crf 23 -r 30 <dir>/raw.mp4
```

Criterion 1's unreachable half is unchanged and is not being re-claimed: the INPUT line still reads
`Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12, 1920x1080, 1000k tbr, 1000k tbn`. An
AVFoundation screen input reports no rate for the demuxer to estimate, and nothing about the rebase
changes that.

### The alignment, measured and applied - the thing the split had to not break

10-minute run:

```
[RecordingService] Stop: mic/video alignment - video first frame at 245471.173000s,
                        mic first sample at 245471.636625s, offset 0.464s
[AudioCapture] Stop: wrote 57661440 bytes of PCM = 600.64s of audio over a 600.92s capture
```

and the mux it produced, which is the split working end to end on the shipped defaults:

```
pass 1  "ffmpeg" -y -i <dir>/mic_native.wav -filter_complex
        "[0:a]aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same,
          aformat=channel_layouts=mono,asetnsamples=n=480:p=1,
          arnndn=m='.../bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];
         [m]adelay=463.625:all=1,alimiter=limit=0.95:level=false[a]"
        -map [a] -c:a pcm_f32le -rf64 auto <dir>/audio_processed.wav          2.67 s
pass 2  "ffmpeg" -y -i <dir>/raw.mp4 -i <dir>/audio_processed.wav
        -map 0:v -c:v copy -map 1:a -c:a aac -b:a 160k -f mp4 <dir>/recording.mp4.muxing.tmp
                                                                              4.12 s
```

`adelay=463.625:all=1` is #114's measured 0.464 s offset, inside #128's chain, in #136's pass 1,
with no video stream in the command. 60-second run: offset **0.470 s**, `adelay=470.417:all=1`,
`wrote 5793792 bytes of PCM = 60.35s of audio over a 60.59s capture`.

**Post-processing completed on the shipped defaults, first attempt, on both.** `PostProcessing.mux`
= `{"State":"done","Attempts":1}` for each; the intermediate was removed; nothing was left at the
muxing temp. That is the arithmetic the Implementation Lead was holding the merge for: macOS now
keeps its whole recording AND finalizes it. (`package` fails with "Not signed in to DevThrottle" on
both, which is expected on a machine nobody has signed in on and is not this issue.)

### Criterion 8, re-derived rather than carried forward

The pre-rebase argument for criterion 8 was an enumeration of `MicChain`'s callers plus a 52-
invocation cross-tree dump. After the split that argument is the wrong shape, so it was redone:

1. **`MicChain` is byte-identical to `main`'s.** It does not appear in
   `git diff origin/main..HEAD -- src/AgentEyes.Core/Video/FfmpegArgs.cs`. The shared clean-voice
   chain cannot have changed, for any caller, on any platform.
2. **The only shared change left is one optional parameter on two pass-1 builders**, and
   `MacMicAlignmentTests.FilterMicToWav_WithNoOffset_IsTheListItEmittedBeforeIssue114` and
   `MixMicSystemToWav_WithNoOffset_IsTheListItEmittedBeforeIssue114` pin, by exact equality, that
   the no-argument overload and an explicit `0.0` both emit a literal expected list. The literal is
   spelled out in the test rather than composed from the product's own constants, so a pin built the
   way the thing it pins is built cannot silently follow it.
3. **`MixTwoWav` and `FilterSystemToWav`**, the two shared builders this branch does not touch at
   all, are pinned by exact equality in the same file.
4. `tests/AgentEyes.Tests.Linux` is green locally (281 passed) and the Windows suite is green in CI
   (2377 passed). **What this does not cover, plainly:** the two shared RUNTIME changes this branch
   makes - the recording clock (`RecordingService.cs`) and the drain (`FfmpegRecorder.cs`) - shift
   behaviour on Windows and Linux and no argument test can see them. Their only Windows coverage is
   still the green CI job. That was true before the rebase and it is still true.

What was NOT repeated: QA's throwaway harness that compiled against both trees and diffed 52
argument lists. Point 1 makes it unnecessary for the chain - an unchanged method cannot emit a
changed list - but it is a different and weaker instrument than the one QA used, and that is stated
rather than glossed.

### Criterion 10, and the guard proved to fire

`tests/AgentEyes.Tests.Mac/MacMicAlignmentTests.cs` was rewritten for the split: the alignment pins
now assert the pass-1 command (the `adelay` for a late mic, the `-ss` for an early one, the
millisecond rounding, and that the pass carries no video stream and no `copy` at any offset), and
`MuxPass_JoinsThePictureToTheIntermediateWithNoFilterGraph` pins pass 2.

Two fail-closed checks, both of which were run against a known-BAD input and shown to FIRE
(DEVELOPMENT_METHOD 6c item 3):

* `TheSingleCommandMuxBuildersAreGone` refuses either removed builder coming back by name.
* `FfmpegMuxShapeTests.NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` catches the shape
  under any name. **Mutation evidence:** `MuxVideoAddMic` was re-added verbatim and the suite went
  to `Failed: 2, Passed: 272`, the guard naming the builder and printing its whole command line:

```
a copied video stream is back in the same ffmpeg command as a filter graph (issue #136 ...):
  FfmpegArgs.MuxVideoAddMic: -y -i fixture.mp4 -i fixture.mp4 -filter_complex
  [0:a]aeval=...,aformat=channel_layouts=stereo,speechnorm=e=4:p=0.95,volume=1[m];
  [m]adelay=1500:all=1,alimiter=limit=0.95:level=false[a]
  -map 0:v -c:v copy -map [a] -c:a aac -b:a 160k fixture.mp4
```

The mutation was reverted; the committed tree is the one the counts above were measured on. An
EMPTY sweep result is a broken instrument and fails in that test by construction, not by this note.

## Criteria CARRIED FORWARD, and why each is untouched by the conflicts

Nothing in the measured set was carried forward - criteria 1 through 10 were all re-run or
re-derived above. Two things from the pre-rebase record are carried, and neither is a criterion:

* **F3's answer** (`AvfCameraCapture` and `AlwaysOnArgs`). No conflict touched either file:
  `AlwaysOnArgs` is gdigrab and has no macOS path at all, `AvfCameraCapture` is unchanged by this
  branch and by the rebase, and the root cause is a property of the launchd job rather than of any
  argument list, so it is shared by every process the service spawns. Still untestable here
  (`cameras: []`).
* **The root-cause reproduction** (98.9% of a capture kept at normal QoS against 16.8% under
  `taskpolicy -b`). That measurement is about the launchd plist's scheduling class, which no
  conflict touched - `scripts/mac/com.agenteyes.recorder.plist` merged without conflict and the
  installed plist was re-verified above. It is carried, not re-run.

## The gaps, unchanged and still open

* **The three-input mixed-audio path is still not exercised end to end.** `POST /record/start` with
  `"source":"mixed"` is refused outright on this machine - `/devices` reports
  `systemAudio: (unavailable: none of the audio inputs on this Mac is a loopback device ...)`. The
  split applies to it (`MixMicSystemToWav` + pass 2) and its argument list is pinned by
  `MicPlusSystemPass_MicIsInputZero_SystemIsInputOne` and
  `MicPlusSystemPass_AlignsTheMicTheSameWayTheMicOnlyPassDoes`, but the running capture is
  unverified and must not be read as verified. Installing a loopback device needs the person at the
  keyboard.
* **Criterion 1 remains partly met**, exactly as amended, for the reason quoted above.
* **One limit of these two runs, stated because it bears on criterion 7.** The Mac's screen was
  LOCKED throughout (`CGSSessionScreenIsLocked = Yes`), so the picture is static wallpaper and the
  x264 encode is easier than a real desktop's. `speed=1x` / `speed=0.999x` is therefore a weaker
  stress than the pre-rebase runs, which measured the same figures on a live desktop while four
  cores of this ten-core machine were pinned by orphaned muxes. The frame-rate, duration and audio-
  length criteria do not depend on the content; criterion 7 does.

## Machine state left behind, since the anomaly the Lead recorded on 2026-11-14 is now resolved

The installed service no longer disagrees with any commit: it was reinstalled from this branch, so
the binary and the `ProcessType Interactive` plist now come from the same tree. New service pid
82638 (the old one was 90675, so the restart is proven by a new pid rather than assumed).

One orphan was cleared. `ffmpeg` pid 78054 - the OLD build's single-pass mux of
`2026-11-15_093623_video`, the exact shape #136 fixes - had been spinning at 100% CPU for 10 minutes
and survived the service restart reparented to pid 1, which is QA's F4 finding reproducing itself. It
was killed and nothing respawned. **The new build then completed that same mux in 0.2 s on startup**
(`PostProcessing.mux` `State: done, Attempts: 3` - the old build had burned two), which is an
independent corroboration of #136 on a recording nobody made for the purpose. Nothing under
`~/Movies/AgentEyes` was deleted.

## Gate on the rebased head

`AgentEyes.sln` contains the WPF app and cannot build on a Mac, so the Windows gate commands are not
runnable here; CI covers that suite.

| Command | Result |
|---------|--------|
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac` | **0 Error(s)**, 1 pre-existing warning (`AlwaysOnEngine.cs:901` CS8602) |
| `dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release` | **274 passed, 0 failed, 0 skipped** |
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux` | **0 Error(s)**, same 1 warning |
| `dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release` | **281 passed, 0 failed, 1 skipped** (`MachineLocalAppData_OnLinux_IsTheXdgDataHome`) |

The counts are now `main`'s counts plus this branch's tests, not the pre-rebase 105/94 - this branch
is rebased, so it carries `tests/Shared` and everything #122, #128 and #136 added.

Note for whoever measures next: running that gate rewrites the macOS app output as a Linux one
(`tests/AgentEyes.Tests.Linux` pins `AgentEyesUnixPlatform=linux` and shares `bin/Release/net8.0`),
so rebuild the desktop app before launching or inspecting it on a Mac. The two recordings above were
made by the INSTALLED service from `scripts/mac/install.sh`, which publishes separately, so they are
unaffected.

CI on the rebased head, all three jobs, on more than one run - run ids and counts are in the issue
comment that accompanies this addendum.
