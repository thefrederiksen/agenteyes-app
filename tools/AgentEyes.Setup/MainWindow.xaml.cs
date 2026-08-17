using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyes.Setup.Engine;
using AgentEyesSetup.Services;
using AgentEyesSetup.Steps;

namespace AgentEyesSetup;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private int _installedCount;
    private int _skippedCount;
    private bool _alreadyUpToDate;
    private string? _latestVersion;
    private EngineInstallRunner.Prep? _cachedPrep;

    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly bool _isUpdate;
    private readonly string? _installedVersion;

    private WelcomeStep? _welcomeStep;
    private OptionsStep? _optionsStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

    // Wizard steps: 1 Welcome, 2 Options, 3 Install, 4 Complete.
    private const int StepInstall = 3;
    private const int StepComplete = 4;

    public MainWindow()
    {
        InitializeComponent();

        _isUpdate = InstallDetector.IsInstalled(_layout);
        _installedVersion = _isUpdate ? InstallDetector.GetInstalledVersion(_layout) : null;

        SetupLog.Write($"[MainWindow] Started: isUpdate={_isUpdate}, installedVersion={_installedVersion}");

        if (_isUpdate)
        {
            Title = "AgentEyes Update";
            SubtitleText.Text = "Update";
            Step3Label.Text = "Update";
        }

        Loaded += MainWindow_Loaded;
        ShowStep(1);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isUpdate)
            _ = FetchLatestVersionAsync();
    }

    private async Task FetchLatestVersionAsync()
    {
        SetupLog.Write("[MainWindow] FetchLatestVersionAsync: checking for latest release");
        try
        {
            var release = await new EngineInstallRunner().ResolveReleaseAsync();
            _latestVersion = release.Manifest.Version;
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync: latestVersion={_latestVersion}");
            _welcomeStep?.UpdateVersionInfo(_installedVersion, _latestVersion);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync FAILED: {ex.Message}");
        }
    }

    private List<StepUI> GetStepUIs() =>
    [
        new(Step1Circle, Step1Label, null),
        new(Step2Circle, Step2Label, Step2Num),
        new(Step3Circle, Step3Label, Step3Num),
        new(Step4Circle, Step4Label, Step4Num),
    ];

    private Border[] GetLines() => [Line12, Line23, Line34];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            1 => _welcomeStep ??= new WelcomeStep(_isUpdate, _installedVersion),
            2 => _optionsStep ??= new OptionsStep(),
            3 => _installStep ??= new InstallStep(),
            4 => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _layout.AppDir,
                     _layout.PathFor(ComponentRegistry.App), _isUpdate, _alreadyUpToDate),
            _ => null
        };

        if (step == StepInstall && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == StepInstall)
            _ = RunInstallAsync();
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");

        for (int i = 0; i < stepUIs.Count; i++)
        {
            var stepNum = i + 1;
            var ui = stepUIs[i];

            if (stepNum < _currentStep)
            {
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else if (stepNum == _currentStep)
            {
                ui.Circle.Background = accentBrush;
                ui.Label.Foreground = Brushes.White;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else
            {
                ui.Circle.Background = inactiveBrush;
                ui.Label.Foreground = dimBrush;
                if (ui.Number != null) ui.Number.Foreground = dimBrush;
            }

            if (i < lines.Length)
                lines[i].Background = stepNum < _currentStep ? successBrush : inactiveBrush;
        }
    }

    private void UpdateNavButtons()
    {
        BackButton.Visibility = _currentStep > 1 && _currentStep < StepComplete
            ? Visibility.Visible : Visibility.Collapsed;

        if (_currentStep == StepComplete)
        {
            NextButton.Content = "Close";
            NextButton.IsEnabled = true;
        }
        else if (_currentStep == StepInstall)
        {
            NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
            NextButton.IsEnabled = false;
        }
        else
        {
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
        }
    }

    private async Task RunInstallAsync()
    {
        SetupLog.Write("[MainWindow] RunInstallAsync: starting");

        var runner = new EngineInstallRunner { OnStatus = s => _installStep?.SetStatus(s) };

        _installStep?.SetStatus("Fetching release info...");

        EngineInstallRunner.Prep prep;
        try
        {
            prep = _cachedPrep ?? await runner.PrepareAsync();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED: {ex.Message}");
            // A 404 from "latest" means no release has been published yet - say so
            // instead of looking like a network failure.
            _installStep?.SetStatus(ex.Message.Contains("404")
                ? "ERROR: No published release found on GitHub yet. (Testing a local build? " +
                  "Launch with: AgentEyes-Setup-win-x64.exe --release-dir <dir>)"
                : $"ERROR: Could not fetch release info: {ex.Message}");
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        _cachedPrep = prep;
        VersionText.Text = $"v{prep.Version}";
        _installStep?.SetItems(prep.Items);

        if (_isUpdate && prep.IsUpToDate)
        {
            SetupLog.Write($"[MainWindow] Already up to date: {prep.Version}");
            _alreadyUpToDate = true;
            _installStep?.SetUpToDate(prep.Version);
            if (_installStep != null)
                _installStep.OnRepairRequested += OnRepairRequested;
            _installedCount = 0;
            _skippedCount = 0;
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
            return;
        }

        _installStep?.SetStatus(_isUpdate && _installedVersion != null
            ? $"Updating from v{_installedVersion.Split('+')[0]} to v{prep.Version}..."
            : $"Installing v{prep.Version}...");

        await RunEngineApplyAsync(runner, prep, repair: false);
    }

    private void OnRepairRequested()
    {
        SetupLog.Write("[MainWindow] OnRepairRequested: user requested repair reinstall");
        _alreadyUpToDate = false;
        _ = RunRepairAsync();
    }

    private async Task RunRepairAsync()
    {
        NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
        NextButton.IsEnabled = false;

        var runner = new EngineInstallRunner { OnStatus = s => _installStep?.SetStatus(s) };
        var prep = _cachedPrep ?? await runner.PrepareAsync();
        _cachedPrep = prep;

        _installStep?.SetItems(prep.Items);
        _installStep?.SetStatus($"Repairing v{prep.Version}...");

        await RunEngineApplyAsync(runner, prep, repair: true);
    }

    /// <summary>Apply the prepared release via the engine and finalize the UI.</summary>
    private async Task RunEngineApplyAsync(EngineInstallRunner runner, EngineInstallRunner.Prep prep, bool repair)
    {
        var options = _optionsStep?.GetOptions() ?? new EngineInstallRunner.Options(
            Autostart: true, AddToPath: true, DesktopShortcut: true);

        var (installed, skipped) = await runner.ApplyAsync(prep, options);
        _installedCount = installed;
        _skippedCount = skipped;

        var verb = repair ? "Repair complete" : "Done";
        _installStep?.SetStatus($"{verb} - {installed} installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunEngineApplyAsync: repair={repair}, installed={installed}, skipped={skipped}");

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(_currentStep - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == StepComplete)
        {
            Close();
            return;
        }

        if (_currentStep == StepInstall && NextButton.Content?.ToString() == "Retry")
        {
            _installStep = null;
            ShowStep(StepInstall);
            return;
        }

        if (_currentStep < StepComplete)
        {
            // Leaving Install: rebuild Complete with the final counts.
            if (_currentStep == StepInstall)
                _completeStep = null;

            ShowStep(_currentStep + 1);
        }
    }
}
