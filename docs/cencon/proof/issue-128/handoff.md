# Issue #128 - Developer handoff (Windows)

Branch: `issue-128-mic-chain-stall`. Machine: Windows 11 (SORENLAPTOP).

## The answer first: Windows exposure

**A Windows recording with default mic settings does NOT hang in post-processing today. The stall
is not ffmpeg-version-specific: it is INPUT-specific, and the Windows ffmpeg hangs exactly the same
way when it is given the trigger.**

| What | ffmpeg | Result |
|---|---|---|
| Shipped app v1.11.11 (commit 01ada18; MicChain / AudioMix / Ffmpeg.Run identical to main df81375 on this path), Control API `/record/start {mode:video, source:mic, mic:"Microphone (2- Realtek(R) Audio)"}`, no denoise/level fields = defaults on, **622.9 s** | bundled `9.0.2-essentials_build-www.gyan.dev` - the SAME version as the macOS measurement | **Completed.** `process mic audio` ran `[0:a]arnndn=m='...bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];[m]alimiter=limit=0.95:level=false[a]`, start 11:14:21.535, `ok` 11:14:47.903 = **26.4 s**. ffprobe: video 622.900 s, audio 622.033 s. |
| Same chain, direct, 180 s real speech | 9.0.2 | completes, 6 s |
| Same chain, 720 s real speech (48 kHz mono) | 9.0.2 | completes, 17 s |
| Same chain, 600 s digital silence / pink noise / -80 dB tone | 9.0.2 | all complete |
| `speechnorm=e=4:p=0.95` on a tone with **ONE NaN sample** at 30 s | 9.0.2 and 8.1.1 (full build) | **HANGS forever** (killed at 40 s); output frozen at the sample before the NaN; `-progress` keeps printing with `out_time_us=29930667` frozen |
| Same with +Inf instead of NaN | 9.0.2 | completes |
| arnndn / alimiter / volume alone on NaN input | 9.0.2 | complete |
| arnndn on a tone with ONE NaN sample | 9.0.2 | completes, but **every sample after the NaN is NaN** (2,880,000 of 4,320,000) - its recurrent state is poisoned |

Shipped-run manifest, final `PostProcessing` block (recording `2026-11-14_110358_video`):

```
"mux":       { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:21.5147713Z" }
"thumbnail": { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:48.3445892Z" }
"package":   { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:58.7797243Z" }
"plugins":   { "State": "done", "Attempts": 1, "LastAttemptUtc": "2026-11-14T16:14:58.7980342Z" }
```

Caveat on that run: the room was quiet (mic mean -87.3 dB raw, -79.5 dB processed), so it proves
the shipped path completes on a 10-minute Windows capture, not much about speech.

**Why the macOS bisect looks the way it does** (consistent with all of the above, NOT verified on a
Mac - I have no macOS capture): arnndn alone is "fine" because it emits NaN harmlessly; speechnorm
alone is fine because the raw capture is finite; together, one NaN reaching arnndn (or produced by
it) becomes NaN-forever, and speechnorm spins on the first one. Windows dshow captures s16 PCM,
which cannot carry a NaN - which is why Windows is not exposed. The macOS seat can confirm the
trigger in one command on the failing `raw.mp4`:

```
ffmpeg -i raw.mp4 -af "aformat=sample_fmts=flt,astats=measure_perchannel=none" -f null - 2>&1 | grep "Number of NaNs"
ffmpeg -i raw.mp4 -af "arnndn=m=<model>,astats=measure_perchannel=none" -f null - 2>&1 | grep "Number of NaNs"
```

Non-zero on the first = the capture carries NaN. Zero on the first and non-zero on the second =
arnndn produces it on that platform (then the fix below converts the hang into a reported failure,
not into silence - see "What the fix does NOT do").

Severity: **latent on Windows** (same ffmpeg, one NaN = an unbounded hang, nothing notices), live on
macOS.

**#113:** different failure (exit 183 on an unreadable `raw.mp4` with no moov atom). Nothing here
changes that diagnosis. The one bearing: the `process mic audio` stage that failed there is now run
under the stall watchdog, so it can no longer hang unbounded either.

## What changed

1. `src/AgentEyes.Core/Video/FfmpegArgs.cs` - `MicChain` prepends
   `aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same` (const `FiniteSamples`) as the
   FIRST stage on the mic input whenever denoise or level is on. It must be before arnndn, because
   arnndn turns one NaN into NaN-forever. Per channel (`ch`), layout kept (`c=same`). Applies to all
   three mic paths: `FilterVideoMic`, `MixTwoWav`, `MuxVideoMixMicSystem`.
