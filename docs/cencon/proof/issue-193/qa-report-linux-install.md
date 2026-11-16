# Issue #193 - QA report (Linux seat): installing the published v1.12.0 on Linux

**Result: it installs and records.** On the Linux machine (`devlinux`), `scripts/linux/install.sh`
from a checkout of the `v1.12.0` tag built, installed and started the recorder and the app, exit 0.
The service reports 1.12.0 and embeds commit `02a4622`. A 32 s take through the installed service
decodes with exit 0, and its last frame is within one frame of the end. The unit still carries
`KillMode=mixed`.

**The release notes are wrong or incomplete for a Linux user in four places (N1-N4 below).** The
sharpest one: #159 says the missing file "was looked for on macOS only". On this install it was
looked for on Linux, and it was missing. `/record/stop` returned a `File` path that did not exist
for about 3.9 s.

## What "released on Linux" means today, exactly

- `release.yml` runs on `windows-latest`. The published v1.12.0 release has six assets:
  `AgentEyes-Setup-win-x64.exe`, `agenteyes-win-x64.exe`, `AgentEyesApp-win-x64.exe`,
  `agenteyes-setup-cli-win-x64.exe`, `agenteyes-ffmpeg-win-x64.zip` and `release-manifest.json`
  (`gh release view v1.12.0`). **None of them is for Linux. There is nothing to download.** That is #194.
- Installing v1.12.0 on Linux means: get the source, check out the tag, and build it on your own
  machine with `scripts/linux/install.sh`. That needs the .NET 8 SDK, ffmpeg, libvlc, xrandr and an
  X11 session. Nothing here is a release artifact. A Linux user is building `02a4622` from source.
- So "v1.12.0 on Linux" is a source tag. Whatever is installed is whatever the person's own SDK
  produced from it, with no checksum to compare against.

## 1. What landed

| Item | Before (installed from main `b0e6ae8` earlier tonight) | After (installed from tag `v1.12.0`) |
|---|---|---|
| Checkout | - | `git checkout v1.12.0` -> HEAD `02a4622774f50382e35b3599c90c65bc2abb4608` (tag object `94266a3`) |
| `GET /version` | `{"app":"AgentEyes","version":"1.11.11"}` | `{"app":"AgentEyes","version":"1.12.0"}` |
| `agenteyes.dll` InformationalVersion | `1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb` | `1.12.0+02a4622774f50382e35b3599c90c65bc2abb4608` |
| `AgentEyesDesktop.dll` InformationalVersion | (not captured) | `1.12.0+02a4622774f50382e35b3599c90c65bc2abb4608` |
| Unit file sha256 | `db53caaf...fbb9c` | `db53caaf...fbb9c`, the same, and equal to `git show v1.12.0:scripts/linux/agenteyes.service` |
| Service | PID 2211032, up since 15:58 | PID 2690287, started 18:20:15, `NRestarts=0` |

