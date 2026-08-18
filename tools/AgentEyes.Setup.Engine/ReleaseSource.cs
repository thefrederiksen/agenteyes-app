using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

// UpdateChannelTests substitutes ONLY the transport of the DEFAULT-constructed ReleaseSource
// (see ReleaseSource.DefaultTransport), so the construction path every production caller takes is
// the path under test. Nothing else in the engine is exposed by this.
[assembly: InternalsVisibleTo("AgentEyes.Tests")]

namespace AgentEyes.Setup.Engine;

/// <summary>A manifest plus the per-asset download URLs needed to fetch each asset.</summary>
public sealed record ResolvedRelease(ReleaseManifest Manifest, IReadOnlyDictionary<string, string> DownloadUrls);

/// <summary>
/// Resolves the release a plan is computed against. Shared by all front-ends
/// (the wizard, the CLI, the in-app updater). Three modes:
///   - a local manifest file (LoadLocalManifest): plan/dry-run only, no URLs.
///   - a local release directory (LoadLocalReleaseDir): offline install/update.
///   - "latest" (FetchLatestAsync): the GitHub latest release, mapping asset
///     download URLs and parsing release-manifest.json.
/// </summary>
public sealed class ReleaseSource
{
    /// <summary>The GitHub owner of the update channel. Pinned by UpdateChannelTests.</summary>
    public const string Owner = "thefrederiksen";

    /// <summary>
    /// The ONE repo AgentEyes is developed in AND released from (issue #184). The source used to be
    /// private, which is the only reason releases ever lived in a separate binaries-only repo; the
    /// source is public now, so the release is cut from the repo whose source is being built and the
    /// installer/updater still fetch it anonymously.
    ///
    /// Changing this value RE-POINTS EVERY INSTALLED COPY of AgentEyes, so it is pinned by
    /// UpdateChannelTests: a silent edit fails the build, not a user's update check months later.
    /// </summary>
    public const string Repo = "agenteyes-app";

    private const string ManifestAssetName = "release-manifest.json";

    /// <summary>
    /// The one URL the updater asks "what is the latest release" - the update channel, in full.
    /// Exposed so a test can pin the whole URL rather than its pieces, and so there is exactly one
    /// place the channel is spelled.
    /// </summary>
    public static string LatestReleaseUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// The transport a DEFAULT-constructed ReleaseSource sends through - the object every production
    /// caller ends up with, because the app updater, the setup CLI and the setup wizard all write
    /// `new ReleaseSource()` with no argument.
    ///
    /// Production NEVER assigns this: it stays null and HttpClient uses its own platform transport.
    /// It exists because a test that hands in its own HttpClient proves only the injected path. The
    /// independent review gate demonstrated exactly that gap - it made the retired channel selected
    /// only when `http is null`, and all sixteen channel tests stayed green, because not one of them
    /// ever ran the constructor production runs. Substituting only the transport keeps the default
    /// construction path itself under test.
    /// </summary>
    internal static HttpMessageHandler? DefaultTransport;

    private readonly HttpClient _http;

    public ReleaseSource(HttpClient? http = null)
    {
        _http = http ?? NewDefaultClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("agenteyes-setup");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>The client every caller that writes `new ReleaseSource()` gets. One statement, one
    /// timeout, one place: the only thing that ever differs is the transport underneath it.</summary>
    private static HttpClient NewDefaultClient() =>
        new(DefaultTransport ?? new HttpClientHandler(), disposeHandler: DefaultTransport is null)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

    public static ResolvedRelease LoadLocalManifest(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest file not found: {path}", path);
        var manifest = ReleaseManifest.Parse(File.ReadAllText(path));
        return new ResolvedRelease(manifest, new Dictionary<string, string>());
    }

    /// <summary>
    /// Treat a local directory as a release: it must contain release-manifest.json
    /// plus each asset file. The "download URL" for an asset is its local file
    /// path, so a full install/update can run offline with no network and no admin.
    /// Used by the local build script's verify step and for hermetic testing.
    /// </summary>
    public static ResolvedRelease LoadLocalReleaseDir(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestAssetName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No {ManifestAssetName} in release dir: {dir}", manifestPath);

        var manifest = ReleaseManifest.Parse(File.ReadAllText(manifestPath));
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetName in manifest.Assets.Keys)
        {
            var assetPath = Path.Combine(dir, assetName);
            if (File.Exists(assetPath)) urls[assetName] = assetPath; // local path acts as the URL
        }
        return new ResolvedRelease(manifest, urls);
    }

    public async Task<ResolvedRelease> FetchLatestAsync(CancellationToken ct)
    {
        var url = LatestReleaseUrl;
        EngineLog.Write($"[ReleaseSource] FetchLatestAsync: channel={url}");
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        string? manifestUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;
                urls[name] = dl;
                if (name == ManifestAssetName) manifestUrl = dl;
            }
        }

        if (manifestUrl is null)
            throw new InvalidOperationException($"Latest release has no {ManifestAssetName} asset.");

        var manifestJson = await _http.GetStringAsync(manifestUrl, ct);
        var manifest = ReleaseManifest.Parse(manifestJson);
        return new ResolvedRelease(manifest, urls);
    }

    /// <summary>
    /// Stage an asset to a temp file and return its path. The resolved value is
    /// either an http(s) URL (latest/online mode) or a local file path
    /// (release-dir mode); each is handled explicitly. Throws when no source is
    /// known for the asset.
    /// </summary>
    public async Task<string> DownloadAssetAsync(string assetName, IReadOnlyDictionary<string, string> urls, CancellationToken ct)
    {
        if (!urls.TryGetValue(assetName, out var source))
            throw new InvalidOperationException($"No source for asset '{assetName}'. Use latest or a release dir.");

        var dest = Path.Combine(Path.GetTempPath(), $"agenteyes-setup-{Guid.NewGuid():N}-{assetName}");

        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(dest);
            await src.CopyToAsync(fs, ct);
            return dest;
        }

        // Local release-dir mode: the source is a file path; stage a copy.
        if (!File.Exists(source))
            throw new FileNotFoundException($"Local asset not found: {source}", source);
        File.Copy(source, dest, overwrite: true);
        return dest;
    }
}
