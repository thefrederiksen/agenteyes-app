# Issue #142 - QA report, LINUX (devlinux)

This is the **Linux** platform report. The Windows report is `qa-report-windows.md`. The macOS report is
not part of this one and was not run here.

This report combines two runs on two builds. **Each row says which build it was measured on.**

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

## Summary

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
