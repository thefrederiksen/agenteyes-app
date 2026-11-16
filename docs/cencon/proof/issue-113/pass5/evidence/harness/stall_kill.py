"""Issue #113 pass 5, item 5: stall / kill a REAL Windows screen+mic capture and read the watchdog's verdict.

Usage: python stall_kill.py <arm> <evidence-root> <host-exe> suspend|kill
 suspend - NtSuspendProcess on the capture ffmpeg (the Windows SIGSTOP): alive, producing nothing.
           The product's watchdog is expected to decide and ABANDON (kill) it - that kill is the product's.
 kill    - TerminateProcess on the capture ffmpeg (the Windows SIGKILL) - that kill is this script's.
Only ever touches the ffmpeg whose parent is the evidence host this script started.
"""
import ctypes, json, os, subprocess, sys, time, urllib.request, datetime
arm, root, host_exe, how = sys.argv[1:5]
PORT = 7990
MIC = "Microphone (2- Realtek(R) Audio)"
base = f"http://127.0.0.1:{PORT}"
scratch = os.path.join(root, arm)
os.makedirs(scratch, exist_ok=False)
logf = open(os.path.join(scratch, "scenario.log"), "w", encoding="ascii", errors="replace")
t0 = time.monotonic()
def now(): return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"
def log(m):
    line = f"{now()} +{time.monotonic() - t0:7.1f}s {m}"; print(line, flush=True); logf.write(line + "\n"); logf.flush()
def http(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=60) as r: return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e: return e.code, e.read().decode("utf-8", "replace")
KEYS = ["State", "ElapsedSeconds", "CaptureLiveness", "CaptureFile", "CaptureBytes", "CaptureLastGrowthUtc",
        "CaptureSecondsSinceGrowth", "LastStopFailed", "LastCaptureFault", "LastCaptureFaultUtc", "LastStopError"]
def status(tag=""):
    s = json.loads(http("GET", "/status")[1]); log(f"/status{tag} " + json.dumps({k: s.get(k) for k in KEYS})); return s
def parent_of(pid):
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          f"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId"],
                         capture_output=True, text=True).stdout.strip()
    return int(out) if out.isdigit() else None
def alive(pid):
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
    return str(pid) in out

host = subprocess.Popen([host_exe, scratch, str(PORT)], stdout=open(os.path.join(scratch, "host.out"), "w"), stderr=subprocess.STDOUT)
log(f"evidence host pid={host.pid}")
for _ in range(60):
    try: http("GET", "/status"); break
    except Exception: time.sleep(0.5)
log("host.out: " + open(os.path.join(scratch, "host.out")).read().replace("\n", " | "))
code, body = http("POST", "/record/start", {"mode": "video", "screen": 1, "source": "mic", "mic": MIC, "fps": 30})
log(f"POST /record/start -> {code}")
d = status()
d = json.loads(http("GET", "/status")[1])["Dir"]
log(f"dir {d}")

# criterion 3's shape: healthy, healthy (10 s apart), then the stall
time.sleep(8); status(" [healthy 1]")
time.sleep(10); status(" [healthy 2]")
owner = json.load(open(os.path.join(d, "capture.owner.json"), encoding="utf-8-sig"))
writer = [w for w in owner["Writers"] if w["Label"] == "video"][0]["Pid"]
ppid = parent_of(writer)
log(f"capture writer pid={writer} parent={ppid} (evidence host {host.pid})")
if ppid != host.pid:
    log("ABORT: the writer's parent is not the evidence host - not touching it"); sys.exit(2)

k32 = ctypes.WinDLL("kernel32", use_last_error=True); ntdll = ctypes.WinDLL("ntdll")
k32.OpenProcess.restype = ctypes.c_void_p
h = k32.OpenProcess(0x0800 | 0x0001 | 0x00100000, False, writer)   # SUSPEND_RESUME | TERMINATE | SYNCHRONIZE
if not h: log(f"OpenProcess failed {ctypes.get_last_error()}"); sys.exit(2)
inj = time.monotonic(); inj_utc = now()
if how == "suspend":
    rc = ntdll.NtSuspendProcess(ctypes.c_void_p(h)); log(f"INJECT: NtSuspendProcess(pid {writer}) rc={rc} by THIS SCRIPT at {inj_utc}")
else:
    rc = k32.TerminateProcess(ctypes.c_void_p(h), 137); log(f"INJECT: TerminateProcess(pid {writer}, 137) rc={rc} by THIS SCRIPT at {inj_utc}")

decided = None
while time.monotonic() - inj < 120:
    time.sleep(2)
    s = status()
    if s["State"] != "recording" and decided is None:
        decided = time.monotonic() - inj
        log(f"STATE LEFT 'recording' {decided:.1f}s after the injection; fault named: {s.get('LastCaptureFault')!r}")
    if s["State"] == "idle": break
log(f"writer pid {writer} alive after the verdict: {alive(writer)}"
    + (" (the product's Abandon killed the suspended process)" if how == "suspend" and not alive(writer) else ""))
if alive(writer):
    log(f"CLEANUP: the writer {writer} is still alive - resuming and terminating it (THIS SCRIPT)")
    ntdll.NtResumeProcess(ctypes.c_void_p(h)); k32.TerminateProcess(ctypes.c_void_p(h), 1)
k32.CloseHandle(ctypes.c_void_p(h))

for _ in range(60):
    time.sleep(2)
    m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
    if m.get("PendingMux") is None and os.path.exists(os.path.join(d, "recording.mp4")): break
status(" [final]")
files = {n: os.path.getsize(os.path.join(d, n)) for n in sorted(os.listdir(d)) if os.path.isfile(os.path.join(d, n))}
log("files: " + json.dumps(files))
m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
log("manifest fault fields: " + json.dumps({k: m.get(k) for k in m if k.startswith("Capture") or k in ("DurationSeconds", "PostProcessing")}))
for lf in os.listdir(os.path.join(scratch, "LocalAppData", "AgentEyes", "logs")):
    for line in open(os.path.join(scratch, "LocalAppData", "AgentEyes", "logs", lf), encoding="utf-8", errors="replace"):
        if "CAPTURE FAULT" in line or "Abandon" in line or "PublishVerdict" in line: log("app log: " + line.strip()[:400])
open(os.path.join(scratch, "harness.stop"), "w").close(); host.wait(timeout=120)
log(f"evidence host exited {host.returncode}"); print("DIR=" + d)
