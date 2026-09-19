using System;

namespace AgentEyes.Housekeeping
{
    /// <summary>
    /// What the Housekeeper is allowed to do (issues #55, #56).
    ///
    /// AgentEyes is an always-on recorder that has never reduced its own footprint. Measured on
    /// 2026-09-18, one machine's recordings root held 52.9 GiB across 43 recordings, of which the
    /// durable readable record - transcripts, walkthrough pages, manifests, thumbnails - was 11 MB.
    /// Everything else was raw capture, preserved backups, and extracted frames. These are the knobs
    /// on the pass that fixes that, carried as a value object so the PLANNER stays pure and testable:
    /// nothing here reads a file or starts a process.
    /// </summary>
    internal sealed class HousekeepingSettings
    {
        /// <summary>
        /// Master switch. False means the pass does nothing at all - it does not even look.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// When true the pass PLANS and REPORTS and changes nothing on disk.
        ///
        /// This is the default, and it stays the default for the first release that ships it. A
        /// destructive pass that nobody has watched once is a pass nobody can vouch for, and the
        /// owner pays for a wrong answer in recordings that cannot be recovered. The report is the
        /// thing that earns the switch being turned off.
        /// </summary>
        public bool ReportOnly { get; set; } = true;

        /// <summary>
        /// How old a recording must be, in days, before the preserved originals named in its
        /// manifest's <c>OriginalFiles</c> are deleted (Tier 3).
        ///
        /// ASSUMPTION, flagged in #56 and not yet ruled on by the owner: the useful life of a
        /// cleaned-versus-raw comparison (the reason issue #83 preserves them at all) is about a
        /// month. It is a setting precisely because it is an assumption.
        /// </summary>
        public int PreservedOriginalDays { get; set; } = 30;

        /// <summary>
        /// How much of an ffmpeg log to keep when truncating it. The tail is where a failure is, and
        /// one real recording's <c>raw.mp4.ffmpeg.log</c> was 3.6 MB of per-frame progress lines.
        /// </summary>
        public long LogTailBytes { get; set; } = 40 * 1024;

        /// <summary>
        /// Whether the preserved audio transcode (Tier 1) must be BIT-EXACT.
        ///
        /// True - the default - transcodes to WavPack, which round-trips 48 kHz stereo 32-bit float
        /// PCM bit for bit: 40.2% of the WAV size, verified by comparing the decoded stream hash of
        /// the source and the output.
        ///
        /// False transcodes to FLAC instead, which is considerably smaller - 20.8% - and is NOT
        /// bit-exact for this audio, and the difference is measured rather than theoretical. ffmpeg's
        /// FLAC encoder cannot hold 32-bit float; asked to encode this track it writes
        /// <c>bits_per_raw_sample=24</c>, discarding the low 8 bits. In a real two-minute slice of
        /// system audio 22.7% of samples had a non-zero low byte, so bits that exist are lost. It is
        /// far below anything audible, and for the cleaned-versus-raw comparison these files exist for
        /// (issue #83) 24 bits is beyond sufficient - but it is still a reduction in the fidelity of
        /// an archive the owner did not ask to be reduced, so it is opt-in and never the default.
        ///
        /// Either way the transcode is RECORDED in the manifest, including which of these two it was,
        /// so what happened to a preserved original is answerable from the record rather than inferred.
        /// </summary>
        public bool PreservedAudioMustBeBitExact { get; set; } = true;

        /// <summary>
        /// The encoder and container for a bit-exact transcode. WavPack compression level 1 is the
        /// measured sweet spot: 40.2% of the WAV at 109 times realtime, where level 6 reaches only
        /// 39.7% at 3 times realtime. At level 1 the whole 27.78 GiB of preserved audio on the
        /// machine this was measured on converts in about eleven minutes of background work.
        /// </summary>
        public string BitExactCodec { get; set; } = "wavpack";
        public int BitExactCompressionLevel { get; set; } = 1;
        public string BitExactExtension { get; set; } = ".wv";

