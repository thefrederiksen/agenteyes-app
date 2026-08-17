namespace AgentEyes.Setup.Engine;

/// <summary>The outcome of applying one plan item.</summary>
public enum ApplyStatus { Installed, Updated, Failed }

/// <summary>Result of applying one component.</summary>
public sealed record ApplyResult(
    string ComponentId,
    ApplyStatus Status,
    string? FromVersion,
    string? ToVersion,
    string? Error,
    string? BackupPath);

/// <summary>Result of an entire update/install run.</summary>
public sealed class UpdateRunResult
{
    public required IReadOnlyList<ApplyResult> Results { get; init; }
    public int Installed => Results.Count(r => r.Status == ApplyStatus.Installed);
    public int Updated => Results.Count(r => r.Status == ApplyStatus.Updated);
    public int Failed => Results.Count(r => r.Status == ApplyStatus.Failed);
}

/// <summary>
/// Executes an <see cref="UpdatePlan"/>: for each actionable item, download the
/// asset, verify its SHA-256 against the manifest, then swap it into place
/// (keeping a .old backup). Downloading is injected as a delegate so the whole
/// flow is testable without a network: production passes a delegate backed by
/// the GitHub release download; tests pass one that produces a local file.
///
/// Single-file assets (the app, agenteyes, agenteyes-setup) are placed directly. Archive
/// assets (the ffmpeg .zip) are extracted into the app dir via
/// <see cref="ArchiveInstaller"/>.
/// </summary>
public sealed class UpdateRunner
{
    /// <summary>Downloads the asset for a plan item and returns the local staged file path.</summary>
    public delegate Task<string> Downloader(PlanItem item, CancellationToken ct);

    private readonly InstallLayout _layout;
    private readonly IReadOnlyDictionary<string, Component> _componentsById;
    private readonly Downloader _download;

    public UpdateRunner(InstallLayout layout, IEnumerable<Component> components, Downloader download)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentNullException.ThrowIfNull(components);
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _componentsById = components.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<UpdateRunResult> ApplyAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ApplyResult>();

        foreach (var item in plan.Actionable)
        {
            if (!_componentsById.TryGetValue(item.ComponentId, out var component))
            {
                results.Add(new ApplyResult(item.ComponentId, ApplyStatus.Failed, item.FromVersion, item.ToVersion,
                    "Component not in scope.", null));
                continue;
            }

            results.Add(await ApplyOneAsync(item, component, ct));
        }

        RecordInstalledVersions(results);

        EngineLog.Write($"[UpdateRunner] ApplyAsync done: installed={results.Count(r => r.Status == ApplyStatus.Installed)}, " +
                        $"updated={results.Count(r => r.Status == ApplyStatus.Updated)}, " +
                        $"failed={results.Count(r => r.Status == ApplyStatus.Failed)}");
        return new UpdateRunResult { Results = results };
    }

    private async Task<ApplyResult> ApplyOneAsync(PlanItem item, Component component, CancellationToken ct)
    {
        var wasPresent = item.Kind == PlanItemKind.Update;
        try
        {
            var staged = await _download(item, ct);
            if (!File.Exists(staged))
                return Fail(item, "Download produced no file.");

            if (!Hashing.Sha256Matches(staged, item.Sha256))
            {
                EngineLog.Write($"[UpdateRunner] {item.ComponentId}: SHA-256 mismatch; rejecting.");
                TryDelete(staged);
                return Fail(item, "SHA-256 mismatch; download rejected.");
            }

            string? backup = null;
            if (item.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ArchiveInstaller.Place(_layout.AppDir, staged);
            }
            else
            {
                var target = _layout.PathFor(component);
                backup = InstallSwapper.Place(target, staged);
            }
            TryDelete(staged);

            var status = wasPresent ? ApplyStatus.Updated : ApplyStatus.Installed;
            return new ApplyResult(item.ComponentId, status, item.FromVersion, item.ToVersion, null, backup);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[UpdateRunner] {item.ComponentId} FAILED: {ex.Message}");
            return Fail(item, ex.Message);
        }
    }

    /// <summary>
    /// Persist the version we just placed for each successfully installed/updated component, so the
    /// planner has a reliable installed version next time (esp. ffmpeg, whose vendor file-version
    /// stamp is unrelated to our manifest version). Best-effort: a bookkeeping write must never
    /// fail a good install.
    /// </summary>
    private void RecordInstalledVersions(IReadOnlyList<ApplyResult> results)
    {
        try
        {
            var manifest = InstalledManifest.Load(_layout);
            var changed = false;
            foreach (var r in results)
            {
                if (r.Status is not (ApplyStatus.Installed or ApplyStatus.Updated)) continue;
                var version = r.ToVersion;
                if (string.IsNullOrWhiteSpace(version)) continue;
                manifest.Set(r.ComponentId, version);
                changed = true;
            }
            if (changed) manifest.Save(_layout);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[UpdateRunner] recording installed versions failed: {ex.Message}");
        }
    }

    private static ApplyResult Fail(PlanItem item, string error) =>
        new(item.ComponentId, ApplyStatus.Failed, item.FromVersion, item.ToVersion, error, null);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { EngineLog.Write($"[UpdateRunner] cleanup delete failed for {path}: {ex.Message}"); }
    }
}
