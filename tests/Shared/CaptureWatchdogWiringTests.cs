using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

// Issue #113, round two. ONE source file, compiled into all three suites like CaptureWatchdogTests.cs.
//
// WHY THIS FILE EXISTS. On round one QA deleted `StartWatchdog(_video!);` from
// RecordingService.StartVideo - disconnecting the guard from the product - and the whole suite stayed
// green, because every watchdog test exercised CaptureWatchdog on its own and nothing exercised the
// service that runs it. The defect QA failed the issue on (the verdict reached /status only after a
// 13.5 s stop) lived in exactly that untested wiring.
//
// These tests drive the REAL RecordingService - StartVideo, the watchdog it starts, the verdict, the
// stop, the manifest - through RecordingServiceSeams, with a capture the test controls: a real file
// the test grows (the watchdog stats it exactly as in production), a clock the test moves, and a timer
// the test fires. No ffmpeg, no display, no waiting in real time except where a test deliberately
// blocks the capture's abandon to look at /status while the stop is in progress.
//
// EVERY ARM IS STATED. The "fires" assertions are presences (a named fault, LastStopFailed=true, a
// manifest field with a value, a timer that exists and was armed). A service that never starts the
// watchdog creates no timer: WatchdogTimer is null and every test here fails on that, by name, rather
// than passing on an absence.
namespace AgentEyes.Tests.Shared
{
    using AudioMixOptions = global::AgentEyes.AudioMixOptions;
    using AudioSourceKind = global::AgentEyes.AudioSourceKind;
    using CaptureFault = global::AgentEyes.Video.CaptureFault;
    using CaptureWatchdog = global::AgentEyes.Video.CaptureWatchdog;
    using FfmpegRecorder = global::AgentEyes.Video.FfmpegRecorder;
    using IScreenRecorder = global::AgentEyes.Video.IScreenRecorder;
    using IWatchdogTimer = global::AgentEyes.IWatchdogTimer;
    using MonitorInfo = global::AgentEyes.MonitorInfo;
    using RecordingService = global::AgentEyes.RecordingService;
    using RecordingServiceSeams = global::AgentEyes.RecordingServiceSeams;
    using RecordingStopFailedException = global::AgentEyes.RecordingStopFailedException;

    /// <summary>An ordered record of what happened, shared by the fakes, so a test can assert order.</summary>
    internal sealed class EventLog
    {
        private readonly List<string> _events = new();
        public void Add(string e) { lock (_events) _events.Add(e); }
        public IReadOnlyList<string> Snapshot() { lock (_events) return _events.ToList(); }
        public int IndexOf(string e) { lock (_events) return _events.IndexOf(e); }
    }

    /// <summary>A watchdog timer the test fires by hand.</summary>
    internal sealed class FakeWatchdogTimer : IWatchdogTimer
    {
        private readonly Action _tick;
        private readonly EventLog _log;
        public FakeWatchdogTimer(Action tick, EventLog log) { _tick = tick; _log = log; }
        public int ArmCount { get; private set; }
        public TimeSpan? LastDue { get; private set; }
        public bool Disposed { get; private set; }
        public void Arm(TimeSpan due) { ArmCount++; LastDue = due; _log.Add("watchdog armed"); }
        public void Dispose() { Disposed = true; _log.Add("watchdog disposed"); }

        /// <summary>Run one check, as the real timer would on its own thread.</summary>
        public void Fire() => _tick();
    }

    /// <summary>
    /// A screen capture the test controls. Its output is a REAL file, so the service's watchdog reads
    /// it through the same FileSize call production uses. Abandon can be held open, so a test can read
    /// /status while the stop is still in progress.
    /// </summary>
    internal sealed class FakeScreenRecorder : IScreenRecorder
    {
        private readonly EventLog _log;
        public FakeScreenRecorder(string outputPath, EventLog log)
        {
            OutputPath = outputPath;
            _log = log;
            File.WriteAllBytes(outputPath, Array.Empty<byte>());
        }

