using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentEyes
{
    /// <summary>
    /// The last-resort manifest for a recording whose normal manifest save failed (issue #153).
    ///
    /// Raw capture bytes with no manifest.json beside them are invisible: the Library skips the
    /// directory, and every recovery pass in the app decides what is outstanding from the artifacts
    /// PLUS that file (<see cref="PostRecordingPlan"/>, <see cref="TranscriptionBacklog"/>). So when
    /// the normal save fails and the capture files are already on disk, a REDUCED record is written
    /// instead of leaving the recording orphaned.
    ///
    /// Reduced on purpose - this record exists to be written when the full one could not be, so it
    /// carries only what recovery needs:
    ///  - the identity of the recording (mode, label, created, monitor, mic, region),
    ///  - the media it produced (<see cref="Manifest.VideoFile"/> / <see cref="Manifest.AudioFile"/>)
    ///    and the files actually found in the directory,
    ///  - and <see cref="Manifest.PendingMux"/>, which is not optional: a deferred mux means the
    ///    final media file does not exist yet, and without that record a raw.mp4 + sys_native.wav
    ///    recording is not recoverable by anything.
    ///
    /// It deliberately drops the decorative and regenerable parts - the post-processing journal, the
    /// ffmpeg command line, the AI cost/title fields and the shot index. The shot PNGs themselves
    /// stay on disk; only their offsets are lost, and only on a path where the full save had already
    /// failed.
    ///
    /// This is not a silent fallback: it is written only after the real failure has been logged and
    /// recorded, and the stop still reports itself as failed (see
    /// <see cref="RecordingStopSequence"/>).
    /// </summary>
    internal static class RecoveryManifest
    {
        /// <summary>
        /// Build the reduced record for <paramref name="dir"/> from the live manifest.
        /// </summary>
        public static Manifest From(Manifest source, double durationSeconds, string dir)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));

            var recovery = new Manifest
            {
                Mode = source.Mode,
                Label = source.Label,
                DisplayName = source.DisplayName,
                CreatedUtc = source.CreatedUtc,
                MonitorIndex = source.MonitorIndex,
                MonitorName = source.MonitorName,
                Region = source.Region,
                Microphone = source.Microphone,
                Imported = source.Imported,
                ImportedSource = source.ImportedSource,
                DurationSeconds = Math.Round(durationSeconds, 2),
                VideoFile = source.VideoFile,
                AudioFile = source.AudioFile,
                PendingMux = source.PendingMux,
            };

            foreach (string name in FilesOnDisk(dir)) recovery.Files.Add(name);
            return recovery;
        }

        /// <summary>
        /// Write the reduced record over <c>manifest.json</c> in <paramref name="dir"/>. Throws when
        /// it cannot be written - the caller records that as its own failure rather than pretending
        /// the recording was saved.
        /// </summary>
        public static void Save(Manifest source, double durationSeconds, string dir)
        {
            Log.Info($"[RecoveryManifest] Save: writing the reduced recovery record for {dir}");
            var recovery = From(source, durationSeconds, dir);
            // A deliberate whole-content write (issue #155): the REDUCED record replaces whatever is
            // there, because the normal save has already failed and this is the last-resort record.
            ManifestStore.Replace(dir, recovery);
            Log.Warn($"[RecoveryManifest] Save: {dir} was saved with the REDUCED recovery record " +
                     $"(files={recovery.Files.Count}, pendingMux={(recovery.PendingMux != null ? "yes" : "no")}) " +
                     "because the normal manifest save failed");
        }

        /// <summary>The capture files in the recording directory itself (not the shots subfolder),
        /// excluding the manifest and any manifest write-temp a killed process left behind (issue
        /// #155 writes manifest.json.&lt;id&gt;.tmp and renames it into place). Read from disk rather
        /// than from the failed manifest, because what is actually there is what has to be
        /// recoverable.</summary>
        private static IEnumerable<string> FilesOnDisk(string dir)
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name)
                            && !name!.StartsWith(ManifestStore.FileName, StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
