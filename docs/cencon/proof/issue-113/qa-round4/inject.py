#!/usr/bin/env python3
"""Inject a capture fault via the Control API and watch the service's response.

usage: inject.py <STOP|KILL|NONE> <observe_seconds> [--source mic|none] [--label NAME]
Prints a timestamped timeline: /status state, liveness, fault, the capture child's
process state, and the service's CPU.
"""
import json, os, re, signal, subprocess, sys, time, urllib.request

API = "http://127.0.0.1:7882"
MIC = "Yealink UH36"

def api(path, body=None):
    url = API + path
    if body is None:
        req = urllib.request.Request(url)
    else:
        req = urllib.request.Request(url, data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode() or "{}")

def svc_pid():
    out = subprocess.run(["launchctl", "print", f"gui/{os.getuid()}/com.agenteyes.recorder"],
                         capture_output=True, text=True).stdout
    m = re.search(r"^\tpid = (\d+)", out, re.M)
    return int(m.group(1)) if m else None

def pstate(pid):
    if pid is None: return "-"
    r = subprocess.run(["ps", "-o", "state=", "-p", str(pid)], capture_output=True, text=True)
    return r.stdout.strip() or "gone"

def pcpu(pid):
    if pid is None: return "-"
    r = subprocess.run(["ps", "-o", "%cpu=", "-p", str(pid)], capture_output=True, text=True)
    return r.stdout.strip() or "gone"

def ffmpegs():
    """every ffmpeg child of the service, with its output path"""
    r = subprocess.run(["ps", "-eo", "pid=,ppid=,state=,%cpu=,command="], capture_output=True, text=True)
    out = []
    for line in r.stdout.splitlines():
        parts = line.split(None, 4)
        if len(parts) < 5: continue
        pid, ppid, st, cpu, cmd = parts
        if "ffmpeg" not in cmd: continue
        if "grep" in cmd or "/bin/zsh" in cmd: continue
        out.append(dict(pid=int(pid), ppid=int(ppid), state=st, cpu=cpu, cmd=cmd))
    return out

def screen_ffmpeg():
    for p in ffmpegs():
        if "raw.mp4" in p["cmd"]:
            return p
    return None

def mic_ffmpeg():
    for p in ffmpegs():
        if "raw.mp4" not in p["cmd"]:
            return p
    return None

def main():
    mode = sys.argv[1].upper()
    observe = float(sys.argv[2])
    source = "mic"
    label = mode
    if "--source" in sys.argv: source = sys.argv[sys.argv.index("--source")+1]
    if "--label" in sys.argv: label = sys.argv[sys.argv.index("--label")+1]

    sp = svc_pid()
    print(f"# label={label} inject={mode} source={source} observe={observe}s service_pid={sp}")
    print(f"# load at start: {open('/dev/null').close() or subprocess.run(['uptime'],capture_output=True,text=True).stdout.strip()}")
    print(f"# UTC start {time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}")

    st = api("/status")
    if st["State"] != "idle":
        print(f"!! service is not idle (State={st['State']}) - aborting"); sys.exit(2)

    body = {"mode": "video", "screen": 1, "fps": 30, "source": source}
    if source == "mic": body["mic"] = MIC
    r = api("/record/start", body)
    print(f"# /record/start -> {json.dumps(r)}")
    t0 = time.time()

    # wait for healthy: liveness growing
    child = None
    healthy_at = None
    while time.time() - t0 < 90:
        s = api("/status")
        if child is None: child = screen_ffmpeg()
        if s.get("CaptureLiveness") == "growing" and s.get("CaptureBytes"):
            healthy_at = time.time() - t0
            print(f"# healthy at +{healthy_at:.1f}s  liveness=growing bytes={s['CaptureBytes']}")
            break
        time.sleep(1)
    if healthy_at is None:
        print("!! never became healthy"); 
    child = child or screen_ffmpeg()
    micp = mic_ffmpeg()
    print(f"# children: screen={child['pid'] if child else None} mic={micp['pid'] if micp else None}")
    if child: print(f"#   screen cmd: {child['cmd'][:200]}")

    # three polls 10s apart for AC3 while healthy
    print("# --- AC3 healthy polls (10 s apart) ---")
    for i in range(2):
        s = api("/status")
        print(f"AC3POLL t=+{time.time()-t0:7.1f}s {json.dumps({k:s.get(k) for k in ['State','CaptureLiveness','CaptureBytes','CaptureLastGrowthUtc','CaptureSecondsSinceGrowth']})}")
        time.sleep(10)

    inj_t = None
    if mode in ("STOP", "KILL") and child:
        sig = signal.SIGSTOP if mode == "STOP" else signal.SIGKILL
        os.kill(child["pid"], sig)
        inj_t = time.time()
        print(f"# INJECTED {mode} on screen ffmpeg pid {child['pid']} at +{inj_t-t0:.1f}s "
              f"UTC {time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}")

    # observe
    fault_at = None
    notrec_at = None
    idle_at = None
    end = time.time() + observe
    while time.time() < end:
        s = api("/status")
        now = time.time()
        cs = pstate(child["pid"]) if child else "-"
        line = (f"T +{now-t0:7.1f}s "
                f"{'(inj+%6.1fs)'%(now-inj_t) if inj_t else '            '} "
                f"State={s['State']:<11} Live={str(s.get('CaptureLiveness')):<12} "
                f"Fault={str(s.get('LastCaptureFault')):<10} "
                f"Bytes={str(s.get('CaptureBytes')):<10} "
                f"SinceGrowth={str(s.get('CaptureSecondsSinceGrowth')):<7} "
                f"child={cs} svc_cpu={pcpu(sp)}")
        print(line, flush=True)
        if inj_t:
            if fault_at is None and s.get("CaptureLiveness") == "faulted":
                fault_at = now - inj_t
                print(f"### FAULT NAMED on /status at inj+{fault_at:.2f}s  body:")
                print(json.dumps(s, indent=1))
            if notrec_at is None and s["State"] != "recording":
                notrec_at = now - inj_t
                print(f"### STATE LEFT 'recording' at inj+{notrec_at:.2f}s (State={s['State']})")
            if idle_at is None and s["State"] == "idle":
                idle_at = now - inj_t
                print(f"### REACHED IDLE at inj+{idle_at:.2f}s")
                print(json.dumps(s, indent=1))
                break
        time.sleep(2)

    print(f"# SUMMARY label={label} fault_at={fault_at} not_recording_at={notrec_at} idle_at={idle_at}")
    if child:
        print(f"# final child state: pid {child['pid']} -> {pstate(child['pid'])}")
    print(f"# remaining ffmpegs: {json.dumps([{k:p[k] for k in ['pid','state','cpu']} for p in ffmpegs()])}")
    print(f"# final /status: {json.dumps(api('/status'))}")
    print(f"# load at end: {subprocess.run(['uptime'],capture_output=True,text=True).stdout.strip()}")
    print(f"# UTC end {time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}")

main()
