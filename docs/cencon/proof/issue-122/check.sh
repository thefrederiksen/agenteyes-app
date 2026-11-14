#!/bin/bash
# One measured check: recordings dir before, the command, its exit code, the dir after, GET /status.
R=~/Videos/AgentEyes
check() {
  echo "################################################################"
  echo "\$ agenteyes $*"
  echo "--- before: $(ls -1 $R | wc -l) entries; newest 3:"; ls -1 --full-time -d $R/* | sort -k6,7 | tail -3 | awk '{print "    "$6" "$7" "$9}'
  echo "--- output (stdout+stderr):"
  agenteyes "$@" 2>&1 | sed 's/^/    /'
  echo "--- exit code: ${PIPESTATUS[0]}"
  echo "--- after:  $(ls -1 $R | wc -l) entries; newest 3:"; ls -1 --full-time -d $R/* | sort -k6,7 | tail -3 | awk '{print "    "$6" "$7" "$9}'
  echo "--- GET /status: $(curl -s 127.0.0.1:7882/status | grep -oE '"(State|Dir)": [^,]*' | tr '\n' ' ')"
}
