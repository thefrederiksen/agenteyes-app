# qa-record - global Dictation (Ctrl+H) and spin-out (plan for the implementing agent)

## Why this exists

The recorder already runs all the time (tray, single-instance, REST API, run-at-login). We want to use it
as an everyday tool that **replaces Windows' Win+H voice typing**: press a global hotkey (default **Ctrl+H**)
anywhere, a small **Dictate** window appears (the same one we use in cc-director - see the reference
screenshot), you talk, and the transcribed text is dropped into **whatever text box currently has focus** -
a chat box, a code editor, an email, a browser field.

Two tracks live in this plan:
1. **Dictation** - the new feature (the priority).
2. **Spin-out** - qa-record stops being a sub-folder of cc-qa-agent and becomes its own shipped tool.

## What the user sees (matches the cc-director "Dictate" window)

A compact window, dark theme, titled **Dictate**:
- **Mic** dropdown (remembers the last device).
- **RECORDING** label + a live **timer** (`0:04.2`).
- A live **level meter** (the vertical bars) so you can see the mic is hearing you.
- A **transcript box** - "(your words will appear here)" - that fills as you speak and is **editable** so
  you can fix a word before committing.
- Bottom bar: **Cancel** | **Pause/Resume** | **Insert** (green) | **Send** (blue).

Behavior:
- **Insert** - types the transcript into the text box that had focus when you pressed Ctrl+H, and leaves the
  caret there (no submit).
- **Send** - same as Insert, then presses **Enter** (submit the message / run the prompt).
- **Cancel** - discards, returns focus to where you were, injects nothing.
- **Pause** - stops listening without ending the session; Resume continues.

## The hard parts (design them deliberately)

### 1. Global activation (default: double-tap Ctrl)
- **Default trigger is a double-tap of Ctrl**, not a chord - chosen so it collides with nothing (Ctrl+H is
  already used inside cc-director, so a global Ctrl+H would shadow it everywhere).
- Detection needs a **low-level keyboard hook** (`SetWindowsHookEx WH_KEYBOARD_LL`), not `RegisterHotKey`:
  - Count a "clean Ctrl tap" only when Ctrl goes **down then up with no other key in between** (so
    Ctrl+C / Ctrl+V / Ctrl+arrow never count).
  - Fire when two clean taps land within a short window (~350-400 ms). **Debounce** so fast typing or
    holding Ctrl as a modifier never triggers it.
- **Rebindable** in Settings, stored in config. The setting supports both styles:
  - a **double-tap of a modifier** (Ctrl / Shift / Alt), or
  - a **standard chord** (then we use `RegisterHotKey` + `WM_HOTKEY` instead of the tap detector).
- Same global caveats as any hook/hotkey: it will **not** fire while an **elevated** window has focus if our
  app is non-elevated (UIPI), the combo can be lost to an app that grabbed it first (chord mode), and
  exclusive full-screen apps can swallow it. Surface a clear status; no silent fallback (house rule).

### 2. Remember the target before we steal focus
- On hotkey fire, **before** showing the Dictate window, capture:
  - `GetForegroundWindow()` -> target window HWND.
  - The focused control: `AttachThreadInput` to the target's thread, then `GetGUIThreadInfo().hwndFocus`.
- Store these. The Dictate window is a **tool window** (no taskbar entry). On Insert/Send we re-foreground the
  target (`SetForegroundWindow` + `AttachThreadInput`/`SetFocus`) and then inject.

### 3. Inject text into any text box
- **Default: clipboard paste.** Save the current clipboard, set it to the transcript, re-foreground the
  target, send **Ctrl+V** via `SendInput`, then restore the clipboard (best-effort, after a short delay).
  Most reliable across Win32 / browser / Electron / Office.
- **Alternative: synthetic keystrokes** (`SendInput` with `KEYEVENTF_UNICODE`, char by char). No clipboard
  clobber; slower for long text and a few apps drop fast input. Expose as a Settings toggle.
