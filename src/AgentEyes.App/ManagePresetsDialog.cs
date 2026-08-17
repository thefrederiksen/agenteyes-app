using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// Manage Presets (issue #21): the home of all preset CRUD after the inline
    /// links left the main window. Operates directly on the shared collection;
    /// PresetEditor stays unchanged behind Edit/New.
    /// </summary>
    internal sealed class ManagePresetsDialog : Window
    {
        private readonly ObservableCollection<CapturePreset> _presets;
        private readonly Config _cfg;
        private readonly ListBox _list;

        /// <summary>The preset the caller should treat as active when the dialog closes.</summary>
        internal CapturePreset? ActivePreset { get; private set; }

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal ManagePresetsDialog(ObservableCollection<CapturePreset> presets, Config cfg, CapturePreset? active)
        {
            _presets = presets;
            _cfg = cfg;
            ActivePreset = active;

            Title = "Manage presets";
            Width = 560; Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var root = new Grid { Margin = new Thickness(18) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var listHost = new Border
            {
                Background = Res<Brush>("DkCard"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3),
            };
            _list = new ListBox
            {
                DisplayMemberPath = "Name",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemContainerStyle = Res<Style>("DkSelectItem"),
                ItemsSource = _presets,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_list, "PresetList");
            _list.MouseDoubleClick += (_, _) => Edit();
            listHost.Child = _list;
            root.Children.Add(listHost);

            var buttons = new StackPanel { Margin = new Thickness(14, 0, 0, 0), Width = 130 };
            buttons.Children.Add(Btn("New...", (_, _) => New()));
            buttons.Children.Add(Btn("Edit...", (_, _) => Edit()));
            buttons.Children.Add(Btn("Duplicate", (_, _) => Duplicate()));
            buttons.Children.Add(Btn("Rename...", (_, _) => Rename()));
            buttons.Children.Add(Btn("Delete", (_, _) => Delete()));
            buttons.Children.Add(Btn("Set active", (_, _) => SetActive(), topMargin: 18));
            Grid.SetColumn(buttons, 1);
            root.Children.Add(buttons);

            var close = new Button
            {
                Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 90,
                Style = Res<Style>("DkPrimary"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            close.Click += (_, _) => Close();
            Grid.SetRow(close, 1); Grid.SetColumnSpan(close, 2);
            root.Children.Add(close);

            Content = root;
            _list.SelectedItem = active ?? _presets.FirstOrDefault();
        }

        private Button Btn(string text, RoutedEventHandler onClick, double topMargin = 0)
        {
            var b = new Button
            {
                Content = text,
                Style = Res<Style>("DkMini"),
                Margin = new Thickness(0, topMargin == 0 ? 0 : topMargin, 0, 8),
                Height = 30,
            };
            b.Click += onClick;
            return b;
        }

        private CapturePreset? Selected => _list.SelectedItem as CapturePreset;

        private void Persist()
        {
            PresetStore.Save(_presets.ToList());
            _list.Items.Refresh();
        }

        /// <summary>Also reachable directly via the dropdown's "New preset...".</summary>
        internal void New()
        {
            var p = PresetStore.Default();
            p.Name = "New preset";
            EditAndApply(p);
        }

        private void Edit()
        {
            if (Selected != null) EditAndApply(Selected);
        }

        private void EditAndApply(CapturePreset preset)
        {
            var editor = new PresetEditor(preset) { Owner = this };
            if (editor.ShowDialog() != true || editor.SavedPreset == null) return;
            var saved = editor.SavedPreset;
            if (!_presets.Contains(saved)) _presets.Add(saved);   // new (New flow) or Save-as result
            Persist();
            _list.SelectedItem = saved;
            _list.ScrollIntoView(saved);
            ActivePreset = saved;   // a just-saved preset becomes the active one, visibly
        }

        private void Duplicate()
        {
            if (Selected == null) return;
            var copy = Selected.Clone();
            copy.Name = Selected.Name + " (copy)";
            _presets.Add(copy);
            Persist();
            _list.SelectedItem = copy;
        }

        private void Rename()
        {
            if (Selected == null) return;
            string? name = PromptDialog.Ask(this, "Rename preset", "New name:", Selected.Name);
            if (name == null) return;
            Selected.Name = name;
            Persist();
        }

        private void Delete()
        {
            if (Selected == null) return;
            if (_presets.Count <= 1)
            {
                MessageBox.Show("Can't delete the last preset.", "AgentEyes",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var p = Selected;
            if (MessageBox.Show($"Delete preset \"{p.Name}\"?", "AgentEyes", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _presets.Remove(p);
            if (_cfg.LastUsedPresetId == p.Id) { _cfg.LastUsedPresetId = null; _cfg.Save(); }
            if (ActivePreset == p) ActivePreset = _presets.FirstOrDefault();
            Persist();
            _list.SelectedItem = _presets.FirstOrDefault();
        }

        private void SetActive()
        {
            if (Selected == null) return;
            ActivePreset = Selected;
            _cfg.LastUsedPresetId = Selected.Id;
            _cfg.Save();
        }
    }
}
