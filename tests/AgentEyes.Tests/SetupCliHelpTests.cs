using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Cli;
using AgentEyes.Setup.Engine;
using Xunit;
using CliProgram = AgentEyes.Setup.Cli.Program;
using CliUsageException = AgentEyes.Setup.Cli.UsageException;

namespace AgentEyes.Tests
{
    // Declared INSIDE the namespace: AgentEyes.Core has its own internal AgentEyes.CliArgs, and a name in
    // an enclosing namespace outranks a compilation-unit alias, so a top-level alias would not bind.
    using CliArgs = AgentEyes.Setup.Cli.CliArgs;
    using CliHelp = AgentEyes.Setup.Cli.CliHelp;

    /// <summary>
    /// The classes that substitute the process-wide <see cref="ReleaseSource.DefaultTransport"/> run
    /// serially with each other. Two classes swapping one static in parallel would let a request
    /// land on the other class's transport - a false pass for the one asserting "no request".
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class ReleaseTransportSeamCollection
    {
        public const string Name = "ReleaseSource.DefaultTransport seam";
    }

    /// <summary>
    /// Issue #83: `agenteyes-setup install --help` ran a REAL install. On 2026-09-23 the owner's
    /// `agenteyes-setup-cli-win-x64.exe install --help` downloaded the latest release and replaced
    /// the installed app ("installed=0 updated=4"). The parser knew "help" as a flag, but Program
    /// dispatched help only when it was the FIRST argument, so `install --help` ran `install`; and
    /// an unknown option (`--bogus`) was silently filed as a flag, so a typo ran the command too.
    ///
    /// These tests run the CLI's REAL entry point (<see cref="CliProgram.RunAsync"/>) in-process,
    /// with two instruments that fail closed:
    ///   - the release channel is <see cref="ForbiddenTransport"/>, installed at the seam every
    ///     production `new ReleaseSource()` reads; it records every request and throws, so a command
    ///     that dispatches ends as exit 1 with its message, never as a quiet pass;
    ///   - the install root is a temp path that is NEVER created up front; the first thing a
    ///     dispatched command does is create `&lt;root&gt;\logs`, so "the root does not exist afterwards"
    ///     is the fact that nothing ran.
    /// Both instruments are fired at a known-bad input in
    /// <see cref="Run_PlanWithoutHelp_ReachesTheTransportAndOpensTheLog_SoBothInstrumentsAreLive"/>:
    /// the same double and root with a command that really dispatches, shown to trip both.
    ///
    /// What these tests CANNOT see: they run the compiled CLI assembly in the test host, not the
    /// published single-file exe, so the apphost/bundle layer is out of frame (the one behaviour
    /// that layer adds - re-exec from a temp copy - is reached only after the release is resolved,
    /// which is after the transport). And "no install" is shown through its two mandatory
    /// prerequisites (the release fetch and the log dir), not by a hook inside UpdateRunner, which
    /// has no seam.
    /// </summary>
    [Collection(ReleaseTransportSeamCollection.Name)]
    public sealed class SetupCliHelpTests : IDisposable
    {
        private readonly ForbiddenTransport _transport = new();
        private readonly string _root;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        public SetupCliHelpTests()
        {
            // Deliberately NOT created: its absence after a run is the assertion.
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-cli-help-" + Guid.NewGuid().ToString("N"));
            ReleaseSource.DefaultTransport = _transport;
        }

