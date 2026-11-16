# Issue #118 - QA report, macOS (independent seat)

PR #179, branch `issue-118-macos-app-bundle`, **head tested `9f668b54555cfbf35569c620b58e287eee37e220`**.
Machine: devthrottle-mac-mini, macOS 26.5.2 arm64. The screen was **locked** for the whole pass
(`CGSSessionScreenIsLocked = true`, frontmost app `loginwindow`).
There is **no .NET 8 SDK** on this machine. SDK 10.0.403 builds the net8.0 projects, and
`DOTNET_ROLL_FORWARD=LatestMajor` was set only for the test hosts and the IL-scan tool. Every test
count below was measured under those conditions.

Every number here is in `qa-evidence-macos.txt`, next to the command that produced it. I took the
developer's handoff as a list of claims and measured each one again myself.

## Verdict

**PASS up to the stop point. No defect found.** Label: `flow:ready-gate`.

What is proven is an **ad-hoc signed, self-contained `AgentEyes.app`**. It runs here with no .NET in
its environment, holds no Screen Recording grant and needs none, and installs beside the service
without touching it. The bundle is **built, not deliverable**: `spctl` rejects it, and the five
notarisation criteria, the Finder double-click (3), the dock and menu-bar photograph (4), the full
installer run (5) and the clean-machine run (10) are **open**. Each of these is recorded as open,
with its reason, and none is marked passed (item 6).

## Build identity

- I built the bundle myself from the head (`scripts/mac/build-app.sh`). I then installed it with
  `scripts/mac/install-app.sh` (item 4).
- `AgentEyesDesktop.dll` in that bundle reports `1.11.11+9f668b54555cfbf35569c620b58e287eee37e220`,
  and `git rev-parse HEAD` gives the same commit.
- **The bundle that was installed when I arrived was not the head.** It reported
  `1.11.11+a56752a...`, the developer's earlier commit. My install replaced it, so every runtime
  result below is from 9f668b5.
- The service I left untouched reports `1.11.11+f9ef230...`.

## The six items

### (1) Self-contained, with its control - PASS

| Arm | Command | Result |
|---|---|---|
| Bundle | `env -i HOME=$HOME USER=$USER PATH=/usr/bin:/bin:/usr/sbin:/sbin COREHOST_TRACE=1 ... ~/Applications/AgentEyes.app/Contents/MacOS/AgentEyesDesktop` | `13:07:54.157 [pid 34786] [MainWindow] Opened`, then `PollAsync: service answering`. Host trace: 1695 lines, **0** naming a global .NET location, 663 naming the bundle. `Executing as a self-contained app`, hostfxr 8.0.33 from the bundle. `ps -E` on the process shows HOME, USER, PATH and the two COREHOST variables, and **0** `DOTNET_*` entries. |
| Control | the same command on a **framework-dependent** build of the same head (`dotnet build ... -o <scratch>/fdd`, runtimeconfig `Microsoft.NETCore.App 8.0.0`) | **exit 131**, `You must install .NET to run this application.`, `.NET location: Not found`. The trace has 6 global-location lines. |

The window is in `qa-macos-window-envi.png`, photographed by window id (780, owned by pid 34786). I
read the image: it is the full Record page, not a blank render. It is byte-identical (`cmp`) to the
developer's `dev-window-bundle.png`, which was taken from a different build and process. So the
window renders the same at both commits.

**Expected:** the window opens with no .NET in the environment, and the control fails.
**Actual:** both happened.

What this proves is narrower than a clean machine. Under `env -i`, the .NET host finds no .NET at
all, and the bundle runs anyway. Criterion 10 is still open.

### (2) The grant facts - PASS, all five

| Fact | Measurement | Result |
|---|---|---|
| The bundle holds no grant | Launchd job `com.agenteyes.qa118.bundlepf` runs the bundle's own executable with the hook. `QA_CAPTURES` was unset, so no capture was attempted. | `CGPreflightScreenCaptureAccess() = False` |
| The service holds the grant (positive control) | Launchd jobs `shot1` and `shotrec` run the installed `agenteyes` with the hook. | `= True`, both times |
| The app displays a live service recording | `POST /record/start {"mode":"video","screen":1,"source":"none"}` at 13:08:45, then the window photographed by id, then `POST /record/stop -d '{}'` | `qa-macos-window-recording.png` shows STOP and `REC 00:05`, with the controls greyed. `/status` read 5.73 s just before the photograph and 6.36 s just after. The window belongs to pid 34786, the `env -i` launch, which holds no grant. ffprobe: h264, 1920x1080, 30/1, **261 frames, 8.70 s**. |
| The SERVICE's apphost cdhash does **not** change across a rebuild | A fresh self-contained publish of the engine from 9f668b5, compared with the installed service built from f9ef230 | The apphost is **cmp-identical**, and both have `designated => cdhash H"0b2bc9d9...7ffb"`. `agenteyes.dll` differs, as expected for a different commit. |
| The ad-hoc BUNDLE's cdhash **does** change per release | b1 and b2 are two builds of 9f668b5. b3 is 9f668b5 with only `<Version>` changed to 1.11.12 (reverted afterwards, tree clean). | b1 = b2 = installed = `9fdc7938...`. b3 = `353ed2d8...`. |

