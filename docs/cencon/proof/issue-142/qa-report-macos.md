# Issue #142 - Wave A QA report: macOS (devthrottle-mac-mini)

**Headline: on this Mac, the recorder installs and records correctly - screen-only, screen plus
microphone, and a ten-minute take that is fully playable - and the two defects #114 was filed for are
gone, measured in both directions. The desktop-app rows cannot be run here because no macOS desktop
app exists in the tree, and I found two problems in how this mission tells seats to identify a build.**

| | |
|---|---|
| Platform | macOS 26.5.2 (build 25F84), arm64 |
| Machine | `devthrottle-mac-mini` - Mac16,10, 10 cores, 16 GB |
| Commit tested | **`main` at `f9ef230`** - the current head of `origin/main` |
| Installed by | `scripts/mac/install.sh` (the platform's supported route) |
| Answering build | `agenteyes.dll` sha256 `1b9092d0...4111ddb0`, service **pid 39753** |
| ffmpeg | 9.0.2 (Homebrew, `/opt/homebrew/bin/ffmpeg`) |
| Date | 2026-11-15 |
| Screen state | **LOCKED** for the whole run - see "The screen was locked" below |
| Load | 2.4 - 2.9 on 10 cores throughout; no stray `ffmpeg` found before, during or after |

## What I tested, and what this report therefore does NOT certify

I tested `main` at `f9ef230`. **It does NOT contain #113.** #113 has passed QA and is waiting at
`flow:ready-gate` for an independent Review Gate.

That is deliberate, and the lead checked it rather than enforcing this issue's original "wait for the
whole Phase 0 stack" rule: #113 is about the **failure** path - a capture that silently stops producing
output - whereas rows 4, 5 and 6 are all **happy** path. So:

- **Rows 4, 5 and 6 below certify the happy path only.**
- **Nothing in this report says a stalled capture is handled.** On the build I tested, it is not.
  Resilience to a stalled capture is covered by #113's own verification, not by any row here.
- A reader must not read row 6's pass as "a ten-minute recording survives a freeze". It means a
  ten-minute recording that does not freeze completes and is playable.

I confirmed #113's absence in the answering binary in two independent ways - a content marker count
and the recorder's own ffmpeg command line - both shown under "Which build answered" below.

`f9ef230` contains #114, #128 and #136.

## The gate (the macOS gate, not CLAUDE.md's)

`AgentEyes.sln` cannot build on a Mac - it contains the WPF app - so CLAUDE.md's gate does not apply
here and was not run. The macOS gate is four commands, and all four passed:

| Command | Result |
|---|---|
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` | **0 errors** (1 pre-existing warning, `AlwaysOnEngine.cs(901,47)` CS8602) |
| `dotnet test tests/AgentEyes.Tests.Mac -c Release` | **274 passed, 0 failed**, 0 skipped |
| `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=linux` | **0 errors** |
| `dotnet test tests/AgentEyes.Tests.Linux -c Release` | **281 passed, 0 failed, 1 skipped** |

`dotnet` is not on this machine's PATH; it is at `~/.dotnet` and the gate needs
`export PATH="$HOME/.dotnet:$PATH"` and `DOTNET_ROLL_FORWARD=LatestMajor`.

**The fourth row is not a Linux run.** It is the Linux test *project* executed on a macOS *host*, and
the suite says so itself: the one skipped test is `LinuxPathsTests.MachineLocalAppData_OnLinux_IsTheXdgDataHome`,
which skips precisely because the host is not Linux. I quote it as a macOS-host run of the Linux
project and nothing more. That suite also pins the app to the linux platform and shares
`bin/Release/net8.0`, so I rebuilt with `-p:AgentEyesUnixPlatform=mac` before installing or inspecting
anything.

## The screen was locked

Checked first, before any capture:

```
ioreg -n Root -d1 -r -k IOConsoleUsers | tr ',' '\n' | grep CGSSessionScreenIsLocked
    "CGSSessionScreenIsLocked"=Yes
```

It stayed locked for the whole run. A locked Mac composites the wallpaper alone - no menu bar, no
Dock, no window. **That is not a blank render, not a broken capture and not a product defect**, and no
row here depends on what is on the screen. What rows 4 and 5 prove is that the capture pipeline
produces a correctly timed, fully decodable picture; they do not prove that any particular thing was
displayed. I read a frame out of every take rather than assuming, and each one is a complete,
correctly rendered image of the lock screen - see `macos/frames/`.

## Which build answered - and two problems with how we have been deciding that

Rule 3 of #142 says to establish which build is answering and tie every quoted line to it. I did, and
in doing so found that **the method circulated for doing it does not work**. Both problems are below
as findings M1 and M2, because the next seat will hit them.

**The chain for this report:**

1. Pre-install service was pid 34201. `scripts/mac/install.sh` ran at 18:43:09 UTC and exited 0.
2. Post-install service is **pid 39753** - a genuinely new pid, so #138's bootout/bootstrap race did
   not silently leave the old binary running this time. (A new pid is the presence; "the script
   printed [OK]" would not be.)
3. `lsof -p 39753` shows that process holding
   `~/Library/Application Support/AgentEyes/bin/agenteyes.dll`, the exact file I hashed:
   sha256 `1b9092d077892723730e042bf062eb18b32964326e3d67fa228df0874111ddb0`.
4. pid 39753 answered every measurement in this report. It did not restart during the run.

**That it is `f9ef230` and not #113, shown two independent ways:**

| Marker | Encoding | #113 branch (`ad21557`) | **the answering build** |
|---|---|---|---|
| `frag_keyframe` | UTF-16LE (a string literal) | 1 | **0** |
| `CaptureLiveness` | UTF-8 (a C# identifier) | 3 | **0** |
| `FragmentedOutput` | UTF-8 (a C# identifier) | 1 | **0** |
| `CaptureWatchdog` | UTF-8 (a C# identifier) | 1 | **0** |
| `capture-stalled` | UTF-16LE | 0 | 0 |
| `MuxInTwoPasses` | UTF-8 | 1 | 1 |

The instrument is shown working in both directions: each of the top four markers is **present on the
build that has #113 and absent on the build that does not**, measured in this session. `capture-stalled`
reads 0 in both trees and `MuxInTwoPasses` reads 1 in both, so neither discriminates anything - the
lead had already said so and I confirm it.

The platform arm is also shown both ways, so it cannot pass by looking at nothing:

| Literal | mac build | linux build |
|---|---|---|
| `avfoundation` | 2 | 0 |
| `screencapture` | 4 | 0 |
| `x11grab` | 0 | 1 |
| `pulse` | 0 | 8 |
| `v4l2` | 0 | 1 |
| `xrandr` | 0 | 4 |

**And a second, behavioural confirmation that needs no byte counting at all.** #113 adds
`FragmentedOutput()` to the macOS capture arguments. The ffmpeg command line the recorder actually ran,
recorded in every manifest, is:

```
ffmpeg -y -f avfoundation -thread_queue_size 1024 -capture_cursor 1 -pixel_format nv12 \
  -framerate 30 -i 0:none -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 -r 30 raw.mp4
```

There is no `-movflags +frag_keyframe+empty_moov+default_base_moof`. The running recorder is not
applying #113's fragmented output. That is the build identified by what it *did*, not by what it
contains, and it agrees with the marker table.

## The acceptance matrix on this machine

| # | Check | Result |
|---|---|---|
| 1 | Installs by the platform's supported route | **PASS** - `scripts/mac/install.sh` exit 0, `/health` answers, new pid. #147 and #148 do not reproduce here; #138 did not fire on this install |
| 2 | Launches; the main window opens | **NOT RUNNABLE** - no macOS desktop app exists (#118). Screen also locked |
| 3 | The library lists existing recordings | **NOT RUNNABLE** - same, #118 |
| 4 | Records the screen, no audio | **PASS** - 639 frames / 21.300 s = 30.0000 fps, frame error 0, full decode exit 0. Package stage hits known #126 |
| 5 | Records screen + microphone (the #114 check) | **PASS** - 30.0000 fps, frame error 0; audio accounted for to 4 ms; mux done on attempt 1 |
| 6 | A 10-minute recording completes and is playable (the #113 check) | **PASS** - **`moov` at offset 332,364,545**, 18,170 frames / 605.667 s = 30.0000 fps, full decode exit 0. See the scope note: this is the happy path, NOT stall resilience |
| 7 | Plays a recording back in the app | **NOT RUNNABLE** - no macOS desktop app (#118) |
| 8 | Detail view, settings and dictionary open and save | **NOT RUNNABLE** - no macOS desktop app (#118) |
| 9 | Tray / menu-bar icon shows idle and recording | **NOT RUNNABLE** - no macOS desktop app (#118) |
| 10 | Single instance: a second launch raises the first window | **NOT RUNNABLE** - no macOS desktop app (#118) |
| 11 | Camera + microphone recording | **NOT RUNNABLE** - this Mac has no camera; `GET /devices` returns `"cameras": []` |
| 12 | Always-on keeps the stretches with sound | **NOT RUNNABLE** - depends on Phase 5, not started; always-on is Windows-only in the engine today |

Wave A on macOS is rows 1, 4, 5 and 6: **four run, four PASS.** The other eight are not runnable on
this machine and each says why above - none is blank.

> **Superseded for rows 2, 3, 7, 8, 9 and 10 on 2026-11-16.** #118 merged, so a macOS app now exists
> and those six rows have been run. Their current verdicts are in **"Wave B, 2026-11-16 - the macOS
> application rows"** at the end of this report. The "NOT RUNNABLE" entries above are kept as the
> record of what was true on 2026-11-15.
>
> **Rows 4, 5 and 6 were re-run on 2026-11-16 against `main` at `b0e6ae8` (with #113 and #156).**
> Current verdicts: row 4 PASS, row 6 PASS on the sharpened criterion, **row 5 FAIL as measured on a
> virtual input** - see **"Wave A re-run, 2026-11-16"** at the end of this report. The rows above are the
> record of `f9ef230` on a physical microphone.
>
> **Row 5 re-run again on 2026-11-16 evening on the PHYSICAL Yealink UH36, same build `b0e6ae8`: PASS,
> 2 of 2 takes (0.614 s and 0.670 s), plus a single 3661 s drift take (1.094 s).** See **"Row 5 on the
> physical microphone, and the hour-scale drift take"** at the end of this report. That verdict
> supersedes the virtual-input FAIL for row 5.

## Row 1 - Installs by the platform's supported route: PASS

`scripts/mac/install.sh`, transcript in `macos/install-transcript.txt`. Exit 0, every step `[OK]`,
and `/health` answers `{"ok":true,"app":"AgentEyes","host":"headless","platform":"macos"}`.

I measured the failing arm first, by accident and then kept it: run without `dotnet` on PATH, the
script stops at its precondition check with

```
[FAIL] dotnet is not installed (see https://dotnet.microsoft.com/download)     exit=1
```

That is the guard behaving exactly as CLAUDE.md's "no fallback programming" rule requires - it fails
explicitly, names the missing thing and gives the fix - and it means the `[OK] built` on the second
run is a real result and not an unchecked path. (`dotnet` lives at `~/.dotnet` on this machine and is
not on PATH; that is the machine's setup, not a product defect.)

**The two Windows installer defects do not reproduce here.**

- **#147** (installer sets `DOTNET_BUNDLE_EXTRACT_BASE_DIR` user-wide, other apps extract into the
  AgentEyes directory): **absent on macOS, by construction and by measurement.** `install.sh` contains
  no `export`, no `launchctl setenv` and no append to any shell profile; the one environment variable
  it sets is `PATH`, scoped *inside* the launchd plist where it belongs. The same grep that finds
  nothing in the script does find that `PATH` key in the plist, so the grep is working.
  `launchctl getenv DOTNET_BUNDLE_EXTRACT_BASE_DIR` reads empty - and I proved that instrument reports
  a present value differently by setting a probe variable, reading it back as `present-1234`, and
  unsetting it. There is also no bundle to extract: the macOS publish is self-contained but not
  single-file (192 files, 13 loose `.dylib`s), so the Windows mechanism has no macOS analogue at all.
- **#148** (`install --release-dir` is a no-op over a different build with the same version): **absent
  on macOS.** There is no version comparison anywhere in `install.sh` - lines 83-84 `rm -rf "$lib"/*`
  and `cp -R` unconditionally. Proven empirically rather than only by reading: see "The #148 probe"
  below, where installing a different build carrying the *same* version string `1.11.11` replaced the
  binary and changed its hash.
- **#138** (the bootout/bootstrap race silently leaving the old binary running) **did not manifest on
  this install**: pid went 34201 -> 39753. The defect is real and still open; it just did not fire
  here. One clean install is not evidence that the race is gone.

## Row 4 - Records the screen, no audio: PASS

21.11 s take, `source: "none"`, `~/Movies/AgentEyes/2026-11-15_134545_video`.

| Measurement | Value |
|---|---|
| Streams | exactly 1 - `h264`, video. No audio stream, as asked |
| Duration | 21.300 s |
| Frames (decoded, counted) | **639** |
| `avg_frame_rate` | 30/1 |
| Frames / duration | **30.0000 fps** |
| Frame error vs 30 fps | 639 expected, 639 actual - **0** |
| Full decode (`-f null -`) | **exit 0** |
| `moov` | present at offset 12,304,864 |

Frame read at t=10 s (`macos/frames/row4-t10s.jpg`): a complete, correctly rendered 1920x1080 image of
the macOS lock screen - the Lake Tahoe wallpaper, sharp, full colour, with the cursor composited top
right. Not blank, not black.

**Known defect met: #126.** The `package` post-processing stage failed with
`ffmpeg failed during extract audio (exit 234)` - the stage tries to extract audio from a recording
that deliberately has none. That is #126 exactly (open, `flow:ready-dev`), and both other platforms
logged the same thing on this row. The recording itself is unaffected.

## Row 5 - Records screen + microphone: PASS (the #114 check)

31.72 s take, `source: "mic"`, mic `Yealink UH36`, shipping defaults (denoise, gate, levelling all on),
`~/Movies/AgentEyes/2026-11-15_134706_video`.

**#114's two signatures are both gone**, and this is the row they were filed against:

| #114's symptom | What this build did |
|---|---|
| screen capture ran at 13 fps instead of 30 | **975 frames / 32.500 s = 30.0000 fps**, frame error **0** |
| a 30 s recording kept 4.0 s of audio (83% gone) | **every millisecond of audio accounted for** - see below |

The audio track is not merely "long enough"; it is fully explained:

```
mic_native.wav captured          30.976000 s
+ adelay applied by the mux       0.470688 s    (logged: adelay=470.688 ms)
= predicted audio track          31.446688 s
  measured audio track           31.450687 s
  residual                            4.0 ms
```

That `adelay` is the product measuring, per recording, how far the mic's first sample sits after the
first video frame and compensating for it - the alignment #114 introduced. So the audio is not short;
it is correctly placed, with its leading silence where the mic genuinely was not yet running.

Audio is AAC 48 kHz mono, 1476 packets. Video 32.500 s against audio 31.451 s is **96.77%**, which is
lower than Linux's 99.93% - that is addressed in observation O1 below and is a consequence of take
length, not of loss.

**The mux completed on the first attempt** (`"mux": {"State": "done", "Attempts": 1}`). This is the
exact stage that failed 3-of-3 on Windows (#146), and it did not fail here. The mic on this Mac
arrives at **48 kHz** natively, so ffmpeg inserts no resampler after #128's `asetnsamples=n=480:p=1`
framing - which is precisely the condition the Windows seat identified as the trigger. **macOS does not
reproduce #146, and the reason it does not is the same reason the Windows seat gave for why Windows
does.** That is independent corroboration of their diagnosis from a second platform.

The `package` stage failed here for an unrelated and expected reason: `Not signed in to DevThrottle.`
No account is signed in on this machine.

## Row 6 - A 10-minute recording completes and is playable: PASS

605.67 s take (10m 06s), screen + microphone - the only audio this Mac can record -
`~/Movies/AgentEyes/2026-11-15_134854_video`, 332,943,283 bytes.

| Measurement | Value |
|---|---|
| Video | h264, 605.667 s, **18,170 frames decoded** |
| Frames / duration | **30.0000 fps**, frame error **0** |
| Audio | AAC 48 kHz mono, 604.578 s, 28,341 packets |
| **`moov` atom** | **PRESENT at offset 332,364,545, size 578,738 bytes** |
| Full decode of every stream | **exit 0** |
| Mux | `"State": "done"`, **`"Attempts": 1`** - **13.68 s**, measured from the product's own start and done log lines (13:59:00.868 -> 13:59:14.549) |

The `moov` offset is read from a top-level atom walk of the file, not inferred: the file is
`ftyp` (0) / `free` (32) / `mdat` (40) / **`moov` (332,364,545)**. The index is at the end, which is
the normal non-fragmented layout and is what this build should produce - #113's `FragmentedOutput`
is absent here, as established above.

**I read frames from the start, middle and end** - t=5 s, t=300 s and t=603 s
(`macos/frames/row6-t*.jpg`). All three are complete, correctly rendered 1920x1080 images of the lock
screen. The frame at **t=603 s** is the one that matters: the capture was still producing real picture
ten minutes in. That is the presence; "nothing appeared in the log" would not be.

**The audio accounting closes to the same 4 ms as row 5:**

```
mic_native.wav captured         604.096000 s
+ adelay applied by the mux       0.478125 s
= predicted audio track         604.574125 s
  measured audio track          604.578125 s
  residual                           4.0 ms
```

### What row 6 proves about #114, which is more than row 5 could

Before probing this file I wrote down a prediction (`macos/row6-prediction.txt`, timestamped
18:55:10 UTC, before the measurement) that row 6 would discriminate between two explanations of row
5's 96.77% audio/video ratio:

- **(A)** a fixed ~1 s start/stop skew between two independent captures - predicts a ~1 s shortfall on
  a 606 s take, a ratio near 99.8%;
- **(B)** proportional audio loss, which is #114's actual failure mode - predicts a ~20 s shortfall,
  a ratio near 96.8% again. **If (B) had held, row 6 would be a FAIL and #114 would not be fixed.**

| | row 5 (32 s) | row 6 (606 s) |
|---|---|---|
| video | 32.500 s | 605.667 s |
| audio | 31.451 s | 604.578 s |
| **shortfall** | **1.049 s** | **1.089 s** |
| audio / video | 96.77% | **99.82%** |

**The take grew 18.6x and the shortfall grew 1.04x.** (A) confirmed, (B) refuted. The shortfall is a
constant, not a rate, so there is no proportional audio loss on this build.

### What row 6 does NOT prove

It does not prove a stalled capture is handled. The build has no #113: no `CaptureWatchdog`, no
fragmented output, so a capture that froze mid-take would produce a file whose `moov` was never
written. Row 6 says a ten-minute recording that runs normally completes and is playable. Nothing
here exercises the freeze, and no capture stalled spontaneously during this run.

## The #114 fix is load-bearing, measured in both directions

Rows 5 and 6 show the recorder keeping its audio. They do not, on their own, show *why* - and a pass
that cannot produce the failing state is a pass that cannot tell you whether the fix is doing
anything. So I ran the fix's own mechanism as an experiment.

`ProcessType Interactive` in the launchd plist is #114's fix. I ran the installed binary as one-shot
launchd jobs capturing 20 s of microphone, **identical in every respect except the `ProcessType`
key**:

| Arm | `ProcessType` | WAV bytes | Duration | Of 20 s | |
|---|---|---|---|---|---|
| 1 | **`Interactive`** (what ships) | 1,944,620 | **20.256 s** | **101.3%** | intact |
| 2 | `Background` | 310,316 | 3.232 s | 16.2% | **83.8% GONE** |
| 3 | **key absent entirely** | 771,116 | 8.032 s | 40.2% | **59.8% GONE** |

The repo's own plist comment (`scripts/mac/com.agenteyes.recorder.plist:44-47`) records the original
measurement as 1,920,000 bytes (20.00 s) normal versus 319,392 bytes (3.33 s) background. **Today's
numbers are 1,944,620 and 310,316** - the defect and the fix both reproduce on the shipping build,
within a rounding error of the figures that motivated the issue.

Two things follow that are worth having in writing:

1. **The `Interactive` line is doing real work right now.** Delete it and #114 returns. This is not
   inferred from a code comment; arm 3 is that experiment, and it loses 59.8% of the audio.
2. **The plist comment's claim that "no ProcessType" means throttled is correct**, and I had doubted
   it - Apple documents `Standard` as the default. Arm 3 settles it empirically for this machine.

**One arm of this experiment did NOT reproduce, and I am reporting that rather than dropping it.**
#114's other symptom was the screen capture running at 13 fps instead of 30. I ran the same three
arms as 20 s **screen-only** captures and all three produced **exactly 30.0000 fps** with zero frame
error (`macos/throttle-video-measured.txt`) - including `Background`. So on this machine, at a load
of 3-4 on 10 cores, ProcessType throttling costs audio but not frames. I cannot tell you from this
run whether the frame-rate symptom needs heavier load, or whether it only ever appeared because the
old code captured screen and mic in one shared process, which the current macOS topology no longer
does. **Treat the audio arm as proven and the frame-rate arm as not reproduced here.**

## The rows I could not run, and why - never blank

### Rows 2, 3, 7, 8, 9, 10 - the desktop app: NOT RUNNABLE ON THIS PLATFORM

These are all rows about the desktop application - it launches, it lists recordings, it plays one
back, its detail/settings/dictionary screens open and save, its menu-bar icon shows idle and
recording, and a second launch raises the first window. **There is no macOS desktop app to run them
against.** This is #118 (Phase 2), and the evidence is structural rather than a failed attempt:

| Check | Result |
|---|---|
| Desktop-app projects in the tree | `src/AgentEyes.App` (WPF, Windows) and `src/AgentEyes.LinuxApp`. **There is no macOS app project.** |
| What `scripts/mac/install.sh` publishes | `src/AgentEyes.Core/AgentEyes.Core.csproj` and nothing else |
| Desktop references in `scripts/mac/install.sh` | **0** |
| Desktop references in `scripts/linux/install.sh` | 7 - which is the instrument showing the grep finds them when they exist |

So this is not "I could not get it to start". There is no binary, no install route and no project.

Separately, and independently of #118: **this Mac's screen was locked for the entire run**, so even
once a macOS app exists, nobody has yet checked this machine's window, menu-bar icon or second-launch
behaviour. Those rows need both #118 and an unlocked screen.

### Row 11 - Camera + microphone: NOT RUNNABLE ON THIS MACHINE

`devthrottle-mac-mini` has no camera. The product itself reports it: `GET /devices` answers
`"cameras": []`, on a response that in the same breath lists one monitor and two microphones - so the
empty list is a real absence and not a dead endpoint. A Mac mini has no built-in camera and none is
attached. Needs a USB webcam and the owner's hands.

Note the lead's correction of 2026-11-15: a camera DOES exist on `SORENLAPTOP`, row 11 is runnable on
Windows, and it FAILS there (#149). "Blocked" here means measured-blocked on this machine, not
impossible.

### Row 12 - Always-on keeps the stretches with sound: NOT RUNNABLE

Depends on Phase 5, which has not started. Always-on is Windows-only in the engine today.

### And the one that is not a row: system audio

macOS cannot record what it is PLAYING without a virtual audio device, and none is installed. The
product says so itself rather than failing obscurely - `GET /devices` returns:

```
"systemAudio": "(unavailable: none of the audio inputs on this Mac is a loopback device that carries
the system output. macOS needs a virtual audio device such as BlackHole)"
```

So any row or check involving system or mixed audio is unverifiable on this machine as it stands.
Installing BlackHole needs an admin password and a System Settings approval - the owner's hands.
This is why row 6 below was run as screen + microphone: it is the only audio this Mac can record.
## New findings, handed to the lead and NOT filed

### M1 - The dll sha256 cannot identify a build across seats, and there is an exact identifier sitting unused inside every binary

The lead's note said misidentifying a build is the single thing most likely to invalidate this
report, and circulated a reference table: *main-without-#113 is `0446dd56`; #113's branch head is
`4cb4665c`*. **Neither value reproduced for me, from any tree, at any commit I tried.**

**What I measured.** The same commit built from two different directories gives two different files:

```
f9ef230, published from /Users/<user>/ReposFred/agenteyes-qa-mac   -> 1b9092d0...
f9ef230, published from a worktree at a different path            -> 81ae5fda...
ad21557, published from /Users/<user>/ReposFred/agenteyes-qa-mac   -> dc6ddf45...
ad21557, published from a worktree at a different path            -> 1ff0c669...
```

The build is otherwise perfectly deterministic - three publishes of `f9ef230` from the same path gave
`1b9092d0` every time, matching the installed file byte for byte.

**Two causes, and the second is the useful one:**

1. A .NET deterministic build embeds **absolute source paths**. They are visible in this machine's
   own log: every stack trace carries
   `/Users/<user>/ReposFred/agenteyes-qa-mac/src/AgentEyes.Core/...`. Change the clone, change the bytes.
2. The build also embeds the **git commit SHA**, in the informational version
   `1.11.11+<40 hex>`. So *every* commit produces a different dll - **including a docs-only commit**.
   `f9ef230` is a docs-only change on top of `90c9118`, and its dll still differs.

Cause 2 is why a circulated hash table goes stale the moment anyone commits anything, and cause 1 is
why it was never portable between seats in the first place. `0446dd56` is simply an earlier commit of
this tree, built here, by the seat that was on this Mac before me - not "main without #113" as
labelled.

**The fix is a one-liner, and it is exact, path-independent and needs no reference table:**

```
python3 -c "import re,sys; d=open(sys.argv[1],'rb').read(); \
print(sorted({m.group(1).decode() for m in re.finditer(rb'1\.11\.11\+([0-9a-f]{40})', d)}))" \
  ~/Library/Application\ Support/AgentEyes/bin/agenteyes.dll
```

On the build that answered this report it prints `f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab` - the
commit, read out of the binary, which `git rev-parse f9ef230` confirms exactly. The same binary built
at two different paths reports the same commit while its hashes differ, so this identifier survives
exactly the thing the hash does not.

**Recommendation:** replace rule 3's "quote the binary's sha256" with "quote the commit the binary
reports". If hashes are still wanted as a cross-seat check, set `ContinuousIntegrationBuild=true` in
`src/AgentEyes.Core/AgentEyes.Core.csproj`, which normalises the embedded paths and would make them
comparable between clones. Today they are not.

### M2 - Two of the three #113 markers circulated as discriminators cannot be found by the counting method circulated with them

The instruction was: count UTF-16LE bytes, never `strings`; the discriminating markers are
`frag_keyframe`, `CaptureLiveness` and `FragmentedOutput`.

That is right for the first and **wrong for the other two**, because they are not the same kind of
thing:

| Marker | What it is in source | Stored as | UTF-16LE count on a build that HAS it |
|---|---|---|---|
| `frag_keyframe` | a string literal, `Video/FfmpegArgs.cs:118` | UTF-16 | 1 - found |
| `CaptureLiveness` | a C# **class name**, `Video/CaptureWatchdog.cs:14` | **UTF-8** metadata heap | **0 - NOT found** |
| `FragmentedOutput` | a C# **method name**, `Video/FfmpegArgs.cs:116` | **UTF-8** metadata heap | **0 - NOT found** |

Only string literals are UTF-16 in a .NET assembly; type and member names live in the metadata string
heap as UTF-8. **A seat following the instruction exactly would count 0 for `CaptureLiveness` and
`FragmentedOutput` on the #113 build itself and conclude #113 was absent from a build that contains
it.** That is the warned-about failure one level up: a marker only ever watched failing to appear is
an absence dressed as evidence, and this method guarantees it never appears.

Fix: count UTF-8 for identifiers and UTF-16LE for literals, and require every marker to be shown
present on one build and absent on the other before trusting it. The corrected, both-ways table is in
"Which build answered" above. Better still, use M1's commit read-out and skip markers entirely.

### M3 - The CLI prints a success banner, and names the Windows capture engine, before failing on macOS

Running the documented command `agenteyes video --screen 1 --mic "NAME" --seconds 20` on macOS prints
to stdout:

```
[ok] recording monitor 1 (1920x1080) + mic "Yealink UH36" (denoise+level)
     engine: ffmpeg gdigrab + dshow
     hotkeys: S=marker screenshot  Q=stop
```

and then exits **1**, with this on stderr:

```
[error] on macOS the screen recorder cannot capture the microphone: ffmpeg's AVFoundation demuxer
loses most of the samples when a display and an audio device share one process (issue #114). ...
```

Three separate problems, all on the same path:

1. **`Commands.cs:313` prints `[ok] recording ...` before anything is captured.** The guard that
   refuses is `Video/FfmpegRecorder.cs:129`, which runs afterwards. A command that prints `[ok]` and
   then fails is the "reports success while doing nothing" shape #148 was filed for, in a different
   place. The banner belongs after the recorder has started.
2. **`Commands.cs:314` hardcodes `"engine: ffmpeg gdigrab"`** with no platform branch, so it names the
   Windows capture engine on macOS - where the engine is `avfoundation` - and would name it wrongly on
   Linux too, where it is `x11grab`. This is exactly the kind of line that misleads a QA seat about
   which code path is running; I nearly wrote down that the mac build was using the Windows path.
3. **The CLI's own `--help` advertises the combination** - `agenteyes video --screen N [--mic "NAME"]`
   - that this platform then refuses. Either the help should say it is unavailable on macOS, or the
   command should do what the service does and split the capture.

Severity is low - the *service* path that the app and the Control API use is correct, and it is what
rows 5 and 6 exercised - but (1) and (2) are both "the output says something untrue", which is the
class of defect this mission has spent the most time paying for.

### O1 - Observation, not judged: on macOS the audio/video duration ratio can never reach 100%, so row 5's criterion as written is unreachable here

Row 5 asks for "audio duration matching video duration". On macOS the screen and the microphone are
**two independent ffmpeg captures** - the video command is `-i 0:none`, with no audio input at all,
and the mic is written separately to `mic_native.wav`. They therefore start and stop at slightly
different moments, and the two durations differ by that skew no matter how healthy the recording is:

- the video overran the take by 0.780 s (encoder drain - the Linux seat logged the same shape as its
  own observation O1);
- the mic started 0.471 s after the first video frame. That is **measured by the product** and
  compensated with `adelay=470.688`, so the audio sits in the right place; it does not make the
  track longer.

The gap is a roughly constant ~1 s, so the *ratio* depends on take length and nothing else: 96.8% on a
32 s take, 99.8%+ on a ten-minute one. Linux's 99.93% came from a 107 s take and Windows' 99.46% from
a 136 s one, so **the three platforms' row 5 percentages are not comparable to each other** - they are
mostly a measure of how long each seat recorded for.

I am not calling this a defect: every millisecond is accounted for, the alignment is correct, and the
audio is where it should be against the picture. But the acceptance criterion would be better stated
as a bounded absolute difference (say, audio within 1.5 s of video, with the alignment delay
accounted for) than as a ratio, or the next seat on a short take will read a healthy recording as a
failure - or, worse, a long take as a pass when a real proportional loss is hiding inside it.

## Machine load, and a check for stray processes

Rule 4. Load averages are quoted beside every take in the transcripts. Across the whole run the
1-minute load stayed between **2.4 and 4.9 on 10 cores**, and the steady contributors were the
machine's own background work - `mediaanalysisd` at ~38%, `textunderstandingd`, the session
processes - not AgentEyes.

**I checked for stray `ffmpeg` before starting, and found none.** `ps -Ao pid,ppid,pcpu,etime,command`
filtered for `ffmpeg` returned only my own grep. Nothing was killed, because there was nothing to
kill. No capture stalled spontaneously at any point during this run.

## How the installs are sequenced, since this report quotes more than one

Rule 3 says install once and keep that build answering. I deviated deliberately and say so here:

| When | Build installed | pid | Why |
|---|---|---|---|
| 18:43 | **`f9ef230`, `1b9092d0`** | **39753** | the install under test (row 1). **Every row measurement and the whole #114 experiment was taken on this pid.** |
| 14:04 local | `ad21557`, `dc6ddf45` | 50425 | the #148 probe - a *different* build carrying the *same* version `1.11.11`, run only after all rows were measured |
| 14:05 local | `f9ef230`, `1b9092d0` | 50642 | restored, so the machine is left on the commit this report is about |

Both probe installs happened **after** every row number in this report was banked, so no measurement
spans two builds. The probe is what makes the #148 claim empirical rather than a reading of the
script, and the restore is verified: the installed dll hashes `1b9092d0...` again and reports commit
`f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab`.

## Machine left as found

- Installed build: **`main` at `f9ef230`**, the commit this report covers. Service healthy, idle,
  pid 50642, `/health` answering on 127.0.0.1:7882.
- The launchd plist is the repo's, rewritten by `install.sh`, with `ProcessType Interactive` present.
- All three one-shot experiment labels (`com.agenteyes.qa114.*`) were booted out; `launchctl print
  gui/$UID` shows none remaining.
- The experiment captures were written to a scratch directory, not the library. One early arm did
  write `recording.mp4`, `manifest.json`, `recording.mp4.ffmpeg.log` and an empty `shots/` into the
  **root** of `~/Movies/AgentEyes` - because `agenteyes <cmd> --out DIR` writes into `DIR` itself
  rather than creating a per-recording subdirectory under it. I confirmed all four were mine by
  timestamp (14:01-14:03, minutes old, nothing older touched) and removed them. The three real
  recordings from rows 4, 5 and 6 are left in place.
- Nothing else in `~/Movies/AgentEyes` was deleted or modified. The screen was never unlocked and no
  input was synthesised.
- The temporary `AGENTEYES_QA_PROBE` launchd variable used to prove the `launchctl getenv` instrument
  was unset again.

## Evidence

Under `docs/cencon/proof/issue-142/macos/`:

| File | What it holds |
|---|---|
| `install-transcript.txt` | row 1, both arms - the precondition failure and the successful install |
| `post-install-identity.txt` | new pid, installed hashes, `/health` |
| `pid-to-file.txt` | `lsof` tying pid 39753 to the dll that was hashed |
| `which-build-is-answering.txt` | the marker table across installed / alt-path / #113 builds |
| `commit-in-binary.txt` | the commit SHA read out of four binaries, against `git rev-parse` |
| `processtype.txt` | `ProcessType Interactive` in the repo template and the installed plist |
| `devices.json` | monitors, mics, `"cameras": []`, the system-audio message |
| `row4-take.txt`, `row4-dir.txt`, `row4-probe.txt` | row 4 drive, directory, measurements |
| `row5-take.txt`, `row5-dir.txt`, `row5-probe.txt` | row 5 drive, directory, measurements |
| `row6-prediction.txt` | the prediction, timestamped **before** row 6 was probed |
| `row6-take.txt`, `row6-probe.txt`, `row6-accounting.txt` | row 6 drive, atom map, the prediction test |
| `throttle-audio-measured.txt` | the #114 instrument - the three ProcessType arms, audio |
| `throttle-video-measured.txt` | the arm that did NOT reproduce - three arms, all 30.0000 fps |
| `installer-147-shape.txt` | the #147 probe and the proof that `launchctl getenv` works |
| `probe-148.txt`, `restore-main.txt` | the #148 probe and the restore |
| `rows-not-runnable.txt` | the structural evidence for rows 2, 3, 7-10 |
| `frames/` | every frame I extracted and read: row 4 t=10 s, row 6 t=5 s, t=300 s, t=603 s |

---

# Addendum, 2026-11-15 - the 1.5 s bound, and two corrections to this report

The lead amended row 5's criterion to **audio within 1.5 s absolute of video** (adopting O1) and
asked one question: is 1.5 s right for macOS at the longest take I ran, or does the skew grow with
length in a way that would eventually breach it?

To answer it I ran a **30-minute take** on the same build - `main` `f9ef230`, dll `1b9092d0`, service
**pid 50642** (the pid after the installer probes; same commit and same binary as rows 4, 5 and 6).
Load 2.7 - 3.7 on 10 cores. `~/Movies/AgentEyes/2026-11-15_142036_video`.

| Measurement | Value |
|---|---|
| Video | 1802.500 s, **54,075 frames**, **30.0000 fps**, frame error **0** |
| Audio | AAC 48 kHz mono, 1801.653 s |
| `moov` | **PRESENT at offset 990,825,190**, size 1,719,210 |
| Full decode | **exit 0** |
| Mux | `done`, **attempt 1**, 40.62 s |
| Audio accounting | mic 1801.184 + adelay 0.462667 = 1801.6467 vs measured 1801.6527 - **6.0 ms residual** |

## Correction 1 - row 6's mux figure in this report was wrong

I wrote that row 6's mux took "about 6 s". **It took 13.68 s.** I had inferred the figure from my
driver's post-stop settle time, which is not the mux at all: **the mux is deferred.** `/status`
returns `idle` and `POST /record/stop` returns while the mux is still running, so settle time
measures the stop, not the muxing. The real figures, from the product's own start and done log lines:

| Take | Mux duration | As a share of the take |
|---|---|---|
| row 5, 31.7 s | 0.50 s | 1.6% |
| row 6, 605.7 s | **13.68 s** | 2.3% |
| long take, 1801.9 s | **40.62 s** | 2.3% |

The row 6 table above is corrected. No other figure in this report depended on it, and row 6's own
measurements were taken after its mux had finished, so they stand.

**This is worth a line of its own for the next seat: `idle` does not mean the recording is finished
on disk.** I probed the 30-minute take the moment the driver reported idle and found no
`recording.mp4` at all - only `recording.mp4.muxing.tmp`. The authority is
`manifest.json` -> `PostProcessing.mux.State`, not the service state.

**And I checked it was progress and not a stall before waiting on it**, because a stall here is a
finding and killing it would have destroyed the evidence: `recording.mp4.muxing.tmp` was 945 MB and
growing, the ffmpeg child (pid 63761) was in state **R** at 102.6% CPU, pass one had already written
`audio_processed.wav`, and the service itself sat at 0% waiting on its child. Nothing was killed and
it completed on its own, on attempt 1.

## Correction 2 - my own drift hypothesis was wrong, and the third point refutes it

From rows 5 and 6 alone I had fitted a ~68 ppm growth term and written down, before measuring, that
it predicted a **1.170 s** shortfall at 1800 s against **1.089 s** for a constant. I flagged at the
time that two points cannot separate those.

**Measured: 0.847 s.** Lower than both. The prediction is refuted.

| Take | video | audio | shortfall | headroom under 1.5 s |
|---|---|---|---|---|
| row 5 | 32.500 s | 31.451 s | 1.0493 s | 0.451 s |
| row 6 | 605.667 s | 604.578 s | 1.0885 s | 0.411 s |
| **long** | **1802.500 s** | **1801.653 s** | **0.8473 s** | **0.653 s** |

**The shortfall does not grow with take length.** Across a 56x range the longest take has the
*largest* headroom. The two-point slope was scatter in the video tail, which varies by about
+/-120 ms run to run.

## But there IS a real proportional term, on the mic side

Separately from the video, the product measures its own microphone capture on every recording and
logs it: `[AudioCapture] Stop: wrote ... = 1801.18s of audio over a 1801.53s capture`. That isolates
the mic - no video, no mux, no `adelay`. Across **20 captures on this Mac today, 20.5 s to 1801.5 s,
an 88x range**:

```
deficit(T) = 0.2593 s + 49.6 ppm * T
slope standard error 17.5 ppm, t = 2.83 on 18 df (critical 2.101)
95% interval on the slope: 12.8 to 86.4 ppm - EXCLUDES zero
predicted deficit at 1801.5 s: 0.349 s      measured: 0.350 s
```

With only 604 s of lever arm this slope was **not** significant (52 +/- 39 ppm, t = 1.32). The
30-minute capture is what resolved it. **~50 ppm is the ordinary magnitude of a crystal mismatch
between an audio device clock and the system timebase**, so there is a mechanism behind it, not only
a fit.

Both results are true at once: a 50 ppm drift accumulates only **89 ms over 1800 s**, which is
smaller than the video tail's run-to-run scatter, so it is real on the mic side and still invisible
in the video-minus-audio figure at half-hour scale.

## The answer to the question

**1.5 s is right for macOS, for takes up to at least 30 minutes.** Verified directly at 1802 s with
0.65 s to spare; the bound was never approached at any length I ran.

**It should not be extrapolated to always-on.** At ~50 ppm, drift alone adds **0.18 s per hour**, so
a base of 1.0 - 1.1 s reaches 1.5 s at roughly **2.2 hours** on the point estimate and as early as
**1.3 hours** at the 95% upper bound. Phase 5 records for far longer than that.

Recommendation: keep **1.5 s absolute** as the row 5 criterion for takes up to an hour, which covers
every Wave A row on every platform. For Phase 5 always-on, either express the bound as
**1.0 s + 100 ppm x T** or re-measure at hour scale before applying a flat number.

## One more thing the throttle arms produced

The product diagnosed my own #114 experiment without being asked. The three `ProcessType` arms are
distinguishable in its log by severity alone:

```
[AudioCapture] Stop: wrote 1944576 bytes of PCM = 20.26s of audio over a 20.51s capture
WARN [AudioCapture] Stop: ... = 3.23s of audio over a 20.42s capture - 17.19s MISSING. The
     AVFoundation device dropped samples this end did not take in time ...
WARN [AudioCapture] Stop: ... = 8.03s of audio over a 20.35s capture - 12.32s MISSING. ...
```

The `Interactive` arm logged at INFO with no complaint; both throttled arms logged at **WARN**,
naming the exact AVFoundation sample-drop mechanism #114 was filed about. The instrument is not only
my arithmetic - the product itself separates the healthy arm from the throttled ones.

Additional evidence: `macos/skew3-prediction.txt` (written before the measurement),
`macos/skew3-take.txt`, `macos/skew3-probe.txt`, `macos/skew3-accounting.txt`,
`macos/skew-regression-final.txt`, `macos/mux-durations.txt`, `macos/mux-inflight-1.txt`,
`macos/audiocapture-stop-lines.txt`, `macos/throttle-product-warning.txt`.

---

# Wave B, 2026-11-16 - the macOS application rows

**Headline: the first run of the six application rows on a Mac. The app installs, opens, lists the
real library with a correct count, plays a recording, and opens its detail, settings and dictionary
screens; its menu-bar icon was photographed idle and recording. Saving is BLOCKED (it needs a click).
Row 10 FAILS on the route a Mac user actually takes: a second launch through Launch Services (Finder,
Dock, Spotlight, Launchpad) does not bring back a hidden window - the app has no reopen handler. The
app's own single-instance handoff, used by a second launch of the executable, works.**

| | |
|---|---|
| Machine | `devthrottle-mac-mini`, macOS 26.5.2 arm64 |
| Commit tested | **`main` at `277a9bc`** (`277a9bcf71c7f82547353177299178efc1020327`), the #118 merge; `git fetch origin` at 14:18, `origin/main` = that commit |
| App, as installed | `~/Applications/AgentEyes.app`, `Identifier=com.agenteyes.app`, `Signature=adhoc`, `CDHash=f137a81e35efa32b27ab84afdc949aa9eac33354`, `codesign --verify --deep --strict` exit 0, 198 MB. `AgentEyesDesktop.dll` reports **`1.11.11+277a9bcf71c7f82547353177299178efc1020327`** |
| App before my install | `1.11.11+9f668b54555cfbf35569c620b58e287eee37e220`, cdhash `9fdc7938...` (#118's QA build); not running |
| Service (the owner's, untouched) | pid **50642**, `agenteyes.dll` reports `1.11.11+f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab`. **It is not built from 277a9bc** - the app rows exercise the app against the installed service, as a user would after `install-app.sh` |
| Screen | **LOCKED** throughout (`CGSSessionScreenIsLocked=Yes` at 14:18:43, 14:19:47, 14:25:16 and 14:30) |
| Load | 2.32 at the start, 2.12 at the end, 10 cores |
| Time | 14:18 - 14:30 EST |

## The verdicts

| # | Check | Verdict | One-line reason |
|---|---|---|---|
| 2 | Launches; the main window opens | **PASS** | `open` -> pid 57343, `14:19:49.185 [MainWindow] Opened`; window 796 photographed, full Record page |
| 3 | The library lists existing recordings | **PASS** | photograph reads **"81 recordings"**; 81 directories, all 81 with `manifest.json`; positive control: a new recording moves it to **82** |
| 7 | Plays a recording back in the app | **PASS, opened by launch verb not by click** | `--preview <id>` -> `[PlayerWindow] Load: playing`; two photographs read `0:03 / 0:21` then `0:09 / 0:21` |
| 8 | Detail view, settings and dictionary open and save | **OPEN: PASS. SAVE: BLOCKED** | all three opened and photographed; every save needs a click or typing |
| 9 | Menu-bar icon shows idle and recording | **PASS for the icon itself; the in-place menu-bar photograph BLOCKED** | the status-item window photographed by id: crescent idle, red dot recording, back to the crescent after the stop, in step with the app's `Render` lines. One instance did not log its return to idle (O3, not reproduced) |
| 10 | Single instance: a second launch raises the first window | **FAIL on the Launch Services route; PASS on the executable route** | `open` while hidden: no process, no log line, **no window**. Executable: both processes log the handoff and window 813 appears |

## How anything was seen with the screen locked

Reused, not rewritten: `docs/cencon/proof/issue-116/mac-pass3-capture-tools/` (`oneshot.py`,
`StartupHook.cs`, `compose.swift`) and `docs/cencon/proof/issue-116/mac-window-probe.swift`, built
unchanged into a scratch directory. `macos-app/tools/cap.sh` is my thin driver around `oneshot.py`.

- Window ids come from the Window Server (`mac-window-probe`), not from the app's own log.
- Each photograph is taken by a **one-shot launchd job** running the **installed service binary**
  `agenteyes` with `DOTNET_STARTUP_HOOKS=QaCaptureHook.dll`, which runs
  `screencapture -x -o -l <window id>` and exits before the recorder's `Main`.
- Every hook report shows `CGPreflightScreenCaptureAccess() = True`, `AXIsProcessTrusted() = False`,
  `CGPreflightPostEventAccess() = False`. **That last pair is why no row could click.**
- Nine jobs (`com.agenteyes.qa142.row2`, `.row3`, `.row9rec`, `.row7a`, `.row7b`, `.row8`, `.row10`, `.rt1`, `.rt2`),
  each bootstrapped, polled with a 40 s hard timeout, finished in about 0.5 s with exit 0, and was
  booted out (`post-bootout print exit=113`). `launchctl list | grep -c qa142` = 0 after each.
- The service's pid, apphost sha256 and `/health` are logged before and after every job: unchanged.
- **No `screencapture` was run from my shell.** The app holds no grant and was given no hook.

## The service was not touched - measured

| When | pid | `agenteyes` sha256 | `agenteyes.dll` sha256 | `/health` |
|---|---|---|---|---|
| 14:18:43 baseline | 50642 | `348b93b8...d2607022` | `1b9092d0...4111ddb0` | `ok:true` |
| 14:19:01 before `install-app.sh` | 50642 | `348b93b81d867fc3` | `1b9092d077892723` | `ok:true` |
| 14:19:12 after `install-app.sh` | 50642 | `348b93b81d867fc3` | `1b9092d077892723` | `ok:true` |
| 14:25:16 after row 10 | 50642, launchd `state = running` | `348b93b8...d2607022` | `1b9092d0...4111ddb0` | `ok:true`, idle |
| 14:30 at the end | 50642, launchd `state = running` | `348b93b8...d2607022` | `1b9092d0...4111ddb0` | `ok:true`, idle |

`scripts/mac/install-app.sh --no-start` built, signed, verified, installed and registered the bundle
(`macos-app/install-app-transcript.txt`). **I did not capture its exit code** (my pipe swallowed it);
the script runs under `set -euo pipefail` and printed its closing paragraph, which it reaches only
after every step, and the installed bundle is verified separately above. `scripts/mac/install.sh` was
**not** run.

## Row 2 - Launches; the main window opens: PASS

**Expected:** the installed app launches by the platform's route and its main window opens, shown by
the app's log line and a photograph.

**Actual** (`macos-app/row2-launch.txt`): with no app running, `open ~/Applications/AgentEyes.app` at
14:19:47 started pid 57343 (`~/Applications/AgentEyes.app/Contents/MacOS/AgentEyesDesktop`), which logged:

```
14:19:48.767 [pid 57343] [INFO] [Program] Main: AgentEyes desktop app starting on mac (tray=False, pid=57343)
14:19:49.185 [pid 57343] [INFO] [MainWindow] Opened
14:19:49.284 [pid 57343] [INFO] [AppState] PollAsync: service answering, state=idle
14:19:49.308 [pid 57343] [INFO] [MainWindow] RequestScan: 81 recording(s), 68 pending, 46 ms
```

The Window Server lists `pid=57343 owner="AgentEyes" window=796 layer=0 onscreen=true bounds=1280x748`.
**Photograph:** `macos-app/row2-window-record.png`. I read it: the Record page - REC button, Screen
"Monitor 1 - 1920 x 1080 (primary)", Microphone "No microphone", the VIDEO / MIC / SYSTEM cards, the
left rail. Not blank.

## Row 3 - The library lists existing recordings: PASS

**Expected:** the Library view lists the owner's recordings with a count, and the count is right.

**Actual:** a second launch asked for the Library (`--view library`, see row 10); the window was then
photographed: `macos-app/row3-window-library.png`. I read it: header **"81 recordings"**, a grid of
cards, the first row dated Nov 16 1:08 PM, 12:34 PM, 9:06 AM, 9:05 AM.

**Cross-check against the directory** (`macos-app/row3-crosscheck.txt`, 14:20:52). The scanner counts
every top-level folder of `~/Movies/AgentEyes` that holds a `manifest.json`
(`Model/LibraryEntry.cs`, `LibraryScanner.Scan`):

| Measure | Value |
|---|---|
| top-level directories | 81 |
| ... with `manifest.json` | **81** |
| ... without | none |
| by mode | 73 video, 8 audio |
| newest four folders | `2026-11-16_130845`, `_123404`, `_090652`, `_090503` - the photographed first row, in order |
| the app's own log | `RequestScan: 81 recording(s)` |

**Positive control - the capture sees the count change.** Row 9 starts a recording. The app logged
`RequestScan: 82 recording(s)` at 14:21:05.573, and the photograph taken at 14:21:09
(`macos-app/row9-window-recording.png`) reads **"82 recordings"** with a new first card,
"Recording..." / "Processing...", dated Nov 16 2:21 PM. So the "81" is read from a live window, not a
stale image.

## Row 7 - Plays a recording back in the app: PASS, opened by launch verb, not by click

**Why this was reachable.** Starting playback by hand is a click on a card. But the app accepts
launch verbs that a second launch hands to the first (`Program.cs:56-68`), and
`--preview <id>` runs the same `OpenPreview` the card's Preview button runs
(`MainWindow.axaml.cs`, `Preview_Click` -> `OpenPreview`). The player starts on open:
`PlayerWindow.Load` calls `_player.Play(_media)` (`PlayerWindow.axaml.cs:79`). No click is needed.

**What I played.** `2026-11-15_134545_video` - Wave A's row-4 take, 21.300 s, **one video stream
and no audio**, chosen so that playback on an unattended machine is silent.

**Actual** (`macos-app/row7-preview.txt`):

```
14:21:44.633 [pid 59004] [SingleInstance] TryAcquire: another instance is running - sent it 'preview:2026-11-15_134545_video'
14:21:44.636 [pid 57343] [MainWindow] OpenPreview: 2026-11-15_134545_video
14:21:44.665 [pid 57343] [PlayerWindow] ctor: ~/Movies/AgentEyes/2026-11-15_134545_video/recording.mp4 (Video)
14:21:45.534 [pid 57343] [PlayerWindow] Load: playing ~/Movies/AgentEyes/2026-11-15_134545_video/recording.mp4
```

The player is window 802 (1000x668), owned by pid 57343. Photographed twice:

| Photograph | Taken | Time label | Seek thumb | Button |
|---|---|---|---|---|
| `macos-app/row7-player-a.png` | 14:21:50.211 | **`0:03 / 0:21`** | about 19% | pause glyph (playing) |
| `macos-app/row7-player-b.png` | 14:21:54.890 | **`0:09 / 0:21`** | about 43% | pause glyph |

I read both: the recorded desktop (the lake wallpaper) rendered by libvlc in the player, title
"Monitor 1", and the transport bar. **The two time labels are the positive control:** the position
advanced, so this is playback and not a still frame.

**Limit, stated:** the label moves by at least 5.0 s across 4.68 s of wall time. The label is floored
to whole seconds and refreshed every 250 ms (`PlayerWindow.axaml.cs:25`, `:104`), which accounts for
most of that; the remaining fraction of a second I attribute to libvlc's time reporting and have not
measured. I claim "playback advances", not a playback rate.

## Row 8 - Detail view, settings and dictionary open and save: OPEN PASS, SAVE BLOCKED

**Open - PASS.** Each screen was opened by a second launch with its verb (`macos-app/row8-open.txt`):

| Screen | Launch | The first instance logged | Window | Photograph |
|---|---|---|---|---|
| Details | `--details 2026-11-15_134545_video` | `OpenDetails`, `[DetailWindow] ctor`, `LoadAsync: ... transcript=None chars=0` | 807, 680x668 | `macos-app/row8-details.png`: title "Monitor 1", Nov 15 2026 1:45 PM, the sign-in banner, "Not transcribed.", buttons Play / Build walkthrough / Copy transcript (disabled) / Open folder / Delete |
| Settings | `--settings general` | `OpenSettings: General`, `[SettingsWindow] ctor: tab=General` | 808, 560x508 | `macos-app/row8-settings.png`: General and Account tabs, "Run at login" unchecked, "Recorder service: running (http://127.0.0.1:7882)", the two folder buttons |
| Dictionary | `--view dictionary` | `Rail_Changed: Dictionary`, `LoadDictionaryAsync`, `dictionary: seeded starter file at ~/Library/Application Support/AgentEyes/dictionary.json` | 796 (the main window) | `macos-app/row8-dictionary.png`: six starter terms, each with "+ misheard", Edit, Delete |

**Redactions:** in `row8-settings.png` the two folder paths (they carry the account name) and in
`row8-dictionary.png` one starter term (the owner's name) are painted over with
`macos-app/tools/redact.swift`. Nothing else in either image was altered. The term itself is in
`src/AgentEyes.Core/Transcription/TranscriptionDictionary.cs`, so nothing evidential is hidden.

**My own error, kept in the record:** my first two attempts passed `--details <id>` and
`--settings general` as **one** argument each (zsh does not split `$args`), so the app received
`show` and opened nothing new. `row8-open.txt` keeps those two runs and then the correct ones.

**Save - BLOCKED.** Every save on these screens is a user action: Settings' only setting is the
"Run at login" checkbox; the dictionary saves on Add / Edit / Delete / "+ misheard"; the detail view
renames or deletes by button. All need a click or typing, and the screen was locked
(`CGPreflightPostEventAccess() = False`, `AXIsProcessTrusted() = False` in every hook report). I did
not drive a save from inside the app with a hook: that would test a method call, not the screen.
**Nothing was saved, and this report does not say saving works.**

**One finding from reading the save path (F1 below):** on macOS, the "Run at login" checkbox would
save to a file macOS never reads.

## Row 9 - Menu-bar icon shows idle and recording: PASS for the icon, in-place photograph BLOCKED

**Which window is the icon.** On macOS 26 every status item is a layer-25 window **owned by Control
Center** (pid 431), never by the app, so a per-pid probe cannot find it. I listed all layer-25
windows before and after each launch:

- First launch: 10 before and 10 after, with exactly one new window, **797**, 38x30 at 1432,0
  (one stale off-screen item, 794, was removed by the system in the same interval).
- The tray-only launch in row 10: exactly one new window, **812**, at the same place.

**Photographs, by window id:**

| State | Photograph | sha1 of the raw capture | What I see |
|---|---|---|---|
| idle (14:20:11, pid 57343) | `macos-app/row9-item-idle.png`, enlarged `row9-item-idle-x6.png` | `3d08460349df6e0e...` | white crescent moon with a dot |
| recording (14:21:09, 4.7 s into a recording) | `macos-app/row9-item-recording.png`, enlarged `row9-item-recording-x6.png` | `562448ea56b2a804...` | red dot with a dark ring |
| idle again, a different instance (14:24:51, pid 60625) | `macos-app/row10-item.png` | `3d08460349df6e0e...` | identical bytes to the first idle capture |

**It follows the recorder.** The recording was started through the Control API, not through the app:
`POST /record/start {"mode":"video","screen":1,"source":"none"}` at 14:21:04, then `/status`
`recording 4.75` before the photograph and `recording 5.45` after it, then `POST /record/stop {}`
(5.47 s). The app logged `14:21:05.489 [TrayController] Render: idle -> recording`. These are the
same two drawings #116's seat recorded (`3d084603...` and `562448ea...`), and what
`TrayController.cs` draws (`DrawMoon`, `DrawDot`).

**BLOCKED, and why:**
- **The icon in place on the menu bar.** A rectangle capture fails while the screen is locked, so I
  have no photograph of the bar with the icon among its neighbours. The `-x6` images are the item
  window composited onto a synthetic dark fill by `compose.swift`; that fill is the tool's colour, not
  the menu bar's. Legibility on the real bar is not shown.
- **The icon's menu.** Opening it needs a click.

**Return to idle in the same instance - measured separately** (`macos-app/row9-return-to-idle.txt`).
While writing this report I found that the first instance (pid 57343) **never logged
`recording -> idle`** in the three minutes it ran after the stop (see O3). So I ran it again on a fresh
launch, pid 62329, item window 816, main window 815:

| Moment | App log | Icon (sha1) | Main window |
|---|---|---|---|
| 14:28:57 `POST /record/start` | `14:28:57.952 Render: idle -> recording` | `562448ea...` (`row9-rt-item-recording.png`) | `row9-rt-window-recording.png`: **STOP**, `REC 00:03`, controls greyed |
| 14:29:01 `POST /record/stop` (4.47 s) | `14:29:02.527 Render: recording -> idle` | `3d084603...` (`row9-rt-item-after-stop.png`, `-x6`) | `row9-rt-window-after-stop.png`: REC again - **byte-identical** (`cmp`) to row 2's idle photograph |

A second recording on the same instance with the Library view showing (the first instance's
condition) also returned: `14:29:32.437 idle -> recording`, `14:29:37.455 recording -> idle`.

## Row 10 - Single instance, a second launch raises the first window: FAIL on the Launch Services route

There are two different "second launches" on a Mac, and they behave differently.

### Arm B - a second launch of the executable: PASS

The app's own single-instance handoff (`SingleInstance.cs`, a Unix socket in `$TMPDIR`).

**Window already showing** (`macos-app/row10-second-launch.txt`), `AgentEyesDesktop --view library`
at 14:20:33, exit 0 after 0.05 s:

```
14:20:33.767 [pid 57818] [Program] Main: AgentEyes desktop app starting on mac (tray=False, pid=57818)
14:20:33.771 [pid 57818] [SingleInstance] TryAcquire: another instance is running - sent it 'view:library'
14:20:33.772 [pid 57343] [SingleInstance] Accept: 'view:library'
14:20:33.773 [pid 57343] [App] ShowWindow
14:20:33.777 [pid 57343] [MainWindow] Rail_Changed: Library
```

Afterwards only pid 57343 runs. The same handoff carried `preview:`, `details:`, `settings:` and
`view:dictionary` in rows 7 and 8, each logged by both processes.

**Window hidden - the case that tests "raises"** (`macos-app/row10-tray-start.txt`,
`macos-app/row10-armB-exe.txt`). I stopped pid 57343 with SIGTERM, then started the app tray-only with
`open ~/Applications/AgentEyes.app --args --tray` (the login-autostart form): pid 60625,
`tray=True`, its status item 812, and **no window** (`mac-window-probe 60625` -> `[EMPTY]`, exit 2; the
same probe had found window 796 for the previous instance, so the empty result is not a broken
instrument). Then the executable, no arguments, at 14:24:43:

```
14:24:43.998 [pid 60866] [SingleInstance] TryAcquire: another instance is running - sent it 'show'
14:24:43.999 [pid 60625] [SingleInstance] Accept: 'show'
14:24:44.001 [pid 60625] [App] ShowWindow
14:24:44.235 [pid 60625] [MainWindow] Opened
```

Window **813** now exists for pid 60625; `macos-app/row10-window-raised.png` shows the Record page.
Window absent before, present after: the second launch brought the first instance's window up.

### Arm A - a second launch through Launch Services: FAIL

On a Mac, a person launches an app from Finder, the Dock, Spotlight or Launchpad, and all of them go
through Launch Services. Launch Services does **not** start a second process for a running app; it
activates the running one and sends it a "reopen" event. The app's own handoff is never reached.

- **Window showing** (`macos-app/row10-open-again.txt`): `open ~/Applications/AgentEyes.app` at
  14:23:39, exit 0. Still only pid 57343; **0** app log lines. Nothing to raise was observable
  while locked.
- **Window hidden** (`macos-app/row10-armA-open.txt`): with the tray-only pid 60625 running,
  `open ~/Applications/AgentEyes.app` at 14:24:22, exit 0. Still only pid 60625, **0** app log lines,
  and **no window** (`mac-window-probe 60625` -> `[EMPTY]`). Twenty-one seconds later arm B, on the
  same instance and the same probe, produced window 813, so the probe was working.

**Why, from the code:** the app uses Avalonia 11.3.22, which offers an application-reopen event
(`IActivatableLifetime`, `ActivationKind`, and `IAvnApplicationEvents.OnReopen` in
`Avalonia.Native.dll` - all present in the installed bundle). The app subscribes to none of them:
`grep -E "IActivatableLifetime|ActivationKind|TryGetFeature"` over `src/AgentEyes.DesktopApp`
finds nothing. So a reopen has nothing to show the window, and the locked screen is not what stopped
it.

**What this means for a user (F2 below):** close the window (it hides to the menu bar,
`App.axaml.cs`), then open AgentEyes again from Launchpad, Spotlight, Finder or the Dock - and no
window comes back. The only way back is the menu-bar icon's "Show AgentEyes".

**Limits:** I produced the hidden state by starting with `--tray`, not by closing the window (a
click). A closed window is `Hide()`n rather than never created; I expect the same result but did not
measure it. Whether a reopen raises a window that is showing but behind others needs an unlocked screen.

## New findings, handed to the lead and NOT filed

### F2 - Measured: a second launch through Launch Services does not bring back a hidden window

Row 10 arm A above. Fix direction, for whoever owns it: handle Avalonia's reopen activation
(`IActivatableLifetime.Activated` with `ActivationKind.Reopen`) by running the same `ShowWindow` the
socket handoff runs. A test cannot easily drive Launch Services; the measurement above (tray-only
instance, `open`, probe for a window) is a repeatable runtime check.

### F1 - By code reading: "Run at login" on macOS writes an XDG autostart file that macOS never reads

`Model/AppSettings.cs:61-95`: `Autostart.EntryPath` is `~/.config/autostart/agenteyes.desktop` on
every platform, and `Enable` writes a `[Desktop Entry]` file with `Exec=... --tray` and
`X-GNOME-Autostart-enabled=true`. macOS has no XDG autostart. And `IsEnabled` is
`File.Exists(EntryPath)`, so after a user ticks the box, the box **reads back as ticked** - the setting
looks saved and does nothing. **Not run:** it needs a click and a fresh login. The macOS form would be a
LaunchAgent or `SMAppService`.

### O2 - Observation, not judged: the Record page says "System sound: Recorded" on a Mac that cannot record it

Every Record-page photograph shows "System sound - Recorded" and a green SYSTEM card ("what you hear,
70%"). The service on the same machine answers `GET /devices` with `systemAudio: "(unavailable: none of
the audio inputs on this Mac is a loopback device ...)"`. The app's default is
`SystemSound = true` (`AppSettings.cs:19`) and its source is `system` when no microphone is set
(`AppSettings.SourceFor`). What REC does from this state needs a click; not measured.

### O3 - Observation, NOT reproduced: one app instance never logged its return to idle

The first instance, pid 57343, logged `14:21:05.489 [TrayController] Render: idle -> recording` and
then **no further `Render` line** until I stopped it at 14:24:12, although the recording stopped at
14:21:11 and `/status` answered `idle` from 14:21:13. `TrayController.Render` logs every change of
state (`TrayController.cs:80-81`), and the app kept polling: the service logged **60, 60 and 59
`GET /status` a minute** for 14:22, 14:23 and 14:24, with no other app running. pid 57343 logged no
WARN or ERROR line.

- **What it may mean:** the icon (and possibly the Record page) stayed in the recording state after the
  recording ended.
- **What I did not see:** I did not photograph that instance's icon or Record page after the stop, so
  this is a missing log line, not an observed wrong icon.
- **Two attempts to reproduce it, both returned to idle within 1.4 s** (row 9 above), including one with
  the Library view showing.
- **A possible mechanism, from reading, unproven:** `AppState.PollAsync` runs fire-and-forget
  (`_ = PollAsync()`, `AppState.cs:58-60`) and raises `Changed` outside its try/catch
  (`AppState.cs:84`). `TrayController` subscribes before `MainWindow`, so if `TrayController.Render`
  threw before its state comparison, the exception would vanish silently, and `MainWindow.Render`
  would not run either. I have no evidence that it threw.

## What this run did NOT cover

- **Any click.** The REC button, the rail buttons, a card's Preview / Details buttons and its context
  menu, the menu-bar icon and its menu, any menu item. Every screen here was reached through a launch
  verb or the Control API. The click handlers call the same methods (`OpenPreview`, `OpenDetails`,
  `OpenSettings`), but the click paths themselves were not exercised.
- **Any save** (row 8).
- **Audio playback.** The played recording has no audio track, deliberately.
- **The icon in place on the menu bar, and its menu** (row 9).
- **Closing the window to the menu bar, then launching again** (row 10 used a tray-only start).
- **An unlocked screen**, Finder double-click, Dock behaviour, focus and z-order.
- **The full `scripts/mac/install.sh`** and **a clean Mac** (row 1, owed per comment 6925350295).
- **A service built from 277a9bc.** The app talked to the owner's service at f9ef230.
- **No product code changed, so no build or test gate was run.** `install-app.sh` built the bundle.

## Hazards - what I touched and what I did not

- **Not touched:** `com.agenteyes.recorder` (never booted out or re-registered); the installed service
  binary (sha256 unchanged); any TCC or privacy setting; the other clones on this machine.
- **Replaced:** `~/Applications/AgentEyes.app`, from 9f668b5 to 277a9bc, by `install-app.sh` as asked.
- **Started and stopped:** app pids 57343, 60625 and 62329 (SIGTERM; each was gone at the first check,
  within a second); the transient second-launch processes (57818, 59004, 59657, 59664, 59726, 59734,
  59793, 60866, and one more for row 9's Library view), each exited 0 on its own. No app was running
  when I arrived and none is running now.
- **Added to the owner's library, as evidence (do not delete):** three short screen-only recordings
  from row 9 - `2026-11-16_142104_video` (h264 1920x1080, 169 frames, 5.633 s), `2026-11-16_142857_video`
  (4.47 s) and `2026-11-16_142931_video` (4.71 s). The library went from 81 to 84; nothing was
  deleted; `2026-11-16_123404_video` and `2026-11-16_130845_video` are still present. Each recording's
  package stage fails on extracting audio from a file with no audio track - the service's known #126,
  already recorded in Wave A, not new.
- **Created by the app, left in place:** `~/Library/Application Support/AgentEyes/dictionary.json`,
  seeded with the starter terms the first time the Dictionary view opened. `linux-app.json` was
  rewritten by `AppSettings.Save` with the values it had loaded.
- **ffmpeg:** none was running before; the row 9 recording's ffmpeg was the service's; none afterwards.
- **#161:** 154 app log lines from my processes (14:19:40 - 14:30:30) went into the owner's real
  `AgentEyes-20261116.log`. These rows are meant to use the real library, so no scratch service was
  started.

## Evidence (Wave B)

Under `docs/cencon/proof/issue-142/macos-app/`:

| File | What it holds |
|---|---|
| `baseline.txt`, `identity-before.txt` | the machine and service at 14:18, the embedded commit of the app and service |
| `install-app-transcript.txt` | `install-app.sh --no-start`, the service before and after, the installed bundle's identity |
| `row2-launch.txt`, `row2-window-record.png` | row 2 |
| `row3-crosscheck.txt`, `row3-window-library.png` | row 3, and the directory count |
| `row7-preview.txt`, `row7-player-a.png`, `row7-player-b.png` | row 7 |
| `row8-open.txt`, `row8-details.png`, `row8-settings.png`, `row8-dictionary.png` | row 8 (two images redacted) |
| `row9-recording.txt`, `row9-item-idle*.png`, `row9-item-recording*.png`, `row9-window-recording.png` | row 9, and row 3's positive control |
| `row9-return-to-idle.txt`, `row9-rt-*.png`, `cap-rt1.txt`, `cap-rt2.txt` | row 9's return to idle in one instance, and the O3 reproduction attempts |
| `row10-second-launch.txt`, `row10-open-again.txt`, `row10-tray-start.txt`, `row10-armA-open.txt`, `row10-armB-exe.txt`, `row10-window-raised.png`, `row10-item.png` | row 10, both arms |
| `cap-*.txt` | every capture job: the plist bootstrap, the hook report, the bootout, the service either side |
| `machine-left.txt` | the end state, measured twice (after row 10, and after the row 9 reproduction) |
| `tools/cap.sh`, `tools/redact.swift` | my capture driver and the redaction tool |

---

# Wave A re-run, 2026-11-16 - recorder rows 4, 5 and 6 on `main` at `b0e6ae8` (with #113 and #156)

**Headline: rows 4 and 6 PASS on main with #113 in it - the fragmented container is in use and a
ten-minute take decodes to its expected end, which a truncation control shows the check can tell apart
from a cut file. Row 5 FAILS as measured, on a VIRTUAL input: the only audio input left on this Mac is
"Microsoft Teams Audio", which delivers about 0.78 x real time. A capture with the product out of the
loop reproduces that shortfall, so it is the device's - but the product then lays the remaining samples
end to end, which is #157's mechanism, provoked on macOS for the first time.**

| | |
|---|---|
| Machine | `devthrottle-mac-mini`, Mac16,10, macOS 26.5.2 arm64, **10 cores** |
| Commit tested | **`main` at `b0e6ae8`** (`b0e6ae8213c9777781ba28b588c5edc04c0a85fb`), the #113 merge; `git fetch origin` at 16:34 EST, `origin/main` = that commit; built from a detached checkout of it |
| Installed service, before | pid **50642**, `agenteyes.dll` reports **`1.11.11+f9ef2306539007cbd0ef8cc3d2a746e8dd88dfab`** - **pre-#113 and pre-#156**. Tarballed before replacement at `~/qa-backups/agenteyes-142-mac/` |
| Installed service, for every row below | pid **88521** (new pid; launchd `state = running`), `lsof` shows it holding the installed `agenteyes` and `agenteyes.dll`; the dll reports **`1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb`**. It answered every take and did not restart |
| App | `scripts/mac/install.sh` also reinstalled and opened `AgentEyes.app` (pid 89913), now reporting `1.11.11+b0e6ae82...` too; it polls `/status` once a second and does not record |
| Settings | **defaults**: no `fps` sent; the manifest records `-framerate 30 ... -r 30` |
| ffmpeg | 9.0.2, Homebrew; libx264 reports `threads=15 lookahead_threads=5 rc_lookahead=10 bframes=3` |
| Screen | **LOCKED** throughout (`CGSSessionScreenIsLocked=Yes` at 16:34 and 17:01) |
| Load | 1.9 - 3.3 on 10 cores; **no `ffmpeg` running** before the first take or after the last |
| Time | 16:34 - 17:03 EST |

## The verdicts

| # | Check | Verdict | Measured |
|---|---|---|---|
| 4 | Records the screen, no audio | **PASS** | 1 stream (h264), 626 frames, 30.0000 fps, last frame 20.833 s vs expected 20.76 s, 60 frames in the last 2 s, full decode exit 0 with empty stderr |
| 5 | Records screen + microphone, audio within 1.5 s of video | **FAIL as measured, on a VIRTUAL input (Microsoft Teams Audio) - shortfall reproduced without the product, so attributed to the device; NOT verified on physical hardware** | 2 of 2 takes: \|video - audio\| = **7.433 s** and **7.651 s**. The Yealink UH36 used for yesterday's physical measurement is no longer connected |
| 6 | A 10-minute recording completes and is playable (sharpened criterion) | **PASS** | expected 607.2 s; last frame **607.167 s** (-0.033 s); **60 frames in the last 2 s of a 607.2 s recording**; full decode exit 0, empty stderr. **Truncation control FAILS the check** on a copy cut at a clean fragment boundary |

## Reinstall: the apphost comparison and the Screen Recording grant

The protocol was: build `main`, byte-compare the new apphost against the installed one and compare their
cdhashes, install only if identical, and read `CGPreflightScreenCaptureAccess` for the service before and after.

| | installed (`f9ef230`) | built from `b0e6ae8` (`dotnet publish` with `install.sh`'s exact arguments) |
|---|---|---|
| `agenteyes` (apphost) sha256 | `348b93b81d867fc34529fc073bf93c0d23d9428923dbe65f3a5e36a1d2607022` | `348b93b81d867fc34529fc073bf93c0d23d9428923dbe65f3a5e36a1d2607022` |
| `cmp` | | **byte-identical** (exit 0) |
| `codesign` CDHash | `0b2bc9d96c152a6858bafc7ae9a6b84a1b717ffb` | `0b2bc9d96c152a6858bafc7ae9a6b84a1b717ffb` |
| `agenteyes.dll` sha256 | `1b9092d0...4111ddb0` | `439966ea...ccf0ff63` - differs, as expected |
| embedded commit | `f9ef2306...` | `b0e6ae82...` |

Identical, so I installed with `scripts/mac/install.sh` (exit 0, `macos/main-b0e6ae8/install-transcript.txt`).
`install.sh` rebuilds rather than copying my build; after install the apphost on disk is again
`348b93b8...` with CDHash `0b2bc9d9`, so what was installed is what I compared. The plist the script
writes is byte-identical to the one it replaced (`diff` empty).

`CGPreflightScreenCaptureAccess()` was read from a one-shot launchd job running the **installed**
`agenteyes` with the committed `QaCaptureHook` startup hook (no captures requested):

| Reading | Binary | Result |
|---|---|---|
| before install, 16:35:28 | installed `agenteyes` (`f9ef230`) | **True** |
| **negative control**, 16:35:36 | `AgentEyes.app`'s `AgentEyesDesktop`, which holds no grant | **False** |
| after install, 16:36:39 | installed `agenteyes` (`b0e6ae8`) | **True** |

The negative control is what makes the True readings mean something. The behavioural confirmation is
stronger than either: every take below captured a real, fully rendered picture of the lock screen,
which a service without the grant cannot do.

## Which build answered - by what it DID

The manifest of every take records the capture command the service ran, and it now ends
`-movflags +frag_keyframe+empty_moov+default_base_moof+delay_moov -frag_duration 2000000 -flush_packets 1`.
The `f9ef230` build had no `-movflags` at all (see "Which build answered" above). The atom walks agree:
row 4's file is `ftyp / moov(28) / 13 x moof+mdat / mfra` - a fragmented file whose index is at the
front - where yesterday's row 4 had `moov` at the end. The log shows #113's watchdog
(`StartWatchdog: watching .../raw.mp4 (stall 45s, start-up 45s, every 2s)`) and #156's claim
(`CaptureLock TryAcquire ... claimed by capture session of pid 88521`, `Release ... released by pid 88521`)
on every take.

## Row 4 - Records the screen, no audio: PASS

20.10 s by my wall clock (20.10 - 21.58 s between the inner and outer bounds of the two API calls);
manifest `DurationSeconds` 20.76. `~/Movies/AgentEyes/2026-11-16_164310_video`.

| Measurement | Value |
|---|---|
| Streams | exactly 1 - h264 video, no audio, as asked |
| Decoded frames | **626**, first 0.000 s, last **20.833 s**, **30.0000 fps** |
| Container duration | 20.867 s |
| Frames in [18.76, 20.76] s | **60** |
| Full decode (`ffmpeg -map 0 -f null -`) | **exit 0, empty stderr** |
| Atoms | `ftyp`, `moov` at 28, 13 `moof`+`mdat`, `mfra`; the walk ends exactly at the file size |

Frame at t = 19.5 s (`macos/main-b0e6ae8/frames/row4-t19.5s.jpg`): a complete, correctly rendered image
of the lock-screen wallpaper. `package` failed with `extract audio (exit 234)` - #126, as on every platform.

**A driver error of mine, kept rather than hidden.** My first row 4 stop request had no body; the
service answered **411 Length Required** and logged
`POST /record/stop REJECTED before any effect`, so that take kept recording until I stopped it 5 min
29 s later (`2026-11-16_163736_video`, 328.98 s; `macos/main-b0e6ae8/row4-driver-error-take.txt`). That is
the API refusing a malformed request as designed, not a product defect. The row 4 above is the clean re-take.

## Row 5 - Records screen + microphone: FAIL as measured, on a VIRTUAL input (Microsoft Teams Audio) - shortfall reproduced without the product, so attributed to the device; NOT verified on physical hardware

**The input.** `system_profiler SPAudioDataType` and `GET /devices` list exactly one audio input,
**"Microsoft Teams Audio"** - a virtual device from Microsoft Teams' Core Audio driver (Teams is running;
no call). The Yealink UH36 USB microphone used for yesterday's rows 5 and 6 is not connected. I asked the
lead, who ruled: measure on the virtual input and label it.

Two 30 s takes, `source: mic`, shipping defaults:

| | take A (`2026-11-16_164424_video`) | take B (`2026-11-16_164458_video`) |
|---|---|---|
| video | 946 frames, **31.533 s**, 30.0000 fps | 943 frames, **31.433 s**, 30.0000 fps |
| frames in the last 2 s of the expected take | 60 | 60 |
| audio track | AAC 48 kHz mono, ends at **24.100 s** | ends at **23.783 s** |
| **\|video end - audio end\|** | **7.433 s** - bound 1.5 s | **7.651 s** - bound 1.5 s |
| product's own log | `WARN [AudioCapture] Stop: ... = 23.70s of audio over a 30.68s capture - 6.98s MISSING` | `... = 23.39s of audio over a 30.59s capture - 7.20s MISSING` |
| mux | `done`, attempt 1, 0.3 s | `done`, attempt 1, 0.3 s |
| audio content | digital silence, `volumedetect` max **-91.0 dB** | max -91.0 dB |

**Beside yesterday's physical-device numbers** (Yealink UH36, `main` at `f9ef230`, from this report above):

| Take | device | build | video | audio | shortfall | samples captured / capture time |
|---|---|---|---|---|---|---|
| row 5, 2026-11-15 | Yealink UH36 (physical) | f9ef230 | 32.500 s | 31.451 s | **1.049 s** | 30.976 s of ~31 s |
| row 6, 2026-11-15 | Yealink UH36 (physical) | f9ef230 | 605.667 s | 604.578 s | **1.089 s** | 604.096 s |
| row 5 A, today | Teams Audio (virtual) | b0e6ae8 | 31.533 s | 24.100 s | **7.433 s** | 23.70 of 30.68 s = **0.773** |
| row 5 B, today | Teams Audio (virtual) | b0e6ae8 | 31.433 s | 23.783 s | **7.651 s** | 23.39 of 30.59 s = **0.765** |
| row 6, today | Teams Audio (virtual) | b0e6ae8 | 607.200 s | 472.764 s | **134.436 s** | 472.38 of 605.92 s = **0.780** |

The device class changed the answer completely, and the build is not what changed it - see the discriminator.

### The discriminator: the same device with the product out of the loop

If the recorder were losing samples, a capture that bypasses it would get them. So I ran **ffmpeg
directly**, writing the device **straight to a file** - no pipe, no product reader, no mux - from a one-shot
launchd job, inside the granted `agenteyes` binary through a startup hook (`macos/main-b0e6ae8/tools/MicHook.cs`)
so it has the same microphone attribution as the service:

| Direct capture | wall-clock | samples written | ratio |
|---|---|---|---|
| 1, to WAV | 30.033 s | **23.520 s** | **0.783** |
| 2, to Matroska | 20.028 s | **15.966 s** | **0.797** |

**The same ~22 % shortfall, without the product.** The device delivers about 0.78 x real time. The
product's WARN line reports exactly what the device gave it.

**And the deficit is proportional, not a fixed loss.** The take grew 19.75 x (30.68 s -> 605.92 s of
capture) and the missing audio grew 19.1 x (6.98 s -> 133.54 s). A start-up loss would have stayed
near 7 s at ten minutes; it reached 133.5 s. That is a rate characteristic of the device.

### Two defects stacked, which this report keeps apart

1. **The SHORTFALL is the device's.** 7.4 s of samples per 30 s are never delivered to anyone. If
   #157 were fixed tomorrow those samples would still be missing, and the file would still have 7 s
   less audio content than video.
2. **The MISALIGNMENT is the product's, and it is #157.** The device's packets carry timestamps that
   follow wall-clock time: in direct capture 2, **1,486 packets span 20.266 s** of timestamps (20.028 s
   of wall-clock), yet hold 15.97 s of samples. The missing samples are **414 small gaps** (> 5 ms each,
   4.69 s in total), spread evenly - 78, 83, 82, 82 and 83 in each 4 s of the take - not a few large
   holes (`macos/main-b0e6ae8/row5-direct-device-timestamps.txt`; Matroska timestamps have 1 ms resolution).
   So the timing information exists at capture. The product's mic path reads the device as headerless
   `s16le` over a pipe (`AvfStream Start mic: ... -f s16le pipe:1`), which keeps the samples and drops
   the timestamps, and the mux then lays the samples end to end. The audio track's length is exactly
   that: `mic_native.wav` + the start-offset `adelay` = 23.701 + 0.390 = 24.091 s against 24.100 s
   measured (take A), and 472.384 + 0.374 = 472.758 s against 472.764 s (row 6). With the gaps preserved,
   each sample would sit at its capture time with silence where none arrived, and the file would stay
   **in sync while short**. Here, instead, audio drifts progressively early - by 7.4 s at the end of a
   30 s take and 134 s at the end of ten minutes.

**What I could NOT measure, stated plainly:** the misalignment is *inferred*, from the timestamps and
the accounting above, and not observed in the file. The audio is digital silence (-91 dB), so no
event in it can be lined up against the picture. Where in the pipeline the timestamps are lost
(the `s16le` pipe) is read from the product's own logged command line; I did not instrument it.

### What a virtual device cannot tell us

Yesterday's seat measured a real **~49.6 ppm** drift term on the Yealink (95 % CI 12.8 - 86.4 ppm, excluding
zero), attributed to a crystal mismatch between the microphone's clock and the system timebase. A virtual
input has no crystal of its own. Here that point cuts the other way from the one I expected: this device
is **dramatically worse** than the physical one, not better, so nothing here could be misread as an
improvement. But it does mean **today's macOS row 5 says nothing about drift on physical hardware**, and
yesterday's **1.049 s and 1.089 s on the Yealink at `f9ef230` remain the only physical-microphone
evidence on this platform.** In particular it does not show whether #113's container change affects sync
on a real microphone. The lead has asked the owner to reconnect it.

## Row 6 - A 10-minute recording completes and is playable: PASS

605.13 s by my wall clock (605.13 - 608.14 s between the inner and outer bounds); manifest
`DurationSeconds` **607.2**, `CapturedSeconds` 607.13. Screen + the Teams input (the only audio this Mac
now has), defaults. `~/Movies/AgentEyes/2026-11-16_164737_video`, 325,009,201 bytes.

**The expected length is not read from the file.** For a healthy stop the manifest's `DurationSeconds` is
the session's elapsed time (`RecordingService.cs:997`), set before and independently of the file. (For an
*abandoned* capture #113 replaces it with a probe of the file, `RecordingService.cs:1011` - which would make
this check circular - so I also bound it with my own wall clock, and 607.2 s sits inside 605.13 - 608.14 s.)
**Tolerance: +/- 1.0 s** on the last frame, and **>= 54 of the 60** frames a 30 fps capture produces in the
last 2 s.

| Measurement | final `recording.mp4` (muxed) | raw fragmented capture (`recording.original.mp4`) |
|---|---|---|
| decoded video frames | **18,216**, 30.0000 fps | 18,216, 30.0000 fps |
| last frame | **607.167 s** vs expected 607.200 s: **-0.033 s** | 607.167 s |
| **frames in [605.2, 607.2] s** | **60** (605.200 ... 607.167) | 60 |
| full decode | exit 0, empty stderr | exit 0, empty stderr |
| atoms | `ftyp / free / mdat / moov` at 324,528,271 | `ftyp / moov(28) / 363 x moof+mdat / mfra` |
| **sharpened check** | **PASS** | **PASS** |

Mux `done`, attempt 1, **2.7 s** - read from the manifest's `PostProcessing.mux.State` and the product's
`stage mux: start` / `done ... in 2.7s` lines, not from `/status`. (Yesterday's 13.68 s on `f9ef230` was a
different build and a real microphone's audio; I did not investigate the difference.) Frames at 5 s, 300 s
and **606 s** (`macos/main-b0e6ae8/frames/row6-t*.jpg`) are complete, correctly rendered images.

Row 6's audio is 134.4 s short, for the reason given under row 5. It is not part of row 6's criterion.

### The truncation control - the check fails on a cut file

| Copy, in scratch (never in the library) | `moov` present | decode exit 0 | stderr | last frame | frames in the last 2 s of the **expected** end | **check** |
|---|---|---|---|---|---|---|
| row 6 raw, cut at a fragment boundary (offset 285,706,294 = start of fragment 327 of 363) | **yes**, at 28 | **0** | **empty** | 544.900 s (-62.3 s) | **0** | **FAIL** |
| row 4, cut at a fragment boundary (start of fragment 9 of 13) | yes | 0 | empty | 14.300 s (-6.46 s) | 0 | **FAIL** |
| row 4, cut mid-atom at 70 % of its bytes | yes | 0 | `Invalid NAL unit size ... partial file` | 14.833 s | 0 | **FAIL** |

This reproduces on macOS what the Linux seat found: **"`moov` present" and "decode exits 0" both pass on a
truncated fragmented file** - cut on a fragment boundary, even stderr is empty. And a "frame near the end of
the bytes" check passes too, since the cut copy decodes 30 fps continuously up to 544.9 s. Only the frames
at the **expected** end separate the real file from the cut one. Evidence:
`macos/main-b0e6ae8/truncation-control-row6.txt`, `truncation-control-row4.txt`.

## The empty window on this machine

Measured, not carried over: two screen-only takes at the default 30 fps, polling the file every 50 ms
(`macos/main-b0e6ae8/tools/emptywin.py`, output `emptywin.txt`).

| Run | start POST took | first byte on disk, after the POST returned | first byte, from the start command |
|---|---|---|---|
| 1 | 0.802 s | **2.888 s** | **3.69 s** |
| 2 | 0.800 s | **2.909 s** | **3.71 s** |

**About 3.7 s from the start command (2.9 s after the API returns), at 30 fps on 10 cores, libx264
`threads=15`.** Before then the file is empty (`delay_moov`), so an unclean end leaves nothing. The first
byte arrives as one complete fragment, and in the same run, as a positive control, a copy of it decoded to
**60 frames**. Not measured: an unclean end itself. I killed nothing, so the "fragment plus start-up delay"
loss figure is not re-measured here, and neither is any other frame rate.

## What this run did NOT cover

- **Physical microphone.** Neither row 5 nor row 6 on macOS speaks to physical hardware today. The Yealink
  UH36 is disconnected, so yesterday's 1.049 s and 1.089 s on that device, at `f9ef230`, remain the only
  physical-microphone evidence on this platform. Whether #113's container change affects A/V sync on a real
  microphone is **unmeasured on macOS**.
- **#157 under its intended trigger.** It was provoked here by a device, not by load. Neither a loaded
  physical mic nor a fix was tested, and the misalignment is inferred from timestamps, not seen.
- **Stall resilience (#113's failure path).** Every take was healthy. No capture was frozen or killed, so
  the watchdog was observed starting and stopping, never firing.
- **#156's protection against a second process.** The claim was observed being taken and released. Nothing
  tried to take it from another process.
- **Any frame rate other than 30**, and therefore #184 and #188's low-fps behaviour.
- **The desktop app.** It was reinstalled by `install.sh` as a side effect and not re-tested; rows 2, 3 and
  7 - 10 stand as measured at `277a9bc` above.
- **System audio** - still impossible on this Mac without a virtual loopback device.
- **CLAUDE.md's gate does not apply on a Mac.** The macOS gate was run on `b0e6ae8`:
  `dotnet build src/AgentEyes.Core -c Release -p:AgentEyesUnixPlatform=mac` **0 errors** (1 warning);
  `dotnet test tests/AgentEyes.Tests.Mac -c Release` **430 passed, 0 failed, 2 skipped**
  (`macos/main-b0e6ae8/gate.txt`). The Linux test project was not run.

## Hazards - what I touched and what I did not

- **Replaced the owner's service**: pid 50642 (`f9ef230`) -> pid **88521** (`b0e6ae8`), via the supported
  installer, after the apphost check. It is left installed and running. The previous `bin/` and plist are in
  `~/qa-backups/agenteyes-142-mac/`.
- **Reinstalled and opened `AgentEyes.app`** - `install.sh` does this unconditionally. Left running (pid 89913).
- **Added seven recordings to the owner's library** (84 -> 91 directories): row 4, the 411 driver-error
  take, rows 5 A and B, row 6, and two empty-window takes. Each is
  named in its evidence file. **Nothing was deleted.** All truncated copies were made in scratch and removed
  from there.
- **No TCC or privacy setting changed.** No `screencapture` was run from my shell. Five one-shot launchd jobs
  (`com.agenteyes.qa142r6.preflight-before`, `.preflight-negctl`, `.preflight-after`, `.micdirect1`,
  `.micdirect2`), each bootstrapped, polled with a hard timeout, finished on its own with exit 0 and was booted
  out (`post-bootout print exit=113`). `launchctl list | grep -c qa142` = 0 at the end.
- **ffmpeg**: none was killed by me or by the watchdog. The only ffmpeg processes I started were the two in the
  direct captures, and both exited on `q` with exit 0. `pgrep ffmpeg` was empty at the end.

## Evidence (re-run, all under `macos/main-b0e6ae8/`)

`install-transcript.txt`, `post-install-identity.txt`, `preflight-before-service.txt`,
`preflight-after-service.txt`, `preflight-negative-control-app.txt`, `gate.txt`, `row4-take.txt`,
`row4-probe.txt`, `row4-driver-error-take.txt`, `row5A-take.txt`, `row5A-probe.txt`, `row5B-take.txt`,
`row5B-probe.txt`, `row5-direct-device-capture.txt`, `row5-direct-device-timestamps.txt`, `row6-take.txt`,
`row6-probe.txt`, `truncation-control-row6.txt`, `truncation-control-row4.txt`, `emptywin.txt`,
`product-log-excerpt.txt`, `end-state.txt`, `frames/`, and the instruments in `tools/` (`take.sh`,
`probe.py`, `emptywin.py`, `MicHook.cs`, `QaMicHook.csproj`). The capture hook is the committed
`docs/cencon/proof/issue-116/mac-pass3-capture-tools/StartupHook.cs`, unchanged.

---

# Row 5 on the physical microphone, and the hour-scale drift take - 2026-11-16, 17:13 - 18:20 EST

**Headline: row 5 PASSES on macOS on physical hardware - 0.614 s and 0.670 s on two 66 s takes against a
1.5 s bound, with real, non-silent audio and no detectable dropped samples. The virtual-input FAIL earlier
tonight was the device. The single hour take measured 1.094 s video-minus-audio at 3661 s, and a mic-side
drift of 43.9 ppm, inside yesterday's 95 % prediction interval; adding it to yesterday's 20 captures narrows
the drift term to 44.9 ppm, 95 % CI 27.4 - 62.4 ppm.**

| | |
|---|---|
| Machine | `devthrottle-mac-mini`, the same as above |
| Build | **`main` at `b0e6ae8`**, the install left by the re-run above - **not reinstalled**. Confirmed before the first take: service pid **88521**, `agenteyes.dll` embeds `1.11.11+b0e6ae8213c9777781ba28b588c5edc04c0a85fb`, apphost sha256 `348b93b8...` and dll sha256 `439966ea...` unchanged |
| Screen Recording grant | `CGPreflightScreenCaptureAccess() = True`, read at 17:13:21 from the **installed** `agenteyes` in a one-shot launchd job with the committed `QaCaptureHook` (no captures requested), booted out afterwards (`physical-yealink/preflight-before.txt`) |
| Microphone | **Yealink UH36** (reconnected by the owner): default input, USB, 2 input channels, **Current SampleRate 16000** (`system_profiler`); `GET /devices` lists it as mic 0 |
| Settings | defaults: screen 1 + `source: mic`, no `fps` (manifest records `-framerate 30 ... -r 30`) |
| Driver | the re-run's `tools/take.sh` and `tools/probe.py`, unchanged; "finished" = manifest `PostProcessing.mux.State` = `done`, never `/status` |

## Row 5 - Records screen + microphone, audio within 1.5 s of video: PASS on physical hardware

| | take A (`2026-11-16_171328_video`) | take B (`2026-11-16_171441_video`) |
|---|---|---|
| wall clock (start returned -> stop sent) | 65.122 s | 65.114 s |
| video | **1995 frames**, last 66.467 s, end **66.500 s**, 30.0000 fps | **1996 frames**, last 66.500 s, end **66.533 s**, 30.0000 fps |
| audio stream | AAC 48 kHz mono, duration **65.886 s** | **65.863 s** |
| **\|video end - audio end\|** | **0.614 s** - bound 1.5 s | **0.670 s** - bound 1.5 s |
| mean / max volume, final track (`volumedetect`) | **-71.2 dB** / -54.3 dB | **-69.3 dB** / -45.1 dB |
| mean / max volume, `mic_native.wav` (as captured, before the chain) | -82.5 dB / -66.2 dB | -71.7 dB / -40.3 dB |
| product accounting | `[AudioCapture] Stop: wrote 6282240 bytes of PCM = 65.44s of audio over a 65.67s capture` (INFO) | `... = 65.41s of audio over a 65.65s capture` (INFO) |
| full decode | exit 0, empty stderr | exit 0, empty stderr |
| mux | `done`, attempt 1 | `done`, attempt 1 |

**The accounting closes:** video end - (`adelay` + `mic_native.wav`) = 66.500 - (0.4456 + 65.440) = **0.614 s**,
exactly the measured gap (take A); 66.533 - (0.4533 + 65.408) = 0.672 s against 0.670 s (take B).

### Beside both prior sets - the comparison is the finding

| Take | device | build | take | video - audio | samples captured / capture time |
|---|---|---|---|---|---|
| row 5, 2026-11-15 | Yealink UH36 (physical) | f9ef230 | 32.5 s | 1.049 s | 30.98 of 31.22 s |
| row 6, 2026-11-15 | Yealink UH36 (physical) | f9ef230 | 605.7 s | 1.089 s | 604.10 of 604.38 s |
| row 5 A, 2026-11-16 16:44 | Teams Audio (virtual) | b0e6ae8 | 31.5 s | **7.433 s** | 23.70 of 30.68 s = 0.773 |
| row 5 B, 2026-11-16 16:44 | Teams Audio (virtual) | b0e6ae8 | 31.4 s | **7.651 s** | 23.39 of 30.59 s = 0.765 |
| **row 5 A, tonight** | **Yealink UH36 (physical)** | **b0e6ae8** | 66.5 s | **0.614 s** | 65.44 of 65.67 s |
| **row 5 B, tonight** | **Yealink UH36 (physical)** | **b0e6ae8** | 66.5 s | **0.670 s** | 65.41 of 65.65 s |

**Tonight lands near yesterday's physical figures - about 0.4 s better, not worse - and nowhere near the
virtual device's 7.4 - 7.7 s.** Same build as the virtual-input FAIL, different device, so the device class
explains that failure, as the out-of-the-loop control had already attributed. I did not isolate why tonight
is ~0.4 s better than yesterday; the build changed (#113's container) and so did the take length.

### Was something actually recorded? Yes - live samples; acoustic content only in the hour take

The two 66 s takes are quiet: raw RMS -82.6 dB and -71.6 dB, peaks -66.2 dB and -40.3 dB. That is a real,
varying, non-zero signal, against the virtual input's **digital silence (max -91.0 dB)**, and it matches the
same device yesterday measured the same way tonight (`2026-11-15_134706_video`: raw -81.3 / -66.2 dB, final
-70.0 / -54.2 dB). I made no sound - it is the owner's machine - so the row 5 takes prove live device samples,
not captured speech. **The hour take does prove acoustic capture**: its raw track peaks at **-3.7 dB**, with
loud events at 549 - 550 s, ~1022 s, 1502 s and 3567 s into the take (`hour-loudest-seconds.txt`) - sounds
in the room, not made by me.

### (1) The 16 kHz device and #146's resample

The chain the product built, from its own log (`row5-product-log-excerpt.txt`):

```
capture: ffmpeg -f avfoundation -thread_queue_size 1024 -i :0 -ac 1 -ar 48000 -f s16le pipe:1
mux:     [0:a]aeval=...,aformat=channel_layouts=mono,aresample=48000,asetnsamples=n=480:p=1,
         arnndn=m='.../bd.rnnn',speechnorm=e=4:p=0.95,volume=1[m];[m]adelay=445.625:all=1,alimiter=...
```

**The 16 kHz -> 48 kHz conversion happens at capture**, in the reader's ffmpeg (`-ar 48000`), so
`mic_native.wav` is already 48000 Hz (ffprobe). In the mux chain `aresample=48000` sits **before**
`asetnsamples` and `arnndn`, where #146 put it - but on macOS it receives 48 kHz and does no work. **So a 16 kHz
device does not exercise #146's ordering on this platform**; it exercises ffmpeg's capture-side resampler, and
every recording completed with the mux `done` on attempt 1. Whether a 16 kHz device on Windows, where #146
bit, reaches the chain at 16 kHz is not covered here.

### (2) Were samples dropped? None detectable - and that does not retire #157

The capture deficit is **0.23 s (A) and 0.24 s (B)**. The product's clock starts at `AudioCapture Start` and the
first sample arrives at `capture clock start`: **0.199 s and 0.200 s** later. The unexplained residual is
**0.03 - 0.04 s**, well under the product's 1.0 s WARN slack, and both lines logged at INFO. Limit: the `s16le`
pipe discards timestamps, so a gap below ~50 ms could not be seen by this method. **A clean row 5 does not
retire #157** - its timestamp-collapsing mux only bites when samples are dropped, and these takes dropped none.
#157 remains provoked only by the virtual device, as above.

### (3) The product's own accounting

Quoted in the table: `65.44s of audio over a 65.67s capture` and `65.41s ... over a 65.65s capture`, both INFO,
neither saying MISSING. On the virtual device the same line read `6.98s MISSING` and `7.20s MISSING`.

## The hour-scale drift take

One take, as asked. `2026-11-16_171737_video`, Yealink UH36, defaults, 17:17:37 - 18:18:39.

| Measurement | Value |
|---|---|
| wall clock | 3660.212 s (3660.21 - 3662.76 s between the inner and outer bounds); manifest `DurationSeconds` **3661.87** |
| video | **109,856 frames**, last 3661.833 s, end **3661.867 s**, **30.0000 fps**; 59 frames in the last 2 s of the expected take; full decode exit 0, empty stderr |
| audio stream | AAC 48 kHz mono, **3660.772 s** |
| **video end - audio end** | **1.094 s** - under 1.5 s with 0.406 s headroom |
| product accounting | `[AudioCapture] Stop: wrote 351390720 bytes of PCM = 3660.32s of audio over a 3660.74s capture` (INFO) |
| warm-up (Start -> capture clock start) | 0.197 s |
| mux | `done`, attempt 1, **75.3 s** (`stage mux: done ... in 75.3s`); the driver waited **75.5 s past `idle`** - #159 again, at hour scale |
| volume | final track mean -66.7 dB, max -9.9 dB; raw mean -52.3 dB, max **-3.7 dB** |
| file | 2,020,728,599 bytes, `ftyp / free / mdat / moov`, atom walk ends at the file size |

**Accounting closes:** 3661.867 - (0.45223 + 3660.320) = 1.095 s against 1.094 s measured.

### Instrument A - the mic drift, the quantity yesterday's fit measured

| | value |
|---|---|
| deficit at T = 3660.74 s | **0.420 s** (bytes are exact; the capture time is logged to 0.01 s, so +/- 1.4 ppm) |
| yesterday's fit predicts (0.2593 s + 49.6 ppm x T) | **0.441 s**, 95 % prediction interval **0.297 - 0.584 s** -> **INSIDE** |
| **implied drift** using yesterday's intercept, (0.420 - 0.2593) / T | **43.9 ppm** |
| implied drift using tonight's measured warm-up, (0.420 - 0.197) / T | 60.9 ppm |
| **refit, yesterday's 20 + this take** (n = 21) | **0.2603 s + 44.9 ppm x T**, SE 8.4 ppm, **95 % CI 27.4 - 62.4 ppm**, t = 5.38 |
| refit with tonight's two 66 s takes too (n = 23) | 0.2573 s + 46.2 ppm x T, 95 % CI 29.1 - 63.2 ppm |

**Consistent with yesterday's fit.** The point estimate barely moves (49.6 -> 44.9 ppm), and the interval
narrows from 12.8 - 86.4 to **27.4 - 62.4 ppm**: the hour of lever arm halves the slope's standard error. The
drift is real, still excludes zero, and is crystal-mismatch-sized. (Mixed builds: yesterday's 20 points are
`f9ef230`, tonight's are `b0e6ae8`. The mic path the deficit measures - `AvfStream`, `AudioCapture` - is
the same code in both as far as these log lines show; I did not diff it.)

### Instrument B - video minus audio, the quantity the criterion measures

| Take | build | T | video - audio |
|---|---|---|---|
| row 5, yesterday | f9ef230 | 32.5 s | 1.049 s |
| row 6, yesterday | f9ef230 | 605.7 s | 1.089 s |
| long take, yesterday | f9ef230 | 1802.5 s | 0.847 s |
| row 5 A / B, tonight | b0e6ae8 | 66.5 s | 0.614 / 0.670 s |
| **hour take, tonight** | **b0e6ae8** | **3661.9 s** | **1.094 s** |

Against tonight's two short takes on the same build, the gap grew **0.45 s over 3595 s - 126 ppm if read as
pure drift. It is not all drift**, and the decomposition says where it went (video end - audio end =
[video length - mic capture window] - adelay + mic deficit):

| term | take A | take B | hour |
|---|---|---|---|
| mic deficit (instrument A) | 0.23 | 0.24 | **0.42** (+0.19 s: the drift) |
| video length - mic capture window | 0.83 | 0.88 | **1.13** (+0.27 s) |
| - adelay | -0.446 | -0.453 | -0.452 |
| = video - audio | 0.614 | 0.670 | 1.094 |

The second term grew by 0.27 s, and the log shows where: the mic stops first and the video pipeline drains
after it - **86 ms and 132 ms on the 66 s takes, 414 ms on the hour** (`FfmpegRecorder Stop: pipeline drained
in ...`). That is a stop-time tail on the video side, not a clock rate. **One hour-long point cannot say
whether the tail grows with take length or is scatter** (yesterday's 30-minute take came in *lower* than its
short takes); I flag it rather than fit it.

### What this means for row 12's criterion

- **At one hour, both candidate criteria hold:** flat 1.5 s (1.094 s, 0.41 s headroom) and **1.0 s + 100 ppm x T**
  (1.366 s at 3661.9 s; 0.27 s headroom).
- **A flat 1.5 s is still not safe for always-on.** From 1.094 s at one hour, the mic drift alone (44.9 ppm,
  upper 62.4 ppm) adds 0.16 - 0.22 s per hour, reaching 1.5 s at roughly **3.0 h (point) / 2.8 h (upper)**; if
  the whole 126 ppm growth of instrument B were proportional, at about **1.9 h**.
- **1.0 s + 100 ppm x T has room on the mic drift** (the 95 % upper bound, 62 ppm, is well inside 100 ppm), but
  **not necessarily on the video stop tail**: if instrument B really grew at 126 ppm from a 0.6 s base it would
  cross 1.0 s + 100 ppm x T at about 4 h. Whether it does is what a second long take, or a fix that stops the
  mic after the video drains, would settle. This is the data; the criterion is the lead's to write.

## What this run did NOT cover

- **Captured speech in the row 5 takes** - they record live device noise only; acoustic content is shown only
  by the hour take's loud events. **A/V alignment of any event** - nothing in the picture (a locked screen)
  marks when a sound happened, so sync is inferred from durations and accounting, not observed.
- **Why tonight's short takes are ~0.4 s better than yesterday's** - build vs take length vs scatter, not isolated.
- **#146 on a 16 kHz device where it matters** - on macOS the conversion is at capture, so the chain's
  `aresample` is a no-op here. Windows with a 16 kHz device is untested.
- **#157** - no samples were dropped, so it was not exercised. Sub-50 ms gaps are below this method's resolution.
- **Whether the video stop tail grows with length** - one hour-long point; flagged, not fitted.
- **Any frame rate other than 30, stall resilience, the desktop app** - unchanged from the re-run above.
- **The gate** - no product code changed and nothing was built from source; the macOS gate result above
  (`b0e6ae8`, 430 passed) is the previous seat's and was not re-run.

## Hazards - what I touched and what I did not

- **Nothing reinstalled.** Service pid 88521 answered every take and is still running; apphost and dll hashes
  unchanged. `com.agenteyes.recorder` not touched. `AgentEyes.app` not touched.
- **Added three recordings to the owner's library** (91 -> 94): `2026-11-16_171328_video`, `2026-11-16_171441_video`,
  `2026-11-16_171737_video` (2.0 GB). **Nothing deleted.** Yesterday's Yealink recordings were read, not modified.
- **No audio settings, default input, preset, TCC or privacy setting changed.** No `screencapture` run.
- **One one-shot launchd job** (`com.agenteyes.qa142r5p.preflight`), exit 0, booted out (`print exit=113`);
  `launchctl list | grep -c qa142` = 0.
- **ffmpeg**: I started none directly against the device and killed none. The only ffmpeg runs of mine were
  `ffprobe`/`ffmpeg -f null` analyses of files.

## Evidence (all under `macos/main-b0e6ae8/physical-yealink/`)

`preflight-before.txt`, `device.txt`, `row5A-take.txt`, `row5A-probe.txt`, `row5B-take.txt`, `row5B-probe.txt`,
`row5-product-log-excerpt.txt`, `hour-take.txt`, `hour-probe.txt`, `hour-product-log-excerpt.txt`,
`hour-fit.txt`, `hour-loudest-seconds.txt`, and `tools/fit.py` (refits yesterday's 20 points from
`macos/audiocapture-stop-lines.txt` and tests new points against it; it reproduces yesterday's
0.2593 s + 49.6 ppm, t = 2.83, exactly).
