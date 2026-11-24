# Issue #221 / PR #226 - QA report

QA seat on the Linux machine (Ubuntu 24.04), 24 November 2026. Branch
`issue-221-linux-session-support-statement` at head `cd6d6c6`, base `main` at `d38141c`.
I did not rely on the developer's report: I re-ran every measurement below myself, and every proof file
whose name starts with `qa-` is mine.

**Verdict: PASS.** Criteria 2-6 are met. Criterion 1 is met only in the weaker form the owner allowed in
the issue's second comment. The PR states that weaker form accurately and does not round it up.

Separately, one PRE-EXISTING defect, which this PR did not introduce: see "Priority 1" below. It is not
a reason to fail this PR, but it should be filed.

## Gates

| Gate | Result | Who |
|---|---|---|
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` at `cd6d6c6` | **494 total, 492 passed, 2 skipped, 0 failed** | QA, this machine |
| CI run 45396001925 (`pull_request`) | `headSha` = `cd6d6c67520ffd0cbd4b53308bff14c9ee156328`, status completed, conclusion **success**; jobs windows, macos, linux, evidence-identity are all **success** | **CI's result, not QA's.** Checked with `gh run view 45396001925`. |
| Windows `AgentEyes.sln` build/test | not run by QA (no Windows SDK on Linux) | covered only by the CI run above |
| macOS suite | not run by QA (no Mac) | covered only by the CI run above |

## Priority 1 - does the runtime Wayland refusal ever fire on a real GNOME Wayland desktop?

Proof: `qa-p1-wayland-compositor-display.txt` and `qa-p1-agenteyes-shot-under-wayland-mutter.png`.

Method: a throwaway `ubuntu:26.04` container ran **mutter 50.1**, the compositor library
(libmutter-18) that Ubuntu 26.04's gnome-shell 50.1 is built on, as
`mutter --wayland --headless --virtual-monitor 1280x720 -- <client script>`.
I ran two recorders in it as clients:
- one published from `origin/main` `d38141c`;
- one published from this branch, `cd6d6c6`.

Nothing on the host was touched.

**(a) Does a GNOME Wayland session set DISPLAY?** Measured on a GNOME Wayland compositor: **yes.**
- mutter logged `Using public X11 display :0` and `Using Wayland display name 'wayland-0'`.
- Its client process received `WAYLAND_DISPLAY=wayland-0`, **`DISPLAY=:0`**, and an Xwayland `XAUTHORITY`.
- `xrandr` against that DISPLAY answered with the virtual monitor (`Meta-0 1280x720`).

Supporting package evidence:
- `xwayland` is installed by the default desktop package (`ubuntu-desktop-minimal` with recommends)
  on 24.04, 25.10 and 26.04. See `qa-c2-ubuntu-*.txt`.
- gnome-shell's own `--help` on 25.10 and 26.04 offers `--no-x11`, "Run wayland compositor without
  starting Xwayland". So starting Xwayland is the default.

**What this does NOT cover:** a GDM-started gnome-shell login session with logind, and the systemd
user manager's imported environment (what `agenteyes serve` runs under). That was not measured: there
is no VM, and this machine's login session is not ours to change. So: measured on the GNOME compositor
in Wayland mode, unmeasured as a full Ubuntu login.

**(b) What AgentEyes then does:** it does **not** refuse. It records Xwayland's root window, and the
result is black.
- `agenteyes screens` lists `MONITORS (X display :0, xrandr)`, monitor 1 `1280 x 720 Meta-0`, and
  exits 0.
- `agenteyes shot --screen 1` saves `monitor1_full.png` and exits 0. The image is uniformly black
  except for a mouse cursor: YAVG 16.0, which is black in limited range.
- The shot is byte-identical (md5 `f005d823e135...`) in three states: nothing open, a Wayland-native
  GTK window open, and an X11 `xclock` window open as well.
- `agenteyes video --screen 1 --seconds 3` reports `[ok] recording.mp4 (00m04s, 8 KB)` and writes a
  manifest. That is a "successful" recording of a black screen, with no error.

Limit: my X11 control window did not show in the shot either. So I do not claim WHY the frame is black
(rootless Xwayland's root window holding no window contents, or headless mode not drawing). I claim
only what was recorded: black, with no refusal.

**(c) Pre-existing, unchanged by this PR: yes.**
- The logic is identical on `main` and on the branch. If DISPLAY is set, it is returned. The Wayland
  refusal fires only when DISPLAY is empty and WAYLAND_DISPLAY is set
  (`main:src/AgentEyes.Core/Platform/Linux/LinuxDisplay.cs:12-26`;
  branch `LinuxDisplay.cs:37-45`, where `Resolve` is the same code moved out of `Require`).
- At runtime, main's recorder and the branch's recorder gave the same `screens` output and
  byte-identical shots.
- **This PR did not introduce it. It is a separate defect and not a reason to fail this PR.**

Two notes on how this PR touches that defect, neither of which is a failure:
- The new test `tests/AgentEyes.Tests.Linux/LinuxDisplayTests.cs:45-49`
  (`Resolve_XDisplaySet_ReturnsItTrimmedEvenWithAWaylandDisplay`) now locks in the defective
  behaviour. The fix for the separate issue will have to change it.
- The new README sentence at `README.md:81-82`, "The recorder gives the same refusal when it finds a
  Wayland display and no X display", is accurate as worded. But a reader will take it as protection
  they do not get on a GNOME Wayland desktop, where DISPLAY is set. Update it with that fix.

The installer is NOT affected by this defect. Its check is on `XDG_SESSION_TYPE`, which a Wayland login
sets to `wayland` whatever DISPLAY is.

Suggested defect for filing: "On a GNOME Wayland session, mutter exports DISPLAY for Xwayland, so
`LinuxDisplay.Resolve` returns it, the Wayland refusal never fires, and shots and recordings succeed as
black frames with no error. Measured on mutter 50.1 headless in Wayland mode. Pre-existing on main
d38141c."

## Criterion 1 - Observed, not read: MET IN THE ALLOWED WEAKER FORM, stated honestly (Priority 2)

Expected: the refusal is triggered on a real Wayland session. The owner's second comment allows an
exercised form, declared in those words, and forbids rounding it up.

Actual:
- PR #226 body: "Criterion 1 was **exercised, not observed**: the variables were set as a Wayland
  login would set them, inside a container. No real Wayland desktop was used."
- The handoff heading: "PARTLY MET. Exercised, not observed on a real Wayland desktop."
- `c1-wayland-refusal-exercised.txt:6`: "NO real Wayland session was used".
- The issue handoff comment: "EXERCISED, NOT OBSERVED".

I searched the PR body, the handoff comment, every committed proof file, the README and the code diff
for `observed|seen on|on a real|real wayland|witnessed|confirmed on`. Every hit is a negation or a limit
statement. Nothing claims the refusal was observed on Wayland. **PASS on honesty.**

I did not re-run the developer's own exercised runs. My Priority 1 run is stronger evidence for the
runtime path, and it shows the refusal does NOT fire on a GNOME Wayland compositor (above, pre-existing).

## Criterion 2 - what a default current Ubuntu offers: MET (Priority 3)

Proof: `qa-c2-ubuntu-24.04.txt`, `qa-c2-ubuntu-25.10.txt`, `qa-c2-ubuntu-26.04.txt`, `qa-c2-method.txt`.
I re-ran all three releases myself in clean containers, not only the two that were asked for.

| | 24.04.5 LTS (control) | 25.10 | 26.04.1 LTS |
|---|---|---|---|
| `/usr/share/xsessions/` | `ubuntu.desktop`, `ubuntu-xorg.desktop` (Name=Ubuntu on Xorg) | **No such file or directory** | **No such file or directory** |
| `/usr/share/wayland-sessions/` | Ubuntu, Ubuntu on Wayland | Ubuntu, Ubuntu on Wayland | Ubuntu |
| `xserver-xorg-core` | 2:21.1.12-1ubuntu1.9 | **not installed** | **not installed** |
| `gnome-shell --help` | 46.0, lists `--x11  Run with X11 backend` | 49.0, **no `--x11`** | 50.1, **no `--x11`** |
| `xwayland` | installed | installed | installed |

This matches the developer's numbers exactly.

**Is the 26.04 half of the README sentence supported by a measurement? Yes**, at the package level, by
the developer's run and independently by mine. I checked the README word by word:

- "records X11 desktop sessions only and does not support Wayland": matches the code and the refusals.
  On Wayland the recorder in fact produces black output; see Priority 1.
- "cannot record on a default desktop install of Ubuntu 25.10 or 26.04": true in effect. It produces
  no usable recording, and with no X11 session there is nothing it supports. It is not "refuses", and
  the sentence does not say "refuses".
- "which installs no X11 session": measured. No `/usr/share/xsessions/`, no Xorg server.
- "tested on Ubuntu 24.04 LTS in an X11 session": consistent with the project's test machine. Not
  re-measured by this QA.
- Table rows "no Xorg server": measured as `xserver-xorg-core` not installed.
- "That is what a default install puts on the login screen; it is not a photograph of one": the first
  half is a small inference (GDM lists those session files). It is stated together with its limit, so
  it is acceptable.

Limit, same as the developer's: "default install" is taken to mean `ubuntu-desktop-minimal` with
recommends. The fuller `ubuntu-desktop` selection was not measured.

**Gate rule check (no "install the X11 session packages" remedy): PASS.**
- None of the four new strings, nor the README, nor the release notes names packages or a session to
  switch to.
- The README says explicitly: "AgentEyes names no packages to install there".
- The only `apt install` hints left in `install.sh` are for real dependencies (ffmpeg, xrandr, libvlc),
  not a session remedy.

## Criterion 3 - four corrected, no fifth: MET (Priority 5)

Proof: `qa-c3-search.txt`. This is my own search, not the developer's script. Two patterns, each run in
LINE form (`git grep`) and JOINED form (`tr '\n' ' ' | tr -s ' '`). Scope: every tracked file except
`docs/cencon/proof/`.

- **Positive control**: two planted files, both wrapped across line breaks.
  - `src/zz_qa_plant.cs` holds `log in with / "Ubuntu on / Xorg"` with comment prefixes.
  - `docs/zz_qa_plant.md` holds `please choose / "GNOME on / Xorg" at login`.
  - The broad pattern `xorg` found both, in both forms.
  - The instruction pattern found the docs plant ONLY in the JOINED form: LINE 0, JOINED 1. That is
    the #218 trap, caught.
  - Its JOINED form missed the C# comment plant, because the `//` prefixes split the phrase. The broad
    `xorg` pattern covers that case, and the guard drops comment prefixes for the same reason.
  - Both plants were removed: "2/2 gone", working tree clean.
