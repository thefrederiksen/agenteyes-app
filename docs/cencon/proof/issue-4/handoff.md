# Issue #4 - Developer handoff to QA

**Issue:** [App] The desktop Library claims a recording is transcribed when only legacy flat
text exists
**Branch:** `issue-4-library-transcribed-claim`
**Status (round 2):** I believe this is finished. The review gate's two round-1 blocking
defects are fixed - see section 7 for what changed and how QA verifies each at runtime.
Build clean, `dotnet test` Failed: 0 (850 passed; 24 issue-#4 tests in
`TranscriptPresenceTests`, 7 of them new in round 2). Round-1 context below is unchanged.

---

## 0. A carry-over discrepancy QA must know about (verified, not assumed)

The issue body describes the archived repo's #156 as already landed: "the canonical completion
predicate [is] a PARSE of the transcript JSON, and the REST list/detail fields now use it,
exposing legacy flat text separately." **That change never reached this repository's `main`.**
Before this branch:

- `RecordingLibrary.HasTranscript` counted a flat `transcript.txt` as a transcript
  (old `src/AgentEyes.Core/RecordingLibrary.cs:279-284`), so even the REST API called a
  flat-text-only recording transcribed.
- No `HasFlatTranscript` existed anywhere in the solution (verified:
  `grep -rn "HasFlatTranscript" src tests` -> zero hits before this branch).

The two UI defects the issue cites DID both reproduce on `main` exactly as written:

- `src/AgentEyes.App/MainWindow.xaml.cs` (old line 2356): Transcript chip =
  `File.Exists(dir + "\transcript.txt")`.
- `src/AgentEyes.App/RecordingDetailWindow.cs` (old lines 150-168): `_hasTranscript` =
  flat-text LENGTH.

So this branch introduces the shared canonical predicate AND points the REST fields and both
UI surfaces at it. The issue's Affected Projects allows exactly this: "`AgentEyes.Core` (only
if the canonical predicate needs to be exposed to the App layer)" - it did.

**Scope boundary with issue #15 (deliberate):** the predicate here is EXISTENCE of the
manifest-named `transcript.json` (default name `transcript.json`) - the same artifact
`TranscriptionBacklog.NeedsTranscription` and the packager treat as completion. Judging that
artifact by PARSING it (zero-byte / truncated JSON) is issue #15's scope and is deliberately
NOT absorbed here. When #15 lands, it upgrades ONE place (`TranscriptStatus`) and every
surface follows.

## 1. What was implemented

| File | Change |
|------|--------|
| `src/AgentEyes.Core/TranscriptStatus.cs` (new) | The canonical predicate: `TranscriptKind { None, FlatTextOnly, Transcribed }`, `IsTranscribed(dir, manifest)` (manifest-named transcript.json exists), `HasFlatText(dir)` / `FlatTextPath(dir)`, `Classify(dir, manifest)`, and `JsonArtifactName(manifest)` - the ONE resolution of the artifact name, shared with `RecordingLibrary.ReadTranscript`. Internal, visible to the App and Tests. |
| `src/AgentEyes.Core/RecordingLibrary.cs` | `HasTranscript` now delegates to `TranscriptStatus.IsTranscribed` (transcript.txt no longer counts); new `HasFlatTranscript` on `Summary` and `Detail`; `GetTranscript`'s body extracted as `ReadTranscript(dir, manifest)` for the desktop; an unparseable/segment-less transcript.json is now `Log.Warn`ed (it used to be silently swallowed) and a JSON-null segment `Text` no longer NREs. |
| `src/AgentEyes.App/RestServer.cs` | `GET /recordings` items and `GET /recordings/{id}` now carry `hasFlatTranscript` next to the (now canonical) `hasTranscript`. |
| `src/AgentEyes.App/MainWindow.xaml.cs` | `RecentItem.From` sets the chips from `TranscriptStatus.Classify`: Transcript chip = `Transcribed` only; new `FlatTextChipVisibility` = `FlatTextOnly` only. The chip classification sits OUTSIDE the manifest try/catch (with a null manifest on failure), so a corrupt manifest cannot hide artifacts that are on disk. `AdoptFrom` carries the new property so in-place refresh (issue #3 model) cannot lose it. |
| `src/AgentEyes.App/MainWindow.xaml` | New quieter chip "Text file" (italic, muted foreground, plain stroke border - visually distinct from the Transcript chip) bound to `FlatTextChipVisibility`; tooltip "Not transcribed - a legacy text file exists. View text and details"; same click handler, so it opens the same detail view (access preserved). |
| `src/AgentEyes.App/TranscriptPresentation.cs` (new) | The detail window's transcript decisions extracted into a testable non-UI type (the issue's flagged assumption): `Kind`, `HasTranscript` (canonical claim), `Text` (the flat timestamped rendering when it has content - exactly what the window displayed before this issue - else the JSON text via `RecordingLibrary.ReadTranscript`), `CanCopy` (text exists - independent of the transcribed claim), `LegacyNotice` (FlatTextOnly AND text shown - a 0-byte legacy file gets no caption). |
| `src/AgentEyes.App/RecordingDetailWindow.cs` | The window renders `TranscriptPresentation` and decides nothing itself: `_hasTranscript` (flat-text length) is deleted; flat-text-only recordings show the text under a quiet italic caption "Not transcribed - showing the text file saved with this recording."; Copy transcript follows `CanCopy`, so flat text stays copyable. Both constructor catches (manifest load, presentation) log via `Log.Error`. |
| `clients/python/agenteyes_client/client.py` | Docstrings only: `recordings()` and `transcript()` now state that `hasTranscript` means transcription complete, that legacy flat-only recordings report `hasFlatTranscript=True`, and that `transcript()` still serves their text with 200. |
| `tests/AgentEyes.Tests/TranscriptPresenceTests.cs` (new) | 17 tests, see per-criterion mapping below. |

