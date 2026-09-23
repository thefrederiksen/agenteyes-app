"""Issue #33 - run each new check against a KNOWN-BAD implementation and record whether it FIRES.

A check only ever run against the state you hope passes has demonstrated nothing
(DEVELOPMENT_METHOD.md section 6c, item 3). Each entry below breaks ONE specific decision in the way
a plausible careless implementation would break it, runs the tests that are supposed to notice, and
records the result. The mutation is reverted before the next one runs.

    python docs/cencon/proof/issue-33/mutation-evidence.py

Expected result: every mutation FIRED. Bad result: any mutation SILENT - a check that cannot fail is
a defect, not weak coverage. A mutation that DID NOT APPLY, or empty output, means the source has
moved and this instrument is BROKEN - it never means the code is fine.

The recorded run is in mutation-evidence.txt beside this file.
"""
import io, os, subprocess, sys

# The repository root, from this file's own location (docs/cencon/proof/issue-33/).
WT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", ".."))

MUTATIONS = [
    # (label, relative file, old text, new text, test filter, what must go red)
    ("M1 JpegFrame stops checking the JPEG markers (a truncated buffer counts as a whole frame)",
     r"src\AgentEyes.Core\Preview\JpegFrame.cs",
     "            return buffer[0] == Soi1 && buffer[1] == Soi2\n                && buffer[count - 2] == Eoi1 && buffer[count - 1] == Eoi2;",
     "            return true;",
     "FullyQualifiedName~PreviewFramingTests|FullyQualifiedName~PreviewTapTests"),

    ("M2 MjpegFramer keeps the bytes that arrived before the first frame",
     r"src\AgentEyes.Core\Preview\MjpegFramer.cs",
     "            if (soi > 0) Discard(soi);",
     "            // mutation: leading junk is left in the buffer",
     "FullyQualifiedName~PreviewFramingTests"),

    ("M3 MjpegFramer buffers an unterminated frame for ever (no ceiling, no count)",
     r"src\AgentEyes.Core\Preview\MjpegFramer.cs",
     "            if (_length > _maxFrameBytes)",
     "            if (false)",
     "FullyQualifiedName~PreviewFramingTests"),

    ("M4 PreviewTap stops draining the pipe while the preview is hidden (the recording hazard)",
     r"src\AgentEyes.Core\Preview\PreviewTap.cs",
     "                    if (!interpreting) continue;",
     "                    if (!interpreting || !_publishing) continue;",
     "FullyQualifiedName~PreviewTapTests"),

    ("M5 PreviewTap lets a publish failure stop the drain (the AC10 hazard)",
     r"src\AgentEyes.Core\Preview\PreviewTap.cs",
     "                            if (_publishing) Publish(frame);",
     "                            if (_publishing) { File.WriteAllBytes(_tempPath, frame); File.Move(_tempPath, _framePath, true); }",
     "FullyQualifiedName~PreviewTapTests"),

    ("M6 PreviewTap keeps the previous recording's leftover frame",
     r"src\AgentEyes.Core\Preview\PreviewTap.cs",
     "                if (File.Exists(framePath)) File.Delete(framePath);",
     "                // mutation: the previous recording's frame is left in place",
     "FullyQualifiedName~PreviewTapTests"),

    ("M7 PreviewTap leaves the published frame behind when the preview is hidden",
     r"src\AgentEyes.Core\Preview\PreviewTap.cs",
     "                if (!value) RemoveFrameFile();",
     "                // mutation: the last frame is left on disk",
     "FullyQualifiedName~PreviewTapTests"),

    ("M8 PreviewFrameFile opens the frame without sharing (it would break the writer's rename)",
     r"src\AgentEyes.Core\Preview\PreviewFrameFile.cs",
     "                    FileShare.ReadWrite | FileShare.Delete);",
     "                    FileShare.None);",
     "FullyQualifiedName~PreviewTapTests"),

    ("M9 IsStale treats 'no frame has ever arrived' as fine (the fail-open reading)",
     r"src\AgentEyes.App\HudPreviewState.cs",
     "            lastFrameUtc is not { } last || (nowUtc - last).TotalSeconds >= StaleAfterSeconds;",
     "            lastFrameUtc is { } last && (nowUtc - last).TotalSeconds >= StaleAfterSeconds;",
     "FullyQualifiedName~HudPreviewStateTests"),

    ("M10 ManifestCorner is written even when no overlay was being shown",
     r"src\AgentEyes.App\HudPreviewState.cs",
     "            Visible && CameraAvailable && Mode == PreviewMode.Both ? PreviewNames.Text(Corner) : null;",
     "            PreviewNames.Text(Corner);",
     "FullyQualifiedName~HudPreviewStateTests"),

    ("M11 the camera layer is drawn for a recording that has no camera track",
     r"src\AgentEyes.App\HudPreviewState.cs",
     "            Visible && CameraAvailable && Mode is PreviewMode.Camera or PreviewMode.Both;",
     "            Visible && Mode is PreviewMode.Camera or PreviewMode.Both;",
     "FullyQualifiedName~HudPreviewStateTests"),

    ("M12 a camera mode is silently accepted on a recording with no camera",
     r"src\AgentEyes.App\HudPreviewState.cs",
     "            if (!CameraAvailable && mode is PreviewMode.Camera or PreviewMode.Both) return false;",
     "            // mutation: the mode is accepted whatever the recording has",
     "FullyQualifiedName~HudPreviewStateTests"),

    ("M13 the preview is a FILE output on ffmpeg itself - the measured recording-truncating shape",
     r"src\AgentEyes.Core\Video\FfmpegArgs.cs",
     '            "-f", "mjpeg",\n            "-flush_packets", "1",\n            "pipe:1",',
     '            "-f", "image2",\n            "-update", "1",\n            "preview.jpg",',
     "FullyQualifiedName~FfmpegArgsTests"),

    ("M14 the preview output is added to every recording, preview or not",
     r"src\AgentEyes.Core\Video\FfmpegArgs.cs",
     "            if (previewStream) a.AddRange(PreviewOutput());\n            return a;\n        }\n\n        /// <summary>Extract a 16 kHz mono WAV",
     "            a.AddRange(PreviewOutput());\n            return a;\n        }\n\n        /// <summary>Extract a 16 kHz mono WAV",
     "FullyQualifiedName~FfmpegArgsTests"),

    ("M15 the preview filter scales every input frame and decimates afterwards (the measured-slow shape)",
     r"src\AgentEyes.Core\Video\FfmpegArgs.cs",
     'public static string PreviewFilter => $"fps={PreviewFps},scale=-2:{PreviewHeight}:flags=neighbor";',
     'public static string PreviewFilter => $"scale=-2:{PreviewHeight},fps={PreviewFps}";',
     "FullyQualifiedName~FfmpegArgsTests"),

    ("M16 the panel draws layers for a recording that carries no preview feed",
     r"src\AgentEyes.App\HudPreviewState.cs",
     "            Visible && FeedAvailable && Mode is PreviewMode.Screen or PreviewMode.Both;",
     "            Visible && Mode is PreviewMode.Screen or PreviewMode.Both;",
     "FullyQualifiedName~HudPreviewStateTests"),

    ("M17 a recording with no feed stops saying why the panel is empty",
     r"src\AgentEyes.App\HudPreviewState.cs",
     '                if (!FeedAvailable)',
     '                if (false)',
     "FullyQualifiedName~HudPreviewStateTests"),

    ("M18 the manifest writes a default corner instead of leaving the field absent",
     r"src\AgentEyes.Core\Manifest.cs",
     "        public string? PreviewOverlayCorner { get; set; }",
     '        public string? PreviewOverlayCorner { get; set; } = "bottom-right";',
     "FullyQualifiedName~PreviewManifestTests"),

    # ---- round 2 + round 3: AC1 and AC7, the sizing of the preview panel --------------------
    # THREE shapes of one question: which of the sizes a window reports is a size a PERSON chose.
    # Round 1 read it at close time, by which point the HUD had auto-sized back to the pill. Round 2
    # recorded every manually-sized report, so the pill reported from inside the switch to manual
    # sizing became the remembered size - and the panel opened at 367x52 with a zero-sized picture.
    # Round 3 makes the transition explicit, and these mutations break each part of it in turn.

    ("M19 a half-specified saved size is restored (a config with only a width becomes a size)",
     r"src\AgentEyes.App\HudSizeMemory.cs",
     "            if (savedWidth is > 0 && savedHeight is > 0)",
     "            if (savedWidth is > 0 || savedHeight is > 0)",
     "FullyQualifiedName~HudSizeMemoryTests"),

    ("M20 taking the panel down leaves the HUD manually sized, so it never returns to its pill",
     r"src\AgentEyes.App\HudPreviewSizing.cs",
     "            window.SizeToContent = SizeToContent.WidthAndHeight;",
     "            // mutation: the panel comes down but the window stays manually sized",
     "FullyQualifiedName~HudPreviewSizingOrderTests"),

    ("M21 SavePosition goes back to reading the window's live size at close time (round 1's bug)",
     r"src\AgentEyes.App\HudWindow.cs",
     "            if (_size.HasSize)\n            {\n                _cfg.HudWidth = _size.Width;\n                _cfg.HudHeight = _size.Height;\n            }",
     "            if (SizeToContent == SizeToContent.Manual && ActualWidth > 0 && ActualHeight > 0)\n            {\n                _cfg.HudWidth = ActualWidth;\n                _cfg.HudHeight = ActualHeight;\n            }",
     "FullyQualifiedName~HudSizeMemoryTests"),

    # ---- issue #33 round 4: the HUD's size memory is written by GESTURES, not by size reports.
    # M22-M32 were re-aimed when the polarity was inverted (round 4). The decisions they used to
    # break - "is the transition over", "is this report trustworthy" - no longer exist, because
    # nothing observes the window's size any more. What is breakable now is the identification of
    # the gesture and the wiring that carries it.

    ("M22 the window never watches for user resizes (a memory nothing can ever feed)",
     r"src\AgentEyes.App\HudWindow.cs",
     "            _userResize.Watch();",
     "            // mutation: nobody is watching for the person's resize",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M23 a window MOVE is taken for a resize (WM_SIZING is no longer required)",
     r"src\AgentEyes.App\HudUserResize.cs",
     "                    if (!_draggingASizingEdge) return;",
     "                    // mutation: any finished modal loop counts as a resize",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M24 ROUND 3'S SHIPPED MECHANISM, RECONSTRUCTED: the window's own size reports are trusted",
     r"src\AgentEyes.App\HudUserResize.cs",
     "            if (new WindowInteropHelper(_window).Handle != IntPtr.Zero) { HookTheWindowMessages(); return; }",
     "            _window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);   // mutation: rounds 2-3\n"
     "            if (new WindowInteropHelper(_window).Handle != IntPtr.Zero) { HookTheWindowMessages(); return; }",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M25 the peer stops advertising Transform, so UI Automation resizes the HUD behind WPF's back",
     r"src\AgentEyes.App\HudUserResize.cs",
     "            patternInterface == PatternInterface.Transform ? this : base.GetPattern(patternInterface);",
     "            base.GetPattern(patternInterface);",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M26 the panel's state is read at the END of the gesture, so a pill drag becomes a panel size",
     r"src\AgentEyes.App\HudUserResize.cs",
     "                    Record(_thePanelWasUpWhenTheGestureBegan, \"the sizing border\");",
     "                    Record(ThePanelIsUp, \"the sizing border\");",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M27 HudWindow sizes the window by hand again, where no test can drive it",
     r"src\AgentEyes.App\HudWindow.cs",
     "                HudPreviewSizing.ShowPanel(this, _size, DefaultPreviewWidth, DefaultPreviewHeight);",
     "                if (SizeToContent != SizeToContent.Manual)\n                {\n                    SizeToContent = SizeToContent.Manual;\n                    Width = _size.Width ?? DefaultPreviewWidth;\n                    Height = _size.Height ?? DefaultPreviewHeight;\n                }",
     "FullyQualifiedName~HudSizeMemoryTests"),

    ("M28 the panel always opens at the default, ignoring the size the person left it at",
     r"src\AgentEyes.App\HudSizeMemory.cs",
     "            return (_width ?? defaultWidth, _height ?? defaultHeight);",
     "            return (defaultWidth, defaultHeight);",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M29 the HUD stops seeding its memory from the config (QA round-2 blind spot Q1)",
     r"src\AgentEyes.App\HudWindow.cs",
     "            _size = new HudSizeMemory(cfg.HudWidth, cfg.HudHeight);",
     "            _size = new HudSizeMemory(null, null);",
     "FullyQualifiedName~HudSizeMemoryTests"),

    ("M30 the sizing-mode narrowing is dropped, so a gesture on the pill becomes a panel size",
     r"src\AgentEyes.App\HudUserResize.cs",
     "            if (!thePanelWasUpWhenTheGestureBegan) return;",
     "            // mutation: any gesture records, whatever the window is doing",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),

    ("M31 a degenerate 0x0 layout is accepted as a size somebody chose",
     r"src\AgentEyes.App\HudSizeMemory.cs",
     "            if (!(width > 0) || !(height > 0)) return;",
     "            // mutation: a window that has not been laid out counts as a size",
     "FullyQualifiedName~HudSizeMemoryTests"),

    ("M32 the resize grip stops recording, so a drag of it is lost at the next recording",
     r"src\AgentEyes.App\HudUserResize.cs",
     "            Record(panelWasUp, null);",
     "            // mutation: the grip resizes but is never remembered",
     "FullyQualifiedName~HudSizeMemoryTests|FullyQualifiedName~HudPreviewSizingOrderTests|FullyQualifiedName~HudUserResizeTests"),
]


