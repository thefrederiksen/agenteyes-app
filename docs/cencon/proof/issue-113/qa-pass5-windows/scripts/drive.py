"""QA (Windows half) driver for issue #113 pass 5. Independent of the developer's scripts.

Usage: python drive.py <run-name> <host-dir> <mode> [--fps N] [--watch S] [--kill-at S] [--record S]
  host-dir : folder holding AgentEyes.Tests.exe (evidence host) and agenteyes.exe from the SAME tree
  mode     : gate | stall | kill | plain | earlykill
Every run gets a NEW folder under EVID (never reused, never deleted). Only an ffmpeg whose parent
is the evidence host THIS script started is ever suspended or terminated.
"""
import sys, os, json, time, subprocess, datetime, urllib.request, ctypes, argparse, glob

EVID = os.path.join(os.environ["USERPROFILE"], "AgentEyes-QA-evidence", "issue113-pass5-qa")
PORT = 7990
MIC = "Microphone (2- Realtek(R) Audio)"
BASE = f"http://127.0.0.1:{PORT}"

ap = argparse.ArgumentParser()
ap.add_argument("run"); ap.add_argument("hostdir"); ap.add_argument("mode")
ap.add_argument("--fps", type=int, default=30); ap.add_argument("--watch", type=int, default=120)
ap.add_argument("--kill-at", type=float, default=1.0); ap.add_argument("--record", type=int, default=40)
a = ap.parse_args()

run_dir = os.path.join(EVID, a.run)
os.makedirs(run_dir, exist_ok=False)
logf = open(os.path.join(run_dir, "scenario.log"), "w", encoding="ascii", errors="replace")
T0 = time.monotonic()
def utc(): return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"
def log(m):
    line = f"{utc()} +{time.monotonic()-T0:7.1f}s {m}"
    print(line, flush=True); logf.write(line + "\n"); logf.flush()

def http(method, path, body=None, timeout=60):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r: return r.status, r.read().decode()
    except urllib.error.HTTPError as e: return e.code, e.read().decode()

KEYS = ["State", "CaptureLiveness", "CaptureBytes", "CaptureSecondsSinceGrowth", "LastStopFailed", "LastCaptureFault"]
def status(tag):
    c, b = http("GET", "/status"); s = json.loads(b)
    log(f"/status [{tag}] " + " ".join(f"{k}={s.get(k)}" for k in KEYS))
    return s

