using System;
using AgentEyes;

namespace AgentEyes.App
{
    /// <summary>
    /// The detail window's transcript decisions, extracted from the window so they are unit-testable
    /// without a WPF <c>Application</c> (issue #4). <see cref="RecordingDetailWindow"/> renders this;
    /// it decides nothing about transcript presence itself.
    ///
    /// Before this, the window set its private has-transcript flag from the LENGTH of the flat
    /// transcript.txt, so a legacy flat-text-only recording - one the transcription backlog still
    /// has queued - was presented as transcribed. The claim now comes from the canonical predicate
    /// (<see cref="TranscriptStatus"/>), while the flat text itself stays fully readable and
    /// copyable: the distinction must never remove access to content that already exists.
    /// </summary>
    internal sealed class TranscriptPresentation
    {
        /// <summary>The canonical classification (<see cref="TranscriptStatus.Classify"/>).</summary>
        public TranscriptKind Kind { get; }

        /// <summary>The one "is transcribed" claim the detail window may make: canonical
        /// completion, never flat-text length.</summary>
        public bool HasTranscript => Kind == TranscriptKind.Transcribed;

        /// <summary>The text to display: the manifest-named transcript.json when transcribed, else
        /// the legacy flat transcript.txt - the same precedence the Control API serves
        /// (<see cref="RecordingLibrary.ReadTranscript"/>). Empty when there is nothing to show.</summary>
        public string Text { get; }

        /// <summary>Copy stays available whenever there is text - a legacy flat text is content the
        /// user owns, whether or not the recording is transcribed.</summary>
        public bool CanCopy => Text.Length > 0;

        /// <summary>The quiet caption shown over a legacy flat text (null otherwise): the text is
        /// readable, but the recording must not read as transcribed.</summary>
        public string? LegacyNotice { get; }

        private TranscriptPresentation(TranscriptKind kind, string text)
        {
            Kind = kind;
            Text = text;
            LegacyNotice = kind == TranscriptKind.FlatTextOnly
                ? "Not transcribed - showing the text file saved with this recording."
                : null;
        }

        /// <summary>What the window falls back to when the recording cannot be read at all (the
        /// error is logged at the entry point): no claim, no text, no Copy.</summary>
        public static TranscriptPresentation None { get; } = new(TranscriptKind.None, "");

        /// <summary>Build the presentation for one recording directory. <paramref name="manifest"/>
        /// may be null when the manifest could not be read - a flat text is still surfaced.</summary>
        public static TranscriptPresentation For(string dir, Manifest? manifest)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("dir is required", nameof(dir));
            var kind = TranscriptStatus.Classify(dir, manifest);
            string text = "";
            if (kind != TranscriptKind.None)
                text = RecordingLibrary.ReadTranscript(dir, manifest)?.Text?.Trim() ?? "";
            Log.Info($"[TranscriptPresentation] For: dir={dir} kind={kind} chars={text.Length}");
            return new TranscriptPresentation(kind, text);
        }
    }
}
