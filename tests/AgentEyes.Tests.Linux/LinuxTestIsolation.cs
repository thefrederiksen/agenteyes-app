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
    }
}
