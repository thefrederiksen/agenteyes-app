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
