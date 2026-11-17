# Issue #200 - Developer handoff: `agenteyes-setup --root` stops the real installed app

Machine: the developer's Windows laptop. Branch `issue-200-setup-root-isolation` from main 02a4622. SDK 10.0.301 building
net8.0, runtime 8.0.21. The installed app on this machine is v1.12.0 (1.12.0+02a4622). Paths in the proof
files are scrubbed: `%USERPROFILE%`, `<scratch>` (this seat's scratch folder) and `<scratch-16nov>` (the
16 November seat's scratch folder).

## QA bounce 1 (17 Nov): the test that guards `uninstall --root` could delete the real install

Files: `bounce1/`. Branch at QA's 7d226df; main is da85516. Nothing in the product fix of round 1 changed.

**QA's defect, confirmed by reading the code.** `UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone`
drives the real CLI into the real `Uninstaller.Apply`. Suppose the isolated branch at `Uninstaller.cs:78` regressed.
`Apply` would then delete the real Start Menu and desktop shortcuts, the Run value and the uninstall entry,
and only after that would the test go red. The test's safety depended on the correctness of the code it
tests. It is the same mechanism as my own M3 incident in round 1, and I had designed that out only for the
refusal tests.

### How the test was made safe, and why this option

Three options were open. I chose the seam:

| Option | Verdict |
|--------|---------|
| Assert on `Uninstaller.Plan` instead of calling `Apply` | **Rejected.** The regression QA named lives in `Apply` (line 78), not in `Plan`. A Plan-only test passes with `Apply` regressed. That is the "safe by no longer checking the dangerous path" outcome. `Plan` already has its own test. |
| A precondition that refuses to run unless the targets are inside the root | **Not on its own.** The shortcut paths and HKCU keys are fixed by the OS, so they can never be inside a temp root. Without a seam, such a precondition can only ever refuse, so the test would never run. It is kept as the FIRST assert, on top of the seam (below). |
| **Put the removals behind a seam the whole test run redirects** | **Chosen.** It is the pattern this repo already uses for PATH and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` (`IUserEnvironment`, issue #78). It keeps the test driving the real `Apply` through the real CLI, so the dangerous path is still checked. And the redirect is installed by the test assembly's module initializer, not by this test. So it protects every test in the assembly, not just the one QA found. |

What changed:

- **`tools/AgentEyes.Setup.Engine/UserShell.cs` (new).** `IUserShell` holds the shortcut paths and the
  shortcut, Run-value, uninstall-entry and Inno-key reads and writes. `WindowsUserShell` is the real store:
  the old `InstallFinalizer` and `InnoMigration` code, moved verbatim (the same keys, values, COM calls and
  `GetFolderPath` lookups).
- **`InstallFinalizer` / `Uninstaller` / `InnoMigration`.** Every touch of those entries now goes through
  `InstallFinalizer.UserShell`. That includes `Uninstaller.Plan`'s existence checks and `RemoveShortcut`'s
  `File.Exists` / `File.Delete`. In the product the behaviour is unchanged.
- **`TestRunIsolation.RedirectEverything`** (the module initializer, which runs before any test) sets
  `InstallFinalizer.UserShell` to an `InMemoryUserShell`:
  - Its shortcut paths are under the test run's folder.
  - Its shortcuts are real placeholder files, so even a delete that goes AROUND the seam shows.
  - The Run value and the uninstall entry are in memory.
  - Every write through it is recorded.
- **The uninstall test now:**
  1. FIRST asserts the seam is the in-memory store and its shortcut paths are under the run folder. A run
     without the redirect fails here, before the uninstall runs.
  2. Seeds the store with the REAL install's four entries, pointing at an install outside the root.
  3. Runs `uninstall --root` and asserts no write reached the store, the snapshot is unchanged, and all
     four entries are still present.
  4. Then makes every assertion it made before.

  So it proves MORE than it did. It previously could not check that the shortcuts survive, because a
  regression would have deleted the real ones.
- **Two guards in `TestIsolationTests`, so the protection cannot quietly erode:**
  - `InstallFinalizer_InATestRun_KeepsTheShellEntriesInMemoryNotTheRealFolders`
  - `RegistryCalls_InTheSetupEngineAndCli_AreOnlyInsideTheRealUserShell`: an IL scan. Every
    `Microsoft.Win32.Registry*` call in the setup engine and CLI must sit inside `WindowsUserShell`, so a new
    HKCU write that bypasses the seam fails a test. Its instrument check requires that the scan SEES
    `WindowsUserShell`'s own calls.
  - The pinned folder lookups move from `InstallFinalizer` to `WindowsUserShell`.
- **`WindowsUserShell_WriteShortcutToATempPath_...`** exercises the moved COM shortcut code for real
  against a temp path. The moved REGISTRY code has no unit test. Testing it means writing the real HKCU,
  which is exactly what this change forbids. It is a verbatim move, and QA may want to read it side by side.

### The proof: red without deleting (`bounce1/M-uninstall-78-red-without-deleting.txt`)

`bounce1/mutate-uninstall-78.ps1`, rerunnable. It disables the isolated branch
(`if (false && _layout.IsIsolated)`), fingerprints the REAL six finalizer writes with `bounce1/fingerprint.ps1`,
builds, runs the test through `AgentEyes.sln` (the fresh `bin\x64` build), fingerprints again, and restores
the file byte-for-byte.

```
Failed UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone
  an isolated uninstall wrote outside the root:
    DeleteShortcut <run folder>\Shell\StartMenu\Programs\AgentEyes.lnk |
    DeleteShortcut <run folder>\Shell\Desktop\AgentEyes.lnk |
    SetRunValue (removed) | DeleteUninstallEntry
```

The regressed `Apply` made all four deletions QA predicted. Every one landed in the test run's store.

Real finalizer writes, BEFORE (04:55:20.809) and AFTER (04:55:33.188) the mutation run, identical line for line:

```
1-2 LNK %USERPROFILE%\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\AgentEyes.lnk target=%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe mtime=2026-11-17 04:00:18.957 sha256=901B71BB9FC498A8
1-2 LNK %USERPROFILE%\Desktop\AgentEyes.lnk target=%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe mtime=2026-11-16 18:26:46.480 sha256=494F392F1D96AEF4
3 ARP ver=1.12.0 loc=%USERPROFILE%\AppData\Local\AgentEyes\app uninstall="%USERPROFILE%\AppData\Local\AgentEyes\app\agenteyes-setup.exe" uninstall icon=%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe
4 RUN "%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe" --tray
5 PATH-AgentEyes %USERPROFILE%\AppData\Local\AgentEyes\app
6 DOTNET_BUNDLE_EXTRACT_BASE_DIR %USERPROFILE%\AppData\Local\AgentEyes\bundle
APP pid=17580 started=04:22:21   health: { "ok": true, "app": "AgentEyes" }
```

Each shortcut line carries its write time to the millisecond and a hash of the .lnk bytes. So a delete, a
re-create or a repoint would all change the line.

**Two more mutations, aimed at the safety net itself** (`bounce1/mutate-safety-net.ps1` -> `bounce1/M-safety-net.txt`).
The real fingerprint is identical around each.

- **M-B, a delete that goes around the seam:** the isolated branch also does
  `File.Delete(InstallFinalizer.StartMenuShortcutPath())`. No seam write is recorded, but the test goes red
  on the snapshot (`Assert.Equal() Failure: Strings differ`), because the store's shortcuts are real files.
- **M-C, the safety net removed:** the module initializer no longer swaps the seam. This run used the
  product CORRECT, so nothing real could have been deleted even if the guard failed. The uninstall test
  fails at its precondition (`Assert.IsType() Failure`) before the uninstall runs, and the
  `KeepsTheShellEntriesInMemory` guard fails too.

**A finding of my own on the way.** The first `:78` run recorded only `SetRunValue | DeleteUninstallEntry`,
with no shortcut deletes. My edit to `Uninstaller.RemoveShortcut` had silently not applied (a line-ending
mismatch in a scripted replace), so that path still called `File.Exists` / `File.Delete` directly. That was
harmless, because the path itself came from the seam and resolved under the run folder, but it bypassed
the store. The mutation exposed it. It is fixed, and the M-B placeholder files now make any such bypass
visible. Also: one early targeted run used `dotnet test tests/AgentEyes.Tests` (the PROJECT). That picked up
the stale `bin\Release` DLL, which is the same trap recorded below, and its "54 passed" is discarded. Every
result quoted here ran through `AgentEyes.sln` against `bin\x64`.

### Sibling sweep: tests that call a real Apply / Finalize or write outside a temp root

The pattern was copied. Every test below relied on product correctness to stay away from the real shell
entries. All of them are now protected by the seam, because the redirect is assembly-wide.

| Test(s) | What they drive | Exposure before | Now |
|---------|-----------------|-----------------|-----|
| `SetupRootIsolationTests`: install/update `--root --no-finalize` (3), `--dry-run` (1), refusals (4 cases), Inno (1) | real CLI `install`/`update` | If `--no-finalize` or a refusal regressed, the finalizer wrote the real Start Menu shortcut and ARP entry (and the Run value or desktop shortcut for `--autostart` / `--desktop-shortcut`). This is exactly my round-1 M3 incident. The Inno path's HKCU key delete was likewise unguarded. | through the seam; each test now also asserts `UserShell.Writes` is empty |
| `SetupCliRestartTests` (11 runs of `update`/`install --no-finalize`) | real CLI on the default layout (AGENTEYES_ROOT-redirected temp root) | Same shape: a regression in honouring `--no-finalize` would have finalized the REAL Start Menu shortcut and ARP entry, pointing at a temp folder | through the seam (no assertion added: that class tests the restart cycle) |
| `WizardInstallRunnerTests` | the wizard's `EngineInstallRunner.ApplyAsync` | Its own comment says it is safe because the failure paths "return before the wizard's finalize step, which writes the real" entries. That is safety by product correctness again. Its Inno takeover deletes an HKCU key the same way. | through the seam |
| `SetupEngineTests.UninstallPlan_IncludesTheBundleDirectoryAndItsVariable` | `Uninstaller.Plan` on a non-isolated layout | READ-only: it read the real shortcuts' existence and the real HKCU Run value | reads the test store |

**Found but not fixed here:**
- `WizardInstallRunnerTests` writes its setup log into the real `%LOCALAPPDATA%\AgentEyes\logs\setup` (QA's
  non-blocking note). That is a log, not a finalizer write, and is outside this bounce. Recommend filing it.
- No test reaches the WPF app's own Run-key writer (`src/AgentEyes.App/Autostart.cs`). Nothing in `tests/`
  references it, and the suite never launches the app. It stays outside the seam.

### Gate (bounce 1) - `bounce1/gate-windows.txt`

SDK 10.0.301 building net8.0, runtime 8.0.21.

- `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Error(s). The final build showed 22 warnings,
  because the test project recompiled. A `--no-incremental` rebuild lists 23 distinct warnings, all
  pre-existing: xUnit analyzer rules in untouched test files and one CS8602 in `AlwaysOnEngine.cs`. None is in a
  file this change touches, checked by filtering that list.
- `dotnet test AgentEyes.sln -c Release`:
  - **AgentEyes.Tests 2567 total, 2551 passed, 16 skipped (opt-in), 0 failed.** That is 3 more tests than
    round 1's 2564: the two guards and the COM shortcut test.
  - **AgentEyes.Tests.DesktopApp 23/23.**

### For QA (bounce 1)

1. Fingerprint the real machine (`bounce1/fingerprint.ps1 -Label before`).
2. Run `bounce1/mutate-uninstall-78.ps1`. Expected: the test is red, naming four writes to the run folder,
   and the before and after fingerprints are identical.
3. Optionally run `bounce1/mutate-safety-net.ps1` (M-B and M-C).
4. Read `WindowsUserShell` against the removed `InstallFinalizer` and `InnoMigration` code: it must be a
   verbatim move.

The real install is now safe to mutate against for this test and its siblings. That was not true in round 1.

## The answer in three lines

1. **Cause (measured):** `--root` changed only where files go. The stop -> swap -> relaunch cycle still
   found "the running AgentEyes" **by process name** (`RunningAppHandle.Find` -> `Process.GetProcessesByName
   ("AgentEyesApp")`), so it found the REAL app, stopped it, and started the **scratch** exe in its place
   with the real app's arguments.
2. **The crash is a SECOND, separate defect:** the relaunch hands the scratch app
   `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<root>\bundle` built from the root **as typed**. A root given with forward
   slashes (Git Bash, as on 16 November) makes that variable `C:/.../root\bundle`, and the WPF app then
   dies loading its native DLLs before it logs a line. With a backslash root the same relaunch does NOT
   crash. The stop happens either way; the crash only decides what the person is left with afterwards.
3. **Fix:** `--root` now builds an ISOLATED layout. It never stops or starts an app, refuses every step
   that writes outside the root, stages downloads and extractions inside the root, and `uninstall --root`
   removes only what is inside the root.

## 1. Reproduced first (criterion 1)

The real app was idle in every run, so nothing could be lost. Files are in `repro/`.

| Run | Command (published v1.12.0 `agenteyes-setup`) | Real app before | After |
|-----|------------------------------------------------|-----------------|-------|
| incident, 16 Nov | `install --release-dir dl --root C:/.../root-good --no-finalize` (Git Bash) | pid 19972, real path, `--tray` | pid 19972 stopped (quit request delivered, `app exit` 18:21:10); scratch pid 8680 started with `--tray`; crash event 18:21:14; no recorder until 18:28:41 |
| run 1 | same, backslash root | pid 10060, real path, no args | **pid 10060 gone**; scratch pid 21984 running, owns 7882, answers `/health` |
| run 2 | same, backslash root | pid 19132, real path, `--tray` | **pid 19132 gone**; scratch pid 14464 `--tray` running |
| run 3 | same, **forward-slash** root | pid 19004, real path, `--tray` | **pid 19004 gone**; scratch pid 17648 **crashed** with the incident's exact stack; **nothing listening on 7882** |

The stop happened in 3 runs out of 3, plus the incident. Each file shows the process list, `/health`,
`/status` and the owner of 7882, before and after.

**What the scratch copy did while it ran (run 1).** It was not a harmless copy. It ran as the full product
against the person's REAL data, because the app keeps its config, log and library under the real per-user
root whatever exe it starts from. Its own log lines (in the real log) show `REST API listening on
http://127.0.0.1:7882/`, `RefreshAccountIndicator: signedIn=True` and a housekeeper pass over the real
library (`186 recording(s)`). The housekeeper was report-only, so nothing was changed. This is why the fix
refuses to start any app under `--root` instead of trying to "start the right one".

## 2. The cause, with evidence (criterion 2)

- **The stop.** The incident's own setup log (`repro/incident-16nov-setup-cli-log.txt`) says it outright:
  `[RunningAppHandle] Find: pid 19972, exe %USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe` ->
  `[RunningApp] StopAndWait: name=AgentEyesApp` -> `quit requested from pid=19972` -> `Relaunch: starting
  the installed <scratch-16nov>/root-good\app\AgentEyesApp.exe with argument(s): --tray (the stopped
  process ran from ...\AgentEyes\app\AgentEyesApp.exe)`. Runs 1-3 print the same `note: the stopped
  process ... was running from ... not from the installed ...`. The issue's candidate (the
  stop-before-replace step) was right, and the mechanism is the process-NAME lookup.
  The second command that night (`root-bad`) then found the scratch pid 8680 as "the running AgentEyes",
  which is the same defect again.
