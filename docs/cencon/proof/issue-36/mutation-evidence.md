# Issue #36 - mutation evidence: every new check run against a KNOWN-BAD product

A check only ever run against the state you hope passes has demonstrated nothing. Each section
below removes exactly ONE thing this feature claims, rebuilds the solution, and runs the issue-36
checks. **FIRED** means they failed, i.e. the check does its job. **SURVIVED** would be a defect
in the check, not a curiosity. A mutation that does not COMPILE is recorded as such - that is also
a closed door, just a different one.

To reproduce any row by hand: apply the one-line edit shown, run
`dotnet build AgentEyes.sln -c Release`, then
`dotnet test AgentEyes.sln -c Release --no-build --filter "FullyQualifiedName~CameraOverlay"`, and
undo the edit. Nothing in this file needs a script.

Baseline at the time of the sweep, unmutated: **Failed: 0, Passed: 1135** over the whole suite. The
per-mutation "Passed" figures below count only the issue-36 and issue-33 classes the sweep filtered
to (145 checks), not the whole suite. Three further checks
(`CameraFrameSizeRealCaptureTests`, added after the sweep from a real camera capture) bring the final
suite to **Failed: 0, Passed: 1138**; they read a committed fixture and no mutation above touches
them.

## M1 the default circle sits in the UPPER portion of the frame (E3) - FIRED

- File: `src/AgentEyes.Core/Preview/CameraOverlay.cs`
- Mutation: `public const double DefaultCentreY = 0.42;` -> `public const double DefaultCentreY = 0.50;`
- What it breaks: the default framing stops being above the middle, where a seated head is
- Result: `Failed!  - Failed:     3, Passed:   142`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayGeometryTests.Circle_Default_IsCentredHorizontallyAndHighInTheFrame`
  - `AgentEyes.Tests.CameraOverlayManifestTests.Preset_Clone_CopiesTheOverlayInsteadOfSharingIt`
  - `AgentEyes.Tests.CameraOverlayManifestTests.Preset_SavedBeforeThisFeature_ReadsBackAsACircleWithTheDefaults`

## M2 circle is the DEFAULT shape (AC1) - FIRED

- File: `src/AgentEyes.Core/Preview/PreviewOverlay.cs`
- Mutation: `Rectangle => CameraOverlayShape.Rectangle,
            _ => CameraOverlayShape.Circle,` -> `Circle => CameraOverlayShape.Circle,
            _ => CameraOverlayShape.Rectangle,`
- What it breaks: an unknown or absent shape reads as rectangle instead of circle
- Result: `Failed!  - Failed:     6, Passed:   139`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayGeometryTests.Canonical_UnknownSpellings_AreReplacedByTheDefaultsBeforeAnythingIsStored`
  - `AgentEyes.Tests.CameraOverlayGeometryTests.PreviewNames_UnknownShape_ReadsAsTheDocumentedDefault`
  - `AgentEyes.Tests.CameraOverlaySyncTests.Read_HandEditedNonsenseInConfig_IsReadAsTheDocumentedDefaults`

## M3 the circle is clamped by the frame's ASPECT, not by half its diameter - FIRED

- File: `src/AgentEyes.Core/Preview/CameraOverlay.cs`
- Mutation: `double halfX = diameter / (2.0 * aspect);` -> `double halfX = diameter / 2.0;`
- What it breaks: a circle at the left edge is pushed in by the wrong amount on any non-square frame
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayGeometryTests.ClampedTo_CircleOffTheLeftEdge_IsPushedInByTheFramesOwnAspect`

## M4 the crop is a SQUARE in pixels, so the overlay is round - FIRED

- File: `src/AgentEyes.Core/Preview/CameraOverlay.cs`
- Mutation: `double width = c.Diameter / aspect;` -> `double width = c.Diameter;`
- What it breaks: the circle becomes an oval on any non-square frame
- Result: `Failed!  - Failed:     4, Passed:   141`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayGeometryTests.ClampedTo_TallNarrowFrame_ShrinksTheCircleUntilItFitsAcross`
  - `AgentEyes.Tests.CameraOverlayGeometryTests.PixelBounds_IsASquare_SoTheOverlayIsRoundAndNotAnOval`
  - `AgentEyes.Tests.CameraOverlayGeometryTests.PixelBounds_MovingTheCentre_MovesWhichPixelsAreInsideTheCircle`
  - `AgentEyes.Tests.CameraOverlayGeometryTests.Viewbox_IsTheCirclesBoundingSquare_InFractionsOfTheFrame`

