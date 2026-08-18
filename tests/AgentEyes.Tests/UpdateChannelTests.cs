using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Guards THE UPDATE CHANNEL - the one URL every installed copy of AgentEyes asks "is there a
    /// newer version" (issue #184).
    ///
    /// Why this is worth its own file. The channel is a compile-time constant baked into every exe
    /// that has ever shipped, so a machine can only ever be re-pointed by an update it receives
    /// through the channel it ALREADY has. Point the product at a repo nothing is published to and
    /// GitHub keeps serving whatever is there: every install answers "up to date" and nobody sees an
    /// error. That is the silent-failure mode CLAUDE.md forbids, and it cannot be noticed from inside
    /// the app.
    ///
    /// So the channel is pinned five ways, weakest to strongest:
    ///   1. the constant itself (<see cref="ReleaseSource.LatestReleaseUrl"/>);
    ///   2. the URL <see cref="ReleaseSource.FetchLatestAsync"/> actually REQUESTS on an INJECTED
    ///      client, observed on a stub transport that answers the new channel and 404s everything
    ///      else;
    ///   3. the URL it requests on the DEFAULT-constructed client - the construction path the app,
    ///      the setup CLI and the setup wizard all take. Round 2 of this issue was rejected because
    ///      only 2 existed: the review gate made the retired URL selected exclusively when
    ///      `http is null`, and all sixteen tests stayed green over a completely re-pointed product;
    ///   4. a full older-install -> discover -> download -> install pass over that same stub;
    ///   5. the COMPILED setup engine's string literals, so a retired channel reintroduced on a
    ///      branch no test walks still cannot hide.
    /// Each pin is also fired at a KNOWN-BAD input and shown to FAIL, because a check only ever run
    /// against the state you hope passes has proved nothing.
    ///
    /// The last section is NOT a sixth pin and does not pretend to be one. It is a LITERAL STRING
    /// SCAN of the files under `.github` for the two retired names, with no behavioural claim of any
    /// kind. Rounds 2 and 3 tried to guard the PUBLISHING side with a release-command classifier;
    /// that classifier is deleted, and the reasons - five demonstrated evasions plus false positives
    /// on three ordinary Actions idioms - are written out in full at section 6 below.
    ///
    /// What these tests CANNOT see, stated rather than papered over: they prove what THIS build
    /// requests, plus the presence or absence of two literal strings in the CI files. They cannot
    /// prove what an already-installed v1.4.4 exe requests (its channel is compiled in and
    /// unreachable from here); they say NOTHING about where the workflow can publish, because a
    /// target can be spelled through a variable, a script outside `.github`, a raw HTTPS call, an
    /// action, or an external reusable workflow; they never touch the network; and they never run
    /// the workflow, so they say nothing about whether a release has actually been published
    /// anywhere. The protection against a stray cross-repo publish is STRUCTURAL - the retired
    /// repositories will not exist, so the attempt 404s - and it is recorded in the issue #184
    /// handoff, not asserted here.
    /// </summary>
    public sealed class UpdateChannelTests : IDisposable
    {
        /// <summary>The one channel this build is allowed to read releases from.</summary>
        private const string ExpectedChannelUrl =
            "https://api.github.com/repos/thefrederiksen/agenteyes-app/releases/latest";

        /// <summary>The retired channel, kept here ONLY as the known-bad input every pin below is
        /// fired against. Nothing in the product or the CI may resolve to it.</summary>
        private const string RetiredChannelUrl =
            "https://api.github.com/repos/thefrederiksen/AgentEyes-releases/releases/latest";

        private const string RetiredRepo = "thefrederiksen/AgentEyes-releases";

        private const string AssetBase =
            "https://github.com/thefrederiksen/agenteyes-app/releases/download/v1.4.5/";

        private readonly string _temp;

        public UpdateChannelTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "agenteyes-channel-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        public void Dispose()
        {
            ReleaseSource.DefaultTransport = null;
            try { Directory.Delete(_temp, recursive: true); } catch { }
        }

        /// <summary>The pin itself, as one named operation, so the SAME comparison that passes for the
        /// real channel below can be fired at the retired one and shown to fail.</summary>
        private static void AssertIsTheUpdateChannel(string observedUrl) =>
            Assert.Equal(ExpectedChannelUrl, observedUrl);

        // ---- 1. the constant --------------------------------------------------

        [Fact]
        public void UpdateChannel_IsPinnedToTheOneConsolidatedRepo()
        {
            Assert.Equal("thefrederiksen", ReleaseSource.Owner);
            Assert.Equal("agenteyes-app", ReleaseSource.Repo);
            AssertIsTheUpdateChannel(ReleaseSource.LatestReleaseUrl);
        }

        [Fact]
        public void UpdateChannelPin_FiresWhenTheChannelIsTheRetiredRepo()
        {
            // The known-bad input: the channel this repo is migrating OFF. If the pin let this
            // through, it would let any other silent re-point through too.
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertIsTheUpdateChannel(RetiredChannelUrl));
            Assert.Contains("agenteyes-app", failure.Message, StringComparison.Ordinal);
        }

        // ---- 2. the URL the code requests on an INJECTED client ----------------

        [Fact]
        public async Task FetchLatestAsync_RequestsExactlyTheConsolidatedChannel()
        {
            var releaseDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            ServeRelease(channel, releaseDir, "1.4.5");

            var resolved = await new ReleaseSource(new HttpClient(channel)).FetchLatestAsync(CancellationToken.None);

            // The FIRST request the updater makes IS the channel. Quoted, not summarized.
            Assert.NotEmpty(channel.Requests);
            AssertIsTheUpdateChannel(channel.Requests[0]);
            Assert.Equal("1.4.5", resolved.Manifest.Version);
        }

        [Fact]
        public async Task FetchLatestAsync_FailsLoudlyWhenOnlyTheRetiredChannelIsServed()
        {
            // The mutation for pin 2: a transport that serves ONLY the old channel. If the product
            // still pointed there this would succeed, so its failure is what proves the code moved.
            var releaseDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            channel.Serve(RetiredChannelUrl, ReleaseJson(releaseDir, "1.4.5"));
            ServeAssets(channel, releaseDir);

            var source = new ReleaseSource(new HttpClient(channel));

            await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchLatestAsync(CancellationToken.None));
            Assert.Equal(new[] { ExpectedChannelUrl }, channel.Requests.ToArray());
        }

        // ---- 3. THE PRODUCTION CONSTRUCTION PATH -------------------------------
        //
        // Every test above hands ReleaseSource a client it built itself. No production caller does:
        // the app updater (AgentEyes.App/UpdateChecker.cs), the setup CLI
        // (AgentEyes.Setup.Cli/Commands.cs) and the setup wizard
        // (AgentEyes.Setup/Services/EngineInstallRunner.cs) all write `new ReleaseSource()`. The
        // review gate exploited exactly that gap - it selected the retired URL only when
        // `http is null` and every channel test stayed green - so these tests construct
        // ReleaseSource the way production does and substitute ONLY the transport underneath it.

        [Fact]
        public async Task DefaultConstruction_RequestsExactlyTheConsolidatedChannel()
        {
            var releaseDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            ServeRelease(channel, releaseDir, "1.4.5");
            ReleaseSource.DefaultTransport = channel;

            // No argument: this is the constructor call every shipping front-end makes.
            var resolved = await new ReleaseSource().FetchLatestAsync(CancellationToken.None);

            // An empty request log would mean the substitution never took effect - a broken
            // instrument, not a clean run - so the presence of the request is asserted first.
            Assert.NotEmpty(channel.Requests);
            AssertIsTheUpdateChannel(channel.Requests[0]);
            Assert.Equal("1.4.5", resolved.Manifest.Version);
        }

        [Fact]
        public async Task DefaultConstruction_FailsLoudlyWhenOnlyTheRetiredChannelIsServed()
        {
            var releaseDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            channel.Serve(RetiredChannelUrl, ReleaseJson(releaseDir, "1.4.5"));
            ServeAssets(channel, releaseDir);
            ReleaseSource.DefaultTransport = channel;

            await Assert.ThrowsAsync<HttpRequestException>(
                () => new ReleaseSource().FetchLatestAsync(CancellationToken.None));
            Assert.Equal(new[] { ExpectedChannelUrl }, channel.Requests.ToArray());
        }

        [Fact]
        public async Task DefaultConstruction_AndInjectedClient_RequestTheSameChannel()
        {
            // The negative control that DISTINGUISHES the two construction paths. A difference
            // between them - which is what the gate's `_usesDefaultHttp` mutation introduced - is a
            // failure here even if each path individually looked reasonable.
            var releaseDir = BuildRelease("1.4.5");

            var injectedChannel = new StubChannel();
            ServeRelease(injectedChannel, releaseDir, "1.4.5");
            await new ReleaseSource(new HttpClient(injectedChannel)).FetchLatestAsync(CancellationToken.None);

            var defaultChannel = new StubChannel();
            ServeRelease(defaultChannel, releaseDir, "1.4.5");
            ReleaseSource.DefaultTransport = defaultChannel;
            await new ReleaseSource().FetchLatestAsync(CancellationToken.None);

            Assert.NotEmpty(defaultChannel.Requests);
            Assert.Equal(injectedChannel.Requests.ToArray(), defaultChannel.Requests.ToArray());
        }

        [Fact]
        public async Task DefaultConstruction_SendsTheGitHubApiHeaders()
        {
            // The other half of "the production path is the path under test": the default client is
            // built in its own statement, so its headers are pinned rather than assumed.
            var releaseDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            ServeRelease(channel, releaseDir, "1.4.5");
            ReleaseSource.DefaultTransport = channel;

            await new ReleaseSource().FetchLatestAsync(CancellationToken.None);

            Assert.NotEmpty(channel.Headers);
            Assert.Contains("agenteyes-setup", channel.Headers[0].UserAgent, StringComparison.Ordinal);
            Assert.Contains("application/vnd.github+json", channel.Headers[0].Accept, StringComparison.Ordinal);
        }

        // ---- 4. the whole update path, on an older install --------------------

        [Fact]
        public async Task OlderInstall_OffersAndInstallsTheNextReleaseFromTheConsolidatedChannel()
        {
            // A machine sitting on v1.4.4 - the version the owner is actually on, and the last one
            // built before the channel moved. Installed the same way a real install gets there: by
            // running the engine over a v1.4.4 release, which is what records installed.json.
            var layout = new InstallLayout(Path.Combine(_temp, "root"));
            var oldRelease = ReleaseSource.LoadLocalReleaseDir(BuildRelease("1.4.4"));
            var offline = new ReleaseSource();
            var installed144 = await new Orchestrator(layout, new InstalledStateReader(layout)).RunAsync(
                ComponentRegistry.All, oldRelease.Manifest,
                (item, ct) => offline.DownloadAssetAsync(item.AssetName, oldRelease.DownloadUrls, ct));
            Assert.Equal(0, installed144.Run!.Failed);
            Assert.Equal("AgentEyesApp-win-x64.exe v1.4.4", File.ReadAllText(layout.PathFor(ComponentRegistry.App)));

            // The next release, served ONLY on the consolidated channel.
            var nextDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            ServeRelease(channel, nextDir, "1.4.5");
            var source = new ReleaseSource(new HttpClient(channel));

            // OFFERS: discovered through the channel, and the planner says "update 1.4.4 -> 1.4.5".
            var release = await source.FetchLatestAsync(CancellationToken.None);
            AssertIsTheUpdateChannel(channel.Requests[0]);
            var reader = new InstalledStateReader(layout);
            var plan = UpdatePlanner.Plan(ComponentRegistry.All, reader.ReadAll(ComponentRegistry.All), release.Manifest);

            Assert.True(plan.HasWork);
            var app = plan.Items.Single(i => i.ComponentId == ComponentRegistry.App.Id);
            Assert.Equal(PlanItemKind.Update, app.Kind);
            Assert.Equal("1.4.4", app.FromVersion);
            Assert.Equal("1.4.5", app.ToVersion);

            // INSTALLS: downloaded over the same channel, SHA-256 verified by the runner, swapped in.
            var result = await new Orchestrator(layout, reader).RunAsync(
                ComponentRegistry.All, release.Manifest,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));

            Assert.NotNull(result.Run);
            Assert.Equal(0, result.Run!.Failed);
            Assert.Equal("AgentEyesApp-win-x64.exe v1.4.5", File.ReadAllText(layout.PathFor(ComponentRegistry.App)));
            Assert.Equal("agenteyes-win-x64.exe v1.4.5", File.ReadAllText(layout.PathFor(ComponentRegistry.Cli)));
            Assert.Equal("1.4.5", InstalledManifest.Load(layout).Get(ComponentRegistry.App.Id));

            // Every byte came off the pinned repo - the API call AND the asset downloads, which live
            // on github.com rather than api.github.com - and nothing reached the retired one.
            Assert.All(channel.Requests, url =>
                Assert.Contains($"/{ReleaseSource.Owner}/{ReleaseSource.Repo}/", url, StringComparison.Ordinal));
            Assert.DoesNotContain(channel.Requests, url => url.Contains("AgentEyes-releases", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OlderInstall_GetsNothingWhenOnlyTheRetiredChannelHasTheRelease()
        {
            // The mutation for pin 4, and the accepted consequence of consolidating: an exe compiled
            // against the retired channel can no longer be reached by anything this build publishes.
            // There is exactly ONE such machine (the owner's), and it is updated by hand by running
            // the new installer - which is why no bridge release exists.
            var layout = new InstallLayout(Path.Combine(_temp, "stranded"));
            var oldRelease = ReleaseSource.LoadLocalReleaseDir(BuildRelease("1.4.4"));
            var offline = new ReleaseSource();
            await new Orchestrator(layout, new InstalledStateReader(layout)).RunAsync(
                ComponentRegistry.All, oldRelease.Manifest,
                (item, ct) => offline.DownloadAssetAsync(item.AssetName, oldRelease.DownloadUrls, ct));

            var nextDir = BuildRelease("1.4.5");
            var channel = new StubChannel();
            channel.Serve(RetiredChannelUrl, ReleaseJson(nextDir, "1.4.5"));
            ServeAssets(channel, nextDir);

            var source = new ReleaseSource(new HttpClient(channel));
            await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchLatestAsync(CancellationToken.None));
            Assert.Equal("AgentEyesApp-win-x64.exe v1.4.4", File.ReadAllText(layout.PathFor(ComponentRegistry.App)));
        }

        // ---- 5. the COMPILED product ------------------------------------------

        [Fact]
        public void CompiledSetupEngine_CarriesTheConsolidatedChannelAndNotTheRetiredOne()
        {
            // The strongest pin available without running every branch: whatever code path selects a
            // URL, the URL has to exist in the assembly as a literal. This is what makes a
            // production-only re-point - the shape the review gate used - visible even though no test
            // walks that branch.
            //
            // LIMIT, stated here rather than implied: a string assembled at run time from fragments
            // that individually do not contain the searched text is NOT seen. This answers "does the
            // compiled product carry this string", not "can it ever produce this string". Pin 3 above
            // is what covers the behavior of the path production actually takes.
            string engine = CompiledCode.EngineAssembly;
            int total = CompiledCode.StringLiteralCount(engine);
            Assert.True(total > 50, $"Only {total} string literals were read from {engine} - the scanner is broken.");

            Assert.NotEmpty(CompiledCode.StringLiterals(engine, v => v.Contains("agenteyes-app", StringComparison.Ordinal)));
            Assert.NotEmpty(CompiledCode.StringLiterals(engine, v => v.Contains("api.github.com", StringComparison.Ordinal)));

            var offenders = CompiledCode.StringLiterals(engine, IsRetiredChannelLiteral);
            Assert.True(offenders.Count == 0,
                "The compiled setup engine carries the retired channel: " + CompiledCode.Describe(
                    offenders.Select(o => new CompiledCode.CallSite(o.Assembly, o.Method, o.Value))));
        }

        [Fact]
        public void CompiledLiteralScan_FiresOnAKnownBadAssembly()
        {
            // The negative control for the scan above. THIS test assembly compiles RetiredChannelUrl
            // and RetiredRepo into real ldstr instructions, so a scanner that reported nothing over
            // the engine has to report something here - otherwise "no offending literal" would only
            // ever have meant "the scanner read nothing".
            var found = CompiledCode.StringLiterals(CompiledCode.TestAssembly, IsRetiredChannelLiteral);
            Assert.NotEmpty(found);
            Assert.Contains(found, f => f.Value == RetiredChannelUrl);
        }

        [Fact]
        public void CompiledApp_CarriesNoRetiredChannelLiteralAtAll()
        {
            // Until issue #186 the app named the retired repo in ONE place - the plugin registry URL -
            // and this test pinned that exact single occurrence. #186 re-pointed the registry at the
            // consolidated repo, so the allowed set is now EMPTY: any occurrence at all fails here.
            //
            // The scanner is anchored first, so an empty offender list can never be produced by a scan
            // that read nothing: AgentEyesApp.dll must yield a large literal count and must carry the
            // consolidated name.
            string app = CompiledCode.AppAssembly;
            int total = CompiledCode.StringLiteralCount(app);
            Assert.True(total > 50, $"Only {total} string literals were read from {app} - the scanner is broken.");
            Assert.NotEmpty(CompiledCode.StringLiterals(app, v => v.Contains("agenteyes-app", StringComparison.Ordinal)));

            var values = CompiledCode.StringLiterals(app, IsRetiredChannelLiteral)
                .Select(s => s.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(Array.Empty<string>(), values);
        }

        private static bool IsRetiredChannelLiteral(string value) =>
            value.Contains("AgentEyes-releases", StringComparison.OrdinalIgnoreCase)
            || value.Contains("-releases", StringComparison.OrdinalIgnoreCase);

        // ---- 6. the CI files: ONE LITERAL STRING SCAN, and nothing more --------
        //
        // WHAT WAS HERE AND WHY IT IS GONE (round 4 of issue #184). Rounds 2 and 3 carried a
        // release-command CLASSIFIER - PwshScript tokenized each step's PowerShell, ReleaseWorkflowModel
        // classified every resulting command, and a 43-input corpus fired it at known-bad workflows. It
        // is DELETED, not repaired. QA round 3 got FIVE of six novel evasions through it, one of them a
        // complete cross-repo publish with all 806 tests green, across all four mechanisms it could ever
        // face: a command name carried in a double-quoted string behind the call operator, a `gh` call
        // inside scripts/build-release.ps1 (which the workflow invokes, and which lives outside .github),
        // a raw-HTTP release create under `shell: bash` via $GITHUB_API_URL, and an external reusable
        // workflow pulled in with `secrets: inherit`. In the same run it FALSE-POSITIVED on three
        // canonical Actions idioms - a step with no `shell:` key, an unrelated new workflow file, and
        // job-level `defaults: run: shell:` - each turning seven tests red with a message about shells
        // that named nothing about releases. Evadable four ways AND red on ordinary CI edits is the
        // profile of a guard the next developer deletes, and classifying arbitrary shell is not a finite
        // grammar problem: authority can also flow through a launched process, a generated script, a
        // composite action, an MSBuild target, a JavaScript action, or a new API client.
        //
        // WHAT PROTECTS THE PUBLISH TARGET INSTEAD IS STRUCTURAL, NOT A TEST. The classifier existed to
        // stop a publish into thefrederiksen/AgentEyes-releases. That repository is being retired and
        // DELETED (issue #186 lands first, then the repo goes), and the DevThrottle mirror it also
        // watched is already deleted. Once the target does not exist, the mistake fails LOUDLY with a
        // 404 from gh at the moment it is attempted - which is stronger than any parser, because it
        // holds for every mechanism above, including the ones no parser sees.

        /// <summary>
        /// The retired names. `AgentEyes-releases` is the update/release repo this issue migrates OFF;
        /// `RELEASES_TOKEN` is the cross-repo credential the deleted DevThrottle mirror needed. Neither
        /// may occur in any file under `.github`.
        /// </summary>
        private static readonly string[] RetiredCiNames = { "AgentEyes-releases", "RELEASES_TOKEN" };

        /// <summary>Every file under `.github`, repo-relative, with its text. Throws when the directory
        /// or the two files known to live in it are missing, so "no retired name found" can never be
        /// produced by a scan that read nothing.</summary>
        private static List<(string Path, string Text)> GithubFiles()
        {
            string root = Path.Combine(RepoSource.Root, ".github");
            if (!Directory.Exists(root))
                throw new InvalidOperationException($"No .github directory under '{RepoSource.Root}' - this scan is looking at the wrong tree.");

            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Path: p.Substring(RepoSource.Root.Length + 1).Replace('\\', '/'), Text: File.ReadAllText(p)))
                .ToList();

            foreach (string expected in new[] { ".github/workflows/release.yml", ".github/actions/sign-windows/action.yml" })
                if (!files.Any(f => f.Path == expected))
                    throw new InvalidOperationException($"'{expected}' was not read by this scan - the instrument is broken, not the tree clean.");

            foreach (var file in files)
                if (file.Text.Length == 0)
                    throw new InvalidOperationException($"'{file.Path}' read as empty - a scan over empty text finds nothing by construction.");

            return files;
        }

        /// <summary>The scan itself, as one named operation, so the SAME comparison that passes over the
        /// real `.github` below can be fired at a deliberately re-poisoned copy and shown to fail.</summary>
        private static void AssertNoRetiredCiNames(IEnumerable<(string Path, string Text)> files)
        {
            var hits = files
                .SelectMany(f => RetiredCiNames
                    .Where(name => f.Text.Contains(name, StringComparison.OrdinalIgnoreCase))
                    .Select(name => $"{f.Path}: {name}"))
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToArray();

            Assert.True(hits.Length == 0, "A retired name occurs under .github: " + string.Join("; ", hits));
        }

        [Fact]
        public void GithubFiles_ContainNoRetiredChannelOrTokenName_LiteralStringScanOnly()
        {
            // READ THE NAME LITERALLY. This is a LITERAL STRING SCAN of the files under `.github`, and
            // it makes NO behavioural claim whatsoever. It does not - cannot - show that the workflow
            // publishes to only one repository.
            //
            // INVISIBLE TO IT, listed rather than implied: a publish to any OTHER repository name; a
            // target spelled through a variable, an expression, or string concatenation; a `gh` call in
            // any .ps1/.sh the workflow invokes (those live outside `.github`); a raw HTTPS call to the
            // releases API; a third-party or composite action that publishes; and an external reusable
            // workflow called with `secrets: inherit`. QA round 3 demonstrated four of those live.
            //
            // What it IS good for is exactly one thing: the two retired names are decidable text, so a
            // copy-paste of the old channel or the old cross-repo token back into a CI file is caught at
            // build time instead of at publish time. The real protection against a stray cross-repo
            // publish is structural - the retired repositories will not exist - and it is recorded in
            // the handoff, not asserted here.
            var files = GithubFiles();

            // ANCHOR, and NOT a claim of exclusivity: the intended publish command is still in the file
            // this scan actually read. If it were not, the scan would be reading something other than
            // this repository's release workflow, and its silence would mean nothing.
            string workflow = files.Single(f => f.Path == ".github/workflows/release.yml").Text;
            Assert.Contains("gh release create \"${{ github.ref_name }}\"", workflow, StringComparison.Ordinal);
            Assert.Contains("--repo \"${{ github.repository }}\"", workflow, StringComparison.Ordinal);
            Assert.Contains("--verify-tag", workflow, StringComparison.Ordinal);
            Assert.Contains("GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}", workflow, StringComparison.Ordinal);

            AssertNoRetiredCiNames(files);
        }

        [Fact]
        public void RetiredCiNameScan_FiresWhenARetiredNameIsPutBack()
        {
            // The negative control for the scan above: the same operation, over the same real files with
            // ONE of them re-poisoned exactly the way a copy-paste would do it. A scan that stayed green
            // here would only ever have meant "this code found nothing anywhere".
            var files = GithubFiles();
            int index = files.FindIndex(f => f.Path == ".github/workflows/release.yml");
            files[index] = (files[index].Path, files[index].Text
                + "\n      - name: Mirror\n        env:\n          GH_TOKEN: ${{ secrets.RELEASES_TOKEN }}\n"
                + "        run: gh release create v9.9.9 --repo " + RetiredRepo + "\n");

            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoRetiredCiNames(files));
            Assert.Contains("AgentEyes-releases", failure.Message, StringComparison.Ordinal);
            Assert.Contains("RELEASES_TOKEN", failure.Message, StringComparison.Ordinal);
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>Build a complete, self-consistent release (four assets + a manifest whose hashes
        /// match them) in a fresh temp directory, and return that directory.</summary>
        private string BuildRelease(string version)
        {
            string dir = Path.Combine(_temp, "release-" + version);
            Directory.CreateDirectory(dir);

            foreach (var name in new[] { "AgentEyesApp-win-x64.exe", "agenteyes-win-x64.exe", "agenteyes-setup-cli-win-x64.exe" })
                File.WriteAllText(Path.Combine(dir, name), $"{name} v{version}");

            string zipPath = Path.Combine(dir, "agenteyes-ffmpeg-win-x64.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.CreateEntry(exe);
                    using var w = new StreamWriter(entry.Open());
                    w.Write($"{exe} v{version}");
                }

            string Sha(string name) => Hashing.Sha256OfFile(Path.Combine(dir, name));
            File.WriteAllText(Path.Combine(dir, "release-manifest.json"), $$"""
                {
                  "version": "{{version}}",
                  "assets": {
                    "AgentEyesApp-win-x64.exe": { "version": "{{version}}", "sha256": "{{Sha("AgentEyesApp-win-x64.exe")}}" },
                    "agenteyes-win-x64.exe": { "version": "{{version}}", "sha256": "{{Sha("agenteyes-win-x64.exe")}}" },
                    "agenteyes-setup-cli-win-x64.exe": { "version": "{{version}}", "sha256": "{{Sha("agenteyes-setup-cli-win-x64.exe")}}" },
                    "agenteyes-ffmpeg-win-x64.zip": { "version": "{{version}}", "sha256": "{{Sha("agenteyes-ffmpeg-win-x64.zip")}}" }
                  }
                }
                """);
            return dir;
        }

        /// <summary>The GitHub /releases/latest response body for a built release directory.</summary>
        private static string ReleaseJson(string releaseDir, string version)
        {
            var assets = Directory.GetFiles(releaseDir)
                .Select(Path.GetFileName)
                .Select(name => new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["browser_download_url"] = AssetBase + name,
                });
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tag_name"] = "v" + version,
                ["assets"] = assets,
            });
        }

        private static void ServeAssets(StubChannel channel, string releaseDir)
        {
            foreach (var file in Directory.GetFiles(releaseDir))
                channel.Serve(AssetBase + Path.GetFileName(file), File.ReadAllBytes(file));
        }

        private static void ServeRelease(StubChannel channel, string releaseDir, string version)
        {
            channel.Serve(ExpectedChannelUrl, ReleaseJson(releaseDir, version));
            ServeAssets(channel, releaseDir);
        }

        /// <summary>
        /// A transport that answers ONLY the URLs it was explicitly given and 404s everything else,
        /// recording every URL asked for. Both halves matter: the recording is what lets a test say
        /// WHICH channel was read, and the 404 is what makes a wrong channel a loud failure instead of
        /// a silent one.
        /// </summary>
        private sealed class StubChannel : HttpMessageHandler
        {
            private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

            public List<string> Requests { get; } = new();

            /// <summary>The headers each request carried, so the default-constructed client's own
            /// setup is observable rather than assumed.</summary>
            public List<(string UserAgent, string Accept)> Headers { get; } = new();

            public void Serve(string url, byte[] bytes) => _content[url] = bytes;

            public void Serve(string url, string text) => Serve(url, Encoding.UTF8.GetBytes(text));

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string url = request.RequestUri!.ToString();
                Requests.Add(url);
                Headers.Add((request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString()));
                var status = _content.ContainsKey(url) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(_content.TryGetValue(url, out var bytes) ? bytes : Array.Empty<byte>()),
                });
            }
        }
    }
}
