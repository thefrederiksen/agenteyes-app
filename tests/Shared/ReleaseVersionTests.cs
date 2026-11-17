using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

// Issue #196: every project that declares a <Version> declares the SAME one.
//
// release.yml checks the pushed tag against src/AgentEyes.App/AgentEyes.App.csproj alone, so a release
// whose other projects were not bumped publishes cleanly and ships an app reporting an older version -
// which is what scripts/new-release.ps1 would have done to src/AgentEyes.DesktopApp after #116 added it.
// This test is the check that goes red BEFORE a tag exists. ONE source file, compiled into the Windows
// suite (AgentEyes.Tests), the Linux suite and the macOS suite; it only reads project files.
//
// The discovery rule is the one scripts/new-release.ps1 uses: every *.csproj / *.props / *.targets that
// declares a <Version>...</Version> element outside an XML comment, excluding docs/ (frozen proof
// harnesses) and bin/obj (build output).
namespace AgentEyes.Tests.Shared
{
    public sealed class ReleaseVersionTests
    {
        /// <summary>The projects that declare a version today. The search must find at least these, so a
        /// discovery that silently finds nothing (or loses a project) is a failure, not a pass.</summary>
        private static readonly string[] KnownVersionFiles =
        {
            "src/AgentEyes.App/AgentEyes.App.csproj",
            "src/AgentEyes.Core/AgentEyes.Core.csproj",
            "src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj",
            "tools/AgentEyes.Setup/AgentEyes.Setup.csproj",
            "tools/AgentEyes.Setup.Cli/AgentEyes.Setup.Cli.csproj",
        };

        private static readonly Regex VersionElement = new Regex("<Version>([^<]*)</Version>", RegexOptions.Compiled);
        private static readonly Regex XmlComment = new Regex("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

        [Fact]
        public void DeclaredVersions_FoundBySearch_IncludeEveryKnownProject()
        {
            IReadOnlyList<string> found = DeclaredVersions().Select(d => d.File).ToList();

            foreach (string known in KnownVersionFiles)
                Assert.True(found.Contains(known),
                    $"{known} declares a <Version> but the search did not find it. Found: {string.Join(", ", found)}");
        }

        [Fact]
        public void DeclaredVersions_EveryProject_DeclaresExactlyOneVersion()
        {
            foreach ((string file, IReadOnlyList<string> versions) in DeclaredVersions())
                Assert.True(versions.Count == 1,
                    $"{file} declares {versions.Count} <Version> elements ({string.Join(", ", versions)}); "
                    + "scripts/new-release.ps1 refuses a file with more than one.");
        }

        [Fact]
        public void DeclaredVersions_AllProjects_DeclareTheSameVersion()
        {
            var declared = DeclaredVersions();
            Assert.True(declared.Count > 0, "The search found no project declaring a <Version> - the instrument looked at nothing.");

            // The reference is the one release.yml checks the tag against, so the message says which
            // files would ship a version different from the tag.
            const string reference = "src/AgentEyes.App/AgentEyes.App.csproj";
            string expected = declared.Single(d => d.File == reference).Versions.First();

            var outOfStep = declared
                .Where(d => d.Versions.Any(v => v != expected))
                .Select(d => $"{d.File} declares {string.Join(", ", d.Versions)}")
                .ToList();

            Assert.True(outOfStep.Count == 0,
                $"Project versions disagree. {reference} (the one release.yml checks the tag against) declares "
                + $"{expected}, but:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", outOfStep)}"
                + $"{Environment.NewLine}Run scripts/new-release.ps1, which bumps every declared version, or fix the file(s) by hand.");
        }

        /// <summary>Every project file declaring a &lt;Version&gt;, repo-relative with '/' separators, sorted.</summary>
        private static List<(string File, IReadOnlyList<string> Versions)> DeclaredVersions()
        {
            string root = RepoRoot();
            var result = new List<(string, IReadOnlyList<string>)>();
            foreach (string path in ProjectFiles(root, isRoot: true))
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                var versions = VersionElement.Matches(XmlComment.Replace(File.ReadAllText(path), string.Empty)).Select(m => m.Groups[1].Value).ToList();
                if (versions.Count > 0) result.Add((rel, versions));
            }
            return result.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
        }

        /// <summary>The *.csproj / *.props / *.targets files under <paramref name="dir"/>, never entering
        /// .git, build output (bin, obj) or the top-level docs folder.</summary>
        private static IEnumerable<string> ProjectFiles(string dir, bool isRoot)
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (ext == ".csproj" || ext == ".props" || ext == ".targets") yield return file;
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name == ".git" || name == "bin" || name == "obj") continue;
                if (isRoot && name == "docs") continue;
                foreach (string file in ProjectFiles(sub, isRoot: false)) yield return file;
            }
        }

        /// <summary>The repo root, stamped into the test assembly by its .csproj (RepoRoot metadata), so
        /// the search can never silently run somewhere with no projects in it.</summary>
        private static string RepoRoot()
        {
            string? root = typeof(ReleaseVersionTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("The RepoRoot assembly metadata is missing - add it to this test project's .csproj.");
            if (!File.Exists(Path.Combine(root, "AgentEyes.sln")))
                throw new InvalidOperationException($"No AgentEyes.sln under the stamped repo root '{root}'.");
            return root;
        }
    }
}
