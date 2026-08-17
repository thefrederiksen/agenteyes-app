using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #151 - the guard that stops this defect shipping a fourth time.
    ///
    /// Three releases in a row shipped the same bug: a post-stop step was wired into ONE stop path
    /// and silently skipped on the others (#141 thumbnail, #142 repair pass, #151 the tray producing
    /// raw media only). The tray is not an edge case - the app normally runs with --tray and never
    /// builds MainWindow, so the tray IS the primary stop control.
    ///
    /// The fix was to make the routing impossible to get wrong: exactly one file may call
    /// <c>RecordingService.Stop</c>, and it offers three NAMED operations - Keep (full pipeline),
    /// Discard (stop and delete), StopWithoutPostProcessing (deliberately raw, must give a reason).
    /// This test enumerates every stop call site in the solution SOURCE and fails when a new one
    /// appears anywhere else. A bare <c>_svc.Stop()</c> added to a new caller fails here.
    ///
    /// It reads source rather than IL because the test assembly cannot reference AgentEyes.App (a
    /// WinExe) and because "which file calls this" is a source fact. The repo root is stamped into
    /// the assembly by the .csproj, so the scan cannot silently look at nothing.
    /// </summary>
    public sealed class StopPathTests
    {
        /// <summary>The one file allowed to call RecordingService.Stop.</summary>
        private const string StopOwner = "RecordingStop.cs";

        /// <summary>The named operations that file must offer - a caller that skips the pipeline
        /// has to pick one by name instead of just not calling it.</summary>
        private static readonly string[] RequiredOperations =
        {
            "public static StoppedRecording Keep(",
            "public static RecordResult Discard(",
            "public static RecordResult StopWithoutPostProcessing(",
        };

        /// <summary>Every file that stops a recording. Each must route through RecordingStop; the
        /// list is asserted to be non-empty and each entry to exist, so a rename cannot turn this
        /// test into a check that passes by finding nothing.</summary>
        private static readonly string[] KnownStopCallers =
        {
            @"src\AgentEyes.App\MainWindow.xaml.cs",   // window Stop button + HUD stop + HUD discard
            @"src\AgentEyes.App\TrayHost.cs",          // tray menu stop + tray Quit
            @"src\AgentEyes.App\RestServer.cs",        // POST /record/stop
            @"src\AgentEyes.App\TestPanel.xaml.cs",    // guided test takes (named skip)
        };

        // ---- the enumeration ------------------------------------------------

        [Fact]
        public void StopCallers_EveryOne_RoutesThroughTheSharedOperation()
        {
            var sources = SolutionSources();
            Assert.True(sources.Count > 50,
                $"the source scan found only {sources.Count} files - it is not looking at the repo");

            var names = RecordingServiceVariableNames(sources);
            Assert.Contains("_svc", names);   // the field every App caller holds; proves the scan works

            var offenders = new List<string>();
            foreach (var (path, text) in sources)
            {
                if (string.Equals(Path.GetFileName(path), StopOwner, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (int line in StopCallLines(text, names))
                    offenders.Add($"{Relative(path)}:{line}");
            }

            Assert.True(offenders.Count == 0,
                "These call RecordingService.Stop directly instead of going through "
                + $"RecordingStop (Keep / Discard / StopWithoutPostProcessing):{Environment.NewLine}"
                + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void StopOwner_OffersTheNamedOperations_SoSkippingIsNeverByOmission()
        {
            string text = ReadRepoFile(@"src\AgentEyes.App\RecordingStop.cs");
            foreach (string signature in RequiredOperations)
                Assert.Contains(signature, text, StringComparison.Ordinal);
        }

        [Fact]
        public void StopOwner_IsTheOnlyFileThatCallsRecordingServiceStop()
        {
            var sources = SolutionSources();
            var names = RecordingServiceVariableNames(sources);

            var files = sources
                .Where(s => StopCallLines(s.Text, names).Any())
                .Select(s => Path.GetFileName(s.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Exactly one file, and it is the owner - not "none", which would mean the pattern
            // stopped matching and the guard had quietly become decorative.
            Assert.Equal(new[] { StopOwner }, files);
        }

        [Theory]
        [InlineData(@"src\AgentEyes.App\MainWindow.xaml.cs")]
        [InlineData(@"src\AgentEyes.App\TrayHost.cs")]
        [InlineData(@"src\AgentEyes.App\RestServer.cs")]
        [InlineData(@"src\AgentEyes.App\TestPanel.xaml.cs")]
        public void KnownStopCaller_UsesRecordingStop(string relativePath)
        {
            string text = ReadRepoFile(relativePath);
            Assert.Contains("RecordingStop.", text, StringComparison.Ordinal);
        }

        [Fact]
        public void KnownStopCallers_AllExist()
        {
            Assert.NotEmpty(KnownStopCallers);
            foreach (string relative in KnownStopCallers)
                Assert.True(File.Exists(Path.Combine(RepoRoot, relative)), relative + " is missing");
        }

        [Fact]
        public void TrayQuit_LogsTheStopFailure_InsteadOfSwallowingIt()
        {
            // Issue #151 AC2: Quit used to be `try { _svc.Stop(); } catch { }` - a stop failure
            // vanished with the recording. The failure must reach Log.Error WITH the directory.
            string quit = MethodBody(ReadRepoFile(@"src\AgentEyes.App\TrayHost.cs"), "private void Quit()");
            Assert.DoesNotContain("catch { }", quit, StringComparison.Ordinal);
            Assert.DoesNotContain("catch {}", quit, StringComparison.Ordinal);
            Assert.Contains("[TrayHost] Quit: stopping the recording FAILED (dir=", quit, StringComparison.Ordinal);
        }

        [Fact]
        public void PostRecordingCompletion_IsAnnouncedOnlyByTheSequenceItself()
        {
            // Criterion 6: Completed fires exactly ONCE per stopped recording. It did not before -
            // the window announced its private pipeline's completion itself. Only PostRecording.Run
            // may announce now, so no path can double-fire or skip it.
            var sources = SolutionSources();
            var offenders = sources
                .Where(s => !string.Equals(Path.GetFileName(s.Path), "PostRecording.cs", StringComparison.OrdinalIgnoreCase))
                .Where(s => s.Text.Contains("PostRecording.NotifyCompleted(", StringComparison.Ordinal))
                .Select(s => Relative(s.Path))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Only PostRecording.Run may announce completion; these announce it themselves: "
                + string.Join(", ", offenders));
        }

        // ---- scanning helpers -----------------------------------------------

        private readonly record struct SourceFile(string Path, string Text);

        /// <summary>The repo root, stamped in at build time by the .csproj. No guessing from the
        /// working directory, and a loud failure if the source tree is not there.</summary>
        private static string RepoRoot
        {
            get
            {
                string? root = typeof(StopPathTests).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "The RepoRoot assembly metadata is missing - add it to AgentEyes.Tests.csproj.");
                if (!Directory.Exists(Path.Combine(root, "src")))
                    throw new InvalidOperationException($"No src directory under the stamped repo root '{root}'.");
                return root;
            }
        }

        private static string ReadRepoFile(string relativePath)
        {
            string full = Path.Combine(RepoRoot, relativePath);
            if (!File.Exists(full)) throw new FileNotFoundException("Expected source file is missing", full);
            return File.ReadAllText(full);
        }

        /// <summary>The text of one method, from its signature to its closing brace, so an
        /// assertion about that method cannot be answered by code somewhere else in the file.</summary>
        private static string MethodBody(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException($"'{signature}' is not in this file any more.");

            int open = text.IndexOf('{', start);
            if (open < 0) throw new InvalidOperationException($"'{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException($"'{signature}' body is unbalanced.");
        }

        private static string Relative(string full) =>
            full.StartsWith(RepoRoot, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(RepoRoot.Length).TrimStart(Path.DirectorySeparatorChar)
                : full;

        /// <summary>Every C# source file in src/ and tools/, minus build output.</summary>
        private static List<SourceFile> SolutionSources()
        {
            var files = new List<SourceFile>();
            foreach (string area in new[] { "src", "tools" })
            {
                string root = Path.Combine(RepoRoot, area);
                if (!Directory.Exists(root)) continue;
                foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    if (path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(new SourceFile(path, File.ReadAllText(path)));
                }
            }
            return files;
        }

        /// <summary>
        /// Every identifier declared as a RecordingService anywhere in the solution - fields,
        /// parameters, locals. Derived from the source instead of hard-coded, so a new caller that
        /// names its field something else is still caught.
        /// </summary>
        private static HashSet<string> RecordingServiceVariableNames(IEnumerable<SourceFile> sources)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            // The trailing [;,)={] keeps this to real declarations - prose in a comment that happens
            // to read "... the shared RecordingService so ..." must not enrol "so" as a variable.
            var declaration = new Regex(@"\bRecordingService\??\s+(@?[A-Za-z_][A-Za-z0-9_]*)\s*[;,)={]", RegexOptions.Compiled);
            foreach (var source in sources)
                foreach (Match m in declaration.Matches(source.Text))
                    names.Add(m.Groups[1].Value);
            return names;
        }

        /// <summary>1-based line numbers where <paramref name="text"/> calls .Stop() on one of the
        /// RecordingService identifiers. Comment lines are skipped - the class documents the trap it
        /// prevents and must be able to name it.</summary>
        private static IEnumerable<int> StopCallLines(string text, IEnumerable<string> names)
        {
            var pattern = new Regex(
                @"(?<![A-Za-z0-9_.])(" + string.Join("|", names.Select(Regex.Escape)) + @")\s*\.\s*Stop\s*\(\s*\)",
                RegexOptions.Compiled);

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                    continue;
                if (pattern.IsMatch(line)) yield return i + 1;
            }
        }
    }
}
