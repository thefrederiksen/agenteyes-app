# Issue #142 - QA report, LINUX (devlinux)

This is the **Linux** platform report. The Windows report is `qa-report-windows.md`. The macOS report is
not part of this one and was not run here.

This report combines three runs on three builds. **Each row says which build it was measured on.**

- **NEWEST: rows 4, 5 and 6 were re-run on 2026-11-16 on `main` at b0e6ae8, which contains #113 and #156.**
  See the section "2026-11-16 RE-RUN" directly below the build table. Verdicts there: row 4 PASS,
  row 5 PASS on 2 of 2 takes (with #157 not exercised), row 6 PASS on the amended criterion. The f9ef230 rows
  4-6 further down are kept as the record of main before #113.
- **Rows 4, 5 and 6 (the recorder) were re-measured on current `main` at f9ef230.** That is this
  seat's own work, done on 2026-11-15 between 14:08 and 15:00 EST.
- **Rows 1, 2, 3, 7, 8, 9 and 10 are carried forward from the earlier Linux seat.** That seat measured
  them on branch `issue-116-desktop-app` at **c80cb9d**, 09:33 - 09:52 EST the same day. They were not
  re-measured here. Its numbers are its own and are cited, not repeated as new.
- **Rows 11 and 12 are BLOCKED** on this machine, for the reasons given under each row.

Why the recorder rows were re-run: the earlier run's build, c80cb9d, contains none of #114, #128 or
#136. Rows 4, 5 and 6 are the mux and mic-chain rows, and #128 and #136 are the mux and mic-chain
fixes, applied on every platform. So the earlier rows 4-6 described code that will not ship (lead's
comment on #142, "both existing reports measured the WRONG BUILD").

| | |
|---|---|
| Machine | devlinux - Ubuntu 24.04.5 LTS, GNOME on Xorg, DISPLAY=:0, 32 cores, 39 GB RAM, kernel 6.8.0-147 |
| Commit under test, rows 4-6 | `main` at **f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab**. It contains #114, #128 and #136. **It does NOT contain #113** (PR #137, at `flow:ready-gate`) |
| Service under test, rows 4-6 | `~/.local/lib/agenteyes/agenteyes serve`, **pid 3332249**, started 14:09:30. Its `agenteyes.dll` **reports commit `f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab` about itself** (the `1.11.11+<sha>` version string), equal to `git rev-parse origin/main`. File sha256 `766086dc...` (machine-local only, see below) |
| Build under test, rows 1-3 and 7-10 | the earlier seat's: `issue-116-desktop-app` at c80cb9d, service pid 4100728, `agenteyes.dll` `81083721...cbec22`, app `AgentEyesDesktop` pid 4100815 |
| ffmpeg | 6.1.1-3ubuntu5, the system package (`/usr/bin/ffmpeg`) |
| Machine load (1-min loadavg, 32 cores) | 9.2 - 10.5 during the gate and install. Then **10 - 26** for row 4, **32 - 43** for row 5 take A, **16 - 44** for take B, **17.9 - 71.5** for row 6. Background on every take: the foreign go2rtc camera-wall ffmpeg (pid 2197930, 115 - 165% CPU, not touched). From 14:21 onward, also other sessions' .NET builds: a 32-node `dotnet build` of cc-director, then a `csc` at up to 1241% CPU. None of them were mine |
| Test gate (this seat, on f9ef230) | `dotnet build src/AgentEyes.Core -c Release`: **0 Error(s)**, 1 warning (the existing CS8602 at AlwaysOnEngine.cs:901). `dotnet test tests/AgentEyes.Tests.Linux -c Release`: **Passed 282, Failed 0, Skipped 0**. `AgentEyes.sln` contains the WPF app and cannot build on Linux, so it was not run here |

## 2026-11-16 RE-RUN: rows 4, 5 and 6 on main b0e6ae8 (with #113 and #156)

This section is a later seat's work, done on 2026-11-16 between 15:56 and 16:35 EST. It **adds to** the
f9ef230 measurements further down and does not replace them. Those are still the record of main
before #113. Evidence is in `linux/main-b0e6ae8/`.

**Corrected on 2026-11-20 after the Review Gate rejected head 5cefff8.** What changed, and why, is listed in
"Corrections after the Review Gate verdict" at the end of this section. Three evidence files were added by
the correction seat on 2026-11-20, read-only against the same recordings (the file sizes match the ones
logged on 2026-11-16): `row4-probe-rerun.txt`, `full-decode-rerun.txt` and `service-log-excerpt.txt`.
The test gate was re-run and its transcript committed as `test-gate-linux.txt`.

**Why these rows were re-run:** #113 (merged as `b0e6ae8`) changes the container of **every** screen
capture to fragmented MP4 (`-movflags +frag_keyframe+empty_moov+default_base_moof+delay_moov
-frag_duration 2000000 -flush_packets 1`, seen in the capture command line of every take below). It
also adds a capture watchdog. #156 (merged as `d0d0a27`) makes a capture hold a `capture.lock` claim.
Both change the healthy path that rows 4-6 measure.

### Verdicts

| # | Check | Build | Result |
|---|---|---|---|
| 4 | Records the screen, no audio | **main b0e6ae8** | **PASS** - one h264 stream and no audio stream, 25.700 s, 771 frames decoded, full decode exit 0 with no error output (`full-decode-rerun.txt`), 30 frames read in the last 1 s (`row4-probe-rerun.txt`) |
| 5 | Records screen + microphone, audio within 1.5 s of video | **main b0e6ae8** | **PASS, 2 of 2 takes** - audio **0.095 s** and **0.080 s** short on ~100.7 s takes. The defect behind the earlier FAIL (#157, open) was **not exercised**: neither take had an audio timestamp gap over 50 ms (see the limit below) |
| 6 | A 10-minute recording completes and is playable | **main b0e6ae8** | **PASS, on the amended criterion** - 610.567 s, full decode **exit 0 with no error output**, and **60 frames read in the last 2 s** (last pts 610.533). The camera clock in the picture reads 16:25:19 at pts 608.57, which is the second in which the stop request was sent (about 16:25:19.69) |

### Which binary was actually running

| | |
|---|---|
| Found installed on arrival | `~/.local/lib/agenteyes` and `~/.local/lib/agenteyes-app` both embed **`1.11.11+89e15934278fdf106d30d6ae2cfd13c22e9972c2`**. That is the unmerged #113 branch. It is **pre-#156**: the `capture.lock` marker is absent from its dll (0 vs 3). Service pid 6560, running since 06:35:49. Its dll sha256 `77586071...298284e` is the same file the f9ef230 seat restored on 2026-11-15, so nothing changed it in between |
| Reinstalled | **Yes.** `scripts/linux/install.sh` from this clone at `b0e6ae8213c9777781ba28b588c5edc04c0a85fb` (= `git rev-parse origin/main` after `git fetch`), 2026-11-16 **15:57:51 - 15:58:10 EST**, exit 0 (`linux/main-b0e6ae8/install-main-b0e6ae8.txt`). It replaced the 89e1593 service lib, the 89e1593 app lib and the `AgentEyesLinux --tray` app (pid 1298614) |
| Service under test | `~/.local/lib/agenteyes/agenteyes serve`, **pid 2211032**, started 15:58:08. The dll embeds **`1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb`**, and so does the app lib. Every log line quoted here carries `[pid 2211032]`. The dll sha256 `f50b04b2...` is machine-local only, not an identity |
| Feature markers (found-on-arrival vs under-test dll) | `capture.lock` (#156): **0 vs 3**. `delay_moov` (#113 pass 5): **0 vs 1**. `CAPTURE FAULT` and `frag_keyframe` (#113): present in both, as expected of a #113-branch build. Runtime: every take logged `[CaptureLock] TryAcquire ... claimed by capture session of pid 2211032` and `StartWatchdog: watching ... (stall 45s, start-up 45s, every 2s)` (`service-log-excerpt.txt`: 4 claims, 4 releases, 4 watchdog starts) |
| systemd unit (#165 baseline) | byte-identical before and after the reinstall. **`KillMode=mixed`**, `KillSignal=15`, `TimeoutStopUSec=2min` (`systemctl --user show` agrees) |
| Left installed afterwards | **main b0e6ae8**, on the lead's instruction. 89e1593 was not restored, because a pre-#156 service runs the repair pass over the real library while ignoring `capture.lock`. The pre-session install is kept at **`~/qa-backups/agenteyes-142/pre-session-install-89e1593.tgz`** (both libs, the unit, both `.desktop` entries). Restoring it is not blocked, and the steps are in `linux/main-b0e6ae8/build-identity.txt` |
| Test gate, on b0e6ae8 code | The 2026-11-16 seat reported **Passed 472, Failed 0, Skipped 2** but committed no transcript. The correction seat re-ran it on 2026-11-20 on head 5cefff8, whose code is identical to b0e6ae8 (the PR touches only `docs/cencon/proof/issue-142/`): `dotnet build src/AgentEyes.Core -c Release`: **0 Error(s)**, 1 warning (the existing CS8602). `dotnet test tests/AgentEyes.Tests.Linux -c Release`: **Passed 472, Failed 0, Skipped 2, Total 474**, exit 0 (`linux/main-b0e6ae8/test-gate-linux.txt`). `AgentEyes.sln` holds the WPF app and does not build on Linux |
| ffmpeg | 6.1.1-3ubuntu5, the system package |
| Machine load (1-min, 32 cores) | row 4: 5.8 - 6.7. Row 5 take A: 4.4 - 10.3. Take B: 5.8 - 7.6. Row 6: 2.19 - 24.59 in the committed samples, which are every 30th second plus the ones around O2 (31 of 606; the full series was not committed). On every take the foreign go2rtc camera-wall ffmpeg (pid 72110) was running and was not touched. The f9ef230 run's row 5 FAIL came at load 32 - 43, so **today's machine was much quieter** |

**Comparing with the f9ef230 numbers further down:** the binary moved twice. On 2026-11-15 rows 4-6
were measured on **f9ef230**, installed by that seat, which then restored **89e1593**. Today's rows are on
**b0e6ae8**, installed at 15:58:08. A difference between the two sets is a difference between builds
and loads, not necessarily a regression.

### All takes ran at default settings

All takes used `POST /record/start` with no `fps`, so the Control API default of **30 fps** applies
(`ControlApiRequest.cs:64`). The capture lines show `-framerate 30`. x264 ran with `threads=34
lookahead_threads=8` on this 32-core machine (`row4.txt`). **No low frame rate was used**, so #184 and #188 were
not in scope.

### The empty window, measured here (limit 2)

**Two reference points, and which one each number uses.** The harness prints `t=+` from a clock it starts
just BEFORE it sends `POST /record/start`. Its zero is derived as (sample time - its `t=+` value), so it is
good to +/- 0.05 s. On every take that zero falls at the harness's `ffmpeg before` line (within that
0.05 s) and 0.09 to 0.17 s before the service logged `[CaptureLock] TryAcquire` (`service-log-excerpt.txt`),
so it marks the moment just before the request went out. The 2026-11-16 version of this table printed these `t=+` values but labelled them "from the
`/record/start` response". That was wrong. The request took 0.6 s to 1.8 s to answer, so the two references
differ by that much.

**Two sources for "first bytes", and what each bounds.** Rows 4 and 5 A read `CaptureBytes` from
`/status`. That is the service's view, and it can trail the disk: on row 5 B the harness's own `stat` read
306,407 bytes at 16:12:51.017 while `/status` still read 0. So a `/status` reading bounds the disk's first
bytes from ABOVE only. Rows 5 B and 6 read the disk directly with `stat` of `raw.mp4`. The harness samples
once a second, so every figure is a bracket between two samples, not an instant.

All times below are from `row4.txt`, `row5.txt` and `row6.txt`.

| take | request sent (harness zero, derived) | `/record/start -> 200` logged | last sample with 0 bytes | first sample with bytes | first bytes, from the REQUEST | first bytes, from the RESPONSE |
|---|---|---|---|---|---|---|
| row 4 | 15:59:12.34 | 15:59:12.957 | 15:59:16.057 (`/status`) | 15:59:17.063, `/status` 221,466 | no later than +4.7 s | **no later than 4.11 s** |
| row 5 A | 16:10:09.29 | 16:10:10.504 | 16:10:13.603 (`/status`) | 16:10:14.608, `/status` 404,891 | no later than +5.3 s | **no later than 4.10 s** |
| row 5 B | 16:12:46.71 | 16:12:47.917 | 16:12:50.012 (`stat` 0) | 16:12:51.017, `stat` 306,407 | +3.3 to +4.31 s | **2.10 to 3.10 s** |
| row 6 | 16:15:09.13 | 16:15:10.913 | 16:15:11.014 is the last 0 in the committed file; the samples between are not committed | 16:15:14.029, `stat` 331,716 | no later than +4.90 s | **no later than 3.12 s** |

**What follows from this, and what does not.** Measured from the response, the first bytes reached the
disk no later than about 3.1 s (rows 5 B and 6, disk readings) and no later than about 4.1 s (rows 4 and
5 A, `/status` readings). The only committed lower bound on the disk is row 5 B's 2.10 s. Those figures are
**consistent with the 2-5 s window the lead published for the default 30 fps**. They do not contradict it.
The 2026-11-16 sentence saying "That is not the 2-5 s figure the lead published" was drawn from the wrong
reference point and is withdrawn. Measured from the request, the upper bounds are 4.3 to 5.3 s, because
they include the 0.6 - 1.8 s the request took. These takes do not test the corrected low-frame-rate figures
(about 7.7 s at 5 fps, about 30 s at 1 fps), because no low frame rate was run. None of these rows ended
uncleanly, so no verdict here depends on the window. It is quoted because every take passed through it.

(Cross-check against the gate's table: rows 4, 5 A and 6 agree. For row 5 B the gate gives 3.10 s. That is
the first sample WITH bytes. The sample one second earlier read 0, so the bracket is 2.10 to 3.10 s.)

### Row 4 - Records the screen, no audio: PASS (main b0e6ae8)

`POST /record/start {"mode":"video","screen":1,"source":"none"}` -> 200 logged at 15:59:12.957, and
`POST /record/stop {}` -> 200 logged at 15:59:38.484 after a 0.32 s round trip (so sent at about
15:59:38.16) -> `DurationSeconds 25.74`. Directory `2026-11-16_155912_video`.

| check | Expected | Actual |
|---|---|---|
| streams | video only | **exactly one stream: h264** 1920x1080 30/1. `-count_packets` on the audio streams returns nothing |
| positive control for the absence | the same `ffprobe` finds audio when there is audio | on row 5 take A the identical command returns `index=1 ... nb_read_packets=4719` |
| duration + frames | about the wall time | **25.700 s**, `nb_read_frames=771` (= 25.7 x 30). The wall time from the start response to the stop response was 25.53 s (to the stop request, about 25.2 s) |
| playable | full decode exit 0 | `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0** (`row4.txt`). The error output was not recorded on 2026-11-16. Re-run on 2026-11-20: **exit 0, 0 bytes of stderr**, and a half-length copy gives exit 0 with 6 error lines, so the stderr check does catch truncation (`full-decode-rerun.txt`) |
| frame near the end | a frame within 1 s of the end | `row4.txt` shows the seek to 24.70 s returned a frame (exit 0, a PNG written), but prints only "first frame pts at or after seek: 16.666667" and no frame count. The probe was re-run and its output committed (`row4-probe-rerun.txt`): **30 frames decoded at or after 24.70 s, first 24.700, last pts 25.667**. The 16.667 is explained there: the file's keyframes are at 0, 8.333, 16.667 and 25.000 s, the seek lands on the keyframe at 16.667, and that is the first frame decoded BEFORE the "at or after 24.70" filter. So the row 4 line matches the unfiltered first frame, and the rows 5 and 6 lines are the filtered count. The 2026-11-16 harness source is not committed, so this explanation was reproduced, not proven. The committed frame (`row4-near-end-frame-appwindow.png`, a crop of the app window) shows the app's `REC 00:24` |
| container | the #113 fragmented form | for screen-only, the capture file **is** `recording.mp4`. It holds `ftyp`, `moov` (770 bytes, at offset 28), **16 `moof`/`mdat` pairs**, and a closing `mfra` (a clean end) |

The package stage failed with `ffmpeg failed during extract audio (exit 234)`. That is known **#126**,
met again here. `row4.txt` captured the manifest at 15:59:39, after the first attempt, so it shows
`Attempts: 1`. The service log shows three attempts, `1/3` and `2/3` at 15:59:38-39 and `3/3` at 16:12:00,
each failing the same way (`service-log-excerpt.txt`). Screen-only has no mux stage: the manifest holds
`thumbnail: done` and `package: failed`. Evidence: `linux/main-b0e6ae8/row4.txt`.

### Row 5 - Records screen + microphone, audio within 1.5 s of video: PASS on 2 of 2 takes (main b0e6ae8)

`POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}`, with default mic
settings. Capture topology as before: one process (`x11grab` + `pulse` in one ffmpeg). Audio duration
is AAC packets x 1024 / 48000. The mux timing comes from **manifest `PostProcessing.mux.State`**, not from
`/status`, because `/status` was already `idle` 0.5 s after the stop on both takes, while the mux was still
`running`.

| take | dir | capture health | final video | final audio | difference (bound 1.5 s) | gaps > 50 ms (raw / final) | mux (manifest) |
|---|---|---|---|---|---|---|---|
| **A** | `2026-11-16_161009_video` | speed 1x, dup 6 / drop 4, 0 queue or non-monotonic warnings | 3023 frames, **100.767 s** | 4719 pkts = **100.672 s** | **0.095 s short - PASS** | 0 / 0 | `running` at +0.50 s, `done` by +5.51 s |
| **B** | `2026-11-16_161246_video` | speed 1x, dup 6 / drop 4, 0 warnings | 3020 frames, **100.667 s** | 4715 pkts = **100.587 s** | **0.080 s short - PASS** | 0 / 0 | `running` at +0.50 s, `done` by +6.01 s |

Both final files fully decode with **exit 0** (`row5.txt`). Their error output was not recorded on
2026-11-16. Re-run on 2026-11-20: exit 0 and **0 bytes of stderr** on both final files and both raw
captures (`full-decode-rerun.txt`). Both yield 30 frames in their last second (`row5.txt`), and the
committed frames show `REC 01:39` (`row5A-`/`row5B-near-end-frame-appwindow.png`). The package stage
completed on both. Title generation logged `transcript is empty; nothing to title`, and each recording
kept its preset name (`service-log-excerpt.txt`).

**The limit on this PASS:** the f9ef230 row 5 FAILED because a CPU-starved capture dropped ~5.5 s of
mic samples, and the two-pass mux then closed the holes up (L1, now **#157, still OPEN on main**).
Today **neither raw capture had an audio timestamp gap over 50 ms** (`gaps > 50 ms: 0`), so #157's path
was not entered. This PASS
shows the healthy path is within the bound on b0e6ae8. It does **not** show that the earlier FAIL is
fixed. Under the load that produced it, I would expect it to fail the same way. Evidence:
`linux/main-b0e6ae8/row5.txt`.

### Row 6 - A 10-minute recording completes and is playable: PASS on the amended criterion (main b0e6ae8)

**The criterion used is the amended one: a FULL DECODE TO EXIT 0, plus A FRAME READ FROM NEAR THE END.**
This seat added one tightening, from its own control below: the full decode must also leave **no
error output**, because exit 0 alone is vacuous on a truncated file too.

`POST /record/start {"mode":"video","screen":1,"source":"mixed","mic":"Family 17h"}` (screen + mic +
system) -> 200 logged at 16:15:10.913. `POST /record/stop {}` -> 200 logged at 16:25:20.467 after a
0.78 s round trip (so sent at about 16:25:19.69) -> `DurationSeconds 610.4`. Directory
`2026-11-16_161509_video`. Every second of the take was sampled: `CaptureLiveness` was `starting` x4,
**`growing` x600** and `not-growing` x2 (see O2; the counts are in `row6.txt`, the 606 samples themselves
are condensed). The service log has 0 `CAPTURE FAULT` and 0 `Abandon` lines for the whole run, and the
same fixed-string searches find 13 and 26 in the 2026-11-14 log, where a stalled capture was ended, so
the search would have found them (`service-log-excerpt.txt`). That absence does not carry the verdict:
the 600 `growing` samples and the frames at the end do. After the stop, the manifest showed mux `running` at +0.50 s and `done` by
**+46.54 s**, then thumbnail `done` (+47.04 s) and package `done` (+55.05 s), while `/status` read `idle`
the whole time.

| check | Expected | Actual |
|---|---|---|
| full decode | exit 0, no error output | `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0** (10.6 s, `row6.txt`). The error output was not recorded for the final file on 2026-11-16. Re-run on 2026-11-20: **exit 0, 0 bytes of stderr** (`full-decode-rerun.txt`) |
| frame near the end | frames in the last 2 s of the **expected** (wall-clock) length | **60 frames** decoded at or after 608.57 s, last pts **610.533** |
| length vs wall clock | about 610 s | 18317 frames, **610.567 s**. The camera wall's on-screen clock, which is part of the captured screen, reads **16:15:40 at pts 30.0** and **16:25:19 at pts 608.57**: 579 s of wall clock over 578.57 s of media (`row6-camera-clock-at-pts30.png`, `...-pts608.57.png`). The clock is read to the whole second. 16:25:19 is the second in which the stop request was sent, about 16:25:19.69. (`row6.txt` labels 16:25:20.467 "stop request". That is the time the stop RESPONSE was logged.) |
| audio vs video | within 1.5 s | 28619 pkts = **610.539 s** against 610.567 s: **0.028 s** short. 0 gaps > 50 ms in the final file and in the raw capture |
| capture health | keeping up | final progress line: speed 1x, dup 6 / drop 5 over 18317 frames, 0 queue or non-monotonic warnings |
| raw capture | fragmented (#113) and complete | `recording.original.mp4`: `moov` at offset 28, **367 `moof` fragments**, 610.567 s, fully decodes with exit 0 and 0 error lines (the full copy in the control below, `row6.txt`; the 2026-11-20 re-run on the library file agrees, `full-decode-rerun.txt`) |
| final file | a muxed MP4 | `ftyp`, `free`, `mdat`, **`moov` (667,991 bytes) at offset 58,732,237**, the index at the end, written by the mux |

**Positive control, showing that the old criterion, and exit 0 alone, are vacuous** (`row6.txt`, end). A
**copy** of the raw fragmented capture was made in a scratch directory and truncated to half its bytes. The
library file was not modified. Both criteria were then applied to the full copy and the half copy:

| | full copy | half copy (truncated) |
|---|---|---|
| OLD: `moov` atom present | yes, at offset 28 | **yes, at offset 28 - passes on a truncated file** |
| format duration | 610.567 s | 260.333 s |
| full decode exit code | 0 | **0 - passes on a truncated file too** |
| full decode error output | none | `partial file`, `Invalid NAL unit size` |
| NEW: frames at or after 608.57 s of the expected 610.57 s | **59** | **0** (last pts 259.633) |

The same control on row 5 take A's raw capture gives the same pattern (half copy: `moov` present, exit 0,
0 frames near the expected end). So on this ffmpeg (6.1.1) **a truncated fragmented file decodes to exit
0**, and only two checks catch it: the frame read near the **expected** end, measured against the wall
clock rather than the file's own reported duration, and an empty error output. Row 6 passes both.
That is what this control shows on two files at one truncation point. It does not prove those are the only
checks that could catch truncation.

The `system.original.wav` track is 608.980 s against 610.567 s of video, **1.59 s short**. That is known
**#121**, met here and not re-raised (the f9ef230 run measured 2.373 s). The system source was silent.
Evidence: `linux/main-b0e6ae8/row6.txt`.

### Observations, not judged as defects

- **O2 - `CaptureLiveness` read `not-growing` for 2 s on a healthy capture.** At elapsed 300.07 s and 301.08 s
  of row 6, `/status` reported `not-growing` with `CaptureBytes` 31,277,258. My own `stat` of `raw.mp4` at
  those same instants already read **31,387,397**. By 302.08 s the service reported `growing` again. So the
  service's view trailed the disk by about one 2 s poll. It is far inside the 45 s stall limit and caused
  no action. A client that polls `/status` and alarms on `not-growing` would see a false flicker.
- **O3 - the desktop app's Record page did not reflect the API-started recording's source.** In the
  committed row 5 A and B frames (mic takes), the app window shows `REC` and `STOP` but its Microphone
  selector reads "No microphone". The row 6 frame is partly covered by a dialog (O4), so it is not cited
  for this. That is app UI, outside rows 4-6, and was not investigated.
- **O4 - `install.sh` opens the app window on the desktop**, and it stayed open during every take, so it
  appears in the captured frames. An Ubuntu "upgrade available" dialog appeared over it during row 6.
  That dialog was not mine and was not touched. It is why the row 6 frame cannot show the `REC` counter,
  and why the camera clock is used instead.
- The first capture created `~/.local/share/AgentEyes/host-id` (`[CaptureLock] ReadOrCreateHostId: created`
  at 15:59:12.441, `service-log-excerpt.txt`).
  That is #156's own behaviour on its first run on this machine, noted because it is a new file in the
  owner's app data.

### What this re-run does NOT cover

- **#113's failure path.** No take stalled or ended uncleanly, so the watchdog's stall and abandon actions,
  and the playability of an unclean end, were **not** exercised. The truncation control shows how a truncated
  fragmented file behaves. It is not a capture that the product itself ended.
- **#156's protection against a second process.** The claim was taken and released on every take
  (`service-log-excerpt.txt`), but no second process tried to act on a live recording, so the refusal itself was not tested.
- **#157 under load.** See row 5. The committed load samples peak at 24.59 (row 6) and are under 11 on
  rows 4 and 5. No take showed an audio timestamp gap over 50 ms.
- **Low frame rates (#184, #188).** Not run, as instructed.
- **Audio content and lip sync on a sound event.** The mic and the system source were near-silent. These
  rows prove track length and timestamps, not intelligibility or a sound lining up with its picture.
- **Rows 1-3 and 7-12**, and **Windows and macOS**.

### State of devlinux after this re-run

- **Installed: main b0e6ae8**, service pid 2211032, app `AgentEyesDesktop` pid 2211136. The pre-session
  89e1593 install is in the tarball named above.
- **`~/Videos/AgentEyes`: nothing deleted.** Four recordings were added: `2026-11-16_155912_video`
  (row 4), `_161009_video` (row 5 A), `_161246_video` (row 5 B), `_161509_video` (row 6).
  The four directories and `2026-11-14_102532_video` were still present when the correction seat checked
  on 2026-11-20. The 2026-11-16 count of library entries was not committed and is not repeated here.
- **Processes:** the harness listed ffmpeg processes before each take and at the end of rows 5 A, 5 B and
  6, and each end list holds only the go2rtc ffmpeg (pid 72110). Row 4's harness ended before its end list,
  but the list taken before row 5 A (16:10:09) again holds only pid 72110, so row 4's capture ffmpeg
  (2213909) had exited by then. The 2026-11-16 seat states that the only process it killed was its own
  harness script (`take.py`, after row 4: its end condition waited for a mux stage that screen-only
  recordings do not have) and that it killed no ffmpeg. The service log has no `Abandon` line, so the
  watchdog ended no capture (`service-log-excerpt.txt`).
- The truncation controls and every scratch file were written to a session scratch directory, never into
  the library. The 2026-11-20 correction seat only read the library files, wrote its own truncated copy to
  a scratch directory and deleted it, and killed no process.

### Corrections after the Review Gate verdict on 5cefff8 (2026-11-20)

- **B1, the empty window.** The table had the wrong reference point. It now gives both references and says
  which source bounds what. The sentence saying the measurement contradicted the published 2-5 s figure is
  withdrawn: from the response, the evidence is consistent with it. The numbers were derived again from
  `row4.txt`, `row5.txt` and `row6.txt`. They agree with the gate's table except row 5 B, where the
  previous sample read 0, so the bracket is 2.10 to 3.10 s, not 3.10 s.
- **B2, row 4's frame near the end.** Chosen fix: **re-ran the probe and committed the output**
  (`row4-probe-rerun.txt`), read-only on the same file (size 1,958,629 bytes, as in `row4.txt`). It gives
  30 frames at or after 24.70 s and last pts 25.667. It also explains the 16.666667: that is the keyframe
  the seek lands on, the first frame decoded before the "at or after" filter. The re-run used `ffprobe
  -read_intervals`. The 2026-11-16 harness source was not committed, so this shows a mechanism that
  reproduces 16.666667 on this file. It does not prove it is the harness's exact command.
- **Test gate.** Re-run and transcript committed (`test-gate-linux.txt`): 472 passed, 0 failed, 2 skipped,
  the same as claimed.
- **Same class of defect, found on re-reading.** "No error output" on the full decodes of rows 4, 5 and 6
  had no file behind it (the probes logged only the exit code). It was re-run with stderr captured and a
  truncated-copy control (`full-decode-rerun.txt`). The gate's non-blocking items are fixed: the row 4
  camera-clock claim is removed (the committed image is a crop without the clock); the row 4 retry claim
  is now backed by the service log, not by `row4.txt`; the row 6 load range now matches the committed
  samples; the CAPTURE FAULT / abandon absence now has a committed log and a positive control; and the
  row 4 wall time is labelled response to response. Other changes: the stop times are labelled as
  response times; "a single timestamp gap" is now "a gap over 50 ms", which is what was measured;
  `bframes=3` is removed (it is in no committed file); the library entry count and the `pgrep` claim are
  replaced by what the committed files show; O3 now cites only the frames that show it.
- **Verdicts unchanged:** row 4 PASS, row 5 PASS on 2 of 2 takes (#157 not exercised), row 6 PASS on the
  amended criterion.

---

## Summary (2026-11-15 runs: rows 4-6 on f9ef230, the rest on c80cb9d)

| # | Check | Build | Result |
|---|---|---|---|
| 1 | Installs by the supported route | c80cb9d (earlier seat) | **PASS** (carried forward) |
| 2 | Launches; the main window opens | c80cb9d (earlier seat) | **PASS** (carried forward) |
| 3 | The library lists existing recordings, with a count | c80cb9d (earlier seat) | **PASS** (carried forward) - 76 |
| 4 | Records the screen, no audio | **main f9ef230** | **PASS** - one h264 stream, 23.267 s, 698 frames, full decode exit 0 (package stage hits known #126) |
| 5 | Records screen + microphone, audio within 1.5 s of video | **main f9ef230** | **FAIL** - 1 of 2 takes. Take B: **0.106 s** short on a 109.6 s take (within the bound). Take A: **5.610 s** short on a 104.6 s take (outside it), and the audio runs ~5.5 s early from 26.7 s to the end. Cause: new defect **L1** |
| 6 | A 10-minute recording completes and is playable | **main f9ef230** | **PASS** - `moov` at offset **59,272,721**, 635.93 s, audio 0.157 s short, full decode exit 0. Tested without #113 |
| 7 | Plays a recording back in the app | c80cb9d (earlier seat) | **PASS** (carried forward) |
| 8 | Detail view, settings and dictionary open and save | c80cb9d (earlier seat) | **PASS** (carried forward) |
| 9 | Tray icon shows idle and recording | c80cb9d (earlier seat) | **PASS** (carried forward) |
| 10 | Single instance: a second launch raises the first window | c80cb9d (earlier seat) | **PASS** (carried forward) - defect #143 seen in the log |
| 11 | Camera + microphone recording | - | **BLOCKED** - no camera on devlinux |
| 12 | Always-on keeps the stretches with sound | - | **BLOCKED** - Phase 5 has not started |

**Headline: on current main, Linux records the screen cleanly and completes a 10-minute recording
with its `moov` atom in place. But a microphone recording made while the machine is heavily loaded comes out with its
audio ~5.5 s out of sync with the picture. The cause is the new two-pass mux (#136): when the capture
drops a few seconds of mic audio, the mux closes the hole up instead of keeping it in place (L1).**

**Tested WITHOUT #113.** Main at f9ef230 does not contain #113. A row 6 PASS here means "a healthy
10-minute capture completes and is playable". It does **not** mean "a capture that stalls is caught":
on this build, it is not.

### #113 will change rows 4-6: an objection to the brief, raised before the takes were run

The brief says rows 4-6 can be measured without #113, and that no Wave A row will need re-running
once #113 merges. **The first half holds.** #113's watchdog and `Abandon` path only act on a capture
that has stopped producing output, and none of the takes here stopped.

**The second half does not hold.** #113 changes the **healthy** path as well. On its branch
(`issue-113-capture-watchdog`, head ad21557), `FfmpegArgs.Linux.cs` and `FfmpegArgs.cs` add
`FragmentedOutput()` to **every** screen capture:
`-movflags +frag_keyframe+empty_moov+default_base_moof -frag_duration 2000000 -flush_packets 1`.

That changes the container rows 4, 5 and 6 all read:

- the raw file becomes fragmented MP4, and its `moov` moves to the front as an empty index;
- the mux reads fragmented input;
- the file grows in a different pattern.

So **rows 4, 5 and 6 should be re-run on the commit where #113 lands.** The measurements here are a
valid baseline for main as it stands, and they are labelled that way.

## Which build was answering

1. **Found on arrival:** service pid 28960, `agenteyes.dll` `77586071...298284e`. This is #113's
   branch build, which the earlier seat put back. The pre-session install (both libs, the unit and both
   desktop entries) was taken as a tarball before anything changed.
2. **Installed:** `scripts/linux/install.sh` from a clean checkout of main at f9ef230, exit 0
   (`linux/install-main-f9ef230.txt`). **New pid 3332249**, `agenteyes.dll` `766086dc...9f63c3a`.
3. **The build's own word, the primary identifier.** The assembly embeds `1.11.11+<commit>`. The
   answering dll reports **`f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab`**, which equals
   `git rev-parse origin/main`. The same read on the build found on arrival reports `89e1593` (#113's
   "round-two handoff" commit), so the check gives a different answer for a different build. This
   follows the lead's correction of the brief: a dll hash embeds the absolute source path and the
   commit, so it does not reproduce across clones. The hashes in this report only tell the two files on
   this machine apart. They are not reference values.
4. **The apphost is not an identifier.** The native launcher `agenteyes` hashes the same
   (`65445a3f...`) in both builds. It is the generic self-contained stub, as on macOS. Every hash in
   this report is of `agenteyes.dll`.
5. **Feature markers, each shown both ways.** String literals are counted as UTF-16LE, and an ASCII
   grep finds most of them in neither build. Identifiers (`FragmentedOutput`, `CaptureLiveness`,
   `MuxInTwoPasses`) are metadata, so they are counted as UTF-8. `FragmentedOutput` is utf8 1 in the old
   build and 0 in the new one, and `CaptureLiveness` is 3 and 0.

   | marker | from | old 77586071 | new 766086dc |
   |---|---|---|---|
   | `CAPTURE FAULT` | #113 | 2 | **0** |
   | `intermediate processed audio`, `MuxInTwoPasses` | #136 | 0, 0 | **2, 1** |
   | `asetnsamples=n=480:p=1`, `ReapOrphans: no records` | #128 | 0, 0 | **2, 1** |
   | `mic_native.wav`, `difference in device warm-up` | #114 | 0, 0 | **2, 1** |

   Each marker is present in exactly one build, so each one can tell the two apart. The runtime log
   agrees: row 5 runs `[AudioMix] MuxInTwoPasses` (#136), with the `aeval` NaN guard and
   `asetnsamples` framing at the head of the mic chain (#128). Every log line quoted below carries
   `[pid 3332249]`. Details: `linux/build-identity.txt`.

---

## Row 1 - Installs by the supported route: PASS (carried forward, c80cb9d)

Measured by the earlier seat on c80cb9d. `scripts/linux/install.sh` exited 0, a new service pid and a
new app pid were running, and `/health` returned `ok: true`. Evidence:
`linux/earlier-c80cb9d/row1-install-transcript.txt`.

Not re-run, because a recorder change does not touch the installer. This seat did run the same script
on main to install the build under test, and it also exited 0 (`linux/install-main-f9ef230.txt`). That
run is a by-product, not a re-measurement of row 1.

## Row 2 - Launches; the main window opens: PASS (carried forward, c80cb9d)

Measured by the earlier seat: `09:34:37.377 [pid 4100815] [INFO] [MainWindow] Opened`, with an X window
of 1280x720 in state IsViewable. Evidence: `linux/earlier-c80cb9d/row2-3-main-window.png`. The desktop
app exists only on `issue-116-desktop-app`, so that branch is the right place to measure it.

## Row 3 - The library lists existing recordings with a count: PASS (carried forward, c80cb9d)

Measured by the earlier seat: the header read "76 recordings", and the directory count on disk at that
moment was 76. Evidence: `linux/earlier-c80cb9d/row3-library-76.png`.

## Row 4 - Records the screen, no audio: PASS (main f9ef230)

`POST /record/start {"mode":"video","screen":1,"source":"none"}` at 14:20:55.013, and
`POST /record/stop {}` at 14:21:17.860. Directory `2026-11-15_142055_video`. 1-min load 10.0 at the
start, 22.2 at the stop (another session's 32-node build began during the take).

| ffprobe | value |
|---|---|
| streams | **exactly one: `h264`**, 1920x1080, 30/1, and no audio stream (the presence check: the file's only stream is video) |
| duration | 23.267 s |
| frame count | `nb_frames=698`, and decoded with `-count_frames`: `nb_read_frames=698` |
| playable | full decode `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0**, no error output (load 26.1) |

The package stage failed with `ffmpeg failed during extract audio (exit 234)`. That is known defect
**#126**, met here and not re-filed. The recording itself is intact. Evidence: `linux/row4.txt`.

**Observation O1, judged: benign.** The video is 23.27 s long, and the wall time from start request to
stop request was 22.85 s, so the video is +0.42 s (1.8%) longer. In the earlier run on c80cb9d it was
+2.08 s (9%). This time the drain took 527 ms (`pipeline drained in 527ms (take 20968ms, media origin
1805ms)`), where the earlier run's took 2096 ms. The video's length matches the capture process's own
lifetime: the process ran from about 14:20:55.1 until the drain ended at 14:21:18.42, which is
23.3 s. The capture keeps grabbing the real screen until it is told to quit. So the extra time is real
screen content from the drain after stop was pressed, not invented or stretched video. It shrinks
as the drain gets shorter.

**Supplementary, unintended: a 608 s screen-only take.** The first row-4 attempt was not stopped after
22 s, because the harness sent `/record/stop` with no body and the service answered `411 Length
Required`. That was my harness's bug, not a product defect. The service logged no stop request until
the correct one at 14:20:39 (`DurationSeconds 608.52`). Directory `2026-11-15_141031_video`. Its
measurements are in `linux/row4-supplementary-608s.txt`. Top-level atoms `ftyp`, `free`, `mdat`, then **`moov` at offset 38,103,644**. One h264 stream, 608.567 s, 18257 frames (`nb_read_frames` 18257), full decode exit 0. The package stage hit #126 and retried a bounded number of times. This is an extra 10-minute data point for the screen-only path, and it was measured on the same build.

## Row 5 - Records screen + microphone, audio within 1.5 s of video: FAIL (main f9ef230)

This is the #114 check. **The criterion, as revised by the lead on 2026-11-15: the audio duration
within 1.5 s ABSOLUTE of the video duration**, reported with the difference in seconds, the take length
and the capture topology. A percentage depends on the length of the take, so it is shown alongside but
is not the criterion.

**Capture topology on Linux: one process.** The recorded mic track comes from the same ffmpeg as the
screen (`-f x11grab ... -i :0+0,0 -f pulse ... -i alsa_input...` in one command line), and the mux's
pass 1 reads `[0:a]` from that file. A second `-f pulse` ffmpeg that writes s16le to a pipe runs
alongside it. That one feeds the level meter (`[PulseStream] Stop mic`) and is not the recorded track.

Both takes used
`POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}`
with the default mic settings (arnndn + speechnorm chain). Audio durations come from AAC packets
counted with `ffprobe -count_packets`, x 1024 samples / 48000 Hz.

| take | dir | 1-min load | capture | final audio | final video | difference (bound 1.5 s) / ratio |
|---|---|---|---|---|---|---|
| **A** | `2026-11-15_142203_video` | **32 - 43** | starved: speed 0.68x at 26 s, 597 dup / 595 drop | 4642 pkts = 99.03 s (stream 98.990 s) | 3138 frames, 104.600 s | **5.610 s short - FAIL** / 94.6%. Audio after 26.7 s runs ~5.5 s early |
| **B** | `2026-11-15_143104_video` | 16 - 44 | keeping up: speed 1x, 7 dup / 5 drop | 5131 pkts = **109.461 s** | 3287 frames, **109.567 s** | **0.106 s short - PASS** / 99.90% |

**Take B passes**, at 0.106 s against a 1.5 s bound. That is close to the earlier c80cb9d run (0.079 s)
and much tighter than macOS's ~1 s, as you would expect from one-process capture. The mux took 9.5 s, the package
stage completed, and both files fully decode with exit 0.

**Take A fails.** What happened, in order (evidence: `linux/row5.txt`):

1. **The capture was starved of CPU.** Take A started right as another session's 32-node build
   pushed the 1-min load to 43. The capture's own log shows the encoder at speed **0.108x** at 3.7 s
   and 0.68x at 26 s, with 597 duplicated and 595 dropped frames in total. Both input queues logged
   `Thread message queue blocking`. Then came 14 `Queue input is backward in time` and 35
   `Non-monotonic DTS` warnings on the audio stream (counted lines; ffmpeg folds further repeats into
   "Last message repeated" lines).
2. **The raw mic track lost about 5.5 s of samples in two bursts**: pts 0 to 3.08 and pts 24.0 to 26.7.
   The second burst is 14:22:27, inside the load peak. The raw file (`recording.original.mp4`) keeps
   these as **timestamp gaps**. Its audio still spans 104.52 s, so in the raw file the sound stays in
   sync with the picture and plays silence in the holes.
3. **The mux then closed the gaps up.** `recording.mp4` has 0 gaps, and its audio ends at 98.99 s
   against 104.60 s of video. Everything after the second burst plays ~5.5 s early, to the end of the
   recording. That is new defect **L1**, below.

**A harness alarm that was not a stall.** My no-growth alarm fired at 14:22:42 (`raw.mp4` had been 48
bytes for 18 s). I followed rule 7 and did not stop or kill anything. `/status` said `recording`,
elapsed 46.1 s. The capture ffmpeg was in state `R` at 110 - 165% CPU, and `raw.mp4` was growing again
by 14:22:49 (1,572,912 bytes = 48 + 6 x 256 KiB). It then grew steadily, sampled every second, until
the planned stop. So it was a starved encoder holding output back, not a blocked capture. The 18 s
of no output came from the same CPU starvation as point 1.

**Why the row is FAIL and not PASS-with-a-caveat:** on one of two takes, the audio is 5.61 s off,
nearly four times the 1.5 s bound, on the build that will ship. A loaded machine
is not an exotic condition for an always-on recorder on a shared box. More importantly, the part that
turns a dropout into a desync is the product's own post-processing, and it reproduces without any load
at all (below).

## Row 6 - A 10-minute recording completes and is playable: PASS (main f9ef230)

This is the #113 check, and **#113 is not in this build**, so the row is measured here as "a healthy
10-minute capture completes and is playable", nothing more.
`POST /record/start {"mode":"video","screen":1,"source":"mixed","mic":"Family 17h"}` (screen + mic +
system) at 14:34:40.856, and `POST /record/stop {}` at 14:45:15.062, which returned at 14:45:18.769.
Log: `stop: return ... dur=634.4s`. Directory `2026-11-15_143441_video`. 1-min load over the take was
17.9 - 71.5 (other sessions' builds, plus the go2rtc ffmpeg). The harness sampled `/status` and the
size of `raw.mp4` every 5 s. The file grew at 122 of 124 samples. The two exceptions were single
5-s intervals (14:36:58 and 14:40:35) with no 256 KiB write, and growth resumed at the next sample. Post-processing finished every stage:
two-pass mux (`MuxInTwoPasses: mux video+mic+system done`) in 71.0 s, the thumbnail, and package in
9.8 s.

| check | result |
|---|---|
| top-level atoms of recording.mp4 | `ftyp` 32, `free` 8, `mdat` 59,272,681, **`moov` 695,479 bytes at offset 59,272,721** |
| ffprobe | h264 1920x1080 30/1, **19078 frames** (decoded count 19078), **635.933 s**; aac 48 kHz stereo, 29803 packets |
| audio vs video | 29803 x 1024 / 48000 = **635.776 s** against **635.933 s**: **0.157 s** short (bound 1.5 s), 99.98% |
| timestamp gaps | 0 audio gaps > 50 ms in the final file, and 0 in the raw capture |
| capture health | final progress line speed 1x, 125 dup / 124 drop over 19078 frames, and 0 queue / non-monotonic warnings |
| playable | full decode `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0, no error output**, 45.8 s at load 47.9 |

The raw capture `recording.original.mp4` also has its `moov` (at 56,723,966), and it also fully decodes
with exit 0. Compared with the earlier c80cb9d run (moov at 19,364,628, 632.03 s, audio 0.033 s short):
same verdict, on a different build.

**Note on load and take A:** row 6 ran at load up to 71 with the capture keeping up at speed 1x, while
row 5 take A starved at load 43. So the 1-min load average alone does not predict starvation. What
coincided with take A was the start of a 32-node build. I state this as an observation and do not
claim a cause.

Evidence: `linux/row6.txt`.

## Row 7 - Plays a recording back in the app: PASS (carried forward, c80cb9d)

Measured by the earlier seat. The real X window showed the video playing, and the scrubber moved from
0:04 to 0:24 between two captures. The app's self-render of the video area was black (libvlc draws into
a native child window that the self-render cannot see), and that seat diagnosed it rather than passing
it. Evidence: `linux/earlier-c80cb9d/row7-player-xwd.png`, `row7-player-screen.png`,
`row7-player-selfrender-video-area-black.png`.

## Row 8 - Detail view, settings and dictionary open and save: PASS (carried forward, c80cb9d)

Measured by the earlier seat. Each save was proven on disk or through the API, then reverted:

- Settings: `[Autostart] Disable`, and the autostart file was gone; then restored byte-identical.
- Detail: rename -> `POST /recordings/.../rename: 200`.
- Dictionary: add, then delete, each confirmed in `dictionary.json`; the file was restored byte for byte.

Evidence: `linux/earlier-c80cb9d/row8-*.png`.

## Row 9 - Tray icon shows idle and recording: PASS (carried forward, c80cb9d)

Measured by the earlier seat. The icon showed a red dot with D-Bus `Title` `AgentEyes - recording`, and a
moon with `Title` `AgentEyes`, each paired with its `[TrayController] Render` log line. Evidence:
`linux/earlier-c80cb9d/row9-tray-recording.png`, `row9-tray-idle.png`.

## Row 10 - Single instance: a second launch raises the first window: PASS (carried forward, c80cb9d)

Measured by the earlier seat. The window was hidden (IsUnMapped), then a second launch ran, the first
instance logged `Accept: 'show'`, the window became IsViewable, and one process was left. Evidence:
`linux/earlier-c80cb9d/row10.txt`.

The same excerpt shows **#143**: concurrent writers to the shared daily log overwrite each other's
bytes. 3 of the 16 handoffs in that run lost the start of a line. It is filed, and only cited here.

## Row 11 - Camera + microphone: BLOCKED

`ls /dev/video*` -> `No such file or directory`, and `GET /devices` returns `"cameras": []` (re-checked
by this seat at 14:10 on f9ef230). devlinux has no camera. Running this row needs a USB webcam plugged
in by the owner. **BLOCKED means "measured blocked on devlinux"**: Windows has a camera, and runs row 11
there.

## Row 12 - Always-on keeps the stretches with sound: BLOCKED

Always-on is Windows-only in the engine until Phase 5 (#119), which has not started. That makes this
row blocked, not failed.

---

## New defect (handed to the lead, not filed)

**L1 - The two-pass mux (#136) closes up gaps in the raw audio, so a short capture dropout becomes a
desync that lasts the rest of the recording.**

- **Where:** #136's pass 1 renders the processed mic audio to `audio_processed.wav`
  (`-c:a pcm_f32le`, `FfmpegArgs.ProcessedAudioCodec`). A WAV file holds samples with no timestamps.
  Pass 2 then pairs that WAV with the copied video from time 0.
- **Effect:** when the raw capture has timestamp holes (here, about 5.5 s of mic samples lost while
  the machine was starved of CPU), the WAV packs the remaining samples together. Every sound after
  the first hole plays early, by the total length of the holes so far.
- **Reproduced deterministically** on take A's raw file, outside the service
  (`linux/row5-repro-mux-drops-timestamp-gaps.txt`):

  | mux shape | audio | in sync? |
  |---|---|---|
  | as shipped (#136 pass 1 -> WAV) | **98.990 s**, the same as the final file | no |
  | the same chain with `aresample=async=1:first_pts=0` in front | **104.560 s** (video 104.567 s) | yes |
  | the pre-#136 single-pass shape (chain straight into AAC) | **104.548 s**, 24 gaps kept | yes |

- **Why it matters:** on this evidence, #136 introduced this regression, and it only shows up when the
  capture drops samples. On the same raw file, the pre-#136 mux shape kept the timing (row 3 of the
  table). The earlier Linux run on c80cb9d could not have shown it, because its take dropped nothing.
  With the old shape, a dropout costs a few seconds of sound. With #136's shape, it also costs lip sync
  for the rest of the recording. I reconstructed the pre-#136 shape from `FfmpegArgs.cs` at 1b1b266^
  and ran it by hand. I did not run it through an older service build.
- **Not covered:** why PulseAudio/PipeWire overran is environmental here (load 32 - 43 from other
  sessions). Whether the capture should resist starvation better (larger `thread_queue_size`, priority)
  is a separate question. The `aresample=async=1` line is a direction for the fix and was not tested
  through the service or the test suite. The mixed mic+system mux was not checked for the same
  behaviour, though it uses the same pass-1 intermediate.

## Known defects met, cited and not re-raised

- **#126** - the package stage fails on screen-only recordings (`extract audio`, exit 234). Met on
  row 4 and the 608 s take.
- **#121** - row 6's system track (`system.original.wav`) is 633.560 s against 635.933 s of video, **2.373 s short**. The earlier run measured 1.85 s. The system source was digital silence (-91.0 dB flat), so the picture-to-sound offset could not be measured, only the track length.
- **#143** - carried forward from row 10 (the earlier seat). Not exercised by this seat's rows: only one
  process wrote to the log during rows 4-6.
- **#128** - not met. With #128 in the build, the default mic chain finished on every take: mux 7.3 s
  (take A), 9.5 s (take B), 71.0 s (row 6).

## What this report does NOT cover

- **#113's behaviour.** Main at f9ef230 does not contain it. No take here stalled, so the stalled-capture
  path was neither exercised nor certified. And see the objection above: rows 4-6 should be re-run
  where #113 lands.
- **Rows 1-3 and 7-10 on main.** The desktop app is not on main. Those rows stand on c80cb9d and need
  confirming at #116's final head when it merges.
- **Audio content and lip sync on a sound event.** The mic and the system source were near-silent
  (take A's final mix: mean -69.8 dB, max -53.4 dB). These rows prove track **length** and timestamps.
  They do not prove that speech is intelligible, or that a sound lines up with its picture.
- **Windows and macOS.**

## State of devlinux after this run

- **The service build found on arrival is running again.** I restored the pre-session tarball (both
  libs, the unit and both desktop entries), and `diff -r` shows every path identical to the tarball.
  Service pid **638371**. Its `agenteyes.dll` reports commit `89e1593` (#113's branch), the same as on
  arrival, and `/health` is ok. `AgentEyesLinux --tray` is relaunched (pid 639161), as it was before.
- **Processes:** every capture ffmpeg was started by the service and exited with its recording. The app
  started by my install run (pid 3332380) was stopped by pid. The foreign go2rtc ffmpeg (pid 2197930)
  was never touched.
- **`~/Videos/AgentEyes`: nothing deleted.** Five recordings were added: `2026-11-15_141031_video` (608 s
  supplementary), `_142055_video` (row 4), `_142203_video` (row 5 take A), `_143104_video` (take B),
  `_143441_video` (row 6). `2026-11-14_102532_video` and the earlier seat's recordings are present.
- The only scratch outputs (the L1 reproduction WAV/MP4 files) were written to a temp directory and
  never into the recording folders.

## Evidence files (`docs/cencon/proof/issue-142/linux/`)

This seat, on main f9ef230: `build-identity.txt`, `install-main-f9ef230.txt`, `row4.txt`,
`row4-supplementary-608s.txt`, `row5.txt`, `row5-repro-mux-drops-timestamp-gaps.txt`, `row6.txt`.

The earlier seat, on c80cb9d (brought into the repository from its branch commit 0c62e33, unchanged
except for two lines in the install transcript: a mktemp staging path now reads `<stage>`, and the
signed-in account now reads `<owner account>`):
`earlier-c80cb9d/row1-install-transcript.txt`, `row2-3-main-window.png`, `row3-library-76.png`,
`row4.txt`, `row5.txt`, `row6.txt`, `row6-probe.txt`, `row7-*.png`, `row8-*.png`, `row9-*.png`,
`row10.txt`. Its rows 4-6 files are kept as history, and they describe the c80cb9d build.
