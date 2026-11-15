# Issue #142 - QA report, LINUX (devlinux)

This is the **Linux** platform report, the first of three. The Windows and macOS reports are not part
of this one and were not run here.

| | |
|---|---|
| Machine | devlinux - Ubuntu, GNOME on Xorg, DISPLAY=:0, 32 cores, 39 GB RAM |
| Date | 2026-11-15, 09:33 - 09:52 EST |
| Branch / commit under test | `issue-116-desktop-app` (PR #141) at **c80cb9d10cdd8954405b5ccc90340ae884507a34** |
| Recorder service under test | `~/.local/lib/agenteyes/agenteyes serve`, **pid 4100728**, started 09:34:34, `agenteyes.dll` sha256 **81083721...cbec22** (`810837217adbbdfae5e48a52ac8cd9a0d7e4dd25ebc38bf8513205674d9bec22`) |
| Desktop app under test | `~/.local/lib/agenteyes-app/AgentEyesDesktop`, **pid 4100815**, `AgentEyesDesktop.dll` sha256 `014f0fd21b7d1033a9abb1b2d1ac680fe48449d928f1522dc5aaca473580c5d4` |
| Machine load | 1-min load 9.46 at session start (before any recording), 3.2 - 5.8 during rows 4-6, 2.4 - 3.6 during the decode and the tests. 32 cores. |
| Test gate | `dotnet test tests/AgentEyes.Tests.Linux -c Release`: **Passed 278, Failed 0** (the full sln cannot build on Linux). |

## Summary

| # | Check | Result |
|---|---|---|
| 1 | Installs by the supported route | **PASS** |
| 2 | Launches; the main window opens | **PASS** |
| 3 | The library lists existing recordings, with a count | **PASS** - 76 |
| 4 | Records the screen, no audio | **PASS** - with observations O1 and O2 |
| 5 | Records screen + microphone, audio matches video | **PASS** - 106.688 s audio / 106.767 s video |
| 6 | A 10-minute recording completes and is playable | **PASS** - moov present, 632.03 s, full decode clean |
| 7 | Plays a recording back in the app | **PASS** |
| 8 | Detail view, settings and dictionary open and save | **PASS** |
| 9 | Tray icon shows idle and recording | **PASS** |
| 10 | Single instance: a second launch raises the first window | **PASS** - with new defect D1 in the log |
| 11 | Camera + microphone recording | **BLOCKED** - no camera on this machine |
| 12 | Always-on keeps the stretches with sound | **BLOCKED (not applicable yet)** - Phase 5 |

**One new defect (D1)**: two processes logging at the same instant can overwrite each other's bytes in
the shared log file. Details are under "New defects". It is not filed; it is handed to the lead.

## Which build was answering

The service I found on arrival was **#113's branch build**, not this branch: pid 2454852, `agenteyes.dll`
sha256 `77586071...298284e`. I checked this with an instrument that can tell the two builds apart.
The string `CAPTURE FAULT` exists only in #113's code. .NET stores string literals as UTF-16, so an ASCII
`grep -a` found **0 in both builds**. That grep could not tell them apart. Searching for the UTF-16LE
bytes instead:

```
old.dll (the service found on arrival)          2
~/.local/lib/agenteyes/agenteyes.dll (installed) 0
```

So the found build was #113's, and the build under test is this branch's. Every log line quoted below
carries `[pid 4100728]` (the service) or `[pid 4100815]` (the app), the pids of the build I installed.

---

## Row 1 - Installs by the supported route: PASS

**Expected:** `scripts/linux/install.sh` from the branch builds and installs both the service and the
app, exits 0, and the service answers `/health`.
**Actual:** exit 0. The old service (2454852) and the old app `AgentEyesLinux` (2455039) were stopped. The
new service pid 4100728 and the new app `AgentEyesDesktop` pid 4100815 are running, and `/health` returns
`{"ok": true, ..., "platform": "linux"}`. The only build warning is the existing CS8602 in
AlwaysOnEngine.cs:901.
Evidence: `linux/row1-install-transcript.txt`, which includes the process lists before and after, the
unit's MainPID and the hashes. The harness lines were removed.

## Row 2 - Launches; the main window opens: PASS

**Expected:** a window on screen, plus the app's log line.
**Actual:** `09:34:37.377 [pid 4100815] [INFO] [MainWindow] Opened`. X window 0x02400013 "AgentEyes",
1280x720, Map State IsViewable. The screenshot (`xwd` of that window) shows the Record view: devices
loaded ("Monitor 1 - 1920 x 1080 (primary)"), "Signed in", REC enabled, and the VIDEO and SYSTEM chips ON.
Evidence: `linux/row2-3-main-window.png`.

## Row 3 - The library lists existing recordings with a count: PASS

**Expected:** a Library screenshot whose count matches what is on disk.
**Actual:** the header reads **"76 recordings"**. `ls ~/Videos/AgentEyes | wc -l` = 76 at the same
moment: 74 before this session plus my rows 4 and 5. The app's own scan agrees:
`[MainWindow] RequestScan: 74 recording(s)` at launch, before my recordings. The two newest rows show
"Recording..." (row 5 in progress) and "Transcribing..." (row 4).
Evidence: `linux/row3-library-76.png`, rendered by the app itself (`AgentEyesDesktop --view library`,
then `--snap DIR`, both handed to pid 4100815 over the single-instance socket).

## Row 4 - Records the screen, no audio: PASS

Started through the Control API: `POST /record/start {"mode":"video","screen":1,"source":"none"}` at
09:35:34.860, stopped at 09:35:57.046. Directory `2026-11-15_093534_video`.

| ffprobe | value |
|---|---|
| streams | one: `h264`, 1920x1080, 30/1. **No audio stream** (that is the presence check: the file has exactly one stream, and it is video) |
| duration | 24.133 s |
| frame count | `nb_frames=724`, and decoded with `-count_frames`: `nb_read_frames=724` |

Evidence: `linux/row4.txt`.

- **O1, observation (not a failure of the row):** the video holds 724 frames = 24.13 s, but the take
  was 22.05 s (`FfmpegRecorder Stop: ... take 22049ms`; wall clock from start to stop 22.19 s). The
  encoder log shows the cause: it ran at `speed=0.93x` and was at frame 672 when stop arrived, and the
  2.1 s drain (`pipeline drained in 2096ms`) flushed the rest. The file has about 2 s (9%) more video
  than real time on a 22 s take. The same effect is smaller on longer takes: row 5 is 106.77 s of
  video for a 105.5 s take, and row 6 is 632.03 s for 629.9 s. Load at the time was 3.96 / 8.78 /
  10.23. I state it, and do not judge whether it is a defect.
- **O2, known defect #126:** the package stage FAILED with `ffmpeg failed during extract audio (exit
  234)`, on a recording that has no audio by design. That is #126, open. The recording itself is
  intact and playable.

## Row 5 - Records screen + microphone, audio duration matches video: PASS

This is the #114 check. `POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}`
at 09:36:37.099, stopped at 09:38:23.7, default mic settings (arnndn + speechnorm chain). Directory
`2026-11-15_093637_video`. The mux finished in 4.0 s (`stage mux: done ... in 4.0s`), so there was **no
#128 hang** on this 105 s take.

AAC packet arithmetic (packets counted with `ffprobe -count_packets`, x 1024 samples / 48000 Hz):

| file | AAC packets | audio | video packets / 30 | video | audio / video |
|---|---|---|---|---|---|
| recording.mp4 (final) | 5001 | **106.688 s** | 3203 | **106.767 s** | **99.93%** (0.079 s short) |
| recording.original.mp4 (raw) | 5000 | 106.667 s | 3203 | 106.767 s | 99.91% |

On macOS, #114 kept 12% of the audio. Here the audio runs the full length of the video, to within one
packet run.
**Note on content:** the mic is the analog input with nothing plugged in. volumedetect gives mean
-68.8 dB, max -35.7 dB, so it records near-silence and the transcript is empty. That does not affect a
duration measurement. It does mean this run proves the audio track's LENGTH, not its intelligibility.
Evidence: `linux/row5.txt`.

## Row 6 - A 10-minute recording completes and is playable: PASS

This is the #113 check. `POST /record/start {"mode":"video","screen":1,"source":"mixed","mic":"Family 17h"}`
(screen + mic + system) at 09:39:07.014. `/status` showed `elapsed 612.41` at 09:49:21, and the stop
came at 09:49:38.656, returning `DurationSeconds 629.92`. Directory `2026-11-15_093907_video`.
Post-processing finished every stage: mux 37.9 s, thumbnail 0.4 s, package 8.4 s.

| check | result |
|---|---|
| top-level atoms of recording.mp4 | `ftyp` 32, `free` 8, `mdat` 19,364,588, **`moov` 724,555 bytes at offset 19,364,628** |
| ffprobe | h264 1920x1080 30/1, 18961 frames, 632.033 s; aac 48 kHz stereo, 29625 packets |
| audio vs video | 29625 x 1024 / 48000 = **632.000 s** against **632.033 s** (99.99%, 0.033 s) |
| playable | full decode `ffmpeg -v error -i recording.mp4 -f null -`: **exit 0, no error output**, 11 s |

Load at the decode: 2.38 / 3.44 / 5.99. Evidence: `linux/row6.txt`, `linux/row6-probe.txt`.

**#121 (system audio about 1.3 s ahead of the picture) - recorded, not a failure of this row.** The raw
system track `system.original.wav` is **630.18 s** against **632.03 s** of video: 1.85 s short. That is
consistent with #121's late system capture start. Nothing was playing during the take: system audio
mean -85.5 dB, max -47.2 dB. So there is no sound event to measure a picture-to-sound offset against,
and this run neither confirms nor refutes the 1.3 s figure. The mic level was also near-silent (final
mix mean -69.7 dB).

## Row 7 - Plays a recording back in the app: PASS

`AgentEyesDesktop --preview 2026-11-15_093637_video` handed `preview:` to pid 4100815:
`[PlayerWindow] ctor: .../2026-11-15_093637_video/recording.mp4 (Video)`, then `[PlayerWindow] Load: playing`.

- **A blank render, diagnosed:** the app's self-render of the player (`--snap`) shows a **black video
  area**, with the controls showing pause and `0:04 / 1:46`
  (`linux/row7-player-selfrender-video-area-black.png`). Avalonia's `RenderTargetBitmap` cannot see
  libvlc's native child window, so that instrument cannot see video. I re-captured the real X window
  (`xwd -id 0x0240001b`) and the screen region (`ffmpeg x11grab`). Both show the playing video: the
  recorded desktop at 09:37 with the AgentEyes Library inside it, the scrubber at **0:24 / 1:46**
  (`linux/row7-player-xwd.png`, `linux/row7-player-screen.png`). The scrubber moved from 0:04 to 0:24
  between the two captures, so playback was advancing.
- **Redaction:** the recorded frame contained a browser address bar holding an OAuth device-auth
  callback code. That band is blacked out in both committed images. The recording on disk is untouched.

## Row 8 - Detail view, settings and dictionary open and save: PASS

**Opening** was done through the single-instance socket (`--details ID`, `--settings general`,
`--view dictionary`). Each window was rendered by the app itself: `row8-details-open.png`,
`row8-settings-open.png`, `row8-dictionary-open.png`.

**Saving** needs one click or keystroke per view, and the app is not on the AT-SPI bus (checked: no
AgentEyes in `Atspi.get_desktop(0)`). So I used `xdotool`, only inside AgentEyes windows. First I sent
the owner a desktop notification, waited 2 minutes, and confirmed through Mutter's IdleMonitor that
nobody was at the desktop. The desktop had been idle 3 h. The one reset lined up with my own player
close at 09:40:05. Every change was reverted, as stated below.

| view | action | presence proof | reverted |
|---|---|---|---|
| Settings | unchecked "Run at login" | `[Autostart] Disable` at 09:43:05.798; `~/.config/autostart/agenteyes.desktop` **gone** (`row8-settings-saved-unchecked.png`) | re-checked: `[Autostart] Enable ... --tray` at 09:43:13.195; file back, **byte-identical** to the pre-session copy |
| Detail | typed a title and pressed Enter | app `RenameAsync -> QA142 detail save`. Service: `POST /recordings/2026-11-15_093637_video/rename: 200`. The manifest gained `"DisplayName": "QA142 detail save"`. The window shows `Renamed to "QA142 detail save".` (`row8-details-saved.png`) | left as is: it is my own QA recording (row 5), and the name marks it plainly |
| Dictionary | added `QA142term`, then Delete -> confirm | `AddTermAsync: QA142term` / `SaveDictionaryAsync: 7 term(s)`, and the term is in `dictionary.json` (`row8-dictionary-saved.png`). Delete: `ConfirmAsync -> True` / `SaveDictionaryAsync: 6 term(s)`, and the term is gone from the file | the file's terms and variants were the same as before but re-sorted. The original file was restored byte for byte (sha256 `056d44e1...`, same as before) |

## Row 9 - Tray icon shows idle and recording: PASS

Both states are in the same top-bar slot. Each screenshot is paired with the item's own D-Bus state.
The item is `org.kde.StatusNotifierItem-4100815-0`, owned by the app pid.

| state | screenshot | D-Bus `Title` | app log |
|---|---|---|---|
| recording (row 4, 09:35:38) | red dot (`row9-tray-recording.png`) | `"AgentEyes - recording"` | `[TrayController] Render: idle -> recording` 09:35:35.763 |
| idle (after row 6, 09:49:42) | moon (`row9-tray-idle.png`) | `"AgentEyes"` | `[TrayController] Render: recording -> idle` 09:49:39.830 |

## Row 10 - Single instance: a second launch raises the first window: PASS

The window was first hidden, so that "raised" is something the check can observe. Evidence:
`linux/row10.txt`.

```
09:44:32.093  wmctrl close of the main window
09:44:32.098 [pid 4100815] [App] window closed to the tray      -> Map State: IsUnMapped
09:44:33.608  second launch: AgentEyesDesktop (no arguments)
09:44:33.713 [pid 14274]   [Program] Main: ... starting on linux (pid=14274)
09:44:33.717 [pid 14274]   [SingleInstance] TryAcquireSocket: /run/user/1000/agenteyes-app.sock command='show'
09:44:33.727 [pid 4100815] [SingleInstance] Accept: 'show'
09:44:33.727 [pid 4100815] [App] Run: show
09:44:33.727 [pid 4100815] [App] ShowWindow                     -> Map State: IsViewable
             second launch exit=0; AgentEyesDesktop processes afterwards: 4100815 only
