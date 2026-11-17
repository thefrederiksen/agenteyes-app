#!/bin/bash
# QA #142 macOS app rows: photograph windows BY ID from a one-shot launchd job running the INSTALLED
# service binary (the one holding the Screen Recording grant) with the committed QaCaptureHook.
# usage: cap.sh <jobname> "<name>|<window id>;<name>|<window id>..."
# Each job: bootstrapped, polled with a 40 s hard timeout, ALWAYS booted out (oneshot.py).
set -u
T=$(cd "$(dirname "$0")" && pwd); S=$(dirname "$T")
INST="$HOME/Library/Application Support/AgentEyes/bin/agenteyes"
job=$1; specs=$2; out=$S/cap/$job; mkdir -p "$out"
caps=""; IFS=';' read -ra parts <<< "$specs"
for p in "${parts[@]}"; do caps="$caps${p%%|*}|-x -o -l ${p##*|};"; done
echo "=== capture job com.agenteyes.qa142.$job $(date +%H:%M:%S)  captures: $caps"
echo "service before: pid=$(pgrep -f 'agenteyes serve' | tr '\n' ' ') sha256=$(shasum -a 256 "$INST" | cut -c1-16) health=$(curl -s -m 5 http://127.0.0.1:7882/health | tr -d ' \n')"
python3 -I "$T/oneshot.py" "com.agenteyes.qa142.$job" 40 "$out" "[\"$INST\", \"--version\"]" \
  "{\"DOTNET_STARTUP_HOOKS\": \"$T/hook/out/QaCaptureHook.dll\", \"QA_OUT\": \"$out\", \"QA_CAPTURES\": \"$caps\", \"PATH\": \"/usr/bin:/bin:/usr/sbin:/sbin\"}"
echo "--- hook report"; cat "$out/hook-report.txt" 2>&1
echo "service after:  pid=$(pgrep -f 'agenteyes serve' | tr '\n' ' ') sha256=$(shasum -a 256 "$INST" | cut -c1-16) health=$(curl -s -m 5 http://127.0.0.1:7882/health | tr -d ' \n')"
echo "qa142 launchd labels left: $(launchctl list | grep -c qa142)"
