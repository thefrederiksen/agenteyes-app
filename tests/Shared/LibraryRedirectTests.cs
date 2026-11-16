using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentEyes.Headless;
using Xunit;

namespace AgentEyes.Tests.Shared
{
    /// <summary>
    /// Issue #161: AGENTEYES_REDIRECT_DATA / AGENTEYES_REDIRECT_VIDEOS point a REAL process at a
    /// scratch library. Shared by all three suites (Windows, Linux, macOS), so each platform's build of
    /// the engine is held to the same rules by its own gate.
    ///
    /// What these tests prove and what they do not:
    ///  - The rules (<see cref="LibraryRedirect.Decide"/>) are tested as a pure function, because the
    ///    test host is already redirected in-process and a process gets one redirect.
    ///  - The wiring is tested on a REAL child process: the built agenteyes.dll is started with the
    ///    variables set, and its exit code, stderr and the files it wrote are read back. Every child
    ///    is started with BOTH variables pointing into this run's scratch folder, or with a partial
    ///    set that must be refused before anything is touched - never with neither, which would run it
    ///    on the machine's real library.
    ///  - /status is read from a real HeadlessControlServer over loopback. In this host the redirect
    ///    is the TEST HOST's, so it reports "in-process (test host)"; the "environment" source is
    ///    asserted on the child process's log, and on a real service in the proof.
    /// </summary>
    // Issue #161 pass 2: one xunit collection for every class in a suite that starts a loopback
    // HeadlessControlServer. Each picks a "free" port by probing and releasing it, and two classes running in
    // parallel can be handed the same one; the managed HttpListener then shares that port between two
    // servers, and the second Dispose fails with "Address already in use" (CI 43925309084 and 43928220679,
    // HeadlessControlServerFramingTests). A collection runs its classes one at a time.
    [Collection(LoopbackServerCollection.Name)]
    public sealed class LibraryRedirectTests
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "agenteyes-161-tests", $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");

        // Absolute paths that look the same shape on every platform.
        private static string Abs(params string[] parts) =>
            Path.Combine(new[] { RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\" : "/" }.Concat(parts).ToArray());

        private static readonly string RealLocal = Abs("home", "me", "local");
        private static readonly string RealVideos = Abs("home", "me", "Movies");

        private static LibraryRedirect.Decision Decide(string? data, string? videos, params string[] extraNames)
        {
            var names = new List<string> { "PATH", "HOME", "AGENTEYES_ROOT" };
            if (data != null) names.Add(LibraryRedirect.DataVariable);
            if (videos != null) names.Add(LibraryRedirect.VideosVariable);
            names.AddRange(extraNames);
            return LibraryRedirect.Decide(data, videos, names, RealLocal, RealVideos, namesAreCaseInsensitive: false);
        }

        // ---- the rules ------------------------------------------------------------------------

        [Fact]
        public void Decide_NeitherVariableSet_NotRequested()
        {
            var d = Decide(null, null);

            Assert.False(d.Redirect);
            Assert.False(d.Refused);
        }

        [Fact]
        public void Decide_BothAbsoluteScratchFolders_RedirectsToThem()
        {
            var d = Decide(Abs("tmp", "s", "data"), Abs("tmp", "s", "videos"));

            Assert.True(d.Redirect);
            Assert.False(d.Refused);
            Assert.Equal(Abs("tmp", "s", "data"), d.Data);
            Assert.Equal(Abs("tmp", "s", "videos"), d.Videos);
        }

        [Fact]
        public void Decide_OnlyDataSet_RefusedNamingTheMissingVideosVariable()
        {
            var d = Decide(Abs("tmp", "s", "data"), null);

            Assert.True(d.Refused);
            Assert.False(d.Redirect);
            Assert.Contains("partial library redirect", d.Refusal);
            Assert.Contains(LibraryRedirect.VideosVariable + " is not", d.Refusal);
        }

        [Fact]
        public void Decide_OnlyVideosSet_RefusedNamingTheMissingDataVariable()
        {
            var d = Decide(null, Abs("tmp", "s", "videos"));

            Assert.True(d.Refused);
            Assert.Contains("partial library redirect", d.Refusal);
            Assert.Contains(LibraryRedirect.DataVariable + " is not", d.Refusal);
        }

        [Fact]
        public void Decide_EmptyValues_AreUnsetSoOneEmptyIsStillPartial()
        {
            Assert.False(Decide("", "").Refused);
            Assert.False(Decide("", "").Redirect);

            var d = Decide(Abs("tmp", "s", "data"), "");
            Assert.True(d.Refused);
            Assert.Contains("partial library redirect", d.Refusal);
        }

        [Fact]
        public void Decide_RelativePath_Refused()
        {
            var d = Decide("scratch/data", Abs("tmp", "s", "videos"));

            Assert.True(d.Refused);
            Assert.Contains(LibraryRedirect.DataVariable + " must be an absolute path", d.Refusal);
        }

        [Fact]
        public void Decide_ATypoOfARedirectVariable_RefusedEvenWithNeitherRealOneSet()
        {
            // The case a "both or neither" rule alone would miss: BOTH names misspelt, so neither real
            // variable is set, and the process would run on the real library while the person believes
            // it is redirected.
            var d = Decide(null, null, "AGENTEYES_REDIRECT_RECORDINGS", "AGENTEYES_REDIRECT_STATE");

            Assert.True(d.Refused);
            Assert.Contains("AGENTEYES_REDIRECT_RECORDINGS, AGENTEYES_REDIRECT_STATE", d.Refusal);
        }

        [Fact]
        public void Decide_LowerCaseName_UnknownWhereNamesAreCaseSensitive_AcceptedWhereTheyAreNot()
        {
            var names = new[] { "agenteyes_redirect_data", "agenteyes_redirect_videos" };

            var unix = LibraryRedirect.Decide(null, null, names, RealLocal, RealVideos, namesAreCaseInsensitive: false);
            var windows = LibraryRedirect.Decide(Abs("tmp", "d"), Abs("tmp", "v"), names, RealLocal, RealVideos, namesAreCaseInsensitive: true);

            Assert.True(unix.Refused);
            Assert.True(windows.Redirect);
        }

        [Fact]
        public void Decide_InstallerRootVariable_IsNotARedirectVariable()
        {
            // AGENTEYES_ROOT is the installer's, and must neither trigger nor block a redirect.
            var d = LibraryRedirect.Decide(null, null, new[] { "AGENTEYES_ROOT" }, RealLocal, RealVideos, false);

            Assert.False(d.Refused);
            Assert.False(d.Redirect);
        }

        [Fact]
        public void Decide_BothVariablesTheSameFolder_RefusedBecauseTheRootsWouldBeOne()
        {
            var d = Decide(Abs("tmp", "s"), Abs("tmp", "s"));

            Assert.True(d.Refused);
            Assert.Contains("overlap", d.Refusal);
        }

        [Fact]
        public void Decide_VideosIsTheRealVideosFolder_RefusedAsAMixWithTheRealLibrary()
        {
            // A scratch state root with the REAL recordings root - the partial redirect in disguise.
            var d = Decide(Abs("tmp", "s", "data"), RealVideos);

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
        }

        [Fact]
        public void Decide_DataIsTheRealVideosFolder_RefusedBecauseStateWouldLandInTheRealLibrary()
        {
            var d = Decide(RealVideos, Abs("tmp", "s", "videos"));

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
        }

        [Fact]
        public void Decide_DataInsideTheRealStateRoot_Refused()
        {
            var d = Decide(Path.Combine(RealLocal, "AgentEyes", "scratch"), Abs("tmp", "s", "videos"));

            Assert.True(d.Refused);
            Assert.Contains("the REAL state root", d.Refusal);
        }

        [Fact]
        public void Decide_RedirectedRootContainsTheRealRoot_Refused()
        {
            // The real Videos folder sits under a folder called AgentEyes, so a redirect to its
            // grandparent would put the redirected recordings root AROUND the real one.
            string realVideos = Abs("x", "AgentEyes", "v");
            var d = LibraryRedirect.Decide(Abs("tmp", "d"), Abs("x"),
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable }, RealLocal, realVideos, false);

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
        }

        [Fact]
        public void Decide_MachineHasNoVideosFolder_RedirectsWithoutComparingAgainstARelativePath()
        {
            // Issue #169: with no Videos folder .NET answers "", and the unredirected recordings root is
            // the RELATIVE path "AgentEyes". There is no fixed real place to compare against, and
            // comparing would resolve it against the working directory - so the comparison is skipped.
            var d = LibraryRedirect.Decide(Abs("tmp", "d"), Abs("tmp", "v"),
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable }, RealLocal, "", false);

            Assert.True(d.Redirect);
        }

