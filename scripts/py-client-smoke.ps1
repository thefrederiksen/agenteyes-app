# Python client smoke test (issue #75, S2). Standalone (per assumption A2): the QA Agent runs this
# explicitly; it is NOT wired into run-all.ps1 so the .NET gate stays independent of a Python
# toolchain. It starts the app in tray mode (no window), drives the loopback Control API through
# the local agenteyes_client (clients/python), and asserts AC1-AC5. Prints a single PASS/FAIL line.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\py-client-smoke.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path "$PSScriptRoot\..")

# Both projects set <Platforms>x64</Platforms>, so `dotnet build -c Release` lands in
# bin\x64\Release\. A stale non-x64 output directory on an old checkout holds a month-old binary -
# launching it silently tests code nobody built (issue #9). x64 path ONLY; missing = FAIL, no fallback.
$exe  = "src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
if (-not (Test-Path $exe)) {
  "PY-CLIENT-SMOKE: FAIL (app binary not found - it has not been built)"
  "  expected: $(Join-Path (Get-Location) $exe)"
  "  build it: dotnet build AgentEyes.sln -c Release"
  exit 1
}
$base = "http://127.0.0.1:7882"
$crash = Join-Path $env:TEMP 'AgentEyes-crash.log'
Remove-Item $crash -ErrorAction SilentlyContinue

# Resolve a Python 3 interpreter (py launcher preferred on Windows, else python).
$py = $null
foreach ($cand in @('py', 'python')) {
  $cmd = Get-Command $cand -ErrorAction SilentlyContinue
  if ($cmd) { $py = $cmd.Source; break }
}
if (-not $py) { "PY-CLIENT-SMOKE: FAIL (no Python 3 interpreter on PATH - install Python 3.10+)"; exit 1 }
"using python: $py"

$fail = 0
function Chk($name, $cond, $detail) {
  if ($cond) { "[PASS] $name  $detail" } else { "[FAIL] $name  $detail"; $script:fail = 1 }
}

Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process $exe -ArgumentList '--tray'

# Wait for the API to come up.
$up = $false
for ($i = 0; $i -lt 40; $i++) {
  try { $h = Invoke-RestMethod "$base/health" -TimeoutSec 2; if ($h.ok) { $up = $true; break } } catch { }
  Start-Sleep -Milliseconds 500
}
if (-not $up) { "PY-CLIENT-SMOKE: FAIL (API did not come up)"; exit 1 }

# Ensure there is at least one capture so AC4's capture count is meaningful (full-screen monitor 1).
try { [void](Invoke-RestMethod "$base/capture" -Method Post -ContentType 'application/json' -Body (@{ mode='full'; screen=1 } | ConvertTo-Json)) } catch { }

# A single Python program asserts AC1-AC5 through agenteyes_client and prints "PY-OK ..." / "PY-FAIL ...".
$pyScript = @'
import os, sys, json
sys.path.insert(0, os.path.join("clients", "python"))
from agenteyes_client import AgentEyesClient, AgentEyesApiError

base = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:7882"
agenteyes = AgentEyesClient(base_url=base)
ok = True
def chk(name, cond, detail=""):
    global ok
    print(("PY-PASS " if cond else "PY-FAIL ") + name + "  " + str(detail))
    if not cond: ok = False

# AC1: version() non-empty and equal to GET /version directly.
import urllib.request
raw = urllib.request.urlopen(base + "/version", timeout=10).read().decode("utf-8")
direct = json.loads(raw)["version"]
v = agenteyes.version()
chk("AC1-version", bool(v) and v == direct, "client=%s direct=%s" % (v, direct))

# AC2: recordings(limit=2) -> total int, <=2 items, each with the S1 field set.
page = agenteyes.recordings(limit=2)
fields = ["id","dir","label","title","mode","durationSeconds","createdUtc","shotCount","hasVideo","hasAudio","hasTranscript"]
items = page.get("items", [])
fields_ok = (len(items) == 0) or all(f in items[0] for f in fields)
chk("AC2-recordings", isinstance(page.get("total"), int) and len(items) <= 2 and fields_ok,
    "total=%s items=%s" % (page.get("total"), len(items)))

# AC3: recording("<unknown>") -> AgentEyesApiError(.status==404, .code=="not_found"), not None, not raw.
try:
    agenteyes.recording("no-such-recording-xyz")
    chk("AC3-not_found", False, "no error raised")
except AgentEyesApiError as e:
    chk("AC3-not_found", e.status == 404 and e.code == "not_found", "status=%s code=%s" % (e.status, e.code))

# AC4: the example script runs end-to-end and exits 0.
import subprocess
r = subprocess.run([sys.executable, os.path.join("clients","python","examples","list_everything.py"), base],
                   capture_output=True, text=True)
chk("AC4-example", r.returncode == 0 and "version:" in r.stdout, "rc=%s" % r.returncode)
for line in r.stdout.splitlines(): print("  example> " + line)

# AC5: record_start -> is_recording true -> record_stop -> is_recording false.
chk("AC5-pre-idle", not agenteyes.is_recording(), "recording=%s" % agenteyes.is_recording())
agenteyes.record_start(mode="audio", screen=1, source="system")
during = agenteyes.is_recording()
res = agenteyes.record_stop()
after = agenteyes.is_recording()
chk("AC5-roundtrip", during and (not after), "during=%s after=%s file=%s" % (during, after, res.get("File")))

print("PY-OK" if ok else "PY-NOT-OK")
sys.exit(0 if ok else 1)
'@
$pyFile = Join-Path $env:TEMP 'agenteyes-py-client-smoke.py'
Set-Content -Path $pyFile -Value $pyScript -Encoding ASCII

& $py $pyFile $base
$pyExit = $LASTEXITCODE
Chk "py-client" ($pyExit -eq 0) "python assertions exit=$pyExit"

if (Test-Path $crash) { "CRASH LOG PRESENT:"; Get-Content $crash -Raw; $fail = 1 }

Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $pyFile -ErrorAction SilentlyContinue

if ($fail) { "PY-CLIENT-SMOKE: FAIL"; exit 1 } else { "PY-CLIENT-SMOKE: PASS"; exit 0 }