## 2. Acceptance criteria -> how each is met -> how QA verifies

### Criterion 1 - card indicator driven by the canonical predicate, fails if swapped back

- Implemented: `MainWindow.xaml.cs`, `RecentItem.From` - `TranscriptStatus.Classify(dir, m)`
  drives `TranscriptChipVisibility` (Transcribed only) and `FlatTextChipVisibility`
  (FlatTextOnly only).
- Tests: `LibraryCard_FlatTextOnly_DoesNotShowTranscriptChip`,
  `LibraryCard_TranscribedRecording_ShowsTranscriptChipOnly`,
  `LibraryCard_NoTranscript_ShowsNeitherChip`,
  `LibraryCard_CorruptManifest_StillClassifiesFromDisk`.
- Fails-if-reverted: the flat-only fixture HAS a `transcript.txt`, so reverting the chip to
  `File.Exists(transcript.txt)` makes it `Visible` and
  `Assert.NotEqual(Visibility.Visible, card.TranscriptChipVisibility)` fails. QA can prove
  this mechanically: in `RecentItem.From` replace the `transcriptKind` block with the old line
  `item.TranscriptChipVisibility = File.Exists(Path.Combine(dir, "transcript.txt")) ?
  Visibility.Visible : Visibility.Collapsed;`, run
  `dotnet test -c Release --filter TranscriptPresenceTests`, watch it fail, then revert.

### Criterion 2 - detail window's decision driven by the canonical predicate

- Implemented: the decision no longer lives in the window at all. `_hasTranscript` (flat-text
  length) is deleted; `TranscriptPresentation.HasTranscript` (canonical) replaces it, and the
  window renders the presentation. Constructing the WPF window in a unit test requires a live
  `Application` with the app's resource dictionary, which is exactly the "impractical as unit
  tests" case the issue flags - so the decision was extracted per the issue's stated fallback
  ("extract the decision into a testable non-UI type").
- Tests: `DetailPresentation_FlatTextOnly_MakesNoTranscriptClaim` (the flat fixture has text
  with length > 0, so reverting the claim to text length fails it),
  `DetailPresentation_TranscribedRecording_ClaimsTranscriptAndKeepsFlatRendering`,
  `DetailPresentation_TranscribedWithoutFlatFile_FallsBackToJsonText`,
  `DetailPresentation_EmptyFlatTextOnly_NoNoticeNoCopy`,
  `DetailPresentation_NoTranscript_NoClaimNoCopy`.
- The predicate itself is pinned by `Classify_FlatTextOnly_IsFlatTextOnlyNotTranscribed`,
  `Classify_TranscriptJson_IsTranscribed`, `Classify_ManifestNamedArtifact_IsHonored`,
  `Classify_NoArtifacts_IsNone`, `Classify_NullManifest_StillFindsDefaultNamesAndFlatText`.

### Criterion 3 - the recorded search (run on the finished branch, 2026-08-21)

Four patterns over `src/AgentEyes.App` (`--include=*.cs --include=*.xaml`, `obj/` excluded),
all via `grep -rni`:

1. `transcript\.txt|transcript\.json` - 9 hits, ALL comments/doc-comments (MainWindow.xaml.cs
   2043/2049/2368, Plugins.cs 19, RestServer.cs 219-220, TranscriptPresentation.cs 12/26-27).
   No code decision.
2. `File.Exists.*transcript|transcript.*File.Exists` - 1 hit: TestPanel.xaml.cs:311
   `if (row.WantsTranscript && file != null && File.Exists(file))` - that is the mic TEST
   panel checking the audio FILE it just recorded exists before running a live transcription;
   it is not a claim about a recording having a transcript. Left alone.
