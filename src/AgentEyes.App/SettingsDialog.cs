using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyes.DevThrottle;

namespace AgentEyes.App
{
    /// <summary>
    /// App settings, grouped into tabs: General (login + REST API), Account (DevThrottle),
    /// Plugins. Capture settings live in presets. AgentEyes runs 100% on DevThrottle
    /// (issue #88) - there is no third-party AI-provider tab.
    /// </summary>
    internal sealed class SettingsDialog : Window
    {
        private readonly Config _cfg;
        private readonly CheckBox _api;
        private readonly TextBox _port;
        private readonly CheckBox _login;
        private readonly CheckBox _autoUpdate;

        // DevThrottle Account tab (issue #87): AgentEyes runs 100% on DevThrottle.
        private readonly TextBlock _dtStatus;
        private readonly TextBlock _dtKeyLine;
        private readonly TextBlock _dtCreditsLine;
        private readonly Button _dtSignIn;
        private readonly Button _dtReconnect;
        private readonly Button _dtAddCredits;
        private readonly Button _dtSignOut;

        private const double FieldWidth = 620;

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        /// <param name="initialTab">Header of the tab to select on open ("General", "Account",
        /// "Plugins"). Null selects the first tab. The rail's account indicator passes "Account"
        /// so a not-signed-in user reaches Sign in with one click (issue #129).</param>
        internal SettingsDialog(Config cfg, string? initialTab = null)
        {
            _cfg = cfg;
            Title = "Settings";
            Width = 800; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            // ---- General tab ----
            var general = new StackPanel();

            _login = new CheckBox { Content = "Run at login", IsChecked = Autostart.IsEnabled(), Margin = new Thickness(0, 0, 0, 12), Style = Res<Style>("DkCheck") };
            general.Children.Add(_login);

            _autoUpdate = new CheckBox { Content = "Automatically check for updates on startup", IsChecked = cfg.AutoUpdate, Margin = new Thickness(0, 0, 0, 12), Style = Res<Style>("DkCheck") };
            general.Children.Add(_autoUpdate);

            _api = new CheckBox { Content = "REST control API enabled", IsChecked = cfg.ApiEnabled, Margin = new Thickness(0, 0, 0, 8), Style = Res<Style>("DkCheck") };
            general.Children.Add(_api);

            var portRow = new StackPanel { Orientation = Orientation.Horizontal };
            portRow.Children.Add(new TextBlock { Text = "API port", VerticalAlignment = VerticalAlignment.Center, Width = 70, Foreground = Res<Brush>("DkText") });
            _port = new TextBox { Text = cfg.Port.ToString(), Width = 90, Height = 28, Style = Res<Style>("DkTextBox") };
            portRow.Children.Add(_port);
            general.Children.Add(portRow);

            general.Children.Add(Note("API and port changes take effect after restarting AgentEyes."));

            // ---- Account tab (issue #87): AgentEyes runs 100% on DevThrottle ----
            var account = new StackPanel();
            account.Children.Add(new TextBlock
            {
                Text = "AgentEyes runs on your DevThrottle account. Recording transcription and AI run on "
                     + "DevThrottle-hosted models and draw down your DevThrottle credits. There is no other "
                     + "provider - one account, one path.",
                Foreground = Res<Brush>("DkDim"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            });

            _dtStatus = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            account.Children.Add(_dtStatus);
            _dtKeyLine = new TextBlock { Foreground = Res<Brush>("DkDim"), FontSize = 11, Margin = new Thickness(0, 0, 0, 16) };
            account.Children.Add(_dtKeyLine);
            _dtCreditsLine = new TextBlock { Foreground = Res<Brush>("DkDim"), FontSize = 13, Margin = new Thickness(0, 0, 0, 16) };
            account.Children.Add(_dtCreditsLine);

            var dtButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _dtSignIn = new Button { Content = "Sign in", MinWidth = 110, Height = 30, Style = Res<Style>("DkPrimary") };
            _dtReconnect = new Button { Content = "Reconnect", MinWidth = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0), Style = Res<Style>("DkMini") };
            _dtAddCredits = new Button { Content = "Add credits", MinWidth = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0), Style = Res<Style>("DkPrimary") };
            _dtSignOut = new Button { Content = "Sign out", MinWidth = 110, Height = 30, Style = Res<Style>("DkMini") };
            _dtSignIn.Click += OnDtSignIn;
            _dtReconnect.Click += OnDtReconnect;
            _dtAddCredits.Click += (_, _) => OpenCreditsPage();
            _dtSignOut.Click += OnDtSignOut;
            dtButtons.Children.Add(_dtSignIn);
            dtButtons.Children.Add(_dtReconnect);
            dtButtons.Children.Add(_dtAddCredits);
            dtButtons.Children.Add(_dtSignOut);
            account.Children.Add(dtButtons);