**What changed between `b0e6ae8` and `02a4622`, and what did not.** `git diff b0e6ae8 v1.12.0 -- src tools`
is five lines, `<Version>1.11.11</Version>` -> `<Version>1.12.0</Version>`, in the five project files.
`scripts/` and `.github/` are byte-identical between the two commits. The rest of the release commit is
documentation (`docs/releases/v1.12.0.md` and the #193 handoff and Windows QA report). So the recorder
installed tonight is the code the rows seat verified on `b0e6ae8`, with only the version number
changed. The version is the only thing the product now reports differently, through `/version` and the
assembly metadata. `/health` carries no version. The one build warning in the transcript (CS8602 at
`AlwaysOnEngine.cs:901`) is in unchanged code.

Evidence: `linux-install/install-transcript.txt`, `linux-install/landed.txt`,
`linux-install/library-and-preinstall.txt`.

## 2. /health and /status

Both answer on `127.0.0.1:7882` after the install: `/health` gives `{"ok":true,"app":"AgentEyes","host":"headless","platform":"linux"}`,
and `/status` gives `State: idle, SignedIn: true` (`landed.txt`).
One side note: `localhost:7882/status` returns **404** while `127.0.0.1:7882/status` returns 200,
because the listener is bound to the `127.0.0.1` host name. The installer and the docs use `127.0.0.1`,
so this is not a mismatch in the notes. I only mention it because it cost me one confusing read.

## 3. The recording, its decode and the end-frame check

Request: `POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}` (screen plus
the built-in mic, the same shape as #142 row 5, with no audio settings changed). It ran 30 s, then
`POST /record/stop {}`. The take is `~/Videos/AgentEyes/2026-11-16_182129_video` (`linux-install/take.txt`).

| Check | Expected | Actual | |
|---|---|---|---|
| Capture runs | `growing` while recording | `starting` at t+1 s, `growing` at 10/20/30 s (432 KB -> 1.99 MB) | PASS |
| Mux finishes | `PostProcessing.mux.State = done` | `done` at stop+4 s, 1 attempt | PASS |
| Full decode, `ffmpeg -v error -xerror -i recording.mp4 -f null -` | exit 0, no stderr | exit 0, 0 stderr lines | PASS |
| **Last decoded video frame** | near the end. `/record/stop` said 32.81 s (wall clock since the start request); the manifest says `DurationSeconds` 32.27 and `CapturedSeconds` 32.16 | **32.233 s**, 968 frames, which is the container's 32.267 s minus one 30 fps frame | PASS |
| Audio end | near the end | last AAC frame at 32.02 s (stream 32.03 s) | PASS |
| `recording.original.mp4` (the kept fragmented capture) | the same | exit 0, 968 frames, last frame at 32.233 s | PASS |

The 0.54 s between 32.81 and 32.27 is the capture's start-up time: the wall clock starts at the request,
and the first frame arrives after it. It is not a lost tail.

**Truncation control (cheap, so I ran it).** I copied `recording.original.mp4` into a scratch folder and
cut the copy to half its bytes (`linux-install/trunc.txt`). The library file's sha256 was compared before and after and is unchanged.

| Check on the truncated copy | Result | Catches the truncation? |
|---|---|---|
| `moov` atom present | **True** (the fragmented header is at the front) | **No** |
| Bare decode, `ffmpeg -v error -i half.mp4 -f null -` | **exit 0** (it prints "partial file" to stderr) | **No** |
| `-xerror` decode | exit 183 | yes, on this cut |
| `format.duration` | 16.33 s | yes, if compared to the expected length |
| **Last decoded video frame** | **16.333 s vs 32.233 s on the full file** | **yes** |

This reproduces what the brief warned about: under this release's fragmented container, "moov present"
and a bare decode both pass on a file that has lost half its length. The end-frame check is what tells
them apart.

## 4. The unit

Installed `~/.config/systemd/user/agenteyes.service`, as systemd reads it (`systemctl --user show`):
`KillMode=mixed`, `KillSignal=15` (SIGTERM), `TimeoutStopUSec=2min`. The file also has
`Restart=on-failure` and `RestartSec=10`. Its sha256 is equal to the tag's copy and to the pre-install
copy. **The installed unit matches what the #165 entry describes.** The reinstall did not change it.

I did not re-measure the 2-4 s SIGKILL on the installed unit. That needs killing the service mid-take,
and then `Restart=on-failure` brings the service back and its start-up repair pass runs over the owner's
library. That was outside this brief. The #165 entry's "has not been re-measured" stays true.

## 5. The release notes read by a Linux user with the product installed

Sections checked: 1 (per-platform shape), 2 Linux (install), 3 (every defect entry that mentions Linux:
#157, #159, #188, #149, #184, #165), 4 (what is new) and 6 (what was verified). All ten defect issues are
OPEN (`gh issue view`), which matches "All ten are open".

### N1 - #159: Linux has now been measured, and the file really is missing (WRONG, the sharpest)

The note says: *"The missing file itself was looked for on macOS only. On Windows and Linux the status read
`idle` while the manifest still said the mux was running."* It also says: *"For a while only
`recording.mp4.muxing.tmp` is on disk."*

On this v1.12.0 install, after a 32 s take (`take.txt`):

| Time after stop | `/status` | manifest `mux` | Files in the folder |
|---|---|---|---|
| response to `/record/stop` | - | - | the response says `"File": ".../recording.mp4"` |
| +0.6 s | `idle` | `running` | `raw.mp4`, no `recording.mp4`, **no `.muxing.tmp`** |
| +1.7 s | `idle` | `running` | `+ audio_processed.wav`, still no `recording.mp4` |
| +2.8 s | `idle` | `running` | `+ recording.mp4.muxing.tmp`, still no `recording.mp4` |
| +3.9 s | `idle` | `done` | `recording.mp4` present |

So on Linux:
1. The missing file was looked for, and it was missing for about 3.9 s after a 32 s take.
2. **`/record/stop` returns, as `File`, a path that does not exist yet.** That is the field a script
   reads. The note does not mention it.
3. "Only `recording.mp4.muxing.tmp` is on disk" is not what a Linux user sees in the first seconds. At
   first there is only the raw capture, and the `.muxing.tmp` file appears later.

Suggested replacement for the last part of the #159 entry: *"On Linux, on this release, after a 32 s take
`recording.mp4` was missing for 3.9 s after `/status` read `idle`, and the path `/record/stop` returned as
`File` did not exist yet. At first only the raw capture is on disk, then `recording.mp4.muxing.tmp`.
On Windows the status read `idle` while the manifest still said the mux was running; the file itself was
not looked for."*

### N2 - The Linux install steps leave out four things the script does or demands (INCOMPLETE)

Section 2 says you need the .NET 8 SDK, and that the script installs ffmpeg if it is missing. That part is
accurate: SDK 8.0.134 built both projects here. But `scripts/linux/install.sh` at the tag also does these
things, and the notes do not say so:

- **It refuses to run on Wayland.** It stops with *"this is a Wayland session; AgentEyes on Linux records
  X11 only (log in with 'Ubuntu on Xorg')"* (install.sh, the `XDG_SESSION_TYPE` check). Current Ubuntu logs
  in to Wayland by default, so this is the first wall most Linux users will hit. Read from the script; this
  machine runs X11, so I did not see the refusal.
- **It needs `xrandr`** and stops if it is missing (`sudo apt install -y x11-xserver-utils`).
- **It installs libvlc and two VLC plugin packages as root**, the same way it installs ffmpeg
  (`libvlc-dev vlc-plugin-base vlc-plugin-video-output`). The notes name only ffmpeg as the thing it asks
  permission for.
- **It signs you in to DevThrottle during the install.** If you are not signed in, it runs
  `agenteyes signin`, and **the install ends with `[FAIL]` if sign-in does not complete**. By then the
  service and the app are already installed and running. On this machine the account was already signed
  in, so the transcript shows only `[OK] signed in as <owner account>`.

Two smaller points for the same section:
- *"To update, check out the newer tag and run the script again"* - the script **stops a running
  recording** first ("Stopping the running service (a running recording is stopped and kept)"). The notes
  should say so. I installed while idle, so I did not observe it.
- The script also opens the app window at the end. "Starts both" covers that, but a reader may not expect a window.

### N3 - Section 4 (#156) says Linux politely stops leftover captures; under the installed unit there are none to stop (CONTRADICTS #165)

Section 4: *"Leftover capture processes from a dead recorder are cleaned up at start (on Linux and macOS
with a polite stop first ...)"*. Section 3 #165, for the same platform: when the service dies, systemd
SIGKILLs the capture 2-4 s later. The installed unit has `RestartSec=10`, so by the time the service starts
again, systemd has already SIGKILLed the capture. **For the recorder the installer sets up, the polite stop
in section 4 never gets a capture to stop. The capture has already been SIGKILLed, which is #165.** The polite stop only
applies to a capture outside the service's cgroup, such as one started by the CLI. This is an inference from
the installed unit (verified above) and #165's measurement. It was not measured. Suggested addition to section 4: *"On
Linux, a capture made by the installed service is killed by systemd before the restart (#165), so this
clean-up does not save it."*

### N4 - Section 1 still tells the reader to put the notes on the release page, which is done (STALE)

*"The GitHub release page will not show these notes on its own ... edit the release after the workflow
finishes"*. The published v1.12.0 page body **is** these notes now (`gh release view v1.12.0`, published
2026-11-16T23:17:01Z). So a reader on the release page is told to do something to the page they are reading. This is not
Linux-specific. Delete the paragraph from the page body, or reword it as a maintainer step.

### Entries that match what I see

- **Section 1, Linux row** ("Nothing to download", built from the tag with `install.sh`): accurate; see the asset list above.
- **#165**: the unit is `KillMode=mixed`, as the entry says (section 4 above). The "not re-measured" caveat stays true.
- **#188**: "4-5 s on a 32-core Linux machine" fits this machine (`nproc` = 32). I did not re-measure it.
- **#184**: "neither have macOS and Linux" is still true. My take ran at 30 fps.
- **#157**: it cannot be checked on a quiet machine. My take was healthy, and the audio ends 0.2 s before the video.
- **#149**: "Linux has no camera in the test fleet" is still true. I did not try a camera.
- **Section 6, Linux column**: not re-run. My take is one healthy 32 s screen+mic recording at 30 fps on the release build. It does not replace rows 4-6.

## Observations outside the notes (not assessed as defects)

- `capture.lock` and `mux.lock` (both 0 bytes) are still in the take's folder after the mux, thumbnail and
  transcription finished. I did not look into whether that is intended.
- `localhost:7882` returns 404, while `127.0.0.1:7882` works (section 2).

## Hazards and what I touched

- **Library: read-only, except for my own take.** Before the install and after the take there were 850
  files outside `2026-11-16_182129_video`, with the same path, size and mtime, and their diff is empty. The
  only new top-level entry is my take, and nothing was deleted. `2026-11-14_102532_video` is present.
  `~/qa-backups/agenteyes-142` was not opened. Evidence: `linux-install/library-and-preinstall.txt`.
- **The other mission's ffmpeg.** The go2rtc ffmpeg (PID 2553250, `-user_agent go2rtc/ffmpeg -rtsp_flags ... rtsp://127.0.0.1:8554/lake`)
  ran before, during and after the take. It was identified by its command line and not touched. The take's own ffmpeg processes were
  2695697 (x11grab+pulse) and 2695910 (pulse meter), and both were gone after the stop.
- **Audio settings**: not changed. The take selected the built-in mic by name, as #142 row 5 does.
- **The reinstall itself** stopped and restarted the owner's service and app, as the brief requires. It was idle when I started (`/status` `State: idle`).
- **Tests**: no code was changed, so the build and test gate was not run. This PR adds documentation only.

## Evidence files (`docs/cencon/proof/issue-193/linux-install/`)

| File | What it holds |
|---|---|
| `install-transcript.txt` | the checkout at the tag and the complete `install.sh` output, exit 0 |
| `landed.txt` | `/health`, `/version`, the embedded InformationalVersion of both assemblies, the binary timestamps, the unit as systemd reads it, the unit sha256 and `/status` |
| `take.txt` | the 30 s take: start, liveness every 10 s, stop, then status, manifest and files every second until the mux is done |
| `decode.txt` | the full decode, frame count and last video and audio frame of `recording.mp4` and `recording.original.mp4` |
| `trunc.txt` | the truncation control on a scratch copy |
| `library-and-preinstall.txt` | the pre-install version, commit and unit, and the library before/after comparison |

Paths are written relative to the home folder, and the account is shown as `<owner account>`.