## M5 the stop COPIES the overlay shape into the manifest already on disk - FIRED

- File: `src/AgentEyes.Core/RecordingService.cs`
- Mutation: `m.PreviewOverlayShape = manifest.PreviewOverlayShape;` -> `(line deleted)`
- What it breaks: the shape is set on the session manifest and written to no file at all
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayStopCopyTests.EveryOverlayFieldTheSessionSets_IsAlsoCopiedIntoTheManifestOnDisk`

## M5b the stop copies the CIRCLE GEOMETRY too - FIRED

- File: `src/AgentEyes.Core/RecordingService.cs`
- Mutation: `m.PreviewOverlayCircle = manifest.PreviewOverlayCircle;` -> `(line deleted)`
- What it breaks: the recording records a shape with no geometry behind it
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayStopCopyTests.EveryOverlayFieldTheSessionSets_IsAlsoCopiedIntoTheManifestOnDisk`

## M6 no overlay means the fields are ABSENT from manifest.json (AC10) - FIRED

- File: `src/AgentEyes.Core/Manifest.cs`
- Mutation: `public string? PreviewOverlayShape { get; set; }` -> `public string? PreviewOverlayShape { get; set; } = "circle";`
- What it breaks: a preview-off recording's manifest grows a field it never had
- Result: `Failed!  - Failed:     2, Passed:   143`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayManifestTests.Manifest_WithNoOverlayAtAll_WritesNoneOfTheOverlayFields`
  - `AgentEyes.Tests.CameraOverlayManifestTests.Manifest_WrittenBeforeThisFeature_StillLoadsWithNoOverlay`

## M7 the preset's framing SEEDS the HUD (AC3, AC7) - FIRED

- File: `src/AgentEyes.App/HudOverlayConfig.cs`
- Mutation: `var overlay = (preset.Overlay ?? new CameraOverlaySettings()).Canonical();
            Write(cfg, overlay);` -> `var overlay = (preset.Overlay ?? new CameraOverlaySettings()).Canonical();`
- What it breaks: the framing chosen in the editor never reaches the HUD
- Result: `Failed!  - Failed:     4, Passed:   141`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlaySyncTests.HudPreviewState_BuiltFromASeededConfig_ShowsThePresetsFraming`
  - `AgentEyes.Tests.CameraOverlaySyncTests.ManifestOverlay_WhenTheOverlayIsBeingShown_IsTheWholeFraming`
  - `AgentEyes.Tests.CameraOverlaySyncTests.Seed_ARectanglePreset_ReachesTheHudAsARectangle`
  - `AgentEyes.Tests.CameraOverlaySyncTests.Seed_PutsThePresetsFramingWhereTheHudReadsIt`

## M8 the circle host has NO background (AC1) - FIRED

- File: `src/AgentEyes.App/HudWindow.cs`
- Mutation: `_cameraHost.Background = circle ? Brushes.Transparent : Brushes.Black;` -> `_cameraHost.Background = Brushes.Black;`
- What it breaks: the circle is boxed back in, so no screen shows through the bounding-box corners
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.Hud_CircleOverlay_HasNoBackgroundAndNoBorder`

## M8b the circle host is SQUARE, or the ellipse is not a circle - FIRED

- File: `src/AgentEyes.App/HudWindow.cs`
- Mutation: `_cameraHost.Height = circle ? inset : double.NaN;` -> `_cameraHost.Height = double.NaN;`
- What it breaks: the overlay renders as an oval stretched to the frame's aspect
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.Hud_CircleOverlay_HasNoBackgroundAndNoBorder`

## M9 the editor never GUESSES where the camera picture is - FIRED

- File: `src/AgentEyes.App/PresetEditor.xaml.cs`
- Mutation: `if (_cameraPreview.SourceSize is not { } camera) return null;` -> `var camera = _cameraPreview.SourceSize ?? new CameraFrameSize(4, 3);`
- What it breaks: an assumed camera size draws a convincing circle over the wrong part of the picture
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.PresetEditor_TheAdorner_NeverGuessesWhereTheCameraPictureIs`

## M10 the camera size is read from ffmpeg's INPUT block, not its output - FIRED

- File: `src/AgentEyes.Core/Video/CameraFrameSize.cs`
- Mutation: `if (!inInput) continue;` -> `if (false) continue;`
- What it breaks: the padded 320x240 output buffer is mistaken for the camera's own frame
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlaySyncTests.CameraFrameSize_OutputBlockOnly_IsNull`

