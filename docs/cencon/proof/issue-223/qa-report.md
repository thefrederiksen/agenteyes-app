# #223 / PR #224 - QA report

QA seat, macOS host, 2026-11-24. Branch `issue-223-release-figure-reference-points`, head `1b89cff`,
on top of main `d38141c` (`git merge-base --is-ancestor origin/main HEAD` true). Docs only: the one
content change is `docs/releases/v1.12.0.md`, 11 lines in, 4 out.

The developer's report was not taken on trust. Every number below was re-derived here from the
committed files. Every search used `/usr/bin/grep` or Python (never the bare `grep`, which is a
ugrep wrapper in this shell), and every search ran per line AND on the joined text (all whitespace
collapsed to one space), with a planted positive control.

**Verdict: PASS - all five acceptance criteria met.** One finding about the ISSUE, not the fix, is
recorded under "The issue's premise" below, so the Review Gate sees it.

## Criterion 1 - the corrected figure is traced to a committed file: PASS

The page now gives Linux, from the `/record/start` response: 2.1 - 3.1 s on one take, no later than
3.1 - 4.1 s on three others. Source: `docs/cencon/proof/issue-142/qa-verify-pr190-linux.md` B1
table (lines 21-24). Re-derived from the raw lines (response line, last 0-byte sample, first sample
with bytes), `docs/cencon/proof/issue-142/linux/main-b0e6ae8/`:

| take | response | last 0 bytes | first bytes | from the response |
|---|---|---|---|---|
| row 4 | row4.txt:8 12.957 | row4.txt:30 16.057 | row4.txt:31 17.063 | 3.100 - 4.106 s |
| row 5 A | 10.504 | row5.txt:27 13.603 | row5.txt:28 14.608 | 3.099 - 4.104 s |
| row 5 B | row5.txt:174 47.917 | row5.txt:194 50.012 | row5.txt:195 51.017 | 2.095 - 3.100 s |
| row 6 | row6.txt:5 10.913 | row6.txt:24 11.014 | row6.txt:23 14.029 | 0.101 - 3.116 s |

Upper bounds 4.11 / 4.10 / 3.12 s and the one two-sided bracket 2.1 - 3.1 s: the page's wording
matches. The PR body quotes the row 5 B table line. 32 cores: `build-identity.txt:28`.

## Criterion 3 - the macOS 3.7 s reference point: PASS (established, from the request being SENT)

- `docs/cencon/proof/issue-142/macos/main-b0e6ae8/tools/emptywin.py:23`:
  `t_sent = time.time(); st = post("/record/start", ...); t0 = time.time()`. `post()` returns only
  after `urlopen` has the response, so `t0` is the response. Line 28 `el = time.time() - t0`, and
  line 30 prints `t+{el}`. So `t+` counts from the RESPONSE. Line 25 prints `t0 - t_sent` as
  "returned".
- `emptywin.txt:3` returned 0.802 s, `:5` t+ 2.888 s -> 0.802 + 2.888 = 3.690 s.
  `emptywin.txt:16` returned 0.800 s, `:18` t+ 2.909 s -> 0.800 + 2.909 = 3.709 s.
- So 3.7 s is from the request being sent; 2.9 s is from the response. The page states both, each
  labelled, with the 0.8 s request time. Arithmetic confirmed. Precision: the poll interval is 50 ms
  (`emptywin.py:41`), and each take's previous printed sample is t+0.000 with size 0.

## The issue's premise - recorded for the Review Gate

#223 says "a reader comparing 3.7 s on a Mac with 4-5 s on Linux is comparing two different clocks".
**That premise is false.** Both old figures were measured from (about) the request being sent:

- Mac 3.7 s: from `t_sent`, the moment before the POST (above).
- Linux "4-5 s": the harness zero is sample time minus its `t=+` value, e.g. row 6
  16:15:14.029 - 4.90 = 16:15:09.13, which is the `ffmpeg before` probe line (row6.txt:4,
  16:15:09.130), taken just before the POST. `qa-verify-pr190-linux.md:26-30` gives the same
  (zero within 0.03 s of each `ffmpeg before` line; request round trip 0.62 - 1.78 s).
  The Linux zero is a few tens of ms earlier than the Mac's, which is immaterial at this precision.

So the old page compared two figures on the SAME clock. The real defect was narrower: (a) neither
figure stated its reference point, and (b) the Linux figure's basis had been withdrawn in #190
because that report had labelled it as measured from the response. The fix addresses exactly that:
it restates both on the response, names it, and keeps the Mac send-referenced 3.7 s labelled. The
fix does not depend on the false premise, so this does not fail the PR, but the issue overstates
the original defect.

