#!/bin/sh
# QA #173 round 3: re-run the corpus under the round-2 guard, three mutations, and a threshold sweep.
# Run from the repo root. Copies go to a temp dir; the tree is not modified.
set -e
T=$(mktemp -d)
mk() { mkdir -p "$T/$1/scripts" "$T/$1/tests"; cp -R tests/evidence-identity-corpus "$T/$1/tests/"; cp scripts/test-check-proof-identity.py "$T/$1/scripts/"; }
count() { python3 "$T/$1/scripts/test-check-proof-identity.py" "$T/$1" | grep -c '^\[FAIL\] [gdr]' || true; }
mk r2; git show 06e9ade:scripts/check-proof-identity.py > "$T/r2/scripts/check-proof-identity.py"
mk nowidth; sed 's/    return match.start(2) - line_start >= WRAP_WIDTH/    return True/' scripts/check-proof-identity.py > "$T/nowidth/scripts/check-proof-identity.py"
mk nojoin; awk '{print} /^    content_end = match.start\(\)$/ {print "    return False"}' scripts/check-proof-identity.py > "$T/nojoin/scripts/check-proof-identity.py"
mk noutf16; awk '/^    for m in UTF16LE_RUN.finditer\(data\):$/ {print "    return"} {print}' scripts/check-proof-identity.py > "$T/noutf16/scripts/check-proof-identity.py"
for m in r2 nowidth nojoin noutf16; do echo "$m: $(count $m) corpus files fail"; done
for w in 79 72 60 40; do mk w$w; sed "s/^WRAP_WIDTH = 80/WRAP_WIDTH = $w/" scripts/check-proof-identity.py > "$T/w$w/scripts/check-proof-identity.py"
  echo "WRAP_WIDTH=$w: corpus fails $(count w$w); whole tree $(python3 "$T/w$w/scripts/check-proof-identity.py" . | grep -c '^\[FAIL\] docs' || true) hits"; done
rm -rf "$T"
