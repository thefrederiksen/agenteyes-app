# Issue #9 - Developer Handoff

Issue: [CenCon] Smoke scripts launch a month-stale binary instead of the x64 Release build
Branch: issue-9-smoke-x64-paths

## What changed

Seven scripts referenced the non-x64 `bin\Release\` output (or fell back to it), which on an
old checkout holds a month-stale binary. All now reference ONLY `bin\x64\Release\`, fail
loudly with the exact expected path and the build command when the binary is absent, and a
new test guards against reintroduction.

| File | Change |
|------|--------|
| `scripts/api-smoke.ps1` | Both binary paths (`AgentEyesApp.exe` at old :10, `agenteyes.exe` at old :89) now x64; both resolved and existence-checked at the TOP of the script, before any process is touched. |
| `scripts/gui-smoke.ps1` | Both paths (old :18-19) now x64; the existing missing-binary checks now print the expected path and build command. |
| `scripts/py-client-smoke.ps1` | App path (old :11) now x64; missing-binary check added before launch. |
| `scripts/run-all.ps1` | CLI path (old :20) now x64; missing-binary check added right before the selftest step (after the build step, so a fresh build can produce it). |
| `scripts/try.cmd` | `CLIBIN` (old :9) now x64; error message now names the expected path; build hint updated to `dotnet build AgentEyes.sln -c Release`. |
| `scripts/doc-companion-demo.ps1` | REMOVED the try-x64-fall-back-to-stale path array (old :25-26) - single x64 path, loud failure. |
| `scripts/qa-walk-companion-demo.ps1` | Same fallback array removed (old :23-24) - single x64 path, loud failure. |
| `tests/AgentEyes.Tests/ScriptBinaryPathTests.cs` | NEW - the reintroduction guard (criterion 4). |

Not touched: `scripts/build-release.ps1` (its `*-win-x64.exe` strings are dotnet-publish
artifact names under `dist\release`, not `bin\` build output), `scripts/write-manifest.ps1`
(reads `dist\release`), `scripts/spikes/m0-ddagrab-soak.ps1` (runs the INSTALLED ffmpeg under
`%LOCALAPPDATA%\AgentEyes\app`, not a repo build output), `scripts/run-dev.ps1`,
`scripts/new-release.ps1`, `scripts/package-plugin.ps1`, `scripts/make-*.ps1` (no built-binary
references).

## Acceptance criteria -> how to verify

### AC1 - api-smoke.ps1 and gui-smoke.ps1 reference only bin\x64\Release\ paths

Implemented: both scripts define their binary paths once, as
`...\bin\x64\Release\net8.0-windows10.0.19041.0\...`.

QA check (no app launch needed):

    grep -rniE "bin[\\/]+(Release|Debug)" scripts/

Expected: NO matches (exit 1 from grep). Bad: any match = defect. Then confirm the
instrument sees paths at all (empty-result arm):

    grep -rn "bin.x64.Release" scripts/

Expected: hits in api-smoke.ps1 (x2), gui-smoke.ps1 (x2), py-client-smoke.ps1, run-all.ps1,
try.cmd, doc-companion-demo.ps1, qa-walk-companion-demo.ps1. Zero hits here means the grep
instrument or the corpus is broken, NOT a clean run.

### AC2 - every other script corrected; grep finds no remaining non-x64 reference

Implemented: py-client-smoke.ps1, run-all.ps1, try.cmd fixed the same way; the two demo
scripts additionally had their x64-then-stale FALLBACK arrays removed (the CLAUDE.md
"no fallback programming" rule - assumption 1 in the issue).

QA check: same grep as AC1 covers the whole `scripts/` tree recursively. Also eyeball
`scripts/doc-companion-demo.ps1` and `scripts/qa-walk-companion-demo.ps1` to confirm the
`$exe` candidate ARRAY is gone (single path + Test-Path fail).

### AC3 - missing binary = clear error naming expected path and build command, no fallback

Implemented: every launching script checks `Test-Path` on the exact x64 binary it intends to
run BEFORE touching any process, and on failure prints three lines - a FAIL line, the full
expected path, and `build it: dotnet build AgentEyes.sln -c Release` - then `exit 1`. There
is no second path to try anywhere.

QA check (silent, instant - the check precedes any app launch, so nothing starts and no
audio records):

    Rename-Item src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe AgentEyesApp.exe.hidden
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\api-smoke.ps1 -Confirm
    # then rename back

Expected output (verified by the developer on this branch, exit code 1):

    API-SMOKE: FAIL (app binary not found - it has not been built)
      expected: D:\...\src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe
      build it: dotnet build AgentEyes.sln -c Release

Bad: the app launches anyway (a fallback survived) or an obscure downstream error. Remember
to rename the exe back.

### AC4 - a test fails if a non-x64 build-output path is reintroduced under scripts/

Implemented: `tests/AgentEyes.Tests/ScriptBinaryPathTests.cs` (text-level scan, per the
issue's assumption 2 - scripts are not compiled, so there is no IL to inspect). Regex:
`\bbin[\\/]+(Release|Debug)\b` - fires on any bin directly followed by a configuration
segment; `bin\x64\Release` does not match. Fail-closed arms per DEVELOPMENT_METHOD.md 6c:

- `Scripts_AllFiles_ContainNoNonX64BuildOutputPath` - the guard; reports file:line of every offender.
- `ScriptsScan_KnownLaunchScripts_AreAllInTheCorpus` - instrument check: the scan must
  actually visit api-smoke/gui-smoke/py-client-smoke/run-all/try.cmd; a rename or an
  empty scan fails here instead of passing silently.
- `ScriptsScan_X64BuildOutputPath_IsPresentInCorpus` - presence arm: the corpus must still
  contain literal x64 build-output paths, so the scan cannot pass on an empty field.
- `StaleBinPathDetector_KnownBadReference_Fires` (4 cases) - committed mutation evidence:
  the detector fires on the exact pre-fix strings (api-smoke :10 form, try.cmd form,
  forward-slash form, Debug form).
- `StaleBinPathDetector_X64OrNonBuildPath_DoesNotFire` (3 cases) - the x64 path and the
  publish artifact names (`*-win-x64.exe`) do not false-positive.

Honest limit (stated in the test's doc comment): a text scan cannot see a stale path
assembled at runtime from concatenated fragments; it guards the literal-path form, which is
the only form the repo's scripts use and the form the defect shipped in.

Live mutation run by the developer: appending
`# MUTATION: src\AgentEyes.Core\bin\Release\net8.0\agenteyes.exe` to run-all.ps1 produced

    Failed AgentEyes.Tests.ScriptBinaryPathTests.Scripts_AllFiles_ContainNoNonX64BuildOutputPath
    run-all.ps1:62: # MUTATION: src\AgentEyes.Core\bin\Release\net8.0\agenteyes.exe

