# Issue #136 - QA report

**[Recorder] macOS: the mux deadlocks when a copied video stream is paired with the filtered mic chain**

PR #140, branch `issue-136-mux-queue-deadlock` at `d51b2e8`. Verified on `devthrottle-mac-mini`,
macOS, ffmpeg 9.0.2 (`/opt/homebrew/bin/ffmpeg`), .NET SDK 10.0.403 targeting net8.0.
Machine load at the start of the run: `2.02 1.92 2.25`; at the end: `2.12 2.26 2.31`. No ffmpeg was
running on this machine when I started and none when I finished - the 55-minute stuck mux the
developer recorded (pid 94897) is gone.

**VERDICT: PASS.** Every acceptance criterion is met, criterion 9 excepted - the Implementation
Lead deferred it in writing and this report does not require it. Three findings are recorded for the
Review Gate and for #128 round two; none of them is a defect in this change.

**I did NOT reinstall the service and I recorded nothing.** The installed plist still carries
#114's `ProcessType Interactive` (verified after my run) and the installed binary is still
`main`-based. Everything below is file-level, driven through the product's own code. I deleted
nothing under `~/Movies/AgentEyes`; all six protected directories are intact and their file counts
and sizes are in "Housekeeping" at the end.

---

## How I verified, and what I refused to take on trust

The developer's handoff is unusually complete and it would have been easy to re-state its numbers. I
did not. Every number below is one I produced, and in three places I deliberately built the
instrument before the measurement:

- I took the failing command from the **installed service's own log**, not from the handoff, and ran
  it myself.
- For the atomic rename I **mutated the rename out of the product**, measured the damage, put it
  back, and measured again - so "nothing at the final path" is paired with a listing showing what
  used to be there. An absence on its own would have proved nothing.
- For the #114 claim I **grafted #114's two real builders onto this branch** from its own branch and
  watched the guard name them, rather than believing that it would.

All mutations are reverted and the revert is proven by hash, below.

---

## Acceptance criteria: Expected vs Actual

### Criterion 1 - the failing recording now muxes. PASS

**The BEFORE, reproduced by me.** The exact command from the installed service's log
(`AgentEyes-20261114.log`, 11:18:49.316), against the intact failing pair in
`~/Movies/AgentEyes/2026-11-14_111731_video` (`raw.mp4` 77.067 s + `mic_native.wav` 75.488 s,
pcm_s16le mono). Output written to a scratch path; the fixture directory was not written to.

- **Expected:** stalls and never finalizes.
- **Actual: STALLED.** Frozen for the entire 150-second observation window - `frame=2283`,
  `time=00:01:16.03`, output bytes pinned at 45,350,960, CPU 99.0-100.0% on every one of the fifteen
  10-second samples:

  ```
  [ 10s] cpu=99.3%   frame= 2283  time=00:01:16.03  outbytes=45350960
  [ 70s] cpu=99.0%   frame= 2283  time=00:01:16.03  outbytes=45350960
  [150s] cpu=100.0%  frame= 2283  time=00:01:16.03  outbytes=45350960
  === STILL RUNNING after 150s - killing (this is the deadlock)
  ```

  It reached the end of the content and then spun: the last progress line reads
  `frame=2283 ... time=00:01:16.03 ... speed=0.507x elapsed=0:02:30.09`. What it left at the output
  path was **45,350,960 bytes, `ffprobe`: moov atom not found**.

  **Corroboration I did not create.** `2026-11-14_111731_video/recording.mp4` - left in the field by
  the installed service's failed mux - is **45,350,960 bytes and also `moov atom not found`**, the
  same byte count my repro produced. The field failure and my reproduction are the same event.

**The AFTER, through the real deferred-mux path** (`RecordingService.FinalizePending`, reached by
`agenteyes package <dir>`), on a fixture built by the developer's own
[build-mux-fixtures.sh](build-mux-fixtures.sh) from 111731's two halves.

- **Expected:** completes.
- **Actual: COMPLETED in 1.084 s** (mux start 14:09:41.739 -> done 14:09:42.823), of which pass 1
  took 0.381 s and pass 2 took 0.700 s. `recording.mp4` **45,872,117 bytes** - the exact byte count
  the handoff reports. `ffprobe`: h264 video 77.066667 s + aac 48 kHz mono 75.490 s, **1.577 s
  apart**. No `audio_processed.wav` and no `recording.mp4.muxing.tmp` left behind.

**And the two commands really have the shape claimed** - read from the log of that run, not from the
source:

```
pass 1: ffmpeg -y -i <dir>/raw.mp4 -filter_complex "[0:a]aeval=...,arnndn=...,speechnorm=...,volume=1[m];
                 [m]alimiter=...[a]" -map [a] -c:a pcm_f32le -rf64 auto <dir>/audio_processed.wav
        -> NO video stream, NO -c:v copy
pass 2: ffmpeg -y -i <dir>/raw.mp4 -i <dir>/audio_processed.wav -map 0:v -c:v copy -map 1:a
                 -c:a aac -b:a 160k -f mp4 <dir>/recording.mp4.muxing.tmp
        -> NO -filter_complex of any kind
```

