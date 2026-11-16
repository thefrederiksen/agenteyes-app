# Issue #156 - QA (Linux seat), passes 5 and 5b

**Verdict: PASS on all five items -> `flow:ready-gate`.** The statx path reads the **UID**, measured
against a live subject whose uid and gid differ, so the code is correct. The *test* gap the
developer suspected is real, though: a Uid->Gid mutant of the statx offset passes the whole shipped
Linux suite (390/2). That gap is a follow-up and not a defect of this PR. The trial merge onto
current `main` is clean, it builds, and the Linux suite passes on it.

Artefacts: [`qa-linux-pass5/`](qa-linux-pass5/). Paths in them are scrubbed to `<SCRATCH>`, `<HOME>` and `<TMP>`.

## Under test

| | |
|---|---|
| Branch head | `f6d16bf` (`git fetch origin`; local = `origin/issue-156-capture-ownership`). The code is `219a56c` (pass 5b) on top of `9747a81` (pass 5). `25906b0..f6d16bf` is docs only |
| Binary I ran | `agenteyes.dll` built from that tree. Embedded version `1.11.11+f6d16bfc02405b9facb5a03136f831ca814d8e2e`, printed at the top of every transcript |
| Controls built | `9abbb01` (pass 4, the head the pass-4 seat verified), embedded `+9abbb01a...`<br>`3e818e6` (pass 5, before 5b), embedded `+3e818e6f...`<br>A Uid->Gid mutant of `f6d16bf`. It embeds the same commit, so it is told apart on this machine only, by sha256 prefix `044dace5` vs head `49802fd2` |
| Machine | devlinux, kernel 6.8.0 (Landlock ABI 4), glibc 2.39, SDK 8.0.134 / runtime 8.0.34. Prober uid 1000, gid 1000, member of `docker` (gid 986) |
| Containers | `ubuntu:18.04` (glibc 2.27) and `ubuntu:24.04` (glibc 2.39), local images, `--network none`, `--rm`. Microsoft .NET runtime 8.0.34 from `dotnet-install.sh`, into my scratch |

**Trap found on the way, for the next Linux reader.** On Linux, `dotnet build -c Release` writes to
`src/AgentEyes.Core/bin/Release/net8.0/`. On this clone, `bin/x64/Release/net8.0/` holds a **stale
`9747a81`** build, from pass 5. The developer's `pass5/eperm5.sh` points its "after" CLI at the x64
path. That was correct for pass 5, and it would silently test pass 5 code if re-run now. Every run
in this report uses the fresh `bin/Release` build and prints its embedded commit.

## (1) Pass 5 - the Linux EPERM mapping: PASS

**Harness.** The pass-4 seat's `llprobe.c` (byte-identical, `cmp`) runs under Landlock, which refuses
reads of other processes' `/proc/<pid>`. With `--kill-eperm`, seccomp also makes `kill(2)` return
EPERM. The script is [`harness/qa5-eperm.sh`](qa-linux-pass5/harness/qa5-eperm.sh), the developer's
`eperm5.sh` with three changes:
1. The binaries come from `bin/Release`.
2. Isolation is proven **read-only before any product command runs**. The harness `paths` command
   prints the recordings, state and registry roots, and the script aborts unless all three are under
   the scratch run. The pass-5 script ran `agenteyes housekeep` to find this out.
3. Arm G is new: a live writer whose egid differs from its euid (item 3, end to end).

Both builds ran back to back:
[`eperm-before-9abbb01.txt`](qa-linux-pass5/eperm-before-9abbb01.txt) and
[`eperm-head-f6d16bf.txt`](qa-linux-pass5/eperm-head-f6d16bf.txt). The writers are real `ffmpeg -re`
processes writing `raw.mp4`. The crashed owner is a real `CaptureLock` claim whose process
SIGKILLs itself.

