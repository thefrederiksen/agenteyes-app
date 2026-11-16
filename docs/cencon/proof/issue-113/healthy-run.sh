#!/usr/bin/env bash
# Issue #113 AC4: a healthy screen+mic recording of SECONDS (default 600) runs to its own end without
# the watchdog ending it. Polls /status every 10 s and FAILS on any poll that is not State=recording.
#   healthy-run.sh [seconds]     environment: MIC, OUT
set -uo pipefail
api=http://127.0.0.1:7882
dur="${1:-600}"
mic="${MIC:-Family 17h}"
out="${OUT:-./healthy-$(date -u +%H%M%S)}"
mkdir -p "$out"
now() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
field() { python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('$1'))"; }
[[ "$(curl -s $api/status | field State)" == "idle" ]] || { echo "[FAIL] the service is not idle"; exit 1; }
curl -s -H 'Content-Type: application/json' -d "{\"mode\":\"video\",\"screen\":1,\"source\":\"mic\",\"mic\":\"$mic\"}" "$api/record/start" > "$out/start.json"
echo "[$(now)] started $(field Dir < "$out/start.json")"
start=$(date +%s); bad=0; n=0; last=-1
while (( $(date +%s) - start < dur )); do
    sleep 10; n=$((n+1))
    body=$(curl -s $api/status); echo "$body" > "$out/poll-$(printf %03d $n).json"
    st=$(echo "$body" | field State); lv=$(echo "$body" | field CaptureLiveness); b=$(echo "$body" | field CaptureBytes)
    echo "[$(now)] poll $n: State=$st CaptureLiveness=$lv CaptureBytes=$b CaptureLastGrowthUtc=$(echo "$body" | field CaptureLastGrowthUtc) ElapsedSeconds=$(echo "$body" | field ElapsedSeconds)"
    [[ "$st" == "recording" ]] || { echo "[FAIL] poll $n: State=$st - the recording ended early"; bad=1; break; }
    [[ "$b" =~ ^[0-9]+$ ]] && (( b > last )) || { echo "[FAIL] poll $n: CaptureBytes did not advance ($last -> $b)"; bad=1; }
    last=$b
done
echo "[$(now)] stop: $(curl -s -H 'Content-Type: application/json' -d '{}' $api/record/stop | tr -d '\n')"
curl -s $api/status > "$out/after.json"
echo "polls=$n all-recording-and-advancing=$([[ $bad == 0 ]] && echo yes || echo NO) LastCaptureFault=$(field LastCaptureFault < "$out/after.json") LastStopFailed=$(field LastStopFailed < "$out/after.json")"
exit $bad
