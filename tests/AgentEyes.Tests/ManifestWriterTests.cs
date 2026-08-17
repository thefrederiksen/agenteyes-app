using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #155, criterion 4, SOURCE layer: the file-level view of who writes manifest.json.
    ///
    /// READ THIS FIRST - what this file is and is NOT. The enforcement of criterion 4 lives in
    /// <see cref="ManifestWriterIlTests"/>, which pins every call site of the ENUMERATED write APIs in
    /// the COMPILED IL of AgentEyes.Core and AgentEyesApp - and states there, exactly, the two shapes
    /// it cannot see (reflective invocation, and a path-taking framework API that is not on its
    /// list). This file is the source-level cross-check that sits beside
    /// it: it says WHICH FILES are involved, in the vocabulary a human reviews in, which IL names
    /// cannot. It is deliberately no longer claimed to be the guard, because it cannot be one - see
    /// the limits below, which are exactly the holes two reviewers walked through in rounds 1 and 2.
    ///
    /// What each layer actually claims - stated exactly, because a guard that is believed to cover
    /// more than it does is worse than no guard:
    ///
    ///  1. <see cref="Manifest"/> has no Save method and <c>Manifest.JsonOptions</c> is internal to
    ///     its own assembly, so the ordinary way to write one does not compile. A COMPILE-TIME floor
    ///     only: <see cref="Manifest"/> is an ordinary public type that <c>JsonSerializer</c> will
    ///     serialize with default options, so this removes the convenient route, not every route, and
    ///     reflection is not subject to it at all.
    ///  2. <see cref="ExpectedWriters"/> pins every <c>ManifestStore.Update</c> /
    ///     <c>ManifestStore.Replace</c> CALL SITE and its OPERATION, per file. An extra call inside an
    ///     already-listed file changes that file's counts and fails.
    ///     <see cref="TheCallSiteGuard_FailsOnAnExtraCallInAnAlreadyListedFile"/> is the negative
    ///     control. (<c>ManifestWriterIlTests</c> pins the same 22 call sites from the IL, per METHOD;
    ///     the two lists are independent counts of one fact and must agree.)
    ///  3. Only a pinned set of files may NAME manifest.json in code at all (comments excluded), and
    ///     of those, only a pinned few also contain a file-write API - each checked statement by
    ///     statement.
    ///
    /// LIMITS OF LAYER 3 - stated plainly, because claiming otherwise is what failed round 2. The
    /// statement scan splits on ';' and needs the manifest name and the write API in the SAME
    /// statement. It therefore does NOT see:
    ///
    ///  - a path aliased into a local in one statement and written in the next;
    ///  - a write routed through a helper in a file that never names the manifest;
    ///  - a name rebuilt at runtime ("manifest" + ".json");
    ///  - a write reached through reflection.
    ///
    /// Layer 3 is a narrowing device, not a closure. The FIRST THREE are covered by
    /// <see cref="ManifestWriterIlTests"/>, where the spelling of the C# is irrelevant because the
    /// compiled instruction is the same either way; the first two are committed there as negative
    /// controls in the exact form the QA Agent used to defeat this file.
    ///
    /// THE FOURTH IS NOT COVERED ANYWHERE, and this file used to say it was. Reflective invocation -
    /// <c>typeof(File).GetMethod("WriteAllText").Invoke(...)</c> - carries no metadata token naming
    /// the write API, so the IL guard does not report it either; and
    /// <see cref="NoSourceFileReachesTheManifestTypeThroughReflection"/> below only looks for
    /// reflection through the <see cref="Manifest"/> TYPE, which a writer serializing the object by
    /// other means never touches. Reflection is an OPEN limit of criterion 4 in this repository, and
    /// layer 1 does not cover it either - reflection is exactly what a compile-time barrier does not
    /// stop. What stands against reflection is human review of a short inventory. Not a test.
    ///
    /// The scans here read the repo's own SOURCE, and "which FILE writes this file" is a source fact.
    /// <see cref="RepoSource"/> stamps the repo root in at build time, so a scan can never silently
    /// look at nothing.
    /// </summary>
    public sealed class ManifestWriterTests
    {
        /// <summary>
        /// Every manifest write in the product, by call site. Update / Replace is the distinction
        /// that matters: Replace is only legitimate where the caller owns the whole content (a
        /// session publishing the record for a directory it just created, an import, the #153
        /// recovery record); everything that changes SOME fields of an existing recording must be an
        /// Update, or it erases whatever it never read.
        ///
        /// 22 call sites in 14 files: 13 Update, 9 Replace.
        /// </summary>
        private static readonly WriterFile[] ExpectedWriters =
        {
            new("src/AgentEyes.App/MainWindow.xaml.cs", Updates: 1, Replaces: 0,
                "Update: the Library rename sets DisplayName"),
            new("src/AgentEyes.App/RecordingDetailWindow.cs", Updates: 1, Replaces: 0,
                "Update: the detail-window rename sets DisplayName"),
            new("src/AgentEyes.Core/Commands.cs", Updates: 0, Replaces: 3,
                "Replace x3: a CLI capture session's own record (shot, audio, video)"),
            new("src/AgentEyes.Core/Package.cs", Updates: 1, Replaces: 1,
                "Update: what packaging produced. Replace: a synthesized bare-video manifest"),
            new("src/AgentEyes.Core/Packaging/TitleBackfill.cs", Updates: 1, Replaces: 0,
                "Update: the generated title/description, and the AI cost ADDED to the running total"),
            new("src/AgentEyes.Core/PostRecordingState.cs", Updates: 1, Replaces: 0,
                "Update: the issue #152 post-recording stage journal"),
            new("src/AgentEyes.Core/RecordingService.cs", Updates: 2, Replaces: 2,
                "Replace: the session's record published at start (issue #155) and a one-shot screenshot. "
                + "Update: the stop's own fields, and the deferred mux result"),
            new("src/AgentEyes.Core/RecoveryManifest.cs", Updates: 0, Replaces: 1,
                "Replace: the issue #153 reduced last-resort record"),
            new("src/AgentEyes.Core/SelfTest.cs", Updates: 0, Replaces: 1,
                "Replace: the throwaway self-test recording"),
            new("src/AgentEyes.Core/SubtitleBurner.cs", Updates: 1, Replaces: 0,
                "Update: registers the burned-in output file"),
            new("src/AgentEyes.Core/Thumbnails.cs", Updates: 1, Replaces: 0,
                "Update: the thumbnail attempt counter"),
            new("src/AgentEyes.Core/TranscriptionBacklog.cs", Updates: 2, Replaces: 0,
                "Update x2: the title attempt stamp, and the transcribe attempt counter"),
            new("src/AgentEyes.Core/Translator.cs", Updates: 1, Replaces: 0,
                "Update: registers a translated language and adds the AI cost to the running total"),
            new("src/AgentEyes.Core/VideoImport.cs", Updates: 1, Replaces: 1,
                "Replace: the imported recording's new record. Update: its transcript artifacts"),
        };

        /// <summary>One file's manifest writes, by operation.</summary>
        private sealed record WriterFile(string File, int Updates, int Replaces, string Why);

        /// <summary>The file that IS the canonical path - excluded from the "nobody else writes"
        /// scans, and asserted to still contain the atomic write itself.</summary>
        private const string StorePath = "src/AgentEyes.Core/ManifestStore.cs";

        /// <summary>The type that defines the manifest shape; it may name its own file.</summary>
        private const string ManifestPath = "src/AgentEyes.Core/Manifest.cs";

        /// <summary>
        /// Files allowed to NAME manifest.json in code, with what they do with it. Reading, existence
        /// checks and skip rules are all legitimate; writing is not, and layer 3 above is why this
        /// list is pinned rather than merely counted.
        /// </summary>
        private static readonly (string File, string Why)[] ManifestNameUsers =
        {
            ("src/AgentEyes.App/LibrarySnapshot.cs",             "a directory is a Library row when it holds one (issue #178)"),
            ("src/AgentEyes.Core/Commands.cs",                   "CLI: resolves a recording directory by the file's presence"),
            (ManifestPath,                                       "defines the file name and loads it"),
            (StorePath,                                          "THE writer"),
            ("src/AgentEyes.Core/Package.cs",                    "packaging: is this directory a recording, or a bare video?"),
            ("src/AgentEyes.Core/PostRecording.cs",              "the sequence reports a recording with no manifest"),
            ("src/AgentEyes.Core/PostRecordingPlan.cs",          "recovery scan: no manifest, nothing to resume"),
            ("src/AgentEyes.Core/RecordingLibrary.cs",           "a directory IS a recording when it holds one"),
            ("src/AgentEyes.Core/RecordingStopSequence.cs",      "raw-artifact detection excludes the manifest and its write-temps"),
            ("src/AgentEyes.Core/RecoveryManifest.cs",           "the reduced record excludes the manifest and its write-temps"),
            ("src/AgentEyes.Core/SubtitleBurner.cs",             "resolves the recording directory"),
            ("src/AgentEyes.Core/Thumbnails.cs",                 "thumbnail backlog: skip a directory with no manifest"),
            ("src/AgentEyes.Core/TranscriptionBacklog.cs",       "transcription backlog: skip a directory with no manifest"),
            ("src/AgentEyes.Core/Translator.cs",                 "resolves the recording directory"),
        };

        /// <summary>
        /// Of the files above, the ones that also contain a file-write API, and the NON-manifest file
        /// each of them writes. Every one is additionally checked statement by statement.
        /// </summary>
        private static readonly (string File, string Writes)[] WritersOfOtherFiles =
        {
            ("src/AgentEyes.Core/Commands.cs",  "File.Move of the pre-processing audio to its .original backup"),
            ("src/AgentEyes.Core/Package.cs",   "walkthrough.html, transcript.json, transcript.txt, transcript.<lang>.vtt"),
            ("src/AgentEyes.Core/Translator.cs", "transcript.<lang>.vtt for a translated language"),
        };

        private static readonly string[] WriteApis =
        {
            "File.WriteAllText(", "File.WriteAllBytes(", "File.WriteAllLines(",
            "File.AppendAllText(", "File.Create(", "File.OpenWrite(", "new FileStream(",
            "File.Copy(", "File.Move(",
        };

        // ---- source access ---------------------------------------------------

        /// <summary>Every product source file (no obj/bin), repo-relative with forward slashes.</summary>
        internal static IReadOnlyList<string> ProductionSources()
        {
            string root = Path.Combine(RepoSource.Root, "src");
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(p => Path.GetRelativePath(RepoSource.Root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // A scan that found nothing would pass every assertion below by finding no violations.
            Assert.True(files.Count > 20, $"Only {files.Count} source files found under src - the scan is looking at the wrong place.");
            return files;
        }

        /// <summary>One file's CODE: comments stripped, so a doc comment that mentions manifest.json
        /// is never mistaken for a file that touches it.</summary>
        internal static string CodeOf(string relativePath) => StripComments(RepoSource.Read(relativePath));

        internal static string StripComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            // Not preceded by a colon, so a "http://..." inside a string literal does not take the
            // rest of that line - and with it real code - out of the scan.
            return Regex.Replace(text, @"(?<!:)//[^\r\n]*", "");
        }

        /// <summary>
        /// The call-site counter, over a source tree supplied as (file -> code). Taking the reader as
        /// a parameter is what makes the negative control possible: the same code that guards the
        /// product is run against a tree with one extra call injected.
        /// </summary>
        internal static Dictionary<string, (int Updates, int Replaces)> CountWriters(
            IEnumerable<string> files, Func<string, string> read)
        {
            var counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (string file in files)
            {
                if (string.Equals(file, StorePath, StringComparison.OrdinalIgnoreCase)) continue;
                string code = read(file);
                int updates = Regex.Matches(code, @"ManifestStore\.Update\s*\(").Count;
                int replaces = Regex.Matches(code, @"ManifestStore\.Replace\s*\(").Count;
                if (updates + replaces > 0) counts[file] = (updates, replaces);
            }
            return counts;
        }

        private static Dictionary<string, (int Updates, int Replaces)> Expected() =>
            ExpectedWriters.ToDictionary(w => w.File, w => (w.Updates, w.Replaces), StringComparer.Ordinal);

        /// <summary>Both maps as sorted "file: nU nR" lines, so a failure names what moved.</summary>
        private static string Describe(Dictionary<string, (int Updates, int Replaces)> counts) =>
            string.Join(Environment.NewLine, counts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}: {kv.Value.Updates} Update, {kv.Value.Replaces} Replace"));

        // ---- layer 1: it does not compile ------------------------------------

        [Fact]
        public void Manifest_HasNoSaveMethod_SoADirectWriteCannotCompile()
        {
            var save = typeof(Manifest).GetMethod(
                "Save",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            Assert.Null(save);
        }

        [Fact]
        public void TheCanonicalPath_StillDoesTheAtomicWriteItself()
        {
            // Guards every scan below: if this file stopped doing the temp-write-then-rename,
            // "nobody writes manifest.json directly" would be true and meaningless.
            string store = RepoSource.Read(StorePath);

            Assert.Contains("FileMode.CreateNew", store, StringComparison.Ordinal);
            Assert.Contains("Flush(flushToDisk: true)", store, StringComparison.Ordinal);
            Assert.Contains("File.Move(temp, path, overwrite: true)", store, StringComparison.Ordinal);
        }

        // ---- layer 2: every call site, and its operation ----------------------

        [Fact]
        public void EveryManifestWriteInTheSource_IsAPinnedCallSite()
        {
            var found = CountWriters(ProductionSources(), CodeOf);

            // Compared as text so a failure shows exactly which file's operation counts moved. Every
            // call site and its operation is pinned on purpose: adding one is a decision someone
            // records here rather than a line nobody notices.
            Assert.Equal(Describe(Expected()), Describe(found));
        }

        [Fact]
        public void TheWriterCount_IsTheOneStatedInTheIssue()
        {
            var found = CountWriters(ProductionSources(), CodeOf);

            Assert.Equal(14, found.Count);                                  // files
            Assert.Equal(13, found.Values.Sum(v => v.Updates));             // read-modify-write
            Assert.Equal(9, found.Values.Sum(v => v.Replaces));             // whole-content
            Assert.Equal(22, found.Values.Sum(v => v.Updates + v.Replaces));
        }

        [Fact]
        public void TheCallSiteGuard_FailsOnAnExtraCallInAnAlreadyListedFile()
        {
            // The negative control. This is the case the previous file-level guard could not see: a
            // second, stale, whole-content write added to a file that is already a known writer.
            const string target = "src/AgentEyes.Core/Thumbnails.cs";
            Assert.Contains(target, Expected().Keys);

            string Injected(string file) => file == target
                ? CodeOf(file) + Environment.NewLine + "ManifestStore.Replace(dir, stale);"
                : CodeOf(file);

            var found = CountWriters(ProductionSources(), Injected);

            Assert.Equal((1, 1), found[target]);        // the extra Replace IS seen
            Assert.NotEqual(Expected()[target], found[target]);
            Assert.NotEqual(Describe(Expected()), Describe(found));
        }

        [Fact]
        public void TheCallSiteGuard_FailsOnAWriterInAFileThatWasNeverListed()
        {
            const string newcomer = "src/AgentEyes.Core/RecordingPaths.cs";
            Assert.DoesNotContain(newcomer, Expected().Keys);

            string Injected(string file) => file == newcomer
                ? CodeOf(file) + Environment.NewLine + "ManifestStore.Update(dir, m => { });"
                : CodeOf(file);

            var found = CountWriters(ProductionSources(), Injected);

            Assert.True(found.ContainsKey(newcomer));
            Assert.NotEqual(Describe(Expected()), Describe(found));
        }

        [Fact]
        public void EveryPinnedWriter_StillExistsAndStillWritesThroughTheCanonicalPath()
        {
            foreach (var writer in ExpectedWriters)
            {
                string code = CodeOf(writer.File);   // RepoSource.Read throws if the file was renamed away
                Assert.True(
                    Regex.IsMatch(code, @"ManifestStore\.(Update|Replace)\s*\("),
                    $"{writer.File} no longer writes through ManifestStore, but is pinned as: {writer.Why}");
            }
        }

        // ---- layer 3: the chokepoint - who may even NAME the file -------------

        [Fact]
        public void OnlyPinnedFiles_NameManifestJsonInCode()
        {
            // A narrowing device, NOT the alias/helper answer - a helper in a file that never names
            // the manifest is invisible here, which is precisely how this layer was defeated in round
            // 2 and why ManifestWriterIlTests exists. What this still buys is a short, reviewed list
            // of the files that deal with the manifest at all. Comments are stripped so prose about
            // the manifest costs nothing.
            var found = ProductionSources()
                .Where(f => NamesTheManifest(CodeOf(f)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var expected = ManifestNameUsers.Select(u => u.File).OrderBy(f => f, StringComparer.Ordinal).ToList();

            Assert.Equal(expected, found);
        }

        [Fact]
        public void OfThoseFiles_OnlyThePinnedFewAlsoContainAFileWriteApi()
        {
            var found = ManifestNameUsers.Select(u => u.File)
                .Where(f => !string.Equals(f, StorePath, StringComparison.OrdinalIgnoreCase))
                .Where(f => WriteApis.Any(api => CodeOf(f).Contains(api, StringComparison.Ordinal)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var expected = WritersOfOtherFiles.Select(w => w.File).OrderBy(f => f, StringComparer.Ordinal).ToList();

            Assert.Equal(expected, found);
        }

        [Fact]
        public void NoStatementAnywhere_WritesTheManifestFileDirectly()
        {
            var offenders = ProductionSources()
                .Where(f => !string.Equals(f, StorePath, StringComparison.OrdinalIgnoreCase))
                .SelectMany(f => DirectManifestWrites(f, CodeOf(f)))
                .ToList();

            Assert.Empty(offenders);
        }

        [Fact]
        public void TheDirectWriteScan_SeesAWriteWhenThereIsOne()
        {
            // The negative control for the statement scan: an injected direct write must be reported,
            // or "no offenders" means nothing.
            var offenders = DirectManifestWrites(
                "injected.cs",
                "File.WriteAllText(Path.Combine(dir, \"manifest.json\"), json);").ToList();

            Assert.Single(offenders);
        }

        [Fact]
        public void NoSourceFileReachesTheManifestTypeThroughReflection()
        {
            // Named limit, not a claim of completeness: this is the ONE reflection shape a source scan
            // can see - reflection that names the Manifest TYPE. It is not the reflection shape that
            // matters most. A writer that reflects over System.IO.File instead
            // (typeof(File).GetMethod("WriteAllText").Invoke(...)) never mentions Manifest at all and
            // is invisible here, and invisible to the IL guard too, which has no token to match.
            //
            // Nor does Layer 0 close it, and this comment used to imply it did: the missing Save
            // method and the internal JsonOptions are COMPILE-TIME barriers, and a Manifest is an
            // ordinary public type that JsonSerializer will serialize with default options. Removing
            // Save narrows the convenient route; it does not close the reflective one. Reflection is
            // an open limit of criterion 4 - see ManifestWriterIlTests, where it is listed under WHAT
            // IT DOES NOT CLAIM and pinned by a committed control.
            var offenders = ProductionSources()
                .Where(f => !string.Equals(f, StorePath, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(f, ManifestPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => Regex.IsMatch(CodeOf(f), @"typeof\(\s*Manifest\s*\)|GetType\(\s*""[^""]*Manifest"))
                .ToList();

            Assert.Empty(offenders);
        }

        [Fact]
        public void NothingButTheManifestAndItsStore_SerializesAManifest()
        {
            // The other way around the missing Save method: serialize the object yourself and write
            // the bytes. Manifest.JsonOptions is internal to make that visible when it happens.
            var offenders = ProductionSources()
                .Where(f => !string.Equals(f, StorePath, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(f, ManifestPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => CodeOf(f).Contains("Manifest.JsonOptions", StringComparison.Ordinal))
                .ToList();

            Assert.Empty(offenders);
        }

        // ---- scanning helpers ------------------------------------------------

        private static bool NamesTheManifest(string code) =>
            code.Contains("manifest.json", StringComparison.OrdinalIgnoreCase)
            || code.Contains("ManifestStore.FileName", StringComparison.Ordinal);

        /// <summary>Statements in <paramref name="code"/> that both name the manifest and call a
        /// file-write API.</summary>
        private static IEnumerable<string> DirectManifestWrites(string file, string code)
        {
            foreach (string statement in code.Split(';'))
            {
                if (!NamesTheManifest(statement)) continue;
                if (!WriteApis.Any(api => statement.Contains(api, StringComparison.Ordinal))) continue;
                yield return $"{file}: {statement.Trim().Replace("\r", " ").Replace("\n", " ")}";
            }
        }
    }
}
