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
    /// loaded, restored, deleted - so a later start never replays a stale handover. A file that cannot
    /// be read is logged and ignored (the start then recovers as from a crash); the recording never
    /// waits on it.
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

        /// <summary>Read the handover at <paramref name="path"/>: null when there is none, or when the file
        /// cannot be read (logged as a warning - the start then recovers as from a crash).</summary>
        public static AlwaysOnHandover? Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var h = JsonSerializer.Deserialize<AlwaysOnHandover>(File.ReadAllText(path));
                if (h == null) throw new JsonException("the file holds no handover");
                h.Sound ??= new SoundLogState(Array.Empty<long>(), Array.Empty<long>(), null);
                Log.Info($"[AlwaysOnHandover] Load: {path} -> stopped {h.StoppedUtc.ToLocalTime():HH:mm:ss} ({h.Why}), "
                         + $"open clip {(h.Open == null ? "none" : Path.GetFileName(h.Open.Dir))}, {h.Sound.SoundSeconds.Length}s of sound");
                return h;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"[AlwaysOnHandover] Load: {path} could not be read ({ex.Message}); the start recovers as from a crash");
                return null;
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
