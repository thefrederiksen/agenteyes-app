#!/bin/bash
# Issue #156 pass 2 - criterion 4's control arm, REAL capture, plain MP4 container.
#   S1 captures (x11grab + pulse via POST /record/start) -> S1 is SIGKILLed mid-capture -> its ffmpeg
#   orphan keeps writing -> "another host" is refused -> S2 starts: the reaper ends the orphan,
#   the repair pass recovers the recording -> "another host" then accepts it (package + its own
#   service's repair pass).
# "Another host" = a process whose gethostname()/uname() report "otherhost" (LD_PRELOAD shim) AND
# whose AgentEyes data root (so its host-id file) is separate, on the SAME library. Not two machines.
# Usage: c4-control.sh <fix|prefix> <portbase>
# Only pids this script starts are ever signalled. Foreign ffmpeg is re-identified by command line.
set -u
TAG=$1; PB=$2
SP=${SP:?set SP to a scratch directory holding wt-d245037/ and shim/}
case $TAG in
  fix)    BIN=/home/<user>/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  prefix) BIN=$SP/wt-d245037/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  *) echo "usage"; exit 2 ;;
esac
SHIM=$SP/shim/fakehost.so
R=$SP/runs/c4-$TAG-$(date -u +%H%M%S)
mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes" "$R/other/.local/share"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
export DISPLAY=:0 XAUTHORITY=/run/user/1000/gdm/Xauthority XDG_RUNTIME_DIR=/run/user/1000
LIB=$R/home/Videos/AgentEyes; LOG=$R/home/.local/share/AgentEyes/logs
OTHER_ENV=(env LD_PRELOAD=$SHIM FAKE_HOSTNAME=otherhost XDG_DATA_HOME=$R/other/.local/share)
T=$R/transcript.txt
ts() { date -u +%H:%M:%S.%3N; }
say() { printf '%s %s\n' "$(ts)" "$*" | tee -a "$T"; }
sz() { [ -f "$1" ] && stat -c %s "$1" || echo absent; }
alive() { kill -0 "$1" 2>/dev/null && echo yes || echo NO; }
MYPIDS=()
cleanup() { for p in "${MYPIDS[@]}"; do kill -TERM "$p" 2>/dev/null; done; for _ in $(seq 1 30); do a=0; for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && a=1; done; [ $a = 0 ] && break; sleep 1; done; for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && { say "cleanup: SIGKILL $p"; kill -9 "$p"; }; done; }
trap cleanup EXIT

