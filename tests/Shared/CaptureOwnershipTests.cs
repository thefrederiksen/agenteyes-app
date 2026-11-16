using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Non-parallel: these tests swap process-wide seams (<see cref="PostRecording.MuxStep"/>,
    /// <see cref="CaptureLock.IsSameProcess"/>, <see cref="CaptureLock.HostName"/>) that other test
    /// classes also read.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class CaptureOwnershipCollection
    {
        public const string Name = "CaptureOwnership (issue #156)";
    }

    /// <summary>
    /// Issue #156: a capture's claim on its recording directory must be visible to OTHER processes,
    /// and a claim whose owner died must not strand the recording.
    ///
    /// Compiled into all three suites, because the lock is a different OS mechanism on each (a
    /// Windows sharing violation; an exclusive flock on macOS and Linux). "Another process" is stood
    /// in for by an independent handle on the lock file - a sharing violation and a flock conflict
    /// are both per open handle, so the OS refuses a second handle in this process exactly as it
    /// refuses another process's - plus one test with a genuinely separate holder process that is
    /// then killed (<see cref="TryAcquire_HolderProcessKilled_ClaimIsFreedByTheOs"/>).
    ///
    /// BOTH ARMS ARE HERE ON PURPOSE. A guard that refuses everything passes every "live capture is
    /// left alone" test; the positive controls (a dead owner's recording IS repaired, a recording no
    /// capture ever claimed IS repaired) are what tell a fix from a wall.
    /// </summary>
    [Collection(CaptureOwnershipCollection.Name)]
    public sealed class CaptureOwnershipTests : IDisposable
    {
        private readonly string _dir;
        private readonly Func<int, DateTime, bool> _realIsSame = CaptureLock.IsSameProcess;
        private readonly Func<string> _realHost = CaptureLock.HostName;
        private readonly Func<string> _realHostId = CaptureLock.ThisHostId;
        private readonly List<string> _muxCalls = new();

        public CaptureOwnershipTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agenteyes-capture-" + Guid.NewGuid().ToString("N"), "2026-11-16_120000_video");
            Directory.CreateDirectory(_dir);
            PostRecording.MuxStep = MuxStub;
            PostRecording.ThumbnailStep = _ => { };
            PostRecording.ComposeStep = _ => { };
        }

        public void Dispose()
        {
            CaptureLock.IsSameProcess = _realIsSame;
            CaptureLock.HostName = _realHost;
            CaptureLock.ThisHostId = _realHostId;
            // Tests that record THIS process as a writer leave its reap record behind (a running
            // writer's record is kept on release, by design); it is never this suite's to keep.
            Video.FfmpegChildRegistry.Unregister(Environment.ProcessId);
            PostRecording.RestoreDefaultSteps();
            PostRecording.ComposeStep = dir => CameraCompose.Run(dir);
            RecordingWorkset.ReleaseForTests(_dir);
            try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); }
            catch (IOException) { }
        }

        // ---- fixtures ------------------------------------------------------------

        private string LockPath => Path.Combine(_dir, CaptureLock.FileName);
        private string OwnerPath => Path.Combine(_dir, CaptureLock.OwnerFileName);

        /// <summary>A mux that does what a successful FinalizePending does to the files, and counts.</summary>
        private void MuxStub(string dir)
        {
            _muxCalls.Add(dir);
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "muxed");
            ManifestStore.Update(dir, m => { m.PendingMux = null; m.DurationSeconds = 12.0; });
        }

        /// <summary>The START manifest a live capture leaves on disk, plus its raw file.</summary>
        private void WriteStartManifest()
        {
            File.WriteAllText(Path.Combine(_dir, "raw.mp4"), "still being written");
            ManifestStore.Replace(_dir, new Manifest
            {
                Mode = "video",
                Label = "video",
                CreatedUtc = "2026-11-16T12:00:00Z",
                PendingMux = new Manifest.PendingMuxInfo
                {
                    Mode = "video", Source = "mic", RawVideo = "raw.mp4", FinalFile = "recording.mp4",
                },
            });
        }

        /// <summary>What another process's capture leaves: its owner record, and (optionally) its
        /// lock held by an independent handle.</summary>
        private FileStream? HoldLikeAnotherProcess(bool holdLock, int ownerPid = 424242, string host = "OTHERBOX",
            params CaptureLock.WriterRecord[] writers)
        {
            var owner = new CaptureLock.OwnerRecord
            {
                Host = host, Pid = ownerPid, What = "capture session",
                StartedUtc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc),
                Writers = new List<CaptureLock.WriterRecord>(writers),
            };
            File.WriteAllText(OwnerPath, JsonSerializer.Serialize(owner));
            return holdLock ? new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
        }

        private static CaptureLock.WriterRecord Writer(int pid) =>
            new() { Label = "video", Pid = pid, StartUtc = new DateTime(2026, 11, 16, 12, 0, 1, DateTimeKind.Utc) };

        private bool IsLockedOnDisk()
        {
            try
            {
                using var probe = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private Manifest.PostStageRecord? MuxRecord() => PostRecordingState.Get(Manifest.Load(_dir), PostStage.Mux);

        // ---- the capture side ----------------------------------------------------

        [Fact]
        public void TryAcquire_Free_HoldsTheLockAndPublishesTheOwnerUntilDisposed()
        {
            Assert.False(IsLockedOnDisk(), "broken fixture: the lock is held before anything took it");

            var held = CaptureLock.TryAcquire(_dir, "capture session", out string refusal);

            Assert.NotNull(held);
            Assert.Equal("", refusal);
            Assert.True(IsLockedOnDisk(), "the capture lock was acquired but another handle could still open capture.lock");
            var owner = JsonSerializer.Deserialize<CaptureLock.OwnerRecord>(File.ReadAllText(OwnerPath))!;
            Assert.Equal(Environment.ProcessId, owner.Pid);
            Assert.Equal(Environment.MachineName, owner.Host);

            held!.Dispose();
            Assert.False(IsLockedOnDisk(), "the capture lock was released but capture.lock is still locked");
            Assert.False(File.Exists(OwnerPath), "a cleanly released claim must not leave an owner record behind");
        }

        [Fact]
        public void TryAcquire_HeldByAnotherHandle_RefusesNamingTheHolder()
        {
            using var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 31337);

            var held = CaptureLock.TryAcquire(_dir, "capture session", out string refusal);

            Assert.Null(held);
            Assert.Contains("pid 31337", refusal);
            Assert.Contains("OTHERBOX", refusal);
        }

        [Fact]
        public void TryAcquire_SecondTimeInThisProcess_IsRefused()
        {
            using var first = CaptureLock.TryAcquire(_dir, "capture session", out _);
            Assert.NotNull(first);

            Assert.Null(CaptureLock.TryAcquire(_dir, "capture session", out string refusal));
            Assert.Contains("this process", refusal);
        }

        [Fact]
        public void NoteWriter_RecordsThePidAndItsStartTime()
        {
            using var held = CaptureLock.TryAcquire(_dir, "capture session", out _)!;
            using var me = Process.GetCurrentProcess();

            held.NoteWriter("video", me.Id);

            var owner = JsonSerializer.Deserialize<CaptureLock.OwnerRecord>(File.ReadAllText(OwnerPath))!;
            var w = Assert.Single(owner.Writers);
            Assert.Equal("video", w.Label);
            Assert.Equal(me.Id, w.Pid);
            Assert.True((w.StartUtc - me.StartTime.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(1),
                $"the writer's recorded start {w.StartUtc:o} is not the process start {me.StartTime.ToUniversalTime():o}");
        }

        /// <summary>The genuinely cross-process case: a separate OS process holds capture.lock exactly as
        /// a capture does, and is then killed. Its death must free the claim with nothing to clean up.</summary>
        [Fact]
        public void TryAcquire_HolderProcessKilled_ClaimIsFreedByTheOs()
        {
            File.WriteAllText(LockPath, "");
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("powershell.exe");
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                             $"$s=[IO.File]::Open('{LockPath}','OpenOrCreate','ReadWrite','None'); 'LOCKED'; [Console]::Out.Flush(); Start-Sleep 60" })
                    psi.ArgumentList.Add(a);
            }
            else
            {
                // flock(2), the call .NET makes for FileShare.None on macOS and Linux. python3 ships on
                // the macOS and Ubuntu CI images; its absence is a broken fixture, not a pass.
                psi = new ProcessStartInfo("python3");
                foreach (var a in new[] { "-c",
                             "import fcntl,os,sys,time\nfd=os.open(sys.argv[1],os.O_RDWR)\nfcntl.flock(fd,fcntl.LOCK_EX)\nprint('LOCKED',flush=True)\ntime.sleep(60)",
                             LockPath })
                    psi.ArgumentList.Add(a);
            }
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var holder = Process.Start(psi)!;
            try
            {
                string? line = holder.StandardOutput.ReadLine();
                Assert.True(line == "LOCKED", $"broken fixture: the holder process did not report holding the lock (got '{line}')");

                Assert.True(CaptureLock.Inspect(_dir).Live, "a capture lock held by another process was not seen as live");
                Assert.Null(CaptureLock.TryAcquire(_dir, "capture session", out _));

                holder.Kill(entireProcessTree: true);
                holder.WaitForExit();

                Assert.False(CaptureLock.Inspect(_dir).Live, "the claim was still live after the process holding it was killed");
                using var after = CaptureLock.TryAcquire(_dir, "capture session", out string refusal);
                Assert.True(after != null, $"the lock was still refused after its holder died: {refusal}");
            }
            finally
            {
                if (!holder.HasExited) holder.Kill(entireProcessTree: true);
            }
        }

        // ---- the finalize side: what is live and what is stale -------------------

        [Fact]
        public void Inspect_NoCaptureEverClaimedIt_IsNotLive()
        {
            var r = CaptureLock.Inspect(_dir);
            Assert.False(r.Live, r.Reason);
            Assert.False(File.Exists(LockPath), "a probe must not create capture.lock in a directory no capture claimed");
        }

        [Fact]
        public void Inspect_LockHeldByAnotherHandle_IsLiveAndNamesTheHolder()
        {
            using var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 8304);

            var r = CaptureLock.Inspect(_dir);

            Assert.True(r.Live);
            Assert.Contains("pid 8304", r.Reason);
        }

        [Fact]
        public void Inspect_HeldByACaptureInThisProcess_IsLive()
        {
            using var held = CaptureLock.TryAcquire(_dir, "capture session", out _);
            Assert.True(CaptureLock.Inspect(_dir).Live);
        }

        [Fact]
        public void Inspect_OwnerDeadButItsWriterStillRunning_IsLiveAndNamesTheWriter()
        {
            CaptureLock.HostName = () => "OTHERBOX";
            CaptureLock.IsSameProcess = (pid, _) => pid == 91047;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91100, host: "OTHERBOX", writers: new[] { Writer(91047) });

            var r = CaptureLock.Inspect(_dir);

            Assert.True(r.Live, "a dead owner's still-running ffmpeg was read as a free recording");
            Assert.Contains("pid 91047", r.Reason);
            Assert.Contains("STILL RUNNING", r.Reason);
        }

        /// <summary>Criterion 4, positive arm: what makes a claim stale is proof, not age.</summary>
        [Fact]
        public void Inspect_OwnerDeadAndWritersExited_IsNotLiveAndSaysItTookOver()
        {
            CaptureLock.HostName = () => "OTHERBOX";
            CaptureLock.IsSameProcess = (_, _) => false;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91100, host: "OTHERBOX", writers: new[] { Writer(91047) });

            var r = CaptureLock.Inspect(_dir);

            Assert.False(r.Live, r.Reason);
            Assert.Contains("stale capture claim taken over", r.Reason);
            Assert.Contains("pid 91100", r.Reason);
        }

        [Fact]
        public void Inspect_PidReusedByAnotherProcess_IsNotLive()
        {
            // The real probe: this test process's pid, but a start time it never had - a reused pid.
            CaptureLock.HostName = () => "OTHERBOX";
            HoldLikeAnotherProcess(holdLock: false, host: "OTHERBOX",
                writers: new[] { new CaptureLock.WriterRecord { Label = "video", Pid = Environment.ProcessId, StartUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) } });

            Assert.False(CaptureLock.Inspect(_dir).Live, "a reused pid was taken for the dead owner's writer");
        }

        [Fact]
        public void Inspect_WriterOnAnotherHost_IsLiveWithTheExactFix()
        {
            CaptureLock.HostName = () => "THISBOX";
            CaptureLock.IsSameProcess = (_, _) => throw new InvalidOperationException("a foreign host's pid must never be probed locally");
            HoldLikeAnotherProcess(holdLock: false, host: "OTHERBOX", writers: new[] { Writer(5555) });

            var r = CaptureLock.Inspect(_dir);

            Assert.True(r.Live);
            Assert.Contains("OTHERBOX", r.Reason);
            Assert.Contains(CaptureLock.OwnerFileName, r.Reason);
        }

        [Fact]
        public void Inspect_LockFreeOwnerRecordUnreadable_ThrowsWithTheFix()
        {
            File.WriteAllText(OwnerPath, "{ not json");

            var ex = Assert.Throws<UsageException>(() => CaptureLock.Inspect(_dir));

            Assert.Contains("delete " + CaptureLock.OwnerFileName, ex.Message);
        }

        // ---- the claim lives and dies with the in-process capture claim ----------

        [Fact]
        public void Attach_ReleaseOfThatClaim_DisposesTheCaptureLock()
        {
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "capture session", out var ticket));
            var disk = CaptureLock.TryAcquire(_dir, "capture session", out _)!;
            RecordingWorkset.Attach(ticket, disk);
            Assert.True(IsLockedOnDisk());

            RecordingWorkset.Release(ticket);

            Assert.False(IsLockedOnDisk(), "releasing the capture claim left capture.lock held - the recording would be declined forever");
            Assert.False(CaptureLock.Inspect(_dir).Live);
        }

        [Fact]
        public void Attach_ToAClaimThatIsNoLongerHeld_Throws()
        {
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "capture session", out var ticket));
            RecordingWorkset.Release(ticket);
            using var disk = CaptureLock.TryAcquire(_dir, "capture session", out _)!;

            Assert.Throws<InvalidOperationException>(() => RecordingWorkset.Attach(ticket, disk));
        }

        [Fact]
        public void Release_OfAStaleTicket_LeavesTheCurrentOwnersLockAlone()
        {
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "first", out var first));
            RecordingWorkset.Release(first);
            Assert.True(RecordingWorkset.TryClaim(_dir, RecordingWorkKind.Capture, "second", out var second));
            RecordingWorkset.Attach(second, CaptureLock.TryAcquire(_dir, "second", out _)!);

            RecordingWorkset.Release(first);

            Assert.True(IsLockedOnDisk(), "a stale ticket released the current capture's disk lock");
            RecordingWorkset.Release(second);
        }

        // ---- criteria 2, 3 and 4 end to end on the finalizing paths --------------

        /// <summary>Criteria 2 and 3: three foreign passes over a live recording consume nothing,
        /// and the owning recording then completes on its first attempt.</summary>
        [Fact]
        public void Resume_LiveCaptureInAnotherProcess_ThreeTimes_ConsumesNoAttemptAndTheRecordingStillCompletes()
        {
            WriteStartManifest();
            var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 8304)!;

            for (int i = 0; i < 3; i++)
            {
                var outcome = PostRecording.Resume(_dir, hostedWorkAllowed: false);
                Assert.True(outcome.Skipped, $"foreign pass {i + 1} did not decline a live capture");
            }

            Assert.Empty(_muxCalls);
            Assert.Null(MuxRecord());
            Assert.NotNull(Manifest.Load(_dir).PendingMux);
            Assert.True(File.Exists(Path.Combine(_dir, "raw.mp4")), "a declined pass moved the live raw file");

            // The capture ends: its lock is released and its record removed, as CaptureLock.Dispose does.
            File.Delete(OwnerPath);
            other.Dispose();

            var done = PostRecording.Resume(_dir, hostedWorkAllowed: false);

            Assert.False(done.Skipped);
            Assert.Single(_muxCalls);
            var record = MuxRecord()!;
            Assert.Equal(1, record.Attempts);
            Assert.Equal(PostStageState.Done, record.State);
        }

        /// <summary>Criterion 4 and the positive control: a recording whose capture owner died, and
        /// whose writers are gone, is repaired exactly as before - the stale record does not block it.</summary>
        [Fact]
        public void Resume_CaptureOwnerDiedAndWritersExited_RepairsTheRecording()
        {
            WriteStartManifest();
            CaptureLock.IsSameProcess = (_, _) => false;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91092, host: Environment.MachineName, writers: new[] { Writer(91038) });
            File.WriteAllText(LockPath, "");   // the lock file a dead capture leaves, unheld

            var outcome = PostRecording.Resume(_dir, hostedWorkAllowed: false);

            Assert.False(outcome.Skipped, "a dead owner's recording was declined - the guard strands what #128 exists to recover");
            Assert.Single(_muxCalls);
            Assert.Null(Manifest.Load(_dir).PendingMux);
            Assert.Equal(1, MuxRecord()!.Attempts);
        }

        [Fact]
        public void Resume_OwnerDeadButWriterStillRunning_DeclinesUntilTheWriterEnds()
        {
            WriteStartManifest();
            bool writerAlive = true;
            CaptureLock.IsSameProcess = (pid, _) => pid == 91047 && writerAlive;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91100, host: Environment.MachineName, writers: new[] { Writer(91047) });

            Assert.True(PostRecording.Resume(_dir, hostedWorkAllowed: false).Skipped);
            Assert.Empty(_muxCalls);
            Assert.Null(MuxRecord());

            writerAlive = false;
            Assert.False(PostRecording.Resume(_dir, hostedWorkAllowed: false).Skipped);
            Assert.Single(_muxCalls);
        }

        [Fact]
        public void RunForPackage_LiveCaptureInAnotherProcess_RefusesNamingTheHolderWithoutCounting()
        {
            WriteStartManifest();
            using var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 8304);

            var ex = Assert.Throws<UsageException>(() => MuxCoordinator.RunForPackage(_dir));

            Assert.Contains("refusing to package", ex.Message);
            Assert.Contains("pid 8304", ex.Message);
            Assert.Null(MuxRecord());
            Assert.NotNull(Manifest.Load(_dir).PendingMux);
        }

        [Fact]
        public void FinalizePending_LiveCaptureInAnotherProcess_RefusesBeforeTouchingAFile()
        {
            WriteStartManifest();
            using var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 8304);

            var ex = Assert.Throws<UsageException>(() => RecordingService.FinalizePending(_dir));

            Assert.Contains("refusing to finalize", ex.Message);
            Assert.True(File.Exists(Path.Combine(_dir, "raw.mp4")));
            Assert.False(File.Exists(Path.Combine(_dir, "recording.mp4")));
            Assert.False(File.Exists(Path.Combine(_dir, "recording.original.mp4")));
        }

        /// <summary>Regression: a start that failed before any writer ran leaves only its manifest and
        /// its released capture.lock - that is not a recording, and Discard must still remove it.</summary>
        [Fact]
        public void HasRawArtifacts_OnlyTheManifestAndTheCaptureClaim_IsFalse()
        {
            ManifestStore.Replace(_dir, new Manifest { Mode = "video", Label = "video" });
            using (CaptureLock.TryAcquire(_dir, "capture session", out _)) { }
            File.WriteAllText(OwnerPath + ".1234.tmp", "");
            Assert.True(File.Exists(LockPath), "broken fixture: the released lock file should stay in place");

            Assert.False(RecordingStopSequence.HasRawArtifacts(_dir), "capture.lock was counted as capture bytes");

            File.WriteAllText(Path.Combine(_dir, "raw.mp4"), "x");
            Assert.True(RecordingStopSequence.HasRawArtifacts(_dir), "control: a real raw file must still count");
        }

        // ---- review round: the races and the stranding paths ---------------------

        /// <summary>Two finalizers probing the same finished recording at the same instant (the
        /// service's repair pass and an `agenteyes package`) must not read each other as a holder.</summary>
        [Fact]
        public void Inspect_AnotherProbeOpenAtTheSameInstant_IsNotLive()
        {
            CaptureLock.IsSameProcess = (_, _) => false;
            HoldLikeAnotherProcess(holdLock: false, host: Environment.MachineName, writers: new[] { Writer(91038) });
            File.WriteAllText(LockPath, "");
            using var otherProbe = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var r = CaptureLock.Inspect(_dir);

            Assert.False(r.Live, $"a concurrent probe was read as a live capture: {r.Reason}");
        }

        [Fact]
        public void TryAcquire_OwnerRecordUnreadable_ThrowsAndDoesNotLeakTheLock()
        {
            File.WriteAllText(OwnerPath, "{ not json");

            Assert.Throws<UsageException>(() => CaptureLock.TryAcquire(_dir, "capture session", out _));

            Assert.False(IsLockedOnDisk(), "a start that threw left capture.lock held by a leaked handle");
        }

        [Fact]
        public void Dispose_AWriterIsStillRunning_KeepsTheClaimLiveForOtherProcesses()
        {
            var held = CaptureLock.TryAcquire(_dir, "capture session", out _)!;
            held.NoteWriter("video", Environment.ProcessId);   // this test process: certainly still running

            held.Dispose();

            Assert.False(IsLockedOnDisk(), "the lock itself is still released");
            Assert.True(File.Exists(OwnerPath), "the owner record of a still-running writer was deleted");
            var r = CaptureLock.Inspect(_dir);
            Assert.True(r.Live, "a file still being written was published to every other process");
            Assert.Contains("STILL RUNNING", r.Reason);
        }

        [Fact]
        public void Inspect_HostRenamedButSameHostId_IsCheckedLocally()
        {
            // macOS renames the host when the network changes; the record from before the rename is
            // still this machine's, and its exited writer must not strand the recording.
            CaptureLock.HostName = () => "NEWNAME";
            CaptureLock.IsSameProcess = (_, _) => false;
            var owner = new CaptureLock.OwnerRecord
            {
                Host = "OLDNAME", HostId = CaptureLock.ThisHostId(), Pid = 1, What = "capture session",
                Writers = new List<CaptureLock.WriterRecord> { Writer(91038) },
            };
            File.WriteAllText(OwnerPath, JsonSerializer.Serialize(owner));

            var r = CaptureLock.Inspect(_dir);

            Assert.False(r.Live, $"this machine's own record was read as another host's after a rename: {r.Reason}");
        }

        [Fact]
        public void NoteWriter_WriterAlreadyExited_ThrowsSayingSo()
        {
            using var held = CaptureLock.TryAcquire(_dir, "capture session", out _)!;
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c exit 0")
                : new ProcessStartInfo("/bin/sh", "-c true");
            psi.UseShellExecute = false;
            int pid;
            using (var p = Process.Start(psi)!) { pid = p.Id; p.WaitForExit(); }

            var ex = Assert.Throws<UsageException>(() => held.NoteWriter("video", pid));

            Assert.Contains("exited immediately", ex.Message);
        }

        [Fact]
        public void FindUnfinished_RecordingCapturedByAnotherProcess_IsSkippedUntilTheCaptureEnds()
        {
            WriteStartManifest();
            string root = Path.GetDirectoryName(_dir)!;
            var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 8304)!;

            Assert.DoesNotContain(_dir, PostRecordingPlan.FindUnfinished(root));

            File.Delete(OwnerPath);
            other.Dispose();
            Assert.Contains(_dir, PostRecordingPlan.FindUnfinished(root));
        }

        // ---- pass 2, D2: a recovered crash must not keep its claim for ever ---------

        /// <summary>Become ANOTHER MACHINE on the same library: a different name and host id.</summary>
        private static void BecomeAnotherHost()
        {
            CaptureLock.HostName = () => "OTHERBOX";
            CaptureLock.ThisHostId = () => "00000000000000000000000000000b0b";
        }

        /// <summary>
        /// THE CONTROL ARM QA asked for: a capture on this machine crashed (owner dead, writer
        /// exited), this machine's repair pass recovers it - and then ANOTHER HOST accepts the
        /// finished recording. Before the fix the recovery kept capture.owner.json and the other host
        /// refused it for ever ("cannot be checked from OTHERBOX"); the precondition shows that view.
        /// </summary>
        [Fact]
        public void Resume_CrashedCaptureRecovered_AnotherHostThenAcceptsTheRecording()
        {
            WriteStartManifest();
            CaptureLock.IsSameProcess = (_, _) => false;   // owner and writer both dead
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91092, host: Environment.MachineName, writers: new[] { Writer(91038) });
            File.WriteAllText(LockPath, "");

            string realHost = Environment.MachineName;
            BecomeAnotherHost();
            var before = CaptureLock.Inspect(_dir);
            Assert.True(before.Live, "precondition: another host must refuse a claim it cannot probe - the fix would be untested");
            Assert.Contains("cannot be checked from OTHERBOX", before.Reason);
            CaptureLock.HostName = _realHost;
            CaptureLock.ThisHostId = _realHostId;
            Assert.Equal(realHost, CaptureLock.HostName());

            var recovered = PostRecording.Resume(_dir, hostedWorkAllowed: false);

            Assert.False(recovered.Skipped, "this host did not recover its own crashed capture");
            Assert.Single(_muxCalls);
            Assert.False(File.Exists(OwnerPath), "the recovery kept capture.owner.json - every other host refuses the recording for ever");

            BecomeAnotherHost();
            var after = CaptureLock.Inspect(_dir);
            Assert.False(after.Live, $"another host still refuses the recovered recording: {after.Reason}");
            var otherPass = PostRecording.Resume(_dir, hostedWorkAllowed: false);
            Assert.False(otherPass.Skipped, "another host's pass declined a finished recording");
            Assert.Equal(1, MuxRecord()!.Attempts);   // the other host's pass found nothing left to mux
        }

        [Fact]
        public void ClearStaleClaim_LockHeldByAnotherProcess_KeepsTheRecord()
        {
            using var other = HoldLikeAnotherProcess(holdLock: true, ownerPid: 8304, host: Environment.MachineName);

            Assert.False(CaptureLock.ClearStaleClaim(_dir, "test"));

            Assert.True(File.Exists(OwnerPath), "a live capture's owner record was removed");
        }

        [Fact]
        public void ClearStaleClaim_OwnerDeadButWriterStillRunning_KeepsTheRecord()
        {
            CaptureLock.IsSameProcess = (pid, _) => pid == 91047;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91100, host: Environment.MachineName, writers: new[] { Writer(91047) });

            Assert.False(CaptureLock.ClearStaleClaim(_dir, "test"));

            Assert.True(File.Exists(OwnerPath), "the claim of a writer still writing was removed");
            Assert.True(CaptureLock.Inspect(_dir).Live);
        }

        [Fact]
        public void ClearStaleClaim_WriterRecordedByAnotherHost_KeepsTheRecord()
        {
            // This host cannot probe another machine's pid, so it cannot PROVE the claim stale.
            CaptureLock.IsSameProcess = (_, _) => false;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91100, host: "OTHERBOX", writers: new[] { Writer(91047) });

            Assert.False(CaptureLock.ClearStaleClaim(_dir, "test"));

            Assert.True(File.Exists(OwnerPath));
        }

        [Fact]
        public void ClearStaleClaim_ThisProcessIsCapturing_KeepsTheRecord()
        {
            using var held = CaptureLock.TryAcquire(_dir, "capture session", out _)!;

            Assert.False(CaptureLock.ClearStaleClaim(_dir, "test"));

            Assert.True(File.Exists(OwnerPath));
            Assert.True(CaptureLock.Inspect(_dir).Live);
        }

        [Fact]
        public void ClearStaleClaim_ProvablyStale_RemovesTheRecordAndReleasesTheLock()
        {
            CaptureLock.IsSameProcess = (_, _) => false;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91092, host: Environment.MachineName, writers: new[] { Writer(91038) });

            Assert.True(CaptureLock.ClearStaleClaim(_dir, "test"));

            Assert.False(File.Exists(OwnerPath));
            Assert.False(IsLockedOnDisk(), "ClearStaleClaim left capture.lock held");
            Assert.Equal("no capture holds it", CaptureLock.Inspect(_dir).Reason);
        }

        /// <summary>Self-review regression (pass 2): the clear holds the lock SHARED, the same way every
        /// finalizer probes it, so a probe and a clear at the same instant never see each other as a
        /// holder. An exclusive clear would be refused here - and, the other way round, would read to a
        /// concurrent FinalizePending (which checks after its attempt is counted) as a live capture.</summary>
        [Fact]
        public void ClearStaleClaim_AnotherFinalizerProbingAtTheSameInstant_StillClearsAndNeitherSeesAHolder()
        {
            CaptureLock.IsSameProcess = (_, _) => false;
            HoldLikeAnotherProcess(holdLock: false, ownerPid: 91092, host: Environment.MachineName, writers: new[] { Writer(91038) });
            File.WriteAllText(LockPath, "");   // the unheld lock a crashed capture leaves
            using var otherProbe = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Assert.True(CaptureLock.ClearStaleClaim(_dir, "test"), "a concurrent finalizer's probe was read as a holder");

            Assert.False(File.Exists(OwnerPath));
            Assert.False(CaptureLock.Inspect(_dir).Live);
        }

        [Fact]
        public void ClearStaleClaim_NoOwnerRecord_IsANoOp()
        {
            Assert.False(CaptureLock.ClearStaleClaim(_dir, "test"));
            Assert.False(File.Exists(LockPath), "a no-op created capture.lock");
        }

        // ---- pass 2, G1: the manifest names who captured it ------------------------

        [Fact]
        public void OwnerForManifest_MatchesThePublishedOwnerRecord()
        {
            using var held = CaptureLock.TryAcquire(_dir, "capture session", out _)!;
            var published = JsonSerializer.Deserialize<CaptureLock.OwnerRecord>(File.ReadAllText(OwnerPath))!;

            var info = held.OwnerForManifest();

            Assert.Equal(Environment.ProcessId, info.Pid);
            Assert.Equal(published.Host, info.Host);
            Assert.Equal(published.Pid, info.Pid);
            Assert.Equal(published.ProcessStartUtc, info.ProcessStartUtc);
            using var me = Process.GetCurrentProcess();
            Assert.True((me.StartTime.ToUniversalTime() - info.ProcessStartUtc).Duration() < TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void CaptureOwner_SurvivesTheManifestRoundTripAndTheRecoveryRecord()
        {
            var owner = new Manifest.CaptureOwnerInfo
            {
                Host = "devlinux", Pid = 4242, ProcessStartUtc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc),
            };
            ManifestStore.Replace(_dir, new Manifest { Mode = "video", Label = "video", CaptureOwner = owner });

            var loaded = Manifest.Load(_dir).CaptureOwner!;
            var reduced = RecoveryManifest.From(Manifest.Load(_dir), 1.0, _dir).CaptureOwner!;

            Assert.Equal(("devlinux", 4242, owner.ProcessStartUtc), (loaded.Host, loaded.Pid, loaded.ProcessStartUtc.ToUniversalTime()));
            Assert.Equal(("devlinux", 4242), (reduced.Host, reduced.Pid));
            Assert.Contains("\"CaptureOwner\"", File.ReadAllText(Path.Combine(_dir, "manifest.json")));
        }

        // ---- criterion 5 ---------------------------------------------------------

        [Fact]
        public void NoteStarted_RecordsWhoConsumedEachAttempt()
        {
            WriteStartManifest();

            PostRecordingState.NoteStarted(_dir, PostStage.Mux, new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc));
            PostRecordingState.NoteStarted(_dir, PostStage.Mux, new DateTime(2026, 11, 16, 12, 1, 0, DateTimeKind.Utc));

            var by = MuxRecord()!.AttemptedBy!;
            Assert.Equal(2, by.Count);
            Assert.StartsWith("attempt 1 at 2026-11-16T12:00:00", by[0]);
            Assert.StartsWith("attempt 2 at 2026-11-16T12:01:00", by[1]);
            Assert.Contains($"pid {Environment.ProcessId} on {Environment.MachineName}", by[1]);
        }
    }
}
