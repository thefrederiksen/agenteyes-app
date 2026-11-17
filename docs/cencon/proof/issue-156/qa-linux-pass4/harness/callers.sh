#!/bin/bash
# QA #156 pass 4 (Linux) - every caller x {Unknown, Mine, NotMine}, real probe, real lock, real claim.
# Usage: callers.sh <a70fd90|1927c62> <unknown|mine|notmine>
set -u
B=$1; MODE=$2; S=${SCRATCH:?}
H=$S/harness/out-$B/AgentEyes.Tests.Linux
BIN=$( [ $B = a70fd90 ] || [ $B = 9abbb01 ] && echo /home/<user>/ReposFred/agenteyes-app || echo $S/wt-$B )/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
R=$S/runs/callers-$B-$MODE-$(date -u +%H%M%S); mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
T=$R/transcript.txt; say() { printf '%s %s\n' "$(date -u +%H:%M:%S.%3N)" "$*" | tee -a "$T"; }
LIB=$R/home/Videos/AgentEyes; D=$LIB/2026-11-16_170000_video; REL=$LIB/2026-11-16_170100_video; HL=$LIB/2026-11-16_165900_video
MINE=()
trap 'for p in "${MINE[@]}"; do kill -0 $p 2>/dev/null && kill $p; done' EXIT
say "=== callers binary=$B mode=$MODE scratch=$R"
say "product embedded: $(strings $(dirname $BIN)/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}')"
p=$(pgrep -f 'user_agent go2rtc/ffmpeg' | head -1); say "foreign go2rtc ffmpeg by command line: pid ${p:-NONE} (never signalled)"
root=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //')
case "$root" in "$R"/*) say "isolation: report-only housekeep under this env -> root $root (inside scratch)";; *) say "ABORT: root '$root' outside scratch"; exit 9;; esac
fix() { mkdir -p "$1"; cat > "$1/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-16T17:00:00.0000000Z",
  "MonitorIndex": 1, "MonitorName": "stand-in",
  "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }
JSON
}
fix "$D"; fix "$REL"; fix "$HL"
TH=$LIB/2026-11-16_170200_video; mkdir -p "$TH"; cat > "$TH/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-16T17:02:00.0000000Z", "MonitorIndex": 1, "MonitorName": "stand-in", "VideoFile": "recording.mp4" }
JSON
ffmpeg -nostdin -hide_banner -nostats -loglevel error -f lavfi -i testsrc2=size=320x240:rate=30 -t 3 -c:v libx264 -pix_fmt yuv420p "$HL/raw.mp4" && cp "$HL/raw.mp4" "$TH/recording.mp4" && say "thumbnail fixture $(basename $TH): no PendingMux, finished recording.mp4, claimed below by the crashed owner with the same live writer" && say "healthy sibling $(basename $HL): finished raw.mp4, no claim (the control that the sweep still serves others)"
ffmpeg -nostdin -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 -t 300 -c:v libx264 -pix_fmt yuv420p -flush_packets 1 -f mp4 "$D/raw.mp4" & W=$!; MINE+=($W)
ffmpeg -nostdin -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 -t 300 -c:v libx264 -pix_fmt yuv420p -flush_packets 1 -f mp4 "$REL/raw.mp4" & RW=$!; MINE+=($RW)
sleep 3; g1=$(stat -c %s "$D/raw.mp4"); sleep 2; g2=$(stat -c %s "$D/raw.mp4"); say "W state $(awk "/^State/{print \$2}" /proc/$W/status), raw.mp4 growing: $g1 -> $g2 bytes in 2 s"; [ "$g2" -gt "$g1" ] || { say "ABORT: writer not writing"; exit 9; }
say "writer W=$W (mine, uid $(ps -o uid= -p $W | xargs)) writing $(basename $D)/raw.mp4; release writer RW=$RW writing $(basename $REL)/raw.mp4"
"$H" owner "$D" $W "$TH" > "$R/owner.txt" 2>&1; say "crashed owner exited with $? ($(grep -c OWNER "$R/owner.txt") claim line): $(grep OWNER "$R/owner.txt" | cut -c14-)"
if [ $MODE = notmine ]; then kill $W; wait $W 2>/dev/null; say "notmine: W $W ended by me -> $(kill -0 $W 2>/dev/null && echo alive || echo gone)"; fi
a=$(stat -c %s "$D/raw.mp4" 2>/dev/null)
if [ $MODE = unknown ]; then (cd $S/harness/out-$B && "$H" probe $MODE "$LIB" "$D" "$REL" $RW) 2>&1 | tee -a "$T"
else "$H" probe $MODE "$LIB" "$D" "$REL" $RW 2>&1 | tee -a "$T"; fi
sleep 2; b=$(stat -c %s "$D/raw.mp4" 2>/dev/null)
say "after: W $W $(kill -0 $W 2>/dev/null && echo ALIVE || echo gone); $(basename $D)/raw.mp4 $a -> $b bytes; recording.mp4 $([ -f $D/recording.mp4 ] && echo PRESENT || echo absent); manifest mux: $(python3 -c "import json;m=json.load(open('$D/manifest.json'));pp=(m.get('PostProcessing') or {}).get('mux');print(('pending' if m.get('PendingMux') else 'cleared'),pp)")"
say "product log lines (this run's data root):"
grep -hE "IdentifyProcess|IsSameProcess|ThrowIfLive|ClearStaleClaim|NOT REPAIRED|NOT GENERATED|FindUnfinished|ReapOrphans|Release:|UNDETERMINED|undetermined" $R/home/.local/share/AgentEyes/logs/*.log | cut -c1-400 | tee -a "$T"
