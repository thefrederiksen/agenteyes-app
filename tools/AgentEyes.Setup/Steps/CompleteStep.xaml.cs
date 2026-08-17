using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AgentEyes.Setup.Engine;
using AgentEyesSetup.Services;

namespace AgentEyesSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _appExePath;

    public CompleteStep(int installed, int skipped, string installPath, string appExePath, bool isUpdate, bool alreadyUpToDate = false)
    {
        InitializeComponent();
        _appExePath = appExePath;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        LogPathBox.Text = SetupLog.Path;

        if (alreadyUpToDate)
        {
            HeadingText.Text = "Already Up to Date";
            DescriptionText.Text = "AgentEyes is already running the latest version.";
            PathNote.Visibility = Visibility.Collapsed;
        }
        else if (isUpdate)
        {
            HeadingText.Text = "Update Complete";
            DescriptionText.Text = "AgentEyes has been updated successfully.";
            PathNote.Visibility = Visibility.Collapsed;
        }

        // Success looks clean: the log panel stays behind the tiny "Logs" link.
        // Only a failure earns prominent troubleshooting UI.
        if (skipped > 0)
        {
            ReportHeading.Text = $"{skipped} component(s) did not install - see the log";
            ReportHeading.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
            ReportPanel.Visibility = Visibility.Visible;
            LogsToggle.Visibility = Visibility.Collapsed;
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}");
    }

    private void LogsToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ReportPanel.Visibility = ReportPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] OpenLogButton_Click");
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetupLog.Write($"[CompleteStep] OpenLogButton_Click FAILED: {ex.Message}"); }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");

        if (!File.Exists(_appExePath))
        {
            SetupLog.Write($"[CompleteStep] AgentEyesApp.exe not found at {_appExePath}");
            return;
        }

        // Running-aware launch (issue #95): if an AgentEyes instance is already in the tray,
        // do NOT start a second process - that only trips the single-instance guard and shows
        // the "already running" popup. No-op and close the wizard instead. On an update the old
        // instance was already stopped during install, so this branch launches the new build.
        if (RunningApp.IsRunningForExe(_appExePath))
        {
            SetupLog.Write("[CompleteStep] LaunchButton_Click: AgentEyes already running - not launching a duplicate");
            Window.GetWindow(this)?.Close();
            return;
        }

        try
        {
            // Build a fresh PATH from the registry so the launched process (and any
            // terminals it spawns) sees the PATH entry the install just added.
            var psi = new ProcessStartInfo
            {
                FileName = _appExePath,
                UseShellExecute = false,
            };

            var freshPath = GetFreshPath();
            if (freshPath != null)
                psi.Environment["PATH"] = freshPath;

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: AgentEyes launched");

            Window.GetWindow(this)?.Close();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] LaunchButton_Click FAILED: {ex.Message}");
        }
    }

    private static string? GetFreshPath()
    {
        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey("Environment");
            var userPath = userKey?.GetValue("Path", "") as string ?? "";

            using var sysKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            var systemPath = sysKey?.GetValue("Path", "") as string ?? "";

            return systemPath + ";" + userPath;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] GetFreshPath FAILED: {ex.Message}");
            return null;
        }
    }
}
