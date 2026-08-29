"""QA round 5 mutation harness for issue #33 (thefrederiksen/agenteyes-app).

For each mutation: apply ONE textual change to ONE product file, rebuild --no-incremental,
run the WHOLE suite, record which tests went red, then restore the file BYTE-EXACTLY and
verify the restore with a sha256 comparison. A mutation that leaves the suite green is
recorded as such, with the reason - never hidden.
"""
import hashlib, os, re, subprocess, sys

ROOT = r"D:\ReposFred\agenteyes-qa33-r5"

M = [
 ("QM1 the drain publishes inline again (gate defect 1 put back verbatim)",
  r"src\AgentEyes.Core\Preview\PreviewTap.cs",
  "                            if (_publishing) Offer(frame);",
  "                            if (_publishing) Publish(frame);"),

 ("QM2 a filesystem call inside the framer - a Core helper the drain reaches transitively",
  r"src\AgentEyes.Core\Preview\MjpegFramer.cs",
  "                OversizeDrops++;",
  "                OversizeDrops++;\n                System.IO.File.AppendAllText(\"qa-mutation-probe.txt\", \"drop\");"),

 ("QM3 the drain calls the WRITE DELEGATE FIELD directly (the call-graph guard's blind spot)",
  r"src\AgentEyes.Core\Preview\PreviewTap.cs",
  "                            if (_publishing) Offer(frame);",
  "                            if (_publishing) { _writeFrame(frame); Offer(frame); }"),

 ("QM4 the snap arm removed - a move loop that ended at a different size is not a resize",
  r"src\AgentEyes.App\HudUserResize.cs",
  "                    if (!_draggingASizingEdge && !snapped) return;",
  "                    if (!_draggingASizingEdge) return;"),

 ("QM5 the window-state route removed - a maximise is invisible again",
  r"src\AgentEyes.App\HudUserResize.cs",
  "            _window.StateChanged += (_, _) => ByWindowState();",
  "            // QA MUTATION: the window-state route removed"),

 ("QM6 a restore FROM minimised is treated as a resize",
  r"src\AgentEyes.App\HudUserResize.cs",
  "            if (now == WindowState.Minimized || was == WindowState.Minimized) return;",
  "            if (now == WindowState.Minimized) return;"),

 ("QM7 the completeness canary never fires",
  r"src\AgentEyes.App\HudSizeMemory.cs",
  "            if (_accountedWidth is not > 0 || _accountedHeight is not > 0) return null;",
  "            if (true) return null;\n            if (_accountedWidth is not > 0 || _accountedHeight is not > 0) return null;"),

 ("QM8 the HUD writes config.json synchronously on the UI thread again",
  r"src\AgentEyes.App\HudWindow.cs",
  "            _cfg.HudPreviewCorner = PreviewNames.Text(_preview.Corner);\n            _cfg.SaveWithoutBlockingTheUiThread();",
  "            _cfg.HudPreviewCorner = PreviewNames.Text(_preview.Corner);\n            _cfg.Save();"),

 ("QM9 the CONSTRUCTOR remembers a choice again (the developer says nothing fires - checked here)",
  r"src\AgentEyes.App\HudWindow.cs",
  "            ApplyPreviewState();\n\n            _timer.Tick += (_, _) => OnTick();",
  "            ApplyAndRememberPreviewChoice();\n\n            _timer.Tick += (_, _) => OnTick();"),

 ("QM10 the background writer writes on the caller's thread",
  r"src\AgentEyes.App\BackgroundFileWriter.cs",
  "            _idle.Reset();\n            if (Interlocked.Exchange(ref _pending, text) != null)\n                Interlocked.Increment(ref _superseded);\n            _work.Set();",
  "            _write(_path, text);\n            Interlocked.Increment(ref _writes);"),

 ("QM11 hiding the preview deletes the frame file on the caller's (WPF UI) thread",
  r"src\AgentEyes.Core\Preview\PreviewTap.cs",
  "                    Interlocked.Exchange(ref _latest, null);\n                    Interlocked.Exchange(ref _removeFrameFile, 1);\n                    _publisherIdle.Reset();\n                    _publisherWork.Set();",
  "                    Interlocked.Exchange(ref _latest, null);\n                    RemoveFrameFile();"),

 ("QM12 the bare apply persists a choice by itself",
  r"src\AgentEyes.App\HudWindow.cs",
  "            _svc.PreviewArmed = _preview.ArmNextRecording;\n        }\n\n        private static void Select(",
  "            _svc.PreviewArmed = _preview.ArmNextRecording;\n            SavePreviewChoices();\n        }\n\n        private static void Select("),
]

def sha(p):
    with open(p, "rb") as f: return hashlib.sha256(f.read()).hexdigest()

def run(cmd):
    return subprocess.run(cmd, cwd=ROOT, shell=True, capture_output=True, text=True)

def suite():
    b = run(r"dotnet build AgentEyes.sln -c Release --no-restore --no-incremental")
    if "Build succeeded" not in b.stdout:
        return None, "BUILD FAILED\n" + b.stdout[-3000:]
    t = run(r"dotnet test AgentEyes.sln -c Release --no-build --no-restore")
    out = t.stdout
    failed = sorted(set(re.findall(r"^\s*(?:Failed|X)\s+([A-Za-z0-9_.]+)", out, re.M)))
    m = re.search(r"Failed:\s*(\d+),\s*Passed:\s*(\d+)", out)
    summary = m.group(0) if m else "(no summary line)"
    return failed, summary

def main():
    print("=== QA round 5, issue #33 - QA's own mutation sweep ===")
    print("Worktree:", ROOT)
    base_failed, base_summary = suite()
    print("BASELINE:", base_summary)
    if base_failed is None or base_failed:
        print("BASELINE IS NOT GREEN - stop.", base_failed); return 1
    for name, rel, old, new in M:
        p = os.path.join(ROOT, rel)
        before = open(p, "r", encoding="utf-8-sig", newline="").read()
        h0 = sha(p)
        n = before.count(old)
        print("\n" + "="*100)
        print(name)
        print("  file:", rel, " anchor occurrences:", n)
        if n != 1:
            print("  RESULT: ANCHOR NOT UNIQUE - mutation not applied, this is a broken instrument")
            continue
        with open(p, "w", encoding="utf-8", newline="") as f: f.write(before.replace(old, new))
        try:
            failed, summary = suite()
            if failed is None:
                print("  RESULT:", summary.splitlines()[0]); print(summary[:1500])
            else:
                print("  " + summary)
                if failed:
                    print("  FIRED (%d):" % len(failed))
                    for t in failed: print("    RED  " + t)
                else:
                    print("  NOTHING FIRED - the suite stayed green with this defect in place")
        finally:
            with open(p, "w", encoding="utf-8", newline="") as f: f.write(before)
            h1 = sha(p)
            print("  restored byte-exactly:", h0 == h1, h0[:16])
    print("\n=== final verification: the tree is back to green ===")
    f2, s2 = suite()
    print(s2, "failed:", f2)
    return 0

sys.exit(main())
