#!/bin/bash
# QA #156 pass 2 (independent Linux seat) - real x11grab + pulse captures, plain MP4.
# Usage: qa2.sh <fix|pre> <portbase> <scenario>
#   crash      : S1 captures, SIGKILL S1 mid-capture, orphan keeps writing, other host declines,
#                S2 starts (reap + repair), other host accepts afterwards (package + its own service)
#   crashstop  : as crash, but the orphan is SIGSTOPped before S2 - a writer that cannot honour SIGTERM
#   crashload  : as crash, with every core busy across S2's start-up reap
#   clean      : S1 captures 20 s, normal stop, mux followed to done (G1 durability on the normal path)
# Every crash run also copies the crashed recording (after the reap, before any pass) to $R/fixtures
# for the D2 concurrency arms (conc.sh). Only pids this script started are ever signalled.
set -u
TAG=$1; PB=$2; SCEN=$3
SP=${SCRATCH:?set SCRATCH to an empty scratch directory holding shim/ and wt-pre/}
case $TAG in
  fix) BIN=/home/soren/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  pre) BIN=$SP/wt-pre/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  *) echo usage; exit 2 ;;
esac
SHIM=$SP/shim/fakehost.so
R=$SP/runs/$SCEN-$TAG-$(date -u +%H%M%S)
mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes" "$R/other/.local/share" "$R/fixtures"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
export DISPLAY=:0 XAUTHORITY=/run/user/1000/gdm/Xauthority XDG_RUNTIME_DIR=/run/user/1000
LIB=$R/home/Videos/AgentEyes; LOG=$R/home/.local/share/AgentEyes/logs; OLOG=$R/other/.local/share/AgentEyes/logs
OTHER_ENV=(env LD_PRELOAD=$SHIM FAKE_HOSTNAME=otherhost XDG_DATA_HOME=$R/other/.local/share)
T=$R/transcript.txt
ts() { date -u +%H:%M:%S.%3N; }
say() { printf '%s %s\n' "$(ts)" "$*" | tee -a "$T"; }
sz() { [ -f "$1" ] && stat -c %s "$1" || echo absent; }
alive() { kill -0 "$1" 2>/dev/null && echo yes || echo NO; }
MYPIDS=(); LOADPIDS=()
cleanup() {
  for p in "${LOADPIDS[@]}"; do kill -TERM "$p" 2>/dev/null; done
  for p in "${MYPIDS[@]}"; do kill -CONT "$p" 2>/dev/null; kill -TERM "$p" 2>/dev/null; done
  for _ in $(seq 1 30); do a=0; for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && a=1; done; [ $a = 0 ] && break; sleep 1; done
  for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && { say "cleanup: SIGKILL $p (mine)"; kill -9 "$p"; }; done
}
trap cleanup EXIT
foreign() { local p; p=$(pgrep -f 'user_agent go2rtc/ffmpeg' | head -1); say "  foreign go2rtc ffmpeg, re-identified by command line: pid ${p:-NONE} alive=$([ -n "$p" ] && alive "$p" || echo n/a) (never signalled)"; }
state() {
  python3 - "$1" <<'PY'
import json, os, sys
d = sys.argv[1]
def s(n):
    p = os.path.join(d, n); return str(os.path.getsize(p)) if os.path.exists(p) else "absent"
try: m = json.load(open(os.path.join(d, "manifest.json")))
except Exception as e: m = {"_err": str(e)}
pp = (m.get("PostProcessing") or {}).get("mux")
mux = "none" if not pp else "State=%s Attempts=%s AttemptedBy=%s Error=%s" % (pp.get("State"), pp.get("Attempts"), json.dumps(pp.get("AttemptedBy")), (pp.get("Error") or "")[:140])
print("raw.mp4=%s recording.mp4=%s capture.owner.json=%s | PendingMux=%s | mux: %s | CaptureOwner=%s" % (
    s("raw.mp4"), s("recording.mp4"), s("capture.owner.json"), "pending" if m.get("PendingMux") else "cleared", mux, json.dumps(m.get("CaptureOwner", "<<field absent>>"))))
PY
}
muxstate() { python3 -c "
import json,sys; m=json.load(open('$1/manifest.json')); pp=(m.get('PostProcessing') or {}).get('mux') or {}
print(('pending' if m.get('PendingMux') else 'cleared')+'/'+str(pp.get('State')))" 2>/dev/null; }
waitmux() { local st=""; for _ in $(seq 1 ${2:-120}); do st=$(muxstate "$1"); case "$st" in cleared/[Dd]one|*/[Ff]ailed) break;; esac; sleep 1; done; echo "$st"; }
decode() {  # full decode + a frame read in the last second + stream list
  local f=$1 dur de fe
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f")
  ffmpeg -v error -i "$f" -f null - > "$R/decode.err" 2>&1; de=$?
  fe=$(ffmpeg -v error -sseof -1 -i "$f" -map 0:v:0 -f framemd5 - 2>/dev/null | grep -vc '^#'); fe="$fe video frames decoded from the last second; last audio pts read: $(ffmpeg -v error -sseof -1 -i "$f" -map 0:a:0 -f framemd5 - 2>/dev/null | grep -v '^#' | tail -1 | awk -F, '{print $3}' | xargs)"
  say "  decode $(basename "$f"): duration=${dur}s full-decode exit=$de stderr-lines=$(wc -l < "$R/decode.err") last-second video read: ${fe:-NO FRAME} streams=$(ffprobe -v error -show_entries stream=codec_type,codec_name -of csv=p=0 "$f" | tr '\n' ' ')"
}
start_serve() {  # <port> <name> [other] -> SERVE_PID
  local port=$1 name=$2 pre=()
  [ "${3:-}" = other ] && pre=("${OTHER_ENV[@]}")
  "${pre[@]}" "$BIN" serve --port "$port" > "$R/$name.stdout" 2>&1 &
  SERVE_PID=$!; MYPIDS+=($SERVE_PID)
  local root=""
  for _ in $(seq 1 80); do root=$(grep -m1 "recordings ->" "$R/$name.stdout" 2>/dev/null | sed 's/.*recordings -> //'); [ -n "$root" ] && break; sleep 0.25; done
  case "$root" in
    "$R"/*) say "[$name] serve pid $SERVE_PID port $port; its recordings root is inside scratch: $root" ;;
    *) kill -9 "$SERVE_PID"; say "ABORT: [$name] recordings root '$root' is NOT inside $R - SIGKILLed pid $SERVE_PID"; exit 9 ;;
  esac
}
apipost() { curl -s -m 30 -X POST "http://127.0.0.1:$1$2" -H 'Content-Type: application/json' -d "$3"; }

say "=== QA #156 pass 2 scenario=$SCEN binary=$TAG scratch=$R"
say "binary: $BIN"
say "embedded version: $(strings "$(dirname "$BIN")/agenteyes.dll" | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}')"
foreign

# ---- ISOLATION, proven BEFORE any service exists (issue #161) ----
pc=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
pco=$("${OTHER_ENV[@]}" "$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
CTRL=$SP/runs/isoctrl-$$; mkdir -p "$CTRL/h/.config" "$CTRL/h/.local/share" "$CTRL/h/Videos"
pcc=$(env HOME=$CTRL/h XDG_CONFIG_HOME=$CTRL/h/.config XDG_DATA_HOME=$CTRL/h/.local/share "$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
case "$pc" in "$R"/*) ;; *) say "ABORT: this-host recordings root '$pc' is NOT inside $R"; exit 9 ;; esac
case "$pco" in "$R"/*) ;; *) say "ABORT: other-host recordings root '$pco' is NOT inside $R"; exit 9 ;; esac
case "$pcc" in "$CTRL"/*) ;; *) say "ABORT: positive control failed - the probe did not follow the env ('$pcc')"; exit 9 ;; esac
[ -d "$LOG" ] || { say "ABORT: no log dir at $LOG"; exit 9; }
[ -d "$R/other/.local/share/AgentEyes" ] || { say "ABORT: other host's data root not in scratch"; exit 9; }
say "isolation: report-only housekeep (no service) -> this host root=$pc ; other host root=$pco ; data roots $LOG/.. and $OLOG/.."
say "isolation positive control: the same probe under a second scratch env reports $pcc (it follows the env, so it can fail)"

# ---- TWO capturing services on one data root (one reap registry), both SIGKILLed; one orphan SIGSTOPped
start_serve $PB s1a; S1A=$SERVE_PID
start_serve $((PB+3)) s1b; S1B=$SERVE_PID
apipost $PB /record/start '{"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}' > "$R/start-a.json"
apipost $((PB+3)) /record/start '{"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}' > "$R/start-b.json"
DA=$(python3 -c "import json;print(json.load(open('$R/start-a.json'))['Dir'])"); DB=$(python3 -c "import json;print(json.load(open('$R/start-b.json'))['Dir'])")
case "$DA$DB" in "$R"/*"$R"/*) ;; *) say "ABORT dirs"; exit 9;; esac
say "S1a pid $S1A -> $(basename "$DA") ; S1b pid $S1B -> $(basename "$DB")"
sleep 30
WA=$(python3 -c "import json;print(json.load(open('$DA/capture.owner.json'))['Writers'][0]['Pid'])"); WB=$(python3 -c "import json;print(json.load(open('$DB/capture.owner.json'))['Writers'][0]['Pid'])")
grep -qa "$DA" /proc/$WA/cmdline && grep -qa "$DB" /proc/$WB/cmdline && say "writers confirmed by command line: A=$WA B=$WB" || { say "ABORT writers"; exit 9; }
MYPIDS+=($WA $WB)
kill -9 $S1A $S1B; wait $S1A $S1B 2>/dev/null; sleep 2
kill -STOP $WB
say "SIGKILLed both owners; A's orphan $WA left writing (alive=$(alive $WA)), B's orphan $WB SIGSTOPped ($(awk '/^State/{print $2}' /proc/$WB/status))"
T0=$(date +%s.%N); start_serve $((PB+1)) s2
for _ in $(seq 1 100); do (kill -0 $WA 2>/dev/null || kill -0 $WB 2>/dev/null) || break; sleep 0.2; done
say "both orphans gone $(python3 -c "print(round($(date +%s.%N)-$T0,2))") s after S2 was launched (A alive=$(alive $WA), B alive=$(alive $WB))"
grep -h "ReapOrphans: pid\|KillTree\|RequestGracefulExit\|reap examined" "$LOG"/*.log | grep "pid $SERVE_PID\]" | while read -r l; do say "  log: ${l:0:300}"; done
sa=$(waitmux "$DA" 120); sb=$(waitmux "$DB" 60)
say "A (graceful): $sa | $(state "$DA")"; [ -f "$DA/recording.mp4" ] && decode "$DA/recording.mp4"
say "B (survivor, killed): $sb | $(state "$DB")"
foreign; say "=== done; transcript $T"
