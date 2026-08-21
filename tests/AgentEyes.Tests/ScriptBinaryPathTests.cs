using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #9 - the guard that keeps the smoke scripts pointed at the binary that was actually built.
    ///
    /// Both product projects set <c>&lt;Platforms&gt;x64&lt;/Platforms&gt;</c>, so
    /// <c>dotnet build -c Release</c> lands in <c>bin\x64\Release\</c>. A plain <c>bin\Release\</c>
    /// directory left behind by an older checkout holds a stale binary; a script that launches it
    /// silently tests code nobody built (this produced a false QA FAIL on issue #141). This test
    /// scans every script under <c>scripts/</c> and fails when any of them references a build-output
    /// path whose <c>bin</c> is followed directly by a configuration segment (Release/Debug) instead
    /// of the x64 platform segment.
    ///
    /// Fail-closed arms (DEVELOPMENT_METHOD.md Section 6c):
    /// - EMPTY result = broken instrument: the scan asserts it actually visited the known launch
    ///   scripts, and that the x64 build-output path is PRESENT in the corpus - so a scan that reads
    ///   nothing, or a detector blind to build-output paths, fails rather than passing.
    /// - Known-bad input: the detector is unit-tested against stale-path samples and shown to FIRE.
    ///
    /// Honest limit: this is a TEXT scan of the script files (scripts are not compiled, so there is
    /// no IL to inspect - issue #9 assumption 2). It cannot see a stale path assembled at runtime
    /// from concatenated fragments (e.g. "bin\" + $config); it guards the literal-path form, which
    /// is the form every script in this repo uses and the form the defect shipped in.
    /// </summary>
    public sealed class ScriptBinaryPathTests
    {
        /// <summary>
        /// A build-output reference where "bin" is followed directly by a configuration name -
        /// i.e. no platform segment, the path a non-x64 build would produce. bin\x64\Release does
        /// not match ("x64" is not "Release"/"Debug"); bin\Release and bin/Debug do.
        /// </summary>
        private static readonly Regex StaleBinPath = new Regex(
            @"\bbin[\\/]+(Release|Debug)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Scripts that launch a built binary and therefore must be in the scanned corpus -
        /// if a rename removes one, the instrument is broken and the test fails loudly.</summary>
        private static readonly string[] KnownLaunchScripts =
        {
            "api-smoke.ps1",
            "gui-smoke.ps1",
            "py-client-smoke.ps1",
            "run-all.ps1",
            "try.cmd",
        };

        private static string ScriptsDir => Path.Combine(RepoSource.Root, "scripts");

        private static List<string> ScriptFiles()
        {
            if (!Directory.Exists(ScriptsDir))
                throw new DirectoryNotFoundException("scripts directory missing under repo root: " + ScriptsDir);
            return Directory.EnumerateFiles(ScriptsDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".ps1" || ext == ".psm1" || ext == ".cmd" || ext == ".bat" || ext == ".sh";
                })
                .ToList();
        }

        [Fact]
        public void ScriptsScan_KnownLaunchScripts_AreAllInTheCorpus()
        {
            // Instrument check: an empty or partial scan is a broken instrument, never a clean run.
            List<string> files = ScriptFiles();
            string[] names = files.Select(Path.GetFileName).ToArray()!;
            foreach (string expected in KnownLaunchScripts)
            {
                Assert.Contains(expected, names);
            }
        }

        [Fact]
        public void ScriptsScan_X64BuildOutputPath_IsPresentInCorpus()
        {
            // Presence arm: the corpus must actually contain build-output paths for the stale-path
            // scan to mean anything. If every literal path vanished (refactored into some helper this
            // test cannot see), this fails instead of the scan passing on an empty field.
            bool anyX64 = ScriptFiles().Any(f =>
                Regex.IsMatch(File.ReadAllText(f), @"bin[\\/]+x64[\\/]+Release", RegexOptions.IgnoreCase));
            Assert.True(anyX64,
                "No script under scripts/ contains a bin\\x64\\Release build-output path any more. " +
                "The stale-path scan has nothing to guard - update ScriptBinaryPathTests to match how " +
                "scripts now locate built binaries.");
        }

        [Fact]
        public void Scripts_AllFiles_ContainNoNonX64BuildOutputPath()
        {
            List<string> files = ScriptFiles();
            Assert.NotEmpty(files);

            var offenders = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (StaleBinPath.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            Assert.True(offenders.Count == 0,
                "Non-x64 build-output path(s) under scripts/ - these launch a stale binary, not the " +
                "one `dotnet build AgentEyes.sln -c Release` produces (bin\\x64\\Release\\). Fix the " +
                "path(s):\n" + string.Join("\n", offenders));
        }

        [Theory]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData(@"set ""CLIBIN=%~dp0..\src\AgentEyes.Core\bin\Release\net8.0-windows10.0.19041.0""")]
        [InlineData("cli/bin/Release/net8.0/agenteyes.exe")]
        [InlineData(@"src\AgentEyes.App\bin\Debug\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        public void StaleBinPathDetector_KnownBadReference_Fires(string knownBad)
        {
            // Mutation evidence: the detector, run against the exact stale strings this issue is
            // about (including the pre-fix api-smoke/try.cmd lines), FIRES. A detector only ever run
            // against the state we hope passes has demonstrated nothing (Section 6c item 3).
            Assert.Matches(StaleBinPath, knownBad);
        }

        [Theory]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData("src/AgentEyes.Core/bin/x64/Release/net8.0-windows10.0.19041.0/agenteyes.exe")]
        [InlineData("Publish-SingleFile produces AgentEyesApp-win-x64.exe under dist/release")]
        public void StaleBinPathDetector_X64OrNonBuildPath_DoesNotFire(string good)
        {
            Assert.DoesNotMatch(StaleBinPath, good);
        }
    }
}
