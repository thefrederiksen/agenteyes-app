using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Guards THE PLUGIN CHANNEL - the URL every installed copy of AgentEyes asks "what plugins are
    /// there", and the URL the entries it gets back point their downloads at (issue #186).
    ///
    /// Why this is worth its own file. The registry URL is a compile-time constant baked into every
    /// exe that has ever shipped. It used to name `thefrederiksen/AgentEyes-releases`, which is
    /// retired and is being DELETED. Neither half of that fails loudly: while the repo still exists,
    /// GitHub keeps serving a file nobody updates any more, so the catalog silently freezes; once the
    /// repo is gone, a shipped copy still asking the old address gets a 404 nobody chose. Both are the
    /// silent-staleness failure CLAUDE.md forbids, and neither is visible from inside the app.
    ///
    /// So the channel is pinned four ways, weakest to strongest, and each pin is ALSO fired at a
    /// known-bad input and shown to fail - a check only ever run against the state you hope passes has
    /// proved nothing:
    ///   1. the constants themselves (<see cref="PluginRegistry.Owner"/>, <see cref="PluginRegistry.Repo"/>,
    ///      <see cref="PluginRegistry.DefaultUrl"/>);
    ///   2. the URL <see cref="PluginRegistry.UrlFor"/> resolves for a default config - the value the
    ///      catalog dialog and the plugin manager both take when the user has set no override;
    ///   3. the URL <see cref="PluginRegistry.FetchAsync"/> actually REQUESTS, observed on a stub
    ///      transport that answers the consolidated URL and 404s everything else, over THIS
    ///      repository's real `plugins/registry.json` - so the parse, the URL and the published file
    ///      are all exercised together;
    ///   4. the COMPILED app's string literals, so a retired URL reintroduced on a branch no test
    ///      walks still cannot hide.
    /// A fifth section runs `scripts/package-plugin.ps1` for real and reads the `zipUrl` it generates,
    /// because the registry URL and the download URLs inside the registry are two different
    /// dependencies on the retired repo and fixing one does not fix the other. A sixth pins the
    /// ENVIRONMENT that packaging run gets: the child shell's PSModulePath is stated, not inherited,
    /// after an inherited PowerShell 7 one cost two releases by hiding `Get-FileHash` from Windows
    /// PowerShell 5.1 on CI only (issue #191).
    ///
    /// What these tests CANNOT see, stated rather than papered over. They never touch the network, so
    /// they say NOTHING about whether either URL resolves today - the registry file reaches
    /// `agenteyes-app/main` through the ordinary public source sync and the plugin zips are uploaded
    /// to that repo's `plugins` release by hand; both are recorded as sequencing steps in the issue
    /// #186 handoff, not asserted here. The literal scan in section 4 answers "does the compiled app
    /// carry this string", not "can it ever produce this string": a URL assembled at run time from
    /// fragments that individually do not contain the searched text is invisible to it, which is what
    /// section 3 (the URL a request actually goes to) is for.
    /// </summary>
    public sealed class PluginRegistryChannelTests : IDisposable
    {
        /// <summary>The one URL this build is allowed to read the plugin catalog from.</summary>
        private const string ExpectedRegistryUrl =
            "https://raw.githubusercontent.com/thefrederiksen/agenteyes-app/main/plugins/registry.json";

        /// <summary>The prefix every plugin download must sit under.</summary>
        private const string ExpectedZipUrlPrefix =
            "https://github.com/thefrederiksen/agenteyes-app/releases/download/plugins/";

        /// <summary>The retired registry URL, kept here ONLY as the known-bad input every pin below is
        /// fired against. Nothing in the product may resolve to it.</summary>
        private const string RetiredRegistryUrl =
            "https://raw.githubusercontent.com/thefrederiksen/AgentEyes-releases/main/plugins/registry.json";

        /// <summary>The retired download URL, same role. `MyQuietShadow-releases` is the same
        /// repository under its pre-rename name - the live registry still spelled it that way - so a
        /// scan that only looked for the current name would have missed it.</summary>
        private const string RetiredZipUrl =
            "https://github.com/thefrederiksen/MyQuietShadow-releases/releases/download/plugins/qa-walk-companion-1.0.0.zip";

        private static readonly string[] RetiredRepoNames = { "AgentEyes-releases", "MyQuietShadow-releases" };

        private readonly List<string> _temps = new();

        public void Dispose()
        {
            PluginRegistry.DefaultTransport = null;
            foreach (string dir in _temps)
                try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-plugin-channel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _temps.Add(dir);
            return dir;
        }

        // ---- the two pins, each as ONE named operation ------------------------
        //
        // Naming them means the SAME comparison that passes for the real value below can be fired at
        // the retired one and shown to fail, instead of two similar-looking assertions written twice.

        private static void AssertIsTheRegistryUrl(string observedUrl) =>
            Assert.Equal(ExpectedRegistryUrl, observedUrl);

        /// <summary>Spelled with Assert.True and an explicit message rather than Assert.StartsWith
        /// because xUnit truncates the strings in a StartsWith failure, and the negative controls below
        /// read the failure message to show WHICH check fired.</summary>
        private static void AssertZipUrlIsOnTheConsolidatedRepo(string observedZipUrl) =>
            Assert.True(observedZipUrl.StartsWith(ExpectedZipUrlPrefix, StringComparison.Ordinal),
                $"Plugin download URL is not on the consolidated repo. Expected it to start with "
                + $"'{ExpectedZipUrlPrefix}', got '{observedZipUrl}'.");

        // ---- 1. the constants -------------------------------------------------

        [Fact]
        public void PluginRegistryChannel_IsPinnedToTheOneConsolidatedRepo()
        {
            Assert.Equal("thefrederiksen", PluginRegistry.Owner);
            Assert.Equal("agenteyes-app", PluginRegistry.Repo);
            Assert.Equal("plugins/registry.json", PluginRegistry.RegistryPath);
            AssertIsTheRegistryUrl(PluginRegistry.DefaultUrl);
        }

        [Fact]
        public void RegistryUrlPin_FiresWhenTheUrlIsTheRetiredRepo()
        {
            // The known-bad input: the URL this issue migrates OFF. If the pin let this through, it
            // would let any other silent re-point through too.
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertIsTheRegistryUrl(RetiredRegistryUrl));
            Assert.Contains("agenteyes-app", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ZipUrlPin_FiresWhenTheDownloadIsOnARetiredRepo()
        {
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertZipUrlIsOnTheConsolidatedRepo(RetiredZipUrl));
            Assert.Contains("agenteyes-app", failure.Message, StringComparison.Ordinal);
        }

        // ---- 2. the URL the production path resolves --------------------------

        [Fact]
        public void UrlFor_WithNoOverride_IsExactlyTheConsolidatedRegistryUrl()
        {
            // `new Config()` is what PluginCatalogDialog and PluginManagerWindow hand to UrlFor for
            // every user who has never set PluginRegistryUrl - which is every user.
            AssertIsTheRegistryUrl(PluginRegistry.UrlFor(new Config()));
            AssertIsTheRegistryUrl(PluginRegistry.UrlFor(new Config { PluginRegistryUrl = null }));
            AssertIsTheRegistryUrl(PluginRegistry.UrlFor(new Config { PluginRegistryUrl = "   " }));
        }

        [Fact]
        public void UrlForPin_FiresWhenTheResolvedUrlIsTheRetiredRepo()
        {
            // The negative control for the test above, driven through UrlFor itself rather than through
            // a bare string: a configured override is the one supported way to make UrlFor return
            // something else, so it is the closest reachable stand-in for a re-pointed default. A pin
            // that stayed green here would only ever have meant "this assertion compares nothing".
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertIsTheRegistryUrl(
                    PluginRegistry.UrlFor(new Config { PluginRegistryUrl = RetiredRegistryUrl })));
            Assert.Contains("agenteyes-app", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void UrlFor_WithAnOverride_StillUsesTheOverride()
        {
            // The pin above is about the DEFAULT, and says nothing about the documented override. Kept
            // explicit so the pin is not later "strengthened" into breaking a supported setting.
            Assert.Equal("https://example.invalid/registry.json",
                PluginRegistry.UrlFor(new Config { PluginRegistryUrl = "https://example.invalid/registry.json" }));
        }

        // ---- 3. the URL a request actually goes to ----------------------------

        [Fact]
        public async Task FetchAsync_RequestsExactlyTheConsolidatedUrl_AndParsesThisRepoRegistryFile()
        {
            // The strongest pin here: the file THIS repository publishes, served at the ONE URL the
            // app is allowed to ask for, parsed by the app's own parser. Everything else 404s, so a
            // wrong URL is a loud failure instead of a silent one.
            string registryJson = RepoSource.Read("plugins/registry.json");
            Assert.True(registryJson.Length > 100,
                "plugins/registry.json read as near-empty - a parse over nothing proves nothing.");

            var channel = new StubChannel();
            channel.Serve(ExpectedRegistryUrl, registryJson);
            PluginRegistry.DefaultTransport = channel;

            var plugins = await PluginRegistry.FetchAsync(new Config());

            Assert.Equal(new[] { ExpectedRegistryUrl }, channel.Requests.ToArray());
            AssertIsTheRegistryUrl(Assert.Single(channel.Requests));

            // The registry the app got back is the real one, not an empty list that would satisfy any
            // "no bad entry" assertion by finding nothing.
            Assert.Equal(2, plugins.Count);
            Assert.Equal(
                new[] { "doc-companion", "qa-walk-companion" },
                plugins.Select(p => p.Id).OrderBy(v => v, StringComparer.Ordinal).ToArray());

            foreach (var p in plugins)
            {
                AssertZipUrlIsOnTheConsolidatedRepo(p.ZipUrl);
                Assert.Equal(64, p.Sha256.Length);
            }
        }

        [Fact]
        public async Task FetchAsync_FailsLoudlyWhenOnlyTheRetiredUrlIsServed()
        {
            // The negative control for the test above: the identical setup with the registry moved to
            // the retired address. A build that still read the old repo would pass this; this one must
            // fail, and must name the URL it asked for so the failure is diagnosable.
            var channel = new StubChannel();
            channel.Serve(RetiredRegistryUrl, RepoSource.Read("plugins/registry.json"));
            PluginRegistry.DefaultTransport = channel;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginRegistry.FetchAsync(new Config()));

            Assert.Contains(ExpectedRegistryUrl, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(RetiredRegistryUrl, channel.Requests);
        }

        // ---- 4. the COMPILED app ----------------------------------------------

        [Fact]
        public void CompiledApp_CarriesTheConsolidatedRegistryUrlAndNoRetiredRepoLiteral()
        {
            // LIMIT, stated here rather than implied: a string assembled at run time from fragments
            // that individually do not contain the searched text is NOT seen. This answers "does the
            // compiled app carry this string", not "can it ever produce this string". Section 3 above
            // is what covers the URL a request actually goes to.
            string app = CompiledCode.AppAssembly;
            int total = CompiledCode.StringLiteralCount(app);
            Assert.True(total > 50, $"Only {total} string literals were read from {app} - the scanner is broken.");

            Assert.NotEmpty(CompiledCode.StringLiterals(app, v => v == ExpectedRegistryUrl));

            var offenders = CompiledCode.StringLiterals(app, IsRetiredRepoLiteral)
                .Select(o => $"{o.Method}: {o.Value}")
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();
            Assert.True(offenders.Length == 0,
                "The compiled app carries a retired repository name: " + string.Join("; ", offenders));
        }

        [Fact]
        public void CompiledRetiredRepoScan_FiresOnAKnownBadAssembly()
        {
            // The negative control for the scan above. THIS test assembly compiles RetiredRegistryUrl
            // and RetiredZipUrl into real ldstr instructions, so a scanner that reported nothing over
            // the app has to report something here - otherwise "no offending literal" would only ever
            // have meant "the scanner read nothing".
            var found = CompiledCode.StringLiterals(CompiledCode.TestAssembly, IsRetiredRepoLiteral);
            Assert.NotEmpty(found);
            Assert.Contains(found, f => f.Value == RetiredRegistryUrl);
            Assert.Contains(found, f => f.Value == RetiredZipUrl);
        }

        private static bool IsRetiredRepoLiteral(string value) =>
            RetiredRepoNames.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

        // ---- 5. the zipUrl a freshly packaged plugin actually gets -------------

        [Fact]
        public void PackagePlugin_GeneratesAZipUrlOnTheConsolidatedRepo()
        {
            // Read the name literally: this RUNS scripts/package-plugin.ps1 against this repository and
            // reads the `zipUrl` in the registry entry it prints. It does NOT reach the network, so it
            // does not claim that URL resolves today - the zips are uploaded to the agenteyes-app
            // `plugins` release by hand, and that step is recorded in the issue #186 handoff.
            //
            // It exists because the registry URL and the download URLs inside the registry are two
            // separate dependencies on the retired repo. Fixing PluginRegistry.cs alone would still
            // have had this script minting dead download URLs into every new registry entry.
            var entry = RunPackagePlugin(Path.Combine(RepoSource.Root, "scripts", "package-plugin.ps1"),
                "qa-walk-companion");

            Assert.Equal("qa-walk-companion", entry.GetProperty("id").GetString());
            Assert.Equal("1.0.1", entry.GetProperty("version").GetString());
            Assert.Equal(64, entry.GetProperty("sha256").GetString()!.Length);

            string zipUrl = entry.GetProperty("zipUrl").GetString()!;
            AssertZipUrlIsOnTheConsolidatedRepo(zipUrl);
            Assert.Equal(ExpectedZipUrlPrefix + "qa-walk-companion-1.0.1.zip", zipUrl);
        }

        [Fact]
        public void PackagePluginZipUrlCheck_FiresOnAScriptThatStillNamesTheRetiredRepo()
        {
            // The negative control for the test above, and it is a REAL RUN, not a re-assertion over a
            // string constant: a synthetic repo (scripts/ + plugins/qa-walk-companion/) holding a copy
            // of the script with line 53 put back the way it was before this issue. The same operation
            // over that script's real output must go red, otherwise the check above would only ever
            // have meant "this code ran a script and looked at nothing".
            string fakeRepo = NewTempDir();
            string scripts = Directory.CreateDirectory(Path.Combine(fakeRepo, "scripts")).FullName;
            string plugin = Directory.CreateDirectory(
                Path.Combine(fakeRepo, "plugins", "qa-walk-companion")).FullName;

            File.Copy(Path.Combine(RepoSource.Root, "plugins", "qa-walk-companion", "plugin.json"),
                Path.Combine(plugin, "plugin.json"));
            File.Copy(Path.Combine(RepoSource.Root, "plugins", "qa-walk-companion", "run.ps1"),
                Path.Combine(plugin, "run.ps1"));

            string mutated = Path.Combine(scripts, "package-plugin.ps1");
            string text = RepoSource.Read("scripts/package-plugin.ps1");
            string reverted = text.Replace(
                "https://github.com/thefrederiksen/agenteyes-app/releases/download/plugins/",
                "https://github.com/thefrederiksen/AgentEyes-releases/releases/download/plugins/");
            Assert.NotEqual(text, reverted);   // the mutation landed; otherwise this proves nothing
            File.WriteAllText(mutated, reverted);

            var entry = RunPackagePlugin(mutated, "qa-walk-companion");
            string zipUrl = entry.GetProperty("zipUrl").GetString()!;

            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertZipUrlIsOnTheConsolidatedRepo(zipUrl));
            Assert.Contains("agenteyes-app", failure.Message, StringComparison.Ordinal);
            Assert.Contains("AgentEyes-releases", zipUrl, StringComparison.Ordinal);
        }

        /// <summary>Run package-plugin.ps1 and return the registry entry it printed. Throws on a
        /// non-zero exit, on empty output, or when no JSON object was printed - so "the zipUrl looked
        /// fine" can never be produced by a run that did not happen.</summary>
        private JsonElement RunPackagePlugin(string scriptPath, string pluginId)
        {
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("package-plugin.ps1 is not where this test expects it", scriptPath);

            string outDir = NewTempDir();
            var psi = new ProcessStartInfo(WindowsPowerShellExe())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // PSModulePath is INHERITED, and inheriting it is what made these tests pass here and
            // fail on CI (issue #191). `Get-FileHash` is not a compiled-in cmdlet in Windows
            // PowerShell 5.1 - it is a FUNCTION exported by the Microsoft.PowerShell.Utility MODULE
            // under %SystemRoot%\System32\WindowsPowerShell\v1.0\Modules. CI runs `dotnet test` from
            // a `shell: pwsh` step, so the environment this process hands the child lists PowerShell
            // 7's $PSHOME\Modules and NOT Windows PowerShell's own; 5.1 then binds the name
            // Microsoft.PowerShell.Utility to PS7's Core-only copy and every function that module
            // ships disappears - while the engine's cmdlets (ConvertFrom-Json, Write-Output) keep
            // working, which is why the script gets all the way to the hash line before dying with
            // CommandNotFoundException. Pin the child to Windows PowerShell's own module path so what
            // it can resolve is stated here rather than borrowed from the shell that started the run.
            psi.Environment["PSModulePath"] = WindowsPowerShellModulePath();

            foreach (string arg in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", scriptPath, "-Id", pluginId, "-OutDir", outDir,
                     })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("powershell.exe did not start.");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("package-plugin.ps1 did not finish within 120s.");
            }
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"package-plugin.ps1 exited {proc.ExitCode}. stderr: {stderr}{Environment.NewLine}stdout: {stdout}");

            string zip = Path.Combine(outDir, $"{pluginId}-1.0.1.zip");
            if (!File.Exists(zip))
                throw new InvalidOperationException($"package-plugin.ps1 produced no zip at '{zip}'. stdout: {stdout}");

            int open = stdout.IndexOf('{');
            if (open < 0)
                throw new InvalidOperationException($"package-plugin.ps1 printed no registry entry. stdout: {stdout}");

            using var doc = JsonDocument.Parse(stdout.Substring(open));
            return doc.RootElement.Clone();
        }

        /// <summary>Windows PowerShell 5.1 itself, by absolute path rather than off PATH. Same idea as
        /// the module path below: which shell runs the script is STATED here, not taken from the
        /// environment of whatever started the test run.</summary>
        private static string WindowsPowerShellExe()
        {
            string exe = Path.Combine(WindowsPowerShellHome(), "powershell.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException(
                    "Windows PowerShell 5.1 is not where package-plugin.ps1 has to run from.", exe);
            return exe;
        }

        /// <summary>The PSModulePath a Windows PowerShell 5.1 child needs: its own module directory
        /// first, then the machine-wide one. It REPLACES the inherited value rather than extending it
        /// - a PowerShell 7 entry left in front of these would take the Microsoft.PowerShell.Utility
        /// name back off them, which is the whole failure (issue #191).</summary>
        private static string WindowsPowerShellModulePath()
        {
            string systemModules = Path.Combine(WindowsPowerShellHome(), "Modules");
            if (!Directory.Exists(systemModules))
                throw new DirectoryNotFoundException(
                    $"Windows PowerShell's module directory is missing at '{systemModules}'. " +
                    "package-plugin.ps1 needs it for Get-FileHash and Compress-Archive.");

            string machineModules = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules");
            return systemModules + ";" + machineModules;
        }

        private static string WindowsPowerShellHome() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");

        // ---- 6. the environment the packaging run inherits (issue #191) -------

        /// <summary>PowerShell 7's Microsoft.PowerShell.Utility manifest, cut to the fields that do the
        /// damage: the same module NAME and GUID Windows PowerShell 5.1 uses, marked Core-only, and
        /// claiming the Utility commands as CMDLETS - `Get-FileHash` among them, which 5.1 only has as
        /// a module FUNCTION. A directory holding this is what a `shell: pwsh` step puts in front of
        /// Windows PowerShell's own modules on PSModulePath.</summary>
        private const string PowerShell7UtilityManifest = @"@{
