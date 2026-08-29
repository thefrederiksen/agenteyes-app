# QA round 6 - issue #28, camera failure boundaries (PR #32, head `8fad138`)

**Verdict: PASS - 17/17 acceptance criteria verified against the AMENDED spec.**

Verified independently. The Developer Agent's round-6 handoff and the round-5 QA report were read
as CONTEXT ONLY; every number below was produced by this session.

## How this round was verified

Four prior Review Gate rejections, so nothing here rests on reading the code and agreeing with it.

1. **An independent seam probe, written from the criteria, not from the developer's tests.**
   A console app whose assembly name is `AgentEyes.Tests` (so the existing
   `InternalsVisibleTo` reaches the internal `ICameraProcess` seam) drives the real compiled
   `FfmpegCameraRecorder`, `StrandedCameraOwner`, `RecordingService.Status` and
   `RecordingWorkset`. Source: `qa-round6-probe.cs.txt`. Results: `qa-round6-probe-results.txt`.
2. **The probe was run against the ROUND-5 tree first and made to FIRE.** The same binary source,
   pointed at commit `0558585`, reproduces the Review Gate's round-4 observations verbatim. A probe
   only ever run against the tree that is supposed to pass demonstrates nothing.
3. **20 mutations of the round-6 code**, each run against the FULL suite in an isolated worktree.
   `qa-mutations-round6.txt`. 18 fired; the 2 that did not are written up below rather than
   omitted.
4. **Real hardware**: 5 real camera recordings (CLI, REST, launcher/UIA), a real mid-run PID kill,
   a real force-kill produced by SUSPENDING the camera ffmpeg so it could not answer `q`, and a
   real busy-camera failure with the device held by a second ffmpeg.
5. **The stack above** was merged and run.

Build isolation: everything was built and run in dedicated worktrees
(`agenteyes-wt-qa28` at `8fad138`, `agenteyes-wt-qa28-r5` at `0558585`,
`agenteyes-wt-qa28-stack` at the stack tip). The `x64` Release output was used throughout;
the human's running v1.6.2 tray app never held the outputs under test.

### The instrument's own failure, recorded rather than hidden

The probe's FIRST run printed `complete=unknown` on every case - the perfect fail-open result. The
cause was the fake never delivering ffmpeg's open headers, so `Open()` threw before any case ran.
That is why every probe row carries a `threw=` field: a row without `threw=(none)` is a broken
instrument, never a clean run.

## The gate's four probe cases: was, now

Gate round-4 verdict on the left (its own words), this session's measurement on the right. Same
cases, same seam, run by this session against both trees.

| case | round-5 tree (`0558585`) - MUST fire | round-6 tree (`8fad138`) |
|---|---|---|
| `ONE_TICK_STALL_2_9S` | `captured=0.5 stopKind=clean-quit stderrComplete=True complete=yes` | `complete=unknown` |
| `ZERO_TICK_INCOMPLETE_STDERR` | `captured=0 stopKind=clean-quit stderrComplete=False complete=no` (+ `lostMidRun=True`) | `complete=unknown`, `lostMidRun=False` |
| `FAILED_QUIT_THEN_ERROR_EXIT` | `captured=1 stopKind=clean-quit stderrComplete=True complete=yes` | `stopKind=(not observed) complete=unknown` |
| `RETAINED_PROCESS_DIED` | `hasExited=True isAbandoned=True cameraStuck=True statusRows=1 pid=4242 claimHeld=True` | `hasExited=True isAbandoned=False cameraStuck=False holdsAny=False statusRows=0 claimHeld=False disposes=1` |
| `HEALTHY` (AC17 control) | `complete=yes` | `complete=yes` |

Two cases the gate did not have, added because defect 1 has more than one shape:

| case | round-5 | round-6 |
|---|---|---|
| `QUIT_OK_THEN_ERROR_EXIT` (quit delivered, exit -5) | `clean-quit` / `yes` | `(not observed)` / `unknown` |
| `FAILED_QUIT_THEN_ZERO_EXIT` (quit write failed, exit 0) | `clean-quit` / `yes` | `(not observed)` / `unknown` |

`FAILED_QUIT_THEN_ERROR_EXIT` is deliberately given TWO fresh advances so AC13's clause is
SATISFIED. Without that, defect 2's fix would have masked defect 1 and the probe would have proved
nothing about the quit.

