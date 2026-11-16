# Issue #160 stage 1 - parity inventory: the WPF app against the Avalonia app

**Measured on:** `main` at `2d60f77` (the #116 merge), branch `issue-160-parity-inventory`.
**WPF side:** `src/AgentEyes.App` (`AgentEyesApp.exe`), 46 `.cs` files (15,020 lines) and 5 `.xaml` files.
It also uses two WPF windows that live in `src/AgentEyes.Core` (`MonitorHighlight`, `RegionOverlay`).
**Avalonia side:** `src/AgentEyes.DesktopApp` (`AgentEyesDesktop`), 3,655 lines of `.cs` and `.axaml`.

**Method:** source reading only. No app was launched, no test suite was run and no code was changed. The
stage writes one document; a suite run would prove nothing about it. Wherever a row describes behaviour on
Windows, it comes from reading the code, not from watching it, and the row says so. The recording HUD is
excluded from capture (`WDA_EXCLUDEFROMCAPTURE`), so its existence is asserted from its source and its UIA
names (`HudWindow.cs:21`, `:885-910`), not from a picture.

**Status words used in the table:**

- `PRESENT`: the Avalonia app has it. The row says what was compared.
- `PARTIAL`: the Avalonia app has something in its place that does less. The row says exactly what less means.
- `MISSING`: the Avalonia app has nothing for it.

Every gap goes to the owner on #160 to accept or to have closed. This document only lists them.

---

## 0. Read this first: three findings bigger than any row

These are not feature gaps. They decide what stage 2 *is*. The owner and the Implementation Lead need them
before any row below can be closed.

1. **On Windows the WPF app IS the recorder. The Avalonia app is only a remote control for one.**
   `App.xaml.cs:96-145` creates the `RecordingService`, the `RestServer` on 7882, the `RepairService` and the
   `AlwaysOnController` *inside the WPF process*. `AgentEyes.DesktopApp` creates none of them. Every action it
   takes goes over HTTP to `http://127.0.0.1:7882` (`Services/ControlClient.cs:41`, a constant). Its tray Quit
   says "the recorder keeps running" (`TrayController.cs:46`). On Linux and macOS, `agenteyes serve`
   (`src/AgentEyes.Core/Headless/HeadlessHost.cs`) runs as a service and plays the recorder's part. **On
   Windows nothing plays it once the WPF app is deleted.** The #116 Windows QA confirms this from the other
   side: the Avalonia app it launched was driving "the owner's installed app (pid 26652)"
   (`docs/cencon/proof/issue-116/qa-report-windows.md:155`). So deleting `src/AgentEyes.App` without
   something else serving 7882 on Windows leaves an app that can record nothing. The issue's OUT list says
   "the REST Control API and the recorder engine. Neither changes", but which *process* hosts them on Windows
   has to change. That is a decision, not a row.

2. **The two Control API servers are not the same API.** The WPF app serves `RestServer.cs` and the headless
   service serves `HeadlessControlServer.cs`. Their route tables (`RestServer.cs:96-228`,
   `HeadlessControlServer.cs:127-153`) differ in both directions:
   - **WPF only:** `GET /captures`, `GET /presets`, `GET /housekeeping`, `POST /housekeeping/run`,
     `GET /always-on`, `POST /always-on/start`, `POST /always-on/stop`, `GET /always-on/history`,
     `POST /screenshot`, `GET /capture-info`, `POST /capture`, `POST /import`,
     `POST /transcripts/{id}/translate`, `POST /recordings/{id}/subtitle` (singular), and
     `GET /recordings/{id}/shots`.
   - **Headless only:** `GET /account`, `POST /account/refresh`, `POST /recordings/{id}/rename`,
     `DELETE /recordings/{id}`, `POST /recordings/{id}/subtitles` (plural), and
     `POST /recordings/{id}/walkthrough`.

   The Avalonia app calls six of the headless-only routes (`ControlClient.cs:87-117`): rename, delete,
   subtitles, walkthrough and account refresh. **Against the WPF app's server, which is what answers on 7882
   on Windows today, those calls fall through to `Error(ctx, 404, "no such endpoint")`
   (`RestServer.cs:229`).** Picking a host for item 1 therefore also picks an API. Whichever one wins, the
   other's routes and the scripts and agents that call them (`scripts/api-smoke.ps1`, the `agenteyes-control`
   skill) are affected.

3. **The Avalonia app compiles a file out of the WPF folder.**
   `AgentEyes.DesktopApp.csproj:67` has
   `<Compile Include="..\AgentEyes.App\TranscriptPresentation.cs" Link="Shared\TranscriptPresentation.cs" />`.
   Deleting `src/AgentEyes.App` as written in criterion 8 breaks the Avalonia build until that file moves.

Two narrower findings come from the same split, and each also appears as a row:

- **Post-recording plugins run only in the WPF host.** `PostRecordingPlugins.Configure(_cfg)` is called only
  from `App.xaml.cs:110`. `HeadlessHost.cs` never calls it, so a recording made through `agenteyes serve`
  runs no plugins (row B9).
- **Housekeeping runs with defaults in the headless host.** `HeadlessHost.cs` builds `RepairService` without
  setting `HousekeepingSettings`, so it gets `new HousekeepingSettings()` (`RepairService.cs:133-134`), not
  the person's settings from `config.json` as in `App.xaml.cs:129` (row B5).

---

## 1. Completeness: how every WPF item was found, and the counts

The issue asks for the row count to be checked against enumerations, not asserted. Each enumeration below is
a command, its output, and a positive control that shows the instrument can see something it is supposed to
see. Commands ran in Git Bash from the repository root on `2d60f77`. `obj/` and `bin/` are excluded because
they hold generated copies (the `KeyDown` grep without the exclusion returns 9 extra hits from `*.g.cs`).

### E1 - the `.xaml` files

```
$ find src/AgentEyes.App -name "*.xaml" -not -path "*/obj/*" -not -path "*/bin/*"
src/AgentEyes.App/App.xaml          (application resources: brushes and styles, no window)
src/AgentEyes.App/MainWindow.xaml   -> W1
src/AgentEyes.App/PresetEditor.xaml -> W3
src/AgentEyes.App/PreviewWindow.xaml -> W4
src/AgentEyes.App/TestPanel.xaml    -> W2
```

There are 5 files. Four are windows, and each has a W row. `App.xaml` declares resources only; it was read
for `Click=` and other event attributes and has 0 of them.

### E2 - every window and user control, including the ones built in code

```
$ grep -nE "class \w+\s*:\s*(Window|UserControl)\b" src/AgentEyes.App/*.cs
DevThrottleSignInWindow.cs:15 DictTermDialog.cs:15 HudWindow.cs:35 MainWindow.xaml.cs:25
ManagePresetsDialog.cs:16 NoticeToast.cs:15 PluginCatalogDialog.cs:14 PluginConfigDialog.cs:15
PluginManagerWindow.cs:22 PresetEditor.xaml.cs:23 PreviewWindow.xaml.cs:15 PromptDialog.cs:8
RecordingDetailWindow.cs:17 SettingsDialog.cs:17 TestPanel.xaml.cs:25
count: 15
$ grep -cE "class \w+\s*:\s*UserControl\b" src/AgentEyes.App/*.cs     -> 0
$ grep -rnE "class \w+\s*:\s*(Wpf\.Window|Window)\b" src/AgentEyes.Core --include=*.cs
src/AgentEyes.Core/MonitorHighlight.cs:18   src/AgentEyes.Core/RegionOverlay.cs:20
```

- **Positive control for the UserControl zero:** the same pattern found the 15 `: Window` classes, so it can
  see a class declaration. There are no user controls. The MainWindow views are `Grid` panels toggled by the
  rail (`MainWindow.xaml:444`), and each one is inventoried in section V.
- **Rows:** 15 App windows -> W1 to W15. The 2 Core WPF windows the App opens -> W16 and W17. That makes
  **17 window rows**.
- **Windows created any other way:** `grep -nE "new Window\b|new Window \{"` returns 0. Its positive control
  is `new (\w+(Window|Dialog))\(`, which returns the 15 known constructions.
- **System dialogs:** `grep -nE "OpenFileDialog|OpenFolderDialog|FolderBrowserDialog"` returns 4 sites
  (`MainWindow.AlwaysOn.cs:199`, `MainWindow.xaml.cs:471`, `:1977`, `PluginManagerWindow.cs:198`). Each
  belongs to the row of the command that opens it: V64, V87, V39 and G1.
- **Message boxes:** `grep -c "MessageBox.Show"` gives 16 sites in 6 files. Each one is mapped to a row in
  Appendix A.3.

