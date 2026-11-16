. "$SP/lib.sh"; set +u
T=$1; PORT=$2; U=ae156qa-$T
run_env $SP/runs/systemd-$T; rm -rf $RUNROOT/home/Videos/AgentEyes $RUNROOT/home/.local/share/AgentEyes $RUNROOT/transcript.txt
say "=== systemd run $T: transient user unit $U, KillMode=mixed Restart=on-failure RestartSec=10 TimeoutStopSec=120 (as the installed unit)"
systemd-run --user --unit=$U -p KillMode=mixed -p KillSignal=SIGTERM -p TimeoutStopSec=120 -p Restart=on-failure -p RestartSec=10 \
  --setenv=HOME=$HOME --setenv=XDG_CONFIG_HOME=$XDG_CONFIG_HOME --setenv=XDG_DATA_HOME=$XDG_DATA_HOME --setenv=DISPLAY=:0 \
  --setenv=XAUTHORITY=$XAUTHORITY --setenv=XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR ${BINS[$T]} serve --port $PORT 2>&1 | tee -a $RUNROOT/transcript.txt
for i in $(seq 1 40); do R=$(journalctl --user -u $U --since "-2min" -o cat 2>/dev/null | grep -m1 "recordings ->" | sed 's/.*-> //'); [ -n "$R" ] && break; sleep 0.25; done
case "$R" in $RUNROOT/*) say "unit recordings root inside scratch: $R";; *) systemctl --user kill -s SIGKILL $U; systemctl --user stop $U; say "ABORT root=$R"; exit 9;; esac
M=$(systemctl --user show -p MainPID --value $U); say "MainPID $M"
api $PORT POST /record/start "$START_BODY" > $RUNROOT/start.json; sleep 12
D=$(python3 -c "import json;print(json.load(open('$RUNROOT/start.json'))['Dir'])"); W=$(pgrep -P $M -f x11grab); say "B=$(basename $D) writer $W; $(state $D)"
kill -9 $M; say "MainPID $M SIGKILLed"
for i in $(seq 1 26); do
  M2=$(systemctl --user show -p MainPID --value $U); S=$(systemctl --user show -p ActiveState,SubState --value $U | tr '\n' '/')
  say "  +$((i*2))s unit=$S MainPID=$M2 writer $W alive=$(alive $W) raw=$(sz $D/raw.mp4)"
  sleep 2
done
say "waiting for the restarted service's first repair pass..."; sleep 15
M2=$(systemctl --user show -p MainPID --value $U); say "restarted MainPID $M2"
journalctl --user -u $U --since "-3min" -o cat | grep -E "ReapOrphans: pid|reap examined|FindUnfinished|capture claim|NoteStarted|NoteDone|NoteFailed|AudioChannels FAILED" | cut -c1-260 | sed 's/^/    /' | tee -a $RUNROOT/transcript.txt
L=$LOGDIR/*.log; grep -hE "ReapOrphans: pid|reap examined|FindUnfinished|capture claim|NoteStarted|NoteDone|NoteFailed|AudioChannels FAILED" $L | cut -c1-260 | sed 's/^/    log: /' | tee -a $RUNROOT/transcript.txt
say "B: $(state $D)"; ffprobe -v error -show_entries format=duration -of csv=p=0 $D/raw.mp4 2>&1 | head -1 | sed 's/^/  raw probe: /' | tee -a $RUNROOT/transcript.txt
decode_check $D/recording.mp4
systemctl --user stop $U; systemctl --user reset-failed $U 2>/dev/null; say "unit stopped: $(systemctl --user is-active $U 2>&1)"
[ -d /proc/$W ] && { say "LEFTOVER writer $W - SIGTERM"; kill -TERM $W; }
foreign_check