GUID = '1DA87E53-152B-403E-98DC-74D7B4D63D59'
Author = 'PowerShell'
ModuleVersion = '7.0.0.0'
CompatiblePSEditions = @('Core')
PowerShellVersion = '3.0'
CmdletsToExport = @('Get-FileHash', 'New-Guid', 'Format-Hex', 'ConvertFrom-Json', 'Out-String', 'Select-Object', 'Write-Output')
FunctionsToExport = @()
AliasesToExport = @('fhx')
NestedModules = @('Microsoft.PowerShell.Commands.Utility.dll')
}
";

        [Fact]
        public void PackagePlugin_TheParentProcessCarriesAPowerShell7ModulePath_StillPackages()
        {
            // The regression test for the CI-only failure in issue #191. `dotnet test` runs from a
            // `shell: pwsh` step on the runner, so the process that starts package-plugin.ps1 exports
            // PowerShell 7's PSModulePath. Reproduced here with a PS7-shaped module tree and no
            // Windows PowerShell entry at all - the shape that took `Get-FileHash` away on CI. A real
            // PowerShell 7 install reproduces the CI failure end to end and is recorded in the issue
            // #191 handoff; the synthetic tree is what lets this check run on a machine without one.
            string powerShell7Modules = NewPowerShell7ModulePath();

            // The control, and it comes FIRST: that environment has to genuinely break a child which
            // inherits it, or the packaging run below would only ever have meant "nothing was wrong".
            Assert.False(ChildResolvesGetFileHash(powerShell7Modules));
            Assert.True(ChildResolvesGetFileHash(WindowsPowerShellModulePath()));

            // Poisoning THIS process's environment is what makes the run below inherit the CI value -
            // ProcessStartInfo copies the environment at construction. No other test in the suite
            // spawns a PowerShell, and this class's tests do not run concurrently with each other, so
            // the window is confined to the run below; it is restored in the finally either way.
            string? restore = Environment.GetEnvironmentVariable("PSModulePath");
            try
            {
                Environment.SetEnvironmentVariable("PSModulePath", powerShell7Modules);

                var entry = RunPackagePlugin(Path.Combine(RepoSource.Root, "scripts", "package-plugin.ps1"),
                    "qa-walk-companion");

                Assert.Equal(ExpectedZipUrlPrefix + "qa-walk-companion-1.0.1.zip",
                    entry.GetProperty("zipUrl").GetString());
                Assert.Equal(64, entry.GetProperty("sha256").GetString()!.Length);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PSModulePath", restore);
            }
        }

        /// <summary>A temp directory shaped like PowerShell 7's module tree: one
        /// Microsoft.PowerShell.Utility folder holding <see cref="PowerShell7UtilityManifest"/>, and
        /// nothing of Windows PowerShell's.</summary>
        private string NewPowerShell7ModulePath()
        {
            string root = NewTempDir();
            string module = Directory.CreateDirectory(
                Path.Combine(root, "Microsoft.PowerShell.Utility")).FullName;
            File.WriteAllText(Path.Combine(module, "Microsoft.PowerShell.Utility.psd1"),
                PowerShell7UtilityManifest);
            return root;
        }

        /// <summary>Start Windows PowerShell with this PSModulePath and report whether `Get-FileHash`
        /// resolves in it. Throws when the child does not run or answers neither way, so a false can
        /// only ever come from a shell that really did start and really could not find the command.</summary>
        private static bool ChildResolvesGetFileHash(string psModulePath)
        {
            var psi = new ProcessStartInfo(WindowsPowerShellExe())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["PSModulePath"] = psModulePath;
            foreach (string arg in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                         "if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) { 'RESOLVED' } else { 'MISSING' }",
                     })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("powershell.exe did not start.");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("powershell.exe did not answer within 60s.");
            }

            if (stdout.Contains("RESOLVED", StringComparison.Ordinal)) return true;
            if (stdout.Contains("MISSING", StringComparison.Ordinal)) return false;
            throw new InvalidOperationException(
                $"powershell.exe answered neither RESOLVED nor MISSING. stdout: {stdout}{Environment.NewLine}stderr: {stderr}");
        }

        /// <summary>
        /// A transport that answers ONLY the URLs it was explicitly given and 404s everything else,
        /// recording every URL asked for. Both halves matter: the recording is what lets a test say
        /// WHICH URL was read, and the 404 is what makes a wrong URL a loud failure instead of a
        /// silent one.
        /// </summary>
        private sealed class StubChannel : HttpMessageHandler
        {
            private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

            public List<string> Requests { get; } = new();

            public void Serve(string url, string text) => _content[url] = Encoding.UTF8.GetBytes(text);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string url = request.RequestUri!.ToString();
                Requests.Add(url);
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
