"""Issue #113 pass 5 - LINUX QA driver. Not product code.

Usage: python3 drive.py <arm-name> <evidence-root> <host-binary> <action> <fps> [<seconds>]
  action:
    stop     - healthy capture for <seconds>, then POST /record/stop (offset measurement)
    sigstop  - criterion 1: healthy 8 s + 10 s, then SIGSTOP the capture ffmpeg (MINE); the product
               is expected to decide and kill it
    sigkill  - criterion 2: same, SIGKILL instead (that kill is MINE)
    early    - SIGKILL the capture ffmpeg <seconds> after ITS OWN process start (early-kill case)
Only ever signals an ffmpeg whose parent pid is the evidence host this script started AND whose
command line names this arm's scratch folder.
"""
import json, os, signal, subprocess, sys, time, datetime, urllib.request, urllib.error

arm, root, host_bin, action, fps = sys.argv[1:6]
secs = float(sys.argv[6]) if len(sys.argv) > 6 else 20.0
fps = int(fps)
PORT = 7991
MIC = "Family 17h (Models 00h-0fh) HD Audio Controller Analog Stereo"
base = f"http://127.0.0.1:{PORT}"
scratch = os.path.join(root, arm)
os.makedirs(scratch, exist_ok=False)
logf = open(os.path.join(scratch, "scenario.log"), "w", encoding="ascii", errors="replace")
t0 = time.monotonic()
CLK = os.sysconf_names and os.sysconf("SC_CLK_TCK")


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"


def log(m):
    line = f"{now()} +{time.monotonic() - t0:7.2f}s {m}"
    print(line, flush=True)
    logf.write(line + "\n")
    logf.flush()


