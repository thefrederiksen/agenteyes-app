#!/bin/bash
# QA #156 pass 4 - issue #172 measurement: a prober in its OWN pid namespace, sharing the state root
# (so the host id matches) and the user namespace (same uid), against a LIVE writer of the host.
# The container is docker's default private pid namespace, run as uid 1000; the host's .NET 8 is
# bind-mounted read-only. Only pids this script started are ever signalled.
set -u
S=${SCRATCH:?}; BIND=/home/<user>/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0
H=$S/harness/out-${HB:-a70fd90}/AgentEyes.Tests.Linux
R=$S/runs/pidns-$(date -u +%H%M%S); mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
T=$R/transcript.txt; say() { printf '%s %s\n' "$(date -u +%H:%M:%S.%3N)" "$*" | tee -a "$T"; }
D=$R/home/Videos/AgentEyes/2026-11-16_171000_video; mkdir -p "$D"
W=; trap '[ -n "$W" ] && kill $W 2>/dev/null' EXIT
st() { python3 -c "import json,os;m=json.load(open('$D/manifest.json'));pp=(m.get('PostProcessing') or {}).get('mux');print('PendingMux=%s mux=%s capture.owner.json=%s raw.mp4=%s recording.mp4=%s'%('pending' if m.get('PendingMux') else 'cleared',pp,'PRESENT' if os.path.exists('$D/capture.owner.json') else 'ABSENT',os.path.getsize('$D/raw.mp4') if os.path.exists('$D/raw.mp4') else 'absent','PRESENT' if os.path.exists('$D/recording.mp4') else 'absent'))"; }
DOCK=(docker run --rm --user 1000:1000 -e HOME -e XDG_CONFIG_HOME -e XDG_DATA_HOME -e DOTNET_ROOT=/usr/lib/dotnet -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
      -v /usr/lib/dotnet:/usr/lib/dotnet:ro -v $S/harness:$S/harness:ro -v $BIND:$BIND:ro -v $R:$R)
say "=== #172 measurement, product $(strings $BIND/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'), scratch $R"
say "host: pid namespace $(readlink /proc/self/ns/pid), user namespace $(readlink /proc/self/ns/user), hostname $(hostname)"
p=$(pgrep -f 'user_agent go2rtc/ffmpeg' | head -1); say "foreign go2rtc ffmpeg by command line: pid ${p:-NONE} (never signalled)"
root=$("$BIND/agenteyes" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //'); case "$root" in "$R"/*) say "isolation (host): root $root";; *) say "ABORT root $root"; exit 9;; esac
croot=$("${DOCK[@]}" --name qa156-iso-$$ ubuntu:24.04 $BIND/agenteyes housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //'); case "$croot" in "$R"/*) say "isolation (container): root $croot";; *) say "ABORT container root '$croot'"; exit 9;; esac
cat > "$D/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-16T17:10:00.0000000Z", "MonitorIndex": 1, "MonitorName": "stand-in",
  "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }
JSON
ffmpeg -nostdin -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 -t 300 -c:v libx264 -pix_fmt yuv420p -flush_packets 1 -f mp4 "$D/raw.mp4" & W=$!
sleep 3
"$H" owner "$D" $W > "$R/owner.txt" 2>&1; say "crashed owner (real TryAcquire + NoteWriter, then SIGKILL itself) exit $?: $(grep OWNER "$R/owner.txt" | cut -c14-200)"
say "owner record: $(cat "$D/capture.owner.json" | tr -d '\n' | cut -c1-400)"
say "host-id file: $(cat $XDG_DATA_HOME/AgentEyes/host-id) (shared with every arm through the bind-mounted state root)"
say "live writer W=$W, uid $(ps -o uid= -p $W | xargs), raw.mp4 $(stat -c %s "$D/raw.mp4") bytes"
say "--- ARM H (control): host namespace, plain prober"; "$H" inspect "$D" 2>&1 | cut -c1-420 | tee -a "$T"
say "--- ARM C2 (control): container with --pid=host"; "${DOCK[@]}" --pid=host --name qa156-c2-$$ ubuntu:24.04 $H inspect "$D" 2>&1 | cut -c1-420 | tee -a "$T"
say "--- ARM C1 (THE QUESTION): container, its own pid namespace, same uid, same state root"; "${DOCK[@]}" --name qa156-c1-$$ ubuntu:24.04 bash -c "echo \"\$(date -u +%H:%M:%S.%3N) inside: pid ns \$(readlink /proc/self/ns/pid), user ns \$(readlink /proc/self/ns/user), hostname \$(hostname), uid \$(id -u), kill -0 $W -> \$(kill -0 $W 2>&1 || true)\"; $H inspect $D" 2>&1 | cut -c1-420 | tee -a "$T"
say "  state after the read-only arms: $(st)"
a=$(stat -c %s "$D/raw.mp4")
say "--- ARM C3: the real CLI from the container's own pid namespace: agenteyes package <dir> --no-frames"
"${DOCK[@]}" --name qa156-c3-$$ ubuntu:24.04 $BIND/agenteyes package "$D" --no-frames > "$R/c3-package.log" 2>&1; rc=$?
say "  exit $rc: $(grep -m1 -iE 'refusing|error|fail' "$R/c3-package.log" | cut -c1-300)"
sleep 2; b=$(stat -c %s "$D/raw.mp4")
say "  after: $(st); writer W $(kill -0 $W 2>/dev/null && echo ALIVE || echo gone), raw.mp4 $a -> $b bytes"
say "  product log, the container's lines:"
grep -hE "IdentifyProcess|ThrowIfLive|ClearStaleClaim|NoteStarted|stale capture claim|DECLINED" $XDG_DATA_HOME/AgentEyes/logs/*.log | grep -v "pid $$\b" | tail -8 | cut -c1-330 | tee -a "$T"
say "=== done"
