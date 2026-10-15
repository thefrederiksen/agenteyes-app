#!/usr/bin/env bash
# Install (or update) the AgentEyes headless recorder as a systemd USER service (issue #102).
#
#   scripts/linux/install.sh            build, install to ~/.local/lib/agenteyes, enable + (re)start
#   scripts/linux/install.sh --no-start build and install, enable at login, do not start now
#
# What it puts where:
#   ~/.local/lib/agenteyes/            the self-contained linux-x64 build of agenteyes
#   ~/.local/bin/agenteyes             a link to it (the CLI: record start/stop, signin, screens)
#   ~/.config/systemd/user/agenteyes.service
# Recordings land in the XDG Videos folder: ~/Videos/AgentEyes/<timestamp>_video_video/
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
lib="$HOME/.local/lib/agenteyes"
bin="$HOME/.local/bin"
unit_dir="$HOME/.config/systemd/user"
start=1
[[ "${1:-}" == "--no-start" ]] && start=0

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

command -v dotnet >/dev/null  || fail "dotnet is not installed (sudo apt install -y dotnet-sdk-8.0)"
# First run: ffmpeg does all the capturing. It needs root to install, so this asks for your
# password once (sudo) rather than leaving it as a manual step.
if ! command -v ffmpeg >/dev/null; then
    command -v apt-get >/dev/null || fail "ffmpeg is not installed and this is not an apt system - install ffmpeg with your package manager"
    echo "ffmpeg is not installed - AgentEyes records through it. Installing it now (sudo asks for your password) ..."
    sudo apt-get install -y ffmpeg || fail "installing ffmpeg failed - run: sudo apt install -y ffmpeg"
    ok "ffmpeg installed"
fi
command -v xrandr >/dev/null  || fail "xrandr is not installed (sudo apt install -y x11-xserver-utils)"
ffmpeg -hide_banner -devices 2>/dev/null | grep -q x11grab || fail "this ffmpeg has no x11grab input"
ffmpeg -hide_banner -devices 2>/dev/null | grep -q pulse   || fail "this ffmpeg has no pulse input"
[[ "${XDG_SESSION_TYPE:-x11}" == "wayland" ]] && fail "this is a Wayland session; AgentEyes on Linux records X11 only (log in with 'Ubuntu on Xorg')"

echo "Building agenteyes (Release, linux-x64, self-contained) ..."
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
dotnet publish "$repo/src/AgentEyes.Core/AgentEyes.Core.csproj" -c Release -f net8.0 -r linux-x64 \
    --self-contained true -p:Platform=x64 -o "$stage" -v q --nologo
[[ -x "$stage/agenteyes" ]] || fail "the build did not produce $stage/agenteyes"
ok "built $stage/agenteyes"

if systemctl --user is-active --quiet agenteyes.service; then
    echo "Stopping the running service (a running recording is stopped and kept) ..."
    systemctl --user stop agenteyes.service
fi

mkdir -p "$lib" "$bin" "$unit_dir"
rm -rf "$lib.new"
cp -a "$stage" "$lib.new"
rm -rf "$lib"
mv "$lib.new" "$lib"
ln -sfn "$lib/agenteyes" "$bin/agenteyes"
install -m 0644 "$repo/scripts/linux/agenteyes.service" "$unit_dir/agenteyes.service"
ok "installed $lib, linked $bin/agenteyes, unit $unit_dir/agenteyes.service"

# The service records the X display: make sure the user manager has it (GNOME normally imports it).
systemctl --user import-environment DISPLAY XAUTHORITY 2>/dev/null || true
systemctl --user daemon-reload
systemctl --user enable agenteyes.service >/dev/null
ok "enabled at login (graphical-session.target)"

if [[ $start -eq 1 ]]; then
    systemctl --user restart agenteyes.service
    for _ in $(seq 1 30); do
        if curl -fsS http://127.0.0.1:7882/health >/dev/null 2>&1; then
            ok "service is up: $(curl -fsS http://127.0.0.1:7882/health | tr -d '\n ')"
            break
        fi
        sleep 1
    done
    curl -fsS http://127.0.0.1:7882/health >/dev/null 2>&1 \
        || fail "the service did not answer on 127.0.0.1:7882 - see: journalctl --user -u agenteyes -n 50"
fi

# First run: sign in to DevThrottle, which transcribes and titles the recordings. The service
# cannot ask (it has no window), so the installer does, once. Already signed in = nothing to do.
if "$bin/agenteyes" account | grep -q '^signed in as'; then
    ok "$("$bin/agenteyes" account)"
else
    echo "Not signed in to DevThrottle - recordings would not be transcribed or titled. Signing in now ..."
    "$bin/agenteyes" signin || fail "sign-in did not complete - run it again any time: agenteyes signin"
fi

echo
echo "Ready. Record with:  agenteyes record start --mix --mic \"<part of your mic name>\""
echo "            stop:   agenteyes record stop"
echo "  list mics with:   agenteyes screens"
