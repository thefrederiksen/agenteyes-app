#!/bin/zsh
# Issue #136 - build the three mux fixtures QA needs to re-measure criteria 1, 2 and 3 on
# devthrottle-mac-mini WITHOUT recording anything. The defect is in the mux, which takes two files,
# and all the material is already on the machine.
#
#   usage:  ./build-mux-fixtures.sh [output-dir]
#
# Each fixture is a recording directory holding
#   raw.mp4        a long video stream plus a long mic track, muxed with NO filters
#   manifest.json  a PendingMux of Mode: video, Source: mic with the shipped default mic options
# so that RecordingService.FinalizePending(dir) - which 'agenteyes package <dir>' calls first - runs
# the real deferred mux over it. Report the wall clock and the ffprobe of the output.
#
# Building each raw.mp4 is itself PASS 2's shape (a video copy beside an audio encode, no filter
# graph), so the time it prints is a measurement in its own right: on the 602 s fixture it was 2.90 s.
#
# Do NOT delete 2026-11-14_111731_video, 2026-11-14_110301_video or 2026-11-14_111433_video. They
# are the material, and 111731 is the regression case.
set -eu

OUT=${1:-$HOME/agenteyes-136-fixtures}
A=$HOME/Movies/AgentEyes
mkdir -p "$OUT"

build() {
  local name=$1 video=$2 mic=$3 dur=$4
  local d=$OUT/$name
  rm -rf "$d"; mkdir -p "$d/shots"
  echo "=== $name"
  echo "    video $video"
  echo "    mic   $mic"
  local t0=$(date +%s.%N)
  ffmpeg -hide_banner -v error -y -i "$video" -i "$mic" \
    -map 0:v -c:v copy -map 1:a -c:a aac -b:a 160k "$d/raw.mp4"
  local t1=$(date +%s.%N)
  printf "    raw.mp4 built in %.2fs - this is PASS 2's shape, timed\n" $((t1-t0))
  cat > "$d/manifest.json" <<JSON
{
  "Tool": "AgentEyes",
  "Mode": "video",
  "Imported": false,
  "Label": "video",
  "CreatedUtc": "2026-11-14T16:17:31.5882860Z",
  "MonitorIndex": 1,
  "MonitorName": "Display 1",
  "Microphone": "Yealink UH36",
  "DurationSeconds": $dur,
  "VideoFile": "recording.mp4",
  "Transcripts": {},
  "TranscribeAttempts": 0,
  "TitleAttempts": 0,
  "ThumbAttempts": 0,
  "Shots": [],
  "Files": [ "recording.mp4" ],
  "Keep": false,
  "Housekeeping": [],
  "OriginalFiles": [],
  "PendingMux": {
    "Mode": "video",
    "Source": "mic",
    "RawVideo": "raw.mp4",
    "FinalFile": "recording.mp4",
    "RawDurationSeconds": $dur,
    "Options": {
      "MicGain": 1,
      "SystemGain": 0.7,
      "NoiseSuppression": true,
      "NoiseGate": false,
      "GateCalibrated": false,
      "VoiceLeveling": true
    }
  }
}
JSON
  ffprobe -v error -show_entries stream=codec_type,codec_name,channels,sample_rate \
    -show_entries format=duration,size -of default=noprint_wrappers=1 "$d/raw.mp4" | sed 's/^/    /'
}

# criterion 1 - the regression case: both halves of the mux that wedged, intact.
build c1-76s  "$A/2026-11-14_111731_video/raw.mp4"                "$A/2026-11-14_111731_video/mic_native.wav" 76.23
# criterion 3 - sixty seconds.
build c3-60s  "$A/2026-11-14_111433_video/recording.original.mp4" "$A/2026-11-14_111433_video/mic_native.wav" 61.83
# criterion 2 - ten minutes.
build c2-600s "$A/2026-11-14_110301_video/recording.original.mp4" "$A/2026-11-14_110301_video/mic_native.wav" 601.90

echo "=== done"
du -sh "$OUT"/*
echo
echo "Now run the mux over each one. Either:"
echo "  agenteyes package <dir>        - calls FinalizePending first, then goes on to transcribe"
echo "                                  (that later step fails if the machine is not signed in; the"
echo "                                   mux has already happened and the service log records it)"
echo "  or call RecordingService.FinalizePending(dir) directly, which is what was measured."
echo
echo "Expected: 76 s -> ~1.2 s, 61 s -> ~1.1 s, 602 s -> ~6.7 s, no audio_processed.wav left behind,"
echo "ffprobe audio vs video within 1.7 s. For the BEFORE, run the single-pass command quoted in"
echo "handoff.md criterion 1 against 2026-11-14_111731_video: it freezes near time=00:01:16 and does"
echo "not finish."
echo
echo "NOTE on the 60-second case: its audio also trips a SEPARATE, pre-existing speechnorm stall"
echo "(chain-stall-repeats.txt). A failure there is that, not this fix - it has no video stream and"
echo "no -filter_complex in the same command."
