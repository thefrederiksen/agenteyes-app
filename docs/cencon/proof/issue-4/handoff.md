# Issue #4 - Developer handoff to QA

**Issue:** [App] The desktop Library claims a recording is transcribed when only legacy flat
text exists
**Branch:** `issue-4-library-transcribed-claim`
**Status:** I believe this is finished. Build clean, `dotnet test` Failed: 0 (843 passed,
17 of them new in `TranscriptPresenceTests`). A self-review pass (code-review on the diff)
ran before handoff; what it found and what was done about each finding is section 6.

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
