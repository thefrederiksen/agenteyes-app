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

---

## Pass 2 - the qa-failed delta (merge main, correct the counts, correct the ffmpeg sentence)

Windows seat, SORENLAPTOP. Input: QA's `flow:qa-failed` at `3084271` (`qa-report-windows.md`,
comment 6916669389). The scope is that delta only. **Pass 2 changes no product code, no test code,
no opt-in mechanism and no kill-ownership code.** The only change outside the docs is three csproj
lines, and they come from main.

### 1. Merge of origin/main 497e9d4 - merge commit `6a65fa1`

There were three conflicts. All three sit at the same `<Compile>` insertion point, right after the
`FfmpegMuxShapeTests.cs` link:

| File | Ours (#151) | Theirs (main, #146) | Resolution and why |
|---|---|---|---|
| `tests/AgentEyes.Tests/AgentEyes.Tests.csproj` | links `Shared/OwnedFfmpeg.cs` and `Shared/OwnedFfmpegTests.cs` | links `Shared/MicChainOrderTests.cs` | Keep both. These are three unrelated source files, and neither side links the other's. Dropping either side would remove a test class from the Windows gate |
| `tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj` | the two OwnedFfmpeg links plus `../AgentEyes.Tests/FfmpegOptInAttributes.cs` | `MicChainOrderTests.cs` | Keep both. Main's Linux csproj does not link `FfmpegOptInAttributes.cs`, so it is linked once and no `Compile` item is duplicated. `MicChainOrderTests` uses plain `[Fact]` and no opt-in attribute, so it needs nothing from our side |
| `tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj` | same as Linux | same as Linux | Keep both, for the same reasons |

Both sides added `tests/AgentEyes.Tests/FfmpegOptInAttributes.cs`. `git diff HEAD origin/main` on
that file is empty, so the two copies are byte-identical and it did not conflict. CLAUDE.md merged
automatically: it brought in main's D7 changes (the Review Gate, `flow:ready-gate`) and kept our gate
passage. After resolving, I checked that no conflict markers remain and that none of the three files
has a duplicate `Compile Include`.

### 2. Counts I measured on the merged head (not copied from QA)

Windows, on this machine: **SDK 10.0.301 building net8.0; the tests run on
Microsoft.NETCore.App 8.0.21.** The binary is `bin\x64\Release\...\AgentEyes.Tests.dll`, built
07:42 on the day. The stale `bin\Release` copy is dated 2026-11-15, and the "Test run for" line
names the x64 path.

| Arm | Total | Passed | Skipped | Failed | Test time | ffmpeg / ffprobe starts (shim) |
|---|---|---|---|---|---|---|
| Default, no shim | 2390 | 2375 | 15 | 0 | 1 m 5 s | - |
| `AGENTEYES_FFMPEG_TESTS=1`, no shim | 2394 | 2394 | 0 | 0 | 1 m 17 s | - |
| Default, shim | 2390 | 2375 | 15 | 0 | 1 m 21 s | **235 / 115** |
| Opt-in, shim | 2394 | 2394 | 0 | 0 | 2 m 28 s | **322 / 128** |

The opt-in arm skips nothing, so all 15 default skips are opt-in tests. A skipped Theory reports as
one row, which is why 2390 + 4 = 2394.

The instrument is QA's `qa-windows/counting-shim-Program.cs`, rebuilt unchanged and run through
`qa-windows/rungate.sh`, with ffmpeg 8.1.1 as the real tools. My own positive control: 4 ffmpeg
starts (one with a bad flag, exit 8 forwarded) and 1 ffprobe start were counted as 4 and 1. No
ffmpeg or ffprobe was alive after either run. QA's limit still applies: a launch by absolute path is
invisible, so these counts are a floor in principle. Evidence: `dev-pass2/windows-gate-merged.txt`.

**Linux and macOS come from CI and were not measured here** (those suites cannot run on this
machine). Source: the CI job logs on merge commit `6a65fa18af8955e45fd1f5ca7105f977513de43b`.

| Suite | Run | Job | Default | Opt-in |
|---|---|---|---|---|
| Windows | pull_request 43870216038 | 133309954668 | 2390 (15 skipped), 39 s | 2394 (0 skipped), 46 s |
| Linux | pull_request 43870216038 | 133310001368 | 292 (1 skipped), 780 ms | 292 (0 skipped), 2 s |
| macOS | pull_request 43870216038 | 133309957336 | 284 (1 skipped), 421 ms | (CI runs one arm) |
| Linux | push 43870208914 | 133309946542 | 292 (1 skipped), 604 ms | 292, 2 s |
| macOS | push 43870208914 | 133310011099 | 284 (1 skipped), 788 ms | - |

The Windows job of push run 43870208914 failed twice (jobs 133309062692 and 133309944571). Both
times it failed in "Download ffmpeg (essentials build)" with `www.gyan.dev 503 Service Unavailable`,
before the build or any test. That is an infrastructure failure, and no test ran. The pull_request
run's Windows job supplies the Windows CI figure. Evidence: `dev-pass2/ci-merged-head.txt`. Linux
goes from 288 to 292 and macOS from 280 to 284 because #146's 4 `MicChainOrderTests` facts are
linked into both suites.

### 3. The governance wording, before and after

**CLAUDE.md, gate policy tier 1.** Before (the branch at 3084271, lines 69-77):

> Measured on the Windows CI runner (issue #151, 2026-11-15): 2382 tests (11 skipped by design) in
> 33-50 s of test time (six runs). It is silent (no app launch, no audio) but it is NOT ffmpeg-free:
> end-to-end tests outside the mic chain start about 235 short ffmpeg runs. [...] (2383 tests, about a
> minute; CI and the release build always do). The Linux and macOS gates [...] are 288 and 280 tests
> in under a second and start no ffmpeg.

On main, the same passage reads "288 tests in ~2 seconds, no app launch, no audio, no ffmpeg". That
is the false claim the lead ruled on. Pass 1 had already removed "no ffmpeg" from the branch, and the
merge kept the branch's text.

After:

> It is silent (no app launch, no audio) but it is NOT ffmpeg-free. Measured by issue #151 on main
> 497e9d4 merged into the #151 fix (2026-11-16): 2390 tests, 15 of them skipped (the opt-in tests
> below), in 39 s of test time on the Windows CI runner (run 43870216038) and 65 s on a laptop (SDK
> 10.0.301 building net8.0, runtime 8.0.21). On that laptop, a counting shim on ffmpeg and ffprobe saw
> the default gate start **235 ffmpeg and 115 ffprobe** processes, all short end-to-end test runs. The
> tests that exist only to run a real ffmpeg against the mic chain (#128, #146) are opt-in: set
> `AGENTEYES_FFMPEG_TESTS=1` to run them too (2394 tests; 46 s on CI, 77 s on the laptop; 322 ffmpeg
> and 128 ffprobe starts). CI and the release build always run them. What the default gate should
> start, if anything, is an open policy question tracked in issue #153 and NOT settled here. The Linux
> and macOS gates [...] are 292 and 284 tests (1 opt-in skip each), in under a second on CI (run
> 43870216038). They start no ffmpeg (Linux traced with strace in #151; macOS by source review).

The hard rule block ("THE HUMAN NEVER RUNS TESTS. EVER.") is untouched.

**docs/cencon/DEVELOPMENT_METHOD.md, dev gate item 4.** Before:

> What `dotnet test AgentEyes.sln` costs, measured (issue #151, 2026-11-15, Windows CI runner): 2382
> tests in 33-50 s, silent (no app launch, no audio) but NOT ffmpeg-free - end-to-end tests start about
> 235 short ffmpeg runs. The tests that exist only to run a real ffmpeg against the mic chain are opt-in
> (`AGENTEYES_FFMPEG_TESTS=1`); CI and the release build always run them.

After:

> What `dotnet test AgentEyes.sln` costs, measured (issue #151, 2026-11-16, main 497e9d4 merged into
> the #151 fix): 2390 tests, 15 skipped by default, in 39 s on the Windows CI runner (run 43870216038)
> and 65 s on a laptop (SDK 10.0.301 building net8.0, runtime 8.0.21). It is silent (no app launch, no
> audio) but NOT ffmpeg-free: on that laptop a counting shim saw the default gate start 235 ffmpeg and
> 115 ffprobe processes. The tests that exist only to run a real ffmpeg against the mic chain are opt-in
> (`AGENTEYES_FFMPEG_TESTS=1`: 2394 tests, 322 ffmpeg and 128 ffprobe starts); CI and the release build
> always run them. What the default gate should start is an open policy question, issue #153. The Linux
> and macOS suites are 292 and 284 tests on CI (same run).

The sentence that follows, about the old "288 tests in ~2 seconds, no ffmpeg" figure, is unchanged,
and so is the hard rule block.

### Noted, not touched (outside this delta's scope)

- The developer-agent skill (`SKILL.md:138`) and the implementation-loop skill (`SKILL.md:214`)
  still describe `dotnet test` as "~2s, no app launch, no audio". That is the same stale figure, in
  the agents' own skills. Neither says "no ffmpeg", but "~2s" is false on Windows (39-65 s). This
  belongs to #153 or a docs follow-up.
- The opt-in arm's 322 ffmpeg starts are more than QA's 297 at the pre-merge head `2f50acf`, because
  #146's `MicChainSampleRateTests` are opt-in and launch ffmpeg. The default arm is 235 at both heads.

### How QA should re-verify the delta

1. `git diff 3084271 6a65fa1 -- tests/*/*.csproj` should show exactly one added
   `MicChainOrderTests.cs` line per file and nothing removed. `git diff 6a65fa1 HEAD --stat` should
   touch only `CLAUDE.md`, `docs/cencon/DEVELOPMENT_METHOD.md` and `docs/cencon/proof/issue-151/`.
2. In `src` and `tests`, the only changes since `2f50acf` should be main's (`90c9118..497e9d4`) plus
   the three csproj hunks. Nothing QA verified was touched except by main.
3. Optionally, re-run both Windows arms with the shim for your own counts. Then check CI on the final
   head.
