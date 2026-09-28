using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AgentEyes.DevThrottle;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Background work (titles, translation, transcription) is INCLUDED with the subscription and
    /// never paid from credits (owner, 2026-09-27). These pin the three things that made an empty
    /// wallet raise the "credits are empty" toast every fifteen minutes.
    /// </summary>
    [Collection(PostRecordingCollection.Name)]
    public sealed class HostedAiRefusalTests : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _dirs = new();

        public HostedAiRefusalTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AgentEyes-refusal-" + Guid.NewGuid().ToString("N"));
            for (int i = 0; i < 3; i++)
            {
                string dir = Path.Combine(_root, "2026-09-27_18000" + i + "_video");
                Directory.CreateDirectory(dir);
                ManifestStore.Replace(dir, new Manifest
                {
                    Mode = "video",
                    Label = "video",
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                    VideoFile = "recording.mp4",
                    TranscribeAttempts = 0,
                });
                _dirs.Add(dir);
            }
        }

        public void Dispose()
        {
            RepairService.RestoreDefaultSteps();
            foreach (string dir in _dirs) RecordingWorkset.ReleaseForTests(dir);
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
        [Fact]
        public void Titles_and_translation_ask_for_the_included_model_not_a_catalog_one()
        {
            Assert.Equal("devthrottle/agenteyes", DevThrottleClient.ChatModel);
        }

        [Fact]
        public void A_402_keeps_the_proxys_own_words_and_code()
        {
            const string body =
                "{\"error\":{\"message\":\"This AI feature is included with a DevThrottle Pro subscription. " +
                "Your account has no active subscription or trial - see devthrottle.com/pricing.\"," +
                "\"type\":\"insufficient_quota\",\"code\":\"subscription_required\"}}";

            var ex = DevThrottleClient.ErrorFrom(402, body);

            Assert.Equal(402, ex.Status);
            Assert.Equal("subscription_required", ex.Code);
            Assert.StartsWith("This AI feature is included", ex.Message);
            Assert.DoesNotContain("credits", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_402_found_anywhere_in_the_chain_stops_the_pass()
        {
            var refused = new DevThrottleException("fair use", 402, "fair_use_limit_reached");
            var wrapped = new InvalidOperationException("packaging failed", refused);

            Assert.Same(refused, DevThrottleClient.RefusalIn(wrapped));
            Assert.True(DevThrottleClient.IsHostedAiRefused(wrapped));
        }

        [Fact]
        public void Any_other_failure_is_not_a_refusal()
        {
            Assert.Null(DevThrottleClient.RefusalIn(new DevThrottleException("busy", 503)));
            Assert.False(DevThrottleClient.IsHostedAiRefused(new InvalidOperationException("disk full")));
        }

        [Fact]
        public void A_refused_packaging_run_gives_its_attempt_back()
        {
            string dir = _dirs[0];
            var refused = new DevThrottleException("no subscription", 402, "subscription_required");

            var thrown = Assert.Throws<DevThrottleException>(() =>
                PostRecording.RunCountingAttempt(dir, _ => throw refused));

            Assert.Same(refused, thrown);                              // still reported to the caller
            Assert.Equal(0, TranscriptionBacklog.AttemptsSoFar(dir));  // but not held against the recording
        }

        [Fact]
        public void Any_other_packaging_failure_keeps_its_attempt()
        {
            // The control: without it, "0 attempts" above could be a counter that never counted.
            string dir = _dirs[0];

            Assert.Throws<InvalidOperationException>(() =>
                PostRecording.RunCountingAttempt(dir, _ => throw new InvalidOperationException("ffmpeg died")));

            Assert.Equal(1, TranscriptionBacklog.AttemptsSoFar(dir));
        }

        [Fact]
        public void Returning_an_attempt_never_goes_below_zero()
        {
            TranscriptionBacklog.ReturnAttempt(_dirs[0]);

            Assert.Equal(0, TranscriptionBacklog.AttemptsSoFar(_dirs[0]));
        }

        [Fact]
        public async Task A_refused_title_stops_the_pass_and_hands_the_refusal_to_the_window()
        {
            using var service = new RepairService(() => false);
            var refused = new DevThrottleException("fair use", 402, "fair_use_limit_reached");
            DevThrottleException? shown = null;
            service.HostedAiRefused = r => shown = r;
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; throw refused; };

            await service.TitleAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(1, calls);        // every other recording would be refused the same way
            Assert.Same(refused, shown);
        }

        [Fact]
        public async Task Any_other_title_failure_moves_on_to_the_next_recording()
        {
            // The control for the stop above.
            using var service = new RepairService(() => false);
            DevThrottleException? shown = null;
            service.HostedAiRefused = r => shown = r;
            int calls = 0;
            RepairService.TitleStep = _ => { calls++; throw new DevThrottleException("busy", 503); };

            await service.TitleAsync(_dirs, CaptureSignal.Epoch);

            Assert.Equal(3, calls);
            Assert.Null(shown);
        }
    }
}
