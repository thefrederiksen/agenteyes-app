# QA report - issue #28, round 9 (PR #32, commit `84221fd`)

**Verdict: PASS - 17/17 acceptance criteria verified against the AMENDED spec.**

Verified by the QA Agent in an isolated detached worktree at
`D:/ReposFred/agenteyes-qa28-r9` (`git worktree add --detach 84221fd`), restored before
building, Release output read from `bin\x64\Release\` only. The tray app that normally holds
that output was stopped for the runtime section and the INSTALLED build was put back
afterwards; the user's `presets.json` and `config.json` were backed up before the AC6 UIA
work and restored byte for byte.

The developer's report was read as context and verified independently. Nothing in it was
accepted on its word: every number below was produced by a command run in this session, and
every check was fired against a known-bad input before its green was believed.

---

## 1. What round 9 changed, and what it had to prove

Gate round 7 rejected the previous head with ONE blocking defect. `CameraTerminationRecord`
kept `_quitsDelivered` as a LIFETIME counter, and the derivation paired it with the NEWEST
observed exit - two facts from two different termination rounds. Round 1 delivers `q` and is
refused; the `Dispose()` retry's write to stdin fails while the process exits 0; round 1's
stale delivery then certified round 2's exit as `clean-quit`, reopening completeness to `yes`.

The fix records a delivery against the round it happened in (`_roundQuitDelivered`) and
latches `_exitFollowedADeliveredQuit = _roundQuitDelivered` inside
`ExitObservedAfterTermination()` - the two facts are paired at the one moment both are known.
Only `CameraTerminationRecord.cs` changed in the product
(`src/AgentEyes.Core/Video/CameraTerminationRecord.cs:144`, `:171`, `:202`, `:234`, `:284`,
`:349`).

## 2. The three rows the round turns on

Driven through QA's OWN probe - QA's own fake `ICameraProcess`, QA's own scenario driver,
QA's own expectations table - in the real production order: `Stop()`, then the normal
`Dispose()` retry. Verbatim output in
`docs/cencon/proof/issue-28/qa-round9-probe-results.txt`; the probe source is committed
beside it as `qa-round9-probe.cs.txt`.

| Row | Required | Rejected tree (QA-M1) | Reviewed head |
|-----|----------|-----------------------|---------------|
| `PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | absent / unknown | **clean-quit / yes** | **absent / unknown** |
| `PRIOR_UNDELIVERED_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0` | absent / unknown | absent / unknown | absent / unknown |
| `PRIOR_REFUSAL_THEN_DISPOSE_RETRY_QUIT_DELIVERED_EXIT_0` | clean-quit / yes | clean-quit / yes | clean-quit / yes |

All three end `quits=2 kills=1 exited=True`, exactly as the gate's own probe reported. The
only fact that differs is which round delivered the `q`.

**The probe was fired against the rejected tree FIRST.** Restoring the exact head derivation
(`_exitFollowedADeliveredQuit` -> `_quitsDelivered > 0`) reproduced the gate's verdict line
for line:

```
FAIL PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_0 quits=2 kills=1 exited=True kind=clean-quit complete=yes abandoned=False  <-- EXPECTED kind=absent complete=unknown
```

and BOTH controls stayed green under that mutation, which is what isolates the defect to the
cross-round join rather than to the recorder at large.

**The over-correction is pinned too.** Adding `&& _refusedRounds == 0` - "a refused round
anywhere poisons the whole recorder" - takes the POSITIVE control red and leaves the failing
row green: the mirror image of QA-M1. A fix that rejected all recovery success would not
survive this build.

QA additionally added a fourth row, `PRIOR_DELIVERY_THEN_DISPOSE_RETRY_QUIT_FAILS_EXIT_255`,
because the accepted set has two members and the gate's scenario only exercised one. It
behaves identically (absent / unknown at head, clean-quit / yes on the rejected tree).

## 3. The design property, read off the COMPILED artifact

> A call site must not be able to record a stop kind it has not earned.

Checked by reflection over the built `agenteyes.dll`, not over source text, with the
broken-instrument arm stated first: **248 types inspected** (an empty inventory fails the
check outright - the first attempt at this, through `Assembly.LoadFrom` in PowerShell, threw
inside `GetTypes()` and printed "NONE", and that result was discarded as a broken instrument
rather than read as a clean run).

