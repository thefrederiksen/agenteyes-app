# QA report - issue #102 on a live Linux host

- **Verdict: FAIL (flow:qa-failed)** - one measured defect in the recording itself (system audio is
  about 1.3 s ahead of the picture in the final mp4), and one criterion part that this host
  cannot prove (no v4l2 camera exists here).
- Host: devlinux, Ubuntu, kernel 6.8.0-147-generic, GNOME on X11 (`DISPLAY=:0`), PipeWire 
  with pipewire-pulse.
- Code under test: `main` at `f29c8c2`, installed with `scripts/linux/install.sh` on 2026-11-14
  09:26 EST (service version 1.11.11).
- Date: 2026-11-14. QA ran every command and measurement below on the host itself.

Artefacts referenced below live in `docs/cencon/proof/issue-102/qa-linux/`. The recordings themselves
stay on devlinux under `~/Videos/AgentEyes/` (named per test below).

## Environment found (before anything was installed)

| Check | Result |
|---|---|
| `dotnet --list-sdks` | `8.0.134 [/usr/lib/dotnet/sdk]` (.NET 8 SDK only) |
| ffmpeg devices | `x11grab` (D), `pulse` (DE), `video4linux2,v4l2` (DE), `alsa` (DE) |
| `$XDG_RUNTIME_DIR` | `/run/user/1000` |
| `systemctl --user is-system-running` | `degraded` - one unrelated unit failed (`warmforward-forecaster.service`); nothing AgentEyes |
| `pactl info` | **pactl is not installed.** Sound server checked another way: `pipewire`, `pipewire-pulse`, `wireplumber` user services all `active (running)`; `/run/user/1000/pulse/native` exists; `wpctl status` lists 1 sink (IEC958 digital) and 1 source (onboard analog) |
| `ls /dev/video*` | **No such file. This host has no camera.** `v4l2loopback.ko` exists in the kernel tree but loading it needs root, and `sudo -n` needs a password |
| AgentEyes already installed? | Yes. `agenteyes.service` was `enabled`, `active (running)` since 2026-11-09 09:30:10, running a 2026-10-15 build from `~/.local/lib/agenteyes`; `/health` answered `{"ok":true,...,"platform":"linux"}`. It was replaced by the supported installer below |

## Build and tests

- `dotnet build src/AgentEyes.Core -c Release -f net8.0 -p:Platform=x64` -> `0 Error(s)`, 1 warning
  (CS8602 at `AlwaysOnEngine.cs:901`).
