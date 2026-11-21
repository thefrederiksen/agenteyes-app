# PR #190 (issue #142, Linux rows 4-6) - QA verification after the Review Gate REJECT on 5cefff8

**Result: PASS.** Head verified: `8c92fc5`. Docs only. QA seat on devlinux, 2026-11-20 22:00 - 22:45 EST.
QA does not merge: this sets `flow:ready-gate` for the Review Gate (D7).

Method: every figure below was derived again from the raw evidence files (`linux/main-b0e6ae8/row4.txt`,
`row5.txt`, `row6.txt`, `service-log-excerpt.txt`, `full-decode-rerun.txt`, `row4-probe-rerun.txt`,
`build-identity.txt`, `install-main-b0e6ae8.txt`). I did not check them against the gate's verdict or the
developer's comment. Those were used only to know what to look for. QA's own new evidence is in
`linux/main-b0e6ae8/qa-verify-pr190/`.

## B1 - the empty window: FIXED

Expected: every number names its reference point, and the sentence contradicting the published figure is
gone or restated correctly.

Derived here. The harness zero is (sample time - its `t=+` value). The response time is the
`POST /record/start -> 200` line.

| take | harness zero | response | last 0-byte sample | first sample with bytes | from the response | report says |
|---|---|---|---|---|---|---|
| row 4 | 15:59:13.044 - 0.7 = 12.34 | 12.957 | 16.057 (`/status`) | 17.063 (`/status`) | 3.10 - 4.11 s | no later than 4.11 s |
| row 5 A | 16:10:10.590 - 1.3 = 09.29 | 10.504 | 13.603 (`/status`) | 14.608 (`/status`) | 3.10 - 4.10 s | no later than 4.10 s |
| row 5 B | 16:12:48.005 - 1.3 = 46.71 | 47.917 | 50.012 (`stat` raw=0) | 51.017 (`stat` 306407) | 2.10 - 3.10 s | 2.10 to 3.10 s |
| row 6 | 16:15:14.029 - 4.90 = 09.13 | 10.913 | 11.014 (`stat` raw=0) | 14.029 (`stat` 331716) | 0.10 - 3.12 s | no later than 3.12 s |

- Every figure agrees. The from-the-request column (+4.7, +5.3, +3.3 to +4.31, +4.90) also agrees. So does
  the request round trip of 0.6 - 1.8 s (0.62 / 1.21 / 1.21 / 1.78 s). The zero is 0.10 - 0.17 s before
  each `[CaptureLock] TryAcquire` line (the report says 0.09 - 0.17 s, inside its stated +/- 0.05 s).
  The zero also coincides with each `ffmpeg before` line to within 0.03 s.
- The developer's correction to the gate on row 5 B is right. The sample one second before the gate's 3.10 s
  read `raw=0`, so the bracket is 2.10 - 3.10 s.
- The table now has a column per reference. The prose labels `/status` readings as upper bounds only, with
  the reason (row 5 B: `stat` 306,407 while `/status` read 0).
- The sentence "That is not the 2-5 s figure the lead published" is withdrawn by name. The new conclusion
  says "consistent with ... They do not contradict it", which is no stronger than the evidence: only
  row 5 B gives a meaningful lower bound.

## B2 - row 4's frame near the end: FIXED (remedy: re-run and commit)

- `row4-probe-rerun.txt` is committed. It covers the same file (1,958,629 bytes, as in `row4.txt`). It shows
  30 frames at or after 24.70 s, first 24.700, last 25.666667, keyframes 0 / 8.333 / 16.667 / 25.000,
  and 271 unfiltered frames starting at 16.666667.
- **Re-derived by QA, read-only** (`qa-verify-pr190/row4-probe-qa.txt`). sha256
  `edf952a9...b0544` matches the rerun file. Keyframes, 271 / 16.666667 and 30 / 24.700000 / 25.666667
  are all identical, and the full decode has 0 bytes of stderr.
- The row now quotes `row4.txt`'s conflicting 16.666667 and explains it. It says outright that the
  explanation was reproduced, not proven to be the 2026-11-16 harness's own command. That is the right
  strength.
- The image `row4-near-end-frame-appwindow.png` was opened and reads `REC 00:24`.

## Test suite: counts match

| | Passed | Failed | Skipped | Total | tree |
|---|---|---|---|---|---|
| committed `test-gate-linux.txt` | 472 | 0 | 2 | 474 | 5cefff8 |
| **QA run** (`qa-verify-pr190/test-gate-qa.txt`) | **472** | **0** | **2** | **474** | 8c92fc5 |

Both runs skipped the same two tests (`OwnedFfmpegTests.KillOwned_FailurePath_...` and
`ProcessIdentityTests.PackageCli_SandboxedProberLiveWriter_...`). The build had 0 errors. `AgentEyes.sln`
cannot build on Linux because it includes the WPF app. The PR changes no code
(`git diff b0e6ae8 8c92fc5` touches only `docs/cencon/proof/issue-142/`).

## The sweep: every number against a committed file

Scope: the whole 2026-11-16 RE-RUN section of `qa-report-linux.md` (lines 34-305), the part this PR
adds.

Method:

1. **Distinct numeric tokens:** 215 were extracted (`grep -oE '[0-9][0-9,]*(\.[0-9]+)?'`, sorted and
   unique).
