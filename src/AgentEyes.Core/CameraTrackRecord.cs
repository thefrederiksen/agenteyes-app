using System;
using AgentEyes.Video;

namespace AgentEyes
{
    /// <summary>
    /// The ONE place the camera track's record is written into a manifest (issue #28, spec
    /// amendment 2026-08-28).
    ///
    /// WHY IT IS ONE PLACE. Two writers reach this record - the service's stop and the CLI's video
    /// command - and while each of them assigned the fields itself, "the manifest says what the
    /// recorder observed" was a habit rather than a structure. A literal in either
    /// (<c>CameraComplete = "yes"</c>) would put the original defect straight back into the file the
    /// user keeps while every behavioural test on the recorder still passed, and it is exactly the
    /// shape a later tidy-up produces.
    ///
    /// So the assignment lives here, alone, and the guard around it is structural: nothing else in
    /// the product may set these manifest properties, and this method must read all four
    /// observations off the recorder. Both are read out of the compiled IL by
    /// <c>CameraFailurePathTests</c>, so an alias, a helper or a local cannot hide a missing read.
    ///
    /// Every field is an OBSERVATION except <see cref="Manifest.CameraComplete"/>, which is the
    /// recorder's own three-state verdict - and this method neither widens nor narrows it.
    /// </summary>
    internal static class CameraTrackRecord
    {
        /// <summary>The camera track's file name - the same in every recording directory.</summary>
        public const string FileName = "camera.mp4";

        public static void Write(Manifest manifest, FfmpegCameraRecorder camera)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            manifest.CameraFile = FileName;
            manifest.CameraCapturedSeconds = Math.Round(camera.CapturedSeconds, 2);
            manifest.CameraStopKind = CameraObservation.Text(camera.StopKind);
            manifest.CameraStderrComplete = camera.StderrComplete;
            manifest.CameraComplete = CameraObservation.Text(camera.Completeness);
            if (!manifest.Files.Contains(FileName)) manifest.Files.Add(FileName);

            Log.Info($"[CameraTrackRecord] Write: camera=\"{camera.DeviceName}\" captured="
                     + $"{manifest.CameraCapturedSeconds}s stopKind={manifest.CameraStopKind ?? "(not observed)"} "
                     + $"stderrComplete={manifest.CameraStderrComplete} complete={manifest.CameraComplete}");
        }

        /// <summary>
        /// Copy the camera track's record from the session's manifest onto the one being read back
        /// off disk (issue #155: the stop is a read-modify-write of the record the START wrote, not
        /// a whole-content replace).
        ///
        /// It lives here for the same reason <see cref="Write"/> does: this is the second and last
        /// place these fields are assigned, and putting it anywhere else would give the verdict a
        /// second chance to become something other than what the recorder observed. It COPIES and
        /// does nothing else - no rounding, no defaulting, no "if it is null, assume".
        /// </summary>
        public static void CopyTo(Manifest target, Manifest source)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (source == null) throw new ArgumentNullException(nameof(source));

            target.CameraFile = source.CameraFile;
            target.CameraStartOffsetSeconds = source.CameraStartOffsetSeconds;
            target.CameraCapturedSeconds = source.CameraCapturedSeconds;
            target.CameraStopKind = source.CameraStopKind;
            target.CameraStderrComplete = source.CameraStderrComplete;
            target.CameraComplete = source.CameraComplete;
        }
    }
}
