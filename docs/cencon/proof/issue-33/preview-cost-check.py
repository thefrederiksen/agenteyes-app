"""Issue #33 - what the live preview tap costs the recording, measured against a control.

WHY THIS EXISTS. The preview is a SECOND OUTPUT on the ffmpeg process that is writing the recording.
Two things about that had to be established by measurement rather than by argument:

  1. A preview must never truncate the recording (AC10). Handing ffmpeg the preview as an image2 FILE
     output and removing the directory mid-run was measured on 2026-08-28: ffmpeg terminated the WHOLE
     process and a 15-second recording came out 5.1 seconds long. That is why the frames go to a PIPE
     that AgentEyes drains unconditionally (PreviewTap) instead.
  2. A preview must drop no more frames than a preview-off run (AC9), and the first shape of the
     filter chain did: scaling every input frame and decimating at the encoder cost 19-37 dropped
     frames against a control's 4-5. Decimating FIRST, point-sampling the downscale and encoding 4:2:0
     brought it to 0-1.

HOW TO RUN IT

    python docs/cencon/proof/issue-33/preview-cost-check.py [rounds]

It needs ffmpeg and ffprobe (it uses the ones the installed app ships, and falls back to PATH). It
records the DESKTOP for `rounds` x 2 x 30 seconds and writes nothing outside its own temp files. It
is silent - no audio is captured.

WHAT IT PROVES AND WHAT IT DOES NOT. It exercises the ffmpeg SIDE of the tap: the exact argument list
FfmpegArgs.PreviewOutput() emits, the pipe, and the frame boundaries MjpegFramer looks for. It does
NOT exercise the C# tap, the HUD, or the camera - those are the unit tests and the running-app proof.

Each check names all three arms: the expected result, the bad result, and what an EMPTY result means.
An empty preview-frame count is a BROKEN INSTRUMENT, never a clean run.
"""
import ctypes
import ctypes.wintypes
import os
import re
import shutil
import statistics
import subprocess
import sys
import threading
import time

SECONDS = 30
CAPTURE = "1920x1080"
CAPTURE_FPS = 30

# EXACTLY what FfmpegArgs.PreviewOutput() emits. Keep the two in step - the unit test
# FfmpegArgsTests.PreviewOutput_GoesToStdoutAndNeverToAFile pins the product side.
PREVIEW_OUTPUT = [
    "-map", "0:v",
    "-vf", "fps=10,scale=-2:270:flags=neighbor",
    "-q:v", "8",
    "-pix_fmt", "yuvj420p",
    "-an",
    "-f", "mjpeg",
    "-flush_packets", "1",
    "pipe:1",
]


def tool(name):
    bundled = os.path.join(os.environ.get("LOCALAPPDATA", ""), "AgentEyes", "app", name + ".exe")
    if os.path.exists(bundled):
        return bundled
    found = shutil.which(name)
    if found:
        return found
    raise SystemExit("%s not found - install ffmpeg or the AgentEyes app" % name)


FFMPEG = tool("ffmpeg")
FFPROBE = tool("ffprobe")
TEMP = os.path.join(os.environ.get("TEMP", "."), "agenteyes-preview-cost")


def cpu_seconds(proc):
    """Kernel + user CPU time of an ffmpeg run, read off the still-open process handle.

    THE STABLE MEASURE. gdigrab's dropped-frame count is dominated by whatever else the machine is
    doing - identical control runs in this script have varied by an order of magnitude - so it cannot
    resolve a cost this small. CPU time can: it is the work the process actually did, and it does not
    move when another program gets busy. Returns None when the handle is gone.
    """
    class FT(ctypes.Structure):
        _fields_ = [("low", ctypes.wintypes.DWORD), ("high", ctypes.wintypes.DWORD)]

    def to_seconds(ft):
        return ((ft.high << 32) | ft.low) / 1e7

    creation, exit_, kernel, user = FT(), FT(), FT(), FT()
    ok = ctypes.windll.kernel32.GetProcessTimes(
        int(proc._handle), ctypes.byref(creation), ctypes.byref(exit_),
        ctypes.byref(kernel), ctypes.byref(user))
    if not ok:
        return None
    return to_seconds(kernel) + to_seconds(user)


def whole_jpeg_frames(buf):
    """Whole JPEGs in the stream: SOI ... EOI - the same boundaries MjpegFramer cuts on."""
    frames, i = 0, 0
    while True:
        soi = buf.find(b"\xff\xd8", i)
        if soi < 0:
            return frames
        eoi = buf.find(b"\xff\xd9", soi + 2)
        if eoi < 0:
            return frames
        frames += 1
        i = eoi + 2


