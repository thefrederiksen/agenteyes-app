# Issue #47 handoff - the camera is rendered into the final video

## What was implemented

The framing chosen before recording now reaches the video. Two halves, and the first was the
blocker for the second.

**1. The framing is persisted from the PRESET, not from the preview window.**
`RecordingService.StartVideo` gained an `overlay` parameter and SEEDS `_previewOverlay` with it
instead of clearing it. Before, the framing arrived only from `HudWindow` via `SetPreviewOverlay`,
so a recording made with no HUD open wrote no `PreviewOverlay*` fields at all - which is the state
all three recordings from 2026-08-30 were in. Both start paths pass it: `CapturePreset.Start` uses
the preset's own `Overlay`, and `RestServer` uses the named preset's overlay falling back to the
persisted `HudOverlayConfig`. The HUD can still refine it mid-recording; this is the starting value.

**2. A compose stage renders it.**

- `src/AgentEyes.Core/Preview/CameraComposition.cs` - the pure geometry. Fractions plus two real
  frame sizes in, pixels out: the camera crop, the inset size, the corner position. Every size and
  offset is forced EVEN (yuv420p cannot express odd ones), the inset is clamped to fit any screen,
  and a circle's crop is square in PIXELS so a 16:9 or 4:3 camera cannot make it an ellipse.
- `src/AgentEyes.Core/Video/ComposeArgs.cs` - the ffmpeg command. The circle is masked with a
  pre-drawn image through `alphamerge`, NOT with `geq`: `geq` is a per-pixel interpreted expression,
  about 1.6 billion evaluations on a 576px inset over a 160s 30fps take.
- `src/AgentEyes.Core/Video/CircleMask.cs` - draws that mask, antialiased, and it is deleted after
  the compose.
- `src/AgentEyes.Core/CameraCompose.cs` - the orchestrator: probe both sizes, resolve the geometry,
  run ffmpeg, put the composed video in place as `recording.mp4`, keep the screen-only cut.
- `PostStage.Compose` runs between `Mux` and `Thumbnail` - it needs the media the mux writes, and
  the thumbnail must be made from the video people actually get. A failed compose is deliberately
  NOT fatal: it leaves `recording.mp4` as the screen-only video, which is what people got before
  this feature - a worse video, not a lost one - so it must not cost the transcript.
- `agenteyes compose <dir>` recomposes an existing directory.

## How QA should test each criterion

Everything below was run against the real app and real recordings.

**AC1 (framing persisted with no preview window)** - started via the Control API with
`{"preset":"Demo Screen Capture With Camera","mic":"Microphone (HD Webcam eMeet C960)"}`.
`GET /status` while recording, with `PreviewArmed: false` and `PreviewAvailable: false`:

```
State                : recording
Camera               : HD Webcam eMeet C960
PreviewOverlayCorner : bottom-right
PreviewOverlayShape  : circle
```

Both were `null` before this change. The stopped manifest carried the full framing:
`PreviewOverlayInset 0.2145`, `PreviewOverlayCircle {CentreX 0.5419, CentreY 0.4891, Diameter 0.4243}`.

**AC2 (a compose stage that reaches done)** - the same recording's `manifest.json`:

```
PostProcessing  {'mux': 'done', 'compose': 'done', 'thumbnail': 'done'}
ComposedCamera  True
```

Note the ORDER in the journal - compose sits between mux and thumbnail, which
`Compose_runs_after_the_mux_and_before_the_thumbnail` also pins from `PostStage.All`.

**AC3 / AC4 (placement, size, and a real circular mask)** - `agenteyes compose` on a copy of
`2026-08-30_172406_video` with a bottom-right circle at inset 0.30. Geometry resolves to a 576x576
inset at (1306, 466) on a 1920x1080 screen. Extracting frame t=80s from the composed video and from
`recording.screen.mp4` and differencing pixels:

| point | RGB difference | meaning |
|---|---|---|
| inset centre | 500 | the camera IS drawn (0 would mean nothing rendered) |
| inset bbox top-left | 0 | screen shows through |
| inset bbox top-right | 0 | screen shows through |
| inset bbox bottom-left | 0 | screen shows through |
| inset bbox bottom-right | 0 | screen shows through |
| above / left of the inset | 0 | untouched |

All four bounding-box corners being identical to the screen while the centre differs is what
distinguishes a real circular mask from a rectangle. The rendered frame was also LOOKED AT, not
just measured: a clean circular inset, correct corner, smooth antialiased rim.

**AC5 (time alignment)** - the manifest's offset was -0.855s. Comparing the composed inset against
`camera.mp4` sampled at two different times, inside the circle, mean per-pixel RGB difference:

| camera sampled at | mean difference |
|---|---|
| screen-time 80.000s (no alignment) | 15.12 |
| screen-time 80.855s (offset applied) | **9.38** |

The aligned sample is the match. The residual is x264 re-encode and rescale noise.

**AC6 (camera.mp4 untouched, screen-only preserved)** - `camera.mp4` md5 before and after compose:
`e50b8c6393680e6e724aeec6f50132ca` both times. `recording.screen.mp4` is written and listed in
`OriginalFiles`. Re-running compose on an already-composed directory leaves the screen-only cut's
md5 unchanged - it does not bury the real screen-only video under a composed one.

