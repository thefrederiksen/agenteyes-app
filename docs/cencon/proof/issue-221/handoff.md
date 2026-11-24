# Issue #221 - Developer handoff

Branch `issue-221-linux-session-support-statement`, from `main` at `d38141c`. Written on the Linux
machine (Ubuntu 24.04), 24 November 2026. I believe this is finished, with the limits stated below.

## The supported-session statement (one sentence, README.md "Supported Linux sessions")

> AgentEyes on Linux records X11 desktop sessions only and does not support Wayland, so it cannot
> record on a default desktop install of Ubuntu 25.10 or 26.04, which installs no X11 session; it is
> tested on Ubuntu 24.04 LTS in an X11 session.

The remedy is gone everywhere. The product now says the session type is unsupported and names NO
login session and NO packages to install. That follows the Review Gate's correction on this issue:
on 25.10 and 26.04 there is no X11 GNOME session to install (measured below), so any named remedy
would replace one instruction a user cannot follow with another.

## What changed

| File | Change |
|---|---|
| `src/AgentEyes.Core/Platform/Linux/LinuxDisplay.cs` | The runtime refusal is `WaylandRefusal`: "...records an X11 desktop session only; Wayland sessions are not supported. See 'Supported Linux sessions' in the AgentEyes README." `Resolve(display, waylandDisplay)` was split out of `Require()` so it can be tested without changing the process environment. The behaviour is unchanged. |
| `scripts/linux/install.sh:95` | The installer refusal uses the same wording and names no remedy. |
| `README.md` | A new section, `### Supported Linux sessions`, placed before `### Linux (x64, X11 desktop)`. That Linux section now opens with "Read Supported Linux sessions first", and its old line 76 points to the new section. |
| `docs/releases/v1.12.0.md` | Not changed. The bullet was already corrected on `main`. Its "Ubuntu on Xorg" mention is a fact about the 24.04 test machine, not an instruction, and the guard allows that on purpose. |
| `tests/Shared/SessionRemedyGuardTests.cs` (new; linked into the Windows, Linux and macOS suites) | Reads README.md, docs/releases/*.md, src/**/*.cs and scripts/**/*.sh and *.ps1 in their joined form (comment prefixes dropped, C# `" + "` joints closed, all whitespace including line breaks collapsed). See criterion 5 for what it checks. |
| `tests/AgentEyes.Tests.Linux/LinuxDisplayTests.cs` (new) | Tests the runtime refusal: its text, that it names no session, the no-DISPLAY refusal, trimming, and ASCII only. |

## Acceptance criteria

### 1. Observed, not read - PARTLY MET. Exercised, not observed on a real Wayland desktop.

Proof file: `c1-wayland-refusal-exercised.txt`. Both paths were run for real, before (main) and after
(branch), each with a control. All runs were inside a throwaway ubuntu:24.04 container, so nothing on
this machine was touched.
- **Installer:** `XDG_SESSION_TYPE` was **set by hand to the value a Wayland login sets
  (`wayland`)**. This is not a real Wayland desktop. Output after the change:
  `[FAIL] this is a Wayland session. AgentEyes on Linux records an X11 desktop session only; Wayland sessions are not supported. See 'Supported Linux sessions' in the AgentEyes README.`
  The exit code was 1 and nothing was installed. In the control run with `x11`, the script passed
  the check and installed into the container. That run's later exit 1 comes from `systemctl --user`,
  because the container has no systemd user bus.
- **Runtime:** `agenteyes screens` and `agenteyes serve`, run with `DISPLAY` **unset** and
  `WAYLAND_DISPLAY` **set by hand to `wayland-0`**. No compositor was running. Both print the new
  refusal and exit 1. The control run, with neither variable set, prints the no-DISPLAY refusal.

What the criterion literally asks for, a real Wayland login session, was not available. This machine
has no VM, and changing its own login session is forbidden. The issue's second comment allows this
exercised form, declared as such, and that is all I am claiming.

**Open risk, unmeasured:** a real GNOME Wayland session normally also sets `DISPLAY`, through
Xwayland. If it does, the runtime refusal never fires on a real Wayland desktop, and the recorder
would try to grab Xwayland's root window instead. That is a behaviour question outside this issue,
which says "changing the refusal itself" is out of scope. I am flagging it for a follow-up and have
not measured it. Also out of scope and only noted: `install.sh` checks the session type **after**
it has installed ffmpeg and libvlc as root, so a Wayland user is asked for root and then refused.

### 2. What a default current Ubuntu offers - MET, at the package level

Proof files: `c2-ubuntu-{24.04,25.10,26.04}-desktop-sessions.txt`, made by the script in
`c2-measure.sh`. Method: a clean `ubuntu:<release>` container, then `ubuntu-desktop-minimal` installed
**with recommends** (the default selection), then the session files listed and their owners shown.
This is a **package-level measurement of what a default desktop install puts on the login screen. It
is not a photograph of a login screen.**

| | 24.04.5 LTS (control) | 25.10 | 26.04.1 LTS |
|---|---|---|---|
| `/usr/share/xsessions/` | `ubuntu.desktop`, `ubuntu-xorg.desktop` (Name=Ubuntu on Xorg) | **does not exist** | **does not exist** |
| `/usr/share/wayland-sessions/` | Ubuntu, Ubuntu on Wayland | Ubuntu, Ubuntu on Wayland | Ubuntu |
| `xserver-xorg-core` | installed | **not installed** | **not installed** |
| gnome-shell | 46.0, `--help` lists `--x11` | 49.0, **no `--x11`** | 50.1, **no `--x11`** |

The missing `--x11` option is direct evidence for the Review Gate's reading that GNOME Shell on these
releases cannot run on X11. So installing packages would not bring "Ubuntu on Xorg" back, and no
packages are named as a remedy. The 25.10 archive was still reachable, so no repointing to
old-releases was needed.

### 3. All four corrected, and no fifth occurrence - MET

Proof files: `c3-no-fifth-occurrence.txt`, made by the script in `c3-search.sh`.
- Every search was run in two forms: LINE (`git grep`) and JOINED (`tr '\n' ' ' | tr -s ' '` per file).
- Scope is every tracked file except two, excluded by name because they quote the old strings on
  purpose: `docs/cencon/proof/` and the guard test's own fixtures.
- Positive control 1 planted the remedy **wrapped across a line break**. LINE found **0** and JOINED
  found **1**. This is the #218 trap, and the joined form catches it.
- Positive control 2 planted it on one line. LINE found 1 and JOINED found 1. Each planted file was
  removed afterwards, and the output records its removal.
- The real search: remedy pattern A found **0 / 0**. Pattern B (any mention of an Xorg session name)
  found 2 / 2. Both are true statements of fact about 24.04, not instructions: the README table row
  and the already-corrected line in the v1.12.0 notes.

### 4. Supported-session statement reachable before the install instructions - MET

`README.md`: `### Supported Linux sessions` sits under `## Install`, immediately before
`### Linux (x64, X11 desktop)`, and the Linux section's first sentence links to it. The Windows
install steps come earlier in `## Install`, but they are not Linux install instructions.

### 5. The guard goes red when any of the four strings is reinstated - MET

Proof file: `c5-guard-goes-red.txt`. Each original string was put back into its **real** file, one at
a time, in its original shape: the split C# concatenation, the wrapped Markdown, and the rejected
release-notes bullet from PR #201 commit `5a1543c`. After each, the Linux suite's guard ran and the
file was restored with `git checkout`. Results:
- LinuxDisplay.cs: 5 tests failed.
- install.sh: 3 tests failed.
- README.md: 2 tests failed.
- v1.12.0.md: 2 tests failed.
- After all four files were restored: 17/17 passed.

The guard checks three things:
- (a) the four removed strings;
- (b) the class: any "log in / choose / select / switch to / use ..." followed by a named session
  such as "Ubuntu on Xorg", "GNOME on Wayland" or "Plasma (X11)";
- (c) shipped code (src/, scripts/) may not name a login session at all.

The guard's own test cases also include each original shape as a positive control.

### 6. Gates on all three platforms - Linux run here; Windows and macOS NOT run by me

| Gate | Result | Who ran it |
|---|---|---|
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | **494 total, 492 passed, 2 skipped, 0 failed** (17 new) | me, on this machine, at the branch head |
| `dotnet build tests/AgentEyes.Tests.Mac -c Release` | compiles, 0 errors | me. Its 12 guard tests also pass **under Linux**. That is NOT a macOS gate. |
| `dotnet build AgentEyes.sln -c Release` + `dotnet test AgentEyes.sln -c Release` (Windows) | **not run** | Cannot run on Linux: the WindowsDesktop SDK is absent (`MSB4019`). Left to CI and QA. |
| `dotnet test tests/AgentEyes.Tests.Mac` (macOS) | **not run** | There is no Mac here. Left to CI and QA. |

CI on the PR runs all three gates. Its results will be posted on the PR, and they are CI's results,
not mine.

## For QA

- None of this needs the app launched. No smoke is needed, because the change is two refusal
  strings, a README section and tests.
- To re-run the proofs: run the `c2-measure.sh` script inside a `ubuntu:<release>` container; run
  `bash docs/cencon/proof/issue-221/c3-search.sh` from the repo root; and for criterion 1, follow
  the docker steps described at the top of `c1-wayland-refusal-exercised.txt`.
- On the Linux machine: do NOT change its desktop or login session, and never kill an ffmpeg you
  did not start, because the machine runs a live camera wall. Any real `agenteyes` process acts on
  the owner's library. That is why every run above happened inside a container.

## CenCon impact

No drift in the component map. The privacy posture (visible, controllable) is unchanged. The README
now states a support boundary that was previously implied by silence: a default install of Ubuntu
25.10 or 26.04 is unsupported by AgentEyes on Linux. That is the product decision the issue asked to
have recorded. Recording Wayland sessions is not built and belongs in its own issue.

## Round 2 - the Review Gate rejection of PR #226 at 990ff5f (wording only)

The gate rejected new README text that promised a recorder refusal the recorder was measured not
to perform. This round changes wording only. The refusal logic, the four removed remedy strings,
the guard test, the container evidence and criterion 2's table are untouched.

| Gate item | What changed |
|---|---|
| README.md:80-82 (recorder refusal) | Now says the recorder refuses only when there is no X display at all; a Wayland session that also provides one through Xwayland (measured on mutter 50.1) is not refused, exits 0 with `[ok]`, and the screenshot and recording are black. States the limit: headless mutter in a container, not a GDM login session, not the systemd user service environment. Points at issue #227. |
| README.md:68-69 ("does not record") | Now "not supported; a recording made there is black (see below)". |
| LinuxDisplayTests.cs:9-10, LinuxDisplay.cs:13 | Doc comments now say only the no-X-display case is refused, and that the Xwayland case passes Resolve and records black (#227). No code changed. |
| README.md:80 ("before it installs any part") | Now says the installer checks ffmpeg and libvlc first, installs whichever is missing with apt-get as root, and on 25.10 and 26.04 (neither is in the default desktop set) does both before it refuses; it stops before installing the AgentEyes recorder or app. The check was NOT moved. |
| README.md:76-77 ("no X11 GNOME session to install") | Narrowed to what was measured (GNOME Shell has no X11 mode), plus: whether any other X11 session from the archive records with AgentEyes was not tested. No package and no session is named. |
| README.md line endings | Round 1 converted the whole file from CRLF to LF (main: 165 of 165 lines CRLF; branch: 0). That was not part of the fix and was not mentioned. Reverted: all 201 lines are CRLF, and `git diff origin/main -- README.md` now equals `git diff -w`: 38 insertions, 2 deletions. |

Searches (README.md and both .cs files), each in line form and whitespace-joined form, for "does
not record", "gives the same refusal", "before it installs any part", "no X11 GNOME session to
install", "It refuses (correct": LINE 0, JOINED 0 in every file. Control: a planted "gives the
same" / "refusal" wrapped across a CRLF line break in README.md gave LINE 0, JOINED 1; removed,
back to 0.

Gate, run by me at the round-2 head: `dotnet test tests/AgentEyes.Tests.Linux -c Release` ->
494 total, 492 passed, 2 skipped, 0 failed. `git merge-tree` against main b5f5e2c: clean.