**A refinement for #176:** the bundle's identity changes with **every commit**, not only with every
version bump.
- The developer's a56752a bundle had `b2b257b5...` and the 9f668b5 bundle has `9fdc7938...`, with no
  change to `<Version>` between them.
- The cause is `AgentEyesDesktop.dll`: it embeds the commit SHA (`1.11.11+<sha>`), and the bundle
  seal covers that file.
- So while the bundle is ad-hoc, any rebuild from a new commit is a new application to TCC. Only a
  byte-identical rebuild keeps its identity.

This is consistent with the developer's claim and stronger than it. It does not matter today,
because the app holds no grant.

**Limit:** I measured code identities. I did not measure TCC's own decision; the TCC database
cannot be read here. "The service keeps its grant across an update" is an inference from the
unchanged designated requirement. A rebuild with an identical apphost has not been observed keeping
its grant.

### (3) The "Avalonia Application" defect and its Linux risk - PASS on macOS; Linux OWED

| Arm | `lsappinfo info -only name` | Window Server owner |
|---|---|---|
| Mutant: scratch bundle from the head with `Name="AgentEyes"` removed from App.axaml (reverted, tree clean, unregistered from Launch Services afterwards) | `"Avalonia Application"` | `AgentEyes` |
| Installed 9f668b5 | `"AgentEyes"` | `AgentEyes` |

- **The defect is real, and the fix is what fixes it.** Removing one attribute brings the defect
  back.
- **A correction to one piece of the developer's evidence.** The Window Server owner name is
  "AgentEyes" in **both** arms, so it is not evidence for the fix. `LSDisplayName` is the
  discriminating reading.
- **Is the IL-scan claim consistent with the code? Yes.**
  - I rebuilt the developer's `tools/ilscan` and ran it over the Avalonia assemblies inside my own
    bundle. It reproduces the developer's result exactly. `Application::get_Name` is called from
    Avalonia.Native (two methods), FreeDesktop's DBus IME and one Avalonia.Controls lambda, and never
    from Avalonia.X11 or Avalonia.Win32. The `X11PlatformOptions::get_WmClass` positive control
    finds 4 calls in X11Window.
  - In the app's own code, `WM_CLASS` comes from an explicit option, `Program.cs:23`
    (`WmClass = "AgentEyesDesktop"`) and `Program.cs:74` (`new X11PlatformOptions { WmClass = WmClass }`).
    It matches `scripts/linux/install.sh:132`.
  - The scan's own limit applies: it finds direct calls only, so a read through the property system
    or through reflection would not show up.
- **I cannot test Linux from here.** The Linux suite run on this Mac (324 + 2 skipped of 326) is not
  a Linux run. **Owed, for routing:** on Linux, `xprop WM_CLASS` on the app window must still say
  `AgentEyesDesktop`, and the dock must still match the `.desktop` entry.

### (4) The install path - PASS for the app-only path; the full install.sh was deliberately not run

**`scripts/mac/install-app.sh` run over a running install** (the developer's a56752a app, pid 32759,
opened first so that the stop-by-path step would be exercised):
- `[OK] stopped the running app (pid 32759)`, `[OK] installed`, `[OK] registered with Launch
  Services`, `[OK] AgentEyes opened`, exit 0.
- After the run: the new app was running as pid 34500, `LSDisplayName="AgentEyes"`, the installed
  bundle passed `codesign --verify --deep --strict`, and it reported 9f668b5.
- **The service before and after:** pid 50642, launchd `state = running`, and the sha256 of both
  `agenteyes` and `agenteyes.dll` (`348b93b8...` and `1b9092d0...`) were unchanged.
  `/health` returned `{"ok":true,...}` both times.

**The service half of `install.sh` is unchanged, by diff** (`git diff origin/main...HEAD -- scripts/mac/install.sh`):
- 3 hunks.
  - Lines 3-16: header **comments** only.
  - A new block, inserted **after** `ok "agent installed but not started (--no-start)"` and its
    `fi`. It calls `install-app.sh`, with or without `--no-start`, and fails with a retry hint.
  - The closing **heredoc text** (the permission paragraph).
