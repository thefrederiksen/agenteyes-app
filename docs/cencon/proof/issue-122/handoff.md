# Issue #122 - Developer handoff

`agenteyes record start --help` started a real screen recording, and an unknown flag was silently
ignored. Both are fixed on branch `issue-122-cli-help-and-unknown-flags`.

Everything below was measured on devlinux on 2026-11-14 against the INSTALLED binary
(`bash scripts/linux/install.sh`, so `agenteyes` -> `~/.local/lib/agenteyes/agenteyes`), unless it
says otherwise. The help and refusal run is on the binary of the PR head (installed 11:08). The
criterion-5 recordings are on the install just before it (11:00). The only difference between the
two is a type rename, `CliCommands` -> `CliCommandTable` (see "Windows CI" below), which changes no
behaviour. All seven guard-disabled mutations were re-run after the rename with identical counts. The full command output is committed next to this note:

| File | What it holds |
|------|---------------|
| `real-binary-help-and-refusals.txt` | 33 commands: every `--help`, `-h`, `help`, and every refusal, each with the recordings-dir listing before and after, the exit code and `GET /status` |
| `real-binary-correct-invocations.txt` | criterion 5: real recordings started and stopped, with manifest `Mode`, plus a baseline run of main's binary |
| `cli-log-extract.txt` | the CLI's own log for both runs: every dispatcher decision, with counts, and the presence control |
| `guard-disabled-runs.txt` | the regression tests run with each part of the fix switched off (M1-M7), every failing test by name |
| `check.sh`, `run-help.sh`, `run-correct.sh`, `mutate.sh` | the scripts that produced those files, so QA can re-run them |

## What changed

The cause: `Program.Main` switched on the first word and passed a generic `CliArgs` to each
command. No command was told which flags it accepted, so an unknown flag (`--help` included) was
read and then ignored.

The fix is one table plus one dispatcher, and everything reads from that table:

* `src/AgentEyes.Core/CliCommandTable.cs` (new; class `CliCommandTable`). It lists every command (19 entries, with `record
  start|stop|status` as three). Each entry has its synopsis text, its options (name, value placeholder
  or boolean, one-line description), its maximum number of positional arguments, and its handler.
  **This is the only list of flags any command accepts.**
* `src/AgentEyes.Core/CliDispatcher.cs` (new). It turns a command line into exactly one of three
  outcomes: help printed (exit 0), a refusal (exit 1), or ONE handler run. Help and refusals are
  decided before any command handler runs. The only thing a help or refusal writes is the
  dispatcher's own lines in the CLI log.
* `src/AgentEyes.Core/CliArgs.cs`: a new `Parse(args, booleanFlags)` overload. A boolean flag no
  longer swallows the next word (`record start --audio-only help` used to read `help` as the flag's
  value). A help token (`-h`, `help`) is never read as an option's value. Before, `record start --mic -h`
  tried to record from a microphone named `-h`, and `record start --mic help` from one named `help`.
* `src/AgentEyes.Core/Program.cs`: `Main` runs `StorageMigration.Run()` first, as before, then makes
  one call into the dispatcher. The old switch and the hand-written `PrintUsage` are gone; the
  top-level usage is printed from the table. The migration MUST stay first: `Log` creates the
  AgentEyes data folder, and the qa-record -> AgentEyes move skips itself for good once that folder
  exists. The self-review caught my first version, which ran the migration after the dispatcher
  had logged. `ProgramMain_RunsTheStorageMigrationBeforeTheDispatcher` now pins the order from the IL.
* `src/AgentEyes.Core/Headless/HeadlessCli.cs`: `Record(opts)` (a switch on the verb) is now three
  handlers, `RecordStart`, `RecordStop` and `RecordStatus`, so each verb can declare its own flags.
  Their bodies are unchanged apart from added logging. `HeadlessCli.Usage` moved into the table.
* `src/AgentEyes.Core/AlwaysOn/AlwaysOnCli.cs`: `Usage` moved into the table (`always-on`).

The rules the dispatcher enforces:

1. `--help`, `-h`, or the word `help` ANYWHERE on a command line prints that command's help and
   exits 0. No handler runs. This holds in any position (`record --help start`, `help record start`,
   `--help record start`) and in any case (`RECORD START --HELP`). Help wins over a refusal:
   `record start --nonsense --help` prints help.
