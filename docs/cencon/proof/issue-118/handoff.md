# Issue #118 - Developer handoff: AgentEyes.app on macOS

PR #179, branch `issue-118-macos-app-bundle`, from main 2d60f77 (#116 merged).
Every number below is in `dev-evidence.txt` beside this file, with the command that produced it.
Those are the developer's measurements, not QA proof.

## Where this stops, and why

This issue is implemented **up to and including an ad-hoc signed, self-contained `AgentEyes.app` that
runs on devthrottle-mac-mini, and it stops before the notarise step.** The Apple Developer Program
enrolment is pending, so there is no Team ID, no Developer ID Application certificate and no
notarytool credentials. The five signing criteria the lead added on 16 November are **deliberately
unchecked** (see the last table). **An ad-hoc bundle is not the deliverable a user can install.**
Gatekeeper's assessment rejects it (`spctl -a -vv -t exec` -> `rejected`). It runs here only because a
bundle built locally carries no quarantine attribute, so Gatekeeper never assesses it.

## The decisions

**1. Bundle identifier: `com.agenteyes.app`. Permanent. Ratified by the lead, not chosen by me alone.**
It sits under the same `com.agenteyes` prefix as the service's launchd label
(`com.agenteyes.recorder`), so the product has one naming scheme. It names the product, not the project
(`AgentEyes.DesktopApp` / `AgentEyesDesktop`), so renaming the project never resets anyone's grant.
`MacBundleTests.InfoPlist_BundleIdentifier_IsThePermanentOne` fails if it changes.
- *Not verified:* who controls `agenteyes.com`. The lead is asking the owner now whether he has a
  prefix convention, because this is the only cheap moment to change it.
- *What it means for the first signed build:* today the bundle's identity to TCC **is its code hash**
  (`codesign -d -r-` -> `designated => cdhash H"b2b257b5..."`). That hash covers `Info.plist` and the
  seal of all 657 files, so it changes with every release. Measured: the same source rebuilt gave the
  same cdhash, and a version bump alone gave `e1853140...`. With a Developer ID signature, the
  designated requirement becomes "this identifier, signed by this Team ID", which survives updates.
  So the first Developer ID build is a **new application** to the privacy settings, once. After that
  it stays the same application, provided the identifier and the team never change.
  Today none of this costs a user anything, because the app holds no grant and needs none (criterion 6).

**2. Install location: `~/Applications/AgentEyes.app`** (approved by the lead). It is per-user and
needs no administrator, which matches the per-user service install. Finder, Spotlight and Launchpad
index it.

**3. StartupHookSupport is unchanged; that is the lead's decision.** The bundle keeps the SDK default.
Measured: a startup hook still loads in the bundle's own process. So anything that can set
`DOTNET_STARTUP_HOOKS` for AgentEyesDesktop can run code as `com.agenteyes.app`. That borrows nothing
today, because the app holds no grant, but it would the day it holds one. Closing it
(`StartupHookSupport=false`) needs a replacement for the mission's only locked-screen capture route
first.

**4. The bundle layout follows Avalonia's macOS deployment model.** The whole publish goes in
`Contents/MacOS`, because the apphost needs the runtime beside it. That means every file there must be
signed: codesign refused the bundle while a managed `.dll` was unsigned. A non-Mach-O file's signature
lives in **extended attributes**. Measured: the signature survives `cp -R` (install-app.sh verifies it
again after its copy), but not zip followed by `unzip`. The notarised download therefore has to be a
format that keeps extended attributes (a `.dmg`, a `.pkg`, or a ditto zip opened by Archive Utility),
or the layout has to change. That is recorded for the follow-up, not decided here.

## Acceptance criteria

Status words: **MET** = measured by me and QA can repeat it. **NOT OBSERVABLE WHILE LOCKED** = needs a
person or an unlocked screen. **STATED-NOT-RUN** = deliberately not run, with the reason.

