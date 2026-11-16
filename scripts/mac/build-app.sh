#!/usr/bin/env bash
# Build AgentEyes.app - the desktop app as a double-clickable, self-contained macOS application
# (issue #118).
#
#   scripts/mac/build-app.sh <output-dir>      writes <output-dir>/AgentEyes.app
#
# scripts/mac/install.sh calls this; it can also be run on its own. It installs nothing, starts nothing
# and touches no permission.
#
# THE BUNDLE
#   AgentEyes.app/Contents/Info.plist        src/AgentEyes.DesktopApp/macOS/Info.plist, version filled in
#   AgentEyes.app/Contents/PkgInfo           APPL????
#   AgentEyes.app/Contents/MacOS/            the whole self-contained publish: AgentEyesDesktop (the
#                                            apphost), the .NET 8 runtime, the app, libvlc
#   AgentEyes.app/Contents/Resources/AgentEyes.icns   assets/AgentEyes.icns (scripts/mac/make-icns.sh)
#
# SELF-CONTAINED: the .NET runtime is inside the bundle, so the app runs on a Mac with no .NET at all
# and needs no DOTNET_ROLL_FORWARD or DOTNET_ROOT - the same choice as the recorder service's own
# install (install.sh publishes agenteyes with --self-contained true).
#
# SIGNED AD-HOC, AND THAT IS NOT THE DELIVERABLE. Every Mach-O in the bundle, and then the bundle, is
# signed with the ad-hoc identity ("-"). That makes the bundle's signature valid and sealed - it runs on
# THIS Mac - but it carries no developer identity, so Gatekeeper refuses it on any other Mac once it has
# been downloaded, and macOS keys any permission it is granted to this exact build (its code hash): a
# rebuild is a new application to the privacy settings. The Developer ID signature, the hardened runtime
# and notarisation are the follow-up step of issue #118, blocked on the Apple Developer Program
# enrolment.
#
# NOT CHANGED HERE: StartupHookSupport. The publish keeps the SDK default (startup hooks enabled). That
# is a decision the lead owns (issue #118), not a side effect of packaging.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="$repo/src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj"
plist_template="$repo/src/AgentEyes.DesktopApp/macOS/Info.plist"
icns="$repo/assets/AgentEyes.icns"
exe_name="AgentEyesDesktop"

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

[[ $# -eq 1 ]] || fail "usage: scripts/mac/build-app.sh <output-dir>"
out_dir="$1"
[[ "$(uname -s)" == "Darwin" ]] || fail "this builds a macOS application bundle; run it on a Mac"
command -v dotnet >/dev/null || fail "dotnet is not installed (see https://dotnet.microsoft.com/download)"
command -v codesign >/dev/null || fail "codesign was not found - install the Xcode command line tools: xcode-select --install"
[[ -f "$plist_template" ]] || fail "missing $plist_template"
[[ -f "$icns" ]] || fail "missing $icns - generate it with scripts/mac/make-icns.sh"
[[ -d "$out_dir" ]] || fail "output directory $out_dir does not exist"
app="$out_dir/AgentEyes.app"
[[ ! -e "$app" ]] || fail "$app already exists - this script never overwrites a bundle; remove it or pick another directory"

arch="$(uname -m)"
case "$arch" in
    arm64)  rid="osx-arm64"; platform="arm64" ;;
    x86_64) rid="osx-x64";   platform="x64" ;;
    *)      fail "unsupported architecture '$arch' - AgentEyes builds for osx-arm64 and osx-x64" ;;
esac

version="$(dotnet msbuild "$project" -getProperty:Version -nologo)" || fail "could not read <Version> from $project"
[[ "$version" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]] || fail "<Version> in $project is '$version'; the bundle needs a dotted number (CFBundleShortVersionString)"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

echo "Building the AgentEyes app (Release, $rid, self-contained, version $version) ..."
dotnet publish "$project" -c Release -r "$rid" --self-contained true -p:Platform="$platform" \
    -o "$stage/publish" -v q --nologo >/dev/null || fail "the publish failed - run the same dotnet publish without -v q to see why"
[[ -x "$stage/publish/$exe_name" ]] || fail "the publish produced no $exe_name executable"
ok "published"

bundle="$stage/AgentEyes.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$stage/publish"/. "$bundle/Contents/MacOS"/
cp "$icns" "$bundle/Contents/Resources/AgentEyes.icns"
sed -e "s|__VERSION__|$version|g" "$plist_template" > "$bundle/Contents/Info.plist"
printf 'APPL????' > "$bundle/Contents/PkgInfo"
plutil -lint "$bundle/Contents/Info.plist" >/dev/null || fail "the generated Info.plist is not valid"
[[ "$(plutil -extract CFBundleExecutable raw "$bundle/Contents/Info.plist")" == "$exe_name" ]] \
    || fail "Info.plist CFBundleExecutable does not name $exe_name"
ok "assembled AgentEyes.app"

# Sign every file under Contents/MacOS but the main executable, then the bundle, which signs the main
# executable and seals the rest.
#
# WHY EVERY FILE AND NOT ONLY THE Mach-O ONES. codesign treats everything in Contents/MacOS as code,
# and the .NET publish has to live there: the apphost finds the runtime and AgentEyesDesktop.dll next
# to itself. Signing the bundle with a managed .dll unsigned there fails with "code object is not
# signed at all. In subcomponent: .../System.Net.WebSockets.Client.dll" (measured for #118). A
# non-Mach-O file's signature is stored in its EXTENDED ATTRIBUTES, so whatever copies the bundle must
# keep them: cp and ditto on macOS do; a plain zip does not. install.sh re-verifies after its copy.
# Re-signing a library its publisher already signed is deliberate: the bundle's signature covers what
# is inside it, and the Developer ID step will re-sign every one of them with its own identity anyway.
signed=0
while IFS= read -r -d '' f; do
    [[ "$f" == "$bundle/Contents/MacOS/$exe_name" ]] && continue
    codesign --force --sign - --timestamp=none "$f" 2>/dev/null || fail "codesign could not sign $f"
    signed=$((signed + 1))
done < <(find "$bundle/Contents/MacOS" -type f -print0)
codesign --force --sign - --timestamp=none "$bundle" || fail "codesign could not sign the bundle"
codesign --verify --deep --strict "$bundle" || fail "the signed bundle does not verify (codesign --verify --deep --strict)"
ok "signed ad-hoc ($signed files inside, then the bundle) and verified - NOT notarised"

mv "$bundle" "$app"
ok "built $app ($(du -sh "$app" | cut -f1))"
