#!/bin/bash
source "$(dirname "$0")/qa-check.sh"
for c in "record start" "record stop" "record status" screens shot audio video package compose always-on housekeep import translate subtitle selftest serve signin signout account; do
  qcheck $c --help
done
qcheck record start -h
qcheck record start help
qcheck record --help
qcheck help record start
qcheck --help
qcheck -h
qcheck record start --audio-only -h
qcheck record start --mic -h
qcheck record start --mic help
qcheck RECORD START --HELP
# refusals
qcheck record start --nonsense
qcheck record start --audio-only --nonsense
qcheck record start -x
qcheck record start hepl
qcheck record start --mic
qcheck record start --mic=Yeti
qcheck record start --fps=30
qcheck record start --region
qcheck record stop --audio-only
qcheck video --nonsense
qcheck serve --nonsense
qcheck shot --nonsense
qcheck selftest --nonsense
qcheck signout --nonsense
qcheck record --nonsense start
