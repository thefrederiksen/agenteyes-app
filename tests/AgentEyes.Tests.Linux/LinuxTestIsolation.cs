using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AgentEyes.Tests.Linux
{
    /// <summary>
    /// Moves AgentEyes' data and recordings folders into a per-run temp folder before any test runs
    /// (the issue #78 contract the Windows suite keeps in TestRunIsolation), so a test run on a machine
    /// where the service is running never writes into its log, credential or recordings.
    /// </summary>
    internal static class LinuxTestIsolation
    {
        internal static string RunRoot { get; private set; } = "";

#pragma warning disable CA2255 // A module initializer is exactly the point: it must run before any test does.
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Redirect()
        {
            RunRoot = Path.Combine(Path.GetTempPath(), "agenteyes-linux-tests", $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
            string data = Path.Combine(RunRoot, "data");
            string videos = Path.Combine(RunRoot, "Videos");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(videos);
            AppDataPaths.RedirectForThisProcess(data, videos);
        }

        internal static string NewTempDir(string name)
        {
            string dir = Path.Combine(RunRoot, name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// A fresh directory for a Unix domain socket, named short enough that the socket path inside
        /// it fits sun_path on EVERY platform this suite runs on. sun_path holds 104 bytes on macOS
        /// (108 on Linux), and the macOS per-user TMPDIR is already 49 characters before anything is
        /// added - so RunRoot, whose name carries a timestamp and a pid, cannot hold a socket there:
        /// its path came to 121 bytes and .NET threw ArgumentOutOfRangeException (issue #116).
        /// Deliberately NOT under RunRoot for that reason; the caller deletes it.
        /// </summary>
        internal static string NewSocketDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ae" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// A named-pipe name short enough to be usable on every platform this suite runs on. .NET maps
        /// a named pipe on Unix onto a socket at Path.GetTempPath() + "CoreFxPipe_" + name, so the name
        /// spends the same sun_path budget: the old 47-character name came to 107 bytes on macOS.
        /// </summary>
        internal static string NewPipeName() => "ae" + Guid.NewGuid().ToString("N")[..12];

        /// <summary>The largest Unix socket path any platform here accepts: sun_path is 104 bytes on
        /// macOS and 108 on Linux, so 104 is the limit that must hold everywhere.</summary>
        internal const int MaxUnixSocketPathBytes = 104;
    }
}
