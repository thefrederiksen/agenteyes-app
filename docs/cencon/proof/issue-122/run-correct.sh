#!/bin/bash
# Criterion 5 on the installed binary. Optional: MAIN_AGENTEYES=<path to main's agenteyes> for the baseline.
source $(dirname $0)/check.sh
R=~/Videos/AgentEyes
mode_of() { d=$(ls -1td $R/*/ | head -1); echo "--- newest dir: $d"; echo "--- manifest.json: $(grep -oE '"Mode": *"[^"]*"' ${d}manifest.json)"; }
echo "Real-binary run (correct invocations) for issue #122 on $(hostname), $(date -Is)"
check record start --screen 1 --mic "Family 17h"
sleep 4
check record stop
sleep 3; mode_of
check record start --audio-only --mic "Family 17h"
sleep 4
check record stop
sleep 3; mode_of
check record status
echo
echo "The criterion's bare form, through this binary - the service refuses it (no source):"
check record start --audio-only
if [[ -n "${MAIN_AGENTEYES:-}" ]]; then
  echo
  echo "BASELINE: the same bare form through main's binary (built from a clean worktree of main, df81375):"
  echo "\$ <main>/agenteyes record start --audio-only"
  echo "--- before: $(ls -1 $R | wc -l) entries"
  "$MAIN_AGENTEYES" record start --audio-only 2>&1 | sed 's/^/    /'; echo "--- exit code: ${PIPESTATUS[0]}"
  echo "--- after:  $(ls -1 $R | wc -l) entries"
  echo "--- GET /status: $(curl -s 127.0.0.1:7882/status | grep -oE '"(State|Dir)": [^,]*' | tr '\n' ' ')"
fi