            account.Children.Add(Note("Keys are created and revoked at devthrottle.com; AgentEyes stores only the "
                + "one this machine uses, encrypted with Windows DPAPI. Add credits at devthrottle.com.", topMargin: 16));

            RefreshDtStatus();

            // ---- Plugins tab (issue #61): managed in a dedicated Plugin Manager window ----
            var plugins = new StackPanel();
            plugins.Children.Add(Note("Plugins turn a finished recording into something else - documentation, "
                + "a QA report, meeting notes. Install (from a file or the catalog), enable, configure, and remove "
                + "them in the Plugin Manager.", topMargin: 0));
            var manageBtn = new Button
            {
                Content = "Manage plugins...", MinWidth = 150, Height = 30,
                Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Style = Res<Style>("DkPrimary"),
            };
            manageBtn.Click += (_, _) => new PluginManagerWindow(_cfg) { Owner = this }.ShowDialog();
            plugins.Children.Add(manageBtn);

            // ---- tabs + buttons ----
            // Tab order (issue #88): General, Account, Plugins
            var tabs = new TabControl { Style = Res<Style>("DkTab"), Height = 360 };
            tabs.Items.Add(Tab("General", general));
            tabs.Items.Add(Tab("Account", account));
            tabs.Items.Add(Tab("Plugins", plugins));

