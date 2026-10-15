#!/usr/bin/env bash
# Install (or update) AgentEyes on Linux: the recorder as a systemd USER service (issue #102) and the
# desktop app - library, player, record, settings and the tray icon (issue #107).
#
#   scripts/linux/install.sh            build, install, enable at login, (re)start both
#   scripts/linux/install.sh --no-start build and install, enable at login, do not start now
#
# What it puts where:
#   ~/.local/lib/agenteyes/            the self-contained linux-x64 build of agenteyes
#   ~/.local/bin/agenteyes             a link to it (the CLI: record start/stop, signin, screens)
#   ~/.config/systemd/user/agenteyes.service
#   ~/.local/lib/agenteyes-app/        the desktop app (AgentEyesLinux), self-contained linux-x64
#   ~/.local/share/applications/agenteyes.desktop   "AgentEyes" in the app grid
#   ~/.config/autostart/agenteyes.desktop           starts the app in the tray at login
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

# Root, asked for the way the person can answer. AgentEyes is meant to be installed BY AN AGENT
# (a coding agent session) that has no terminal to type a password into, so with no tty the request
# goes to the desktop's own permission window (polkit / pkexec - GNOME shows it). A person running
# this in a terminal gets the ordinary sudo prompt.
as_root() {
    if sudo -n true 2>/dev/null; then
        sudo "$@"
    elif [[ -t 0 ]]; then
        sudo "$@"
    elif command -v pkexec >/dev/null && [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
        echo "  asking for permission in a desktop window (polkit) - approve it there ..."
        pkexec "$@"
    else
        fail "root is needed for: $* - and there is neither a terminal nor a desktop permission window to ask in"
    fi
}
# First run: ffmpeg does all the capturing. It needs root to install, so this asks for permission
# once (as_root above) rather than leaving it as a manual step.
if ! command -v ffmpeg >/dev/null; then
    command -v apt-get >/dev/null || fail "ffmpeg is not installed and this is not an apt system - install ffmpeg with your package manager"
    echo "ffmpeg is not installed - AgentEyes records through it. Installing it now (this needs your permission) ..."
    as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y ffmpeg \
        || fail "installing ffmpeg failed (or permission was refused) - run: sudo apt install -y ffmpeg"
    ok "ffmpeg installed"
fi
command -v xrandr >/dev/null  || fail "xrandr is not installed (sudo apt install -y x11-xserver-utils)"
ffmpeg -hide_banner -devices 2>/dev/null | grep -q x11grab || fail "this ffmpeg has no x11grab input"
ffmpeg -hide_banner -devices 2>/dev/null | grep -q pulse   || fail "this ffmpeg has no pulse input"
# The app plays recordings with libvlc (LibVLCSharp). It needs root to install, like ffmpeg.
# Read once: 'ldconfig -p | grep -q' under pipefail fails on the SIGPIPE grep causes, even on a match.
shared_libs="$(ldconfig -p)"
if [[ "$shared_libs" != *"libvlc.so "* || "$shared_libs" != *"libvlccore.so"* ]]; then
    command -v apt-get >/dev/null || fail "the app plays recordings with libvlc - install libvlc (and its base plugins) with your package manager"
    echo "The video playback library (libvlc) is not installed. Installing it now (this needs your permission) ..."
    as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y libvlc-dev vlc-plugin-base vlc-plugin-video-output \
        || fail "installing libvlc failed - run: sudo apt install -y libvlc-dev vlc-plugin-base vlc-plugin-video-output"
    ok "video playback library installed"
fi
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

# The desktop app (issue #107): its own folder, "AgentEyes" in the app grid, and started in the tray at
# login. It replaces the Python tray icon of the first Linux install: that one's two known files are
# removed by exact path (nothing else is touched).
app_lib="$HOME/.local/lib/agenteyes-app"
apps_dir="$HOME/.local/share/applications"
autostart_dir="$HOME/.config/autostart"
echo "Building the AgentEyes app (Release, linux-x64, self-contained) ..."
app_stage="$(mktemp -d)"
trap 'rm -rf "$stage" "$app_stage"' EXIT
dotnet publish "$repo/src/AgentEyes.LinuxApp/AgentEyes.LinuxApp.csproj" -c Release -r linux-x64 \
    --self-contained true -p:Platform=x64 -o "$app_stage" -v q --nologo
[[ -x "$app_stage/AgentEyesLinux" ]] || fail "the build did not produce $app_stage/AgentEyesLinux"
install -m 0644 "$repo/assets/icon-256.png" "$app_stage/agenteyes.png"
for p in $(pgrep -x AgentEyesLinux || true); do kill "$p"; done
rm -rf "$app_lib.new"
cp -a "$app_stage" "$app_lib.new"
rm -rf "$app_lib"
mv "$app_lib.new" "$app_lib"
mkdir -p "$apps_dir" "$autostart_dir"
desktop_entry() {
    printf '%s\n' \
        "[Desktop Entry]" \
        "Type=Application" \
        "Name=AgentEyes" \
        "Comment=Screen, system sound and microphone recorder" \
        "Exec=$app_lib/AgentEyesLinux$1" \
        "Icon=$app_lib/agenteyes.png" \
        "Terminal=false" \
        "Categories=AudioVideo;Recorder;" \
        "StartupWMClass=AgentEyesLinux"
}
desktop_entry "" > "$apps_dir/agenteyes.desktop"
{ desktop_entry " --tray"; echo "X-GNOME-Autostart-enabled=true"; } > "$autostart_dir/agenteyes.desktop"
pkill -f "python3 $HOME/.local/lib/agenteyes-tray/agenteyes-tray.py" 2>/dev/null || true
rm -f "$autostart_dir/agenteyes-tray.desktop"
rm -rf "$HOME/.local/lib/agenteyes-tray"
ok "installed the app ($app_lib): 'AgentEyes' in the app grid, starts in the tray at login"

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

if [[ $start -eq 1 && -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
    setsid "$app_lib/AgentEyesLinux" >/dev/null 2>&1 < /dev/null &
    ok "AgentEyes app started - its window opens, and the moon in the top bar stays when you close it"
fi

echo
echo "Ready. Open AgentEyes from the app grid or the moon in the top bar. From a terminal:"
echo "            record: agenteyes record start --mix --mic \"<part of your mic name>\""
echo "            stop:   agenteyes record stop"
echo "  list mics with:   agenteyes screens"
