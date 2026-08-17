using System.IO;
using System.Windows;
using AgentEyes.Setup.Engine;
using AgentEyesSetup.Services;

namespace AgentEyesSetup;

public partial class App : Application
{
    /// <summary>
    /// "--release-dir &lt;dir&gt;": install from a local release directory (the output of
    /// scripts\build-release.ps1) instead of the latest GitHub Release. Same offline
    /// mode the CLI has; used for testing builds before they are published.
    /// </summary>
    public static string? ReleaseDirOverride { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (var i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i].Equals("--release-dir", StringComparison.OrdinalIgnoreCase))
                ReleaseDirOverride = Path.GetFullPath(e.Args[i + 1]);

        // Route the shared engine's detailed step logs (downloads, SHA verify, swaps,
        // the Inno takeover) into the setup log. Without this the engine's lines are
        // discarded (EngineLog defaults to a no-op), leaving the log blank exactly
        // where a failed/stuck install would need diagnosing.
        EngineLog.Sink = SetupLog.Write;
        SetupLog.Write($"[App] startup (releaseDir={ReleaseDirOverride ?? "latest"})");
        base.OnStartup(e);
    }
}
