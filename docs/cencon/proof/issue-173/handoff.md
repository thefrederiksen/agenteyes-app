# Issue #173 - Developer handoff: the owner's identity scrubbed from a public repository

Branch `issue-173-scrub-identity`, from main `02a4622`. Scope is the issue body as CORRECTED by the
lead's comment 6943321310 (84-ish files, not five; `AccountStateTests.cs` brought IN).

## Read this first: what this does NOT do

**Git history is not rewritten.** The address is in past commits of a public repository, so it is
already out, and a force-push across branches several seats have live would buy nothing. This change
stops the NEXT leak. It does not undo the last one. (Commit author metadata is also untouched: main's
own commits already carry the owner's addresses as author email, and that is a git-config matter, not
a file in the tree.)

## Measured before and after (main 02a4622 -> this branch)

Search: `git grep` over the whole tree, the address matched case-insensitively in `@` and `%40` forms,
home paths as `(/home/|/Users/|\\+Users\\+)<username>` (the `\\+` catches the JSON-escaped
`C:\\Users\\...` form, which a fixed-string search for one backslash misses - that is why the
comment's 16 for `C:\Users\` became 110 here).

| | main 02a4622 | this branch |
|---|---|---|
| Files carrying the owner's address | **82** (81 under `docs/` + the test) | **0** |
| Occurrences of the address | 90 (84 in `docs/`, 6 in the test) | 0 |
| ... of which `docs/cencon/proof/issue-113/qa-runs/round2/` | 78 files | 0 |
| A second owner address (in the archived #188 review) | 3 occurrences, 1 file | 0 |
| `/home/<username>` | 794 occurrences, 400 files | 0 |
| `/Users/<username>` | 330 occurrences, 50 files | 0 |
| `C:\Users\<username>` (incl. JSON-escaped) | 3,521 occurrences, 110 files | 0 in `docs/` |
| Files changed by the sweep | | **556**, 4,729 values |

My figures differ from the comment's in two places, both measured: 82 files for the address, not 84
(round2 is 78 files, not 79), and the Windows home path is in 110 files, not 16.

One home path is left on purpose: `tests/AgentEyes.Tests/CameraFailurePathTests.cs:77` (an ffmpeg
output line used as test data). The instruction was to touch no product code or test except
`AccountStateTests.cs`. Flagged for a follow-up.

Also left, because they are outside this issue's three path forms and the address, and counted so
nobody mistakes them for done: the username inside a named-pipe name (474), the host name (95), an
`ls -l` owner column (77), and an encoded project slug (6) - 699 case-insensitive occurrences of the
username in `docs/` in all.

## Criterion by criterion

| # | Criterion | What I did | Proof |
|---|---|---|---|
| 1 | Every instance redacted | 556 files swept with the existing convention: address -> `<owner account>`, the second address -> `<owner second account>`, home-path username -> `<user>`. Before/after example below. | table above; `sweep-verify.txt` |
| 2 | Fresh sweep finds no other | Shape search (any email-shaped string, text and binary) over `docs/cencon/proof` + `docs/cencon/review`: 1,572 files, 19.66 MB, 0 hits. Whole tree: the only email shapes left are reserved-domain test fixtures and a two-letter placeholder address in `Describe_StatesAreDistinguishable` (same test file, untouched). | `guard-red-green.txt` section 2 |
| 3 | The guard fails on purpose | Red on main's real leak (84 hits, each file:line, masked); red on a planted `@` and on a planted `%40` in a new proof file; green when removed; exit 2 when it has nothing to scan. The CI job repeats the planted-probe red on EVERY run before the real check. | `guard-red-green.txt` sections 1, 3, 4 |
| 4 | Guard green on the redacted tree | exit 0 | `guard-red-green.txt` section 2 |
| 5 | The written rule exists | `DEVELOPMENT_METHOD.md` section 6a: scrub identity AND paths; the tokens; "redact the identity, never the evidence"; the length-preserving form (`<stage> (57 chars)`) where a path's length is the evidence; what the guard does not check; history not rewritten. | the diff |
| 6 | Nothing else changed | `verify-sweep.py` splits OLD at identity values and NEW at tokens and requires the pieces between to be byte-identical, plus every digit run, LF/CR count and JSON parse. 556 files, 163,334 digit runs, 324 JSON files, 0 failures. Shown RED when one digit moves and when a token is swapped. | `sweep-verify.txt` |
| lead (1) | Test fixture | `AccountStateTests.cs`: reserved-domain addresses; the signed-in case is a `[Theory]` over two DIFFERENT addresses, each asserted as the value it was given. | `fixture-proof.txt` |
| lead | Open evidence PRs | None would be rejected once this lands first. | `open-evidence-prs.txt` |

Example (`docs/cencon/proof/issue-113/qa-runs/round2/stop/after.json`, one of the 78 poll files):

```
-  "AccountEmail": "<the owner's address>",
+  "AccountEmail": "<owner account>",
-  "LastStopDir": "/home/<username>/Videos/AgentEyes/2026-11-14_133931_video",
+  "LastStopDir": "/home/<user>/Videos/AgentEyes/2026-11-14_133931_video",
```

(Quoted with the value elided: this note must pass its own guard.)

## The guard

`scripts/check-proof-identity.py`, CI job `evidence-identity` in `.github/workflows/ci.yml`.

- **It contains no address.** It matches the SHAPE: a local part, `@` (or `%40`), a dotted domain, an alphabetic TLD.
  Hits print masked (`s***@***.com`) so a public CI log does not republish what it caught.
- **Scope:** every file, text and binary, under `docs/cencon/proof/` AND `docs/cencon/review/`. The
  issue named proof only; review is included because one of this sweep's leaks was there.
- **Two allowances, by shape, with reasons in the file:** RFC 2606 / 6761 reserved domains (the
  stand-ins seats should use), and systemd template-instance units (`<name>@<uid>.service`, which
  appears in Linux `systemctl status` evidence and is a number plus a unit type, not a domain). No
  per-file or per-line exemptions.
- **The #116 `mac-gate.txt` ruling (comment 6923616878):** those four lines carry a per-user macOS
  `TMPDIR` segment, not an address and not a home path. The guard does not fire on them and the sweep
  did not touch them (`git diff 02a4622 -- docs/cencon/proof/issue-116/mac-gate.txt` is empty).
- **It caught this note.** A first draft of this handoff illustrated the shape with literal
  placeholder addresses; the guard went red on three of them (file and line named) before commit.
- **Does not check** home paths, user names or host names (stated in the file and in the rule), and
  cannot read text inside an image.

## Gates

| Gate | Result |
|---|---|
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | Passed 471, Skipped 3, Failed 0 (474) |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | Passed 430, Skipped 2, Failed 0 (432) |
| SDK | 10.0.403, net8.0 tests on runtime 10 via `DOTNET_ROLL_FORWARD=LatestMajor` (no 8.x runtime on this Mac) |
| **`dotnet build AgentEyes.sln` / `dotnet test AgentEyes.sln` (Windows)** | **NOT RUN BY ME - cannot build on a Mac.** |

**QA: the one test file this change edits, `AccountStateTests.cs`, is compiled ONLY by the Windows
suite** - neither Unix suite includes it, so the two green runs above say nothing about it. I ran the
real file against the real `AccountState.Describe` in a scratch net8.0 xunit harness (7/7, and red on
two mutants - `fixture-proof.txt`), but that is not the gate. **Run `dotnet build AgentEyes.sln -c
Release` and `dotnet test AgentEyes.sln -c Release` on Windows** and expect the AccountState tests to
be 7 (was 6).

No heavy smoke applies: no runtime surface changed.

## How QA verifies

1. `python3 docs/cencon/proof/issue-173/verify-sweep.py 02a4622` -> `failures=0`; then change any
   digit in any swept file and see it go red.
2. `python3 scripts/check-proof-identity.py` -> exit 0; plant an address-shaped string in a new file
   under `docs/cencon/proof/` -> exit 1 naming the file; remove it.
3. `git archive 02a4622 docs/cencon | tar -x -C <dir>` and point the guard at `<dir>` -> 84 hits.
4. Re-count the address and home paths on this branch yourself (the search above); the residual
   `CameraFailurePathTests.cs` line is the expected one.
5. `git diff 02a4622 --stat -- src/` is empty; the only file outside `docs/` changed besides the
   guard, the workflow and the rule is `tests/AgentEyes.Tests/AccountStateTests.cs`.
6. The Windows gate, as above.
7. CI on the PR: job `evidence-identity` shows the probe going red, then the real check green.

## CenCon impact

`DEVELOPMENT_METHOD.md` 6a gains the identity rule. No component-map change. Privacy posture: this is
the posture applied to the repository itself; no product behaviour changes.

I believe this is finished, with the Windows gate as the open item for QA.
