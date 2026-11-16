# Issue #161 passes 4 and 5 - QA report, LINUX (devlinux)

- **Verdict: FAIL -> flow:qa-failed, on ONE item (D-P5 below), ruled a defect by the Lead (reply to QA, 21:24Z).**
  Everything else in passes 4 and 5 verifies on Linux, and the whole guard still holds.
- **Scope: LINUX, and the delta of passes 4 and 5.** The Windows derivation and the Windows start-time measurement are
  still owed and are being routed separately by the Lead. Nothing here measures Windows. macOS is CI only.
- **Head tested:** `22ec8cb9d5e05e28ffe6d31a0ae6b5d0f49bef5f` (PR #175). Code is `5eaf3fd`: `22ec8cb` changes only `handoff.md`.
- **Binary run:** `agenteyes.dll`, built here at that head under Landlock, embeds `1.11.11+22ec8cb9d5e05e28ffe6d31a0ae6b5d0f49bef5f`.
  The pass-4 comparison binary was built from `git archive 6ee962d` and embeds `1.11.11+6ee962d493577cf3fe86f1f5d18f45fa98850a5d`.
- **Last QA-verified head:** `f9aba6a` (pass 2). Pass 3 was accepted by the Lead but not verified by a QA seat. Its rules
  (marker, F2 links, banner, rule 1) are re-verified here by behaviour (job 5), not by a pass-3 review.
- Artefacts: `qa-linux-pass5/` (harness in `qa-linux-pass5/harness/`). Paths are redacted to `<home>`, `<qa-scratch>` and `<tmp-scratch>`.

## Verdict per job

| Job | Verdict |
|---|---|
| 1 Timeout behaviour | **PASS. Reproduced.** All five shapes, 10 runs each, pass 4 and pass 5 binaries side by side, in a `--network none` container. Every row matches the developer's within load noise. The +60 ms on connect-never-completes is confirmed by an interleaved A/B. |
| 2 The injection | **PASS on connects, FAIL on binds (D-P5).** **0 connects to 7882** in the Linux default, Linux opt-in and DesktopApp gates and in every harness run. Each count comes with a canary in the SAME trace that shows exactly 1, and a container control that shows the identical counter catching a real connect to 7882. But the suite **binds 7882** twice per Linux run, and where 7882 is free the twin test IMPERSONATES the service: a third-party `POST /record/start` got **HTTP 200**. |
| 3 Serialisation | **PASS.** The loopback collection runs in parallel again. 61 of its 146 tests overlap tests of other classes (479 overlaps), against 0 in a serial-control run of the same build. The stand-in threads stay, per #189. |
| 4 Nothing else moved | **PASS.** Since pass 3 (`c86081c`), 8 files changed, all on-scope, with no merges. Every untouched member of `LibraryRedirect.cs`, `RealLibrary.cs` and `FfmpegChildRegistry.cs` is byte-identical. The one #156 line changed is pass 4's geteuid consolidation (same syscall). The Lead's seven no-diff files have 0 diff lines. |
| 5 The whole guard | **PASS.** D1 attack (`serve`) and x1-x4, plus y1/y5/y8: exit 5, **0 opens** of a real root. N1 refuses, and the write probe runs LAST, after the overlap, link AND marker rules (shown with controls). Marker rule, F2 link judgement (0 syscalls through the link), the banner's comparison list, rule 1 via an injected stand-in, and the three port guards all hold. |
| 6 Gates | **PASS** (with the explained sandbox failure). Linux 553 passed / 1 failed / 3 skipped (557). Opt-in 554/1/2. DesktopApp 25/25. The Mac suite builds. The 1 failure is #156's `sg` test under `no_new_privs`, and it is green on CI. **CI at 22ec8cb itself:** push 43956469720 and pull_request 43956485760 are green on linux, windows and macos. |

## The instrument, first (`00-landlock-control.txt`)

Everything on the host ran under `lldeny` (the pass-1 QA `lldeny.c`, recompiled), with a deny on BOTH real roots
(`<home>/.local/share/AgentEyes`, `<home>/Videos/AgentEyes`). The control ran BEFORE anything else:
- `ls` of each root: EACCES. `touch` in each root: EACCES. `touch` in scratch: exit 0.
- `cat` of a non-existent file under the state root: ENOENT. Path lookup is not a Landlock right. This is stated, not hidden.

Heavy runs were `nice -n 19`: the owner's installed service on 7882 was **recording** throughout (x11grab + pulse
ffmpeg, a child of `agenteyes serve` pid 2211032). That capture was the 161fix seat's mission row.