| # | Criterion | What I built | What I measured | How QA verifies | Status |
|---|---|---|---|---|---|
| 1 | Self-contained, no .NET, no env var | `build-app.sh`: `dotnet publish -r osx-arm64 --self-contained true` (`osx-x64` when built on Intel; untested, no Intel Mac here) | `env -i` launch (no `DOTNET_*`): host trace `Executing as a self-contained app`, hostfxr 8.0.33 resolved from the app folder, **0** of 1695 trace lines touch a global .NET location. Positive control: the framework-dependent build in the same environment exits 131, "You must install .NET". `[MainWindow] Opened`; the window was photographed. | `env -i HOME=$HOME USER=$USER PATH=/usr/bin:/bin ~/Applications/AgentEyes.app/Contents/MacOS/AgentEyesDesktop`, then grep the log (`~/Library/Application Support/AgentEyes/logs/AgentEyes-YYYYMMDD.log`) for `[pid N] ... [MainWindow] Opened`. Window: section "Screenshots" below. | MET (criterion 10 covers the clean-machine question) |
| 2 | A real application | `macOS/Info.plist`, `Contents/{MacOS,Resources}`, `PkgInfo`, `AgentEyes.icns` | `plutil -p` quoted in evidence B; `open` started it; `lsappinfo`: `bundleID="com.agenteyes.app"`, `type="Foreground"`, `Version="1.11.11"` | `plutil -p ~/Applications/AgentEyes.app/Contents/Info.plist`; `open -a AgentEyes`; `lsappinfo info -only name com.agenteyes.app` | MET |
| 3 | Double-click from Finder | nothing beyond 2 | **Not done.** No click is possible while the screen is locked. `open` goes through Launch Services, the same service a Finder double-click uses, but it is not Finder. | Unlocked screen: double-click `~/Applications/AgentEyes.app` in Finder; photograph the window; quote the `[Program] Main` and `[MainWindow] Opened` log lines with their timestamps | NOT OBSERVABLE WHILE LOCKED |
| 4 | Dock icon + "AgentEyes" in the menu bar | the `.icns`; **a fix**: `App.axaml` `Name="AgentEyes"` | **Defect found and fixed.** The first install showed in Launch Services as `"Avalonia Application"`, Avalonia's default `Application.Name`, which overrides `CFBundleName`. After the fix: `"LSDisplayName"="AgentEyes"`, and the Window Server's owner name is `"AgentEyes"`. The icon macOS resolves for the bundle is the AgentEyes moon (`dev-resolved-icon-bundle.png`); control: the bare apphost resolves to the generic `exec` icon. The dock and the menu bar themselves were **not** photographed: AgentEyes is never frontmost while locked (the frontmost app is `loginwindow`), the menu-bar window shows another app's menus, and the Dock window captures blank. | Unlocked screen: open the app, make it frontmost, photograph the dock and the menu bar. Note (#116): on macOS 26 the TRAY item belongs to Control Center (pid 431), not to the app. | Name MET; photo NOT OBSERVABLE WHILE LOCKED |
| 5 | Installer installs both | `install-app.sh` (the app only); `install.sh` calls it after the service, and the service half of `install.sh` is unchanged | `install-app.sh` run twice. The second run went over a running install and stopped it by path (pid 4808). Before and after each run: service pid 50642, its binary's sha256 and `/health ok:true` all unchanged. The app launched from `~/Applications`. | Re-run `scripts/mac/install-app.sh`; check `/health` before and after. **Full `install.sh`: STATED-NOT-RUN** - it boots out and re-registers `com.agenteyes.recorder`, the agent that holds this machine's only Screen Recording grant, which cannot be re-granted while the screen is locked; risking it would cost every remaining macOS verification in the mission (approved by the lead). CI's macOS job builds and verifies the bundle with `build-app.sh` from a clean checkout. | App half MET; full route STATED-NOT-RUN |
| 6 | Permission story stated and true | install texts (`install.sh`, `install-app.sh`) and `INDEX.md` say: switch on **"agenteyes"** (the service); the app needs nothing | **The app needs no grant, and holds none.** Inside the bundle's own process, `CGPreflightScreenCaptureAccess() = False`. Inside the installed service, the same probe gives `True` (positive control). With no grant, the app showed a live recording made by the service (criterion 7). It never prompted, because it never asks. | Repeat evidence E1 and E2 with `docs/cencon/proof/issue-116/mac-pass3-capture-tools/oneshot.py` and the hook. **Never run a capture from the bundle's process**: an ungranted capture prompts or hangs, and would put a TCC entry on this machine. | MET |
| 7 | Live recorder state inside the bundle | nothing new | `dev-window-recording.png`: STOP, `REC 00:04`, controls greyed, taken between `/status recording 5.68` and `6.3` | `POST /record/start {"mode":"video","screen":1,"source":"none"}`, photograph the window by id, then `POST /record/stop` with `-d '{}'` | MET |
| 8 | Size and content | - | **198 MB** on disk. libvlc 80.5 MB (39%), .NET 8 runtime 77.4 MB (37%), Avalonia/Skia/HarfBuzz 27.4 MB (13%), Whisper natives for **8 platforms** 17.5 MB (8.5%, of which only `macos-arm64` is usable here), AgentEyes' own code **0.5 MB**. The engine's apphost (`agenteyes`) is also inside; the app never starts it. | `python3 -I docs/cencon/proof/issue-118/tools/sizes.py ~/Applications/AgentEyes.app` | MET |
| 9 | Service not regressed | - | macOS gate below. A recording through the Control API after the install: `ffprobe` h264 1920x1080, **261 frames**, 30/1, **8.70 s**. | the same `ffprobe` command (evidence F) on a new recording | MET |
| 10 | Clean-machine statement | - | **NOT proved on a clean Mac.** No clean account (creating one needs an administrator) and no macOS VM. What I can say is narrower: to the .NET host, the `env -i` environment has no .NET at all (the framework-dependent control fails with "You must install .NET"), and the bundle ran in it. And nothing in the bundle loads a library from outside it: across 329 Mach-O files the only non-system loads are `@rpath` names; the same instrument finds 18 Homebrew loads in ffmpeg. System libraries (`/usr/lib`, `/System`) are assumed present on any macOS 11+. | A Mac with no `~/.dotnet` and no Homebrew - or a fresh user account created by the owner: copy the bundle there and open it | STATED: not a clean machine |

