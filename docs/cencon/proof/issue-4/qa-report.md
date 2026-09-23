# QA report - issue #4 (PR #27)

**Issue:** [App] The desktop Library claims a recording is transcribed when only legacy flat
text exists
**Branch / tip:** `issue-4-library-transcribed-claim` @ `af63f79`
**Verified:** 2026-08-21, independently (fresh context; the developer's handoff was used as a
map, never as evidence)
**Verdict: VERIFIED - all acceptance criteria met.** Handed to the Review Gate
(`flow:ready-gate`, per D7 QA does not merge).

---

## Gate (run by QA itself)

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)` (2 pre-existing
  xUnit1031 warnings in PostRecordingQueueTests.cs, untouched by this branch).
- `dotnet test AgentEyes.sln -c Release` ->
  `Passed! - Failed: 0, Passed: 843, Skipped: 0, Total: 843, Duration: 14 s`.
- Machine note (honest limit): this machine has x64 WindowsDesktop runtimes 6.x and 10.x but
  no 8.x, so both the test host and the app itself need `DOTNET_ROLL_FORWARD=LatestMajor` to
  start. The suite therefore ran on the 10.x desktop runtime, not 8.x. This matches the
  developer's machine note and is a fact of this machine, not of the change.

## Criterion-by-criterion (Expected vs Actual)

### 1. Card indicator driven by the canonical predicate; test fails if swapped back - PASS

- Expected: the Library card's Transcript chip derives from the canonical predicate, with a
  test that FAILS when reverted to `File.Exists(transcript.txt)`.
- Actual: `src/AgentEyes.App/MainWindow.xaml.cs:2381-2386` - `TranscriptStatus.Classify(dir,
  manifest)` drives `TranscriptChipVisibility` (Transcribed only) and the new
  `FlatTextChipVisibility` (FlatTextOnly only). The classification sits outside the manifest
  try/catch with a null-manifest fallback (`MainWindow.xaml.cs:2290,2377`), and `AdoptFrom`
  carries the new property (`MainWindow.xaml.cs:2446`).
- Mutation drill (run by QA, three arms stated): reverted the chip line to
  `File.Exists(Path.Combine(dir, "transcript.txt"))` and ran
  `dotnet test -c Release --filter TranscriptPresenceTests`.
  - Expected on the mutant: failures. Actual: `Failed: 2, Passed: 15`
    (`LibraryCard_FlatTextOnly_DoesNotShowTranscriptChip` with
    `Expected: Not Visible / Actual: Visible`, plus
    `LibraryCard_CorruptManifest_StillClassifiesFromDisk`).
  - Reverted the mutation; full suite green again (`Failed: 0, Passed: 843`). Working tree
    confirmed clean afterwards.

### 2. Detail window's decision driven by the canonical predicate - PASS

- Expected: `_hasTranscript` (flat-text length) no longer decides; a test fails if the claim
  is reverted to length.
- Actual: `_hasTranscript` is deleted (`src/AgentEyes.App/RecordingDetailWindow.cs:238-240`
  is now a comment); every decision lives in the extracted, testable
  `src/AgentEyes.App/TranscriptPresentation.cs` (`HasTranscript => Kind ==
  TranscriptKind.Transcribed`, line 25). The window renders the presentation and decides
  nothing (`RecordingDetailWindow.cs:159-205`). The extraction is the issue's own flagged
  fallback for the WPF-Application test impracticality.
- Mutation drill: changed `HasTranscript` to `Text.Length > 0` and ran the filtered suite.
  - Expected on the mutant: failure. Actual: `Failed: 1, Passed: 16`
    (`DetailPresentation_FlatTextOnly_MakesNoTranscriptClaim`).
  - Reverted; suite green again.

### 3. No remaining ad-hoc presence decision in src/AgentEyes.App - PASS

QA re-ran its OWN search (not the developer's), `grep -rni` over `src/AgentEyes.App`
excluding `obj/`, four patterns:

1. `transcript\.(txt|json)` (`--include=*.cs --include=*.xaml`): 8 hits, all
   comments/doc-comments (MainWindow.xaml.cs 2043/2049/2383, Plugins.cs 19,
   RestServer.cs 219-220, TranscriptPresentation.cs 13/27). No code decision.
2. `File\.Exists.*[Tt]ranscript|[Tt]ranscript.*File\.Exists`: 1 hit, TestPanel.xaml.cs:311 -
   verified in context (lines 300-320): it checks the just-recorded AUDIO file exists before
   running a live mic-test transcription; not a transcript-presence claim.
3. `hastranscript` (case-insensitive): RecordingDetailWindow.cs:239 (comment), RestServer.cs
   218/413 (serializing RecordingLibrary's canonical flags), TranscriptPresentation.cs:25
   (the canonical derivation itself).
4. `transcript ... .Length`: RecordingDetailWindow.cs:172 is `item.Status.Length` (progress
   text, not presence); RestServer.cs:239 is URL-segment parsing. The two
   `presentation.Text.Length > 0` uses in RecordingDetailWindow choose text vs placeholder
   rendering of the canonical presentation's text - no presence claim.

A fifth broad sweep (`transcrib|transcript` over all remaining App files) returned only
comments, UI strings, and pipeline-orchestration text. Result: no remaining decision about
transcript presence in `src/AgentEyes.App` uses file existence or flat-text length. The
developer's recorded search (handoff section 2, criterion 3) matches what QA found.

### 4. Runtime: fixture cards show the truth - PASS

Fixtures created by QA under `%USERPROFILE%\Videos\AgentEyes\` (no media files, so the
repair pass ignores them; both deleted after the run):

- `2026-08-21_090000_flatonly`: manifest + `transcript.txt` ("legacy flat words") only.
- `2026-08-21_091000_transcribed`: manifest naming `transcript.json`, a 2-segment
  `transcript.json`, and `transcript.txt` ("hello world").

App launched from the x64 build output
(`src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe`). Deviation
from the tray-first instruction, stated openly: `--tray` never builds a MainWindow at all
(App.xaml.cs:70-71), so the Library cards cannot exist in tray mode; QA launched windowed
once (the gui-smoke pattern), dismissed the startup DevThrottle sign-in dialog via UIA
Invoke on its Cancel button, and drove everything after launch via REST + UIA + PrintWindow
only - no forced foregrounding, no synthesized mouse/keyboard input.

REST (`GET /recordings?limit=100`, quoted actual output):

- flatonly row: `hasTranscript : False`, `hasFlatTranscript : True` (old behavior would have
  been True/absent - the defect).
- transcribed row: `hasTranscript : True`, `hasFlatTranscript : True`.
- `GET /recordings/{id}` details: same values respectively.

UIA read of the Library (view selected programmatically via the "Library view" rail item):

- `2026-08-21_091000_transcribed` card: TranscriptChip=True, TextFileChip=False.
- `2026-08-21_090000_flatonly` card: TranscriptChip=False, TextFileChip=True.

Screenshot (PrintWindow, read by QA - both cards render clearly, no blank areas):
`qa-library-both-cards.png` - the transcribed card carries the "Transcript" chip, the
flatonly card carries the quieter italic "Text file" chip.

### 5. Flat text stays readable and copyable - PASS

- Code: the "Text file" chip shares `TranscriptChip_Click` -> `OpenDetail`
  (MainWindow.xaml:174-184, MainWindow.xaml.cs:1520-1523); Copy follows
  `presentation.CanCopy` (text exists), not the transcribed claim
  (RecordingDetailWindow.cs:213-215, TranscriptPresentation.cs:39).
- Runtime: QA invoked the flatonly card's "Text file" chip via UIA. The "Recording details"
  window opened; UIA text dump (quoted): caption
  `Not transcribed - showing the text file saved with this recording.`, body edit box
  `legacy flat words`, buttons `... | Copy transcript | Open folder | Delete`. Screenshot
  `qa-detail-flatonly.png` (read by QA - caption, text, and Copy transcript all visible).
- Contrast check on the transcribed card's detail: no legacy caption present, body shows
  `hello world`, Copy transcript present.
- REST access is also preserved: `GET /recordings/2026-08-21_090000_flatonly/transcript` ->
  `HTTP 200`, `{ "text": "legacy flat words", "segments": [] }` - `hasTranscript=false` no
  longer implies a 404, exactly as the updated Python client docstrings say.

### 6. Build + tests - PASS

See Gate above: `0 Error(s)`, `Failed: 0, Passed: 843` (17 new in
`tests/AgentEyes.Tests/TranscriptPresenceTests.cs`, counted by QA in the file: 5 Classify,
4 LibraryCard, 6 DetailPresentation, 2 RecordingLibrary API-row tests).

## Scope and no-target verification (searched, not trusted)

- The developer's carry-over note is TRUE: on `main`, `RecordingLibrary.HasTranscript`
  (main:src/AgentEyes.Core/RecordingLibrary.cs:279-283) counts `transcript.txt` as a
  transcript, and `git grep HasFlatTranscript main -- src clients` returns nothing - while
  the same grep on this branch returns 5 hits (the instrument fires on the branch, so the
  empty main result is a real absence, not a broken check). The archived repo's #156 REST
  split never landed here; this branch introduces the predicate AND rewires REST + both UI
  surfaces, which the issue's Affected Projects line explicitly allows.
- #15 boundary honored and honestly stated: the predicate is EXISTENCE of the
  manifest-named transcript.json; parse-based completion (zero-byte/corrupt JSON still
  classifies Transcribed) is deliberately left to #15, documented in
  TranscriptStatus.cs:36-39, the HasTranscript doc comment, and handoff sections 0 and 6.
- Escalated-issue scope untouched: `tests/AgentEyes.Tests/CompiledCode.cs` is not in the
  diff (10 files changed, listed via `git diff main...HEAD --name-only`); branches
  `issue-2-library-verification-followups` and `issue-9-smoke-x64-paths` are untouched.
- Privacy posture: no change to visibility/control surfaces. ASCII-only holds in all new
  code, strings, and this proof set.
- Shutdown: `/status` showed `"State": "idle"` before stop; after stopping the app, no
  AgentEyesApp or ffmpeg processes remained and no `%TEMP%\AgentEyes-crash.log` existed.
  Fixtures deleted; the user's six pre-existing recordings are intact.

## Evidence files

- `docs/cencon/proof/issue-4/qa-library-both-cards.png` - Library view, both fixture cards.
- `docs/cencon/proof/issue-4/qa-detail-flatonly.png` - flatonly detail: caption, text, Copy.

**VERIFIED - all acceptance criteria met.** -> `flow:ready-gate`.

---

# Round 2 - the review gate's two blocking defects, re-verified by QA

**Branch / tip:** `issue-4-library-transcribed-claim` @ `9a40dd2` (round-2 fix commit on top of
round-1 tip `2c509b2`)
**Verified:** 2026-08-21, fresh QA context; the developer's round-2 handoff (section 7) was used
as a map, never as evidence.
**Verdict: VERIFIED - both gate defects fixed, round-1 criteria still hold.** Handed to the
Review Gate (`flow:ready-gate`, per D7 QA does not merge).

## Gate (run by QA itself)

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)` (same 2
  pre-existing xUnit1031 warnings, untouched).
