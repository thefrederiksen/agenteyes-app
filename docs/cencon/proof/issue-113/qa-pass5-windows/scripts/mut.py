import sys, subprocess, pathlib, re, os, time
repo = pathlib.Path(r"C:\ReposFred\agenteyes-app")
RS = repo/"src/AgentEyes.Core/RecordingService.cs"
CW = repo/"src/AgentEyes.Core/Video/CaptureWatchdog.cs"
MUT = {
 "QA-M1 silent read catch (no CheckFailed, no publish, no re-arm)":
   (RS, "live = watchdog.CheckFailed(readEx);", "return;"),
 "QA-M2 outer catch only logs (no re-arm)":
   (RS, "_watchdogTimer?.Arm(CaptureWatchdog.DefaultCheckInterval);\n                        Log.Warn($\"[RecordingService] OnWatchdogTick: re-armed",
        "Log.Warn($\"[RecordingService] OnWatchdogTick: re-armed"),
 "QA-M3 failed check ignores the exit code":
   (CW, "int? exit = _exitCode();\n            return Decide(size: null, exit: exit, readFailed: true);",
        "int? exit = null;\n            return Decide(size: null, exit: exit, readFailed: true);"),
 "QA-N1 NEUTRAL: CheckFailed log wording only":
   (CW, "counted as no growth\");", "counted as NO GROWTH (qa neutral mutation)\");"),
}
FILTER = "FullyQualifiedName~CaptureWatchdog|FullyQualifiedName~WatchdogFailOpen"
def run(cmd):
    p = subprocess.run(cmd, cwd=repo, capture_output=True, text=True, shell=True)
    return p.returncode, p.stdout + p.stderr
def test(tag):
    rc, out = run("dotnet build tests/AgentEyes.Tests/AgentEyes.Tests.csproj -c Release -nologo -v q")
    if rc != 0: print(tag, "BUILD FAILED"); print(out[-2000:]); sys.exit(2)
    rc, out = run(f'dotnet test tests/AgentEyes.Tests/AgentEyes.Tests.csproj -c Release --no-build -nologo --filter "{FILTER}"')
    failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
    summ = [l.strip() for l in out.splitlines() if re.search(r"(Passed|Failed)!", l)]
    print(f"[{tag}] {summ}"); [print("    FAILED TEST:", f) for f in failed]
for name,(f,old,new) in MUT.items():
    src = f.read_text(encoding="utf-8")
    assert src.count(old) == 1, (name, src.count(old))
    try:
        f.write_text(src.replace(old,new), encoding="utf-8")
        assert new in f.read_text(encoding="utf-8")
        test(name)
    finally:
        f.write_text(src, encoding="utf-8"); os.utime(f, None)
rc,out = run("git status --porcelain src"); print("git status src after restore:", repr(out.strip()))
test("BASELINE after restore")
