"""Apply / revert one named mutation to the worktree, so a test can be shown to FIRE."""
import io, os, sys

# The repo root, four levels up from docs/cencon/proof/issue-35/round2/probes/.
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "..", "..")) + os.sep

MUTATIONS = {
    # DEFECT 1 - put back the missing disposed-state guard in Select.
    "d1": ("src/AgentEyes.App/CameraPreviewController.cs",
           """            if (IsDisposed)
            {
                Log.Error($"[CameraPreviewController] Select REFUSED: this controller is disposed (the preset editor " +
                          $"has closed), so it will not open \\"{wanted ?? "(none)"}\\". A camera opened now would be " +
                          "held by a window nobody can see and released by nothing.");
                return;
            }
""",
           """            // MUTATION d1: the reviewed head's behaviour - Select accepts calls after disposal.
"""),

    # DEFECT 1b - the second half of the guard, inside the publishing lock.
    "d1b": ("src/AgentEyes.App/CameraPreviewController.cs",
            """                if (Volatile.Read(ref _disposed) != 0)
                {
                    Log.Error("[CameraPreviewController] Select REFUSED: the controller was disposed while the " +
                              "selection was being applied - no camera is opened.");
                    return;
                }

""",
            """                // MUTATION d1b: no disposal check inside the publishing lock.
"""),

    # DEFECT 2 - put back "unregister first, stop afterwards".
    "d2": ("src/AgentEyes.App/CameraPreviewController.cs",
           """            var release = StopSession("the preset editor closed");

            if (!release.DeviceConfirmedFree)""",
           """            // MUTATION d2: the reviewed head's order - unregister BEFORE the camera is free.
            UnregisterHolder("the preset editor closed");
            var release = StopSession("the preset editor closed");

            if (!release.DeviceConfirmedFree)"""),

    # DEFECT 3 - put back "a timeout on the in-flight open returns as a release".
    "d3": ("src/AgentEyes.Core/Video/CameraReleaseRecord.cs",
           """                if (_openUnresolved) return CameraReleaseOutcome.Unknown;
""",
           """                // MUTATION d3: the reviewed head's behaviour - an unresolved open is ignored.
"""),

    # DEFECT 4 - put back "Stop announces a release it did not establish, Dispose drops the handle".
    "d4": ("src/AgentEyes.Core/Video/FfmpegCameraPreview.cs",
           """            if (IsAbandoned)
            {
                Log.Error($"[FfmpegCameraPreview] Dispose: the preview ffmpeg for \\"{DeviceName}\\" " +
                          $"(PID {ProcessId?.ToString() ?? "unknown"}) is STILL RUNNING - it still holds the camera. " +
                          "The process handle is KEPT (releasing it would not end the process, only hide it); this " +
                          "session can still be stopped again.");
                return;
            }

""",
           """            // MUTATION d4: the reviewed head's behaviour - dispose the wrapper regardless.
"""),

    # DEFECT 4b - put back the unconditional "camera released" claim / latched stop.
    "d4b": ("src/AgentEyes.Core/Video/FfmpegCameraPreview.cs",
            """        public bool IsAbandoned =>
            Volatile.Read(ref _stopped) != 0 && Volatile.Read(ref _handleReleased) == 0 && !_proc.HasExited;""",
            """        // MUTATION d4b: the reviewed head's behaviour - the stop announces success regardless.
        public bool IsAbandoned => false;"""),

    # THE SESSION RETENTION - the controller discards a surviving session instead of retaining it.
    "retain": ("src/AgentEyes.App/CameraPreviewController.cs",
               """            CameraDeviceArbiter.StrandedPreviews.RetainIfStranded(session, dir: null);""",
               """            session.Dispose();   // MUTATION retain: the reviewed head's behaviour - discard the handle."""),
}


COMBOS = {"d1all": ["d1", "d1b"]}


def one(name, direction):
    rel, good, bad = MUTATIONS[name]
    p = ROOT + rel
    s = io.open(p, "r", encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in s else "\n"
    g = good.replace("\n", nl)
    b = bad.replace("\n", nl)
    frm, to = (g, b) if direction == "apply" else (b, g)
    if s.count(frm) != 1:
        raise SystemExit(f"MUTATION {name} {direction}: anchor found {s.count(frm)} times - refusing")
    io.open(p, "w", encoding="utf-8", newline="").write(s.replace(frm, to))
    print(f"MUTATION {name} {direction}: OK ({rel})")


def main():
    name, direction = sys.argv[1], sys.argv[2]
    for part in COMBOS.get(name, [name]):
        one(part, direction)


main()