Note on the pairing, carried from the handoff and confirmed: criterion 1's original shape is #114's
`MuxVideoAddMic` (video plus a separate WAV), which is not on this branch. The fixture is 111731's
video stream with 111731's `mic_native.wav` as its audio track - the same bytes, in this branch's
`ProcessVideoMic` shape.

### Criterion 2 - ten minutes, at the mux level as amended. PASS, with a declared non-#136 caveat

**Pairing, stated because the video's size and bitrate are the mechanism:**
`2026-11-14_110301_video/mic_native.wav` (600.384 s, pcm_s16le, 48 kHz, mono) with
**`2026-11-14_110301_video/recording.original.mp4`** (601.900 s, h264, 4.27 Mbit/s, 321 MB) - that
recording's own video. The fixture `raw.mp4` is 333,919,633 bytes.

- **Expected:** completes; audio vs video within 3.0 s.
- **Actual: 4 runs - 1 COMPLETED, 3 wedged in PASS 1.**

| run | wall | mux | classification |
|---|---|---|---|
| 1 | 6.91 s | **6.526 s** (14:12:41.126 -> 14:12:47.652) | **COMPLETED.** `recording.mp4` 332,354,609 B; `ffprobe` video 601.900 s / audio 600.390 s = **1.510 s apart** |
| 2 | 63.18 s | none | **WEDGE IN PASS 1** - the audio-only command, NO video stream. #128's D3, not this issue |
| 3 | 63.20 s | none | WEDGE IN PASS 1 |
| 4 | 63.14 s | none | WEDGE IN PASS 1 |

Each wedge was killed by #128's watchdog with the stage, the pass and the frozen point named:

```
ffmpeg stalled during process mic audio (audio): output stopped advancing at 600.4s of
output for 60s (limit 60s) after 63s - process killed
```

`600.4s of output` against a 600.384 s mic track. **I classified every wedge rather than counting
it, and every one was in pass 1 - a command with one input, no video stream and no copied video
stream.** The muxing queue needs two streams to interleave; that command has one. On the three
failed runs the cleanup was correct: no intermediate, no fragment at the final path.

**So criterion 2 is met at the mux level: the muxing-queue deadlock is gone - when the mux runs it
takes 6.53 s at ten minutes instead of never - and the failures that remain are #128's unfixed D3,
bounded at 63 s by #128's watchdog instead of spinning forever.** My 1-of-4 completion rate is
consistent with the handoff's honest report of a 3-of-3 bare-graph wedge rate on this audio.

### Criterion 3 - sixty seconds, at the mux level as amended. PASS, same caveat

**Pairing:** `2026-11-14_111433_video/mic_native.wav` (60.224 s, pcm_s16le, 48 kHz, mono) with
**`2026-11-14_111433_video/recording.original.mp4`** (61.833 s, h264, 3.95 Mbit/s, 30.5 MB).

- **Expected:** completes; audio vs video within 3.0 s.
- **Actual: 4 runs - 2 COMPLETED, 2 wedged in PASS 1.**

| run | wall | mux | classification |
|---|---|---|---|
| 1 | 1.10 s | **0.938 s** | **COMPLETED.** 31,612,581 B; video 61.833 s / audio 60.230 s = **1.603 s apart** |
| 2 | 61.12 s | none | **WEDGE IN PASS 1** (audio-only command) - `output stopped advancing at 60.2s ... after 61s` |
| 3 | 61.61 s | none | WEDGE IN PASS 1 - `... after 62s` |
| 4 | 1.13 s | **0.961 s** | **COMPLETED.** 31,612,581 B; same durations |

Both completions produced byte-identical output at 31,612,581 B, which is the handoff's figure. No
litter after any of the four runs, success or failure.

**Across criteria 1, 2 and 3 - nine product mux runs plus the before-repro - not one wedge occurred
in a command containing a copied video stream.** Every wedge was pass 1. That is the classification
the Implementation Lead asked for and it is the result that matters.

### Criterion 4 - the audio is still processed. PASS

Measured by me with `volumedetect` on 111731's content, on a run that **COMPLETED** (the valid
45,872,117-byte output from criterion 1 - not a partial file from a wedged run).

| | mean_volume | max_volume |
|---|---|---|
| the unprocessed capture (`mic_native.wav`) | **-75.1 dB** | **-38.3 dB** |
| the unprocessed mic track inside the fixture source | -75.1 dB | -38.3 dB |
| ONE-PASS reference: the same filter graph, no video stream, AAC 160k - run A | **-68.8 dB** | **-41.0 dB** |
| the same reference - run B (reproducibility) | **-68.8 dB** | **-41.0 dB** |
| **the TWO-PASS mux output (the fix)** | **-68.8 dB** | **-41.0 dB** |