        public string OutputPath { get; }
        public string CommandLine => "fake-capture " + OutputPath;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        /// <summary>
        /// Issue #156 records the capture writer's pid and start time in the claim and in the ffmpeg
        /// child registry. The fake has no process of its own, so it reports the test host's: a
        /// process that is provably running for the whole test (NoteWriter reads its start time), and
        /// one no reap can ever kill on its behalf - a reap needs the owner gone, and the owner is this
        /// same process. A made-up pid would either fail NoteWriter or name a stranger's process.
        /// </summary>
        public int ProcessId { get; } = Environment.ProcessId;
        public int? ExitCode { get; set; }
        public double MediaSeconds { get; set; } = 20.0;

        /// <summary>Issue #114's alignment reading. Null here: nothing in these tests aligns a mic.</summary>
        public double? InputStartSeconds { get; set; }

        public ManualResetEventSlim AbandonEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseAbandon { get; } = new(true);
        public volatile bool AbandonReturned;
        public string? AbandonReason;

        public void Grow(int bytes)
        {
            using var f = new FileStream(OutputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            f.Write(new byte[bytes], 0, bytes);
        }

        public void Stop() => _log.Add("video stop");

        public void Abandon(string reason)
        {
            _log.Add("video abandon entered");
            AbandonReason = reason;
            AbandonEntered.Set();
            if (!ReleaseAbandon.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("the test never released the abandon");
            AbandonReturned = true;
            _log.Add("video abandon returned");
        }

        public void Dispose() => _log.Add("video dispose");
    }

    /// <summary>One RecordingService wired to fakes, plus everything a test needs to drive it.</summary>
    internal sealed class WatchdogHarness : IDisposable
    {
        public readonly EventLog Log = new();
        public readonly FakeCaptureClock Clock = new();
        public readonly string Root;
        public readonly List<string> PostProcessed = new();
        public readonly ManualResetEventSlim PostProcessRan = new(false);
        public FakeScreenRecorder? Recorder;
        public FakeWatchdogTimer? WatchdogTimer;
        public string? Dir;
        public readonly RecordingService Service;
        private int _starts;

        /// <summary>What the fake probe says the abandoned file holds.</summary>
        public const double ProbedSeconds = 18.73;

        public WatchdogHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), "agenteyes-wiring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Service = new RecordingService(new RecordingServiceSeams
            {
                RequireMonitor = n => new MonitorInfo { Index = n, Name = "fake", Width = 640, Height = 480, Primary = true },
                NewRecordingDir = (mode, label) =>
                {
                    Dir = Path.Combine(Root, $"rec{++_starts}_{label}");
                    Directory.CreateDirectory(Path.Combine(Dir, "shots"));
                    return Dir;
                },
                StartScreen = (capture, mic, fps, crf, outPath, preview, screen) =>
                {
                    Log.Add("video start");
                    Recorder = new FakeScreenRecorder(outPath, Log);
                    return Recorder;
                },
                NewWatchdogClock = () => Clock,
                NewWatchdogTimer = tick =>
                {
                    WatchdogTimer = new FakeWatchdogTimer(tick, Log);
                    return WatchdogTimer;
                },
                ProbeDurationSeconds = _ => ProbedSeconds,
                PostProcess = dir =>
                {
                    lock (PostProcessed) PostProcessed.Add(dir);
                    PostProcessRan.Set();
                },
            });
        }

        public void StartVideo() =>
            Service.StartVideo(1, AudioSourceKind.None, null, null, new AudioMixOptions(), 30);

        /// <summary>Advance one check interval, optionally grow the file, and fire the check.</summary>
        public void Check(int growBy = 0)
        {
            Clock.Advance(CaptureWatchdog.DefaultCheckInterval.TotalSeconds);
            if (growBy > 0) Recorder!.Grow(growBy);
            Require(WatchdogTimer, "StartVideo created no watchdog timer - the watchdog is not wired in").Fire();
        }

