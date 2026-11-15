# Issue #151 - Developer handoff to QA

Branch `issue-151-gate-ffmpeg`, off `main` 90c9118. Linux seat, devlinux. The Windows and macOS
evidence below comes from GitHub Actions, not from a machine of mine, and says so each time.

## The change

**The kill (defect b).** `Ffmpeg.RunCore` reports every child it starts to a test-only observer,
`Ffmpeg.ChildStartedObserver`. It's an `AsyncLocal` like the existing `PostProcessStallTimeoutOverride`,
so it flows into the test's own `Task.Run` and a parallel test or the product never sees it.
Production never sets it. `tests/Shared/OwnedFfmpeg` records the pid and start time of each child
started in that test's flow. `KillOwned()` kills exactly those, and only while the pid still names the
same process (`FfmpegChildRegistry.IsSameProcess`, made internal and reused, not copied). A child it
cannot prove is its own is left alone. The sweep over `GetProcessesByName("ffmpeg")` is gone.

I found no reason pid tracking could not work. Every ffmpeg that the #128 failure path has to kill is
started by `Ffmpeg.RunWatched`, so it passes through `RunCore`. No test needs to kill a process it did
not start.

**The gate (defect a).** The ten `MicChainStallTests` that launch ffmpeg are `[FfmpegOptInFact]` /
`[FfmpegOptInTheory]` behind `AGENTEYES_FFMPEG_TESTS=1`: 9 facts and the 2-case `ArnndnFraming`
theory. `FfmpegOptInAttributes.cs` is #146's file byte for byte (PR #150), so the two branches merge
as an identical add. The 16 tests that need no ffmpeg (shape, watchdog, compiled-code, manifest) stay
in the gate.

**Not deleting the guards.** `ci.yml` runs the Windows suite a second time with the opt-in set. The
Linux job installs ffmpeg and runs its opt-in tests. `release.yml`'s test step sets the opt-in.
Without that last one, moving the guards would have silently dropped them from every release build.

## Expected vs Actual