3. `hastranscript` - hits are: a comment in RecordingDetailWindow.cs:232, the REST payload
   fields reading `RecordingLibrary`'s canonical flags (RestServer.cs 218/413), and
   `TranscriptPresentation.HasTranscript => Kind == TranscriptKind.Transcribed` (the canonical
   derivation itself).
4. `transcript.*\.Length` - RestServer.cs:264 is URL-path parsing (`"/transcripts/".Length`);
   TranscriptPresentation.cs:61 is a log line. RecordingDetailWindow still contains
   `presentation.Text.Length > 0` twice - those choose between showing text and showing the
   placeholder/dim color, i.e. "is there something to display", and the TEXT comes from the
   canonical presentation; they make no transcribed/not-transcribed claim (a flat-only
   recording takes that branch and is simultaneously labeled by `LegacyNotice`).

Conclusion: no remaining transcript-PRESENCE decision in `src/AgentEyes.App` uses file
existence or flat-text length.

### Criterion 4 - runtime proof (QA produces this)

Fixtures: create two folders under `%USERPROFILE%\Videos\AgentEyes\` (delete both afterwards).
Do NOT put a `recording.mp4`/`audio.wav` in them - without media the repair pass
(`TranscriptionBacklog.NeedsTranscription`) ignores them, so nothing tries to transcribe the
fixtures while you look at them.

1. `2026-08-21_090000_flatonly\manifest.json`:
   `{ "Tool": "AgentEyes", "Mode": "video", "Label": "flatonly", "CreatedUtc": "2026-08-21T09:00:00.0000000Z", "DurationSeconds": 30 }`
   plus `transcript.txt` containing e.g. `legacy flat words`.
2. `2026-08-21_091000_transcribed\manifest.json`: same shape plus
   `"Transcript": "transcript.json"`, a `transcript.json` of
   `[{"StartSeconds":0.0,"EndSeconds":1.5,"Text":"hello"},{"StartSeconds":1.5,"EndSeconds":3.0,"Text":"world"}]`,
   and a `transcript.txt` of `hello world` (the pipeline writes both).

Then start the app (`src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe`
- the x64 path, never `bin\Release\`), open the Library, and screenshot both cards
(PrintWindow / UIA per `scripts\gui-smoke.ps1` patterns - background-safe; do not
force-foreground). Expected vs Actual:

- flatonly card: NO "Transcript" chip; an italic "Text file" chip instead. (Old behavior:
  "Transcript" chip - that is the defect.)
- transcribed card: "Transcript" chip, no "Text file" chip.
- REST cross-check (focus-free): `GET http://127.0.0.1:7882/recordings` - the flatonly row
  must carry `hasTranscript: false, hasFlatTranscript: true`; the transcribed row
  `hasTranscript: true, hasFlatTranscript: true`. This is the API/UI agreement the issue
  exists to restore.
- The recording HUD is irrelevant here, but the standing reminders apply: REST / UIA /
  PrintWindow are the focus-free layers; never force-foreground + synthesize input without
  warning the human; HUD state (if you touch it) is asserted via UIA or `/status`, never a
  screen grab (it is capture-excluded).

### Criterion 5 - flat text stays readable

- Implemented: the "Text file" chip opens the same detail view (`TranscriptChip_Click`); the
  detail window shows the flat text under the quiet caption "Not transcribed - showing the
  text file saved with this recording."; "Copy transcript" follows `CanCopy` (text exists),
  not the transcribed claim.
- Tests: `DetailPresentation_FlatTextOnly_TextStaysReadableAndCopyable`.
- QA runtime check: click the flatonly card's "Text file" chip -> the detail window shows
  `legacy flat words` with the caption above it, and the "Copy transcript" button is present.
  Screenshot it.

### Criterion 6 - the gate