- **The crash** (`repro/run3-forward-slash-root.txt`, `repro/crash-isolation.txt`). Run 3 differed from
  runs 1-2 only in slash direction, and only run 3 crashed:
  `.NET Runtime 1026: System.DllNotFoundException ... at MS.Internal.WindowsBase.NativeMethodsSetLastError.
  SetWindowLongPtrWndProc`, then `Application Error 1000, 0xe0434352` and `0xc000041d`, faulting path the
  scratch exe. To isolate the variable WITHOUT touching the real app, I started the same scratch exe with
  `--tray` (a refused second instance exits quietly) and changed only `DOTNET_BUNDLE_EXTRACT_BASE_DIR`:
  - backslash: it reached the single-instance check and logged `second instance refused`, exit 0;
  - forward slashes, twice: it died before its first log line, **exit 0xC00000FD**.
  The real app's pid was unchanged across all three.
  **Two failures, one trigger (resolved by QA, `qa/crash-0xC00000FD-vs-DllNotFound.txt`, 2/2 each).** The
  forward-slash `DOTNET_BUNDLE_EXTRACT_BASE_DIR` produces one of TWO different failures. Which one depends on
  whether the extraction directory's PARENT already exists:
  - **Parent exists:** the native DLLs extract, then the WPF app dies with `DllNotFoundException`
    (0xe0434352). This is event-for-event the end-to-end crash of the incident and run 3. The scratch
    install had created the root, so the parent existed.
  - **Parent missing:** a native stack overflow in ntdll (0xC00000FD) before anything is extracted. My
    isolated runs above hit this one, because they pointed the variable at a folder whose parent did not
    exist.
  My first write-up named the first mechanism and reported the second as an unexplained difference. It is
  not unexplained: both come from the same forward-slash value, and the attribution to that value stands.
  The exact loader path inside the runtime is not characterised.