## The signing criteria - DELIBERATELY UNCHECKED

Every row below is blocked on the same thing: the Apple Developer Program enrolment (a Team ID, a
Developer ID Application certificate on this machine, notarytool credentials in `cc-secrets`).

| Criterion (lead, 16 Nov) | Status | What is already known for it |
|---|---|---|
| `codesign -dv --verbose=4` shows a Developer ID Application identity and the hardened runtime | UNCHECKED | Today: `Signature=adhoc`, `TeamIdentifier=not set`, `flags=0x2(adhoc)` |
| `notarytool submit` -> `Accepted`; `stapler validate` passes | UNCHECKED | The distribution format must keep extended attributes (decision 4) |
| `spctl -a -vv -t exec` -> `accepted`, `source=Notarized Developer ID` | UNCHECKED | Today: `rejected` |
| Quarantine test: download or set `com.apple.quarantine`, double-click, no Gatekeeper refusal | UNCHECKED | Not tried on the ad-hoc bundle: Gatekeeper's dialog cannot be answered on a locked screen. Whether an ad-hoc bundle can still be opened by right-click and Open on macOS 26 (the issue's second assumption) is **therefore unknown**. |
| Screen Recording after a signed bundle replaces an unsigned one | UNCHECKED, and mostly moot | The app holds no grant. The first Developer ID build is a new TCC identity once (decision 1). |

**Measured for the follow-up, so it does not start from a guess: the hardened runtime cannot be
rehearsed with an ad-hoc signature.** Re-signed ad-hoc with `--options runtime`, the bundle exits at
once, with or without `com.apple.security.cs.allow-jit`. Library validation refuses the bundle's own
`libhostfxr.dylib` ("mapping process and mapped file (non-platform) have different Team IDs"). So
whether `allow-jit` is needed, and what the hardened runtime does to startup hooks, can be measured only
on the first Developer ID build. **Do not** get past it with `disable-library-validation`.
Entitlements by reasoning, not yet measured:
- `allow-jit`: likely needed, because CoreCLR uses a JIT.
- Microphone and camera: none for the app, because it opens neither device.
- Network: none for the loopback Control API; network entitlements apply only under the App Sandbox,
  which this app does not use.

## The cases I was tempted to assume away

1. **"The grant will carry over to the app."** It does not, because the bundle is a separate TCC
   subject. It also does not need to: measured, the app holds no grant and needs none.
2. **"The bundle will be called AgentEyes because Info.plist says so."** It was not: Launch Services
   called it "Avalonia Application" until `Application.Name` was set. The same may be true of the menu
   bar's bold title, which I could not photograph. A test pins the fix.
3. **"The icon will just work."** The icon macOS resolves for the bundle is right. The dock was not
   photographed, so I do not claim that the dock shows it.
