# Issue #114 - QA report

**Verdict: PASS.** All ten acceptance criteria verified, criterion 1 judged as amended by the
Implementation Lead. Two gaps are stated rather than passed over, and four findings are recorded
for the review gate. The merge is held by #128; that is the lead's decision and nothing in this
pass changes it in either direction.

| | |
|---|---|
| Issue | #114, PR #127, branch `issue-114-macos-audio-loss-and-frame-rate` |
| Head verified | `4b77ff698bc5c0c313bad043d4d545cf59643ea4` (working tree clean at that commit) |
| Machine | `devthrottle-mac-mini`, macOS 26.5, arm64, ffmpeg 9.0.2 (Homebrew), one 1920x1080 display |
| Date | 2026-11-14 |

---

## 0. What was actually answering - the build tied to every number below

The plist is a large part of the fix and only takes effect on reinstall, so the install was done
first and the running service was identified before anything was driven.

```
bash scripts/mac/install.sh

grep -A1 -E "ProcessType|LowPriorityIO" ~/Library/LaunchAgents/com.agenteyes.recorder.plist
    <key>ProcessType</key>
    <string>Interactive</string>
    <key>LowPriorityIO</key>
    <false/>

curl -s http://127.0.0.1:7882/health
    {"ok": true, "app": "AgentEyes", "host": "headless", "platform": "macos"}
```

The INSTALLED plist carries `ProcessType Interactive`, not just the repo copy.

Installed binary and the process serving the API:

```
sha256  agenteyes.dll  2268539548251fb151b2c377a53e60480ff3f959ed820cf34de238e81fcfc4a1
                       mtime 2026-11-14 11:01:47
sha256  agenteyes      348b93b81d867fc34529fc073bf93c0d23d9428923dbe65f3a5e36a1d2607022
                       mtime 2026-11-14 11:01:48   (the apphost, unchanged by a rebuild)

ps -o pid,lstart,command -p 65721
  65721  Sat Nov 14 11:01:58 2026  .../AgentEyes/bin/agenteyes serve

launchctl print gui/501/com.agenteyes.recorder    state = running, pid = 65721
```

The service was restarted by the installer ten seconds after the managed assembly was written.
**Every log line quoted in this report carries `[pid 65721]`**, so it came from that build.

Devices, for the record (`GET /devices`): one display 1920x1080; mics `Yealink UH36` (avfoundation
`:0`) and `Microsoft Teams Audio`; `cameras: []`; system audio **unavailable** - "none of the audio
inputs on this Mac is a loopback device that carries the system output".

---

## 1. Criterion 9 - the gate, run by QA

`AgentEyes.sln` contains the WPF app and cannot build on a Mac, so the solution-wide commands in
the repository's project instructions are Windows-only here. The four macOS/Linux commands were run:

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac
    Build succeeded.  1 Warning(s)  0 Error(s)      (CS8602 AlwaysOnEngine.cs:901, pre-existing)
dotnet test  tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release
    Passed!  - Failed: 0, Passed: 103, Skipped: 0, Total: 103, Duration: 37 ms

dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux
    Build succeeded.  1 Warning(s)  0 Error(s)      (the same pre-existing warning)
dotnet test  tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
    Passed!  - Failed: 0, Passed: 94, Skipped: 1, Total: 95, Duration: 72 ms
```

The developer reported mac 103 passed and linux 94 passed / 1 skipped. **Both reproduce exactly.**
The one skip is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, skipped because this
is not Linux.

CI on PR #127, all three jobs against head `4b77ff69`:

```
gh api repos/thefrederiksen/agenteyes-app/commits/4b77ff6/check-runs
    linux   success   4b77ff69
    macos   success   4b77ff69
    windows success   4b77ff69
```

**Criterion 9: PASS.**

---

## 2. The before state, re-measured by QA

Not taken from the issue text. Measured here from the kept evidence recording
`~/Movies/AgentEyes/2026-11-14_085412_video`:

```
recording.mp4           video 35.300001 s, 458 packets  -> 458 / 35.300001 = 12.97 fps
                        audio 48000 Hz, 188 AAC packets -> 188 x 1024 / 48000 = 4.011 s
