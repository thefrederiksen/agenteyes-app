REJECT

# PR #39 / issue #33 - Review Gate round 2

Reviewed live PR #39 at head `c1eb48e8f11d71a61a07a424a8bb2a8d0411f215` against
`origin/main` at `e57d828578f499df95a9d66d19523436a222b590`. The merge base is that exact
main commit and main is an ancestor of the PR head. I read issue #33 in full, the round-1 Gate
verdict, `git diff origin/main...origin/issue-33-hud-live-preview`, the round-5 handoff and QA
evidence, and traced the repaired paths independently. Evidence from the other seats was treated as
context, not as the verdict.

An isolated worktree at the exact PR head was restored before building. Independent Gate result:

```
dotnet restore AgentEyes.sln
  Restored all six projects

dotnet build AgentEyes.sln -c Release --no-restore --no-incremental
  Build succeeded. 4 Warning(s), 0 Error(s)

dotnet test AgentEyes.sln -c Release --no-build --no-restore
  Passed: 1117, Failed: 0, Skipped: 0, Total: 1117

dotnet test AgentEyes.sln -c Release --no-build --no-restore \
  --filter "FullyQualifiedName~CameraFailurePathTests|FullyQualifiedName~CameraTrackTests"
  Passed: 86, Failed: 0, Skipped: 0, Total: 86

dotnet test AgentEyes.sln -c Release --no-build --no-restore \
  --filter "FullyQualifiedName~PreviewTapTests|FullyQualifiedName~HudResponsivenessTests|FullyQualifiedName~HudUserResizeTests|FullyQualifiedName~HudPreviewSizingOrderTests"
  Passed: 67, Failed: 0, Skipped: 0, Total: 67
```

The test assembly was the x64 output. The four analyzer warnings are in two test files unchanged by
this branch (`PostRecordingQueueTests.cs` and `StrandedCameraOwnerTests.cs`). The green suite does
not exercise the three failures below.

## Blocking defects

### 1. The pipe drain is isolated now, but preview lifecycle I/O can still block start or stop without a bound

The narrow round-1 defect is fixed: `Drain` reaches framing, `Note`, and `Offer`
(`src/AgentEyes.Core/Preview/PreviewTap.cs:319-362`); `Offer` is an interlocked swap plus event set
(`src/AgentEyes.Core/Preview/PreviewTap.cs:374-380`), and `Note` queues rather than calling the
logger (`src/AgentEyes.Core/Preview/PreviewTap.cs:391-401`). A stalled publisher therefore no longer
stops the only pipe reader.

The stronger claim made by the fix - that preview work is strictly subordinate and stop never waits
on it indefinitely - is still false in the lifecycle around that drain:

- `TryCreateAt` synchronously logs, creates the preview directory, and deletes old frame files
  (`src/AgentEyes.Core/Preview/PreviewTap.cs:225-244`). `RecordingService` calls it for both tracks
  before starting the recording writers (`src/AgentEyes.Core/RecordingService.cs:525-534`). Its catch
  handles a quick exception, not a filesystem operation that never returns.
- `Dispose` logs synchronously before its bounded joins, then, after the publisher join has timed
  out, calls `FlushNotes` and `RemoveFrameFile` synchronously on the stop thread
  (`src/AgentEyes.Core/Preview/PreviewTap.cs:514-540`). `RemoveFrameFile` performs `File.Exists` and
  `File.Delete` on the same preview path (`src/AgentEyes.Core/Preview/PreviewTap.cs:495-505`). The
  logger is itself a directory operation and `File.AppendAllText` under a process-wide lock
  (`src/AgentEyes.Core/Log.cs:25-39`). These calls have no timeout.
- `RecordingService.Stop` runs that disposal before returning the service to idle
  (`src/AgentEyes.Core/RecordingService.cs:813-828`), so this is not harmless cleanup after stop has
  completed.

Concrete failure scenario: use the same unavailable-share reparse point that motivated round 1 for
`%LOCALAPPDATA%\AgentEyes\preview`. On the next recording, `TryCreateAt` can remain inside
`Directory.CreateDirectory`, `File.Exists`, or `File.Delete`, and the recording never starts. If the
path stalls after a recording has started, the publisher may remain blocked as designed; Stop waits
three seconds for it and then immediately touches that same stalled path in `RemoveFrameFile`, so
Stop never returns and the service remains `finalizing`. A stalled logger can block even earlier at
the direct `Log.Info` in `Dispose`. The bounded slot protects ffmpeg's pipe, but the unbounded
downstream operation has merely moved to recording start/stop. That still violates AC10 and the
responsive recording-lifecycle requirement.

