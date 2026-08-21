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
