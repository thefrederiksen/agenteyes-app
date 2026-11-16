# Issue #116 - Developer handoff to QA

One desktop app: `src/AgentEyes.LinuxApp` -> `src/AgentEyes.DesktopApp` (namespace `AgentEyes.DesktopApp`,
assembly and executable `AgentEyesDesktop`). PR #141, branch `issue-116-desktop-app`.

**Where it was run.** Everything below marked "devlinux" was run on devlinux (Ubuntu, GNOME on Xorg).
**macOS and Windows were NOT run by me.** No Mac and no Windows machine was used. For those two platforms
I made the code and csproj changes and cross-compiled both from Linux (see "Cross-compile" below).
That proves they compile and link the right engine. It does not prove a window opens. QA owns criteria
2, 4, 5, 9, 10 and 11 on those platforms.

## Decisions the issue asked me to name

- **Target framework: conditional, not multi-targeted.** `AgentEyesDesktopPlatform` is `windows` when
  `$(OS) == Windows_NT` or `EnableWindowsTargeting=true` (the engine uses the same switch). Otherwise it
  takes `AgentEyesUnixPlatform` if that is set, then `mac` on an OSX host, then `linux`. Windows builds
  `net8.0-windows10.0.19041.0`; the others build `net8.0`. I chose this over multi-targeting because a
  multi-targeted app would build its `net8.0` flavour on Windows too. That flavour links the Unix engine,
  which is exactly the thing criterion 7 exists to prevent.
- **Platforms: `x64;arm64`, not AnyCPU.** `Platforms` is only solution and IDE metadata. The solution maps
  `Release|Any CPU` to `x64` for every project, and this one follows the others. A plain
  `dotnet build src/AgentEyes.DesktopApp -c Release` builds Platform=AnyCPU and runs on the host's
  architecture, which is how the Mac arm64 experiment launched. A shipped build's architecture comes from
  its RID at publish (`-r osx-arm64`, `-r win-x64`, `-r linux-x64`). Cross-OS publish is not supported:
  publish on the platform you ship. That is Phase 2's concern.
- **Engine reference:** `AdditionalProperties="AgentEyesUnixPlatform=linux"` deleted; the engine
  auto-detects. `tests/AgentEyes.Tests.Linux` now pins BOTH of its references (engine and app) to `linux`.
  Without that, the app's engine build (no global property) and the test's (`=linux`) are two builds of
  the same project writing to the same `bin/Release/net8.0`.
- **libvlc:** `VideoLAN.LibVLC.Windows` 3.0.21 only when windows, `VideoLAN.LibVLC.Mac` 3.1.3.2 only
  when mac, nothing on Linux (system libvlc, as before).
- **Windows single instance:** a named pipe `agenteyes-app-<UserName>` with `PipeOptions.CurrentUserOnly`.
  The first launch claims the name with `PipeOptions.FirstPipeInstance`, which fails if the pipe exists,
  so two simultaneous launches cannot both win. The next pipe instance is created BEFORE the connected
  one is closed, so the pipe never disappears between launches. Linux and macOS keep the Unix socket
  unchanged.
- **Log location:** unchanged. The app logs through the engine's `Log` -> `AppDataPaths.Root/logs`, i.e.
  `Environment.SpecialFolder.LocalApplicationData` + `AgentEyes/logs/AgentEyes-YYYYMMDD.log`. On .NET 8
  that is `%LOCALAPPDATA%\AgentEyes\logs` on Windows, `~/.local/share/AgentEyes/logs` on Linux (observed),
  and `~/Library/Application Support/AgentEyes/logs` on macOS. **The macOS path is from the .NET 8
  documentation, not observed by me.** Confirm it on the Mac and quote the path. The issue says not to
  change engine behaviour, so I did not touch it.
- **X11PlatformOptions** is still passed on every platform. Only Avalonia's X11 backend reads it. Evidence
  for macOS: the 13 November Mac launch passed the same option and opened its window. Windows: unverified,
  for QA (criterion 4 covers it).

## Acceptance criteria

