using System;
using System.Collections.Generic;
using AgentEyes.Video;

namespace AgentEyes
{
    /// <summary>
    /// What a stranded camera ffmpeg looks like from outside the process (issue #28, AC16) - one
    /// row of <c>GET /status</c>.
    ///
    /// The PID is the load-bearing field. "A camera process is stuck" is a sentence, not something a
    /// person can act on; "PID 24512 is stuck holding C:\...\camera.mp4" is what Task Manager,
    /// taskkill and Get-Process all take.
    /// </summary>
    internal sealed class StrandedCameraReport
    {
        /// <summary>The exact DirectShow device the live process still holds.</summary>
        public string Device { get; set; } = "";

        /// <summary>The operating system process id of the ffmpeg that will not die.</summary>
        public int? Pid { get; set; }

        /// <summary>The camera.mp4 it still owns and may still be writing.</summary>
        public string? Output { get; set; }

        /// <summary>The recording directory it is writing into - which is why that directory's claim
        /// is deliberately NOT released while this row exists.</summary>
        public string? Dir { get; set; }
    }

    /// <summary>
    /// The lifetime owner of a camera ffmpeg that AgentEyes could not kill (issue #28, spec
    /// amendment 2026-08-28, AC16).
    ///
    /// WHY THIS CLASS EXISTS, in the Review Gate's words: "keeping a handle inside an object that
    /// immediately becomes unreachable does not keep the process recoverable." The recorder had
    /// already been fixed to KEEP its process handle when a stop could not confirm ffmpeg dead - and
    /// it was still useless, because <see cref="RecordingService.Stop"/> cleared <c>_camera</c>,
    /// dropped the local, went idle and released the recording claim. The handle survived; nothing
    /// referenced the object holding it. So this is the reference.
    ///
    /// It does three things, and each of them is one of AC16's clauses:
    ///
    ///  1. RETAINS the recorder, so the process stays reachable and can be stopped again later.
    ///  2. KEEPS THAT RECORDING'S CLAIM. Releasing it would publish a directory a live ffmpeg is
    ///     still writing into to every automatic repair, packaging and transcription pass in the
    ///     app. A stop that could not stop the writer has not finished with the directory, and
    ///     saying otherwise is the same "we asked it to die, so it died" mistake one level up.
    ///  3. REPORTS it on <c>/status</c>, with the PID, so the failure is visible and actionable
    ///     rather than a line in a log file.
    ///
    /// The decision itself lives HERE, in one method each caller makes a single call to, rather than
    /// as an `if` at the two call sites. A branch at the call site is a branch that can be got wrong
    /// in one place and right in the other - and this exact rule has now been got wrong three times.
    /// </summary>
    internal sealed class StrandedCameraOwner
    {
        private sealed class Stranded
        {
            public Stranded(FfmpegCameraRecorder recorder, RecordingClaimTicket claim, string? dir)
            {
                Recorder = recorder;
                Claim = claim;
                Dir = dir;
            }

            public FfmpegCameraRecorder Recorder { get; }
            public RecordingClaimTicket Claim { get; }
            public string? Dir { get; }
        }

        private readonly object _gate = new();
        private readonly List<Stranded> _held = new();

        /// <summary>True while at least one camera ffmpeg is still running that AgentEyes asked to
        /// die and could not kill.</summary>
        public bool HoldsAny
        {
            get { lock (_gate) return _held.Count > 0; }
        }

        /// <summary>
        /// Every stranded camera, for <c>/status</c>. A LIST rather than one slot because a second
        /// recording can be started after the first one's camera was abandoned, and dropping either
        /// reference to keep the shape simple would throw away the only handle to a live process -
        /// which is the whole defect this class closes.
        /// </summary>
        public IReadOnlyList<StrandedCameraReport> Report()
        {
            lock (_gate)
            {
                var rows = new List<StrandedCameraReport>(_held.Count);
                foreach (var s in _held)
                    rows.Add(new StrandedCameraReport
                    {
                        Device = s.Recorder.DeviceName,
                        Pid = s.Recorder.ProcessId,
                        Output = s.Recorder.OutputPath,
                        Dir = s.Dir,
                    });
                return rows;
            }
        }

