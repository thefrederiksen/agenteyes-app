using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #153, the half that lives in the WPF app: a stop failure must never be swallowed, and
    /// the user must never be told it was "logged" by a catch that logs nothing.
    ///
    /// These are source facts - which catch writes which log line - and the test assembly cannot
    /// reference AgentEyes.App (a WinExe), which is exactly how an empty catch survived in tray Quit
    /// and an unlogged one in the window while both paths displayed the word "logged". The repo root
    /// is stamped into the assembly by the .csproj, so a scan can never silently look at nothing.
    /// </summary>
    public sealed class StopFailureSourceTests
    {
        /// <summary>Every method that catches a failure on a stop path. Each must log it.</summary>
        public static IEnumerable<object[]> StopCatchers => new[]
        {
            new object[] { @"src\AgentEyes.App\MainWindow.xaml.cs", "private async Task StopAsync()" },
            new object[] { @"src\AgentEyes.App\MainWindow.xaml.cs", "private async Task DiscardAsync()" },
            new object[] { @"src\AgentEyes.App\TestPanel.xaml.cs", "private async void Stop_Click(" },
            new object[] { @"src\AgentEyes.App\TrayHost.cs", "private void Quit()" },
            new object[] { @"src\AgentEyes.App\TrayHost.cs", "private void StopInBackground()" },
        };

        /// <summary>The only files allowed to tell the user a failure was "(logged)" - each is
        /// asserted below to actually log it.</summary>
        private static readonly string[] LoggedClaimFiles =
        {
            @"src\AgentEyes.App\MainWindow.xaml.cs",
            @"src\AgentEyes.App\TestPanel.xaml.cs",
        };

        // ---- criterion 5: no empty catch, no catch whose only output is UI ----

        [Theory]
        [MemberData(nameof(StopCatchers))]
        public void StopCatch_LogsTheFailure_NeverJustSetsAUiString(string relativePath, string signature)
        {
            string body = RepoSource.MethodBody(RepoSource.Read(relativePath), signature);

            Assert.DoesNotContain("catch { }", body, StringComparison.Ordinal);
            Assert.DoesNotContain("catch {}", body, StringComparison.Ordinal);

            var blocks = CatchBlocks(body);
            Assert.NotEmpty(blocks);   // a stop path with no catch at all means this test stopped looking
            foreach (string block in blocks)
                Assert.True(Logs(block),
                    $"{relativePath} -> {signature}: this catch reports the failure to the UI without logging it:"
                    + Environment.NewLine + block);
        }

        [Fact]
        public void StopFailureHelper_IsWhatMakesTheWindowsCatchesLog()
        {
            // The window's catches delegate to one helper; that helper is where Log.Error must be, or
            // the assertion above would be satisfied by a name that does nothing.
            string helper = RepoSource.MethodBody(
                RepoSource.Read(@"src\AgentEyes.App\MainWindow.xaml.cs"),
                "private static string LogStopFailure(");
            Assert.Contains("Log.Error(", helper, StringComparison.Ordinal);
            Assert.Contains("dir=", helper, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(StopCatchers))]
        public void StopCatch_NamesTheRecordingDirectory_SoTheRecordingCanBeFound(string relativePath, string signature)
        {
            // A stop failure with no directory in it is a log entry nobody can act on - the recording
            // is exactly what was lost. Either the catch names it, or it delegates to the window's
            // one helper, which is asserted to name it above.
            string body = RepoSource.MethodBody(RepoSource.Read(relativePath), signature);
            Assert.True(body.Contains("dir=", StringComparison.Ordinal)
                        || body.Contains("LogStopFailure(", StringComparison.Ordinal),
                $"{relativePath} -> {signature}: the stop failure is logged without naming the recording directory");
        }

        // ---- criterion 6: "(logged)" is only claimed where it is true --------

        [Fact]
        public void LoggedClaim_AppearsOnlyInFilesWhoseCatchesLog()
        {
            var files = SourceFilesContaining("(logged)");
            Assert.NotEmpty(files);   // the phrase is still in the UI; a rename must fail here, not pass
            Assert.Equal(LoggedClaimFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray(),
                files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        [Fact]
        public void LoggedClaim_InTheTestPanel_IsBackedByALogCall()
        {
            string body = RepoSource.MethodBody(
                RepoSource.Read(@"src\AgentEyes.App\TestPanel.xaml.cs"), "private async void Stop_Click(");
            Assert.Contains("(logged)", body, StringComparison.Ordinal);
            Assert.Contains("Log.Error(", body, StringComparison.Ordinal);
        }

        // ---- the engine side: the stop is routed through the isolated sequence

        [Fact]
        public void RecordingServiceStop_StopsEveryWriterThroughTheIsolatedSequence()
        {
            string stop = RepoSource.MethodBody(
                RepoSource.Read(@"src\AgentEyes.Core\RecordingService.cs"), "public RecordResult Stop()");

            Assert.Contains("RecordingStopSequence.Run(", stop, StringComparison.Ordinal);
            Assert.Contains("RecoveryManifest.Save(", stop, StringComparison.Ordinal);
            Assert.Contains("throw new RecordingStopFailedException(", stop, StringComparison.Ordinal);

            // The defect itself: writers stopped and disposed inline, in one try block, so the first
            // throw abandoned everything after it. They are method-group STEPS now (audio.Stop with
            // no parentheses), so a re-appearing inline call is what this catches.
            var inline = new Regex(@"\b(audio|loop|video)\s*\.\s*(Stop|Dispose)\s*\(");
            var offenders = NonCommentLines(stop).Where(line => inline.IsMatch(line)).ToList();
            Assert.True(offenders.Count == 0,
                "RecordingService.Stop stops writers inline again instead of as isolated steps:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void RecordingServiceStop_ReportsAFailedStopInsteadOfALookingCleanIdle()
        {
            string source = RepoSource.Read(@"src\AgentEyes.Core\RecordingService.cs");

            // The failure survives the return to idle and is readable without catching the exception -
            // /status carries it, which is how QA can see a failed stop from outside the process.
            Assert.Contains("LastStopFailure", source, StringComparison.Ordinal);
            Assert.Contains("public bool LastStopFailed", source, StringComparison.Ordinal);
            Assert.Contains("public string? LastStopDir", source, StringComparison.Ordinal);

            // RecordingStopped means "a session ended cleanly" - a failed stop must not raise it.
            string stop = RepoSource.MethodBody(source, "public RecordResult Stop()");
            int thrown = stop.IndexOf("throw new RecordingStopFailedException(", StringComparison.Ordinal);
            int raised = stop.IndexOf("RecordingStopped?.Invoke()", StringComparison.Ordinal);
            Assert.True(thrown >= 0 && raised > thrown,
                "the failed-stop throw must come BEFORE RecordingStopped is raised");
        }

        // ---- scanning helpers ------------------------------------------------

        /// <summary>Every catch block in <paramref name="body"/>, brace-balanced so a nested block
        /// cannot end the match early.</summary>
        private static List<string> CatchBlocks(string body)
        {
            var blocks = new List<string>();
            foreach (Match m in Regex.Matches(body, @"\bcatch\b[^{]*\{"))
            {
                int open = m.Index + m.Length - 1;
                int depth = 0;
                for (int i = open; i < body.Length; i++)
                {
                    if (body[i] == '{') depth++;
                    else if (body[i] == '}' && --depth == 0)
                    {
                        blocks.Add(body.Substring(m.Index, i - m.Index + 1));
                        break;
                    }
                }
            }
            return blocks;
        }

        /// <summary>True when this catch block writes a log entry itself or through the window's
        /// one logging helper.</summary>
        private static bool Logs(string block) =>
            block.Contains("Log.Error(", StringComparison.Ordinal)
            || block.Contains("Log.Warn(", StringComparison.Ordinal)
            || block.Contains("LogStopFailure(", StringComparison.Ordinal);

        private static IEnumerable<string> NonCommentLines(string text) =>
            text.Replace("\r\n", "\n").Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

        /// <summary>The repo-relative C# files under src/ containing <paramref name="phrase"/> in real
        /// code (comment lines are skipped - these classes document the trap they prevent).</summary>
        private static List<string> SourceFilesContaining(string phrase)
        {
            var hits = new List<string>();
            string root = Path.Combine(RepoSource.Root, "src");
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) continue;
                if (NonCommentLines(File.ReadAllText(path)).Any(line => line.Contains(phrase, StringComparison.Ordinal)))
                    hits.Add(path.Substring(RepoSource.Root.Length).TrimStart(Path.DirectorySeparatorChar));
            }
            return hits;
        }
    }
}
