using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using AgentEyes;
using AgentEyes.AlwaysOn;

namespace AgentEyes.App
{
    /// <summary>
    /// The Always On page (issue #66): the keep rule, the recording setup it uses, the disk cap, and
    /// the Start button. Every setting saves the moment it changes; while always-on runs the settings
    /// are locked, because a run uses the settings it was started with.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>One choice in a drop-down: what it says, and the value it stands for.</summary>
        private sealed class AOChoice
        {
            public AOChoice(string label, double? value) { Label = label; Value = value; }
            public string Label { get; }
            public double? Value { get; }
            public override string ToString() => Label;
        }

        private static readonly double[] AOMinutes = { 1, 2, 3, 5, 10, 15, 30 };
        private static readonly double[] AOCapsGb = { 1, 2, 5, 10, 20, 50, 100 };
        private static readonly double[] AOThresholds = { -25, -30, -35, -40, -45, -50, -55, -60 };

        private AlwaysOnController? _alwaysOn;

        /// <summary>True while the page is filling its controls, so the fill does not save.</summary>
        private bool _aoLoading;

        /// <summary>The in-progress line last shown, so a change is logged once, not every refresh (issue #70).</summary>
        private string? _aoClipNowShown;

        /// <summary>Today's clip list last shown, as one key, so the list is rebuilt only when it changes.</summary>
        private string? _aoClipsShownKey;

        /// <summary>Wire the page to the app's one always-on controller. Called from the constructor.</summary>
        private void InitAlwaysOn(AlwaysOnController? alwaysOn)
        {
            _alwaysOn = alwaysOn;
            if (_alwaysOn == null)
            {
                RailAlwaysOn.Visibility = Visibility.Collapsed;
                return;
            }
            _alwaysOn.Changed += () => Dispatcher.BeginInvoke(new Action(UpdateAlwaysOnStatus));
            Closed += (_, _) => _alwaysOn = null;
        }

        /// <summary>Open the main window on the Always On page (the tray's "Always-on settings").</summary>
        internal void ShowAlwaysOnPage() => RailAlwaysOn.IsChecked = true;

