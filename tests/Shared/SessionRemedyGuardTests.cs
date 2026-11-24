using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

// Issue #221: no user-facing text tells a Linux user to log in with a named desktop session.
//
// Four places told the user to log in with "Ubuntu on Xorg": the runtime refusal
// (src/AgentEyes.Core/Platform/Linux/LinuxDisplay.cs), the installer refusal (scripts/linux/install.sh),
// README.md, and a release-notes bullet (docs/releases/v1.12.0.md, caught by the Review Gate on PR #201).
// That session is not installed by a default desktop install of the Ubuntu releases after 24.04, so the
// only instruction a blocked user got was one they could not follow. The code cannot verify that a
// login session exists on the reader's machine, so no product string may name one, and no document may
// tell the reader to choose one.
//
// What this guard scans, and how: every user-facing text surface - README.md, docs/releases/*.md,
// every *.cs under src/, every *.sh and *.ps1 under scripts/. docs/cencon/proof/ is the historical
// record and quotes the old strings on purpose; it is NOT scanned. Each file is read in its JOINED form:
// comment prefixes dropped, C# string concatenations ("..." + "...") closed up, and every run of
// whitespace (line breaks included) collapsed to one space - because a wrapped line defeats a
// single-line search, which has already produced two wrong numbers in this repository (issue #218).
//
// ONE source file, compiled into the Windows, Linux and macOS suites; it only reads files.
namespace AgentEyes.Tests.Shared
{
    public sealed class SessionRemedyGuardTests
    {
        /// <summary>The four strings issue #221 removed, in their joined form. Reinstating any one of them,
        /// in any scanned file, turns this guard red.</summary>
        internal static readonly string[] RemovedStrings =
        {
            // LinuxDisplay.cs:20 (runtime) and install.sh:93 (installer)
            "log in with 'Ubuntu on Xorg'",
            // README.md:76
            "log in with \"Ubuntu on Xorg\"",
            // docs/releases/v1.12.0.md, the bullet the Review Gate rejected on PR #201
            "choose \"Ubuntu on Xorg\" at the login screen",
        };

