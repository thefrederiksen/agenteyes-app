# Handoff - Issue #85 - CenCon skills and CLAUDE.md point at the retired tracker

- Repository: `thefrederiksen/agenteyes-app`
- Issue #85 - branch `issue-85-tracker-name` (from `main` at `096c642`)
- Affected area: docs and skills only. No product code, no test code.

## What was implemented

Every reference to the retired tracker in `CLAUDE.md`, `.claude/` and `docs/cencon/` now names
`thefrederiksen/agenteyes-app`. One sentence in `docs/cencon/DEVELOPMENT_METHOD.md` Section 2
("The Hard Gate") states that the predecessor repo is retired; it is the only place the old name
still appears. In this note and in the two grep proof files the old name is written
`thefrederiksen/[RETIRED]AgentEyes`, and the grep pattern is spelled `Agent[E]yes` (the usual grep
self-exclusion idiom - it matches exactly the same text), so that the proof directory does not
match the acceptance grep itself.

Files changed (18):

| File | Change |
|------|--------|
| `CLAUDE.md` | tracker name (1 reference) |
| `docs/cencon/DEVELOPMENT_METHOD.md` | tracker name (2), the retired sentence added after the Hard Gate paragraph, `Last Updated` -> 2026-09-24 |
| `docs/cencon/INDEX.md` | tracker name (1) |
| `.claude/skills/developer-agent/skill.md` | tracker name (6, all `gh ... --repo`); version 0.1 -> 0.2 + one changelog line |
| `.claude/skills/implementation-loop/skill.md` | tracker name (8); DEV spawn prompt checkout `D:\ReposFred\...` -> `C:\ReposFred\agenteyes-app`; version 0.3 -> 0.4 + one changelog line |
| `.claude/skills/product-agent/skill.md` | tracker name (5, incl. the github.com issue URL); version 0.1 -> 0.2 + one changelog line |
| `.claude/skills/qa-agent/skill.md` | tracker name (10); version 0.2 -> 0.3 + one changelog line |
| `.claude/skills/support-agent/skill.md` | tracker name (2); version 0.1 -> 0.2 + one changelog line |
| `docs/cencon/proof/issue-33/` (8 historical handoff / QA records) | the parenthetical "(the old name in the skill files is ARCHIVED)" reworded to "(the predecessor repo then named in the skill files is ARCHIVED)" - one line each, meaning preserved |
| `docs/cencon/review/archived-pr-188-...md`, `archived-pr-189-...md` | first line of the carried-over header: "the archived private repo <old name>" -> "the archived private predecessor repo (retired - see docs/cencon/DEVELOPMENT_METHOD.md Section 2)" |

The 10 historical records had to change because the acceptance criterion sweeps all of
`docs/cencon` and allows exactly one match. Each edit is a single line and keeps the meaning of
the record (those runs already targeted `thefrederiksen/agenteyes-app`).

Nothing else in the method changed (OUT of scope per the issue). Observed but deliberately NOT
touched: `CLAUDE.md` still says "288 tests in ~2 seconds" (today: 1984 tests, 49 s) and still
describes QA as the merger (superseded by D7 in the method) - both are separate work items.

## Acceptance criteria -> how QA verifies

| # | Criterion | Implemented | How QA verifies |
|---|-----------|-------------|-----------------|
| 1 | `grep -rn "thefrederiksen/Agent[E]yes\b"` over CLAUDE.md, .claude, docs/cencon returns only the retired sentence | 45 matches at base `096c642` -> 1 match on the branch (`docs/cencon/DEVELOPMENT_METHOD.md:32`) | Run the grep on the checked-out branch, with the proof directory present. Expected: exactly one line, the sentence quoted below. Two or more lines = defect. ZERO lines = the sentence is missing, i.e. a broken instrument, not a pass. Known-bad input that FIRES: `git grep -n "thefrederiksen/Agent[E]yes\b" 096c642 -- CLAUDE.md .claude docs/cencon | wc -l` -> 45. |
| 2 | `gh` commands in the skills target `thefrederiksen/agenteyes-app` consistently | Every runnable `gh` command in the five skills uses `--repo thefrederiksen/agenteyes-app`. `--repo` is the long spelling of `-R` (the same gh flag); the skills already used the long form and it was kept rather than mixing the two spellings. | `grep -rnE "gh (issue|pr|label|api|repo) " .claude/skills | grep -v -- --repo` -> exactly two lines, `qa-agent/skill.md:104` and `:175`, both prose describing what `gh pr merge --delete-branch` does to the remote branch (a note and the 0.2 changelog), not commands to run; no `-R` spelling anywhere (`grep -rnE "gh .* -R " .claude/skills` -> nothing). `grep -rc thefrederiksen/agenteyes-app .claude/skills/*/skill.md` -> developer 7, implementation-loop 9, product 6, qa 11, support 3. |
| 3 | No code change; `dotnet build` clean | `git diff --stat 096c642..HEAD -- src tests tools` is empty | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)` (21 warnings, pre-existing). `dotnet test AgentEyes.sln -c Release` -> Passed 1984, Failed 0, Skipped 0. |

The retired sentence, verbatim (Section 2 of DEVELOPMENT_METHOD.md, line 32; old name marked here):

    The predecessor repo `thefrederiksen/[RETIRED]AgentEyes` is retired - no issue is created, moved, or read there.

## Proof committed

- `docs/cencon/proof/issue-85/grep-before.txt` - the 45 matches at base `096c642` (reproducible with the `git grep` command in its header)
- `docs/cencon/proof/issue-85/grep-after.txt` - the 1 remaining match on the branch
- Both files write the old name with the `[RETIRED]` marker so the proof directory does not itself add matches to the acceptance grep. Re-run the commands in their headers for the raw output.

## Gate run by the Developer Agent

- `dotnet build AgentEyes.sln -c Release` -> Build succeeded. 21 Warning(s), 0 Error(s)
- `dotnet test AgentEyes.sln -c Release` -> Passed! Failed: 0, Passed: 1984, Skipped: 0, Total: 1984, Duration: 49 s
- Heavy smokes: not run - the change has no runtime surface (docs and skill text only).

## CenCon impact

No drift of the component map or the privacy posture. The method text itself is corrected
(tracker name) and otherwise unchanged; skill versions bumped with one changelog line each.

## Notes for QA

- Line endings: the worktree is CRLF (autocrlf), the index stores LF for all touched files except
  the two archived review files, which are stored as raw CRLF. `git diff -w --stat` shows the real
  change size (one or two lines per file); a plain `git diff` on the two review files may look
  larger on a checkout with different eol settings.
- I believe this is finished.