- The bundle folder of the incident's scratch root is complete (all five WPF native DLLs extracted
  18:21:14.03-.32), so a missing file is not the explanation either.

## 3. The fix

| File | Change |
|------|--------|
| `tools/AgentEyes.Setup.Engine/InstallLayout.cs` | `InstallLayout.Isolated(root)`: `IsIsolated`, the root made absolute (`Path.GetFullPath`, so forward slashes become backslashes), `StagingDir` (`<root>\staging` when isolated, `%TEMP%` otherwise, unchanged) and `Contains(path)`. |
| `tools/AgentEyes.Setup.Engine/IsolatedRootAppHandle.cs` (new) | The running-app handle for an isolated layout. It never stops anything. An AgentEyes running OUTSIDE the root is logged and left alone (`Find` returns null). One running INSIDE the root means refuse, naming the pid. An unreadable exe path means refuse. Exe paths are read with `PROCESS_QUERY_LIMITED_INFORMATION`. `RefusingAppLauncher` throws if anything ever asks it to start an app. |
| `tools/AgentEyes.Setup.Cli/Program.cs` | `--root` -> `InstallLayout.Isolated`. `DefaultLayoutFactory` test seam (see Tests). |
| `tools/AgentEyes.Setup.Cli/Commands.cs` | Isolated `install`/`update`: uses the isolated handle and the refusing launcher (never the process-name handle). Refuses without `--no-finalize` (unless `--dry-run`); refuses `--autostart` and `--desktop-shortcut` (previously IGNORED silently with `--no-finalize`); refuses the Inno takeover (it deletes an HKCU key); stages downloads in the root. `uninstall --root` refuses only for a copy running inside the root, and its relaunch-from-temp copy goes into the root. The restart line says what happened (`isolated --root install: no AgentEyes was stopped or started ...`) instead of the false "AgentEyes was not running". |
| `tools/AgentEyes.Setup.Engine/ReleaseSource.cs`, `ArchiveInstaller.cs`, `UpdateRunner.cs` | Overloads that take the staging dir; `UpdateRunner` passes `layout.StagingDir`. Non-isolated behaviour is unchanged (`%TEMP%`). |
| `tools/AgentEyes.Setup.Engine/Uninstaller.cs` | Isolated: plans and removes only the app dir, setup bookkeeping and bundle dir, and reports one line naming everything outside the root that it left alone. **Before this, `uninstall --root` would have deleted the REAL Start Menu and desktop shortcuts, the Run key and the Add/Remove Programs entry by fixed name.** That is found by reading the code and was not run against the real install. |
| `tools/AgentEyes.Setup.Cli/CliHelp.cs` | `--root` help states the isolation. |