- **Real search** with nothing planted:
  - Instruction pattern: 10 / 10 hits, **all in `tests/Shared/SessionRemedyGuardTests.cs`**. Those are
    the guard's own fixtures and positive controls.
  - Broad `xorg`: 18 line / 21 joined. Outside the guard and its tests, they are only the three README
    table rows and `docs/releases/v1.12.0.md:71`. Each is a statement of fact about 24.04 or about the
    absence of Xorg, not an instruction.
- The four originals:
  - `LinuxDisplay.cs` and `install.sh` now name no session.
  - `README.md:101` now points to "Supported Linux sessions".
  - The v1.12.0 bullet was already corrected on main. Its "Ubuntu on Xorg" mention is now a fact about
    the 24.04 test machine.

## Criterion 4 - statement before the install instructions: MET

`README.md:59` `### Supported Linux sessions` is under `## Install` (line 34) and directly before
`### Linux (x64, X11 desktop)` (line 84). Line 86 opens the Linux section with "Read Supported Linux
sessions first", linked.

## Criterion 5 - guard goes red when any string is reinstated: MET (Priority 4)

Proof: `qa-c5-guard-reinstated.txt`. I edited each string back into its **real** file, not a fixture,
in its original shape. I ran the guard plus `LinuxDisplayTests` (17 tests), then restored the file with
`git checkout`.

