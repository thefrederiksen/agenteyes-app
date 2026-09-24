# Issue #84 - CameraPreviewTests fail at random on a first run - Developer handoff

Branch: `issue-84-camera-preview-flake` (from `main` at v1.11.4, e5c73af). Tracker: thefrederiksen/agenteyes-app.

I believe this is finished. The root cause is stated with evidence; the fix is in product and test code;
the 20-run loop on the final tree is green with its output committed next to this note; every new check
was shown to fire on a known-bad input before it was trusted; and the three earlier loop attempts that
did NOT pass are committed too, because each one found a further window and none was noise.

Files in this folder:

| File | What it is |
|------|-----------|
| `handoff.md` | this note |
| `test-loop-20-runs.txt` | THE PROOF: 20 consecutive full-suite runs on the final tree, each a fresh test-host process |
| `mutation-evidence.txt` | mutations M1-M6: each new check shown to FAIL on known-bad code, quoted, with the byte-for-byte restore |
| `test-loop-attempt-1-before-the-publish-wait.txt` | 4 of 7 green on an intermediate tree - exposed the factory-to-publication gap (section 1, link 3) |
| `test-loop-attempt-2-before-review-fixes.txt` | 20 of 20 green, then superseded by the code-review fixes (section 4) - kept because it shows the core fix held |
| `test-loop-attempt-3-before-the-holder-identity-fix.txt` | 6 of 7 green - exposed the parallel PresetEditor class moving the holder count (section 1, link 4) |

---

## 1. Root cause (acceptance criterion 1)

Three CameraPreviewTests failed at random in a full parallel run and passed alone or on the next run.
Reproduced on this branch before any change: 2 of 3 full runs failed (4 and 3 failures). From the TRX
timelines of both failing runs, the wild failures are ONE chain. Two further windows in the same class
were found by the loop itself while fixing it. All four are timing plus shared static state.

**Link 1 - the open was a thread-pool work item, and the parallel suite starves the pool.**
`CameraPreviewController.Select` queued the camera open with `Task.Run`. The product code under test has
45 `Task.Run` sites (always-on engine, post-recording queue, preview feed) and the tests block on them, so
under the full suite the shared pool is saturated and a queued work item can wait more than five seconds.
The helper `WaitForSession` waits 5 s and then THROWS (`No camera preview session #0 was created within
5s (created 0)`). Discriminating experiment: with `DOTNET_ThreadPool_MinThreads=256` the unchanged suite
passed 3 of 3 and ran faster (41-50 s vs 54-71 s). It is the pool, not the CPU. Issue #78's handoff had
recorded this mechanism and left it as the follow-up that became this issue.

**Link 2 - one aborted test poisons the process-global arbiter for every later test.**
In both failing runs the FIRST failure was `ASessionThatSurvivesItsStop_IsNotCountedAsARelease` (10.0 s /
7.6 s = the 5 s `WaitForSession` timeout + `Dispose` waiting up to `OpenWaitMs` for the still-unscheduled
open). That test's fixture is `SessionsSurviveTheStop = true`. When it threw, its `using` disposed the
controller while the open was still queued; the open later landed stale, went through `ReleaseOrRetain`,
the fake reported it survived, and it was RETAINED in the static `CameraDeviceArbiter.StrandedPreviews`
with the fixture's constant `Pid = 24512` - permanently, because the line that would have made it killable
never ran. Every later test asserting an empty stranded report failed:
`Dispose_WithACleanStop_RetainsNothingAndUnregisters` and
`Dispose_WhoseCameraSurvivesTheStop_KeepsTheHolderAndRetainsTheSession`, both with
`Assert.DoesNotContain() Failure ... Pid = 24512`. That is the "3 CameraPreviewTests". Mutation M2 (disable
the teardown) reproduces this exact signature deterministically.