- `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Errors (2 pre-existing
  xUnit1031 warnings in PostRecordingQueueTests.cs, untouched by this branch).
- `dotnet test AgentEyes.sln -c Release`: `Failed: 0, Passed: 843, Skipped: 0, Total: 843`.
- Machine note: the x64 .NET host on this machine has WindowsDesktop runtimes 6.x and 10.x
  but not 8.x, so the test run needs `DOTNET_ROLL_FORWARD=LatestMajor` (PowerShell:
  `$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test AgentEyes.sln -c Release`). This is a
  runtime-resolution fact of the machine, not of this change.

## 3. Behavior changes QA should be aware of (intended)

- REST `hasTranscript` is now FALSE for a legacy flat-text-only recording (it used to be
  true). `hasFlatTranscript` is new on list rows and detail. `scripts\api-smoke.ps1` only
  asserts the PRESENCE of the `hasTranscript` property (line 140), so it still passes.
  `GET /recordings/{id}/transcript` still returns the flat text with 200 for such a
  recording - `hasTranscript=false` no longer implies the transcript route 404s; the Python
  client docstrings say so explicitly now.
- A flat-text-only card now shows "Text file" instead of "Transcript".
- The detail window's DISPLAYED text is unchanged for normal recordings (the timestamped
  flat rendering); only when the flat file is missing/empty does it fall back to the JSON
  text via `RecordingLibrary.ReadTranscript` - so a recording whose flat txt was deleted
  but whose transcript.json survives now shows its text instead of nothing.

## 4. Smoke scoping suggestion (QA decides)

- Worth it: the criterion-4 manual fixture run above (light - no recording started), plus a
  targeted `GET /recordings` check. `api-smoke.ps1` end-to-end also covers the recording
  pipeline if you want it, but it records real audio/video for minutes.
- Not needed: gui-smoke's full preset/record sweep; nothing in start/stop/preset paths changed.

## 5. CenCon impact

No drift: no component-map change, no privacy-posture change. No `docs/cencon` edits beyond
this proof note.

## 6. Self-review round (code-review on the diff) - what was found, what was done

Fixed in this branch (each with the test named where one exists):

1. Detail view flattening the timestamped transcript (json-join replacing the flat
   `[HH:MM:SS]` rendering) -> display now prefers the flat rendering, json is the fallback;
   `DetailPresentation_TranscribedRecording_ClaimsTranscriptAndKeepsFlatRendering` and
   `..._TranscribedWithoutFlatFile_FallsBackToJsonText`. This also removes the added
   UI-thread json parse for normal recordings (the constructor reads exactly the file it
   read before this issue; the wider sync-ctor pattern is pre-existing and is issue #16's
   UI-responsiveness pass).
2. NRE on a transcript.json segment with `"Text": null` in `ReadTranscript` -> `?? ""`.
3. Silently swallowed transcript.json parse failure in `ReadTranscript` (pre-existing, but
   newly on the desktop path) -> `Log.Warn` with dir + reason; classification stays
   existence-based (parsing-as-completion is #15).
4. Empty legacy transcript.txt showing "showing the text file" over the empty-state
   placeholder -> `LegacyNotice` now requires shown text;
   `DetailPresentation_EmptyFlatTextOnly_NoNoticeNoCopy`.
5. Silent empty catch on the detail window's manifest load -> `Log.Error`.
6. `RecentItem.From` collapsing both chips on a corrupt manifest although the artifacts are
   on disk -> chip classification moved outside the try, null-manifest fallback;
   `LibraryCard_CorruptManifest_StillClassifiesFromDisk`.
7. Duplicated manifest-name resolution expression -> `TranscriptStatus.JsonArtifactName` is
   the one copy, used by the predicate and `ReadTranscript` (the third copy in
   `Translator.cs:190` is pre-existing, untouched - it resolves for translation input, not
   presence, and folding it in is #15's centralization).
8. Python client docstrings still promising "404 when no transcript" under the old
   semantics -> updated (see section 3).

Reviewed and deliberately NOT changed:

- `TranscriptionBacklog.NeedsTranscription` hardcoding "transcript.json" while the
  predicate honors the manifest name: pre-existing divergence (the old
  `RecordingLibrary.HasTranscript` honored the manifest name too), the pipeline only ever
  writes the default name, and folding the backlog onto the shared predicate is exactly
  issue #15's centralization scope, which this issue explicitly must not absorb. The doc
  comment on `RecordingLibrary.HasTranscript` states this boundary.
- A corrupt transcript.json still classifies as Transcribed: existence vs parsed
  completion is #15 by the issue's own OUT list; the failure now at least logs (item 3).

---

## 7. Round 2 - the review gate's two blocking defects, fixed

### Defect 1: cached card chips diverged from the canonical predicate after external changes

The chips were computed once when the card was BUILT (`RecentItem.From`) and never again, so
deleting or creating `transcript.json` outside the app (the card's own "Open folder" action
invites exactly that) left the visible card contradicting `GET /recordings`, which re-reads
the disk on every request, until some unrelated reload.

What changed:

| File | Change |
|------|--------|
| `src/AgentEyes.App/MainWindow.xaml.cs` (`RecentItem`) | New `RefreshArtifactChips()`: re-derives the Transcript / Text file / Walkthrough chips from disk through the canonical `TranscriptStatus.Classify`. `From` now routes its build-time chip computation through the same method (one copy of the rule), and the card keeps the manifest it was built from (`_manifest`, carried across reloads by `AdoptFrom`) so the refresh honors a manifest-NAMED artifact name with only File.Exists checks - no manifest re-read per card on the UI thread. |
| `src/AgentEyes.App/LibraryCoherence.cs` | New `RefreshArtifactChips()`: runs the card refresh on every row, logs once. Deliberately claims NO epoch and updates NO fact: chips are re-derived from disk on every refresh and every reload, so epoch ordering adds nothing - and stamping rows Present at a fresh epoch would wrongly outrank an in-flight snapshot's honest report of a recording deleted from disk. Membership and ordering untouched, so the issue #3 model is not disturbed. |
| `src/AgentEyes.App/MainWindow.xaml.cs` (`Rail_Checked`) | Showing the Library (rail navigation) now calls `_library.RefreshArtifactChips()`, so the cards agree with the predicate - and therefore with REST - whenever they are (re)shown. Cost: two or three File.Exists per card, per the gate's own sizing. |

Round-2 tests (`TranscriptPresenceTests`, "round 2" sections):

- `LibraryCard_TranscriptDeletedExternally_RefreshAgreesWithThePredicate` - build a
  transcribed card, delete transcript.json, refresh -> Transcript chip gone, Text file chip on.
- `LibraryCard_TranscriptAppearedExternally_RefreshAgreesWithThePredicate` - the inverse.
- `LibraryCard_ManifestNamedArtifact_RefreshStillHonorsTheManifestName` - the cached-manifest
  mechanism, on a non-default artifact name.
- `Library_ShowingTheLibrary_RefreshesEveryRowsChips` - the model route over several rows.
- `RailNavigation_RefreshesTheLibrarysArtifactChips` - the WIRING pin, read from IL
  (fail-closed `CompiledCode.CallsIn`): `Rail_Checked` must call
  `LibraryCoherence::RefreshArtifactChips`. Unwiring the navigation refresh fails this test
  (mutation drill run: it does).

QA runtime verification - the external-delete divergence drill:

1. Start the app; build/keep a transcribed fixture (default `transcript.json` +
   `transcript.txt`, recipe in the criterion-4 section above). The Library card shows the
   "Transcript" chip; `GET http://127.0.0.1:7882/recordings` row has `hasTranscript=true`.