## 1. The five timeout shapes (`01-timeout-shapes.txt`, `01b-interleaved-ab.txt`)

My own stand-in (`harness/svc.py`: answer-after-delay, silent, full-queue) and runner (`harness/shapes.sh`). The
developer's scripts were not reused. Each shape is a redirected `agenteyes --help` with the PRODUCTION default probe
(no `AGENTEYES_SERVICE_PROBE_PORT`), run in a `--network none` container whose 7882 is its own. The host's `/usr` was
mounted read-only and nothing of the owner's home was mounted. Mean of 10 runs:

| 7882 in the container | dev pass 4 | dev pass 5 | **QA pass 4** | **QA pass 5** | service roots added (QA p4 / p5) |
|---|---|---|---|---|---|
| nothing listening | 183 | 193 | **178** | **177** | 0/10, 0/10 |
| a service answering | 204 | 215 | **200** | **202** | 10/10, 10/10 |
| accepts, never answers | 641 | 5143 | **640** | **5141** | 0/10, 0/10 |
| connect never completes (full queue) | 650 | 710 | **629** | **842** sequential; interleaved **639 -> 699** | 0/10, 0/10 |
| answers after 1.5 s | 640, dropped | 1715, added | **632, DROPPED 10/10** | **1878, ADDED 10/10** | 0/10, 10/10 |

- **Reproduced.** The one row that looked off (842 ms) was taken at load average 18-27. The 161fix capture and
  buildkit were busy. An interleaved A/B (pass 4 and pass 5 alternating run by run, same container) gives 639 -> 699 ms,
  the developer's +60 ms. The 1.5 s row's spread is 1731-2073 ms under the same load. An earlier run of mine measured it at 1711.
- **The stand-in saw exactly 10 `GET /status`** per answered shape and nothing else.
- **A void run is kept, not hidden:** `01a-timeout-shapes-VOID-runner-bug.txt`. My first runner passed the log path into
  the silent and full-queue stand-ins' delay argument, so they crashed and those two rows measured "nothing listening".
  The runner now refuses to time a shape whose stand-in is not up.

## 2. The injection: zero connects to 7882, and the bind that is not zero

**The instrument, with positive controls in both places it is used:**
- **Container control on 7882 itself** (`02a-instrument-control-container.txt`). The same `strace -f -e trace=connect`
  and the same counter (`harness/count.sh`) were used.
  - A redirected child with the variable UNSET shows **1 connect with `htons(7882)`**. It is in the dual-mode form,
    `sin6_port=htons(7882) ... "::ffff:127.0.0.1"`; a v4-only grep sees 0, which is the trap pass 5 also hit.
  - With `AGENTEYES_SERVICE_PROBE_PORT=41999`, the same child shows **0 to 7882 and 1 to 41999**, and its banner says
    "the service probe was pointed at".
- **In-run canary on the host** (`03-gates-and-7882.txt`, `harness/gate.sh`). Each gate ran as `lldeny -> strace -f ->`
  [one redirected canary child probing a known closed port] then `dotnet test --no-build`. So the trace that counts 0 for
  7882 also holds exactly 1 for the canary.

| Gate (host, under lldeny + strace) | result | loopback connects | canary | **connects to 7882** | binds to 7882 |
|---|---|---|---|---|---|
| Linux default | 553 / 1 / 3 | 50 | 1 | **0** | **2** (EADDRINUSE) |
| Linux opt-in | 554 / 1 / 2 | 50 | 1 | **0** | **2** (EADDRINUSE) |
| DesktopApp | 25 / 0 / 0 | 4 | 1 | **0** | 0 |

Also 0 connects to 7882 in every trace of the job-5 harness, with 25 + 7 connects to its closed probe ports in the same traces.

**Pass 4's ~18 GET /status per run are gone. Confirmed.**

### D-P5 (DEFECT, Lead-ruled): the default-port twin binds 7882 and, where it is free, impersonates the service