Every outside-root touch I found, and what happens to it under `--root` now:

| Touch | Before | Now |
|-------|--------|-----|
| Find + stop the running app by name | stops the REAL app (measured) | never; outside left alone, inside refused |
| Relaunch the app | starts the scratch copy on the REAL data and port (measured) | never (`RefusingAppLauncher`) |
| PATH, `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, Start Menu shortcut, uninstall entry | written, pointing at the scratch root | refused without `--no-finalize` (exit 2, nothing touched) |
| `--autostart`, `--desktop-shortcut` | written (or silently ignored with `--no-finalize`) | refused (exit 2) |
| Inno takeover (HKCU key delete) | done | refused (exit 1, nothing touched) |
| Download staging, zip extraction | `%TEMP%` (measured by the kernel deny) | `<root>\staging` |
| relaunch-from-temp copy (update/uninstall of a setup exe inside the root) | `%TEMP%` | `<root>\staging` |
| uninstall: shortcuts, Run key, uninstall entry, PATH, variable | deleted by fixed name (code) | not touched; named in the report |

## 4. After the fix, the same command leaves the real install untouched (criterion 3)

`after-fix/fixed-build-medium-integrity.txt` was run at normal integrity, as the person would run it, with
the real app (pid 20576, `--tray`) running:

- A: backslash root, B: forward-slash root. Both exit 0 with all 4 components installed in the root, and
  print `isolated --root install: no AgentEyes was stopped or started`.
- C: the same without `--no-finalize`: exit 2, `needs --no-finalize`, nothing touched.
- D: `uninstall --root`: removes the root's app and bookkeeping and prints the not-touched line.
- **The real app: pid 20576, started 03:48:10, before, after A, after B and 15 s after D.** `/health`
  answered every time. The Start Menu shortcut's write time, the uninstall entry (1.12.0 at the real app
  dir), the Run key and the bundle variable are identical before and after. There are no crash events.

Paired control: the same command on the published v1.12.0 stopped the real app in runs 1-3 (section 1).

## 5. The scratch install works, and the crash is explained (criterion 4)

The scratch install works: section 4 runs A/B, and the kernel-deny run below. It is not started, by design
(an app started from a scratch root uses the real data and port, so it cannot be isolated). The crash is the
second defect in section 2. Under `--root` it is now unreachable twice over: nothing is launched, and the
root is normalised to backslashes. **It is still reachable outside `--root`; see "Not fixed" below.**

## 6. Tests, and the mutations (criterion 5)

New: `tests/AgentEyes.Tests/SetupRootIsolationTests.cs`, 25 tests, in the CLI test collection. They run the
CLI's real entry point with `--root` while the running-app and launcher seams are SPIES reporting an
AgentEyes running OUTSIDE the root (the incident's situation). So a regression fails a test instead of
stopping the developer's own recorder.

- `InstallWithRoot_/UpdateWithRoot_AnAppRunsOutsideTheRoot_IsNeverLookedUpStoppedOrStarted`: the defect.
- `InstallWithRoot_StagesEveryDownloadAndExtractionInsideTheRoot`: asserts a PRESENCE (2 downloads,
  1 extraction, read from the root's log), every one inside the root.
- Refusals: no `--no-finalize`; `--autostart on|off`; `--desktop-shortcut`; Inno marker; `--dry-run` allowed.
- `UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone` and the isolated `Plan()`.
- The layout (`Isolated` is absolute with no `/`; `StagingDir`; `Contains`, including a sibling with the
  same prefix and a `..` escape).
- The isolated handle: outside -> null; inside -> refused naming the pid; unreadable -> refused; `StopAsync`
  and `RefusingAppLauncher` always refuse.
- `IsolatedHandleFind_RealProcessNamedAgentEyesApp_...`: a REAL process named `AgentEyesApp.exe` (a renamed
  copy of cmd.exe) outside the root is found and left running. With the root moved over it, it is refused
  by pid and is still running.

Changed: `SetupCliRestartTests` (12 calls). These test #86's stop -> swap -> relaunch cycle and used
`--root` only to reach a temp root; one of them (`Update_StoppedProcessRanFromElsewhere_StartsTheInstalledExe`)
literally pinned the defect's behaviour under `--root`. They now reach their temp root through
`Program.DefaultLayoutFactory` with the same assertions.

**Mutations** (`mutations/`), each run against the 25 tests and then restored:

| Mutation | Caught by |
|----------|-----------|
| M1 the defect back: `--root` uses the process-name handle and launcher | 2 tests (`...IsNeverLookedUpStoppedOrStarted`, install and update: `Expected: 0, Actual: 1`) |
| M2 downloads staged in `%TEMP%` again | 1 test (`...StagesEveryDownloadAndExtractionInsideTheRoot`) |
| M3 the `--no-finalize` refusal removed | 1 test (`...WithoutNoFinalize_IsRefusedBeforeAnythingIsTouched`) |
| M4 a copy inside the root treated as outside | 2 tests (the injected and the real-process refusal tests) |

**An incident of mine, in the mutation run. Reported, repaired and designed out.** The first M3 run
(`mutations/M2-M4-first-pass.txt`) gave the refusal test a REAL release dir. With the refusal gone, the
install ran the finalizer, and the finalizer has no test seam for the Start Menu or HKCU (TestRunIsolation
swaps only the environment store). It **repointed this machine's real Start Menu shortcut and Add/Remove
Programs entry at the test's temp root (version 9.9.9)**. I found it by checking right after. I repaired it
with the product's own finalizer (v1.12.0 `install --release-dir <v1.12.0 release>`: `Nothing to do`, then
`Start Menu shortcut: created`, `Add/Remove Programs: registered v1.12.0`, real app pid unchanged).
Verified: the shortcut targets `%USERPROFILE%\AppData\Local\AgentEyes\app\AgentEyesApp.exe` and the entry
is 1.12.0 at the real app dir. The Run key, desktop shortcut, PATH and variable were never touched. The
refusal tests now give a release dir that does not exist, so a regressed refusal fails reading it BEFORE
the finalizer. The M3 rerun (`mutations/M3-M4-redo.txt`) fails the test and leaves the real shortcut's
write time and the uninstall entry exactly as they were. The same first pass's M4 result is INVALID: its
mutation did not compile, so it tested M3's binary. It was redone with a compiling mutation.

**I designed that incident out for the refusal tests only, not for the uninstall test.** QA found the gap
(bounce 1, below). Since bounce 1 the finalizer and the uninstaller reach the shortcuts, the Run value and
the uninstall entry only through a seam, and the test run keeps that seam in memory. So the release dir that
does not exist is no longer the only thing between a regressed refusal and the real Start Menu.

## 7. The kernel-level deny (the #161 method, on Windows)

`lowil/harness.ps1` + `lowil/probe.ps1`, rerunnable. Mandatory Integrity Control: the CLI runs at **Low
integrity** (a low-labelled copy of the exe), so the kernel refuses its writes to every Medium object (the
real per-user root, `%TEMP%`, the Start Menu, HKCU, the person's processes and named events). Only the
scratch root is labelled Low.

- **Denial control, proven first in every run.** The probe ran at Low (S-1-16-4096). It was DENIED: a file
  in the real root, in `%TEMP%` and in the Start Menu; a value in HKCU\Environment and HKCU Run; full access
  to the real app's process; modify on its quit event. It was ALLOWED a file in the scratch root. The
  harness stops if any of that differs.
- **Fixed build** (`lowil/run-fixed-build.txt`): exit 0, 4 components installed, all 4 downloads and the
  extraction inside `<root>\staging`, real state identical before and after.
- **Positive control, the published v1.12.0** (`lowil/run-control-v1.12.0.txt`): **refused by the kernel**
  at its first write outside the root: `Access to the path '%USERPROFILE%\AppData\Local\Temp\agenteyes-setup-
  ...-AgentEyesApp-win-x64.exe' is denied`, exit 1.
