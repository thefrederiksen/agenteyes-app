# Issue #116 - QA, macOS, pass 3

**No defect found on macOS. Labels left exactly as they are (`flow:ready-qa`).** Windows and Linux are
not claimed here.

## Who measured what - read this first

Two QA seats produced this report.

- **The preceding seat** took the macOS measurements on 2026-11-16 between 08:58 and 09:10 EST and
  was then parked before it could write them up. Its evidence is `mac-pass3-evidence.txt`,
  `mac-pass3-screenshot-question.txt`, `mac-pass3-gate.txt`, `mac-pass3-linkage.txt`, the tools in
  `mac-pass3-capture-tools/` and the nine `mac-pass3-*.png` / `.jpg` images.
- **I am the continuation seat.** I wrote those measurements up. I did not redo them, and they are
  not mine. I read every file and opened every image; none is blank or black.
- Anything I measured myself is marked **[continuation]**. It is in `mac-pass3-cont-evidence.txt`,
  `mac-pass3-cont-tools/` and three `mac-pass3-cont-*.png` images, taken 11:23 - 11:28 EST.

I ran four things of my own, because the evidence had gaps I could close:

1. The macOS gate again (criterion 12).
2. The **clean quit**, which the preceding seat did not exercise: it stopped the app with SIGTERM.
3. One reproduction of the locked-screen capture method, from the committed tools alone.
4. Read-only facts about the installed binary: its sha256 and its code signature.

| | |
|---|---|
| Machine | `devthrottle-mac-mini`, macOS 26.5.2 (25F84), arm64, display 1920x1080 |
| Head under test | `02b6f37` (code `e5ce76c`) |
| Commit embedded in the binaries the preceding seat ran | `881e93b`: `agenteyes.dll 1.11.11+881e93b8e86dff870a65230fe8cd4f790a52a93b`, `AgentEyesDesktop.dll 1.0.0+881e93b8...`. That was the branch head at the time, not `02b6f37`. `git diff --stat e5ce76c 881e93b -- src tests scripts .github` is empty (recorded by the preceding seat; re-run by me, still empty) |
| Commit embedded in the binaries I ran | the same `881e93b`, built before I pulled `f84a6c6` (also documents only) |
| Lock state | `"CGSSessionScreenIsLocked"=Yes` at **08:58:09 EST**, re-measured Yes at 09:01:25, 09:04:31, 09:04:40, 09:05:02, 09:06:50, 09:09:30. **[continuation]** Yes at 11:23:51 and at the start of each of my four runs |
| Toolchain | `dotnet 10.0.403`, no .NET 8 SDK; `DOTNET_ROLL_FORWARD=LatestMajor` in the shell only |
| Recorder service | the installed launchd agent `com.agenteyes.recorder`, pid 50642. Not reinstalled, not restarted, not touched |

## Verdict per criterion (macOS half)

| # | Criterion | Verdict | Measured by |
|---|---|---|---|
| 2 | macOS builds and opens its window, with a screenshot | **PASS** - the screenshot owed since pass 1 now exists | preceding seat; second window capture mine |
| 8 | macOS links its own backend | **PASS**, with a positive control | preceding seat; counts re-read by me |
| 9 | The app shows the recorder's live state while `/health` is ok | **PASS** - window captured mid-recording between two `ok:true` | preceding seat |
| 10 | The menu bar, reported | **REPORTED IN PART.** The icon exists and follows the recorder's state. Whether its menu opens on screen and whether a click raises the window are **NOT OBSERVED** | preceding seat; item count and menu model mine |
| 11 | Single instance, and the clean quit (as amended) | **PASS, with a stated limit.** Hand-off: 5 of 5, then 4 of 4. Clean quit: 3 of 3, reached at the menu item's managed click, not by a mouse | hand-off: preceding seat and mine; **clean quit: mine only** |
| 12 | macOS gate green | **PASS** - 226 / 291 + 1 skipped / 13 | preceding seat; re-run by me, same counts |

Criteria 1 and 13 were not measured by either macOS seat in this pass. For 13, I only looked the run
up: run 43873340100 at `02b6f37` shows `windows success`, `linux success`, `macos success`.

## 1. The headline: a window CAN be photographed on this Mac while the screen is locked

Every earlier macOS pass on this issue was stuck behind the locked screen. The preceding seat found
a way through. It is written out in full here because every later macOS criterion in this mission
depends on it. Source: `mac-pass3-screenshot-question.txt`.

