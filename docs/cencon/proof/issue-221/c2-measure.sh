#!/bin/bash
# Runs INSIDE a ubuntu:<release> container. Package-level measurement only.
set -u
export DEBIAN_FRONTEND=noninteractive
echo "== image"; date -u; grep -E '^(PRETTY_NAME|VERSION_ID|VERSION_CODENAME)=' /etc/os-release
echo "== apt-get update (archive as shipped in the image)"
if ! apt-get update -q >/tmp/upd.log 2>&1; then
  tail -5 /tmp/upd.log
  echo "== archive unreachable as shipped; repointing to old-releases.ubuntu.com (release is end-of-life)"
  for f in /etc/apt/sources.list /etc/apt/sources.list.d/ubuntu.sources; do
    [ -f "$f" ] && sed -i -E 's#https?://(archive|security|ports)\.ubuntu\.com/ubuntu(-ports)?/?#http://old-releases.ubuntu.com/ubuntu/#g' "$f"
  done
  apt-get update -q >/tmp/upd.log 2>&1 || { tail -20 /tmp/upd.log; echo "APT UPDATE FAILED"; exit 2; }
fi
tail -2 /tmp/upd.log
echo "== install ubuntu-desktop-minimal WITH recommends (the installer's default selection)"
apt-get install -y -q ubuntu-desktop-minimal >/tmp/inst.log 2>&1; echo "apt exit=$?"; tail -3 /tmp/inst.log
echo "== installed versions"
dpkg-query -W -f='${Package} ${Version}\n' ubuntu-desktop-minimal ubuntu-session gnome-shell gnome-session-bin gdm3 xserver-xorg-core xwayland 2>&1
echo "== ls /usr/share/xsessions/"; ls -la /usr/share/xsessions/ 2>&1
echo "== ls /usr/share/wayland-sessions/"; ls -la /usr/share/wayland-sessions/ 2>&1
echo "== Name= lines of every session file"
for f in /usr/share/xsessions/*.desktop /usr/share/wayland-sessions/*.desktop; do [ -f "$f" ] && echo "$f: $(grep -m1 '^Name=' "$f")"; done
echo "== who owns them"; dpkg -S /usr/share/xsessions /usr/share/wayland-sessions 2>&1
echo "== files of ubuntu-session"; dpkg -L ubuntu-session 2>&1 | grep -E 'sessions/' 
echo "== repository: any package whose file list would put an Xorg session in place? (candidates by name)"
apt-cache search --names-only 'ubuntu-session|gnome-session|xorg-session|session-xorg' 2>&1
echo "== gnome-shell: does the installed binary offer an X11 mode?"
gnome-shell --help 2>&1 | grep -iE 'x11|wayland|nested|display-server' || echo "(no x11/wayland option lines in gnome-shell --help)"
echo "== done"
