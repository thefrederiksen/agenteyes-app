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

---

# QA Round 2 - review-gate REJECT fix (2026-08-21)

Branch tip reviewed: 50d414c (round-1 tip was 7e99d32; QA proof commit 0de40e4; the fix is
50d414c only). QA date: 2026-08-21.
Verdict: **VERIFIED - the gate defect is fixed and all 6 acceptance criteria still hold.**
Handing to the Review Gate (flow:ready-gate).

## The defect under test

Review-gate round-1 REJECT, one blocking defect (issue #9 comment, 2026-08-21):
`tests/AgentEyes.Tests/ScriptBinaryPathTests.cs:40` - the reintroduction guard only
blacklisted `bin\Release` and `bin\Debug`, so a wrong-platform build path
(`bin\x86\Release\...`, `bin\arm64\...`) left every guard test green, violating AC4
("fail on ANY non-x64 build-output path").

## Scope of the fix (verified from the diff)

`git show 50d414c --stat`: exactly two files - `tests/AgentEyes.Tests/ScriptBinaryPathTests.cs`
(the guard) and `docs/cencon/proof/issue-9/handoff.md` (round-2 documentation). Zero product
code, zero script changes. Therefore the round-1 runtime evidence (AC3 missing-binary error,
AC5 api-smoke against the fresh 7e99d32 binary, mid-run process capture) remains valid and is
cited, not re-run - the smoke surface is byte-identical to what round 1 exercised.

## Gate results (run by QA on tip 50d414c)

| Check | Command | Result |
|-------|---------|--------|
| Build | `dotnet build AgentEyes.sln -c Release` | `Build succeeded.` `0 Error(s)` (same 2 pre-existing xUnit1031 warnings) |
| Tests | `dotnet test AgentEyes.sln -c Release` | `Passed! - Failed: 0, Passed: 834, Skipped: 0, Total: 834` |

Same machine note as round 1: `DOTNET_ROLL_FORWARD=LatestMajor` required on this box (QA
reproduced the abort without it first, then the 834/834 run above).

## The fixed guard - Expected vs Actual

Expected (AC4 as the gate reads it): the guard fails when ANY non-x64 build-output path is
reintroduced under scripts/ - platform-less, wrong-platform, wrong-configuration, or unknown.

Actual: PASS. The detector is now an allow-form, fail-closed regex
(`tests/AgentEyes.Tests/ScriptBinaryPathTests.cs:43-46`):

    \bbin[\/]+(?!x64[\/]+Release\b)[A-Za-z0-9_.-]+

The only continuation after `bin` that does not fire is exactly `x64\Release` (either
separator, any case). QA traced the gate's named forms through the regex and through the
committed theory cases (ScriptBinaryPathTests.cs:123-130 known-bad, 142-144 does-not-fire):

| Input form | Detector | Evidence |
|------------|----------|----------|
| `bin\Release\...` | FIRES | theory case line 123 + live mutation round 1 |
| `bin\Debug\...` / `bin/Debug` | FIRES | theory case line 126 |
| `bin\x86\Release\...` | FIRES | theory case line 127 + live mutation below |
| `bin/arm64/Release/...` | FIRES | theory case line 128 + live mutation below |
| `bin\x64\Debug\...` | FIRES | theory case line 129 + live mutation below |
| `bin\AnyCPU\Release\...` (unknown segment) | FIRES | theory case line 130 - unknown = defect, fail closed |
| `bin\x64\Release\...` | does not fire | DoesNotFire cases lines 142-143 |
| `AgentEyesApp-win-x64.exe` publish artifact | does not fire | DoesNotFire case line 144 |

Edge cases QA checked by reading the regex, beyond the committed cases: `bin\x64Release`
(missing separator) and `bin\x64\ReleaseCandidate` both fail the lookahead and FIRE -
near-miss forms are defects, not passes. Honest limit unchanged and still stated in the
test header (ScriptBinaryPathTests.cs:28-31): a text scan cannot see a path assembled at
runtime from fragments; every script in this repo uses the literal form.

## Mutation drill (run by QA - the guard was SEEN to fail)

Per Section 6c item 3, QA planted the gate's exact scenario itself on tip 50d414c:
appended three lines to `scripts/run-all.ps1` -

    # QA-MUTATION: $exe = "src\AgentEyes.App\bin\x86\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
    # QA-MUTATION: src/AgentEyes.Core/bin/arm64/Release/net8.0-windows10.0.19041.0/agenteyes.exe
    # QA-MUTATION: $old = "src\AgentEyes.App\bin\x64\Debug\net8.0-windows10.0.19041.0\AgentEyesApp.exe"

then ran `dotnet test AgentEyes.sln -c Release --filter "FullyQualifiedName~ScriptBinaryPathTests"`:

    Failed AgentEyes.Tests.ScriptBinaryPathTests.Scripts_AllFiles_ContainNoNonX64BuildOutputPath [10 ms]
    run-all.ps1:62: # QA-MUTATION: $exe = "src\AgentEyes.App\bin\x86\Release\...\AgentEyesApp.exe"
    run-all.ps1:63: # QA-MUTATION: src/AgentEyes.Core/bin/arm64/Release/.../agenteyes.exe
    run-all.ps1:64: # QA-MUTATION: $old = "src\AgentEyes.App\bin\x64\Debug\...\AgentEyesApp.exe"
    Failed! - Failed: 1, Passed: 13, Total: 14

All three wrong-platform/wrong-config plants - including the two forms that slipped past the
round-1 guard - are caught with file:line. Reverted the plant (`git checkout -- scripts/run-all.ps1`,
tree clean), re-ran the filter: `Passed! - Failed: 0, Passed: 14, Total: 14`. The guard has
now been observed in both states by QA on this tip.

## Acceptance criteria still hold on the fixed tip

- AC1/AC2 (x64-only references under scripts/): re-swept on 50d414c with the fail-closed form -
  `grep -rniE "bin[\/]+" scripts/ | grep -viE "bin[\/]+x64[\/]+Release"` -> zero matches
  (grep exit 1); presence arm: `bin\x64\Release` literals present in 7 scripts (api-smoke.ps1 x3,
  gui-smoke.ps1 x3, doc-companion-demo.ps1 x2, py-client-smoke.ps1 x2, qa-walk-companion-demo.ps1 x2,
  run-all.ps1 x2, try.cmd x2) - the instrument sees paths; the empty negative arm is a real absence.
- AC3 (missing-binary error) and AC5 (smoke exercises the fresh build): unchanged since round 1 -
  the fix commit touches no script - round-1 evidence stands (this report above: rename-away
  exit-1 error naming the exact path and build command; mid-run process capture of pid 5756
  running the 7e99d32-stamped binary, `[PASS] version v1.4.9`).
- AC4: the fixed guard, verified above.
- AC6: 834/834, Failed: 0, run by QA on this tip (gate table above).
- Handoff note documents the round-2 change: `docs/cencon/proof/issue-9/handoff.md` gained a
  "Round 2 - fix for the review-gate REJECT (2026-08-21)" section describing the defect, the
  allow-form regex, the four new theory cases, and the revised AC4 QA check. Verified present
  in the 50d414c diff.

## Method checks (round 2)

- ASCII-only: `git show 50d414c` contains 0 non-ASCII bytes (awk byte scan; the instrument was
  first shown to FIRE on a known non-ASCII probe before its empty result was believed).
- Scope: test file + handoff doc only; no product code, no privacy-posture surface.
- Working tree left clean; the mutation plant was reverted and the revert verified by git status
  and a green re-run.

VERIFIED - gate defect fixed, all acceptance criteria hold on tip 50d414c. QA does not merge
(D7); the Review Gate decides.

---

# QA Round 3 - second review-gate REJECT fix (2026-08-21)

Branch tip reviewed: 1b11732 (round-2 tip was 5aba634; the fix is 1b11732 only).
QA date: 2026-08-21.
Verdict: **VERIFIED - both halves of the round-2 gate defect are fixed and all 6 acceptance
criteria still hold.** Handing to the Review Gate (flow:ready-gate).

## The defect under test

Review-gate round-2 REJECT, one blocking defect in two halves:

(a) the round-2 detector required a literal segment after `bin\`
    (`...(?!x64[\/]+Release\b)[A-Za-z0-9_.-]+`), so a non-x64 path COMPOSED at the call
    site - `$platform = 'x86'` upstream then `"...\bin\$platform\Release\..."`, or a
    `"...\bin\" + $platform + ...` fragment concatenation - escaped every guard test;
(b) while escaping those forms, the guard's test name and text still claimed ANY
    reintroduced non-x64 build-output path was detected - an overclaim (an honestly stated
    limit passes the 6c gate; an overclaim does not).

## Scope of the fix (verified from the diff)

`git diff 5aba634..1b11732 --stat`: exactly two files -
`tests/AgentEyes.Tests/ScriptBinaryPathTests.cs` (the guard) and
`docs/cencon/proof/issue-9/handoff.md` (round-3 documentation). Zero product code, zero
script changes - so the round-1 runtime evidence (AC3 missing-binary error, AC5 api-smoke
against the fresh build) remains valid and is cited, not re-run; the smoke surface is
byte-identical to what round 1 exercised.

## Gate results (run by QA on tip 1b11732)

| Check | Command | Result |
|-------|---------|--------|
| Build | `dotnet build AgentEyes.sln -c Release` | `Build succeeded.` `0 Error(s)` (same 2 pre-existing xUnit1031 warnings) |
| Tests | `dotnet test AgentEyes.sln -c Release` | `Passed! - Failed: 0, Passed: 840, Skipped: 0, Total: 840` |

Same machine note as rounds 1-2: this box has no Microsoft.WindowsDesktop.App 8.0 runtime;
QA first reproduced the testhost abort without the workaround ("Test Run Aborted",
framework 8.0.0 not found), then set DOTNET_ROLL_FORWARD=LatestMajor and got the 840/840
run above. Suite grew 834 -> 840: the 5 new composed-path theory cases plus 1 new
does-not-fire case, matching the handoff's claim exactly.

## Half (a) - composed paths now fire: Expected vs Actual

Expected: a non-x64 bin path assembled from a variable, placeholder, or fragment
concatenation is rejected by the guard (fail closed - unverifiable text is a defect).

Actual: PASS. The detector dropped the trailing character class entirely
(`tests/AgentEyes.Tests/ScriptBinaryPathTests.cs:53-55`):

    \bbin[\/]+(?!x64[\/]+Release\b)

Nothing after `bin\` needs to be recognized to be rejected: the ONLY continuation that does
not fire is the literal `x64\Release`. A variable sigil (`bin\$platform`), a format
placeholder (`bin\{0}`), a cmd variable (`bin\%PLATFORM%`), a shell expansion
(`bin/${platform}`), and a quote at a fragment boundary (`"...\bin\" + $x`) all fire.
Committed theory evidence: `NonX64BinSegmentDetector_ComposedPath_Fires` (5 cases,
ScriptBinaryPathTests.cs:154-160) and the widened does-not-fire arm (4 cases, :170-175,
including a comment line mentioning `bin\x64\Release\.`).

## Mutation drill (run by QA - the guard was SEEN to fail on BOTH gate scenarios)

Per Section 6c item 3, QA planted the round-2 gate's exact scenarios itself on tip 1b11732.

Baseline first: `dotnet test ... --filter "FullyQualifiedName~ScriptBinaryPathTests"` ->
`Passed! - Failed: 0, Passed: 20, Total: 20` (legitimate literal `bin\x64\Release` usages
in 7 scripts stay green).

Plant (i) - variable-composed, appended to `scripts/run-all.ps1`:

    $platform = 'x86'
    $exe = "src\AgentEyes.App\bin\$platform\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"

Result - the guard FAILED, naming the plant with file:line:

    Failed AgentEyes.Tests.ScriptBinaryPathTests.Scripts_EveryTextualBinSegment_IsLiterallyX64Release [2 ms]
    run-all.ps1:63: $exe = "src\AgentEyes.App\bin\$platform\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
    Failed! - Failed: 1, Passed: 19, Total: 20

Reverted (`git checkout -- scripts/run-all.ps1`). Plant (ii) - fragment concatenation:

    $exe = $srcDir + "\bin\" + $platform + "\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"

Result - the guard FAILED again with file:line:

    Failed AgentEyes.Tests.ScriptBinaryPathTests.Scripts_EveryTextualBinSegment_IsLiterallyX64Release [3 ms]
    run-all.ps1:62: $exe = $srcDir + "\bin\" + $platform + "\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
    Failed! - Failed: 1, Passed: 19, Total: 20

Reverted; `git status` clean; filtered re-run -> `Passed! - Failed: 0, Passed: 20,
Total: 20`. Both forms that escaped the round-2 guard have now been observed by QA to fire,
and the untouched tree observed green, on this tip.

## Half (b) - the overclaim is gone: Expected vs Actual

Expected: the guard's name, comments, and the handoff claim only the pinned textual fact
and state the runtime-composition limit honestly.

Actual: PASS.

- Test renamed `Scripts_AllFiles_ContainNoNonX64BuildOutputPath` ->
  `Scripts_EveryTextualBinSegment_IsLiterallyX64Release` (ScriptBinaryPathTests.cs:110) -
  the name now states exactly the textual fact the scan can pin, not an ANY-path claim.
- Detector renamed `StaleBinPath` -> `NonX64BinSegment` (ScriptBinaryPathTests.cs:53).
- The class doc comment opens with "WHAT THIS GUARD PINS (the exact textual fact, no
  more)" (:18-27) and carries an explicit "Honest limit" paragraph (:36-41): paths
  assembled at runtime from pieces never textually adjacent to `bin` (Join-Path,
  environment variable, config file, process output) are beyond a text scan's reach and
  the guard "makes no claim about those".
