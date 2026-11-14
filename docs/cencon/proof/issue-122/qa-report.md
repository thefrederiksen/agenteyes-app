# Issue #122 - QA report (devlinux)

**Verdict: PASS.** Every criterion holds, criterion 5 as amended by the lead. Handing to the review gate (`flow:ready-gate`).

- PR #129, branch `issue-122-cli-help-and-unknown-flags`, head `4c9559c`. The only change from `35c0670` to `4c9559c` is `handoff.md`.
- QA run on devlinux, 2026-11-14, 11:15-11:25 EST. dotnet SDK 8.0.134.
- Evidence is in `docs/cencon/proof/issue-122/qa/`. Every number below can be read back from those files.

## Which build was answering

The service the previous seat left was installed at 11:08:12. That is before `35c0670` was committed (11:09:35), so it came from an uncommitted working tree. Its `agenteyes.dll` hash also differed from a build of the clean head (`a8ff3318...` vs `b9b3ff1d...`). So I did not trust it, and reinstalled the Core myself:

- I checked `/status` was `idle`.
- I ran the same `dotnet publish` line as `scripts/linux/install.sh` (linux-x64, self-contained) from the clean head `4c9559c`.
- I stopped the unit, swapped `~/.local/lib/agenteyes`, and started the unit.
- I skipped the script's desktop-app half on purpose: it kills and relaunches a window on the user's session.

After the swap:
- the installed `agenteyes.dll` hash is `b9b3ff1d29af1344aa170cddafde0c74a5cef1e60d54d2c5c5719e1aa1fb59d6`
- the unit restarted at 11:18:45 (pid 2122968)
- `/health` returns `{"ok":true,"app":"AgentEyes","host":"headless","platform":"linux"}`

**The service is left running, healthy and idle, on branch head `4c9559c`.** It is not on main. Whoever merges should reinstall from main afterwards.

For the main comparison (criterion 5), I built main's binary (`origin/main` = `df81375`) the same way, from a separate worktree. Its DLL contains no `CliDispatcher` string, which confirms it is main's code.

## Gates

```
$ dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64
    1 Warning(s)   (pre-existing AlwaysOnEngine.cs(901,47) CS8602)
    0 Error(s)
$ dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
Passed!  - Failed:     0, Passed:   268, Skipped:     0, Total:   268
  filter = CliDispatcherTests only:   Passed 154, Failed 0
  filter = everything else:           Passed 114, Failed 0   (= main's Linux count)
```

`AgentEyes.sln` (the Windows suite) cannot build on Linux. Windows is judged from CI, check 4.

## Acceptance criteria

The script for criteria 1-4 is `qa/qa-run-help.sh` (it sources `qa/qa-check.sh`). It ran 44 command lines.

For each one it records:
- `ls -1 ~/Videos/AgentEyes | wc -l` and `ls -1 | tail -5`, before and after
- the full output and the exit code
- `GET /status` State

The full output is in `qa/qa-help-and-refusals.txt`. Its `SUMMARY` lines give **44 of 44** with `count 49 -> 49` and `"State": "idle"`: **29** help lines exit 0 and **15** refusals exit 1.

**Proof the count instrument can move.** The criterion-5 runs on the same instrument went `49 -> 50`, `50 -> 51`, `51 -> 52` and `52 -> 53` with `"State": "recording"`. So "unchanged" is a measurement, not a blind spot.

