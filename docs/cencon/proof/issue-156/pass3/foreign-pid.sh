#!/usr/bin/env bash
# Issue #156 pass 3 - the gate's defect on the REAL binary: a crashed recording whose recorded capture
# writer pid is now held by ANOTHER USER's process.
#
# Usage:  foreign-pid.sh <agenteyes-binary> <scratch-dir>
#
# What a crash followed by a reboot leaves, built by hand: a START manifest with a pending mux, a
# complete raw.mp4 (an ffmpeg lavfi source with the product's output options - this Mac's shell has no
# Screen Recording grant), a FREE capture.lock, and a same-host capture.owner.json whose owner pid does
# not exist and whose one writer is pid 1 - root-owned, always present, with a start time the record
# cannot match. Then a second process runs 'agenteyes package <dir> --no-frames'.
#
# Expected on a pre-fix binary: the package fails with the framework's Win32Exception message.
# Expected on the fix: the stale claim is cleared, the recording is muxed (PendingMux cleared,
# Attempts 1) and capture.owner.json is gone.
#
# Everything lives under <scratch-dir>. It starts one ffmpeg of its own and signals nothing.
# NOTE (#161): an isolated HOME does not redirect the data root on macOS, so the CLI's own log lines
# land in the real ~/Library/Application Support/AgentEyes/logs. It never reads or writes a recording
# outside <scratch-dir>.
set -u
BIN="$1"; OUT="$2"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}"   # no .NET 8 here: runs on .NET 10
REC="$OUT/2026-11-15_120000_video"
rm -rf "$REC"; mkdir -p "$REC"
say() { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*"; }

say "binary: $BIN (built from: ${BUILT_FROM:-unstated})"
say "pid 1: $(ps -o pid=,user=,lstart= -p 1 | xargs)   this user: $(id -un)"

ffmpeg -hide_banner -nostats -loglevel error -f lavfi -i testsrc2=size=1280x720:rate=30 \
  -f lavfi -i sine=frequency=440:sample_rate=48000 -t 4 \
  -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 -g 30 -c:a aac -b:a 128k -y "$REC/raw.mp4"
say "raw.mp4 written: $(stat -f %z "$REC/raw.mp4") bytes"

cat > "$REC/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-15T12:00:00.0000000Z",
  "MonitorIndex": 1, "MonitorName": "stand-in",
  "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }
JSON
: > "$REC/capture.lock"
DEAD=99999   # the top of macOS's pid range; checked free on the next line
ps -p $DEAD >/dev/null && { say "ABORT: pid $DEAD exists"; exit 3; }
cat > "$REC/capture.owner.json" <<JSON
{ "Host": "$(hostname -s)", "Pid": $DEAD, "ProcessStartUtc": "2026-11-15T12:00:00Z", "What": "capture session",
  "StartedUtc": "2026-11-15T12:00:00Z", "Writers": [ { "Label": "video", "Pid": 1, "StartUtc": "2026-11-15T12:00:01Z" } ] }
JSON
say "crashed recording: lock free, owner pid $DEAD (gone), writer pid 1 (another user's)"

say "SECOND PROCESS: agenteyes package --no-frames"
"$BIN" package "$REC" --no-frames > "$OUT/package.log" 2>&1; rc=$?
say "  exit $rc"
grep -iE 'Win32Exception|Unable to retrieve|refusing|error|failed|stale|CLEARED|recording.mp4' "$OUT/package.log" | head -6 | cut -c1-300 | sed 's/^/    /'
python3 - "$REC/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1])); pp = (m.get("PostProcessing") or {}).get("mux") or {}
print("    PendingMux=%s  mux.State=%s  mux.Attempts=%s  DurationSeconds=%s" % (
    "pending" if m.get("PendingMux") else "cleared", pp.get("State"), pp.get("Attempts"), m.get("DurationSeconds")))
PY
say "  recording.mp4: $( [ -f "$REC/recording.mp4" ] && stat -f %z "$REC/recording.mp4" || echo absent )   capture.owner.json: $( [ -f "$REC/capture.owner.json" ] && echo PRESENT || echo absent )"
if [ -f "$REC/recording.mp4" ]; then
  say "  decode: $(ffprobe -v error -show_entries format=duration:stream=codec_type -of csv=p=0 "$REC/recording.mp4" | tr '\n' ' ')"
  ffmpeg -v error -i "$REC/recording.mp4" -f null - && say "  full decode exit 0"
fi
