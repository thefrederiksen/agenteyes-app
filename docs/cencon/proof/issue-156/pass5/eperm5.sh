#!/bin/bash
# Dev #156 pass 5 (Linux) - QA pass 4's eperm.sh (D-L1), unchanged in its four arms, plus the positive
# controls IN THE SAME RUN:
#   A  a crashed owner's claim whose writer is a LIVE uid-1000 ffmpeg, writing:
#        P plain inspect, L Landlock-only inspect, E Landlock + kill EPERM inspect,
#        E2 the real `package --no-frames` under Landlock + kill EPERM
#      then the writer is stopped (SIGTERM, our own ffmpeg) - a genuinely stale claim:
#        S1 Landlock + kill EPERM inspect, S2 an ORDINARY `package --no-frames` -> recovered
#   B  a crashed owner's claim whose writer pid is now held by ANOTHER USER (a root process; the
#      record keeps our writer's start time, as a reused pid would):
#        BP plain inspect, BE Landlock + kill EPERM inspect, BE2 the real `package --no-frames`
#        under Landlock + kill EPERM -> recovered
# Usage: eperm5.sh <before|after>
set -u
B=$1; S=${SCRATCH:?}; H=$S/harness/out-$B/AgentEyes.Tests.Linux; LL=$S/harness/llprobe
case $B in before) BIN=$S/wt-before/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes;; after) BIN=/home/soren/ReposFred/agenteyes-116/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes;; esac
R=$S/runs/eperm5-$B-$(date -u +%H%M%S); mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes"
REAL_LIB=/home/soren/Videos/AgentEyes
lib_sig() { find "$REAL_LIB" -maxdepth 2 -printf '%p %s %T@\n' 2>/dev/null | sort | md5sum | cut -c1-32; }
SIG0=$(lib_sig); touch "$R/start.marker"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share
T=$R/transcript.txt; say() { printf '%s %s\n' "$(date -u +%H:%M:%S.%3N)" "$*" | tee -a "$T"; }
A=$R/home/Videos/AgentEyes/2026-11-16_180000_video; Bd=$R/home/Videos/AgentEyes/2026-11-16_180100_video; mkdir -p "$A" "$Bd"
W=; X=; trap '[ -n "$W" ] && kill $W 2>/dev/null; [ -n "$X" ] && kill $X 2>/dev/null' EXIT
st() { python3 -c "import json,os;d='$1';m=json.load(open(d+'/manifest.json'));pp=(m.get('PostProcessing') or {}).get('mux');print('PendingMux=%s mux=%s capture.owner.json=%s recording.mp4=%s'%('pending' if m.get('PendingMux') else 'cleared',pp,'PRESENT' if os.path.exists(d+'/capture.owner.json') else 'ABSENT','PRESENT' if os.path.exists(d+'/recording.mp4') else 'absent'))"; }
manifest() { cat > "$1/manifest.json" <<'JSON'
{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-16T18:00:00.0000000Z", "MonitorIndex": 1, "MonitorName": "stand-in",
  "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }
JSON
}
plog() { grep -hE "IdentifyProcess|ThrowIfLive|ClearStaleClaim|NoteStarted|DECLINED|UNDETERMINED|NoteFinished|NoteFailed" $XDG_DATA_HOME/AgentEyes/logs/*.log 2>/dev/null | tail -${1:-6} | cut -c1-400 | tee -a "$T"; }
decode() { if [ -f "$1/recording.mp4" ]; then ffmpeg -nostdin -v error -i "$1/recording.mp4" -f null - 2>&1 | head -3; echo "decode exit ${PIPESTATUS[0]}; streams: $(ffprobe -v error -show_entries stream=codec_name -of csv=p=0 "$1/recording.mp4" | tr '\n' ' ')"; else echo "no recording.mp4"; fi; }

say "=== #156 pass 5 Linux EPERM row; build $B; harness product $(strings $(dirname $H)/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'), CLI $(strings $(dirname $BIN)/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}')"
p=$(pgrep -f 'user_agent go2rtc/ffmpeg' | head -1); say "foreign go2rtc ffmpeg by command line: pid ${p:-NONE} (never signalled)"
root=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //'); case "$root" in "$R"/*) say "isolation: root $root";; *) say "ABORT root $root"; exit 9;; esac
say "real library signature before: $SIG0"
manifest "$A"; manifest "$Bd"

# ---- A: the live writer ---------------------------------------------------------------------------
ffmpeg -nostdin -hide_banner -nostats -loglevel error -re -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 \
  -t 300 -c:v libx264 -pix_fmt yuv420p -c:a aac -f mp4 "$A/raw.mp4" & W=$!
sleep 3
"$H" owner "$A" $W > "$R/ownerA.txt" 2>&1; say "A: crashed owner exit $?: $(grep OWNER "$R/ownerA.txt" | cut -c14-200)"
say "A: LIVE writer W=$W uid $(ps -o uid= -p $W | xargs) (mine: $(id -u)); state $(awk '/^State/{print $2}' /proc/$W/status); /proc/$W owned by uid $(stat -c %u /proc/$W) (dumpable: owner is the euid)"
say "--- A arm P (control): plain prober";                        "$H" inspect "$A" 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-360 | tee -a "$T"
say "--- A arm L (control): Landlock only (kill(pid,0) succeeds)"; (cd $(dirname $H) && "$LL" -- "$H" inspect "$A") 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-360 | tee -a "$T"
say "--- A arm E (THE QUESTION): Landlock + kill(2) answers EPERM"; (cd $(dirname $H) && "$LL" --kill-eperm -- "$H" inspect "$A") 2>&1 | grep -E "llprobe|recorded writer|\[Inspect\]" | cut -c1-360 | tee -a "$T"
say "  syscall control under the same confinement: $("$LL" --kill-eperm -- /bin/sh -c "cat /proc/$W/status >/dev/null 2>&1 && echo status-readable || echo status-REFUSED; stat -c 'stat /proc/$W uid %u' /proc/$W; kill -0 $W 2>&1 | tail -1 | sed 's/^/kill -0: /'" 2>/dev/null | grep -v llprobe | tr '\n' ';')"
say "  A state after the read-only arms: $(st "$A")"
a=$(stat -c %s "$A/raw.mp4")
say "--- A arm E2: the real CLI under the same confinement: agenteyes package <A> --no-frames"
"$LL" --kill-eperm -- "$BIN" package "$A" --no-frames > "$R/A-e2-package.log" 2>&1; rc=$?
say "  exit $rc: $(grep -m1 -iE 'refusing|being captured|error|fail' "$R/A-e2-package.log" | cut -c1-300)"
sleep 2; b=$(stat -c %s "$A/raw.mp4")
say "  after: $(st "$A"); writer W $(kill -0 $W 2>/dev/null && echo ALIVE || echo gone), raw.mp4 $a -> $b bytes"
say "  product log of arm E2:"; plog 6

say "--- A: stop the writer for real (SIGTERM to OUR ffmpeg $W, cmd: $(tr '\0' ' ' </proc/$W/cmdline | cut -c1-60)...) - the claim is now genuinely stale"
kill -TERM $W; for i in $(seq 1 100); do kill -0 $W 2>/dev/null || break; sleep 0.1; done; say "  writer W $(kill -0 $W 2>/dev/null && echo STILL-ALIVE || echo exited); raw.mp4 $(stat -c %s "$A/raw.mp4") bytes"; W=
say "--- A arm S1: Landlock + kill EPERM inspect of the stale claim (writer gone)"; (cd $(dirname $H) && "$LL" --kill-eperm -- "$H" inspect "$A") 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-360 | tee -a "$T"
say "--- A arm S2 (POSITIVE CONTROL): an ORDINARY agenteyes package <A> --no-frames"
t0=$(date +%s.%N); "$BIN" package "$A" --no-frames > "$R/A-s2-package.log" 2>&1; rc=$?; t1=$(date +%s.%N)
say "  exit $rc in $(python3 -c "print('%.1f'%($t1-$t0))") s; after: $(st "$A")"
say "  $(decode "$A" | tr '\n' ' ')"
say "  product log of arm S2:"; plog 5

# ---- B: the pid reused by ANOTHER USER ---------------------------------------------------------------
ffmpeg -nostdin -hide_banner -nostats -loglevel error -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 \
  -t 6 -c:v libx264 -pix_fmt yuv420p -c:a aac -f mp4 "$Bd/raw.mp4"
sleep 600 & X=$!
"$H" owner "$Bd" $X > "$R/ownerB.txt" 2>&1; say "B: crashed owner exit $?: $(grep OWNER "$R/ownerB.txt" | cut -c14-200)"
kill $X; wait $X 2>/dev/null; X=
RP=1; say "B: another user's process: pid $RP, uid $(stat -c %u /proc/$RP) ($(ps -o user=,comm= -p $RP | xargs)); our writer $X exited"
python3 - "$Bd/capture.owner.json" $RP <<'PY'
import json,sys
p,rp=sys.argv[1],int(sys.argv[2]); o=json.load(open(p))
k=[k for k in o if k.lower()=='writers'][0]; w=o[k][0]; pk=[k for k in w if k.lower()=='pid'][0]
old=w[pk]; w[pk]=rp; json.dump(o,open(p,'w'))
print(f"B: owner record writer pid {old} -> {rp} (its recorded start time kept: the pid was reused)")
PY
say "$(tail -0 /dev/null)B: owner record: $(cat "$Bd/capture.owner.json" | cut -c1-300)"
say "--- B arm BP (control): plain prober";                      "$H" inspect "$Bd" 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-360 | tee -a "$T"
say "--- B arm BE: Landlock + kill(2) answers EPERM";             (cd $(dirname $H) && "$LL" --kill-eperm -- "$H" inspect "$Bd") 2>&1 | grep -E "recorded writer|\[Inspect\]" | cut -c1-360 | tee -a "$T"
say "--- B arm BE2 (POSITIVE CONTROL): the real CLI under Landlock + kill EPERM: agenteyes package <B> --no-frames"
"$LL" --kill-eperm -- "$BIN" package "$Bd" --no-frames > "$R/B-be2-package.log" 2>&1; rc=$?
say "  exit $rc; after: $(st "$Bd")"
say "  $(decode "$Bd" | tr '\n' ' ')"
say "  product log of arm BE2:"; plog 6
say "  (BE2's mux runs an ffmpeg CHILD whose /proc this confinement also refuses - a harness property; the claim decision above is the row under test)"
say "--- B arm BO (POSITIVE CONTROL, recovery): an ORDINARY agenteyes package <B> --no-frames"
"$BIN" package "$Bd" --no-frames > "$R/B-bo-package.log" 2>&1; rc=$?
say "  exit $rc ($(grep -m1 -oE 'transcription FAILED: Not signed in' "$R/B-bo-package.log" || echo -)); after: $(st "$Bd")"
say "  $(decode "$Bd" | tr '\n' ' ')"
say "pid $RP still running: $(kill -0 $RP 2>&1 | grep -q 'not permitted' && echo yes-not-ours || echo check)"
SIG1=$(lib_sig); say "real library signature after: $SIG1 ($( [ "$SIG0" = "$SIG1" ] && echo UNCHANGED || echo CHANGED))"; say "real library entries modified during this run: $(find "$REAL_LIB" -newer "$R/start.marker" 2>/dev/null | wc -l) $(find "$REAL_LIB" -newer "$R/start.marker" 2>/dev/null | head -5 | tr '\n' ' ')"; say "evidence recording present: $(ls -d $REAL_LIB/2026-11-14_102532_video >/dev/null 2>&1 && echo yes || echo NO)"
say "=== done"
