#!/usr/bin/env bash
# Issue #156 - run the live arm and both dead-owner arms IN THE SAME RUN, concurrently, against one
# binary and one container, and print the three transcripts in order.
#   both-arms.sh <agenteyes-binary> <scratch-dir> <plain|frag>
# See repro-cross-process.sh for what each arm does and what stands in for a capture.
set -u
BIN="$1"; OUT="$2"; CONTAINER="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
echo "binary: $("$BIN" --version 2>/dev/null | head -1 || echo "$BIN")"
echo "ffmpeg: $(ffmpeg -version | head -1)"
echo "container: $CONTAINER   host: $(hostname -s)   started: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
pids=()
for arm in live-package orphan-writer dead-owner; do
  rm -rf "$OUT/$arm"
  "$HERE/repro-cross-process.sh" "$BIN" "$OUT/$arm" "$CONTAINER" "$arm" > "$OUT/$arm.txt" 2>&1 &
  pids+=($!)
done
for p in "${pids[@]}"; do wait "$p"; done
for arm in live-package orphan-writer dead-owner; do echo; echo "######## $arm"; cat "$OUT/$arm.txt"; done
