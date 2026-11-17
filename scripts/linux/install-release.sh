#!/usr/bin/env bash
# AgentEyes @VERSION@ for Linux: download, VERIFY, and install the release (issue #194).
#
# This file is a TEMPLATE. scripts/linux/build-release.sh writes it into the release as
# agenteyes-install-linux-x64.sh with @VERSION@ replaced, so the script you download always
# installs the release it came from:
#
#   curl -fsSLO https://github.com/thefrederiksen/agenteyes-app/releases/latest/download/agenteyes-install-linux-x64.sh
#   bash agenteyes-install-linux-x64.sh
#
# What it does, in order, and it stops at the first thing that is wrong:
#   1. fetches release-manifest.json and agenteyes-linux-x64.tar.gz from that release
#      (or takes both from --release-dir DIR, a folder holding a built release, for offline use)
#   2. checks the tarball's size and SHA-256 against release-manifest.json - a corrupt or altered
#      download is REFUSED here, before anything is extracted or installed
#   3. extracts it and runs its scripts/linux/install.sh --prebuilt, which installs the recorder as a
#      systemd USER service and the desktop app for THIS user (nothing system-wide; ffmpeg and libvlc
#      come from the distribution and are the only things that ask for root)
#
# Options: --release-dir DIR, and install.sh's own --no-start and --no-signin, passed through.
set -euo pipefail

version="@VERSION@"
asset="agenteyes-linux-x64.tar.gz"
manifest_name="release-manifest.json"
base_url="https://github.com/thefrederiksen/agenteyes-app/releases/download/v$version"

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

[[ "$version" != "@"*"@" ]] || fail "this is the unbuilt template (scripts/linux/install-release.sh); download agenteyes-install-linux-x64.sh from a release instead"

release_dir=""
pass=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --release-dir)
            [[ $# -ge 2 ]] || fail "--release-dir needs a folder"
            release_dir="$2"
            shift ;;
        --no-start|--no-signin) pass+=("$1") ;;
        *) fail "unknown option: $1 (options: --release-dir <dir>, --no-start, --no-signin)" ;;
    esac
    shift
done

for tool in python3 sha256sum tar; do
    command -v "$tool" >/dev/null || fail "$tool is not installed (sudo apt install -y $tool)"
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "AgentEyes $version for Linux"
if [[ -n "$release_dir" ]]; then
    [[ -f "$release_dir/$manifest_name" ]] || fail "no $manifest_name in $release_dir"
    [[ -f "$release_dir/$asset" ]] || fail "no $asset in $release_dir"
    cp "$release_dir/$manifest_name" "$release_dir/$asset" "$work/"
    ok "took $asset and $manifest_name from $release_dir"
else
    command -v curl >/dev/null || fail "curl is not installed (sudo apt install -y curl)"
    for name in "$manifest_name" "$asset"; do
        echo "  downloading $base_url/$name"
        curl -fsSL --retry 3 -o "$work/$name" "$base_url/$name" || fail "could not download $base_url/$name"
    done
    ok "downloaded $asset and $manifest_name"
fi

# The manifest entry for the tarball: release version, sha256, size, platform. Read by python3 because
# it is JSON; every field is required, and a manifest without them is refused, not guessed around.
expected="$(python3 -I - "$work/$manifest_name" "$asset" <<'PY'
import json, sys
path, asset = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8-sig") as f:
    m = json.load(f)
entry = m.get("assets", {}).get(asset)
if entry is None:
    sys.exit(f"release-manifest.json has no entry for {asset}")
for key in ("sha256", "size", "platform"):
    if key not in entry:
        sys.exit(f"release-manifest.json entry for {asset} has no '{key}'")
if entry["platform"] != "linux":
    sys.exit(f"release-manifest.json says {asset} is for platform '{entry['platform']}', not linux")
print(m.get("version", ""), entry["sha256"].lower(), entry["size"])
PY
)" || fail "release-manifest.json cannot be used to verify $asset (above)"
read -r manifest_version expected_sha expected_size <<<"$expected"
[[ "$manifest_version" == "$version" ]] \
    || fail "release-manifest.json is for version $manifest_version, but this installer is for $version"

actual_size="$(stat -c %s "$work/$asset")"
actual_sha="$(sha256sum "$work/$asset" | cut -d' ' -f1)"
if [[ "$actual_size" != "$expected_size" || "$actual_sha" != "$expected_sha" ]]; then
    echo "[FAIL] $asset does NOT match release-manifest.json - it is corrupt or has been altered." >&2
    echo "       expected: sha256 $expected_sha, $expected_size bytes" >&2
    echo "       got:      sha256 $actual_sha, $actual_size bytes" >&2
    echo "       Nothing was extracted and nothing was installed. Download it again." >&2
    exit 1
fi
ok "verified $asset against release-manifest.json (sha256 $actual_sha, $actual_size bytes)"

mkdir "$work/bundle"
tar -xzf "$work/$asset" -C "$work/bundle"
bundle="$work/bundle/agenteyes-linux-x64"
[[ -x "$bundle/scripts/linux/install.sh" ]] || fail "$asset holds no agenteyes-linux-x64/scripts/linux/install.sh"
ok "extracted AgentEyes $(cat "$bundle/VERSION")"

"$bundle/scripts/linux/install.sh" --prebuilt "$bundle/prebuilt" "${pass[@]}"
