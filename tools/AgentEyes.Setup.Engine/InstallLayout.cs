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

    public InstallLayout(string localRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("localRoot must not be empty.", nameof(localRoot));
        LocalRoot = localRoot;
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
