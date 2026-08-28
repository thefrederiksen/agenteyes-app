# Developer handoff - issue #28, round 6 (the five Review Gate round-4 defects)

**PR #32** - branch `issue-28-camera-failure-boundaries`
**Verdict answered:** `docs/cencon/review/pr32-issue28-gate-round4.md` (committed in this round; the
gate never commits its own verdicts, and it was sitting untracked on disk)
**Mutation evidence:** `docs/cencon/proof/issue-28/mutation-evidence-round6.txt`

The gate rejected round 5 with five blocking defects, each REPRODUCED by its own disposable probe
against the compiled production paths. Every one of those probe cases is now a committed test that
was run against the round-5 tree FIRST and fired. Nothing below is inferred.

```
dotnet build AgentEyes.sln -c Release    Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release    Passed!  Failed: 0, Passed: 940, Total: 940
```

Built and run from a separate `git worktree` at `D:\ReposFred\agenteyes-wt-28`, into
`bin\x64\Release\`, because the tray app (v1.6.2) is running from the main checkout's Release output
and would have served a stale binary. The tray app was not touched.

---

## The five defects

### 1. A failed quit could still be recorded as `clean-quit` / `yes`

Gate probe: `FAILED_QUIT_THEN_ERROR_EXIT captured=1 stopKind=clean-quit stderrComplete=True complete=yes`

`Stop` caught a `SendQuit` failure, logged it, and then read `WaitForExit(...) == true` as proof that
ffmpeg had ANSWERED a quit it never received. It never read the exit code on the stop path. A camera
that crashed with exit -5 while the write to stdin failed came out as a clean quit with a complete
take.

**Fix** (`FfmpegCameraRecorder.cs`): a new `_quitDelivered` flag is set only when the write to stdin
returns without throwing, and the exit code is READ on the stop path, before anything can release the
handle. `CameraStopKind.CleanQuit` is now recorded only when BOTH hold: the quit was delivered, and
the exit code is not negative. Otherwise nothing is recorded - the stop watched the process end and
did not observe HOW, so `CameraStopKind` is ABSENT in the manifest and `CameraComplete` is `unknown`,
which is what the amendment requires of every unanticipated case. A WARNING naming the exit code and
whether the quit was delivered goes to the log.

**Why a negative code and not "non-zero".** A negative exit code is the OS reporting an abnormal
termination (an NTSTATUS such as 0xC0000005 surfacing as a negative int) - that process did not run
its own exit path and cannot have written the MP4 trailer. A non-negative code is deliberately not
held against the take: `FfmpegLocator` takes a bundled, PATH or winget ffmpeg, and different builds
answer `q` with 0 and with 255. Reading every non-zero code as broken would turn AC17's positive
control into `unknown` on somebody else's machine - the fail-open fix wearing the opposite mask. The
limit is stated in `mutation-evidence-round6.txt` section D and in the code.

### 2. AC13's one-tick case still earned `yes` inside the 3-second window

Gate probe: `ONE_TICK_STALL_2_9S captured=0.5 stopKind=clean-quit stderrComplete=True complete=yes`

`OutputWasAdvancingAtTheStop` asked only whether the LAST advance was at most 3s old. One advance at
0.5s then a 2.9s stall walks straight through it. The gate: it "never establishes that ticks
CONTINUED after the first one."

**Fix**: a new `_outputAdvances` counter (incremented only on an ADVANCE, never an arrival),
snapshotted at the stop request as `_advanceCountAtStop` for exactly the reason the timestamp is -
ffmpeg's parting flush must not certify what came before it. `yes` now needs BOTH presences: the
position moved forward MORE THAN ONCE, and the last of those advances was inside the window.

**The control that stops this over-tightening**: `Stop_WhenTheOutputAdvancedTwiceAndWasStillFresh_
IsRecordedAsComplete` - two advances inside the window is the smallest recording that is still a
recording, and it says `yes`. A rule demanding many advances would pass the stall test while quietly
making every SHORT healthy recording `unknown`, which is AC17 failing by degrees.

### 3. An incomplete stderr became a positive claim that the file is EMPTY

Gate probe: `ZERO_TICK_INCOMPLETE_STDERR captured=0 stopKind=clean-quit stderrComplete=False complete=no`

Two ordering errors, both fixed:

* `Stop` used the absence of `_wroteOutput` FROM AN INCOMPLETE STREAM to set `_lostMidRun` and log
  "camera.mp4 is EMPTY". That block is now gated on `_stderrComplete`; when the stderr is incomplete
  the log says plainly that emptiness is NOT being claimed and the track is `unknown`.
* `Completeness` returned `No` for "no observed tick" BEFORE it checked `_stderrComplete`. The
  `_stderrComplete` check now comes first, ahead of BOTH absence-based clauses (`!_opened` and
  `!_wroteOutput`) - both are absences read through that same stream, and neither is an absence until
  the stream is finished.

**The committed test that codified the overclaim is fixed, and I am saying so plainly.**
`Stop_WhenTheStderrNeverReachesEndOfStream_StillReportsTheLossRatherThanAssumingATake`
(CameraFailurePathTests.cs:803-819) REQUIRED `LostMidRun == true` on incomplete evidence - it demanded
the very claim the amendment forbids, so the product could not be fixed while it stood. It is
rewritten (not deleted) as `..._DrawsNoConclusionFromTheUnfinishedRead`, and it now FAILS against the
code it used to pass. Its failing output is in the evidence file.

**The control that keeps the round-3 fix**: `Stop_WhenNoTickArrivedOnACOMPLETEStderr_
StillRecordsTheEmptyFileAsKnownBroken` - once the stderr HAS reached EOF, "never reported a frame" is
a real absence and stays `no`.

### 4. `/status` asserted a process was live after it exited, and held the claim for ever

Gate probe: `RETAINED_PROCESS_DIED hasExited=True isAbandoned=True cameraStuck=True statusRows=1 pid=4242 claimHeld=True`

This is the one the human said matters most. Three parts:

* `FfmpegCameraRecorder.IsAbandoned` asked the stored stop kind and `_terminated` - two facts about
  what AgentEyes DID, neither of which changes when the process later exits by itself. It now also
  asks the PROCESS (`!_proc.HasExited`). Reading that is safe on every path: the handle is released
  only by `Dispose`, and only once `_terminated` is true, which the test short-circuits on first.
* `StrandedCameraOwner` gained `Reap()` - the PASSIVE half of `Recover()`. `Report()` and `HoldsAny`
  both call it before publishing anything, because every retained row makes two live claims about the
  present ("this PID is stuck", "this directory still has a writer") and both stop being true the
  instant ffmpeg exits, with no code of ours running to notice. Reaping disposes the recorder
  (releasing the handle), releases the recording's claim, and drops the `/status` row - together.
  `Recover()` now does its Dispose retries and then calls the SAME `Reap()`, so letting go is one
  decision in one place rather than two copies that can diverge.
* `Stop` no longer RELABELS a stop kind an earlier stop already observed. Without this the `Reap()`
  Dispose would rewrite `abandoned` as `exited-early` for a process that ignored the quit AND the
  kill, making the durable record depend on when somebody next looked. (This also closes the
  observation QA filed for the gate in round 5.)

Net effect: the very next read of `GET /status` after a stranded ffmpeg dies reports no stuck camera
and releases the claim, so packaging and transcription are unblocked without waiting for a later
recording.

### 5. The CLI dropped the only reference to an abandoned recorder

`Commands.Video` wrote the honest `abandoned` / `unknown` manifest and then let its `finally` call
`Dispose()` and the local leave scope. The gate: "the only `StrandedCameraOwner` in product code is
the service field at RecordingService.cs:121; there is no transfer from the CLI path."

**Fix**: `Commands` now has its own `CliStrandedCameras` (a static `StrandedCameraOwner`, static
because the recorder must outlive the command frame), and `StrandedCameraOwner.RetainIfStranded`
routes the CLI through the SAME decision method the service uses. Both CLI camera failure boundaries
now use it:

* the `finally`, after the Dispose retry - a still-live ffmpeg is retained and its PID, device and
  output are printed with the `taskkill` line that ends it;
* the FAILED-OPEN catch - which also decides the directory: a directory is no longer removed around a
  live ffmpeg (that fails on the file it holds open and replaces the real camera error with an IO
  error). AC8/AC9's "no directory left behind" is unchanged for the ordinary failed open, where the
  process is confirmed gone and `RetainIfStranded` returns false.

**Stated honestly**: a CLI process cannot outlive itself. What this buys is that nothing later in the
command can lose the reference, that the decision is not a second branch that can be right in one
place and wrong in the other, and that the PID is printed and logged - and the printed PID is what
remains actionable after `agenteyes.exe` exits. No claim is made beyond that.

### Hygiene (the gate's non-blocking note)

`docs/cencon/proof/issue-28/mutation-evidence-round5.txt` had a blank line at EOF.
`git diff --check` on this branch is now clean.

---

## Every check was run against the unfixed code first

11 checks fired against the round-5 tree (commit `e485561`), the exact tree the gate rejected;
3 more are controls that pass before AND after. Verbatim output:
`docs/cencon/proof/issue-28/mutation-evidence-round6.txt`.

The fail-open mutation was ALSO re-run on this build: make `Completeness` answer `unknown`
unconditionally and 6 checks go red, AC17's positive control
(`Stop_WhenTheOutputKeptAdvancingUntilTheStop_RecordsTheTakeAsComplete`) and round 6's own tight
two-advance control among them. The mutation was reverted and the full suite re-run green before
anything was committed.

## What must not regress, and did not

* The round-3 fixes the gate confirmed working are untouched: the `Create`/`Open` split, the CLI
  failure boundary, the open-header probe, the pre-stop process-loss check, Devices exception
  propagation. No new wait was added to the camera-open sequence, so the AC3/AC9 timing balance is
  unchanged.
* AC17's positive control is green in the same build as AC13-AC16, at the unit level and at the
  tight two-advance boundary.
* No public signature changed. `ICameraProcess` is untouched (`ExitCode` was already on it).
  `FfmpegCameraRecorder.Create` / `Open` / `CreateOver` are untouched, so issue #33's preview tap
  wiring (`FfmpegRecorder`, `FfmpegCameraProcess`, `Create(..., PreviewTap?)`) is unaffected - the
  round-6 edits are in the fields, `Completeness`, `IsAbandoned`, `OnStderrLine` and `Stop`, none of
  which the stack touches. The three stacked branches (#33 -> #35 -> #36) need no adaptation; a
  merge of this branch into them touches disjoint regions of `FfmpegCameraRecorder.cs`.

## How QA should exercise it

Fast, and none of it needs the human:

1. `dotnet build AgentEyes.sln -c Release` and `dotnet test AgentEyes.sln -c Release` - 940 green.
2. Apply your OWN mutations; do not take mine on faith. The four that matter most:
   * `Completeness` always `unknown` -> AC17's control must go red.
   * remove the `advances < 2` clause from `OutputWasAdvancingAtTheStop` -> the 2.9s stall must go
     red.
   * move the `_stderrComplete` check back below the `!_wroteOutput` clause -> the incomplete-stderr
     test must go red.
   * drop `!_proc.HasExited` from `IsAbandoned` -> four `/status` liveness tests must go red.
3. Runtime, if you want the AC17 end-to-end: `POST /record/start {"mode":"video","camera":"<name>"}`,
   `POST /record/stop`, and read `manifest.json` - a healthy take must still say
   `"CameraComplete": "yes"`. AC14 is reproducible on the real webcam by suspending the camera ffmpeg
   (`NtSuspendProcess`) before the stop; AC10 by killing it by PID mid-run. AC13, AC15 and AC16 are
   NOT producible with a physical webcam and are established at the `ICameraProcess` seam, the same
   route the gate's probe used.
4. `GET /status` while nothing is stuck must report `CameraStuck: false` and an empty
   `StuckCameras` - the reap runs on every read, so a stale row is now a defect you can see directly.

I believe this is finished.