### The recipe

1. **Find the window id without any grant.** `mac-window-probe.swift` lists windows through
   `CGWindowListCopyWindowInfo`. Owner, pid, layer, bounds and window id come back without the
   Screen Recording grant; only the window name is withheld.
2. **Build the hook.** `mac-pass3-capture-tools/StartupHook.cs` is a .NET startup hook (a `net8.0`
   class library with `StartupHook.Initialize()`).
3. **Run the INSTALLED recorder binary as a one-shot launchd job with the hook loaded.**
   `oneshot.py <label> <timeout> <outdir> <argv-json> <env-json>` writes a plist (`RunAtLoad`,
   `LimitLoadToSessionType=Aqua`, `ProcessType=Interactive`), bootstraps it into `gui/501`, polls
   `launchctl print` until it reads `not running` with a `last exit code`, stops at a hard timeout,
   and **always** boots the job out. It then checks the label is gone (`print` exit 113).
   - argv: `<install>/bin/agenteyes --version`
   - env: `DOTNET_STARTUP_HOOKS=<QaCaptureHook.dll>`, `QA_OUT`, `QA_CAPTURES='name|args;name|args'`
4. **Inside that process** the hook reads `CGPreflightScreenCaptureAccess()`, starts
   `/usr/sbin/screencapture -x -o -l <windowid> <file>` for each spec with its own 20 s kill timeout,
   writes a report, and calls `Environment.Exit(0)`.

Why it works: macOS charges the capture to the process that holds the Screen Recording grant. The
job IS the installed `agenteyes` binary, so the `screencapture` it starts is covered by its grant.

### What the preceding seat measured

| Where the question was asked | `CGPreflightScreenCaptureAccess()` | Artefact |
|---|---|---|
| The QA seat's own shell | `false` | section A |
| A one-shot launchd job running a plain probe binary (pid 29240, ppid 1) | `false` | section A |
| A one-shot launchd job running the installed `agenteyes` with the hook (pid 29303) | **`True`** | section C |

| Capture, screen locked | Result | Time |
|---|---|---|
| `screencapture -x -o -l 711` (the app window, by id) | **exit 0**, 74194 bytes, 1280x748 | **88 ms** |
| `screencapture -x -R 320,162,1280,748` (the same rectangle of the screen) | **exit 1**, `could not create image from rect` | 49 ms |
| `screencapture -x -o -l 711` during a recording | exit 0, 61886 bytes | 59 ms |
| `screencapture -x` (the whole screen) | exit 0, 3338387 bytes, 1920x1080 | 108 ms |
| Ten status-item windows and the Menubar window, by id | exit 0 each | 43 - 75 ms |

So, while locked: **a window can be captured by its id; a rectangle of the screen cannot.** The
whole-screen capture succeeds but holds no window. I read `mac-pass3-lockscreen-fullscreen.jpg`: it
is the wallpaper (a lake with rocks), with no app window, no menu bar and no Dock. That matches the
frame pass 1 pulled from a recording. I see no clock or sign-in field in it, so I describe it as
"the wallpaper with nothing on it" and not as the lock screen's own interface.

Nothing hung. Every job reached `not running` in 0.5 - 1.0 s and every label was booted out.

### [continuation] Reproduced once from the committed tools

I rebuilt `QaCaptureHook` from the committed source without changing it, reused `oneshot.py` as it
is, and ran three jobs (labels `com.agenteyes.qa116.cont-r1`, `-r2`, `-r2-b`):

- `CGPreflightScreenCaptureAccess() = True` in all three.
- `screencapture -x -o -l 733`: exit 0, 86 ms, 577729 bytes, 1280x748.
- I read the result, `mac-pass3-cont-window-library.png`: the Library view, "79 recordings", real
  thumbnails, the four recordings of 9:02 - 9:06 AM at the top. Not blank.
- All three labels gone afterwards (`print` exit 113; `launchctl list | grep -c qa116` = 0).

**The method is usable by a seat that was not the one that invented it.** I did not re-run the two
`false` arms or the failing rectangle capture; those stand on the preceding seat's evidence alone.

### The method's own limits

1. **It borrows the installed binary's grant.** The installed binary was not changed: sha256
   `348b93b8...7022` "UNCHANGED before and after" (preceding seat). **[continuation]** I measured the
   full hash, `348b93b81d867fc34529fc073bf93c0d23d9428923dbe65f3a5e36a1d2607022`, before my first job
   and after each of my three; it never changed. The job changes no file of the install and no
   permission entry, and it never touches the `com.agenteyes.recorder` agent.
