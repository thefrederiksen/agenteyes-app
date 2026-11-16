using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace AgentEyes.Tests.Shared
{
    /// <summary>
    /// Issue #161 pass 2: the desktop app (src/AgentEyes.DesktopApp - renamed from AgentEyes.LinuxApp by #116
    /// while #161 was in review) must REFUSE to run redirected. It always drives the service at the fixed
    /// address ControlClient.BaseUrl - the person's REAL AgentEyes - so a redirected desktop app would keep a
    /// scratch log while showing and driving the real library: a half-redirect, the worst outcome this
    /// feature can have. Pass 1 wired the refusal into the Linux app; this pins that it survived the rename,
    /// on the built app, as a real child process.
    ///
    /// Linked into AgentEyes.Tests.Linux (Linux CI) and AgentEyes.Tests.DesktopApp (the Windows gate). The
    /// macOS suite does not reference the desktop app, so macOS is not covered by a gate here.
    ///
    /// Every child is started with BOTH variables pointing into this run's scratch folder: if the refusal
    /// were ever lost, the child would be on the scratch library, never the person's real one.
    /// </summary>
    public sealed class DesktopAppRedirectTests : IDisposable
    {
        // Literals, not LibraryRedirect's constants: the engine's internals are not visible to the Windows
        // desktop-app test project, and a test that spells the contract out cannot drift with the code.
        private const string DataVariable = "AGENTEYES_REDIRECT_DATA";
        private const string VideosVariable = "AGENTEYES_REDIRECT_VIDEOS";
        private const int RefusedExitCode = 5;

        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "agenteyes-161-tests", $"desk-{Environment.ProcessId}-{Guid.NewGuid():N}");

        /// <summary>This run's scratch folder (the redirected log the refusal wrote) is removed after each test.</summary>
        public void Dispose()
        {
            if (Directory.Exists(Scratch)) Directory.Delete(Scratch, recursive: true);
        }

        [Fact]
        public void Main_Redirected_RefusesWithExit5BeforeAnyWindowAndSaysWhy()
        {
            // Existing, writable folders, so the redirect itself is accepted and the DESKTOP APP's refusal is
            // what is tested (a missing folder is refused earlier, by the engine - issue #161 pass 2, N1).
            string data = Directory.CreateDirectory(Path.Combine(Scratch, "data")).FullName;
            string videos = Directory.CreateDirectory(Path.Combine(Scratch, "videos")).FullName;

            var (code, stderr) = RunDesktopApp(data, videos, "--tray");

            Assert.Equal(RefusedExitCode, code);
            Assert.Contains("library redirect refused: the desktop app always drives the service at http://127.0.0.1:7882", stderr);
            Assert.Contains("cannot run redirected", stderr);
        }

        [Fact]
        public void Main_PartialRedirect_RefusedWithExit5AndWritesNothing()
        {
            string data = Path.Combine(Scratch, "partial", "data");

            var (code, stderr) = RunDesktopApp(data, videos: null, "--tray");

            Assert.Equal(RefusedExitCode, code);
            Assert.Contains("library redirect refused: partial library redirect", stderr);
            Assert.False(Directory.Exists(data), $"a refused start created {data}");
        }

        private static (int Code, string Stderr) RunDesktopApp(string data, string? videos, string args)
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "AgentEyesDesktop.dll");
            Assert.True(File.Exists(dll), $"the desktop app was not built next to the tests: {dll}");
            string dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string host = Path.Combine(dotnetRoot, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
            Assert.True(File.Exists(host), $"no dotnet host at {host}");

            var psi = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(dll);
            foreach (string a in args.Split(' ')) psi.ArgumentList.Add(a);
            foreach (string name in psi.Environment.Keys.Where(k => k.StartsWith("AGENTEYES_REDIRECT_", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(name);
            psi.Environment[DataVariable] = data;
            if (videos != null) psi.Environment[VideosVariable] = videos;
            psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000))
            {
                // A redirected desktop app that did NOT refuse is running its tray on the scratch library.
                p.Kill(entireProcessTree: true);
                throw new Xunit.Sdk.XunitException($"the redirected desktop app did not exit within 60s - it did not refuse. stderr: {stderr.Result}");
            }
            _ = stdout.Result;
            return (p.ExitCode, stderr.Result);
        }
    }
}
