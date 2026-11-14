using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.DesktopApp;
using AgentEyes.DesktopApp.Services;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    /// <summary>
    /// Issue #116: the desktop app is ONE project for Windows, macOS and Linux. These pin the parts
    /// that differ by platform and can be checked without a window: the opener per platform, the
    /// single-instance handoff over BOTH transports (the Windows named pipe runs here too - .NET
    /// carries named pipes on Linux - though Windows pipe semantics are only proven on Windows), and
    /// that the Linux installer names the app the build actually produces.
    /// </summary>
    public sealed class DesktopAppPlatformTests
    {
        [Theory]
        [InlineData("windows", "explorer.exe")]
        [InlineData("mac", "open")]
        [InlineData("linux", "xdg-open")]
        public void OpenerFor_EachPlatform_ReturnsThatPlatformsOpener(string platform, string expected)
        {
            Assert.Equal(expected, Shell.OpenerFor(platform));
        }

        [Fact]
        public void OpenerFor_UnknownPlatform_Throws()
        {
            Assert.Throws<ArgumentException>(() => Shell.OpenerFor("macos"));
        }

        [Fact]
        public void PlatformName_OnThisLinuxHost_IsLinux()
        {
            Assert.Equal("linux", Shell.PlatformName);
        }

        [Fact]
        public void WmClass_IsTheAssemblyName()
        {
            // The installer launches the executable by the assembly name and writes StartupWMClass from
            // the same word; GNOME groups the window under its launcher only when they agree.
            Assert.Equal("AgentEyesDesktop", typeof(AgentEyes.DesktopApp.Program).Assembly.GetName().Name);
            Assert.Equal(typeof(AgentEyes.DesktopApp.Program).Assembly.GetName().Name, AgentEyes.DesktopApp.Program.WmClass);
        }

        [Fact]
        public void InstallScript_NamesTheBuiltExecutableAndWmClass()
        {
            string name = typeof(AgentEyes.DesktopApp.Program).Assembly.GetName().Name!;
            string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "linux", "install.sh"));

            Assert.Contains("src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj", script);
            Assert.Contains($"[[ -x \"$app_stage/{name}\" ]]", script);
            Assert.Contains($"\"Exec=$app_lib/{name}$1\"", script);
            Assert.Contains($"\"StartupWMClass={AgentEyes.DesktopApp.Program.WmClass}\"", script);
            Assert.Contains($"setsid \"$app_lib/{name}\"", script);
        }

        [Fact]
        public void InstallScript_StopsTheRunningAppByPath_NotByTruncatedProcessName()
        {
            // A Linux process name is cut to 15 characters; "AgentEyesDesktop" is 16, so pgrep -x on it
            // matches nothing and the old app would survive the install and swallow the new launch.
            string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "linux", "install.sh"));
            string[] code = script.Split('\n').Where(l => !l.TrimStart().StartsWith("#")).ToArray();

            Assert.Contains(code, l => l.Contains("pgrep -f \"^$app_lib/AgentEyes(Desktop|Linux)( |\\$)\""));
            Assert.DoesNotContain(code, l => l.Contains("pgrep -x"));
        }

        [Fact]
        public void TryAcquireSocket_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("single"), "app.sock");
            AssertHandoff(cmd => SingleInstance.TryAcquireSocket(cmd, path), "socket");
        }

        [Fact]
        public void TryAcquirePipe_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull()
        {
            string name = "agenteyes-test-" + Guid.NewGuid().ToString("N");
            AssertHandoff(cmd => SingleInstance.TryAcquirePipe(cmd, name), "pipe");
        }

        private static void AssertHandoff(Func<string, SingleInstance?> acquire, string transport)
        {
            var received = new BlockingCollection<string>();
            using (var first = acquire("show"))
            {
                Assert.NotNull(first);
                Assert.Equal(transport, first!.Transport);
                first.CommandReceived += received.Add;

                var second = acquire("view:library");
                Assert.Null(second);
                Assert.True(received.TryTake(out string? cmd, TimeSpan.FromSeconds(10)), "the first instance never received the second launch's command");
                Assert.Equal("view:library", cmd);

                // And again: the listener keeps serving after the first handoff.
                Assert.Null(acquire("settings:account"));
                Assert.True(received.TryTake(out cmd, TimeSpan.FromSeconds(10)), "the first instance stopped listening after one handoff");
                Assert.Equal("settings:account", cmd);
            }

            // Once the first instance is gone, the next launch is the first again.
            using var next = acquire("show");
            Assert.NotNull(next);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentEyes.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("AgentEyes.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