| # | Criterion | Actual | Evidence |
|---|---|---|---|
| 1 | The default gate launches no ffmpeg, shown as a presence with both arms | **Linux: met.** strace over the whole `dotnet test` tree: default arm 0 ffmpeg execs (3 execs in total, all `dotnet`); opt-in arm 3 ffmpeg execs. **Windows: NOT met, and it can't be within this issue's scope.** Kernel process auditing with a 3-of-3 positive control: the default gate starts **235** ffmpeg, the opted-in pass **279**. #128's tests were about 41 of those. The other 235 come from end-to-end tests in other files (AlwaysOn\*, Decay, Housekeeper, FrameConversion, MuxTwoPass, CameraFailurePath), which this issue leaves out. The premise that the gate was ffmpeg-free before #128 doesn't hold. **macOS:** its suite contains no ffmpeg-launching code (source grep); not instrumented on CI. | `linux-gate-both-arms.txt`, `windows-ffmpeg-starts.txt` |
| 2 | Measured count and duration on all three platforms; CLAUDE.md updated to exactly those | **Windows (CI):** 2382 tests (2371 passed, 11 skipped by design), 33-50 s test time over six runs (36.4 s wall in the measured run); with the opt-in, 2383 tests in 44-68 s. **Linux (devlinux):** 288 tests (287 passed, 1 skipped), 0.25-0.7 s test time, 4.6 s wall; with the opt-in, 288 in 3 s. **macOS (CI):** 280 tests (279 passed, 1 skipped), 0.15-0.3 s. CLAUDE.md now states these numbers and says plainly that the Windows gate is not ffmpeg-free. | CI runs below; `linux-gate-both-arms.txt` |
| 3 | With the opt-in, every #128 guard runs and passes, with its count; mutate the framing and show them fail by name | **Pass:** the opted-in Windows pass is 2383 of 2383 passed, 0 skipped, so all 26 `MicChainStallTests` methods (27 cases) ran and passed, on every run at f87341c, 242a441 and 6dd5ac6. **Mutation A** (`ArnndnFraming` loses `p=1`): the opt-in pass fails `ArnndnFraming_PartialFinalFrame_ArnndnEmitsNoNonFiniteSample` (mono and stereo), `MixWavs_DefaultSettingsMicWithNanSample_CompletesAndKeepsTheMicAfterTheNan` and `MicChain_Denoise_HandsArnndnOnlyWholePaddedFrames` by name; macOS fails 5 `MacMicAlignmentTests` pins. | `c3-mutation-a-framing-windows.txt` |
| 4 | Kill is own-pid-only; an unrelated long ffmpeg survives the failure path | **Reproduced first, on main:** the verbatim old sweep killed an unrelated ffmpeg started during the test (pid 2732944) together with the owned run. **After:** `KillOwned_FailurePath_KillsTheStuckRunAndLeavesUnrelatedFfmpegRunning` starts three ffmpeg after the test begins: the stuck owned run, an unrelated "recorder", and a parallel test's run under its own scope. The test asserts that all three are in the old sweep's selection, then that the failure path kills only the owned one and the other two are still running. Passes on Linux (devlinux and CI) and on Windows CI. A real foreign ffmpeg on devlinux (go2rtc, pid 2197930) ran throughout and was never touched. Pure tests pin the decision: only recorded pids are probed or killed; a reused pid or one with an unread start time is left alone. A source scan fails if any test sweeps ffmpeg by name again. | `c4-hazard-on-main-linux.txt`, `linux-gate-both-arms.txt`, `foreign-ffmpeg-on-devlinux.txt` |
| 5 | No orphan: forcing the failure path leaves no spinning ffmpeg | **Mutation B** (the stall guard never fires, #128's mutation-B shape) on Windows CI: `RunWatched_FilterThatStallsOnNan_FailsWithinTheBound` fails by name at 1 m, its log shows `failure path: pid 4996: killed`, the suite returns, and the step listing every ffmpeg on the runner afterwards shows **0**. On Linux, the bystander test asserts the owned pid is gone after the kill, and `ps` after the run shows only the foreign go2rtc ffmpeg. | `c5-mutation-b-noguard-windows.txt`, `linux-gate-both-arms.txt` |
| 6 | Gates green on all three platforms with counts; CI green across more than one run | **devlinux:** `dotnet build src/AgentEyes.Core -c Release -f net8.0`: 0 errors (1 warning, the existing CS8602 at AlwaysOnEngine.cs:901). `dotnet test tests/AgentEyes.Tests.Linux -c Release`: 287 passed, 1 skipped; with the opt-in, 288 of 288. **CI:** all three jobs green at f87341c, 242a441, 6dd5ac6 and e83ddb1 (push and pull_request runs), and at the final head (see the PR checks). `AgentEyes.sln` was not built on this machine: it contains the WPF app, and this SDK has no WindowsDesktop pack. Its build and test are the Windows CI job. | PR checks |

## Found on the way, not fixed (scope)

1. **The Windows default gate starts 235 ffmpeg outside #128.** Criterion 1 can't be met without
   moving those tests too. That needs a decision: a follow-up issue, or an amendment to this one.
2. **`docs/cencon/DEVELOPMENT_METHOD.md:252`** still says "288 tests in ~2 seconds, no app launch,
   no audio, no ffmpeg". That's the same stale sentence, in the governance contract. I left it.
3. **`release.yml:39-42` states a policy, "the suite refuses to skip - a skip is indistinguishable
   from a pass in every report".** The opt-in convention (#146, and now #151) is a deliberate skip.
   This PR keeps release builds honest by setting the opt-in there, but the stated policy and the
   convention now disagree.
4. **Thread-pool starvation on the Windows runner is real.** A `Task.Run` in my test waited more
   than 15 s for a thread. Any test that bounds a `Task.Run` tightly is exposed.

## How QA should verify

- Linux: `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0`, then
  `dotnet test tests/AgentEyes.Tests.Linux -c Release` (expect 287 passed, 1 skipped), then the same
  with `AGENTEYES_FFMPEG_TESTS=1` (expect 288 of 288). For criterion 4, run with
  `--filter "FullyQualifiedName~OwnedFfmpeg" --logger "console;verbosity=detailed"`. Before you kill
  anything you didn't start, capture `ps -eo pid,ppid,pcpu,etime,args | grep [f]fmpeg`.
- Windows: the gate as written in CLAUDE.md. The opt-in pass is
  `set AGENTEYES_FFMPEG_TESTS=1` then `dotnet test AgentEyes.sln -c Release`.
- To repeat the hazard on main, use the scratch test in `c4-hazard-on-main-linux.txt`.
