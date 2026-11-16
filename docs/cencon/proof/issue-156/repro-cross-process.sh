#!/usr/bin/env bash
# Issue #156 - cross-process capture ownership: reproduction and both control arms.
#
# Usage:  repro-cross-process.sh <agenteyes-binary> <scratch-dir> <plain|frag> <arm>
#   arm = live-package   a live capture (owner alive, writer running); a SECOND process runs
#                        'agenteyes package <dir> --no-frames' three times, then the owner stops
#                        and the recording is finalized.
#         live-repair    same live capture; a SECOND process runs a real repair pass
#                        ('agenteyes serve', whose first pass runs 20 s after start).
#         orphan-writer  the owner process dies (SIGKILL) while its ffmpeg keeps writing; package
#                        is run while the orphan writes, then again after it ends.
#         dead-owner     the owner process dies and its writer ends with it; package recovers it.
#
# Without a Screen Recording grant a real capture cannot be started from a shell, so the capture
# is stood in for by:
#   - an ffmpeg lavfi source writing raw.mp4 with the product's output options (libx264 veryfast
#     yuv420p crf 23, aac 128k - the Windows/Linux topology where the mic is inside raw.mp4); with
#     'frag' it adds fragmented-MP4 movflags, standing in for #113's FragmentedOutput();
#   - the START manifest a live capture leaves on disk, written by hand;
#   - an owner process (python, flock(2) - the same lock call .NET makes for FileShare.None on
#     macOS/Linux) holding capture.lock and publishing capture.owner.json exactly as
#     CaptureLock.TryAcquire + NoteWriter do. On a binary WITHOUT issue #156 nothing reads these.
#
# Everything lives under <scratch-dir> with its own HOME. It never touches a real recordings
# directory, never deletes a recording, and only ever signals processes it started itself.
set -u
BIN="$1"; OUT="$2"; CONTAINER="$3"; ARM="$4"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
# This Mac has no .NET 8 runtime: the net8.0 binary runs on the installed .NET 10 by roll-forward.
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}"
export HOME="$OUT/home"
mkdir -p "$HOME/Movies/AgentEyes"
REC="$HOME/Movies/AgentEyes/2026-11-16_120000_video"
mkdir -p "$REC"
say() { printf '%s [%s] %s\n' "$(date -u +%H:%M:%S)" "$ARM/$CONTAINER" "$*"; }
size() { if [ -f "$1" ]; then stat -f %z "$1"; else echo "absent"; fi; }

cat > "$REC/manifest.json" <<'JSON'
{
  "Tool": "AgentEyes",
  "Mode": "video",
  "Label": "video",
  "CreatedUtc": "2026-11-16T12:00:00.0000000Z",
  "MonitorIndex": 1,
  "MonitorName": "stand-in",
  "PendingMux": {
    "Mode": "video",
    "Source": "mic",
    "RawVideo": "raw.mp4",
    "FinalFile": "recording.mp4",
    "RawDurationSeconds": 0
  }
}
JSON

MOV=()
[ "$CONTAINER" = "frag" ] && MOV=(-movflags +frag_keyframe+empty_moov+default_base_moof)
mkfifo "$OUT/ffin"
# The writer: live-paced, keeps a writer on the fifo so 'q' can stop it cleanly like the product.
( exec ffmpeg -hide_banner -nostats -loglevel error -re \
    -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 -g 30 -c:a aac -b:a 128k ${MOV[@]+"${MOV[@]}"} \
    -y "$REC/raw.mp4" < "$OUT/ffin" > "$OUT/ffmpeg.log" 2>&1 ) &
FFSH=$!
exec 7> "$OUT/ffin"
sleep 1
FF=$(pgrep -P $FFSH ffmpeg || echo $FFSH)
FFEPOCH=$(LC_ALL=C date -j -f "%a %b %d %T %Y" "$(LC_ALL=C ps -o lstart= -p "$FF" | xargs)" +%s)
FFSTART=$(date -u -r "$FFEPOCH" +%Y-%m-%dT%H:%M:%SZ)
say "writer ffmpeg pid $FF started $FFSTART"

# The owner: holds capture.lock with flock(LOCK_EX) and publishes the owner record.
cat > "$OUT/owner.py" <<'PY'
import fcntl, json, os, socket, sys, time, datetime
rec, ffpid, ffstart = sys.argv[1], int(sys.argv[2]), sys.argv[3]
fd = os.open(os.path.join(rec, "capture.lock"), os.O_RDWR | os.O_CREAT, 0o644)
fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
rec_ = {"Host": sys.argv[4], "Pid": os.getpid(), "ProcessStartUtc": now, "What": "capture session",
        "StartedUtc": now, "Writers": [{"Label": "video", "Pid": ffpid, "StartUtc": ffstart}]}
tmp = os.path.join(rec, "capture.owner.json.%d.tmp" % os.getpid())
open(tmp, "w").write(json.dumps(rec_, indent=2))
os.replace(tmp, os.path.join(rec, "capture.owner.json"))
print("owner pid %d holds capture.lock" % os.getpid(), flush=True)
try:
    time.sleep(3600)
finally:
    pass
PY
HOSTNAME_DOTNET=$(hostname -s)   # what .NET's Environment.MachineName reports on macOS/Linux
python3 "$OUT/owner.py" "$REC" "$FF" "$FFSTART" "$HOSTNAME_DOTNET" > "$OUT/owner.log" 2>&1 &
OWNER=$!
sleep 1
say "owner pid $OWNER: $(cat "$OUT/owner.log")"

