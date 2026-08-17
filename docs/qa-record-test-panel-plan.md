# qa-record - in-app Test Panel (plan for the implementing agent)

## Why this exists

The previous testing approach was wrong: the agent started recordings from the background and told the
user "talk now / type stop". The agent cannot see the user's screen or know when they are ready, so it
wasted the user's time. **Fix: the tests live inside the app. The user controls all timing by clicking
buttons; the app gives the instructions and shows the results.** The agent is never in the loop during a
test.

## Concept

A "Tests" panel in the app (own window, opened from the main window and the tray). A list of tests, each
a row with a Run button. Two kinds:

- **Automatic** - app runs it and shows PASS/FAIL instantly, no user action.
- **Guided** - the app walks the user through; the user clicks Start/Stop at their own pace.

## How a guided test feels (the important part)

1. User clicks **Run** on a test.
2. App shows an instruction card: what this checks, then "Click **Start** when ready, do [specific
   action], then click **Stop**."
3. User clicks **Start** - the user decides when, not the agent.
4. While recording: a big **timer**, a live **mic level** bar and **system level** bar, and a **Stop**
   button. (Seeing the meter move tells the user the mic is actually hearing them before they trust the take.)
5. User clicks **Stop**.
6. **Result card**: objective checks (PASS/FAIL with numbers), a **Play** button to hear/see the file,
   and for tunable tests, inline **sliders** (mic volume, system volume, gate on/off) plus **Run again**
   to retry without leaving the panel.
7. Optional thumbs up/down, saved to a small test report.

## Test catalog

Automatic (no user action):
- **Devices** - monitors + microphones detected.
- **Screenshot** - full monitor and a region.
- **Injected self-test** - tones + TTS speech run through the whole pipeline (the existing 12-check
  matrix: loopback, mixed, gate, transcription, video, cleanup). ~1 minute.

Guided (user presses buttons; app instructs):
- **Mic check (per microphone)** - "Start, count to ten, Stop." Shows which mic, its level in dB, a live
  meter, and the transcript. Tells the user whether their voice was captured and which mic is best. *This
  is the test we actually needed - it would have caught the dead/quiet FDUCE mic immediately.*
- **Voice + music (speakers)** - "Start, play music and talk over it, Stop." Shows transcript + Play;
  mic/system/gate sliders; Run again to retune until the voice sits over the music.
- **Voice + music (headphones)** - clean baseline for comparison.
- **Screen walkthrough** - "Start, do something on screen and narrate, Stop." Then Play the MP4.

## Design principles

- The app instructs; the user controls timing. No background "do it now."
- Every guided test ends with a playable artifact the user judges, next to the objective numbers.
- Live level meters so the user can confirm the mic is picking them up *before* trusting a take.
- Tunable tests expose mic vol / system vol / gate inline with a Run-again button.

## Implementation notes

- New `TestPanel.xaml` window; open it from the main window and the tray menu.
- Reuse `RecordingService` (start/stop, `.Level`, `.Elapsed`) and the existing REST verbs; no new capture
  code needed.
- Automatic tests call the existing `SelfTest` / engine directly and render the PASS/FAIL table.
- Save guided-test artifacts + a small report under `recordings/_tests/`.
- Keep everything verifiable headlessly too: the automatic tests already run via `run-all.ps1`.
