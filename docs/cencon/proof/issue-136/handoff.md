# Issue #136 - Developer handoff

**[Recorder] macOS: the mux deadlocks when a copied video stream is paired with the filtered mic chain**

PR: #140, against `main`, **stacked on #133** (issue #128's PR). Branch
`issue-136-mux-queue-deadlock`, cut from `issue-128-mic-chain-stall` at `ea7b66a`, not from `main`:
#128 adds the progress watchdog criterion 9 asks me to leave working and it edits the same files.

**#128 went `flow:qa-failed` while I was working**, so my base will move. I have not rebased - the
Implementation Lead's instruction was that she tells me when to. Nothing in #128's QA report
contradicts this change; one of its defects (D3) is in fact the root cause of the second finding
below, which is a help rather than a problem. A `git diff` of this branch against the **current**
tip of `issue-128-mic-chain-stall` will show its two QA-report commits as deletions - that is the
usual two-dot-diff artifact, not something I removed. Against the merge base it is 15 files,
+2165/-68.

I believe this is finished, with one thing stated up front rather than buried. The decision asked
for in the issue is **candidate (1), two passes**; the reasoning and the measurements are in "The
decision" below. The muxing-queue deadlock this issue is about is gone, measured at 76 s, 61 s and
602 s through the product's own code path. While measuring it I ran into a **separate, pre-existing,
intermittent `speechnorm` stall** that has nothing to do with the muxing queue - it reproduces with
no video stream in the command and on `main`'s chain - and which will sometimes fail the same mux
for its own reason. It is reported to the Implementation Lead, it is not fixed here, and criteria 2,
3 and 4 below say exactly where it touches them. If QA's reading is that criterion 2 cannot be
called met until that is fixed, that is a fair reading and I would not argue with it; what I will
not do is present it as met without saying so.

Machine: `devthrottle-mac-mini`, macOS, ffmpeg 9.0.2 (`/opt/homebrew/bin/ffmpeg`), .NET 8.
**I did not reinstall the service and I did not record anything.** The installed plist is still the
hand-modified one with #114's `ProcessType Interactive` and the installed binary is still
`main`-based - untouched. Everything below is my branch's code driven over material that already
existed on the machine, through `RecordingService.FinalizePending`, which is the real deferred-mux
code path the service uses.

---

## The decision: two passes, not `-max_muxing_queue_size`

Two passes. The queue bound was measured and rejected.

