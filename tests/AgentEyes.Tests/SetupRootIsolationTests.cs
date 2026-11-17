using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;
using CliCommands = AgentEyes.Setup.Cli.Commands;
using CliProgram = AgentEyes.Setup.Cli.Program;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #200: <c>agenteyes-setup --root &lt;scratch&gt;</c> - the option whose whole purpose is to install
    /// somewhere other than the real location - found the person's REAL AgentEyes by process name, stopped it,
    /// and started the scratch copy in its place (measured on the developer's Windows laptop, 16 and 17 November 2026).
    ///
    /// These tests run the CLI's real entry point with --root and assert that nothing outside the root is
    /// reached. The running-app and launcher seams are replaced by SPIES that report a running AgentEyes
    /// OUTSIDE the root - the exact situation of the incident - so an isolated install that consults them at
    /// all, or stops or starts anything, fails here instead of stopping a real recorder. The spies are also
    /// what makes a regression safe to run: it fails these tests rather than stopping the developer's own app.
    /// The shortcuts, the run-at-login value and the Add/Remove Programs entry are equally out of reach: the
    /// test run swaps <see cref="InstallFinalizer.UserShell"/> for an in-memory store, so a regression that
    /// writes or deletes them changes that store and fails here, and the real ones are never touched.
    /// </summary>
    [Collection(ReleaseTransportSeamCollection.Name)]
    public sealed class SetupRootIsolationTests : IDisposable
    {
        private readonly string _temp;
        private readonly string _releaseDir;
        private readonly string _root;
        private readonly SpyApp _spy = new();
        private readonly SpyLauncher _launcher = new();
        private readonly Func<InstallLayout, IRunningAppHandle> _handleBefore;
        private readonly Func<InstallLayout, IAppLauncher> _launcherBefore;
        private readonly string? _pathBefore;
        private readonly string? _bundleVarBefore;

        public SetupRootIsolationTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-root-isolation-" + Guid.NewGuid().ToString("N"));
            _releaseDir = Path.Combine(_temp, "release");
            _root = Path.Combine(_temp, "scratch-root");
            Directory.CreateDirectory(_releaseDir);
            WriteRelease("9.9.9");

            // A running AgentEyes OUTSIDE the root - the person's real install, as in the incident.
            _spy.Instance = new RunningAppInstance(19972, Path.Combine(_temp, "real", "app", "AgentEyesApp.exe"), new[] { "--tray" });

            _handleBefore = CliCommands.RunningAppHandleFactory;
            _launcherBefore = CliCommands.AppLauncherFactory;
            CliCommands.RunningAppHandleFactory = _ => _spy;
            CliCommands.AppLauncherFactory = _ => _launcher;

            TestRunIsolation.UserShell.Seed(shortcuts: false, runValue: null, entry: null);
            _pathBefore = TestRunIsolation.UserEnvironment.Get("Path");
            _bundleVarBefore = TestRunIsolation.UserEnvironment.Get(InstallFinalizer.BundleExtractBaseDirVariable);
        }

        public void Dispose()
        {
            CliCommands.RunningAppHandleFactory = _handleBefore;
            CliCommands.AppLauncherFactory = _launcherBefore;
            TestRunIsolation.UserEnvironment.Set("Path", _pathBefore);
            TestRunIsolation.UserEnvironment.Set(InstallFinalizer.BundleExtractBaseDirVariable, _bundleVarBefore);
            TestRunIsolation.UserShell.Seed(shortcuts: false, runValue: null, entry: null);
            EngineLog.Sink = null;
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }

        // ---- the incident -----------------------------------------------------------------------

        [Fact]
        public async Task InstallWithRoot_AnAppRunsOutsideTheRoot_IsNeverLookedUpStoppedOrStarted()
        {
            var (exit, stdout, stderr) = await Run("install", "--release-dir", _releaseDir, "--root", _root, "--no-finalize");

            Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}; stdout: {stdout}");
            Assert.Equal(0, _spy.FindCalls);
            Assert.Equal(0, _spy.StopCalls);
            Assert.Empty(_launcher.Launches);
            Assert.Contains(CliCommands.IsolatedRestartLine, stdout);
            Assert.DoesNotContain("restarted the running app", stdout);
            Assert.Empty(TestRunIsolation.UserShell.Writes);
            // The install itself happened, inside the root.
            Assert.Equal("app build 9.9.9", File.ReadAllText(Path.Combine(_root, "app", "AgentEyesApp.exe")));
            Assert.True(File.Exists(Path.Combine(_root, "app", "ffmpeg.exe")));
        }

        [Fact]
        public async Task UpdateWithRoot_AnAppRunsOutsideTheRoot_IsNeverLookedUpStoppedOrStarted()
        {
            var (exit, stdout, stderr) = await Run("update", "--release-dir", _releaseDir, "--root", _root, "--no-finalize", "--json");

            Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}; stdout: {stdout}");
            Assert.Equal(0, _spy.FindCalls);
            Assert.Equal(0, _spy.StopCalls);
            Assert.Empty(_launcher.Launches);
            Assert.Contains("\"restarted\": false", stdout);
            Assert.Empty(TestRunIsolation.UserShell.Writes);
        }

        [Fact]
        public async Task InstallWithRoot_StagesEveryDownloadAndExtractionInsideTheRoot()
        {
            var (exit, _, stderr) = await Run("install", "--release-dir", _releaseDir, "--root", _root, "--no-finalize");
            Assert.True(exit == 0, stderr);

            // The CLI logs into the root; read the paths it staged to. A PRESENCE is asserted (two downloads
            // and one extraction), so a log that recorded nothing cannot pass as "nothing outside the root".
            string log = File.ReadAllText(Path.Combine(_root, "logs", "setup-cli.log"));
            var staged = Lines(log, "[ReleaseSource] DownloadAssetAsync: ", " -> ");
            var unpacked = Lines(log, "[ArchiveInstaller] Place: ", "(unpacking in ");
            Assert.Equal(2, staged.Count);
            Assert.Single(unpacked);
            var layout = InstallLayout.Isolated(_root);
            foreach (var path in staged.Concat(unpacked.Select(u => u.TrimEnd(')'))))
                Assert.True(layout.Contains(path), $"staged outside the root: {path}");
            Assert.Empty(TestRunIsolation.UserShell.Writes);
        }

        // ---- refusals: steps that cannot be scoped to a root -------------------------------------

        // The refusal tests below pass a release dir that does NOT exist. The refusal comes first, so a correct
        // build never reads it; a build whose refusal has regressed fails reading it - before the finalizer. That
        // was once the ONLY protection: a mutation run of this test with a real release dir repointed the real
        // Start Menu shortcut and Add/Remove Programs entry at a test folder. Since the #200 QA bounce the finalizer
        // reaches those only through InstallFinalizer.UserShell, which this test run keeps in memory, and every
        // test here asserts that store saw no write.

        [Fact]
        public async Task InstallWithRoot_WithoutNoFinalize_IsRefusedBeforeAnythingIsTouched()
        {
            var (exit, _, stderr) = await Run("install", "--release-dir", MissingReleaseDir, "--root", _root);

            Assert.Equal(2, exit);
            Assert.Contains("needs --no-finalize", stderr);
            Assert.False(Directory.Exists(Path.Combine(_root, "app")), "nothing may be installed when the command is refused");
            Assert.Equal(0, _spy.FindCalls);
            Assert.Empty(_launcher.Launches);
            Assert.Equal(_pathBefore, TestRunIsolation.UserEnvironment.Get("Path"));
            Assert.Equal(_bundleVarBefore, TestRunIsolation.UserEnvironment.Get(InstallFinalizer.BundleExtractBaseDirVariable));
            Assert.Empty(TestRunIsolation.UserShell.Writes);
        }

        [Theory]
        [InlineData("--autostart", "on")]
        [InlineData("--autostart", "off")]
        [InlineData("--desktop-shortcut", null)]
        public async Task InstallWithRoot_AskingForSomethingOutsideTheRoot_IsRefused(string option, string? value)
        {
            var argv = new List<string> { "install", "--release-dir", MissingReleaseDir, "--root", _root, "--no-finalize", option };
            if (value != null) argv.Add(value);

            var (exit, _, stderr) = await Run(argv.ToArray());

            Assert.Equal(2, exit);
            Assert.Contains($"{option} cannot be used with --root", stderr);
            Assert.False(Directory.Exists(Path.Combine(_root, "app")));
            Assert.Empty(TestRunIsolation.UserShell.Writes);
        }

        [Fact]
        public async Task UpdateWithRoot_DryRun_NeedsNoNoFinalize_AndTouchesNothing()
        {
            var (exit, stdout, stderr) = await Run("update", "--release-dir", _releaseDir, "--root", _root, "--dry-run");

            Assert.True(exit == 0, stderr);
            Assert.Contains("Plan:", stdout);
            Assert.False(Directory.Exists(Path.Combine(_root, "app")));
            Assert.Equal(0, _spy.FindCalls);
            Assert.Empty(TestRunIsolation.UserShell.Writes);
        }

        [Fact]
        public async Task InstallWithRoot_InnoInstallInTheRoot_IsRefused_AndLeftInPlace()
        {
            string marker = Path.Combine(_root, "app", "unins000.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "inno");

            var (exit, _, stderr) = await Run("install", "--release-dir", _releaseDir, "--root", _root, "--no-finalize");

            Assert.Equal(1, exit);
            Assert.Contains("Inno", stderr);
            Assert.True(File.Exists(marker), "the Inno install must be left exactly as it was");
            Assert.Empty(TestRunIsolation.UserShell.Writes);
            Assert.False(File.Exists(Path.Combine(_root, "app", "AgentEyesApp.exe")));
        }

        /// <summary>
        /// This drives the REAL <see cref="Uninstaller.Apply"/>. It is safe to run against a regressed build
        /// (QA, issue #200 bounce) because the shortcuts, the run-at-login value and the Add/Remove Programs
        /// entry are reached only through <see cref="InstallFinalizer.UserShell"/>, which the test run swaps
        /// for an in-memory store before any test runs. The test asserts that swap FIRST, so a run where it
        /// did not happen fails before the uninstall instead of deleting the machine's real entries. Its
        /// safety therefore does not depend on the isolated branch it is checking.
        /// </summary>
        [Fact]
        public async Task UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone()
        {
            // Fail closed: never reach Apply unless the shell entries it could delete are the test run's own.
            var shell = Assert.IsType<InMemoryUserShell>(InstallFinalizer.UserShell);
            Assert.Same(TestRunIsolation.UserShell, shell);
            Assert.StartsWith(TestRunIsolation.RunRoot, shell.StartMenuShortcutPath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(TestRunIsolation.RunRoot, shell.DesktopShortcutPath, StringComparison.OrdinalIgnoreCase);

            // The REAL install's shell entries, as on the incident machine: both shortcuts, the Run value and
            // the uninstall entry, all pointing at an install OUTSIDE the root. An isolated uninstall that
            // regressed to the full one removes all four - here, in memory, where this test sees it.
            string realApp = Path.Combine(_temp, "real", "app");
            shell.Seed(shortcuts: true, runValue: $"\"{Path.Combine(realApp, "AgentEyesApp.exe")}\" --tray",
                entry: new UninstallEntry("AgentEyes", "1.12.0", "test", realApp, Path.Combine(realApp, "AgentEyesApp.exe"),
                    $"\"{Path.Combine(realApp, "agenteyes-setup.exe")}\" uninstall", $"\"{Path.Combine(realApp, "agenteyes-setup.exe")}\" uninstall --json"));
            string shellBefore = shell.Snapshot();

            var layout = InstallLayout.Isolated(_root);
            Directory.CreateDirectory(layout.AppDir);
            File.WriteAllText(layout.PathFor(ComponentRegistry.App), "app");
            Directory.CreateDirectory(layout.SetupStateDir);
            Directory.CreateDirectory(layout.BundleExtractDir);
            // As if an old --root install had finalized: the PATH entry and the variable point INTO the root.
            // They are still outside it, so the isolated uninstall must leave them alone - and say so.
            TestRunIsolation.UserEnvironment.Set("Path", layout.AppDir);
            TestRunIsolation.UserEnvironment.Set(InstallFinalizer.BundleExtractBaseDirVariable, layout.BundleExtractDir);

            var (exit, stdout, stderr) = await Run("uninstall", "--root", _root);

            // The shortcuts, the Run value and the uninstall entry FIRST: not one write, and all still there. A
            // regression fails here naming every write it made - to the in-memory store, never the machine.
            Assert.True(shell.Writes.Count == 0,
                "an isolated uninstall wrote outside the root: " + string.Join(" | ", shell.Writes));
            Assert.Equal(shellBefore, shell.Snapshot());
            Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}; stdout: {stdout}");
            Assert.False(Directory.Exists(layout.AppDir));
            Assert.False(Directory.Exists(layout.SetupStateDir));
            Assert.False(Directory.Exists(layout.BundleExtractDir));
            Assert.Contains(Uninstaller.OutsideTheRootNotTouched, stdout);
            Assert.Equal(layout.AppDir, TestRunIsolation.UserEnvironment.Get("Path"));
            Assert.Equal(layout.BundleExtractDir, TestRunIsolation.UserEnvironment.Get(InstallFinalizer.BundleExtractBaseDirVariable));
            Assert.True(shell.ShortcutExists(shell.StartMenuShortcutPath), "the real Start Menu shortcut was removed");
            Assert.True(shell.ShortcutExists(shell.DesktopShortcutPath), "the real desktop shortcut was removed");
            Assert.NotNull(shell.GetRunValue());
            Assert.True(shell.UninstallEntryExists(), "the real Add/Remove Programs entry was removed");
        }

        [Fact]
        public void UninstallerPlan_Isolated_ListsOnlyTargetsInsideTheRoot()
        {
            var layout = InstallLayout.Isolated(_root);

            var plan = new Uninstaller(layout).Plan();

            Assert.Equal(3, plan.Count);
            Assert.All(plan, t => Assert.True(layout.Contains(t.Path), $"{t.Kind} {t.Path} is outside the root"));
            Assert.DoesNotContain(plan, t => t.Kind is UninstallKind.Shortcut or UninstallKind.RunKey or UninstallKind.ArpEntry
                                                 or UninstallKind.PathEntry or UninstallKind.EnvVar);
        }

        // ---- the layout ---------------------------------------------------------------------------

        [Fact]
        public void Isolated_IsMarked_IsAbsolute_AndStagesInsideTheRoot()
        {
            var layout = InstallLayout.Isolated(_root.Replace('\\', '/'));

            Assert.True(layout.IsIsolated);
            Assert.Equal(Path.GetFullPath(_root), layout.LocalRoot);
            Assert.DoesNotContain('/', layout.LocalRoot);
            Assert.Equal(Path.Combine(layout.LocalRoot, "staging"), layout.StagingDir);
        }

        [Fact]
        public void Constructor_IsNotIsolated_AndStagesInTemp()
        {
            var layout = new InstallLayout(_root);

            Assert.False(layout.IsIsolated);
            Assert.Equal(Path.GetTempPath(), layout.StagingDir);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("app\\AgentEyesApp.exe", true)]
        [InlineData("..\\scratch-root2\\app\\AgentEyesApp.exe", false)]
        [InlineData("..\\real\\app\\AgentEyesApp.exe", false)]
        [InlineData("app\\..\\..\\real\\AgentEyesApp.exe", false)]
        public void Contains_OnlyTheRootAndBelow(string relative, bool expected)
        {
            var layout = InstallLayout.Isolated(_root);

            Assert.Equal(expected, layout.Contains(Path.Combine(_root, relative)));
        }

        // ---- the isolated handle and launcher -----------------------------------------------------

        [Fact]
        public void IsolatedHandleFind_ProcessOutsideTheRoot_IsLeftAlone_AndReportedAsNotRunning()
        {
            var handle = new IsolatedRootAppHandle(InstallLayout.Isolated(_root),
                _ => new[] { Process.GetCurrentProcess() }, _ => Path.Combine(_temp, "real", "app", "AgentEyesApp.exe"));

            Assert.Null(handle.Find());
        }

        [Fact]
        public void IsolatedHandleFind_ProcessInsideTheRoot_RefusesNamingThePid()
        {
            int pid = Environment.ProcessId;
            var handle = new IsolatedRootAppHandle(InstallLayout.Isolated(_root),
                _ => new[] { Process.GetCurrentProcess() }, _ => Path.Combine(_root, "app", "AgentEyesApp.exe"));

            var ex = Assert.Throws<IsolatedRootRefusedException>(() => handle.Find());
            Assert.Contains($"pid {pid}", ex.Message);
            Assert.Contains("never stops or starts an app", ex.Message);
        }

        [Fact]
        public void IsolatedHandleFind_ExePathUnreadable_Refuses()
        {
            var handle = new IsolatedRootAppHandle(InstallLayout.Isolated(_root),
                _ => new[] { Process.GetCurrentProcess() }, _ => null);

            var ex = Assert.Throws<IsolatedRootRefusedException>(() => handle.Find());
            Assert.Contains("could not read which exe", ex.Message);
        }

        [Fact]
        public async Task IsolatedHandleStop_IsAlwaysRefused()
        {
            var handle = new IsolatedRootAppHandle(InstallLayout.Isolated(_root));

            await Assert.ThrowsAsync<IsolatedRootRefusedException>(() =>
                handle.StopAsync(new RunningAppInstance(1, "x.exe", Array.Empty<string>()), CancellationToken.None));
        }

        [Fact]
        public void IsolatedHandle_NonIsolatedLayout_Throws()
        {
            Assert.Throws<ArgumentException>(() => new IsolatedRootAppHandle(new InstallLayout(_root)));
        }

        [Fact]
        public void RefusingAppLauncherLaunch_AlwaysThrows()
        {
            Assert.Throws<IsolatedRootRefusedException>(() => new RefusingAppLauncher().Launch("AgentEyesApp.exe", Array.Empty<string>()));
        }

        /// <summary>
        /// The real detection against a REAL process named AgentEyesApp, as the incident had: a stand-in (a
        /// renamed copy of cmd.exe) runs from outside the root and must be found, left alone and still alive;
        /// with the root moved over it, the same process must be refused by pid - and still be alive.
        /// </summary>
        [Fact]
        public void IsolatedHandleFind_RealProcessNamedAgentEyesApp_OutsideIsLeftRunning_InsideIsRefused()
        {
            string standInDir = Path.Combine(_temp, "standin", "app");
            Directory.CreateDirectory(standInDir);
            string standIn = Path.Combine(standInDir, "AgentEyesApp.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), standIn);
            using var p = Process.Start(new ProcessStartInfo(standIn, "/d /c ping -n 60 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            try
            {
                var outside = new IsolatedRootAppHandle(InstallLayout.Isolated(_root));
                Assert.Null(outside.Find());
                Assert.False(p.HasExited, "the stand-in outside the root must still be running");

                var inside = new IsolatedRootAppHandle(InstallLayout.Isolated(Path.Combine(_temp, "standin")));
                var ex = Assert.Throws<IsolatedRootRefusedException>(() => inside.Find());
                Assert.Contains($"pid {p.Id}", ex.Message);
                Assert.False(p.HasExited, "a refusal must not stop the process it names");
            }
            finally
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
            }
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>A release dir that is never created - see the note above the refusal tests.</summary>
        private string MissingReleaseDir => Path.Combine(_temp, "no-such-release");

        private static List<string> Lines(string log, string marker, string after)
        {
            var found = new List<string>();
            foreach (var line in log.Split('\n'))
            {
                int m = line.IndexOf(marker, StringComparison.Ordinal);
                if (m < 0) continue;
                int a = line.IndexOf(after, m + marker.Length, StringComparison.Ordinal);
                if (a < 0) continue;
                found.Add(line.Substring(a + after.Length).Trim());
            }
            return found;
        }

        /// <summary>Run the CLI's real entry point in-process with the console captured.</summary>
        private static async Task<(int Exit, string Stdout, string Stderr)> Run(params string[] argv)
        {
            var outBefore = Console.Out;
            var errBefore = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var syncOut = TextWriter.Synchronized(stdout);
            var syncErr = TextWriter.Synchronized(stderr);
            Console.SetOut(syncOut);
            Console.SetError(syncErr);
            try
            {
                int exit = await CliProgram.RunAsync(argv, syncOut, syncErr);
                syncOut.Flush();
                syncErr.Flush();
                return (exit, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(outBefore);
                Console.SetError(errBefore);
                EngineLog.Sink = null;
            }
        }

        /// <summary>A local release with the app (a file) and ffmpeg (a zip), so both the download staging
        /// and the archive extraction run.</summary>
        private void WriteRelease(string version)
        {
            string app = Path.Combine(_releaseDir, ComponentRegistry.App.Asset);
            File.WriteAllText(app, $"app build {version}");
            string zip = Path.Combine(_releaseDir, ComponentRegistry.Ffmpeg.Asset);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    using var w = new StreamWriter(z.CreateEntry(exe).Open());
                    w.Write($"{exe} {version}");
                }
            }
            File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), $$"""
                {
                  "version": "{{version}}",
                  "assets": {
                    "{{ComponentRegistry.App.Asset}}": { "version": "{{version}}", "sha256": "{{Hashing.Sha256OfFile(app)}}" },
                    "{{ComponentRegistry.Ffmpeg.Asset}}": { "version": "{{version}}", "sha256": "{{Hashing.Sha256OfFile(zip)}}" }
                  }
                }
                """);
        }

        private sealed class SpyApp : IRunningAppHandle
        {
            public RunningAppInstance? Instance { get; set; }
            public int FindCalls { get; private set; }
            public int StopCalls { get; private set; }

            public RunningAppInstance? Find()
            {
                FindCalls++;
                return Instance;
            }

            public Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct)
            {
                StopCalls++;
                return Task.FromResult(true);
            }
        }

        private sealed class SpyLauncher : IAppLauncher
        {
            public List<(string Exe, IReadOnlyList<string> Arguments)> Launches { get; } = new();

            public int Launch(string exePath, IReadOnlyList<string> arguments)
            {
                Launches.Add((exePath, arguments));
                return 4242;
            }
        }
    }
}
