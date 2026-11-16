#!/usr/bin/env bash
# Issue #156 Linux QA - scenario v2: tests 1-4 in ONE run on one binary, real X11 screen captures.
#   combo2.sh <bintag> <port-base>
# B, C: captured by S0 / S0c, whose owners are SIGKILLed mid-capture (a real crash).
#       A foreign 'package' meets B while its orphaned writer is STILL WRITING (test 4).
# S1:   the next service start (reaps orphans), then captures A live for the rest of the run.
#       A foreign 'package' meets A three times (test 1); and meets C after the reap (CLI positive control).
# S2:   a second service; its real repair pass meets live A and orphaned B together (tests 2 + 3).
SP=${SCRATCH:?set SCRATCH to an empty scratch directory}
. $SP/lib.sh
set +u
BINS[merged]=$WT/merged/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
T=$1; PB=$2
run_env $SP/runs/v2-$T
rm -rf "$RUNROOT/home/Videos/AgentEyes" "$RUNROOT/home/.local/share/AgentEyes" "$RUNROOT"/*.log "$RUNROOT"/*.stdout "$RUNROOT"/*.json "$RUNROOT"/transcript.txt "$RUNROOT"/decode.*
trap cleanup EXIT
MINE=()   # every ffmpeg this run's services started
B_BIN=${BINS[$T]}
say "=== v2 run: bin=$T embedded=$(strings ${B_BIN}.dll | grep -m1 -o '1\.11\.11+[0-9a-f]\{40\}') CaptureLock=$(strings -el ${B_BIN}.dll | grep -c '\[CaptureLock\]') frag=$(strings -el ${B_BIN}.dll | grep -c frag_duration) watchdog=$(strings -el ${B_BIN}.dll | grep -c '\[CaptureWatchdog\]') host=$(hostname) ffmpeg=$(ffmpeg -version | head -1 | cut -d' ' -f3) load=$(cut -d' ' -f1 /proc/loadavg)"
foreign_check
growth() { local a b; a=$(sz "$1"); sleep 3; b=$(sz "$1"); echo "$a -> $b in 3s"; }
st1() { api $((PB+1)) GET /status | python3 -c 'import json,sys;s=json.load(sys.stdin);print("State=%s Elapsed=%s CaptureLiveness=%s LastStopFailed=%s LastStopError=%s" % (s.get("State"), s.get("ElapsedSeconds"), s.get("CaptureLiveness"), s.get("LastStopFailed"), (s.get("LastStopError") or "")[:160]))' 2>&1 | cut -c1-300; }

# ---- B and C: two captures whose owners crash
start_serve "$T" $PB s0; S0=$SERVE_PID; MYPIDS+=($S0)
api $PB POST /record/start "$START_BODY" > "$RUNROOT/s0.start.json"
sleep 2
start_serve "$T" $((PB+3)) s0c; S0C=$SERVE_PID; MYPIDS+=($S0C)
api $((PB+3)) POST /record/start "$START_BODY" > "$RUNROOT/s0c.start.json"
sleep 12
DB=$(python3 -c "import json;print(json.load(open('$RUNROOT/s0.start.json'))['Dir'])")
DC=$(python3 -c "import json;print(json.load(open('$RUNROOT/s0c.start.json'))['Dir'])")
BW=$(pgrep -P $S0 -f x11grab | head -1); CW=$(pgrep -P $S0C -f x11grab | head -1)
MINE+=($(pgrep -P $S0) $(pgrep -P $S0C))
say "B = $(basename "$DB") owner S0 pid $S0, writer pid $BW;  C = $(basename "$DC") owner S0c pid $S0C, writer pid $CW"
[ -f "$DB/capture.owner.json" ] && say "  B capture.owner.json: $(tr -d '\n ' < "$DB/capture.owner.json" | cut -c1-300)"
lockinfo "$DB"
kill -9 $S0 $S0C; sleep 1
say "S0 and S0c SIGKILLed mid-capture (alive: $(alive $S0) $(alive $S0C)); writers alive: B $(alive $BW) C $(alive $CW), B writer ppid now $(ps -o ppid= -p $BW | xargs)"
say "  B raw.mp4 growth with its owner dead: $(growth "$DB/raw.mp4")"
lockinfo "$DB"

# ---- test 4: a foreign process meets a still-writing orphan
for i in 1 2; do
  package "$T" "$DB" "Borphan$i"
  say "  B writer alive: $(alive $BW); B raw.mp4 growth: $(growth "$DB/raw.mp4")"
done

# ---- S1: the next service start (reap), then the live capture A
start_serve "$T" $((PB+1)) s1; S1=$SERVE_PID; MYPIDS+=($S1)
sleep 1
say "after S1 start: B writer alive $(alive $BW), C writer alive $(alive $CW)"
grep -h "pid $S1\]" "$LOGDIR"/*.log | grep -E "ReapOrphans: (pid|no rec)|reap examined" | sed "s/^/    /" | cut -c1-300 | tee -a "$RUNROOT/transcript.txt"
say "  B raw.mp4 after reap: $(growth "$DB/raw.mp4")"
api $((PB+1)) POST /record/start "$START_BODY" > "$RUNROOT/s1.start.json"
sleep 12
DA=$(python3 -c "import json;print(json.load(open('$RUNROOT/s1.start.json'))['Dir'])")
AW=$(pgrep -P $S1 -f x11grab | head -1); MINE+=($(pgrep -P $S1))
say "A = $(basename "$DA") owner S1 pid $S1, writer pid $AW"
[ -f "$DA/capture.owner.json" ] && say "  A capture.owner.json: $(tr -d '\n ' < "$DA/capture.owner.json" | cut -c1-300)"
lockinfo "$DA"
say "  A: $(state "$DA")"

# ---- test 1: a foreign process drives the finalize of the live capture
for i in 1 2 3; do
  package "$T" "$DA" "A$i"
  say "  A writer alive: $(alive $AW); A raw.mp4 growth: $(growth "$DA/raw.mp4"); S1 $(st1)"
done
# ---- CLI positive control, same run: a crashed recording with no live writer IS packaged
package "$T" "$DC" "Cdead"
decode_check "$DC/recording.mp4"

# ---- tests 2 + 3: a second service's real repair pass meets live A and orphaned B together
start_serve "$T" $((PB+2)) s2; S2=$SERVE_PID; MYPIDS+=($S2)
sleep 1
grep -h "pid $S2\]" "$LOGDIR"/*.log | grep -E "ReapOrphans: (pid|no rec)|reap examined" | sed "s/^/    /" | cut -c1-300 | tee -a "$RUNROOT/transcript.txt"
say "  A writer alive after S2's reap: $(alive $AW)"
say "waiting for S2's first repair pass (+20 s)..."
for _ in $(seq 1 120); do grep -h "pid $S2\]" "$LOGDIR"/*.log | grep -qE "\[RepairService\] RunAsync(: trigger=.* done| FAILED)" && break; sleep 1; done
say "S2 repair-pass lines (pid $S2):"
grep -h "pid $S2\]" "$LOGDIR"/*.log | grep -E "RepairService\] (RunAsync|Resume)|FindUnfinished|DECLINED|capture claim|admitted|NoteStarted|NoteFailed|NoteDone|ThrowIfLive|refusing|could not|FAILED|stale" | sed "s/^/    /" | cut -c1-420 | tee -a "$RUNROOT/transcript.txt"
say "  A: $(state "$DA")"
say "  B: $(state "$DB")"
decode_check "$DB/recording.mp4"
say "  S1 $(st1)"

# ---- one more foreign pass, then hold A through the 45 s watchdog window
package "$T" "$DA" "A4"
for i in 1 2 3 4 5 6; do
  sleep 10
  say "  +${i}0s A writer alive $(alive $AW); raw.mp4=$(sz "$DA/raw.mp4") recording.mp4=$(sz "$DA/recording.mp4") original=$(sz "$DA/recording.original.mp4"); S1 $(st1)"
done
say "S1 watchdog / stall lines:"; grep -h "pid $S1\]" "$LOGDIR"/*.log | grep -iE "stall|CaptureWatchdog\] .*(verdict|abandon|kill)|Abandon" | sed "s/^/    /" | cut -c1-300 | tee -a "$RUNROOT/transcript.txt"

kill -TERM $S2; for _ in $(seq 1 60); do kill -0 $S2 2>/dev/null || break; sleep 1; done; say "S2 stopped by SIGTERM (alive: $(alive $S2))"

# ---- the owner stops and finalizes A; follow the manifest, not /status (#159)
say "owner S1 POST /record/stop -> $(api $((PB+1)) POST /record/stop | python3 -c 'import json,sys;s=json.load(sys.stdin);print(s.get("State"), s.get("LastStopFailed"), s.get("LastStopError") or s.get("error"))' 2>&1 | cut -c1-300)"
wait_mux "$DA" 240
say "  A: $(state "$DA")"
decode_check "$DA/recording.mp4"
lockinfo "$DA"
say "S1 finalize lines:"; grep -h "pid $S1\]" "$LOGDIR"/*.log | grep -E "NoteStarted|capture claim|Mux.*(done|Done|FAILED)|refus|DECLINED" | sed "s/^/    /" | cut -c1-300 | tee -a "$RUNROOT/transcript.txt"

kill -TERM $S1; for _ in $(seq 1 90); do kill -0 $S1 2>/dev/null || break; sleep 1; done; say "S1 stopped by SIGTERM (alive: $(alive $S1))"
# ---- leftovers: any ffmpeg this run started that is still alive (e.g. an unreaped orphan)
for k in "${MINE[@]}"; do
  if [ -d /proc/$k ] && [ "$(cat /proc/$k/comm 2>/dev/null)" = ffmpeg ]; then
    say "LEFTOVER: my ffmpeg pid $k still alive ($(tr '\0' ' ' < /proc/$k/cmdline | cut -c1-60)) - SIGTERM"; kill -TERM $k; sleep 3; [ -d /proc/$k ] && kill -9 $k
  fi
done
say "  final B: $(state "$DB")"
say "  final C: $(state "$DC")"
foreign_check
say "=== v2 run $T done"
