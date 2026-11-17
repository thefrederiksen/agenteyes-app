using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AgentEyes.Setup.Engine;

/// <summary>The values the Add/Remove Programs entry carries.</summary>
public sealed record UninstallEntry(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string InstallLocation,
    string DisplayIcon,
    string UninstallString,
    string QuietUninstallString);

/// <summary>
/// The per-user shell entries the installer owns OUTSIDE any install root: the Start Menu and desktop
/// shortcuts, the run-at-login Run value, the Add/Remove Programs entry and the legacy Inno uninstall
/// key. Every read and write of them goes through this seam, the way the PATH entry and
/// DOTNET_BUNDLE_EXTRACT_BASE_DIR go through <see cref="IUserEnvironment"/>.
///
/// Why it exists (issue #200, QA bounce): the test that proves an isolated uninstall never touches the
/// real install drove the real <see cref="Uninstaller.Apply"/>. Had the isolation it guards regressed,
/// that test run would have deleted the machine's real shortcuts, Run value and uninstall entry before
/// going red - the test's safety depended on the correctness of the code under test. A test host swaps
/// this seam for an in-memory store before any test runs, so no test can reach the real entries,
/// whatever the product code does.
/// </summary>
public interface IUserShell
{
    /// <summary>Where the Start Menu shortcut lives.</summary>
    string StartMenuShortcutPath { get; }

    /// <summary>Where the desktop shortcut lives.</summary>
    string DesktopShortcutPath { get; }

    /// <summary>True when a shortcut exists at <paramref name="lnk"/>.</summary>
    bool ShortcutExists(string lnk);

    /// <summary>Create or overwrite the shortcut at <paramref name="lnk"/> pointing at <paramref name="targetExe"/>.</summary>
    void WriteShortcut(string lnk, string targetExe);

    /// <summary>Delete the shortcut at <paramref name="lnk"/>. Throws when it cannot be deleted.</summary>
    void DeleteShortcut(string lnk);

    /// <summary>The run-at-login command line, or null when it is not set.</summary>
    string? GetRunValue();

    /// <summary>Set the run-at-login command line; null removes it.</summary>
    void SetRunValue(string? commandLine);

    /// <summary>True when the Add/Remove Programs entry exists.</summary>
    bool UninstallEntryExists();

    /// <summary>Create or overwrite the Add/Remove Programs entry.</summary>
    void WriteUninstallEntry(UninstallEntry entry);

    /// <summary>Delete the Add/Remove Programs entry (no-op when absent).</summary>
    void DeleteUninstallEntry();

    /// <summary>Delete the legacy Inno v0.1 uninstall entry (no-op when absent).</summary>
    void DeleteInnoUninstallEntry();
}

/// <summary>The real store: shortcut files in the user's shell folders and keys under HKCU.</summary>
public sealed class WindowsUserShell : IUserShell
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AgentEyes";
    private const string UninstallParentPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyName = "AgentEyes";

    /// <summary>The Inno AppId from the legacy MyQuietShadow installer (installer/MyQuietShadow.iss),
    /// as Windows registered it. Kept verbatim - it must still match the genuine pre-AgentEyes
    /// registry key. With the AgentEyes clean-break rename this path no longer fires in practice.</summary>
    private const string InnoUninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C2F4D7A-30A2-4E8B-9B57-MyQuietShadow}_is1";

    /// <inheritdoc/>
    public string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "AgentEyes.lnk");

    /// <inheritdoc/>
    public string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AgentEyes.lnk");

    /// <inheritdoc/>
    public bool ShortcutExists(string lnk) => File.Exists(lnk);

    /// <inheritdoc/>
    public void WriteShortcut(string lnk, string targetExe)
    {
        EngineLog.Write($"[WindowsUserShell] WriteShortcut: {lnk} -> {targetExe}");
        var dir = Path.GetDirectoryName(lnk);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM object not available.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        var shortcut = shell.GetType().InvokeMember("CreateShortcut",
            BindingFlags.InvokeMethod, null, shell, [lnk])
            ?? throw new InvalidOperationException("CreateShortcut returned null.");

        var t = shortcut.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetExe]);
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(targetExe)]);
        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{targetExe},0"]);
        t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["AgentEyes"]);
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

        Marshal.ReleaseComObject(shortcut);
        Marshal.ReleaseComObject(shell);
    }

    /// <inheritdoc/>
    public void DeleteShortcut(string lnk)
    {
        EngineLog.Write($"[WindowsUserShell] DeleteShortcut: {lnk}");
        File.Delete(lnk);
    }

    /// <inheritdoc/>
    public string? GetRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName)?.ToString();
    }

    /// <inheritdoc/>
    public void SetRunValue(string? commandLine)
    {
        EngineLog.Write($"[WindowsUserShell] SetRunValue: {commandLine ?? "(removed)"}");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{RunKeyPath}.");
        if (commandLine != null) key.SetValue(RunValueName, commandLine);
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <inheritdoc/>
    public bool UninstallEntryExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallParentPath + "\\" + UninstallKeyName);
        return key != null;
    }

    /// <inheritdoc/>
    public void WriteUninstallEntry(UninstallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EngineLog.Write($"[WindowsUserShell] WriteUninstallEntry: v{entry.DisplayVersion} at {entry.InstallLocation}");
        using var key = Registry.CurrentUser.CreateSubKey(UninstallParentPath + "\\" + UninstallKeyName, writable: true)
            ?? throw new InvalidOperationException($"Could not create HKCU\\{UninstallParentPath}\\{UninstallKeyName}.");
        key.SetValue("DisplayName", entry.DisplayName);
        key.SetValue("DisplayVersion", entry.DisplayVersion);
        key.SetValue("Publisher", entry.Publisher);
        key.SetValue("InstallLocation", entry.InstallLocation);
        key.SetValue("DisplayIcon", entry.DisplayIcon);
        key.SetValue("UninstallString", entry.UninstallString);
        key.SetValue("QuietUninstallString", entry.QuietUninstallString);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <inheritdoc/>
    public void DeleteUninstallEntry()
    {
        EngineLog.Write("[WindowsUserShell] DeleteUninstallEntry");
        using var parent = Registry.CurrentUser.OpenSubKey(UninstallParentPath, writable: true);
        parent?.DeleteSubKeyTree(UninstallKeyName, throwOnMissingSubKey: false);
    }

    /// <inheritdoc/>
    public void DeleteInnoUninstallEntry()
    {
        EngineLog.Write("[WindowsUserShell] DeleteInnoUninstallEntry");
        Registry.CurrentUser.DeleteSubKeyTree(InnoUninstallKeyPath, throwOnMissingSubKey: false);
    }
}
