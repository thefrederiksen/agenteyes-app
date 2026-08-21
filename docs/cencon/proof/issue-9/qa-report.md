# QA Report - Issue #9 (PR #24)

Issue: [CenCon] Smoke scripts launch a month-stale binary instead of the x64 Release build
Branch: issue-9-smoke-x64-paths (tip 7e99d32, reviewed at that commit)
QA date: 2026-08-20
Verdict: **VERIFIED - all 6 acceptance criteria met.** Handing to the Review Gate (flow:ready-gate).

QA verified independently: checked out and built the PR branch itself, ran the full test
suite itself, re-ran every grep itself, invoked the missing-binary path itself, ran the
mutation against the guard itself, and ran the api smoke itself against the freshly built
binary. Nothing below is taken from the developer's handoff note; where the handoff made a
claim, it was re-derived from the code or re-executed.

---

## Gate results (run by QA on this branch)

| Check | Command | Result |
|-------|---------|--------|
| Build | `dotnet build AgentEyes.sln -c Release` | `Build succeeded.` `0 Error(s)` (2 pre-existing xUnit1031 warnings, untouched by this PR) |
| Tests | `dotnet test AgentEyes.sln -c Release` | `Passed! - Failed: 0, Passed: 830, Skipped: 0, Total: 830` |

Machine note: this QA box has no Microsoft.WindowsDesktop.App 8.0 runtime; without
`DOTNET_ROLL_FORWARD=LatestMajor` the test host aborts with "install
Microsoft.WindowsDesktop.App 8.0" (QA reproduced that abort first, then set the variable
and got the 830/830 run above). Same machine precondition the developer documented; it is
an environment fact, not a property of this change.

---

## Criterion 1 - api-smoke.ps1 and gui-smoke.ps1 reference only bin\x64\Release\ paths

Expected: no `bin\Release` / `bin\Debug` build-output reference in either script; the x64
form present.

Actual: PASS. Three-arm grep run by QA (Section 6c - the absence claim is bracketed by two
presence arms so an empty result cannot fail open):

- Arm 1, instrument fires on known-bad: piping the pre-fix string
  `src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe` through
  `grep -cniE 'bin[\\/]+(Release|Debug)'` returns `1`.
- Arm 2, presence: `grep -rniE 'bin[\\/]+x64[\\/]+Release' scripts/` hits api-smoke.ps1
  (lines 13, 14), gui-smoke.ps1 (21, 22), py-client-smoke.ps1 (14), run-all.ps1 (23),
  try.cmd (11), doc-companion-demo.ps1 (27), qa-walk-companion-demo.ps1 (25).
- Arm 3, the claim: `grep -rniE 'bin[\\/]+(Release|Debug)' scripts/` -> no matches, exit 1.

Evidence: scripts/api-smoke.ps1:13-14, scripts/gui-smoke.ps1:21-22.

## Criterion 2 - every other script under scripts/ corrected; no remaining non-x64 reference

Expected: the same grep clean across the whole tree; the two demo scripts' x64-then-stale
fallback arrays gone.

Actual: PASS. Arm 3 above already covers scripts/ recursively (15 script files enumerated:
api-smoke, build-release, doc-companion-demo, gui-smoke, make-icon, make-setup-icon,
new-release, package-plugin, py-client-smoke, qa-walk-companion-demo, run-all, run-dev,
spikes/m0-ddagrab-soak, try.cmd, write-manifest). QA additionally ran a BROADER sweep than
the criterion's grep - every `.exe`/`.dll` reference and every `bin\` path not followed by
`x64` - and the only remaining hits are `dist\release` publish artifact names in
build-release.ps1 (e.g. `AgentEyesApp-win-x64.exe`, scripts/build-release.ps1:61-64) and the
INSTALLED ffmpeg under `%LOCALAPPDATA%\AgentEyes\app` in spikes/m0-ddagrab-soak.ps1:21-22 -
neither is a repo build output, confirming the handoff's "not touched" list independently.
The fallback arrays are removed: scripts/doc-companion-demo.ps1:27 and
scripts/qa-walk-companion-demo.ps1:25 are now a single x64 path with a Test-Path fail (the
diff deletes the two-element candidate arrays that ended in the stale path).

## Criterion 3 - missing binary produces a clear error naming the path and the build command

