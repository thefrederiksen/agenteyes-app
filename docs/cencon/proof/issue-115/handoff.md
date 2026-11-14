# Issue #115 - Developer handoff

macOS: `POST /record/shot` always fails - `screencapture` is refused where ffmpeg succeeds.

Branch: `issue-115-macos-still-via-avfoundation` (off `main` at `af0ddb1`).
Machine: devthrottle-mac-mini, macOS 26.5, arm64, one 1920x1080 display, ffmpeg 9.0.2 (Homebrew).

## READ THIS FIRST - THIS ISSUE IS NOT FINISHED

The cause named in the issue is confirmed and fixed. A SECOND cause was found underneath it, it
was not in the issue, and it cannot be fixed inside this issue's stated scope. **Acceptance
criteria 1 to 5 are NOT met.** Criteria 6 to 9 are met. The detail is in "The second cause" below;
the short version is that during a recording the AVFoundation display is held exclusively by the
recorder's own ffmpeg, so no second process can capture it, and the only route to a marker shot
is a second output on the recorder's own ffmpeg - in `AvfVideoCapture`, the one file this work was
told not to touch because PR #127 rewrites it.

Do not merge this as a fix for #115. It is a correct and measured part of the fix, and the
remaining part is a design decision for the Implementation Lead.

## The first cause - confirmed, and fixed

`ScreencaptureStill` justified shelling out to `/usr/sbin/screencapture` on the grounds that it
"reads the SAME Screen Recording permission" as the recording. That clause is false: TCC attributes
a grant to the responsible process, and `screencapture` spawned by the launchd agent is not covered
by the agent's grant.

Replaced by `FfmpegArgs.AvfStill` - one frame through the same AVFoundation grabber the recording
uses, the macOS twin of `X11Still`:

```
ffmpeg -y -f avfoundation -capture_cursor 1 -pixel_format nv12 -framerate 30 -i 0:none \
       [-vf crop=W:H:X:Y[,pad=...]] -frames:v 1 -update 1 <out>.png
```

Measured working on this machine, through the installed launchd service that holds the grant
(three consecutive captures of the whole display, service at `ProcessType Interactive`):

| run | ffmpeg time | PNG bytes | dimensions | YAVG | distinct colours (480x270 downsample) |
|-----|------------|-----------|------------|------|----------------------------------------|
| 1 | 766 ms | 2,360,534 | 1920x1080 | 110.8 | 56,958 |
| 2 | 806 ms | 2,373,203 | 1920x1080 | - | - |
| 3 | 757 ms | 2,369,872 | 1920x1080 | - | - |

The image was READ, not just sized: it is the desktop wallpaper with no windows open. A flat frame
would show a near-zero colour count; 56,958 distinct colours in a 480x270 downsample is a real
picture of the screen.

**So the warm-up cost the old comment described is real and is now a number: about 0.8 s of ffmpeg
time, about 1.1 s including the display resolution ahead of it.** The old comment's "a second or
more" was roughly right. It is affordable, and it is what a working still costs.

## The second cause - found by measurement, NOT in the issue, and the blocker

A still through AVFoundation works when nothing else holds the display, and HANGS when the
recording holds it. Same binary, same launchd responsible process, same command, same grant - the
only variable is whether a recording is running:

| display state | result |
|---------------|--------|
| idle | exit 0, 2.37 MB PNG, a real 1920x1080 frame, ~2.1 s process wall |
| held by the recording's ffmpeg | no frame in 10 s, killed, exit 1 |

The recording running at that moment captured 1250 frames of 1920x1080 at 29.93 fps, so the grant
was live and working in that same process at that same instant. This is not a permission problem.

It is also not a device-CONFIGURATION problem: a still asking for no `-pixel_format` and no
`-framerate` at all was measured and hangs identically. **ffmpeg's AVFoundation screen input cannot
open a display that another ffmpeg process already has open.**

This codebase has already met and documented this exact constraint once, for the DirectShow camera,
in `Preview/PreviewTap.cs`:

> While a recording runs, ffmpeg holds the DirectShow camera EXCLUSIVELY. A preview that opened the
> device a second time would either fail outright or fight the recorder for it, so preview frames
> must come from the RECORDING PIPELINE - a second, low-resolution output on the recorder's own
> ffmpeg. One device open, one process, two outputs.

That is the answer here too, and the lead's hypothesis of "a warm capture session held open for the
duration of a recording" is right in spirit but cannot be a SEPARATE session - it has to be the
recording's own.

### The remaining work, measured as feasible before being proposed

