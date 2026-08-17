# AgentEyes - CenCon Documentation Index

**Last Updated:** 2026-06-09
**Schema:** CenCon Method v1.0

---

## Overview

AgentEyes is a Windows desktop application (.NET 8 / WPF) - an always-on local recorder. It
runs in the background and records screen and audio into a rolling buffer, so the user never has to
remember to hit record; when something matters they scrub the timeline and **keep** the part they
want. Everything captured is transcribed into a timestamped, searchable log. The privacy posture
is non-negotiable: **visible, controllable**.

This document is the central CenCon reference for the repo: a component map plus the development
method that governs how the repo is changed.

---

## System Components

### Capture engine + CLI (AgentEyes.Core -> agenteyes.exe)

| Component | Purpose |
|-----------|---------|
| Screen capture | Monitor / region capture, fps-controlled video |
| Audio capture + mixing | Mic + system audio, volume, noise gate |
| Screenshots | Single-frame capture |
| Manifests | Per-recording `manifest.json` describing the capture |
| Whisper transcription | In-process speech-to-text (model loaded once at startup) |
| Headless selftest | `agenteyes selftest` - the no-GUI verification path |

CLI verbs: `agenteyes screens`, `agenteyes shot`, `agenteyes audio`, `agenteyes video`, `agenteyes package`, `agenteyes selftest`.

### WPF tray app (AgentEyes.App -> AgentEyesApp.exe)

| Component | Purpose |
|-----------|---------|
| Tray launcher | Background app, run-at-login, recording indicator HUD |
| Presets | OBS-style named presets (monitor/region, video/audio/shot, mic + system mix, gate) |
| Record view | Preset split-dropdown, REC / STOP / CAPTURE |
| Recent recordings | Recent-recordings list + walkthrough builder (transcript + HTML) |
| REST Control API | Loopback API on `127.0.0.1:7882` |

### Installer (AgentEyes.Setup / AgentEyes.Setup.Cli)

The setup wizard users download, plus the `agenteyes-setup` CLI (install / update / uninstall). Installs
per-user (no admin) to `%LOCALAPPDATA%\AgentEyes\app`.

### Tests (AgentEyes.Tests)

xUnit unit tests, run as part of the green gate.

---

## REST Control API (proof surface)

Loopback only, `http://127.0.0.1:7882`. Key routes: `/health`, `/status`, `/devices`,
`/record/start`, `/record/stop`, `/screenshot`. Starting a recording while one is in progress
returns `409`. This API is the most reliable, focus-free way for the Developer and QA agents to
drive and inspect a running app for proof. See `scripts\api-smoke.ps1`.

---

## Verification (user-invoked)

`scripts\run-all.ps1 -Confirm` is the HEAVY, USER-INVOKED full verification - the human runs it when
they decide; agents never run it (they build only), and it refuses without `-Confirm`. It covers:

1. `dotnet build AgentEyes.sln -c Release`
2. xUnit unit tests
3. `agenteyes selftest` (headless)
4. `scripts\api-smoke.ps1` (REST surface, status transitions, 409 conflict)
5. `scripts\gui-smoke.ps1` (UIA: records through each preset, asserts manifests on disk)

---

## Security posture

No `security_profile.yaml` / DT-* rule set exists yet (deferred - see DEVELOPMENT_METHOD.md Section
8 and D6). The governing constraint until then is the README privacy posture:

- **Visible** - an always-on recording indicator; no stealth mode, ever.
- **Controllable** - hard pause, per-app/window exclusions, bounded disk use.

Any change that weakens visible / controllable must be flagged.

---

## CenCon Development Method (how this repo is changed)

[DEVELOPMENT_METHOD.md](DEVELOPMENT_METHOD.md) defines a four-agent process (Product / Developer /
QA / Support). The single hard rule: **no code is written without a clearly-defined GitHub issue
that passed the Definition of Ready.**

State is carried by `flow:*` labels on GitHub issues in `thefrederiksen/AgentEyes`:

| Label | Stage | Owning agent | Skill |
|-------|-------|--------------|-------|
| `flow:ready-dev` | spec ready to implement | Developer Agent | `.claude/skills/developer-agent` |
| `flow:rejected` | spec too weak; bounced back | Product Agent | `.claude/skills/product-agent` |
| `flow:ready-qa` | implemented + proof linked | QA Agent | `.claude/skills/qa-agent` |
| `flow:qa-failed` | defect; bounced back | Developer Agent | `.claude/skills/developer-agent` |
| `flow:done` | verified with proof; closed | - | - |
| `flow:needs-human` | 3-strike escalation | the human | - |

Proof (screenshot + HTML report) is committed to the PR branch under `docs/cencon/proof/issue-<n>/`
and linked repo-relative from the issue; merging the PR to `main` is always a human step. The
Support Agent owns and keeps these CenCon documents current.

---

## Related Documentation

| Document | Purpose |
|----------|---------|
| [DEVELOPMENT_METHOD.md](DEVELOPMENT_METHOD.md) | CenCon Development Method - how AgentEyes is changed (four agents, flow:* label state machine, Definition of Ready/Done) |
| [../../CLAUDE.md](../../CLAUDE.md) | Project instructions + inline coding standards |
| [../../README.md](../../README.md) | Product overview, repo layout, build/run |
| [../vision.md](../vision.md) | The full product vision |
| [proof/README.md](proof/README.md) | How CenCon proof travels on the PR branch |

---

*Generated for CenCon Method v1.0*