### E3 - the tray menu construction site

Every tray item is built in one method, `TrayHost.BuildMenu()` (`TrayHost.cs:206-249`). It is the only
`ContextMenuStrip` in the project.

```
$ grep -nE "new WinForms.ToolStrip(MenuItem|Separator)\(" src/AgentEyes.App/TrayHost.cs
210 "Status: idle"  215 "Show recording indicator"  219 "Show window"  220 "Tests..."
221 "Quick screenshot"  223 "Start recording"  229 "Pause always-on"  231 "Open clips folder"
232 "Always-on settings..."  235 "Open recordings folder"  236 "Check for updates"
238 "Install update now"  241 "Run at login"  246 "Quit"
items: 14  separators: 4
```

There are **14 menu items -> T1 to T14**, one row each. The icon's own behaviours are found by
`grep -nE "MouseClick|DoubleClick|BalloonTipClicked|ShowBalloonTip|_icon.Icon =|_icon.Text ="` and give
T15 to T19 (click, icon states, tooltip, balloons, exit-wait icon). Tray total: **19 rows**.

### E4 - the hotkey registration site

```
$ grep -rnE "SetWindowsHookEx|RegisterHotKey|new KeyboardHook\(" src/AgentEyes.App --include=*.cs
App.xaml.cs:233   new KeyboardHook(TriggerSpec.Parse(trigger))
KeyboardHook.cs:42  SetWindowsHookEx(WH_KEYBOARD_LL, ...)     (the one hook install)
$ grep -nE "ArmCaptureHook\(" src/AgentEyes.App/App.xaml.cs
223  ArmCaptureHook(_cfg!.CaptureRegionTrigger, "region", ...)       default "hotkey:printscreen" (Config.cs:87)
225  ArmCaptureHook(_cfg.CaptureFullTrigger, "full-screen", ...)     default "hotkey:ctrl+printscreen" (Config.cs:88)
```

The site is `App.InstallCaptureHooks()` (`App.xaml.cs:221-227`), and it arms **2 global hotkeys -> H1 and
H2**. `RegisterHotKey` has no hits, so the low-level hook is the only global-key mechanism.

```
$ grep -rnE "InputBinding|KeyBinding|KeyGesture" src/AgentEyes.App   -> 0
$ grep -rnE "KeyDown" src/AgentEyes.App --include=*.cs --include=*.xaml   (obj excluded)
MainWindow.xaml:579 RecentList_KeyDown   MainWindow.xaml:893 DictNewTerm_KeyDown
PreviewWindow.xaml:7 Window_KeyDown      RecordingDetailWindow.cs:89 _title.KeyDown
```

- **Positive control for the zero:** the same `grep -rnE` invocation finds the `KeyDown` lines. There are no
  WPF command bindings.
- **Rows:** the 4 in-window key handlers carry 5 keys (Delete; Enter; Esc and Space; Enter) and give H3 to
  H6. Hotkey total: **6 rows**.

### E5 - every UI event handler in the `.xaml` files

```
$ grep -oE '(Click|Checked|Unchecked|SelectionChanged|ValueChanged|TextChanged|KeyDown|MouseUp|...)="\w+"' <file> | sort -u
MainWindow.xaml 56   PresetEditor.xaml 19   PreviewWindow.xaml 4   TestPanel.xaml 11      total 90
```

The raw `Checked=` pattern also matches `IsChecked="True"`, which is not a handler. That one false hit is
removed from the counts above. **Appendix A.1 maps all 90 handlers, one by one, to a row ID.** No handler is
left unmapped. The handlers wired in code (`.Click +=`) in the 11 code-only windows are listed per window in
the W rows and their subsections.

### E6 - background behaviours

Background behaviour means anything the app starts that is not a click. These are everything
`App.OnStartup` (`App.xaml.cs:37-198`) and `App.OnExit` (`:331-377`) construct or subscribe, plus the
per-window timers. They are enumerated by reading those two methods line by line: B1 to B20.

### The count

| Section | Rows | Enumerated against |
|---------|------|--------------------|
| W windows and dialogs | 17 | E2: 15 App windows + 2 Core WPF windows |
| T tray | 19 | E3: 14 `ToolStripMenuItem` + 5 icon behaviours |
| H hotkeys and keys | 6 | E4: 2 global hooks + 4 in-window key handlers |
| V MainWindow views and commands | 74 | E5: all 56 MainWindow.xaml handlers map to a V or H row (Appendix A.1; some rows hold several handlers), plus the code-built preset menu and the display-only items with no handler (meters, chips, status lines, overlays) |
| S settings | 6 | `SettingsDialog.cs` tabs General, Account, Plugins |
| P preset editor and manage presets | 11 | E5: PresetEditor.xaml's 19 handlers + `ManagePresetsDialog.cs:67-71` |
| G plugins | 6 | `PluginManagerWindow.cs`, `PluginCatalogDialog.cs`, `PluginConfigDialog.cs` |
| D diagnostics (Tests) | 9 | E5: TestPanel.xaml's 11 handlers + `TestPanel.xaml.cs:54-70` test list |
| U recording HUD | 11 | `HudWindow.cs` buttons and the window's styles (E2) |
| B background | 20 | E6 |
| **Total** | **179** | |

The table in section 2 has 179 rows. The check is a command that counts them by section prefix:
`grep -cE "^\| (W|T|H|V|S|P|G|D|U|B)[0-9]+ \|" parity-inventory.md` returns 179.

---

## 2. The inventory

Columns:

- **ID:** the row ID.
- **WPF item and where it was read:** the WPF side.
- **Avalonia, where it was read:** the Avalonia side. All Avalonia paths are relative to
  `src/AgentEyes.DesktopApp/`. All WPF paths are relative to `src/AgentEyes.App/` unless they start `Core/`.
- **Status:** PRESENT, PARTIAL or MISSING.
- **What was compared:** what was set side by side.