| # | What I did | Run here? | How QA exercises it |
|---|---|---|---|
| 1 | Renamed dir, csproj, RootNamespace, AssemblyName, all namespaces/`x:Class`, `InternalsVisibleTo`, test reference, the Windows `ManifestWriterTests` path list, install.sh, the test class (`LinuxAppTests` -> `DesktopAppTests`). | devlinux | Run the grep (see "Criterion 1 hits" below). |
| 2 | macOS: csproj natives + engine auto-detect. | **NO** | On devthrottle-mac-mini (see #114 "macOS developer loop" for the SDK): `dotnet build src/AgentEyes.DesktopApp -c Release`, run `bin/Release/net8.0/AgentEyesDesktop` (needs `DOTNET_ROLL_FORWARD=LatestMajor` there for a framework-dependent build; per the issue, do NOT script it). Grep the log for `[MainWindow] Opened`. Take the window screenshot with a tool holding the Screen Recording grant and say which tool. |
| 3 | Linux build + window. | **devlinux** | `linux-window.png`, `linux-runtime.txt` (`[MainWindow] Opened` 16:34:11.590). |
| 4 | Windows: windows TFM, LibVLC.Windows, pipe single instance, explorer. | **NO** (cross-compiled only) | On SOREN_NORTH or SORENLAPTOP: `dotnet build src\AgentEyes.DesktopApp -c Release -p:Platform=x64`, run `src\AgentEyes.DesktopApp\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesDesktop.exe`, `[MainWindow] Opened` in `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-YYYYMMDD.log`, PrintWindow screenshot. |
| 5 | Added to `AgentEyes.sln` (solution folder `src`, all configs -> x64). | **NO** (Linux cannot build the sln) | `dotnet build AgentEyes.sln -c Release` on Windows. Quote the `AgentEyes.DesktopApp -> ...\AgentEyesDesktop.dll` line. The CI windows job runs the same build. |
| 6 | install.sh renamed throughout; stops the app by path (old and new name). | **devlinux** | `linux-install-transcript.txt`: exit 0, old `AgentEyesLinux` pid 3035138 stopped, new app started, desktop entries rewritten. Then `gtk-launch agenteyes` (the app-grid entry) launched pid 2451234. |
| 7 | Windows TFM -> Windows engine. | Cross-compiled on devlinux; CI windows job | CI step "The Windows desktop app must link the Windows engine". **Note:** `gdigrab` alone cannot decide this. The shared recorder code (Commands.cs, Video/FfmpegRecorder.cs) names gdigrab, and the Linux engine contains it twice too. The deciding checks are no `x11grab`/`pulse`/`v4l2`/`avfoundation`, plus an `NAudio` assembly reference (Windows engine only). |
| 8 | mac and Linux link their own engine. | Cross-compiled (mac) and built (Linux) on devlinux; CI | CI steps "The macOS desktop app must link the macOS engine and carry libvlc" and "The Linux desktop app must link the Linux engine". |
| 9 | Nothing new; the app already reads `/status`. | **devlinux** | `linux-window.png` shows devices, "Signed in" and an enabled REC, taken at 16:34:22 while `/health` returned `ok: true`. On Mac and Windows: same screenshot plus `curl http://127.0.0.1:7882/health`. A machine with no recorder service shows the offline state; say so. |
| 10 | No change (report only). | **devlinux** | See "Tray, per platform". |
| 11 | Windows pipe added; Unix socket unchanged. | **devlinux** (socket); pipe only in unit tests on Linux | Windows: launch `AgentEyesDesktop.exe` twice. The second must log `[SingleInstance] TryAcquire: pipe agenteyes-app-<user> is owned by another instance` then `... sent it 'show'`, and the first must log `[SingleInstance] Accept: 'show'` then `[App] ShowWindow`. Also start two at once (`start A & start B` in one cmd line): exactly one must survive. That is the FirstPipeInstance claim, which no Linux test can prove. |
| 12 | Linux gate run here; mac engine compile here. | devlinux | Mac: `dotnet test tests/AgentEyes.Tests.Mac` ON THE MAC (I did not run it). Windows: `dotnet build` + `dotnet test AgentEyes.sln -c Release`. |
| 13 | ci.yml: each job builds the app and checks linkage. | CI | Runs linked in the issue comment. |

### Criterion 1 hits that remain (deliberate)

`grep -rn "LinuxApp\|AgentEyesLinux" --include "*.cs" --include "*.axaml" --include "*.csproj" --include "*.sh" --include "*.yml" .`
(excluding bin/obj/proof) returns exactly two lines, both historical comments:

```
scripts/linux/install.sh:113:# running. AgentEyesLinux is the name the app had before issue #116: an install over an older one must
src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj:4:       first written for Linux as AgentEyes.LinuxApp in issue #107). ...
```

**There is also old-name CODE that this grep cannot see**, because the word is split by a regex.
`install.sh:116` is `pgrep -f "^$app_lib/AgentEyes(Desktop|Linux)( |\$)"`, and the test that pins it
(`DesktopAppPlatformTests.cs`) carries the same literal. That is deliberate: an install over a pre-#116
install must stop the running `AgentEyesLinux`. Otherwise the new app finds it holding the single-instance
socket, hands it the launch and exits. Observed on devlinux: old pid 3035138 was stopped. I found it by
reading install.sh, not by the grep. The grep is a source scan and is blind to any split or computed name.

## Tray, per platform (criterion 10)

- **Linux (devlinux), observed:** the icon appears (moon, `linux-tray-topbar.png`, GNOME + Ubuntu
  AppIndicator). The item is registered as `org.kde.StatusNotifierItem-<pid>-0` with `ItemIsMenu=false`.
  Its menu, read over D-Bus (`com.canonical.dbusmenu.GetLayout`), has all five entries, and the status
  line reads "Ready to record" from the Control API. Closing the window hid it to the tray (`[App] window
  closed to the tray`). Sending `org.kde.StatusNotifierItem.Activate` brought it back (`[App] ShowWindow`,
  Map State IsViewable).
  **What I did NOT do:** a physical mouse click on the icon, or opening the menu on screen. The project
  rules forbid synthesizing input on the shared desktop without warning the human. So "clicking raises the
  window" is proven at the protocol level, Activate. Whether the AppIndicator extension sends Activate on
  a left click, or opens the menu instead, is unobserved. QA: click it once and report.
- **macOS: not run.** Known risk from the issue: `TrayIcon.Clicked` may not fire when the icon owns a
  menu. Report: does the icon appear in the menu bar; does a click open the menu; does any click raise
  the window; does "Show AgentEyes" raise it.
- **Windows: not run.** Report the same four things for the notification area (it may sit in the
  overflow chevron).

A negative finding on either is a PASS for this issue and becomes a Phase 4 issue.

## Gates (devlinux)

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64      Build succeeded, 0 Error(s), 1 Warning(s) (pre-existing CS8602 AlwaysOnEngine.cs:901)
dotnet test  tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release              Passed! Failed: 0, Passed: 278, Total: 278   (main: 268 -> +10 new)
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac   Build succeeded, 0 Error(s)
dotnet build src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj -c Release                   Build succeeded, 0 Error(s)
```

Repeatability: the full Linux suite ran 10 times in a row under `CI=true`, 10/10 with 278 passed. The 45
desktop-app tests also ran as 6 concurrent processes x 3 rounds, 18/18 passed.

**Cross-compile from Linux** (Microsoft .NET 8 SDK; Ubuntu's source-built SDK has no WindowsDesktop SDK):
- `dotnet build src/AgentEyes.DesktopApp -c Release -p:EnableWindowsTargeting=true -p:Platform=x64`:
  0 Error(s). The output `net8.0-windows10.0.19041.0/agenteyes.dll` has gdigrab=2, x11grab=0, pulse=0,
  avfoundation=0, an NAudio reference, and `libvlc/win-x64/` present.
- `dotnet build src/AgentEyes.DesktopApp -c Release -p:AgentEyesUnixPlatform=mac`: 0 Error(s). The
  engine has x11grab/pulse/v4l2/xrandr=0, avfoundation=2, screencapture=4, and
  `libvlc/osx-arm64/lib/libvlc.dylib` + `osx-x64` present.
- Caution for anyone building by hand: the mac and Linux flavours share `bin/Release/net8.0`. A mac
  build there leaves `libvlc/osx-*` behind for the next Linux build. CI jobs are separate machines, so CI
  is unaffected.

## Evidence files in this folder

- `linux-install-transcript.txt`: the installer run on devlinux, before and after process lists, and the desktop entries it wrote.
- `linux-runtime.txt`: the desktop-entry launch, WM_CLASS, /health at screenshot time, the tray over D-Bus, and the single-instance handoff.
- `linux-window.png`, `linux-tray-topbar.png`: the window (1280x720, xwd of the window) and the top bar (ffmpeg x11grab, cropped).
- `mutation-unit-tests.txt`: the raw output of the 12 mutations below.
- `linkage-checks.txt`: the three CI linkage steps run against 3 good and 10 bad outputs. All 3 good runs exit 0 and all 10 bad runs exit 1.

## Guards disabled, and what failed (by name)

Each mutation was applied to the source, then `dotnet test tests/AgentEyes.Tests.Linux --filter
FullyQualifiedName~DesktopApp` was run, then the source was restored. Every one FAILED the named tests:

| Mutation | Failing test(s) |
|---|---|
| M1 Shell: mac opener -> xdg-open | `OpenerFor_EachPlatform_ReturnsThatPlatformsOpener(mac)` |
| M2 Shell: windows opener -> xdg-open | `OpenerFor_EachPlatform_ReturnsThatPlatformsOpener(windows)` |
| M3 Program.WmClass back to AgentEyesLinux | `WmClass_IsTheAssemblyName`, `InstallScript_NamesTheBuiltExecutableAndWmClass`, `DesktopEntry_Tray_StartsInTheTrayAndIsAutostartEnabled` |
| M4 install.sh: `pgrep -x AgentEyesDesktop` (the truncation bug) | `InstallScript_StopsTheRunningAppByPath_NotByTruncatedProcessName` |
| M5 install.sh launches AgentEyesLinux | `InstallScript_NamesTheBuiltExecutableAndWmClass` |
| M6 install.sh StartupWMClass=AgentEyesLinux | `InstallScript_NamesTheBuiltExecutableAndWmClass` |
| M7 pipe: first instance never raises the command | `TryAcquirePipe_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull` |
| M8 pipe: serves one handoff then stops | `TryAcquirePipe_SecondLaunch_...` ("stopped listening after one handoff") |
| M9 pipe: first launch does not claim the name (no FirstPipeInstance) | `TryAcquirePipe_SecondLaunch_...` |
| M10 socket: first instance never raises the command | `TryAcquireSocket_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull` |
| M11 AppSettings autostart entry writes the old WM class | `DesktopEntry_Tray_StartsInTheTrayAndIsAutostartEnabled` |
| M12 pipe: Dispose returns before the pipe is closed | `TryAcquirePipe_SecondLaunch_...` (the next launch hands off to a dying instance) |

The CI linkage checks were run locally against good and deliberately BAD outputs (an engine dll from
another platform swapped in, libvlc deleted, an empty folder):

- **Windows check:** fires on the Unix engine (x11grab, pulse, v4l2, no NAudio), the mac engine
  (avfoundation, no NAudio), no libvlc, and an empty folder.
- **macOS check:** fires on the Linux engine and on no libvlc.
- **Linux check:** fires on the mac engine, the Windows engine and an empty folder.

All three pass on the good build. The one exception: the Linux cross-build makes no Windows apphost, so
for the local Windows runs the `.exe` was a stub file. On the CI windows job the real `.exe` is checked.

**A defect the mutations found in my own first version:** the pipe test failed intermittently. The
cause was real, not the test. Between one pipe instance closing and the next being created, a second
launch found no pipe, or on Linux a closing listener ("Broken pipe"). Separately, Dispose returned
before the pipe was closed. Before the fix, 8 of 40 repeated runs passed. After it (commit 09fd207:
FirstPipeInstance claim, next instance before closing, Dispose waits), 40 of 40 passed.

## Not covered, and how I know

- **Windows pipe semantics.** The pipe tests run on Linux, where .NET emulates named pipes over Unix
  sockets. `FirstPipeInstance` failing across PROCESSES is a Windows kernel behaviour. The Linux test
  only shows it within one process (M9 proves the test sees the claim in-process). QA criterion 11 on
  Windows, including two simultaneous launches, is the only proof.
- **Any window on macOS or Windows.** Nothing here opens a window off Linux. Known by construction: no
  test or step in this PR starts the GUI on those platforms.
- **The tray on a physical click**, on any platform (see above).
- **Shell.Open at runtime** off Linux. `OpenerFor` is unit-tested as a pure mapping. Nobody has run
  `explorer.exe <folder>` or `open <folder>` from this app. Settings -> "Open logs" or tray -> "Open
  recordings folder" exercises it.
- **Linux-isms that remain, as the issue scoped them out:** `systemctl` in MainWindow (offer to start
  the service), the XDG autostart entry for "Run at login", `linux-app.json`, and "for Linux" in the
  About text. On Windows and macOS these are wrong or inert. They are parity work (Phase 3+), not
  needed to open the window. Found by `grep -n "systemctl\|XDG\|for Linux"` over the app. That is a
  text scan, so it may not be complete.
- **`release.yml`** was not changed. It does not build the desktop app. Checked by `grep -n "LinuxApp\|DesktopApp" .github/workflows/release.yml`, which returned no hits.

## State of devlinux after this work

- `scripts/linux/install.sh` from this branch replaced BOTH the recorder service and the app. The service
  it replaced was **#113's branch build**: its agenteyes.dll contains the `CAPTURE FAULT at elapsed`
  string, which `main` does not have. Before installing I backed up `~/.local/lib/agenteyes`,
  `~/.local/lib/agenteyes-app`, both `.desktop` files and the unit. After the evidence I restored all of
  them. `diff -r` against the backup showed them identical. agenteyes.dll sha256 is `77586071...298284e`,
  the same as before. The service was restarted and `/health` returned ok:true with MainPID 2454852.
- The previous app (`AgentEyesLinux`, which an earlier install started at 13:39) was relaunched with
  `--tray`, pid 2455039, so the top bar looks as it did. My own app processes (2450474, 2451234, and the
  second-launch 2453867, which exited by itself) are all gone.
- `~/Videos/AgentEyes`: nothing deleted. `2026-11-14_102532_video` is present.
  `2026-11-14_093032_video` is **NOT present**. Its last log line is 09:32:23 today, hours before this
  session, and no command of mine touched `~/Videos` (only `ls`). Flagged to the lead.

I believe this is finished for Linux, and ready for QA on macOS and Windows.

---

# Developer pass 2 - the macOS gate (answering the macOS QA FAIL)

Machine `devthrottle-mac-mini`, branch `issue-116-desktop-app`. This pass answers the one defect in
`qa-report-macos.md`: three tests in `tests/AgentEyes.Tests.Linux/DesktopAppPlatformTests.cs` failed
on a Mac (branch Failed 3 / Passed 274; `origin/main` Failed 0 / Passed 267 on the same machine).

**It did not stay a test-only pass.** Fixing the fixtures uncovered a real macOS product defect that
no test could reach before, and it is fixed here under authorisation from the Implementation Lead,
who also amended criterion 11 for it (see "Criterion 11 is amended" below).

## 1. Was the 104-character overrun the product or the fixture? THE FIXTURE.

QA asked this and was right to. Established from the code, not assumed:

- There is exactly **one** product call site: `Program.cs:40` -> `SingleInstance.TryAcquire` ->
  on Windows the named pipe (no `sun_path` limit applies), everywhere else
  `TryAcquireSocket(command, SocketPath)`.
- `SocketPath` (`SingleInstance.cs:57-59`) is the only path the product ever passes:
  `$XDG_RUNTIME_DIR`, or the temp directory when that is unset, plus `agenteyes-app.sock`.
  **Measured on this Mac: 67 bytes.**
- `grep -rn UnixDomainSocketEndPoint src/` returns exactly two hits, both inside `SingleInstance`,
  both built from that one path.
- The two over-long paths were constructed **only in the test file**: 121 bytes for the socket
  (`RunRoot` carries a timestamp and a pid) and 107 bytes for the pipe (.NET maps a named pipe on
  Unix onto `GetTempPath() + "CoreFxPipe_" + name`). Both measured, both over the 104-byte limit.

**The limit of that claim, stated rather than hidden:** the product's path length rides on an
environment variable, so a `TMPDIR` or `XDG_RUNTIME_DIR` deliberately set longer than 86 characters
would still overflow. The real shapes do not: the macOS per-user `TMPDIR` is a fixed 49 characters,
and Linux `/run/user/<uid>` is 14-15. That is a configuration hazard, not a defect, and there is now
a test that would catch it if the product's own path ever grew (see M2 below).

## 2. What changed

### Tests

| File | Change |
|------|--------|
| `LinuxTestIsolation.cs` | `NewSocketDir()` and `NewPipeName()` - short enough to fit `sun_path` on every platform - and `MaxUnixSocketPathBytes = 104` (104 on macOS, 108 on Linux; 104 is the one that must hold everywhere). |
| `DesktopAppPlatformTests.cs` | `PlatformName_OnThisLinuxHost_IsLinux` -> `PlatformName_OnAnyHost_NamesTheOperatingSystemThisProcessRunsOn`. New `SocketPath_OnThisHost_FitsTheUnixSocketPathLimit`. Both handoff tests build short paths and assert the fixture's own path fits before using it. `AssertHandoff` now also asserts that **shutdown returns** and **delivers no command of its own**. |

The platform test is **host-independent, not skipped**. QA pointed at `LinuxOnlyFact` and that is the
right pattern for a Linux operating-system convention, but this fact is not Linux-specific: the
behaviour worth pinning is "whatever host this is, the app names it correctly and therefore picks
that host's opener". Expected comes from a file only that operating system has
(`/System/Library/CoreServices/SystemVersion.plist`, `/proc/version`, the Windows system
directory), not from `OperatingSystem.IsX`, so it is not a mirror of the implementation.

It earned its keep twice, against me. I first took the expectation from
`RuntimeInformation.OSDescription`: that failed here because the description is `macOS 26.5.2`, not
`Darwin ...`, and then failed on Linux CI because .NET answers with the distribution's pretty name -
`Ubuntu 26.04.1 LTS`, which names no platform at all. Both times the `throw` on an unrecognised host
is what caught it, which is exactly why that arm is there instead of a default: a default would have
passed without checking anything.

**The lesson is worth more than the test.** I ran "the Linux suite" on a Mac and called it a Linux
run. It is not one - `tests/AgentEyes.Tests.Linux` on a Mac is the Linux PROJECT on a macOS HOST, and
for a host-dependent fact those are different machines. The only real Linux run available from here
is CI, so anything host-dependent has to go round CI before it can be claimed.

Worth saying plainly: the OLD assertion did not merely fail on a Mac, it **rewarded the bug** there.
`Assert.Equal("linux", Shell.PlatformName)` is exactly what a `PlatformName` that wrongly answered
"linux" on macOS would satisfy. Mutation M1 below makes that concrete.

### Product - `SingleInstance.Dispose`, and nothing else

Once the fixture path fit, the socket test reached code that had never run on a Mac, and **hung
forever**. The hang is in product code:

- The product's own log ends on the `Dispose` entry line and nothing follows, in three separate
  runs: `Accept: 'view:library'`, `Accept: 'settings:account'` - the handoff itself works perfectly -
  then `[SingleInstance] Dispose: socket ...`, then nothing. A single-test run was still going after
  45 seconds with no result line.
- A 30-line standalone repro referencing nothing from AgentEyes - bind a Unix socket, run `Accept()`
  on a background thread, `Dispose()` the listener - prints
  `RESULT: Dispose DID NOT RETURN within 20 s - HUNG` on this Mac.
- A process sample agrees from a second direction: one thread in `__accept_nocancel`, the main
  thread in the `SafeSocketHandle` sleep-wait loop.

Cause: on macOS, closing a listening socket does not wake a thread blocked in `accept()` -
`shutdown()` answers ENOTCONN on a listener - and .NET's `SafeSocketHandle` close then waits for
that pending operation. Linux wakes the blocked `accept()` itself, which is why the Linux suite and
Linux CI never saw it.

Fix (`SingleInstance.cs:205-222`): before closing the listener, connect to our own socket once. The
pending `Accept()` returns, `_running` is already false, and the loop exits. A failed wake-up logs a
warning naming the consequence rather than pretending to recover.

**Why this is not cosmetic.** `Program.cs:49` disposes this in `Main`'s `finally`. On macOS the app
therefore never finished quitting and never removed its socket file.

**Corroboration from the real app on this machine - and it is corroboration, not proof.** Across
every desktop-app launch logged here (4 launches, 11-13 and 11-15) there is not one
`[Program] Main: exited` and not one `[SingleInstance] Dispose:` line, while the same grep finds the
`[Program] Main: AgentEyes ...` start lines, so the instrument reads the file. Twice, on two
different days, the next launch logged `stale socket ... - taking it over`, which is the file a
clean `Dispose` would have deleted. **A `kill -9` would leave exactly the same trace**, so the log
is the reason to believe this bites in the real app; the repro and the test log are the proof.

**What the wake-up connection does, and does not do** (the most likely way such a fix introduces a
new defect): it carries no bytes. `Accept`'s `Receive` returns 0, `cmd` is empty, and the existing
`if (cmd.Length > 0)` guard means `CommandReceived` is not raised. `CommandReceived` has exactly two
raise sites (`SingleInstance.cs:146` and `:177`), both behind that guard, and exactly one consumer
(`App.axaml.cs:26` -> `Run(cmd)`), so no window can be shown and no command acted on. The only
observable effect is one `[SingleInstance] Accept: ''` line at shutdown, visible in the run log.
M6 below proves the test fails if that connection ever did speak.

## 3. The gate, three runs

```
1) dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=mac
   Build succeeded.  1 Warning(s)  0 Error(s)      (the warning is the CS8602 also present on main)
2) dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release
   Passed!  - Failed: 0, Passed: 226, Skipped: 0, Total: 226
3) dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
   run 1: Passed!  - Failed: 0, Passed: 278, Skipped: 1, Total: 279
   run 2: Passed!  - Failed: 0, Passed: 278, Skipped: 1, Total: 279
   run 3: Passed!  - Failed: 0, Passed: 278, Skipped: 1, Total: 279
```

Three of three, matching the three runs in which QA reproduced the failure. The skip is the
pre-existing `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, unchanged. Total is 279
rather than 278 because `SocketPath_OnThisHost_FitsTheUnixSocketPathLimit` is new.

**Before and after on the hanging test**, same machine, same command:

```
BEFORE  dotnet test --filter TryAcquireSocket_SecondLaunch   killed by a watchdog at 45 s,
        ONE test, no result line ever printed; the product log ends at "Dispose: socket ..."
AFTER   Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 5 ms
```

**CI proves nothing about this.** The macOS CI job never runs `tests/AgentEyes.Tests.Linux`, which is
exactly why these three were green in CI and red on the machine. The local runs above are the
evidence; CI is only there to show Linux and Windows did not regress.

## 4. Every check was run against a known-BAD input and FIRED

| # | Mutation | Test that fired | Message |
|---|----------|-----------------|---------|
| M1 | `Shell.PlatformName` answers "linux" on macOS | `PlatformName_OnAnyHost_...` | `Assert.Equal() Failure: Strings differ`. The OLD test PASSED under this mutation on a Mac. Re-run after the expectation source changed, and it still fires. |
| M2 | `SingleInstance.SocketPath` made 128 bytes | `SocketPath_OnThisHost_FitsTheUnixSocketPathLimit` | `SingleInstance.SocketPath is 128 bytes, over the 104-byte sun_path limit: ...` |
| M3 | pipe listener serves one handoff then stops | `TryAcquirePipe_SecondLaunch_...` | `Assert.Null() Failure: Value is not null` |
| M4 | fixture builds the old 47-character pipe name | `TryAcquirePipe_SecondLaunch_...` | `THE FIXTURE, not the product: this test's path is 107 bytes ...` (the exact 107 QA measured) |
| M5 | the wake-up connection removed | `TryAcquireSocket_SecondLaunch_...` | `Dispose did not return within 10 s on the socket transport - the listener is not being woken` |
| M6 | the wake-up connection sends "show" | `TryAcquireSocket_SecondLaunch_...` | `shutting down the socket transport delivered a command of its own: 'show'` |

