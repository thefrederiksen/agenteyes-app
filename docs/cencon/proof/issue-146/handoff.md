# Issue #146 - Developer handoff to QA

Branch `issue-146-resample-before-framing`, off `main` 432105b. Windows seat, SORENLAPTOP.

## The change

One stage, in one place. `FfmpegArgs.MicChain` now emits

```
aeval(...),aformat=channel_layouts=<mic>,aresample=48000,asetnsamples=n=480:p=1,arnndn=...,speechnorm=...,volume=...
```

`aresample=48000` (`FfmpegArgs.ArnndnRate`) goes ahead of #128's framing, only when denoise is on.
arnndn accepts only 48 kHz. Without the explicit stage, ffmpeg inserted its own resampler at arnndn's
input, AFTER the framing. That re-chunked the audio and undid the framing.

**Where, and why there:** an explicit resample in the shared chain, not a forced dshow capture rate.
Measured in `windows/capture-rate-measurements.txt`: dshow does not resample, and a mic that does not
offer the requested rate fails to open (no recording at all). The chain is shared by all three
platforms, so this one stage covers every mic source at any rate.

## Acceptance criteria - Expected vs Actual

| # | Expected | Actual | Evidence |
|---|---|---|---|
| 1 | Default-settings recording, 44.1 kHz mic, completes 10 of 10 | **10 of 10.** App driven over REST, mic "Microphone (2- Realtek(R) Audio)" at its native 44100 Hz, ffmpeg 9.0.2. Each run: every PostProcessing stage `done`, `Attempts 1`, settled 4.1-4.2 s after stop. Before the fix, same machine, mic and ffmpeg: 10 of 10 stalled. | `windows/c1-44k-ten-recordings.txt` |
| 2 | 48 kHz case still 10 of 10 | **10 of 10.** Real 48 kHz captures, all ten ending on a partial 480-sample frame, through the product's `AudioMix.ProcessVideoMic`. Not app recordings: the app cannot be made to capture this mic at 48 kHz. Why, stated in the file. | `windows/c2-48k-ten-captures.txt` |
| 3 | Order pinned: graph quoted, a test fails if swapped | Graph quoted from the app log. GATE GUARD: `tests/Shared/MicChainOrderTests.cs` sweeps every mic-chain builder by reflection (the #136 pattern, no ffmpeg) and fails by name in 21 ms when swapped; it runs on all three CI jobs. With the stages **swapped**, 5 of 7 tests FAIL. On ffmpeg 8.1.1, which does not stall, the executed-graph test still fails the unfixed chain: `into arnndn: from G0_auto_aresample_1`. Fixed: 10 of 10 pass on 9.0.2 and on 8.1.1. | `windows/c3-order-pinned.txt` |
| 4 | macOS and Linux audited | Both deliver 48 kHz on `main`. macOS since #114 resamples to 48 kHz in `AvfStream`. Linux pulse defaults to 48 kHz. The fix is in the shared chain anyway, so it is pinned by a 16/22.05/44.1/48 kHz test and a shared order test that runs on all three CI jobs. No seat needed. | `c4-macos-linux-audit.md` |
| 5 | Denoise and leveling measurably applied | 44.1 kHz noise: denoise removes 28.9 dB. Quiet signal: leveling adds +12.0 dB. Both are committed as opt-in tests (`AGENTEYES_FFMPEG_TESTS=1`). On the ten real recordings leveling shows (+12.0 dB). Denoise **cannot** be shown on them: the mic captured about -97 dBFS, so there was nothing to remove. Said plainly, not claimed. | `windows/c5-audio-still-processed.txt` |
| 6 | Ten-minute default-settings recording, every stage `done`, `Attempts 1` | **Completed.** 615 s MIXED recording (the shape that stalled 3 attempts as #142 row 6), 44.1 kHz mic. Manifest PostProcessing: mux, thumbnail, package and plugins all `done`, `Attempts 1`. Mux stage took 47.2 s; everything settled 56.6 s after stop; 615.6 s of 48 kHz audio. | `windows/c6-ten-minute.txt` |
| 7 | Gates green on all three platforms, with counts; CI green over more than one run | **Windows:** build 0 errors; `dotnet test` 2381 passed, 4 skipped (opt-in ffmpeg tests), 0 failed. **Linux/macOS:** run locally on a Windows host, both suites gain exactly the 4 new shared tests (Linux 281 passed vs 277 on main, macOS 277 vs 273). Their only failures (4 Linux file-mode tests, 1 macOS path test) fail identically on unmodified main on this host. The real Linux and macOS gate is CI. **CI:** see the PR checks; the open requirement is more than one green run. | PR checks |

## How QA should verify

- **Gate:** `dotnet build AgentEyes.sln -c Release`, then `dotnet test AgentEyes.sln -c Release`. The
  real-ffmpeg tests (`MicChainSampleRateTests`) are SKIPPED in that gate by design. To run them, set
  `AGENTEYES_FFMPEG_TESTS=1` plus `QA_RECORD_FFMPEG_DIR=%LOCALAPPDATA%\AgentEyes\app` (the shipped
  9.0.2). Without the second variable, on a dev machine, `FfmpegLocator` picks whatever ffmpeg is on PATH (8.1.1 here),
  where the stall never reproduces. The executed-graph test still sees the mechanism on 8.1.1.
- **The order test bites:** swap `{ArnndnRate},{ArnndnFraming}` in `FfmpegArgs.MicChain` and run
  `--filter "FullyQualifiedName~MicChainOrderTests"`. The sweep must fail, naming every builder.
- **Running app (audio path, so worth a smoke):** launch
  `src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe --tray` (the `x64`
  path, never `bin\Release`), with `QA_RECORD_FFMPEG_DIR` set as above. Then
  `POST /record/start {"mode":"video","screen":1,"source":"mic","mic":"<a 44.1 kHz mic>"}`, wait,
  `POST /record/stop`, and read `manifest.json` PostProcessing. Expect every stage `done`, `Attempts 1`.
  The capture's native rate is in `raw.mp4.ffmpeg.log` ("Stream #1:0: Audio: ... 44100 Hz").
  `recording.original.mp4` is the unprocessed capture kept for comparison.
- **Closing the C5 gap:** a run with real speech or room noise on a 44.1 kHz mic, comparing
  `recording.original.mp4` with `recording.mp4`, would show denoise on a real capture.
- Reminders: drive the app over REST, UIA or PrintWindow; never force-foreground it and synthesize
  input. The recording HUD is capture-excluded, so assert its state with UIA or `/status`, never a
  screenshot.

## CenCon impact

No drift. No component-map or privacy-posture change: the recorder captures exactly what it did
before. Only the post-processing graph gains one stage.
