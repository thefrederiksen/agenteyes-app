# Issue 111 handoff - the DirectShow-only -audio_buffer_size is gone from both macOS audio inputs

Developer Agent -> QA. I believe this is finished, with ONE acceptance criterion that this machine
cannot prove and that I have NOT passed: AC4 needs the macOS Screen Recording permission, which
only the person at the keyboard can grant. Read "What is NOT proven" before trusting anything above
it.

Implementing machine: Mac mini, Apple M4 (arm64), macOS 26.5.2 (Darwin 25.5.0), ffmpeg 9.0.2 from
Homebrew, two displays at 1920x1080, mics `[0] HD Pro Webcam C920` `[1] Yealink UH36`
`[2] Microsoft Teams Audio`, no loopback/virtual audio device of any kind.

Evidence in this folder:

* `runtime-macos.txt` - every runtime transcript (the isolated flag, the `agenteyes audio` run with
  `volumedetect`, the live ffmpeg command line out of the log, the AVFoundation wording, the
  `raw.mp4.ffmpeg.log`, the Control API transcript, and the two permission blockers).
* `guard-mutation.txt` - the regression guard run against the flag deliberately put back, showing
  it FIRES, plus the green run after reverting.

## What changed

| File | Change |
|------|--------|
| `src/AgentEyes.Core/Platform/Mac/FfmpegArgs.Mac.cs` | `AvfAudioInput` no longer emits `-audio_buffer_size 20`. The doc comment that claimed this was "the macOS twin of the dshow -audio_buffer_size guard (issue #125)" now says the opposite in full: AVFoundation has no buffer option of any name, lists the options it does have, and states that macOS does not get the #125 mitigation. |
| `src/AgentEyes.Core/Platform/Mac/AvfStream.cs` | `Arguments` no longer emits it either. Same correction to the comment that called it "the AVFoundation form of the issue #125 lesson". This is the one that powers `AudioCapture` (the mic level meter) and `LoopbackCapture`. |
| `src/AgentEyes.Core/PlatformApiNames.cs` (new) | Two compile-time constants naming the capture API this build talks to: `DirectShow` under `WINDOWS`, `AVFoundation` under `MACOS`, `PulseAudio`/`V4L2` otherwise. |
| `src/AgentEyes.Core/DeviceResolver.cs` | The six hardcoded "DirectShow" failure messages (lines 55, 76, 81, 105, 123, 128 of the old file) now name `PlatformApiNames.Audio` / `.Camera`. The Windows text is byte-for-byte what it was, by construction. Two stale doc comments corrected. |
| `src/AgentEyes.Core/Video/FfmpegRecorder.cs` | `DiagnoseImmediateExit`: a new first branch for `Unrecognized option` / `Option not found` says ffmpeg rejected an option in OUR command line and the device is not the problem; the mic branch names `PlatformApiNames.Audio`. This is the message the issue quotes as blaming the wrong platform. |
| `tests/AgentEyes.Tests.Mac/MacFfmpegArgsTests.cs` | The two expected argument lists that ASSERTED the bug (lines 23 and 189) no longer contain it. |
| `tests/AgentEyes.Tests.Mac/MacForeignFfmpegOptionTests.cs` (new) | The regression guard - see below. |
| `tests/AgentEyes.Tests.Mac/MacDeviceWordingTests.cs` (new) | The macOS wording: a failed/ambiguous mic match, a failed camera match, and the two `DiagnoseImmediateExit` cases. |
| `tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj` | Stamps `RepoRoot` into the assembly, as `tests/AgentEyes.Tests` does, so the guard's source backstop cannot silently scan nothing. |

