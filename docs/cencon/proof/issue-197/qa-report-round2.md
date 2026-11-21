# Issue #197 - QA round 2 report

**Verdict: PASS -> flow:ready-gate.** Criterion 3 (the live page refreshed) is not met yet. By the owner's
instruction the lead publishes the body after the Review Gate merges, so the gate and the lead must close it
(see "Criterion 3" below).

- PR #201, branch `issue-197-release-notes-corrections`, head under review `23a0385`.
- Main at review time: `4cb0fad`.
- QA seat: macOS, .NET SDK 10.0.403, `DOTNET_ROLL_FORWARD=Major`.

## 1. The round 1 defect - #159 figures taken from the raw rows

Source: `docs/cencon/proof/issue-193/linux-install/take.txt` on branch `issue-193-linux-install-qa` (PR #198),
read with `git show`. These are the rows, verbatim. The file list is cut down to the files that matter here.

```
49  18:22:02.118 POST /record/stop  (dir=~/Videos/AgentEyes/2026-11-16_182129_video)
52    "File": "~/Videos/AgentEyes/2026-11-16_182129_video/recording.mp4",
56  18:22:02.750 stop+1s status=[idle False] manifest.mux=[running] files=[... raw.mp4 raw.mp4.ffmpeg.log shots ]
57  18:22:03.825 stop+2s status=[idle False] manifest.mux=[running] files=[audio_processed.wav ... raw.mp4 ... shots ]
58  18:22:04.898 stop+3s status=[idle False] manifest.mux=[running] files=[... recording.mp4.muxing.tmp shots ]
59  18:22:05.974 stop+4s status=[idle False] manifest.mux=[done] files=[... recording.mp4 recording.original.mp4 ... ]
```

The `stopN s` labels count loop passes. They are not time offsets, so all the arithmetic uses the timestamps:

| Claim in the corrected sentence | Arithmetic from the timestamps | Result |
|---|---|---|
| "still missing 2.8 s after the stop request" | last row without `recording.mp4` is 04.898. 04.898 - 02.118 = 2.780 s | [OK] 2.8 |
| "by which time /status had read idle for 2.1 s" | first row reading `idle` is 02.750. 04.898 - 02.750 = 2.148 s | [OK] 2.1 |
| "(it was there 1.1 s later)" | first row with `recording.mp4` is 05.974. 05.974 - 04.898 = 1.076 s | [OK] 1.1 |
| "the `File` path that `/record/stop` returned did not exist yet" | line 52 returns `.../recording.mp4`. It is absent from rows 56-58 | [OK] |
| "at first there is not even a temporary file" | no `.muxing.tmp` in rows 56-57. It first appears in row 58 | [OK] |

The round 1 error is gone: `grep -c "3.9 s" docs/releases/v1.12.0.md` returns 0. To show that grep can find
the string, the same grep on the round 1 head `4c27852` returns 1.

Not a defect, just a note: "`/status` goes to `idle` at once" in the same entry fits the first read at
+0.63 s. "only the raw capture" fits row 56. Row 57 already has `audio_processed.wav`, and "at first" covers that.

## 2. The branch against main

- Merge-base `02a4622`. Main `4cb0fad` is **NOT** an ancestor of the branch: 5 main commits are missing
  (`ffa737b`, `befabfb`, `da85516`, `5e6e727`, `4cb0fad`).
- **None of them touches `docs/releases/` or `docs/cencon/proof/issue-197/`.** `git diff --stat 02a4622 origin/main`
  on those paths is empty, while the same command over the whole repository lists 767 files.
- I made a trial merge of the branch into `4cb0fad` in a scratch worktree. It merged with no conflict, and the
  staged result is exactly the branch's 3 files (+572/-21). Nothing from main is dropped.
- **The stale branch caused a real problem, and I fixed it in this commit.** Main's new #173 guard
  (`scripts/check-proof-identity.py`, a CI job since `4cb0fad`) went **red on the merged tree**:
  `docs/cencon/proof/issue-197/qa-report.md:138: home path naming user x***`. That line is in round 1's QA
  report, so it is a QA file, not the developer's. It wrote a literal `/Users/` path with a one-letter name to
  describe its grep control. It is not a real identity, but after the gate's merge it would turn main's CI
  `evidence-identity` job red. I reworded that one line so it gives no literal path. After the fix the merged tree
  is **green** (section 7). I did not touch any developer file.
- Advice for the gate: there is nothing to rebase. Merging main into the branch first is optional, because
  the squash result is the same either way.

## 3. Every difference between the published body and the branch file

The published body comes from `gh release view v1.12.0 --json body`, and `gh api .../releases/tags/v1.12.0`
returns the same bytes. Compared with the file at `02a4622`, the only difference is the trailing newline that
`--jq` adds. The release's `updated_at` is `2026-11-16T23:17:29Z`. **So `gh release edit` has not been run
since publication.** The developer did not run it, which is correct.

`diff -U0 published branch` gives 15 hunks. Each one is matched to its #197 item:

| # | Published line | Change | #197 item | Verdict |
|---|---|---|---|---|
| 1 | 3 | "Prepared ... for issue #193" -> "Released 16 November 2026" | Windows item 4: re-read as the downloader | intended |
| 2 | 18 | table cell: "SmartScreen will show Unknown publisher" -> "Windows warns ... Section 2 says exactly what to click" | Windows item 1 | intended |
| 3 | 31-36 | maintainer paragraph and `gh release edit` command deleted | Windows item 4, Linux item 4 | intended |
| 4 | 44-45 | "choose to run it anyway" sentence removed | Windows items 1-2 | intended |
| 5 | +40-52 | SmartScreen click path and Smart App Control (untested) | Windows items 1, 2, 3 | intended. Checked in section 4 |
| 6 | 57 | Linux: Wayland, xrandr, libvlc as root, sign-in `[FAIL]` | Linux item 2 | intended |
| 7 | +78-80 | "Updating stops any recording ... kept, but that has not been tested" | Linux item 2 | intended. It does not claim "loses the take" |
| 8 | 90 | "QA or developer seat" -> "tester or developer" | downloader pass | intended |
| 9 | 104 | #157: the gap-closing sentence now marked "shown on Linux only". macOS: "read, not tested" | body item 1 | intended. The macOS clause comes from gate comment 6932209492: "On macOS the seat placed the loss at the capture pipe, by reading the command line" |
| 10 | 109-110 | #159: "only `.muxing.tmp` on disk" -> not on disk; Linux has no temp file at first | Linux item 1 | intended. Matches rows 56-58 |
| 11 | 114-115 | #159 Linux figures. Windows half kept, with "not looked for there" | Linux item 1 + round 1 defect | intended. Checked in section 1 |
| 12 | +147 | "Waiting on the temporary file does not work" | Linux item 1 | intended. Follows from rows 56-57 |
| 13 | 224 | "repository variable is unset" -> "not signed yet" | downloader pass | intended |
| 14 | 248 | #165: systemd kills the service's capture, marked "inferred ... not measured" | Linux item 3 | intended. "A few seconds" matches #165's "2 to 4 seconds" |
| 15 | 329-330 | "found #186 and #187" -> #186 found running; #187 found by reading code, not run | body item 2 | intended |

**15 of 15 accounted for, and none is a defect.** All other lines are byte-identical to the published body.
Round 1 verified the file at `4c27852`. Since then, `git diff 4c27852 23a0385 -- docs/releases/v1.12.0.md`
touches exactly 2 hunks: the #159 sentence and the "More info" clause, which are round 1's two defects.

## 4. The SmartScreen passage, checked against the screenshots

I read these screenshots myself from PR #199 (branch `issue-193-windows-install-qa`):

- `06-smartscreen-windows-protected.png`: title "Windows protected your PC". The only button is
  **"Don't run"**, plus the window's close X. **"More info"** is small, underlined text under the message.
- `07-smartscreen-more-info-run-anyway.png`: after clicking More info, it shows "App: AgentEyes-Setup-win-x64.exe",
  "Publisher: Unknown publisher", and buttons **"Run anyway"** and "Don't run".
- `01-open-file-security-warning.png`: "Open File - Security Warning", "The publisher could not be
  verified", and buttons "Run" and "Cancel".

The passage says the window's only button is "Don't run". It tells the reader not to click it, and to click
"More info", described as "a small underlined link, easy to miss". Then "Publisher: Unknown publisher" and
"Run anyway" appear, and the reader clicks "Run anyway". **Every named control exists in a screenshot, and
the passage promises no button that is not shown. "More info" is presented as the only way through the
first screen.** The passage marks Smart App Control as untested and only "expected" to block. The phrase
"choose to run it anyway" is gone: 0 hits on the branch, 1 in the published body.

Not covered: I am on a Mac, so this is checked against the Windows seat's recorded screenshots. I did not
see it happen again myself.

## 5. No version, tag, code or test change

- `git diff --name-only 02a4622 23a0385` changes only files under `docs/`, and there are 0 others. Control: the
  same filter against `origin/main` counts 75 non-docs files.
- None of the 15 hunks changes a version string. `v1.11.11` and `v1.12.0` appear unchanged on both sides.
- Tag `v1.12.0` still points to `02a4622`. `gh release list` shows no release newer than v1.12.0.

## 6. Tests (QA ran them, on main `4cb0fad` + this branch merged)

- `dotnet test tests/AgentEyes.Tests.Mac`: **433 passed, 2 skipped, 0 failed (435)**.
- `dotnet test tests/AgentEyes.Tests.DesktopApp`: **23 passed, 0 failed**.

The change touches documentation only, so no app smoke applies.

## 7. ASCII, attribution, identity

- Non-ASCII lines: 0 in the notes and in this report. Control: a string containing one e-acute counts as 1.
- Attribution strings: the only hit in the notes is the filename `CLAUDE.md` in the history table, which the
  published body already contains. That is the allowed case.
- Identity guard (`check-proof-identity.py` from main): **green** on the merged tree after the line 138 fix,
  and red before it. Control: a planted address in `issue-197/` is flagged by file and line.
- Because of #218, I also ran a manual search over this report and the edited line in three forms: plain,
  with line breaks removed, and with backslashes doubled. The search covered email shapes, home paths, and the
  owner's name, domain and machine names. Result: 0 hits in each form. Control: I planted a copy with a
  Windows home path, a Unix home path and an address wrapped across two lines. The plain search found 2, the
  joined search found 3 (only the joined form catches the wrapped address), and the doubled search found 2.

## Criterion 3

"The published release body refreshed, shown by fetching it back" **cannot pass until the lead runs
`gh release edit`, which the owner keeps for the lead alone.** Today the live page still has the old
SmartScreen sentence (section 3). The gate should treat criterion 3 as a step after the merge, and someone
should fetch the body back after publishing and compare it with the merged file.

## Criteria summary

| AC | Result |
|---|---|
| 1 Both sentences, before and after | [OK] hunks 9 and 15 |
| 2 Other entries re-read | [OK] the developer reports nothing to change. My check of the hunks found nothing new |
| 3 Live body refreshed | PENDING. Reserved to the lead, after the merge |
| 4 Nothing else changes | [OK] 15 of 15 hunks accounted for |
| 5 No version, tag or release | [OK] |