        /// <summary>The encoder and container when <see cref="PreservedAudioMustBeBitExact"/> is
        /// false. FLAC compression level 8; see that property for what is given up.</summary>
        public string SmallerCodec { get; set; } = "flac";
        public int SmallerCompressionLevel { get; set; } = 8;
        public string SmallerExtension { get; set; } = ".flac";

        /// <summary>The extension the preserved audio will be transcoded into, given the above.</summary>
        public string PreservedAudioExtension =>
            PreservedAudioMustBeBitExact ? BitExactExtension : SmallerExtension;

        /// <summary>The encoder name to pass to ffmpeg, given the above.</summary>
        public string PreservedAudioCodec =>
            PreservedAudioMustBeBitExact ? BitExactCodec : SmallerCodec;

        /// <summary>The compression level to pass to ffmpeg, given the above.</summary>
        public int PreservedAudioCompressionLevel =>
            PreservedAudioMustBeBitExact ? BitExactCompressionLevel : SmallerCompressionLevel;

        /// <summary>
        /// False skips Tier 1 entirely - the preserved audio is left as it is.
        ///
        /// It exists because the two halves of this pass cost wildly different things. The deletes and
        /// truncations are instant and free; re-encoding 28 GB of preserved audio and verifying every
        /// byte of it against its source is half an hour of processor and disk. A machine that wants
        /// the free space now, or that should not spend the processor at all, turns this off and still
        /// gets every other tier.
        /// </summary>
        public bool TranscodePreservedAudio { get; set; } = true;

        /// <summary>
        /// Convert extracted walkthrough frames from PNG to JPEG once a recording is this many days
        /// old (Tier 2). Zero or less turns it off.
        ///
        /// Seven days rather than immediately because a fresh recording is the one being looked at, and
        /// a frame re-encoded under the reader is a needless surprise. The frames are the single largest
        /// item in a mature library - 11.52 GB in 18,823 files on the machine this was measured on,
        /// against 5.25 GB of actual video.
        /// </summary>
        public int FrameDays { get; set; } = 7;

        /// <summary>
        /// JPEG quality for a converted frame, 1 to 100. 88 keeps screen text crisp; the frames measured
        /// 22% to 50% of their PNG size across real recordings at a comparable setting.
        ///
        /// It is the System.Drawing scale, where higher is better - NOT ffmpeg's inverted -q:v scale,
        /// where 2 is near-transparent and 31 unusable. The encoder moved in-process for speed and the
        /// number changed meaning with it, so a 4 here would now mean "destroy the frame".
        /// </summary>
        public int FrameJpegQuality { get; set; } = 88;

        /// <summary>
        /// False leaves the frames alone.
        ///
        /// Worth knowing before turning it on: a frame is a DERIVED artifact. Package extracts frames
        /// from recording.mp4 at the interval the manifest records, so as long as the recording exists a
        /// lost or degraded frame can be made again. That is what makes re-encoding them acceptable at
        /// all - the same reasoning would not justify touching the video.
        /// </summary>
        public bool ConvertFramesToJpeg { get; set; } = true;

        /// <summary>
        /// How old a video recording must be, in days, before its composed video expires and the
        /// recording decays to its source of truth: transcript, walkthrough text, manifest, thumbnail
        /// (issue #59). Zero or less turns it off.
        ///
        /// The owner's ruling, 2026-09-19: after about a month the recording's value is its
        /// transcript, and the video - the largest file in the library - is the least re-read. Thirty
        /// days is the default the owner stated, not an assumption this time. It is ONE-WAY: the
        /// video is what the extracted frames regenerate from, so expiring it ends regeneration too,
        /// which is the point.
        /// </summary>
        public int KeepVideoDays { get; set; } = 30;

        /// <summary>
        /// A footprint ceiling in bytes, or 0 for none. Past the ceiling the OLDEST unpinned
        /// recordings are treated as having reached <see cref="PreservedOriginalDays"/> early, because
        /// an age rule on its own does not protect a disk from one very long capture.
        /// </summary>
        public long CeilingBytes { get; set; }
    }
}
