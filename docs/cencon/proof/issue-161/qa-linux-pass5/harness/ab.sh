#!/bin/bash
# INSIDE a --network none container. Interleaved A/B: pass 4 and pass 5 alternate run by run, so machine load hits both alike.
N=${1:-10}; D=/usr/lib/dotnet/dotnet; unset AGENTEYES_SERVICE_PROBE_PORT
one() { r=/tmp/ab-$RANDOM$RANDOM; mkdir -p $r/d $r/v; t0=$(date +%s%N); AGENTEYES_REDIRECT_DATA=$r/d AGENTEYES_REDIRECT_VIDEOS=$r/v $D $1 --help >$r/out 2>&1; t1=$(date +%s%N); echo $(( (t1-t0)/1000000 )); }
for shape in nothing-listening connect-never-completes; do
  [ $shape = connect-never-completes ] && { python3 -I /svc.py 7882 fullq 0 /dev/null >/tmp/fq.out 2>&1 & SP=$!; sleep 1; echo "# $(cat /tmp/fq.out)"; grep -q READY /tmp/fq.out || exit 9; }
  a=0; b=0
  for i in $(seq 1 $N); do x=$(one /p4/agenteyes.dll); y=$(one /p5/agenteyes.dll); a=$((a+x)); b=$((b+y)); echo "$shape pair $i: pass4 ${x} ms, pass5 ${y} ms"; done
  echo "$shape INTERLEAVED MEAN: pass4 $((a/N)) ms, pass5 $((b/N)) ms (n=$N each)"
  [ -n "$SP" ] && kill $SP
done
