# Issue #28 - Developer handoff, ROUND 4 (Review Gate REJECT round 2, PR #32)

Branch: `issue-28-camera-failure-boundaries` | PR #32 | Verdict answered:
[`docs/cencon/review/pr32-issue28-gate-round2.md`](../../review/pr32-issue28-gate-round2.md)

This round answers the gate's three blocking defects. **The human overrode the 3-strike limit for
exactly one more round**, so this note is written to be checked rather than believed: every claim
below names the file, the check that holds it, and the mutation that was shown firing against the
unfixed code.

---

## The one thing to read first: these were never three defects

All three gate findings - and all five from round 1, and the AC3 regression in between - are two
mistakes wearing different clothes:

| | The mistake | Where round 3 still made it |
|---|---|---|
| **(A)** | "we ASKED the process to die" treated as "the process DIED" | the open probe's kill (defect 1); `Dispose` after the stop's retry (defect 2) |
| **(B)** | "the DEVICE opened" treated as "the device is PRODUCING VIDEO" | the stop's completeness verdict (defect 3) |

So the fix is not three line edits. It is two rules, applied everywhere in the camera path, and the
whole file was audited for other instances of both (see **Audit** below). The rules are now written
at the top of `FfmpegCameraRecorder` as rules 4, 5 and 6, alongside the three the gate added in
round 2.

---

## Defect 1 - a startup timeout could return failure while leaving ffmpeg alive and unreachable

**What the gate found.** `FailOpen` killed, waited, LOGGED that the process had survived, and then
unconditionally set `_terminated`, disposed the wrapper, and returned an ordinary "could not be
opened" `UsageException`. `ICameraProcess.Dispose` only disposes the `Process` handle - it does not
terminate the OS process. And because `Open` (then `Start`) threw, `RecordingService.cs:358` and
`Commands.cs:313` never completed their assignments, so **no caller owned a recorder it could
retry**. The stalled ffmpeg kept the webcam and `camera.mp4` for the life of the process.

**What changed - two halves, because the defect had two.**

1. **Ownership: construction and opening are now separate.**
   `FfmpegCameraRecorder.Start(...)` is gone. In its place:
   - `FfmpegCameraRecorder.Create(camera, fps, crf, outPath)` - builds the recorder and **starts
     nothing**. It cannot leave anything behind because nothing exists yet.
   - `recorder.Open()` - starts ffmpeg and runs the open probe.

   Both callers now hold the recorder **before** ffmpeg exists:

   - `src/AgentEyes.Core/RecordingService.cs` (the camera start step): `_camera = ...Create(...)`
     then `_camera.Open();`. The field is set the moment the writer is constructed - the rule issue
     #155 already states for every other writer - so a failed `Open` is rolled back by `LiveWriters`
     -> `RecordingStopSequence.StopWriters`, which calls `Stop` (a real retry) and then `Dispose`
     (another one).
   - `src/AgentEyes.Core/Commands.cs` (`Video`): `cameraRec = ...Create(...)` then a `try { Open(); }`
     whose catch disposes the camera **before** removing the directory, and whose outer `finally`
     is still the camera's last owner.

2. **Honesty: `FailOpen` no longer conflates the two outcomes.**
   - *Confirmed gone* (`_proc.HasExited` after the kill and wait): `_terminated`/`_disposed` set, the
     handle released, and the caller gets the `UsageException` naming the real cause. **This is every
     failure a user actually hits** - absent, busy, unsupported framerate all make ffmpeg exit by
     itself - so AC8/AC9 behaviour is byte-for-byte what QA measured in round 3.
   - *Still running*: `_terminated` stays **false**, the handle is **kept**, and the failure raised is
     `CameraStopFailedException` - because the actionable fact is not "the camera would not open", it
     is "a live ffmpeg is on the camera". Its owner can and does try again.

   `Stop` on a recorder whose open failed skips the "q" grace entirely (there is no MP4 to finalize)
   and goes straight to a confirmed kill, so the retry is fast rather than 8 seconds polite.

**Coverage the gate named as missing, now present** (`tests/AgentEyes.Tests/CameraFailurePathTests.cs`):