- `dotnet test tests/AgentEyes.Tests.Linux -c Release` -> **Passed 94, Failed 0, Skipped 0**
  (the count has grown from 48 since #105; the desktop app's tests were added by #110).
  As the issue warns, these tests check the TEXT of ffmpeg command lines and parsers. They open no
  device and prove nothing for AC2 to AC6.
- `scripts/linux/install.sh` (read before running; no root was needed because ffmpeg, xrandr and
  libvlc were present) -> every step `[OK]`, ending in `service is up` and
  `signed in as <the owner's DevThrottle account>`. Note: the installer also starts the
  desktop app (`AgentEyesLinux`), which opened its window on the live display; it shows in
  the recordings below.

## Acceptance criteria - Expected vs Actual

### AC1 - builds on Linux; `agenteyes screens` lists X11 monitors and pulse sources - **PASS**

- Expected: real monitors from the X server and real pulse sources.
- Actual (`qa-linux/agenteyes-screens.txt`): monitor `1  1920 x 1080 (0, 0) primary HDMI-0`, the
  same as `xrandr --listmonitors` (`0: +*HDMI-0 1920/598x1080/336+0+0`). Microphone
  `[0] "Family 17h ... Analog Stereo" <alsa_input.pci-0000_08_00.3.analog-stereo>`. System audio
  `<alsa_output.pci-0000_08_00.3.iec958-stereo.monitor>`. Cameras `(none found)`, which is correct
  for this host.
- When a PipeWire virtual source was created (`pw-loopback`, node `qa_virtual_mic`), `screens`
  immediately listed it as `[1] "QAVirtualMic" <qa_virtual_mic>`: the listing is live, not
  cached.

### AC2 - `GET /devices` lists monitors, pulse mics, the monitor source and v4l2 cameras - **PARTIAL (cameras NOT VERIFIED)**

- Expected: all four device kinds, read from real hardware.
- Actual (`qa-linux/devices.json`): `monitors` = 1 (`HDMI-0`, 1920x1080, primary); `mics` = 1
  (onboard analog); `systemAudio` = `Monitor of ... (alsa_output.pci-0000_08_00.3.iec958-stereo.monitor)`;
  `cameras` = `[]`.
- Monitors, mics and the monitor source: **PASS**.
- v4l2 cameras: **NOT VERIFIED.** The empty list is accurate, but it only shows an absence. Proving
  this part needs a v4l2 node: a USB webcam plugged into devlinux, or `sudo modprobe v4l2loopback`
  (which needs the owner's password). It cannot be passed from this host as it is.

### AC3 - `POST /record/start {"source":"mixed","mic":...}` starts screen + mic + system audio; `/status` = recording - **PASS**

- Request: `POST /record/start {"source":"mixed","mic":"Analog Stereo"}` at 09:27:39.
- Response and `/status` 3 s later: `State=recording, Mode=video, Source=mixed,
  Dir=~/Videos/AgentEyes/2026-11-14_092739_video`.
- Processes running at that moment (pgrep): one ffmpeg `-f x11grab ... -video_size 1920x1080 -i :0+0,0
  -f pulse ... -i alsa_input...analog-stereo ... raw.mp4` (screen + mic); one
  `-f pulse -i alsa_input...` mic level meter; one `-f pulse -i alsa_output...iec958-stereo.monitor`
  (system audio). All three captures are running.
- The level meters are live: during speech, `/status` read `SystemLevel` 0.213, 0.386, 0.482, 0.469,
  0.111, 0.478 over 6 s (recording `2026-11-14_093312_video`).

### AC4 - `POST /record/stop` gives a dir with recording.mp4, transcript, thumbnail, manifest title - **FAIL (system audio out of sync)**

All the artefacts are present and correct. What fails is the timing of the mixed track. Measurements:

**Recording 1 - API, 100 s (`2026-11-14_092739_video`).** The owner was not at the machine, so
no voice was available. 16 spoken sentences were played into system audio (`spd-say`) from about
t=10 s to t=95 s. The onboard analog mic has nothing plugged in. It recorded its noise floor
(`MicMeasure: noise floor -73.0 dBFS`).

| File | Stream | Duration | Count |
|---|---|---|---|
| recording.mp4 (2,984,544 B) | h264 1920x1080 30 fps | 100.833 s | 3025 frames |
| recording.mp4 | aac 48 kHz stereo | **100.784 s** | **4726 packets** (x1024/48000 = 100.82 s) |
| recording.original.mp4 (raw screen+mic) | aac | 100.764 s | 4725 packets |
| system.original.wav (19,065,644 B) | pcm s16le 48 kHz stereo | 99.300 s | - |

- Audio covers the whole video, unlike #114 on macOS. Mean volume of the mp4 audio per 10 s window:
  0-10 s -70.2 dB (before the speech began), then -20.7, -19.9, -20.4, -20.6, -20.2, -20.3, -19.9,
  -20.8, -24.1 dB through 100 s. Sound is present in every window where speech was played.
- The video is live, not frozen. The app window's own timer reads `REC 00:18` at t=20 s and
  `REC 01:27` at t=90 s (`qa-linux/rec1-frames-t20-t90.png`).
- Thumbnail `thumb.jpg` (10,285 B): the real desktop, with the AgentEyes window recording
  (`qa-linux/rec1-api-thumb.jpg`). Not blank.
- Transcript: `transcript.json`, `transcript.en.vtt`, `transcript.txt`, 19 segments. The text
  matches what was played, e.g. `[00:15] The quick brown fox jumps over the lazy dog.`,
  `[00:33] Bananas, oranges, and purple elephants are dancing in the kitchen.`
  (`qa-linux/rec1-api-transcript.txt`).
- Manifest (`qa-linux/rec1-manifest-excerpt.json`): `Title = "Linux Screen and Audio Quality Check"`,
  `Description` set, `DurationSeconds = 100.83`, `PostProcessing` mux/thumbnail/package all `done`
  on attempt 1. Hosted titling cost 430 DevThrottle tokens.
- The stop response (from recording `2026-11-14_093312_video`, sent with a JSON body):
  `HTTP/1.1 200 OK {"Dir": ".../2026-11-14_093312_video", "File": ".../recording.mp4",
  "DurationSeconds": 12.24, "Shots": 0}`.

**Recording 2 - CLI with a real mic signal, 74 s (`2026-11-14_093053_video`).** A PipeWire virtual
source `QAVirtualMic` was created (`pw-loopback`), and distinct speech was played into it
("Microphone channel speaking. Zebra, violin, and seventeen lighthouses"). Other speech went to
system audio ("System channel. Copper kettles boil on Tuesday", etc.), alternating.

| File | Stream | Duration | Count |
|---|---|---|---|
| recording.mp4 (1,274,469 B) | h264 | 74.467 s | 2234 frames |
| recording.mp4 | aac | **74.443 s** | **3483 packets** |
| recording.original.mp4 (mic) | aac | 74.433 s | 3482 packets |
| system.original.wav | pcm | 72.380 s | - |

- The two sources are cleanly separated. Mean volume per 2 s window, mic track / system track:
  t=4 s -16.4 / -91.0 dB; t=8 s -16.9 / -91.0; t=13 s -91.0 / -15.4; t=20 s -16.2 / -91.0;
  t=27 s -91.0 / -16.1.
- The transcript contains BOTH sources: `[00:05] zebra, violin, and 17 lighthouses. microphone
  channel speaking.`, `[00:13] Copper Kettles boil on Tuesday.`, `[00:26] System Channel: Nine
  green parrots fly north.` (`qa-linux/rec2-cli-transcript.txt`). Title: `Repeated Microphone
  Channel Test Recording`. Thumbnail present (`qa-linux/rec2-cli-thumb.jpg`).

**THE DEFECT - recording 3, sync test, API, 138.6 s (`2026-11-14_093418_video`).** A 0.25 s,
1 kHz beep was played at the same instant into the virtual mic and into the default sink (whose
monitor is the system-audio source), 4 times. The mic track is recorded by the same ffmpeg process as
the screen (its start timestamp is 26 ms from the video's), so it is the picture-synced reference.
Beep onsets (`silencedetect`, -40 dB):

| Beep | mic track (= picture) | system.original.wav | system audio early by |
|---|---|---|---|
| 1 | 5.410 s | 4.091 s | **1.319 s** |
| 2 | 126.041 s | 124.752 s | **1.289 s** |
| 3 | 130.349 s | 129.061 s | **1.288 s** |
| 4 | 134.652 s | 133.365 s | **1.287 s** |

- **Control (outside AgentEyes):** one plain ffmpeg recording both pulse sources at once, same beep
  pair, gave onsets 3.755 / 3.712 s and 8.149 / 8.107 s, an offset of **0.043 s**. The 1.29 s comes
  from AgentEyes, not from the test setup.
- In the final `recording.mp4` the single beep is heard TWICE: at 4.35 s (system copy) and 5.43 s
  (mic copy). Every system sound plays about 1.3 s before the screen shows its cause. For a meeting
  recording, the other speakers are out of lip-sync with the picture.
- Cause, from the service log for this recording: x11grab and the mic start at 09:34:18.547. The
  system-audio ffmpeg is launched only at 09:34:19.574, after the sequential start steps
  `starting mic level meter` and `starting system loopback`, each of which runs extra
  `ffmpeg -sources pulse` probes and a 300 ms settle. The mux
  (`src/AgentEyes.Core/Audio/AudioMix.cs`, filter
  `[0:a]...[m];[1:a]volume=0.7[s];[m][s]amix=inputs=2:duration=longest`) then puts `sys_native.wav`
  at t=0 with no delay. Cameras have `CameraStartOffsetSeconds`; system audio has no equivalent
  offset. The same 1-2 s shortfall shows in every run: 100.83 vs 99.30 s, 74.47 vs 72.38 s,
  138.57 vs 136.72 s.
- Likely fix (for the developer, not done here): record the system-audio start time relative to the
  screen start, and either `adelay` the `[1:a]` input by that much in the mux, or start the system
  capture before the screen. Add a regression test on the generated filter.

### AC5 - runs as `systemd --user` unit, enabled at login, no terminal; `agenteyes record` drives it - **PASS**

- `systemctl --user is-enabled agenteyes` -> `enabled`. Link
  `~/.config/systemd/user/graphical-session.target.wants/agenteyes.service` exists.
- `systemctl --user status agenteyes` -> `Active: active (running)` (`qa-linux/systemctl-status.txt`).
  The main PID's parent is PID 2224 `/usr/lib/systemd/systemd --user`. TTY is `?`, so no terminal.
- Started at login with no terminal: the user journal for the 2026-11-09 boot (system boot 09:27:55)
  shows `09:30:09 Reached target graphical-session.target` and then `09:30:10 Started
  agenteyes.service`. Nobody started it by hand.
- The CLI drives it: `agenteyes record start --mix --mic "QAVirtualMic"` ->
  `[ok] recording -> .../2026-11-14_093053_video source=mixed mode=video`;
  `agenteyes record status` -> `state=recording elapsed=2.2s`; `agenteyes record stop` ->
  `[ok] saved .../2026-11-14_093053_video (72.2s)`. That is recording 2 above.

### AC6 - the DevThrottle credential file is mode 0600 - **PASS**

- `stat -c '%a'` -> `600` for `~/.local/share/AgentEyes/devthrottle.cred` (1620 B), and `700` for
  its directory, under a umask of `0002`. Without the explicit mode a new file would be 664.
- The service log from when it was written (2026-10-15 18:21:52) reads
  `[CredentialFile] Write: done, mode=600`. `CredentialFile.cs` is unchanged since that commit
  (`4ff09f4`). It creates the file with `UnixCreateMode = UserRead|UserWrite` and renames it into
  place.
- Not re-written today: a fresh write needs a browser sign-in, and the owner was not present. The
  file in use and the writer's log line are the evidence.

### AC7 / AC8

Not re-run on this host (Windows CI; already marked MET in the handoff). AC8's Linux test project
runs green here: 94 passed.

## Other defects found (separate bugs, not #102 criteria)

1. **A bodyless `POST /record/stop` stops the recording but answers `411 Length Required`.**
   `curl -X POST http://127.0.0.1:7882/record/stop` (no `-d`) returned `<h1>Length Required</h1>`.
   The service log shows the stop DID run and saved the recording
   (`09:29:20.100 POST /record/stop` ... `Stop: ... saved`). Writing the answer then failed with
   `ObjectDisposedException ... HttpListenerResponse` at `HeadlessControlServer.cs:426` (via line
   132). The managed HttpListener on Linux sends the 411 itself but still dispatches the request. A
   client is told the stop failed when it succeeded, and a retry gets `409 not recording`. With
   `-d '{}'` the same call answers 200 correctly.
2. **`agenteyes record start --help` starts a recording instead of printing help.** It printed
   `[ok] recording -> .../2026-11-14_093032_video source=none mode=video` and recorded until
   stopped (3.5 s, kept on disk).

## Environment gaps the lead should know about

- No camera on devlinux. AC2's camera part needs a webcam plugged in, or the owner to run
  `sudo modprobe v4l2loopback` once.
- `pactl` is not installed (`pulseaudio-utils`). AgentEyes does not need it; it lists devices
  through `ffmpeg -sources pulse`.
- The onboard analog input has no microphone attached (noise floor -73 dBFS). Mic content was proven
  with a PipeWire virtual source instead.

## Recordings left on devlinux (kept, not deleted)

`~/Videos/AgentEyes/2026-11-14_092739_video` (API, 100 s), `..._093032_video` (accidental, 3.5 s,
from `--help`), `..._093053_video` (CLI, 74 s), `..._093312_video` (level meter check, 12 s),
`..._093418_video` (sync test, 138.6 s).
