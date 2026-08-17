# AgentEyes - CenCon Development Method

**Schema:** CenCon Method v1.0 (Development Governance)
**Status:** DRAFT v0.1
**Last Updated:** 2026-08-12
**Owner:** Support Agent (maintains this document)
**Adapted from:** cc-director `docs/cencon/DEVELOPMENT_METHOD.md` (same method, same GitHub-Issues tracker, .NET desktop stack)

---

## 1. Purpose

This document governs how this repository is **changed**. It defines a five-seat development
process and one hard rule:

> **No code is written without a clearly-defined work item.**

When you sit down at AgentEyes, you do not start editing files. You start a work item, you make
it clearly defined, and then you let the Developer Agent, the QA Agent and the Review Gate carry it
the rest of the way.
This is not optional guidance - it is the method the repository enforces.

The five seats are single-purpose roles (Product / Developer / QA / Review Gate / Support). They do not talk
directly; they hand work down the line by changing one `flow:*` label on a GitHub issue.

---

## 2. The Hard Gate

Every code change traces back to exactly one **GitHub issue** (in `thefrederiksen/AgentEyes`)
that has passed the **Definition of Ready** (Section 5). There are no exceptions for "small" changes.

Rationale:

- The issue is the durable, auditable handoff artifact between agents.
- A change with no issue has no spec, no acceptance criteria, and no proof target.
- Labels on the issue are the bus: they overlap cleanly with a normal GitHub flow and let any
  agent (or human) pick up where another left off.

The only work that may happen without an issue:

- Drafting/refining an issue (Product Agent's own job).
- Answering questions (Support Agent - read-only, never edits code).

---

## 3. The Five Seats

Each seat is a single-purpose agent session - four of them running this repository's primary agent
tool, the Review Gate deliberately running a DIFFERENT one (Section 3.4). Seats do not talk directly; they hand work
down the line by changing the `flow:*` label on the issue. Between work items, an agent's memory is
cleared so no context bleeds from the last ticket into the next (Section 7).

### 3.1 Product Agent

- **Job:** Own issues. Create them and sharpen them until they meet the Definition of Ready. The
  only way work enters the system.
- **Never:** Writes implementation code.
- **Input:** A raw request, idea, bug report, or an issue bounced back with `flow:rejected`.
- **Output:** An issue labeled `flow:ready-dev` that satisfies the Definition of Ready.
- **Tracker surface:** `gh` CLI; issue kinds map to the existing `bug` / `enhancement` labels
  (plus area labels as needed).

### 3.2 Developer Agent

- **Job:** Implement one ready issue end to end.
- **Reject path:** If the item does not meet the Definition of Ready, the Developer Agent labels
  it `flow:rejected`, writes WHY in a comment, and returns it to Product. It does not "do its best"
  with a weak spec.
- **Definition of Done it must satisfy before handing off:** Section 6.
- **Handoff:** On completion (build clean - no test runs), commits a handoff note to the PR
  branch under `docs/cencon/proof/issue-<n>/` describing what was implemented and how QA should test
  each criterion, and links it repo-relative in an issue comment (Section 6a). The developer does NOT
  run smokes or produce running-app proof - that is QA's job (revised 2026-06-16).
- **Input:** An issue labeled `flow:ready-dev`.
- **Output:** Either `flow:rejected` (back to Product) or `flow:ready-qa` with the handoff note linked.

### 3.3 QA Agent

- **Job:** Loop over `flow:ready-qa` issues, verify each independently with proof, pass or fail.
- **Independence:** Verifies actual behavior in the running app - it does not trust the Developer
  Agent's report. (SOC 2 separation of duties; the QA session is a different identity from the
  developer session.)
- **Fail path:** Labels `flow:qa-failed`, writes WHY, returns to Developer.
- **Pass path:** Commits the QA proof report and re-labels the issue `flow:ready-gate` for the
  Review Gate (Section 3.4). **QA NO LONGER MERGES** (revised 2026-08-12, D7 - this supersedes D5).
  The label change is the handoff here exactly as everywhere else.
