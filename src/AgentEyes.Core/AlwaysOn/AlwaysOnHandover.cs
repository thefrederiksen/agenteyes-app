using System;
using System.IO;
using System.Text.Json;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// What a PLANNED stop of always-on leaves for the next start (issue #86): the clip that was open
    /// and the sound log behind the pieces still to be decided.
    ///
    /// Without it, a restart of the app - an update, or Quit and start - looked exactly like a crash to
    /// the next start: the open clip's holding folder was joined whole and closed, the last piece
    /// (finished by the stop, never decided) was deleted because "its sound log went with that run",
    /// and speech after the restart began a new clip with no lead-in (the tester's 2026-09-24 report).
    /// Once an update restarts the app on purpose, every update would do that.
    ///
    /// The file lives in the work folder beside the pieces and is CONSUMED by the start that reads it:
    /// loaded, restored, deleted - so a later start never replays a stale handover. A file that reads
    /// but is not a handover is set aside as .bad and said (the start then recovers as from a crash);
    /// a file that cannot be read at all fails the start with the file and the fix (issue #86 review, N7).
    /// </summary>
    internal sealed class AlwaysOnHandover
    {
        /// <summary>The format, for a future reader that has to tell an old file apart.</summary>
        public int Version { get; set; } = 1;

        /// <summary>When the planned stop happened.</summary>
        public DateTime StoppedUtc { get; set; }

        /// <summary>Why: "app exit - it comes back at the next start", an update's restart.</summary>
        public string Why { get; set; } = "";

        /// <summary>The clip that was open at the stop, or null when none was.</summary>
        public HandoverClip? Open { get; set; }

        /// <summary>The last sound of the newest closed clip - sound up to here never starts a new one.</summary>
        public DateTime? ClosedSoundUtc { get; set; }

        /// <summary>The next clip number to hand out, so the restart does not reuse a number.</summary>
        public int NextClip { get; set; } = 1;

        /// <summary>The seconds the sound log knew that anything still undecided could reach.</summary>
        public SoundLogState Sound { get; set; } = new(Array.Empty<long>(), Array.Empty<long>(), null);

        /// <summary>
        /// Read the handover at <paramref name="path"/>: null when there is none. There is no "could not
        /// read it, so none" (issue #86 review, N7): a file that is there but cannot be READ - locked by
        /// another process, no access - throws <see cref="InvalidOperationException"/> naming the file and
        /// the fix, because a start that shrugged it off would then trip over the same file a line later,
        /// or leave it to be replayed by the next start; a file that reads but does not hold a handover
        /// throws <see cref="InvalidDataException"/>, which the caller sets aside deliberately (.bad) and
        /// says so.
        /// </summary>
        public static AlwaysOnHandover? Load(string path)
        {
            if (!File.Exists(path)) return null;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"[AlwaysOnHandover] Load: {path} cannot be read", ex);
                throw new InvalidOperationException(
                    $"always-on cannot start: the handover left by the last planned stop, {path}, cannot be read ({ex.Message}). "
                    + "Close whatever holds the file open - or delete it to recover the pieces as from a crash - and switch always-on on again.", ex);
            }
            try
            {
                var h = JsonSerializer.Deserialize<AlwaysOnHandover>(text);
                if (h == null) throw new JsonException("the file holds no handover");
                h.Sound ??= new SoundLogState(Array.Empty<long>(), Array.Empty<long>(), null);
                Log.Info($"[AlwaysOnHandover] Load: {path} -> stopped {h.StoppedUtc.ToLocalTime():HH:mm:ss} ({h.Why}), "
                         + $"open clip {(h.Open == null ? "none" : Path.GetFileName(h.Open.Dir))}, {h.Sound.SoundSeconds.Length}s of sound");
                return h;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path} does not hold a handover ({ex.Message})", ex);
            }
        }

        /// <summary>Write the handover atomically: a full file appears under its name or none does.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>The clip a planned stop left open (issue #86): <see cref="OpenClip"/> plus where its
    /// kept pieces are and when it began.</summary>
    internal sealed class HandoverClip
    {
        public int Id { get; set; }
        public DateTime FirstSoundUtc { get; set; }
        public DateTime LastSoundUtc { get; set; }
        public DateTime LastPieceEndUtc { get; set; }
        /// <summary>The holding folder with its kept pieces.</summary>
        public string Dir { get; set; } = "";
        /// <summary>The start of its first kept piece.</summary>
        public DateTime StartUtc { get; set; }
    }
}