- **Where:** `tests/Shared/LibraryRedirectTests.cs:1795-1810` (`DefaultPortFreeFactAttribute`). Its constructor does
  `new TcpListener(IPAddress.Loopback, LocalAppConfig.DefaultPort).Start()` at line 1801. It runs at discovery and
  again at execution. When that succeeds, the twin at `:1576` runs and holds 7882 with `StatusStandIn` (`:1591`).
  `StatusStandIn` (`:1252`) answers EVERY request, whatever the method or path, with `HTTP/1.1 200 OK` and the status
  body (`:1283`).
- **On devlinux today** the two binds fail with EADDRINUSE because the owner's service holds 7882, so the twin skips.
- **Where 7882 is FREE** (`02b-default-port-twin-when-7882-free.txt`, measured in a `--network none` container): the twin
  passes and binds 7882 three times (`bind(... htons(7882) ...) = 0`). **A third-party client's `POST /record/start` to
  127.0.0.1:7882 during the test got `HTTP 200` with the stand-in's body.**
- **Why it matters (the Lead's ruling):**
  - It impersonates the service. Anything driving the Control API at that moment - a seat, the desktop app, the owner's
    own tooling - is told a capture started when nothing is recording: a false success handed to a caller.
  - While it holds the port, a restarting recorder cannot bind it.
  - The window is real. devlinux's service had been restarted about 10 minutes before this QA began, when another seat
    reinstalled it from main for the mission's rows.
- **Fix, as ruled:**
  - Run the twin only on CI or inside its own network namespace.
  - Elsewhere, skip it by name with the reason stated, and do not bind 7882 at all in order to decide.
  - **The test must actually run somewhere.** CI must be shown RUNNING the twin, not skipping it too. Today it does:
    at 22ec8cb the Linux CI job's only skips are the ffmpeg opt-in test and `PackageCli_SandboxedProberLiveWriter_...`
    (`09-ci.txt`), so the twin ran there. The fix must keep that and show it.
- **Disclosure.** The developer DISCLOSED the twin (handoff pass 5 s4), but not what it does when the port is free.

## 3. Serialisation (`04-parallelism.txt`, `harness/overlap.py`)

- `LoopbackServerCollection` has no `[CollectionDefinition]`, so `DisableParallelization` is gone from it. The
  remaining `DisableParallelization` collections are #156's `CaptureOwnershipTests` and older ones that this PR does
  not touch.
- **Measured.** Same build, Linux suite, under lldeny, TRX per run. A test's start time is `endTime - duration`: TRX
  writes `startTime` at report time (start == end). My first version used it and read 0 for everything in BOTH runs,
  which is a broken instrument, now fixed and noted in the file.

| Run | loopback tests overlapped by another class's test | total overlaps | other-class tests inside the collection's span | suite time |
|---|---|---|---|---|
| parallel (as shipped) | **61 of 146** | 479 | 321 of 408 | 12 s |
| serial control (`ParallelizeTestCollections=false`) | 0 of 146 | 0 | 0 of 408 | 18 s |

- The 5 s hung-service test overlapped 5 tests of other classes. The full-queue test overlapped 17.
- All probe tests passed in the parallel run.
- The stand-ins' own threads are still there (`StandIn`), as the Lead accepted: the Windows pool starvation is #189.
  It does not reproduce on Linux and was not chased here.

## 4. Nothing else moved (`05-untouched-members.txt`, `06-scope-diff.txt`, `harness/members.py`)

- **Range `c86081c` (pass-3 code) .. `22ec8cb`:** 8 files changed outside the proof folder, with no merge commits.
  - `LibraryRedirect.cs`, `RealLibrary.cs`, `FfmpegChildRegistry.cs`.
  - The ISOLATING doc (the timeout sentence and the account-name scrub).
  - Two pin lists: one `AGENTEYES_SERVICE_PROBE_PORT` env-read pin, and one `libc!geteuid` import removed.
  - `DesktopAppRedirectTests.cs`: the injected closed port.
  - `LibraryRedirectTests.cs`.
- **Function-level, by my own extractor:**
  - `LibraryRedirect.cs`: only `ApplyFromEnvironment` changed (the probe block). `ServiceProbePortVariable`,
    `ServiceProbePort` and `ServiceProbeNote` were added. 44 members are identical, including `Decide`,
    `UsableFolderProblem`, `FirstEscapingLink`, `UnmarkedLibraryProblem`, `WriteScratchMarker`, `Banner`,
    `ComparisonLimit`, `RealRootsStatement`, both `ServicePortRefusal`, `ServiceLibraryRefusal` and `ResolveLinks`.
  - `RealLibrary.cs`: `ServiceTimeout` was replaced by `ConnectTimeout` / `ResponseTimeout`. `StatusUri` was added.
    `AskRunningService` was split into two overloads. `DeriveForThisProcess`, `Derive`, `Distinct`,
    `FromServiceStatus`, `AccountHome`, `VideosFromUserDirs`, `EffectiveUid` and the `Native` imports are identical.
  - `FfmpegChildRegistry.cs` (#156): ONE line in `OwnUidsOnLinux`: `euid = GetEuid()` -> `euid = RealLibrary.EffectiveUid()`.
    The `GetEuid` import was removed. `EffectiveUid()` is `Native.GetEffectiveUid()` -> `[DllImport("libc.so.6", EntryPoint = "geteuid")]`
    on Linux, the same syscall. `OwnUidsOnMac` is identical. The live banner reads "passwd entry for uid 1000", which is `id -u` here.
- **The Lead's no-diff list** has 0 diff lines since pass 3: `AppDataPaths`, `LocalAppConfig`, CLI `Program`,
  `HeadlessHost`, `App.xaml.cs`, `DesktopApp/Program`, `RecordingService`.
- **Since the last QA-verified head `f9aba6a`:** 198 files, because pass 3 merged main (#118, #156). Pass 3's own
  behaviour is covered by job 5. Its diff was not re-reviewed line by line here.
- **Current main.** `git merge-tree` of 22ec8cb with current main `b0e6ae8` (#113 has landed since the PR's CI) is
  clean (`10-merge-tree.txt`). That merged tree was NOT built or tested here.

## 5. The whole guard still holds (`07-guard-matrix-n1-marker-rule1-ports.txt`, `08-f2-links-probe-order-banner.txt`)

`harness/guard.sh` and `harness/guard-links.sh` are my own consolidation of the pass-1 and pass-2 QA attacks. Every run
was under strace + lldeny. **Every REDIRECTED child was given `AGENTEYES_SERVICE_PROBE_PORT=<closed port>`**, so nothing
of this harness asked the owner's 7882, which was recording. I did NOT run pass 2's `ports.sh` O1/O2, which send
`record status` to the owner's real 7882 by design.

- **Positive control (P0):** an unredirected `--help` makes 6 opens/creates against the real state root, all refused by
  Landlock. So the "0 opens" below comes from a counter that can see opens.
- **D1 attack + x1-x4:**
  - **ATK** (`HOME=<scratch>`, DATA = `<home>/.local/share`, `serve --port 17163`): exit 5, **0 opens**, no probe, no
    7882. It names `passwd entry for uid 1000, home <home>, XDG default`.
  - **x1, x2, x3, x4:** all exit 5 with 0 opens. y1 (spoof HOME + XDG_DATA_HOME + XDG_CONFIG_HOME), y5 (a link to the
    real share) and y8 (`/proc/self/root<home>/...`) are also exit 5 with 0 opens.
  - The real-root syscalls in these runs are `newfstatat` / `readlink` of the requested path NAME during resolution.
    Nothing is opened.
- **N1:**
  - n1 / n2 (missing): refused, nothing created.
  - n3 (videos 0555): the data probe is written and unlinked, then the videos probe gets EACCES. Nothing is left.
  - n6 (a file): refused.
- **The write probe runs LAST, against every earlier rule:**
  - n7: an overlap plus a 0555 data folder -> refused by the OVERLAP, 0 probe syscalls.
  - N8: a marked root with an escaping link plus 0555 -> refused by the LINK, **0 probe syscalls**.
  - n9: an unmarked non-empty root plus 0555 -> refused by the MARKER, 0 probe syscalls.
  - N9 control: the same marked 0555 root with no link -> the probe runs (3 syscalls) and is refused as unwritable.
    This proves N8's 0 is the order of the rules, not an absent probe.
- **Marker rule:**
  - m1 (non-empty, unmarked): refused. The message names both ways on.
  - m2 (fresh, empty): accepted, and `.agenteyes-scratch-library` is written into BOTH `AgentEyes` folders.
  - m3 (the same roots again, now non-empty and marked): accepted.
- **F2 - judged, not followed.** These cases use MARKED roots. My first link cases used unmarked roots and were refused
  by the marker rule instead, so they are not counted as F2 evidence.
  - K1: an absolute link to the real `logs`. K2: a link to the real Videos root, with `serve`. K3: a relative `..` link.
    K4: a nested `..` link.
  - All four exit 5, refused by the LINK rule. **0 syscalls through the link**: only `readlink` and `newfstatat` of the
    link itself.
  - K5 control: a link that stays inside is accepted.
- **Banner comparison list** (K5, accepted). It names:
  - both real roots, each with its derivation (`passwd entry for uid 1000, home <home>, XDG default + this process's
    environment`, and `XDG_VIDEOS_DIR in <home>/.config/user-dirs.dirs` for recordings);
  - `the service probe was pointed at 127.0.0.1:<port> by AGENTEYES_SERVICE_PROBE_PORT, so the default port 7882 was NOT asked`,
    followed by what that port answered;
  - the `NOT CHECKED by path` residue, pointing at `ISOLATING_A_REAL_PROCESS.md`.
  - There is no certainty claim.
- **Rule 1 on the host via the injected port:**
  - s1: a stand-in on 17611 names a relocated library, and a redirect onto it is refused with `(derived from: the
    AgentEyes service answering on 127.0.0.1:17611 (/status DataRoot))`.
  - s2 control: the same stand-in with an unrelated redirect is accepted. The stand-in saw exactly two `GET /status`.
- **The three port guards:**
  - P1: a redirected `serve` with no `--port` is refused naming 7882, with no bind to 7882.
  - P2 / P3: a redirected `record status` / `record start` against my unredirected scratch service on 17481 is refused
    after ONE `GET /status`, with **no POST**. The service is still `idle` afterwards.
  - P4 control: an unredirected CLI drives it.
  - The fourth surface, the Windows tray app (`App.xaml.cs:120`), is out of Linux scope. The Linux DesktopApp guard is
    covered by its 25 tests, which were traced with 0 connects to 7882. I did not start the DesktopApp directly: if its
    guard ever failed, it would talk to the owner's recording service.

## 6. Gates (`03-gates-and-7882.txt`, `09-ci.txt`, `build.txt`)

- **Build** under lldeny with node reuse off:
  - `tests/AgentEyes.Tests.Linux`, `.Mac` and `.DesktopApp`: `Build succeeded`, 0 errors.
  - I did not attach to the foreign MSBuild node (pid 2203366, `nodeReuse:true`).
- **Local Linux** (under lldeny):
  - Default: 553 passed / 1 failed / 3 skipped (557).
  - Opt-in (`AGENTEYES_FFMPEG_TESTS=1`): 554 / 1 / 2.
  - DesktopApp: 25 / 0 / 0.
  - The Mac suite builds; it was not run (Linux machine).
  - **The 1 failure** is #156's `ReadLinuxProcOwner_LiveSubjectWhoseUidAndGidDiffer_ReadsUidNotGid`: "sg adm did not
    start the subject". Under lldeny the process has `NoNewPrivs: 1`, so `sg` cannot change group. It exercises
    `ReadLinuxProcOwner` with its own uid lambda, not the `OwnUidsOnLinux` line that pass 4 changed. It is green on CI
    at this head. I did not run it outside Landlock.
- **CI at 22ec8cb itself** (after the developer's handoff, which quoted 5eaf3fd):

| Run | linux (default / opt-in) | macos | windows (default / opt-in) | DesktopApp |
|---|---|---|---|---|
| push 43956469720 | 555/0/2, 556/0/1 | 510/0/3 | 2588/0/31, 2607/0/16 | 25/25 x2 |
| pull_request 43956485760 | 602/0/2, 603/0/1 | 557/0/3 | 2636/0/31, 2655/0/16 | 25/25 x2 |

- On macOS, `AskRunningService_AConnectThatNeverCompletes_GivesUpAtTheConnectTimeout` is SKIPPED by name.
- Together with 5eaf3fd's push 43954815168 and pull_request 43954827978 (attempts 1 and 2), the code has passed in more than one CI run.

## My view on the trade: the split is right, and 5 s is the right number

- **Agree with the split.** "Is anything listening" and "is it answering" are different questions, and the
  measurement shows the split does what it was for: a 1.5 s service went from DROPPED 10/10 to ADDED 10/10. The cost
  where nothing listens is unchanged on Linux (178 -> 177 ms).
- **Agree with 5 s, for these reasons:**
  - **Who pays it.** Only accept-then-silence pays the full 5 s, which is a hung service. In that case the extra wait
    buys nothing (it names nothing either way), so the cost is honest dead time. It falls only on a machine whose
    recorder is already broken, and the banner names the timeout.
  - **What it buys** is every service answering between the old 0.5 s and 5 s. The only evidence of stalls in that
    range is CI's 2354 ms, and that was a starved TEST HOST pool, not a production probe. A redirected start makes one
    synchronous call at process start, so 5 s is conservative rather than tight.
  - **Lower:** I would not go below about 3 s. That leaves no margin over the one stall ever observed.
  - **Higher:** buys nothing measured and makes the hung case worse. The test's 10 s cap is the right ceiling.
  - **Not fail-closed either.** Refusing the start when a listener never answers would block every redirected start
    while the owner's recorder is hung. The passwd, environment and marker rules all still apply, and the banner says
    the service named nothing.
- **One wording suggestion, not a defect.** The note for a hung service is .NET's "The request was canceled due to the
  configured HttpClient.Timeout of 5 seconds elapsing". For a connect that never completes it is "The operation was
  canceled." A reader cannot tell "something is LISTENING on 7882 and did not answer - its library is not in this list"
  from "nothing reachable". The banner could say that in its own words. The developer chose to leave the wording
  alone, as out of the two items.

## The owner's library and service - footprint of this seat

- **No process of mine opened either real root.**
  - Every host build, test and harness run was under lldeny, with the control first. P0's attempted opens were refused
    by the kernel (EACCES).
  - The containers mounted nothing of the owner's home.
- **Reported, outside Landlock:**
  1. At the start, ONE `ls -ld` of the two real roots, to confirm their paths. This is a `stat` of each directory: metadata only, no open, no listing.
  2. One `grep` of `<home>/.config/user-dirs.dirs` (a config file, not the library).
- **No request of mine reached the owner's service.**
  - Every traced run shows 0 connects to 7882.
  - Two untraced suite runs (job 3's TRX runs) are the same suite whose traced runs showed 0.
  - The unredirected scratch service on 17481 was not traced. It is unredirected, so it has no probe.
- **Foreign processes, identified by command line and left alone:**
  - the owner's `agenteyes serve` (7882; recording the 161fix seat's row, ffmpeg x11grab + pulse) and `AgentEyesDesktop`;
  - go2rtc and its ffmpeg (camera wall);
  - the 161fix seat's `take.py`;
  - a foreign MSBuild node and two VBCSCompiler servers (shared per user).
- **Everything I started has exited:** the stand-ins, my scratch `serve` on 17481 (its own timeout), and the containers (`--rm`).

## What I did NOT cover

- **Windows:** the derivation, the start-time cost of the 500 ms connect bound where nothing listens, and the #189 pool
  starvation. These are routed separately by the Lead. **macOS:** CI only.
- The merged tree with current main `b0e6ae8`: a textual merge only, not built or tested.
- Pass 3's diff was not reviewed line by line. Its rules are verified by behaviour in job 5.
- The production default port on the HOST: never exercised on purpose, because the owner's service holds it. It was
  exercised only in containers (shapes, the 7882 instrument control, the twin).
- `#156`'s `sg` test was not run outside Landlock; CI covers it.
- An `http_proxy` environment: none is set here. I did not test whether a proxy variable would route the probe through
  a proxy, which a connect counter would not see as a connect to 7882.
- `libc.so.6`: since pass 4, #156's own-uid check on Linux binds `geteuid` through `libc.so.6` instead of `libc`. This
  is the same on glibc. A non-glibc Linux was not tested. The redirect itself has required `libc.so.6` since pass 2.
- Path lookups (`stat`, `readlink` of a path name) are not Landlock rights. The 0-opens counts are opens/creates;
  lookups of the requested path names are listed in the artefacts.
