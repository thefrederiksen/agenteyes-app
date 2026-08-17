using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentEyes.App
{
    /// <summary>Minimal single-line text prompt (used for Rename and Save as). Returns the entered text or null.</summary>
    internal sealed class PromptDialog : Window
    {
        private readonly TextBox _box;

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        private PromptDialog(string title, string prompt, string initial)
        {
            Title = title;
            Width = 380; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), Foreground = Res<Brush>("DkText") });
            _box = new TextBox { Text = initial, Height = 30, Style = Res<Style>("DkTextBox") };
            _box.SelectAll();
            panel.Children.Add(_box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = Res<Style>("DkPrimary") };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Style = Res<Style>("DkMini") };
            ok.Click += (_, _) => { DialogResult = true; Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }

        public string Value => _box.Text.Trim();

        public static string? Ask(Window owner, string title, string prompt, string initial = "")
        {
            var dlg = new PromptDialog(title, prompt, initial) { Owner = owner };
            return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
        }
    }
}