        /// <summary>Grow for a few checks, then stop growing and check until one check short of the
        /// stall verdict. The next <see cref="Check"/> decides.</summary>
        public void RunUpToTheVerdict()
        {
            for (int i = 0; i < 5; i++) Check(growBy: 40_000);
            int quiet = (int)Math.Ceiling(CaptureWatchdog.DefaultStallTimeout.TotalSeconds
                                          / CaptureWatchdog.DefaultCheckInterval.TotalSeconds) - 1;
            for (int i = 0; i < quiet; i++) Check();
        }

        public JsonElement ReadManifest()
        {
            string path = Path.Combine(Require(Dir, "no recording directory was created"), "manifest.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }

        public static T Require<T>(T? value, string why) where T : class =>
            value ?? throw new Xunit.Sdk.XunitException(why);

        public void Dispose()
        {
            if (Recorder != null) Recorder.ReleaseAbandon.Set();
            try { if (Service.IsRecording) Service.Stop(); } catch (Exception) { }
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// One collection for every test that starts a capture through <see cref="WatchdogHarness"/>, so
    /// they run one at a time. Since #156 the service records each capture writer's pid in the ffmpeg
    /// child registry, whose record file is named by pid; every <see cref="FakeScreenRecorder"/>
    /// reports the test host's own pid, so two harness tests in parallel opened the same record file
    /// and failed on a sharing violation (measured on Windows on the #156 + #113 merge). A real writer's
    /// pid is unique, so this is a property of the fake, not of the product.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class WatchdogHarnessCollection { public const string Name = "watchdog harness (one fake pid)"; }

    [Collection(WatchdogHarnessCollection.Name)]
    public sealed class CaptureWatchdogWiringTests
    {
        [Fact]
        public void StartVideo_StartsTheWatchdog_OnTheCaptureFile()
        {
            using var h = new WatchdogHarness();
            h.StartVideo();

            // Expected: a timer exists and is armed for the first check, and /status describes the
            // capture file as starting. Bad (the round-one mutant): no timer at all.
            var timer = WatchdogHarness.Require(h.WatchdogTimer, "StartVideo created no watchdog timer");
            Assert.Equal(1, timer.ArmCount);
            Assert.Equal(CaptureWatchdog.DefaultCheckInterval, timer.LastDue);
            var started = h.Service.Status();
            Assert.Equal("recording", started.State);
            Assert.Equal("starting", started.CaptureLiveness);
            Assert.Equal("recording.mp4", started.CaptureFile);

            // And it is watching THAT file: growth on it reaches /status.
            h.Check(growBy: 12_345);
            var growing = h.Service.Status();
            Assert.Equal("growing", growing.CaptureLiveness);
            Assert.Equal(12_345, growing.CaptureBytes);
            Assert.Equal(2, timer.ArmCount);    // re-armed for the next check
        }

        [Fact]
        public void Status_AtDetection_NamesTheFaultBeforeTheWriterStopReturns()
        {
            using var h = new WatchdogHarness();
            h.StartVideo();
            h.RunUpToTheVerdict();
            Assert.Equal("recording", h.Service.Status().State);
            Assert.Null(h.Service.Status().LastCaptureFault);

            // Hold the capture's abandon open: this is the window QA polled on round one, where the
            // service had decided and /status still read like a clean stop.
            h.Recorder!.ReleaseAbandon.Reset();
            var verdict = Task.Run(() => h.Check());
            Assert.True(h.Recorder.AbandonEntered.Wait(TimeSpan.FromSeconds(10)),
                "the verdict check never reached the capture's abandon");

            var during = h.Service.Status();
            Assert.False(h.Recorder.AbandonReturned, "the writer stop had already returned - the test proves nothing");
            Assert.Equal("finalizing", during.State);
            Assert.Equal("faulted", during.CaptureLiveness);
            Assert.True(during.LastStopFailed, "LastStopFailed=false while the watchdog is ending the recording");
            Assert.NotNull(during.LastCaptureFault);
            Assert.StartsWith("capture stalled: recording.mp4 stopped growing at 200000 bytes", during.LastCaptureFault);
            Assert.Equal(h.Dir, during.LastCaptureFaultDir);
            Assert.Equal(FakeCaptureClock.Origin + h.Clock.Monotonic, during.LastCaptureFaultUtc);
            Assert.Contains("capture watchdog: capture stalled", during.LastStopError);
            Assert.Equal(h.Dir, during.LastStopDir);

            h.Recorder.ReleaseAbandon.Set();
            Assert.True(verdict.Wait(TimeSpan.FromSeconds(10)), "the stop did not finish after the abandon returned");
            Assert.Equal("idle", h.Service.Status().State);
        }

        [Fact]
        public void WatchdogEnded_Recording_IsAFailedStop_AbandonedNotDrained_WithTheManifestFields()
        {
            using var h = new WatchdogHarness();
            h.StartVideo();
            h.RunUpToTheVerdict();
            h.Check();      // the verdict, run to completion on this thread

            var after = h.Service.Status();
            Assert.Equal("idle", after.State);
            Assert.True(after.LastStopFailed);
            Assert.StartsWith("capture watchdog: capture stalled", after.LastStopError);
            Assert.StartsWith("capture stalled: recording.mp4", after.LastCaptureFault);
            Assert.NotNull(h.Service.LastStopFailure);
            Assert.True(h.Service.LastStopFailure!.HasManifest, "the final stop report has no manifest on disk");

            // Abandoned (killed), never drained: Stop is the drain-and-quit path.
            var events = h.Log.Snapshot();
            Assert.Contains("video abandon returned", events);
            Assert.DoesNotContain("video stop", events);
            Assert.StartsWith("capture stalled: recording.mp4", h.Recorder!.AbandonReason);

            var m = h.ReadManifest();
            Assert.Equal("stalled", m.GetProperty("CaptureFault").GetString());
            Assert.StartsWith("recording.mp4 stopped growing at 200000 bytes", m.GetProperty("CaptureFaultDetail").GetString());
            Assert.Equal(WatchdogHarness.ProbedSeconds, m.GetProperty("CapturedSeconds").GetDouble());
            Assert.True(m.GetProperty("CaptureFaultElapsedSeconds").GetDouble() >= CaptureWatchdog.DefaultStallTimeout.TotalSeconds);
            Assert.Equal(FakeCaptureClock.Origin + h.Clock.Monotonic,
                m.GetProperty("CaptureFaultDetectedUtc").GetDateTime().ToUniversalTime());

            // The verdict was the only failure, so what was captured is post-processed at once.
            Assert.True(h.PostProcessRan.Wait(TimeSpan.FromSeconds(10)), "post-processing was never queued");
            lock (h.PostProcessed) Assert.Equal(new[] { h.Dir }, h.PostProcessed);
        }

        [Fact]
        public void WatchdogEnded_DeadCapture_IsNamedAtTheNextCheck()
        {
            using var h = new WatchdogHarness();
            h.StartVideo();
            h.Check(growBy: 40_000);
            h.Recorder!.ExitCode = 137;
            h.Check();

            var after = h.Service.Status();
            Assert.Equal("idle", after.State);
            Assert.True(after.LastStopFailed);
            Assert.StartsWith("capture exited: the capture process exited (code 137)", after.LastCaptureFault);
            Assert.Equal("exited", h.ReadManifest().GetProperty("CaptureFault").GetString());
        }

        [Fact]
        public void UserStop_TakesTheWatchdogDownBeforeTheWritersStop()
        {
            using var h = new WatchdogHarness();
            h.StartVideo();
            h.Check(growBy: 40_000);
            var timer = WatchdogHarness.Require(h.WatchdogTimer, "StartVideo created no watchdog timer");

            h.Service.Stop();

            var events = h.Log.Snapshot();
            int down = h.Log.IndexOf("watchdog disposed");
            int writer = h.Log.IndexOf("video stop");
            Assert.True(down >= 0, "the watchdog was never taken down: " + string.Join(", ", events));
            Assert.True(writer >= 0, "the capture was never stopped: " + string.Join(", ", events));
            Assert.True(down < writer, "the watchdog came down AFTER the capture was stopped: " + string.Join(", ", events));
            Assert.DoesNotContain("video abandon entered", events);

            var after = h.Service.Status();
            Assert.Equal("idle", after.State);
            Assert.False(after.LastStopFailed);
            Assert.Null(after.LastCaptureFault);

            // A check that was already in flight when the user stopped acts on nothing.
            int armed = timer.ArmCount;
            timer.Fire();
            Assert.Equal(armed, timer.ArmCount);
            Assert.False(h.Service.Status().LastStopFailed);
        }

        [Fact]
        public void UserStop_AfterTheVerdictWasPublished_CarriesTheVerdict()
        {
            // The race between the verdict and EndFaultedCapture's own Stop: whichever stop runs takes
            // the verdict, so the user's stop is the failed stop and the manifest still says why.
            using var h = new WatchdogHarness();
            h.StartVideo();
            h.RunUpToTheVerdict();
            h.Recorder!.ReleaseAbandon.Reset();
            var verdict = Task.Run(() => h.Check());
            Assert.True(h.Recorder.AbandonEntered.Wait(TimeSpan.FromSeconds(10)));

            // While the watchdog's stop is in progress a user stop is refused, as for any finalizing
            // recording - and that refusal does not clear what /status says.
            Assert.Throws<global::AgentEyes.UsageException>(() => h.Service.Stop());
            Assert.True(h.Service.Status().LastStopFailed);

            h.Recorder.ReleaseAbandon.Set();
            Assert.True(verdict.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal("stalled", h.ReadManifest().GetProperty("CaptureFault").GetString());
        }

        [Fact]
        public void NextRecording_ClearsThePreviousVerdict()
        {
            using var h = new WatchdogHarness();
            h.StartVideo();
            h.Check(growBy: 40_000);
            h.Recorder!.ExitCode = 137;
            h.Check();
            Assert.True(h.Service.Status().LastStopFailed);

            h.StartVideo();
            var next = h.Service.Status();
            Assert.Equal("recording", next.State);
            Assert.False(next.LastStopFailed);
            Assert.Null(next.LastCaptureFault);
            h.Service.Stop();
        }
    }

    /// <summary>
    /// The real <see cref="FfmpegRecorder.Abandon"/> against a real process that ignores the 'q' quit,
    /// the way a frozen ffmpeg does. Round one's Abandon ran the drain-and-quit Stop on it: 5 s of
    /// drain, an 8 s quit wait, then the kill - about 13.5 s, measured by QA on a SIGSTOPped ffmpeg.
    /// </summary>
    public sealed class RecorderAbandonTests
    {
        [Fact]
        public void Abandon_OnAProcessThatIgnoresTheQuit_KillsItWithinTheAbandonBound()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-abandon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("ping", "-n 600 127.0.0.1")
                : new ProcessStartInfo("sleep", "600");
            psi.RedirectStandardInput = true;   // as the real capture: Stop would write its 'q' here
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start the stand-in process");
            using var rec = FfmpegRecorder.ForStartedProcess(proc, Path.Combine(dir, "raw.mp4"));
            try
            {
                Assert.Null(rec.ExitCode);

                var sw = Stopwatch.StartNew();
                rec.Abandon("test: capture stalled");
                sw.Stop();

                Assert.True(proc.HasExited, "Abandon returned with the process still running");
                Assert.NotNull(rec.ExitCode);
                Assert.True(sw.Elapsed < FfmpegRecorder.AbandonExitWait,
                    $"Abandon took {sw.Elapsed.TotalSeconds:F2}s; the bound budgeted in the 60 s window is "
                    + $"{FfmpegRecorder.AbandonExitWait.TotalSeconds:F0}s");
            }
            finally
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }
}
