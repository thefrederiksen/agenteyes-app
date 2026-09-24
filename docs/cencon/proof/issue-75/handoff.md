# Issue #75 - Developer handoff

Four HUD/preview call-graph guards failed on `main` since 9292e9a (#26) and blocked the v1.11.1
release. All four reported paths are FALSE. They come from the reachability walker in
`tests/AgentEyes.Tests/CompiledCode.cs`, not from product code. The fix is in the walker. No product
code changed.

## Root cause

#26 made `CompiledCode.Reachable` treat a type's static constructor (`.cctor`) as reached whenever
a member of the type is touched. That edge is REAL: a type initializer runs on whichever thread
touches the type first. The product builds its background workers in type initializers on purpose,
so that their thread bodies stay off the callers' threads:

- `PreviewLog` starts its appender thread from `.cctor`.
- `PreviewChores.Shared` is constructed in `.cctor`.
- `Config.Writer` (a `BackgroundFileWriter`) is constructed in `.cctor`.

Before this fix the walker counted every delegate it saw built (`ldftn X`) as a call from the method
that built it to X. So once a `.cctor` was reached, the walk followed:

- `new Thread(Loop)` into the thread body, and
- the `Action` parked in a private field (`_write`, `_perform`) into the method that does the disk work,

and reported that work as if the HUD thread or the preview drain did it.

Chains found by instrumenting the walk (parent of each reached node):

| Reported path | Chain the walker followed | Verdict |
|---|---|---|
| `BackgroundFileWriter::WriteToDisk -> File::WriteAllText` | `HudWindow::.ctor` -> `Config::get_HudWidth` -> (implicit) `Config::.cctor` -> `new BackgroundFileWriter(...)` -> `.ctor` builds `write ?? WriteToDisk` (ldftn) -> `WriteToDisk` | **FALSE**: the `.ctor` only stores the delegate in `_write`. The only place `_write` is read is `WriteOnce`, which is called only from `Loop`, the writer thread started by `Start()` from `Config.Load` at app startup. It never runs on the HUD thread. |
| `Config::WriteJson -> File::WriteAllText` | `HudWindow::.ctor` -> `Config::get_HudWidth` -> (implicit) `Config::.cctor` builds `new BackgroundFileWriter(FilePath, WriteJson)` (ldftn) -> `WriteJson` | **FALSE**: `WriteJson` is passed as the constructor's `write` argument and stored in `_write`. Same as the row above: only the writer thread's `WriteOnce` invokes it. On the HUD thread, `.cctor` runs `Environment.GetFolderPath` + `Path.Combine` (path strings, no file write) plus allocations; in practice `Config.Load` at startup touches the type first anyway. |
| `PreviewLog::Drain -> Log::Error` (+ Info/Warn -> `Log::Write`) | `PreviewTap::Drain` / `set_Publishing` -> `PreviewLog::Info` -> (implicit) `PreviewLog::.cctor` -> `StartAppender` -> `new Thread(Loop)` (ldftn) -> `Loop` -> `Drain` | **FALSE**: `Drain` is called only from `Loop`, which is the start delegate of the appender thread. `new Thread(...)` never invokes its delegate. `Thread.Start` runs it on the new thread. On the drain or HUD thread, `PreviewLog.Info` does one enqueue and one event set. |
| `PreviewLog::Loop -> Log::Error` | same chain up to `new Thread(Loop)` | **FALSE**: same reason. `Loop` is the thread body itself. |
| (also in the critical-paths guard) `PreviewChores::Carry` / `DoRemove` -> Directory/File | `PreviewTap::TryCreateAt` -> `PreviewChores::Prepare` -> (implicit) `PreviewChores::.cctor` -> `.ctor` builds `perform ?? Carry` (ldftn) -> `Carry` -> `DoRemove` | **FALSE**: `_perform` is read only by `Perform`, which is called only from `Loop`, the chores worker thread started by `new Thread(Loop)` in the `.ctor`. |

## What changed (tests only)

`tests/AgentEyes.Tests/CompiledCode.cs` has a new `DelegateHandoffs` pass, used by `Reachable`. By
default a delegate is still an edge from the method that builds it. There are exactly two
exceptions, and each is recognized from the IL instructions that consume the new delegate. The
compiler's method-group cache (`dup; stsfld <>O::...`) is looked through:

1. **Thread entry.** The delegate is the only argument of `new System.Threading.Thread(...)`. It is
   not an edge on the builder's thread.
2. **Parked in a private field.** The delegate goes into `stfld`/`stsfld` of a private field in the
   same assembly. This also covers a delegate passed as the LAST argument of an in-assembly
   constructor or non-virtual method, when every use of that parameter stores it straight into a
   private field (`p ?? Default` included). The edge moves to every method that READS the field.

Any other shape keeps the builder edge (fail closed). An edge is removed only when every naming of
it in that method is a handoff.

Other changes:

- `PreviewTapTests.TouchesTheFilesystem` / `TouchesTheFilesystemOrTheSharedLogger` changed from
  `private` to `internal`, so the decoy tests use the exact predicates the guards use. No assertion
  in any of the 4 guard tests changed.
- New decoys: `tests/AgentEyes.Tests/DelegateHandoffDecoys.cs`.
- New tests: `tests/AgentEyes.Tests/DelegateHandoffWalkTests.cs`.

## Acceptance criteria -> how QA verifies

| # | Criterion | Implemented | How QA verifies |
|---|---|---|---|
| 1 | All 4 guards pass; full release.yml test command green | walker fix | `dotnet test tests/AgentEyes.Tests/AgentEyes.Tests.csproj -c Release` -> `Failed: 0` (dev run: 1730 passed). Guard output: `docs/cencon/proof/issue-75/guard-tests.txt` |
| 2 | Per-path real/false with chain | table above (also in the PR body) | Read the table. Optionally re-instrument `Reachable` to print parents. |
| 3 | Walker changed -> a decoy proves it still flags a real HUD-thread write and a real drain -> shared-logger call | `DelegateHandoffWalkTests.HudFileWriteScan_RealUiThreadWrite_IsStillReported` (4 shapes: writing `.cctor`, parked delegate invoked on the thread, same-thread external invoker `List.ForEach`, ctor that also invokes its parameter) and `DrainLoggerScan_RealDrainToSharedLoggerCall_IsStillReported` (3 shapes: direct `Log.Error`, logging `.cctor`, parked logger invoked on the thread). Narrowness: product shapes copied member for member must NOT be reported. | Run `--filter FullyQualifiedName~DelegateHandoffWalkTests`. Mutation drills below. |
| 4 | #26 library tests still pass | untouched | `--filter FullyQualifiedName~LibraryFlatListTests` green (in the full run) |
| 5 | `dotnet build AgentEyes.sln -c Release` clean | - | `Build succeeded.`, `0 Error(s)` (19 pre-existing xUnit analyzer warnings, unchanged) |

## Mutation drills (run by the developer; QA should repeat them in an isolated worktree)

- **A - over-broad cut** (every delegate treated as a thread handoff): 4 must-report decoys went RED
  (parked-invoked write, ctor-also-invokes, `List.ForEach`, parked logger), plus both walk-level
  presence tests.
- **B - field readers ignored** (parked edge dropped instead of moved): the parked-write and
  parked-logger decoys went RED, plus both presence tests.
- **C - walker reverted to `main`** (new tests kept): the 4 product guards went RED, all 3
  narrowness decoys went RED, and both presence tests went RED. This is red-first evidence that the
  fix is what turns them green.

## Stated limits (unchanged or new)

- The thread-entry rule now applies to every user of `Reachable`, including the Library guards from
  #26. For those guards, a Library handler that hands work to `new Thread(...)` is no longer counted
  as reaching that work. WPF views have dispatcher affinity, so grouping a view from another thread
  fails at run time. `new Thread(Loop, maxStackSize)` and lambdas are not recognized, so they
  over-report (fail closed).
- The `<>O` look-through relies on the compiler's cache shape: the fast path jumps to the same
  consumer instruction.

## Smoke scope

None. This change is test-only static analysis and ships no product code. No app launch or smoke is
needed. One unrelated flake appeared once in a full run and passed on re-run:
`CameraPreviewTests` reported a stranded camera owned by the live always-on recorder's ffmpeg on this
laptop.

## CenCon impact

No drift. The component map and privacy posture are unchanged.

I believe this is finished.
