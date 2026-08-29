"""Issue #33 round 3 - run the NEW ordering tests against the ROUND-2 CODE THEY WERE WRITTEN FOR.

QA's round-2 report made one finding that matters more than the defect itself: all 1031 tests were
green, all 22 of the developer's own mutations fired, and QA's mutation of the very call site that
carried the shipped bug was SILENT. A suite that cannot fail on the shipped bug is not coverage.

So round 3's new tests (tests/AgentEyes.Tests/HudPreviewSizingOrderTests.cs) drive a REAL WPF window,
and this script proves they would have caught the shipped defect - by putting the round-2 code back
underneath them and showing them go red.

    python docs/cencon/proof/issue-33/round3/red-against-head.py

What it does, and undoes:
  1. Restores src/AgentEyes.App/HudSizeMemory.cs and src/AgentEyes.App/HudWindow.cs from the round-2
     head commit (081598b - the commit QA failed).
  2. Writes src/AgentEyes.App/HudPreviewSizing.cs containing round 2's sizing sequence VERBATIM,
     lifted out of HudWindow.ApplyPreviewState as it stood at that commit. It is the same three
     statements in the same order; only their address changed, so that the tests can reach them.
  3. Moves tests/AgentEyes.Tests/HudSizeMemoryTests.cs aside - it is written against round 3's API
     and cannot compile against round 2's.
  4. Runs the WPF ordering tests.
  5. Puts every file back, whatever happened.

EXPECTED RESULT: Failed. Each failure names an acceptance criterion round 2 broke. A PASS here would
mean the new tests cannot see the defect either, and the round-3 test work is worthless.

The recorded run is in red-against-head.txt beside this file; the green run of the same tests against
round 3 is in green-round3.txt.
"""
import io, os, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
WT = os.path.abspath(os.path.join(HERE, "..", "..", "..", "..", ".."))

ROUND2 = "081598b"   # "QA round 2 on the HUD live preview: FAIL on AC1 and AC7 (#33)"

# The product files round 3 changed, restored to what they were when the defect shipped.
FROM_ROUND2 = [
    r"src\AgentEyes.App\HudSizeMemory.cs",
    r"src\AgentEyes.App\HudWindow.cs",
]

# Round 2's sizing sequence, verbatim. HudWindow.ApplyPreviewState:497-505 and 324-325 at 081598b.
ROUND2_SIZING = '''using System;
using System.Windows;

namespace AgentEyes.App
{
    /// <summary>ROUND 2'S SIZING CODE, VERBATIM, at an address a test can reach. Written by
    /// docs/cencon/proof/issue-33/round3/red-against-head.py; not part of the product.</summary>
    internal static class HudPreviewSizing
    {
        public static void Attach(Window window, HudSizeMemory memory, Func<bool> panelVisible)
        {
            // HudWindow.cs:324-325 at 081598b
            window.SizeChanged += (_, _) => memory.Observe(
                window.SizeToContent == SizeToContent.Manual, window.ActualWidth, window.ActualHeight);
        }

        public static void ShowPanel(Window window, HudSizeMemory memory,
                                     double defaultWidth, double defaultHeight)
        {
            // HudWindow.cs:497-505 at 081598b
            if (window.SizeToContent != SizeToContent.Manual)
            {
                window.SizeToContent = SizeToContent.Manual;
                window.Width = memory.Width ?? defaultWidth;
                window.Height = memory.Height ?? defaultHeight;
            }
        }

        public static void HidePanel(Window window, HudSizeMemory memory)
        {
            // HudWindow.cs:508 at 081598b
            window.SizeToContent = SizeToContent.WidthAndHeight;
        }
    }
}
'''

SIZING_PATH = os.path.join(WT, r"src\AgentEyes.App\HudPreviewSizing.cs")
NEW_API_TESTS = os.path.join(WT, r"tests\AgentEyes.Tests\HudSizeMemoryTests.cs")
FILTER = "FullyQualifiedName~HudPreviewSizingOrderTests"


def main():
    saved = {}
    for rel in FROM_ROUND2 + [r"src\AgentEyes.App\HudPreviewSizing.cs"]:
        path = os.path.join(WT, rel)
        saved[rel] = io.open(path, encoding="utf-8").read() if os.path.exists(path) else None

    try:
        for rel in FROM_ROUND2:
            round2 = subprocess.run(["git", "show", "%s:%s" % (ROUND2, rel.replace("\\", "/"))],
                                    cwd=WT, capture_output=True, text=True, check=True).stdout
            io.open(os.path.join(WT, rel), "w", encoding="utf-8", newline="\n").write(round2)
        io.open(SIZING_PATH, "w", encoding="utf-8", newline="\n").write(ROUND2_SIZING)
        shutil.move(NEW_API_TESTS, NEW_API_TESTS + ".hidden")

        print("Round-2 code restored. Running: dotnet test --filter %s" % FILTER)
        print("=" * 100)
        p = subprocess.run(["dotnet", "test", "AgentEyes.sln", "-c", "Release", "--nologo",
                            "--filter", FILTER],
                           cwd=WT, capture_output=True, text=True, timeout=900)
        print(p.stdout)
    finally:
        if os.path.exists(NEW_API_TESTS + ".hidden"):
            shutil.move(NEW_API_TESTS + ".hidden", NEW_API_TESTS)
        for rel, text in saved.items():
            path = os.path.join(WT, rel)
            if text is None:
                if os.path.exists(path):
                    os.remove(path)
            else:
                io.open(path, "w", encoding="utf-8", newline="\n").write(text)
        print("=" * 100)
        print("Round-3 code restored.")

    verdict = [l for l in p.stdout.splitlines() if l.startswith(("Passed!", "Failed!"))]
    print("VERDICT: %s" % (verdict[0] if verdict else "NO SUMMARY LINE - the run is broken"))
    if not verdict or verdict[0].startswith("Passed!"):
        print("BAD: the new tests do not see round 2's defect. The round-3 test work proves nothing.")
        sys.exit(1)


if __name__ == "__main__":
    main()
