# Issue #160 stage 1 - QA report (Windows): the parity inventory and the hosting question

**Under test:** PR #177, branch `issue-160-parity-inventory`, commit `6498aa8`, one file:
`docs/cencon/proof/issue-160/parity-inventory.md`.
**Seat:** independent QA, Windows laptop. QA verifies; it does not merge (method D7).
**Date:** 2026-11-16.

**Verdict: FAIL (flow:qa-failed), on the completeness proof only.** Everything the owner's decision
rests on holds up. The three structural findings are confirmed, and finding 2 was measured against the
running WPF app. The row count and the status counts (179 / 36 / 29 / 114) re-count exactly. But the
document's own completeness proof has a broken instrument. **E5 says 90 XAML handlers, all mapped.
There are 97, and 7 are not in Appendix A.1.** There are also three smaller wrong claims (section 1.3).
None of them changes a status, a count, or a finding. Each is a small edit. They are named precisely so
that the fix is quick.

The answer the Implementation Lead asked for (job 3): **`agenteyes serve` works on Windows.** It
served the Control API, answered `/health` and `/status`, started and stopped a real screen recording,
and that recording decodes. **It is not a drop-in Windows host yet.** It has no graceful stop on
Windows, and most host-side features are compiled into `src/AgentEyes.App`, not Core (section 4).

The two numbers (job 4): **of the 114 MISSING rows, 11 are HOST-SIDE and 103 are UI-SIDE.** Of the 11
host rows, **5 are already in `agenteyes serve`** and **6 live only in the WPF project** and must be moved
into Core. Section 5 has the detail and Appendix Q has every row.

---

## 0. Gate

| Check | Result |
|---|---|
| `dotnet build AgentEyes.sln -c Release` at `6498aa8` | `Build succeeded.` `0 Error(s)` |
| `dotnet test AgentEyes.sln -c Release --no-build` | `AgentEyes.Tests`: Failed 0, Passed 2375, Skipped 15, Total 2390 (1 m 17 s). `AgentEyes.Tests.DesktopApp`: Failed 0, Passed 15, Total 15 |

The PR is docs-only, so the gate proves only that the branch is not broken.

---

## 1. Job 1 - completeness, re-run against the developer's own enumerations

Every command ran in Git Bash from the repository root at `6498aa8`. `obj/` and `bin/` were excluded.

### 1.1 The six enumerations

| # | Claim | My command and output | Match? |
|---|---|---|---|
| E1 | 5 `.xaml` files | `find src/AgentEyes.App -name "*.xaml" ...` -> App, MainWindow, PresetEditor, PreviewWindow, TestPanel | YES, 5 |
| E2 | 15 `Window` classes + 2 Core WPF windows; `UserControl` = 0 | `grep -nE "class \w+\s*:\s*(Window\|UserControl)\b" src/AgentEyes.App/*.cs` -> 15 lines, the same 15 files. UserControl count 0. Core: `MonitorHighlight.cs:18`, `RegionOverlay.cs:20`. All 46 `.cs` files are top-level (no subfolders the `*.cs` glob could miss) | YES |
| E3 | `TrayHost.BuildMenu`: 14 items, 4 separators, + 5 icon behaviours | `grep -nE "new WinForms.ToolStrip(MenuItem\|Separator)\(" TrayHost.cs` -> 14 items (lines 210-246), 4 separators. No other `ContextMenuStrip` / `ToolStripMenuItem` in the project. The other `ContextMenu` hits are the XAML menus (rows V2, V4, V11, V36) | YES |
| E4 | `InstallCaptureHooks`: 2 global hooks + 4 in-window key handlers | `SetWindowsHookEx` only at `KeyboardHook.cs:42`; `new KeyboardHook(` only at `App.xaml.cs:233`; `ArmCaptureHook(` at `:223` and `:225`. `RegisterHotKey`, `InputBinding`, `KeyBinding`, `KeyGesture`, `PreviewKeyDown`, `KeyUp` -> 0. `KeyDown` -> the 4 sites listed | YES |
| **E5** | **90 handlers (56 / 19 / 4 / 11), all mapped in A.1** | **97 handlers (56 / 23 / 7 / 11).** See 1.2 | **NO** |
| E6 | `MessageBox.Show`: 16 sites in 6 files | `grep -n "MessageBox.Show" *.cs \| wc -l` -> 16; `grep -l` -> 6 files | YES |