4. **"An ad-hoc signature is a rehearsal for Developer ID."** It is not. The hardened runtime refuses
   ad-hoc libraries outright, so nothing about entitlements or hooks can be learned before the
   certificate exists.
5. **"The signature travels with the bundle."** Only through a copy that keeps extended attributes.
   Zip followed by unzip breaks it.
6. **"A rebuild is the same application."** For the bundle, only a byte-identical rebuild is; every
   release is a new identity while it is ad-hoc. For the service, a rebuild **is** the same
   application, because its identity is the bare apphost, which is byte-identical across rebuilds
   (#114, and re-measured today). I told the lead this, because the lead's draft issue assumed the
   service loses its grant on every update. It does not, unless the .NET SDK's apphost changes.
7. **"Self-contained means it needs nothing outside it."** I checked the load commands, not the name.
8. **"The bundle contains only the app."** It also carries the engine's apphost and Whisper natives for
   seven platforms it can never run on (about 17 MB). Trimming them is not this issue's work.
9. **"`open` is a Finder double-click."** It uses the same Launch Services service, but it is not the
   criterion's wording, so criterion 3 stays open.

## Screenshots (how they were taken)

The screen was **locked** for the whole run (`CGSSessionScreenIsLocked = true`). I took every window
photograph with the committed tooling in `docs/cencon/proof/issue-116/mac-pass3-capture-tools/`: a
one-shot launchd job runs the **installed** `agenteyes`, which holds the grant, with the startup hook,
and the hook calls `screencapture -x -o -l <window id>`. Window ids come from
`docs/cencon/proof/issue-116/mac-window-probe.swift <pid>`. Each job was bootstrapped, polled with a
hard timeout and booted out. Never run `screencapture` from your own shell: it is refused and it
hangs. The new bundle **cannot** take these photographs itself, because it holds no grant.

- `dev-window-bundle.png` - the installed bundle's window (opened with `open`). An `env -i` launch of
  the bundle's executable produced a byte-identical photograph (`cmp`), so it is committed once.
- `dev-window-recording.png` - the same window during a Control API recording.
- `dev-resolved-icon-bundle.png` / `dev-resolved-icon-bare-apphost.png` - the icon macOS resolves
  (`tools/bundleicon.swift`), and its control.
- `dev-menubar-while-locked.png` - why criterion 4's menu bar could not be observed.

## Gate (macOS, this machine)

There is no .NET 8 SDK here (SDK 10.0.403; .NET 10 runtimes only). `DOTNET_ROLL_FORWARD=LatestMajor`
was exported in the shell for the test hosts only; nothing in the product or the scripts sets it. Full
output: `dev-gate-macos.txt`.

```
1) engine, mac configuration     Build succeeded. 0 Warning(s) 0 Error(s)
2) tests/AgentEyes.Tests.Mac     Passed: 283, Skipped: 1, Total: 284, Failed: 0
3) engine, linux configuration   Build succeeded. 1 Warning(s) (AlwaysOnEngine.cs:901, pre-existing) 0 Error(s)
4) tests/AgentEyes.Tests.Linux   Passed: 324, Skipped: 2, Total: 326, Failed: 0   - ON A MAC: NOT a Linux run
5) tests/AgentEyes.Tests.DesktopApp  Passed: 23, Total: 23, Failed: 0
```

`AgentEyes.sln` cannot be built on a Mac, and that is expected. The Windows gate (`dotnet build` and
`dotnet test AgentEyes.sln`) and the real Linux run are CI's windows and ubuntu jobs on PR #179.
- On a56752a (run 43921228624) all three jobs passed: windows, linux and macos. The real Linux suite
  was `Passed: 325, Skipped: 1, Total: 326`, and `Passed: 326` with the ffmpeg-backed tests. The macOS
  job includes the new "Build AgentEyes.app" step.
- QA: quote the run on the head you verify, not this one.

**The new tests fail closed** (`dev-mutations.txt`). Each was run against a known-bad input and fired:
- M1: the bundle identifier changed -> `InfoPlist_BundleIdentifier_IsThePermanentOne` FAIL.
- M2: `Name` removed from App.axaml -> `AppName_LoadedFromXaml_EqualsTheBundleName` FAIL.
- M3: an upscaled 512 slot added to the icon -> `Icns_HasEverySizeUpTo256_AndNoUpscaledSlot` FAIL.
  The first M3 attempt did not mutate anything, because a shell glob error stopped it. That is
  recorded, and the re-run fired.
- M4: the app's Version made to differ from the engine's -> the version test FAIL.
- Restored: 8 of 8 pass.

What the tests cannot see, stated:
- They read the committed **inputs** (Info.plist, .icns, App.axaml, the versions). They do not build a
  bundle. CI's macOS step builds one and verifies its signature, but does not launch it.

## Self-review (code review of the diff), and what I did with each finding

| Finding | Done |
|---|---|
| `--no-start` stopped a running app silently | Fixed. It now prints a WARNING with the exact `open` command. Measured, evidence L1. |
| A race between `pgrep` and `kill` could abort the installer with no message | Fixed. `kill ... || true`; the wait loop decides, and fails with a reason. |
| The "stop only this path" check used an unescaped regex built from `$HOME` | Fixed. Each candidate's command line is compared with the path as a **literal** string. Both arms were measured with another copy running (L2). |
| codesign's error was discarded | Fixed. The error text and the likely cause are printed. |
| Does `Name="AgentEyes"` change the Linux `WM_CLASS`? | No. IL inventory (L3): X11 never reads `Application.Name`; `WM_CLASS` comes from `X11PlatformOptions.WmClass`, which `Program.cs` sets explicitly. Limit stated in L3. **QA on Linux:** `xprop WM_CLASS` on the window should still say `AgentEyesDesktop`. |
| The version is in two csproj files | Kept. `MacBundleTests` pins them equal, and it runs in the Windows gate and on Linux CI. A shared `Directory.Build.props` would touch every project, which is outside this issue. |
| `MacBundleTests` is not in `tests/AgentEyes.Tests.Mac` | Kept: that project references the engine only. **Run `tests/AgentEyes.Tests.DesktopApp` on the Mac too** (gate step 5); it is not in #114's documented loop yet. |
| CI re-verifies what `build-app.sh` checks | Kept on purpose. CI checks the bundle at its final path and adds what the script does not check: the identifier, `libhostfxr` and `libvlc`. |

## What changed

- `src/AgentEyes.DesktopApp/macOS/Info.plist` (new), `App.axaml` (`Name`), `AgentEyes.DesktopApp.csproj`
  (`<Version>1.11.11</Version>`)
- `assets/AgentEyes.icns` (new), `scripts/mac/make-icns.sh` (new)
- `scripts/mac/build-app.sh` (new), `scripts/mac/install-app.sh` (new), `scripts/mac/install.sh` (calls
  install-app.sh; header and closing text)
- `tests/Shared/MacBundleTests.cs` (new; in both test projects).
  `tests/Shared/DesktopAppQuitTests.cs`: the headless session is now a single **collection** fixture,
  because Avalonia can be set up once per process; plus a `Dispatch` helper. The guard tests are
  otherwise unchanged.
- `.github/workflows/ci.yml`: macOS step "Build AgentEyes.app (macOS bundle)"
- `docs/cencon/INDEX.md`: the macOS install, as a component

**CenCon impact:** a new installed component (in `INDEX.md`). The privacy posture is unchanged and is
now stated: the service is the only process that captures. Visible and controllable are unchanged,
because the tray/menu-bar item and the window are the same app.

## Hazards for QA on devthrottle-mac-mini (carried from the lead)

- Do not run the full `install.sh` here, and do not bootout or re-register `com.agenteyes.recorder`:
  it holds the only Screen Recording grant. Do not change any TCC setting. Leave the owner's service
  (`agenteyes serve`) alone.
- `~/Movies/AgentEyes` holds evidence; never delete a recording. **I added one:**
  `2026-11-16_123404_video` (criterion 9).
- #161: an isolated HOME does not redirect the library on macOS. Any real service you start scans the
  owner's real library.
- I left `~/Applications/AgentEyes.app` **installed and not running**: I quit my own instances, so the
  machine is as it was apart from the new bundle. QA starts it with `open ~/Applications/AgentEyes.app`.
- The REST, UIA and PrintWindow guidance in the method is for Windows. On this Mac the routes are the
  Control API and window-id photographs by the granted binary.
- No smoke is needed beyond the above: the engine code is unchanged.

I believe this is finished up to the stop point: everything short of notarisation that can be done
on a locked machine is done. The rest is the unchecked table above, criterion 3 (Finder), the
photographs for criterion 4 (dock and menu bar), and the full-installer run of criterion 5.
