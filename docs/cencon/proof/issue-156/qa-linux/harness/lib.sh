# Issue #156 Linux QA harness library. Sourced by the run scripts.
# Every agenteyes process runs under a scratch HOME/XDG root. A service is SIGKILLed at once if its
# own "recordings ->" line is not inside that root. Only pids started here are ever signalled.
set -u
SP=${SCRATCH:?set SCRATCH to an empty scratch directory}
WT=$SP/wt
declare -A BINS=(
  [fix]=/home/soren/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
  [fixfrag]=$WT/fixfrag/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
  [main]=$WT/main/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
  [mainfrag]=$WT/mainfrag/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
)
FOREIGN_FFMPEG=72110   # go2rtc camera wall, another mission's. Never touched.

# run_env <run-root>: the scratch environment for one run
run_env() {
  local R=$1
  mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos"
  export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
  export DISPLAY=:0 XAUTHORITY=/run/user/1000/gdm/Xauthority XDG_RUNTIME_DIR=/run/user/1000
  export RUNROOT=$R LIB=$R/home/Videos/AgentEyes LOGDIR=$R/home/.local/share/AgentEyes/logs
}

ts() { date -u +%H:%M:%S.%3N; }
say() { printf '%s %s\n' "$(ts)" "$*" | tee -a "$RUNROOT/transcript.txt"; }
sz() { if [ -f "$1" ]; then stat -c %s "$1"; else echo absent; fi; }
alive() { kill -0 "$1" 2>/dev/null && echo yes || echo NO; }
pstart() { ps -o lstart= -p "$1" 2>/dev/null | xargs; }

# start_serve <bintag> <port> <name>: sets SERVE_PID
start_serve() {
  local B=${BINS[$1]} port=$2 name=$3
  "$B" serve --port "$port" > "$RUNROOT/$name.stdout" 2>&1 &
  SERVE_PID=$!
  local root=""
  for _ in $(seq 1 60); do
    root=$(grep -m1 "recordings ->" "$RUNROOT/$name.stdout" 2>/dev/null | sed 's/.*recordings -> //')
    [ -n "$root" ] && break
    sleep 0.25
  done
  case "$root" in
    "$RUNROOT"/*) say "[$name] $1 serve pid $SERVE_PID port $port; recordings root inside scratch: $root" ;;
    *) kill -9 "$SERVE_PID"; say "ABORT: [$name] recordings root '$root' is NOT inside $RUNROOT - SIGKILLed pid $SERVE_PID"; exit 9 ;;
  esac
}

api() { local body=${4:-}; [ "$2" = POST ] && [ -z "$body" ] && body='{}'; curl -s -m 30 -X "$2" "http://127.0.0.1:$1$3" ${body:+-H 'Content-Type: application/json' -d "$body"}; }

# The newest recording dir in this run's library
newest_dir() { ls -1dt "$LIB"/*/ 2>/dev/null | head -1 | sed 's:/$::'; }

# One-line state of a recording directory
state() {
  local d=$1
  python3 - "$d" <<'PY'
import json, os, sys
d = sys.argv[1]
def s(n):
    p = os.path.join(d, n)
    return str(os.path.getsize(p)) if os.path.exists(p) else "absent"
m = {}
try: m = json.load(open(os.path.join(d, "manifest.json")))
except Exception as e: m = {"_err": str(e)}
pp = (m.get("PostProcessing") or {}).get("mux")
mux = "none" if not pp else "State=%s Attempts=%s AttemptedBy=%s Error=%s" % (pp.get("State"), pp.get("Attempts"), pp.get("AttemptedBy"), (pp.get("Error") or "")[:160])
print("raw.mp4=%s recording.mp4=%s recording.original.mp4=%s capture.lock=%s capture.owner.json=%s | PendingMux=%s | mux: %s | DurationSeconds=%s" % (
    s("raw.mp4"), s("recording.mp4"), s("recording.original.mp4"), s("capture.lock"), s("capture.owner.json"),
    "pending" if m.get("PendingMux") else "cleared", mux, m.get("DurationSeconds")))
PY
}

# wait_mux <dir> <timeout-s>: follow manifest PostProcessing.mux.State (never /status, #159)
wait_mux() {
  local d=$1 t=$2
  for _ in $(seq 1 "$t"); do
    local st
    st=$(python3 -c "
import json,sys
m=json.load(open('$d/manifest.json'))
pp=(m.get('PostProcessing') or {}).get('mux') or {}
print(('pending' if m.get('PendingMux') else 'cleared')+' '+str(pp.get('State')))" 2>/dev/null)
    case "$st" in "cleared Done"|"cleared done"|*Failed*|*failed*) say "mux settled: $st"; return 0 ;; esac
    sleep 1
  done
  say "mux NOT settled after ${t}s: $st"; return 1
}

# package <bintag> <dir> <tag>: a SECOND process drives the finalize directly
package() {
  local B=${BINS[$1]} d=$2 tag=$3
  say "SECOND PROCESS ($1): agenteyes package $(basename "$d") --no-frames  [$tag]"
  "$B" package "$d" --no-frames > "$RUNROOT/package.$tag.log" 2>&1
  local rc=$?
  say "  exit $rc: $(grep -m1 -iE 'refusing|error|could not|failed|\[ok\]|packaged' "$RUNROOT/package.$tag.log" | cut -c1-420)"
  say "  $(state "$d")"
}

# lockinfo <dir>: who holds capture.lock, measured by the kernel, not by AgentEyes
lockinfo() {
  local f=$1/capture.lock
  [ -f "$f" ] || { say "  capture.lock absent"; return; }
  local ino; ino=$(stat -c %i "$f")
  local line; line=$(grep -E ":$ino " /proc/locks | head -3 | tr '\n' ';')
  say "  /proc/locks for capture.lock inode $ino: ${line:-NONE}"
  if flock -n -s "$f" true 2>/dev/null; then say "  independent probe: flock -n -s capture.lock SUCCEEDED (lock free)"; else say "  independent probe: flock -n -s capture.lock REFUSED (exclusive lock held)"; fi
}

decode_check() {
  local f=$1
  [ -f "$f" ] || { say "  decode: $f absent"; return; }
  local dur frames err
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f")
  frames=$(ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 "$f")
  ffmpeg -v error -i "$f" -f null - > "$RUNROOT/decode.$(basename "$f").err" 2>&1; err=$?
  say "  decode $(basename "$f"): duration=${dur}s video_frames=$frames full-decode exit=$err errors=$(wc -l < "$RUNROOT/decode.$(basename "$f").err") streams=$(ffprobe -v error -show_entries stream=codec_type -of csv=p=0 "$f" | tr '\n' ',')"
}

foreign_check() { say "  foreign go2rtc ffmpeg $FOREIGN_FFMPEG alive: $(alive $FOREIGN_FFMPEG)"; }
MIC="Family 17h"
START_BODY='{"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}'
# cleanup trap: signal only pids this script recorded
MYPIDS=()
cleanup() { for p in "${MYPIDS[@]}"; do kill -TERM "$p" 2>/dev/null; done; for _ in $(seq 1 90); do local any=0; for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && any=1; done; [ $any = 0 ] && break; sleep 1; done; for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && { echo "cleanup: SIGKILL $p"; kill -9 "$p"; }; done; }