recording.original.mp4  audio 16000 Hz,  63 AAC packets ->  63 x 1024 / 16000 = 4.032 s
audio_16k.wav           3.970 s
```

12.97 fps and 4.0 s of audio in a 35.3 s video, reproduced independently. Note that the audio
STREAM's container duration in that file reads 34.53 s while only 4.01 s of samples exist - which is
exactly why this defect was invisible. Every audio number below is therefore given BOTH ways: the
container duration and the AAC-frame arithmetic.

---

## 3. The two headline runs

Both driven through the Control API (the only way a real screen capture happens on this machine),
both with `"denoise":false,"gate":false,"level":false` - the honest scope, because of #128.

Request body, identical for both:

```json
{"mode":"video","screen":1,"source":"mic","mic":"Yealink UH36",
 "denoise":false,"gate":false,"level":false}
```

### 3.1 The 10-minute run - `~/Movies/AgentEyes/2026-11-14_110301_video`

Started 11:03:00, stopped 11:13:02 (600 s of wall clock between the two curl calls).

`/record/stop` response:

```json
{"Dir":"/Users/soren/Movies/AgentEyes/2026-11-14_110301_video",
 "File":".../recording.mp4","DurationSeconds":601.24,"Shots":0}
```

`ffprobe -count_packets`:

```
recording.mp4           video h264 30/1   601.900000 s   18057 packets
                        audio aac 48000 Hz 1 ch  600.868938 s   28167 packets
recording.original.mp4  video h264 30/1   601.900000 s   18057 packets   (video only, by design)
mic_native.wav          pcm_s16le 48000 Hz 1 ch  600.384000 s
audio_16k.wav           pcm_s16le 16000 Hz       600.868938 s
```

Arithmetic:

```
frame rate     18057 / 601.900000           = 30.000 fps
expected       601.900000 x 30              = 18057.0 frames  -> deviation 0.000 %
AAC to seconds 28167 x 1024 / 48000         = 600.896 s
audio vs video 601.900 - 600.869            = 1.031 s
file vs report 601.900 - 601.24             = 0.660 s
16k vs video   601.900 - 600.869            = 1.031 s
```

`FfmpegCommand` from `manifest.json`:

```
"/opt/homebrew/bin/ffmpeg" -y -f avfoundation -thread_queue_size 1024 -capture_cursor 1
  -pixel_format nv12 -framerate 30 -i 0:none -c:v libx264 -preset veryfast -pix_fmt yuv420p
  -crf 23 -r 30 .../raw.mp4
```

From `raw.mp4.ffmpeg.log`:

```
  Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12, 1920x1080, 1000k tbr, 1000k tbn, start 158149.872500
Output #0, mp4, to '.../raw.mp4':
  Stream #0:0: Video: h264 (avc1 / 0x31637661), yuv420p(tv, progressive), 1920x1080, q=2-31, 30 fps, 15360 tbn
frame=18057 fps= 30 q=-1.0 Lsize=  313928KiB time=00:10:01.83 bitrate=4273.1kbits/s speed=   1x elapsed=0:10:09.94
```

Service log, all `[pid 65721]`:

```
11:03:02.191 [AvfStream] mic: capture clock start 158150.357438s
11:13:02.364 [RecordingService] Stop: mic/video alignment - video first frame at 158149.872500s,
             mic first sample at 158150.357438s, offset 0.485s
11:13:02.648 [AvfStream] Stop mic: ffmpeg reported ... size=   56286KiB time=00:10:00.41
             bitrate= 768.0kbits/s speed=   1x elapsed=0:10:00.42
11:13:02.649 [AudioCapture] Stop: wrote 57636864 bytes of PCM = 600.38s of audio over a 600.69s capture
11:13:03.084 [FfmpegRecorder] Stop: pipeline drained in 433ms (take 599945ms, media origin 1357ms)
             - full take captured
```

The `[AudioCapture] Stop:` line is the fix's own fail-closed check. It is at INFO, i.e. it did not
warn: 0.31 s short of the capture, inside the 1.0 s device warm-up slack.

### 3.2 The 60-second run - `~/Movies/AgentEyes/2026-11-14_111433_video`

Started 11:14:32, stopped 11:15:34. `/record/stop` reported `DurationSeconds: 61.23`.

```
recording.mp4   video h264 30/1   61.833333 s   1855 packets
                audio aac 48000 Hz 60.736896 s   2849 packets
