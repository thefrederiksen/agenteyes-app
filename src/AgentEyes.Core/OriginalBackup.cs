using System.Collections.Generic;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// Issue #83: an always-on recorder must never destroy the only clean copy of a capture. Instead
    /// of DELETING the untouched pre-processing capture after the clean-voice mux, we RENAME it to a
    /// stable ".original" backup next to the cleaned output. This lets the cleaned vs. raw take be
    /// A/B compared later and makes any over-removal recoverable. The pure name mapping (<see
    /// cref="Plan"/>) is unit-tested without ffmpeg; <see cref="Preserve"/> performs the renames.
    /// </summary>
    internal static class OriginalBackup
    {
        /// <summary>One raw-&gt;original rename. Both are file names relative to the recording dir.</summary>
        public readonly record struct Rename(string From, string To);

        /// <summary>
        /// The preserve plan for a finished recording: which raw capture file (the finalize / CLI
        /// path would otherwise DELETE it) becomes which ".original" backup, per the issue #83 table.
        /// Empty when nothing is preserved (e.g. mic-only audio, whose audio.wav is already the
        /// untouched final file).
        /// </summary>
        public static IReadOnlyList<Rename> Plan(string mode, AudioSourceKind src)
        {
            var list = new List<Rename>();
            if (mode == "audio")
            {
                if (src == AudioSourceKind.System)
                {
                    list.Add(new Rename("sys_native.wav", "audio.original.wav"));
                }
                else if (src == AudioSourceKind.Mixed)
                {
                    list.Add(new Rename("mic.wav", "mic.original.wav"));
                    list.Add(new Rename("sys_native.wav", "system.original.wav"));
                }
            }
            else if (mode == "video")
            {
                if (src == AudioSourceKind.Mic)
                {
                    list.Add(new Rename("raw.mp4", "recording.original.mp4"));
                }
                else if (src == AudioSourceKind.Mixed)
                {
                    list.Add(new Rename("raw.mp4", "recording.original.mp4"));
                    list.Add(new Rename("sys_native.wav", "system.original.wav"));
                }
                else if (src == AudioSourceKind.System)
                {
                    list.Add(new Rename("raw.mp4", "recording.original.mp4"));
                    list.Add(new Rename("sys_native.wav", "system.original.wav"));
                }
            }
            return list;
        }

        /// <summary>
        /// Preserve the originals for a finished recording: rename each present raw capture file to
        /// its ".original" backup (overwrite-safe move, NOT a copy, so there is no extra IO). Returns
        /// the relative backup names actually created (for the manifest). Never deletes a capture.
        /// </summary>
        public static List<string> Preserve(string dir, string mode, AudioSourceKind src)
        {
            var preserved = new List<string>();
            foreach (var r in Plan(mode, src))
            {
                string from = Path.Combine(dir, r.From);
                if (!File.Exists(from)) continue;
                string to = Path.Combine(dir, r.To);
                File.Move(from, to, overwrite: true);
                preserved.Add(r.To);
                Log.Info($"[OriginalBackup] preserved {r.From} -> {r.To}");
            }
            return preserved;
        }
    }
}