2. Delete that recording's `transcript.json` on disk (do it via the card's "Open folder"
   action if you want the exact gate scenario).
3. `GET /recordings` immediately reports `hasTranscript=false, hasFlatTranscript=true`.
4. Navigate the rail away (Record) and back (Library) - UIA: toggle the rail buttons; no
   foregrounding needed. Expected: the card now shows the quieter "Text file" chip and no
   "Transcript" chip - it agrees with REST. Defect result (round 1): the card kept the
   "Transcript" chip. Assert via UIA or a PrintWindow grab of the card; the log also shows
   `[LibraryCoherence] RefreshArtifactChips: N row(s) re-derived from disk` on each Library
   show.
5. Inverse: put `transcript.json` back (copy it in), rail away and back - the "Transcript"
   chip returns.

### Defect 2: the detail window's JSON-only display path blocked the WPF constructor thread

`TranscriptPresentation.For` does `File.ReadAllText` + full JSON deserialization; round 1
called it synchronously from `RecordingDetailWindow`'s constructor, so a large
`transcript.json` with no flat `transcript.txt` froze the UI before the window appeared
(CLAUDE.md section 1 violation).

What changed (`src/AgentEyes.App/RecordingDetailWindow.cs` only; `TranscriptPresentation`
itself is unchanged - it stays the testable loader):

- The constructor no longer builds the presentation. It builds the transcript area
  immediately with a "Loading transcript..." line (dim), the legacy-notice caption
  (collapsed) and the "Copy transcript" button (hidden) already in place, so the async load
  only fills VALUES in - the visual tree is never restructured after construction.
- `Loaded` fires `LoadTranscriptAsync(manifest)`: the read + deserialization runs in
  `Task.Run` on a background thread; the awaiter marshals back to the UI thread, which sets
  the text, foreground, notice and Copy visibility. Entry-point try-catch with `Log.Error`;
  a failed read degrades to the empty state, never a dead window. Entry and completion are
  logged (`[RecordingDetailWindow] LoadTranscriptAsync: dir=... kind=... chars=...`).

Round-2 tests (structural, from compiled IL - the window itself needs a WPF Application to
construct, which is exactly why the decisions live in `TranscriptPresentation`):

- `DetailWindow_TranscriptLoad_IsNotInvokedOnConstruction` - no call to
  `TranscriptPresentation::For` from the constructor OR any of its lambdas (CompiledCode
  folds lambda bodies back onto the declaring method), and - fail-closed - the call must
  exist in `LoadTranscriptAsync`, so deleting the feature cannot read as fixing the defect.
  Mutation drill run: re-adding a ctor call fails exactly this test.
- `DetailWindow_TranscriptLoad_RunsOnABackgroundThread` - `LoadTranscriptAsync` must hand
  the read to `Task::Run` (an async method that called `For` inline would still block the
  UI thread).

