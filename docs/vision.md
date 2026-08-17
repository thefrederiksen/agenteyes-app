# AgentEyes - vision

## The idea

AgentEyes is a **quiet shadow on your computer**: an always-on process that is
*already recording*, so you never have to remember to hit record. You can't predict the
moment you'll wish you'd captured something - a call, a demo, a bug that flashed by, a
sentence someone said. The shadow has been watching and listening the whole time, so
within a rolling window (default **the last 24 hours**) you can reach back and grab
*anything*, then keep just the part you want.

It runs 24/7, stays out of the way, and keeps its storage bounded by **overwriting the
oldest footage** as new footage comes in - a rolling buffer of your recent computer life.
You only ever interact with it when you need to pull something back.

> It was watching, so you don't have to remember.
> Your machine's memory. Yours alone.

## Why

Today's recorders make you decide *up front* to record. The shadow inverts that:
**capture is the default; retrieval is the action.** The cost of "I wish I'd recorded
that" goes to zero.

## The interaction model: forcefully keep

The buffer is ephemeral by design - everything in it will be overwritten. That makes
*keeping* the one explicit act in the product:

1. Something just happened that matters.
2. Open the timeline, scrub back to the moment (or search the transcript to find it).
3. Mark in/out and **keep** it - export a real file (mp4 / wav / gif) that is yours
   forever, ready to drop into a message, ticket, or doc.

You never manage recordings, name sessions, or clean up disk. The shadow forgets
everything on its own schedule; you forcefully keep the exceptions. This asymmetry -
automatic forgetting, deliberate keeping - is also what makes an always-on recorder
livable: the default outcome of being recorded is that it evaporates.

## Core capabilities (planned)

1. **Always-on rolling capture.** Continuously capture screen (and audio) in the
   background at low overhead, written as short segmented files. A retention window
   (default 24h, configurable) bounds disk use; the oldest segments are auto-overwritten
   as new ones arrive (a ring buffer on disk).

2. **Instant retrieval and keeping.** Scrub the last N hours on a timeline, mark in/out,
   and keep just that section as a normal file. "Give me the last 5 minutes" or "the bit
   around 2:14pm" should be one or two clicks.

3. **Ambient auto-transcription.** Whenever there is sound, transcribe it continuously
   into a timestamped, **searchable text log** of what the machine heard. Search the log
   to *find* the moment ("where did they mention the invoice?") and jump straight to that
   point in the footage. The Whisper pipeline that already powers recording
   transcription is the foundation for this.

4. **Bounded, local, private by default.** Everything stays on the machine. Clear
   pause/exclude controls (don't capture certain apps/windows, mute capture on demand)
   and a visible indicator that the shadow is live.

## What already exists (the foundation in this repo)

The recorder this product is built from already ships the hard parts:

- **Capture engine** (`src/AgentEyes.Core`, CLI `agenteyes.exe`) - screen + audio (mic,
  system loopback, mixed with a noise gate), screenshots, region capture, ffmpeg muxing,
  manifests, in-process Whisper transcription (batch + streaming). One shared
  `RecordingService` drives the GUI, the tray, and the REST API.
- **Presets** - OBS-style named capture profiles; pick one and record (launcher + editor).
- **Tray app, REST control API, run-at-login** (`src/AgentEyes.App`,
  `AgentEyesApp.exe`) - it already lives in the tray and can run on startup.
- **Test panel + headless self-tests** - a deterministic QA harness (12-check selftest,
  API and GUI smoke tests, unit tests) the human runs on demand: `scripts/run-all.ps1 -Confirm`
  (heavy; user-invoked - agents never run it).

In other words: continuous capture, audio transcription, and an always-on background
presence are already working in pieces. AgentEyes is about turning them into the
always-on rolling buffer described above.

## Privacy posture (non-negotiable)

Microsoft Recall's launch and the reception of Rewind.ai are the category's cautionary
tales: an always-on recorder that gets the privacy posture wrong creeps people out and
dies. The posture here is first-class, not a settings page:

- **Visible** - an always-on recording indicator. No stealth mode, ever. (DeskGhost is
  the example of the framing to avoid.)
- **Hard pause** - one action stops everything, visibly.
- **Exclusions** - per-app/per-window capture exclusion; password fields blanked.
- **Bounded** - the buffer self-destructs on schedule; disk use is capped.

## Staying distinct (competitive neighbors)

- **shadow.do** - "AI for meetings, voice typing"; the closest concept neighbor AND a
  "Shadow" name. Differentiate on the full-screen rolling buffer vs meetings-only.
- **NVIDIA ShadowPlay** - rolling-buffer recorder for games. Same mechanic, different
  audience; avoid anything that reads as a sibling (no "ShadowReplay" etc.).
- **Shadow.tech** - cloud-PC brand with a lot of "Shadow" mindshare in tech.
- **Rewind.ai / Microsoft Recall** - the "record everything, search your past" category;
  win on privacy posture and the keep-a-clip workflow.

## Open design questions (to resolve before building)

- **Storage budget.** 24h of screen video is large. Segmented files, sensible
  codec/fps/resolution (downscale? active monitor only? motion-aware frame skipping?),
  hardware encoding, and a disk cap with eviction. Estimate and make it configurable.
- **Performance.** Background capture must be near-invisible (CPU/GPU/disk).
- **Index and search.** The transcript log + a lightweight timeline index so retrieval
  is instant.
- **Retrieval UX.** The timeline scrubber + mark in/out + keep flow is the make-or-break
  surface of the product.

## Rough roadmap

- **Phase A - Rolling capture core.** Segmented continuous capture with a retention cap
  + auto-eviction; prove bounded disk use over a full day.
- **Phase B - Retrieve and keep.** Timeline scrubber over the buffer; mark in/out;
  export a clip.
- **Phase C - Ambient transcription + search.** Continuous transcription of audio into a
  searchable, timestamped log linked to the timeline.
- **Phase D - Privacy and polish.** Exclusions, pause, indicators, disk/retention
  settings, packaging (installer / winget), run-at-login by default.

This document is the starting point; features get fleshed out as we go. The goal is to
learn, from living with the shadow, what it most wants to become.