Defect 4 is the one the human cares about most, so it is stated plainly: the retained recorder's
process ends **on its own**, with no code of ours running at that moment and **no explicit
`Recover()` call**. The very next read of `RecordingService.Status()` drops the `/status` row,
disposes the recorder and releases the recording claim on that directory (`claimHeld=False`,
`disposes=1`). On the round-5 tree the same read still asserted a dead PID was live and held the
claim.

## AC17 - the anti-cheat, run as a mutation and not as a reading

The cheap way to satisfy AC10 and AC13-AC16 is to write `unknown` everywhere. Mutation **M1** does
exactly that (`Completeness` returns `Unknown` as its first statement) and the suite goes RED with
6 failures, two of which are the positive controls:

```
Stop_WhenTheOutputKeptAdvancingUntilTheStop_RecordsTheTakeAsComplete
Stop_WhenTheOutputAdvancedTwiceAndWasStillFresh_IsRecordedAsComplete
```

And the same build wrote `CameraComplete: yes` into **four** real manifests (CLI, REST, launcher,
plus the AC6 preset run) while writing `no` for AC10/AC14 and `unknown` for AC13/AC15/AC16.

## Criterion by criterion

Legend: EXPECTED / ACTUAL / how.

### AC1 - Devices API lists cameras. PASS

`GET http://127.0.0.1:7882/devices` on the branch build -> HTTP 200,
`"cameras": ["HD Webcam eMeet C960", "OBS Virtual Camera"]` - the exact DirectShow names.

### AC2 - CLI lists cameras. PASS (with the round-1 deviation, restated)

`agenteyes screens` prints:

```
CAMERAS: DirectShow video devices (used by 'video' mode --camera)
  "HD Webcam eMeet C960"
  "OBS Virtual Camera"
```

The header is `CAMERAS:`, not `Cameras:`. It matches the existing `MONITORS` / `MICROPHONES`
headers, the Developer flagged it for the human in round 1, and the 2026-08-28 amendment left AC2
standing unchanged. Recorded as a case-insensitive match and as a deviation, not silently accepted.

### AC3 - Two separate files, both play, camera.mp4 is video-only, durations within 1.0s. PASS

Measured on real recordings, by this session, with `ffprobe`:

| path | recording.mp4 | camera.mp4 | delta | camera streams |
|---|---|---|---|---|
| REST (`/record/start` + `/record/stop`) | 14.466667 s | 14.699985 s | **0.233 s** | 1, `codec_type=video` |
| CLI (`agenteyes video --camera`) | 12.900000 s | 13.199987 s | **0.300 s** | 1, `codec_type=video` |
| launcher (UIA REC/STOP, AC6 preset) | 14.466667 s | 14.699985 s | **0.233 s** | 1, `codec_type=video` |

Non-trivial sizes throughout (camera.mp4 31-36 MB; recording.mp4 0.34-1.19 MB). All three are
inside the 1.0 s limit, and the round-3 open-probe balance (open headers, not the first progress
tick) is intact - `StartAndProbe` reported open after 594-689 ms on every run.

### AC4 - Manifest records the camera track. PASS

From the REST recording's `manifest.json`:

```
CameraFile = camera.mp4
CameraStartOffsetSeconds = -0.692        (numeric)
CameraCapturedSeconds = 14.63
CameraStopKind = clean-quit
CameraStderrComplete = True
CameraComplete = yes
Files = ['recording.mp4', 'camera.mp4', 'recording.original.mp4', 'system.original.wav']
```

The property names are PascalCase, as every other manifest property is; AC4 quotes them in
camelCase, including its own `files` reference. Flagged by the Developer in round 1 and left
standing by the amendment. Recorded as a deviation, not redefined away.

### AC5 - Status reports the camera. PASS

- during a camera recording: `State=recording  Camera='HD Webcam eMeet C960'`
- during a recording started WITHOUT a camera: `State=recording  Camera=null`
- idle: `Camera=null`

### AC6 - Preset round-trip. PASS

Driven end to end through UI Automation against the branch build (script committed alongside as
`qa-round6-ac6-uia.ps1.txt`; its output is reproduced verbatim). The user's `presets.json` and `config.json` were backed up and
restored - verified restored afterwards (6 original presets, the QA preset gone).