M5 is the regression test for the product fix, and note what it buys beyond catching the bug: a
regression now **fails in ten seconds with a named reason** instead of hanging the suite forever.
The deadline is in the test, not in the product.

## 5. Criterion 11 is amended

The Implementation Lead amended criterion 11 on the issue. It said "launch the app twice; the second
launch raises the first window and exits" - entirely about the SECOND launch, which is precisely why
this defect survived QA: the second-launch path returns at `Program.cs:41`, before the `try/finally`
that disposes. It now ALSO requires that **the first instance exits cleanly and removes its socket**.

## 6. What QA should do, and what this pass does NOT cover

1. **The amended criterion 11, on macOS, in the running app.** Launch the app, launch it a second
   time (it should hand off and exit 0), then **quit the first instance from the tray menu** and
   check the app log for `[Program] Main: exited` and
   `[SingleInstance] Dispose: socket listener closed and ... removed`, that the process is gone, and
   that `$TMPDIR/agenteyes-app.sock` no longer exists. On macOS the app log is
   `~/Library/Application Support/AgentEyes/logs/`.
2. **The same on Linux and Windows** - the fix is in the socket transport, so Windows (named pipe)
   should be unaffected, and Linux never had the hang.
3. **The clean quit has NOT been exercised in the running app here, and I am saying so rather than
   implying otherwise.** `Quit` is only reachable from the tray menu (`App.axaml.cs:100`, the only
   `Shutdown()` call site); there is no headless route, this Mac's screen is locked, and the project
   rules forbid synthesizing input on the owner's desktop unasked. A `kill` would not exercise it
   either: `Main`'s `finally` does not run on SIGTERM. What IS proven is the same `Dispose` on the
   same transport, at unit level, three runs, plus the standalone repro of the operating-system
   behaviour underneath it.
