#!/usr/bin/env python3 -I
# QA #113 macOS driver. usage: drive.py <port> <harness_pid> <fps> <NONE|STOP|KILL> <inject_after_s> <observe_s> <outfile>
# Only ever signals an ffmpeg whose PARENT is <harness_pid> (the QA process this seat started).
import json, os, signal, subprocess, sys, time, urllib.request, glob
port, hpid, fps, mode, inj_after, observe, outf = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4].upper(), float(sys.argv[5]), float(sys.argv[6]), sys.argv[7]
API = f"http://127.0.0.1:{port}"
log = open(outf, "w")
def say(s):
    line = f"{time.strftime('%H:%M:%S')}.{int(time.time()*1000)%1000:03d} {s}"; print(line, flush=True); log.write(line + "\n"); log.flush()
def api(path, body=None):
    req = urllib.request.Request(API + path) if body is None else urllib.request.Request(API + path, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=30) as r: return json.loads(r.read().decode() or "{}")
def mine():
    r = subprocess.run(["ps", "-eo", "pid=,ppid=,state=,command="], capture_output=True, text=True).stdout
    out = []
    for l in r.splitlines():
        p = l.split(None, 3)
        if len(p) == 4 and int(p[1]) == hpid and "ffmpeg" in p[3]: out.append((int(p[0]), p[2], p[3]))
    return out
KEYS = ["State", "ElapsedSeconds", "CaptureLiveness", "CaptureBytes", "CaptureLastGrowthUtc", "CaptureFault", "LastStopFailed", "LastStopError", "Dir"]
def brief(st): return {k: st.get(k) for k in st if k in KEYS or k.startswith("Capture")}
t0 = time.time()
st = api("/status"); say(f"pre /status State={st['State']}")
assert st["State"] == "idle", "not idle"
r = api("/record/start", {"mode": "video", "screen": 1, "source": "mic", "mic": "Microsoft Teams Audio", "fps": fps}); say(f"/record/start -> {json.dumps(r)}")
screen = None; injected = None; inj_t = None
while True:
    el = time.time() - t0
    try: st = api("/status")
    except Exception as e: say(f"status error {e}"); time.sleep(1); continue
    procs = mine()
    if screen is None:
        for p in procs:
            if "raw.mp4" in p[2]: screen = p[0]; say(f"screen ffmpeg pid={screen} (parent {hpid}) cmd={p[2]}")
    pst = next((p[1] for p in mine() if p[0] == screen), "gone") if screen else "-"
    say(f"T+{el:6.2f}s{'' if inj_t is None else f' (inj+{time.time()-inj_t:6.2f}s)'} child={pst} {json.dumps(brief(st))}")
    if mode != "NONE" and injected is None and el >= inj_after and screen:
        sig = signal.SIGSTOP if mode == "STOP" else signal.SIGKILL
        # re-verify parentage at the instant of the signal
        if any(p[0] == screen for p in mine()):
            os.kill(screen, sig); injected = sig; inj_t = time.time(); say(f"INJECTED {sig.name} -> pid {screen} (mine: parent {hpid})")
    if mode == "NONE" and el >= inj_after and injected is None:
        injected = "stop"; say(f"/record/stop -> {json.dumps(api('/record/stop', {}))}")
    if injected is not None and st["State"] != "recording" and (time.time() - (inj_t or t0)) > 0:
        if st["State"] == "idle": break
    if el > observe: say("observe window elapsed"); break
    time.sleep(1.0 if (inj_t and time.time()-inj_t < 70) else 2.0)
# follow the manifest, not /status (#159)
d = st.get("Dir") or st.get("LastStopDir")
if not d:
    d = sorted(glob.glob(os.path.join(sys.argv[7].rsplit('/',1)[0], 'root/Videos/AgentEyes/*')))[-1:]; d = d[0] if d else None
say(f"recording dir {d}")
for i in range(120):
    try:
        m = json.load(open(os.path.join(d, "manifest.json")))
        mux = (m.get("PostProcessing") or {}).get("mux", {})
        if mux.get("State") in ("done", "failed"): say(f"manifest mux State={mux.get('State')} after {i*2}s: {json.dumps(mux)}"); break
    except Exception as e: pass
    time.sleep(2)
say("final /status " + json.dumps(brief(api("/status"))))
say(f"ffmpegs still parented by {hpid}: {[(p[0],p[1]) for p in mine()]}")
