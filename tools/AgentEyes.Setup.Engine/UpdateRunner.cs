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

/// <summary>One plan item downloaded and verified, waiting to be swapped in.</summary>
public sealed record StagedItem(PlanItem Item, Component Component, string StagedPath);

/// <summary>
/// Every actionable item of a plan, downloaded and SHA-256 verified, and nothing installed touched
/// yet (issue #86 review, B1a). Handed from <see cref="UpdateRunner.StageAsync"/> to
/// <see cref="UpdateRunner.Swap"/> with the running app stopped in between. Disposing it deletes the
/// staged files - after a swap they are already consumed, and after a failure before the swap they
/// are just temp files.
/// </summary>
public sealed class StagedUpdate : IDisposable
{
    public StagedUpdate(IReadOnlyList<StagedItem> items)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public IReadOnlyList<StagedItem> Items { get; }

    public void Dispose()
    {
        foreach (var s in Items) UpdateRunner.TryDelete(s.StagedPath);
    }
}

/// <summary>
/// A component could not be downloaded and verified. Thrown by <see cref="UpdateRunner.StageAsync"/>
/// BEFORE the running app is stopped and before any installed file is touched: the message says so.
/// </summary>
public sealed class UpdateStageException : Exception
{
    public UpdateStageException(string componentId, string reason)
        : base($"{componentId} could not be downloaded and verified ({reason}). Nothing was replaced and the running "
               + "AgentEyes was not stopped - the installed version is unchanged. Check the connection (or the release) "
               + "and run the update again.")
    {
        ComponentId = componentId;
        Reason = reason;
    }

    public string ComponentId { get; }
    public string Reason { get; }
}

/// <summary>
/// A component could not be swapped in with the app stopped (issue #86 review, B1b). The components
/// already swapped were rolled back to the previous build, so the install is on ONE version - the old
/// one - unless <see cref="RollbackFailures"/> is non-empty, in which case the message names the exact
/// half state and the fix.
/// </summary>
public sealed class UpdateSwapException : Exception
{
    public UpdateSwapException(string componentId, string reason, IReadOnlyList<string> rolledBack, IReadOnlyList<string> rollbackFailures)
        : base(Compose(componentId, reason, rolledBack, rollbackFailures))
    {
        ComponentId = componentId;
        Reason = reason;
        RolledBack = rolledBack;
        RollbackFailures = rollbackFailures;
    }

    public string ComponentId { get; }
    public string Reason { get; }
    /// <summary>Component ids that had been swapped and were put back to the previous build.</summary>
    public IReadOnlyList<string> RolledBack { get; }
    /// <summary>"component (file): reason" for every rollback that failed - the install is mixed when this is non-empty.</summary>
    public IReadOnlyList<string> RollbackFailures { get; }
    public bool InstallIsMixed => RollbackFailures.Count > 0;

    private static string Compose(string componentId, string reason, IReadOnlyList<string> rolledBack, IReadOnlyList<string> rollbackFailures)
    {
        string head = $"{componentId} could not be replaced ({reason}).";
        if (rollbackFailures.Count == 0)
        {
            string back = rolledBack.Count == 0
                ? "No other component had been replaced yet"
                : $"The {rolledBack.Count} component(s) already replaced ({string.Join(", ", rolledBack)}) were rolled back to the previous build";
            return $"{head} {back} - the install is on the previous version throughout. Run the update again.";
        }
        return $"{head} ROLLBACK FAILED for: {string.Join("; ", rollbackFailures)}. The install is now MIXED - "
               + "the file(s) that could not be rolled back are on the new build, the rest on the previous one. "
               + "Fix: run 'agenteyes-setup install' (a repair - it replaces every component), or restore each "
               + "'<file>.old' over its '<file>' by hand.";
    }
}

