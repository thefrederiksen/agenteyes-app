# Issue #116 - QA report, macOS half

**Verdict: FAIL (flow:qa-failed).** One defect, reproducible, in the test suite the macOS developer
loop runs. Everything else I could reach on this machine passed, and three criteria are blocked by a
machine state I cannot change - the Mac's screen is locked.

| | |
|---|---|
| Machine | `devthrottle-mac-mini`, macOS 26.5.2 (25F84), arm64 Mac mini, display 1920x1080 |
| Branch / head | `issue-116-desktop-app` at `c80cb9d` |
| PR | #141 |
| Baseline compared against | `origin/main` at `af0ddb1`, same machine, same commands |
| Toolchain | `dotnet 10.0.403`. There is no .NET 8 SDK here, so `DOTNET_ROLL_FORWARD=LatestMajor` and `DOTNET_ROOT=$HOME/.dotnet` are exported in the shell. No script in the repo was changed, as the issue requires. |
| Recorder service | the installed launchd agent `com.agenteyes.recorder`, pid 90675, already running. NOT reinstalled. |

**This report covers macOS only. Windows is not mine.** Criteria 4, 5, 7, the Windows halves of 2, 9,
10 and 11, and the Windows half of 12 are untouched here and still owed by whoever runs
`SOREN_NORTH` or `SORENLAPTOP`. Criteria 3 and 6 are Linux and were run by the developer on
`devlinux`; I did not re-run them and I am not carrying their result across.

I did not change any product code.

---

## 1. The defect

### D1 - FAIL, criterion 12: the macOS gate is not green. `tests/AgentEyes.Tests.Linux` fails 3 tests on macOS, and passed on `main` on this same machine

**Expected** (criterion 12, verbatim): "On macOS: build the engine with `-p:AgentEyesUnixPlatform=mac`
plus `dotnet test tests/AgentEyes.Tests.Mac`, and the same pair for `linux` with
`tests/AgentEyes.Tests.Linux`." All four commands green. This is also the documented macOS developer
loop recorded on #114 ("The macOS developer loop, proven on devthrottle-mac-mini"), whose stated
counts are Mac 226 and Linux 267 passed / 1 skipped.

**Actual**, on this machine, this branch:

```
1/4  dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac    Build succeeded, 0 Error(s)
2/4  dotnet test  tests/AgentEyes.Tests.Mac   -c Release                        Passed!  Failed: 0, Passed: 226
3/4  dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux  Build succeeded, 0 Error(s)
4/4  dotnet test  tests/AgentEyes.Tests.Linux -c Release                        Failed!  Failed: 3, Passed: 274, Skipped: 1, Total: 278
```

Same four commands on `origin/main` (`af0ddb1`), same machine, same shell:

```
4/4  dotnet test  tests/AgentEyes.Tests.Linux -c Release                        Passed!  Failed: 0, Passed: 267, Skipped: 1, Total: 268
```

Branch 3 failures in 3 of 3 runs; main 0 failures in 3 of 3 runs. Deterministic both ways, not a
flake. Full transcript with the stack traces: `mac-gate.txt`.

All three failures are in the new file `tests/AgentEyes.Tests.Linux/DesktopAppPlatformTests.cs`,
which this PR adds.

**D1a - `DesktopAppPlatformTests.cs:37-40`, `PlatformName_OnThisLinuxHost_IsLinux`.**

```
Assert.Equal() Failure: Strings differ   Expected: "linux"   Actual: "mac"
```

`Shell.PlatformName` (`src/AgentEyes.DesktopApp/Services/Shell.cs:13-14`) is a runtime host check -
`OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux"`. On a Mac it
returns `"mac"`, so this assertion can never pass here. It is not flaky and it is not an environment
quirk: it is a test that hard-asserts the host operating system inside the one project this PR has
just pointed at the cross-platform app. The same project already has the right pattern three files
away - `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome` SKIPS on a non-Linux host and
shows as `Skipped` in every run above.

**D1b - `DesktopAppPlatformTests.cs:76-88`, the two handoff tests.**