- **Expected:** the two-pass output is equivalent to a one-pass reference, and is not simply the raw
  capture (a mux that completes because the filters stopped working is a fail).
- **Actual:** identical to the one-pass reference to 0.1 dB on both figures, and the reference
  reproduced exactly across two runs so the comparison is not measuring ffmpeg's nondeterminism. It
  is **not** the raw numbers: mean is 6.3 dB up (speechnorm lifting quiet speech) and peak 2.7 dB
  down (arnndn plus the limiter). The filters are still doing their work.

I used 111731's content for this, as the handoff advises, because 111433's processed level is not
reproducible between runs. I did not independently re-measure that 26 dB spread; it is the
developer's reason for the fixture choice and it does not bear on the result.

### Criterion 5 - every copied-video-plus-filtered-audio mux is covered. PASS

I did not grade the handoff's table of ten. I re-enumerated the engine from source, exhaustively, in
both directions:

**Every filter-graph option in `src/AgentEyes.Core` (7 sites):**

| site | builder | has a copied video stream? |
|---|---|---|
| `Thumbnails.cs:103` | waveform PNG | no input video at all |
| `Video/FfmpegArgs.cs:450` | `MixTwoWav` | no video at all |
| `Video/FfmpegArgs.cs:498` | `FilterMicToWav` - **pass 1** | **no** |
| `Video/FfmpegArgs.cs:514` | `MixMicSystemToWav` - **pass 1** | **no** |
| `Video/FfmpegArgs.cs:529` | `FilterSystemToWav` - **pass 1** | **no** |
| `Video/ComposeArgs.cs:106` | `CameraInset` | `-c:a copy` only; the video is `libx264` |
| `AlwaysOn/AlwaysOnArgs.cs:130` | `Capture` | live capture; the video is encoded |

**Every stream-copy value in `src/AgentEyes.Core` (5 sites):**

| site | builder | has a filter graph? |
|---|---|---|
| `Video/FfmpegArgs.cs:553` | `MuxVideoWithProcessedAudio` - **pass 2**, `-c:v copy` | **no** |
| `Video/FfmpegArgs.cs:588` | `BurnSubtitles`, `-c:a copy` | no |
| `Video/ComposeArgs.cs:110` | `CameraInset`, `-c:a copy` | yes, but on the video, which is encoded |
| `AlwaysOn/AlwaysOnArgs.cs:229` | `Join`, `-c copy` | no |
| `AlwaysOn/AlwaysOnArgs.cs:255` | `Trim`, `-c copy` | no |

- **Expected:** no ffmpeg command in this engine pairs a copied video stream with a filter graph.
- **Actual:** confirmed. The intersection is empty. The only `-c:v copy` is pass 2, and no
  `-filter_complex`, `-lavfi` or `-filter_complex_script` appears in that builder - which I also
  confirmed from the live command line in criterion 1 rather than only from the source.

The three builders that held the shape (`MuxVideoMixMicSystem`, `FilterVideoMic`,
`MuxVideoAddSystem`) are gone. Splitting the system-only path too, which the handoff calls a
judgement call, is the right one: the invariant is then machine-checkable with no exemption list.

### Criterion 6 - Windows and Linux still complete. PASS, with the handoff's limit restated

- **Linux:** build clean and `dotnet test` green here (120 passed, 1 skipped) and in CI (121).
- **Windows:** CI on PR #140 green - build succeeded, 0 Error(s), 2183 passed.
- **Did either ever have this shape fail?** Not on record, and I did not reproduce it on either.
- **The limit, which the handoff states and I am not softening:** no Windows or Linux *runtime* mux
  was measured by anyone. "Windows and Linux still complete" rests on unit tests plus the absence of
  a report, not on a measured mux. That is an honestly documented limit and it passes
  DEVELOPMENT_METHOD 6c item 6. It is also an argument for having split all three platforms' path
  rather than only macOS's, which this change does.

### Criterion 7 - a regression test that fails against today's code. PASS

Verified by re-adding the banned shape and watching it fail **by name in both Unix suites**:

```
AgentEyes.Tests.Shared.FfmpegMuxShapeTests.NoArgumentListPairsACopiedVideoStreamWithAFilterGraph [FAIL]
  a copied video stream is back in the same ffmpeg command as a filter graph (issue #136 ...)
mac:   Failed: 1, Passed:  78, Total:  79
linux: Failed: 1, Passed: 119, Skipped: 1, Total: 121
```

Details, including the #114 merge simulation, under check 4.

### Criterion 8 - gates green. PASS

**My own run, on the branch at `d51b2e8`** (the four-command macOS loop; `AgentEyes.sln` contains
the WPF app and cannot build on a Mac):