/// <summary>
/// Executes an <see cref="UpdatePlan"/> in TWO steps (issue #86 review, B1): <see cref="StageAsync"/>
/// downloads every actionable item and verifies its SHA-256 against the manifest - with the app still
/// running and no installed file touched - and <see cref="Swap"/> then places all of them, rolling the
/// ones already placed back when one fails, so the install is never left on two versions. Downloading
/// is injected as a delegate so the whole flow is testable without a network: production passes a
/// delegate backed by the GitHub release download; tests pass one that produces a local file.
///
/// Single-file assets (the app, agenteyes, agenteyes-setup) are placed directly. Archive assets (the
/// ffmpeg .zip) are extracted into the app dir via <see cref="ArchiveInstaller"/>.
/// <see cref="ApplyAsync"/> is the two steps back to back, for a caller with no running app to stop.
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

    /// <summary>Stage, then swap. For a caller that has no running app to stop in between.</summary>
    public async Task<UpdateRunResult> ApplyAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        using var staged = await StageAsync(plan, ct);
        return Swap(staged);
    }

    /// <summary>
    /// Download and verify EVERY actionable item before anything else happens. Throws
    /// <see cref="UpdateStageException"/> on the first item that cannot be downloaded or whose SHA-256
    /// does not match, after deleting what was staged so far; no installed file is read or written here.
    /// </summary>
    public async Task<StagedUpdate> StageAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var actionable = plan.Actionable;
        EngineLog.Write($"[UpdateRunner] StageAsync: downloading and verifying {actionable.Count} component(s) before anything is stopped or replaced");
        var staged = new List<StagedItem>();
        try
        {
            foreach (var item in actionable)
            {
                if (!_componentsById.TryGetValue(item.ComponentId, out var component))
                    throw new UpdateStageException(item.ComponentId, "component not in scope");

                string path;
                try
                {
                    path = await _download(item, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    EngineLog.Write($"[UpdateRunner] StageAsync: {item.ComponentId} download FAILED: {ex.Message}");
                    throw new UpdateStageException(item.ComponentId, "download failed: " + ex.Message);
                }
                if (!File.Exists(path))
                    throw new UpdateStageException(item.ComponentId, "the download produced no file");

                if (!Hashing.Sha256Matches(path, item.Sha256))
                {
                    EngineLog.Write($"[UpdateRunner] StageAsync: {item.ComponentId} SHA-256 mismatch; rejecting");
                    TryDelete(path);
                    throw new UpdateStageException(item.ComponentId, "SHA-256 mismatch; download rejected");
                }

                EngineLog.Write($"[UpdateRunner] StageAsync: {item.ComponentId} {item.FromVersion ?? "(absent)"} -> {item.ToVersion} verified at {path}");
                staged.Add(new StagedItem(item, component, path));
            }
        }
        catch
        {
            foreach (var s in staged) TryDelete(s.StagedPath);
            throw;
        }
        EngineLog.Write($"[UpdateRunner] StageAsync: all {staged.Count} component(s) verified; nothing installed has been touched");
        return new StagedUpdate(staged);
    }

    /// <summary>
    /// Place every staged item, all or nothing (issue #86 review, B1b). When one cannot be placed the
    /// ones already placed are rolled back (the ".old" backup restored, or a fresh file removed) in
    /// reverse order and <see cref="UpdateSwapException"/> is thrown; if a rollback itself fails the
    /// exception names the exact mixed state and the fix. Installed versions are recorded only when
    /// every item is in place. The staged files are consumed.
    /// </summary>
    public UpdateRunResult Swap(StagedUpdate staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        EngineLog.Write($"[UpdateRunner] Swap: placing {staged.Items.Count} verified component(s)");
        var results = new List<ApplyResult>();
        // Everything placed so far, newest last: (component id, target, backup or null for a fresh file).
        var placed = new List<(string ComponentId, string Target, string? Backup)>();

        foreach (var s in staged.Items)
        {
            var item = s.Item;
            try
            {
                string? backup = null;
                if (item.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (target, fileBackup) in ArchiveInstaller.Place(_layout.AppDir, s.StagedPath))
                        placed.Add((item.ComponentId, target, fileBackup));
                }
                else
                {
                    var target = _layout.PathFor(s.Component);
                    backup = InstallSwapper.Place(target, s.StagedPath);
                    placed.Add((item.ComponentId, target, backup));
                }
                TryDelete(s.StagedPath);
                var status = item.Kind == PlanItemKind.Update ? ApplyStatus.Updated : ApplyStatus.Installed;
                results.Add(new ApplyResult(item.ComponentId, status, item.FromVersion, item.ToVersion, null, backup));
                EngineLog.Write($"[UpdateRunner] Swap: {item.ComponentId} {status}");
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[UpdateRunner] Swap: {item.ComponentId} FAILED: {ex.Message}; rolling back {placed.Count} placed file(s)");
                var (rolledBack, failures) = RollBack(placed, item.ComponentId);
                throw new UpdateSwapException(item.ComponentId, ex.Message, rolledBack, failures);
            }
        }

        RecordInstalledVersions(results);
        EngineLog.Write($"[UpdateRunner] Swap done: installed={results.Count(r => r.Status == ApplyStatus.Installed)}, " +
                        $"updated={results.Count(r => r.Status == ApplyStatus.Updated)}");
        return new UpdateRunResult { Results = results };
    }

    /// <summary>
    /// Undo the placements in reverse order. A file that had a backup goes back to it; a file that was
    /// new is removed. Files of the component that failed half-way (an archive with several files) are
    /// undone too. Returns the component ids rolled back and "component (file): reason" for every failure.
    /// </summary>
    private static (IReadOnlyList<string> RolledBack, IReadOnlyList<string> Failures) RollBack(
        List<(string ComponentId, string Target, string? Backup)> placed, string failedComponentId)
    {
        var rolledBack = new List<string>();
        var failures = new List<string>();
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            var (componentId, target, backup) = placed[i];
            try
            {
                if (backup != null)
                {
                    if (!InstallSwapper.Rollback(target))
                        throw new FileNotFoundException("the .old backup to restore is gone", InstallSwapper.BackupPathFor(target));
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    EngineLog.Write($"[UpdateRunner] RollBack: removed the fresh {target}");
                }
                if (componentId != failedComponentId && !rolledBack.Contains(componentId)) rolledBack.Add(componentId);
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[UpdateRunner] RollBack FAILED for {componentId} ({target}): {ex.Message}");
                failures.Add($"{componentId} ({target}): {ex.Message}");
            }
        }
        return (rolledBack, failures);
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

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { EngineLog.Write($"[UpdateRunner] cleanup delete failed for {path}: {ex.Message}"); }
    }
}
