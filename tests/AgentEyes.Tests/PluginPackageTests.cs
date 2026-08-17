using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using AgentEyes.Plugins;

namespace AgentEyes.Tests
{
    public class PluginPackageTests
    {
        // ---- local zip install --------------------------------------------

        [Fact]
        public void InstallZip_installs_a_valid_plugin_and_returns_its_id()
        {
            using var tmp = new TempDir();
            byte[] zip = MakeZip(
                ("plugin.json", "{\"id\":\"my-plugin\",\"command\":[\"run.cmd\"]}"),
                ("run.cmd", "echo hi"));

            string id = PluginPackage.InstallZip(zip, tmp.Path);

            Assert.Equal("my-plugin", id);
            Assert.True(File.Exists(Path.Combine(tmp.Path, "my-plugin", "plugin.json")));
            Assert.True(File.Exists(Path.Combine(tmp.Path, "my-plugin", "run.cmd")));
        }

        [Fact]
        public void InstallZip_accepts_a_zip_of_the_folder_not_just_its_contents()
        {
            // People zip the folder, producing "my-plugin/plugin.json" rather than root-level.
            using var tmp = new TempDir();
            byte[] zip = MakeZip(("my-plugin/plugin.json", "{\"id\":\"my-plugin\",\"command\":[\"x\"]}"));

            string id = PluginPackage.InstallZip(zip, tmp.Path);

            Assert.Equal("my-plugin", id);
            Assert.True(File.Exists(Path.Combine(tmp.Path, "my-plugin", "plugin.json")));
        }

        [Fact]
        public void InstallZip_rejects_a_package_with_no_manifest()
        {
            using var tmp = new TempDir();
            byte[] zip = MakeZip(("readme.txt", "no manifest here"));

            var ex = Assert.Throws<InvalidOperationException>(() => PluginPackage.InstallZip(zip, tmp.Path));
            Assert.Contains("plugin.json", ex.Message);
        }

        [Fact]
        public void InstallZip_rejects_zip_slip()
        {
            using var tmp = new TempDir();
            byte[] zip = MakeZip(
                ("plugin.json", "{\"id\":\"ok\",\"command\":[\"x\"]}"),
                ("../escape.txt", "pwned"));

            var ex = Assert.Throws<InvalidOperationException>(() => PluginPackage.InstallZip(zip, tmp.Path));
            Assert.Contains("escapes the plugin folder", ex.Message);
        }

        [Fact]
        public void InstallZip_verifies_sha256_when_expected_and_rejects_a_mismatch()
        {
            using var tmp = new TempDir();
            byte[] zip = MakeZip(("plugin.json", "{\"id\":\"ok\",\"command\":[\"x\"]}"));

            var ex = Assert.Throws<InvalidOperationException>(
                () => PluginPackage.InstallZip(zip, tmp.Path, expectedSha256: new string('a', 64)));
            Assert.Contains("SHA-256 mismatch", ex.Message);
            Assert.False(Directory.Exists(Path.Combine(tmp.Path, "ok")));   // nothing installed
        }

        [Fact]
        public void InstallZip_rejects_a_manifest_with_no_id()
        {
            using var tmp = new TempDir();
            byte[] zip = MakeZip(("plugin.json", "{\"command\":[\"x\"]}"));

            var ex = Assert.Throws<InvalidOperationException>(() => PluginPackage.InstallZip(zip, tmp.Path));
            Assert.Contains("no \"id\"", ex.Message);
        }

        [Fact]
        public void InstallZip_rejects_an_id_that_is_not_folder_safe()
        {
            using var tmp = new TempDir();
            byte[] zip = MakeZip(("plugin.json", "{\"id\":\"../evil\",\"command\":[\"x\"]}"));

            Assert.Throws<InvalidOperationException>(() => PluginPackage.InstallZip(zip, tmp.Path));
        }

        [Fact]
        public void InstallZip_replaces_an_existing_install_of_the_same_id()
        {
            using var tmp = new TempDir();
            PluginPackage.InstallZip(MakeZip(("plugin.json", "{\"id\":\"p\",\"command\":[\"x\"]}"),
                ("old.txt", "v1")), tmp.Path);
            PluginPackage.InstallZip(MakeZip(("plugin.json", "{\"id\":\"p\",\"command\":[\"x\"]}")), tmp.Path);

            Assert.False(File.Exists(Path.Combine(tmp.Path, "p", "old.txt")));   // old files gone
            Assert.True(File.Exists(Path.Combine(tmp.Path, "p", "plugin.json")));
        }

        // ---- folder install ------------------------------------------------

        [Fact]
        public void InstallFolder_copies_the_plugin_and_leaves_the_source_intact()
        {
            using var tmp = new TempDir();
            string src = Path.Combine(tmp.Path, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "plugin.json"), "{\"id\":\"folder-plug\",\"command\":[\"x\"]}");

            string id = PluginPackage.InstallFolder(src, Path.Combine(tmp.Path, "plugins"));

            Assert.Equal("folder-plug", id);
            Assert.True(File.Exists(Path.Combine(tmp.Path, "plugins", "folder-plug", "plugin.json")));
            Assert.True(File.Exists(Path.Combine(src, "plugin.json")));   // source untouched
        }

        // ---- remove --------------------------------------------------------

        [Fact]
        public void Remove_deletes_the_folder_and_its_settings_file()
        {
            using var tmp = new TempDir();
            PluginPackage.InstallZip(MakeZip(("plugin.json", "{\"id\":\"p\",\"command\":[\"x\"]}")), tmp.Path);
            File.WriteAllText(Path.Combine(tmp.Path, "p.settings.json"), "{}");

            PluginPackage.Remove(tmp.Path, "p");

            Assert.False(Directory.Exists(Path.Combine(tmp.Path, "p")));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "p.settings.json")));
        }

        // ---- helpers -------------------------------------------------------

        private static byte[] MakeZip(params (string Name, string Content)[] entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    var e = zip.CreateEntry(name);
                    using var s = e.Open();
                    var bytes = Encoding.UTF8.GetBytes(content);
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agenteyes-plugintest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
            }
        }
    }
}