def http(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


KEYS = ["State", "ElapsedSeconds", "CaptureLiveness", "CaptureFile", "CaptureBytes", "CaptureLastGrowthUtc",
        "CaptureSecondsSinceGrowth", "LastStopFailed", "LastCaptureFault", "LastCaptureFaultUtc", "LastStopError"]


def status(tag="", quiet=False):
    s = json.loads(http("GET", "/status")[1])
    if not quiet:
        log(f"/status{tag} " + json.dumps({k: s.get(k) for k in KEYS}))
    return s


def ppid_of(pid):
    try:
        with open(f"/proc/{pid}/stat") as f:
            return int(f.read().rsplit(")", 1)[1].split()[1])
    except OSError:
        return None


def cmdline(pid):
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            return [a.decode("utf-8", "replace") for a in f.read().split(b"\0") if a]
    except OSError:
        return None


def start_age(pid):
    """Seconds since the process started (from /proc/<pid>/stat field 22)."""
    with open(f"/proc/{pid}/stat") as f:
        st = int(f.read().rsplit(")", 1)[1].split()[19])
    with open("/proc/uptime") as f:
        up = float(f.read().split()[0])
    return up - st / CLK


def proc_state(pid):
    try:
        with open(f"/proc/{pid}/stat") as f:
            return f.read().rsplit(")", 1)[1].split()[0]
    except OSError:
        return "gone"


def find_writer(host_pid):
    """The capture ffmpeg: a direct child of the host whose command line writes raw.mp4 under scratch."""
    for p in os.listdir("/proc"):
        if not p.isdigit():
            continue
        pid = int(p)
        if ppid_of(pid) != host_pid:
            continue
        c = cmdline(pid)
        if c and os.path.basename(c[0]) == "ffmpeg" and any(a.endswith("raw.mp4") and a.startswith(scratch) for a in c):
            return pid, c
    return None, None


def verify_mine(pid, host_pid):
    c = cmdline(pid)
    ok = ppid_of(pid) == host_pid and c is not None and any(a.startswith(scratch) for a in c)
    if not ok:
        log(f"ABORT: pid {pid} is not this arm's capture (ppid={ppid_of(pid)} host={host_pid}) - not touching it")
        sys.exit(2)


host = subprocess.Popen([host_bin, scratch, str(PORT)], stdout=open(os.path.join(scratch, "host.out"), "w"),
                        stderr=subprocess.STDOUT)
log(f"evidence host pid={host.pid}")
for _ in range(120):
    try:
        http("GET", "/status")
        break
    except Exception:
        time.sleep(0.25)
log("host.out: " + open(os.path.join(scratch, "host.out")).read().replace("\n", " | "))

req = {"mode": "video", "screen": 1, "source": "mic", "mic": MIC, "fps": fps}
writer = None
if action == "early":
    # start the request in a thread-free way: fire it, then poll /proc for the writer at once
    import threading
    res = {}
    th = threading.Thread(target=lambda: res.update(r=http("POST", "/record/start", req)))
    th.start()
    deadline = time.monotonic() + 30
    while writer is None and time.monotonic() < deadline:
        writer, wcmd = find_writer(host.pid)
        if writer is None:
            time.sleep(0.01)
    if writer is None:
        log("ABORT: no capture writer appeared")
        sys.exit(2)
    log(f"capture writer pid={writer} parent={ppid_of(writer)} (host {host.pid}) age={start_age(writer):.3f}s")
    log("writer cmdline: " + " ".join(wcmd))
    while start_age(writer) < secs:
        time.sleep(0.005)
    verify_mine(writer, host.pid)
    age = start_age(writer)
    raw = [a for a in wcmd if a.endswith("raw.mp4")][0]
    size_before = os.path.getsize(raw) if os.path.exists(raw) else None
    os.kill(writer, signal.SIGKILL)
    log(f"INJECT: SIGKILL(pid {writer}) by THIS SCRIPT at writer age {age:.3f}s; raw.mp4 bytes just before={size_before}")
    th.join(timeout=60)
    log(f"POST /record/start -> {res.get('r', ('?', ''))[0]} {res.get('r', ('', ''))[1][:300]}")
    d = os.path.dirname(raw)
    time.sleep(3)
    s = status(" [after early kill]")
    if s["State"] == "recording":
        c, b = http("POST", "/record/stop", {})
        log(f"POST /record/stop -> {c} {b[:400]}")
else:
    code, body = http("POST", "/record/start", req)
    log(f"POST /record/start -> {code} {body[:300]}")
    s = status()
    d = s["Dir"]
    for _ in range(100):
        writer, wcmd = find_writer(host.pid)
        if writer:
            break
        time.sleep(0.1)
    log(f"dir {d}")
    log(f"capture writer pid={writer} parent={ppid_of(writer)} (host {host.pid})")
    log("writer cmdline: " + " ".join(wcmd))
    if action == "stop":
        time.sleep(secs / 2)
        status(" [healthy 1]")
        time.sleep(secs / 2)
        status(" [healthy 2]")
        c, b = http("POST", "/record/stop", {})
        log(f"POST /record/stop -> {c} {b[:400]}")
    else:
        time.sleep(8)
        status(" [healthy 1]")
        time.sleep(10)
        status(" [healthy 2]")
        verify_mine(writer, host.pid)
        inj = time.monotonic()
        if action == "sigstop":
            os.kill(writer, signal.SIGSTOP)
            log(f"INJECT: SIGSTOP(pid {writer}) by THIS SCRIPT; proc state now {proc_state(writer)}")
        else:
            os.kill(writer, signal.SIGKILL)
            log(f"INJECT: SIGKILL(pid {writer}) by THIS SCRIPT")
        flipped = named = None
        last = None
        lastlog = 0
        while time.monotonic() - inj < 120:
            time.sleep(0.5)
            s = status(quiet=True)
            sig = (s["State"], s.get("CaptureLiveness"), s.get("LastCaptureFault"), s.get("LastStopFailed"))
            el = time.monotonic() - inj
            if sig != last or el - lastlog >= 5:
                log(f"/status [+{el:5.1f}s after inject] writer={proc_state(writer)} "
                    + json.dumps({k: s.get(k) for k in KEYS}))
                last = sig
                lastlog = el
            if s["State"] != "recording" and flipped is None:
                flipped = el
                log(f"STATE LEFT 'recording' {el:.1f}s after the injection (State={s['State']!r})")
            if s.get("LastCaptureFault") and named is None:
                named = el
                log(f"FAULT NAMED {el:.1f}s after the injection: {s.get('LastCaptureFault')!r}")
            if flipped is not None and named is not None and s["State"] == "idle":
                break
        log(f"SUMMARY state_flip={flipped} fault_named={named} writer_after={proc_state(writer)}")
        if proc_state(writer) not in ("gone", "Z"):
            verify_mine(writer, host.pid)
            log(f"CLEANUP: writer {writer} still alive ({proc_state(writer)}) - SIGCONT+SIGKILL by THIS SCRIPT")
            os.kill(writer, signal.SIGCONT)
            os.kill(writer, signal.SIGKILL)

# follow the manifest, not /status (#159)
mf = os.path.join(d, "manifest.json")
mux = None
for _ in range(90):
    time.sleep(1)
    try:
        m = json.load(open(mf, encoding="utf-8-sig"))
    except Exception:
        continue
    mux = ((m.get("PostProcessing") or {}).get("mux") or {})
    if mux.get("State") in ("done", "failed") and m.get("PendingMux") is None:
        break
    if mux.get("State") == "failed":
        break
log("manifest mux: " + json.dumps(mux))
status(" [final]")
files = {n: os.path.getsize(os.path.join(d, n)) for n in sorted(os.listdir(d)) if os.path.isfile(os.path.join(d, n))}
log("files: " + json.dumps(files))
m = json.load(open(mf, encoding="utf-8-sig"))
log("manifest fields: " + json.dumps({k: m.get(k) for k in m if k.startswith("Capture") or k in
                                       ("DurationSeconds", "PostProcessing", "PendingMux")}))
logs = os.path.join(scratch, "LocalAppData", "AgentEyes", "logs")
if os.path.isdir(logs):
    for lf in sorted(os.listdir(logs)):
        for line in open(os.path.join(logs, lf), encoding="utf-8", errors="replace"):
            if any(t in line for t in ("CAPTURE FAULT", "Abandon", "PublishVerdict", "mux FAILED", "mux failed")):
                log("app log: " + line.strip()[:400])
open(os.path.join(scratch, "harness.stop"), "w").close()
host.wait(timeout=180)
log(f"evidence host exited {host.returncode}")
print("DIR=" + d)
