"""Issue #113 mutation run: disable the guard in several ways and list which tests fail by name."""
import pathlib, re, shutil, subprocess, sys

repo = pathlib.Path("/home/soren/ReposFred/agenteyes-app-113")
dog = repo / "src/AgentEyes.Core/Video/CaptureWatchdog.cs"
args = repo / "src/AgentEyes.Core/Video/FfmpegArgs.cs"
svc = repo / "src/AgentEyes.Core/RecordingService.cs"

MUTANTS = {
    "F-behind-never-recorded": (svc, [
        ("            if (shortBy <= BehindToleranceSeconds) return null;", "            return null;"),
    ]),
    "A-watchdog-never-fires": (dog, [
        ("            if (exit != null)\n            {\n                _fault = new",
         "            if (false && exit != null)\n            {\n                _fault = new"),
        ("                if (_watched >= StartupTimeout)\n                    _fault =",
         "                if (false)\n                    _fault ="),
        ("            else if (_watched - _lastGrowthWatched.Value >= StallTimeout)",
         "            else if (false)"),
    ]),
    "B-no-fragmented-output": (args, [
        ('''            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "2000000",
            "-flush_packets", "1",
        };''', "        };"),
    ]),
    "C-gaps-counted-as-stalls": (dog, [
        ("            if (sinceLast > GapThreshold)", "            if (false)"),
    ]),
    "D-no-startup-window": (dog, [
        ("                if (_watched >= StartupTimeout)", "                if (_watched >= TimeSpan.Zero)"),
    ]),
    "E-stall-ignores-timeout": (dog, [
        ("            else if (_watched - _lastGrowthWatched.Value >= StallTimeout)",
         "            else if (_watched - _lastGrowthWatched.Value >= StallTimeout + StallTimeout)"),
    ]),
}

def run_suite():
    r = subprocess.run(["dotnet", "test", "tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj", "-c", "Release",
                        "--blame-hang-timeout", "60s"],
                       cwd=repo, capture_output=True, text=True)
    out = r.stdout + r.stderr
    summary = [l.strip() for l in out.splitlines() if "Passed!" in l or "Failed!" in l or "error CS" in l]
    failed = sorted(set(m.group(1) for m in re.finditer(r"^\s+Failed (\S+) \[", out, re.M)))
    return summary, failed

for name, (path, edits) in MUTANTS.items():
    backup = path.read_text()
    text = backup
    for old, new in edits:
        if text.count(old) != 1:
            sys.exit(f"[FAIL] mutant {name}: anchor not found exactly once in {path.name}: {old[:60]!r}")
        text = text.replace(old, new, 1)
    path.write_text(text)
    try:
        summary, failed = run_suite()
    finally:
        path.write_text(backup)
    print(f"=== {name}", flush=True)
    for s in summary: print("   ", s)
    for f in failed: print("    FAILED", f)
    if not failed: print("    (NO TEST FAILED - this mutant survives)")

summary, failed = run_suite()
print("=== restored:", *summary, *(f"FAILED {f}" for f in failed))