```
types inspected: 248
fields storing a CameraStopKind: 0 []
writable CameraStopKind properties: 0 []
methods accepting a CameraStopKind: 2 [AgentEyes.Video.CameraObservation.Text(kind), AgentEyes.Video.CameraObservation.Text(kind)]
```

The instrument was proved to FIRE: planting `private CameraStopKind? _qaPlantedStopKind` plus
an `internal void QaPlant(CameraStopKind)` on `FfmpegCameraRecorder` turned it red and named
both (QA-M12). Its limit is stated: it sees fields, properties and parameters typed
`CameraStopKind`; it would not see one smuggled through an `int`, a `string` or a boxed
object.

### The behavioural attack on the same property

Every sequence below was driven through the seam and is a row in the committed probe output.
None yielded a stop kind the attempt history had not earned:

| Sequence | Result |
|----------|--------|
| Direct `Dispose` with no earlier `Stop`, retry kill lands | `force-killed` / `no`, quits=2 kills=2 - NOT abandoned off one round |
| Direct `Dispose`, both rounds refused | `abandoned` / `unknown`, quits=2 kills=2 |
| Repeated `Dispose` (three times) after an earned abandoned | still `abandoned` / `unknown`; quits=4 kills=4, no new claim |
| `Stop` -> `Dispose` -> recovery `Stop` that finally lands | durable kind stays `abandoned`, `IsAbandoned` drops to false |
| Recovery quit answered after an earned abandoned | `abandoned` / `unknown` preserved (monotone) |
| A stop attempt interleaved with a recovery sweep, then an unwatched exit 0 | `abandoned` / `unknown` |
| Process dies between the failed stop and the `Dispose` retry | absent / `unknown` |

## 4. Every acceptance criterion

AC10 as amended; AC13-AC17 new; AC1-AC9, AC11, AC12 as originally written.

