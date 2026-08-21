# Issue #4 - Developer handoff to QA

**Issue:** [App] The desktop Library claims a recording is transcribed when only legacy flat
text exists
**Branch:** `issue-4-library-transcribed-claim`
**Status:** I believe this is finished. Build clean, `dotnet test` Failed: 0 (840 passed,
14 of them new in `TranscriptPresenceTests`).

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
| `src/AgentEyes.Core/TranscriptStatus.cs` (new) | The canonical predicate: `TranscriptKind { None, FlatTextOnly, Transcribed }`, `IsTranscribed(dir, manifest)` (manifest-named transcript.json exists), `HasFlatText(dir)` (transcript.txt exists), `Classify(dir, manifest)`. Internal, visible to the App and Tests. |
| `src/AgentEyes.Core/RecordingLibrary.cs` | `HasTranscript` now delegates to `TranscriptStatus.IsTranscribed` (transcript.txt no longer counts); new `HasFlatTranscript` on `Summary` and `Detail`; `GetTranscript`'s body extracted as `ReadTranscript(dir, manifest)` so the desktop reads text through the same precedence the API serves (json first, else flat txt). |
| `src/AgentEyes.App/RestServer.cs` | `GET /recordings` items and `GET /recordings/{id}` now carry `hasFlatTranscript` next to the (now canonical) `hasTranscript`. |
| `src/AgentEyes.App/MainWindow.xaml.cs` | `RecentItem.From` sets the chips from `TranscriptStatus.Classify`: Transcript chip = `Transcribed` only; new `FlatTextChipVisibility` = `FlatTextOnly` only. `AdoptFrom` carries the new property so in-place refresh (issue #3 model) cannot lose it. |
| `src/AgentEyes.App/MainWindow.xaml` | New quieter chip "Text file" (italic, muted foreground, plain stroke border - visually distinct from the Transcript chip) bound to `FlatTextChipVisibility`; tooltip "Not transcribed - a legacy text file exists. View text and details"; same click handler, so it opens the same detail view (access preserved). |
| `src/AgentEyes.App/TranscriptPresentation.cs` (new) | The detail window's transcript decisions extracted into a testable non-UI type (the issue's flagged assumption): `Kind`, `HasTranscript` (canonical claim), `Text` (json text when transcribed, else flat text, via `RecordingLibrary.ReadTranscript`), `CanCopy` (text exists - independent of the transcribed claim), `LegacyNotice` (non-null for FlatTextOnly). |
| `src/AgentEyes.App/RecordingDetailWindow.cs` | The window renders `TranscriptPresentation` and decides nothing itself: `_hasTranscript` (flat-text length) is deleted; flat-text-only recordings show the text under a quiet italic caption "Not transcribed - showing the text file saved with this recording."; Copy transcript follows `CanCopy`, so flat text stays copyable. |
| `tests/AgentEyes.Tests/TranscriptPresenceTests.cs` (new) | 14 tests, see per-criterion mapping below. |

## 2. Acceptance criteria -> how each is met -> how QA verifies

### Criterion 1 - card indicator driven by the canonical predicate, fails if swapped back

- Implemented: `MainWindow.xaml.cs`, `RecentItem.From` - `TranscriptStatus.Classify(dir, m)`
  drives `TranscriptChipVisibility` (Transcribed only) and `FlatTextChipVisibility`
  (FlatTextOnly only).
- Tests: `LibraryCard_FlatTextOnly_DoesNotShowTranscriptChip`,
  `LibraryCard_TranscribedRecording_ShowsTranscriptChipOnly`,
  `LibraryCard_NoTranscript_ShowsNeitherChip`.
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
  `DetailPresentation_TranscribedRecording_ClaimsTranscriptFromJson`,
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
- `dotnet test AgentEyes.sln -c Release`: `Failed: 0, Passed: 840, Skipped: 0, Total: 840`.
- Machine note: the x64 .NET host on this machine has WindowsDesktop runtimes 6.x and 10.x
  but not 8.x, so the test run needs `DOTNET_ROLL_FORWARD=LatestMajor` (PowerShell:
  `$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test AgentEyes.sln -c Release`). This is a
  runtime-resolution fact of the machine, not of this change.

## 3. Behavior changes QA should be aware of (intended)

- REST `hasTranscript` is now FALSE for a legacy flat-text-only recording (it used to be
  true). `hasFlatTranscript` is new on list rows and detail. `scripts\api-smoke.ps1` only
  asserts the PRESENCE of the `hasTranscript` property (line 140), so it still passes.
- A flat-text-only card now shows "Text file" instead of "Transcript".
- The detail window reads transcript text through `RecordingLibrary.ReadTranscript` (json
  segments first, else flat txt) instead of only `transcript.txt` - so a recording whose
  flat txt was deleted but whose transcript.json survives now shows its text.

## 4. Smoke scoping suggestion (QA decides)

- Worth it: the criterion-4 manual fixture run above (light - no recording started), plus a
  targeted `GET /recordings` check. `api-smoke.ps1` end-to-end also covers the recording
  pipeline if you want it, but it records real audio/video for minutes.
- Not needed: gui-smoke's full preset/record sweep; nothing in start/stop/preset paths changed.

## 5. CenCon impact

No drift: no component-map change, no privacy-posture change. No `docs/cencon` edits beyond
this proof note.
