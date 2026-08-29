# Release blocker: the HUD sizing tests only passed on a big enough monitor

Release v1.7.0 failed at the workflow's `Run tests` step
(`thefrederiksen/agenteyes-app`, run 33258545700, 2026-08-29). Nothing was published;
the failure stopped the workflow before the publish step and the latest public release
is still v1.4.9.

This is a TEST defect, not a product defect. No product code is changed by the fix.

## What CI actually reported

```
Failed!  - Failed:    11, Passed:  1272, Skipped:     0, Total:  1283
```

Eleven failures, all in the issue #33 HUD resize area, and every numeric one the same pair:

```
Assert.Equal() Failure: Values are not within 1 decimal place
Expected: 1560 (rounded from 1560)
Actual:   1044 (rounded from 1044)
```

The eleven:

```
HudPreviewSizingOrderTests.AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary
HudPreviewSizingOrderTests.AResizeNoGestureClaimed_ReachesTheLogOnTheOrdinaryStop
HudPreviewSizingOrderTests.AResizeWithNoGestureBehindIt_IsNeverRemembered
HudPreviewSizingOrderTests.DraggingTheSizingBorder_IsRemembered
HudPreviewSizingOrderTests.HidingAndShowingAgainInOneRecording_ReopensAtTheResizedSize
HudPreviewSizingOrderTests.ResizeToThreeTimesTheDefault_ThenStop_IsWhatTheNextRecordingOpensAt
HudPreviewSizingOrderTests.SnappingTheWindowToAScreenEdge_IsRemembered
HudPreviewSizingOrderTests.StoppingWhileThePreviewIsStillOn_LeavesTheResizedSizeToSave
HudUserResizeTests.ADragOfTheSizingBorder_RecordsTheSizeTheWindowWasLeftAt
HudUserResizeTests.ALoopThatEndedAtADifferentSize_RecordsTheSizeItEndedAt
HudUserResizeTests.AMoveAfterAResize_DoesNotRecordAgain
```

That set is exactly, and only, the tests that require the window to REALLY BECOME 1560
wide. Every other HUD sizing test passed, including the ones that resize to 660 or
maximise. Two of the eleven failed on a substring instead of a number, and they name the
cause outright:

```
String:    "hud: the HUD ended up at 1044x400 but the"...
Not found: "1560"
```

The height, 400, was correct throughout. Only the width was wrong.

## Diagnosis

**Windows will not make a resizable window wider than its maximum tracking size**, which
is the virtual screen plus the window frame (`SM_CXMAXTRACK` / `SM_CYMAXTRACK`).
DefWindowProc enforces it on `WM_WINDOWPOSCHANGING`, so it applies to every route a size
can arrive by - a `Window.Width` assignment, a bare `SetWindowPos`, a border drag alike.
That is why all three rig mechanisms produced the same clamped number.

Measured on the developer machine:

```
SM_CXSCREEN        = 1920
SM_CXVIRTUALSCREEN = 3840
SM_CXMAXTRACK      = 3860      <- virtual screen + 20
SM_CXFRAME         = 4
SM_CXPADDEDBORDER  = 4
```

The GitHub Windows runner's desktop is 1024x768. `1024 + 20 = 1044` - exactly what CI
reported, to the pixel. 1560 was simply not an available size on that machine. The
production code recorded the 1044 the window really was, which is correct behaviour; the
assertion compared it against a number that was only ever true of the developer's monitor.

### It is NOT a DPI or scaling mismatch

`1560 / 1044 = 1.4943` looks like a 1.5x scaling factor, and that is a coincidence.

* Both sides of every failing assertion were already in the SAME unit - WPF
  device-independent pixels. The rigs assign `Window.Width`; the production code reads
  `Window.ActualWidth`.
* The one place the suite crosses into device pixels, `HudRig.ResizeTheHwnd`'s
  `SetWindowPos`, already converts through `VisualTreeHelper.GetDpi(Window)`.
* A scaling error would have moved the HEIGHT too (400 -> 267). Every height was right.
* The CI runner is at 100% scaling, where DIP and device pixels are identical, so a
  DIP/device mismatch could not have failed there at all.

## The fix

Test-only. The absolute pixel count is replaced by a MEASURED one:
`tests/AgentEyes.Tests/TheDisplayUnderTest.cs` asks a real window, in the same shape the
rigs use, for an impossible size and reports what Windows gives it back. That is the
ceiling for this machine, expressed in exactly the device-independent pixels the
assertions are written in - no unit conversion, and no assumption about the process's DPI
awareness.

