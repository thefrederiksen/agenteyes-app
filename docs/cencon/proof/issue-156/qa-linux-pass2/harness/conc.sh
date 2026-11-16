#!/bin/bash
# QA #156 pass 2 - D2 under concurrency, on copies of REAL crashed recordings (fixtures made by qa2.sh
# after the start-up reap SIGTERMed the orphan, before any pass touched them: raw.mp4 complete,
# PendingMux pending, capture.owner.json naming a dead owner and a dead writer, capture.lock present).
# Usage: conc.sh <run-dir-A> <run-dir-B> <portbase>      (both from FIX crash runs)
set -u
SP=${SCRATCH:?set SCRATCH to an empty scratch directory holding shim/ and wt-pre/}
BIN=/home/soren/ReposFred/agenteyes-app/src/AgentEyes.Core/bin/x64/Release/net8.0/agenteyes
A=$1; B=$2; PB=$3
R=$SP/runs/conc-$(date -u +%H%M%S); mkdir -p "$R/home/.config" "$R/home/.local/share" "$R/home/Videos/AgentEyes" "$R/other/.local/share" "$R/w"
export HOME=$R/home XDG_CONFIG_HOME=$R/home/.config XDG_DATA_HOME=$R/home/.local/share DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000
LOG=$R/home/.local/share/AgentEyes/logs; OLOG=$R/other/.local/share/AgentEyes/logs; LIB=$R/home/Videos/AgentEyes
OTHER_ENV=(env LD_PRELOAD=$SP/shim/fakehost.so FAKE_HOSTNAME=otherhost XDG_DATA_HOME=$R/other/.local/share)
T=$R/transcript.txt
say() { printf '%s %s\n' "$(date -u +%H:%M:%S.%3N)" "$*" | tee -a "$T"; }
MYPIDS=(); cleanup() { for p in "${MYPIDS[@]}"; do kill -TERM "$p" 2>/dev/null; done; sleep 2; for p in "${MYPIDS[@]}"; do kill -0 "$p" 2>/dev/null && kill -9 "$p"; done; }; trap cleanup EXIT
st() {
  python3 - "$1" <<'PY'
import json, os, sys
d = sys.argv[1]; m = json.load(open(os.path.join(d, "manifest.json")))
pp = (m.get("PostProcessing") or {}).get("mux")
mux = "none" if not pp else "State=%s Attempts=%s nAttemptedBy=%d Error=%s" % (pp.get("State"), pp.get("Attempts"), len(pp.get("AttemptedBy") or []), (pp.get("Error") or "")[:100])
print("%s | owner.json=%s | PendingMux=%s | mux: %s | AttemptedBy=%s" % (os.path.basename(d), "present" if os.path.exists(os.path.join(d,"capture.owner.json")) else "absent",
      "pending" if m.get("PendingMux") else "cleared", mux, json.dumps((pp or {}).get("AttemptedBy"))))
PY
}
dec() { local f=$1/recording.mp4; [ -f "$f" ] || { echo "no recording.mp4"; return; }; ffmpeg -v error -i "$f" -f null - >/dev/null 2>"$R/de.err"; echo "decode exit=$? errs=$(wc -l <"$R/de.err") dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f") last-second frames=$(ffmpeg -v error -sseof -1 -i "$f" -map 0:v:0 -f framemd5 - 2>/dev/null | grep -vc '^#') streams=$(ffprobe -v error -show_entries stream=codec_type -of csv=p=0 "$f" | tr '\n' ',')"; }
pk() { "$BIN" package "$1" --no-frames > "$2" 2>&1; echo $? > "$2.rc"; }
opk() { "${OTHER_ENV[@]}" "$BIN" package "$1" --no-frames > "$2" 2>&1; echo $? > "$2.rc"; }
first() { echo "rc=$(cat "$1.rc") $(grep -m1 -iE 'refusing|mux .*failed|could not|held by|holds|\[error\]' "$1" | cut -c1-230)"; }

