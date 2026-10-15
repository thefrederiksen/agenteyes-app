using AgentEyes.Headless;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class HeadlessRequestTests
    {
        [Fact]
        public void ReadStart_EmptyBody_VideoScreen1MicOnlyDefaults()
        {
            var r = ControlApiRequest.ReadStart(ControlApiRequest.Parse(""));

            Assert.Equal("video", r.Mode);
            Assert.Equal(1, r.Screen);
            Assert.Equal(AudioSourceKind.Mic, r.Source);
            Assert.Null(r.Mic);
            Assert.Equal(30, r.Fps);
            Assert.False(r.Options.NoiseGate);   // GateDefaults: mic-only has no gate
            Assert.Equal(0.7, r.Options.SystemGain, 6);
        }

        [Fact]
        public void ReadStart_MixedWithMicAndVolumes_ReadsEveryField()
        {
            var r = ControlApiRequest.ReadStart(ControlApiRequest.Parse(
                "{\"source\":\"mixed\",\"mic\":\"Yealink\",\"screen\":2,\"fps\":15,\"micVol\":80,\"sysVol\":50,\"camera\":\"C920\",\"region\":[0,0,640,480]}"));

            Assert.Equal(AudioSourceKind.Mixed, r.Source);
            Assert.Equal("Yealink", r.Mic);
            Assert.Equal(2, r.Screen);
            Assert.Equal(15, r.Fps);
            Assert.Equal(0.8, r.Options.MicGain, 6);
            Assert.Equal(0.5, r.Options.SystemGain, 6);
            Assert.True(r.Options.NoiseGate);
            Assert.Equal("C920", r.Camera);
            Assert.Equal(new[] { 0, 0, 640, 480 }, r.Region);
        }

        [Fact]
        public void Parse_NotJson_IsAUsageError()
        {
            Assert.Throws<UsageException>(() => ControlApiRequest.Parse("{not json"));
        }

        [Fact]
        public void StartBody_Mix_SendsMixedAndTheMic()
        {
            var body = HeadlessCli.StartBody(CliArgs.Parse(new[] { "start", "--mix", "--mic", "Yealink" }));

            Assert.Equal("mixed", body["source"]);
            Assert.Equal("Yealink", body["mic"]);
            Assert.Equal("video", body["mode"]);
            Assert.Equal(1, body["screen"]);
        }

        [Fact]
        public void StartBody_MixWithoutMic_Throws()
        {
            Assert.Throws<UsageException>(() => HeadlessCli.StartBody(CliArgs.Parse(new[] { "start", "--mix" })));
        }

        [Fact]
        public void StartBody_LoopbackOnly_IsSystemSource()
        {
            var body = HeadlessCli.StartBody(CliArgs.Parse(new[] { "start", "--loopback", "--screen", "2" }));
            Assert.Equal("system", body["source"]);
            Assert.Equal(2, body["screen"]);
        }

        [Fact]
        public void StartBody_NoAudioFlags_IsVideoOnly()
        {
            Assert.Equal("none", HeadlessCli.StartBody(CliArgs.Parse(new[] { "start" }))["source"]);
        }

        [Fact]
        public void StartBody_MixAndLoopback_Throws()
        {
            Assert.Throws<UsageException>(() => HeadlessCli.StartBody(CliArgs.Parse(new[] { "start", "--mix", "--loopback", "--mic", "x" })));
        }
    }
}
