"""Issue #33 round 5 - run the NEW tests against ROUND 4's code and show each one go RED.

The Review Gate rejected round 4 with three blocking defects (docs/cencon/review/
pr34-issue33-gate-round1.md). Round 5 fixes them. A fix is worth nothing unless the suite can SEE
the defect it fixed, so this script puts each defect BACK, one at a time, rebuilds, runs the whole
suite, and records which tests fail.

    python docs/cencon/proof/issue-33/round5/red-against-round4.py

Every mutation is reverted before the next one is applied, and the file is restored even if the run
is interrupted. The recorded run is in red-against-round4.txt beside this file; the green run of the
whole suite against unmutated round 5 is in green-round5.txt.

READ THE THREE ARMS. For each mutation:
  * the named tests FAIL          -> the check works
  * the named tests PASS          -> the check is a decoration and the fix is unproven
  * the mutation does not apply   -> a BROKEN INSTRUMENT, reported as such, never a clean run

One mutation (M10) is expected to produce NO failure. It is included deliberately: it is the limit
this round could not close, it is stated here rather than hidden, and the reason is written beside
it.
"""
import io
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
WT = os.path.abspath(os.path.join(HERE, "..", "..", "..", "..", ".."))

TAP = os.path.join(WT, "src", "AgentEyes.Core", "Preview", "PreviewTap.cs")
RESIZE = os.path.join(WT, "src", "AgentEyes.App", "HudUserResize.cs")
MEMORY = os.path.join(WT, "src", "AgentEyes.App", "HudSizeMemory.cs")
HUD = os.path.join(WT, "src", "AgentEyes.App", "HudWindow.cs")
WRITER = os.path.join(WT, "src", "AgentEyes.App", "BackgroundFileWriter.cs")

# (id, what it puts back, file, find, replace, tests that must go red)
MUTATIONS = [
    ("M1", "the drain publishes INLINE again (gate defect 1)", TAP,
     "                            if (_publishing) Offer(frame);",
     "                            if (_publishing) Publish(frame);",
     ["Drain_WhilePublishingIsStalledForever_StillReadsThePipeToTheEnd",
      "NothingTheDrainCanReach_TouchesTheFilesystem"]),

    ("M2", "hiding the preview deletes the frame on the CALLER's thread (gate defect 1)", TAP,
     "                    Interlocked.Exchange(ref _latest, null);\n"
     "                    Interlocked.Exchange(ref _removeFrameFile, 1);\n"
     "                    _publisherIdle.Reset();\n"
     "                    _publisherWork.Set();",
     "                    RemoveFrameFile();",
     ["NothingTurningThePreviewOffCanReach_TouchesTheFilesystem"]),

    ("M3", "the drain logs through the shared (file-appending, globally locked) logger", TAP,
     '                                Note("INFO", $"[PreviewTap] Drain: track={_track} first frame, {frame.Length} bytes");',
     '                                Log.Info($"[PreviewTap] Drain: track={_track} first frame, {frame.Length} bytes");',
     ["NothingTheDrainCanReach_TouchesTheFilesystem"]),

    ("M4", "a Windows snap is not treated as a resize (gate defect 2)", RESIZE,
     "                    if (!_draggingASizingEdge && !snapped) return;",
     "                    if (!_draggingASizingEdge) return;",
     ["SnappingTheWindowToAScreenEdge_IsRemembered",
      "ALoopThatEndedAtADifferentSize_RecordsTheSizeItEndedAt"]),

    ("M5", "a maximise is invisible again (gate defect 2)", RESIZE,
     "            _window.StateChanged += (_, _) => ByWindowState();\n",
     "",
     ["MaximisingTheWindow_IsRemembered",
      "AWindowStateCommand_RecordsTheSizeTheWindowSettlesAt",
      "HudWindow_WiresUpEveryGestureRoute"]),

    ("M6", "the app sets the HUD's own WindowState, so a state change stops proving anything", HUD,
     "            ResizeMode = ResizeMode.CanResize;",
     "            ResizeMode = ResizeMode.CanResize;\n            WindowState = WindowState.Normal;",
     ["NothingInTheHudEverSetsItsOwnWindowState"]),

    ("M7", "the completeness canary never fires", MEMORY,
     "            if (_accountedWidth is not > 0 || _accountedHeight is not > 0) return null;",
     "            if (true) return null;\n"
     "            if (_accountedWidth is not > 0 || _accountedHeight is not > 0) return null;",
     ["AResizeNoGestureClaimed_IsReportedByTheCompletenessCanary"]),

    ("M8", "the HUD writes config.json on the WPF UI thread again (gate defect 3)", HUD,
     "            _cfg.SaveWithoutBlockingTheUiThread();\n        }\n\n        /// <summary>Stop reading frames",
     "            _cfg.Save();\n        }\n\n        /// <summary>Stop reading frames",
     ["NothingTheHudsUiThreadCanReach_WritesAFile",
      "EveryPreviewButton_RemembersTheChoice"]),

    ("M9", "the background writer writes on the caller's thread (gate defect 3)", WRITER,
     "            _idle.Reset();\n"
     "            if (Interlocked.Exchange(ref _pending, text) != null)\n"
     "                Interlocked.Increment(ref _superseded);\n"
     "            _work.Set();",
     "            WriteOnce(text);",
     ["Queue_WhileTheWriteIsStalled_ReturnsAtOnce",
      "Queue_TwiceInARow_WritesTheNewestStateAndCountsTheOneItSuperseded"]),

    ("M10", "the constructor remembers a preview choice again - THE DOCUMENTED LIMIT, no test fires",
     HUD,
     "            ApplyPreviewState();\n\n            _timer.Tick",
     "            ApplyAndRememberPreviewChoice();\n\n            _timer.Tick",
     []),
]


