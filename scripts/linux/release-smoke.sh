#!/usr/bin/env bash
# Install the Linux release asset the way a person would, on a machine that has never had AgentEyes
# (issue #194). release.yml's linux job runs exactly this before anything is published.
#
#   scripts/linux/release-smoke.sh RELEASE_DIR
#
# RELEASE_DIR holds agenteyes-install-linux-x64.sh, agenteyes-linux-x64.tar.gz and a
# release-manifest.json that has the tarball's entry (write-manifest.ps1 writes it).
#
# THE MACHINE: a new container from the public ubuntu:24.04 image, with systemd running as PID 1 so
# a systemd USER manager exists, and one new user (tester) who has never run AgentEyes. No .NET, no
# ffmpeg, no libvlc - the installer has to bring those in from the distribution itself. What it is
# given up front, and why:
#   - systemd, dbus, sudo, curl, python3, x11-xserver-utils: an Ubuntu desktop has all of these; the
#     container image does not.
#   - passwordless sudo for tester: stands in for the person approving the root prompt that installs
#     ffmpeg and libvlc. The installer asks; here nobody is there to type a password.
#   - lingering for tester: a desktop login starts the user manager; in a container nobody logs in.
#   - the apt package lists (apt-get update), which the image ships without. install.sh installs ffmpeg
#     and libvlc with apt-get install and never runs apt-get update itself, so a machine with empty
#     package lists fails there; an Ubuntu desktop has them.
#   - an X display: Xvfb on :99, a virtual X server, standing in for the desktop session. The service
#     refuses to start without DISPLAY ("there is no X screen to record") - measured, that is correct
#     behaviour, not a container quirk to work around - so the install runs with DISPLAY=:99 the way it
#     runs inside a desktop session, and the app is launched against it too.
#   - no browser: sign-in is skipped with --no-signin (it opens a browser and waits for a person).
#
# TWO CHECKS, in this order, each one failing the script:
#   1. REFUSAL: a copy of the tarball with ONE byte changed is refused by the installer before anything
#      is extracted - the exit is non-zero, the mismatch is printed, and nothing is installed.
#   2. INSTALL: the real tarball installs, the user unit is active, and /health answers.
# Nothing on the host is touched: everything runs inside the container, which is removed at the end.
set -euo pipefail

fail() { echo "[FAIL] $*" >&2; exit 1; }
ok()   { echo "[OK] $*"; }

release="$(cd "${1:?usage: release-smoke.sh RELEASE_DIR}" && pwd)"
for f in agenteyes-install-linux-x64.sh agenteyes-linux-x64.tar.gz release-manifest.json; do
    [[ -f "$release/$f" ]] || fail "$release has no $f"
done
command -v docker >/dev/null || fail "docker is not installed"

image="agenteyes-release-smoke:ubuntu-24.04"
name="agenteyes-release-smoke-$$"
echo "== build the clean-machine image ($image) =="
docker build -q -t "$image" - <<'DOCKERFILE' >/dev/null
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update -q \
 && apt-get install -y -q --no-install-recommends systemd systemd-sysv dbus dbus-user-session sudo \
        curl ca-certificates python3 x11-xserver-utils xvfb
RUN useradd -m -s /bin/bash tester \
 && echo 'tester ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/tester && chmod 0440 /etc/sudoers.d/tester
CMD ["/sbin/init"]
DOCKERFILE
ok "image built"

cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run -d --name "$name" --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
    --tmpfs /run --tmpfs /run/lock -v "$release:/release:ro" "$image" >/dev/null
# 'is-system-running --wait' returns at once, with nothing, while systemd's own bus is not up yet, so
# it is asked again until it gives an answer (60 s at most).
state=""
for _ in $(seq 1 60); do
    state="$(docker exec "$name" systemctl is-system-running --wait 2>/dev/null || true)"
    [[ -n "$state" ]] && break
    sleep 1
done
[[ "$state" == "running" || "$state" == "degraded" ]] || fail "systemd in the container did not come up (state: '$state')"
ok "container $name up, systemd $state"

uid="$(docker exec "$name" id -u tester)"
docker exec "$name" loginctl enable-linger tester
for _ in $(seq 1 30); do docker exec "$name" test -S "/run/user/$uid/bus" && break; sleep 1; done
docker exec "$name" test -S "/run/user/$uid/bus" || fail "tester's systemd user manager did not start"
ok "tester's systemd user manager is running"

