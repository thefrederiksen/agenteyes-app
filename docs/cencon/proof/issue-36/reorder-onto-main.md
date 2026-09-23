# Issue #36 - reordered onto main

Issue #36 passed QA 11/11 on branch `issue-36-circular-camera-overlay`, which sat on the OLD
issue-35 branch, which sat on the OLD issue-33 branch. Since that pass, #28, #33 and #35 all
merged to `main` and were substantially REWORKED on the way, so the branch #36 was verified on
no longer exists anywhere in `main`'s history.

This note records the replay of #36's three commits onto `main` (`38a8047`), what each conflict
was, and which side won.

    git rebase --onto origin/main ffd3970 issue-36-circle-on-main

Branch: `issue-36-circle-on-main`. The original `issue-36-circular-camera-overlay` was NOT
force-pushed over - it is left intact as the record of what QA actually verified.

## The governing rule

**Main wins on mechanism; #36 contributes the circle.** Every guarantee `main` carries into these
files was established over multiple review-gate rounds and is not re-litigated here. Where #36's
version of a mechanism differs from main's, main's stands and #36's feature is re-expressed
through it.

## The five conflicts

### 1. `src/AgentEyes.App/CameraPreviewController.cs` - one hunk, BOTH sides kept

Both sides added members to the same place in the class.

| Side | Content | Outcome |
|------|---------|---------|
| main | `IsDisposed`, and the long `HoldsCamera` doc recording that AN UNRESOLVED OPEN COUNTS (issue #35 gate round 1, defect 3) | KEPT, verbatim |
| #36 | the `SourceSize` property - the camera's real frame size, read under `_gate` | KEPT, verbatim |

#36 also carried a one-line `HoldsCamera` summary ("True while this controller holds (or is
opening) a camera"). That line was DROPPED in favour of main's, which is the same claim plus the
rule the gate forced: an open that was waited for and did not finish may already hold the device,
so an empty `_session` field is not "nothing is held". Losing that comment would have invited the
next reader to re-introduce exactly the absence the gate rejected.

Final order: `IsDisposed` -> `SourceSize` -> `HoldsCamera`. `HoldsCamera`'s body is untouched.

### 2. `src/AgentEyes.App/Config.cs` - one hunk, main wins, #36 ADDS

main's side of the hunk was empty; #36's side added its overlay geometry AND its own copy of the
preset-editor window properties.

- **KEPT from #36:** `HudPreviewShape`, `HudPreviewCircleCentreX/Y`, `HudPreviewCircleDiameter`,
  `HudPreviewInsetFraction` - the six new scalars, placed directly under `HudPreviewCorner`.
- **DROPPED from #36:** its `PresetEditorTab` / `Width` / `Height` / `Left` / `Top` block. Main
  already declares those (lines 26-30, from the reworked #35). Taking #36's copy would have been
  five duplicate properties and a compile error.
- **UNTOUCHED:** everything #33 put here (`HudWidth`, `HudHeight`, `HudPreviewVisible`,
  `HudPreviewMode`, `HudPreviewCorner`) and `Config.Save`'s single background writer. #36 adds
  properties only; it does not go near the write path, so the newest snapshot still lands last.

### 3. `src/AgentEyes.Core/Video/FfmpegCameraPreview.cs` - two hunks

**Hunk 1 - class members. BOTH sides kept.** Main's `ProcessId`, `OutputPath` and `IsAbandoned`
are the `IStrandedCameraProcess` surface that hands a surviving preview process to
`StrandedCameraOwner`, and `IsAbandoned` asks the process on every read rather than remembering
the outcome of a kill that failed. All three kept verbatim, then #36's `SourceSize` added after
them. No path here reports a release it has not established, and this change adds none.

**Hunk 2 - process start. MAIN WINS on the seam; #36 supplies the handler.**

    main:  proc.Start(line => preview._stderr.AppendLine(line));
    #36:   proc.ErrorDataReceived += ...; if (!proc.Start()) throw ...; proc.BeginErrorReadLine();
    kept:  proc.Start(preview.OnStderrLine);

#36 was written against a raw `System.Diagnostics.Process`. Main replaced that with the
`ICameraPreviewProcess` seam (`void Start(Action<string> onStderrLine)`) - the injection point the
ownership decisions are tested through, because "ffmpeg ignored the kill" and "Kill threw" are not
states a real ffmpeg can be asked to enter. Reinstating #36's raw wiring would have deleted that
seam and with it main's stranded-process tests. Main's call stands, passing #36's `OnStderrLine`,
which does both jobs: append to the diagnosis buffer, and pick the camera's frame size out of
ffmpeg's `Input #0` block once.

One consequence of #36 that is a strengthening and was kept: `_stderr` is now written by the
stderr callback thread, so `Stop`'s `string err = _stderr.ToString()` is taken under
`lock (_stderr)`.

### 4. `tests/AgentEyes.Tests/CameraPreviewTests.cs` - one hunk, BOTH sides kept