| Check | Covers |
|---|---|
| `Open_WhenTheStalledFfmpegSurvivesTheKill_KeepsTheProcessHandleForARetry` | `KillEndsIt = false` at STARTUP |
| `Open_WhenTheKillItselfThrows_KeepsTheProcessHandleForARetry` | `KillThrows = true` at STARTUP |
| `Open_WhenTheStalledFfmpegDiesOnTheRetry_ReleasesTheCameraAndTheHandle` | positive control: the retry really terminates, and only then is the handle released |
| `Open_CalledTwice_RefusesToStartASecondFfmpeg` | the new hole the split opens: one recorder, one ffmpeg |
| `EveryCallerThatOpensACamera_ConstructsTheRecorderInTheSameMethod` | IL, both product assemblies: nothing opens a camera it did not construct |
| `TheRecordingService_StoresTheCameraBeforeStartingIt` | the field store precedes the open |
| `TheVideoCommand_OwnsTheCameraThroughAFinallyBoundary` | retargeted from `Start` to **both** `Create` and `Open` |

---

## Defect 2 - `Dispose` abandoned a live ffmpeg after its one retry failed

**What the gate found.** `Stop` correctly threw and left `_terminated` false, but `Dispose` caught
and suppressed the retry's failure and then disposed the wrapper anyway - leaving no handle for
further recovery while `RecordingService` returned to idle and released the recording claim.

**What changed.** `Dispose` now releases the handle **only once the OS process is confirmed gone**:

```
if (_disposed) return;
if (!_terminated) { try { Stop(); } catch (...) { log } }     // the retry
if (!_terminated) { log "STILL RUNNING ... the handle is KEPT"; return; }   // <- the fix
_disposed = true; _proc.Dispose();
```

Disposing an `ICameraProcess` closes a handle and terminates nothing, so doing it while ffmpeg is
alive converts a *reported* failure into an *invisible* one. The recorder stays valid and can be
stopped again - by the stop sequence, or by whoever reads the failure off `/status`.

> **Deliberate, stated boundary.** `RecordingService` still returns to idle and releases the claim
> after a failed stop. That is issue #153's decision, not an oversight: refusing to go idle would
> leave the user unable to record at all. The stop is **not** reported as clean - the camera step's
> `CameraStopFailedException` is collected by `RecordingStopSequence`, the stop throws
> `RecordingStopFailedException`, `LastStopFailure` is set, and `/status` reports
> `lastStopFailed: true` with the message naming the device and `camera.mp4`. If the gate wants idle
> itself blocked while a camera process survives, that is a change to #153's decision and belongs to
> the human, not to this round.

**THE TEST FOR THIS WAS ITSELF A DEFECT, AND IT IS FIXED, NOT DELETED.**
`Stop_AfterAFailedTermination_DisposeTriesToTerminateTheProcessAgain` set `KillEndsIt = false` and
then asserted only `Kills == 2` and `Disposes == 1` - both true whether or not ffmpeg died. It
certified a lifetime guarantee it never checked: a check that **fails open**. It is now
`Stop_AfterAFailedTermination_DisposeKeepsTheProcessReachableInsteadOfAbandoningIt` and asserts the
guarantee:

- `proc.HasExited == false` (the scenario is real, not accidentally satisfied by a dead process);
- `Kills == 2` (the retry happened);
- **`Disposes == 0`** (the handle to a LIVE process was not thrown away);
- and a third `Stop()` still reaches the process (`Kills == 3`) - it is a working handle, not a husk.

