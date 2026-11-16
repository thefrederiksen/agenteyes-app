#!/bin/bash
# Runs INSIDE a --network none container: 127.0.0.1:7882 there is the container's own. Times a redirected
# `agenteyes --help` N times per shape, with the PRODUCTION default probe (no AGENTEYES_SERVICE_PROBE_PORT).
DLL="$1"; N="${2:-10}"; LABEL="$3"; D=/usr/lib/dotnet/dotnet
unset AGENTEYES_SERVICE_PROBE_PORT
probe() { timeout 1 bash -c 'exec 3<>/dev/tcp/127.0.0.1/7882' 2>/dev/null; }
run() {
  local tot=0 added=0 i
  for i in $(seq 1 $N); do
    r=/tmp/r-$1-$i; mkdir -p $r/d $r/v
    t0=$(date +%s%N)
    AGENTEYES_REDIRECT_DATA=$r/d AGENTEYES_REDIRECT_VIDEOS=$r/v $D "$DLL" --help > $r/out 2>&1; rc=$?
    t1=$(date +%s%N); ms=$(( (t1-t0)/1000000 )); tot=$((tot+ms))
    if grep -q '/qa-svc/state/AgentEyes' $r/out; then a=ADDED; added=$((added+1)); else a=not-added; fi
    note=$(grep -o 'the \(AgentEyes \)\?service[^;]*' $r/out | head -1 | cut -c1-170)
    echo "$LABEL $1 run $i: exit $rc, ${ms} ms, service roots $a | $note"
  done
  echo "$LABEL $1 SUMMARY: mean $((tot/N)) ms over $N runs; service roots added in $added of $N"
}
echo "# $LABEL; dll embeds $(grep -aoE '1\.11\.11\+[0-9a-f]{40}' "$DLL" | head -1); 7882 before: $(probe && echo LISTENING || echo free)"
run nothing-listening
start() { python3 -I /svc.py 7882 "$@" /tmp/svc.log > /tmp/svc.out 2>&1 & SP=$!; for k in $(seq 1 50); do grep -q READY /tmp/svc.out && break; sleep 0.1; done; echo "# stand-in: $(cat /tmp/svc.out)"; grep -q READY /tmp/svc.out || { echo "INSTRUMENT FAILURE: stand-in not up"; exit 9; }; }
stop() { kill $SP; wait $SP 2>/dev/null; echo "# stand-in stopped; requests it saw: $(wc -l < /tmp/svc.log) ($(cut -d' ' -f2- /tmp/svc.log | sort | uniq -c | tr -s ' '))"; rm -f /tmp/svc.log; }
start answer 0;   run answering;            stop
start silent 0;     run accepts-never-answers; stop
start fullq 0;      run connect-never-completes; stop
start answer 1.5; run answers-after-1.5s;    stop
