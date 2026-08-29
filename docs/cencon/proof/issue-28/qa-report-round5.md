# QA round 5 - issue #28, the camera track after the spec amendment

**Issue:** #28 - [Capture] Record the webcam to a separate camera.mp4 alongside the screen recording
**PR:** #32, branch `issue-28-camera-failure-boundaries`, head `0558585`
**Contract verified against:** the issue body AS AMENDED by the human ruling of 2026-08-28
(AC10 amended; AC13-AC17 new; assumption A7). AC1-AC9, AC11, AC12 stand unchanged.
**Verdict:** **PASS - 17/17 acceptance criteria verified**, with two clauses of AC1/AC2 explicitly
recorded as CODE-VERIFIED ONLY (no hardware to exercise them) rather than claimed as passed.

`flow:ready-gate`. **QA did not merge and did not close this issue** - DEVELOPMENT_METHOD.md D7
(2026-08-12) supersedes D5: an independent Review Gate of a different agent vendor authorizes the
merge.

---

## The build trap, closed before anything else

The tray app was running from this checkout's normal Release output, and its `AgentEyesApp.exe` was
stamped **12:43:42** - one hour and thirty-five minutes BEFORE the round-5 commit
(**14:18:39**). Every number below was produced from a separate

```
git worktree add --detach <scratch>/qa5 0558585
```

built into `bin\x64\Release\`, and the app driven over `127.0.0.1:7882` was confirmed to be the
worktree binary by reading its `ExecutablePath` off `Win32_Process`:

```
ProcessId      : 34648
ExecutablePath : ...\scratchpad\qa5\src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe
```

No `bin\Release\` (non-x64) directory exists in the worktree. Gate, run by QA at HEAD with a clean
tree:

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.   0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 926, Total: 926
```

---

## AC17 is the criterion this round turns on, so it is the one I attacked hardest

The obvious way to satisfy the amendment is to write `unknown` everywhere. That passes AC13, AC14,
AC15 and AC16 while telling the user nothing, and it is exactly the shape a fix under pressure
takes. So the positive control was not read off the developer's tests - it was **mutated**.

I wrote **my own probe** (`qa-round5-probe.cs.txt`, committed) with **my own fake `ICameraProcess`** -
deliberately not the developer's, because a fake that hides a behaviour hides it from every test
built on it. It drives the real `FfmpegCameraRecorder` and the real `CameraTrackRecord.Write` into a
real `Manifest` and asserts **the manifest strings the user ends up with**, which is what AC13-AC17
are written about. Ten checks, baseline 10/10 green.

Then I broke the product code ten ways and watched them fire (full transcript:
`qa-mutations-round5.txt`):

| # | mutation applied to the product | probes turned RED |
|---|---|---|
| QM1 | a clean quit is always `yes` (drop the opened/output/stderr/stall clauses) | 4 - AC13 x2, AC15, zero-frame |
| **QM2** | **`Completeness` always answers `unknown` - the fail-open cheat** | **4, including `AC17_HealthyCamera_ManifestSaysComplete_Yes`** |
| QM3 | time the tick's ARRIVAL instead of its ADVANCE | 1 - AC13 one-tick-then-stall |
| QM4 | read the LIVE advance instead of the stop-time snapshot | 1 - AC13 parting-flush |
| QM5 | the force-kill never reaches the caller | 1 - AC14 |
| QM6 | `Dispose` releases a live process handle anyway | 1 - AC16 |
| QM7 | the owner releases the claim regardless | 1 - AC16 (`RecordingWorkset.IsClaimed` goes false) |
| QM8 | force-killed is not counted as KNOWN broken | 1 - AC14 |
| QM9 | the manifest writer hardcodes `CameraComplete = "yes"` | 7 |
| QM10 | an abandoned process is reported as complete | 1 - AC16 |

QM2 is the whole point: **the fail-open cheat is detectable, and it is detected.** QM9 is the same
cheat one level up, at `CameraTrackRecord.Write`, and seven checks catch it.

---

## Acceptance criteria

### AC17 - POSITIVE CONTROL: a good recording still says `yes`