- `lowil/run-stale-1.11.11-binary.txt` is a run I made by mistake against a STALE build (the setup-CLI
  path trap below). It was refused exactly like the control and is kept as a second positive control.
- **What this instrument does NOT cover:** at Low integrity a process cannot even see the person's Medium
  processes (`GetProcessesByName` returns none; probe P6 reads "not running"). So the kernel deny proves
  "no write outside the root", but it cannot show a stop attempt. That half of the claim rests on the
  Medium-integrity paired runs (sections 1 and 4) and on the M1/M4 mutations.

## 8. Gates (criterion 6)

- `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test AgentEyes.sln -c Release` (Windows, SDK 10.0.301 / net8.0 / runtime 8.0.21, default gate):
  **AgentEyes.Tests 2564 total, 2548 passed, 16 skipped (opt-in), 0 failed**, 73 s;
  AgentEyes.Tests.DesktopApp 23/23.
- Linux and macOS suites: **not runnable on this Windows host**. The Linux project does not restore off its
  platform (`NU1201`). The macOS project runs here, but one on-Mac path test fails by design on Windows. I
  checked that neither suite compiles any file this branch changes (they reference only Core and
  DesktopApp). Their gate is CI on the PR; "green across more than one run" is for QA to read off CI.

## For QA - how to verify each criterion

