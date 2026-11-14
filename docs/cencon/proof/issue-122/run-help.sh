#!/bin/bash
source $(dirname $0)/check.sh
echo "Real-binary run for issue #122 on $(hostname), $(date -Is). agenteyes -> $(readlink -f $(which agenteyes))"
# criterion 1, 3, 6
check record start --help
check record start -h
check record start help
# criterion 2: every other subcommand
for c in "record stop" "record status" screens shot audio video package compose always-on housekeep import translate subtitle selftest serve signin signout account; do
  check $c --help
done
check record --help
check help record start
check --help
# criterion 4
check record start --nonsense
check record start --audio-only --nonsense
check record start -x
check record start hepl
check record start --mic
check record start --mic help
check record start --mic=Yeti
check video --nonsense
check serve --nonsense
