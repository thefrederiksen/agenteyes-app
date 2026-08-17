using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentEyes
{
    /// <summary>
    /// Finds recordings whose transcription never completed, so they can be repaired automatically
    /// (issue #132).
    ///
    /// Transcription fires exactly once, when a recording finishes. Before this, a failure at that
    /// moment - no account, a revoked key, no credits, a network blip - left the recording without
    /// a transcript FOREVER. Four recordings were lost that way on 2026-08-04 and 2026-08-10 and
    /// had to be rescued by hand.
    ///
    /// Detection is deliberately STATELESS: a directory with playable media but no transcript.json
    /// needs transcription. That is exactly how the four broken recordings were found, and it means
    /// there is no separate bookkeeping file to drift out of sync with the truth on disk.
    /// </summary>
    internal static class TranscriptionBacklog
    {
        /// <summary>
        /// How many automatic attempts one recording gets at TRANSCRIPTION before the pass leaves it
        /// alone. A judgement call (issue #132): high enough to ride out transient failures, low
        /// enough that a permanently un-transcribable file cannot burn credits on every launch
        /// forever.
        ///
        /// The cost argument that sets it (issue #148): transcription uploads the WHOLE recording -
        /// 70 to 85 MB of 16 kHz WAV for a 40-minute meeting, split into 38 to 45 parts. One attempt
        /// is expensive, so three is the ceiling. This is deliberately NOT the titling ceiling; see
        /// <see cref="MaxTitleAttempts"/>.
        /// </summary>
        public const int MaxTranscribeAttempts = 3;

        /// <summary>
        /// How many automatic attempts one recording gets at TITLING inside one
        /// <see cref="TitleAttemptCooldown"/> window (issue #148).
        ///
        /// The cost argument that sets it: titling sends a few thousand tokens of transcript text and
        /// asks for one short JSON object back - 7,052 prompt + 1,898 completion tokens for a
        /// 44-minute meeting on 2026-08-11. That is orders of magnitude cheaper than one transcription
        /// attempt, and it is the operation whose provider is flaky: on 2026-08-11 the chat endpoint
        /// answered 5 of 14 calls, and two recordings burned all three of the old shared attempts
        /// inside that one busy window and were stranded under their preset name forever.
        ///
        /// Ten is high enough to ride out that outage (the repair pass runs every
        /// <see cref="RepairSchedule.Interval"/>, so ten attempts span more than two hours of
        /// provider trouble) and still cheap - ten titling calls cost far less than one transcription
        /// attempt. It is a ceiling, not a licence: past it the recording waits out
        /// <see cref="TitleAttemptCooldown"/>, so the loop stays bounded.
        /// </summary>
        public const int MaxTitleAttempts = 10;

        /// <summary>
        /// How long a recording must go without a titling attempt before its title budget is fresh
        /// again (issue #148).
        ///
        /// This is the re-eligibility mechanism: a recording that exhausts
        /// <see cref="MaxTitleAttempts"/> is not condemned - after a full day of quiet the next
        /// attempt starts a new window at 1. It was chosen over an explicit user-run re-title action
        /// because AgentEyes is an always-on recorder whose owner never sees the attempt counter; a
        /// recovery that needs a human to know a command exists is a recovery that does not happen.
        ///
        /// A day is the bound on the cost: the worst case for a permanently un-titleable recording is
        /// <see cref="MaxTitleAttempts"/> cheap calls per day, not four an hour forever, and a
        /// provider outage measured in hours is fully absorbed inside a single window.
        /// </summary>
        public static readonly TimeSpan TitleAttemptCooldown = TimeSpan.FromHours(24);

        /// <summary>
        /// True when <paramref name="dir"/> is a recording that still needs transcription: it has
        /// media, it has no transcript, and it has not already exhausted its attempts.
        /// </summary>
        public static bool NeedsTranscription(string dir)
        {
            if (!Directory.Exists(dir)) return false;

            // A screenshot-only folder (mode "shot") has no media and must never be picked up.
            bool hasMedia = File.Exists(Path.Combine(dir, "recording.mp4"))
                         || File.Exists(Path.Combine(dir, "audio.wav"));
            if (!hasMedia) return false;

            if (File.Exists(Path.Combine(dir, "transcript.json"))) return false;

            return AttemptsSoFar(dir) < MaxTranscribeAttempts;
        }

        /// <summary>
        /// True when <paramref name="dir"/> has a transcript but no title (issue #138).
        ///
        /// Titling is deliberately non-fatal - a naming failure must never cost the transcript - but
        /// before this nothing ever retried it, so one stalled request left a recording showing its
        /// generic preset name forever. Detection is stateless like <see cref="NeedsTranscription"/>:
        /// a transcript on disk plus an empty Title is the whole condition.
        /// </summary>
        public static bool NeedsTitle(string dir) => NeedsTitle(dir, DateTime.UtcNow);

        /// <summary>
        /// <see cref="NeedsTitle(string)"/> against an explicit clock. The clock is a parameter so
        /// the cooling-off window (issue #148) is testable without sleeping for a day.
        /// </summary>
        public static bool NeedsTitle(string dir, DateTime nowUtc)
        {
            if (!Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, "transcript.json"))) return false;   // nothing to title from

            var manifest = TryLoad(dir);
            if (manifest is null) return false;   // no manifest: nowhere to record a title
            if (!string.IsNullOrWhiteSpace(manifest.Title)) return false;
            if (!HasTitleableContent(dir)) return false;

            return IsTitleEligible(manifest.TitleAttempts, manifest.LastTitleAttemptUtc, nowUtc);
        }

        /// <summary>
        /// The title-budget rule (issue #148), separated from the files on disk so it can be reasoned
        /// about and tested directly: a recording may be titled while it has attempts left in the
        /// current window, or once that window has cooled off.
        ///
        /// A manifest written before this issue carries no <paramref name="lastAttemptUtc"/> stamp -
        /// the two recordings stranded on 2026-08-11 are exactly that shape. There is no date to
        /// measure their window from, and the attempts are known to be old, so they are treated as
        /// cooled off. That is not an unbounded retry: the very next attempt stamps the manifest and
        /// starts a real window.
        /// </summary>
        public static bool IsTitleEligible(int attempts, DateTime? lastAttemptUtc, DateTime nowUtc)
        {
            if (attempts < MaxTitleAttempts) return true;
            return HasCooledOff(lastAttemptUtc, nowUtc);
        }

        /// <summary>
        /// The attempt number the next titling try takes: 1 when the previous window has cooled off
        /// (a fresh budget), otherwise one more than the last (issue #148).
        /// </summary>
        public static int NextTitleAttempt(int attempts, DateTime? lastAttemptUtc, DateTime nowUtc)
        {
            if (attempts <= 0) return 1;
            return HasCooledOff(lastAttemptUtc, nowUtc) ? 1 : attempts + 1;
        }

        /// <summary>True when a full <see cref="TitleAttemptCooldown"/> has passed since the last
        /// titling attempt, or no attempt was ever stamped.</summary>
        private static bool HasCooledOff(DateTime? lastAttemptUtc, DateTime nowUtc)
        {
            if (lastAttemptUtc is null) return true;
            return nowUtc - lastAttemptUtc.Value >= TitleAttemptCooldown;
        }

        /// <summary>
        /// The least transcript worth naming. A few seconds of silence still produces a transcript -
        /// Whisper emits things like "..." or a stray hallucinated word - and asking a model to name
        /// that spends a credit to get nonsense back. The first run of the title pass titled an
        /// 11-character transcript of literal dots as "Missing Transcript", which is worse than
        /// leaving it unnamed. 40 characters is roughly one short spoken sentence.
        /// </summary>
        public const int MinTitleableChars = 40;

        /// <summary>True when the recording's transcript has enough real speech to name.</summary>
        public static bool HasTitleableContent(string dir)
        {
            string txt = Path.Combine(dir, "transcript.txt");
            if (!File.Exists(txt)) return false;

            // Strip the "[mm:ss] " stamps so the threshold measures SPEECH, not formatting.
            string spoken = Regex.Replace(File.ReadAllText(txt), @"\[\d{1,2}:\d{2}(:\d{2})?\]", "").Trim();
            return spoken.Length >= MinTitleableChars;
        }

        /// <summary>Every recording under <paramref name="root"/> that has a transcript but no title.</summary>
        public static IReadOnlyList<string> FindMissingTitles(string root)
            => FindMissingTitles(root, DateTime.UtcNow);

        /// <summary>
        /// <see cref="FindMissingTitles(string)"/> against an explicit clock (issue #148), so the
        /// cooling-off window can be exercised without waiting a day.
        /// </summary>
        public static IReadOnlyList<string> FindMissingTitles(string root, DateTime nowUtc)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();

            var pending = Directory.GetDirectories(root)
                .Where(d => NeedsTitle(d, nowUtc))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();

            Log.Info($"[TranscriptionBacklog] FindMissingTitles: root={root}, pending={pending.Count}");
            return pending;
        }

        /// <summary>
        /// Records one more titling attempt, bounding retries to <see cref="MaxTitleAttempts"/> per
        /// <see cref="TitleAttemptCooldown"/> window.
        /// </summary>
        public static void NoteTitleAttempt(string dir) => NoteTitleAttempt(dir, DateTime.UtcNow);

        /// <summary>
        /// <see cref="NoteTitleAttempt(string)"/> against an explicit clock, so the window reset is
        /// testable without waiting a day.
        /// </summary>
        public static void NoteTitleAttempt(string dir, DateTime nowUtc)
        {
            var manifest = TryLoad(dir);
            if (manifest is null)
            {
                Log.Info($"[TranscriptionBacklog] NoteTitleAttempt: no manifest at {dir}; nothing to record");
                return;
            }

            manifest = ManifestStore.Update(dir, m =>
            {
                m.TitleAttempts = NextTitleAttempt(m.TitleAttempts, m.LastTitleAttemptUtc, nowUtc);
                m.LastTitleAttemptUtc = nowUtc;
            });
            Log.Info($"[TranscriptionBacklog] NoteTitleAttempt: {Path.GetFileName(dir)} attempt "
                + $"{manifest.TitleAttempts}/{MaxTitleAttempts} in this {TitleAttemptCooldown.TotalHours:0}h window");
        }

        /// <summary>
        /// Every recording under <paramref name="root"/> that needs transcription, oldest first so
        /// the backlog clears in the order it was created.
        /// </summary>
        public static IReadOnlyList<string> FindPending(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();

            var pending = Directory.GetDirectories(root)
                .Where(NeedsTranscription)
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();

            Log.Info($"[TranscriptionBacklog] FindPending: root={root}, pending={pending.Count}");
            return pending;
        }

        /// <summary>How many automatic attempts this recording has already had.</summary>
        public static int AttemptsSoFar(string dir)
        {
            var manifest = TryLoad(dir);
            return manifest?.TranscribeAttempts ?? 0;
        }

        /// <summary>
        /// Records one more attempt against the recording, so a repeatedly-failing file eventually
        /// drops out of the automatic pass. Counted BEFORE the attempt runs - a crash mid-attempt
        /// must still consume a try, or a file that hard-crashes the pipeline would retry forever.
        /// </summary>
        public static void NoteAttempt(string dir)
        {
            var manifest = TryLoad(dir);
            if (manifest is null)
            {
                Log.Info($"[TranscriptionBacklog] NoteAttempt: no manifest at {dir}; nothing to record");
                return;
            }

            manifest = ManifestStore.Update(dir, m => m.TranscribeAttempts++);
            Log.Info($"[TranscriptionBacklog] NoteAttempt: {Path.GetFileName(dir)} attempt {manifest.TranscribeAttempts}/{MaxTranscribeAttempts}");
        }

        private static Manifest? TryLoad(string dir)
        {
            // A directory with no readable manifest is still transcribable - it just cannot carry
            // an attempt count, so it is treated as never attempted.
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) return null;
            return Manifest.Load(dir);
        }
    }
}
