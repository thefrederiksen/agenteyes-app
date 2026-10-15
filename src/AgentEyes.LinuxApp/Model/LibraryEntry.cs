using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AgentEyes.LinuxApp.Model
{
    /// <summary>What kind of file a library entry previews.</summary>
    internal enum MediaKind { None, Video, Audio, Image }

    /// <summary>
    /// One recording in the library, read from its folder (issue #107). Toolkit-free and immutable:
    /// the view model wraps it. The rules are the Windows app's (RecentItem.From in
    /// AgentEyes.App/MainWindow.xaml.cs) so a recording reads the same on both machines:
    ///  - badge VID / AUD / SHOT from the manifest mode,
    ///  - name priority: user rename (DisplayName) > generated Title > derived name,
    ///  - "MMM d, yyyy  h:mm tt   -   description" detail line,
    ///  - start time from manifest CreatedUtc ONLY; no start time = "Undated", sorted last.
    /// </summary>
    internal sealed record LibraryEntry
    {
        public string Id { get; init; } = "";
        public string Dir { get; init; } = "";
        public string Mode { get; init; } = "";
        public string Badge { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string DateText { get; init; } = "Undated";
        public DateTime? StartedUtc { get; init; }
        public string Duration { get; init; } = "-";
        public double DurationSeconds { get; init; }
        public string? MediaPath { get; init; }
        public MediaKind Media { get; init; }
        public string? ThumbPath { get; init; }
        public TranscriptKind Transcript { get; init; }
        public bool HasWalkthrough { get; init; }
        public string? Description { get; init; }

        /// <summary>AI spend for this recording, as the Windows card shows it: "$0.0123", "~$0.01 est", or "11887 tokens".</summary>
        public string Cost { get; init; } = "";

        /// <summary>Changes whenever anything shown for this entry changes on disk.</summary>
        public string Signature { get; init; } = "";

        public bool CanPreview => MediaPath != null;
        public bool IsShot => Mode == "shot";

        /// <summary>Build the entry for one recording folder. Throws when the manifest cannot be read;
        /// the scanner logs it and shows the folder as a bare, undated entry.</summary>
        public static LibraryEntry From(string dir)
        {
            var m = Manifest.Load(dir);
            string mic = MicName(m.Microphone);
            string badge;
            string derived;
            switch (m.Mode)
            {
                case "video":
                    badge = "VID";
                    derived = $"Monitor {m.MonitorIndex}" + (m.Region != null ? " (region)" : "");
                    break;
                case "audio":
                    badge = "AUD";
                    derived = mic.Length > 0 ? mic : "Audio";
                    mic = "";   // already the title; not repeated in the detail line
                    break;
                case "shot":
                    badge = "SHOT";
                    derived = $"Screenshot - Monitor {m.MonitorIndex}";
                    break;
                default:
                    badge = (m.Mode ?? "").ToUpperInvariant();
                    derived = Path.GetFileName(dir);
                    break;
            }
            string title = !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName!
                : !string.IsNullOrWhiteSpace(m.Title) ? m.Title!
                : derived;

            var (mediaPath, media) = MediaFor(dir, m);
            DateTime? started = StartUtc(m.CreatedUtc);
            string when = DateLabel(started?.ToLocalTime());
            string detail = !string.IsNullOrWhiteSpace(m.Description) ? $"{when}   -   {m.Description}"
                : mic.Length > 0 ? $"{when}   -   {mic}"
                : when;
            string? thumb = Thumbnails.PathFor(dir);
            var transcript = TranscriptStatus.Classify(dir, m);
            bool walkthrough = File.Exists(Path.Combine(dir, "walkthrough.html"));

            return new LibraryEntry
            {
                Id = Path.GetFileName(dir),
                Dir = dir,
                Mode = m.Mode ?? "",
                Badge = badge,
                Title = title,
                Detail = detail,
                DateText = when,
                StartedUtc = started,
                Duration = FormatDuration(m.Mode, m.DurationSeconds),
                DurationSeconds = m.DurationSeconds,
                MediaPath = mediaPath,
                Media = media,
                ThumbPath = thumb,
                Transcript = transcript,
                HasWalkthrough = walkthrough,
                Description = m.Description,
                Cost = m.AiCost == null ? "" : FormatCost(m.AiCost.CostUsd, m.AiCost.IsEstimate, m.AiCost.PromptTokens + m.AiCost.CompletionTokens),
                Signature = string.Join("|", title, detail, thumb ?? "", transcript, walkthrough, mediaPath ?? "", m.DurationSeconds,
                    m.AiCost?.PromptTokens, m.AiCost?.CompletionTokens),
            };
        }

        /// <summary>The entry shown for a folder whose manifest could not be read.</summary>
        public static LibraryEntry Unreadable(string dir) => new()
        {
            Id = Path.GetFileName(dir),
            Dir = dir,
            Title = Path.GetFileName(dir),
            Detail = "Undated   -   the manifest could not be read",
            Signature = "unreadable",
        };

        private static (string? path, MediaKind kind) MediaFor(string dir, Manifest m)
        {
            switch (m.Mode)
            {
                case "video":
                {
                    string p = Path.Combine(dir, m.VideoFile ?? "recording.mp4");
                    return File.Exists(p) ? (p, MediaKind.Video) : (null, MediaKind.None);
                }
                case "audio":
                {
                    string p = Path.Combine(dir, m.AudioFile ?? "audio.wav");
                    return File.Exists(p) ? (p, MediaKind.Audio) : (null, MediaKind.None);
                }
                case "shot":
                {
                    string shots = Path.Combine(dir, "shots");
                    string? png = Directory.Exists(shots)
                        ? Directory.GetFiles(shots, "*.png").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
                        : null;
                    return png != null ? (png, MediaKind.Image) : (null, MediaKind.None);
                }
            }
            return (null, MediaKind.None);
        }

        /// <summary>The start instant from manifest CreatedUtc, normalized to UTC; null when absent.</summary>
        internal static DateTime? StartUtc(string? createdUtc)
        {
            if (string.IsNullOrWhiteSpace(createdUtc)
                || !DateTime.TryParse(createdUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
                return null;
            return started.Kind == DateTimeKind.Local ? started.ToUniversalTime() : DateTime.SpecifyKind(started, DateTimeKind.Utc);
        }

        internal static string DateLabel(DateTime? startedLocal) =>
            startedLocal.HasValue
                ? startedLocal.Value.ToString("MMM d, yyyy  h:mm tt", CultureInfo.InvariantCulture)
                : "Undated";

        /// <summary>The Windows card's cost tag (MainWindow.FormatCost).</summary>
        internal static string FormatCost(double costUsd, bool estimate, int tokens)
        {
            if (costUsd > 0)
            {
                string n = "$" + costUsd.ToString("0.0000", CultureInfo.InvariantCulture);
                return estimate ? "~" + n + " est" : n;
            }
            return tokens > 0 ? tokens + " tokens" : "";
        }

        internal static string FormatDuration(string? mode, double seconds) =>
            mode == "shot" || seconds <= 0 ? "-" : $"{(int)seconds / 60}:{(int)seconds % 60:D2}";

        /// <summary>The microphone part of a manifest Microphone value: Linux writes
        /// "Yealink UH36 Analog Stereo + (system)", Windows "Microphone (Yealink UH36)".</summary>
        internal static string MicName(string? mic)
        {
            if (string.IsNullOrWhiteSpace(mic)) return "";
            string s = mic.Trim();
            const string sys = "+ (system)";
            if (s.EndsWith(sys, StringComparison.Ordinal)) s = s[..^sys.Length].Trim();
            if (s == "(system)") return "";
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            return open > 0 && close > open ? s[(open + 1)..close] : s;
        }
    }

    /// <summary>Newest first by recording start (UTC instant); undated last; ties on folder name.
    /// The Windows library's one ordering rule (NewestFirstComparer).</summary>
    internal sealed class NewestFirst : IComparer<LibraryEntry>
    {
        public static readonly NewestFirst Instance = new();

        public int Compare(LibraryEntry? x, LibraryEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            if (x.StartedUtc is null && y.StartedUtc is null) return TieBreak(x, y);
            if (x.StartedUtc is null) return 1;
            if (y.StartedUtc is null) return -1;
            int byStart = y.StartedUtc.Value.CompareTo(x.StartedUtc.Value);
            return byStart != 0 ? byStart : TieBreak(x, y);
        }

        private static int TieBreak(LibraryEntry x, LibraryEntry y) =>
            string.Compare(y.Dir, x.Dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the whole library from the recordings folder. Any thread; no UI types.</summary>
    internal static class LibraryScanner
    {
        public static List<LibraryEntry> Scan(string root)
        {
            var list = new List<LibraryEntry>();
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                try
                {
                    list.Add(LibraryEntry.From(dir));
                }
                catch (Exception ex)
                {
                    Log.Error($"[LibraryScanner] Scan: cannot read {dir}", ex);
                    list.Add(LibraryEntry.Unreadable(dir));
                }
            }
            list.Sort(NewestFirst.Instance);
            return list;
        }

        /// <summary>The folders still waiting for transcription (shown as "Transcribing...").</summary>
        public static HashSet<string> Pending(IEnumerable<LibraryEntry> entries)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                try
                {
                    if (e.Media is MediaKind.Video or MediaKind.Audio && TranscriptionBacklog.NeedsTranscription(e.Dir)) set.Add(e.Dir);
                }
                catch (Exception ex)
                {
                    Log.Error($"[LibraryScanner] Pending: cannot classify {e.Dir}", ex);
                }
            }
            return set;
        }
    }
}
