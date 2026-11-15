using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        public void PlatformName_OnAnyHost_NamesTheOperatingSystemThisProcessRunsOn()
        {
            // Shell.PlatformName is a RUNTIME host check, so an assertion that names one platform can
            // only hold on that platform (it read "linux" here and failed on a Mac - issue #116). The
            // behaviour worth pinning is host-independent: whatever host this is, the app names it
            // correctly and therefore picks that host's opener. Expected comes from the operating
            // system's own description rather than from OperatingSystem.IsX, so this is not a mirror
            // of the implementation: swap two arms of PlatformName and this fails on both hosts.
            // Expected comes from a file only that operating system has, which is the OS speaking for
            // itself. RuntimeInformation.OSDescription is NOT usable: on Linux .NET returns the
            // distribution's pretty name - the CI runner answers "Ubuntu 26.04.1 LTS", which names no
            // platform at all - and on macOS it answers "macOS 26.5.2". Both were tried and both were
            // caught by the throw below, which is why the throw is here: an unrecognised host must say
            // so, never fall through to a default that would pass without checking anything.
            bool mac = File.Exists("/System/Library/CoreServices/SystemVersion.plist");
            bool linux = !mac && File.Exists("/proc/version");
            bool windows = !mac && !linux && Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            (string name, string opener) =
                mac ? ("mac", "open") :
                linux ? ("linux", "xdg-open") :
                windows ? ("windows", "explorer.exe") :
                throw new InvalidOperationException(
                    $"unrecognised host '{RuntimeInformation.OSDescription}' - no macOS, Linux or Windows marker found; this suite runs on those three");

            Assert.Equal(name, Shell.PlatformName);
            Assert.Equal(opener, Shell.OpenerFor(Shell.PlatformName));
        }

        [Fact]
        public void SocketPath_OnThisHost_FitsTheUnixSocketPathLimit()
        {
            // The product's ONE socket path: Program.cs:40 -> TryAcquire -> TryAcquireSocket(SocketPath).
            // It is $XDG_RUNTIME_DIR, or the temp directory when that is unset (the macOS case), plus
            // "agenteyes-app.sock". The fixtures above overran sun_path on macOS; this asserts the
            // PRODUCT does not, on whatever host the suite is running on - 67 bytes on this Mac.
            string path = SingleInstance.SocketPath;
            int bytes = Encoding.UTF8.GetByteCount(path);
            Assert.True(
                bytes > 0 && bytes <= LinuxTestIsolation.MaxUnixSocketPathBytes,
                $"SingleInstance.SocketPath is {bytes} bytes, over the {LinuxTestIsolation.MaxUnixSocketPathBytes}-byte sun_path limit: {path}");
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
            string dir = LinuxTestIsolation.NewSocketDir();
            try
            {
                string path = Path.Combine(dir, "app.sock");
                AssertFitsSunPath(path);
                AssertHandoff(cmd => SingleInstance.TryAcquireSocket(cmd, path), "socket");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void TryAcquirePipe_SecondLaunch_HandsItsCommandToTheFirstAndReturnsNull()
        {
            string name = LinuxTestIsolation.NewPipeName();
            // .NET lays a named pipe on Unix over a socket at GetTempPath() + "CoreFxPipe_" + name, so
            // the name spends the same budget as a socket path does.
            if (!OperatingSystem.IsWindows()) AssertFitsSunPath(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name));
            AssertHandoff(cmd => SingleInstance.TryAcquirePipe(cmd, name), "pipe");
        }

        /// <summary>A fixture path too long for sun_path throws ArgumentOutOfRangeException from inside
        /// the product call and reads as a product defect. Fail here instead, saying which it is.</summary>
        private static void AssertFitsSunPath(string path)
        {
            int bytes = Encoding.UTF8.GetByteCount(path);
            Assert.True(
                bytes > 0 && bytes <= LinuxTestIsolation.MaxUnixSocketPathBytes,
                $"THE FIXTURE, not the product: this test's path is {bytes} bytes, over the {LinuxTestIsolation.MaxUnixSocketPathBytes}-byte sun_path limit: {path}");
        }

        private static void AssertHandoff(Func<string, SingleInstance?> acquire, string transport)
        {
            var received = new BlockingCollection<string>();
            var first = acquire("show");
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

            // Shutting down must RETURN. On macOS, closing a listening socket whose accept thread is
            // blocked in accept() used to block forever, so Program.Main's finally never finished and
            // the app never quit (issue #116). Disposing on another thread with a deadline is what
            // makes a regression FAIL here in ten seconds instead of hanging the whole suite.
            var disposed = Task.Run(first.Dispose);
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(10)),
                $"Dispose did not return within 10 s on the {transport} transport - the listener is not being woken");

            // And shutting down must not deliver a command of its own. The socket transport wakes its
            // accept thread by connecting to its own socket; that connection carries no bytes and must
            // never be mistaken for a launch asking the app to show a window.
            Assert.False(received.TryTake(out string? spurious, TimeSpan.FromSeconds(1)),
                $"shutting down the {transport} transport delivered a command of its own: '{spurious}'");

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
