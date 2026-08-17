using AgentEyes.Setup.Engine;
using AgentEyesSetup.Models;

namespace AgentEyesSetup.Services;

/// <summary>
/// Drives an install/update through the shared AgentEyes.Setup.Engine (the
/// same engine the headless CLI uses), then layers on the installer-only
/// concerns: the Inno v0.1 takeover, PATH, shortcuts, run-at-login, and the
/// Add/Remove Programs entry.
///
/// The interactive installer always pulls the published build for every
/// component (force install / repair semantics). Per-component "is it behind?"
/// skipping is the in-app updater's job, not the installer's.
/// </summary>
public sealed class EngineInstallRunner
{
    /// <summary>
    /// Optional progress hook so the host can surface a status line while the runner
    /// stops a running instance before installing (issue #95). No prompting: the
    /// running app is stopped automatically, never by asking the user to quit it.
    /// </summary>
    public Action<string>? OnStatus { get; set; }

    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly ReleaseSource _source = new();

    /// <summary>The install dir (also what goes on PATH).</summary>
    public string AppDir => _layout.AppDir;

    /// <summary>The canonical app exe path (%LOCALAPPDATA%\AgentEyes\app\AgentEyesApp.exe).</summary>
    public string AppExePath => _layout.PathFor(ComponentRegistry.App);

    /// <summary>Everything <see cref="ApplyAsync"/> needs, plus the UI items and up-to-date state.</summary>
    public sealed record Prep(
        string Version,
        ResolvedRelease Release,
        List<ComponentItem> Items,
        IReadOnlyDictionary<string, ComponentItem> ItemsByComponentId,
        string? InstalledAppVersion,
        bool IsUpToDate);

    /// <summary>What the user picked on the Options step.</summary>
    public sealed record Options(bool Autostart, bool AddToPath, bool DesktopShortcut);

    /// <summary>Resolve the release: the latest GitHub Release, or the --release-dir override.</summary>
    public async Task<ResolvedRelease> ResolveReleaseAsync(CancellationToken ct = default) =>
        App.ReleaseDirOverride is { } dir
            ? ReleaseSource.LoadLocalReleaseDir(dir)
            : await _source.FetchLatestAsync(ct);

    /// <summary>Fetch the release and build the UI item list.</summary>
    public async Task<Prep> PrepareAsync(CancellationToken ct = default)
    {
        SetupLog.Write("[EngineInstallRunner] PrepareAsync: resolving release");
        var release = await ResolveReleaseAsync(ct);
        var version = release.Manifest.Version;

        var items = new List<ComponentItem>();
        var byId = new Dictionary<string, ComponentItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ComponentRegistry.All)
        {
            var item = new ComponentItem { Name = c.Id, DisplayName = c.DisplayName, AssetName = c.Asset };
            var asset = release.Manifest.TryGetAsset(c.Asset);
            if (asset is null) { item.Status = "Skipped"; item.SizeText = "Not in release"; }
            else item.SizeText = FormatSize(asset.Size);
            items.Add(item);
            byId[c.Id] = item;
        }

        var reader = new InstalledStateReader(_layout);
        var installedApp = reader.Read(ComponentRegistry.App).Version;
        var appAsset = release.Manifest.TryGetAsset(ComponentRegistry.App.Asset);
        var upToDate = installedApp != null && appAsset != null
            && VersionUtil.TryParse(installedApp) is { } iv
            && VersionUtil.TryParse(appAsset.Version) is { } rv
            && iv == rv
            // The Inno install reports the same app version but must still migrate.
            && !InnoMigration.IsInnoInstall(_layout);