4. Everything in "Not covered, and how I know" from pass 1 still stands.

**Trap, carried from QA and confirmed here:** `tests/AgentEyes.Tests.Linux` pins the app to
`AgentEyesUnixPlatform=linux` and shares `bin/Release/net8.0` with it, so running the macOS gate
rewrites the macOS app output as a Linux one. Measured on this machine: `x11grab 1, pulse 8,
avfoundation 0` after the gate; `0, 0, 2` after
`dotnet build src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj -c Release -p:AgentEyesUnixPlatform=mac`.
**Rebuild the app after the gate before launching or inspecting it on a Mac.** I left this machine
with the macOS build in place (0, 0, 2 re-measured after the final gate run).

## 7. Machine state

Recorder service not reinstalled and not restarted. Nothing deleted under `~/Movies/AgentEyes`;
`2026-11-15_093623_video`, added by QA as evidence, is untouched. No app was launched in this pass.

I believe this is finished.

---

# Developer pass 3 - the Windows tray Quit crash (answering the Windows QA FAIL)

Machine `SOREN_NORTH`, branch `issue-116-desktop-app` on top of `6da099a`. This pass answers the one
defect in `qa-report-windows.md` and the Implementation Lead's ruling on the issue: on Windows, Quit
from the tray crashed the app 3 of 3 with `NotSupportedException: DefaultMenuInteractionHandler is not
attached to the menu`, so `[SingleInstance] Dispose` and `[Program] Main: exited` never ran.

## 1. The cause, measured - and the hypothesis was wrong

QA's hypothesis (and the ruling's) was that `App.Quit` ran `Close`/`Shutdown` synchronously inside the
menu's click and tore the window down under the popup. **I made exactly that change first and it did
not fix it**: with Quit deferred through `Dispatcher.UIThread.Post` the app still crashed, 2 of 2, and
it crashed **before the shutdown ever ran** - `[App] Quit` is never logged
(`windows-pass3-measurements.txt`, rows 2 and 3). Our shutdown was never the trigger.

A temporary `Closing`/`Closed` trace on the tray popup (removed again) caught the real sequence with
stacks (row 3). It is Avalonia's own Windows tray popup, `Avalonia.Win32.TrayIconImpl+TrayPopupRoot`,
which closes itself on `Deactivated`. After ANY item is clicked:

```
Close #1:  DefaultMenuInteractionHandler.Click -> CloseMenu -> TrayIconMenuFlyoutPresenter.Close -> Window.Close
             -> CloseInternal -> WindowImpl.Dispose -> DestroyWindow -> WM_DESTROY -> Window.HandleClosed
Close #2:      -> WindowBase.HandleDeactivated -> TrayPopupRoot_Deactivated -> Window.Close     (re-entrant)
```

The nested Close runs while the first is still inside `DestroyWindow` (Avalonia's `WindowImpl.Dispose`
clears its HWND only after `DestroyWindow` returns - read from the decompiled 11.3.22 source), so the
popup is torn down a second time and its menu presenter is detached twice; the second detach throws in
the message loop. It is intermittent: the re-entrant close happened in every run I traced, the crash in
5 of 28 unguarded runs today (it was 3 of 3 for QA and for my first three runs). Avalonia 11.3.23 (the
latest 11.x) and 12.1.6 have the same `TrayPopupRoot` and the same `Dispose` ordering, so an upgrade
does not fix it.

**The criterion-10 popup-close crash is the same defect.** QA saw it 2 of 6 when closing the popup
without choosing anything. In an A/B in the same session (row 5) the unguarded build crashed exactly
there - on the WM_CLOSE dismiss, a re-entrant close, then the same exception - and the fixed build did
not. One change fixes both, so no separate issue is filed.

## 2. What changed

| File | Change |
|---|---|
| `src/AgentEyes.DesktopApp/TrayPopupCloseGuard.cs` (new) | **The fix.** A class handler on `Window.WindowOpenedEvent` that, for that one Avalonia type only, cancels in `Closing` any close that starts while the popup is already closing. The first close always proceeds; the duplicate is dropped, so the popup is torn down once. If anything else ever cancels the first close (nothing does today), a check posted after it sees the popup never reached `Closed` and lets the next close through, so the menu can still be dismissed (from the code review). Logs every decision with `active=`. Inert on Linux (D-Bus menu) and macOS (native menu): the type never opens there. |
| `src/AgentEyes.DesktopApp/DeferredQuit.cs` (new) | Quit is posted to the UI dispatcher at Background priority instead of running inside the menu's click. **Not the fix - measured above - but kept as the shape the ruling asked to pin.** With the guard and a synchronous Quit the app also exits cleanly (row 4), but `Shutdown` then closes the popup from inside its own click handling and the menu's `CloseMenu` runs on a closed window. Deferred, the menu closes its popup itself and the shutdown runs after it. A second Quit while one is pending is ignored and logged. |
| `App.axaml.cs`, `TrayController.cs` | Install the guard before the tray icon exists; the tray takes a `DeferredQuit`, not an `Action`, so its Quit item cannot be wired straight to a shutdown again. `Quit` is renamed `ShutdownNow`; its log line is unchanged (`[App] Quit (the recorder service keeps running)`). |
| `tests/AgentEyes.Tests.DesktopApp/` (new, **in `AgentEyes.sln`**) | The first desktop-app test project the Windows gate runs. Its TFM follows the app's (`net8.0-windows10.0.19041.0` on Windows), so it links the Windows engine: gdigrab 2, x11grab 0 in its output. |
| `tests/Shared/DesktopAppQuitTests.cs` (new) | 13 tests, compiled into the new project AND `AgentEyes.Tests.Linux` (so Linux CI runs them). They pin: the guard's decisions (first close allowed, re-entrant ones cancelled, a close cancelled elsewhere marks nothing and is reopened); that `Avalonia.Win32.TrayIconImpl+TrayPopupRoot` still exists and is a `Window` (an Avalonia upgrade that renames it fails here instead of silently disarming the guard); that App installs the guard before creating the tray; that `Request` never runs the shutdown inside the caller and posts it once; that `TrayController` only accepts a `DeferredQuit`; and, as a source fact, that the only `.Shutdown(` in the app is in `ShutdownNow`, handed only to `DeferredQuit`, whose production constructor posts to the dispatcher. |
| `scripts/windows/tray-quit-repro.ps1` (new) | QA's repro as a script: start `--tray`, open the menu with the shell's own tray callback, optionally dismiss it N times with WM_CLOSE, click Quit, then report the exit code, the three log lines, any unhandled exception and the pipe. **It posts messages only to the test app's own windows** - no input at the cursor, no other window touched; the popup is on screen for under a second per open. |
| `windows-pass3-quit-runs.txt`, `windows-pass3-measurements.txt` (new) | The final runs, and every measurement in section 1. |

## 3. The Windows runtime repro, passing (ruling artefact 1)

`windows-pass3-quit-runs.txt`: **10 of 10 PASS** on the solution-built binary of the committed code:
plain Quit, Quit after one or two dismissals, and "dismiss, stay alive, then Quit". Every run shows
`[App] Quit`, `[SingleInstance] Dispose: pipe agenteyes-app-soren`, `[Program] Main: exited`, **exit
code 0**, no unhandled exception, and the pipe gone afterwards. In every run the guard logged the
re-entrant close it cancelled. Across the session: **0 crashes in 38 guarded runs**, against 5 in 28
unguarded. Because the crash is intermittent these are rates, not a proof of absence; the measurements
file states the arithmetic.

