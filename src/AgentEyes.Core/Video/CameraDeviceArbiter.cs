using System;
using System.Collections.Generic;

namespace AgentEyes.Video
{
    /// <summary>
    /// The one place that says "a recording is about to open this camera - let go of it NOW".
    ///
    /// A DirectShow camera on Windows is EXCLUSIVE: one graph holds the device and everything else
    /// is refused. The preset editor's live preview (issue #29) therefore cannot simply keep the
    /// device while the user starts a recording - the recording's own open would fail, and by issue
    /// #28 decision 3 a camera that cannot be opened fails the WHOLE recording start. A preview that
    /// can do that to a recording is worse than no preview at all.
    ///
    /// The release is hooked at the ONE place a camera is opened for recording -
    /// <see cref="FfmpegCameraRecorder.Start"/> - rather than at each recording entry point (the
    /// launcher, the tray, POST /record/start). A new recording path cannot forget to stop the
    /// preview, because there is nothing for it to remember.
    ///
    /// WHAT THIS CANNOT DO, stated rather than hidden: it coordinates holders INSIDE this process.
    /// The CLI (agenteyes.exe) runs in its own process and cannot ask the tray app's preview to let
    /// go; a recording started there while a preview is running fails loudly with "already in use by
    /// another application" (<see cref="FfmpegCameraRecorder.DiagnoseOpenFailure"/>). That is the
    /// honest outcome - a named failure, never a silent screen-only recording.
    /// </summary>
    internal static class CameraDeviceArbiter
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// Everything in this process that may be holding a camera. Each returns true when it
        /// actually released a device, so the log can say whether anything was let go.
        /// </summary>
        private static readonly List<Func<string, bool>> Holders = new List<Func<string, bool>>();

        /// <summary>How many holders are currently registered (diagnostics and tests).</summary>
        public static int HolderCount
        {
            get { lock (Gate) { return Holders.Count; } }
        }

        /// <summary>
        /// Register something that may be holding a camera. The callback is invoked SYNCHRONOUSLY on
        /// the thread that is starting a recording and must have released the device by the time it
        /// returns; it must not need the UI thread, or a busy UI would deadlock a recording start.
        /// </summary>
        public static void Register(Func<string, bool> holder)
        {
            if (holder == null) throw new ArgumentNullException(nameof(holder));
            lock (Gate)
            {
                if (Holders.Contains(holder)) return;
                Holders.Add(holder);
            }
            Log.Info($"[CameraDeviceArbiter] Register: {HolderCount} camera holder(s) registered");
        }

        /// <summary>Stop asking this holder to release (its owner is gone).</summary>
        public static void Unregister(Func<string, bool> holder)
        {
            if (holder == null) throw new ArgumentNullException(nameof(holder));
            bool removed;
            lock (Gate) { removed = Holders.Remove(holder); }
            if (removed) Log.Info($"[CameraDeviceArbiter] Unregister: {HolderCount} camera holder(s) remain");
        }

        /// <summary>
        /// Tell every registered holder to release its camera before a recording opens
        /// <paramref name="deviceName"/>. Returns how many holders actually released something.
        ///
        /// The release is unconditional - a holder is asked to let go even when it is showing a
        /// DIFFERENT camera. The two failure directions are not symmetric: releasing a preview that
        /// did not need releasing costs the user a preview they were about to lose anyway, while
        /// keeping one because two device names were compared and judged different costs them the
        /// recording. The requested name is passed through and logged so the holder can report what
        /// it dropped.
        /// </summary>
        public static int ReleaseForRecording(string deviceName)
        {
            Func<string, bool>[] holders;
            lock (Gate) { holders = Holders.ToArray(); }

            if (holders.Length == 0)
            {
                Log.Info($"[CameraDeviceArbiter] ReleaseForRecording: camera=\"{deviceName}\" - nothing else holds a camera");
                return 0;
            }

            int released = 0;
            foreach (var holder in holders)
            {
                // This is the boundary where the engine calls back into whatever registered itself,
                // so it catches here (CLAUDE.md standard 4: try-catch at entry points). A holder that
                // throws has NOT released, and nothing here pretends otherwise: the failure is logged
                // as an error and the camera open that follows fails loudly with "already in use",
                // which is the accurate result rather than a swallowed one.
                try { if (holder(deviceName)) released++; }
                catch (Exception ex)
                {
                    Log.Error($"[CameraDeviceArbiter] ReleaseForRecording: a camera holder FAILED to release " +
                              $"\"{deviceName}\" - the camera open that follows will report it as in use", ex);
                }
            }

            Log.Info($"[CameraDeviceArbiter] ReleaseForRecording: camera=\"{deviceName}\" asked {holders.Length} " +
                     $"holder(s), {released} released");
            return released;
        }
    }
}
