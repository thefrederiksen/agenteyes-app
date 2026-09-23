using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AgentEyes.Video;

namespace AgentEyes.Housekeeping
{
    /// <summary>
    /// The outcome of one preserved-audio transcode, with enough detail to answer "what happened to my
    /// original" from the record rather than from a log (issue #56).
    /// </summary>
    internal sealed class TranscodeResult
    {
        /// <summary>True only when the output exists AND verification passed.</summary>
        public bool Verified { get; init; }

        /// <summary>True when the decoded streams matched byte for byte.</summary>
        public bool BitExact { get; init; }

        public long SourceBytes { get; init; }
        public long OutputBytes { get; init; }

        /// <summary>The decoded-stream hash of the source, for the record.</summary>
        public string? SourceHash { get; init; }

        /// <summary>The decoded-stream hash of the output.</summary>
        public string? OutputHash { get; init; }

        public double SourceDurationSeconds { get; init; }
        public double OutputDurationSeconds { get; init; }

        /// <summary>Why it failed, when it did. Null on success.</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Re-encode a preserved audio original into a smaller container, and PROVE the result before the
    /// source is allowed to be deleted (issues #55, #56).
    ///
    /// The proof is not optional and it is not an inference from the encoder's exit code. Both files are
    /// decoded to the same raw sample format and hashed; a bit-exact transcode produces identical
    /// hashes, and nothing else counts as bit-exact. This exists because the obvious choice was wrong:
    /// FLAC looks like the right answer for lossless audio, and for THIS audio - 48 kHz stereo 32-bit
    /// float PCM - ffmpeg's FLAC encoder silently writes 24 bits and the hashes do not match. Only a
    /// test that compares the samples catches that. An assumption would have shipped it.
    ///
    /// There is deliberately no fallback. A transcode that cannot be verified leaves BOTH files on disk
    /// and reports why; it does not quietly accept a smaller file it could not vouch for.
    /// </summary>
    internal static class PreservedAudioTranscode
    {
        /// <summary>
        /// How far the output's duration may differ from the source's and still be the same audio.
        /// Container framing rounds; a real truncation is orders of magnitude larger than this.
        /// </summary>
        public const double DurationToleranceSeconds = 0.5;

        /// <summary>
        /// The sample format both files are decoded to before hashing. 32-bit float is the widest of
        /// the formats in play, so decoding to it cannot itself hide a difference: a 24-bit output and
        /// a 32-bit float source decode to different f32 streams, which is exactly the loss that has
        /// to be visible here.
        /// </summary>
        public const string ComparisonFormat = "pcm_f32le";

        /// <summary>
        /// Encode <paramref name="sourcePath"/> to <paramref name="outputPath"/> and verify it.
        ///
        /// An output that already exists is NOT re-encoded - a previous pass may have been interrupted
        /// between writing it and removing the source - but it IS re-verified, because an interrupted
        /// write is precisely the case where a half-written file is sitting there.
        /// </summary>
        public static TranscodeResult Run(
            string sourcePath, string outputPath, string codec, int compressionLevel, bool requireBitExact)
        {
            long sourceBytes = new FileInfo(sourcePath).Length;

            if (!File.Exists(outputPath))
            {
                try
                {
                    Ffmpeg.Run(new[]
                    {
                        "-v", "error",
                        "-y",
                        "-i", sourcePath,
                        "-c:a", codec,
                        "-compression_level", compressionLevel.ToString(CultureInfo.InvariantCulture),
                        outputPath,
                    }, $"housekeeping transcode {Path.GetFileName(sourcePath)}");
                }
                catch (Exception ex)
                {
                    TryDeletePartial(outputPath);
                    return new TranscodeResult
                    {
                        Verified = false,
                        SourceBytes = sourceBytes,
                        Error = $"encode failed: {ex.Message}",
                    };
                }
            }

            if (!File.Exists(outputPath))
            {
                return new TranscodeResult
                {
                    Verified = false,
                    SourceBytes = sourceBytes,
                    Error = "the encoder reported success but wrote no output file",
                };
            }

            return Verify(sourcePath, outputPath, requireBitExact);
        }

