# Issue #128 - QA report (Windows)

QA seat: session 153 (AgentEyes One App - Worker - muxqa), SORENLAPTOP, Windows 11.
Branch `issue-128-mic-chain-stall`, head `ea7b66a` (PR #133). Date 2026-11-14.
Scope: criteria 1, 4 (Windows), 5, 6, 7, 8, 9, 10, 11, 12, 13. Criteria 2 and 3 are deferred by
the Implementation Lead and are not judged here.

## VERDICT: FAIL (flow:qa-failed)

The gate is green and a lot of this branch works: the watchdog fires on the real path, the
Windows job object plus the pid registry leave no orphan, and packaging no longer runs a 4th
mux. Five defects block the pass:

| # | Defect | Criterion |
|---|--------|-----------|
| D1 | Two ffmpeg writers on one `recording.mp4` at the same time (app + `agenteyes package`), and the second attempt is not counted | 12, 13 |
| D2 | A mux ended by process death (the restart case) leaves a truncated `recording.mp4` at the FINAL path. Later stages run on it, and the manifest stays at `running` forever | 11, check 4 |
| D3 | **The default chain still hangs on finite, NaN-free input on the shipped ffmpeg 9.0.2.** arnndn emits a whole NaN frame at end of stream and the head sanitizer cannot see it | PR core claim; 1/4 caveat |
| D4 | No test fails when the watchdog is removed from the product path (mutation F survives, 2164/0) | 7 |
| D5 | `RunStage`'s cleanup `File.Delete` can throw and replace the stall error in the manifest | 6 (minor) |

## Gate (run by QA on `ea7b66a`)

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded.   0 Error(s)   (22 warnings, pre-existing analyzer)
dotnet test  AgentEyes.sln -c Release   -> Passed!  - Failed: 0, Passed: 2164, Skipped: 0, Total: 2164
```

The 21 new tests ran by name: 16 in `MicChainStallTests` and 5 in `FfmpegChildRegistryTests`, all
Passed. `RunWatched_FilterThatStallsOnNan_FailsWithinTheBound` took 12 s.

A note on 2164 versus the 2297 quoted for main: the branch is 1 commit behind `origin/main`
(`af0ddb1`, #122/#129). `git merge-tree` shows it merges with no conflicts. 2143 + 21 = 2164
locally. CI runs the PR merge ref: 2297 + 21 = 2318. The rebase the lead's merge rule requires is
still outstanding.

Trap observed: `dotnet test tests\AgentEyes.Tests\AgentEyes.Tests.csproj` without
`-p:Platform=x64` picks the stale `bin\Release` test DLL from Sep 24, which contains none of the
new tests. All filtered runs here used `-p:Platform=x64`.

CI, PR #133, run 43620546957 (pull_request, head ea7b66a), all SUCCESS:

```
windows  Passed!  - Failed: 0, Passed: 2318, Skipped: 0, Total: 2318 - AgentEyes.Tests.dll
linux    Passed!  - Failed: 0, Passed:  268, Skipped: 0, Total:  268 - AgentEyes.Tests.Linux.dll
macos    Passed!  - Failed: 0, Passed:  226, Skipped: 0, Total:  226 - AgentEyes.Tests.Mac.dll
```

## Method of the live runs

- ffmpeg pinned to the shipped `9.0.2-essentials_build-www.gyan.dev` via `QA_RECORD_FFMPEG_DIR`.
  Unit tests use PATH, which here is 8.1.1-full.
- The installed app (idle, pid 3460) was stopped for the runs and relaunched afterwards (pid 22200).
- The stall injection is mutation A (sanitizer blanked) built into the branch app, run against a
  crafted `2026-11-14_130000_qa128` directory. Its `raw.mp4` holds 90 s of video plus float-PCM mic
  with ONE NaN at 30 s (`astats`: `Number of NaNs: 1`), and its `PendingMux` carries the shipped
  defaults.
- Process listings come from `Get-CimInstance Win32_Process` filtered to ffmpeg.exe /
  AgentEyesApp.exe / agenteyes.exe. Every listing prints an instrument check (for example
  "291 processes total visible to the query, e.g. 0 System Idle Process, 4 System, ..."), so an
  empty filtered result is a measurement, not a broken query.
- Both crafted directories were deleted afterwards (each was checked by its `qa128*` label
  first). The installed app has no watchdog and would hang on them. The registry folder was
  left empty.
- Raw evidence is in `qa-windows/` next to this file.

## Criteria: Expected vs Actual

### 1. Windows exposure established, with a number: PASS (as written), with a correction (D3)
- Expected: a >= 10 min default-settings Windows recording, plus the ffmpeg version, whether it
  completed, the time taken and the PostProcessing block.
- Actual: the developer's run is reported with all of these. My own branch run (criterion 4) also
  completed on 9.0.2.
- Correction: the handoff concludes "Windows dshow captures s16 PCM, which cannot carry a NaN -
  latent on Windows". That is incomplete. On 9.0.2, arnndn manufactures a NaN frame itself at end
  of stream on finite s16 input (D3). The shipped app (no sanitizer, no watchdog) would hang
  forever on such an input. Real dshow captures have so far escaped it: three of three 10-minute
  runs completed.

### 4. Windows: 10-minute default-settings recording completes: PASS (as written)
- Fixed branch app, `POST /record/start {"mode":"video","source":"mic","mic":"Microphone (2- Realtek(R) Audio)"}`.
  No denoise or level fields were sent, so the defaults apply. Stopped at 615 s.
- Log: `13:10:28.720 ffmpeg [process mic audio]: ... "[0:a]aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same,arnndn=..."`
  then `13:10:46.073 ffmpeg [process mic audio] ok` = **17.4 s**.
- Manifest: `"mux": {"State":"done","Attempts":1}`, `"thumbnail": {"State":"done","Attempts":1}`.
- ffprobe `recording.mp4`: video **617.800 s**, audio **616.889 s**, a gap of **0.91 s**
  (the bound is 3.0 s).
- Caveat: this proves a real capture completes. It does NOT prove every default-settings input
  completes (see D3).

### 5. The audio is still processed: PASS on controlled speech; NOT shown on live speech
- Live: I played TTS speech through the speakers for the whole 10-minute recording, but the
  laptop mic did not pick it up. Raw (`recording.original.mp4`): mean -91.0 dB, max -55.3 dB.
  Processed: mean -83.3 dB, max -48.4 dB. That is a silent room, so it cannot show denoise or
  leveling on speech. This is stated as a gap, not a pass.
- Controlled, using the product's exact chain on 9.0.2: TTS speech at -12 dB plus pink noise
  (amplitude 0.01), 138 s, trimmed to 6,624,000 samples (a multiple of 480, to avoid D3):

```
input  (speech+noise)           mean_volume: -32.1 dB  max_volume: -12.2 dB
sanitizer+arnndn only           mean_volume: -32.4 dB  max_volume: -12.2 dB
full fixed chain                mean_volume: -20.4 dB  max_volume:  -0.4 dB   (leveling +11.7 dB mean)
pre-fix chain                   mean_volume: -20.4 dB  max_volume:  -0.4 dB
md5 fixed = md5 pre-fix = 1763fe3953cae414c1bad873d1da6b72   (sanitizer is an identity on finite audio)
noise only (pink, 20 s)         mean_volume: -54.4 dB  max_volume: -41.6 dB
noise only after denoise        mean_volume: -74.5 dB  max_volume: -54.1 dB   (denoise removes 20.1 dB)
```

### 6. A hung stage fails instead of spinning, within a bound: PASS
- Unit level, mutation A, real `AudioMix.MixWavs` path:
  `AgentEyes.UsageException : ffmpeg stalled during mix mic+system: output stopped advancing at 30.0s of output for 60s (limit 60s) after 61s - process killed` [1 m 3 s].
- Real app, mutation A, attempt 2 (pid 8724 / ffmpeg 15332), log lines it WROTE:
```
12:40:51.793 [FfmpegChildRegistry] Register: pid 15332 [process mic audio] owner pid 8724
12:41:53.116 [ERROR] ffmpeg [process mic audio] ffmpeg stalled during process mic audio: output stopped advancing at 30.3s of output for 60s (limit 60s) after 61s - process killed. ...time=00:00:30.26 ... elapsed=0:01:00.94
12:41:53.149 [FfmpegChildRegistry] Unregister: pid 15332
```
- Real app, FIXED build, finite input (D3), a clean manifest:
```
13:12:35.674 Register: pid 19724 [process mic audio] owner pid 7424
13:13:39.025 [ERROR] ffmpeg stalled during process mic audio: output stopped advancing at 89.9s of output for 60s (limit 60s) after 63s - process killed
13:13:39.056 [AudioMix] RunStage: process mic audio did not complete - removed partial output ...recording.mp4
"mux": {"State":"failed","Attempts":1,"Error":"ffmpeg stalled during process mic audio: output stopped advancing at 89.9s of output for 60s (limit 60s) after 63s - process killed"}
```
- Bound observed: 61-63 s from ffmpeg start.

### 7. Regression tests: FAIL (D4)
- The chain shape tests and the stall test exist. Mutations A and B reproduce (see below).
- Mutation F (`AudioMix.RunStage` calls `Ffmpeg.Run` instead of `RunWatched`, AudioMix.cs:49)
  removes the watchdog from every product path, and the FULL suite stays green:
  `Passed!  - Failed: 0, Passed: 2164`. The test of the watchdog is decoupled from its wiring. The
  handoff calls `MixWavs_DefaultSettingsMicWithNanSample...` "the whole product path", but with
  the sanitizer present it never reaches the watchdog.
- `MixWavs_DefaultSettingsMicWithNanSample...` (MicChainStallTests.cs:234) uses 90 s =
  4,320,000 samples, an exact multiple of 480. That is exactly the length that avoids arnndn's
  own NaN frame (D3). At 90.01 s the same test would hang until the watchdog fires, with the fix
  in place.
- Under mutation B, the stall test fails by name after 60 s, but its ffmpeg is never killed
  (MicChainStallTests.cs:201). The orphan (pid 22312, 590 s of CPU, parent already exited) kept
  `dotnet test` from returning until I killed it by hand. "Fails by name instead of hanging the
  suite" is therefore only half true.

### 8. Gates: PASS
See "Gate" above. Windows local: 0 errors and 2164/0. CI: 2318/0, 268/0, 226/0.

### 9. No orphan survives the stage being abandoned: PASS (Windows)
```
--- BEFORE (12:40:55.361)
  8724 ppid   4596 AgentEyesApp.exe  ... --tray
 15332 ppid   8724 ffmpeg.exe        cpu 4.2s  ...ffmpeg.exe -hide_banner -progress pipe:1 -stats_period 1 -y -i ...2026-11-14_130000_qa128\raw.mp4 -filter_complex [0:a]arnndn=...
--- AFTER watchdog fired (12:42:13.986)
  8724 ppid   4596 AgentEyesApp.exe  ... --tray
instrument check: 287 processes total visible to the query
pid 15332 alive? False      registry records: (none)
```

### 10. No orphan survives a service restart: PASS (Windows)
Hard restart (`Stop-Process -Force`) during a live stall:
```
--- BEFORE hard restart (12:39:42.949)
 22000 ppid  14160 AgentEyesApp.exe  cpu 1.5s  ...AgentEyesApp.exe --tray
   336 ppid  22000 ffmpeg.exe        cpu 9.2s  ...-progress pipe:1 -stats_period 1 -y -i ...qa128\raw.mp4 ...
record: 336.json {"Pid":336,"StartUtc":"2026-11-14T17:39:34.342745Z","OwnerPid":22000,...,"Label":"process mic audio"}
manifest mux: {"State":"running","Attempts":1}
Stop-Process -Id 22000 -Force at 12:39:54.300
--- AFTER (12:39:57.371)
(none of the three names running)
instrument check: 289 processes total visible to the query
pid 336 alive? False        record left behind: 336.json
next start: 12:40:31.557 ReapOrphans: pid 336 [process mic audio]: already exited (or pid reused) - record removed
```
On Windows the job object kills the child first, so the run above never exercises the registry's
kill path. I proved that path separately in the real app's start hook. I started an ffmpeg outside
the job (pid 3740) and planted a record naming the dead owner pid 22000, then started the fixed
app:
```
--- BEFORE (13:12:13.985)   3740 ppid 18852 ffmpeg.exe cpu 8s ... -f lavfi -i sine=f=200:r=48000 -f null -
13:12:15.455 [pid 7424] ReapOrphans: pid 3740 [QA planted orphan]: ORPHAN (owner pid 22000 gone) - killed
--- AFTER (13:12:18.856)    7424 AgentEyesApp.exe only; planted pid 3740 alive? False
```
Not verified: macOS and Linux hardware (a stated gap, as in the handoff).

### 11. Retries bounded and visible: FAIL (D2)
- The bound itself holds. Attempts counted at start were 1 (L1, hard-killed), 2 (L2, watchdog),
  3 (L3, hard-killed). L4 ran no 4th mux: `FindUnfinished: ... needs thumbnail, package` with no mux.
- But after a hard kill on the last attempt the recording is left saying `running`, with no
  error, permanently:
  `"mux":{"State":"running","Attempts":3,"LastAttemptUtc":"2026-11-14T17:43:02.9337767Z"}`.
  That is not "a state that says what happened".
- Also on L4: `RefuseExhaustedMux` reports `Last error: (no error recorded)` for the same reason.

### 12. One writer per output path, ever: FAIL (D1)
App attempt 2 was stalling. Then `agenteyes package <dir>` was run from the same build:
```
--- BEFORE second attempt (12:40:55.361): one ffmpeg, 15332 ppid 8724 (app)
--- AFTER CLI package started (12:41:11.494)
 15332 ppid   8724 ffmpeg.exe   ...
 15088 ppid   4424 agenteyes.exe ... package C:\Users\soren\Videos\AgentEyes\2026-11-14_130000_qa128
 12196 ppid  15088 ffmpeg.exe   ...
pid 15332 ppid 8724  output-arg: ... -b:a 160k C:\Users\soren\Videos\AgentEyes\2026-11-14_130000_qa128\recording.mp4
pid 12196 ppid 15088 output-arg: ... -b:a 160k C:\Users\soren\Videos\AgentEyes\2026-11-14_130000_qa128\recording.mp4
manifest mux: {"State":"running","Attempts":2}     <- the second attempt was not counted
```
Two live writers on one output path. There is no enforcement across processes:
`RecordingWorkset` is an in-process `ConcurrentDictionary` (RecordingWorkset.cs:224), and the
registry's reaper deliberately leaves a child alone while its owner lives. In-process, the GUI's
walkthrough button IS refused by the claim (MainWindow.xaml.cs PackageDirAsync, TryClaim Stage),
so the hole is between processes: CLI vs app, and on macOS/Linux any second service instance.

### 13. A hung attempt counts as an attempt: FAIL on the package path (D1)
- Automatic path: PASS. `NoteStarted` comes before the work (PostRecording.cs:367), and the
  manifest went 1 -> 2 -> 3 across a hard kill, a watchdog abandonment and a hard kill.
- Package path: the CLI's mux was abandoned by the watchdog
  (`[error] ffmpeg stalled during process mic audio: ... after 61s - process killed`), and
  `Attempts` stayed at 2. `Package.RunAsync` (Package.cs:112-113) runs `FinalizePending` without
  `NoteStarted`, so below the ceiling every `agenteyes package` and every walkthrough rebuild is
  an uncounted mux attempt.

## The five checks the lead asked for

1. **The watchdog fires on the real path.** YES. Quoted under criterion 6: 61 s (mutation A,
   real app) and 63 s (fixed build, finite input), with the Register / ERROR / Unregister lines.
2. **Orphan and restart.** YES on Windows, both with before and after listings (criteria 9 and 10),
   plus a live proof of the registry kill path in the real start hook. macOS and Linux remain
   unverified.
3. **Criteria 12 and 13.** 12 FAILS live (two writers). 13 holds on the automatic path and fails
   on the package path.
4. **The two bypasses.** Bypass 2 (an uncounted 4th mux from packaging) is CLOSED at the
   ceiling. Live on L4: `[Package] RefuseExhaustedMux: the audio mux for 2026-11-14_130000_qa128 failed 3 time(s) and is not retried automatically (limit 3)`.
   It is still open below the ceiling (D1). Bypass 1 (a fragment at the final path) is closed
   only when the stage fails in-process. After a hard kill, an 18,350,128-byte moov-less
   `recording.mp4` remained. The next start planned `needs mux, thumbnail, package`, and once the
   mux was out of attempts the thumbnail stage ran on the fragment twice:
   `ffmpeg [poster thumbnail] failed ... moov atom not found` (12:43:51.547 and 12:43:51.737).
   That burned all 3 thumb attempts and 1 transcribe attempt.
5. **The audio is still processed.** YES on controlled speech (+11.7 dB leveling, -20.1 dB noise).
   The live speech capture failed to reach the mic, and that is stated.

## Mutation reproduction

| Mutation | Developer's list | QA result |
|----------|------------------|-----------|
| A: `string finite = "";` (FfmpegArgs.cs:397) | 4 failed | **4 failed, same names**: `MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan` [1 m 3 s, stall error at 61 s], `FilterVideoMic_DefaultSettings_SanitizesNonFiniteSamplesBeforeArnndn`, `MicChain_AllMixPaths_CarryTheSanitizer`, `MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm`. `Failed: 4, Passed: 17, Total: 21` |
| B: `RunWatched => RunCore(args, label, null)` (Ffmpeg.cs:29) | 1 failed | **1 failed, same name**: `RunWatched_FilterThatStallsOnNan_FailsWithinTheBound` "RunWatched was still running after 60 s - the stall guard did not fire". `Failed: 1, Passed: 20, Total: 21`. The run hung until QA killed the leaked ffmpeg |
| F (new): `RunStage` uses `Ffmpeg.Run` (AudioMix.cs:49) | - | **SURVIVES: full suite `Failed: 0, Passed: 2164`** |

Reverted and proven: `git diff --stat` was empty and `git status --short` was empty, then a
rebuild gave `0 Error(s)` and `Passed!  - Failed: 0, Passed: 2164` on `ea7b66a`.

What the tests do NOT catch:
- The watchdog's wiring into AudioMix (mutation F).
- arnndn's own end-of-stream NaN (D3). The NaN test is pinned to a multiple-of-480 length.
- A mux killed by process death leaving a fragment, and downstream predicates trusting it (D2).
- Concurrent writers across processes (D1), and uncounted mux attempts from `Package.RunAsync`.
- `KillOnCloseJob.Assign` for watched children (Ffmpeg.cs): removing it would leave every test green.
- The cleanup `File.Delete` throwing and masking the stall error (D5).

## Defects (for the Developer Agent)

**D1 - criteria 12 and 13. Second writer, and an uncounted attempt, through `Package.RunAsync`.**
`src/AgentEyes.Core/Package.cs:112-113` calls `RefuseExhaustedMux` then
`RecordingService.FinalizePending`. Below the ceiling this starts a mux with no
`PostRecordingState.NoteStarted` and no claim that crosses processes
(`RecordingWorkset.cs:224` is in-process only).
Repro: start a mux in the app, then run `agenteyes package <dir>` while it runs. Two ffmpeg
processes write `<dir>\recording.mp4`, and `Attempts` does not move.
Needed:
- a per-recording cross-process exclusive lock held for the duration of any mux (for example an
  exclusive-share lock file in the recording directory), with the second caller refused or waiting;
- every mux attempt counted, whatever the path.

**D2 - criterion 11 and bypass 1. The fragment survives process death, and `running` is never reconciled.**
`src/AgentEyes.Core/Audio/AudioMix.cs:44-60` cleans up in `finally`, which cannot run when the
process is killed. `Thumbnails.cs:172` and `TranscriptionBacklog.cs:81` accept any
`recording.mp4` even while `PendingMux != null`.
Repro: hard-kill the app during the mux on attempt 3 (or on any attempt), then start the app. The
thumbnail stage runs on the fragment ("moov atom not found"), and the mux stays
`State: running` with no Error.
Needed:
- mux output written to a temporary name and renamed on success (so the final path is only ever
  a complete file), or downstream predicates that treat `PendingMux != null` as "no final media";
- at start, an interrupted attempt (`running` with no live owner) recorded as `failed` with an
  "interrupted" error.

**D3 - blocking, the PR's core claim. arnndn on ffmpeg 9.0.2 emits a NaN frame at end of stream on finite input.**
The sanitizer (`FfmpegArgs.cs:397`) sits before arnndn by necessity, so it cannot remove it, and
speechnorm then spins at the end.
Measured on the shipped `9.0.2-essentials_build-www.gyan.dev`:
```
input noisy.wav (s16, 138.51 s, TTS speech + pink noise)   Number of NaNs: 0
after aeval sanitizer + arnndn                             Number of NaNs: 480   (one 10 ms frame)
fixed chain / sanitizer+arnndn+speechnorm / pre-fix chain  rc=124 killed at 45 s, out_time frozen at 00:02:18.500000 of 02:18.511
speechnorm alone                                           completes, 0 s
same file, ffmpeg 8.1.1: arnndn NaNs 0; fixed chain completes in 4 s
length sweep, 9.0.2 (samples mod 480 -> NaNs): 0->0, 1->480, 50->480, 100->480, 200->480, 300->0, 400->480, 470->0, 479->0
pure 300 Hz tone, 480123 samples: NaNs 480
```
Product path, fixed build: a finite s16 recording of 4,320,500 samples (90.01 s, remainder 20)
failed with `ffmpeg stalled during process mic audio: output stopped advancing at 89.9s of output ... after 63s`.
It will fail all 3 attempts identically, so the user gets no final `recording.mp4` (the raw is
kept).
The real 10-minute dshow capture completed even though its sample count (27,204,408, remainder
408) is not a multiple of 480. So the trigger depends on content as well as length, and how often
real captures hit it is not established.
Reproduce with:
```
ffmpeg -f lavfi -i "sine=f=300:r=48000:d=11" -af "atrim=end_sample=480123,arnndn=m=<bd.rnnn>,astats=measure_perchannel=none" -f null -
```
This may also bear on #136. This QA seat has not verified that.
Needed: a fix that holds for finite input on the shipped ffmpeg. Options include sanitizing after
arnndn too (but the issue forbids quiet degradation, so prove the frame is only the terminal
padding), padding or trimming the mic to a 480-sample multiple before arnndn, or another approach
with a test at a non-multiple length.

**D4 - criterion 7. Test gaps.** Mutation F survives the full suite. The NaN product test is
pinned to a multiple-of-480 length. The stall test leaks its ffmpeg on timeout
(MicChainStallTests.cs:201). Add a test that fails when the AudioMix stages are unwatched (for
example an injectable stall timeout plus a stalling input through `AudioMix.RunStage`), and kill
the process on timeout.

**D5 - minor. Exception masking in `RunStage`.** `AudioMix.cs:56` `File.Delete` in `finally`
threw `IOException: The process cannot access the file ...recording.mp4 because it is being used by another process`.
That replaced the stall error, and the manifest recorded the IOException instead of the stall.
A cleanup failure must not discard the original exception.

## Left behind on this machine
- Recording `C:\Users\soren\Videos\AgentEyes\2026-11-14_130010_video` (the 10-minute criterion 4
  run, a silent room) is kept as evidence.
- The installed app is running again (pid 22200). There is no ffmpeg running and no registry
  records.
