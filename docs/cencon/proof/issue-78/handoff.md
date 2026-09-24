# Issue #78 - Developer handoff

**Issue:** [Tests] Test runs write into the live app's log and state, hiding real always-on problems
**Branch:** `issue-78-test-log-isolation`
**Gate:** `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)`;
`dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1742` (see "Test runs" below
for every run, including the pre-existing CameraPreviewTests flake).

I believe this is finished, except for criterion 5's live check, which is DEFERRED to the owner's go
(the owner forbade live/runtime checks for this issue because their app is running).

## What changed

| Area | Change |
|------|--------|
| `AgentEyes.Core/AppDataPaths.cs` (new) | The ONE place the product asks Windows for `%LOCALAPPDATA%` and `Videos`. `Root` = `%LOCALAPPDATA%\AgentEyes`, `RecordingsRoot` = `Videos\AgentEyes`. `RedirectForThisProcess(local, videos)` moves both for a test host. It is fail-closed: it throws if any path was handed out before it (something may hold the real path), if called twice, or with a relative path. The product never calls it. |
| Every state path | Log, config.json (App + Core reader), presets.json, plugins, always-on work folder (today.json, pieces), always-on clips, preview frames, RNNoise + Whisper models, dictionary.json, DevThrottle credential, recordings, and the qa-record storage migration are all built from `AppDataPaths`. `FfmpegLocator`'s winget lookup uses `AppDataPaths.MachineLocalAppData` (a tool location, never redirected). |
| `AgentEyes.Core/Log.cs` | Each line is now `HH:mm:ss.fff [pid N] [LEVEL] message` (pure `FormatLine`). Nothing in the repo parses the old shape (checked: src, scripts, clients, plugins, tests). |
| `tests/.../TestRunIsolation.cs` (new) | A `[ModuleInitializer]` that, before any test runs, redirects both roots to `%TEMP%\agenteyes-tests\run-yyyyMMdd-HHmmss-<pid>\{LocalAppData,Videos}`, sets `AGENTEYES_ROOT` for this process only (so `InstallLayout.Default()` lands there too), and swaps the setup engine's user-environment store for an in-memory one. Run folders older than 2 days are pruned - only folders whose NAME parses as this host's own `run-<stamp>-<pid>` shape. |
| `Setup.Engine/UserEnvironment.cs` (new) + `InstallFinalizer.cs` | The user PATH and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` now go through `IUserEnvironment` (`RegistryUserEnvironment` in the product - same calls as before). `SetupEngineTests` used to rewrite the REAL `HKCU\Environment\DOTNET_BUNDLE_EXTRACT_BASE_DIR` (set to a temp dir, then removed, restored in `finally`) - see finding 6. |
| `tests/.../TestIsolationTests.cs` (new) + `CompiledCode.cs` | The guards (below). Runs in its own non-parallel collection (see "Test runs"). |

## Acceptance criteria

### AC1 - During `dotnet test`, all logging goes to a per-run test directory; a test asserts the real log path is not used

- **Implemented:** `Log.Dir` = `AppDataPaths.Root\logs`; the module initializer redirects the root before
  any test code runs.
- **Test:** `TestIsolationTests.LogFile_InATestRun_IsInThisRunsFolderAndNotTheRealLog` - asserts the
  redirect is active, `Log.Dir` equals `<run>\LocalAppData\AgentEyes\logs`, the file is under the run
  folder (itself under `%TEMP%`) and NOT under the real `%LOCALAPPDATA%\AgentEyes`, and that a line
  logged now is actually IN that file (a presence, not just a path string).
- **Evidence from this session (a read-only look at the owner's real log by the agent, not by any
  test):** I ran the full suite 13 times (plus 3 filtered runs and 5 mutation runs) between 10:14 and
  10:55 on 2026-09-24. In `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-20260924.log`, the window
  10:14-10:57 holds exactly three lines, all the live app's own timer:
  `10:26:01.774 [INFO] [RepairService] RunAsync: trigger=timer - recording in progress; skipped`
  (and the same at 10:41:01.796 and 10:56:01.782). The file has 0 `[pid ` lines (the installed build
  predates the pid) and 0 hits for `TestIsolation|agenteyes-tests|RealFolderDecoys`. The newest
  test-shaped line in it (`setup="test"|fake ffmpeg|stop-throws|then-retry|abandon-report|
  agenteyes-alwayson-|agenteyes-stop-|agenteyes-test`) is `09:26:17.627` - from test runs made before
  this branch existed.
- **QA:** run `dotnet test AgentEyes.sln -c Release`, then confirm the newest
  `%TEMP%\agenteyes-tests\run-*\LocalAppData\AgentEyes\logs\AgentEyes-<date>.log` holds the run's lines,
  and that the real log gained no test lines during the run (e.g. no `[pid <testhost pid>]`, no
  `TestIsolationTests` marker).

### AC2 - No test reads or writes the real `%LOCALAPPDATA%\AgentEyes`, verified by a guard test

- **Guards (all in `TestIsolationTests`):**
  - `EveryStateLocation_InATestRun_IsInThisRunsFolderAndNotTheRealOne` - 16 named locations (log,
    roots, recordings, always-on work/clips/today.json, preview, dictionary, Whisper model, plugins,
    `InstallLayout.Default()`, and by reflection the private `Config.FilePath`, `PresetStore.FilePath`,
    `LocalAppConfig.FilePath`, `DevThrottleAccount.CredPath`) must be inside the run folder and outside
    the real one. A renamed private member THROWS instead of being skipped.
  - `FolderLookups_InTheCompiledProductAndTests_AreExactlyThePinnedOnes` - IL inventory of every
    `Environment.GetFolderPath` call in agenteyes.dll, AgentEyesApp.dll, AgentEyes.Setup.Engine.dll AND
    AgentEyes.Tests.dll, by method and decoded folder. A non-constant argument or the two-argument
    overload is reported as such, never guessed.
  - `FolderLookupScanner_OnTheCompiledDecoys_ReportsBothShapes` - instrument check: two compiled,
    never-called decoys (a direct LocalApplicationData lookup and one with the folder in a variable)
    must both be reported.
  - `EnvironmentVariableRoutes_ToTheUserFolders_AreNotInTheProduct` - IL inventory of every
    `GetEnvironmentVariable` read in the product by the variable it names (none is LOCALAPPDATA /
    APPDATA / USERPROFILE), no `ExpandEnvironmentVariables` call, and the only known-folder P/Invoke is
    the pinned `shell32!SHGetKnownFolderPath` (CaptureService, FOLDERID_Screenshots - not AgentEyes state).
  - `InstallFinalizer_InATestRun_WritesTheUserEnvironmentInMemoryNotTheRegistry`.
  - `RedirectForThisProcess_WhenAlreadyRedirected_Throws`, `..._RelativePath_Throws`.
- **Stated limits** (also in the test class comment): reflection and run-time-assembled strings are
  invisible to any IL scan; `%TEMP%` is `%LOCALAPPDATA%\Temp` on a default profile and is deliberately
  not guarded (it is where every test's scratch lives); out-of-process children are not scanned; the
  setup wizard/CLI projects are not referenced by the tests and not scanned; read-only probes of
  non-AgentEyes machine state (devices, monitors, Start Menu/Desktop shortcut existence, the Run key)
  are outside this criterion.
- **Mutation evidence:** `docs/cencon/proof/issue-78/mutation-evidence.txt` - five mutations, each fires:
  M1 a state path bypassing `AppDataPaths` (inventory + location guard fail), M2 pid dropped,
  M3 `GetEnvironmentVariable("LOCALAPPDATA")`, M4 user-env store not swapped, M5 a path resolved before
  the redirect (module initializer throws; every test in the assembly fails). Mutations were run with
  `--filter TestIsolationTests` only, so a broken isolation could never write into the live folder.

### AC3 - Each log line includes the process id

- **Implemented:** `Log.FormatLine` -> `09:03:01.250 [pid 1111] [INFO] message`.
- **Tests:** `Log_EachLineWritten_CarriesTheWritingProcessId` (writes a real line, finds it in the file,
  parses `[pid N]`, asserts N == `Environment.ProcessId`); `FormatLine_TwoProcesses_AreToldApart`.
- **QA:** any line of the per-run test log; after deploy, the app's log.

### AC4 - Investigate whether a test run can disturb a running AgentEyes; fix anything found

| # | Shared thing | Finding | Action |
|---|--------------|---------|--------|
| 1 | The day log | Shared - every test's `Log.*` appended to the live `AgentEyes-<date>.log`. | FIXED (AC1). |
| 2 | `%LOCALAPPDATA%\AgentEyes` state | Reached by tests: `LocalAppConfig` read the live `config.json` (`DecayLadderTests`), `RnnoiseModel.Ensure` would (re)write `models\bd.rnnn`, `DictionaryStore.Load()` seeds `dictionary.json` if absent, `PreviewTap.TryCreate` publishes to the `preview` folder the live HUD reads, `AlwaysOnController.BuildOptions` produced options on the live `alwayson` work folder (today.json). | FIXED - all redirected. |
| 3 | `Videos\AgentEyes` | `RecordingServiceTests` counted folders in the REAL recordings root before/after (a live recording starting in between would fail the test; and the test was reading the user's data). | FIXED - redirected (`RecordingsRoot`). |
| 4 | Named pipes | The only named pipe is always-on's `agenteyes-alwayson-<pid>-<counter>` - the process id is in the name, so a test host and the app can never collide. | None needed. |
| 5 | Killing ffmpeg / the app | Product code kills only processes it started and holds a handle to; `KillOnCloseJob` is an UNNAMED job holding only this process's children. Nothing enumerates processes by name except `RunningApp` (setup engine), which tests only call with injected providers that return the test's own `cmd.exe` child. No test starts a real capture: ffmpeg runs in tests synthesize media from `lavfi` sources; `RecordingServiceTests` throws on a bogus mic before any ffmpeg starts; camera tests use fakes. | None needed. |
| 6 | HKCU user environment | `SetupEngineTests` SET the real `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a temp dir and then REMOVED it (restored in `finally`). The installed app depends on that variable (issue #120); a test run that died between remove and restore left the installed app extracting into `%TEMP%` again. | FIXED - `IUserEnvironment`; tests use an in-memory store. |
| 7 | Single-instance mutex `AgentEyes-singleinstance`, REST port 7882, DevThrottle sign-in listener | Only created by `App.OnStartup` / `RestServer` / an interactive sign-in; no test runs any of them. | None needed. |
| 8 | "no new piece since 09:01:45" | Not established. Nothing above can stop the live ffmpeg or touch its piece folder; machine-level load (CPU/disk from a 1742-test run with ffmpeg encodes) is the only remaining shared resource and cannot be ruled out statically. | Stated, not claimed. |

### AC5 - Full suite while the installed app runs always-on: app log has no test lines, always-on uninterrupted (GET /always-on before and after)

- **DEFERRED to the owner's go.** Live `/always-on` calls were forbidden for this implementation.
- The log half has read-only evidence from this session (AC1 above: 13 full runs, zero test lines in
  the live log).
- **QA / owner steps:** `GET http://127.0.0.1:7882/always-on` (save it) -> `dotnet test AgentEyes.sln -c Release`
  on this branch -> `GET /always-on` again. Expected: state still `listening`/`keeping` (not `retrying`/
  `off`), the same session start, and pieces/clips counters that only grew; the live log for the run's time window contains only the app's own lines (none with the testhost's pid, none mentioning
  `setup="test"`, `fake ffmpeg`, `agenteyes-tests`).

### AC6 - `dotnet build` clean and `dotnet test` green

- `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.` / `0 Error(s)` (19 warnings, all
  pre-existing; none in files this branch touches).
- `dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1742, Skipped: 0, Total: 1742`.

## Test runs (every full run, quoted)

`CameraPreviewTests` is a documented pre-existing timing flake on this laptop (issue-70 QA report,
issue-72 handoff, issue-75 QA report: its `WaitForSession` waits 5s for a `Task.Run` to start, and
thread-pool starvation under the parallel suite pushes it past that). With `TestIsolationTests` in the
parallel mix CameraPreviewTests failed far more often (in 7 of 9 full runs, e.g.
`Failed: 3, Passed: 1739`; the only other failures in those runs were this class's own first-draft
pins, fixed before commit); without that class, 3 of 3 runs were `Failed: 0, Passed: 1730`. The class's
IL inventories read four whole assemblies, so it now runs in a non-parallel collection
(`TestIsolationCollection`), after the parallel part. After that change: 3 of 4 runs
`Passed! - Failed: 0, Passed: 1742`; the fourth failed 4 CameraPreviewTests only - the pre-existing
rate (#75 saw 1 in 4). The per-run log of a failing run shows the cause: `[CameraPreviewController]
Select` at 10:26:21.297 and the open never starting before the 5000 ms stop timeout at 10:26:31.326.
Recommended follow-up (not this issue): make `CameraPreviewTests` independent of thread-pool latency.

## Smokes

None run (owner constraint: no app launch, no scripts). A deploy + AC5 is the only live check this
change needs; no api/gui smoke is required beyond it - no UI, route or capture behaviour changed.

## CenCon impact

No drift to `docs/cencon/`. Privacy posture (visible / controllable): unchanged - the product still
stores everything in the same folders; the redirect exists only for a test host and is never called
by the product. Component map: one new Core type (`AppDataPaths`) and one new Setup.Engine seam
(`IUserEnvironment`).

## Reminders for QA

- Focus-free layers are REST (127.0.0.1:7882), UIA, PrintWindow. Never force-foreground + synthesize
  input without warning the human.
- The recording HUD is capture-excluded; assert HUD/recording state via UIA or `/status`, not a grab.
- Run the suite from the `bin\x64\Release` build of this branch; a stale `bin\Release` binary tests
  other code.