| # | Expected | Actual (this session) | Verdict |
|---|----------|-----------------------|---------|
| AC1 | `GET /devices` 200 with a `cameras` array carrying the exact DirectShow name | HTTP 200, `cameras` = `HD Webcam eMeet C960`, `OBS Virtual Camera` (2 entries) | PASS |
| AC2 | `agenteyes screens` prints a cameras section with the same names | `CAMERAS: DirectShow video devices` listing both exact names; exit 0 | PASS |
| AC3 | Two files in one directory, both play, `camera.mp4` one video stream, durations within 1.0 s | REST run `2026-08-28_233441_video`: `recording.mp4` 14.9667 s / `camera.mp4` 15.4333 s, **delta 0.467 s**; `camera.mp4` one stream, `codec_type=video`; 304 frames decoded by `ffprobe -count_frames` | PASS |
| AC4 | `cameraFile`, numeric `cameraStartOffsetSeconds`, `camera.mp4` in `files` | `"CameraFile": "camera.mp4"`, `"CameraStartOffsetSeconds": -0.622`, `Files: [recording.mp4, camera.mp4]` | PASS |
| AC5 | `/status` reports the resolved camera while recording; null without one | recording with a camera: `"Camera": "HD Webcam eMeet C960"`; recording without one: `Camera` null; idle: null | PASS |
| AC6 | Preset with a camera survives a restart in `presets.json` AND the reopened editor; the launcher produces the AC3 shape | Selected `OBS Virtual Camera` in the preset editor via UIA -> `presets.json` updated -> **full app restart** -> `presets.json` still `OBS Virtual Camera` AND the reopened editor's `CameraBox` read back `[OBS Virtual Camera]` via UIA. Set back to the real webcam, saved, and the launcher REC/STOP produced `2026-08-28_233945_video`: `recording.mp4` 15.333 s + `camera.mp4` 16.000 s (**delta 0.667 s**), `camera.mp4` video-only, `recording.mp4` carrying the mic audio | PASS |
| AC7 | CLI parity: same two-file directory and manifest fields | `agenteyes video --screen 1 --camera "eMeet" --seconds 12` -> `recording.mp4` 14.9667 s / `camera.mp4` 15.4667 s, **delta 0.500 s**, one video stream, 305 frames decoded, manifest identical in shape | PASS |
| AC8 | Unknown camera: CLI names it, exits non-zero, NO directory; REST 400 with the fragment; `/status` idle | CLI printed `no DirectShow camera matches "no-such-device"`, **exit 1**, directory counts unchanged in BOTH `%USERPROFILE%\Videos\AgentEyes` (37 -> 37) and the CLI's own `recordings` dir (5 -> 5). REST returned **HTTP 400** with `"no DirectShow camera matches \"no-such-device\""` in the body, `/status` `idle`, 37 -> 37 directories | PASS |
| AC9 | Busy camera fails the start with the AC8 shape | A second ffmpeg held the device - **proved holding** by `Input #0, dshow, from 'video=HD Webcam eMeet C960'` in its own stderr before the run. Then: `[error] the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5). Likely cause: ... already in use`, **exit 1**, 37 -> 37 and 5 -> 5 directories, holder still alive afterwards | PASS |
| AC10 (amended) | Mid-run death: screen recording survives and is valid; `exited-early` / `no` / observed seconds; WARNING naming the camera | Killed the camera ffmpeg by PID (54012) mid-run: `/status` still `recording` (elapsed 11.51 s); `recording.mp4` valid, 14.4667 s, 433 frames decoded; manifest `CameraStopKind=exited-early`, `CameraComplete=no`, `CameraCapturedSeconds=3.13`; log carried `[WARN] ... the camera "HD Webcam eMeet C960" stopped during the recording ... camera.mp4 is truncated at 3.1s` | PASS |
| AC11 | No camera: one `recording.mp4`, no `camera.mp4`, no `cameraFile` key | `2026-08-28_233530_video`: `recording.mp4` only, `camera.mp4` absent from disk, and the manifest contains no key matching `"Camera` at all; `Files: [recording.mp4]` | PASS |
| AC12 | Build clean and `dotnet test` green, including the new tests | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, **0 Error(s)**, 4 pre-existing analyzer warnings. `dotnet test AgentEyes.sln -c Release` -> **951 passed, 0 failed, 0 skipped** | PASS |
| AC13 | One tick then a stall is never `yes` | `AC13_ONE_TICK_THEN_STALL`: one advance at 0.5 s, a 30 s stall on a hand-driven clock, then a clean quit -> `clean-quit` / **`unknown`** | PASS |
| AC14 | Force-killed is never `yes`, and the stop surfaces it | `AC14_FORCE_KILLED`: `force-killed` / **`no`**, and `Stop()` threw `CameraForceKilledException` to its caller | PASS |
| AC15 | Incomplete stderr is never `yes` | `AC15_INCOMPLETE_STDERR`: clean quit, exit 0, `DrainStderr` false -> `clean-quit` / **`unknown`**. Also `EXIT_255_INCOMPLETE_STDERR` -> `clean-quit` / `unknown`: 255 earns the STOP KIND ONLY, never completeness | PASS |
| AC16 | Abandoned stays reachable and reported; kind `abandoned`, complete `unknown`, claim not released | Derivation half at the seam: `abandoned` / `unknown` only after TWO refused rounds, monotone under recovery and repeated `Dispose`, `IsAbandoned` true while the process lives. `/status` carries `CameraStuck` and `StuckCameras`; the PID clause is load-bearing - nulling `StrandedCameraOwner`'s `Pid` turns `Report_NamesTheStuckProcessAndItsPid` and `Report_WhileTheStrandedProcessIsStillAlive_KeepsReportingItAndItsClaim` red (QA-M11) | PASS |
| AC17 | POSITIVE CONTROL: a healthy recording still says `yes`, in the same build | **Three real recordings in this build** - CLI, REST and launcher-from-preset - each wrote `"CameraStopKind": "clean-quit"`, `"CameraStderrComplete": true`, **`"CameraComplete": "yes"`**. At the seam, `AC17_HEALTHY_EXIT_0` and `AC17_HEALTHY_EXIT_255` both `clean-quit` / `yes`. The anti-cheat was RUN: forcing `Completeness` to answer `Unknown` unconditionally takes the suite to **10 red**, both AC17 positive controls among them (QA-M6) | PASS |

## 5. Regression: nothing the gate confirmed over seven reviews was undone

