#!/bin/zsh
# Issue #161 proof on macOS. Every agenteyes run is under sandbox-exec with the two REAL roots denied.
set -u
S=<scratch>/p161
OUT=$S/run
rm -rf "$OUT"; mkdir -p "$OUT"
BIN=/Users/<owner account>/ReposFred/agenteyes-dev161/src/AgentEyes.Core/bin/Release/net8.0/agenteyes.dll
DN=/Users/<owner account>/.dotnet/dotnet
REAL_V="/Users/<owner account>/Movies/AgentEyes"
REAL_D="/Users/<owner account>/Library/Application Support/AgentEyes"
PROFILE="(version 1)(allow default)(deny file-read* file-write* (subpath \"$REAL_V\") (subpath \"$REAL_D\"))"
export DOTNET_ROLL_FORWARD=Major
unset AGENTEYES_REDIRECT_DATA AGENTEYES_REDIRECT_VIDEOS
DATA=$OUT/data; VIDEOS=$OUT/videos
T0="$(date '+%Y-%m-%d %H:%M:%S')"
touch "$OUT/t0"
echo "T0=$T0"
echo "== 0. SANDBOX CONTROL: the profile must block ls and touch on BOTH real roots"
for d in "$REAL_V" "$REAL_D"; do
  sandbox-exec -p "$PROFILE" /bin/ls "$d" >/dev/null 2>"$OUT/ctl.err"; echo "ls    '$d' -> exit $? : $(cat $OUT/ctl.err)"
  sandbox-exec -p "$PROFILE" /usr/bin/touch "$d/.agenteyes161-sandbox-probe" 2>"$OUT/ctl.err"; echo "touch '$d/.agenteyes161-sandbox-probe' -> exit $? : $(cat $OUT/ctl.err)"
  if [ -e "$d/.agenteyes161-sandbox-probe" ]; then echo "ABORT: the profile did NOT block a write to $d"; exit 99; fi
  echo "  probe file absent afterwards: yes"
done
# And the same profile does NOT block the scratch area (it is not a deny-everything profile).
sandbox-exec -p "$PROFILE" /usr/bin/touch "$OUT/scratch-ok"; echo "touch scratch -> exit $?"

echo "== 1. RUNTIME HOME TRAP (standalone probe, touches nothing)"
mkdir -p "$OUT/home"
echo "-- HOME unchanged:"; $DN $S/../probe/out/probe.dll
echo "-- HOME=$OUT/home:"; HOME=$OUT/home $DN $S/../probe/out/probe.dll

echo "== 2. REAL LIBRARY BASELINE (directory-entry count only, read-only, before any agenteyes run)"
echo "recording folders in $REAL_V: $(find "$REAL_V" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')"
echo "owner's service /status (7882):"; curl -s --max-time 5 http://127.0.0.1:7882/status | grep -E '"State"|"PendingTranscriptions"|"SignedIn"'

redirected() { sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_DATA="$DATA" AGENTEYES_REDIRECT_VIDEOS="$VIDEOS" "$@"; }

echo "== 3. SCRATCH LIBRARY: two imports of a clip this script made with ffmpeg lavfi"
/opt/homebrew/bin/ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=duration=3:size=320x240:rate=10 -f lavfi -i sine=frequency=440:duration=3 -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac "$OUT/clip.mp4"; echo "ffmpeg lavfi -> exit $?"
mkdir -p "$DATA/AgentEyes"; echo '{"Port": 17961}' > "$DATA/AgentEyes/config.json"
for i in 1 2; do
  redirected $DN $BIN import "$OUT/clip.mp4" >"$OUT/import$i.out" 2>"$OUT/import$i.err"; echo "import $i -> exit $?"
  echo "  stdout:"; sed 's/^/    /' "$OUT/import$i.out"; echo "  stderr:"; sed 's/^/    /' "$OUT/import$i.err"
  sleep 1
