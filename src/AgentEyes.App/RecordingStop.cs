using System;
using System.IO;
using System.Threading.Tasks;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Progress sinks for <see cref="RecordingStop.Keep"/>. Every callback arrives on a BACKGROUND
    /// thread (the stop and the post-recording sequence never touch the UI thread), so a WPF
    /// subscriber marshals for itself - see MainWindow.StopAsync for the pattern.
    ///
    /// This is how a window shows the staged sequence without owning a private copy of it, which is
    /// the trap issue #151 closed: the window's duplicate had drifted away from the shared one, and
    /// the tray had no copy at all.
    /// </summary>
    internal sealed class StopProgress
    {
        /// <summary>The raw flush ("Saving video..." / "Saving audio..."). Fires before and, for
        /// video, during <see cref="RecordingService.Stop"/>.</summary>
        public Action<string>? Saving { get; set; }

        /// <summary>The raw files and manifest are on disk and the recording exists in the library.
        /// Fires once, before post-processing starts, so a UI can show the row immediately.</summary>
        public Action<RecordResult>? Saved { get; set; }

        /// <summary>The post-recording sequence stages (<see cref="PostRecording.StageMixing"/> and
        /// friends, plus any plugin status), ending with <see cref="PostRecording.StageDone"/>.</summary>
        public Action<string>? Processing { get; set; }
    }

    /// <summary>A stopped recording: the raw result (already on disk) plus the post-recording work
    /// that is still running in the background. Awaiting <see cref="PostProcessing"/> is optional -
    /// it never faults, because <see cref="PostRecording.Run"/> logs and announces its own
    /// failures - but a caller that is about to shut the process down MUST await it.</summary>
    internal sealed class StoppedRecording
    {
        public StoppedRecording(RecordResult result, Task postProcessing)
        {
            Result = result;
            PostProcessing = postProcessing;
        }

        public RecordResult Result { get; }
        public Task PostProcessing { get; }
    }

    /// <summary>
    /// The ONE way the app stops a recording (issue #151).
    ///
    /// Before this existed, "stop" was written five times: the window's Stop button, the REST API,
    /// the tray menu, tray Quit, and the guided test panel. Each was a bare
    /// <see cref="RecordingService.Stop"/> plus whatever post-processing that author remembered, so
    /// a step added to one path silently skipped the others - three shipped defects in a row (#141,
    /// #142, #151). Stopping from the tray, which is the PRIMARY stop control because the app
    /// normally runs with --tray and never builds MainWindow, produced raw media and nothing else.
    ///
    /// So there are exactly three named operations here and no fourth way to stop:
    ///  - <see cref="Keep"/> - the user keeps this recording: stop, then run the full
    ///    post-recording sequence (<see cref="PostRecording.Run"/>). Every keeping caller uses it.
    ///  - <see cref="Discard"/> - the user threw this recording away: stop, then DELETE it.
    ///  - <see cref="StopWithoutPostProcessing"/> - a caller that deliberately wants raw files and
    ///    no pipeline (the guided test panel analyzes its own throwaway takes). It must say WHY.
    ///
    /// Skipping the pipeline is therefore always a NAMED decision that appears in the log, never an
    /// omission. StopPathTests enforces that no other file in the solution calls
    /// <see cref="RecordingService.Stop"/> at all.
    /// </summary>
    internal static class RecordingStop
    {
        /// <summary>
        /// Wire the app-level parts of the post-recording sequence ONCE, at startup, so they apply
        /// on every stop path including a process that never builds a window. Called from
        /// App.OnStartup.
        /// </summary>
        public static void Configure(Config cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            Log.Info("[RecordingStop] Configure: registering the post-packaging plugin step");

            // Post-recording plugins (issue #13). Registered here rather than passed in per stop:
            // a caller that can forget to pass it is the bug this class exists to prevent.
            PostRecording.AfterPackaging = (dir, progress) =>
            {
                if (cfg.EnabledPlugins.Count == 0) return;
                Plugins.RunEnabledAsync(dir, cfg, progress).GetAwaiter().GetResult();
            };
        }

        /// <summary>
        /// Stop a recording the user KEEPS, and run the post-recording sequence for it. This is the
        /// only stop entry point that keeps a recording - the window Stop button, the HUD, the tray
        /// menu, tray Quit and POST /record/stop all come through here.
        ///
        /// Blocking on the raw flush only (fast and bounded - issue #77), so the caller runs it on a
        /// background thread. Returns as soon as the raw files and manifest are on disk; the rest of
        /// the sequence (mux, thumbnail, transcript, title, plugins) runs on a background task
        /// exposed as <see cref="StoppedRecording.PostProcessing"/>.
        /// </summary>
        public static StoppedRecording Keep(RecordingService svc, StopProgress? progress = null)
        {
            if (svc == null) throw new ArgumentNullException(nameof(svc));

            // Captured BEFORE the stop: if the stop throws, the service has already forgotten the
            // session and this is the only way to name the recording in the error (issue #151 AC2).
            // One Status() call, not two - it scans the recordings root for the pending count.
            var status = svc.Status();
            string? dir = status.Dir;
            bool isVideo = status.Mode == "video";
            Log.Info($"[RecordingStop] Keep: dir={dir ?? "(none)"} mode={(isVideo ? "video" : "audio")}");

            // Issue #152: post-recording work is in flight from the moment this stop is DECIDED, not
            // from when the background task below happens to start running. RecordingService.Stop
            // raises RecordingStopped inside the call below and a deferred update restart listens to
            // it - without this ticket the process answers "no session active" in the gap between the
            // capture ending and the sequence starting, and restarts straight through the mux and the
            // transcription it had not begun yet.
            var work = PostRecording.TrackWork("keep " + (dir ?? "(pending)"));

            RecordResult result;
            try
            {
                // Issue #77: the stop only flushes the raw files (near-instant); the audio mux is
                // deferred to the sequence below. Stage the labels so the save is visible.
                Report(progress?.Saving, isVideo ? "Saving video..." : "Saving audio...");
                result = svc.Stop();
                if (isVideo) Report(progress?.Saving, "Saving audio...");
            }
            catch (Exception ex)
            {
                // No post-processing will follow a stop that threw, so the app must not be left
                // looking busy forever.
                work.Dispose();
                Log.Error($"[RecordingStop] Keep FAILED to stop the recording: dir={dir ?? "(unknown)"}", ex);
                // Issue #153: a failed stop is no longer the end of the recording. Say on the record
                // whether it still has a manifest, because that is what decides whether the periodic
                // recovery pass (issue #152) can finish it later.
                if (ex is RecordingStopFailedException stopFailure)
                    Log.Error($"[RecordingStop] Keep: {stopFailure.Dir} was left with " +
                              (stopFailure.Report.HasManifest
                                  ? "a manifest on disk - the recovery pass will finish it"
                                  : "NO manifest - it cannot be recovered automatically"));
                throw;
            }

            Report(progress?.Saved, result);

            Log.Info($"[RecordingStop] Keep: raw files saved, queuing post-processing for {result.Dir}");
            var post = Task.Run(() =>
            {
                try { PostRecording.Run(result.Dir, progress?.Processing); }
                finally { work.Dispose(); }
            });
            return new StoppedRecording(result, post);
        }

        /// <summary>
        /// Stop a recording the user THREW AWAY (the HUD's Discard): stop the engine, then delete
        /// the directory. Deliberately skips the post-recording sequence - there is nothing to
        /// transcribe or title because the recording is about to stop existing.
        /// Blocking; the caller runs it on a background thread.
        /// </summary>
        public static RecordResult Discard(RecordingService svc)
        {
            if (svc == null) throw new ArgumentNullException(nameof(svc));
            string? dir = svc.Status().Dir;
            Log.Info($"[RecordingStop] Discard: dir={dir ?? "(none)"}");

            RecordResult result;
            try
            {
                result = svc.Stop();
            }
            catch (Exception ex)
            {
                Log.Error($"[RecordingStop] Discard FAILED to stop the recording: dir={dir ?? "(unknown)"}", ex);
                throw;
            }

            if (Directory.Exists(result.Dir)) Directory.Delete(result.Dir, recursive: true);
            Log.Info($"[RecordingStop] Discard: deleted {result.Dir}");
            return result;
        }

        /// <summary>
        /// Stop a recording and deliberately leave it raw - no mux, no thumbnail, no transcript, no
        /// title. The guided test panel uses this: its takes exist only to be measured by the panel
        /// itself, and it runs its own analysis on the raw files.
        ///
        /// <paramref name="reason"/> is required and is logged, so a raw recording in the library is
        /// always traceable to a deliberate decision rather than to a forgotten call (issue #151).
        /// Blocking; the caller runs it on a background thread.
        /// </summary>
        public static RecordResult StopWithoutPostProcessing(RecordingService svc, string reason)
        {
            if (svc == null) throw new ArgumentNullException(nameof(svc));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("reason is required - a stop that skips post-processing must say why", nameof(reason));

            string? dir = svc.Status().Dir;
            Log.Info($"[RecordingStop] StopWithoutPostProcessing: dir={dir ?? "(none)"} reason={reason}");

            try
            {
                var result = svc.Stop();
                Log.Info($"[RecordingStop] StopWithoutPostProcessing: {result.Dir} left raw on purpose ({reason})");
                return result;
            }
            catch (Exception ex)
            {
                Log.Error($"[RecordingStop] StopWithoutPostProcessing FAILED: dir={dir ?? "(unknown)"}", ex);
                throw;
            }
        }

        /// <summary>Hands one value to a caller's reporting sink. A sink that throws must not take
        /// down the stop it is only describing.</summary>
        private static void Report<T>(Action<T>? sink, T value)
        {
            if (sink == null) return;
            try { sink(value); }
            catch (Exception ex) { Log.Error("[RecordingStop] progress sink FAILED", ex); }
        }
    }
}
