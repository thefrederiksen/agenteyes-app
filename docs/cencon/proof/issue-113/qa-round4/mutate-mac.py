#!/usr/bin/env python3
"""Independent re-run of issue #113's 14-mutant set against the MAC suite.

Round one A-F and round two W-G are the definitions from the repo's own
docs/cencon/proof/issue-113/mutate.py. B2 and N are defined here from the
rebase report's description, because the script that produced them was not committed.
"""
import pathlib, re, subprocess, sys

repo = pathlib.Path(__file__).resolve().parents[4]   # the repo root
dog  = repo / "src/AgentEyes.Core/Video/CaptureWatchdog.cs"
args = repo / "src/AgentEyes.Core/Video/FfmpegArgs.cs"
macargs = repo / "src/AgentEyes.Core/Platform/Mac/FfmpegArgs.Mac.cs"
svc  = repo / "src/AgentEyes.Core/RecordingService.cs"
rec  = repo / "src/AgentEyes.Core/Video/FfmpegRecorder.cs"

MUTANTS = {
 "F-behind-never-recorded": (svc, [
   ("            if (shortBy <= BehindToleranceSeconds) return null;", "            return null;")]),
 "A-watchdog-never-fires": (dog, [
   ("            if (exit != null)\n            {\n                _fault = new",
    "            if (false && exit != null)\n            {\n                _fault = new"),
   ("                if (_watched >= StartupTimeout)\n                    _fault =",
    "                if (false)\n                    _fault ="),
   ("            else if (_watched - _lastGrowthWatched.Value >= StallTimeout)",
    "            else if (false)")]),
 "B-no-fragmented-output": (args, [
   ('''            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "2000000",
            "-flush_packets", "1",
        };''', "        };")]),
 "C-gaps-counted-as-stalls": (dog, [
   ("            if (sinceLast > GapThreshold)", "            if (false)")]),
 "D-no-startup-window": (dog, [
   ("                if (_watched >= StartupTimeout)", "                if (_watched >= TimeSpan.Zero)")]),
 "E-stall-ignores-timeout": (dog, [
   ("            else if (_watched - _lastGrowthWatched.Value >= StallTimeout)",
    "            else if (_watched - _lastGrowthWatched.Value >= StallTimeout + StallTimeout)")]),
 "W-startvideo-never-starts-the-watchdog": (svc, [
   ("                StartWatchdog(_video!);\n", "")]),
 "P-verdict-published-only-after-the-stop": (svc, [
   ("""            _lastCaptureFault = fault;
            _lastCaptureFaultDir = _dir;
            _lastStopFailure = new RecordingStopReport(
                _dir!,
                new[] { new RecordingStopFailure(VerdictStage, new CaptureFaultException(fault)) },
                manifestSaved: false, recoveryManifestSaved: false);
""", "")]),
 "K-abandon-drains-and-quits": (rec, [
   ("            if (_stopped)\n            {\n                Log.Info($\"[FfmpegRecorder] Abandon:",
    "            Stop(); if (_stopped) return;\n            if (_stopped)\n            {\n                Log.Info($\"[FfmpegRecorder] Abandon:")]),
 "O-watchdog-down-after-the-writers": (svc, [
   ("                StopWatchdog();\n                fault = _pendingFault;", "                fault = _pendingFault;"),
   ("                DisposePreviewTaps();\n\n                RecordingClaimTicket claim;",
    "                lock (_lock) StopWatchdog();\n                DisposePreviewTaps();\n\n                RecordingClaimTicket claim;")]),
 "V-verdict-not-in-the-stop-report": (svc, [
   ("                    fault == null\n                        ? null\n", "                    true\n                        ? null\n")]),
 "G-stop-after-the-verdict-takes-16s": (rec, [
   ("AbandonExitWait = TimeSpan.FromSeconds(3);", "AbandonExitWait = TimeSpan.FromSeconds(16);")]),
 # ---- this rebase's two, defined here ----
 "B2-mac-capture-drops-the-fragmented-output": (macargs, [
   ("            a.AddRange(FragmentedOutput());   // issue #113: a playable file after an unclean end\n", "")]),
 "N-faulted-capture-not-killed-first": (svc, [
   ("            if (faulted && video != null) steps.Add(video);\n", ""),
   ("            if (!faulted && video != null) steps.Add(video);", "            if (video != null) steps.Add(video);")]),
}

def run_suite():
    r = subprocess.run(["dotnet","test","tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj","-c","Release",
                        "--blame-hang-timeout","120s"],
                       cwd=repo, capture_output=True, text=True)
    out = r.stdout + r.stderr
    summary = [l.strip() for l in out.splitlines() if "Passed!" in l or "Failed!" in l or "error CS" in l]
    failed = sorted(set(m.group(1) for m in re.finditer(r"^\s+Failed (.+?) \[[<\d]", out, re.M)))
    return summary, failed

wanted = sys.argv[1:]
# verify every anchor first, so a silently-missing anchor cannot look like a surviving mutant
bad = False
for name,(path,edits) in MUTANTS.items():
    t = path.read_text()
    for old,_ in edits:
        if t.count(old) != 1:
            print(f"[ANCHOR-FAIL] {name}: {t.count(old)} matches in {path.name} for {old[:70]!r}")
            bad = True
if bad: sys.exit("anchors bad - aborting")
print("[OK] all 14 mutants' anchors found exactly once\n", flush=True)

for name,(path,edits) in MUTANTS.items():
    if wanted and not any(name.startswith(w) for w in wanted): continue
    backup = path.read_text(); text = backup
    for old,new in edits: text = text.replace(old,new,1)
    path.write_text(text)
    try: summary, failed = run_suite()
    finally: path.write_text(backup)
    print(f"=== {name}", flush=True)
    for s in summary: print("   ", s)
    for f in failed: print("    FAILED", f)
    if not failed: print("    (NO TEST FAILED - THIS MUTANT SURVIVES)")
    print(flush=True)

summary, failed = run_suite()
print("=== restored:", *summary, *(f"FAILED {f}" for f in failed))
