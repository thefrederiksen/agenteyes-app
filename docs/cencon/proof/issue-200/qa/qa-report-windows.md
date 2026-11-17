# Issue #200 - QA report (Windows) - PR #207 at 2821837

**Verdict: FAIL (flow:qa-failed) - one defect, in the test that guards `uninstall --root`.** Everything else in
this report verified independently and passes; the defect is narrow and the rest need not be redone.

QA seat: a fresh clone (`git clone` into a separate folder, not the developer's tree), branch
`issue-200-setup-root-isolation` at 2821837 over main 02a4622, on the owner's laptop. SDK 10.0.301 building
net8.0, runtime 8.0.21. Paths scrubbed to `%USERPROFILE%` / `<scratch>`. Raw evidence is in this folder.

## 0. Priority one - the real install, checked FIRST: CLEAN

The developer disclosed that a mutation run repointed the real Start Menu shortcut and uninstall entry at a test
folder, and repaired it. Checked read-only before anything else, enumerating every item `InstallFinalizer`
writes (not only the two named) - `machine-state-first.txt`:

| Item | Actual |
|------|--------|
| Start Menu `AgentEyes.lnk` target | `%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe` (written 04:00:18, the repair) |
| Desktop `AgentEyes.lnk` target | same exe (written 16 Nov 18:26, never touched) |
| Uninstall entry | DisplayVersion 1.12.0, InstallLocation `...\AgentEyes\app`, UninstallString `"...\AgentEyes\app\agenteyes-setup.exe" uninstall` |
| HKCU Run | `"...\AgentEyes\app\AgentEyesApp.exe" --tray` |
| `DOTNET_BUNDLE_EXTRACT_BASE_DIR` | `...\AgentEyes\bundle` |
| PATH | `...\AgentEyes\app` |
| File associations | none written by the finalizer; none present |
| Installed app | 1.12.0+02a4622, pid 20576 `--tray`, `/health` ok, `/status` idle |

Nothing points at a test folder. The repair holds.

## 1. Criterion 1 - reproduced on the published v1.12.0 (control): REPRODUCED, 1/1

`repro-and-fix-live.txt`. `agenteyes-setup` 1.12.0+02a4622 `install --release-dir <v1.12.0 assets> --root
<scratch>\ctl-root1 --no-finalize` with the real app idle on pid 20576:

- Before: pid 20576 (real exe, `--tray`), `/health` ok, last `REST API listening` line `[pid 20576]`.
- Output: `restarted the running app (pid 20576 -> pid 23136)` plus the "stopped process was running from ..." note.
- After: **pid 20576 gone**; pid 23136 runs `<scratch>\ctl-root1\app\AgentEyesApp.exe --tray`; `/health` ok; the
  REAL app log's last line is `[pid 23136] REST API listening on http://127.0.0.1:7882/`. The scratch copy owns
  the real port on the real data. (HTTP.sys reports the listener as pid 4, so the app's own log line is the
  attribution.)
- Restored: scratch pid stopped (verified its exe path and that it had no children first); real app restarted
  as the Run key starts it; pid 17580, `/health` ok, `[pid 17580] REST API listening`.

## 2. Criterion 2 - cause named with evidence: PASS

Code: `Commands.UpdateAsync` built the cycle from `RunningAppHandleFactory` -> `RunningAppHandle` (process-NAME
lookup) for every layout; `--root` only changed file paths. My control run printed the same "stopped process was
running from ... not from the installed ..." note the incident log has. The PR routes an isolated layout to
`IsolatedRootAppHandle` + `RefusingAppLauncher` (`Commands.cs:133-136`).

## 3. Criterion 3 - after the fix, the same command leaves the real install untouched: PASS

PR build (`tools\AgentEyes.Setup.Cli\bin\Release\net8.0-windows\agenteyes-setup.exe`, 1.12.0+2821837), real app
pid 17580 running:

| Run | Result |
|-----|--------|
| A backslash root, `--no-finalize` | exit 0, 4 installed, `isolated --root install: no AgentEyes was stopped or started` |
| B FORWARD-slash root, `--no-finalize` | exit 0, 4 installed; root normalised to backslashes; root log: `pid 17580 runs from outside the root ... left alone`; all downloads and the extraction under `<root>\staging` |
| C no `--no-finalize` | exit 2, `needs --no-finalize ... Nothing was touched.`, no root created |
| D `--autostart on` / D2 `--desktop-shortcut` | exit 2 each, no root created |
| E `uninstall --root` (root A) | exit 0; removed only `<root>\app`, `<root>\config\setup`; printed the "outside the --root, not touched" line |

Real app: **pid 17580, started 04:22:21, before A, after A, after B, 5 s after E**; `/health` ok and the last
`REST API listening` line `[pid 17580]` every time. The six-item finalizer fingerprint (`realstate.ps1`) is
identical before and after, line for line. Paired with section 1 on the same machine and the same release
assets, the difference is attributable to the change.

## 4. Criterion 4 - the scratch install works; the crash explained: PASS, with a correction

The install works (A, B, and the kernel-deny run). On the crash, the developer's two observations had different
symptoms (DllNotFoundException end to end, 0xC00000FD in isolation). **They are two different failures with the
same trigger**, split by whether the extraction dir's PARENT exists (`crash-0xC00000FD-vs-DllNotFound.txt`):

- Parent exists (the end-to-end case - the root already existed): native DLLs extract, then
  `DllNotFoundException` at `SetWindowLongPtrWndProc`; events 1026 -> 0xe0434352 -> 0xc000041d at KERNELBASE
  offset 0xc569a - **identical to the end-to-end crash**. 2/2, with and without CREATE_NO_WINDOW.
- Parent missing (the developer's isolated runs): native stack overflow in ntdll (0xC00000FD), **nothing
  extracted**, no .NET event. 2/2. A different failure.
- Backslash: extracts and reaches the lock, exit 0.

So the developer's isolated experiment reproduced a different failure; the attribution to the forward-slash
variable was still right and is now confirmed with a matching symptom. The single-instance lock is not a factor:
both crash before `OnStartup` takes it. Not measured: the exact apphost frame that recurses. Both failures are
unreachable under `--root` after this PR (root normalised, nothing launched) and remain reachable by a
forward-slash `AGENTEYES_ROOT`; that filing should name both symptoms.

## 5. Criterion 5 - a test fails if the root is escaped, mutation shown failing: FAIL (the defect)

Mutations I applied myself, each built and run against `SetupRootIsolationTests` (25), then restored
(`mutations-pass1.txt`, `mutations-pass2-M4-M5-compiling.txt`). Real finalizer state identical across all runs.

| Mutation | Caught by |
|----------|-----------|
| M1 the defect back (isolated layout uses the process-name handle and launcher) | `InstallWithRoot_AnAppRunsOutsideTheRoot_IsNeverLookedUpStoppedOrStarted`, `UpdateWithRoot_AnAppRunsOutsideTheRoot_IsNeverLookedUpStoppedOrStarted` |
| M2 `StagingDir` back to `%TEMP%` | `InstallWithRoot_StagesEveryDownloadAndExtractionInsideTheRoot`, `Isolated_IsMarked_IsAbsolute_AndStagesInsideTheRoot` |
| M3 up-front refusal removed | `InstallWithRoot_WithoutNoFinalize_IsRefusedBeforeAnythingIsTouched` + the 3 `InstallWithRoot_AskingForSomethingOutsideTheRoot_IsRefused` cases |
| M4 copy inside the root treated as outside | `IsolatedHandleFind_ProcessInsideTheRoot_RefusesNamingThePid`, `IsolatedHandleFind_RealProcessNamedAgentEyesApp_OutsideIsLeftRunning_InsideIsRefused` |
| M5 (QA) isolated `Uninstaller.Plan()` lists outside targets | `UninstallerPlan_Isolated_ListsOnlyTargetsInsideTheRoot` |
| M6 (QA) only the zip extraction back to `%TEMP%` | `InstallWithRoot_StagesEveryDownloadAndExtractionInsideTheRoot` |

My first M4 and M5 used a constant `if (false)` and **did not compile** (the same trap as the developer's first
M4); they were redone with a runtime-false condition and both went red. Unmutated: 25/25.

**DEFECT - the test that guards `uninstall --root` escapes the root on the regression it exists to catch.**
Found by code reading and deliberately NOT run, because running it deletes the owner's real shortcuts.

- `UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone`
  (`tests/AgentEyes.Tests/SetupRootIsolationTests.cs:182-203`) runs the real CLI `uninstall --root` -> the real
  `Uninstaller.Apply`.
- If the isolated branch at `tools/AgentEyes.Setup.Engine/Uninstaller.cs:78` regresses (the exact pre-PR
  behaviour), `Apply` falls through to `Uninstaller.cs:97-107`: `RemoveShortcut(StartMenuShortcutPath())`,
  `RemoveShortcut(DesktopShortcutPath())`, `SetAutostart(enabled: false)`, `RemoveUninstallEntry()`.
- Those resolve to the REAL machine: `Environment.GetFolderPath(StartMenu / DesktopDirectory)`
  (`InstallFinalizer.cs:143,147`) and `Registry.CurrentUser` (`InstallFinalizer.cs:194,248`).
  `TestRunIsolation.RedirectEverything` (`tests/AgentEyes.Tests/TestRunIsolation.cs:86-101`) redirects data
  paths, `AGENTEYES_ROOT` and the environment store only - none of these.
- The isolated `IsolatedRootAppHandle.Find()` lets the command proceed while the real app runs outside the root,
  so nothing stops it before `Apply`.
- Consequence: the run that would show this mutation failing deletes the test machine's real Start Menu and
  desktop shortcuts, its run-at-login value and its Add/Remove Programs entry, and THEN goes red. That is the
  same mechanism as the incident disclosed in the handoff (M3, a finalizer with no test seam) - designed out for
  the refusal tests by a missing release dir, not designed out here. The class comment
  (`SetupRootIsolationTests.cs:24-25`) says "The spies are also what makes a regression safe to run"; for this
  test it is not.
- Criterion 5 asks for a mutation shown failing. For the uninstall escape no mutation can be shown failing on a
  developer machine without performing the escape, and the handoff's mutation table has no uninstall mutation.

Expected: a regression of the isolated uninstall fails a test without touching anything outside the test's
temp root. Actual (by code): it deletes four real items first. Direction, for the developer to choose: give the
uninstall test a way to fail BEFORE `Apply` can reach real targets (as the refusal tests do), or put the
shortcut paths and the HKCU keys behind a seam the test run redirects - and then show the `Uninstaller.cs:78`
mutation going red with the real fingerprint unchanged.

## 6. The kernel-level deny: PASS - control proven first

The harness exits 3 unless P0 is Low and P1-P7 are DENIED and P8 ALLOWED, and runs that BEFORE the command; the
developer's committed control output shows `CONTROL PROVEN` ahead of the command. Re-run by QA
(`lowil-fixed-build.txt`, `lowil-control-v1.12.0.txt`), control proven first in both:

- PR build at Low integrity: exit 0, 4 installed, all 4 downloads and the extraction under `<root>\staging`.
- v1.12.0 at Low integrity: exit 1, refused by the kernel at its first `%TEMP%` write.
- Real state (pid 17580, `/health`, shortcut, entry, Run, PATH, variable) identical before and after both.

Note on the instrument: the probe counts ANY exception as DENIED. P6's "denial" is `Process with an Id of 17580
is not running` - invisibility, not a kernel refusal. The developer stated this limit (the stop half rests on the
Medium-integrity runs and M1/M4), and P1-P5 and P7 carry genuine access-denied messages.

## 7. Criterion 6 - gates: Windows PASS; CI noted

- `dotnet build AgentEyes.sln -c Release`: 0 Error(s), **24 Warning(s)** (fresh clone). None is in a file this
  PR changes (all pre-existing xUnit analyzer warnings plus one CS8602 in AlwaysOnEngine.cs). The handoff's
  "0 Warning(s)" was an incremental build.
- `dotnet test AgentEyes.sln -c Release` (Windows, default gate): **AgentEyes.Tests 2564 total, 2548 passed, 16
  skipped, 0 failed** (1 m 21 s); **AgentEyes.Tests.DesktopApp 23/23**. The developer's numbers are consistent
  (2548 + 16 = 2564). Setup CLI path: built to `tools\AgentEyes.Setup.Cli\bin\Release\net8.0-windows\`; no stale
  `bin\x64\Release` setup exe exists in this fresh clone.
- CI on 2821837, two runs: Windows 2/2, macOS 2/2, **Linux 1/2** - the push run failed
  `HeadlessControlServerFramingTests` with `HttpListenerException: Address already in use` in `Dispose`, the
  known flake #174, in a suite this PR does not touch.

## 8. Scope

- `AGENTEYES_ROOT` not isolated: OUT - `InstallLayout.Default` unchanged; filed as #208.
- Forward-slash `DOTNET_BUNDLE_EXTRACT_BASE_DIR` outside `--root`: OUT - `ProcessAppLauncher.cs` and
  `InstallFinalizer.cs` not changed. No filing found yet.
- "`uninstall --root` would have deleted the real shortcuts and uninstall entry" is stated in the handoff as code
  reading, not run, and left that way. Confirmed by reading that the new isolated `Plan()`/`Apply()`
  (`Uninstaller.cs:37,78`) touch only `<root>\app`, `<root>\config\setup` and `<root>\bundle`.

## Observations (not blocking)

- Finalization is gated by the up-front refusal plus `--no-finalize` only; the finalize block
  (`Commands.cs:180`) has no `IsIsolated` check of its own. One guard, not two.
- `WizardInstallRunnerTests` (not touched by this PR) writes its wizard log into the REAL
  `%LOCALAPPDATA%\AgentEyes\logs\setup\` during `dotnet test` (fake pids, v9.9.9). Log-only, same family; worth
  filing.

## Machine state at the end

Real app: pid 17580, `%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe --tray` (restarted by QA after
the control run), v1.12.0+02a4622, `/health` ok, idle. Shortcuts, uninstall entry, Run key, PATH and variable
identical to section 0. Every scratch process QA started is gone. No ffmpeg was touched; no recording was made
or deleted.
