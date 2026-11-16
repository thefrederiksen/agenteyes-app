# Issue 143 handoff

## Change

`AgentEyes.Core.Log` now serializes append operations across processes with an operating system file lock. Windows and Linux lock a stable sidecar file with a byte-range lock, while macOS uses an exclusive delete-on-close lock lease. Each record is encoded and written as one complete byte sequence while the lock is held, then flushed before the lock is released. The existing in-process lock, readable log sharing, and log format are unchanged.

The installer appenders were enumerated as part of the issue scope:

- `src/AgentEyes.Core/Log.cs`: shared daily application log; fixed with the new cross-process append path.
- `tools/AgentEyes.Setup.Cli/Program.cs`: shared `setup-cli.log`; fixed through `CrossProcessFileAppender`.
- `tools/AgentEyes.Setup/Services/SetupLog.cs`: timestamped setup log; fixed through `CrossProcessFileAppender`.
- `src/AgentEyes.Core/AlwaysOn/AlwaysOnHistory.cs`: retained history JSONL; fixed through `Log.AppendLine`.
- `src/AgentEyes.Core/AlwaysOn/AlwaysOnEngine.cs`: retained clip ledger; fixed through `Log.AppendLine`.

Replacement writes such as `File.WriteAllText` are not appenders and are outside this issue.

## Acceptance coverage

1. The regression harness starts two real .NET child processes against one file. Each process attempts 250 records with a 16 KiB payload.
2. The test requires exactly 500 complete lines and checks the complete set of expected records, so an interleaved or lost record fails.
3. The child probe preserves the production logging contract by swallowing append exceptions; the parent detects any missing record.
4. The focused test is `TestIsolationTests.Log_ConcurrentProcesses_AppendCompleteLinesWithoutLoss`.
5. The original `File.AppendAllText` implementation was run as a known-bad mutation. Under the same two-process load, the test failed because records were lost during file contention. With the file-lock implementation, all 500 records were present.

## Verification

- Release solution build: passed with the inherited `Version=V7.25.21` environment overridden to numeric `1.0.0` for the SDK.
- Focused cross-process test: passed, 1/1.
- Focused shared-log and Always-On regression set: passed, 141/141.
- IL file-write inventory: passed, 12/12.
- Full Windows suite: 2,294 passed, 4 failed. The remaining failures were the three `PublishedPluginAssetTests` cases that require HTTPS access to GitHub plus the stale IL inventory run started before the final baseline rebuild; the rebuilt IL suite passed separately.
- Linux target build: passed after restoring its missing assets. Running Linux permission tests on this Windows host is not valid: 4 tests fail with Windows ACL/platform-not-supported results and 1 Linux path test is skipped.
- `git diff --check`: passed.

## QA follow-up

Run the normal Windows build and test gate, then exercise the Linux single-instance handoff at least 16 times and record the before/after counts requested by Issue 143. The CI matrix should provide the Linux runtime coverage for the shared appender.
