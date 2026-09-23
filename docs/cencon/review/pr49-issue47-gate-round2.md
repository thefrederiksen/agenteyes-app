REJECT

# PR #49 / issue #47 - Review Gate round 2

## Basis of review

Reviewed PR #49 at head `8a91e60493ad63534f56c34d4e95586913b6943e` against
`origin/main` at `36c98f13017b666491f89e8d3ab2d09b04ef9141`. Local head, remote branch,
and PR head were identical. I read issue #47 and its AC7 decisions, the full branch diff, the
round-1 verdict, the developer handoff, and the surrounding recording, manifest,
post-processing, overlay, ffmpeg, and test code.

The five round-1 defects have substantive fixes on head. Independent compiled-head probes
confirmed the two runtime seams that the unit suite still does not reach:

- Re-composing after changing the corner from top-left to bottom-right left the old inset position
  as the original screen blue (`0,0,254`) and drew the camera only at the new position
  (`253,0,0`). The SHA-256 of `recording.screen.mp4` stayed identical.
- With a camera offset of `+1.0s`, a frame at `0.5s` retained the original screen blue at the inset
  position, while a frame at `1.5s` contained the camera red. An outside pixel stayed screen blue,
  the composed file retained an audio stream, and `camera.mp4` stayed SHA-256 identical.
- The issue-specific test class positively discovered and completed 38 tests: 38 passed, 0 failed,
  exit 0. These tests cover geometry, argument construction, and planning predicates. They still do
  not execute `CameraCompose.Run`, `Swap`, or a real ffmpeg composition.

I did not make a new physical-camera recording. The required independent QA report and its
running-app proof are absent, as described in blocking defect 4.

## Blocking defects

### 1. A successful CLI compose leaves the required compose-stage journal absent or stale

`Commands.Compose` calls `CameraCompose.Run` directly (`src/AgentEyes.Core/Commands.cs:562`). On
success, `CameraCompose.Run` records the files and sets `ComposedCamera`, but does not update the
compose-stage journal (`src/AgentEyes.Core/CameraCompose.cs:117-122`). The only code that records a
compose stage as `done` is the automatic `PostRecording` sequence after its delegate returns
(`src/AgentEyes.Core/PostRecording.cs:454-459`). The CLI bypasses that sequence.

This is not theoretical. On compiled head, both the derived three-second fixture and a copy of the
real 160-second camera recording returned CLI exit 0, produced `recording.mp4`, preserved
`recording.screen.mp4`, kept `camera.mp4` byte-identical, and set `ComposedCamera: true`. Reading
each resulting manifest back produced:

```text
ComposeExit=0
ComposedCamera=True
ComposeJournalPresent=False
```

Concrete failure scenario: an automatic compose records `PostProcessing.compose.State: failed`,
then the person fixes the input and successfully runs `agenteyes compose <dir>`. The video is now
composed, but the manifest still reports the compose as failed. On an older recording it reports no
compose stage at all. This contradicts AC2's required `State` / `Attempts` / `LastAttemptUtc` record
and the handoff's claim at `docs/cencon/proof/issue-47/handoff.md:56` that the compose stage is
`done`.

### 2. Re-composition can remove recording.mp4 permanently from automatic recovery

When `recording.screen.mp4` already exists, `Swap` deletes the current `recording.mp4` and only then
moves the newly composed temp file into its place
(`src/AgentEyes.Core/CameraCompose.cs:149-156`). Those are two separate filesystem operations, not
an atomic replacement. An already-composed manifest has `ComposedCamera: true`, and
`NeedsCompose` returns false from that flag before looking at any artifact
(`src/AgentEyes.Core/PostRecordingPlan.cs:73-75`).

Concrete failure scenario: re-compose an existing recording and terminate the process, lose power,
or hit an I/O failure after `File.Delete(final)` and before `File.Move(composed, final)`. The durable
screen-only and camera files remain, but canonical `recording.mp4` is gone. Every automatic repair
pass refuses to rebuild it because `ComposedCamera` is still true. The Library, thumbnail, package,
and sharing paths therefore see a recording with no playable final until somebody manually runs the
CLI again. This contradicts the implementation's stated guarantee that a failed compose leaves
`recording.mp4` as the screen-only video (`src/AgentEyes.Core/PostRecording.cs:371-373`).

### 3. The required full-suite gate is not green on this head, and the committed proof is stale

The release build completed with exit 0: `Build succeeded`, 4 warnings, 0 errors. The exact required
full-suite command was then run twice on this head, and both runs returned exit 1:

```text
run 1: total 1355, passed 1353, failed 2
run 2: total 1355, passed 1354, failed 1
```

The second run wrote a TRX result and positively enumerated its one failure:

```text
AgentEyes.Tests.PublishedPluginAssetTests.PublishedScripts_MatchRepoSource_AndCarryNoStaleCredentialPath
System.IO.IOException: plugin.json is being used by another process
```

The first run positively reported
`SetupEngineTests.SetBundleExtractBaseDir_CreatesTheDirectoryAndIsIdempotent` as one failure. Both
named tests passed when rerun alone, which narrows the failure to full-suite concurrency or shared
environment state; it does not turn either full run green. AC9 requires the full solution command,
not isolated retries of its failures.

The committed handoff still says 1,345 tests passed
(`docs/cencon/proof/issue-47/handoff.md:106-108`). Head contains ten additional round-2 tests and
discovers 1,355, so that proof covers the previous commit, not the PR head. This also violates the
head-timestamp rule for verification and AC9 as written.

### 4. The issue did not receive the independent QA handoff required before this gate

The issue comment explicitly says the same seat wrote the code and produced the verification.
`docs/cencon/proof/issue-47/` contains only `handoff.md`; there is no QA report. The method requires
an independent QA session and a committed QA report before `flow:ready-gate`
(`docs/cencon/DEVELOPMENT_METHOD.md:78-90`, `docs/cencon/DEVELOPMENT_METHOD.md:291-318`). The Review
Gate is a separate later control and does not retroactively create that separation of duties.

This gap is especially material after round 1. The branch added ten regression tests, but the
derived test call-site inventory still contains no call to `CameraCompose.Run` or `Swap`, and the
developer handoff was not updated with current-head runtime or mutation evidence. The method's
fail-closed rule requires the verifier to run known-bad input, show that the check fires, and commit
that evidence (`docs/cencon/DEVELOPMENT_METHOD.md:340-350`). That QA artifact does not exist.

## Independent check record

- Release build: exit 0, `Build succeeded`, 4 warnings, 0 errors.
- Issue-specific tests: exit 0, 38 passed, 0 failed, total 38.
- Full suite run 1: exit 1, 1,353 passed, 2 failed, total 1,355.
- Full suite run 2: exit 1, 1,354 passed, 1 failed, total 1,355; TRX enumerated exactly one failed
  result and named it above.
- Each named full-suite failure passed alone; this diagnoses a suite-wide race/shared-state defect,
  not a green full-suite run.
- Compiled late-camera fixture: early inset screen blue, late inset camera red, outside screen blue,
  audio present, camera SHA-256 unchanged.
- Compiled corner-change recompose fixture: old position screen blue, new position camera red,
  screen-only SHA-256 unchanged.
- Compiled CLI manifest readback on both synthetic and real-media copies: exit 0,
  `ComposedCamera: true`, no `PostProcessing.compose` record.
- No physical-camera recording was made in this gate round.
