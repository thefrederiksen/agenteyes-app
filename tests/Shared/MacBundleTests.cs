using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AgentEyes.DesktopApp;
using Xunit;

// Issue #118: AgentEyes.app, the macOS bundle of the desktop app. ONE source file, compiled into
// AgentEyes.Tests.DesktopApp (in AgentEyes.sln: the Windows gate) and AgentEyes.Tests.Linux (CI), like
// DesktopAppQuitTests. They read the committed bundle inputs - src/AgentEyes.DesktopApp/macOS/Info.plist,
// assets/AgentEyes.icns - and the compiled app, so they run on every platform; none of them builds a
// bundle, signs anything or starts the app. What a built and signed bundle does is a runtime check on a
// Mac (docs/cencon/proof/issue-118/handoff.md).
namespace AgentEyes.Tests.Shared
{
    [Collection(HeadlessAvaloniaCollection.Name)]
    public sealed class MacBundleTests
    {
        /// <summary>
        /// The permanent identity. macOS keys Screen Recording, Microphone and Camera grants to it: changing
        /// it makes every user grant every permission again. If this test fails, the change is the defect.
        /// </summary>
        private const string PermanentBundleIdentifier = "com.agenteyes.app";

        private readonly GuardHeadlessSession _session;

        public MacBundleTests(GuardHeadlessSession session) => _session = session;

        [Fact]
        public void InfoPlist_BundleIdentifier_IsThePermanentOne()
        {
            Assert.Equal(PermanentBundleIdentifier, PlistString("CFBundleIdentifier"));
        }

        [Fact]
        public void InfoPlist_Executable_IsTheAppsAssemblyName()
        {
            // The apphost a publish produces is named after the assembly; a bundle whose CFBundleExecutable
            // names anything else does not launch.
            string assemblyName = typeof(AgentEyes.DesktopApp.App).Assembly.GetName().Name!;
            Assert.Equal("AgentEyesDesktop", assemblyName);
            Assert.Equal(assemblyName, PlistString("CFBundleExecutable"));
        }

        [Fact]
        public void AppName_LoadedFromXaml_EqualsTheBundleName()
        {
            // Without Application.Name, Avalonia tells macOS the app is "Avalonia Application" - the menu bar
            // and Launch Services show that even inside a bundle that says AgentEyes (measured, #118).
            string? name = _session.Dispatch(() =>
            {
                // App.Initialize is the app's own AvaloniaXamlLoader.Load(this), which the Avalonia build
                // compiles into a call to App.axaml's generated loader; a Load from this assembly is not.
                var app = new AgentEyes.DesktopApp.App();
                app.Initialize();
                return app.Name;
            });
            Assert.Equal("AgentEyes", name);
            Assert.Equal("AgentEyes", PlistString("CFBundleName"));
            Assert.Equal("AgentEyes", PlistString("CFBundleDisplayName"));
        }

        [Fact]
        public void InfoPlist_Version_IsFilledInByTheBuild_AndTheAppVersionIsTheEngines()
        {
            // build-app.sh replaces __VERSION__ with the app's <Version>; the app's version must be the
            // product's, which is the engine's.
            Assert.Equal("__VERSION__", PlistString("CFBundleShortVersionString"));
            Assert.Equal("__VERSION__", PlistString("CFBundleVersion"));
            Version? app = typeof(AgentEyes.DesktopApp.App).Assembly.GetName().Version;
            AssemblyName? engine = typeof(AgentEyes.DesktopApp.App).Assembly.GetReferencedAssemblies()
                .SingleOrDefault(a => a.Name == "agenteyes");
            Assert.True(engine != null, "AgentEyesDesktop does not reference the engine assembly 'agenteyes' - the instrument found nothing to compare");
            Assert.Equal(engine!.Version, app);
        }

        [Fact]
        public void InfoPlist_ApplicationKeys_ArePresent()
        {
            Assert.Equal("APPL", PlistString("CFBundlePackageType"));
            Assert.Equal("AgentEyes.icns", PlistString("CFBundleIconFile"));
            Assert.Equal("NSApplication", PlistString("NSPrincipalClass"));
            Assert.Equal("true", PlistValue("NSHighResolutionCapable").Name.LocalName);
            Assert.Matches(@"^\d+\.\d+$", PlistString("LSMinimumSystemVersion"));
        }