| | two passes | `-max_muxing_queue_size <n>` |
|---|---|---|
| 76 s recording | 1.17 s | ~1 s (the Implementation Lead's measurement) |
| 602 s recording | 6.71 s | not measured - see below |
| depends on a tuned number | no | yes |
| what happens when the number is too small | n/a | the defect comes back, silently, as an unkillable 99%-CPU spin |

The reason is not the measured times - both are fast. It is that the queue depth needed grows with
how far the copied video can race ahead of the filters, and that distance grows with the
recording's length and its bitrate. A value chosen against a 76-second 4.6 Mbit/s recording is a
claim about one point on a two-dimensional surface. Pick 9999 and the failure mode of being wrong
is not an error message: it is this exact defect, on some future longer recording, on a user's
machine, with the recording lost. I would have had to pick a number AND justify it at ten minutes
AND say what happens past it, and the honest answer to the last part is "it deadlocks again".

The two-pass split has no such parameter. It removes the race rather than widening the queue the
race is lost in: pass 1 has no video stream in the command, so nothing can race the filters; pass 2
has no filter graph in the command, so nothing can race the copy. It also matches the architecture
the system-audio path already used with `sys_native.wav`. The costs are real and I am stating them
rather than burying them: one extra pass over the audio (2.9 s of the 6.71 s at ten minutes) and a
transient intermediate file (115 MB for a ten-minute mono capture), deleted when the mux ends
however it ends.

One thing I checked before committing to it, because the whole fix rests on it: **pass 2's shape -
`-c:v copy` beside an audio encode with no filter graph - does not race at ten minutes either.**
Building the 602 s fixture is exactly that command, and it took **2.90 s** on 334 MB. If pass 2 had
been slow or fragile, the split would have been no better than the magic number.

---

## The fix

`src/AgentEyes.Core/Video/FfmpegArgs.cs`

Removed - each of these was one ffmpeg run pairing `-c:v copy` with a filter graph:

| removed | was at | replaced by |
|---|---|---|
| `MuxVideoMixMicSystem` | FfmpegArgs.cs:435-446 | `MixMicSystemToWav` + `MuxVideoWithProcessedAudio` |
| `FilterVideoMic` | FfmpegArgs.cs:452-463 | `FilterMicToWav` + `MuxVideoWithProcessedAudio` |
| `MuxVideoAddSystem` | FfmpegArgs.cs:467-477 | `FilterSystemToWav` + `MuxVideoWithProcessedAudio` |

Added:

- `MicOnlyGraph(o)` / `MicPlusSystemGraph(o)` - the two filter-graph strings, extracted so the
  processing cannot drift between the WAV path and the video path. `MixTwoWav` (the audio-only
  recording's final output) now uses `MicPlusSystemGraph` and is otherwise unchanged.
- `FilterMicToWav` / `MixMicSystemToWav` / `FilterSystemToWav` - **pass 1**. The audio graph alone to
  an intermediate WAV, no video stream mapped.
- `MuxVideoWithProcessedAudio` - **pass 2**. `-map 0:v -c:v copy -map 1:a -c:a aac -b:a 160k`, no
  filter graph of any kind.
- `ProcessedAudioCodec = "pcm_f32le"` - the intermediate's sample format. This is the detail that
  keeps the split honest: 32-bit float is what the filter graph computes in and what the AAC encoder
  takes, so pass 2's encoder gets the same samples the single pass handed it. `pcm_s16le` would have
  been half the disk and a quantization step nobody asked for.
- `-rf64 auto` on every pass-1 command. **A bound I went looking for after arguing against silent
  cliffs, and found one of my own.** A plain RIFF WAV holds its size in 32 bits, so it cannot
  describe more than 4 GB, and float at 48 kHz is 192 kB/s per channel - about 6 hours of mono or 3
  hours of stereo. Before this change there was no intermediate and therefore no such limit, so
  leaving the default (`-rf64 never`) would have introduced exactly the kind of unannounced length at
  which things stop working that I rejected `-max_muxing_queue_size` for. Measured: `auto` leaves a
  plain RIFF header below 4 GB (same bytes as the default apart from the 36-byte placeholder the
  upgrade needs), `always` writes an `RF64` magic, and pass 2 reads an RF64 intermediate and produces
  a correct `h264 + aac` output. I did **not** generate a 4 GB file to see the wrap; the bound is
  ffmpeg's documented reason for the option.

`src/AgentEyes.Core/Audio/AudioMix.cs`

- `MuxVideoMixed`, `MuxVideoSystemOnly`, `ProcessVideoMic` keep their signatures and their call sites
  (`RecordingService.FinalizePending`, `Commands`, `SelfTest` are unchanged) and now go through
  `MuxInTwoPasses`.
- `MuxInTwoPasses(rawMp4, outMp4, label, audioPass)` runs both passes through the existing
  `RunStage`, so **both** carry #128's stall watchdog and partial-output removal, and deletes the
  intermediate in a `finally`.
- `ProcessedAudioFileName = "audio_processed.wav"`, in the recording's own directory - beside
  `sys_native.wav`, on the same volume as the output, so a ten-minute intermediate cannot land on a
  smaller system temp disk.

**The defect my own regression test found in my first cut, worth reading because it is the #128
interaction.** `RunStage` deletes the partial output of *the pass that failed*. In a two-pass mux a
failure in pass 1 therefore cleans up the intermediate and **not** the final path - so a fragment
already sitting at the final path from an earlier killed attempt would have survived, and #128
measured exactly that fragment being taken for the finished recording by the thumbnail stage
("moov atom not found"). `MuxInTwoPasses` now also removes the final path unless *both* passes
succeeded. Splitting the command nearly dropped one of #128's guarantees on the floor.

---

## Acceptance criteria: Expected vs Actual

### 1. The failing recording now muxes

The exact shipped command from the service log, on `~/Movies/AgentEyes/2026-11-14_111731_video`
(`raw.mp4` 77.067 s 4.62 Mbit/s + `mic_native.wav` 75.488 s pcm_s16le mono):

- **Expected (the Lead's matrix, case 1):** stalls.
- **Actual:** STALLED. Output time frozen at `time=00:01:16.03` from 1.52 s elapsed onward,
  `frame=2283` unchanging, 100.0% CPU, killed at 151 s. My environment agrees with the matrix;
  I did not re-derive the rest of it.

My branch's own shape, on the same two files' content, through the real `FinalizePending` path:

- **Expected:** completes.
- **Actual:** **1.17 s**, `recording.mp4` 45,872,117 bytes, `ffprobe` h264 77.067 s video + aac
  48 kHz mono 75.490 s audio, no intermediate left behind. Full log:
  [mux-run-log.txt](mux-run-log.txt).

Note on the pairing, because it matters. The issue's regression case is #114's `MuxVideoAddMic`
shape (video-only `raw.mp4` + a separate `mic_native.wav`), and **that builder is not on this
branch** - it is on `issue-114-macos-audio-loss-and-frame-rate`. On this branch the equivalent
mic-only path is `ProcessVideoMic`, which takes the mic from inside the video. So the fixture is
`111731/raw.mp4`'s video stream with `111731/mic_native.wav` as its audio track, muxed with no
filters - the same bytes, in this branch's shape. See "#114" below; the shape guard is what stops
#114's two builders merging un-split.

**Independent corroboration, found while measuring and not created by me:** the installed
`main`-based service hit this defect live on this machine during the session. `ffmpeg` pid 94897,
started 12:29:30 for `~/Movies/AgentEyes/2026-11-14_120937_video` (a **13.4-second** recording),
was still running at 100% CPU **55 minutes later**, the last time I looked. I left it alone - it is
the installed app's child and it is evidence. Thirteen seconds of audio, and the shipped mux cannot
finish it; that is the defect, in the field, on the machine, while I measured the fix for it. It also means every timing below was taken with one of this machine's ten cores
already pinned, so the numbers are conservative.

### 2. Ten minutes, default settings, macOS - amended to the mux level

Pairing, stated because the video's size and bitrate are the mechanism:
`2026-11-14_110301_video/mic_native.wav` (600.384 s, pcm_s16le, 48 kHz, mono) with
`2026-11-14_110301_video/recording.original.mp4` (601.900 s, h264, 4.27 Mbit/s, 321 MB) - the same
recording's own video, and the same bitrate class as criterion 1's.

- **Expected:** completes; audio vs video duration within 3.0 s.
- **Actual, run 1:** **6.71 s** wall clock through `FinalizePending`. `recording.mp4` 332,354,524
  bytes, `ffprobe` video 601.900 s / audio 600.390 s - **1.510 s apart**, which is the capture's own
  difference carried through unchanged. `PendingMux` cleared, `DurationSeconds` 601.9, no
  intermediate left. Default settings: `NoiseSuppression` true, `VoiceLeveling` true, `NoiseGate`
  false, `MicGain` 1, `SystemGain` 0.7, RNNoise model materialized by the product.
- **Actual, run 2 of the same fixture: it did not complete, and I am reporting that rather than the
  run that suited me.** `pass 1` - the audio pass, which has no video stream in its command at all -
  froze at the very end of the content and #128's watchdog killed it:

  ```
  AgentEyes.UsageException : ffmpeg stalled during process mic audio (audio):
  output stopped advancing at 600.4s of output for 60s (limit 60s) after 63s - process killed
  ```

  `600.4s of output` against a 600.384 s mic track: it produced all of the audio and then failed to
  finalize. This is the second finding below, not the muxing queue - the muxing queue needs two
  streams to interleave and that command has one. The cleanup was correct: no `audio_processed.wav`
  and no `recording.mp4` fragment left, `raw.mp4` intact, `PendingMux` still set so a later attempt
  can retry.

  So the honest statement of criterion 2 is: **the muxing-queue deadlock is gone at ten minutes -
  when the mux runs, it takes 6.71 s instead of never - and on this particular material the audio
  pass has an independent, pre-existing stall that will often fail it instead, bounded at 63 s by
  #128's watchdog rather than spinning forever.** Often, not occasionally: the bare filter graph on
  this audio wedged 3 of 3 times, so the 6.71 s completion was the lucky one. The second finding
  below has the rates, the fact that it reproduces on `main`'s chain with no video in the command,
  and what I could and could not establish about it - including a `-filter_complex_threads 1` lead
  that looked like the answer on the ten-minute audio and then failed on the sixty-second one, so
  there is no workaround to hand QA. Both runs are in [mux-run-log.txt](mux-run-log.txt).

### 3. Sixty seconds, default settings, macOS - amended to the mux level

Pairing: `2026-11-14_111433_video/mic_native.wav` (60.224 s, pcm_s16le, 48 kHz, mono) with
`2026-11-14_111433_video/recording.original.mp4` (61.833 s, h264, 3.95 Mbit/s, 30.5 MB).

- **Expected:** completes; audio vs video within 3.0 s.
- **Actual:** **1.05 s** (run 1) and **1.02 s** (run 2), both completed. `recording.mp4` 31,612,581 /
  31,612,592 bytes, video 61.833 s / audio 60.230 s - **1.603 s apart**. No intermediate left either
  time.

**Two caveats on this one that QA must read, and neither is about my fix.** Both are the second
finding below.

- The mic chain on **this recording's content** intermittently wedges *with no video stream in the
  command at all*, on `main`'s chain as well as this one: 2 of 4 bare-filter-graph runs on the same
  bytes wedged with #128's sanitizer, 4 of 4 without it, 0 of 2 with `speechnorm` taken out. Both
  mux runs above happened to complete; a third may not.
- **The processed level is not reproducible on this content either.** Run 1's output measured mean
  -68.5 dB / max -41.5 dB and run 2's measured mean -42.6 dB / max -0.4 dB - 26 dB apart, from
  identical input through identical code, with run 2 pinned against the limiter. That is the same
  `speechnorm` nondeterminism showing up as gain rather than as a hang. It is why criterion 4's
  identical-output claim is made on 111731's content, which reproduced exactly twice, and not on
  this one.

### 4. The audio is still processed

Measured with `volumedetect` on `2026-11-14_111731_video`'s content. The decisive comparison is not
raw-vs-processed (which only shows *something* happened) but **two-pass output vs the same filter
graph run in one pass with no video present** - the Lead's matrix case 6, which completes:

| | mean_volume | max_volume |
|---|---|---|
| raw capture, no processing | -75.1 dB | -38.3 dB |
| the filter graph in ONE pass, no video | -68.8 dB | -41.0 dB |
| **the TWO-PASS mux output (the fix)** | **-68.8 dB** | **-41.0 dB** |

Identical to the tenth of a dB, and **reproducible** - two separate `FinalizePending` runs of this
fixture produced mean -68.8 / max -41.0 both times. The split did not change what the chain does:
the mean is 6.3 dB up on the raw capture (`speechnorm` boosting quiet speech) and the peak is 2.7 dB
down (`arnndn` plus the limiter). A mux that completed because the filters stopped doing anything
would show the raw numbers; it does not.

I measured this on 111731's content deliberately, and the reason is in criterion 3's second caveat:
on 111433's content the chain's own output level is **not** reproducible between runs (-68.5/-41.5
then -42.6/-0.4), so a comparison there would have measured ffmpeg's nondeterminism rather than my
change. 111731 reproduces exactly, which is what a reference comparison needs.

The static half of the same claim is pinned by test:
`FfmpegMuxShapeTests.TheTwoPassesTogetherStillCarryTheWholeCleanVoiceChain` asserts the sanitizer,
`arnndn`, `speechnorm` and `alimiter` are all in pass 1 **in #128's order**, that the intermediate
is `pcm_f32le`, and that pass 2 contains none of them.

### 5. Every `-c:v copy` + filtered-audio mux is covered

The complete enumeration of this engine's argument lists that hold either a stream copy or a filter
graph. Line numbers are as they were **before** this change.

| # | builder | file:line | the shape | changed? |
|---|---|---|---|---|
| 1 | `FfmpegArgs.MuxVideoMixMicSystem` | `Video/FfmpegArgs.cs:435-446` | `-c:v copy` + mic chain + amix | **YES - split** |
| 2 | `FfmpegArgs.FilterVideoMic` | `Video/FfmpegArgs.cs:452-463` | `-c:v copy` + mic chain | **YES - split.** This is the one live on macOS |
| 3 | `FfmpegArgs.MuxVideoAddSystem` | `Video/FfmpegArgs.cs:467-477` | `-c:v copy` + `volume` on the system WAV | **YES - split** |
| 4 | `FfmpegArgs.MixTwoWav` | `Video/FfmpegArgs.cs:420-430` | filter graph, **no video at all** | no - not the shape (the Lead's case 6, completes) |
| 5 | `FfmpegArgs.BurnSubtitles` | `Video/FfmpegArgs.cs:501-512` | `-c:v libx264` + `-c:a copy`, no filter graph | no - the video is encoded |
| 6 | `ComposeArgs.CameraInset` | `Video/ComposeArgs.cs:106-115` | filter graph + `-c:v libx264` + `-c:a copy` | no - the graph is on the video and the video is encoded |
| 7 | `AlwaysOnArgs.Capture` | `AlwaysOn/AlwaysOnArgs.cs:130` | filter graph + `-c:v libx264` | no - live capture, the video is encoded |
| 8 | `AlwaysOnArgs.Join` | `AlwaysOn/AlwaysOnArgs.cs:223-232` | `-c copy`, no filter graph | no |
| 9 | `AlwaysOnArgs.Trim` | `AlwaysOn/AlwaysOnArgs.cs:255` | `-c copy`, no filter graph | no |
| 10 | `Thumbnails` waveform | `Thumbnails.cs:96-108` | filter graph, no video input, one PNG out | no |

**Number 3 was the judgement call.** Its filter is a single `volume`, which has never been measured
losing the race, and splitting it costs a full extra pass over the audio. I split it anyway, for two
reasons. The issue's scope says to apply the treatment wherever the shape exists, and more
importantly an exemption list is a hole: the invariant "no copied video stream in the same command
as a filter graph" is checkable by a machine, whereas "no copied video stream beside a filter graph
that is slow enough to lose" is a judgement that has to be re-made correctly by every future author
of every future stage. One rule, no exemptions, and a test that enforces it.

The invariant is now enforced by `tests/Shared/FfmpegMuxShapeTests.cs`: a reflection sweep over
every static argument builder on `FfmpegArgs` (every partial in the build) and `ComposeArgs`, which
invokes each one and inspects what it really emits. Numbers 7, 8 and 9 have no cheap fixture for
their parameter types, so they are asserted directly in the same file. The guard is **compiled into
all three suites** by a linked `<Compile Include>`, so each platform's own `FfmpegArgs` partial is
covered by that platform's gate rather than by a sweep that only ever sees the Windows build.

Each of 1, 2 and 3 shown completing: criteria 1-3 above cover `ProcessVideoMic` (2) at 76 s, 61 s
and 602 s; `MuxVideoSystemOnly` (3) and `MuxVideoMixed` (1) are covered by
`MuxTwoPassTests.MuxVideoSystemOnly_RunsBothPassesAndLeavesNoIntermediate` and
`MuxVideoMixed_RunsBothPassesAndLeavesNoIntermediate`, which run real ffmpeg end to end and check
the output's streams. **I did not run 1 and 3 on ten minutes of real material**, because this
machine has no virtual audio device and so cannot produce a `sys_native.wav` at all (the #114 note
says the same). That is stated under "Not covered".

### 6. Windows and Linux still complete

- **Linux:** `dotnet build` + `dotnet test` green here and in CI (below). The Linux capture writes
  `mic.wav` / `sys_native.wav` and goes through the same `AudioMix` entry points, so it takes the
  same split.
- **Windows:** build + `dotnet test AgentEyes.sln` green in CI on PR #140. The solution cannot build
  on a Mac, so CI is the only place this is provable, and it is the gate the issue names.
- **Did either ever have this shape fail?** Not on record, and I did not reproduce it on either.
  The shape is identical on all three platforms; what differs is the race. On macOS the mic chain is
  slow enough to lose it. The Windows exposure that #128 recorded was the NaN stall, a different
  trigger in the same stage. Treat "Windows and Linux were never seen to deadlock here" as the
  absence of a report, not as proof of immunity - which is a reason to split all three platforms'
  path rather than only macOS's, and this change does.

### 7. A regression test that fails against today's code

Two new files plus retargeted existing ones.

`tests/Shared/FfmpegMuxShapeTests.cs` - 7 tests, compiled into all three suites:

- `NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` - the sweep. **Empty is a broken
  instrument:** a builder that cannot be invoked FAILS the test rather than being skipped, and the
  sweep asserts eight named builders are present so a rename cannot silently shrink it.
- `TheGuardFires_OnTheExactCommandThatDeadlocked` - the detector run against the shipped command
  line copied from the service log, so it is proven to fire rather than assumed to, and against
  each new builder alone, so the split is shown to clear it.
- `TheGuardFires_OnEverySpellingOfACopiedVideoStream` - `-c`/`-codec`/`-c:v`/`-codec:v`/`-vcodec`
  **and the stream-specifier forms (`-c:v:0`)**, crossed with
  `-filter_complex`/`-filter_complex_script`/`-lavfi`. The video forms are matched by prefix rather
  than by equality precisely because `-c:v:0 copy` copies the video exactly as `-c:v copy` does and
  an equality check would have waved it through; the same test asserts the detector does NOT fire on
  `-c:a copy`.
- `TheGuardDoesNotTouchTheCasesThatAreFine` - a copied *audio* stream beside a video filter graph
  (the camera compose), and a pure stream copy. Flagging these would have meant breaking compose.
- `TheAlwaysOnBuildersCarryNoFilterGraphBesideACopiedVideo`, plus the two pass-content tests - which
  also pin the intermediate's `pcm_f32le` and its `-rf64 auto`.

`tests/AgentEyes.Tests/MuxTwoPassTests.cs` - 6 tests, real ffmpeg: both passes run, the
intermediate is cleaned up, a failure in either pass leaves neither a fragment at the final path nor
an intermediate, and the intermediate goes beside the output rather than into the system temp.

Retargeted (same assertions, now against the pass-1 builders that carry the chain): 4 tests in
`MicChainStallTests` (#128's chain-order guards), 5 call sites in `GateCalibrationTests`, and the
three mux-shape tests in `FfmpegArgsTests` rewritten as four.

`ManifestWriterIlTests` gained one pinned entry for `MuxInTwoPasses`'s two `File.Delete` sites.

**Does any of it actually fail? I disabled each guard in turn and ran the suites. Failures, by
name:**

| what I disabled | failed |
|---|---|
| re-added a single-pass `-c:v copy` + filter-graph builder | `FfmpegMuxShapeTests.NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` - mac suite (Failed 1, Passed 78) **and** linux suite (Failed 1, Passed 119) |
| `ProcessedAudioCodec` back to `pcm_s16le` | `FfmpegMuxShapeTests.TheTwoPassesTogetherStillCarryTheWholeCleanVoiceChain` |
| the final-path cleanup (`if (!ok ...)`) | `MuxTwoPassTests.MuxInTwoPasses_AudioPassFails_RemovesTheIntermediateAndThePartialOutput` |
| the intermediate deletion | `MuxTwoPassTests.MuxVideoSystemOnly_RunsBothPassesAndLeavesNoIntermediate`, `MuxTwoPassTests.MuxInTwoPasses_MuxPassFails_RemovesTheIntermediateAndThePartialOutput`, `MuxTwoPassTests.MuxVideoMixed_RunsBothPassesAndLeavesNoIntermediate`, `MuxTwoPassTests.ProcessVideoMic_RunsBothPassesAndLeavesNoIntermediate`, `MuxTwoPassTests.MuxInTwoPasses_TheIntermediateGoesBesideTheOutput_NotIntoTheSystemTemp` (5 of 6) |
| `-rf64 auto` removed from all three pass-1 builders | `FfmpegMuxShapeTests.EveryPassOneBuilderWritesTheIntermediateAndNothingElse` |
| added a builder name to `RequiredBuilders` that the sweep cannot find | `FfmpegMuxShapeTests.NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` - the broken-instrument assertion is live |

All patches reverted; the tree is the committed state.

One honest limit in that table:
`MuxTwoPassTests.MuxInTwoPasses_MuxPassFails_RemovesTheIntermediateAndThePartialOutput` passes with
or without my `if (!ok)` block, because `RunStage` already removes the final path when it is pass 2
that fails. The test that proves the new block is the `AudioPassFails` one. The `MuxPassFails` test
earns its place on the intermediate, not on the final path.

### 8. Gates

| gate | result |
|---|---|
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` | Build succeeded, **0 Error(s)** (1 pre-existing warning, `AlwaysOnEngine.cs:901`) |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | **Failed: 0, Passed: 79**, Skipped 0 (inherited from #128's branch: 72) |
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux` | Build succeeded, **0 Error(s)** |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | **Failed: 0, Passed: 120**, Skipped 1 (inherited: 113 passed, 1 skipped - the skip is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, which is Linux-only and skips on a Mac) |
| Windows: `dotnet build AgentEyes.sln -c Release` + `dotnet test AgentEyes.sln -c Release` | CI, run 43624342222: Build succeeded, **0 Error(s)**; **Failed: 0, Passed: 2178**, Skipped 0. `AgentEyes.sln` contains the WPF app and cannot build on a Mac. |

CI on PR #140: **all three jobs green** (macos 79, linux 121, windows 2178). The first CI run failed
the `ManifestWriterIlTests` IL call-site guard - `MuxInTwoPasses` adds two `File.Delete` sites that
the issue #155 guard pins by name - which nothing local could have caught, since that guard reads
`AgentEyesApp.dll`'s IL. Fixed in `6cfa3a4`.

No inherited count reduced; **+7** on each Unix suite (the linked shape guard).

**One number that looks alarming and is not, so QA does not have to work it out.** #128's own CI run
reports Windows 2318 / mac 226 / linux 268, against my 2178 / 79 / 121. That is not 140-odd tests
disappearing. #128's run is a `pull_request` event, so GitHub builds its branch **merged with
`main`**, and `main` is one commit ahead of #128's base - `af0ddb1` (#122/#129), which adds about
147 tests to each Unix suite. My run is a `push` event on my branch head alone. The apples-to-apples
comparison is local, on the same machine, same commands:

| suite | `origin/issue-128-mic-chain-stall` (what I inherited) | this branch |
|---|---|---|
| Mac | 72 passed | **79** passed |
| Linux | 113 passed, 1 skipped | **120** passed, 1 skipped |

**And I checked the merge, because my green CI is for my branch head and not for what will actually
be merged.** `git merge origin/main` into this branch in a throwaway worktree: three conflicts, all
of them the same trivial one - `af0ddb1` and I each appended an `ItemGroup` just before `</Project>`
in the three test csproj files, and the resolution is keep both. With that resolved, the merged tree
builds clean and tests green on both Unix gates: **Mac 233 passed** (226 + my 7), **Linux 274 passed
+ 1 skipped** (267 + my 7). The worktree was removed; nothing of it is on the branch.

Incidentally `af0ddb1` had already introduced the exact `tests/Shared` + linked-`<Compile Include>`
convention I arrived at independently for the shape guard, so the two sit side by side. I switched my
include to `../Shared/...` forward slashes to match it.

I also compiled and ran the four Windows-suite files I touched against the macOS Core in a scratch
project (same assembly name, so internals resolve): **101 passed, 0 failed**, including #128's
real-ffmpeg stall tests. That is not the Windows gate - it does not build the WPF app or the
Windows-only tests - but it is why I expect the Windows job to be green on the test files I changed.

### 9. #128's guards still fire

Nothing of #128's was removed, duplicated or "improved": the sanitizer, the watchdog, the child
registry and the retry-ceiling fix are all as they were on `issue-128-mic-chain-stall`.

- **Both** passes go through `AudioMix.RunStage`, so both get `Ffmpeg.RunWatched` with
  `PostProcessStallTimeout` (60 s) and both get the partial-output removal. A two-pass mux is
  therefore *more* watched than the single pass was, not less.
- **And it fired for real, on this branch, on the product path, while I was measuring criterion 2.**
  I did not arrange this; the second run of the 602-second fixture stalled and the watchdog killed
  it:

  ```
  AgentEyes.UsageException : ffmpeg stalled during process mic audio (audio):
  output stopped advancing at 600.4s of output for 60s (limit 60s) after 63s - process killed
  ```

  That is criterion 9 answered by a real stalled stage rather than by a synthetic one: the message
  names the stage **and which pass of it**, the frozen output time and the bound; the process was
  killed at 63 s rather than spinning; and `MuxInTwoPasses` then left no `audio_processed.wav`, no
  `recording.mp4` fragment, `raw.mp4` intact and `PendingMux` still set for a retry. So this fix is
  demonstrably not the reason the watchdog would never trigger again - it triggered on the first
  genuinely stalled stage it met.
- `MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound` still passes: the
  known-stalling filter is killed and failed within the bound, with the message naming the stage,
  the frozen output time and the limit. Verified here (it is in the 101 above), not only in CI.
- The watchdog's own unit tests (`Watchdog_*`, 5 of them) pass unchanged.
- The fix is not the reason the watchdog would never trigger again: it removes the *muxing-queue*
  stall only. A stalled filter graph still stalls, and the watchdog still kills it - see the second
  finding below, which is a live example on this very branch.

---

## Two findings that are NOT this issue, reported separately

Both are in the mic chain, both reproduce identically before and after my change, and both are out
of scope here ("do not remove, duplicate or improve #128's sanitizer"). The first is #128's; the
second is older than #128 and is on `main`. I have raised both to the Implementation Lead. Recording
them here so QA does not rediscover them as #136 defects.

### First: #128's sanitizer segfaults ffmpeg on a mono mic plus stereo system audio

`aeval=...:c=same` on a MONO stream that is then up-mixed to stereo crashes ffmpeg 9.0.2 with
**exit 139 (SIGSEGV)**. That is the shipped default for every `Mixed` recording: a mono microphone
plus a stereo loopback capture, which `amix` up-mixes. Measured on two 3 s 48 kHz pcm_s16le
fixtures:

| graph | result |
|---|---|
| mono `aeval` + stereo -> `amix` | **rc=139** |
| the same with the `aeval` removed (`main`'s chain) | rc=0 |
| mono `aeval` alone, no `amix` | rc=0 |
| mono `aeval` -> `alimiter`, no `amix` | rc=0 |
| stereo `aeval` + stereo -> `amix` | rc=0 |
| mono `aeval` -> `aformat=channel_layouts=stereo` -> `amix` | **rc=139** |

So it is not `amix` as such - it is the `aeval:c=same` output being up-mixed from mono. The full
shipped mixed graph end to end: `main`'s chain rc=0, #128's chain rc=139. #128's own test does not
see it because its fixture mic WAV is stereo and its system WAV is stereo, and stereo/stereo is the
one combination that survives. The code path is `AudioMix.MixWavs` and `AudioMix.MuxVideoMixed`.

**Consequence for this PR:** `MuxTwoPassTests`'s video fixture has a **stereo** 48 kHz mic track, on
purpose and commented as such, so the mixed-path tests exercise my orchestration rather than
somebody else's crash. Mono is the common microphone; if that crash is fixed, make the fixture mono
again.

### Second: the mic chain itself intermittently wedges at the end of the content, with no video present, on `main`

While measuring criterion 4 I ran the clean-voice graph on `c3-60s`'s audio with **no video stream
mapped at all** and it wedged at 100% CPU past the end of the content - the same
reaches-the-end-then-spins signature, but it cannot be the muxing queue, because there is no second
stream to interleave. It then happened again inside the product, on the 602-second fixture, where
#128's watchdog killed it at 63 s (criterion 9 above) - so it is not confined to one short
recording. It is **intermittent on identical bytes**, it is **not** #128's sanitizer, and the
stalling stage is `speechnorm`:

| pass 1's exact command, `pcm_f32le` to .wav | input | runs | wedged |
|---|---|---|---|
| with #128's `aeval` sanitizer (this branch) | 60.224 s | 4 | **2** |
| without it (`main`'s chain) | 60.224 s | 4 | **4** |
| with the sanitizer, `speechnorm` removed | 60.224 s | 2 | **0** |
| the same chain on 111731's audio (criterion 1's recording) | 75.488 s | 3 | **0** |
| the same chain on the ten-minute audio | 600.384 s | 3 | **3** |
| the ten-minute audio with `-filter_complex_threads 1` | 600.384 s | 2 | **0** (3 s each) |
| the 60-second audio with `-filter_complex_threads 1` | 60.224 s | 3 | **2** |

So it is pre-existing on `main`, the sanitizer is not the cause - it wedged **more** often without
it - and it is content-dependent: `speechnorm`, on some content and not other content. Read against
#128's own criterion 3 ("default-settings post-processing completes on macOS"), that 2-of-4 is worth
someone's attention: the sanitizer reduces the `speechnorm` stall on this content but does not
remove it, and the 602-second material stalled the same way inside the product.

The same nondeterminism also shows up as **gain** rather than as a hang: two `FinalizePending` runs
of the 60-second fixture both completed and produced outputs 26 dB apart (mean -68.5 / max -41.5,
then mean -42.6 / max -0.4, the second pinned against the limiter) from identical input through
identical code. Whatever `speechnorm` is doing at the end of this stream, its state is not
reproducible run to run.

The frozen size, on the other hand, is identical every time, on both inputs: **11,534,336 bytes**
against 11,564,252 complete on the 60-second audio (60.07 s of 60.23 s), and **115,081,216** against
115,275,008 complete on the ten-minute one (599.4 s of 600.4 s). The chain produces all but the last
fraction of a second and then freezes without writing the WAV's final header. `-f adts`
output (a raw stream with no header to rewrite) completed where `.wav` and `.m4a` did not. That
reads as an ffmpeg 9.0.2 end-of-stream scheduling race rather than a filter that cannot finish -
the same family as the muxing-queue deadlock, and not the same bug.

**A conclusion I drew too early and am withdrawing.** When the ten-minute single-threaded runs came
back 0 of 2 against a default 3 of 3, I wrote here - and raised to the Implementation Lead - that
`-filter_complex_threads 1` removes the stall. The 60-second single-threaded runs then came back **2
of 3 wedged**, the same rate as that input's default. So it does not: single-threading changed
nothing measurable on the shorter audio, and 0-of-2 against 3-of-3 on the longer one is too small a
sample to claim a cause from. There is no flag in this branch that mitigates this stall, and I am
not going to invent one.

What survives is the part that was measured rather than inferred: the stall is real and frequent
(3 of 3 on the ten-minute audio), it is not the muxing queue, it is not #128's sanitizer, it is
`speechnorm` - removing that stage is the only intervention that cleanly stopped it (0 of 2) - it is
content-dependent, and #128's watchdog bounds it.

**AND THE ROOT CAUSE IS ESTABLISHED - by #128's QA seat, independently, on Windows, while I was
measuring this on the Mac.** #128 went `flow:qa-failed` during my pass, and its QA report's defect
D3 is this stall:

> **arnndn on ffmpeg 9.0.2 emits a NaN frame at end of stream on finite input.** The sanitizer sits
> before arnndn by necessity, so it cannot remove it, and speechnorm then spins at the end.

with `astats` showing 0 NaNs in the input and **480 NaNs (one 10 ms frame) after
sanitizer + arnndn**, the same stall under ffmpeg 9.0.2 and none under 8.1.1, and a length sweep in
which `samples mod 480` partly predicts it. That explains every number I measured and I am adopting
it rather than keeping my own partial account:

- it freezes a fraction of a second from the end, because the NaN frame is the terminal one;
- removing `speechnorm` stops it (0 of 2), because the NaN then passes through to a filter that can
  survive it;
- removing the sanitizer makes it **worse** (4 of 4), because the sanitizer removes the input's own
  non-finite samples but cannot remove the one `arnndn` manufactures after it;
- and it is content-dependent as well as length-dependent, which both seats measured separately -
  their real ten-minute dshow capture completed at a non-multiple length, and 111731's audio
  completed 3 of 3 here.

So "speechnorm is the stalling stage" is right and incomplete: `speechnorm` is where it spins,
`arnndn`'s end-of-stream NaN is what feeds it. Their report says "this may also bear on #136. This
QA seat has not verified that." It does bear on it, exactly as described, and these are the Mac-side
numbers for it. It remains #128's defect to fix, not mine: the fix belongs at the head or the tail
of the mic chain, which I was told not to touch.

One further caveat: whatever it is, it has a second symptom that no threading change touched - the
processed **level** is not reproducible either. The two completed single-threaded ten-minute runs
were 15 dB apart (mean -52.4 / max -0.4, then mean -67.7 / max -25.8), and two completed 60-second
product runs were 26 dB apart. So the hang and the varying gain may not share one cause.

All of it is in [chain-stall-repeats.txt](chain-stall-repeats.txt).

**Why it does not change this issue, and what it does change.** It reproduces with no video input,
so the two-pass split neither causes nor cures it, and it reproduces without the change to #128's
chain, so it is nobody's regression. What #128 gives it is a bound: the watchdog fails the stage in
60 s instead of spinning forever. Criterion 3's 1.05 s is a real measurement of a real completion,
but QA should know that **re-running the 60-second case may occasionally fail instead**, and that
such a failure is this, not a #136 regression. The two are easy to tell apart: a #136 failure needs
a video stream and a `-filter_complex` in the same ffmpeg command, and no command on this branch has
both - the log line names which pass failed.

---

## #114, and why its merge cannot quietly undo this

`issue-114-macos-audio-loss-and-frame-rate` adds two builders that are **exactly the removed shape**:

| builder | file | shape |
|---|---|---|
| `FfmpegArgs.MuxVideoAddMic` | `Video/FfmpegArgs.cs` (on that branch) | `-c:v copy` + `MicChain(o, "[1:a]")` + `adelay` + `alimiter` |
| `FfmpegArgs.MuxVideoAddMicAndSystem` | same | `-c:v copy` + the same chain + `amix` |

They are not on this branch, so this fix cannot reach them, and criterion 1's exact failing command
is one of them. **`FfmpegMuxShapeTests` is what stops them merging un-split:** the sweep invokes
every builder on `FfmpegArgs`, so on the merge commit the guard fails and names them. That is
deliberate - it is the one place a cross-branch regression of this defect can be caught.

How to split them, so the merge is mechanical:

- `MuxVideoAddMic(rawMp4, micWav, outMp4, o, micOffsetSeconds)` becomes
  `FilterMicToWav(micWav, intermediate, o)` with the `-ss` / `adelay` offset handling moved into
  that pass-1 command (input 0 becomes the mic WAV, so `MicChain(o, "[0:a]")` and the graph label
  changes from `[1:a]`), followed by `MuxVideoWithProcessedAudio(rawMp4, intermediate, outMp4)`.
  `AudioMix.MuxVideoMicWav` then calls `MuxInTwoPasses` exactly as `ProcessVideoMic` does.
- `MuxVideoAddMicAndSystem` becomes `MixMicSystemToWav` with the offset, plus the same pass 2.
- #114's `AudioMix` calls `Ffmpeg.Run` (it predates #128); on the merged result those become
  `RunStage` via `MuxInTwoPasses`, which is how they pick up the watchdog.

Note the mono/stereo segfault above applies to `MuxVideoAddMicAndSystem`'s `amix` too.

---

## How QA exercises this

**No heavy smoke is needed and none is appropriate.** The change has no UI surface, no Control API
surface and no capture surface: it alters only the ffmpeg commands the post-capture mux runs.
`api-smoke.ps1` and `gui-smoke.ps1` would exercise nothing this touched. The runtime surface is the
mux, and the way to drive the mux is the one I used.

1. **The gate.** The four Unix commands above, then the Windows pair. Expect the counts in criterion
   8 and no reduction against `issue-128-mic-chain-stall`.

2. **The shape invariant, which is the heart of it.** `dotnet test` any suite and read
   `FfmpegMuxShapeTests`. To satisfy yourself the guard is live rather than vacuous, add a builder to
   `FfmpegArgs` that pairs `-c:v copy` with a `-filter_complex` and watch
   `NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` name it.

3. **The real mux, on real material, without recording anything.** Everything needed is on
   `devthrottle-mac-mini`. Build a recording directory whose `raw.mp4` is a long video plus a long
   mic track and whose `manifest.json` carries a `PendingMux` of `Mode: video, Source: mic`, then
   drive `RecordingService.FinalizePending(dir)` (or `agenteyes package <dir>`, which calls it
   first). **I ran the `agenteyes package` route myself rather than only asserting it works**, since
   it is the route I am asking QA to use: on the 76-second fixture it muxed, renamed `raw.mp4` to
   `recording.original.mp4`, left no `audio_processed.wav`, and then failed at the NEXT stage -
   `transcription FAILED: Not signed in to DevThrottle` - which is expected on this machine and is
   after the mux, not part of it. Whole invocation 1.29 s.
   [build-mux-fixtures.sh](build-mux-fixtures.sh) builds the exact three directories I measured,
   from material already on the machine; [mux-run-log.txt](mux-run-log.txt) holds both runs it
   produced,
   with the expected numbers. Do not delete `2026-11-14_111731_video`, `2026-11-14_110301_video` or
   `2026-11-14_111433_video` - they are the material, and 111731 is the regression case.

4. **The before, if you want it.** Run the shipped single-pass command from the service log (it is
   quoted in criterion 1) against `2026-11-14_111731_video`. Expect output time to freeze near
   `time=00:01:16` within two seconds and the process to sit at ~100% CPU indefinitely. Kill it;
   it will not finish. Nothing in the repository can run that command any more, which is the point.

   Use **111731** for this, not 111433 or 110301. Those two also trip the separate `speechnorm`
   stall, so a hang there would not tell you which of the two defects you were looking at. 111731's
   audio completed 3 of 3 bare-graph runs and 2 of 2 product runs, so on that recording a hang means
   the muxing queue and nothing else.

5. **The watchdog (criterion 9).** `MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound`
   and the five `Watchdog_*` unit tests. The two-pass mux runs both passes through the same
   `RunStage`, so there is no path that escapes the watchdog - readable in
   `AudioMix.MuxInTwoPasses`.

Reminders carried for QA's benefit: the focus-free layers are REST / UIA / PrintWindow; never
force-foreground the app and synthesize input without warning the human; the recording HUD is
capture-excluded, so HUD or recording state is asserted via UIA or `/status`, never a screen grab.
None of those are needed for this issue.

---

## What this does NOT cover - stated, not left to be discovered

- **No end-to-end capture.** I recorded nothing, by instruction and because the defect is in the
  mux, which takes two files. The fresh-10-minute and fresh-60-second end-to-end runs with all
  manifest stages `done` move to #114's post-merge re-verification, which is where the issue's own
  amendment puts them. What I proved is the mux stage, driven by the product's own code, on real
  captures of the stated durations.
- **The mixed and system-only paths were not run on long real material.** This machine has no
  virtual audio device, so a `sys_native.wav` cannot be produced on it at all and a mixed or
  system-only recording cannot be started. Those two paths are covered by argument-shape tests and
  by real-ffmpeg runs on 5-second synthetic fixtures, not by a ten-minute capture. The mixed path
  additionally cannot be run with a mono mic at all right now - see the first finding.
- **No Windows or Linux runtime run.** Both are covered by build + unit tests only; I have no
  machine for either. The shape is identical and the split is platform-independent, but "the mux
  completes on Windows" rests on CI's unit tests plus the fact that Windows was not reported
  failing, not on a measured Windows mux.
- **The tests do not reproduce the deadlock.** They pin the *shape* and the orchestration. A
  five-second tone cannot lose the race, and reproducing it needs about a minute of real speech
  through `arnndn` beside a multi-megabit video - minutes of audible work, not a unit test. So if
  someone finds a *different* way to deadlock a mux that does not involve a copied video stream
  beside a filter graph, nothing here will catch it.
- **`pcm_f32le` is a claim about sample fidelity that I checked by `volumedetect`, not sample by
  sample.** Mean and max agree to 0.1 dB with the one-pass output; I did not compute a difference
  signal.
- **The 4 GB WAV bound was reasoned and the mechanism measured, but not reached.** I verified
  `-rf64 auto`'s behaviour below the bound and that pass 2 reads an RF64 file; I did not write a
  4 GB intermediate. Separately, `MixTwoWav` - the audio-only recording's own final `.wav`, which
  this change does not touch - has the same latent 4 GB RIFF limit at about 6 hours and no `-rf64`.
  That is pre-existing and out of scope here; it is worth its own issue.
- **The intermediate can be left behind by a hard kill.** The `finally` covers a failure and an
  exception; a `SIGKILL` or a power cut between pass 1 and pass 2 leaves `audio_processed.wav` in
  the recording directory, where `RecoveryManifest.FilesOnDisk` would list it as capture bytes. The
  next mux attempt overwrites it (`-y`) and then deletes it, so it persists only for a recording
  that is abandoned entirely. `sys_native.wav` has always had the same property.
- **The 602 s timing is one run on one machine with one core already pinned** by the stuck installed
  mux. 6.71 s against a 60 s stall bound is not a close call, but it is one sample - and the only
  other run of that fixture did not complete, for the separate reason above. I ran each fixture
  twice, not twenty times; what I can show is that the muxing-queue deadlock never recurred in any
  run, and that the runs which failed failed in PASS 1, with no video stream in the command.
- **I did not find the root cause of that second stall, and I did not try to fix it.** I established
  that it is `speechnorm`, that it is at the end of the stream, that it is intermittent, that it is
  on `main`, and that #128's watchdog bounds it. Why it is intermittent is unanswered: filter
  threading was the obvious suspect and `-filter_complex_threads 1` did not survive the data, so I
  withdrew that claim rather than keep it. **#128's QA seat then established it independently on
  Windows while I was measuring - its defect D3, `arnndn` emitting a NaN frame at end of stream on
  ffmpeg 9.0.2, which the head sanitizer cannot see and which `speechnorm` then spins on.** The
  second finding below adopts their account and gives the Mac-side numbers for it. The fix belongs in
  the mic chain, which is #128's and which I was told not to touch.