foreign() {  # re-identify the go2rtc ffmpeg by its command line, every time; never signalled
  local p; p=$(pgrep -f 'go2rtc/ffmpeg' | head -1)
  say "  foreign go2rtc ffmpeg (by command line): pid ${p:-NONE} alive=$( [ -n "$p" ] && alive "$p" || echo n/a)"
}
state() {
  python3 - "$1" <<'PY'
import json, os, sys
d = sys.argv[1]
def s(n):
    p = os.path.join(d, n); return str(os.path.getsize(p)) if os.path.exists(p) else "absent"
try: m = json.load(open(os.path.join(d, "manifest.json")))
except Exception as e: m = {"_err": str(e)}
pp = (m.get("PostProcessing") or {}).get("mux")
mux = "none" if not pp else "State=%s Attempts=%s AttemptedBy=%s Error=%s" % (pp.get("State"), pp.get("Attempts"), pp.get("AttemptedBy"), (pp.get("Error") or "")[:160])
print("raw.mp4=%s recording.mp4=%s capture.owner.json=%s | PendingMux=%s | mux: %s | DurationSeconds=%s | CaptureOwner=%s" % (
    s("raw.mp4"), s("recording.mp4"), s("capture.owner.json"), "pending" if m.get("PendingMux") else "cleared", mux,
    m.get("DurationSeconds"), json.dumps(m.get("CaptureOwner"))))
PY
}
start_serve() {  # <port> <name> [other]  -> SERVE_PID
  local port=$1 name=$2 pre=()
  [ "${3:-}" = other ] && pre=("${OTHER_ENV[@]}")
  "${pre[@]}" "$BIN" serve --port "$port" > "$R/$name.stdout" 2>&1 &
  SERVE_PID=$!; MYPIDS+=($SERVE_PID)
  local root=""
  for _ in $(seq 1 60); do root=$(grep -m1 "recordings ->" "$R/$name.stdout" 2>/dev/null | sed 's/.*recordings -> //'); [ -n "$root" ] && break; sleep 0.25; done
  case "$root" in
    "$R"/*) say "[$name] serve pid $SERVE_PID port $port; recordings root inside scratch: $root" ;;
    *) kill -9 "$SERVE_PID"; say "ABORT: [$name] recordings root '$root' is NOT inside $R - SIGKILLed pid $SERVE_PID"; exit 9 ;;
  esac
}
api() { local body=${3:-}; [ "$2" = POST ] && [ -z "$body" ] && body='{}'; curl -s -m 30 -X "$2" "http://127.0.0.1:$1$3" ${body:+-H 'Content-Type: application/json' -d "$body"}; }
apipost() { curl -s -m 30 -X POST "http://127.0.0.1:$1$2" -H 'Content-Type: application/json' -d "$3"; }

say "=== issue #156 pass 2, criterion 4 control arm: binary=$TAG ($BIN), scratch=$R"
say "binary version: $(strings "$(dirname "$BIN")/agenteyes.dll" | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}')"
foreign

# ---- ISOLATION PRE-CHECK, before ANY service exists (issue #161) ----
pc=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
pco=$("${OTHER_ENV[@]}" "$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
case "$pc" in "$R"/*) ;; *) say "ABORT: pre-check recordings root '$pc' is NOT inside $R"; exit 9 ;; esac
case "$pco" in "$R"/*) ;; *) say "ABORT: other-host pre-check root '$pco' is NOT inside $R"; exit 9 ;; esac
[ -d "$LOG" ] || { say "ABORT: no log dir at $LOG - the data root is not where this script believes"; exit 9; }
[ -d "$R/other/.local/share/AgentEyes" ] || { say "ABORT: the other host's data root did not land in scratch"; exit 9; }
say "isolation pre-check (report-only housekeep, no service): this-host root=$pc, other-host root=$pco, this-host data root=$R/home/.local/share/AgentEyes, other-host data root=$R/other/.local/share/AgentEyes"

# ---- S1 captures for real ----
start_serve $PB s1; S1=$SERVE_PID
apipost $PB /record/start '{"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}' > "$R/start.json"
D=$(python3 -c "import json;print(json.load(open('$R/start.json'))['Dir'])")
say "S1 pid $S1 capturing into $(basename "$D")"
sleep 45
W=$(python3 -c "import json;o=json.load(open('$D/capture.owner.json'));print(o['Writers'][0]['Pid'])")
grep -q "$D" /proc/$W/cmdline 2>/dev/null && say "writer pid $W confirmed by command line to write into $(basename "$D")" || { say "ABORT: writer $W does not write into $D"; exit 9; }
say "  owner record: $(tr -d '\n ' < "$D/capture.owner.json" | cut -c1-300)"
say "  $(state "$D")"

say "SIGKILL S1 pid $S1 mid-capture"
kill -9 $S1; wait $S1 2>/dev/null
a=$(sz "$D/raw.mp4"); sleep 4; b=$(sz "$D/raw.mp4")
say "after S1's death: writer $W alive=$(alive $W) ppid=$(ps -o ppid= -p $W | xargs) raw.mp4 $a -> $b bytes in 4 s"

say "OTHER HOST package before recovery (must decline, naming the writer it cannot check):"
"${OTHER_ENV[@]}" "$BIN" package "$D" --no-frames > "$R/other-package-1.log" 2>&1; rc=$?
say "  exit $rc: $(grep -m1 -iE 'refusing|error|failed' "$R/other-package-1.log" | cut -c1-400)"
say "  $(state "$D")"

# ---- S2: the next service start on this host ----
start_serve $((PB+1)) s2; S2=$SERVE_PID
for _ in $(seq 1 60); do kill -0 $W 2>/dev/null || break; sleep 0.25; done
say "writer $W alive after S2's start-up reap: $(alive $W); raw.mp4 now $(sz "$D/raw.mp4") bytes"
grep -h "ReapOrphans: pid\|KillTree\|RequestGracefulExit" "$LOG"/*.log 2>/dev/null | grep -v "^\s*$" | tail -4 | while read -r l; do say "  log: ${l:0:330}"; done
say "  raw.mp4 probe: $(ffprobe -v error -show_entries format=duration -of csv=p=0 "$D/raw.mp4" 2>&1 | tail -1 | cut -c1-160)"

for _ in $(seq 1 120); do
  st=$(python3 -c "
import json; m=json.load(open('$D/manifest.json')); pp=(m.get('PostProcessing') or {}).get('mux') or {}
print(('pending' if m.get('PendingMux') else 'cleared')+' '+str(pp.get('State')))" 2>/dev/null)
  case "$st" in "cleared Done"|"cleared done"|*ailed*) break ;; esac; sleep 1
done
say "recovery by S2: $st"
say "  $(state "$D")"
if [ -f "$D/recording.mp4" ]; then
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$D/recording.mp4")
  ffmpeg -v error -i "$D/recording.mp4" -f null - > "$R/decode.err" 2>&1; de=$?
  say "  decode recording.mp4: duration=${dur}s full-decode exit=$de errors=$(wc -l < "$R/decode.err") streams=$(ffprobe -v error -show_entries stream=codec_type -of csv=p=0 "$D/recording.mp4" | tr '\n' ',')"
fi
grep -h "ClearStaleClaim" "$LOG"/*.log 2>/dev/null | grep "$(basename "$D")" | tail -3 | while read -r l; do say "  log: ${l:0:330}"; done
say "  S1 as recorded by the kernel earlier: pid $S1; manifest CaptureOwner above must name pid $S1 on $(hostname)"

say "OTHER HOST package after recovery (must get past the claim):"
"${OTHER_ENV[@]}" "$BIN" package "$D" --no-frames > "$R/other-package-2.log" 2>&1; rc=$?
say "  exit $rc: $(grep -m1 -iE 'refusing|error|failed|\[ok\]' "$R/other-package-2.log" | cut -c1-400)"
say "  $(state "$D")"

say "OTHER HOST service (S3) - its own repair pass over the same library:"
start_serve $((PB+2)) s3 other; S3=$SERVE_PID
sleep 32
grep -h "$(basename "$D")" "$R"/other/.local/share/AgentEyes/logs/*.log 2>/dev/null | grep -E "FindUnfinished|Execute|Thumbnail|cannot be checked|DECLINED" | tail -6 | while read -r l; do say "  S3 log: ${l:0:330}"; done
say "  S3 reports host: $(grep -h -m1 -oE 'from otherhost|on otherhost' "$R"/other/.local/share/AgentEyes/logs/*.log "$R/other-package-1.log" 2>/dev/null | head -1)"
say "  $(state "$D")"
foreign
say "=== done; transcript $T"