QA's injected eight-second write did not reach this failure: its injected delegate stalled only
`_writeFrame`, while `Dispose`'s real `RemoveFrameFile` and the shared logger still used healthy
local paths.

### 2. The runtime resize canary is discarded on the normal Stop path

`HudPreviewSizing.HidePanel` computes and returns the canary string before auto-sizing the window
(`src/AgentEyes.App/HudPreviewSizing.cs:79-89`). An explicit Show/Hide click consumes and logs that
return value (`src/AgentEyes.App/HudWindow.cs:541-548`). The ordinary stop path does not: `SetStatus`
calls the same method and discards its result (`src/AgentEyes.App/HudWindow.cs:390-408`). The later
save persists only the last size a recognized route put in `HudSizeMemory`
(`src/AgentEyes.App/HudWindow.cs:732-752`).

Concrete failure scenario: resize the normal-state HUD through a fifth shell route that produces no
resize modal loop and no `WindowState` change - Windows keyboard snap / Snap Layouts is the route QA
itself left unmeasured, and the already-tested bare `SetWindowPos` message shape demonstrates the
same state transition. Leave the preview visible and press Stop. No `HudUserResize.Record` call
claims the new size. `HidePanel` therefore returns the exact warning the canary exists to produce,
but line 407 drops it; `SavePosition` writes the old remembered size and the next recording opens at
that old size. There is no log evidence that a route was missed.

The focused canary test passes because its rig captures `HidePanel`'s return value. It does not test
the production `SetStatus` caller. The canary is derived rather than another enumeration, but on the
most common path - stopping with the panel still visible - it reports to nobody. It therefore does
not close round 1's exhaustiveness gap and AC7 remains structurally incomplete.

### 3. The background config writer can write an older HUD snapshot after a newer synchronous save

The HUD now queues a serialized full-config snapshot (`src/AgentEyes.App/Config.cs:108-117`), while
the launcher, settings, tray, preset manager, and plugin manager still call synchronous `Save`, which
writes another full snapshot directly (`src/AgentEyes.App/Config.cs:89-106`). Both eventually enter
`WriteJson`'s lock (`src/AgentEyes.App/Config.cs:127-133`), but that lock prevents simultaneous file
writes; it does not order the snapshots. The background loop removes a pending value before waiting
to write it (`src/AgentEyes.App/BackgroundFileWriter.cs:154-163`), so a newer synchronous save can
take the lock, land, and then be overwritten by the older queued value.

I reproduced the ordering deterministically against the branch's exact `BackgroundFileWriter`
source. The probe held the same shared write gate, queued an older HUD snapshot, waited until the
writer had taken it, performed the newer synchronous save under the gate, and released the writer:

```
newer synchronous launcher snapshot -> older HUD snapshot
REPRODUCED: the older queued snapshot lands last
```

Concrete failure scenario: a HUD preview/position change queues snapshot A. Before its writer gets
the config lock, the person changes the capture folder, a shortcut, plugin state, run-at-login, or
last preset; that UI calls `Config.Save` and writes newer snapshot B. The background writer then
writes A last. Because each is the whole JSON document, the newer launcher choice is silently
reverted on disk. The race is wider under the exact disk/filter-driver stalls this fix is intended
to tolerate. The current latest-wins tests cover only multiple calls through the background slot;
they do not cover the mixed synchronous/asynchronous writers introduced here.

## Rebase and remaining round-1 rulings

- The rebase did not damage issue #28's termination model. The branch has no diff in
  `CameraTerminationRecord.cs`, `CameraTrackRecord.cs`, or the termination derivation/tests;
  `FfmpegCameraRecorder` changes only its creation/argument wiring for the preview
  (`src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:552-580`). The explicit termination history,
  monotone stop-kind derivation, and three-state `CameraComplete` remain intact. All 86 targeted
  camera termination/manifest tests passed.
- AC3/C1 device ownership remains correct. `CameraCapture` still has one DirectShow camera input and
  appends the MJPEG stdout output from that same input (`src/AgentEyes.Core/Video/FfmpegArgs.cs:208-234`);
  there is no independent camera open.
- AC6/C5 remains based on `WDA_EXCLUDEFROMCAPTURE`
  (`src/AgentEyes.App/HudWindow.cs:760-785`) and was not relaxed.
- I checked QA's stalled-publisher control rather than accepting its summary: the shipped shape kept
  both drain counts climbing and produced valid 49.066s/49.433s tracks with `CameraComplete: yes`;
  the known-bad inline shape stopped its drain counts and force-killed the camera. That evidence is
  discriminating for the pipe-reader fix, but not for blocking start/disposal, the dropped canary,
  or mixed config-write ordering above.

No product code, branch, commit, GitHub issue, or pull request was changed by this review. The
installed tray app was not displaced and no ffmpeg process was started.