def run(cmd):
    return subprocess.run(cmd, cwd=WT, capture_output=True, text=True, shell=False)


def failures_of(output):
    names = []
    for line in output.splitlines():
        line = line.strip()
        if line.startswith("Failed ") and "[" in line:
            names.append(line[len("Failed "):].split(" [")[0])
    return sorted(set(names))


def main():
    ok = True
    for mid, what, path, find, replace, expected in MUTATIONS:
        # Binary IO throughout: this repository holds a mix of LF and CRLF files, and a
        # mutation harness that rewrites line endings would leave a diff of its own.
        raw = open(path, "rb").read()
        source = raw.decode("utf-8")
        count = source.count(find)
        print("=" * 100)
        print("%s: %s" % (mid, what))
        print("   file: %s" % os.path.relpath(path, WT))
        if count != 1:
            print("   BROKEN INSTRUMENT: the mutation anchor matched %d times, not 1. "
                  "Nothing was measured." % count)
            ok = False
            continue

        open(path, "wb").write(source.replace(find, replace).encode("utf-8"))
        try:
            build = run(["dotnet", "build", "AgentEyes.sln", "-c", "Release", "--no-restore"])
            if "Build succeeded." not in build.stdout:
                print("   BROKEN INSTRUMENT: the mutated tree does not build. Nothing was measured.")
                print(build.stdout[-2000:])
                ok = False
                continue
            test = run(["dotnet", "test", "AgentEyes.sln", "-c", "Release",
                        "--no-build", "--no-restore"])
        finally:
            open(path, "wb").write(raw)

        got = failures_of(test.stdout)
        print("   tests that FAILED under the mutation (%d):" % len(got))
        for name in got:
            print("     - %s" % name)
        if not expected:
            print("   EXPECTED: none. This mutation is the limit this round did not close - every "
                  "HUD button's Click handler is a lambda declared in the constructor, so the IL "
                  "folds it into .ctor and no call-graph guard can tell what the constructor itself "
                  "calls. What IS guarded is that ApplyPreviewState has no path to a save at all "
                  "(ApplyingThePreviewState_NeverRemembersAChoiceByItself) and that every button "
                  "does (EveryPreviewButton_RemembersTheChoice).")
            continue

        missing = [e for e in expected if not any(e in g for g in got)]
        if missing:
            print("   FAIL: these were expected to go red and did not: %s" % ", ".join(missing))
            ok = False
        else:
            print("   OK: every expected check fired.")

    print("=" * 100)
    print("RESULT: %s" % ("every mutation was seen" if ok else "AT LEAST ONE CHECK DID NOT FIRE"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