- **Never stops on its own** - it takes the next QA item until the queue is empty.
- **Input:** An issue labeled `flow:ready-qa`.
- **Output:** `flow:qa-failed` (back to Developer) or `flow:ready-gate` (on to the Review Gate).

### 3.4 Review Gate (independent, different agent vendor)

- **Job:** Adversarially review one pull request BEFORE it may merge. Find what is still broken.
- **Independence:** a DIFFERENT AGENT VENDOR from the Developer and QA seats, spawned with
  `cc-devthrottle session spawn <repo> --agent <gate-agent>`. The current gate agent is named in
  `.claude/skills/implementation-loop`, not here, so this document stays vendor-neutral. Same-vendor
  review shares the same blind spots; that is the entire point of the seat.
- **Never:** changes product code, commits, pushes, merges, or touches GitHub issues/PRs. Review only.
- **Output:** a verdict file under `docs/cencon/review/` whose first line is exactly one of
  `APPROVE`, `APPROVE-WITH-FOLLOWUPS`, or `REJECT`, plus blocking defects with file:line and a
  concrete failure scenario.
- **On APPROVE / APPROVE-WITH-FOLLOWUPS:** the orchestrator commits the verdict to the PR branch,
  squash-merges, deletes the branch, sets `flow:done`, and files the follow-ups as new issues.
- **On REJECT:** the orchestrator posts the defects on the issue and sets `flow:qa-failed`.
- **Input:** An issue labeled `flow:ready-gate`.
- **Output:** `flow:done` (merged) or `flow:qa-failed` (back to Developer).

### 3.5 Support Agent

- **Job:** The idle seat. Answers questions about the codebase and about what the other four
  seats are doing. Owns and maintains the CenCon documents (this file and `docs/cencon/`).
- **Never:** Touches issues or implementation code. Read-only with respect to product code.
- **Input:** Questions.
- **Output:** Answers, and CenCon doc updates when the architecture/method drifts.

---

## 4. The Label State Machine

State lives as a `flow:*` **label** on the GitHub issue. One agent watches for one trigger label.

```
  (raw request / idea / bug)
            |
            v
      [ Product Agent ]
            |
            |  meets Definition of Ready
            v
      flow:ready-dev ----------------------------+
            |                                     |
            v                                     |
      [ Developer Agent ]                         |
            |                                     |
     +------+-------------------------+           |
     |                                |           |
 weak spec                      implemented       |
     |                          + proof linked     |
     v                                |           |
 flow:rejected --> [ Product Agent ]--+ (re-sharpen, re-label ready-dev)
                                      ^
            +-------------------------+
            |
            v
      flow:ready-qa
            |
            v
      [ QA Agent ]
            |
     +------+----------------+
     |                       |
  defect found          verified (QA does NOT merge)
     |                       |
     v                       v
 flow:qa-failed        flow:ready-gate
     |                       |
     |                       v
     |            [ Review Gate - independent, other vendor ]
     |                       |
     |              +--------+---------+
     |              |                  |
     |          REJECT            APPROVE /
     |              |         APPROVE-WITH-FOLLOWUPS
     |              |                  |
     +<-------------+                  v
     |                            flow:done
     v                       (closed + PR merged to main)
 [ Developer Agent ] (fix, re-label ready-qa)
```

Label vocabulary (single source of truth - these labels exist in the repo):

| Label | Meaning | Owner who sets it | Next agent |
|-------|---------|-------------------|------------|
| `flow:ready-dev` | Spec is ready to implement | Product Agent | Developer Agent |
| `flow:rejected` | Spec too weak; see comment | Developer Agent | Product Agent |
| `flow:ready-qa` | Implemented + proof linked | Developer Agent | QA Agent |
| `flow:ready-gate` | QA verified; awaiting the Review Gate's merge decision | QA Agent | Review Gate |
| `flow:qa-failed` | Defect found by QA OR by the Review Gate; see comment | QA Agent / orchestrator | Developer Agent |
| `flow:done` | Verified AND passed the Review Gate | orchestrator, on a gate APPROVE | (closed) |
| `flow:needs-human` | 3-strike escalation | Product Agent | the human |