Untouched, deliberately: `src/AgentEyes.Core/Video/FfmpegArgs.cs:64` and
`src/AgentEyes.Core/AlwaysOn/AlwaysOnArgs.cs:98` keep `-audio_buffer_size` (correct on dshow,
issue #125), and the Linux `-fragment_size` is correct on pulse.

## The regression guard, and what it cannot see

`MacForeignFfmpegOptionTests` is four tests. It does NOT hand-list call sites: it REFLECTS over
every static argument builder on `FfmpegArgs` (both partials - the shared/Windows one and
Platform/Mac's) and on `AvfStream`, INVOKES each with a synthesized fixture, and inspects the
argument values actually emitted. Twelve option names belonging to another platform's demuxer are
checked (the dshow set including `audio_buffer_size`, plus pulse's `fragment_size`).

It fails closed:

* a builder that cannot be invoked is a FAILURE, not a skip (so adding a builder with a parameter
  type the fixture does not know breaks the test rather than shrinking it);
* the four AVFoundation builders are asserted present BY NAME, so a rename cannot quietly empty the
  sweep;
* `TheGuardFires_OnTheExactFlagThatShipped` runs the detector against the pre-fix argument list, so
  the detector is proven to fire in the suite itself, not just in my mutation run;
* `TheGuardDoesNotTouchTheWindowsDshowMitigation` asserts the gdigrab/dshow list still CONTAINS
  `-audio_buffer_size` and is not classed as an AVFoundation list - fixing this issue must not have
  meant breaking Windows.

Blind spots, stated rather than left to be found: the reflection sweep cannot see an argument list
built somewhere other than those two types, which is why `NoForeignOptionInAnyMacSourceFile` also
scans every `Platform/Mac/*.cs` for the same option names with comments stripped; that source scan
in turn is defeated by an alias or a computed string. Neither sees whether ffmpeg accepts the REST
of the list - only a real capture does, which is section 2 of `runtime-macos.txt`.

## Acceptance criteria

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | `grep -rn "audio_buffer_size" src/AgentEyes.Core/Platform/Mac/` returns nothing | PASS | the grep prints nothing and exits 1. This criterion and "correct the comments" pull against each other, and the criterion wins: the corrected comments say the option belongs to DirectShow alone and that AVFoundation has no buffer option of any name WITHOUT spelling its name, so the owner's grep stays clean. Both comments say why they are worded that way. The exact name lives in the guard's `ForeignOptions` list and in this folder. |
| 2 | The two Windows uses still there | PASS | `grep -rn "audio_buffer_size" src/AgentEyes.Core/Video/FfmpegArgs.cs src/AgentEyes.Core/AlwaysOn/AlwaysOnArgs.cs` -> `FfmpegArgs.cs:64` (`"-audio_buffer_size"` / `"80"`) and `AlwaysOnArgs.cs:98`. Also asserted by `TheGuardDoesNotTouchTheWindowsDshowMitigation`. |
| 3 | `agenteyes audio --screen 1 --mic "<real mic>" --seconds 5` exits 0, ~5 s file, mean volume not `-inf` | PASS | `runtime-macos.txt` section 2: exit 0, `Duration: 00:00:05.09`, `mean_volume: -58.8 dB`, `max_volume: -39.3 dB`, and the log line showing ffmpeg exit=0 with 488448 bytes of PCM. |
| 4 | `POST /record/start` video+mic -> 200, `/status` recording with non-zero `MicLevel`, `/record/stop` leaves a playable file with an audio stream | **BLOCKED - NOT PASSED** | `runtime-macos.txt` sections 6 and 7. Start DOES return 200 with `State: recording` now (pre-fix: 400, exit 8), but the screen input delivers no frames without the Screen Recording permission, so no `raw.mp4` and no `recording.mp4` exists, and `MicLevel` reads 0.000 because a detached/service process on this Mac gets a microphone that delivers no samples. Both are owner-granted permissions. |
| 5 | The recording's `raw.mp4.ffmpeg.log` contains no `Unrecognized option` line | PASS | `runtime-macos.txt` section 5: the 714-byte log quoted in full, `grep -c` = 0. The file is non-empty, so the zero is an answer and not a missing instrument. |
| 6 | A failed `--mic` match on a Mac names AVFoundation; the Windows wording unchanged on Windows | PASS on macOS; Windows half by construction + compile only | `runtime-macos.txt` section 4 (three real CLI messages) and `MacDeviceWordingTests`. The Windows half: `PlatformApiNames.Audio` is the literal `"DirectShow"` under `#if WINDOWS` and the format strings are otherwise unchanged, and the Windows TFM compiles here (0 errors) - but the Windows suite CANNOT RUN on this Mac (no `Microsoft.WindowsDesktop.App` runtime for osx-arm64), so `DeviceResolverTests` / `FfmpegRecorderTests` are unrun by me. CI's windows job runs them. |
| 7 | A regression test fails if a DirectShow-only option name reappears in any `FfmpegArgs.Mac` / `AvfStream.Arguments` list | PASS | `MacForeignFfmpegOptionTests` + `guard-mutation.txt` (3 of 4 fire on the reintroduced flag). |
| 8 | `dotnet build AgentEyes.sln -c Release` and `dotnet test AgentEyes.sln -c Release` both pass | PARTIAL ON THIS MACHINE - see below | |

## The gate on a Mac mini, stated plainly

`dotnet build AgentEyes.sln -c Release` as written FAILS here with `NETSDK1100` for the three
Windows-TFM projects (AgentEyes.App, AgentEyes.Tests, AgentEyes.Setup) - macOS cannot build a
Windows target without being told to. What I ran instead, all from this branch:

    dotnet build AgentEyes.sln -c Release -p:EnableWindowsTargeting=true   -> 0 Error(s)  (22 warnings, pre-existing)
    dotnet build src/AgentEyes.Core -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac -t:Rebuild    -> 0 Error(s)
    dotnet test  tests/AgentEyes.Tests.Mac   -c Release   -> Passed!  Failed: 0, Passed: 71
    dotnet build src/AgentEyes.Core -c Release -f net8.0 -p:AgentEyesUnixPlatform=linux -t:Rebuild  -> 0 Error(s)
    dotnet test  tests/AgentEyes.Tests.Linux -c Release   -> Passed!  Failed: 0, Passed: 93, Skipped: 1
    dotnet build src/AgentEyes.Core -c Release -f net8.0-windows10.0.19041.0 -p:EnableWindowsTargeting=true -t:Rebuild -> 0 Error(s)

So: the WHOLE SOLUTION compiles (including the WPF app and the Windows test project), the macOS and
Linux suites are green, and `dotnet test AgentEyes.sln` is NOT RUNNABLE on macOS because the
Windows suite needs the WindowsDesktop runtime. The Mac suite was 60 tests before this change and
is 71 now (+11: 4 guard, 7 wording). Environment notes for anyone repeating this: `dotnet` is not
on PATH in an agent shell (`export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH`),
there is no .NET 8 SDK or runtime here, so `DOTNET_ROLL_FORWARD=LatestMajor` is needed to run the
net8.0 test assemblies on the 10.x runtime. `-t:Rebuild` matters: the mac and linux configurations
of Core build to the SAME path, so switching between them without it can leave a stale assembly.

## What is NOT proven, and what QA should do about it

1. **AC4 - the video recording through the Control API. Still outstanding.** The macOS Screen
   Recording permission is not granted to `agenteyes` on this machine, and only the owner can grant
   it (System Settings > Privacy and Security > Screen and System Audio Recording). Observed
   failure, verbatim: `[AVFoundation indev] Configuration of video device failed, falling back to
   default.` followed by `[FfmpegRecorder] Stop: pipeline did not reach 13788ms within 5s (reached
   0ms)` and no `raw.mp4` at all. This issue cannot be called fully verified until a Mac with that
   permission runs AC4.
2. **A non-zero `/status` MicLevel needs the Microphone permission for the SERVICE.** A detached
   process gets no samples here - proven with plain ffmpeg, orphaned from the shell, hung on a
   `-t 3` capture with no file (`runtime-macos.txt` section 7). A terminal child of the same build
   writes 488448 bytes of real audio. If QA wants the meter proven, grant Microphone to `agenteyes`
   in System Settings, or drive the CLI from a terminal session rather than the launchd agent.
3. **The Windows suite is unrun by me** (see AC6 row). Please let CI's windows job, or a Windows
   machine, confirm `DeviceResolverTests` and `FfmpegRecorderTests` - `DiagnoseImmediateExit` gained
   a branch, and `DiagnoseImmediateExit_with_mic_and_no_region_error_points_at_the_mic` (stderr
   `"could not run graph"`, asserts `Contains("DirectShow")`) is the one to look at first. It passes
   by construction: that stderr contains neither `Unrecognized option` nor `Option not found`, so
   the new branch does not take it, and `PlatformApiNames.Audio` is `"DirectShow"` on Windows.

## Two judgment calls to review as drift

* **`FfmpegRecorder.DiagnoseImmediateExit`** is not in the issue's IN list, which names
  `DeviceResolver.cs` only. I changed it because the issue's own problem statement calls it out
  ("the error message blames the wrong platform, sending a reader after the device name instead of
  the argument list") and because it is the exact message in the bug report. Bounce it if you
  disagree; nothing else depends on it.
* **`PlatformApiNames` also changes Linux wording** ("no PulseAudio microphone matches", "no V4L2
  camera matches") because the resolver is shared and the alternative was a macOS-only special
  case. The issue flagged this as the negotiable part of the fix.

Not fixed, and worth their own issues: `src/AgentEyes.Core/Video/FfmpegCameraRecorder.cs:1219` still
says "no DirectShow device is named ... any more" on a Mac, and `FfmpegCameraPreview` has two
"needs an exact DirectShow device name" guards. They are camera-stranding diagnostics, outside this
issue's scope. Also, a hung `ffmpeg -f avfoundation -i 1:none` from the ORIGINAL repro (10:02, 50
minutes before my first run) was still alive on this machine when I finished; I left it alone.

## How QA can re-run what I ran

The installed service (`launchd com.agenteyes.recorder`, `~/Library/Application
Support/AgentEyes/bin`, CLI symlink `~/.local/bin/agenteyes`) is currently built FROM THIS BRANCH -
I re-ran `scripts/mac/install.sh` so the Control API transcript came from this code. `dotnet build`
alone would not have changed it.

* CenCon impact: no drift. No component map or privacy-posture change - this removes an argument
  that prevented capture; nothing about "visible / controllable" moves.
* Suggested smokes: none of the PowerShell smokes run on macOS. The targeted equivalents are the
  two in `runtime-macos.txt`: `agenteyes audio --screen 1 --mic "Yealink" --seconds 5` plus
  `ffmpeg -i <file> -af volumedetect -f null -`, and the `/record/start` -> `/status` ->
  `/record/stop` sequence against `127.0.0.1:7882`. `POST /record/stop` needs a body
  (`-d '{}'`) or HttpListener answers `411 Length Required`.
* The room is quiet: play sound while capturing (`afplay -v 10 /System/Library/Sounds/Sosumi.aiff`
  in a loop) or the file is honest silence and proves nothing.