A throwaway experiment (applied, measured, reverted - it is not in this branch) added a second
full-resolution output to the recorder's own ffmpeg:

```
-map 0:v -filter:v fps=1 -q:v 3 -update 1 <dir>/latest.jpg
```

| | frames | fps | still produced |
|---|--------|-----|----------------|
| baseline, no second output | 1250 / 41.8 s | 29.93 | - |
| with the full-res second output | 515 / 17.4 s | 29.54 | 1920x1080, 254 KB, YAVG 112.5 |

No dropped frames were logged, and the recording finalized normally. **A second full-resolution
still output on the recorder's own ffmpeg costs the recording essentially nothing.** A marker shot
then becomes a copy of the newest such frame, which is exactly "one frame off an already-running
session".

Two things that experiment does NOT settle, and they are the reason this is a decision and not a
task I should have just done:

1. It wrote to a FILE. `PreviewTap`'s class comment records that handing ffmpeg a file for a second
   output was measured on 2026-08-28 and REJECTED: when the path failed mid-run, ffmpeg's image2
   muxer terminated the whole process and truncated a 15 s recording to 5.1 s. The shipped version
   of this should almost certainly be a second MJPEG pipe drained by a tap, as the preview is - and
   there is only one stdout, so it needs either a second pipe or a merged tap.
2. It changes `AvfVideoCapture` and the recorder start path. **That is the file PR #127 rewrites**,
   and this work was explicitly scoped away from it.

The existing preview tap cannot be reused as-is: `PreviewFilter` scales to `PreviewHeight` = 270,
so preview frames are 480x270 and would fail acceptance criterion 2's pixel dimensions. Criteria
are not redefined to make them pass (DEVELOPMENT_METHOD 6c item 4).

## Acceptance criteria - Expected vs Actual

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | A shot during a recording produces a file | **NOT MET** | `/record/shot` returns the timeout error; `shots/` stays empty. Blocked by the second cause above. |
| 2 | The image shows the screen | **NOT MET for a marker shot**; met for every other still | idle capture: 1920x1080, YAVG 110.8, 56,958 distinct colours, image read and it is the desktop |
| 3 | The shot is counted | **NOT MET** | `/record/stop` still reports `"Shots": 0` - no shot is taken, so none is counted. The counting code was never the defect. |
| 4 | Three shots in one recording all land | **NOT MET** | same blocker |
| 5 | Latency stated as a number | **MET, for the path that works** | ffmpeg 766 / 806 / 757 ms; ~1.1 s in-service including display resolution; 2.1 s for a whole `agenteyes shot` process. A failing marker shot costs the 10 s budget. |
| 6 | No misleading permission message | **MET** | see below |
| 7 | Windows and Linux still capture stills | **MET** | `FfmpegArgs.cs` and `FfmpegArgs.Linux.cs` are untouched; `LinuxFfmpegArgsTests` (268 Linux tests) green |
| 8 | Build + test, mac and linux | **MET** | 4/4 below |
| 9 | A regression test in tests/AgentEyes.Tests.Mac | **MET** | `MacStillCaptureTests` |

### Criterion 6 - which condition now produces the permission message, and why it is accurate

Exactly one error path names the Screen Recording permission: `Screenshot.Hung`, reached only when
the capture produced no frame inside the 10 s budget.

That is accurate because it was measured. An AVFoundation screen capture started by a process
WITHOUT the grant does not fail - it hangs indefinitely, printing nothing but
`Configuration of video device failed, falling back to default` (measured from an unentitled shell:
15 s, no frame, no file, killed). A capture by the process that HAS the grant finishes in about
0.8 s. So a hang is what a missing grant looks like from here.

**The honest limit, stated rather than left to be found:** a timeout is also reachable without a
missing grant - a machine that simply does not deliver a frame in time, and, today, a marker shot
during a recording (the second cause). The message therefore names the permission as the cause to
check and explicitly leaves the other case open, instead of asserting a diagnosis. Every other
failure path - `Screenshot.Failure`, for a non-zero exit or an exit-zero-no-file - reports what
ffmpeg actually said and says nothing about permissions at all;
`Failure_NeverBlamesTheScreenRecordingPermission` asserts that over four outcomes.

Note that an EMPTY AVFoundation device listing is NOT a reliable signature of a missing grant, as
`AvfDevices.Read`'s comment supposes: an unentitled shell lists `[0] Capture screen 0` perfectly
well on this machine. That comment is inherited and out of this issue's scope, but it is wrong and
should get its own issue.

## What changed

