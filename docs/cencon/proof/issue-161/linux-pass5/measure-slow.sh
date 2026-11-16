#!/bin/bash
# Runs INSIDE the container (--network none): a listening-but-slow service on the container's own 7882.
DLL="$1"; N="${2:-10}"; D=/usr/lib/dotnet/dotnet
/usr/bin/python3.12 /slow.py > /tmp/slow.out 2>&1 &
SP=$!; sleep 1
tot=0
for i in $(seq 1 $N); do
  r=/tmp/run-slow-$i; mkdir -p $r/d $r/v
  t0=$(date +%s%N)
  AGENTEYES_REDIRECT_DATA=$r/d AGENTEYES_REDIRECT_VIDEOS=$r/v $D "$DLL" > $r/out 2>&1; rc=$?
  t1=$(date +%s%N); ms=$(( (t1-t0)/1000000 )); tot=$((tot+ms))
  echo "slow-service run $i: exit $rc, ${ms} ms | $(grep -o 'the \(AgentEyes \)\?service[^;]*' $r/out | head -1)"
done
echo "slow-service MEAN: $((tot/N)) ms over $N runs"
kill $SP