Row-count re-check: `grep -cE "^\| (W|T|H|V|S|P|G|D|U|B)[0-9]+ \|"` -> **179**. By section: W 17,
T 19, H 6, V 74, S 6, P 11, G 6, D 9, U 11, B 20. No duplicate IDs. Status column: PRESENT **36**,
PARTIAL **29**, MISSING **114**. All match the document.

### 1.2 The E5 defect: 7 handlers the instrument could not see

The document's regex lists a fixed set of event names (`Click|Checked|...|MouseUp|...`). It does not
include `MouseLeftButtonDown`, `MouseMove`, `SizeChanged`, `MediaOpened`, `MediaEnded` or
`MediaFailed`. I used a different instrument: every XAML attribute value that names a `void X(` method
in the code-behind. Its positive control is that it reproduces MainWindow's 56 and TestPanel's 11
exactly.

| File | Document | Measured | Handlers missing from Appendix A.1 |
|---|---|---|---|
| PresetEditor.xaml | 19 | **23** | `OverlayAdorner_MouseDown` (MouseLeftButtonDown), `OverlayAdorner_MouseMove`, `OverlayAdorner_SizeChanged`, `InsetSchematic_SizeChanged` |
| PreviewWindow.xaml | 4 | **7** | `Media_Opened`, `Media_Ended` (rewind to 0 and pause), `Media_Failed` (shows "Playback failed: ...", `PreviewWindow.xaml.cs:115-120`) |
| MainWindow.xaml | 56 | 56 | - |
| TestPanel.xaml | 11 | 11 | - |
| **Total** | **90** | **97** | **7** |

Impact: the 4 PresetEditor handlers belong in P7 (camera overlay framing), which is already MISSING.
The 3 PreviewWindow handlers belong in W4. W4 is PRESENT, but it compared only PlayPause, Restart, Seek
and KeyDown. End-of-media behaviour and the playback-failure message were not compared, and W4 itself
says sizing (`Media_Opened`) was not compared. **No status changes.** The defect is in the proof: the
document says "No handler is left unmapped" and "The mapping covers ... 90 handlers", and both
statements are false.

### 1.3 Other wrong claims found

1. **P10 is missing a command.** It cites `ManagePresetsDialog.cs:67-71` and lists New, Edit,
   Duplicate, Rename and Delete. Line **72** adds a sixth button, **"Set active"** (`SetActive()`,
   `:175`). The row stays MISSING; its contents are incomplete.
2. **Finding 2 says the Avalonia app "calls six of the headless-only routes"**, then names five.
   `ControlClient.cs` calls five: rename, `DELETE`, subtitles, walkthrough and `POST /account/refresh`.
   It never calls `GET /account`. Change the number to five.
3. **W2's instrument is imprecise.** It says there is "no `Diagnostic` string in DesktopApp". That grep
   hits 2 files (`using System.Diagnostics;` in `Services/Shell.cs` and `Views/MainWindow.axaml.cs`).
   The conclusion (there is no diagnostics UI) is right; the stated grep result is not.
4. (Minor, same row) **G1 omits drag-and-drop install.** `PluginManagerWindow.cs:39-42` sets
   `AllowDrop` and installs a dropped `.zip` or folder. G1 lists only "Install from file...".

### 1.4 The enumeration the developer did not do: the UI's own labels

An enumeration of handlers cannot prove that no row is missing. So I enumerated what a person
**sees**: every `Header="..."` and `Content="..."` in the 5 XAML files (68 distinct labels), plus every
code-built `Content = "..."`, `Header = "..."` and `Btn("...")` in the code-only windows (40 distinct
labels). I checked each against the inventory. The non-obvious ones were tray "Show recording
indicator", HUD "Hide" and "Show preview", "Noise gate on mic" (TestPanel), "Start when Windows starts"
("On"), "Reset framing", "Set", "Square 1080x1080", "Copy transcript" and "Add credits". **Every label
lands in an existing row except "Set active" (1.3 item 1).**

I also looked for background mechanisms that E6 could miss because they are outside `OnStartup` and
`OnExit`. I grepped `SystemEvents`, `SessionSwitch`, `PowerModeChanged`, `FileSystemWatcher`, timers,
clipboard, toasts, drag-drop and `HwndSource`. Every hit maps to an existing row: the timers belong to
the HUD, the window, the preview and the test meters; the clipboard to V69 and W6; the toast to W14; the
drop to G1; and `HwndSource` to U11 and W14.