2. **Literal search:** each token was searched in `linux/main-b0e6ae8/`, with and without thousands
   commas. The search discriminates, because it returned 24 misses and not 0.
3. **The 24 misses:** each was traced by hand.
4. **Context check:** for the table figures, the literal hits were also checked in context. This covers
   packets, durations, frame counts, mux times, dup/drop, atom offsets, load ranges (recomputed min/max
   per take: 5.80-6.71, 4.39-10.27, 5.80-7.63 and 2.19-24.59 over 31 samples) and liveness counts
   (4 / 600 / 2).

What the 24 misses are:

| class | count | tokens | check |
|---|---|---|---|
| arithmetic on committed values | 14 | 0.028, 0.080, 0.095, 100.587, 100.672, 610.539 (packets x 1024 / 48000 and differences); 12.34, 46.71 (harness zeros); 19.69, 38.16 (response minus round trip); 2.10, 3.10, 3.12, 4.10 (brackets) | each recomputed and correct |
| rounding of committed values | 5 | 100.667, 100.767, 610.567, 16.667, 25.667 | correct |
| derived offset | 1 | 0.09 | actual 0.098 - 0.166, within the stated +/- 0.05 s |
| in another committed file | 1 | 2.373 | `linux/row6.txt:169` (the f9ef230 run) |
| issue number | 1 | 184 | not a measurement |
| response-to-response wall time | 1 | 25.53 | 15:59:38.484 - 15:59:12.957 = 25.527 |
| library directory name | 1 | 102532 | see N3 |

All six images were opened. They show `REC 00:24`, `REC 01:39` twice, "No microphone" on both row 5
frames (O3), the Ubuntu 26.04.1 upgrade dialog over the row 6 frame (O4), and the camera clock at
16:15:40 and 16:25:19.

Conclusions checked against their evidence:

- Row 5's limit (#157 not exercised) is stated in the verdict row and again under the row.
- Row 6 says the CAPTURE FAULT / Abandon absence "does not carry the verdict". It has a positive control,
  13 / 26 hits in the 2026-11-14 log.
- The truncation control is limited to "two files at one truncation point".
- The empty-window conclusion is "consistent with", not "confirms".

**Defects found by the sweep: 0 blocking.** Non-blocking notes:

- **N1 (outside this PR, for #197).** `docs/releases/v1.12.0.md:168-169` on main says the empty window was
  "4-5 s on a 32-core Linux machine". That is this report's withdrawn figure, measured from the request.
  Measured from the response, the report now gives no later than 3.1 - 4.1 s. The release-notes sentence
  states no reference point, and it sits beside the Mac's 3.7 s, whose reference point it also does not
  state. This PR does not touch that file, so the item is not blocking here.
- **N2.** "the 2-5 s window the lead published" cites no source. The figure is in the lead's comments on
  #113, #142 and #188. The published release notes say 3-5 s.
- **N3.** The claim that the four directories and `2026-11-14_102532_video` were present on 2026-11-20
  had no committed file behind it. QA confirmed it on disk at 22:34 EST (`row4-probe-qa.txt`).
- **N4.** "The only committed lower bound on the disk is row 5 B's 2.10 s" is not quite right. Row 6's
  committed `raw=0` at 16:15:11.014 is also one, at 0.10 s. It is weaker, so the conclusion stands.
- **N5.** `test-gate-linux.txt` names tree 5cefff8, not the head 8c92fc5. The code is identical, and
  QA's run on 8c92fc5 matches it.

## Branch state against main

- The branch's merge base is `b0e6ae8`, so it is **7 commits behind** main `78011d5`. It was not rebased.
- **No file overlap.** The PR changes 16 files and main changed 779 since `b0e6ae8`. The intersection is
  empty (`comm -12`). The `issue-142` files main touched are under `earlier-c80cb9d/`, `macos*/` and
  `windows/`, and in the macOS and Windows reports. None is in this PR.
- **Trial merge** of main into 8c92fc5 (not committed, then aborted): clean.
- **Identity guard on the merged tree:** main's `scripts/check-proof-identity.py` reported `[OK]` over 1725
  files. The PR branch predates the guard, so its own CI does not run it. The same guard run on the
  unmerged branch tree goes red. Every hit is in pre-#173 `issue-113` evidence that main has already
  scrubbed and this PR does not touch. That red run is also a live positive control for the guard.
- **QA's own multi-form search of the 14 PR text files**, for an address or a home path (plain, newlines
  deleted, newlines to spaces): 0 hits. Its positive control (a planted wrapped `/ho` + `me/x`, and a
  wrapped address) hit.

## Identity and banned strings

- The 14 PR text files, plus this report and QA's two evidence files, were searched for the banned
  attribution strings. Four forms were used: plain, newlines deleted, newlines to spaces, and backslashes
  collapsed. Result: 0 hits. Positive controls (a mid-word wrap and a word wrap) each hit once.
- Non-ASCII: 0 files.
- The PR's commit messages and body: 0 hits.

## What QA did not do

- QA re-made no recording. Its only reads of the library were the row 4 probe and directory listings.
- QA killed no process and deleted nothing.
- QA did not re-run rows 5 and 6 against the library files.
