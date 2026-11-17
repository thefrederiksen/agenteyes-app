#!/usr/bin/env bash
# Build the Linux release assets (issue #194). release.yml's linux job runs exactly this.
#
#   scripts/linux/build-release.sh [OUT]        OUT defaults to dist/linux
#
# Writes:
#   OUT/release/agenteyes-linux-x64.tar.gz         the prebuilt recorder + desktop app + installer
#   OUT/release/agenteyes-install-linux-x64.sh     the download-verify-install script for that tarball
#   OUT/asset-versions-linux.json                  the two assets' versions, for write-manifest.ps1
#
# The tarball holds one folder, agenteyes-linux-x64/:
#   VERSION
#   prebuilt/agenteyes/          AgentEyes.Core, self-contained linux-x64 (the recorder and CLI)
#   prebuilt/agenteyes-app/      AgentEyes.DesktopApp, self-contained linux-x64 (the app)
#   scripts/linux/install.sh     the SAME installer a source checkout uses, run with --prebuilt
#   scripts/linux/agenteyes.service
#   assets/icon-256.png
# Nothing here hashes anything: write-manifest.ps1 hashes every asset of the release in one place.
set -euo pipefail

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="${1:-$repo/dist/linux}"
command -v dotnet >/dev/null || fail "dotnet is not installed"

csproj_version() {
    python3 -I - "$1" <<'PY'
import sys, xml.etree.ElementTree as ET
versions = [e.text for e in ET.parse(sys.argv[1]).getroot().iter("Version") if e.text]
if len(versions) != 1:
    sys.exit(f"expected exactly one <Version> in {sys.argv[1]}, found {len(versions)}")
print(versions[0].strip())
PY
}
version="$(csproj_version "$repo/src/AgentEyes.App/AgentEyes.App.csproj")"
core_version="$(csproj_version "$repo/src/AgentEyes.Core/AgentEyes.Core.csproj")"
[[ "$core_version" == "$version" ]] || fail "version mismatch: app $version vs cli $core_version"
echo "version: $version"

rm -rf "$out"
bundle="$out/bundle/agenteyes-linux-x64"
mkdir -p "$out/release" "$bundle/prebuilt" "$bundle/scripts/linux" "$bundle/assets"

echo "== publish the recorder (AgentEyes.Core, linux-x64, self-contained) =="
dotnet publish "$repo/src/AgentEyes.Core/AgentEyes.Core.csproj" -c Release -f net8.0 -r linux-x64 \
    --self-contained true -p:Platform=x64 -o "$bundle/prebuilt/agenteyes" -v q --nologo
[[ -x "$bundle/prebuilt/agenteyes/agenteyes" ]] || fail "the publish produced no agenteyes"

echo "== publish the app (AgentEyes.DesktopApp, linux-x64, self-contained) =="
dotnet publish "$repo/src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj" -c Release -r linux-x64 \
    --self-contained true -p:Platform=x64 -o "$bundle/prebuilt/agenteyes-app" -v q --nologo
[[ -x "$bundle/prebuilt/agenteyes-app/AgentEyesDesktop" ]] || fail "the publish produced no AgentEyesDesktop"

install -m 0755 "$repo/scripts/linux/install.sh" "$bundle/scripts/linux/install.sh"
install -m 0644 "$repo/scripts/linux/agenteyes.service" "$bundle/scripts/linux/agenteyes.service"
install -m 0644 "$repo/assets/icon-256.png" "$bundle/assets/icon-256.png"
echo "$version" > "$bundle/VERSION"

tarball="$out/release/agenteyes-linux-x64.tar.gz"
tar -czf "$tarball" -C "$out/bundle" --owner=0 --group=0 agenteyes-linux-x64
rm -rf "$out/bundle"
ok "wrote $tarball ($(stat -c %s "$tarball") bytes)"

installer="$out/release/agenteyes-install-linux-x64.sh"
sed "s/@VERSION@/$version/g" "$repo/scripts/linux/install-release.sh" > "$installer"
chmod 0755 "$installer"
grep -q '@VERSION@' "$installer" && fail "$installer still holds the @VERSION@ placeholder"
bash -n "$installer" || fail "$installer is not valid bash"
ok "wrote $installer (installs $version)"

cat > "$out/asset-versions-linux.json" <<JSON
{
  "version": "$version",
  "assetVersions": {
    "agenteyes-linux-x64.tar.gz": "$version",
    "agenteyes-install-linux-x64.sh": "$version"
  }
}
JSON
ok "wrote $out/asset-versions-linux.json"
ls -l "$out/release"
