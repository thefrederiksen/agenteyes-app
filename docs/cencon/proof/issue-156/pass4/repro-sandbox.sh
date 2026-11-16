#!/usr/bin/env bash
# Issue #156 pass 4 - the Review Gate's sandboxed-prober reproduction, BOTH ARMS IN ONE RUN.
# Derived from the gate's probe/live-orphan.sh (/private/tmp/agenteyes-gate-156-pass3/ on the gate Mac):
# the live arm is the gate's, unchanged in shape; the STALE arm is the positive control this pass adds.
#
# Usage: repro-sandbox.sh <agenteyes-binary> <scratch-dir>
#   live-plain     a LIVE writer (an ffmpeg this script started, still writing raw.mp4), dead owner, plain prober
#   live-sandbox   the same, probed from inside the gate's Seatbelt profile         <- the defect
#   live-sigdeny   the same, from the gate's profile PLUS signals denied outside the sandbox, so
#                  kill(pid, 0) fails EPERM too (pass 4's second measured case)
#   stale-plain    the writer has EXITED (a finished raw.mp4), dead owner, plain prober    <- positive control
#   stale-sandbox  the same, probed from inside the sandbox
# Every recording lives under <scratch-dir>. Every ffmpeg is started and ended by this script.
set -u
BIN="$1"; OUT="$2"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_ROLL_FORWARD=LatestMajor
PROFILE='(version 1)(allow default)(deny process-info*)(allow process-info* (target same-sandbox))'
SIGDENY="$PROFILE(deny signal)(allow signal (target same-sandbox))"
say() { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*"; }
DEAD=99999; ps -p $DEAD >/dev/null && { say "ABORT: pid $DEAD exists"; exit 3; }

start_of() { python3 -I -c "
import subprocess,datetime,sys
s=subprocess.check_output(['ps','-o','lstart=','-p',sys.argv[1]]).decode().strip()
t=datetime.datetime.strptime(s,'%a %b %d %H:%M:%S %Y').astimezone().astimezone(datetime.timezone.utc)
print(t.strftime('%Y-%m-%dT%H:%M:%S')+'.5Z')" "$1"; }

claim() {  # <rec> <writer-pid> <writer-start>
  cat > "$1/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-15T12:00:00.0000000Z",
  "MonitorIndex": 1, "MonitorName": "stand-in",
  "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }
JSON
  : > "$1/capture.lock"
  cat > "$1/capture.owner.json" <<JSON
{ "Host": "$(hostname -s)", "Pid": $DEAD, "ProcessStartUtc": "2026-11-15T12:00:00Z", "What": "capture session",
  "StartedUtc": "2026-11-15T12:00:00Z", "Writers": [ { "Label": "video", "Pid": $2, "StartUtc": "$3" } ] }
JSON
}

package() {  # <rec> <plain|sandbox>
  if [ "$2" = sandbox ]; then sandbox-exec -p "$PROFILE" "$BIN" package "$1" --no-frames > "$1.package.log" 2>&1
  elif [ "$2" = sigdeny ]; then sandbox-exec -p "$SIGDENY" "$BIN" package "$1" --no-frames > "$1.package.log" 2>&1
  else "$BIN" package "$1" --no-frames > "$1.package.log" 2>&1; fi
}

report() {  # <rec> <rc>
  say "  package exit $2"
  grep -iE 'UNDETERMINED|STILL RUNNING|refus|stale|CLEARED|zombie|error|failed|Attempt' "$1.package.log" | head -4 | cut -c1-300 | sed 's/^/    /'
  python3 -I - "$1/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1])); pp = (m.get("PostProcessing") or {}).get("mux") or {}
print("    PendingMux=%s  mux.State=%s  mux.Attempts=%s  AttemptedBy=%s" % (
    "pending" if m.get("PendingMux") else "cleared", pp.get("State"), pp.get("Attempts"), pp.get("AttemptedBy")))
PY
  say "  capture.owner.json: $( [ -f "$1/capture.owner.json" ] && echo PRESENT || echo ABSENT )  recording.mp4: $( [ -f "$1/recording.mp4" ] && echo "$(stat -f %z "$1/recording.mp4") bytes" || echo absent )"
}

say "binary: $BIN"
for ARM in plain sandbox sigdeny; do
  REC="$OUT/2026-11-15_12000${ARM:0:2}_live-$ARM"; rm -rf "$REC" "$REC.package.log"; mkdir -p "$REC"
  ffmpeg -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 \
    -f lavfi -i sine=frequency=440:sample_rate=48000 -t 90 \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -g 30 -c:a aac -y "$REC/raw.mp4" &
  W=$!; sleep 3
  claim "$REC" $W "$(start_of $W)"
  say "ARM live-$ARM: LIVE writer = my ffmpeg pid $W (uid $(ps -o uid= -p $W | xargs)), raw.mp4 $(stat -f %z "$REC/raw.mp4") bytes and growing"
  package "$REC" $ARM; rc=$?
  say "  writer still running after package: $(ps -p $W >/dev/null && echo YES || echo no)"
  report "$REC" $rc
  kill $W 2>/dev/null; wait $W 2>/dev/null; say "  my ffmpeg $W ended by me"
done

for ARM in plain sandbox; do
  REC="$OUT/2026-11-15_13000${ARM:0:1}_stale-$ARM"; rm -rf "$REC" "$REC.package.log"; mkdir -p "$REC"
  ffmpeg -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 \
    -f lavfi -i sine=frequency=440:sample_rate=48000 -t 4 \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -g 30 -c:a aac -y "$REC/raw.mp4" &
  W=$!; sleep 1; WS="$(start_of $W)"; wait $W
  claim "$REC" $W "$WS"
  say "ARM stale-$ARM: writer my ffmpeg pid $W has EXITED ($(ps -p $W >/dev/null && echo 'STILL PRESENT?!' || echo 'gone')), raw.mp4 $(stat -f %z "$REC/raw.mp4") bytes"
  package "$REC" $ARM; rc=$?
  report "$REC" $rc
done
say "done"
