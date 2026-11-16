#!/usr/bin/env bash
# Regenerate assets/AgentEyes.icns, the macOS bundle icon (issue #118), from assets/icon-256.png.
#
#   scripts/mac/make-icns.sh
#
# The .icns is committed, so a build never needs this; run it only when assets/icon-256.png changes.
#
# WHICH SIZES, AND WHY NOT ALL OF THEM. The only source is 256x256. The iconset gets every size that is
# 256 pixels or smaller (16, 32, 64, 128 and 256 pixels, as the 1x and 2x slots allow) - each one a
# DOWN-scale of the source. It deliberately has NO 256@2x, 512 or 512@2x slot: those would be the
# 256-pixel source blown up to 512 or 1024, which is a blurry icon wearing a sharp icon's name. macOS
# scales the largest slot present when it needs a bigger one (Finder's largest icon view, Quick Look),
# exactly as it would scale an upscaled slot, so leaving them out loses nothing and claims nothing.
# A sharp icon at those sizes needs a 1024x1024 source; that is a request to the lead, not a script fix.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
src="$repo/assets/icon-256.png"
out="$repo/assets/AgentEyes.icns"

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

[[ "$(uname -s)" == "Darwin" ]] || fail "iconutil and sips exist only on macOS"
[[ -f "$src" ]] || fail "missing $src"
w="$(sips -g pixelWidth "$src" | awk '/pixelWidth/ {print $2}')"
h="$(sips -g pixelHeight "$src" | awk '/pixelHeight/ {print $2}')"
[[ "$w" == "256" && "$h" == "256" ]] || fail "$src is ${w}x${h}; this script expects 256x256 (change the slot list below for another source)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
set_dir="$work/AgentEyes.iconset"
mkdir "$set_dir"

# slot-name pixels
while read -r name px; do
    sips -z "$px" "$px" "$src" --out "$set_dir/$name.png" >/dev/null || fail "sips could not make $name ($px px)"
done <<'EOF'
icon_16x16 16
icon_16x16@2x 32
icon_32x32 32
icon_32x32@2x 64
icon_128x128 128
icon_128x128@2x 256
icon_256x256 256
EOF

iconutil -c icns "$set_dir" -o "$out" || fail "iconutil could not build $out"
ok "wrote $out ($(wc -c < "$out" | tr -d ' ') bytes)"
