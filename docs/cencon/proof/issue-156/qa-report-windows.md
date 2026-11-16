# Issue #156 - QA report, Windows, pass 3 (the predicate under another user and under elevation)

**Verdict: VERIFIED -> `flow:ready-gate`.** On Windows the predicate is now total for another
user's process: it answers FALSE, logs the pid and native error 5, and the crashed recording is
recovered. Before the fix every one of those calls THREW on Windows too, not just on macOS. The
dangerous elevated same-user case does not exist on .NET 8: the framework opens the target with
`PROCESS_QUERY_LIMITED_INFORMATION` (read out of its IL), and Windows grants that right across
elevation for the same user. Measured against four running High-integrity processes of this user, the
product's predicate answers TRUE for each with its real start time, and a crashed recording whose
orphan writer is such a process is DECLINED as live. No defect was found in this change. There are
two non-blocking observations (O1, O2) and an honest list of gaps at the end.

| | |
|---|---|
| Machine | `SORENLAPTOP`, Windows 11 Home 10.0.26200, session token **Medium** integrity, user in Administrators as **deny-only** (UAC filtered token, `ConsentPromptBehaviorAdmin=5`) |
| Head tested | `1927c62` (code `e7ed955` + merge `9a1accb`; `6eb55df`, `d99830c` and `1927c62` touch docs only) |
| Binary run | `src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe` / `.dll`, embedded `1.11.11+1927c621f06eb58e423680dc9362528e639bd7b4` |
| Pre-fix control | `2594cfc` (the head both Linux passes verified), scratch worktree, embedded `1.11.11+2594cfc733eb58103a36055ec7b7d1295fe4d8c5` |
| Pre-#156 control | `main` `17c244d`, scratch worktree, embedded `1.11.11+17c244df603fd30115b145523e9a6d76d48c6fa5` |
| SDK / runtime | .NET SDK 10.0.301 building net8.0; runtime .NET 8.0.21 (`Microsoft.NETCore.App 8.0.21`) |
| ffmpeg | 8.1.1 full_build (winget Gyan.FFmpeg) |
| Build | `dotnet build AgentEyes.sln -c Release`: `Build succeeded.`, `0 Error(s)`, 22 warnings |
| CI | green on `1927c62`: push run 43889795102 and pull_request run 43889811299, linux, macos and windows jobs all pass |

Every harness is under `qa-windows/harness/`, and every run file is under `qa-windows/run-*.txt`.
In the run files, this session's scratch directory is shown as `<scratch>`.

---

## (1) Another user's process - PASS

**Probe:** `harness/PredicateProbe.cs` loads the agenteyes.dll under test and calls the product's own
`FfmpegChildRegistry.IsSameProcess`, `CaptureLock.Inspect`, `PostRecordingPlan.FindUnfinished` and
`ReapOrphansIn` by reflection. There is no seam on the predicate. It runs as a non-admin, Medium token,
with AgentEyes' data and recordings roots redirected into scratch (`AppDataPaths.RedirectForThisProcess`).
The redirect was checked before anything ran.

