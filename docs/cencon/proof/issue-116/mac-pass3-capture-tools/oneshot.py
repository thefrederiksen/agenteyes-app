#!/usr/bin/env python3 -I
# One-shot launchd job with a HARD timeout: bootstrap, wait for the job to exit or the deadline,
# then always boot it out. Prints every command and its exit code.
import plistlib, subprocess, sys, time, os, json
label, timeout, out = sys.argv[1], float(sys.argv[2]), sys.argv[3]
argv = json.loads(sys.argv[4]); env = json.loads(sys.argv[5]) if len(sys.argv) > 5 else {}
uid = os.getuid(); plist = f"{out}/{label}.plist"
pl = {"Label": label, "ProgramArguments": argv, "RunAtLoad": True, "LimitLoadToSessionType": "Aqua",
      "ProcessType": "Interactive", "StandardOutPath": f"{out}/{label}.out", "StandardErrorPath": f"{out}/{label}.err",
      "EnvironmentVariables": env}
with open(plist, "wb") as f: plistlib.dump(pl, f)
def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    print(f"$ {' '.join(cmd)}\n  exit={r.returncode} {r.stdout.strip()[:300]} {r.stderr.strip()[:300]}"); return r
print(time.strftime("%H:%M:%S"), "job", label, "argv", argv, "env", env)
run(["launchctl", "bootstrap", f"gui/{uid}", plist])
t0 = time.time(); state = "?"
while time.time() - t0 < timeout:
    r = subprocess.run(["launchctl", "print", f"gui/{uid}/{label}"], capture_output=True, text=True)
    state = next((l.strip() for l in r.stdout.splitlines() if l.strip().startswith("state =")), "state = ?")
    lec = next((l.strip() for l in r.stdout.splitlines() if "last exit code" in l), "")
    if "not running" in state and lec: break
    time.sleep(0.5)
el = time.time() - t0
print(f"{time.strftime('%H:%M:%S')} after {el:.1f}s: {state} {lec} {'[TIMEOUT - booting out]' if el >= timeout else ''}")
run(["launchctl", "bootout", f"gui/{uid}/{label}"])
r = subprocess.run(["launchctl", "print", f"gui/{uid}/{label}"], capture_output=True, text=True)
print(f"  post-bootout print exit={r.returncode} (non-zero = the label is gone)")
for ext in ("out", "err"):
    p = f"{out}/{label}.{ext}"
    if os.path.exists(p) and os.path.getsize(p): print(f"  {ext}: " + open(p).read().strip()[:800])
