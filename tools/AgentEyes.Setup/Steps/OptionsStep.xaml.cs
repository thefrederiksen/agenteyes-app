using System.Windows.Controls;
using AgentEyesSetup.Services;

namespace AgentEyesSetup.Steps;

public partial class OptionsStep : UserControl
{
    public OptionsStep()
    {
        InitializeComponent();
        SetupLog.Write("[OptionsStep] Created");
    }

    public EngineInstallRunner.Options GetOptions() => new(
        Autostart: AutostartCheck.IsChecked == true,
        AddToPath: PathCheck.IsChecked == true,
        DesktopShortcut: DesktopCheck.IsChecked == true);
}