**Link 3 - the factory-to-publication gap (found by loop attempt 1).** `WaitForSession` returned when the
FACTORY had recorded a session, a few instructions before the controller PUBLISHED it. A pre-emption there
sends the next Stop/Dispose down the controller's "superseded while opening" door, whose (correct)
outcomes differ from the published-session outcomes the tests assert. Run 2's log shows it: `OpenSession:
... was superseded while opening` -> `outcome=StillHeld` -> `RETAINING ... PID 24512`, then the Dispose's
own `StopSession: outcome=NothingWasHeld ... openInFlight=True openWaitMs=3`. Pre-existing; the dedicated
thread made it easier to hit.

**Link 4 - a parallel class moves the process-wide holder count (found by loop attempt 3).**
`PresetEditorFitsWithoutScrollingTests` builds `PresetEditor` windows (never shown, no camera, no ffmpeg),
and `PresetEditor`'s constructor creates a `CameraPreviewController`, which registers with the static
arbiter. Run 7's log shows `[PresetEditor] LoadOverlayFrom` interleaved with `Register: 2 camera
holder(s)` while `Dispose_WhileTheCameraIsStillBeingReleased_KeepsTheHolderRegistered` was reading the
count (`Expected: 1, Actual: 2`). Every test here that COUNTED holders was exposed to that class; the
claim that CameraPreviewTests was the arbiter's only test-time user (mine, in an earlier draft of this
note, and repeated by the code review) was wrong.

## 2. What changed

### 2.1 Product

`src/AgentEyes.App/CameraPreviewController.cs`
- The open runs on a dedicated background thread (`new Thread(...) { IsBackground = true, Name =
  "AgentEyes camera preview open" }`) instead of `Task.Run`. `_opening` stays a `Task` (a
  `TaskCompletionSource` completed in `OpenSession`'s `finally`, after `_unresolvedOpens` is cleared, so the
  stop path sees the ordering it always saw). The stop path is untouched. Why a product fix: `StopSession`
  waits at most 5 s for an in-flight open and then, correctly, refuses to call the camera free - so an open
  that had not even been scheduled turned a queue delay into "the camera may STILL BE HELD" on a recording
  start; a scheduling delay read as a device state (issue #35 defect 3's shape). The repo already puts
  blocking workers on their own threads at 15 sites, and the IL delegate-handoff walker recognizes exactly
  the `new Thread(delegate)` shape, so the HUD responsiveness guards are unaffected (green in every run).
- `Announce(Starting)` is raised BEFORE the thread starts (code review finding): a factory failing at once
  could otherwise announce Failed first and have it buried under Select's Starting - a pane stuck on
  "Starting camera..." with `HoldsCamera` true. `_opening` is set after the announcement, so a throwing
  subscriber cannot leave an open on record that no thread completes.
- The failure report inside the thread body's catch is itself guarded (code review finding): on a dedicated
  thread an escaping exception ends the process, where a faulted Task was merely unobserved.
- New `public bool OpenInFlight` (the fact `StopSession` waits on; closes link 3) and
  `public bool IsRegisteredWithArbiter` (asked by identity; closes link 4). Both observable on purpose,
  like `IsDisposed`.

`src/AgentEyes.Core/Video/CameraDeviceArbiter.cs`
- `public static bool IsRegistered(Func<string, bool> holder)` - whether THIS holder will be asked on the
  next recording start. `HolderCount` stays for diagnostics.

### 2.2 Tests - `tests/AgentEyes.Tests/CameraPreviewTests.cs` (33 -> 37 tests)

- `FakeCameraFactory` is `IDisposable`. Its teardown makes every fake it handed out killable, clears the
  gates, and calls `CameraDeviceArbiter.ReleaseForRecording("CameraPreviewTests teardown")`, which runs
  `StrandedPreviews.Recover()` (reaps rows no longer abandoned) and asks each holder to release (a disposed
  holder then unregisters). Every test declares `using var factory` BEFORE `using var preview`. Idempotent.
  The request also reaches the parallel PresetEditors' controllers; they hold nothing and answer false.
- `WaitForSession(factory, preview, index)` requires BOTH the factory's record AND `!preview.OpenInFlight`.
  Still 5 s, still throws.
- No test counts holders any more; five ask `preview.IsRegisteredWithArbiter`.
- The one test-side `Task.Run` is a dedicated `Thread` whose body captures its exception for an
  `Assert.Null` (a raw thread exception would take the test host down - code review finding).
- Four new tests, each with its known-bad arm: the open runs off the pool (M1); the teardown recovers a
  leaked surviving session, asserting the poisoned state FIRST (M2 - reproduces the wild signature); the
  publication gap reads as an open in flight and closes when it lands (M4); a factory that fails at once ends
  Failed, never stuck on Starting (M5 - a race the test cannot force; it fired on 1 of 3 mutant runs and is
  reported as such).

### 2.3 Second, related fix (small and mechanical) - readers of the shared run log

Separate root cause, fixed here because it is nine mechanical lines and QA on #79 asked for it. `Log.Write`
appends via `File.AppendAllText` (Write access, Read sharing); `File.ReadAllText` opens with Read sharing
too; they refuse each other in both directions (a reader mid-append throws "being used by another process"
- the #77 `AlwaysOnAppTests.FromJson_V111Config...` failure; a writer during a read gets an IOException that
`Log.Write` swallows - a LOST line).
- `TestRunIsolation.ReadLog()` / `ReadShared(path)` open with `FileShare.ReadWrite | FileShare.Delete`.
- The 8 `File.ReadAllText(Log.CurrentFile)` sites (AlwaysOnAppTests x1, AlwaysOnEngineTests x3,
  AlwaysOnHistoryTests x4) call it. The four offset readers (RecordingStopSequenceTests, AlwaysOnStallTests,
  PreviewChoresTests, HudPreviewSizingOrderTests) already used this sharing mode and are unchanged.
- `TestIsolationTests.ReadShared_WhileTheLoggerHoldsTheFileForAppend_Reads_WhereReadAllTextIsRefused`
  proves both arms on a probe file (by HResult `0x80070020`, not localized text - code review finding).

NOT changed (follow-up): the WRITER side of `Log.Write`. Two processes appending to one day file (the app
and the CLI) can still lose a line. `FileMode.Append` is not an atomic append across processes in .NET, so
admitting concurrent appenders trades a dropped line for possible interleaving; that is a logging design
change with its own proof, not a mechanical fix for a `[Tests]` issue.

### 2.4 `scripts/test-loop.ps1` (new)

Runs `dotnet test AgentEyes.sln -c Release` N times, each a fresh test-host process, quotes each run's
result line, every failed test with its message and first in-test stack frame, and the test host's per-run
folder (`run-yyyyMMdd-HHmmss-<pid>`: a new pid per run). Builds once first. Exit 0 only when every run
passed; a run with no result line counts as a failure. Silent and app-free. `-StartAt`/`-Append` split a
long loop across invocations without renumbering; `-Out` is resolved once against the caller's directory;
no TRX is written (code review findings).

## 3. The proof (acceptance criterion 2)

**Final tree: `test-loop-20-runs.txt` - 20 of 20 runs `Passed! - Failed: 0, Passed: 1917, Skipped: 0,
Total: 1917`, numbered 1..20, 20 distinct test-host pids** (`run-20260924-181332-28632` ...
`run-20260924-183201-10880`), 18:13-18:32 on 2026-09-24, 46-59 s per run, three `RESULT ... 0 failed`
lines (7 + 7 + 6).

Produced in three back-to-back invocations (runs 1-7 with the build, 8-14, 15-20) because one invocation
is capped at ten minutes in this session; each run is its own process regardless.

**The three attempts before it are committed and each found something:**
- Attempt 1 (4 of 7): runs 2, 5 failed the new teardown test (14/46 ms), run 7 failed the pre-existing
  `AStopThatThrows_...` (97 ms) - link 3. Fixed by `OpenInFlight` + the two-part `WaitForSession`.
- Attempt 2 (20 of 20): green on the tree before the code review; superseded because the review's fixes
  changed product code and a proof must be on the committed tree.
- Attempt 3 (6 of 7): run 7 failed `Dispose_WhileTheCameraIsStillBeingReleased_KeepsTheHolderRegistered`
  (`Expected: 1, Actual: 2`, 30 s - the assert fired before the gate was set, so the blocked Stop waited out
  its 30 s cap) - link 4. Fixed by asking the arbiter by identity.

No test is skipped, retried, or given a longer timeout (criterion 3): `WaitForSession` is still 5 s and
still throws; `SpinUntil` bounds are unchanged; `git diff main -- tests/AgentEyes.Tests/CameraPreviewTests.cs`
has no `Skip`, no retry loop, no raised timeout.

## 4. Code review of this diff (the skill's self-review step)

`/code-review` at effort high returned nine findings. Fixed: the raw-thread exception escape (guarded
report), `Announce(Starting)` ordering, the localized sharing-violation text (HResult), the test thread's
exception capture, the script's relative `-Out` and TRX accumulation, the overclaiming helper comment and
its pointless wrapper. Moot: "the proof shows only 18 runs" - the review read the file while chunk 3 was
still writing it. Deferred with reasons: the writer side of `Log.Write` (section 2.3); routing the four
offset readers through the helper (a refactor, not a fix). The review also repeated the "only test-time
user" claim that attempt 3 disproved - section 1, link 4.

## 5. Acceptance criteria -> how each is met -> how QA verifies

| Criterion | How the change satisfies it | How QA verifies |
|-----------|-----------------------------|-----------------|
| Root cause stated in the PR | Section 1 and the PR body: pool starvation of a `Task.Run` open + a surviving fake left in the process-global `StrandedPreviews` by the aborted test + the publication gap + a parallel class moving the holder count | Read section 1 against the code and the quoted log/TRX facts; re-run M2 from `mutation-evidence.txt` |
| 20 consecutive full-suite runs green, fresh process each, output in the PR | `test-loop-20-runs.txt` | Read it; optionally `scripts\test-loop.ps1 -Runs 3` (about 3 minutes, silent). Known-bad: attempts 1 and 3 are this script exiting 1 and naming the failure |
| No skip / retry / longer timeout | Section 3, last paragraph | `git diff main -- tests/AgentEyes.Tests/CameraPreviewTests.cs`: search `Skip`, `Retry`, `5000` |
| `dotnet build` clean, `dotnet test` green | `Build succeeded.` `0 Error(s)` (warnings pre-existing, none in touched files); every run `Failed: 0` | Run both; ~50 s |

## 6. What this does NOT prove (stated limits)

- One machine (8 logical CPUs, Windows 11). The mechanism is scheduling; a slower runner makes the OLD code
  fail more, not the new code, but `release.yml`'s runner has not run this tree.
- The dedicated thread and `OpenInFlight` are proven with the fake session; no real camera was opened
  (forbidden for this issue and needed by no criterion). The tester's live checks cover the running app.
- The M5 race cannot be forced by a test; the fix is by construction and the test pins the end state.
- The shared-log fix covers the TEST readers only (section 2.3).

## 7. CenCon impact

No drift: no component-map change, no privacy-posture change. The `TestIsolationCollection` comment
(TestIsolationTests.cs) still explains its non-parallel collection by the CameraPreviewTests symptom; that
reason is now historical (the class is still heavy and still worth running alone). Left untouched so the
proven tree is exactly what is committed; a one-line comment follow-up.

## 8. For QA - scope of checks

- No REST / UIA / running-app surface changed. `dotnet build` + `dotnet test` is the whole gate; no heavy
  smoke is warranted.
- Reminders carried per the method: the focus-free layers are REST / UIA / PrintWindow; never
  force-foreground and synthesize input without warning the human; the recording HUD is capture-excluded.
- The developer did NOT launch AgentEyesApp.exe or agenteyes.exe, run any smoke, selftest, install, release
  or tag - per the owner's brief for this issue.
