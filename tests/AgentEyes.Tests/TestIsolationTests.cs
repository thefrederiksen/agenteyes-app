using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AgentEyes.AlwaysOn;
using AgentEyes.Packaging;
using AgentEyes.Preview;
using AgentEyes.Setup.Engine;
using AgentEyes.Transcription;
using Whisper.net.Ggml;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// NEGATIVE CONTROLS for the folder-lookup guard below (issue #78). Never called. They exist to be
    /// COMPILED into this assembly so the guard can be run over real IL and shown to report both
    /// shapes - a guard that has never been seen to fire has demonstrated nothing.
    /// </summary>
    internal static class RealFolderDecoys
    {
        /// <summary>The direct shape: asks Windows for the real %LOCALAPPDATA%.</summary>
        internal static string AsksForTheRealLocalAppData() =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>The indirect shape: the folder arrives in a variable, so no constant names it.</summary>
        internal static string AsksWithAFolderInAVariable(Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder);
    }

    /// <summary>
    /// Runs <see cref="TestIsolationTests"/> ALONE, after the parallel part of the suite. Its IL
    /// inventories read four whole assemblies, and run in parallel with everything else that CPU load
    /// starved the thread pool enough to push the timing-sensitive CameraPreviewTests past their 5s
    /// waits (7 of 9 full runs failed with it in parallel; 3 of 3 passed without it).
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class TestIsolationCollection
    {
        public const string Name = "Test isolation guards (run alone)";
    }

    /// <summary>
    /// Issue #78: a test run never writes to - or reads from - the machine's REAL AgentEyes state
    /// (%LOCALAPPDATA%\AgentEyes and Videos\AgentEyes), and every log line names its process.
    ///
    /// HOW IT IS ENFORCED. <see cref="TestRunIsolation"/> redirects <see cref="AppDataPaths"/> before
    /// any test runs; every product path to per-user state is built from <see cref="AppDataPaths"/>.
    ///
    /// WHAT THESE TESTS CLAIM, exactly:
    ///  - In this test host, the log and every named state location (config, presets, plugins,
    ///    always-on work + clips, preview, models, dictionary, DevThrottle credential, recordings, the
    ///    setup engine's install root) resolve inside this run's folder and NOT inside the real one.
    ///  - By the compiled IL of AgentEyes.Core, AgentEyesApp, AgentEyes.Setup.Engine AND this test
    ///    assembly, the only methods that ask Windows for a folder via Environment.GetFolderPath are the
    ///    pinned ones below - so a new direct lookup of %LOCALAPPDATA% or Videos anywhere in those
    ///    assemblies (a new file, a helper, a lambda, a test) changes the inventory and fails.
    ///  - No product assembly reads LOCALAPPDATA / APPDATA / USERPROFILE with GetEnvironmentVariable
    ///    (every read is pinned by the variable it names), none calls ExpandEnvironmentVariables, and the
    ///    one known-folder P/Invoke (SHGetKnownFolderPath) is pinned - the other ways to find the folder.
    ///    That one import is CaptureService resolving the Windows Screenshots folder; tests only
    ///    resolve that path, they never save a snip into it.
    ///
    /// WHAT THEY DO NOT CLAIM (the limits, stated so this guard is not believed to cover more):
    ///  - Reflection (typeof(Environment).GetMethod("GetFolderPath").Invoke) is not seen by any IL scan.
    ///  - A literal ASSEMBLED at run time ("LOCAL" + "APPDATA") or a hard-coded "C:\Users\..." path is
    ///    not seen.
    ///  - Path.GetTempPath() is %LOCALAPPDATA%\Temp on a default Windows profile. Temp is not AgentEyes
    ///    state and is where every test's scratch folder lives; only %LOCALAPPDATA%\AgentEyes is guarded.
    ///  - Out-of-process children (ffmpeg, powershell) are not scanned; none of them is given an
    ///    AgentEyes state path by a test.
    ///  - The setup WIZARD and setup CLI (AgentEyes.Setup, AgentEyes.Setup.Cli) are not referenced by
    ///    this test project, are not run by it, and are not scanned.
    ///  - Read-only probes of machine state that is not AgentEyes' own data - installed devices,
    ///    monitors, the Start Menu / Desktop shortcut paths and the Run key that the uninstall PLAN
    ///    only checks for existence - are outside this criterion.
    /// </summary>
    [Collection(TestIsolationCollection.Name)]
    public sealed class TestIsolationTests
    {
        private static string RealAgentEyesRoot =>
            Path.Combine(AppDataPaths.MachineLocalAppData, AppDataPaths.ProductFolder);

        private static bool IsUnder(string path, string root)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            string parent = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        // ---- criterion 1: the log -------------------------------------------------------------

        [Fact]
        public void LogFile_InATestRun_IsInThisRunsFolderAndNotTheRealLog()
        {
            Assert.True(AppDataPaths.IsRedirected, "the test host did not redirect AgentEyes' data folders");
            Assert.True(IsUnder(TestRunIsolation.RunRoot, Path.GetTempPath()), $"run folder {TestRunIsolation.RunRoot} is not under %TEMP%");

            string expectedDir = Path.Combine(TestRunIsolation.LocalAppData, "AgentEyes", "logs");
            Assert.Equal(expectedDir, Log.Dir);
            Assert.True(IsUnder(Log.CurrentFile, TestRunIsolation.RunRoot), $"log {Log.CurrentFile} is outside the run folder");
            Assert.False(IsUnder(Log.CurrentFile, RealAgentEyesRoot), $"log {Log.CurrentFile} is the REAL AgentEyes log");
            Assert.NotEqual(Path.Combine(RealAgentEyesRoot, "logs"), Log.Dir, StringComparer.OrdinalIgnoreCase);

            // And a line said now really lands in that file - the path is not just a string.
            string marker = "[TestIsolationTests] marker " + Guid.NewGuid().ToString("N");
            Log.Info(marker);
            Assert.Contains(marker, ReadShared(Log.CurrentFile));
        }

        // ---- criterion 3: the process id on every line ----------------------------------------

        [Fact]
        public void Log_EachLineWritten_CarriesTheWritingProcessId()
        {
            string marker = "[TestIsolationTests] pid marker " + Guid.NewGuid().ToString("N");
            Log.Warn(marker);

            string line = ReadShared(Log.CurrentFile)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Single(l => l.EndsWith(marker, StringComparison.Ordinal));
            var m = Regex.Match(line, @"^\d\d:\d\d:\d\d\.\d{3} \[pid (\d+)\] \[WARN\] ");
            Assert.True(m.Success, $"the line does not have the [pid N] shape: {line}");
            Assert.Equal(Environment.ProcessId, int.Parse(m.Groups[1].Value));
        }

        [Fact]
        public void FormatLine_TwoProcesses_AreToldApart()
        {
            var at = new DateTime(2026, 9, 24, 9, 3, 1, 250);

            string app = Log.FormatLine(at, 1111, "INFO", "[AlwaysOnEngine] kept a clip");
            string tests = Log.FormatLine(at, 2222, "INFO", "[AlwaysOnEngine] kept a clip");

            Assert.Equal("09:03:01.250 [pid 1111] [INFO] [AlwaysOnEngine] kept a clip", app);
            Assert.Equal("09:03:01.250 [pid 2222] [INFO] [AlwaysOnEngine] kept a clip", tests);
            Assert.NotEqual(app, tests);
        }

        [Fact]
        public void FormatLine_MultilineMessage_PutsThePidOnEveryLine()
        {
            var at = new DateTime(2026, 9, 24, 9, 3, 1, 250);
            string nl = Environment.NewLine;

            string text = Log.FormatLine(at, 1111, "ERROR", "first\r\nsecond\nthird\rfourth");

            Assert.Equal(
                "09:03:01.250 [pid 1111] [ERROR] first" + nl
                + "09:03:01.250 [pid 1111] [ERROR] second" + nl
                + "09:03:01.250 [pid 1111] [ERROR] third" + nl
                + "09:03:01.250 [pid 1111] [ERROR] fourth", text);
        }

        [Fact]
        public void Log_ErrorWithAnException_EveryLineOfTheStackTraceCarriesThePid()
        {
            string marker = "[TestIsolationTests] multiline marker " + Guid.NewGuid().ToString("N");
            Exception ex;
            try { throw new InvalidOperationException("inner " + marker); }
            catch (InvalidOperationException caught) { ex = caught; }

            Log.Error(marker, ex);

            // The entry's physical lines: the message line, the exception line, then each "   at ..."
            // frame. Taken WITHOUT requiring the pid, so a continuation line that lost it is still
            // collected - and then reported below.
            var lines = ReadShared(Log.CurrentFile).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int first = Array.FindIndex(lines, l => l.EndsWith("[ERROR] " + marker, StringComparison.Ordinal));
            Assert.True(first >= 0, "the entry's first line was not found in the log");
            var entry = lines.Skip(first).TakeWhile((l, i) => i < 2 || l.Contains("   at ", StringComparison.Ordinal)).ToList();
            Assert.True(entry.Count >= 3, "expected the message, the exception and at least one stack frame:"
                + Environment.NewLine + string.Join(Environment.NewLine, entry));
            Assert.Contains(entry, l => l.Contains("InvalidOperationException: inner " + marker, StringComparison.Ordinal));

            string pidPrefix = $@"^\d\d:\d\d:\d\d\.\d{{3}} \[pid {Environment.ProcessId}\] \[ERROR\] ";
            var bare = entry.Where(l => !Regex.IsMatch(l, pidPrefix)).ToList();
            Assert.True(bare.Count == 0, "these lines of the entry do not carry the pid:" + Environment.NewLine + string.Join(Environment.NewLine, bare));
        }

        // ---- criterion 2: every state location -----------------------------------------------

        [Fact]
        public void EveryStateLocation_InATestRun_IsInThisRunsFolderAndNotTheRealOne()
        {
            var locations = new List<(string Name, string Path)>
            {
                ("AppDataPaths.Root", AppDataPaths.Root),
                ("AppDataPaths.RecordingsRoot", AppDataPaths.RecordingsRoot),
                ("Log.Dir", Log.Dir),
                ("RecordingPaths.Root", RecordingPaths.Root),
                ("AlwaysOnOptions.DefaultWorkFolder (alwayson, today.json)", AlwaysOnOptions.DefaultWorkFolder),
                ("AlwaysOnOptions.DefaultClipsFolder", AlwaysOnOptions.DefaultClipsFolder),
                ("new AlwaysOnOptions().StatsFile", new AlwaysOnOptions().StatsFile),
                ("PreviewPaths.Dir", PreviewPaths.Dir),
                ("DictionaryStore.DefaultPath", DictionaryStore.DefaultPath),
                ("ModelStore.PathFor(Base)", ModelStore.PathFor(GgmlType.Base)),
                ("App Plugins.Root", AgentEyes.App.Plugins.Root),
                ("InstallLayout.Default().LocalRoot", InstallLayout.Default().LocalRoot),
                ("App Config.FilePath (config.json)", PrivateStaticPath(typeof(AgentEyes.App.Config), "FilePath")),
                ("App PresetStore.FilePath (presets.json)", PrivateStaticPath(typeof(AgentEyes.App.PresetStore), "FilePath")),
                ("LocalAppConfig.FilePath (config.json)", PrivateStaticPath(typeof(LocalAppConfig), "FilePath")),
                ("DevThrottleAccount.CredPath", PrivateStaticPath(typeof(AgentEyes.DevThrottle.DevThrottleAccount), "CredPath")),
            };

            var outside = locations
                .Where(l => !IsUnder(l.Path, TestRunIsolation.RunRoot) || IsUnder(l.Path, RealAgentEyesRoot))
                .Select(l => $"{l.Name} = {l.Path}")
                .ToList();

            Assert.True(outside.Count == 0,
                "these AgentEyes state locations escape the test run's folder:" + Environment.NewLine
                + string.Join(Environment.NewLine, outside));
        }

        [Fact]
        public void InstallFinalizer_InATestRun_WritesTheUserEnvironmentInMemoryNotTheRegistry()
        {
            Assert.Same(TestRunIsolation.UserEnvironment, InstallFinalizer.UserEnvironment);
            Assert.IsNotType<RegistryUserEnvironment>(InstallFinalizer.UserEnvironment);
        }

        // ---- the compiled-code guard -----------------------------------------------------------

        /// <summary>
        /// Every Environment.GetFolderPath call in the guarded assemblies, by method, folder and count.
        /// Adding one means adding a line here, deliberately - that is the review moment.
        /// </summary>
        private static readonly string[] PinnedFolderLookups =
        {
            // The ONE place per-user AgentEyes state is found. Redirected in a test host.
            "agenteyes.dll!AgentEyes.AppDataPaths::get_MachineLocalAppData -> LocalApplicationData x1",
            "agenteyes.dll!AgentEyes.AppDataPaths::get_Videos -> MyVideos x1",
            // The install root: honors AGENTEYES_ROOT first, which the test host sets for its own process.
            "AgentEyes.Setup.Engine.dll!AgentEyes.Setup.Engine.InstallLayout::Default -> LocalApplicationData x1",
            // Shortcut PATHS. Tests only read them (the uninstall plan checks existence); none creates one.
            "AgentEyes.Setup.Engine.dll!AgentEyes.Setup.Engine.InstallFinalizer::DesktopShortcutPath -> DesktopDirectory x1",
            "AgentEyes.Setup.Engine.dll!AgentEyes.Setup.Engine.InstallFinalizer::StartMenuShortcutPath -> StartMenu x1",
            // Where Windows PowerShell lives, for the plugin packaging test. Tool locations, not state.
            "AgentEyes.Tests.dll!AgentEyes.Tests.PluginRegistryChannelTests::WindowsPowerShellHome -> System x1",
            "AgentEyes.Tests.dll!AgentEyes.Tests.PluginRegistryChannelTests::WindowsPowerShellModulePath -> ProgramFiles x1",
            // The negative controls above - compiled in, never called.
            "AgentEyes.Tests.dll!AgentEyes.Tests.RealFolderDecoys::AsksForTheRealLocalAppData -> LocalApplicationData x1",
            "AgentEyes.Tests.dll!AgentEyes.Tests.RealFolderDecoys::AsksWithAFolderInAVariable -> <not a constant> x1",
        };

        /// <summary>Every Environment.GetEnvironmentVariable read in the product, by the variable it names.</summary>
        private static readonly string[] PinnedEnvironmentReads =
        {
            "AgentEyes.Setup.Engine.dll!AgentEyes.Setup.Engine.InstallLayout::Default -> AGENTEYES_ROOT x1",
            // The installer's user-variable store (PATH, DOTNET_BUNDLE_EXTRACT_BASE_DIR) - swapped for an
            // in-memory one in a test host, which InstallFinalizer_InATestRun_... asserts.
            "AgentEyes.Setup.Engine.dll!AgentEyes.Setup.Engine.RegistryUserEnvironment::Get -> <not a literal> x1",
            "AgentEyesApp.dll!AgentEyes.App.HudWindow::ApplyWindowStyles -> MQS_HUD_CAPTURABLE x1",
            "agenteyes.dll!AgentEyes.Video.FfmpegLocator::Find -> PATH x1",
            "agenteyes.dll!AgentEyes.Video.FfmpegLocator::Find -> QA_RECORD_FFMPEG_DIR x1",
        };

        private static IReadOnlyList<string> GuardedAssemblies() => new[]
        {
            CompiledCode.CoreAssembly, CompiledCode.AppAssembly, CompiledCode.EngineAssembly, CompiledCode.TestAssembly,
        };

        [Fact]
        public void FolderLookups_InTheCompiledProductAndTests_AreExactlyThePinnedOnes()
        {
            var lookups = GuardedAssemblies().SelectMany(CompiledCode.FolderLookups).ToList();

            string expected = string.Join(Environment.NewLine, PinnedFolderLookups.OrderBy(s => s, StringComparer.Ordinal));
            string actual = CompiledCode.Describe(lookups);
            Assert.True(expected == actual,
                "the folder lookups in the compiled code changed. Actual inventory:" + Environment.NewLine + actual);
        }

        [Fact]
        public void FolderLookupScanner_OnTheCompiledDecoys_ReportsBothShapes()
        {
            // The instrument check: the scan must SEE a direct lookup and a variable one, or its
            // "only the pinned ones" above would be the answer of a scan that saw nothing.
            var decoys = CompiledCode.FolderLookups(CompiledCode.TestAssembly)
                .Where(l => l.Method.StartsWith("AgentEyes.Tests.RealFolderDecoys::", StringComparison.Ordinal))
                .Select(l => $"{l.Method} -> {l.Argument}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[]
            {
                "AgentEyes.Tests.RealFolderDecoys::AsksForTheRealLocalAppData -> LocalApplicationData",
                "AgentEyes.Tests.RealFolderDecoys::AsksWithAFolderInAVariable -> <not a constant>",
            }, decoys);
        }

        /// <summary>
        /// Every reader of <see cref="AppDataPaths.MachineLocalAppData"/> - the one member that is NEVER
        /// redirected. The folder-lookup guard above pins only that member's own GetFolderPath call, so
        /// without this list a new caller could build a path to the real %LOCALAPPDATA%\AgentEyes from
        /// it and pass every other check.
        /// </summary>
        private static readonly string[] PinnedMachineLocalAppDataReaders =
        {
            // The redirectable root falls back to it when no redirect is set - the normal product path.
            "agenteyes.dll!AgentEyes.AppDataPaths::get_LocalAppData -> AgentEyes.AppDataPaths::get_MachineLocalAppData x1",
            // The winget package folder ffmpeg may be installed in: a tool location, not AgentEyes state.
            "agenteyes.dll!AgentEyes.Video.FfmpegLocator::Find -> AgentEyes.AppDataPaths::get_MachineLocalAppData x1",
            // This class, naming the real folder it asserts the test run stays out of.
            "AgentEyes.Tests.dll!AgentEyes.Tests.TestIsolationTests::get_RealAgentEyesRoot -> AgentEyes.AppDataPaths::get_MachineLocalAppData x1",
        };

        [Fact]
        public void MachineLocalAppDataReaders_InTheCompiledProductAndTests_AreExactlyThePinnedOnes()
        {
            var readers = GuardedAssemblies()
                .SelectMany(a => CompiledCode.CallSites(a, c => c == "AgentEyes.AppDataPaths::get_MachineLocalAppData"))
                .ToList();

            // Instrument check: the scan must see the product's own fallback read, or "exactly the
            // pinned ones" could be the answer of a scan that matched nothing.
            Assert.Contains(readers, r => r.Method == "AgentEyes.AppDataPaths::get_LocalAppData");

            string expected = string.Join(Environment.NewLine, PinnedMachineLocalAppDataReaders.OrderBy(s => s, StringComparer.Ordinal));
            string actual = CompiledCode.Describe(readers);
            Assert.True(expected == actual,
                "the readers of the never-redirected AppDataPaths.MachineLocalAppData changed. Actual inventory:"
                + Environment.NewLine + actual);
        }

        [Fact]
        public void EnvironmentVariableRoutes_ToTheUserFolders_AreNotInTheProduct()
        {
            string[] names = { "LOCALAPPDATA", "APPDATA", "USERPROFILE" };
            var product = new[] { CompiledCode.CoreAssembly, CompiledCode.AppAssembly, CompiledCode.EngineAssembly };

            // Instrument check: the literal scan reads real strings in each assembly.
            foreach (string assembly in product)
                Assert.True(CompiledCode.StringLiteralCount(assembly) > 0, $"no string literals read from {assembly}");

            // GetEnvironmentVariable: every read, by the variable it names. None names a user folder.
            string reads = CompiledCode.Describe(product.SelectMany(CompiledCode.EnvironmentVariableReads));
            string pinnedReads = string.Join(Environment.NewLine, PinnedEnvironmentReads.OrderBy(x => x, StringComparer.Ordinal));
            Assert.True(pinnedReads == reads,
                "the environment-variable reads in the product changed. Actual inventory:" + Environment.NewLine + reads);
            foreach (string name in names)
                Assert.DoesNotContain(" -> " + name + " x", reads, StringComparison.OrdinalIgnoreCase);

            // ExpandEnvironmentVariables("%LOCALAPPDATA%\..."): never called at all.
            var expands = product.SelectMany(a => CompiledCode.CallSites(a,
                c => c == "System.Environment::ExpandEnvironmentVariables")).ToList();
            Assert.True(expands.Count == 0, "the product expands environment variables:" + Environment.NewLine + CompiledCode.Describe(expands));

            var imports = product
                .SelectMany(CompiledCode.NativeImports)
                .Where(i => i.Contains("SHGetKnownFolderPath", StringComparison.OrdinalIgnoreCase)
                         || i.Contains("SHGetFolderPath", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // The one import is CaptureService's, and it asks for FOLDERID_Screenshots (the Windows
            // Screenshots folder snips are saved to) - not AgentEyes state. A second import, or one in
            // another assembly, changes this list.
            Assert.Equal(new[] { "shell32.dll!SHGetKnownFolderPath" }, imports);
        }

        // ---- the redirect's own contract ---------------------------------------------------------

        [Fact]
        public void RedirectForThisProcess_WhenAlreadyRedirected_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                AppDataPaths.RedirectForThisProcess(Path.Combine(Path.GetTempPath(), "x"), Path.Combine(Path.GetTempPath(), "y")));
            Assert.Contains("already redirected", ex.Message);
            Assert.True(IsUnder(AppDataPaths.Root, TestRunIsolation.RunRoot));   // unchanged
        }

        [Fact]
        public void RedirectForThisProcess_RelativePath_Throws()
        {
            Assert.Throws<ArgumentException>(() => AppDataPaths.RedirectForThisProcess(@"relative\local", Path.GetTempPath()));
            Assert.Throws<ArgumentException>(() => AppDataPaths.RedirectForThisProcess(Path.GetTempPath(), ""));
        }

        // ---- the run-folder prune ------------------------------------------------------------------

        [Fact]
        public void PruneOldRuns_DeletesOnlyOldFoldersThisHostNamed()
        {
            string parent = Path.Combine(Path.GetTempPath(), "agenteyes-prune-test-" + Guid.NewGuid().ToString("N"));
            var now = new DateTime(2026, 9, 24, 12, 0, 0);
            string old = Directory.CreateDirectory(Path.Combine(parent, "run-20260920-080000-4242")).FullName;
            string recent = Directory.CreateDirectory(Path.Combine(parent, "run-20260924-110000-4243")).FullName;
            string notOurs = Directory.CreateDirectory(Path.Combine(parent, "run-somebody-elses")).FullName;
            string badPid = Directory.CreateDirectory(Path.Combine(parent, "run-20260920-080000-abc")).FullName;
            string other = Directory.CreateDirectory(Path.Combine(parent, "keep-me")).FullName;
            File.WriteAllText(Path.Combine(old, "AgentEyes.log"), "old run");
            try
            {
                var deleted = TestRunIsolation.PruneOldRuns(parent, now, TimeSpan.FromDays(2));

                Assert.Equal(new[] { old }, deleted);
                Assert.False(Directory.Exists(old));
                Assert.True(Directory.Exists(recent));
                Assert.True(Directory.Exists(notOurs));
                Assert.True(Directory.Exists(badPid));
                Assert.True(Directory.Exists(other));
            }
            finally
            {
                Directory.Delete(parent, recursive: true);
            }
        }

        [Fact]
        public void RunStamp_ParsesOnlyTheRunFolderShape()
        {
            Assert.Equal(new DateTime(2026, 9, 24, 9, 3, 1), TestRunIsolation.RunStamp("run-20260924-090301-1234"));
            Assert.Null(TestRunIsolation.RunStamp("run-20260924-090301-"));
            Assert.Null(TestRunIsolation.RunStamp("run-20260924-090301"));
            Assert.Null(TestRunIsolation.RunStamp("run-2026092x-090301-1"));
            Assert.Null(TestRunIsolation.RunStamp("walk-20260924-090301-1"));
            Assert.Null(TestRunIsolation.RunStamp(""));
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>A private static string property, read by reflection. Throws when it does not
        /// exist, so a rename cannot turn this into a check of nothing.</summary>
        private static string PrivateStaticPath(Type type, string property)
        {
            var p = type.GetProperty(property, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException($"{type.FullName}.{property} does not exist - update this guard");
            return (string?)p.GetValue(null)
                ?? throw new InvalidOperationException($"{type.FullName}.{property} returned null");
        }
    }
}