        public void Dispose()
        {
            ReleaseSource.DefaultTransport = null;
            EngineLog.Sink = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- 1. the parser ------------------------------------------------------

        [Theory]
        [InlineData("install --help")]
        [InlineData("install -h")]
        [InlineData("install -H")]
        [InlineData("update --help")]
        [InlineData("uninstall -h")]
        [InlineData("plan --help")]
        [InlineData("install --root C:\\x --no-finalize --help")]
        [InlineData("install --release-dir dist --json -h --no-finalize")]
        [InlineData("--help")]
        [InlineData("-h")]
        [InlineData("--help install")]
        public void Parse_HelpSwitchAnywhereOnTheLine_SetsWantsHelp(string line)
        {
            var args = CliArgs.Parse(Split(line));

            Assert.True(args.WantsHelp, line);
        }

        [Fact]
        public void Parse_WithoutTheSwitch_DoesNotWantHelp()
        {
            var args = CliArgs.Parse(Split("install --root C:\\x --json"));

            Assert.False(args.WantsHelp);
            Assert.Equal("install", args.Command);
        }

        [Theory]
        [InlineData("install --bogus", "--bogus")]
        [InlineData("install -x", "-x")]
        [InlineData("status --json=true", "--json=true")]
        [InlineData("components --bogus", "--bogus")]
        [InlineData("install --help --bogus", "--bogus")]   // help does not rescue a malformed line
        [InlineData("--bogus", "--bogus")]
        public void Parse_UnknownOption_ThrowsUsageException_NamingTheOption(string line, string offender)
        {
            var ex = Assert.Throws<CliUsageException>(() => CliArgs.Parse(Split(line)));

            Assert.Contains($"unknown option '{offender}'", ex.Message);
        }

        [Theory]
        [InlineData("install /?", "/?")]
        [InlineData("install /help", "/help")]
        [InlineData("install help", "help")]
        [InlineData("install extra --json", "extra")]
        [InlineData("install \u2013help", "\u2013help")]  // an en-dash (U+2013) pasted from a chat or a document: not '-', so a positional, not an option
        [InlineData("help install extra", "extra")]
        public void Parse_PositionalNoCommandTakes_ThrowsUsageException(string line, string offender)
        {
            // A token the command would have ignored while it RAN - "install /?" used to install.
            var ex = Assert.Throws<CliUsageException>(() => CliArgs.Parse(Split(line)));

            Assert.Contains($"unexpected argument '{offender}'", ex.Message);
        }

        [Fact]
        public void Parse_ToString_SummarizesTheLineForTheLog()
        {
            var args = CliArgs.Parse(Split("install --root D:/x --json --component app"));

            var s = args.ToString();

            Assert.Contains("command=install", s);
            Assert.Contains("json", s);
            Assert.Contains("root=D:/x", s);
            Assert.Contains("component=app", s);
        }

        [Theory]
        [InlineData("install --root")]
        [InlineData("install --root --json")]
        [InlineData("plan --manifest -h")]
        public void Parse_ValueOptionWithoutAValue_ThrowsUsageException(string line)
        {
            var ex = Assert.Throws<CliUsageException>(() => CliArgs.Parse(Split(line)));

            Assert.Contains("requires a value", ex.Message);
        }

        [Fact]
        public void Parse_TheReleaseWorkflowInvocation_StillParses()
        {
            // The exact shape .github/workflows/release.yml runs after every build - the one external
            // caller of this CLI. Closing the vocabulary must not reject it.
            var args = CliArgs.Parse(new[]
            {
                "install", "--release-dir", "dist/release", "--root", @"C:\t\agenteyes-install-test", "--no-finalize", "--json",
            });

            Assert.Equal("install", args.Command);
            Assert.Equal("dist/release", args.Option("release-dir"));
            Assert.Equal(@"C:\t\agenteyes-install-test", args.Option("root"));
            Assert.True(args.HasFlag("no-finalize"));
            Assert.True(args.HasFlag("json"));
            Assert.False(args.WantsHelp);
            Assert.Empty(args.Positionals);
        }

        [Theory]
        [InlineData("update")]
        [InlineData("uninstall")]
        public void Parse_TheRelaunchedReExecShape_StillParses(string command)
        {
            // Commands.RelaunchFromTempIfInsideInstall re-runs the same line plus --relaunched.
            var args = CliArgs.Parse(new[] { command, "--relaunched" });

            Assert.Equal(command, args.Command);
            Assert.True(args.HasFlag("relaunched"));
        }

        // ---- 2. the entry point: help ----------------------------------------------

        [Theory]
        [InlineData("install --help", "install")]
        [InlineData("install -h", "install")]
        [InlineData("update --help", "update")]
        [InlineData("update -h", "update")]
        [InlineData("uninstall --help", "uninstall")]
        [InlineData("uninstall -h", "uninstall")]
        [InlineData("plan --help", "plan")]
        [InlineData("plan -h", "plan")]
        [InlineData("components --help", "components")]
        [InlineData("components -h", "components")]
        [InlineData("status --help", "status")]
        [InlineData("status -h", "status")]
        [InlineData("install --no-finalize --json --help", "install")]   // the switch after other options
        [InlineData("update --component app -h", "update")]
        [InlineData("plan --manifest latest --help", "plan")]            // "latest" would be a fetch; help wins
        [InlineData("help install", "install")]
        [InlineData("--help uninstall", "uninstall")]
        public async Task Run_CommandHelp_PrintsThatCommandsPage_ExitsZero_AndTouchesNothing(string line, string command)
        {
            var exit = await Run(WithRoot(Split(line)));

            Assert.Equal(0, exit);
            Assert.Equal(CliHelp.ForCommand(command), _out.ToString());
            Assert.Contains($"Usage: agenteyes-setup {command} [options]", _out.ToString());
            Assert.Equal("", _err.ToString());
            AssertNothingWasTouched();
        }

        [Fact]
        public async Task InstallHelp_Issue83_PrintsHelpInsteadOfDownloadingTheLatestReleaseAndReplacingTheInstalledApp()
        {
            // The report, verbatim: exactly `install --help`, through the real entry point, with the
            // DEFAULT install root because the report had no --root. A repeat of the defect goes
            // install -> WireLogging(default root) -> resolve "latest" -> the transport, which throws:
            // exit 1 and the double's message on stderr, never a green test. Because the root is the
            // real one, the real setup-cli.log is pinned too: a regression would append to it before
            // the transport tripped, and that write must show up here, not only in the transport log.
            var defaultLog = Path.Combine(InstallLayout.Default().LogsDir, "setup-cli.log");
            var before = LogSnapshot(defaultLog);

            var exit = await Run("install", "--help");

            Assert.Equal(0, exit);
            Assert.Contains("Usage: agenteyes-setup install [options]", _out.ToString());
            Assert.Equal("", _err.ToString());
            Assert.Empty(_transport.Requests);
            Assert.Equal(before, LogSnapshot(defaultLog));
        }

        [Fact]
        public async Task Run_NoArguments_PrintsTheGeneralPage()
        {
            var exit = await Run();

            Assert.Equal(0, exit);
            Assert.Equal(CliHelp.General(), _out.ToString());
            Assert.Equal("", _err.ToString());
            Assert.Empty(_transport.Requests);
        }

        [Fact]
        public async Task Run_HelpCommand_PrintsTheGeneralPage()
        {
            var exit = await Run("help");

            Assert.Equal(0, exit);
            Assert.Equal(CliHelp.General(), _out.ToString());
        }

        // ---- 3. the entry point: usage errors ------------------------------------------

        [Fact]
        public async Task Run_InstallWithAnUnknownOption_ExitsWithTheUsageCode_AndDoesNothing()
        {
            var exit = await Run("install", "--bogus", "--root", _root);

            Assert.Equal(2, exit);
            Assert.Contains("usage error: unknown option '--bogus'", _err.ToString());
            Assert.Contains(CliHelp.UsageHint, _err.ToString());
            Assert.Equal("", _out.ToString());
            AssertNothingWasTouched();
        }

        [Fact]
        public async Task Run_InstallWithAWindowsStyleHelpToken_ExitsWithTheUsageCode_AndDoesNothing()
        {
            // "install /?" is not help in this CLI and it is not an option either; before this change it
            // was a positional nobody read, and the install ran.
            var exit = await Run("install", "/?", "--root", _root);

            Assert.Equal(2, exit);
            Assert.Contains("usage error: unexpected argument '/?'", _err.ToString());
            Assert.Equal("", _out.ToString());
            AssertNothingWasTouched();
        }

        [Fact]
        public async Task Run_UnknownCommand_ExitsWithTheUsageCode_BeforeTheInstallRootIsTouched()
        {
            var exit = await Run("frobnicate", "--root", _root);

            Assert.Equal(2, exit);
            Assert.Contains("unknown command: frobnicate", _err.ToString());
            Assert.Equal("", _out.ToString());
            AssertNothingWasTouched();
        }

        [Fact]
        public async Task Run_HelpForAnUnknownCommand_ExitsWithTheUsageCode()
        {
            var exit = await Run("help", "frobnicate", "--root", _root);

            Assert.Equal(2, exit);
            Assert.Contains("unknown command: frobnicate", _err.ToString());
            AssertNothingWasTouched();
        }

        // ---- 4. the known-bad arm: both instruments shown to fire --------------------------

        [Fact]
        public async Task Run_PlanWithoutHelp_ReachesTheTransportAndOpensTheLog_SoBothInstrumentsAreLive()
        {
            // The negative control for AssertNothingWasTouched. Same double, same never-created root, a
            // command that really dispatches. `plan` is the safe one: it resolves `--manifest latest`
            // FIRST (so it must ask the transport, which throws) and would apply nothing even if it
            // got a release. Wiring the log is the first thing a dispatched command does, so the
            // root's logs dir must now exist.
            var exit = await Run("plan", "--root", _root);
            // The dispatched command pointed the process-wide EngineLog.Sink at a file under _root. Other
            // test classes call EngineLog.Write in parallel; detach the sink now rather than in Dispose so
            // the window in which their lines land under a directory about to be deleted is as short as
            // the run itself. (Nothing in the suite installs its own sink - see the class comment.)
            EngineLog.Sink = null;

            Assert.Equal(1, exit);
            Assert.Equal(new[] { ReleaseSource.LatestReleaseUrl }, _transport.Requests.ToArray());
            Assert.Contains(ForbiddenTransport.Message, _err.ToString());
            Assert.True(Directory.Exists(Path.Combine(_root, "logs")), "a dispatched command must have created <root>\\logs");
        }

        // ---- 5. CliHelp --------------------------------------------------------------

        [Theory]
        [InlineData("install", true)]
        [InlineData("INSTALL", true)]
        [InlineData("plan", true)]
        [InlineData("help", false)]
        [InlineData("frobnicate", false)]
        [InlineData("", false)]
        public void IsCommand_KnowsExactlyTheDispatchedCommands(string name, bool expected)
        {
            Assert.Equal(expected, CliHelp.IsCommand(name));
        }

        [Fact]
        public void ForCommand_EveryCommand_ReturnsAPageHeadedByItsUsageLine()
        {
            foreach (var command in CliHelp.Commands)
            {
                var page = CliHelp.ForCommand(command);

                Assert.StartsWith($"Usage: agenteyes-setup {command} [options]", page, StringComparison.Ordinal);
                Assert.Contains("--help, -h", page);
                Assert.EndsWith(Environment.NewLine, page, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void ForCommand_IsCaseInsensitive()
        {
            Assert.Equal(CliHelp.ForCommand("install"), CliHelp.ForCommand("Install"));
        }

        [Fact]
        public void ForCommand_UnknownCommand_ThrowsUsageException()
        {
            var ex = Assert.Throws<CliUsageException>(() => CliHelp.ForCommand("frobnicate"));

            Assert.Contains("unknown command: frobnicate", ex.Message);
        }

        [Fact]
        public void General_ListsEveryCommandAndEveryOptionTheParserAccepts()
        {
            var page = CliHelp.General();

            foreach (var command in CliHelp.Commands)
                Assert.Matches(new Regex($@"^\s+{Regex.Escape(command)}\s", RegexOptions.Multiline), page);
            foreach (var option in CliArgs.KnownFlags.Concat(CliArgs.KnownOptions).Where(o => o != "relaunched"))
                Assert.Contains($"--{option}", page);
            // The short switch as its own token: "-h" is also a substring of "--help", so a bare Contains proves nothing.
            Assert.Matches(new Regex(@"^\s+--help, -h\s", RegexOptions.Multiline), page);
            Assert.Contains("exit code 2", page);
        }

        [Fact]
        public void HelpPages_DocumentOnlyOptionsTheParserAccepts()
        {
            // The two-way pin between the text and the parser: an option a page promises that
            // CliArgs would reject as unknown is a defect in whichever side changed.
            var accepted = new HashSet<string>(CliArgs.KnownFlags.Concat(CliArgs.KnownOptions), StringComparer.OrdinalIgnoreCase);
            var pages = CliHelp.Commands.Select(CliHelp.ForCommand).Append(CliHelp.General()).ToList();
            Assert.Equal(CliHelp.Commands.Count + 1, pages.Count);

            var documented = pages
                .SelectMany(p => Regex.Matches(p, @"--([a-z][a-z-]*)").Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.NotEmpty(documented);
            Assert.Empty(documented.Where(o => !accepted.Contains(o)));
        }

        // ---- helpers ----------------------------------------------------------------

        private Task<int> Run(params string[] argv) => CliProgram.RunAsync(argv, _out, _err);

        private string[] WithRoot(string[] argv) => argv.Concat(new[] { "--root", _root }).ToArray();

        private static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Existence and length of a log file - enough to see one appended line.</summary>
        private static (bool exists, long length) LogSnapshot(string path) =>
            File.Exists(path) ? (true, new FileInfo(path).Length) : (false, 0L);

        /// <summary>
        /// Nothing dispatched: the release channel saw no request and the install root (the first
        /// thing a dispatched command creates) does not exist. Both are absences; the known-bad arm
        /// in section 4 shows each of them present when a command really runs.
        /// </summary>
        private void AssertNothingWasTouched()
        {
            Assert.Empty(_transport.Requests);
            Assert.False(Directory.Exists(_root), $"the install root {_root} was created - something dispatched");
        }

        /// <summary>
        /// The release channel that must never be asked. Records the request, then throws, so a
        /// command that resolves "latest" fails loudly instead of quietly reaching GitHub.
        /// </summary>
        private sealed class ForbiddenTransport : HttpMessageHandler
        {
            public const string Message = "issue #83 instrument: the setup CLI reached the release channel";

            public List<string> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                lock (Requests) Requests.Add(url);
                throw new InvalidOperationException($"{Message}: {request.Method} {url}");
            }
        }
    }
}