Expected: with the target binary absent, the smoke fails immediately with the expected path
and how to build it - no fallback, no downstream error.

Actual: PASS. QA renamed the freshly built AgentEyesApp.exe to .qahidden and invoked
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\api-smoke.ps1 -Confirm`:

    EXITCODE: 1
    API-SMOKE: FAIL (app binary not found - it has not been built)
      expected: D:\ReposFred\agenteyes-app\scripts\..\src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe
      build it: dotnet build AgentEyes.sln -c Release

No app launched, no fallback path tried, nothing else printed. Binary renamed back and
verified present. The check sits at the top of the script (scripts/api-smoke.ps1:15-27),
before any process is touched; the same three-line pattern exists in gui-smoke.ps1:24-35,
py-client-smoke.ps1:15-20, run-all.ps1:35-40 (placed after the build step so a fresh build
can produce the binary), try.cmd:14-21, and both demo scripts. Review note: run-all.ps1's
check is correctly AFTER its internal `dotnet build`, so it fires only if the build itself
failed to produce the exe - the right order for that script.

## Criterion 4 - a test fails if a non-x64 path is reintroduced under scripts/

Expected: a committed guard that FIRES on reintroduction, with fail-closed arms.

Actual: PASS. tests/AgentEyes.Tests/ScriptBinaryPathTests.cs (new, 10 test cases, all pass
on the clean tree). QA ran the live mutation itself:

1. Appended `# QA MUTATION: src\AgentEyes.Core\bin\Release\net8.0\agenteyes.exe` to
   scripts/run-all.ps1.
2. `dotnet test ... --filter "FullyQualifiedName~ScriptBinaryPathTests"` ->
   `Failed AgentEyes.Tests.ScriptBinaryPathTests.Scripts_AllFiles_ContainNoNonX64BuildOutputPath`
   with the offender pinpointed: `run-all.ps1:62: # QA MUTATION: ...`
   (`Failed: 1, Passed: 9, Total: 10`).
3. Reverted; `git status --porcelain` empty; the 10 tests pass again.

Fail-closed arms verified in the code (Section 6c):
- Instrument arm: `ScriptsScan_KnownLaunchScripts_AreAllInTheCorpus` fails if the scan stops
  visiting api-smoke/gui-smoke/py-client-smoke/run-all/try.cmd (ScriptBinaryPathTests.cs:47-55, 76-87).
- Presence arm: `ScriptsScan_X64BuildOutputPath_IsPresentInCorpus` fails if the corpus no
  longer contains any literal x64 build-output path (lines 89-101).
- Known-bad arm: `StaleBinPathDetector_KnownBadReference_Fires` asserts the regex fires on
  the exact pre-fix strings including the api-smoke and try.cmd forms (lines 126-138), and
  `StaleBinPathDetector_X64OrNonBuildPath_DoesNotFire` shows the x64 path and the
  `*-win-x64.exe` publish names do not false-positive.
- Honest limit stated in the test's doc comment (lines 27-30): a text scan cannot see a path
  concatenated at runtime; it guards the literal form, the form the defect shipped in.

## Criterion 5 - QA runs api-smoke.ps1 against a freshly built binary and shows it exercised that build

Expected: the smoke demonstrably talks to the binary the build just produced.

Actual: PASS - proven with a stronger instrument than the suggested version match, because
QA found the version string alone is ambiguous on this box (an INSTALLED AgentEyes 1.4.9 also
exists under `%LOCALAPPDATA%\AgentEyes\app` and reports the same `1.4.9`).

