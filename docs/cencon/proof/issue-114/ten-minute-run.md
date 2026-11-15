# Issue #114 - the 10-minute and 60-second runs, as measured

Machine `devthrottle-mac-mini`, macOS 26.5, arm64, ffmpeg 9.0.2 (Homebrew), one 1920x1080
display, mic `Yealink UH36` (avfoundation `:0`). Driven through the Control API on
127.0.0.1:7882, which is the only way a real screen capture happens on this machine - TCC grants
the Screen Recording permission to the launchd agent, not to a shell.

All three runs were started with mic processing OFF:

```
{"mode":"video","screen":1,"source":"mic","mic":"Yealink UH36",
 "denoise":false,"gate":false,"level":false}
```

**Why processing is off, and it is not to make the numbers look better.** With the default
`denoise`/`level` on, the mux hangs forever - a PRE-EXISTING defect in the shared clean-voice
chain (`arnndn` followed by `speechnorm`, on roughly a minute or more of audio), proved in the
handoff note to reproduce in the shape the pre-existing `FilterVideoMic` already emits, and so
not caused by this change. It needs its own issue. These runs are therefore the honest scope of
what is verified end to end.

---

## The 10-minute run - `~/Movies/AgentEyes/2026-11-14_103623_video`

`/record/stop` response:

```json
{
  "Dir": "/Users/soren/Movies/AgentEyes/2026-11-14_103623_video",
  "File": "/Users/soren/Movies/AgentEyes/2026-11-14_103623_video/recording.mp4",
  "DurationSeconds": 609.49,
  "Shots": 0
}
```

`ffprobe` of the files:

```
recording.mp4            video[30/1, 609.900000 s, 18297 frames]  audio[48000 Hz, 609.132062 s, 28555 frames]
recording.original.mp4   video[30/1, 609.900000 s, 18297 frames]  audio[none - the capture is video only]
mic_native.wav           audio[48000 Hz, 608.672000 s]
audio_16k.wav            audio[16000 Hz, 609.132062 s]
```

AAC frames into seconds: **28555 x 1024 / 48000 = 609.17 s**.

`manifest.json` -> `FfmpegCommand`:

```
"/opt/homebrew/bin/ffmpeg" -y -f avfoundation -thread_queue_size 1024 -capture_cursor 1
  -pixel_format nv12 -framerate 30 -i 0:none -c:v libx264 -preset veryfast -pix_fmt yuv420p
  -crf 23 -r 30 .../raw.mp4
```

and the microphone's own ffmpeg, a separate process:

```
"/opt/homebrew/bin/ffmpeg" -hide_banner -nostats -loglevel info -f avfoundation
  -thread_queue_size 1024 -i :0 -ac 1 -ar 48000 -f s16le pipe:1
```

From `raw.mp4.ffmpeg.log`:

```
  Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12, 1920x1080, 1000k tbr, 1000k tbn, start 156552.689000
Output #0, mp4, to '.../raw.mp4':
  Stream #0:0: Video: h264 (avc1 / 0x31637661), yuv420p(tv, progressive), 1920x1080, q=2-31, 30 fps, 15360 tbn
frame=18297 fps= 30 q=-1.0 Lsize=  318645KiB time=00:10:09.83 bitrate=4280.4kbits/s dup=4 drop=0 speed=   1x elapsed=0:10:09.94
```

`dup=4 drop=0` over 18297 frames: the constant-rate output repeated four frames in ten minutes,
so the capture is delivering a genuine 30 fps, not 13 fps padded up to 30.

Service log for this recording:

```
Stop: mic/video alignment - video first frame at 156552.689000s, mic first sample at 156553.149063s, offset 0.460s
[AvfStream] Stop mic: ffmpeg reported ... size=   57063KiB time=00:10:08.83 bitrate= 767.8kbits/s speed=   1x
[AudioCapture] Stop: wrote 58432512 bytes of PCM = 608.67s of audio over a 609.10s capture
[FfmpegRecorder] Stop: pipeline drained in 86ms (take 608338ms, media origin 1338ms) - full take captured
```

### Expected vs Actual