`TheDisplayUnderTest.AtMost(3 * 520, 400)` therefore yields 1560 wherever it fits (this
developer machine, unchanged) and the display's ceiling where it does not (1044 on the
runner). AC7's meaning - "three times the default preview width" - is preserved as the
size ASKED FOR; what changes is that the test no longer asserts a number the machine
cannot produce.

The remaining precondition is asserted by name rather than left to fail as a mystery
number: `TheDisplayRunningThisSuite_CanHoldTheWindowsTheseTestsResize`, one per sizing
test class, fails loudly on a display with no room to enlarge a window.

## Local reproduction and verification

CI's display cannot be created here, but its CONSTRAINT can: WPF serves
`WM_GETMINMAXINFO` from a window's `MaxWidth` / `MaxHeight`, which is the same mechanism
`SM_CXMAXTRACK` uses. Capping every window in these two test classes at 1044x788
reproduces a 1024x768 desktop faithfully.

**Known-bad arm** - the literal `1560` restored, windows capped:

```
Failed!  - Failed:    11, Passed:   126, Skipped:     0, Total:   137
```

and the eleven names are byte-for-byte the eleven CI reported. The reproduction is real,
not an approximation.

**Fixed arm** - the same caps, the measured size:

```
Passed!  - Failed:     0, Passed:   137, Skipped:     0, Total:   137
```

**Fail-open check** - the new precondition test is not decoration. Capped at 600x500:

```
HudPreviewSizingOrderTests.TheDisplayRunningThisSuite_CanHoldTheWindowsTheseTestsResize [FAIL]
   This display allows at most a 600x500 window, which cannot take the grip drag in
   DraggingTheGrip_IsRemembered (660x460). That test would measure a clamp rather than a drag.
```

**Uncapped, on the developer machine** - the whole suite:

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded. 0 Error(s)
dotnet test  AgentEyes.sln -c Release   -> Passed! Failed: 0, Passed: 1285, Total: 1285
```

1285 = main's 1283 plus the two new precondition tests.

## Would it pass at a different display scaling?

Argued from the units, since only one scaling can be tested on one machine.

Every value on both sides of these assertions is a WPF device-independent pixel:
`TheDisplayUnderTest` measures `Window.ActualWidth`, the rigs assign `Window.Width`, and
the production code records `Window.ActualWidth`. No literal survives on the assertion
side, so there is nothing left for a scaling factor to be applied to or omitted from. The
one device-pixel crossing (`SetWindowPos`) converts in both directions through the
window's own `VisualTreeHelper.GetDpi`, and its round trip is exact by construction: the
probe reports `maxTrackDevicePx / scale`, and the rig sends
`round(that * scale) = maxTrackDevicePx` straight back.

At any scaling the ceiling simply moves - a 1920x1080 monitor gives roughly 1940 DIP at
100%, 1552 at 125%, 1293 at 150% and 970 at 200% - and `AtMost` takes whichever of 1560
and the ceiling is smaller. The comparison is then always between a size the display has
already been observed to give a window and the size the window took, which is a tautology
about this machine rather than a claim about any machine.

What could still fail is a display with genuinely no room, and that now fails as a named
precondition rather than as a wrong number.

## Sweep for the same class of defect

The whole test suite was checked for other machine-dependence: absolute device pixels, a
screen size, a monitor count, a DPI, or a path that only exists on this machine.

* `new Window` and `ApartmentState.STA` appear in exactly two test files -
  `HudUserResizeTests.cs` and `HudPreviewSizingOrderTests.cs`. They are the only tests
  that create a real window, so they are the only ones a display can constrain. Both are
  fixed here.
* No test source references `SystemParameters`, `GetSystemMetrics`, `SM_CX*`,
  `VirtualScreen`, `PrimaryScreen`, `Screen.`, `MonitorFromWindow` or `EnumDisplay*`.
  (Binary matches in `bin/` are the product assemblies, not test code.)
* The other absolute pixel literals in the suite - `1920x1080`, `3840x2160`, `1080x1920`
  in `FfmpegArgsTests`, `CameraOverlayGeometryTests`, `CameraFrameSizeRealCaptureTests`,
  `CaptureServiceTests` - are all PASSED IN as arguments to pure geometry or
  string-formatting functions. Nothing reads them from the machine, so they are
  reproducible anywhere.
* The `C:\Users\...` strings in `CameraFailurePathTests`, `FfmpegArgsTests`,
  `RunningAppTests` and `SetupEngineTests` are fixtures for parsing and path arithmetic.
  Nothing touches the filesystem with them.
* `PluginRegistryChannelTests` derives paths from
  `Environment.GetFolderPath(ProgramFiles / System)`, which resolve on any Windows box.
* No test reads `ProcessorCount`, `MachineName` or `UserName`.

The display-size class was therefore confined to the two files fixed here.
