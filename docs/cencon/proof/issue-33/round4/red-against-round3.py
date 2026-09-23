"""Issue #33 round 4 - run the NEW tests against ROUND 3'S MECHANISM and show them go red.

QA has now failed this issue three times on ONE defect class: a layout event mistaken for a person's
intent, so a size nobody chose is written to config.json. Each previous fix was a BLOCKLIST - name a
transition that produces a bogus size and suppress it - and each was defeated by a transition nobody
had enumerated yet. Round 4 inverts the polarity: nothing is recorded unless a person resizing the
window is positively identified.

A new design is worth nothing unless the suite can SEE the old defect. Round 3's code cannot simply
be checked out under round 4's tests - its API is gone - so this script does the honest equivalent:
it puts ROUND 3'S DECISION PROCEDURE back, inside round 4's shape, and runs the tests.

    python docs/cencon/proof/issue-33/round4/red-against-round3.py

THE MUTATION (one line, in HudUserResize.Watch):

    _window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);

That is rounds 2 and 3 exactly: every size the window REPORTS, while it is manually sized, is taken
for a size the person chose. It is the shape QA reproduced on the constructor path, where the window
reports 520x400 for the first time from inside its own first layout and that number reaches
config.json as a deliberate choice.

EXPECTED RESULT: Failed, with these two among the failures - they are QA's round-3 reproductions,
turned into tests:

  * HudPreviewSizingOrderTests.ShowPanel_FromTheConstructorBeforeTheWindowIsShown_RemembersNothing
  * HudPreviewSizingOrderTests.AHandsOffRecording_WithARememberedSizeTheWindowCannotTake_ChangesNothing

A PASS here would mean the round-4 tests cannot see round 3's defect either, and the test work is
worthless. The recorded run is in red-against-round3.txt beside this file; the green run of the whole
suite against round 4 is in green-round4.txt.
"""
import io, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
WT = os.path.abspath(os.path.join(HERE, "..", "..", "..", "..", ".."))

TARGET = os.path.join(WT, "src", "AgentEyes.App", "HudUserResize.cs")

ANCHOR = ("            if (new WindowInteropHelper(_window).Handle != IntPtr.Zero) "
          "{ HookTheWindowMessages(); return; }")
ROUND3 = ("            _window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);   "
          "// ROUND 3'S MECHANISM\n") + ANCHOR

FILTER = ("FullyQualifiedName~HudSizeMemoryTests"
          "|FullyQualifiedName~HudPreviewSizingOrderTests"
          "|FullyQualifiedName~HudUserResizeTests")


def main():
    source = io.open(TARGET, encoding="utf-8").read()
    if source.count(ANCHOR) != 1:
        print("MUTATION DID NOT APPLY (%d matches) - this instrument is BROKEN, not the code."
              % source.count(ANCHOR))
        return 2

    io.open(TARGET, "w", encoding="utf-8", newline="\n").write(source.replace(ANCHOR, ROUND3))
    try:
        p = subprocess.run(
            ["dotnet", "test", "AgentEyes.sln", "-c", "Release", "--nologo", "-v", "n",
             "--filter", FILTER],
            cwd=WT, capture_output=True, text=True, timeout=1800)
        out = p.stdout
    finally:
        io.open(TARGET, "w", encoding="utf-8", newline="\n").write(source)

    failures = [l.strip() for l in out.splitlines() if re.search(r"Failed\s+AgentEyes\.", l)]
    passed = len([l for l in out.splitlines() if re.search(r"Passed\s+AgentEyes\.", l)])
    if "error CS" in out:
        summary = next(l.strip() for l in out.splitlines() if "error CS" in l)
    elif not failures and not passed:
        summary = "NO TESTS RAN - this instrument is BROKEN, not the code. " + out.strip()[-300:]
    else:
        summary = "Result: Failed: %d, Passed: %d, Total: %d" % (
            len(failures), passed, len(failures) + passed)

    print("ROUND 3'S MECHANISM, PUT BACK UNDER ROUND 4'S TESTS")
    print("=" * 110)
    for f in sorted(failures):
        print("  " + f)
    print()
    print(summary)
    print()
    print("The two that are QA's round-3 report, verbatim:")
    for want in ("ShowPanel_FromTheConstructorBeforeTheWindowIsShown_RemembersNothing",
                 "AHandsOffRecording_WithARememberedSizeTheWindowCannotTake_ChangesNothing"):
        seen = any(want in f for f in failures)
        print("  %-8s %s" % ("RED" if seen else "MISSING", want))
    return 0 if failures else 1


if __name__ == "__main__":
    sys.exit(main())