2. **The hook exits before the recorder's `Main` is reached.** `StartupHook.Initialize()` ends in
   `Environment.Exit(0)` on every path, and each hook report ends with
   `Environment.Exit(0) - the recorder's Main is never reached`. A startup hook runs before `Main`
   by design. The argv is `--version` in any case, so a hook that failed to load would print a
   version and exit, not start a second recorder. The recorder service stayed pid 50642 throughout.
3. **It works only on a binary that accepts a startup hook, and the installed one is not signed
   with the hardened runtime.** **[continuation]** `codesign -dvvv` on the installed binary reads
   `flags=0x2(adhoc)`, `Signature=adhoc`, `TeamIdentifier=not set`. The hardened-runtime flag is
   not set and there is no library validation.
   - **#118 will sign and notarise. That may BREAK this technique, and it would also close the
     grant-borrowing hole.** Both halves matter: today any process running as this user can borrow
     the recorder's Screen Recording grant exactly the way QA just did.
   - **What is NOT established, and I am not going to assert it:** nobody has run this against a
     hardened-runtime build. My own reading, untested: the hardened runtime restricts native code
     injection, while `DOTNET_STARTUP_HOOKS` is read by the .NET runtime itself and loads a managed
     assembly. So signing alone may not stop the hook. The switch that certainly stops it is the
     runtime's own (`StartupHookSupport=false` at publish). Separately, re-signing changes the
     identity the grant is recorded against, so the grant itself may need the owner again.
   - **So #118 owes two things:** a deliberate decision on startup hooks in the shipped binary, and
     a re-run of this recipe on the first signed build before any macOS QA is planned around it.
4. **It cannot click.** Inside the granted process `AXIsProcessTrusted() = False` and
   `CGPreflightPostEventAccess() = False`. It photographs; it cannot press a menu item.
5. **It proves a window renders, not that a person sees it.** A capture by id reads the window's
   own pixels. It says nothing about what is in front of it, and the screen was locked throughout.

## 2. Criterion 2 - macOS builds and opens its window, with a screenshot: PASS

Measured by the preceding seat (`mac-pass3-evidence.txt` sections 1 - 3).

| | Expected | Actual |
|---|---|---|
| Build | `Build succeeded.`, `0 Error(s)` | `dotnet build src/AgentEyes.DesktopApp -c Release` at 08:58:59, exit 0: `Build succeeded.`, `0 Error(s)`, `1 Warning(s)` (CS8602 at `AlwaysOnEngine.cs:901`, also on main) |
| Log line | `[MainWindow] Opened` with a timestamp | `09:01:08.518 [pid 27892] [INFO] [MainWindow] Opened` |
| Window server | a real window | `pid=27892 owner="AgentEyesDesktop" window=711 layer=0 onscreen=true alpha=1.0 bounds=1280x748 at 320,162`; the same probe on the recorder (no window) answers `[EMPTY]`, exit 2 |
| Screenshot | the window, rendered | **`mac-pass3-window.png`**, window 711 captured by id at 09:04:40.828, 1280x748 |

I read `mac-pass3-window.png`: title bar "AgentEyes"; the Record view; a red REC button; Screen
`Monitor 1 - 1920 x 1080 (primary)`; Microphone `No microphone`; System sound on; VIDEO 30 fps ON,
MIC OFF, SYSTEM ON; the left rail with Record, Library, Dictionary, Sign in, Settings, Help. It is
fully rendered.

How it was captured, as the issue asks: by window id, from the one-shot launchd job in section 1.

`mac-pass3-selfrender-record.png` is a different thing and must not be mistaken for a screenshot. It
is the app's own `--snap` render (1280x720, no title bar), written by the app at 09:03:30.435. It
shows the same view. It proves the app's render path, not the window server's pixels.

**[continuation]** A second, independent launch: `11:26:36.887 [pid 72087] [MainWindow] Opened`,
window 733, captured as `mac-pass3-cont-window-library.png` (section 1).

## 3. Criterion 8 - macOS links its own backend: PASS

Measured by the preceding seat (`mac-pass3-linkage.txt`), with the CI step `ci.yml:167-184` verbatim.

