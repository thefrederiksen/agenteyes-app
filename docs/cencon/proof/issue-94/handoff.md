# Issue #94 - The app relaunched by update inherits the updater's output handles - Developer handoff

Branch: `issue-94-detached-relaunch` (from `main` at 8dd30e3, v1.11.6). PR #95. Tracker: thefrederiksen/agenteyes-app.

I believe this is finished, with ONE criterion deliberately left to the tester session (criterion 5, the
live `$o = & agenteyes-setup update 2>&1`): the developer ran no AgentEyes binary at all - not
AgentEyesApp.exe, agenteyes.exe, agenteyes-setup.exe, nor any smoke or selftest. The gate is
`dotnet build` + `dotnet test`, and every new check was fired at the restored pre-#94 launcher and
shown to fail there (`mutation-evidence.txt`).

Files in this folder:

| File | What it is |
|------|-----------|
| `handoff.md` | this note |
| `mutation-evidence.txt` | the `Launch_*` tests run against the pre-#94 `Process.Start` launcher restored in place: the two pipe checks FAIL after their full bound with the child alive (M1, M2); then M4, the working-directory regression test fired at the first version's line; quoted output, each mutant reverted and the tree re-gated |

Gate on the final tree (after the review fix pass, section 8): `dotnet build AgentEyes.sln -c Release` ->
`Build succeeded.`, `0 Error(s)`; `dotnet test AgentEyes.sln -c Release` ->
`Passed! - Failed: 0, Passed: 2095, Skipped: 0, Total: 2095` (2079 on `main`, +16), including one full
run at `--logger "console;verbosity=normal"` on the final binaries: 2095 `Passed` lines, zero `Failed`
lines, exit 0 (section 5). The new class alone (`--filter FullyQualifiedName~ProcessAppLauncherTests`):
`Passed: 16, Failed: 0, Duration: 859 ms`.

---

## 1. Root cause (confirmed in the code)

