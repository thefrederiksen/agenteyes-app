---
name: implementation-loop
description: The autonomous Developer<->QA loop driver for the CenCon Development Method in AgentEyes. Takes ONE clearly-defined GitHub issue (flow:ready-dev) and drives it all the way to flow:done by spawning the Developer Agent and the QA Agent as separate sub-agent contexts, looping on flow:qa-failed until QA passes - at which point QA squash-merges the PR to main. Stops early on a spec rejection (flow:rejected) or a 3-strike QA escalation (flow:needs-human). Triggers on "/implementation-loop", "implementation loop", "implement loop", "dev-qa loop", "run the developer qa loop", "implement this issue and loop until QA passes".
---

# Implementation Loop (CenCon Development Method - AgentEyes)

You are the **orchestrator**. You do NOT write product code, you do NOT verify - you carry one
issue down the `flow:*` state machine by handing it to the right single-purpose agent and reading
back the label. The Developer Agent and the QA Agent do the actual work; you route between them.

**Read the contract first:** `docs/cencon/DEVELOPMENT_METHOD.md` (Section 4 state machine, Section 6
Definition of Done, Section 6b Definition of Verified, Section 7/7a the loop + QA-bounce guard).
That document wins on any disagreement.

Tracker: **GitHub Issues** in `thefrederiksen/AgentEyes` (via `gh`). State is the `flow:*` label.

## What you guarantee

Given an issue at `flow:ready-dev`, you end in exactly one terminal state:
- **`flow:done`** - implemented, the full QA sweep passed, proof committed, PR squash-merged to
  `main`, issue closed. (The success path.)
- **`flow:rejected`** - the Developer Agent judged the spec too weak to implement. The Product Agent
  / human owns it now; the loop cannot manufacture missing intent. Stop and report.
- **`flow:needs-human`** - the same issue failed QA three times (Section 7a). Stop and report.

You never merge, never edit code, never set `flow:done` yourself - those happen inside the agent
sub-contexts. You only spawn agents, read labels, count bounces, and (on a 3-strike) escalate.

## Why separate sub-agent contexts

You spawn the Developer Agent and the QA Agent as **distinct sub-agents** (the Agent tool), never in
your own context and never sharing a context with each other. This makes the QA independence in
DEVELOPMENT_METHOD.md Section 3.3 structural: the QA context cannot see the developer's working
memory, assumptions, or "it works on my machine" - it only sees the issue, the PR branch, and the
acceptance criteria. Do not collapse the two roles into one sub-agent to "save a step."

## How you spawn a phase sub-agent (the mechanics - READ THIS)

The CenCon roles are **Skill-tool skills, NOT registered Agent sub-agent types**. There is no
`.claude/agents/` registration for them, so you CANNOT pass `subagent_type: developer-agent` (or
`qa-agent`) to the Agent tool - that errors with "agent type not found". You also do NOT make the
sub-agent invoke the Skill tool (skills are not reliably exposed inside a spawned sub-agent).

Instead, do exactly what cc-director's loop does:

- Spawn with the **Agent tool using the default agent type** (`general-purpose`).
- The **prompt itself** tells the sub-agent to **read the role's `skill.md` as a plain file** (the
  Read tool) and follow it exactly. The skill file IS the role; reading it loads the role.
