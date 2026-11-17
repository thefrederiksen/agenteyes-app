#!/bin/bash
# QA #142 macOS rows 4-6 on main b0e6ae8: one take through the Control API at DEFAULT settings
# (no fps given - the product default applies). Records wall-clock start/stop as the EXPECTED take
# length, then waits for the recording to be finished ON DISK (manifest PostProcessing.mux.State or,
# for a take with no audio, recording.mp4 present and no *.tmp), with a hard timeout.
# usage: take.sh <name> <body-json> <seconds> <outfile>
set -u
name=$1; body=$2; secs=$3; out=$4
api=http://127.0.0.1:7882
ts() { date -u +%H:%M:%S.%3NZ 2>/dev/null || date -u +%H:%M:%SZ; }
now() { python3 -c 'import time; print(f"{time.time():.3f}")'; }
{
echo "$(date -u +%FT%TZ) TAKE $name body=$body planned=${secs}s"
echo "load: $(sysctl -n vm.loadavg)  cores: $(sysctl -n hw.ncpu)"
echo "service pid: $(pgrep -f 'agenteyes serve' | tr '\n' ' ')"
echo "state before: $(curl -s -m5 $api/status | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["State"])')"
t0=$(now)
resp=$(curl -s -m20 -X POST -H 'Content-Type: application/json' -d "$body" $api/record/start)
t1=$(now)
echo "start POST sent at epoch $t0, returned at $t1"
echo "start response: $(echo "$resp" | head -c 400)"
dir=$(curl -s -m5 $api/status | python3 -c 'import json,sys; print(json.load(sys.stdin)["Dir"])')
echo "dir: $dir"
python3 - "$secs" <<'EOF'
import sys, time; time.sleep(float(sys.argv[1]))
EOF
echo "status just before stop: $(curl -s -m5 $api/status | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["State"], d["ElapsedSeconds"], d["Source"])')"
t2=$(now)
sresp=$(curl -s -m120 -X POST -H 'Content-Type: application/json' -d '{}' $api/record/stop)
t3=$(now)
echo "stop POST sent at epoch $t2, returned at $t3"
echo "stop response: $(echo "$sresp" | head -c 600)"
python3 -c "print(f'wall-clock take (start POST returned -> stop POST sent): {$t2-$t1:.3f} s;  outer bound (start sent -> stop returned): {$t3-$t0:.3f} s')"
echo "status after stop: $(curl -s -m5 $api/status | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["State"], "LastStopFailed=",d["LastStopFailed"], "LastStopError=",d["LastStopError"])')"
# wait until finished ON DISK, not until /status says idle (#159)
python3 - "$dir" <<'EOF'
import json, os, sys, time
d = sys.argv[1]; t0 = time.time(); deadline = 300
while True:
    m = json.load(open(os.path.join(d, "manifest.json")))
    pp = m.get("PostProcessing") or {}
    mux = pp.get("mux")
    tmp = [f for f in os.listdir(d) if f.endswith(".tmp")]
    have = os.path.exists(os.path.join(d, "recording.mp4"))
    if (mux and mux.get("State") in ("done", "failed")) or (mux is None and have and not tmp and m.get("DurationSeconds")):
        break
    if time.time() - t0 > deadline:
        print(f"TIMEOUT after {deadline}s waiting for finish on disk: mux={mux} tmp={tmp} recording.mp4={have}"); break
    time.sleep(1)
print(f"finished on disk after {time.time()-t0:.1f}s of polling: mux={json.dumps(mux)}")
print("manifest DurationSeconds:", m.get("DurationSeconds"), " VideoFile:", m.get("VideoFile"))
print("FfmpegCommand:", m.get("FfmpegCommand"))
print("PostProcessing:", json.dumps({k: {kk: vv for kk, vv in v.items() if kk != "Error"} | ({"Error": v["Error"][:160]} if v.get("Error") else {}) for k, v in pp.items()}))
print("files:", sorted(os.listdir(d)))
EOF
echo "load after: $(sysctl -n vm.loadavg)"
echo "service pid after: $(pgrep -f 'agenteyes serve' | tr '\n' ' ')"
} 2>&1 | tee "$out"
