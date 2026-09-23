# Issue #28 - Developer handoff, round 5 (the spec amendment: observe, do not claim)

PR #32, branch `issue-28-camera-failure-boundaries`.

Previous rounds: `handoff.md` (round 1), `handoff-round3.md`, `handoff-round4.md`; gate verdicts in
`docs/cencon/review/`. Round 4 passed QA 12/12 and was then REJECTED by the Review Gate for the
third time, on one theme: **the manifest claimed a completeness it had not established**, and the
gate reproduced three false-clean results empirically.

The human's ruling of 2026-08-28 changed the design rather than asking for a fourth attempt at the
same proof. This round implements that amendment: AC10 amended, AC13-AC17 new, assumption A7.

---

## 1. What changed, and why it is smaller than what it replaces

The recorder no longer tries to establish that `camera.mp4` is complete. It records **what it
observed**, and its one judgement has somewhere honest to go when the evidence does not reach.

### The new manifest contract

`CameraTruncated` (bool) is **removed**, not kept alongside (A7). In its place:

| field | type | meaning |
|-------|------|---------|
| `CameraCapturedSeconds` | number | unchanged - the last output position ffmpeg reported |
| `CameraStopKind` | string | `clean-quit` \| `force-killed` \| `exited-early` \| `abandoned`; **absent** when no stop ever watched the process end |
| `CameraStderrComplete` | bool | true only when ffmpeg's stderr was drained to END OF STREAM |
| `CameraComplete` | string | `yes` \| `no` \| `unknown` - a THREE-STATE verdict, never a bool |

`CameraFile` and `CameraStartOffsetSeconds` are unchanged. A recording with no camera still writes
none of these (AC11 unaffected).

`CameraComplete` is decided in `FfmpegCameraRecorder.Completeness`, and `yes` is a one-way door
needing the WHOLE presence:

1. `StopKind == clean-quit` (it answered `q` and exited on its own, so ffmpeg wrote the trailer);
2. `StderrComplete` (the verdict is read from a COMPLETE log, not a stream still being delivered);
3. it reported writing output at all; and
4. that output position was still **advancing** within 3 s of the stop request.

`no` is reserved for what is KNOWN short or broken: `exited-early`, `force-killed`, or never a
frame. **Everything else is `unknown`**, including cases nobody anticipated. Writing `unknown` is
never a failure of the implementation; writing `yes` from an absence is.

### The three gate-reproduced cases, and what each one now records

| gate case | what happens now |
|---|---|
| `ONE_TICK_STALL` | `clean-quit`, `stderrComplete: true`, `captured 0.5s`, **`complete: unknown`** |
| `ONE_TICK_INCOMPLETE_STDERR` | `clean-quit`, `stderrComplete: false`, **`complete: unknown`** |
| `FORCED_KILL_AFTER_OUTPUT` | **`force-killed`**, **`complete: no`**, and `Stop()` THROWS `CameraForceKilledException` to its caller instead of returning cleanly (AC14) |

### The AC13 instrument, and the two traps it had to avoid

The stall is caught by the **last time the output position MOVED FORWARD**, snapshotted at the
moment the stop was requested (`OutputStallWindow` = 3 s; ffmpeg prints progress ~2x/second, so
that is six missed reports).

- It counts an **advance**, never an arrival. ffmpeg prints a final summary line on `q`, and a
  stalled camera's summary repeats the position it stalled at - an arrival-based check would read
  that repeat as activity and certify the stall.
- It reads a **snapshot taken at the stop request**, not the live value. ffmpeg flushes what it
  holds when told to quit, so a stalled camera can push its position forward on the way OUT -
  judging freshness after that flush would let the parting tick certify the stall.

Both traps are pinned by their own tests and by mutations M2 and M3.

### Defect 1: a real lifetime owner, not just a retained handle (AC16)

The gate was right that round 4 fixed nothing here: the recorder correctly KEPT its process handle,
and the object holding it was dropped one line later. New: `StrandedCameraOwner`
(`src/AgentEyes.Core/StrandedCameraOwner.cs`), held by `RecordingService`, which

