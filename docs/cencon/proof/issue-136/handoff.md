# Issue #136 - Developer handoff

**[Recorder] macOS: the mux deadlocks when a copied video stream is paired with the filtered mic chain**

PR: #140, against `main`, **stacked on #133** (issue #128's PR). Branch
`issue-136-mux-queue-deadlock`, cut from `issue-128-mic-chain-stall`, not from `main`: #128 adds the
progress watchdog criterion 9 asks me to leave working and it edits the same files.

I believe this is finished. The decision asked for in the issue is **candidate (1), two passes**;
the reasoning and the measurements behind it are in "The decision" below.

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
was still running at 100% CPU 31 minutes later. I left it alone - it is the installed app's child
and it is evidence. It also means every timing below was taken with one of this machine's ten cores
already pinned, so the numbers are conservative.

### 2. Ten minutes, default settings, macOS - amended to the mux level

Pairing, stated because the video's size and bitrate are the mechanism:
`2026-11-14_110301_video/mic_native.wav` (600.384 s, pcm_s16le, 48 kHz, mono) with
`2026-11-14_110301_video/recording.original.mp4` (601.900 s, h264, 4.27 Mbit/s, 321 MB) - the same
recording's own video, and the same bitrate class as criterion 1's.

- **Expected:** completes; audio vs video duration within 3.0 s.
- **Actual:** **6.71 s** wall clock through `FinalizePending`. `recording.mp4` 332,354,524 bytes,
  `ffprobe` video 601.900 s / audio 600.390 s - **1.510 s apart**, which is the capture's own
  difference carried through unchanged. `PendingMux` cleared, `DurationSeconds` 601.9, no
  intermediate left. Default settings: `NoiseSuppression` true, `VoiceLeveling` true, `NoiseGate`
  false, `MicGain` 1, `SystemGain` 0.7, RNNoise model materialized by the product.

### 3. Sixty seconds, default settings, macOS - amended to the mux level

Pairing: `2026-11-14_111433_video/mic_native.wav` (60.224 s, pcm_s16le, 48 kHz, mono) with
`2026-11-14_111433_video/recording.original.mp4` (61.833 s, h264, 3.95 Mbit/s, 30.5 MB).

- **Expected:** completes; audio vs video within 3.0 s.
- **Actual:** **1.05 s**. `recording.mp4` 31,612,581 bytes, video 61.833 s / audio 60.230 s -
  **1.603 s apart**. No intermediate left.

**A caveat on this one that QA must read, and it is not about my fix.** While measuring criterion 4
I found that the mic chain on **this particular recording's content** intermittently wedges *with no
video stream in the command at all*, on `main`'s chain as well as this one - so it is not the muxing
queue, it is not a regression, and the two-pass split neither causes nor cures it. See "Second
finding" below: 2 of 4 bare-filter-graph runs on the same bytes wedged with #128's sanitizer, 4 of 4
without it, and 0 of 2 with `speechnorm` taken out. The mux above completed in 1.05 s; a repeat may
hit that stall instead, and
#128's watchdog is what turns that into a bounded failure rather than a hang - which is the other
reason criterion 9 matters.

### 4. The audio is still processed

Measured with `volumedetect` on `2026-11-14_111731_video`'s content. The decisive comparison is not
raw-vs-processed (which only shows *something* happened) but **two-pass output vs the same filter
graph run in one pass with no video present** - the Lead's matrix case 6, which completes:

| | mean_volume | max_volume |
|---|---|---|
| raw capture, no processing | -75.1 dB | -38.3 dB |
| the filter graph in ONE pass, no video | -68.8 dB | -41.0 dB |
| **the TWO-PASS mux output (the fix)** | **-68.8 dB** | **-41.0 dB** |