2. `src/AgentEyes.Core/Video/FfmpegProgressWatchdog.cs` (new) - pure, clock-injected decision:
   stalled when `out_time_us` has not ADVANCED for longer than the limit (progress lines that keep
   arriving with a frozen output time are NOT liveness - that is exactly the hang).
3. `src/AgentEyes.Core/Video/Ffmpeg.cs` - new `RunWatched(args, label, stallTimeout)`: adds
   `-progress pipe:1 -stats_period 1`, feeds stdout to the watchdog, and on a stall kills the process
   tree and throws `UsageException("ffmpeg stalled during <label>: output stopped advancing at X s of
   output for N s (limit L s) after T s - process killed")`. `Run` is unchanged in behaviour.
   `PostProcessStallTimeout` = 60 s. `-stats_period` needs ffmpeg 4.4 or newer; a Linux distro ffmpeg
   older than that would reject the option and fail the stage loudly (not hang).
4. `src/AgentEyes.Core/Audio/AudioMix.cs` - the four audio post-processing stages
   (`mix mic+system`, `mux video+mic+system`, `mux video+system`, `process mic audio`) use
   `RunWatched`. Sparse-output stages (scene extract, thumbnails) deliberately keep plain `Run`: a
   frozen output time is normal for them.
5. `src/AgentEyes.Core/Video/FfmpegChildRegistry.cs` (new), wired into `Ffmpeg.RunWatched` and
   `RepairService.Start`; on Windows `RunWatched` children also join `KillOnCloseJob` - criteria 9/10.
6. `AudioMix.RunStage` deletes the partial output of a pass that did not complete;
   `Package.RefuseExhaustedMux` stops packaging from re-running a mux past its ceiling - criterion 11.
7. Tests: `MicChainStallTests` (16), `FfmpegChildRegistryTests` (5); the three new file-write call
   sites are pinned in `ManifestWriterIlTests`.

**Defaults are unchanged** (denoise and level still on). **The sound is unchanged:** on 720 s of
NaN-free speech, the pre-fix chain and the fixed chain produce bit-identical output
(`-f md5` of both = `64fd8535cdfa013d240e97de0cbdff8e`) - the sanitizer is an identity on finite
audio.

**What the fix does NOT do:** if a platform's arnndn ITSELF produces NaN from finite input, the
sanitizer at the head cannot help (it sits before arnndn by necessity). In that case the run now
FAILS after 60 s with the stall error above instead of hanging - loud, not a quiet fallback. I did
not add a second sanitizer after arnndn on purpose: it would turn that case into minutes of silence
with a "done" status, which is the quiet degradation this repo forbids.

## Acceptance criteria

| # | Criterion | What I did | Status |
|---|---|---|---|
| 1 | Windows exposure, with a number | Above: 622.9 s shipped-app recording, defaults, ffmpeg 9.0.2, completed in 26.4 s, manifest block quoted | Done (Windows) |
| 2 | Linux exposure | NOT done - no Linux machine in this seat. The mechanism above says what to look for: whether the Linux capture (Pulse) can carry NaN. Same two astats commands apply. | Open for the Linux seat |
| 3 | macOS completes | NOT done - no Mac here. The fix is shared code; QA on macOS must run it. | Open for the macOS seat |
| 4 | Same on Windows / Linux | Windows: see "Branch run" below. Linux: open. | Windows done |
| 5 | Audio still processed | See "Processing still happens" below | Done (Windows) |
| 6 | Stall now fails, within a bound | Injected the pre-fix chain (mutation A below) into the real `AudioMix.MixWavs` path with a NaN-bearing mic: failed after 61 s with `ffmpeg stalled during mix mic+system: output stopped advancing at 30.0s of output for 60s (limit 60s) after 61s - process killed`. Unit-level: 4 s limit fails in well under 30 s. Bound = 60 s of frozen output + up to ~1.5 s (1 s progress period + 0.5 s poll). | Done |
| 7 | Regression tests | 21 tests, both kinds, including the stall itself in ~12 s. Mutation evidence below. | Done |
| 8 | Gates | Below | Windows done; Unix via CI |
| 9 | No orphan survives the stage being abandoned | Watchdog kills the process tree; product run below shows the pid gone after the failure | Done (Windows) |
| 10 | No orphan survives a service restart | Windows: post-processing children now in the kill-on-close job, hard restart during a live stall, ps before/after below. All platforms: pid registry reaped at start (real-process test) | Done (Windows); the registry kill path is unverified on macOS/Linux hardware |
| 11 | Retries bounded and visible | Existing bound of 3, plus two bypasses found and closed; product run to the bound below | Done (Windows) |

