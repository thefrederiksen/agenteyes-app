#!/usr/bin/env bash
# Issue #113: what a recording directory holds after a test - the manifest's capture fields, the
# post-processing stages, and ffprobe's duration + frame count for every MP4 in it (AC5, AC6).
#   inspect-recording.sh <recording dir> [wait-seconds]
# Waits up to wait-seconds (default 0) for post-processing to leave the directory (PendingMux cleared).
set -uo pipefail
d="${1:?usage: inspect-recording.sh <dir> [wait-seconds]}"
wait="${2:-0}"
[[ -f "$d/manifest.json" ]] || { echo "[FAIL] $d has no manifest.json - wrong directory?"; exit 1; }
end=$(( $(date +%s) + wait ))
until python3 -c "import json,sys; sys.exit(0 if json.load(open('$d/manifest.json')).get('PendingMux') is None else 1)" \
      || (( $(date +%s) >= end )); do sleep 2; done
echo "=== $d"
echo "files: $(ls "$d" | tr '\n' ' ')"
python3 - "$d/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
for k in ['DurationSeconds', 'CapturedSeconds', 'CaptureFault', 'CaptureFaultDetail', 'CaptureFaultDetectedUtc',
          'CaptureFaultElapsedSeconds', 'PendingMux']:
    print(f"  manifest.{k} = {json.dumps(m.get(k))}")
print("  manifest.PostProcessing =", {k: v.get('State') for k, v in m.get('PostProcessing', {}).items()})
PY
for f in "$d"/*.mp4; do
    echo "  ffprobe $(basename "$f"): $(ffprobe -v error -count_frames -show_entries format=duration:stream=codec_type,nb_read_frames -of compact=p=0 "$f" 2>&1 | tr '\n' ' ')"
done
