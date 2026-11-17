# Issue #173 - Developer handoff: the owner's identity scrubbed from a public repository

Branch `issue-173-scrub-identity`, from main `02a4622`. Scope is the issue body as CORRECTED by the
lead's comment 6943321310 (84-ish files, not five; `AccountStateTests.cs` brought IN).

## Round 4 - RESCOPED by the owner (Section 7a split, 2026-11-17); read this first

#173 hit the three-strike rule. Every failure was in the guard's WRAP detection, never in the sweep, so
the owner split the issue. **#173 now merges: the sweep, the test fixture, the redacted wrapped
occurrence, and SINGLE-LINE guard detection. Wrap detection is #218.** Everything below this section
is the history of rounds 1 to 3 and still describes the sweep accurately; where it describes the
guard's joined view, that guard now lives on the park branch, not here.

### 1. Where the wrap work is parked, and how #218 picks it up
Branch **`issue-218-wrap-detection`**, commit **`f382eec`** (pushed). It is this branch's `445153e`
plus ONE commit that restores the wrap detector, `scripts/test-check-proof-identity.py`, the full
`tests/evidence-identity-corpus/` (green, red, declared-miss, declared-flag), the CI wrapped controls
and the method paragraph exactly as at `388b111`, with this round's allowances ported in. Corpus 57/57
as declared; green on a tree including main 5e6e727. QA round 3's D2 and D3 are left open on purpose:
they are #218's spec. **Pickup:** after #173 squash-merges, `git checkout -b issue-218-<slug>
origin/main && git cherry-pick f382eec` (steps and conflict rule in
`docs/cencon/proof/issue-218/parked-from-173.md` on that branch).

### 2. The guard: single-line, green on a tree that includes main 5e6e727, red on a planted probe
- `scripts/check-proof-identity.py`: the joined view and the console-wrap discriminator are removed.
  UTF-16 text is still decoded and scanned, line by line. The docstring states the wrapped shape as a
  declared miss owned by #218.
- **What main 5e6e727 actually tripped.** All 22 hits were HOME PATHS, not addresses: two throwaway
  Linux container accounts in #194's evidence - `tester` (3 hits in `issue-194/handoff.md`,
  created by `scripts/linux/release-smoke.sh` with `useradd`) and `qauser` (19 hits in
  `qa/install-transcript.txt` and `qa/qa-evidence.txt`, the #194 QA container user). Both are now allowed home users, with that
  provenance in the docstring.
- **`user@1000.service` and `scratch@example.invalid` were already green** on the round-3 guard
  (the numeric-instance unit rule and the RFC 2606 / 6761 reserved-name rule both existed), so they
  were not among the 22. The owner's point still stands for the GENERAL case, and it was a real gap:
  `getty@tty1.service`, `sshd@vsock.socket`, `backup@daily.timer` - any NAMED instance - read as an
  address. **TLD plausibility rule:** a last label of service, socket, device, mount, automount, swap,
  path, timer, slice or scope is a unit type, not a TLD. I checked each against the IANA root zone
  list (version 2026111700): none is delegated, **except `target`, which IS a delegated brand TLD** -
  so `.target` stays allowed only with a numeric instance, and a named-instance `.target` is a
  declared flag (`declared-flag/flag01`). `.services` (plural) is a real TLD and stays red (`leak24`).
- Reserved names kept: `example.com/.net/.org`, `.example`, `.invalid`, `.test`, `.localhost`.
- Proof, `round4-controls.txt` Part 2: guard on the tree **exit 0**; with a planted single-line
  address **exit 1**, naming the file and line. Each allowance is load-bearing - a copy of the guard
  with it removed goes red: tester/qauser removed -> **22 hits** (exactly main's #194 evidence);
  unit-TLD rule removed -> the named-instance units are red; both unit rules removed ->
  `issue-102/qa-linux/systemctl-status.txt` (the `user@1000.service` line) is red; reserved rule
  removed -> `issue-196/proof.sh` (`scratch@example.invalid`) is red.
- Corpus (this branch): green 27, red 10, declared-miss 19, declared-flag 1, all as declared. The 14
  wrapped leak shapes moved to `declared-miss/wrapped-*` (pinned GREEN: the single-line guard does not
  see them, and the runner fails if one turns red without the docstring changing); the two wrap flags
  moved to `green/was-*`. New: `green/fp22-fp25` (unit names, reserved domains, container accounts),
  `red/leak23-24`, `declared-flag/flag01`.
- CI: the positive control plants single-line shapes only (address, Windows and Linux home paths);
  the negative control plants a pytest decorator, `user@1000.service`, `getty@tty1.service` and a
  `.invalid` address in the real tree and requires green; then the corpus.

### 3. The username this PR introduced: removed from `verify-sweep.py`
`verify-sweep.py` carries no identity now. Home paths are matched by SHAPE (any user segment, the same
sandbox-HOME lookbehind as the guard): a path that changed must become the same prefix + `<user>`; a
segment that names no person (`<...>`, `...`, runner, runneradmin, tester, qauser) turned into `<user>`
is **over-redaction** and fails; a segment that names someone left as it was is **under-redaction**
and fails - so undoing the wrapped redaction is still red without the script holding the name.

### 4. Re-measured - the sweep did not move
Base main **5e6e727** (merged into this branch at `b3e5725`, no rebase, no force-push):

| Measure | Round 3 (base da85516) | Round 4 (base 5e6e727) |
|---|---|---|
| `verify-sweep.py` files / values redacted | 556 / 4730 | **556 / 4730** |
| digit runs compared / JSON parsed both sides | 165617 / 324 | **165617 / 324** |
| distinct addresses / failures / mapping | 2 / 0 / exact | **2 / 0 / exact** |

Controls (`round4-controls.txt` Part 1, value files outside the repo): RED on one digit moved, one
token swapped, a token replaced by a non-convention stand-in, an LF turned into CRLF, over-redaction
(`AppData` -> `<user>`) and under-redaction (the wrapped redaction undone). 19/19 as stated; 18/18
without the value files (the under-redaction run needs the username file and is skipped, saying so).

Whole tree, every blob, QA's own `qa/qa-forms.py` and `remeasure-wrapped.py` (5e6e727 -> `445153e`):
owner address 87 occ / 82 files -> **0**; second address 3 / 1 -> **0**; `/home/<username>` 794 / 400
-> **0**; `/Users/<username>` 330 / 50 -> **0**; Windows `Users\<username>` 3522 / 118 -> **1**
(`tests/AgentEyes.Tests/CameraFailurePathTests.cs`, declared); WRAPPED 1 -> **0**. Every encoded form
(%40, \u0040, &#64;, &#x40;, &commat;, [at], %2540) is 0 at the tip. Output in `round4-measure.txt`.

Gates: Linux 474/3/0 (477) and Mac 433/2/0 (435), SDK 10.0.403, on this branch. No C# changed in this
round. Windows suite (AccountStateTests is in it): CI run on the pushed tip - see the #173 comment.

### Still NOT covered - stated plainly
- **Git history is not rewritten.** The address is in the author metadata of 67 commits on a public
  repository; it is already out. This guard stops the next leak, not the last.
- **`tests/AgentEyes.Tests/CameraFailurePathTests.cs:77` stays** (outside the evidence directories,
  declared since round 1).
- **#212 is out**: machine names and the bare username elsewhere (pipe names, host names, `ps`/`ls`
  owner columns - 224 files). Two such lines appear in this PR's diff only because the sweep
  redacted the home path on lines that also carry an `ls`/`ps` owner column; that column is on main
  already and is #212's.
- **The open evidence PRs #190, #198, #199 and #177 are not covered by this zero.** They branch
  from an older main; whichever merges after #173 meets the guard then.
- **Wrapped leaks are not detected by CI** until #218 lands. The one that existed is redacted.

## QA round 2 bounce (2026-11-17) - the guard joins only console-wrap breaks; read this first

QA (comment 6945003526, `qa/qa-report-round2.html`) passed the sweep and judged the verifier residual
acceptable. The blocking defect was the guard: removing EVERY line break made identity-free files red
(a pytest decorator after `import pytest` read as an address) while six leak shapes stayed green.

**The discriminator: join a break only when it has the structure of a hard console wrap.**
1. The next line starts at column 0 with a visible character. A console continues a wrapped line at
   column 0; an indented or blank next line is a new line (this alone clears the property setter).
2. Whitespace before the break is ignored - console captures pad lines to the width. (B, B2, B3.)
3. Tier 1, MID-WORD: the characters on both sides are word characters (letter, digit, `_`, `-`). A
   person ending a line almost never splits a word; a console does it all the time. Joined at any
   width, so QA's short synthetic leaks are caught.
4. Tier 2, BESIDE PUNCTUATION (a line ending `/` or `\`, a line starting `@` or `/`): joined only when
   the line before is 80+ columns, padding included. Lines that start with `@` or `/` are ordinary in
   code and listings (decorators, mentions, scoped packages, `ls` output) and follow short lines; a
   real console wrap follows a full-width line. (FP1, FP3, FP5, FP6.)
5. CRLF, LF and a lone CR all count as breaks. (D.)
6. `/home/runner` followed by a line that carries on as a path (`bob/Videos`) is reported: a name
   that only begins with an allowed one. (F.)
7. UTF-16 text - any run of 4+ ASCII chars each followed by NUL, anywhere in a file - is decoded and
   scanned the same way; UTF-16BE matches one byte along. (E.)

Why not QA's width-only hint: QA's own leak probes break after SHORT lines (`Cannot remove C:\Us   `),
so a width-only rule would turn B, B3, C, C2, D and F green. Tier 1 keeps them red; the width gate is
used only where the break is beside punctuation, which is exactly where the false positives were.

**What it misses (pinned GREEN in `declared-miss/`):** a wrap beside punctuation on a console narrower
than 80 columns (`C:\Users\` | name, `name` | `@domain`); an indented continuation (PowerShell
Format-List style) split inside the prefix - a split after the prefix is still red as written, because
the partial name is itself a name; `/home/runner` + a name with no path after it; UTF-32 and any other
encoding. **What it flags that is not a leak (pinned RED in `declared-flag/`):** a line of 80+ columns
ending at a home prefix or mid-word, followed by a line that completes a home path or an address.

**The deliverable: `tests/evidence-identity-corpus/`, run by `scripts/test-check-proof-identity.py`.**
Each file is planted alone in a throwaway repository root and the real guard runs on it as CI runs it.
green 21 (QA's FP1-FP6 and A, plus decorators, mentions, `cd /Users/` then `ls`, `dir C:\Users\` CRLF,
path lists, a markdown hard break, UTF-16 identity-free files, QA's literal B2) must exit 0; red 22
(every wrapped and unwrapped shape incl. QA's six misses, a padded 120-column PowerShell wrap, UTF-16LE
wrapped, UTF-16BE with and without BOM, UTF-16 inside a binary) must exit 1 and name the file;
declared-miss 5 and declared-flag 2 pinned both ways. `.gitattributes` keeps the corpus byte for byte.
CI runs it after planting an identity-free pytest file in the real tree and requiring green.

The corpus can fail (`guard-round3.txt` section 2): the round-2 guard fails it on 25 files (11 green
gone red, 11 red gone green); WRAP_WIDTH set to 0 fails 13; no joining fails 16; no UTF-16 scan fails 5.

| QA round-3 ask | Result | Proof |
|---|---|---|
| FP1-FP6 green, FP4 and A still green | all green | `guard-round3.txt` s1, s3 |
| B/B3 red | red | s1 (leak03, leak05), s3 |
| B2 red | **QA's literal cannot be**: it is `/ho<TAB>LF/me/...` - an extra `/`, so no reading of it holds a home path. The intended shape (`/ho<TAB>LF me/...`) is red (leak04); QA's literal is pinned green (fp19). | s3 note |
| D/F/E fixed or declared | all three FIXED, red | s1 (leak08, leak09, leak10), s3 |
| Green FP control in CI | new step "Negative control": plants an identity-free pytest file, requires exit 0, then runs the corpus. Both steps run locally from the YAML: exit 0. | s5, `ci.yml` |
| "exact" only on a passing mapping | a failing run now prints `[FAIL] ... NOT exact - 1 failure(s)` | s7 |
| `qa-r2-probes.py` "as expected" on every line | every line but B2 (above) | s3 |

**Re-measured at 388b111 (main da85516; main has not moved)**, every blob, plain and wrapped: owner 87 -> 0,
second 3 -> 0, `/home/<username>` 794 -> 0, `/Users/<username>` 330 -> 0, Windows home 3522 -> 1 (the declared
`CameraFailurePathTests.cs:77`), WRAPPED 1 -> 0. QA's `qa-forms.py` and `qa-wrapped-r2.py` agree at 388b111.
Whole tree under the new guard: 1690 files, 0 hits; 214017 breaks, of which 93993 joined.

**Gates:** Linux 474/3/0 (477), Mac 433/2/0 (435), SDK 10.0.403. No C# changed. Windows: CI on this tip,
named in the #173 comment.

**Noticed, not changed:** `verify-sweep.py:39` holds the bare username as a constant (present since an
earlier round of this issue). A bare username is outside the three path forms this issue scrubs and
the guard checks - the #212 class. Flagged so QA can decide whether it belongs in this issue.

**Still OUT, unchanged:** git history (the address is in the author metadata of 67 commits);
`CameraFailurePathTests.cs:77`; usernames in pipe names, host names and `ls` owner columns (#212); and
the four open evidence PRs #190 #198 #199 #177, which this zero does not cover.

## QA round 1 bounce (2026-11-17) - what changed, read before the rest

QA (comment 6944138900, `qa/qa-report.html`) found ONE survivor: a Windows home path split across a
PowerShell console wrap in `docs/cencon/proof/issue-156/qa-windows/run-rc-main.txt:53-54` (`C:\Us` /
`ers\<username>\...`). Every line-by-line search - my sweep, `verify-sweep.py`, the guard - missed it.

| Fix | Proof |
|---|---|
| (1) Redacted: line 54's username became `<user>`. | `remeasure-wrapped.txt` |
| (2) **The guard sees wrapped strings.** It scans every file as written AND with line breaks (and the indentation after them) removed; a hit that spans a break is reported `WRAPPED` with file and line. A joined hit whose part before the break was already a complete string is left to the as-written scan (so `user@example.com` followed by a line is not glued into a false hit). | `guard-wrapped-red-green.txt` |
| (2b) **The guard now checks home paths too** (`/home/`, `/Users/`, `<drive>:\Users\` with any number of backslashes): a user segment that is a name, not a placeholder, is red. The real defect was a path, and an address-only guard could not have caught it wrapped or not. Allowed: `<...>` placeholders, `...`, and GitHub's `runner` / `runneradmin`; a `/home/` mid-path (`$SCRATCH/x/home/.config`, a sandbox HOME) is not a home directory. Zero false positives on the 1,683-file tree. | same |
| Wrapped probes fired: the real defect (7ffa6d5's copy): OLD guard exit 0, NEW guard exit 1 naming `run-rc-main.txt:53`. Planted `C:\Us` CRLF `ers\planted-probe\...` (the defect's exact shape), `/home` LF `/planted-probe`, an address split inside its domain with an indented continuation, and `%4` CRLF `0`: each OLD-guard green where run, NEW-guard red, file and line named. False-positive controls green. Removed: exit 0. | same |
| CI positive control extended: every run plants an address, a home path, a wrapped address and a CRLF-wrapped home path, requires red naming file AND line for each, then green with all removed. Run locally from the workflow YAML: all four `[OK]`, final exit 0. | `.github/workflows/ci.yml` |
| (3) **`verify-sweep.py` pairs each old value with its token** (QA: it was green on a valid-token swap). One swapped occurrence: RED with and without the value files. Every occurrence swapped: RED (84 = 81 + 3) with the value files; GREEN without them, and the script prints a WARNING saying exactly that. Its home-path prefix now tolerates a wrap, so it also REQUIRES the wrapped redaction (red on 7ffa6d5's copy, where the round-1 verifier was green). | `sweep-verify.txt` (last section) |
| Rule: `DEVELOPMENT_METHOD.md` 6a - search across line wraps; what the guard now checks and does not. | the diff |

**Re-measured against main `da85516` (merged in at eae905b; no rebase, no force-push) -> this branch at `e28a068`**, every blob,
case-insensitive, single-line AND wrapped: owner address 87 occ / 82 files -> 0; second 3 / 1 -> 0; `/home/<username>`
794 / 400 -> 0; `/Users/<username>` 330 / 50 -> 0; `Users\+<username>` 3,522 / 118 -> 1 / 1 (the declared
`CameraFailurePathTests.cs:77`); WRAPPED 1 (this defect) -> 0. QA's own `qa/qa-wrapped.py`: 1 hit on da85516, 0 on
e28a068. `verify-sweep.py da85516`: 556 files, 4,730 values (4,729 + this one), 0 failures, exact with the value files.

**Still OUT, stated, not fixed:** git history (the address is in author metadata of commits on main); the home path
in `CameraFailurePathTests.cs:77`; the username in pipe names, host names and `ls` owner columns (host names are
#212); and **the four still-open evidence PRs #190 #198 #199 #177 - the zero above does NOT cover them.** Their
heads merged onto e28a068 are green under the NEW guard (`open-evidence-prs.txt` UPDATE 2), and that is all it says.

Gates this round: Linux 474 passed / 3 skipped / 0 failed (477); Mac 433 / 2 / 0 (435); SDK 10.0.403,
`DOTNET_ROLL_FORWARD=LatestMajor`. **The Windows suite is unrun by me (cannot build on a Mac).** No C# changed
this round; `AccountStateTests.cs` (Windows suite only) is covered by CI run 44052259594, job windows
133932729592 on `a0d6e67`: 2524 passed, 16 skipped, 0 failed. This branch's next CI run re-runs it.

## Read this first: what this does NOT do

**Git history is not rewritten.** The address is in past commits of a public repository, so it is
already out, and a force-push across branches several seats have live would buy nothing. This change
stops the NEXT leak. It does not undo the last one. (Commit author metadata is also untouched: main's
own commits already carry the owner's addresses as author email, and that is a git-config matter, not
a file in the tree.)

## Which commit these counts are true of

**Measured against main `da85516`** (current main at handoff; this branch has main merged in, no force-push).
Between `02a4622` and `da85516` main took #191 (`ffa737b`), #192 (`befabfb`) and #202; none of them moved
either count (address files 82 at both, home-path occurrences 4,646 at both), and the verifier re-ran
clean against `da85516` (`sweep-verify.txt`). **Still OPEN and so NOT covered by this zero: #190, #198,
#199, #177.** At their current heads they add no address and no home path of their own and merge cleanly
on top of this branch with the guard green (`open-evidence-prs.txt`); a later push to them is covered by
the CI guard, not by this measurement. #185 was closed unmerged (superseded by #192).

## Measured before and after (main 02a4622 / da85516 -> this branch)

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
- **Does not check** host names or user names outside a home path (stated in the file and in the rule),
  and cannot read text inside an image. (Round 1 said "home paths" here too; since the QA bounce it DOES
  check home paths, as written and wrapped - see the section at the top.)

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
