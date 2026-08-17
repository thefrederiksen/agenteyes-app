# qa-record - thorough self-testing plan

Goal: prove the recording library actually works **without the UI and without bothering the user**,
using audio/video I generate and control. Only involve the user at the very end for a real-world
acoustic check (their voice + music). Date: 2026-06-03.

## Principle

The UI is the worst place to discover bugs. Everything the GUI does goes through the engine
(`qa-record` assembly). So: make the engine independently testable and self-verifying, drive it
headlessly with **injected** audio/video, and assert on the produced files - levels, streams,
durations, transcript content, cleanup, manifest. The GUI then only needs a thin smoke test.

## Phase 1 - Logging (so failures are visible, not mysterious)

- Add a `Log` facility in the engine: timestamped lines to `%LOCALAPPDATA%\qa-record\logs\qa-record-YYYYMMDD.log`.
- Log at every meaningful step: session start (mode/source/options), device chosen, ffmpeg command line,
  capture start/stop, mux start/result, file sizes, durations, and every caught error with stack.
- The GUI and CLI both write to the same log. The GUI also logs UI actions (REC clicked, STOP clicked).
- Replace the giant crash message box with: log full detail + show a short, friendly status line. The app
  must never hard-crash on a recording error.

## Phase 2 - Fix the known defects

- Snapshot all per-session state (paths, capture objects) into locals before the background stop/mux work,
  so a new session or an early ffmpeg exit can't null them mid-flight.
- Serialize stop: a single re-entrant guard; `OnTick` auto-stop and a manual STOP can't both run.
- `Ffmpeg.Run` (and the arg builders): reject null/empty args with a clear message naming the bad field.
- Re-validate via the harness below.

## Phase 3 - Audio injection utilities (so I control the inputs)

- `tone(freq, secs)` - generate a known sine WAV (via ffmpeg lavfi).
- `speech(text)` - generate a spoken WAV with **known words** using the Windows `SpeechSynthesizer`
  (System.Speech). This gives a deterministic transcript target.
- `playToSpeakers(wav)` - loop a WAV out the default render device (System.Media.SoundPlayer) so WASAPI
  loopback captures it. This is how I "inject system audio" with no movie and no user.
- For mic injection where needed, I drive the same way and rely on loopback for the deterministic signal;
  the mic path is validated for capture/format/level rather than content.

## Phase 4 - `qa-record selftest` (the centerpiece, fully headless)

A new CLI subcommand that runs the whole matrix against injected media and asserts, printing a PASS/FAIL
table and writing a log + a small HTML report with the numbers. No UI. Cases:

- **enumerate**: >=1 monitor; >=1 NAudio mic; DirectShow list non-empty.
- **shot (full)**: PNG exists, decodes, dimensions == monitor size.
- **shot (region rect)**: capture an explicit rect (no overlay needed) -> PNG of that size.
- **audio mic**: WAV is 16 kHz mono, duration within tolerance.
- **audio loopback + injected tone**: mean level above threshold; **silence test** (nothing playing) ->
  near-silent. Proves loopback captures real output and isn't fabricating signal.
- **audio mixed + injected tone**: output 48 kHz stereo, level healthy, tone present.
- **gate behaviour**: feed a silent mic + injected system -> mixed output still has the system; feed a
  loud test into the mic chain with gate -> below-threshold content is attenuated (assert on a crafted file).
- **video mic / mixed / system-only + injected tone**: MP4 has the expected streams (h264 always; aac when
  audio requested), duration within tolerance, audio level healthy for the injected cases.
- **package (transcribe)**: transcribe a `speech("the quick brown fox ...")` clip and assert the expected
  words appear (case-insensitive, allow minor ASR noise). Proves Whisper path produces real text.
- **package (frames + walkthrough)**: key frames extracted (>0), `walkthrough.html` contains the frames and
  transcript segments.
- **cleanup**: after each session, assert there are no leftover temp files (`raw.mp4`, `*_native.wav`).
- **manifest**: fields correct (mode, source, region, durations, file list).

Each case logs its command line and the measured numbers, so a failure is self-explaining.

## Phase 5 - Expand the xUnit suite

Add unit tests for the pure logic the harness exercises end-to-end: arg builders for the new mux paths,
gate-on/off, volume math, manifest round-trips for video/mixed, selftest assertion helpers (level
thresholds, word-match). Keep the existing 67 green.

## Phase 6 - GUI smoke test that I run (not the user)

A self-driven UI Automation script (already proven workable) that launches the app, clicks through each
mode (Screenshot, Audio mic/system/mixed, Video mic/mixed) with `--seconds`-style short runs via injected
audio, then asserts: a recording was produced for each, and the **crash log is empty**. This catches GUI
lifecycle bugs (like the one above) without the user touching it.

## Phase 7 - One-command runner

`tools\qa-record\run-all.ps1`: builds both projects, runs `dotnet test` (unit) and `qa-record selftest`
(integration) and the GUI smoke test, and prints one PASS/FAIL summary. I run this every iteration.

## What still genuinely needs the user (only when all above is green)

- A real **acoustic** check: their actual voice + music, once on **headphones** (should be clean) and once
  on **speakers** (to feel the noise gate working against bleed). This is the only thing I can't fully
  synthesize - it's about real-world capture and subjective quality.
- I will not ask for this until `run-all.ps1` is fully green and I've reviewed the produced media myself
  (levels, transcript, playback sanity).

## Acceptance bar before I involve the user again

1. `run-all.ps1` green: unit tests + selftest + GUI smoke, no crash-log output.
2. I have personally inspected: a mixed audio WAV (tone + injected), a mixed MP4 (streams + level), and a
   transcript of an injected speech clip matching the known words.
3. Logging is on and a sample log is readable.
