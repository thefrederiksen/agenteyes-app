using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentEyes
{
    /// <summary>
    /// One named unit of shutdown in a stop: a writer to stop and then dispose (issue #153).
    /// Stop and Dispose are separate delegates because they fail separately - a WAV writer whose
    /// flush throws must still be disposed, or the file handle stays open for the life of the
    /// process.
    /// </summary>
    internal sealed class RecordingStopStep
    {
        public RecordingStopStep(string name, Action? stop, Action? dispose)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("a stop step must be named - the name is what the failure is reported as", nameof(name));
            Name = name;
            Stop = stop;
            Dispose = dispose;
        }

        /// <summary>What this step shuts down ("audio", "loopback", "video") - used in the log line,
        /// the report, and the exception message.</summary>
        public string Name { get; }

        public Action? Stop { get; }
        public Action? Dispose { get; }
    }

    /// <summary>One thing that went wrong during a stop (issue #153).</summary>
    internal sealed class RecordingStopFailure
    {
        public RecordingStopFailure(string stage, Exception error)
        {
            Stage = stage ?? throw new ArgumentNullException(nameof(stage));
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        /// <summary>Where it failed: "audio stop", "video dispose", "manifest save", ... </summary>
        public string Stage { get; }

        public Exception Error { get; }

        public override string ToString() => $"{Stage}: {Error.Message}";
    }

    /// <summary>
    /// What a stop actually managed to do (issue #153). A stop is no longer all-or-nothing, so the
    /// caller needs more than "it threw": which steps failed, and whether the recording ended up
    /// with a manifest on disk.
    /// </summary>
    internal sealed class RecordingStopReport
    {
        public RecordingStopReport(string dir, IReadOnlyList<RecordingStopFailure> failures,
            bool manifestSaved, bool recoveryManifestSaved)
        {
            Dir = dir;
            Failures = failures ?? throw new ArgumentNullException(nameof(failures));
            ManifestSaved = manifestSaved;
            RecoveryManifestSaved = recoveryManifestSaved;
        }

        /// <summary>The recording directory this stop belonged to.</summary>
        public string Dir { get; }

        /// <summary>Every failure hit, in the order they happened - never just the first.</summary>
        public IReadOnlyList<RecordingStopFailure> Failures { get; }

        /// <summary>True when the normal manifest save succeeded.</summary>
        public bool ManifestSaved { get; }

        /// <summary>True when the normal save failed and the reduced recovery record was written
        /// instead, so the directory is still discoverable by the recovery passes.</summary>
        public bool RecoveryManifestSaved { get; }

        /// <summary>True when the recording has a manifest on disk by either route.</summary>
        public bool HasManifest => ManifestSaved || RecoveryManifestSaved;

        public bool Failed => Failures.Count > 0;

        /// <summary>Every failure on one ASCII line, for a log entry or an exception message.</summary>
        public string Summary() => string.Join("; ", Failures.Select(f => f.ToString()));
    }

    /// <summary>
    /// Raised by <see cref="RecordingService.Stop"/> when any part of the stop failed (issue #153).
    ///
    /// A stop used to throw the FIRST exception it hit and abandon everything after it. It now runs
    /// every step and throws this at the end, so the caller learns about all of the failures, the
    /// recording directory they belong to, and whether a manifest reached disk - a failed stop is
    /// never mistaken for a clean one.
    /// </summary>
    internal sealed class RecordingStopFailedException : Exception
    {
        public RecordingStopFailedException(RecordingStopReport report)
            : base(BuildMessage(report), report.Failures.Count > 0 ? report.Failures[0].Error : null)
        {
            Report = report;
        }

        public RecordingStopReport Report { get; }

        public string Dir => Report.Dir;

        private static string BuildMessage(RecordingStopReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            string manifest = report.ManifestSaved
                ? "the manifest was saved"
                : report.RecoveryManifestSaved
                    ? "a recovery manifest was written"
                    : "NO manifest reached disk";
            return $"stopping the recording in {report.Dir} failed ({manifest}): {report.Summary()}";
        }
    }

    /// <summary>
    /// The failure-isolated raw stop (issue #153).
    ///
    /// The defect this replaces: <see cref="RecordingService.Stop"/> stopped and disposed the audio
    /// capture, the loopback capture and the video writer in ONE try block and saved the manifest at
    /// the end of it. The first throw abandoned every later writer and the manifest save - yet the
    /// finally block still cleared the session and returned the service to idle. The app reported
    /// itself ready while the recording had no manifest and a writer could still be open, and the
    /// callers hid it (an empty catch in tray Quit, an unlogged one in the window).
    ///
    /// So every step here runs in its own protected block, all failures are COLLECTED rather than
    /// the first one thrown, and the manifest is saved even when a writer failed - because the raw
    /// bytes are on disk by then and a manifest is what makes them recoverable. If the normal save
    /// itself fails and raw artifacts exist, a reduced recovery record is written so the directory
    /// is still found by the existing artifact-based recovery passes
    /// (<see cref="PostRecordingPlan"/> / <see cref="TranscriptionBacklog"/>).
    ///
    /// The try/catch-per-step is deliberate and is not the catch-and-continue the coding standards
    /// forbid: nothing is hidden. Every failure is logged with the recording directory, carried in
    /// the <see cref="RecordingStopReport"/>, and raised to the caller as a
    /// <see cref="RecordingStopFailedException"/>. This class IS the entry point for each step.
    ///
    /// Injectable by construction: the steps and the two save actions are delegates, so a test can
    /// fail any position (audio stop, loopback stop, video stop, manifest save) without a sound
    /// card, ffmpeg, or a full disk.
    /// </summary>
    internal static class RecordingStopSequence
    {
        /// <summary>
        /// Run the raw stop: every step, then the manifest, collecting every failure.
        /// </summary>
        /// <param name="dir">The recording directory (used in every log line and in the report).</param>
        /// <param name="steps">The writers to stop and dispose, in shutdown order.</param>
        /// <param name="saveManifest">Populate and save the recording's manifest.</param>
        /// <param name="saveRecoveryManifest">Write the reduced recovery record. Called ONLY when
        /// <paramref name="saveManifest"/> failed and raw artifacts exist in the directory.</param>
        public static RecordingStopReport Run(
            string dir,
            IReadOnlyList<RecordingStopStep> steps,
            Action saveManifest,
            Action saveRecoveryManifest)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            if (saveManifest == null) throw new ArgumentNullException(nameof(saveManifest));
            if (saveRecoveryManifest == null) throw new ArgumentNullException(nameof(saveRecoveryManifest));

            Log.Info($"[RecordingStopSequence] Run: dir={dir} steps={steps.Count}");
            var failures = new List<RecordingStopFailure>();

            StopWriters(dir, steps, failures);

            bool manifestSaved = Attempt(failures, dir, "manifest save", saveManifest);
            bool recoverySaved = false;
            if (!manifestSaved)
            {
                if (HasRawArtifacts(dir))
                {
                    Log.Warn($"[RecordingStopSequence] Run: the manifest save failed but raw artifacts exist in {dir} - writing the recovery record");
                    recoverySaved = Attempt(failures, dir, "recovery manifest save", saveRecoveryManifest);
                }
                else
                {
                    Log.Warn($"[RecordingStopSequence] Run: the manifest save failed and {dir} holds no raw artifacts - there is nothing to recover");
                }
            }

            var report = new RecordingStopReport(dir, failures, manifestSaved, recoverySaved);
            if (report.Failed)
                Log.Error($"[RecordingStopSequence] Run FAILED: dir={dir} failures={failures.Count} manifest={(report.HasManifest ? "on disk" : "MISSING")} - {report.Summary()}");
            else
                Log.Info($"[RecordingStopSequence] Run: dir={dir} completed with no failures");
            return report;
        }

        /// <summary>
        /// Stop and dispose every writer, each in its own protected block, collecting failures into
        /// <paramref name="failures"/> rather than letting the first one abandon the rest.
        ///
        /// Issue #155: this is shared with the START-failure cleanup
        /// (<see cref="RecordingStartSequence"/>), which has exactly the same job - get every writer
        /// that may have started off the microphone, the loopback device and ffmpeg - and must not be
        /// a second, subtly different implementation of it. A writer left running after a failed
        /// start is a recorder that captures while the app reports idle.
        /// </summary>
        public static void StopWriters(string dir, IReadOnlyList<RecordingStopStep> steps,
            ICollection<RecordingStopFailure> failures)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("a recording directory is required", nameof(dir));
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            if (failures == null) throw new ArgumentNullException(nameof(failures));

            foreach (var step in steps)
            {
                if (step == null) throw new ArgumentException("a stop step is null", nameof(steps));
                Attempt(failures, dir, step.Name + " stop", step.Stop);
                // Dispose runs even when Stop threw: an undisposed writer keeps its file handle (and
                // its device) for the life of the process, which is how "record again" fails next.
                Attempt(failures, dir, step.Name + " dispose", step.Dispose);
            }
        }

        /// <summary>
        /// True when <paramref name="dir"/> holds capture bytes worth recovering - any file that is
        /// neither the manifest nor one of its write-temps. This is what decides whether a failed
        /// manifest save is worth a recovery record, and (issue #155) whether a session that failed
        /// to start captured anything before it did: an empty directory has nothing to point at.
        ///
        /// A manifest.json.&lt;id&gt;.tmp is deliberately NOT capture bytes. It is the litter of a
        /// write that failed, nothing reads it, and counting it as an artifact would make a recording
        /// that captured nothing look worth keeping - which is how a failed start used to leave an
        /// empty directory and a stray temp behind.
        /// </summary>
        public static bool HasRawArtifacts(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!IsTheManifestOrItsWriteTemp(Path.GetFileName(file))) return true;
            }
            return false;
        }

        /// <summary>manifest.json itself, or a manifest.json.&lt;id&gt;.tmp left by a failed write.</summary>
        private static bool IsTheManifestOrItsWriteTemp(string name) =>
            string.Equals(name, ManifestStore.FileName, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith(ManifestStore.FileName + ".", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Run one step. Returns true when it succeeded; on a failure it records it, logs it WITH the
        /// recording directory, and returns false so the sequence carries on to the next step.
        /// </summary>
        private static bool Attempt(ICollection<RecordingStopFailure> failures, string dir, string stage, Action? action)
        {
            if (action == null) return true;
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                failures.Add(new RecordingStopFailure(stage, ex));
                Log.Error($"[RecordingStopSequence] {stage} FAILED: dir={dir}", ex);
                return false;
            }
        }
    }
}
