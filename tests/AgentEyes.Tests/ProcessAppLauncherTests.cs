using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #94: the app that "agenteyes-setup update" started again inherited the updater's standard
    /// handles, so a script that captured the updater's output ("$o = &amp; agenteyes-setup update 2>&amp;1")
    /// hung until the app was quit. The real <see cref="ProcessAppLauncher"/> is exercised here on a
    /// harmless long-lived child (cmd.exe waiting on pause, hidden) - never on AgentEyesApp.exe - and
    /// every child is killed on the way out.
    ///
    /// Each check fails closed: the pipe tests wait for a specific event (end-of-stream on the read end)
    /// within a bound and assert that the child is STILL RUNNING at that moment, so a launcher that
    /// hands the child a copy of the pipe (the pre-#94 Process.Start) times out instead of passing.
    /// </summary>
    public sealed class ProcessAppLauncherTests : IDisposable
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteyes-launcher-test-" + Guid.NewGuid().ToString("N"));
        private readonly List<int> _children = new();

        private InstallLayout Layout => new(_root);

        private static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

        private static string PowerShellExe => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        public void Dispose()
        {
            foreach (int pid in _children) Kill(pid);
            try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
        }

        // ---- criterion: no inherited stdio handles, detached from the updater's console ----------

        [Fact]
        public async Task Launch_ChildHoldsNoCopyOfAnInheritableHandle_ReadEndSeesEofWhileChildStillRuns()
        {
            // An inheritable pipe end in THIS process stands in for the stdout pipe a script hands the
            // updater. After the launch, this process drops its own copy; if the child got one, the read
            // end never sees end-of-stream until the child dies - which is exactly the #94 hang.
            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

            int pid = new ProcessAppLauncher(Layout).Launch(CmdExe, new[] { "/c", "pause", "--tray" });
            _children.Add(pid);

            pipe.DisposeLocalCopyOfClientHandle();
            var read = pipe.ReadAsync(new byte[16], 0, 16);

            bool closed = await Task.WhenAny(read, Task.Delay(Bound)) == read;

            Assert.True(closed, $"the read end did not reach end-of-stream within {Bound}: the child (pid {pid}) holds a copy of the pipe");
            Assert.Equal(0, await read);
            Assert.False(Process.GetProcessById(pid).HasExited, "the child must still be running when the pipe closes - otherwise the close proves nothing");
        }

        [Fact]
        public async Task Launch_FromAProcessWhoseStdoutIsAPipe_ThePipeClosesWhenThatProcessExits_WhileTheChildKeepsRunning()
        {
            // The literal #94 scenario. The "updater" is this test assembly run as a separate process
            // (LaunchProbe.Main) with stdout AND stderr captured through pipes, the way PowerShell's
            // "$o = & agenteyes-setup update 2>&1" captures the real updater. The probe runs the real
            // launcher on cmd.exe /c pause, prints the pid and exits. Both pipes must then close
            // within the bound with that child still alive.
            string testsDll = typeof(LaunchProbe).Assembly.Location;
            string dotnet = DotnetExe();
            Assert.True(File.Exists(testsDll), $"the test assembly is not at {testsDll}");

            var psi = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(testsDll);
            psi.ArgumentList.Add(LaunchProbe.Switch);
            psi.ArgumentList.Add(_root);
            psi.ArgumentList.Add(CmdExe);
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("pause");
            psi.ArgumentList.Add("--tray");

            using var probe = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned no probe process");
            var stderr = probe.StandardError.ReadToEndAsync();

            // The pid line arrives while the probe runs; it is what lets the child be killed even when
            // the pipe then never closes (the failure this test exists to catch).
            var firstLine = probe.StandardOutput.ReadLineAsync();
            Assert.True(await Task.WhenAny(firstLine, Task.Delay(Bound)) == firstLine, "the probe printed no pid line within the bound");
            string? pidLine = await firstLine;
            Assert.NotNull(pidLine);
            Assert.StartsWith("pid=", pidLine, StringComparison.Ordinal);
            int pid = int.Parse(pidLine!.Substring("pid=".Length));
            _children.Add(pid);

            var sw = Stopwatch.StartNew();
            var rest = probe.StandardOutput.ReadToEndAsync();
            var bothClosed = Task.WhenAll(rest, stderr);
            bool closed = await Task.WhenAny(bothClosed, Task.Delay(Bound)) == bothClosed;
            sw.Stop();

            Assert.True(closed, $"the probe's stdout/stderr pipes did not close within {Bound}: the child (pid {pid}) holds a copy of them");
            Assert.True(probe.WaitForExit((int)Bound.TotalMilliseconds), "the probe did not exit");
            Assert.True(probe.ExitCode == 0, $"the probe exited {probe.ExitCode}: {await stderr}");
            Assert.False(Process.GetProcessById(pid).HasExited, "the child must still be running when the pipes close - otherwise the close proves nothing");
            Assert.True(sw.Elapsed < Bound, $"pipes closed after {sw.Elapsed}");
        }

        // ---- criterion: the relaunched app still gets its original arguments ----------------------

        [Fact]
        public void Launch_ChildRunsWithExactlyTheArgumentsGiven_TrayIncluded()
        {
            // The child's own command line, as Windows recorded it, split the way the child's C runtime
            // splits it - the same read the update uses to find out how the app was running (issue #86).
            var arguments = new[] { "/c", "pause", "--tray" };

            int pid = new ProcessAppLauncher(Layout).Launch(CmdExe, arguments);
            _children.Add(pid);

            string? commandLine = RunningAppHandle.WmiCommandLine(pid);
            Assert.False(string.IsNullOrWhiteSpace(commandLine), "WMI returned no command line for the child");
            var argv = RunningAppHandle.SplitCommandLine(commandLine!);
            Assert.Equal(CmdExe, argv[0], ignoreCase: true);
            Assert.Equal(arguments, argv.Skip(1).ToArray());
        }

        [Fact]
        public void Launch_ChildSeesTheParentEnvironmentPlusTheBundleVariable()
        {
            // The updater's environment must reach the child (a script's variables, PATH, ...) with the
            // native-extraction directory set from the layout on top (issue #120); proven by a child that
            // writes both values to a file named by a variable only this test process set.
            Directory.CreateDirectory(_root);
            string marker = "AGENTEYES_TEST_" + Guid.NewGuid().ToString("N");
            string outFile = Path.Combine(_root, "env.txt");
            Environment.SetEnvironmentVariable(marker, outFile);
            try
            {
                var arguments = new[]
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                    $"[IO.File]::WriteAllText($env:{marker}, $env:{InstallFinalizer.BundleExtractBaseDirVariable} + '|' + 'arg with spaces')",
                };
                int pid = new ProcessAppLauncher(Layout).Launch(PowerShellExe, arguments);
                _children.Add(pid);

                Assert.True(WaitForExit(pid, Bound), "the powershell child did not exit within the bound");
                Assert.True(File.Exists(outFile), "the child wrote nothing: the marker variable or the command did not reach it");
                Assert.Equal(Layout.BundleExtractDir + "|arg with spaces", File.ReadAllText(outFile));
            }
            finally
            {
                Environment.SetEnvironmentVariable(marker, null);
            }
        }

        [Theory]
        [InlineData("--tray")]
        [InlineData("")]
        [InlineData("has spaces")]
        [InlineData("has \"quotes\" inside")]
        [InlineData("ends with backslash\\")]
        [InlineData("C:\\dir with spaces\\")]
        [InlineData("back\\\\slash \"then quote")]
        [InlineData("tab\there")]
        public void BuildCommandLine_RoundTripsThroughTheChildsArgvSplit(string argument)
        {
            const string exe = @"C:\Program Files\AgentEyes\AgentEyesApp.exe";
            var arguments = new[] { "--tray", argument, "last" };

            string line = ProcessAppLauncher.BuildCommandLine(exe, arguments);
            var argv = RunningAppHandle.SplitCommandLine(line);

            Assert.Equal(new[] { exe }.Concat(arguments).ToArray(), argv.ToArray());
        }

        [Fact]
        public void BuildCommandLine_NoArguments_IsTheQuotedExeAlone()
        {
            Assert.Equal("\"C:\\a b\\x.exe\"", ProcessAppLauncher.BuildCommandLine(@"C:\a b\x.exe", Array.Empty<string>()));
        }

        // ---- failure shape --------------------------------------------------------------------------

        [Fact]
        public void Launch_ExeMissing_ThrowsFileNotFound_BeforeAnythingIsStarted()
        {
            var ex = Assert.Throws<FileNotFoundException>(() =>
                new ProcessAppLauncher(Layout).Launch(Path.Combine(_root, "missing.exe"), Array.Empty<string>()));
            Assert.Contains("missing.exe", ex.FileName);
        }

        [Fact]
        public void Launch_NotAnExecutable_ThrowsWithTheWindowsReason()
        {
            Directory.CreateDirectory(_root);
            string notAnExe = Path.Combine(_root, "notes.txt");
            File.WriteAllText(notAnExe, "not a program");

            var ex = Assert.Throws<System.ComponentModel.Win32Exception>(() =>
                new ProcessAppLauncher(Layout).Launch(notAnExe, Array.Empty<string>()));
            Assert.Contains("CreateProcess failed", ex.Message);
            Assert.Contains("notes.txt", ex.Message);
        }

        // ---- helpers --------------------------------------------------------------------------------

        /// <summary>The dotnet host that runs this very test: three levels up from the shared runtime
        /// directory System.Private.CoreLib was loaded from. Fails loudly when it is not there.</summary>
        private static string DotnetExe()
        {
            string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            string dotnet = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", "dotnet.exe"));
            Assert.True(File.Exists(dotnet), $"dotnet.exe was not found at {dotnet} (runtime dir {runtimeDir})");
            return dotnet;
        }

        private static bool WaitForExit(int pid, TimeSpan bound)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.WaitForExit((int)bound.TotalMilliseconds);
            }
            catch (ArgumentException)
            {
                return true; // already gone
            }
        }

        private static void Kill(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // already gone
            }
            catch (InvalidOperationException)
            {
                // exited between the check and the kill
            }
        }
    }
}