```
TryAcquireSocket_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull
System.ArgumentOutOfRangeException : The path '/var/folders/b8/<redacted>/T/agenteyes-linux-tests/
run-20261115-094152-45049/single-d9b09b35/app.sock' is of an invalid length for use with domain
sockets on this platform. The length must be between 1 and 104 characters, inclusive.
  at SingleInstance.TryAcquireSocket(...) in src/AgentEyes.DesktopApp/Services/SingleInstance.cs:91

TryAcquirePipe_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull
System.ArgumentOutOfRangeException : The path '/var/folders/b8/<redacted>/T/CoreFxPipe_agenteyes-
test-e8637feef3b1485b8417c4f48894d1f2' is of an invalid length ... 1 and 104 characters
  at SingleInstance.NewPipeInstance(...) in src/AgentEyes.DesktopApp/Services/SingleInstance.cs:132
```

The cause is the macOS temp directory. `$TMPDIR` here is
`/var/folders/b8/<32 chars>/T/` - 49 characters before anything is appended - and the fixtures then
add `agenteyes-linux-tests/run-<stamp>-<pid>/single-<guid>/app.sock` (socket) or
`CoreFxPipe_agenteyes-test-<32-hex-guid>` (pipe, the prefix .NET adds itself). Both land past the
104-character `sun_path` limit.

**The product is NOT affected, and I checked rather than assumed.** The app's real socket path is
`SingleInstance.SocketPath` = `$TMPDIR + "agenteyes-app.sock"` = 67 characters, and the running app
bound it successfully (section 3 below, and `mac-runtime.txt`). The defect is in the test fixtures'
path construction, not in `SingleInstance`.

**Why CI did not catch it:** the macOS job in `.github/workflows/ci.yml:116-184` runs
`tests/AgentEyes.Tests.Mac` only. It never builds or runs `tests/AgentEyes.Tests.Linux`, so the
three failures are invisible to CI and appear only to a developer on a Mac running the documented
loop. CI at `c80cb9d` is genuinely green (section 5) - it is simply not looking at this.

**What a fix has to satisfy, for the QA round after this one:** the four macOS gate commands green
on this machine, with `tests/AgentEyes.Tests.Linux` reporting 0 failed and the host-specific facts
either skipped with a stated reason or made host-independent. Shortening the fixture paths is not
enough on its own - D1a is a separate problem from D1b.

---

## 2. Criterion by criterion - what I verified on macOS

| # | Criterion (macOS half) | Verdict | Evidence |
|---|---|---|---|
| 1 | Old names gone (not platform-specific; checked because it is free) | PASS | section 6 |
| 2 | macOS builds and opens its window, with a screenshot | PARTIAL - build and window PASS, screenshot BLOCKED | sections 3, 4 |
| 8 | macOS links its own backend | PASS | `mac-linkage.txt` |
| 9 | The app reaches the recorder, with a screenshot | PARTIAL - the app reads live state, PASS; screenshot BLOCKED | section 3, `mac-runtime.txt` |
| 10 | The tray, reported | NOT VERIFIED - blocked | section 4 |
| 11 | Single instance | PASS | section 3, `mac-runtime.txt` |
| 12 | Gates green | **FAIL** | section 1, `mac-gate.txt` |
| 13 | CI green, more than one run at the head | PASS | section 5 |
| - | LibVLC initialises | PASS | section 3 |

---

## 3. What passed, and the evidence for each

Full transcript: `mac-runtime.txt`.

### Criterion 2, the build half - PASS

**Expected:** `dotnet build src/AgentEyes.DesktopApp -c Release` -> `Build succeeded.`, `0 Error(s)`.
**Actual:**

```
AgentEyes.Core       -> <repo>/src/AgentEyes.Core/bin/Release/net8.0/agenteyes.dll
AgentEyes.DesktopApp -> <repo>/src/AgentEyes.DesktopApp/bin/Release/net8.0/AgentEyesDesktop.dll
Build succeeded.
    1 Warning(s)     (pre-existing CS8602 at AlwaysOn/AlwaysOnEngine.cs:901, also on main)
    0 Error(s)
```

Platform auto-detection works as the issue asked: the engine was built with no
`AgentEyesUnixPlatform` property on the command line and produced the macOS backend
(`avfoundation` present, `x11grab`/`pulse`/`v4l2`/`xrandr` all absent - criterion 8 below).

### LibVLC initialises - PASS, and this is the one I was asked to state explicitly

`Program.Main` calls `LibVLCSharp.Shared.Core.Initialize()` at `src/AgentEyes.DesktopApp/Program.cs:43`,
between `SingleInstance.TryAcquire` and Avalonia's start. Without `VideoLAN.LibVLC.Mac` it throws
`VLCException` there and no window appears. Both the line before it and the line after it are in the
log, so it returned:

```
09:35:43.810 [pid 43308] [INFO] [SingleInstance] TryAcquire: listening on /var/folders/b8/<redacted>/T/agenteyes-app.sock
09:35:44.931 [pid 43308] [INFO] [App] OnFrameworkInitializationCompleted
```

That is an inference from a gap, so here is the positive form - the native libraries actually mapped
into the running process, read with `lsof` against its pid:

```
<repo>/src/AgentEyes.DesktopApp/bin/Release/net8.0/libvlc/osx-arm64/lib/libvlc.dylib
<repo>/src/AgentEyes.DesktopApp/bin/Release/net8.0/libvlc/osx-arm64/lib/libvlccore.dylib
```

The arm64 flavour, loaded, in the app's address space. The csproj change this issue most depends on
does what it was supposed to do.

### Criterion 2, the window half - the window EXISTS; the screenshot is blocked

**Expected:** the app runs, `[MainWindow] Opened` appears in its log with a timestamp, and a
screenshot of the window. The lead's instruction was explicit that a log line alone is not enough,
because a previous session on this machine claimed a launch on the strength of one.

**Actual, the log line:**

```
09:35:45.303 [pid 43308] [INFO] [MainWindow] ctor
09:35:45.380 [pid 43308] [INFO] [MainWindow] Opened
```

**Actual, better than the log line:** I asked the macOS window server itself, not the app. A small
Swift probe over `CGWindowListCopyWindowInfo(.optionAll)` - owner, pid, layer, bounds and on-screen
state are returned without the Screen Recording grant; only the window's NAME is withheld:

```
pid=43308 owner="AgentEyesDesktop" window=623 layer=0 onscreen=true alpha=1.0 bounds=1280x748 at 320,162 name=(name withheld: no Screen Recording grant)
```

The window server has a real, on-screen, fully opaque 1280x748 window owned by a process named
`AgentEyesDesktop`. The same probe aimed at the recorder service, which owns no window, returns
`[EMPTY]` and exit 2 - so the probe is discriminating, not answering yes to everything.

**What this still does NOT prove, and I am not going to pretend otherwise:** that the window RENDERS.
A window that painted nothing would report the same bounds. The screenshot the criterion asks for is
the thing that would close that gap, and I could not take it - see section 4. **Criterion 2 is
therefore not fully met on macOS, and the next macOS seat still owes the screenshot.**

### Criterion 9 - the app reads the recorder's live state over the Control API - PASS for the behaviour, screenshot blocked

**Expected:** the running app shows the recorder's state rather than "not answering", while
`GET /health` returns `ok: true`, with a screenshot.

**Actual:**

```
$ curl -s http://127.0.0.1:7882/health
{ "ok": true, "app": "AgentEyes", "host": "headless", "platform": "macos" }
```

and, from the app's own log at the same moment:

```
09:35:45.522 [pid 43308] [INFO] [AppState] PollAsync: service answering, state=idle
09:35:45.522 [pid 43308] [INFO] [TrayController] Render:  -> idle
09:35:46.083 [pid 43308] [INFO] [ControlClient] GetDevicesAsync: 1 monitor(s), 2 mic(s)
```

Stronger than a static read: while I had a recording running through the API (section 4), the app
followed the state change and came back:

```
09:36:24.207 [pid 43308] [INFO] [TrayController] Render: idle -> recording
09:36:32.541 [pid 43308] [INFO] [TrayController] Render: recording -> idle
```

So the app is talking to the recorder, getting its devices, and tracking its state live on macOS.
The criterion also asks for a screenshot of the window with the status visible; that half is blocked.

### Criterion 11 - single instance on macOS - PASS

**Expected:** launch twice; the second raises the first window and exits, with a log excerpt from both.

`XDG_RUNTIME_DIR` is unset on macOS, which is exactly the temp-directory fallback at
`SingleInstance.cs:57-59`. The socket path is 67 characters - inside the 104-character limit that
breaks the two unit tests in D1b, which is how I know the product is not hit by it.

**Actual.** First launch, pid 43308 (it also found the stale socket left by the 13 November
experiment and took it over, which is the recovery path working):

```
09:35:43.804 [SingleInstance] TryAcquireSocket: /var/folders/b8/<redacted>/T/agenteyes-app.sock command='show'
09:35:43.810 [SingleInstance] TryAcquire: stale socket ... (ConnectionRefused) - taking it over
09:35:43.810 [SingleInstance] TryAcquire: listening on /var/folders/b8/<redacted>/T/agenteyes-app.sock
```

Second launch, `./AgentEyesDesktop --view library`, run in the foreground so its exit code is visible.
**It exited 0.** Both processes, interleaved, from the one log file:

```
09:39:15.869 [pid 44378] [Program] Main: AgentEyes desktop app starting on mac (tray=False, pid=44378)
09:39:15.870 [pid 44378] [SingleInstance] TryAcquireSocket: ...T/agenteyes-app.sock command='view:library'
09:39:15.874 [pid 44378] [SingleInstance] TryAcquire: another instance is running - sent it 'view:library'
09:39:15.874 [pid 43308] [SingleInstance] Accept: 'view:library'
09:39:15.874 [pid 43308] [App] Run: view:library
09:39:15.875 [pid 43308] [App] ShowWindow
09:39:15.877 [pid 43308] [MainWindow] RunCommand: view library
09:39:15.878 [pid 43308] [MainWindow] Rail_Changed: Library
```

The second process handed off and exited; the first accepted the command, raised its window and
actually acted on it by switching to the Library. One process remained afterwards. This is a
presence on both sides - the sender's line, the receiver's line, and the effect.

### Criterion 8, macOS half - the macOS build links the macOS backend - PASS

Full output: `mac-linkage.txt`. Run against a build made ON this Mac, not a cross-compile, using the
CI step at `.github/workflows/ci.yml:167-184` copied verbatim except that the output directory is an
argument so it can be aimed at a bad build.

```
x11grab 0   pulse 0   v4l2 0   xrandr 0   avfoundation 2   screencapture 4   gdigrab 2
libvlc/osx-arm64/   libvlc/osx-x64/
[OK] the macOS desktop app links the macOS engine and carries libvlc        exit=0
```

`gdigrab 2` is expected and is exactly why the lead's amendment to criterion 7 was right: it is in
the shared recorder code in every engine build and discriminates nothing.

And the check FIRES, which is the part that makes the pass mean something. Three bad inputs, each a
copy of the good output:

```
BAD 1  the Linux-configuration engine swapped in   -> 5 [FAIL] lines, exit 1
BAD 2  the libvlc folder deleted                   -> [FAIL] libvlc.dylib ... is missing, exit 1
BAD 3  an empty output folder                      -> [FAIL] AgentEyesDesktop was not built, exit 1
```

### A trap for the next Mac developer, found while running the gate - not a criterion failure

`tests/AgentEyes.Tests.Linux` references the desktop app pinned to `AgentEyesUnixPlatform=linux`, and
the mac and linux flavours share `src/AgentEyes.DesktopApp/bin/Release/net8.0`. So running the macOS
gate REPLACES the macOS app output with a Linux-flavoured one. Measured in that order in the one
directory:

```
after the gate run:    x11grab 1, pulse 8, avfoundation 0
after rebuilding mac:  x11grab 0, pulse 0, avfoundation 2
```

Launching `bin/Release/net8.0/AgentEyesDesktop` straight after the gate therefore starts an app
carrying the X11/PulseAudio engine on a Mac. The developer flagged the engine half of this in the
handoff note; it also applies to the app's own output. CI never hits it (its macOS job does not build
the Linux test project). I rebuilt before taking the linkage evidence above. Worth one line in the
developer loop: on a Mac, rebuild the app after the gate, before you run it.

---

## 4. What I could NOT verify, and exactly why

**The Mac's screen is locked, and only the owner can unlock it.**

```
$ ioreg -n Root -d1 -r -k IOConsoleUsers
  kCGSSessionOnConsoleKey = Yes
  kCGSessionLoginDoneKey  = Yes
  CGSSessionScreenIsLocked = Yes
$ ioreg -c IOHIDSystem | grep HIDIdleTime
  59286 seconds idle            (16.5 hours - nobody has touched this machine)
```

The capture route I was given works, and I ran it: I started a recording through the Control API
(`POST /record/start` with `{"mode":"video","screen":1,"source":"mic","mic":"Yealink UH36","fps":30}`),
let it run 6 seconds, stopped it with `POST /record/stop -H 'Content-Type: application/json' -d '{}'`,
and pulled a frame out of `raw.mp4` with ffmpeg. It produced a real 1920x1080 frame from
`~/Movies/AgentEyes/2026-11-15_093623_video/raw.mp4`.

**I read the frame.** It is the desktop wallpaper and nothing else: no menu bar, no Dock, no app
window. That is what a locked macOS session composites - the frame is correct, there is just nothing
on the screen to photograph. `mac-screen-locked.png` is that frame; `mac-menubar-absent.png` is its
top strip at full width, showing the menu bar area empty apart from the orange recording-privacy dot.
This is NOT a blank render by the app and it is NOT a broken capture; it is the lock screen, and I am
recording it so nobody has to re-derive it.

