# Issue #83 - 'install --help' runs a real install instead of printing help - Developer handoff

Branch: `issue-83-setup-help` (from `main` at f264ef7, v1.11.4). Tracker: thefrederiksen/agenteyes-app.
Commits: `14734c7` (the fix + tests), `6ea1218` (code-review fixes), then this proof folder.

I believe this is finished. The root cause is the one the issue named, confirmed in the code; the fix
moves the help/usage decision ahead of everything that has a side effect; every acceptance criterion
is covered by a named test that runs the CLI's real entry point in-process against a transport double
that fails the test if touched; each new check was fired at the restored defect and shown to fail; and
the console output the proof target asks for was captured from the binary the final tree builds, after
the full suite had passed on that same binary.

**Read section 5 first.** During verification the developer's own process error ran the CLI with a
real `install` against the default root on the developer machine once (one network call, no binary
replaced, a re-finalize). It is disclosed there in full, with the log lines.

Files in this folder:

| File | What it is |
|------|-----------|
| `handoff.md` | this note |
| `install-help-console.txt` | THE PROOF TARGET: `install --help` and the other help / unknown-option / stray-positional lines run against the freshly built `agenteyes-setup.exe` from commit `6ea1218`, with exit codes, plus the check that the `--root` two lines were given was never created |
| `mutation-evidence.txt` | the new tests fired at the ORIGINAL defect (M1: help only as first argument; M2: unknown option accepted) and at the review-added rule (M3: positionals accepted), each built with the gate's exact command, run, quoted, reverted. Attempts 1 and 4 are recorded as DISCARDED with the reason; attempt 4 is the incident in section 5 |

---

## 1. Root cause (confirmed)

`tools/AgentEyes.Setup.Cli/CliArgs.cs` listed `help` among its known flags, so `install --help` parsed
as command `install` with flag `help`. `tools/AgentEyes.Setup.Cli/Program.cs` then dispatched on the
COMMAND only - `"help" or "--help" => Help()` - so the `help` flag was never read and `install --help`
ran `Commands.UpdateAsync(installMode: true)`: resolve `--manifest latest` from GitHub, download, swap,
finalize. That is the 2026-09-23 report verbatim (`installed=0 updated=4`).

Same class of defect on the other side: the parser had no closed vocabulary. An unknown `--x` was filed
as a flag when nothing followed it (`install --bogus` ran a real install) or swallowed the next token
as its value; and a positional after the command (`install /?`, `install help`) was collected into a
list no command reads - and the command ran. Nothing rejected a typo.

Also: `ResolveLayout` + `WireLogging` ran BEFORE the dispatch, so even the general `help` command
created `%LOCALAPPDATA%\AgentEyes\logs` first. Help now runs before either.

## 2. What changed

### 2.1 Product - `tools/AgentEyes.Setup.Cli`

`Program.cs`
- `Main` is a one-liner over `public static Task<int> RunAsync(string[] argv, TextWriter stdout,
  TextWriter stderr)`, so tests run the REAL entry point and read what it printed. A command that runs
  still writes through Console as before; the help / usage paths write to the given writers.
- Order of business: parse -> usage error (exit 2) -> help (exit 0) -> unknown command (exit 2) ->
  ONLY THEN `ResolveLayout`, `WireLogging`, the parse summary to the log (`CliArgs.ToString()`), dispatch.
  Nothing before the dispatch touches the disk, the engine or the network.
- `help <command>` prints that command's page. `<command> --help` / `-h` / `-H` anywhere prints the same.
- Every exit-2 message has one shape - `usage error: <problem>` then `CliHelp.UsageHint` - whether it
  came from the parser or from a command's own `UsageException` (one `UsageError` helper).
- The `_ =>` arm of the dispatch throws `InvalidOperationException`: an unknown command is rejected
  before the switch, so reaching that arm means `CliHelp.Commands` and the switch disagree - a
  programming error reported as one.

`CliArgs.cs`
- `KnownFlags` (json, dry-run, help, relaunched, desktop-shortcut, no-finalize) and `KnownOptions`
  (manifest, release-dir, component, autostart, root) are the whole vocabulary, public so tests pin the
  help text to them.
- `-h` (any case) sets the help flag. `WantsHelp` is that flag, wherever it appeared.
- Unknown option -> `UsageException("unknown option '--x'.")`. Value option at the end of the line or
  followed by another option -> `"option '--root' requires a value."`. A positional after any command
  but `help` (which takes exactly one) -> `"unexpected argument '/?'. 'install' takes options only."`.
