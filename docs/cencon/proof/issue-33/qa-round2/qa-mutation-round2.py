"""QA round 2 (issue #33, AC7) - INDEPENDENT mutations, written by QA, not by the developer.

The developer's mutation-evidence.py mutates HudSizeMemory itself (M19/M20) and the two IL-asserted
window facts (M21/M22). These four probe the THREE remaining seams the developer's set does not
touch - the ones between the window and the memory:

  Q1  the config seed is dropped        (the memory never learns what the last recording saved)
  Q2  the panel re-opens from the config instead of the memory (the pre-fix read, inside one run)
  Q3  the call site always claims "manually sized" (the pill's 367x52 becomes the remembered size)
  Q4  the call site always claims "auto-sized"    (nothing is ever remembered)

Expected: a SILENT result here is NOT a pass. It marks a decision the unit tests cannot see, which
QA must then close at RUNTIME against the real WPF window. Q1..Q4 are recorded for exactly that
reason; the runtime AC7 reproduction is the instrument that covers them.
"""
import io, os, subprocess

WT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", ".."))
HUD = r"src\AgentEyes.App\HudWindow.cs"
FILT = "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewStateTests"

MUTATIONS = [
    ("Q1 the HUD stops seeding its memory from the config (nothing survives a stop)",
     HUD,
     "_size = new HudSizeMemory(cfg.HudWidth, cfg.HudHeight);",
     "_size = new HudSizeMemory(null, null);",
     FILT),

    ("Q2 the panel re-opens from the config, not the memory (the pre-fix read)",
     HUD,
     "                    Width = _size.Width ?? DefaultPreviewWidth;\n                    Height = _size.Height ?? DefaultPreviewHeight;",
     "                    Width = _cfg.HudWidth is > 0 ? _cfg.HudWidth!.Value : DefaultPreviewWidth;\n                    Height = _cfg.HudHeight is > 0 ? _cfg.HudHeight!.Value : DefaultPreviewHeight;",
     FILT),

    ("Q3 the call site always claims manually-sized (the pill becomes the remembered size)",
     HUD,
     "            SizeChanged += (_, _) => _size.Observe(\n                SizeToContent == SizeToContent.Manual, ActualWidth, ActualHeight);",
     "            SizeChanged += (_, _) => _size.Observe(true, ActualWidth, ActualHeight);",
     FILT),

    ("Q4 the call site always claims auto-sized (nothing is ever remembered)",
     HUD,
     "            SizeChanged += (_, _) => _size.Observe(\n                SizeToContent == SizeToContent.Manual, ActualWidth, ActualHeight);",
     "            SizeChanged += (_, _) => _size.Observe(false, ActualWidth, ActualHeight);",
     FILT),
]


def run(filter_expr):
    p = subprocess.run(
        ["dotnet", "test", "AgentEyes.sln", "-c", "Release", "--nologo", "--filter", filter_expr],
        cwd=WT, capture_output=True, text=True, timeout=900)
    for line in p.stdout.splitlines():
        if line.startswith("Passed!") or line.startswith("Failed!") or "error CS" in line:
            return line.strip()
    return "NO SUMMARY LINE - BROKEN INSTRUMENT - " + p.stdout.strip()[-300:]


def main():
    results = []
    for label, rel, old, new, filt in MUTATIONS:
        path = os.path.join(WT, rel)
        src = io.open(path, encoding="utf-8").read()
        if src.count(old) != 1:
            results.append((label, "MUTATION DID NOT APPLY (%d matches) - INSTRUMENT BROKEN" % src.count(old)))
            print("!! %s: pattern matched %d times" % (label, src.count(old)))
            continue
        io.open(path, "w", encoding="utf-8", newline="\n").write(src.replace(old, new))
        try:
            summary = run(filt)
        finally:
            io.open(path, "w", encoding="utf-8", newline="\n").write(src)
        results.append((label, summary))
        print("%-95s %s" % (label[:95], summary))

    print()
    print("=" * 110)
    for label, summary in results:
        print("%-6s %s" % ("FIRED" if summary.startswith("Failed!") else "SILENT", label))
        print("       %s" % summary)


if __name__ == "__main__":
    main()