**I found no missing row**, only the missing items within rows listed above. I could not open the
owner's WPF window to click through its menus (see section 6), so this sweep reads labels; it does not
come from using the app.

---

## 2. Job 2 - the three structural findings

### (a) "On Windows the WPF app IS the recorder" - CONFIRMED

- Source: `App.xaml.cs` constructs `new RecordingService()` (line 98), `new RestServer(_service,
  _cfg.Port, ...)` (line 114), `new RepairService(...)` with the person's `HousekeepingSettings` (lines
  124 and 129), and `new AlwaysOnController(_service, _cfg)` (line 142).
- Measured on this laptop: port 7882 is held by the owner's installed WPF app,
  `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe`, pid 16160, started 2026-11-15. `GET /health` ->
  `{"ok": true, "app": "AgentEyes"}`, with no `host` field (the headless server returns
  `"host": "headless"`). `GET /version` -> `1.11.11`, the same version as both `.csproj` files at
  `6498aa8`.
- Nothing else on Windows serves 7882. `DesktopApp` builds no `RecordingService` and its base URL is a
  constant (`ControlClient.cs:41`).

### (b) "The two servers serve different routes; the Avalonia app's calls get 404 from the WPF server" - CONFIRMED BY MEASUREMENT

Measured against the running WPF app on `127.0.0.1:7882`. I used a recording id that does not exist,
`qa160-nonexistent-0000`, so that no call could change anything. The WPF server gives two different
404 bodies. "no such endpoint" means the route is absent. "no recording with id" means the route exists
and refused the id. That difference is the positive control.