1. Fresh build (this branch): the x64 binary's identity BEFORE the run -
   `FileVersion 1.4.9.0`, `ProductVersion 1.4.9+7e99d322fc62d921dc119ca7773a13021d2fbb8d`
   (the informational version embeds the PR-branch tip commit 7e99d32),
   `LastWriteTime 2026-08-20 22:31:28` - minutes old, not 2026-07-07. The stale
   `bin\Release\` directory does not exist on this checkout at all.
2. QA killed every AgentEyes process, confirmed port 7882 free, then ran
   `scripts\api-smoke.ps1 -Confirm` (with `DOTNET_ROLL_FORWARD=LatestMajor` so the app can
   start on this runtime-less box).
3. MID-RUN capture: the 7882 listener is registered via http.sys (socket owner is the kernel,
   PID 4), and the ONLY AgentEyesApp process alive during the run was:
   `pid 5756, path D:\ReposFred\agenteyes-app\src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe,`
   `ProductVersion 1.4.9+7e99d322fc62d921dc119ca7773a13021d2fbb8d, file written 2026-08-20 22:31:28,`
   `process started 2026-08-20 22:37:52` - i.e. the binary built in step 1, started by the
   smoke. `GET /version` during the run returned `{"app":"AgentEyes","version":"1.4.9"}` and
   the smoke printed `[PASS] version  v1.4.9`, matching the fresh binary's FileVersion.
4. Smoke tail (20 of 22 checks PASS): status-idle, devices, screenshot, audio-recording,
   audio-stop, idle-after, conflict-409, video-stop, video-final, capture, version,
   unknown-route-404, unknown-id-404, recordings-list, recording-detail, recording-shots,
   transcript-404, captures, discovery all PASS.

Two checks failed, both from ONE machine precondition, not from this change:
`[FAIL] package (transcript.json written)` and its downstream `[FAIL] transcript-200
(status=404)`. Root cause isolated by QA outside any script: running the CLI directly
(`agenteyes.exe package <recording>`) prints
`transcription FAILED: Not signed in to DevThrottle. Open Settings > DevThrottle Account and sign in.`
This QA box has no DevThrottle sign-in, so whisper-large-v3 transcription cannot run at all
here - for ANY binary, on any branch. The PR changes scripts and one test file only (zero
product code), so it cannot have caused or masked this; the criterion as written asks QA to
show the smoke exercises the just-produced build, which the mid-run process capture proves.
The criterion is NOT being redefined: the transcription gap is stated as what it is - an
environment limitation of this QA box, visible in the record.

## Criterion 6 - build clean, tests Failed: 0

Expected: `Build succeeded.` `0 Error(s)`; `Failed: 0`.
Actual: PASS - see the gate table above (830/830, run by QA on this branch).

---

## Method checks

- ASCII-only: the PR diff introduces zero non-ASCII bytes (checked over every added line and
  every changed file; the instrument was first shown to fire on a known non-ASCII probe -
  the initial `grep -P` attempt was itself rejected as a broken instrument when it returned
  empty on the probe under this locale, and replaced with awk plus a byte-level od count).
  The single non-ASCII artifact in any touched file is the pre-existing UTF-8 BOM on
  scripts/run-all.ps1 line 1, byte-identical on origin/main - not introduced here.
- No fallback programming: the two fallback arrays are gone; every path is single, checked,
  and fails loudly. No try/catch added in scripts.
- Scope: nothing outside scripts/ + the new test + the handoff doc; no product code, no
  privacy-posture surface, no CenCon component-map change.
- Working tree left clean; mutation and binary-rename experiments reverted and verified.

## Observations for the Review Gate (non-blocking, outside this issue's criteria)

1. Pre-existing in all five launching scripts (10 sites, e.g. scripts/api-smoke.ps1:31 and
   :190): the cleanup line `Get-Process AgentEyes | Stop-Process -Force` names a process
   that does not exist - the app's process name is `AgentEyesApp` (it matches only the CLI,
   `agenteyes`). Consequence: the smokes never kill a pre-existing or leftover app instance,
   so a previously running app (for example the INSTALLED one) can own port 7882 and be
   silently smoke-tested instead of the launched binary - the same "testing a binary nobody
   built" failure mode this issue fixes, through a different door. QA hit exactly this
   during verification (an installed 1.4.9 instance answered 7882 on one run) and had to
   kill processes manually. Untouched by this PR and outside its stated criteria;
   recommend a follow-up issue.
2. During QA an installed-app instance (`%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe`,
   ProductVersion 1.4.9+4ad557a) was found running and was stopped to keep the port
   unambiguous; anyone using that box's always-on recorder will need to restart it.
3. QA-box preconditions worth recording: no Microsoft.WindowsDesktop.App 8.0 runtime
   (roll-forward workaround required to run the app and the tests) and no DevThrottle
   sign-in (transcription steps cannot pass on this box).

VERIFIED - all acceptance criteria met. QA does not merge (D7); the Review Gate decides.