- `ToString()` is the one-line parse summary Program logs once the sink exists. No logging inside
  `Parse`: it runs before the host has decided whether a log file may be opened at all.
- Decision, flagged as an ASSUMPTION (the issue does not say): a malformed line is rejected even when
  it also carries `--help` (`install --help --bogus` -> exit 2, nothing runs). Help does not rescue a
  typo; either way nothing runs. Easy to flip if the owner prefers help to win.

`CliHelp.cs` (new, public)
- `Commands` (the six dispatched commands), `IsCommand`, `General()`, `ForCommand(command)`, `UsageHint`.
  Pure text; building a page touches nothing. Every option line is a shared constant used by the
  general page AND the command pages, so the wording cannot drift. `ForCommand` decides "unknown
  command" in its switch's default arm - the one place.

### 2.2 Tests - `tests/AgentEyes.Tests`

`SetupCliHelpTests.cs` (new, 62 tests). Two instruments, both fail-closed:
- `ForbiddenTransport`, installed at `ReleaseSource.DefaultTransport` - the seam every production
  `new ReleaseSource()` reads (the CLI's `Commands.ResolveReleaseAsync` included). It records each
  request and THROWS, so a command that dispatches ends as exit 1 with a distinctive message, never
  as a quiet pass.
- A `--root` under `%TEMP%` that is never created. The first thing a dispatched command does is
  create `<root>\logs`, so "the root does not exist afterwards" is the fact that nothing ran.
- Both are fired at a known-bad input in
  `Run_PlanWithoutHelp_ReachesTheTransportAndOpensTheLog_SoBothInstrumentsAreLive`: same double, same
  root, `plan` without `--help` -> exit 1, `Requests == [LatestReleaseUrl]`, the double's message on
  stderr, `<root>\logs` exists. `plan` is the safe command for this: it resolves the release FIRST and
  would apply nothing even if it got one. The test detaches the process-wide `EngineLog.Sink` right
  after its run.
- The regression test uses the DEFAULT root (the report had no `--root`) and therefore also pins the
  real `setup-cli.log` unchanged (existence + length before and after): a regression would append to it
  before the transport tripped, and that must show in the test, not only in the transport log.

`AgentEyes.Tests.csproj` references `AgentEyes.Setup.Cli`. The aliases `CliArgs` / `CliHelp` are
declared INSIDE `namespace AgentEyes.Tests` because `AgentEyes.Core` has its own internal
`AgentEyes.CliArgs`, and an enclosing-namespace name outranks a compilation-unit alias.

`UpdateChannelTests.cs`: one attribute, `[Collection(ReleaseTransportSeamCollection.Name)]`. Both
classes swap the static `ReleaseSource.DefaultTransport`; run in parallel, a request could land on the
OTHER class's transport and the class asserting "no request" would pass falsely.

### 2.3 Docs

`docs/installer-spec.md`: the Setup CLI row states `--help` / `-h` anywhere prints that command's help
and runs nothing, and an unknown option is exit 2 and runs nothing.

## 3. Acceptance criteria -> how each is met -> how QA verifies

| Criterion | How the change satisfies it | Test(s) | How QA verifies |
|-----------|-----------------------------|---------|-----------------|
| `install --help`, `update --help`, `uninstall --help`, `plan --help` print command help, exit 0, perform no install/update/uninstall/network call (transport double fails the test if called) | `WantsHelp` answered before layout/logging/engine; `CliHelp.ForCommand` is pure text | `Run_CommandHelp_PrintsThatCommandsPage_ExitsZero_AndTouchesNothing` - 13 cases: all four commands with `--help` AND `-h`, the switch AFTER other options (`install --no-finalize --json --help`, `update --component app -h`, `plan --manifest latest --help`), `help install`, `--help uninstall`. Each asserts exit 0, stdout == the command page, stderr empty, `Requests` empty, root absent. Known-bad arm: `Run_PlanWithoutHelp_..._SoBothInstrumentsAreLive` | `dotnet test --filter FullyQualifiedName~SetupCliHelpTests`; read `install-help-console.txt` (exit 0 on every help line, `C:\nowhere` never created); mutation M1 shows these 11 cases + the regression test FAIL with `Expected: 0 / Actual: 1` when help is again dispatched only as the first argument |
| `install --bogus` exits with the usage exit code and does nothing | `CliArgs.Parse` throws -> `UsageError` prints `usage error: unknown option '--bogus'.` + hint, returns 2 before `ResolveLayout` | `Run_InstallWithAnUnknownOption_ExitsWithTheUsageCode_AndDoesNothing`; `Parse_UnknownOption_ThrowsUsageException_NamingTheOption` (5 lines incl. `-x`, `--json=true`, `--help --bogus`); `Parse_ValueOptionWithoutAValue_ThrowsUsageException` (3); `Run_UnknownCommand_..._BeforeTheInstallRootIsTouched`; and the review-added positional rule: `Parse_PositionalNoCommandTakes_ThrowsUsageException` (6: `/?`, `/help`, `help`, `extra --json`, an en-dash `-help`, `help install extra`), `Run_InstallWithAWindowsStyleHelpToken_ExitsWithTheUsageCode_AndDoesNothing` | Console lines `install --bogus`, `install --help --bogus`, `install /? --root C:\nowhere` -> `[exit 2]`; M2: `Run_InstallWithAnUnknownOption` FAILS `Expected: 2 / Actual: 1` (the install dispatched and hit the transport), 4 of 5 `Parse_UnknownOption` cases FAIL `No exception was thrown`; M3: 5 `Parse_PositionalNoCommandTakes` cases + `Run_InstallWithAWindowsStyleHelpToken` FAIL the same way |
| Regression test for this exact report | `InstallHelp_Issue83_PrintsHelpInsteadOfDownloadingTheLatestReleaseAndReplacingTheInstalledApp` - argv exactly `install --help`, through `Program.RunAsync`, DEFAULT install root; asserts exit 0, the install page, stderr empty, `Requests` empty, the real `setup-cli.log` unchanged | same | In M1 it fails `Expected: 0 / Actual: 1` |
| `dotnet build` clean, `dotnet test` green | - | - | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.` `0 Error(s)`; `dotnet test AgentEyes.sln -c Release` -> `Passed! - Failed: 0, Passed: 1979` (1917 on main + 62) |

Unit tests for the new public surface (coding standard 5): `IsCommand_KnowsExactlyTheDispatchedCommands`
(6), `ForCommand_EveryCommand_ReturnsAPageHeadedByItsUsageLine`, `ForCommand_IsCaseInsensitive`,
`ForCommand_UnknownCommand_ThrowsUsageException`, `General_ListsEveryCommandAndEveryOptionTheParserAccepts`,
`HelpPages_DocumentOnlyOptionsTheParserAccepts` (the two-way text/parser pin),
`Parse_HelpSwitchAnywhereOnTheLine_SetsWantsHelp` (11, incl. `-H`), `Parse_WithoutTheSwitch_DoesNotWantHelp`,
`Parse_ToString_SummarizesTheLineForTheLog`, `Run_NoArguments_PrintsTheGeneralPage`,
`Run_HelpCommand_PrintsTheGeneralPage`, `Run_HelpForAnUnknownCommand_ExitsWithTheUsageCode`.

Real callers are pinned so the closed vocabulary cannot break them: `Parse_TheReleaseWorkflowInvocation_StillParses`
(the exact `.github/workflows/release.yml` line: `install --release-dir dist/release --root <dir> --no-finalize --json`)
and `Parse_TheRelaunchedReExecShape_StillParses` (`update|uninstall --relaunched`, the CLI's own temp-copy re-exec).
The wizard and the in-app updater call the engine directly, not this CLI.

## 4. The evidence, quoted

Gate on the final tree (`6ea1218`):

```
Build succeeded.    0 Error(s)
Passed!  - Failed:     0, Passed:  1979, Skipped:     0, Total:  1979
```

`install-help-console.txt` - the binary is `tools/AgentEyes.Setup.Cli/bin/Release/net8.0-windows/agenteyes-setup.exe`
built from `6ea1218` (the CLI project sets no `<Platforms>`, so unlike the app it lands in `bin\Release`,
not `bin\x64\Release`), run only AFTER the full suite had passed on that same build. Ten lines, exit codes
in order: `install --help` 0, `install -h` 0, `update --help` 0, `uninstall -h` 0,
`plan --root C:\nowhere --json --help` 0, `install --bogus` 2, `install --help --bogus` 2,
`install /? --root C:\nowhere` 2, `help install` 0, `--help` 0; then `ls C:\nowhere` -> does not exist.

`mutation-evidence.txt` - five attempts, all recorded:
- Attempt 1, DISCARDED: a project-level build wrote `bin\Release\...\agenteyes-setup.dll` while the
  solution-level test read `bin\x64\Release\...` (unmutated), so 53/53 passed on a mutated tree - the
  stale-output trap the project instructions record. A discarded false pass is part of the record.
- Attempt 2 (M1 - help only as the first argument; solution build): `Failed: 12` - the regression test +
  11 `Run_CommandHelp` cases, all `Expected: 0 / Actual: 1`. The 2 green cases are `help install` and
  `--help uninstall`, whose `command == "help"` path M1 leaves intact. M2 did not apply in this attempt
  (CRLF after a checkout vs an LF pattern) - stated in the file, hence attempt 3.
- Attempt 3 (M2 - unknown option accepted): 5 failures - `Run_InstallWithAnUnknownOption`
  `Expected: 2 / Actual: 1`, 4 of 5 `Parse_UnknownOption` cases `No exception was thrown`.
- Attempt 4, DISCARDED - the incident, section 5.
- Attempt 5 (M3 - positional rule disabled, on the COMMITTED reviewed tree, one line by a unique anchor):
  6 failures - 5 `Parse_PositionalNoCommandTakes` cases `No exception was thrown` (the 6th,
  `help install extra`, still throws because `help` keeps its one positional) and
  `Run_InstallWithAWindowsStyleHelpToken` `Expected: 2 / Actual: 1`.
Each valid attempt ends with `git checkout --` of the mutated file and a `git status` showing only the
untracked proof folder; the gate above was run on the restored tree afterwards.

## 5. INCIDENT during verification - disclosed in full

While producing mutation M3 the developer made two process errors, and the second had a real effect on
the developer machine (the owner's laptop, where this session runs):

1. The pattern meant for the positional-rule line also matched the `for` loop start above it (both ended
   in `? 1 : 0;`), so the mutation was a double one. Discarded.
2. The "restore" was `git checkout -- CliArgs.cs` while the code-review fixes to that file were still
   UNCOMMITTED. It restored the file to `14734c7` - the pre-review parser, in which `/?` is a positional
   nobody reads - not to the reviewed tree. The gate that followed built a MIXED binary (new `Program`
   and `CliHelp`, old `CliArgs`) and failed 9 tests, which was the mixed tree showing; the console capture
   then ran against that binary, and its line `agenteyes-setup install /?` DISPATCHED A REAL INSTALL
   against the default root. From `%LOCALAPPDATA%\AgentEyes\logs\setup-cli.log`, 2026-09-24:

   ```
   19:48:45 [Program] RunAsync: AgentEyes.Setup.Cli.CliArgs root=C:\Users\...\AppData\Local\AgentEyes
   19:48:46 [ReleaseSource] FetchLatestAsync: channel=https://api.github.com/repos/thefrederiksen/agenteyes-app/releases/latest
   19:48:47 [UpdatePlanner] Plan: 4 components, 0 install, 0 update, 0 missing-asset.
   19:48:47 [InstallFinalizer] DOTNET_BUNDLE_EXTRACT_BASE_DIR already set: ...\AgentEyes\bundle
   19:48:47 [InstallFinalizer] created shortcut: ...\Start Menu\Programs\AgentEyes.lnk
   19:48:47 [InstallFinalizer] registered Add/Remove Programs entry (v1.11.4)
   19:48:47 [Program] RunAsync: command=install exit=0
   ```

   Effect: ONE network call to the release channel (the `releases/latest` query and the manifest); NO
   binary replaced - the plan was 0 install / 0 update and every file under `app\` still carries the
   owner's own 16:59 timestamps from the v1.11.4 update; the Start Menu shortcut re-created and the
   identical v1.11.4 Add/Remove Programs entry re-registered (install mode always finalizes; PATH and the
   bundle dir were already set and unchanged); seven lines appended to the real `setup-cli.log`. The
   owner's brief forbade any network call and any real command, and this violated both - by the
   developer's process, not by the code under review: the reviewed parser rejects `/?` (exit 2) and
   `Run_InstallWithAWindowsStyleHelpToken_ExitsWithTheUsageCode_AndDoesNothing` pins it.

   Nothing needs undoing on the machine: no file, PATH entry or registry value differs from before,
   except the rewritten-identical shortcut and ARP entry and the seven log lines. The owner should still
   know it happened.

   Lessons applied for the rest of this handoff: mutate only after the tree is committed, so a checkout
   restores what was meant; target one line by a unique anchor; run the console capture only after the
   full suite has passed on the SAME binary (attempt 5 and the final capture follow all three).

## 6. Code review of this diff (the skill's self-review step) - nine findings, all acted on

`/code-review` on the branch at effort medium, after `14734c7`:

| # | Finding | Done |
|---|---------|------|
| 1 | Stray positionals silently accepted, so `install /?`, `install help`, an en-dash `-help` still ran a real install | Rejected as `unexpected argument` for every command but `help` (which takes one). 6 parser cases + 1 entry-point test + M3 |
| 2 | `Assert.DoesNotContain("installed=", _out)` could never fail (Commands write to Console, not the injected writer) | Dropped - it claimed coverage it lacked |
| 3 | Regression test on the default root: a regression would append to the real `setup-cli.log` before the transport tripped | The real log is pinned unchanged (existence + length) around the run; argv stays verbatim |
| 4 | `EngineLog.Write` in `CliArgs.Parse` / `CliHelp` fired before the sink existed - discarded in production | Removed; `Program` logs `CliArgs.ToString()` after `WireLogging`. `Parse_ToString_SummarizesTheLineForTheLog` |
| 5 | Two sources of truth for the command set with two unreachable `_ => throw` arms | `ForCommand`'s upfront `IsCommand` check removed; its default arm is the one decision. `Program`'s `_ =>` arm stays as the programming-error guard between `CliHelp.Commands` and the switch (a single command table is a larger refactor; noted) |
| 6 | Option constants used on one page each while other pages inlined different wording | Every page uses the shared constants (`OptAutostart` / `OptDesktopShortcut` say "- install only"; `OptDryRun` reads "Show what would change; download, apply and remove nothing") |
| 7 | `Run_PlanWithoutHelp` repoints the process-wide `EngineLog.Sink` under a temp root that Dispose deletes while other classes log in parallel | The sink is detached immediately after the run (Dispose still nulls it too). No test in the suite installs its own sink (grep), stated in the class comment |
| 8 | `-h` case-sensitive while `--HELP` was not | `-h` compared `OrdinalIgnoreCase`; `install -H` added to the theory |
| 9 | A `UsageException` thrown during command execution printed no hint | One `UsageError` helper for both paths |

## 7. What this does NOT prove (stated limits)

- The tests run the compiled CLI assembly inside the test host, not the single-file publish;
  `build-release.ps1`'s bundle layer is out of frame. The dispatch code is the same assembly in both.
- "No install" is shown through its two mandatory prerequisites - the release fetch (transport double)
  and the log dir (never-created root) - not by a hook inside `UpdateRunner` / `InstallFinalizer`, which
  have no seam. With `--release-dir` the release is a directory read instead of a transport request; the
  root instrument still covers it, because the layout is resolved before any command.
- Per-command option validity is not enforced (`status --autostart on` parses; `status` ignores it).
  Same as before this change; the closed vocabulary is global, not per command. Follow-up if wanted.
- `/?` is rejected, not answered with help. Mapping `/?` and `/help` to help is a one-line follow-up if
  the owner wants the Windows habit honoured.

## 8. CenCon impact

No drift: no component-map change, no privacy-posture change. The setup CLI's surface gains help pages
and a closed vocabulary; the engine, the wizard and the app are untouched.

## 9. For QA - scope of checks

- No REST / UIA / running-app surface changed. `dotnet build` + `dotnet test` is the whole gate; no
  heavy smoke is warranted. QA may re-run the lines in `install-help-console.txt` against the freshly
  built exe - ONLY after `dotnet test` has passed on that same build (section 5 is what happens
  otherwise), and every line must carry `--help` / `-h`, an unknown option, or a `--root` under `%TEMP%`.
  Do NOT run the CLI with a real command against the default root.
- Reminders carried per the method: the focus-free layers are REST / UIA / PrintWindow; never
  force-foreground and synthesize input without warning the human; the recording HUD is capture-excluded.
- The developer did NOT launch AgentEyesApp.exe or agenteyes.exe, run any smoke, selftest, release or
  tag. The one real `install` dispatch and its single network call are the incident in section 5.
