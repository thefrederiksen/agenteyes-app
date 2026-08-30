REJECT

# PR #49 / issue #47 - Review Gate round 1

## Basis of review

Reviewed PR #49 at head `fff706c4f2a34ea1c12dcc51cacaff5b62a42377` against
`origin/main` at `36c98f13017b666491f89e8d3ab2d09b04ef9141`. Local head, remote branch,
and PR head were identical. I read issue #47, both AC7 decisions, the developer handoff, the full
diff, and the surrounding recording, post-processing, manifest, and overlay code.

The issue is labeled `flow:ready-gate`, but it did not get there through the required independent
QA seat. The issue comment says the same seat wrote the code and produced the verification, and
`docs/cencon/proof/issue-47/` contains only `handoff.md`, not a QA report. That does not satisfy the
independent QA handoff required by `docs/cencon/DEVELOPMENT_METHOD.md:80-86` and
`docs/cencon/DEVELOPMENT_METHOD.md:291-318`. This gate review does not retroactively create the
missing separation of duties. The code defects below independently require REJECT, so the verdict
does not rest only on that process failure.

## Blocking defects

### 1. Re-composing after a framing change leaves the old camera inset in the video

`CameraCompose.Run` always takes its screen input from `manifest.VideoFile`, which is
`recording.mp4` (`src/AgentEyes.Core/CameraCompose.cs:72`). After the first compose, that file is
already the composed output. Although `Swap` preserves the first screen-only cut, a later run only
uses its existence as a reason to delete the current `recording.mp4`; it never selects
`recording.screen.mp4` as the new input (`src/AgentEyes.Core/CameraCompose.cs:133-140`). This
contradicts both the command's stated purpose and its success message
(`src/AgentEyes.Core/Commands.cs:550-569`).

Concrete failure scenario: compose a bottom-right inset, change the manifest framing to top-left,
then run `agenteyes compose <dir>`. The new top-left camera is drawn over a video that still contains
the bottom-right camera, so the result has two camera circles.

I reproduced this on compiled head with a copy of the real 1920x1080 composed recording. The
manifest was changed only from `bottom-right` to `top-left`. The command returned:

```text
[ok] composed  ...\recording.mp4
     the screen-only cut is recording.screen.mp4
     camera.mp4 is unchanged - re-run compose after changing the framing
COMPOSE_EXIT=0
```

The frame at 80 seconds before the run had one bottom-right circle. The frame at 80 seconds after
the run was inspected and had both the new top-left circle and the old bottom-right circle. The
preserved screen-only file was present throughout, so the failure is specifically that the command
does not use it. This violates AC8's recomposition behavior.

### 2. A valid circular inset becomes an ellipse on a wide output

`CameraComposition.For` derives a circle's width from the screen width and initially makes its
height equal, but then clamps width and height independently
(`src/AgentEyes.Core/Preview/CameraComposition.cs:84-95`). The supported inset range reaches 0.60
(`src/AgentEyes.Core/Preview/CameraOverlay.cs:212-219`). On a 3840x1080 output at that valid value,
the width is 2304 pixels while the independently clamped height is 1080 pixels. `ComposeArgs` then
scales the square mask to those non-square dimensions
(`src/AgentEyes.Core/Video/ComposeArgs.cs:82-85`), producing an ellipse rather than a circle.

The compiled-head probe asserted the required square output and fired:

```text
inset=2304x1080
circular=True
DEFECT: a circular inset is not square in output pixels
GEOMETRY_PROBE_EXIT=1
```

Concrete failure scenario: record a 3840x1080 display, choose the maximum supported Size on screen,
and use the circle shape. The final video contains a 2304x1080 ellipse. This directly violates AC4.

### 3. A camera that starts late paints an opaque black inset before camera footage exists

For a positive `CameraStartOffsetSeconds`, `ComposeArgs` pads the camera stream with opaque black
frames (`src/AgentEyes.Core/Video/ComposeArgs.cs:70-73`) and then overlays those frames normally for
both shapes (`src/AgentEyes.Core/Video/ComposeArgs.cs:80-90`). Padding content is not the same as
delaying the overlay: it covers the screen before the camera begins.

The compiled-head probe used a uniform blue three-second screen, a uniform red two-second camera,
a rectangular top-left inset, and `CameraStartOffsetSeconds: 1.0`. The compose command returned exit
0. The extracted frame at 0.5 seconds was inspected and had an opaque black inset over the blue
screen; the frame at 1.5 seconds had the expected red camera inset.

Concrete failure scenario: any recording whose manifest says the camera started one second after
the screen gets a black box (or black circle) for the first second rather than the original screen.
That is visible invented content and does not satisfy AC5's time-aligned composition.

### 4. A permanently failing compose is retried every 15 minutes forever

The stage counts each attempt before running (`src/AgentEyes.Core/PostRecording.cs:366-374`), and the
comment says that a compose which can never finish must consume a try. `NeedsCompose`, however,
checks only `ComposedCamera`, `CameraFile`, and framing; it never reads the counted attempts and has
no compose-attempt ceiling (`src/AgentEyes.Core/PostRecordingPlan.cs:65-78`). This differs from the
mux predicate immediately above it, which enforces `MaxMuxAttempts`
(`src/AgentEyes.Core/PostRecordingPlan.cs:35-49`). The repair timer runs every 15 minutes
(`src/AgentEyes.Core/RepairSchedule.cs:22-30`).

Concrete failure scenario: leave a camera recording with a corrupt or unreadable `camera.mp4`.
Every repair pass records another failed compose and launches ffmpeg again, indefinitely. The
attempt count grows but cannot change admission. This is the exact infinite-retry condition the
new comment claims to prevent.

### 5. The preset framing is absent from the manifest until Stop completes

The feature is described as persisting the preset framing at record time, but start constructs the
manifest and records `CameraFile` without copying the overlay into it
(`src/AgentEyes.Core/RecordingService.cs:509-515`). It later assigns only the in-memory
`_previewOverlay` field (`src/AgentEyes.Core/RecordingService.cs:568-573`). `BeginSession` publishes
the separate `_manifest` object before capture starts (`src/AgentEyes.Core/RecordingService.cs:1000-1073`).
The first assignment of `PreviewOverlayCorner`, `Shape`, `Inset`, and `Circle` to that manifest is
inside the Stop path (`src/AgentEyes.Core/RecordingService.cs:778-795`).

Concrete failure scenario: start a camera recording through the Control API with no HUD, then the
process or machine stops before `RecordingService.Stop` writes its update. The durable start
manifest says `CameraFile: camera.mp4` but contains no `PreviewOverlay*` fields. Recovery therefore
returns false from `NeedsCompose`, leaving the same separate camera file this issue exists to fix.
The preset framing was known before the first byte but was not put in the first manifest. This does
not meet AC1's persistence requirement or the recovery guarantee of the start-manifest design.

## Independent checks

- Release build completed with exit 0: `Build succeeded`, 4 warnings, 0 errors.
- The full test assembly positively collected and completed with exit 0: 1,345 passed, 0 failed,
  0 skipped, total 1,345, duration 5m01s.
- The issue-specific class positively completed with exit 0: 28 passed, 0 failed, total 28,
  duration 217ms.
- Those 28 tests assert argument strings and planning predicates but never execute
  `CameraCompose.Run`, `Swap`, or a real ffmpeg composition. The recompose and positive-offset
  probes above both returned a successful CLI exit while producing the wrong frame, demonstrating
  the suite's reach limit rather than treating a green run as proof of those paths.
- I did not perform a new physical-camera recording. The only real-camera proof on the branch was
  produced by the developer seat, and the required independent QA report is absent as stated above.
