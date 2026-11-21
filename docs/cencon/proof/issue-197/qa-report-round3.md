# Issue #197 - QA round 3 report (Review Gate bounce 1)

**Verdict: PASS -> flow:ready-gate.** The gate's blocking defect is fixed: the Wayland bullet no
longer promises a login option the reader may not find. The three non-blocking edits are true to
the evidence. Nothing else changed. Criterion 3 (refreshing the live page) is still the lead's
step after the gate merges.

- PR #201, branch `issue-197-release-notes-corrections`, head under review `e4b732c`.
- Gate-reviewed head: `e109b3f`. Main at review time: `4cb0fad`.
- QA seat: macOS, .NET SDK 10, `DOTNET_ROLL_FORWARD=Major` (the seat has no 8.0 runtime).
- Narrow round, as the lead asked. Not redone: the #159 figures, the SmartScreen passage against
  the screenshots, the 15 hunks against the live body, the identity sweep of earlier text, CI history.

## 0. Scope - what changed since the gate looked

`git diff --stat e109b3f e4b732c`:

    docs/cencon/proof/issue-197/handoff.md | 84 +++++++++-
    docs/releases/v1.12.0.md               | 17 ++++---

The diff to `docs/releases/v1.12.0.md` has exactly four hunks: the SmartScreen paragraph (line 47),
the Wayland bullet (line 69), the #159 "seen on" sentence (line 140) and the #159 temporary-file
sentence (line 151). There are no other changes. [OK]

## 1. The Wayland bullet - the gate's blocking defect

New text (`docs/releases/v1.12.0.md:69-73`):

    - **Refuses to run on Wayland.** AgentEyes on Linux records X11 only, and the script stops if the
      session is Wayland. Ubuntu logs in to Wayland by default. The test machine runs Ubuntu 24.04,
      where an X11 session, "Ubuntu on Xorg", is installed. Newer Ubuntu releases do not install an
      X11 session by default, so that choice may not be on your login screen, and AgentEyes has not
      been tested on them. The refusal was read from the script, not seen: the test machine runs X11.

| Check | Expected | Actual | Result |
|---|---|---|---|
| Handoff commits the test machine's release, read from the machine | `/etc/os-release` and `/usr/share/xsessions/` output in the repo | `handoff.md:21-33` commits `PRETTY_NAME="Ubuntu 24.04.5 LTS"`, `VERSION_ID="24.04"`, `ls /usr/share/xsessions/` -> `ubuntu.desktop  ubuntu-xorg.desktop`, `Name=Ubuntu on Xorg`, and `loginctl ... Type=x11`. Dated 20 November 2026, host label omitted | [OK] |
| "24.04" and "'Ubuntu on Xorg' is installed" rest on committed lines | yes | they rest on `VERSION_ID` and `ubuntu-xorg.desktop` above | [OK] |
| "the test machine runs X11" rests on a committed line | yes | `Type=x11` | [OK] |
| Does the bullet promise a login option the reader may not find? | no | it gives no instruction to choose anything. It says what the test machine has and warns that the choice "may not be on your login screen" on newer releases | [OK] - defect fixed |
| No stronger claim (that the login manager REMOVED the option) anywhere | absent | `grep -niE "remov|gdm|login manager|25\.10|26\.04|drop"` over both files: no hit in the release notes; the only handoff hit (`handoff.md:40`) says the session packages were dropped "from the default install (they remain in the repositories)". That is the packages mechanism, not the removal claim | [OK] |
| "Ubuntu on Xorg" appears nowhere else in the notes | one mention | `grep -n Xorg` -> line 71 only | [OK] |

**The judgment call: the sentence "Newer Ubuntu releases do not install an X11 session by default".**
My opinion: **acceptable.** Its provenance is recorded honestly.

- The handoff (`handoff.md:36-42`) says what the sentence rests on: "the lead's reading that Ubuntu
  25.10 dropped the X11 session packages from the default install". It also says the developer did
  not verify it, that no newer release was looked at, and that the sentence therefore names no
  release number. That is an honest provenance line. It does not claim a measurement it does not have.
- The sentence is hedged where it matters to the reader ("may not be on your login screen") and is
  paired with "AgentEyes has not been tested on them", so it does not overstate.