mic_native.wav  48000 Hz  60.224000 s
audio_16k.wav   16000 Hz  60.736875 s

frame rate     1855 / 61.833333      = 30.000 fps
expected       61.833333 x 30        = 1855.0 frames  -> deviation 0.000 %
AAC to seconds 2849 x 1024 / 48000   = 60.779 s
audio vs video 61.833 - 60.737       = 1.096 s
file vs report 61.833 - 61.23        = 0.603 s
16k vs video   61.833 - 60.737       = 1.096 s
```

Log lines:

```
  Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12, 1920x1080, 1000k tbr, 1000k tbn, start 158841.842667
  Stream #0:0: Video: h264 (avc1 / 0x31637661), yuv420p(tv, progressive), 1920x1080, q=2-31, 30 fps, 15360 tbn
frame= 1855 fps= 30 q=-1.0 Lsize=   29833KiB time=00:01:01.76 bitrate=3956.8kbits/s speed=0.999x elapsed=0:01:01.85

11:15:34.305 [RecordingService] Stop: mic/video alignment - video first frame at 158841.842667s,
             mic first sample at 158842.355563s, offset 0.513s
11:15:34.449 [AudioCapture] Stop: wrote 5781504 bytes of PCM = 60.22s of audio over a 60.50s capture
11:15:34.993 [FfmpegRecorder] Stop: pipeline drained in 543ms (take 59771ms, media origin 1350ms)
             - full take captured
```

### 3.3 Every file was opened and read

A duration is not a recording. Both runs were inspected for content:

```
10-minute  recording.mp4 audio  n_samples 28,841,709  mean -69.7 dB  max -27.8 dB
           mic_native.wav       n_samples 28,818,432  mean -69.7 dB  max -27.8 dB
60-second  recording.mp4 audio  n_samples  2,915,371  mean -74.2 dB  max -37.9 dB
```

28,818,432 / 48000 = 600.384 s, which is `mic_native.wav` exactly. The samples are real microphone
audio at a low level - this Mac sits in an empty room - not digital silence, which would report
`-inf dB` and a max at the noise floor of the format.

`thumb.jpg` from both runs, and a frame extracted at **t = 580 s** of the 10-minute recording, were
all opened and all show the live desktop. None is blank. The 580 s frame matters: it proves the tail
of the recording is real picture, not a stalled encoder repeating one frame.

### 3.4 The frame rate is delivered, not padded - and the instrument was checked

`-r 30` on the output could in principle manufacture 30 fps by duplicating frames. It did not: the
final progress line of BOTH runs carries **no `dup=` or `drop=` field at all**, and the string
"duplicated" appears zero times in either log.

An absence proves nothing by itself, so the instrument was checked against a known-bad input - a
5 fps source forced to a 30 fps output on this same ffmpeg:

```
ffmpeg -f lavfi -i testsrc=size=320x240:rate=5 -t 4 -c:v libx264 -r 30 dup_probe.mp4
frame=  120 ... dup=100 drop=0 speed=67.5x
```

This ffmpeg prints `dup=` the moment it duplicates anything. Our runs print none, so 18057 and 1855
are genuinely delivered frames. (The developer's own 10-minute run reported `dup=4 drop=0`; this one
duplicated nothing at all.)

Worth recording: the 10-minute run was made while **four orphaned mux ffmpeg processes from the
developer's session were each burning ~100% CPU** (see finding F4). It still delivered a true
30.00 fps with zero duplicates on a machine four cores down.

---

## 4. Expected vs Actual, criterion by criterion

| # | Criterion | Expected | Actual | |
|---|---|---|---|---|
| 1 | Device accepts the frame rate (**as amended by the lead**: `Output #0` reads the requested fps AND the measured delivered rate matches) | `30 fps` on `Output #0`; delivered 30 fps | `Stream #0:0: Video: h264 ..., 30 fps, 15360 tbn` on both runs; delivered **30.000 fps** on both, with no duplicated frames | PASS |
| 2 | Audio spans a 10-minute recording, within 3.0 s | <= 3.0 s | video 601.900 s vs audio 600.869 s = **1.031 s**; 28167 x 1024 / 48000 = 600.896 s | PASS |
| 3 | Same on a 60-second recording, within 3.0 s | <= 3.0 s | video 61.833 s vs audio 60.737 s = **1.096 s**; 2849 x 1024 / 48000 = 60.779 s | PASS |
| 4 | File length vs the reported `DurationSeconds`, within 1.0 s | <= 1.0 s | 10 min: 601.900 vs 601.24 = **0.660 s**; 60 s: 61.833 vs 61.23 = **0.603 s** | PASS |
| 5 | Frame count within 10% of duration x fps | <= 10% | 10 min: 18057 vs 18057.0 = **0.000%**; 60 s: 1855 vs 1855.0 = **0.000%** | PASS |
| 6 | `audio_16k.wav` spans the recording, within 3.0 s | <= 3.0 s | 10 min: **1.031 s** (600.869 vs 601.900); 60 s: **1.096 s** (60.737 vs 61.833) | PASS |
| 7 | Encoder keeps up, `speed=` >= 0.95x | >= 0.95x | 10 min: **`speed=   1x`**; 60 s: **`speed=0.999x`** | PASS |
| 8 | Windows and Linux untouched | argument lists unchanged; their tests pass | 52 shared-builder argument lists **byte-identical** between `main` and the PR head (section 5); Linux suite 94 passed here; Windows suite green in CI | PASS |
| 9 | Build + tests on both platform configurations | `0 Error(s)`, `Failed: 0` | mac **103 passed**, linux **94 passed 1 skipped**, both builds `0 Error(s)` | PASS |
| 10 | A regression test that fails before the fix and passes after | fires against the shipped arguments | five mutations, five guards fired (section 6) | PASS |