Against mutation **M2** (which restores round 3's `Dispose`) it fires with `Expected: 0 / Actual: 1`.
Its positive control `Stop_WhenTheRetryFinallyTerminatesFfmpeg_DisposeReleasesTheHandle` fires
against **M6** (a `Dispose` that never releases anything), so "keep the handle" cannot be satisfied
by leaking every `Process` object.

---

## Defect 3 - the header probe could turn a camera that never delivered a frame into a clean track

**What the gate found.** Two stderr headers alone set `_opened`. At stop, loss was inferred **only**
when the process had already exited; `_wroteOutput` was merely logged and never affected
`LostMidRun`. So: ffmpeg prints both headers, the camera stalls without any `time=` progress while
staying alive, later answers `q` normally -> `CapturedSeconds == 0`, `LostMidRun == false` ->
the manifest writes `cameraTruncated: false` with no loss warning. An empty `camera.mp4` marked good.

**What changed - rule 6.** A camera track is "complete" only when ffmpeg said it wrote something.

1. `Stop` now evaluates the track **after** the process is confirmed gone, on every path, and reports
   `LostMidRun` whenever no output was ever reported - alive or dead, quit or killed.
2. `_wroteOutput` is set only on a **strictly positive** output position. ffmpeg prints ticks before
   it has encoded anything (`time=N/A`, then `time=00:00:00.00`); counting those would reopen the
   hole with one extra step.
3. The verdict is read from **complete** stderr. `Process.WaitForExit(int)` does not flush the
   asynchronous readers, so a real camera's last tick can still be in flight when the process is
   already gone. `ICameraProcess.DrainStderr(ms)` waits (bounded, 2 s) for the reader's **EOF** -
   a presence, not a sleep - and a drain that times out is logged as an INCOMPLETE read and still
   reports the track lost. "We could not read it" never becomes "it was fine".
4. The CLI's `[ok] camera.mp4` line is a claim about the file, so a lost track now prints
   `[warn] ... - TRUNCATED` instead.

**Coverage the gate named as missing, now present.** The old test for this case called `proc.End(1)`
FIRST and only ever proved the already-exited path. It is kept (renamed
`Stop_WhenTheCameraDiedAfterOpeningWithoutDeliveringAFrame_StillReportsTheLoss`) and joined by:

| Check | Covers |
|---|---|
| `Stop_WhenALIVECameraOpenedAndNeverDeliveredAFrame_StillReportsTheLoss` | **the gate's exact scenario**: process still alive, no `time=`, answers `q` (asserts `Quits == 1`, `Kills == 0` - nothing was wrong with the process, the FILE is empty) |
| `Stop_WhenTheCameraReportedOnlyAZeroOutputPosition_StillReportsTheLoss` | `time=N/A` and `time=00:00:00.00` are not frames |
| `Stop_ReadsTheZeroFrameVerdictFromCOMPLETEStderr` | the verdict waits for stderr EOF (`Drains == 1`) |
| `Stop_WhenTheStderrNeverReachesEndOfStream_StillReportsTheLossRatherThanAssumingATake` | an unreadable stream is a broken instrument, not a clean run |

**Honest limit, stated rather than hidden:** a camera recording so short that ffmpeg never emitted a
positive `time=` will now be recorded as truncated at 0.0 s. That is *under*-claiming and it is
consistent with `cameraCapturedSeconds: 0.0`, which the manifest already wrote; the previous
pairing ("0.0 seconds captured, and the take is complete") is the thing that was false. Measured on
the shipped ffmpeg the first real tick lands 2.7 s in, and ffmpeg emits a final progress line on
quit, so this affects only sub-second takes - none of QA's round-3 runs (6-9 s, 9.06/9.59 s
captured) come near it.

---

## Audit: the same two assumptions, everywhere else in the camera path

Every place in `FfmpegCameraRecorder`, `ICameraProcess`, `RecordingService` and `Commands.Video`
that decides "is it dead" or "is it good" was re-read against (A) and (B):

| Site | Verdict |
|---|---|
| `StartAndProbe` exit check | reads `HasExited` (the fact), reads `ExitCode` before disposing. OK |
| `StartAndProbe` timeout | now routes through the two-outcome `FailOpen`. **FIXED** |
| `FailOpen` kill | **FIXED** - confirm-or-keep-the-handle |
| `Stop` pre-quit loss check | reads `HasExited`, before the quit makes it ambiguous. OK (round-2 defect 4) |
| `Stop` quit path | new `KillOrThrow` helper: returning normally means the OS says it is gone, never that a kill was issued. **HARDENED** |
| `Stop` un-opened path | new: no "q" grace for a camera with no file to finalize; straight to a confirmed kill. **NEW** |
| `Stop` completeness verdict | **FIXED** - rule 6, from complete stderr |
| `Stop` ffmpeg-log write | now gated on `_opened`, so a failed-start retry cannot write a log into a directory AC8/AC9 requires to be removable |
| `Dispose` | **FIXED** - handle released only when confirmed gone; idempotent |
| `OnExited` | a convenience only; `Stop` re-reads the process. OK (round-2 defect 4) |
| `OnStderrLine` `_wroteOutput` | **FIXED** - strictly positive position |
| `_stopRequested` vs `_terminated` vs `_disposed` | three separate facts: asked / confirmed gone / handle released. Previously two, which is how (A) hid |
| `ICameraProcess.Dispose` (real) | doc now states plainly that it terminates nothing |
| `Commands.Video` failed-open cleanup | disposes the camera before deleting the directory, and the delete's own failure is logged rather than replacing the camera error |
| `Commands.Video` `[ok] camera.mp4` | **FIXED** - `[warn] ... TRUNCATED` for a lost track |
| `RecordingService` manifest `cameraTruncated` | reads `camera.LostMidRun`, which is now correct at the source |