        /// <summary>The class, not only the instance: an instruction to log in to / choose / select / switch
        /// to a NAMED desktop session ("Ubuntu on Xorg", "GNOME on Wayland", "Plasma (X11)", ...).</summary>
        internal static readonly Regex SessionInstruction = new Regex(
            @"\b(log ?in|sign ?in|choose|select|pick|switch(es)? to|use)\b[^.;]{0,60}?"
            + @"\b(Ubuntu|GNOME|Plasma|KDE|Xfce|Cinnamon|MATE)\s*(on\s+(Xorg|X11|Wayland)|\((X11|Xorg|Wayland)\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Shipped product code runs on every release, so it may not even NAME a login session.</summary>
        internal static readonly Regex SessionName = new Regex(
            @"\b(Ubuntu|GNOME|Plasma|KDE|Xfce|Cinnamon|MATE)\s*(on\s+(Xorg|X11|Wayland)|\((X11|Xorg|Wayland)\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The four files the strings lived in. The scan must cover every one of them, so a scan
        /// that silently reads nothing (or loses a file) is a failure, not a clean pass.</summary>
        private static readonly string[] KnownSurfaces =
        {
            "README.md",
            "docs/releases/v1.12.0.md",
            "scripts/linux/install.sh",
            "src/AgentEyes.Core/Platform/Linux/LinuxDisplay.cs",
        };

        [Fact]
        public void ScannedSurfaces_FoundBySearch_IncludeEveryFileTheStringsLivedIn()
        {
            IReadOnlyList<string> scanned = ScannedFiles().Select(f => f.Relative).ToList();

            foreach (string known in KnownSurfaces)
                Assert.True(scanned.Contains(known),
                    $"{known} is where a removed session remedy lived, but the scan did not read it. Scanned {scanned.Count} file(s).");
        }

        [Fact]
        public void UserFacingText_AnySurface_ContainsNoRemovedSessionRemedy()
        {
            var hits = new List<string>();
            foreach ((string rel, string joined) in ScannedFiles())
                foreach (string removed in RemovedStrings)
                    if (joined.Contains(removed, StringComparison.OrdinalIgnoreCase))
                        hits.Add($"{rel}: \"{removed}\"");

            Assert.True(hits.Count == 0,
                "A session remedy removed by issue #221 is back. That login session is not installed by a default "
                + "desktop install of current Ubuntu, so the instruction cannot be followed. Hits: " + string.Join(" | ", hits));
        }

        [Fact]
        public void UserFacingText_AnySurface_NeverTellsTheReaderToChooseANamedSession()
        {
            var hits = new List<string>();
            foreach ((string rel, string joined) in ScannedFiles())
                foreach (Match m in SessionInstruction.Matches(joined))
                    hits.Add($"{rel}: \"{m.Value}\"");

            Assert.True(hits.Count == 0,
                "User-facing text tells the reader to choose a named login session. AgentEyes cannot verify that "
                + "session is installed on the reader's machine (issue #221). Hits: " + string.Join(" | ", hits));
        }

        [Fact]
        public void ProductCode_SrcAndScripts_NamesNoLoginSession()
        {
            var hits = new List<string>();
            foreach ((string rel, string joined) in ScannedFiles())
            {
                if (!rel.StartsWith("src/", StringComparison.Ordinal) && !rel.StartsWith("scripts/", StringComparison.Ordinal)) continue;
                foreach (Match m in SessionName.Matches(joined))
                    hits.Add($"{rel}: \"{m.Value}\"");
            }

            Assert.True(hits.Count == 0,
                "Shipped code names a login session; it runs on releases where that session may not exist (issue #221). "
                + "Hits: " + string.Join(" | ", hits));
        }

        /// <summary>Positive controls: each original string, in the exact shape it had in its file (split
        /// across a C# concatenation, wrapped across Markdown lines), is FOUND by the same joining and
        /// matching the guard uses. Without these, an empty result would certify nothing.</summary>
        [Theory]
        [InlineData("cs",
            "                    throw new UsageException(\n"
            + "                        \"this is a Wayland session with no X display. AgentEyes on Linux records an X11 \"\n"
            + "                        + \"session only - log in with 'Ubuntu on Xorg'.\");")]
        [InlineData("sh",
            "[[ \"${XDG_SESSION_TYPE:-x11}\" == \"wayland\" ]] && fail \"this is a Wayland session; AgentEyes on Linux records X11 only (log in with 'Ubuntu on Xorg')\"")]
        [InlineData("md",
            "To **update**, download the newer release's script and run it again; there is no in-app updater on\n"
            + "Linux. Wayland sessions are not supported - log in with \"Ubuntu on Xorg\".")]
        [InlineData("md",
            "- **Refuses to run on Wayland.** AgentEyes on Linux records X11 only, and the script stops if the session is\n"
            + "  Wayland by default, so log in to an X11 session first (on Ubuntu, choose \"Ubuntu on Xorg\" at\n"
            + "  the login screen).")]
        public void Join_OriginalShapeOfEachRemovedString_IsFoundByTheGuard(string kind, string original)
        {
            string joined = Join(original, kind);

            Assert.Contains(RemovedStrings, removed => joined.Contains(removed, StringComparison.OrdinalIgnoreCase));
            Assert.Matches(SessionInstruction, joined);
            Assert.Matches(SessionName, joined);
        }

        [Theory]
        [InlineData("log in with 'GNOME on Xorg'")]
        [InlineData("select Plasma (X11) at the login screen")]
        [InlineData("switch to Ubuntu on Wayland")]
        public void SessionInstruction_OtherNamedSessions_AreCaught(string text)
        {
            Assert.Matches(SessionInstruction, Join(text, "md"));
        }

        [Fact]
        public void SessionInstruction_StatementOfWhatATestMachineRuns_IsNotAnInstruction()
        {
            // The v1.12.0 notes record, as a measured fact, which session the 24.04 test machine has. That
            // tells nobody to choose it, and the guard must not force the fact out.
            string text = "The test machine runs Ubuntu 24.04,\n  where an X11 session, \"Ubuntu on Xorg\", is installed.";

            Assert.DoesNotMatch(SessionInstruction, Join(text, "md"));
        }

        /// <summary>A file's joined form: comment prefixes dropped, C# string concatenations closed up,
        /// and all whitespace (line breaks included) collapsed to one space.</summary>
        internal static string Join(string text, string kind)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n')
                .Select(l => kind == "cs" ? Regex.Replace(l, @"^\s*(///|//|\*)\s?", " ")
                           : kind == "sh" || kind == "ps1" ? Regex.Replace(l, @"^\s*#\s?", " ")
                           : l);
            string joined = string.Join(" ", lines);
            if (kind == "cs") joined = Regex.Replace(joined, "\"\\s*\\+\\s*@?\\$?\"", string.Empty);
            return Regex.Replace(joined, @"\s+", " ");
        }

        private static IReadOnlyList<(string Relative, string Joined)> ScannedFiles()
        {
            string root = RepoRoot();
            var files = new List<string> { Path.Combine(root, "README.md") };
            files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "docs", "releases"), "*.md", SearchOption.AllDirectories));
            files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories));
            foreach (string pattern in new[] { "*.sh", "*.ps1" })
                files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "scripts"), pattern, SearchOption.AllDirectories));

            return files
                .Select(f => (Full: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
                .Where(f => !f.Rel.Split('/').Any(part => part == "bin" || part == "obj"))
                .Select(f => (f.Rel, Join(File.ReadAllText(f.Full), Path.GetExtension(f.Full).TrimStart('.'))))
                .OrderBy(f => f.Rel, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The repo root, stamped into the test assembly by its .csproj (RepoRoot metadata), so
        /// the scan can never silently run somewhere with nothing in it.</summary>
        private static string RepoRoot()
        {
            string? root = typeof(SessionRemedyGuardTests).Assembly
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
