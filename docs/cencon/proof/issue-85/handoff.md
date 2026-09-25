# Handoff - Issue #85 - CenCon skills and CLAUDE.md point at the retired tracker

- Repository: `thefrederiksen/agenteyes-app`
- Issue #85 - branch `issue-85-tracker-name` (from `main` at `096c642`), PR #91
- Affected area: docs and skills only. No product code, no test code.

## What was implemented

Every reference to the retired tracker in the LIVE method files - `CLAUDE.md`, the five CenCon skills
under `.claude/skills/`, `docs/cencon/DEVELOPMENT_METHOD.md` and `docs/cencon/INDEX.md` - now names
`thefrederiksen/agenteyes-app`. One sentence in `docs/cencon/DEVELOPMENT_METHOD.md` Section 2 ("The
Hard Gate") states that the predecessor repo is retired; it is the only place in the live method
files where the old name still appears:

    The predecessor repo `thefrederiksen/AgentEyes` is retired - no issue is created, moved, or read there.

Files changed (8):

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

Plus this proof directory (`docs/cencon/proof/issue-85/`: `handoff.md`, `grep-before.txt`,
`grep-after.txt`). Nothing under `docs/cencon/proof/issue-33/` or `docs/cencon/review/` differs from
`origin/main` - see "Review fix pass" below.

Nothing else in the method changed (OUT of scope per the issue).

## Review fix pass (2026-09-24)

The first two commits on this branch (`5e4020c`, `622e72d`) also edited ten historical records by one
line each - eight handoff / QA reports under `docs/cencon/proof/issue-33/` and the two carried-over
review headers under `docs/cencon/review/` - so that a grep over all of `docs/cencon` would return
only the retired sentence. An independent read-only review of PR #91 flagged this: those records are
the audit trail, and their text already said the old repo was archived, so the hits were false
positives of the grep, not live pointers. Editing history to make a check pass is not acceptable
practice. This pass:

- **Reverted all ten files** to their exact content on `origin/main`
  (`git checkout origin/main -- docs/cencon/proof/issue-33 docs/cencon/review`;
  `git diff origin/main --stat -- docs/cencon/proof/issue-33 docs/cencon/review` is empty).
- **Adopted this reading of acceptance criterion 1 for this PR:** the criterion applies to the LIVE
  method files - `CLAUDE.md`, `.claude`, `docs/cencon/DEVELOPMENT_METHOD.md`, `docs/cencon/INDEX.md`.
  `docs/cencon/proof/` and `docs/cencon/review/` are history and are excluded. Why: the audit trail
  is not edited to satisfy an instrument, and the old text there already says the repo was archived,
  so it cannot send an agent to the wrong tracker (which is the problem the issue describes).
- **Re-ran the acceptance grep in two forms and recorded the RAW output** (no markers, no
  transcription) in `grep-before.txt` (at `origin/main` = `096c642`) and `grep-after.txt` (at the
  fixed tip), with the exact commands at the top of each file:
  - (a) live method files only: 35 matches before -> **1** after (the retired sentence).
  - (b) the full `CLAUDE.md .claude docs/cencon` set as the issue wrote it: 45 before -> **11** after
    = the retired sentence + the 10 historical records, listed, all byte-identical to `origin/main`.
  - `docs/cencon/proof/issue-85` is excluded BY PATH (`--exclude-dir=issue-85`) from form (b) because
    this proof directory quotes the pattern and the raw matches, so it would otherwise match the
    instrument it records. Nothing in it is a live pointer.
- The reading is recorded on issue #85 in a comment titled "Acceptance criterion 1 - reading adopted"
  so it is not silent precedent; the owner may narrow the criterion's wording or object there.

## Acceptance criteria -> how QA verifies

| # | Criterion | Implemented | How QA verifies |
|---|-----------|-------------|-----------------|
| 1 | `grep -rn "thefrederiksen/AgentEyes\b"` over CLAUDE.md, .claude, docs/cencon returns only the retired sentence | Read as applying to the LIVE method files (see "Review fix pass"). 35 -> 1 on those files. Over the full set: 45 -> 11, the extra 10 being unchanged historical records. | (a) `grep -rn "thefrederiksen/AgentEyes\b" CLAUDE.md .claude docs/cencon/DEVELOPMENT_METHOD.md docs/cencon/INDEX.md` -> exactly one line, `docs/cencon/DEVELOPMENT_METHOD.md:32`, the sentence quoted above. Two or more lines = defect. ZERO lines = the sentence is missing, i.e. a broken instrument, not a pass. (b) `grep -rn --exclude-dir=issue-85 "thefrederiksen/AgentEyes\b" CLAUDE.md .claude docs/cencon` -> 11 lines: the same sentence plus 8 under `proof/issue-33/` and 2 under `review/`; confirm `git diff origin/main --stat -- docs/cencon/proof/issue-33 docs/cencon/review` is empty. Known-bad input that FIRES: `git grep -n "thefrederiksen/AgentEyes\b" 096c642 -- CLAUDE.md .claude docs/cencon/DEVELOPMENT_METHOD.md docs/cencon/INDEX.md | wc -l` -> 35. Both raw outputs are in `grep-before.txt` / `grep-after.txt`. |
| 2 | `gh` commands in the skills target `thefrederiksen/agenteyes-app` consistently | Every runnable `gh` command in the five skills uses `--repo thefrederiksen/agenteyes-app`. `--repo` is the long spelling of `-R` (the same gh flag); the skills already used the long form and it was kept rather than mixing the two spellings. | `grep -rnE "gh (issue|pr|label|api|repo) " .claude/skills | grep -v -- --repo` -> exactly two lines, `qa-agent/skill.md:104` and `:175`, both prose describing what `gh pr merge --delete-branch` does to the remote branch (a note and the 0.2 changelog), not commands to run; no `-R` spelling anywhere (`grep -rnE "gh .* -R " .claude/skills` -> nothing). `grep -rc thefrederiksen/agenteyes-app .claude/skills/*/skill.md` -> developer 7, implementation-loop 9, product 6, qa 11, support 3. |
| 3 | No code change; `dotnet build` clean | `git diff --stat origin/main -- src tests tools` is empty | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)` (21 warnings, pre-existing). `dotnet test AgentEyes.sln -c Release` -> Passed 1984, Failed 0, Skipped 0. |

## Proof committed

- `docs/cencon/proof/issue-85/grep-before.txt` - raw output of both grep forms at `origin/main` (`096c642`): 35 live, 45 full
- `docs/cencon/proof/issue-85/grep-after.txt` - raw output of both grep forms at the fixed tip: 1 live, 11 full (1 + 10 historical)

## Gate run by the Developer Agent

- `dotnet build AgentEyes.sln -c Release` -> Build succeeded. 21 Warning(s), 0 Error(s)
- `dotnet test AgentEyes.sln -c Release` -> Passed! Failed: 0, Passed: 1984, Skipped: 0, Total: 1984
- Re-run after the review fix pass (docs-only revert; nothing moved) - numbers in the PR #91 comment "Review fix pass".
- Heavy smokes: not run - the change has no runtime surface (docs and skill text only).

## CenCon impact

No drift of the component map or the privacy posture. The method text itself is corrected
(tracker name) and otherwise unchanged; skill versions bumped with one changelog line each.

## Follow-ups (observed, out of this issue's scope - NOT changed here; each needs its own issue)

- `docs/signing.md` line 97 still names `thefrederiksen/AgentEyes` as the place to set the Actions
  signing secrets. That is a LIVE instruction (a human would go to the wrong repo), outside the
  `CLAUDE.md` / `.claude` / `docs/cencon` set this issue scoped.
- `CLAUDE.md` still says QA squash-merges to `main` (lines 38 and 217) - superseded by D7 in
  `docs/cencon/DEVELOPMENT_METHOD.md` (the Review Gate authorizes the merge).
- `CLAUDE.md` still says "288 tests in ~2 seconds" (line 69); today `dotnet test` reports 1984 tests
  in about 49 s.

## Notes for QA

- Line endings: the worktree is CRLF (autocrlf), the index stores LF for all touched files.
  `git diff -w --stat origin/main` shows the real change size.
- I believe this is finished.