            if (!string.IsNullOrWhiteSpace(initialTab))
            {
                foreach (TabItem t in tabs.Items)
                {
                    if (!string.Equals(t.Header as string, initialTab, StringComparison.OrdinalIgnoreCase)) continue;
                    tabs.SelectedItem = t;
                    break;
                }
            }

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(tabs);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = Res<Style>("DkPrimary") };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Style = Res<Style>("DkMini") };
            ok.Click += OnOk;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            bool login = _login.IsChecked == true;
            if (login != Autostart.IsEnabled()) Autostart.Set(login);
            _cfg.RunAtLogin = login;
            _cfg.AutoUpdate = _autoUpdate.IsChecked == true;
            _cfg.ApiEnabled = _api.IsChecked == true;
            if (int.TryParse(_port.Text.Trim(), out int port) && port is > 0 and < 65536) _cfg.Port = port;

            _cfg.Save();
            DialogResult = true;
            Close();
        }

        // ---- DevThrottle Account tab helpers (issue #87) ----

        private void RefreshDtStatus()
        {
            var cred = DevThrottleAccount.Load();
            bool signedIn = cred?.ApiKey is { Length: > 0 };
            if (signedIn)
            {
                _dtStatus.Text = "Connected" + (string.IsNullOrWhiteSpace(cred!.Email) ? "" : " as " + cred.Email);
                _dtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0x6a)); // green
                _dtKeyLine.Text = "Key: " + MaskKey(cred.ApiKey);
                _dtCreditsLine.Text = "Credits: loading...";
                _ = RefreshDtCreditsAsync();
            }
            else
            {
                _dtStatus.Text = "Not signed in - sign in to enable transcription and AI.";
                _dtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xd8, 0xa6, 0x57)); // amber
                _dtKeyLine.Text = "";
                _dtCreditsLine.Text = "";
            }
            _dtSignIn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            _dtReconnect.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            _dtAddCredits.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            _dtSignOut.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task RefreshDtCreditsAsync()
        {
            try
            {
                var credits = await DevThrottleClient.GetCreditsAsync();
                ShowCredits(credits);
            }
            catch (DevThrottleException ex) when (ex.Status == 401)
            {
                _dtCreditsLine.Text = "Credits: reconnect to refresh your account balance.";
                _dtCreditsLine.Foreground = new SolidColorBrush(Color.FromRgb(0xd8, 0xa6, 0x57));
            }
            catch (Exception ex)
            {
                _dtCreditsLine.Text = "Credits: unavailable (" + ex.Message + ")";
                _dtCreditsLine.Foreground = new SolidColorBrush(Color.FromRgb(0xd8, 0xa6, 0x57));
            }
        }

        internal void RefreshDevThrottleCredits(DevThrottleCredits credits)
        {
            Dispatcher.BeginInvoke(() => ShowCredits(credits));
        }

        private void ShowCredits(DevThrottleCredits credits)
        {
            _dtCreditsLine.Text = "Credits: " + FormatMicros(credits.BalanceMicros);
            _dtCreditsLine.Foreground = credits.BalanceMicros <= 0
                ? new SolidColorBrush(Color.FromRgb(0xd8, 0xa6, 0x57))
                : Res<Brush>("DkDim");
        }

        private static string FormatMicros(long micros)
        {
            decimal dollars = micros / 1_000_000m;
            return dollars.ToString("$0.00", CultureInfo.InvariantCulture);
        }

        private static void OpenCreditsPage()
        {
            Process.Start(new ProcessStartInfo(DevThrottleAccount.CreditsUrl) { UseShellExecute = true });
        }

        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string tail = key.Length >= 4 ? key.Substring(key.Length - 4) : key;
            int first = key.IndexOf('_');
            int second = first >= 0 ? key.IndexOf('_', first + 1) : -1;
            string prefix = second > 0 ? key.Substring(0, second + 1)
                          : first > 0 ? key.Substring(0, first + 1) : "";
            return prefix + "..." + tail;
        }

        // Each of these three CHANGES the stored credential, so the app-wide indicator is
        // re-read here (issue #129). Deliberately not inside RefreshDtStatus() - that also runs
        // when the dialog merely opens, and refreshing there would clear a live 401 and make a
        // stale key look signed in again.

        private void OnDtSignIn(object sender, RoutedEventArgs e)
        {
            if (!DevThrottleSignInWindow.Prompt(this)) return;
            RefreshDtStatus();
            AccountState.Refresh();
        }

        private void OnDtReconnect(object sender, RoutedEventArgs e)
        {
            if (!DevThrottleSignInWindow.Prompt(this, reconnect: true)) return;
            RefreshDtStatus();
            AccountState.Refresh();
        }

        private void OnDtSignOut(object sender, RoutedEventArgs e)
        {
            DevThrottleAccount.Clear();
            RefreshDtStatus();
            AccountState.Refresh();
        }

        private static TabItem Tab(string header, UIElement content) =>
            new TabItem { Header = header, Content = content, Style = Res<Style>("DkTabItem") };

        private static TextBlock Note(string text, double topMargin = 8) => new TextBlock
        {
            Text = text,
            Foreground = Res<Brush>("DkDim"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, topMargin, 0, 0),
        };

        private static ComboBox LabeledCombo(Panel host, string label, string[] items, int selected)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 70, Foreground = Res<Brush>("DkText") });
            var combo = new ComboBox { Width = FieldWidth, Height = 30, Style = Res<Style>("DkCombo") };
            foreach (var it in items) combo.Items.Add(it);
            combo.SelectedIndex = selected;
            row.Children.Add(combo);
            host.Children.Add(row);
            return combo;
        }
    }
}