Criterion 1's original input clause (`30 tbr`) is NOT met - the input line reads
`1920x1080, 1000k tbr, 1000k tbn` on both runs. Per the lead's ruling this is not judged against,
and the reason was verified rather than accepted: the log carries
`[in#0/avfoundation] Stream #0: not enough frames to estimate rate; consider increasing probesize`,
so the demuxer has nothing to estimate from.

---

## 5. Criterion 8 in detail - the shared-code risk, measured

This is the riskiest part of the PR: it changes `RecordingService.cs`, `FfmpegRecorder.cs`,
`Video/FfmpegArgs.cs`, `Audio/AudioMix.cs`, `Manifest.cs` and `Platform/Linux/AudioCapture.cs`.

**The claim.** `MicChain` gained a `source` parameter defaulting to `"[0:a]"`, so every existing
caller emits what it emitted before.

**Verified by reading.** `src/AgentEyes.Core/Video/FfmpegArgs.cs:367` is the new signature;
`:396` substitutes `{source}`. The default is resolved at the call site, and the three callers that
do not pass one - `MixTwoWav` (`:413`), `MuxVideoMixMicSystem` (`:428`), `FilterVideoMic` (`:445`) -
all call `MicChain(o)`. No other existing line in that file changed.

**Verified by measurement, which is what makes it a presence.** A throwaway test (QA scratchpad, not
committed) was compiled against both trees - `main` and PR head `4b77ff69` - and dumped every shared
builder's exact argument list across all 16 combinations of denoise / gate / level / measured
threshold, plus `MuxVideoAddSystem`, `ExtractWav` and `BurnSubtitles`:

```
52 builder invocations, 16,721 bytes of argument text
diff argdump_main.txt argdump_pr.txt  ->  IDENTICAL, byte for byte
```

The dump is not empty and was read - a sample line:

```
FilterVideoMic[denoise=True gate=True level=True thr=0.02] ==> -y | -i | raw.mp4 | -filter_complex |
  [0:a]arnndn=m='/models/bd.rnnn',agate=threshold=0.02:ratio=2:attack=20:release=250,
  speechnorm=e=4:p=0.95,volume=1[m];[m]alimiter=limit=0.95:level=false[a] | -map | 0:v | ...
```

**The tests that pin it**, as the criterion asks the proof to name:

| File | Test | What it pins |
|---|---|---|
| `tests/AgentEyes.Tests.Mac/MacMicAlignmentTests.cs:236` | `FilterVideoMic_StillReadsTheMicFromInsideInputZero` | the full `FilterVideoMic` list, exact-equality, reading `[0:a]` |
| `tests/AgentEyes.Tests.Mac/MacMicAlignmentTests.cs:253` | `MuxVideoAddSystem_IsUnchanged` | the full `MuxVideoAddSystem` list, exact-equality |
| `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs:15` | `X11VideoCapture_WholeScreenAndMic_ExactCommandLine` | the x11grab + pulse list INCLUDING its mic input |
| `tests/AgentEyes.Tests.Linux/LinuxFfmpegArgsTests.cs:35` | `CaptureTopology_OnLinux_TheScreenRecorderCapturesTheMicItself` | Linux still captures the mic inside the screen recorder |
| `tests/AgentEyes.Tests/FfmpegArgsTests.cs:286` | `FilterVideoMic_copies_video_and_filters_mic_without_amix` | the Windows suite's own assertion; runs in CI only |

Mutation M5 (section 6) proves the first of those is load-bearing rather than decorative.

**What criterion 8 does NOT cover, stated plainly.** Two changes in this PR alter shared RUNTIME
behaviour on Windows and Linux, and no argument-list test can see them:

* `RecordingService.cs:473` / `:755` - `_sw.Restart()` moved before `StartSession`, so the reported
  `DurationSeconds` now includes writer start-up on all three platforms and will shift by a few
  hundred milliseconds on Windows and Linux.
* `FfmpegRecorder.cs:245-250` - the drain now targets `now - mediaOrigin` instead of `now`.

Both are the developer's own disclosed risk, both are correct in intent, and on this machine the
media origin measured 1350-1357 ms. Neither can be exercised on a Mac. The only coverage they have
is the Windows CI job, which is **green on all three jobs at head `4b77ff69`**. That is the honest
extent of the Windows evidence in this pass.

---

## 6. Criterion 10 in detail - the guards were run against known-bad input

A guard that has only ever seen the state you hope passes has demonstrated nothing
(DEVELOPMENT_METHOD 6c item 3). Five mutations were applied to a throwaway copy of the tree in the
QA scratchpad - **the repository was not modified** - and the Mac suite run after each.

| | Mutation | Suite | Guards that fired |
|---|---|---|---|
| M0 | none (baseline) | 103 passed, 0 failed | - |
| M1 | put the shipped microphone input (`-f avfoundation -thread_queue_size 1024 -i :0`) and `-c:a aac -b:a 128k` back into `AvfVideoCapture` | 99 passed, **4 failed** | `AvfVideoCapture_NeverCapturesTheMicrophone_WhateverTheScreen`, `AvfVideoCapture_WholeScreen_ExactCommandLine` |
| M2 | remove `-r <fps>` from the screen capture output | 99 passed, **4 failed** | `AvfVideoCapture_DeclaresTheRequestedRateOnTheOutput`, `AvfVideoCapture_WholeScreen_ExactCommandLine` |
| M3 | remove `-thread_queue_size 1024` from `AvfStream.Arguments` | 100 passed, **3 failed** | `AvfStreamArguments_GiveTheDeviceADeepInputQueue`, `AvfStreamArguments_AreRawMonoPcmOnStdout` |
| M4 | put the microphone log level back to `warning` | 101 passed, **2 failed** | `AvfStreamArguments_LogLevelIsInfo_SoTheCaptureClockIsReported`, `AvfStreamArguments_AreRawMonoPcmOnStdout` |
| M5 | change `MicChain`'s default source label from `[0:a]` to `[1:a]` | 102 passed, **1 failed** | `MacMicAlignmentTests.FilterVideoMic_StillReadsTheMicFromInsideInputZero` |

M1 is the criterion-10 regression test proper: it is the argument list that shipped, and the guard
fires on it. M5 is the criterion-8 pin: it fires the moment the shared default moves.

Each guard also asserts a PRESENCE first (that the list really is an AVFoundation capture), so an
empty or throwing builder fails rather than passing for free. That was read in the test source, not
assumed.

---

## 7. The four checks the Implementation Lead asked for

### 7.1 Reproduce the root-cause contrast - REPRODUCED

The entire fix rests on the claim that scheduling priority, not the capture arguments, destroys the
audio. The same ffmpeg command line - the mic argument list this PR ships, deep input queue included
- was run twice from a shell, bounded by 20 s of wall clock, differing only in scheduling class.
48000 Hz mono s16 is 96000 bytes/s, so a clean 20 s capture is exactly 1,920,000 bytes.

```
normal QoS                      1,898,496 bytes = 19.78 s of a 20 s capture   (98.9% kept)
background QoS (taskpolicy -b)    322,560 bytes =  3.36 s of a 20 s capture   (16.8% kept)
```