---

## Non-regression: rounds 1-3 are intact

- **Round-1 gate defects 1-5** - all five checks are still present and still fire. Defect 1's
  boundary check was **retargeted** (it named `FfmpegCameraRecorder::Start`, which no longer exists)
  and **strengthened**: it now requires the Finally/Fault boundary and the `Dispose` cleanup around
  **both** `Create` and `Open`. It was re-demonstrated firing (mutation M5).
- **Round-2 defects (open probe, failed stop, exit/stop race)** - unchanged in substance; every
  existing check kept.
- **AC3 (the round-3 regression)** - the open probe is **untouched**. It still returns on ffmpeg's
  two headers, never on the first encoded frame.
  `Start_DoesNotHoldTheRecordingStartWaitingForTheFirstEncodedFrame` (the 1.0 s budget) and
  `Start_WhenOnlyAProgressTickArrives_DoesNotCountThatAsAnOpenCamera` both still pass. Nothing was
  added to the start path; the only new wait is a bounded post-exit drain at STOP, which cannot
  change either file's duration.
- **AC9** - the busy/absent/unsupported camera path is bit-identical: ffmpeg exits by itself, so
  `FailOpen` takes the confirmed-gone branch exactly as before, and neither caller writes anything
  into the recording directory. The service's rollback now sees a non-null `_camera`, but its `Stop`
  is a no-op (`_terminated` already true) and writes no file, so `Discard` still finds only
  `manifest.json` and removes the directory.
- **No existing regression test was weakened or deleted.** One was **strengthened** - see defect 2.

---

## Gate

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.   0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Failed: 0, Passed: 900, Skipped: 0, Total: 900
```

Both were run with `-p:UseArtifactsOutput=true -p:ArtifactsPath=<scratch>` and the artifacts
directory **deleted first**, for the reason the gate itself gave: the running tray app
(`AgentEyesApp.exe`) holds a write lock on
`src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.dll`, so a normal-path
build fails `MSB3027` and a stale `bin\` can hand back a green that tested code nobody built. The
tray app was NOT killed - it is the human's running session. Verbatim output is at the end of
[`mutation-evidence-round4.txt`](mutation-evidence-round4.txt).

900 tests, up from 889: 11 new checks, 2 rewritten/retargeted, 0 removed.

---

## Proof that the checks can fail

[`docs/cencon/proof/issue-28/mutation-evidence-round4.txt`](mutation-evidence-round4.txt) - six
mutations that restore the round-3 behaviour, each with the verbatim failing test output, plus the
clean run with every mutation reverted. Summary:

| Mutation | Restores | Fires |
|---|---|---|
| M1 | `FailOpen` treats a survived kill as a clean one | 3 checks |
| M2 | `Dispose` releases the handle of a live ffmpeg | 2 checks, incl. the rewritten one (`Expected: 0 / Actual: 1`) |
| M3 | no zero-frame rule, no stderr drain, zero position counts | 4 checks |
| M4 | the service opens the camera before it owns it | 2 checks |
| M5 | no failure boundary in `Commands::Video`; no second-open guard | 2 checks |
| M6 | `Dispose` never releases the handle at all | 2 checks (the positive controls) |

No mutation left in the tree: `grep -rn "MUTATION" src/ tests/` returns nothing.

---

## How QA should re-verify

### Defect 1 - a failed start never strands ffmpeg

*Unit (fast, no camera):*
```
dotnet test AgentEyes.sln -c Release -p:UseArtifactsOutput=true -p:ArtifactsPath=<scratch> ^
  --filter "FullyQualifiedName~CameraFailurePathTests"
