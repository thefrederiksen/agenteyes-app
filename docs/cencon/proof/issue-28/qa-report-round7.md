# QA round 7 - issue #28, camera track: PASS, 17/17 against the AMENDED spec

- **Issue:** #28 (spec amendment 2026-08-28: AC10 amended, AC13-AC17 new, A7 new; AC1-AC9, AC11, AC12 stand)
- **PR:** #32, branch `issue-28-camera-failure-boundaries`
- **Head verified:** `a0d1fe3` ("Record the round-7 proof for the camera failure boundaries (#28)")
- **Rejected tree used as the known-bad control:** `b61553c` (Review Gate round 5, REJECT, 2 blocking defects)
- **Verdict:** PASS -> `flow:ready-gate`. QA does NOT merge and does NOT close (DEVELOPMENT_METHOD.md D7 supersedes D5).

Evidence committed beside this report:

| file | what it is |
|---|---|
| `qa-round7-probe.cs.txt` | the independent seam probe QA wrote from the criteria |
| `qa-round7-probe-results.txt` | its output on BOTH trees - the rejected tree first |
| `qa-mutations-round7.txt` | 14 mutations + 3 re-applied by hand, full suite each time |

---

## 0. The build trap, closed

Everything below was built and run from a **separate `git worktree --detach a0d1fe3`**
(`D:\ReposFred\agenteyes-qa28-r7`), never from the shared checkout and never from `bin\Release\`.
The installed tray app (v1.6.2, `%LOCALAPPDATA%\AgentEyes\app`) was running from its own install
directory and could not have served a stale binary into this worktree. Every app-driven check below
was made against a process whose **command line was read and recorded** as
`D:\ReposFred\agenteyes-qa28-r7\...\bin\x64\Release\...\AgentEyesApp.exe --tray`.

```
dotnet build AgentEyes.sln -c Release   Build succeeded.  0 Error(s)   (4 pre-existing xUnit warnings)
dotnet test  AgentEyes.sln -c Release   Passed!  Failed: 0, Passed: 942, Total: 942
```

A second QA session was verifying issue #33 on this machine while this ran. Its branch and its
worktree were not touched. Its app briefly owned `127.0.0.1:7882`; the collision was detected from
the `/status` payload shape (it carried `PreviewCameraFrame`, a field this branch does not have) and
that data was **discarded rather than read as this build's**.

---

## 1. The two Review Gate round-5 blockers - proved fixed on a probe that FIRES on the rejected tree

QA wrote its own `ICameraProcess` seam probe from the amended criteria (assembly named
`AgentEyes.Tests`, so the existing `InternalsVisibleTo` reaches the internal seam, the real compiled
`FfmpegCameraRecorder`, `StrandedCameraOwner`, `RecordingService.Status()` and `RecordingWorkset`).
It was run against the **rejected round-6 tree `b61553c` FIRST**. Every row prints its own
expectation and a MATCH/MISMATCH verdict, so a row that never ran cannot read as a pass.

**19 rows. On `b61553c`: 8 MISMATCH. On `a0d1fe3`: 0 MISMATCH.** The 8 are exactly the two defects.

### Defect 1 - every non-negative exit code was a clean quit

| case | b61553c (rejected) | a0d1fe3 (head) |
|---|---|---|
| `DELIVERED_QUIT_THEN_EXIT_1` | `stopKind=clean-quit complete=yes` | `stopKind=(not observed) complete=unknown` |
| `..._2`, `..._69`, `..._137`, `..._254`, `..._256`, `..._2147483647` | all `clean-quit` / `yes` | all `(not observed)` / `unknown` |
| `DELIVERED_QUIT_THEN_EXIT_-5` | already `(not observed)` / `unknown` | unchanged |

**Verified in BOTH directions, which is the part that mattered.** The fix is an enumerated set
`QuitExitCodes = { 0, 255 }` (`FfmpegCameraRecorder.cs:221`, read at `:1030`), not a narrower range:

- **255 REMAINS ACCEPTED.** `HEALTHY_AC17_EXIT_255` -> `stopKind=clean-quit complete=yes` on head.
- Mutation **M2** removes 255 from the set and
  `Stop_WhenADeliveredQuitEndedTheProcessNormally_IsStillRecordedAsACleanQuit` goes RED - so the
  acceptance of 255 is pinned by a test, not by luck.
- Mutation **M3** adds 1 to the set, and **M1** restores the rejected `exitCode >= 0`; both turn
  `Stop_WhenADeliveredQuitEndedInAnFfmpegErrorCode_IsNeverRecordedAsACleanQuit` RED.

### Defect 2 - `abandoned` asserted after the FIRST kill timeout

| case | b61553c (rejected) | a0d1fe3 (head) |
|---|---|---|
| `DIED_BEFORE_DISPOSE_RETRY` | `hasExited=True isAbandoned=False stopKind=abandoned complete=unknown` | `stopKind=(not observed) complete=unknown`, and mid-run `afterStop(stopKind=(not observed), isAbandoned=True)` |
| `RETAINED_PROCESS_ALIVE` (AC16) | `abandoned` / `unknown` | `abandoned` / `unknown` - unchanged |

The split is exactly what the amendment asks for: `_killRefused` (a fact about the refusal, read by
`IsAbandoned`, live from the first refusal so the owner can act) is now separate from the durable
`_stopKind`, which is written **only in `Dispose`** (`FfmpegCameraRecorder.cs:1267`) once all three
clauses have happened. A retry that finds the process gone writes nothing - not `abandoned`, not
`exited-early` (`:962`, `:1066`).

**The test that used to REQUIRE the overclaim was fixed, not merely renamed.**
`CameraFailurePathTests.cs:1358-1400` is now
`StopKind_WhenTheProcessDiesBeforeTheDisposeRetry_IsNeverRecordedAsAbandoned` and asserts
`Assert.Null(rec.StopKind)` before and after the retry. Mutation **M4** (write the provisional
`Abandoned` back at the refused kill) turns it RED. A companion test
`StopKind_WhenTheSurvivingProcessFinallyDiesAfterTheDisposeRetry_StaysAbandoned` keeps the property
the old test was really protecting; mutation **M5** (never write `abandoned` in `Dispose`) turns it
and `Stop_WhenFfmpegSurvivesEverything_MarksItselfAbandonedAndKeepsItsProcessId` RED.

---

## 2. AC17's positive control, on the SAME build

Two independent confirmations, both from the build that produced the 942/942 run above:

1. **Seam:** `HEALTHY_AC17_EXIT_0` and `HEALTHY_AC17_EXIT_255` -> `clean-quit` / `complete=yes`.
2. **Real hardware:** four real recordings on the `HD Webcam eMeet C960` wrote
   `"CameraStopKind": "clean-quit"`, `"CameraStderrComplete": true`, **`"CameraComplete": "yes"`**
   into `manifest.json` (REST, CLI and launcher-preset runs - see section 4).

**The always-unknown cheat still fires.** Mutation **M6** makes `Completeness` answer `Unknown`
unconditionally: **7 tests RED**, including both positive controls
(`Stop_WhenTheOutputKeptAdvancingUntilTheStop_RecordsTheTakeAsComplete` and
`Stop_WhenTheOutputAdvancedTwiceAndWasStillFresh_IsRecordedAsComplete`).

---

## 3. The four round-4 probe cases and the round-1 / round-3 fixes - no regression

Same probe, same run:

| case | head |
|---|---|
| `ONE_TICK_STALL_2_9S` (AC13) | `clean-quit` / **`unknown`**, captured 0.5 |
| `ZERO_TICK_INCOMPLETE_STDERR` (AC15) | `stderrComplete=False` / **`unknown`**, `lost=False` - no false emptiness claim |
| `FAILED_QUIT_THEN_ERROR_EXIT` | `stopKind=(not observed)` / **`unknown`** |
| `RETAINED_PROCESS_DIED` | row gone, claim released, recorder disposed on the next plain LOOK - `rowsAfterDeath=0 claimHeldAfterDeath=False holdsAny=False`, with `rowsWhileAlive=1 claimHeldWhileAlive=True` as the control |
| `AC16_STATUS` | `RecordingService.Status()` reports `CameraStuck=True` with the row `QA Cam#31337` while alive, and `CameraStuck=False` / 0 rows after the process dies |
| `EXITED_EARLY` (AC10) | `exited-early` / `no` |
| `FORCED_KILL_AFTER_OUTPUT` (AC14) | `force-killed` / `no`, and `Stop` throws `CameraForceKilledException` |

