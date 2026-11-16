# #193 Windows seat - v1.12.0 installed from the published release

Date: 16 November 2026. Machine: the Windows laptop used as the proving ground (Windows 11 Home
10.0.26200, Smart App Control OFF, the microphone records silence at about -96 dBFS - a known
hardware fault, not a product defect). Paths are written as `%LOCALAPPDATA%` / `%USERPROFILE%`.

**Verdict: the release installs and works.** Downloaded from the release page, all five assets
match `release-manifest.json`. A corrupted asset is refused before anything is touched. The wizard
upgraded the machine from a main build to 1.12.0 at commit `02a4622`, and a recording made through
the installed app decodes cleanly to its end. The notes have **two wrong statements and two
omissions** a user will run into (section 6). Separately, the setup CLI's `--root` testing option
stopped the owner's real running app (section 7). That is not reachable from the wizard or the
README command, but it did leave this machine with no recorder running for a few minutes.

---

## 1. What was on the machine before

| Item | Before |
|------|--------|
| `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe` | ProductVersion `1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb`: a build of main `b0e6ae8`, installed by an earlier seat. It still reports **1.11.11** because the version bump is in `02a4622`, after `b0e6ae8` |
| `agenteyes.exe`, `agenteyes-setup.exe` | same, `1.11.11+b0e6ae8...` |
| Running | `AgentEyesApp.exe --tray`, pid 19972. `/health` returned `{"ok":true,"app":"AgentEyes"}`. `/status` State `idle` |
| Desktop shortcut | present (`AgentEyes.lnk`) |

## 2. Download - from the release, not a build

Release `v1.12.0`, published 2026-11-16T23:17:01Z, target `main`, tag commit
`02a4622774f50382e35b3599c90c65bc2abb4608`. Each asset was fetched with `curl -L` from:

    https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.12.0/AgentEyes-Setup-win-x64.exe
    https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.12.0/agenteyes-setup-cli-win-x64.exe
    https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.12.0/agenteyes-win-x64.exe
    https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.12.0/AgentEyesApp-win-x64.exe
    https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.12.0/agenteyes-ffmpeg-win-x64.zip
    https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.12.0/release-manifest.json

All six returned HTTP 200. The URL in the notes' section 2,
`.../releases/latest/download/AgentEyes-Setup-win-x64.exe`, redirects (302) to
`.../releases/download/v1.12.0/AgentEyes-Setup-win-x64.exe`. **The notes' download link is right.**

Also the wizard itself fetches from the release, not a local copy. Its log says
`FetchLatestAsync: channel=https://api.github.com/repos/thefrederiksen/agenteyes-app/releases/latest`
-> `latestVersion=1.12.0`, and then it downloads and verifies all four components itself.

## 3. Hashes - they match, and the check can fail

### 3a. The downloaded bytes match the manifest

| Asset | Size (manifest = downloaded) | manifest sha256 | `sha256sum` of the download |
|-------|------|-----------------|-----------------------------|
| AgentEyes-Setup-win-x64.exe | 71823903 | `ef732b74...b71c1` | `ef732b74dd2daa2e19db440751540c0f26e80b18efa938e6ba6e44c59c4b71c1` MATCH |
| agenteyes-setup-cli-win-x64.exe | 35373872 | `b6c7d9fa...96d9c` | `b6c7d9fab22b7a380211c8e9f463549bd60c7f3ef98ff35fd27ad695c3596d9c` MATCH |
| agenteyes-win-x64.exe | 80938685 | `6f001845...063a7` | `6f0018456603dce76b020b6eec8ffd306177bfd26bd6fa62003005b5ffa063a7` MATCH |
| AgentEyesApp-win-x64.exe | 83803279 | `95b744de...17a9b` | `95b744decd45f69834c854d237817af849db12ffc31eaa13aa1a4699e8117a9b` MATCH |
| agenteyes-ffmpeg-win-x64.zip | 79878230 | `48d22238...727c5` | `48d22238a853e212eb87c9a1139207824867e9e0aabd4ab5d80c2d834f0727c5` MATCH |

The manifest reads `"version": "1.12.0"`, `"tag": "v1.12.0"`. The four exes carry ProductVersion
`1.12.0+02a4622774f50382e35b3599c90c65bc2abb4608`, which is the tag commit.

### 3b. Proof the check fails on bad bytes

