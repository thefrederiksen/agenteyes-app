using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentEyes;
using AgentEyes.AlwaysOn;

namespace AgentEyes.App
{
    /// <summary>
    /// The History tab of the Always On page (issue #77): every decision, level line and problem
    /// always-on recorded, newest first, filtered All / Decisions / Levels / Problems, live while the
    /// tab is open.
    ///
    /// RESPONSIVE: the tab paints at once with "Loading..."; the history file is read and the rows are
    /// built on a worker; the finished collection is put on the list back on the UI thread. A new
    /// event arrives on the engine's thread and is dispatched to the UI thread before it touches the
    /// collection. While the tab is not showing, new events only mark it stale; showing it reloads.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>One row of the History list.</summary>
        private sealed record AOHistoryRow(string When, string Badge, string Text, Brush BadgeBrush, Brush TextBrush, string? Detail);

        private ObservableCollection<AOHistoryRow> _aoHistoryRows = new();
        private HistoryFilter _aoHistoryFilter = HistoryFilter.All;
        private bool _aoHistoryStale = true;
        private bool _aoHistoryLoading;
        private int _aoHistoryLoadSeq;
        private Action<AlwaysOnEvent>? _aoHistoryListener;

        /// <summary>Wire the tab to the controller's history. Called from <see cref="InitAlwaysOn"/>.</summary>
        private void InitAlwaysOnHistory(AlwaysOnController alwaysOn)
        {
            AOHistoryList.ItemsSource = _aoHistoryRows;
            _aoHistoryListener = OnAlwaysOnHistoryAppended;
            alwaysOn.History.Appended += _aoHistoryListener;
            Closed += (_, _) =>
            {
                if (_aoHistoryListener != null) alwaysOn.History.Appended -= _aoHistoryListener;
                _aoHistoryListener = null;
            };
            Log.Info($"[MainWindow] InitAlwaysOnHistory: history file {alwaysOn.History.Path}");
        }

        /// <summary>True while the History tab is the one on screen.</summary>
        private bool AlwaysOnHistoryVisible =>
            AlwaysOnPanel.Visibility == Visibility.Visible && ReferenceEquals(AOTabs.SelectedItem, AOTabHistory);

        /// <summary>The page was shown or its tab changed: load the history when it is on screen and
        /// has not been loaded (or new events arrived while it was hidden).</summary>
        private void ShowAlwaysOnHistoryIfSelected()
        {
            if (!AlwaysOnHistoryVisible) return;
            if (_aoHistoryStale && !_aoHistoryLoading) LoadAlwaysOnHistory();
        }