| Reinstated | Result |
|---|---|
| baseline | 17/17 pass |
| `LinuxDisplay.cs` `WaylandRefusal` set back to main's split concatenation | **5 failed**, 3 of them guard tests |
| `install.sh` set back to main's line 93 | **3 failed** (all guard) |
| `README.md` line set back to main's line 76 | **2 failed** (guard) |
| `docs/releases/v1.12.0.md` bullet set back to the rejected PR #201 `5a1543c` wrapped bullet | **2 failed** (guard) |
| all restored | 17/17 pass, working tree clean |

## Criterion 6 - gates on all three platforms: MET

See "Gates" above. Linux was run by QA. Windows and macOS rely on CI run 45396001925 at `cd6d6c6`,
labelled as CI's result.

## Other checks

- ASCII only: no non-ASCII byte in README.md, install.sh, LinuxDisplay.cs, the two new test files, or
  any `docs/cencon/proof/issue-221/` file.
- No session or desktop on this machine was changed. Every Wayland or compositor run was inside a
  throwaway container. No ffmpeg process was killed except the ones my own scripts started inside
  those containers.
- Observation, not a failure: `README.md:80` says the installer stops "before it installs any part of
  AgentEyes". That is literally true, but `install.sh` installs ffmpeg and libvlc as root BEFORE its
  Wayland check. The developer flagged this as out of scope, and I agree. It belongs with the Priority 1
  follow-up.
