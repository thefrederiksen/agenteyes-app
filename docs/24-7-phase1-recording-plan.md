# 24/7 Always-On Capture - Phase 1: Continuous Recording Engine

Date: 2026-06-08
Status: PLAN (Phase 1 = RECORD ONLY)
Companion: docs/24-7-capture-feasibility.md (the "can we do it" research),
docs/vision.md (the product idea). Transcription is explicitly Phase 2+ (issue #1).

## Goal

Stand up an always-on recording engine that captures ONE monitor + the default
mic + system sound continuously into a bounded rolling buffer, and never turns
itself off. Phase 1 ends when the machine has been recording itself for days with
bounded disk use and you can export a clip from any point in the buffer. WHAT we
transcribe from all of this is a later phase - not in scope here.

## Locked decisions (from the feasibility research + Soren, 2026-06-08)

- Capture ONE chosen monitor (ffmpeg `ddagrab output_idx=N`), not all of them. The
  movie on another monitor is never recorded.
- Video is CHANGE-ONLY: `ddagrab ... dup_frames=0` + variable frame rate, so a
  static screen writes almost nothing. Hardware-encoded (NVENC/QuickSync/AMF, with
  h264_mf / libx264 fallback) so CPU/GPU cost is near-free.
- Audio is CONTINUOUS: WASAPI loopback (system) + the DEFAULT mic in SHARED mode
  (records even while Teams/Zoom hold the mic). mic + system together = both sides
  of any call.
- The engine NEVER turns off for content. No content-based pausing in Phase 1.
- Storage is a ring buffer: short segment files, retention bounded by a time cap
  (default 24h) AND a hard disk cap; oldest segments evicted first.
- Stays inside the ffmpeg we already bundle. No native DirectX in Phase 1.
- SETUP/UX (decided 2026-06-08): 24/7 is not a new kind of recording - it is an
  existing capture config that never stops. You pick WHAT it records by selecting a
  saved PRESET (monitor + fixed mic + system), exactly like any other recording. It
  lives in its own dedicated "Always On" rail destination (config + live status), not
  buried in Settings; the config is also mirrored into Settings. The mic must be an
  explicitly chosen device, not "Windows default" (default can change mid-day) - warn
  if the selected preset uses the default mic.

## Phase 1 UX: the "Always On" view

A dedicated left-rail destination. It reuses the shipped capture chips + monitor
strip (the "what is being recorded" readout from the Record view) so 24/7 shows the
same at-a-glance state, plus a master toggle, retention/disk, and live status.

Configuring (toggle OFF):

```
  Always On                                          [ OFF (####    ) ]

  What gets recorded, around the clock
   .--------------------------------------.
   | [#] Work Monitor                 [v] |   <- pick a preset, like any recording
   '--------------------------------------'

   +-----------+   +-----------+   +-----------+
   |  SCREEN   |   |    MIC    |   |  SYSTEM   |
   |  Mon 1    |   | Yeti USB  |   |    on     |
   | [= ON =]  |   | [= ON =]  |   | [= ON =]  |
   +-----------+   +-----------+   +-----------+

   Capturing Monitor 1  -  1920 x 1080
   .---.  .=========.  .---.
   | 1 |  |    1    |  | 3 |        the movie on Mon 3 is NEVER recorded
   '---'  '========='  '---'

   Keep the last [ 24 hours v ]     Disk cap [ 50 GB v ]
   Buffer  %LOCALAPPDATA%\AgentEyes\buffer
```

Running (toggle ON):

```
  Always On                                  [ (    ####) ON ]   (o) REC

   +-----------+   +-----------+   +-----------+
   |  SCREEN   |   |    MIC    |   |  SYSTEM   |
   |  Mon 1    |   | Yeti USB  |   |    on     |
   | |||| .  . |   | ||| .|. | |   | |.|||| .  |   <- live activity / levels
   +-----------+   +-----------+   +-----------+

   Buffer  [|==============================        ]
           oldest yesterday 18:42      now 19:05   (span 24h)
   Disk    31.2 GB / 50 GB        Encoder  HEVC (NVENC)

   [ Keep last 5 min ]  [ Keep around a time... ]   [ Stop always-on ]
```

The chips answer "what is being recorded"; the monitor strip answers "which screen"
and shows the movie monitor is excluded; while running, the chips host live activity
so you can SEE it is capturing (the anti-silent-failure idea from the meeting bug).

## Non-goals for Phase 1 (deferred, tracked separately)

- Transcription / VAD / music gating -> issue #1 (Phase 2). We RECORD now; we decide
  what to transcribe later.
- Full timeline retrieval UX (scrubber). Phase 1 ships only a minimal "export a time
  range" so we can prove the buffer is real and usable.
- Per-window exclusion, hard-pause, password-field blanking -> Phase 3 privacy.
- Encryption-at-rest of segments. REQUIRED before any public release
  (positioning.md), NOT a Phase-1 blocker for Soren's own machine. Flagged so it is
  not forgotten.
- Multi-monitor capture, audio per-app routing.

## Architecture (Phase 1)

```
VIDEO  ddagrab(output_idx=N, framerate=<cap>, dup_frames=0)   [D3D11 GPU frames]
         -> hardware HEVC/H.264 (probe: nvenc|qsv|amf|mf|libx264), VFR
AUDIO  WASAPI loopback (system)  +  default mic (shared mode)
         -> mixed or 2-track; continuous
MUX    -> -f segment -segment_time 60 -reset_timestamps 1
         -> buffer/seg_%05d.mp4   (+ a sidecar index: seg #, wall-clock start/end)
BUFFER supervisor evicts oldest segments past the time cap or disk cap
KEEP   pick [t0,t1] -> concat/trim the covering segments -> export real mp4
```

One long-lived ffmpeg process per capture, owned by a supervisor that restarts it on
crash or DXGI_ERROR_ACCESS_LOST (resolution change, RDP, UAC secure desktop, GPU
reset). A new segment file every 60s gives clean restart/eviction boundaries.

## Milestones

### M0 - Spike: prove A/V sync + segmenting (THE make-or-break)
Before any product code. A throwaway script that runs ddagrab(change-only) +
loopback + default mic, hardware-encoded, segmented at 60s, for 30-60 min.
Acceptance:
- Segments are continuous (no gaps/overlaps at boundaries), each independently
  playable.
- Audio stays in sync with the sparse VFR video across several segment boundaries.
- A static screen yields tiny video; activity yields normal video.
- Process survives a resolution change / lock screen (or restarts cleanly).
If sync/segmenting with VFR video is unworkable, fall back here to a low constant
fps (e.g. 5) before building on it. THIS milestone decides the encoding shape.

### M1 - Capture engine + supervisor
- `ContinuousCapture` service in AgentEyes.Core: builds the ddagrab+audio
  ffmpeg args (extend FfmpegArgs.cs), launches via the existing Ffmpeg runner,
  writes to the buffer dir.
- Hardware-encoder probe at first run (try nvenc -> qsv -> amf -> h264_mf ->
  libx264); persist the choice; clear error only if ALL fail.
- Supervisor: detect exit/ACCESS_LOST, relaunch, log; bounded restart backoff.
- Monitor + mic resolution reuses Monitors/DefaultMic already in the repo.

### M2 - Ring buffer + retention
- Segment dir under a configurable buffer root (default %LOCALAPPDATA%\AgentEyes\buffer).
- Sidecar index (segment file -> wall-clock start/end, size) for fast lookup.
- Eviction: delete oldest segments when total age > retention hours OR total bytes >
  disk cap. Runs continuously; atomic; never deletes the segment being written.
- Prove bounded disk over a multi-hour soak.

### M3 - Configuration + setup
- Settings: monitor index, retention hours (default 24), disk cap GB, fps cap,
  codec/quality, buffer path. Mic is always the system default (no picker).
- Wire into the existing Settings dialog + config.json.

### M4 - "Always On" view + always-on integration
- New rail destination "Always On" (see UX section): master toggle, a PRESET picker
  for what is recorded, the reused capture chips + monitor strip, retention/disk,
  and live status. Config mirrored into Settings.
- 24/7 reads monitor + mic + system source from the SELECTED PRESET; warn if the
  preset's mic is the Windows default (24/7 wants a fixed device).
- Run the engine from the tray; start at login; restart with the app.
- Always-on RECORDING INDICATOR (vision.md / positioning.md: no stealth mode).
- Status surface (recording yes/no, buffer span, disk used, encoder) in the view +
  REST API; selftest check that the engine is alive and segments advance.

### M5 - Minimal keep/export (prove the buffer is usable)
- Given [start,end], find covering segments, trim/concat to one mp4 (ffmpeg), save to
  the normal recordings library (reuses Manifest/library). No timeline UI yet - a
  simple "export last N minutes" / "export around <time>" is enough for Phase 1.

### M6 - Soak + QA
- Multi-hour (ideally multi-day) soak: bounded disk, continuous segments, survives
  lock/unlock/resolution-change/sleep-wake.
- selftest additions (run-all.ps1): engine starts, segments advance, eviction holds
  the cap, export produces a playable file.

## Build order

M0 (spike) -> M1 -> M2 -> M3 -> M4 -> M5 -> M6. M0 gates everything; do not build the
service until the encoding/segmenting shape is proven.

## Configuration defaults (Phase 1)

| Setting        | Default                                   |
|----------------|-------------------------------------------|
| Monitor        | primary (user-selectable)                 |
| Mic            | Windows default (shared mode), automatic  |
| System audio   | on (WASAPI loopback)                       |
| Video          | ddagrab change-only, fps cap 10, HW HEVC  |
| Retention      | 24 h                                       |
| Disk cap       | configurable (e.g. 50 GB) hard ceiling    |
| Buffer path    | %LOCALAPPDATA%\AgentEyes\buffer        |
| Runs           | always; starts at login                    |

## Touch points in the existing repo

- src/AgentEyes.Core/Video/FfmpegArgs.cs - add ddagrab + segment arg builders.
- src/AgentEyes.Core/Audio/ (LoopbackCapture, DefaultMic) - reuse for audio.
- src/AgentEyes.Core/Video/Ffmpeg*.cs - long-lived process + supervisor.
- src/AgentEyes.Core/RecordingService.cs - own the continuous engine alongside
  the existing on-demand recorder.
- src/AgentEyes.App (tray, Settings, REST, indicator) - control + status.
- src/AgentEyes.Core/SelfTest.cs + scripts/run-all.ps1 - soak/selftest checks.

## Risks

1. VFR (change-only) video + continuous audio sync/segmenting - de-risked by M0.
2. Per-machine hardware-encoder variance - probe + fallback (M1).
3. DXGI ACCESS_LOST on lock/RDP/secure-desktop/resolution-change - supervisor restart.
4. Protected content (DRM/HDCP) records black via Desktop Duplication - acceptable
   (we record a work monitor); note in UI.
5. SSD write endurance 24/7 - bounded bitrate + cap; document.
6. Disk-full / eviction correctness - never delete the in-progress segment; soak test.

## Open questions

- Mix mic+system into one track, or keep 2 tracks in the segments? (2 tracks keeps
  the meeting "both sides" separable for later transcription; costs a little size.)
- Segment length (60s vs 120s) vs eviction granularity vs export seams.
- One ffmpeg process (video+audio) vs separate audio capture muxed by us.

## Definition of done (Phase 1)

The machine records its chosen monitor + default mic + system sound continuously,
never turning off, into a 24h ring buffer with bounded disk; survives lock / sleep /
resolution changes; shows a visible live indicator; and can export a real mp4 clip
from any point in the buffer. No transcription. Then we move to "what do we
transcribe from all of this" (issue #1).
