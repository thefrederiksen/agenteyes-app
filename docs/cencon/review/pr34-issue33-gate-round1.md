REJECT

# PR #34 / issue #33 - Review Gate round 1

Reviewed PR head `b61f71a85e62e1a4424da24cac3c4a6a34b35cb8` against the issue-33 merge
base `e485561353db56ceb1a07a360bd0957d7aae75df`. The remote branch and
`refs/pull/34/head` both resolved to that head. I read issue #33 in full (AC1-AC12,
C1-C6), the PR diff, all four developer handoffs, all four QA reports, and the
round-4 window-message evidence. Evidence committed by the other seats was treated
as context, not as the verdict.

An isolated worktree at the exact head was restored before building. Independent
gate result:

```
dotnet restore AgentEyes.sln
dotnet build AgentEyes.sln -c Release --no-restore --no-incremental
  Build succeeded. 2 Warning(s), 0 Error(s)
dotnet test AgentEyes.sln -c Release --no-build --no-restore
  Passed: 1068, Failed: 0, Skipped: 0, Total: 1068
```

The two warnings are the pre-existing xUnit1031 warnings in
`PostRecordingQueueTests.cs:309,314`. Test discovery used the x64 output and found
the expected test assembly and count.

## Blocking defects

### 1. AC10 is not structurally true: publishing can block the only stdout drain

`PreviewTap.Drain` invokes `Publish(frame)` synchronously before returning to its
next read (`src/AgentEyes.Core/Preview/PreviewTap.cs:193-208`). `Publish` performs
synchronous filesystem operations on that same thread
(`src/AgentEyes.Core/Preview/PreviewTap.cs:233-254`, especially lines 237-238).
Those threads are the only readers of the screen and camera ffmpeg stdout pipes
(`src/AgentEyes.Core/Video/FfmpegRecorder.cs:106` and
`src/AgentEyes.Core/Video/ICameraProcess.cs:153`).

Concrete failure scenario: while an armed preview is visible, replace
`%LOCALAPPDATA%\AgentEyes\preview` with a directory reparse point to an unavailable
share, or let an NTFS/filter-driver/disk stall block `File.WriteAllBytes` or
`File.Move`. The pump is then blocked inside publishing and performs no more pipe
reads. The anonymous pipe fills and blocks the same ffmpeg process that writes
`recording.mp4` or `camera.mp4`, degrading or truncating the recording. The catch
does not help: it runs only after the blocking filesystem operation returns or
throws.

The killed-directory evidence exercises a fast failure (`DirectoryNotFoundException`),
so it proves that quick exceptions are caught; it does not prove that downstream
publishing cannot stop the drain. The current implementation therefore has not
moved all fallible downstream work behind the unconditional drain as AC10 requires.
The drain must never wait for publishing; a bounded latest-frame handoff to a
separate publisher is one viable shape.

### 2. AC7's allowlist drops a genuine user resize: maximize / Windows snap

The Win32 route records only a modal loop containing `WM_SIZING`
(`src/AgentEyes.App/HudUserResize.cs:116-138`). The only other accepted routes are
the panel grip (`src/AgentEyes.App/HudUserResize.cs:150-156`) and the custom UIA
TransformPattern (`src/AgentEyes.App/HudUserResize.cs:163-170`). The HUD is a
normal resizable window (`src/AgentEyes.App/HudWindow.cs:99-105`), so maximize and
Windows snap are genuine ways a person can resize it.

I ran an isolated WPF window-message probe with the HUD's `WindowStyle.None` and
`ResizeMode.CanResize`. Posting the user maximize command produced:

```
WM_SYSCOMMAND wp=0xF030
WM_WINDOWPOSCHANGED
WM_SIZE
WindowState=Maximized Actual=1934x1094
```

It produced no `WM_ENTERSIZEMOVE`, `WM_SIZING`, or `WM_EXITSIZEMOVE`. Those are the
only window messages `HudUserResize` recognizes, so the actual maximized/snapped
size is never recorded. On stop, `SavePosition` persists the old memory
(`src/AgentEyes.App/HudWindow.cs:697-714`), and the next recording returns to the
old/default size rather than the size where the person left the HUD.

The structural checks are live for the mutations they enumerate, but they do not
close this gap. In particular,
`Record_IsOnlyEverReachedFromAPositivelyIdentifiedGesture` hand-lists exactly three
callers and rejects only missing or extra callers from that list
(`tests/AgentEyes.Tests/HudUserResizeTests.cs:90-115`). It cannot detect a genuine
fourth resize route that was never listed. This is the same completeness problem
as an allowlist that proves its members but cannot prove its own exhaustiveness.

### 3. The preview toggle still performs synchronous file I/O on the WPF UI thread

The HUD click handlers call `ApplyPreviewState(fromUser: true)` synchronously
(`src/AgentEyes.App/HudWindow.cs:456-479`). That reaches `SavePreviewChoices`
(`src/AgentEyes.App/HudWindow.cs:535-538`, `662-670`), which calls `Config.Save`;
`Config.Save` performs synchronous `File.WriteAllText`
(`src/AgentEyes.App/Config.cs:74-82`). The constructor also calls
`ApplyPreviewState(fromUser: true)` (`src/AgentEyes.App/HudWindow.cs:339`), so it
rewrites config while constructing every HUD even though the adjacent comment says
the construction path is not a user choice.

Concrete failure scenario: config I/O is delayed by disk pressure, antivirus, or a
filter driver just as the person clicks Show/Hide preview. The WPF dispatcher is
blocked inside `File.WriteAllText`; the preview action and, critically, the STOP
button cannot respond until that I/O returns. This violates issue #33's explicit
responsive-UI scope and the mandatory no-sync-I/O rule in `CLAUDE.md:130-134`.

## Other rulings

- AC3/C1's device ownership design is correct: camera preview is a second output on
  the recording camera ffmpeg, with one DirectShow input; no independent camera open
  exists in this diff.
- AC6/C5 remains correctly based on `WDA_EXCLUDEFROMCAPTURE`; the branch does not
  relax it, and the excluded/known-bad evidence is discriminating.
- The opt-in-per-recording consequence is honest and acceptable. An unarmed ffmpeg
  cannot gain an output without restarting the recorder, and the panel explicitly
  says that live frames begin with the next recording. A first-run hint may improve
  the experience, but this trade is not a blocking defect.
- AC9's evidence is noisy but not a gate blocker here: the round-4 60-second pairs
  reported equal drop counts in each control/preview pair and stayed inside #28's
  duration bound.

No product code, branch, commit, GitHub issue, or pull request was changed by this
review. The installed tray app was not displaced.