Note for QA: the binary's ProductVersion embeds `6da099a` because it is stamped from HEAD at build time
and was built just before the commit. Rebuild from the pass 3 commit before you verify.

## 4. What the Windows gate covers now, and what it still cannot (ruling artefact 2)

- **Now covered** by `dotnet test AgentEyes.sln -c Release` (and the CI windows job, which runs it):
  the 13 tests above - the guard's logic, that its target type still exists, that it is installed, and
  that the quit path cannot run the shutdown inside a click.
- **Still not covered by any gate:** the crash itself. It happens in Avalonia's Win32 message loop, only
  with a real tray popup holding activation, and intermittently. No unit test reaches it and CI never
  launches the app. The only check is `scripts\windows\tray-quit-repro.ps1`, run by an agent on a
  Windows desktop (it shows the popup briefly). Also not covered: an Avalonia upgrade that **changes the
  popup's behaviour without renaming it** - the type-exists test catches a rename only.
- The other desktop-app tests (`DesktopAppTests.cs`, `DesktopAppPlatformTests.cs`) still live in
  `AgentEyes.Tests.Linux` and still do not run on Windows. Moving them is out of this issue's scope.

## 5. Linux and macOS need re-verification - not "should be fine"

**Linux must be re-run before this passes.** It passed criterion 11 four of four through the real
D-Bus tray menu, and this change is in shared code. My reasoning that it is unaffected - the guard only
matches a Win32 type, and the deferral changes WHEN the shutdown runs, not what it does - is reasoning,
not a measurement. The platform difference is exactly the risk the ruling named: on Linux the Quit click
arrives over D-Bus, and posting the shutdown to the dispatcher from there has not been observed.
devlinux was offline, so **nothing in this pass ran on Linux**: no build, no `AgentEyes.Tests.Linux`
run, no tray Quit. The Linux CI job compiles and runs the 13 shared tests on push; that is unit
coverage, not the runtime re-run. QA on devlinux: re-run the criterion 11 tray Quit (the steps in
`qa-report-linux-criterion11.md`) and expect new `[DeferredQuit]` lines between the click and `[App] Quit`.

**macOS was not re-run either.** Its Quit also goes through `DeferredQuit` now. Pass 2 never exercised
a tray Quit on the Mac (section 6.3 of pass 2), so that gap is unchanged.

## 6. Scope rulings, honoured

- The tray presentation findings (icon in the overflow, off-white idle icon, UIA cannot invoke the menu
  items) are #163 and are **not touched here**. The repro script works around the UIA gap by posting to
  the popup; it does not fix it.
- **`linux-app.json` stays, deliberately.** The settings file name (`Model/AppSettings.cs:23`) survived
  the rename. Renaming it without a migration would silently reset every existing Linux user's settings,
  which is worse than a stale name. It is a follow-up that must carry a migration, not an oversight.
- The 41-second first cold start is out of scope. I saw one slow first start here too (the first tray
  popup took over 5 s to appear on the first launch after a build); I did not measure it further.

## 7. Gates run, and the owner's machine

- `dotnet build AgentEyes.sln -c Release`: `Build succeeded.`, `0 Error(s)`. The output lists
  `AgentEyes.DesktopApp -> ...\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesDesktop.dll` and
  `AgentEyes.Tests.DesktopApp -> ...`. The warnings are in `AgentEyes.Core` and `AgentEyes.Tests`, which
  this pass does not touch.
- `dotnet test tests/AgentEyes.Tests.DesktopApp -c Release`: `Failed: 0, Passed: 13`.
- **Mutation check**: three deliberate regressions, each caught by its test and each restored -
  synchronous Quit (`DesktopApp_LifetimeShutdown_IsReachedOnlyThroughDeferredQuit`), guard not installed
  (`App_InstallsTheGuard_BeforeTheTrayIconExists`), guard never cancels
  (`CancelThisClose_ReentrantSecondAndThirdClose_AreCancelled`).
- **I did NOT run the whole solution test suite** (`dotnet test AgentEyes.sln`), per the ruling: until
  #151 is on `main` that suite starts real ffmpeg processes and its cleanup kills ffmpeg by start time.
  This change touches neither `AgentEyes.Core` nor the WPF app, so the targeted project is the relevant
  one. The CI windows job runs the full suite on push.
- **CI on the pass 3 commit `e5ce76c`, run 43872825652: linux, macos and windows all `success`.**
  windows `Test AgentEyes.sln`: `AgentEyes.Tests.DesktopApp.dll` `Failed: 0, Passed: 13` and
  `AgentEyes.Tests.dll` `Failed: 0, Passed: 2297`. linux `Test the Linux build`: `AgentEyes.Tests.Linux.dll`
  `Failed: 0, Passed: 292`, which now includes the 13 shared tests - so the Avalonia.Win32 type check
  passes on Linux too. Unit level only; section 5 still stands.
- The owner's recorder: `GET /status` read `idle` immediately before every app launch in this pass
  (about 70), and each batch was set to stop itself if it was not. No ffmpeg was started or killed. Every
  process I stopped was an `AgentEyesDesktop` test instance I had started, left over after a crash or a
  harness timeout. No recording, preset or audio setting was touched, and the installed WPF app ran
  throughout. No input was sent to any other window. **But the tray popup takes the foreground while
  it is open** (Avalonia shows it activated), so each run took keyboard focus from whatever the owner
  had in front for up to about a second per open - roughly 100 times over about 45 minutes. It is the
  menu's own behaviour, the same as a real right-click, but it is a focus steal and QA's re-run will
  repeat it.

---

# Developer pass 4 - the merge with main, and a test that pins the guard (answering the Review Gate)

Machine `SORENLAPTOP`. Every count below is from SDK 10.0.301 building net8.0 on runtime 8.0.21. Branch
`issue-116-desktop-app`, previously at `0f54fba`, with main `17c244d` merged in. This pass answers the
Review Gate's rejection (issue comment 6922665900) and the Lead's ruling (6922692531). It changes no
product source. Every file under `src/` and `scripts/` that differs from `0f54fba` (26 files) is
identical to `main`'s version, so the merge brought those changes in and this pass wrote none of them. The guard, the
deferral and the four QA reports on this branch are untouched.

## 1. Defect 1 - the merge, each conflict and why

I merged rather than rebased. The branch is pushed and carries four QA seats' commits, and a merge keeps
every SHA they cite. Merge commit `f940b2b` (it also carries the test in section 2).

