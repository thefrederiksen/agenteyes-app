#!/usr/bin/env bash
# Round two: poll /status every 0.25 s and log one line per poll with its UTC time, for the length
# of a fault run, so the whole window - healthy, verdict, named, idle - is on record, not sampled.
#   timeline.sh <outfile> <seconds>
out="$1"; secs="$2"; end=$(( $(date +%s) + secs ))
while (( $(date +%s) < end )); do
  t=$(date -u +%H:%M:%S.%3NZ)
  curl -s http://127.0.0.1:7882/status | python3 -c "
import json,sys
d=json.load(sys.stdin)
f=d.get('LastCaptureFault')
print('$t', 'State='+str(d['State']), 'Liveness='+str(d.get('CaptureLiveness')), 'Bytes='+str(d.get('CaptureBytes')),
      'SinceGrowth='+str(d.get('CaptureSecondsSinceGrowth')), 'LastStopFailed='+str(d.get('LastStopFailed')),
      'LastCaptureFault='+(repr(f[:60]) if f else 'None'))" >> "$out"
  sleep 0.25
done
