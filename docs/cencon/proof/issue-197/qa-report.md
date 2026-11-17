# Issue #197 - QA report: corrections to the published v1.12.0 release notes

PR #201, head `5a1543c` on `issue-197-release-notes-corrections`, from main `02a4622`.
QA seat: the macOS QA clone. Date: 17 November 2026.

**Result: FAIL (flow:qa-failed).** One new sentence, in the #159 rewrite, says more than its
measurement shows. Everything else checks out against the source evidence.

What this was verified against: NOT the developer's handoff. The sources are:

- **Windows text:** the Windows install seat's report, `docs/cencon/proof/issue-193/qa-report-windows-install.md`
  on PR #199 (`69462d0`), sections 4 and 6, and its screenshots `06-smartscreen-windows-protected.png`,
  `07-smartscreen-more-info-run-anyway.png` and `01-open-file-security-warning.png`, which I opened
  and read. I also checked the lead's #197 comment of 2026-11-16 23:38Z, which summarises that report.
- **Linux text:** the Linux install seat's report, `docs/cencon/proof/issue-193/qa-report-linux-install.md`
  on PR #198 (`85c8e5f`), its raw `linux-install/take.txt`, and `scripts/linux/install.sh` at the
  tag `02a4622`. I also checked the lead's #197 comment of 2026-11-16 23:27Z.
- **Everything else:** issues #157, #165 and #187, and the gate's comment 6932209492.

**I am on a Mac. I did not observe SmartScreen.** The Windows text is verified against the Windows
seat's recorded observation (the report and the three screenshots above). I did not re-observe it.

---

## DEFECT 1 (blocking) - #159: "still missing 3.9 s after `/status` read `idle`"

