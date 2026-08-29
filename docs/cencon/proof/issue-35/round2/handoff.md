# Issue #35 - Developer handoff, round 2 (Review Gate round 1 REJECT)

Branch: `issue-35-preset-editor-tabs` - PR #40. Round 1's handoff is
[`../handoff.md`](../handoff.md) and it still stands for the layout work; this note covers ONLY the
four blocking defects in [`../../../review/pr40-issue35-gate-round1.md`](../../../review/pr40-issue35-gate-round1.md)
(now committed to the branch - the gate never commits its own verdicts).

**I believe this is finished.**

---

## The four defects were one disease, and this repo had already cured it once

The gate's four findings are not four bugs in four call sites. Every one of them is either

* **the code CLAIMING THE CAMERA IS FREE WHEN IT IS NOT**, or
* **DISCARDING THE HANDLE TO A PROCESS THAT SURVIVED**.

That is precisely the class issue #28 spent nine developer rounds and nine gate reviews closing in
`FfmpegCameraRecorder`, and its cure is on `main`: an explicit RECORD of what was attempted and
observed (`CameraTerminationRecord`), ONE derivation over that record, monotone so nothing can be
downgraded, no call site able to assert an outcome it did not earn, and `StrandedCameraOwner` to keep
a surviving process reachable.

I did **not** patch the four call sites one at a time - that approach failed eight times on #28. The
preview lifecycle now has the same four parts:

| #28, on `main` | #35, this change |
|---|---|
| `CameraTerminationRecord` - the observation record | **`CameraReleaseRecord`** (`src/AgentEyes.Core/Video/CameraReleaseRecord.cs`) |
| `StopKind` - ONE derivation, ordered worst-first, monotone | **`CameraReleaseOutcome Outcome`** - `StillHeld` / `Unknown` beat `Released`; `DeviceConfirmedFree` is true for only two of the four |
| No method takes a `CameraStopKind`, so no call site can claim one | No method takes an outcome or a "released" flag. The deciding observation is not passed in at all: **`ObserveAfterStop(session)` asks the session, which asks the OS process** |
| `IsAbandoned` asks the process every time | **`IStrandedCameraProcess.IsAbandoned`**, now implemented by BOTH the recorder and the preview session |
| `StrandedCameraOwner` keeps a surviving writer reachable | **the same class, reused** - `CameraDeviceArbiter.StrandedPreviews` |

`StrandedCameraOwner` is REUSED, not re-invented: I extracted the four members it actually uses into
`IStrandedCameraProcess` (`DeviceName`, `ProcessId`, `OutputPath`, `IsAbandoned`) and
`FfmpegCameraRecorder` now implements it unchanged. A stranded preview is retained, retried and
reported on `/status` by exactly the code path a stranded recording is.

**Nothing in #28's merged design was disturbed.** `CameraTerminationRecord.cs`, `CameraObservation.cs`,
`Manifest.cs`, `CameraTrackRecord.cs`, `CameraFailurePathTests.cs` and `CameraTrackTests.cs` are
byte-identical to `main`. `FfmpegCameraRecorder.cs` changes by exactly one line - its base list
(`: IDisposable` -> `: IStrandedCameraProcess`, and `IStrandedCameraProcess : IDisposable`). The
explicit termination history, the single monotone stop-kind derivation and the three-state
`CameraComplete` are untouched.

---

## Defect by defect

### Defect 1 - closing during camera enumeration could start a preview after the window was gone

**Fix.** `CameraPreviewController` now has an OBSERVABLE, FINAL disposal (`IsDisposed`). `Select`
refuses after disposal, and refuses *again* inside the same `_gate` lock it publishes `_opening`
under - so there is no instant between "not disposed" and "opening" for a close to fall into.
`OpenSession` also treats "disposed" as staleness. The dialog got the second half of the fix:
`PresetEditor` sets `_closed` in `Window.Closed` and `LoadCamerasAsync` returns when it is set, so a
closed editor does not write into its own controls either.