| File | Resolution | Why |
|---|---|---|
| `docs/cencon/proof/issue-142/qa-report-linux.md`, `linux/row4.txt`, `row5.txt`, `row6.txt` (add/add) | **`main`'s version, exactly** | `main`'s `ae80c4d` (#158) re-ran rows 4-6 and records **row 5 FAIL**: the audio drift that became #157. This branch carried the older `0c62e33` pass. Keeping ours would have turned a recorded failure back into a stale pass. They are #142's files, not #116's. |
| The rest of `docs/cencon/proof/issue-142/` (no textual conflict) | **also `main`'s tree, exactly** | `0c62e33` also added 16 files at `linux/row*.{txt,png}`. `main` holds the same files moved to `linux/earlier-c80cb9d/`: 15 byte-identical, plus `row1-install-transcript.txt`, which is REDACTED on `main` (a temp path and the owner's sign-in address replaced). Git would have kept ours at the old paths without a conflict. That would have left stale duplicates and put the unredacted transcript back onto `main`, so I removed them. `git diff origin/main f940b2b -- docs/cencon/proof/issue-142/` is empty. The one thing dropped: the "row 10 re-confirmed at 0e1b889" addendum that this branch's `78a973e` added to the #142 report. Its evidence is #116's, and it stays in `qa-report-linux-criterion11.md` and `linux-criterion11-evidence.txt`. |
| `.github/workflows/ci.yml`, windows job | **both** | Order: build, `Test AgentEyes.sln`, then #116's "The Windows desktop app must link the Windows engine", then #151's second pass with `AGENTEYES_FFMPEG_TESTS=1`. They do not interact: the linkage check reads the build output, and the opt-in pass runs with `--no-build`. |
| `.github/workflows/ci.yml`, linux job | **both** | Order: Core build, `Test the Linux build`, #116's desktop-app build and Linux-engine linkage check, #151's ffmpeg install and opt-in pass (`--no-build` on `AgentEyes.Tests.Linux`), then the macOS compile check. The desktop-app build between the two test passes writes to the app's and the engine's output folders, not the test project's. So the `--no-build` pass runs the test assembly the first pass built. |
| `tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj` | **both** | This branch's `DesktopAppQuitTests.cs` link AND `main`'s links: `MuxLockTests`, `FfmpegMuxShapeTests` (#136), `OwnedFfmpeg`, `OwnedFfmpegTests`, `FfmpegOptInAttributes` (#151) and `MicChainOrderTests` (#146). |

After the merge `gh pr view 141` reports `mergeable: MERGEABLE`. It was `CONFLICTING`.

Criterion 6, re-checked as the gate asked: the merge does not change `scripts/linux/`, which holds
`install.sh` and the service file. `main` has not touched it since the merge base `af0ddb1`, so the
carried-forward substitution still holds for this tree.

## 2. Defect 2 - a test that fails when the fix is removed

The gate deleted the guard's `e.Cancel = true` (G1) and inverted its type filter (G2), and all 13 tests
stayed green. They pinned the decision table (`CloseState`) and the deferral, but nothing connected that
decision to a window's `Closing` event. So the tests covered the half of the change that is not the fix.
The cause is the one the Lead named: the tests were written for the deferral hypothesis before pass 3
refuted it.

**New: `TrayPopupCloseGuardWindowTests`**, two tests in `tests/Shared/DesktopAppQuitTests.cs`, so both
the Windows gate and the Linux CI suite run them:

- `TrayPopup_ClosedReentrantly_SecondCloseIsCancelledByTheGuard` is **the regression test for the fix.**
- `OtherWindow_ClosedReentrantly_IsNotTouchedByTheGuard` checks that the guard leaves every other window alone.

How they reach the real guard rather than a copy of it:

- They run on Avalonia's headless platform (`Avalonia.Headless` 11.3.22, the app's Avalonia version,
  added to both test projects). A fixture starts one headless session and calls the production
  `TrayPopupCloseGuard.Install()` once, the same call `App` makes. The guard's own class handler on
  `Window.WindowOpenedEvent` then attaches its own `Closing` handler, so lines 56 and 63 run as written.
- Each test shows a window and closes it. The window then closes itself AGAIN from inside the first
  close, from `OnClosed`. Avalonia runs `OnClosed` while the first close is still disposing the window,
  which is where the real popup's `Deactivated` handler re-closes it. The window records every close's
  FINAL `Cancel`, after all `Closing` handlers have run, the guard's included. Expected for the tray
  popup: `[false, true]`, meaning the first close goes through and the re-entrant one is cancelled.
  Expected for any other window: `[false, false]`.
- Each test first asserts that exactly two closes were recorded. A run where the re-entrant close never
  happened cannot pass as "nothing cancelled".
- **The stand-in.** The guard picks windows by full type name. The real
  `Avalonia.Win32.TrayIconImpl+TrayPopupRoot` is internal to `Avalonia.Win32` and opens only on the
  Win32 platform. So the test declares, in its own assembly, a window with exactly that full name. A
  separate test, `TrayPopupTypeName_NamesAvaloniasWindowsTrayPopup_WhichIsAWindow`, still pins that the
  real type exists under that name. Together the two say: the name is still right, and the guard cancels
  a re-entrant close of a window with that name.

**The mutations.** I made each one on the uncommitted tree and restored it with `git checkout --`;
`git status` showed the file clean after each. Full logs: `pass4-mutation-G1-cancel-deleted.txt`,
`pass4-mutation-G2-filter-inverted.txt`, `pass4-mutation-unmutated.txt`.

| Tree | `dotnet test tests/AgentEyes.Tests.DesktopApp -c Release` | Failing test(s) and message |
|---|---|---|
| unmutated | Passed 15, Failed 0 | - |
| **G1**: `TrayPopupCloseGuard.cs:63` `e.Cancel = true;` deleted | Passed 14, **Failed 1** | `TrayPopup_ClosedReentrantly_SecondCloseIsCancelledByTheGuard`: "the tray popup's re-entrant second close was NOT cancelled - TrayPopupCloseGuard did not stop the double teardown that crashes the app on Windows (issue #116)" |
| **G2**: line 56 inverted to `if (IsTrayPopup(window.GetType())) return;` | Passed 13, **Failed 2** | `TrayPopup_ClosedReentrantly_SecondCloseIsCancelledByTheGuard`: the same message as under G1. `OtherWindow_ClosedReentrantly_IsNotTouchedByTheGuard`: "TrayPopupCloseGuard cancelled a close of a window that is not the tray popup - it must guard Avalonia's Windows tray popup and nothing else" |

`TrayPopup_ClosedReentrantly_SecondCloseIsCancelledByTheGuard` fails under both mutations. I ran the
mutations on Windows only. Linux CI compiles and runs the same file unmutated (section 3).

**What this test does NOT cover.** It proves the guard cancels the re-entrant close of a window with the
popup's name. It does not prove the crash is gone. That needs Avalonia's Win32 message loop and a real
popup, and it stays with `scripts/windows/tray-quit-repro.ps1` and pass 3's runtime rates. It also cannot
catch an Avalonia upgrade that keeps the popup's name but changes how it closes. Pass 3's section 4 said
the 13 tests covered "the guard's logic". That was true only of the decision table. From this pass on,
the gate also pins the guard's effect on a window's `Closing` event.

**The tests no longer present the deferral as the fix.** The test file's header now says the guard is
the fix and gives pass 3's measured rates. It says `DeferredQuit` is "NOT the fix and does not prevent
the crash" and is pinned only as a shape. The DeferredQuit section heading now reads "a pinned shape,
NOT the crash fix". The deferral tests themselves are kept, as the Lead ruled. I checked the test names,
and none presents the deferral as a fix or a crash guard.

**Comments in the app's source that still call the deferral the cure (left for #168).** I searched
every file this branch changes for the deferral. Three product-source comments still describe it as the
cure:
- `src/AgentEyes.DesktopApp/DeferredQuit.cs:9-22` ("WHY IT IS DEFERRED"). #168 item 1 names this one.
- `src/AgentEyes.DesktopApp/App.axaml.cs:27`: "Quit is DEFERRED until the tray menu has closed - see DeferredQuit".
- `src/AgentEyes.DesktopApp/TrayController.cs:47-48`: "closing the app under it crashed (issue #116, see DeferredQuit)".