Identical to the tenth of a dB. The split did not change what the chain does: the mean is 6.3 dB
up on the raw capture (`speechnorm` boosting quiet speech) and the peak is 2.7 dB down (`arnndn`
plus the limiter). A mux that completed because the filters stopped doing anything would show the
raw numbers; it does not.

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
  crossed with `-filter_complex`/`-filter_complex_script`/`-lavfi`.
- `TheGuardDoesNotTouchTheCasesThatAreFine` - a copied *audio* stream beside a video filter graph
  (the camera compose), and a pure stream copy. Flagging these would have meant breaking compose.
- `TheAlwaysOnBuildersCarryNoFilterGraphBesideACopiedVideo`, plus the two pass-content tests.

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

### Second: the mic chain itself intermittently wedges on 111433's content, with no video present, on `main`

While measuring criterion 4 I ran the clean-voice graph on `c3-60s`'s audio with **no video stream
mapped at all** and it wedged at 100% CPU past the end of the content - the same
reaches-the-end-then-spins signature, but it cannot be the muxing queue, because there is no second
stream to interleave. It is **intermittent on identical bytes**, it is **not** #128's sanitizer, and the stalling stage is
`speechnorm`:

| chain, same 60.224 s input, `pcm_f32le` to .wav, 75 s bound | runs | wedged |
|---|---|---|
| with #128's `aeval` sanitizer (this branch) | 4 | **2** |
| without it (`main`'s chain) | 4 | **4** |
| with the sanitizer, `speechnorm` removed | 2 | **0** |
| the same chain on 111731's audio instead (criterion 1's recording) | 3 | **0** |

So it is pre-existing on `main`, the sanitizer is not the cause - it wedged **more** often without
it - and it is content-dependent: `speechnorm` on this one recording's audio. Read against #128's
own criterion 3 ("default-settings post-processing completes on macOS"), that 2-of-4 is worth
someone's attention: the sanitizer reduces the `speechnorm` stall on this content but does not
remove it. The frozen size is identical every time, **11,534,336 bytes = exactly 11 MiB**,
against 11,564,252 bytes on a successful run: the chain produces 60.07 s of the 60.23 s and then
freezes 0.16 s from the end, on a flush boundary, without writing the WAV's final header. `-f adts`
output (a raw stream with no header to rewrite) completed where `.wav` and `.m4a` did not. That
reads as an ffmpeg 9.0.2 end-of-stream scheduling race rather than a filter that cannot finish -
the same family as the muxing-queue deadlock, and not the same bug.

The repeat measurements are in [chain-stall-repeats.txt](chain-stall-repeats.txt).

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
   first - note that packaging then goes on to transcribe and will fail at that step if the machine
   is not signed in; the mux has already happened by then and the log records it).
   [build-mux-fixtures.sh](build-mux-fixtures.sh) builds the exact three directories I measured,
   from material already on the machine; [mux-run-log.txt](mux-run-log.txt) is the run it produced,
   with the expected numbers. Do not delete `2026-11-14_111731_video`, `2026-11-14_110301_video` or
   `2026-11-14_111433_video` - they are the material, and 111731 is the regression case.

4. **The before, if you want it.** Run the shipped single-pass command from the service log (it is
   quoted in criterion 1) against `2026-11-14_111731_video`. Expect output time to freeze near
   `time=00:01:16` within two seconds and the process to sit at ~100% CPU indefinitely. Kill it;
   it will not finish. Nothing in the repository can run that command any more, which is the point.

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
- **The intermediate can be left behind by a hard kill.** The `finally` covers a failure and an
  exception; a `SIGKILL` or a power cut between pass 1 and pass 2 leaves `audio_processed.wav` in
  the recording directory, where `RecoveryManifest.FilesOnDisk` would list it as capture bytes. The
  next mux attempt overwrites it (`-y`) and then deletes it, so it persists only for a recording
  that is abandoned entirely. `sys_native.wav` has always had the same property.
- **The 602 s timing is one run on one machine with one core already pinned** by the stuck installed
  mux. 6.71 s against a 60 s stall bound is not a close call, but it is one sample.