## M11 the diameter control really covers the model's range (AC2) - FIRED

- File: `src/AgentEyes.App/PresetEditor.xaml`
- Mutation: `x:Name="CircleSizeSlider" Grid.Row="2" Grid.Column="1" Minimum="0.1" Maximum="1"` -> `x:Name="CircleSizeSlider" Grid.Row="2" Grid.Column="1" Minimum="0.4" Maximum="0.7"`
- What it breaks: the person cannot ask for a circle the model would accept
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.PresetEditor_CircleControls_CoverTheWholeFrameAndAreDrivableByAutomation`

## M11b moving a slider actually does something (AC2) - FIRED

- File: `src/AgentEyes.App/PresetEditor.xaml`
- Mutation: `Value="0.42" SmallChange="0.01" LargeChange="0.05"
                                                Margin="8,0" Vertical` -> `Value="0.42" SmallChange="0.01" LargeChange="0.05"
                                                Margin="8,0" Vertical`
- What it breaks: the up/down control is wired to nothing
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.PresetEditor_CircleControls_CoverTheWholeFrameAndAreDrivableByAutomation`

## M12 nothing framed means nothing recorded (AC10) - FIRED

- File: `src/AgentEyes.App/HudPreviewState.cs`
- Mutation: `Visible && CameraAvailable && Mode == PreviewMode.Both ? _overlay.Canonical() : null;` -> `_overlay.Canonical();`
- What it breaks: a screen-only preview writes overlay geometry it never framed
- Result: `Failed!  - Failed:     6, Passed:   139`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlaySyncTests.ManifestOverlay_WhenNothingWasFramed_IsNull`
  - `AgentEyes.Tests.HudPreviewStateTests.ManifestCorner_NoOverlayFramed_IsNull`
  - `AgentEyes.Tests.HudPreviewStateTests.WithNoFeed_NoCornerReachesTheManifest`

## M13 Clone copies the overlay instead of sharing it - FIRED

- File: `src/AgentEyes.App/CapturePreset.cs`
- Mutation: `Overlay = (Overlay ?? new CameraOverlaySettings()).Clone(),` -> `Overlay = Overlay,`
- What it breaks: two presets end up sharing one framing object
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayManifestTests.Preset_Clone_CopiesTheOverlayInsteadOfSharingIt`

## M14 the HUD's inset size comes from the chosen framing (E5) - FIRED

- File: `src/AgentEyes.App/HudWindow.cs`
- Mutation: `double inset = Math.Max(MinInsetWidth, surfaceWidth * _preview.InsetFraction);` -> `double inset = Math.Max(MinInsetWidth, surfaceWidth * 0.30);`
- What it breaks: the size control is ignored and issue #33's constant is used instead
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.Hud_InsetSize_ComesFromTheChosenFramingAndNotAHardCodedConstant`

## M15 the HUD draws the chosen CROP, not the whole shrunken frame - FIRED

- File: `src/AgentEyes.App/HudWindow.cs`
- Mutation: `var viewbox = _preview.Circle.Viewbox(frame.Width, frame.Height);` -> `var viewbox = new OverlayRect(0, 0, 1, 1);`
- What it breaks: the circle becomes a round hole onto the middle of the frame
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.Hud_CircleOverlay_ShowsTheChosenCropRatherThanTheWholeShrunkenFrame`

## M16 the editor SAVES the framing onto the preset - FIRED

- File: `src/AgentEyes.App/PresetEditor.xaml.cs`
- Mutation: `p.Overlay = ReadOverlay();` -> `(line deleted)`
- What it breaks: everything chosen in the editor is discarded on Save
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.PresetEditor_SavesAndLoadsTheOverlayWithThePreset`

## M17 the HUD's status line names the SHAPE (the only assertable surface) - FIRED

- File: `src/AgentEyes.App/HudWindow.cs`
- Mutation: `? " " + PreviewNames.Text(_preview.Shape) + " " + PreviewNames.Text(_preview.Corner)` -> `? " " + PreviewNames.Text(_preview.Corner)`
- What it breaks: nothing can assert the overlay shape, since the HUD cannot be screenshotted
- Result: `Failed!  - Failed:     1, Passed:   144`
- Checks that caught it:
  - `AgentEyes.Tests.CameraOverlayUiTests.Hud_StatusLine_NamesTheShape_SinceTheHudCannotBeScreenshotted`

## Summary

All 20 mutations were caught (FIRED, or refused to compile). No check in
this feature passes by finding nothing.