| File | Change |
|------|--------|
| `Platform/Mac/FfmpegArgs.Mac.cs` | `ScreencaptureStill` -> `AvfStill`: one AVFoundation frame, region as a crop filter, same clamp-and-pad as the recording. `AvfVideoCapture`, `AvfCameraCapture` untouched (comment references only). |
| `Platform/Mac/Screenshot.cs` | captures through ffmpeg under a bounded run; new failure wording; `Hung` is the one permission path; keeps `ClearStaleTarget` and the file-presence proof |
| `Platform/Mac/MacDisplay.cs` | adds `RequireMainScreen`; `RequireScreen` and it now share `PairedScreens` (the count-mismatch guard) |
| `Platform/Linux/ToolRunner.cs` | adds `TryRun`, which reports a timeout instead of throwing. `Run` is now a wrapper and its behaviour and message are unchanged. |
| `tests/AgentEyes.Tests.Mac/MacStillCaptureTests.cs` | new - the argument list, the wording, and the grabber guard |
| `tests/AgentEyes.Tests.Mac/MacFfmpegArgsTests.cs` | the two `ScreencaptureStill` arg tests removed (the method is gone) |
| `tests/AgentEyes.Tests.Mac/MacScreenshotFailureTests.cs` | deleted - every assertion ported into `MacStillCaptureTests`, with the permission assertions inverted |

`MacForeignFfmpegOptionTests.cs` is deliberately UNTOUCHED to keep the rebase over PR #127 trivial;
`AvfStill` is swept by it automatically (it reflects over every `FfmpegArgs` builder returning a
string list), and `AvfStill_IsOnTheSweptBuilderSurface` asserts it stays on that surface.