- `dotnet test AgentEyes.sln -c Release` ->
  `Passed! - Failed: 0, Passed: 850, Skipped: 0, Total: 850, Duration: 14 s`
  (843 in round 1 + the 7 new round-2 tests in `TranscriptPresenceTests`).
- Same machine limit as round 1, stated honestly: no x64 WindowsDesktop 8.x runtime here, so
  the test host and the app ran with `DOTNET_ROLL_FORWARD=LatestMajor` on the 10.x desktop
  runtime. A first run WITHOUT the roll-forward aborts with the framework-resolution error -
  the instrument fires on the known-bad configuration, so the green run is a real run.

## Scope of the round-2 diff (2c509b2..9a40dd2) - reviewed, in scope

Exactly five files: `docs/cencon/proof/issue-4/handoff.md` (handoff round-2 section),
`src/AgentEyes.App/LibraryCoherence.cs` (+RefreshArtifactChips bulk route),
`src/AgentEyes.App/MainWindow.xaml.cs` (RecentItem.RefreshArtifactChips + _manifest carry +
Rail_Checked wiring), `src/AgentEyes.App/RecordingDetailWindow.cs` (async transcript load),
`tests/AgentEyes.Tests/TranscriptPresenceTests.cs` (7 round-2 tests). Nothing else changed;
`TranscriptPresentation`/`TranscriptStatus`/REST are untouched, so every round-1 criterion is
re-covered by the same (green) tests. Review findings: the refresh deliberately claims no
epoch and touches no membership (LibraryCoherence.cs:432-436 comment verified against the
issue #3 model); `AdoptFrom` carries `_manifest` so a reload cannot resurrect a stale manifest
(MainWindow.xaml.cs:2493-2496); the detail window's visual tree is built once and the async
load only fills values in; the `Loaded` handler is the entry point and holds the try-catch
with `Log.Error` - errors are logged and degrade to the empty state, not swallowed
(RecordingDetailWindow.cs:248,269-297). CLAUDE.md standards hold (responsive UI, entry-point
catch, enterprise logging, ASCII).

## Gate defect 1 - card chips diverge from disk until an unrelated reload: FIXED, proven at runtime

Fixtures built by QA under `%USERPROFILE%\Videos\AgentEyes\` (deleted after the run; the
user's six recordings untouched):

- `2026-08-21_110000_qa2div`: manifest naming `transcript.json`, a 2-segment
  `transcript.json`, and a flat `transcript.txt`.
- `2026-08-21_111000_qa2big`: manifest + JSON-ONLY transcript (no `transcript.txt`).

App launched windowed from the x64 Release output (same documented deviation as round 1:
`--tray` never builds a MainWindow, so Library cards cannot exist in tray mode); the
DevThrottle sign-in dialog dismissed via UIA Invoke on Cancel; everything after launch driven
by REST + UIA + PrintWindow, no foregrounding, no synthesized input.

External-DELETE drill (quoted actual output):

1. Baseline agrees everywhere: REST `hasTranscript=True hasFlatTranscript=True`; UIA card
   `TranscriptChip=True TextFileChip=False`.
2. Deleted `qa2div\transcript.json` on disk while the Library was showing.
   REST immediately: `hasTranscript=False hasFlatTranscript=True`. Card pre-navigation, as
   designed, still stale: `TranscriptChip=True` - this IS the gate's divergence window, and
   the fix closes it at the next show.
3. Rail away (Record view) and back (Library view) via UIA SelectionItem.Select. Card:
   `TranscriptChip=False TextFileChip=True` - it agrees with REST again. Expected: chip
   re-derived to the flat-text state. Actual: exactly that.
   Screenshot `qa2-divergence-after-reshow.png` (read by QA: the qa2div card carries the
   quieter italic "Text file" chip; the JSON-only qa2big card next to it keeps "Transcript").
   App log, quoted: `[LibraryCoherence] RefreshArtifactChips: 8 row(s) re-derived from disk`
   on every Library show (three occurrences during the drills).

External-CREATE drill (the inverse):

4. Wrote `transcript.json` back into qa2div. REST immediately `hasTranscript=True`; card
   pre-navigation still `TextFileChip=True` (stale, expected); after rail away+back:
   `TranscriptChip=True TextFileChip=False`. Screenshot
   `qa2-divergence-inverse-upgrade.png` (read by QA: both cards now carry "Transcript").

Mutation drill on the WIRING (three arms stated - run by QA):

- Commented out `if (library) _library.RefreshArtifactChips();` in `Rail_Checked`
  (MainWindow.xaml.cs:221) and ran
  `dotnet test -c Release --filter TranscriptPresenceTests`.
- Expected on the mutant: the IL wiring pin fails. Actual, quoted: `Failed: 1, Passed: 23` -
  exactly `RailNavigation_RefreshesTheLibrarysArtifactChips` (an empty/aborted run would have
  been a broken instrument, not a pass).
- Rehooked (git checkout of the file); filtered suite green again: `Failed: 0, Passed: 24`;
  working tree confirmed clean.

## Gate defect 2 - detail window read+deserialized the transcript on the constructor thread: FIXED, proven at runtime

Code: the constructor builds the transcript area with a dim "Loading transcript..." line, the
collapsed legacy caption, and the hidden Copy button; `Loaded += async` fires
`LoadTranscriptAsync`, which runs `TranscriptPresentation.For` inside `Task.Run` (verified in
the diff AND pinned from IL by `DetailWindow_TranscriptLoad_IsNotInvokedOnConstruction` -
fail-closed, the call must exist in `LoadTranscriptAsync` - and
`DetailWindow_TranscriptLoad_RunsOnABackgroundThread`). The catch is at the entry point and
logs via `Log.Error`, degrading to the empty state - not swallowed.

Runtime, large-fixture drill (fixture built by QA; timings from a stopwatch started at the
UIA Invoke of the card's Transcript chip; quoted actual output):

- First pass, 42 MB / 40,000-segment JSON-only transcript: the load completed in ~0.8 s
  (log bracket `LoadTranscriptAsync: ...` at 09:18:10.878 -> `kind=Transcribed
  chars=39999999` at 09:18:11.677), the window showed the full 39,999,999-char text and the
  Copy transcript button - too fast to probe interactivity mid-load, so QA refit the fixture.
- Second pass, 31 MB / 400,000-segment JSON-only transcript (deserialization-bound):
  - `t=5ms` chip invoked; `t=42ms` the `Recording details` window is VISIBLE (Win32
    EnumWindows, `IsHungAppWindow=False`).
  - While the load ran (~12 s), repeated UIA ValuePattern fetches of the transcript body were
    each answered by the WPF UI thread in 11-31 ms and read `Loading transcript...` - the
    window is interactive, not frozen (probes quoted in the drill record: t=47..72ms,
    202..225ms, 255..266ms, 308..339ms, 378..389ms, 417..431ms, ...).
    Screenshot `qa2-detail-loading.png` (read by QA: dim "Loading transcript...", actions
    row WITHOUT Copy transcript).
  - `t=12117ms`: the content rendered (8,688,889 chars) and `Copy transcript` appeared.
    Screenshot `qa2-detail-large-loaded.png` (read by QA: segment text visible, Copy
    transcript present in the actions row).
- Round-1 behavior preserved through the async path, spot-checked at runtime: a flat-only
  recording's detail still shows the caption
  `Not transcribed - showing the text file saved with this recording.`, the flat text
  (`[00:00:00] hello / [00:00:01] world`), and Copy transcript - quoted from the UIA dump.

Instrument note (honest limit): QA's first drill pass polled UIA RootElement children for the
dialog and missed it - the OWNED `Recording details` window did not surface there even though
its HWND existed and was responsive (verified via Win32 EnumWindows). The drill was re-run
finding the HWND at the Win32 layer and attaching with `AutomationElement.FromHandle`; the
earlier "window never appeared" was the instrument, not the app, and the app log plus the
Win32 probe prove the window was up.

## Shutdown and cleanup

`/status` quoted: `"State":"idle"` before stop; no ffmpeg process existed. App process
stopped; after stop: `AgentEyesApp: 0`, `ffmpeg: 0`, `%TEMP%\AgentEyes-crash.log` does not
exist. Both fixtures deleted; the six pre-existing recordings intact. Repo tree left clean.

## Round-2 evidence files

- `qa2-divergence-after-reshow.png` - Library after the external delete + reshow: qa2div
  carries "Text file", qa2big keeps "Transcript".
- `qa2-divergence-inverse-upgrade.png` - after the external create + reshow: both carry
  "Transcript".
- `qa2-detail-loading.png` - detail window open and interactive while the 31 MB transcript
  loads; no Copy button yet.
- `qa2-detail-large-loaded.png` - the same window with the content rendered and Copy
  transcript present.

**VERIFIED - both review-gate defects fixed with runtime proof; all round-1 criteria still
hold.** -> `flow:ready-gate`.

---

# Round 3 - the review gate's three UI-thread I/O defects, re-verified by QA

QA round 3, 2026-08-21, on PR #27 tip fc06c42 (branch issue-4-library-transcribed-claim),
fresh QA context, independent of the developer's report. Round-2 gate verdict: three blocking
defects, all synchronous I/O on the WPF thread - (1) an unbounded File.Exists chip sweep on
the UI thread when entering the Library, (2) a constructor-time manifest.json read+deserialize
in the detail window pre-show, (3) Log writes on the UI thread in the load continuations.
All three verified FIXED in the code and at runtime, the handoff's justified-remaining-probes
list (section 8.4) audited line by line, and all rounds 1-2 criteria re-verified.

## Gate (run by QA itself)

- dotnet build AgentEyes.sln -c Release: Build succeeded., 0 Error(s) (2 pre-existing
  xUnit1031 warnings in PostRecordingQueueTests.cs, untouched by this branch).
- dotnet test AgentEyes.sln -c Release, quoted actual output:
  "Passed!  - Failed: 0, Passed: 855, Skipped: 0, Total: 855, Duration: 14 s".
- Machine note (same fact the handoff records): the x64 host on this machine now carries
  WindowsDesktop runtimes 10.0.x but no 8.0.x, so both the test host and the app itself need
  DOTNET_ROLL_FORWARD=LatestMajor. Without it the test run ABORTS (QA hit exactly that
  first; an aborted run is a broken instrument, not a pass) - with it, 855/855. This is a
  runtime-resolution fact of the machine, not of this change.

## Scope of the round-3 diff (d45b8bb..fc06c42) - reviewed, in scope

Five files: src/AgentEyes.App/LibraryCoherence.cs, src/AgentEyes.App/MainWindow.xaml.cs,
src/AgentEyes.App/RecordingDetailWindow.cs, tests/AgentEyes.Tests/TranscriptPresenceTests.cs,
docs/cencon/proof/issue-4/handoff.md. Nothing outside the issue's presentation-layer scope;
no REST shape change, no privacy-posture change, no Core change. Working tree clean at checkout.

## Gate defect 1 - unbounded UI-thread chip sweep entering the Library: FIXED

Code (file:line evidence):

- src/AgentEyes.App/LibraryCoherence.cs:459 RefreshArtifactChipsAsync replaces the
  synchronous sweep: the owning thread snapshots each row's probe inputs with no I/O, the
  File.Exists probes AND their count log line run inside Task.Run (the ChipSweepProbing
  test seam at :475 sits inside that worker body, the Log.Info at :479), and the awaiter
  dispatches back to the calling context where results are applied as property writes only.
  Coherence semantics preserved: still NO epoch and NO fact update; rows are updated in
  place, never replaced, so a captured row IS the visible row and a row deleted mid-flight
  is inert.
- src/AgentEyes.App/MainWindow.xaml.cs:227 Rail_Checked now fires RefreshLibraryChips
  (:247), an async-void entry point with the try-catch at the entry point (CLAUDE.md rule 4)
  whose failure log is itself written from a worker (:256).
- src/AgentEyes.App/MainWindow.xaml.cs:2452-2504 RecentItem splits the probe:
  CaptureChipProbe (no I/O, UI thread) / ChipProbe.Run (the probes, any thread) /
  ApplyArtifactChips (property writes only). The synchronous RefreshArtifactChips
  remains only for the off-UI-thread snapshot worker (From) and the bounded single-row
  Insert route.

Tests (QA read each, then ran the suite):

- LibraryChipSweep_ProbesOnAWorkerWhileTheOwningThreadIsFree
  (tests/AgentEyes.Tests/TranscriptPresenceTests.cs:494) - the deterministic proof: HOLDS
  the worker at the probe point via the seam and asserts the call already returned to the
  owning thread, the chips still show pre-sweep values, and the probing thread differs from
  the owner; releasing the worker lands the re-derived values (the external-delete
  direction). This is the load-bearing stall-impossibility proof.
- LibraryChipSweep_HandsTheProbesToAWorker (:538) - IL pin: Task::Run inside
  RefreshArtifactChipsAsync.
- RailNavigation_RefreshesTheLibrarysArtifactChips (:423) - both wiring hops, fail-closed
  (CallsIn throws on a renamed method; the CallSites leg is a positive Contains).

Runtime (fixtures built by QA under %USERPROFILE%\Videos\AgentEyes\: qa3flat flat-text
only, qa3div transcribed, qa3big 33 MB JSON-only 400,000-segment transcript, plus 300
bulk fixture rows qa3bulk000..299 to make the sweep unbounded-ish; all deleted after the
run; the user's six recordings untouched. App launched windowed from the x64 Release output -
same documented deviation as rounds 1 and 2: --tray never builds a MainWindow so Library
cards cannot exist in tray mode; the DevThrottle sign-in dialog dismissed via UIA Invoke on
Cancel; everything after launch driven by REST + UIA + PrintWindow, no forced foregrounding,
no synthesized input):

- Library entry with 309 rows: the UIA SelectionItem.Select on "Library view" returned in
  14 ms (quoted: "select returned at 14ms"), and the worker's count line landed in the app
  log 29 ms after the select was issued, quoted:
  "09:55:23.182 [INFO] [LibraryCoherence] RefreshArtifactChipsAsync: 309 row(s) re-derived
  from disk on a worker" - the worker route IS the one that runs on rail navigation, with
  an unbounded row count, and the rail handler returns before it completes. Honest limit,
  stated: on this machine's local SSD the 309-row probe sweep finishes in ~15 ms, so QA
  could not produce a HUMAN-VISIBLE stall-vs-paint separation at runtime; a true slow-storage
  repro was not simulated. The deterministic held-worker seam test above is the load-bearing
  proof that the paint cannot wait on the probes, and QA verified that test's assertions by
  reading it and running it (it is one of the 855 green).
- Round-2 divergence drill re-run against the ASYNC sweep, both directions, quoted:
  external DELETE of qa3div transcript.json -> REST immediately
  "hasTranscript=False hasFlatTranscript=True"; card pre-navigation still stale
  "TranscriptChip=True" (the designed show-time window); rail away+back ->
  "TranscriptChip=False TextFileChip=True". External CREATE (inverse) -> REST immediately
  "hasTranscript=True"; card stale "TextFileChip=True"; rail away+back ->
  "TranscriptChip=True TextFileChip=False". Three sweep log lines total, all
  "...on a worker", one per Library show. Coherence semantics survived the async rework.

Mutation drill for this defect class was run by QA on the DETAIL ctor pin (below); the
developer's drills (a) and (b) on the sweep wiring were not re-run - the two wiring/Task.Run
pins are fail-closed by construction (CallsIn throws on absence; the CallSites legs assert
presence) and QA verified their assertions by reading the IL helper (CompiledCode.Fold,
tests/AgentEyes.Tests/CompiledCode.cs:508, correctly folds ctor-lambda / display-class /
state-machine names back to the declaring method).

## Gate defect 2 - detail ctor read+deserialized manifest.json pre-show: FIXED

Code: the constructor (src/AgentEyes.App/RecordingDetailWindow.cs:56-244) now touches no
disk at all - no Manifest.Load, no DevThrottleAccount.IsSignedIn (credential read +
DPAPI decrypt, found by the developer's sweep), no walkthrough File.Exists (the button
label reads the card's already-derived chip at :208-215). The summary TextBlock and sign-in
banner are built collapsed; LoadDetailsAsync (:262) reads manifest + account state +
transcript in ONE Task.Run body after Loaded (:243) and applies values on the UI thread.
The Open-folder lambda was extracted to OpenFolder() (:223, method at :327) so the ctor
pin can ban ALL File/Directory calls from construction.

IL pin verified to actually pin the ctor, then MUTATION-DRILLED by QA:

- DetailWindowConstructor_TouchesNoDiskAndWritesNoLog (TranscriptPresenceTests.cs:580)
  first asserts the scan SEES RecordingDetailWindow::.ctor (fail-closed), then asserts no
  call site in the ctor (including folded lambdas) targets System.IO.File::*,
  System.IO.Directory::*, Manifest::Load, get_IsSignedIn, or AgentEyes.Log::*.
- QA's mutation drill, three arms stated: reintroduced a synchronous
  File.Exists(Path.Combine(item.Dir, "manifest.json")) into the ctor, rebuilt, ran the
  filtered suite. Expected: the pin fails naming the ctor. Actual, quoted:
  "Failed: 1, Passed: 28" with
  "Collection: [CallSite { Assembly = AgentEyesApp.dll, Method =
  AgentEyes.App.RecordingDetailWindow::.ctor, Callee = System.IO.File::Exists }]".
  (An empty or aborted run would have been a broken instrument, not a red.) Reverted via
  git checkout; filtered suite green again: "Failed: 0, Passed: 29"; tree clean.
- DetailWindow_ManifestRead_IsNotInvokedOnConstruction (:560) additionally requires the
  manifest read to EXIST in LoadDetailsAsync (presence, not absence).

Runtime (quoted actual output):

- qa3big (33 MB JSON-only): detail window VISIBLE at t=11ms after the chip Invoke
  (Win32 EnumWindows; IsHungAppWindow=False). The worker load bracket in the app log:
  entry "09:57:38.773 ... LoadDetailsAsync: dir=...qa3big" -> result "09:57:39.429 ...
  kind=Transcribed chars=10688889 aiConfigured=False" - 0.66 s on the worker. UIA then read
  the full 10,688,889-char body from the transcript TextBox, with "Copy transcript" present
  and the sign-in banner correctly appearing only after the load (aiConfigured=False, no
  Description). Screenshot qa3-detail-large-loaded.png (read by QA: segment text, banner,
  full actions row - no blank areas).
- Honest notes, stated plainly: (1) the worker load was so fast (0.66 s vs round 2's 12 s -
  the 10.x runtime deserializes this fixture much faster) that QA could not sample the
  intermediate "Loading transcript..." state this round; its existence is held by the round-2
  runtime record, the unchanged ctor-built loading line, and the green pins. (2) After the
  worker load, applying the 10.7-million-char string to the visible TextBox cost the UI
  thread roughly 14 s of layout work (one UIA probe spanned t=681..15193ms) - the same class
  and magnitude as round 2's accepted 12.1 s render of an 8.7M-char fixture. That is CPU/
  text-layout on a deliberately pathological fixture (roughly 50x any real transcript), not
  disk I/O, and is outside this issue's criteria; noting it here so the gate sees it was
  measured, not missed.
- Walkthrough label read from the chip, verified at runtime: qa3big has no walkthrough.html
  and the actions row shows "Build walkthrough" (quoted in the UIA button dump).

## Gate defect 3 - load continuations logged on the WPF thread: FIXED

Code: every log on the load path is inside the Task.Run body
(RecordingDetailWindow.cs:268, :276, :283), written before the UI update is dispatched; the
catch path logs via await Task.Run(() => Log.Error(...)) (:316), as does CommitRename's
catch (:382) and RefreshLibraryChips' catch (MainWindow.xaml.cs:256); the chip sweep's
count line logs on its worker (LibraryCoherence.cs:479). The dispatcher hop applies values
only.

Test: DetailWindow_Logging_IsConfinedToTheWorkerBackedPaths (:610) - its limit stated IN
the test, exactly as method 6c item 6 requires: IL folding cannot distinguish a Log call
inside a method's Task.Run lambda from one after its await, so THAT placement is held by
code reading (QA re-read each call, listed above) and by this runtime pass; what the pin
holds fail-closed is that logging stays confined to LoadDetailsAsync + CommitRename and
that the load path really does log (presence). QA accepts this as an honestly documented
limit, not an overclaim.

Runtime: the entry/result lines quoted under defect 2 (and the flat fixture's pair:
"09:59:49.515" entry -> "09:59:49.517" "kind=FlatTextOnly chars=17 aiConfigured=False")
landed in the log while the windows showed content without waiting on them.

## Handoff section 8.4 audit - every justification checked against the code

QA read each entry and swept the touched files itself
(grep -n "File\.|Directory\.|Manifest.Load|IsSignedIn|Log\." over RecordingDetailWindow.cs,
LibraryCoherence.cs, and the touched MainWindow regions):

- OpenFolder (RecordingDetailWindow.cs:328 Directory.Exists) and
  WalkthroughChip_Click (MainWindow.xaml.cs:1563 File.Exists): one bounded probe on an
  explicit user click, never paint-critical; matches every other click handler in the app.
  JUSTIFIED. (TranscriptChip_Click performs no I/O at all.)
- RecentItem.From reached on the UI thread only via LibraryCoherence.Insert
  (LibraryCoherence.cs:291): the bounded ONE-row "a recording just appeared" route, which
  already read that row's whole manifest on main before this PR; the unbounded every-row
  sweep is what the defect named and what went off-thread. JUSTIFIED as pre-existing
  issue-#3 design, not a regression of this PR.
- LibraryCoherence's other Log.Info/Warn/Error calls (:161-:614): pre-existing issue-#3
  code outside this diff, one line per user action or per snapshot (event-scale, bounded),
  not a per-row sweep. JUSTIFIED.
- Rail_Checked's other branches (LoadDictionary, LoadCaptures, ...): other rail
  destinations, untouched by this diff, out of this issue's scope. JUSTIFIED.
- TranscriptPresentation.For: QA confirmed by grep its ONLY product caller is
  LoadDetailsAsync's Task.Run body (RecordingDetailWindow.cs:280). RecordingLibrary /
  RestServer serve REST on listener threads. JUSTIFIED.

QA's own sweep found NO UI-thread synchronous I/O on any touched path that is neither fixed
nor on the justified list.

## Rounds 1-2 criteria still hold (re-verified this round)

- Criterion 4 baseline at runtime, quoted: REST "qa3flat: hasTranscript=False
  hasFlatTranscript=True", "qa3div: hasTranscript=True hasFlatTranscript=True"; UIA cards
  "qa3flat: TranscriptChip=False TextFileChip=True", "qa3div/qa3big: TranscriptChip=True
  TextFileChip=False". Screenshot qa3-library-cards.png (read by QA: the two Transcript
  chips and the italic "Text file" chip all render).
- Criterion 5 at runtime: the flat card's "Text file" chip opened the detail; UIA dump
  quoted: caption "Not transcribed - showing the text file saved with this recording.",
  body "legacy flat words", "Copy transcript" present. Screenshot qa3-detail-flatonly.png
  (read by QA). REST transcript route unchanged.
- Criteria 1-3: the predicate tests and the criterion-3 search record are unchanged on this
  branch and green in the 855.
- Criterion 6: the gate above.

## Shutdown and cleanup

/status quoted before stop: "State":"idle" (PendingTranscriptions:4 refers to the user's
own pre-existing recordings - the fixtures carry no media so the backlog ignores them, as
designed). After stop: AgentEyesApp: 0, ffmpeg: 0, %TEMP%\AgentEyes-crash.log does not
exist. All 303 fixture folders deleted; the six pre-existing recordings verified intact by
listing. Repo tree left clean.

## Round-3 evidence files

- qa3-library-cards.png - the Library after all drills: qa3big and qa3div carry
  "Transcript", qa3flat carries the italic "Text file" chip, bulk rows behind them.
- qa3-detail-large-loaded.png - the 33 MB JSON-only detail fully loaded: segment text,
  sign-in banner (post-load), Copy transcript, chip-derived "Build walkthrough" label.
- qa3-detail-flatonly.png - the flat-only detail: legacy caption, readable text, Copy
  transcript.

**VERIFIED - all three round-2 gate defects fixed with code, test, mutation and runtime
proof; section 8.4 audited clean; all rounds 1-2 criteria still hold.** -> flow:ready-gate.
