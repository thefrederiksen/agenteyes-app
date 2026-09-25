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
    /// downloads, stops a running instance and replaces the files (issue #95). No prompting:
    /// the running app is stopped automatically, never by asking the user to quit it.
    /// </summary>
    public Action<string>? OnStatus { get; set; }

    private readonly InstallLayout _layout;
    private readonly ReleaseSource _source;
    private readonly IRunningAppHandle _app;
    private readonly IAppLauncher _launcher;

    public EngineInstallRunner() : this(InstallLayout.Default(), new ReleaseSource(), null, null) { }

    /// <summary>
    /// Testable seams (issue #86 review, N1): the install root, the release source, and the running-app
    /// handle and launcher the update cycle uses. Null for the handle or launcher = the real ones for
    /// the layout. A test passes a handle that throws or a fake app, and no AgentEyes is ever touched.
    /// </summary>
    public EngineInstallRunner(InstallLayout layout, ReleaseSource source, IRunningAppHandle? app, IAppLauncher? launcher)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _app = app ?? new RunningAppHandle(_layout);
        _launcher = launcher ?? new ProcessAppLauncher(_layout);
    }

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
        return Prepare(release);
    }

    /// <summary>The item list and up-to-date state for an already resolved release (the pure part of
    /// <see cref="PrepareAsync"/>; a test hands in a local release dir).</summary>
    public Prep Prepare(ResolvedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
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

        SetupLog.Write($"[EngineInstallRunner] Prepare: version={version}, installedApp={installedApp}, upToDate={upToDate}");
        return new Prep(version, release, items, byId, installedApp, upToDate);
    }

    /// <summary>What the last <see cref="ApplyAsync"/> did about the running app (issue #86): the pids
    /// when it was stopped and started again. Null until it ran, and when it failed.</summary>
    public AppRestartReport? LastRestart { get; private set; }

    /// <summary>Why the last <see cref="ApplyAsync"/> failed, or null when it did not (issue #86 review,
    /// N1). Every exception the update cycle raises - a download that fails its SHA-256, a running app
    /// that cannot be stopped or whose command line cannot be read, a swap that was rolled back, a
    /// relaunch that failed - lands here as one clear message, so the window shows an error state
    /// instead of hanging at "Installing...".</summary>
    public string? LastError { get; private set; }

    /// <summary>Install/refresh every component, then finalize. Returns (installed, skipped); on a
    /// failure (0, all) with <see cref="LastError"/> set.</summary>
    public async Task<(int installed, int skipped)> ApplyAsync(Prep prep, Options options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prep);
        ArgumentNullException.ThrowIfNull(options);
        LastRestart = null;
        LastError = null;
        SetupLog.Write($"[EngineInstallRunner] ApplyAsync: v{prep.Version}");
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

        // Issue #86, revised after the review of PR #92: the same find -> download+verify -> stop -> swap
        // -> relaunch cycle as the setup CLI. Every component is downloaded and verified with the app
        // still running; only then is the running app stopped (automatically, issue #95 - no "please
        // quit it" prompt), and NOTHING is replaced when it cannot be stopped; the Inno takeover (it
        // deletes the whole app dir) and the all-or-nothing swap follow; then the installed app is
        // started again with the arguments it was running with. The stop runs off the UI thread.
        if (RunningApp.IsRunning(_layout))
            OnStatus?.Invoke("Downloading (AgentEyes keeps running until everything is verified)...");
        var cycle = new UpdateRestartCycle(new StatusReportingHandle(_app, OnStatus), _launcher, _layout.PathFor(ComponentRegistry.App));
        UpdateRestartOutcome<UpdateRunResult> outcome;
        try
        {
            outcome = await cycle.RunAsync(
                innerCt => runner.StageAsync(new UpdatePlan { Items = planItems }, innerCt),
                (staged, _) =>
                {
                    foreach (var item in planItems) Set(prep, item.ComponentId, "Verified", null);
                    OnStatus?.Invoke("Replacing the files...");
                    if (InnoMigration.IsInnoInstall(_layout))
                    {
                        SetupLog.Write("[EngineInstallRunner] taking over the Inno v0.1 install");
                        InnoMigration.RemoveInnoInstall(_layout);
                    }
                    return Task.FromResult(runner.Swap(staged));
                }, ct);
            UpdateAttemptMarker.Clear(_layout);
        }
        catch (Exception ex)
        {
            // The wizard's service boundary (issue #86 review, N1): every failure of the cycle becomes ONE
            // error state the window shows - never an unobserved exception behind "Installing...". The
            // cycle's own message says what was and was not touched.
            LastError = ex.Message;
            string stage = UpdateAttemptMarker.StageOf(ex);
            foreach (var item in prep.Items)
            {
                if (item.Status is "Pending" or "Downloading" or "Verified")
                {
                    item.Status = "Skipped";
                    item.StatusDetail = ex is AppStopFailedException ? "Could not stop the running AgentEyes" : "Not installed - see the error";
                }
            }
            SetupLog.Write($"[EngineInstallRunner] ApplyAsync FAILED ({stage}): {ex}");
            RecordFailedAttempt(prep.Version, stage, ex.Message);
            OnStatus?.Invoke("ERROR: " + ex.Message);
            return (0, prep.Items.Count);
        }

        var result = outcome.Result;
        LastRestart = outcome.Restart;
        SetupLog.Write($"[EngineInstallRunner] ApplyAsync: {LastRestart.Describe()}");
        if (LastRestart.Note != null) SetupLog.Write($"[EngineInstallRunner] ApplyAsync: {LastRestart.Note}");

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

    /// <summary>The failed attempt goes on record for the app's AutoUpdate (issue #86 review, B1c). A
    /// record that cannot be written is logged and must not hide the failure being recorded.</summary>
    private void RecordFailedAttempt(string version, string stage, string reason)
    {
        try
        {
            UpdateAttemptMarker.WriteFailed(_layout, version, stage, reason, "wizard");
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[EngineInstallRunner] RecordFailedAttempt FAILED: {ex.Message}");
        }
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

    /// <summary>Tells the status line when the cycle reaches the stop - the one step that can take up
    /// to the engine's bound - and otherwise passes straight through to the real handle.</summary>
    private sealed class StatusReportingHandle : IRunningAppHandle
    {
        private readonly IRunningAppHandle _inner;
        private readonly Action<string>? _status;

        public StatusReportingHandle(IRunningAppHandle inner, Action<string>? status)
        {
            _inner = inner;
            _status = status;
        }

        public RunningAppInstance? Find() => _inner.Find();

        public Task<bool> StopAsync(RunningAppInstance instance, CancellationToken ct)
        {
            _status?.Invoke("Everything is verified - closing the running AgentEyes...");
            return _inner.StopAsync(instance, ct);
        }
    }
}
