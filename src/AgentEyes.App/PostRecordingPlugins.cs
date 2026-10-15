using System;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// The app-level part of the post-recording sequence: the plugin step (issue #13). It was
    /// RecordingStop.Configure until issue #102 moved RecordingStop itself into Core so the headless
    /// Linux service shares the one stop operation; the plugins stay here because they read the
    /// app's Config.
    /// </summary>
    internal static class PostRecordingPlugins
    {
        /// <summary>
        /// Wire the app-level parts of the post-recording sequence ONCE, at startup, so they apply
        /// on every stop path including a process that never builds a window. Called from
        /// App.OnStartup.
        /// </summary>
        public static void Configure(Config cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            Log.Info("[PostRecordingPlugins] Configure: registering the post-packaging plugin step");

            // Post-recording plugins (issue #13). Registered here rather than passed in per stop:
            // a caller that can forget to pass it is the bug RecordingStop exists to prevent.
            PostRecording.AfterPackaging = (dir, progress) =>
            {
                if (cfg.EnabledPlugins.Count == 0) return;
                Plugins.RunEnabledAsync(dir, cfg, progress).GetAwaiter().GetResult();
            };
        }
    }
}
