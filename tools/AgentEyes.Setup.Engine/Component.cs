namespace AgentEyes.Setup.Engine;

/// <summary>The category of an installable component.</summary>
public enum ComponentKind
{
    /// <summary>The AgentEyes tray/GUI app (AgentEyesApp.exe).</summary>
    App,

    /// <summary>The agenteyes command-line tool (agenteyes.exe).</summary>
    Cli,

    /// <summary>The setup CLI (agenteyes-setup.exe) - installed so updates/uninstall work without re-downloading the wizard.</summary>
    SetupCli,

    /// <summary>The bundled ffmpeg/ffprobe pair, shipped as a zip with its own version.</summary>
    Ffmpeg,
}

/// <summary>
/// A single installable component. Immutable description used by the registry,
/// the install layout, and the update planner.
/// </summary>
/// <param name="Id">Canonical id (e.g. "app", "cli", "ffmpeg").</param>
/// <param name="Kind">Category.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Asset">
/// The release-asset filename this component ships as
/// (e.g. "AgentEyesApp-win-x64.exe"). This is the key into the release manifest.
/// </param>
public sealed record Component(
    string Id,
    ComponentKind Kind,
    string DisplayName,
    string Asset);

/// <summary>
/// The canonical list of installable components. AgentEyes's set is small
/// and fixed (unlike cc-director's runtime-discovered tool set), so this is a
/// plain static list. Asset naming follows the release pipeline
/// (scripts/build-release.ps1 + release.yml).
/// </summary>
public static class ComponentRegistry
{
    public static readonly Component App = new(
        Id: "app",
        Kind: ComponentKind.App,
        DisplayName: "AgentEyes",
        Asset: "AgentEyesApp-win-x64.exe");

    public static readonly Component Cli = new(
        Id: "cli",
        Kind: ComponentKind.Cli,
        DisplayName: "agenteyes command line",
        Asset: "agenteyes-win-x64.exe");

    public static readonly Component SetupCli = new(
        Id: "setup-cli",
        Kind: ComponentKind.SetupCli,
        DisplayName: "Setup CLI",
        Asset: "agenteyes-setup-cli-win-x64.exe");

    public static readonly Component Ffmpeg = new(
        Id: "ffmpeg",
        Kind: ComponentKind.Ffmpeg,
        DisplayName: "ffmpeg (bundled)",
        Asset: "agenteyes-ffmpeg-win-x64.zip");

    /// <summary>All components, in install order.</summary>
    public static readonly IReadOnlyList<Component> All = [App, Cli, SetupCli, Ffmpeg];

    /// <summary>Resolve a component by id, or throw with the valid ids.</summary>
    public static Component ById(string id)
    {
        foreach (var c in All)
            if (c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return c;
        throw new ArgumentException(
            $"Unknown component '{id}'. Valid: {string.Join(", ", All.Select(c => c.Id))}.", nameof(id));
    }
}