        private void AOTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Entry point. SelectionChanged bubbles up from the combo boxes inside the Recording tab,
            // so only the tab control's own change counts.
            if (!ReferenceEquals(e.OriginalSource, AOTabs)) return;
            try
            {
                Log.Info($"[MainWindow] AOTabs_SelectionChanged: {(ReferenceEquals(AOTabs.SelectedItem, AOTabHistory) ? "History" : "Recording")}");
                ShowAlwaysOnHistoryIfSelected();
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOTabs_SelectionChanged FAILED", ex);
                AOHistoryStatus.Text = "The history could not be shown: " + ex.Message;
            }
        }

        /// <summary>A filter radio was checked: reload with it.</summary>
        private void AOHistoryFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Entry point. Fires during InitializeComponent for the checked default - the list is not built yet.
            if (AOHistoryList == null || sender is not RadioButton { Tag: string tag }) return;
            try
            {
                _aoHistoryFilter = AlwaysOnHistory.ParseFilter(tag);
                Log.Info($"[MainWindow] AOHistoryFilter_Changed: {_aoHistoryFilter}");
                _aoHistoryStale = true;
                ShowAlwaysOnHistoryIfSelected();
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] AOHistoryFilter_Changed FAILED", ex);
                AOHistoryStatus.Text = "The filter could not be applied: " + ex.Message;
            }
        }

        /// <summary>Read the history on a worker and show it. Immediate feedback, then the rows.</summary>
        private async void LoadAlwaysOnHistory()
        {
            // Entry point (async void): failures are shown on the tab, never thrown into the dispatcher.
            var ao = _alwaysOn;
            if (ao == null) return;
            int seq = ++_aoHistoryLoadSeq;
            _aoHistoryLoading = true;
            _aoHistoryStale = false;
            AOHistoryStatus.Text = "Loading...";
            var filter = _aoHistoryFilter;
            var brushes = AlwaysOnHistoryBrushes();
            try
            {
                var rows = await Task.Run(() =>
                {
                    var events = ao.History.Events(null, filter);
                    var today = DateTime.Now.Date;
                    // Built off the UI thread; it is not bound to anything until it is handed over below.
                    return new ObservableCollection<AOHistoryRow>(events.Select(ev => HistoryRow(ev, today, brushes)));
                });
                if (seq != _aoHistoryLoadSeq) return;       // a newer load took over
                _aoHistoryRows = rows;
                AOHistoryList.ItemsSource = rows;
                AOHistoryStatus.Text = HistoryStatusText(rows.Count, filter);
                Log.Info($"[MainWindow] LoadAlwaysOnHistory: {rows.Count} events shown ({filter})");
            }
            catch (Exception ex)
            {
                Log.Error("[MainWindow] LoadAlwaysOnHistory FAILED", ex);
                AOHistoryStatus.Text = "The history could not be loaded: " + ex.Message;
            }
            finally
            {
                if (seq == _aoHistoryLoadSeq) _aoHistoryLoading = false;
            }
            // Events that arrived during the load are in the file but not in the rows.
            if (seq == _aoHistoryLoadSeq && _aoHistoryStale) ShowAlwaysOnHistoryIfSelected();
        }

        /// <summary>A new event, on the engine's thread: dispatched to the UI thread, and inserted at the
        /// top when the tab is showing and the filter takes it.</summary>
        private void OnAlwaysOnHistoryAppended(AlwaysOnEvent ev)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Entry point on the UI thread.
                try
                {
                    if (!AlwaysOnHistoryVisible || _aoHistoryLoading)
                    {
                        _aoHistoryStale = true;
                        return;
                    }
                    if (!ev.Matches(_aoHistoryFilter)) return;
                    _aoHistoryRows.Insert(0, HistoryRow(ev, DateTime.Now.Date, AlwaysOnHistoryBrushes()));
                    AOHistoryStatus.Text = HistoryStatusText(_aoHistoryRows.Count, _aoHistoryFilter);
                }
                catch (Exception ex)
                {
                    Log.Error("[MainWindow] OnAlwaysOnHistoryAppended FAILED", ex);
                }
            }));
        }

        /// <summary>The brushes a row uses, from the app's resources - never hard-coded here.</summary>
        private (Brush Info, Brush Warning, Brush Error, Brush Dim) AlwaysOnHistoryBrushes() =>
            ((Brush)FindResource("RdText"), (Brush)FindResource("DkAmber"), (Brush)FindResource("DkRed"), (Brush)FindResource("RdDim"));

        internal static string HistoryStatusText(int count, HistoryFilter filter) => count == 0
            ? (filter == HistoryFilter.All
                ? "No events yet. Start always-on and what it does appears here, newest first."
                : $"No {filter.ToString().ToLowerInvariant()} in the last {AlwaysOnHistory.RetentionDays} days.")
            : $"{count} event{(count == 1 ? "" : "s")}, newest first - today and the last {AlwaysOnHistory.RetentionDays} days.";

        /// <summary>When a row says: the time for today's events, the date as well for older ones.</summary>
        internal static string HistoryWhen(DateTime atLocal, DateTime todayLocal) =>
            atLocal.Date == todayLocal ? atLocal.ToString("HH:mm:ss") : atLocal.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>The badge on a row: the severity when it is a warning or an error, else the kind.</summary>
        internal static string HistoryBadge(AlwaysOnEvent ev) => ev.Severity switch
        {
            HistorySeverity.Warning => "WARNING",
            HistorySeverity.Error => "ERROR",
            _ => ev.Kind.ToString().ToLowerInvariant(),
        };

        private static AOHistoryRow HistoryRow(AlwaysOnEvent ev, DateTime todayLocal, (Brush Info, Brush Warning, Brush Error, Brush Dim) b)
        {
            var text = ev.Severity switch { HistorySeverity.Warning => b.Warning, HistorySeverity.Error => b.Error, _ => b.Info };
            var badge = ev.Severity == HistorySeverity.Info ? b.Dim : text;
            return new AOHistoryRow(HistoryWhen(ev.AtLocal, todayLocal), HistoryBadge(ev), ev.Text, badge, text, ev.Detail);
        }
    }
}
