# Issue #136 - rebase onto `main`, and the evidence re-run on the rebased code

`main` moved while this branch waited: #128 landed as `93681fa` (its three rounds squashed) on top
of `af0ddb1` (#122/#129). This branch was cut from `issue-128-mic-chain-stall` at `ea7b66a` - round
ONE - so it did not contain `main`, and PR #140 was `CONFLICTING`.

The rebase is `git rebase --onto origin/main ea7b66a`: the four #128 round-one commits
(`953fdfc`, `83528d3`, `123bb49`, `ea7b66a`) are DROPPED, because `main` now carries all three
rounds of that work; the fifteen #136 commits are replayed on top. `merge-base --is-ancestor
origin/main HEAD` is now YES.

Machine: `devthrottle-mac-mini`, ffmpeg 9.0.2 (`/opt/homebrew/bin/ffmpeg`), .NET 8. Load during the
measurements below: **2.12 to 2.97 on ten cores, with ONE CORE PINNED** - see "What the machine was
doing" at the end. The service was NOT reinstalled, nothing was recorded, and nothing under
`~/Movies/AgentEyes` was deleted. Harness paths below are written `<scratch>`.

---

## 1. What conflicted, and how each was resolved

Five files conflicted across three of the sixteen replayed commits.

| file | the conflict | resolution |
|---|---|---|
| `tests/AgentEyes.Tests.csproj`, `...Tests.Mac.csproj`, `...Tests.Linux.csproj` | both sides append an `ItemGroup` linking a `tests/Shared` file - `main` links `CliDispatcherTests.cs` + `MuxLockTests.cs` (#122, #128), this branch links `FfmpegMuxShapeTests.cs` | **KEEP BOTH**, in all three suites |
| `src/AgentEyes.Core/Audio/AudioMix.cs` | only the `using` block, plus a UTF-8 BOM this branch had added and then removed | took `main`'s `using System; using AgentEyes.Video;`, no BOM. The method bodies merged: `RunStage` is **`main`'s** (round two's D5 `RemovePartialOutput`), `MuxInTwoPasses` is this branch's |
| `tests/AgentEyes.Tests/MicChainStallTests.cs` | `main` added round two's D3/D7 tests to the same file this branch had retargeted | took **`main`'s whole file**, then applied this branch's one rule to it: every call to a builder #136 deleted becomes the builder that replaced it |
| `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` (twice) | both sides add entries to the IL write-site inventory | keep `main`'s three (`MuxCoordinator::RemoveFragment`, `MuxLock::TryAcquire`, `AudioMix::RemovePartialOutput`) and this branch's three; `main` moved `RunStage`'s delete to `RemovePartialOutput`, so this branch's now-stale `RunStage` line is dropped rather than re-added |

Two things conflicted SEMANTICALLY rather than textually - they merged clean and then failed:

1. **`main`'s D7 pins the NaN sanitizer to the mic's measured layout**, so `AudioMixOptions` built by
   hand must now carry `MicChannels`. Five `FfmpegMuxShapeTests` threw
   `the mic's channel count has not been measured`. Both fixtures in that file now set
   `MicChannels = 2`, with the reason written at the point of use. `MuxTwoPassTests` needed nothing:
   its options go through `AudioMix.Prepare`, which measures it.
2. **`main`'s D3 adds `asetnsamples=n=480:p=1` before `arnndn` and `aformat=channel_layouts=...`
   after the sanitizer.** `MicChain` is shared by this branch's pass-1 builders, so both stages are
   now inside pass 1 - which is why criteria 2 and 3 behave differently after the rebase (below).

`RunStage` is still **byte-for-byte `main`'s**. This branch moved its callers; the rebase did not
change that.

---

## 2. And the three deletes QA's F2 named

QA's F2 counted four unguarded `File.Delete` calls in `MuxInTwoPasses` (`AudioMix.cs:137`, `:142`,
`:152`, `:167` on `47b4da1`), three of which are #128's D5 pattern - a cleanup delete that can
replace the exception describing the failure - and said round two's fix to `RunStage` would not
reach them, because that method did not exist when D5 was filed.

Applied here, following the shape #128 landed on `main` (`RemovePartialOutput`: attempt the delete,
and when it fails, keep the stage error FIRST and append the cleanup failure to it):

