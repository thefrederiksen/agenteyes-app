# Capture shortfall on devlinux - is it load or a bug? (v1.12.0)

Measured 2026-11-20 on devlinux (Ubuntu, X11 session on DISPLAY :0, AMD Threadripper 1950X, 32 threads,
GTX 1060). The installed recorder is AgentEyes 1.12.0+02a4622 (the v1.12.0 tag commit), running as the
systemd user service agenteyes.service, PID 2690287. No code or config was changed for these runs.

Both runs used the same request through the Control API:

    POST /record/start {"mode":"video","screen":1,"source":"system","fps":15}

That means x11grab of the full 1920x1080 monitor, libx264, and system-audio loopback, with no mic or camera.

## Result

| | Run 1 (busy host) | Run 2 (settled host) |
|---|---|---|
| Recording dir | 2026-11-20_212444_video | 2026-11-20_214911_video |
| Host load1 at start | 12.5 - 17 range in the minutes before (sampled 21:23-21:39) | 7.32 at start, 10.73 at end |
| System CPU idle during the run | not measured | 81.0% (from /proc/stat deltas across the run) |
| Session wall clock (service) | 19.46 s | 20.85 s (stop answer DurationSeconds) |
| Session wall clock (client side) | n/a | 19.82 s from start answer to stop request; 21.77 s from start request to stop answer |
| Video duration (raw capture, ffmpeg final time=) | 10.86 s | 20.86 s |
| Video frames | 166 | 316 |
| Requested fps | 15 | 15 |
| Achieved capture rate | 8.8 fps, speed=0.579x, dup=53 drop=17 | 15 fps, speed=0.991x, no dup or drop reported |
| Video short of the session by | 8.60 s (44%) | about 0 s |
| /status LastCaptureFault | "capture behind: the capture wrote 10.86s of a 19.46s session; raw.mp4 is short by 8.60s" | null |

**Answer to the question asked:** on a settled host, a ~20.9 s session produced 20.86 s of video at the
full 15 fps. The run-1 shortfall is **load-dependent degradation**: x11grab plus libx264 fell to 0.58x
real time while the host was busy. It is not a fixed-rate bug that shows up on every run.

**Caveat, not hidden:** the host was never idle. The go2rtc camera wall's ffmpeg (about 127% CPU) runs
all the time and is not ours to stop, and other sessions' dotnet and python jobs were running too.
load1 settled to about 5 before run 2 (load-samples-before-run2.txt) and then rose to 10.7 during it.
So run 2 is a "settled", not an "idle", measurement. It still produced full-length video.

## Is the shortfall recorded anywhere other than /status?

Yes, in three places. An earlier report from this seat said "not in the log file". **That was wrong.**
That search used the /status wording ("capture behind", "short by"), and the log line words it differently.

1. **Service log** (~/.local/share/AgentEyes/logs/AgentEyes-20261120.log), at WARN level:

       21:25:04.630 [pid 2690287] [WARN] stop: the screen capture wrote 10.86s of a 19.46s session - recording DurationSeconds=10.86, not the session length

2. **The recording's own manifest.json** keeps it durably with the recording:
   `CaptureFault: "behind"`, `CaptureFaultDetail`, `CaptureFaultDetectedUtc`, `CaptureFaultElapsedSeconds`
   (manifests-and-ffprobe.txt).
3. **The recording's raw.mp4.ffmpeg.log** has the capture's own figures: `fps=8.8 ... dup=53 drop=17 speed=0.579x`.

Searches run (over every AgentEyes-*.log file, Oct 15 - Nov 20, plus tray.log):

    grep -nE 'wrote [0-9.]+s of a [0-9.]+s session' AgentEyes-*.log tray.log   -> run 1's WARN, plus 15 such lines on 2026-11-14
    grep -ciE 'capture behind|short by|CaptureFault|behind' AgentEyes-*.log    -> 0 on 2026-11-20 (4 on 11-14, all stalled/exited verdicts)
    grep -ciE 'drop=|dup=|dropped|duplicat' AgentEyes-*.log tray.log           -> 0 in every file
    journalctl --user -u agenteyes --since "2026-11-20 21:24" --until "2026-11-20 21:26" | grep -iE 'behind|short|fault|drop'  -> 0

**What the service log does NOT record:**
- The drop and duplicate counts and the achieved fps. These are only in the per-recording ffmpeg log.
- A `PublishVerdict: /status now reports ... LastCaptureFault=...` line for the "behind" fault. On
  2026-11-14 that line is logged for "capture stalled" and "capture exited". No PublishVerdict line exists
  anywhere in the 2026-11-20 log, so "behind" reaches /status without its verdict being logged.
- The word "behind". A search for the /status text does not find the WARN line.

## Two observations from run 1, outside the question

- **Durations disagree.** The WARN line and the /record/stop answer say `DurationSeconds=10.86`, but the
  manifest says `DurationSeconds: 16.5`. The finished recording.mp4 has an 11.07 s video track and a 16.5 s
  audio track, so its container duration is 16.5 s. Each reader gets a different length, and none of
  them is the 19.46 s session.
- **Capture start-up gap.** System audio started about 2.6 s after the start request in run 1
  (16.5 s of audio, 3,168,000 PCM bytes), and run 2's audio is 19.76 s (3,793,920 bytes) against a 20.85 s
  session. Recorded here only. Not investigated.

## Files

- load-samples-before-run2.txt: load1 and top CPU users every 30 s while waiting for load to settle
- run1-raw.mp4.ffmpeg.log, run2-raw.mp4.ffmpeg.log: the capture ffmpeg's own logs (home path scrubbed)
- manifests-and-ffprobe.txt: each recording's fault and duration fields, plus ffprobe of its mp4s
- service-log-excerpts.txt: the service-log lines for both runs

Both recordings are kept on devlinux. Nothing was deleted, and no process outside AgentEyes was touched.
