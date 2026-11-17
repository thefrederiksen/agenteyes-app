#!/bin/bash
# QA #156 pass 4 (independent Linux seat) - real x11grab + pulse captures, plain MP4. Derived from
# pass 2's qa2.sh (same isolation checks, same state/decode instruments).
# Usage: qa4.sh <fix|pre|p3|main> <portbase> <crash|live>
#   crash : S1 captures, SIGKILL S1 mid-capture (the orphan keeps writing). Then, in order:
#           U1  a Landlock-confined `package <dir> --no-frames` (llprobe: reads of other /proc/<pid> refused)
#           U2  a Landlock-confined `serve` - its start-up reap and its first repair pass - then SIGTERMed by me
#           S2  an ordinary `serve` (the next service start): reap + recovery. Followed on the manifest.
#   live  : S1 captures; while it captures, a second process runs `package <dir> --no-frames`
#           (plain, then confined); then S1 is told to stop and the mux is followed to its end.
# Only pids this script started are ever signalled.
set -u
TAG=$1; PB=$2; SCEN=$3
SP=${SCRATCH:?}
case $TAG in
  fix)  BIN=/home/<user>/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  pre)  BIN=$SP/wt-d245037/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  p3)   BIN=$SP/wt-1927c62/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  main) BIN=$SP/wt-17c244d/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes ;;
  *) echo usage; exit 2 ;;