**Expected:** a healthy camera recording records `CameraComplete: yes`, in the same build that
passes AC13-AC16.
**Actual:** `yes` on **four independent healthy runs of one build** - REST, CLI, the launcher
preset, and the seam probe:

| surface | CameraStopKind | CameraStderrComplete | CameraComplete |
|---|---|---|---|
| REST `/record/start` (AC3 run) | `clean-quit` | true | **`yes`** |
| CLI `agenteyes video --camera` (AC7 run) | `clean-quit` | true | **`yes`** |
| launcher preset via UIA (AC6 run) | `clean-quit` | true | **`yes`** |
| QA seam probe `AC17_HealthyCamera_...` | `clean-quit` | true | **`yes`** |

The same build produced `no` / `unknown` for AC10, AC13, AC14, AC15 and AC16. **PASS.**

### AC13 - one tick then stall is NOT complete

**Expected:** `CameraComplete` is `no` or `unknown`, never `yes`.
**Actual:** the gate's `ONE_TICK_STALL` reproduced at the same seam the gate used, and hardened
past the two traps that make it easy to fake:

- the stalled ffmpeg goes on **printing progress for thirty seconds** (60 arrivals, zero advances) -
  timing the arrival instead of the advance would certify it, and QM3 proves the code times the
  advance;
- ffmpeg's **parting flush on `q`** both repeats and advances the position in two separate cases -
  reading the live value instead of the stop-time snapshot would certify it, and QM4 proves the code
  reads the snapshot.

Result: `captured=0.5s`, `stopKind=clean-quit`, `stderrComplete=true`, **`complete=unknown`**.
`FfmpegCameraRecorder.cs:409` (`OutputWasAdvancingAtTheStop`), `:424-434`, `:755-760`. **PASS.**

### AC14 - a force-killed file is never claimed complete

**Expected:** `CameraStopKind: force-killed`, `CameraComplete` never `yes`, and the stop surfaces
the condition to its caller.
**Actual:** verified at the seam AND **on the real webcam**. I suspended the camera ffmpeg with
`NtSuspendProcess` so it stayed alive and could no longer answer `q` - the only way a real ffmpeg
performs this - then stopped the recording:

```
SUSPENDED camera ffmpeg PID 14404 (NtSuspendProcess rc=0)
STOP REPORTED FAILURE TO ITS CALLER: HTTP 500
  "camera stop: the camera "HD Webcam eMeet C960" ignored the quit request and had to be
   force-killed, so ...\camera.mp4 was never finalized by ffmpeg and may be truncated - it covers
   6.9s of reported output. The screen recording is unaffected."
STATUS after: state=idle lastStopFailed=True
MANIFEST: captured=6.93 stopKind=force-killed stderrComplete=True complete=no
recording.mp4 decode: exit=0 errorOutput=[]      (the screen recording is untouched)
stray ffmpeg processes: 0
```

`FfmpegCameraRecorder.cs:924-925`, `:937-947`. **PASS.**

### AC15 - incomplete stderr is never claimed complete

**Expected:** `CameraStderrComplete` false and `CameraComplete` never `yes`, regardless of any
earlier positive tick.
**Actual:** the gate's `ONE_TICK_INCOMPLETE_STDERR` at the seam, with **fresh, advancing output
right up to the stop** so the ONLY thing wrong is the unfinished read: `stderrComplete=false`,
`complete=unknown`, and the drain was actually attempted (`Drains == 1` - an unattempted drain would
be a broken instrument, not a clean run). `FfmpegCameraRecorder.cs:406`, `:889`. **PASS.**

### AC16 - an abandoned camera process stays reachable and is reported

**Expected:** the service RETAINS the recorder; `/status` reports the stuck process INCLUDING ITS
PID; the stop reports failure to its caller; the manifest records `abandoned` / `unknown`; the claim
is NOT released.
**Actual:** every clause verified at the seam, each with the mutation that breaks it:

- retained and reachable: `Stop()` throws `CameraStopFailedException`, `Dispose()` retries and also
  fails, `IsAbandoned` true, **the process handle is NOT disposed** (`Disposes == 0` - QM6 fires),
  and `StrandedCameraOwner.Recover()` later reaches the same process and finally releases it;