QA runtime verification - the large-transcript responsiveness check:

1. Build a JSON-only fixture with a LARGE transcript, e.g. in a recording folder with a
   valid manifest ($dir below):

       $seg = '{"StartSeconds":0.0,"EndSeconds":1.0,"Text":"' + ('word ' * 200).Trim() + '"}'
       '[' + (($seg + ',') * 20000).TrimEnd(',') + ']' |
           Set-Content "$dir\transcript.json" -Encoding ascii
       Remove-Item "$dir\transcript.txt" -ErrorAction SilentlyContinue   # force the JSON-only path

   (Roughly 20+ MB; scale up if the machine is fast.)
2. Open the Library, open that card's detail view. Expected: the window appears IMMEDIATELY
   showing "Loading transcript..." and stays responsive while loading (it can be moved; the
   title box takes focus), then the text fills in and "Copy transcript" appears. Defect
   result (round 1): the window does not appear until the whole file is read and parsed.
   Two log lines bracket the load (entry, then `kind=Transcribed chars=...`).
3. Also worth one glance on a NORMAL recording: transcript shows as before, flat-only
   recordings still show the caption "Not transcribed - showing the text file saved with
   this recording." and stay copyable - the round-1 behavior is preserved, only the loading
   moved off the UI thread.

### Round-2 gate

