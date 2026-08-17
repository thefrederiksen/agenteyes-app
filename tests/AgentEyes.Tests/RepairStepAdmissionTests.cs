using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #154, criterion 4, ROUND 3: the window between a repair step's guard decision and the
    /// costly work itself.
    ///
    /// THE DEFECT THESE EXIST FOR, and why the round-2 tests could not see it. Round 2 read the
    /// capture signals, then took a stage claim, then invoked the hosted call or ffmpeg - three
    /// separate statements. An independent reviewer paused a title pass after its stage claim, added
    /// a capture claim, and let the step continue: the hosted call went out DURING the capture. The
    /// check-then-act had moved, not gone, and every one of the 673 tests on that branch was green.
    ///
    /// The fix is not another sample closer to the work - that is the same shape again. Admission
    /// (<see cref="RecordingWorkset.TryAdmitStep"/>) and the step's BEGIN
    /// (<see cref="RecordingWorkset.TryRunStep{T}"/>) are transitions taken under the same monitor a
    /// capture publishes its claim under, so the two events have one order: a capture that claims
    /// before the begin transition stops the step dead, and a step that has begun keeps running while
    /// the capture starts anyway (capture never waits for repair - the disclosed limit, stated on
    /// <see cref="RecordingWorkset"/>).
    ///
    /// HOW THESE TESTS REACH THAT WINDOW DETERMINISTICALLY. <c>RecordingWorkset.BeforeStepBegins</c>
    /// is invoked at exactly the instant between admission and begin, so the capture claim is
    /// inserted THERE rather than by racing threads and hoping. Each negative case asserts both that
    /// the hook fired (so the window was really entered) and that the step did not run; each has a
    /// control that runs the same loop with the same hook doing nothing and asserts the steps DO run,
    /// so a count of zero can never come from a loop that did nothing.
    ///
    /// Delete the capture test inside <c>TryRunStep</c> - or go back to calling the step directly -
    /// and every negative case here fails with the count at 1.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class RepairStepAdmissionTests : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _dirs = new();

        /// <summary>The recording a capture session writes into - a DIFFERENT directory from the ones
        /// the repair loops walk, because the guard is about the machine, not one recording.</summary>
        private readonly string _capturing;

        private int _hookFired;

        public RepairStepAdmissionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-admit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            for (int i = 0; i < 2; i++)
            {
                string dir = Path.Combine(_root, "2026-08-12_12000" + i + "_audio");
                Directory.CreateDirectory(dir);
                _dirs.Add(dir);
            }
            _capturing = Path.Combine(_root, "2026-08-12_120099_video");
            Directory.CreateDirectory(_capturing);
        }

        public void Dispose()
        {
            RecordingWorkset.BeforeStepBegins = null;
            RepairService.RestoreDefaultSteps();
            // A leaked capture claim is process-wide: it would make every later test yield.
            RecordingWorkset.ReleaseForTests(_capturing);
            foreach (string dir in _dirs) RecordingWorkset.ReleaseForTests(dir);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        private static RepairService NotRecording() => new(() => false);

        /// <summary>A real capture claim, taken ONCE, in the window between a step's admission and
        /// its start. This is the interleaving the reviewer reproduced.</summary>
        private void ACaptureClaimsTheMachineNow()
        {
            if (_hookFired++ > 0) return;
            Assert.True(RecordingWorkset.TryClaim(_capturing, RecordingWorkKind.Capture, "capture session", out _));
        }

        /// <summary>The same seam with nothing in it - what the controls use, so "the step ran" and
        /// "the window was entered" are proven by the same code path.</summary>
        private void NothingHappensInTheWindow() => _hookFired++;

        // ---- the hosted title call --------------------------------------------

        [Fact]
        public async Task TitleAsync_ACaptureClaimsBetweenAdmissionAndTheCall_MakesNoHostedCall()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; return Task.FromResult(true); };
            RecordingWorkset.BeforeStepBegins = ACaptureClaimsTheMachineNow;

            await service.TitleAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(1, _hookFired);   // the window WAS entered - the case is not vacuous
            Assert.Equal(0, calls);
            Assert.All(_dirs, d => Assert.False(RecordingWorkset.IsClaimed(d),
                "a step that did not run must still give the recording back"));
        }

        [Fact]
        public async Task TitleAsync_NothingClaimsInThatWindow_NamesEveryRecording()
        {
            // The control: same loop, same seam, no capture.
            using var service = NotRecording();
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; return Task.FromResult(true); };
            RecordingWorkset.BeforeStepBegins = NothingHappensInTheWindow;

            await service.TitleAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, _hookFired);
            Assert.Equal(2, calls);
        }

        // ---- the thumbnail ffmpeg run -----------------------------------------

        [Fact]
        public async Task ThumbsAsync_ACaptureClaimsBetweenAdmissionAndFfmpeg_RunsNoFfmpeg()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };
            RecordingWorkset.BeforeStepBegins = ACaptureClaimsTheMachineNow;

            await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(1, _hookFired);
            Assert.Equal(0, calls);
            Assert.All(_dirs, d => Assert.False(RecordingWorkset.IsClaimed(d)));
        }

        [Fact]
        public async Task ThumbsAsync_NothingClaimsInThatWindow_GeneratesEveryThumbnail()
        {
            using var service = NotRecording();
            int calls = 0;
            RepairService.ThumbStep = _ => { calls++; return true; };
            RecordingWorkset.BeforeStepBegins = NothingHappensInTheWindow;

            await service.ThumbsAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, _hookFired);
            Assert.Equal(2, calls);
        }

        // ---- the resume pass (the deferred mux and the transcription upload) ---

        [Fact]
        public async Task ResumeAsync_ACaptureClaimsBetweenAdmissionAndTheResume_ResumesNothing()
        {
            using var service = NotRecording();
            var resumed = new List<string>();
            RepairService.ResumeStep = (dir, _) => { resumed.Add(dir); return new PostRecordingOutcome(dir); };
            RecordingWorkset.BeforeStepBegins = ACaptureClaimsTheMachineNow;

            await service.ResumeAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(1, _hookFired);
            Assert.Empty(resumed);
        }

        [Fact]
        public async Task ResumeAsync_NothingClaimsInThatWindow_ResumesEveryRecording()
        {
            using var service = NotRecording();
            var resumed = new List<string>();
            RepairService.ResumeStep = (dir, _) => { resumed.Add(dir); return new PostRecordingOutcome(dir); };
            RecordingWorkset.BeforeStepBegins = NothingHappensInTheWindow;

            await service.ResumeAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(2, _hookFired);
            Assert.Equal(_dirs, resumed);
        }

        // ---- the admission itself ---------------------------------------------

        [Fact]
        public void TryAdmitStep_ACaptureIsInProgress_IsRefusedAndClaimsNothing()
        {
            Assert.True(RecordingWorkset.TryClaim(_capturing, RecordingWorkKind.Capture, "capture session", out _));

            var admission = RecordingWorkset.TryAdmitStep(_dirs[0], "title repair", out var step);

            Assert.Equal(RepairStepAdmission.CaptureYielded, admission);
            Assert.False(step.Admitted);
            Assert.False(RecordingWorkset.IsClaimed(_dirs[0]),
                "a refused admission must not leave a stage claim behind");
        }

        [Fact]
        public void TryAdmitStep_SomebodyElseHasTheRecording_IsBusy_AndTheRestOfThePassGoesOn()
        {
            Assert.True(RecordingWorkset.TryClaim(_dirs[0], RecordingWorkKind.FullPipeline, "post-recording", out _));
            try
            {
                Assert.Equal(RepairStepAdmission.DirectoryBusy,
                    RecordingWorkset.TryAdmitStep(_dirs[0], "title repair", out var busy));
                Assert.False(busy.Admitted);

                // ...and a DIFFERENT recording is still admitted: a busy directory is one recording's
                // problem, not the pass's.
                Assert.Equal(RepairStepAdmission.Admitted,
                    RecordingWorkset.TryAdmitStep(_dirs[1], "title repair", out var step));
                RecordingWorkset.EndStep(step);
            }
            finally { RecordingWorkset.ReleaseForTests(_dirs[0]); }
        }

        [Fact]
        public void EndStep_GivesTheRecordingBack_AndStopsCountingTheStepAsRunning()
        {
            Assert.Equal(RepairStepAdmission.Admitted,
                RecordingWorkset.TryAdmitStep(_dirs[0], "thumbnail repair", out var step));
            Assert.True(RecordingWorkset.IsClaimed(_dirs[0]));

            Assert.True(RecordingWorkset.TryRunStep(step, () => 42, out int answer));
            Assert.Equal(42, answer);
            Assert.Equal(1, RecordingWorkset.RunningSteps);

            RecordingWorkset.EndStep(step);

            Assert.False(RecordingWorkset.IsClaimed(_dirs[0]));
            Assert.Equal(0, RecordingWorkset.RunningSteps);
        }

        [Fact]
        public void TryRunStep_ACaptureArrivedAfterAdmission_RunsNothing_AndIsNotCountedAsRunning()
        {
            Assert.Equal(RepairStepAdmission.Admitted,
                RecordingWorkset.TryAdmitStep(_dirs[0], "thumbnail repair", out var step));
            try
            {
                Assert.True(RecordingWorkset.TryClaim(_capturing, RecordingWorkKind.Capture, "capture session", out _));

                bool ran = RecordingWorkset.TryRunStep(step, () => 42, out int answer);

                Assert.False(ran);
                Assert.Equal(0, answer);
                Assert.Equal(0, RecordingWorkset.RunningSteps);
            }
            finally { RecordingWorkset.EndStep(step); }
        }

        [Fact]
        public void TryRunStep_AStepThatWasNeverAdmitted_Throws()
        {
            // Fail closed: a default ticket must not be usable as permission to run.
            Assert.Throws<InvalidOperationException>(
                () => RecordingWorkset.TryRunStep(default, () => 1, out int _));
        }

        // ---- the shape of the fix, read from the compiled code ------------------

        [Fact]
        public void TheStepStartsImmediatelyAfterTheCaptureTransition_InTheCompiledCode()
        {
            // The whole rejection in round 2 was that a re-read followed by a few statements is still
            // check-then-act. What makes TryRunStep different is that the transition is the LAST
            // thing before the step: anything inserted between them - a log line, a manifest read,
            // another sample - would appear after TryBegin in this list and reopen the window.
            //
            // (The delegate invocation itself is a generic instantiation, so it carries a TypeSpec
            // rather than a method token and no static scan can name it - which is exactly why the
            // assertion is "nothing is called after the transition" and why the behavioral cases
            // above are what prove the step actually runs.)
            var calls = CompiledCode.CallsIn(CompiledCode.CoreAssembly, "RecordingWorkset::TryRunStep").ToList();

            Assert.Contains("AgentEyes.RecordingWorkset::TryBegin", calls);
            Assert.Equal("AgentEyes.RecordingWorkset::TryBegin", calls[^1]);
        }

        [Fact]
        public void TheCaptureTransitionIsTakenUnderTheSameMonitorAsAClaim_InTheCompiledCode()
        {
            // The transition has to be a LOCKED state change, not a read: a read cannot be ordered
            // against a capture claim, which is what round 2 shipped.
            var calls = CompiledCode.CallsIn(CompiledCode.CoreAssembly, "RecordingWorkset::TryBegin").ToList();

            Assert.Contains("System.Threading.Monitor::Enter", calls);
            Assert.Contains("System.Threading.Monitor::Exit", calls);
        }

        [Fact]
        public void NoProductionCodeReleasesAClaimItDoesNotOwn_InTheCompiledCode()
        {
            // ReleaseForTests removes whichever claim is on a directory - the exact power that let a
            // failed capture start tear down another owner's claim. Test fixtures need it to clean
            // up; production must never call it, and this is what keeps that true.
            var sites = CompiledCode.ProductAssemblies()
                .SelectMany(a => CompiledCode.CallSites(a, c => c == "AgentEyes.RecordingWorkset::ReleaseForTests"))
                .ToList();

            Assert.True(sites.Count == 0,
                "production must release claims through the ticket it was given: " + CompiledCode.Describe(sites));
        }

        [Fact]
        public void TheTestSeamIsUsedByTestsOnly_InTheProductSource()
        {
            // BeforeStepBegins is a field, so no call-site scan can see an assignment to it. Read the
            // product source instead: it may be DECLARED in RecordingWorkset.cs and mentioned
            // nowhere else in src/.
            var offenders = Directory
                .EnumerateFiles(Path.Combine(RepoSource.Root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "RecordingWorkset.cs", StringComparison.OrdinalIgnoreCase))
                .Where(f => File.ReadAllText(f).Contains("BeforeStepBegins", StringComparison.Ordinal))
                .ToList();

            Assert.True(offenders.Count == 0,
                "the admission test seam must not be used by product code: " + string.Join(", ", offenders));
        }
    }
}
