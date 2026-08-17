using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Unit tests for the setup engine: manifest parsing, version comparison,
    /// plan decisions, the swap/rollback mechanics, and a full offline
    /// install/update pass against a local release dir. All filesystem work
    /// happens in per-test temp directories.
    /// </summary>
    public sealed class SetupEngineTests : IDisposable
    {
        private readonly string _temp;

        public SetupEngineTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-engine-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        public void Dispose()
        {
            try { Directory.Delete(_temp, recursive: true); } catch { }
        }

        // ---- manifest -------------------------------------------------------

        [Fact]
        public void Manifest_ParsesAssetsAndInheritsReleaseVersion()
        {
            const string json = """
                {
                  "version": "0.2.0",
                  "assets": {
                    "AgentEyesApp-win-x64.exe": { "version": "0.2.0", "sha256": "AA", "platform": "windows", "size": 10 },
                    "agenteyes-ffmpeg-win-x64.zip": { "version": "7.1.1", "sha256": "BB", "platform": "windows", "size": 20 },
                    "no-own-version.exe": { "sha256": "CC", "platform": "windows", "size": 30 }
                  }
                }
                """;
            var m = ReleaseManifest.Parse(json);
            Assert.Equal("0.2.0", m.Version);
            Assert.Equal("7.1.1", m.TryGetAsset("agenteyes-ffmpeg-win-x64.zip")!.Version);
            Assert.Equal("0.2.0", m.TryGetAsset("no-own-version.exe")!.Version); // inherited
        }

        [Fact]
        public void Manifest_RejectsStructurallyInvalidJson()
        {
            Assert.Throws<FormatException>(() => ReleaseManifest.Parse(""));
            Assert.Throws<FormatException>(() => ReleaseManifest.Parse("{}"));
            Assert.Throws<FormatException>(() => ReleaseManifest.Parse("""{ "version": "1.0.0" }"""));
        }

        // ---- versions -------------------------------------------------------

        [Theory]
        [InlineData("0.2.0", "0.1.0", true)]
        [InlineData("v0.2.0", "0.1.0", true)]
        [InlineData("0.2.0-rc1", "0.1.0", true)]
        [InlineData("0.1.0", "0.1.0", false)]
        [InlineData("0.1.0", "0.2.0", false)]
        [InlineData("0.1.0+abc", "0.1.0.4", false)]   // 4th part ignored by normalization
        [InlineData(null, "0.1.0", false)]
        [InlineData("0.2.0", "garbage", false)]       // unreadable installed -> never auto-update
        // Pre-release channel (issue #111): the dotted "-rc.N" suffix parses (does not throw
        // like a raw System.Version would) and normalizes to its X.Y.Z prefix.
        [InlineData("1.0.0-rc.1", "1.0.0", false)]    // an rc of the SAME version is not newer than the stable install
        [InlineData("v1.0.0-rc.1", "0.9.1", true)]    // higher numeric prefix wins, leading 'v' + suffix both tolerated
        [InlineData("1.0.0", "1.0.0-rc.1", false)]    // stable == the rc it supersedes (both normalize to 1.0.0)
        public void VersionUtil_IsNewer(string? candidate, string? installed, bool expected)
        {
            Assert.Equal(expected, VersionUtil.IsNewer(candidate, installed));
        }

        // ---- planner --------------------------------------------------------

        [Fact]
        public void Planner_DecidesPerComponentIndependently()
        {
            var manifest = ReleaseManifest.Parse("""
                {
                  "version": "0.2.0",
                  "assets": {
                    "AgentEyesApp-win-x64.exe": { "version": "0.2.0", "sha256": "AA" },
                    "agenteyes-win-x64.exe": { "version": "0.2.0", "sha256": "BB" },
                    "agenteyes-ffmpeg-win-x64.zip": { "version": "7.1.1", "sha256": "CC" }
                  }
                }
                """);
            var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase)
            {
                ["app"] = new("app", true, "0.1.0", "x"),          // behind -> Update
                ["cli"] = new("cli", false, null, "x"),            // absent -> Install
                ["ffmpeg"] = new("ffmpeg", true, "7.1.1", "x"),    // current -> UpToDate
                // setup-cli missing from map -> treated as absent, but its asset is
                // also missing from the manifest -> MissingAsset
            };

            var plan = UpdatePlanner.Plan(ComponentRegistry.All, installed, manifest);

            Assert.Equal(PlanItemKind.Update, plan.Items.Single(i => i.ComponentId == "app").Kind);
            Assert.Equal(PlanItemKind.Install, plan.Items.Single(i => i.ComponentId == "cli").Kind);
            Assert.Equal(PlanItemKind.UpToDate, plan.Items.Single(i => i.ComponentId == "ffmpeg").Kind);
            Assert.Equal(PlanItemKind.MissingAsset, plan.Items.Single(i => i.ComponentId == "setup-cli").Kind);
            Assert.Equal(2, plan.Actionable.Count);
        }

        // ---- swapper --------------------------------------------------------

        [Fact]
        public void Swapper_PlacesWithBackupAndRollsBack()
        {
            var target = Path.Combine(_temp, "swap", "tool.exe");
            var v1 = Path.Combine(_temp, "v1.bin");
            var v2 = Path.Combine(_temp, "v2.bin");
            File.WriteAllText(v1, "one");
            File.WriteAllText(v2, "two");

            Assert.Null(InstallSwapper.Place(target, v1));          // fresh install: no backup
            Assert.Equal("one", File.ReadAllText(target));

            var backup = InstallSwapper.Place(target, v2);          // upgrade: backup kept
            Assert.Equal("two", File.ReadAllText(target));
            Assert.Equal("one", File.ReadAllText(backup!));

            Assert.True(InstallSwapper.Rollback(target));           // restore previous
            Assert.Equal("one", File.ReadAllText(target));
            Assert.False(InstallSwapper.Rollback(target));          // backup consumed
        }

        // ---- PATH helpers ---------------------------------------------------

        [Fact]
        public void Finalizer_PathHelpersAreIdempotentAndCaseInsensitive()
        {
            const string dir = @"C:\Users\x\AppData\Local\AgentEyes\app";
            var with = InstallFinalizer.ComputePathWith(@"C:\Windows", dir);
            Assert.Equal($@"C:\Windows;{dir}", with);
            Assert.Equal(with, InstallFinalizer.ComputePathWith(with, dir.ToUpperInvariant()));
            Assert.Equal(@"C:\Windows", InstallFinalizer.ComputePathWithout(with, dir.ToUpperInvariant() + @"\"));
        }

        // ---- single-file native extraction dir (issue #120) -------------------

        [Fact]
        public void Layout_BundleExtractDir_IsUnderTheLocalRootNotTemp()
        {
            var layout = new InstallLayout(@"C:\Users\x\AppData\Local\AgentEyes");
            Assert.Equal(@"C:\Users\x\AppData\Local\AgentEyes\bundle", layout.BundleExtractDir);
            Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), layout.BundleExtractDir, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void IsSameDirectory_IgnoresCaseTrailingSlashAndPadding()
        {
            const string dir = @"C:\Users\x\AppData\Local\AgentEyes\bundle";
            Assert.True(InstallFinalizer.IsSameDirectory(dir, dir));
            Assert.True(InstallFinalizer.IsSameDirectory(dir.ToUpperInvariant() + @"\", dir));
            Assert.True(InstallFinalizer.IsSameDirectory("  " + dir + "  ", dir));
            Assert.False(InstallFinalizer.IsSameDirectory(@"C:\Temp\.net", dir));
            Assert.False(InstallFinalizer.IsSameDirectory(null, dir));
            Assert.False(InstallFinalizer.IsSameDirectory("   ", dir));
        }

        [Fact]
        public void SetBundleExtractBaseDir_CreatesTheDirectoryAndIsIdempotent()
        {
            // Point the variable at a temp root so the test never touches the real install.
            var layout = new InstallLayout(Path.Combine(_temp, "bundle-root"));
            var before = Environment.GetEnvironmentVariable(
                InstallFinalizer.BundleExtractBaseDirVariable, EnvironmentVariableTarget.User);
            var pathBefore = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            try
            {
                Assert.True(InstallFinalizer.SetBundleExtractBaseDir(layout));   // first call sets it
                Assert.True(Directory.Exists(layout.BundleExtractDir));
                Assert.True(InstallFinalizer.IsBundleExtractBaseDirSet(layout));
                Assert.Equal(layout.BundleExtractDir, Environment.GetEnvironmentVariable(
                    InstallFinalizer.BundleExtractBaseDirVariable, EnvironmentVariableTarget.User));

                Assert.False(InstallFinalizer.SetBundleExtractBaseDir(layout));  // second call: no change
                Assert.True(InstallFinalizer.IsBundleExtractBaseDirSet(layout));
                // The PATH is untouched by this code path.
                Assert.Equal(pathBefore, Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));

                Assert.True(InstallFinalizer.RemoveBundleExtractBaseDir(layout)); // uninstall clears it
                Assert.False(InstallFinalizer.IsBundleExtractBaseDirSet(layout));
                Assert.False(InstallFinalizer.RemoveBundleExtractBaseDir(layout)); // already gone
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    InstallFinalizer.BundleExtractBaseDirVariable, before, EnvironmentVariableTarget.User);
            }
        }

        [Fact]
        public void RemoveBundleExtractBaseDir_LeavesAValuePointingElsewhereAlone()
        {
            var layout = new InstallLayout(Path.Combine(_temp, "bundle-root"));
            var before = Environment.GetEnvironmentVariable(
                InstallFinalizer.BundleExtractBaseDirVariable, EnvironmentVariableTarget.User);
            const string foreign = @"C:\SomewhereElse\dotnet-bundles";
            try
            {
                Environment.SetEnvironmentVariable(
                    InstallFinalizer.BundleExtractBaseDirVariable, foreign, EnvironmentVariableTarget.User);
                Assert.False(InstallFinalizer.RemoveBundleExtractBaseDir(layout));
                Assert.Equal(foreign, Environment.GetEnvironmentVariable(
                    InstallFinalizer.BundleExtractBaseDirVariable, EnvironmentVariableTarget.User));
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    InstallFinalizer.BundleExtractBaseDirVariable, before, EnvironmentVariableTarget.User);
            }
        }

        [Fact]
        public void UninstallPlan_IncludesTheBundleDirectoryAndItsVariable()
        {
            // Plan() is pure (existence checks only) - safe to run against a temp root.
            var layout = new InstallLayout(Path.Combine(_temp, "uninstall-root"));
            Directory.CreateDirectory(Path.Combine(layout.BundleExtractDir, "AgentEyesApp", "hash"));
            File.WriteAllText(Path.Combine(layout.BundleExtractDir, "AgentEyesApp", "hash", "wpfgfx_cor3.dll"), "native");

            var plan = new Uninstaller(layout).Plan();

            var dir = plan.Single(t => t.Kind == UninstallKind.Directory && t.Path == layout.BundleExtractDir);
            Assert.True(dir.Present);
            Assert.Equal("Native extraction cache", dir.Description);

            var envVar = plan.Single(t => t.Kind == UninstallKind.EnvVar);
            Assert.Contains(InstallFinalizer.BundleExtractBaseDirVariable, envVar.Description, StringComparison.Ordinal);
            Assert.False(envVar.Present);   // this temp layout is not the installed one
        }

        // ---- offline end-to-end: install then selective update ----------------

        [Fact]
        public async Task EndToEnd_LocalReleaseDir_InstallThenSelectiveUpdate()
        {
            // Release v1: all four assets.
            var releaseDir = Path.Combine(_temp, "release");
            Directory.CreateDirectory(releaseDir);
            WriteAsset(releaseDir, "AgentEyesApp-win-x64.exe", "app-v1");
            WriteAsset(releaseDir, "agenteyes-win-x64.exe", "cli-v1");
            WriteAsset(releaseDir, "agenteyes-setup-cli-win-x64.exe", "setup-v1");
            WriteFfmpegZip(releaseDir, "ffmpeg-v1");
            WriteManifest(releaseDir, releaseVersion: "0.1.0", ffmpegVersion: "7.1.0");

            var layout = new InstallLayout(Path.Combine(_temp, "root"));
            var release = ReleaseSource.LoadLocalReleaseDir(releaseDir);
            var source = new ReleaseSource();

            // Install everything.
            var reader = new InstalledStateReader(layout);
            var result = await new Orchestrator(layout, reader).RunAsync(
                ComponentRegistry.All, release.Manifest,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));

            Assert.NotNull(result.Run);
            Assert.Equal(0, result.Run!.Failed);
            Assert.Equal("app-v1", File.ReadAllText(layout.PathFor(ComponentRegistry.App)));
            Assert.Equal("ffmpeg-v1", File.ReadAllText(Path.Combine(layout.AppDir, "ffmpeg.exe")));
            Assert.True(File.Exists(Path.Combine(layout.AppDir, "ffprobe.exe")));

            // Release v2: app bumped, ffmpeg version unchanged.
            WriteAsset(releaseDir, "AgentEyesApp-win-x64.exe", "app-v2");
            WriteFfmpegZip(releaseDir, "ffmpeg-v2");                     // content differs, version does not
            WriteManifest(releaseDir, releaseVersion: "0.2.0", ffmpegVersion: "7.1.0");
            var release2 = ReleaseSource.LoadLocalReleaseDir(releaseDir);

            var result2 = await new Orchestrator(layout, new InstalledStateReader(layout)).RunAsync(
                ComponentRegistry.All, release2.Manifest,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release2.DownloadUrls, ct));

            Assert.NotNull(result2.Run);
            Assert.Equal(0, result2.Run!.Failed);
            Assert.Equal("app-v2", File.ReadAllText(layout.PathFor(ComponentRegistry.App)));
            // ffmpeg was up to date by version: NOT re-applied despite different zip content.
            Assert.Equal("ffmpeg-v1", File.ReadAllText(Path.Combine(layout.AppDir, "ffmpeg.exe")));
            // The previous app build is kept as the .old backup.
            Assert.Equal("app-v1", File.ReadAllText(layout.PathFor(ComponentRegistry.App) + ".old"));

            // Third pass: nothing to do.
            var result3 = await new Orchestrator(layout, new InstalledStateReader(layout)).RunAsync(
                ComponentRegistry.All, release2.Manifest,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release2.DownloadUrls, ct));
            Assert.True(result3.NoWork);
        }

        [Fact]
        public async Task Runner_RejectsSha256Mismatch()
        {
            var releaseDir = Path.Combine(_temp, "badrelease");
            Directory.CreateDirectory(releaseDir);
            WriteAsset(releaseDir, "AgentEyesApp-win-x64.exe", "app-v1");
            // Manifest with a deliberately wrong hash for the app.
            File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), """
                {
                  "version": "0.1.0",
                  "assets": {
                    "AgentEyesApp-win-x64.exe": { "version": "0.1.0", "sha256": "DEADBEEF" }
                  }
                }
                """);

            var layout = new InstallLayout(Path.Combine(_temp, "badroot"));
            var release = ReleaseSource.LoadLocalReleaseDir(releaseDir);
            var source = new ReleaseSource();

            var plan = UpdatePlanner.Plan(new[] { ComponentRegistry.App },
                new Dictionary<string, InstalledComponent>(), release.Manifest);
            var runner = new UpdateRunner(layout, new[] { ComponentRegistry.App },
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));
            var run = await runner.ApplyAsync(plan);

            Assert.Equal(1, run.Failed);
            Assert.False(File.Exists(layout.PathFor(ComponentRegistry.App)));
        }

        // ---- helpers --------------------------------------------------------

        private static void WriteAsset(string dir, string name, string content) =>
            File.WriteAllText(Path.Combine(dir, name), content);

        private static void WriteFfmpegZip(string dir, string content)
        {
            var zipPath = Path.Combine(dir, "agenteyes-ffmpeg-win-x64.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = zip.CreateEntry(exe);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }

        private static void WriteManifest(string dir, string releaseVersion, string ffmpegVersion)
        {
            string Sha(string name) => Hashing.Sha256OfFile(Path.Combine(dir, name));
            File.WriteAllText(Path.Combine(dir, "release-manifest.json"), $$"""
                {
                  "version": "{{releaseVersion}}",
                  "assets": {
                    "AgentEyesApp-win-x64.exe": { "version": "{{releaseVersion}}", "sha256": "{{Sha("AgentEyesApp-win-x64.exe")}}" },
                    "agenteyes-win-x64.exe": { "version": "{{releaseVersion}}", "sha256": "{{Sha("agenteyes-win-x64.exe")}}" },
                    "agenteyes-setup-cli-win-x64.exe": { "version": "{{releaseVersion}}", "sha256": "{{Sha("agenteyes-setup-cli-win-x64.exe")}}" },
                    "agenteyes-ffmpeg-win-x64.zip": { "version": "{{ffmpegVersion}}", "sha256": "{{Sha("agenteyes-ffmpeg-win-x64.zip")}}" }
                  }
                }
                """);
        }
    }
}