2. `agenteyes record --help` (no verb) prints the help for all three record verbs.
3. A flag the command does not declare is refused with
   `[error] unknown option --X for 'agenteyes <cmd>'. See: agenteyes <cmd> --help` and exit 1.
   This also covers `--fps=30`, which was silently ignored before, and a flag that belongs to a
   different command (`record start --region`).
4. A single-dash token other than `-h` (`-x`), where the command takes no further argument, is
   refused the same way. Where the command takes a path or id, a dash-led one is still passed through
   (`agenteyes import -intro.mp4` works as before).
5. A value-taking flag given no value is refused: `--mic needs a value (NAME). See: ...`.
6. More positional arguments than the command takes are refused:
   `unexpected argument 'hepl' for 'agenteyes record start'`. A typo of a flag or verb therefore no
   longer starts a recording.
7. `--name=value` is refused with a pointer to the supported form:
   `unknown option --mic=Yeti (an option's value goes after a space: --mic Yeti)`.

Behaviour of correctly invoked commands is unchanged. The bodies of the handlers were not touched,
except that the record verb switch was split into three methods.

## Subcommands covered (criterion 2)

All 19 entries of the table. That is every command the CLI dispatches, because the table IS the
dispatch now - there is no other switch:

`screens`, `shot`, `audio`, `video`, `package`, `compose`, `always-on`, `housekeep`, `import`,
`translate`, `subtitle`, `selftest`, `serve`, `record start`, `record stop`, `record status`,
`signin`, `signout`, `account`. The `record` group help and the top-level help are covered too.

The test `CommandTable_HasEveryCommandTheCliDocuments` pins this list, so a truncated table cannot
make the per-command theories pass by having nothing to iterate.

## Acceptance criteria

### 1. `agenteyes record start --help` prints usage and records nothing

Implemented by rule 1. Measured (from `real-binary-help-and-refusals.txt`):

```
$ agenteyes record start --help
--- before: 49 entries; newest 3:
    2026-11-14 10:51:13.347457292 /home/soren/Videos/AgentEyes/2026-11-14_105106_audio
    2026-11-14 11:01:30.056540130 /home/soren/Videos/AgentEyes/2026-11-14_110118_video
    2026-11-14 11:01:36.928641518 /home/soren/Videos/AgentEyes/2026-11-14_110130_audio
--- output (stdout+stderr):
    USAGE
      agenteyes record start [--mix | --loopback] [--mic "NAME"] [--screen N] [--camera "NAME"] [--fps N] [--audio-only]

    OPTIONS
      --mix                      record the mic AND system audio (needs --mic)
      --loopback                 record system audio only
      --mic NAME                 microphone name fragment (see 'agenteyes screens')
      --screen N                 monitor number (default 1)
      --camera NAME              webcam name fragment
      --fps N                    screen frame rate
      --audio-only               record audio without the screen
      -h, --help                 print this help and exit; no command runs
--- exit code: 0
--- after:  49 entries; newest 3:
    2026-11-14 10:51:13.347457292 /home/soren/Videos/AgentEyes/2026-11-14_105106_audio
    2026-11-14 11:01:30.056540130 /home/soren/Videos/AgentEyes/2026-11-14_110118_video
    2026-11-14 11:01:36.928641518 /home/soren/Videos/AgentEyes/2026-11-14_110130_audio
--- GET /status: "State": "idle" "Dir": null
```

**QA:** run `docs/cencon/proof/issue-122/run-help.sh` (it sources `check.sh`), or
`agenteyes record start --help; echo $?` with `ls -1 ~/Videos/AgentEyes | wc -l` before and after,
and `curl -s 127.0.0.1:7882/status`.

### 2. The same for every other subcommand

Implemented by rule 1 for every table entry. Measured for all 18 others, plus `record --help`,
`help record start` and `--help`: every one exited **0**, the recordings dir stayed at **49 entries**
with the same newest three, and `/status` said `idle`. The full help text of each one is in
`real-binary-help-and-refusals.txt`. Summary, one line per command, extracted from that file:

```
$ agenteyes record start --help  | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes record start -h      | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes record start help    | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes record stop --help   | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes record status --help | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes screens --help       | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes shot --help          | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes audio --help         | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes video --help         | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes package --help       | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes compose --help       | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes always-on --help     | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes housekeep --help     | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes import --help        | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes translate --help     | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes subtitle --help      | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes selftest --help      | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes serve --help         | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes signin --help        | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes signout --help       | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes account --help       | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes record --help        | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes help record start    | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
$ agenteyes --help               | exit code: 0 | before: 49 entries -> after:  49 entries | "State": "idle"
```

`/status` and the recordings dir only show the recorder's side effects. They say nothing about
`signin --help`, `signout --help`, `serve --help`, `housekeep --help` or `selftest --help`, whose
side effects would be elsewhere (a browser sign-in, a deleted key, a second server, file changes,
a self-test run). For those the evidence is the CLI's own log (`cli-log-extract.txt`). Across the whole run
(11:08:28.958-11:08:35) the dispatcher logged **33** runs: **25** help decisions plus **8** usage
errors, with **0** `Dispatch: running` lines and **0** `[HeadlessCli] Record*` lines. That window
starts at the script's first command. The installer's own two bare `agenteyes account` calls came
0.5 s before it and are outside it; I checked them by pid and arguments. The
same log in the next window shows a `Dispatch: running record start` line for every real start,
which proves the instrument does see a handler run. After `signout --help`,
`~/.local/share/AgentEyes/devthrottle.cred` still has its 2026-10-15 mtime, and `agenteyes account`
still answers `signed in as ...`. The unit tests prove the general statement: no handler is reached
on any help path.

### 3. `-h` behaves identically to `--help`

Measured: `agenteyes record start -h` printed the same text as `--help`, exit 0, 47 -> 49 entries,
`idle` (see the file). The test theory `Run_CommandWithHelp_PrintsItsHelpAndRunsNoHandler` runs
`--help`, `-h` and `help` against every one of the 19 commands (57 cases). `-h` is also tested in
positions that used to break it: `record start --audio-only -h`, `record start --mic -h`, and
`record start --mic help` (measured on the real binary too: exit 0, help printed, 49 -> 49, `idle`).

### 4. An unknown flag is refused

Implemented by rules 3-6. Measured:

```
$ agenteyes record start --nonsense
--- before: 49 entries; newest 3:
    2026-11-14 10:51:13.347457292 /home/soren/Videos/AgentEyes/2026-11-14_105106_audio
    2026-11-14 11:01:30.056540130 /home/soren/Videos/AgentEyes/2026-11-14_110118_video
    2026-11-14 11:01:36.928641518 /home/soren/Videos/AgentEyes/2026-11-14_110130_audio
--- output (stdout+stderr):
    [error] unknown option --nonsense for 'agenteyes record start'. See: agenteyes record start --help
--- exit code: 1
--- after:  49 entries; newest 3:
    2026-11-14 10:51:13.347457292 /home/soren/Videos/AgentEyes/2026-11-14_105106_audio
    2026-11-14 11:01:30.056540130 /home/soren/Videos/AgentEyes/2026-11-14_110118_video
    2026-11-14 11:01:36.928641518 /home/soren/Videos/AgentEyes/2026-11-14_110130_audio
--- GET /status: "State": "idle" "Dir": null
```

The other refusals measured, each exit 1, 47 -> 49 entries, `idle`:

```
[error] unknown option --nonsense for 'agenteyes record start'. See: agenteyes record start --help   (record start --audio-only --nonsense)
[error] unknown option -x for 'agenteyes record start'. See: agenteyes record start --help
[error] unexpected argument 'hepl' for 'agenteyes record start'. See: agenteyes record start --help
[error] --mic needs a value (NAME). See: agenteyes record start --help
[error] unknown option --mic=Yeti (an option's value goes after a space: --mic Yeti) for 'agenteyes record start'. See: agenteyes record start --help
[error] unknown option --nonsense for 'agenteyes video'. See: agenteyes video --help
[error] unknown option --nonsense for 'agenteyes serve'. See: agenteyes serve --help
```

The test theory `Run_CommandWithUnknownFlag_ExitsOneNamingTheFlagAndRunsNoHandler` covers
`<cmd> --nonsense` for all 19 commands.

### 5. Correct invocations are unchanged

Measured (from `real-binary-correct-invocations.txt`; these started REAL recordings on the shared
service, each checked idle first and stopped straight after):