docker exec -d -u tester "$name" Xvfb :99 -screen 0 1280x720x24 -nolisten tcp
for _ in $(seq 1 15); do docker exec -u tester -e DISPLAY=:99 "$name" xrandr >/dev/null 2>&1 && break; sleep 1; done
docker exec -u tester -e DISPLAY=:99 "$name" xrandr >/dev/null 2>&1 || fail "Xvfb did not come up on :99"
ok "X display :99 (Xvfb) is up"

as_tester() {
    docker exec -u tester -w /home/tester -e DISPLAY=:99 -e "XDG_RUNTIME_DIR=/run/user/$uid" \
        -e "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid/bus" "$name" bash -c "$1"
}

echo "== what this machine has before the install =="
as_tester 'for t in dotnet ffmpeg agenteyes; do printf "  %-9s %s\n" "$t" "$(command -v $t || echo "not installed")"; done
           ls -d ~/.local/lib/agenteyes ~/.config/systemd/user/agenteyes.service 2>&1 | sed "s/^/  /"'

echo "== 1. a download with ONE byte changed must be refused =="
as_tester 'mkdir -p corrupt && cp /release/release-manifest.json /release/agenteyes-linux-x64.tar.gz corrupt/
           python3 -I - corrupt/agenteyes-linux-x64.tar.gz <<'"'"'PY'"'"'
import os, sys
path = sys.argv[1]
offset = os.path.getsize(path) // 2
with open(path, "r+b") as f:
    f.seek(offset); b = f.read(1); f.seek(offset); f.write(bytes([b[0] ^ 0x01]))
print(f"  changed 1 byte at offset {offset}: 0x{b[0]:02x} -> 0x{b[0] ^ 0x01:02x}")
PY
           echo "  original:  $(sha256sum /release/agenteyes-linux-x64.tar.gz | cut -d" " -f1)"
           echo "  corrupted: $(sha256sum corrupt/agenteyes-linux-x64.tar.gz | cut -d" " -f1)"' \
    || fail "could not make the corrupted copy"
set +e
refusal="$(as_tester 'bash /release/agenteyes-install-linux-x64.sh --release-dir corrupt --no-signin' 2>&1)"
refusal_exit=$?
set -e
echo "$refusal" | sed 's/^/  | /'
echo "  installer exit code: $refusal_exit"
[[ $refusal_exit -ne 0 ]] || fail "the installer ACCEPTED a corrupted download (exit 0)"
grep -q 'does NOT match release-manifest.json' <<<"$refusal" || fail "the installer failed, but not with the hash mismatch"
as_tester 'test ! -e ~/.local/lib/agenteyes && test ! -e ~/.config/systemd/user/agenteyes.service' \
    || fail "the corrupted download was refused, but something was installed anyway"
ok "corrupted download refused (exit $refusal_exit), nothing installed"

echo "== 2. the real download installs =="
set +e
as_tester 'bash /release/agenteyes-install-linux-x64.sh --release-dir /release --no-signin' 2>&1 | sed 's/^/  | /'
install_exit=${PIPESTATUS[0]}
set -e
echo "  installer exit code: $install_exit"
if [[ $install_exit -ne 0 ]]; then
    as_tester 'systemctl --user status agenteyes.service --no-pager; journalctl --user -u agenteyes -n 60 --no-pager' 2>&1 \
        | sed 's/^/  journal | /' || true
    fail "the installer failed on the real download (above)"
fi

echo "== after the install =="
active="$(as_tester 'systemctl --user is-active agenteyes.service' || true)"
echo "  systemctl --user is-active agenteyes.service: $active"
[[ "$active" == "active" ]] || { as_tester 'journalctl --user -u agenteyes -n 40 --no-pager' || true; fail "the user unit is not active"; }
health="$(as_tester 'curl -fsS http://127.0.0.1:7882/health' | tr -d '\n ')"
echo "  GET http://127.0.0.1:7882/health: $health"
[[ "$health" == *'"ok":true'* ]] || fail "/health did not answer ok"
as_tester 'echo "  ffmpeg: $(command -v ffmpeg), from package $(dpkg -S "$(command -v ffmpeg)" | cut -d: -f1)"
           echo "  unit:   $(systemctl --user show -p FragmentPath --value agenteyes.service)"
           echo "  dotnet: $(command -v dotnet || echo "not installed - the asset needs no .NET")"
           echo "  app:    $(pgrep -af "^$HOME/.local/lib/agenteyes-app/AgentEyesDesktop" || echo "not running")"'
ok "installed from the release asset: user unit active, /health answering"
