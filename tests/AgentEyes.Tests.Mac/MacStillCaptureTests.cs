using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests.Mac
{
    /// <summary>
    /// ISSUE #115: the macOS still goes through the SAME AVFoundation grabber the recording uses,
    /// not through Apple's screencapture tool, and no error path blames a permission that is
    /// granted.
    ///
    /// WHAT SHIPPED AND WHY IT SURVIVED. POST /record/shot always failed on macOS - in every
    /// circumstance, including from the launchd agent that holds the Screen Recording grant - because
    /// it shelled out to /usr/sbin/screencapture, which TCC does not cover under the agent's grant.
    /// Measured: with ffmpeg writing frames of this screen successfully in that same process at that
    /// same moment, screencapture exited 1 with "could not create image from rect", and the endpoint
    /// returned an error naming a permission that was on and working. The old tests all passed, every
    /// one of them, because they only ever asserted the SHAPE of a screencapture command line - which
    /// was correct. Nothing asserted which grabber the still used.
    ///
    /// SO THAT IS WHAT THIS FILE ASSERTS, as a PRESENCE in two independent places:
    ///   * <see cref="AvfStill_FullDisplay_IsOneAvfoundationFrame"/> and its neighbours pin the exact
    ///     argument list the still now emits - it names the avfoundation demuxer and asks for one
    ///     frame;
    ///   * <see cref="TheMacStillPathRunsFfmpegAndNoOtherCaptureTool"/> reads the Platform/Mac SOURCE
    ///     and requires the still's call path to name FfmpegArgs.AvfStill, and requires no Mac source
    ///     file to name another screen-capture tool.
    /// Each arm states all three results: expected -> pass, a screencapture reference or a missing
    /// AvfStill call -> FAIL, and NOTHING SCANNED / NO BUILDER FOUND -> a broken instrument that
    /// FAILS rather than a clean run.
    /// <see cref="TheSourceGuardFires_OnTheCallThatShipped"/> runs the detector against the line that
    /// actually shipped, so the detector is proven to fire rather than assumed to.
    ///
    /// WHAT THIS FILE CANNOT SEE, stated rather than left to be discovered:
    ///   * whether ffmpeg accepts the list or the frame is a real picture of the screen. Only a live
    ///     capture through the Control API proves that, and that proof is the QA run on issue #115 -
    ///     these are pure tests and launch nothing.
    ///   * the LATENCY of a still. It is a property of the running machine, not of an argument list.
    ///   * a still built by code outside src/AgentEyes.Core/Platform/Mac, or assembled at run time
    ///     from a computed string an alias hides. The source scan is defeated by either; the
    ///     argument-list tests are defeated by a second still builder nothing calls.
    /// </summary>
    public sealed class MacStillCaptureTests
    {
        // ---- the argument list ---------------------------------------------

        [Fact]
        public void AvfStill_FullDisplay_IsOneAvfoundationFrame()
        {
            var args = FfmpegArgs.AvfStill(
                new Rectangle(0, 0, 1920, 1080), 0, new Size(1920, 1080), "/r/shots/00m03s.png");

            Assert.Equal(new[]
            {
                "-y",
                "-f", "avfoundation",
                "-capture_cursor", "1",
                "-pixel_format", "nv12",
                "-framerate", "30",
                "-i", "0:none",
                "-frames:v", "1",
                "-update", "1",
                "/r/shots/00m03s.png",
            }, args);
        }

        [Fact]
        public void AvfStill_UsesTheAvfoundationDemuxerAndNotAnotherTool()
        {
            // The one-line statement of the whole fix: the still names the avfoundation demuxer the
            // recording uses, addresses the display by AVFoundation index, and shells out to nothing.
            var still = FfmpegArgs.AvfStill(
                new Rectangle(0, 0, 1920, 1080), 0, new Size(1920, 1080), "/r/shot.png");

            Assert.Equal("avfoundation", still[still.IndexOf("-f") + 1]);
            Assert.Equal("0:none", still[still.IndexOf("-i") + 1]);
            Assert.DoesNotContain(still, a => a.Contains("screencapture", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AvfStill_Region_IsCroppedInsideTheDisplay()
        {
            var args = FfmpegArgs.AvfStill(
                new Rectangle(100, 200, 640, 480), 0, new Size(1920, 1080), "/r/shot.png");

            // The region is a crop FILTER, never an input offset - AVFoundation opens a whole
            // display. Same arithmetic as the recording, so a marker shot frames what the video does.
            Assert.Contains("-vf", args);
            Assert.Equal("crop=640:480:100:200", args[args.IndexOf("-vf") + 1]);
            // ... and the input is still the whole display.
            Assert.Equal("0:none", args[args.IndexOf("-i") + 1]);
        }

        [Fact]
        public void AvfStill_RegionOffTheEdge_IsPaddedBackToTheAskedSize()
        {
            // Hangs 200px off the right edge: cropped to what exists, padded back to 800x600 so the
            // PNG is the size that was asked for - the same clamp-and-pad AvfVideoCapture does.
            var args = FfmpegArgs.AvfStill(
                new Rectangle(1320, 0, 800, 600), 0, new Size(1920, 1080), "/r/shot.png");

            string chain = args[args.IndexOf("-vf") + 1];
            Assert.Equal("crop=600:600:1320:0,pad=800:600:0:0:black", chain);
        }

        [Fact]
        public void AvfStill_FullDisplay_HasNoFilterChainAtAll()
        {
            var args = FfmpegArgs.AvfStill(
                new Rectangle(0, 0, 1920, 1080), 0, new Size(1920, 1080), "/r/shot.png");

            Assert.DoesNotContain("-vf", args);
        }

        [Fact]
        public void AvfStill_OddRegion_IsEvenized()
        {
            var args = FfmpegArgs.AvfStill(
                new Rectangle(0, 0, 641, 481), 0, new Size(1920, 1080), "/r/shot.png");

            Assert.Equal("crop=640:480:0:0", args[args.IndexOf("-vf") + 1]);
        }

        [Fact]
        public void AvfStill_RegionOutsideTheDisplay_IsRefusedAndSaysWhy()
        {
            var ex = Assert.Throws<UsageException>(() => FfmpegArgs.AvfStill(
                new Rectangle(4000, 4000, 640, 480), 0, new Size(1920, 1080), "/r/shot.png"));

            Assert.Contains("does not overlap the display", ex.Message);
        }

        [Fact]
        public void AvfStill_NegativeIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.AvfStill(
                new Rectangle(0, 0, 1920, 1080), -1, new Size(1920, 1080), "/r/shot.png"));
        }

        [Fact]
        public void AvfStill_IsOnTheSweptBuilderSurface()
        {
            // MacForeignFfmpegOptionTests reflects over every static builder on FfmpegArgs that
            // returns a string list and checks each AVFoundation list for another platform's options.
            // The still is only covered by that sweep while it IS such a builder, so that is asserted
            // here rather than assumed - a still moved off this surface would silently leave the
            // sweep. EMPTY (no such method) FAILS.
            var m = typeof(FfmpegArgs).GetMethod(
                "AvfStill", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.NotNull(m);
            Assert.Equal(typeof(List<string>), m!.ReturnType);
        }

        // ---- the wording a person has to act on -----------------------------

        [Fact]
        public void Failure_ExitZeroAndFileWritten_IsNoFailure()
        {
            Assert.Null(Screenshot.Failure(0, "", fileWritten: true));
        }

        [Fact]
        public void Failure_ExitZeroButNoFile_IsAFailure()
        {
            // A capture can report success and write nothing. Trusting the exit code alone would
            // report a capture that never happened.
            string? why = Screenshot.Failure(0, "", fileWritten: false);

            Assert.NotNull(why);
            Assert.Contains("wrote no image file", why);
        }

        [Fact]
        public void Failure_NonZeroExit_KeepsFfmpegsOwnWords()
        {
            string? why = Screenshot.Failure(1, "Invalid argument\n", fileWritten: false);

            Assert.NotNull(why);
            Assert.Contains("exit 1", why);
            Assert.Contains("Invalid argument", why);
        }

        [Fact]
        public void Failure_NonZeroExitButAFileExists_IsStillAFailure()
        {
            // A leftover file from an earlier run must not make a failed capture look successful.
            Assert.NotNull(Screenshot.Failure(1, "boom", fileWritten: true));
        }

        [Fact]
        public void Failure_NonZeroExitWithNoStderr_StillExplainsItself()
        {
            string? why = Screenshot.Failure(2, "   ", fileWritten: false);

            Assert.NotNull(why);
            Assert.Contains("exit 2", why);
            Assert.DoesNotContain("):", why);
        }

        [Theory]
        [InlineData(1, "could not create image from rect", false)]
        [InlineData(0, "", false)]
        [InlineData(1, "", true)]
        [InlineData(255, "Invalid data found when processing input", false)]
        public void Failure_NeverBlamesTheScreenRecordingPermission(int exit, string stderr, bool written)
        {
            // ISSUE #115, ACCEPTANCE CRITERION 6. The endpoint used to answer every failure by
            // sending the user to a settings pane that was already correct. A missing grant does not
            // reach this method at all - it hangs the capture and arrives at Hung - so no outcome
            // here may name the permission.
            string? why = Screenshot.Failure(exit, stderr, written);

            Assert.NotNull(why);
            Assert.DoesNotContain("Screen Recording", why);
            Assert.DoesNotContain("Privacy and Security", why);
            Assert.DoesNotContain("permission", why, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Hung_IsTheOnePathThatNamesThePermission()
        {
            // And it is accurate: a capture by a process without the grant does not fail, it waits
            // for a frame that never comes (measured). The budget is named so the reader knows how
            // long was waited, and the "already on" case is left open rather than asserted away.
            string why = Screenshot.Hung(TimeSpan.FromSeconds(10), "");

            Assert.Contains("no frame within 10s", why);
            Assert.Contains("MISSING Screen Recording permission", why);
            Assert.Contains("Privacy and Security", why);
            Assert.Contains("only accepts that from the person at the keyboard", why);
            Assert.Contains("already on", why);
        }

        [Fact]
        public void Hung_CarriesWhateverFfmpegManagedToSay()
        {
            // The one line a no-grant capture does print, measured from an unentitled shell.
            string why = Screenshot.Hung(
                TimeSpan.FromSeconds(10), "Configuration of video device failed, falling back to default.");

            Assert.Contains("Configuration of video device failed", why);
        }

        [Fact]
        public void Budget_IsBoundedWellInsideAnyHttpClientsPatience()
        {
            // MarkerShot holds the session lock across the capture, so the bound is not decoration.
            Assert.InRange(Screenshot.Budget.TotalSeconds, 5, 15);
        }

        // ---- the guard: which grabber the still actually uses ----------------

        /// <summary>
        /// Capture tools that are NOT this build's screen grabber on macOS. screencapture is the one
        /// that shipped and failed; the others are here so a later "quick fix" through a different
        /// external tool is caught by the same check.
        /// </summary>
        private static readonly string[] ForeignCaptureTools =
        {
            "screencapture",
            "/usr/sbin/screencapture",
            "screenshot-cli",
            "scrot",
            "import",
        };

        [Fact]
        public void TheMacStillPathRunsFfmpegAndNoOtherCaptureTool()
        {
            string dir = Path.Combine(RepoRoot, "src", "AgentEyes.Core", "Platform", "Mac");
            Assert.True(Directory.Exists(dir), "the Platform/Mac source directory is gone: " + dir);

            // RECURSIVE on purpose, and the file count is asserted: a scan that silently looked at
            // nothing is a broken instrument, not a clean run.
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            Assert.True(files.Length > 0, "no Mac source files were scanned - broken instrument.");

            var violations = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComment(lines[i]);
                    foreach (string tool in ForeignCaptureTools)
                    {
                        if (NamesTool(code, tool)) violations.Add($"{Path.GetFileName(file)}:{i + 1} -> {lines[i].Trim()}");
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "the macOS still is back on an external capture tool that TCC does not cover under "
                + "the recorder's Screen Recording grant (issue #115):\n  "
                + string.Join("\n  ", violations));

            // AND THE POSITIVE HALF, which is what makes this a presence check: the still's call path
            // must actually call the AVFoundation builder. An absence of screencapture would also be
            // satisfied by a still that captures nothing at all.
            string screenshot = Path.Combine(dir, "Screenshot.cs");
            Assert.True(File.Exists(screenshot), "Platform/Mac/Screenshot.cs is gone: " + screenshot);
            string body = string.Join("\n", File.ReadAllLines(screenshot).Select(StripComment));
            Assert.Contains("FfmpegArgs.AvfStill(", body);
            Assert.Contains("FfmpegLocator.Ffmpeg()", body);
        }

        [Fact]
        public void TheSourceGuardFires_OnTheCallThatShipped()
        {
            // The detector, run against the KNOWN-BAD source - the two lines exactly as they were
            // before this fix. A check only ever run against the state you hope passes has
            // demonstrated nothing (DEVELOPMENT_METHOD 6c, item 3).
            string asItShipped = "        private const string Tool = \"/usr/sbin/screencapture\";";
            string alsoAsItShipped = "            var r = ToolRunner.Run(Tool, args, TimeSpan.FromSeconds(30));";

            Assert.True(NamesTool(StripComment(asItShipped), "screencapture"));
            Assert.False(NamesTool(StripComment(alsoAsItShipped), "screencapture"));

            // The positive half fires too: the shipped Screenshot.cs did not call AvfStill.
            Assert.DoesNotContain("FfmpegArgs.AvfStill(", asItShipped + "\n" + alsoAsItShipped);

            // And a COMMENT naming screencapture - this fix documents why it was dropped - is not
            // read as the bug, or the guard would be unfixable.
            Assert.False(NamesTool(StripComment("        // screencapture is not covered by the grant"), "screencapture"));
            Assert.False(NamesTool(StripComment("        /// out to Apple's screencapture tool"), "screencapture"));
        }

        /// <summary>
        /// Whether a line of CODE (comments already stripped) names a tool - as a string literal or
        /// as part of a path in one, which is how an external tool is reached from here.
        /// </summary>
        private static bool NamesTool(string code, string tool) =>
            code.Contains("\"" + tool + "\"", StringComparison.Ordinal)
            || code.Contains(tool + "\"", StringComparison.Ordinal);

        /// <summary>Drop a line's comment so a comment NAMING the old tool does not read as the bug.</summary>
        private static string StripComment(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                return "";
            }
            int i = line.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? line : line.Substring(0, i);
        }

        /// <summary>The repo root, stamped in at build time by the .csproj: a scan can then never
        /// silently look at nothing.</summary>
        private static string RepoRoot
        {
            get
            {
                string? root = typeof(MacStillCaptureTests).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "The RepoRoot assembly metadata is missing - add it to AgentEyes.Tests.Mac.csproj.");
                if (!Directory.Exists(Path.Combine(root, "src")))
                    throw new InvalidOperationException($"No src directory under the stamped repo root '{root}'.");
                return root;
            }
        }
    }
}
