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
