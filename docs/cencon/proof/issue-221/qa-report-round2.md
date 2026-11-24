# QA report - issue #221 / PR #226, round 2 (head 8ec74ab)

Verdict: **PASS.** The six Review Gate items are fixed, nothing else was churned, and every
README line added by this PR describes a behaviour that has been measured.

What was wrong with round 1's QA pass: its report wrote down that README.md:81-82 "a reader will take it
as protection they do not get" and passed it anyway. This round, the test for every added README
line was: was this measured? A line that needs a warning fails. No added line needed one.

## The gate's six items

| # | Item | Expected | Actual (QA, at 8ec74ab) | Result |
|---|---|---|---|---|
| 1 | README recorder paragraph (README.md:87-93) | Says the recorder does NOT reliably refuse; refuses only with no X display; mutter 50.1 with WAYLAND_DISPLAY and DISPLAY=:0; screens/shot/video exit 0 with [ok]; shot and recording black; limit = headless mutter in a container, not GDM, not the systemd user service; points at #227 | All of it is present. #227 is open and is that defect. The code agrees: `LinuxDisplay.Resolve` (LinuxDisplay.cs:39-48) refuses only when DISPLAY is empty. QA re-measured it, see "Independent measurements" below | PASS |
| 2 | README table rows 25.10 / 26.04 (README.md:68-69) | Say what the recorder does | "not supported; a recording made there is black (see below)". QA measured black video on both releases (mutter 49.0 on 25.10, 50.1 on 26.04); before this round, nobody had measured 25.10 or the video's luminance | PASS |
| 3 | LinuxDisplayTests.cs:9-14, LinuxDisplay.cs:13-15 | No implied refusal that does not happen; behaviour unchanged | Both doc comments now say only the no-X-display case is refused and the Xwayland case records black (#227). The diff against 990ff5f changes only `///` lines (3+/1- and 6+/4-); no other line changed | PASS |
| 4 | README line endings | CRLF restored | main 165/165 CRLF; branch 201/201 CRLF; `git diff origin/main` = `git diff -w` = 1 file, 38 insertions, 2 deletions | PASS |
| 5 | Installer sentence (README.md:81-85) | No longer reads as "nothing happens"; matches install.sh; check not moved | The text says the installer checks ffmpeg and libvlc first, installs them with apt-get as root, and only then refuses, before installing the recorder or app. install.sh:73-92 runs before the session check at :95 (unchanged). The release script (install-release.sh:108) runs this install.sh with --prebuilt, so this is the path users get. QA ran it: see below | PASS |
| 6 | "no X11 GNOME session to install" (README.md:76-79) | Narrowed to "GNOME Shell has no X11 mode"; names NO remedy | It now says the shell has no `--x11`. It also says no other X11 session from the archive was tested. No package or session is named. The search below found no flashback or xserver-xorg in README, the release notes, the scripts, or the two .cs files | PASS |

## Independent measurements (throwaway containers; host desktop untouched; no host ffmpeg touched)

- **Recorder under headless mutter** - `qa2-recorder-under-mutter.txt`. Recorder published from 8ec74ab.
  - 26.04.1, mutter 50.1-0ubuntu2.5: client got WAYLAND_DISPLAY=wayland-0 and DISPLAY=:0. `screens`, `shot` and `video --seconds 3` each exited 0; shot `[ok] saved`; video `[ok] recording.mp4 (00m04s, 8 KB)`. Shot YAVG 16.01; video 122 of 122 frames YAVG 16.01 (black; YMAX is the cursor).
  - 25.10, mutter 49.0-2ubuntu5.3: same result, same luminance.
  - Same limit as the README states: headless mutter, not a GDM login session, not the systemd user service.
- **Default package set** - `qa2-installer-wayland.txt`. Method: `apt-get install -s ubuntu-desktop-minimal`.
  - On 25.10 and 26.04, ffmpeg and libvlc are NOT in the set. x11-xserver-utils (xrandr) IS in it.
  - So install.sh gets past its xrandr check and reaches the session check.
- **Installer on a Wayland session** - same file.
  - Setup: 26.04, run as a non-root user who has sudo, with XDG_SESSION_TYPE=wayland. ffmpeg and libvlc were absent at the start.
  - Result: it apt-installed ffmpeg, printed `[OK] video playback library installed`, then `[FAIL] ... Wayland sessions are not supported ...`, and exited 1.
  - Afterwards ffmpeg and libvlc5 were installed, and no AgentEyes file was in the user's home.
  - This matches README.md:81-85 word for word.

## Every added README line, read for unmeasured promises

`git diff origin/main 8ec74ab -- README.md`: 38 lines added, read one by one.
- The supported-session sentence and the 24.04 row are unchanged from round 1. The gate already verified them.
- "cannot record on a default desktop install of 25.10 or 26.04" is true as measured: what is written there is black.
- The 25.10 and 26.04 rows are covered by the mutter measurements above.
- The installer paragraph is covered by the installer run above.
- The recorder paragraph is covered by the code and the measurements above, and it states its own limit.
- The install-section cross-references promise nothing.
- Result: no added line promises a behaviour that was not measured.

## Remedy strings - `qa2-remedy-search.txt`

Files searched: README.md, docs/releases/v1.12.0.md, install.sh, install-release.sh, LinuxDisplay.cs and LinuxDisplayTests.cs. Each was searched in line form and in whitespace-joined form, with CR stripped.

- The four removed remedies: 0 hits in both forms.
- flashback, xserver-xorg and "install the X11": 0 hits.
- The only remaining hits are statements of fact:
  - "Ubuntu on Xorg": the 24.04 table cell, and the gate-verified v1.12.0 notes line, which is unchanged.
  - "log in with": a doc comment that says the old message used to say this, and a test assertion `DoesNotContain("log in with")`.
- Control: a remedy planted in README.md, wrapped across a CRLF break, gave LINE 0 and JOINED 1 for the full phrase. Removing it returned the search to the baseline.

## Guard goes red - `qa2-guard-reinstated.txt`

- install.sh with main's refusal text restored: 3 of 12 guard tests fail.
- README.md with main's "log in with "Ubuntu on Xorg"", split across a CRLF break: 2 of 12 fail.
- After restoring both, the guard and LinuxDisplayTests pass 17 of 17, and the full Linux gate is green.

## Proof folder not churned

Against 990ff5f, the only change under docs/cencon/proof/issue-221/ is handoff.md, which gained 24 lines and lost none. No gate-verified file was modified or removed. This round adds only the qa2-* files and this report.

## Gate, run by QA

- `dotnet test tests/AgentEyes.Tests.Linux -c Release` at 8ec74ab: **494 total, 492 passed, 2 skipped, 0 failed**.
- The same counts on a throwaway merge of 8ec74ab with main b5f5e2c. `git merge-tree` is clean.
- AgentEyes.sln was not built; it does not build on Linux.
- CI is CI's result, not QA's. For 8ec74ab, runs 45408872247 (push) and 45408889022 (pull_request) are both green on windows, macos, linux and evidence-identity.

VERIFIED - all six gate items fixed; label flow:ready-gate. QA does not merge (D7).