- **retains** the recorder when `IsAbandoned` (survived quit + kill + the Dispose retry);
- **keeps that recording's claim** rather than releasing it - a live writer is still in that
  directory, and releasing would publish it to every automatic repair/packaging/transcription pass;
- **reports it on `GET /status`** as `CameraStuck: true` plus `StuckCameras: [{ Device, Pid, Output,
  Dir }]` - the PID is the field that makes it actionable;
- **retries** on the next `StartVideo`, releasing the claim and the handle once the process is gone.

The decision lives in ONE method each caller calls once (`ReleaseClaimUnlessStranded`,
`DiscardDirectoryUnlessStranded`), not as an `if` at two call sites - this exact rule has been got
wrong three times, and a branch at the call site is a branch that can be right in one place and
wrong in the other. The stop already reported failure to its caller
(`RecordingStopFailedException`); that is unchanged and still true.

### One place writes the record

`CameraTrackRecord.Write(manifest, camera)` is now the only method in the product that assigns
`CameraComplete` / `CameraStopKind` / `CameraStderrComplete` / `CameraCapturedSeconds` (plus
`CopyTo` for the stop's read-modify-write of the start record). Both writers - the service's stop
and the CLI's `video` command - call it. This exists because a literal at either call site would put
the original defect straight back into the file the user keeps while every behavioural test still
passed. See section 3: that is not hypothetical, it is what the first version of the wiring test let
through.

### Files changed

| file | change |
|------|--------|
| `src/AgentEyes.Core/Video/CameraObservation.cs` | NEW - `CameraStopKind`, `CameraCompleteness`, and their manifest spellings |
| `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs` | observations recorded; three-state `Completeness`; `IsAbandoned`; `ProcessId`; `CameraForceKilledException`; injectable clock |
| `src/AgentEyes.Core/Video/ICameraProcess.cs` | `ProcessId` (captured at start - reading it after the handle is released throws) |
| `src/AgentEyes.Core/StrandedCameraOwner.cs` | NEW - the lifetime owner and the `/status` rows |
| `src/AgentEyes.Core/CameraTrackRecord.cs` | NEW - the one place the manifest record is written |
| `src/AgentEyes.Core/Manifest.cs` | `CameraTruncated` removed; three new fields |
| `src/AgentEyes.Core/RecordingService.cs` | holds the owner; both exits go through it; `/status` fields; retry on start |
| `src/AgentEyes.Core/Commands.cs` | same record through the same writer; `[ok]` only for `complete: yes` |

---

## 2. What was NOT changed (no regression on the earlier rounds)

- The **round-1 gate defects** and the **round-3 improvements the gate confirmed working** are
  untouched: the Create/Open split, the CLI failure boundary, the open-header probe, the pre-stop
  process-loss check, and Devices exception propagation. Their tests still pass, and the gate's own
  ordering/boundary scans still fire (they run in the same suite).
- **No new wait on the startup success path.** Nothing in this round touches `StartAndProbe`, so
  the round-2 AC3/AC9 regression is not reintroduced. The busy-camera path still takes the
  natural-exit branch, is confirmed gone, and its directory is still discarded - the abandoned
  branch, which is the only new behaviour on that path, requires a process that survives a kill.
- **Decision 4 is intact**: a camera lost mid-run does NOT fail the stop. Only a FORCE-KILL throws,
  and only for a camera that opened.
- Issue #29 (camera preview, PR #31) was not touched.

---

## 3. How every new and changed check was demonstrated to FAIL first

`docs/cencon/proof/issue-28/mutation-evidence-round5.txt` - 13 mutations, each applied alone, built,
run, and reverted, with verbatim failing output; the green baseline is re-proved at the end.

| mutation | what it removes | RED |
|---|---|---|
| M1 | the AC13 stall clause | 3 |
| M2 | the stop-time snapshot (judge from the live advance) | 1 |
| M3 | advance -> arrival | 1 |
| M4 | the AC15 stderr result | 1 |
| M5 | the AC14 report to the caller | 2 |
| M6 | force-kill recorded as a clean quit | 2 |
| M7 | **the fail-open fix: always answer `unknown`** | 4 |
| M8 | retention of a stranded camera | 6 |
| M9 | the stop releases its claim around the owner | 3 |
| M10 | the PID on the `/status` row | 2 |
| M11 | AC10's `exited-early` | 1 |
| M12 | a literal verdict at the call site | 1 |
| M13 | the CLI's camera record | 1 |

**M7 is the one that matters most.** It is the fail-open fix the amendment warns about - a recorder
that answers `unknown` to everything satisfies AC13-AC16 and tells the user nothing. Four checks go
red on it, including AC17's positive control.

### A check of mine failed open, and it is recorded rather than quietly replaced

The FIRST version of the manifest-wiring test asserted that each writer method READ the four
observations somewhere in its body. Mutation M12 (`manifest.CameraComplete = "yes"` at the call
site) left it **GREEN**, because the same method still read `Completeness` for a log line two
statements later. That is a check that survives the defect it names - a defect, not weak coverage.
It was rewritten to assert that those manifest properties are assigned in exactly ONE method in the
whole product, read out of IL; M12 then fires. The stated limit is in the test: it proves WHERE the
assignment happens and what that method reads, not that each right-hand side matches its left-hand
side - clause 1 is what reduces the remaining surface to eight lines.

### Existing regression tests: two were STRENGTHENED, none weakened or deleted

`CaptureClaimOwnershipTests.TheStopReleasesThisSessionsOwnClaim_NotWhateverHoldsTheDirectory` and
`SessionManifestTests.Stop_UpdatesTheExistingRecordInsteadOfReplacingIt` both asserted the literal
text `RecordingWorkset.Release(claim)` inside `RecordingService.Stop`. That pinned one spelling of
one call site, so it broke when the release moved behind the owner while saying nothing about the
guarantee. Both now pin the guarantee instead, and the first gained a check the old one could not
make: **no method in either product assembly releases a claim by directory name**
(`RecordingWorkset::ReleaseForTests`, the only by-name release, has zero product callers, read from
IL). Mutation M9 fires on them.

`CameraTrackTests` was updated for the new contract, including a new test that `unknown` survives
the round trip as itself and that `CameraTruncated` no longer appears in the JSON at all.

### Coverage added

`CameraFailurePathTests` 33 -> 48, plus `StrandedCameraOwnerTests` (10 new). Suite **900 -> 926**.

---

## 4. The gate (run by me, not by the human)

```
dotnet build AgentEyes.sln -c Release   ->  Build succeeded.  0 Error(s)
dotnet test  AgentEyes.sln -c Release   ->  Passed!  Failed: 0, Passed: 926, Total: 926
```

**The build trap was closed, not assumed.** `AgentEyesApp.exe` was running from
`src\AgentEyes.App\bin\x64\Release\...` for the whole of this work and holds locks on that output,
so every build and test run above used an **isolated artifacts directory**
(`dotnet build --artifacts-path D:\ReposFred\.agenteyes-dev-artifacts`, outside the repo) - a fresh
tree that cannot serve a stale binary. The tray app was NOT killed. There is no `bin\Release\`
directory in this checkout (one was created by an early single-project build and removed; the
solution build lands in `bin\x64\Release\`).

---

## 5. How QA should verify AC13 - AC17

**Read this section before planning the run: three of these five cases cannot be produced by a real
webcam on demand, and saying so is part of the result.**

### AC17 - the positive control. Do this one FIRST and in the SAME build.

A normal camera recording over REST (`POST /record/start` with a camera, ~20 s, `POST /record/stop`).
The manifest must read:

```json
"CameraStopKind": "clean-quit",
"CameraStderrComplete": true,
"CameraComplete": "yes",
```

The CLI prints `[ok] camera.mp4 (...) video only - complete: yes`. If a healthy recording does not
say `yes`, the fix has degenerated into "always unknown" and AC13-AC16 mean nothing - that is a
FAIL, not a conservative pass. Run AC3's duration check on the same recording (the 1.0 s delta;
round 4 measured 0.233 s REST / 0.267 s CLI / 0.367 s launcher, and nothing in this round touches
the start path).

### AC10 - reproducible on real hardware, as in round 4

Start a camera recording, kill the camera `ffmpeg.exe` by PID mid-run, let the screen recording run
on, then stop. Expect `recording.mp4` intact and decodable, and:

```json
"CameraStopKind": "exited-early",  "CameraComplete": "no",  "CameraCapturedSeconds": <what it got>
```

plus a WARNING naming the camera in the app log.

### AC14 - reproducible on real hardware, with one trick

Force-kill means ffmpeg IGNORED `q`. To make a real ffmpeg do that, **suspend the camera
ffmpeg process** before stopping (any suspend tool, e.g. `pssuspend <pid>`), then stop the
recording. The quit times out after 8 s, the kill lands, and the stop reports a FAILURE to its
caller. Expect `"CameraStopKind": "force-killed"`, `"CameraComplete": "no"`, `/status`
`LastStopFailed: true` with the camera named in `LastStopError`, and (over the CLI) exit code 1.

### AC13, AC15, AC16 - the seam, not the hardware

A camera that stalls without dying, a stderr that never reaches EOF, and a process that survives
`Kill(entireProcessTree: true)` are not behaviours a real ffmpeg can be asked to perform - which is
exactly why the `ICameraProcess` seam exists and why the Review Gate wrote its probe against it.
Verify them the way the gate did:

- Run `CameraFailurePathTests` and `StrandedCameraOwnerTests` (48 + 10) and confirm all execute.
- Then **apply your own mutations** - do not take mine on faith. The three that matter most:
  delete the `OutputWasAdvancingAtTheStop` clause (AC13 must go red), make `_stderrComplete = true`
  unconditionally (AC15), and make `StrandedCameraOwner.TryRetain` always return false (AC16).
  `mutation-evidence-round5.txt` lists all 13 with anchors.
- If you want the gate's probe shape: `FfmpegCameraRecorder.CreateOver(proc, name, out, log,
  timeout, clock)` takes both a fake `ICameraProcess` and a **clock you move by hand** - that clock
  is how "stalled for thirty seconds" is reached in milliseconds and without a sleep.
- AC16's `/status` shape is verifiable from the code and the unit tests
  (`StrandedCameraOwnerTests.Report_NamesTheStuckProcessAndItsPid` asserts the PID by value); a LIVE
  stuck PID on `/status` cannot be produced on this hardware. Record that as code-verified, not as
  a passed runtime check - do not report a runtime check you did not run.

### The traps that still apply

- The REST API (`127.0.0.1:7882`), UIA, and PrintWindow all work with the window in the background.
  Never force-foreground the app and synthesize input.
- The recording HUD is capture-excluded - assert HUD/recording state via UIA or `/status`, never a
  screen grab.
- **Build from an isolated worktree or `--artifacts-path`.** The tray app locks the normal Release
  output and will hand you an hour-stale binary, as it did in round 4. Release output is
  `bin\x64\Release\`, never `bin\Release\`.
- `presets.json` / `config.json`: back up and restore if you drive the preset editor.

---

## 6. CenCon impact

No drift. No change to the component map and no change to the privacy posture (visible /
controllable): this round adds no capture surface, no new file, and no new network call. It makes
the recording's own record of itself MORE conservative - a track is now only called complete when
that was established - and it makes a camera process the app could not stop **visible on
`/status`** instead of silently held, which strengthens the "controllable" half rather than
weakening it.

---

## 7. What I believe

I believe this is finished, and I believe the honest limits above are part of it: AC13, AC15 and
AC16 are established at the seam and cannot be produced by this hardware, and the manifest-wiring
guard proves where the verdict is assigned rather than that every right-hand side matches its
left-hand side. The three cases the Review Gate reproduced now record `unknown`, `unknown` and
`no` respectively, a healthy recording still records `yes` in the same build, and every check that
says so was run against code with its fix removed first.