- the claim: `RecordingWorkset.IsClaimed(dir)` stays **true** after
  `ReleaseClaimUnlessStranded` - QM7 fires. A healthy stop through the same method releases
  normally (paired control), so this is not "never releases";
- the PID on the wire: serialized through **the same `JsonSerializer.Serialize(payload, JsonOpts)`
  call `RestServer.Json` makes** (`RestServer.cs:466-470`) over the same internal `RecordStatus`,
  and the JSON contains `CameraStuck`, `StuckCameras`, the device name and `4242`. An internal type
  whose rows never serialized would have made this criterion false while every in-process assertion
  still passed;
- manifest: `stopKind=abandoned`, `complete=unknown` - QM10 fires.

**LIMIT, STATED:** this is NOT reproducible with a physical camera. Windows `TerminateProcess`
ends a normal process, suspended or not, so no real ffmpeg can be made to survive quit + kill +
retry. It is established at the `ICameraProcess` seam - the same route the Review Gate used to
reproduce the defect - plus the real serializer for the `/status` clause. `StrandedCameraOwner.cs`
(whole file), `RecordingService.cs:665-672`, `:884-894`. **PASS, with that limit on the record.**

### AC10 (amended) - mid-run loss is reported as loss

**Expected:** screen recording survives and is valid; manifest `exited-early` / `no` / observed
seconds; a WARNING naming the camera.
**Actual:** killed the camera ffmpeg by PID mid-recording (PID 9940). `/status` six seconds later:
`state=recording elapsed=12.64` - **the screen recording kept going**. After the stop:

```
recording.mp4 decode: exit=0 errorOutput=[]   duration=20.633333
MANIFEST: captured=4.93 stopKind=exited-early stderrComplete=True complete=no
LOG: [WARN] the camera "HD Webcam eMeet C960" stopped during the recording (ffmpeg exited on its
     own) - the screen recording continues; camera.mp4 is truncated at 4.9s.
LOG: [WARN] stop: the camera "HD Webcam eMeet C960" was lost during this recording - camera.mp4
     covers 4.9s of a 17.7s session; the screen recording is unaffected
```

**PASS.**

### AC3 - two separate files (with AC9 held together)

**Expected:** one directory with both files, both play, `camera.mp4` exactly one `video` stream,
durations within 1.0 s.
**Actual (REST):**

```
FILE camera.mp4        12,659,543 bytes      FILE recording.mp4     424,475 bytes
FFPROBE recording.mp4 duration=21.133333 streams=[0,video]
FFPROBE camera.mp4    duration=21.299979 streams=[0,video]     <- ONE stream, video
DECODE recording.mp4: exit=0 errorOutput=[]
DECODE camera.mp4:    exit=0 errorOutput=[]
```

Delta **0.167 s** against the 1.0 s limit. Measured on all three surfaces of this build:
**0.167 s** (REST), **0.167 s** (CLI, AC7), **0.167 s** (launcher preset, AC6). **PASS.**

### AC9 - busy camera fails the start

**Expected:** non-zero exit / HTTP 400, no directory, state stays idle - never a silent screen-only
recording.
**Actual:** a real ffmpeg was made to hold the camera, and **the holder was not believed until it
proved it held the device** - my first holder attempt failed exactly the way the round-4 report
warned (PowerShell split the unquoted `video=HD Webcam eMeet C960` at the spaces and ffmpeg looked
for a device called "HD"), and the guard REFUSED to produce a verdict rather than passing:

```
BROKEN INSTRUMENT: the holder never reported the camera open
```

Re-run with the argument quoted:

```
HOLDER CONFIRMED: reported "Input #0, dshow" and is still alive
REST: HTTP 400  "the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code
      -5). Likely cause: ... already in use by another application."
      dirs before=20 after=20 ; state=idle ; holder still alive: True
CLI : exit=1, same message, dirs unchanged in BOTH roots ; holder still alive: True
ffmpeg processes during: 1 (the holder) -> no orphan left by either failed start
```

**PASS**, and it holds together with AC3 on the same build.

### AC8 - unknown camera fails the start