| Engine | x11grab | pulse | v4l2 | xrandr | avfoundation | screencapture | Check |
|---|---|---|---|---|---|---|---|
| The macOS build made on this Mac | 0 | 0 | 0 | 0 | 2 | 4 | `[OK] the macOS desktop app links the macOS engine and carries libvlc`, exit 0 |
| **Positive control:** the same output with the Linux-configuration engine swapped in | 1 | 8 | 1 | 4 | 0 | 0 | five `[FAIL]` lines, **exit 1** |

The absence is paired with a presence, and the check fires on a bad input in the same command.

**[continuation]** After my own gate run and rebuild, the app output reads
`x11grab 0, pulse 0, v4l2 0, xrandr 0, avfoundation 2, screencapture 4`, with `libvlc/osx-arm64` and
`libvlc/osx-x64` present. I did not re-run the positive control.

## 4. Criterion 9 - the app shows the recorder's live state while /health is ok: PASS

Measured by the preceding seat (`mac-pass3-screenshot-question.txt` section D). A real screen-only
recording was started through the Control API, not through the window, while the screen was locked:

```
09:05:02 POST /record/start screen-only   -> State recording, Dir .../2026-11-16_090503_video
09:05:09 GET /health {"ok":true,"app":"AgentEyes","host":"headless","platform":"macos"}
09:05:09 GET /status recording 6.72
09:05:10.046 [hook] win-recording: exit=0 ms=59 file_bytes=61886        <- the capture
09:05:10 GET /health {"ok":true,"app":"AgentEyes","host":"headless","platform":"macos"}
09:05:10 GET /status recording 7.34
09:05:10 POST /record/stop -> DurationSeconds 7.36
```

**Expected:** the window shows the recorder's state from the Control API, not "not answering",
while `/health` returns `ok: true`.
**Actual:** `mac-pass3-window-recording.png`, captured between two `ok:true` answers. I read it: the
button reads **STOP**, with **REC 00:06** under it, and the Screen and Microphone boxes are greyed
out. `/status` said 6.72 s one second earlier. The window did not start that recording, so the
state on screen can only have come from the recorder.

The app's own log agrees: `09:01:08.723 [AppState] PollAsync: service answering, state=idle`,
`GetDevicesAsync: 1 monitor(s), 1 mic(s)`, and `Render: idle -> recording` / `recording -> idle`
around each recording.

Two things the picture also shows, neither a failure: the rail reads "Sign in" with a warning badge
(`/status` says `SignedIn: false` on this machine), and both level meters are flat (the recording
had no audio source, deliberately).

## 5. Criterion 10 - the menu bar: REPORTED IN PART

**A negative finding passes this criterion. An unanswered question does not, and two of its three
questions are unanswered on macOS.** I am not calling this a pass; the lead decides.

### Why pass 1 saw no menu-bar item

On macOS 26 **every layer-25 status item is a window owned by Control Center (pid 431), not by the
app.** Pass 1 probed the app's own pid and found none, and could not tell "absent" from "owned by
someone else". The preceding seat listed all windows instead: ten layer-25 windows, all
`owner="Control Center"`, plus `pid=171 "Window Server" window=26 layer=24 ... name=Menubar`.

### What is established

| Question | Answer | Evidence |
|---|---|---|
| Does the icon exist? | **Yes.** One status-item window appears when the app starts | **[continuation]** exactly one new layer-25 window appeared on launch in each of my four runs, at `38x30 at 1424,0` (ids 734, 737, 742, 745); 9 before the first launch, 10 after |
| Is it the app's icon? | **Yes.** Idle it is a crescent moon with a dot; recording it is a red dot | `mac-pass3-menubar-item-idle-x6.png`, `mac-pass3-menubar-recording.png`. That is exactly what `TrayController.cs` draws: `DrawMoon` for idle, `DrawDot` for recording |
| Does it follow the recorder? | **Yes.** Only that one window changes between idle and recording; the nine others are byte-identical | sha1 of window 712: idle `3d084603...`, recording `562448ea...`, after exit `3cbdf379...`; the nine others "same" |
| What happens when the app exits? | The icon goes blank. The window stays listed for a while, off-screen | `mac-pass3-menubar-item-after-exit.png` (703 bytes, blank). **[continuation]** same after a clean quit: byte-identical blank image, `onscreen=false`; earlier ones are later removed |
| What is in its menu? | The same seven entries as on Linux, with a live status line | **[continuation]** read from the running app: `Ready to record` (disabled), separator, `Start recording`, `Show AgentEyes`, `Open recordings folder`, separator, `Quit AgentEyes (the recorder keeps running)` |

