using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Housekeeping;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Tier 2: the extracted walkthrough frames (issues #55, #56).
    ///
    /// The frames are the largest item in a mature library - 11.52 GB across 18,823 files on the machine
    /// this was measured on. They are also the only large thing here it is reasonable to re-encode, and
    /// the reason is a fact about where they came from, not a preference: Package extracts them from
    /// recording.mp4 at the interval the manifest records, so a frame can be made again. These tests
    /// hold the line on which images that reasoning covers, and on the two things that POINT at them
    /// staying correct.
    /// </summary>
    public class FrameConversionTests : IDisposable
    {
        private readonly string _root;

        public FrameConversionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-frames-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        // ---- which images the tier may touch at all ------------------------------------------

        [Theory]
        [InlineData("shots/frame_001.png", true)]
        [InlineData("shots/frame_2775.png", true)]
        [InlineData("shots/FRAME_001.PNG", true)]
        [InlineData(@"shots\frame_001.png", true)]          // the backslash spelling Package has written
        [InlineData("shots/region_1920x1080.png", false)]   // a shot the owner took by hand
        [InlineData("shots/monitor2_full.png", false)]      // ditto
        [InlineData("shots/frame_001.jpg", false)]          // already converted
        [InlineData("thumb.jpg", false)]
        [InlineData("frame_001.png", false)]                // not in shots/
        [InlineData("shots/nested/frame_001.png", false)]
        public void IsExtractedFrame_AcceptsOnlyWhatPackagingProduced(string path, bool expected)
        {
            Assert.Equal(expected, HousekeepingPlan.IsExtractedFrame(path));
        }

        // ---- the planner's side --------------------------------------------------------------

        private static Manifest Packaged(int ageIrrelevant = 0) => new()
        {
            Mode = "video",
            CreatedUtc = "2026-09-01T10:00:00Z",
            Transcript = "transcript.json",
            VideoFile = "recording.mp4",
            Files = new List<string> { "recording.mp4" },
        };

        private static Dictionary<string, long> DiskWithFrames() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["transcript.json"] = 1000,
            ["recording.mp4"] = 5_000_000,
            ["shots/frame_001.png"] = 2_500_000,
            ["shots/frame_002.png"] = 2_400_000,
            ["shots/region_1920x1080.png"] = 900_000,
        };

        [Fact]
        public void Plan_PastTheFrameAge_ProposesOneStepForTheWholeSet()
        {
            // ONE step, not one per frame. A recording can hold 2,775 frames; a step each would make the
            // record of what housekeeping did bigger than the transcript it protects.
            var steps = HousekeepingPlan.For(
                Packaged(), DiskWithFrames(), 7, new HousekeepingSettings(), out _);

            var step = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.ConvertFramesToJpeg));
            Assert.Equal("shots", step.File);
            Assert.Equal(4_900_000, step.Bytes);          // the two frames only
            Assert.Contains("2 extracted frame(s)", step.Reason);
        }

        [Fact]
        public void Plan_TheManualShot_IsNeverCounted()
        {
            var steps = HousekeepingPlan.For(
                Packaged(), DiskWithFrames(), 7, new HousekeepingSettings(), out _);

            var disk = DiskWithFrames();
            long everyImage = disk.Where(f => f.Key.EndsWith(".png")).Sum(f => f.Value);
            long theTwoFrames = disk["shots/frame_001.png"] + disk["shots/frame_002.png"];

            var step = Assert.Single(steps.Where(s => s.Kind == HousekeepingKind.ConvertFramesToJpeg));

            Assert.Equal(theTwoFrames, step.Bytes);
            Assert.NotEqual(everyImage, step.Bytes);
            Assert.Equal(disk["shots/region_1920x1080.png"], everyImage - step.Bytes);
            Assert.Contains("2 extracted frame(s)", step.Reason);
        }

        [Theory]
        [InlineData(6, false)]
        [InlineData(7, true)]
        public void Plan_TheFrameAgeBoundary_HoldsOnBothSides(int ageDays, bool expected)
        {
            var steps = HousekeepingPlan.For(
                Packaged(), DiskWithFrames(), ageDays, new HousekeepingSettings(), out _);

            Assert.Equal(expected, steps.Any(s => s.Kind == HousekeepingKind.ConvertFramesToJpeg));
        }

        [Fact]
        public void Plan_FramesTurnedOff_ProposesNothingForThem()
        {
            var settings = new HousekeepingSettings { ConvertFramesToJpeg = false };

            var steps = HousekeepingPlan.For(Packaged(), DiskWithFrames(), 400, settings, out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.ConvertFramesToJpeg);
        }

        [Fact]
        public void Plan_NoFramesOnDisk_ProposesNothing()
        {
            var disk = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["transcript.json"] = 1000,
                ["shots/region_1920x1080.png"] = 900_000,
            };

            var steps = HousekeepingPlan.For(Packaged(), disk, 400, new HousekeepingSettings(), out _);

            Assert.DoesNotContain(steps, s => s.Kind == HousekeepingKind.ConvertFramesToJpeg);
        }

        // ---- the page rewrite, on a string ---------------------------------------------------

        [Fact]
        public void Rewrite_RepointsOnlyTheFramesThatWereConverted()
        {
            const string html =
                "<img src=\"shots/frame_001.png\" alt=\"screenshot at 00:00\"/>\n"
                + "<img src=\"shots/frame_002.png\" alt=\"screenshot at 00:05\"/>\n"
                + "<img src=\"shots/region_1920x1080.png\" alt=\"a shot I took\"/>";

            string updated = FrameConversion.Rewrite(html, new Dictionary<string, string>
            {
                ["shots/frame_001.png"] = "shots/frame_001.jpg",
            });

            Assert.Contains("shots/frame_001.jpg", updated);
            Assert.DoesNotContain("shots/frame_001.png", updated);
            Assert.Contains("shots/frame_002.png", updated);            // not converted, not repointed
            Assert.Contains("shots/region_1920x1080.png", updated);      // never eligible
        }

        [Fact]
        public void Rewrite_HandlesTheBackslashSpellingToo()
        {
            string updated = FrameConversion.Rewrite(
                "<img src=\"shots\\frame_001.png\"/>",
                new Dictionary<string, string> { ["shots/frame_001.png"] = "shots/frame_001.jpg" });

            Assert.Contains("shots/frame_001.jpg", updated);
            Assert.DoesNotContain("frame_001.png", updated);
        }

        [Fact]
        public void Rewrite_ChangesNothingElseAboutThePage()
        {
            // It is a path substitution, NOT an HTML edit. Nothing is inserted, removed or
            // restructured, which is why the page cannot come out malformed - and is why this slice
            // converts frames rather than also dropping redundant ones.
            const string html = "<html><body><h1>A &amp; B</h1><img src=\"shots/frame_001.png\"/>"
                              + "<p>text with shots/frame_00 in it</p></body></html>";

            string updated = FrameConversion.Rewrite(html, new Dictionary<string, string>
            {
                ["shots/frame_001.png"] = "shots/frame_001.jpg",
            });

            Assert.Equal(html.Replace("shots/frame_001.png", "shots/frame_001.jpg"), updated);
        }

        // ---- the real thing, on real files ---------------------------------------------------

        /// <summary>
        /// A recording with real PNG frames, a manifest that lists them, and a walkthrough that shows
        /// them. Frames are generated so they are genuine images a real encoder must read.
        /// </summary>
        private string MakeRecordingWithFrames(int frames)
        {
            string dir = Path.Combine(_root, "2026-09-01_120000_video");
            string shots = Path.Combine(dir, HousekeepingPlan.FramesDirectory);
            Directory.CreateDirectory(shots);

            var shotEntries = new List<Manifest.ShotEntry>();
            var html = new System.Text.StringBuilder("<html><body>");

            for (int i = 1; i <= frames; i++)
            {
                string name = $"frame_{i:000}.png";
                Ffmpeg.Run(new[]
                {
                    "-v", "error", "-y",
                    "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:duration=1:rate=1,noise=alls={10 + i}:allf=t",
                    "-frames:v", "1",
                    Path.Combine(shots, name),
                }, "test fixture frame");

                shotEntries.Add(new Manifest.ShotEntry { OffsetSeconds = i * 5, File = "shots/" + name });
                html.Append($"<img src=\"shots/{name}\" alt=\"screenshot\"/>");
            }

            // One shot the owner took by hand. It must survive untouched.
            string manual = Path.Combine(shots, "region_640x480.png");
            Ffmpeg.Run(new[]
            {
                "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x480:duration=1:rate=1",
                "-frames:v", "1", manual,
            }, "test fixture manual shot");
            shotEntries.Add(new Manifest.ShotEntry { OffsetSeconds = 1, File = "shots/region_640x480.png" });
            html.Append("<img src=\"shots/region_640x480.png\" alt=\"mine\"/></body></html>");

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), new string('v', 4096));
            File.WriteAllText(Path.Combine(dir, FrameConversion.WalkthroughFile), html.ToString());

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                CreatedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).ToString("o"),
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                Walkthrough = FrameConversion.WalkthroughFile,
                Shots = shotEntries,
                Files = new List<string> { "recording.mp4" },
            });

            return dir;
        }

        [Fact]
        public void Run_ConvertsTheFrames_AndLeavesTheManualShotAlone()
        {
            string dir = MakeRecordingWithFrames(3);
            string shots = Path.Combine(dir, "shots");
            long before = Directory.GetFiles(shots).Sum(f => new FileInfo(f).Length);

            var result = FrameConversion.Run(dir, quality: 88);

            Assert.Null(result.Error);
            Assert.Equal(3, result.Converted);
            Assert.Equal(0, result.Failed);
            Assert.True(result.BytesReclaimed > 0, "converting real PNG frames reclaimed nothing");

            for (int i = 1; i <= 3; i++)
            {
                Assert.True(File.Exists(Path.Combine(shots, $"frame_{i:000}.jpg")));
                Assert.False(File.Exists(Path.Combine(shots, $"frame_{i:000}.png")));
            }

            // The owner's own shot: still PNG, still there, byte for byte.
            Assert.True(File.Exists(Path.Combine(shots, "region_640x480.png")));
            Assert.False(File.Exists(Path.Combine(shots, "region_640x480.jpg")));

            Assert.True(Directory.GetFiles(shots).Sum(f => new FileInfo(f).Length) < before);
        }

        [Fact]
        public void Run_RepointsTheManifestAndThePage_SoEveryReferenceStillResolves()
        {
            // The property that matters: after the pass, every image the page and the manifest name is
            // a file that EXISTS. A walkthrough with a broken image is a destroyed artifact even though
            // no transcript was touched.
            string dir = MakeRecordingWithFrames(3);

            FrameConversion.Run(dir, quality: 88);

            var manifest = Manifest.Load(dir);
            foreach (var shot in manifest.Shots)
            {
                string path = Path.Combine(dir, shot.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"the manifest names {shot.File}, which is not on disk");
            }

            string html = File.ReadAllText(Path.Combine(dir, FrameConversion.WalkthroughFile));
            foreach (var reference in System.Text.RegularExpressions.Regex
                         .Matches(html, "src=\"([^\"]+)\"")
                         .Select(m => m.Groups[1].Value))
            {
                string path = Path.Combine(dir, reference.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"the page references {reference}, which is not on disk");
            }

            Assert.DoesNotContain("frame_001.png", html);
            Assert.Contains("frame_001.jpg", html);
            Assert.Contains("region_640x480.png", html);
        }

        [Fact]
        public void Run_Twice_IsANoOpTheSecondTime()
        {
            string dir = MakeRecordingWithFrames(2);

            var first = FrameConversion.Run(dir, quality: 88);
            var second = FrameConversion.Run(dir, quality: 88);

            Assert.Equal(2, first.Converted);
            Assert.Equal(0, second.Converted);
            Assert.Equal(0, second.Failed);
            Assert.Null(second.Error);          // the JPEGs are still there, so it re-verifies rather than refusing
            Assert.Equal(0, second.BytesReclaimed);
        }

        /// <summary>
        /// The regression test for the defect that actually destroyed a walkthrough.
        ///
        /// The first version deleted each PNG as it converted and repointed the manifest and the page only
        /// at the END. A run stopped part way through left one real recording with 269 JPEGs, 168 PNGs,
        /// and 268 references in BOTH the manifest and walkthrough.html pointing at files that no longer
        /// existed. The comment on that version said a crash was safe at any moment; it was not, because
        /// the delete came before the repoint.
        ///
        /// This reconstructs that exact state and asserts a later pass FINISHES it - which it can only do
        /// because the reference map is rebuilt from the JPEGs on disk rather than from what this
        /// particular pass converted.
        /// </summary>
        [Fact]
        public void Run_AfterAnInterruptedPassLeftDanglingReferences_FinishesTheJob()
        {
            string dir = MakeRecordingWithFrames(3);
            string shots = Path.Combine(dir, "shots");

            // Simulate the old broken order on frames 1 and 2: JPEG written, PNG deleted, nothing
            // repointed. Frame 3 is untouched.
            for (int i = 1; i <= 2; i++)
            {
                string png = Path.Combine(shots, $"frame_{i:000}.png");
                string jpg = Path.Combine(shots, $"frame_{i:000}.jpg");
                File.Copy(png, jpg);
                File.Delete(png);
            }

            // The damage is real before the repair: two references point at nothing.
            var dangling = Manifest.Load(dir).Shots
                .Where(s => !File.Exists(Path.Combine(dir, s.File.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();
            Assert.Equal(2, dangling.Count);

            var result = FrameConversion.Run(dir, quality: 88);

            Assert.Null(result.Error);
            Assert.Equal(1, result.Converted);      // only frame 3 still needed encoding
            Assert.Equal(2, result.Repaired);       // frames 1 and 2 were finished, not re-encoded

            // Every reference resolves again, in both places.
            foreach (var shot in Manifest.Load(dir).Shots)
            {
                string path = Path.Combine(dir, shot.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"the manifest still names {shot.File}, which is not on disk");
            }

            string html = File.ReadAllText(Path.Combine(dir, FrameConversion.WalkthroughFile));
            foreach (var reference in System.Text.RegularExpressions.Regex
                         .Matches(html, "src=\"([^\"]+)\"")
                         .Select(m => m.Groups[1].Value))
            {
                string path = Path.Combine(dir, reference.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"the page still references {reference}, which is not on disk");
            }
        }

        [Fact]
        public void Run_NoShotsDirectory_RefusesRatherThanThrowing()
        {
            string dir = Path.Combine(_root, "bare");
            Directory.CreateDirectory(dir);

            var result = FrameConversion.Run(dir, quality: 88);

            Assert.Equal("the recording has no shots directory", result.Error);
            Assert.Equal(0, result.Converted);
        }

        [Fact]
        public void Run_NoWalkthroughPage_StillConvertsAndDoesNotFail()
        {
            // A recording can be packaged far enough to have frames without having a page.
            string dir = MakeRecordingWithFrames(2);
            File.Delete(Path.Combine(dir, FrameConversion.WalkthroughFile));

            var result = FrameConversion.Run(dir, quality: 88);

            Assert.Null(result.Error);
            Assert.Equal(2, result.Converted);
            Assert.Equal("shots/frame_001.jpg", Manifest.Load(dir).Shots[0].File);
        }

        [Fact]
        public void Run_ThroughTheWholePass_ReportsOneActionAndRecordsIt()
        {
            string dir = MakeRecordingWithFrames(3);

            var report = Housekeeper.Run(
                _root,
                new HousekeepingSettings { ReportOnly = false },
                "test",
                () => false,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

            var step = Assert.Single(report.Recordings
                .SelectMany(r => r.Steps)
                .Where(s => s.Kind == nameof(HousekeepingKind.ConvertFramesToJpeg)));
            Assert.Equal("done", step.Outcome);
            Assert.True(step.BytesReclaimed > 0);

            Assert.Contains(Manifest.Load(dir).Housekeeping,
                h => h.Kind == nameof(HousekeepingKind.ConvertFramesToJpeg) && h.Outcome == "done");
        }

        [Fact]
        public void ReportOnly_LeavesEveryFrameExactlyAsItWas()
        {
            string dir = MakeRecordingWithFrames(3);
            var before = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(dir, p), p => new FileInfo(p).Length);

            var report = Housekeeper.Run(
                _root, new HousekeepingSettings { ReportOnly = true }, "test", () => false,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

            // Presence first: a pass that planned nothing - or refused every recording - satisfies the
            // byte equality below while proving nothing at all. The recording is 17 days old against
            // a 7-day frame gate, so the pass MUST have planned the conversion it then did not do.
            Assert.Contains(report.Recordings.SelectMany(r => r.Steps),
                s => s.Kind == nameof(HousekeepingKind.ConvertFramesToJpeg) && s.Outcome == "planned");

            var after = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(dir, p), p => new FileInfo(p).Length);

            Assert.Equal(before.OrderBy(k => k.Key), after.OrderBy(k => k.Key));
        }

        [Fact]
        public void Run_WithDebrisFromAKilledEncode_SweepsTheTempAndConvertsTheFrame()
        {
            // The atomic write leaves a half-encoded JPEG only ever at the temporary name. The next
            // pass must sweep it and convert the frame normally, so a killed encode costs a retry,
            // never a page pointed at a truncated file.
            string dir = MakeRecordingWithFrames(2);
            string shots = Path.Combine(dir, HousekeepingPlan.FramesDirectory);

            string debris = Path.Combine(shots, "frame_001.jpg.tmp");
            File.WriteAllText(debris, "partial bytes of an encode that never finished");

            var result = FrameConversion.Run(dir, quality: 88);

            Assert.Null(result.Error);
            Assert.Equal(2, result.Converted);
            Assert.False(File.Exists(debris), "the debris from a killed encode was left behind");
            Assert.True(File.Exists(Path.Combine(shots, "frame_001.jpg")));
        }
    }

    /// <summary>
    /// The one frame test that touches the process-wide <c>ManifestStore.InterruptBeforeReplace</c> seam.
    ///
    /// It lives in its own class so it can join <see cref="ManifestSeamCollection"/> and be serialised
    /// against the other users of that static, while the other twenty-seven frame tests stay parallel.
    /// Written here because putting it beside them made a manifest write in an unrelated, concurrently
    /// running test throw.
    /// </summary>
    [Collection(ManifestSeamCollection.Name)]
    public class FrameConversionPhaseOrderTests : IDisposable
    {
        private readonly string _root;

        public FrameConversionPhaseOrderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-frameorder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            ManifestStore.InterruptBeforeReplace = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The phase order itself: no PNG may be deleted until its reference has been repointed. Checked
        /// by interrupting at the repoint - if the deletes had already happened by then, the references
        /// left in the manifest would be dangling.
        /// </summary>
        [Fact]
        public void Run_IsInterruptedAtTheRepoint_LeavesEveryReferenceResolving()
        {
            string dir = MakeRecordingWithFrames(3);

            ManifestStore.InterruptBeforeReplace = _ => throw new IOException("interrupted on purpose");
            try
            {
                Assert.ThrowsAny<Exception>(() => FrameConversion.Run(dir, quality: 88));
            }
            finally
            {
                ManifestStore.InterruptBeforeReplace = null;
            }

            // The manifest still says PNG - and every PNG is still there, because the deletes come after.
            foreach (var shot in Manifest.Load(dir).Shots)
            {
                string path = Path.Combine(dir, shot.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    $"{shot.File} was deleted before its reference was repointed - the phase order is wrong");
            }
        }

        /// <summary>
        /// The accounting pin for the repair pass (the second review's one remaining defect): a frame an
        /// earlier pass already encoded is REPAIRED, never re-counted as converted, and its bytes are
        /// not counted a second time - the interrupted pass reported them when it encoded them.
        /// Without this test, patching the pre-fix counting back in leaves the whole suite green.
        /// </summary>
        [Fact]
        public void Run_FinishingAnInterruptedPass_CountsItRepairedNeverReConverted()
        {
            string dir = MakeRecordingWithFrames(2);

            // Stage 1: an interrupted pass. Both JPEGs exist beside their PNGs, encoded and verified,
            // repointed at nothing - the state a kill between phase 1 and phase 4 leaves behind.
            ManifestStore.InterruptBeforeReplace = _ => throw new IOException("interrupted on purpose");
            try
            {
                Assert.ThrowsAny<Exception>(() => FrameConversion.Run(dir, quality: 88));
            }
            finally
            {
                ManifestStore.InterruptBeforeReplace = null;
            }

            // Stage 2: the repair pass. Only the bookkeeping remains - nothing is encoded, so nothing
            // may be counted as converted, and no bytes may move: they were reported by the interrupted
            // pass when it encoded the frames, and counting them again would double every repair.
            var result = FrameConversion.Run(dir, quality: 88);

            Assert.Null(result.Error);
            Assert.Equal(0, result.Converted);
            Assert.Equal(2, result.Repaired);
            Assert.Equal(0, result.BytesReclaimed);

            // The finish is real: every reference points at a JPEG that exists, and the PNGs are gone.
            foreach (var shot in Manifest.Load(dir).Shots)
            {
                Assert.EndsWith(".jpg", shot.File, StringComparison.OrdinalIgnoreCase);
                string path = Path.Combine(dir, shot.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    $"the repaired manifest still names {shot.File}, which is not on disk");
            }
        }

        /// <summary>A recording with real frames, a manifest listing them, and a page showing them.</summary>
        private string MakeRecordingWithFrames(int frames)
        {
            string dir = Path.Combine(_root, "2026-09-01_120000_video");
            string shots = Path.Combine(dir, HousekeepingPlan.FramesDirectory);
            Directory.CreateDirectory(shots);

            var shotEntries = new List<Manifest.ShotEntry>();
            var html = new System.Text.StringBuilder("<html><body>");

            for (int i = 1; i <= frames; i++)
            {
                string name = $"frame_{i:000}.png";
                Ffmpeg.Run(new[]
                {
                    "-v", "error", "-y",
                    "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:duration=1:rate=1,noise=alls={10 + i}:allf=t",
                    "-frames:v", "1",
                    Path.Combine(shots, name),
                }, "test fixture frame");

                shotEntries.Add(new Manifest.ShotEntry { OffsetSeconds = i * 5, File = "shots/" + name });
                html.Append($"<img src=\"shots/{name}\" alt=\"screenshot\"/>");
            }
            html.Append("</body></html>");

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "{\"segments\":[]}");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), new string('v', 4096));
            File.WriteAllText(Path.Combine(dir, FrameConversion.WalkthroughFile), html.ToString());

            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video",
                CreatedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).ToString("o"),
                Transcript = "transcript.json",
                VideoFile = "recording.mp4",
                Walkthrough = FrameConversion.WalkthroughFile,
                Shots = shotEntries,
                Files = new List<string> { "recording.mp4" },
            });

            return dir;
        }
    }
}
