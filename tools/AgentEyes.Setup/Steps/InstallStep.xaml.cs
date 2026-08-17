using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyesSetup.Models;
using AgentEyesSetup.Services;

namespace AgentEyesSetup.Steps;

public partial class InstallStep : UserControl
{
    private List<ComponentItem> _items = [];

    public InstallStep()
    {
        InitializeComponent();
        LogFooter.Text = $"Setup log: {SetupLog.Path}";
        SetupLog.Write("[InstallStep] Created");
    }

    public event Action? OnRepairRequested;

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] OpenLogButton_Click");
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetupLog.Write($"[InstallStep] OpenLogButton_Click FAILED: {ex.Message}"); }
    }

    public void SetItems(List<ComponentItem> items)
    {
        _items = items;
        ComponentList.ItemsSource = _items;
    }

    public void SetUpdateMode()
    {
        HeadingText.Text = "Updating";
    }

    public void SetUpToDate(string version)
    {
        SetupLog.Write($"[InstallStep] SetUpToDate: version={version}");

        HeadingText.Text = "Up to Date";
        StatusText.Text = $"You are running the latest version (v{version}).";
        RepairButton.Visibility = Visibility.Visible;

        foreach (var item in _items)
            item.Status = "Done";
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] RepairButton_Click");
        RepairButton.Visibility = Visibility.Collapsed;
        foreach (var item in _items)
        {
            item.Status = "Pending";
            item.StatusDetail = "";
        }
        OnRepairRequested?.Invoke();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
        StatusText.Foreground = status.StartsWith("ERROR", StringComparison.Ordinal)
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0x44, 0x44))
            : (Brush)FindResource("TextForeground");
    }
}
