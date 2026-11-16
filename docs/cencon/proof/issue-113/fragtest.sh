#!/usr/bin/env bash
# fragmented-MP4 survival test: growth cadence, SIGKILL, SIGSTOP+SIGKILL, clean q.
cd "$(dirname "$0")/frag" || exit 1
MIC=alsa_input.pci-0000_08_00.3.analog-stereo
FR=(-movflags +frag_keyframe+empty_moov+default_base_moof -frag_duration 2000000 -flush_packets 1)
PID=
start() {
    rm -f "$1.mp4" "$1.in"; mkfifo "$1.in"
    DISPLAY=:0 ffmpeg -hide_banner -y -f x11grab -thread_queue_size 1024 -framerate 30 -video_size 1920x1080 \
        -draw_mouse 1 -i :0+0,0 -f pulse -thread_queue_size 1024 -fragment_size 3840 -i $MIC \
        -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 -c:a aac -b:a 128k "${FR[@]}" "$1.mp4" \
        <"$1.in" >/dev/null 2>"$1.log" &
    PID=$!
    exec 7>"$1.in"
}
probe() { echo "  probe: $(ffprobe -v error -count_frames -show_entries format=duration:stream=codec_type,nb_read_frames -of compact=p=0 "$1.mp4" 2>&1 | tr '\n' ' ')"; }

start kill9; s=""; for i in $(seq 1 10); do sleep 1; s="$s $(stat -c %s kill9.mp4)"; done
kill -9 $PID; wait $PID 2>/dev/null; exec 7>&-
echo "kill9 size each second:$s"; probe kill9

start stopkill; sleep 8; kill -STOP $PID; s1=$(stat -c %s stopkill.mp4); sleep 5; s2=$(stat -c %s stopkill.mp4)
kill -9 $PID; wait $PID 2>/dev/null; exec 7>&-
echo "stopkill size at STOP=$s1, 5s later=$s2"; probe stopkill

start clean; sleep 8; echo q >&7; exec 7>&-; wait $PID
echo "clean exit=$? size=$(stat -c %s clean.mp4)"; probe clean
tr '\r' '\n' < clean.log | grep "frame=" | tail -1 | cut -c1-120
uptime | sed 's/.*load/load/'