```
Expect 33/33. Then re-run mutation **M1** from the evidence file and confirm the three `Open_*`
checks fire. **A check that does not fire is the defect.**

*Runtime (AC9, the path a user hits):* hold the webcam open in another app, then
`POST http://127.0.0.1:7882/record/start {"mode":"video","camera":"<name>"}`.
Expect: HTTP 400 naming the camera, `GET /status` -> `"state":"idle"`, **no new directory** under
`%USERPROFILE%\Videos\AgentEyes\`, and no orphan `ffmpeg.exe` in `tasklist` afterwards. Same via
`agenteyes video --screen 1 --camera "<fragment>"` -> non-zero exit, no directory. Round 3 measured
0.40 s for this; it should be unchanged.

*Structural:* `EveryCallerThatOpensACamera_ConstructsTheRecorderInTheSameMethod` reads IL from both
product assemblies; `TheRecordingService_StoresTheCameraBeforeStartingIt` reads
`RecordingService.StartVideo`'s source. Its limit is stated in the test: it sees the store/open
order in one method body, not aliases or helpers - which is why the IL check sits beside it.

### Defect 2 - a live ffmpeg is never abandoned

*Unit:* `Stop_AfterAFailedTermination_DisposeKeepsTheProcessReachableInsteadOfAbandoningIt` plus its
positive control. Re-run **M2** and confirm the first fires (`Expected: 0 / Actual: 1`), then **M6**
and confirm the control fires. Read the assertions: the old version of this test passed against M2,
which is exactly why it was a defect.

*Runtime:* this path needs an ffmpeg that ignores `q` AND survives `Kill(entireProcessTree)`, which
cannot be produced on request - that is why `ICameraProcess` exists. What IS checkable at runtime is
that a normal stop still releases everything: record with a camera, stop, then confirm no
`ffmpeg.exe` remains and `GET /status` shows `lastStopFailed: false`.

### Defect 3 - a camera that never delivered a frame is never a clean track

*Unit:* the four checks in the table above; re-run **M3** and confirm all four fire.

*Runtime (AC10, the observable half):* start a camera recording, kill the CAMERA ffmpeg by PID
mid-run, stop. Expect `recording.mp4` valid and playable, a WARNING naming the camera in
`%LOCALAPPDATA%\AgentEyes\` logs, and `manifest.json` with `"cameraTruncated": true` and
`cameraCapturedSeconds` equal to the seconds actually captured (round 3: 6.93 s of a 14.47 s
session). The live zero-frame case cannot be produced from a real webcam on demand - a device that
opens and then delivers nothing is a hardware fault - so it is held by the unit checks, and that
limit is stated in the test file.

### AC3 / AC9 non-regression (please re-measure, they were the last regression)

- **AC3:** `POST /record/start {"mode":"video","camera":"<name>"}`, wait ~6 s, `POST /record/stop`.
  `ffprobe` both files: durations within **1.0 s** (round 3: 0.133 s REST, 0.300 s CLI, 0.333 s
  launcher), `camera.mp4` exactly one stream with `codec_type=video`. Also confirm
  `"cameraTruncated": false` on that healthy take - the new rule must not fire on a good camera.
- **AC9:** as above under defect 1 - failure fast, no directory, state idle.
- **AC11:** a `video` recording with no camera - one `recording.mp4`, no `camera.mp4`, no
  `cameraFile` key.

Smokes worth scoping: `scripts\api-smoke.ps1` (the REST paths above) and, if the CLI surface is in
scope for QA this round, one `agenteyes video --screen 1 --camera "<fragment>" --seconds 6`. The
GUI was not touched.

---

## CenCon impact

No drift. No change to the component map, no change to the privacy posture (visible / controllable):
the changes make a failed camera **more** visible - a live ffmpeg that could not be terminated is now
reported instead of silently unreachable, and an empty camera track is reported instead of recorded
as good. `docs/cencon/` needs no update beyond this note and the evidence file.

## Files changed

| File | Why |
|---|---|
| `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs` | `Create`/`Open` split; `FailOpen` two outcomes; `KillOrThrow`; rule 6 at stop; `Dispose` keeps a live handle; `_disposed`/`_openAttempted`; `_wroteOutput` strictly positive; `CameraStopFailedException` carries its context |
| `src/AgentEyes.Core/Video/ICameraProcess.cs` | `DrainStderr(ms)` (stderr EOF as a presence); real process sets EOF from `ErrorDataReceived(null)`; `Dispose` doc states it terminates nothing |
| `src/AgentEyes.Core/RecordingService.cs` | camera start step: store `_camera` before `Open()` |
| `src/AgentEyes.Core/Commands.cs` | `Video`: `Create` then guarded `Open`; dispose before discarding the directory; `DiscardEmptyRecordingDirectory`; honest `[warn]` for a truncated camera |
| `tests/AgentEyes.Tests/CameraFailurePathTests.cs` | 11 new checks, 1 fail-open test rewritten, 1 boundary check retargeted and strengthened |
| `docs/cencon/proof/issue-28/mutation-evidence-round4.txt` | the failing output of every one of them against the unfixed code |
| `docs/cencon/review/pr32-issue28-gate-round2.md` | the gate verdict this round answers, committed to the branch |

**I believe this is finished.**
