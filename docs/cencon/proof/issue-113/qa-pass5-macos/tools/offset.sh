#!/bin/zsh
# offset.sh <run-dir>  : print start_time of every stream in raw (recording.original.mp4 / raw.mp4) and final, plus edit-list presence and first video pts
for D in $1/root/Videos/AgentEyes/*(N); do
  echo "## $(basename $D)"
  for f in $D/raw.mp4(N) $D/recording.original.mp4(N) $D/recording.mp4(N); do
    echo "-- $(basename $f) $(stat -f %z $f) bytes"
    ffprobe -v error -show_entries stream=index,codec_type,start_time,duration,nb_frames,r_frame_rate -of compact $f
    echo "   edts/elst boxes: $(python3 -c "import sys;d=open(sys.argv[1],'rb').read();print('elst' , d.count(b'elst'))" $f)"
    echo "   first 3 video pkts (pts_time,dts_time): $(ffprobe -v error -select_streams v:0 -show_entries packet=pts_time,dts_time -read_intervals %+#3 -of csv=p=0 $f | tr '\n' ' ')"
  done
done