- I checked public reporting independently on 20 November 2026 (a web search, nothing installed).
  The Ubuntu Discourse announcement "Ubuntu 25.10 drops support for GNOME on Xorg" and press
  coverage (OMG! Ubuntu, Ubuntu Handbook, It's FOSS) agree that 25.10's default desktop session
  is Wayland-only and the X11 session is not in the default install. Kubuntu made the same change,
  with the Plasma X11 session available as a separate package. The unhedged half of the sentence,
  "do not install ... by default", matches that reporting. The search did not confirm exact package
  names, and the notes name none.
- Leaving it out would make advice that only holds for 24.04 read as universal, which is the
  defect being fixed. Stating it with its source labelled is the right trade.
- Optional, not a bounce: a later round could cite the Ubuntu Discourse announcement in the
  handoff, so that the provenance points at a public source and not only at "the lead's reading".

## 2. SmartScreen - "A real browser download was not measured"

Source: PR #199, `docs/cencon/proof/issue-193/qa-report-windows-install.md` at `69462d0`, lines 29
and 94-96. Every asset was fetched with `curl -L`. "`curl` does not add a Mark-of-the-Web ... I put the
same Zone.Identifier a browser writes on a copy (`ZoneId=3`, HostUrl = the release URL)".

| Sentence | Evidence | Result |
|---|---|---|
| "A real browser download was not measured" | no browser download anywhere in the seat report | [OK] |
| "the test marked a terminal-downloaded copy the way a browser would" | curl copy plus a hand-written `ZoneId=3` Zone.Identifier | [OK] exact |
| "A browser may show its own warning about the file before any of this" | hedged ("may") and labelled unmeasured | [OK] |

## 3. #159 - "The early `idle` was seen on all three platforms"

`docs/releases/v1.12.0.md:137-151`. The same entry gives the evidence per platform:

- macOS: the missing file was looked for while the status read idle.
- Linux: "`/status` had read `idle` for 2.1 s" while `recording.mp4` was missing. The round 2
  report checked this against the raw rows.
- Windows: "the status read `idle` while the manifest still said the mux was running; the file
  itself was not looked for there."

On all three platforms the status read idle before the mux finished. So the narrowed claim, the
early idle, is supported everywhere. The claim it replaced, "seen", could be read as covering the
missing file, which was not looked for on Windows. [OK]

**Nit (not failed, as the lead asked):** line 140 is now 117 characters, longer than the file's
~100-character wrap. It renders fine.

## 4. Temporary file - scoped to Linux

"On Linux, waiting on the temporary file does not work, because it is not there at first. The
other platforms were not checked for this." The only evidence that the temporary file is absent at
first comes from the Linux rows (round 2: rows 56-57 show raw capture only, and
`recording.mp4.muxing.tmp` first appears at row 58). Neither the macOS nor the Windows evidence in
this entry speaks to the temporary file. The scoping is supported, and it only narrows the claim. [OK]

## 5. Files owned by #221 - not touched

`git diff --stat 02a4622 HEAD -- src/AgentEyes.Core/Platform/Linux/LinuxDisplay.cs scripts/linux/install.sh README.md`
-> empty. `git log 02a4622..HEAD -- src scripts README.md` -> 0 commits. The PR's whole file list
against its merge base is four files, all docs: `handoff.md`, `qa-report.md`,
`qa-report-round2.md`, `v1.12.0.md`. [OK]

## 6. The release was not edited

`gh api repos/thefrederiksen/agenteyes-app/releases/tags/v1.12.0` ->
`published_at 2026-11-16T23:17:01Z`, `updated_at 2026-11-16T23:17:29Z`. That is four days before
this round. The live body still contains "run it anyway" (1 hit). `gh release edit` has not been
run. [OK]

## 7. Branch currency - NOT current with main, but harmless

The lead asked me to confirm the branch is current with main `4cb0fad`. **It is not.** The merge
base is `02a4622`, and the branch is 5 commits behind: `ffa737b`, `befabfb`, `da85516`, `5e6e727`
and `4cb0fad`. None of them touch a file this PR changes. They touch `README.md` and
`scripts/linux/install.sh`, among others, and this PR does not. GitHub reports `MERGEABLE`, so
the squash-merge carries no conflict.

The CI run on head `e4b732c` (run 44857948560) was triggered by `pull_request`, which tests
GitHub's merge of the PR with main. That merge already includes `4cb0fad`'s `evidence-identity`
guard, and the job **passed**. macOS and Linux passed. At writing, Windows is in progress (started
02:49 UTC). This is a docs-only diff, so the gate should glance at that job before merging.
I did not count the 5 commits behind as a defect. A rebase would add nothing and cost a round.

## 8. Tests run by QA

`DOTNET_ROLL_FORWARD=Major dotnet test tests/AgentEyes.Tests.Mac -c Release` on `e4b732c`:
`Passed! - Failed: 0, Passed: 430, Skipped: 2, Total: 432`. The diff contains no code. I did not
run the Windows solution gate on this macOS seat. CI covers it, and that job is still running (above).

## 9. Identity sweep of this report

I swept this file for the owner's address, the home-path username and the vendor names in three
forms (plain, line breaks removed, backslashes doubled), with a planted positive control first.
The control was found in every form. Real hits: 0.

## Result

VERIFIED: the gate's blocking defect is fixed, and the three non-blocking edits are supported.
Before merging, the gate should know two things: the branch is 5 commits behind main with no
overlap (section 7), and the Windows CI job was still running at writing. One open nit: the long
line 140.