**[continuation]** My idle capture of the item is byte-identical to the preceding seat's
(sha1 `3d08460349df6e0e2ddc9e283ef29cd6781d4a95`), in a different launch with a different window id.

### What is NOT established

- **Whether the menu opens on screen.** Not observed. The menu entries above were read from the
  app's own menu object, not from a menu drawn by macOS.
- **Whether a click raises the window.** Not observed. No input was synthesized: the granted process
  has neither Accessibility nor event-posting rights, the screen is locked, and the project rules
  forbid synthesizing input on the owner's desktop unasked. `TrayController.cs` sets both a `Menu`
  and a `Clicked` handler on one icon, which is the combination the issue flags for macOS. The
  question the issue most wanted answered is still open.
- **Whether a person sees the icon.** The screen was locked. And the evidence disagrees with itself:
  the preceding seat's list at 09:06:50 shows the app's item, window 712, as **`onscreen=false`**
  while the app was running, with the nine others `onscreen=true`. In my four runs the app's item
  was **`onscreen=true`** while running and `false` only after exit. I cannot explain the difference
  and I am not going to pick the answer I prefer.

### The menu-bar pictures are reconstructions, not photographs

`mac-pass3-menubar-idle.png`, `mac-pass3-menubar-recording.png` and `mac-pass3-menubar-full-idle.png`
were **assembled** by `strip.swift` and `compose.swift`: each status-item window, captured by id,
pasted at its x position onto a flat dark fill. They show what each item renders. They do not show
what the screen looked like, and the background colour is the tool's, not the menu bar's. So they
say nothing about contrast or legibility on the real bar. The `-x6` images are 6x enlargements.

## 6. Criterion 11 - single instance, and the clean quit: PASS, with a stated limit

### Hand-off - measured by the preceding seat, repeated by me

**Expected:** the second launch raises the first window and exits, with a log excerpt from both.
**Actual (preceding seat, `mac-pass3-evidence.txt` sections 4 and 5):** five second launches, every
one `exit=0`, every one accepted and acted on by pid 27892:

```
09:03:04.061 [pid 28813] [SingleInstance] TryAcquire: another instance is running - sent it 'view:library'
09:03:04.061 [pid 27892] [SingleInstance] Accept: 'view:library'
09:03:04.061 [pid 27892] [App] Run: view:library
09:03:04.061 [pid 27892] [App] ShowWindow
09:03:04.062 [pid 27892] [MainWindow] RunCommand: view library
09:03:04.062 [pid 27892] [MainWindow] Rail_Changed: Library
```

The others: `snap:` at 09:03:06, `view:record` at 09:03:28, `snap:` at 09:03:30 and 09:03:37. One
`AgentEyesDesktop` process remained.

**[continuation]** Four more, one per run, all `exit=0`, same six-line sequence, one process left.

### The clean quit - the preceding seat did NOT measure it

Its evidence is explicit: `09:07:53 kill -TERM 27892 (NOT the menu-bar Quit - that needs a click,
see report)`. The report it points to was never written. What that run shows is the opposite of a
clean quit: the process was gone at once, no log line was written after the signal, and **the
socket was still there afterwards**. That is expected - `Main`'s `finally` does not run on SIGTERM -
but it left criterion 11 as amended unmeasured on macOS. It has been open since developer pass 2,
whose fix for the macOS `Dispose` hang had never run in the real app on a Mac.

### [continuation] The clean quit - 3 of 3

Quit lives only in the menu-bar menu (`App.axaml.cs`, `ShutdownNow`, the one `Shutdown()` call
site). I could not click it, so I raised the click one level down.

**How Quit was reached.** A startup hook in the desktop app **built from the branch** (never the
installed recorder; no product code changed; `mac-pass3-cont-tools/QuitHook.StartupHook.cs`). On a
trigger it finds the tray icon's menu on the UI thread and calls
`Avalonia.Controls.INativeMenuItemExporterEventsImplBridge.RaiseClicked()` on the Quit item. That
interface is what Avalonia's macOS backend calls when a person picks a menu item
(`Avalonia.Native.dll` carries both `RaiseClicked` and `MenuActionCallback`). From there on it is
the product's own path: the item's `Click` handler, `DeferredQuit`, `ShutdownNow`, `Main`'s
`finally`, `SingleInstance.Dispose`. No kill and no signal.