**Actual:** REST `HTTP 400` with the fragment in the message
(`no DirectShow camera matches "no-such-device"`), no new directory, `/status` idle. CLI `exit=1`
with the same message, no directory in `%USERPROFILE%\Videos\AgentEyes` **or** in the CLI's real
root `<CWD>\recordings\`. Paired positive control, because "no directory appeared" is an absence:
a subsequent successful CLI run at the same root DID create one (2 -> 3), so the instrument can see
what it claims not to see. **PASS.**

### AC1 - Devices API lists cameras

**Actual:** `GET /devices` -> 200, `cameras: ['HD Webcam eMeet C960', 'OBS Virtual Camera']`, the
exact DirectShow names.
**NOT VERIFIED AT RUNTIME:** the "no camera attached -> `[]`" clause. This machine has two cameras
and I will not claim a case I did not run. Code-verified: `RestServer.cs:405` no longer wraps
`FfmpegDevices.ListVideo()`, so an empty array now means "no cameras" and a broken enumerator
answers 500 - the two are distinguishable, which is what makes the clause meaningful. Mutation R1
(re-wrap it) fires. **PASS on what was run; the empty-machine clause is code-verified only.**

### AC2 - CLI lists cameras

**Actual:** `agenteyes screens` prints a cameras section listing the same two names as AC1.
**Two deviations recorded rather than smoothed over:** the header reads `CAMERAS: DirectShow video
devices` (upper case, matching the neighbouring `MONITORS` / `MICROPHONES`) where the issue writes
`Cameras:` - an ASCII-case difference in the heading, the listing itself being what the criterion is
about; and the `(none found)` branch could not be exercised on this hardware and is code-verified
only. **PASS on what was run, with both deviations on the record.**

### AC4 - manifest records the camera track

**Actual:** `"CameraFile": "camera.mp4"`, `"CameraStartOffsetSeconds": -0.713` (numeric; negative
because the camera is opened before the screen recorder starts - assumption A5 calls it a hint),
and `"camera.mp4"` in `Files`. Keys are PascalCase (`CameraFile`) where the issue writes lower-camel;
that matches the manifest's own existing `VideoFile` convention. **PASS.**

### AC5 - status reports the camera

**Actual:** during a camera recording `/status` -> `"Camera":"HD Webcam eMeet C960"`; during a
recording started without one, `Camera` is `null` (`IsNull=True`). **PASS.**

### AC6 - preset round-trip

**Actual:** driven over UIA against the worktree build. A preset seeded with `Camera=null`, the
camera then chosen **in the editor** (`CameraBox: (None)` -> `HD Webcam eMeet C960`) and saved:

```
presets.json AFTER SAVE:    Camera=[HD Webcam eMeet C960] CameraFps=[30]
presets.json AFTER RESTART: Camera=[HD Webcam eMeet C960]
REOPENED EDITOR load signal: [PresetEditor] LoadCamerasAsync: 2 camera(s) listed, selected index 1
REOPENED EDITOR CameraBox selection (UIA): [HD Webcam eMeet C960]
launcher REC/stop -> recording.mp4 15.533333 + camera.mp4 15.699984 (delta 0.167s, one video stream)
MANIFEST: captured=15.63 stopKind=clean-quit stderrComplete=True complete=yes
```

**A CHECK OF MINE FAILED AND IS RECORDED RATHER THAN QUIETLY REPLACED.** My first reopen check read
the picker's selection immediately and got `(None)` - a FAIL. It was my instrument, not the app: the
picker's placeholder is `[(None), Loading cameras...]` with index 0 selected, so `(None)` is also
what an *unfinished* load looks like. The check was rewritten to wait for a PRESENCE independent of
the answer - the app's own `LoadCamerasAsync` log line for that editor instance - and only then read
the value, with the log's own `selected index 1` as a second independent reading.
`presets.json` and `config.json` were backed up and restored. **PASS.**

### AC7 - CLI parity

**Actual:** `agenteyes video --screen 1 --camera "eMeet" --seconds 15` -> exit 0, the same two-file
directory, `recording.mp4` 18.066667 / `camera.mp4` 18.233315 (delta **0.167 s**, one video stream),
and the same manifest fields including `complete: yes`. The CLI also prints the verdict:
`[ok] camera.mp4 (18.2s, 20.5 MB), video only - complete: yes`. **PASS.**

### AC11 - no regression with no camera

**Actual:** a `video` recording with no camera: `recording.mp4` only, **no `camera.mp4` on disk**,
and **no camera key of any kind** in `manifest.json` (regex for `"Camera*"` returns empty). **PASS.**

### AC12 - the gate

`Build succeeded. 0 Error(s)` and `Failed: 0, Passed: 926, Total: 926`, run by QA at HEAD in the
isolated worktree with a clean tree. **PASS.**

---

## No regression of the earlier rounds

This round restructured the manifest path, so the round-1 and round-3 fixes the gate confirmed
working were re-proved rather than assumed. Seven mutations, each against the FULL suite, each
firing on a specific named test (campaign 2 of `qa-mutations-round5.txt`):

| mutation | test that fires |
|---|---|
| R1 Devices swallows the enumeration failure | `TheDevicesEndpoint_DoesNotSwallowACameraEnumerationFailure` |
| R2 the CLI loses its camera failure boundary | `TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary` |
| R3 the open probe accepts a live process | `Start_WhenFfmpegOpensTheCameraButNeverOpensTheOutputFile_FailsTheStart` |
| R4 no pre-stop process-loss check | `Stop_WhenTheCameraDiedWithoutItsExitCallbackDelivered_RecordsTheTrackAsLost` |
| R5 the service opens before it stores the recorder | `TheRecordingService_StoresTheCameraBeforeStartingIt` |
| R6 `FailOpen` terminates and disposes regardless | 3 startup tests, incl. `Open_WhenTheKillItselfThrows_...` |
| R7 the kill failure is swallowed | 12 tests |

The Create/Open split, the CLI failure boundary, the open-header probe, the pre-stop process-loss
check and the Devices exception propagation are all present and all pinned.

---

## Recorded honestly

- **Two instruments of mine produced a wrong answer and were fixed before being believed**: the AC9
  camera holder that never held the device (it refused rather than passing), and the AC6 reopen
  check that read the picker before its async load finished (it reported a false FAIL). Both are
  written up above rather than silently replaced.
- **Not verified at runtime, and therefore not claimed as passed:** AC1's empty-`cameras`-array
  clause and AC2's `(none found)` - no camera-less machine available; both code-verified with
  file:line.
- **AC13, AC15 and AC16 are not producible with a physical webcam** and are established at the
  `ICameraProcess` seam, which is the same route the Review Gate used to reproduce them. AC14 and
  AC10 WERE reproduced on the real camera as well as at the seam. AC17 was reproduced on the real
  camera on three surfaces.
- **One observation for the Review Gate, not a defect against any criterion.** After a stop is
  abandoned, if the process dies on its own before the `StrandedCameraOwner.Recover()` retry, the
  retry's `Stop()` re-enters the pre-stop loss check (`FfmpegCameraRecorder.cs:817-826`) and records
  `StopKind = ExitedEarly` - a slightly inaccurate label for a process that was in fact killed. It
  cannot reach a manifest (the manifest for that recording was written during the stop, when the
  kind was `Abandoned`) and it never produces `yes`, so neither recurring theme is present. Flagged
  rather than left out.
- The tray app the human had running was stopped so the worktree build could own port 7882, and
  relaunched afterwards. `presets.json` / `config.json` backed up and restored. No stray `ffmpeg.exe`
  left behind on any path exercised.

---

## Artifacts

- `docs/cencon/proof/issue-28/qa-round5-probe.cs.txt` - QA's own probe, with QA's own fake
  `ICameraProcess`. Drop into `tests/AgentEyes.Tests/` and `dotnet test --filter QaRound5Probe`.
- `docs/cencon/proof/issue-28/qa-mutations-round5.txt` - both mutation campaigns, verbatim output.

**VERIFIED - all acceptance criteria met**, with the limits above stated rather than papered over.
Handed to the Review Gate (`flow:ready-gate`). QA did not merge and did not close.
