using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyes.DevThrottle;

namespace AgentEyes.App
{
    /// <summary>
    /// Modal DevThrottle sign-in (issue #87). Runs the loopback browser handback and shows progress.
    /// Used both from Settings (Account tab) and as the first-run gate. Dark, self-contained styling
    /// so it works before the app resource dictionary is guaranteed loaded.
    /// </summary>
    internal sealed class DevThrottleSignInWindow : Window
    {
        private readonly TextBlock _status;
        private readonly Button _signIn;
        private CancellationTokenSource? _cts;

        internal DevThrottleSignInWindow(bool reconnect)
        {
            Title = reconnect ? "Reconnect to DevThrottle" : "Sign in to DevThrottle";
            Width = 470; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            var fg = new SolidColorBrush(Color.FromRgb(0xe6, 0xe8, 0xec));
            var dim = new SolidColorBrush(Color.FromRgb(0x9a, 0xa0, 0xa8));

            var root = new StackPanel { Margin = new Thickness(22) };
            root.Children.Add(new TextBlock
            {
                Text = reconnect ? "Reconnect to DevThrottle" : "Sign in to DevThrottle",
                Foreground = fg, FontSize = 18, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });
            root.Children.Add(new TextBlock
            {
                Text = "AgentEyes runs on your DevThrottle account - it powers transcription and AI, billed "
                     + "to your DevThrottle credits. Your browser opens to approve this device; no password "
                     + "is ever typed into AgentEyes.",
                Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });

            _status = new TextBlock
            {
                Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14), MinHeight = 18,
            };
            root.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _signIn = new Button { Content = reconnect ? "Reconnect" : "Sign in", MinWidth = 100, Height = 30, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, Height = 30, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            _signIn.Click += OnSignIn;
            buttons.Children.Add(_signIn);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            Closed += (_, _) => { try { _cts?.Cancel(); } catch { /* nothing to cancel */ } };
        }

        private async void OnSignIn(object sender, RoutedEventArgs e)
        {
            _signIn.IsEnabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                await DevThrottleSignIn.SignInAsync(
                    msg => Dispatcher.Invoke(() => _status.Text = msg), _cts.Token);
                _status.Text = "Signed in.";
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                AgentEyes.Log.Error("DevThrottle sign-in failed", ex);
                _status.Text = "Sign-in failed: " + ex.Message;
                _signIn.IsEnabled = true;
            }
        }

        /// <summary>Shows the modal sign-in. Returns true when a credential was obtained and stored.</summary>
        internal static bool Prompt(Window? owner, bool reconnect = false)
        {
            var w = new DevThrottleSignInWindow(reconnect);
            if (owner != null) w.Owner = owner;
            return w.ShowDialog() == true;
        }
    }
}