| gate | result |
|---|---|
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` | Build succeeded, **0 Error(s)** (1 pre-existing warning, `AlwaysOnEngine.cs:901`) |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | **Failed: 0, Passed: 79**, Skipped 0 |
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux` | Build succeeded, **0 Error(s)** |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | **Failed: 0, Passed: 120**, Skipped 1 |

Both counts re-run after every mutation was reverted, and identical. Inherited counts from
`issue-128-mic-chain-stall` are 72 and 113/1, so this branch is **+7** on each Unix suite and
reduces nothing.

**CI on PR #140 at `d51b2e8`, run 43629488269 - all three jobs green:**

| job | build | tests |
|---|---|---|
| windows | Build succeeded, **0 Error(s)** | **Failed: 0, Passed: 2183** |
| linux | **0 Error(s)** | **Failed: 0, Passed: 121** |
| macos | Build succeeded, **0 Error(s)** | **Failed: 0, Passed: 79** |

**The merge with `main`, verified by me rather than taken from the handoff.** In a throwaway
worktree at `d51b2e8`, `git merge origin/main`:

- **three conflicts, all the same trivial one** - `tests/AgentEyes.Tests.csproj`,
  `AgentEyes.Tests.Mac.csproj`, `AgentEyes.Tests.Linux.csproj`. Each side appended a
  `<Compile Include="../Shared/...">` at the same spot: #136's `FfmpegMuxShapeTests.cs` against
  `main`'s `CliDispatcherTests.cs` (#122). Resolution is **keep both**, nothing else.
- with that resolved, the merged tree is **clean and green on both Unix gates: Mac Failed: 0,
  Passed: 233; Linux Failed: 0, Passed: 274, Skipped: 1** - `main`'s 226/267+1 plus this branch's 7.

The worktree was removed; `git worktree list` shows only the main checkout.

### Criterion 9 - #128's guards still fire. DEFERRED BY THE IMPLEMENTATION LEAD - not required here

I did not require it and this report does not pass or fail on it. Her reasoning is on the issue and
is structural rather than convenient: this fix makes the mux complete, so there is nothing left on
this path for a watchdog to catch.

Recorded because I happen to have it, and it is stronger than the criterion asked for: **the
watchdog fired for real five times during my own measurements**, on the product path, on genuinely
stalled stages I did not arrange - three times on the 602 s fixture and twice on the 60 s one. Each
time the message named the stage, **which pass of it**, the frozen output point and the bound; the
process was killed at 61-63 s rather than spinning; and the cleanup left no intermediate and no
fragment at the final path. Both passes go through the unchanged `AudioMix.RunStage`, so a two-pass
mux is more watched than the single pass was, not less. The static half also holds: the 16
`MicChainStall*` and `Watchdog_*` guards pass (run on this Mac - see check 8).

---

## The eight checks the Implementation Lead asked for

### Check 1 - criterion 1 before and after. DONE

Both reported above, both produced by me, with the field fragment's matching byte count as
corroboration I did not create.

### Check 2 - criteria 2 and 3 at the mux level, through the real code path. DONE

Both driven through `RecordingService.FinalizePending` via `agenteyes package`, never a hand-typed
ffmpeg command. **Which video was paired with which WAV is stated explicitly in each criterion
above**, with duration, bitrate and byte size, because that is the mechanism.

### Check 3 - the atomic rename, with the host killed first. DONE - and paired both ways

Method: `agenteyes package` in a child process; **SIGKILL the managed host FIRST** so no `finally`
can run (which is what process death means), then its ffmpeg, then read the directory. Fixture: the
601.9 s / 321 MB video (110301) with 111731's 75.5 s mic track, so pass 1 is short and reliable and
the kill lands in the long pass 2.

**The BEFORE is mine, not the handoff's.** I mutated the rename out of the product - pass 2 writes
the final path directly again - rebuilt, and ran the same kill points. An absence on its own is a
broken instrument, so this is the paired presence:

| kill point | WITHOUT the rename (mutated) | WITH the rename (committed) |
|---|---|---|
| 0.45 s into pass 2 | `recording.mp4` **262,192 B - moov atom not found** | final path **ABSENT**; `recording.mp4.muxing.tmp` 524,336 B |
| 0.80 s into pass 2 | `recording.mp4` **23,330,864 B - moov atom not found** | final path **ABSENT**; `recording.mp4.muxing.tmp` 23,593,008 B |
| 1.2 s into pass 1 | - | final path **ABSENT**; `audio_processed.wav` 50,069,504 B in flight |
| 2.5 s into pass 1 | - | final path **ABSENT**; `audio_processed.wav` 106,692,608 B in flight |

Directory listing after the 0.80 s kill with the fix in place, quoted whole:

```
    -rw-r--r--@ 1 soren  wheel   14494208 audio_processed.wav
    -rw-r--r--@ 1 soren  wheel        755 manifest.json
    -rw-r--r--@ 1 soren  wheel  323017495 raw.mp4
    -rw-r--r--@ 1 soren  wheel   23593008 recording.mp4.muxing.tmp
--- is anything at the FINAL path?
    ABSENT - nothing at the final path
```