- `docs/cencon/proof/issue-9/handoff.md` AC4 section retitled "the reintroduction guard
  (exact claim, revised round 3)" with the same WHAT-IT-PINS / KNOWN-LIMIT split; the
  round-3 section at the top documents the defect, both halves of the fix, and the
  developer's own mutation runs.
- Sweep for residual overclaims: grep of the test file and handoff for the old test name
  and "any non-x64"-style claims finds them only in historical round-1/2 narrative
  (quoted verbatim as history in this report and the handoff's changelog), never in the
  guard's current name, comments, or current-claim text.

## Acceptance criteria still hold on the fixed tip

- AC1/AC2: re-swept on 1b11732 - `grep -rInE "\bbin[\/]+" scripts/` piped through
  `grep -viE "bin[\/]+x64[\/]+Release"` -> zero survivors (exit 1); presence arm: the
  unfiltered grep DID produce bin-segment hits and `bin\x64\Release` literals are present
  in 7 scripts (api-smoke.ps1, gui-smoke.ps1, doc-companion-demo.ps1, py-client-smoke.ps1,
  qa-walk-companion-demo.ps1, run-all.ps1, try.cmd) - the instrument sees paths; the empty
  negative arm is a real absence.
- AC3 (missing-binary error) and AC5 (smoke exercises the fresh build): unchanged - the
  round-3 commit touches no script or product code; round-1 evidence stands.
- AC4: the widened, honestly-scoped guard, verified above with QA's own two-plant drill.
- AC6: 840/840, Failed: 0, run by QA on this tip (gate table above).
- Handoff documents round 3: "Round 3 - fix for the round-2 review-gate REJECT
  (2026-08-21)" section present at the top of handoff.md with the defect, the fix, the
  new counts (834 -> 840, 20 guard tests), and the QA repro recipe. Verified in the diff.

## Method checks (round 3)

- ASCII-only: byte scan of the changed files (test file, handoff.md, this report) found 0
  non-ASCII bytes; the instrument was first shown to FIRE on a known non-ASCII probe
  before its empty result was believed.
- Commit message of 1b11732 checked: no banned attribution strings.
- Scope: test file + handoff doc only; no product code, no privacy-posture surface.
- Working tree left clean; both mutation plants reverted, each revert verified by git
  status and a green filtered re-run.

VERIFIED - both halves of the round-2 gate defect fixed; all acceptance criteria hold on
tip 1b11732. QA does not merge (D7); the Review Gate decides.

---

# QA ROUND 4 - verification of the round-3 review-gate REJECT fix (tip 5b7a184)

QA date: 2026-08-21
Round-4 verdict: **VERIFIED - the round-3 gate defect is fixed on tip 5b7a184.**
Handing to the Review Gate (flow:ready-gate). QA does not merge (D7).

## The defect under test

Review-gate round-3 REJECT, one blocking defect: the guard's failure diagnostic made ONE
claim for BOTH offender categories - "Both launch something other than the binary
`dotnet build AgentEyes.sln -c Release` produces". For a composed path that is an
overclaim: `$platform = 'x64'` upstream composes `bin\$platform\Release` to EXACTLY the
correct binary, yet the old message asserted it launches something else. The fix must
report literal non-x64 paths as provably wrong-binary and composed/variable/fragment
paths as UNVERIFIABLE (the text cannot statically prove `bin\x64\Release`; rejected
fail-closed), with test assertions updated to pin the split.

## Scope of the fix (verified from the diff)

`git diff --stat 4915010..5b7a184`: exactly two files -
`tests/AgentEyes.Tests/ScriptBinaryPathTests.cs` and
`docs/cencon/proof/issue-9/handoff.md` (round-4 note), +98/-10 total. Zero product code,
zero script changes - the round-1 runtime evidence (AC3 missing-binary error, AC5
api-smoke against the fresh build) remains valid and is cited, not re-run; the smoke
surface is byte-identical to what round 1 exercised. Round-4 scope is the diagnostic
wording, its classifier, and its tests - nothing else moved.

## Gate results (run by QA on tip 5b7a184)

| Check | Command | Result |
|-------|---------|--------|
| Build | `dotnet build AgentEyes.sln -c Release` | `Build succeeded.` `0 Error(s)` (same 2 pre-existing xUnit1031 warnings) |
| Tests | `dotnet test AgentEyes.sln -c Release` | `Passed!  - Failed:     0, Passed:   854, Skipped:     0, Total:   854` |

Machine note (worse than rounds 1-3): this box now has NO x64 Microsoft.WindowsDesktop.App
8.x at all (`dotnet --list-runtimes` shows 3.1/5.0/6.0/10.0 only; 8.0.10 exists for
NETCore.App and AspNetCore.App but not WindowsDesktop). QA first reproduced the abort
("Test Run Aborted ... Framework: 'Microsoft.WindowsDesktop.App', version '8.0.0' (x64)"
not found), then installed the real 8.0.30 x64 windowsdesktop + base runtimes user-locally
via dotnet-install.ps1 into %LOCALAPPDATA%\dotnet8-desktop and ran the suite with
DOTNET_ROOT pointed there - so this round's 854/854 ran on an actual .NET 8 desktop
runtime, not a roll-forward. Environment fact, not a property of this change. Suite grew
840 -> 854: exactly the 14 new classifier theories (7 + 7), matching the handoff's claim.

## The split diagnostic - Expected vs Actual (file:line)

Expected 1: a literal non-x64 path is reported as provably launching the wrong binary.
Actual: PASS. `wrongLiterals` offenders render under the heading "Literal non-x64 bin\
path(s) under scripts/ - provably NOT the bin\x64\Release\ output of `dotnet build
AgentEyes.sln -c Release`, so each launches the wrong (stale or never-built) binary."
(ScriptBinaryPathTests.cs:165-172).

Expected 2: a composed/variable/fragment path is reported as UNVERIFIABLE and is NOT
accused of launching the wrong binary. Actual: PASS. `composed` offenders render under the
separate heading "Unverifiable composed bin\ path(s) under scripts/ (variable,
placeholder, or fragment where a literal segment belongs) - a text scan cannot statically
prove such a path is bin\x64\Release (it may even resolve there), so it is rejected
fail-closed." (ScriptBinaryPathTests.cs:174-181). The old single-claim wording ("Both
launch something other than ...") is deleted from the file (verified by grep: zero hits
for "launch something other").

Expected 3: both categories still FAIL the guard - the split changes the claim, not the
verdict. Actual: PASS. `Assert.True(wrongLiterals.Count == 0 && composed.Count == 0, ...)`
(ScriptBinaryPathTests.cs:184) - fail-closed on either list being non-empty.

The classifier `IsComposedOffender` (ScriptBinaryPathTests.cs:73-86): after a
`NonX64BinSegment` match, the first continuation segment is tested against
`LiteralSegment` (`^[A-Za-z0-9_.\-]+$`, :58). Non-literal first segment (`$platform`,
`%PLATFORM%`, `${platform}`, empty at a `"` fragment boundary) -> composed; literal
`x64` followed by a non-literal config segment (`bin\x64\$config`) -> composed; any other
literal (`Release`, `Debug`, `x86`, `arm64`, `AnyCPU`, and `x64\Debug` via the second
segment being literal) -> wrong literal. A non-offender line throws (:75-76) rather than
silently classifying - fail-closed. Committed theory evidence:
`OffenderDiagnostic_LiteralNonX64Path_ClassifiedAsWrongBinary` (7 cases, :222-235) and
`OffenderDiagnostic_ComposedPath_ClassifiedAsUnverifiable` (7 cases, :237-252, first case
literally `$platform = 'x64'` - the resolves-correct scenario pinned as unverifiable,
never as wrong-binary).

## Mutation drill (run by QA - each offender SEEN under its own wording)

Per Section 6c item 3, QA planted BOTH categories at once on tip 5b7a184, appended to
`scripts/run-all.ps1`:

    $qaPlantLiteral = "src\AgentEyes.App\bin\x86\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
    $platform = 'x64'; $qaPlantComposed = "src\AgentEyes.App\bin\$platform\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"

Note plant (ii) sets `$platform = 'x64'` ON THE SAME LINE - this composed path RESOLVES to
the correct binary, the round-3 gate's exact overclaim scenario.

Result - the guard FAILED (Failed: 1), and the message carried TWO headings with each
plant under the correct one, verbatim:

    Literal non-x64 bin\ path(s) under scripts/ - provably NOT the bin\x64\Release\ output of `dotnet build AgentEyes.sln -c Release`, so each launches the wrong (stale or never-built) binary. Use the single literal bin\x64\Release path:
    run-all.ps1:62: $qaPlantLiteral = "src\AgentEyes.App\bin\x86\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
    Unverifiable composed bin\ path(s) under scripts/ (variable, placeholder, or fragment where a literal segment belongs) - a text scan cannot statically prove such a path is bin\x64\Release (it may even resolve there), so it is rejected fail-closed. Use the single literal bin\x64\Release path:
    run-all.ps1:63: $platform = 'x64'; $qaPlantComposed = "src\AgentEyes.App\bin\$platform\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"

Three-arm reading of that output:
- literal plant (run-all.ps1:62) appears ONLY under the wrong-binary heading - PASS;
- composed plant (run-all.ps1:63) appears ONLY under the unverifiable heading, and the
  x64-resolving composed path still FAILS the guard (honest fail-closed) while no longer
  being accused of launching the wrong binary - PASS;
- an empty or one-heading message would have been a broken classifier - not observed.

Reverted (`git checkout -- scripts/run-all.ps1`), `git status --short` clean, full suite
re-run -> `Passed!  - Failed:     0, Passed:   854` on the untouched tree.

## Method checks (round 4)

- ASCII-only: `LC_ALL=C grep -c` for bytes 0x80-0xFF -> 0 for both changed files; the
  instrument was first shown to fire (count 1) on a known non-ASCII probe.
- Commit message of 5b7a184: no banned attribution strings (grep for the banned forms ->
  no hits).
- Scope: test file + handoff doc only; no product code, no privacy-posture surface, no
  script changes.
- Working tree left clean; both mutation plants reverted, the revert verified by
  git status and a green full-suite re-run.
- Handoff documents round 4: "Round 4 - fix for the round-3 review-gate REJECT
  (2026-08-21)" section present at the top of handoff.md with the split wording, the
  counts (840 -> 854), and the developer's own two-plant run. Verified in the diff.

VERIFIED - the round-3 gate defect (diagnostic overclaim on composed paths) is fixed on
tip 5b7a184; the guard's two claims are now each true of the category they describe, and
composed paths remain rejected fail-closed. All prior-round acceptance-criteria evidence
stands (no product or script surface moved). QA does not merge (D7); the Review Gate
decides.
