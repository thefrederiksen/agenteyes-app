"""Issue #113 mutation run: disable the guard in several ways and list which tests fail by name.

Round one: mutants A-F (the watchdog itself, the fragmented output, the behind check).
Round two adds W-V: the SERVICE's wiring of the watchdog and the abandon, which round one had no test
for (QA deleted the StartWatchdog call and the suite stayed 294 / 0 green).
Usage: python3 mutate.py [MUTANT-PREFIX ...]   (no argument = every mutant)"""
import pathlib, re, shutil, subprocess, sys

repo = pathlib.Path("/home/soren/ReposFred/agenteyes-app-113")
dog = repo / "src/AgentEyes.Core/Video/CaptureWatchdog.cs"
args = repo / "src/AgentEyes.Core/Video/FfmpegArgs.cs"
svc = repo / "src/AgentEyes.Core/RecordingService.cs"
rec = repo / "src/AgentEyes.Core/Video/FfmpegRecorder.cs"

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
    # ---- round two: the wiring and the abandon -------------------------------------------------
    "W-startvideo-never-starts-the-watchdog": (svc, [
        ("                StartWatchdog(_video!);\n", ""),
    ]),
    "P-verdict-published-only-after-the-stop": (svc, [
        # Round one's shape: the verdict is held for the stop and reaches /status from SaveManifest.
        ("""            _lastCaptureFault = fault;
            _lastCaptureFaultDir = _dir;
            _lastStopFailure = new RecordingStopReport(
                _dir!,
                new[] { new RecordingStopFailure(VerdictStage, new CaptureFaultException(fault)) },
                manifestSaved: false, recoveryManifestSaved: false);
""", ""),
    ]),
    "K-abandon-drains-and-quits": (rec, [
        # Round one's Abandon: the drain-and-quit Stop on a process already judged dead.
        ("            if (_stopped)\n            {\n                Log.Info($\"[FfmpegRecorder] Abandon:",
         "            Stop(); if (_stopped) return;\n            if (_stopped)\n            {\n                Log.Info($\"[FfmpegRecorder] Abandon:"),
    ]),
    "O-watchdog-down-after-the-writers": (svc, [
        ("                StopWatchdog();\n                fault = _pendingFault;", "                fault = _pendingFault;"),
        ("                DisposePreviewTaps();\n\n                RecordingClaimTicket claim;",
         "                lock (_lock) StopWatchdog();\n                DisposePreviewTaps();\n\n                RecordingClaimTicket claim;"),
    ]),
    "V-verdict-not-in-the-stop-report": (svc, [
        ("                    fault == null\n                        ? null\n", "                    true\n                        ? null\n"),
    ]),
    "G-stop-after-the-verdict-takes-16s": (rec, [
        # The cost of round one's stop (5 s drain + 8 s quit wait + 3 s after the kill), budgeted.
        ("AbandonExitWait = TimeSpan.FromSeconds(3);", "AbandonExitWait = TimeSpan.FromSeconds(16);"),
    ]),
}

def run_suite():
    r = subprocess.run(["dotnet", "test", "tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj", "-c", "Release",
                        "--blame-hang-timeout", "60s"],
                       cwd=repo, capture_output=True, text=True)
    out = r.stdout + r.stderr
    summary = [l.strip() for l in out.splitlines() if "Passed!" in l or "Failed!" in l or "error CS" in l]
    failed = sorted(set(m.group(1) for m in re.finditer(r"^\s+Failed (.+?) \[[<\d]", out, re.M)))
    return summary, failed

wanted = sys.argv[1:]
for name, (path, edits) in MUTANTS.items():
    if wanted and not any(name.startswith(w) for w in wanted): continue
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