```

The same handoff carried every command used in rows 3, 7 and 8. For example, at 09:36:43:
`[pid 4147867] TryAcquire: another instance is running - sent it 'view:library'` ->
`[pid 4100815] Accept: 'view:library'`. Sixteen second launches were made in all, and every one exited by
itself.

**The 09:44:33 excerpt shows defect D1.** pid 14274's line `TryAcquire: another instance is running -
sent it 'show'` reached the log as only `er instance is running - sent it 'show'`. Its first ~70 bytes
were overwritten by pid 4100815's `Accept` line, written in the same millisecond. The handoff itself
worked: the window was raised and the second process exited 0. The row passes, and the log is the
defect.

## Row 11 - Camera + microphone: BLOCKED

`ls /dev/video*` -> `No such file or directory`, and `/devices` reports `"cameras": []`. This machine has
no camera. v4l2loopback needs root and was not loaded, as instructed. The row needs a USB webcam, which
needs the owner's hands (#142 known blocker 1).

## Row 12 - Always-on: BLOCKED (not applicable yet)

Always-on is Windows-only in the engine until Phase 5 (#119). Not runnable on Linux today.

---

## New defects (handed to the lead, not filed)

**D1 - Two processes logging at once overwrite each other's bytes in the shared log.**
- Where: `src/AgentEyes.Core/Log.cs:55` writes with `File.AppendAllText(CurrentFile, ...)` into one file per
  day (`~/.local/share/AgentEyes/logs/AgentEyes-YYYYMMDD.log`). That file is shared by the service, the
  desktop app and every second launch.
- Observed: line 105988 of `AgentEyes-20261115.log` is `er instance is running - sent it 'show'`. pid
  14274's line lost its timestamp, pid tag and the first ~70 bytes. The bytes of pid 4100815's
  `Accept: 'show'` line, written in the same millisecond (09:44:33.727), took their place. Raw bytes
  (`cat -A`) confirm it is a truncated line, not a wrap.
- Likely mechanism, not proven: on Unix, .NET's append mode seeks to the end of the file when it opens
  it, rather than opening with O_APPEND. So two writers that open at the same moment write at the same
  offset, and the second write overwrites the first.
- Why it matters: lines from the second process vanish exactly when two processes talk to each other
  (single instance, app and service), and those are the moments proof depends on. A pass that rests on
  "process B logged X" can fail to find X.
- Rate: **3 of the 16** second launches in this run lost the start of their `sent it` line
  (`details:2026-11-15_093637_video`, `settings:general`, `show`). The other 13 are intact, among them
  09:36:43 `view:library`. Found with
  `grep -v -E '^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} \[pid'` over this session's part of the log:
  exactly those 3 lines. The same grep finds the 13 good lines when inverted, so the instrument works.
- Not covered: I did not reproduce it deliberately, and the mechanism above is inferred from the code.

## Known defects met, cited and not re-raised

- **#126** - row 4's package stage fails on a recording with no audio (`extract audio`, exit 234).
- **#121** - row 6's system track is 1.85 s shorter than the video, consistent with the late system
  start. The offset itself is unmeasurable here because the system audio was silent.
- **#128** - not met. The default mic chain finished in 4.0 s (105 s take) and 37.9 s (630 s mixed take).
- **#113 / #114** - not met on this build. Row 6 has its moov atom, and row 5's audio is the full length.

## What this report does NOT cover

- **Windows and macOS.** This is the Linux report only.
- **The tray on a physical click.** Row 9 shows the icon's two states. Clicking it was not part of
  row 9, and I did not click it.
- **Audio content and sync.** Both the mic and the system source were near-silent. Rows 5 and 6 prove
  track LENGTH and presence, not that speech is intelligible or that sound lines up with the picture.
- **This branch's service build was not left running** (see "State of devlinux").

## State of devlinux after this run

- Before installing, I took a tarball of `~/.local/lib/agenteyes`, `~/.local/lib/agenteyes-app`, the
  unit and both desktop entries. After the evidence I put all of them back. `diff -r` against the
  tarball came back **identical** for every path. The service is running **#113's branch build again**:
  pid **28960**, `agenteyes.dll` sha256 `77586071...298284e`, `/health` ok:true. The pre-session app
  `AgentEyesLinux --tray` was relaunched (pid 29068), so the top bar looks as it did.
- My own processes: the branch app (pid 4100815) was stopped, and all sixteen second-launch processes had
  exited by themselves. No process of mine is left running.
- `~/Videos/AgentEyes`: nothing deleted. My three recordings were added: `2026-11-15_093534_video`,
  `2026-11-15_093637_video` (titled "QA142 detail save") and `2026-11-15_093907_video`.
  `2026-11-14_102532_video` (#113's evidence) is present.
- Restored to their pre-session bytes: `dictionary.json` and `~/.config/autostart/agenteyes.desktop`.

## Evidence files (`docs/cencon/proof/issue-142/linux/`)

`row1-install-transcript.txt`, `row2-3-main-window.png`, `row3-library-76.png`, `row4.txt`, `row5.txt`,
`row6.txt`, `row6-probe.txt`, `row7-player-xwd.png`, `row7-player-screen.png`,
`row7-player-selfrender-video-area-black.png`, `row8-details-open.png`, `row8-details-saved.png`,
`row8-settings-open.png`, `row8-settings-saved-unchecked.png`, `row8-dictionary-open.png`,
`row8-dictionary-saved.png`, `row9-tray-idle.png`, `row9-tray-recording.png`, `row10.txt`.