### W - windows and dialogs (E2)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| W1 | Main window: 64 px icon rail, top bar, view host (`MainWindow.xaml:267-1131`) | `Views/MainWindow.axaml` | PARTIAL | WPF rail has 5 views: Record, Capture, Always On, Library, Dictionary (`MainWindow.xaml:279-326`). Avalonia has 3: Record, Library, Dictionary (`MainWindow.axaml:127-149`). Each view is broken down in section V |
| W2 | Tests / Diagnostics window (`TestPanel.xaml`, `TestPanel.xaml.cs`) | none: no `TestPanel` and no `Diagnostic` string in DesktopApp (positive control: the same grep finds `TrayIcon` in 3 files) | MISSING | see D1 to D9 |
| W3 | Preset editor (`PresetEditor.xaml`, `.xaml.cs`, 1,120 lines) | none: no `Preset` type, and the only `preset` hits are 2 comments saying Linux has none (`Model/AppSettings.cs:9`, `MainWindow.axaml:236`) | MISSING | see P1 to P9 |
| W4 | Preview player (`PreviewWindow.xaml`; MediaElement; Play/Pause, Restart, Seek; Esc closes, Space toggles) | `Views/PlayerWindow.axaml(.cs)` (LibVLC; Play/Pause, Restart, Seek; Esc, Space) | PRESENT | Controls side by side: PlayPause_Click, Restart_Click, Seek and Window_KeyDown (`PreviewWindow.xaml.cs:91-95`) against PlayPause_Click, Restart_Click, Seek and OnKeyDown (`PlayerWindow.axaml.cs`). Both show stills. WPF sizes the window to the video's natural size (`Media_Opened`, `:104`); Avalonia's sizing was not compared. Neither was played on Windows |
| W5 | Recording HUD (`HudWindow.cs`, 915 lines) | none: 0 hits for `Topmost`, `Hud`, `HUD`, `DisplayAffinity`, `ExcludeFromCapture`, `Discard` | MISSING | see U1 to U11 |
| W6 | Recording details (`RecordingDetailWindow.cs`): editable title, summary, AI-off banner with Sign in, transcript, and Play / Walkthrough / Copy transcript / Open folder / Delete (`:195-236`) | `Views/DetailWindow.axaml(.cs)`: same title, summary, banner and transcript, and Play / Walkthrough / Copy transcript / Open folder / Delete (`DetailWindow.axaml:24-28`) | PRESENT | The button lists match one for one. The walkthrough label switches between Open and Build in both. Rename: WPF commits on Enter; Avalonia commits on Enter and on focus loss, and also closes on Esc. Avalonia renames and deletes through the API (see finding 2) |
| W7 | Settings dialog: General, Account and Plugins tabs, OK / Cancel (`SettingsDialog.cs:44-153`) | `Views/SettingsWindow.axaml`: General and Account tabs, Close | PARTIAL | Tabs: there is no Plugins tab (S5). General: auto-update (S2) and API/port (S3) are missing. Account: S4. WPF applies on OK; Avalonia applies each change at once and has only Close |
| W8 | Manage presets dialog (`ManagePresetsDialog.cs`) | none | MISSING | see P10 and P11 |
| W9 | Plugins window (`PluginManagerWindow.cs`) | none: 0 hits for `Plugin` and `plugin` | MISSING | see G1 to G4 |
| W10 | Plugin catalog dialog (`PluginCatalogDialog.cs`) | none | MISSING | see G5 |
| W11 | Plugin configure dialog (`PluginConfigDialog.cs`) | none | MISSING | see G3 |
| W12 | DevThrottle sign-in window (`DevThrottleSignInWindow.cs`): explains the account, Cancel, opens the browser flow | `Views/SettingsWindow.axaml.cs` `SignIn_Click`, the same browser flow (`DevThrottleSignIn.SignInAsync`) inline in the Account tab, where the button turns into "Cancel sign-in" | PARTIAL | The flow and the call are the same, but there is no standalone window. That matters only for B12 (the first-run prompt), which opens this window when the app starts signed out |
| W13 | Dictionary term editor (`DictTermDialog.cs`): one dialog for the term and its misheard forms, Save / Cancel, and rejects an empty term (`:75`) | `Views/MainWindow.axaml.cs:815-825`: two prompts in a row, "Term:" then "Misheard as (comma separated)" | PARTIAL | An empty term counts as a cancel in Avalonia (`Dialogs.cs:50` returns null for blank). **But cancelling the second prompt clears every misheard form and saves:** `r.Variants.Clear()` runs whatever `v` is, and `AddRange` only when `v != null` (`MainWindow.axaml.cs:821-824`). WPF Cancel changes nothing. This is from reading the code; it was not observed |
| W14 | Notice toast (`NoticeToast.cs`): a topmost corner toast with an action button. Used for hosted-AI refusals: "View plans" -> pricing URL, "Open Billing" -> credits URL (`MainWindow.xaml.cs:1790-1804`) | none: 0 hits for `Toast` and `Notification` | MISSING | When DevThrottle refuses a recording's AI work (out of credits, wrong plan), the Avalonia app tells the person nothing. See also V24 |
| W15 | Prompt dialog (`PromptDialog.cs`): title, prompt, text, OK / Cancel | `Views/Dialogs.cs` `PromptAsync` (plus `ConfirmAsync`, `InfoAsync`, `ChooseAsync`) | PRESENT | Same inputs and outputs. Avalonia also turns a blank answer into a cancel |
| W16 | Show-area flash (`Core/MonitorHighlight.cs`): outlines the monitor or region about to be recorded for 2.5 s with its size (`MainWindow.xaml.cs:1096-1114`) | none: 0 hits for `Highlight` and `Show area` | MISSING | see V13 |
| W17 | Region selection overlay (`Core/RegionOverlay.cs`): drag a rectangle on screen; Esc cancels. Used by Region snip, the region hotkey and the preset editor's "Select area..." | none: 1 `region` hit, which is a label string (`Model/LibraryEntry.cs:61`) | MISSING | see H1, V61 and P2 |

### T - tray (E3)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| T1 | "Status: idle" / "Recording mm:ss (mode)" / "Finishing the recording before closing..." (`TrayHost.cs:210`, `:250-268`) | `_status` with "Ready to record" / "Recording mm:ss" / "Recorder service is not running" (`TrayController.cs:19`, `:75-77`) | PARTIAL | Avalonia does not show the mode, and has no "finishing before closing" text because its Quit does not wait (T14). Avalonia adds an offline state |
| T2 | "Show recording indicator": visible only while the HUD is hidden (`TrayHost.cs:215`) | none | MISSING | There is no HUD (U1) |
| T3 | "Show window" (`TrayHost.cs:219`) | "Show AgentEyes" (`TrayController.cs:39`) | PRESENT | Both call the app's show-window action |
| T4 | "Tests..." opens the diagnostics window (`TrayHost.cs:220`) | none | MISSING | see W2 |
| T5 | "Quick screenshot" of the primary monitor (`TrayHost.cs:221`) | none | MISSING | There is no screenshot command anywhere in DesktopApp. `ControlClient` has no `/screenshot` or `/record/shot` call (`ControlClient.cs:45-117`) |
| T6 | "Start recording (*preset name*)" / "Stop recording": starts the last-used preset; stops through `RecordingStop.Keep` in-process (`TrayHost.cs:223`, `:291-335`) | "Start recording" / "Stop recording" over `/record/start` and `/record/stop` (`TrayController.cs:32-37`) | PARTIAL | Avalonia starts its single remembered setup (screen, mic, system sound), not a preset, and its label names nothing. If the last WPF preset was audio-only, a screenshot or a region, the Avalonia tray records full-screen video instead (V19 and V20) |
| T7 | "Pause always-on" / "Resume always-on" (`TrayHost.cs:229`) | none: 0 hits for `AlwaysOn` and `always-on` | MISSING | see V80 to V95 |
| T8 | "Open clips folder" for always-on (`TrayHost.cs:231`) | none | MISSING | |
| T9 | "Always-on settings..." opens the window on the Always On view (`TrayHost.cs:232`, `App.xaml.cs:313`) | none | MISSING | |
| T10 | "Open recordings folder" (`TrayHost.cs:235`, explorer on `RecordingPaths.Root`) | "Open recordings folder" (`TrayController.cs:42`, `Shell.Open` -> explorer.exe on Windows, `Shell.cs`) | PRESENT | Same folder, same opener on Windows |
| T11 | "Check for updates" (`TrayHost.cs:236` -> `UpdateChecker.CheckAndPrompt`) | none: 0 hits for "Check for updates" | MISSING | see B7 |
| T12 | "Install update vX now (restarts AgentEyes)": shown only while an update waits for a recording to end (`TrayHost.cs:238`, `:172-182`) | none | MISSING | see B7 |
| T13 | "Run at login" checkable item, which writes the HKCU Run key with `--tray` (`TrayHost.cs:241`, `Autostart.cs`) | not in the tray. The Settings checkbox (`SettingsWindow.axaml:14`) writes an **XDG `.desktop` file** (`Model/AppSettings.cs:61-95`) | PARTIAL | Avalonia has no tray item. And on Windows its Autostart writes `%USERPROFILE%\.config\autostart\agenteyes.desktop`, which Windows never reads, so the checkbox would show "on" and nothing would start at login. This is read from the code, not run. See S1 |
| T14 | "Quit": with a recording running it finishes the recording (mix, transcript, title) and then exits; a second click forces; the balloon says so (`TrayHost.cs:364-410`). Because the recorder is in-process, Quit also ends recording | "Quit AgentEyes (the recorder keeps running)", deferred one dispatcher turn (`TrayController.cs:46-50`, `DeferredQuit.cs`) | PARTIAL | The meaning differs. WPF Quit ends AgentEyes; Avalonia Quit ends only the window and tray, and a recording keeps going in the service. Which one is right on Windows depends on finding 1 |
| T15 | Left-click shows the window; double-click shows it too (`TrayHost.cs:81-82`) | `_tray.Clicked` shows the window (`TrayController.cs:53`) | PRESENT | Left-click is equivalent. There is no separate double-click handler in Avalonia, and what a double-click does there was not observed. #116 QA noted the icon is hidden in the overflow by default on Windows 11 and is a near-invisible off-white on the light flyout (`qa-report-windows.md:170`) |
| T16 | Icon states: plain = idle; red dot = recording or always-on listening; red with white centre = keeping a clip; grey = always-on paused or retrying (`TrayHost.cs:68-72`, `:106-125`, `TrayDot.cs`) | moon = idle; red dot = recording; crossed moon = recorder offline (`TrayController.cs:21-23`, `:83`) | PARTIAL | Recording and idle match. The three always-on states are missing. Offline is Avalonia-only |
| T17 | Tooltip: "recording mm:ss", and whether the indicator is hidden; always-on state; always-on start error (`TrayHost.cs:124-151`) | "AgentEyes - recording" / "AgentEyes" (`TrayController.cs:84`) | PARTIAL | No elapsed time, no indicator state, no always-on text |
| T18 | Balloons: "Always-on did not start" (once per reason), "AgentEyes update ready" (click installs), info such as "you are up to date", "AgentEyes is closing" (`TrayHost.cs:147`, `:177`, `:184`, `:380`) | none | MISSING | Avalonia has no balloon or notification API in use (0 hits for `Notification`) |
| T19 | Exit-wait icon: with always-on on, the icon stays up as "writing the always-on clip, then quitting" (`TrayHost.cs:158-169`, `App.xaml.cs:350`) | none | MISSING | Depends on always-on (B6) |