say "=== D2 concurrency arms; binary $(strings "$(dirname "$BIN")/agenteyes.dll" | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'); scratch $R"
pc=$("$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //'); case "$pc" in "$R"/*) say "isolation: this-host root $pc (inside scratch)";; *) say "ABORT root $pc"; exit 9;; esac
pco=$("${OTHER_ENV[@]}" "$BIN" housekeep 2>&1 | grep -m1 "root:" | sed 's/.*root: //'); case "$pco" in "$R"/*) say "isolation: other-host root $pco (inside scratch)";; *) say "ABORT other root $pco"; exit 9;; esac
n=0; for src in "$A"/fixtures/fx* "$B"/fixtures/fx*; do n=$((n+1)); cp -a "$src" "$R/w/f$n"; done
say "working copies: $n, from $(basename "$A") and $(basename "$B"); each starts as: $(st "$R/w/f1")"

# ---- Arm 1: the exact kernel lock ClearStaleClaim takes (shared flock on capture.lock), held across a whole counted finalize
say "--- arm 1: shared flock held on f1/capture.lock (what ClearStaleClaim holds) for the whole of a counted package"
flock -s "$R/w/f1/capture.lock" sleep 25 & FL=$!; MYPIDS+=($FL); sleep 0.5
say "  /proc/locks while held: $(grep -c . /proc/locks) lines; ours: $(awk -v p=$FL '$5==p' /proc/locks | head -1)"
pk "$R/w/f1" "$R/a1.log"; say "  package under the shared hold: $(first "$R/a1.log")"
say "  $(st "$R/w/f1")"; say "  $(dec "$R/w/f1")"; kill $FL 2>/dev/null; wait $FL 2>/dev/null

say "--- arm 1 positive control: EXCLUSIVE flock on f2/capture.lock (what a live capture holds, and what ClearStaleClaim's first version held)"
flock -x "$R/w/f2/capture.lock" sleep 25 & FL=$!; MYPIDS+=($FL); sleep 0.5
pk "$R/w/f2" "$R/a1c.log"; say "  package under the exclusive hold: $(first "$R/a1c.log")"
say "  $(st "$R/w/f2")"; kill $FL 2>/dev/null; wait $FL 2>/dev/null
pk "$R/w/f2" "$R/a1c2.log"; say "  package after the exclusive hold is released: $(first "$R/a1c2.log")"
say "  $(st "$R/w/f2")"; say "  $(dec "$R/w/f2")"

# ---- Arm 2: strace - which lock does ClearStaleClaim really take, and is the owner record removed under it?
say "--- arm 2: strace of a package on f3 (flock / unlink calls on capture.lock and capture.owner.json)"
strace -f -qq -e trace=openat,flock,close,unlink,unlinkat -o "$R/strace-f3.txt" "$BIN" package "$R/w/f3" --no-frames > "$R/a2.log" 2>&1
say "  package: rc=$? ; $(st "$R/w/f3")"
python3 - "$R/strace-f3.txt" <<'PY' | while read -r l; do say "  $l"; done
import re, sys
fd_of = {}; out = []
for line in open(sys.argv[1]):
    m = re.match(r'(\d+)\s+openat\(.*?"([^"]+capture\.lock)".*\)\s*=\s*(\d+)', line)
    if m: fd_of[(m.group(1), m.group(3))] = True; out.append("open capture.lock pid %s -> fd %s" % (m.group(1), m.group(3))); continue
    m = re.match(r'(\d+)\s+flock\((\d+),\s*([A-Z_|]+)\)\s*=\s*(-?\d+)', line)
    if m and (m.group(1), m.group(2)) in fd_of: out.append("flock(fd %s, %s) = %s" % (m.group(2), m.group(3), m.group(4))); continue
    m = re.match(r'(\d+)\s+unlink(at)?\(.*?"([^"]+capture\.owner\.json)".*\)\s*=\s*(-?\d+)', line)
    if m: out.append("unlink capture.owner.json = %s" % m.group(4)); continue
    m = re.match(r'(\d+)\s+close\((\d+)\)', line)
    if m and (m.group(1), m.group(2)) in fd_of: out.append("close capture.lock fd %s" % m.group(2)); del fd_of[(m.group(1), m.group(2))]
print("strace sequence: " + " ; ".join(out))
print("LOCK_EX on capture.lock: %d, LOCK_SH: %d" % (sum('LOCK_EX' in o for o in out), sum('LOCK_SH' in o for o in out)))
PY
strace -qq -e trace=flock -o "$R/strace-ctrl.txt" flock -x "$R/w/f3/capture.lock" true
say "  strace positive control (flock -x on the same file): $(grep -o 'flock([0-9]*, LOCK_EX)' "$R/strace-ctrl.txt" | head -1)"

# ---- Arm 3: stress - 3 same-host finalizers + 1 other-host finalizer fired at once on each recording
say "--- arm 3: f4..f$n, each hit at the same instant by 3 same-host packages and 1 other-host package"
for i in $(seq 4 $n); do
  pk "$R/w/f$i" "$R/s$i-a.log" & pk "$R/w/f$i" "$R/s$i-b.log" & pk "$R/w/f$i" "$R/s$i-c.log" & opk "$R/w/f$i" "$R/s$i-o.log" &
done; wait
bad=0
for i in $(seq 4 $n); do
  s=$(st "$R/w/f$i"); d=$(dec "$R/w/f$i")
  say "  $s"; say "    $d"
  for k in a b c o; do say "    $k: $(first "$R/s$i-$k.log")"; done
  echo "$s" | grep -q "State=done Attempts=1 nAttemptedBy=1" && echo "$s" | grep -q "owner.json=absent" && echo "$d" | grep -q "decode exit=0 errs=0" || { bad=$((bad+1)); say "    ^^^ INVARIANT BROKEN"; }
done
say "  arm 3 invariant (done, Attempts=1, one AttemptedBy, owner record gone, full decode): $((n-3-bad)) of $((n-3)) hold"
say "  same-host ClearStaleClaim CLEARED lines: $(grep -h 'ClearStaleClaim' "$LOG"/*.log | grep -c CLEARED) ; 'removed by another process first': $(grep -h 'ClearStaleClaim' "$LOG"/*.log | grep -c 'removed by another process first')"
say "  OTHER-host ClearStaleClaim lines: total $(cat "$OLOG"/*.log 2>/dev/null | grep -c 'ClearStaleClaim') ; of them CLEARED: $(cat "$OLOG"/*.log 2>/dev/null | grep 'ClearStaleClaim' | grep -c CLEARED)"
say "  other-host outcomes: declined $(grep -l 'cannot be checked from otherhost' "$R"/s*-o.log | wc -l), passed the claim $(grep -L 'cannot be checked from otherhost' "$R"/s*-o.log | wc -l)"
say "  any process anywhere that counted a FAILED mux: $(grep -hl -iE 'mux.*fail|NoteFailed|could not read the audio' "$R"/s*.log 2>/dev/null | wc -l)"
cat "$LOG"/*.log | grep -E "ThrowIfLive DECLINED" | grep -c "/w/f" | xargs -I{} say "  same-host declines logged: {}"
say "=== done; transcript $T"