| Arm | before `9abbb01` | head `f6d16bf` |
|---|---|---|
| A-P live writer, plain prober (control) | Mine, STILL RUNNING | **Mine**, STILL RUNNING |
| A-L live writer, Landlock only (control) | Unknown, UNDETERMINED | **Unknown**, UNDETERMINED |
| A-E live writer, Landlock + kill EPERM | **NotMine "ANOTHER USER (kill(pid, 0) errno 1)"**; Inspect "stale" | **Unknown: "pid W is THIS USER's (/proc/W owned by uid 1000) but kill(pid, 0) was refused (EPERM)"**; Inspect Live+Undetermined |
| A-E2 real `agenteyes package --no-frames` under the same confinement | **ClearStaleClaim CLEARED, `failed Attempts=1`, `capture.owner.json` ABSENT**; writer ALIVE, raw.mp4 262,192 -> 524,336 | **exit 1 "refusing to package ... UNDETERMINED"**, `mux=None` (no attempt), **`capture.owner.json` PRESENT**; writer ALIVE, raw.mp4 262,192 -> 524,336 |
| S1 writer stopped (our SIGTERM), Landlock + EPERM | - | NotMine "exited while being read (/proc/W does not exist)" (the ENOENT row) |
| **S2 positive control: an ordinary `package`** | done, Attempts=2 (the first was burned by E2) | **done, Attempts=1**, claim cleared, `recording.mp4` decodes (exit 0), h264 + aac |
| G-P writer euid 1000 / **egid 986**, plain (control) | Mine | **Mine** |
| G-E the same writer, Landlock + kill EPERM | NotMine "ANOTHER USER" | **Unknown "owned by uid 1000"** |
| G-E2 real `package` under the same confinement | **CLEARED, Attempts=1**, claim ABSENT, writer ALIVE | **declined UNDETERMINED, no attempt, claim PRESENT**, writer ALIVE |
| B-P pid reused by ANOTHER USER (pid 1, root), plain | NotMine (start time differs) | NotMine (start time differs) |
| **B-E positive control: the same, Landlock + kill EPERM** | NotMine "errno 1" | **NotMine "ANOTHER USER (/proc/1 owned by uid 0; kill(pid, 0) EPERM)"** |
| B-E2 real `package` under Landlock + EPERM | claim CLEARED | **claim CLEARED** (the stale claim is taken over). The mux then fails because the confinement refuses its own ffmpeg child's `/proc`. That harness property is already named by pass 4 |
| B-O ordinary `package` (recovery) | done, decodes | **done**, decodes h264 + aac |

Mapping against the ruling (6923154064):
- EPERM with a different uid gives NotMine (B-E).
- EPERM with our uid gives Unknown (A-E, G-E).
- ENOENT gives NotMine (S1).
- "Refused" gives Unknown. Pinned by the unit tests listed below, and by item 2's totality arms.

The lead's wording, "our uid falls through to the start-time comparison", is satisfied by
construction. `ClassifyUnreadable` is only reached after the start time was already refused, so the
code answers Unknown directly (`FfmpegChildRegistry.cs:504-506`).

One honest note on G-E2 at head: the writer was confirmed ALIVE by `kill -0`, but `raw.mp4` did not
grow in that 2 s window (262,192 -> 262,192). The muxer flushes in 256 KiB steps, and it did grow
in arm A's identical window. This does not change the answer: the decline and the PRESENT claim are
the row under test.

## (2) Pass 5b - totality without statx: PASS, reproduced in the glibc 2.27 container

I reproduced the developer's container arm myself, and **went one step further: the EPERM is real,
not injected.** A static `llprobe` runs *inside* the container, with Landlock plus seccomp, and the
real public path (`IdentifyProcess` -> `CaptureLock.Inspect`) and the real CLI `package` both run
under it. The script is [`harness/qa5-container.sh`](qa-linux-pass5/harness/qa5-container.sh) and
the transcript is [`container-glibc227-vs-239.txt`](qa-linux-pass5/container-glibc227-vs-239.txt).