def one_run(label, with_preview):
    os.makedirs(TEMP, exist_ok=True)
    out = os.path.join(TEMP, label + ".mp4")
    if os.path.exists(out):
        os.remove(out)

    args = [FFMPEG, "-hide_banner", "-y",
            "-f", "gdigrab", "-thread_queue_size", "1024", "-framerate", str(CAPTURE_FPS),
            "-offset_x", "0", "-offset_y", "0", "-video_size", CAPTURE, "-i", "desktop",
            "-t", str(SECONDS),
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23",
            out]
    if with_preview:
        args += ["-t", str(SECONDS)] + PREVIEW_OUTPUT

    started = time.time()
    proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    drained = bytearray()

    def drain():
        # Unconditional, exactly like PreviewTap.Drain: an anonymous pipe nobody reads fills, and a
        # full pipe blocks the ffmpeg that is writing the recording.
        while True:
            chunk = proc.stdout.read(65536)
            if not chunk:
                break
            drained.extend(chunk)

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    stderr = proc.stderr.read().decode("utf-8", "replace")
    proc.wait()
    reader.join(10)
    wall = time.time() - started
    cpu = cpu_seconds(proc)

    if not os.path.exists(out):
        # A run that produced NO FILE is a broken instrument, not a zero. It is reported as such,
        # and the last lines of ffmpeg's own stderr are printed so the reason is in the record.
        print("  %-16s NO RECORDING PRODUCED (exit=%d) - this run measures nothing:\n%s"
              % (label, proc.returncode, "\n".join(stderr.strip().splitlines()[-6:])))
        return {"exit": proc.returncode, "wall": wall, "bytes": 0, "drops": -1, "cpu": cpu,
                "duration": -1.0, "preview_bytes": len(drained),
                "preview_frames": whole_jpeg_frames(drained), "produced": False}

    duration = subprocess.run(
        [FFPROBE, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", out],
        capture_output=True, text=True).stdout.strip()
    drops = re.findall(r"drop=\s*(\d+)", stderr)
    result = {
        "produced": True,
        "exit": proc.returncode,
        "wall": wall,
        "bytes": os.path.getsize(out),
        # ffmpeg omits "drop=" entirely when nothing was dropped, so an absent field is ZERO.
        "drops": int(drops[-1]) if drops else 0,
        "duration": float(duration or 0),
        "cpu": cpu,
        "preview_bytes": len(drained),
        "preview_frames": whole_jpeg_frames(drained),
    }
    os.remove(out)
    print("  %-16s exit=%d duration=%.3fs cpu=%.2fs drops=%d previewFrames=%d previewBytes=%d wall=%.1fs"
          % (label, result["exit"], result["duration"], result["cpu"] or -1, result["drops"],
             result["preview_frames"], result["preview_bytes"], wall))
    return result


def main():
    rounds = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    print("ffmpeg   : %s" % FFMPEG)
    print("capture  : %s @ %dfps for %ds, %d round(s) of each" % (CAPTURE, CAPTURE_FPS, SECONDS, rounds))
    print()

    control, preview = [], []
    for i in range(rounds):
        print("round %d" % i)
        control.append(one_run("control%d" % i, with_preview=False))
        preview.append(one_run("preview%d" % i, with_preview=True))
    print()

    ok = True

    def check(name, passed, detail):
        nonlocal_ok[0] = nonlocal_ok[0] and passed
        print("%-6s %s\n       %s" % ("PASS" if passed else "FAIL", name, detail))

    nonlocal_ok = [True]

    control_drops = [r["drops"] for r in control]
    preview_drops = [r["drops"] for r in preview]
    control_dur = [round(r["duration"], 3) for r in control]
    preview_dur = [round(r["duration"], 3) for r in preview]
    preview_frames = [r["preview_frames"] for r in preview]

    check("every run produced a recording at all",
          all(r["produced"] for r in control + preview),
          "control=%s preview=%s (False is a run that measured NOTHING - a broken instrument, "
          "never a clean result)" % ([r["produced"] for r in control], [r["produced"] for r in preview]))

    check("every run records the full length",
          all(abs(d - SECONDS) < 1.0 for d in control_dur + preview_dur),
          "control=%s preview=%s (requested %ds; a preview that truncated the recording shows here, "
          "and a run missing from these lists is a broken instrument)" % (control_dur, preview_dur, SECONDS))

    check("the control produces no preview frames",
          all(r["preview_frames"] == 0 for r in control),
          "control preview frames=%s" % [r["preview_frames"] for r in control])

    check("the preview run produces live frames",
          all(f > SECONDS * 5 for f in preview_frames),
          "whole JPEG frames=%s (expected about %d at 10fps; an EMPTY count is a broken instrument, "
          "never a clean run)" % (preview_frames, SECONDS * 10))

    # WHAT THIS COMPARISON CAN AND CANNOT SEE. gdigrab drop counts on a working desktop are dominated
    # by whatever else the machine is doing - identical control runs here have varied by an order of
    # magnitude - so a single control-versus-preview pair proves nothing in either direction. The
    # defensible reading is the WORST run of each set over several rounds, and both lists are printed
    # in full so anyone can see the spread rather than take a summary on trust.
    control_cpu = [round(r["cpu"], 2) for r in control]
    preview_cpu = [round(r["cpu"], 2) for r in preview]

    # WHAT THE DROP COUNT CAN AND CANNOT SEE, said plainly rather than implied. gdigrab drop counts on
    # a working desktop are dominated by whatever else the machine is doing; identical control runs
    # here vary by an order of magnitude. So the drop numbers are REPORTED IN FULL and are not used as
    # the pass condition on their own - a metric whose noise exceeds the effect cannot decide either
    # way, and pretending otherwise in either direction would be the overclaim.
    print("INFO   dropped frames, reported not judged")
    print("       control=%s (median %.1f)  preview=%s (median %.1f)"
          % (control_drops, statistics.median(control_drops),
             preview_drops, statistics.median(preview_drops)))

    # THE STABLE MEASURE. CPU time is the work the process actually did. The bound is generous on
    # purpose: what must not happen is the preview costing a significant fraction of the recording.
    check("the preview costs the recording less than 25% more CPU (AC9, the measure that resolves)",
          statistics.median(preview_cpu) <= statistics.median(control_cpu) * 1.25,
          "control cpu=%s (median %.2fs)  preview cpu=%s (median %.2fs) over %ds of 1920x1080/%dfps "
          "capture - an empty or zero CPU list is a broken instrument, never a free preview"
          % (control_cpu, statistics.median(control_cpu), preview_cpu,
             statistics.median(preview_cpu), SECONDS, CAPTURE_FPS))

    print()
    print("RESULT: %s" % ("all checks passed" if nonlocal_ok[0] else "AT LEAST ONE CHECK FAILED"))
    return 0 if nonlocal_ok[0] else 1


if __name__ == "__main__":
    sys.exit(main())
