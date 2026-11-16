. "$SP/lib.sh"; set +u
run_env $SP/runs/orphan-why; rm -f $RUNROOT/transcript.txt $RUNROOT/strace.txt
strace -f -qq -e trace=exit_group,write -e signal=all -o $RUNROOT/strace.txt ${BINS[fix]} serve --port 17695 > $RUNROOT/w.stdout 2>&1 &
ST=$!
for i in $(seq 1 60); do grep -q "recordings ->" $RUNROOT/w.stdout 2>/dev/null && break; sleep 0.5; done
ROOT=$(grep -m1 "recordings ->" $RUNROOT/w.stdout | sed 's/.*-> //'); case "$ROOT" in $RUNROOT/*) ;; *) kill -9 $ST; echo ABORT; exit 9;; esac
SV=$(pgrep -P $ST -f "serve --port 17695"); say "strace $ST, serve $SV, root ok"
api 17695 POST /record/start "$START_BODY" >/dev/null; sleep 8
D=$(newest_dir); W=$(pgrep -P $SV -f x11grab); say "writer $W SigIgn=$(grep SigIgn /proc/$W/status|awk '{print $2}') SigBlk=$(grep SigBlk /proc/$W/status|awk '{print $2}') fds: 0->$(readlink /proc/$W/fd/0) 1->$(readlink /proc/$W/fd/1) 2->$(readlink /proc/$W/fd/2)"
kill -9 $SV; say "serve SIGKILLed"
for i in $(seq 1 20); do say "  writer alive=$(alive $W) raw=$(sz $D/raw.mp4)"; kill -0 $W 2>/dev/null || break; sleep 0.5; done
sleep 1; kill $ST 2>/dev/null
say "last strace lines for writer $W:"; grep "^$W " $RUNROOT/strace.txt | grep -v "write(2[0-9]\|write([3-9]," | tail -8 | cut -c1-200
say "writes to fd 2 (stderr) by writer: $(grep -c "^$W  *write(2," $RUNROOT/strace.txt)  EPIPE results: $(grep "^$W " $RUNROOT/strace.txt | grep -c EPIPE)"
