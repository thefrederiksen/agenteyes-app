# Issue #51 proof - the HUD no longer erases the preset's camera framing

This document exists because the Review Gate on PR #52 rejected the hotfix for having none
(`docs/cencon/review/pr52-issue51-gate.md`, defect 2). The verification had been done and then
DELETED during cleanup before anyone else could check it. Proof that is destroyed is not proof.
The recording this document refers to was kept until this file was committed.

## The defect, from the owner's own recording

`2026-08-30_223040_video`, started from the tray with the preset "Demo Screen Capture With Camera"
on v1.8.0:

```
22:30:40.607  StartVideo: framing recorded at start - circle bottom-right inset 0.21
22:30:42.093  SetPreviewOverlay: overlay=(none)
22:31:09.704  PostRecording: no camera to compose
```

The framing was recorded and then destroyed 1.5 seconds later. `HudWindow.ApplyPreview` calls
`SetPreviewOverlay` on every apply including the one at construction, and
`HudPreviewState.ManifestOverlay` is null whenever the preview PANEL is not showing - the default.
The stop wrote no framing, `NeedsCompose` returned false, and `camera.mp4` was left beside an
uncomposed `recording.mp4`.

## The fix, on the same path

`2026-08-30_232327_video`, started with the GUI **REC button** on installed **v1.8.1**, preview
panel hidden (`PreviewArmed: false` on `/status` at start):

```
23:23:27.616  StartVideo: framing recorded at start - circle bottom-right inset 0.21
23:23:29.899  hud: watching for user resizes            <- the HUD is constructed, as in the failing run
23:23:29.901  SetPreviewOverlay: (none) - the preview is not framing anything;
              keeping circle bottom-right inset 0.21 (centre 0.542,0.489 diameter 0.424)
23:23:49.192  Calibrate: noise floor -88.0 dBFS, RMS -64.9 dBFS -> gate at -78.0 dBFS (measured)
23:23:50.356  CameraCompose] Run: dir=...2026-08-30_232327_video
23:23:52.909  stage compose: done in 2.6s
```

The HUD sends the SAME null at the SAME point in the sequence. It is now kept.

### AC3 - the manifest

```
PostProcessing  {'mux': 'done', 'compose': 'done', 'thumbnail': 'done'}
ComposedCamera  True
framing         bottom-right circle 0.21452513966480463
CameraFile      camera.mp4
```

Files on disk - the composed video, the preserved screen-only cut, the untouched camera, and the
pre-audio-processing capture:

```
49823545  camera.mp4
 1322894  recording.mp4            (composed)
  599414  recording.original.mp4   (pre audio processing, issue #83)
  664548  recording.screen.mp4     (pre compose, issue #47)
```

### AC4 - the frame

Frame at t=6s from the composed `recording.mp4`, differenced against the same frame of the
preserved `recording.screen.mp4`. The geometry is the one the log recorded, a 412x412 inset at
(1470, 630):

| point | RGB difference | meaning |
|---|---|---|
| inset centre | 213 | the camera IS drawn - 0 would mean nothing rendered |
| inset bbox top-left | 0 | screen shows through - a real circular mask |
| inset bbox bottom-right | 0 | screen shows through |

The frame was also LOOKED AT, not only measured: a circular inset in the bottom-right corner with a
clean antialiased rim, the screen visible around it. No image is committed here - this repository is
public and the frame is a picture of the owner's screen and face.

## AC1 / AC2 - the tests, and what they can and cannot prove

The gate's defect 1 was that the original tests guarded the SETTER and never reached the seam the
outage lived in: deleting the seed in `StartVideo`, or the preset's handoff of it, left all four
tests green while the outage returned.

The guards added under issue #53 are SOURCE guards, and the reason is this suite's own contract:
`StartVideo` opens ffmpeg and a physical webcam, and the suite is required to run fast and silent
with neither. A source guard cannot prove the seam WORKS - the runtime evidence above does that -
but it does prove the seam is still WIRED, which is the regression the gate described.

Three negative controls, each run and each observed to fire:

| control | what was changed | result |
|---|---|---|
| 1 | `_previewOverlay = overlay?.Canonical();` in `StartVideo` replaced with `= null` (the pre-#47 behaviour) | `StartVideo_seeds_the_session_framing_from_that_parameter` FAILED |
| 2 | `CapturePreset` stopped passing `p.Overlay` | `The_preset_hands_its_own_framing_to_StartVideo` and `A_recording_with_no_camera_still_hands_over_no_framing` FAILED |
| 3 | a fourth mid-session `_previewOverlay = null;` added | `StartVideo_seeds_the_session_framing_from_that_parameter` FAILED |

Control 3 guards the property that clearing happens ONLY at the three session boundaries - the
stop, the failed-start rollback, and `Reset` - which is what keeps a framing from bleeding into the
next recording, or onto a camera-less recording whose manifest must keep the shape issue #33 AC11
and issue #36 AC10 require.

The earlier setter tests keep their own negative control from PR #52: run against the compiled
v1.8.0 core, two of the four fail, both naming the null-erasure assertion.

## AC5 - the gate

`dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Error(s).
`dotnet test AgentEyes.sln -c Release`: **1397 passed, 0 failed**.

Run with AgentEyes STOPPED. With the app running, `HudPreviewSizingOrderTests` fails on shared HUD
state - the same class of contention that made `SetupEngineTests` and `PublishedPluginAssetTests`
fail during the PR #49 gate rounds, when other worktrees were running tests concurrently. A full
run is only meaningful when nothing else is competing for that state.

## What is still open

Issue #50 - the independent QA seat. This document was written by the seat that wrote the code,
which is the very gap #50 tracks, and the gate's defect 3 raised it again. It is recorded, not
resolved.