**Also fixed, because it is the same rule:** a `Select` whose *current* camera could not be released
does not open the next one. One stuck preview must not become two.

### Defect 2 - closing unregistered the holder before it released the camera

**Fix.** `Dispose` now **stops first** and unregisters only on a release it ESTABLISHED. A holder
that still holds **stays registered**, stays askable, and is retried by the next recording start
(`ReleaseForRecording`) - which is also the one place a late unregistration can happen, because it is
the one place a late release is established. Release attempts are serialized on `_stopGate`, so a
recording start arriving during a closing dialog waits for that close rather than reading its
half-finished state as an absence.

### Defect 3 - an in-flight open timed out by claiming a release

**Fix.** `StopSession` records `InFlightOpenWaited(finished, ms)`; `finished == false` derives
`Unknown`, and `Unknown` is **not** `DeviceConfirmedFree`. `ReleaseForRecording` returns
`record.AnythingReleased`, so the arbiter's count is an observation rather than an intention. Two
further holes of the same shape were closed with it:

* `HoldsCamera` now counts an **unresolved open**. Reading the empty `_session` field as "nothing is
  held" is the same absence one step later.
* the timed-out open's `Task` is **kept** in `_opening`, so the *next* release attempt waits on the
  same open instead of finding an empty field and calling it an absence.

### Defect 4 - the real ffmpeg stop returned normally after a failed kill and discarded the handle

**Fix.** `FfmpegCameraPreview.Stop` makes **no claim at all**. It attempts, waits, then **asks the
process** (`_proc.HasExited`) and logs what it saw; a `Kill` that throws is a failed attempt, never a
release. `IsAbandoned` asks the process on every read. `Dispose` releases the process handle **only**
once the process is confirmed gone - otherwise the object stays valid, stays loud and stays
stoppable, and every later `Stop` is a fresh termination attempt (which is what makes
`StrandedCameraOwner.Recover()` able to get the camera back).

To make that testable I added `ICameraPreviewProcess` + `FfmpegPreviewProcess` beside #28's
`ICameraProcess`, for the reason #28 gives verbatim: "ffmpeg ignored the kill" and "Kill threw" are
not states a real ffmpeg can be asked to enter. It is a separate seam because a preview IS its stdout
while the recorder hands stdout to an async line reader - widening #28's seam to carry a stream only
one of them may touch would have put a trap in issue #28's code.

---

## The audit the gate asked for

I swept the whole preview lifecycle for the same claim-without-proof / discard-the-handle shape
rather than only the four named paths. **Three further instances**, all now routed through one door
(`ReleaseOrRetain`, or `StopSession`):

1. `CameraPreviewController.OpenSession`, stale branch - `session.Stop(); session.Dispose();`
   unconditionally. An open that landed after a close and could not be killed had its handle thrown
   away, defect 4 exactly.
2. `CameraPreviewController.OnFailed` - same two lines. A camera that reports a failure has *usually*
   already exited; "usually" is a claim.