- Pass NO code and NO file contents - the sub-agent reads the issue and the skill file itself.
- Demand a compact structured `RESULT` block back. You parse ONLY that block; a build log, a
  screenshot, or a file dump must never cross back into your (the orchestrator's) context.

DEV spawn prompt (template):

```
You are the Developer Agent for the AgentEyes repo (D:\ReposFred\AgentEyes).
Read these files and follow them exactly, in order:
  1. CLAUDE.md
  2. docs/cencon/DEVELOPMENT_METHOD.md
  3. .claude/skills/developer-agent/skill.md   <- this skill file IS your role; obey it
Then implement issue #<N> in thefrederiksen/AgentEyes (currently flow:ready-dev or
flow:qa-failed). Do ALL work in your own context. When done, your final message must be ONLY
this block, nothing else:

RESULT
outcome: ready-qa | rejected
issue: <N>
pr: <pr number or none>
branch: <branch name or none>
proof: <repo-relative path to committed proof, or none>
summary: <one line - what you implemented, or which DoR item was missing on a reject>
```

QA spawn prompt is the same shape, pointing at `.claude/skills/qa-agent/skill.md`, with
`outcome: pass | fail`, plus `merged: yes | no` and `defect: <one line>` (the reproducible defect)
on a fail.

You read only the RESULT block to decide the next step and write your one-line ledger entry.

## Inputs

- An issue number whose current label is `flow:ready-dev` (fresh) or `flow:qa-failed` (resume).
  If the user did not name one, pick the oldest `flow:ready-dev`:
  ```bash
  gh issue list --repo thefrederiksen/AgentEyes --label flow:ready-dev --state open \
    --json number,title,updatedAt --jq 'sort_by(.updatedAt) | .[0]'
  ```
  If none, report "no flow:ready-dev issues" and stop.

## The loop

Let `N` = the issue number. Track `bounces` = count of `flow:qa-failed` seen for this issue
(initialize from existing issue comments if resuming).

### Step 0a: Pre-flight (clean tree + correct base) - BEFORE any issue work

The loop must start from a clean, known base so its work never mixes with unrelated uncommitted
changes or branches off the wrong point. Check this FIRST, every run:

```bash
git status --porcelain          # must be EMPTY
git rev-parse --abbrev-ref HEAD # expected: main (the base the Developer branches from)
```

- **Dirty working tree** (any output): STOP. Report the uncommitted files and ask the human to
  commit, stash, or discard. The loop NEVER auto-stashes or discards - that could swallow real work.
- **Not on `main`** (unless the issue/PR says otherwise): STOP and confirm the base with the human.
- Only when the tree is clean and the base is correct does the loop proceed.

### Step 1: Confirm the starting state
```bash
gh issue view N --repo thefrederiksen/AgentEyes --json number,title,labels,comments
```
Proceed only if the label is `flow:ready-dev` or `flow:qa-failed`. Anything else (e.g. already
`flow:ready-qa` or `flow:done`) - report the actual state and stop; do not double-drive it.

### Step 2: Developer pass (sub-agent context)

Spawn a **Developer sub-agent** with the Agent tool using the **default agent type**
(`general-purpose`) and the **DEV spawn prompt** from "How you spawn a phase sub-agent" above -
pointing it at `.claude/skills/developer-agent/skill.md` and issue `N`. Do NOT pass
`subagent_type: developer-agent` (it does not exist). The sub-agent reads `CLAUDE.md`,
`docs/cencon/DEVELOPMENT_METHOD.md`, and the developer-agent skill file, implements on a PR branch
(never `main`), runs the **dev gate** (`dotnet build` clean AND `dotnet test` green; revised
2026-07-14), writes a handoff note (how QA should test each criterion) and commits it to the PR
branch, sets the label to `flow:ready-qa`, and returns its `RESULT` block. (The developer RUNS the
tests - the human never runs tests. Heavy smokes only when the change touches that area.) If the spec is genuinely under-specified it sets
`flow:rejected` with a specific comment instead. You read ONLY the RESULT block.

Then re-read the label to confirm the handback matches reality:
```bash
gh issue view N --repo thefrederiksen/AgentEyes --json labels --jq '[.labels[].name]'
```
- `flow:rejected` (RESULT `outcome: rejected`) -> terminal. Report the rejection reason and stop.
  (Product/human owns it.)
- `flow:ready-qa` (RESULT `outcome: ready-qa`) -> record pr/branch from RESULT, go to Step 3.
- still `flow:ready-dev` / anything else -> the developer pass did not complete; report what the
  sub-agent returned and stop (do not silently retry).

### Step 3: QA pass (separate sub-agent context)

Spawn a **QA sub-agent** with the Agent tool using the **default agent type** (`general-purpose`)
and the **QA spawn prompt** - pointing it at `.claude/skills/qa-agent/skill.md` and issue `N`. This
is a FRESH context that never saw the developer's working memory (that independence is the point; do
NOT reuse the developer sub-agent). Again, do NOT pass `subagent_type: qa-agent`. The sub-agent
checks out the PR branch and verifies independently (it does NOT trust the developer's report),
verifies by **independent REVIEW** of the diff/code against each acceptance criterion, and runs
`dotnet build` + `dotnet test` itself (never trusting that the developer did). If a criterion needs a
runtime check, QA runs it itself - driving the app via the REST API / UIA, or the smoke for that area.
The human never runs tests. Then:
- On PASS: commits the QA proof report, sets `flow:done`, closes the issue, and squash-merges the PR
  to `main` + deletes the branch (the QA pass IS the merge authorization, D5).
- On FAIL: writes a specific reproducible defect, commits the failure proof, sets `flow:qa-failed`.
It returns its `RESULT` block (`outcome: pass | fail`, `merged: yes | no`, `defect: <one line>` on
fail). You read ONLY that block.

Then re-read the label to confirm the handback:
- `flow:done` (RESULT `outcome: pass`, `merged: yes`) -> SUCCESS. Confirm the PR is merged and the
  issue closed, then go to Step 5.
- `flow:qa-failed` (RESULT `outcome: fail`) -> go to Step 4.
- anything else -> the QA pass did not complete; report and stop.

### Step 4: Bounce and loop (or escalate)

`flow:qa-failed` means the Developer must fix it.
- Increment `bounces`.
- If `bounces >= 3` (Section 7a): STOP the autonomous loop. Set `flow:needs-human`, comment a
  summary (the recurring defect across the three QA fails + what the Developer tried each time), and
  report the escalation:
  ```bash
  gh issue edit N --repo thefrederiksen/AgentEyes --add-label flow:needs-human --remove-label flow:qa-failed
  gh issue comment N --repo thefrederiksen/AgentEyes --body "$(cat escalation.md)"
  ```
- Otherwise loop back to **Step 2** - the Developer Agent picks up `flow:qa-failed`, fixes it
  (re-running its build + unit gate + updating the handoff note), and re-labels `flow:ready-qa`. Each Developer/QA pass is a
  fresh sub-agent context.

### Step 5: Report

**Clean-tree gate (mandatory, before you report).** Whatever the outcome, assert the repo was left
clean - no orphaned branch, no uncommitted WIP, no dangling PR. Two checks, because a stale local
branch does NOT show up in `git status` (a working-tree check alone is insufficient):
```bash
git status --porcelain            # (a) working tree MUST be empty
git branch --list "issue-*"       # (b) on a DONE outcome MUST be empty - no orphaned feature branch
```
- (a) empty AND (b) empty: good. On a DONE outcome also confirm the PR is gone (`gh pr list --repo
  thefrederiksen/AgentEyes --state open` must not show it).
- (a) NOT empty: a sub-agent left WIP behind. Do NOT auto-stash or discard (that could swallow real
  work). STOP, report exactly which files are dirty, and ask the human.
- (b) NOT empty on a DONE outcome: the QA sub-agent merged but did not delete its LOCAL branch
  (qa-agent Step 3a step 4 - happens on squash merges because `--delete-branch` only force-deletes
  the remote). This is the gap that left `issue-75-python-client` behind once. Because the issue is
  already merged+closed (DONE), the orphan is safe to force-delete here as a backstop:
  `git checkout main && git branch -D <branch>`. Note it in the report so the QA skill regression is
  visible, do not silently swallow it. (On a non-DONE outcome a parked branch may legitimately remain.)

One concise final report:
```
Issue #N: DONE - merged to main (PR #PR), <X>/<X> criteria verified, <bounces> QA bounce(s) | link
Issue #N: REJECTED - spec not ready: <reason> | link
Issue #N: NEEDS-HUMAN - 3 QA fails on <recurring defect> | link
```

## The two-tier gate (do not blur it)

| Agent | What it runs | Script |
|-------|--------------|--------|
| Developer | build ONLY (no tests, no smokes, no app launch) | `dotnet build` |
| QA | build + `dotnet test`, then verifies by REVIEW of the diff/code; runs a smoke if the area needs it | `dotnet build` + `dotnet test` |

Revised 2026-07-14 (the hard rule): **THE HUMAN NEVER RUNS TESTS. EVER.** Every agent runs
`dotnet build` + `dotnet test` (fast + silent: ~2s, no app launch, no audio) after changing code.
The heavy smokes (`api-smoke.ps1`, `gui-smoke.ps1`, `agenteyes selftest`, `run-all.ps1 -Confirm`) DO
launch the app and record audio, so an agent runs one only when the change actually touches that
area - targeted, not reflexive. No agent ever asks the human to run a check. You (the orchestrator)
run nothing yourself; the sub-agents run their own gates.

## What you do NOT do

- You do not write or edit product code, run the gates yourself, or capture proof - the agents do.
- You do not merge, close, or set `flow:done` - the QA sub-agent does that on a pass.
- You do not invent missing spec - a `flow:rejected` is terminal for the loop (Product/human owns it).
- You do not run Developer and QA in the same context, or skip the QA pass because "the dev proved it."
- You do not loop past 3 QA fails - that is `flow:needs-human`.
- You do not **deploy** (build-release + `agenteyes-setup install`) - that stays a separate, explicitly
  requested step even after `flow:done`.

---

**Skill Version:** 0.3 (DRAFT - the autonomous Dev<->QA loop driver, AgentEyes)
**Implements:** the loop + QA-bounce guard in docs/cencon/DEVELOPMENT_METHOD.md Sections 7/7a (D2)
**Builds on:** the Agent tool (default `general-purpose` sub-agent reading a role skill.md file),
`.claude/skills/developer-agent`, `.claude/skills/qa-agent`
**Created:** 2026-06-10
**Changes in 0.2:** Fixed the spawn mechanics - the CenCon roles are Skill-tool skills, not
registered Agent sub-agent types, so spawn the default `general-purpose` agent and have its prompt
READ `.claude/skills/<role>/skill.md` (do NOT pass `subagent_type: developer-agent`, do NOT invoke
the Skill tool inside the sub-agent). Added the structured RESULT handback block, Step 0a pre-flight
(clean tree + correct base), and the Step 5 clean-tree gate. Ported from cc-director loop v0.3.
**Changes in 0.3:** Step 5 clean-tree gate now ALSO asserts no orphaned local `issue-*` branch on a
DONE outcome (a stale local branch does not appear in `git status`, so the working-tree check alone
missed it - the #75 run left issue-75-python-client behind). Backstops the primary fix in qa-agent
skill v0.2 Step 3a (force-delete the local branch after a squash merge).
