"""Issue #113 pass 5: the Review Gate's scenario on a REAL Windows screen+mic capture.

A capture runs in the evidence host (production RecordingService). From a SECOND process, the real
'agenteyes package <dir> --no-frames' drives the finalize while the capture is live. Then we keep
polling /status well past the watchdog's 45 s window, stop normally, and decode the result.

Usage: python gate_scenario.py <arm-name> <evidence-root> <host-exe> <agenteyes-exe> [fps] [watch-after-package-s] [mode]
mode: 'package' (default) or 'none' (no second process - a plain recording, for offset/decode work)
"""
import json, os, subprocess, sys, time, urllib.request, datetime

arm, root, host_exe, cli_exe = sys.argv[1:5]
fps = int(sys.argv[5]) if len(sys.argv) > 5 else 30
watch_after = int(sys.argv[6]) if len(sys.argv) > 6 else 120
mode = sys.argv[7] if len(sys.argv) > 7 else 'package'
PORT = 7990
MIC = "Microphone (2- Realtek(R) Audio)"
base = f"http://127.0.0.1:{PORT}"
scratch = os.path.join(root, arm)
os.makedirs(scratch, exist_ok=False)          # never reuse (and never delete) an evidence folder
logf = open(os.path.join(scratch, "scenario.log"), "w", encoding="ascii", errors="replace")
t0 = time.monotonic()

def now(): return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"
def log(msg):
    line = f"{now()} +{time.monotonic() - t0:7.1f}s {msg}"
    print(line, flush=True); logf.write(line + "\n"); logf.flush()

def http(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=60) as r: return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e: return e.code, e.read().decode("utf-8", "replace")

KEYS = ["State", "ElapsedSeconds", "Dir", "CaptureLiveness", "CaptureFile", "CaptureBytes",
        "CaptureSecondsSinceGrowth", "LastStopFailed", "LastCaptureFault", "LastStopError"]
def status(tag=""):
    code, body = http("GET", "/status")
    s = json.loads(body)
    log(f"/status{tag} " + json.dumps({k: s.get(k) for k in KEYS}))
    return s

def files(d):
    out = {}
    for n in sorted(os.listdir(d)):
        p = os.path.join(d, n)
        if os.path.isfile(p): out[n] = os.path.getsize(p)
    return out

host = subprocess.Popen([host_exe, scratch, str(PORT)], stdout=open(os.path.join(scratch, "host.out"), "w"),
                        stderr=subprocess.STDOUT)
log(f"evidence host pid={host.pid} exe={host_exe}")
for _ in range(60):
    try: http("GET", "/status"); break
    except Exception: time.sleep(0.5)
log("host.out: " + open(os.path.join(scratch, "host.out")).read().replace("\n", " | "))

code, body = http("POST", "/record/start", {"mode": "video", "screen": 1, "source": "mic", "mic": MIC, "fps": fps})
log(f"POST /record/start fps={fps} -> {code} {body[:300]}")
s = status()
d = s["Dir"]
log(f"recording dir {d}")

# healthy run-up: 12 s, the gate's timing
for _ in range(6): time.sleep(2); status()
log("files before the second process: " + json.dumps(files(d)))
for n in ("capture.owner.json",):
    p = os.path.join(d, n)
    if os.path.exists(p): log(f"{n}: " + open(p, encoding="utf-8").read().replace("\n", " "))
m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
log("manifest at start: PendingMux=" + json.dumps(m.get("PendingMux")))

if mode == 'package':
    log(f"SECOND PROCESS: {cli_exe} package {d} --no-frames")
    t = time.monotonic()
    p = subprocess.run([cli_exe, "package", d, "--no-frames"], capture_output=True, text=True, timeout=600)
    log(f"second process exit={p.returncode} after {time.monotonic() - t:.1f}s")
    for stream, txt in (("stdout", p.stdout), ("stderr", p.stderr)):
        for line in txt.strip().splitlines(): log(f"  package {stream}: {line}")
    log("files right after the second process: " + json.dumps(files(d)))
    m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
    log("manifest after the second process: PendingMux=" + json.dumps(m.get("PendingMux"))
        + " PostProcessing=" + json.dumps(m.get("PostProcessing")) + " DurationSeconds=" + json.dumps(m.get("DurationSeconds")))

end = time.monotonic() + watch_after
while time.monotonic() < end:
    time.sleep(10)
    s = status()
    log("files: " + json.dumps(files(d)))
    if s["State"] != "recording":
        log("RECORDING ENDED WITHOUT A STOP")
        break

if s["State"] == "recording":
    code, body = http("POST", "/record/stop", {})
    log(f"POST /record/stop -> {code} {body[:600]}")
for _ in range(90):
    time.sleep(2)
    m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
    if m.get("PendingMux") is None and os.path.exists(os.path.join(d, "recording.mp4")): break
status(" after stop")
log("files after stop + mux: " + json.dumps(files(d)))
m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
log("manifest final: " + json.dumps({k: m.get(k) for k in ("DurationSeconds", "CaptureFault", "CaptureFaultDetail",
    "PendingMux", "PostProcessing", "VideoStartOffsetSeconds", "MicOffsetSeconds")}))

open(os.path.join(scratch, "harness.stop"), "w").close()
host.wait(timeout=120)
log(f"evidence host exited {host.returncode}")
print("DIR=" + d)