- the three in the `finally` now go through **`RemoveMuxScratch`**, which REPORTS a failed delete
  instead of throwing it. On the failure path the caller appends what it reports to the stage error
  and keeps the stage error as `InnerException`; on the success path there is no error to mask, so a
  cleanup failure raises on its own rather than leaving litter in the recording directory - which is
  what a throwing `finally` already did, now with a message that says what happened.
- the fourth, in **`RemoveStaleTemp`, is deliberately left alone** and the reason is written at the
  point of use: it runs BEFORE either pass, so no stage error is in flight for it to replace. If it
  throws, its own exception IS the whole story. Guarding it would turn a clear failure into a
  swallowed one, which is the opposite of what D5 asks for.

**Regression test, run against a known-BAD input and shown to fire.**
`MuxInTwoPasses_CleanupCannotRemoveATemp_TheStageErrorSurvivesWithTheCleanupFailureAppended` plants a
DIRECTORY at the intermediate's path - `File.Delete` cannot remove one on any platform - and fails
the audio pass separately on a missing input.

| arm | result |
|---|---|
| expected | ONE `UsageException`; its message STARTS with ffmpeg's own complaint, has the cleanup failure appended, and the stage error is the `InnerException`. Observed: pass |
| known-bad (the guard mutated out) | `Assert.IsType() Failure: Expected typeof(AgentEyes.UsageException), Actual typeof(System.UnauthorizedAccessException)` - the defect itself, the cleanup replacing the real error |
| empty | no exception at all would mean the mux did not fail; `Assert.NotNull(ex)` is the first assertion |

The same mutation also reds `PassTwo_ReallyWritesTheMuxingTemp_AndNotTheFinalPath`, which plants a
directory at the muxing temp - so 2 of 12 fire, not 1.

**The IL call-site inventory was re-read out of the compiled assembly, not guessed.** After the
change, `agenteyes.dll` contains exactly:

```
AgentEyes.Audio.AudioMix::MuxInTwoPasses     -> System.IO.File::Move   x1
AgentEyes.Audio.AudioMix::RemoveMuxScratch   -> System.IO.File::Delete x1
AgentEyes.Audio.AudioMix::RemovePartialOutput-> System.IO.File::Delete x1
AgentEyes.Audio.AudioMix::RemoveStaleTemp    -> System.IO.File::Delete x1
```

which is what `ManifestWriterIlTests` now pins. (That guard lives in the Windows suite, so it is CI
that runs it; the inventory above was produced on this machine by running the repo's own
`CompiledCode` IL walker over the built `agenteyes.dll`, so the pinned list is a reading rather than
a prediction.)

---

## 3. What was RE-RUN on the rebased code, and what is carried forward

A rebase with real product-code conflicts invalidates a carried-forward pass, so everything the
conflicts touch was re-measured. All of it through `RecordingService.FinalizePending` - the real
deferred-mux path - over fixtures built by this branch's own `build-mux-fixtures.sh` from material
already on the machine.

### RE-RUN - criterion 1, the regression case (`2026-11-14_111731_video`)

`raw.mp4` 77.067 s / 4.62 Mbit/s + that directory's `mic_native.wav` 75.488 s - both halves of the
mux that wedged in the field.

| run | wall clock | output | video / audio |
|---|---|---|---|
| 1 | **1.27 s** | 45,872,117 B | 77.067 / 75.490 = 1.577 s apart |
| 2 | **1.26 s** | 45,872,117 B | same |

Byte-identical to QA's `45,872,117 B`. No intermediate and no muxing temp left behind either time.
Criterion 4 re-measured on the same output: **mean -68.8 dB / max -41.0 dB**, the same figure the
developer and QA both recorded, so the merged mic chain did not change what the chain does to this
material.

### RE-RUN - criteria 2 and 3, which the merged mic chain IMPROVED

These are re-run because `main`'s D3 framing stage now sits inside pass 1, which is where the
separate `speechnorm` stall used to fail them.

| criterion | before the rebase (QA, on `d51b2e8`) | after the rebase |
|---|---|---|
| 2, 602 s | 1 of 4 completed, 6.53 s | **5 of 5 completed** - 7.04, 6.83, 6.90, 6.92, 6.83 s, 332,354,609 B |
| 3, 60 s | 2 of 4 completed, 0.938 s | **6 of 6 completed** - 1.06, 1.08, 1.09, 1.08, 1.07, 1.10 s, 31,612,581 B |

