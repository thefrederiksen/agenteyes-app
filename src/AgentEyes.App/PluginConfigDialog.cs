using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentEyes.App
{
    /// <summary>
    /// Per-plugin settings editor (issue #61), opened from the Plugin Manager's
    /// "Configure". Renders the plugin's declared settings (text / bool) and saves them
    /// to the sibling &lt;id&gt;.settings.json via Plugins.SaveSettings. The same fields
    /// previously lived inline on the Settings > Plugins tab.
    /// </summary>
    internal sealed class PluginConfigDialog : Window
    {
        private readonly PluginInfo _plugin;
        private readonly List<(PluginSetting Setting, FrameworkElement Control)> _controls = new();

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal PluginConfigDialog(PluginInfo plugin)
        {
            _plugin = plugin;
            Title = "Configure " + plugin.Name;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var root = new StackPanel { Margin = new Thickness(18) };
            if (plugin.Description.Length > 0)
                root.Children.Add(new TextBlock
                {
                    Text = plugin.Description, Foreground = Res<Brush>("DkDim"), FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
                });

            var values = Plugins.LoadSettings(plugin);
            if (plugin.Settings.Length == 0)
                root.Children.Add(new TextBlock
                {
                    Text = "This plugin has no settings.", Foreground = Res<Brush>("DkDim"), FontSize = 12,
                });

            foreach (var setting in plugin.Settings)
            {
                string current = values.TryGetValue(setting.Key, out var v) ? v : setting.Default;
                FrameworkElement control;
                if (setting.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
                {
                    var box = new CheckBox
                    {
                        Content = setting.Label,
                        IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase),
                        Style = Res<Style>("DkCheck"),
                        Margin = new Thickness(0, 6, 0, 4),
                        ToolTip = setting.Description.Length > 0 ? setting.Description : null,
                    };
                    System.Windows.Automation.AutomationProperties.SetName(box, setting.Key);
                    root.Children.Add(box);
                    control = box;
                }
                else
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = setting.Label, FontSize = 12, Foreground = Res<Brush>("DkText"),
                        Margin = new Thickness(0, 8, 0, 3),
                        ToolTip = setting.Description.Length > 0 ? setting.Description : null,
                    });
                    var box = new TextBox
                    {
                        Text = current, Height = 28, Style = Res<Style>("DkTextBox"),
                        ToolTip = setting.Description.Length > 0 ? setting.Description : null,
                    };
                    System.Windows.Automation.AutomationProperties.SetName(box, setting.Key);
                    root.Children.Add(box);
                    control = box;
                }
                _controls.Add((setting, control));
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };
            var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Style = Res<Style>("DkPrimary") };
            ok.Click += (_, _) => { Save(); DialogResult = true; };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Style = Res<Style>("DkMini") };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
        }

        private void Save()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (setting, control) in _controls)
                values[setting.Key] = control switch
                {
                    CheckBox cb => cb.IsChecked == true ? "true" : "false",
                    TextBox tb => tb.Text.Trim(),
                    _ => setting.Default,
                };
            Plugins.SaveSettings(_plugin, values);
        }
    }
}