Re-run rather than assumed, because this round touched the derivation again.

- **Round-6 blockers.** `DIRECT_DISPOSE_ONE_KILL_THEN_RETRY_KILL_LANDS` -> `force-killed` /
  `no` with two quits and two kills (Dispose performs its retry). `RECOVERY_AFTER_ABANDONED`
  -> the earned `abandoned` survives a recovery quit that finally lands; only `IsAbandoned`
  changes. Demoting the monotone test from first to last in the derivation goes 3 red (QA-M9).
- **Round-5 blockers.** `DELIVERED_QUIT_THEN_EXIT_1` -> absent / `unknown`.
  `DIED_BEFORE_DISPOSE_RETRY` -> absent / `unknown`.
- **The ENUMERATED accepted set** is still `{ 0, 255 }` at
  `src/AgentEyes.Core/Video/CameraTerminationRecord.cs:116`, listed and not ranged. A spread
  of **1, 2, 69, 137, 254, 256, `int.MaxValue`, -5 and -1** each produced an ABSENT kind and
  `unknown`. Widening it to admit ffmpeg's own exit 1 goes 2 red (QA-M8).
- **The genuine three-clause `abandoned` control** still needs two refused rounds:
  `RefusedRoundsForAbandoned` 2 -> 1 goes 7 red (QA-M7).
- **The four round-4 cases and the round-1/round-3 defects** are covered by the enumerated
  camera suite: **121 passed, 0 failed** on a `~Camera` filter.
- **`git diff --check origin/main...HEAD`**: clean, exit 0, across the full 64-file head.
- **ASCII-only**: all 64 changed files scanned byte by byte, **0 non-ASCII**. The scanner was
  proved to fire first on a planted `\xe2\x9c\x93` (reported `NON-ASCII 3 bytes`).
- **No `nul` files** anywhere in the worktree.

## 6. The stack above

`origin/issue-36-circular-camera-overlay` at `75e62ad`, merged with this head
(`--no-commit --no-ff`) in a throwaway worktree:

- no conflicts (one auto-merge in `FfmpegCameraRecorder.cs`);
- `git diff --check --cached` clean;
- `dotnet build -c Release` succeeded, 0 errors;
- **1,163 passed, 0 failed, 0 skipped** - the developer's number, reproduced independently;
- QA's own 29-row probe and the design-property check both ran GREEN in the merged tree.

The throwaway worktree was removed.

## 7. Honest limits of this verification

- **AC13, AC15 and AC16 are not producible on demand with a physical webcam** (Windows ends a
  normal ffmpeg on `q`; a dshow device cannot be told to tick once and stall). They are
  established at the `ICameraProcess` seam - the same seam the Review Gate uses - plus, for
  AC16, the `StrandedCameraOwner` tests over the real report shape.
- **The equivalence QA-M5 exposes**: `=` versus `|=` on `_exitFollowedADeliveredQuit` fires
  nothing, because a second `ExitObservedAfterTermination` is unreachable (the first sets
  `_terminated`). That is a documented equivalence, not an unguarded path.
- **The reflection inventory sees types, not intent**: a stop kind smuggled through an `int`
  or a `string` would be invisible to it. The behavioural attack in section 3 is what covers
  that side.
- **One instrument of mine was wrong and was fixed before being believed**: the first
  design-property check threw inside `GetTypes()` and printed "NONE" - a textbook fail-open.
  It was rebuilt inside the test assembly with an explicit type-count assertion.
- **Out of scope, observed and reported rather than swallowed**: a `video` recording started
  with `source: none` leaves the post-recording `package` step `failed` with
  `ffmpeg failed during extract audio (exit -22)`. This is NOT a camera defect - it reproduced
  identically on the AC11 run that had **no camera at all**, and nothing in this PR touches
  audio extraction. It is not one of the 17 criteria and is not a reason to fail this issue.
- **QA litter**: four recordings remain under `%USERPROFILE%\Videos\AgentEyes` and one under
  the repo's gitignored `recordings\` - they are the AC3/AC5/AC6/AC10/AC11/AC17 evidence.

---

**VERIFIED - all 17 acceptance criteria met.** Handed to the Review Gate as `flow:ready-gate`
(method decision D7 - QA does not merge).
