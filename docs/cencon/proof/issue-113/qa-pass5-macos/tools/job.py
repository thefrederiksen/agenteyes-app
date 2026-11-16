#!/usr/bin/env python3 -I
# start|stop a one-shot launchd job running the INSTALLED agenteyes apphost with the QA startup hook.
# Label prefix com.agenteyes.qa113mac.* - never com.agenteyes.recorder.
import plistlib, subprocess, sys, time, os
S = "<scratch>"
APP = os.path.expanduser("~/Library/Application Support/AgentEyes/bin/agenteyes")
uid = os.getuid()
def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    print(f"$ {' '.join(cmd)} -> exit={r.returncode} {r.stdout.strip()[:200]} {r.stderr.strip()[:200]}", flush=True); return r
def state(label):
    r = subprocess.run(["launchctl", "print", f"gui/{uid}/{label}"], capture_output=True, text=True)
    if r.returncode != 0: return "gone", None
    st = next((l.strip() for l in r.stdout.splitlines() if l.strip().startswith("state =")), "?")
    pid = next((l.strip().split("=")[1].strip() for l in r.stdout.splitlines() if l.strip().startswith("pid =")), None)
    return st, pid
cmd, name = sys.argv[1], sys.argv[2]
label = f"com.agenteyes.qa113mac.{name}"
out = f"{S}/runs/{name}"; os.makedirs(out, exist_ok=True)
if cmd == "start":
    variant, port = sys.argv[3], sys.argv[4]   # variant: head|main|nodelay|probe
    env = {"DOTNET_STARTUP_HOOKS": f"{S}/out/hook/QaAlcHook.dll", "PATH": "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"}
    if variant != "probe":
        env["QA_HARNESS"] = f"{S}/out/{variant}/AgentEyes.Tests.dll"; env["QA_ARGS"] = f"{out}/root|{port}"
    pl = {"Label": label, "ProgramArguments": [APP, "--version"], "RunAtLoad": True, "LimitLoadToSessionType": "Aqua",
          "ProcessType": "Interactive", "StandardOutPath": f"{out}/job.out", "StandardErrorPath": f"{out}/job.err", "EnvironmentVariables": env}
    with open(f"{out}/{label}.plist", "wb") as f: plistlib.dump(pl, f)
    print(time.strftime("%H:%M:%S"), "start", label, variant, port)
    run(["launchctl", "bootstrap", f"gui/{uid}", f"{out}/{label}.plist"])
    time.sleep(1.5); print("state", state(label))
elif cmd == "stop":
    os.makedirs(f"{out}/root", exist_ok=True); open(f"{out}/root/harness.stop", "w").close()
    t0 = time.time()
    while time.time() - t0 < float(sys.argv[3] if len(sys.argv) > 3 else 90):
        st, pid = state(label)
        if "not running" in st or st == "gone": break
        time.sleep(1)
    print(f"after {time.time()-t0:.1f}s: {state(label)}")
    run(["launchctl", "bootout", f"gui/{uid}/{label}"])
    print("post-bootout", state(label))
