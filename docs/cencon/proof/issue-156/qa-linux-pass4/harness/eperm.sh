#!/bin/bash
# QA #156 pass 4 (Linux) - the Linux EPERM row, as the lead put in scope (6922629027): a prober refused
# a LIVE same-user writer's start time (Landlock: other /proc/<pid> reads refused) AND refused kill(2)
# with EPERM (seccomp RET_ERRNO(EPERM) on kill - the errno Landlock ABI 6 signal scoping returns).
# Usage: eperm.sh <harness-tag> ; the CLI of the same build is used for the end-to-end arm.
set -u
B=$1; S=${SCRATCH:?}; H=$S/harness/out-$B/AgentEyes.Tests.Linux; LL=$S/harness/llprobe
case $B in 9abbb01) BIN=/home/soren/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes;; *) BIN=$S/wt-$B/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes;; esac
R=$S/runs/eperm-$B-$(date -u +%H%M%S); mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
T=$R/transcript.txt; say() { printf '%s %s\n' "$(date -u +%H:%M:%S.%3N)" "$*" | tee -a "$T"; }
D=$R/home/Videos/AgentEyes/2026-11-16_172000_video; mkdir -p "$D"
W=; trap '[ -n "$W" ] && kill $W 2>/dev/null' EXIT
st() { python3 -c "import json,os;m=json.load(open('$D/manifest.json'));pp=(m.get('PostProcessing') or {}).get('mux');print('PendingMux=%s mux=%s capture.owner.json=%s recording.mp4=%s'%('pending' if m.get('PendingMux') else 'cleared',pp,'PRESENT' if os.path.exists('$D/capture.owner.json') else 'ABSENT','PRESENT' if os.path.exists('$D/recording.mp4') else 'absent'))"; }
say "=== Linux EPERM row; harness $B product $(strings $(dirname $H)/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'), CLI $(strings $(dirname $BIN)/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}')"
p=$(pgrep -f 'user_agent go2rtc/ffmpeg' | head -1); say "foreign go2rtc ffmpeg by command line: pid ${p:-NONE} (never signalled)"
root=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //'); case "$root" in "$R"/*) say "isolation: root $root";; *) say "ABORT root $root"; exit 9;; esac
cat > "$D/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-16T17:20:00.0000000Z", "MonitorIndex": 1, "MonitorName": "stand-in",
  "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }
JSON
ffmpeg -nostdin -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 -t 300 -c:v libx264 -pix_fmt yuv420p -flush_packets 1 -f mp4 "$D/raw.mp4" & W=$!
sleep 3
"$H" owner "$D" $W > "$R/owner.txt" 2>&1; say "crashed owner exit $?: $(grep OWNER "$R/owner.txt" | cut -c14-200)"
say "LIVE writer W=$W uid $(ps -o uid= -p $W | xargs) (same as mine: $(id -u)); state $(awk '/^State/{print $2}' /proc/$W/status)"
say "--- arm P (control): plain prober";                          "$H" inspect "$D" 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-330 | tee -a "$T"
say "--- arm L (control): Landlock only (kill(pid,0) succeeds)";   (cd $(dirname $H) && "$LL" -- "$H" inspect "$D") 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-330 | tee -a "$T"
say "--- arm E (THE QUESTION): Landlock + kill(2) answers EPERM";  (cd $(dirname $H) && "$LL" --kill-eperm -- "$H" inspect "$D") 2>&1 | grep -E "llprobe|recorded writer|\[Inspect\]" | cut -c1-330 | tee -a "$T"
say "  state after the read-only arms: $(st)"
a=$(stat -c %s "$D/raw.mp4")
say "--- arm E2: the real CLI under the same confinement: agenteyes package <dir> --no-frames"
"$LL" --kill-eperm -- "$BIN" package "$D" --no-frames > "$R/e2-package.log" 2>&1; rc=$?
say "  exit $rc: $(grep -m1 -iE 'refusing|error|fail' "$R/e2-package.log" | cut -c1-300)"
sleep 2; b=$(stat -c %s "$D/raw.mp4")
say "  after: $(st); writer W $(kill -0 $W 2>/dev/null && echo ALIVE || echo gone), raw.mp4 $a -> $b bytes"
say "  product log of arm E2:"
grep -hE "IdentifyProcess|ThrowIfLive|ClearStaleClaim|NoteStarted|DECLINED" $XDG_DATA_HOME/AgentEyes/logs/*.log | tail -6 | cut -c1-380 | tee -a "$T"
say "=== done"
