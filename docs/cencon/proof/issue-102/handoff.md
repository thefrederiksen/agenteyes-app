# Issue 102 handoff - AgentEyes Core on Linux

## Who wrote this note, and why that matters

The Linux implementation in commits 4ff09f4, 9dc476a and 73d094a was NOT written by whoever is
writing this note. Those commits were made earlier and the issue was never handed off: at the
time this note was added, issue 102 had no pull request, no handoff note, and no comments, while
sitting on `flow:ready-dev`.

This note exists so the work stops being invisible. It is NOT a claim that issue 102 is done. It
records exactly which of its acceptance criteria have evidence and which do not, from a machine
that cannot test Linux.

The verification below was all performed on a Mac mini (Apple M4, macOS 26.5.2). **No Linux
machine has run any of this code.**

## Acceptance criteria against available evidence

| # | Criterion | Status |
|---|-----------|--------|
| AC1 | Builds on Linux; `screens` lists X11 monitors and pulse sources | **PARTIAL** - compiles, listing unverified |
| AC2 | `GET /devices` lists monitors, pulse mics, monitor source, v4l2 cameras | **NOT VERIFIED** |
| AC3 | `POST /record/start` starts screen + mic + system audio; status recording | **NOT VERIFIED** |
| AC4 | `POST /record/stop` yields mp4, transcript, thumbnail, manifest title | **NOT VERIFIED** |
| AC5 | Runs as `systemd --user` unit, enabled at login, no terminal | **NOT VERIFIED** |
| AC6 | Credential file is mode 0600 | **NOT VERIFIED** |
| AC7 | Windows build and test green in CI | **NOT VERIFIED** - no CI run linked |
| AC8 | New pure logic has unit tests that run on Linux | **PARTIAL** - they pass, but on macOS |

### What IS evidenced

**AC1, the compile half.** The Linux configuration builds clean from this tree:

    dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -p:AgentEyesUnixPlatform=linux
    -> Build succeeded.   0 Error(s)

The resulting assembly provably contains the Linux capture backend - `x11grab`, `pulse`, `v4l2`
and `xrandr` are all present as UTF-16 literals, confirmed by byte-count rather than by reading
the project file.

**AC8, the tests, with a serious caveat.**

    tests/AgentEyes.Tests.Linux -> Failed: 0, Passed: 47, Skipped: 1

The caveat is the important part, and it is the reason this note does not mark AC8 as met. Those
47 tests assert the TEXT of Linux command lines - that a built argument string contains
`x11grab`, that a parser handles a sample of `xrandr` output, that a source name is
`alsa_input.mic`. They are pure string builders and parsers. They pass identically on a machine
with no X11, no PulseAudio and no v4l2, because nothing in them ever opens a device.

That is a proof covering the wrong thing. It demonstrates the engine CONSTRUCTS plausible Linux
commands. It cannot demonstrate that those commands RECORD anything, because no Linux kernel,
X server or sound server was ever involved. AC8 asks for tests "that run on Linux"; these have
run on macOS only.

The 48th test, `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`, asserts the XDG
data home and legitimately cannot hold on a Mac. It previously FAILED here and reported a defect
that did not exist; it is now a `LinuxOnlyFact` and skips off Linux. On Linux it must run, and
QA should confirm it is not skipping there.

### What is NOT evidenced, and cannot be from here

AC2 through AC6 all require a running Linux machine: a real X display, a PulseAudio or PipeWire
server, a v4l2 camera node, a systemd user session and a real recording with its transcript and
title. None of that can be simulated on macOS and none of it was attempted.

The issue's own proof target says it plainly: "A real recording made on devlinux through the
service (screen + mic + system audio) with its finished package listed, `systemctl --user status
agenteyes` active, and the green Windows CI run." **That recording does not exist.** No part of
this note substitutes for it.

AC7 asks for a green Windows CI run, linked. No run is linked. What exists instead is a local
compile of the whole solution from macOS with Windows targeting forced on:

    dotnet build AgentEyes.sln -c Release -p:EnableWindowsTargeting=true
    -> Build succeeded.   0 Error(s)

That shows the Windows TARGET still compiles after the multi-targeting change, which is worth
having, but it is not AC7. The Windows test suite cannot run on macOS: it requires
`Microsoft.WindowsDesktop.App`, which Microsoft does not ship for this platform.

## A defect in this issue's own design, fixed under issue 104

Issue 102 selects its platform with the condition "not Windows", which is not the same thing as
"Linux". On macOS that selected the plain `net8.0` target AND compiled `Platform/Linux` into it,
so a Mac build contained `x11grab`, `pulse` and `v4l2` and told the user to run
`systemctl --user import-environment DISPLAY XAUTHORITY`.

It never actually recorded on a Mac - no `DISPLAY`, no `xrandr`, and no Linux grabbers in the Mac
build of ffmpeg all stopped it first - so the harm was message quality, not corrupt output. But
it compiled and looked like a working build, which is worse than not building.

Issue 104 replaces that two-way guess with a real three-way choice. Nothing in the Linux capture
path was changed by it; the Linux configuration produces the same engine it did before, which the
binary comparison above confirms.

## What QA must still do, on an actual Linux machine

1. Build and run `agenteyes screens` - confirm real X11 monitors and real pulse sources appear.
2. Start the service, `GET /devices`, confirm monitors, microphones, the system-audio monitor
   source and v4l2 cameras are all listed from real hardware.
3. `POST /record/start` with `{"source":"mixed","mic":"<fragment>"}`, confirm `GET /status`
   reports recording, then `POST /record/stop` and OPEN the resulting directory. Play the mp4.
   Read the transcript. A file existing is not a file being correct.
4. `systemctl --user status agenteyes` active after login with no terminal open.
5. `stat -c '%a' <credential file>` returns 600.
6. Link a green Windows CI run for AC7.
7. Confirm the Linux-only test runs rather than skips on Linux.