then the mutation was reverted. QA can repeat this in one minute:
add any `bin\Release` line to any script under `scripts/`, run
`dotnet test AgentEyes.sln -c Release --filter "FullyQualifiedName~ScriptBinaryPathTests"`,
see the named failure with file:line, revert.

### AC5 - QA runs api-smoke.ps1 against a freshly built binary and shows it exercised that build

This one is QA's to produce (running-app proof). Suggested drive:

1. `dotnet build AgentEyes.sln -c Release` (note the version:
   `(Get-Item src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe).VersionInfo.FileVersion`
   and the file's LastWriteTime - it must be from the build you just ran, not 2026-07-07).
2. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\api-smoke.ps1 -Confirm`
   (HEAVY: launches the app, records audio, runs ffmpeg/Whisper - this issue is squarely
   about the smoke scripts, so the smoke is in scope for QA).
3. The smoke's `[PASS] version  vX.Y.Z` line comes from `GET /version` on the app the script
   launched - assert it matches the freshly built binary's version from step 1.

The smoke is focus-free (REST on 127.0.0.1:7882 against a --tray app); do not
force-foreground anything. The recording HUD is capture-excluded (WDA_EXCLUDEFROMCAPTURE),
so recording state is asserted via `/status`, never a screen grab.

### AC6 - build clean, tests Failed: 0

Developer gate run on this branch:

    dotnet build AgentEyes.sln -c Release   -> Build succeeded. 0 Error(s)
    dotnet test  AgentEyes.sln -c Release   -> Passed! Failed: 0, Passed: 830, Total: 830

MACHINE NOTE for QA: this dev box has no .NET 8 WindowsDesktop runtime (only 3.1/5/6/10), so
`dotnet test` aborts with "install Microsoft.WindowsDesktop.App 8.0". Set
`$env:DOTNET_ROLL_FORWARD='LatestMajor'` first and the suite runs (on 10.x) - that is how
the numbers above were produced. A box with the 8.0 desktop runtime installed needs nothing.

## Smoke scoping for QA

- api: YES (AC5 requires it - the change is the smoke scripts themselves).
- gui: optional; gui-smoke.ps1 got the same mechanical path fix as api-smoke.ps1, and AC1/AC2
  are provable by grep. Run it only if you want belt-and-braces.

## CenCon impact

No drift - no component map or privacy posture change. Scripts and a test only; no product
code touched.

I believe this is finished.
