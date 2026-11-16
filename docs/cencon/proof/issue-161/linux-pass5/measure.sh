#!/bin/bash
# Runs INSIDE the container: --network none, so 127.0.0.1:7882 is the container's own (free) loopback port.
DLL="$1"; N="${2:-10}"
D=/usr/lib/dotnet/dotnet
up() { timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/7882" 2>/dev/null; }
run() { # $1 label
  local tot=0 i
  for i in $(seq 1 $N); do
    r=/tmp/run-$1-$i; mkdir -p $r/d $r/v
    t0=$(date +%s%N)
    AGENTEYES_REDIRECT_DATA=$r/d AGENTEYES_REDIRECT_VIDEOS=$r/v $D "$DLL" > $r/out 2>&1; rc=$?
    t1=$(date +%s%N); ms=$(( (t1-t0)/1000000 )); tot=$((tot+ms))
    echo "$1 run $i: exit $rc, ${ms} ms | $(grep -o 'the \(AgentEyes \)\?service[^;]*' $r/out | head -1)"
  done
  echo "$1 MEAN: $((tot/N)) ms over $N runs"
}
echo "# 7882 before: $(up && echo LISTENING || echo free)"
run no-service
# The stand-in plays the person's post-#161 service on the container's own loopback: GET /status names a
# library (in this container's tmpfs). The owner's library is not mounted in this container at all.
/usr/bin/python3.12 /standin.py > /tmp/svc.out 2>&1 &
SP=$!
for i in $(seq 1 100); do up && break; sleep 0.1; done
echo "# stand-in service pid $SP on 7882: $(up && echo LISTENING || { echo NOT UP; cat /tmp/svc.out; })"
run service
kill $SP; wait $SP 2>/dev/null
echo "# stand-in stopped; 7882: $(up && echo LISTENING || echo free)"
/usr/bin/python3.12 /silent.py > /tmp/silent.out 2>&1 &
SP=$!
for i in $(seq 1 100); do up && break; sleep 0.1; done
echo "# silent stand-in pid $SP on 7882 (accepts, never answers): $(up && echo LISTENING || { echo NOT UP; cat /tmp/silent.out; })"
run silent
kill $SP; wait $SP 2>/dev/null
echo "# silent stand-in stopped; 7882: $(up && echo LISTENING || echo free)"
/usr/bin/python3.12 /fullq.py > /tmp/fullq.out 2>&1 &
SP=$!
for i in $(seq 1 100); do grep -q filled /tmp/fullq.out && break; sleep 0.1; done
echo "# full-queue listener pid $SP on 7882 (connect never completes): $(cat /tmp/fullq.out)"
run connect-hangs
kill $SP; wait $SP 2>/dev/null
