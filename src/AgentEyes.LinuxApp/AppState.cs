using System;
using System.Threading.Tasks;
using AgentEyes.LinuxApp.Model;
using AgentEyes.LinuxApp.Services;
using Avalonia.Threading;

namespace AgentEyes.LinuxApp
{
    /// <summary>
    /// What the window and the tray share (issue #107): the service client, the remembered capture
    /// setup, and the service status polled once a second. Start / stop go through here so the tray
    /// and the Record view can never disagree about whether a recording is running.
    /// </summary>
    internal sealed class AppState
    {
        public static AppState Current { get; } = new();

        public ControlClient Client { get; } = new();
        public AppSettings Settings { get; }

        /// <summary>The last status the service gave; null while it is not answering.</summary>
        public ServiceStatus? Status { get; private set; }

        /// <summary>Why the service is not answering, when it is not.</summary>
        public string? OfflineReason { get; private set; }

        /// <summary>A start or stop request is in flight.</summary>
        public bool Busy { get; private set; }

        public bool IsRecording => Status?.IsRecording == true;

        /// <summary>Raised on the UI thread after every poll and every start / stop.</summary>
        public event Action? Changed;

        /// <summary>Raised on the UI thread when a recording was saved: its folder.</summary>
        public event Action<string>? RecordingSaved;

        /// <summary>Raised on the UI thread when start / stop fails: the message to show.</summary>
        public event Action<string>? Failed;

        private bool _polling;

        private AppState()
        {
            AppSettings s;
            try { s = AppSettings.Load(); }
            catch (Exception ex)
            {
                // Entry point (app start): a damaged settings file must not stop the app opening.
                Log.Error("[AppState] settings could not be read - starting from the defaults", ex);
                s = new AppSettings();
            }
            Settings = s;
        }

        public void StartPolling()
        {
            Log.Info("[AppState] StartPolling");
            DispatcherTimer.Run(() => { _ = PollAsync(); return true; }, TimeSpan.FromSeconds(1));
            _ = PollAsync();
        }

        public async Task PollAsync()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                var s = await Client.GetStatusAsync();
                if (Status == null || OfflineReason != null) Log.Info($"[AppState] PollAsync: service answering, state={s.State}");
                Status = s;
                OfflineReason = null;
            }
            catch (ControlApiException ex)
            {
                if (OfflineReason == null) Log.Warn($"[AppState] PollAsync: service offline - {ex.Message}");
                Status = null;
                OfflineReason = ex.Message;
            }
            finally
            {
                _polling = false;
            }
            Changed?.Invoke();
        }

        public async Task StartRecordingAsync()
        {
            if (Busy || IsRecording) return;
            string source = AppSettings.SourceFor(Settings.Mic, Settings.SystemSound);
            Log.Info($"[AppState] StartRecordingAsync: screen={Settings.Screen} source={source} mic={Settings.Mic ?? "(none)"}");
            Busy = true;
            Changed?.Invoke();
            try
            {
                await Client.StartAsync(Settings.Screen, source, string.IsNullOrEmpty(Settings.Mic) ? null : Settings.Mic);
                Log.Info("[AppState] StartRecordingAsync: recording");
            }
            catch (ControlApiException ex)
            {
                Log.Error("[AppState] StartRecordingAsync FAILED", ex);
                Failed?.Invoke("Could not start recording: " + ex.Message);
            }
            finally
            {
                Busy = false;
            }
            await PollAsync();
        }

        public async Task StopRecordingAsync()
        {
            if (Busy || !IsRecording) return;
            Log.Info("[AppState] StopRecordingAsync");
            Busy = true;
            Changed?.Invoke();
            try
            {
                var (dir, seconds) = await Client.StopAsync();
                Log.Info($"[AppState] StopRecordingAsync: saved {dir} ({seconds:0.0}s)");
                RecordingSaved?.Invoke(dir);
            }
            catch (ControlApiException ex)
            {
                Log.Error("[AppState] StopRecordingAsync FAILED", ex);
                Failed?.Invoke("Could not stop the recording: " + ex.Message);
            }
            finally
            {
                Busy = false;
            }
            await PollAsync();
        }
    }
}
