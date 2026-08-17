# qa-record

## PRD (cc-qa-agent repo-local tool) - spec

A simple screen + audio recorder that lives entirely inside the cc-qa-agent repo. Capture a quick
screenshot (full screen or a dragged region), record narrated walkthroughs (screen video +
microphone), or record audio with manually triggered screenshots. Multi-monitor aware: you pick
which screen you are working on.

This tool is NOT a cc-director tool and is NOT installed on PATH as a `cc-*` binary. It lives only in
this repo and is invoked from here. It is written in **C# / .NET** (the same stack as cc-director
itself). Where cc-director already has proven C# code (microphone capture, recording manifest,
markdown-to-HTML rendering), we copy ("steal") that source into this repo and adapt it. Everything
else is built fresh in C#. The tool is fully self-contained and does not depend on any `cc-*` binary
being present at runtime.

Name: `qa-record` (matches the repo's `qa-*` convention: qa-login, qa-smoke, qa-report). Ships as
`qa-record.exe`. Date: 2026-06-02.

---

## 1. Why

Two everyday needs while doing QA work that nothing in this repo covers yet:

1. Capture evidence and demos quickly while working on a screen - a screenshot of the whole monitor
   or a dragged region, without reaching for a separate tool.
2. Record a narrated walkthrough ("here is how you do X") - screen video plus voice, in one file,
   to show someone how to do something.

The cc-qa-agent already captures browser screenshots for testing. qa-record is broader (whole-OS, any
app, plus narrated audio/video) and produces richer evidence and how-to walkthroughs as a documentation
output.

---

## 2. Language and what we reuse vs build

**Language: C# / .NET** (target `net8.0-windows`). Chosen because:
- The capture problem is Windows-native - screen capture, audio capture, an always-on-top overlay,
  multi-monitor and DPI handling are all first-class in .NET and awkward in Python.
- cc-director is itself C#, so we can reuse its proven recording code rather than reimplement it.
- A single language and runtime keeps the tool self-contained (one `qa-record.exe`, no Python bridge).

There are no runtime dependencies on cc-director or on any `cc-*` binary. The inventory below is about
where source comes from.

**Reuse - copy proven C# source from cc-director, then adapt:**
- Microphone capture - `cc-director/playground/voice-chat/src/VoiceChat.Core/Pipeline/AudioCapture.cs`
  (NAudio `WaveInEvent`, chunked sample events). Drives Mode A audio and the level meter.
- Recording manifest pattern - `cc-director/phone/CcRecorder/Recording/LocalManifest.cs` (a clean
  on-disk manifest precedent to mirror for `manifest.json`).
- Markdown-to-HTML rendering - `cc-director/src/CcDirector.Avalonia/Helpers/MarkdownHtmlRenderer.cs`
  for assembling the walkthrough document.

Copied source lives under `vendor/` with a short `PROVENANCE.md` noting which cc-director file each
module came from and when, so it can be re-synced deliberately rather than drifting silently.

**Net-new - build fresh in C# (none of this exists in reusable form today):**
- Screen-video capture engine: capture a chosen monitor or region and encode to MP4. Recommended
  approach is `Windows.Graphics.Capture` (`Direct3D11CaptureFramePool`) for frames plus Media
  Foundation `SinkWriter` for H.264 video + AAC audio muxed into a single MP4 - all native .NET, no
  external binary. (The earlier ffmpeg `gdigrab` idea is now a fallback-free alternative to weigh, not
  the default.)
- OS-level screenshot capture (full monitor or dragged region).
- Multi-monitor enumeration (resolution, virtual-desktop position, primary, DPI).
- Draggable region-selector overlay (always-on-top transparent WPF/WinForms window).
- Session orchestration: floating HUD, hotkeys, timeline, offset-named shots, manifest writing.

**Self-contained libraries (NuGet, bundled - not `cc-*`):**
- NAudio (microphone capture).
- Whisper.net (local Whisper transcription in-process; replaces the Python `cc-whisper`). Needs a GGML
  model file bundled or downloaded once and documented precisely - no silent fallback if missing.
- SixLabors.ImageSharp (optional, screenshot crop/annotate).
- Windows SDK projection for the `Windows.Graphics.Capture` / Media Foundation APIs.

So: the screen-video engine, screenshots, overlay, monitor enumeration, and session are net-new C#;
audio capture, the manifest pattern, and HTML rendering are copied from cc-director; transcription is a
self-contained .NET library.

---

## 3. Modes

### Mode A - Audio + screenshots
Record microphone audio continuously while the user triggers screenshots on demand (full monitor or
a dragged region). Output: one audio file plus timestamped screenshots, with each screenshot's
offset into the audio recorded. Optionally auto-transcribe (Whisper.net) and assemble a walkthrough
doc where each screenshot sits next to what was being said at that moment.

### Mode B - Video walkthrough (screen + audio together)
Record screen video of the chosen monitor (or region) with microphone audio muxed into a single
MP4. Optionally still grab screenshots during the recording (markers). For demos and how-to videos.

### Mode C - Quick screenshot (one-off)
No recording session - just capture immediately: full chosen monitor, or show the region selector
and capture what the user drags over. The fast path.

Rule: audio-only-with-screenshots (A) and video-with-optional-screenshots (B) are the two recording
shapes; C is the instant-capture path.

---

## 4. Core features

- Multi-monitor: enumerate connected displays; user chooses which monitor to capture. Built for a
  multi-screen setup (the default machine has several).
- Capture target per action: full monitor, or a draggable rectangular region (marker the user drags
  over the area of interest).
- Microphone selection: enumerate input devices; choose which mic; show a basic level indicator.
- Hotkeys during a session: take screenshot, pause/resume, stop.
- Timestamped output folder; screenshots named with their offset so they align to the audio/video.
- Optional post-processing on stop: transcribe (Whisper.net) and assemble a Markdown/HTML walkthrough
  (reused MarkdownHtmlRenderer) interleaving screenshots + transcript - all in-process, no external
  `cc-*` calls.

---

## 5. Outputs

```
recordings/<timestamp>_<label>/
  recording.mp4          (Mode B) screen video + audio muxed
  audio.mp3 / .wav       (Mode A) microphone audio
  shots/
    00m03s.png           screenshots, named by offset (full monitor or region)
    00m12s.png
  transcript.txt/.json   (optional) from Whisper.net, with timestamps
  walkthrough.html       (optional) screenshots + transcript assembled by reused HTML renderer
  manifest.json          monitor chosen, region rects, mic device, durations, file list
```

Default recordings location is repo-local (for example `recordings/` at the repo root, gitignored).

---

## 6. Technical approach (recommendation, not a mandate)

- Screen-video capture (net-new): `Windows.Graphics.Capture` to grab frames from the chosen monitor or
  region; Media Foundation `SinkWriter` to encode H.264 and mux in the AAC mic audio to one MP4 in a
  single pipeline. No external binary. (Alternative to weigh: ffmpeg `gdigrab` + `dshow` as a separate
  process - simpler to wire, adds an external dependency.)
- Microphone capture (reuse): NAudio via the copied `AudioCapture.cs`; same engine drives the level
  meter and Mode A audio files.
- Monitor + audio-device enumeration: Win32 `EnumDisplayMonitors` / `Screen.AllScreens` for displays;
  NAudio device enumeration for mics.
- Region selector (net-new): a lightweight always-on-top transparent WPF (or WinForms) overlay the
  user drags to define a rectangle; returns the rect to the capture engine.
- Screenshots (net-new): `Graphics.CopyFromScreen` or a single `Windows.Graphics.Capture` frame for
  the chosen monitor/region.
- Transcription + packaging: Whisper.net in-process for the transcript; the reused MarkdownHtmlRenderer
  for `walkthrough.html`. No shelling out to `cc-*`.

---

## 7. CLI sketch (qa-* convention, repo-local)

Built as `qa-record.exe` (a .NET project in the repo, for example under `tools/qa-record/`). Driven
from the repo by humans or by the Python QA skills, which invoke the exe as a subprocess. Also
referenceable as a .NET library if a future C# caller wants it in-process.

```
qa-record screens                         # list monitors and audio input devices
qa-record shot   --screen 2 [--region]    # Mode C: instant screenshot (full monitor or drag region)
qa-record audio  --screen 2 --mic "Mic (Realtek)" --out recordings/...   # Mode A: audio + on-demand shots
qa-record video  --screen 2 --mic "..." [--region] --out recordings/...  # Mode B: screen video + audio
# during a session (hotkeys): S = screenshot, P = pause/resume, Q = stop
qa-record package <recording-dir>         # optional: transcribe + build walkthrough.html (in-process)
```

No cc-director Control API integration. The tool stands alone.

---

## 8. Open questions for the building agent

- Screen-video encode path: native `Windows.Graphics.Capture` + Media Foundation `SinkWriter` (no
  external binary, more code) vs ffmpeg `gdigrab`/`dshow` (external binary, less code). Pick one - no
  runtime fallback between them.
- Region selector: WPF vs WinForms transparent overlay - pick the lightest that feels instant.
- Default capture codec/quality default (file size vs clarity).
- Whisper.net model: which GGML size to bundle, and bundle-vs-download-once with a precise error if
  the model is absent.
- Global hotkeys vs in-terminal keypresses for screenshot/stop during a session.
- Whether Mode A's screenshots are manual-only or also auto-on-content-change.
- Where recordings live by default and retention.
- Exactly which cc-director files to vendor, and how to record their provenance for later re-sync.

---

## 9. Relationship to cc-qa-agent and cc-director

qa-record is part of cc-qa-agent and lives only in this repo as a C#/.NET tool (`qa-record.exe`). It
produces narrated how-to walkthroughs and demo videos as a richer documentation/evidence output
alongside the existing browser screenshots. The Python QA skills invoke it as a subprocess.

It is intentionally decoupled from cc-director at runtime: it does not call any `cc-*` binary and does
not use the cc-director Control API. The only relationship to cc-director is at build time - we copy
proven C# source (microphone capture, the recording manifest pattern, markdown-to-HTML rendering) into
this repo and adapt it, so the tool is fully self-contained and works even where cc-director is not
installed.
