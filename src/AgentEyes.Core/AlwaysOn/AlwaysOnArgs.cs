using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes.AlwaysOn
{
    /// <summary>The PCM format of the system sound fed to ffmpeg through the named pipe.</summary>
    internal sealed record PipeAudioFormat(int SampleRate, int Channels);

    /// <summary>
    /// Pure ffmpeg argument builders for always-on recording (issue #66). Side-effect free, so the
    /// exact command line is pinned by tests without launching anything.
    /// </summary>
    internal static class AlwaysOnArgs
    {
        /// <summary>The hardware encoders tried, best first, then the software one every build has.
        /// Measured on 2026-09-23 (Intel i7-13700 / UHD 770, over RDP, static screen, 60s each):
        /// gdigrab 10fps + h264_qsv 0.30 cores / 210 MB / 36 MB per hour, against today's normal
        /// recording shape (gdigrab 30fps + libx264 veryfast) at 0.96 cores / 845 MB / 90 MB per hour.</summary>
        public static readonly string[] EncoderPreference = { "h264_qsv", "h264_nvenc", "h264_amf", "libx264" };

        /// <summary>The file name pattern of a piece. ffmpeg fills it with the wall-clock time the piece
        /// was opened (-strftime 1), which is how the keeper knows when each piece started. The recorder
        /// runs ffmpeg with TZ=UTC0, so the time is UTC: a local time repeats an hour every autumn.</summary>
        public const string PiecePattern = "piece_%Y%m%d-%H%M%S.mp4";

        /// <summary>The format of <see cref="PiecePattern"/> for parsing a piece's name back.</summary>
        public const string PieceStampFormat = "yyyyMMdd-HHmmss";

        /// <summary>The keyframe interval of the always-on capture, in seconds (issue #79).</summary>
        public const int DefaultKeyframeSeconds = 2;

        /// <summary>
        /// One continuous capture written as fixed-length pieces.
        ///
        /// THE FORCED KEYFRAME IS THE POINT. The segment muxer can only cut at a keyframe; the June
        /// spike (docs/24-7-m0-spike-findings.md) left keyframes to the encoder and got pieces of 106,
        /// 26 and 54 seconds. Forcing one every <paramref name="keyframeSeconds"/> of output time -
        /// a whole number of which make one piece - makes every piece cut land where it was asked for.
        ///
        /// Issue #79 moved the keyframe from once a piece (60 s) to every 2 s: a clip now starts
        /// keep-before (10 s) ahead of the speech, and a lossless stream-copy trim can only start on a
        /// keyframe. The keyframe is asked for TWICE, because -force_key_frames alone is not honoured on
        /// time by every encoder: on the owner's laptop (h264_qsv, 10 fps) 60 s pieces came out 43-77 s
        /// long, so the hardware encoder placed the forced keyframe tens of seconds off. So besides
        /// -force_key_frames the encoder's own GOP is set to the same interval (<see cref="GopArgs"/>:
        /// -g fps*keyframeSeconds, plus the encoder's flag that makes a forced keyframe a real IDR frame
        /// where it has one). The trim (<see cref="ClipTrim"/>) does not assume the keyframes are on the
        /// grid - it seeks on the input, which lands on the keyframe actually at or before the asked time.
        /// </summary>
        /// <param name="capture">Region in virtual-desktop device pixels (a whole monitor = its bounds).</param>
        /// <param name="desktop">The virtual-desktop bounds, to clamp and pad an oversized region.</param>
        /// <param name="dshowMic">The microphone's DirectShow name, or null when the mic is not recorded.</param>
        /// <param name="systemPipe">The named pipe carrying the system sound, or null when it is not recorded.</param>
        /// <param name="systemFormat">The PCM format on that pipe (32-bit float, interleaved).</param>
        public static List<string> Capture(
            Drawing.Rectangle capture, Drawing.Rectangle? desktop, int fps, string encoder,
            string? dshowMic, double micGain, string? systemPipe, PipeAudioFormat? systemFormat, double systemGain,
            int pieceSeconds, string pieceDir, int keyframeSeconds = DefaultKeyframeSeconds)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "frame rate must be positive");
            if (pieceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(pieceSeconds), "piece length must be positive");
            if (keyframeSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(keyframeSeconds), "the keyframe interval must be positive");
            if (pieceSeconds % keyframeSeconds != 0)
                throw new ArgumentException($"a piece ({pieceSeconds}s) must be a whole number of keyframe intervals ({keyframeSeconds}s), "
                    + "or the pieces would not be cut where they are asked to be", nameof(keyframeSeconds));
            if (string.IsNullOrWhiteSpace(encoder)) throw new ArgumentException("an encoder is required", nameof(encoder));
            if (string.IsNullOrWhiteSpace(pieceDir)) throw new ArgumentException("a piece directory is required", nameof(pieceDir));
            if (systemPipe != null && systemFormat == null)
                throw new ArgumentException("a system-sound pipe needs its PCM format", nameof(systemFormat));

            var (grab, padFilter) = FfmpegArgs.GrabAndPad(capture, desktop);
            var inv = CultureInfo.InvariantCulture;

            var a = new List<string>
            {
                "-y",
                "-f", "gdigrab",
                "-thread_queue_size", "1024",
                "-framerate", fps.ToString(inv),
                "-offset_x", grab.X.ToString(inv),
                "-offset_y", grab.Y.ToString(inv),
                "-video_size", $"{grab.Width}x{grab.Height}",
                "-i", "desktop",
            };

            int input = 1;
            int micInput = -1, sysInput = -1;
            if (!string.IsNullOrWhiteSpace(dshowMic))
            {
                a.AddRange(new[]
                {
                    "-f", "dshow",
                    "-thread_queue_size", "1024",
                    "-audio_buffer_size", "80",
                    // The June spike saw "real-time buffer too full" on the mic; an all-day capture
                    // gets the headroom a short one never needed.
                    "-rtbufsize", "64M",
                    "-i", $"audio={dshowMic}",
                });
                micInput = input++;
            }
            if (systemPipe != null)
            {
                a.AddRange(new[]
                {
                    "-f", "f32le",
                    "-ar", systemFormat!.SampleRate.ToString(inv),
                    "-ac", systemFormat.Channels.ToString(inv),
                    "-thread_queue_size", "1024",
                    "-i", systemPipe,
                });
                sysInput = input++;
            }

            // Video: the pad (only for an oversized region) and the pixel format the encoder takes.
            string pixFmt = encoder == "libx264" ? "yuv420p" : "nv12";
            string vf = padFilter == null ? $"format={pixFmt}" : $"{padFilter},format={pixFmt}";
            a.AddRange(new[] { "-map", "0:v", "-vf", vf });

            // Audio: one input is mapped through a volume filter; two are mixed. amix's normalize=0
            // keeps each source at the gain the preset chose instead of halving both.
            if (micInput >= 0 && sysInput >= 0)
            {
                string fc = $"[{micInput}:a]volume={Gain(micGain)}[m];[{sysInput}:a]volume={Gain(systemGain)}[s];"
                            + "[m][s]amix=inputs=2:duration=longest:normalize=0[aout]";
                a.AddRange(new[] { "-filter_complex", fc, "-map", "[aout]" });
            }
            else if (micInput >= 0)
            {
                a.AddRange(new[] { "-map", $"{micInput}:a", "-af", $"volume={Gain(micGain)}" });
            }
            else if (sysInput >= 0)
            {
                a.AddRange(new[] { "-map", $"{sysInput}:a", "-af", $"volume={Gain(systemGain)}" });
            }

            a.AddRange(EncoderArgs(encoder));
            a.AddRange(GopArgs(encoder, fps, keyframeSeconds));
            a.AddRange(new[] { "-force_key_frames", $"expr:gte(t,n_forced*{keyframeSeconds.ToString(inv)})" });
            if (micInput >= 0 || sysInput >= 0) a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });

            a.AddRange(new[]
            {
                "-f", "segment",
                "-segment_time", pieceSeconds.ToString(inv),
                "-reset_timestamps", "1",
                "-segment_format", "mp4",
                "-strftime", "1",
                Path.Combine(pieceDir, PiecePattern),
            });
            return a;
        }

        /// <summary>The encoder's own options: a constant-quality mode comparable to the normal
        /// recorder's CRF 23, so a kept clip looks like a recording and not like a surveillance tape.</summary>
        public static IReadOnlyList<string> EncoderArgs(string encoder) => encoder switch
        {
            "h264_qsv" => new[] { "-c:v", "h264_qsv", "-global_quality", "25" },
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "25" },
            "h264_amf" => new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "25", "-qp_p", "27" },
            "libx264" => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23" },
            _ => throw new ArgumentException($"unknown always-on encoder '{encoder}'", nameof(encoder)),
        };

        /// <summary>
        /// The encoder's own keyframe interval (issue #79, review fix pass), so a keyframe every
        /// <paramref name="keyframeSeconds"/> is not left to -force_key_frames alone. The live finding on
        /// the owner's laptop: h264_qsv at 10 fps with only -force_key_frames every 60 s cut pieces of
        /// 43-77 s, so the forced keyframes were not where they were asked for. Per encoder:
        ///
        ///  - every encoder: -g fps*keyframeSeconds (20 frames at 10 fps / 2 s) - the GOP length the
        ///    encoder closes on its own, independent of the forced-keyframe expression;
        ///  - h264_qsv: -forced_idr 1 - QSV writes a forced keyframe as a real IDR frame only with this
        ///    flag; without it the "keyframe" can be a plain I-frame a demuxer cannot start a copy at;
        ///  - h264_nvenc: -forced-idr 1 - the same flag, spelled with a hyphen in NVENC;
        ///  - h264_amf: no such flag exists; -g is all AMF takes (its IDR period follows the GOP);
        ///  - libx264: -keyint_min equal to -g so the GOP cannot be shortened, and -sc_threshold 0 so a
        ///    scene cut does not insert a keyframe that shifts the grid.
        ///
        /// These are output options for the video stream and follow -c:v. <see cref="EncoderProbe"/>
        /// deliberately leaves them out: a 10-frame probe answers "does the encoder work", nothing more.
        /// </summary>
        public static IReadOnlyList<string> GopArgs(string encoder, int fps, int keyframeSeconds)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "frame rate must be positive");
            if (keyframeSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(keyframeSeconds), "the keyframe interval must be positive");
            string gop = (fps * keyframeSeconds).ToString(CultureInfo.InvariantCulture);
            return encoder switch
            {
                "h264_qsv" => new[] { "-g", gop, "-forced_idr", "1" },
                "h264_nvenc" => new[] { "-g", gop, "-forced-idr", "1" },
                "h264_amf" => new[] { "-g", gop },
                "libx264" => new[] { "-g", gop, "-keyint_min", gop, "-sc_threshold", "0" },
                _ => throw new ArgumentException($"unknown always-on encoder '{encoder}'", nameof(encoder)),
            };
        }

        /// <summary>A one-second test encode that answers "does this encoder work on this machine".
        /// Fed through the same nv12 conversion the real capture uses. It carries no GOP options
        /// (<see cref="GopArgs"/>): ten frames say nothing about keyframe placement.</summary>
        public static List<string> EncoderProbe(string encoder)
        {
            var a = new List<string>
            {
                "-v", "error",
                "-f", "lavfi", "-i", "color=c=black:s=640x360:r=10",
                "-frames:v", "10",
                "-vf", encoder == "libx264" ? "format=yuv420p" : "format=nv12",
            };
            a.AddRange(EncoderArgs(encoder));
            a.AddRange(new[] { "-f", "null", "-" });
            return a;
        }

        /// <summary>
        /// Join a clip's pieces into one file WITHOUT RE-ENCODING (the concat demuxer with stream
        /// copy): seconds of disk I/O, and the kept video is byte-for-byte what was captured.
        /// </summary>
        public static List<string> Join(string listFile, string outPath) => new()
        {
            "-y",
            "-f", "concat",
            "-safe", "0",
            "-i", listFile,
            "-c", "copy",
            "-movflags", "+faststart",
            outPath,
        };

        /// <summary>
        /// Cut one piece of a clip WITHOUT RE-ENCODING (issue #79): a stream copy that starts at
        /// <paramref name="inSeconds"/> (a keyframe time - the input seek lands on the keyframe at or
        /// before it) and/or ends at <paramref name="outSeconds"/>, both measured from the piece's start.
        /// Null keeps the piece from its start / to its end. The copied timestamps are shifted to start
        /// at zero so the cut piece joins the rest with the concat demuxer like any other.
        /// </summary>
        public static List<string> Trim(string input, string output, double? inSeconds, double? outSeconds)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("an input piece is required", nameof(input));
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("an output path is required", nameof(output));
            if (inSeconds is null && outSeconds is null) throw new ArgumentException("a trim needs a start or an end");
            if (inSeconds < 0) throw new ArgumentOutOfRangeException(nameof(inSeconds), "a trim cannot start before the piece");
            if (outSeconds is double o && o <= (inSeconds ?? 0))
                throw new ArgumentOutOfRangeException(nameof(outSeconds), "a trim must end after it starts");

            var inv = CultureInfo.InvariantCulture;
            var a = new List<string> { "-y" };
            if (inSeconds is double i) a.AddRange(new[] { "-ss", i.ToString("0.###", inv) });
            a.AddRange(new[] { "-i", input });
            if (outSeconds is double end) a.AddRange(new[] { "-t", (end - (inSeconds ?? 0)).ToString("0.###", inv) });
            a.AddRange(new[] { "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", output });
            return a;
        }

        /// <summary>The concat demuxer's list file for the given pieces, in order.</summary>
        public static string JoinList(IEnumerable<string> piecePaths)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in piecePaths)
            {
                // The concat list is single-quoted; a quote inside a path is written '\''.
                string safe = p.Replace('\\', '/').Replace("'", "'\\''");
                sb.Append("file '").Append(safe).Append("'\n");
            }
            return sb.ToString();
        }

        /// <summary>Parse the start time out of a piece's file name (UTC, as ffmpeg wrote it), or null
        /// when the name is not a piece.</summary>
        public static DateTime? PieceStartUtc(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            if (!name.StartsWith("piece_", StringComparison.Ordinal)) return null;
            string stamp = name.Substring("piece_".Length);
            return DateTime.TryParseExact(stamp, PieceStampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t
                : null;
        }

        private static string Gain(double g) => g.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
