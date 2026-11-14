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