| Target (SYSTEM-owned; `QUERY_LIMITED` refused, error 5) | FIX `1927c62` (`run-pp-head.txt`) | PRE-FIX `2594cfc` (`run-pp-prefix.txt`) |
|---|---|---|
| pid 4 System, services, lsass, wininit, winlogon, spoolsv, 3x svchost | `False` for all 9, no throw | **`THREW Win32Exception: Access is denied.`** for all 9 |
| `Inspect`, crashed recording whose writer is pid 4 | `Live=False` "stale capture claim taken over ..." | **THREW Win32Exception** |
| `FindUnfinished` over 10 recordings, including that crash | **admits** `..._writer-is-system-pid4` | skips it: "it could not be read" (`at IsSameProcess ... FfmpegChildRegistry.cs:line 287`) |
| `ReapOrphansIn`, record whose OWNER pid is 4 (the gate's item 3) | both records removed, 0 left | **THREW Win32Exception**, 2 records left |

**The log line at the decision** (redirected product log, `run-pp-head.txt`):
`[FfmpegChildRegistry] IsSameProcess: pid 4 - start time cannot be read (5: Access is denied.); Windows refused to read it (see the native error: 5 is access denied - another user's, a protected, or possibly an elevated process; the elevated case is unmeasured) - answered NOT the recorded process`

**On the real CLI binary** (`qa-windows/foreign-pid.ps1`, a Windows port of the developer's
`pass3/foreign-pid.sh`). The recording is a real 4 s lavfi `raw.mp4` with a start manifest, a FREE
`capture.lock`, a dead owner pid, and writer pid 4. Then a second process runs
`agenteyes package <dir> --no-frames`:

| | FIX (`run-cli-head.txt`) | PRE-FIX (`run-cli-prefix.txt`) |
|---|---|---|
| exit | 0 | **3, `[error] Access is denied.`** |
| manifest | `PendingMux` cleared, `mux.State=done`, **`Attempts=1`** | pending, no attempt |
| file | `recording.mp4` 1520404 B, ffprobe `video audio 4.02`, full decode exit 0 | none |
| `capture.owner.json` | absent. Real log: `ClearStaleClaim: ... CLEARED capture.owner.json (lock held shared): stale capture claim taken over` | PRESENT |

Recovered, not skipped. **Positive control in the same run:** case C has a writer pid that does not
exist and recovers on BOTH binaries at Attempts 1, so the harness can produce a recovery and the
pre-fix failure is the predicate.

## (2) The elevated same-user case - the hole is NOT real on .NET 8; the configuration is reachable

**Which access right the predicate asks for.** Both of these sources agree:

1. **The framework's IL** (`harness/IlProbe.cs`, `run-il-process.txt`), read out of
   `C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.21\System.Diagnostics.Process.dll`:
   `get_StartTime -> get_StartTimeCore -> GetProcessTimes`, which passes **`0x1000`
   (PROCESS_QUERY_LIMITED_INFORMATION)** to `GetProcessHandle`. `get_HasExited -> UpdateHasExited`
   passes **`0x101000`** (LIMITED | SYNCHRONIZE). Neither path asks for `PROCESS_QUERY_INFORMATION`
   (0x400). That constant appears only in `GetProcessHandle(Int32,Boolean)`'s exit-code check, which
   is gated on the caller having asked for it. So the premise in the mission brief, that StartTime
   opens with `PROCESS_QUERY_INFORMATION`, does not hold for .NET 8.0.21.
2. **Measurement** (`harness/AccessProbe.cs`, `run-access-probe-summary.txt`; 284 processes, Medium
   non-admin token). Four processes on this machine run at **High** integrity and are owned by
   `SORENLAPTOP\soren`: ctfmon 14552, AsusOSD 2996, AsusSoftwareManagerAgent 21740 and taskhostw 4752.
   For each, `OpenProcess(PROCESS_QUERY_INFORMATION)` gives **ERR5**,
   `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` gives **ok**, and .NET `HasExited` and `StartTime`
   both **succeed** with the same start time `GetProcessTimes` returns. That row pattern is the
   positive control. The instrument does see refusals: 163 other processes refuse both rights, and
   for those .NET throws `Win32Exception(5)`.

**The product predicate against them** (`run-pp-head.txt`):

| Elevated same-user pid | real start time | start time -10 min | crashed recording with it as writer |
|---|---|---|---|
| 14552, 2996, 21740, 4752 | **`True`** for each | `False` for each | `Live=True` "... video ffmpeg pid N is STILL RUNNING and writing into it", and **`FindUnfinished` skips all four**. The four reused-pid twins are admitted. |

**On the real CLI** (`run-cli-head.txt`, case B, writer = ctfmon 14552 with its real start time):
`package` exits 1, `refusing to package ...: ... video ffmpeg pid 14552 is STILL RUNNING`, `PendingMux`
is still pending, no attempt is recorded, and `capture.owner.json` is PRESENT. The pre-fix binary does
the same. That is the safe answer: the live capture is not read as stale. The predicate's
"unreadable -> FALSE" branch is never reached for an elevated process of the same user, because
nothing about it is unreadable at the right .NET asks for.

**Reachability in the shipped product.** The configuration IS reachable, but narrowly. The app installs
per-user (`C:\Users\soren\AppData\Local\AgentEyes\app\AgentEyesApp.exe`, running here as pid 16160)
and autostarts unelevated. The capture writer is a child ffmpeg and inherits its parent's token. So a
writer ends up High-integrity, and the probing process Medium, whenever a user:

- starts `AgentEyesApp.exe` once with "Run as administrator", or
- runs the CLI (`agenteyes video ...`) from an elevated terminal while the tray app runs unelevated.

The second is an ordinary developer habit. Measured outcome for that configuration: declined as live,
no data loss. The reverse direction (elevated probe, unelevated writer) can open everything of the same
user and was not a concern.

**What was NOT done:** a real AgentEyes started elevated. That needs a UAC consent click
(`ConsentPromptBehaviorAdmin=5`, `PromptOnSecureDesktop=1`), and I did not fake one. The four High-IL
processes are stand-ins. They are what an elevated process of this user looks like to this probe, but
none of them is an ffmpeg started by an elevated AgentEyes.

## (3) Nothing else moved - PASS

`git diff 2594cfc 1927c62 -- src tests` touches 9 files. Each is attributed:

- **From main, byte-identical to `17c244d`:** `Ffmpeg.cs`, `MicChainStallTests.cs`, `OwnedFfmpeg.cs`,
  `OwnedFfmpegTests.cs` (`git diff 17c244d 1927c62 -- <file>` is empty for each).
- **The three test csproj files** differ from main only by this branch's `<Compile Include>` lines:
  `CaptureOwnershipTests`, `CaptureWriterReapTests` (both already at `2594cfc`) and the new
  `ProcessIdentityTests`.
- **`FfmpegChildRegistry.cs`:** `git diff e7ed955 1927c62` is exactly ONE hunk,
  `private static void KillTree` -> `internal static void KillTree`. That is main's #151 change, which
  `OwnedFfmpeg.cs` calls. Main's other #151 hunk (`IsSameProcess` internal) was already internal on the
  branch. `git show --cc 9a1accb` shows the same single conflict resolution. **The developer's claim
  holds.**
- **New:** `tests/Shared/ProcessIdentityTests.cs`, unchanged since `e7ed955`.
- `6eb55df`, `d99830c`, `1927c62`: 0 files outside `docs/`.

## (4) The gate's scratch reproduction - PASS, with one honest limit

`pass3/harness/GateProbeScratchTests.cs` has exactly one commit (`6eb55df`) and has not been modified
since. Its header says the body is the gate's file unchanged. **I could not compare it byte-for-byte
with the gate's original**: the original is in the gate's macOS scratchpad, and `devthrottle-mac-mini`
does not resolve from this laptop.

Run on Windows (`run-gate-probe-windows.txt`), copied temporarily into `tests/AgentEyes.Tests` on both
trees and removed afterwards (`git status` clean):

- **Unchanged, it proves nothing on Windows.** Its stand-in is pid 1, which does not exist here
  (`Get-Process -Id 1` returns nothing). So `Inspect(pid 1)` takes the "pid gone" path, and both builds
  answer the same.
- **The labelled Windows variant** (`harness/GateProbeScratchWinPid4Tests.cs`: pid 1 -> pid 4, class
  renamed, nothing else) discriminates:

| Gate row with pid 4 | PRE-FIX `2594cfc` | FIX `1927c62` |
|---|---|---|
| `Inspect`, `ClearStaleClaim`, `ThrowIfLive` | **THREW Win32Exception: Access is denied.** (each) | `False ... stale`, `True`, `passed` |
| `FindUnfinished(root with healthy + pid4)` | `2026-11-14_090000_healthy` only (crash dropped) | `healthy, rootpid` (crash admitted) |
| `Thumbnails.FindMissing(root)` | **THREW Win32Exception** | `OK: 0` |
| `ReapOrphansIn(owner pid 4)` | **THREW**, record still on disk: True | record removed, on disk: False |

The variant's two controls (writer gone; writer = this process with the wrong start) pass on both
builds, so the difference is the predicate.

## (5) The 14 shared tests in the Windows gate, and mutations - PASS

All 14 run and pass in `tests/AgentEyes.Tests` on this machine (`run-pid-tests-baseline.txt`):
`FindUnfinished_WriterPidHeldByAnotherUser_AdmitsTheCrashedRecording`,
`ReapOrphansIn_OwnerPidHeldByAnotherUser_RemovesTheRecord`,
`DescribeUnreadable_PidZeroOrNegative_IsCorruptNotAGroupSignal(pid: 0)` and `(pid: -1)`,
`IsSameProcess_OwnProcessDifferentStartTime_ReturnsFalse`,
`IsSameProcess_PidHeldByAnotherUsersProcess_ReturnsFalseWithoutThrowing`,
`Resume_WriterPidHeldByAnotherUser_RecoversTheRecording`,
`IsSameProcess_OwnProcessRecordedStartTime_ReturnsTrue`, `Inspect_WriterStillRunning_IsLive`,
`IsSameProcess_NoSuchProcess_ReturnsFalse`, `IsSameProcess_ThisUsersZombie_ReturnsFalse`,
`FindMissing_RootHoldsAForeignPidRecording_CompletesForEveryRecording`,
`Inspect_WriterPidHeldByAnotherUser_IsAStaleClaim`, `DescribeUnreadable_PidOne_SaysAnotherUser`.

Mutations made by me in `FfmpegChildRegistry.cs`, each restored with `git checkout` and checked by an
empty `git diff` (`run-mutations-windows.txt`):

| Mutation | Result on Windows |
|---|---|
| M1: the `catch (Win32Exception)` block deleted (the `d7b7cc7` behaviour) | **6 of 14 fail**: e.g. `IsSameProcess_PidHeldByAnotherUsersProcess_ReturnsFalseWithoutThrowing` -> `System.ComponentModel.Win32Exception : Access is denied.`, `FindUnfinished_...` -> `Assert.Contains() Failure: Item not found in collection`, `Resume_...` -> `Assert.Single() Failure: The collection was empty` |
| M2: an unreadable start time answered TRUE | **5 of 14 fail**: e.g. `IsSameProcess_PidHeldByAnotherUsersProcess_...` -> `Assert.False() Failure Expected: False Actual: True`; `ReapOrphansIn_...` -> `Filter not matched in collection` |
| M3 (extra): `if (p.HasExited) return false` made `return true` | **0 fail - survives.** See O2: on Windows that branch is reachable only in a race. |

## (6) Criterion 4's control arm on a REAL Windows capture - PASS

`qa-windows/real-crash.ps1` with `harness/CrashHarness.cs`. The harness is QA's code. It loads the
tree's agenteyes.dll, redirects AgentEyes' roots into scratch (#161: there is no supported redirect for
a real agenteyes process, so no `serve` was started), and starts a **real capture through the product's
`RecordingService.StartVideo`**: screen 1, a 320x240 corner, 15 fps, DirectShow mic "Microphone (2-
Realtek(R) Audio)". This laptop's mic records silence (a hardware fault), but the audio stream and the
shipped mux are real. After 12 s the owner is ended with **TerminateProcess**, the Windows SIGKILL. The
orphaned capture ffmpeg keeps running. A second process (that tree's real `agenteyes.exe`) runs
`package` while the orphan writes. QA then ends the orphan with CTRL_C on its console, which is **QA's
tool and not a product path**: on Windows the product's own start-up reap kills an orphan at once,
#166. Then `package` runs again. The captured media was deleted afterwards.

| Step | FIX `1927c62` (`run-rc-head.txt`) | PRE-#156 `main 17c244d` (`run-rc-main.txt`, `run-rc-main-step3.txt`) |
|---|---|---|
| claim | `TryAcquire ... claimed`, `NoteWriter: ... video pid 15052 recorded` | none: no `capture.owner.json` |
| after the owner's kill | orphan 15052 alive; raw.mp4 48 B (plain MP4: no moov until the end) | orphan 20580 alive |
| 2nd process `package` while it writes | **exit 1: `refusing to package ...: ... video ffmpeg pid 15052 is STILL RUNNING`**, no attempt | **admitted**: exit 1 `could not read the audio channel count`, `State=failed Attempts=1` |
| a second foreign `package` | - | admitted again: **`State=failed Attempts=2`** |
| orphan ended (CTRL_C) | raw.mp4 304401 B, `video audio 17.93` | raw.mp4 5882699 B, `video audio 334.53` |
| `package` after | exit 0, **`State=done Attempts=1`**, `AttemptedBy="attempt 1 ... by agenteyes pid 20736 on SORENLAPTOP"`, `capture.owner.json` absent | exit 0, `State=done` **`Attempts=3`** |
| file | `recording.mp4` 370192 B, `video audio 17.93`, **full decode exit 0** | decodes |

Read this honestly. **Main did NOT lose this recording.** Two foreign passes burned two of its three
attempts on a healthy live capture, and it survived only because its third attempt came after the
writer ended. One more foreign pass while it was writing would have marked it failed. That is the
issue's own defect, reproduced on a real Windows capture. On the fix the same scenario consumed zero
foreign attempts and finished at Attempts 1.

An earlier no-audio arm on the fix (`run-rc-head-noaudio.txt`) is the same shape: declined while the
orphan wrote. With audio `None` the capture writes `recording.mp4` directly and there is no mux, so it
is not counted as the criterion-4 arm.

On the fix, the claim's log lines (`TryAcquire`, `OwnerForManifest`, `NoteWriter`) come from the real
capture, not from a hand-written owner file.

## (7) The full solution suite, both arms - PASS

Run on `1927c62`, SDK 10.0.301 / runtime 8.0.21, from this laptop:

| Arm | Result |
|---|---|
| `dotnet test AgentEyes.sln -c Release` | `Passed! - Failed: 0, Passed: 2437, Skipped: 15, Total: 2452, Duration: 1 m 15 s` |
| `AGENTEYES_FFMPEG_TESTS=1 dotnet test AgentEyes.sln -c Release` | `Passed! - Failed: 0, Passed: 2456, Skipped: 0, Total: 2456, Duration: 1 m 32 s` |

**The suite kills only its own ffmpeg - with a positive control.** Before both runs I started a sentinel
ffmpeg of my own (pid 22576, `-re` realtime anullsrc, 1500 s), confirmed it alive after 8 s, and checked
it after each arm: **alive after the default suite, alive after the opt-in suite**. Then I stopped it
myself, after matching its start time.

A first sentinel attempt was invalid. Without `-re`, ffmpeg finished its 1500 s of null audio in
seconds, so its absence afterwards meant nothing. It was replaced, not counted.

---

## Observations (non-blocking, for the lead)

- **O1 - the log text now overstates the elevated risk.** `DescribeUnreadable`'s Windows string and
  the method summary say error 5 may be "possibly an elevated process; the elevated case is
  unmeasured". It is now measured: an elevated process of the same user never reaches that branch on
  .NET 8, because the right .NET asks for is granted across elevation. On Windows the line fires for
  other users' processes and protected processes. Worth a one-line text fix, so a field log does not
  send someone chasing elevation. Comment and log text only; no behaviour changes.
- **O2 - two mappings are not pinned, and two of the 14 are no-ops on Windows.** M3 survives. That is
  expected and measured: an exited pid whose handle is still held makes `GetProcessById` throw
  `ArgumentException: Process with an Id of 23512 is not running.`, so the `HasExited` branch is
  reachable on Windows only in a race. The `InvalidOperationException` ("exited between two calls")
  mapping is equally unpinned. Both are main's code from #128, unchanged here.
  `IsSameProcess_ThisUsersZombie_ReturnsFalse` and `DescribeUnreadable_PidOne_SaysAnotherUser` return
  early on Windows and pass without asserting.
  **Inference, not measured:** on CI's ADMIN Windows runner, pid 4 is probably readable at the limited
  right, so the six pin tests pass there by start-time comparison and would not catch M1. This
  non-admin laptop is where the catch is actually exercised on Windows.

## What I did NOT cover

- **A real elevated AgentEyes.** It needs a UAC consent click. The four High-IL processes of this user
  are stand-ins; none is an ffmpeg started by an elevated AgentEyes.
- **The reaper's KILL across elevation.** After the predicate correctly answers TRUE for an elevated
  orphan whose owner died, Windows `ReapOrphans` kills it at once (`RequestGracefulExit` is false on
  Windows). Normal mandatory-integrity policy denies a Medium process `PROCESS_TERMINATE` on a High
  one. Then `KillTree` would throw from `ReapOrphans`, which has no per-record catch, and the start-up
  reap would abort. The recording would be safe; the other records would be left. **Not measured:**
  `OpenProcess(PROCESS_TERMINATE)` from here SUCCEEDED on all four stand-ins (nothing was sent). That
  contradicts the usual policy, so the stand-ins may not represent an elevated ffmpeg for this right.
  This is outside the predicate. It is the same family as #170 and #166, and worth one measurement
  once someone can click UAC.
- **Another interactive Windows user's process.** No user was created. SYSTEM-owned processes stand in
  for the class (the same error 5 at the limited right).
- **An ADMIN-token run on Windows** (CI's runner): not run by me; see O2.
- **A full `RepairService.RunAsync` pass on Windows** with a foreign-pid crash (thumbnail backfill,
  housekeeping, drain). I showed `FindUnfinished` admits it and `Thumbnails.FindMissing` completes, the
  call that used to throw. The whole pass ran only on the developer's Mac.
- **A real `serve` start-up reap on Windows, and the repair timer:** not run (#161; the owner's app
  holds this machine's library and port).
- **Byte-identity of the committed gate file with the gate's original:** the Mac is unreachable.
- **Linux and macOS:** not re-run by me; CI only.
- **n=1:** one real crash per arm. The CTRL_C that ended the orphans is QA's tool, not product
  behaviour. The audio in every real capture is silence (hardware).
- **Mislabelled line in my own main-arm run:** the "raw.mp4 after the writer ended" line in
  `run-rc-main.txt` is wrong. The writer was still running, because main writes no owner record for
  the script to find. `run-rc-main-step3.txt` says so and holds the corrected steps.

## Footprint (#161 and the hazards)

The CLI `package` runs and one `agenteyes screens` appended lines to the owner's real
`%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-20261116.log`. That cannot be redirected for a real process.
Every recording read or written was under this session's scratch directory. No service was started.
The owner's running AgentEyesApp (pid 16160), recordings, presets and audio settings were not
touched. The only processes signalled were the two harness owners (TerminateProcess), the two orphaned
capture ffmpegs those harnesses started (CTRL_C, after matching parent and command line), and my own
sentinel ffmpeg. I changed no product code and merged nothing.