| # | Criterion | Expected | Actual | Result |
|---|-----------|----------|--------|--------|
| 1 | `record start --help` prints usage, records nothing | usage text, exit 0, dir count unchanged, `/status` idle | Full usage below. exit 0. count 49 -> 49, newest five unchanged (`..._102532_video ... _110130_audio`). `"State": "idle"` | PASS |
| 2 | `--help` on every other subcommand | each prints its help, no side effect | All 18 other table entries, plus `record --help`, `help record start`, `--help`, `-h`: each exit 0, 49 -> 49, idle. The 19 commands are listed under check "table". Side effects outside the recorder are covered below | PASS |
| 3 | `-h` identical to `--help` for `record start` | same text | `record start -h`: same USAGE/OPTIONS text, exit 0, 49 -> 49, idle. Also `record start --audio-only -h`, `--mic -h`, `--mic help`, `RECORD START --HELP`: all help, exit 0, 49 -> 49 | PASS |
| 4 | `record start --nonsense` refused | exit non-zero, names `--nonsense`, mentions `--help`, records nothing | `[error] unknown option --nonsense for 'agenteyes record start'. See: agenteyes record start --help`, exit **1**, 49 -> 49, idle. 14 more refusals, all exit 1, 49 -> 49: `--audio-only --nonsense`, `-x`, `hepl`, `--mic` (no value), `--mic=Yeti`, `--fps=30`, `--region`, `record stop --audio-only`, `video/serve/shot/selftest/signout --nonsense`, `record --nonsense start` | PASS |
| 5 (amended) | correct invocations behave exactly as on main | same output, exit code and Mode from both binaries; `--audio-only --mic` records Mode audio; bare `--audio-only` refused the same way as main | Both binaries run against the same service: **27 output lines each, `diff` empty** once the timestamps are normalised. Bare `--audio-only`: `[error] the service refused: audio requires source mic, system, or mixed (HTTP 400)`, exit 1, no recording, on both. `--screen 1 --mic "Family 17h"`: exit 0, manifest `"Mode": "video"`, on both. `--audio-only --mic "Family 17h"`: exit 0, manifest `"Mode": "audio"`, on both. `record stop`: `[ok] saved ... (4.2s)`, exit 0. `record status`: `state=idle`, exit 0 | PASS |
| 6 | usage text lists every option; one source | all of `--mix --loopback --mic --screen --camera --fps --audio-only` | All seven are listed (below). They match main's `HeadlessCli.Usage` line (`HeadlessCli.cs:23` on main) and the seven keys `RecordStart` reads (`HeadlessCli.cs:68-84`). Help and validation both read the one `record start` entry of `CliCommandTable.All` (`CliDispatcher.cs` `WriteHelp`/`Validate`). Drift is pinned by `Synopsis_EveryCommand_NamesExactlyTheOptionsItAccepts` and `Handlers_ReadExactlyTheFlagsTheirTableEntryDeclares` (M5/M6 in the developer's matrix) | PASS |
| 7 | regression tests in all three suites | help path, no record path, unknown flag refused | 154 cases in ONE file, linked into all three suites (check 3). They fail when the guard is disabled (check 1) | PASS |
| 8 | gates green, with counts | | Linux local 268/0. CI: windows 2297/0, linux 268/0, macos 226/0 (check 4) | PASS |

`agenteyes record start --help`, quoted in full from the real binary:

```
USAGE
  agenteyes record start [--mix | --loopback] [--mic "NAME"] [--screen N] [--camera "NAME"] [--fps N] [--audio-only]

OPTIONS
  --mix                      record the mic AND system audio (needs --mic)
  --loopback                 record system audio only
  --mic NAME                 microphone name fragment (see 'agenteyes screens')
  --screen N                 monitor number (default 1)
  --camera NAME              webcam name fragment
  --fps N                    screen frame rate
  --audio-only               record audio without the screen
  -h, --help                 print this help and exit; no command runs
```

**Side effects the recordings directory cannot see.** `signin`, `signout`, `serve`, `housekeep` and `selftest` would act elsewhere if they ran. I took the CLI's own log for the help/refusal window (`qa/qa-cli-log-help-window.txt`). It has:
- **44** `[CliDispatcher] Run:` lines
- **29** help decisions, **15** usage errors, which together make 44
- 29 `exit=0` and 15 `exit=1`
- **0** `Dispatch: running` lines and **0** `[HeadlessCli]` lines

The same log in the criterion-5 window (`qa/qa-cli-log-correct-window.txt`) shows `Dispatch: running record start|stop|status` for every branch-binary invocation. That proves the log does record a handler when one runs.

After the sweep:
- `~/.local/share/AgentEyes/devthrottle.cred` still has its 2026-10-15 18:21:52 mtime
- `agenteyes account` still says `signed in as ...`
- `pgrep -af "agenteyes serve"` shows the single service process 2122968

## The four checks

### 1. Guard-disabled matrix, reproduced

I re-ran it with the developer's `mutate.sh` (read first; it applies the change, runs the Linux suite, restores the file and touches it). Output: `qa/qa-guard-runs.txt`.

| Mutation | Developer | QA | Failing test names, QA vs `guard-disabled-runs.txt` |
|----------|-----------|----|-----------------------------------------------------|
| M1 help detection off (`WantsHelp` -> `false`) | 69 | **69** (`Failed: 69, Passed: 199`) | sorted name lists identical, `diff` 0 lines |
| M2 `Validate(command, opts)` commented out | 28 | **28** (`Failed: 28, Passed: 240`) | identical, `diff` 0 lines |
| M7 `StorageMigration.Run()` after the dispatcher | 1 | **1** (`Failed: 1, Passed: 267`) | identical: `ProgramMain_RunsTheStorageMigrationBeforeTheDispatcher` |
| QA-M8 (mine): `Log.Info("qa probe");` inserted BEFORE `StorageMigration.Run()` | - | **0 failed** (`Passed: 268`) | see the limit under check 2 |
| restored | 268/0 | **268/0** | |

**Reverted.** After the runs, `git diff HEAD --quiet` returned clean and `git status --short` was empty. The clean-tree suite gave `Passed: 268, Failed: 0`.

### 2. M7 - the migration ordering

