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
    /// silently tests code nobody built (this produced a false QA FAIL on issue #141).
    ///
    /// WHAT THIS GUARD PINS (the exact textual fact, no more): in every script under
    /// <c>scripts/</c>, every textual occurrence of a <c>bin\</c> or <c>bin/</c> path segment is
    /// immediately followed by the literal <c>x64\Release</c>. ANY other continuation fires:
    /// - stale literals (bin\Release, bin/Debug), wrong platforms (bin\x86\..., bin\arm64\...),
    ///   wrong configuration (bin\x64\Debug), unknown segments (bin\AnyCPU\...);
    /// - statically visible COMPOSITION, where the segment after bin is not a literal at all:
    ///   a variable (bin\$platform\Release), a format placeholder (bin\{0}\Release), a cmd
    ///   variable (bin\%PLATFORM%\Release), or a fragment boundary ("...\bin\" + $platform).
    ///   Such a path cannot be verified by reading the text, so it is rejected as UNVERIFIABLE -
    ///   fail closed: nothing after bin\ needs to be recognized to be rejected.
    ///
    /// Fail-closed arms (DEVELOPMENT_METHOD.md Section 6c):
    /// - EMPTY result = broken instrument: the scan asserts it actually visited the known launch
    ///   scripts, and that the x64 build-output path is PRESENT in the corpus - so a scan that reads
    ///   nothing, or a detector blind to build-output paths, fails rather than passing.
    /// - Known-bad input: the detector is unit-tested against stale-path AND composed-path samples
    ///   and shown to FIRE.
    ///
    /// Honest limit (what remains beyond static reach of this text scan): a launch path assembled
    /// at runtime from pieces that are never textually adjacent to <c>bin</c> in the script - e.g.
    /// <c>Join-Path $dir 'bin' $platform 'Release'</c>, a path read from an environment variable,
    /// a config file, or process output. No text scan can evaluate what the text never shows.
    /// This guard makes no claim about those; it pins the adjacent-text forms above, which are the
    /// only forms this repo's scripts have ever used and the form the original defect shipped in.
    /// </summary>
    public sealed class ScriptBinaryPathTests
    {
        /// <summary>
        /// Fires on any textual <c>bin\</c> / <c>bin/</c> segment whose continuation is not
        /// EXACTLY the literal <c>x64\Release</c>. There is no character class to satisfy after
        /// the lookahead, so a variable sigil ($), a placeholder ({), a cmd variable (%), a
        /// closing quote at a fragment boundary, or a segment this test has never heard of all
        /// fire equally: an unrecognized or unverifiable continuation is a defect until a human
        /// widens the allow-form, never a pass.
        /// </summary>
        private static readonly Regex NonX64BinSegment = new Regex(
            @"\bbin[\\/]+(?!x64[\\/]+Release\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A fully literal path segment: letters, digits, dot, underscore, hyphen.</summary>
        private static readonly Regex LiteralSegment = new Regex(
            @"^[A-Za-z0-9_.\-]+$",
            RegexOptions.Compiled);

        /// <summary>
        /// Classifies an offending line (one <see cref="NonX64BinSegment"/> already matched) for
        /// the guard's diagnostic. The two categories carry DIFFERENT claims and must not share one:
        /// - false (wrong literal): the segment(s) after bin\ are literal and provably not
        ///   x64\Release - such a path launches the wrong (stale or never-built) binary. This is a
        ///   statement of fact about the resolved path.
        /// - true (composed/unverifiable): a variable, placeholder, or fragment boundary sits where
        ///   the platform or configuration segment should be. The text alone cannot prove what the
        ///   path resolves to - e.g. $platform = 'x64' upstream composes to the CORRECT binary -
        ///   so the guard rejects it as textually UNVERIFIABLE, not as wrong.
        /// </summary>
        private static bool IsComposedOffender(string offendingLine)
        {
            Match m = NonX64BinSegment.Match(offendingLine);
            if (!m.Success)
                throw new InvalidOperationException("Not an offender line: " + offendingLine);

            string continuation = offendingLine.Substring(m.Index + m.Length);
            Match segs = Regex.Match(continuation, @"^([^\\/""'\s]*)(?:[\\/]+([^\\/""'\s]*))?");
            string first = segs.Groups[1].Value;
            if (!LiteralSegment.IsMatch(first))
                return true; // variable/placeholder/fragment right after bin\ - unverifiable
            if (first.Equals("x64", StringComparison.OrdinalIgnoreCase))
                return !LiteralSegment.IsMatch(segs.Groups[2].Value); // bin\x64\<non-literal> - unverifiable config
            return false; // literal non-x64 segment: provably not bin\x64\Release
        }

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
            // Presence arm: the corpus must actually contain build-output paths for the bin-segment
            // scan to mean anything. If every literal path vanished (refactored into some helper this
            // test cannot see), this fails instead of the scan passing on an empty field.
            bool anyX64 = ScriptFiles().Any(f =>
                Regex.IsMatch(File.ReadAllText(f), @"bin[\\/]+x64[\\/]+Release", RegexOptions.IgnoreCase));
            Assert.True(anyX64,
                "No script under scripts/ contains a bin\\x64\\Release build-output path any more. " +
                "The bin-segment scan has nothing to guard - update ScriptBinaryPathTests to match how " +
                "scripts now locate built binaries.");
        }

        [Fact]
        public void Scripts_EveryTextualBinSegment_IsLiterallyX64Release()
        {
            List<string> files = ScriptFiles();
            Assert.NotEmpty(files);

            var wrongLiterals = new List<string>();
            var composed = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!NonX64BinSegment.IsMatch(lines[i]))
                        continue;
                    string entry = $"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}";
                    (IsComposedOffender(lines[i]) ? composed : wrongLiterals).Add(entry);
                }
            }

            // The two categories carry different claims, so the diagnostic is split: a literal
            // non-x64 path provably launches the wrong binary; a composed path is rejected only
            // because the text cannot prove it - it may even resolve to the correct binary.
            var message = new System.Text.StringBuilder();
            if (wrongLiterals.Count > 0)
            {
                message.AppendLine(
                    "Literal non-x64 bin\\ path(s) under scripts/ - provably NOT the " +
                    "bin\\x64\\Release\\ output of `dotnet build AgentEyes.sln -c Release`, so " +
                    "each launches the wrong (stale or never-built) binary. Use the single " +
                    "literal bin\\x64\\Release path:");
                foreach (string entry in wrongLiterals) message.AppendLine("  " + entry);
            }
            if (composed.Count > 0)
            {
                message.AppendLine(
                    "Unverifiable composed bin\\ path(s) under scripts/ (variable, placeholder, " +
                    "or fragment where a literal segment belongs) - a text scan cannot statically " +
                    "prove such a path is bin\\x64\\Release (it may even resolve there), so it is " +
                    "rejected fail-closed. Use the single literal bin\\x64\\Release path:");
                foreach (string entry in composed) message.AppendLine("  " + entry);
            }

            Assert.True(wrongLiterals.Count == 0 && composed.Count == 0, message.ToString());
        }

        [Theory]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData(@"set ""CLIBIN=%~dp0..\src\AgentEyes.Core\bin\Release\net8.0-windows10.0.19041.0""")]
        [InlineData("cli/bin/Release/net8.0/agenteyes.exe")]
        [InlineData(@"src\AgentEyes.App\bin\Debug\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\x86\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData("src/AgentEyes.Core/bin/arm64/Release/net8.0-windows10.0.19041.0/agenteyes.exe")]
        [InlineData(@"src\AgentEyes.App\bin\x64\Debug\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        [InlineData(@"src\AgentEyes.App\bin\AnyCPU\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        public void NonX64BinSegmentDetector_KnownBadLiteral_Fires(string knownBad)
        {
            // Mutation evidence: the detector, run against the exact stale strings this issue is
            // about (including the pre-fix api-smoke/try.cmd lines) AND the wrong-platform forms the
            // round-1 review gate flagged (bin\x86, bin\arm64, plus bin\x64\Debug and an unknown
            // AnyCPU segment), FIRES on every one. A detector only ever run against the state we
            // hope passes has demonstrated nothing (Section 6c item 3).
            Assert.Matches(NonX64BinSegment, knownBad);
        }

        [Theory]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\$platform\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData(@"$exe = $srcDir + ""\bin\"" + $platform + ""\Release\net8.0\AgentEyesApp.exe""")]
        [InlineData(@"$exe = [string]::Format(""src\AgentEyes.App\bin\{0}\Release\AgentEyesApp.exe"", $platform)")]
        [InlineData(@"set ""CLIBIN=%~dp0..\src\AgentEyes.Core\bin\%PLATFORM%\Release\net8.0-windows10.0.19041.0""")]
        [InlineData("agent_dir=\"$root/src/AgentEyes.App/bin/${platform}/Release\"")]
        public void NonX64BinSegmentDetector_ComposedPath_Fires(string composed)
        {
            // The round-2 review gate's scenario: a refactor sets $platform = 'x86' upstream and
            // builds the launch path across variables or fragments - e.g. bin\$platform\Release, a
            // "...\bin\" + $x concatenation, a format placeholder, or a cmd %VAR%. The text alone
            // cannot prove what such a path resolves to, so the detector rejects the bin\ segment
            // as UNVERIFIABLE (fail closed) rather than trusting it.
            Assert.Matches(NonX64BinSegment, composed);
        }

        [Theory]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData(@"set ""CLIBIN=%~dp0..\src\AgentEyes.Core\bin\Release\net8.0-windows10.0.19041.0""")]
        [InlineData(@"src\AgentEyes.App\bin\Debug\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\x86\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData("src/AgentEyes.Core/bin/arm64/Release/net8.0-windows10.0.19041.0/agenteyes.exe")]
        [InlineData(@"src\AgentEyes.App\bin\x64\Debug\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        [InlineData(@"src\AgentEyes.App\bin\AnyCPU\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe")]
        public void OffenderDiagnostic_LiteralNonX64Path_ClassifiedAsWrongBinary(string knownBadLiteral)
        {
            // Diagnostic split (round-4 gate fix): a fully literal non-x64 path is provably NOT
            // the built binary, so the guard may - and does - say "launches the wrong binary".
            Assert.False(IsComposedOffender(knownBadLiteral));
        }

        [Theory]
        [InlineData(@"$platform = 'x64'; $exe = ""src\AgentEyes.App\bin\$platform\Release\AgentEyesApp.exe""")]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\$platform\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData(@"$exe = $srcDir + ""\bin\"" + $platform + ""\Release\net8.0\AgentEyesApp.exe""")]
        [InlineData(@"$exe = [string]::Format(""src\AgentEyes.App\bin\{0}\Release\AgentEyesApp.exe"", $platform)")]
        [InlineData(@"set ""CLIBIN=%~dp0..\src\AgentEyes.Core\bin\%PLATFORM%\Release\net8.0-windows10.0.19041.0""")]
        [InlineData("agent_dir=\"$root/src/AgentEyes.App/bin/${platform}/Release\"")]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\x64\$config\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        public void OffenderDiagnostic_ComposedPath_ClassifiedAsUnverifiable(string composed)
        {
            // Diagnostic split (round-4 gate fix): a composed path can resolve to the CORRECT
            // binary ($platform = 'x64' upstream - the first case is exactly that), so the guard
            // must NOT claim it launches the wrong binary. It is rejected only because the text
            // cannot statically prove it is bin\x64\Release - reported as UNVERIFIABLE.
            Assert.True(IsComposedOffender(composed));
        }

        [Theory]
        [InlineData(@"$exe = ""src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe""")]
        [InlineData("src/AgentEyes.Core/bin/x64/Release/net8.0-windows10.0.19041.0/agenteyes.exe")]
        [InlineData(@"# a Release build lands in bin\x64\Release\.")]
        [InlineData("Publish-SingleFile produces AgentEyesApp-win-x64.exe under dist/release")]
        public void NonX64BinSegmentDetector_LiteralX64ReleaseOrNonBuildPath_DoesNotFire(string good)
        {
            Assert.DoesNotMatch(NonX64BinSegment, good);
        }
    }
}