| run | pid | `[App] Quit` | `Main: exited` | exit code | socket while running | socket after | lines after `[App] Quit` |
|---|---|---|---|---|---|---|---|
| r2 | 73182 | 11:27:41.375 | 11:27:41.399 | **0** | present | **absent** | 6 |
| r3 | 73582 | 11:27:58.619 | 11:27:58.648 | **0** | present | **absent** | 6 |
| r4 | 73762 | 11:28:02.703 | 11:28:02.744 | **0** | present | **absent** | 6 |

Run r2 in full, every line from that pid after the trigger, unfiltered:

```
11:27:41.375 [pid 73182] [DeferredQuit] Request: shutdown posted to run after the menu has closed
11:27:41.375 [pid 73182] [DeferredQuit] Run: shutting down
11:27:41.375 [pid 73182] [App] Quit (the recorder service keeps running)
11:27:41.398 [pid 73182] [App] Quit: lifetime shutdown requested
11:27:41.398 [pid 73182] [DeferredQuit] Run: shutdown returned
11:27:41.399 [pid 73182] [SingleInstance] Dispose: socket <TMPDIR>/agenteyes-app.sock
11:27:41.399 [pid 73182] [SingleInstance] Accept: ''
11:27:41.399 [pid 73182] [SingleInstance] Dispose: socket listener closed and <TMPDIR>/agenteyes-app.sock removed
11:27:41.399 [pid 73182] [Program] Main: exited
```

- **The macOS `Dispose` hang is gone in the real app.** `Dispose` returned within 2 ms in all three
  runs. Before the pass-2 fix it never returned on macOS.
- **The wake-up connection delivers no command.** The six lines after `[App] Quit` are the same six
  Linux recorded. There is no `[App] Run:` and no `ShowWindow` after any Quit. The same logs hold
  the real `Accept: 'view:library'` -> `Run` -> `RunCommand` sequence, so the instrument does see
  those lines when they happen. No line after the trigger lacked its pid tag (0 in each run).
- **No WARN and no ERROR** from any of the three pids. Guard lines: 1 `Install` each, nothing else.

**The known-bad arm.** My first run, r1, is kept in the evidence because it shows these instruments
fail when the exit is not clean. My hook had a bug of its own (a wrong namespace), no click was
raised, and my script killed the app after 30 s. Result: **exit 137, zero lines after the trigger,
the socket left behind.** That was my harness, not the product. The preceding seat's SIGTERM is a
second bad arm with the same signature.

**The stated limit.** This is the menu item's click as the app receives it. It is **not** a mouse
click: the status item, the menu macOS draws, and its tracking loop were not exercised. Linux's
equivalent went over D-Bus, the real transport; this one stays inside the process. Three runs, on an
idle recorder, with the main window open.

## 7. Criterion 12 - the macOS gate: PASS

Measured by the preceding seat (`mac-pass3-gate.txt`, 09:08:41) and re-run by me at 11:25:17
(`mac-pass3-cont-evidence.txt` section 1). The counts are identical.

| Command | Preceding seat | [continuation] |
|---|---|---|
| `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac` | `Build succeeded.`, `0 Error(s)`, exit 0 | `Build succeeded.`, `0 Error(s)`, exit 0 |
| `dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release` | `Failed: 0, Passed: 226, Skipped: 0, Total: 226` | `Failed: 0, Passed: 226, Skipped: 0, Total: 226` |
| `dotnet build ... -p:AgentEyesUnixPlatform=linux` | `Build succeeded.`, `0 Error(s)` | `Build succeeded.`, `0 Error(s)`, exit 0 |
| `dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release` | `Total 292, Passed 291, Skipped 1` | `Failed: 0, Passed: 291, Skipped: 1, Total: 292` |
| extra: `dotnet test tests/AgentEyes.Tests.DesktopApp/...` | `Failed: 0, Passed: 13, Total: 13` | `Failed: 0, Passed: 13, Total: 13` |

The one skip is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, which skips on a
host that is not Linux. All 13 `DesktopAppQuitTests` are named as Passed in the preceding seat's
listing. Every count is on roll-forward to the .NET 10 runtime.

Both seats rebuilt the macOS app after the gate, because the gate rewrites the app output as a
Linux-flavoured one. The linkage counts in section 3 were read after that rebuild.

## 8. What is NOT covered

- **A physical click on the menu-bar icon, the menu as macOS draws it, and whether a click raises
  the window.** Criterion 10's two open questions.