New text (section 3, #159):

> after a 32 s take, `recording.mp4` was still missing 3.9 s after `/status` read `idle`

The seat's raw timeline (`docs/cencon/proof/issue-193/linux-install/take.txt` on PR #198):

| Clock | Since the stop request | `/status` | manifest mux | `recording.mp4` |
|-------|------------------------|-----------|--------------|-----------------|
| 18:22:02.118 | 0 (request sent) | - | - | - (reply's `File` names it) |
| 18:22:02.750 | +0.6 s | `idle` (first read) | running | **missing** |
| 18:22:03.825 | +1.7 s | `idle` | running | **missing** |
| 18:22:04.898 | +2.8 s | `idle` | running | **missing** (`.muxing.tmp` now present) |
| 18:22:05.974 | +3.9 s | `idle` | done | **present** |

- At 3.9 s the file was **present**, not missing. The last read that found it missing was at 2.8 s.
- The 3.9 s is counted from the **stop request**, not from `idle`. `idle` was first read at +0.6 s.
  So the file was last seen missing **2.1 s after `idle` was first read**.
- What the measurement supports: the file appeared between 2.8 s and 3.9 s after the stop request,
  and it was still missing at least 2.1 s after `/status` first read `idle`.

The new sentence overstates the gap by about 1.8 s and names the wrong starting point. This is the
same kind of error the 19 earlier corrections removed. The error came from the seat's own prose ("missing
for about 3.9 s") and the lead's comment ("missing 3.9 s after idle"). The handoff says the source was
"Linux install report N1 (timeline table)". The table itself has the file present at +3.9 s.

The commit message repeats it ("file missing 3.9 s after idle"). That message is history and cannot
be changed. Please do not repeat it in the fix commit.

**Fix, for example:** "after a 32 s take, `recording.mp4` was still missing 2.8 s after the stop
request, by which time `/status` had read `idle` for 2.1 s, and it was there 1.1 s later". Any
wording is fine if it matches the table above.

Everything else in the #159 rewrite matches `take.txt`:
- `/record/stop` returned a `File` path that did not exist yet.
- At +0.6 s there was only the raw capture and no `.muxing.tmp`. `.muxing.tmp` appeared by +2.8 s.

## DEFECT 2 (minor; I would not block on this alone, but fix it while you are there) - "they do not look like one"

New text: *"Click the words "More info" under the message: they are a link, though they do not look
like one."*

`06-smartscreen-windows-protected.png` shows "More info" **underlined**, directly under the message.
That is a link's normal look. The seat's report calls it "a small More info link" and "a text link".
Only the lead's summary says "not obviously a link". The screenshot shows a link that is easy to miss,
not one that looks like something else. Suggested: "they are a small underlined link, easy to miss".
The instruction to click it is correct and stays.

---

## Every new sentence, checked against what measured it

| Edit | Source checked | Verdict |
|------|----------------|---------|
| Table cell: "Windows warns before it runs the installer. Section 2 says exactly what to click." | Windows report s4 | OK. "The installer" is the downloaded wizard exe. The README one-liner fetches the setup CLI with `iwr` (README.md:44), which writes no Mark-of-the-Web, and section 2 says it showed no warning. |
| SmartScreen: "Windows protected your PC", only button "Don't run" | screenshot 06, report s4 item 1 | OK, exact |
| "Click the words More info under the message" | screenshot 06 | OK. It is placed directly under the message text |
| "they are a link, though they do not look like one" | screenshot 06 | **DEFECT 2** |
| "The window then shows 'Publisher: Unknown publisher' and a second button, Run anyway" | screenshot 07 | OK, exact |
| "Click Run anyway and the installer opens" | report s4: "the wizard opened. No second prompt and no UAC prompt" | OK |
| "If you downloaded it with a browser and open it from File Explorer" | report s4: the seat wrote a browser's Zone.Identifier (ZoneId=3) on a curl copy and double-clicked it in Explorer | OK as a conditional. Note: no real browser download was made. The Mark-of-the-Web, which is what triggers SmartScreen, was simulated. See "second Windows observation" below |
| "Open File - Security Warning ... publisher could not be verified. Click Run" | screenshot 01, report s4 item 2 | OK. The "you may see" hedge fits: it was seen only with `Start-Process` |
| "the one-line terminal install showed no warning at all" | report s4 / s6 | OK. Qualified "on the test machine" |
| Smart App Control paragraph | report s4 "Not measured" and s6 item 3 | OK. Same words as the seat's: "expected to", "not measured", "off on the machine" |
| #157 "shown on Linux only"; macOS "read, not tested" | #157 body (devlinux); gate 6932209492 ("placed the loss at the capture pipe, by reading the command line") | OK |
| #159 "For a while it is not on disk at all"; Linux "only the raw capture; later .muxing.tmp" | take.txt | OK |
| #159 "still missing 3.9 s after /status read idle" | take.txt | **DEFECT 1** |
| #159 "the File path ... did not exist yet" | take.txt | OK |
| #159 "On Windows ... the file itself was not looked for there" | Windows report s5: the seat waited on the manifest and did not look for the file | OK |
| #159 "Waiting on the temporary file does not work, because it is not there at first" | take.txt (Linux) | OK. A wait that fails on one platform is not reliable. Only Linux was measured |
| Linux: refuses on Wayland | install.sh:68 at 02a4622 | OK. It is what the script does. The seat read this from the script and did not see it. Note: the check runs after the ffmpeg/libvlc root installs (lines 48-67) |
| Linux: needs xrandr | install.sh:55 | OK, exact message |
| Linux: libvlc as root, three packages | install.sh:58-66 | OK |
| Linux: sign-in failure ends `[FAIL]`, recorder already installed and running | install.sh:25, 141-161 | OK on the default path (`start=1`). The app starts only after sign-in (line 163), so the note is right to name only the recorder |
| Linux: "Updating stops any recording ... the script says the recording is kept, but that has not been tested" | install.sh:78-80; the seat installed while idle | OK. It does not say "loses the take" |
| Section 4 / #165 paragraph | #165 body; Linux report N3 | OK. It is labelled inferred and not measured |
| Section 6: #186 found by running, #187 by reading the code | #187 body ("found by reading the shared implementation ... The macOS case has not been run") | OK |
| Section 1 maintainer paragraph removed | Linux N4, Windows s6 item 4 | OK. 0 `gh release` hits remain in the notes |
| D1 "Released 16 November 2026" | Windows report s2: published 2026-11-16T23:17:01Z | OK |
| D2, D3 | wording only | OK |

**The overstatement check.** I scanned the added lines for "lose", "lost", "loss", "never",
"always", "exactly", "any" and "all". "Loses the take" appears nowhere. The update case says
"stops ... has not been tested", and the Smart App Control and #165 text keep the seats'
qualifiers. The one new overstatement is Defect 1. Defect 2 is a smaller one.

## The reversal check, re-run with my own script

My own Python script, not the developer's (the developer's is not in the PR). It takes the 13
before/after pairs from the handoff. For the deleted paragraph, the reverse direction re-inserts it
before "## 2. How to install". Each replacement is asserted to match exactly once. Files: `02a4622`
and `5a1543c` versions of `docs/releases/v1.12.0.md`.

    pairs: 13
    FORWARD  old -> 13 replacements == new : True
    REVERSE  new -> 13 reversed     == old : True
    CONTROL reverse on new with 1 char changed outside edits == old : False
    CONTROL forward on old with 1 char changed outside edits == new : False

The controls changed "and opens on Windows from source" -> "...source!" (new) and "This release
ships with known defects, on purpose." -> "...purpose!" (old). I also read
`git diff 02a4622 5a1543c -- docs/releases/v1.12.0.md` hunk by hunk. It has 10 hunks, and every
changed line belongs to one of the 13 listed replacements. `git diff --stat` shows only the notes
and the handoff.

## ASCII, attribution, identity (lines this PR adds: 349)

- Non-ASCII: **0**. Control: a string with one e-acute is counted as 1.
- Attribution (the four banned strings from the project rules, case-insensitive): 2 hits, both in the handoff, both
  the filename `CLAUDE.md`. The allowed case. 0 hits in the notes.
- Identity (owner name, domains, `/Users/`, `/home/`, machine names): the only hits are
  `thefrederiksen/agenteyes-app` (the repository's own URL) and the handoff describing its own
  grep. No email, home path or machine name. Control: `/Users/x` is counted as 1. The 3 hits in
  `docs/cencon/proof/issue-142` belong to #173 and are out of scope.

## Tests - run by QA, and the 284 vs 430 count explained

`AgentEyes.sln` cannot build on a Mac. SDK 10.0.403, `DOTNET_ROLL_FORWARD=Major` (net8.0 tests
on the .NET 10 runtime, because there is no .NET 8 runtime here). At `5a1543c`:

    tests/AgentEyes.Tests.Mac        Passed! - Failed: 0, Passed: 430, Skipped: 2, Total: 432
    tests/AgentEyes.Tests.DesktopApp Passed! - Failed: 0, Passed:  23, Skipped: 0, Total:  23

**The discrepancy, resolved by measurement.** CLAUDE.md's "284" was written at `17c244d` (#151),
and `git log -S` finds that commit. The Mac test project links shared test files
(`../Shared/*.cs`), and #156 and #113 added files to it after that commit. I ran the same suite at
three commits in a scratch worktree:

| Commit | Mac suite total |
|--------|-----------------|
| `17c244d` (#151, where CLAUDE.md's figure was written) | **284** (283 passed, 1 skipped) - matches CLAUDE.md |
| `d0d0a27` (#156 merged) | 385 (383 + 2 skipped) |
| `b0e6ae8` (#113 merged) | **432** (430 + 2 skipped) |
| `5a1543c` (this PR) | 432 - this PR changes no code |

So 430/432 is correct. CLAUDE.md's 284 is out of date. That is not this PR's defect, but the lead
may want it updated.

`/status` was `idle` before the runs. No service, launchd agent, recording or ffmpeg was touched.

## Second Windows observation?

**Not needed for these steps.** Screenshots 06, 07 and 01 show each screen the steps describe, in
order: "Don't run" only -> "More info" -> "Run anyway". Nothing in the steps is unrecorded. Only one
thing was not observed: a real browser download. The seat simulated the browser's Zone.Identifier.
A real Edge or Chrome download, double-clicked, would close that gap. It is optional, not required
before publishing.