### H - hotkeys and keys (E4)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| H1 | Global **region snip** hotkey, default PrintScreen: low-level hook -> region overlay -> PNG plus clipboard (`App.xaml.cs:223`, `:264-283`, `KeyboardHook.cs:42`, `Config.cs:87`) | none: 0 hits for `KeyboardHook`, `Hotkey`, `HotKey`, `PrintScreen` | MISSING | |
| H2 | Global **full-screen snip** hotkey, default Ctrl+PrintScreen (`App.xaml.cs:225`, `:286-299`, `Config.cs:88`) | none | MISSING | |
| H3 | Library: **Delete** removes the selection, after a confirm (`MainWindow.xaml.cs:1876`) | `RecentList_KeyDown` (`MainWindow.axaml.cs:537-544`) | PRESENT | Both delete the multi-selection after a confirm |
| H4 | Dictionary: **Enter** in the new-term box adds the term (`MainWindow.xaml.cs:349`) | `NewTermBox_KeyDown` (`MainWindow.axaml.cs:850`) | PRESENT | Same key, same action |
| H5 | Player: **Esc** closes, **Space** plays or pauses (`PreviewWindow.xaml.cs:91-95`) | `OnKeyDown` (`PlayerWindow.axaml.cs`) | PRESENT | Same two keys |
| H6 | Details: **Enter** in the title box renames (`RecordingDetailWindow.cs:89`) | `TitleBox.KeyDown` (`DetailWindow.axaml.cs`) | PRESENT | Same. Avalonia adds Esc to close |

### V - main window views and commands (E5; Appendix A.1 maps every handler here)

**Shell**

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| V1 | Rail view switching, `Rail_Checked` (`MainWindow.xaml.cs:208`) | `Rail_Changed` (`MainWindow.axaml.cs:88`) | PARTIAL | Works for the 3 views Avalonia has. Capture and Always On are missing (V60 to V95) |
| V2 | Rail Help button -> menu with "About AgentEyes" and "Diagnostics..." (`MainWindow.xaml:366-373`) | Help button opens About directly (`MainWindow.axaml:175`) | PARTIAL | Diagnostics is missing (W2) |
| V3 | About: version message box (`MainWindow.xaml.cs:1089`) | About dialog with version, recordings path, data path and service URL (`MainWindow.axaml.cs:884-890`) | PARTIAL | **The Avalonia text says "AgentEyes {version} for Linux" on every platform** (`MainWindow.axaml.cs:888`) |
| V4 | Overflow menu: "Open recordings folder", "Check for updates...", "About AgentEyes" (`MainWindow.xaml:401-404`) | Overflow menu: "Open recordings folder", "Refresh library", "About AgentEyes" (`MainWindow.axaml:194-197`) | PARTIAL | "Check for updates..." is missing. Avalonia adds Refresh library |
| V5 | Account rail item with a signed-out badge; opens Settings on the Account tab (`MainWindow.xaml.cs:172-203`) | `RailAccount` with badge and label; opens Settings on Account (`MainWindow.axaml.cs:151-153`, `:874`) | PRESENT | Same badge rule and target. Source of truth differs: WPF reads the local credential (`AccountState`); Avalonia reads `/status` `SignedIn` from the service |
| V6 | Closing the window hides it to the tray (`App.xaml.cs:305`) | `Closing` cancelled -> Hide (`App.axaml.cs:46-52`) | PRESENT | Same |
| V7 | Top-bar view title (`ViewTitle`, `MainWindow.xaml:390`) | `ViewTitle` (`MainWindow.axaml:189`) | PRESENT | Same |
| V8 | Status line (`StatusText`) | `StatusText` (`MainWindow.axaml:211`) | PRESENT | Same role. The messages differ by command |