- **Send** = inject, then `SendInput` Enter (`VK_RETURN`).
- Known limits (document, don't fight): cannot inject into **elevated** windows from a non-elevated process
  (UIPI), and some UWP fields are picky. Surface a quiet status if injection clearly failed.

### 4. Transcription
- Reuse the existing **Whisper.net** path: `Transcriber.TranscribeWavAsync` + `ModelStore` (the base model
  is already downloaded by the self-test / test panel). Same model, no new dependency.
- Mic capture reuses `QaRecord.Audio.AudioCapture` (16 kHz mono - already the format Whisper wants) and its
  `LevelChanged` event drives the meter.
- **MVP**: capture to a temp WAV; on Stop/Insert, transcribe the whole clip once, fill the box, inject.
  Ships the end-to-end flow fast; the box fills at the end rather than live.
- **Live (fast-follow)**: re-transcribe the growing clip every ~1.5-2s and replace the box text (Whisper
  base is fast enough for short dictations < ~1-2 min). A later refinement is VAD/segment finalization so we
  only re-run the tail. Keep the editable box either way.

### 5. Don't collide with screen recording
- Screen recordings are owned by the single-instance `RecordingService`. Dictation must use its **own**
  `AudioCapture` instance (mic only), not `RecordingService`, so the two can coexist. `WaveInEvent` is shared
  mode, so opening the same mic concurrently is generally fine; if it isn't, warn rather than crash.

## Components to build

- `DictationTrigger` - owns the low-level keyboard hook, detects the double-tap (or chord), debounces, and
  raises an event when activation fires. Registers at startup regardless of window visibility.
- `ForegroundTarget` - captures the target HWND + focused control, and re-focuses it later.
- `TextInjector` - `Insert(string)` and `Send(string)` via clipboard-paste (default) or keystrokes.
- `DictationSession` - owns the mic `AudioCapture`, the timer, pause/resume, and the (chunked) transcription;
  exposes `Level`, `Elapsed`, current `Text`.
- `DictateWindow.xaml` - the dark window matching the screenshot (mic combo, timer, meter, transcript box,
  Cancel/Pause/Insert/Send), bound to `DictationSession`.
- Wire-up in `App.xaml.cs`: register the hotkey at startup; on press, snapshot the target, open/raise the
  Dictate window.
- Settings additions (in `SettingsDialog` + `Config`): hotkey, dictation mic, injection mode, model size,
  "dictation enabled".
- Optional REST: `POST /dictate` to trigger the window from cc-director (the hotkey is the primary path).

## Settings / config (config.json)

- `DictationEnabled` (bool, default true)
- `DictationTrigger` (default `double-tap Ctrl`; alternatively a modifier double-tap or a chord like Ctrl+Alt+Space)
- `DictationMic` (device name; default = last used)
- `InjectionMode` (`paste` | `keystroke`, default `paste`)
- `DictationModel` (whisper size, default base)

## Always-on + spin-out track

- Always-on: default **Run at login** on for the dictation use-case; the hotkey registers at startup whether
  or not a window is shown. (Already a tray app - small change.)
- Spin-out: lift `tools/qa-record`, `tools/qa-record.App`, `tools/qa-record.Tests` into their own
  repo/solution with its own installer and versioning; keep the REST API. The product is becoming more than
  "qa-record" (capture + dictation) - **naming is an open decision**.

## Phasing

- **Phase 1 (MVP, ship-able):** global Ctrl+H -> Dictate window -> record mic with meter+timer ->
  transcribe-on-stop -> **Insert/Send** via clipboard paste into the prior focus. Editable transcript box.
  Settings for hotkey + mic.
- **Phase 2:** live (chunked) transcription so the box fills as you speak; Pause/Resume; keystroke-injection
  option.
- **Phase 3:** spin-out into a standalone tool (installer, naming, autostart-by-default), optional REST
  trigger, polish (per-language model, punctuation/casing cleanup).

## Verify it headlessly where possible

- `TextInjector` against a known Notepad/edit control via UI Automation (assert the text landed).
- Hotkey registration success/failure path (assert error surfaced, not swallowed).
- Transcription reuses the existing tested Whisper path; the injected-self-test already covers transcription.
- The window itself is user-driven (like the Test Panel) - the agent does not drive a live dictation.

## Decisions (locked)

1. **Theme**: build the Dictate window **dark** to match the cc-director screenshot.
2. **MVP transcription**: **transcribe-on-stop** for v1; live (chunked) is Phase 2.
3. **Default injection**: **clipboard-paste** (save -> set -> Ctrl+V -> restore); keystroke mode is an option.
4. **Activation default**: **double-tap Ctrl** via a low-level keyboard hook (chosen to avoid cc-director's
   Ctrl+H); rebindable in Settings to another modifier double-tap or a chord, and stored in config.
5. **Product name** for the spin-out: still open.
