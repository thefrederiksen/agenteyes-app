# Issue #151 - independent QA, Windows (PR #152)

**Verdict: QA-FAILED, narrowly.** Every acceptance criterion's substance PASSES on the tested head,
including the safety-critical ownership guard, proven adversarially with a positive control. But the
PR **does not merge** into current `main` (497e9d4): three csproj conflicts against #146. Once those are
resolved, the gate numbers this PR writes into CLAUDE.md and the method contract are **wrong on what
lands**, which breaks criterion 2's "exactly those numbers" on `main`. The defect is that rebase plus a
number update. Re-QA only needs that delta. Everything below stands for 2f50acf.

## What was tested

| | |
|---|---|
| Machine | SORENLAPTOP, Windows 11 Home 10.0.26200, not elevated (no kernel process auditing available) |
| Head under test | `2f50acfb3f2373a5a878d8fd7a7d55bff81d7bcd` (branch `issue-151-gate-ffmpeg`, equal to `origin/issue-151-gate-ffmpeg` at test time) |
| Embedded commit in the binary | `tests\AgentEyes.Tests\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.dll` ProductVersion = `1.11.11+2f50acfb3f2373a5a878d8fd7a7d55bff81d7bcd` |
| Stale-binary trap | `bin\Release\...\agenteyes.dll` on this clone is `1.11.11+b9bdec1...` (stale). Every run here used the `x64` output |
| SDK | 10.0.301. No .NET 8 SDK is installed (9.0.306, 10.0.103 and 10.0.301 are). The projects target `net8.0` / `net8.0-windows10.0.19041.0` and build cleanly with the 10.x SDK |
| Runtime | **Microsoft.NETCore.App 8.0.21**. Confirmed by a net8.0 probe printing `.NET 8.0.21`. The tests run on .NET 8, not on a roll-forward to 9 or 10, so the counts are representative |
| ffmpeg used by the gate | winget Gyan 8.1.1 full_build (what `FfmpegLocator` finds on this machine). Criterion 3 was also run against 9.0.2 (a copy of the installed product's ffmpeg), see below |
| Laptop app | AgentEyesApp pid 16160 running; `/status` = `idle` before and after every ffmpeg-touching run; 0 ffmpeg processes before and after each adversarial run |

Gate on the head: `dotnet build AgentEyes.sln -c Release` -> **0 Error(s)**. `dotnet test AgentEyes.sln -c Release`
(no shim) -> **Passed 2371, Skipped 11, Total 2382, Failed 0, 1 m 5 s test time, 69 s wall**.

## Instrument: the counting shim

`qa-windows/counting-shim-Program.cs` is a net8.0 apphost named `ffmpeg.exe`, copied as `ffprobe.exe`.
For each start it writes ONE file to `SHIM_LOG_DIR`, then runs the real tool from `SHIM_REAL_DIR` with
inherited std handles and forwards the exit code. The shim folder is set on BOTH `QA_RECORD_FFMPEG_DIR`
and the front of `PATH` (`qa-windows/rungate.sh`). One file per process means concurrent starts cannot
lose a line.

- **Positive control** (`shim-positive-control.txt`): 3 ffmpeg runs, 1 ffmpeg run with a bad flag (exit 8
  forwarded) and 1 ffprobe gave 4 ffmpeg files and 1 ffprobe file. Every launch was counted.
- **ffprobe is counted separately, not as ffmpeg.**
- **What the shim cannot see:** a launch by absolute path that bypasses both `QA_RECORD_FFMPEG_DIR` and
  `PATH`. Checked statically: `FfmpegLocator` step 0 (an ffmpeg next to the test binary) finds nothing,
  because no `ff*.exe` is in the test output folder. A grep of `src` and `tests` for ffmpeg process
  starts not built from `FfmpegLocator` found none. The test-side direct `Process.Start` calls
  (`MicChainStallTests.cs:421`, `:442`) take their path from `FfmpegLocator.Ffmpeg()`. So the count is
  a total for launches routed through the locator, and a floor in principle. I found no route around it.

## Criterion 1 - default gate launches no ffmpeg (as AMENDED by the lead: Linux proven both arms; Windows measured and documented)

| Arm | Expected | Actual (this machine, same shim both times) |
|---|---|---|
| Windows default gate, head 2f50acf | measured, documented | **235 ffmpeg starts** (+115 ffprobe). 2382 tests, 0 failed. Zero `agenteyes-128-*` roots (`roots-default.txt`) |
| Windows opt-in (`AGENTEYES_FFMPEG_TESTS=1`), head | non-zero, higher | **297 ffmpeg** (+127 ffprobe). 2383 tests, 0 failed, 0 skipped |
| Difference | = the moved tests | 297 - 235 = **62** = 55 `agenteyes-128` (MicChainStallTests) + 4 `-version` probes (`MicChainStallTests.FfmpegVersion`, opt-in) + 3 with no temp root (the OwnedFfmpegTests hazard test: owned, recorder, parallel) |
| Linux default gate | 0, proven both arms | **Not measured by me** (no Linux machine here). Checked by review and CI instead: the only ffmpeg launcher compiled into `AgentEyes.Tests.Linux` is `OwnedFfmpegTests.KillOwned_FailurePath_...`, an `[FfmpegOptInFact]`. `FfmpegMuxShapeTests` only checks argument shapes. CI at 2f50acf: Linux default 287 passed + 1 skipped, opt-in 288/288. The developer's strace evidence (`linux-gate-both-arms.txt`) is the measured arm |
| Governance documents say the Windows gate is not ffmpeg-free | stated in both | CLAUDE.md:68-76 and DEVELOPMENT_METHOD.md:252-254 both say it, with ~235 starts |

**PASS** on 2f50acf. The Windows count matches the developer's kernel-audit count (235) to the process,
measured here with a different instrument.

### #153: the 235 vs 276 discrepancy - CONFIRMED as a commit effect, with one correction

Same machine and same shim, b9bdec1 built in a separate worktree (`1.11.11+b9bdec1...`), default gate:
**292 ffmpeg** (`roots-b9b-default.txt`). Root by root against the head (`roots-default.txt`):

| root | b9bdec1 | 2f50acf | |
|---|---|---|---|
| `agenteyes-128` | 55 | **0** | moved behind the opt-in by this PR |
| `(-version)` | 4 | **0** | `MicChainStallTests.FfmpegVersion`, moved with it |
| `agenteyes-136` | 29 | 31 | run-to-run jitter, the only other difference |
| every other root | identical | identical | |

292 - 55 - 4 + 2 = 235. **The hypothesis holds:** the drop is exactly #128's tests leaving the default
gate, and nothing else moved. **The correction:** the absolute numbers depend on the machine. Here
b9bdec1 is 292 with 55 `agenteyes-128` roots, not 276 with 39. So "276" was that seat's machine, and
should not be quoted as the b9bdec1 figure in general. (That b9bdec1 run also had one failure:
`MuxTwoPassTests.PassTwo_ReallyWritesTheMuxingTemp_AndNotTheFinalPath`, an IOException on
`models\bd.rnnn.tmp` "being used by another process". This is a model-materialisation race between
parallel tests on b9bdec1, `b9b-default-flake.txt`. It did not occur in any run on the PR head.)

## Criterion 2 - measured count and duration, three platforms; CLAUDE.md exact

| Platform | CLAUDE.md says | Measured |
|---|---|---|
| Windows (CI) | 2382 tests, 11 skipped, 33-50 s; opt-in 2383 | CI at 2f50acf, runs 43749854171 and 43749858638: 2382 (2371 + 11 skipped) in 39 s and 39 s; opt-in 2383/2383 in 54 s and 46 s. Matches |
| Windows (this laptop) | - | 2382 / 11 skipped, 1 m 5 s test time with no shim (1 m 31 s under the shim). The laptop is slower than CI. CLAUDE.md labels its figure as CI's, which is honest |
| Linux (CI) | 288 tests, under a second | 287 + 1 skipped, 552 ms / 502 ms; opt-in 288/288, 2 s. Matches |
| macOS (CI) | 280, under a second | 279 + 1 skipped, 246 ms / 478 ms. Matches |

**PASS on the branch. FAILS on what lands. This is the defect, see below.**

## Criterion 3 - opt-in: every #128 guard runs and passes, with count; a framing mutation fails them by name

Head 2f50acf, `MicChainStallTests` plus `OwnedFfmpegTests`:
- Default: 32 total, 21 passed, **11 skipped** (10 MicChainStallTests methods, one a 2-case theory, plus 1 OwnedFfmpeg).
- Opt-in: **33/33 passed, 0 skipped**. MicChainStallTests alone: 27 cases (16 non-ffmpeg + 11 ffmpeg cases).

Mutation A (`FfmpegArgs.cs:420` `asetnsamples=n=480:p=1` -> `p=0`, in a throwaway worktree, never committed):

| ffmpeg | Unmutated | Mutated |
|---|---|---|
| **9.0.2** (the shipped build, with `CI=true` so the test also asserts that the bare defect reproduces) | **27/27 passed**. Bare arnndn: 480 of 480480 (mono) and 960 of 960960 (stereo) non-finite; framed: 0 | **5 failed by name:** `ArnndnFraming_PartialFinalFrame_ArnndnEmitsNoNonFiniteSample(mono)`, `(stereo)` (framed now 480 / 960 non-finite), `MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan` ("ffmpeg stalled during mix mic+system ... process killed"), `ProcessVideoMic_DefaultSettingsFiniteS16MonoNotAWholeFrame_Completes`, `MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames` |
| 8.1.1 (winget) | passes | only `MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames` fails. 8.1.1 does not have the arnndn defect, as the test itself documents, so the real-ffmpeg guards cannot bite on it |

**PASS.** The guards still bite, and I reproduced it locally on the ffmpeg that ships. The developer's
CI run showed 4 failures, and this run found one more (`ProcessVideoMic_...`). Evidence:
`c3-902-unmutated-summary.txt`, `c3-902-mutA-summary.txt`, `c3-mutA-summary.txt`.

## Criterion 4 - the kill is own-pid-only (ADVERSARIAL, with a positive control)

Harness: `qa-windows/adversarial.ps1`. It runs `MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound`
under **mutation B** (`FfmpegProgressWatchdog.IsStalled` always false, so the stall guard never fires
and the test's failure path runs). It waits until the shim logs the test's own stalling ffmpeg
(`-progress`). Then, 2 s later, it starts a **FOREIGN** ffmpeg directly from the real binary, not
through the suite and not through the shim (`-re` sine for 600 s to null). That foreign ffmpeg starts
after the test's own ffmpeg, so it is inside the old sweep's start-time window. It checks the foreign
pid AND its start time after `dotnet test` returns, then kills the foreign process by its own handle
(QA started it).

| Run | Code | Foreign ffmpeg | Result |
|---|---|---|---|
| **Positive control** (`adv-old-90c9118-mutB.txt`) | 90c9118 (the PR's base, with the old `KillFfmpegStartedSince`) + mutation B | pid 19656, started 07:20:11.23, 2 s after the test's ffmpeg | **DEAD.** The old sweep logged `killed leaked ffmpeg pid 9408`, `5480`, **`19656`** (foreign exit code -1). The hazard is reproduced on Windows, and the check can fail |
| **Under test** (`adv-new-2f50acf-mutB.txt`) | 2f50acf + mutation B, opt-in | pid 15868, started 07:21:29.00, 2 s after the test's ffmpeg | **ALIVE after the failure path.** The test logged only `failure path: pid 3288: killed` (its own run) and failed by name: "RunWatched was still running after 60 s - the stall guard did not fire" |

Review (file:line): the sweep is gone (`MicChainStallTests.cs`, old lines 225-243 deleted). `OwnedFfmpeg`
(`tests/Shared/OwnedFfmpeg.cs:36-62`) kills only pids recorded through `Ffmpeg.ChildStartedObserver`
(`src/AgentEyes.Core/Video/Ffmpeg.cs:93-98`), and only while pid plus start time still match
(`FfmpegChildRegistry.IsSameProcess`, 1 s tolerance). A child whose start time was never read is left
alone. The observer is an `AsyncLocal`, so a parallel test's flow and the product cannot feed it.
`TestSources_NoTestSweepsEveryFfmpegOnTheMachine` guards against the sweep coming back and checks its
own instrument for presence. On Windows the PR's own hazard test (owned, recorder and parallel, all
three asserted to be inside the old selection) passed in every opt-in run here.

**PASS.**

## Criterion 5 - no orphan

In the mutation-B run on the head, the owned run was killed as a tree (shim pid 3288 and its real
ffmpeg child). `ffmpeg processes after: 0` once QA had removed its own foreign process, and the suite
returned in about 66 s rather than hanging. **PASS.**

## Criterion 6 - gates green, counts, CI more than one run

Locally on 2f50acf: build 0 errors; default 2382 (0 failed) twice (with and without the shim); opt-in
2383/2383. CI: 2f50acf green on all three jobs, push 43749854171 and pull_request 43749858638, plus earlier
heads adc99ec and e83ddb1 (two runs each). **PASS on the branch.**

## THE DEFECT: the PR does not merge into current main, and its numbers are wrong once it does

1. **Merge conflicts** with `main` 497e9d4 (#146, merged after this branch was cut from 90c9118). Both
   sides add `<Compile>` lines at the same spot:
   - `tests/AgentEyes.Tests/AgentEyes.Tests.csproj:66-71` (this PR: OwnedFfmpeg links; main: `Shared/MicChainOrderTests.cs`)
   - `tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj` (OwnedFfmpeg and FfmpegOptInAttributes links vs MicChainOrderTests)
   - `tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj` (same)
   The resolution is keep-both. `FfmpegOptInAttributes.cs` is identical on both sides and merges clean;
   CLAUDE.md auto-merges.
2. **After that resolution, criterion 2's numbers are false on `main`.** I resolved it locally (keep-both,
   never pushed) and measured it with the same shim:

   | | CLAUDE.md:69-74 / DEVELOPMENT_METHOD.md:253-254 say | merged result measured |
   |---|---|---|
   | Windows default | 2382 tests, 11 skipped | **2390 tests, 15 skipped** (2375 passed) |
   | Windows opt-in | 2383 | **2394** (2394/2394) |
   | Windows default ffmpeg | about 235 | **235** (unchanged; #146's tests are opt-in) |
   | Linux / macOS | 288 / 280 | **will change**: main links `MicChainOrderTests.cs` into both. Not measurable here, so the number must come from CI on the merged head |

   **Fix:** merge `main` into the branch, keep both sides of each conflict, then take the Windows, Linux
   and macOS counts and durations from CI on the new head. Put them in CLAUDE.md:68-76 and
   DEVELOPMENT_METHOD.md:252-254. No product or test code needs to change.

The first opt-in run on the merged tree had one unrelated failure:
`PreviewTapTests.Dispose_WhileThePublisherIsWedged_ReturnsAndDoesNoFileWorkOfItsOwn`, with
`UnauthorizedAccessException` on `screen.jpg` in teardown (`merge-optin-run1-flake.txt`). It is not
caused by this PR. Five isolated reruns of PreviewTapTests gave 24/24, and the full merged opt-in rerun
gave 2394/2394. It is recorded as a teardown flake that someone should track.

## Observations (not defects of this issue)

- `MuxCoordinatorTests` (#128 round two, root `agenteyes-128r2`) still starts 2 ffmpeg in the default
  gate. This is in scope for #153, not #151: the issue's IN list names only `MicChainStallTests.cs`.
- The default gate also starts about 115 **ffprobe** processes, which no earlier count included. #153
  should treat these as part of what the gate costs.
- `RepairService` calls `FfmpegChildRegistry.ReapOrphans()` against the real `%LOCALAPPDATA%` folder, but
  it kills only children whose recorded owner process is provably gone. A live recorder's ffmpeg is
  owned by the live app and is left alone. I did not exercise this here.

## What I did NOT cover

- **The Linux arm of criterion 1 was not independently measured.** I have no Linux machine. The verdict
  there rests on code review, CI test counts and the developer's strace evidence.
- **macOS** was checked only by CI counts and review.
- **Kernel-level process accounting.** This seat is not elevated, so the count rests on the shim plus a
  static bypass check. A launch by an absolute path that does not come from `FfmpegLocator` would not be
  counted. I found none, but the count is a floor in principle.
- **The owner's machine (SOREN_NORTH)** was not touched. The adversarial test used a foreign ffmpeg that
  QA started on this laptop as a stand-in for a live recording. A real AgentEyes recording was not used.
- **The merged result** was measured locally only. Its CI, and its Linux and macOS counts, do not exist
  yet.
- **No audio quality** was judged. This laptop's mic records silence, and nothing here needed it.

## Evidence index (`docs/cencon/proof/issue-151/qa-windows/`)

`counting-shim-Program.cs`, `counting-shim-ffmpeg.csproj`, `rungate.sh`, `adversarial.ps1` (instruments) -
`shim-positive-control.txt` - `shim-{default,optin,b9b-default,merge-default,merge-optin2}-ffmpeg.txt` (one
line per ffmpeg start) - `roots-{default,optin,b9b-default,merge-default}.txt` - `adv-old-90c9118-mutB.txt`,
`adv-new-2f50acf-mutB.txt` - `c3-*-summary.txt` - `b9b-default-flake.txt`, `merge-optin-run1-flake.txt`.