def run(filter_expr):
    p = subprocess.run(
        ["dotnet", "test", "AgentEyes.sln", "-c", "Release", "--nologo", "--filter", filter_expr],
        cwd=WT, capture_output=True, text=True, timeout=900)
    for line in p.stdout.splitlines():
        if line.startswith("Passed!") or line.startswith("Failed!") or "error CS" in line:
            return line.strip()
    return "NO SUMMARY LINE - " + p.stdout.strip()[-300:]


def main():
    # Optional: run a subset by mutation id, e.g. "python mutation-evidence.py M19 M20". No
    # arguments runs every mutation, which is what the recorded evidence file is.
    wanted = set(sys.argv[1:])
    results = []
    for label, rel, old, new, filt in MUTATIONS:
        if wanted and label.split()[0] not in wanted:
            continue
        path = os.path.join(WT, rel)
        src = io.open(path, encoding="utf-8").read()
        if src.count(old) != 1:
            results.append((label, "MUTATION DID NOT APPLY (%d matches) - INVESTIGATE" % src.count(old)))
            print("!! %s: pattern matched %d times" % (label, src.count(old)))
            continue
        io.open(path, "w", encoding="utf-8", newline="\n").write(src.replace(old, new))
        try:
            summary = run(filt)
        finally:
            io.open(path, "w", encoding="utf-8", newline="\n").write(src)
        results.append((label, summary))
        print("%-100s %s" % (label[:100], summary))

    print()
    print("=" * 110)
    for label, summary in results:
        fired = summary.startswith("Failed!")
        print("%-6s %s" % ("FIRED" if fired else "SILENT", label))
        print("       %s" % summary)


if __name__ == "__main__":
    main()
