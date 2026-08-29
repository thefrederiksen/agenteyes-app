# Issue #35 replayed onto main (off issue #33)

Date: 2026-08-29
Branch: `issue-35-preset-editor-tabs` (new), cut from `origin/main` at `e57d828`
Supersedes as the merge vehicle: `issue-35-preset-editor-two-columns` @ `cb6b019` (left untouched)

## Why

Issue #35 passed QA round 1 with 10/10 and sits at `flow:ready-gate`. Its branch was STACKED on
issue #33 - its own commits were cut from `178cf2a` ("Let the recording HUD show what is being
recorded (#33)"), the first commit of a branch the Review Gate has now rejected twice. #35 was
therefore blocked behind however many more #33 rounds are needed, for no reason of its own.

The human ruled: land #35 now, independently of #33. This note records the replay and the
re-verification that it is still the feature QA passed.

Nothing about the feature was changed. No test was weakened or deleted.

## What was replayed

The old branch was not linear. Its own work was:

```
178cf2a  (#33's HUD commit - the base being removed)
  |
  +-- 0b5c2c4  wip: two-column layout (superseded by the tabbed scope change)
  |
  +-- ffd3970  MERGE: "Give the preset editor tabs, room, and a live camera preview (#35)"
  |              first parent  0b5c2c4
  |              second parent d2c7391, the issue #29 branch (cut from eee17b4, NOT from #33)
  |              and it is an EVIL MERGE - the whole tabbed rewrite of PresetEditor.xaml /
  |              PresetEditor.xaml.cs / PresetEditorLayoutTests.cs lives IN the merge commit,
  |              in neither parent.
  |
  +-- cb6b019  QA round 1 on the preset editor: PASS 10/10 (#35)
```

That structure is the trap in this replay: a plain `git rebase --onto origin/main 178cf2a` drops
the merge commit, and with it the entire tabbed editor. It applies four commits, looks like it
worked, and silently produces the OLD single-column editor with #29's preview bolted on.

The replay therefore reconstructs the work explicitly, as four linear commits on `origin/main`:

| # | Commit | Origin |
|---|--------|--------|
| 1 | Live camera preview in the preset editor (#29) | cherry-pick of `5dc0401` (its own base `eee17b4` IS the merge base with main, so this is an exact 3-way) |
| 2 | Handoff note and mutation evidence for issue #29 | cherry-pick of `d2c7391` |
| 3 | Give the preset editor tabs, room, and a live camera preview (#35) | `ffd3970`'s TREE, replayed with `178cf2a` as the merge base, so #33's content is dropped and main's newer #28 work is kept |
| 4 | QA round 1 on the preset editor: PASS 10/10 (#35) | cherry-pick of `cb6b019` |

Issue #29 comes with it, by necessity and by design: PR #31 was closed as superseded precisely
because #29's work lives inside this branch. The tabbed Camera tab IS #29's preview, enlarged to
480x360.

## Conflicts and how they were resolved

Four files conflicted, all for the same reason - "ours" carries #29 (from commits 1-2) and
"theirs" carries #29 plus the tab edits:

| File | Resolution |
|------|-----------|
| `src/AgentEyes.App/PresetEditor.xaml` | took theirs; #33 never touched this file, so theirs is the QA-passed content |
| `src/AgentEyes.App/PresetEditor.xaml.cs` | took theirs, same reason |
| `tests/AgentEyes.Tests/CameraPreviewTests.cs` | took theirs, same reason |
| `src/AgentEyes.App/Config.cs` | NOT theirs. #33 also edits this file, and theirs carries #33's `HudWidth`, `HudHeight`, `HudPreviewVisible`, `HudPreviewMode` and `HudPreviewCorner`. Resolved by hand to main's content plus ONLY #35's five `PresetEditor*` properties. |

Two more files auto-merged WRONG and were redone. Git produced a duplicated block in each,
because "ours" already carried #29's addition and the base did not:

- `src/AgentEyes.Core/Video/FfmpegArgs.cs` - auto-merge gave +112 lines against main where the
  original branch's own delta is +56, i.e. every #29 method twice.
- `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs` - auto-merge gave +14 where the original is
  +7: `CameraDeviceArbiter.ReleaseForRecording(dshowCameraName)` emitted TWICE in `Create`.

Both were redone as a proper three-way `git merge-file` (base `178cf2a`, ours `origin/main`,
theirs `ffd3970`) and now carry +56 and +7, character-for-character the same delta the QA-passed
branch carried.

## Structural verification of the replay

Measured, not asserted:

- Of the 43 files the old branch's own chain touched, 40 are BYTE-IDENTICAL to `cb6b019`.
  `PresetEditor.xaml`, `PresetEditor.xaml.cs`, `CameraPreviewController.cs`,
  `CameraDeviceArbiter.cs`, `FfmpegCameraPreview.cs`, `CameraPreviewSession.cs`,
  `CameraPreviewTests.cs` and `PresetEditorLayoutTests.cs` are all in that set - the entire
  feature surface is the same bytes QA passed.
- The 3 that are not identical are exactly the 3 that #33 also edits (`Config.cs`,
  `FfmpegArgs.cs`, `FfmpegCameraRecorder.cs`). For each, the diff against `origin/main` was
  compared line-for-line with the same file's diff across `178cf2a..cb6b019`: IDENTICAL in all
  three. They differ from `cb6b019` only by NOT carrying #33.
- #33 leakage: NONE. Every file `178cf2a` introduced or changed that #35 does not touch is still
  exactly `origin/main`'s version - no `HudPreviewState.cs`, no `PreviewFrameFeed.cs`, no
  `Preview/*`, no `docs/cencon/proof/issue-33/*`.
- Main disturbed: NONE. Every file main's own #28 rounds 6-9 changed
  (`Commands.cs`, `StrandedCameraOwner.cs`, `CameraTerminationRecord.cs`,
  `CameraFailurePathTests.cs`, `StrandedCameraOwnerTests.cs`) is byte-identical to
  `origin/main`. #28's merged design - the explicit termination history, the one monotone
  stop-kind derivation and the three-state `CameraComplete` - is untouched.
- No file on main was removed, and no conflict marker survives anywhere in the tree.

## The gate

Built from an isolated worktree (`D:\ReposFred\agenteyes-dev35-reorder`), restored first. The
human's installed v1.6.2 tray app was never displaced and port 7882 was never taken.

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  4 Warning(s)  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Failed: 0, Passed: 983, Skipped: 0, Total: 983
```

The four warnings are main's own (two xUnit1031 in `PostRecordingQueueTests`, two xUnit2031 in
`StrandedCameraOwnerTests`), present on `origin/main` before this branch existed.

983 is SMALLER than the 1043 QA counted on the old branch, and that is expected - the old branch
carried #33's tests and an older #28. The number is accounted for exactly rather than waved at:

```
origin/main (e57d828), built and run the same way   ->  Passed: 951
this branch                                         ->  Passed: 983
983 - 951 = 32
CameraPreviewTests + PresetEditorLayoutTests only   ->  Passed:  32
```

So this branch adds exactly its own 32 tests to main and removes nothing from main.

## Acceptance criteria, re-verified on the new base

The instrument is QA's OWN probe, `qa-probe.cs.txt` / `qa-probe.csproj.txt`, committed to this
folder in round 1 and re-run unmodified against this branch (only the two checkout paths it
documents were repointed). Full output: `reorder-probe-output.txt`, verbatim except that the
run's temp directory is written as `<scratch>`. It exits non-zero on any missing measurement,
and it exited 0.

| AC | Result on the reordered branch | Round-1 value |
|----|-------------------------------|---------------|
| AC1/AC3 no scrollbar at 1000x760 | Capture `Collapsed` 285.7/527.9, Audio `Collapsed` 311.8/527.9, Camera `Collapsed` 515.6/527.9; 3 tabs Capture/Audio/Camera | same three numbers |
| AC2 named controls present and fully visible in their tab | 10/10 | 10/10 |
| AC4 x:Names | 38 before, 0 removed, 0 renamed, 0 retyped, 48 after - full list in `reorder-xname-check.txt` | 38 -> 48 |
| AC5 preset round-trip | LoadFrom -> ReadInto byte-identical, and identical through the real `presets.json` | same |
| AC6 safety net at 600 tall | Camera tab `Visible`, 515.6/367.9, 147.8 scrollable; every checked control reachable | same |
| AC8 live pane | `CameraPreviewPanel` measured 480x360 (floor 320x240); image 320x240; 172,291 of 230,400 bytes changed over 2 seconds | 171,435 of 230,400 |
| AC9 release before recording | all four paths released, and a REAL camera recording opened in 1111 ms mid-preview, 633 ms after leaving the Camera tab, 626 ms after closing the editor - budget 2000 ms | 1135 / 608 / 638 ms |
| AC10 reopen on last tab/size/position | tab=2 size=1120x700 pos=140,90 written and restored | same |

AC9 got the hardest look, because `FfmpegCameraRecorder.cs` is one of the three files that is NOT
byte-identical to the QA-passed tree - it now sits on top of main's much larger #28 rounds 6-9.
Three independent facts, not one:

1. Source: `CameraDeviceArbiter.ReleaseForRecording(dshowCameraName)` is the FIRST statement of
   `FfmpegCameraRecorder.Create`, before the `ProcessStartInfo` is even built, and `Open()` -
   which is what launches ffmpeg, via `StartAndProbe` - runs strictly after `Create` returns.
2. Compiled IL: `CameraPreviewTests.OpeningACameraForRecording_AsksEveryHolderToReleaseIt` reads
   the call site out of the assembly built from THIS branch and finds it in
   `FfmpegCameraRecorder::Create`; its negative control confirms the scan discriminates between
   methods rather than reporting every one it walks.
3. Call sites: the only two production callers of `Create` on this base are `Commands.cs:333`
   (the CLI) and `RecordingService.cs:405` (the app and `POST /record/start`). `CreateOver` is
   `internal` and test-only. So no recording path reaches a camera without passing the release.

WHAT THIS DOES NOT COVER: the probe drives `FfmpegCameraRecorder.Create` + `Open` - the exact
pair `RecordingService` calls to serve `POST /record/start` - but it does not send the literal
HTTP request, because doing so would have meant stopping the human's running always-on recorder
to take port 7882. Round 1 did send it (200 in 1827/1548/1317/1404 ms) against the identical
`PresetEditor`, `CameraPreviewController`, `CameraDeviceArbiter` and `FfmpegCameraPreview` bytes;
what changed under them since is main's #28 work, which passed its own gate on main.

## Environment

- The human's installed v1.6.2 tray app (pid 52132) ran throughout and was never stopped.
- `config.json` and `presets.json` restored byte-for-byte; verified by SHA-256 against a snapshot
  taken before the probe ran (`d60f3c2...` and `bffc425...`, unchanged).
- No ffmpeg process left behind (`tasklist` shows none).
- The probe's test recordings went to a scratch folder, never to
  `%USERPROFILE%\Videos\AgentEyes`, and are deleted. The newest entry in the recording library is
  still the human's own `2026-08-28_234202_video`.

## Branches deliberately NOT touched

- `issue-33-hud-live-preview` - a Review Gate session is on it.
- `issue-36-circular-camera-overlay` - still stacked on the ORIGINAL
  `issue-35-preset-editor-two-columns`, which is why that branch was left in place rather than
  force-pushed over.

The label stays `flow:ready-gate`. This issue already passed QA 10/10; the replay is a change of
base, not of behaviour, and it goes straight to the Review Gate.
