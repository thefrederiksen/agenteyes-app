#!/usr/bin/env python3
"""Issue #156 pass 2, D1: how long does a REAL orphaned x11grab+pulse capture take to finish its
MP4 (write the moov) and exit after SIGTERM?

Reproduces the reaper's conditions: the ffmpeg's parent is gone (an intermediate process starts
it with pipes on stdin/stdout/stderr and exits at once, so stdin is at EOF and stderr is a broken
pipe), and SIGPIPE is ignored (inherited, as from the .NET runtime). The args are the recorder's
X11VideoCapture args for a full 1920x1080 screen at 30 fps, crf 23, with the mic.

Usage: sigterm_bound.py <outdir> <seconds-before-sigterm> <tag>
Only the pid this script starts is ever signalled.
"""
import json, os, signal, subprocess, sys, time

outdir, secs, tag = sys.argv[1], float(sys.argv[2]), sys.argv[3]
os.makedirs(outdir, exist_ok=True)
out = os.path.join(outdir, f"raw-{tag}.mp4")
env = dict(os.environ, DISPLAY=":0", XAUTHORITY="/run/user/1000/gdm/Xauthority", XDG_RUNTIME_DIR="/run/user/1000")
args = ["/usr/bin/ffmpeg", "-hide_banner", "-y", "-f", "x11grab", "-thread_queue_size", "1024", "-framerate", "30",
        "-video_size", "1920x1080", "-draw_mouse", "1", "-i", ":0+0,0",
        "-f", "pulse", "-thread_queue_size", "1024", "-fragment_size", "3840",
        "-i", "alsa_input.pci-0000_08_00.3.analog-stereo",
        "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
        "-c:a", "aac", "-b:a", "128k", out]

# The intermediate parent: starts ffmpeg with pipes and SIGPIPE left ignored, prints its pid, exits.
starter = (
    "import os,signal,subprocess,sys,json\n"
    "signal.signal(signal.SIGPIPE, signal.SIG_IGN)\n"
    "p=subprocess.Popen(json.loads(sys.argv[1]),stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,"
    "restore_signals=False,env=json.loads(sys.argv[2]),start_new_session=False)\n"
    "print(p.pid,flush=True)\n"
    "os._exit(0)\n")
r = subprocess.run([sys.executable, "-c", starter, json.dumps(args), json.dumps(env)], capture_output=True, text=True, check=True)
pid = int(r.stdout.strip())

def state(pid):
    try:
        with open(f"/proc/{pid}/stat") as f: return f.read().rsplit(")", 1)[1].split()[0]
    except FileNotFoundError: return None

def sigign(pid):
    with open(f"/proc/{pid}/status") as f:
        for line in f:
            if line.startswith("SigIgn"): return line.split()[1]

ppid = open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()[1]
print(f"[{tag}] ffmpeg pid {pid} orphaned (ppid now {ppid}), SigIgn={sigign(pid)}; capturing {secs}s", flush=True)
time.sleep(secs)
if state(pid) in (None, "Z"):
    print(json.dumps({"tag": tag, "error": "ffmpeg exited before SIGTERM"})); sys.exit(1)
size_before = os.path.getsize(out)
t0 = time.monotonic()
os.kill(pid, signal.SIGTERM)
while state(pid) not in (None, "Z"):
    if time.monotonic() - t0 > 120:
        print(json.dumps({"tag": tag, "error": "still running 120 s after SIGTERM"})); os.kill(pid, signal.SIGKILL); sys.exit(1)
    time.sleep(0.005)
exit_s = time.monotonic() - t0

probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", out],
                       capture_output=True, text=True)
info = json.loads(probe.stdout or "{}")
print(json.dumps({"tag": tag, "captured_s": secs, "exit_after_sigterm_s": round(exit_s, 3),
                  "size_before_sigterm": size_before, "size_after": os.path.getsize(out),
                  "probe_ok": probe.returncode == 0, "duration": (info.get("format") or {}).get("duration"),
                  "streams": [s["codec_type"] for s in info.get("streams", [])],
                  "probe_err": probe.stderr.strip()[:200]}), flush=True)