and after the same kill with the rename mutated out:

```
    -rw-r--r--@ 1 soren  wheel   14494208 audio_processed.wav
    -rw-r--r--@ 1 soren  wheel        755 manifest.json
    -rw-r--r--@ 1 soren  wheel  323017495 raw.mp4
    -rw-r--r--@ 1 soren  wheel   23330864 recording.mp4
--- is anything at the FINAL path?
    PRESENT: recording.mp4 23330864 bytes
    ffprobe: moov atom not found
```

- **Expected:** a death at any point leaves the final path either absent or holding a complete file.
- **Actual:** confirmed at four mid-flight kill points. The moov-less fragment at the final path -
  the 9,699,376 B and 35,913,776 B files the developer measured, and the 262,192 B and 23,330,864 B
  ones I measured on the mutant - **is now impossible**, and the fragment is accounted for at the
  temp rather than merely missing, so the check is not vacuous.
- One kill at 2.0 s landed **after** the whole mux finished (it takes about 2.3 s on this fixture),
  so the final path legitimately held a complete 601.900 s file. I am reporting that run as what it
  was - a completed mux, not a mid-flight kill - rather than counting it.

**One correction to the Lead's framing of this check.** She asked me to show "no intermediate is
orphaned". That is not what the fix claims and it is not what happens: a process death **does**
leave `audio_processed.wav` and `recording.mp4.muxing.tmp` in the recording directory, as the four
rows above show, because no `finally` runs. The handoff declares this plainly under "What this does
NOT cover". What is guaranteed is the **final path**, and what handles the orphans is the next
attempt. I verified that too, on the real orphans my own kills left:

```
[WARN] [AudioMix] MuxInTwoPasses: process mic audio removed a stale temp from an attempt that
       did not finish: <dir>/audio_processed.wav (14494208 bytes)
[WARN] [AudioMix] MuxInTwoPasses: process mic audio removed a stale temp from an attempt that
       did not finish: <dir>/recording.mp4.muxing.tmp (23593008 bytes)
```

after which that mux completed with a valid 601.900 s / **322,805,414 B** recording. Run against the
mutant's directory as well, the retry cleared the stale intermediate at WARN and **replaced the
moov-less 23,330,864 B fragment wholesale** with the same valid 322,805,414 B file. `PendingMux` was
still set in both manifests, so the retry was the product's own recovery path and not something I
arranged.

### Check 4 - the guard is real, and it will catch #114. DONE - verified, not believed

