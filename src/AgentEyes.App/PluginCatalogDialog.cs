using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentEyes.App
{
    /// <summary>
    /// "Get plugins" (issue #32): browse the registry, install or update with one
    /// click. Every failure (registry unreachable, hash mismatch, bad zip) surfaces
    /// verbatim in the dialog - the install either works or says exactly why not.
    /// </summary>
    internal sealed class PluginCatalogDialog : Window
    {
        private readonly Config _cfg;
        private readonly StackPanel _list;
        private readonly TextBlock _status;

        /// <summary>True when anything was installed/updated (caller refreshes its plugin list).</summary>
        internal bool Changed { get; private set; }

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal PluginCatalogDialog(Config cfg)
        {
            _cfg = cfg;
            Title = "Get plugins";
            Width = 600; Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var source = new TextBlock
            {
                Text = "Registry: " + PluginRegistry.UrlFor(cfg),
                FontSize = 11,
                Foreground = Res<Brush>("DkDim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 10),
            };
            root.Children.Add(source);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _list = new StackPanel();
            scroll.Content = _list;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, Style = Res<Style>("DkPrimary") };
            close.Click += (_, _) => Close();
            DockPanel.SetDock(close, Dock.Right);
            bottom.Children.Add(close);
            _status = new TextBlock
            {
                Text = "Loading registry...",
                FontSize = 11,
                Foreground = Res<Brush>("DkDim"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            bottom.Children.Add(_status);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = root;
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            _list.Children.Clear();
            try
            {
                var available = await PluginRegistry.FetchAsync(_cfg);
                _status.Text = available.Count == 0 ? "The registry lists no plugins yet." : "";
                foreach (var p in available) _list.Children.Add(Row(p));
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }
        }

        private UIElement Row(RegistryPlugin p)
        {
            var card = new Border
            {
                Background = Res<Brush>("DkCard"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var row = new DockPanel();

            string? installed = PluginRegistry.InstalledVersion(p.Id);
            var button = new Button { MinWidth = 86, Height = 28, Style = Res<Style>("DkMini"), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(button, "Install " + p.Id);
            if (installed == null) button.Content = "Install";
            else if (PluginRegistry.IsUpdate(installed, p.Version)) button.Content = $"Update to {p.Version}";
            else { button.Content = "Installed"; button.IsEnabled = false; }

            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                button.Content = "Installing...";
                _status.Text = "";
                try
                {
                    await PluginRegistry.InstallAsync(p);
                    button.Content = "Installed";
                    Changed = true;
                    _status.Text = $"{p.Name} {p.Version} installed. Enable it on the Plugins tab.";
                }
                catch (Exception ex)
                {
                    button.Content = installed == null ? "Install" : "Update";
                    button.IsEnabled = true;
                    _status.Text = ex.Message;
                    Log.Error("plugin install " + p.Id, ex);
                }
            };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);

            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("DkText") };
            title.Text = $"{p.Name}  ({p.Version})" + (installed != null ? $"   -   installed: {installed}" : "");
            text.Children.Add(title);
            if (p.Description.Length > 0)
                text.Children.Add(new TextBlock
                {
                    Text = p.Description,
                    FontSize = 11,
                    Foreground = Res<Brush>("DkDim"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });
            row.Children.Add(text);

            card.Child = row;
            return card;
        }
    }
}