done
echo "scratch library:"; find "$VIDEOS" -maxdepth 2 | sed "s|^|  |"
REC1=$(find "$VIDEOS/AgentEyes" -mindepth 1 -maxdepth 1 -type d | sort | head -1)
echo "-- package (redirected) on $REC1:"
redirected $DN $BIN package "$REC1" >"$OUT/package.out" 2>"$OUT/package.err"; echo "package -> exit $?"
sed 's/^/    /' "$OUT/package.out" | tail -5; sed 's/^/    /' "$OUT/package.err"

echo "== 4. REFUSALS"
echo "-- partial (DATA only):"
sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_DATA="$OUT/p/data" $DN $BIN serve --port 17963 >"$OUT/partial.out" 2>"$OUT/partial.err"; echo "exit $?"; cat "$OUT/partial.err"
echo "   $OUT/p exists afterwards: $([ -e $OUT/p ] && echo YES || echo no)"
echo "-- partial (VIDEOS only):"
sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_VIDEOS="$OUT/p/videos" $DN $BIN record status >"$OUT/partial2.out" 2>"$OUT/partial2.err"; echo "exit $?"; cat "$OUT/partial2.err"
echo "-- VIDEOS = the real Movies folder (scratch state + REAL recordings):"
sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_DATA="$OUT/m/data" AGENTEYES_REDIRECT_VIDEOS=/Users/<owner account>/Movies $DN $BIN serve --port 17963 2>&1 & VPID=$!; wait $VPID; echo "exit $? (pid $VPID)"
echo "-- redirected serve on the default port (scratch config has no Port):"
sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_DATA="$OUT/dp/data" AGENTEYES_REDIRECT_VIDEOS="$OUT/dp/videos" $DN $BIN serve 2>&1 | grep -v "^\[redirect\]"; echo "exit ${pipestatus[1]}"

echo "-- scratch videos/AgentEyes is a SYMLINK to the real recordings root (creating the link does not touch its target):"
mkdir -p "$OUT/lk/videos"; ln -s "$REAL_V" "$OUT/lk/videos/AgentEyes"
sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_DATA="$OUT/lk/data" AGENTEYES_REDIRECT_VIDEOS="$OUT/lk/videos" $DN $BIN serve --port 17963 2>&1 & LPID=$!; wait $LPID; echo "exit $? (pid $LPID)"

echo "== 5. REDIRECTED SERVE (port 17961) - repair pass + housekeeper"
# Started DIRECTLY (not through the shell function) so $! is the service itself: sandbox-exec and env
# both exec, keeping the pid. A backgrounded function would make $! a subshell's pid instead.
sandbox-exec -p "$PROFILE" env AGENTEYES_REDIRECT_DATA="$DATA" AGENTEYES_REDIRECT_VIDEOS="$VIDEOS" $DN $BIN serve --port 17961 >"$OUT/serve.out" 2>"$OUT/serve.err" &
SPID=$!
sleep 2; echo "serve pid=$SPID: $(ps -p $SPID -o command= )"
sleep 28
echo "-- GET /status:"; curl -s --max-time 10 http://127.0.0.1:17961/status > "$OUT/status.json"; echo "curl exit $?"
grep -E '"State"|"SignedIn"|"PendingTranscriptions"|"DataRoot"|"RecordingsRoot"|"LibraryRedirect' "$OUT/status.json"
echo "-- redirected 'agenteyes record status' (client on the same scratch library):"
redirected $DN $BIN record status >"$OUT/rs.out" 2>"$OUT/rs.err"; echo "exit $?"; cat "$OUT/rs.out" "$OUT/rs.err"
kill -TERM $SPID; wait $SPID; echo "serve exit=$?"
echo "-- serve stdout:"; cat "$OUT/serve.out"; echo "-- serve stderr:"; cat "$OUT/serve.err"
SLOG=$(ls "$DATA/AgentEyes/logs/"*.log)
echo "-- scratch log: $SLOG ($(wc -l < $SLOG | tr -d ' ') lines); lines from serve pid $SPID that name roots or the libraries examined:"
grep "\[pid $SPID\]" "$SLOG" | grep -E "LibraryRedirect|HeadlessHost\] Run: starting|LIBRARY REDIRECTED|ReapOrphans|ResumeUnfinishedAsync|ResumeAsync|PostRecording\] Resume: dir|Housekeeper\] Run|RepairService\] RunAsync|CliDispatcher\] Run: args|FindPending" | sed 's/^/  /'