Purely additive on both sides of `FakeCameraSession`. Main's `Pid` / `ProcessId` / `OutputPath` /
`IsAbandoned` (the fake that survives every kill - issue #35 gate round 1) and #36's settable
`SourceSize` are unrelated members; both are present. No coverage from either side was dropped.

### 5. `src/AgentEyes.App/HudWindow.cs` - two hunks, MAIN WINS on structure

**Hunk 1 - `ApplyPreviewState()`.**

- **KEPT from main:** the `_svc.PreviewArmed = _preview.ArmNextRecording;` block and its comment.
- **KEPT from #36:** `_svc.SetPreviewOverlay(_preview.ManifestOverlay);` replacing main's
  `_svc.SetPreviewOverlayCorner(_preview.ManifestCorner);`. This is #36's AC4 - the corner alone
  becomes the whole framing. `SetPreviewOverlayCorner` no longer exists on `RecordingService`
  after #36's (cleanly auto-merged) change there, so main's line could not have compiled.
- **DROPPED from #36:** `if (fromUser) SavePreviewChoices();`. Main REMOVED the `fromUser` flag
  entirely. Review Gate round 1 on PR #34 found that every HUD ever built rewrote config.json
  while it was being put on screen, because the constructor passed `fromUser: true` and no
  call-graph guard can see an argument. Main's fix is two methods - `ApplyPreviewState()` persists
  nothing, `ApplyAndRememberPreviewChoice()` applies and saves - and `HudResponsivenessTests`
  asserts against the IL that the constructor does not reach `SavePreviewChoices`. Re-introducing
  #36's flag would have broken that assertion and restored the defect. #36 loses nothing: a
  person's click already routes through `ApplyAndRememberPreviewChoice`.

**Hunk 2 - `SavePreviewChoices()`.**

    main:  _cfg.HudPreviewCorner = PreviewNames.Text(_preview.Corner);  _cfg.Save();
    #36:   HudOverlayConfig.Write(_cfg, _preview.Framing);              _cfg.Save();
    kept:  HudOverlayConfig.Write(_cfg, _preview.Framing);              _cfg.SaveWithoutBlockingTheUiThread();

#36's `HudOverlayConfig.Write` is a superset of main's single assignment - it writes the corner
too - so main's line is subsumed, not lost. The SAVE CALL is main's: `SavePreviewChoices` runs on
the dispatcher that serves the Stop button, so it serialises on the UI thread and writes on a
background one. #36's `_cfg.Save()` would have put file I/O back on that dispatcher.

Nothing in either hunk touches `HudSizeMemory`, `HudUserResize` or `HudPreviewSizing`: the resize
allowlist still has one mutator, written only by `HudUserResize`'s positively-identified gestures;
nothing here observes layout; `HidePanel` is still idempotent and still reports the resize canary
itself.

## One test updated by the resolution

`CameraOverlayUiTests.Hud_WritesItsFramingToTheConfigOnly_NeverToAPreset` reads the source of
`SavePreviewChoices` and asserted `_cfg.Save()`. That literal is main's changed API, not #36's
guarantee. The assertion now reads `_cfg.SaveWithoutBlockingTheUiThread()`, which keeps the test's
claim (the framing is persisted, to the CONFIG, never to a preset - `PresetStore` and
`CapturePreset` are still asserted absent from the whole file) and additionally pins main's
UI-thread rule. No assertion was removed or relaxed. This was the ONLY test change in the reorder.

## AC5 is intact - `camera.mp4` is still not cropped

The reorder touched no part of the camera recording path.
`CameraOverlayUiTests.TheOverlay_NeverReachesTheCameraRecorder` - which reads `FfmpegArgs.cs` and
`FfmpegCameraRecorder.cs` and asserts neither knows the overlay exists - passes. The circle
remains preview-and-metadata only.

## Verification

Built and tested from an isolated worktree (`D:\ReposFred\agenteyes-dev36-reorder`), restored
first, output at `bin\x64\Release\`.

    dotnet build AgentEyes.sln -c Release  ->  Build succeeded.  4 Warning(s)  0 Error(s)
    dotnet test  AgentEyes.sln -c Release  ->  Passed!  Failed: 0, Passed: 1283, Skipped: 0

1283 = main's 1188 plus #36's 95. No test from #28, #33, #35 or #36 fails.

All 4 warnings are main's pre-existing baseline, in files this branch does not touch:
`PostRecordingQueueTests.cs` (xUnit1031, x2) and `StrandedCameraOwnerTests.cs` (xUnit2031, x2).
The reorder introduces no new warning.

## NOT verified in this reorder - and why

This reorder was done under an explicit instruction not to launch `AgentEyesApp.exe`, start a
recording, take screenshots, drive the Control API or UIA, or run any smoke script, because the
machine was in use. The following therefore rest on issue #36's ORIGINAL QA pass
(`docs/cencon/proof/issue-36/qa/`) plus the source-level tests above, and have NOT been
re-observed against `main`:

- **AC1 / AC6** - the HUD rendering the circle (and the rectangle) on screen, with screen content
  visible at the bounding-box corners. Asserted here only through `CameraOverlayUiTests` reading
  `PaintCameraFrame`'s source for the `ImageBrush` viewbox crop.
- **AC2 / AC3** - the preset editor's live-image centre/diameter controls, and editor-to-HUD
  framing match.
- **AC4 / AC5 / AC10** - `manifest.json` contents and the `ffprobe` width/height comparison across
  circle, rectangle and preview-off recordings. AC5 is covered here by the source assertion that
  nothing in the recorder path knows the overlay exists, which is strong but is not an `ffprobe`
  reading.
- **AC8 / AC9** - the killed-feed run and the dropped-frame comparison.

The re-verification worth running, given how much of `main` changed underneath: **AC3 and AC7**
together. Those are the two that cross the seam this reorder actually rewired - preset -> config
-> HUD - and #33's and #35's rework landed on both ends of it. AC1/AC6 rendering is second.