- `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Errors (same 2 pre-existing
  xUnit1031 warnings, untouched).
- `dotnet test AgentEyes.sln -c Release`: `Failed: 0, Passed: 850, Skipped: 0, Total: 850`
  (the machine still needs `DOTNET_ROLL_FORWARD=LatestMajor`, see criterion 6 above).
- Mutation drills run and reverted: (a) ctor call to `TranscriptPresentation.For` re-added
  -> `DetailWindow_TranscriptLoad_IsNotInvokedOnConstruction` fails; (b) `Rail_Checked`
  refresh unwired -> `RailNavigation_RefreshesTheLibrarysArtifactChips` fails; (c) card
  refresh made a no-op -> 7 tests fail.

### CenCon impact (round 2)

No drift: no component-map change, no privacy-posture change, no REST shape change. The
issue #3 coherence guards (rows-field scan, row-write scan, route table) all still pass -
the new refresh lives inside `RecentItem` + `LibraryCoherence`, exactly where those guards
require row writes to live.

---

## 8. Round 3 - the review gate's three UI-thread I/O defects, fixed, plus the sweep

The round-2 verdict: three blocking defects, all synchronous I/O on the WPF thread. All
three are fixed, and every other path this PR touched was then swept for the same class
(File.*, Directory.*, Log writes in constructors, event handlers and continuations).
Findings and justifications are in 8.4.

### Defect 1: entering the Library ran unbounded synchronous File.Exists probes on the WPF thread

The round-2 coherence fix itself introduced this: `Rail_Checked -> LibraryCoherence.
RefreshArtifactChips` probed every row inline, and `LibrarySnapshot` has no row cap, so a
large library or slow/unavailable storage stalled the rail paint.

Fix - the sweep is off-thread by construction, with the round-2 coherence semantics intact:

- `src/AgentEyes.App/LibraryCoherence.cs` - `RefreshArtifactChipsAsync` (replaces the
  synchronous method, ~line 445): the owning thread only SNAPSHOTS each row's probe inputs
  (directory + the manifest reference it classifies with - no I/O), the probes and their
  log line run inside `Task.Run`, and the awaiter dispatches the results back to the
  calling context (the WPF dispatcher in the app) where they are applied as plain property
  writes. Still NO epoch and NO fact update (the round-2 rationale stands); the apply step
  touches only chip values on row objects that are updated in place, never replaced.
- `src/AgentEyes.App/MainWindow.xaml.cs` - `Rail_Checked` (~line 227) now fires
  `RefreshLibraryChips` (~line 245), a fire-and-forget async entry point that awaits the
  model's sweep; the rail handler itself stays synchronous and paints immediately with the
  chips the cards already have. Its failure log is written from a worker too.
- `src/AgentEyes.App/MainWindow.xaml.cs` - `RecentItem` (~line 2430): the probe is split
  into `CaptureChipProbe` (no I/O, UI thread) / `ChipProbe.Run` (the File.Exists probes,
  any thread) / `ApplyArtifactChips` (property writes only). The synchronous
  `RefreshArtifactChips` remains only for the callers that are off the UI thread already
  (the snapshot worker via `From`) or probe exactly one new row (`Insert`).

Regression tests (tests/AgentEyes.Tests/TranscriptPresenceTests.cs, round-3 section):

- `LibraryChipSweep_ProbesOnAWorkerWhileTheOwningThreadIsFree` - deterministic, via the
  model's `ChipSweepProbing` seam (same observability reasoning as the model's existing
  `RepairedDivergences` diagnostic): the test HOLDS the worker at the probe point and
  asserts the call has already returned to the owning thread, the chips still show their
  pre-sweep values, and the probing thread differs from the owner; releasing the worker
  lands the re-derived values (the external-delete direction, same as QA's round-2 drill).
- `LibraryChipSweep_HandsTheProbesToAWorker` - IL pin: `Task::Run` inside
  `RefreshArtifactChipsAsync`.
- `RailNavigation_RefreshesTheLibrarysArtifactChips` - updated to follow both hops:
  `Rail_Checked -> MainWindow::RefreshLibraryChips` (CallsIn, fail-closed) and
  `RefreshLibraryChips -> LibraryCoherence::RefreshArtifactChipsAsync` (CallSites). So the
  sweep is proven off-thread AND still proven to fire on rail navigation.
- `Library_ShowingTheLibrary_RefreshesEveryRowsChips` - updated to await the async sweep;
  both divergence directions still covered at the model level.

How QA verifies at runtime:

1. Round-2 divergence drill unchanged: delete/create `transcript.json` externally, leave
   and re-enter the Library via the rail - the chip re-derives both directions (the
   coherence semantics were not weakened).
2. Slow-storage / large-library simulation: the unit seam already proves the stall cannot
   happen, deterministically. If QA wants a runtime feel for it, generate a few hundred
   fixture recording folders under `%USERPROFILE%\Videos\AgentEyes\` (manifest + empty
   media file each, the section-2 fixture recipe in a loop), open the app, and flip rails:
   the Library must paint instantly (UIA fetch of the list within the usual latency)
   while the log's "[LibraryCoherence] RefreshArtifactChipsAsync: N row(s) re-derived from
   disk on a worker" line lands after the paint. A true slow-storage repro (network drive
   or a deliberately huge directory) is optional; the seam test is the load-bearing proof.

### Defect 2: the detail constructor still read+deserialized manifest.json pre-show

Honest provenance note, as asked: the synchronous constructor-time `Manifest.Load` PREDATES
this PR - on main it is `try { summaryText = Manifest.Load(item.Dir).Description ?? ""; }
catch { }` (a swallowed-exception sync read). Round 1 of this PR kept it synchronous (it
made the failure loud and reused the one read for classification); it did not introduce it.
It is fixed regardless, because the window's immediate-show claim depends on it.

Fix - `src/AgentEyes.App/RecordingDetailWindow.cs`:

- The constructor now touches NO disk at all: no `Manifest.Load`, no
  `DevThrottleAccount.IsSignedIn` (that property reads and DECRYPTS the stored credential
  file - also pre-existing constructor I/O, found by the sweep), no `File.Exists` for the
  walkthrough button label (the label now reads the card's already-derived walkthrough
  chip, which the Library re-probes on every show). The summary TextBlock and the sign-in
  banner are built collapsed; the load shows at most one of them.
- `LoadDetailsAsync` (~line 260, replaces `LoadTranscriptAsync`) reads the manifest, the
  account state and the transcript in ONE `Task.Run` body after `Loaded`, then applies
  values on the UI thread.

Regression tests:

- `DetailWindow_ManifestRead_IsNotInvokedOnConstruction` - `Manifest::Load` present in
  `LoadDetailsAsync`, absent from the ctor.
- `DetailWindowConstructor_TouchesNoDiskAndWritesNoLog` - the strict sweep pin: the ctor
  (including every lambda it wires, which the IL folding attributes back to it) calls no
  `System.IO.File::*`, no `System.IO.Directory::*`, no `Manifest::Load`, no
  `get_IsSignedIn`, no `AgentEyes.Log::*`. Any reintroduction fails by name. (The one
  ctor-lambda that legitimately probed the disk - Open folder's `Directory.Exists` - was
  extracted to a named `OpenFolder` method so the pin can be this strict; see 8.4.)
- The two round-2 pins updated to the new method name (`LoadDetailsAsync`).

How QA verifies at runtime: re-run the round-2 large-transcript drill (the 31 MB
JSON-only fixture) but ALSO make the manifest expensive if desired (a manifest on slow
storage behaves the same as a large transcript now - both are on the worker). Expected:
window visible with "Loading transcript..." immediately (round 2 measured 42 ms), UIA
answers throughout, summary/banner and transcript fill in when the load completes. The
flat-only caption and Copy behavior are unchanged from round 2.

### Defect 3: the load continuations logged synchronously on the WPF thread

Fix: every log on the load path is written INSIDE the `Task.Run` body, before the UI
update is dispatched - entry log, manifest-failure log, result log. The catch path logs
via `await Task.Run(() => Log.Error(...))` before touching the controls. Same for the
chip sweep (`LibraryCoherence.RefreshArtifactChipsAsync` logs its count line on the
worker), for the `RefreshLibraryChips` failure log, and for `CommitRename`'s failure log
(pre-existing, fixed by the sweep) which now also logs from a worker. Enterprise-logging
coverage is intact - every path still logs entry/result/failure, none of it on the
paint-critical dispatcher hop.

Regression test: `DetailWindow_Logging_IsConfinedToTheWorkerBackedPaths` - with its limit
stated plainly: IL folding cannot distinguish a Log call inside a method's `Task.Run`
lambda from one after its await (both fold to the method), so THAT placement is held by
code reading and QA's runtime pass; what the pin holds fail-closed is that logging in the
window stays confined to the two worker-backed paths (`LoadDetailsAsync`, `CommitRename`)
and that the load path really does log. A Log call added to the ctor or any synchronous
member fails by name.

How QA verifies at runtime: open details on a fixture; the log carries the
`LoadDetailsAsync` entry and result lines; the window shows content without waiting on
them (they are written before the UI update is dispatched, from the worker).

### 8.4 The sweep - every other touched path, fixed or justified

Fixed by the sweep (beyond the three cited defects):

- `RecordingDetailWindow` ctor: `DevThrottleAccount.IsSignedIn` (credential file read +
  DPAPI decrypt) and the walkthrough-label `File.Exists` - both pre-existing, both moved
  off the constructor (see defect 2).
- `RecordingDetailWindow.CommitRename` catch: `Log.Error` on the UI thread after the
  await - pre-existing; now logs from a worker.
- `RecordingDetailWindow` Open-folder click lambda: extracted to `OpenFolder()`. Its
  single `Directory.Exists` stays synchronous - justified below - but the extraction lets
  the ctor pin ban ALL File/Directory calls from construction.

Justified (left as-is, with reasons):

- `OpenFolder` / `WalkthroughChip_Click` / `TranscriptChip_Click`: one `Directory.Exists`
  or `File.Exists` on an explicit user CLICK (or none). Single bounded probe, never
  paint-critical, matching every other click handler in the app; not part of this PR's
  diff except the extraction above.
- `RecentItem.From` on the UI thread via `LibraryCoherence.Insert`: pre-existing issue #3
  design - Insert is the "a recording just appeared, show it NOW" route for exactly ONE
  row, and `From` reads that row's manifest there today on main. This PR only moved the
  chip derivation to the end of `From`; the unbounded EVERY-row sweep (the actual defect)
  is what went off-thread. Reworking Insert's single-row read is issue #3 territory, not
  a regression of this PR.
- `LibraryCoherence`'s other `Log.Info` calls (Insert, Rename, Refresh, ApplySnapshot,
  Delete, ...): pre-existing issue #3 code, not in this PR's diff; one log per user
  action or per landing snapshot (event-scale, bounded), not a per-row sweep. The one
  this PR ADDED (the sweep's count line) logs on the worker.
- `Rail_Checked`'s dictionary/capture branches (`LoadDictionary`, `LoadCaptures`, ...):
  untouched pre-existing code for OTHER rail destinations, outside this issue's scope and
  diff.
- `TranscriptPresentation.For` (file reads + one Log.Info): only caller is
  `LoadDetailsAsync`'s `Task.Run` body. `RecordingLibrary` / `RestServer`: serve REST on
  listener threads, never the dispatcher.

### Round-3 gate

- `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Errors (same 2 pre-existing
  xUnit1031 warnings).
- `dotnet test AgentEyes.sln -c Release`: `Failed: 0, Passed: 855, Skipped: 0, Total: 855`
  (850 + 5 new round-3 tests; the machine currently lacks the x64 .NET 8 desktop runtime,
  so the test host needs `DOTNET_ROLL_FORWARD=LatestMajor` - it runs on the installed
  10.0.x, same quirk as noted in earlier rounds).
- Mutation drills run and reverted, each failing exactly the intended test(s):
  (a) `Rail_Checked` refresh unwired -> `RailNavigation_RefreshesTheLibrarysArtifactChips`
  fails (Failed: 1); (b) sweep probes made inline (Task.Run removed) -> both
  `LibraryChipSweep_*` tests fail (Failed: 2); (c) a ctor-time `Manifest.Load` re-added ->
  `DetailWindow_ManifestRead_IsNotInvokedOnConstruction` AND
  `DetailWindowConstructor_TouchesNoDiskAndWritesNoLog` fail (Failed: 2).

### CenCon impact (round 3)

No drift: no component-map change, no privacy-posture change, no REST shape change. The
issue #3 coherence guards still pass; the sweep still writes rows only inside
`RecentItem` + `LibraryCoherence`.

I believe this is finished.
