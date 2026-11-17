# Issue #196 - handoff to QA

`scripts/new-release.ps1` bumped four hard-coded csproj files and missed `src/AgentEyes.DesktopApp`
(added by #116). Branch `issue-196-release-script-all-versions`, from main 02a4622.

Developer pass run on a Mac (macOS 26, arm64). **`AgentEyes.sln` cannot build on a Mac, so the Windows
gate was NOT run by me.** QA runs it on Windows.

## What changed

| File | Change |
|------|--------|
| `scripts/new-release.ps1` | No list. Finds every tracked `*.csproj` / `*.props` / `*.targets` with a `<Version>` element outside XML comments, excluding `docs/` (frozen proof harnesses). Validates EVERY file (exactly one element; the bump actually lands) before writing ANY. Checks the exit code of `git ls-files`, `git add`, `git commit`, `git tag`. Paths are forward-slash (from `git ls-files`). |
| `tests/Shared/ReleaseVersionTests.cs` | New. Same discovery rule over the file system (skipping `.git`, `bin`, `obj`, top-level `docs`). Three tests: the five known projects are found (so a search that finds nothing fails); each declares exactly one; all agree, with a failure message naming every out-of-step file against `AgentEyes.App.csproj` (the one `release.yml` checks the tag against). |
| `tests/AgentEyes.Tests/AgentEyes.Tests.csproj`, `tests/AgentEyes.Tests.Mac/...csproj`, `tests/AgentEyes.Tests.Linux/...csproj` | Link the shared test. The Linux suite also gets the `RepoRoot` assembly metadata the Windows and macOS suites already stamp. |

`release.yml` is unchanged - criterion 4, and the issue keeps what it publishes out of scope.

## A second defect found while reproducing (same script, fixed in the same change)

The old script never checked a git exit code. On macOS/Linux its backslash paths made `git add` fail,
`git commit` then committed nothing, and the script **still created tag v9.9.9 on the un-bumped commit
and exited 0** (proof log, C1: `HEAD and tag` both show 02a4622). On Windows the backslash paths work,
so that particular trigger is Unix-only - but any failed commit (a hook, a lock, an identity problem)
would have been tagged the same way on Windows. Now: `git commit failed (exit 1); NOT tagged`.

## Acceptance criteria

| # | Criterion | Evidence (all in `proof-macos.log`, produced by `proof.sh`; every scenario on a throwaway clone) |
|---|-----------|------------------------------------------------------------------------------------------|
| 1 | Reproduced | C1: old script on 02a4622, bump to 9.9.9 -> App, Core, Setup, Setup.Cli at 9.9.9, **DesktopApp at 1.12.0**. |
| 2 | All five move; a sixth is picked up | C2: throwaway `tools/AgentEyes.Throwaway` committed at 1.12.0 -> `version files found: 6`, all six at 9.9.9, tag v9.9.9 on the release commit with all six files in it, test green afterwards. The clone, and the throwaway project with it, deleted. |
| 3 | Check fails on purpose, naming the file | C3: DesktopApp at 1.11.0 -> red on BOTH the macOS and Linux suites: `src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj declares 1.11.0`. C3b: a new sixth project at 1.0.0 -> red, naming `tools/AgentEyes.Throwaway/AgentEyes.Throwaway.csproj`. |
| 4 | No release behaviour change for a correctly bumped tree | `release.yml` untouched; its "Run tests" step gains 3 tests that pass when all versions agree. On the real tree the test is green (all five at 1.12.0). The script's output for a good tree: same commit message `Release vX`, same tag `vX`, same five files plus any new one. |
| 5 | Gates on all three platforms, CI | See below. Windows: NOT run by me. CI: not yet - it runs on the PR. |

Also proven: a refusal (a project with two `<Version>` elements) changes nothing - `git status` empty;
and the real working tree's fingerprint (HEAD, porcelain status, `v9*` tags, hash of every declared
version) is identical before and after the proof run.

## Gates run here (devthrottle-mac-mini)

SDK 10.0.403 building net8.0. There is no .NET 8 runtime on this machine, so the test hosts ran with
`DOTNET_ROLL_FORWARD=LatestMajor` on runtime 10.0.x (the convention #118 used on this machine).

| Gate | Result |
|------|--------|
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | Passed 433, Skipped 2, Failed 0, Total 435 |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` (on macOS) | Passed 474, Skipped 3, Failed 0, Total 477 |
| `ReleaseVersionTests` filtered, each suite | 3 of 3 passed |
| `dotnet build AgentEyes.sln -c Release` + `dotnet test AgentEyes.sln -c Release` | **NOT RUN - cannot build on a Mac. QA: run on Windows.** The shared test is linked into `AgentEyes.Tests`; I could not compile it there. |
| A real Linux host | Not run; the Linux suite ran on macOS. CI's ubuntu job covers it. |

## What reads the DesktopApp's version (the scope question)

Two things, and no updater:

1. **The About box** - `src/AgentEyes.DesktopApp/Views/MainWindow.axaml.cs`, `About_Click`:
   `typeof(MainWindow).Assembly.GetName().Version?.ToString(3)` -> "AgentEyes X.Y.Z for Linux ...".
   That is the number a user would read back to us. (Unrelated, NOT fixed here: the text says "for Linux"
   on every platform. Worth its own issue.)
2. **The macOS bundle** - `scripts/mac/build-app.sh` reads `<Version>` from the DesktopApp csproj with
   `dotnet msbuild -getProperty:Version` and writes it into `Info.plist` as `CFBundleShortVersionString`
   and `CFBundleVersion` - what Finder's Get Info and macOS itself report for AgentEyes.app.

The window title is the fixed string "AgentEyes" (no version). Nothing in the DesktopApp compares its
version with a release (no updater). So a stale value does not break behaviour; it misleads the About
box and the bundle metadata - exactly where a user report would come from.

Note for QA: an existing test already half-covered this. `MacBundleTests.InfoPlist_Version_IsFilledInByTheBuild_AndTheAppVersionIsTheEngines`
asserts the DesktopApp assembly version equals the engine's. It runs in the Linux suite and the Windows
`AgentEyes.Tests.DesktopApp`, not the macOS suite, and compares only DesktopApp against Core - App,
Setup and Setup.Cli were never compared. And `release.yml`'s "Run tests" step runs
`tests/AgentEyes.Tests` only, which did not contain it. The new test covers every declared version in all
three suites - and because it is linked into `tests/AgentEyes.Tests`, **the release workflow's own "Run
tests" step now goes red on a disagreeing tree before anything is published**, with no change to
`release.yml`. (Expected from reading release.yml line 63; not exercised - no tag or release run was made.)

## How QA verifies

1. On Windows: `dotnet build AgentEyes.sln -c Release` and `dotnet test AgentEyes.sln -c Release`; confirm
   `ReleaseVersionTests` ran (3 tests in `AgentEyes.Tests`).
2. Red on purpose (Windows too, since the Windows build is the one I could not see): in a scratch copy,
   change one `<Version>`, run `dotnet test ... --filter FullyQualifiedName~ReleaseVersionTests`, read the
   message naming the file. Revert.
3. Re-run `proof.sh` (bash; adjust the three paths at the top) or reproduce any scenario by hand on a
   clone. Never run `new-release.ps1` against a real checkout - it commits and tags.
4. No heavy smoke is relevant: nothing here launches or changes the app.

## CenCon impact

No drift: no component map or privacy-posture change. The release procedure is unchanged for a person
running it; it now touches every versioned project and stops instead of tagging on a failure.

I believe this is finished.