Round-1 and round-3 improvements were re-mutated on THIS tree rather than assumed to have survived
another edit of the same file:

| mutation | reverts | result |
|---|---|---|
| M9 | round-3 two-header open probe (`&&` -> `||`) | 1 RED |
| M10 | round-1 defect 2 - a refused kill no longer throws | **20 RED** |
| M11 | round-1 defect 5 - the `/devices` `catch { cameras = [] }` fallback | 1 RED |
| M12 | round-1 defect 4 - the pre-stop process-loss observation | 1 RED |
| M13 | round-6 AC13 - one advance is enough again | 1 RED |
| M14 | AC15 - conclude from an incomplete stderr | 2 RED |
| M15 | round-1 defect 1 - the CLI's outer `finally` becomes `catch { ... throw; }` | 2 RED |

**The Review Gate's CLI follow-up is closed.** The guard is now per site
(`StrandedCameraOwnerTests.cs:434-505`), telling the two identical `RetainIfStranded` calls apart by
IL exception region. Deleting **either boundary alone** now fails:

- **M7** - delete only the failed-open transfer (`Commands.cs:351`) -> RED.
- **M8** - delete only the final `finally` transfer (`Commands.cs:511`, the gate's defect-5 site) -> RED.

Both were the mutations the gate reported as NOT firing in round 6.

---

## 4. The 17 acceptance criteria

Runtime work was done against the QA worktree build; the camera was the physical
`HD Webcam eMeet C960` (a second DirectShow device, `OBS Virtual Camera`, is also attached).

| AC | Result | Expected vs Actual |
|----|--------|--------------------|
| **AC1** devices API lists cameras | PASS | `GET /devices` -> **HTTP 200**, `cameras` = `["HD Webcam eMeet C960", "OBS Virtual Camera"]`. The "no camera -> `[]`" branch is NOT observable on this hardware and is code-verified only (`RestServer.cs:405`, no `catch` - an enumeration failure answers 500, so `[]` really does mean "no camera"). Stated, not glossed. |
| **AC2** CLI lists cameras | PASS | `agenteyes screens` prints a `CAMERAS: DirectShow video devices ...` section with both names. `(none found)` branch not observable here (`Commands.cs` prints it when the list is empty). Standing wording deviation: `CAMERAS:` vs AC2's `Cameras:` - a case-insensitive match satisfies it; flagged to the human in round 1 and never re-ruled. |
| **AC3** two separate files | PASS | REST run `2026-08-28_212208_video`: both `recording.mp4` and `camera.mp4` on disk; `ffprobe` **13.900000** vs **14.399986** -> **delta 0.500 s** (limit 1.0 s); `camera.mp4` has exactly one stream, `codec_type=video`. |
| **AC4** manifest records the camera track | PASS | `"CameraFile": "camera.mp4"`, `"CameraStartOffsetSeconds": -0.714` (numeric), `"camera.mp4"` in `Files`. Standing casing deviation: PascalCase keys vs AC4's camelCase quoting - matches every other key this repo writes; flagged in round 1, never re-ruled. |
| **AC5** status reports the camera | PASS | While a camera recording ran: `"Camera": "HD Webcam eMeet C960"`. While a recording started **without** a camera ran: `State=recording`, `Camera` is **null** (verified live over REST, not inferred). Idle: null. |
| **AC6** preset round-trip | PASS | Driven through UI Automation: `CameraBox` `(None)` -> `HD Webcam eMeet C960`, Save -> `presets.json` `Camera = 'HD Webcam eMeet C960'`, `CameraFps = 30`; app restarted -> reopened editor still shows it and `presets.json` still carries it; launcher **REC/STOP** produced `2026-08-28_213742_video` with both files, `ffprobe` **14.000000** vs **14.399986** -> **delta 0.400 s**, manifest `Complete=yes`. `presets.json`/`config.json` were backed up and restored. |
| **AC7** CLI parity | PASS | `agenteyes video --screen 2 --source none --camera "OBS Virtual"` -> both files; `ffprobe` **11.366667** vs **11.899988** -> **delta 0.533 s**; single `codec_type=video` stream; manifest `CameraFile`, `CameraStartOffsetSeconds -0.494`, `CameraStopKind clean-quit`, `CameraComplete yes`. |
| **AC8** unknown camera fails the start | PASS | CLI: **exit 1**, `no DirectShow camera matches "no-such-device"`, and **no directory** - proved against an instrument shown to create and see directories at the CLI's real root (`<CWD>\recordings\`), not at `%USERPROFILE%\Videos\AgentEyes`. REST: **HTTP 400** with body naming `no-such-device`, `/status` `idle`, **0** new directories, and a well-formed request to the same endpoint returned 200 as the control. |
| **AC9** busy camera fails the start | PASS | Holder asserted alive **before and after**, and asserted to have printed ffmpeg's own `Input #0, dshow` header before its result was believed. CLI: **exit 1**, `the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5). Likely cause: ... already in use by another application.`, **0 session directories** left behind. REST: **HTTP 400**, same diagnosis, `/status` `idle`, `CameraStuck=False`, **0** new directories. Control: the identical call created a directory once the camera was free. |
| **AC10 (amended)** mid-run loss reported as loss | PASS | Killed the camera ffmpeg (PID 13336) by PID mid-run: the SCREEN ffmpeg (PID 21160) was **still alive** afterwards; `recording.mp4` **27.866667 s** and decodes end to end (`ffmpeg -f null -`, no output, exit 0); manifest `"CameraStopKind": "exited-early"`, `"CameraComplete": "no"`, `"CameraCapturedSeconds": 6.73`; app log `[WARN] ... the camera "HD Webcam eMeet C960" stopped during the recording ...`. |
| **AC11** no regression with no camera | PASS | CLI `video` with no camera: one `recording.mp4`, **no** `camera.mp4`, and the manifest contains **no key matching `Camera` at all** (checked as a regex over the whole file, not by naming three keys). |
| **AC12** gate | PASS | Build succeeded / 0 Errors; `Failed: 0, Passed: 942, Total: 942`, run by QA in the isolated worktree off the `x64` Release output. |
| **AC13** one tick then stall is not complete | PASS | Seam `ONE_TICK_STALL_2_9S` -> `clean-quit`, `stderrComplete=True`, `captured=0.5`, **`complete=unknown`**. Mutation M13 (accept a single advance) turns `Stop_WhenTheCameraTickedOnceAndStalledInsideTheFreshnessWindow_IsNeverRecordedAsComplete` RED. |
| **AC14** a force-killed file is never claimed complete | PASS | Reproduced for real by **SUSPENDING** the camera ffmpeg (`NtSuspendProcess`, rc=0) so it could not answer `q`, twice: manifest `"CameraStopKind": "force-killed"`, `"CameraComplete": "no"`, `CameraCapturedSeconds 5.69`. The stop surfaced it to its caller - app log shows `CameraForceKilledException` thrown from `FfmpegCameraRecorder.Stop()` and caught in `AgentEyes.Commands.Video`, and the CLI printed `[fail] the camera did not stop cleanly` (`Commands.cs:486-491`, which then `return 1`). Seam row `FORCED_KILL_AFTER_OUTPUT` -> `force-killed` / `no` / `threw=CameraForceKilledException`. |
| **AC15** incomplete stderr never claimed complete | PASS | Seam `ZERO_TICK_INCOMPLETE_STDERR` -> `stderrComplete=False`, **`complete=unknown`**, and `lost=False` - no positive "camera.mp4 is EMPTY" claim from an unfinished read. Mutation M14 turns 2 tests RED. |
| **AC16** an abandoned camera stays reachable and is reported | PASS | Seam: `RETAINED_PROCESS_ALIVE` -> stop threw `CameraStopFailedException`, `IsAbandoned=True`, handle KEPT (`disposes=0`), `stopKind=abandoned`, `complete=unknown`. Owner: retained with its claim (`rowsWhileAlive=1`, `claimHeldWhileAlive=True`), and `RecordingService.Status()` reports `CameraStuck=True` with the row carrying the **PID** (`QA Cam#31337`). Once the process dies, the next plain LOOK drops the row, releases the claim and disposes the recorder. `/status` on the live app carries the `CameraStuck` / `StuckCameras` fields. |
| **AC17** POSITIVE CONTROL - a good recording still says `yes` | PASS | See section 2. Four real manifests wrote `"CameraComplete": "yes"` in the same build that passes AC13-AC16, and the always-unknown cheat (M6) turns 7 tests RED including both positive controls. |

---

## 5. The stack above this branch

Merged this head into `origin/issue-36-circular-camera-overlay` (which carries #33 and #35 beneath
it) in a throwaway worktree, without committing:

```
git merge a0d1fe3                      clean merge, no conflicts
dotnet build AgentEyes.sln -c Release  Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release  Passed!  Failed: 0, Passed: 1154, Total: 1154
```

The developer's 1154/1154 is confirmed independently. The concurrent QA session's branch
`issue-33-hud-live-preview` was not touched.

---

## 6. Hygiene

```
git diff --check origin/main...a0d1fe3   ->  0 lines          (the FULL head, not just src and tests)
git diff --check origin/main...b61553c   ->  2078 lines       (the rejected tree - the instrument fires)
```

The whitespace instrument was proved against a known-bad committed tree rather than trusted for
answering nothing. A byte scan for `\r` over every file the PR adds or changes also finds none.

---

## 7. Recorded honestly

- **Two instruments were caught FAILING and were fixed rather than believed.** The ffmpeg
  exit-code measurement printed an empty exit code and was rewritten until it reported
  `BROKEN INSTRUMENT` (the camera was busy) instead of a blank; and the first AC9 CLI holder used
  `OBS Virtual Camera`, which is **not** an exclusive device - the recording legitimately succeeded,
  and that run was **rejected as AC9 evidence** and re-run against the physical webcam. It is kept
  here as AC7 evidence, which is what it actually is.
- **`git diff --check origin/main...HEAD` cannot see the working tree.** An injected CRLF was not
  reported, because the three-dot form compares commits. The claim above is therefore made against
  the committed trees, with the rejected tree as the positive control.
- **Two clauses are NOT runtime-verified and are marked as such, not glossed:** AC1's
  "on a machine with no camera `cameras` is `[]`" and AC2's `(none found)`. Two cameras are attached
  to this machine. Both are judged from code with file:line.
- **The CLI exit code could not be read back through PowerShell's `Start-Process -PassThru` object**
  on the AC14 runs (it returned empty three times, including through a `cmd.exe` `%ERRORLEVEL%`
  wrapper that never launched). AC14's caller-facing clause is therefore established from the
  observed `[fail]` console line, the thrown `CameraForceKilledException` in the app log, and
  `Commands.cs:486-491` - not from an exit code this session managed to read. Exit codes WERE read
  successfully on AC8 (1), AC9 (1) and AC11 (0), where `-Wait` was used.
- **Two spec-wording deviations still stand** from round 1 and are restated rather than quietly
  accepted: `CAMERAS:` vs AC2's `Cameras:`, and PascalCase manifest keys vs AC4's camelCase quoting.
  Neither was re-ruled on by the amendment.
- **QA fixtures cleaned up:** all six QA recordings removed, `presets.json` and `config.json`
  restored from backup, the QA app stopped, the installed v1.6.2 tray app restarted and confirmed
  `idle` on `127.0.0.1:7882`, no stray `ffmpeg.exe` left on the machine.

---

**VERIFIED - all 17 acceptance criteria met.** Moving to `flow:ready-gate` for the Review Gate.
QA does not merge (D7).
