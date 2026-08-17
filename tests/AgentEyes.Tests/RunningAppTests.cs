using System;
using System.Diagnostics;
using System.IO;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Unit tests for the shared running-instance detection/stop helper (issue #95).
    /// The stop mechanics use an injected process provider so the bounded graceful-then-force
    /// stop is exercised without launching the real AgentEyes app.
    /// </summary>
    public sealed class RunningAppTests
    {
        // ---- detection targets the installed exe name (the bug the old "AgentEyes" literal had) ----

        [Fact]
        public void ProcessName_DerivedFromInstalledAppExe_IsAgentEyesApp()
        {
            var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), "agenteyes-name-test"));
            // The installed tray app is AgentEyesApp.exe, so the process name is "AgentEyesApp"
            // (NOT "AgentEyes", which never matched the running process).
            Assert.Equal("AgentEyesApp", RunningApp.ProcessName(layout));
        }

        [Theory]
        [InlineData(@"C:\Users\x\AppData\Local\AgentEyes\app\AgentEyesApp.exe", "AgentEyesApp")]
        [InlineData(@"D:\somewhere\AgentEyesApp.exe", "AgentEyesApp")]
        public void ProcessNameFromExe_StripsDirectoryAndExtension(string exe, string expected)
        {
            Assert.Equal(expected, RunningApp.ProcessNameFromExe(exe));
        }

        [Fact]
        public void ProcessNameFromExe_EmptyPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => RunningApp.ProcessNameFromExe(""));
        }

        // ---- IsRunning over an injected provider ----

        [Fact]
        public void IsRunning_ProviderReturnsNone_False()
        {
            Assert.False(RunningApp.IsRunning("whatever", _ => Array.Empty<Process>()));
        }

        [Fact]
        public void IsRunning_ProviderReturnsAProcess_True()
        {
            // The current test-runner process is a safe, live process to report - never stopped here.
            Assert.True(RunningApp.IsRunning("whatever", _ => new[] { Process.GetCurrentProcess() }));
        }

        // ---- bounded, confirmed stop ----

        [Fact]
        public void StopAndWait_NoRunningInstance_ReturnsTrueImmediately()
        {
            bool gone = RunningApp.StopAndWait("whatever",
                TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1), _ => Array.Empty<Process>());
            Assert.True(gone);
        }

        [Fact]
        public void StopAndWait_TerminatesRunningProcess_AndConfirmsGone()
        {
            // A real, disposable child that will not exit on its own (cmd waiting on pause).
            var psi = new ProcessStartInfo("cmd.exe", "/c pause")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var child = Process.Start(psi)!;
            int pid = child.Id;

            // Provider returns ONLY this child by pid, so no other process on the machine is touched.
            Process[] Provider(string _)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    return p.HasExited ? Array.Empty<Process>() : new[] { p };
                }
                catch (ArgumentException)
                {
                    return Array.Empty<Process>(); // already gone
                }
            }

            bool gone = RunningApp.StopAndWait("cmd",
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), Provider);

            Assert.True(gone);
            Assert.True(child.HasExited);
        }
    }
}