| | 18.04 / glibc 2.27, pass 5 `3e818e6` | 18.04 / glibc 2.27, **head** | 24.04 / glibc 2.39, pass 5 | 24.04 / glibc 2.39, head |
|---|---|---|---|---|
| owner read, our pid / root pid / missing pid | **THREW `EntryPointNotFoundException: ... 'statx' in shared library 'libc'`** on all three | **Owned 1000 `(stat)` / Owned 0 `(stat)` / Gone** | Owned 1000 / Owned 0 / Gone | Owned 1000 `(statx)` / Owned 0 `(statx)` / Gone |
| predicate EPERM row on the same three | **THREW** on all three | **Unknown / NotMine / NotMine** | Unknown / NotMine / NotMine | Unknown / NotMine / NotMine |
| real path, plain prober (control) | Mine | Mine | Mine | Mine |
| real path, Landlock + real kill EPERM, live same-user writer | **unhandled exception, process aborted** | **Unknown, Inspect UNDETERMINED** | Unknown | Unknown |
| real CLI `package` under the same | **exit 3, "Unable to find an entry point named 'statx'"**; claim PRESENT | **exit 1, refusing ... UNDETERMINED; claim PRESENT; no mux state written** | exit 1, UNDETERMINED | exit 1, UNDETERMINED |
| positive control: writer pid now root's, Landlock + EPERM | **unhandled exception** | **NotMine "owned by uid 0"; Inspect "stale capture claim taken over"** | NotMine | NotMine |

So in a supported configuration, pass 5 broke the invariant: the predicate threw out of
`Inspect` and out of the real CLI. Pass 5b answers through `SystemNative_Stat` (the detail says
`(stat)`), and its answers are identical to the glibc 2.39 control, which goes through `statx`.

Code read, `FfmpegChildRegistry.cs:619-666`:
- `ReadIfPresent` catches exactly `EntryPointNotFoundException` and `DllNotFoundException`. Those
  are the "read does not exist here" cases.
- ENOSYS from statx falls through to stat. Any other statx errno is Unreadable, then Unknown, with
  no second read.
- When neither read is available, the result is Unreadable and the answer is Unknown.
- No new state was added.

## (3) Does the statx path read UID or GID? It reads UID. The code is correct; only the test was weak

[`uidgid-statx-head.txt`](qa-linux-pass5/uidgid-statx-head.txt) and
[`uidgid-statx-MUTANT-gid-offset.txt`](qa-linux-pass5/uidgid-statx-MUTANT-gid-offset.txt) record
these reads:
- **The layout itself**, by my own independent `statx` P/Invoke (same flags, same mask), compared
  with `stat(1)`:
  - `/etc/shadow` gives u32@20 = **0** and u32@24 = **42**, against `stat(1)` 0:42.
  - The subject `/proc/<pid>` gives u32@20 = **1000** and u32@24 = **986**, against `stat(1)` 1000:986.
- **The subject** is `sg docker -c 'exec sleep 300'`: a live process of this user with euid 1000 and
  egid 986, so its `/proc/<pid>` is owned by 1000:986. This is exactly the case the lead feared: a
  user whose uid and gid differ.
- **Head, product `ReadLinuxProcOwner(pid)`:** `owned by uid 1000 (statx)`. The predicate's EPERM
  row with the real reader answers **Unknown**, "THIS USER's". On `wpa_supplicant` (pid 1611,
  0:110) it reads `uid 0 (statx)` and the answer is **NotMine**.
- **The mutant** (`StxUidOffset = 20` -> `24`, which is `stx_gid`) reads `owned by uid 986 (statx)`,
  and the predicate answers **NotMine "belongs to ANOTHER USER"** for our own process. That is the
  defect the lead described, and it is what my instrument shows when the field is wrong. So the head
  result is a positive reading, not an absence. The pid 1611 control flips to "uid 110".
- **End to end on the real binary**, arm G in item 1: a live ffmpeg writer with egid 986, probed by a
  real Landlock plus EPERM prober, answers Unknown and the real `package` declines. The before build
  answers "ANOTHER USER" there, so the arm discriminates.

`StxUidOffset = 20` (`FfmpegChildRegistry.cs:698`) is correct. It matches `linux/stat.h`
(stx_mask u32 @0, stx_blksize u32 @4, stx_attributes u64 @8, stx_nlink u32 @16, **stx_uid u32 @20**,
stx_gid u32 @24) and the measurement above.

