#!/usr/bin/env bash
# Install (or update) the AgentEyes headless recorder as a launchd USER AGENT on macOS - the
# counterpart of scripts/linux/install.sh.
#
#   scripts/mac/install.sh            build, install to ~/Library/Application Support/AgentEyes/bin,
#                                     load the agent and start it
#   scripts/mac/install.sh --no-start build and install, load at login, do not start now
#
# What it puts where:
#   ~/Library/Application Support/AgentEyes/bin/      the self-contained build of agenteyes
#   ~/.local/bin/agenteyes                            a link to it (the CLI: record start/stop, screens)
#   ~/Library/LaunchAgents/com.agenteyes.recorder.plist
# Recordings land in ~/Movies/AgentEyes/<timestamp>_video_video/
#
# WHAT THIS SCRIPT CANNOT DO. It cannot grant the Screen Recording permission - macOS only accepts
# that from the person at the keyboard. The script finishes by saying exactly what to switch on and
# where, and 'agenteyes screens' is the check that it worked.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
support="$HOME/Library/Application Support/AgentEyes"
lib="$support/bin"
logs="$support/logs"
bin="$HOME/.local/bin"
agents="$HOME/Library/LaunchAgents"
label="com.agenteyes.recorder"
plist="$agents/$label.plist"
start=1
[[ "${1:-}" == "--no-start" ]] && start=0

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

# ---- what must be true before anything is installed ------------------------
[[ "$(uname -s)" == "Darwin" ]] || fail "this is the macOS installer; on Linux run scripts/linux/install.sh"

command -v dotnet >/dev/null || fail "dotnet is not installed (see https://dotnet.microsoft.com/download)"

# ffmpeg does all the capturing. Homebrew is how a Mac gets it, and installing it needs the user's
# own consent, so this reports and stops rather than running brew unasked.
if ! command -v ffmpeg >/dev/null; then
    if command -v brew >/dev/null; then
        fail "ffmpeg is not installed - AgentEyes records through it. Install it with: brew install ffmpeg"
    fi
    fail "ffmpeg is not installed and Homebrew was not found. Install Homebrew (https://brew.sh) then: brew install ffmpeg"
fi

# The capture inputs this build actually uses. A Mac ffmpeg without avfoundation cannot record at
# all, and finding that out now is the difference between a clear message and a broken service.
ffmpeg -hide_banner -devices 2>/dev/null | grep -q avfoundation \
    || fail "this ffmpeg has no avfoundation input, so it cannot capture a Mac screen. Reinstall it with: brew reinstall ffmpeg"
ok "ffmpeg: $(command -v ffmpeg)"

# ---- build ----------------------------------------------------------------
arch="$(uname -m)"
case "$arch" in
    arm64)  rid="osx-arm64" ;;
    x86_64) rid="osx-x64" ;;
    *)      fail "unsupported architecture '$arch' - AgentEyes builds for osx-arm64 and osx-x64" ;;
esac

echo "Building agenteyes (Release, $rid, self-contained) ..."
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

dotnet publish "$repo/src/AgentEyes.Core/AgentEyes.Core.csproj" \
    -c Release -f net8.0 -r "$rid" --self-contained true \
    -p:AgentEyesUnixPlatform=mac \
    -o "$stage" >/dev/null || fail "the build failed"
[[ -x "$stage/agenteyes" ]] || fail "the build produced no agenteyes executable"
ok "built"

# ---- stop whatever is running, then install -------------------------------
# Unload before replacing the files: a running service holding the old binary is why an update
# otherwise half-applies. 'bootout' on a label that is not loaded is not an error here.
if launchctl print "gui/$UID/$label" >/dev/null 2>&1; then
    launchctl bootout "gui/$UID/$label" 2>/dev/null || true
    ok "stopped the running agent"
fi

mkdir -p "$lib" "$logs" "$bin" "$agents"
# Replace the contents rather than the directory, so the install path stays stable.
rm -rf "$lib"/*
cp -R "$stage"/. "$lib"/
chmod +x "$lib/agenteyes"
ok "installed to $lib"

ln -sf "$lib/agenteyes" "$bin/agenteyes"
ok "linked $bin/agenteyes"

# The plist carries absolute paths, which a template cannot know - they are filled in here.
sed -e "s|__LIB__|$lib|g" -e "s|__LOGS__|$logs|g" \
    "$repo/scripts/mac/com.agenteyes.recorder.plist" > "$plist"
ok "wrote $plist"

# ---- load -----------------------------------------------------------------
launchctl bootstrap "gui/$UID" "$plist" || fail "launchctl could not load $plist"
ok "agent loaded (starts at login)"

if [[ $start -eq 1 ]]; then
    launchctl kickstart -k "gui/$UID/$label" || fail "launchctl could not start the agent"
    ok "agent started"
else
    launchctl bootout "gui/$UID/$label" 2>/dev/null || true
    ok "agent installed but not started (--no-start)"
fi

cat <<EOF

-------------------------------------------------------------------------
ONE THING IS LEFT, AND ONLY YOU CAN DO IT.

macOS will not let any program record the screen until you allow it, per
application. Open:

  System Settings > Privacy and Security > Screen and System Audio Recording

and switch on "agenteyes". If it is not in the list yet, run this once to
make macOS ask:

  agenteyes shot --screen 1 --out ~/Desktop

Then check it worked:

  agenteyes screens          the displays, microphones and cameras it can see
  agenteyes record status    the service, on http://127.0.0.1:7882

NOTE ON SOUND. macOS has no way to record what it is PLAYING without a
virtual audio device - unlike Windows and Linux. The microphone works on its
own (--mic). For system sound (--mix / --loopback), install one first:

  brew install blackhole-2ch

Logs: $logs
Stop:  launchctl bootout gui/$UID/$label
Start: launchctl kickstart -k gui/$UID/$label
-------------------------------------------------------------------------
EOF