### Branch run (criterion 4, Windows)

Branch CLI `agenteyes video --screen 1 --mic "Microphone (2- Realtek(R) Audio)" --seconds 620`
(defaults: denoise + level, gate off), ffmpeg pinned to the shipped 9.0.2 via `QA_RECORD_FFMPEG_DIR`.
The CLI runs the same `AudioMix.ProcessVideoMic` -> `Ffmpeg.RunWatched` path as the app.

- Log: `ffmpeg [process mic audio]: ... "[0:a]aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same,arnndn=m='...bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];[m]alimiter=limit=0.95:level=false[a]" ...`
  at 11:43:35.673, `ok` at 11:43:52.164 = **16.5 s** for 10m22s.
- CLI: `[ok] recording.mp4 (10m22s, 23.7 MB)`.
- ffprobe recording.mp4: video **622.100 s**, audio **621.318 s** (0.78 s apart; the bound is 3.0 s).
- Quiet room again (mean -90.3 dB raw, -80.3 dB processed), so criterion 5 is shown on speech below.

### Criteria 9, 10, 11 - product runs on Windows

Setup: a recording directory crafted in `Videos\AgentEyes` whose `raw.mp4` holds 90 s of video plus
float-PCM mic audio with ONE NaN sample at 30 s (the trigger), and a `PendingMux` with the shipped
defaults. The branch app was built with the pre-fix chain (mutation A - the "ready-made stall" the
issue names) and launched `--tray` with ffmpeg pinned to 9.0.2. The installed app was stopped for
the duration (it has no watchdog and would have hung on that directory), then relaunched; the
crafted directory was removed. Log extract: `stall-run-log.txt`; final manifest:
`stall-recording-manifest.json` (both in this folder).

**The retry policy before this change:** the mux is already bounded - `PostRecordingState.MaxMuxAttempts = 3`,
counted at START (`PostRecording.cs:363-368`), with `PostRecordingPlan.NeedsMux` stopping at 3. Every
attempt on a Mac hung forever, so a restart (or the 15-minute repair tick) is what started the next
one. That's the "Attempts 2 within seconds" the Mac QA saw. This run found two bypasses, now closed:
1. A killed/failed mux left a truncated `recording.mp4` (no moov atom) at the FINAL path. Later
   stages test for that file, so after attempt 3 the thumbnail stage ran on it (`moov atom not found`)
   and the package stage ran too.
2. `Package.RunAsync` calls `RecordingService.FinalizePending` itself - an UNCOUNTED mux - so the
   package stage started a 4th mux (`finalize: begin mux video src=mic` at 11:54:53, Attempts still 3).

**Orphans and Windows (the KillOnCloseJob question):** on main, `KillOnCloseJob` is used ONLY by
`AlwaysOn/ContinuousRecorder.cs:174`. `Ffmpeg.Run` children - every post-processing ffmpeg - were NOT
in it on Windows either, so a Windows mux child would also have survived a crashed app (Windows does
not kill children with their parent). On macOS/Linux `KillOnCloseJob` is a stub that throws
`NotOnMac`/`NotOnLinux`. So no platform tied post-processing children to the app. That is the
mechanism behind the Mac orphans (with the infinite stall supplying a long-lived child), and it
belongs on #119's gate list: the job object is Windows-only by construction, and the registry in
this change is the cross-platform replacement for post-processing.

Criterion 10 - hard restart (force-kill) during a live stall:

```
--- BEFORE restart, 11:49:13.082
 23028 ppid  19820 AgentEyesApp.exe cpu     2.2s  "...\bin\x64\Release\...\AgentEyesApp.exe" --tray
 21268 ppid  23028 ffmpeg.exe       cpu    10.5s  "...\ffmpeg.exe" -hide_banner -progress pipe:1 -stats_period 1 -y -i ...\2026-11-14_120000_st...
record: 21268.json {"Pid":21268,"StartUtc":"2026-11-14T16:49:03.9072245Z","OwnerPid":23028,"OwnerStartUtc":"2026-11-14T16:48:42.0471445Z","Label":"process mic audio"}

Stop-Process -Id 23028 -Force

--- AFTER force-kill of the app, 11:49:25.344
no ffmpeg.exe or AgentEyesApp.exe process exists
pid 21268 alive? False
(positive listing, so the query works: 7600 AggregatorHost, 19084 AnalyticsSrv, 3964 AppActions, ...)
record left behind: 21268.json
```

