using System;
using System.IO;
using System.Text;
using AgentEyes.Platform.Linux;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    public sealed class CredentialFileTests
    {
        [Fact]
        public void Write_NewFile_IsMode600InA700Folder()
        {
            string dir = Path.Combine(LinuxTestIsolation.NewTempDir("cred"), "AgentEyes");
            string path = Path.Combine(dir, "devthrottle.cred");

            CredentialFile.Write(path, Encoding.UTF8.GetBytes("{\"ApiKey\":\"dt_x\"}"));

            Assert.Equal(CredentialFile.OwnerOnly, File.GetUnixFileMode(path));
            Assert.Equal("600", CredentialFile.Describe(path));
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(dir) & (UnixFileMode.OtherRead | UnixFileMode.GroupRead | UnixFileMode.OtherExecute | UnixFileMode.GroupExecute));
            Assert.Equal("{\"ApiKey\":\"dt_x\"}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void Write_OverAWorldReadableFile_LeavesItMode600()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("cred"), "devthrottle.cred");
            File.WriteAllText(path, "old");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            CredentialFile.Write(path, Encoding.UTF8.GetBytes("new"));

            Assert.Equal(CredentialFile.OwnerOnly, File.GetUnixFileMode(path));
        }

        [Fact]
        public void RequirePrivate_Mode644_ThrowsWithTheFix()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("cred"), "devthrottle.cred");
            File.WriteAllText(path, "x");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            var ex = Assert.Throws<InvalidOperationException>(() => CredentialFile.RequirePrivate(path));
            Assert.Contains("chmod 600", ex.Message);
        }

        [Fact]
        public void RequirePrivate_Mode600_Passes()
        {
            string path = Path.Combine(LinuxTestIsolation.NewTempDir("cred"), "devthrottle.cred");
            CredentialFile.Write(path, new byte[] { 1 });
            CredentialFile.RequirePrivate(path);
        }

        [Fact]
        public void DevThrottleAccount_SaveThenLoad_RoundTripsThroughThe600File()
        {
            // AppDataPaths is redirected by LinuxTestIsolation, so this is the run's own folder.
            DevThrottle.DevThrottleAccount.Save(new DevThrottle.DevThrottleCredential { ApiKey = "dt_test", Email = "a@b.c" });
            try
            {
                var back = DevThrottle.DevThrottleAccount.Load();
                Assert.NotNull(back);
                Assert.Equal("dt_test", back!.ApiKey);
                Assert.Equal("600", CredentialFile.Describe(Path.Combine(AppDataPaths.Root, "devthrottle.cred")));
            }
            finally
            {
                DevThrottle.DevThrottleAccount.Clear();
            }
        }
    }
}