#168's scope includes "a sweep for the same shape in the files #116 added". The Lead kept #168 out of
this branch and product source off-limits for this pass, so I did not edit these comments. I added the
two that #168 does not yet name to #168, so its sweep cannot miss them. This handoff's pass 3 section
already says "Not the fix" (its section 2).

## 3. Gates

- `dotnet build AgentEyes.sln -c Release`: `Build succeeded.`, `22 Warning(s)`, `0 Error(s)`. Every
  warning is in `AgentEyes.Core` (CS8602) or `AgentEyes.Tests` (xUnit analyzers). None is in a file this
  pass changed.
- **The full Windows suite, run locally on the merged tree**: `dotnet test AgentEyes.sln -c Release
  --no-build`, without the opt-in (`pass4-full-suite-windows.txt`):
  - `AgentEyes.Tests.dll`: **Failed 0, Passed 2375, Skipped 15, Total 2390.** That is `main`'s count. The
    15 skips are #151's opt-in ffmpeg tests.
  - `AgentEyes.Tests.DesktopApp.dll`: **Failed 0, Passed 15, Total 15.**
  - Both ran from `bin\x64\Release\net8.0-windows10.0.19041.0` (the paths are in the log). 82 s wall
    clock on this laptop.
- Safety: just before the run, `GET /status` read `"State": "idle"` and `tasklist` showed no
  `ffmpeg.exe`. #151 is in this tree, so the suite's failure path kills only ffmpeg it started. I
  started and killed no ffmpeg outside the suite. No recording, preset or audio setting was touched. No
  app was launched and nothing was shown on screen; the headless platform draws nothing.
- **CI on the merged head: green on `f940b2b`, all three jobs, on both runs.** Run 43911878522 is the
  `pull_request` run, which checks out PR #141 merged into `main` `17c244d`. `main` had not moved, so
  that is the tree that would land. Run 43911867444 is the `push` run on the branch head itself. Counts
  from 43911878522:
  - windows `Test AgentEyes.sln`: `AgentEyes.Tests.dll` Failed 0, Passed 2375, Skipped 15, Total 2390;
    `AgentEyes.Tests.DesktopApp.dll` Failed 0, Passed 15. The opt-in pass (`AGENTEYES_FFMPEG_TESTS=1`):
    `AgentEyes.Tests.dll` Failed 0, Passed 2394, Total 2394; DesktopApp Passed 15. Then `[OK] the Windows
    desktop app links the Windows engine`. These are `main`'s 2390 and 2394 counts, so CI ran the merged
    suite. The gate's earlier run 43908582745 ran 2297.
  - linux `AgentEyes.Tests.Linux.dll`: Failed 0, Passed 317, Skipped 1, Total 318; the opt-in pass Failed
    0, Passed 318. `[OK] the Linux desktop app links the Linux engine`. The two new headless tests are in
    this assembly through the shared link, so they ran on Linux too.
  - macos `AgentEyes.Tests.Mac.dll`: Failed 0, Passed 283, Skipped 1, Total 284 (`main`'s 284), and both
    macOS linkage checks `[OK]`. The Mac suite does not link the shared quit tests; that is unchanged.
  - The handoff commit after `f940b2b` changes only files under `docs/`. CI runs on it too.

## 4. For QA

Product behaviour did not change in this pass. Pass 3's runtime evidence was taken on `e5ce76c`, and
`src/AgentEyes.DesktopApp/` is identical between `e5ce76c` and `f940b2b`. The merge did bring `main`'s
changes into `AgentEyes.Core` (#128, #136, #146, #151). What to verify:

1. The merge resolutions in section 1. In particular, `git diff origin/main <head> --
   docs/cencon/proof/issue-142/` must be empty.
2. Make G1 and G2 yourself and watch `TrayPopup_ClosedReentrantly_SecondCloseIsCancelledByTheGuard` fail.
3. CI is green on the head you judge, and the Linux job's `AgentEyes.Tests.Linux` count includes the
   two new tests.

I believe this pass is finished.

---

# Post-QA redaction (after Windows QA pass 4, on 46d287a)

The Lead's ruling: the owner's personal email address that this PR added must be redacted before it
merges. The four instances already on `main` are issue #173 and are NOT touched here. This is the
whole delta after QA's pass: the redaction. No product source, test, measurement, count or verdict
changed.

## 1. What was redacted

`docs/cencon/proof/issue-116/linux-install-transcript.txt`, two lines, nothing else:

    line  7  before: [OK] built /tmp/tmp.kVn8D3Ibcl/agenteyes
             after:  [OK] built <stage>/agenteyes
    line 14  before: [OK] signed in as <the owner's address, removed here>
             after:  [OK] signed in as <owner account>

The convention is the one `main` already uses, not a new one. It was checked against the
unredacted ancestor: `0c62e33:docs/cencon/proof/issue-142/linux/row1-install-transcript.txt` and
`main`'s `docs/cencon/proof/issue-142/linux/earlier-c80cb9d/row1-install-transcript.txt` differ in
exactly two lines. The mktemp staging path becomes `<stage>` and the signed-in address becomes
`<owner account>`. The `/home/...` install paths stay as evidence there, and so they stay here too.
The transcript still shows the same install: the build, the install paths, the service health JSON,
a successful sign-in, the app start, exit=0, and the before and after pids.

## 2. What the rest of the PR adds, searched by shape

Scope: every file added or modified between merge-base `17c244d` (= `main`) and `46d287a`. That is
107 files, 26 of them binary (24 png, 1 jpg, 1 LinuxApp). Only the ADDED lines were searched
(`git diff -U0`, `+` lines, 11739 lines), so content inherited from `main` is not attributed to this PR.

- Email shape, strict: `[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}`.
  1 hit, line 14 above. That hit is also the positive control: the pattern finds a known address.
- Email shape, loose: `[A-Za-z0-9._%+-]@[A-Za-z0-9]`. Hits are only `actions/checkout@v4` (CI
  excerpts) and a `// path@x` comment in a Swift helper. Neither is an address.
- Obfuscated forms: `%40`, `[at]`, `(at)`, `mailto:`. 0 hits.
- Binaries: `strings -n 6` on all 26 files, then the strict pattern. 0 hits.
- Temp and harness paths: `/tmp/`, `/private/tmp`, `/var/folders/`, `AppData\Local\Temp`, `\temp\`,
  `%TEMP%`, `$env:TEMP`, `tmp.XXXXXX`, `mktemp`, `scratchpad`, `/T/`. Hits:
  - `linux-install-transcript.txt` line 7. Redacted above.
  - `mac-pass3-gate.txt`: `/tmp/x`, a literal test argument in a test name. Not a temp path.
  - `mac-runtime.txt` and `qa-report-macos.md`: `/var/folders/b8/<redacted>/T/...`. Already redacted
    by the macOS seat.
  - `mac-gate.txt`: 4 lines carry the raw macOS per-user `$TMPDIR` segment
    (`/var/folders/b8/<32 chars>/T/`). **Reported, NOT changed.** It is not the owner's identity, and
    it is not a staging path in the `<stage>` sense. Also, those lines record a path-LENGTH failure
    (`ArgumentOutOfRangeException ... invalid length`), so rewriting the path would change the
    evidence. The same segment is already `<redacted>` in `qa-report-macos.md`. If the Lead or #173
    wants it masked, that is a separate, length-aware change.

Result: this PR adds exactly one email address, and it is now redacted. It adds one mktemp staging
path, which is also redacted. The method above is what produced that result.

## 3. Tests

Not run. This change edits text in one proof file and appends this section. No build input or test
input is touched. `AgentEyes.sln` cannot build on this Mac in any case. A suite run here would prove
nothing about the change, so none is quoted.