Only one `flow:*` label is present at a time. Changing the label IS the handoff:

```bash
gh issue edit <N> --repo thefrederiksen/AgentEyes --add-label flow:ready-qa --remove-label flow:ready-dev
```

DECIDED (D1): the `flow:*` labels are authoritative. GitHub's open/closed state is cosmetic and is
not required to track these states; an issue is only closed when it reaches `flow:done`.

DECIDED (D3): the reject round-trip is fully autonomous. When the Developer Agent labels
`flow:rejected`, the Product Agent re-sharpens and re-submits with no human in the loop. (Guard
against ping-pong: see Section 5a.)

---

## 5. Definition of Ready (Product Agent's bar)

An issue is `flow:ready-dev` only when ALL of the following are true. The Developer Agent rejects
anything that fails this list.

1. **Title** is a single, specific outcome, with an area prefix (see Area Prefixes below).
2. **Problem / value:** one paragraph - what is wrong or wanted, and why it matters.
3. **Scope:** explicitly states what is IN and what is OUT.
4. **Acceptance criteria:** a checklist of observable, testable conditions. Each must be verifiable
   by the QA Agent with a screenshot, a Control API (`127.0.0.1:7882`) response, or a log/command -
   no "should feel faster".
5. **Affected area:** which projects are expected to change - `AgentEyes.Core`,
   `AgentEyes.App`, `AgentEyes.Tests`, `AgentEyes.Setup`, or `AgentEyes.Setup.Cli`.
6. **Proof target:** what the success screenshot/report must show.
7. **No invented design intent:** any assumption about intended behavior is flagged as an
   assumption inside the issue, not stated as fact.

If a request cannot be made to satisfy this list, it is not ready - the Product Agent keeps working
it (or asks the human), it does not pass it down.

### 5a. Ping-pong guard (autonomous reject loop)

Because the reject round-trip is fully autonomous (D3), a spec must not bounce forever between
Product and Developer:

- Each `flow:rejected` -> re-sharpen -> `flow:ready-dev` cycle increments a reject count recorded in
  an issue comment.
- On the **third** rejection of the same issue, the agents stop the autonomous loop, label the
  issue `flow:needs-human`, and leave a comment summarizing the disagreement for the human to
  resolve.
- The Developer Agent's rejection comment must be specific (which DoR item failed and why) so
  Product can act on it rather than re-submitting the same spec.

---

## 6. Definition of Done (Developer Agent's bar)

Before labeling `flow:ready-qa`, the Developer Agent must have ALL of:

1. Code implemented against every acceptance criterion in the issue.
2. The coding standards in `CLAUDE.md` honored before/while writing code (responsive UI, enterprise
   logging, no fallbacks, try-catch only at entry points, tests, UI-thread safety, ASCII-only).
3. Solution builds clean: `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`,
   `0 Error(s)`.
4. **Build clean AND tests green - the dev gate (revised 2026-07-14, the hard rule).**
   `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.`, `0 Error(s)`, AND
   `dotnet test AgentEyes.sln -c Release` -> `Failed: 0`.

   > **THE HUMAN NEVER RUNS TESTS. EVER.** Tests are run by the agent that changed the code, right
   > after it changes the code. The Developer Agent never ends its turn asking the human to run
   > tests, and never hands QA or the human an unrun suite. Doing so is a defect, not caution.

   `dotnet test` is FAST and SILENT - 288 tests in ~2 seconds, no app launch, no audio, no ffmpeg.
   There is no reason to skip it. The heavy smokes (`api-smoke.ps1`, `gui-smoke.ps1`,
   `agenteyes selftest`, `run-all.ps1`) DO launch the app and record audio, so the Developer runs
   them **only when the change actually touches that area** - targeted, not reflexive, and never
   delegated upward. The 2026-06-16 rule was aimed at agents firing a minutes-long audible sweep on
   every trivial issue; the fix is TARGETING that sweep, not pushing it onto the human.