## The gate - run after every change, all four green

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac    0 Error(s)
dotnet test  tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release                      Failed: 0, Passed: 241
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux  0 Error(s)
dotnet test  tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release                  Failed: 0, Passed: 267, Skipped: 1
```

Mac was 226 on `main` before this change and is 241 after (+21 new, -6 removed with the two deleted
files). Linux is unchanged at 268.

`AgentEyes.sln` cannot build on a Mac (it contains the WPF app), so the `CLAUDE.md` gate is a
Windows gate and was not run here.

## The fix disabled - which tests fail, BY NAME

A test that cannot fail is worse than no test. Three mutations were applied to the working tree,
the suite run, and the tree restored:

**M1 - `AvfStill` emits the screencapture argument list that shipped.** 6 failures:
- `MacStillCaptureTests.AvfStill_FullDisplay_IsOneAvfoundationFrame`
- `MacStillCaptureTests.AvfStill_UsesTheAvfoundationDemuxerAndNotAnotherTool`
- `MacStillCaptureTests.AvfStill_Region_IsCroppedInsideTheDisplay`
- `MacStillCaptureTests.AvfStill_RegionOffTheEdge_IsPaddedBackToTheAskedSize`
- `MacStillCaptureTests.AvfStill_OddRegion_IsEvenized`
- `MacStillCaptureTests.AvfStill_RegionOutsideTheDisplay_IsRefusedAndSaysWhy`

**M2 - `Screenshot.cs` shells out to `/usr/sbin/screencapture` again.** 1 failure:
- `MacStillCaptureTests.TheMacStillPathRunsFfmpegAndNoOtherCaptureTool`

**M3 - `Failure` re-adds the permission sentence.** 1 failure:
- `MacStillCaptureTests.Failure_NeverBlamesTheScreenRecordingPermission(exit: 0, stderr: "", written: False)`

Restored tree: `Failed: 0, Passed: 241`.

## What these tests do NOT cover

- **That a marker shot works.** It does not, and no test here claims it does. The unit tests are
  pure; they assert the argument list, the wording and the grabber, and nothing about a running
  capture.
- **That ffmpeg accepts the argument list, or that a frame is a real picture.** Only a live capture
  proves that. It was proven by hand (table above) and is not automated anywhere.
- **The device-exclusivity constraint itself.** Nothing in the suite would notice if a future ffmpeg
  allowed a second session, or if this one stopped allowing the first.
- **Latency.** It is a property of the machine, not of an argument list. `Budget_Is...` asserts only
  that the bound stays in a sane range, not that a capture meets it.
- **Multi-display.** This machine has one display. `CaptureMonitor` resolves its own display and is
  correct on any monitor; `CaptureRect` is handed a display-local rectangle with no display number,
  so it reads it against the MAIN display - on macOS every monitor's bounds start at (0,0), so a
  rectangle alone cannot name a display. A region marker shot on a non-main display would therefore
  capture the main one. That is a pre-existing limitation of the rectangle API, not a regression,
  and nothing tests it because nothing here can.
- **The source scan** in `TheMacStillPathRunsFfmpegAndNoOtherCaptureTool` is defeated by an alias, a
  computed string, or a still built outside `src/AgentEyes.Core/Platform/Mac`. The argument-list
  tests are defeated by a second still builder that nothing calls. Together they cover each other's
  blind spot; neither covers a call assembled at run time.

## How QA should exercise what IS here

Nothing here needs a Windows smoke. On a Mac:

1. `bash scripts/mac/install.sh`, then put `ProcessType Interactive` back in
   `~/Library/LaunchAgents/com.agenteyes.recorder.plist` if testing against `main` (that line is on
   the #114 branch, not on `main`, and without it the service runs at throttled background QoS).
2. **The still that works.** The Control API has no idle-still endpoint, and a capture started from
   a shell is refused by TCC, so drive `agenteyes shot --screen 1 --out <dir>` as a one-shot launchd
   job - launchd is then the responsible process, as it is for the recorder. The job needs
   `EnvironmentVariables PATH=/opt/homebrew/bin:...` or ffmpeg is not found. Expect a 1920x1080 PNG
   of about 2.3 MB in under 3 s. Read the image.
3. **The blocker.** Start a recording, run the same job: expect no frame in 10 s and exit 1. That is
   the open half of this issue, not a defect in this change.
4. `POST /record/shot` while idle still answers `{"error":"not recording","code":"conflict"}` -
   that contract is unchanged and was re-checked.

Reminders: the focus-free layers are REST / UIA / PrintWindow; never force-foreground and
synthesize input; the recording HUD is capture-excluded, so HUD state is asserted via UIA or
`/status`, never a screen grab.

## CenCon impact

No drift. No component moves, and the privacy posture (visible, controllable) is untouched - a
still is still taken only when asked for, and the recording indicator is unaffected.

## Housekeeping from this run, so nobody reads it as a defect

Three recordings were left with a truncated `recording.mp4` ("moov atom not found"):
`2026-11-14_120548_video`, `2026-11-14_120937_video`, `2026-11-14_121011_video`. The trigger is
mine: the rebuild harness booted the service out while each one's deferred audio mux was still
running. Each `raw.mp4` is intact where it was not already consumed. Nothing under
`~/Movies/AgentEyes` was deleted.

**SIX ORPHANED FFMPEG PROCESSES WERE LEFT RUNNING AT ~100% CPU, AND THEY ARE NOT MINE TO KILL
(#128 evidence).** They are listed here rather than cleaned up, as instructed:

| pid | started | recording being muxed |
|-----|---------|------------------------|
| 87698 | 12:06:06 | `2026-11-14_120548_video` |
| 88738 | 12:09:54 | `2026-11-14_120937_video` |
| 88767 | 12:09:57 | `2026-11-14_120548_video` |
| 88995 | 12:10:28 | `2026-11-14_121011_video` |
| 90146 | 12:12:28 | `2026-11-14_120548_video` |
| 90865 | 12:14:30 | `2026-11-14_120937_video` |

All six are the deferred mic-audio mux (`arnndn` denoise over `raw.mp4`). The pattern matters more
than the count, and it may be the mechanism behind #128:

* Booting the service out does NOT kill the mux ffmpeg it started. The child is orphaned and keeps
  burning a core indefinitely - 13 minutes for the oldest, with no one reading its output.
* On the next start the service re-queues the SAME recording and starts a NEW mux ffmpeg beside the
  orphan. `2026-11-14_120548_video` has three of them running concurrently over one input, each
  writing the same output path.
* So a restart during post-processing is not merely lossy, it STACKS: every cycle adds a
  full-core process and another writer to the same file, and nothing detects or reaps the previous
  one. Three of my rebuild cycles produced six live processes.

I did not kill them, as instructed. The Implementation Lead captured each one's pid, ppid, CPU,
elapsed and output path and then cleared all six, confirming the stacking above: `120548` had three
concurrent muxes writing one `recording.mp4` and `120937` had two, which is why those outputs are
truncated. **That makes #128 a data-corruption defect and not only a CPU leak**, and it is now two
further criteria there. The service spawned a replacement within three seconds of the kill, so the
retry half is captured live on that issue too. Machine load went from 9.62 back to 6.39.

## I do not believe this is finished

Criteria 1 to 5 are not met and cannot be met inside the stated scope. This is with the
Implementation Lead.