Next start, before any repair pass:
`11:49:37.722 [FfmpegChildRegistry] ReapOrphans: pid 21268 [process mic audio]: already exited (or pid reused) - record removed`.
On Windows the job object got there first. On macOS/Linux the same record would be found with the
pid alive and its start time matching, and killed. `ReapOrphans_RealOrphanedProcess_IsKilled` proves
that kill path with a real process, but not on a Mac or Linux box.

Criterion 9 - the stage abandoned by the watchdog:

```
11:49:57.965 [FfmpegChildRegistry] Register: pid 14140 [process mic audio] owner pid 18792
11:51:00.383 [ERROR] ffmpeg [process mic audio] ffmpeg stalled during process mic audio: output stopped advancing at 30.3s of output for 60s (limit 60s) after 62s - process killed. ...time=00:00:30.26 ... elapsed=0:00:57.89
11:51:00.407 [FfmpegChildRegistry] Unregister: pid 14140
--- after the watchdog fired, 11:51:11
 18792 ppid  14344 AgentEyesApp.exe
pid 14140 alive? False
```

Criterion 11 - run to the bound on the final code (one app launch per attempt, each force-killed
after its attempt failed, then a 4th launch):

```
launch 1 pid 4064  at 12:01:50 -> 12:03:17 mux attempts=1 state=failed; recording.mp4 exists=False; ffmpeg running=False
launch 2 pid 11648 at 12:03:18 -> 12:04:46 mux attempts=2 state=failed; recording.mp4 exists=False; ffmpeg running=False
launch 3 pid 18628 at 12:04:47 -> 12:06:14 mux attempts=3 state=failed; recording.mp4 exists=False; ffmpeg running=False
launch 4 pid 2728  at 12:06:15, waited 60 s:
  12:06:35.877 [PostRecordingPlan] FindUnfinished: root=C:\Users\soren\Videos\AgentEyes, unfinished=0
  --- 12:07:15 process listing (ffmpeg + AgentEyesApp):
    2728 ppid  22068 AgentEyesApp.exe
  recording.mp4 exists=False
(each attempt also logged e.g. 12:03:14.250 [AudioMix] RunStage: process mic audio did not complete - removed partial output ...\recording.mp4)
```

Final `PostProcessing` block:

```
"mux": { "State": "failed", "Attempts": 3, "LastAttemptUtc": "2026-11-14T17:05:07.6125333Z",
         "Error": "ffmpeg stalled during process mic audio: output stopped advancing at 30.3s of output for 60s (limit 60s) after 62s - process killed" }
```

No thumbnail or package entry: with no fragment at the final path, nothing downstream mistakes the
recording for finished. Worst case per recording is now 3 x ~62 s of one core, then nothing more
automatically, with the reason in the manifest. `RefuseExhaustedMux` covers the explicit
`agenteyes package <dir>` path, which would otherwise still run an uncounted mux.

Not changed, flagged: the manifest is the only place this failure is visible. Nothing surfaces it in
the UI or `/status`. That reporting gap is outside this issue's criteria.

### Processing still happens (criterion 5)

On 720 s of real speech (a Windows capture) with pink noise mixed in at a known level
(`anoisesrc=color=pink:amplitude=0.01`), 0.5 s window-RMS percentiles:

| File | 5th pct (floor) | 50th | 90th (speech) |
|---|---|---|---|
| noisy input | -54.7 dB | -53.5 dB | -34.5 dB |
| sanitizer + arnndn only | -71.1 dB | -65.0 dB | -34.6 dB |
| full fixed chain | -59.0 dB | -53.5 dB | -22.6 dB |

Denoise removes ~16 dB of floor with speech untouched; leveling lifts speech +12 dB while the floor
rises only ~4 dB (speech-to-floor 20 dB -> 36 dB). On the clean speech: mean_volume -39.3 -> -27.3
dB, max_volume -15.0 -> -3.2 dB.

### Mutation evidence (the tests can fail)

Mutation A - sanitizer disabled (`string finite = "";`), run when the class had 13 tests:

```
Failed AgentEyes.Tests.MicChainStallTests.MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan [1 m 2 s]
  AgentEyes.UsageException : ffmpeg stalled during mix mic+system: output stopped advancing at 30.0s of output for 60s (limit 60s) after 61s - process killed
Failed AgentEyes.Tests.MicChainStallTests.FilterVideoMic_DefaultSettings_SanitizesNonFiniteSamplesBeforeArnndn [1 ms]
Failed AgentEyes.Tests.MicChainStallTests.MicChain_AllMixPaths_CarryTheSanitizer [3 ms]
Failed AgentEyes.Tests.MicChainStallTests.MicChain_LevelWithoutDenoise_SanitizesBeforeSpeechnorm [< 1 ms]
Failed!  - Failed:     4, Passed:     9, Skipped:     0, Total:    13
```

Mutation B - watchdog disabled (`RunWatched` -> `RunCore(args, label, null)`):

```
Failed AgentEyes.Tests.MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound [31 s]
  RunWatched was still running after 30 s - the stall guard did not fire
Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13
```

Mutations C, D, E together - partial-output delete off, exhausted-mux guard a no-op, reaper never kills:

```
Failed AgentEyes.Tests.FfmpegChildRegistryTests.ReapOrphans_OwnerGoneChildAlive_KillsTheChildAndRemovesTheRecord [150 ms]
  Assert.Equal() Failure: Collections differ
Failed AgentEyes.Tests.MicChainStallTests.RefuseExhaustedMux_AttemptsUsedUp_ThrowsWithCountAndLastError [56 ms]
  Assert.Throws() Failure: No exception was thrown
Failed AgentEyes.Tests.FfmpegChildRegistryTests.ReapOrphans_RealOrphanedProcess_IsKilled [10 s]
  the orphan was still running after the reap
Failed AgentEyes.Tests.MicChainStallTests.RunStage_FailedPass_RemovesThePartialOutput [94 ms]
  the failed pass left its partial output at the final path
Failed!  - Failed:     4, Passed:    17, Skipped:     0, Total:    21
```

(The stall test waits on a worker with a 30 s bound precisely so a dead guard fails by name instead
of hanging the suite. The watchdog's own unit tests are independent of the wiring and stay green
under B, as they should.)

### Gates

Windows, final branch head:

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   -> Passed!  - Failed: 0, Passed: 2164, Skipped: 0, Total: 2164
```

(2143 on main this morning + 21 new.)

The Linux and macOS test projects BUILD on Windows (0 errors). Run on Windows they show 4 and 1
failures, all POSIX-only tests that cannot pass on NTFS and that this change does not touch
(`CredentialFileTests.*Mode600*`, `MacPathsTests.MachineLocalAppData_OnMac_*`). Their real gate is
this PR's CI `linux` and `macos` jobs. I have no Linux or macOS machine.

Review: the `/code-review` pass returned an EMPTY result after one tool call. Per 6c an empty result
is a broken instrument, not a clean review, so it is not counted; I reviewed the diff by hand.

## How QA should exercise it

1. Build + test: `dotnet build AgentEyes.sln -c Release`, `dotnet test AgentEyes.sln -c Release`
   (the new tests need ffmpeg on PATH or `QA_RECORD_FFMPEG_DIR`; Windows CI provides it).
2. Windows product run: launch the branch app from
   `src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe --tray` (NOT
   `bin\Release\`), `POST http://127.0.0.1:7882/record/start {"mode":"video","source":"mic","mic":"<name from /devices>"}`
   (no denoise/level fields = defaults), stop after >= 600 s with `/record/stop`, then read the
   recording's `manifest.json` `PostProcessing.mux` (expect `done`), the log line pair
   `ffmpeg [process mic audio]: ... aeval=...` / `ffmpeg [process mic audio] ok`, and
   `ffprobe -show_entries stream=codec_type,duration recording.mp4` (audio within 3.0 s of video).
3. Stall: the quickest honest injection is mutation A above (blank the sanitizer, rebuild, run
   `--filter MixWavs_DefaultSettingsMicWithNanSample`) - expect the quoted stall error at ~61 s.
4. Orphan / restart / retry (9-11): reproduce "Criteria 9, 10, 11" above (raw.mp4 with one NaN as
   float PCM in a mov container, PendingMux with defaults, mutation-A build). Stop the installed app
   first. On macOS/Linux the path that matters is the registry: `ps` must show the reparented ffmpeg
   killed by the next start.
5. macOS / Linux (criteria 2, 3, 4): run the two astats commands on a real default-settings capture
   first, then a 10-minute default-settings recording on the branch.

Reminders: drive the app through the REST API / UIA / PrintWindow, never force-foreground with
synthesized input without warning the human; the HUD is capture-excluded, so assert it via UIA or
`/status`, not a screenshot.