Every wedge QA recorded was in PASS 1, which is #128's D3 and not this defect. `main` now carries
D3's fix, and on this material the stall does not recur in 11 runs. **Stated as a rate, not a
verdict:** D3's trigger is nondeterministic (the measurements on this issue put it at roughly 1 in 4
on `110301`), so 11 clean runs is evidence that the fix works on this material, not proof that the
stall is gone. Output sizes are byte-identical to QA's, so the two fixes compose without changing
what the mux produces.

### RE-RUN - the atomic publish, paired both ways

The managed HOST is SIGKILLed FIRST - so no `finally`, no `catch` and no cleanup of ours can run -
and then its ffmpeg child. Fixture `c2-600s` (601.9 s / 334 MB).

| kill point | rename in place (committed) | rename mutated out |
|---|---|---|
| pass 2, 1,310,768 B written | final path **ABSENT**; `.muxing.tmp` 2,621,488 B | - |
| pass 2, 22,282,288 B written | final path **ABSENT**; `.muxing.tmp` 23,593,008 B | - |
| pass 2, ~524,336 B written | - | `recording.mp4` **26,476,592 B - moov atom not found** |
| pass 2, ~22,806,576 B written | - | `recording.mp4` **41,943,088 B - moov atom not found** |
| pass 1, 40,108,032 B written | final path **ABSENT**; `audio_processed.wav` 40,894,464 B in flight | - |
| pass 1, 100,139,008 B written | final path **ABSENT**; `audio_processed.wav` 101,187,584 B in flight | - |

The absence is paired with a presence, so it is not vacuous: without the rename the same kill leaves
a moov-less fragment AT the final path; with it, the bytes are accounted for at the temp. As the
handoff declares and QA corrected the Implementation Lead on: **the temps ARE orphaned by a process
death** - what is guaranteed is the final path.

And the next attempt clears them, verified on REAL orphans rather than planted ones. Re-running
`FinalizePending` over the directory left by the kill at 12,582,960 B, with nothing reset:

```
[WARN] [AudioMix] MuxInTwoPasses: process mic audio removed a stale temp from an attempt that did
       not finish: <scratch>/c2-600s/audio_processed.wav (115275008 bytes)
[WARN] [AudioMix] MuxInTwoPasses: process mic audio removed a stale temp from an attempt that did
       not finish: <scratch>/c2-600s/recording.mp4.muxing.tmp (14417968 bytes)
[INFO] [AudioMix] MuxInTwoPasses: process mic audio done out=<scratch>/c2-600s/recording.mp4
```

6.84 s, 332,354,609 B, both orphans gone, at WARN with their byte counts.

### RE-RUN - the shape guard, and #114's two builders with it

`MuxVideoAddMic` was grafted VERBATIM from `origin/issue-114-macos-audio-loss-and-frame-rate` (the
only adaptation is `MicChain(o, "[1:a]")` -> `MicChain(o)`, since the label overload lives on that
branch; the shape under test is untouched). The guard fires **by name in both Unix suites** - Mac
1 of 239, Linux 1 of 281:

```
NoArgumentListPairsACopiedVideoStreamWithAFilterGraph
  a copied video stream is back in the same ffmpeg command as a filter graph (issue #136 - on macOS
  that command never finalizes; split it into an audio pass and a mux pass, see
  FfmpegArgs.MuxVideoWithProcessedAudio):
  FfmpegArgs.MuxVideoAddMic: -y -i fixture.mp4 -i fixture.mp4 -filter_complex
  [0:a]aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same,aformat=channel_layouts=stereo,
  speechnorm=e=4:p=0.95,volume=1[m];[m]adelay=1500:all=1,alimiter=limit=0.95:level=false[a]
  -map 0:v -c:v copy -map [a] -c:a aac -b:a 160k fixture.mp4
```

The printed command carries `main`'s `aeval` and `aformat` stages, so the guard is reading the
post-rebase builder and not a stale one. **The mechanism that protects #114 on its merge commit
still works after the rebase.**