        /// <summary>
        /// The end of a stop: release this session's recording claim - UNLESS its camera ffmpeg is
        /// still running, in which case the recorder and the claim are both kept instead.
        ///
        /// One call, not a branch at the call site, so "the claim is not released as though the stop
        /// were clean" cannot be true on one path and false on the other. Returns true when the
        /// camera was retained (and the claim therefore deliberately NOT released).
        /// </summary>
        public bool ReleaseClaimUnlessStranded(FfmpegCameraRecorder? camera, in RecordingClaimTicket claim, string? dir)
        {
            if (TryRetain(camera, claim, dir)) return true;

            RecordingWorkset.Release(claim);
            return false;
        }

        /// <summary>
        /// The end of a FAILED START: discard the directory nothing was captured into - UNLESS the
        /// camera ffmpeg that failed to open is still running inside it.
        ///
        /// Deleting a directory around a live ffmpeg does not stop the ffmpeg; it fails on the file
        /// the process still has open and replaces an actionable camera failure with an IO error.
        /// Returns true when the camera was retained (and the directory therefore left alone).
        /// </summary>
        public bool DiscardDirectoryUnlessStranded(FfmpegCameraRecorder? camera, string? dir, in RecordingClaimTicket claim)
        {
            if (TryRetain(camera, claim, dir))
            {
                Log.Error($"[StrandedCameraOwner] the failed start's directory {dir} is NOT being discarded - a "
                          + $"camera ffmpeg (PID {camera!.ProcessId?.ToString() ?? "unknown"}) is still running inside "
                          + "it. Removing it would fail on the file that process holds open and would hide the real "
                          + "cause; it is reported on /status instead.");
                return true;
            }

            if (dir == null) return false;
            RecordingStartSequence.Discard(dir, claim);
            return false;
        }

        /// <summary>
        /// Try again to get every stranded ffmpeg off its camera, and let go of everything that is
        /// finally gone.
        ///
        /// This is what makes retaining the recorder worth something rather than a museum piece: the
        /// retry runs through the SAME <see cref="FfmpegCameraRecorder.Dispose"/> the stop uses, so a
        /// process that has since died (or that dies under this kill) releases its handle, its claim
        /// and its row on <c>/status</c> in one step.
        ///
        /// It is an entry point - called from a recording start, where the user is asking for the
        /// camera back - so it reports rather than propagates.
        /// </summary>
        public void Recover()
        {
            lock (_gate)
            {
                if (_held.Count == 0) return;
                Log.Info($"[StrandedCameraOwner] Recover: retrying {_held.Count} stranded camera process(es)");

                for (int i = _held.Count - 1; i >= 0; i--)
                {
                    var s = _held[i];
                    try
                    {
                        s.Recorder.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[StrandedCameraOwner] Recover: retrying the stop for \"{s.Recorder.DeviceName}\" failed", ex);
                    }

                    if (s.Recorder.IsAbandoned)
                    {
                        Log.Error($"[StrandedCameraOwner] Recover: the camera ffmpeg for \"{s.Recorder.DeviceName}\" "
                                  + $"(PID {s.Recorder.ProcessId?.ToString() ?? "unknown"}) is STILL RUNNING - it keeps "
                                  + $"the camera, {s.Recorder.OutputPath} and the claim on {s.Dir}");
                        continue;
                    }

                    Log.Info($"[StrandedCameraOwner] Recover: the camera ffmpeg for \"{s.Recorder.DeviceName}\" is gone "
                             + $"at last - releasing the claim on {s.Dir}");
                    RecordingWorkset.Release(s.Claim);
                    _held.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Take ownership of a recorder whose ffmpeg survived the quit, the kill AND the Dispose
        /// retry. Anything else - no camera, a camera that stopped, a camera that was force-killed -
        /// is NOT retained, which is what keeps a normal recording's claim being released normally.
        /// </summary>
        private bool TryRetain(FfmpegCameraRecorder? camera, in RecordingClaimTicket claim, string? dir)
        {
            if (camera == null || !camera.IsAbandoned) return false;

            lock (_gate)
            {
                foreach (var existing in _held)
                    if (ReferenceEquals(existing.Recorder, camera)) return true;

                _held.Add(new Stranded(camera, claim, dir));
            }

            Log.Error($"[StrandedCameraOwner] RETAINING the camera ffmpeg for \"{camera.DeviceName}\" "
                      + $"(PID {camera.ProcessId?.ToString() ?? "unknown"}): it survived the quit, the kill and the "
                      + $"Dispose retry and is STILL RUNNING. It still holds the camera and {camera.OutputPath}. "
                      + $"The recording claim on {dir} is deliberately NOT released - a live writer is still in that "
                      + "directory. This is reported on /status until the process is gone.");
            return true;
        }
    }
}