**AC7 (naming decided)** - decided on the issue: `recording.mp4` is the composed video.
**One deviation from that comment, and it is deliberate:** the screen-only cut is
`recording.screen.mp4`, NOT `recording.original.mp4`. That name is already taken - issue #83 uses it
for the capture as it was BEFORE audio processing, and both files exist in the same directory. Two
different "originals" under one name would make it impossible to say which one a directory held.

**AC8 (the CLI command)** - `agenteyes compose <dir>` returns 0 and reports what it wrote. The two
skip paths are not silent successes: no camera exits 2, no recorded framing exits 3, each naming
the reason. Exit 3 is what your existing recordings hit, since they predate AC1.

**AC9 (gate)** - `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Error(s).
`dotnet test AgentEyes.sln -c Release`: Passed, **1357** tests, 0 failed, at the current head. The
app was stopped for the run AND no other worktree was running tests: a running AgentEyes holds the
webcam, and a concurrent suite in another worktree races this one on shared user-environment state
(`SetupEngineTests.SetBundleExtractBaseDir...`, `PublishedPluginAssetTests...`). Round 2 saw both of
those fail for exactly that reason.

## Three things the reviewer should look at

1. **A predicate that threw on a stranded directory.** `NeedsCompose` originally called
   `Manifest.Load` unguarded. The recovery scan calls `Outstanding()` on every directory it finds,
   including recordings stranded before a manifest was written, so it took down the whole scan
   rather than one directory. The suite caught it (`SessionManifestTests`). Fixed with the same
   guard `NeedsMux` carries, and pinned by two regression tests.
2. **New write call sites are registered in three inventories** - `ManifestWriterTests`
   (source counts), `ManifestWriterIlTests` (IL call sites, file writes AND ManifestStore calls).
   `CameraCompose` does one `ManifestStore.Update`; the File.Move/Delete pairs are the composed
   swap and the mask cleanup. Worth confirming the descriptions match what the code does.
3. **The default circle framing is not centred on a real face.** Composing the reference take with
   the documented defaults (centre 0.50/0.42) put the head cropped at the top of the circle. That is
   issue #36's stated starting point rather than a bug here - and this feature is what finally makes
   it visible, since the framing now reaches the output. It may deserve a follow-up issue.


---

## Review Gate rounds - what changed after each

**Round 1 (REJECT)** - `docs/cencon/review/pr49-issue47-gate-round1.md`. Five defects: re-composing
drew a second inset; a circle became an ellipse on a wide output; a late camera painted an opaque
black box; a failing compose retried forever; and the framing was absent from the durable start
manifest. All five fixed in `8a91e60`.

**Round 2 (REJECT)** - `docs/cencon/review/pr49-issue47-gate-round2.md`. Round 2 independently
CONFIRMED the five round-1 fixes on compiled head, and found four more:

1. **A successful CLI compose left the stage journal stale.** `agenteyes compose` calls
   `CameraCompose.Run` directly and bypasses `PostRecording`, so a compose that succeeded after an
   automatic failure still read `State: failed`. `CameraCompose.Run` now records `NoteDone` itself -
   the one place that actually does the work - so the record is true whichever path ran it. It does
   NOT record `NoteStarted`; the automatic sequence already counts the attempt, and counting twice
   would burn the ceiling on one try.
2. **`Swap` could lose `recording.mp4` permanently.** Delete-then-move is two operations, and dying
   between them left no final file - which recovery would never rebuild, because `ComposedCamera` is
   already true and `NeedsCompose` returns false on that flag before looking at any artifact. It is
   now a single overwriting move, so the name always resolves to one of the two videos.
3. **The full suite was not green on head.** Both failures were shared-environment races with the
   concurrent gate worktrees; the clean run above is the answer.
4. **The independent QA seat is still absent.** See below - that one is not a code defect.

### Verifying the round-2 fixes

Journal (defect 1), on a copy of the real 160s recording seeded with a FAILED compose stage:

```
before:  PostProcessing.compose = {"State": "failed",  "Attempts": 1, ...}
agenteyes compose <dir>   ->  [ok] composed
after:   PostProcessing.compose = {"State": "done",    "Attempts": 1, ...}
         ComposedCamera = True
```

Atomic replace (defect 2) is guarded by `Swap_replaces_the_final_file_in_one_operation`, which reads
the source of `Swap` and fails if a `File.Delete(final)` ever returns, and by the IL write-site
inventory, which pins `Swap` to two Moves and NO Delete.

## The one thing that is still open, and it is not code

Both gate rounds recorded that **the independent QA seat never ran for this issue**. The same seat
wrote the code and produced the verification in this document. The method requires QA to be a
separate identity that commits its own report before `flow:ready-gate`
(`DEVELOPMENT_METHOD.md` 3.3), and the Review Gate does not retroactively create that separation.
It is recorded on the issue and here rather than being papered over; whether to merge without it is
the owner's decision, not this seat's.
