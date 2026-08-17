using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AgentEyes;
using AgentEyes.Plugins;

namespace AgentEyes.App
{
    /// <summary>
    /// The Plugin Manager (issue #61): a list-based home for installed plugins -
    /// enable/disable, configure, update, remove - plus install from a local file
    /// (a plugin .zip or folder, including drag-and-drop) or the catalog. Replaces the
    /// cramped Settings > Plugins tab. Every change (enable, settings, install, remove)
    /// is saved immediately; install/remove/extract logic lives in Core's PluginPackage.
    /// </summary>
    internal sealed class PluginManagerWindow : Window
    {
        private readonly Config _cfg;
        private readonly StackPanel _list;
        private readonly TextBlock _status;
        private List<RegistryPlugin> _available = new();   // registry entries, for update badges

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal PluginManagerWindow(Config cfg)
        {
            _cfg = cfg;
            Title = "Plugins";
            Width = 660; Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            AllowDrop = true;
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            Drop += OnDrop;
            DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var fromFile = new Button { Content = "Install from file...", MinWidth = 130, Height = 30, Style = Res<Style>("DkPrimary") };
            fromFile.Click += (_, _) => InstallFromFileDialog();
            var fromCatalog = new Button { Content = "Browse catalog...", MinWidth = 130, Height = 30, Margin = new Thickness(8, 0, 0, 0), Style = Res<Style>("DkMini") };
            fromCatalog.Click += (_, _) =>
            {
                var c = new PluginCatalogDialog(_cfg) { Owner = this };
                c.ShowDialog();
                if (c.Changed) Rebuild();
            };
            top.Children.Add(fromFile);
            top.Children.Add(fromCatalog);
            root.Children.Add(top);

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
                Text = "", FontSize = 11, Foreground = Res<Brush>("DkDim"),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            bottom.Children.Add(_status);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = root;
            Rebuild();
            // Best-effort update check: annotate rows once the registry answers (failure = no badges).
            Loaded += async (_, _) =>
            {
                try { _available = await PluginRegistry.FetchAsync(_cfg); Rebuild(); }
                catch { /* registry unreachable: updates simply are not shown */ }
            };
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            var installed = Plugins.Load();
            if (installed.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = "No plugins installed. Install one from a file (a plugin .zip or folder), or browse the "
                         + "catalog. You can also drop a plugin .zip or folder straight onto this window.",
                    Foreground = Res<Brush>("DkDim"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 8, 2, 0),
                });
                return;
            }
            foreach (var p in installed) _list.Children.Add(Row(p));
            _list.Children.Add(new TextBlock
            {
                Text = "Enabled plugins run after each recording is transcribed, each in its own process. "
                     + "Installing from a file runs third-party code on your machine.",
                Foreground = Res<Brush>("DkDim"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 10, 2, 0),
            });
        }

        private Border Row(PluginInfo p)
        {
            var card = new Border
            {
                Background = Res<Brush>("DkCard"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
            };
            var row = new DockPanel();

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var reg = _available.FirstOrDefault(r => r.Id == p.Id);
            if (reg != null && PluginRegistry.IsUpdate(p.Version, reg.Version))
            {
                var upd = new Button { Content = $"Update to {reg.Version}", MinWidth = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0), Style = Res<Style>("DkMini") };
                upd.Click += async (_, _) =>
                {
                    upd.IsEnabled = false; upd.Content = "Updating...";
                    try { await PluginRegistry.InstallAsync(reg); _status.Text = $"Updated {p.Name} to {reg.Version}."; Rebuild(); }
                    catch (Exception ex) { _status.Text = ex.Message; upd.IsEnabled = true; upd.Content = $"Update to {reg.Version}"; }
                };
                actions.Children.Add(upd);
            }
            if (p.Settings.Length > 0)
            {
                var cfgBtn = new Button { Content = "Configure", MinWidth = 86, Height = 28, Style = Res<Style>("DkMini") };
                cfgBtn.Click += (_, _) => new PluginConfigDialog(p) { Owner = this }.ShowDialog();
                actions.Children.Add(cfgBtn);
            }
            var removeBtn = new Button { Content = "Remove", MinWidth = 74, Height = 28, Margin = new Thickness(8, 0, 0, 0), Style = Res<Style>("DkMini") };
            removeBtn.Click += (_, _) => Remove(p);
            actions.Children.Add(removeBtn);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);

            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var enable = new CheckBox
            {
                Content = $"{p.Name}  ({p.Version})",
                IsChecked = _cfg.EnabledPlugins.Contains(p.Id),
                Style = Res<Style>("DkCheck"),
            };
            System.Windows.Automation.AutomationProperties.SetName(enable, "Enable " + p.Id);
            enable.Checked += (_, _) => SetEnabled(p.Id, true);
            enable.Unchecked += (_, _) => SetEnabled(p.Id, false);
            left.Children.Add(enable);
            if (p.Description.Length > 0)
                left.Children.Add(new TextBlock
                {
                    Text = p.Description, Foreground = Res<Brush>("DkDim"), FontSize = 11,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
                });
            row.Children.Add(left);

            card.Child = row;
            return card;
        }

        private void SetEnabled(string id, bool on)
        {
            _cfg.EnabledPlugins.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (on) _cfg.EnabledPlugins.Add(id);
            _cfg.Save();
        }

        private void Remove(PluginInfo p)
        {
            if (MessageBox.Show($"Remove {p.Name}? This deletes the plugin and its settings.",
                "Remove plugin", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                PluginPackage.Remove(Plugins.Root, p.Id);
                SetEnabled(p.Id, false);
                _status.Text = $"Removed {p.Name}.";
                Rebuild();
            }
            catch (Exception ex) { _status.Text = ex.Message; Log.Error("plugin remove " + p.Id, ex); }
        }

        private void InstallFromFileDialog()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Install plugin from file",
                Filter = "Plugin package (*.zip)|*.zip|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == true) InstallFromPath(dlg.FileName);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths.Length > 0) InstallFromPath(paths[0]);
        }

        private void InstallFromPath(string path)
        {
            try
            {
                string id = Directory.Exists(path)
                    ? PluginPackage.InstallFolder(path, Plugins.Root)
                    : PluginPackage.InstallZipFile(path, Plugins.Root);
                Rebuild();
                var plugin = Plugins.Load().FirstOrDefault(x => x.Id == id);
                if (plugin != null) OfferEnable(plugin);
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
                Log.Error("plugin install from " + path, ex);
                MessageBox.Show(ex.Message, "Install failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>After a local install, show the command the plugin will run (local installs
        /// carry no signature) and let the user enable it right there - nothing runs until enabled.</summary>
        private void OfferEnable(PluginInfo p)
        {
            string command = string.Join(" ", p.Command);
            var result = MessageBox.Show(
                $"Installed {p.Name} ({p.Version}).\n\n"
                + "Plugins run third-party code on your machine. After each recording, this one runs:\n\n"
                + $"    {command}\n\n"
                + "Enable it now?",
                "Plugin installed", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                SetEnabled(p.Id, true);
                Rebuild();
            }
            else
            {
                _status.Text = $"Installed {p.Name} (disabled - enable it when ready).";
            }
        }
    }
}