3. `CameraPreviewController.HoldsCamera` - answered from `_session` alone (defect 3's absence).

And two smaller ones, fixed with them: `Select`'s and `Stop`'s status announcements said the camera
had been released whatever happened (they now show the device and PID that is still held), and
`/status` reported only the RECORDING owner's stranded rows, so a preview-stranded webcam was
invisible on the one surface meant to make it actionable (`RecordingService.Status()` now unions
both).

---

## Proof - COMPILED-HEAD probes, not inference

The gate wrote compiled-head probes rather than reading source; so did I. **One probe source,
built twice** - once against the reviewed head `3b0f21c` and once against this branch - so the two
sets of numbers come from the SAME instrument. Committed: [`probes/CameraReleaseProbe.cs`](probes/CameraReleaseProbe.cs),
[`probes/CameraReleaseProbe.csproj`](probes/CameraReleaseProbe.csproj). Raw output:
[`probe-output-prefix.txt`](probe-output-prefix.txt) / [`probe-output-fixed.txt`](probe-output-fixed.txt).

| Observation | Reviewed head `3b0f21c` | This branch |
|---|---|---|
| `P1_SESSIONS_CREATED_AFTER_DISPOSE` | **1** | **0** |
| `P1_POST_DISPOSE_SESSION_HELD` | **True** | **False** |
| `P1_POST_DISPOSE_CONTROLLER_HOLDS` | **True** | **False** |
| `P2_DISPOSE_GAP_HOLDER_COUNT` | **0** | **1** |
| `P2_DISPOSE_GAP_RELEASE_RETURNED_WHILE_HELD` | **True** | **False** |
| `P2_DISPOSE_GAP_RELEASED_COUNT` | **0** | (still waiting - it does not return until the device is free) |
| `P3_BLOCKED_OPEN_RELEASED_COUNT` | **1** | **0** |
| `P3_BLOCKED_OPEN_RELEASE_MS` | 5012 | 5006 |
| `P3_BLOCKED_OPEN_CONTROLLER_HOLDS_AFTER_RETURN` | **False** | **True** |
| `P3_BLOCKED_OPEN_STATE_AFTER_RETURN` | **Stopped** | **Failed** |

**Negative controls, identical on both builds** - so none of the above is an empty or constant
instrument: `P1N` (no close -> a second session IS created and IS held: 1 / True / True);
`P2N` (holder count 1 while open, 0 after a clean close); `P3N` (a normal open -> released 1 in 0ms,
holding nothing). `PROBE_FAILURES=0` on both runs; a probe that cannot reach the state it is testing
prints `PROBE_BROKEN` and exits non-zero rather than printing a clean-looking absence.

**What the probe cannot see, stated rather than implied:** it drives the LIFECYCLE with a fake
session, so it says nothing about a real ffmpeg handing a real webcam back to Windows. And defect 4
lives in `FfmpegCameraPreview`'s own stop path, which had **no seam at the reviewed head** and
therefore cannot be driven by a probe built against it - that one is proved by unit tests over the
new process seam plus the mutation evidence below.

## Proof - every new test demonstrated to FAIL

[`mutation-evidence.txt`](mutation-evidence.txt): each mutation puts the reviewed head's behaviour
back into ONE place, rebuilds, and runs the 44 camera-preview tests. A mutation that does not compile
is reported `BUILD_FAILED`, never as a pass.

| Mutation (reviewed-head behaviour restored) | Result |
|---|---|
| `d1all` - both halves of the disposal guard removed | **1 failed**: `Select_AfterTheEditorClosed_NeverStartsASession` |
| `d2` - unregister before the stop | **2 failed**: `Dispose_WhileTheCameraIsStillBeingReleased_KeepsTheHolderRegistered`, `Dispose_WhoseCameraSurvivesTheStop_KeepsTheHolderAndRetainsTheSession` |
| `d3` - an unresolved open ignored by the derivation | **1 failed**: `ARecordingStartingDuringAnUnfinishedOpen_IsNotToldTheCameraWasReleased` |
| `d4` - `Dispose` releases the wrapper regardless | **1 failed**: `Dispose_WhenFfmpegIgnoresTheKill_KEEPS_TheProcessHandle` |
| `d4b` - `IsAbandoned => false`, i.e. the stop announces success regardless | **5 failed** across `CameraPreviewStopTests` |
| `retain` - a surviving session discarded instead of retained | **3 failed** |

`d1` and `d1b` applied SEPARATELY did **not** fire, and that is recorded rather than hidden: each
half of the disposal guard covers for the other, so only `d1all` - which is what the reviewed head
actually looked like - is a real known-bad input. A half-mutation that leaves the property standing
proves nothing about the property.

The one new test not covered by a mutation is
`Select_WhoseCurrentCameraCannotBeReleased_DoesNotOpenTheNextOne`; its known-good control is
`Select_BeforeTheEditorCloses_DoesStartASession`, the same instrument reporting the opposite result.

---

## The gate

* `dotnet build AgentEyes.sln -c Release` -> **Build succeeded, 0 Error(s), 4 Warning(s)** - the same
  four pre-existing xUnit analyser warnings as at the reviewed head. **Zero warnings in product code.**
* `dotnet test AgentEyes.sln -c Release` -> **Failed: 0, Passed: 1002, Skipped: 0** (983 at the
  reviewed head + 19 new). **No existing test was weakened or deleted**; the only edits to
  `CameraPreviewTests.cs` are additive plus the fake session gaining the three members the interface
  now carries.

Heavy smokes were NOT run: no camera is attached to this machine, and the running-app camera checks
are AC9's, which QA drives with a camera present. Nothing here touches the recording pipeline, audio,
ffmpeg arguments, or the installer.

## Nothing regressed from round 1's 10/10

* **AC4** - `PresetEditor.xaml` is **byte-identical to the reviewed head** (`git diff HEAD --
  src/AgentEyes.App/PresetEditor.xaml` is empty); 48 `x:Name`s, the gate's own number, all 38
  pre-existing ones intact. This change touches no XAML at all.
* **AC9** - every exit path (leaving the Camera tab, Save, Save as, Cancel, the X, Esc) still runs
  through `UpdateCameraPreview` / `Stop` / `Dispose` unchanged; what changed is that they can no
  longer *say* the camera is free when it is not. A healthy preview still releases in a few
  milliseconds - `P3N_RELEASE_MS=0` on both builds - so AC9's two-second budget is untouched.
* AC1/AC2/AC3/AC5/AC6/AC8/AC10 - no code on those paths changed.

---

## How QA should verify this

**Re-run the compiled-head probe (the strongest check, ~1 minute).**

```
rm -rf docs/cencon/proof/issue-35/round2/probes/obj docs/cencon/proof/issue-35/round2/probes/bin
dotnet build docs/cencon/proof/issue-35/round2/probes/CameraReleaseProbe.csproj -c Release \
  -p:HeadDir=<dir with AgentEyesApp.dll + agenteyes.dll>
# copy that HeadDir's contents next to the probe exe, then run AgentEyes.Tests.exe
```

Build it once against `src/AgentEyes.App/bin/x64/Release/net8.0-windows10.0.19041.0` on this branch
and once against a build of `3b0f21c`. **Wipe `obj/` and `bin/` between the two** - MSBuild's
incremental compile will otherwise reuse the previous IL and the probe will silently report the other
build's numbers (it did, once, while this was being written).

**Re-run the mutation evidence:** `python docs/cencon/proof/issue-35/round2/probes/mutate.py <name>
apply`, `dotnet build`, `dotnet test --filter FullyQualifiedName~CameraPreview`, then `... revert`.
Names: `d1all d1 d1b d2 d3 d4 d4b retain`.

**Running-app checks (AC9, needs a camera attached).** For each of: leaving the Camera tab, Save,
Save as, Cancel, the window X, Esc - `POST http://127.0.0.1:7882/record/start` must succeed within
two seconds afterwards, then `POST /record/stop`. Then the new surface:
`GET /status` -> `cameraStuck` must be `false` and `stuckCameras` empty after every clean close. Do
NOT force-foreground the editor and synthesise input; the REST API, UIA and PrintWindow layers all
work with the window in the background, and the HUD is capture-excluded so its state is read from
UIA or `/status`, never from a screen grab.

**Worth a smoke:** `scripts\api-smoke.ps1` (the `/status` shape changed - `stuckCameras` can now carry
preview rows). `gui-smoke.ps1` is not indicated: no XAML changed.

## CenCon impact

No drift. The component map is unchanged - no new project, no new external surface. The privacy
posture is **strengthened**: a camera process AgentEyes cannot kill is now reported on `/status` with
its PID instead of being silently forgotten, which is "visible, controllable" applied to the exact
failure that left an orphaned capture running for 3.6 hours on this machine on 2026-08-28.