```
STEP1 editor opened: Edit preset
STEP1 CameraBox selection BEFORE = (None)
STEP1 CameraBox selection AFTER  = HD Webcam eMeet C960
STEP2 presets.json Camera = 'HD Webcam eMeet C960'  CameraFps = 30
STEP3 after RESTART, editor CameraBox = 'HD Webcam eMeet C960'
STEP3 presets.json after restart Camera = 'HD Webcam eMeet C960'
STEP4 launcher REC -> State=recording Camera=HD Webcam eMeet C960 Dir=...2026-08-28_201558_video
STEP4 files   = audio_16k.wav, camera.mp4, camera.mp4.ffmpeg.log, manifest.json, ...,
                recording.mp4, recording.original.mp4, ...
STEP4 manifest CameraFile=camera.mp4 CapturedSeconds=14.63 StopKind=clean-quit
                StderrComplete=True Complete=yes
AC6_RESULT PASS
```

The camera was selected IN THE EDITOR (it was `(None)` before), survived a full app restart in both
`presets.json` and the reopened editor read back through UIA, and the launcher REC/STOP produced the
two-file directory of AC3.

An earlier attempt reported "window 'Edit preset' not found" - a UIA root-children walk cannot see
this OWNED modal dialog. That was diagnosed (the HWND provably exists) and the script switched to
`AutomationElement.FromHandle`; it is recorded here because the first result would have read as a
product failure and was an instrument failure.

### AC7 - CLI parity. PASS

`agenteyes video --screen 1 --camera "eMeet" --seconds 10` -> exit 0, both files, and the same
manifest shape (`CameraFile`, `CameraStartOffsetSeconds=-0.715`, `CameraCapturedSeconds=13.13`,
`CameraStopKind=clean-quit`, `CameraStderrComplete=True`, `CameraComplete=yes`), durations
12.900 s / 13.200 s.

### AC8 - Unknown camera fails the start. PASS

- CLI: `agenteyes video --screen 1 --camera "no-such-device"` ->
  `[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.`
  **exit code 1** (measured without a pipe - a piped exit code would have read 0), and a
  before/after directory diff showing **no new directory**.
- REST: HTTP **400** with the same message naming the fragment; `/status` still `idle`; no new
  directory.

### AC9 - Busy camera fails the start. PASS

The camera was held open by a second ffmpeg (verified alive and ticking before each attempt).

- CLI: exit 1 in 609 ms,
  `the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5). Likely
  cause: ... already in use by another application.` No new directory.
- REST: HTTP 400 with the same message in 465 ms; `/status` `State=idle`, `CameraStuck=False`,
  `StuckCameras=0`; no new directory.

It never silently recorded screen-only: no directory of any kind was produced.

### AC10 (amended) - Mid-run loss is reported as loss. PASS

The camera ffmpeg (pid 44192) was killed by PID mid-recording. The screen ffmpeg (52260) was
still the only ffmpeg alive afterwards and `/status` still read `State=recording Elapsed=10.24`.

```
DurationSeconds = 17.57              recording.mp4 ffprobe: 17.566667 s, video + audio streams
CameraCapturedSeconds = 3.89
CameraStopKind = exited-early
CameraStderrComplete = True
CameraComplete = no
```

And the WARNING naming the camera, from the application log:

```
20:10:44.715 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during the
  recording (ffmpeg exited on its own) - the screen recording continues; camera.mp4 is truncated
  at 3.9s.
20:10:56.368 [WARN] stop: the camera "HD Webcam eMeet C960" was lost during this recording -
  camera.mp4 covers 3.9s of a 14.6s session; the screen recording is unaffected
```

`camera.mp4` itself is unreadable (`moov atom not found`) - which is exactly what
`CameraComplete: no` now says about it, and what the old boolean did not.

### AC11 - No regression with no camera. PASS

CLI and REST both: one `recording.mp4`, **no** `camera.mp4` on disk, and **no** camera key of any
kind in `manifest.json` (a case-insensitive grep for "camera" returns 0 hits).

### AC12 - Gate. PASS

```
dotnet build AgentEyes.sln -c Release  ->  Build succeeded.  0 Error(s)  (2 pre-existing xUnit1031 warnings)
dotnet test  AgentEyes.sln -c Release  ->  Failed: 0, Passed: 940, Skipped: 0, Total: 940
```

Run by this session in the isolated worktree, not taken from the handoff.

### AC13 - One tick then stall is NOT complete. PASS

Not producible with a physical webcam - stated, not glossed. Verified at the `ICameraProcess`
seam, on the injectable clock, on the real compiled recorder:

```
ONE_TICK_STALL_2_9S   captured=0.5 stopKind=clean-quit stderrComplete=True complete=unknown
ONE_TICK_STALL_30S    captured=0.5 stopKind=clean-quit stderrComplete=True complete=unknown
```

