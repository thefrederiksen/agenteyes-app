using System.IO;
using AgentEyes.Platform.Linux;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class V4l2DevicesTests
    {
        private static string FakeSysClass()
        {
            string root = LinuxTestIsolation.NewTempDir("video4linux");
            void Node(string node, string name, string index)
            {
                string d = Path.Combine(root, node);
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "name"), name + "\n");
                File.WriteAllText(Path.Combine(d, "index"), index + "\n");
            }
            Node("video0", "HD Pro Webcam C920", "0");
            Node("video1", "HD Pro Webcam C920", "1");     // the metadata node of the same camera
            Node("video10", "Integrated Camera", "0");
            return root;
        }

        [Fact]
        public void CameraNames_CaptureNodesOnly_InNodeOrder()
        {
            var names = V4l2Devices.CameraNames(FakeSysClass());

            Assert.Equal(new[] { "HD Pro Webcam C920 [/dev/video0]", "Integrated Camera [/dev/video10]" }, names);
        }

        [Fact]
        public void CameraNames_MissingSysClass_ReturnsEmpty()
        {
            Assert.Empty(V4l2Devices.CameraNames(Path.Combine(LinuxTestIsolation.RunRoot, "does-not-exist")));
        }

        [Fact]
        public void DevicePath_ListedName_ReturnsTheNode()
        {
            Assert.Equal("/dev/video0", V4l2Devices.DevicePath("HD Pro Webcam C920 [/dev/video0]"));
        }

        [Fact]
        public void DevicePath_NameWithoutNode_Throws()
        {
            Assert.Throws<UsageException>(() => V4l2Devices.DevicePath("HD Pro Webcam C920"));
        }

        [Fact]
        public void ResolveCameraName_Fragment_MatchesTheListedName()
        {
            var names = V4l2Devices.CameraNames(FakeSysClass());
            Assert.Equal("HD Pro Webcam C920 [/dev/video0]", DeviceResolver.ResolveCameraName(names, "c920"));
        }
    }
}