The check lives in `UpdateRunner.StageAsync` (`tools/AgentEyes.Setup.Engine/UpdateRunner.cs:177`).
It runs for every component before anything is stopped or replaced. I made a copy of the
downloaded release folder and flipped one byte of `agenteyes-win-x64.exe` at offset 40,000,000
(XOR 0xFF). The size was unchanged, and the sha256 became
`a59629255325d34a2e3c7f89d1c5d1ba28a83c176b34129010eb15755807d261`. Then I ran the downloaded
setup CLI against both folders, each into its own empty throwaway root:

    agenteyes-setup install --release-dir <untouched copy> --root <empty root A> --no-finalize
      Install complete: app/cli/setup-cli/ffmpeg Installed   installed=4 updated=0 failed=0   exit=0

    agenteyes-setup install --release-dir <one byte flipped> --root <empty root B> --no-finalize
      ERROR: cli could not be downloaded and verified (SHA-256 mismatch; download rejected).
      Nothing was replaced and the running AgentEyes was not stopped - the installed version
      is unchanged. Check the connection (or the release) and run the update again.
      exit=1

After the refused run, root B held only `config/setup/last-update-attempt.json` and
`logs/setup-cli.log`, and **no binary**. The refusal happened at the `cli` component, after `app`
had already verified, so the all-or-nothing staging held. The same bytes with no flip installed
cleanly, so the refusal comes from the byte flip and from nothing else.

What this does not cover: the corruption came from a local release folder (`--release-dir`), not
over the network. The download path differs only in where the file comes from. The hash check at
`UpdateRunner.cs:177` is the same line for both.

## 4. Install - the wizard, as a user would

I used **`AgentEyes-Setup-win-x64.exe`, the wizard**, and drove it through UI Automation (no
synthesized keyboard or mouse input). No step needed a human hand, so the CLI was not needed for
the real install.

**SmartScreen.** `curl` does not add a Mark-of-the-Web, so a curl download raises no warning at
all. To see what a browser user sees, I put the same Zone.Identifier a browser writes on a copy
(`ZoneId=3`, HostUrl = the release URL) and launched it two ways:

1. **Launched from Explorer (double-click route): Microsoft Defender SmartScreen.**
   - First screen (`06-smartscreen-windows-protected.png`): **"Windows protected your PC -
     Microsoft Defender SmartScreen prevented an unrecognized app from starting. Running this
     app might put your PC at risk."** The only button is **"Don't run"**. There is a small
     **"More info"** link. The words "Unknown publisher" are **not** on this screen.
   - After clicking **More info** (`07-smartscreen-more-info-run-anyway.png`):
     `App: AgentEyes-Setup-win-x64.exe`, `Publisher: Unknown publisher`, and a **"Run anyway"**
     button appears beside "Don't run".
   - After clicking **Run anyway**, the wizard opened. No second prompt and no UAC prompt.
   - **What the user has to click: "More info", then "Run anyway". That is two clicks, and the
     first one is a text link on a screen whose only button says "Don't run".**
2. **Launched by a program that calls ShellExecute (here PowerShell `Start-Process`): the
   Attachment Manager prompt** (`01-open-file-security-warning.png`): "Open File - Security
   Warning. The publisher could not be verified. Are you sure you want to run this software?
   Publisher: Unknown Publisher", with **Run** / **Cancel**. One click on "Run". No SmartScreen
   screen appeared on this route.

The README one-line PowerShell install (`iwr ... ; & $e install`) raises **no** warning at all,
because `Invoke-WebRequest` writes no Mark-of-the-Web.

Not measured: **Smart App Control.** It is OFF on this laptop. On a Windows 11 machine where it is
ON, an unsigned exe is expected to be blocked with no "Run anyway" button. I did not run that
case, and the notes do not mention it (section 6, item 3).

**The wizard, screen by screen.** Its title bar says "AgentEyes Update" because a version was
already installed.

| Step | What it showed | Screenshot |
|------|----------------|-----------|
| 1 Welcome | "Update AgentEyes - Upgrade available: v1.11.11 -> v1.12.0 - Currently installed: v1.11.11" | `02-wizard-welcome-upgrade-available.png` |
| 2 Options | Three boxes, all ticked by default: "Start AgentEyes when I sign in", "Add the agenteyes command line tool to PATH", "Also create a desktop shortcut". Accepted as is | `03-wizard-options-defaults.png` |
| 3 Update | Took 41 s from Next to "Done - 4 installed, 0 skipped" (18:26:05 -> 18:26:46 in the setup log). It downloaded and verified all four, then swapped each, keeping a `.old` backup | - |
| 4 Complete | "Update Complete - AgentEyes has been updated successfully. Components installed: 4, Skipped: 0", a **Launch AgentEyes** button and **Close** | `04-wizard-update-complete.png` |