I did not try `screencapture` or `osascript`, as instructed - they are refused in an agent's process
tree and hang. I did not try to unlock the machine.

What this blocks:

- **Criterion 2's screenshot.** The window exists in the window server (section 3); its pixels are
  unphotographed.
- **Criterion 9's screenshot** of the window with the recorder status visible. The behaviour is
  proven from the API and the log; the picture is not.
- **Criterion 10 entirely - the tray.** I can report nothing honest about the macOS menu-bar icon.
  I tried: the window-server probe shows the app owns exactly one window, layer 0, and no layer-25
  window, which is where an `NSStatusItem` lives. But that probe cannot discriminate in this state -
  the only owner of layer-25 windows while the screen is locked is Control Center, and Tailscale and
  RustDesk, which both certainly have menu-bar icons on this machine, show none either. So the
  absence proves nothing about the app. **Criterion 10 on macOS is UNKNOWN, not negative and not
  positive.** It needs an unlocked screen, one screenshot of the menu bar, and one physical click.

To close these three, a seat needs the owner to unlock the Mac mini and leave it unlocked. Then the
work is about ten minutes: launch the app, screenshot via the recorder service exactly as above,
click the menu-bar icon once, report. The window probe is committed next to this report as
`mac-window-probe.swift` (build and run instructions in its header), and the exact commands are in
`mac-runtime.txt`.

**A note on clicking the tray icon.** Even unlocked, a physical click cannot be synthesized from an
agent shell here (`osascript` is refused, -25211) and the project rules forbid synthesizing input on
the owner's desktop without warning him. Criterion 10's "does clicking it raise the window" may need
the owner's own hand, or his explicit say-so first. `TrayController.cs:50-52` sets a `Menu` AND a
`Clicked` handler on the same `TrayIcon`, which is the exact combination the issue flags as behaving
differently on macOS - so this is the question most worth asking, and it is still open.

---

## 5. Criterion 13 - CI, green across more than one run at the head

Three completed runs at `c80cb9d`, every job green in every one:

| run | event | linux | macos | windows |
|---|---|---|---|---|
| 43643912137 | push | success | success | success |
| 43643915962 | pull_request | success | success | success |
| 43643947657 | workflow_dispatch | success | success | success |

That satisfies the "more than one run at the final head" rule. It does not cover D1: as shown in
section 1, the macOS job never runs `tests/AgentEyes.Tests.Linux`, so CI being green is not evidence
against the defect.

---

## 6. Criterion 1 - the old names (not platform-specific, checked because it costs nothing)

```
$ grep -rn "LinuxApp\|AgentEyesLinux" --include "*.cs" --include "*.axaml" --include "*.csproj" \
       --include "*.sh" --include "*.yml" .     (excluding bin/, obj/, docs/cencon/proof/)
src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj:4: ... first written for Linux as AgentEyes.LinuxApp in issue #107 ...
scripts/linux/install.sh:113: # running. AgentEyesLinux is the name the app had before issue #116 ...
```

Two hits, both historical comments, both the ones the developer declared. `src/AgentEyes.LinuxApp/`
no longer exists. The developer's own statement that the grep is blind to the deliberate split
literal at `install.sh:116` (`AgentEyes(Desktop|Linux)`) is correct and I confirmed it is still
there - a source text scan cannot see a name a regex splits, and saying so is the right way to
declare that limit.

---

## 7. State of this machine when I finished

- The recorder service was **not** reinstalled and not restarted. Still pid 90675, `/health` ok.
- Nothing under `~/Movies/AgentEyes` was deleted. One directory was ADDED by my capture:
  `2026-11-15_093623_video` (6.1 s, video + mic). It is the source of the screenshots in this folder.
  Leaving it.
- The desktop app I launched (pids 43308, 44378) is stopped; no `AgentEyesDesktop` process remains.
  Its single-instance socket is the app's own and is cleaned up on exit.
- The app's log file `~/Library/Application Support/AgentEyes/logs/AgentEyes-20261115.log` is the
  shared one; the app's lines are tagged with its pid, as quoted above. That also answers the
  developer's open question: on macOS the app's log lands in
  `~/Library/Application Support/AgentEyes/logs/`, observed, not inferred from documentation.
- Work was done in a separate git worktree so the shared checkout on this machine was not disturbed.
