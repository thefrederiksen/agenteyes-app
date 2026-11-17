#!/bin/bash
# QA #116 pass 3, continuation seat: one launch -> handoff -> (optional capture) -> Quit through the tray menu item's click -> what is left.
# usage: run.sh <name> <capture:0|1>
S=$(cd "$(dirname "$0")" && pwd); name=$1; cap=$2
REPO=/Users/<user>/ReposFred/agenteyes-qa-mac; APP=$REPO/src/AgentEyes.DesktopApp/bin/Release/net8.0
INST="$HOME/Library/Application Support/AgentEyes/bin/agenteyes"
LOG="$HOME/Library/Application Support/AgentEyes/logs/AgentEyes-$(date +%Y%m%d).log"
SOCK="${TMPDIR}agenteyes-app.sock"; R=$S/run/$name; rm -rf "$R"; mkdir -p "$R"
ts(){ date +%H:%M:%S; }
echo "===== run $name  $(ts)"
ioreg -n Root -d1 | grep -o '"CGSSessionScreenIsLocked"=[A-Za-z]*' || echo "lock key ABSENT (grep exit $?) = not locked"
st=$(curl -s -m 5 http://127.0.0.1:7882/status | python3 -c "import json,sys;print(json.load(sys.stdin)['State'])"); echo "recorder /status State=$st"
[ "$st" = idle ] || { echo "[STOP] recorder is not idle - not launching"; exit 3; }
pgrep -fl AgentEyesDesktop && { echo "[STOP] an AgentEyesDesktop is already running"; exit 3; }
echo "socket before: $(ls -la "$SOCK" 2>&1)"
$S/winprobe | grep "layer=25" | sort > "$R/items-before.txt"; echo "layer-25 windows before launch: $(wc -l < "$R/items-before.txt")"
loglines=$(wc -l < "$LOG")
cd "$APP"
( DOTNET_ROOT=$HOME/.dotnet DOTNET_ROLL_FORWARD=LatestMajor DOTNET_STARTUP_HOOKS=$S/quithook/out/QaQuitHook.dll QA_OUT="$R" ./AgentEyesDesktop > "$R/app.out" 2>&1; echo $? > "$R/exit.txt" ) &
for i in $(seq 1 100); do pid=$(pgrep -f "^./AgentEyesDesktop$" | head -1); [ -n "$pid" ] && tail -n +$((loglines+1)) "$LOG" | grep -q "\[pid $pid\].*\[MainWindow\] Opened" && break; sleep 0.2; done
echo "$(ts) app pid=$pid"; tail -n +$((loglines+1)) "$LOG" | grep "\[pid $pid\]" | grep -E "Main:|SingleInstance|MainWindow\] Opened|TrayController|PollAsync|GetDevicesAsync:"
sleep 2
echo "--- window server, pid $pid"; $S/winprobe $pid; echo "probe exit=$?"
$S/winprobe | grep "layer=25" | sort > "$R/items-running.txt"; echo "layer-25 windows while running: $(wc -l < "$R/items-running.txt"); NEW since before launch:"; comm -13 "$R/items-before.txt" "$R/items-running.txt"
item=$(comm -13 "$R/items-before.txt" "$R/items-running.txt" | sed -n 's/.*window=\([0-9]*\).*/\1/p' | head -1)
win=$($S/winprobe $pid | sed -n 's/.*window=\([0-9]*\) layer=0.*/\1/p' | head -1)
echo "--- second launch $(ts): ./AgentEyesDesktop --view library   (no hook)"
DOTNET_ROOT=$HOME/.dotnet DOTNET_ROLL_FORWARD=LatestMajor ./AgentEyesDesktop --view library > "$R/second.out" 2>&1; echo "second launch exit=$?"
sleep 0.5; tail -n +$((loglines+1)) "$LOG" | grep -E "sent it 'view:library'|Accept: 'view:library'|Run: view:library|RunCommand: view library|Rail_Changed: Library"
echo "AgentEyesDesktop processes now: $(pgrep -fl AgentEyesDesktop | tr '\n' ';')"
echo "socket while running: $(ls -la "$SOCK" 2>&1)"
if [ "$cap" = 1 ]; then
  echo "--- capture by window id through the documented one-shot launchd job (installed binary + QaCaptureHook built from the committed source)"
  echo "installed sha256 before: $(shasum -a 256 "$INST" | cut -c1-64)"
  echo "$(ts) GET /health $(curl -s -m 5 http://127.0.0.1:7882/health | tr -d ' \n')"
  mkdir -p "$R/cap"
  python3 -I $S/oneshot.py com.agenteyes.qa116.cont-$name 30 "$R/cap" "[\"$INST\", \"--version\"]" "{\"DOTNET_STARTUP_HOOKS\": \"$S/caphook/out/QaCaptureHook.dll\", \"QA_OUT\": \"$R/cap\", \"QA_CAPTURES\": \"win|-x -o -l $win;item|-x -o -l $item\", \"PATH\": \"/usr/bin:/bin:/usr/sbin:/sbin\"}"
  echo "$(ts) GET /health $(curl -s -m 5 http://127.0.0.1:7882/health | tr -d ' \n')"
  echo "--- hook report"; cat "$R/cap/hook-report.txt"
  echo "installed sha256 after:  $(shasum -a 256 "$INST" | cut -c1-64)"
  echo "recorder still: $(pgrep -fl 'agenteyes serve')"
fi
mark=$(wc -l < "$LOG")
echo "--- $(ts) trigger: raise the tray menu's Quit click"; touch "$R/quit.trigger"
for i in $(seq 1 150); do [ -f "$R/exit.txt" ] && break; sleep 0.2; done
if [ -f "$R/exit.txt" ]; then echo "$(ts) process exit code = $(cat "$R/exit.txt")"; else echo "$(ts) [FAIL] the app did NOT exit within 30 s of the Quit click - sampling and killing it"; sample $pid 2 -file "$R/sample.txt" >/dev/null 2>&1; kill -KILL $pid; fi
echo "--- hook transcript"; cat "$R"/quit-hook-*.txt
echo "--- every log line from pid $pid after the trigger, unfiltered"; tail -n +$((mark+1)) "$LOG" | grep "\[pid $pid\]"
echo "--- lines of any pid after the trigger that are NOT tagged with pid $pid (a clipped line loses its tag, #143): $(tail -n +$((mark+1)) "$LOG" | grep -v "\[pid $pid\]" | grep -c -E "SingleInstance|App\]|Program\]|DeferredQuit")"
echo "socket after: $(ls -la "$SOCK" 2>&1)"
echo "AgentEyesDesktop processes after: $(pgrep -fl AgentEyesDesktop | tr '\n' ';')(end)"
$S/winprobe | grep "layer=25" | sort > "$R/items-after.txt"; echo "layer-25 windows after quit: $(wc -l < "$R/items-after.txt"); status item window $item still listed: $(grep -c "window=$item " "$R/items-after.txt")"
$S/winprobe $pid; echo "probe of the dead pid exit=$? (2 = no window, as expected)"
echo "app.out bytes: $(wc -c < "$R/app.out")"; head -c 600 "$R/app.out"

if [ "$cap" = 1 ] && [ "$(grep -c "window=$item " "$R/items-after.txt")" = 1 ]; then
  echo "--- the status item window $item after the app is gone, captured the same way"
  grep "window=$item " "$R/items-after.txt"
  mkdir -p "$R/cap2"
  python3 -I $S/oneshot.py com.agenteyes.qa116.cont-$name-b 30 "$R/cap2" "[\"$INST\", \"--version\"]" "{\"DOTNET_STARTUP_HOOKS\": \"$S/caphook/out/QaCaptureHook.dll\", \"QA_OUT\": \"$R/cap2\", \"QA_CAPTURES\": \"item-after|-x -o -l $item\", \"PATH\": \"/usr/bin:/bin:/usr/sbin:/sbin\"}"
  cat "$R/cap2/hook-report.txt"; echo "installed sha256 after:  $(shasum -a 256 "$INST" | cut -c1-64)"
fi
echo "qa116 launchd labels left: $(launchctl list | grep -c qa116)"
