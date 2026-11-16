# Issue #146, criterion 4 - macOS and Linux capture paths audited

Read from the argument builders on `main` 432105b (macOS #114 merged today). Neither platform
can be run from the Windows seat, so this is a code reading plus a test, not a measurement on
those machines, and it says which is which.

## What rate each platform hands the mic chain

| Platform | Mic capture path | Rate pinned? | Rate the mic chain receives |
|---|---|---|---|
| Windows | dshow input inside the screen ffmpeg (`FfmpegArgs.VideoCapture`), no `-sample_rate` | **No** | The dshow default for that device. 44.1 kHz on SORENLAPTOP's Realtek mic (measured), 48 kHz on the #128 machine. **This is the hole #146 is about.** |
| Linux (screen + mic) | `-f pulse` input inside the screen ffmpeg (`FfmpegArgs.PulseInput`), no `-sample_rate` | By ffmpeg's default, not by our args | 48 kHz: ffmpeg's pulse demuxer defaults its `sample_rate` option to 48000 and the PulseAudio/PipeWire server resamples to it. **From ffmpeg's source, not measured here** (the Windows ffmpeg build has no pulse device). |
| Linux (audio-only, meters) | `PulseStream`, its own ffmpeg, `-ar 48000` output option (`Linux.AudioCapture.SampleRate = 48000`) | **Yes** | 48 kHz |
| macOS | Since #114: `AvfVideoCapture` carries **no** audio input. The mic is `AvfStream`, its own ffmpeg, `-ar 48000` output option (`Mac.AudioCapture.SampleRate = 48000`) | **Yes** | 48 kHz, whatever the device delivers. `-ar` on the output resamples, so AVFoundation's 16 kHz becomes 48 kHz before it reaches the WAV |

## The AVFoundation 16 kHz observation

That was the device's native rate, seen when the macOS mic was still an input of the screen
ffmpeg with no rate given (the pre-#114 `AvfAudioInput`, which no code calls any more). On
`main` the macOS mic WAV is written at 48 kHz by `AvfStream`. A recording made **before** #114
and recovered afterwards could still carry a 16 kHz mic track into post-processing.

## Why neither needs its own seat

The fix is not at any capture. It is in `FfmpegArgs.MicChain`, the clean-voice graph that
**every** platform's post-processing runs (`FilterMicToWav`, `MixMicSystemToWav`,
`MixTwoWav`). Whatever rate a mic arrives at, from any platform, the chain resamples it to
48 kHz ahead of the framing. This is pinned by test, not just argued:

- `MicChain_AnySourceRate_ArnndnIsFedStraightFromTheFramingAt48k` (Windows suite, opt-in
  `AGENTEYES_FFMPEG_TESTS=1`, real ffmpeg
  9.0.2 and 8.1.1) runs 16000, 22050, 44100 and 48000 Hz sources. In ffmpeg's **executed** graph,
  arnndn's input comes from `asetnsamples` at 48000 Hz in all four.
- `tests/Shared/MicChainOrderTests.cs` (pure, no ffmpeg, in the default gate) sweeps every mic-chain
  builder in the build and is compiled into the **Linux and macOS**
  test projects as well as Windows, so the order is checked on all three CI jobs.

**Conclusion:** neither macOS nor Linux can hand arnndn a non-48 kHz source after this change.
Before it, macOS on `main` and Linux could not either in a fresh recording (both deliver 48 kHz).
The one residual path was a recovered pre-#114 macOS recording, and the shared chain now covers
that too. No macOS or Linux seat needed for this defect.