5. **A handoff note committed to the PR branch** (NOT running-app proof): a short note under
   `docs/cencon/proof/issue-<n>/` (e.g. `handoff.md`) listing each acceptance criterion, what was
   implemented for it, and exactly how QA should exercise it (which REST Control API calls / UIA
   controls / steps), plus any area worth a smoke (api / gui) so QA can scope its checks.
   The developer is no longer required to exercise the running app; QA produces the running-app proof
   in Section 6b.

A missing handoff note is itself a Definition-of-Done failure - the issue does not advance.

### 6a. Proof on GitHub (the branch transport)

GitHub's `gh` CLI cannot attach arbitrary files/images to an issue. Therefore the developer's handoff
note and the QA proof travel on the **pull request branch**:

1. The Developer Agent works on a branch and opens a PR. Commits to the **PR branch** are authorized
   by adoption of this method (CLAUDE.md commit policy carves out exactly this exception).
2. The Developer Agent commits its **handoff note** under `docs/cencon/proof/issue-<n>/`
   (e.g. `handoff.md`) - what was implemented and how QA should test each criterion. (The developer no
   longer commits running-app screenshots; QA produces the running-app proof. Revised 2026-06-16.)
3. The Developer Agent links it **repo-relative** in an issue comment, alongside the PR link:

   ```
   Handoff: docs/cencon/proof/issue-123/handoff.md  (PR #124)
   ```

4. **The Review Gate authorizes the merge** (D7, superseding D5). QA passes the issue and STOPS; the
   gate reviews the PR and, on APPROVE or APPROVE-WITH-FOLLOWUPS, the orchestrator squash-merges and
   deletes the branch. Developer branch commits remain authorized; the Developer Agent
   still never merges. **Deploying** the merged change (build-release + `agenteyes-setup install`) is still
   a separate, explicitly-requested step - merging is not deploying.

The QA Agent's proof report follows the same path: committed under `docs/cencon/proof/issue-<n>/`
(e.g. `qa-report.html`) and linked from the `flow:done` (or `flow:qa-failed`) comment.

### 6b. Definition of Verified (QA Agent's bar)

The gate (revised 2026-07-14): the Developer's gate is **build + `dotnet test`** (Section 6 item 4);
the QA Agent verifies by **independent review**, re-runs the tests itself, and runs a heavy smoke when
the change warrants one. Before handing the issue to the Review Gate, the QA Agent must have ALL of:

1. Checked out the PR branch and built it itself (`dotnet build AgentEyes.sln -c Release` clean).
2. **QA verifies by independent REVIEW, and runs the tests itself (revised 2026-07-14, the hard
   rule).** QA reviews the PR diff and surrounding code against every acceptance criterion (an
   independent second pair of eyes - logic errors, missing cases, CLAUDE.md standard violations, CenCon
   drift), checks the handoff note, confirms a clean `dotnet build`, and runs
   `dotnet test AgentEyes.sln -c Release` itself (~2s, silent - never trust that the Developer ran it).

   > **THE HUMAN NEVER RUNS TESTS. EVER.** QA never bounces a runtime check up to the human and never
   > passes an issue with "unrun, human should verify." If a criterion needs the running app, QA drives
   > the app itself via the REST Control API / UIA, or runs the relevant smoke. Asking the human to run
   > something is a defect in QA, not diligence.

   Heavy smokes are TARGETED: QA runs `api-smoke.ps1` / `gui-smoke.ps1` / `run-all.ps1 -Confirm` when the
   change touches that area, and skips them when it plainly does not. QA records, per criterion, Expected
   vs what the code does, PASS/FAIL, and file:line evidence.
