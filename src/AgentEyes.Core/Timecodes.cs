using System;

namespace AgentEyes
{
    /// <summary>Offset formatting shared by screenshots, manifests, and the walkthrough.</summary>
    internal static class Timecodes
    {
        /// <summary>e.g. 00m03s.png, 01m12s.png - sortable and offset-aligned.</summary>
        public static string FileName(TimeSpan t) => $"{(int)t.TotalMinutes:D2}m{t.Seconds:D2}s.png";

        /// <summary>e.g. 00m03s, 01m12s.</summary>
        public static string Label(TimeSpan t) => $"{(int)t.TotalMinutes:D2}m{t.Seconds:D2}s";

        /// <summary>e.g. 00:03, 01:12 - for transcript/walkthrough display.</summary>
        public static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }
}