The developer measured 1,920,000 vs 319,392. **The contrast reproduces: 83.2% of the audio destroyed
by scheduling class alone.** A 2-second control capture first returned exactly 192,000 bytes, so the
instrument was known to be working before the pair was run.

Two things this adds to the record. First, it was reproduced WITH `-thread_queue_size 1024` present,
so the queue does not rescue a descheduled demuxer - `ProcessType Interactive` is doing the work, and
the plist line is load-bearing, not a tuning preference. Second, it is the only measurement in this
pass that can be made from a shell, because it needs no Screen Recording grant.

### 7.2 Confirm the #128 stall - CONFIRMED

A 76-second screen+mic recording with the API's DEFAULT mic settings (no `denoise`/`gate`/`level`
keys in the body at all):

```
POST /record/start  {"mode":"video","screen":1,"source":"mic","mic":"Yealink UH36"}
POST /record/stop   -> {"DurationSeconds":76.23}       the stop itself returns normally

t+10s   mux=running   cpu=98.5   recording.mp4=45350960 bytes
t+60s   mux=running   cpu=99.1   recording.mp4=45350960 bytes
t+120s  mux=running   cpu=100.0  recording.mp4=45350960 bytes
t+180s  mux=running   cpu=100.0  recording.mp4=45350960 bytes
```

Three minutes at a full core with the output frozen to the byte and `PostProcessing.mux.State` still
`running`. The hung command line:

```
ffmpeg -y -i .../raw.mp4 -i .../mic_native.wav -filter_complex
  [1:a]arnndn=m='.../bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];
  [m]adelay=517.855:all=1,alimiter=limit=0.95:level=false[a]
  -map 0:v -c:v copy -map [a] -c:a aac -b:a 160k .../recording.mp4
```

**And the attribution was tested, not accepted.** If the stall lived in the NEW two-input macOS mux
(`[1:a]`, a second input, `adelay`), it would be caused by this change and the scope claim would be
wrong. So the PRE-EXISTING shape was run directly - one input, `[0:a]`, no adelay, no second input:
exactly what `FilterVideoMic` has always emitted on all three platforms - against the 61.8 s
recording from section 3.2, whose mic IS inside input 0:

```
[0:a]arnndn,volume[m];[m]alimiter[a]                      COMPLETED in 2s, 31,642,330 bytes
[0:a]speechnorm,volume[m];[m]alimiter[a]                  COMPLETED in 2s, 31,630,984 bytes
[0:a]arnndn,speechnorm,volume[m];[m]alimiter[a]           STALLED - 90s at 99% CPU, output frozen
```

The developer's attribution is correct: **the stall is in the pre-existing shared `MicChain` shape
and this PR only un-masks it** by making the audio long enough to reach it. Either filter alone
completes in two seconds; the pair stalls.

This bears directly on #128's open question. On this ffmpeg (9.0.2), the shape that stalls is
literally the one `FilterVideoMic` emits today on Windows for any mic recording with denoise and
level on and roughly a minute or more of audio. Whether the Windows ffmpeg build behaves the same
cannot be tested from this machine, so this is evidence for the question, not an answer to it.

### 7.3 The three-input mixed-audio mux - NOT VERIFIED END TO END, stated as a gap

This PR adds a new macOS three-input mux for mic + system audio
(`FfmpegArgs.MuxVideoAddMicAndSystem`, `AudioMix.MuxVideoMicAndSystemWavs`). It is **argument-tested
only.** It is not passed here as if it were exercised.

The gap is structural on this machine, and that was confirmed rather than assumed:

```
POST /record/start {"mode":"video","screen":1,"source":"mixed","mic":"Yealink UH36", ...}
{"error":"system audio cannot be recorded on this Mac: none of the audio inputs on this Mac is a
 loopback device that carries the system output. ...", "code":"bad_request"}
```

No recording starts, so no mixed capture can be made here at all. What exists:

* `MacMicAlignmentTests.MuxVideoAddMicAndSystem_MicIsInputOne_SystemIsInputTwo` (line 188) pins the
  full argument list by exact equality.
* `MacMicAlignmentTests.MuxVideoAddMicAndSystem_AlignsTheMicTheSameWayTheMicOnlyMuxDoes` (line 208)
  pins the `adelay` / `-ss` alignment and that the seek applies to the mic, not the system WAV.