| Call (what the Avalonia app sends) | Status | Body `error` | Meaning |
|---|---|---|---|
| `POST /recordings/{id}/rename` | 404 | `no such endpoint: /recordings/qa160-nonexistent-0000/rename` | route absent |
| `DELETE /recordings/{id}` | 404 | `no such endpoint: /recordings/qa160-nonexistent-0000` | route absent |
| `POST /recordings/{id}/subtitles` | 404 | `no such endpoint: .../subtitles` | route absent |
| `POST /recordings/{id}/walkthrough` | 404 | `no such endpoint: .../walkthrough` | route absent |
| `POST /account/refresh` | 404 | `no such endpoint: /account/refresh` | route absent |
| `GET /account` (not called by the app) | 404 | `no such endpoint: /account` | route absent |
| control: `POST /recordings/{id}/subtitle` (WPF's singular route) | 404 | `no recording with id: qa160-nonexistent-0000` | route **present** |
| control: `GET /recordings/{id}` | 404 | `no recording with id: qa160-nonexistent-0000` | route present |
| control: `GET /recordings/{id}/transcript` | 404 | `no transcript for recording: qa160-nonexistent-0000` | route present |

I also compared the route tables in source. The WPF-only list (`RestServer.cs:98-229`) and the
headless-only list (`HeadlessControlServer.cs:127-153` plus the subroutes at `:188-221`) are exactly as
the inventory states. The one error is the "six" in 1.3 item 2.

### (c) "`DesktopApp.csproj:67` compiles `..\AgentEyes.App\TranscriptPresentation.cs`" - CONFIRMED

Line 67 reads `<Compile Include="..\AgentEyes.App\TranscriptPresentation.cs"
Link="Shared\TranscriptPresentation.cs" />`.

### The two narrower findings - CONFIRMED

- `PostRecordingPlugins.Configure` has one caller, `App.xaml.cs:110`. `PostRecordingPlugins.cs`
  is in `src/AgentEyes.App`, so `agenteyes serve` cannot even reference it.
- `HeadlessHost.cs:56` builds `new RepairService(() => svc.IsRecording)` and never sets
  `HousekeepingSettings`. The default is `new HousekeepingSettings()` (`RepairService.cs:133-134`).
  That property's own summary says the default is **report-only**, so under `serve`, **a disk cap the
  person set never deletes anything**. The inventory says "defaults"; "report-only" is the sharper and
  more important fact.

---

## 3. Job 3 - `agenteyes serve` on Windows, measured

### How it was run, and why that way

Two hazards ruled out running it straight from `6498aa8`:

1. Port 7882 belongs to the owner's running WPF app.
2. At `6498aa8`, a real `agenteyes` process cannot be pointed away from the owner's library. `serve`
   would have used the owner's `%LOCALAPPDATA%\AgentEyes` config, credential and log, and run its
   repair pass over `%USERPROFILE%\Videos\AgentEyes`. That pass backfills transcripts and titles, and it
   would have run alongside the WPF app's own pass on the same folders.

So `agenteyes.exe` was built from **`c595c41`**, the unmerged #161 branch. That branch adds
`AGENTEYES_REDIRECT_DATA` and `AGENTEYES_REDIRECT_VIDEOS`. It was built in a separate git worktree with
`dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:Platform=x64` (0 errors), then run
redirected to a scratch library on port **7891**. Its diff to `HeadlessHost.cs` against `6498aa8` only
adds the redirect's port refusal and log lines. The recorder, server and repair wiring are identical.
**This is a measurement of `serve` as on main plus #161's isolation, not of `6498aa8` itself.**

Paths below are shortened: `<scratch>` stands for the session scratch directory under `%TEMP%`.

### Start

```
$ AGENTEYES_REDIRECT_DATA=<scratch>\lib\data AGENTEYES_REDIRECT_VIDEOS=<scratch>\lib\videos \
    agenteyes.exe serve --port 7891
[redirect] LIBRARY REDIRECTED by AGENTEYES_REDIRECT_DATA/AGENTEYES_REDIRECT_VIDEOS: state -> <scratch>\lib\data\AgentEyes ; recordings -> <scratch>\lib\videos\AgentEyes ; log -> <scratch>\lib\data\AgentEyes\logs (pid 2628)
[ok] AgentEyes headless service listening on http://127.0.0.1:7891/
     recordings -> <scratch>\lib\videos\AgentEyes
     data       -> <scratch>\lib\data\AgentEyes
     [redirect] LIBRARY REDIRECTED ... - this is NOT the person's real library
     signed in to DevThrottle: NO - run 'agenteyes signin' (recordings will not be transcribed)
```

### Control API answers

```
GET /health  [200] {"ok": true, "app": "AgentEyes", "host": "headless", "platform": "windows"}
GET /version [200] {"app": "AgentEyes", "version": "1.11.11"}
GET /status  [200] {"State": "idle", ..., "LibraryRedirected": true, "RecordingsRoot": "<scratch>\\lib\\videos\\AgentEyes", ...}
GET /devices [200] monitors: DISPLAY1 1920x1080 primary, DISPLAY2 1920x1080 at -1920,-474;
                   mics: "Microphone (2- Realtek(R) Audio)"; cameras: "USB2.0 HD UVC WebCam";
                   systemAudio: "(WASAPI loopback of the default output)"
```

### A real screen recording, started and stopped

The request body is the one the Avalonia app sends (`ControlClient.StartAsync`: mode, screen, source,
mic).

```
POST /record/start {"mode":"video","screen":1,"source":"both","mic":"Microphone (2- Realtek(R) Audio)"}
  [200] State=recording Mode=video Source=mixed Dir=<scratch>\lib\videos\AgentEyes\2026-11-16_124440_video
GET /status (6 s later)  State=recording ElapsedSeconds=7.32
POST /record/stop {}
  [200] {"File": "...\\2026-11-16_124440_video\\recording.mp4", "DurationSeconds": 7.5, "Shots": 0}
GET /status  State=idle LastStopFailed=false LastStopError=null
```

### The file decodes

```
$ ffprobe -v error -show_entries format=duration,size,format_name:stream=index,codec_type,codec_name,width,height,r_frame_rate -of compact recording.mp4
stream|index=0|codec_name=h264|codec_type=video|width=1920|height=1080|r_frame_rate=30/1
stream|index=1|codec_name=aac|codec_type=audio|r_frame_rate=0/0
format|format_name=mov,mp4,m4a,3gp,3g2,mj2|duration=8.100000|size=403411
$ ffmpeg -v error -i recording.mp4 -f null -        -> no output, exit 0
$ ffmpeg -i recording.mp4 -map 0:v:0 -f null -      -> frame=214
$ ffmpeg -i recording.mp4 -af volumedetect -vn -f null -   -> mean -85.5 dB, max -66.8 dB
```

The near-silent audio is this laptop's known microphone fault (-96.7 dBFS), plus a quiet system output.
It is not a product defect. `thumb.jpg` was generated. I opened it and it shows the real desktop at
record time, so the capture is real and not blank. It is **not** attached here because it shows the
owner's screen. The manifest's post-processing: `mux` done, `thumbnail` done, `package` failed with
"Not signed in to DevThrottle". The scratch library has no credential by design, so that failure is
expected and is not a `serve` defect.

### How it stops on Windows - the gap this measurement found

A Windows host has to be stopped by the installer for an upgrade (criterion 4, row B8). That path was
measured too:

```
POST /record/start {"mode":"video","screen":1,"source":"system"}  [200]   (ffmpeg pid 2092, parent pid 2628)
$ taskkill /PID 2628              -> "SUCCESS: Sent termination signal" ... process still running, /status still "recording"
$ taskkill /F /PID 2628           -> terminated
ffmpeg 2092 is still running 3 s and 6 s later (orphaned, still grabbing the screen); I stopped it myself (I started it)
restart serve -> RepairService: "orphan ffmpeg reap examined 0 record(s)";
  "ResumeAsync: 2 recording(s) with unfinished post-processing"
  2026-11-16_124524_video: mux FAILED - "moov atom not found ... Invalid data found when processing input"
```

So on Windows today:

- **`serve` has no graceful stop.** It listens for SIGINT/SIGTERM through `PosixSignalRegistration`. A
  console process with no console window gets neither from `taskkill`.
- **The only stop that works, `/F`, loses the take in flight and orphans its ffmpeg**, which keeps
  recording the screen with no owner. `KillOnCloseJob` exists in Core but is used only by always-on.
- The WPF app gets this right through `QuitRequest.Listen` (B8). It is wired only in `App.xaml.cs:153`.

### Conclusion for job 3

**`agenteyes serve` does serve the Control API on Windows, answers `/health` and `/status`, and starts
and stops a real screen recording that decodes. The Lead's hypothesis holds at the engine level: the
cutover is a hosting problem first.** But the host as it stands is missing what makes it a Windows
product host:

1. a graceful stop channel (B8);
2. always-on (B6);
3. plugins (B9);
4. the person's housekeeping settings (B5, which is report-only today);
5. the auto-updater (B7);
6. the WPF-only routes the WPF UI features call: `/presets`, `/captures`, `/screenshot`, `/capture`,
   `/capture-info`, `/import`, `/always-on*`, `/housekeeping*` and `/transcripts/{id}/translate`;
7. autostart at login (the Avalonia app's Run-at-login writes an XDG file on Windows, T13/S1).

The Avalonia app also hard-codes `127.0.0.1:7882` (`ControlClient.cs:41`). It could therefore not be
pointed at this test host, and it was not driven against `serve` here.

---

## 4. Why "host-side" does not mean "comes with the host"

This is the one place where the Lead's framing needs correcting before it reaches the owner. Most of the
host-side code is **compiled into `src/AgentEyes.App`**, not into Core:

| Feature | Class | Lives in | Lines |
|---|---|---|---|
| Always-on controller (engine is in Core `AlwaysOn/`) | `AlwaysOnController` | `src/AgentEyes.App` | 376 |
| Presets model and store | `CapturePreset`, `PresetStore` | `src/AgentEyes.App` | 222 |
| Auto-update | `UpdateChecker` | `src/AgentEyes.App` | 279 |
| Plugins, registry, post-recording wiring | `Plugins`, `PluginRegistry`, `PostRecordingPlugins` | `src/AgentEyes.App` | 204 + 136 + 31 |
| The WPF Control API and its extra routes | `RestServer` | `src/AgentEyes.App` | 741 |
| Global hotkeys | `KeyboardHook`, `TriggerSpec` | `src/AgentEyes.App` | 130 + |
| Planned-stop listener wiring | `QuitRequest.Listen` call | `App.xaml.cs:153` | - |
| Diagnostics injection check | `InjectionVerifier` | `src/AgentEyes.App` | 76 |
| Housekeeping, video import, capture, self-test, repair | `HousekeepingSettings`, `VideoImport`, `CaptureService`, `SelfTest`, `RepairService` | **Core** | - |

Deleting `src/AgentEyes.App` deletes the first group. A Windows headless host gets them only if they are
**moved into Core and wired into `HeadlessHost` and `HeadlessControlServer`**. That is real porting
work, though porting code that already exists is much cheaper than writing it again. It does not come
free with "run `agenteyes serve` on Windows".

---

## 5. Job 4 - the 114 MISSING rows, classified

**Rule used.** A row is **HOST-SIDE** when what it describes runs with no window in the recorder
process: the engine, the API, a background pass, or a listener. A row is **UI-SIDE** when it is a
window, a control, a tray item, a notification, or something that needs the interactive desktop (a
global keyboard hook, an on-screen overlay). A UI row whose feature also needs a host route or service
that `serve` lacks today stays UI-SIDE, and is flagged **+host** in Appendix Q. That flag counts work the
UI number alone hides.

| Bucket | Count | Rows |
|---|---|---|
| **HOST-SIDE** | **11** | B1 B2 B3 B4 B5 B6 B7 B8 B9 B13 B17 |
| - of which already in `agenteyes serve` | 5 | B1, B2 (with the different route table, finding 2), B3, B4, B13 |
| - of which must move from `src/AgentEyes.App` into Core | 6 | B5 (settings wiring + `/housekeeping` routes), B6, B7, B8, B9, B17 |
| **UI-SIDE** | **103** | everything else (Appendix Q) |
| - of which **+host** (also needs a route or service `serve` lacks) | 38 | always-on view and tray (V80-V95, T7-T9, T19), capture view and triggers (V60-V72, T5, H1, H2, B19), import (V39) |
| **Total** | **114** | |

**Rows I am unsure of (14)**, each with the bucket I chose:

| Row | Chose | Why it is a judgement |
|---|---|---|
| H1, H2 | UI (+host) | A low-level keyboard hook needs an interactive-session process with a message loop, and it opens an overlay. It could live in a per-user host instead |
| B19, B20 | UI | They follow H1/H2 and the gallery. If the hooks go into the host, B19 becomes a host-to-app event |
| B7 | HOST | The updater has to know recording state and outlive the window. It could be app-side if the app asks the host |
| B17 | HOST | It arms the recording service's preview output from config. It is only meaningful with the HUD (U8) |
| V19 | UI | The headless server already accepts `mode: audio`. Screenshot mode needs `/screenshot`, which only WPF serves |
| V24 | UI | A notice. It needs the host to report post-recording failures in `/status` |
| S3 | UI | A settings control for a value that only the host uses |
| D2-D6 | UI | The test panel is UI. The tests themselves run recordings (`SelfTest` is Core; `InjectionVerifier` is App) |

**What the two numbers mean for the owner.** "Windows gets the same architecture as macOS and Linux"
turns 11 rows into a hosting job, and 5 of those already work. It does **not** shrink the other 103.
Presets, Capture, Always On, the HUD, Diagnostics, Plugins, Updates and Import are user interface. They
have to exist in the Avalonia app or be given up, whatever process hosts the recorder. 38 of those 103
also need host routes that only the WPF server has today.

---

## 6. Job 5 - issue #178 (Edit term wipes misheard forms)

**Confirmed from code, not observed at runtime.** At `MainWindow.axaml.cs:818-824` the code does three
things in order: `string? v = await Dialogs.PromptAsync(...)`, then `r.Variants.Clear();`
unconditionally, then `if (v != null) r.Variants.AddRange(...)`, then `await SaveDictionaryAsync();`.
`Dialogs.PromptAsync` returns null both for Cancel and for a blank answer (`Dialogs.cs:50`,
`return string.IsNullOrWhiteSpace(result) ? null : result;`), so Cancel saves an empty list. The prompt
itself says "empty for none", so a blank OK is meant to clear the forms. Cancel cannot be told apart
from that. I did not drive it at runtime. The Avalonia app writes the dictionary through
`DictionaryStore` into the real `%LOCALAPPDATA%\AgentEyes`, which holds the owner's dictionary, and at
`6498aa8` there is no redirect for it. Not fixed, per scope.

---

## 7. What this QA did not cover

- **I did not launch the WPF app from this build.** The owner's installed WPF app (pid 16160) holds the
  `AgentEyes-singleinstance` mutex and port 7882. A second WPF process cannot start beside it, and
  stopping the owner's daily tool was out of bounds. Finding 2 was therefore measured against the
  **installed** WPF app (version 1.11.11, the same version string as the source). I confirmed the route
  tables in source match what it served, but I cannot prove byte-for-byte that the installed binary is
  `6498aa8`.
- **I did not launch the Avalonia app.** It hard-codes 7882, so it would have driven the owner's real
  recorder and written the owner's real dictionary. It could not be pointed at the scratch `serve`.
- **I did not click through the WPF UI.** Section 1.4's label sweep stands in for that. Opening the
  owner's window would put it in front of whatever they were doing.
- **`serve` was measured from #161's commit `c595c41`, not from `6498aa8`**, for the isolation reason
  given in section 3.
- **I did not open the owner's library.** No recording, preset, dictionary or audio setting was read
  or changed. The two scratch recordings and the scratch data folder live under `%TEMP%` and are not
  committed.
- I re-checked a sample of the status claims by grep (Import, Plugin, AlwaysOn, Toast, Notification,
  Preset, Hud, Topmost, Discard, KeyboardHook, PrintScreen, UpdateChecker, QuitRequest, Camera,
  Highlight: all 0 in DesktopApp). V3 "for Linux" is at `MainWindow.axaml.cs:888`. **I did not re-verify
  all 36 PRESENT rows one by one.** A wrong PRESENT is the more dangerous error, and the sampled ones held.
- I did not run the heavy smokes. The change is docs-only.

---

## 8. What the developer needs to change (and only this)

1. E5 and Appendix A.1: 97 handlers (56 / 23 / 7 / 11). Map the 4 PresetEditor handlers to P7 and the 3
   PreviewWindow handlers to W4. Either compare end-of-media and playback-failure in W4, or say there
   that they were not compared. Fix the regex, or replace it with an instrument that is not a fixed list
   of event names.
2. P10: add "Set active" (`ManagePresetsDialog.cs:72`).
3. Finding 2: "six" -> "five".
4. W2: restate the grep (for example `Diagnostics window|TestPanel`). As written it hits
   `using System.Diagnostics`.
5. Optional: G1 mention drag-and-drop install. B5: say report-only, not just "defaults".

---

## Appendix Q - every MISSING row, bucketed

`H` = HOST-SIDE, `H*` = HOST-SIDE and already in `agenteyes serve`, `U` = UI-SIDE, `U+` = UI-SIDE and
also needs a host route or service `serve` lacks today, `?` = judgement call (section 5).

| Row | Bucket | Row | Bucket | Row | Bucket | Row | Bucket |
|---|---|---|---|---|---|---|---|
| W2 | U | V19 | U ? | V82 | U+ | G1 | U |
| W3 | U | V20 | U | V83 | U+ | G2 | U |
| W5 | U | V21 | U | V84 | U+ | G3 | U |
| W8 | U | V22 | U | V85 | U+ | G4 | U |
| W9 | U | V24 | U ? | V86 | U+ | G5 | U |
| W10 | U | V33 | U | V87 | U+ | G6 | U |
| W11 | U | V39 | U+ | V88 | U+ | D1 | U |
| W14 | U | V60 | U+ | V89 | U+ | D2 | U ? |
| W16 | U | V61 | U+ | V90 | U+ | D3 | U ? |
| W17 | U | V62 | U+ | V91 | U+ | D4 | U ? |
| T2 | U | V63 | U+ | V92 | U+ | D5 | U ? |
| T4 | U | V64 | U+ | V93 | U+ | D6 | U ? |
| T5 | U+ | V65 | U+ | V94 | U+ | D7 | U |
| T7 | U+ | V66 | U+ | V95 | U+ | D8 | U |
| T8 | U+ | V67 | U+ | S2 | U | D9 | U |
| T9 | U+ | V68 | U+ | S3 | U ? | U1-U11 (11 rows) | U |
| T11 | U | V69 | U+ | S5 | U | B1 | H* |
| T12 | U | V70 | U+ | P1 | U | B2 | H* |
| T18 | U | V71 | U+ | P2 | U | B3 | H* |
| T19 | U+ | V72 | U+ | P3 | U | B4 | H* |
| H1 | U+ ? | V80 | U+ | P5 | U | B5 | H |
| H2 | U+ ? | V81 | U+ | P6 | U | B6 | H |
| V11 | U | | | P7 | U | B7 | H ? |
| V12 | U | | | P8 | U | B8 | H |
| V13 | U | | | P9 | U | B9 | H |
| | | | | P10 | U | B12 | U |
| | | | | | | B13 | H* |
| | | | | | | B15 | U |
| | | | | | | B17 | H ? |
| | | | | | | B19 | U+ ? |
| | | | | | | B20 | U ? |

Totals: H* 5 + H 6 = **11 HOST-SIDE**; U 65 + U+ 38 = **103 UI-SIDE**; 114 in all.