| Command | Output | Exit | Dir count | Manifest `Mode` |
|---------|--------|------|-----------|-----------------|
| `agenteyes record start --screen 1 --mic "Family 17h"` | `[ok] recording -> .../2026-11-14_110118_video  source=mic mode=video` | 0 | 47 -> 48, `/status` `recording` | `"Mode": "video"` |
| `agenteyes record stop` | `[ok] saved .../2026-11-14_110118_video (4.4s)` | 0 | `/status` `idle` | |
| `agenteyes record start --audio-only --mic "Family 17h"` | `[ok] recording -> .../2026-11-14_110130_audio  source=mic mode=audio` | 0 | 48 -> 49, `/status` `recording` | `"Mode": "audio"` |
| `agenteyes record stop` | `[ok] saved .../2026-11-14_110130_audio (4.3s)` | 0 | `/status` `idle` | |
| `agenteyes record status` | `state=idle elapsed=4.3s ...` | 0 | | |

**A spec premise QA must see - I have NOT redefined the criterion.** As written, criterion 5 says
`agenteyes record start --audio-only` (with no source) "still starts" a recording. It does not, and
**it did not before this change either**. The service refuses it:
`[error] the service refused: audio requires source mic, system, or mixed (HTTP 400)`, exit 1, no
recording. I measured the same command through **main's own binary** (df81375, built from a clean
worktree) against the same service. The output was identical, exit 1, 49 -> 49 entries, `idle`
(both runs are in `real-binary-correct-invocations.txt`). The CLI sends the same request body as
before (`HeadlessCli.StartBody` is unchanged and still unit-tested). So "unchanged" holds, but the
criterion's premise that this exact command records is false on main. The audio-only invocation that
names a source records with `Mode: audio` (table row 3). Note the mic is "Family 17h" because that
is this machine's only microphone (`agenteyes screens`). Whether a bare `--audio-only` SHOULD
default to a source is a product question, outside this issue's scope ("Any change to what the
commands DO when correctly invoked" is OUT).

The unit tests `CorrectInvocation_EveryCommand_RunsExactlyThatHandler` (all 19) and
`RecordStart_CorrectInvocations_ReachTheHandlerWithTheirFlags` prove that correct invocations reach
their handler with their flag values. They are also the presence controls for every "nothing ran"
assertion (see criterion 7).

### 6. The usage text is useful, and cannot drift from the parser

`agenteyes record start --help` lists all seven options it accepts (quoted in full under
criterion 1). Where the text comes from:

* The synopsis line and the OPTIONS list are both printed from the `record start` entry of
  `CliCommandTable.All`. The dispatcher refuses any flag not in that same entry. So the help cannot list
  a flag the parser refuses, and the parser cannot accept a flag the help leaves out: there is one
  list, not two.
* The synopsis is hand-written text (it carries `[--mix | --loopback]` grouping that a generated
  line would lose). `Synopsis_EveryCommand_NamesExactlyTheOptionsItAccepts` proves, for every
  command, that the `--flags` named in the synopsis are EXACTLY the declared options.
* `Handlers_ReadExactlyTheFlagsTheirTableEntryDeclares` reads the COMPILED handlers' IL and proves
  that each handler reads exactly the flags its entry declares. A flag that is declared but never read
  would be the silently-ignored-flag defect again. A flag that is read but not declared would be a
  correct invocation now refused. The test's own doc comment states its limits: a non-literal key
  fails the test as UNRESOLVED rather than being skipped; reads inside a lambda's closure are not
  followed (no handler does that today); callees are followed only when they take a `CliArgs`.
  Building the table this way surfaced flags that the old hand-written usage never listed but the
  handlers DO read. They are now in the help: `--gate`, `--fps`, `--crf`, `--out`, `--label` for
  `video`; `--gate`, `--out`, `--label` for `audio`; `--frames`/`--no-frames` for `package`;
  `--keep-video-days` for `housekeep`.

### 7. Regression tests, in all three suites

ONE source file, `tests/Shared/CliDispatcherTests.cs`, is compiled into all three suites by a
`<Compile Include="..\Shared\CliDispatcherTests.cs" Link=...>` in
`tests/AgentEyes.Tests/AgentEyes.Tests.csproj` (Windows),
`tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj` and
`tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj`. Why: these are pure argument-parsing tests
over platform-independent code. A shared file means every platform's build of the CLI passes the
SAME tests, and the copies cannot drift apart. 154 test cases.