* Code review of `RecordingService.cs:1084-1093`: the three-input form is selected before the
  two-input one, which is correct - a macOS `raw.mp4` has no `[0:a]` for `MuxVideoMixed` to find, so
  without this change a macOS mixed recording would have failed its mux outright. The developer found
  this in their own review and it is a genuine save.

**What is unverified: the running capture.** No BlackHole was installed - that is a decision for the
lead, not a QA step.

### 7.4 Windows and Linux untouched - see section 5

Verified three ways: by reading the diff, by the 52-invocation byte-identity measurement, and by CI
green on all three jobs at head `4b77ff69`. The named tests are tabulated in section 5, and section 5
also states exactly what that evidence does not cover - the two shared runtime behaviour changes,
whose only Windows coverage is the CI job.

---

## 8. Findings for the review gate

None of these fails an acceptance criterion. All are reported with file and line; **no product code
was changed by QA.**

### F1 - Two product comments still state the DISPROVED root cause as established fact

`src/AgentEyes.Core/Platform/Mac/FfmpegArgs.Mac.cs:102-111` still reads:

```
///  * screen in one ffmpeg, mic in ANOTHER, at the same time, same devices
///                                        -> 25.000 s asked, 392 packets = 25.09 s. NO LOSS.
///
/// So the loss is coupling INSIDE one ffmpeg process between an AVFoundation screen input and
/// an AVFoundation audio input ...
```

and `src/AgentEyes.Core/Video/CaptureTopology.cs:10-12`:

```
/// macOS cannot: two AVFoundation inputs in one process, one of them a display, lose about 88% of
/// the microphone's samples - see the measurement table on FfmpegArgs.AvfVideoCapture.
```