`ProcessAppLauncher.Launch` (then in `tools/AgentEyes.Setup.Engine/RunningAppHandle.cs`) started the
installed app with `Process.Start(new ProcessStartInfo { UseShellExecute = false, ... })`. .NET's
`Process.Start` on Windows calls `CreateProcess` with `bInheritHandles = TRUE` unconditionally, and
without `STARTF_USESTDHANDLES` when nothing is redirected. So the relaunched AgentEyesApp.exe received a
copy of every inheritable handle the updater held - and the stdout/stderr handles a script gives the
updater when it captures its output (PowerShell's `& agenteyes-setup.exe update 2>&1`) are inheritable
pipe ends. The pipe therefore stayed open for as long as the app ran, and the capturing caller never saw
end-of-stream: exactly the 2026-09-24 23:38 observation (exit=0 logged, the caller hung).

`UseShellExecute = true` (the issue's first suggestion) would have avoided the inheritance but was not
usable: .NET throws when `ProcessStartInfo.Environment` is used with it, and the launcher must set
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` explicitly for the app (the CLI's temp copy drops that variable from
its own environment; an app inheriting that would unpack native DLLs into %TEMP%, issue #120).

## 2. What changed, per project

### 2.1 `AgentEyes.Setup.Engine` (tools/AgentEyes.Setup.Engine)

- `ProcessAppLauncher.cs` (new file; the class moved out of `RunningAppHandle.cs`, which lost it and
  nothing else). `Launch(exePath, arguments)` keeps its signature and its checks (empty path, missing
  exe -> `FileNotFoundException` before anything starts) and now:
  1. builds ONE command line with `BuildCommandLine(exePath, arguments)` (public, pure): the exe quoted,
     each argument quoted by the C runtime's rules (spaces/tabs/quotes/trailing backslashes), so the
     child's own argv split returns the arguments verbatim;
  2. builds the child's environment: this process's variables plus `DOTNET_BUNDLE_EXTRACT_BASE_DIR` =
     `layout.BundleExtractDir` (unchanged intent from #86/#120);
  3. calls `CreateProcessW` with `bInheritHandles = FALSE`, a `STARTUPINFOW` with `dwFlags = 0` (no
     `STARTF_USESTDHANDLES`, no std handles), `CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW`, and the
     exe's directory as the working directory (from the exe's FULL path: for a bare name `GetDirectoryName`
     is `""`, which CreateProcessW rejects - review finding, section 8; of a full path it is null only for a
     drive root, which no file can be, so that throws `InvalidOperationException` - no fallback, section 9). `CREATE_NO_WINDOW` gives a console child a hidden console
     of its own instead of the updater's; it is ignored for a GUI exe such as AgentEyesApp.exe. Both
     returned handles are closed at once; the pid is returned.
  On failure `CreateProcess`'s error is thrown as a `Win32Exception` whose message names the command
  line and the Windows reason; `UpdateRestartCycle.Relaunch` wraps it in `AppRelaunchFailedException` as
  before. Entry, the exact command line with the working directory, the pid and every failure are logged
  through `EngineLog`; each validation throw in `Launch` (empty path, null arguments, missing exe) and in
  `BuildCommandLine` writes its reason before throwing.
- `UpdateRestartCycle.cs`: unchanged - it still hands the launcher the stopped instance's own argument
  list (`--tray` comes back as `--tray`, pinned by the existing `UpdateRestartCycleTests`).

### 2.2 `AgentEyes.Tests` (tests/AgentEyes.Tests)

- `ProcessAppLauncherTests.cs` (new, 16 tests, section 3), in a NON-PARALLEL xunit collection
  (`ProcessSpawningCollection`, `DisableParallelization = true`) so it runs alone after every parallel
  collection: one test holds an INHERITABLE pipe end in the process during a launch, which any concurrent
  `Process.Start` elsewhere in the suite (bInheritHandles=TRUE) would hand to ITS child - a false failure;
  and the environment test snapshots the whole process environment while `PluginRegistryChannelTests`
  may be poisoning `PSModulePath` (that class's "no other test spawns a PowerShell" comment is updated). Every child is `cmd.exe /c pause --tray`
  started HIDDEN through the real launcher (it blocks on its own hidden console's stdin, so it lives
  until killed), or a one-shot `powershell.exe` that writes a file and exits. Each test records its
  children and `Dispose` kills them (`Kill(entireProcessTree: true)`); no AgentEyes binary is involved.
- `LaunchProbe.cs` (new) + `AgentEyes.Tests.csproj` (`<GenerateProgramFile>false</GenerateProgramFile>`):
  the test assembly's own `Main`. xunit never calls it; the out-of-process test runs
  `dotnet AgentEyes.Tests.dll --launch-probe <layoutRoot> <exe> [args]` as the UPDATER STAND-IN with its
  stdout and stderr piped, and the probe runs the real `ProcessAppLauncher`, prints `pid=N`, exits 0.
  (The test project emits no apphost exe, hence `dotnet <dll>`; `dotnet.exe` is resolved from the
  running shared runtime's directory and asserted to exist.)

## 3. Acceptance criteria -> what was implemented -> how QA verifies

| # | Criterion | Implemented by | How QA verifies |
|---|-----------|----------------|-----------------|
| 1 | The relaunched app is started with no inherited stdio handles, detached from the updater's console | `ProcessAppLauncher.StartDetached`: `CreateProcessW(..., bInheritHandles: false, CREATE_UNICODE_ENVIRONMENT \| CREATE_NO_WINDOW, ...)`, `STARTUPINFOW.dwFlags = 0` | Read `tools/AgentEyes.Setup.Engine/ProcessAppLauncher.cs` (`StartDetached`, the two constants, the struct). Run `Launch_ChildHoldsNoCopyOfAnInheritableHandle_ReadEndSeesEofWhileChildStillRuns`: an inheritable pipe end lives in the test process; after the launch the test drops its copy and the read end must hit EOF within 15 s while `Process.GetProcessById(pid).HasExited` is false. Against the old launcher it times out (`mutation-evidence.txt`, M2). |
| 2 | A test starts the launcher from a process whose stdout is a pipe and asserts the pipe closes when the updater exits while the relaunched child keeps running | `Launch_FromAProcessWhoseStdoutIsAPipe_ThePipeClosesWhenThatProcessExits_WhileTheChildKeepsRunning` + `LaunchProbe.Main` | Run that test (`--filter FullyQualifiedName~ProcessAppLauncherTests`). The probe process (stdout AND stderr redirected to pipes, like `2>&1`) prints `pid=N` and exits; the test asserts both pipes reach EOF within 15 s, the probe exited 0, and the child pid is still running at that moment. Against the old launcher it times out with "the child (pid N) holds a copy of them" (`mutation-evidence.txt`, M1). |
| 3 | The relaunched app still gets its original arguments (e.g. `--tray`) | `BuildCommandLine` (CRT quoting) + `CreateProcessW` given that line; `UpdateRestartCycle` unchanged | `Launch_ChildRunsWithExactlyTheArgumentsGiven_TrayIncluded` reads the child's real command line back (WMI `Win32_Process.CommandLine`, split by `CommandLineToArgvW` - the same read #86 uses to find the running app) and asserts argv == [cmd.exe, `/c`, `pause`, `--tray`]. `BuildCommandLine_RoundTripsThroughTheChildsArgvSplit` (8 cases: `--tray`, empty, spaces, embedded quotes, trailing backslash, tab) and `BuildCommandLine_NoArguments_IsTheQuotedExeAlone` pin the quoting. `Launch_ChildSeesTheParentEnvironmentPlusTheBundleVariable` proves the environment (parent's + the bundle variable) reaches the child. The cycle-level `--tray` carry is `UpdateRestartCycleTests.RunAsync_AppRunning_..._ThenStartsTheInstalledAppWithTheSameArguments` (unchanged). |
| 4 | `dotnet build` clean, `dotnet test` green | - | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded. 0 Error(s)`; `dotnet test AgentEyes.sln -c Release` -> `Passed: 2094, Failed: 0` on this branch. QA runs both itself. |
| 5 | Live (tester): `$o = & agenteyes-setup update 2>&1` returns within seconds of "exit=0" while the app keeps running | the change above | **PENDING TESTER** - not attempted by the developer (owner constraint: no AgentEyes binary is launched from this session). The tester session installs/updates from this branch's build and runs the command; expected: the assignment returns within seconds of the `exit=0` line in setup-cli.log, `agenteyes-setup status` / `/health` on 127.0.0.1:7882 shows the app running as the new pid, and the app started with its previous arguments (`--tray` app is still a tray app). |

Failure-shape tests, both new: `Launch_ExeMissing_ThrowsFileNotFound_BeforeAnythingIsStarted`,
`Launch_NotAnExecutable_ThrowsWithTheWindowsReason` (`Win32Exception` whose message contains
`CreateProcess failed` and the file name). Regression test from the review:
`Launch_BareExeNameInTheCurrentDirectory_StartsIt` (fails on the first version with error 123, M4).

## 4. What the tests can and cannot see (fail-closed statement)

- Both pipe checks wait for a specific PRESENCE (end-of-stream on a read end within a bound) and then
  assert the child is still alive at that moment. A child that dies early would make the pipe close for
  the wrong reason, which is why the liveness assertion is there; an empty/absent event is a timeout,
  never a pass.
- The children are console exes (cmd.exe, powershell.exe), never AgentEyesApp.exe. Handle inheritance
  is decided by `CreateProcess`'s `bInheritHandles`, which does not depend on the child's subsystem, and
  `CREATE_NO_WINDOW` is documented as ignored for a GUI exe; but the real app under a real
  `agenteyes-setup update` is only proven by criterion 5 (tester).
- `Launch_ChildRunsWithExactlyTheArgumentsGiven_TrayIncluded` depends on WMI answering for a process
  this test just started; the assertion fails loudly (not skips) when WMI returns nothing.

## 5. Mutation evidence (summary; full quoted output in `mutation-evidence.txt`)

The pre-#94 `Process.Start(UseShellExecute=false)` body was restored in place of the `StartDetached`
call, the solution rebuilt with the gate command, and the six `Launch_*` tests run:
M1 (probe/pipe) FAILED [15 s] "the probe's stdout/stderr pipes did not close within 00:00:15: the child
(pid 23792) holds a copy of them"; M2 (in-process pipe) FAILED [15 s] "the read end did not reach
end-of-stream within 00:00:15: the child (pid 4024) holds a copy of the pipe". The argument/environment
tests passed on the mutant too, as regression guards must (they pin what the fix preserves). The mutant
was then discarded (`git checkout --`, `git status` clean), the solution rebuilt and the whole suite
re-run. No `cmd.exe /c pause` or probe process was left running after either run (checked with
`Get-CimInstance Win32_Process`). M4 (review fix pass): the first version's working-directory line
restored in place -> `Launch_BareExeNameInTheCurrentDirectory_StartsIt` FAILED with
`Win32Exception: CreateProcess failed for cmd.exe /c pause: The filename, directory name, or volume label
syntax is incorrect.`; restored -> passes; full suite `Passed: 2095, Failed: 0`.

On the unnamed failure: the FIRST full-suite run after the first review fixes reported `Failed: 1,
Passed: 2094` with only the summary line captured. The reviewer (section 9, note 6) asked for a NAMED
run: `dotnet test AgentEyes.sln -c Release --logger "console;verbosity=normal"` on the final binaries
lists every test - 2095 `Passed` lines, no `Failed` line, exit 0 - and the four full runs before it were
green too. The failure did not recur in five runs and no test is named because none failed in the named
run; the raw one-off is recorded in `mutation-evidence.txt` so it is not silently dropped.

## 6. Areas worth a smoke (QA decides)

- `api-smoke.ps1` / `gui-smoke.ps1`: NOT touched by this change (no App/Core code changed). Not needed.
- The one runtime surface is the setup CLI's `update` relaunch (criterion 5) - the tester session's
  live check. If QA drives it itself: build the setup CLI from this branch, run
  `$o = & <path>\agenteyes-setup.exe update 2>&1` from PowerShell with the app running, and time the
  return against the `exit=0` line; then `Invoke-RestMethod http://127.0.0.1:7882/health` for the new
  pid. Reminders: the REST API / UIA / PrintWindow layers are focus-free - never force-foreground the app
  and synthesize input; the recording HUD is capture-excluded, so assert app state via `/status`, not a
  screen grab.

## 7. CenCon impact

No drift: no component-map change (the launcher stays inside `AgentEyes.Setup.Engine`) and no change to
the privacy posture (visible / controllable). The one policy-adjacent effect is that a console child
started by this launcher gets a HIDDEN console; the only production child is the GUI tray app, for which
the flag is a no-op, and the app remains as visible as before (the relaunch honours its original
arguments, so a windowed app comes back windowed).

## 8. Review fix pass (self-review of PR #95, all addressed on the branch)

| Finding | What was done |
|---------|---------------|
| In-process pipe test could flake under xunit parallelism (another class's `Process.Start` child inherits the test's inheritable pipe end) | the class runs in a non-parallel collection after all parallel ones |
| Environment test spawns PowerShell while `PluginRegistryChannelTests` poisons `PSModulePath`; that class's invariant comment was false | same non-parallel collection; the comment now names this class and why it cannot collide |
| `Path.GetDirectoryName(exePath) ?? AppDir` never fell back for a bare exe name; CreateProcessW rejects `""` | exe resolved with `GetFullPath` first; regression test + M4 |
| Teardown `Directory.Delete` could mask the test's own verdict | best-effort cleanup catches `IOException` / `UnauthorizedAccessException` |
| Probe failure before the pid line hid the probe's stderr | the pid-line guard now fails with the probe's exit code and stderr |
| Dead stopwatch assertion duplicating the bound | removed |
| `BuildCommandLine` (public) had no logging | logs its result and every throw path |
| Criterion 5 marked pending tester | recorded in the issue comment: an OWNER CONSTRAINT on this developer session (no AgentEyes binary may be launched from it); criterion 5 is the tester session's gate, not a skipped step |

## 9. Second review pass (independent reviewer's non-blocking notes on PR #95, all addressed)

| Note | What was done |
|------|---------------|
| PR description stale (15 tests / 2094) | PR #95 body rewritten to the branch's facts (16 / 2095, collection, M4, probe kill) |
| The three validation throws in `Launch` did not log | each writes an `EngineLog` line before throwing |
| `?? _layout.AppDir` fallback was dead after `GetFullPath` | removed; a null `GetDirectoryName` (a drive root - impossible for a file) throws `InvalidOperationException` with the path |
| Two stacked `<summary>` blocks on the collection, none on the test class | each class has its own summary |
| A probe hanging before `pid=` was disposed, not killed | the timeout path kills the probe tree (`KillProcess`, waits for exit) and reports its stderr; the malformed-line path kills it too if it has not exited |
| One unnamed test failure in the record | a named (`verbosity=normal`) full run: 2095 passed, none failed (section 5) |

I believe this is finished, apart from criterion 5 which is pending the tester session by owner constraint.
