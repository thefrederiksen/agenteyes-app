namespace AgentEyes.Setup.Engine;

/// <summary>
/// Resolves where each component lives on disk. The layout is per-user, no admin
/// (the same root the Inno v0.1 installer used, so a new-style install takes
/// over in place):
///   %LOCALAPPDATA%\AgentEyes\
///     app\                 install-owned binaries (AgentEyesApp.exe, agenteyes.exe, agenteyes-setup.exe, ffmpeg)
///     config\setup\        install bookkeeping (installed.json)
///     logs\                setup logs
///     config.json, ...     USER DATA - never touched by install/uninstall
/// The root is injectable (and overridable via AGENTEYES_ROOT) so tests can point at
/// temp directories.
/// </summary>
public sealed class InstallLayout
{
    /// <summary>%LOCALAPPDATA%\AgentEyes (or the AGENTEYES_ROOT override) - per-user, no admin.</summary>
    public string LocalRoot { get; }

    /// <summary>
    /// True for a layout made by <see cref="Isolated"/> - the setup CLI's <c>--root</c> (issue #200). An
    /// isolated layout promises that NOTHING outside <see cref="LocalRoot"/> is touched: no running AgentEyes
    /// is stopped or started (the app finds its install by process name and keeps its data under the REAL
    /// per-user root whatever this layout says, so neither can be scoped), no per-user finalization (PATH,
    /// the native-extraction variable, shortcuts, the run-at-login key, the Add/Remove Programs entry), no
    /// Inno takeover (it deletes a registry key), and every staged download and extraction stays under the
    /// root (<see cref="StagingDir"/>). A step that cannot honour that refuses; it never quietly proceeds.
    /// </summary>
    public bool IsIsolated { get; }

    public InstallLayout(string localRoot) : this(localRoot, isolated: false) { }

    private InstallLayout(string localRoot, bool isolated)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("localRoot must not be empty.", nameof(localRoot));
        LocalRoot = localRoot;
        IsIsolated = isolated;
    }

    /// <summary>
    /// The layout for an explicit install root that must stay isolated from the real install (the setup
    /// CLI's <c>--root</c>, issue #200). The root is made absolute so that every path derived from it - and
    /// every refusal that names one - is the path Windows will actually use.
    /// </summary>
    public static InstallLayout Isolated(string localRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("localRoot must not be empty.", nameof(localRoot));
        var full = Path.GetFullPath(localRoot);
        EngineLog.Write($"[InstallLayout] Isolated: root={full} - nothing outside it will be touched");
        return new InstallLayout(full, isolated: true);
    }

    /// <summary>The production layout, honoring AGENTEYES_ROOT for the per-user root.</summary>
    public static InstallLayout Default()
    {
        var root = Environment.GetEnvironmentVariable("AGENTEYES_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(localAppData, "AgentEyes");
        }
        return new InstallLayout(root);
    }

    /// <summary>Install-owned binaries. The ONLY directory the uninstaller deletes.</summary>
    public string AppDir => Path.Combine(LocalRoot, "app");

    /// <summary>Per-user install bookkeeping (installed-version manifest) - NOT user data.</summary>
    public string SetupStateDir => Path.Combine(LocalRoot, "config", "setup");

    /// <summary>The installed-version manifest: component id -> the version actually placed on disk.</summary>
    public string InstalledManifestPath => Path.Combine(SetupStateDir, "installed.json");

    /// <summary>The last FAILED update attempt (issue #86 review, B1c): written by the setup CLI or the
    /// wizard when an update fails, read by the app's AutoUpdate so it does not hand over again for the
    /// same target version, removed by a successful update. See <see cref="UpdateAttemptMarker"/>.</summary>
    public string UpdateAttemptMarkerPath => Path.Combine(SetupStateDir, "last-update-attempt.json");

    /// <summary>Setup/engine log directory.</summary>
    public string LogsDir => Path.Combine(LocalRoot, "logs");

    /// <summary>
    /// Where the single-file host unpacks the bundled native DLLs
    /// (DOTNET_BUNDLE_EXTRACT_BASE_DIR). Deliberately NOT under %TEMP%: a temp cleaner
    /// deletes the unlocked native DLLs there and the host never re-extracts them, which
    /// permanently breaks WPF with DllNotFoundException wpfgfx_cor3.dll (issue #120).
    /// Install-owned, not user data - the uninstaller removes it.
    /// </summary>
    public string BundleExtractDir => Path.Combine(LocalRoot, "bundle");

    /// <summary>
    /// Where downloads are staged and archives are unpacked before they are placed. %TEMP% for the real
    /// install (unchanged); <c>&lt;root&gt;\staging</c> for an <see cref="IsIsolated"/> one, because %TEMP%
    /// is outside the root (issue #200).
    /// </summary>
    public string StagingDir => IsIsolated ? Path.Combine(LocalRoot, "staging") : Path.GetTempPath();

    /// <summary>True when <paramref name="path"/> is <see cref="LocalRoot"/> itself or lies beneath it.</summary>
    public bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalRoot));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The on-disk file whose presence/version represents the component.</summary>
    public string PathFor(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component.Kind switch
        {
            ComponentKind.App => Path.Combine(AppDir, "AgentEyesApp.exe"),
            ComponentKind.Cli => Path.Combine(AppDir, "agenteyes.exe"),
            ComponentKind.SetupCli => Path.Combine(AppDir, "agenteyes-setup.exe"),
            // The ffmpeg zip carries ffmpeg.exe + ffprobe.exe; ffmpeg.exe is the
            // representative file for presence/version checks.
            ComponentKind.Ffmpeg => Path.Combine(AppDir, "ffmpeg.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
        };
    }
}