        [Theory]
        [InlineData(new[] { "s", "AgentEyes" }, new[] { "s", "AgentEyes" }, true)]
        [InlineData(new[] { "s", "AgentEyes" }, new[] { "s", "AgentEyes", "x" }, true)]
        [InlineData(new[] { "s", "AgentEyes", "x" }, new[] { "s", "AgentEyes" }, true)]
        [InlineData(new[] { "s", "AgentEyes" }, new[] { "s", "AgentEyesX" }, false)]
        [InlineData(new[] { "s", "data", "AgentEyes" }, new[] { "s", "videos", "AgentEyes" }, false)]
        [InlineData(new[] { "s", "AGENTEYES" }, new[] { "s", "AgentEyes" }, true)]
        public void Overlaps_TwoFolders_TrueOnlyForSameOrNested(string[] a, string[] b, bool expected)
        {
            Assert.Equal(expected, LibraryRedirect.Overlaps(Abs(a), Abs(b)));
        }

        // ---- symbolic links ------------------------------------------------------------------

        [Fact]
        public void Decide_ScratchRootResolvesIntoTheRealLibrary_RefusedNamingTheResolution()
        {
            // The resolver says the scratch recordings root is really the real one (a link).
            string scratchRec = Path.Combine(Abs("tmp", "s", "videos"), "AgentEyes");
            string realRec = Path.Combine(RealVideos, "AgentEyes");
            var d = LibraryRedirect.Decide(Abs("tmp", "s", "data"), Abs("tmp", "s", "videos"),
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable }, RealLocal, RealVideos, false,
                p => p == scratchRec ? realRec : p);