- **Code.** `src/AgentEyes.Core/Program.cs`, `Main`: `StorageMigration.Run();` is the first statement, before `return CliDispatcher.Run(...)`. Main's `Program.cs` also had it first.
- **Premise.** `Log.Write` (`src/AgentEyes.Core/Log.cs`) runs `Directory.CreateDirectory(Dir)` where `Dir = AppDataPaths.Root/logs`. `StorageMigration.Migrate` returns early when `Directory.Exists(newDir)`. So any log line written before the migration would skip it for good. The developer's reasoning is correct.
- **The test fires.** Swapping the order fails `ProgramMain_RunsTheStorageMigrationBeforeTheDispatcher` (M7 above).
- **Limit.** The test pins "migration before `CliDispatcher.Run`". It does not pin "migration before ANYTHING that logs". QA-M8 puts a `Log.Info` call ahead of the migration, which is the exact failure mode, and all 268 tests stay green. The test also reads IL order, not execution order.
  - This is not a defect in this PR: the shipped order is correct.
  - It is a gap a future edit to `Main` could slip through.
  - **Follow-up for the gate:** assert that `StorageMigration.Run` is the FIRST call in `Main`'s IL, not merely before the dispatcher.

### 3. The shared test file

It is genuinely ONE file: `find . -name CliDispatcherTests.cs` (excluding bin/obj) returns only `./tests/Shared/CliDispatcherTests.cs`.

Each suite compiles it through a linked `<Compile>` item in its own `<ItemGroup>`:

```xml
<!-- tests/AgentEyes.Tests/AgentEyes.Tests.csproj (Windows) -->
<Compile Include="..\Shared\CliDispatcherTests.cs" Link="Shared\CliDispatcherTests.cs" />
<!-- tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -->
<Compile Include="..\Shared\CliDispatcherTests.cs" Link="Shared\CliDispatcherTests.cs" />
<!-- tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj (forward slashes) -->
<Compile Include="../Shared/CliDispatcherTests.cs" Link="Shared/CliDispatcherTests.cs" />
```

`tests/Shared/` sits outside every project folder, so no default glob picks it up twice. The namespace is `AgentEyes.Tests.Shared`. On Linux, filtering on that class gives exactly 154 tests, and the rest gives 114, which is main's count.

**For #125:** copy this pattern. Put the file under `tests/Shared/` and add one `<Compile Include=... Link=...>` per suite csproj. The file must use only types visible to all three suites.

### 4. PR CI

Two CI runs exist on head `4c9559c`, both green: run 43614383669 (pull_request) and run 43614379486 (push). Counts from the pull_request run's log:

```
windows  Test AgentEyes.sln     Passed!  - Failed: 0, Passed: 2297, Skipped: 0, Total: 2297
linux    Test the Linux build   Passed!  - Failed: 0, Passed:  268, Skipped: 0, Total:  268
macos    Test the macOS build   Passed!  - Failed: 0, Passed:  226, Skipped: 0, Total:  226
```

The baseline is run 43610416654 (the #123 PR head): windows 2143, linux 114, macos 72. That is +154 on each suite, matching the shared file's 154 cases.

## The table against the real command set

There are 19 entries:

`screens`, `shot`, `audio`, `video`, `package`, `compose`, `always-on`, `housekeep`, `import`, `translate`, `subtitle`, `selftest`, `serve`, `record start`, `record stop`, `record status`, `signin`, `signout`, `account`

**That is all of them.**
- Main's `Program.Main` switch had 17 command words plus `-h`/`--help`/`help`.
- Main's `HeadlessCli.Record` had three verbs: `start`, `stop`, `status`.
- 17 - 1 + 3 = 19.
- Every public `int`-returning handler (`Commands.*`, `HeadlessCli.*`, `HeadlessHost.Run`, `SelfTest.Run`) is wired to an entry. None is orphaned.
- `CommandTable_HasEveryCommandTheCliDocuments` pins the list.

## Other findings (not blocking)

1. **Bare `record start --audio-only`.** The lead asked me to confirm it. It is refused with HTTP 400 on main and on the branch alike, so the behaviour is unchanged. But the synopsis still offers `[--audio-only]` as a standalone flag, and nothing works without `--mic`. I consider that a real usability defect, outside this issue. It is worth filing alongside #119 (default microphone).
2. **`handoff.md` has a typo in its counts.** Under criteria 3 and 4 it says "47 -> 49 entries". Its own summary table and `real-binary-help-and-refusals.txt` say 49 -> 49, and my runs measured 49 -> 49. This is documentation only.
3. **Behaviour change.** A positional that is literally `help`, or an option value of `help` or `-h`, now means help. The handoff documents this (pass a directory named `help` as `./help`). It is acceptable.

## Recordings touched by this QA run

- **Created** for criterion 5 and kept as evidence:
  - main binary: `2026-11-14_111948_video` (Mode video) and `2026-11-14_111956_audio` (Mode audio)
  - branch binary: `2026-11-14_112002_video` (Mode video) and `2026-11-14_112010_audio` (Mode audio)
- **Deleted**, as the issue instructs on a pass: `~/Videos/AgentEyes/2026-11-14_093032_video`, the accidental 3.47 s recording (Mode video) this defect made.
  - The count went 53 -> 52 and the path no longer exists.
  - Nothing else under `~/Videos/AgentEyes` was deleted.

VERIFIED - all acceptance criteria met (criterion 5 as amended).