No admin rights were asked for at any point. **The notes' "per-user and needs no admin rights" is
right.**

The owner's app was not running when the wizard started, for the reason in section 7, so the
wizard logged "AgentEyes was not running - it was not started". **This run therefore did not
measure the wizard stopping and restarting an app that was running.** The setup CLI did that
earlier (pid 19972 stopped through its quit request, exit clean in 1 s, relaunched), but that was
in the `--root` case described in section 7.

## 5. What landed

| Check | Expected | Actual | |
|-------|----------|--------|---|
| `AgentEyesApp.exe` ProductVersion | `1.12.0+02a4622...` | `1.12.0+02a4622774f50382e35b3599c90c65bc2abb4608`, sha256 `95b744de...` = manifest | PASS |
| `agenteyes.exe` ProductVersion | same | `1.12.0+02a4622774f50382e35b3599c90c65bc2abb4608`, sha256 `6f001845...` = manifest | PASS |
| `agenteyes-setup.exe` ProductVersion | same | `1.12.0+02a4622774f50382e35b3599c90c65bc2abb4608`, sha256 `b6c7d9fa...` = manifest | PASS |
| `agenteyes-setup status` | 1.12.0 | `app 1.12.0 / cli 1.12.0 / setup-cli 1.12.0 / ffmpeg 9.0.2` | PASS |
| The running app's own update check | up to date | log: `update: already up to date (v1.12.0)` | PASS |
| `.old` backups | previous build kept | `*.exe.old` = `1.11.11+b0e6ae8...` | PASS |
| `GET /health` | answers | `{"ok": true, "app": "AgentEyes"}` | PASS |
| `GET /status` | answers | State `idle`, LastStopFailed false, no capture fault | PASS |
| Autostart | Run key | `HKCU\...\Run\AgentEyes = "%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe" --tray` | PASS |
| Add/Remove Programs | 1.12.0 | `AgentEyes 1.12.0`, uninstall = `agenteyes-setup.exe uninstall` | PASS |
| PATH | added | user PATH contains `%LOCALAPPDATA%\AgentEyes\app` | PASS |
| App window | opens | "Launch AgentEyes" opened the WPF app window, Record page (`05-installed-app-window.png`) | PASS |

`/health` and `agenteyes --version` do not report a version. `--version` is an "unknown command"
and prints usage with exit 1. The embedded ProductVersion, `agenteyes-setup status` and the app
log are the only places the version shows. This is not wrong in the notes, but a user who asks
"which version do I have?" has no command for it except `agenteyes-setup status`.

### The recording (the amended row 6 criterion)