3. Every acceptance criterion independently judged from the diff/code (not trusting the Developer's
   report), with file:line evidence - or, where a runtime check was required, QA ran it itself and
   recorded the result.
4. A QA review report (HTML/MD) committed under `docs/cencon/proof/issue-<n>/qa-report.html` and linked.
5. **Every check FAILS CLOSED (added 2026-08-12).** See Section 6c - this is not advisory.

Then the issue goes to the **Review Gate** (Section 3.4), which decides the merge. QA does not merge
(D7, superseding D5).

### 6c. Checks that fail open - the hard rule (added 2026-08-12)

> **A check FAILS OPEN when its pass condition is an ABSENCE, and FAILS CLOSED when its pass condition
> is a specific PRESENCE. Doing nothing at all satisfies an absence.**

This is the single most expensive defect class this repo has produced. It is why a three-day recording
outage survived two "fixes" while hundreds of tests stayed green. Real examples caught here on
2026-08-11/12, all of which PASSED while proving nothing:

- A committed test named `AndNeverLeavesLitter` containing **no temp-file assertion at all**.
- A real-recording probe gating its completion assertions behind `if (transcribed)`, so a recording
  that never transcribed **passed**.
- A kill harness that returned **exit 0 in damaged mode**.
- A rename-ORDER claim inferred from a final log string instead of observing each rename.
- A guard test that **could not fail**: it enumerated 14 FILES rather than 21 CALL SITES, so a new
  writer inside a listed file kept every guard green.
- A QA pass that observed 47/46/58 criterion violations and declared sub-15ms ones harmless -
  **redefining the criterion instead of satisfying it**.

Every agent that writes or accepts a check must therefore:

1. **State all three arms.** Expected result = pass; bad result = defect; **EMPTY result = a broken
   instrument, NEVER a clean run.**
2. **Quote the actual output**, never the word "passed". A load-bearing number must be visible in the
   record and reproducible from the committed artifact - not from instrumentation that was never
   checked in.
3. **Run it against a known-BAD input** and show it FIRES. A check only ever run against the state you
   hope passes has demonstrated nothing. Commit that mutation evidence.
4. **Never redefine a criterion to make it pass.** If observed behavior violates the criterion as
   written, that is a FAIL however small the deviation. Criteria are changed by the human, not by the
   agent verifying against them.