- **How any of this looks on an unlocked screen.** Every measurement was taken locked.
- **Whether the menu-bar icon is legible on the real bar.** The composites use a synthetic fill.
- **Quit by any other route.** The Dock, the application menu and Command-Q were not tried. Whether
  those reach `DeferredQuit` and `Dispose` on macOS is unknown. SIGTERM and SIGKILL are known to
  leave the socket behind; the next launch recovers it (`stale socket ... - taking it over`, seen at
  09:01:06, 11:26:36 and 11:27:37).
- **A recording with a microphone, playback, sign-in, or a saved setting.** The recordings were
  screen-only.
- **Starting or stopping a recording from the window or the menu.** They were driven through the API.
- **The `.app` bundle, the Dock identity, signing.** Phase 2. The app ran as a bare executable from
  the build output.
- **The capture method against a hardened-runtime build.** Section 1, limit 3.
- **The two `false` arms of the grant test and the failing rectangle capture** were not re-run by me.
- **Criterion 1 and criterion 13** were not measured on macOS this pass.
- **Windows and Linux.** Nothing here speaks for them.

## 9. Gaps and oddities in the preceding seat's evidence, stated rather than smoothed over

1. **"See the report, section 5"** in its section 6 points at a report that was never written.
2. **The clean quit was not measured** (section 6 above). Closed by my runs.
3. **Window 712 reads `onscreen=false` while the app was running.** Unexplained (section 5 above).
4. **Its sections are lettered A, C, D, E-H. There is no B.** Whatever B was is not on disk.
5. **Not kept:** the full-size `fullscreen.png` (the committed `.jpg` is a 960x540 reduction) and the
   app's own render taken during recording (`snap-rec`, 46188 bytes).
6. **It built `881e93b`, not `02b6f37`.** The code is the same; the embedded commit differs from the
   one Linux and Windows QA quote.
7. **It made four recordings, not one.** All screen-only, all under `~/Movies/AgentEyes`:
   `2026-11-16_090238_video` (8.74 s), `090331_video` (6.84 s), `090503_video` (7.36 s),
   `090652_video` (5.96 s). All four are still there. `PendingTranscriptions` went from 62 to 66, so
   they are in the owner's transcription queue.

## 10. Observations, not failures of this issue

- **The app rewrote the saved microphone to empty on open.** `09:01:08.098 [AppSettings] Load:
  screen=1 mic=Yealink UH36`, then `09:01:09.259 [AppSettings] Save: screen=1 mic= `. The Yealink was
  not connected, so the list had no such entry, and `SaveSetup` (`MainWindow.axaml.cs:250-257`)
  stored the empty selection. A user who opens the app with their headset unplugged loses the
  choice. On this machine the lost value had been written by the 13 November experiment, not by the
  owner. It belongs with the settings finding Windows QA raised, not here.
- **The settings file on macOS is `linux-app.json`**, as already ruled.
- **The guard's `Install` line is logged on macOS too** (`guarding Avalonia.Win32.TrayIconImpl+
  TrayPopupRoot`). I saw no other guard line in any run. Unlike Linux QA, I did not prove it inert
  with an instrumented build.
- **A status-item window of a finished app stays listed, off-screen and blank, for a while.** It is
  Control Center's window, and earlier ones do disappear.

## 11. The machine - what was done to it and how it was left

By the preceding seat, from its evidence:

- Built and ran the desktop app (pid 27892) and stopped it with SIGTERM at 09:07:53. That left a
  stale socket file.
- Made the four recordings listed above through the Control API. None was deleted.
- Ran its one-shot launchd jobs under `com.agenteyes.qa116.*`. Each was booted out.

By me:

- Ran the gate and rebuilt the macOS app. Working tree clean apart from the evidence files.
- Launched the desktop app four times (pids 72087, 73182, 73582, 73762). None is running now.
- The stale socket is gone: my first launch took it over and my clean quits removed it.
- Three one-shot launchd jobs, all booted out. `launchctl list | grep -c qa116` = 0.
- **Started no recording, deleted nothing, killed no ffmpeg, ran no `screencapture` from my shell.**
- **`com.agenteyes.recorder` was not touched**: still pid 50642, `/health` ok, `/status` idle, and
  the installed binary's sha256 is unchanged.
- The app saved its settings file on each open, with the same values it loaded.
- One status-item window from my last run (745) was still listed, off-screen, when I finished.