That "screen in one ffmpeg, mic in ANOTHER -> NO LOSS" line is precisely the experiment the developer
retracted in the handoff note ("That experiment was confounded - the sidecar ran from a shell, at
normal QoS. It was the QoS that differed, not the process boundary"), and the lead's ruling records
the retraction. The retraction lives in the handoff, the PR body and the issue; the source files a
future developer will actually open still assert the superseded cause, and use it to justify the
topology.

Concrete failure this invites: someone reading `FfmpegArgs.Mac.cs:105` concludes the process split is
the fix, and treats `ProcessType Interactive` as removable tuning. Section 7.1 measures that it is
not - with the process already split, background QoS still destroys 83% of the audio.

Recommended correction (not applied by QA): state the QoS cause as the finding, keep the process
split with the two merits the handoff gives it, and mark the sidecar line as the confounded
experiment it was. The plist comment already does this correctly and can be the model.

### F2 - "every other caller of `MicChain`" is not accurate, in two places

`tests/AgentEyes.Tests.Mac/MacMicAlignmentTests.cs:229-234` and the criterion-8 row of
`docs/cencon/proof/issue-114/handoff.md` both say the two pinned builders - `FilterVideoMic` and
`MuxVideoAddSystem` - are "every other caller of the `MicChain` I changed". They are not:
`MuxVideoAddSystem` does not call `MicChain` at all, and `MixTwoWav` (`FfmpegArgs.cs:411`) and
`MuxVideoMixMicSystem` (`FfmpegArgs.cs:426`) do call it and are pinned by exact equality nowhere.

An enumeration is an absence claim wearing a list's clothes (DEVELOPMENT_METHOD 6c item 5), and this
one is not exhaustive. The underlying byte-identity is nonetheless true - section 5 measures all
three callers across 16 option combinations and finds them identical - so this is a wrong sentence,
not a wrong change. Worth correcting so the next person does not rely on a pin that is not there.

### F3 - The issue's scope asked for an explicit statement about `AvfCameraCapture` and `AlwaysOnArgs`; the handoff gives none

The issue's IN scope says: "The same inspection applied to `AvfCameraCapture` and `AlwaysOnArgs` IF
the root cause is shared - state explicitly whether it is or is not." Neither name appears in the
handoff note. QA's own answer, since it is cheap:

* **The root cause IS shared, and is already fixed for them.** `ProcessType` is a property of the
  launchd job, not of a capture, so every ffmpeg the agent starts - camera included - leaves
  throttled scheduling behind on reinstall. No per-builder change is needed for that.
* **`AlwaysOnArgs` has no macOS capture path at all.** `src/AgentEyes.Core/AlwaysOn/AlwaysOnArgs.cs:81`
  is `-f gdigrab`, Windows only. There is nothing there to inspect on this platform.
* **`AvfCameraCapture` is unchanged** and already carries `-thread_queue_size 1024` and no audio
  input (`FfmpegArgs.Mac.cs`, the builder after `AvfVideoCapture`). It did NOT receive the `-r <fps>`
  that the screen capture got. That may well be correct - a camera is a real `AVCaptureDevice` and
  reports its own rate, unlike a screen input - but it is untestable here: `/devices` reports
  `cameras: []`. Stated as unverified, not as a defect.

### F4 - For #128: a stalled mux survives the service and is retried, and it is expensive

Not this PR's defect, but measured in this pass and it belongs with #128.

When QA arrived, **four orphaned mux ffmpeg processes from the developer's session were still
running** - pids 56447, 57181, 58575, 61492, parented to launchd (ppid 1), each at 95-98% CPU, with
elapsed times of 35 to 55 minutes:

```
56447  1  97.6  54:41  ffmpeg ... 2026-11-14_100913_video/raw.mp4 ... arnndn ... speechnorm ...
57181  1  94.9  50:07  ffmpeg ... 2026-11-14_100913_video/raw.mp4 ...
58575  1  97.1  47:13  ffmpeg ... 2026-11-14_101820_video/raw.mp4 ...
61492  1  97.8  35:18  ffmpeg ... 2026-11-14_101820_video/raw.mp4 ...
```

Two observations. A stalled mux is not killed when the service is restarted or reinstalled - it is
reparented and keeps a full core indefinitely, so four of them had been consuming roughly four of
this machine's ten cores for the best part of an hour. And the stall is **retried**: killing QA's own
hung mux (pid 70079) produced a fresh one (pid 70784) within seconds at 100% CPU, with
`PostProcessing.mux.Attempts` advancing to 2.

All five were killed as cleanup so later measurements were not distorted; no recording directory was
touched, and nothing under `~/Movies/AgentEyes` was deleted. The wedged recording
`2026-11-14_111731_video` is left in place as evidence for #128.

---

## 9. Checks that could have failed open, and what was done instead

| Check | The absence that would have passed for free | The presence used instead |
|---|---|---|
| "the frame rate is real" | no `dup=` in the log | `dup=` proven to appear on this ffmpeg against a 5 fps source forced to 30 (`dup=100`), then shown absent, with frames = duration x 30 exactly |
| "Windows and Linux unchanged" | no test failed | 52 argument lists dumped from both trees and diffed byte for byte, the dump read and sampled |
| "the regression test works" | the suite is green | five mutations applied, five guards fired, named per mutation |
| "the audio is there" | the audio stream's duration field | AAC frame count x 1024 / sample rate, plus `n_samples` and a level reading showing real samples - the before state proves the duration field alone lies (34.53 s reported over 4.01 s of samples) |
| "the recording is a recording" | the file is non-zero | thumbnails and a t=580 s frame opened and read; all show live desktop |
| "the stall is pre-existing" | the developer says so | the pre-existing `[0:a]` shape run directly and stalled, with both single-filter controls completing in 2 s |
| "the install took" | the repo plist has ProcessType | the INSTALLED plist read, the service PID and start time tied to the installed binary's hash, every log line quoted carries that pid |

---

## 10. Verdict

**PASS - all ten acceptance criteria verified, criterion 1 as amended.**

Gaps stated, not passed over:

1. The three-input macOS mixed-audio mux is argument-tested only and **not exercised end to end**;
   this Mac cannot start a mixed recording at all.
2. The two shared runtime changes in `RecordingService` and `FfmpegRecorder` affect Windows and
   Linux and cannot be exercised here; their only Windows coverage is the green CI job.
3. The proof runs are scoped to `denoise:false, gate:false, level:false`, because with the defaults
   the post-processing hangs - #128, confirmed here and shown to be pre-existing.

Four findings are recorded in section 8 for the review gate. F1 is the one worth acting on before
merge: the source still asserts a root cause the PR itself disproved.

Handed to the Review Gate as `flow:ready-gate`. QA did not merge, and did not change product code.
