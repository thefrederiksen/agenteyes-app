# #223 - Developer handoff: reference points for the v1.12.0 empty-window figures

Branch `issue-223-release-figure-reference-points`, based on main `d38141c`. Docs only: one file
changes, `docs/releases/v1.12.0.md` (11 lines in, 4 out). No product code.

## What changed

1. Section 3, #188 entry: the 51.7 s and 30.8 s first-bytes figures now say that their measurement
   (the Review Gate's, in the review of #113) does not record where its clock started, so they are
   not comparable with the 30 fps empty window.
2. The 30 fps empty window is restated on one named reference point, the moment the
   `/record/start` request **returned**: about 2.9 s on the 10-core Mac (2.888 s and 2.909 s), and
   on the 32-core Linux machine 2.1 - 3.1 s on the one take bounded from both sides, no later than
   3.1 - 4.1 s on the other three. The withdrawn "4-5 s" (request-referenced) is gone.
3. The Mac 3.7 s is kept and labelled with its real reference point: measured from when the
   request was **sent**, and the request took 0.8 s.
4. The 7.7 s (5 fps) and about 30 s (1 fps) figures are marked as coming from the #113 review
   measurement and as not comparable with the 30 fps figures. "On the same 10-core Mac" became
   "on a 10-core Mac": nothing shows that the Review Gate's Mac and the macOS QA seat's Mac are
   the same machine.

## Criterion 1 - the corrected figures, traced to committed files

Linux, response-referenced. The brackets are in `docs/cencon/proof/issue-142/qa-verify-pr190-linux.md`
(commit `5d472be`), B1 table, "from the response" column:

    | row 4 | 15:59:13.044 - 0.7 = 12.34 | 12.957 | 16.057 (`/status`) | 17.063 (`/status`) | 3.10 - 4.11 s | no later than 4.11 s |
    | row 5 A | 16:10:10.590 - 1.3 = 09.29 | 10.504 | 13.603 (`/status`) | 14.608 (`/status`) | 3.10 - 4.10 s | no later than 4.10 s |
    | row 5 B | 16:12:48.005 - 1.3 = 46.71 | 47.917 | 50.012 (`stat` raw=0) | 51.017 (`stat` 306407) | 2.10 - 3.10 s | 2.10 to 3.10 s |
    | row 6 | 16:15:14.029 - 4.90 = 09.13 | 10.913 | 11.014 (`stat` raw=0) | 14.029 (`stat` 331716) | 0.10 - 3.12 s | no later than 3.12 s |

Re-checked here against the raw lines under `docs/cencon/proof/issue-142/linux/main-b0e6ae8/`:

    row4.txt:8    15:59:12.957 POST /record/start -> 200 {
    row4.txt:30   15:59:16.057 t=+    3.7 state=recording el=3.62 live=starting capbytes=0 raw=-1 load=5.89
    row4.txt:31   15:59:17.063 t=+    4.7 state=recording el=4.63 live=growing capbytes=221466 raw=-1 load=5.89
    row5.txt:6    16:10:10.504 POST /record/start -> 200 {
    row5.txt:27   16:10:13.603 t=+    4.3 file=raw.mp4 state=recording el=4.18 live=starting capbytes=0 raw=-1 load=4.68
    row5.txt:28   16:10:14.608 t=+    5.3 file=raw.mp4 state=recording el=5.18 live=growing capbytes=404891 raw=-1 load=4.68
    row5.txt:174  16:12:47.917 POST /record/start -> 200 {
    row5.txt:194  16:12:50.012 t=+    3.3 file=raw.mp4 state=recording el=3.14 live=starting capbytes=0 raw=0 load=6.77
    row5.txt:195  16:12:51.017 FIRST BYTES in raw.mp4 at +4.31s (306407)
    row6.txt:5    16:15:10.913 POST /record/start -> 200 {
    row6.txt:24   16:15:11.014 t=+    1.9 file=raw.mp4 state=recording el=1.72 live=starting capbytes=0 raw=0 load=20.03
    row6.txt:23   16:15:14.029 FIRST BYTES in raw.mp4 at +4.90s (331716)

So: 2.1 - 3.1 s (row 5 B), and upper bounds 4.11 / 4.10 / 3.12 s (rows 4, 5 A, 6), written in the page
as "no later than 3.1 to 4.1 s on three others". 32 cores: `linux/main-b0e6ae8/build-identity.txt:28`
`machine: Ubuntu 24.04.5 LTS, kernel 6.8.0-149-generic, 32 cores, 39 GB RAM, DISPLAY=:0`.

macOS: see criterion 3.

## Criterion 3 - the macOS 3.7 s reference point: ESTABLISHED, request-referenced

Source: `docs/cencon/proof/issue-142/qa-report-macos.md:1376-1378` (commit `befabfb`, #192):

    | 2 | 0.800 s | **2.909 s** | **3.71 s** |
    **About 3.7 s from the start command (2.9 s after the API returns), at 30 fps on 10 cores, libx264

The report's wording was not taken on trust. The tool that made the measurement is committed,
`docs/cencon/proof/issue-142/macos/main-b0e6ae8/tools/emptywin.py`:

    5   # Polls every 50 ms. Time zero = the moment the start POST returned (also prints the POST-sent time).
    23  t_sent = time.time(); st = post("/record/start", {...}); t0 = time.time()
    28      size, complete, _ = frags(f); el = time.time() - t0

and its raw output, `macos/main-b0e6ae8/emptywin.txt`:

    3   start POST sent -> returned 0.802 s; dir 2026-11-16_170048_video
    5     t+ 2.888s  size   1271421  complete mdat fragments 1
    16  start POST sent -> returned 0.800 s; dir 2026-11-16_170101_video
    18    t+ 2.909s  size   1311212  complete mdat fragments 1

`t+` is measured from the response (line 23: `t0` is taken after `post()` returns). 3.7 s =
0.802 + 2.888 = 3.690 and 0.800 + 2.909 = 3.709, i.e. **from when the request was sent**. That is
the same reference point as the withdrawn Linux "4-5 s". Response-referenced, the Mac figure is
2.888 / 2.909 s, at a 50 ms polling interval. The page now gives both, each labelled.

The 5 fps and 1 fps figures (7.7 s, about 30 s) and the #188 entry's 51.7 s / 30.8 s are not in any
committed file. Their source is the Review Gate's comment on #113,
https://github.com/thefrederiksen/agenteyes-app/issues/113#issuecomment-6929032227 (quoted in #188):
"Time to first bytes with the product's encoder and container options on a live-paced source", with
a table that includes `| 5 | automatic | 7.7 s |`, `| 1 | automatic | 30.6 - 30.8 s |` and
`| 1 | 36 | **51.7 s** |`. It names no start point, so the page says that rather than guess one.

## Criterion 2 - every empty-window or start-up figure names its reference point

The 11 start-up / empty-window hits in the sweep below (#19, #20, #22 - #30): 51.7 s and 30.8 s are
marked as having no recorded start point and not comparable; 2.9 / 2.888 / 2.909 s and 2.1 - 3.1 /
3.1 - 4.1 s are from the response; 3.7 s is from the request being sent, with the 0.8 s request time
given; 7.7 s and 30 s are marked as not comparable. The 45 s figures (#18, #42) are a configured
limit (`CaptureWatchdog.DefaultStartupTimeout`, `src/AgentEyes.Core/Video/CaptureWatchdog.cs:167`), not
a measurement, and are left as they are.

## Criterion 4 - the sweep: 50 figures checked, method below

Method:

1. `docs/cencon/proof/issue-223/sweep.py` matches every number or range followed by a time unit
   (`ms`, `s`, `second(s)`, `minute(s)`, `hour(s)`, including `45-second`, `10-minute`). It runs
   twice: per line, and on the joined text (all whitespace, line breaks included, collapsed to one
   space).
2. **Positive control**: a copy of the original file with two planted figures, `12.3\ns` (wrapped
   across a line) and `45.6 s` (one line). Original: line form 43, joined 43. Control: line form
   **44**, joined **45**. The line form missed the wrapped plant, the joined form found both. The
   control file was then deleted.
3. Spelled-out figures, separately: `(one|two|...|ten|few|several|a)[ -](seconds?|minutes?|hours?)`.
   Line form (`/usr/bin/grep -ciE`): 3 lines. Joined form (`tr '\n' ' ' | tr -s ' ' | grep -oiE`):
   5 hits - "two seconds", "few seconds" x2, "a second" x2 (both "a second process/button", not
   times).
4. Every hit was read in context and classified by hand. The full listing is
   `docs/cencon/proof/issue-223/sweep-listing.txt`.

Result on the final file: **47** numeric hits (line form 47, joined 47) + **3** spelled-out time
figures = **50 figures checked**.

| Class | Hits | Reference point |
|---|---|---|
| Empty window / start-up, measured | 11: #19 #20 #22-#30 | Now stated, or marked not comparable (criterion 2) |
| Configured limit, threshold or quoted claim | 4: #18, #42 (45 s), #43 (row 5's 1.5 s pass threshold), "two seconds" | Not measurements. "two seconds" is already marked "never measured" |
| Length of a thing (take, video, audio, difference) | 18: #1-#3, #5, #7, #9, #10, #14, #31-#38, #41, #47 | Intrinsic (a duration, not a moment) |
| A/V offset in the row 5 table | 3: #44-#46 (each the second of a pair; the first of each pair, 0.487 / 0.095 / 0.614, is not a regex hit but was checked with it) | Audio against video, as the row says |
| Elapsed after a named event | 8: #15-#17 (after the stop request), #21 (loss after a kill), #39 #40 (after the service dies), "few seconds" x2 (no number) | Stated in the sentence |
| **#159 "wait" figures** | 6: #4 #6 #8 #11 #12 #13 | **HIT - see below** |

Total 11 + 4 + 18 + 3 + 8 + 6 = 50. #25's low end (2.1) belongs to the same hit as its 3.1.

**Hits outside this issue's scope - reported, not fixed.** #223's scope is start-up and
empty-window figures. Section 3, #159 (lines 140-143) says "The wait depends on the machine and the
audio" and then gives:

- macOS 13.7 s (10-minute take), 40.6 s (30-minute take) and 2.7 s (this release). These are **mux
  durations** from the product's own mux start/done log lines, not a wait after `idle`:
  `qa-report-macos.md:283` ("13.68 s, measured from the product's own start and done log lines"),
  `:642` / `:656` (40.62 s), `:1347-1348` (2.7 s, "read from ... `stage mux: start` / `done ... in 2.7s`").
- Windows 36.0 s and Linux 46.5 s: a literal search of `docs/cencon/proof/` found no committed source
  (the only `46.5` hits are #113 stall timings, unrelated). The reference point is unknown.
- macOS 0.5 s (32 s take): not traced in this sweep.

So that sentence uses an unstated reference point and, on macOS, a mux duration rather than the wait
it describes. That is the same defect class as #223, on a different figure. I recommend a separate
issue. It is not fixed here, so this PR does not widen #223.

Withdrawn bases, checked by name in both forms: "4-5 s" 0 / 0 after the fix (1 / 1 before),
"2-5 s" / "2 to 5" 0 / 0, "at most two seconds" 1 / 1 (kept on purpose: it is the quoted wrong
figure, marked as never measured). Control: a copy of the final file with `4-5\n  s` planted
read line form 0, joined 1, then was deleted.

**Instrument note.** In this shell, `grep` is a function that runs ugrep with `-G
--ignore-files`. `grep -nE '(^|[^0-9.])3\.7 ?s' docs/releases/v1.12.0.md` returned **rc=1 (no match)**
on a line that contains `(3.7 s`. `/usr/bin/grep` returned the hit. Every search here used
`/usr/bin/grep` or Python, never the bare `grep`.

## Criterion 5 - the published body differs from this branch only by this fix

- `gh release view v1.12.0 --json body` (raw API string) is **byte-identical** to
  `docs/releases/v1.12.0.md` on main `d38141c` (24,906 characters each, Python `==` is True).
  `gh ... --jq .body` adds one trailing newline, so compare the raw JSON string, not the jq output.
- `git diff origin/main -- docs/releases/v1.12.0.md` on this branch is the two hunks above, 11 in
  and 4 out. So a refresh from the merged file carries this fix and nothing else.
- QA / lead: re-run the same comparison at refresh time. If anything else has merged into the file
  by then, it will show.

## Gate

`docs/cencon/proof/issue-223/gate.txt`:

- `dotnet test tests/AgentEyes.Tests.Mac -c Release`: **Passed 433, Failed 0, Skipped 2, Total 435**
- `dotnet test tests/AgentEyes.Tests.Linux -c Release`: **Passed 474, Failed 0, Skipped 3, Total 477**

Not run here: `dotnet build AgentEyes.sln` and `dotnet test AgentEyes.sln` (the Windows gate). The
solution includes the WPF app and does not build on a Mac. No code changed, so the Windows suite is
not affected. CI runs it on the PR.

Non-ASCII characters in the changed file: 0.

## How QA should verify

1. Read the two hunks in `git diff origin/main -- docs/releases/v1.12.0.md`.
2. Criterion 3: open `emptywin.py` line 23 and `emptywin.txt` lines 3/5/16/18 and confirm that
   3.7 = round trip + `t+`.
3. Criterion 1: check each Linux bracket against the `row*.txt` lines quoted above.
4. Criterion 4: `python3 -I docs/cencon/proof/issue-223/sweep.py docs/releases/v1.12.0.md list`, then
   repeat the planted-figure control.
5. Criterion 5: compare the raw API body with main's file.

No runtime surface, so no smoke applies. CenCon impact: no drift. The privacy posture is not touched.

I believe this is finished.
