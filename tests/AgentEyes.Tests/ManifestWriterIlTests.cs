using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// NEGATIVE CONTROL for issue #155, criterion 4 - bypass shape (a), exactly as the QA Agent wrote
    /// it in round 2: the manifest path is aliased into a local in ONE statement and written in the
    /// NEXT, through a different serializer. Any scan that requires the file name and the write API to
    /// appear in the same statement - which is what the round-2 source guard did - reports nothing.
    ///
    /// It is never called. It exists to be COMPILED, so the guard can be run over real IL and shown to
    /// report it. Nothing in the product calls it and it touches no real recording.
    /// </summary>
    internal static class ManifestWriteAttacks
    {
        /// <summary>QA bypass shape (a): aliased path, two statements, different serializer.</summary>
        internal static void AliasedPathInTwoStatements(string dir, Manifest manifest)
        {
            string path = Path.Combine(dir, ManifestStore.FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        }

        /// <summary>QA bypass shape (b): the same write routed through a helper in another file that
        /// never names the manifest.</summary>
        internal static void ThroughAHelperThatNeverNamesTheManifest(string dir, Manifest manifest) =>
            ManifestWriteAttackHelper.WriteText(Path.Combine(dir, ManifestStore.FileName),
                                                JsonSerializer.Serialize(manifest));

        /// <summary>Reviewer bypass shape (c), round 2: a PATH-TAKING FRAMEWORK WRITER. Nothing in
        /// this method calls System.IO, so a System.IO-only inventory reported nothing and all 23
        /// writer tests stayed green. Such entry points are on the enumerated list now
        /// (<see cref="CompiledCode.FileWriteApis"/>) and the control below proves this one is
        /// reported - the list is still enumerated, not exhaustive.</summary>
        internal static void ThroughAPathTakingFrameworkApi(string dir)
        {
            var doc = new XmlDocument();
            doc.AppendChild(doc.CreateElement("manifest"));
            doc.Save(Path.Combine(dir, ManifestStore.FileName));
        }

        /// <summary>Reviewer bypass shape (d), round 2: REFLECTIVE INVOCATION - the documented LIMIT.
        /// The compiled IL of this method carries tokens for System.Type and MethodBase::Invoke and
        /// none at all for File::WriteAllText, so no IL call-site scan can name what it writes. It is
        /// committed so that limit is a checked fact rather than a sentence in a comment.</summary>
        internal static void ThroughReflection(string dir, Manifest manifest) =>
            typeof(File).GetMethod("WriteAllText", new[] { typeof(string), typeof(string) })!
                .Invoke(null, new object[]
                {
                    Path.Combine(dir, ManifestStore.FileName),
                    JsonSerializer.Serialize(manifest),
                });
    }

    /// <summary>
    /// Issue #155, criterion 4, as narrowed after round 2 - and the wording matters, because an
    /// overclaimed guard is what failed that round. This does NOT claim to fail whenever any new
    /// direct writer of manifest.json appears. What it claims is exactly this:
    ///
    ///   it PINS, by the compiled IL of the product, the known file-write call sites and the
    ///   ManifestStore operations, so that any change to either inventory is a deliberate, reviewed
    ///   decision - and it does NOT see reflective invocation, or a path-taking framework API that
    ///   is not on the enumerated list in <see cref="CompiledCode.FileWriteApis"/>.
    ///
    /// WHY THIS REPLACED A SOURCE SCAN. Rounds 1 and 2 of this criterion were text scans, and two
    /// independent reviewers defeated both. The reason is structural, not a matter of a better regex:
    /// source text has unlimited ways to spell one write - split it across statements, alias the path
    /// into a local, hide the call in a one-line helper in a file that never names the manifest, route
    /// it through a const, a different serializer, or a delegate - and a scan can only ever chase the
    /// spellings someone thought of. IL removes THAT freedom: each of those spellings compiles to the
    /// same instruction with the same metadata token, in some method, and the method is what gets
    /// pinned here. Both shapes QA used are committed as negative controls below and are proved to
    /// break this guard. IL does not remove the two freedoms listed under DOES NOT CLAIM.
    ///
    /// WHAT THIS GUARD CLAIMS, exactly:
    ///
    ///  - Every call to a write API on the enumerated list (<see cref="CompiledCode.FileWriteApis"/>:
    ///    all of System.IO's file-write entry points, plus a NAMED set of path-taking framework
    ///    writers) anywhere in AgentEyes.Core or AgentEyesApp is pinned below, BY METHOD AND BY COUNT.
    ///    A new one - in a new file, a new helper, a lambda, a local function, an async body, or a
    ///    second call added to a method that already writes something else - changes this inventory
    ///    and fails.
    ///  - Every call to <c>ManifestStore.Update</c> / <c>ManifestStore.Replace</c> is pinned the same
    ///    way, so the canonical path's own call sites are a recorded decision.
    ///  - The product makes no indirect (<c>calli</c>) calls, so no DIRECT call target in it is
    ///    unnameable.
    ///  - Every native (P/Invoke) import is pinned, so a write through CreateFileW/WriteFile cannot
    ///    slip in under a scan that only knows System.IO.
    ///
    /// WHAT IT DOES NOT CLAIM - stated because a guard believed to cover more than it does is worse
    /// than no guard, and claiming coverage this guard did not have is what failed round 2. The first
    /// two are the ones a reviewer demonstrated; they are accepted, documented LIMITS, not oversights:
    ///
    ///  - REFLECTIVE INVOCATION IS NOT SEEN. <c>typeof(File).GetMethod("WriteAllText").Invoke(...)</c>
    ///    compiles to calls on System.Type / System.Reflection.MethodInfo; no token in that IL names
    ///    the write API, so nothing here reports it. (<c>ldtoken</c> of a method IS decoded, so the
    ///    narrow <c>typeof(X).GetMethod</c>-with-a-method-handle shape can surface - but the ordinary
    ///    name-based lookup does not, and neither does a type name built at runtime.) The source-side
    ///    check in <see cref="ManifestWriterTests"/> sees only reflection through the
    ///    <see cref="Manifest"/> TYPE, which is a different and equally partial view - and reflection
    ///    over <c>System.IO.File</c> never mentions <c>Manifest</c> at all. Layer 0 does not close it
    ///    either: it is a COMPILE-TIME barrier, and reflection is precisely the thing that does not
    ///    compile through it. NOTHING IN THIS REPOSITORY CLOSES REFLECTION. What stands against it is
    ///    human review of the short inventory below - a person, not a test.
    ///  - A PATH-TAKING FRAMEWORK API THAT IS NOT ON THE ENUMERATED LIST IS NOT SEEN. The reviewer's
    ///    <c>XmlDocument.Save(manifestPath)</c> is now on it, together with the other XML, image and
    ///    zip entry points of that shape - but that list is ENUMERATED, not exhaustive. The framework
    ///    has an open-ended number of methods that take a path and write it, and one that is not
    ///    listed produces no System.IO instruction in the caller's own IL and is invisible here.
    ///  - It does not know WHICH file a pinned write targets. Passing the manifest's path to a write
    ///    call site that is ALREADY pinned (for instance handing Package's walkthrough writer a
    ///    manifest path) changes no instruction and would not be reported. What stands against that is
    ///    the small, reviewed inventory below: each entry names the file it writes, so the review
    ///    question "is this still writing what it says?" has a fixed, short list to ask it of.
    ///  - It does not follow data flow, so it cannot say a path variable holds "manifest.json".
    ///  - It covers AgentEyes.Core and AgentEyesApp. Other assemblies (the setup engine, plugins) do
    ///    not write manifests and are out of scope; a manifest writer added there would not be seen.
    ///  - Writes made by an out-of-process tool the app launches (ffmpeg) are outside any static scan.
    ///
    /// Layer 0, behind all of this, is still that a direct write does not COMPILE by the ordinary
    /// route: <see cref="Manifest"/> has no Save method and <c>Manifest.JsonOptions</c> is internal.
    /// It is a compile-time barrier only. <see cref="Manifest"/> is an ordinary public type and
    /// <c>JsonSerializer</c> will serialize it with default options, so Layer 0 removes the CONVENIENT
    /// route rather than the possible one, and reflection steps around it entirely.
    /// <see cref="ManifestWriterTests"/> holds that layer and the source-level cross-checks.
    /// </summary>
    public sealed class ManifestWriterIlTests
    {
        /// <summary>
        /// Every file-write call site in the product, by method and count, and what each one writes.
        /// Adding a write means adding a line here - deliberately, because that is the review moment
        /// this criterion exists to create.
        /// </summary>
        private static readonly string[] PinnedFileWrites =
        {
            "AgentEyesApp.dll!AgentEyes.App.App::Log -> System.IO.File::WriteAllText x1",                    // AgentEyes-crash.log
            "AgentEyesApp.dll!AgentEyes.App.BackgroundFileWriter::WriteToDisk -> System.IO.File::WriteAllText x1", // whatever file a background writer owns; today only config.json (issue #33)
            "AgentEyesApp.dll!AgentEyes.App.Config::WriteJson -> System.IO.File::WriteAllText x1",           // the app's config.json - the ONE writer, shared by the blocking save and the background one
            "AgentEyesApp.dll!AgentEyes.App.Plugins::RunOne -> System.IO.File::WriteAllText x1",             // one plugin run's log
            "AgentEyesApp.dll!AgentEyes.App.Plugins::SaveSettings -> System.IO.File::WriteAllText x1",       // one plugin's settings file
            "AgentEyesApp.dll!AgentEyes.App.PresetStore::Save -> System.IO.File::WriteAllText x1",           // presets.json
            "AgentEyesApp.dll!AgentEyes.App.TestPanel::Transcribe -> System.IO.File::Delete x1",             // its own temporary wav
            "AgentEyesApp.dll!AgentEyes.App.TestReport::Save -> System.IO.File::WriteAllText x1",            // the test panel's report
            "agenteyes.dll!AgentEyes.Audio.RnnoiseModel::Ensure -> System.IO.File::Create x1",               // bd.rnnn extracted to a temp
            "agenteyes.dll!AgentEyes.Audio.RnnoiseModel::Ensure -> System.IO.File::Move x1",                 // ...then renamed into place
            "agenteyes.dll!AgentEyes.CaptureService::Delete -> System.IO.File::Delete x1",                   // a capture the user deleted
            "agenteyes.dll!AgentEyes.Commands::Audio -> System.IO.File::Move x1",                            // audio.original.wav backup
            "agenteyes.dll!AgentEyes.DevThrottle.DevThrottleAccount::Clear -> System.IO.File::Delete x1",    // the stored dt_ key
            "agenteyes.dll!AgentEyes.DevThrottle.DevThrottleAccount::Save -> System.IO.File::WriteAllBytes x1", // the DPAPI-protected dt_ key
            "agenteyes.dll!AgentEyes.Log::Write -> System.IO.File::AppendAllText x1",                        // the app log
            "agenteyes.dll!AgentEyes.ManifestStore::WriteAtomic -> System.IO.File::Delete x1",               // THE manifest path: temp cleanup after a failed rename
            "agenteyes.dll!AgentEyes.ManifestStore::WriteAtomic -> System.IO.File::Move x1",                 // THE manifest path: the atomic rename
            "agenteyes.dll!AgentEyes.ManifestStore::WriteAtomic -> System.IO.FileStream::.ctor x1",          // THE manifest path: the flushed temp
            "agenteyes.dll!AgentEyes.OriginalBackup::Preserve -> System.IO.File::Move x1",                   // the .original audio backup
            "agenteyes.dll!AgentEyes.Package::RunAsync -> System.IO.File::WriteAllText x1",                  // walkthrough.html
            "agenteyes.dll!AgentEyes.Package::WriteTranscript -> System.IO.File::WriteAllText x2",           // transcript.json, transcript.<lang>.vtt
            "agenteyes.dll!AgentEyes.Package::WriteTranscript -> System.IO.StreamWriter::.ctor x1",          // transcript.txt
            "agenteyes.dll!AgentEyes.Packaging.ModelStore::EnsureAsync -> System.IO.File::Delete x1",        // a partial Whisper model download
            "agenteyes.dll!AgentEyes.Packaging.ModelStore::EnsureAsync -> System.IO.File::OpenWrite x1",     // the Whisper model download
            "agenteyes.dll!AgentEyes.Plugins.PluginPackage::CopyDir -> System.IO.File::Copy x1",             // installing a plugin's files
            "agenteyes.dll!AgentEyes.Plugins.PluginPackage::InstallZip -> System.IO.Compression.ZipFileExtensions::ExtractToDirectory x1", // a plugin zip unpacked into its folder
            "agenteyes.dll!AgentEyes.Plugins.PluginPackage::Remove -> System.IO.File::Delete x1",            // a removed plugin's settings
            // The HUD live preview (issue #33). Every one of these five is in %LOCALAPPDATA%\AgentEyes\
            // preview and NONE of them is inside a recording directory - which is the property that
            // matters to this inventory: a preview frame is a monitor overwritten ten times a second,
            // and it must never become a file the Library, the repair passes or packaging can find.
            "agenteyes.dll!AgentEyes.Preview.PreviewFrameFile::TryRead -> System.IO.FileStream::.ctor x1",   // READS a published preview frame (FileAccess.Read; the ctor is on the write list, this use is not a write)
            "agenteyes.dll!AgentEyes.Preview.PreviewTap::WriteFrameToDisk -> System.IO.File::Move x1",       // preview\<track>.jpg: the rename that publishes a whole frame (publisher thread only)
            "agenteyes.dll!AgentEyes.Preview.PreviewTap::WriteFrameToDisk -> System.IO.File::WriteAllBytes x1", // preview\<track>.jpg.tmp: the frame, before that rename (publisher thread only)
            "agenteyes.dll!AgentEyes.Preview.PreviewTap::RemoveFrameFile -> System.IO.File::Delete x2",      // the published frame and its temp, when the preview is hidden or the recording ends
            "agenteyes.dll!AgentEyes.Preview.PreviewTap::TryCreateAt -> System.IO.File::Delete x2",            // the previous recording's leftover frame and temp, at the start of a new one
            "agenteyes.dll!AgentEyes.Screenshot::CaptureRect -> System.Drawing.Image::Save x1",              // a screenshot / marker-shot PNG
            "agenteyes.dll!AgentEyes.SelfTest::RunChecks -> System.IO.File::Copy x1",                      // audio.wav of the throwaway self-test recording
            "agenteyes.dll!AgentEyes.SelfTest::WriteReport -> System.IO.File::WriteAllText x1",              // selftest-report.html
            "agenteyes.dll!AgentEyes.Transcription.DictionaryStore::Save -> System.IO.File::WriteAllText x1",// the transcription dictionary
            "agenteyes.dll!AgentEyes.Translator::WriteTranslatedVtt -> System.IO.File::WriteAllText x1",     // transcript.<lang>.vtt
            "agenteyes.dll!AgentEyes.Video.FfmpegCameraRecorder::WriteFfmpegLog -> System.IO.File::WriteAllText x1",   // the camera ffmpeg stderr log (issue #28; the FAILED-open path deliberately writes nothing into the recording directory)
            "agenteyes.dll!AgentEyes.Video.FfmpegRecorder::Start -> System.IO.File::WriteAllText x1",        // the ffmpeg stderr log of a failed start
            "agenteyes.dll!AgentEyes.Video.FfmpegRecorder::Stop -> System.IO.File::WriteAllText x1",         // the ffmpeg stderr log
            "agenteyes.dll!AgentEyes.VideoImport::RunAsync -> System.IO.File::Copy x1",                      // the imported video file
        };

        /// <summary>
        /// Every call site of the canonical manifest path, by method and count. Update is
        /// read-modify-write and Replace is whole-content: Replace is only legitimate where the caller
        /// owns the entire content (a session publishing the record for a directory it just created,
        /// an import, the issue #153 recovery record). Everything that changes SOME fields of an
        /// existing recording must be an Update, or it erases whatever it never read - so the
        /// OPERATION is pinned, not just the site.
        ///
        /// 22 call sites, 13 Update and 9 Replace - the same 22 the QA Agent counted independently in
        /// round 2, now counted from the IL instead of from the source text.
        /// </summary>
        private static readonly string[] PinnedManifestStoreCalls =
        {
            "AgentEyesApp.dll!AgentEyes.App.MainWindow::RenameRecording_Click -> AgentEyes.ManifestStore::Update x1",   // the Library rename sets DisplayName
            "AgentEyesApp.dll!AgentEyes.App.RecordingDetailWindow::CommitRename -> AgentEyes.ManifestStore::Update x1", // the detail-window rename
            "agenteyes.dll!AgentEyes.Commands::Audio -> AgentEyes.ManifestStore::Replace x1",                  // a CLI audio session's own record
            "agenteyes.dll!AgentEyes.Commands::Shot -> AgentEyes.ManifestStore::Replace x1",                   // a CLI screenshot's own record
            "agenteyes.dll!AgentEyes.Commands::Video -> AgentEyes.ManifestStore::Replace x1",                  // a CLI video session's own record
            "agenteyes.dll!AgentEyes.Package::FinalizeManifest -> AgentEyes.ManifestStore::Update x1",         // what packaging produced
            "agenteyes.dll!AgentEyes.Package::PrepareBareVideo -> AgentEyes.ManifestStore::Replace x1",        // a synthesized bare-video manifest
            "agenteyes.dll!AgentEyes.Packaging.TitleBackfill::Apply -> AgentEyes.ManifestStore::Update x1",    // the generated title, and the AI cost ADDED to the total
            "agenteyes.dll!AgentEyes.PostRecordingState::Update -> AgentEyes.ManifestStore::Update x1",        // the issue #152 stage journal
            "agenteyes.dll!AgentEyes.RecordingService::BeginSession -> AgentEyes.ManifestStore::Replace x1",   // the record published at start (issue #155)
            "agenteyes.dll!AgentEyes.RecordingService::FinalizePending -> AgentEyes.ManifestStore::Update x1", // the deferred mux result
            "agenteyes.dll!AgentEyes.RecordingService::Screenshot -> AgentEyes.ManifestStore::Replace x1",     // a one-shot screenshot's own record
            "agenteyes.dll!AgentEyes.RecordingService::Stop -> AgentEyes.ManifestStore::Update x1",            // the stop's own fields
            "agenteyes.dll!AgentEyes.RecoveryManifest::Save -> AgentEyes.ManifestStore::Replace x1",           // the issue #153 reduced last-resort record
            "agenteyes.dll!AgentEyes.SelfTest::RunChecks -> AgentEyes.ManifestStore::Replace x1",              // the throwaway self-test recording
            "agenteyes.dll!AgentEyes.SubtitleBurner::RegisterOutput -> AgentEyes.ManifestStore::Update x1",    // registers the burned-in output file
            "agenteyes.dll!AgentEyes.Thumbnails::NoteThumbAttempt -> AgentEyes.ManifestStore::Update x1",      // the thumbnail attempt counter
            "agenteyes.dll!AgentEyes.TranscriptionBacklog::NoteAttempt -> AgentEyes.ManifestStore::Update x1", // the transcribe attempt counter
            "agenteyes.dll!AgentEyes.TranscriptionBacklog::NoteTitleAttempt -> AgentEyes.ManifestStore::Update x1", // the title attempt stamp
            "agenteyes.dll!AgentEyes.Translator::WriteTranslatedVtt -> AgentEyes.ManifestStore::Update x1",    // a translated language, and the AI cost added to the total
            "agenteyes.dll!AgentEyes.VideoImport::RunAsync -> AgentEyes.ManifestStore::Replace x1",            // the imported recording's new record
            "agenteyes.dll!AgentEyes.VideoImport::WriteArtifacts -> AgentEyes.ManifestStore::Update x1",       // its transcript artifacts
        };

        /// <summary>Every native import in the product. Not one of these is a file API - they are
        /// window, hook and known-folder calls - and a new line here is a review moment for exactly
        /// that question, because CreateFileW/WriteFile would be a manifest writer that no scan of
        /// System.IO can see.</summary>
        private static readonly string[] PinnedNativeImports =
        {
            "dwmapi.dll!DwmSetWindowAttribute",     // dark title bar on the main window
            "kernel32.dll!GetModuleHandle",         // module handle for the keyboard hook
            "shell32.dll!SHGetKnownFolderPath",     // the user's Videos folder
            "user32.dll!CallNextHookEx",            // the low-level keyboard hook chain
            "user32.dll!GetAsyncKeyState",
            "user32.dll!GetWindowLong",             // monitor-highlight overlay style
            "user32.dll!GetWindowLongPtr",          // HUD window style
            "user32.dll!SetWindowDisplayAffinity",  // WDA_EXCLUDEFROMCAPTURE for the HUD
            "user32.dll!SetWindowLong",
            "user32.dll!SetWindowLongPtr",
            "user32.dll!SetWindowsHookEx",
            "user32.dll!UnhookWindowsHookEx",
        };

        private static string Pinned(IEnumerable<string> lines) =>
            string.Join(Environment.NewLine, lines.OrderBy(l => l, StringComparer.Ordinal));

        private static void AssertSameBlock(string expected, string actual, string what)
        {
            Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
                $"{what}{Environment.NewLine}{Environment.NewLine}"
                + $"PINNED:{Environment.NewLine}{expected}{Environment.NewLine}{Environment.NewLine}"
                + $"FOUND:{Environment.NewLine}{actual}");
        }

        // ---- the guard --------------------------------------------------------

        [Fact]
        public void EveryFileWriteInTheProduct_IsAPinnedCallSite()
        {
            string found = CompiledCode.Describe(CompiledCode.FileWrites(CompiledCode.ProductAssemblies()));

            AssertSameBlock(Pinned(PinnedFileWrites), found,
                "A file-write call site in AgentEyes.Core or AgentEyesApp is not the pinned set (issue #155, criterion 4). "
                + "If the new write is a manifest write, it belongs in ManifestStore. If it is not, add it below with the "
                + "file it writes.");
        }

        [Fact]
        public void EveryCallOfTheCanonicalManifestPath_IsPinned()
        {
            string found = CompiledCode.Describe(CompiledCode.CallSites(CompiledCode.CoreAssembly, IsManifestStore)
                .Concat(CompiledCode.CallSites(CompiledCode.AppAssembly, IsManifestStore)));

            AssertSameBlock(Pinned(PinnedManifestStoreCalls), found,
                "A ManifestStore.Update/Replace call site moved (issue #155, criterion 4). Update is read-modify-write and "
                + "Replace is whole-content: picking the wrong one erases fields nobody read, so both the site and the "
                + "operation are pinned.");
        }

        [Fact]
        public void TheProductMakesNoIndirectCalls()
        {
            // calli carries a signature rather than a target, so its callee cannot be named by any
            // static scan. The inventory above is only honest while this is zero.
            foreach (string assembly in CompiledCode.ProductAssemblies())
                Assert.Equal(0, CompiledCode.IndirectCalls(assembly));
        }

        [Fact]
        public void EveryNativeImport_IsPinned()
        {
            string found = Pinned(CompiledCode.ProductAssemblies().SelectMany(CompiledCode.NativeImports).Distinct());

            AssertSameBlock(Pinned(PinnedNativeImports), found,
                "A native (P/Invoke) import appeared or moved. Confirm it cannot write a file - CreateFileW, WriteFile, "
                + "MoveFileEx and friends would be manifest writers that no System.IO scan can see - then record it below.");
        }

        [Fact]
        public void OnlyTheCanonicalPath_WritesTheManifestInsideItsOwnAssembly()
        {
            // The inventory above is the whole product; this narrows it to the one claim the issue is
            // about, so a failure here reads as "something outside ManifestStore now writes files in
            // the manifest's own class" rather than as a diff.
            var inManifestStore = CompiledCode.FileWrites(new[] { CompiledCode.CoreAssembly })
                .Where(s => s.Method.StartsWith("AgentEyes.ManifestStore::", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(inManifestStore);                                        // the canonical write still exists
            Assert.All(inManifestStore, s => Assert.Equal("AgentEyes.ManifestStore::WriteAtomic", s.Method));
        }

        [Fact]
        public void TheAssembliesOutsideTheScan_CannotReachTheManifestAtAll()
        {
            // The guard covers AgentEyes.Core and AgentEyesApp. This is why that scope is a fact and
            // not a hope: the installer engine does not reference the assembly the Manifest and
            // ManifestStore types live in, so it cannot construct, serialize or write one. (The setup
            // wizard and setup CLI reference only this engine, so neither reaches Core either. Its own
            // "release-manifest.json" is a different file with a different shape.)
            string engine = Path.Combine(AppContext.BaseDirectory, "AgentEyes.Setup.Engine.dll");
            var engineRefs = CompiledCode.AssemblyReferences(engine);

            // Positive control first: the check demonstrably SEES this reference when there is one,
            // so "not in the list" is a finding rather than a broken instrument.
            Assert.Contains("agenteyes", CompiledCode.AssemblyReferences(CompiledCode.AppAssembly),
                            StringComparer.OrdinalIgnoreCase);

            Assert.DoesNotContain("agenteyes", engineRefs, StringComparer.OrdinalIgnoreCase);
        }

        // ---- negative controls: the two shapes QA used to defeat round 2 -------

        [Fact]
        public void TheGuard_ReportsQaBypassShapeA_AliasedPathInTwoStatements()
        {
            // Shape (a) compiled for real. The round-2 source guard split statements on ';' and needed
            // the manifest name and the write API in the SAME statement, so it saw nothing here.
            var found = AttackSites()
                .Where(s => s.Method == "AgentEyes.Tests.ManifestWriteAttacks::AliasedPathInTwoStatements")
                .ToList();

            Assert.Equal(new[] { "System.IO.File::WriteAllText" }, found.Select(s => s.Callee).ToArray());
        }

        [Fact]
        public void TheGuard_ReportsQaBypassShapeB_AHelperInAFileThatNeverNamesTheManifest()
        {
            // Shape (b) compiled for real. The helper lives in ManifestWriteAttackHelper.cs, which
            // never names manifest.json - invisible to a source scan, plainly visible in IL.
            var found = AttackSites()
                .Where(s => s.Method == "AgentEyes.Tests.ManifestWriteAttackHelper::WriteText")
                .ToList();

            Assert.Equal(new[] { "System.IO.File::WriteAllText" }, found.Select(s => s.Callee).ToArray());
        }

        [Fact]
        public void TheGuard_ReportsReviewerBypassShapeC_APathTakingFrameworkWriter()
        {
            // Shape (c) compiled for real: XmlDocument.Save(manifestPath). It defeated round 2
            // because it makes no System.IO call at all. It is on the enumerated list now.
            var found = CompiledCode.CallSites(CompiledCode.TestAssembly, CompiledCode.IsFileWriteApi)
                .Where(s => s.Method == "AgentEyes.Tests.ManifestWriteAttacks::ThroughAPathTakingFrameworkApi")
                .ToList();

            Assert.Equal(new[] { "System.Xml.XmlDocument::Save" }, found.Select(s => s.Callee).ToArray());
        }

        [Fact]
        public void TheGuard_DoesNotReportReflectiveInvocation_AndThatLimitIsChecked()
        {
            // NOT a pass - a LIMIT, checked. Shape (d) compiled for real: the IL of a reflective
            // invocation names System.Type and MethodBase::Invoke and never names the write API, so
            // this guard cannot report it and does not claim to (see the summary above). Asserting
            // the absence keeps the claim honest in BOTH directions: if a future change ever did make
            // this visible, this test fails and the "does not claim" list gets shorter.
            var writes = CompiledCode.CallSites(CompiledCode.TestAssembly, CompiledCode.IsFileWriteApi)
                .Where(s => s.Method == "AgentEyes.Tests.ManifestWriteAttacks::ThroughReflection")
                .ToList();

            Assert.Empty(writes);

            // The instrument check that makes that emptiness mean something: the method IS compiled
            // and the walker DOES walk it. Without this, "no write found" would also be what a scan
            // that never looked at the method reports.
            var anyCall = CompiledCode.CallSites(CompiledCode.TestAssembly, _ => true)
                .Where(s => s.Method == "AgentEyes.Tests.ManifestWriteAttacks::ThroughReflection")
                .ToList();

            Assert.NotEmpty(anyCall);
            Assert.Contains(anyCall, s => s.Callee.StartsWith("System.Reflection.", StringComparison.Ordinal)
                                       || s.Callee.StartsWith("System.Type::", StringComparison.Ordinal));
        }

        [Fact]
        public void EitherBypassShape_PlacedInTheProduct_BreaksThePinnedInventory()
        {
            // Detection is not the claim - FAILING is. Each real, compiled attack call site is
            // relabelled onto the product and merged into the real product inventory; the pinned
            // comparison must then differ. Three placements, including the one QA never tried.
            var product = CompiledCode.FileWrites(CompiledCode.ProductAssemblies());
            string pinned = Pinned(PinnedFileWrites);
            var existingWriter = AnExistingWriteCallSite(product);

            var placements = new[]
            {
                // (a) a new method inside a file that is already a manifest writer
                ("agenteyes.dll", "AgentEyes.Package::QaAliasWrite"),
                // (b) a helper in a new file that never names the manifest
                ("agenteyes.dll", "AgentEyes.QaAttackHelper::WriteText"),
                // and the harder one QA never tried: a SECOND write added to a method that already
                // writes a file, where no new name appears anywhere - only a count moves
                (existingWriter.Assembly, existingWriter.Method),
            };

            foreach (var (assembly, method) in placements)
            {
                foreach (var attack in AttackSites())
                {
                    var withAttack = product.Concat(new[] { attack with { Assembly = assembly, Method = method } });

                    Assert.NotEqual(pinned, CompiledCode.Describe(withAttack));
                }
            }
        }

        [Fact]
        public void TheIlWalker_ActuallyWalksTheProduct()
        {
            // The scan's own instrument check. Every assertion above is "the inventory matches", which
            // a walker that read nothing would satisfy against an empty pin. It cannot: the walker
            // throws on an unknown opcode or a lost boundary, and the product demonstrably contains
            // both file writes and manifest writes.
            Assert.NotEmpty(CompiledCode.FileWrites(new[] { CompiledCode.CoreAssembly }));
            Assert.NotEmpty(CompiledCode.FileWrites(new[] { CompiledCode.AppAssembly }));
            Assert.NotEmpty(CompiledCode.CallSites(CompiledCode.CoreAssembly, IsManifestStore));
            Assert.NotEmpty(CompiledCode.CallSites(CompiledCode.AppAssembly, IsManifestStore));
        }

        // ---- helpers ----------------------------------------------------------

        private static bool IsManifestStore(string callee) =>
            callee == "AgentEyes.ManifestStore::Update" || callee == "AgentEyes.ManifestStore::Replace";

        /// <summary>The real, compiled call sites of the two System.IO bypass shapes, read out of the
        /// test assembly's own IL by the same scanner that guards the product. Shape (c) is a
        /// framework writer and shape (d) is reflective, so both are asserted on their own above
        /// rather than being relabelled into the product's System.IO inventory here.</summary>
        private static IReadOnlyList<CompiledCode.CallSite> AttackSites()
        {
            var sites = CompiledCode.CallSites(CompiledCode.TestAssembly, CompiledCode.IsFileWriteApi)
                .Where(s => s.Method.StartsWith("AgentEyes.Tests.ManifestWriteAttack", StringComparison.Ordinal)
                         && s.Callee == "System.IO.File::WriteAllText")
                .ToList();

            Assert.Equal(2, sites.Count);   // shape (a) and shape (b); neither may quietly disappear
            return sites;
        }

        /// <summary>A call site that is ALREADY a pinned file writer, used to place an extra write
        /// inside an existing one - the case a per-file or per-name guard cannot see at all, because
        /// nothing new is named anywhere.</summary>
        private static CompiledCode.CallSite AnExistingWriteCallSite(IEnumerable<CompiledCode.CallSite> product)
        {
            var site = product
                .Where(s => s.Callee == "System.IO.File::WriteAllText")
                .OrderBy(s => $"{s.Assembly}!{s.Method}", StringComparer.Ordinal)
                .FirstOrDefault();

            Assert.NotNull(site);
            return site!;
        }
    }
}
