#!/usr/bin/env bash
# Issue #113: inject a fault into a live screen+mic capture and measure what the service does.
#
#   inject-fault.sh STOP   freeze the capture ffmpeg (SIGSTOP) - alive, producing nothing (AC1)
#   inject-fault.sh KILL   kill it outright (SIGKILL) (AC2)
#   inject-fault.sh EARLY  freeze it in its FIRST SECOND, stop the recording ~6.5 s in with
#                          '-d {}', then resume it: the log signature of the 2026-11-14 Linux
#                          recording ("pipeline did not reach ~6500ms within 5s (reached 0ms)")
#   inject-fault.sh STARVE run it at ~10% of real time (SIGSTOP 0.9 s / SIGCONT 0.1 s) for the
#                          given seconds, then stop the recording - the starved-capture shape of the
#                          2026-11-14 Linux recording (reported 6.46 s, saved 1.43 s)
#
# Drives the service only through the Control API on 127.0.0.1:7882. Writes every /status body it
# quotes to $OUT (default ./inject-<kind>-<time>/), with the UTC clock time of each poll.
# Environment: MIC (a fragment of the microphone name), OUT.
set -uo pipefail
kind="${1:?usage: inject-fault.sh STOP|KILL|EARLY|STARVE [starve-seconds]}"
api=http://127.0.0.1:7882
mic="${MIC:-Family 17h}"
out="${OUT:-./inject-${kind,,}-$(date -u +%H%M%S)}"
mkdir -p "$out"
now() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
secs() { date +%s.%N; }
status() { curl -s "$api/status"; }
field() { python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('$1'))"; }
poll() { # poll <name>: save the full /status body with its clock time
    local t; t=$(now); status > "$out/$1.json"
    echo "$t" > "$out/$1.time"
    echo "[$t] $1: State=$(field State < "$out/$1.json") CaptureLiveness=$(field CaptureLiveness < "$out/$1.json") CaptureBytes=$(field CaptureBytes < "$out/$1.json") CaptureLastGrowthUtc=$(field CaptureLastGrowthUtc < "$out/$1.json") ElapsedSeconds=$(field ElapsedSeconds < "$out/$1.json") Level=$(field Level < "$out/$1.json")"
}

[[ "$(status | field State)" == "idle" ]] || { echo "[FAIL] the service is not idle - not starting a test recording"; exit 1; }
echo "[$(now)] start: $(curl -s -H 'Content-Type: application/json' -d "{\"mode\":\"video\",\"screen\":1,\"source\":\"mic\",\"mic\":\"$mic\"}" "$api/record/start" | tr -d '\n')"

if [[ "$kind" == "EARLY" ]]; then
    for _ in $(seq 1 20); do pid=$(pgrep -f 'ffmpeg.*(raw|recording)\.mp4' | head -1); [[ -n "$pid" ]] && break; sleep 0.05; done
    [[ -n "$pid" ]] || { echo "[FAIL] no capture ffmpeg found"; exit 1; }
    kill -STOP "$pid"; echo "[$(now)] froze capture ffmpeg $pid at once"
    sleep 6.5; poll frozen
    # Resume it 9 s into the stop - after the 5 s drain has given up and while the service waits for
    # the quit it sent - so ffmpeg exits by itself on that quit, as the 2026-11-14 one did.
    ( sleep 9; kill -CONT "$pid" 2>/dev/null ) &
    echo "[$(now)] stop: $(curl -s -H 'Content-Type: application/json' -d '{}' "$api/record/stop" | tr -d '\n')"
    wait
    poll after
    exit 0
fi

sleep 10; poll healthy1
sleep 10; poll healthy2
pid=$(pgrep -f 'ffmpeg.*(raw|recording)\.mp4' | head -1)
[[ -n "$pid" ]] || { echo "[FAIL] no capture ffmpeg found"; exit 1; }
echo "capture ffmpeg pid=$pid: $(tr '\0' ' ' < /proc/$pid/cmdline | cut -c1-160)..."

if [[ "$kind" == "STARVE" ]]; then
    dur="${2:-8}"
    # 0.9 s frozen / 0.1 s running, kept up THROUGH the stop, until the process is gone.
    ( while kill -0 "$pid" 2>/dev/null; do kill -STOP "$pid" 2>/dev/null; sleep 0.9; kill -CONT "$pid" 2>/dev/null; sleep 0.1; done ) &
    starver=$!
    echo "[$(now)] starving pid $pid (0.9 s stopped / 0.1 s running) for ${dur}s, then stopping the recording"
    sleep "$dur"
    echo "[$(now)] stop: $(curl -s -H 'Content-Type: application/json' -d '{}' "$api/record/stop" | tr -d '\n')"
    kill "$starver" 2>/dev/null; kill -CONT "$pid" 2>/dev/null
    poll after
    exit 0
fi

kill "-$kind" "$pid"; injected=$(secs); echo "[$(now)] injected SIG$kind into pid $pid"
echo "$(now)" > "$out/injected.time"
# Poll every half second from the injection, so the latency is measured from the fault itself. The
# "stalled" body (AC3's third poll) is taken 10 s in - if the recording is still running by then.
took_stalled=0
while :; do
    st=$(status | field State)
    [[ "$st" != "recording" ]] && break
    if (( took_stalled == 0 )) && (( $(echo "$(secs) - $injected >= 10" | bc) )); then poll stalled; took_stalled=1; fi
    if (( $(echo "$(secs) - $injected > 120" | bc) )); then echo "[FAIL] still recording 120 s after the fault"; break; fi
    sleep 0.5
done
latency=$(echo "$(secs) - $injected" | bc); echo "[$(now)] State left 'recording' after ${latency}s (state now: $st)"
echo "$latency" > "$out/latency.txt"
poll left
# Wait for idle BEFORE resuming anything: a resumed ffmpeg would read the service's quit and exit
# cleanly, and the abandon path's kill would go unexercised.
for _ in $(seq 1 60); do [[ "$(status | field State)" == "idle" ]] && break; sleep 1; done
poll after
if kill -0 "$pid" 2>/dev/null; then echo "[FAIL] capture ffmpeg $pid is still alive after the service went idle"; kill -CONT "$pid"; else echo "capture ffmpeg $pid is gone"; fi