echo "== 6. POSITIVE CONTROL + PRODUCT HOME TRAP: same binary, NO redirect, HOME=$OUT/home, real roots denied"
HOME=$OUT/home sandbox-exec -p "$PROFILE" $DN $BIN serve --port 17962 >"$OUT/ctl-serve.out" 2>"$OUT/ctl-serve.err" &
CPID=$!
sleep 2; echo "control serve pid=$CPID: $(ps -p $CPID -o command= )"
sleep 28
echo "-- GET /status:"; curl -s --max-time 10 http://127.0.0.1:17962/status > "$OUT/ctl-status.json"; echo "curl exit $?"
grep -E '"State"|"SignedIn"|"PendingTranscriptions"|"DataRoot"|"RecordingsRoot"|"LibraryRedirect' "$OUT/ctl-status.json"
kill -TERM $CPID; wait $CPID; echo "control serve exit=$?"
echo "-- control stdout:"; cat "$OUT/ctl-serve.out"; echo "-- control stderr:"; cat "$OUT/ctl-serve.err"
echo "-- did the control's log land anywhere? files named AgentEyes-*.log under $OUT/home and $OUT (excluding the redirected run's):"
find "$OUT/home" "$OUT" -name 'AgentEyes-*.log' -not -path "$DATA/*" | sed 's/^/  /'

sleep 3
echo "== 7. KERNEL SANDBOX DENIALS since T0"
/usr/bin/log show --start "$T0" --style compact --predicate 'eventMessage CONTAINS "Sandbox:" AND eventMessage CONTAINS "deny("' 2>/dev/null | grep "Sandbox:" > "$OUT/denials.txt"
echo "total denial lines: $(wc -l < $OUT/denials.txt | tr -d ' ')"
echo "-- by process(pid):"; sed -E 's/.*Sandbox: ([^ ]+) deny.*/\1/' "$OUT/denials.txt" | sort | uniq -c
echo "-- lines for the redirected serve pid $SPID: $(grep -c "($SPID)" $OUT/denials.txt)"
echo "-- dotnet processes with ANY denial (every agenteyes run above was a dotnet process under this profile):"
grep -o "dotnet([0-9]*)" "$OUT/denials.txt" | sort | uniq -c | sed 's/^/  /'
echo "   (expected: only the control pid $CPID; the symlink-refusal pid $LPID, whose refusal resolved the link INTO the denied real root; and the VIDEOS=~/Movies refusal pid $VPID, whose redirected root IS the real root and was stat-ed to resolve links)"
echo "-- lines for the VIDEOS=~/Movies refusal pid $VPID:"; grep "($VPID)" "$OUT/denials.txt" | sed -E 's/.*(deny.*)/  \1/' 
echo "-- lines for the symlink-refusal pid $LPID:"; grep "($LPID)" "$OUT/denials.txt" | sed -E 's/.*(deny.*)/  \1/' 
echo "-- lines for the control serve pid $CPID: $(grep -c "($CPID)" $OUT/denials.txt); distinct denied paths:"
grep "($CPID)" "$OUT/denials.txt" | sed -E 's/.*deny\(1\) [^ ]+ //' | sort -u | sed 's/^/  /'

echo "== 8. REAL RECORDINGS LIBRARY AFTER: entries modified since T0 (metadata only)"
find "$REAL_V" -newer "$OUT/t0" | sed 's/^/  /'; echo "(end of list) recording folders now: $(find "$REAL_V" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')"
echo "owner's service /status (7882) after:"; curl -s --max-time 5 http://127.0.0.1:7882/status | grep -E '"State"|"PendingTranscriptions"'
ps -p 50642 -o pid,command | tail -1
echo "my agenteyes leftovers:"; pgrep -fl "agenteyes.dll" || echo "  none"
