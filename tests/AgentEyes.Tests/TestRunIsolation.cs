using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using AgentEyes.Setup.Engine;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #78: a test run must never write into - or read from - the REAL per-user AgentEyes
    /// state of the machine it runs on. On 2026-09-24 the suite appended fake always-on sessions,
    /// "fake ffmpeg: device lost" errors and PostRecording test folders into the installed app's live
    /// log, interleaved with the app's own lines, and hid a real always-on problem.
    ///
    /// So before ANY test code runs, this module initializer moves every AgentEyes data root into a
    /// per-run folder under %TEMP%:
    ///
    ///   %TEMP%\agenteyes-tests\run-yyyyMMdd-HHmmss-pid\LocalAppData\AgentEyes\...  (logs, config, presets,
    ///                                                                              alwayson, preview, models...)
    ///   %TEMP%\agenteyes-tests\run-yyyyMMdd-HHmmss-pid\Videos\AgentEyes\...        (recordings, clips)
    ///
    /// and swaps the setup engine's user-environment store for an in-memory one, so no test changes the
    /// user PATH or DOTNET_BUNDLE_EXTRACT_BASE_DIR the installed app relies on. AGENTEYES_ROOT is set for
    /// THIS process only (never the user's environment) so <see cref="InstallLayout.Default"/> lands in the
    /// run folder too.
    ///
    /// Fail-closed: <see cref="AppDataPaths.RedirectForThisProcess"/> THROWS if any AgentEyes path was
    /// resolved before it ran, and an exception here fails every test in the assembly rather than letting
    /// one run against the real folders. <c>TestIsolationTests</c> asserts the redirect took effect.
    ///
    /// The run folder is kept after the run (its log is the place to look when a test fails). Run
    /// folders from earlier runs are pruned by <see cref="PruneOldRuns"/>, which deletes only folders
    /// whose NAME proves this class created them and that are older than <see cref="KeepRunsFor"/>.
    /// </summary>
    internal static class TestRunIsolation
    {
        /// <summary>How long an earlier run's folder (and its log) is kept for inspection.</summary>
        internal static readonly TimeSpan KeepRunsFor = TimeSpan.FromDays(2);

        private const string RunPrefix = "run-";
        private const string StampFormat = "yyyyMMdd-HHmmss";

        /// <summary>%TEMP%\agenteyes-tests - the parent of every run folder.</summary>
        internal static string RunsParent => Path.Combine(Path.GetTempPath(), "agenteyes-tests");

        /// <summary>This run's folder.</summary>
        internal static string RunRoot { get; private set; } = "";

        /// <summary>The stand-in for %LOCALAPPDATA% this run uses.</summary>
        internal static string LocalAppData { get; private set; } = "";

        /// <summary>The stand-in for the Videos folder this run uses.</summary>
        internal static string Videos { get; private set; } = "";

        /// <summary>The in-memory user environment the setup engine writes to in this run.</summary>
        internal static InMemoryUserEnvironment UserEnvironment { get; } = new();

#pragma warning disable CA2255 // A module initializer is exactly the point: it must run before any test does.
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void RedirectEverything()
        {
            DateTime now = DateTime.Now;
            RunRoot = Path.Combine(RunsParent,
                $"{RunPrefix}{now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Environment.ProcessId}");
            LocalAppData = Path.Combine(RunRoot, "LocalAppData");
            Videos = Path.Combine(RunRoot, "Videos");
            Directory.CreateDirectory(LocalAppData);
            Directory.CreateDirectory(Videos);

            AppDataPaths.RedirectForThisProcess(LocalAppData, Videos);
            Environment.SetEnvironmentVariable("AGENTEYES_ROOT", Path.Combine(LocalAppData, AppDataPaths.ProductFolder));
            InstallFinalizer.UserEnvironment = UserEnvironment;

            Log.Info($"[TestRunIsolation] RedirectEverything: run={RunRoot}");
            PruneOldRuns(RunsParent, now, KeepRunsFor);
        }

        /// <summary>
        /// Delete earlier run folders under <paramref name="parent"/>. A folder is deleted ONLY when its
        /// name parses as <c>run-yyyyMMdd-HHmmss-pid</c> (so this class made it) AND that stamp is older
        /// than <paramref name="keep"/>. Anything else in the folder is left alone. Returns what it
        /// deleted. A folder that cannot be deleted (a run still holding its log open) is logged and
        /// kept - it is retried by the next run.
        /// </summary>
        internal static IReadOnlyList<string> PruneOldRuns(string parent, DateTime now, TimeSpan keep)
        {
            var deleted = new List<string>();
            if (!Directory.Exists(parent)) return deleted;

            foreach (string dir in Directory.GetDirectories(parent, RunPrefix + "*"))
            {
                DateTime? stamp = RunStamp(Path.GetFileName(dir));
                if (stamp == null || now - stamp.Value <= keep) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    deleted.Add(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"[TestRunIsolation] PruneOldRuns: kept {dir} ({ex.Message})");
                }
            }
            Log.Info($"[TestRunIsolation] PruneOldRuns: deleted={deleted.Count} parent={parent}");
            return deleted;
        }

        /// <summary>The start time encoded in a run folder's name, or null when the name is not one
        /// this class produces (<c>run-yyyyMMdd-HHmmss-pid</c>, pid all digits). Pure.</summary>
        internal static DateTime? RunStamp(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(RunPrefix, StringComparison.Ordinal)) return null;
            string rest = name.Substring(RunPrefix.Length);
            if (rest.Length <= StampFormat.Length + 1 || rest[StampFormat.Length] != '-') return null;
            string pid = rest.Substring(StampFormat.Length + 1);
            foreach (char c in pid) if (c < '0' || c > '9') return null;
            return DateTime.TryParseExact(rest.Substring(0, StampFormat.Length), StampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at) ? at : null;
        }
    }

    /// <summary>A user environment that lives in this process only - what the setup engine writes to
    /// during a test run instead of HKCU\Environment.</summary>
    internal sealed class InMemoryUserEnvironment : IUserEnvironment
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public string? Get(string name)
        {
            lock (_gate) return _values.TryGetValue(name, out string? v) ? v : null;
        }

        public void Set(string name, string? value)
        {
            lock (_gate)
            {
                if (value == null) _values.Remove(name);
                else _values[name] = value;
            }
        }
    }
}