How "no recording" is measured, so that it can fail: each test runs the REAL command table with only
the handlers replaced by a spy. The spy records each call synchronously, before
`CliDispatcher.Run` returns, so there is no counter read before a handler has run. "Nothing ran"
means the spy's call list is empty. Each absence has a presence control through the same harness: the
`CorrectInvocation_*` tests show the spy DOES record a call when a handler runs. Each help test also
requires the help TEXT (the synopsis and every option), not merely the absence of a call.

**Guard-disabled runs** (`guard-disabled-runs.txt`, produced by `mutate.sh`). Each part of the fix
was switched off in turn and the Linux suite run. The file holds every failing test by name; the
counts:

| Mutation | What was switched off | Linux suite result |
|----------|-----------------------|--------------------|
| M1 | help detection (`WantsHelp` returns false) | **69 failed**: every `Run_CommandWithHelp_PrintsItsHelpAndRunsNoHandler` case (all 19 commands x `--help`/`-h`/`help` = 57), all 12 `Run_RecordStartHelpInAnyPosition_*` cases except the three that reach help through the top-level path (`help record start`, `--help record start`, `-h record start`), both `Run_RecordGroupHelp_*` cases that go through the group (`record --help`, `record help`), and `Run_RecordStartHelp_ListsEveryOptionRecordStartAccepts` |
| M2 | unknown-flag validation (`Validate(...)` commented out) | **28 failed**: every `Run_CommandWithUnknownFlag_ExitsOneNamingTheFlagAndRunsNoHandler` case (19), and 9 `Run_RecordWithBadArguments_*` cases (--nonsense, --fps=30, --mic=Yeti, -x, --region, hepl, --mic, --screen, record stop --audio-only) |
| M3 | help tokens protected from being read as a value | **2 failed**: `Run_RecordStartHelpInAnyPosition_...(["record", "start", "--mic", "-h"])` and `(["record", "start", "--mic", "help"])` |
| M4 | boolean flags no longer swallow the next word | **2 failed**: `CorrectInvocation_PackageWithDirAndBooleanFlag_KeepsTheDirPositional`, `RecordStart_CorrectInvocations_ReachTheHandlerWithTheirFlags` |
| M5 | `--crf` removed from `video`'s table entry (a real flag missing) | **2 failed**: `Handlers_ReadExactlyTheFlagsTheirTableEntryDeclares`, `Synopsis_EveryCommand_NamesExactlyTheOptionsItAccepts(path: "video")` |
| M6 | an unread `--preset` declared on `record start` | **2 failed**: `Handlers_ReadExactlyTheFlagsTheirTableEntryDeclares`, `Synopsis_EveryCommand_NamesExactlyTheOptionsItAccepts(path: "record start")` |
| M7 | `StorageMigration.Run()` moved after the dispatcher in `Main` | **1 failed**: `ProgramMain_RunsTheStorageMigrationBeforeTheDispatcher` |
| none | fix restored | **Passed: 268, Failed: 0** |

The top-level help tests (`agenteyes --help`, `agenteyes help record start`, ...) do NOT fail under
M1. They go through a separate branch in the dispatcher (the first word is the help token), which
M1 does not touch. That top-level path existed before this issue (`Program.cs` already handled
`agenteyes --help`). It is covered by `Run_TopLevelHelp_PrintsEverySynopsisExitsZeroRunsNothing`
and the three top-level cases of `Run_RecordStartHelpInAnyPosition_*`.

A trap I hit and fixed in the harness: restoring a file with `mv` gave it an older mtime than the
mutated build, so a later "unmutated" run tested a stale binary. `mutate.sh` now `touch`es the
restored file. The committed `guard-disabled-runs.txt` is from the corrected script, ending in the
268/0 clean run.

### 8. Gates green

On devlinux (dotnet SDK 8.0.134):

```
$ dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64
Build succeeded.    0 Error(s)
$ dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
Passed!  - Failed:     0, Passed:   268, Skipped:     0, Total:   268   (main: 114; +154 from this issue)
$ dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac
Build succeeded.    0 Error(s)
```

