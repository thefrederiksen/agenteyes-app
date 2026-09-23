"""QA round 3 (issue #33) - QA's OWN mutations, re-aimed at the round-3 code.

Round 2's four QA probes (qa-round2/qa-mutation-round2.py) were ALL SILENT while the shipped
defect was live: Q3 injected the exact defect that shipped and 58 tests stayed green. The whole
claim of round 3 is that the suite can now see those decisions. This script is the check on that
claim, written by QA, not by the developer.

The four probes are the SAME DECISIONS as round 2's Q1-Q4, re-aimed at where the round-3 code now
makes them (the call site moved from HudWindow into HudPreviewSizing.Attach; the panel-open size
now comes from HudSizeMemory.OpenPanel):

  Q1  the config seed is dropped                         (nothing survives a stop)
  Q2  the panel re-opens from the config, not the memory (the pre-fix read, inside one run)
  Q3  the call site always claims "manually sized"       <- THE DEFECT THAT SHIPPED IN ROUND 2
  Q4  the call site always claims "auto-sized"           (nothing is ever remembered)

Plus three round-3-specific probes of the NEW transition machinery, because a guard that cannot
fail is not a guard:

  Q5  the panel-visible gate always says visible
  Q6  the transition is never entered  (OpenPanel does not arm _settling)
  Q7  Observe ignores the transition   (round 2's defect, reconstructed inside the new design)

FIRED = the full suite goes RED. SILENT = the suite cannot see that decision, which is a blind
spot regardless of whether the visible symptom is currently absent. An unapplied pattern is a
BROKEN INSTRUMENT, never a pass.
"""
import io, os, subprocess, sys

WT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", ".."))
HUD = r"src\AgentEyes.App\HudWindow.cs"
SIZING = r"src\AgentEyes.App\HudPreviewSizing.cs"
MEM = r"src\AgentEyes.App\HudSizeMemory.cs"

ATTACH_OLD = """            window.SizeChanged += (_, _) => memory.Observe(
                panelVisible(),
                window.SizeToContent == SizeToContent.Manual,
                window.ActualWidth,
                window.ActualHeight);"""

MUTATIONS = [
    ("Q1 the HUD stops seeding its memory from the config (nothing survives a stop)",
     HUD,
     "_size = new HudSizeMemory(cfg.HudWidth, cfg.HudHeight);",
     "_size = new HudSizeMemory(null, null);"),

    ("Q2 the panel re-opens from the config, not the memory (the pre-fix read)",
     HUD,
     "HudPreviewSizing.ShowPanel(this, _size, DefaultPreviewWidth, DefaultPreviewHeight);",
     "HudPreviewSizing.ShowPanel(this, new HudSizeMemory(_cfg.HudWidth, _cfg.HudHeight), DefaultPreviewWidth, DefaultPreviewHeight);"),

    ("Q3 the call site always claims manually-sized (THE ROUND-2 SHIPPED DEFECT)",
     SIZING,
     ATTACH_OLD,
     """            window.SizeChanged += (_, _) => memory.Observe(
                panelVisible(),
                true,
                window.ActualWidth,
                window.ActualHeight);"""),

    ("Q4 the call site always claims auto-sized (nothing is ever remembered)",
     SIZING,
     ATTACH_OLD,
     """            window.SizeChanged += (_, _) => memory.Observe(
                panelVisible(),
                false,
                window.ActualWidth,
                window.ActualHeight);"""),

    ("Q5 the panel-visible gate always says visible (the pill can become the panel size)",
     SIZING,
     ATTACH_OLD,
     """            window.SizeChanged += (_, _) => memory.Observe(
                true,
                window.SizeToContent == SizeToContent.Manual,
                window.ActualWidth,
                window.ActualHeight);"""),

    ("Q6 the transition is never entered (OpenPanel does not arm the settling state)",
     MEM,
     "            _settling = true;\n            return (_commandedWidth, _commandedHeight);",
     "            _settling = false;\n            return (_commandedWidth, _commandedHeight);"),

    ("Q7 Observe ignores the transition (round 2's defect rebuilt inside the new design)",
     MEM,
     "            if (_settling)\n            {",
     "            if (false)\n            {"),
]


def run():
    p = subprocess.run(
        ["dotnet", "test", "AgentEyes.sln", "-c", "Release", "--nologo"],
        cwd=WT, capture_output=True, text=True, timeout=1800)
    for line in p.stdout.splitlines():
        if line.startswith("Passed!") or line.startswith("Failed!") or "error CS" in line:
            return line.strip()
    return "NO SUMMARY LINE - BROKEN INSTRUMENT - " + p.stdout.strip()[-400:]


def main():
    only = set(sys.argv[1:])
    results = []
    for label, rel, old, new in MUTATIONS:
        if only and label.split()[0] not in only:
            continue
        path = os.path.join(WT, rel)
        src = io.open(path, encoding="utf-8").read()
        if src.count(old) != 1:
            results.append((label, "MUTATION DID NOT APPLY (%d matches) - INSTRUMENT BROKEN" % src.count(old)))
            print("!! %s: pattern matched %d times" % (label, src.count(old)))
            continue
        io.open(path, "w", encoding="utf-8", newline="\n").write(src.replace(old, new))
        try:
            summary = run()
        finally:
            io.open(path, "w", encoding="utf-8", newline="\n").write(src)
        results.append((label, summary))
        print("%-78s %s" % (label[:78], summary))

    print()
    print("=" * 118)
    fired = 0
    for label, summary in results:
        ok = summary.startswith("Failed!")
        fired += 1 if ok else 0
        print("%-6s %s" % ("FIRED" if ok else "SILENT", label))
        print("       %s" % summary)
    print()
    print("%d of %d FIRED" % (fired, len(results)))


if __name__ == "__main__":
    main()