- No executable line of the service half was removed or changed. The block runs only after the
  service has been installed and started.

**The full `install.sh` was deliberately NOT run.** It boots out and re-registers
`com.agenteyes.recorder`, which holds this machine's only Screen Recording grant, and that grant
cannot be restored while the screen is locked. So criterion 5's full-installer transcript is still
open. CI's macOS job runs `build-app.sh` from a clean checkout (run 43923868208: the step "Build
AgentEyes.app (macOS bundle)" succeeded, `Identifier=com.agenteyes.app`, the bundle verifies). CI
does not run either installer.

### (5) The tests are guards - PASS

**Gate on 9f668b5 (this Mac, no .NET 8 SDK):**
- Engine, mac configuration: Build succeeded, 0 warnings, 0 errors.
- `tests/AgentEyes.Tests.Mac`: **Passed 283, Skipped 1, Total 284**.
- Engine, linux configuration: Build succeeded, 1 warning, 0 errors.
- `tests/AgentEyes.Tests.Linux`, **on a Mac, so NOT a Linux run**: Passed 324, Skipped 2, Total 326.
- `tests/AgentEyes.Tests.DesktopApp`: **Passed 23, Total 23**.

**CI on 9f668b5 (run 43923868208, pull_request):**
- All three jobs succeeded.
- Linux: 325 + 1 skipped of 326, and 326/326 with the ffmpeg-backed tests.
- Windows: `AgentEyes.Tests` 2375 + 15 skipped of 2390 (2394/2394 with the ffmpeg-backed tests),
  and DesktopApp 23/23.
- macOS: 283 + 1 skipped of 284.

**My own mutations.** Each one is different from the developer's, and each was restored with
`git checkout` with a clean tree confirmed afterwards:

| # | Mutation | Named test, and its message | Result |
|---|---|---|---|
| Q0 | none | - | 8/8 pass |
| Q1 | `App.axaml` `Name="AgentEyesDesktop"`: present, but wrong | `AppName_LoadedFromXaml_EqualsTheBundleName`: Expected "AgentEyes", Actual "AgentEyesDesktop" | **FAIL** 1/8 |
| Q2 | `CFBundleIdentifier` changed only in case, to `com.agenteyes.App` | `InfoPlist_BundleIdentifier_IsThePermanentOne`: Expected "com.agenteyes.app", Actual "com.agenteyes.App" | **FAIL** 1/8 |
| Q3 | `CFBundleExecutable` set to `AgentEyes`, a bundle that would not launch | `InfoPlist_Executable_IsTheAppsAssemblyName`: Expected "AgentEyesDesktop", Actual "AgentEyes" | **FAIL** 1/8 |
| Q4 | `NSCameraUsageDescription` key removed | `InfoPlist_UsageDescriptions_ArePresentAndSayWhatHappens`: "Info.plist has 0 'NSCameraUsageDescription' keys, expected exactly one" | **FAIL** 1/8 |
| S1 | **Specificity check.** `CFBundleIdentifier` moved to the end of the dict, and `LSApplicationCategoryType` changed to `video`. Both are harmless changes. | - | **8/8 pass**, so the tests are not fragile |

Each mutation failed exactly one test, the one named for it.

**What the tests cannot see**, which the developer also states:
- They read the committed inputs. They never build, sign or launch a bundle.
- So a `build-app.sh` that stopped copying the `.icns` into the bundle, or that substituted the
  version wrongly, would pass all eight. CI's bundle step does not check the icon or the version
  either.

### (6) What is unfinished is recorded honestly - PASS

| Open item | In the handoff | On the issue (developer comment 6924710073) | Marked passed anywhere? |
|---|---|---|---|
| The 5 notarisation criteria | Table "DELIBERATELY UNCHECKED", one row per criterion, each with its reason (no Team ID, no certificate, no notarytool credentials) and what is already known | "The five signing criteria from 16 November are deliberately unchecked", with the enrolment as the reason | No. The handoff and the comment both say `spctl` -> `rejected` |
| 3: Finder double-click | "NOT OBSERVABLE WHILE LOCKED". Explains why `open` is not Finder | "(3) ... no click is possible" | No |
| 4: dock and menu-bar photograph | "Name MET; photo NOT OBSERVABLE WHILE LOCKED" | "(4) the dock and menu-bar photographs" not done | No. Only the name is claimed |
| 5: full installer run | "App half MET; full route STATED-NOT-RUN", with the grant risk as the reason | "(5) The full install.sh: stated-not-run" | No |
| 10: clean Mac | "STATED: not a clean machine", with the narrower evidence | "(10) Not proved on a clean Mac" | No |

I found nothing that was dropped without being recorded. I also checked the issue's own assumptions:
- **Assumption 2** (right-click and Open on an unsigned bundle) is stated as **unknown**, with the
  reason.