**The test gap: measured, not assumed.** The mutant tree passed the shipped Linux suite at
**390 passed, 2 skipped, 0 failed** ([`suite-linux.txt`](qa-linux-pass5/suite-linux.txt)), so no
shipped test catches a Gid offset on the statx path. There are two reasons:
- `ReadLinuxProcOwner_ThisProcessRootAndMissingPid_OwnerAndGone` (`tests/Shared/ProcessIdentityTests.cs:380`)
  reads only this process (1000:1000) and pid 1 (0:0).
- The `/etc/shadow` pin added in 5b goes through `StatOwnerOfPath`, which is the stat fallback, not
  statx.

A second weakness exists in that 5b pin. Its loop `continue`s past every file whose uid equals its
gid, or that does not exist (`:478-484`, the `continue` at `:482`). On a machine where none of the four files has differing
ids, it asserts nothing and passes. **Follow-up, not a defect of this PR:**
- Pin the statx path against a subject whose uid and gid differ. A statx of `/etc/shadow` through an
  internal path-taking statx read would do, as would a child started with a different egid.
- Make the "no differing subject found" case fail or skip visibly rather than pass.

## (4) Nothing else moved: PASS

- `git diff --name-only 9abbb01 f6d16bf` outside `docs/` lists four files:
  - `src/AgentEyes.Core/Video/FfmpegChildRegistry.cs`
  - `src/AgentEyes.Core/Video/ProcessIdentity.cs` (the new `LinuxProcOwner` types)
  - `tests/AgentEyes.Tests/ManifestWriterIlTests.cs` (the IL import allowlist: `getuid`, `geteuid`,
    `statx`, `SystemNative_Stat`)
  - `tests/Shared/ProcessIdentityTests.cs`
- **The only removed lines in `src`** are six:
  - the wrapper's `OwnUidsOnMac() : null` argument
  - the three-line comment that called the Linux EPERM row "NOT measured"
  - the old `ClassifyUnreadable` signature, which gains an optional `linuxOwner` parameter
  - the old `"ANOTHER USER (kill(pid, 0) errno ...)"` message
- **The hunks** are at `FfmpegChildRegistry.cs` -386/+386 (the wrapper passes the Linux uids and
  reader), -430/+439 (the comment), -446/+475 (the signature), +494 (the EPERM block), -465/+509
  (the message), +590 (`ReadLinuxProcOwner` and its helpers), +698 (constants and imports), and
  `ProcessIdentity.cs` +58.
- None touches the macOS rows, the Windows rows, the zombie proof, the start-time comparison or
  `IdentifyProcess`.
- **Zero diff lines** in `CaptureLock.cs` (tri-state consumers, the shared hold, the host guard,
  `ClearStaleClaim`), `Package.cs`, `PostRecording.cs`, `RecordingService.cs`, `MuxCoordinator.cs`,
  `PostRecordingPlan.cs`, `PostRecordingState.cs`, `Thumbnails.cs`, `RepairService.cs`,
  `Manifest.cs` (G1) and `RecoveryManifest.cs`. The SIGTERM bound and `KillTree` in
  `FfmpegChildRegistry.cs` are outside every hunk.
- **D3:** this branch pulls in no #113 code. In `src`, `IScreenRecorder` has 0 hits, `frag_keyframe`
  has 0 and `empty_moov` has 0. Positive control for the search: `CaptureLiveness` has 1 definition
  (`CaptureLock.cs:10`).

## (5) Suite, and the tree that would land: PASS

| Tree | Linux default | Linux `AGENTEYES_FFMPEG_TESTS=1` | other |
|---|---|---|---|
| head `f6d16bf` | **390 passed / 2 skipped / 0 failed** | **391 / 1 / 0** | `ProcessIdentityTests` 47 / 1 |
| **trial merge `cbf1fb9`** = `f6d16bf` + `origin/main 2d60f77` | **416 / 2 / 0** | **417 / 1 / 0** | DesktopApp tests 15 / 0 / 0; ownership + identity tests 86 / 1 run in the merged tree |
| control: `origin/main 2d60f77` alone | 317 / 1 / 0 (318 total) | | the ownership and identity filter matches **no tests** on main |

