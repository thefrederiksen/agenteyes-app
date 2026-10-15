using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentEyes.Platform.Mac
{
    /// <summary>One display as macOS reports it: its pixel size and whether it is the main one.</summary>
    internal readonly record struct MacScreen(int Width, int Height, bool Primary, string Name);

    /// <summary>
    /// The displays on this Mac. macOS has no DISPLAY variable and no xrandr, so there is nothing to
    /// "require" the way the Linux build requires an X display: a Mac always has a window server.
    /// What CAN be missing is the Screen Recording permission, and that is not a variable - it is a
    /// per-application grant the user makes in System Settings. AvfDevices.Read reports it when the
    /// listing comes back empty.
    ///
    /// SIZES come from system_profiler, because ffmpeg's AVFoundation listing names the displays
    /// without their geometry. The two lists are correlated BY ORDER, and a disagreement about how
    /// many displays exist is reported rather than guessed around - a wrong correlation would record
    /// the wrong monitor, which is exactly the kind of silent wrongness this build must not have.
    /// </summary>
    internal static class MacDisplay
    {
        // "1920 x 1080" - the real pixel count, which is what a capture must crop inside.
        private static readonly Regex Pixels = new(
            @"^\s*(?<w>\d+)\s*x\s*(?<h>\d+)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Parse the display array out of <c>system_profiler SPDisplaysDataType -json</c>. Pure, so
        /// every shape of it is unit-tested without running system_profiler. Displays that are
        /// offline are left out: they cannot be captured.
        /// </summary>
        internal static List<MacScreen> ParseSystemProfiler(string json)
        {
            var list = new List<MacScreen>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var gpus)
                || gpus.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var gpu in gpus.EnumerateArray())
            {
                if (!gpu.TryGetProperty("spdisplays_ndrvs", out var displays)
                    || displays.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var d in displays.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    if (Text(d, "spdisplays_online") == "spdisplays_no") continue;

                    string pixels = Text(d, "_spdisplays_pixels");
                    var m = Pixels.Match(pixels);
                    if (!m.Success) continue;

                    list.Add(new MacScreen(
                        Width: int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
                        Height: int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                        Primary: Text(d, "spdisplays_main") == "spdisplays_yes",
                        Name: FriendlyName(Text(d, "_name"))));
                }
            }
            return list;
        }

        /// <summary>
        /// system_profiler names an unnamed display with its own internal key,
        /// "spdisplays_display". That is not a name to show anybody, so it is treated as no name at
        /// all and the caller numbers the display instead.
        /// </summary>
        internal static string FriendlyName(string reported) =>
            reported == "spdisplays_display" ? "" : reported;

        private static string Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        /// <summary>The online displays, as system_profiler reports them, in its order.</summary>
        public static IReadOnlyList<MacScreen> Screens()
        {
            var r = ToolRunner.Run(
                "system_profiler", new[] { "SPDisplaysDataType", "-json" }, TimeSpan.FromSeconds(30));
            if (r.ExitCode != 0)
            {
                throw new UsageException(
                    $"system_profiler could not list the displays (exit {r.ExitCode}): {r.StdErr.Trim()}");
            }

            List<MacScreen> screens;
            try
            {
                screens = ParseSystemProfiler(r.StdOut);
            }
            catch (JsonException ex)
            {
                Log.Error("[MacDisplay] Screens FAILED: system_profiler did not return readable JSON", ex);
                throw new UsageException(
                    "system_profiler did not return readable display information: " + ex.Message, ex);
            }

            if (screens.Count == 0)
            {
                throw new UsageException(
                    "macOS reports no online display, so there is nothing to record. A Mac with no "
                    + "monitor attached and no display emulator has no screen to capture.");
            }
            Log.Info($"[MacDisplay] Screens: {screens.Count} online display(s)");
            return screens;
        }

        /// <summary>
        /// The AVFoundation input index for a one-based screen number, paired with that screen's
        /// size. The pairing is by order, and a count mismatch between the two sources is a hard
        /// error: addressing the wrong index would silently record the wrong monitor.
        /// </summary>
        public static (int AvfIndex, MacScreen Screen) RequireScreen(int oneBasedIndex)
        {
            var avf = AvfDevices.Screens();
            var sizes = Screens();

            if (avf.Count != sizes.Count)
            {
                throw new UsageException(
                    $"macOS reports {sizes.Count} display(s) but ffmpeg lists {avf.Count} capturable "
                    + "screen(s). AgentEyes will not guess which is which - run 'agenteyes screens' "
                    + "and report this, because recording the wrong monitor is worse than not "
                    + "recording.");
            }
            if (oneBasedIndex < 1 || oneBasedIndex > avf.Count)
            {
                throw new UsageException(
                    $"--screen {oneBasedIndex} out of range. There are {avf.Count} screen(s). "
                    + "Run 'agenteyes screens'.");
            }
            return (avf[oneBasedIndex - 1].Index, sizes[oneBasedIndex - 1]);
        }
    }
}