        /// <summary>
        /// Compare two audio files as SAMPLES: same duration within tolerance, and the same decoded
        /// stream hash. Separated from <see cref="Run"/> so the verification can be tested against a
        /// deliberately mismatched pair without encoding anything.
        /// </summary>
        public static TranscodeResult Verify(string sourcePath, string outputPath, bool requireBitExact)
        {
            long sourceBytes = new FileInfo(sourcePath).Length;
            long outputBytes = new FileInfo(outputPath).Length;

            double sourceDuration = MediaProbe.DurationSeconds(sourcePath);
            double outputDuration = MediaProbe.DurationSeconds(outputPath);

            if (sourceDuration <= 0)
            {
                return new TranscodeResult
                {
                    Verified = false, SourceBytes = sourceBytes, OutputBytes = outputBytes,
                    SourceDurationSeconds = sourceDuration, OutputDurationSeconds = outputDuration,
                    Error = "could not read the source's duration, so nothing can be compared against it",
                };
            }

            if (Math.Abs(sourceDuration - outputDuration) > DurationToleranceSeconds)
            {
                return new TranscodeResult
                {
                    Verified = false, SourceBytes = sourceBytes, OutputBytes = outputBytes,
                    SourceDurationSeconds = sourceDuration, OutputDurationSeconds = outputDuration,
                    Error = $"duration differs by {Math.Abs(sourceDuration - outputDuration):F3}s, "
                          + $"over the {DurationToleranceSeconds:F1}s tolerance",
                };
            }

            string? sourceHash = DecodedHash(sourcePath);
            string? outputHash = DecodedHash(outputPath);

            if (sourceHash is null || outputHash is null)
            {
                return new TranscodeResult
                {
                    Verified = false, SourceBytes = sourceBytes, OutputBytes = outputBytes,
                    SourceDurationSeconds = sourceDuration, OutputDurationSeconds = outputDuration,
                    SourceHash = sourceHash, OutputHash = outputHash,
                    Error = "could not hash the decoded audio of "
                          + (sourceHash is null ? "the source" : "the output"),
                };
            }

            bool bitExact = string.Equals(sourceHash, outputHash, StringComparison.OrdinalIgnoreCase);

            if (requireBitExact && !bitExact)
            {
                return new TranscodeResult
                {
                    Verified = false, BitExact = false,
                    SourceBytes = sourceBytes, OutputBytes = outputBytes,
                    SourceDurationSeconds = sourceDuration, OutputDurationSeconds = outputDuration,
                    SourceHash = sourceHash, OutputHash = outputHash,
                    Error = "the decoded audio does not match the source, and this transcode was "
                          + "required to be bit-exact",
                };
            }

            return new TranscodeResult
            {
                Verified = true, BitExact = bitExact,
                SourceBytes = sourceBytes, OutputBytes = outputBytes,
                SourceDurationSeconds = sourceDuration, OutputDurationSeconds = outputDuration,
                SourceHash = sourceHash, OutputHash = outputHash,
            };
        }

        /// <summary>
        /// The hash of a file's audio AFTER decoding, in a fixed sample format. This is the whole
        /// instrument: it compares what the two files MEAN, not how they are stored.
        /// </summary>
        private static string? DecodedHash(string path)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
            {
                "-v", "error",
                "-i", path,
                "-map", "0:a",
                "-c:a", ComparisonFormat,
                "-f", "md5", "-",
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                Log.Warn($"[PreservedAudioTranscode] hashing {Path.GetFileName(path)} failed "
                       + $"(exit {p.ExitCode}): {error.Trim()}");
                return null;
            }

            const string prefix = "MD5=";
            int at = output.IndexOf(prefix, StringComparison.Ordinal);
            return at < 0 ? null : output.Substring(at + prefix.Length).Trim();
        }

        /// <summary>
        /// Remove a half-written output. A failed encode that leaves its partial file behind would be
        /// picked up by the next pass as "already transcoded" and re-verified rather than re-encoded,
        /// so the partial has to go with the failure that produced it.
        /// </summary>
        private static void TryDeletePartial(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"[PreservedAudioTranscode] could not remove the partial output {path}: {ex.Message}");
            }
        }
    }
}