The 2.9 s case is the one the gate reproduced as `yes`, and it is inside the 3 s freshness window -
so what changed is that ticks must have CONTINUED, not merely been recent. Mutation **M3**
(`advances < 2` -> `advances < 1`) turns the corresponding committed test red, so the rule is not
decoration. Its control is the HEALTHY case in the same run: two advances, still fresh -> `yes`.

### AC14 - A force-killed file is never claimed complete. PASS - on real hardware

Reproduced for real by SUSPENDING the camera ffmpeg (`NtSuspendProcess`, rc=0) so it could not
answer `q`, then stopping the recording:

- the stop **surfaced the condition to its caller** - `POST /record/stop` returned an HTTP error,
  not a clean 200: `the camera "HD Webcam eMeet C960" ignored the quit request and had to be
  force-killed, so ...camera.mp4 was never finalized by ffmpeg and may be truncated - it covers
  4.9s of reported output. The screen recording is unaffected.`
- manifest: `CameraStopKind = force-killed`, `CameraCapturedSeconds = 4.89`,
  `CameraStderrComplete = True`, `CameraComplete = no`
- afterwards: `State=idle`, `LastStopFailed=True`, `CameraStuck=False`, and **zero** ffmpeg
  processes left running.

Also at the seam: `FORCED_KILL_AFTER_OUTPUT ... stopKind=force-killed complete=no
threw=CameraForceKilledException`. Mutation **M12** (the throw removed) turns that test red.

### AC15 - Incomplete stderr is never claimed complete. PASS

Not producible with a physical webcam. At the seam:

```
ZERO_TICK_INCOMPLETE_STDERR  captured=0   stderrComplete=False complete=unknown lostMidRun=False
ONE_TICK_INCOMPLETE_STDERR   captured=1.5 stderrComplete=False complete=unknown lostMidRun=False
```

`lostMidRun=False` is the second half of the fix and is asserted, not assumed: on the round-5 tree
the zero-tick case set `lostMidRun=True` and logged "camera.mp4 is EMPTY" - a positive claim about
a file, drawn from an absence in a stream that had explicitly not finished being read.

The control that stops this becoming "never conclude anything": `ZERO_TICK_COMPLETE_STDERR` -
same silence, but the stderr DID reach EOF - still answers `complete=no`, `lostMidRun=True`. So the
implementation distinguishes "known empty" from "we did not finish reading", which is the whole
point of the amendment.

The committed test that CODIFIED the overclaim (`CameraFailurePathTests.cs:803-819`, which required
`LostMidRun == true` on an incomplete stderr) has been rewritten, not deleted. It is now
`Stop_WhenTheStderrNeverReachesEndOfStream_DrawsNoConclusionFromTheUnfinishedRead` and it asserts
`Assert.False(rec.LostMidRun)`; mutations M4 and M4b both turn it red.

### AC16 - An abandoned camera process stays reachable and is reported. PASS

Not producible with a physical webcam (an ffmpeg that survives `Kill(entireProcessTree)` cannot be
made on demand). Verified at the seam through the REAL `RecordingService.Status()` and
`RecordingWorkset` production paths:

```
RETAINED_PROCESS_ALIVE  retained=True hasExited=False isAbandoned=True stopKind=abandoned
                        complete=unknown cameraStuck=True statusRows=1 pid=4242 claimHeld=True
                        disposes=0
RETAINED_PROCESS_DIED   hasExited=True  isAbandoned=False cameraStuck=False holdsAny=False
                        statusRows=0 claimHeld=False disposes=1
```

Every clause: the service RETAINS the recorder; `/status` reports the stuck camera **with its PID**;
the stop reported failure (`CameraStopFailedException` out of `Stop`); the manifest records
`abandoned` / `unknown`; and the claim is NOT released as though the stop were clean. Then - the
round-6 addition - the row and the claim clear themselves on the next look, with nothing having
asked. `StuckCameras` is present on the live `/status` response of the running branch build (empty,
correctly, on a machine with no stranded camera).

### AC17 - POSITIVE CONTROL: a good recording still says `yes`. PASS

Four real healthy recordings in this build wrote `CameraComplete: yes` (CLI, REST, launcher, and
the AC6 preset run), and the seam's HEALTHY control says `yes` in the same binary that says
`unknown` for AC13/AC15/AC16 and `no` for AC10/AC14. Mutation M1 proves the criterion has teeth.

## Regression check on the earlier rounds

Every round-1 and round-3 improvement the gate confirmed working was re-mutated on THIS tree, not
assumed to have survived a file that was restructured again:

