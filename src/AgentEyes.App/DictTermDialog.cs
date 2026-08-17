using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentEyes.App
{
    /// <summary>
    /// Edit one dictionary term (issue #37): the canonical spelling plus the list
    /// of misheard forms, one per line. Covers add AND remove of variants in one
    /// place; the quick "+ misheard" path on the list stays for one-off additions.
    /// </summary>
    internal sealed class DictTermDialog : Window
    {
        private readonly TextBox _term;
        private readonly TextBox _variants;

        internal string TermText => _term.Text.Trim();
        // Ordinal dedupe: casing matters for mistranscription matching
        // ("CC Director" and "CC director" are distinct wrong forms).
        internal List<string> Variants => _variants.Text
            .Split('\n')
            .Select(v => v.Trim().TrimEnd('\r'))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        internal DictTermDialog(string term, IEnumerable<string> variants)
        {
            Title = "Edit term";
            Width = 460; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Res<Brush>("DkBg");
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(Label("Term (the correct spelling)"));
            _term = new TextBox { Text = term, Height = 28, Style = Res<Style>("DkTextBox"), Margin = new Thickness(0, 4, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_term, "Term");
            root.Children.Add(_term);

            root.Children.Add(Label("Misheard as (one per line)", topMargin: 12));
            _variants = new TextBox
            {
                Text = string.Join(Environment.NewLine, variants),
                Height = 140,
                Style = Res<Style>("DkTextBox"),
                Margin = new Thickness(0, 4, 0, 0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            System.Windows.Automation.AutomationProperties.SetName(_variants, "Misheard forms");
            root.Children.Add(_variants);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Style = Res<Style>("DkPrimary") };
            ok.Click += (_, _) =>
            {
                if (TermText.Length == 0)
                {
                    MessageBox.Show("The term cannot be empty.", "AgentEyes", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DialogResult = true;
                Close();
            };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Style = Res<Style>("DkMini") };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, _) => { _term.Focus(); _term.CaretIndex = _term.Text.Length; };
        }

        private static TextBlock Label(string text, double topMargin = 0) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = Res<Brush>("DkDim"),
            Margin = new Thickness(0, topMargin, 0, 0),
        };
    }
}