            Assert.True(d.Refused);
            Assert.Contains($"{scratchRec} (which resolves to {realRec})", d.Refusal);
            Assert.Contains("the REAL recordings root", d.Refusal);
        }

        [UnixOnlyFact]
        public void ResolveLinks_ALinkAlongThePath_IsFollowedAndTheMissingTailKept()
        {
            string t = Path.Combine(Scratch, "links1");
            Directory.CreateDirectory(Path.Combine(t, "real"));
            Directory.CreateSymbolicLink(Path.Combine(t, "link"), Path.Combine(t, "real"));

            string resolved = LibraryRedirect.ResolveLinks(Path.Combine(t, "link", "not", "yet"));

            Assert.Equal(Path.Combine(LibraryRedirect.ResolveLinks(Path.Combine(t, "real")), "not", "yet"), resolved);
            Assert.DoesNotContain(Path.DirectorySeparatorChar + "link" + Path.DirectorySeparatorChar, resolved);
        }

        [UnixOnlyFact]
        public void ResolveLinks_ARelativeLink_IsResolvedAgainstItsOwnFolder()
        {
            string t = Path.Combine(Scratch, "links2");
            Directory.CreateDirectory(Path.Combine(t, "real"));
            File.CreateSymbolicLink(Path.Combine(t, "rel"), "real");   // relative target

            Assert.Equal(LibraryRedirect.ResolveLinks(Path.Combine(t, "real")), LibraryRedirect.ResolveLinks(Path.Combine(t, "rel")));
        }

        [UnixOnlyFact]
        public void Decide_WithRealLinks_ScratchAgentEyesLinkedToTheRealRecordingsRoot_Refused()
        {
            // On disk: a stand-in "real" Movies folder with its AgentEyes library, and a scratch videos
            // folder whose AgentEyes entry is a LINK to that library. As written the paths differ; as
            // resolved they are the same folder.
            string t = Path.Combine(Scratch, "links3");
            string realMovies = Path.Combine(t, "Movies");
            Directory.CreateDirectory(Path.Combine(realMovies, "AgentEyes"));
            Directory.CreateDirectory(Path.Combine(t, "scratch", "videos"));
            Directory.CreateSymbolicLink(Path.Combine(t, "scratch", "videos", "AgentEyes"), Path.Combine(realMovies, "AgentEyes"));

            var d = LibraryRedirect.Decide(Path.Combine(t, "scratch", "data"), Path.Combine(t, "scratch", "videos"),
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                Path.Combine(t, "local"), realMovies, false, LibraryRedirect.ResolveLinks);

            Assert.True(d.Refused, "a scratch root linked to the real library was accepted");
            Assert.Contains("which resolves to", d.Refusal);
            Assert.Contains("the REAL recordings root", d.Refusal);
        }

        // ---- pass 2: the real roots come from the ACCOUNT, not from HOME -----------------------
        //
        // QA's defect on pass 1: HOME=<scratch> made .NET (on Linux) name <scratch>/.local/share as the
        // "real" state root, so AGENTEYES_REDIRECT_DATA=<the owner's real>/.local/share was ACCEPTED and
        // the process opened the owner's credential, ffmpeg registry and live log. These pin the rule
        // that a spoofed environment cannot remove the account's library from what is protected.

        private static readonly string AccountHome = Abs("home", "me");
        private static readonly string SpoofedHome = Abs("tmp", "scratch", "home");

        private static string? NoFile(string _) => null;

        [Fact]
        public void Derive_SpoofedHome_StillProtectsTheAccountHomeLibrary()
        {
            // The process's environment says HOME is a scratch folder; the passwd entry says /home/me.
            var roots = RealLibrary.Derive(AccountHome, 1000, isMac: false, NoFile,
                Path.Combine(SpoofedHome, ".local", "share"), Path.Combine(SpoofedHome, "Videos"));

            Assert.Contains(roots, r => r.Name == RealLibrary.StateName && r.Parent == Path.Combine(AccountHome, ".local", "share")
                                        && r.DerivedFrom.Contains("passwd entry for uid 1000"));
            Assert.Contains(roots, r => r.Name == RealLibrary.RecordingsName && r.Parent == Path.Combine(AccountHome, "Videos"));
            // The environment's (spoofed) answers are kept too - a union can only add refusals.
            Assert.Contains(roots, r => r.Parent == Path.Combine(SpoofedHome, ".local", "share"));
        }

        [Fact]
        public void Decide_SpoofedHomeRedirectOntoTheAccountHomeStateFolder_Refused()
        {
            // The exact shape QA ran: HOME=<scratch> AGENTEYES_REDIRECT_DATA=<account home>/.local/share.
            var roots = RealLibrary.Derive(AccountHome, 1000, isMac: false, NoFile,
                Path.Combine(SpoofedHome, ".local", "share"), Path.Combine(SpoofedHome, "Videos"));

            var d = LibraryRedirect.Decide(Path.Combine(AccountHome, ".local", "share"), Abs("tmp", "scratch", "videos"),
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable }, roots, false);

            Assert.True(d.Refused, "a redirect onto the account's real state folder was accepted under a spoofed HOME");
            Assert.Contains($"the REAL state root {Path.Combine(AccountHome, ".local", "share", "AgentEyes")}", d.Refusal);
            Assert.Contains("passwd entry for uid 1000", d.Refusal);
        }

        [Fact]
        public void Decide_SpoofedHomeRedirectOntoTheAccountHomeVideosFolder_Refused()
        {
            var roots = RealLibrary.Derive(AccountHome, 1000, isMac: false, NoFile,
                Path.Combine(SpoofedHome, ".local", "share"), "");

            var d = LibraryRedirect.Decide(Abs("tmp", "scratch", "data"), Path.Combine(AccountHome, "Videos"),
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable }, roots, false);

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
        }

        [Fact]
        public void Derive_MacLayout_IsTheAccountsApplicationSupportAndMovies()
        {
            var roots = RealLibrary.Derive(Abs("Users", "me"), 501, isMac: true, NoFile, "", "");

            Assert.Equal(2, roots.Count);
            Assert.Contains(roots, r => r.Name == RealLibrary.StateName && r.Parent == Path.Combine(Abs("Users", "me"), "Library", "Application Support"));
            Assert.Contains(roots, r => r.Name == RealLibrary.RecordingsName && r.Parent == Path.Combine(Abs("Users", "me"), "Movies"));
        }

        [Fact]
        public void Derive_UserDirsMovesVideos_TheAccountsUserDirsDecidesNotTheEnvironments()
        {
            string userDirs = Path.Combine(AccountHome, ".config", "user-dirs.dirs");
            string? Read(string path) => path == userDirs ? "XDG_VIDEOS_DIR=\"$HOME/Media/Clips\"\n" : null;

            var roots = RealLibrary.Derive(AccountHome, 1000, isMac: false, Read, "", "");

            var rec = Assert.Single(roots, r => r.Name == RealLibrary.RecordingsName);
            Assert.Equal(Path.Combine(AccountHome, "Media", "Clips"), rec.Parent);
            Assert.Contains("XDG_VIDEOS_DIR in " + userDirs, rec.DerivedFrom);
        }

        [Fact]
        public void Derive_EnvironmentAgreesWithTheAccount_OneRootNamingBothDerivations()
        {
            var roots = RealLibrary.Derive(AccountHome, 1000, isMac: false, NoFile,
                Path.Combine(AccountHome, ".local", "share"), Path.Combine(AccountHome, "Videos"));

            Assert.Equal(2, roots.Count);
            Assert.All(roots, r => Assert.Contains(RealLibrary.ProcessEnvironmentSource, r.DerivedFrom));
            Assert.All(roots, r => Assert.Contains("passwd entry for uid 1000", r.DerivedFrom));
        }

        [Fact]
        public void Derive_RelativeAccountHome_ThrowsRatherThanGuessing()
        {
            Assert.Throws<InvalidOperationException>(() => RealLibrary.Derive("home/me", 1000, false, NoFile, "", ""));
        }

        [Theory]
        [InlineData("XDG_VIDEOS_DIR=\"$HOME/Videos\"", "Videos")]
        [InlineData("# XDG_VIDEOS_DIR=\"$HOME/Nope\"\nXDG_VIDEOS_DIR=\"$HOME/Vids\"", "Vids")]
        [InlineData("XDG_VIDEOS_DIR=\"$HOME/A\"\nXDG_VIDEOS_DIR=\"$HOME/B\"", "B")]
        [InlineData("XDG_VIDEOS_DIR=\"$HOME/A\"\nXDG_VIDEOS_DIR=\"relative/x\"", "A")]
        [InlineData("XDG_MUSIC_DIR=\"$HOME/Music\"", null)]
        [InlineData("XDG_VIDEOS_DIR=\"$HOME\"", "")]
        public void VideosFromUserDirs_TheXdgFormat_HomeIsTheAccountsNotTheEnvironments(string text, string? expectedUnderHome)
        {
            string? got = RealLibrary.VideosFromUserDirs(text, AccountHome);

            if (expectedUnderHome == null) Assert.Null(got);
            else Assert.Equal(expectedUnderHome == "" ? AccountHome : Path.Combine(AccountHome, expectedUnderHome), got);
        }

        [Fact]
        public void VideosFromUserDirs_AbsoluteValue_TakenAsWritten()
        {
            Assert.Equal(Abs("data", "videos"), RealLibrary.VideosFromUserDirs($"XDG_VIDEOS_DIR=\"{Abs("data", "videos")}\"", AccountHome));
        }

        [UnixOnlyFact]
        public void AccountHome_ThisProcess_IsAnAbsoluteFolderThatExists()
        {
            // The live passwd lookup (getpwuid_r through libc), on the platform's real struct layout.
            string home = RealLibrary.AccountHome(GetEuid());

            Assert.True(Path.IsPathFullyQualified(home), $"passwd home is not absolute: '{home}'");
            Assert.True(Directory.Exists(home), $"passwd home does not exist: '{home}'");
        }

        private static uint GetEuid() => RealLibrary.EffectiveUid();

        // ---- pass 2, ruling N1: each value must be an existing folder this process can WRITE to ------

        [Fact]
        public void UsableFolderProblem_ExistingWritableFolder_NullAndLeavesNoProbeBehind()
        {
            string dir = Made(Path.Combine(Scratch, "usable"));

            Assert.Null(LibraryRedirect.UsableFolderProblem(dir));
            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }

        [Fact]
        public void UsableFolderProblem_Missing_SaysSoWithTheFix()
        {
            string? why = LibraryRedirect.UsableFolderProblem(Path.Combine(Scratch, "no-such-folder"));

            Assert.NotNull(why);
            Assert.Contains("does not exist", why);
            Assert.Contains("mkdir -p", why);
        }

        [Fact]
        public void UsableFolderProblem_AFileNotAFolder_Refused()
        {
            string file = Path.Combine(Made(Path.Combine(Scratch, "afile")), "f");
            File.WriteAllText(file, "x");

            Assert.Contains("is a file, not a folder", LibraryRedirect.UsableFolderProblem(file));
        }

        [UnixOnlyFact]
        public void UsableFolderProblem_ReadOnlyFolder_RefusedByARealWrite()
        {
            // Root ignores mode bits, so this proves nothing as root - the gates do not run as root.
            Assert.True(GetEuid() != 0, "this test needs a non-root account: root can write a 0555 folder");
            string dir = Made(Path.Combine(Scratch, "readonly"));
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                string? why = LibraryRedirect.UsableFolderProblem(dir);

                Assert.NotNull(why);
                Assert.Contains("is not writable: a test write of", why);
            }
            finally
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [Fact]
        public void Decide_UsableFolderCheck_RunsOnlyAfterEveryOverlapRuleSoItNeverProbesTheRealLibrary()
        {
            // The probe WRITES a file. A redirect onto the real library must be refused by the overlap rule
            // before the probe is ever asked about it.
            var asked = new List<string>();
            var d = LibraryRedirect.Decide(Abs("tmp", "s", "data"), RealVideos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                new[] { new RealLibrary.Root(RealLibrary.RecordingsName, RealVideos, "given") }, false, null,
                f => { asked.Add(f); return null; });

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
            Assert.Empty(asked);
        }

        [Fact]
        public void Decide_UsableFolderCheckFails_RefusedNamingTheVariableAndThePath()
        {
            string videos = Abs("tmp", "s", "videoz");
            var d = LibraryRedirect.Decide(Abs("tmp", "s", "data"), videos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                Array.Empty<RealLibrary.Root>(), false, null,
                f => f == videos ? "does not exist." : null);

            Assert.True(d.Refused);
            Assert.StartsWith($"{LibraryRedirect.VideosVariable}={videos} does not exist.", d.Refusal);
        }

        [Fact]
        public void RealProcess_DataFolderDoesNotExist_RefusedWithExit5NamingItAndCreatesNothing()
        {
            string data = Path.Combine(Scratch, "missing", "data");           // a typo: never created
            string videos = Made(Path.Combine(Scratch, "missing", "videos"));

            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            Assert.Equal(LibraryRedirect.RefusedExitCode, code);
            Assert.Contains($"{LibraryRedirect.DataVariable}={data} does not exist", stderr);
            Assert.False(Directory.Exists(data), $"a refused start created {data}");
            Assert.Empty(Directory.GetFileSystemEntries(videos));
        }

        [UnixOnlyFact]
        public void RealProcess_VideosFolderNotWritable_RefusedWithExit5NamingIt()
        {
            Assert.True(GetEuid() != 0, "this test needs a non-root account: root can write a 0555 folder");
            string data = Made(Path.Combine(Scratch, "ro", "data"));
            string videos = Made(Path.Combine(Scratch, "ro", "videos"));
            File.SetUnixFileMode(videos, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

                Assert.Equal(LibraryRedirect.RefusedExitCode, code);
                Assert.Contains($"{LibraryRedirect.VideosVariable}={videos} is not writable", stderr);
                Assert.Empty(Directory.GetFileSystemEntries(data));
            }
            finally
            {
                File.SetUnixFileMode(videos, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // ---- the service's address and library -------------------------------------------------

        [Fact]
        public void ServicePortRefusal_RedirectedOnTheDefaultPort_Refused()
        {
            string? r = LibraryRedirect.ServicePortRefusal(LocalAppConfig.DefaultPort, redirected: true);

            Assert.NotNull(r);
            Assert.Contains("must not listen on the default port " + LocalAppConfig.DefaultPort, r);
        }

        [Theory]
        [InlineData(7882, false)]
        [InlineData(17894, true)]
        [InlineData(17894, false)]
        public void ServicePortRefusal_NotRedirectedOrAnotherPort_Allowed(int port, bool redirected)
        {
            Assert.Null(LibraryRedirect.ServicePortRefusal(port, redirected));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ServiceLibraryRefusal_SameLibrary_Allowed(bool redirected)
        {
            string data = Abs("tmp", "s", "data", "AgentEyes");
            string rec = Abs("tmp", "s", "videos", "AgentEyes");

            Assert.Null(LibraryRedirect.ServiceLibraryRefusal(data, rec, redirected, data, rec, redirected, "http://127.0.0.1:1/"));
        }

        [Fact]
        public void ServiceLibraryRefusal_ServiceOnTheRealLibraryClientRedirected_RefusedNamingBoth()
        {
            string realData = Path.Combine(RealLocal, "AgentEyes");
            string realRec = Path.Combine(RealVideos, "AgentEyes");
            string myData = Abs("tmp", "s", "data", "AgentEyes");
            string myRec = Abs("tmp", "s", "videos", "AgentEyes");

            string? r = LibraryRedirect.ServiceLibraryRefusal(realData, realRec, false, myData, myRec, true, "http://127.0.0.1:7882/");

            Assert.NotNull(r);
            Assert.Contains("DIFFERENT library", r);
            Assert.Contains(realRec, r);
            Assert.Contains(myRec, r);
        }

        [Fact]
        public void ServiceLibraryRefusal_ClientNotRedirectedServiceOnAScratchLibrary_Refused()
        {
            string? r = LibraryRedirect.ServiceLibraryRefusal(
                Abs("tmp", "s", "data", "AgentEyes"), Abs("tmp", "s", "videos", "AgentEyes"), true,
                Path.Combine(RealLocal, "AgentEyes"), Path.Combine(RealVideos, "AgentEyes"), false, "http://127.0.0.1:7882/");

            Assert.NotNull(r);
            Assert.Contains("DIFFERENT library", r);
        }

        [Fact]
        public void ServiceLibraryRefusal_NeitherSideRedirectedRootsDiffer_AllowedAsBefore()
        {
            // Linux: the systemd user service and the person's shell can see different XDG_DATA_HOME.
            // Both are the real library; the CLI worked against its own service before #161 and must still.
            Assert.Null(LibraryRedirect.ServiceLibraryRefusal(
                Abs("home", "me", ".local", "share", "AgentEyes"), Abs("home", "me", "Videos", "AgentEyes"), false,
                Abs("data", "xdg", "AgentEyes"), Abs("home", "me", "Videos", "AgentEyes"), false, "u"));
        }

        [Fact]
        public void ServiceLibraryRefusal_OnlyTheRecordingsRootDiffers_Refused()
        {
            string data = Abs("tmp", "s", "data", "AgentEyes");

            Assert.NotNull(LibraryRedirect.ServiceLibraryRefusal(data, Abs("a", "AgentEyes"), false, data, Abs("b", "AgentEyes"), true, "u"));
        }

        [Fact]
        public void ServiceLibraryRefusal_ServicePredatesTheRoots_AllowedOnlyForAnUnredirectedClient()
        {
            string myData = Abs("tmp", "s", "data", "AgentEyes");
            string myRec = Abs("tmp", "s", "videos", "AgentEyes");

            Assert.Null(LibraryRedirect.ServiceLibraryRefusal(null, null, false, myData, myRec, false, "u"));
            string? r = LibraryRedirect.ServiceLibraryRefusal(null, null, false, myData, myRec, true, "u");
            Assert.NotNull(r);
            Assert.Contains("does not report which library", r);
        }

        // ---- /status --------------------------------------------------------------------------

        [Fact]
        public void Status_OverTheControlApi_ReportsBothRootsAndTheRedirect()
        {
            int port = FreePort();
            using var server = new HeadlessControlServer(new RecordingService(), port);
            server.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            string json = http.GetStringAsync($"http://127.0.0.1:{port}/status").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(AppDataPaths.Root, root.GetProperty("DataRoot").GetString());
            Assert.Equal(AppDataPaths.RecordingsRoot, root.GetProperty("RecordingsRoot").GetString());
            Assert.True(root.GetProperty("LibraryRedirected").GetBoolean());
            Assert.Equal(LibraryRedirect.TestHostSource, root.GetProperty("LibraryRedirect").GetString());
        }

        // ---- a real child process -------------------------------------------------------------

        [Fact]
        public void RealProcess_PartialRedirect_RefusedWithExit5AndWritesNothing()
        {
            string data = Path.Combine(Scratch, "partial", "data");
            var (code, _, stderr) = RunAgentEyes(data, videos: null, "--help");

            Assert.Equal(LibraryRedirect.RefusedExitCode, code);
            Assert.Contains("library redirect refused: partial library redirect", stderr);
            Assert.Contains(LibraryRedirect.VideosVariable, stderr);
            // Refused BEFORE anything resolved a path: not even the scratch state folder was created.
            Assert.False(Directory.Exists(data), $"a refused start created {data}");
        }

        [Fact]
        public void RealProcess_FullRedirect_LogsBothRootsIntoTheScratchLogAndSaysSoOnStderr()
        {
            string data = Made(Path.Combine(Scratch, "full", "data"));
            string videos = Made(Path.Combine(Scratch, "full", "videos"));
            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            string stateRoot = Path.Combine(data, "AgentEyes");
            string recordingsRoot = Path.Combine(videos, "AgentEyes");
            Assert.Equal(0, code);
            Assert.Contains("[redirect] LIBRARY REDIRECTED", stderr);
            Assert.Contains("state -> " + stateRoot, stderr);
            Assert.Contains("recordings -> " + recordingsRoot, stderr);

            // The child's log is in the SCRATCH state root, and names both roots and the source.
            string logDir = Path.Combine(stateRoot, "logs");
            Assert.True(Directory.Exists(logDir), $"no log folder at {logDir}");
            string log = string.Join("\n", Directory.GetFiles(logDir).Select(ReadShared));
            Assert.Contains("[WARN] [LibraryRedirect] ApplyFromEnvironment: LIBRARY REDIRECTED", log);
            Assert.Contains($"state={stateRoot} recordings={recordingsRoot} redirect={LibraryRedirect.EnvironmentSource}", log);
        }

        [Fact]
        public void RealProcess_TypoOfARedirectVariable_RefusedWithExit5()
        {
            string data = Path.Combine(Scratch, "typo", "data");
            string videos = Path.Combine(Scratch, "typo", "videos");
            var (code, _, stderr) = RunAgentEyes(data, videos, "--help", extra: ("AGENTEYES_REDIRECT_RECORDINGS", "/x"));

            Assert.Equal(LibraryRedirect.RefusedExitCode, code);
            Assert.Contains("unknown library-redirect variable(s): AGENTEYES_REDIRECT_RECORDINGS", stderr);
            Assert.False(Directory.Exists(data), $"a refused start created {data}");
        }

        [Fact]
        public void RealProcess_RedirectedRecordStatus_AgainstAServiceOnAnotherLibrary_RefusedNamingBothLibraries()
        {
            // The service is this test host's (its own, in-process-redirected library); the child is
            // redirected to a scratch library and told the service's port through its scratch config -
            // exactly the shape of a redirected CLI pointed at the wrong service.
            int port = FreePort();
            using var server = new HeadlessControlServer(new RecordingService(), port);
            server.Start();

            string data = Path.Combine(Scratch, "client", "data");
            string videos = Made(Path.Combine(Scratch, "client", "videos"));
            Marked(Path.Combine(data, "AgentEyes"));   // pass 3 rule 2: a pre-populated scratch root carries the marker
            File.WriteAllText(Path.Combine(data, "AgentEyes", "config.json"), "{\"Port\": " + port + "}");

            var (code, stdout, stderr) = RunAgentEyes(data, videos, "record status");

            Assert.Equal(1, code);
            Assert.Contains("is acting on a DIFFERENT library", stderr);
            Assert.Contains(AppDataPaths.RecordingsRoot, stderr);
            Assert.Contains(Path.Combine(videos, "AgentEyes"), stderr);
            Assert.DoesNotContain("state=", stdout);
        }

        [Fact]
        public void RealProcess_RedirectedServeOnTheDefaultPort_RefusedBeforeListening()
        {
            string data = Made(Path.Combine(Scratch, "serve", "data"));
            string videos = Made(Path.Combine(Scratch, "serve", "videos"));

            var (code, _, stderr) = RunAgentEyes(data, videos, "serve");

            Assert.Equal(1, code);
            Assert.Contains("must not listen on the default port " + LocalAppConfig.DefaultPort, stderr);
        }

        [UnixOnlyFact]
        public void RealProcess_SpoofedHomeRedirectOntoTheAccountsRealVideosFolder_RefusedWithExit5()
        {
            // QA's attack on pass 1, on a REAL child: HOME (and every XDG_* base) points at scratch, so
            // the child's own environment names only scratch folders as "real" - and the redirect asks for
            // the account's REAL Videos folder. Only the passwd derivation can refuse it, so a mutation
            // that reads $HOME instead makes this test fail.
            //
            // Why the Videos variable and not the data one: if the guard ever regresses, a child with
            // the real Videos folder and a SCRATCH data root does nothing to the real library on --help
            // except StorageMigration's two Directory.Exists stats (measured under Landlock in the #161
            // pass-2 proof) - its log and state stay in scratch. Pointing the data variable at the real
            // state folder would, on regression, write into the person's live log from a test.
            string realVideos = Path.IsPathFullyQualified(AppDataPaths.MachineVideos)
                ? AppDataPaths.MachineVideos
                : Path.Combine(Environment.GetEnvironmentVariable("HOME")!, OperatingSystem.IsMacOS() ? "Movies" : "Videos");
            string fakeHome = Path.Combine(Scratch, "spoof", "home");
            Directory.CreateDirectory(fakeHome);
            string data = Path.Combine(Scratch, "spoof", "data");

            var (code, _, stderr) = RunAgentEyes(data, realVideos, "--help", tweak: psi =>
            {
                psi.Environment["HOME"] = fakeHome;
                foreach (string v in new[] { "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
                    psi.Environment.Remove(v);
            });

            Assert.True(code == LibraryRedirect.RefusedExitCode,
                $"a spoofed HOME let a redirect onto the real Videos folder {realVideos} through (exit {code}): {stderr}");
            Assert.Contains("the REAL recordings root", stderr);
            Assert.Contains("passwd entry for uid", stderr);
            Assert.False(Directory.Exists(data), $"a refused start created {data}");
        }

        [Fact]
        public void RealProcess_FullRedirect_BannerNamesTheRealRootsItDerived()
        {
            string data = Made(Path.Combine(Scratch, "banner", "data"));
            string videos = Made(Path.Combine(Scratch, "banner", "videos"));

            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            Assert.Equal(0, code);
            Assert.Contains("compared against these real roots and overlaps none (each with where it was derived from):", stderr);
            // Pass 3 (F1): what the comparison cannot see is stated, and the old claim is gone.
            Assert.Contains(LibraryRedirect.ComparisonLimit, stderr);
            // Pass 3 rule 1: the running service on the default port was ASKED, and the banner says what it found.
            Assert.Contains("127.0.0.1:" + LocalAppConfig.DefaultPort, stderr);
            Assert.DoesNotContain("kept out", stderr);
            Assert.DoesNotContain("NOT the person's real library", stderr);
            Assert.Contains(RealLibrary.StateName + " ", stderr);
            Assert.Contains(RealLibrary.RecordingsName + " ", stderr);
            if (!OperatingSystem.IsWindows())
                Assert.Contains("passwd entry for uid", stderr);
        }

        // ---- pass 3, F2: a link INSIDE a redirected root ---------------------------------------
        //
        // QA's measurement on pass 2: <scratch>/data/AgentEyes/logs linked to the owner's real logs folder,
        // the redirect ACCEPTED, the banner said the real library was kept out, and the process opened the
        // owner's live log five times. Only the path TO each root was resolved. These pin the walk under it.

        [Fact]
        public void LinkEscape_RelativeTargetFurtherDown_StaysInside()
        {
            string root = Abs("tmp", "s", "data", "AgentEyes");
            Assert.Null(LibraryRedirect.LinkEscape(Path.Combine(root, "a", "logs"), Path.Combine("sub", "x"), root));
        }

        [Fact]
        public void LinkEscape_AbsoluteTargetInsideTheRoot_StaysInside()
        {
            string root = Abs("tmp", "s", "data", "AgentEyes");
            Assert.Null(LibraryRedirect.LinkEscape(Path.Combine(root, "logs"), Path.Combine(root, "elsewhere", "logs"), root));
        }

        [Fact]
        public void LinkEscape_TargetIsTheRootItself_StaysInside()
        {
            string root = Abs("tmp", "s", "data", "AgentEyes");
            Assert.Null(LibraryRedirect.LinkEscape(Path.Combine(root, "a", "self"), root, root));
        }

        [Fact]
        public void LinkEscape_AbsoluteTargetThroughTheRootAsWritten_StaysInside()
        {
            // macOS: the walk runs on the RESOLVED root (/private/var/...), and a link written through the
            // person's spelling of it (/var/...) is still inside. (Code review, pass 3.)
            string resolved = Abs("private", "var", "s", "AgentEyes");
            string written = Abs("var", "s", "AgentEyes");

            Assert.Null(LibraryRedirect.LinkEscape(Path.Combine(resolved, "abs"), Path.Combine(written, "logs-real"), resolved, written));
            Assert.NotNull(LibraryRedirect.LinkEscape(Path.Combine(resolved, "abs"), Path.Combine(written, "logs-real"), resolved));
        }

        [Fact]
        public void LinkEscape_AbsoluteTargetOutsideTheRoot_EscapesNamingTheLinkAndWhereItLeads()
        {
            // QA's exact shape: <root>/logs -> the account's real logs folder.
            string root = Abs("tmp", "s", "data", "AgentEyes");
            string link = Path.Combine(root, "logs");
            string real = Abs("home", "me", ".local", "share", "AgentEyes", "logs");

            string? why = LibraryRedirect.LinkEscape(link, real, root, root);

            Assert.NotNull(why);
            Assert.Contains(link, why);
            Assert.Contains("leads outside " + root, why);
            Assert.Contains("(to " + real + ")", why);
        }

        [Fact]
        public void LinkEscape_SiblingFolderSharingTheRootsNameAsAPrefix_Escapes()
        {
            // "/tmp/s/data/AgentEyes-other" starts with "/tmp/s/data/AgentEyes" as a string but is not inside it.
            string root = Abs("tmp", "s", "data", "AgentEyes");
            Assert.NotNull(LibraryRedirect.LinkEscape(Path.Combine(root, "x"), root + "-other", root));
        }

        [Fact]
        public void LinkEscape_DotDotInTheTarget_EscapesEvenWhenItLooksInsideAsWritten()
        {
            // "sub/../x" is <root>/x as written; if sub is itself a link to the root, the kernel reads it as
            // <root>/../x. The written form is only trustworthy without "..".
            string root = Abs("tmp", "s", "data", "AgentEyes");
            string target = Path.Combine("sub", "..", "x");

            string? why = LibraryRedirect.LinkEscape(Path.Combine(root, "a"), target, root);

            Assert.NotNull(why);
            Assert.Contains("contains '..'", why);
        }

        [Fact]
        public void Decide_ALinkInsideARedirectedRootEscapes_RefusedNamingTheVariableAndTheLink()
        {
            string data = Abs("tmp", "s", "data");
            string videos = Abs("tmp", "s", "videos");
            string stateRoot = Path.Combine(data, "AgentEyes");
            string why = $"{Path.Combine(stateRoot, "logs")} is a link to '/elsewhere', which leads outside {stateRoot} (to /elsewhere).";
            var asked = new List<string>();

            var d = LibraryRedirect.Decide(data, videos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                Array.Empty<RealLibrary.Root>(), false, null,
                usableFolder: f => { asked.Add(f); return null; },
                escapingLink: (f, _) => f == stateRoot ? why : null);

            Assert.True(d.Refused, "a redirected root with a link out of it was accepted");
            Assert.StartsWith($"{LibraryRedirect.DataVariable} puts the redirected state root at {stateRoot}, and {why}", d.Refusal);
            Assert.Contains("Fix: remove the link", d.Refusal);
            // The write probe stays LAST (pass 2, verified by QA): a refused link means no probe was written.
            Assert.Empty(asked);
        }

        [Fact]
        public void Decide_LinkCheck_IsAskedAboutBothRootsResolvedAndAsWritten()
        {
            string data = Abs("tmp", "s", "data");
            string videos = Abs("tmp", "s", "videos");
            string stateRoot = Path.Combine(data, "AgentEyes");
            string recRoot = Path.Combine(videos, "AgentEyes");
            string stateResolved = Abs("private", "tmp", "s", "data", "AgentEyes");
            var asked = new List<(string, string)>();

            var d = LibraryRedirect.Decide(data, videos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                Array.Empty<RealLibrary.Root>(), false, p => p == stateRoot ? stateResolved : p,
                escapingLink: (f, w) => { asked.Add((f, w)); return null; });

            Assert.True(d.Redirect);
            Assert.Equal(new[] { (stateResolved, stateRoot), (recRoot, recRoot) }, asked);
        }

        [Fact]
        public void Decide_LinkCheck_RunsOnlyAfterEveryOverlapRule()
        {
            // A redirect onto the real library is refused by the overlap rule; the walk never lists it.
            var asked = new List<string>();
            var d = LibraryRedirect.Decide(Abs("tmp", "s", "data"), RealVideos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                new[] { new RealLibrary.Root(RealLibrary.RecordingsName, RealVideos, "given") }, false, null,
                escapingLink: (f, _) => { asked.Add(f); return null; });

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
            Assert.Empty(asked);
        }

        [UnixOnlyFact]
        public void FirstEscapingLink_QasShapeOnDisk_LogsLinkedOutOfTheRoot_Reported()
        {
            string t = Path.Combine(Scratch, "f2-walk");
            string root = Path.Combine(t, "data", "AgentEyes");
            string outside = Path.Combine(t, "stand-in-real", "AgentEyes", "logs");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(root, "logs"), outside);

            string? why = LibraryRedirect.FirstEscapingLink(root, root);

            Assert.NotNull(why);
            Assert.Contains(Path.Combine(root, "logs") + " is a link to '" + outside + "'", why);
        }

        [UnixOnlyFact]
        public void FirstEscapingLink_DeepHiddenFileLinkedOut_Reported()
        {
            // Deep, and a dot-file: .NET skips Hidden entries by default, and on Unix a dot-file IS Hidden.
            string t = Path.Combine(Scratch, "f2-deep");
            string root = Path.Combine(t, "videos", "AgentEyes");
            string deep = Path.Combine(root, "2026", "11", "rec");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(t, "outside.txt"), "x");
            File.CreateSymbolicLink(Path.Combine(deep, ".manifest.json"), Path.Combine(t, "outside.txt"));

            string? why = LibraryRedirect.FirstEscapingLink(root, root);

            Assert.NotNull(why);
            Assert.Contains(Path.Combine(deep, ".manifest.json"), why);
        }

        [UnixOnlyFact]
        public void FirstEscapingLink_LinksThatStayInside_NoneReported()
        {
            string t = Path.Combine(Scratch, "f2-inside");
            string root = Path.Combine(t, "data", "AgentEyes");
            Directory.CreateDirectory(Path.Combine(root, "logs-real"));
            Directory.CreateSymbolicLink(Path.Combine(root, "logs"), "logs-real");                       // relative
            Directory.CreateSymbolicLink(Path.Combine(root, "abs"), Path.Combine(root, "logs-real"));    // absolute
            Directory.CreateSymbolicLink(Path.Combine(root, "logs-real", "top"), root);                  // the root itself

            Assert.Null(LibraryRedirect.FirstEscapingLink(root, root));
        }

        [UnixOnlyFact]
        public void FirstEscapingLink_RootReachedThroughAnAlias_AbsoluteLinksThroughTheAliasStayInside()
        {
            // On disk, the macOS shape on any Unix: <t>/alias -> <t>/real; the walk runs on the RESOLVED root
            // <t>/real/AgentEyes, and the link inside is written through the alias.
            string t = Path.Combine(Scratch, "f2-alias");
            string real = Path.Combine(t, "real", "AgentEyes");
            Directory.CreateDirectory(Path.Combine(real, "logs-real"));
            Directory.CreateSymbolicLink(Path.Combine(t, "alias"), Path.Combine(t, "real"));
            string written = Path.Combine(t, "alias", "AgentEyes");
            Directory.CreateSymbolicLink(Path.Combine(real, "logs"), Path.Combine(written, "logs-real"));

            Assert.Null(LibraryRedirect.FirstEscapingLink(real, written));
            Assert.NotNull(LibraryRedirect.FirstEscapingLink(real, real));   // the alias is what makes it inside
        }

        [UnixOnlyFact]
        public void FirstEscapingLink_FolderIsMissing_NothingToCheck()
        {
            string missing = Path.Combine(Scratch, "f2-missing", "AgentEyes");
            Assert.Null(LibraryRedirect.FirstEscapingLink(missing, missing));
        }

        [UnixOnlyFact]
        public void RealProcess_LinkInsideTheScratchStateRootLeadsOut_RefusedWithExit5AndTheTargetUntouched()
        {
            // QA's F2 attack on a REAL child, against a STAND-IN for the real logs folder (never the owner's):
            // <scratch>/data/AgentEyes/logs -> <scratch>/stand-in-real/logs, in a root that carries the scratch
            // marker - so it is the LINK WALK that must refuse it, not the library check. At pass 2 the child was
            // accepted and wrote its log THROUGH the link; a mutation that drops the walk makes this test fail.
            string t = Path.Combine(Scratch, "f2-real");
            string data = Made(Path.Combine(t, "data"));
            string videos = Made(Path.Combine(t, "videos"));
            string standIn = Made(Path.Combine(t, "stand-in-real", "logs"));
            Marked(Path.Combine(data, "AgentEyes"));
            string link = Path.Combine(data, "AgentEyes", "logs");
            Directory.CreateSymbolicLink(link, standIn);

            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            Assert.True(code == LibraryRedirect.RefusedExitCode,
                $"a link out of the scratch state root was accepted (exit {code}): {stderr}");
            Assert.Contains(LibraryRedirect.DataVariable + " puts the redirected state root at", stderr);
            // The walk starts at the RESOLVED root, so the link is named by its resolved path (on macOS the
            // temp folder is under /var, a link to /private/var).
            string linkResolved = Path.Combine(LibraryRedirect.ResolveLinks(Path.Combine(data, "AgentEyes")), "logs");
            Assert.Contains(linkResolved + " is a link to '" + standIn + "'", stderr);
            Assert.Empty(Directory.GetFileSystemEntries(standIn));
        }

        [UnixOnlyFact]
        public void RealProcess_LegacyQaRecordLinkBesideAFreshScratchRoot_NeverAdopted()
        {
            // StorageMigration renames <parent>/qa-record to <parent>/AgentEyes when AgentEyes is absent. A
            // redirected start writes the scratch marker - creating <parent>/AgentEyes - BEFORE the migration
            // runs, so a qa-record link there (here to a stand-in) is never adopted and never written through.
            string t = Path.Combine(Scratch, "legacy-real");
            string data = Made(Path.Combine(t, "data"));
            string videos = Made(Path.Combine(t, "videos"));
            string standIn = Made(Path.Combine(t, "stand-in-real"));
            string legacy = Path.Combine(data, "qa-record");
            Directory.CreateSymbolicLink(legacy, standIn);

            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            Assert.True(code == 0, $"exit {code}: {stderr}");
            Assert.NotNull(new DirectoryInfo(legacy).LinkTarget);                       // still the link, not renamed
            Assert.Null(new DirectoryInfo(Path.Combine(data, "AgentEyes")).LinkTarget); // a real folder
            Assert.True(File.Exists(Path.Combine(data, "AgentEyes", LibraryRedirect.ScratchMarker)));
            Assert.Empty(Directory.GetFileSystemEntries(standIn));
        }

        // ---- pass 3, F1 rule 2: a library recognised by its CONTENT ------------------------------
        //
        // The Lead's ruling: write a marker on first redirected use, refuse an existing, NON-EMPTY AgentEyes folder
        // without it. A relocated real library has content and no marker, wherever it lives.

        [Fact]
        public void UnmarkedLibraryProblem_MissingOrEmpty_NothingToRefuse()
        {
            string t = Path.Combine(Scratch, "r2-empty");
            Assert.Null(LibraryRedirect.UnmarkedLibraryProblem(Path.Combine(t, "AgentEyes")));
            Directory.CreateDirectory(Path.Combine(t, "AgentEyes"));
            Assert.Null(LibraryRedirect.UnmarkedLibraryProblem(Path.Combine(t, "AgentEyes")));
        }

        [Fact]
        public void UnmarkedLibraryProblem_ContentWithoutTheMarker_RefusedNamingAnEntry()
        {
            string root = Path.Combine(Scratch, "r2-content", "AgentEyes");
            Directory.CreateDirectory(Path.Combine(root, "logs"));

            string? why = LibraryRedirect.UnmarkedLibraryProblem(root);

            Assert.NotNull(why);
            Assert.Contains("already holds an AgentEyes library that was not created as a scratch library", why);
            Assert.Contains("'logs'", why);
        }

        [Fact]
        public void UnmarkedLibraryProblem_OnlyAHiddenFile_StillContent()
        {
            string root = Path.Combine(Scratch, "r2-hidden", "AgentEyes");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".devthrottle-credential"), "x");

            Assert.NotNull(LibraryRedirect.UnmarkedLibraryProblem(root));
        }

        [Fact]
        public void UnmarkedLibraryProblem_ContentWithTheMarker_Allowed()
        {
            string root = Path.Combine(Scratch, "r2-marked", "AgentEyes");
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.WriteAllText(Path.Combine(root, LibraryRedirect.ScratchMarker), "");

            Assert.Null(LibraryRedirect.UnmarkedLibraryProblem(root));
        }

        [Fact]
        public void WriteScratchMarker_CreatesTheRootAndTheMarker_AndLeavesAnExistingOneAlone()
        {
            string root = Path.Combine(Scratch, "r2-write", "AgentEyes");
            LibraryRedirect.WriteScratchMarker(root);
            string marker = Path.Combine(root, LibraryRedirect.ScratchMarker);
            Assert.True(File.Exists(marker));
            File.WriteAllText(marker, "kept");

            LibraryRedirect.WriteScratchMarker(root);

            Assert.Equal("kept", File.ReadAllText(marker));
        }

        [Fact]
        public void Decide_AnUnmarkedLibrary_RefusedWithBothWaysToProceed_AndNeitherWalkNorProbeAsked()
        {
            string data = Abs("tmp", "s", "data");
            string videos = Abs("tmp", "s", "videos");
            string stateRoot = Path.Combine(data, "AgentEyes");
            var walked = new List<string>();
            var probed = new List<string>();

            var d = LibraryRedirect.Decide(data, videos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                Array.Empty<RealLibrary.Root>(), false, null,
                usableFolder: f => { probed.Add(f); return null; },
                escapingLink: (f, _) => { walked.Add(f); return null; },
                unmarkedLibrary: f => f == stateRoot ? "which already holds an AgentEyes library ..." : null);

            Assert.True(d.Refused);
            Assert.StartsWith($"{LibraryRedirect.DataVariable} puts the redirected state root at {stateRoot}, which already holds", d.Refusal);
            Assert.Contains("is EMPTY or absent", d.Refusal);
            Assert.Contains($"touch '{Path.Combine(stateRoot, LibraryRedirect.ScratchMarker)}'", d.Refusal);
            Assert.Empty(walked);
            Assert.Empty(probed);
        }

        [Fact]
        public void Decide_LibraryCheck_RunsOnlyAfterEveryOverlapRule()
        {
            var asked = new List<string>();
            var d = LibraryRedirect.Decide(Abs("tmp", "s", "data"), RealVideos,
                new[] { LibraryRedirect.DataVariable, LibraryRedirect.VideosVariable },
                new[] { new RealLibrary.Root(RealLibrary.RecordingsName, RealVideos, "given") }, false, null,
                unmarkedLibrary: f => { asked.Add(f); return null; });

            Assert.True(d.Refused);
            Assert.Contains("the REAL recordings root", d.Refusal);
            Assert.Empty(asked);
        }

        [Fact]
        public void RealProcess_ScratchRootPopulatedByHand_RefusedTellingHowToProceed_ThenAcceptedOnceMarked()
        {
            // The cost of rule 2, measured (the Lead asked for it): a seat pre-populates its scratch root with a
            // fixture. That is now REFUSED - a legitimate pre-populated scratch root is refused so that a real
            // library never is - and the refusal names both ways on.
            string t = Path.Combine(Scratch, "r2-fixture");
            string data = Made(Path.Combine(t, "data"));
            string videos = Made(Path.Combine(t, "videos"));
            Directory.CreateDirectory(Path.Combine(data, "AgentEyes"));
            File.WriteAllText(Path.Combine(data, "AgentEyes", "config.json"), "{\"Port\": 17999}");

            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            Assert.True(code == LibraryRedirect.RefusedExitCode, $"a hand-populated, unmarked scratch root was accepted (exit {code}): {stderr}");
            Assert.Contains("already holds an AgentEyes library that was not created as a scratch library", stderr);
            Assert.Contains("'config.json'", stderr);
            Assert.Contains("is EMPTY or absent", stderr);
            Assert.Contains("touch '", stderr);
            Assert.False(Directory.Exists(Path.Combine(data, "AgentEyes", "logs")), "a refused start logged into the folder");

            File.WriteAllText(Path.Combine(data, "AgentEyes", LibraryRedirect.ScratchMarker), "");
            var (code2, _, stderr2) = RunAgentEyes(data, videos, "--help");
            Assert.True(code2 == 0, $"the marked scratch root was still refused (exit {code2}): {stderr2}");
        }

        [Fact]
        public void RealProcess_RelocatedLibraryUnderASpoofedEnvironment_RefusedByItsContent()
        {
            // QA's F1 "za" shape (pass-2 report 1.4), simulated in scratch: the person's library lives at
            // <scratch>/reloc/xdg/AgentEyes (holding a file), the child's own environment names a DIFFERENT folder
            // (XDG_DATA_HOME spoofed), and the passwd entry knows neither. No path rule can refuse it; its content
            // must. At f9aba6a this was exit 0 and the child's log landed IN that library.
            string t = Path.Combine(Scratch, "r2-reloc");
            string reloc = Made(Path.Combine(t, "reloc", "xdg"));
            string library = Made(Path.Combine(reloc, "AgentEyes"));
            File.WriteAllText(Path.Combine(library, "MARKER"), "person-library");
            string spoof = Made(Path.Combine(t, "spoof"));
            string videos = Made(Path.Combine(t, "v"));

            var (code, _, stderr) = RunAgentEyes(reloc, videos, "--help",
                tweak: psi => psi.Environment["XDG_DATA_HOME"] = spoof);

            Assert.True(code == LibraryRedirect.RefusedExitCode,
                $"a redirect onto a library relocated where no path rule looks was accepted (exit {code}): {stderr}");
            Assert.Contains("already holds an AgentEyes library", stderr);
            Assert.Equal(new[] { "MARKER" }, Directory.GetFileSystemEntries(library).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void RealProcess_FullRedirect_MarksBothRootsAsScratch()
        {
            string data = Made(Path.Combine(Scratch, "r2-marks", "data"));
            string videos = Made(Path.Combine(Scratch, "r2-marks", "videos"));

            var (code, _, stderr) = RunAgentEyes(data, videos, "--help");

            Assert.True(code == 0, $"exit {code}: {stderr}");
            Assert.True(File.Exists(Path.Combine(data, "AgentEyes", LibraryRedirect.ScratchMarker)));
            Assert.True(File.Exists(Path.Combine(videos, "AgentEyes", LibraryRedirect.ScratchMarker)));
        }

        // ---- pass 3, F1 rule 1 (cheap half): ask the running service ------------------------------

        [Fact]
        public void FromServiceStatus_ARootReportingService_BothParentsAdded()
        {
            string data = Abs("data", "xdg", "AgentEyes");
            string rec = Abs("media", "v", "AgentEyes");
            string json = JsonSerializer.Serialize(new { DataRoot = data, RecordingsRoot = rec, LibraryRedirected = false });

            var (roots, note) = RealLibrary.FromServiceStatus(json, 7882);

            Assert.Equal(new[] { (RealLibrary.StateName, Abs("data", "xdg")), (RealLibrary.RecordingsName, Abs("media", "v")) },
                roots.Select(r => (r.Name, r.Parent)).ToArray());
            Assert.All(roots, r => Assert.Contains("the AgentEyes service answering on 127.0.0.1:7882", r.DerivedFrom));
            Assert.Contains("named its library", note);
        }

        [Fact]
        public void FromServiceStatus_AServiceOlderThan161_AddsNothingAndSaysSo()
        {
            var (roots, note) = RealLibrary.FromServiceStatus("{\"Recording\": false}", 7882);

            Assert.Empty(roots);
            Assert.Contains("names no library on /status (it predates issue #161)", note);
        }

        [Theory]
        [InlineData("relative/AgentEyes")]
        [InlineData("/data/xdg/NotAgentEyes")]
        public void FromServiceStatus_ARootItCannotCompare_Throws(string data)
        {
            string json = JsonSerializer.Serialize(new { DataRoot = data, RecordingsRoot = Abs("v", "AgentEyes") });
            Assert.Throws<InvalidOperationException>(() => RealLibrary.FromServiceStatus(json, 7882));
        }

        [Fact]
        public void AskRunningService_AServiceAnswering_ItsRootsComeBack()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string body = JsonSerializer.Serialize(new { DataRoot = Abs("data", "x", "AgentEyes"), RecordingsRoot = Abs("v", "AgentEyes") });
            var serve = System.Threading.Tasks.Task.Run(() =>
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var buf = new byte[4096];
                stream.Read(buf, 0, buf.Length);
                byte[] payload = System.Text.Encoding.ASCII.GetBytes(body);
                byte[] head = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                stream.Write(payload, 0, payload.Length);
            });
            try
            {
                var (roots, _) = RealLibrary.AskRunningService(port);
                Assert.Contains(roots, r => r.Name == RealLibrary.StateName && r.Parent == Abs("data", "x"));
            }
            finally
            {
                serve.Wait(5000);
                listener.Stop();
            }
        }

        [Fact]
        public void AskRunningService_NothingListening_Throws()
        {
            // Linux / macOS refuse a connect to a closed loopback port at once (HttpRequestException); Windows
            // retries until the timeout (TaskCanceledException, measured on CI 43940190095). ApplyFromEnvironment
            // treats both as "nothing answered".
            int port = FreePort();
            var ex = Record.Exception(() => RealLibrary.AskRunningService(port));
            Assert.True(ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException,
                $"expected 'nothing answered' (HttpRequestException or TaskCanceledException), got {ex?.GetType().FullName ?? "no exception"}");
        }

        [DefaultPortFreeFact]
        public void RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_RedirectOntoItRefused()
        {
            // The WIRING of rule 1: a REAL child asks whatever answers on the default port, and a library that
            // service names - relocated where neither the passwd entry nor the child's environment looks - is in
            // the union. A stand-in service plays the person's (it can only run where the default port is free:
            // CI, or an isolated network namespace; elsewhere this is reported SKIPPED). The redirect points at the
            // relocated parent with no AgentEyes folder yet, so rule 2 has nothing to see: only the service's roots
            // can refuse it. Dropping them from the union makes this test fail.
            string t = Path.Combine(Scratch, "r1-service");
            string reloc = Made(Path.Combine(t, "reloc"));
            string videos = Made(Path.Combine(t, "v"));
            string body = JsonSerializer.Serialize(new
            {
                DataRoot = Path.Combine(reloc, "AgentEyes"),
                RecordingsRoot = Path.Combine(t, "relocated-videos", "AgentEyes"),
                LibraryRedirected = false,
            });
            var listener = new TcpListener(IPAddress.Loopback, LocalAppConfig.DefaultPort);
            listener.Start();
            var serving = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        using var client = listener.AcceptTcpClient();
                        using var stream = client.GetStream();
                        var buf = new byte[4096];
                        stream.Read(buf, 0, buf.Length);
                        byte[] payload = System.Text.Encoding.ASCII.GetBytes(body);
                        byte[] head = System.Text.Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length);
                        stream.Write(payload, 0, payload.Length);
                    }
                }
                catch (SocketException) { }          // the listener was stopped: the test is over
                catch (ObjectDisposedException) { }
            });
            try
            {
                var (code, _, stderr) = RunAgentEyes(reloc, videos, "--help");

                Assert.True(code == LibraryRedirect.RefusedExitCode,
                    $"a redirect onto the library the running service names was accepted (exit {code}): {stderr}");
                // The real root is named RESOLVED (macOS: the temp folder /var/... is /private/var/...).
                Assert.Contains("overlaps the REAL state root " + Path.Combine(LibraryRedirect.ResolveLinks(reloc), "AgentEyes"), stderr);
                Assert.Contains("(/status DataRoot)", stderr);
                Assert.False(Directory.Exists(Path.Combine(reloc, "AgentEyes")), "a refused start created the state root");
            }
            finally
            {
                listener.Stop();
                serving.Wait(5000);
            }
        }

        /// <summary>A folder created and marked as a scratch library, so a test that pre-populates it is accepted
        /// by rule 2 and reaches the rule it is about.</summary>
        private static string Marked(string root)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, LibraryRedirect.ScratchMarker), "");
            return root;
        }

        // ---- pass 3, M-A: the ENVIRONMENT half of the union ------------------------------------
        //
        // The guard protects the passwd-derived roots UNION the ones this process's environment names. QA
        // dropped the environment half and all 69 redirect tests stayed green. That half is what protects
        // a person whose library really IS where their environment says (a non-default XDG_DATA_HOME).

        [Fact]
        public void Derive_EnvironmentNamesFoldersTheAccountDoesNot_BothAreProtected()
        {
            string relocatedData = Abs("data", "me", "share");
            string relocatedVideos = Abs("media", "me", "vids");

            var roots = RealLibrary.Derive(AccountHome, 1000, isMac: false, NoFile, relocatedData, relocatedVideos);

            Assert.Contains(roots, r => r.Name == RealLibrary.StateName && r.Parent == relocatedData
                                        && r.DerivedFrom == RealLibrary.ProcessEnvironmentSource);
            Assert.Contains(roots, r => r.Name == RealLibrary.RecordingsName && r.Parent == relocatedVideos
                                        && r.DerivedFrom == RealLibrary.ProcessEnvironmentSource);
            Assert.Equal(4, roots.Count);
        }

        [Fact]
        public void Derive_NoAccount_TheEnvironmentHalfAloneIsProtected()
        {
            // Windows: no passwd entry; the environment's (known-folder) answer is the whole protection.
            string local = Abs("Users", "me", "AppData", "Local");
            string videos = Abs("Users", "me", "Videos");

            var roots = RealLibrary.Derive(null, 0, isMac: false, NoFile, local, videos);

            Assert.Equal(new[] { (RealLibrary.StateName, local), (RealLibrary.RecordingsName, videos) },
                roots.Select(r => (r.Name, r.Parent)).ToArray());
        }

        [LinuxOnlyRedirectFact]
        public void RealProcess_RedirectOntoTheLibraryTheEnvironmentRelocated_RefusedWithExit5()
        {
            // A person whose XDG_DATA_HOME really moves their library: the child's own environment names
            // <scratch>/relocated-share as local app data, and the passwd entry knows nothing of it. A redirect
            // of the state root INTO that folder must be refused by the environment half of the union - and
            // by nothing else, so dropping that half fails exactly this test. Everything is under scratch: on
            // a regression the child redirects into scratch and touches nothing real.
            string t = Path.Combine(Scratch, "m-a");
            string relocated = Made(Path.Combine(t, "relocated-share"));
            string videos = Made(Path.Combine(t, "videos"));

            var (code, _, stderr) = RunAgentEyes(relocated, videos, "--help",
                tweak: psi => psi.Environment["XDG_DATA_HOME"] = relocated);

            Assert.True(code == LibraryRedirect.RefusedExitCode,
                $"a redirect onto the library XDG_DATA_HOME relocated to {relocated} was accepted (exit {code}): {stderr}");
            Assert.Contains("overlaps the REAL state root " + Path.Combine(relocated, "AgentEyes"), stderr);
            Assert.Contains(RealLibrary.ProcessEnvironmentSource, stderr);
            Assert.False(Directory.Exists(Path.Combine(relocated, "AgentEyes")), "a refused start created the state root");
        }

        /// <summary>The folder, created: a redirect is refused unless both values are existing, writable
        /// folders (pass 2, ruling N1), so a test that expects a redirect to be ACCEPTED makes them first.</summary>
        private static string Made(string path) => Directory.CreateDirectory(path).FullName;

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>
        /// Run the built agenteyes.dll (copied next to this test assembly by the project reference)
        /// under the dotnet host this test runs on. <paramref name="data"/> is ALWAYS set, so a child
        /// can never run with neither variable - i.e. on the machine's real library.
        /// </summary>
        private static (int Code, string Stdout, string Stderr) RunAgentEyes(string data, string? videos, string args,
            (string Name, string Value)? extra = null, Action<ProcessStartInfo>? tweak = null)
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "agenteyes.dll");
            Assert.True(File.Exists(dll), $"the engine was not built next to the tests: {dll}");
            string dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string host = Path.Combine(dotnetRoot, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
            Assert.True(File.Exists(host), $"no dotnet host at {host}");

            var psi = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(dll);
            foreach (string a in args.Split(' ')) psi.ArgumentList.Add(a);

            foreach (string name in psi.Environment.Keys.Where(k => k.StartsWith(LibraryRedirect.VariablePrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(name);
            psi.Environment[LibraryRedirect.DataVariable] = data;
            if (videos != null) psi.Environment[LibraryRedirect.VideosVariable] = videos;
            if (extra is { } e) psi.Environment[e.Name] = e.Value;
            tweak?.Invoke(psi);
            // The engine refuses to roll forward on its own; run it on whatever runtime runs the tests.
            psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException($"agenteyes {args} did not exit within 60s");
            }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }

        private static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
    }

    /// <summary>A fact that needs symbolic links an unprivileged process can create: macOS and Linux.
    /// On Windows creating one needs Developer Mode or elevation, so it is reported SKIPPED there - never
    /// silently passed. The pure resolver test above covers the rule on every platform.</summary>
    public sealed class UnixOnlyFactAttribute : FactAttribute
    {
        public UnixOnlyFactAttribute()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Skip = "needs an unprivileged symbolic link (macOS / Linux); the pure resolver test covers Windows";
        }
    }

    /// <summary>A fact about how .NET on LINUX names local app data from XDG_DATA_HOME. Elsewhere that variable
    /// moves nothing (macOS and Windows ask the platform), so it is reported SKIPPED there - never silently
    /// passed. The pure Derive tests cover the union on every platform.</summary>
    public sealed class LinuxOnlyRedirectFactAttribute : FactAttribute
    {
        public LinuxOnlyRedirectFactAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Skip = "XDG_DATA_HOME relocates local app data only on Linux; the pure Derive tests cover the rule everywhere";
        }
    }

    /// <summary>A fact that must play the person's service on the DEFAULT port (issue #161 pass 3, rule 1). It runs
    /// only where that port is free - CI, or an isolated network namespace - and is reported SKIPPED where something
    /// already holds it (on a person's machine, their real service), never silently passed. It never touches
    /// whatever holds the port: it only tries to bind it, and releases it at once.</summary>
    public sealed class DefaultPortFreeFactAttribute : FactAttribute
    {
        public DefaultPortFreeFactAttribute()
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, LocalAppConfig.DefaultPort);
                probe.Start();
                probe.Stop();
            }
            catch (SocketException)
            {
                Skip = $"127.0.0.1:{LocalAppConfig.DefaultPort} is in use (a real AgentEyes service?); this test plays the "
                       + "service there, so it runs only where the port is free (CI, or an isolated network namespace)";
            }
        }
    }

    /// <summary>The name of the collection every loopback-server test class joins (see LibraryRedirectTests).</summary>
    public static class LoopbackServerCollection
    {
        public const string Name = "loopback control servers";
    }
}
