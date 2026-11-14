#!/usr/bin/env bash
# QA driver for #113: screen+mic recording via the Control API, inject SIGSTOP or SIGKILL into the
# capture ffmpeg, measure time until /status State != recording. Saves every body with UTC time.
#   qa-fault.sh STOP|KILL <outdir>
set -uo pipefail
kind="$1"; out="$2"; mkdir -p "$out"
api=http://127.0.0.1:7882
now() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
f() { python3 -c "import json,sys; d=json.load(open('$1')); print(' '.join(f'{k}={d.get(k)}' for k in '$2'.split()))"; }
KEYS="State ElapsedSeconds Level CaptureLiveness CaptureFile CaptureBytes CaptureLastGrowthUtc CaptureSecondsSinceGrowth LastStopFailed LastCaptureFault"
poll() { local t; t=$(now); curl -s $api/status > "$out/$1.json"; echo "$t" > "$out/$1.time"; echo "[$t] $1: $(f "$out/$1.json" "$KEYS")"; }

[[ "$(curl -s $api/status | python3 -c 'import json,sys;print(json.load(sys.stdin)["State"])')" == idle ]] || { echo "[FAIL] not idle"; exit 1; }
poll before
echo "[$(now)] start: $(curl -s -H 'Content-Type: application/json' -d '{"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}' $api/record/start | tr -d '\n')" | tee "$out/start.txt"
sleep 3
pid=$(pgrep -f 'ffmpeg.*raw\.mp4' | head -1)
[[ -n "$pid" ]] || { echo "[FAIL] no capture ffmpeg"; exit 1; }
tr '\0' ' ' < /proc/$pid/cmdline > "$out/ffmpeg-cmdline.txt"; echo "capture pid=$pid ppid=$(ps -o ppid= -p $pid)"
sleep 7; poll healthy1
sleep 10; poll healthy2
if [[ "${ONGROWTH:-0}" == 1 ]]; then raw=$(python3 -c "import json;print(json.load(open('$out/healthy2.json'))['Dir'])")/raw.mp4; s0=$(stat -c %s "$raw"); while [[ $(stat -c %s "$raw") == "$s0" ]]; do sleep 0.01; done; echo "raw.mp4 grew $s0 -> $(stat -c %s "$raw"); injecting now"; fi
kill "-$kind" "$pid"; t0=$(date +%s.%N); echo "[$(now)] injected SIG$kind into $pid" | tee "$out/injected.txt"
took=0
while :; do
  st=$(curl -s $api/status | python3 -c 'import json,sys;print(json.load(sys.stdin)["State"])')
  el=$(echo "$(date +%s.%N) - $t0" | bc)
  [[ "$st" != recording ]] && break
  if (( took == 0 )) && (( $(echo "$el >= 10" | bc) )); then poll stalled; took=1; fi
  (( $(echo "$el > 120" | bc) )) && { echo "[FAIL] still recording after 120 s"; break; }
  sleep 0.25
done
echo "[$(now)] State left recording after ${el}s (now $st)" | tee "$out/latency.txt"
poll left
while :; do
  fl=$(curl -s $api/status | python3 -c 'import json,sys;print(json.load(sys.stdin).get("LastCaptureFault"))')
  el2=$(echo "$(date +%s.%N) - $t0" | bc)
  [[ "$fl" != None ]] && break
  (( $(echo "$el2 > 150" | bc) )) && { echo "[FAIL] fault never named"; break; }
  sleep 0.2
done
echo "[$(now)] /status first NAMED the fault after ${el2}s: $fl" | tee -a "$out/latency.txt"
poll named
for _ in $(seq 1 90); do [[ "$(curl -s $api/status | python3 -c 'import json,sys;print(json.load(sys.stdin)["State"])')" == idle ]] && break; sleep 1; done
poll after
if kill -0 "$pid" 2>/dev/null; then echo "[FAIL] capture ffmpeg $pid still alive after idle - releasing"; kill -CONT "$pid"; kill -9 "$pid"; else echo "capture ffmpeg $pid is gone"; fi