## Criterion 2 - every remaining start-up / empty-window figure names its reference point: PASS

Start-up terms swept in both forms (`empty window|start-?up|first bytes?|first fragment|delay|never
started|nothing on disk|first seconds`): line 11, joined 11 hits, each read in context. Figures:

| figure | treatment on the page | checked against |
|---|---|---|
| 2.9 s, 2.888 s, 2.909 s (Mac) | from the response | emptywin.txt:5,18 |
| 2.1 - 3.1 s, 3.1 - 4.1 s (Linux) | from the response | table above |
| 3.7 s, 0.8 s (Mac) | from the request being sent | emptywin.txt:3,16 |
| 51.7 s, 30.8 s, 7.7 s, 30 s | #113 review measurement, start point not recorded, not comparable | #113 comment 6929032227: "Time to first bytes ... on a live-paced source", table rows 5/auto 7.7 s, 1/auto 30.6 - 30.8 s, 1/36 51.7 s; it is a synthetic ffmpeg, not a `/record/start` call, and names no start point |
| 45-second / 45 s | configured limit, not a measurement | `CaptureWatchdog.DefaultStartupTimeout` |

Non-blocking note: "about 6 s at 5 fps" (the loss on an unclean end, one fragment plus the encoder's
start-up delay) is an amount of content lost, not a time from a start event, so it has no reference
point to name, and the developer's classification is reasonable. Its source is not traced in this PR.

## Criterion 4 - the sweep: PASS

- `python3 -I docs/cencon/proof/issue-223/sweep.py docs/releases/v1.12.0.md`: line 47, joined 47
  (main's file: 43 / 43). Matches the committed listing.
- Positive control, run here: a copy with `12.3\ns` (wrapped), `45.6 s` (one line) and `4-5\n  s`
  (wrapped) appended. sweep.py: line **48**, joined **50** - the line form missed both wrapped
  plants, the joined form found all three. `/usr/bin/grep -cE '4-5 s'`: line 0, joined 1 on the
  control, line 0, joined 0 on the real file. The control file was deleted.
- Withdrawn figures on the final file, both forms: `4-5 s` 0/0, `3-5 s` 0/0, `2-5 s` 0/0,
  `2 to 5` 0/0. `two seconds` 1/1 (the quoted, never-measured code comment, kept on purpose).
- Independent check of the regex's coverage: every decimal NOT followed by a time unit, both forms,
  8 / 8: 8.0 (SDK), 24.04 (Ubuntu), 2.1 and 3.1 (low ends of ranges in hits #25/#26), 44.1 (kHz),
  0.487 / 0.095 / 0.614 (first of each row-5 offset pair). None is a missed time figure. Unit
  abbreviations (`sec|min|h|hrs`) 0 / 0.
- Classification spot-checked: #19-#30 (start-up), #18/#42 (45 s limit), #31-#38 (audio/video
  lengths), #44-#46 (A/V offsets), #15-#17 (after the stop request) - all as the handoff says.
  #4-#13 (#159 "wait" figures): `qa-report-macos.md:283` confirms 13.68 s is a mux duration from the
  product's start/done log lines. The developer reported this as out of scope and did not fix it;
  that is right for #223's scope, and it should get its own issue.

## Criterion 5 - published body vs main: PASS

`gh api repos/thefrederiksen/agenteyes-app/releases/tags/v1.12.0`, `.body` raw string, compared in
Python with `git show origin/main:docs/releases/v1.12.0.md` (main = `d38141c`, last change to the
file `78011d5`): both 24,906 chars, `==` True, no CR. Published body vs this branch's file: 4 lines
out, 11 in - exactly the PR's diff. A refresh from the merged file carries only this fix, provided
nothing else touches the file before the refresh; re-run the same comparison at refresh time.

## Gate (macOS host, dotnet SDK 10.0.403)

- `dotnet test tests/AgentEyes.Tests.Mac -c Release`: Passed 433, Failed 0, Skipped 2, Total 435
- `dotnet test tests/AgentEyes.Tests.Linux -c Release`: Passed 474, Failed 0, Skipped 3, Total 477

Both match the developer's counts. `AgentEyes.sln` was not built: it includes the WPF app and does
not build on a Mac; no code changed. Non-ASCII characters in the changed file: 0. No runtime
surface, so no smoke applies. CenCon drift: none; privacy posture untouched.

**VERIFIED - all acceptance criteria met.** Labelled `flow:ready-gate` for the Review Gate (D7).
QA does not merge.
