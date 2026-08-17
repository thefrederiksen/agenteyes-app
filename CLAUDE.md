# AgentEyes - Project Instructions

AgentEyes is an always-on local recorder (.NET 8 / WPF tray app + CLI). It is
**enterprise-level software**: robust error handling, comprehensive logging, thorough
testing, and a responsive UI are required, not optional.

The product and privacy stance live in [README.md](README.md) and [docs/vision.md](docs/vision.md).
The non-negotiable posture: **visible, controllable** - an always-on recording indicator
(no stealth mode) and hard user control (pause, per-app/window exclusions, bounded disk use).

---

## How this repo is CHANGED: the CenCon Development Method

This repository is governed by the **CenCon Development Method**. Read the contract:
[docs/cencon/DEVELOPMENT_METHOD.md](docs/cencon/DEVELOPMENT_METHOD.md). One hard rule:

> **No code is written without a clearly-defined GitHub issue that passed the Definition of Ready.**

Four single-purpose agents carry work down a `flow:*` label state machine on GitHub issues
(`thefrederiksen/AgentEyes`):

| Label | Stage | Owning agent | Skill |
|-------|-------|--------------|-------|
| `flow:ready-dev` | spec ready to implement | Developer Agent | `.claude/skills/developer-agent` |
| `flow:rejected` | spec too weak; bounced back | Product Agent | `.claude/skills/product-agent` |
| `flow:ready-qa` | implemented + proof linked | QA Agent | `.claude/skills/qa-agent` |
| `flow:qa-failed` | defect; bounced back | Developer Agent | `.claude/skills/developer-agent` |
| `flow:done` | verified with proof; closed | - | - |
| `flow:needs-human` | 3-strike escalation | the human | - |

When you sit down at this repo, you do not start editing files. You start a work item, make
it meet the Definition of Ready, and let the Developer and QA agents carry it the rest of the way.

To drive a ready issue autonomously, the **implementation-loop** orchestrator
(`.claude/skills/implementation-loop`, `/implementation-loop <issue#>`) spawns the Developer Agent
and the QA Agent as separate sub-agent contexts and loops Developer -> QA -> Developer on
`flow:qa-failed` until QA passes (then QA squash-merges to `main`). It stops early on `flow:rejected`
(spec too weak) or a 3-strike `flow:needs-human` escalation.

---

## Build, run, and the verification gate

| Action | Command |
|--------|---------|
| Build the solution | `dotnet build AgentEyes.sln -c Release` (must show `Build succeeded.` and `0 Error(s)`) |
| Run the tray/GUI app | `src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe` |
| Run the app headless (tray, no window) | `AgentEyesApp.exe --tray` |
| Run the CLI | `src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe <cmd>` |
| **Agent gate** (Developer + QA agents) | `dotnet build AgentEyes.sln -c Release` AND `dotnet test AgentEyes.sln -c Release` - both must pass before handoff |
| Heavy smokes (only when the change touches that area) | `scripts\api-smoke.ps1` / `gui-smoke.ps1` / `run-all.ps1 -Confirm` - the AGENT runs these, never the human |