Made through the installed app's Control API:
`POST /record/start {"mode":"video","screen":1,"source":"system","fps":30}`, then about 25 s
later `POST /record/stop`. I waited on `PostProcessing.mux.State`, not on `/status` (#159): it
read `done` on the first poll after the stop returned.

| Measure | Value |
|---------|-------|
| `/record/stop` reply | `DurationSeconds: 26.62`, File `recording.mp4` |
| Container duration (ffprobe) | 26.900 s |
| Full decode: `ffmpeg -v error -xerror -i recording.mp4 -f null -` | **exit 0**, no error output |
| Video frames decoded (`-count_frames`) | 801 (29.8 fps over 26.9 s) |
| **Last video frame pts** | **26.867 s**, one frame (0.033 s) before the container's end |
| Raw capture `recording.original.mp4` | fragmented: `ftyp`, `moov`, 16 x `moof`/`mdat`, `mfra` (the #113 container) |
| Final `recording.mp4` | `ftyp`, `mdat`, `moov`: a whole, indexed file |
| Audio | AAC, 25.23 s, mean -91.0 dB (silence; system loopback, nothing was playing) |

**PASS.** The file decodes to the end and its last frame sits at the expected end.

One observation, not reported as a defect: the audio track is 1.67 s shorter than the video
(25.23 s vs 26.90 s). The raw loopback capture `system.original.wav` is itself 25.23 s, so the
shortfall is in what the system-audio capture delivered while nothing was playing, not in the
mux. This take does not show whether that happens when sound is playing. It is not the #184
shape (that one is mic at 5 fps), and it was not measured further.

## 6. Where the notes do not match what a user sees

1. **WRONG - section 1 table, Windows row: "SmartScreen will show 'Unknown publisher'".**
   The SmartScreen screen a user sees first says **"Windows protected your PC ... prevented an
   unrecognized app from starting"**. Its only button is "Don't run". "Unknown publisher"
   appears only after clicking "More info". A user reading the notes looks for the words
   "Unknown publisher" and an option to run it, and finds neither on the first screen.
   Suggested text: *"SmartScreen will say 'Windows protected your PC'. Click **More info**, then
   **Run anyway**. The publisher shows as 'Unknown publisher' because the exes are unsigned."*

2. **INCOMPLETE - section 2, Windows: "you have to choose to run it anyway".** It is true, but
   it leaves out the one step that is hard to find. "Run anyway" is hidden until you click the
   "More info" link. Name both clicks (see item 1). If you start the exe some other way (from a
   program, or from a download manager that adds the Mark-of-the-Web but not through Explorer),
   you get the older "Open File - Security Warning" box with "Run" instead. On this machine that
   box appeared when PowerShell `Start-Process` launched the file.

3. **OMISSION - Smart App Control.** The notes say nothing about it. On a Windows 11 machine with
   Smart App Control ON, unsigned exes are expected to be blocked outright with no "Run anyway".
   That is **not measured** here because Smart App Control is OFF on this laptop. It should be
   either measured on a machine where it is on, or listed as an unmeasured risk next to the
   signing note.

4. **WRONG ON THE PAGE ITSELF - section 1, "The GitHub release page will not show these notes on
   its own ... edit the release after the workflow finishes: `gh release edit v1.12.0 ...`".**
   The release page now carries these notes (the release body begins "# AgentEyes v1.12.0 -
   release notes"), so the edit has been done. Anyone reading the release page is told that
   the page they are reading will not show these notes, and is handed a maintainer command.
   This paragraph is for the person cutting the release, not the reader. Remove it from the
   published body. It could move to the release checklist.

Checked against the running product and **found right**:

- The `latest/download` link -> v1.12.0.
- The six assets as listed.
- Per-user, no admin.
- The README one-liner updates an existing install: it printed `Nothing to do - all components
  up to date.`, exit 0.
- "Windows still ships the existing Windows app": the installed `AgentEyesApp.exe` is the WPF app.
- #187's Windows paragraph: the shipped app's labels really are "Start when Windows starts"
  (`MainWindow.xaml:747`) and "Run at login" (`SettingsDialog.cs:55`, `TrayHost.cs:241`), and
  autostart writes the HKCU Run key.
- #159's advice to wait on `PostProcessing.mux.State`: that is what I did, and it worked.

The rest of the defect list (#157, #149, #184, #166, #188) concerns cases this seat did not
provoke: no load, no camera, 30 fps only, no killed recorder. I have no observation that confirms
or contradicts those entries.

## 7. A setup-CLI hazard found on the way (not in the notes, not user-facing)

The control half of the hash test ran
`agenteyes-setup install --release-dir <dir> --root <empty scratch root> --no-finalize`.
`--root` is documented in `--help` as "Override the per-user root ... (testing)". That run:

1. found the owner's **real** running app (pid 19972, `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray`),
2. **stopped it** with a quit request,
3. installed into the scratch root, and
4. **launched the scratch-root copy** as the "restarted" app. It printed a note that it had
   stopped a process outside the root.

The scratch-root copy then **crashed at start-up**. The Application event log (`.NET Runtime`
1026, 18:21:14) shows `System.DllNotFoundException` in
`MS.Win32.HwndSubclass.HookWindowProc` (WPF). I did not establish the cause. One candidate is the
user-level `DOTNET_BUNDLE_EXTRACT_BASE_DIR` pointing at the real root's `bundle` directory.

Result: from 18:21 until the wizard install at 18:26-18:28, **no AgentEyes was running on this
machine**. No recording was in progress, and none was lost. The wizard, the README command and
the in-app updater never pass `--root`, so a user cannot reach this. But a QA or developer seat
that uses `--root` to test "without touching the install" will stop the owner's recorder. A
testing option should not reach outside its root. **Recommend an issue:** with `--root`, the
restart step should act only on a process running from inside that root.

## 8. State of the machine after this seat

**Better than before, with nothing lost:**

- AgentEyes 1.12.0 (`02a4622`) is installed and running (pid 10060, window mode, started from
  the wizard's "Launch AgentEyes").
- Autostart, PATH, Start Menu shortcut, desktop shortcut and the Add/Remove Programs entry are
  all set to 1.12.0.
- The previous build is kept as `*.exe.old`.
- No recording was deleted. One new 27-second test recording was added:
  `%USERPROFILE%\Videos\AgentEyes\2026-11-16_182949_video`.
- No ffmpeg was killed.

After the next sign-in the app will start in the tray, from the Run key.

No code changed in this seat, so `dotnet build` / `dotnet test` were not run. What was under
test is the published binaries.