**Record view**

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| V10 | Hero REC / STOP / CAPTURE button (`MainWindow.xaml:452-454`, `Record_Click` `:1119`) | `RecordButton` REC / STOP (`MainWindow.axaml:227`, `:170`) | PARTIAL | Start and stop match. WPF shows CAPTURE for a screenshot preset; Avalonia has no screenshot mode (V19) |
| V11 | Preset split-dropdown: the preset list, "New preset...", "Manage presets..." (`PresetMenu_Click` `MainWindow.xaml.cs:988-1016`) | Screen combo, Microphone combo, System-sound toggle (`MainWindow.axaml:237-246`, `Model/AppSettings.cs`) | MISSING | Avalonia has one remembered setup and no presets. Existing presets in `%LOCALAPPDATA%\AgentEyes\` are not read by it |
| V12 | Edit-current-preset button (`EditCurrentPreset_Click` `:1037`) | none | MISSING | see W3 |
| V13 | Show-area button: flashes the monitor or region bounds for 2.5 s (`ShowArea_Click` `:1096`) | none | MISSING | see W16 |
| V14 | Capture summary chips built from the preset: video (mode, fps, monitor or region), mic and system (volumes), camera (`UpdateCaptureChips` `:815`) | `RenderCaptureSummary` (`MainWindow.axaml.cs:266-277`): VIDEO / MIC / SYSTEM cards | PARTIAL | **The Avalonia cards hard-code "30 fps", "100%" and "70%"** (`:272-274`); they are not read from any setting. There is no camera chip |
| V15 | Monitor strip, to scale, selected monitor highlighted (`UpdateMonitorStrip` `:932`) | `RenderMonitorStrip` (`MainWindow.axaml.cs:317`) | PRESENT | Same picture. WPF also draws a selected region |
| V16 | Live MIC / SYS meters while recording, peak x180; a source not recorded is hidden (`SetRecordingUi` `:1415-1438`, `OnTick` `:1440`) | `LevelPanel`, x180 (`MainWindow.axaml.cs:142-147`) | PARTIAL | Same scale. Avalonia shows both meters whatever the source |
| V17 | Elapsed "REC mm:ss" (`OnTick`) | `ElapsedText` (`MainWindow.axaml.cs:137`) | PRESENT | Same |
| V18 | Minimize **before** capture starts, then wait for idle plus 250 ms so the window is not in the first frames; restore after a screenshot or a failed start (`MinimizeBeforeCaptureAsync` `:1169-1190`) | Minimize **after** `StartRecordingAsync` returns (`MainWindow.axaml.cs:180-188`) | PARTIAL | In Avalonia the window is on screen while the recording starts, so the opening frames would show AgentEyes. Read from the code, not observed. There is no restore on failure |
| V19 | Modes: Video, "Audio + shots", Screenshot (`PresetEditor.xaml:125-133`, `Record_Click` `:1125-1140`) | video only. `StartAsync(screen, source, mic)` sends no `mode` (`ControlClient.cs:72`, `AppState.cs:96`) | MISSING | Audio-only recording and one-click screenshot are gone |
| V20 | Per-recording options from the preset: region, camera track, fps, noise suppression, noise gate, voice levelling, mic and system volume (`PresetEditor.xaml`, `CapturePreset.cs`) | none: 0 hits for `Camera`, `camera`, `Denoise`, `denoise`, `Fps`; `/record/start` body is screen, source and mic only | MISSING | The headless route accepts these fields (`HeadlessControlServer.cs:361`); the app never sends them |
| V21 | Discard the recording, i.e. stop and delete (`DiscardAsync` `:1392`, from the HUD) | none: 0 hits for `Discard` | MISSING | see U5 |
| V22 | HUD shown on start, closed on stop (`ShowHud` `:1362`, `CloseHud` `:1381`) | none | MISSING | see U1 |
| V23 | Stop progress: "Saving ..." labels in the status line and HUD, then per-card stages (saving, transcribing, titling, packaging) (`StopAsync` `:1233-1316`) | "Stopping and saving...", then "Saved X - transcribing and titling follow."; cards show "Recording..." or "Transcribing..." (`MainWindow.axaml.cs:65-69`, `:456-463`) | PARTIAL | Avalonia has two card states against WPF's per-stage labels |
| V24 | Post-recording failure surfaced: "Transcribe error: ..." plus the hosted-AI refusal toast (`OnPostRecordingFailed` `:1333`, `ShowHostedAiRefused` `:1790`) | none: `AppState.Failed` fires only for a failed start or stop (`AppState.cs:102`, `:126`) | MISSING | A recording whose transcript or title fails says nothing in the Avalonia app |
| V25 | Credits refreshed in an open Settings window after each stop (`RefreshDevThrottleCreditsDisplayAsync` `:1817`) | Settings reads credits when it opens (`SettingsWindow.axaml.cs` `RefreshAccountAsync`) | PARTIAL | No refresh after a recording |
| V26 | A stop from the tray or API resets the window to REC (`OnRecordingStopped` `:1200`, `ResetAfterOutsideStop` `:1211`, tick guarantee) | Derived from the 1 s `/status` poll (`AppState.cs:56-87`, `Render`) | PRESENT | Both converge on the engine state within a second |
| V27 | "Recordings are automatically transcribed and titled." (`MainWindow.xaml:558`) | same text (`MainWindow.axaml:260`) | PRESENT | Identical string |

**Library view**

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| V30 | Grid / list toggle (`LibraryGrid_Click`, `LibraryList_Click` `:1591-1592`) | `GridMode_Click`, `ListMode_Click` (`MainWindow.axaml.cs:488-512`) | PRESENT | Same two modes. Avalonia also remembers the choice (`Settings.LibraryMode`) |
| V31 | Search by title, case-insensitive (`MainWindow.xaml.cs:76-77`) | `ApplyFilter` (`MainWindow.axaml.cs:467-486`) | PRESENT | Both match `Title.Contains(q, OrdinalIgnoreCase)` only |
| V32 | Order: newest first by manifest start, undated last, tie on folder name (`RecentItemComparer` `:2711-2726`) | `NewestFirst` (`Model/LibraryEntry.cs:196-213`) | PRESENT | Comparer bodies are identical line for line. Both list only folders that have `manifest.json` |
| V33 | Running "AI cost: $x (incl. estimates)" total in the top bar (`UpdateLibraryTotal` `:1566`, `LibraryTotalText` `MainWindow.xaml:418`) | `LibraryCountText` shows "N recordings" (`MainWindow.axaml.cs:480`) | MISSING | There is no total. Per-card cost tags are present in both (`MainWindow.axaml:65`, `:98`) |
| V34 | A plain click on a card previews it, or opens details if it cannot be previewed; Ctrl or Shift selects (`Card_MouseUp` `:1637`) | `RecentList_PointerReleased` (`MainWindow.axaml.cs:521-533`) | PRESENT | Same rule |
| V35 | Card hover actions: open folder, More (`MainWindow.xaml:125-134`) | `OpenFolder_Click`, `More_Click` (`MainWindow.axaml:47-50`) | PRESENT | Same two buttons |
| V36 | Card context menu: Preview, Details..., Rename..., Export with subtitles..., Delete, Open folder, Build walkthrough (`MainWindow.xaml:11-18`) | Same 7 headers (`MainWindow.axaml:11-18`) | PRESENT | Headers identical. In Avalonia, Rename, Delete, Export and Build walkthrough go over the API to routes that only the headless server serves (finding 2) |
| V37 | Artifact chips: Transcript, Text file, Walkthrough, Processing... (`MainWindow.xaml:166-200`) | same four (`MainWindow.axaml:67-77`) | PRESENT | Same chips and conditions |
| V38 | Transcribing overlay on the card thumbnail (`MainWindow.xaml:141`) | `IsProcessing` overlay (`MainWindow.axaml:54-57`) | PRESENT | Same overlay. Its text detail is covered in V23 |
| V39 | "Import video" button: file picker -> `VideoImport.Run` (`ImportVideo_Click` `:1975`, `MainWindow.xaml:413`) | none: 0 hits for `Import` | MISSING | |
| V40 | Multi-select (Extended) with one confirm for N (`DeleteRecordings` `:1886`) | `SelectionMode` multiple, `DeleteAsync(list)` (`MainWindow.axaml.cs:617-656`) | PRESENT | Same confirm wording pattern. Avalonia reports a 409 as "still being worked on" |
| V41 | Empty state with "Start your first recording" (`MainWindow.xaml:586-598`) | `EmptyState` (`MainWindow.axaml:271-274`) | PRESENT | Same text. Avalonia adds a "No recordings match" text for searches |
| V42 | Live library refresh from in-process events (repair stages, imports, the stop sinks) through `LibraryCoherence` (`MainWindow.xaml.cs:1471`, `LibraryCoherence.cs`) | `FileSystemWatcher` on the recordings root with a 600 ms debounce, plus a rescan when `/status` pending or dir changes (`MainWindow.axaml.cs:159-164`, `:367-393`) | PRESENT | Different mechanism, same effect: the list follows the disk. Not observed on Windows |
| V43 | Missing thumbnails generated on demand (`EnsureThumbInBackground` `:1533`) | loads `thumb.jpg` only (`ViewModels/LibraryItem.cs` `LoadThumbAsync`) | PARTIAL | Avalonia relies on the recorder host's `RepairService` thumbnail pass to create a missing thumb, so it shows nothing until that pass runs |

**Dictionary view**

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| V50 | Term list with search over terms and misheard forms (`ApplyDictFilter` `:328`) | `RenderDictionary` (`MainWindow.axaml.cs:779`) | PRESENT | Same filter rule |
| V51 | Add term, by button or Enter, with a duplicate check (`DictAdd_Click` `:355`) | `AddTermAsync` (`MainWindow.axaml.cs:855`) | PRESENT | Same. Both write `DictionaryStore` |
| V52 | Add a misheard form (`DictAddVariant_Click` `:376`) | "+ misheard" button (`MainWindow.axaml.cs:807-814`) | PRESENT | Same |
| V53 | Edit a term and its forms (`DictEdit_Click` `:387` -> `DictTermDialog`) | two prompts (`MainWindow.axaml.cs:815-825`) | PARTIAL | See W13: cancelling the second prompt wipes the forms |
| V54 | Delete a term, after a confirm (`DictDelete_Click` `:399`) | Delete with `ConfirmAsync` (`MainWindow.axaml.cs:826-832`) | PRESENT | Same |

**Capture view** (`CaptureViewPanel`, `MainWindow.xaml:951-1131`). Avalonia has no Capture view: there is no rail
item, and none of these handler names or strings appear in DesktopApp. Every row is MISSING.

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| V60 | "New capture": full-screen snip of monitor 1 (`NewCapture_Click` `:679`) | none | MISSING | |
| V61 | "Region snip" via the overlay (`RegionSnip_Click` `:687`) | none | MISSING | |
| V62 | Shortcut labels and "Rebind" for the region shortcut, re-armed live (`RebindRegion_Click` `:743`, `RebindCapture` `:748`, `App.ReArmCaptureHooks`) | none | MISSING | |
| V63 | "Rebind" for the full-screen shortcut (`RebindFull_Click` `:744`) | none | MISSING | |
| V64 | Save folder "Change..." folder picker (`CaptureBrowseFolder_Click` `:467`) | none | MISSING | |
| V65 | Save folder "Use default", i.e. the Windows Screenshots known folder (`CaptureDefaultFolder_Click` `:490`) | none | MISSING | |
| V66 | Monitor picker, to scale, click to capture (`BuildCaptureMonitorPicker` `:514`, `CaptureMonitor_Click` `:597`) | none | MISSING | |
| V67 | Snip gallery with thumbnails, refreshed live on `CaptureSaved` (`LoadCaptures` `:616`, `OnCaptureSaved` `:669`) | none | MISSING | |
| V68 | Gallery "Open" (`CaptureOpen_Click` `:701`) | none | MISSING | |
| V69 | Gallery "Copy" image to the clipboard (`CaptureCopy_Click` `:707`) | none | MISSING | |
| V70 | Gallery "Reveal" in Explorer (`CaptureReveal_Click` `:723`) | none | MISSING | |
| V71 | Gallery "Delete" (`CaptureDelete_Click` `:730`) | none | MISSING | |
| V72 | Gallery empty state "No captures yet." (`MainWindow.xaml:1121`) | none | MISSING | |

**Always On view** (`AlwaysOnPanel`, `MainWindow.xaml:603-868`; `MainWindow.AlwaysOn.cs`;
`MainWindow.AlwaysOnHistory.cs`). Avalonia has no always-on anywhere: 0 hits for `AlwaysOn` and `always-on`,
and `ControlClient` has no `/always-on` call. Every row is MISSING.

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| V80 | "Recording setup": choose a screen-recording preset (`AOSetting_Changed` `MainWindow.AlwaysOn.cs:150`) | none | MISSING | |
| V81 | "Sound that counts": Microphone, System sound or Both (`AOCountMic`, `AOCountSystem`, `AOCountBoth`, `MainWindow.xaml:678-682`) | none | MISSING | |
| V82 | "Sound level that counts", with the threshold hint (`MainWindow.xaml:687-688`) | none | MISSING | |
| V83 | "Keep before the speech", 0 to 120 s (`MainWindow.xaml:698`) | none | MISSING | |
| V84 | "Keep after the speech" (`MainWindow.xaml:706`) | none | MISSING | |
| V85 | "Close the clip after silence of" (`MainWindow.xaml:714`) | none | MISSING | |
| V86 | "Disk cap", oldest deleted first (`MainWindow.xaml:722`, `Caps` `MainWindow.AlwaysOn.cs:143`) | none | MISSING | |
| V87 | "Save clips to" with "Change..." (`AOFolder_Click` `MainWindow.AlwaysOn.cs:195`) | none | MISSING | |
| V88 | "Start when Windows starts" (`AOStartWithWindows_Click` `MainWindow.AlwaysOn.cs:218`) | none | MISSING | |
| V89 | "Start always-on" / "Stop always-on" (`AOStart_Click` `MainWindow.AlwaysOn.cs:236`) | none | MISSING | |
| V90 | "Open clips folder" (`AOOpenFolder_Click` `MainWindow.AlwaysOn.cs:262`) | none | MISSING | |
| V91 | Today's kept clips; a click opens the folder (`UpdateAlwaysOnClips` `:355`, `AOClip_Click` `:273`) | none | MISSING | |
| V92 | Live status line: listening, keeping a clip and for how long, the rule in words (`UpdateAlwaysOnStatus` `:318`, `RuleInWords` `:306`) | none | MISSING | |
| V93 | Silent-microphone banner (`UpdateSilentMicBanner` `:383`) | none | MISSING | |
| V94 | "Stop always-on to change its settings." lock note (`AOLockedNote`, `MainWindow.xaml:758`) | none | MISSING | |
| V95 | History tab: newest-first events, filters All / Decisions / Levels / Problems, live append (`AOTabs_SelectionChanged`, `AOHistoryFilter_Changed`, `OnAlwaysOnHistoryAppended`, `MainWindow.AlwaysOnHistory.cs:37-195`) | none | MISSING | |

### S - settings (`SettingsDialog.cs`)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| S1 | General: "Run at login" via the HKCU Run key (`SettingsDialog.cs:55`, `Autostart.cs`) | "Run at login" checkbox via the XDG `.desktop` file (`SettingsWindow.axaml:14`, `Model/AppSettings.cs:61-95`) | PARTIAL | Linux implementation only. On Windows it would write a file Windows ignores (see T13). There is no Windows Run-key or macOS code path in DesktopApp |
| S2 | General: "Automatically check for updates on startup" (`SettingsDialog.cs:58`, `Config.AutoUpdate`) | none | MISSING | see B7 |
| S3 | General: "REST control API enabled" and "API port" (`SettingsDialog.cs:61-70`) | none: the URL is a constant (`ControlClient.cs:41`) | MISSING | This only means something in a process that hosts the API (finding 1) |
| S4 | Account: connected-as, masked key, credits, Sign in / Reconnect, Add credits, Sign out (`SettingsDialog.cs:73-100`, `:175-230`) | Account tab with the same set (`SettingsWindow.axaml:30-42`, `.cs` `RefreshAccountAsync`, `SignIn_Click`, `SignOut_Click`, `AddCredits_Click`) | PRESENT | Same fields and buttons. Avalonia also tells the service to refresh its account (`/account/refresh`, headless-only) |
| S5 | Plugins tab: "Manage plugins..." (`SettingsDialog.cs:112-122`) | none | MISSING | see G1 to G6 |
| S6 | OK / Cancel commit model (`SettingsDialog.cs:151-153`) | changes apply immediately; Close only (`SettingsWindow.axaml:9`) | PARTIAL | There is no way to cancel a change in Avalonia. Listed so the owner sees it, not as a feature |

### P - preset editor and manage presets (`PresetEditor.xaml(.cs)`, `ManagePresetsDialog.cs`)

Avalonia has no presets at all (see W3), so every row here is MISSING.

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| P1 | Name and optional note (`PresetEditor.xaml:39-43`) | none | MISSING | |
| P2 | Capture source: Full monitor or Region; "Select area..." (overlay), aspect lock, exact size with "Set", Quick 1080x1080 / 1080x1920 / 1920x1080 (`PresetEditor.xaml:56-117`) | none | MISSING | |
| P3 | Mode: Screenshot / Audio + shots / Video, and fps (`PresetEditor.xaml:125-133`, `Mode_Changed`) | none | MISSING | |
| P4 | Audio tab: Mic only / System only / Mic + System, and microphone choice (`PresetEditor.xaml:148-161`) | Mic combo plus System toggle (`MainWindow.axaml:242-246`) | PARTIAL | The mic and system choice exists as the single remembered setup; there is no per-preset choice |
| P5 | Clean-up: noise suppression, noise gate, voice levelling (`PresetEditor.xaml:164-177`) | none | MISSING | |
| P6 | Mix: mic volume and system volume sliders (`PresetEditor.xaml:179-198`, `Vol_Changed`) | none | MISSING | V14's "100%" and "70%" are hard-coded text, not settings |
| P7 | Camera tab: camera choice, live preview, overlay framing (shape, left/right, up/down, diameter, reset, corner, size on screen, to-scale picture) (`PresetEditor.xaml:217-443`, `CameraPreviewController.cs`) | none | MISSING | |
| P8 | "Show area" in the editor (`PresetEditor.xaml:63`) | none | MISSING | |
| P9 | Save / Save as... / Cancel (`PresetEditor.xaml:456-460`) | none | MISSING | |
| P10 | Manage presets: New..., Edit..., Duplicate, Rename, Delete with the last-preset guard (`ManagePresetsDialog.cs:67-71`, `:150-170`) | none | MISSING | |
| P11 | "Last used preset" remembered across runs (`RememberUsed` `MainWindow.xaml.cs:1352`, `Config.LastUsedPresetId`) | the single setup is remembered (`Model/AppSettings.cs`) | PARTIAL | Avalonia remembers one setup, not a choice between several |

### G - plugins (`PluginManagerWindow.cs`, `PluginCatalogDialog.cs`, `PluginConfigDialog.cs`, `Plugins.cs`, `PluginRegistry.cs`)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| G1 | "Install from file..." for a plugin `.zip` or folder (`PluginManagerWindow.cs:50`, `:198-250`) | none | MISSING | |
| G2 | Installed list with enable/disable, "Update to vX" (`PluginManagerWindow.cs:125-160`) | none | MISSING | |
| G3 | "Configure" -> plugin settings dialog, Save / Cancel (`PluginManagerWindow.cs:142-143`, `PluginConfigDialog.cs`) | none | MISSING | |
| G4 | "Remove", after a confirm (`PluginManagerWindow.cs:146`, `:184`) | none | MISSING | |
| G5 | "Browse catalog...": the registry list with Install / Update / Installed (`PluginCatalogDialog.cs`) | none | MISSING | |
| G6 | Explanatory notes ("Enabled plugins run after each recording is transcribed, each in its own process") (`PluginManagerWindow.cs:101`, `:111`) | none | MISSING | The behaviour itself is row B9 |

### D - diagnostics, the Tests window (`TestPanel.xaml(.cs)`, `InjectionVerifier.cs`)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| D1 | Test "Devices": the monitors and mics the app sees (`TestPanel.xaml.cs:54`) | none | MISSING | Settings shows only the service line, not devices |
| D2 | Test "Screenshot": full-monitor and region capture (`:55`) | none | MISSING | |
| D3 | Test "Injected self-test", about a minute, injects its own audio (`:56`) | none | MISSING | |
| D4 | Test "Mic check - *name*", one per microphone (`:61`) | none | MISSING | |
| D5 | Tests "Voice + music (speakers)" and "(headphones)" (`:64-66`) | none | MISSING | |
| D6 | Test "Screen walkthrough" (`:68`) | none | MISSING | |
| D7 | Guided run: Run, Start, a RECORDING card with MIC / SYSTEM meters, Stop, and the TRANSCRIPT shown (`TestPanel.xaml:45-147`) | none | MISSING | |
| D8 | "Tune and retry": mic noise gate, mic and system volume, Run again (`TestPanel.xaml:157-185`) | none | MISSING | |
| D9 | Play, Open folder, and the "Did this look/sound right?" Yes / No verdict (`TestPanel.xaml:180-193`) | none | MISSING | |

### U - the recording HUD (`HudWindow.cs`, `HudPreviewState.cs`, `HudUserResize.cs`, `HudSizeMemory.cs`, `PreviewFrameFeed.cs`, `RecordingIndicator.cs`)

The HUD is capture-excluded, so these rows come from its source and its UIA names. None of them come from a
screenshot. Avalonia has no HUD (0 hits for `Topmost`, `Hud`, `HUD`, `DisplayAffinity`), so every row is
MISSING.

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| U1 | The window: always-on-top pill shown only while recording, title "Recording HUD" (`HudWindow.cs:119-128`) | none | MISSING | |
| U2 | Excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`, `HudWindow.cs:21`, `:885-910`) and never takes focus (`WS_EX_NOACTIVATE`, class summary) | none | MISSING | |
| U3 | Pulsing red dot and elapsed timer "00:00" (`HudWindow.cs:151`) | none | MISSING | |
| U4 | Live mic and system meters (class summary, `HudWindow.cs:35-55`) | none | MISSING | |
| U5 | Discard: stop and delete, after a confirm (`HudWindow.cs:217`, `:467`) | none | MISSING | |
| U6 | STOP (`HudWindow.cs:204-209`) | none | MISSING | Stop exists in the window and tray, but not on a floating control |
| U7 | Hide: the HUD goes and the recording continues; it comes back from the tray (T2) (`HudWindow.cs:192-197`, `RecordingIndicator.cs`) | none | MISSING | |
| U8 | "Show preview": a live preview of what is being recorded (`HudWindow.cs:173-181`, `PreviewFrameFeed.cs`) | none | MISSING | |
| U9 | Preview mode Screen / Camera / Both, and inset corner TL / TR / BL / BR; UIA "HUD preview status" (`HudWindow.cs:280-320`) | none | MISSING | |
| U10 | Draggable; position saved in config (class summary, `Config`) | none | MISSING | |
| U11 | User resize with a grip, and the size remembered (`HudUserResize.cs`, `HudSizeMemory.cs`) | none | MISSING | |