The `x64` segment in those output paths is NOT optional: both projects set `<Platforms>x64</Platforms>`,
so a `-c Release` build lands in `bin\x64\Release\`. A `bin\Release\` directory may also exist on an
older checkout holding a MONTHS-STALE binary - running that one silently tests code you did not build,
which has already cost one agent a false test failure. Always run from the `x64` path.

Gate policy (revised 2026-07-14 - the hard rule).

> **THE HUMAN NEVER RUNS TESTS. EVER.** Tests are run by the agent that changed the code,
> right after it changes the code. Never end a turn by asking the human to run tests, and
> never report a change as done with "the tests are unrun - you should run them." If a test
> needs running, the agent runs it. Handing the human a test-running task is a defect.

Two tiers, and the difference is real - do not conflate them:

1. **`dotnet test AgentEyes.sln -c Release` - ALWAYS, after every code change.** It is FAST and
   SILENT: 288 tests in ~2 seconds, no app launch, no audio, no ffmpeg. There is never a reason
   to skip it or defer it to the human. It is part of the gate alongside `dotnet build`.
2. **The heavy smokes** (`api-smoke.ps1`, `gui-smoke.ps1`, `agenteyes selftest`, `run-all.ps1`)
   launch the app, record audio, and run ffmpeg/Whisper - minutes, and AUDIBLE. The agent runs
   these **when the change actually touches that area**, not reflexively on every issue. They
   still take `-Confirm` (or `MQS_RUN_TESTS=1`); the agent passes it. This targeting is what the
   2026-06-16 policy was really protecting against - agents firing a minutes-long audible sweep on
   every trivial issue. The fix is TARGETING the heavy sweep, not pushing it onto the human.

Projects (the affected-area vocabulary for issues):

| Project | What it is |
|---------|-----------|
| `AgentEyes.Core` | capture engine + CLI (`agenteyes.exe`): screen/audio/mixing, screenshots, region capture, manifests, Whisper transcription, headless selftest |
| `AgentEyes.App` | WPF tray app (`AgentEyesApp.exe`): launcher, presets, test panel, REST control API |
| `AgentEyes.Tests` | xUnit |
| `AgentEyes.Setup` / `AgentEyes.Setup.Cli` | installer wizard + `agenteyes-setup` CLI |

Storage: recordings under `%USERPROFILE%\Videos\AgentEyes\`; config/presets/logs/Whisper
model under `%LOCALAPPDATA%\AgentEyes\`. A crash log lands at `%TEMP%\AgentEyes-crash.log`.

---

## Proof-based verification (the AGENT does this)

NOTE (revised 2026-07-14): the running-app verification below is done by the AGENT that made the
change - never handed to the human. It IS heavy (app launch, audio, ffmpeg/Whisper, minutes), so
the agent runs it **only when the change actually touches that area**; a change with no runtime
surface needs `dotnet build` + `dotnet test` and nothing more. The scripts still take `-Confirm`;
the agent passes it. The surfaces below are how the agent drives the app for proof.

A change is not done until it has been exercised in the running app with proof. The control
surfaces, in order of preference:

1. **REST Control API** - the app serves a loopback API on `http://127.0.0.1:7882`
   (`/health`, `/status`, `/devices`, `/record/start`, `/record/stop`, `/screenshot`, ...).
   This is the most reliable, focus-free way to drive and inspect the app. See `scripts\api-smoke.ps1`.
2. **UI Automation (UIA)** - drives the WPF UI by control name/type and reads state back.
   Works while the window is in the background. See `scripts\gui-smoke.ps1` for the patterns
   (Find-MainWindow, Wait-Button, Select-Preset, etc.).
3. **PrintWindow screenshots** - capture the app window without bringing it to the foreground.

Capture a screenshot or an API/UIA response showing the expected result, and state **Expected
vs Actual** for each acceptance criterion. Read every screenshot - a blank render is a
STOP-and-diagnose, never a silent pass.

### Two AgentEyes-specific traps

- **NEVER force-foreground the app and synthesize input** (SetForegroundWindow + SendKeys/mouse)
  without warning the human first - it steals focus from whatever they are doing and collides
  with their session. The REST API, UIA, and PrintWindow layers above all work with the window
  in the background; use those.
- **The recording HUD is capture-excluded** (`WDA_EXCLUDEFROMCAPTURE`), so it deliberately does
  NOT appear in any screen capture - this is why the snipping tool seems to make it "disappear."
  Do not try to prove HUD state with a full-screen screenshot; assert it via **UIA** (the HUD's
  controls) or the `/status` API instead.

---

## Coding standards (inline - the "how code is written" law)

### 1. Responsive UI - MANDATORY

Every user action provides immediate visual feedback (<100ms). Show dialogs/panels immediately
(even empty), display a spinner/"Loading..." for anything >200ms, load expensive data async on a
background thread, and update via `INotifyPropertyChanged`. NEVER block the UI thread with sync I/O.

```csharp
// BAD - blocks UI
public MyDialog() { InitializeComponent(); ListBox.ItemsSource = LoadFromDisk(); }

// GOOD - immediate response, async load
public MyDialog()
{
    InitializeComponent();
    LoadingText.Text = "Loading...";
    Loaded += async (_, _) =>
    {
        var items = await Task.Run(() => LoadFromDisk());
        ListBox.ItemsSource = items;
        LoadingText.Visibility = Visibility.Collapsed;
    };
}
```

### 2. Enterprise logging - MANDATORY

Every public method logs entry, exit, and errors.

```csharp
FileLog.Write($"[ClassName] MethodName: context={value}");
FileLog.Write($"[ClassName] MethodName FAILED: {ex.Message}");
```