The guard (`tests/Shared/FfmpegMuxShapeTests.cs`, linked into all three suites) is a reflection
sweep over the compiled types: `DeclaredOnly | Static | Public | NonPublic` on `FfmpegArgs` (so every
partial in the build, which is why each platform's gate covers its own builders) and `ComposeArgs`,
invoking each builder with synthesized fixtures, bool parameters both ways, and **a builder it
cannot invoke FAILS the sweep rather than being skipped**. It inspects what each builder really
emits, matching the video-codec forms by prefix so `-c:v:0 copy` cannot spell past it.

**Re-adding the banned shape:** fails by name in **both** Unix suites, mac `Failed: 1, Passed: 78`
and linux `Failed: 1, Passed: 119` - the exact counts the handoff claims.

**The #114 claim, tested rather than trusted.** I took `MuxVideoAddMic` and
`MuxVideoAddMicAndSystem` verbatim from
`origin/issue-114-macos-audio-loss-and-frame-rate:src/AgentEyes.Core/Video/FfmpegArgs.cs` (lines
486-508 and 523-548), grafted them onto this branch to simulate the merge commit, and ran both
suites. The only adaptation was `MicChain(o, "[1:a]")` -> `MicChain(o)`, because the label overload
lives on #114's branch; the shape under test is untouched. The guard **named both of them and printed
the whole offending command**, in both suites:

```
FfmpegArgs.MuxVideoAddMic: -y -i fixture.mp4 -i fixture.mp4 -filter_complex [0:a]aeval=...,
  speechnorm=e=4:p=0.95,volume=1[m];[m]adelay=1500:all=1,alimiter=...[a]
  -map 0:v -c:v copy -map [a] -c:a aac -b:a 160k fixture.mp4
FfmpegArgs.MuxVideoAddMicAndSystem: -y -i fixture.mp4 -i fixture.mp4 -i fixture.mp4
  -filter_complex [0:a]aeval=...;[2:a]volume=0.7[s];[m]adelay=1500:all=1,anull[md];
  [md][s]amix=inputs=2:duration=longest:normalize=0,alimiter=...[a]
  -map 0:v -c:v copy -map [a] -c:a aac -b:a 160k fixture.mp4
mac:   Failed: 1, Passed:  78
linux: Failed: 1, Passed: 119
```

- **Expected:** the guard fails on #114's builders at the merge commit and names them.
- **Actual:** confirmed, with #114's real code. **This is the mechanism by which this change protects
  a branch it cannot see, and it works.** The handoff's mechanical split instructions for both
  builders are correct as far as I can check them statically.

**What the guard cannot see, from its own documentation and confirmed by my enumeration:** builders
on types other than `FfmpegArgs` and `ComposeArgs`. The three in `AlwaysOnArgs` are asserted
directly in the same file instead, and `Thumbnails.cs:103` is neither swept nor directly asserted -
it has no video input at all, so it cannot hold the shape, but it is outside the sweep. The guard
states its limits in the file, which is what 6c item 6 asks for.

### Check 5 - audio equivalence on a run that completed. DONE

Criterion 4 above. Measured on the valid 45,872,117-byte output, never on a partial file from a
wedged run - and the one-pass reference was run twice to show the comparison is stable.

### Check 6 - the mutation list, reproduced. DONE - all of it, plus two more

Every mutation applied to the product or test source, built, run, then reverted. The two
`MuxTwoPassTests` rows need the Windows-suite file, which does not compile into the Unix suites, so
**I linked `tests/AgentEyes.Tests/MuxTwoPassTests.cs` into the Mac suite temporarily and ran it
here** - it compiles and passes on macOS, **90 passed / 0 failed** (79 + its 11), which is itself a
result the developer only had from a scratch project.

| # | mutation | failed, by name | matches handoff? |
|---|---|---|---|
| M1 | re-added a single-pass `-c:v copy` + filter-graph builder | `FfmpegMuxShapeTests.NoArgumentListPairsACopiedVideoStreamWithAFilterGraph` - mac 1/78, **linux 1/119** | yes |
| M2 | `ProcessedAudioCodec` -> `pcm_s16le` | `FfmpegMuxShapeTests.TheTwoPassesTogetherStillCarryTheWholeCleanVoiceChain` | yes |
| M3 | the final-path cleanup (`if (!ok ...)`) removed | `MuxTwoPassTests.MuxInTwoPasses_AudioPassFails_...` **and** `..._MuxPassFails_...` - 2 failed | **no - see F1** |
| M4 | the intermediate deletion removed from the `finally` | **exactly 6 of 11** `MuxTwoPassTests`, and the six names match the handoff's list exactly | yes |
| M5 | the atomic rename removed | **exactly one** test: `MuxTwoPassTests.PassTwo_ReallyWritesTheMuxingTemp_AndNotTheFinalPath` (89/90) | yes |
| M6 | `-rf64 auto` removed from all three pass-1 builders | `FfmpegMuxShapeTests.EveryPassOneBuilderWritesTheIntermediateAndNothingElse` | yes |
| M7 | a bogus name added to `RequiredBuilders` | `NoArgumentListPairsACopiedVideoStreamWithAFilterGraph`: `Assert.Contains() Failure: Item not found ... "FfmpegArgs.QaBuilderThatDoesNotExist"` - **the broken-instrument assertion is live** | yes |

**And I tested the two limits the handoff declares as untestable, because a declared limit that is
not the real limit is still a wrong claim:**

| # | mutation | result | handoff's claim |
|---|---|---|---|
| M8 | the start-of-run stale-temp removal disabled | **all 90 green** | "no test can fail on it" - **accurate** |
| M9 | the muxing-temp deletion in the `finally` disabled | **all 90 green** | "no test can fail on it either" - **accurate** |

Both are declared in the handoff as a diagnostic and as belt-and-braces rather than counted as
coverage, with the reason written at the point of use in the code. That is an honest limit and it
passes 6c item 6. The renamed test (`...AreClearedAtTheStart` -> `...DoNotSurviveAndDoNotCorruptTheOutput`)
does describe what it actually proves.

**M5 deserves a note.** The handoff says the rename had no test that caught its removal until the
fourth attempt, and that the one that works makes the temp path a **directory** so the mux must
fail. I confirmed it is the only one of the 90 that fires. That is exactly the defect class
DEVELOPMENT_METHOD 6c exists for, found by the developer running the exercise rather than assuming
it, and declared.

**All mutations reverted, and the revert is proven rather than asserted** - sha256 of each mutated
file against `HEAD`'s blob:

```
IDENTICAL  src/AgentEyes.Core/Audio/AudioMix.cs
IDENTICAL  src/AgentEyes.Core/Video/FfmpegArgs.cs
IDENTICAL  tests/Shared/FfmpegMuxShapeTests.cs
IDENTICAL  tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj
```

`git status --porcelain` is empty and `git diff HEAD` is empty. The full gate was re-run on the
reverted tree and is green at 79 and 120/1.

### Check 7 - criterion 9 is deferred. HONOURED

Read, not required. See criterion 9 above; the watchdog firing five times on my own runs is recorded
as a bonus, not as a gate.

### Check 8 - the gate, CI, and the merge. DONE

Criterion 8 above: my own four-command gate, the three CI counts quoted, and the merge with
`origin/main` performed and tested by me. One gate fact needs the Lead's attention - F3 below.

---

## Findings for the Review Gate and for #128 round two

None of these fails this issue. All three are recorded so nobody rediscovers them as #136 defects.

### F1 - the handoff's "passes either way" admission is stale, and it UNDER-claims

The handoff says `MuxTwoPassTests.MuxInTwoPasses_MuxPassFails_RemovesTheIntermediateAndThePartialOutput`
"passes with or without the `if (!ok)` final-path block, because `RunStage` already removes pass 2's
output when pass 2 fails". **Measured: it does not. Under M3 it FAILS**, so M3 fails two tests, not
one.

The reason is the atomic-rename commit (`9200d3a`), which landed after that sentence was written.
Pass 2 now writes `<final>.muxing.tmp`, so `RunStage` removes the **temp** on failure, not the final
path - and that test plants a fragment at the final path with `File.WriteAllText(final, ...)` before
failing pass 2 (`tests/AgentEyes.Tests/MuxTwoPassTests.cs`, the `MuxPassFails` test). The `if (!ok)`
block is now the only thing that can remove it.

So the coverage is **better** than the handoff claims and the error is in the conservative direction -
an under-claim, not an overclaim, and it fails closed. It is a documentation inaccuracy in the proof
artifact, worth correcting so a later reader does not weaken a load-bearing block on the strength of
it. Not a defect in the change.

### F2 - three new unguarded `File.Delete` sites could mask a real error, if a second writer exists

`AudioMix.MuxInTwoPasses`'s `finally` deletes the intermediate (`AudioMix.cs:137`), the muxing temp
(`:142`) and the final path (`:152`), and `RemoveStaleTemp` deletes at `:167`. None is guarded. If
one throws - which needs another process touching the same paths - the exception propagating from
the `finally` replaces the real one, and a stalled-mux `UsageException` would surface as an
`IOException` naming nothing useful.

This is **exactly the class #128's QA already filed as D5** ("a `File.Delete` in `AudioMix.RunStage`
raced the other process and surfaced as an `IOException`, masking the real stall error"), and its
precondition is **#128's unfixed D1** - two processes muxing one recording, because
`RecordingWorkset` is in-process only and `Package.RunAsync` -> `FinalizePending` runs an uncounted
mux. In the single-process case it is clean: across my five observed pass-1 stalls the real stall
message surfaced correctly every time and no litter remained.

Worth stating because **#128 round two's fix to `RunStage` will not reach these four sites** - they
are in `MuxInTwoPasses`, a method that did not exist when D5 was filed. For #128 round two or a
follow-up, not for #136.

### F3 - a gate fact for the Implementation Lead, since she does the merge

Her rule is that the branch must contain `main`'s head with CI green before she merges. Two things
she needs:

1. **The branch does not contain `main`'s head.** `git merge-base --is-ancestor origin/main HEAD` ->
   **NO**; merge base is `df81375`; PR #140 reports `mergeable: CONFLICTING`. The merge is three
   trivial keep-both csproj conflicts and the merged tree is green at Mac 233 / Linux 274+1, which I
   verified (criterion 8) - so this is a step to take, not a problem to solve.
2. **Merging PR #140 to `main` as it stands would also land four #128 ROUND-ONE commits**, because
   this branch is cut from `issue-128-mic-chain-stall` at `ea7b66a`:

   ```
   953fdfc fix(audio): sanitize NaN/Inf at the head of the mic chain and fail a stalled ...  (#128)
   83528d3 fix(post-processing): kill orphaned post-processing ffmpeg, drop partial mux ...  (#128)
   123bb49 docs(cencon): handoff for #128 ...
   ea7b66a test(#128): stall test uses a 10 s limit and asserts the frozen point ...
   ```

   That is the round that went `flow:qa-failed`, with D1, D3 and D5 open. Her stated plan is that
   #128 round two and #136 land as one stack, so this is presumably intended - but the fifteen #136
   commits cannot reach `main` without those four, and I am not going to let that pass unsaid. It is
   her sequencing decision, not a defect in this change, and it is the reason I have not touched the
   merge.

### F4 - a smaller note, same family as F2

`AudioMix.ProcessedAudioFileName` is the fixed name `audio_processed.wav` in the recording's own
directory (`AudioMix.cs:98`), as is `<final>.muxing.tmp`. Two processes muxing the same recording
would therefore share both temps, and one's `finally` could delete the other's in-flight file. Same
#128 D1 precondition as F2. The placement itself is right - beside the output, on its volume, so the
publish is a rename and not a copy, and so a ten-minute intermediate cannot land on a smaller temp
disk.

---

## Method and standards review

- **The repository coding standards.** Enterprise logging is present and genuinely informative:
  `MuxInTwoPasses` logs entry with every path, the publish, each temp removal, and stale temps at
  **WARN** with their byte counts - which is what made check 3's retry evidence quotable. No fallback
  programming: nothing new swallows an error or substitutes a default. Try/`finally` with no `catch`,
  so nothing is hidden. No UI surface, so the responsiveness rule does not apply. The comments
  explain *why* at length and state the bounds they know about, including the 4 GB RIFF limit the
  developer went looking for and found in their own change.
- **ASCII only.** Every one of the 15 changed files is pure ASCII - zero non-ASCII bytes.
- **No prohibited attribution** in any commit message or file on this branch.
- **Heavy smokes correctly skipped.** The change has no UI, Control API or capture surface;
  `api-smoke.ps1` and `gui-smoke.ps1` would exercise nothing it touches. The runtime surface is the
  mux, and the mux is what I drove - nine times through the product's own path.
- **DEVELOPMENT_METHOD 6c, checks that fail open.** This change is unusually good on this. The guard
  fails on an empty sweep and on a missing required builder (M7 proves it); the rename test was
  rebuilt four times until it could fail (M5 proves it is the only one that does); and the two things
  no test can catch are declared rather than counted (M8 and M9 prove the declaration is accurate).
  My own checks were built to the same rule: the "nothing at the final path" result is paired with a
  mutated build that puts something there.
- **Privacy posture** untouched: no change to the recording indicator, pause, exclusions or disk
  bounds.

## What this verification does NOT cover

- **No end-to-end capture.** I recorded nothing, by instruction. The fresh-10-minute and
  fresh-60-second end-to-end runs with every manifest stage `done` belong to #114's post-merge
  re-verification, which is where the issue's own amendment puts them.
- **No Windows or Linux runtime mux.** CI unit tests only, on both. Same limit the handoff states.
- **The mixed and system-only paths were not run on long real material.** This machine has no
  virtual audio device, so a `sys_native.wav` cannot be produced here. I checked their argument
  shapes and ran their real-ffmpeg orchestration tests (`MuxVideoMixed_...`,
  `MuxVideoSystemOnly_...`, green on this Mac), not a ten-minute capture.
- **The deadlock is not reproduced by any test**, only by real material. If someone finds a different
  way to deadlock a mux that does not pair a copied video stream with a filter graph, nothing here
  catches it.
- **I did not compute a sample-by-sample difference** between the one-pass and two-pass audio. Mean
  and max agree to 0.1 dB over two reference runs; `pcm_f32le` is a reasoned claim about fidelity,
  not a measured bit comparison.
- **The 602 s mux timing is one completion in four runs.** 6.53 s against a 60 s watchdog bound is
  not a close call, but it is one sample. What I can show across all nine runs is that the
  muxing-queue deadlock never recurred, and that every failure was in pass 1 with no video stream in
  the command.
- **I did not investigate #128's D3 beyond classifying it.** My 5-of-8 wedge rate on long audio is
  consistent with the handoff's and with the Lead's nondeterminism finding; I did not count NaN
  frames myself, and per the Lead's instruction I treated no input as a negative control.

## Housekeeping

- **No reinstall.** `~/Library/LaunchAgents/com.agenteyes.recorder.plist` still carries
  `ProcessType Interactive`, verified after my run. The installed binary is untouched and
  `main`-based. I recorded nothing.
- **Nothing deleted under `~/Movies/AgentEyes`.** All six protected directories intact after my run:
  `2026-11-14_111731_video` 6 files / 93M, `2026-11-14_110301_video` 8 / 698M,
  `2026-11-14_111433_video` 8 / 67M, `2026-11-12_152556_video` 4 / 14M,
  `2026-11-14_085412_video` 7 / 26M, `2026-11-14_085801_video` 7 / 12M. All fixtures were rebuilt
  into a scratch directory; the originals were read only.
- **No ffmpeg of mine left running.** None was running when I started (the developer's 55-minute
  orphan pid 94897 had already gone) and `ps` shows none now. Nothing needed clearing. Two idle
  `/bin/zsh` processes from an earlier session (pids 97901/97905, 0.0% CPU, blocked on a `find`) are
  not ffmpeg and not consuming CPU; I left them alone.
- **Load when the timings were taken:** `2.02 1.92 2.25` rising to `2.12 2.26 2.31` on ten cores. No
  cores were pinned, unlike the developer's run, so my mux timings are if anything slightly faster
  than theirs on identical material - 1.084 s against 1.17 s at 76 s, 6.53 s against 6.71 s at 602 s.
- **Product code is at the committed state**, proven by hash above. No product code was changed by
  this verification except the mutations, which are reverted.

---

**VERIFIED - every acceptance criterion met, criterion 9 deferred by the Implementation Lead.
Handing back to the Review Gate (`flow:ready-gate`). I did not merge anything.**