stop_writer() { echo q >&7; exec 7>&-; wait $FFSH 2>/dev/null; say "writer stopped cleanly; raw.mp4 $(size "$REC/raw.mp4") bytes"; }
release_owner() { rm -f "$REC/capture.owner.json"; kill $OWNER 2>/dev/null; wait $OWNER 2>/dev/null; say "owner released the claim (record removed, lock closed)"; }
mux_state() { python3 - "$REC/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
pp = (m.get("PostProcessing") or {}).get("mux")
print("PendingMux=%s mux=%s DurationSeconds=%s" % ("pending" if m.get("PendingMux") else "cleared",
      json.dumps(pp) if pp else "(no attempt recorded)", m.get("DurationSeconds")))
PY
}
package() { say "SECOND PROCESS: agenteyes package --no-frames"; "$BIN" package "$REC" --no-frames > "$OUT/package.$1.log" 2>&1; local rc=$?
  say "  exit $rc: $(grep -m1 -iE 'refusing|error|could not|failed' "$OUT/package.$1.log" | cut -c1-400)"
  say "  raw.mp4=$(size "$REC/raw.mp4") recording.mp4=$(size "$REC/recording.mp4") recording.original.mp4=$(size "$REC/recording.original.mp4")"
  say "  $(mux_state)"; }

sleep 11   # twelve seconds into the capture, as in the gate's run
case "$ARM" in
  live-package)
    for i in 1 2 3; do package "$i"; sleep 2; done
    say "writer still alive: $(ps -p $FF >/dev/null && echo yes || echo NO); raw.mp4 now $(size "$REC/raw.mp4")"
    stop_writer; release_owner
    package final ;;
  live-repair)
    # SAFETY: 'agenteyes serve' scans the REAL recordings root unless the OS folder lookups follow
    # HOME. On macOS they do not - .NET resolves Movies and Application Support without consulting
    # HOME (measured on this branch's development run: the service reported
    # recordings=/Users/<owner>/Movies/AgentEyes under an isolated HOME). There is no CLI switch to
    # redirect it, so this arm refuses to run on macOS rather than point a repair pass at a real
    # library. On Linux the XDG folders follow the variables set here; the service's own startup
    # line is checked below before its first pass (at +20 s) can run.
    if [ "$(uname)" = "Darwin" ]; then
      say "REFUSED on macOS: the repair pass cannot be isolated from the real recordings root here. Run this arm on Linux or Windows."
      kill $OWNER 2>/dev/null; kill $FF 2>/dev/null; exit 3
    fi
    export XDG_DATA_HOME="$HOME/.local/share" XDG_CONFIG_HOME="$HOME/.config" XDG_VIDEOS_DIR="$HOME/Movies"
    mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME"
    printf 'XDG_VIDEOS_DIR="%s"\n' "$HOME/Movies" > "$XDG_CONFIG_HOME/user-dirs.dirs"
    PORT=$((17000 + RANDOM % 900))
    say "SECOND PROCESS: agenteyes serve --port $PORT (real RepairService; first pass at +20 s)"
    "$BIN" serve --port $PORT > "$OUT/serve.log" 2>&1 &
    SERVE=$!
    for _ in 1 2 3 4 5 6 7 8 9 10; do grep -q "recordings ->" "$OUT/serve.log" 2>/dev/null && break; sleep 1; done
    ROOT=$(grep -m1 "recordings ->" "$OUT/serve.log" | sed 's/.*recordings -> //')
    case "$ROOT" in
      "$HOME"/*) say "service recordings root is inside the scratch HOME: $ROOT" ;;
      *) kill $SERVE; say "ABORTED: the service's recordings root '$ROOT' is outside the scratch HOME - killed before its first pass"
         kill $OWNER 2>/dev/null; kill $FF 2>/dev/null; exit 3 ;;
    esac
    sleep 28
    kill -INT $SERVE 2>/dev/null; sleep 3; kill $SERVE 2>/dev/null; wait $SERVE 2>/dev/null
    LOG=$(find "$HOME" -name '*.log' -path '*AgentEyes*' -newer "$OUT/owner.py" | head -1)
    say "service log: $LOG"
    grep -hE "Resume|DECLINED|admitted|capture claim|NoteStarted|finalize|could not" "$LOG" | sed 's/^/    /' | cut -c1-400
    say "  raw.mp4=$(size "$REC/raw.mp4") recording.mp4=$(size "$REC/recording.mp4") recording.original.mp4=$(size "$REC/recording.original.mp4")"
    say "  $(mux_state)"
    stop_writer; release_owner ;;
  orphan-writer)
    kill -9 $OWNER; wait $OWNER 2>/dev/null; say "owner pid $OWNER SIGKILLed; writer pid $FF still running: $(ps -p $FF >/dev/null && echo yes || echo no)"
    package orphan
    stop_writer
    package after ;;
  dead-owner)
    stop_writer
    kill -9 $OWNER; wait $OWNER 2>/dev/null; say "owner pid $OWNER SIGKILLed after its writer ended; capture.owner.json left behind: $(size "$REC/capture.owner.json") bytes"
    package recover ;;
  *) echo "unknown arm $ARM"; exit 2 ;;
esac
kill $OWNER 2>/dev/null; kill $FF 2>/dev/null
say "done; logs in $OUT"