- **Assumption 3** (the app needs no grant) is settled, by item 2 above.

**One observation for the lead. It is not a defect in this PR.** The five signing criteria, and
your newer ask that the bundle be verified *after* the release transport, were added in comments
6914905524 and 6924773522. **The issue body was never amended**: its acceptance list still has 10
items. Carry them into the body of the signing follow-up issue, so the gate and the next seat judge
against one list.

## The extended-attributes round trip (measured)

- The signature of a managed `.dll` lives in `com.apple.cs.CodeDirectory`,
  `com.apple.cs.CodeRequirements` and `com.apple.cs.CodeSignature`.
- A Mach-O file such as `libhostfxr.dylib` carries none of these; its signature is embedded in the
  file.

| Transport | `codesign --verify --deep --strict` |
|---|---|
| the built bundle | exit 0 |
| `cp -R` | exit 0 |
| `ditto -c -k --keepParent`, then `ditto -x -k` | **exit 0** |
| the same ditto zip, opened with `unzip` | **exit 1**: `code object is not signed at all  In subcomponent: .../System.Xml.XPath.XDocument.dll` |
| `zip -r -y`, then `unzip` | **exit 1**: `code object is not signed at all  In subcomponent: .../System.Net.WebSockets.Client.dll`. The dll keeps only `com.apple.provenance`. |

So the ditto archive itself is fine; what decides the result is the tool that **unpacks** it. A user
who extracts a ditto zip with a plain `unzip` receives a broken bundle. The release transport has to
be a `.dmg`, a `.pkg`, or a ditto zip that is extracted by Archive Utility or `ditto`, and it has to
be verified again on the Developer ID build. I did not test Archive Utility, because it needs a
click.

## Size (stated, not a defect)

`du -sh` gives **198 MB**: 206.7 MB of file bytes in 657 files. That is consistent, since
206.7e6 bytes is 197 MiB.

| Share | What |
|---|---|
| 39% (80.5 MB) | libvlc |
| 37% (77.4 MB) | the .NET 8.0.33 runtime |
| 13% (27.4 MB) | Avalonia, Skia and HarfBuzz |
| 8.5% (17.5 MB) | Whisper natives for 8 platforms, of which only macos-arm64 is usable |
| 0.2% (**0.5 MB**) | AgentEyes' own assemblies |

I produced these with the developer's `sizes.py`, run on the bundle I installed myself.

## Hazards: what I touched and what I did not

- **Not touched:**
  - `com.agenteyes.recorder`, which was never booted out or re-registered.
  - The installed service binary: its sha256 was unchanged before and after.
  - Any TCC or privacy setting.
  - Any ffmpeg I did not start. No ffmpeg was running when I started.
  - The other clones on this machine.
- **The service, pid 50642, was healthy throughout.**
- **Added to the owner's library, as evidence (do not delete):**
  `~/Movies/AgentEyes/2026-11-16_130845_video` (item 2). Nothing was deleted, and
  `2026-11-16_123404_video` is still present.
- **#161 footprint:**
  - About 80 log lines from my app processes (pids 34500, 34786, 40742, 40769) and about 23 from the
    headless DesktopApp test hosts went into the owner's real `AgentEyes-20261116.log`. The test
    hosts writing to the real log is pre-existing behaviour, but it is another #161 data point.
  - `linux-app.json` was rewritten by the app's `AppSettings.Save` with the same values it had
    loaded.
  - The `agenteyes package` lines at 13:08 (pids 35303, 35399 and 35434) are child processes of the
    owner's service, not mine.
- **Launchd jobs:** `com.agenteyes.qa118.shot1`, `.bundlepf` and `.shotrec`. Each was bootstrapped,
  polled with a 40 s hard timeout, and booted out (`post-bootout print exit=113`). I never ran
  `screencapture` from my own shell.
- **The installed bundle is now my 9f668b5 build. It is installed and not running**, the same state
  the developer left.

## What I did not cover

- **Linux `WM_CLASS`.** No Linux machine is available from here. Owed; see item 3.
- **A Finder double-click, and the dock and menu-bar photographs.** The screen was locked, and
  nothing here can click.
- **The full `install.sh`.** Deliberately not run.
- **`install-app.sh --no-start` over a running app.** I ran only the default path. The developer's
  run L1 covers `--no-start`, and I reviewed the code.
- **A clean Mac or a clean account.**
- **Everything that needs a Developer ID**, including whether startup hooks still load under the
  hardened runtime.
- **Archive Utility** extraction.
- **An Intel build** (`osx-x64`). There is no Intel Mac here.
- **The Windows gate.** I quoted it from CI, not from a run of my own.
