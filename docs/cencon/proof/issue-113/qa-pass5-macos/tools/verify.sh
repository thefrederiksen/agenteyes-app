#!/bin/zsh
# verify.sh <file> <pngout> : frames, duration, full decode exit + error-line count, a VIDEO frame from the video stream's last second
f=$1
echo "file $(basename $(dirname $f))/$(basename $f) $(stat -f %z $f) bytes"
ffprobe -v error -count_frames -show_entries stream=index,codec_type,duration,nb_read_frames,start_time:format=duration -of compact $f
ffmpeg -v error -i $f -f null - 2> $2.decode.err; rc=$?; echo "full decode exit=$rc error_lines=$(wc -l < $2.decode.err | tr -d ' ')"
vd=$(ffprobe -v error -select_streams v:0 -show_entries stream=duration -of csv=p=0 $f)
ss=$(python3 -c "print(max(0.0, float('$vd') - 1.0))")
rm -f $2; ffmpeg -v error -y -ss $ss -i $f -map 0:v:0 -frames:v 1 $2 2> $2.err; rc=$?
lastpts=$(ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 $f | sort -g | tail -1)
echo "video duration=$vd; frame read at t=$ss (last second): exit=$rc png_bytes=$(stat -f %z $2 2>/dev/null || echo NONE) err_lines=$(wc -l < $2.err | tr -d ' '); highest video pts=$lastpts"
