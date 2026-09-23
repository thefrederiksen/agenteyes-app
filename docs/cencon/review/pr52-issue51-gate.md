REJECT

# PR #52 / issue #51 - Review Gate

## Basis of review

Reviewed PR #52 at branch head `b52b7eef2f70fcad3a8bc51a9f9a419883e1d029`, its
squash commit on main `324d18076375755ba00747885cb77919e87d7153`, and release
`v1.8.1` at `ddbaaf0882e11197cf7610b24a6f1ce89f083630`. The two changed
files on the PR head and release head are byte-identical. I read issue #51, the
full patch, the surrounding tray preset, HUD, recording-session, manifest, and
post-processing paths, the prior PR #49 gate reports, the CenCon method, and the
available local runtime record.

The production state transition on release head is internally consistent:

- `CapturePreset` passes the camera preset overlay into `StartVideo`
  (`src/AgentEyes.App/CapturePreset.cs:241`).
- `StartVideo` seeds `_previewOverlay` after the session reset
  (`src/AgentEyes.Core/RecordingService.cs:611`).
- The HUD construction apply sends its null through `SetPreviewOverlay`
  (`src/AgentEyes.App/HudWindow.cs:593`), and the new null branch retains the
  seeded value (`src/AgentEyes.Core/RecordingService.cs:309-320`).
- `Stop` snapshots that value under the session lock and writes all four framing
  fields to the manifest (`src/AgentEyes.Core/RecordingService.cs:758-771` and
  `819-889`).

That supports AC1 and AC2 by code review. The blockers below prevent certifying
the hotfix as a completed CenCon run.

## Blocking defects

### 1. The regression test does not cover the exact start-then-HUD sequence required by the issue

The test named `A_null_from_the_preview_does_not_erase_the_presets_framing`
does not start a recording from a preset. It seeds the service by calling the
HUD refinement method itself (`tests/AgentEyes.Tests/HudFramingEraseTests.cs:35-40`):

```csharp
svc.SetPreviewOverlay(Framing());
svc.SetPreviewOverlay(null);
```

A derived inventory of every `StartVideo(` occurrence in the test source found
three: two are source-extraction anchors, and the only executable call is the
existing invalid-microphone test at `tests/AgentEyes.Tests/RecordingServiceTests.cs:26`.
That call supplies no camera or overlay and exits before a recording starts.
No test executes `StartVideo` with a preset overlay, then applies the HUD null.

Concrete failure scenario: remove or clear the preset assignment at
`src/AgentEyes.Core/RecordingService.cs:611`, or stop passing `p.Overlay` at
`src/AgentEyes.App/CapturePreset.cs:241`. All four new tests still pass because
none reaches either seam. A tray recording then starts with no in-memory
framing; the ignored HUD null retains no framing; stop writes none; compose is
again skipped. This is the original user-visible failure, and it violates the
issue's explicit in-scope requirement for a regression test covering the exact
sequence.

The tests are alive for the narrower setter behavior: against the compiled
v1.8.0 core they discovered exactly four tests, passed two, and failed two.
Both failures reported expected `bottom-right`, actual null. Against v1.8.1 the
same four named tests passed. That negative control proves the setter check ran;
it does not extend the check's reach into the preset start path.

### 2. AC3 and AC4 have no post-fix tray-path or frame proof

AC3 requires a tray start with a camera preset and the preview panel hidden,
followed by manifest framing, `PostProcessing.compose = done`, and
`ComposedCamera: true`. AC4 requires inspection of an extracted frame from that
recording. The shipped tree contains 322 proof files and 19 gate files, but the
derived issue/PR inventory contains zero issue-51 proof files and zero PR-52
gate files before this verdict. PR #52 has no review or comment containing that
proof.

The local recording `2026-08-30_223040_video` is positive evidence for the
reported defect, not for the fix. Its log records the tray preset seed at
22:30:40.607, the old HUD null assignment at 22:30:42.093, and `no camera to
compose` at 22:31:09.704. The compose now visible in its manifest was a separate
manual compose at 22:42:14-18 after framing was restored; it was not the
automatic post-fix tray path. There are no later recordings on the machine.

Concrete failure scenario: the setter behaves correctly in isolation while the
tray preset-to-start argument or the HUD construction order is wrong. The four
unit tests and the full suite stay green, but a normal tray recording again
leaves `camera.mp4` beside an uncomposed `recording.mp4`. The requested runtime
proof exists specifically to close that GUI seam, and it was not produced.

### 3. The PR merged without the QA and Review Gate states required by CenCon

The issue is closed while its present flow label is `flow:ready-dev`. There is
no developer handoff, QA report, `flow:ready-gate` transition, or pre-merge gate
verdict for issue #51. PR #52 was merged immediately after its single developer
commit. The method requires QA to commit its proof report and set
`flow:ready-gate` (`docs/cencon/DEVELOPMENT_METHOD.md:78-90`, `315-316`), then
requires this independent gate before merge (`docs/cencon/DEVELOPMENT_METHOD.md:92-106`,
`282`).

Concrete failure scenario: the PR's own statement that build and tests passed
is accepted as both implementation and verification, while the two acceptance
criteria that require a different surface - the actual tray/HUD route and an
extracted video frame - are never exercised. That is exactly the separation of
duties failure the Review Gate exists to prevent.

## Independent check record

- Release build: exit 0, `Build succeeded`, 4 warnings, 0 errors.
- Issue-specific tests on v1.8.1: exit 0, 4 passed, 0 failed, total 4.
- Known-bad compiled v1.8.0 control: exit 1, 2 passed, 2 failed, total 4; both
  failures named the null-erasure assertion and reported the actual null.
- Full solution test: exit 0, 1,392 passed, 0 failed, 0 skipped, total 1,392.
  This exactly reconciles to the inventory claimed in the PR.
- No `testhost` or other AgentEyes test run was active at gate start. Reusable
  MSBuild/compiler nodes and one unrelated repository build were present.
- I did not start a new physical-camera recording. The installed v1.8.1 app is
  present, but the Review Gate does not substitute itself for the missing QA
  seat, and no existing post-fix tray recording was available to inspect.

---

## Provenance of this file, and one correction from the orchestrator

The gate session wrote this verdict into its own worktree. That worktree was then
removed with `--force` before the file had been copied into the branch, which
deleted it - the SECOND time in this repository that a verdict was lost that way.
The text above is the verdict restored verbatim from the orchestrator's read of
it; nothing has been edited, softened or omitted.

**Correction to defect 2, on the facts.** The verdict states that the post-fix
tray path was never exercised and that "there are no later recordings on the
machine". The tray-path verification DID happen; its recording directory was
deleted during cleanup before the gate ran, which is why the gate could not find
it. The product log still holds the sequence, and it is reproduced in
`docs/cencon/proof/issue-51/handoff.md`.

That correction does not rescue the finding, and the finding is accepted: proof
that is destroyed before anyone else can check it is not proof. Defect 2 is
answered by the committed evidence in that handoff, produced from a recording
that was kept.

Defects 1 and 3 stand as written. Defect 1 is fixed under issue #53; defect 3 is
tracked as issue #50.
