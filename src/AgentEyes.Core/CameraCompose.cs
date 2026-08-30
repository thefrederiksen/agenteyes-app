using System;
using System.IO;
using AgentEyes.Preview;
using AgentEyes.Video;

namespace AgentEyes
{
    /// <summary>
    /// Issue #47: renders the webcam into the screen recording, so the framing a person chose before
    /// recording actually reaches the video they end up with.
    ///
    /// Before this, the shape/corner/inset chosen in the preset editor drove the LIVE PREVIEW and
    /// was written to manifest.json as edit metadata for "a later edit" (issues #33 and #36). The
    /// later edit did not exist, so camera.mp4 sat beside recording.mp4 forever and the corner
    /// setting silently did nothing to the output.
    ///
    /// What this does NOT do, and both are load-bearing:
    ///  - it never rewrites camera.mp4, which stays the full rectangular frame (issue #36, E1), so
    ///    the framing can be changed and the compose re-run;
    ///  - it never touches the audio, which is copied through from the screen recording.
    /// </summary>
    internal static class CameraCompose
    {
        /// <summary>The screen-only video, kept beside the composed one rather than overwritten.</summary>
        public const string ScreenOnlyFile = "recording.screen.mp4";

        /// <summary>Quality of the composed output.</summary>
        public const int Crf = 23;

        /// <summary>What a compose attempt did.</summary>
        public enum Outcome
        {
            /// <summary>Composed - recording.mp4 now has the camera in it.</summary>
            Composed,

            /// <summary>Nothing to do: this recording has no camera track.</summary>
            NoCamera,

            /// <summary>A camera was recorded but no framing was chosen, so there is no layout to render.</summary>
            NoFraming,
        }

        /// <summary>
        /// Compose the recording in <paramref name="dir"/>.
        ///
        /// Returns what it did rather than throwing for the ordinary "this recording has no camera"
        /// case, so the post-recording sequence can skip quietly while a person typing
        /// <c>agenteyes compose</c> gets told plainly.
        /// </summary>
        public static Outcome Run(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("required", nameof(dir));
            if (!Directory.Exists(dir)) throw new UsageException($"no such recording directory: {dir}");

            Log.Info($"[CameraCompose] Run: dir={dir}");

            var manifest = Manifest.Load(dir);

            if (string.IsNullOrWhiteSpace(manifest.CameraFile))
            {
                Log.Info("[CameraCompose] Run: no camera track - nothing to compose");
                return Outcome.NoCamera;
            }

            var overlay = FramingOf(manifest);
            if (overlay == null)
            {
                Log.Info("[CameraCompose] Run: no framing recorded - nothing to lay out");
                return Outcome.NoFraming;
            }

            string screen = Path.Combine(dir, manifest.VideoFile ?? "recording.mp4");
            string camera = Path.Combine(dir, manifest.CameraFile!);
            RequireFile(screen, "the screen recording");
            RequireFile(camera, "the camera track");

            var (screenW, screenH) = MediaProbe.VideoSize(screen);
            var (cameraW, cameraH) = MediaProbe.VideoSize(camera);
            var composition = CameraComposition.For(screenW, screenH, cameraW, cameraH, overlay);

            Log.Info($"[CameraCompose] Run: screen={screenW}x{screenH} camera={cameraW}x{cameraH} -> {composition}");

            string? mask = null;
            string composed = Path.Combine(dir, "recording.composed.tmp.mp4");
            try
            {
                if (composition.Circular)
                {
                    mask = Path.Combine(dir, "camera.mask.tmp.png");
                    CircleMask.Write(mask, composition.InsetWidth);
                }

                Ffmpeg.Run(
                    ComposeArgs.CameraInset(
                        screen, camera, mask, composed, composition,
                        manifest.CameraStartOffsetSeconds ?? 0.0, Crf),
                    "compose camera inset");

                Swap(dir, screen, composed);
            }
            finally
            {
                // The mask is scaffolding, not an artifact of the recording.
                if (mask != null && File.Exists(mask)) File.Delete(mask);
                if (File.Exists(composed)) File.Delete(composed);
            }

            ManifestStore.Update(dir, m =>
            {
                if (!m.Files.Contains(ScreenOnlyFile)) m.Files.Add(ScreenOnlyFile);
                if (!m.OriginalFiles.Contains(ScreenOnlyFile)) m.OriginalFiles.Add(ScreenOnlyFile);
                m.ComposedCamera = true;
            });

            Log.Info($"[CameraCompose] Run: composed; the screen-only cut is {ScreenOnlyFile}");
            return Outcome.Composed;
        }

        /// <summary>
        /// Put the composed video in place as recording.mp4 and keep the screen-only one beside it.
        ///
        /// The screen-only cut is <see cref="ScreenOnlyFile"/> and NOT "recording.original.mp4",
        /// because that name is already taken: issue #83 uses it for the capture as it was BEFORE
        /// audio processing. Two different "originals" under one name would make it impossible to say
        /// which one a directory held.
        /// </summary>
        private static void Swap(string dir, string screen, string composed)
        {
            if (!File.Exists(composed) || new FileInfo(composed).Length == 0)
                throw new UsageException(
                    "the compose step produced no output; recording.mp4 has been left untouched");

            string screenOnly = Path.Combine(dir, ScreenOnlyFile);

            // Re-composing a directory that was already composed must not bury the screen-only cut
            // under a composed one - the first preserved copy is the real screen-only video.
            if (!File.Exists(screenOnly)) File.Move(screen, screenOnly);
            else File.Delete(screen);

            File.Move(composed, screen);
        }

        /// <summary>
        /// The framing this recording was made with, or null when none was recorded.
        ///
        /// A rectangle needs no circle, so a missing circle block is only fatal for a circle.
        /// </summary>
        private static CameraOverlaySettings? FramingOf(Manifest m)
        {
            if (string.IsNullOrWhiteSpace(m.PreviewOverlayCorner)
                || string.IsNullOrWhiteSpace(m.PreviewOverlayShape))
            {
                return null;
            }

            var overlay = new CameraOverlaySettings
            {
                Corner = m.PreviewOverlayCorner!,
                Shape = m.PreviewOverlayShape!,
                InsetFraction = m.PreviewOverlayInset ?? CameraOverlaySettings.DefaultInsetFraction,
                Circle = m.PreviewOverlayCircle?.Clone() ?? new CameraOverlayCircle(),
            };
            return overlay.Canonical();
        }

        private static void RequireFile(string path, string what)
        {
            if (!File.Exists(path))
                throw new UsageException($"{what} is missing: {path}");
        }
    }
}