5. **An enumeration is an absence claim wearing a list's clothes** - true only if exhaustive, and it
   cannot prove its own exhaustiveness. Prefer a check over the COMPILED ARTIFACT (IL/assembly
   inspection) to one over source text: source scans are defeated by an alias, a helper, a different
   spelling, or reflection. The manifest-writer guard (#155) failed twice as a text scan and only held
   as an IL inventory. But IL is not a cure: the completion-decision inventory (#156) was ALSO an IL
   inventory and still overclaimed - two compiled decisions that answered from the package journal and
   from an existing flat-transcript fact stayed invisible because neither names a transcript file, and
   all ten guard tests passed. A stronger instrument narrows the blind spot; only an honest statement
   of what it cannot see closes the claim.
6. **An honestly documented limit passes this gate; an overclaim does not.** State what the check
   cannot see, in the check itself.

The full reasoning lives in the fleet skill `checks-that-fail-open`.

---

## 7. Memory Reset Between Work Items

Each agent processes exactly one issue per fresh context. When an item leaves an agent (handed
down or bounced back), that agent's session memory is cleared before it picks up the next item.
This prevents spec, code, or assumptions from one ticket leaking into another.

DECIDED (D2, 2026-06-10): the reset + loop mechanism is the **implementation-loop orchestrator**
(`.claude/skills/implementation-loop`). It drives one `flow:ready-dev` issue to `flow:done` by
spawning the Developer Agent and the QA Agent as **separate sub-agent contexts**, and then the
Review Gate as an independent session of another vendor (D7) whose verdict decides the merge -
the loop NEVER reaches `flow:done` without a gate APPROVE (so the QA context
never sees the developer's working memory - the independence in 3.3 is structural, not just a
promise). The orchestrator reads the resulting `flow:*` label after each agent and routes the next
step; it holds no implementation context of its own.

### 7a. QA-bounce guard (autonomous qa-failed loop)

Mirrors the reject guard (5a) so a defect cannot bounce forever between QA and Developer:

- Each `flow:qa-failed` -> fix -> `flow:ready-qa` cycle increments a bounce count recorded in an
  issue comment. **A Review Gate REJECT increments the same count** (added 2026-08-12): it is a
  defect found before merge, and it is the orchestrator that records it rather than QA.
- On the **third** failed QA pass of the same issue, the orchestrator stops the autonomous loop,
  labels the issue `flow:needs-human`, and comments a summary (the recurring defect + what the
  Developer tried) for the human to resolve.
- The defect comment - QA's, or the orchestrator's quoting the gate verdict - must be specific and
  reproducible so the Developer acts on it rather than guessing.
- When an issue keeps bouncing, suspect the SCOPE before the Developer. Issues #154, #155 and #156
  each needed splitting rather than a further attempt; that split is what unblocked them.

---

## 8. Relationship to the Rest of CenCon (and what is deferred here)

cc-director's CenCon also carries an `architecture_manifest.yaml` (machine-readable C4 model) and a
`security_profile.yaml` (OWASP Desktop DT-* + SOC 2 mappings). **AgentEyes has not stood these
up yet.** Until it does:

- The **affected-area** vocabulary (DoR item 5, DoD item) is the project list in Section 5, sourced
  from `README.md` "Repo layout", not a manifest.
- There is **no blocking security rule set (DT-*) yet**. Security-sensitive changes (this app records
  the screen and audio - the privacy posture in README.md is the governing constraint) must still be
  reviewed against that posture by hand; flag any change that weakens "visible / controllable."
- Standing these two documents up is future CenCon work owned by the Support Agent.

INDEX.md is the human entry point; this file is linked from it.

---

## 9. Open Decisions (to resolve as we build)

| ID | Decision | Status |
|----|----------|--------|
| D1 | Labels vs GitHub open/closed state as authoritative | DECIDED: labels authoritative |
| D2 | Memory-reset + loop mechanism (who restarts agents) | DECIDED (2026-06-10, extended 2026-08-12): the implementation-loop orchestrator (`.claude/skills/implementation-loop`) spawns Developer + QA as separate sub-agent contexts, then the Review Gate as an independent other-vendor session (D7), and routes by `flow:*` label; it never reaches `flow:done` without a gate APPROVE. 3-strike bounce, counting gate rejections, -> `flow:needs-human` (Section 7a) |
| D3 | Reject round-trip: human pause or fully autonomous | DECIDED: fully autonomous, 3-strike human escalation (Section 5a) |
| D4 | Proof transport on GitHub | DECIDED: committed to PR branch under docs/cencon/proof/issue-<n>/, linked repo-relative; branch commits authorized. **Merge authorization SUPERSEDED by D7** - the Review Gate decides, not the human and not QA. |
| D5 | Whether merged-to-main is part of `flow:done` or a separate human step | **SUPERSEDED 2026-08-12 by D7.** Merge remains part of `flow:done` and deploy remains a separate explicitly-requested step, but the authorization is the Review Gate's, not QA's. |
| D6 | Stand up architecture_manifest.yaml + security_profile.yaml (DT-* rules) | OPEN: deferred; affected-area is the README project list until then (Section 8) |
| D7 | Who authorizes a merge | DECIDED (2026-08-12): **an independent Review Gate of a DIFFERENT AGENT VENDOR, not QA.** SUPERSEDES D5. Rationale: on 2026-08-11/12 QA passed six PRs that the gate then rejected, including one that reintroduced the original recording-stranding defect and one that let the microphone keep capturing after the app reported idle. Same-vendor review shares the same blind spots. |

---

*Extends CenCon Method v1.0. Source of truth for how AgentEyes is changed.*
