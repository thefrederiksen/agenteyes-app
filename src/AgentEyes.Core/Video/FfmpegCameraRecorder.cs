using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AgentEyes.Video
{
    /// <summary>
    /// Raised when AgentEyes could not get ffmpeg off the webcam (issue #28, gate defect 2, widened
    /// to the START path by gate round 3, defect 1).
    ///
    /// It exists so that "the camera is stopped" can never be a guess. An attempt that sent "q",
    /// timed out, killed, and STILL sees a live process has terminated nothing: ffmpeg is writing
    /// camera.mp4 and holding an exclusive DirectShow device. Reporting that as a clean stop is what
    /// let the service go idle, release the capture claim, and offer to record again while the
    /// webcam was still taken.
    ///
    /// It is raised from BOTH ends of the recorder's life, because the fact it reports is the same
    /// one at both: a live ffmpeg on the camera that we asked to die and that did not. A start whose
    /// open probe timed out and whose kill was refused strands exactly the process a failed stop
    /// strands - the only difference is the sentence in front of it, which is what
    /// <c>context</c> carries.
    /// </summary>
    internal sealed class CameraStopFailedException : Exception
    {
        /// <param name="deviceName">The exact DirectShow device the live process still holds.</param>
        /// <param name="outputPath">The camera.mp4 it still owns.</param>
        /// <param name="diagnostics">Where the ffmpeg output for this camera can be read. It differs
        /// by path: a stop writes the ffmpeg log beside camera.mp4, while a FAILED OPEN deliberately
        /// writes nothing into the recording directory (AC8/AC9) and sends its stderr to the
        /// application log instead - so naming a log file that does not exist would be its own
        /// small lie.</param>
        /// <param name="context">What was being attempted, as a sentence: the failure reads
        /// differently at start and at stop, and the actionable fact is the same either way.</param>
        public CameraStopFailedException(string deviceName, string outputPath, string diagnostics, string context)
            : base($"{context}, and the camera ffmpeg for \"{deviceName}\" could not be terminated - it is "
                   + $"STILL RUNNING and still holds the camera and {outputPath}. {diagnostics}")
        {
            DeviceName = deviceName;
        }

        public string DeviceName { get; }
    }

    /// <summary>
    /// Raised when the camera ffmpeg IGNORED the quit request and had to be killed (issue #28, spec
    /// amendment 2026-08-28, AC14).
    ///
    /// The process really is gone, so this is not the abandoned-process failure above - but the FILE
    /// is a different question, and this is what stops the two being conflated. camera.mp4 was being
    /// written by a process that was shot rather than asked; ffmpeg never wrote the trailer, so the
    /// take was never finalized. Returning normally from a stop like that is how a force-killed file
    /// reached the manifest as a clean complete take through three rounds of this fix.
    ///
    /// The SCREEN recording is unaffected and is already on disk - this reports one track, not a
    /// lost session, and every caller treats it that way.
    /// </summary>
    internal sealed class CameraForceKilledException : Exception
    {
        public CameraForceKilledException(string deviceName, string outputPath, double capturedSeconds, string diagnostics)
            : base($"the camera \"{deviceName}\" ignored the quit request and had to be force-killed, so "
                   + $"{outputPath} was never finalized by ffmpeg and may be truncated - it covers "
                   + $"{capturedSeconds:F1}s of reported output. The screen recording is unaffected. {diagnostics}")
        {
            DeviceName = deviceName;
            CapturedSeconds = capturedSeconds;
        }

        public string DeviceName { get; }

        /// <summary>Seconds of output ffmpeg reported writing before it was killed.</summary>
        public double CapturedSeconds { get; }
    }

    /// <summary>
    /// The webcam half of a recording (issue #28): a SECOND, independent ffmpeg process writing
    /// camera.mp4 next to the screen recorder's recording.mp4, so a session can be edited afterwards
    /// into a screen-plus-presenter piece instead of a layout baked in at record time.
    ///
    /// It is deliberately its own class rather than a mode of <see cref="FfmpegRecorder"/>, because
    /// the two differ in exactly the ways that matter here:
    ///
    ///  - it is VIDEO ONLY (no audio track - decision 1), so it never participates in the deferred
    ///    audio mux and writes straight to its FINAL path (assumption A4);
    ///  - a camera that cannot be OPENED fails the whole recording start, loudly (decision 3);
    ///  - a camera LOST MID-RUN does not (decision 4). The screen recorder keeps running, the loss is
    ///    logged as a WARNING naming the camera, and the manifest records the track as truncated with
    ///    the seconds actually captured. Anything else would throw away a good screen recording
    ///    because a USB cable moved.
    ///
    /// Like the screen recorder it is stopped with "q" on stdin rather than a kill (assumption A6),
    /// so camera.mp4 is finalized rather than truncated.
    ///
    /// THE THREE RULES THE REVIEW GATE ADDED (round 2). All three are the same rule wearing different
    /// clothes - a camera claim is only ever made from a PRESENCE that was observed:
    ///
    ///  1. OPENED means ffmpeg REPORTED the camera open, not "it has not failed yet". A fixed 400 ms
    ///     sleep proved nothing: a busy, unplugged or unsupported device that takes 500 ms to fail
    ///     passed the probe, and its later exit was then filed as a harmless mid-run loss - turning a
    ///     camera that never recorded a frame into a silent screen-only recording, which is the exact
    ///     outcome decision 3 exists to prevent. The presence the probe waits for is named in
    ///     <see cref="StartAndProbe"/>, and WHICH presence it is decides AC3 as much as AC9 - see
    ///     the measurements there.
    ///  2. STOPPED means the OS process is gone. A timeout that logs a warning, a kill whose failure
    ///     is swallowed, and a second wait nobody reads are three ways of reporting a stop that did
    ///     not happen. When ffmpeg survives all of it this throws, and the caller reports a FAILED
    ///     stop instead of going idle with the webcam still held.
    ///  3. LOST means the process ended before the user asked it to - observed from the process
    ///     itself at stop time, not from whether an exit callback happened to have been delivered
    ///     yet. The callback is a convenience; <c>HasExited</c> is the fact.
    ///
    /// ROUND 3 ADDED THREE MORE, and they are the same two mistakes again - "we asked it to die" is
    /// not "it died", and "the device opened" is not "the device produced video":
    ///
    ///  4. A FAILED START MAY NOT STRAND FFMPEG EITHER. The open probe's kill got the same treatment
    ///     the stop's kill used to get - logged and then ignored - and the recorder then marked
    ///     itself terminated and released the process handle. Because the start THREW, no caller
    ///     ever received the object, so nothing in the process still knew about the live ffmpeg on
    ///     the webcam. That is why construction and opening are now two steps
    ///     (<see cref="Create"/> then <see cref="Open"/>): the caller holds the recorder BEFORE
    ///     ffmpeg exists, so a failed open is rolled back by the same owner that rolls back every
    ///     other writer. Inside, a start whose kill was refused keeps <c>_terminated</c> false and
    ///     keeps the handle - see <see cref="FailOpen"/>.
    ///  5. NEITHER MAY DISPOSE. <see cref="ICameraProcess.Dispose"/> closes a HANDLE; it does not
    ///     terminate anything. Releasing it while ffmpeg is alive converts a reported failure into
    ///     an unreachable one, so <see cref="Dispose"/> keeps the handle when the retry could not
    ///     confirm the process gone.
    ///  6. A CAMERA TRACK IS ONLY "COMPLETE" WHEN FFMPEG SAID IT WROTE SOMETHING. Rule 1 opens on
    ///     ffmpeg's headers, which is a claim about the DEVICE, not about the FILE. A camera that
    ///     printed both headers, stalled without ever reporting output, and then answered "q"
    ///     normally used to stop with <c>CapturedSeconds == 0</c> and <c>LostMidRun == false</c> -
    ///     an empty camera.mp4 written into the manifest as a good take. <see cref="Stop"/> now
    ///     draws that conclusion from ffmpeg's COMPLETE stderr (<see cref="ICameraProcess.DrainStderr"/>)
    ///     and reports the track LOST whenever no output was ever reported, alive or dead.
    ///
    /// THE 2026-08-28 SPEC AMENDMENT REPLACED RULE 6 WITH SOMETHING SMALLER AND HONEST. Rounds 1-3
    /// all tried to PROVE camera.mp4 was complete, and the Review Gate reproduced three cases where
    /// the proof came out wrong IN THE USER'S FAVOUR: a camera that emitted one tick and stalled for
    /// a 30-second session, a camera whose stderr never reached EOF after an early tick, and a file
    /// that was force-killed mid-write. All three were recorded as clean complete takes.
    ///
    /// So this class no longer reasons its way to "complete". It records WHAT IT OBSERVED -
    /// <see cref="CapturedSeconds"/>, <see cref="StopKind"/>, <see cref="StderrComplete"/> - and
    /// <see cref="Completeness"/> is a THREE-STATE verdict whose <c>yes</c> requires the full
    /// presence and whose <c>unknown</c> is the correct answer for everything else, INCLUDING cases
    /// nobody anticipated. The one thing it may never do is claim <c>yes</c> from an absence.
    /// </summary>
    internal sealed class FfmpegCameraRecorder : IDisposable
    {
        /// <summary>
        /// How long a freshly started camera is given to report itself open before the open is called
        /// a failure. Generous on purpose - webcams take a moment to warm up, and this is a deadline
        /// for FAILING, not a delay that every start pays: a camera that reports itself open in
        /// 600 ms is recording 600 ms after it was asked to.
        /// </summary>
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(8);

        /// <summary>How often the open probe looks at the process.</summary>
        private static readonly TimeSpan ProbePoll = TimeSpan.FromMilliseconds(25);

        /// <summary>How long a clean "q" quit is given before the process is killed.</summary>
        private const int QuitTimeoutMs = 8000;

        /// <summary>How long the kill is given before the stop is declared FAILED.</summary>
        private const int KillTimeoutMs = 3000;

        /// <summary>
        /// How long a terminated camera's stderr is given to reach end of stream before the stop
        /// draws its conclusions from it. Short, because the process has already exited by the time
        /// this runs and the reader only has to finish handing over what it already has - and
        /// BOUNDED, because a stop that hangs is worse than a stop that reports what it could not
        /// see (see <see cref="ICameraProcess.DrainStderr"/>).
        /// </summary>
        private const int StderrDrainMs = 2000;

        /// <summary>
        /// How stale ffmpeg's last ADVANCE of its output position may be, measured at the moment the
        /// stop is requested, for the take to still be called complete (issue #28, AC13).
        ///
        /// This is the window the ONE_TICK_STALL case walks through. ffmpeg prints its progress
        /// roughly twice a second while it is encoding, so three seconds is about six reports
        /// missed - comfortably past any scheduling hiccup, and nowhere near the "stalled for the
        /// rest of a 30-second session" the gate reproduced. A camera whose position has not moved
        /// for longer than this is not KNOWN broken (the last frames may simply be buffered), so it
        /// is not <c>no</c> - it is <c>unknown</c>, which is exactly the state that did not exist
        /// before.
        ///
        /// Measured against the ADVANCE, never the arrival: ffmpeg prints a final summary line when
        /// it quits, and a stalled camera's summary repeats the position it stalled at. An
        /// arrival-based freshness check would read that repeat as "ticks were still arriving" and
        /// certify the stall.
        /// </summary>
        private static readonly TimeSpan OutputStallWindow = TimeSpan.FromSeconds(3);

        private readonly ICameraProcess _proc;
        private readonly StringBuilder _stderr = new();
        private readonly string _logPath;
        private readonly TimeSpan _openTimeout;

        /// <summary>
        /// The clock, injectable so the stall window above is testable without sleeping. Production
        /// passes UTC now; a test passes a clock it moves by hand, which is the only way to reach
        /// "the camera stalled for 30 seconds" in a unit test that must run in milliseconds.
        /// </summary>
        private readonly Func<DateTime> _now;

        /// <summary>
        /// Set when a stop has been ASKED FOR. It is deliberately not "this recorder is finished":
        /// a stop that could not terminate ffmpeg has finished nothing, and <see cref="Dispose"/>
        /// has to be able to try again. Conflating the two is gate defect 2 - the flag was set at the
        /// top of Stop, so the one retry left in the object could never run.
        /// </summary>
        private bool _stopRequested;

        /// <summary>Set only when the OS process is CONFIRMED gone. The gate on doing the stop's work
        /// twice, and the only state that makes <see cref="Stop"/> a no-op.</summary>
        private bool _terminated;

        /// <summary>
        /// Set only when the process HANDLE has been released, which may only happen once
        /// <see cref="_terminated"/> is true. Keeping the two apart is rule 5: disposing the wrapper
        /// is not a way of stopping anything, it is a way of forgetting.
        /// </summary>
        private bool _disposed;

        /// <summary>Set by <see cref="Open"/> so the process can never be started twice behind one
        /// recorder - a second ffmpeg on the same camera would be a leak nothing owns.</summary>
        private bool _openAttempted;

        /// <summary>
        /// The output position ffmpeg last reported (its "time=" progress field), in milliseconds -
        /// the number of seconds of camera actually written. Read at stop for the manifest, and it is
        /// the ONLY honest answer for a camera that died mid-run: wall time would claim footage the
        /// file does not contain.
        /// </summary>
        private long _mediaMs;

        /// <summary>
        /// Set the first time ffmpeg reports a progress tick carrying a POSITIVE output position. It
        /// is NOT what the open probe waits for - see <see cref="StartAndProbe"/> - because libx264's
        /// frame threading holds the first encoded frame back by seconds.
        ///
        /// It is the only evidence this class ever has that camera.mp4 contains anything, so
        /// <see cref="Stop"/> reads it as rule 6: no reported output means the track is LOST, not
        /// complete. Round 2 merely logged it, which is how a camera that opened and then produced
        /// nothing reached the manifest as a clean 0.0-second take.
        ///
        /// Strictly positive on purpose: ffmpeg prints a first tick before it has encoded anything
        /// (<c>time=00:00:00.00</c>, and <c>time=N/A</c> before that), and reading a zero position as
        /// "it wrote output" would re-open the exact hole this closes.
        /// </summary>
        private volatile bool _wroteOutput;

        /// <summary>
        /// When ffmpeg's reported output position last MOVED FORWARD, as ticks (UTC). The instrument
        /// behind AC13: a camera can go on printing progress lines forever while the position stands
        /// still, so "we heard from it recently" is not "it is still recording". Zero until the
        /// first positive tick.
        ///
        /// Written from the stderr callback thread and read on the stop thread, so it goes through
        /// <see cref="Interlocked"/> like <see cref="_mediaMs"/> rather than being torn on a 32-bit
        /// read.
        /// </summary>
        private long _lastOutputAdvanceTicks;

        /// <summary>
        /// How many times ffmpeg's reported output position MOVED FORWARD. The second half of
        /// AC13's instrument (gate round 4, defect 2).
        ///
        /// Freshness alone was never enough. A camera that advanced ONCE, at 0.5s, and then stalled
        /// until a stop 2.9 seconds later walked straight through the three-second window and earned
        /// "yes" - the rule "never establishes that ticks CONTINUED after the first one", in the
        /// gate's words. One advance is a camera that STARTED; it is not a camera that was
        /// RECORDING, and the difference is the whole of AC13.
        ///
        /// Written from the stderr callback thread, read on the stop thread, so it goes through
        /// <see cref="Interlocked"/> like everything else here.
        /// </summary>
        private long _outputAdvances;

        /// <summary>When the FIRST <see cref="Stop"/> call arrived, as ticks (UTC) - the moment the
        /// output position's freshness is judged against. Zero until a stop is requested.</summary>
        private long _stopRequestedTicks;

        /// <summary>
        /// <see cref="_lastOutputAdvanceTicks"/> SNAPSHOTTED at the instant the stop was requested,
        /// and the only value AC13 is judged from.
        ///
        /// Advances that arrive AFTER the stop are deliberately not counted. ffmpeg flushes what it
        /// is holding when it is told to quit, so a camera that stalled for the whole session can
        /// still push its position forward on the way out - and reading that as "output was still
        /// arriving" would let the parting flush certify the stall, which is the same false-clean
        /// result one step further along. What is being asked is whether this camera was still
        /// recording WHEN THE USER STOPPED IT, and that question can only be answered with evidence
        /// that existed at that moment.
        /// </summary>
        private long _advanceAtStopTicks;

        /// <summary><see cref="_outputAdvances"/> SNAPSHOTTED at the instant the stop was requested,
        /// and judged from for exactly the reason <see cref="_advanceAtStopTicks"/> is: the flush
        /// ffmpeg performs on its way out may itself advance the position, and a parting advance is
        /// not evidence that this camera was recording BEFORE the user stopped it.</summary>
        private long _advanceCountAtStop;

        /// <summary>
        /// True only once "q" was actually WRITTEN to ffmpeg's stdin without error - i.e. the quit
        /// was DELIVERED (gate round 4, defect 1).
        ///
        /// It exists because a quit that never reached the process cannot have been answered by it.
        /// The stop used to catch the failed write, log it, and then read a subsequent
        /// <c>WaitForExit(...) == true</c> as proof that ffmpeg had answered - so a camera that
        /// CRASHED while the pipe went with it was recorded as <c>clean-quit</c>, and the manifest
        /// said the take was complete. The process really did end; how it ended was never observed.
        /// </summary>
        private bool _quitDelivered;

        /// <summary>How the camera process ended, once that has been OBSERVED. Null while the
        /// recording is running and after a stop that never reached the process.</summary>
        private CameraStopKind? _stopKind;

        /// <summary>True only when ffmpeg's stderr was drained to END OF STREAM at the stop, i.e.
        /// what this recorder read is everything ffmpeg ever wrote. False is not a failure - it is a
        /// statement that the evidence is incomplete.</summary>
        private bool _stderrComplete;

        /// <summary>
        /// Set when ffmpeg reports that it OPENED the DirectShow input - the "Input #0, dshow, from
        /// 'video=...'" header it dumps only after the capture graph ran and the stream parameters
        /// were read off the device. Half of the open probe's presence.
        /// </summary>
        private volatile bool _inputOpenReported;

        /// <summary>
        /// Set when ffmpeg reports that it OPENED camera.mp4 for writing - the "Output #0, mp4, to
        /// '...'" header. The other half: input open plus output open is ffmpeg saying, in its own
        /// words, that this camera is being recorded to this file.
        /// </summary>
        private volatile bool _outputOpenReported;

        /// <summary>Set when the process exits while the recording is still running (decision 4).</summary>
        private volatile bool _lostMidRun;

        /// <summary>
        /// True once the open probe has passed, i.e. this camera really is recording. It is what
        /// tells a MID-RUN loss (decision 4 - warn, keep the screen recording) apart from a camera
        /// that never opened at all (decision 3 - fail the start): both end as an ffmpeg exit, and
        /// only one of them is a warning about a recording in progress.
        /// </summary>
        private volatile bool _opened;

        /// <summary>The exact DirectShow device name this process is capturing.</summary>
        public string DeviceName { get; }

        /// <summary>Where camera.mp4 is being written (its final path - no deferred mux).</summary>
        public string OutputPath { get; }

        /// <summary>The full ffmpeg command line, for the manifest and the log.</summary>
        public string CommandLine { get; }

        /// <summary>
        /// When the ffmpeg process was actually started, captured the instant Process.Start returned.
        /// This is what <c>CameraStartOffsetSeconds</c> is measured from (assumption A5) - an
        /// alignment HINT of tens of milliseconds, not frame-accurate genlock.
        /// </summary>
        public DateTime StartedUtc { get; }

        /// <summary>True when the camera stopped on its own before <see cref="Stop"/> was called.</summary>
        public bool LostMidRun => _lostMidRun;

        /// <summary>Seconds of camera footage ffmpeg reported writing (0 before its first progress tick).</summary>
        public double CapturedSeconds => Interlocked.Read(ref _mediaMs) / 1000.0;

        public bool HasExited => _proc.HasExited;

        /// <summary>The OS process id of this camera's ffmpeg, or null before it is started - what
        /// names a stuck process to the person who has to deal with it (AC16).</summary>
        public int? ProcessId => _proc.ProcessId;

        /// <summary>
        /// How the camera process ended, as OBSERVED (issue #28, spec amendment). Null until a stop
        /// has actually watched it end - which is itself an honest answer, and reads out as an
        /// ABSENT manifest field rather than a guess.
        /// </summary>
        public CameraStopKind? StopKind => _stopKind;

        /// <summary>True only when ffmpeg's stderr reached END OF STREAM at the stop. False means
        /// this recorder did not read everything ffmpeg wrote - the evidence is incomplete, and no
        /// conclusion that needs complete evidence may be drawn from it.</summary>
        public bool StderrComplete => _stderrComplete;

        /// <summary>
        /// Whether camera.mp4 is a complete take - the three-state verdict the spec amendment put in
        /// place of the <c>CameraTruncated</c> boolean.
        ///
        /// <c>yes</c> IS A ONE-WAY DOOR and needs the WHOLE presence, every clause of it:
        ///
        ///  1. the process answered "q" and exited on its own, so ffmpeg wrote the MP4 trailer;
        ///  2. its stderr was read to end of stream, so what follows is judged from a COMPLETE log
        ///     rather than from a stream still being delivered (AC15);
        ///  3. it actually reported writing output at all; and
        ///  4. that output position was still ADVANCING when the stop was requested (AC13) - one
        ///     tick at the start of a 30-second session is not a recording.
        ///
        /// <c>no</c> is reserved for what is KNOWN short or broken: the process exited early, it was
        /// force-killed, or it never reported a single frame.
        ///
        /// Everything else is <c>unknown</c>, and that includes every case not anticipated here. It
        /// is not a failure to write - it is the only answer this class is entitled to when a clause
        /// above cannot be established. The mistake it exists to prevent is the opposite one:
        /// claiming <c>yes</c> from an absence of evidence, which is what rounds 1-3 all did.
        /// </summary>
        public CameraCompleteness Completeness
        {
            get
            {
                // No stop has watched this process end. Nothing is established either way.
                if (_stopKind is not { } kind) return CameraCompleteness.Unknown;

                switch (kind)
                {
                    // Still running, so the file is still being written by a process we do not
                    // control. Nothing about its contents is knowable from here (AC16).
                    case CameraStopKind.Abandoned:
                        return CameraCompleteness.Unknown;

                    // KNOWN short: it stopped before the user asked (AC10), or it was shot rather
                    // than asked and ffmpeg never finalized the file (AC14).
                    case CameraStopKind.ExitedEarly:
                    case CameraStopKind.ForceKilled:
                        return CameraCompleteness.No;
                }

                // AN INCOMPLETE READ IS A BROKEN INSTRUMENT, NEVER A CLEAN RUN - and it is judged
                // BEFORE anything that reads an ABSENCE off that same stream (AC15; gate round 4,
                // defect 3). The order used to be the other way round, so a stop that had explicitly
                // failed to reach end of stream still answered "no" - a positive claim that
                // camera.mp4 is empty - from ticks that were merely still in flight. Both of the
                // clauses below are absences observed through this stream: no open report, and no
                // progress tick. Neither is an absence until the stream is finished.
                if (!_stderrComplete) return CameraCompleteness.Unknown;

                // A camera that never reported the device open, or never reported writing a frame,
                // produced nothing - that is KNOWN, not unknown, because the log it is read from is
                // now known to be complete.
                if (!_opened || !_wroteOutput) return CameraCompleteness.No;

                // One tick then a stall for the rest of the session (AC13).
                if (!OutputWasAdvancingAtTheStop) return CameraCompleteness.Unknown;

                return CameraCompleteness.Yes;
            }
        }

        /// <summary>
        /// True when ffmpeg's output was still ADVANCING as the stop was requested - the one clause
        /// of <see cref="Completeness"/> that is about the MIDDLE of the recording rather than its
        /// end. It needs TWO presences, and the second one was missing until gate round 4:
        ///
        ///  1. the position moved forward MORE THAN ONCE, so it can be said to have CONTINUED at
        ///     all - one advance is a camera that started, not a camera that recorded; and
        ///  2. the LAST of those advances was within <see cref="OutputStallWindow"/> of the stop.
        ///
        /// Freshness on its own certified the gate's ONE_TICK_STALL_2_9S case: a single advance at
        /// 0.5s followed by a 2.9-second stall is inside the window, so the take earned "yes" from
        /// evidence that never showed the camera recording for more than one instant.
        ///
        /// Both are read from the snapshots taken when the stop was asked for, never from the live
        /// values: see <see cref="_advanceAtStopTicks"/> for why a flush on the way out is not
        /// evidence about what happened before it.
        /// </summary>
        private bool OutputWasAdvancingAtTheStop
        {
            get
            {
                long advances = Interlocked.Read(ref _advanceCountAtStop);
                if (advances < 2) return false;

                long advanced = Interlocked.Read(ref _advanceAtStopTicks);
                long stopped = Interlocked.Read(ref _stopRequestedTicks);
                if (advanced == 0 || stopped == 0) return false;
                return new DateTime(stopped, DateTimeKind.Utc) - new DateTime(advanced, DateTimeKind.Utc)
                       <= OutputStallWindow;
            }
        }

        /// <summary>
        /// True while a camera ffmpeg this recorder started is STILL RUNNING after everything this
        /// recorder can do to it - the quit, the kill, and the <see cref="Dispose"/> retry (AC16).
        ///
        /// It is what makes the failure OWNABLE rather than merely reported: whoever holds this
        /// object still holds the only handle that can reach that process, and this is the flag that
        /// says the handle is worth holding.
        ///
        /// IT ASKS THE PROCESS, EVERY TIME (gate round 4, defect 4). It used to answer from the
        /// stored stop kind and <see cref="_terminated"/> alone - two facts about what AgentEyes DID,
        /// neither of which can change when the process later exits by itself. So a stranded ffmpeg
        /// that finally ended left every reader of this flag asserting a dead PID was live: a stuck
        /// row on <c>/status</c>, and that recording's claim held against packaging and
        /// transcription, until some later recording happened to run the recovery. "Still running"
        /// is a fact about a process, and only the process can answer it.
        ///
        /// Reading <see cref="ICameraProcess.HasExited"/> is safe here on every path: the handle is
        /// released only by <see cref="Dispose"/>, and only once <see cref="_terminated"/> is true -
        /// which this test short-circuits on first.
        /// </summary>
        public bool IsAbandoned => _stopKind == CameraStopKind.Abandoned && !_terminated && !_proc.HasExited;

        private FfmpegCameraRecorder(ICameraProcess proc, string deviceName, string outputPath, string commandLine,
            string logPath, DateTime startedUtc, TimeSpan openTimeout, Func<DateTime> now)
        {
            _proc = proc;
            DeviceName = deviceName;
            OutputPath = outputPath;
            CommandLine = commandLine;
            _logPath = logPath;
            StartedUtc = startedUtc;
            _openTimeout = openTimeout;
            _now = now;
        }

        /// <summary>
        /// Build the recorder for a camera. NOTHING IS STARTED HERE - no ffmpeg, no file, no device
        /// held - so this cannot fail in a way that leaves anything behind. <see cref="Open"/> is
        /// what puts a process in the world.
        ///
        /// THE SPLIT IS THE FIX FOR GATE ROUND 3, DEFECT 1, and it is the only shape that closes it.
        /// While opening the camera was one static call, a failure inside it threw before the caller
        /// could be handed the object - so when the probe timed out and the kill was REFUSED, the
        /// live ffmpeg on the webcam belonged to nobody: not to the service, whose <c>_camera</c>
        /// assignment never completed and whose rollback therefore had nothing to stop, and not to
        /// the CLI, whose <c>finally</c> disposed a null. Constructing first means the owner holds
        /// the recorder BEFORE the process exists, so every failure of <see cref="Open"/> is rolled
        /// back by the same owner, through the same Stop/Dispose retry as any other failure.
        ///
        /// It is also how this class matches the rule the rest of the capture engine already follows
        /// (issue #155): "a field is set the moment its writer is constructed, so a writer whose
        /// Start threw is still in <c>LiveWriters</c> and still gets stopped and disposed."
        /// </summary>
        public static FfmpegCameraRecorder Create(string dshowCameraName, int fps, int crf, string outPath)
        {
            Log.Info($"[FfmpegCameraRecorder] Create: camera=\"{dshowCameraName}\" fps={fps} crf={crf} out={outPath}");

            string exe = FfmpegLocator.Ffmpeg();
            var args = FfmpegArgs.CameraCapture(dshowCameraName, fps, crf, outPath);
            string cmd = FfmpegArgs.ToCommandLine(exe, args);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args) psi.ArgumentList.Add(a);

            return new FfmpegCameraRecorder(
                new FfmpegCameraProcess(psi, dshowCameraName), dshowCameraName, outPath, cmd,
                outPath + ".ffmpeg.log", DateTime.UtcNow, OpenTimeout, () => DateTime.UtcNow);
        }

        /// <summary>
        /// The same recorder over a supplied process - the seam the failure-path tests drive
        /// (issue #28, gate round 2). Identical logic to <see cref="Create"/> from the moment the
        /// process starts; only the process and the probe deadline are injected, so a test can reach
        /// the delayed-failure, failed-termination and exit/stop-race paths that a real ffmpeg will
        /// not perform on request.
        /// </summary>
        /// <param name="now">The clock the stall window (AC13) is measured on. Supplying one is how
        /// a test reaches "the camera emitted one tick and then stalled for thirty seconds" without
        /// taking thirty seconds - and without a sleep, which would make the check a race.</param>
        internal static FfmpegCameraRecorder CreateOver(ICameraProcess proc, string deviceName, string outPath,
            string logPath, TimeSpan openTimeout, Func<DateTime>? now = null) =>
            new(proc, deviceName, outPath, "(supplied process)", logPath, DateTime.UtcNow, openTimeout,
                now ?? (() => DateTime.UtcNow));

        /// <summary>
        /// Start ffmpeg and hold until this camera has PROVED it is open - then this recorder is
        /// recording <see cref="OutputPath"/>.
        ///
        /// Throws <see cref="UsageException"/> naming the camera when ffmpeg cannot open the device -
        /// absent, in use by another application, refusing the requested framerate, or simply never
        /// producing a frame. That is decision 3: a camera recording that cannot film the camera
        /// FAILS, it never silently records screen-only.
        ///
        /// Throws <see cref="CameraStopFailedException"/> in the one worse case: the open failed AND
        /// the stalled ffmpeg survived the kill. That is not a user error, it is a live process on
        /// the webcam, and it is reported as such. The recorder is still usable after either throw -
        /// its owner (which holds it, because <see cref="Create"/> ran first) can and does call
        /// <see cref="Stop"/>/<see cref="Dispose"/> to try again.
        ///
        /// NOTHING is written into the recording directory on either failure path - not even an
        /// ffmpeg log - because a failed start must leave no directory behind for the Library and the
        /// repair passes to find (issue #28, AC8/AC9). ffmpeg's stderr goes to the APPLICATION log
        /// instead, where it is just as diagnosable and belongs to no recording.
        /// </summary>
        public void Open()
        {
            if (_openAttempted)
                throw new InvalidOperationException(
                    $"the camera \"{DeviceName}\" has already been opened once by this recorder - "
                    + "opening it again would start a second ffmpeg that nothing owns");
            _openAttempted = true;

            Log.Info($"[FfmpegCameraRecorder] Open: camera=\"{DeviceName}\" out={OutputPath}");
            StartAndProbe();
            Log.Info($"[FfmpegCameraRecorder] Open: camera=\"{DeviceName}\" is recording to {OutputPath}");
        }

        /// <summary>
        /// Start the process and hold the recording start until this camera has PROVED it is open -
        /// gate defect 3.
        ///
        /// THE PROOF IS FFMPEG'S OWN OPEN REPORT: the "Input #0, dshow, from 'video=...'" header it
        /// dumps only after the DirectShow capture graph ran and the stream parameters were read off
        /// the device, AND the "Output #0, mp4, to '...'" header it dumps once camera.mp4 is open for
        /// writing. Together they are ffmpeg saying, in its own words, that this camera is being
        /// recorded to this file. There is exactly ONE input and ONE output on the command line
        /// (<see cref="FfmpegArgs.CameraCapture"/>), so neither line can be about anything else.
        ///
        /// Everything else is a failure:
        ///
        ///  - the process ended (any exit code, zero included - ffmpeg exiting cleanly the instant it
        ///    was asked to film is still a camera that filmed nothing);
        ///  - the deadline passed without both reports.
        ///
        /// Both throw, and both make sure the process is dead before they do: a probe that gave up on
        /// a still-running ffmpeg and walked away would replace one leak with another.
        ///
        /// WHY NOT THE FIRST PROGRESS TICK, which is what round 2 of this fix waited for. Because that
        /// tick reports ENCODED output, and libx264 frame-threading holds the first encoded frame back
        /// by seconds - while the camera is already filming. Measured on the eMeet C960 with the
        /// shipped ffmpeg (2026-08-28; 1920x1080 mjpeg in, x264 veryfast, threads=34), relative to
        /// the moment the process started:
        ///
        ///     0.373s  first frame actually captured (quit time minus the resulting file duration)
        ///     0.635s  "Input #0, dshow, from 'video=HD Webcam eMeet C960'"
        ///     0.660s  "Output #0, mp4, to '...camera.mp4'"
        ///     2.696s  first progress tick carrying a real time= (frame=13 time=00:00:00.36)
        ///
        /// The screen recorder is started only after this probe returns, so whatever the probe costs
        /// is head footage that camera.mp4 carries and recording.mp4 does not. Waiting for the tick
        /// bought 2.3s of it and broke AC3's 1.0s duration clause; waiting for the open report costs
        /// ~0.26s and keeps AC3 and AC9 both. The three failures decision 3 names - busy, unplugged,
        /// unsupported framerate - all abort inside ffmpeg's input open and print NEITHER header
        /// (verified against the shipped ffmpeg: a held camera exits -5 in 0.23s, an absent one in
        /// 0.03s, and neither emits "Input #0"), so this presence rejects every one of them.
        ///
        /// What it deliberately does NOT claim is that a frame was ENCODED. A device that opens and
        /// then stops delivering is a MID-RUN loss, which decision 4 governs: the screen recording
        /// survives, the loss is a WARNING naming the camera, and the manifest records the track as
        /// truncated with the seconds actually captured. That is a REPORTED failure, not a silent one,
        /// and it is the only case this presence hands to decision 4 that the progress tick would have
        /// failed at the start.
        /// </summary>
        private void StartAndProbe()
        {
            _proc.Start(OnStderrLine, OnExited);

            var probe = Stopwatch.StartNew();
            while (true)
            {
                // Exit first: a process that has ended is not recording, whatever it printed on the
                // way out.
                if (_proc.HasExited)
                {
                    // READ THE EXIT CODE FIRST. Disposing the process releases the handle every
                    // property needs, and reading it afterwards throws "No process is associated with
                    // this object" - which once replaced the real, actionable "the camera is already
                    // in use" failure with a meaningless one.
                    int exitCode = _proc.ExitCode;
                    throw FailOpen($"ffmpeg exited with code {exitCode}",
                                   $"(ffmpeg exited with code {exitCode})", killFirst: false);
                }

                if (_inputOpenReported && _outputOpenReported) break;

                if (probe.Elapsed >= _openTimeout)
                {
                    string missing = _inputOpenReported
                        ? "it opened the camera but never opened camera.mp4"
                        : "it never reported the camera open";
                    throw FailOpen(
                        $"ffmpeg did not report the camera open within {_openTimeout.TotalSeconds:0.#}s ({missing})",
                        $"(it did not open within {_openTimeout.TotalSeconds:0.#}s - {missing})",
                        killFirst: true);
                }

                Thread.Sleep(ProbePoll);
            }

            // No second exit check here on purpose. The loop reads HasExited at the TOP of the same
            // iteration that sees the two flags, so a death in the microseconds after that read is
            // indistinguishable from a death one poll later - which is a MID-RUN loss, reported by
            // OnExited and by Stop (decision 4). A re-check would be a branch no test can reach.
            _opened = true;
            Log.Info($"[FfmpegCameraRecorder] StartAndProbe: camera=\"{DeviceName}\" reported the camera and "
                     + $"{Path.GetFileName(OutputPath)} open after {probe.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// ffmpeg's report that it OPENED the DirectShow camera: the input header it dumps only after
        /// the capture graph ran and the stream parameters were read off the device. Pure string
        /// inspection, so the open probe's presence is unit testable without ffmpeg.
        ///
        /// Matched on the header SHAPE and not on the device name, because the command line carries
        /// exactly one input and it is this camera - and because a name match that a future ffmpeg
        /// quoted differently would fail a working camera rather than reject a broken one.
        /// </summary>
        internal static bool IsInputOpenReport(string? line) =>
            line != null
            && line.StartsWith("Input #0", StringComparison.Ordinal)
            && line.Contains(", dshow,", StringComparison.Ordinal);

        /// <summary>
        /// ffmpeg's report that it OPENED the output file for writing: the output header it dumps
        /// once the muxer is set up on camera.mp4. The other half of the open probe's presence.
        /// </summary>
        internal static bool IsOutputOpenReport(string? line) =>
            line != null && line.StartsWith("Output #0", StringComparison.Ordinal);

        /// <summary>
        /// Give up on a camera that never started recording: make sure ffmpeg is gone and BUILD the
        /// actionable failure. Returns the exception so that every caller is a visible `throw` - a
        /// helper that only throws by convention is one edit away from falling through into
        /// "opened".
        ///
        /// GATE ROUND 3, DEFECT 1 LIVES IN THE LAST HALF OF THIS METHOD. It used to kill, LOG that
        /// the kill had failed, and then mark itself terminated and release the process handle
        /// anyway - so "we asked ffmpeg to die" was recorded as "ffmpeg died". Because
        /// <see cref="Open"/> throws, the recorder was never handed to anyone, and closing the
        /// handle made the surviving ffmpeg - still holding the webcam and still writing
        /// camera.mp4 - unreachable for the rest of the process's life.
        ///
        /// Now the two outcomes are told apart, and only one of them is an open failure:
        ///
        ///  - CONFIRMED GONE: the camera is free, this recorder is finished, the handle is released,
        ///    and the caller gets the <see cref="UsageException"/> that names the real cause. This is
        ///    every failure a user actually hits (absent, busy, unsupported framerate: ffmpeg exits
        ///    by itself, so there is nothing to kill) and it is the path AC8/AC9 measure.
        ///  - STILL RUNNING: <c>_terminated</c> stays false and the handle is KEPT, so the owner -
        ///    which holds this object, because <see cref="Create"/> ran before <see cref="Open"/> -
        ///    can retry through <see cref="Stop"/>/<see cref="Dispose"/>. The failure raised says
        ///    what is actually true: a live ffmpeg is on the camera.
        /// </summary>
        private Exception FailOpen(string logReason, string userReason, bool killFirst)
        {
            string err = _stderr.ToString();
            // Deliberately NOT written into the recording directory: a failed start must leave no
            // recording behind for the Library and the repair passes to find (AC8/AC9).
            Log.Error($"[FfmpegCameraRecorder] Open FAILED: camera=\"{DeviceName}\" {logReason} "
                      + $"cmd={CommandLine}{Environment.NewLine}{err}");

            // Nothing that happens from here is a mid-run loss - this camera never opened - so the
            // exit callback must stay quiet.
            _stopRequested = true;

            // A probe that timed out is looking at a LIVE process holding the webcam. Leaving it
            // there would trade the defect the gate found for a worse one.
            if (killFirst && !_proc.HasExited)
            {
                try { _proc.Kill(); }
                catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Open: killing the stalled ffmpeg for \"{DeviceName}\" failed", ex); }
                _proc.WaitForExit(KillTimeoutMs);
            }

            if (_proc.HasExited)
            {
                // Confirmed gone. Nothing left to stop, and nothing to write: short-circuit Stop and
                // Dispose so neither can put the ffmpeg log into the recording directory this failed
                // start must leave empty.
                _terminated = true;
                _disposed = true;
                _proc.Dispose();

                return new UsageException(
                    $"the camera \"{DeviceName}\" could not be opened {userReason}. "
                    + "Likely cause: " + DiagnoseOpenFailure(err, DeviceName));
            }

            // STILL RUNNING. _terminated stays false and the handle is NOT released: this object is
            // the only thing that can still reach that process, and its owner is holding it.
            Log.Error($"[FfmpegCameraRecorder] Open FAILED: the stalled ffmpeg for \"{DeviceName}\" is STILL "
                      + $"RUNNING after the kill - it still holds the camera and {OutputPath}. The process "
                      + "handle is KEPT so the recorder's owner can try again through Stop/Dispose.");

            return new CameraStopFailedException(
                DeviceName, OutputPath,
                "See the application log for the ffmpeg error from this camera.",
                $"the camera \"{DeviceName}\" could not be opened {userReason}");
        }

        private void OnStderrLine(string line)
        {
            _stderr.AppendLine(line);

            // The open probe's presence (gate defect 3): ffmpeg's own headers for the input it
            // opened and the file it is writing. See StartAndProbe for why these and not the tick.
            if (IsInputOpenReport(line)) _inputOpenReported = true;
            else if (IsOutputOpenReport(line)) _outputOpenReported = true;

            // ffmpeg writes its progress with a carriage return, which .NET treats as a line break,
            // so each "time=" tick arrives here as its own line. Shared with the screen recorder
            // rather than parsed a second way.
            long ms = FfmpegRecorder.ParseProgressMs(line);
            if (ms < 0) return;

            long previous = Interlocked.Exchange(ref _mediaMs, ms);

            // Strictly POSITIVE, and that is rule 6 in one line: ffmpeg prints ticks before it has
            // encoded anything ("time=00:00:00.00", and "time=N/A" earlier still), so a zero
            // position is not evidence that camera.mp4 contains a single frame.
            if (ms > 0) _wroteOutput = true;

            // AC13's instrument. Only an ADVANCE is recorded, never an arrival: ffmpeg goes on
            // printing progress lines - and prints a final summary on "q" - while a stalled device
            // leaves the position exactly where it stopped. Counting those repeats as activity is
            // precisely how a camera that recorded 0.5s of a 30-second session was certified.
            if (ms > 0 && ms > previous)
            {
                Interlocked.Exchange(ref _lastOutputAdvanceTicks, _now().Ticks);
                Interlocked.Increment(ref _outputAdvances);
            }
        }

        /// <summary>
        /// Decision 4 lives here: a camera that dies mid-run says so, loudly, in the log - and does
        /// NOT touch the screen recording. Exited fires for a clean stop too, which _stopRequested
        /// tells apart. This is a CONVENIENCE, not the authority: <see cref="Stop"/> re-reads the
        /// process itself, because this callback may simply not have been delivered yet.
        /// </summary>
        private void OnExited()
        {
            if (_stopRequested || !_opened) return;
            _lostMidRun = true;
            Log.Warn($"[FfmpegCameraRecorder] the camera \"{DeviceName}\" stopped during the recording "
                     + $"(ffmpeg exited on its own) - the screen recording continues; camera.mp4 is truncated at "
                     + $"{CapturedSeconds:F1}s. See {_logPath}");
        }

        /// <summary>
        /// Stop the camera and finalize camera.mp4.
        ///
        /// This NEVER throws for a camera that already died (decision 4): the loss was reported when
        /// it happened, the screen recording is the artifact that matters, and turning it into a stop
        /// failure here would mark an otherwise clean recording as failed.
        ///
        /// It DOES throw <see cref="CameraStopFailedException"/> when ffmpeg survives the quit AND
        /// the kill (gate defect 2). That is not the same event at all: the process is alive, it owns
        /// the webcam and the output file, and the caller must report a failed stop rather than go
        /// idle. Safe to call again - <see cref="Dispose"/> does exactly that, and the retry is the
        /// last chance the recording has to get the device back.
        ///
        /// It is also where rule 6 is decided (gate round 3, defect 3). The open probe proves the
        /// DEVICE opened; only ffmpeg's progress proves the FILE got anything. So once the process is
        /// confirmed gone, this waits for ffmpeg's stderr to be COMPLETE and then reports the track
        /// LOST if no output position was ever reported - whether the camera died on its own or
        /// answered "q" like a healthy one. A silent camera and a dead camera produce the same empty
        /// camera.mp4, and the manifest must say the same thing about both.
        /// </summary>
        public void Stop()
        {
            bool firstCall = !_stopRequested;
            // Taken on the FIRST stop only: the freshness of ffmpeg's output is judged against the
            // moment the USER asked for the stop, not against a later retry, which would give a
            // stalled camera the benefit of every second spent trying to kill it.
            if (firstCall)
            {
                Interlocked.Exchange(ref _stopRequestedTicks, _now().Ticks);
                Interlocked.Exchange(ref _advanceAtStopTicks, Interlocked.Read(ref _lastOutputAdvanceTicks));
                Interlocked.Exchange(ref _advanceCountAtStop, Interlocked.Read(ref _outputAdvances));
            }
            _stopRequested = true;
            if (_terminated) return;

            // Gate defect 4. Observed from the PROCESS, and observed here - before the quit below
            // makes "it has exited" ambiguous. A camera that died a moment before the user stopped,
            // whose Exited callback has not been delivered yet, is a mid-run loss, and the manifest
            // has to say so: writing CameraTruncated:false over a camera file that ends early tells
            // an editor the take is complete when it is not.
            //
            // ONLY WHILE NOTHING HAS BEEN OBSERVED YET (_stopKind == null). A RETRY - the Dispose
            // that follows a stop which could not kill ffmpeg - reaches this line again, and by then
            // "the process has exited" means something completely different: it means the ABANDONED
            // process finally died. Overwriting the recorded observation there would relabel a
            // camera that ignored the quit AND the kill as one that "died before the stop was
            // requested", making the durable record depend on when somebody next looked.
            if (_opened && _proc.HasExited && !_lostMidRun && _stopKind == null)
            {
                _lostMidRun = true;
                Log.Warn($"[FfmpegCameraRecorder] Stop: the camera \"{DeviceName}\" had already exited when the stop "
                         + $"arrived - camera.mp4 is truncated at {CapturedSeconds:F1}s. See {_logPath}");
            }

            // OBSERVED, not deduced: this process ended before anybody asked it to. Recorded here,
            // where it is still distinguishable from the quit that follows - and never over a kind
            // an earlier stop already observed.
            if (_lostMidRun && _stopKind == null) _stopKind = CameraStopKind.ExitedEarly;

            if (firstCall)
                Log.Info($"[FfmpegCameraRecorder] Stop: camera=\"{DeviceName}\" captured={CapturedSeconds:F1}s "
                         + $"lostMidRun={_lostMidRun} reportedEncodedOutput={_wroteOutput}");

            if (!_proc.HasExited)
            {
                if (_opened)
                {
                    try
                    {
                        _proc.SendQuit();
                        // The quit was DELIVERED. Recorded, because a subsequent exit can only be
                        // read as an ANSWER to a quit that actually arrived (gate round 4, defect 1).
                        _quitDelivered = true;
                    }
                    catch (Exception ex)
                    {
                        // stdin closes when ffmpeg exits; that is the mid-run-loss case, already reported.
                        Log.Warn($"[FfmpegCameraRecorder] Stop: could not send 'q' to the camera ffmpeg "
                                 + $"(\"{DeviceName}\"): {ex.Message}");
                    }

                    if (_proc.WaitForExit(QuitTimeoutMs))
                    {
                        // THE PROCESS IS GONE. WHETHER THAT WAS AN ANSWER TO "q" IS A DIFFERENT
                        // QUESTION, and gate round 4 defect 1 is what happens when the two are
                        // conflated: a failed write to stdin was caught and logged, the wait then
                        // saw a dead process, and a camera that had CRASHED (exit -5) was written
                        // down as clean-quit with the take recorded complete.
                        //
                        // Two presences are required, and the exit code is READ HERE - before
                        // anything can release the handle it needs:
                        //
                        //  1. the quit was delivered; and
                        //  2. the process was not terminated ABNORMALLY. A negative exit code is the
                        //     operating system reporting that (an NTSTATUS such as 0xC0000005
                        //     surfacing as a negative int), so ffmpeg never ran its own exit path and
                        //     cannot have written the MP4 trailer.
                        //
                        // A non-negative code is deliberately NOT held against the take: ffmpeg is
                        // not pinned to one build here (FfmpegLocator will take a bundled, PATH or
                        // winget ffmpeg) and different builds answer "q" with 0 or with 255. Reading
                        // every non-zero code as a broken take would turn AC17's positive control
                        // into "unknown" on somebody else's machine, which is the fail-open fix
                        // wearing the opposite mask.
                        int exitCode = _proc.ExitCode;
                        if (_quitDelivered && exitCode >= 0)
                        {
                            // It answered "q" and finalized the file itself. The ONLY stop kind that
                            // can lead to CameraComplete: yes - and on its own it is still not enough.
                            _stopKind = CameraStopKind.CleanQuit;
                        }
                        else
                        {
                            // The stop watched this process end and did NOT observe how. None of the
                            // four kinds describes it, so none of them is written: CameraStopKind is
                            // ABSENT in the manifest and Completeness answers "unknown", which is
                            // what the amended contract requires of every unanticipated case.
                            Log.Warn($"[FfmpegCameraRecorder] Stop: the camera ffmpeg (\"{DeviceName}\") ended with "
                                     + $"exit code {exitCode} after a quit that was "
                                     + (_quitDelivered ? "delivered" : "NEVER DELIVERED")
                                     + " - this is not a clean quit and camera.mp4 is not claimed complete");
                        }
                    }
                    else
                    {
                        Log.Warn($"[FfmpegCameraRecorder] Stop: the camera ffmpeg (\"{DeviceName}\") did not quit "
                                 + $"within {QuitTimeoutMs / 1000}s - killing it; camera.mp4 may be truncated");
                        KillOrThrow();
                    }
                }
                else
                {
                    // The retry a FAILED OPEN leaves behind (gate round 3, defect 1). A camera that
                    // never reported itself open has no finalized MP4 to protect, so it gets none of
                    // the "q" grace assumption A6 exists for - waiting 8 seconds to be polite to a
                    // process that is holding a webcam it never opened helps nobody.
                    Log.Warn($"[FfmpegCameraRecorder] Stop: the camera ffmpeg (\"{DeviceName}\") never reported the "
                             + "camera open and is STILL RUNNING - killing it");
                    KillOrThrow();
                }
            }
            else if (_stopKind == null)
            {
                // Already gone when the stop arrived, on a recorder that never reported the camera
                // open - so there was no mid-run to be lost from. It still ended before it was asked
                // to, and that is what gets written down.
                _stopKind = CameraStopKind.ExitedEarly;
            }

            // Only now: the process is CONFIRMED gone, on every path that reaches this line.
            _terminated = true;

            if (_opened)
            {
                // Read from COMPLETE stderr, and RECORDED rather than merely logged (AC15).
                // Process.WaitForExit(int) does not flush the asynchronous readers, so ffmpeg's last
                // progress tick can still be in flight when the process is already gone - "no tick
                // arrived" read off a half-delivered stream is not an absence, it is an unfinished
                // read, and Completeness refuses to say "yes" from one.
                _stderrComplete = _proc.DrainStderr(StderrDrainMs);
                if (!_stderrComplete)
                    Log.Warn($"[FfmpegCameraRecorder] Stop: the camera ffmpeg (\"{DeviceName}\") exited but its "
                             + $"stderr did not reach end of stream within {StderrDrainMs}ms - what follows is "
                             + "read from an INCOMPLETE log");

                if (!_wroteOutput && !_lostMidRun && _stderrComplete)
                {
                    // The case the header-based open probe hands to decision 4: ffmpeg said the
                    // camera and camera.mp4 were open, then never reported writing a single frame,
                    // and still answered "q" like a healthy process. CapturedSeconds is 0.0, so
                    // calling the track complete would put "a finished take of zero seconds" in the
                    // manifest and tell an editor the file is good.
                    //
                    // ONLY FROM A COMPLETE STDERR (gate round 4, defect 3). "camera.mp4 is EMPTY" is
                    // a positive claim about the file, and its whole evidence is the ABSENCE of a
                    // progress tick in this stream. Read off a stream that never reached end of
                    // stream that absence is not an absence at all - it is an unfinished read - and
                    // the drain above has just said in as many words that this one is unfinished.
                    _lostMidRun = true;
                    Log.Warn($"[FfmpegCameraRecorder] Stop: the camera \"{DeviceName}\" opened but never reported "
                             + $"writing any video - camera.mp4 is EMPTY and the track is recorded as truncated at "
                             + $"0.0s; the screen recording is unaffected. See {_logPath}");
                }
                else if (!_wroteOutput && !_lostMidRun)
                {
                    // The same silence, on evidence that is KNOWN to be incomplete. Nothing is
                    // concluded about camera.mp4 - not that it is empty, not that it is good - and
                    // the reason is written down where the person reading the log can see it.
                    Log.Warn($"[FfmpegCameraRecorder] Stop: the camera \"{DeviceName}\" reported no video, but its "
                             + $"stderr never reached end of stream - camera.mp4 is NOT being claimed empty and the "
                             + $"track is recorded as complete=unknown. See {_logPath}");
                }

                WriteFfmpegLog();
            }

            Log.Info($"[FfmpegCameraRecorder] Stop: camera=\"{DeviceName}\" done, {CapturedSeconds:F1}s in {OutputPath} "
                     + $"lostMidRun={_lostMidRun} stopKind={CameraObservation.Text(_stopKind) ?? "(not observed)"} "
                     + $"stderrComplete={_stderrComplete} complete={CameraObservation.Text(Completeness)}");

            // AC14. The process is gone - but it was SHOT rather than asked, so ffmpeg never wrote
            // the MP4 trailer and camera.mp4 was never finalized. Returning normally here is how a
            // force-killed file reached the manifest as a clean take through three rounds of this
            // fix: the manifest said "complete" while the code's own warning two screens up said the
            // file may be truncated. The caller decides what to do about it; it does not get to be
            // unaware of it.
            //
            // Only for a camera that OPENED: a recorder whose open failed has no take to make a
            // claim about, and its kill is the rollback working, not a failure to report.
            if (_stopKind == CameraStopKind.ForceKilled && _opened)
                throw new CameraForceKilledException(DeviceName, OutputPath, CapturedSeconds, $"See {_logPath}.");
        }

        /// <summary>
        /// Kill the process and CONFIRM it is gone, or throw. Returning normally from here means the
        /// operating system says the process has ended - never that a kill was issued (gate defect 2,
        /// and gate round 3 defect 1 on the start path).
        ///
        /// The kill's own exception is logged rather than propagated on purpose: "Kill threw" and
        /// "Kill returned but the process lived" are the same outcome, and both have to be judged by
        /// the wait that follows rather than by which of them happened.
        /// </summary>
        private void KillOrThrow()
        {
            try { _proc.Kill(); }
            catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Stop: kill failed for \"{DeviceName}\"", ex); }

            if (_proc.WaitForExit(KillTimeoutMs))
            {
                // Confirmed gone - but killed, not asked. The FILE and the PROCESS are two different
                // questions, and this answers only the second one (AC14).
                _stopKind = CameraStopKind.ForceKilled;
                return;
            }

            // ffmpeg is alive, it still holds an exclusive DirectShow device and still owns
            // camera.mp4. _terminated stays false, so Dispose gets one more attempt at it - and if
            // that one also fails, this is what the service reads to keep the recorder REACHABLE
            // instead of dropping the only handle to it (AC16). Recorded before the throw, so a
            // manifest written between here and the retry says "abandoned" rather than guessing.
            _stopKind = CameraStopKind.Abandoned;
            if (_opened) WriteFfmpegLog();
            Log.Error($"[FfmpegCameraRecorder] Stop FAILED: the camera ffmpeg (\"{DeviceName}\") survived "
                      + $"the kill and is still running - it still holds the camera and {OutputPath}");
            throw new CameraStopFailedException(
                DeviceName, OutputPath,
                _opened ? $"See {_logPath}." : "See the application log for the ffmpeg error from this camera.",
                _opened ? "the recording was stopped" : "the camera never opened");
        }

        private void WriteFfmpegLog()
        {
            try { File.WriteAllText(_logPath, _stderr.ToString()); }
            catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] writing {_logPath} failed", ex); }
        }

        /// <summary>
        /// Turn ffmpeg's stderr into an accurate, actionable cause for a camera that would not open.
        /// Pure string inspection - safe to unit test, and it never guesses past what the text says.
        /// </summary>
        internal static string DiagnoseOpenFailure(string stderr, string dshowCameraName)
        {
            stderr ??= "";
            // The wording below is what ffmpeg 9.0 actually prints for a webcam held by another
            // process - observed verbatim while implementing this (a browser had the camera):
            //   [dshow @ ...] Could not run graph (sometimes caused by a device already in use by
            //   other application)
            //   [in#0 @ ...] Error opening input: I/O error
            if (stderr.Contains("already in use", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Could not run graph", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Could not run filter", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Device or resource busy", StringComparison.OrdinalIgnoreCase))
                return $"the camera \"{dshowCameraName}\" is already in use by another application.";
            if (stderr.Contains("Could not find video device", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("no device found", StringComparison.OrdinalIgnoreCase))
                return $"no DirectShow device is named \"{dshowCameraName}\" any more (was it unplugged?).";
            if (stderr.Contains("Could not set video options", StringComparison.OrdinalIgnoreCase))
                return $"the camera \"{dshowCameraName}\" rejected the requested frame rate.";
            return $"see the application log for the ffmpeg error from \"{dshowCameraName}\".";
        }

        /// <summary>
        /// Last owner of the process. Retries the stop when one is still owed - a stop that threw
        /// because ffmpeg would not die left <c>_terminated</c> false precisely so this can try
        /// again. This is an entry point (it runs from a using/finally and from the stop sequence),
        /// so it reports rather than propagates: the caller is usually already carrying a failure.
        ///
        /// GATE ROUND 3, DEFECT 2. It used to suppress the retry's failure and then release the
        /// process wrapper ANYWAY, which reads as tidy-up and is the opposite: closing the handle
        /// does not terminate ffmpeg (<see cref="ICameraProcess.Dispose"/> disposes a
        /// <see cref="System.Diagnostics.Process"/>, nothing more), it only throws away the last
        /// thing in this process that could still reach a live recorder holding the webcam and
        /// camera.mp4. So the handle is released ONLY once the OS process is confirmed gone. When it
        /// is not, this object stays valid and stays loud, and <see cref="Stop"/> can be called
        /// again.
        ///
        /// AND THERE IS NOW SOMETHING HOLDING IT. Gate round 3 was right that keeping a handle
        /// inside an object nobody references keeps nothing: this returns with
        /// <see cref="IsAbandoned"/> true, and <see cref="StrandedCameraOwner"/> - which the service
        /// consults on both the stop and the failed-start path - takes the recorder off the session
        /// and keeps it, with its recording claim, until the process is finally gone (AC16).
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            if (!_terminated)
            {
                try { Stop(); }
                catch (Exception ex) { Log.Error($"[FfmpegCameraRecorder] Dispose: stopping \"{DeviceName}\" failed", ex); }
            }

            if (!_terminated)
            {
                Log.Error($"[FfmpegCameraRecorder] Dispose: the camera ffmpeg (\"{DeviceName}\") is STILL RUNNING "
                          + $"after a second termination attempt - it still holds the camera and {OutputPath}. The "
                          + "process handle is KEPT (disposing it would not end the process, only hide it); this "
                          + "recorder can still be stopped again.");
                return;
            }

            _disposed = true;
            _proc.Dispose();
        }
    }
}