esac
LL=$SP/harness/llprobe
R=$SP/runs/$SCEN-$TAG-$(date -u +%H%M%S)
mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
export DISPLAY=:0 XAUTHORITY=/run/user/1000/gdm/Xauthority XDG_RUNTIME_DIR=/run/user/1000
LIB=$R/home/Videos/AgentEyes; LOG=$R/home/.local/share/AgentEyes/logs
T=$R/transcript.txt
ts() { date -u +%H:%M:%S.%3N; }
say() { printf '%s %s\n' "$(ts)" "$*" | tee -a "$T"; }
sz() { [ -f "$1" ] && stat -c %s "$1" || echo absent; }
alive() { kill -0 "$1" 2>/dev/null && echo yes || echo NO; }
MYPIDS=()
cleanup() {
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
print("raw.mp4=%s recording.mp4=%s capture.owner.json=%s | PendingMux=%s | mux: %s" % (
    s("raw.mp4"), s("recording.mp4"), s("capture.owner.json"), "pending" if m.get("PendingMux") else "cleared", mux))
PY
}
muxstate() { python3 -c "
import json,sys; m=json.load(open('$1/manifest.json')); pp=(m.get('PostProcessing') or {}).get('mux') or {}
print(('pending' if m.get('PendingMux') else 'cleared')+'/'+str(pp.get('State')))" 2>/dev/null; }
waitmux() { local st=""; for _ in $(seq 1 ${2:-120}); do st=$(muxstate "$1"); case "$st" in cleared/[Dd]one|*/[Ff]ailed) break;; esac; sleep 1; done; echo "$st"; }
decode() {
  local f=$1 dur de fe
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f")
  ffmpeg -nostdin -v error -i "$f" -f null - > "$R/decode.err" 2>&1; de=$?
  fe=$(ffmpeg -nostdin -v error -sseof -1 -i "$f" -map 0:v:0 -f framemd5 - 2>/dev/null | grep -vc '^#'); fe="$fe video frames decoded from the last second; last audio pts read: $(ffmpeg -nostdin -v error -sseof -1 -i "$f" -map 0:a:0 -f framemd5 - 2>/dev/null | grep -v '^#' | tail -1 | awk -F, '{print $3}' | xargs)"
  say "  decode $(basename "$f"): duration=${dur}s full-decode exit=$de stderr-lines=$(wc -l < "$R/decode.err") last-second: ${fe:-NO FRAME} streams=$(ffprobe -v error -show_entries stream=codec_type,codec_name -of csv=p=0 "$f" | tr '\n' ' ')"
}
start_serve() {  # <port> <name> [confined] -> SERVE_PID
  local port=$1 name=$2 pre=()
  [ "${3:-}" = confined ] && pre=("$LL" --)
  "${pre[@]}" "$BIN" serve --port "$port" > "$R/$name.stdout" 2>&1 &
  SERVE_PID=$!; MYPIDS+=($SERVE_PID)
  local root=""
  for _ in $(seq 1 80); do root=$(grep -m1 "recordings ->" "$R/$name.stdout" 2>/dev/null | sed 's/.*recordings -> //'); [ -n "$root" ] && break; sleep 0.25; done
  case "$root" in
    "$R"/*) say "[$name] serve pid $SERVE_PID port $port${3:+ (LANDLOCK-CONFINED: $(grep -m1 llprobe "$R/$name.stdout" | cut -c1-120))}; recordings root inside scratch: $root" ;;
    *) kill -9 "$SERVE_PID"; say "ABORT: [$name] recordings root '$root' is NOT inside $R - SIGKILLed pid $SERVE_PID ($(head -c 300 "$R/$name.stdout"))"; exit 9 ;;
  esac
}
apipost() { curl -s -m 30 -X POST "http://127.0.0.1:$1$2" -H 'Content-Type: application/json' -d "$3"; }
logpid() { grep -h "\[pid $1\]" "$LOG"/*.log 2>/dev/null; }

say "=== QA #156 pass 4 scenario=$SCEN binary=$TAG scratch=$R"
say "binary: $BIN"
say "embedded version: $(strings "$(dirname "$BIN")/agenteyes.dll" | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}')"
foreign
pc=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
CTRL=$SP/runs/isoctrl-$$; mkdir -p "$CTRL/h/.config" "$CTRL/h/.local/share" "$CTRL/h/Videos"
pcc=$(env HOME=$CTRL/h XDG_CONFIG_HOME=$CTRL/h/.config XDG_DATA_HOME=$CTRL/h/.local/share "$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
case "$pc" in "$R"/*) ;; *) say "ABORT: recordings root '$pc' is NOT inside $R"; exit 9 ;; esac
case "$pcc" in "$CTRL"/*) ;; *) say "ABORT: positive control failed - the probe did not follow the env ('$pcc')"; exit 9 ;; esac
say "isolation: report-only housekeep (no service) -> root=$pc ; positive control under a second env -> $pcc"

start_serve $PB s1; S1=$SERVE_PID
apipost $PB /record/start '{"mode":"video","screen":1,"source":"mic","mic":"Family 17h"}' > "$R/start.json"
D=$(python3 -c "import json;print(json.load(open('$R/start.json'))['Dir'])" 2>/dev/null)
[ -n "$D" ] && [ -d "$D" ] || { say "ABORT: record/start gave no dir: $(cat "$R/start.json")"; exit 9; }
case "$D" in "$R"/*) ;; *) say "ABORT: recording dir $D outside scratch"; exit 9 ;; esac
say "S1 pid $S1 capturing into $(basename "$D")"
sleep 12
if [ -f "$D/capture.owner.json" ]; then W=$(python3 -c "import json;o=json.load(open('$D/capture.owner.json'));print(o['Writers'][0]['Pid'])")
else W=""; for c in $(pgrep -P $S1); do grep -qa "$D" /proc/$c/cmdline 2>/dev/null && W=$c; done; say "no capture.owner.json (this build has no claim); writer found as S1's child by command line: ${W:-NONE}"; fi
grep -qa "$D" /proc/$W/cmdline 2>/dev/null && say "writer pid $W confirmed by command line to write into $(basename "$D") (comm=$(cat /proc/$W/comm), uid $(ps -o uid= -p $W | xargs))" || { say "ABORT: writer $W does not write into $D"; exit 9; }
MYPIDS+=($W)

if [ "$SCEN" = live ]; then
  say "LIVE ARM: S1 pid $S1 alive=$(alive $S1), writer $W alive=$(alive $W); $(state "$D")"
  ls "$D" > "$R/ls-before.txt"
  a=$(sz "$D/raw.mp4")
  "$BIN" package "$D" --no-frames > "$R/live-package-plain.log" 2>&1; rc=$?
  say "  second process, plain: package --no-frames -> exit $rc: $(grep -m1 -iE 'refusing|error|fail' "$R/live-package-plain.log" | cut -c1-360)"
  say "  $(state "$D")"
  "$LL" -- "$BIN" package "$D" --no-frames > "$R/live-package-confined.log" 2>&1; rc=$?
  say "  second process, LANDLOCK-confined: package --no-frames -> exit $rc: $(grep -m1 -iE 'refusing|error|fail' "$R/live-package-confined.log" | cut -c1-360)"
  b=$(sz "$D/raw.mp4")
  say "  $(state "$D")"
  say "  directory listing before/after the two finalize attempts: $(diff <(cat "$R/ls-before.txt") <(ls "$D") >/dev/null && echo identical || echo "CHANGED: $(diff <(cat "$R/ls-before.txt") <(ls "$D") | tr '\n' ' ')"); raw.mp4 $a -> $b bytes (still growing: $([ "$b" -gt "$a" ] && echo yes || echo NO)); writer $W alive=$(alive $W)"
  sleep 8
  apipost $PB /record/stop '{}' > "$R/stop.json"; say "POST /record/stop -> $(cut -c1-160 "$R/stop.json")"
  st=$(waitmux "$D" 180); say "followed manifest PostProcessing.mux: $st"
  say "  $(state "$D")"
  [ -f "$D/recording.mp4" ] && decode "$D/recording.mp4"
  foreign; say "=== done; transcript $T"; exit 0
fi

sleep 25
say "  $(state "$D")"
say "SIGKILL S1 pid $S1 mid-capture"
kill -9 $S1; wait $S1 2>/dev/null
a=$(sz "$D/raw.mp4"); sleep 4; b=$(sz "$D/raw.mp4")
say "after S1's death: writer $W alive=$(alive $W) ppid=$(ps -o ppid= -p $W | xargs) raw.mp4 $a -> $b bytes in 4 s"

# ---- U1: a Landlock-confined finalizer, driven directly ----
"$LL" -- "$BIN" package "$D" --no-frames > "$R/u1-package.log" 2>&1; rc=$?
say "U1 confined package --no-frames -> exit $rc: $(grep -m1 -iE 'refusing|error|fail|unable' "$R/u1-package.log" | cut -c1-420)"
say "  $(head -1 "$R/u1-package.log" | cut -c1-160)"
say "  $(state "$D") ; writer $W alive=$(alive $W)"

# ---- U2: a Landlock-confined service: start-up reap + first repair pass ----
start_serve $((PB+1)) u2 confined; U2=$SERVE_PID
sleep 35
say "U2 (confined service pid $U2) log, its own lines only:"
logpid $U2 | grep -E "ReapOrphans|IdentifyProcess|IsSameProcess|FindUnfinished|NOT REPAIRED|NOT GENERATED|NeedsThumb|DECLINED|Execute|RunAsync FAILED|FAILED" | cut -c1-360 | while read -r l; do say "  U2: $l"; done
a=$(sz "$D/raw.mp4"); sleep 2; b=$(sz "$D/raw.mp4")
say "  after U2: writer $W alive=$(alive $W), raw.mp4 $a -> $b in 2 s; $(state "$D")"
say "  U2 /health: $(curl -s -m 5 http://127.0.0.1:$((PB+1))/health | cut -c1-80)"
kill -TERM $U2; for _ in $(seq 1 40); do kill -0 $U2 2>/dev/null || break; sleep 0.25; done; say "  U2 ended by me (SIGTERM): alive=$(alive $U2)"

# ---- S2: the next ordinary service start ----
T0=$(date +%s.%N)
start_serve $((PB+2)) s2; S2=$SERVE_PID
for _ in $(seq 1 80); do kill -0 $W 2>/dev/null || break; sleep 0.25; done
T1=$(date +%s.%N)
say "writer $W alive after S2's start-up reap: $(alive $W) ($(python3 -c "print(round($T1-$T0,2))") s after S2 was launched); raw.mp4 now $(sz "$D/raw.mp4") bytes"
logpid $S2 | grep -E "ReapOrphans: pid|KillTree|RequestGracefulExit" | cut -c1-330 | while read -r l; do say "  S2: $l"; done
st=$(waitmux "$D" 120); say "recovery by S2 (manifest PostProcessing.mux, not /status): $st"
say "  $(state "$D")"
[ -f "$D/recording.mp4" ] && decode "$D/recording.mp4"
logpid $S2 | grep "$(basename "$D")" | grep -E "ClearStaleClaim|stale capture claim" | tail -2 | cut -c1-330 | while read -r l; do say "  S2: $l"; done
foreign
say "=== done; transcript $T"
