# Issue 104 handoff - AgentEyes Core on macOS

Verifying machine: Mac mini, Apple M4 (arm64), macOS 26.5.2 (build 25F84), ffmpeg 9.0.2 from
Homebrew, one display at 1920x1080, NO audio input devices of any kind.

Read the last section before trusting any of the green results above it.

## Acceptance criteria, one by one

### AC1 - macOS build clean

    dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release
    -> Build succeeded.
    -> 0 Error(s)   (1 warning, CS8602 in AlwaysOn/AlwaysOnEngine.cs:901, PRE-EXISTING on main
                     from #86 - not touched by this issue or by #102)

### AC2 - the same tree still builds the Linux configuration on demand

    dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux
    -> Build succeeded.
    -> 0 Error(s)

Implemented with a new `AgentEyesUnixPlatform` property: `mac` when MSBuild reports OSX,
`linux` otherwise, overridable from the command line. Three Unix-generic helpers (ToolRunner,
WavStreamWriter, CredentialFile) are shared through a `UnixSharedCompile` item; the rest of each
platform folder is excluded from the other.

NOTE for QA: both configurations build to the SAME output path, so only one of them exists on
disk at a time. The two test projects pin the configuration they test through
`AdditionalProperties` on their project reference, which is what stops the Linux suite from
silently compiling against the macOS engine. That was a real regression during development: the
Linux suite broke outright on a Mac until the pin was added.

### AC3 and AC4 - the platform split, asserted in BOTH directions

The check is `.github/workflows/ci.yml`, step "The macOS build must carry no Linux capture code".
Run locally against each build:

    macOS build        -> [OK] macOS build carries AVFoundation and no Linux capture code     exit 0
    forced Linux build -> [FAIL] the macOS build contains 'x11grab'
                          [FAIL] the macOS build contains 'pulse'
                          [FAIL] the macOS build contains 'v4l2'
                          [FAIL] the macOS build contains 'xrandr'
                          [FAIL] the macOS build does NOT contain 'avfoundation'
                          [FAIL] the macOS build does NOT contain 'screencapture'        exit 1

A third direction was tested deliberately, because "no forbidden word found" is an ABSENCE and an
absence certifies nothing if the check is not looking at a real file. The assembly was moved
aside and the guard re-run:

    no assembly present -> [FAIL] src/.../agenteyes.dll was not built                     exit 1

So the guard cannot pass by finding nothing. QA should re-run all three.

### AC5 - `agenteyes screens` is macOS-shaped, with no Linux vocabulary

    MONITORS (AVFoundation + system_profiler)
      #  Resolution      Primary  Name
      1   1920 x 1080     yes      Display 1

      Note: a region is cropped inside ONE display on macOS - a region
            spanning two monitors cannot be recorded.

    MICROPHONES - AVFoundation audio inputs
      (none found)

    SYSTEM AUDIO (--mix / --loopback record this)
      (unavailable: this Mac lists no audio input at all. macOS needs a virtual audio device)

No mention of DISPLAY, xrandr, X11, PulseAudio or systemd anywhere in the output.

### AC6 - the headless Control API starts and answers on macOS

    agenteyes serve --port 7903

    GET /health  -> {"ok": true, "app": "AgentEyes", "host": "headless", "platform": "macos"}
    GET /status  -> {"State": "idle", "Mode": null, ... }
    GET /devices -> monitors: [{Index 1, 1920x1080, Primary true, Name "Display 1", X 0, Y 0}]
                    mics: []   dshow: []   cameras: []
                    systemAudio: "(unavailable: this Mac lists no audio input at all ...)"

This is the criterion with the most value in it: before this change `serve` refused to start on a
Mac at all, so the Control API - which the project instructions name as the most reliable way to drive and
inspect AgentEyes - was unreachable on the platform.

### AC7 - a missing permission fails fast and names itself

    agenteyes shot --screen 1   -> exit 1, in under one second
    [error] screencapture failed (exit 1): could not create image from rect. On macOS the usual
    cause is the Screen Recording permission: grant it in System Settings > Privacy and Security >
    Screen and System Audio Recording, to the application that launches AgentEyes, then try again.
    Nothing in AgentEyes can grant it - macOS only accepts that from the person at the keyboard.

A control test proves this is the permission and not a defect in AgentEyes: Apple's own
`/usr/sbin/screencapture -x -t png <file>` run directly from the same process tree fails the same
way, with "could not create image from display" and no file produced.

Before this change the equivalent attempt through ffmpeg AVFoundation HUNG for over two minutes
and produced no file and no error, which is why screenshots deliberately use `screencapture`
rather than ffmpeg and break the Linux "one grabber for everything" symmetry.

### AC8 - features that do not exist on macOS say so, and do not blame Linux

    agenteyes selftest   -> exit 4, "not available on macOS"
    agenteyes always-on  -> exit 4, "not available on macOS"
    agenteyes record status -> "Start it: launchctl kickstart -k gui/$UID/com.agenteyes.recorder
                               (logs: ~/Library/Application Support/AgentEyes/logs)"

The same `record status` command built for Linux prints the systemctl form instead, which is how
the per-platform hint was verified rather than assumed.

Always-on recording remains Windows-only. The product is named for always-on recording, so this
is a real and deliberate gap, declared OUT of scope in the issue.

### AC9 - the test projects

    tests/AgentEyes.Tests.Mac    -> Failed: 0, Passed: 55, Skipped: 0
    tests/AgentEyes.Tests.Linux  -> Failed: 0, Passed: 47, Skipped: 1

The one skip is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, which asserts a
Linux path convention and legitimately cannot hold on a Mac. It previously FAILED here, reporting
a defect that did not exist. It is now a `LinuxOnlyFact` and skips off Linux.

### AC10 - the Windows target still compiles

    dotnet build AgentEyes.sln -c Release -p:EnableWindowsTargeting=true
    -> Build succeeded.   0 Error(s)   22 Warning(s)

Both `net8.0` and `net8.0-windows10.0.19041.0` of Core compile, and so do AgentEyes.App,
AgentEyes.Tests, AgentEyes.Setup and AgentEyes.Setup.Cli.

This is a COMPILE check only. The Windows test suite cannot be RUN here, and that is now measured
rather than assumed:

    dotnet exec tests/AgentEyes.Tests/bin/x64/Release/net8.0-windows10.0.19041.0/AgentEyes.Tests.dll
    -> Framework: 'Microsoft.WindowsDesktop.App', version '8.0.0' (arm64)
    -> No frameworks were found.

Microsoft does not ship the Windows Desktop runtime for macOS, so the main suite is buildable
here and never runnable here. QA on Windows must still run it.

(Note on the x64 path above: `-c Release` lands in `bin/x64/Release/` because the project sets
`<Platforms>x64</Platforms>`. The project instructions warn that a stale `bin/Release/` has already caused one
false test failure. The macOS-only projects do not set Platforms, which is why their output is at
`bin/Release/net8.0/`.)

## How QA should exercise this

On macOS, with the Screen Recording permission GRANTED to the application that launches
AgentEyes (System Settings > Privacy and Security > Screen and System Audio Recording):

1. `agenteyes shot --screen 1` - must now WRITE A FILE. Open it and look at it. A blank or black
   image is a STOP-and-diagnose, not a pass.
2. `agenteyes video --screen 1 --seconds 5` - must produce a playable file. Play it.
3. `agenteyes serve` then `POST /record/start {"source":"screen"}`, `GET /status`,
   `POST /record/stop` - check the returned directory actually contains the recording.
4. `bash scripts/mac/install.sh`, then `launchctl print gui/$UID/com.agenteyes.recorder`, then
   confirm `agenteyes record status` reaches the service with no terminal open.

Areas worth a smoke: none of the Windows smokes apply (no tray application on macOS). The
equivalent here is the Control API sequence in step 3.

## What is NOT proven - read this before trusting anything above

**No frame of video and no sample of audio has been captured on this machine.** All 55 macOS
tests are pure logic by design - they construct command lines, parse device listings and compute
geometry. That is why they run with no permission and no hardware, and exactly why they say
NOTHING about whether a real recording is correct. A green suite here is not evidence of capture.

1. **Screen capture is unproven.** The permission is not granted on this machine and only a
   person at the keyboard can grant it. No dialog appears when AgentEyes asks, which is
   consistent with a previous refusal.
2. **The microphone path cannot be exercised here at all.** The machine reports zero audio input
   devices - it has no built-in microphone. This is not a permission problem and granting
   anything will not fix it.
3. **System sound is unproven.** macOS has no native loopback. The engine looks for an installed
   virtual audio device (BlackHole, Loopback, Soundflower, Existential, Aggregate - that order)
   and otherwise fails with install steps. None is installed here, so only the failure path has
   ever run. It never records silence and never substitutes the microphone.
4. **The launchd agent and its installer have NEVER BEEN RUN.** `scripts/mac/install.sh` is
   `bash -n` clean and that is all. An installer that has never installed anything is not a
   tested installer.
5. **Multi-display correlation is unexercised** - one display here. **The camera path is
   unexercised** - no camera here. **Whisper transcription on Apple silicon is unexercised** -
   the arm64 and Metal natives are present in the package but were never loaded, as that needs a
   model download.
6. **The runtime is wrong.** No .NET 8 runtime is installed on this machine; only .NET 10. Every
   build and test above ran on .NET 10 by roll-forward (`DOTNET_ROLL_FORWARD=LatestMajor`). This
   is NOT the runtime the product ships on.
7. **Nothing here has been reviewed by anyone else.**

---

## Added after the merge: continuous integration evidence

Run **38791305765** against commit `b0cb221`, the content merged to main:

| Job | Runner | Result |
|---|---|---|
| macos | `macos-latest` | Failed: 0, Passed: 55; guard reports `[OK] macOS build carries AVFoundation and no Linux capture code` |
| linux | `ubuntu-latest` | Failed: 0, Passed: 48, Skipped: 0 |
| windows | `windows-latest` | Failed: 0, Passed: 2135 |

This upgrades AC10 from a local compile check to a real result: the Windows suite does not merely
compile after the platform split, it passes in full on a Windows machine. It also means the
macOS platform guard now runs on every pull request and branch push, on a hosted macOS runner
that has no granted Screen Recording permission and no audio device - which is exactly why the
macOS tests were written as pure logic.

Still unchanged: a hosted runner cannot grant a screen-recording permission either, so CI being
green remains silent on whether AgentEyes records anything. Every limitation in the section above
stands.