- The head counts match pass 5b's claim exactly.
- **The trial merge** was done in a scratch worktree, never pushed. `git merge --no-ff origin/main`
  merged clean: 0 conflicted files, parents `f6d16bf` and `2d60f77`. `main` is not an ancestor, so
  this is a real merge, unlike pass 3's trivial one.
- The merged tree builds: `AgentEyes.Core`, `AgentEyes.Tests.Linux` and `AgentEyes.Tests.DesktopApp`
  all show 0 errors. Its binary embeds `+cbf1fb9...`.
- The merged count adds up: 418 = 292 at the merge base `17c244d` + 100 from the branch + 26 from
  main. That is the 292 the #151 measurement gives for the Linux gate.
- See [`trial-merge.txt`](qa-linux-pass5/trial-merge.txt).

## What I did NOT cover

- **The Windows and macOS suites, and `AgentEyes.sln`**, which does not build on Linux. They were
  not run by me, at this head or on the merged tree. For those I rely on the developer's CI note
  for `25906b0` (macOS 383/1, Windows 2474/16 and 2493/1). I did **not** check CI on the merged
  tree; the gate's CI run on the PR's merge ref is the place for that.
- **Real kernel ENOSYS from statx.** That needs a pre-4.11 kernel, or a libc that does not emulate
  statx plus seccomp. ENOSYS is covered only by the developer's injected unit arms, which I re-ran
  as part of the suite.
- **musl** (Alpine): not run. This includes whether the `"libc"` DllImport of
  `getuid`/`geteuid`/`statx` resolves there at all. The `alpine:latest` image is present, but a
  musl .NET runtime was not installed.
- **Landlock ABI 6 signal scoping** (kernel 6.12+), or any named sandbox product. EPERM is still
  produced by seccomp, as in pass 4.
- **A non-dumpable writer** (a setuid, setgid or file-capability ffmpeg), whose `/proc` is root's.
  The developer named this premise; I did not measure it.
- **Real capture.** My writers are synthetic `ffmpeg -re` lavfi encodes into the real claim and
  manifest layout, not x11grab or pulse captures. Pass 4's real-capture arms were not repeated,
  because nothing in this delta touches capture.
- **The service.** Release under Unknown inside a running service, a Landlock-confined *capturing*
  host, and two machines on one library are as pass 4 left them.
- **The developer's 10 + 5 mutations** were not re-run, except one: my Uid->Gid mutant of the
  statx offset, which the shipped suite does not catch (item 3).
- **In the containers the writer stand-in is `sleep`**, not ffmpeg. Its pid namespace is private,
  so #172's case is not exercised there; it is out of scope.

## Footprint (#161): none

- **Isolation:** every product command ran with `HOME`, `XDG_CONFIG_HOME` and `XDG_DATA_HOME` under
  my scratch. The roots were proven read-only by `paths` before the first product command ran.
- **The owner's library:**
  - Its signature was unchanged across both host runs.
  - 0 entries were modified.
  - The evidence recording `2026-11-14_102532_video` is present.
  - The owner's real log mentions my scratch runs on 0 lines.
- **Foreign processes I did not touch:**
  - go2rtc ffmpeg, pid 72110, identified by command line, never signalled. It is present after
    each run.
  - The owner's `agenteyes serve` (6560) and `AgentEyesLinux --tray`.
  - The #161 seat's serve on port 17161, which runs from the other clone.
  - Four foreign containers.
- **What I started and ended:** every ffmpeg, `sleep` and container, and only those. I started no
  service and did not reinstall the installed recorder.
- **My scratch worktrees:** `wt/mut-gid`, `wt/before-*`, `wt/trial` and `wt/main` are under my
  scratch and are removed after this report.

No product code was changed by me and nothing was merged.
