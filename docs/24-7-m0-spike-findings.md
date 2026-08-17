# 24/7 Capture - M0 Spike Findings (issue #65, slice S0 of #60)

**Date:** 2026-06-09
**Status:** GO (with two parameter findings for S1/S2)
**Script:** `scripts/spikes/m0-ddagrab-soak.ps1` (throwaway; not product code)
**Machine:** Intel UHD Graphics 770 (integrated), active console session.

## Verdict: GO

The make-or-break encoding shape works: GPU change-only (VFR) screen capture + a continuous audio
track, hardware-encoded, written as independently-playable 60s-target segments that stay tightly
A/V-synced across boundaries, with tiny output. Build the engine (S1) on this shape. The two findings
below are parameter fixes, not blockers, and neither changes the GO.

## What ran

`ddagrab(output_idx=0, framerate=10, dup_frames=0)` (change-only, VFR) -> `hwdownload` -> nv12 ->
hardware encoder, plus the default mic via dshow, muxed with `-f segment -segment_time 60
-reset_timestamps 1 -segment_format mp4`. Two runs: a 20s validation and a 185s soak.

> Audio note: ffmpeg has no native WASAPI *loopback* input on Windows, so the spike used MIC ONLY
> (dshow). System-loopback audio is the app's existing WASAPI capture and is wired in at S1. The
> make-or-break question (VFR video vs continuous audio: do they segment + stay in sync?) is fully
> answered with one continuous track.

## Data (185s soak)

| Segment | Video s | Audio s | A/V delta s | Size KB | Rate KB/s |
|---------|---------|---------|-------------|---------|-----------|
| seg_00000 | 105.9 | 105.84 | 0.056 | 3515 | 33 |
| seg_00001 | 25.6 | 25.51 | 0.091 | 1154 | 45 |
| seg_00002 | 53.5 | 53.50 | 0.000 | 1292 | 24 |

- **Encoder probe:** picked `h264_qsv` (Intel QSV hardware). The nvenc/amf entries in the probe
  chain are correctly skipped on this Intel-only box - the fallback chain works.
- **Continuity:** sum(video) 185.0s vs wall-clock 186.3s -> delta 1.28s (the ~1.3s is capture
  start-up latency; no gaps/overlaps between segments).
- **A/V sync:** max delta 0.091s across the run (well under the 0.5s bar).
- **Change-only:** 24-45 KB/s. A constant-fps 1080p H.264 stream is ~125-625 KB/s, so change-only is
  roughly 5-20x smaller. `dup_frames=0` is doing its job.

## Findings (carry into S1/S2)

1. **`-segment_time 60` does NOT yield clean 60s segments under VFR change-only.** Segments came out
   105.9 / 25.6 / 53.5s, not 60/60/60. The segment muxer cuts only at keyframes, and with sparse VFR
   keyframes the cut drifts. **Fix in S1:** force periodic keyframes, e.g.
   `-force_key_frames "expr:gte(t,n_forced*60)"` (or a fixed `-g`), so segments cut cleanly at 60s.
   This matters for S2 eviction granularity and S5 export seams.
2. **dshow mic real-time buffer overflowed** ("buffer too full, frame dropped") a couple of times.
   Audio still stayed in sync, but **fix in S1:** raise `-rtbufsize`. (Largely moot once system audio
   comes from the app's WASAPI capture rather than ffmpeg dshow.)
3. **Non-monotonic DTS warnings** from `h264_qsv` under VFR; ffmpeg auto-patches them and every
   segment remained playable and in sync. Watch this across a longer soak (S6); if it ever corrupts
   timestamps at scale, the documented fallback is a low constant fps (e.g. 5) instead of pure VFR.

## Open questions (still the human's, carried from #60)

- 2-track (mic/system separable) vs mixed single audio track in segments.
- Segment length (60 vs 120s) vs eviction granularity vs export seams.
- One ffmpeg process (video+audio) vs separate app-side audio muxed by us (likely the latter, since
  system loopback is WASAPI, not dshow).

## Recommendation

Proceed to **S1** (ContinuousCapture service + supervisor) using `ddagrab dup_frames=0` + hardware
encoder probe (qsv confirmed working here) + forced 60s keyframes for clean segmenting. Do not switch
to constant-fps unless a longer soak shows VFR timestamp corruption.
