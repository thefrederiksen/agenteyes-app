#!/bin/bash
# QA measured check: BIN args... -> dir count + newest 5 before/after, output, exit code, /status State
R=$HOME/Videos/AgentEyes
BIN=${BIN:-$HOME/.local/bin/agenteyes}
snap() { echo "    count=$(ls -1 $R | wc -l)  newest5(ls -1 | tail -5): $(ls -1 $R | tail -5 | tr '\n' ' ')"; }
qcheck() {
  echo "################################################################"
  echo "\$ agenteyes $*"
  echo "--- before:"; snap
  local before=$(ls -1 $R | wc -l)
  echo "--- output:"
  "$BIN" "$@" > /tmp/qa_out.$$ 2>&1; local rc=$?
  sed 's/^/    /' /tmp/qa_out.$$; rm -f /tmp/qa_out.$$
  echo "--- exit code: $rc"
  echo "--- after:"; snap
  local after=$(ls -1 $R | wc -l)
  local st=$(curl -s 127.0.0.1:7882/status | grep -oE '"State": "[a-z]+"')
  echo "--- GET /status: $st"
  echo "SUMMARY | agenteyes $* | exit=$rc | count $before -> $after | $st"
}
