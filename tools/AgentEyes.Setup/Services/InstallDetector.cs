using System.Diagnostics;
using System.IO;
using AgentEyes.Setup.Engine;

namespace AgentEyesSetup.Services;

/// <summary>
/// Detects an existing install (either the new engine layout or the Inno v0.1
/// install - both live at app\AgentEyesApp.exe).
/// </summary>
public static class InstallDetector
{
    public static bool IsInstalled(InstallLayout layout) =>
        File.Exists(layout.PathFor(ComponentRegistry.App));

    public static string? GetInstalledVersion(InstallLayout layout)
    {
        var exe = layout.PathFor(ComponentRegistry.App);
        if (!File.Exists(exe)) return null;
        // Prefer the engine's bookkeeping; fall back to the exe's version stamp
        // (the only source for an Inno-era install).
        return new InstalledStateReader(layout).Read(ComponentRegistry.App).Version
            ?? FileVersionInfo.GetVersionInfo(exe).ProductVersion;
    }
}
