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
    /// URL (default: the public AgentEyes-releases repo) listing installable
    /// plugins; each entry carries a zip URL and its SHA-256. Install = download,
    /// verify the hash, extract to the plugins folder. No hash match, no install -
    /// stated plainly, never silently skipped (no-fallback rule).
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
        public const string DefaultUrl =
            "https://raw.githubusercontent.com/thefrederiksen/AgentEyes-releases/main/plugins/registry.json";

        private static readonly HttpClient Http = Create();

        private static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
            try { json = await Http.GetStringAsync(url); }
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
            try { zip = await Http.GetByteArrayAsync(plugin.ZipUrl); }
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
