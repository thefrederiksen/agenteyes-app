using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentEyes.LinuxApp.Views
{
    /// <summary>
    /// The small modal dialogs the Windows app gets from MessageBox and PromptDialog (issue #107):
    /// confirm, prompt for text, choose one of a list, and a plain message. Built in code, dark like
    /// the rest of the app, and shown immediately.
    /// </summary>
    internal static class Dialogs
    {
        public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string yes = "Delete", bool danger = true)
        {
            bool result = false;
            var win = Shell(title, 440);
            var ok = new Button { Content = yes, Classes = { danger ? "danger" : "primary" }, IsDefault = true, MinWidth = 90 };
            if (danger) ok.Classes.Add("mini");
            var cancel = new Button { Content = "Cancel", Classes = { "mini" }, IsCancel = true, MinWidth = 90 };
            ok.Click += (_, _) => { result = true; win.Close(); };
            cancel.Click += (_, _) => win.Close();
            win.Content = Body(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 }, cancel, ok);
            await win.ShowDialog(owner);
            Log.Info($"[Dialogs] ConfirmAsync: '{title}' -> {result}");
            return result;
        }

        public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial)
        {
            string? result = null;
            var win = Shell(title, 460);
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true, MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", Classes = { "mini" }, IsCancel = true, MinWidth = 90 };
            ok.Click += (_, _) => { result = box.Text?.Trim(); win.Close(); };
            cancel.Click += (_, _) => win.Close();
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Dim });
            stack.Children.Add(box);
            win.Content = Body(stack, cancel, ok);
            win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
            await win.ShowDialog(owner);
            Log.Info($"[Dialogs] PromptAsync: '{title}' -> {(result == null ? "(cancelled)" : result)}");
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        public static async Task<string?> ChooseAsync(Window owner, string title, string label, IReadOnlyList<string> options)
        {
            string? result = null;
            var win = Shell(title, 420);
            var combo = new ComboBox { ItemsSource = options, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true, MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", Classes = { "mini" }, IsCancel = true, MinWidth = 90 };
            ok.Click += (_, _) => { result = combo.SelectedItem as string; win.Close(); };
            cancel.Click += (_, _) => win.Close();
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Dim });
            stack.Children.Add(combo);
            win.Content = Body(stack, cancel, ok);
            await win.ShowDialog(owner);
            Log.Info($"[Dialogs] ChooseAsync: '{title}' -> {result ?? "(cancelled)"}");
            return result;
        }

        public static async Task InfoAsync(Window owner, string title, string message)
        {
            var win = Shell(title, 440);
            var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true, IsCancel = true, MinWidth = 90 };
            ok.Click += (_, _) => win.Close();
            win.Content = Body(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 }, ok);
            await win.ShowDialog(owner);
        }

        private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9AA0AA"));

        private static Window Shell(string title, double width)
        {
            var w = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
            return w;
        }

        private static Control Body(Control content, params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            var stack = new StackPanel { Margin = new Thickness(22, 20) };
            stack.Children.Add(content);
            stack.Children.Add(row);
            return stack;
        }
    }
}
