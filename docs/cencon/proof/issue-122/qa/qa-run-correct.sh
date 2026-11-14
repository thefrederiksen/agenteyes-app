#!/bin/bash
source "$(dirname "$0")/qa-check.sh"
S="$(dirname "$0")"
mode() { local d=$(curl -s 127.0.0.1:7882/status | grep -oE '"LastStopDir": "[^"]*"' | cut -d'"' -f4); echo "--- LastStopDir=$d"; [ -n "$d" ] && echo "--- manifest: $(grep -oE '"Mode": *"[a-z]+"' "$d/manifest.json" | head -1)"; }
for which in main branch; do
  if [ $which = main ]; then BIN=$S/stage-main/agenteyes; else BIN=$HOME/.local/bin/agenteyes; fi
  echo "==================== BINARY: $which ($BIN) ===================="
  qcheck record start --audio-only
  qcheck record start --screen 1 --mic "Family 17h"
  sleep 4
  qcheck record stop
  sleep 1; mode
  qcheck record start --audio-only --mic "Family 17h"
  sleep 4
  qcheck record stop
  sleep 1; mode
  qcheck record status
done