| improvement | mutation | fired |
|---|---|---|
| Create/Open split (service stores before starting) | M16 | yes - 1 red |
| Create/Open split (callers construct what they open) | M8 | yes - 3 red |
| open-header probe (BOTH headers) | M7 | yes - 1 red |
| a failed open keeps the handle | M8 | yes |
| Dispose never releases a live handle | M9 | yes - 6 red |
| pre-stop process-loss check | M10 | yes - 1 red |
| a kill must be CONFIRMED | M11 | yes - 8 red |
| `/devices` does not swallow a camera failure | M15 | yes - 1 red |
| the manifest reports rather than states | M14 | yes - 1 red |
| CLI failure boundary (`finally` + Dispose) | M6c | yes - 1 red (see below) |

## The stack above (issues #33, #35, #36)

Verified, not taken from the handoff. In a scratch worktree at the stack tip
(`origin/issue-36-circular-camera-overlay`, `75e62ad`):

```
git merge origin/issue-28-camera-failure-boundaries   -> clean, no conflicts
dotnet build AgentEyes.sln -c Release                 -> Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release                 -> Failed: 0, Passed: 1152, Total: 1152
```

And the same seam probe, rebuilt against the MERGED tree, returns line-for-line the round-6 result
(section C of `qa-round6-probe-results.txt`) - so #33's preview tap into `FfmpegCameraProcess` and
`FfmpegCameraRecorder.Create` does not undo any of this round's guarantees.

## Findings that are NOT criterion failures - for the Review Gate

**1. The CLI transfer guard is weaker than the behaviour it names.**
`StrandedCameraOwnerTests.Video_HandsAnAbandonedCameraToAStrandedOwnerInsteadOfDroppingTheOnlyReference`
asserts that `Commands::Video` contains AT LEAST ONE `StrandedCameraOwner::*` call in the compiled
IL. There are two such call sites - the failed-open catch (`Commands.cs:351`) and the stop
`finally` (`Commands.cs:511`). Disabling **either one alone** leaves the whole suite green
(mutations M6 and M6b); only disabling both fires it (M6c). The gate's defect 5 was specifically
the `finally` site, so the fix for it is not covered by a check that fires when the fix is reverted.

The test does state a limit ("it proves the transfer is COMPILED INTO `Commands::Video`, not that it
runs on every path"), which is narrower than the hole. Reported as a coverage finding rather than a
product defect because both boundaries ARE present and correct in the round-6 code (read line by
line, and both compile into `Commands::Video` - M6c's scanner sees them), and no failure scenario
follows from the code as it stands. What is honestly not established by any automated check is that
BOTH boundaries will still be there after the next edit.

**2. Two spec-wording deviations, standing from round 1 and restated here rather than quietly
accepted**: `CAMERAS:` vs AC2's `Cameras:` (case), and PascalCase manifest keys vs AC4's camelCase
quoting. Both were flagged to the human by the Developer in round 1; the 2026-08-28 amendment left
AC1-AC9 and AC11-AC12 standing unchanged, so neither was re-ruled on. Neither is a behaviour
difference.

**3. `git diff --check origin/main...8fad138` is clean** - the round-5 blank-line-at-EOF hygiene
note the gate raised is fixed.

## Method compliance

- QA built and tested the branch itself, in an isolated worktree, from the `x64` Release output.
- Every check states its bad result and its EMPTY result; the two instrument failures this session
  hit (the probe's headerless first run, the UIA owned-window search) are recorded above rather
  than deleted.
- The one mutation that did NOT fire is reported as a finding, not omitted.
- No product code, test, issue or PR was changed by QA. The mutations were reverted
  (`git status --short` clean before committing this report).
- The human's installed v1.6.2 tray app was stopped only to free port 7882 for the branch build,
  and was restarted from its own path with its own `--tray` argument afterwards (verified healthy).
  `presets.json` and `config.json` were backed up and restored.
- **The human ran nothing.**

## Verdict

**VERIFIED - all 17 acceptance criteria of the amended spec are met.**

Per DEVELOPMENT_METHOD.md decision **D7** (2026-08-12, superseding D5), QA does NOT merge. The issue
moves to `flow:ready-gate` for the independent Review Gate.

Files in this proof set:

- `qa-report-round6.md` (this file)
- `qa-round6-probe.cs.txt` - the seam probe source
- `qa-round6-probe-results.txt` - its output on the round-5 tree, the round-6 tree, and the merged stack
- `qa-mutations-round6.txt` - the 20-mutation battery and what each one turned red
- `qa-round6-ac6-uia.ps1.txt` - the AC6 UI Automation script