        SetupLog.Write($"[EngineInstallRunner] PrepareAsync: version={version}, installedApp={installedApp}, upToDate={upToDate}");
        return new Prep(version, release, items, byId, installedApp, upToDate);
    }

    /// <summary>Install/refresh every component, then finalize. Returns (installed, skipped).</summary>
    public async Task<(int installed, int skipped)> ApplyAsync(Prep prep, Options options, CancellationToken ct = default)
    {
        // The Inno takeover deletes the whole app dir; a plain reinstall swaps exes.
        // Either way the app must not be running. Stop it automatically (issue #95) -
        // no "please quit it and retry" prompt. The stop is bounded + confirmed, and
        // runs off the UI thread so the wizard stays responsive.
        if (!await StopRunningAppAsync())
        {
            foreach (var item in prep.Items)
                if (item.Status == "Pending") { item.Status = "Skipped"; item.StatusDetail = "Could not stop the running AgentEyes"; }
            SetupLog.Write("[EngineInstallRunner] ApplyAsync aborted: could not stop the running app");
            return (0, prep.Items.Count);
        }

        if (InnoMigration.IsInnoInstall(_layout))
        {
            SetupLog.Write("[EngineInstallRunner] taking over the Inno v0.1 install");
            InnoMigration.RemoveInnoInstall(_layout);
        }

        var planItems = new List<PlanItem>();
        foreach (var c in ComponentRegistry.All)
        {
            var asset = prep.Release.Manifest.TryGetAsset(c.Asset);
            if (asset is null)
            {
                Set(prep, c.Id, "Skipped", "Not in release");
                continue;
            }
            planItems.Add(new PlanItem(c.Id, PlanItemKind.Install, asset.Name, null, asset.Version, asset.Sha256));
        }

        var runner = new UpdateRunner(_layout, ComponentRegistry.All, (item, innerCt) =>
        {
            Set(prep, item.ComponentId, "Downloading", null);
            return _source.DownloadAssetAsync(item.AssetName, prep.Release.DownloadUrls, innerCt);
        });

        var result = await runner.ApplyAsync(new UpdatePlan { Items = planItems }, ct);

        foreach (var r in result.Results)
        {
            var status = r.Status switch
            {
                ApplyStatus.Installed or ApplyStatus.Updated => "Done",
                _ => "Failed",
            };
            Set(prep, r.ComponentId, status, r.Error);
        }

        Finalize(prep, options);

        var installed = result.Installed + result.Updated;
        var skipped = prep.Items.Count(i => i.Status is "Skipped" or "Failed");
        SetupLog.Write($"[EngineInstallRunner] ApplyAsync: installed={installed}, skipped={skipped}");
        return (installed, skipped);
    }

    private void Finalize(Prep prep, Options options)
    {
        if (options.AddToPath)
            InstallFinalizer.AddAppDirToPath(_layout);
        // Not optional: without this the single-file host unpacks its native DLLs into
        // %TEMP%, where a temp cleaner deletes them and permanently breaks WPF (issue #120).
        InstallFinalizer.SetBundleExtractBaseDir(_layout);
        InstallFinalizer.CreateStartMenuShortcut(_layout);
        if (options.DesktopShortcut)
            InstallFinalizer.CreateDesktopShortcut(_layout);
        InstallFinalizer.SetAutostart(_layout, options.Autostart);

        var appVersion = prep.Release.Manifest.TryGetAsset(ComponentRegistry.App.Asset)?.Version ?? prep.Version;
        InstallFinalizer.RegisterUninstallEntry(_layout, appVersion);
        SetupLog.Write($"[EngineInstallRunner] finalized (path={options.AddToPath}, autostart={options.Autostart}, desktop={options.DesktopShortcut}, bundle={_layout.BundleExtractDir})");
    }

    /// <summary>
    /// Ensure the tray app is not running before we swap its files: if it is running,
    /// stop it automatically (bounded graceful-then-force, confirmed gone) via the shared
    /// engine helper. Returns true when the app is confirmed not running. Runs the blocking
    /// stop on a background thread so the wizard UI stays responsive.
    /// </summary>
    private async Task<bool> StopRunningAppAsync()
    {
        if (!RunningApp.IsRunning(_layout))
            return true;

        SetupLog.Write("[EngineInstallRunner] AgentEyes is running - stopping it automatically before install");
        OnStatus?.Invoke("Closing the running AgentEyes...");
        bool stopped = await Task.Run(() => RunningApp.StopAndWait(_layout));
        SetupLog.Write($"[EngineInstallRunner] StopRunningAppAsync: stopped={stopped}");
        return stopped;
    }

    private static void Set(Prep prep, string componentId, string status, string? detail)
    {
        if (!prep.ItemsByComponentId.TryGetValue(componentId, out var item)) return;
        item.Status = status;
        if (detail != null) item.StatusDetail = detail;
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";
}
