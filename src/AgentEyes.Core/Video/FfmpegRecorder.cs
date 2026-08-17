using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Drawing = System.Drawing;

namespace AgentEyes.Video
{
    /// <summary>
    /// Screen-video + microphone recorder backed by a single ffmpeg process (gdigrab + dshow -> MP4).
    /// Stop() sends 'q' to ffmpeg's stdin so the MP4 is finalized cleanly (killing it truncates the file).
    /// </summary>
    internal sealed class FfmpegRecorder : IDisposable
    {
        private readonly Process _proc;
        private readonly StringBuilder _stderr = new();
        private readonly string _logPath;
        private bool _stopped;

        public string OutputPath { get; }
        public string CommandLine { get; }

        private FfmpegRecorder(Process proc, string outputPath, string commandLine, string logPath)
        {
            _proc = proc;
            OutputPath = outputPath;
            CommandLine = commandLine;
            _logPath = logPath;
        }

        public static FfmpegRecorder Start(
            Drawing.Rectangle capture, string? dshowMicName, int fps, int crf, string outPath)
        {
            string exe = FfmpegLocator.Ffmpeg();
            // Pass the virtual-desktop bounds so an oversized social-format region is grabbed clamped
            // and padded back to its exact size instead of failing gdigrab (issue #69).
            var args = FfmpegArgs.VideoCapture(capture, dshowMicName, fps, crf, outPath, Monitors.VirtualBounds());
            string cmd = FfmpegArgs.ToCommandLine(exe, args);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            string logPath = outPath + ".ffmpeg.log";
            var rec = new FfmpegRecorder(proc, outPath, cmd, logPath);

            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) rec._stderr.AppendLine(e.Data); };
            proc.OutputDataReceived += (_, _) => { };

            if (!proc.Start())
            {
                throw new UsageException("failed to start ffmpeg.");
            }
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            // Give ffmpeg a moment to initialize the capture; surface an early crash clearly.
            Thread.Sleep(400);
            if (proc.HasExited && proc.ExitCode != 0)
            {
                string err = rec._stderr.ToString();
                File.WriteAllText(logPath, err);
                throw new UsageException(
                    $"ffmpeg exited immediately (code {proc.ExitCode}). See {logPath}. " +
                    "Likely cause: " + DiagnoseImmediateExit(err, dshowMicName));
            }

            return rec;
        }

        /// <summary>Stop recording, finalize the MP4, and return its duration (best-effort).</summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            try
            {
                _proc.StandardInput.Write("q");
                _proc.StandardInput.Flush();
            }
            catch
            {
                // stdin may already be closed; fall through to wait/kill.
            }

            if (!_proc.WaitForExit(8000))
            {
                try { _proc.Kill(true); } catch { }
                _proc.WaitForExit(3000);
            }

            File.WriteAllText(_logPath, _stderr.ToString());
        }

        /// <summary>
        /// Turn ffmpeg's stderr into an accurate, actionable cause for an immediate exit. The
        /// region-out-of-bounds case is NOT the mic - the region-clamp/pad path (issue #69) normally
        /// prevents it, but if gdigrab still reports it we say so; the mic is only blamed when a mic
        /// was actually requested. Pure string inspection - safe to unit test.
        /// </summary>
        internal static string DiagnoseImmediateExit(string stderr, string? dshowMicName)
        {
            stderr ??= "";
            if (stderr.Contains("extends outside window area", StringComparison.OrdinalIgnoreCase))
                return "the capture region extends past the desktop bounds.";
            if (!string.IsNullOrWhiteSpace(dshowMicName))
                return $"the microphone \"{dshowMicName}\" may not match a DirectShow device.";
            return "see the log for the ffmpeg error.";
        }

        public bool HasExited => _proc.HasExited;

        public void Dispose()
        {
            if (!_stopped)
            {
                try { Stop(); } catch { }
            }
            _proc.Dispose();
        }
    }
}