| # | Criterion | Expected | Actual | |
|---|---|---|---|---|
| 1 | device accepts the rate | input `30 tbr`, output `30 fps` | output **`30 fps`**; input **`1000k tbr`** | PARTLY - see below |
| 2 | audio spans a 10-minute recording | within 3.0 s | video 609.90 s, audio 609.13 s, **diff 0.77 s** | PASS |
| 4 | file length vs reported | within 1.0 s | 609.90 s vs 609.49 s, **diff 0.41 s** | PASS |
| 5 | frame count vs rate | 18297 +-10% | **18297 frames, 609.90 s, 30.00 fps** | PASS |
| 6 | `audio_16k.wav` spans it | within 3.0 s | 609.13 s vs 609.90 s, **diff 0.77 s** | PASS |
| 7 | encoder keeps up | >= 0.95x | **speed=1x** | PASS |

Criterion 1's input clause is NOT met and cannot be with this ffmpeg: `tbr` is the demuxer's
estimate of the input's rate, an AVFoundation screen input reports none ("not enough frames to
estimate rate"), and `avfoundation` is the only screen backend this ffmpeg has. The handoff note
has the detail. The number was not faked to close the criterion.

---

## The 60-second run - `~/Movies/AgentEyes/2026-11-14_104905_video`

Made with the FINAL binary of this branch (the 10-minute run above was made a few commits'
worth of comments earlier; this one re-confirms it after the last behaviour-neutral changes).
Reported `DurationSeconds: 61.36`.

```
recording.mp4            video[30/1, 61.766667 s, 1853 frames]  audio[48000 Hz, 60.750604 s, 2849 frames]
mic_native.wav           audio[48000 Hz, 60.224000 s]
audio_16k.wav            audio[16000 Hz, 60.750625 s]
```

2849 x 1024 / 48000 = **60.78 s** of samples.

| # | Criterion | Expected | Actual | |
|---|---|---|---|---|
| 3 | audio spans a 60 s recording | within 3.0 s | video 61.77 s, audio 60.75 s, **diff 1.02 s** | PASS |
| 4 | file length vs reported | within 1.0 s | 61.77 s vs 61.36 s, **diff 0.41 s** | PASS |
| 5 | frame count vs rate | 1853 +-10% | **1853 frames, 61.77 s, 30.00 fps** | PASS |
| 6 | `audio_16k.wav` spans it | within 3.0 s | 60.75 s vs 61.77 s, **diff 1.02 s** | PASS |
| 7 | encoder keeps up | >= 0.95x | **speed=0.999x** | PASS |

---

## Before and after, on the same machine

| | before (issue #114, `2026-11-14_085412_video`) | after |
|---|---|---|
| asked | 30 s | 60 s / 600 s |
| video | 35.30 s, 458 frames, **12.97 fps** | 61.77 s, 1853 frames, **30.00 fps** / 609.90 s, 18297 frames, **30.00 fps** |
| audio in the file | **4.03 s** of a 35.30 s video (88% missing) | **60.78 s** of a 61.77 s video / **609.17 s** of a 609.90 s video |
| `audio_16k.wav` | 3.97 s | 60.75 s / 609.13 s |
| ffmpeg's audio total | **37 KiB** | 57063 KiB over ten minutes, at the full 767.8 kbit/s |
| drain at stop | 2724 ms, hit the 5 s timeout every time | **63-229 ms** |

## The capture-rate ceiling, measured before the fix

Asking the throttled agent for a frame rate, and what the device delivered:

| asked | delivered |
|---|---|
| 10 fps | 10.04 fps |
| 15 fps | 12.18 fps |
| 20 fps | 12.51 fps |
| 30 fps | 13.24 fps |
| 60 fps | 13.99 fps |

`-capture_cursor 0`, `-pixel_format nv12` and `-drop_late_frames false` were each measured
against this and each changed nothing. After `ProcessType Interactive` the same ask delivers
30.00 fps with 4 duplicated frames in ten minutes, which is what identifies the ceiling as the
throttling rather than the device.

## The QoS measurement that identifies the cause

The same ffmpeg command line, same machine, same device, differing only in scheduling class:

```
normal QoS                      -> 1,920,000 bytes = 20.00 s of a 20 s capture.  No loss.
background QoS (taskpolicy -b)  ->   319,392 bytes =  3.33 s of a 20 s capture.  83% GONE.
```