### B - background behaviours (E6: `App.OnStartup` and `App.OnExit`, `App.xaml.cs:37-377`)

| ID | WPF item and where it was read | Avalonia, where it was read | Status | What was compared |
|----|-----|-----|-----|-----|
| B1 | **Hosts the recorder**: `new RecordingService()` in-process (`App.xaml.cs:98`) | none: the app is an HTTP client (`AppState.cs`, `ControlClient.cs`) | MISSING | Finding 1. On Linux and macOS the host is `agenteyes serve` (`HeadlessHost.cs`). On Windows nothing hosts it once WPF is deleted |
| B2 | **Serves the Control API on 127.0.0.1:7882** when `ApiEnabled` (`App.xaml.cs:112-119`, `RestServer.cs`) | none | MISSING | Finding 1 and finding 2 (the route tables differ) |
| B3 | `RepairService`: resumes interrupted recordings, backfills transcripts, titles and thumbnails, and re-runs on sign-in (`App.xaml.cs:120-130`) | none in the app. `agenteyes serve` runs one (`HeadlessHost.cs` `repair.Start()`) | MISSING | Present in the headless host, not in the app process |
| B4 | Post-recording sequence wired once for every stop path (`RecordingStop.Keep` -> `PostRecording.Run`) (`App.xaml.cs:104-110`) | in whichever process serves `/record/stop` | MISSING | The app itself has none. The headless host stops through `RecordingStop.Keep` (`HeadlessHost.cs` Shutdown, and its `/record/stop`) |
| B5 | Housekeeping (disk retention, every 15 min) driven by the person's settings, with GET/POST `/housekeeping` (`App.xaml.cs:129`, `:133-139`) | none in the app | MISSING | In the headless host it runs with **default** settings, because `HeadlessHost.cs` never sets `HousekeepingSettings` (`RepairService.cs:133-134`), and its API has no `/housekeeping` |
| B6 | Always-on controller, and **restored on startup** if it was on (`App.xaml.cs:142-143`, `:177`, `AlwaysOnController.cs`) | none | MISSING | Not in the headless host either (0 hits for `AlwaysOn` in `Headless/`) |
| B7 | Auto-update: checks the releases repo at startup (opt-out), hands over to the setup CLI, defers while a recording or post-processing is active, and finishes the handover when the session ends (`App.xaml.cs:156-172`, `UpdateChecker.cs`) | none: 0 hits for `UpdateChecker` and `update` | MISSING | |
| B8 | Planned-stop listener: the setup engine asks the app to quit through a named event (`QuitRequest.Listen`), the recording is kept, then exit (`App.xaml.cs:153-154`, `TrayHost.QuitRequested` `TrayHost.cs:416-452`) | none: 0 hits for `QuitRequest` | MISSING | Stage 2's upgrade path (criterion 4) depends on how the installer stops a running app |
| B9 | **Post-recording plugins** configured at startup and run after each transcription (`App.xaml.cs:110`, `PostRecordingPlugins.cs`) | none in the app, and **none in `agenteyes serve`** (the only caller is `App.xaml.cs:110`) | MISSING | Narrower finding in section 0 |
| B10 | Single instance: the `AgentEyes-singleinstance` mutex; a refused second start shows "AgentEyes is already running" unless it asked to start hidden (`App.xaml.cs:62-84`) | `SingleInstance.TryAcquire` over a named pipe; a second launch forwards its command (show, view, details) to the running app and exits (`Program.cs:40-41`, `Services/SingleInstance.cs`) | PRESENT | Different and arguably better: the second launch is honoured, not refused. The WPF mutex and the Avalonia pipe are different names, so on a machine running both, both would start |
| B11 | Hidden-start flags `--tray` and `--minimized` (`LaunchArguments`, `Tray = "--tray"`, `Minimized = "--minimized"`) | `--tray` only (`Program.cs:55-69` `CommandFrom`) | PARTIAL | `--minimized` is not recognised: it falls through to "show" and opens the window. Any existing shortcut or Run-key value using `--minimized` would open a window at login. The WPF `Autostart.cs` writes `--tray`, which works |
| B12 | **First-run sign-in prompt**: on a non-hidden start while signed out, the sign-in window opens before the main window (`App.xaml.cs:185-189`) | none at startup. The account badge and label show "Sign in" (`MainWindow.axaml.cs:151-153`) | MISSING | |
| B13 | One-time storage migration qa-record -> AgentEyes (`App.xaml.cs:96`) | none in the app. `agenteyes` CLI startup runs it (`Core/Program.cs:28`), so `agenteyes serve` does | MISSING | Present in the headless host, not in the app process |
| B14 | Crash handling: AppDomain and Dispatcher unhandled exceptions written to `%TEMP%\AgentEyes-crash.log`, plus a "Something went wrong - it has been logged and the app will keep running" box that keeps the app alive (`App.xaml.cs:89`, `:379-392`) | `UnhandledException` -> `Log.Error` only (`Program.cs:37-38`); no dispatcher handler, no crash file, no box | PARTIAL | An unhandled UI-thread exception in Avalonia ends the process (as in #116's tray crash) with nothing shown to the person, and `CLAUDE.md`'s crash-log location would not exist |
| B15 | Startup failure box: "AgentEyes could not start. See the log" (`App.xaml.cs:193-197`) | none | MISSING | |
| B16 | Tray recording-state poll, 1 s (`TrayHost.cs:30-31`) | `AppState.StartPolling`, 1 s `/status` (`AppState.cs:56-61`) | PRESENT | Same cadence |
| B17 | HUD preview armed from config before the first recording (`App.xaml.cs:104`) | none | MISSING | Depends on U8 |
| B18 | Exit: flush a pending config save (bounded 2 s) and settle the preview log (`App.xaml.cs:363-375`) | `ShutdownNow` closes the window and shuts down (`App.axaml.cs:101-108`) | PARTIAL | Avalonia has no config writer that could be in flight, so there is nothing to flush; the preview log does not exist. Listed so it is not mistaken for missing |
| B19 | Capture-saved event so an open gallery refreshes after a hotkey or API snip (`App.xaml.cs:35`, `:114-116`) | none | MISSING | Depends on V67 |
| B20 | Re-arm the capture hooks live after a rebind, no restart (`App.ReArmCaptureHooks` `App.xaml.cs:252-259`) | none | MISSING | Depends on V62 and V63 |

---

## 3. The gaps, as a list the owner can read without the table

These are grouped by what the owner would lose, with the row IDs that hold the detail.

1. **The recorder itself on Windows (B1 to B5, B9, B13).** The WPF app is the recorder; the Avalonia app is a
   remote for one. Something has to host `RecordingService` and the API on Windows. The two API servers also
   differ (finding 2), and post-recording plugins run only in the WPF host.
2. **Presets (V11, V12, P1 to P11, W3, W8).** None: no region, audio-only, screenshot mode, camera, fps,
   noise clean-up or volumes. There is one remembered screen/mic/system setup.
3. **Always On (V80 to V95, T7 to T9, T16, T19, B6).** The whole feature: page, history, tray items, icon
   states, restore at startup.
4. **Capture / snips (V60 to V72, H1, H2, B19, B20, W17, T5).** The view, the PrintScreen and
   Ctrl+PrintScreen hotkeys, region overlay, gallery, save folder, quick screenshot.
5. **Recording HUD (U1 to U11, V21, V22, T2, B17).** Floating stop, Discard, hide/show, live preview,
   capture-excluded.
6. **Diagnostics / Tests (W2, D1 to D9, T4, V2).**
7. **Plugins (W9 to W11, G1 to G6, S5, B9).**
8. **Updates (B7, B8, T11, T12, S2, V4).** Auto-update, check for updates, deferred install, and the setup
   engine's planned-stop channel.
9. **Import video (V39).**
10. **Smaller ones:**
    - Run at login on Windows writes an XDG file Windows ignores (T13, S1).
    - About says "for Linux" (V3).
    - The window is minimized after capture starts, not before (V18).
    - The capture chips hard-code 30 fps / 100% / 70% (V14).
    - There is no AI-cost total (V33).
    - There is no post-recording failure or hosted-AI refusal notice (V24, W14).
    - There is no first-run sign-in prompt (B12).
    - There is no crash log or startup-failure box (B14, B15).
    - `--minimized` is not recognised (B11).
    - The tray status, tooltip and balloons do less (T1, T17, T18).
    - Settings have no OK/Cancel (S6).
    - Edit term: cancelling the second prompt wipes the misheard forms (W13, V53). This one is a defect in
      the Avalonia app as it stands, not only a parity gap.
11. **Before stage 2 deletes anything:** `AgentEyes.DesktopApp.csproj:67` compiles
    `..\AgentEyes.App\TranscriptPresentation.cs` (finding 3).

**Counts:** 179 rows. PRESENT 36, PARTIAL 29, MISSING 114. The check is a command that counts the status
column: `grep -cE "^\| [A-Z][0-9]+ \|.*\| PRESENT \|"`, and the same with ` PARTIAL ` and ` MISSING `.

---

## Appendix A.1 - every XAML event handler mapped to a row (E5, 90 handlers)

**MainWindow.xaml (56):**

| Handler(s) | Row |
|------------|-----|
| AOClip_Click | V91 |
| AOFolder_Click | V87 |
| AOHistoryFilter_Changed | V95 |
| AOOpenFolder_Click | V90 |
| AOSetting_Changed | V80 to V86 |
| AOStartWithWindows_Click | V88 |
| AOStart_Click | V89 |
| AOTabs_SelectionChanged | V95 |
| About_Click | V3 |
| CaptureBrowseFolder_Click | V64 |
| CaptureCopy_Click | V69 |
| CaptureDefaultFolder_Click | V65 |
| CaptureDelete_Click | V71 |
| CaptureOpen_Click | V68 |
| CaptureReveal_Click | V70 |
| CardMore_Click | V35 |
| Card_MouseUp | V34 |
| CheckUpdates_Click | V4 |
| DeleteRecording_Click | V36 |
| Details_Click | V36 |
| DictAddVariant_Click | V52 |
| DictAdd_Click | V51 |
| DictDelete_Click | V54 |
| DictEdit_Click | V53 |
| DictNewTerm_KeyDown | H4 |
| DictSearch_TextChanged | V50 |
| EditCurrentPreset_Click | V12 |
| ExportSubtitles_Click | V36 |
| GoRecord_Click | V41 |
| Help_Click | V2 |
| ImportVideo_Click | V39 |
| LibraryGrid_Click, LibraryList_Click | V30 |
| NewCapture_Click | V60 |
| OpenFolder_Click | V35 |
| OpenRecordingMenu_Click | V36 |
| OpenRecordings_Click | V4 |
| Overflow_Click | V4 |
| PackageRecordingMenu_Click | V36 |
| Package_Click | V37 (list-row Walkthrough button) |
| PresetMenu_Click | V11 |
| Preview_Click | V36 |
| RailAccount_Click | V5 |
| Rail_Checked | V1 |
| RebindFull_Click | V63 |
| RebindRegion_Click | V62 |
| RecentList_KeyDown | H3 |
| Record_Click | V10 |
| RegionSnip_Click | V61 |
| RenameRecording_Click | V36 |
| Search_TextChanged | V31 |
| Settings_Click | W7 |
| ShowArea_Click | V13 |
| Tests_Click | V2 |
| TranscriptChip_Click | V37 |
| WalkthroughChip_Click | V37 |

**PresetEditor.xaml (19):**

| Handler(s) | Row |
|------------|-----|
| AudioSrc_Changed | P4 |
| Camera_Changed | P7 |
| Cancel_Click | P9 |
| EditorTabs_Changed | P7 (the camera preview runs only while its tab is open) |
| Mode_Changed | P3 |
| OverlayAdorner_MouseUp, OverlayCorner_Changed, OverlayReset_Click, OverlayShape_Changed, Overlay_Changed | P7 |
| QuickLandscape_Click, QuickSquare_Click, QuickVertical_Click, SelectArea_Click, SetExact_Click | P2 |
| SaveAs_Click, Save_Click | P9 |
| ShowArea_Click | P8 |
| Vol_Changed | P6 |

**PreviewWindow.xaml (4):**

| Handler(s) | Row |
|------------|-----|
| PlayPause_Click, Restart_Click, Seek_Changed | W4 |
| Window_KeyDown | H5 |

**TestPanel.xaml (11):**

| Handler(s) | Row |
|------------|-----|
| TestList_SelectionChanged, RunTest_Click | D1 to D7 |
| Start_Click, Stop_Click | D7 |
| Tuning_Changed, Vol_Changed, RunAgain_Click | D8 |
| Play_Click, OpenFolder_Click, ThumbUp_Click, ThumbDown_Click | D9 |

The mapping covers 56 + 19 + 4 + 11 = 90 handlers.

## Appendix A.2 - menus built in code (no XAML handler)

| Menu | Row |
|------|-----|
| Preset dropdown items: the preset list, "New preset...", "Manage presets..." (`MainWindow.xaml.cs:988-1016`) | V11 |
| Tray menu (`TrayHost.BuildMenu`) | T1 to T14 |

## Appendix A.3 - the 16 `MessageBox.Show` sites mapped to rows

| Site | Row |
|------|-----|
| App.xaml.cs:81 (already running) | B10 |
| App.xaml.cs:196 (could not start) | B15 |
| App.xaml.cs:243 (shortcut could not be installed) | H1, H2 |
| App.xaml.cs:281 (region failed) | H1, V61 |
| App.xaml.cs:296 (full-screen failed) | H2, V60 |
| App.xaml.cs:382 (unhandled) | B14 |
| DictTermDialog.cs:75 (empty term) | W13 |
| HudWindow.cs:467 (discard confirm) | U5 |
| MainWindow.xaml.cs:402 (delete term) | V54 |
| MainWindow.xaml.cs:1090 (About) | V3 |
| MainWindow.xaml.cs:1902 (delete recordings) | V40 |
| ManagePresetsDialog.cs:161 (last preset) | P10 |
| ManagePresetsDialog.cs:166 (delete preset) | P10 |
| PluginManagerWindow.cs:184 (remove plugin) | G4 |
| PluginManagerWindow.cs:228 (install failed) | G1 |
| PluginManagerWindow.cs:237 (install confirm) | G1 |

## Appendix B - in the Avalonia app but not in the WPF app

These are listed so the owner sees both directions. None of them is a gap.

| Item | Where | Note |
|------|-------|------|
| Offline banner and the crossed-out tray icon when the recorder does not answer | `MainWindow.axaml:220-223`, `TrayController.cs:23` | The banner's "Start the recorder" button runs `systemctl --user start agenteyes.service` (`MainWindow.axaml.cs:348-363`), and the offline message tells the person to run `systemctl` (`ControlClient.cs:133`). Both are Linux-only advice on Windows (also noted in #116 QA, `qa-report-windows.md:160`) |
| "Refresh library" in the overflow menu | `MainWindow.axaml:195` | |
| Settings: "Open recordings folder", "Open logs folder", recorder service line | `SettingsWindow.axaml:15-26` | |
| Launch commands `--view`, `--details`, `--preview`, `--settings`, `--snap DIR` (renders every window to PNG without taking focus) | `Program.cs:55-69`, `App.axaml.cs:76-96` | `--snap` is a proof tool that would also work on Windows |
| A second launch forwards its command to the running instance | `Services/SingleInstance.cs` | B10 |
| Library mode remembered across runs | `MainWindow.axaml.cs:492-499` | V30 |
