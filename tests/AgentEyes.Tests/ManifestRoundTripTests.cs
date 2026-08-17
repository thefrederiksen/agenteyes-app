using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #155, criteria 1 and 5: a manifest that goes through a load/save cycle comes out with
    /// everything it went in with - including properties this version has never heard of.
    ///
    /// The always-on part runs against the committed fixtures in
    /// <c>tests/AgentEyes.Tests/fixtures/manifests</c> (real shapes, neutral text - see the README
    /// there). The real recordings on a particular machine are scanned by the opt-in test at the
    /// bottom, which is how QA exercises criterion 5 read-only.
    /// </summary>
    public sealed class ManifestRoundTripTests
    {
        private const string FixtureDir = "tests/AgentEyes.Tests/fixtures/manifests";

        /// <summary>Set this to a recordings root to have the last test scan it (read-only).</summary>
        private const string ScanRootVariable = "AGENTEYES_MANIFEST_SCAN_ROOT";

        public static IEnumerable<object[]> Fixtures() =>
            Directory.GetFiles(Path.Combine(RepoSource.Root, FixtureDir), "*.json")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => new object[] { Path.GetFileName(p) });

        [Fact]
        public void TheFixtureSet_IsThere()
        {
            // The theory below is only as good as its data; an empty fixture folder must be a
            // failure, not a quietly passing test run.
            Assert.True(Fixtures().Count() >= 5, "The manifest fixtures are missing - the round-trip theory has nothing to check.");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void EveryFixtureManifest_RoundTripsWithoutLoss(string fixtureName)
        {
            string original = File.ReadAllText(Path.Combine(RepoSource.Root, FixtureDir, fixtureName));

            string written = RoundTrip(original);

            AssertNoLoss(
                JsonDocument.Parse(original).RootElement,
                JsonDocument.Parse(written).RootElement,
                fixtureName);
        }

        [Fact]
        public void AnUnknownProperty_SurvivesAnUpdateThatChangesAKnownField()
        {
            // Criterion 1, stated the way it actually bites: a NEWER AgentEyes wrote fields this
            // build does not know, and this build renames the recording. The unknown fields must
            // still be there afterwards.
            string original = File.ReadAllText(Path.Combine(RepoSource.Root, FixtureDir, "future-unknown-fields.json"));
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "manifest.json"), original);

                ManifestStore.Update(dir, m => m.DisplayName = "renamed by an older build");

                var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement;
                Assert.Equal("renamed by an older build", written.GetProperty("DisplayName").GetString());
                Assert.Equal("keep-30-days", written.GetProperty("RetentionPolicy").GetString());
                Assert.Equal(4, written.GetProperty("SummaryModelVersion").GetInt32());
                Assert.Equal(2, written.GetProperty("Chapters").GetArrayLength());
                Assert.Equal("done", written.GetProperty("Redaction").GetProperty("State").GetString());

                AssertNoLoss(JsonDocument.Parse(original).RootElement, written, "future-unknown-fields.json");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AnUnknownProperty_SurvivesRepeatedCycles()
        {
            string original = File.ReadAllText(Path.Combine(RepoSource.Root, FixtureDir, "future-unknown-fields.json"));
            string once = RoundTrip(original);
            string twice = RoundTrip(once);

            Assert.Equal(once, twice);   // stable: a manifest stops changing once it has been written
            AssertNoLoss(JsonDocument.Parse(original).RootElement, JsonDocument.Parse(twice).RootElement, "twice");
        }

        [Fact]
        public void TextThatNeedsEscaping_RoundTripsExactly()
        {
            string dir = NewTempDir();
            try
            {
                string awkward = "quotes \" and \\ backslashes \\\\ and a newline\nand a tab\t.";
                ManifestStore.Replace(dir, new Manifest
                {
                    Mode = "video",
                    Label = "video",
                    DisplayName = awkward,
                    FfmpegCommand = "\"C:\\Program Files\\AgentEyes\\ffmpeg.exe\" -y -i desktop",
                });

                var loaded = Manifest.Load(dir);
                Assert.Equal(awkward, loaded.DisplayName);
                Assert.Equal("\"C:\\Program Files\\AgentEyes\\ffmpeg.exe\" -y -i desktop", loaded.FfmpegCommand);
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>
        /// Criterion 5. Opt-in because it reads a particular machine's recordings, which no other
        /// machine has: set AGENTEYES_MANIFEST_SCAN_ROOT to the recordings root (normally
        /// %USERPROFILE%\Videos\AgentEyes) and run this test. It is READ-ONLY on that root - every
        /// manifest is copied to a temporary directory and round-tripped there; nothing under the
        /// root is ever written. When the variable points somewhere with no recordings the test
        /// FAILS rather than passing on an empty scan.
        /// </summary>
        [Fact]
        public void RealRecordings_WhenAScanIsRequested_RoundTripWithoutLoss()
        {
            string? root = Environment.GetEnvironmentVariable(ScanRootVariable);
            if (string.IsNullOrWhiteSpace(root)) return;   // not requested - the fixtures above are the always-on check

            Assert.True(Directory.Exists(root), $"{ScanRootVariable} points at '{root}', which does not exist.");

            var manifests = Directory.GetDirectories(root)
                .Select(d => Path.Combine(d, "manifest.json"))
                .Where(File.Exists)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            Assert.True(manifests.Count > 0, $"No manifest.json found under '{root}' - nothing was actually checked.");

            foreach (string path in manifests)
            {
                string original = File.ReadAllText(path);
                string written = RoundTrip(original);
                AssertNoLoss(
                    JsonDocument.Parse(original).RootElement,
                    JsonDocument.Parse(written).RootElement,
                    path);
            }
        }

        // ---- helpers ----

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AgentEyes-roundtrip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Load the given manifest text and write it back through the store, in a temporary
        /// directory - so a scan of real recordings never writes to them.</summary>
        private static string RoundTrip(string manifestJson)
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);
                ManifestStore.Replace(dir, Manifest.Load(dir));
                return File.ReadAllText(Path.Combine(dir, "manifest.json"));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>
        /// Every property of <paramref name="original"/> must still be in <paramref name="written"/>
        /// with the same value. The written file may have MORE (a known field that was absent is
        /// written with its default), which is not loss.
        ///
        /// One deliberate exemption: a property whose original value is JSON null may be absent
        /// afterwards, because the serializer omits nulls and an absent property loads as null - the
        /// same value, spelled shorter.
        /// </summary>
        private static void AssertNoLoss(JsonElement original, JsonElement written, string where)
        {
            switch (original.ValueKind)
            {
                case JsonValueKind.Object:
                    Assert.Equal(JsonValueKind.Object, written.ValueKind);
                    foreach (var property in original.EnumerateObject())
                    {
                        bool present = written.TryGetProperty(property.Name, out var writtenValue);
                        if (!present)
                        {
                            Assert.True(
                                property.Value.ValueKind == JsonValueKind.Null,
                                $"{where}: property '{property.Name}' was lost.");
                            continue;
                        }
                        AssertNoLoss(property.Value, writtenValue, $"{where}/{property.Name}");
                    }
                    break;

                case JsonValueKind.Array:
                    Assert.Equal(JsonValueKind.Array, written.ValueKind);
                    Assert.Equal(original.GetArrayLength(), written.GetArrayLength());
                    var originals = original.EnumerateArray().ToList();
                    var writtens = written.EnumerateArray().ToList();
                    for (int i = 0; i < originals.Count; i++)
                        AssertNoLoss(originals[i], writtens[i], $"{where}[{i}]");
                    break;

                case JsonValueKind.Number:
                    Assert.Equal(JsonValueKind.Number, written.ValueKind);
                    Assert.Equal(original.GetDouble(), written.GetDouble());
                    break;

                case JsonValueKind.String:
                    Assert.Equal(JsonValueKind.String, written.ValueKind);
                    Assert.Equal(original.GetString(), written.GetString());
                    break;

                default:
                    Assert.Equal(original.ValueKind, written.ValueKind);
                    break;
            }
        }
    }
}
