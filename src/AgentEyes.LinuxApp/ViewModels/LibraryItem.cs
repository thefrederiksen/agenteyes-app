using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AgentEyes.LinuxApp.Model;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AgentEyes.LinuxApp.ViewModels
{
    internal abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One library card / row (issue #107): the Windows RecentItem's view half. The entry is replaced
    /// in place when its folder changes on disk, so the card keeps its place and selection.
    /// </summary>
    internal sealed class LibraryItem : ObservableObject
    {
        // The Windows badge colours (RecentItem.From).
        private static readonly IBrush VidBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly IBrush AudBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly IBrush ShotBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0x9E, 0x2E));
        private static readonly IBrush OtherBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x71, 0x78));

        public static readonly Geometry PlayIcon = Geometry.Parse("M0,0 L10,6 L0,12 Z");
        public static readonly Geometry SpeakerIcon = Geometry.Parse("M0,4 H3 L7,0 V12 L3,8 H0 Z M9,3 C10.5,4.5 10.5,7.5 9,9");
        public static readonly Geometry EyeIcon = Geometry.Parse("M0,6 C3,0 11,0 14,6 C11,12 3,12 0,6 Z M7,3.5 A2.5,2.5 0 1 0 7,8.5 A2.5,2.5 0 1 0 7,3.5 Z");

        private LibraryEntry _entry;
        private Bitmap? _thumb;
        private string _status = "";
        private string? _loadedThumbPath;

        public LibraryItem(LibraryEntry entry) => _entry = entry;

        public LibraryEntry Entry => _entry;
        public string Id => _entry.Id;
        public string Dir => _entry.Dir;
        public string Title => _entry.Title;
        public string Detail => _entry.Detail;
        public string Duration => _entry.Duration;
        public string Badge => _entry.Badge;
        public IBrush BadgeBrush => _entry.Badge switch { "VID" => VidBrush, "AUD" => AudBrush, "SHOT" => ShotBrush, _ => OtherBrush };
        public bool CanPreview => _entry.CanPreview;
        public Geometry PreviewIcon => _entry.Media switch { MediaKind.Audio => SpeakerIcon, MediaKind.Image => EyeIcon, _ => PlayIcon };
        public string PreviewTip => _entry.Media switch { MediaKind.Audio => "Play audio", MediaKind.Image => "View screenshot", _ => "Play video" };
        public bool ShowTranscriptChip => _entry.Transcript == TranscriptKind.Transcribed;
        public bool ShowFlatTextChip => _entry.Transcript == TranscriptKind.FlatTextOnly;
        public bool ShowWalkthroughChip => _entry.HasWalkthrough;
        public bool CanBuildWalkthrough => !_entry.IsShot;
        public string Cost => _entry.Cost;
        public bool ShowCost => _entry.Cost.Length > 0;

        public Bitmap? Thumb { get => _thumb; private set { if (Set(ref _thumb, value)) Raise(nameof(ShowPlaceholder)); } }
        public bool ShowPlaceholder => _thumb == null;

        /// <summary>"Transcribing..." while the service still has work to do on it; empty otherwise.</summary>
        public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(IsProcessing)); } }
        public bool IsProcessing => _status.Length > 0;

        /// <summary>Replace the entry after the folder changed. Raises every property; reloads the thumb if it moved.</summary>
        public void Update(LibraryEntry entry)
        {
            _entry = entry;
            foreach (var p in new[] { nameof(Entry), nameof(Title), nameof(Detail), nameof(Duration), nameof(Badge), nameof(BadgeBrush),
                         nameof(CanPreview), nameof(PreviewIcon), nameof(PreviewTip), nameof(ShowTranscriptChip),
                         nameof(ShowFlatTextChip), nameof(ShowWalkthroughChip), nameof(CanBuildWalkthrough), nameof(Cost), nameof(ShowCost) })
                Raise(p);
            _ = LoadThumbAsync();
        }

        /// <summary>Decode the thumbnail off the UI thread, scaled to the card width (x2 for sharpness).</summary>
        public async Task LoadThumbAsync()
        {
            string? path = _entry.ThumbPath;
            if (path == null || path == _loadedThumbPath) return;
            _loadedThumbPath = path;
            try
            {
                var bmp = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(fs, 472);
                });
                await Dispatcher.UIThread.InvokeAsync(() => Thumb = bmp);
            }
            catch (Exception ex)
            {
                Log.Error($"[LibraryItem] LoadThumbAsync: cannot decode {path}", ex);
                _loadedThumbPath = null;
            }
        }
    }
}
