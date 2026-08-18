using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Plugin registry + auto-download (issue #32). The registry is a JSON file at a
    /// URL (default: plugins/registry.json on the main branch of the one consolidated
    /// repo, thefrederiksen/agenteyes-app) listing installable plugins; each entry
    /// carries a zip URL and its SHA-256. Install = download, verify the hash, extract
    /// to the plugins folder. No hash match, no install - stated plainly, never
    /// silently skipped (no-fallback rule).
    /// </summary>
    internal sealed class RegistryPlugin
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "";
        public string ZipUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    internal static class PluginRegistry
    {
        /// <summary>The GitHub owner the plugin registry is served from. Pinned by
        /// PluginRegistryChannelTests.</summary>
        public const string Owner = "thefrederiksen";

        /// <summary>
        /// The ONE repo the plugin registry is served from (issue #186) - the same consolidated repo
        /// the update channel reads (<see cref="AgentEyes.Setup.Engine.ReleaseSource.Repo"/>). The
        /// registry used to be hand-maintained on the binaries-only thefrederiksen/AgentEyes-releases
        /// repo, which is retired and is being DELETED; the file now lives in this repository at
        /// plugins/registry.json and reaches the public repo through the ordinary source sync.
        ///
        /// Changing this value RE-POINTS WHERE EVERY INSTALLED COPY GETS ITS PLUGINS, and a wrong
        /// value does not fail loudly - GitHub keeps serving whatever is at the old address, so the
        /// catalog would silently freeze. That is why it is pinned by PluginRegistryChannelTests: a
        /// silent edit fails the build, not a user's catalog months later.
        /// </summary>
        public const string Repo = "agenteyes-app";

        /// <summary>The registry file's path on <see cref="Repo"/>'s main branch.</summary>
        public const string RegistryPath = "plugins/registry.json";

        /// <summary>
        /// The one URL the app reads the plugin catalog from, in full. Exposed so a test can pin the
        /// whole URL rather than its pieces, and so there is exactly one place it is spelled.
        /// </summary>
        public const string DefaultUrl =
            "https://raw.githubusercontent.com/" + Owner + "/" + Repo + "/main/" + RegistryPath;

        /// <summary>
        /// The transport the registry sends through. Production NEVER assigns this: it stays null and
        /// HttpClient uses its own platform transport.
        ///
        /// It exists so a test can observe WHICH URL the registry actually requests, on the same code
        /// path production takes. Pinning only the constant is not enough - the #184 review gate
        /// demonstrated a re-point that left every constant test green because no test ever watched a
        /// request go out. ReleaseSource.DefaultTransport is the same seam for the update channel.
        /// </summary>
        internal static HttpMessageHandler? DefaultTransport;

        /// <summary>The client every fetch and every download uses. One statement, one timeout, one
        /// user agent: the only thing that ever differs is the transport underneath it.</summary>
        private static HttpClient NewClient()
        {
            var c = new HttpClient(DefaultTransport ?? new HttpClientHandler(), disposeHandler: DefaultTransport is null)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AgentEyes");
            return c;
        }

        public static string UrlFor(Config cfg) =>
            string.IsNullOrWhiteSpace(cfg.PluginRegistryUrl) ? DefaultUrl : cfg.PluginRegistryUrl!;

        /// <summary>Fetch and parse the registry. Throws with a clear message on any
        /// failure (unreachable, bad JSON) - the caller shows it, never hides it.</summary>
        public static async Task<List<RegistryPlugin>> FetchAsync(Config cfg)
        {
            string url = UrlFor(cfg);
            string json;
            using var http = NewClient();
            try { json = await http.GetStringAsync(url); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not reach the plugin registry at {url}: {ex.Message}");
            }

            try
            {
                var doc = JsonSerializer.Deserialize<RegistryDoc>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var list = doc?.Plugins ?? new List<RegistryPlugin>();
                foreach (var p in list)
                    if (p.Id.Length == 0 || p.ZipUrl.Length == 0 || p.Sha256.Length == 0)
                        throw new InvalidOperationException($"registry entry '{p.Id}' is missing id, zipUrl or sha256");
                return list;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"The plugin registry at {url} is not valid JSON: {ex.Message}");
            }
        }

        /// <summary>Download, SHA-256-verify and extract one plugin. Replaces any
        /// existing install of the same id. Throws with the exact reason on failure.
        /// The download-and-place logic is shared with local installs via PluginPackage.</summary>
        public static async Task InstallAsync(RegistryPlugin plugin)
        {
            byte[] zip;
            using var http = NewClient();
            try { zip = await http.GetByteArrayAsync(plugin.ZipUrl); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Download failed for {plugin.Id} ({plugin.ZipUrl}): {ex.Message}");
            }

            string id = AgentEyes.Plugins.PluginPackage.InstallZip(zip, Plugins.Root, plugin.Sha256);
            Log.Info($"plugin registry: installed {id} v{plugin.Version}");
        }

        /// <summary>Installed version for an id, or null when not installed.</summary>
        public static string? InstalledVersion(string id) =>
            Plugins.Load().FirstOrDefault(p => p.Id == id)?.Version;

        /// <summary>True when the registry version is newer than the installed one.
        /// Falls back to ordinal inequality for non-semver strings.</summary>
        public static bool IsUpdate(string installed, string registry)
        {
            if (Version.TryParse(installed, out var i) && Version.TryParse(registry, out var r)) return r > i;
            return !string.Equals(installed, registry, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class RegistryDoc
        {
            public List<RegistryPlugin> Plugins { get; set; } = new();
        }
    }
}
