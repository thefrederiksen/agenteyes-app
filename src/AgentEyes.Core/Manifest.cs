using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEyes
{
    /// <summary>
    /// On-disk record of a capture session: monitor chosen, region, mic, durations, files.
    /// Pattern mirrored from cc-director phone/CcRecorder/Recording/LocalManifest.cs
    /// (commit c2f0c90). See vendor/PROVENANCE.md.
    /// </summary>
    internal sealed class Manifest
    {
        public string Tool { get; set; } = "AgentEyes";
        public string Mode { get; set; } = "";          // "shot" | "audio" | "video"

        /// <summary>
        /// Issue #100: true when this recording was IMPORTED from an existing external video file
        /// (e.g. a Teams meeting recording) rather than captured live by AgentEyes. This is the
        /// distinguishable flag that marks an imported entry; <see cref="Mode"/> stays "video"
        /// because the artifact IS a video recording. Backward compatible: an OLD manifest.json
        /// written before this field existed has no "Imported" property, so deserialization leaves
        /// it <c>false</c> (a native recording).
        /// </summary>
        public bool Imported { get; set; }

        /// <summary>
        /// Issue #100: for an imported recording (<see cref="Imported"/> = true), the original
        /// source video's file name (e.g. "TeamsMeeting.mp4"), preserved for provenance. Null for
        /// native recordings.
        /// </summary>
        public string? ImportedSource { get; set; }

        public string Label { get; set; } = "";
        /// <summary>User-given name (Rename in the recent list); null = show the derived title.</summary>
        public string? DisplayName { get; set; }
        /// <summary>Short title auto-generated from the transcript (issue #8). DisplayName still wins.</summary>
        public string? Title { get; set; }
        /// <summary>One-line description auto-generated from the transcript (issue #8).</summary>
        public string? Description { get; set; }
        /// <summary>What the AI calls for this recording (title/description today) cost. Null until
        /// a recording is processed with an AI provider configured. Measured from API token usage
        /// when available, otherwise estimated - see <see cref="Ai.AiCostInfo.IsEstimate"/>.</summary>
        public Ai.AiCostInfo? AiCost { get; set; }
        public string CreatedUtc { get; set; } = "";

        public int MonitorIndex { get; set; }
        public string MonitorName { get; set; } = "";
        public int[]? Region { get; set; }              // [x, y, w, h] device px, or null for full monitor

        public string? Microphone { get; set; }
        public double DurationSeconds { get; set; }

        public string? VideoFile { get; set; }
        public string? AudioFile { get; set; }

        /// <summary>
        /// Issue #28: the separately-recorded webcam track ("camera.mp4"), or null when the recording
        /// had no camera. It is a SECOND, independent video file in the same directory - never
        /// composited into <see cref="VideoFile"/> - so an editor can still choose the layout
        /// afterwards. It carries no audio track by decision; all audio stays on the screen recording.
        ///
        /// Backward compatible: a manifest written before this field existed has no "CameraFile"
        /// property and deserializes to null, which reads correctly as "no camera track". Null fields
        /// are not written out (see <see cref="JsonOptions"/>), so a camera-less recording's
        /// manifest.json is byte-identical in shape to what it was before this feature.
        /// </summary>
        public string? CameraFile { get; set; }

        /// <summary>
        /// Issue #47: true once the camera has been rendered INTO <see cref="VideoFile"/>, with the
        /// screen-only cut kept beside it as "recording.screen.mp4".
        ///
        /// It exists so "has this been composed" is a fact on the record rather than something
        /// inferred from which files happen to be present, and so re-running the compose is a
        /// deliberate act rather than an accident. Absent (null) on every manifest written before
        /// this feature, which reads correctly as "not composed".
        /// </summary>
        public bool? ComposedCamera { get; set; }

        /// <summary>
        /// Issue #28: how far the camera capture started AFTER the screen capture, in seconds -
        /// negative when the camera started first, which is the normal case (the camera is opened
        /// before the screen so that a camera which cannot be opened fails the start before any
        /// bytes are written).
        ///
        /// An alignment HINT of tens of milliseconds measured in-process between the two ffmpeg
        /// process starts (assumption A5) - NOT frame-accurate genlock. Precise sync is the editor's
        /// job. Null when there is no camera track.
        /// </summary>
        public double? CameraStartOffsetSeconds { get; set; }

        /// <summary>
        /// Issue #28: seconds of camera footage the camera ffmpeg reported writing. This is the
        /// file's own account of itself, not wall time, so it stays honest for a camera that was lost
        /// mid-recording. Null when there is no camera track.
        /// </summary>
        public double? CameraCapturedSeconds { get; set; }

        /// <summary>
        /// Issue #28 (spec amendment 2026-08-28): HOW the camera process ended, as observed - one of
        /// "clean-quit", "force-killed", "exited-early", "abandoned". Null when there is no camera
        /// track, and null when no stop ever watched the process end, which is itself the honest
        /// answer rather than a guessed one.
        /// </summary>
        public string? CameraStopKind { get; set; }

        /// <summary>
        /// Issue #28 (spec amendment 2026-08-28): true only when ffmpeg's stderr was read to END OF
        /// STREAM at the stop, i.e. everything the camera ffmpeg ever said was seen. False means the
        /// evidence behind the other camera fields is INCOMPLETE - which is why
        /// <see cref="CameraComplete"/> can never be "yes" while this is false. Null when there is
        /// no camera track.
        /// </summary>
        public bool? CameraStderrComplete { get; set; }

        /// <summary>
        /// Issue #28 (spec amendment 2026-08-28, assumption A7): whether camera.mp4 is a complete
        /// take - "yes", "no", or "unknown". Null when there is no camera track.
        ///
        /// IT IS A STRING WITH THREE VALUES, AND THAT IS THE POINT. It replaces the
        /// <c>CameraTruncated</c> boolean, which could only say complete or truncated - so every
        /// case the recorder had not anticipated came out as COMPLETE, a claim made from an absence
        /// of evidence. Three rounds of this feature shipped exactly that: a camera that emitted one
        /// progress tick and then stalled for a 30-second session, and a file that was force-killed
        /// mid-write, were both written here as clean complete takes.
        ///
        ///  - "yes" needs the whole presence: a clean quit, stderr read to end of stream, and output
        ///    still advancing when the stop was requested.
        ///  - "no" is what is KNOWN short or broken: exited early, force-killed, or never a frame.
        ///  - "unknown" is everything else, and is the CORRECT answer whenever the evidence does not
        ///    reach. A consumer must treat it as "do not know", never coerce it to false - which is
        ///    why this is a string enum and not a nullable bool.
        /// </summary>
        public string? CameraComplete { get; set; }

        /// <summary>
        /// Issue #33 (assumption C4): which corner of the frame the person was watching the camera in
        /// while this was recorded - "bottom-right", "bottom-left", "top-left" or "top-right".
        ///
        /// AN EDITING HINT, NOT A COMPOSITION. Nothing is composited: the recording is still the two
        /// separate files issue #28 produces, and this changes neither of them. It records the
        /// FRAMING THAT WAS ACTUALLY WANTED at the moment of recording, so the later edit (or a future
        /// auto-compose) starts from the person's own choice instead of a guess made months later.
        ///
        /// Null - and, being null, absent from manifest.json entirely - whenever no overlay framing
        /// was chosen: no preview, a preview showing only the screen or only the camera, or a
        /// recording made before this field existed. A camera-less or preview-less recording's
        /// manifest is therefore identical in shape to what it was before this feature (AC11).
        /// </summary>
        public string? PreviewOverlayCorner { get; set; }

        /// <summary>
        /// Issue #36: the overlay SHAPE the camera was framed in - "circle" (the default) or
        /// "rectangle". Null, and therefore absent from manifest.json entirely, whenever no overlay
        /// framing was chosen, exactly like <see cref="PreviewOverlayCorner"/> (AC10).
        /// </summary>
        public string? PreviewOverlayShape { get; set; }

        /// <summary>
        /// Issue #36: WHERE THE CIRCLE SAT IN THE CAMERA FRAME - centre and diameter as fractions of
        /// the frame, so it survives the camera or its resolution changing (assumption E2). Null when
        /// the overlay was a rectangle, and null when no overlay was framed.
        ///
        /// IT IS EDIT METADATA, NOT A CROP (assumption E1). <c>camera.mp4</c> in this same recording
        /// is the FULL rectangular frame at its normal resolution - identical in dimensions to a
        /// recording made with the rectangle overlay and to one made with the preview off (AC5).
        /// Nothing was cut, so a later edit can reproduce this framing AND move it, which is the
        /// entire reason the circle is recorded here instead of being burned into the file.
        /// </summary>
        public Preview.CameraOverlayCircle? PreviewOverlayCircle { get; set; }

        /// <summary>
        /// Issue #36, assumption E5: how large the inset was ON THE PREVIEW, as a fraction of the
        /// preview's width. A different thing from the circle's diameter, which says how much of the
        /// camera frame was inside it. Null when no overlay was framed.
        /// </summary>
        public double? PreviewOverlayInset { get; set; }

        public string? Transcript { get; set; }
        public string? Walkthrough { get; set; }
        public string? FfmpegCommand { get; set; }

        /// <summary>
        /// Per-language WebVTT transcript artifacts (issue #98): language code -> file name,
        /// e.g. { "en" -&gt; "transcript.en.vtt" }. This is the first-class, subtitle-ready
        /// transcript surface; the legacy <see cref="Transcript"/> (transcript.json) and the flat
        /// transcript.txt are retained unchanged alongside it.
        ///
        /// Backward compatible: an OLD manifest.json written before this field existed has no
        /// "Transcripts" property, so deserialization leaves this map empty (it never throws), and
        /// such a recording's transcript is still identified by <see cref="Transcript"/>.
        /// </summary>
        public Dictionary<string, string> Transcripts { get; set; } = new();

        /// <summary>
        /// How many times the automatic backfill pass has tried to transcribe this recording
        /// (issue #132). Bounds the retry so a permanently un-transcribable file cannot burn
        /// credits on every launch. An older manifest.json without this property deserializes to
        /// 0, i.e. never attempted, which is the correct reading.
        /// </summary>
        public int TranscribeAttempts { get; set; }

        /// <summary>
        /// How many times the automatic pass has tried to TITLE this recording (issue #138).
        /// Separate from <see cref="TranscribeAttempts"/>: a recording can transcribe first time and
        /// still fail to title, and each deserves its own budget.
        /// </summary>
        public int TitleAttempts { get; set; }

        /// <summary>
        /// UTC time of the last automatic titling attempt (issue #148). It is what makes
        /// <see cref="TitleAttempts"/> a budget per cooling-off window rather than a life sentence:
        /// once <see cref="TranscriptionBacklog.TitleAttemptCooldown"/> has passed since this stamp,
        /// the next attempt starts a fresh window. Null on an older manifest.json (and on a recording
        /// never attempted), which reads as "no window in progress".
        /// </summary>
        public DateTime? LastTitleAttemptUtc { get; set; }

        /// <summary>
        /// How many times the automatic repair pass has tried to generate this recording's Library
        /// thumbnail (issue #142). Its own budget, separate from the two above: a recording can
        /// transcribe and title first time and still lose its poster frame to one failed ffmpeg run,
        /// and a file ffmpeg can never read must not be retried on every periodic tick forever.
        /// </summary>
        public int ThumbAttempts { get; set; }

        public List<ShotEntry> Shots { get; set; } = new();
        public List<string> Files { get; set; } = new();

        /// <summary>
        /// Issue #83: untouched pre-processing capture files kept alongside the cleaned output (a
        /// ".original" infix - e.g. recording.original.mp4, mic.original.wav). These are
        /// present-but-secondary: discoverable in the folder/manifest but never the primary playable
        /// (the canonical recording.mp4 / audio.wav stays the playable). Each name is also added to
        /// <see cref="Files"/>.
        /// </summary>
        public List<string> OriginalFiles { get; set; } = new();

        /// <summary>
        /// Set by <see cref="RecordingService.Stop"/> when the audio mux / system-downmix is
        /// deferred to the background packaging pass (issue #77). Non-null = the final mixed
        /// file does not exist yet; the raw capture files (raw mp4, sys/mic WAVs) are still on
        /// disk and are the durable artifact. <see cref="RecordingService.FinalizePending"/>
        /// performs the mux, then clears this back to null. Null = nothing deferred.
        /// </summary>
        public PendingMuxInfo? PendingMux { get; set; }

        /// <summary>
        /// Issue #152: the durable outcome of each post-recording stage (mux / thumbnail / package /
        /// plugins) - stage name -> what happened last time it was attempted. Written by
        /// <see cref="PostRecordingState"/> as the sequence runs, so an interrupted or partly failed
        /// recording says on disk which stages already finished instead of that knowledge dying with
        /// the process.
        ///
        /// It is a JOURNAL, not the authority. What a recovery pass must still do is decided from the
        /// artifacts on disk (<see cref="PostRecordingPlan"/>) - a pending mux, a missing thumbnail, a
        /// missing transcript - so that no record of what happened, however damaged, can convince the
        /// app that finished work is outstanding or that outstanding work is finished. The journal
        /// carries the diagnosis (the error text) and the mux attempt ceiling. (Manifest writes ARE
        /// atomic since issue #155 - <see cref="ManifestStore"/> - so a torn file is no longer the
        /// reason for this split; the artifacts remain the authority because they are the thing the
        /// work actually produces.)
        ///
        /// Backward compatible: a manifest written before this field existed has no "PostProcessing"
        /// property, so it deserializes to an empty map - read as "no stage has reported yet", which
        /// is the correct reading for a recording whose artifacts already say what is done.
        /// </summary>
        public Dictionary<string, PostStageRecord> PostProcessing { get; set; } = new();

        /// <summary>What one post-recording stage did the last time it ran (issue #152).</summary>
        public sealed class PostStageRecord
        {
            /// <summary>One of the <see cref="PostStageState"/> values.</summary>
            public string State { get; set; } = "";

            /// <summary>How many times this stage has been attempted for this recording.</summary>
            public int Attempts { get; set; }

            /// <summary>UTC time of the last attempt; null when the stage has never run.</summary>
            public DateTime? LastAttemptUtc { get; set; }

            /// <summary>The failure message when <see cref="State"/> is
            /// <see cref="PostStageState.Failed"/>; null otherwise. Truncated - it is a diagnosis in
            /// the manifest, not a log replacement.</summary>
            public string? Error { get; set; }
        }

        public sealed class ShotEntry
        {
            public double OffsetSeconds { get; set; }
            public string File { get; set; } = "";
        }

        /// <summary>
        /// The deferred audio-mux work the background pass must complete (issue #77). All paths
        /// are relative to the recording directory so the record survives being moved/relaunched.
        /// </summary>
        public sealed class PendingMuxInfo
        {
            public string Mode { get; set; } = "";      // "audio" | "video"
            public string Source { get; set; } = "";    // "mic" | "system" | "mixed"
            public string? RawVideo { get; set; }        // raw.mp4 (relative), null when none
            public string? MicWav { get; set; }          // mic.wav (relative), null when none
            public string? SysWav { get; set; }          // sys_native.wav (relative), null when none
            public string FinalFile { get; set; } = "";  // recording.mp4 / audio.wav (relative)
            public double RawDurationSeconds { get; set; } // elapsed measured at stop, no probe
            public AudioMixOptions Options { get; set; } = new();
        }

        /// <summary>
        /// Every property in manifest.json that this version does not know about (issue #155),
        /// captured on load and written back out unchanged on save.
        ///
        /// Without it, a load-then-save cycle silently DELETED any property written by a newer
        /// AgentEyes or by an external tool - a live hazard, because #142 and #148 each added a field
        /// that an older code path round-tripping the file would have erased. The bag is per-manifest
        /// and top-level: unknown properties nested inside PostProcessing / PendingMux / Shots
        /// entries are still dropped, which is called out deliberately in the issue #155 handoff note
        /// rather than silently assumed away.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; } = new();

        /// <summary>
        /// How a manifest is serialized. Internal because <see cref="ManifestStore"/> is the only
        /// thing that writes one (issue #155) - this type carries the shape, not the file.
        /// </summary>
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Read the manifest in <paramref name="directory"/>. There is deliberately no Save here:
        /// writing goes through <see cref="ManifestStore.Update"/> (read-modify-write) or
        /// <see cref="ManifestStore.Replace"/> (whole-content write), which are atomic and serialized
        /// per recording. A direct save was how the packaging pass erased a rename (issue #155).
        /// </summary>
        public static Manifest Load(string directory)
        {
            string path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
            {
                throw new UsageException($"no manifest.json in {directory}.");
            }
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions)
                   ?? throw new UsageException($"manifest.json in {directory} is empty or invalid.");
        }
    }
}
