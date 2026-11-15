# Issue #142 - QA report, WINDOWS (SORENLAPTOP) - Wave A, the recorder half

This is the **Windows** platform report, the second of three. It covers **Wave A only**: the engine and
the Control API (rows 1, 4, 5, 6), plus rows 11 and 12, which turned out to be runnable here. Rows 2, 3, 7,
8, 9 and 10 belong to the Avalonia desktop app, which does not ship on Windows yet. The Linux report is
`qa-report-linux.md`. The macOS report is not part of this one and was not run here.

| | |
|---|---|
| Machine | SORENLAPTOP - Windows 11 Home 10.0.26200, Intel i5-1135G7 (4 cores / 8 threads), 15.7 GB RAM, two 1920x1080 monitors |
| Date | 2026-11-15, 10:40 - 11:45 EST |
| Commit under test | `main` at **93681fa** (#128 merged, "default mic chain no longer hangs on a NaN sample ..."). The tree was clean, and `dist\` is git-ignored |
| Build under test | `scripts\build-release.ps1` from 93681fa, version 1.11.11, installed by `agenteyes-setup`. `AgentEyesApp.exe` sha256 **38524e3c...c4f57011** (`38524e3c9882163f72fb4a8d0ab6b9eaa0c36d62a359b4607f1d1d15c4f57011`), `agenteyes.exe` `1c4b3ef17c155a46721f00e1f0a22ae745c6c206700ec047a9d78df7b7eb4fdf`. Installed bytes = dist bytes for all three exes |
| Process answering | `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray`, **pid 22104**, started 10:46:13. Every log line quoted below carries `[pid 22104]` |
| ffmpeg in use | **9.0.2** (gyan.dev essentials, sha256 `3256173f...`). This is the version the published release bundles, and the app's own launch update put it in place (see row 1) |
| Machine load | Windows `LoadPercentage`, sampled during every take: 7 - 15% idle between rows; **row 4** 37 - 57%; **row 5** 15 - 53%; **row 6** 14 - 47%; **row 6 supplementary** 10 - 56%; decodes started at 7% and 46%. 8 logical CPUs. Other work on the machine: the owner's DevThrottle Director and several agent sessions |
| Test gate | `dotnet build AgentEyes.sln -c Release`: **0 Error(s)**, 22 warnings. `dotnet test AgentEyes.sln -c Release`: **Passed 2357, Failed 0, Skipped 0** (1 m 26 s) |

## Summary

| # | Check | Result |
|---|---|---|
| 1 | Installs by the supported route | **PASS** - with findings W2 and W3, and observation O2 |
| 2 | Launches; the main window opens | **NOT YET RUNNABLE ON THIS PLATFORM** - Avalonia desktop app, Phase 3 / Phase 6 |
| 3 | The library lists existing recordings | **NOT YET RUNNABLE ON THIS PLATFORM** - same reason |
| 4 | Records the screen, no audio | **PASS** - 703 frames, 23.80 s, decoded clean (package stage hits known #126) |
| 5 | Records screen + microphone, audio matches video | **FAIL** - new defect **W1**: the default mic chain stalls, and the recording never completes |
| 6 | A 10-minute recording completes and is playable | **FAIL** - W1 again: the 10-minute mixed recording never completes. The capture itself has its `moov` and decodes clean. With denoise and leveling off, the same 10 minutes completes and passes every check |
| 7 | Plays a recording back in the app | **NOT YET RUNNABLE ON THIS PLATFORM** - Avalonia desktop app |
| 8 | Detail view, settings and dictionary open and save | **NOT YET RUNNABLE ON THIS PLATFORM** - Avalonia desktop app |
| 9 | Tray icon shows idle and recording | **NOT YET RUNNABLE ON THIS PLATFORM** - Avalonia desktop app |
| 10 | Single instance: a second launch raises the first window | **NOT YET RUNNABLE ON THIS PLATFORM** - Avalonia desktop app |
| 11 | Camera + microphone recording | **FAIL** - **a camera exists here** and recorded 323 real frames, but the recording never completes (W1: one crash, then two stalls) |
| 12 | Always-on keeps the stretches with sound | **BLOCKED** - always-on runs and discards silence, but this machine's only mic delivers no usable signal (floor -96.7 dBFS against a -50 dBFS line). Proving "keeps the stretches with sound" needs a working mic, and that needs the owner's hands |

**The headline for the owner: Windows records, but with default settings it does not finish any
recording that has a microphone in it.** The capture is fine: on the 10-minute take it kept the audio to
99.90% of the video, the `moov` atom is present, and the full decode is clean. Then post-processing hangs.
#128's watchdog catches the hang and its bounded retries give up after 3 attempts, so nothing is
orphaned. But no `recording.mp4` is ever produced. The cause is measured, not inferred (W1): on this
laptop the mic is captured at **44.1 kHz**, and #128's anti-NaN framing only holds at 48 kHz.

## Which build was answering

1. **The installed build on arrival was not main.** `agenteyes-setup status` reported 1.11.11, the
   same version number as main, but the binaries were from 2026-10-11 (`AgentEyesApp.exe` sha256
   `06cef20e...`). The first install was a no-op because of this (W3), so I uninstalled and installed
   clean.
2. **The answering bytes are main's.** The release was built from 93681fa with a clean tree. The sha256
   of every installed exe equals its `dist\release` asset (`windows/row1-install-transcript.txt`), and
   it differs from the pre-session build. The running process is
   `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe`, pid 22104.
3. **One instrument I tried and dropped.** I looked for a log class unique to #128
   (`[MuxCoordinator]`) as a runtime fingerprint. It also appears in older logs, written by #128
   branch builds run on this machine. The old installed app (pid 16936) never post-processed anything,
   so its zero hits proved nothing. The fingerprint could not tell the two builds apart, so I do not
   rely on it. The hash match above is the evidence.
4. Port 7882 is owned by http.sys (pid 4) because the app uses `HttpListener`. So the tie between the
   API and the process is the app's own log lines, `[pid 22104]`, for every request and recording below.

---

## Row 1 - Installs by the supported route: PASS

**Expected:** the documented route for a build you made yourself (README "Building the installer
yourself": `scripts\build-release.ps1`, then `agenteyes-setup install --release-dir dist\release`).
It exits 0, every component installs, the app starts and `/health` answers.

**Actual:**
- `build-release.ps1`: version 1.11.11, five assets plus `release-manifest.json`, 1 m 38 s.
- First `install --release-dir dist\release`: exit 0, `Nothing to do - all components up to date.`
  It installed nothing (see W3).
- I quit the old app through its own quit event (`AgentEyes-quit-16936`, exited 10:44:37), then ran
  `agenteyes-setup uninstall`. **exit 1**: every install-owned item was removed except the bundle
  cache, `Access to the path 'av_libglesv2.dll' is denied` (W2).
- `install --release-dir dist\release --autostart on --desktop-shortcut` at 10:45:00: **exit 0**,
  `installed=4 updated=0 failed=0`. PATH, the extraction directory, the Start Menu shortcut and the
  Add/Remove Programs entry v1.11.11 were all set. `status` reports app, cli and setup-cli at 1.11.11,
  and ffmpeg at 8.1.1.
- Launched `AgentEyesApp.exe --tray` (pid 23196). `/health` answered at 10:45:55.215 with
  `{"ok":true,"app":"AgentEyes"}`, 1.1 s after the start.
- **The app's launch update ran by itself at 10:46:00.** It fetched GitHub `releases/latest`, planned
  `4 components, 0 install, 1 update`, and staged and verified `ffmpeg 8.1.1 -> 9.0.2` before touching
  anything. It stopped pid 23196 through the quit request (`gone=True` in 1 s), swapped ffmpeg and
  relaunched as **pid 22104**. Everything else was untouched: the three exe hashes after the update
  still equal dist. This is O2.

Run-at-login (`HKCU\...\Run\AgentEyes = "...\AgentEyesApp.exe" --tray`), the desktop shortcut and the
Start Menu shortcut were present before, and they are present after.
Evidence: `windows/row1-install-transcript.txt` (status before and after, both installs, the
uninstall, the update cycle verbatim from `setup-cli.log`, all hashes).

## Rows 2, 3, 7, 8, 9, 10: NOT YET RUNNABLE ON THIS PLATFORM

These rows exercise the Avalonia desktop app (`AgentEyesDesktop`). It does not ship on Windows: that is
Phase 3 (the app on Windows) and Phase 6 (retiring the WPF app), and Phase 6 is blocked on an owner
decision. The WPF tray app that ships today is not the one app these rows certify, so I did not
substitute it. Nothing was attempted.

## Row 4 - Records the screen, no audio: PASS

`POST /record/start {"mode":"video","screen":1,"source":"none"}` at 10:47:36.803, stopped at 10:47:58.730.
`DurationSeconds 21.46`. Directory `2026-11-15_104736_video`.

| ffprobe | value |
|---|---|
| streams | **exactly one**: `h264`, 1920x1080, 30/1. No audio stream. That is the presence check: the single stream that exists is video |
| duration | 23.800 s |
| frame count | `nb_frames=703`; decoded with `-count_frames`: `nb_read_frames=703` |
| playable | full decode `ffmpeg -v error -i recording.mp4 -f null -`: exit 0, no output |
| content | I extracted a frame at 12 s and read it. It shows the real desktop (the DevThrottle Director window), and the taskbar clock reads 10:47, which ties the frame to this take. Not committed, because it is the owner's screen |

- **O1 (same effect as Linux O1, stated, not judged):** 23.80 s of video for a 21.88 s take
  (`FfmpegRecorder Stop: pipeline drained in 1546ms (take 21878ms)`). The encoder log shows
  `speed=0.961x` at frame 662 when the stop arrived, and the drain flushed the rest. The 703 frames are
  also 1.5% short of 30 fps x 23.8 s, so gdigrab under-delivered slightly. Load 37 - 57%.
- **#126 (known):** the package stage `failed`, `ffmpeg failed during extract audio (exit -22)`, on a
  recording that has no audio by design. On Linux the same defect gave exit 234. The thumbnail stage
  was `done`.

Evidence: `windows/row4.txt`.

## Row 5 - Records screen + microphone: FAIL (new defect W1)

`POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Microphone (2- Realtek(R) Audio)"}`,
**default mic settings** (denoise on, voice leveling on), at 10:49:15.928. Stopped at 10:51:30.899,
`DurationSeconds 133.91`. Directory `2026-11-15_104916_video`.

**Expected:** a `recording.mp4` whose audio duration matches its video duration.
**Actual: there is no `recording.mp4`.** The mux stage ran three times, and each time it stalled at the
end of the audio:

```
10:52:39.113 [pid 22104] [INFO] [PostRecordingState] NoteFailed: 2026-11-15_104916_video stage=mux error=ffmpeg stalled during process mic audio: output stopped advancing at 135.5s of output for 60s (limit 60s) after 66s - process killed
10:53:43.843 [pid 22104] ... (attempt 2, identical, after 64s)
11:10:49.675 [pid 22104] ... (attempt 3, identical, after 65s)
manifest: "mux": {"State": "failed", "Attempts": 3, ...}
```

The directory holds `raw.mp4`, its ffmpeg log, `manifest.json` and an empty `mux.lock`. Without a
final file, the thumbnail and package stages never ran.

**What the capture kept** (this measures `raw.mp4`, not the deliverable, and does not turn the row into
a pass):

| file | AAC packets | audio (packets x 1024 / 44100) | video | audio / video |
|---|---|---|---|---|
| raw.mp4 (capture) | 5838 | **135.558 s** | 136.300 s | **99.46%** (0.742 s short) |

The capture's audio is **AAC 44100 Hz stereo**: that is the device default on the dshow input, and W1
starts there. The full decode of `raw.mp4` exits 0. Mic level: mean -90.3 dB, max -59.2 dB. The mic
delivers almost nothing (see row 12), so even this measurement proves LENGTH, not content.

Evidence: `windows/row5.txt` (directory, raw probe, manifest, every mux log line, the exact command).

## Row 6 - A 10-minute recording completes and is playable: FAIL (W1)

`POST /record/start {"mode":"video","screen":1,"source":"mixed","mic":"Microphone (2- Realtek(R) Audio)"}`
(screen + mic + system, **default settings**) at 10:57:51.355. `/status` showed `elapsed 616.38` at
11:08:09, then the stop. The app logged `stop: return ... dur=616.6s deferredMux=True`. Directory
`2026-11-15_105751_video`.

**Actual: the recording never completed.**
`11:09:44.116 [pid 22104] ... stage=mux error=ffmpeg stalled during mux video+mic+system: output stopped
advancing at 618.4s of output for 60s (limit 60s) after 79s - process killed`. The same W1 chain is the
mic branch of the mixed filter graph. There is no `recording.mp4`.

**What the capture kept** - the #113 question, answered on the capture file:

| check | `raw.mp4` (capture) |
|---|---|
| top-level atoms | `ftyp` 32, `free` 8, `mdat` 24,852,103, **`moov` 739,970 bytes at offset 24,852,143** (my own atom walk over the file) |
| streams | h264 1920x1080, 619.067 s, 18248 frames; aac **44100 Hz stereo**, 26634 packets |
| audio vs video | 26634 x 1024 / 44100 = **618.440 s** against **619.067 s** (**99.90%**, 0.626 s short) |
| system track | `sys_native.wav` pcm_f32le 48000 Hz stereo, 616.510 s: 2.56 s short of the video (see #121 below) |
| decode | full decode `ffmpeg -v error -i raw.mp4 -f null -`: **exit 0, 0 bytes of stderr**, 25 s, load 7% |

### Row 6 supplementary: the same 10 minutes with denoise and leveling OFF - every check passes

This run isolates the defect. Request: the same body plus `"denoise":false,"level":false`, at 11:14:50.885.
Stopped at 11:25:09.831, `DurationSeconds 617.85`. Directory `2026-11-15_111451_video`. Every stage
finished: mux 27.0 s, thumbnail 0.2 s, package 6.3 s, plugins.

| check | `recording.mp4` (the final file) |
|---|---|
| top-level atoms | `ftyp` 32, `free` 8, `mdat` 27,412,618, **`moov` 728,822 bytes at offset 27,412,658** |
| streams | h264 1920x1080 30/1, 620.167 s, 18171 frames; aac 44100 Hz stereo, 26675 packets |
| audio vs video | 26675 x 1024 / 44100 = **619.392 s** against **620.167 s** (**99.88%**, 0.774 s short) |
| playable | full decode: **exit 0, 0 bytes of stderr**, 28 s, load 46% at the start |
| thumbnail | `thumb.jpg` read: a real frame of the desktop |
| audio content | mean -90.3 dB, max -60.8 dB: both sources near-silent, so the transcript has 11 empty segments |

So on Windows the 10-minute capture, the mux, the `moov` and the playable file all work. The only
thing between the default configuration and a pass is W1.

- **Frame delivery, stated:** 18171 frames over 620.17 s is 97.7% of nominal 30 fps (row 6 capture:
  98.3%). gdigrab drops frames under this load and keeps the timestamps, so the duration is wall time
  while the frame count runs short.
- **#121 (system audio starts late) - recorded, not a failure of this row:** `system.original.wav`
  is 617.75 s against 620.17 s of video, 2.42 s short (the default run: 2.56 s). That is consistent with
  #121's late system start. Nothing was playing, so no offset could be measured.

Evidence: `windows/row6.txt`, `windows/row6-supplementary-denoise-off.txt`.

## Row 11 - Camera + microphone: FAIL (W1)

**A camera exists here,** unlike on devlinux and the Mac mini: PnP `USB2.0 HD UVC WebCam`, status OK,
and `/devices` reports `"cameras": ["USB2.0 HD UVC WebCam"]`.
`POST /record/start {... "source":"mic", "camera":"USB2.0 HD UVC WebCam"}` at 11:27:16.100, stopped at
11:27:39.434. Directory `2026-11-15_112716_video`.

- **Camera capture works:** `[FfmpegCameraRecorder] Stop: ... done, 23.1s ... stopKind=clean-quit
  stderrComplete=True complete=yes`. `camera.mp4` is h264 1280x720, 23.27 s, `nb_read_frames=323`
  (13.9 fps delivered against 30 nominal; a dim room makes a UVC camera lower its rate). I read a frame
  at 10 s: a real, lit room, not blank. Not committed, because it is the owner's home.
- **The recording never completes.** Mux attempt 1 ended with
  `ffmpeg failed during process mic audio (exit -1073741819)`, which is **0xC0000005, an access
  violation: ffmpeg crashed**. Attempts 2 and 3 stalled at 21.6 s (the W1 signature). The compose step
  never ran. The manifest says `"mux": {"State": "failed", "Attempts": 3}`.

Evidence: `windows/row11.txt`.

## Row 12 - Always-on: BLOCKED (no usable mic signal on this machine)

Always-on is Windows-only in the engine, so I ran it. It was OFF on arrival and is OFF again.
`POST /always-on/start` at 11:37:58.250 gave `State listening`, encoder `h264_qsv`, mic
`Microphone (2- Realtek(R) Audio)`, `MicMuted False`, `MicVolumePercent 65`.

- **What works, as presences:** the engine recorded 60 s pieces and judged each one against the auto
  threshold: `levels: mic floor=-96.7dBFS line=-50.0dBFS (auto)`. The keeper deleted each silent piece
  (`keeper: DELETE piece_20261115-163801.mp4 (60s, no sound near it)`, 4 pieces), and the status
  reports `3 m of silence discarded`.
- **Why it is blocked, not failed:** the half that matters, "keeps the stretches WITH sound", needs
  sound above -50 dBFS at the mic. This mic's floor is -96.7 dBFS, and every take today measured
  -85 to -91 dB on average. I played a 3 s 1 kHz tone through the speakers at 11:39:16.870. The
  60 s window containing it moved the mic peak from -78.5 to **-69.7 dBFS**: the mic heard it, about
  20 dB below the line. No clip was kept, and that is correct behavior for this signal. To prove the
  keep path, this machine needs a working mic (the Realtek input looks like an empty jack). That needs
  the owner's hands.

Evidence: `windows/row12.txt`.

---

## #128's fixes on Windows - what this run shows

| fix | seen here | evidence |
|---|---|---|
| progress watchdog: a stalled stage fails loudly | **YES, presence**: `output stopped advancing at 135.5s of output for 60s (limit 60s) ... process killed`, 8 times across rows 5, 6 and 11 | row5.txt, row6.txt, row11.txt |
| bounded retries | **YES**: each failed recording stops at `Attempts: 3`, `State: failed` | manifests |
| never orphans a partial output | **YES**: a killed attempt's partial `recording.mp4` was seen mid-run (row 5: 5,767,216 bytes at 10:51:37; row 11: 786,480 bytes at 11:27) and is gone afterwards; no `recording.mp4` remains in any failed directory | row5.txt, row11.txt directory listings |
| cross-process mux lock | acquire, re-enter (depth 2) and release on every attempt, by pid 22104. One process only, so contention was **not** exercised | row5.txt |
| NaN avoidance (`asetnsamples=n=480:p=1` before arnndn) | **DEFEATED on a 44.1 kHz mic** - that is W1 | repro-mic-chain-stall.txt |
| layout pin for MONO mic + STEREO system (the segfault) | **NOT EXERCISED, and not exercisable here.** On Windows the video recorder takes the mic from dshow at the device default, which on this Realtek is **stereo 44.1 kHz** in both mic and mixed mode (`AudioChannels: raw.mp4 -> 2`). The mono 48 kHz path (`AudioCapture`) is not the one video recordings use. I used the only mic this machine has. But note that row 11's first attempt **crashed with an access violation in a mic-only STEREO chain**, a configuration #128's notes say "never crashed" | row11.txt |

---

## New defects (handed to the lead, not filed)

**W1 - The default mic chain stalls, and once crashed, whenever the mic is not captured at 48 kHz. No
default recording with a microphone completes on this machine.**
- **Where:**
  - The mic chain, `src/AgentEyes.Core/Video/FfmpegArgs.cs:370` (`denoise = $"{ArnndnFraming},arnndn=..."`),
    with `ArnndnFraming = "asetnsamples=n=480:p=1"` at `FfmpegArgs.cs:420`. Nothing in `MicChain`
    (`FfmpegArgs.cs:361-399`) brings the stream to 48 kHz before the framing.
  - The capture, `FfmpegArgs.cs:57-67`: `-f dshow ... -i "audio=<mic>"` with no `-sample_rate`, so the
    device default comes through. Here that is 44100 Hz stereo.
- **Mechanism, measured:** arnndn accepts only 48 kHz. With a 44.1 kHz input, ffmpeg inserts its own
  resampler between `asetnsamples` and `arnndn`, and #128's padded 480-sample framing is undone before
  the frames reach arnndn. The final partial frame then comes out as NaN (the 9.0.2 behavior #128
  documented), and speechnorm spins forever.
- **Proof** (`windows/repro-mic-chain-stall.txt`). The app's exact command, outside the app, on its own
  captures:

  | input | ffmpeg 9.0.2 shipped chain | 8.1.1 shipped chain | 9.0.2 + `aresample=48000` before `asetnsamples` |
  |---|---|---|---|
  | row 5 raw, 44.1 kHz | stalls at 2:15.50 (exit 124) | completes | completes in 3 s |
  | row 6 raw + system, 44.1 kHz | stalls at 10:18.35 (exit 124 at 150 s) | completes, 16 s | completes, 15 s |
  | row 11 raw, 44.1 kHz, 10 runs each | **10 / 10 stall** | 10 / 10 complete | 10 / 10 complete |
  | row 11 raw re-encoded to **48 kHz** first, shipped chain unchanged, 10 runs | **10 / 10 complete** | - | - |

  The last row isolates the variable: the same audio, the same chain and the same ffmpeg complete at
  48 kHz and stall at 44.1 kHz.
- **Why it matters:** **every default recording with a mic** (mic, mixed, camera+mic) on a machine
  whose mic defaults to 44.1 kHz never produces a `recording.mp4`. The user sees a failed recording
  after a 3 x 60 s watchdog cycle. ffmpeg 9.0.2 is what users run: the published release bundles it,
  and the app's launch update installs it even over a locally built 8.1.1 (O2). 44.1 kHz is a common
  Realtek default. The machines #128 was measured on presumably delivered 48 kHz.
- **Also seen, not reproduced:** one in-app attempt ended with 0xC0000005 (access violation) instead
  of a stall, on a mic-only stereo chain. It did not recur in 10 standalone runs.
- **Not covered:** I did not test a fix inside the app. The resample-first variant is evidence about
  the mechanism, not a proposed patch, and the 48 kHz capture route (`-sample_rate 48000` on the dshow
  input) was not tried.

**W2 - The installer redirects EVERY .NET single-file app's native extraction into AgentEyes' folder,
and uninstall then fails on their files.**
- **Where:** `tools/AgentEyes.Setup.Engine/InstallFinalizer.cs:93-107` sets
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` as a **user-scope** environment variable (`UserEnvironment.Set`,
  line 106), so it applies to every process the user starts, not only to AgentEyes.
- **Observed:** `%LOCALAPPDATA%\AgentEyes\bundle\` holds extraction folders for `cc-director`,
  `cc-launcher` and `devthrottle-gateway` (each with its own `av_libglesv2.dll`). `agenteyes-setup uninstall`
  then exits **1**: `ERROR: Native extraction cache (...\AgentEyes\bundle): Access to the path
  'av_libglesv2.dll' is denied`, because those three apps are running and hold their DLLs open. The
  uninstall also removes the variable while those apps keep using the folder.
- **Why it matters:** the #120 fix (the extraction folder must not live in %TEMP%) is right for
  AgentEyes. But it changes how unrelated software behaves, it makes AgentEyes' uninstall unable to
  finish cleanly, and a future uninstall that did succeed would delete files other running apps depend
  on. The code comment says it "touches no other user environment variable". That is true, but the one
  it touches is global in effect. A per-process route (an app-local runtimeconfig, or a launcher setting
  the variable only for AgentEyes' own processes) would not have this reach. That is a suggestion for
  the lead, not something I tested.

**W3 (finding about the documented route) - `agenteyes-setup install --release-dir` will not replace a
different build that carries the same version.**
- **Where:** `tools/AgentEyes.Setup.Engine/UpdatePlanner.cs:43` updates only
  `if (VersionUtil.IsNewer(asset.Version, fromVersion))`.
- **Observed:** the installed binaries were from 2026-10-11 and main's build differs in every byte,
  but both say 1.11.11. The README route "Building the installer yourself" returned
  `Nothing to do - all components up to date.`, exit 0, and the old build kept running. A tester or
  developer following the README believes they are on their build when they are not. This is exactly
  the stale-binary trap the project instructions warn about, reached through the installer. Comparing the asset sha256 that
  the manifest already records would close it.

## Observations (stated, not judged)

- **O1** - video runs longer than the take on short takes (row 4: 23.80 s for 21.88 s), the same effect
  as Linux O1. gdigrab delivers 97.7 - 98.5% of nominal frames under 30 - 57% load.
- **O2** - a locally built release does not stay as built. The app's launch update compares against
  GitHub `latest` and "upgraded" the bundled ffmpeg 8.1.1 (what `build-release.ps1` took from this
  machine's winget) to the published 9.0.2 within 6 s of the first launch. For this report that is the
  right ffmpeg, because it is what users run. But anyone testing a local build with a different ffmpeg
  is silently testing another one.
- **O3** - before the session, `%LOCALAPPDATA%\AgentEyes\app\recordings\` held two recordings
  (2026-11-07). `uninstall --dry-run` lists the whole `app` directory as install-owned, under the
  heading "your data is preserved", so uninstall would have deleted them. I copied them out first and
  put them back byte-identical (sha256 checked). How recordings came to be inside the install
  directory is not known.
- **Mic content** - every take measured -85 to -91 dB mean. Rows 5 and 6 measure track LENGTH and
  presence, not intelligibility or sync, exactly as on Linux.

## Known defects met, cited and not re-raised

- **#126** - row 4's package stage fails on a no-audio recording (`extract audio`, exit -22 on Windows).
- **#121** - the system track is 2.42 s (supplementary) and 2.56 s (default run) short of the video;
  nothing was playing, so the offset itself is unmeasurable.
- **#143** - not met, and **not exercised**. This session's log part has 2377 lines, all well-formed
  (`^HH:MM:SS.mmm [pid N]`), and the same grep fires on the Linux truncated line. But the two app
  processes (23196, then 22104) never ran at the same time, so there were never two concurrent writers.
  The zero says nothing about #143 (`windows/log-scan.txt`).
- **#113** - not met: both 10-minute files have their `moov` atom (the capture in row 6 and the final
  file in the supplementary run).
- **#114** - not met at capture: the audio is 99.46 - 99.90% of the video. The final-file measurement
  exists only for the supplementary run (99.88%), because W1 prevents a default final file.

## What this report does NOT cover

- **macOS**, and the **Linux** rows: this is the Windows report only.
- **The desktop app rows (2, 3, 7, 8, 9, 10)** - not runnable on Windows yet.
- **The mono-mic plus stereo-system configuration** - this machine produces only a stereo 44.1 kHz mic
  in video recordings, so #128's layout pin was not exercised.
- **Mux-lock contention between processes** - only one process ran post-processing.
- **The WPF tray UI** - not part of Wave A, and nothing here drove it.
- **Audio content and sync** - both sources were near-silent.

## State of SORENLAPTOP after this run

- **Installed build: main 93681fa** (app pid 22104, running, `--tray`), with ffmpeg 9.0.2. The
  pre-session build was an older same-version 1.11.11 from 2026-10-11. I did not reinstall it, because
  it lacks #128 and every earlier fix. Its three exes are kept at
  `%LOCALAPPDATA%\AgentEyes\qa142-preserved\pre-session-exes\`, and their hashes are in the row 1
  transcript. The setup engine's own `.old` copies were removed by the uninstall.
- Run-at-login, the desktop shortcut, the Start Menu shortcut, the PATH entry and
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` are as they were.
- `%LOCALAPPDATA%\AgentEyes\app\recordings\` was restored byte for byte. A safety copy is left at
  `%LOCALAPPDATA%\AgentEyes\qa142-preserved\` and nothing deletes it.
- Always-on: OFF, as found.
- `%USERPROFILE%\Videos\AgentEyes`: nothing deleted. Five recordings added: `2026-11-15_104736_video`
  (row 4), `_104916_video` (row 5, mux failed), `_105751_video` (row 6, mux failed),
  `_111451_video` (row 6 supplementary, complete) and `_112716_video` (row 11, mux failed). Each
  failed one is at `Attempts: 3` and will not be retried. Its `raw.mp4` is intact for whoever fixes W1.
- No process of mine is left running. All reproduction output went to a scratch folder outside the repo.

## Evidence files (`docs/cencon/proof/issue-142/windows/`)

`row1-install-transcript.txt`, `row4.txt`, `row5.txt`, `row6.txt`, `row6-supplementary-denoise-off.txt`,
`row11.txt`, `row12.txt`, `repro-mic-chain-stall.txt`, `log-scan.txt`. No images are committed. The
frames I read (row 4, the row 6 thumbnail, the row 11 camera) show the owner's screen and home, and
each one is described in its row instead.
