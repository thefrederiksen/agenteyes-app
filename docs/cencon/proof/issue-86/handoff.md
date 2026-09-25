# Issue #86 - Update replaces the files but leaves the running app on the old build - Developer handoff

Branch: `issue-86-update-restarts-app` (from `main` at 533d453, v1.11.4). Tracker: thefrederiksen/agenteyes-app.

I believe this is finished, with ONE criterion deliberately left to the tester: the live update
from 1.11.x to the next release on the owner's laptop (section 7). Everything else is proven by
unit tests against doubles - a fake running-app handle, a fake launcher, a fake or real file
replacer on a temp directory, a fake recorder with real short MP4 pieces - and each new check was
fired at the restored defect and shown to fail (`mutation-evidence.txt`). No built binary
(AgentEyesApp.exe, agenteyes.exe, the setup CLI or the wizard) was run by the developer at any
point; the gate is `dotnet build` + `dotnet test` only.

Files in this folder:

| File | What it is |
|------|-----------|
| `handoff.md` | this note |
| `mutation-evidence.txt` | the new tests fired at the two known-bad behaviours (M1: carry on replacing after a failed stop; M2: the pre-#86 Recover that ignores the handover), each built with the gate's command, run, quoted, reverted |

Gate on the final tree: `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)`;
`dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 2041, Skipped: 0, Total: 2041`
(1984 on `main`, +57 in this change). The four new test classes alone: `Passed: 57, Failed: 0`.

---

## 1. Root cause (confirmed in the code)

Two, one per requirement.

**The update left the running app on the old build.** `Commands.UpdateAsync` (setup CLI) computed
the plan and called `UpdateRunner.ApplyAsync` straight away; `InstallSwapper.Place` is rename-based, so
Windows let it replace `AgentEyesApp.exe` under the running process, and nothing afterwards touched
that process. The only stop in the whole path was inside the Inno v0.1 takeover branch. The wizard
(`EngineInstallRunner`) did stop the app first (issue #95) but never started it again. The app's own
AutoUpdate (`UpdateChecker`) swapped its own files in place and then restarted itself - the very
thing issue #107 showed to be unsafe for a single-file host, and a path with no shared decision.

Behind that: `RunningApp.StopAndWait`'s "graceful" phase was `Process.CloseMainWindow`, which a
`--tray` app (no main window) or a window that closes to the tray never honours. Every stop the
installer ever made was therefore the force kill after 3 s: ffmpeg died mid-piece and the always-on
engine never saw the stop coming.

**A restart split the open always-on clip and dropped the last piece** (the tester's 2026-09-24
finding). To the next start, a restart looked exactly like a crash: `Recover` joined the open clip's
holding folder whole and closed it, deleted the loose last piece "because its sound log went with
that run", and speech after the restart began a new clip with no lead-in. Once an update restarts
the app on purpose, every update would do that.

## 2. What changed, per project

### 2.1 `AgentEyes.Setup.Engine` (tools/AgentEyes.Setup.Engine)

- `UpdateRestartCycle.cs` (new, public): THE stop -> replace -> relaunch decision. `RunAsync(replace)`:
  `IRunningAppHandle.Find()`; if running, `StopAsync` and, when that returns false, throw
  `AppStopFailedException` BEFORE `replace` is called (the message names the pid, says "Nothing was
  replaced" and what to do); run `replace`; if the app was running, `IAppLauncher.Launch(exe, args)`
  with the instance's own exe path and argument list - also when `replace` threw (an app the cycle
  stopped is never left stopped; the failure then propagates). Returns the replace result plus an
  `AppRestartReport` whose `Describe()` is the exact console line: `restarted the running app (pid A -> pid B)`
  or `AgentEyes was not running - it was not started`. Also here: `RunningAppInstance(Pid, ExePath, Arguments)`,
  `IRunningAppHandle`, `IAppLauncher`, `UpdateRestartOutcome<T>`.
- `RunningAppHandle.cs` (new): the real handle. `Find()` = `Process.GetProcessesByName` on the layout's
  app name + `MainModule.FileName` + the process's command line from WMI (`Win32_Process.CommandLine`)
  split with `CommandLineToArgvW` and run through `LaunchArguments.ToCarryAcrossRestart` (drops argv[0]).
  `Describe(pid, exe, commandLine)` is the pure part; an unreadable command line THROWS (no guessed
  `--tray`, no relaunch with nothing). `StopAsync` = `RunningApp.StopAndWait(layout)` on a worker thread.
  `ProcessAppLauncher` (real `IAppLauncher`) starts the exe with `ArgumentList` and sets
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` explicitly from the layout - the CLI's temp copy deliberately drops
  that variable from its own environment, and an app inheriting that would unpack into %TEMP% (issue #120).
- `QuitRequest.cs` (new): the planned-stop channel. The app creates a named manual-reset event
  `AgentEyes-quit-<pid>` and waits on it (`Listen`); the engine sets it (`TrySignal(pid)`, false when no
  listener - an older build). Per pid, so the request reaches exactly the process the engine found.
- `RunningApp.cs`: phase 1 now signals the quit request first, then `CloseMainWindow`. `GracePeriod(delivered, graceful)`
  (pure): the whole grace when a request was delivered, else `min(graceful, LegacyGrace 3 s)`.
  `DefaultGraceful` 3 s -> 30 s (ffmpeg gets up to 15 s to finish its piece, then the handover and the
  rest of `App.OnExit`), `DefaultTimeout` 15 s -> 60 s. The injectable `StopAndWait` overload gains an
  optional `requestQuit` seam. The force kill and the confirming re-query are unchanged.
- `UpdateRestartPolicy.cs`: same enum, same pure `Decide`; the doc now says what the two outcomes mean
  since #86 (hand over now / defer) and that the decision precedes any file replacement.
- `InstallSwapper.cs`, `Orchestrator.cs`, `LaunchArguments.cs`: doc comments only (no caller replaces a
  running exe any more; the Orchestrator is kept for the offline end-to-end test; `ToCarryAcrossRestart`
  takes any argv).
- `AgentEyes.Setup.Engine.csproj`: `System.Management` 8.0.0 (the WMI read).

### 2.2 `AgentEyes.Setup.Cli` (tools/AgentEyes.Setup.Cli)

- `Commands.UpdateAsync`: when the plan has work (or an Inno takeover is due) the whole replace step -
  Inno removal + `UpdateRunner.ApplyAsync` - runs inside `UpdateRestartCycle.RunAsync`. `AppStopFailedException`
  -> `ERROR: <message>` on stderr (or `{ "failed": ... }` with `--json`) and exit 1, nothing replaced.
  `PrintRun` prints the report line after the run summary; the JSON gets a `restart` object
  (`restarted`, `oldPid`, `newPid`, `arguments`, `message`). Nothing to do / `--dry-run`: the app is not
  looked for. The Inno branch's own stop (issue #95) is folded into the cycle. Seams for tests:
  `RunningAppHandleFactory`, `AppLauncherFactory` (internal; `InternalsVisibleTo("AgentEyes.Tests")`).
- `CliHelp.cs`: the `install` and `update` pages say the running app is stopped before any file is
  replaced, started again with its arguments, and that nothing is replaced when it cannot be stopped.

### 2.3 `AgentEyes.Setup` (the wizard, tools/AgentEyes.Setup)

- `EngineInstallRunner.ApplyAsync`: the same cycle (real handle + real launcher) around Inno removal +
  apply; a failed stop marks the items "Could not stop the running AgentEyes", reports the error on the
  status line and returns (0, all) as before. `LastRestart` exposes the report. The private
  `StopRunningAppAsync` is gone (the cycle does it).
- `MainWindow.xaml.cs`: keeps the report; the Install step's done line carries the same words the CLI
  prints; `CompleteStep` receives the report.
- `CompleteStep.xaml.cs`: when the app was restarted the page says so with both pids. The "Launch"
  button's running-aware behaviour (issue #95) is unchanged and now finds the relaunched app.

### 2.4 `AgentEyes.App` - AutoUpdate and the planned stop (src/AgentEyes.App)

- `UpdateChecker.cs` (rewritten): the app NEVER replaces its own files. Plan against the latest release;
  if behind, `UpdateRestartPolicy.Decide(sessionActive)`: `RestartNow` -> `HandOverToSetup(layout, version)`
  starts the INSTALLED `agenteyes-setup.exe update` (the CLI re-launches itself from a temp copy, asks this
  process to quit, replaces, relaunches with this process's own arguments); `DeferSessionActive` -> the
  version is remembered, the tray shows a balloon and an "Install update vX now (restarts AgentEyes)"
  item, and `OnSessionEnded` (wired to `RecordingStopped` + `PostRecording.WorkIdle`, issue #152) or the
  tray's `InstallNow` hands over later. A missing setup CLI THROWS with the fix (`FileNotFoundException`:
  run the setup once) - no in-process swap to fall back to. Gone: `RequestRestart`, `StartPendingRestart`,
  `StagedUpdate`, the in-process download/swap. Seam for tests: `StartSetup` (exe, args) -> pid.
- `App.xaml.cs`: after the tray exists, `QuitRequest.Listen(...)` -> `Dispatcher.BeginInvoke(tray.QuitRequested(...))`;
  disposed first thing in `OnExit`. `UpdateChecker.StartPendingRestart()` removed from `OnExit`.
- `TrayHost.cs`: `QuitRequested(why)` (internal): no recording -> `ShutdownNow()`; a recording in progress
  is stopped and KEPT through `RecordingStop.Keep` (raw files + manifest on disk within seconds) and the
  app exits WITHOUT awaiting the post-recording work, which `App.OnExit` logs as in flight and the recovery
  pass finishes on the very next start (issue #152) - the engine's grace period is bounded, so the exit must
  be quick. `NotifyUpdateStaged` -> `NotifyUpdateWaiting(version)`; "Restart to finish update" ->
  "Install update vX now (restarts AgentEyes)" -> `UpdateChecker.InstallNow()`.
- `AlwaysOnController.ShutdownForExit`: `engine.Stop(...)` -> `engine.StopForRestart("app exit - it comes
  back at the next start")`. It still never touches `Config.AlwaysOnEnabled`, which `RestoreOnStartup` reads.

### 2.5 `AgentEyes.Core` - always-on planned-stop persistence (src/AgentEyes.Core/AlwaysOn)

- `AlwaysOnEngine.StopForRestart(why)` (new, public): stop the recorder (ffmpeg finishes its piece),
  run an ORDINARY (not final) keeper pass - exactly issue #81's capture-restart pass: the open clip stays
  open, a piece past its tail waits for sound that may still come, a silent piece whose keep window has
  fully passed is deleted by the rule as on any pass - then `WriteHandover`: the open clip (id, first/last
  sound, last piece end, holding folder, start) + `ClosedSoundUtc` + `NextClip` + `SoundLog.Export(horizon)`
  to `handover.json`. A keeper pass that throws is logged and recorded and the handover is STILL written
  (that is what lets the next start decide the piece the pass could not). `Stop(why)` - the person switching
  always-on OFF - is unchanged: close and write the clip now.
- `AlwaysOnEngine.Recover`: `AlwaysOnHandover.Load`; when present, `RestoreHandover` restores `_open`,
  `_clipDirs`/`_clipStartsUtc`, `_closedSoundUtc`, `_nextClip` and imports the sound log; the carried
  clip's holding folder is NOT joined; loose pieces are NOT deleted (they wait for the keeper, which has
  their sound log). The file is consumed (deleted); an unreadable one is set aside as `handover.json.bad`.
  Without a handover the pre-#86 behaviour is untouched (join whole, delete loose pieces and say so).
  A handover naming a holding folder that is gone drops the clip with a warning and still imports the sound.
- `AlwaysOnHandover.cs` (new): the JSON record + `Load` (null when absent or unreadable, logged) + atomic `Save`.
- `SoundLog.Export(fromUtc)` / `Import(SoundLogState)` and the `SoundLogState(LoudSeconds, SoundSeconds, LastSound)` record.
- `AlwaysOnOptions.HandoverFile` (`<work>\handover.json`).
- The class summary documents PLANNED STOPS next to CRASH SAFETY.

### 2.6 Docs and tests

- `docs/installer-spec.md`: the CLI row and the tray-app paragraph describe the cycle, the quit request,
  the AutoUpdate handover and the always-on handover.
- `tests/AgentEyes.Tests/ManifestWriterIlTests.cs`: the pinned file-write inventory gains the handover's
  writes (`AlwaysOnHandover::Save` Move/WriteAllText, `Recover` Delete x2 / Move x1). Nothing else in the
  pinned set moved.
- New test classes: `UpdateRestartCycleTests` (27), `SetupCliRestartTests` (7), `AlwaysOnRestartTests` (13),
  `UpdateHandoverTests` (10) - section 3 maps them to the criteria.

## 3. Acceptance criteria -> how each is met -> how QA verifies

| Criterion | How the change satisfies it | Test(s) | How QA verifies |
|-----------|-----------------------------|---------|-----------------|
| App running -> `update` stops it (graceful then bounded force, existing `RunningApp`), replaces files, relaunches it; output says `restarted the running app (pid A -> pid B)` | `UpdateRestartCycle.RunAsync` order find -> stop -> replace -> launch; `RunningApp.StopAndWait` (quit request + `CloseMainWindow`, then `Kill(entireProcessTree)`, confirmed); `Commands.PrintRun` prints `AppRestartReport.Describe()` | `UpdateRestartCycleTests.RunAsync_AppRunning_StopsIt_ThenReplaces_ThenStartsItAgainWithTheSameArguments` (journal is exactly `find, stop 4242, replace, launch`; `Describe()` is the exact line), `RunAsync_StopSucceeds_TheRealSwapperReplacesTheFile_AndTheAppIsStartedFromTheSamePath`; the CLI's REAL entry point in-process: `SetupCliRestartTests.Update_AppRunning_PrintsRestartedTheRunningAppWithBothPids_AndRelaunchesWithTheSameArguments` (stdout contains `restarted the running app (pid 13680 -> pid 22104)`, the file is the new build, the launcher got the same exe and `--tray`), `Update_AppRunning_JsonCarriesTheRestart`, `Install_AppRunning_TakesTheSamePath_AndPrintsTheSameLine`; the force phase: `StopAndWait_QuitRequestDeliveredButIgnored_StillForceStopsAndConfirms` (a real `cmd /c pause` child) | `dotnet test --filter "FullyQualifiedName~UpdateRestartCycleTests\|FullyQualifiedName~SetupCliRestartTests"`; read `Commands.UpdateAsync` + `UpdateRestartCycle.RunAsync`; M1 in `mutation-evidence.txt` |
| Same `--tray` state after the relaunch | the instance's argument list is read from the running process (WMI + `CommandLineToArgvW` + `ToCarryAcrossRestart`) and handed to the launcher verbatim | `Describe_TrayApp_CarriesTheTrayFlagAndDropsTheExePath`, `Describe_WindowedApp_CarriesNoArguments`, `SplitCommandLine_FollowsTheWindowsRules` (3 lines), `RunAsync_EveryArgumentIsCarriedVerbatim_NotJustTheTrayFlag`, `WmiCommandLine_ThisProcess_ReturnsItsOwnCommandLine` (the live WMI read, against the test host itself), `Describe_CommandLineUnreadable_ThrowsNamingThePid_SoNothingIsGuessed` (null / "" / "  " - no fallback) | same filter; live: section 7 step 5 |
| App not running -> not started | `Find()` null -> replace only, launcher never called; report says so | `RunAsync_AppNotRunning_ReplacesAndStartsNothing`, `SetupCliRestartTests.Update_AppNotRunning_SaysSoAndStartsNothing`; also `Update_DryRun_NeverLooksForTheRunningApp`, `Update_NothingToDo_LeavesTheRunningAppAlone` | same filter |
| App cannot be stopped -> the update FAILS with a clear error BEFORE any file is replaced; old version intact | `StopAsync` false -> `AppStopFailedException` thrown before `replace` runs; the CLI prints `ERROR: AgentEyes (pid N) is running and could not be stopped. Nothing was replaced - ...` and exits 1 | `RunAsync_AppCannotBeStopped_ThrowsBeforeTheReplaceStep_AndStartsNothing`, `RunAsync_AppCannotBeStopped_TheInstalledFileIsByteForByteUnchanged` (real file + real `InstallSwapper`: bytes equal, no `.old`, staged file untouched), `SetupCliRestartTests.Update_AppCannotBeStopped_FailsWithTheReason_AndTheOldBuildIsUntouched` (exit 1, stderr text, bytes equal, no `.old`, nothing launched) | same filter; M1 shows all three FAIL when the throw is removed |
| Always-on on before is on after (restore-on-start) | `ShutdownForExit` never touches `Config.AlwaysOnEnabled`; `RestoreOnStartup` (`App.xaml.cs` line `_alwaysOn.RestoreOnStartup()`, unchanged) starts it when the flag is true | `UpdateHandoverTests.ShutdownForExit_HandsTheClipOverAndLeavesAlwaysOnEnabled_SoRestoreOnStartupBringsItBack` (flag still true after the exit path, engine Off, handover written), `RestoreOnStartup_AlwaysOnWasOff_DoesNotStartIt`; the RESTORED run's continuity is `AlwaysOnRestartTests` (next row). The "on" arm of `RestoreOnStartup` itself resolves the chosen recording setup and monitor (`BuildOptions`) and is not unit-tested - it is the live check in section 7 step 6 | `dotnet test --filter FullyQualifiedName~UpdateHandoverTests`; live: `GET /always-on` state after the relaunch |
| Planned stop -> the open clip is kept whole and CONTINUED if sound resumes within the silence gap; a planned restart never deletes a piece (tester's addition) | `StopForRestart` + `WriteHandover` + `Recover`/`RestoreHandover`; the #81 restart bridge carries the clip across the downtime | `AlwaysOnRestartTests.StopForRestart_ThenStart_SoundResumesWithinTheGap_OneClipAcrossTheRestart_AndNoPieceIsDeleted` (ONE 14 s clip from seven pieces across two engine instances; zero `DELETE` history events; `RestartsToday` 0), `StopForRestart_ThenStartAfterLongerThanTheGap_TheCarriedClipIsWrittenWithItsSpan_AndNewSpeechStartsANewClip`, `StopForRestart_WritesTheOpenClipAndTheSoundHeard`, `StopForRestart_NoClipOpen_LoosePiecesWaitForTheKeeper_AndAreJudgedByTheRuleNotByRecover`, `Start_NoHandover_RecoversAsFromACrash`, `Start_HandoverNamesAMissingClipFolder_...`, `Start_CorruptHandover_IsSetAsideAndTheStartRecoversAsFromACrash`, `StopForRestart_KeeperPassFails_TheHandoverIsStillWritten_AndTheFailureIsOnRecord`, `StopForRestart_WhenOff_IsANoOp`, `SoundLog_ExportThenImport_...`, `SoundLog_Export_KeepsOnlySecondsFromTheHorizonOn` | `dotnet test --filter FullyQualifiedName~AlwaysOnRestartTests`; M2 shows the three behavioural tests FAIL with the pre-#86 Recover; live: section 7 step 7 |
| AutoUpdate inside the app follows the same path | `UpdateChecker` hands over to the installed `agenteyes-setup.exe update`; never calls a replacer | `UpdateHandoverTests.HandOverToSetup_StartsTheInstalledSetupCliWithUpdate` (exe = `layout.PathFor(SetupCli)`, args = `["update"]`), `HandOverToSetup_SetupCliNotInstalled_ThrowsAndStartsNothing`, `UpdateChecker_CallsTheEnginesPlannerAndLayout_ButNeverAFileReplacer` (IL scan of the built AgentEyesApp.dll: presence arm = the updater's planner/policy/layout calls are seen; absence arm = no call anywhere in the app assembly into `InstallSwapper`, `UpdateRunner`, `Orchestrator`, `ArchiveInstaller`); `UpdateRestartPolicyTests` (unchanged, still the defer decision) | same filter; live: section 7 step 8 |
| Unit tests for the decision; `dotnet build` clean, `dotnet test` green | - | 57 new tests | `dotnet build AgentEyes.sln -c Release` -> `0 Error(s)`; `dotnet test AgentEyes.sln -c Release` -> `Passed: 2041, Failed: 0` |
| Live: update 1.11.x -> next release while the app runs; pid changes; `/health` shows the new version | - | PENDING TESTER | section 7 |

## 4. Assumptions (flagged, not stated as fact)

1. **The graceful stop is a named event, not the REST API.** The API can be off (`ApiEnabled`) and its
   port is configurable; the event `AgentEyes-quit-<pid>` needs neither and targets exactly the found pid.
2. **The relaunch arguments come from the running process (WMI).** Neither the Run key (`--tray`) nor the
   Start Menu shortcut (no arguments) knows how the person actually started the app. Reading the process
   is the only honest source; the price is the `System.Management` package in the engine.
3. **A quit request during a normal recording stops and KEEPS the recording but does not await its
   post-processing** (mux, transcript, title can take minutes; the engine force-stops after 30 s). Issue
   #152's recovery pass finishes that work on the next start - which the relaunch triggers at once. The
   tray's own Quit still awaits it, as before. The app's AutoUpdate never gets here: it defers while a
   session is active (issue #107).
4. **`agenteyes-setup update` started by the app survives the app's stop.** The installed setup exe
   re-launches itself from a temp copy and exits (existing behaviour); by the time the temp copy stops the
   app, the app is not its parent, so a `Kill(entireProcessTree)` of the app does not reach it. Not
   provable with doubles; the live check (section 7 step 8) is where this shows.
5. **The Pause path (a normal recording takes the screen) still closes the open clip** at the pause, as
   before. Only the two PLANNED stops named by the tester - exit and update - hand over.
6. **A clip open at Quit is written at the NEXT start, not at Quit.** This follows from the tester's
   request ("on a planned stop (update/exit) ... continued if sound resumes within the silence gap"): the
   engine cannot know at exit whether the next start is in 30 s (an update) or tomorrow. At the next
   start a clip whose gap has passed is closed with its proper span (better than today's untrimmed
   whole join) and any speech starts a new clip. If the owner wants Quit (as opposed to an update) to
   write the clip immediately, that is one line in `AlwaysOnController.ShutdownForExit` and a new issue.
7. **The first update FROM 1.11.x is stopped the old way.** A 1.11.x app does not listen for the quit
   request, so the new engine logs `quit request delivered=False` and force-stops it after the legacy
   3 s; its always-on clip is recovered as from a crash (that first time only). From the next release on,
   the app listens and the clip is carried. Also: a 1.11.x app with AutoUpdate ON will, at its own start,
   find the new release and take ITS OLD in-place path (swap + self-restart) before the tester gets to run
   the CLI - the tester should switch AutoUpdate off on the 1.11.x app first (Settings) or run the update
   before the 4 s auto-check fires.

## 5. CenCon impact

No drift of the component map; no change to the privacy posture (visible, controllable): the app is
stopped and started again with the same visibility flags it had, and the update tells the person it did
so. `docs/installer-spec.md` updated (section 2.6). No `docs/cencon/` file changes.

## 6. Verification record

- Baseline on `main` (533d453), before any change: `Passed: 1984, Failed: 0`.
- Final tree: `dotnet build AgentEyes.sln -c Release` -> `0 Error(s)`, `Build succeeded.`;
  `dotnet test AgentEyes.sln -c Release --no-build` -> `Passed! - Failed: 0, Passed: 2041, Skipped: 0, Total: 2041`.
- The four new classes alone -> `Passed: 57, Failed: 0`.
- Mutation evidence: `mutation-evidence.txt` (M1: 3 fail / 1 control passes; M2: 3 fail / 10 pass).
- One trap hit and recorded: rebuilding `AgentEyes.Tests.csproj` on its own lands in `bin\Release\`
  (not the solution's `bin\x64\Release\`), so a `dotnet test --no-build` afterwards ran a STALE binary
  and reported three failures that did not exist. CLAUDE.md warns about exactly this. The stray
  `bin\Release` was deleted and every gate run here was a full `dotnet build AgentEyes.sln`.
- Scans over every changed file: zero non-ASCII characters; zero hits for the banned attribution words.

## 7. PENDING TESTER - the live procedure (owner's laptop, after merge)

The developer did not run any binary. The tester does this once the next release exists.

Preconditions: AgentEyes 1.11.x installed and running from the tray with always-on ON. Switch
**Settings -> AutoUpdate OFF** on this 1.11.x app first (assumption 7) - or accept that the old app may
update itself the old way at its next start.

1. Note the running pid and version:
   `Get-Process AgentEyesApp | Select-Object Id, StartTime` and `curl http://127.0.0.1:7882/version`.
   Speak for ~10 s so a clip is in progress (`curl http://127.0.0.1:7882/always-on` -> state `keeping`).
2. Publish the next release (the normal `scripts\new-release.ps1` / `build-release.ps1` step). Download
   the NEW `agenteyes-setup-cli-win-x64.exe` from that release to a folder OUTSIDE `%LOCALAPPDATA%\AgentEyes\app`
   (the installed 1.11.x CLI still has the old update code).
3. Run it: `.\agenteyes-setup-cli-win-x64.exe update`.
   EXPECTED console (proof target): `Update complete:` ... `installed=0 updated=N failed=0` and then
   `restarted the running app (pid <old> -> pid <new>)`. The old app is force-stopped after ~3 s (a 1.11.x app
   does not listen), so `%LOCALAPPDATA%\AgentEyes\logs\setup-cli.log` shows
   `[RunningApp] StopAndWait: quit request delivered=False; waiting up to 3s for a clean exit` then
   `force-killing pid=<old>`, then `[UpdateRestartCycle] RunAsync: restarted the running app (pid <old> -> pid <new>)`.
4. `Get-Process AgentEyesApp` -> exactly one process, Id = `<new>`; `curl http://127.0.0.1:7882/version`
   -> the new version. `/health` -> `ok: true`.
5. Same `--tray` state: the relaunched app shows no window if the old one was started with `--tray`
   (setup-cli.log: `[RunningAppHandle] Find: pid <old>, exe ..., arguments: --tray` and
   `[ProcessAppLauncher] Launch: started ... as pid <new>`).
6. Always-on on before -> on after: `curl http://127.0.0.1:7882/always-on` -> state `listening` or `keeping`;
   the app log has `[AlwaysOnController] RestoreOnStartup: always-on was on - starting it again`.
7. Clip continuity - needs TWO builds that both listen, i.e. run a SECOND update from the new version to
   the one after (or to a `--release-dir` re-stamped build). With a clip in progress (speak ~10 s), run
   `agenteyes-setup update` (the installed one is now the new CLI). EXPECTED: setup-cli.log
   `quit request delivered=True; waiting up to 30s`; the app log (old pid) `[TrayHost] QuitRequested: the
   setup engine asked for a planned stop`, `[AlwaysOnEngine] StopForRestart: stopping always-on for a
   restart (app exit - it comes back at the next start)`, `[AlwaysOnEngine] WriteHandover: ...handover.json -
   the clip in progress (clip_...) stays open; N piece(s) wait ...`; the app log (new pid)
   `[AlwaysOnEngine] RestoreHandover: continuing after the planned stop at HH:mm:ss (...) - the clip in
   progress (clip_...) stays open and continues if sound resumes within 5 min`. Speak again within 5 min;
   within a minute the log/History tab shows `clip_... continues across a capture restart - a gap of Ns
   ... has no piece; the clip is not split there`. Stay quiet 5 min: ONE clip is written that spans both
   sides; nothing in the log says `Recover: deleting`. `GET /always-on/history` carries the same lines.
8. AutoUpdate path: on the new build with AutoUpdate ON and a newer release published, ~4 s after start the
   app log shows `update: vX available and no session is active - handing over to the setup engine now.`
   and `update: vX handed over to "...agenteyes-setup.exe" update (pid N)`; the app then leaves through
   `QuitRequested` and comes back on the new build (steps 3-6 apply). With a recording in progress instead:
   the balloon "AgentEyes update ready" and the tray item "Install update vX now (restarts AgentEyes)";
   stopping the recording (and its post-processing finishing) triggers the handover.

Reminders for QA: the focus-free layers are REST (`127.0.0.1:7882`), UIA and PrintWindow; never
force-foreground the app and synthesize input without warning the human; the recording HUD is
capture-excluded, so HUD/recording state is asserted via UIA or `/status`, not a screen grab. The
update itself is audible-free but it DOES stop and restart the running app - run it when the owner is
not recording.