The one warning is pre-existing (`AlwaysOnEngine.cs(901,47) CS8602`, present on main).

Also run here: the Mac suite (`dotnet test tests/AgentEyes.Tests.Mac -c Release`) on this LINUX host
gave Passed 225, Failed 1. The one failure,
`MacPathsTests.MachineLocalAppData_OnMac_IsTheApplicationSupportFolder`, also fails on unchanged main
on this host (main: Passed 71, Failed 1). It asserts a real Mac's folder layout. All 154 shared
tests passed in that run.

**Not run here:** the Windows suite. `AgentEyes.sln` cannot build on Linux, and this SDK has no
WindowsDesktop targets, so even a compile-only build fails at the import. The PR's CI runs it
(`windows-latest`: build + test `AgentEyes.sln`), as it runs the Linux and macOS suites. Read the CI
result on the PR, not this note, for Windows.

### Windows CI

The first push failed the Windows CI build. `tests/AgentEyes.Tests/SetupCliRestartTests.cs` has a
file-level alias `using CliCommands = AgentEyes.Setup.Cli.Commands;`. My new type, then called
`AgentEyes.CliCommands`, outranked that alias inside `namespace AgentEyes.Tests`, so the test no
longer compiled (`CS0117: 'CliCommands' does not contain a definition for 'RunningAppHandleFactory'`).
I renamed my type to `CliCommandTable` rather than touch that test. I also checked my other new names
(`CliDispatcher`, `CliCommand`, `CliOption`) against every identifier in the Windows suite: none
is used there. Read the Windows result on the PR's CI after this push.

## Notes for QA

* Smoke scope: **CLI only.** No Control API, App or GUI code changed. `agenteyes serve` goes through
  the same dispatcher (no flags), and the installed service started and answers `/health`.
* The recording HUD is capture-excluded. Assert recording state with `GET /status`, never a
  screenshot.
* This machine has ONE recorder service that other seats use. Check `/status` is `idle` before any
  criterion-5 recording, and stop it straight after.
* Recordings created by this developer run, as evidence for criterion 5: on the final binary,
  `~/Videos/AgentEyes/2026-11-14_110118_video` and `~/Videos/AgentEyes/2026-11-14_110130_audio`; on
  the first install of an earlier build of this branch, `2026-11-14_105026_video` and
  `2026-11-14_105106_audio`.
  The accidental recording from the defect, `~/Videos/AgentEyes/2026-11-14_093032_video`, is
  untouched. Per the issue, QA deletes it when this issue passes. I deleted nothing.
* A positional argument that is literally `help`, and an option VALUE that is literally `help` or
  `-h`, now mean help. A recording dir called `help` must be passed as `./help`.
* Unknown-command output is unchanged: `[error] unknown command: X` on stderr, then the usage on
  stdout, exit 1.

## Self-review (`/code-review`) and what I did with it

* **Fixed** - the migration-after-logging regression described under "What changed" (it was real:
  `StorageMigration.Migrate` returns early when the new folder exists, and `Log` creates it).
* **Fixed** - `record start --mic help` read `help` as the mic name. Help tokens are now never values.
* **Fixed** - a dash-led path (`import -intro.mp4`) was refused. It is passed through again.
* **Fixed** - unknown-command output order. The error comes first again, as before.
* **Fixed** - `--x=value` now says the value goes after a space.
* **Tidied** - one help constant instead of two equal ones, a HashSet for the boolean flags, and a
  `parsed <cmd> options=[...] positionals=N` log line so the log shows how arguments were read.
* **Kept, deliberately** - the two-pass parse for `record`. It is what lets a flag stand before the
  verb (`record --mix start --mic X`). One-word commands are now parsed once.
* **Kept, deliberately** - help and refusals still write the dispatcher's own CLI log lines. The
  project's enterprise-logging standard requires them; they are a log, not a recording or any other state change.
* **Not done** - per-call logging in the `CliArgs`/`CliCommand` accessors (`Has`, `Get`,
  `FindOption`). That would be one line per token. The parse is logged once per run instead, matching
  how the existing `CliArgs` methods never logged.

## CenCon impact

No component-map drift. The privacy posture ("visible, controllable") is strengthened: no CLI help or
mistyped flag can start a recording any more. No `docs/cencon` change needed.

I believe this is finished.
