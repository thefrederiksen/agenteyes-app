using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AgentEyes.Plugins;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Proves the PUBLISHED plugin assets against the registry that names them (issue #1).
    ///
    /// PluginRegistryChannelTests states its own limit plainly: it never touches the network, so it
    /// says nothing about whether the hash pinned in plugins/registry.json matches the bytes GitHub
    /// actually serves. That gap is exactly how the 1.0.0 zips went stale - source moved on (the
    /// DevThrottle account switch, issue #88), the published artifacts did not, and nothing went red.
    /// This file closes the gap deliberately: it DOWNLOADS each asset the registry points at and pins
    ///   1. the sha256 of the published bytes to the registry entry (the hash the installer verifies),
    ///   2. the end-to-end install path over those real bytes (PluginPackage.InstallZip with the
    ///      registry hash - the check a wrong hash breaks),
    ///   3. the published run.ps1 and plugin.json CONTENT to this repository's source, so a source
    ///      edit that is not followed by a re-cut + registry update goes red here instead of shipping
    ///      silently stale artifacts again,
    ///   4. the absence of the pre-rename credential path (MyQuietShadow / a bare OpenAI key read)
    ///      in what is actually served to users,
    ///   5. that the `plugins` release has not captured /releases/latest, which the in-app updater
    ///      reads from this same repo (issues #184/#186).
    ///
    /// These tests need github.com to be reachable. That is the point - they verify the published
    /// state of the world, and a run that cannot reach it FAILS LOUDLY naming the URL rather than
    /// passing over nothing (a network check that skips itself when offline certifies a download
    /// that never happened). Each asset is a few KB and is downloaded once per run via a static
    /// cache, so the cost is one small HTTP round trip per plugin.
    /// </summary>
    public sealed class PublishedPluginAssetTests : IDisposable
    {
        /// <summary>Downloaded bytes per URL, shared across the tests in this run so each published
        /// asset is fetched exactly once no matter how many pins read it.</summary>
        private static readonly ConcurrentDictionary<string, byte[]> Downloads = new(StringComparer.Ordinal);

        private readonly List<string> _temps = new();

        public void Dispose()
        {
            foreach (string dir in _temps)
                try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "agenteyes-published-plugin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _temps.Add(dir);
            return dir;
        }

        // ---- the registry entries under test ----------------------------------

        private sealed record RegistryEntry(string Id, string Version, string ZipUrl, string Sha256);

        /// <summary>The entries in THIS repository's plugins/registry.json. Asserts the expected two
        /// plugins are present, so no pin below can pass by iterating an empty list.</summary>
        private static List<RegistryEntry> ReadRegistry()
        {
            using var doc = JsonDocument.Parse(RepoSource.Read("plugins/registry.json"));
            var entries = doc.RootElement.GetProperty("plugins").EnumerateArray()
                .Select(p => new RegistryEntry(
                    p.GetProperty("id").GetString()!,
                    p.GetProperty("version").GetString()!,
                    p.GetProperty("zipUrl").GetString()!,
                    p.GetProperty("sha256").GetString()!))
                .ToList();

            Assert.Equal(
                new[] { "doc-companion", "qa-walk-companion" },
                entries.Select(e => e.Id).OrderBy(v => v, StringComparer.Ordinal).ToArray());
            return entries;
        }

        /// <summary>Download one published asset (cached per run). Throws naming the URL on any
        /// failure, so "the hash matched" can never be produced by a download that did not happen.</summary>
        private static byte[] DownloadPublished(string url)
        {
            return Downloads.GetOrAdd(url, u =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentEyes-Tests");
                try
                {
                    byte[] bytes = http.GetByteArrayAsync(u).GetAwaiter().GetResult();
                    Assert.True(bytes.Length > 1000,
                        $"The published asset at {u} came back suspiciously small ({bytes.Length} bytes) - "
                        + "a hash over an error page proves nothing.");
                    return bytes;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Could not download the published plugin asset at {u}: {ex.Message}. "
                        + "Either github.com is unreachable from this machine, or the asset the registry "
                        + "points at was never published to the 'plugins' release.");
                }
            });
        }

        /// <summary>Read one entry's text out of a plugin zip's bytes. Throws when it is absent, so a
        /// content check cannot pass by comparing nothing.</summary>
        private static string ReadZipEntryText(byte[] zipBytes, string entryName)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidOperationException($"the published zip has no '{entryName}' entry.");
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        /// <summary>Line-ending-normalized comparison text: content drift is real drift, but a
        /// checkout's autocrlf setting is not.</summary>
        private static string Normalize(string text) => text.Replace("\r\n", "\n");

        // ---- the stale-credential scan, as ONE named operation ------------------
        //
        // Named so the same scan that passes over the published scripts can be fired at the known-bad
        // pre-rename text below and shown to fail.

        private static readonly string[] StaleCredentialMarkers = { "MyQuietShadow", "openai" };

        private static void AssertCarriesNoStaleCredentialPath(string pluginId, string scriptText)
        {
            var hits = StaleCredentialMarkers
                .Where(m => scriptText.Contains(m, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(hits.Length == 0,
                $"The published {pluginId} script still carries a pre-rename credential marker: "
                + string.Join(", ", hits) + ".");
        }

        // ---- 1 + 2. the published bytes hash to the registry pin and install ----

        [Fact]
        public void PublishedAssets_HashToTheRegistryPins_AndInstallEndToEnd()
        {
            foreach (var entry in ReadRegistry())
            {
                byte[] zip = DownloadPublished(entry.ZipUrl);

                // The hash pin: the sha256 in plugins/registry.json against the bytes GitHub serves.
                string actual = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
                Assert.True(string.Equals(entry.Sha256, actual, StringComparison.OrdinalIgnoreCase),
                    $"Registry hash mismatch for {entry.Id}: plugins/registry.json pins {entry.Sha256} "
                    + $"but the asset at {entry.ZipUrl} hashes to {actual}. The published artifact and "
                    + "the registry have diverged - re-cut and update them in the same change.");

                // The end-to-end install, over the REAL published bytes with the REAL registry hash -
                // the exact code path a registry install takes (PluginRegistry.InstallAsync delegates
                // here after its download).
                string root = NewTempDir();
                string installedId = PluginPackage.InstallZip(zip, root, entry.Sha256);
                Assert.Equal(entry.Id, installedId);
                Assert.True(File.Exists(Path.Combine(root, entry.Id, "plugin.json")));
                Assert.True(File.Exists(Path.Combine(root, entry.Id, "run.ps1")));

                // The installed manifest agrees with the registry about what was just installed.
                using var manifest = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(root, entry.Id, "plugin.json")));
                Assert.Equal(entry.Id, manifest.RootElement.GetProperty("id").GetString());
                Assert.Equal(entry.Version, manifest.RootElement.GetProperty("version").GetString());
            }
        }

        [Fact]
        public void InstallZip_WrongRegistryHash_RefusesToInstall()
        {
            // The negative control for the pin above, and it needs no network: bytes that do NOT hash
            // to a registry pin must be refused. If this passed, "the hash check passed" above would
            // only ever have meant "no comparison happened".
            var entry = ReadRegistry()[0];
            byte[] notTheAsset = { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => PluginPackage.InstallZip(notTheAsset, NewTempDir(), entry.Sha256));
            Assert.Contains("SHA-256 mismatch", ex.Message, StringComparison.Ordinal);
        }

        // ---- 3 + 4. the published content is this repository's source -----------

        [Fact]
        public void PublishedScripts_MatchRepoSource_AndCarryNoStaleCredentialPath()
        {
            foreach (var entry in ReadRegistry())
            {
                byte[] zip = DownloadPublished(entry.ZipUrl);

                foreach (string file in new[] { "run.ps1", "plugin.json" })
                {
                    string published = ReadZipEntryText(zip, file);
                    string source = RepoSource.Read($"plugins/{entry.Id}/{file}");
                    Assert.True(Normalize(published) == Normalize(source),
                        $"The published {entry.Id}/{file} does not match plugins/{entry.Id}/{file} in "
                        + "this repository - the artifact and the source have diverged again. Re-cut "
                        + "the plugin and update plugins/registry.json in the same change.");
                }

                // The specific staleness that shipped in 1.0.0: a pre-rename credential path and a
                // bare OpenAI key read. Asserted over the PUBLISHED text, not the source, because the
                // published text is what users run.
                string runPs1 = ReadZipEntryText(zip, "run.ps1");
                Assert.Contains("DEVTHROTTLE_API_KEY", runPs1, StringComparison.Ordinal);
                AssertCarriesNoStaleCredentialPath(entry.Id, runPs1);
            }
        }

        [Fact]
        public void StaleCredentialScan_FiresOnThePreRenameScript()
        {
            // The negative control: the shape of the 1.0.0 doc-companion script this issue exists to
            // retire. The same scan that passes over the published scripts must go red here, or "no
            // stale marker" above would only ever have meant "the scan looked at nothing".
            const string preRename =
                "$cfg = Get-Content \"$env:LOCALAPPDATA\\MyQuietShadow\\config.json\" | ConvertFrom-Json\n"
                + "$key = $cfg.openAiApiKey\n";

            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertCarriesNoStaleCredentialPath("doc-companion", preRename));
            Assert.Contains("MyQuietShadow", failure.Message, StringComparison.Ordinal);
        }

        // ---- 5. the plugins release must not capture /releases/latest -----------

        /// <summary>The one assertion, named so the negative control below can fire the identical
        /// comparison at the known-bad value.</summary>
        private static void AssertLatestIsNotThePluginsRelease(string latestLocation)
        {
            Assert.Contains("/releases/tag/", latestLocation, StringComparison.Ordinal);
            Assert.True(!latestLocation.TrimEnd('/').EndsWith("/releases/tag/plugins", StringComparison.OrdinalIgnoreCase),
                $"/releases/latest resolves to the 'plugins' release ({latestLocation}) - the plugins "
                + "release has captured the latest flag and the in-app updater will read plugin zips "
                + "as an app release. Repin the app release: gh release edit vX.Y.Z --latest.");
        }

        [Fact]
        public async Task ReleasesLatest_IsNotThePluginsRelease()
        {
            // github.com/<repo>/releases/latest answers with a redirect to the tag GitHub considers
            // "Latest" - the same notion the updater's API call reads, without the API rate limit.
            // AllowAutoRedirect=false so the Location header IS the answer.
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentEyes-Tests");

            const string url = "https://github.com/thefrederiksen/agenteyes-app/releases/latest";
            HttpResponseMessage resp;
            try { resp = await http.GetAsync(url); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException(
                    $"Could not reach {url}: {ex.Message}. github.com must be reachable for this pin.");
            }

            using (resp)
            {
                Assert.True((int)resp.StatusCode is >= 300 and < 400,
                    $"{url} did not redirect (HTTP {(int)resp.StatusCode}) - with no redirect there is "
                    + "no latest tag to read, and a pin over nothing proves nothing.");
                string location = resp.Headers.Location?.ToString()
                    ?? throw new InvalidOperationException($"{url} redirected without a Location header.");
                AssertLatestIsNotThePluginsRelease(location);
            }
        }

        [Fact]
        public void LatestReleasePin_FiresWhenLatestIsThePluginsRelease()
        {
            // The negative control, no network needed: the exact Location a captured latest flag
            // would produce must trip the pin above.
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertLatestIsNotThePluginsRelease(
                    "https://github.com/thefrederiksen/agenteyes-app/releases/tag/plugins"));
            Assert.Contains("captured the latest flag", failure.Message, StringComparison.Ordinal);
        }
    }
}
