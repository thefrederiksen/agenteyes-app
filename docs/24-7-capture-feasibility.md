# 24/7 Always-On Capture: Feasibility

Date: 2026-06-08
Status: RESEARCH (answers "can we actually do this, and how low-level do we go").
Companion to vision.md (the product idea) and whisper-costs.md / issue #1 (the
transcription-cost side). This doc is the CAPTURE side.

## The question

Run all the time: ONE monitor + the default mic + system sound, recorded
continuously into the rolling 24h buffer. Concerns raised: (a) multiple monitors -
don't record the movie on monitor 3; (b) can two apps hold the mic at once; (c)
what does recording the screen all day cost; (d) only record video when the screen
actually changes. Verdict: YES, feasible - and most of the hard parts already exist
in the repo or in the ffmpeg we already bundle. We do NOT need to write native
DirectX code for v1.

## Answers, point by point

### 1. Pick ONE monitor (ignore the movie on monitor 3)
The bundled ffmpeg's `ddagrab` source (Windows Desktop Duplication API) takes
`output_idx=N` - the 0-based monitor index. We capture only that output; the movie
on another monitor is never touched. This replaces today's `gdigrab` (GDI BitBlt),
which is higher-overhead and has no change info.

### 2. Default mic + system sound, continuously
- System sound: already done - `Audio.LoopbackCapture` (WASAPI loopback) ships today.
- Mic: open the DEFAULT mic in WASAPI SHARED mode (what WaveIn/dshow already do).
- mic + system together is exactly what captures BOTH sides of a call: your voice
  via the mic, the far end via system loopback (the same pairing that the mic-only
  meeting recording got wrong - see audio-capture memory).

### 3. Can multiple apps hold the mic at once? YES.
WASAPI SHARED mode lets any number of apps capture the same mic simultaneously;
Windows mixes/resamples. So AgentEyes records the default mic WHILE Teams/Zoom
use it - no conflict. The only thing that would block us is another app opening the
mic in EXCLUSIVE mode (rare; pro-audio/ASIO). Handling: detect the failure, log it,
keep system-audio + video going, retry. Never request exclusive mode ourselves.

### 4. "Only record when the screen changes" - native, free, built in
Desktop Duplication only produces a new frame when the desktop actually updates.
Two ways to exploit it, both already available:
- `ddagrab=...:dup_frames=0` - frame duplication OFF. By default ddagrab duplicates
  the last frame to hold a constant fps; turning it off means it emits a frame ONLY
  when the screen changed. Pair with variable-frame-rate output -> a static screen
  writes almost nothing.
- `mpdecimate` filter - drops near-duplicate frames (CPU-side belt-and-braces).
- (Lower level, if ever needed) `AcquireNextFrame` returns WAIT_TIMEOUT on no change
  and GetFrameDirtyRects gives the changed regions. We do NOT need this for v1;
  ddagrab already wraps it.

### 5. What does recording the screen all day cost?
- CPU/GPU: near-free. ddagrab returns D3D11 GPU frames; feed straight into a hardware
  encoder (h264_nvenc / hevc_nvenc, h264_qsv, h264_amf, or h264_mf) - no CPU pixel
  round-trip. Expect low single-digit CPU and a few percent GPU.
- Disk: the real cost, and it is BOUNDED by the 24h ring buffer + a hard cap. With
  change-only capture + hardware H.265 on a single 1080p WORK monitor (mostly static):
  rough order of magnitude ~1-5 GB/day; idle stretches approach zero. (Surveillance
  worst-case constant-motion is ~200+ GB/day for several 4MP cams - not our case,
  because we pick a work monitor, not the movie.) Make resolution/fps/codec/cap
  configurable; evict oldest segments first.
- API: $0 for capture. Transcription is the only paid part and is gated separately by
  the VAD/music filter in issue #1 (~$11-22/month instead of ~$259).

### 6. How low-level do we have to go?
v1: NOT low at all. Stay in the ffmpeg we already shell out to:
`ddagrab (one monitor, change-only) -> hardware H.265 -> segmented files`, plus
WASAPI loopback + shared-mode default mic. Segmenting is an ffmpeg flag
(`-f segment -segment_time 60 ...`); the ring buffer is "delete the oldest segment
when over the cap." The repo already has: ffmpeg orchestration, WASAPI loopback,
mic capture, tray/run-at-login, Whisper. Go native (Vortice.Windows / SharpDX over
DXGI + Media Foundation) ONLY if we later need per-WINDOW exclusion, dirty-rect-driven
logic, or our own encoder. Not required to ship the core.

## Recommended v1 architecture

```
video:  ddagrab(output_idx=N, framerate=10, dup_frames=0)  [GPU frames]
          -> hevc_nvenc/qsv/amf (or h264_mf fallback), VFR
          -> -f segment -segment_time 60 -> buffer/seg_%05d.mp4 (ring)
audio:  WASAPI loopback (system)  +  default mic (shared)
          -> continuous; cheap to store (Opus ~250 MB/day)
          -> VAD/music gate (issue #1) decides what gets transcribed
buffer: 24h retention cap; evict oldest segments; disk hard-cap
keep:   scrub timeline -> mark in/out -> export real mp4/wav (the one explicit act)
```

## Real risks / what to prototype first

1. A/V SYNC + SEGMENTING with VFR video. ddagrab(change-only) gives sparse,
   irregular video timestamps; muxing with continuous audio across 60s segments is
   the integration risk. Prototype: 30-min ddagrab+loopback+mic segmented capture,
   verify timestamps, sync, and clean segment boundaries. THIS is the make-or-break.
2. Hardware-encoder selection per machine (NVIDIA/Intel/AMD/none). Probe at setup;
   fall back h264_mf (Media Foundation) or libx264; record the choice.
3. Protected content: Desktop Duplication returns BLACK for DRM/HDCP windows
   (Netflix etc.) - fine (we record a work monitor), but note it.
4. Privacy: full-monitor capture can't easily exclude one WINDOW. Use foreground-app
   exclusions (pause when an excluded app is focused) + the visible indicator + hard
   pause from vision.md. Encrypt segments at rest (positioning.md hard requirement).
5. SSD write endurance over 24/7 - bounded bitrate keeps it modest; document it.

## Bottom line

Technically yes, with low risk. The monitor pick and change-only capture are ffmpeg
flags; the mic-sharing worry is a non-issue in shared mode; CPU/GPU cost is near-free
with hardware encoding; disk is bounded by design. The one thing to de-risk with a
prototype is VFR-video + audio segmenting/sync. Everything else is assembly of parts
we already have.

## Sources

- Desktop Duplication API / AcquireNextFrame / dirty rects:
  https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
  https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe
- ffmpeg ddagrab (output_idx, framerate, frame duplication, D3D11 frames):
  https://ayosec.github.io/ffmpeg-filters-docs/8.0/Sources/Video/ddagrab.html
- WASAPI shared vs exclusive mode (multi-app mic capture):
  https://learn.microsoft.com/en-us/windows/win32/coreaudio/exclusive-mode-streams
- H.265 low-motion storage rates: https://www.arxys.com/storage-savings-with-h265-video-surveillance/
- Verified locally: bundled ffmpeg 8.0.1 has ddagrab, mpdecimate, and nvenc/qsv/amf/mf encoders.
