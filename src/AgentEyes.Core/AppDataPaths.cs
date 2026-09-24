using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// The ONE place AgentEyes asks Windows where the per-user folders are (issue #78):
    ///
    ///   %LOCALAPPDATA%\AgentEyes      - config, presets, logs, always-on work, preview, models, plugins
    ///   %USERPROFILE%\Videos\AgentEyes - recordings and always-on clips
    ///
    /// Every product path to that state is built from here. That is what lets a TEST HOST move all of
    /// it with one call before anything is touched: a test run on a machine where the installed app is
    /// running must never write into that app's log, config, presets, preview frames or always-on work
    /// folder (on 2026-09-24 it did, and the live log became unreadable).
    ///
    /// The redirect is set ONCE per process, and only before any redirected path has been handed out -
    /// a redirect that arrives after some static field already captured the real path would leave that
    /// field pointing at the user's real state while everything else looked isolated, so it throws
    /// instead. The product never calls it; nothing in a normal run changes. The IL guard in
    /// <c>TestIsolationTests</c> pins that no other method in the product asks Windows for these folders.
    /// </summary>
    internal static class AppDataPaths
    {
        /// <summary>The folder name AgentEyes owns under both roots.</summary>
        public const string ProductFolder = "AgentEyes";

        private static readonly object Gate = new();
        private static volatile string? _localAppDataOverride;
        private static volatile string? _videosOverride;
        private static volatile bool _handedOut;

        /// <summary>The machine's real %LOCALAPPDATA%, NEVER redirected. Only for locating things that
        /// are not AgentEyes state (the winget package folder ffmpeg may live in), and for a test to
        /// name the real location it must stay out of.</summary>
        public static string MachineLocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>%LOCALAPPDATA%, or the redirected stand-in for it in a test host.</summary>
        public static string LocalAppData
        {
            get
            {
                _handedOut = true;
                return _localAppDataOverride ?? MachineLocalAppData;
            }
        }

        /// <summary>The user's Videos folder, or the redirected stand-in for it in a test host.</summary>
        public static string Videos
        {
            get
            {
                _handedOut = true;
                return _videosOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }
        }

        /// <summary>%LOCALAPPDATA%\AgentEyes - the root of every piece of per-user state.</summary>
        public static string Root => Path.Combine(LocalAppData, ProductFolder);

        /// <summary>%USERPROFILE%\Videos\AgentEyes - where recordings live.</summary>
        public static string RecordingsRoot => Path.Combine(Videos, ProductFolder);

        /// <summary>True once <see cref="RedirectForThisProcess"/> has moved the roots.</summary>
        public static bool IsRedirected => _localAppDataOverride != null;

        /// <summary>
        /// Move both roots for the rest of this process. For a TEST HOST only: call it before anything
        /// else in the process resolves a path. Throws when a path was already handed out (some caller
        /// may be holding the real one), when the roots are already redirected elsewhere, or when a
        /// root is not an absolute path.
        /// </summary>
        public static void RedirectForThisProcess(string localAppData, string videos)
        {
            if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
                throw new ArgumentException($"the stand-in for %LOCALAPPDATA% must be an absolute path (got '{localAppData}')", nameof(localAppData));
            if (string.IsNullOrWhiteSpace(videos) || !Path.IsPathFullyQualified(videos))
                throw new ArgumentException($"the stand-in for the Videos folder must be an absolute path (got '{videos}')", nameof(videos));

            lock (Gate)
            {
                if (_localAppDataOverride != null)
                    throw new InvalidOperationException(
                        $"AgentEyes' data folders are already redirected to {_localAppDataOverride}; a process gets one redirect.");
                if (_handedOut)
                    throw new InvalidOperationException(
                        "An AgentEyes data path was resolved before the redirect. Something may already hold the REAL "
                        + "%LOCALAPPDATA%\\AgentEyes path, so redirecting now would only look isolated. Redirect first.");

                _videosOverride = videos;
                _localAppDataOverride = localAppData;
            }

            Log.Info($"[AppDataPaths] RedirectForThisProcess: localAppData={localAppData} videos={videos}");
        }
    }
}