        /// <summary>Fill the page from the saved settings. Presets are read off the UI thread.</summary>
        private async void LoadAlwaysOnPage()
        {
            // Entry point (rail click): failures are shown on the page, never thrown into the dispatcher.
            try
            {
                if (_alwaysOn == null) return;
                AOStatusText.Text = "Loading...";
                var presets = await System.Threading.Tasks.Task.Run(AlwaysOnController.VideoPresets);
                var chosen = await System.Threading.Tasks.Task.Run(() => _alwaysOn?.ChosenPreset());
                if (_alwaysOn == null) return;

                _aoLoading = true;
                try
                {
                    AOPresetCombo.ItemsSource = presets;
                    AOPresetCombo.SelectedItem = presets.FirstOrDefault(p => p.Id == chosen?.Id);

                    switch (AlwaysOnController.ParseCounts(_cfg.AlwaysOnCounts))
                    {
                        case SoundSource.System: AOCountSystem.IsChecked = true; break;
                        case SoundSource.Both: AOCountBoth.IsChecked = true; break;
                        default: AOCountMic.IsChecked = true; break;
                    }

                    var thresholds = new List<AOChoice> { new("Auto", null) };
                    thresholds.AddRange(AOThresholds.Select(d => new AOChoice($"{d:0} dBFS", d)));
                    // Like the minutes and the cap: a saved line that is not one of the choices is
                    // added, so the page never says Auto while a fixed line is in force.
                    if (_cfg.AlwaysOnThresholdDb is double saved && !AOThresholds.Contains(saved))
                        thresholds = thresholds.Take(1)
                            .Concat(thresholds.Skip(1).Append(new AOChoice($"{saved:0.#} dBFS", saved)).OrderByDescending(c => c.Value))
                            .ToList();
                    Fill(AOThresholdCombo, thresholds, _cfg.AlwaysOnThresholdDb);
                    Fill(AOBeforeCombo, Minutes(_cfg.AlwaysOnBeforeMinutes), _cfg.AlwaysOnBeforeMinutes);
                    Fill(AOAfterCombo, Minutes(_cfg.AlwaysOnAfterMinutes), _cfg.AlwaysOnAfterMinutes);
                    Fill(AOCapCombo, Caps(_cfg.AlwaysOnCapGb), _cfg.AlwaysOnCapGb);

                    AOFolderText.Text = _alwaysOn.ClipsFolder;
                    AOFolderText.ToolTip = _alwaysOn.ClipsFolder;
                    AOStartWithWindows.IsChecked = Autostart.IsEnabled();
                }
                finally
                {
                    _aoLoading = false;
                }
                UpdateAlwaysOnRule();
                UpdateAlwaysOnStatus();
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] LoadAlwaysOnPage FAILED", ex);
                AOStatusText.Text = "The Always On page could not load: " + ex.Message;
            }
        }

        private static void Fill(System.Windows.Controls.ComboBox combo, List<AOChoice> items, double? value)
        {
            combo.ItemsSource = items;
            combo.SelectedItem = items.FirstOrDefault(i => Nullable.Equals(i.Value, value)) ?? items[0];
        }

        /// <summary>The minute choices, plus the saved value when it is not one of them (set through
        /// config.json or the API) - the page shows what is in force, never a neighbour of it.</summary>
        private static List<AOChoice> Minutes(double saved)
        {
            var values = AOMinutes.Contains(saved) ? AOMinutes : AOMinutes.Append(saved).OrderBy(v => v).ToArray();
            return values.Select(m => new AOChoice(m == 1 ? "1 minute" : $"{m.ToString("0.##", CultureInfo.InvariantCulture)} minutes", m)).ToList();
        }

        private static List<AOChoice> Caps(double saved)
        {
            var values = AOCapsGb.Contains(saved) ? AOCapsGb : AOCapsGb.Append(saved).OrderBy(v => v).ToArray();
            return values.Select(g => new AOChoice(g <= 0 ? "No cap" : $"{g.ToString("0.##", CultureInfo.InvariantCulture)} GB", g)).ToList();
        }

        /// <summary>Any setting changed: save it now.</summary>
        private void AOSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_aoLoading || _alwaysOn == null || AOPresetCombo == null) return;
            try
            {
                if (AOPresetCombo.SelectedItem is CapturePreset p) _cfg.AlwaysOnPresetId = p.Id;
                _cfg.AlwaysOnCounts = AOCountSystem.IsChecked == true ? "system" : AOCountBoth.IsChecked == true ? "both" : "mic";
                if (AOThresholdCombo.SelectedItem is AOChoice t) _cfg.AlwaysOnThresholdDb = t.Value;
                if (AOBeforeCombo.SelectedItem is AOChoice b && b.Value.HasValue) _cfg.AlwaysOnBeforeMinutes = b.Value.Value;
                if (AOAfterCombo.SelectedItem is AOChoice a && a.Value.HasValue) _cfg.AlwaysOnAfterMinutes = a.Value.Value;
                if (AOCapCombo.SelectedItem is AOChoice c && c.Value.HasValue) _cfg.AlwaysOnCapGb = c.Value.Value;
                _cfg.Save();
                Log.Info($"[MainWindow] AOSetting_Changed: preset={_cfg.AlwaysOnPresetId} counts={_cfg.AlwaysOnCounts} "
                         + $"threshold={(_cfg.AlwaysOnThresholdDb?.ToString("0") ?? "auto")} before={_cfg.AlwaysOnBeforeMinutes} "
                         + $"after={_cfg.AlwaysOnAfterMinutes} cap={_cfg.AlwaysOnCapGb}GB");
                UpdateAlwaysOnRule();
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOSetting_Changed FAILED", ex);
                ShowAlwaysOnError("The setting could not be saved: " + ex.Message);
            }
        }

        private void AOFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Choose where always-on clips are saved",
                    InitialDirectory = _alwaysOn?.ClipsFolder,
                };
                if (dlg.ShowDialog(this) != true) return;
                _cfg.AlwaysOnClipsFolder = dlg.FolderName;
                _cfg.Save();
                AOFolderText.Text = dlg.FolderName;
                AOFolderText.ToolTip = dlg.FolderName;
                Log.Info($"[MainWindow] AOFolder_Click: clips folder -> {dlg.FolderName}");
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOFolder_Click FAILED", ex);
                ShowAlwaysOnError("The folder could not be changed: " + ex.Message);
            }
        }

        private void AOStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool on = AOStartWithWindows.IsChecked == true;
                Autostart.Set(on);
                _cfg.RunAtLogin = on;
                _cfg.Save();
                Log.Info($"[MainWindow] AOStartWithWindows_Click: run at login = {on}");
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOStartWithWindows_Click FAILED", ex);
                ShowAlwaysOnError("Start with Windows could not be changed: " + ex.Message);
                AOStartWithWindows.IsChecked = Autostart.IsEnabled();
            }
        }

        private async void AOStart_Click(object sender, RoutedEventArgs e)
        {
            var ao = _alwaysOn;
            if (ao == null) return;
            bool starting = !ao.IsOn;
            // Immediate feedback: the button says what is happening before the work starts.
            AOStartButton.IsEnabled = false;
            AOStartButton.Content = starting ? "Starting..." : "Stopping...";
            AOErrorText.Visibility = Visibility.Collapsed;
            try
            {
                if (starting) await ao.StartAsync("Always On page");
                else await ao.StopAsync("Always On page");
            }
            catch (Exception ex)
            {
                Log.Error($"[MainWindow] AOStart_Click: {(starting ? "start" : "stop")} FAILED", ex);
                ShowAlwaysOnError((starting ? "Always-on could not start: " : "Always-on could not stop: ") + ex.Message);
            }
            finally
            {
                AOStartButton.IsEnabled = true;
                UpdateAlwaysOnStatus();
            }
        }

        private void AOOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { _alwaysOn?.OpenClipsFolder(); }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOOpenFolder_Click FAILED", ex);
                ShowAlwaysOnError("The clips folder could not be opened: " + ex.Message);
            }
        }

        /// <summary>A clip in today's list was clicked: open its folder with the clip selected (issue #70).</summary>
        private void AOClip_Click(object sender, RoutedEventArgs e)
        {
            // Entry point (click): a clip that is gone is said on the page, never thrown.
            try
            {
                if (sender is not FrameworkElement { Tag: string path }) return;
                Log.Info($"[MainWindow] AOClip_Click: {path}");
                AOErrorText.Visibility = Visibility.Collapsed;
                _alwaysOn?.RevealClip(path);
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOClip_Click FAILED", ex);
                ShowAlwaysOnError("The clip could not be shown: " + ex.Message);
            }
        }

        private void ShowAlwaysOnError(string text)
        {
            AOErrorText.Text = text;
            AOErrorText.Visibility = Visibility.Visible;
        }

        /// <summary>"The rule in words", as the mockup states it.</summary>
        private void UpdateAlwaysOnRule()
        {
            string counts = _cfg.AlwaysOnCounts switch { "system" => "system sound", "both" => "microphone or system sound", _ => "microphone sound" };
            string cap = _cfg.AlwaysOnCapGb > 0 ? $" Never use more than {_cfg.AlwaysOnCapGb:0.##} GB." : "";
            AORuleText.Text = $"The rule in words: record the whole time. Keep {Plural(_cfg.AlwaysOnBeforeMinutes)} before any {counts}, "
                              + $"everything while there is sound, and {Plural(_cfg.AlwaysOnAfterMinutes)} after it. "
                              + $"Delete everything else within minutes.{cap}";
        }

        private static string Plural(double minutes) => minutes == 1 ? "1 minute" : $"{minutes:0.##} minutes";

        /// <summary>The live part of the page: state line, button, counters, lock. UI thread.</summary>
        private void UpdateAlwaysOnStatus()
        {
            var ao = _alwaysOn;
            if (ao == null || AlwaysOnPanel.Visibility != Visibility.Visible) return;
            var s = ao.Status();
            bool on = ao.IsOn;

            string line = s.ThresholdDb.HasValue ? $" Line {s.ThresholdDb.Value:0} dBFS{(s.ThresholdAuto ? " (auto)" : "")}." : "";
            string last = s.LastSoundUtc.HasValue ? $" Last sound {s.LastSoundUtc.Value.ToLocalTime():HH:mm:ss}." : "";
            AOStatusText.Text = "Records all the time and keeps only the stretches with sound. " + (ao.BusyText ?? s.State switch
            {
                AlwaysOnState.Listening => $"Listening - recording \"{s.Setup}\" ({s.Encoder}).{line}{last}",
                AlwaysOnState.Keeping => $"Keeping a clip - recording \"{s.Setup}\" ({s.Encoder}).{line}{last}",
                AlwaysOnState.Paused => $"Paused: {s.PausedReason}.",
                AlwaysOnState.Retrying => $"NOT recording - {s.LastError}. Retrying.",
                _ => "Stopped.",
            });

            if (!ao.Busy)
            {
                AOStartButton.Content = on ? "Stop always-on" : "Start always-on";
                AutomationPropertiesName(AOStartButton, on ? "Stop always-on" : "Start always-on");
            }
            AOSettingsGrid.IsEnabled = !on && !ao.Busy;
            AOLockedNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            AOTodayText.Text = "Today: " + ao.TodaySummary() + UnlistedNote(s);
            UpdateAlwaysOnClips(s);
            string? err = s.State == AlwaysOnState.Retrying ? null : (ao.LastStartError != null && !on ? "Last start failed: " + ao.LastStartError : null);
            if (err != null) ShowAlwaysOnError(err);
        }

        /// <summary>
        /// Issue #70: the in-progress line (how long so far, where it will be saved) and today's clips with
        /// their folders. Everything shown comes from the status snapshot - no disk access here. UI thread.
        /// </summary>
        private void UpdateAlwaysOnClips(AlwaysOnStatus s)
        {
            string? clipNow = s.State == AlwaysOnState.Keeping ? s.InProgressLine(DateTime.UtcNow) : null;
            AOClipNowText.Text = clipNow ?? "";
            AOClipNowText.Visibility = clipNow == null ? Visibility.Collapsed : Visibility.Visible;
            if (clipNow != _aoClipNowShown)
            {
                Log.Info($"[MainWindow] UpdateAlwaysOnClips: in-progress line -> {clipNow ?? "(none)"}");
                _aoClipNowShown = clipNow;
            }

            string key = string.Join("|", s.ClipsKeptToday.Select(c => c.Path + (c.Exists ? "" : "?")));
            if (key == _aoClipsShownKey) return;
            _aoClipsShownKey = key;
            AOTodayClipsList.ItemsSource = s.ClipsKeptToday
                .Select(c => new AOClipRow(c.Label, c.Path, c.Exists ? "Open the folder with this clip selected" : c.Path + " is no longer there"))
                .ToList();
            Log.Info($"[MainWindow] UpdateAlwaysOnClips: listing {s.ClipsKeptToday.Count} clips kept today");
        }

        /// <summary>Clips counted today but not listed - kept before the list existed (issue #70).</summary>
        private static string UnlistedNote(AlwaysOnStatus s)
        {
            int unlisted = s.ClipsToday - s.ClipsKeptToday.Count;
            return unlisted > 0 ? $" {unlisted} of today's clips were kept before AgentEyes listed where clips go, so they are not listed below." : "";
        }

        /// <summary>One row of today's clip list.</summary>
        private sealed record AOClipRow(string Label, string Path, string Hint);

        private static void AutomationPropertiesName(DependencyObject d, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(d, name);
    }
}