# ---- Windows process helpers --------------------------------------------------------------
k32 = ctypes.WinDLL("kernel32", use_last_error=True); ntdll = ctypes.WinDLL("ntdll")
PROCESS_ALL = 0x1F0FFF
def children(ppid):
    out = subprocess.run(["powershell", "-NoProfile", "-c",
        f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={ppid}' | ForEach-Object {{ \"$($_.ProcessId)|$($_.Name)|$($_.CommandLine)\" }}"],
        capture_output=True, text=True).stdout
    return [l.split("|", 2) for l in out.splitlines() if l.strip()]
def ffmpeg_child(host_pid):
    kids = [c for c in children(host_pid) if c[1].lower() == "ffmpeg.exe" and "raw.mp4" in c[2]]
    return int(kids[0][0]) if len(kids) == 1 else None
def pid_alive(pid):
    h = k32.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED
    if not h: return False
    code = ctypes.c_ulong(); k32.GetExitCodeProcess(h, ctypes.byref(code)); k32.CloseHandle(h)
    return code.value == 259
def suspend(pid):
    h = k32.OpenProcess(PROCESS_ALL, False, pid); r = ntdll.NtSuspendProcess(h); k32.CloseHandle(h); return r
def terminate(pid, code):
    h = k32.OpenProcess(PROCESS_ALL, False, pid); r = k32.TerminateProcess(h, code); k32.CloseHandle(h); return r

# ---- the host -------------------------------------------------------------------------------
scratch = os.path.join(run_dir, "host")
os.makedirs(scratch)
host_exe = os.path.join(a.hostdir, "AgentEyes.Tests.exe"); cli = os.path.join(a.hostdir, "agenteyes.exe")
hostout = open(os.path.join(run_dir, "host.stdout.txt"), "w")
host = subprocess.Popen([host_exe, scratch, str(PORT)], stdout=hostout, stderr=subprocess.STDOUT)
log(f"host pid={host.pid} exe={host_exe}")
for _ in range(60):
    try:
        if http("GET", "/health", timeout=2)[0] == 200: break
    except Exception: pass
    time.sleep(0.5)
log("host stdout: " + open(os.path.join(run_dir, "host.stdout.txt")).read().replace("\n", " | "))

def rec_dir():
    d = sorted(os.path.dirname(x) for x in glob.glob(os.path.join(scratch, "Videos", "**", "manifest.json"), recursive=True))
    return d[-1] if d else None
def listing(d, tag):
    items = sorted(os.listdir(d))
    log(f"dir [{tag}] " + ", ".join(f"{n}={os.path.getsize(os.path.join(d,n))}" for n in items if os.path.isfile(os.path.join(d,n))))
def manifest(d, tag):
    p = os.path.join(d, "manifest.json")
    m = json.load(open(p, encoding="utf-8-sig"))
    log(f"manifest [{tag}] PendingMux={json.dumps(m.get('PendingMux'))} PostProcessing={json.dumps(m.get('PostProcessing'))} "
        f"CaptureFault={m.get('CaptureFault')} CaptureFaultDetectedUtc={m.get('CaptureFaultDetectedUtc')} "
        f"CapturedSeconds={m.get('CapturedSeconds')} DurationSeconds={m.get('DurationSeconds')}")
    return m
def wait_mux(d, limit=180):
    t = time.monotonic()
    while time.monotonic() - t < limit:
        m = json.load(open(os.path.join(d, "manifest.json"), encoding="utf-8-sig"))
        st = ((m.get("PostProcessing") or {}).get("mux") or {}).get("State")
        if st in ("done", "failed"): log(f"mux State={st} after {time.monotonic()-t:.1f}s"); return st
        time.sleep(1)
    log("mux did not finish in the limit"); return None

def start():
    c, b = http("POST", "/record/start", {"mode": "video", "screen": 1, "source": "mic", "mic": MIC, "fps": a.fps})
    log(f"POST /record/start fps={a.fps} -> {c} {b[:300]}")
    return c

def stop():
    c, b = http("POST", "/record/stop", {}, timeout=180)
    log(f"POST /record/stop -> {c} {b[:700]}")

def healthy_two():
    time.sleep(6); s1 = status("healthy 1"); time.sleep(10); s2 = status("healthy 2")
    assert (s2.get("CaptureBytes") or 0) > (s1.get("CaptureBytes") or 0), "capture not advancing - not healthy"
    return s2

try:
    if a.mode == "earlykill":
        import psutil
        hp = psutil.Process(host.pid)
        start()
        proc = None; t = time.monotonic()
        while proc is None and time.monotonic() - t < 10:
            ks = [c for c in hp.children() if c.name().lower() == "ffmpeg.exe"]
            ks = [c for c in ks if "raw.mp4" in " ".join(c.cmdline())]
            if len(ks) == 1: proc = ks[0]
            else: time.sleep(0.01)
        pid = proc.pid; created = proc.create_time(); cmd = " ".join(proc.cmdline())
        found_age = time.time() - created
        while time.time() - created < a.kill_at: time.sleep(0.005)
        d = rec_dir(); raw = os.path.join(d, "raw.mp4")
        size_before = os.path.getsize(raw) if os.path.exists(raw) else None
        age = time.time() - created
        r = terminate(pid, 137)
        log(f"INJECT (MINE): TerminateProcess(ffmpeg pid {pid}, child of my host {host.pid}) at ffmpeg age {age:.2f}s (found at age {found_age:.2f}s) -> {r}; raw.mp4 size just before={size_before}")
        log(f"capture ffmpeg command line: {cmd}")
        time.sleep(1.0); log(f"raw.mp4 size 1s after kill = {os.path.getsize(raw) if os.path.exists(raw) else None}")
        for i in range(12):
            s = status(f"after kill {i}")
            if s.get("State") == "idle": break
            time.sleep(2)
        listing(d, "after kill")
        wait_mux(d, 60); manifest(d, "final"); listing(d, "final")
    else:
        start(); s = healthy_two(); d = rec_dir(); log(f"recording dir {os.path.basename(d)}")
        pid = ffmpeg_child(host.pid); log(f"capture ffmpeg pid={pid} (parent = my host {host.pid})")
        if a.mode == "gate":
            manifest(d, "before second process"); listing(d, "before second process")
            t = time.monotonic()
            p = subprocess.run([cli, "package", d, "--no-frames"], capture_output=True, text=True)
            el = time.monotonic() - t
            open(os.path.join(run_dir, "second-process.txt"), "w", encoding="ascii", errors="replace").write(
                f"exit={p.returncode} elapsed={el:.2f}s\n--- stdout\n{p.stdout}\n--- stderr\n{p.stderr}\n")
            log(f"SECOND PROCESS agenteyes package --no-frames: exit={p.returncode} in {el:.2f}s; "
                f"out={(p.stdout + p.stderr).strip()[-600:]!r}")
            listing(d, "right after second process"); manifest(d, "right after second process")
            log(f"capture ffmpeg pid {pid} alive={pid_alive(pid)}")
            n_growing = n_polls = 0; last = None
            t = time.monotonic()
            while time.monotonic() - t < a.watch:
                time.sleep(10); s = status(f"watch {n_polls+1}"); n_polls += 1
                rs = os.path.getsize(os.path.join(d, "raw.mp4")) if os.path.exists(os.path.join(d, "raw.mp4")) else None
                rec = os.path.join(d, "recording.mp4")
                log(f"   disk: raw.mp4={rs} recording.mp4={'EXISTS '+str(os.path.getsize(rec)) if os.path.exists(rec) else 'absent'} ffmpeg alive={pid_alive(pid)}")
                if s.get("State") == "recording" and s.get("CaptureLiveness") == "growing" and (last is None or rs > last): n_growing += 1
                last = rs
            log(f"WATCH SUMMARY: growing (state+liveness+disk size) at {n_growing} of {n_polls} polls")
            stop(); wait_mux(d); manifest(d, "final"); listing(d, "final")
        elif a.mode in ("stall", "kill"):
            if a.mode == "stall":
                r = suspend(pid); log(f"INJECT (MINE): NtSuspendProcess(ffmpeg pid {pid}) -> {r}")
            else:
                r = terminate(pid, 137); log(f"INJECT (MINE): TerminateProcess(ffmpeg pid {pid}, 137) -> {r}")
            t_inj = time.monotonic()
            while time.monotonic() - t_inj < 90:
                time.sleep(2); s = status(f"+{time.monotonic()-t_inj:.1f}s after inject")
                if s.get("State") != "recording": break
            log(f"ffmpeg pid {pid} alive after verdict={pid_alive(pid)}")
            if pid_alive(pid):
                log("WARNING: the suspended capture is still alive - the product did not end it; terminating MY suspended child")
                terminate(pid, 1)
            wait_mux(d); manifest(d, "final"); listing(d, "final")
        elif a.mode == "plain":
            time.sleep(max(0, a.record - 16)); status("before stop"); stop(); wait_mux(d); manifest(d, "final"); listing(d, "final")
finally:
    open(os.path.join(scratch, "harness.stop"), "w").close()
    try: host.wait(60)
    except Exception: log("host did not exit in 60s")
    log(f"host exit={host.poll()}")
    alog = glob.glob(os.path.join(scratch, "LocalAppData", "**", "*.log"), recursive=True)
    log("app logs: " + ", ".join(os.path.basename(x) for x in alog))
