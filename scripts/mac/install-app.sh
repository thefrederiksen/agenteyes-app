#!/usr/bin/env bash
# Install (or update) AgentEyes.app, the desktop app, on macOS (issue #118).
#
#   scripts/mac/install.sh                  the documented route: the recorder service, then this
#   scripts/mac/install-app.sh              the app ONLY: build, install, open it
#   scripts/mac/install-app.sh --no-start   build and install, do not open it
#
# It never touches the recorder service: not its binary, not its launchd agent
# (com.agenteyes.recorder), not its permissions. The app talks to the service over the Control API
# (127.0.0.1:7882) and shows "offline" until the service is running.
#
# WHERE: ~/Applications/AgentEyes.app. The per-user Applications folder, because everything else
# AgentEyes installs on a Mac is per-user (the service lives in ~/Library and runs as a launchd USER
# agent), so the install needs no administrator password, and it is where Finder, Spotlight and
# Launchpad look for the user's own applications.
#
# THE PERMISSION STORY. The app needs no Screen Recording, Microphone or Camera permission of its own:
# every capture is done by the recorder service, which holds those permissions under its own name
# ("agenteyes"). The app is a SEPARATE application to macOS (com.agenteyes.app), so it does not
# inherit them - and it does not need them.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
apps="$HOME/Applications"
app="$apps/AgentEyes.app"
exe="$app/Contents/MacOS/AgentEyesDesktop"
start=1
[[ "${1:-}" == "--no-start" ]] && start=0

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

[[ "$(uname -s)" == "Darwin" ]] || fail "this is the macOS app installer; on Linux run scripts/linux/install.sh"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
"$repo/scripts/mac/build-app.sh" "$stage" || fail "the app did not build"

# Stop the installed app by the path it was started from - only that one, never another copy (a
# developer's build, another checkout). Replacing a bundle under a running app is how an update
# half-applies. SIGTERM, then wait: the app must be GONE before its files move, or the new one finds
# the old one holding the single-instance socket, hands it the launch and exits.
pids="$(pgrep -f "^$exe( |\$)" || true)"
if [[ -n "$pids" ]]; then
    kill $pids
    for _ in $(seq 1 50); do
        pgrep -f "^$exe( |\$)" >/dev/null || break
        sleep 0.2
    done
    pgrep -f "^$exe( |\$)" >/dev/null && fail "the running AgentEyes app (pid $pids) did not quit within 10 s - quit it from its menu bar icon, then run this again"
    ok "stopped the running app (pid $pids)"
fi

# Copy beside, then swap, so a failed copy never leaves half an app. cp keeps the extended attributes
# that carry the signatures of the bundle's non-Mach-O files (see build-app.sh), and the bundle is
# verified again AFTER the copy: a copy that dropped them is caught here, not at first launch.
mkdir -p "$apps"
rm -rf "$app.new"
cp -R "$stage/AgentEyes.app" "$app.new"
codesign --verify --deep --strict "$app.new" || fail "the copied bundle does not verify - the copy lost its signatures"
rm -rf "$app"
mv "$app.new" "$app"
ok "installed $app"

# Tell Launch Services now, so 'open -a AgentEyes', Spotlight and Launchpad find it without waiting
# for the next scan of ~/Applications.
lsregister="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
[[ -x "$lsregister" ]] || fail "lsregister was not found at $lsregister"
"$lsregister" -f "$app" || fail "lsregister could not register $app"
ok "registered with Launch Services"

if [[ $start -eq 1 ]]; then
    open "$app" || fail "macOS could not open $app"
    ok "AgentEyes opened - its window, its icon in the dock, and its icon in the menu bar"
fi

cat <<EOF

AgentEyes.app is in $app - open it from Finder, Launchpad or Spotlight.
It needs no permission of its own: recording is done by the AgentEyes service,
which is the one that appears as "agenteyes" under System Settings > Privacy
and Security > Screen and System Audio Recording.
EOF
