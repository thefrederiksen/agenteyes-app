# Issue #142 - QA report, WINDOWS (SORENLAPTOP) - Wave A, the recorder half

This is the **Windows** platform report, the second of three. It covers **Wave A only**: the engine and
the Control API (rows 1, 4, 5, 6), plus rows 11 and 12, which turned out to be runnable here. Rows 2, 3, 7,
8, 9 and 10 belong to the Avalonia desktop app, which does not ship on Windows yet. The Linux report is
`qa-report-linux.md`. The macOS report is not part of this one and was not run here.

> **Rows 4, 5 and 6 were re-run on 2026-11-16 against main b0e6ae8 (#113 and #156 merged).** See the
> section "Re-run of rows 4, 5 and 6 on main b0e6ae8" at the end of this report: row 4 **PASS**, row 5
> **BLOCKED** (this laptop's mic records silence), row 6 **PASS** under the sharpened criterion (duration
> against the expected take length plus frames decoded at the expected end, with a truncation control
> that fails). The
> 2026-11-15 results below are kept unchanged as the record of main 93681fa.
>
> **Row 5 was then measured on a working microphone** (the owner's Windows desktop, main b0e6ae8):
> **PASS, 2 of 2 takes**, audio 0.487 s and 0.683 s short of the video, mean volume -45.2 and -64.3 dB.
> See the last section, "Row 5 on a working microphone".

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

---

## Re-run of rows 4, 5 and 6 on main b0e6ae8 (2026-11-16)

**Why it was re-run.** #113 (merged as b0e6ae8) changes the container of every recording: the capture
is now fragmented MP4, and a capture watchdog ends a stalled capture. #156 (d0d0a27) makes a capture
hold a claim (`capture.lock`) that a second process must respect. #146 (497e9d4) resamples the mic to
48 kHz ahead of arnndn, which is the fix for W1 above. Rows 4-6 measured on 93681fa say nothing about
any of these.

| | |
|---|---|
| Machine | SORENLAPTOP, as above (i5-1135G7, 8 logical CPUs, two 1920x1080 monitors) |
| Date | 2026-11-16, 15:57 - 16:31 EST |
| Commit under test | `main` at **b0e6ae8**, clean tree, built with `scripts\build-release.ps1` |
| Embedded commit of the binaries that ran | **1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb** (ProductVersion of the installed `AgentEyesApp.exe`, `agenteyes.exe` and `agenteyes-setup.exe`) |
| Process answering | `%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe --tray`, **pid 19972**, started 16:01:27 by the app's own launch update. Every log line quoted carries `[pid 19972]`. Before each row there was no other AgentEyes process and no ffmpeg running |
| ffmpeg in use | **9.0.2** (gyan.dev essentials, sha256 `3256173f...`), put in place by the launch update (O2 again) |
| Test gate | `dotnet build AgentEyes.sln -c Release`: **0 Error(s)**, 24 warnings. `dotnet test AgentEyes.sln -c Release`: AgentEyes.Tests **2523 passed, 0 failed, 16 skipped** (the opt-in ffmpeg tests), and AgentEyes.Tests.DesktopApp **23 passed**. SDK 10.0.301 building net8.0, runtime 8.0.21. The gate ran before the rows, never at the same time |
| Settings | **Defaults**: 30 fps, denoise on, leveling on. No low frame rate was used, so #184 and #188 are not inside these rows |

### Summary of the re-run

| # | Check | Result |
|---|---|---|
| 4 | Records the screen, no audio | **PASS** - 967 frames, 32.50 s, one video stream, full decode exit 0, frame read 2 s from the end |
| 5 | Records screen + microphone, audio within 1.5 s of video | **BLOCKED on this machine** - the mic records silence (a hardware fault). The audio track's LENGTH is 0.723 s short of the video, and the default chain now completes (W1 is gone). But content and sync cannot be measured from a silent mic |
| 6 | A 10-minute recording completes and is playable | **PASS** (sharpened criterion) - duration 613.400 s against an expected 613.057 s (0.343 s off, tolerance 2 s), **60 frames decoded in the last 2 s of the expected length**, every post-processing stage `done` at attempt 1. The same check **FAILS on all three truncated copies** |

### Which binary was installed, and which one ran (limit 1)

- **On arrival the installed app was 93681fa, which is pre-#156.** `AgentEyesApp.exe`, `agenteyes.exe`
  and `agenteyes-setup.exe` all read `1.11.11+93681fa5e163624b2098c17f0acd3fc8a49a19c1` (the build the
  2026-11-15 run installed). It was running as pid 16160 (`--tray`, started 2026-11-15 13:37) and was
  idle. A row measured against it would not have been a row against main, and that build ignores
  `capture.lock`.
- **I reinstalled, from `dist\release` built from b0e6ae8.** `agenteyes-setup plan --release-dir` still
  reports every component `UpToDate` for a same-version build (W3, unchanged). So I used the same
  supported route as the 2026-11-15 run:
  1. Quit pid 16160 through its own quit event.
  2. `agenteyes-setup uninstall`: **exit 1** on the bundle cache (W2, unchanged).
  3. `install --release-dir dist\release --autostart on --desktop-shortcut`: **exit 0**, `installed=4`.

  The two recordings inside `%LOCALAPPDATA%\AgentEyes\app\recordings\` (O3) were copied out first and
  restored afterwards. All six files are sha256-identical before and after.
- **The installed bytes are the main build.** The sha256 of each of the three exes equals its
  `dist\release` asset, and each embeds b0e6ae8. The launch update then replaced ffmpeg 8.1.1 with
  9.0.2 and relaunched the app as pid 19972. It touched nothing else (`binary-identity.txt` has
  setup-cli.log verbatim).
- **After this run the installed app is still main b0e6ae8**, running as pid 19972. I did not put
  93681fa back, because it lacks #113, #146 and #156.

### Row 4 - Records the screen, no audio: PASS

`POST /record/start {"mode":"video","screen":1,"source":"none"}` at 16:04:23.255, stopped at 16:04:55.294
(take 30.9 s, `Stop: pipeline drained in 98ms ... full take captured`). Directory
`2026-11-16_160423_video`.

**Expected:** a recording with exactly one stream, a video stream, that decodes cleanly, with its
ffprobe duration and frame count.
**Actual:**

| check | `recording.mp4` |
|---|---|
| container | **fragmented**, as #113 intends: `ftyp`, then `moov` (769 bytes at offset 28, the index written up front), then **19 `moof`+`mdat` pairs** and an `mfra`. My box walk ends exactly at the file size (837,893) |
| streams | **exactly one**: h264 1920x1080 30/1. The positive control for "no audio" is the same ffprobe call on rows 5 and 6, which reports an `aac` stream in each |
| duration | 32.500 s |
| frame count | `nb_read_frames=967` (decoded with `-count_frames`), 99.2% of 30 fps x 32.5 s |
| full decode | `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0, 0 lines of stderr** |
| frame near the end | `-sseof -2 ... -frames:v 1`: exit 0. I read it: the real desktop, with the taskbar clock at **4:04 PM 2026-11-16**, which ties it to this take. Not committed, because it is the owner's screen |

- **#113 and #156, as presences:**
  - The claim was taken: `CaptureLock TryAcquire: ... claimed by capture session of pid 19972`.
  - The writer was recorded: `NoteWriter: ... video pid 17268`. That was the only ffmpeg running, and
    it was a child of 19972.
  - The watchdog was armed: `StartWatchdog: watching ...\recording.mp4 (stall 45s, start-up 45s, every 2s)`.
  - Every `/status` sample after the first showed `CaptureLiveness=growing` and no `LastCaptureFault`.
  - The watchdog was stopped (`StopWatchdog`, 16:04:55.350) before the writer stopped, and the claim
    was released at 16:04:56.080.
- **#126 (known):** the package stage `failed` with `extract audio (exit -22)`, on a recording that has
  no audio by design. The thumbnail stage was `done`.

Evidence: `windows/rerun-b0e6ae8/row4.txt`.

### Row 5 - Records screen + microphone: BLOCKED on this machine

`POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Microphone (2- Realtek(R) Audio)"}`
at 16:15:05, stopped at 16:16:38.387. Default mic settings (denoise on, leveling on). Directory
`2026-11-16_161505_video`.

**Why blocked, not failed:** this laptop's only microphone delivers silence, with a -96.7 dBFS floor
(2026-11-15, row 12). That is a hardware fault, not a product defect.
- The final file's audio measures mean **-68.5 dB** and max -49.5 dB, after leveling. The capture
  measures mean -80.3 dB.
- The row asks whether the audio lines up with the video. On a track that carries no signal, sync
  cannot be measured, so reporting this silence as a product failure would be wrong.
- Row 5 needs a working mic on another Windows machine.

**What was measured, as a supplement and not a pass:**

| file | video | audio | audio short of video |
|---|---|---|---|
| `recording.mp4` (final) | 93.633 s, 2752 frames | aac **48000 Hz** stereo, 92.910 s, 4357 packets | **0.723 s** (within 1.5 s) |
| `recording.original.mp4` (the capture) | last packet ends at 93.633 s, 2752 frames | aac 44100 Hz stereo, 92.887 s | 0.746 s |

- **W1 no longer reproduces at default settings.** On 93681fa this exact request never produced a
  `recording.mp4` (three stalled mux attempts). On b0e6ae8 the manifest shows `mux done/1` at
  **16:16:43.271, 4.0 s after the stop returned** (16:16:39.268).
  - I followed `PostProcessing.mux.State` in the manifest, not `/status`, which said `idle` at 16:16:39.331.
  - The final file is 48 kHz, so the #146 resample is in the chain.
  - Thumbnail, package and plugins are all `done/1`.
  - Both files decode to exit 0 with no stderr, and the capture is fragmented (56 `moof`+`mdat` pairs).
- The title stage logged `transcript is empty; nothing to title`. That is the silent mic again.
- **#157 (audio drift under load) was NOT exercised.** I did not count dropped audio samples. I only
  measured track lengths, on one take at 23 - 42% load. A clean length on one take does not retire a
  defect that only bites under load, and the same applies to row 6's 0.900 s.

Evidence: `windows/rerun-b0e6ae8/row5.txt`.

### Row 6 - A 10-minute recording completes and is playable: PASS

**The criterion, sharpened after the Linux seat's truncation control.** Two earlier forms of this check
turned out to be vacuous.
- "The `moov` atom is present" passes on a truncated fragmented file, because the index is written up
  front (row 4's `moov` sits at offset 28).
- "A full decode to exit 0 plus a frame read near the end" also passes on a truncated file. ffmpeg
  decodes whatever is there and exits cleanly, and "the end" is then the end of the surviving bytes.
  **I measured this here** (below): on a copy cut to 50%, the full decode exits 0 and a frame reads
  2 s from the end of the data.

The form used, which has teeth:
1. **The duration matches the EXPECTED take length.** The expected length is taken from my driver's
   own clock: start request 16:17:39.180 to stop request 16:27:52.237 = **613.057 s**. The tolerance
   is 2 s.
2. **Frames decode AT that expected end.** At least 40 decoded video frames (20 fps) must have a pts
   inside the last 2 s of the expected length, [611.057, 613.107] s.
3. **A truncation control.** The same check is run on truncated copies, and it must FAIL on them.

The full decode is still run and reported, but it no longer decides anything.

`POST /record/start {"mode":"video","screen":1,"source":"mixed","mic":"Microphone (2- Realtek(R) Audio)"}`
(screen + mic + system, defaults) at 16:17:39.58, stopped at 16:27:52.237 (take **611.95 s**,
`Stop: pipeline drained in 273ms ... full take captured`). Directory `2026-11-16_161739_video`.

| check | `recording.mp4` (the deliverable) | `recording.original.mp4` (the capture) |
|---|---|---|
| completes | manifest: `mux done/1`, `thumbnail done/1`, `package done/1`, `plugins done/1` | - |
| container | `ftyp`, `free`, `mdat`, then `moov` (671,030 bytes) at the end: the mux writes a plain MP4 | **fragmented**: 360 `moof`+`mdat` pairs and an `mfra`. The walk ends at the file size |
| video | h264 1920x1080, **613.400 s**, `nb_read_frames=17968` | 17968 frames, last packet ends at 613.400 s |
| audio | aac 48000 Hz stereo, **612.500 s** (0.900 s short of the video) | aac 44100 Hz, 612.490 s |
| **full decode** | `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0, 0 lines of stderr**, 24.1 s | **exit 0, 0 lines of stderr**, 25.2 s |
| frame near the end | `-sseof -2 ... -frames:v 1`: exit 0, a 982,119-byte PNG. I read it: the real desktop, taskbar clock **4:27 PM 2026-11-16**, inside the last seconds of a take that ran 16:17:39 - 16:27:52 | exit 0, the same frame |
| **duration vs expected 613.057 s** | **613.400 s, 0.343 s off -> OK** | 614.100 s, 1.043 s off -> OK |
| **frames decoded in [611.057, 613.107] s** | **60 (need >= 40) -> OK**, last decoded pts 613.367 | 60 -> OK, last decoded pts 613.367 |
| **verdict of the sharpened check** | **PASS** | **PASS** |

**The truncation control: the same check on copies cut short.** The copies were made in a scratch
folder, and the recordings themselves were not modified.

| truncated copy | duration (vs 613.057) | frames in the last 2 s of the expected length | full decode | sharpened check | previous form (decode exit 0 + frame 2 s before the end of the data) |
|---|---|---|---|---|---|
| capture, 98% of its bytes | 600.067 s (12.99 s off) | **0** | exit 0 | **FAIL** | decode passes; the frame read failed (exit 69) -> would fail |
| capture, 50% of its bytes | 307.333 s (305.7 s off) | **0** (last decoded pts 306.7) | exit 0 | **FAIL** | **decode exit 0 and a frame read -> would PASS a half-length file** |
| final file, 98% of its bytes | unreadable (`error reading header`: its `moov` is at the end) | 0 | exit -541478725 | **FAIL** | fails |

The last line of the first table is the one that matters. The check that passes the real recording
fails every truncated copy. The previous form passed the 50% copy, so a row 6 pass under it would not
have meant anything.

- **Completion was followed on disk, not on `/status`** (#159). `/status` said `idle` at 16:27:53.292.
  The mux finished at **16:28:29.190**, **35.95 s after the stop returned**. The thumbnail followed at
  16:28:29.633, the package at 16:28:41.433 and the plugins at 16:28:41.449. Load during the mux was
  28 - 48%.
- **The watchdog during a healthy 10 minutes, presence and absence.**
  - Presence: it was armed at 16:17:40.368 (`stall 45s, start-up 45s, every 2s`).
  - 55 `/status` samples were logged, about one every 11 s, and `CaptureBytes` climbed to 25,561,151.
    The first sample said `starting` (0.93 s in). 53 said `growing`. **One said `not-growing`**:
    92.44 s in, `CaptureSecondsSinceGrowth=2`, at the 2 s fragment cadence and far below the 45 s
    stall limit. The next sample, 11 s later, said `growing`.
  - The largest `CaptureSecondsSinceGrowth` in any sample was 2.
  - The watchdog stopped at 16:27:52.282, before the writer was stopped.
  - Absence: `LastCaptureFault` was empty throughout and after the stop, and pid 19972 logged no
    `OnWatchdogTick` line all day. There were **0 WARN/ERROR lines from pid 19972 between 16:17 and
    16:29**. That grep is not blind: the same pattern returns 1268 INFO lines from pid 19972 in the
    same window, and it does find the #126 errors at 16:04 and 16:16.
- **The claim (#156):** `capture.lock` existed in the directory at every sample (`lockfile=True`). The
  claim was taken by pid 19972 and the writer was ffmpeg pid 23196. It was released at 16:27:53.238,
  before post-processing claimed the directory. The zero-length `capture.lock` file stays in the
  directory after release, which the code documents as intended.
- **Frame delivery, stated:** 17968 frames over 613.4 s is 29.29 fps, 97.6% of nominal. Load was
  23 - 55% during the take, from the owner's Director and agent sessions.
- **#121 (system audio starts late), recorded and not this row's failure:** `system.original.wav` is
  611.87 s, 1.53 s short of the video. Nothing was playing, so the offset itself cannot be measured.

Evidence: `windows/rerun-b0e6ae8/row6.txt`, `windows/rerun-b0e6ae8/row6-endcheck.txt` (the check
script, both real files and the three truncated copies).

### The empty window (limit 2), measured on this machine

The empty window is the period in which an unclean end leaves nothing in the capture file. **Measured on
SORENLAPTOP: 2.98 s and 3.09 s from capture ffmpeg start to the first byte in the file, at 30 fps on
4 cores / 8 logical CPUs**, load 8%.
- **Method:** two extra screen-only takes at defaults (`2026-11-16_164005_video`, `_164035_video`,
  20 s each). I polled the capture file's length every 50 ms, read-only, and timed against the ffmpeg
  process's creation time. That ffmpeg was pid 18728, then pid 4136, each a child of 19972.
- **What the file did:** it stayed at 0 bytes, then jumped to about 175 KB at once. That is the first
  fragment together with the `moov` (delay_moov). It then grew about every 2 s, at the fragment
  cadence.
- **No row ends inside this window**, so it decides no verdict here.
- **This is my machine's figure, at this core count and frame rate,** and it is not a platform
  constant: #188 found the window scales with core count and inversely with frame rate. The 5 fps and
  1 fps figures were not exercised here, because no low frame rate was run.
- **Limits of the measurement:** two takes, screen only. The window for a capture with audio was not
  timed separately.

Evidence: `windows/rerun-b0e6ae8/empty-window.txt`.

### Observations from the re-run (stated, not judged)

- **O4 - the package stage's Attempts count does not grow on resume passes.** Row 4's package stage
  (#126) failed three times in the log: at 16:04:56.507, at 16:04:56.806 (a `resume` pass), and at
  16:16:47.083 (a later pass after row 5's stop). The manifest still says `package failed/1`. Either
  resume passes are deliberately uncounted, or the retry bound is not enforced on them. I did not read
  the code to decide which.
- **W2 and W3 from 2026-11-15 are unchanged on b0e6ae8.** The uninstall still exits 1 on another app's
  `av_libglesv2.dll`, and a same-version build is still reported `UpToDate`.
- **O2 is unchanged.** The launch update replaced the locally built ffmpeg 8.1.1 with 9.0.2 within 25 s
  of the first launch.

### What this re-run does NOT cover

- **Row 5's content and sync.** The mic is silent, so nothing here proves that speech lines up with the
  picture on Windows.
- **The failure paths #113 exists for.** These were healthy takes. No capture was killed, frozen or
  starved, so I did not see the watchdog end a capture, and I did not decode a truncated fragmented
  file. What these rows prove about #113 is narrower: it does **not** disturb a healthy recording at
  default settings, which was the open question this morning.
- **#156 against a second process.** I saw one process take, hold and release the claim. No second
  process tried to touch a live capture, so I did not see the claim respected.
- **Low frame rates (#184, #188).** Deliberately not run.
- **Repetition.** One take per row. On 2026-11-15, W1 included a fault seen only once (the 0xC0000005),
  and one healthy take cannot rule out an intermittent fault.
- **The desktop-app rows, row 11 (camera) and row 12 (always-on).** Not part of this re-run.
- **macOS and Linux.** This is the Windows half only.

### State of SORENLAPTOP after the re-run

- **Installed:** main b0e6ae8 (the exes embed `1.11.11+b0e6ae82...`) with ffmpeg 9.0.2. The app is
  pid 19972, running `--tray`, idle. Run-at-login, the desktop and Start Menu shortcuts, the PATH entry
  and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` are set as before. The pre-session exe hashes are recorded in
  `binary-identity.txt`.
- **`%LOCALAPPDATA%\AgentEyes\app\recordings\`** was restored byte for byte. A copy is left at
  `%LOCALAPPDATA%\AgentEyes\qa142-preserved\app-recordings-20261116\`.
- **`%USERPROFILE%\Videos\AgentEyes`: nothing deleted.** Five recordings were added:
  - `2026-11-16_160423_video` (row 4)
  - `_161505_video` (row 5)
  - `_161739_video` (row 6)
  - `_164005_video` and `_164035_video` (the empty-window takes, 20 s each)

  All five are complete, apart from the package stage on the screen-only takes (#126). The truncated
  copies for row 6's control were made in a scratch folder outside the library and the repository.
- **Nothing else was touched.** No ffmpeg was killed, and no preset or audio setting was changed. The
  owner's library was used only through the real product. No process of mine is left running.

Evidence files (`docs/cencon/proof/issue-142/windows/rerun-b0e6ae8/`):
- `binary-identity.txt`: the arrival state, the build, the reinstall transcript, the launch update and
  the gate.
- `row4.txt`, `row5.txt`, `row6.txt`: the driver transcript, the analysis, the final manifest, app log
  excerpts and the directory listing.
- `row6-endcheck.txt`: the sharpened check and the truncation control.
- `empty-window.txt`: the first-byte timing.

No images are committed. The three end frames I read show the owner's screen, and each is described in
its row.

---

## Row 5 on a working microphone: the owner's Windows desktop, main b0e6ae8 (2026-11-16)

The laptop could not measure row 5 because its microphone records silence. This section adds the
measurement from the second Windows machine, whose microphone works. It is the only Windows measurement
of row 5 with a real signal on the track.

**Row 5 - Records screen + microphone, audio within 1.5 s of video: PASS, 2 of 2 takes.**

| | |
|---|---|
| Machine | The owner's working Windows desktop: Windows 11 Enterprise 10.0.26200, Intel i7-13700 (24 logical CPUs), three monitors. Screen 1, the API default, is a 1920x1080 secondary monitor |
| Date | 2026-11-16, 16:40 - 16:45 EST |
| Commit under test | `main` at **b0e6ae8**, clean worktree, `dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release` (0 errors) |
| Binary that ran | `agenteyes.exe serve --port 7894` from that build, embedded **1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb**. One serve process per take (pid 68796, then pid 86472), started and stopped by the seat. It was NOT installed |
| ffmpeg | the installed app's **9.0.2**, used read-only via `QA_RECORD_FFMPEG_DIR` (no ffmpeg is next to a dev build) |
| The owner's installed app | **left untouched and idle throughout** (port 7882, `State=idle` before every take). Embedded commit **1.11.11+01ada189948e03a6acd858889ea255ca4066c78e** (`01ada18`, "Release v1.11.11"), running since 2026-11-13. That is **pre-#146 and pre-#156**, so it ignores `capture.lock`, and nothing about cross-process behaviour can be read from it. (The brief expected 93681fa. The binary on disk says 01ada18) |
| Settings | **API defaults**: `{"mode":"video","source":"mic","mic":"Microphone (FDUCE SL40 Audio Device)"}`. That means 30 fps, denoise on, leveling on, the gate at its default for a mic source, mic gain 100%. The mic had to be named: the headless host refuses a mic source without one (`mic is required for this source`). I named the mic the owner's own presets use (five of them), and changed nothing in his audio settings, presets or default device |
| Load | Windows `LoadPercentage` during the takes: take 1 37 - 99%, take 2 40 - 83% (the owner's Director and agent sessions were running) |
| Test gate | Not run. This is a docs-only change, and the brief said not to run the full suite on this machine |

### The two takes

Each take ran for 65 s of wall clock at default settings. I followed post-processing in the manifest's
`PostProcessing.mux.State`, not `/status`. `/status` said `idle` within 0.03 s of the stop returning,
before the mux had finished (#159).

| | take 1 (`2026-11-16_164121_video`) | take 2 (`2026-11-16_164336_video`) |
|---|---|---|
| Stop response `DurationSeconds` | 67.85 | 67.69 |
| Manifest `DurationSeconds` / `CapturedSeconds` | 68.67 / 68.56 | 68.53 / 68.43 |
| **Video**, `recording.mp4` (ffprobe stream duration; decoded frames) | **68.667 s**, 1942 frames (`nb_frames` = `nb_read_frames` = 1942) | **68.533 s**, 1910 frames (1910 = 1910) |
| **Audio**, `recording.mp4` (ffprobe stream duration) | **68.180 s**, aac 48000 Hz stereo, 3197 packets | **67.850 s**, aac 48000 Hz stereo, 3182 packets |
| Last packet end, video / audio | 68.667 / 68.180 s | 68.533 / 67.850 s |
| **Audio short of video** | **0.487 s** (within 1.5 s) | **0.683 s** (within 1.5 s) |
| Same, in the capture `recording.original.mp4` (44100 Hz) | 0.500 s (68.667 vs 68.167 last packet end) | 0.687 s (68.533 vs 67.846) |
| Audio packet gaps > 50 ms, capture and final | **0 and 0** | **0 and 0** |
| Full decode of `recording.mp4` | exit 0, no stderr | exit 0, no stderr |
| Mux | `done/1`, 4.87 s after the stop was sent | `done/1`, 3.75 s after the stop was sent |

**The numbers sit inside the range the other machines measured.** The laptop's silent take was 0.723 s
short, and Linux measured 0.095 s and 0.080 s on main. On Windows the audio track consistently ends
0.5 - 0.7 s before the video. That is within the criterion. It is also about five to eight times the
Linux figure. I am stating it as an observation, not a defect: the shortfall is already present in the
capture (`recording.original.mp4`), so it is not introduced by the mux. Nothing at default settings
resembled #184's 4.5 s.

### The audio has content - this is not the laptop's silence

| | take 1 | take 2 | laptop (for comparison) |
|---|---|---|---|
| `volumedetect` mean, `recording.mp4` | **-45.2 dB** | **-64.3 dB** | -68.5 dB (mic floor -96.7 dBFS) |
| `volumedetect` max, `recording.mp4` | **-6.5 dB** | **-25.4 dB** | -49.5 dB |
| `volumedetect` mean / max, capture | -48.7 / -6.8 dB | -66.4 / -25.0 dB | -80.3 dB mean |
| RMS per 10 s window | -71.5, -68.5, -71.5, -65.7, **-38.2**, -64.6, **-42.8** | -77.7, -77.7, -71.7, -67.7, -78.2, **-56.6**, -76.2 | |

Something real was recorded on both takes. The signal varies window by window, with loud events of up to
-6.5 dB, so this is not a constant floor. **But it was room sound, not speech.** Nobody spoke to the mic
during either take: the seat had no way to make a reference sound without playing audio on the owner's
machine, and I chose not to. Take 2's transcript shows the typical Whisper output for a near-silent track
("Thank you." five times). So these takes prove that **the tracks line up and that the mic chain
recorded a live signal.** They do not prove that speech lines up with the picture (lip sync). Only the
track lengths were compared.

### The three carried limits

1. **#157 was not exercised.** Neither take dropped audio samples: 0 audio packet gaps over 50 ms in
   the capture and the final file on both takes. The two-pass mux therefore had no gap to close. **A
   clean row 5 here does not retire #157**, which bites only when samples drop under load. The capture
   log's `drop=15` (take 1) and `drop=31` (take 2) are ffmpeg's **video** frame drop counters at 30 fps,
   not audio.
2. **#184 is not inside these takes.** They ran at the default 30 fps, and both shortfalls are under 0.7 s.
3. **#159.** Mux completion was read from the manifest, not from `/status` (see the table above).

**#113 and #156 seen as presences, on healthy takes only:**
- The watchdog was armed at start (`stall 45s, start-up 45s, every 2s`) and stopped before the writer.
- Liveness read `growing` in every sample.
- No capture fault was reported.
- Each serve took the claim, recorded its writer pid in the manifest's `CaptureOwner`, and released the
  claim at stop.

### What I touched on the owner's machine

- **Two recordings were added to his library**, as the row requires: `2026-11-16_164121_video` and
  `2026-11-16_164336_video`. Nothing was deleted.
- **I made exactly two takes**, plus one start request at 16:40:56 that was refused before any capture
  (`mic is required for this source`), which created no directory.
- **Take 1's transcription was cut short** when my driver stopped its serve about 5 s after the stop.
  Take 2's serve then found it in the library backlog (`TranscriptionBacklog ... pending=1`) and
  transcribed it. Take 1 now has its transcript but no `walkthrough.html`. For take 2, the driver waited
  for `PendingTranscriptions=0` before stopping. Note the consequence: **a dev serve pointed at the real
  library also works on the owner's own untranscribed recordings.** There were none today (`pending=0`
  before take 1), but this is the same exposure #161 describes.
- **An unintended file: `%LOCALAPPDATA%\AgentEyes\host-id` (32 bytes) was CREATED by my serve** at
  16:41:21 (`[CaptureLock] ReadOrCreateHostId: created ...`). This is #156's code. The owner's installed
  app is pre-#156 and never created it, and its CreationTime is that second. I verified it read-only,
  reported it to the lead at once, and **left it in place**. It is inert to his installed app, and any
  #156+ install creates it anyway. It is another instance of #161.
  The lead ruled to **leave it**: it is the machine's stable identity for #156's cross-host claims, and
  deleting it would make the next install mint a different id, turning any earlier claim into a foreign
  host's. It is the config-directory case of #161, not only the log case.
- **364 log lines** from my two serve pids went into his `logs\AgentEyes-20261116.log` (the #161 class
  again).
- **Nothing else.** No ffmpeg was killed: none was running before or after either take, apart from the
  ones my serve started and stopped. No preset, audio setting or default device was changed. His app
  was not stopped, restarted or reinstalled. No process of mine is left running. The build is in a
  separate git worktree, not in his installed app folder.

### What this section does NOT cover

- **Speech-to-picture sync.** Only track lengths were compared, on room sound, not on a reference signal.
- **#157 under load.** No samples were dropped.
- **Low frame rates (#184, #188).** Deliberately not run.
- **The installed app path.** These takes ran through `agenteyes serve`, the headless host, not
  `AgentEyesApp.exe`. The capture and mux code is shared, but the WPF app's own start path was not
  exercised by this row on this machine.
- **More than two takes**, and any mic other than the FDUCE SL40.
- **#113's failure paths and #156 against a second process.** Not exercised.

Evidence: `windows/rerun-b0e6ae8/row5-working-mic.txt`. It holds both driver transcripts, the serve
output, the full ffprobe / volumedetect / astats analysis, the first and last capture progress lines,
both final manifests, and the relevant app log lines. User path and host name are scrubbed.