1. Real app running (`GET http://127.0.0.1:7882/status` -> `State: idle` first; never run this while a
   recording is in progress). Run the published v1.12.0 `agenteyes-setup install --release-dir <v1.12.0
   release> --root <scratch> --no-finalize`; the real pid is gone. Restore the real app afterwards and check
   `/health`.
2. Same command with this branch's build: same pid, same start time, `/health`. Also try forward slashes,
   no `--no-finalize` (exit 2), `--autostart on` (exit 2) and `uninstall --root`.
3. `harness.ps1 -SetupExe <exe> -ReleaseDir <release> -Work <scratch> -Label <new name>` for the fixed build,
   then for v1.12.0. The control must print `CONTROL PROVEN` first.
4. `dotnet test AgentEyes.sln -c Release --filter FullyQualifiedName~SetupRootIsolation` (25), and apply M1
   by hand to watch two fail.
- Focus-free throughout: REST `/health` and `/status`, Win32_Process, the setup CLI's own output. No UI is
  involved, and nothing needs foregrounding.

## Traps I hit (worth knowing)

- **The setup CLI's path trap is the reverse of the app's.** The solution build puts `agenteyes-setup.exe`
  in `tools\AgentEyes.Setup.Cli\bin\Release\net8.0-windows\`, and `bin\x64\Release\` holds a STALE one
  (1.11.11, 16 Nov). I ran the stale one once (`lowil/run-stale-...`).
- `dotnet test tests/AgentEyes.Tests -c Release --no-build` (the PROJECT, not the solution) ran a STALE
  `bin\Release` test dll from 16 Nov and reported green on code that should have failed six tests. Use the
  solution.

## Not fixed here - the cases I am tempted to assume away

- **`AGENTEYES_ROOT` carries the same promise and is NOT isolated.** `InstallLayout.Default()` honours it
  ("so tests can point at temp directories"), but a layout from it is an ordinary one: it stops by name,
  relaunches and finalizes. Only `--root` is isolated. The test host sets it for its own process, so making
  it isolated changes test behaviour well beyond this issue. Recommend filing it.
- **The second defect (forward-slash `DOTNET_BUNDLE_EXTRACT_BASE_DIR`) is still reachable outside
  `--root`.** `ProcessAppLauncher` and `InstallFinalizer.SetBundleExtractBaseDir` write
  `layout.BundleExtractDir` as typed. The default root comes from `SpecialFolder` (backslashes), so only
  `AGENTEYES_ROOT=C:/...` reaches it today, but the finalizer would set it USER-WIDE, and every
  Explorer-launched AgentEyes would then crash at start. That is #147's code; recommend filing it rather
  than widening this change.
- `uninstall --root` driven from a setup exe inside the root leaves its relaunch copy in `<root>\staging`;
  the uninstaller deletes only app, bookkeeping and bundle.
- The low-integrity harness cannot see processes (section 7). The no-stop claim rests on the Medium runs and
  the mutations, not on the kernel deny.

## Noticed for #147 and #148 (queued behind this; not taken)

- **#147:** `%LOCALAPPDATA%\AgentEyes\bundle\` on this machine holds `cc-director\`, `cc-launcher\` and
  `devthrottle-gateway\` extraction folders. Those are OTHER products' single-file apps unpacking into
  AgentEyes' folder because the variable is user-wide. AgentEyes' uninstaller deletes that whole folder,
  under those products' feet. The forward-slash crash above is a second reason the variable's value must be
  normalised wherever it is written.
- **#148:** my repair run `install --release-dir <v1.12.0>` against an up-to-date install printed
  `Nothing to do - all components up to date.`, exit 0, then finalized. Here that was exactly what I
  wanted. It is also the "installs nothing on a version match" behaviour #148 is about, and this branch
  does not change it.

## Machine state at handoff (bounce 1, `bounce1/machine-state-end.txt`)

After the final gate: the real app is pid 17580 (v1.12.0, started 04:22:21, the same pid QA left), `/health`
ok, idle. The six finalizer writes are identical to the fingerprint taken before any bounce-1 test run: both
shortcuts have the same target, write time and hash, and the ARP entry, Run value, PATH and bundle variable
all point at the real install. No ffmpeg is running and none was touched. No recording was made or deleted.

## Machine state at handoff (round 1)

The real app is running, answering `/health`, v1.12.0 (1.12.0+02a4622), started by me with `--tray`
(the Run key's own arguments) at 03:48:10 after run 3. The real Start Menu shortcut, desktop shortcut, Run
key, uninstall entry, PATH and bundle variable point at the real install. Every scratch process I started
is gone. No ffmpeg was touched, and no recording was deleted or made.

## CenCon impact

No component-map change. Posture: this strengthens "visible, controllable". A testing option can no
longer silently stop the always-on recorder.

I believe this is finished.
