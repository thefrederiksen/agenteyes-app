using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.DevThrottle;
using AgentEyes.LinuxApp.Model;
using AgentEyes.LinuxApp.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AgentEyes.LinuxApp.Views
{
    /// <summary>
    /// Settings (issue #107) - the Windows SettingsDialog's General and Account tabs. Run at login is
    /// the XDG autostart entry; the account is the DevThrottle credential the service transcribes with.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private bool _loading = true;
        private CancellationTokenSource? _signIn;

        public SettingsWindow() : this("General") { }

        public SettingsWindow(string tab)
        {
            InitializeComponent();
            Log.Info($"[SettingsWindow] ctor: tab={tab}");
            Tabs.SelectedItem = tab == "Account" ? AccountTab : GeneralTab;
            RunAtLogin.IsChecked = Autostart.IsEnabled;
            var st = AppState.Current.Status;
            ServiceLine.Text = st != null ? $"Recorder service: running ({ControlClient.BaseUrl})" : "Recorder service: NOT running - " + AppState.Current.OfflineReason;
            RecordingsLine.Text = "Recordings: " + RecordingPaths.Root;
            DataLine.Text = "Settings and logs: " + AppDataPaths.Root;
            _loading = false;
            Opened += async (_, _) => await RefreshAccountAsync();
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        private void RunAtLogin_Changed(object? sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                if (RunAtLogin.IsChecked == true)
                {
                    string exe = Environment.ProcessPath ?? throw new InvalidOperationException("the app's own path is unknown");
                    Autostart.Enable(exe, Path.Combine(AppContext.BaseDirectory, "agenteyes.png"));
                }
                else Autostart.Disable();
            }
            catch (Exception ex)
            {
                Log.Error("[SettingsWindow] RunAtLogin_Changed FAILED", ex);
                ServiceLine.Text = "Could not change Run at login: " + ex.Message;
            }
        }

        private void OpenRecordings_Click(object? sender, RoutedEventArgs e) => TrayController.OpenRecordingsFolder();

        private void OpenLogs_Click(object? sender, RoutedEventArgs e)
        {
            try { Shell.Open(Path.Combine(AppDataPaths.Root, "logs")); }
            catch (Exception ex)
            {
                Log.Error("[SettingsWindow] OpenLogs_Click FAILED", ex);
                ServiceLine.Text = "Could not open the logs folder: " + ex.Message;
            }
        }

        private async Task RefreshAccountAsync()
        {
            var cred = await Task.Run(() => DevThrottleAccount.Load());
            bool signedIn = cred?.ApiKey is { Length: > 0 };
            AccountLine.Text = signedIn ? "Connected as " + (cred!.Email ?? "(no email)") : "Not signed in";
            KeyLine.Text = signedIn ? "Key " + MaskKey(cred!.ApiKey!) : "";
            SignInButton.Content = signedIn ? "Reconnect" : "Sign in";
            SignOutButton.IsVisible = signedIn;
            AddCreditsButton.IsVisible = signedIn;
            CreditsLine.Text = "";
            if (!signedIn) return;
            CreditsLine.Text = "Credits: loading...";
            try
            {
                var credits = await DevThrottleClient.GetCreditsAsync();
                CreditsLine.Text = "Credits: " + (credits.BalanceMicros / 1_000_000m).ToString("$0.00", CultureInfo.InvariantCulture);
                CreditsLine.Foreground = credits.BalanceMicros <= 0 ? new SolidColorBrush(Color.Parse("#D8A657")) : (IBrush?)null;
            }
            catch (DevThrottleException ex) when (ex.Status == 401)
            {
                CreditsLine.Text = "Credits: reconnect to refresh your account balance.";
            }
            catch (Exception ex)
            {
                Log.Error("[SettingsWindow] credits FAILED", ex);
                CreditsLine.Text = "Credits: unavailable (" + ex.Message + ")";
            }
        }

        private async void SignIn_Click(object? sender, RoutedEventArgs e)
        {
            if (_signIn != null) { _signIn.Cancel(); return; }
            _signIn = new CancellationTokenSource();
            SignInButton.Content = "Cancel sign-in";
            AccountStatus.Text = "Opening your browser to sign in to DevThrottle...";
            try
            {
                var cred = await DevThrottleSignIn.SignInAsync(
                    s => Avalonia.Threading.Dispatcher.UIThread.Post(() => AccountStatus.Text = s), _signIn.Token);
                Log.Info($"[SettingsWindow] SignIn_Click: signed in as {cred.Email}");
                AccountStatus.Text = "Signed in as " + cred.Email + ".";
                await TellServiceAsync();
            }
            catch (OperationCanceledException)
            {
                AccountStatus.Text = "Sign-in cancelled.";
            }
            catch (Exception ex)
            {
                Log.Error("[SettingsWindow] SignIn_Click FAILED", ex);
                AccountStatus.Text = "Sign-in failed: " + ex.Message;
            }
            finally
            {
                _signIn.Dispose();
                _signIn = null;
            }
            await RefreshAccountAsync();
        }

        private async void SignOut_Click(object? sender, RoutedEventArgs e)
        {
            if (!await Dialogs.ConfirmAsync(this, "Sign out", "Sign out of DevThrottle? New recordings will not be transcribed or titled until you sign in again.", "Sign out")) return;
            try
            {
                await Task.Run(DevThrottleAccount.Clear);
                Log.Info("[SettingsWindow] SignOut_Click: signed out");
                AccountStatus.Text = "Signed out.";
                await TellServiceAsync();
            }
            catch (Exception ex)
            {
                Log.Error("[SettingsWindow] SignOut_Click FAILED", ex);
                AccountStatus.Text = "Sign-out failed: " + ex.Message;
            }
            await RefreshAccountAsync();
        }

        /// <summary>The service caches the account; have it re-read the stored credential.</summary>
        private async Task TellServiceAsync()
        {
            try
            {
                await AppState.Current.Client.RefreshAccountAsync();
                await AppState.Current.PollAsync();
            }
            catch (ControlApiException ex)
            {
                Log.Warn($"[SettingsWindow] TellServiceAsync: {ex.Message}");
                AccountStatus.Text += " (The recorder service did not answer - it picks the account up when it next starts.)";
            }
        }

        private void AddCredits_Click(object? sender, RoutedEventArgs e)
        {
            try { Shell.Open(DevThrottleAccount.CreditsUrl); }
            catch (Exception ex)
            {
                Log.Error("[SettingsWindow] AddCredits_Click FAILED", ex);
                AccountStatus.Text = "Could not open the browser: " + ex.Message;
            }
        }

        internal static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string tail = key.Length >= 4 ? key[^4..] : key;
            int first = key.IndexOf('_');
            int second = first >= 0 ? key.IndexOf('_', first + 1) : -1;
            string prefix = second > 0 ? key[..(second + 1)] : first > 0 ? key[..(first + 1)] : "";
            return prefix + "..." + tail;
        }
    }
}
