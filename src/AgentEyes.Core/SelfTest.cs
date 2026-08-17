using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AgentEyes.Audio;
using AgentEyes.Packaging;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>
    /// Headless end-to-end self-test. Injects known audio (tones + TTS speech) so the whole library
    /// can be verified without the UI and without the user. Prints PASS/FAIL with measured numbers,
    /// writes an HTML report, and returns 0 only if everything passed.
    /// </summary>
    /// <summary>One row of the self-test matrix. Exposed so the in-app Test Panel can render the table.</summary>
    internal sealed record SelfCheck(string Name, bool Pass, string Detail);

    internal static class SelfTest
    {
        public static int Run()
        {
            var (work, results) = RunChecks();

            int passed = results.Count(r => r.Pass);
            Console.WriteLine();
            Console.WriteLine($"{passed}/{results.Count} checks passed");
            Log.Info($"selftest done: {passed}/{results.Count}");

            Console.WriteLine("report: " + Path.Combine(work, "selftest-report.html"));
            return passed == results.Count ? 0 : 1;
        }

        /// <summary>
        /// Runs the full injected matrix and returns the per-check results plus the work folder.
        /// Shared by the CLI <see cref="Run"/> and the in-app Test Panel (which renders the table itself).
        /// </summary>
        public static (string Work, List<SelfCheck> Results) RunChecks()
        {
            string work = Path.Combine(Log.Dir, "..", "selftest", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            work = Path.GetFullPath(work);
            Directory.CreateDirectory(work);
            Log.Info("selftest start: " + work);
            Console.WriteLine("AgentEyes selftest -> " + work);
            Console.WriteLine();

            var results = new List<SelfCheck>();
            void Check(string name, Func<string> body)
            {
                try { string d = body(); results.Add(new(name, true, d)); Console.WriteLine($"[PASS] {name,-22} {d}"); }
                catch (Exception ex) { results.Add(new(name, false, ex.Message)); Console.WriteLine($"[FAIL] {name,-22} {ex.Message}"); Log.Error("selftest " + name, ex); }
            }

            var primary = Monitors.All().FirstOrDefault(m => m.Primary) ?? Monitors.Require(1);
            int micDevice = AudioCapture.Devices().Length > 0 ? AudioCapture.Devices()[0].Number : -1;
            string? micName = AudioCapture.Devices().Length > 0 ? AudioCapture.Devices()[0].Name : null;
            string? dshowMic = SafeFirstDshow();
            var opts = new AudioMixOptions();

            string Tone(int freq, double secs, double amp)
            {
                string p = Path.Combine(work, $"tone_{freq}_{(int)(amp * 1000)}.wav");
                Ffmpeg.Run(FfmpegArgs.GenerateTone(freq, secs, amp, p), "gen tone");
                return p;
            }

            // 1. enumerate
            Check("enumerate", () =>
            {
                var mons = Monitors.All();
                var mics = AudioCapture.Devices();
                Assert(mons.Count >= 1, "no monitors");
                Assert(mics.Length >= 1, "no microphones");
                int dshow = 0; try { dshow = FfmpegDevices.ListAudio().Count; } catch { }
                return $"{mons.Count} mon, {mics.Length} mic, {dshow} dshow";
            });

            // 2. screenshot full
            Check("shot-full", () =>
            {
                string f = Path.Combine(work, "shot_full.png");
                Screenshot.CaptureMonitor(primary, f, copyToClipboard: false);
                using var img = Drawing.Image.FromFile(f);
                Assert(img.Width == primary.Width && img.Height == primary.Height, $"dims {img.Width}x{img.Height}");
                return $"{new FileInfo(f).Length / 1024} KB {img.Width}x{img.Height}";
            });

            // 3. screenshot region
            Check("shot-region", () =>
            {
                var rect = new Drawing.Rectangle(primary.X + 40, primary.Y + 40, 240, 160);
                string f = Path.Combine(work, "shot_region.png");
                Screenshot.CaptureRect(rect, f, copyToClipboard: false);
                using var img = Drawing.Image.FromFile(f);
                Assert(img.Width == 240 && img.Height == 160, $"dims {img.Width}x{img.Height}");
                return $"{img.Width}x{img.Height}";
            });

            // 4. audio mic
            Check("audio-mic", () =>
            {
                Assert(micDevice >= 0, "no mic device");
                string wav = Path.Combine(work, "mic.wav");
                using (var a = new AudioCapture(micDevice)) { a.Start(wav); Thread.Sleep(2500); a.Stop(); }
                double dur = MediaProbe.DurationSeconds(wav);
                Assert(dur >= 1.8 && dur <= 4.0, $"duration {dur:F1}s");
                return $"{dur:F1}s 16k mono";
            });

            // 5. loopback + injected tone
            Check("loopback-inject", () =>
            {
                string tone = Tone(440, 8, 0.5);
                string native = Path.Combine(work, "lb_native.wav");
                string wav = Path.Combine(work, "lb.wav");
                using (var player = new TonePlayer())
                using (var l = new LoopbackCapture())
                {
                    player.Play(tone); Thread.Sleep(500);
                    l.Start(native); Thread.Sleep(4000); l.Stop();
                }
                Ffmpeg.Run(FfmpegArgs.ExtractWav(native, wav), "lb downmix");
                double db = MediaProbe.MeanVolumeDb(wav);
                Assert(db > -40, $"injected tone not captured ({db:F1} dB)");
                return $"{db:F1} dB";
            });

            // 6. mixed + injected tone
            Check("mixed-inject", () =>
            {
                Assert(micDevice >= 0, "no mic device");
                string tone = Tone(660, 8, 0.5);
                string micWav = Path.Combine(work, "mx_mic.wav");
                string sysNative = Path.Combine(work, "mx_sys.wav");
                string mixed = Path.Combine(work, "mixed.wav");
                using (var player = new TonePlayer())
                using (var a = new AudioCapture(micDevice))
                using (var l = new LoopbackCapture())
                {
                    player.Play(tone); Thread.Sleep(500);
                    a.Start(micWav); l.Start(sysNative); Thread.Sleep(4000); a.Stop(); l.Stop();
                }
                AudioMix.MixWavs(micWav, sysNative, mixed, opts);
                var (_, hasAudio) = MediaProbe.Streams(mixed);
                double db = MediaProbe.MeanVolumeDb(mixed);
                Assert(hasAudio, "no audio stream");
                Assert(db > -40, $"mix too quiet ({db:F1} dB)");
                return $"{db:F1} dB, audio ok";
            });

            // 7. noise gate attenuates below-threshold audio
            Check("gate", () =>
            {
                string quiet = Tone(440, 3, 0.01);     // below gate threshold (0.02)
                string gated = Path.Combine(work, "gated.wav");
                Ffmpeg.Run(new List<string> { "-y", "-i", quiet, "-af",
                    "agate=threshold=0.02:ratio=2:attack=20:release=250", gated }, "gate");
                double raw = MediaProbe.MeanVolumeDb(quiet);
                double g = MediaProbe.MeanVolumeDb(gated);
                Assert(g < raw - 1.0, $"gate did not attenuate (raw {raw:F1} -> {g:F1} dB)");
                return $"raw {raw:F1} -> gated {g:F1} dB";
            });

            // 8. video + mic
            Check("video-mic", () =>
            {
                string mp4 = Path.Combine(work, "vid_mic.mp4");
                using (var rec = FfmpegRecorder.Start(primary.Bounds, dshowMic, 30, 23, mp4)) { Thread.Sleep(4000); rec.Stop(); }
                var (v, a) = MediaProbe.Streams(mp4);
                Assert(v, "no video stream");
                Assert(a || dshowMic == null, "no audio stream");
                double dur = MediaProbe.DurationSeconds(mp4);
                Assert(dur >= 2, $"duration {dur:F1}s");
                return $"v={v} a={a} {dur:F1}s";
            });

            // 9. video + mixed (mic + injected system), with mux + cleanup
            Check("video-mixed", () =>
            {
                string tone = Tone(880, 9, 0.5);
                string raw = Path.Combine(work, "vm_raw.mp4");
                string sys = Path.Combine(work, "vm_sys.wav");
                string final = Path.Combine(work, "vid_mixed.mp4");
                using (var player = new TonePlayer())
                {
                    player.Play(tone); Thread.Sleep(500);
                    using var rec = FfmpegRecorder.Start(primary.Bounds, dshowMic, 30, 23, raw);
                    using var l = new LoopbackCapture();
                    l.Start(sys); Thread.Sleep(4000); rec.Stop(); l.Stop();
                }
                AudioMix.MuxVideoMixed(raw, sys, final, opts);
                var (v, a) = MediaProbe.Streams(final);
                double db = MediaProbe.MeanVolumeDb(final);
                Assert(v && a, "missing streams");
                Assert(db > -40, $"audio too quiet ({db:F1} dB)");
                return $"v={v} a={a} {db:F1} dB";
            });

            // 10. CLI mixed audio leaves no temp files (exercises the real cleanup path)
            Check("cli-cleanup", () =>
            {
                Assert(micName != null, "no mic");
                string dir = Path.Combine(work, "cli");
                string exe = Process.GetCurrentProcess().MainModule!.FileName;
                var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var s in new[] { "audio", "--screen", primary.Index.ToString(), "--mix", "--mic", micName!, "--seconds", "3", "--out", dir })
                    psi.ArgumentList.Add(s);
                using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(30000); }
                Assert(File.Exists(Path.Combine(dir, "audio.wav")), "no audio.wav produced");
                // Issue #83: the pre-processing captures are RENAMED to ".original" backups, not
                // deleted - so mic.wav/sys_native.wav are gone by those names, and the untouched
                // originals must now exist alongside the cleaned audio.wav.
                Assert(!File.Exists(Path.Combine(dir, "mic.wav")) && !File.Exists(Path.Combine(dir, "sys_native.wav")), "raw temp names still present");
                Assert(File.Exists(Path.Combine(dir, "mic.original.wav")), "missing mic.original.wav backup");
                Assert(File.Exists(Path.Combine(dir, "system.original.wav")), "missing system.original.wav backup");
                return "audio.wav present, originals preserved";
            });

            // 11. transcription of injected speech (the words must come back)
            string wav16 = Path.Combine(work, "speech16.wav");
            Check("transcribe", () =>
            {
                const string text = "the quick brown fox jumps over the lazy dog";
                string speech = Path.Combine(work, "speech.wav");
                SpeechGen.ToWav(text, speech);
                Ffmpeg.Run(FfmpegArgs.ExtractWav(speech, wav16), "speech downmix");
                // Transcription now runs 100% through DevThrottle (issue #87): this step
                // requires a signed-in account with credit.
                var segs = Transcriber.TranscribeWavAsync(wav16).GetAwaiter().GetResult();
                string got = string.Join(" ", segs.Select(s => s.Text)).ToLowerInvariant();
                string[] expect = { "quick", "brown", "fox", "lazy", "dog" };
                int hits = expect.Count(w => got.Contains(w));
                Assert(hits >= 3, $"only {hits}/5 words. got: '{got.Trim()}'");
                return $"{hits}/5 words: '{got.Trim()}'";
            });

            // 12. packaging -> walkthrough.html with the transcript
            Check("walkthrough", () =>
            {
                Assert(File.Exists(wav16), "speech16.wav missing (transcribe step failed)");
                string dir = Path.Combine(work, "wt");
                Directory.CreateDirectory(Path.Combine(dir, "shots"));
                File.Copy(wav16, Path.Combine(dir, "audio.wav"), true);
                ManifestStore.Replace(dir, new Manifest
                {
                    Mode = "audio",
                    Label = "selftest",
                    AudioFile = "audio.wav",
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                });
                Package.Run(dir, 5.0, null);
                string wt = Path.Combine(dir, "walkthrough.html");
                Assert(File.Exists(wt), "no walkthrough.html");
                string html = File.ReadAllText(wt).ToLowerInvariant();
                Assert(html.Contains("fox") || html.Contains("quick") || html.Contains("dog"), "transcript not in walkthrough");
                return "walkthrough.html has transcript";
            });

            WriteReport(work, results);
            return (work, results);
        }

        private static string? SafeFirstDshow()
        {
            try { return FfmpegDevices.ListAudio().FirstOrDefault(); } catch { return null; }
        }

        private static void Assert(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
        }

        private static void WriteReport(string work, List<SelfCheck> results)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>AgentEyes selftest</title>");
            sb.Append("<style>body{font-family:Georgia,serif;max-width:820px;margin:2rem auto;color:#2D3748;}");
            sb.Append("h1{color:#1A365D;border-bottom:3px solid #D69E2E;padding-bottom:.3em;}");
            sb.Append("table{width:100%;border-collapse:collapse;}td,th{border:1px solid #CBD5E0;padding:.5em .7em;text-align:left;}");
            sb.Append("th{background:#1A365D;color:#fff;}.p{color:#2F855A;font-weight:bold;}.f{color:#C53030;font-weight:bold;}</style></head><body>");
            int passed = results.Count(r => r.Pass);
            sb.Append($"<h1>AgentEyes selftest</h1><p>{passed}/{results.Count} checks passed - {DateTime.Now}</p>");
            sb.Append("<table><tr><th>Check</th><th>Result</th><th>Detail</th></tr>");
            foreach (var r in results)
            {
                string cls = r.Pass ? "p" : "f";
                string word = r.Pass ? "PASS" : "FAIL";
                sb.Append($"<tr><td>{r.Name}</td><td class=\"{cls}\">{word}</td><td>{System.Net.WebUtility.HtmlEncode(r.Detail)}</td></tr>");
            }
            sb.Append("</table></body></html>");
            try { File.WriteAllText(Path.Combine(work, "selftest-report.html"), sb.ToString()); } catch { }
        }
    }
}