### Every mutation reverted, proven by hash

| mutation | reverted, blob = `HEAD`'s |
|---|---|
| D5 guard removed from `RemoveMuxScratch` | `AudioMix.cs` `a976fbf4d12a46a10e5b6166f29f1318b78addc7` |
| atomic rename removed | `AudioMix.cs` `a976fbf4d12a46a10e5b6166f29f1318b78addc7` |
| `MuxVideoAddMic` grafted | `FfmpegArgs.cs` `fdff698893556dded41e90fae119b71e44930f5c` |

`git status` clean, gate re-run green afterwards.

### CARRIED FORWARD, and why each is unaffected by the conflicts

| criterion | carried because |
|---|---|
| **5** - no argument list in the engine pairs a copied video stream with a filter graph | the conflicts did not add, remove or rename a builder. The property is not carried on trust anyway: `NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` re-derives it by reflection over THIS build's `FfmpegArgs` on every run of both Unix suites, and it is green |
| **6** - Windows and Linux are covered by CI only | unchanged by the rebase, and the limit is restated rather than softened: **no Windows or Linux runtime mux has been measured by anyone**, before or after |
| **7** - the guard fails in both Unix suites when a single-pass builder returns | re-run above, so this is not actually carried |
| **9** - #128's progress watchdog still fires | DEFERRED in writing by the Implementation Lead. It is now also moot on this path in a way it was not before: `main` carries D3's fix, and the pass-1 stall that was firing the watchdog did not recur in 11 runs |
| the **`-rf64 auto`** 4 GB bound, and `ProcessedAudioCodec = pcm_f32le` | pure additions in `FfmpegArgs`, in no conflicted hunk, pinned by argument-shape tests that are green |

---

## What the machine was doing, because a timing is a floor and not a measurement

Load **2.12 to 2.97 on ten cores**, with **one core pinned throughout** - and by the defect this
issue fixes, which is worth recording because nobody arranged it.

The installed `main`-based service muxed a recording it made at **09:36 this morning**
(`2026-11-15_093623_video`), with the shipped single-pass command:

```
ffmpeg -hide_banner -y -i .../raw.mp4 -filter_complex [0:a]arnndn=...,speechnorm=...,volume=1[m];
  [m]alimiter=...[a] -map 0:v -c:v copy -map [a] -c:a aac -b:a 160k .../recording.mp4
```

pid 43589, **100.0% CPU, still spinning 71 minutes later** - and it has left
`recording.mp4` at **4,718,640 B** at the FINAL path, which `ffprobe` reports as
**`moov atom not found`**. That is both halves of this branch in one live example: the muxing-queue
deadlock, and the moov-less fragment at the final path that the atomic rename removes. I left it
running - it is the installed app's child and it is evidence - so every number above is conservative.

**No ffmpeg of mine is left running.** The service was not reinstalled, the plist still carries
#114's `ProcessType Interactive`, nothing was recorded, and nothing under `~/Movies/AgentEyes` was
deleted - the fixtures are copies built into a scratch directory.

---

## Gates

| gate | result |
|---|---|
| macOS: `dotnet build AgentEyes.Core -p:AgentEyesUnixPlatform=mac` | **Build succeeded, 0 Error(s)** |
| macOS: `dotnet test tests/AgentEyes.Tests.Mac` | **Failed: 0, Passed: 239** |
| Linux project: `dotnet build AgentEyes.Core -p:AgentEyesUnixPlatform=linux` | **Build succeeded, 0 Error(s)** |
| Linux project: `dotnet test tests/AgentEyes.Tests.Linux` | **Failed: 0, Passed: 280, Skipped: 1** |

`main` is Mac 226 / Linux 267+1; this branch adds the 13 shared shape tests to each, giving 239 and
280+1. No inherited count reduced.

**A limit of the two Unix runs above, stated rather than left implied:** they are the Linux PROJECT
on a macOS HOST, which is not a Linux run. And CI's macOS job does not run
`tests/AgentEyes.Tests.Linux` at all, so neither CI nor this machine covers "the Linux suite on
Linux" for the macOS case - the local runs are what cover it here. CI results for this head are on
PR #140; the Windows suite, which is the one that runs `ManifestWriterIlTests` and
`MuxTwoPassTests`, is covered only there.