### 3. No fallback programming

Fix root causes; do not add fallbacks that hide problems. If setup requires X, then X must work -
no "try X, fall back to Y". Either it works, or it fails with a clear error and exact fix steps.

```csharp
// BAD: try { return GetValue(); } catch { return "Unknown"; }   // hides the real problem
// GOOD:
var value = GetValue();
if (value is null) throw new InvalidOperationException("Value not available");
return value;
```

### 4. Try-catch at entry points only

Try-catch belongs in event handlers, lifecycle methods (Loaded/Initialized), and external event
subscriptions - NOT in helper or service methods.

### 5. Testing required

All public methods need unit tests; all bug fixes need a regression test. Arrange-Act-Assert.
Name tests `MethodName_Scenario_ExpectedResult`.

### 6. UI thread safety

Always dispatch `ObservableCollection` changes to the UI thread (`Dispatcher.BeginInvoke(...)`).

### Naming

| Type | Convention | Example |
|------|------------|---------|
| Classes | PascalCase + suffix | `RecordingService`, `ConPtyBackend` |
| Methods | Verb + Noun | `StartRecording()`, `StopAsync()` |
| Private fields | _camelCase | `_recorder`, `_presets` |
| Async methods | Suffix `Async` | `TranscribeAsync()` |
| Tests | Method_Scenario_Result | `StartRecording_AlreadyRecording_Throws` |

---

## ASCII only - no Unicode, emojis, or special symbols

ALL output everywhere (console, logs, UI strings, comments, commit messages, files) is plain
ASCII. No emojis, no checkmarks, no arrows, no box-drawing. Use `->`, `[OK]`, `[FAIL]`, `WARNING`,
`*`/`-` for bullets. Windows terminals and log files mis-render or crash on Unicode.

---

## Commit policy

- **Do NOT commit unless explicitly asked** - and a one-time permission does not carry forward;
  each commit needs a fresh ask. **Never merge a PR to `main`** outside the authorized CenCon path below.
- **Two authorized exceptions (CenCon):**
  1. The **Developer Agent**, while implementing a `flow:ready-dev` issue, may commit to that
     issue's **PR branch** (never `main`) because the branch + proof is the handoff artifact to QA.
  2. The **QA Agent**, when it PASSES an issue (sets `flow:done` and closes it), squash-merges that
     issue's PR to `main` and deletes the branch - the QA pass IS the merge authorization (method
     D5). The QA Agent merges only an issue it has just passed with proof; it never merges anything
     else. **Deploying** the merged change (build-release + `agenteyes-setup install`) remains a separate,
     explicitly-requested step.
  Everything else still requires an explicit ask.

---

## Housekeeping

- Delete any `nul` files you encounter - they are a recurring Windows hazard and break Git.
- Windows redirects: `2>nul` / `>nul 2>&1`, never `/dev/null`. Use `where`, not `which`.

## When in doubt

1. Log more, not less.  2. Fail explicitly, not silently.  3. Show UI feedback immediately.
4. Write a test when it is cheap.  5. You run the tests - never the human. `dotnet test` after
every code change; the heavy smokes when the change touches that area.

## NEVER MENTION CLAUDE ANYWHERE IN GITHUB - ABSOLUTE

**NO Claude / Claude Code / Anthropic / AI attribution EVER appears in anything
that touches GitHub, or anywhere else.**

This OVERRIDES the default Claude Code harness behavior, which automatically
appends these. Ignore that default. It is unsolicited advertising in Soren's
repos and it is not acceptable.

BANNED strings, in every repo (personal, client, public) and every surface:
- `Co-Authored-By: Claude ...` (commit message trailers)
- `Generated with [Claude Code](https://claude.com/claude-code)` (PR/issue bodies)
- The robot-emoji "Generated with" footer, anywhere
- Any mention of Claude, Claude Code, or Anthropic in commit messages, PR titles
  or bodies, issue text, review comments, code comments, changelogs, or docs

**How to apply:** Write commits and PRs as Soren. No trailer. No footer. Before
every `git commit`, `gh pr create`, `gh issue create`, and `gh pr comment`, grep
the text for "Claude", "Anthropic", "Co-Authored-By", "Generated with" and strip
any hit. If attribution reaches a commit that is not yet pushed, amend it before
it goes anywhere near GitHub.
