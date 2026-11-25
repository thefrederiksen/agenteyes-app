using System;
using System.Collections.Generic;
using System.IO;
using AgentEyes.App;
using AgentEyes.Takes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>Issue #230 round 2: the take list's defaults and how a take attempt is named.</summary>
    public class TakeStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "agenteyes-takes-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private TakeStore NewStore() => new(Path.Combine(_dir, "takes.json"));

        private static TakeList List(string? readingMode, params Take[] takes) =>
            new() { ReadingMode = readingMode, Takes = new List<Take>(takes) };

        [Fact]
        public void Load_NoReadingModeGiven_DefaultsToTeleprompter()
        {
            var store = NewStore();

            var loaded = store.Load(List(null, new Take { Id = "opening", Text = "Hello." }));

            Assert.Equal(TakeStore.Teleprompter, loaded.ReadingMode);
        }

        [Fact]
        public void Load_ReadFirstGiven_KeepsReadFirst()
        {
            var store = NewStore();

            var loaded = store.Load(List(TakeStore.ReadFirst, new Take { Id = "opening", Text = "Hello." }));

            Assert.Equal(TakeStore.ReadFirst, loaded.ReadingMode);
        }

        [Fact]
        public void Load_TeleprompterOverReadFirst_KeepsAttempts()
        {
            var store = NewStore();
            store.Load(List(TakeStore.ReadFirst, new Take { Id = "opening", Text = "Hello." }));
            store.AddAttempt("opening", "recording.mp4", "dir", 11.1);

            var loaded = store.Load(List(TakeStore.Teleprompter, new Take { Id = "opening", Text = "Hello." }));

            Assert.Equal(TakeStore.Teleprompter, loaded.ReadingMode);
            Assert.Single(loaded.Takes[0].Attempts);
        }

        [Fact]
        public void TakeNumber_SecondTake_Is2()
        {
            var store = NewStore();
            store.Load(List(null, new Take { Id = "a", Text = "One." }, new Take { Id = "b", Text = "Two." }));

            Assert.Equal(2, store.TakeNumber("b"));
        }

        [Fact]
        public void TakeNameFor_TitledTake_NamesTakeTitleAndAttempt()
        {
            var name = TakeName.For(1, new Take { Id = "opening", Title = " The opening ", Text = "x" }, 2);

            Assert.Equal("opening", name.TakeId);
            Assert.Equal("Take 1 - The opening - attempt 2", name.DisplayName);
        }

        [Fact]
        public void TakeNameFor_UntitledTake_NamesTakeAndAttempt()
        {
            var name = TakeName.For(3, new Take { Id = "close", Text = "x" }, 1);

            Assert.Equal("Take 3 - attempt 1", name.DisplayName);
        }

        [Fact]
        public void ReadyInstruction_Teleprompter_SaysSpaceStartsAndStops()
        {
            Assert.Equal("Press Space to start recording. Space again to stop.", TakeRecordingWindow.ReadyInstruction(true));
        }

        [Fact]
        public void ReadyInstruction_ReadFirst_SaysTheTextDisappears()
        {
            string text = TakeRecordingWindow.ReadyInstruction(false);

            Assert.StartsWith("Read the line. When you press Space a 3-2-1 count runs, then the text disappears and you say it to the camera.", text);
            Assert.EndsWith("Press Space to start recording. Space again to stop.", text);
        }

        [Fact]
        public void HelpText_DescribesWhatTheRecordingScreenDoes()
        {
            // Round 4 (review D3): no phrase stepping, no Right arrow; read first has its count back.
            Assert.DoesNotContain("Right arrow", TakesWindow.TeleprompterHelp);
            Assert.DoesNotContain("phrase", TakesWindow.TeleprompterHelp);
            Assert.Contains("3-2-1", TakesWindow.ReadFirstHelp);
        }

        [Fact]
        public void AddAttempt_SessionFile_IsKept()
        {
            var store = NewStore();
            store.Load(List(null, new Take { Id = "opening", Text = "Hello." }));

            var a = store.AddAttempt("opening", @"s\takes\opening\attempt-1.mp4", @"s\takes\opening", 5.0, sessionFile: @"s\recording.mp4");

            Assert.Equal(@"s\recording.mp4", a.SessionFile);
            Assert.Equal(@"s\recording.mp4", store.Snapshot().Takes[0].Attempts[0].SessionFile);
        }

        [Fact]
        public void LastTakeMessage_SaysSpaceRecordsAgainAndEscFinishes()
        {
            Assert.Contains("Space", TakeRecordingWindow.LastTakeMessage);
            Assert.Contains("Esc", TakeRecordingWindow.LastTakeMessage);
        }

        [Fact]
        public void WaitInstruction_DoesNotSayRecording()
        {
            Assert.DoesNotContain("Recording", TakeRecordingWindow.WaitInstruction);
            Assert.Contains("wait", TakeRecordingWindow.WaitInstruction);
        }
    }
}