        [Fact]
        public void InfoPlist_UsageDescriptions_ArePresentAndSayWhatHappens()
        {
            foreach (string key in new[] { "NSMicrophoneUsageDescription", "NSCameraUsageDescription" })
            {
                string text = PlistString(key);
                Assert.True(text.Length >= 40, $"{key} is '{text}' - macOS shows it to the user, it must say what AgentEyes does");
                Assert.Contains("recording you started", text);
            }
        }

        [Fact]
        public void BundleInputs_AreAsciiOnly()
        {
            byte[] bytes = File.ReadAllBytes(PlistPath);
            int bad = Array.FindIndex(bytes, b => b > 0x7F);
            Assert.True(bad < 0, $"Info.plist has a non-ASCII byte at offset {bad}");
        }

        [Fact]
        public void Icns_HasEverySizeUpTo256_AndNoUpscaledSlot()
        {
            // The only source is assets/icon-256.png. The .icns must carry every slot it can fill by
            // DOWN-scaling, and no slot that would be the 256-pixel source blown up (scripts/mac/make-icns.sh).
            Dictionary<string, int> entries = IcnsEntries(Path.Combine(RepoRoot(), "assets", "AgentEyes.icns"));
            Assert.True(entries.Count > 0, "assets/AgentEyes.icns has no image entries - the instrument read nothing");

            // Each slot with the entry types iconutil may write for it: 16 px is ic04 (macOS 26 iconutil,
            // measured) or icp4 (older), 32 px is ic05 or icp5; the rest have one type each.
            var slots = new (string Slot, string[] Types)[]
            {
                ("16", new[] { "ic04", "icp4" }), ("32", new[] { "ic05", "icp5" }),
                ("16@2x", new[] { "ic11" }), ("32@2x", new[] { "ic12" }),
                ("128", new[] { "ic07" }), ("128@2x", new[] { "ic13" }), ("256", new[] { "ic08" }),
            };
            foreach (var (slot, types) in slots)
                Assert.True(types.Any(entries.ContainsKey),
                    $"AgentEyes.icns has no {slot} entry ({string.Join(" or ", types)}); it has: {string.Join(", ", entries.Keys)}");
            // ic14 256@2x (512 px), ic09 512, ic10 512@2x (1024 px): only possible by upscaling the source
            foreach (string type in new[] { "ic14", "ic09", "ic10" })
                Assert.False(entries.ContainsKey(type), $"AgentEyes.icns has an upscaled '{type}' entry - a 512 or 1024 pixel slot from a 256 pixel source");
        }

        // ---- the instruments ----

        private static string PlistPath => Path.Combine(RepoRoot(), "src", "AgentEyes.DesktopApp", "macOS", "Info.plist");

        /// <summary>The value element that follows &lt;key&gt;<paramref name="key"/>&lt;/key&gt; in the top-level dict.</summary>
        private static XElement PlistValue(string key)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(PlistPath, settings);
            XElement dict = XDocument.Load(reader).Root!.Element("dict")
                ?? throw new InvalidOperationException("Info.plist has no top-level <dict>");
            XElement[] keys = dict.Elements("key").Where(k => k.Value == key).ToArray();
            Assert.True(keys.Length == 1, $"Info.plist has {keys.Length} '{key}' keys, expected exactly one");
            return keys[0].ElementsAfterSelf().First();
        }

        private static string PlistString(string key)
        {
            XElement value = PlistValue(key);
            Assert.True(value.Name.LocalName == "string", $"Info.plist '{key}' is a <{value.Name.LocalName}>, expected <string>");
            return value.Value;
        }

        /// <summary>The .icns entries by four-character type, with their lengths. Throws on a malformed file.</summary>
        private static Dictionary<string, int> IcnsEntries(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 4) != "icns")
                throw new InvalidDataException($"{path} does not start with the 'icns' magic");
            int total = BigEndian(b, 4);
            if (total != b.Length) throw new InvalidDataException($"{path} header says {total} bytes, the file has {b.Length}");
            var entries = new Dictionary<string, int>();
            for (int at = 8; at < b.Length;)
            {
                string type = Encoding.ASCII.GetString(b, at, 4);
                int len = BigEndian(b, at + 4);
                if (len < 8 || at + len > b.Length) throw new InvalidDataException($"{path}: entry '{type}' at {at} has a bad length {len}");
                entries[type] = len;
                at += len;
            }
            return entries;
        }

        private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "AgentEyes.sln"))) return d.FullName;
            throw new InvalidOperationException($"AgentEyes.sln not found above {AppContext.BaseDirectory}");
        }
    }
}
