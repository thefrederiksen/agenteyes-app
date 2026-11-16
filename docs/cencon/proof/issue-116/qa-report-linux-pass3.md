# Issue #116 - QA, LINUX, pass 3: re-verification after the Windows tray-Quit fix

An independent Linux QA seat. Scope: **Linux only**. This pass checks that the Windows-only fix in shared
code (`TrayPopupCloseGuard` plus `DeferredQuit`, commit e5ce76c) did not break the Linux baseline. That
baseline is `qa-report-linux-criterion11.md`: criterion 11 passed 4 of 4 through the real D-Bus tray menu
at 0e1b889. Windows QA runs in parallel on another machine. macOS is blocked. Neither is claimed here.

| | |
|---|---|
| Machine | devlinux - Ubuntu, GNOME on Xorg, DISPLAY=:0, 32 cores |
| Date | 2026-11-16, 08:42 - 08:53 EST |
| Head tested | `issue-116-desktop-app` (PR #141) at **02b6f3725cfc7fbfcde182f83a2c959678fca19e** (the handoff commit; `git diff --stat e5ce76c 02b6f37` = `handoff.md` only, so the code is **e5ce76c**) |
| Build identity | Read from the assembly, not a hash. `AgentEyesDesktop.dll` InformationalVersion `1.0.0+02b6f3725cfc7fbfcde182f83a2c959678fca19e`. Its engine `agenteyes.dll` `1.11.11+02b6f3725cfc7fbfcde182f83a2c959678fca19e`. The fix's log strings are in the dll: all four `[DeferredQuit] ...` and all five `[TrayPopupCloseGuard] ...` strings |
| App under test | Published from this head with install.sh's own publish command (`dotnet publish ... -r linux-x64 --self-contained true -p:Platform=x64`) into a scratch folder. Not installed |
| Load | 1-min load 2.28 - 4.49 during the runs, 32 cores |
| Raw evidence | `linux-pass3-evidence.txt`: both scripts, every console line, and every log line of every instance. Screenshots: `linux-pass3-window.png`, `linux-pass3-c3-window.png`, `linux-pass3-desktop-entry-window.png` |

## Verdict per item

| # | Item | Result |
|---|---|---|
| 1 | Criterion 11 through the real tray menu (dbusmenu `Event(id,"clicked")`), 4 runs | **PASS, 4 of 4.** Exit 0, `[App] Quit`, both `Dispose` lines, `Main: exited`, and the socket present while running and gone after. Two more tray Quits outside the 4 runs (criteria 3 and 6) also exited cleanly |
| 2 | Post-quit wake-up check, every line after `[App] Quit`, no filter | **PASS. 6 lines in each run** (baseline 4). The 2 extra lines are the 2 new log statements this commit adds. 0 `[App] Run`, 0 `ShowWindow`, 0 window mapped during the quit |
| 3 | The guard does not fire on Linux | **PASS, proved with instruments.** `Avalonia.Win32.dll` is never loaded into the process, while X11 and FreeDesktop are. Guard log lines: 1 `Install`, 0 `guarding`, 0 `closing`, 0 `cancelled`. An instrumented scratch build shows the handler is reached, and that its lines do land in this log when the filter is forced to match |
| 4 | DesktopApp tests run in the Linux suite; mutation caught | **PASS.** `AgentEyes.Tests.Linux`: **Passed 292, Failed 0**. All 13 `DesktopAppQuitTests` are named in the output. Two mutations of my own were each caught by a named test with its message |
| 5 | Criteria 3, 8, 9, 10 at this head; criterion 6 | **3 PASS, 8 PASS, 9 PASS, 10 reported (protocol level; the Linux tray did not change). 6 CARRIED FORWARD, NOT re-run**, by the lead's decision (reason below) |

**No defect found. Labels left as they are** (`flow:ready-qa`), as instructed.

## 1. Criterion 11 - four runs through the real tray menu

### How Quit was reached

Through the menu the app itself publishes: the StatusNotifierItem's `com.canonical.dbusmenu` object. The
helper reads the Quit entry's id from `GetLayout`, where the label reads
`'Quit AgentEyes (the recorder keeps running)'` (id 7 in every run). It then calls
`com.canonical.dbusmenu.Event(7, "clicked", <int32 0>, 0)`, the same call GNOME's AppIndicator extension
makes on a click. **No kill and no signal** were sent to any instance. Each run exited by itself, and
`wait` returned its exit code.

### The cycle (identical for all four runs)

1. Start the app with no arguments, so its window opens.
2. Close the window to the tray (`wmctrl -ic`, which sends `_NET_CLOSE_WINDOW`).
3. Second launch with no arguments (`show`).
4. Four more launches: `--view library`, `--view record`, `--view library`, `--view record`.
5. Close the window to the tray again.
6. Click Quit in the tray menu.

Run 1 differs from the baseline's run 1, which started `--tray`. Here all four runs take the full path
with the window opened.

### Results

| run | pid | socket while running (owner per `ss -xlp`) | second launches | Quit click | `Main: exited` | exit | socket after |
|---|---|---|---|---|---|---|---|
| 1 | 1279050 | present 08:46:25.663, pid=1279050 | 5, all exit 0 | 08:46:38.107 | 08:46:38.181 | 0 | absent, 0 listeners |
| 2 | 1281736 | present 08:47:12.141, pid=1281736 | 5, all exit 0 | 08:47:21.670 | 08:47:21.740 | 0 | absent, 0 listeners |
| 3 | 1282829 | present 08:47:27.636, pid=1282829 | 5, all exit 0 | 08:47:37.080 | 08:47:37.131 | 0 | absent, 0 listeners |
| 4 | 1284082 | present 08:47:43.130, pid=1284082 | 5, all exit 0 | 08:47:52.548 | 08:47:52.609 | 0 | absent, 0 listeners |

Before each run, `ls` reported `No such file or directory` for `/run/user/1000/agenteyes-app.sock`. After
each run, `kill -0 <pid>` failed.

The quit of run 1, in full (runs 2-4 are identical apart from timestamps; all four are in the evidence
file):

```
08:46:38.115 [pid 1279050] [INFO] [DeferredQuit] Request: shutdown posted to run after the menu has closed
08:46:38.116 [pid 1279050] [INFO] [DeferredQuit] Run: shutting down
08:46:38.116 [pid 1279050] [INFO] [App] Quit (the recorder service keeps running)
08:46:38.167 [pid 1279050] [INFO] [App] Quit: lifetime shutdown requested
08:46:38.167 [pid 1279050] [INFO] [DeferredQuit] Run: shutdown returned
08:46:38.180 [pid 1279050] [INFO] [SingleInstance] Dispose: socket /run/user/1000/agenteyes-app.sock
08:46:38.180 [pid 1279050] [INFO] [SingleInstance] Accept: ''
08:46:38.181 [pid 1279050] [INFO] [SingleInstance] Dispose: socket listener closed and /run/user/1000/agenteyes-app.sock removed
08:46:38.181 [pid 1279050] [INFO] [Program] Main: exited
```

The deferral is visible and short. `Request` is logged 8 ms after the click is sent (08:46:38.107 ->
.115), and the posted `Run` follows 1 ms later. On Linux the post is honoured, and nothing runs between
the request and the shutdown.

Second-launch handoff (#142 row 10) still works in every run. After close-to-tray, `mapped=0`; after the
`show` launch, `mapped=1`. Each `Accept: '<cmd>'` line is followed by `[App] Run:` and `ShowWindow`, and
all 20 second launches exited 0 by themselves.

## 2. The wake-up check - every line after `[App] Quit`, unfiltered

| run | lines after `[App] Quit` | `[App] Run:` after Quit | `ShowWindow` after Quit | window mapped during quit (watcher, 60 samples at 50 ms) |
|---|---|---|---|---|
| 1 | **6** | 0 | 0 | max 0 |
| 2 | **6** | 0 | 0 | max 0 |
| 3 | **6** | 0 | 0 | max 0 |
| 4 | **6** | 0 | 0 | max 0 |

**Why 6 and not the baseline's 4.** The 4 baseline lines are all present: `Dispose: socket`,
`Accept: ''`, `Dispose: ... removed` and `Main: exited`. The 2 extra lines are new log statements this
commit adds on the shutdown path itself:

- `[App] Quit: lifetime shutdown requested` (`App.axaml.cs`, `ShutdownNow`, after `desktop.Shutdown()`)
- `[DeferredQuit] Run: shutdown returned` (`DeferredQuit.cs`, `Run`)

They are the tail of the same synchronous call that logged `[App] Quit`. Neither is a queued message
running after the quit. No `[App] Run:`, `ShowWindow`, `MainWindow` or `RunCommand` line appears after
any Quit, and no `[DeferredQuit] Request: already requested - ignored` line appears anywhere.

**The instrument can see what it is looking for.** The same per-pid logs hold 5 `Accept: '<cmd>'` lines
and 6 `[App] Run:` / `ShowWindow` lines per run (the start plus the 5 handoffs). The window watcher
counted `mapped=1` after each `show` handoff, using the same `wmctrl -lp` command.

**What can blind it.** One line in the session lost its first characters to a concurrent write from
another process (#143). This was 1 of 20 second-launch `sent it` lines:
`tance is running - sent it 'view:library'` at 08:46:35.514. A line hit this way also loses its
`[pid N]` tag, so it would drop out of a per-pid count. This was the only malformed line in the session
log, and it was not near any quit, so the post-quit counts above are complete.

**A variance worth recording, not a failure.** The empty `Accept: ''` comes from the accept thread waking
on close. In all four runs it sits between the two `Dispose` lines, as in the baseline. That placement
also held in the criterion-3 run and both instrumented runs. In the criterion-6 run (pid 1295561) it was
written **after** `Main: exited` (08:52:41.376, the same millisecond). The count was still 6, and no
command was acted on. The accept loop runs on a background thread that races process teardown, so its
last line can land on either side of `Main: exited`.

## 3. The guard does not fire on Linux - with instruments

The guard logs when it is installed, when it attaches to a popup (`guarding tray popup`), when a popup
starts closing (`tray popup '...' closing`), when it cancels (`cancelled a re-entrant close`), and when
it re-opens (`the close of tray popup ... was cancelled elsewhere`). So it does have a log instrument.
Three separate pieces of evidence follow.

**(a) Its log lines, per instance (real build):**

| run | `Install:` | `guarding tray popup` | `closing` | `cancelled` | re-open WARN | all guard lines |
|---|---|---|---|---|---|---|
| 1-4 (each) | 1 | 0 | 0 | 0 | 0 | 1 |
| criterion-3 run, criterion-6 run (each) | 1 | 0 | 0 | 0 | 0 | 1 |

The `Install:` line is the positive control for "the guard's logger reaches this log". The guard is
installed on Linux, before the tray icon, as designed.

**(b) The type it matches is never loaded.** `/proc/<pid>/maps` was read twice in each of runs 2-4: 1.5 s
after the window appeared, and again just before the Quit click. Run 1 has the first read only. Every
read gave `Avalonia.Win32.dll=0`, `Avalonia.X11.dll=1` and `Avalonia.FreeDesktop.dll=1`. The X11 and
FreeDesktop hits are the positive control: the same grep sees managed assemblies the process has loaded.
`Avalonia.Win32.TrayIconImpl+TrayPopupRoot` lives in `Avalonia.Win32.dll`, which ships in the Linux publish
but is never loaded. So no instance of that type can exist in the Linux process.

**(c) The handler is reached, and its lines would show (instrumented scratch build, never committed as
code).** I added one log line in `OnWindowOpened` before the type filter, plus a QA-only environment
switch that makes the filter also accept `MainWindow`. The diff is in the evidence file, section 6, and
the source was restored afterwards (`git status` clean).

- Run I1, filter unchanged. The handler ran for every window opened, and the only type it ever saw was
  `type=AgentEyes.DesktopApp.Views.MainWindow isTrayPopup=False` (2 opens). It logged nothing else, and
  the quit was clean (exit 0, socket gone).
- Run I2, `QA_GUARD_MAINWINDOW=1`. The previously absent lines now appear in the same log:
  `[TrayPopupCloseGuard] guarding tray popup ''` (twice, once per open) and
  `[TrayPopupCloseGuard] tray popup '' closing (reason=WindowClosing, active=False)`. That proves the
  0-counts in (a) come from an instrument that does fire when the guard acts.

  The `closing` line appears twice in I2. That is an artefact of my forcing: MainWindow is re-opened, so
  two handlers were attached to one window. The real popup is a fresh window each time. No `cancelled`
  line appeared, and I2 still exited 0 with the socket removed.

## 4. The DesktopApp tests in the Linux suite, and a mutation of my own

`dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release` at 02b6f37, on devlinux:

```
Test Run Successful.
Total tests: 292
     Passed: 292
 Total time: 2.9509 Seconds
```

All 13 shared quit tests appear by name in the output (`tests/Shared/DesktopAppQuitTests.cs`, compiled into
this suite):

```
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.CancelThisClose_FirstClose_IsAllowedAndMarksClosing
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.CancelThisClose_ReentrantSecondAndThirdClose_AreCancelled
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.CancelThisClose_CloseAlreadyCancelledByOthers_DoesNotMarkClosing
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.ReopenIfNotClosed_FirstCloseCancelledElsewhere_LetsTheNextCloseThrough
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.ReopenIfNotClosed_PopupClosed_KeepsCancellingLaterCloses
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.TrayPopupTypeName_NamesAvaloniasWindowsTrayPopup_WhichIsAWindow
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.IsTrayPopup_AnyOtherWindow_IsFalse
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.App_InstallsTheGuard_BeforeTheTrayIconExists
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.Request_ReturnsWithoutRunningTheShutdown
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.Request_PostedWorkRuns_ShutsDownOnce
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.Request_Twice_PostsOneShutdown
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.TrayController_QuitItem_TakesADeferredQuitNotAnAction
Passed AgentEyes.Tests.Shared.DesktopAppQuitTests.DesktopApp_LifetimeShutdown_IsReachedOnlyThroughDeferredQuit
```

The new project itself also runs here: `dotnet test tests/AgentEyes.Tests.DesktopApp -c Release` ->
`Passed!  - Failed: 0, Passed: 13, Skipped: 0, Total: 13 - AgentEyes.Tests.DesktopApp.dll (net8.0)`.

The 292 includes the 279 of the previous Linux pass plus the 13 shared tests.

### Mutations (working tree only, never committed or pushed; restored with `git checkout --`, tree clean after each)

Neither of these is one of the developer's three mutations.

**Mutation A - the production DeferredQuit runs the shutdown synchronously inside the click.** This is
what a refactor that "simplifies away" the deferral would produce:

```
-            : this(work => Dispatcher.UIThread.Post(work, DispatcherPriority.Background), shutdown)
+            : this(work => work(), shutdown)
```

```
  Failed AgentEyes.Tests.Shared.DesktopAppQuitTests.DesktopApp_LifetimeShutdown_IsReachedOnlyThroughDeferredQuit [22 ms]
   Assert.Contains() Failure: Sub-string not found
Failed!  - Failed:     1, Passed:   291, Skipped:     0, Total:   292
```

**Mutation B - the shutdown back inside the tray click handler** (the shape the mission named):

```
-            quitItem.Click += (_, _) => quit.Request();
+            quitItem.Click += (_, _) => ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!).Shutdown();
```

```
  Failed AgentEyes.Tests.Shared.DesktopAppQuitTests.DesktopApp_LifetimeShutdown_IsReachedOnlyThroughDeferredQuit [18 ms]
   Assert.Equal() Failure: Collections differ
Expected: string[]     ["App.axaml.cs"]
Actual:   List<string> ["TrayController.cs", "App.axaml.cs"]
Failed!  - Failed:     1, Passed:   291, Skipped:     0, Total:   292
```

**What these guards cannot see (a stated limit, not a defect).** Both mutations were caught by the same
test, and that test is a **source-text** check. Mutation A was caught only because the literal
`Dispatcher.UIThread.Post(work, DispatcherPriority.Background)` disappeared from `DeferredQuit.cs`. Some
changes would keep that literal present while making the quit synchronous: the same text in a comment,
or the post wrapped in a helper that runs work inline. Those would keep the test green. No behavioural
test drives the production constructor, because the `post` seam is replaced in every test. The test's
own comment says it is "a source fact, not a runtime one", and the handoff names its limits, so this is
documented rather than an overclaim. Mutation B shows the `.Shutdown(` call-site inventory works for an
added call in a new file. It would not see a shutdown reached through an alias or reflection. See
DEVELOPMENT_METHOD 6c item 5: IL inventories are preferred to text scans for this reason.

## 5. The other Linux criteria at this head

### Criterion 3 - Linux builds and opens its window: PASS

- `dotnet build src/AgentEyes.DesktopApp -c Release`:
  `AgentEyes.DesktopApp -> .../src/AgentEyes.DesktopApp/bin/Release/net8.0/AgentEyesDesktop.dll`,
  `Build succeeded.`, `1 Warning(s)`, `0 Error(s)`. The warning is the pre-existing CS8602 at
  `AlwaysOnEngine.cs:901`.
- That output was run (embedded commit `1.0.0+02b6f37...`):
  `08:50:19.989 [pid 1292460] [INFO] [MainWindow] Opened`.
- Screenshot `linux-pass3-c3-window.png`, captured from the window with `xwd -id`, so no focus was taken.
  I read it before attaching: the Record view, `Monitor 1 - 1920 x 1080 (primary)`, VIDEO 30 fps ON,
  SYSTEM ON, MIC off (no microphone selected), "Signed in", and an enabled REC. It is not blank.
  `linux-pass3-window.png` (run 1, the scratch publish) shows the same.

### Criterion 6 - the Linux install route: CARRIED FORWARD, NOT re-run

**Reason, in one sentence:** running `install.sh` would replace the owner's installed recorder. That
recorder is an unmerged #113 build (embedded commit 89e1593). The script would restart it against the
real `~/Videos/AgentEyes`, which is the #161 first-repair-pass hazard, and a second install would then be
needed to put 89e1593 back, for a criterion whose subject did not change in this pass. Decision by the
Implementation Lead, who was asked before anything ran.

What was checked instead, and it is **not** a PASS:

- **The install route is byte-identical** between 09fd207 (where criterion 6 passed) and 02b6f37. Git blob
  hashes:

  | file | 09fd207 | 02b6f37 |
  |---|---|---|
  | `scripts/linux/install.sh` (writes the desktop entry, `Exec=`, `StartupWMClass=AgentEyesDesktop`) | `003f59c0e05f5a4997e60e4900ce614b45a52c7c` | `003f59c0e05f5a4997e60e4900ce614b45a52c7c` |
  | `scripts/linux/agenteyes.service` | `7032eb180a6d4aa3f00a0991111305c99537f426` | `7032eb180a6d4aa3f00a0991111305c99537f426` |
  | `assets/icon-256.png` (the entry's `Icon=`) | `a6392da4961596ad53615e06e972ab8f9b1bc2ab` | `a6392da4961596ad53615e06e972ab8f9b1bc2ab` |
  | `src/AgentEyes.DesktopApp/Program.cs` (`WmClass = "AgentEyesDesktop"`, applied via `X11PlatformOptions`) | `daa08da7deac2f56532d0e78a5d4919313166a9d` | `daa08da7deac2f56532d0e78a5d4919313166a9d` |
  | `src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj` (AssemblyName, so the `AgentEyesDesktop` apphost) | `63be15fe...` | `a43621d0...` - **differs**: the only change is one added line, `<InternalsVisibleTo Include="AgentEyes.Tests.DesktopApp" />` |

- **The app was launched through the exact entry the installer writes.** I extracted `desktop_entry()`
  from `install.sh` itself (sed between `desktop_entry() {` and `}`). I evaluated it with `app_lib` set to
  a scratch copy laid out the way install.sh lays it out: the publish copied in, and `agenteyes.png`
  installed with `install -m 0644`. I wrote the entry, and `desktop-file-validate` printed nothing. I then
  launched it with `gio launch <entry>`, the desktop's own launcher (gio exit 0).
  - The process command line was exactly the `Exec=` path.
  - The window's WM_CLASS was `AgentEyesDesktop.AgentEyesDesktop`, matching `StartupWMClass`.
  - It held the socket and logged `08:51:34.092 [MainWindow] Opened`.
  - Screenshot `linux-pass3-desktop-entry-window.png` (read: same Record view, not blank).
  - It then quit through the tray, with exit and socket removal as in section 1.
- **No service was started** by this check. The app talks to the existing service on 127.0.0.1:7882, and
  no service I started ran any repair pass. "A scratch service root" therefore did not arise.
- **Not exercised:** the script's build, stop, copy and `systemctl --user` steps, the autostart entry,
  the sign-in step, and a launch from the real `~/.local/share/applications` entry.

### Criterion 8 - each platform links its own backend: PASS (both directions)

Both engines were built from this head. The embedded commit in both is
`1.11.11+02b6f3725cfc7fbfcde182f83a2c959678fca19e`. Counts are UTF-16LE string hits (`strings -e l |
grep -c`), the technique CI uses:

| engine | x11grab | pulse | v4l2 | xrandr | avfoundation | screencapture |
|---|---|---|---|---|---|---|
| Linux (`agenteyes.dll` in the linux-x64 publish) | **1** | 8 | 1 | 4 | **0** | 0 |
| macOS (`dotnet build src/AgentEyes.Core -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac -o <scratch>`, `Build succeeded. 0 Error(s)`) | **0** | **0** | **0** | **0** | 2 | 4 |

The Linux build contains x11grab. The macOS build, from the same commit and the same counter, contains
none of x11grab, pulse, v4l2 or xrandr, and does contain avfoundation. So each absence is paired with a
presence on the other side. (`gdigrab` = 2 in both. Those are a shared usage text,
`engine: ffmpeg gdigrab`, and the identifier. They are not criterion 8's subject.) The mac engine was
built into its own output folder, so the shared `bin/Release/net8.0` was not polluted, per the handoff's
caution.

### Criterion 9 - the app shows live recorder state while /health is ok: PASS

The criterion-3 window was captured at 08:50:24.146:

- `GET /health` at 08:50:24.107 (before) and 08:50:24.150 (after) both returned
  `{"ok":true,"app":"AgentEyes","host":"headless","platform":"linux"}`.
- `/status` at 08:50:24.168 returned `"State": "idle"`.
- The app logged `[AppState] PollAsync: service answering, state=idle` and
  `[ControlClient] GetDevicesAsync: 1 monitor(s), 1 mic(s)`.
- The window (`linux-pass3-c3-window.png`) shows the Control API's devices, "Signed in" and an enabled REC
  rather than "not answering".
- The tray menu's status line, read over D-Bus at the same time, was `'Ready to record'`.

### Criterion 10 - the tray icon on Linux: reported; Linux behaviour unchanged

- **The item registers:** `org.kde.StatusNotifierItem-<pid>-0`, `ItemIsMenu=false`, in every instance.
- **Its menu answers over D-Bus** with the same entries as before: `Ready to record`, a separator,
  `Start recording`, `Show AgentEyes`, `Open recordings folder`, a separator, and
  `Quit AgentEyes (the recorder keeps running)`. Quit is id 7, as at 0e1b889.
- **Clicking Quit through dbusmenu quits cleanly**: 6 of 6 tray Quits on the build under test across this pass (runs 1-4, criterion 3, criterion 6),
  and 2 more on the instrumented build.
- **Activate raises the window.** After close-to-tray, `mapped=0`. `org.kde.StatusNotifierItem.Activate
  0 0` was sent at 08:50:25.337, then the app logged `[App] ShowWindow` and `[MainWindow] Opened`, and
  `mapped=1`.
- **No tray screenshot this pass, and why.** A full-screen camera-wall window ("Lake", belonging to
  another mission) covers the whole display on devlinux, GNOME's top bar included. A root capture shows
  only that window where the panel should be. I read that capture, did not attach it, and did not move or
  hide someone else's full-screen window to get one. The earlier screenshot `linux-tray-topbar.png` is
  still the latest image of the Linux tray.
- **Still not observed** (unchanged from the developer's note): a physical mouse click on the icon. I did
  not synthesize input on the shared desktop.
- The tray code changed only in the Quit wiring (`TrayController.cs`: `quit.Request()` instead of
  `quit()`). The Linux tray findings did not change. #163 is Windows-only and was not touched.

## What I did NOT cover

- **Windows and macOS.** Nothing here speaks for them. Windows QA runs on another machine. macOS is
  blocked by a locked screen.
- **Criterion 6 as written.** It is carried forward, not re-run; see above. No real `install.sh` run, no
  service restart, and no launch from the real `~/.local/share/applications/agenteyes.desktop`.
- **devlinux's installed software is not main (a fact about the machine, not a finding against this
  issue).** The installed recorder `~/.local/lib/agenteyes/agenteyes.dll` is
  `1.11.11+89e15934278fdf106d30d6ae2cfd13c22e9972c2`, a build of the unmerged branch
  `origin/issue-113-capture-watchdog`. That commit is not an ancestor of this PR. The installed desktop
  app is still the pre-rename `AgentEyesLinux`. So "the installed service" on this box has been neither
  main nor this PR since the #113 work. Anyone reading Linux evidence should treat it that way.
- **The service under test.** Criterion 9 shows that this head's app reads a live service. That service is
  the #113 build above, not one built from this head.
- **A physical click on the tray icon, and the on-screen menu.** Protocol-level only.
- **A tray screenshot.** The panel was covered; see criterion 10.
- **The Windows crash itself.** This pass shows the guard is inert on Linux. It does not show the guard
  fixes Windows; that is the Windows seat's job.
- **Timing under heavy load.** The deferral was observed at 1-min load 2.3 - 4.5 on 32 cores only.
- **`dotnet build AgentEyes.sln` / `dotnet test AgentEyes.sln`.** Windows gates; not run here.

## The owner's machine - what I borrowed and how I put it back

- **The owner's tray app** (pid 6006, `~/.local/lib/agenteyes-app/AgentEyesLinux --tray`, running since
  06:35:47) held the single-instance socket. It was **stopped through its own D-Bus tray Quit** at
  08:46:01: `[App] Quit (the recorder service keeps running)`, then `[Program] Main: exited` at
  08:46:01.541, and the socket was removed. The lead had been told beforehand that I would do this.
  Honest note: it was sent a few minutes earlier than planned, by a helper invocation I meant as a dry
  run. The route and the result were the ones announced.
- **Restored at 08:52:57 and verified:**
  - Relaunched from `~/.local/lib/agenteyes-app/AgentEyesLinux --tray`; it is pid **1298614**.
  - The socket `/run/user/1000/agenteyes-app.sock` is present (08:52:57.430) and listening:
    `users:(("AgentEyesLinux",pid=1298614,fd=48))`.
  - `org.kde.StatusNotifierItem-1298614-0` answers `GetLayout` with `'Ready to record'` and the Quit
    entry.
  - Its log shows `TryAcquire: listening` and `PollAsync: service answering, state=idle`.
- **Recorder service: not touched.** `MainPID=6560`, `ActiveEnterTimestamp=Mon 2026-11-16 06:35:49 EST`,
  both unchanged. `/status` read `idle` before every launch and at the end. No recording was started.
- **ffmpeg:** the foreign go2rtc ffmpeg (pid 72110, started 06:40:48) was not touched. The only ffmpeg I
  ran was a one-shot `-f xwd_pipe` image conversion of my own window captures.
- **Recordings:** nothing deleted and nothing recorded. `~/Videos/AgentEyes/2026-11-14_102532_video` is
  present. The library lists 82 recordings, before and after.
- **Audio settings:** not changed.
- **My processes:** every `AgentEyesDesktop` instance I started quit through its tray menu. None is left
  running.
- **Working tree:** the instrumentation and both mutations were reverted (`git status` clean before this
  report was added).
