using System;
using System.Linq;
using System.Text.Json;

namespace AgentEyes.Headless
{
    /// <summary>
    /// Reading a Control API request body (issue #102). Missing or mistyped fields take the
    /// documented default, exactly as the app's RestServer reads them, so a body that drives the
    /// Windows app drives the headless service the same way. Pure, unit-tested.
    /// </summary>
    internal static class ControlApiRequest
    {
        public static JsonElement Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return default;
            try
            {
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new UsageException("the request body is not valid JSON: " + ex.Message, ex);
            }
        }

        public static string GetStr(JsonElement b, string k, string def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;

        public static string? GetStrOrNull(JsonElement b, string k) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static int GetInt(JsonElement b, string k, int def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.TryGetInt32(out int i) ? i : def;

        public static double GetDouble(JsonElement b, string k, double def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.TryGetDouble(out double d) ? d : def;

        public static bool GetBool(JsonElement b, string k, bool def) =>
            b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;

        public static int[]? GetIntArray(JsonElement b, string k)
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            return v.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        }

        /// <summary>
        /// A POST /record/start body as a recording request. Defaults: video mode, screen 1, mic-only
        /// source (issue #124), 30 fps, the app's mic-processing defaults. There are no presets on
        /// the headless host; every field is explicit or defaulted.
        /// </summary>
        public static StartRequest ReadStart(JsonElement b)
        {
            var src = RecordingService.ParseSource(GetStr(b, "source", "mic"));
            return new StartRequest
            {
                Mode = GetStr(b, "mode", "video").ToLowerInvariant(),
                Screen = GetInt(b, "screen", 1),
                Source = src,
                Mic = GetStrOrNull(b, "mic"),
                Region = GetIntArray(b, "region"),
                Fps = GetInt(b, "fps", 30),
                Camera = GetStrOrNull(b, "camera"),
                CameraFps = GetInt(b, "cameraFps", 30),
                Options = new AudioMixOptions
                {
                    NoiseSuppression = GetBool(b, "denoise", true),
                    NoiseGate = GetBool(b, "gate", GateDefaults.For(src)),
                    VoiceLeveling = GetBool(b, "level", true),
                    MicGain = GetDouble(b, "micVol", 100) / 100.0,
                    SystemGain = GetDouble(b, "sysVol", 70) / 100.0,
                },
            };
        }
    }

    /// <summary>One POST /record/start, read.</summary>
    internal sealed class StartRequest
    {
        public string Mode { get; init; } = "video";
        public int Screen { get; init; } = 1;
        public AudioSourceKind Source { get; init; } = AudioSourceKind.Mic;
        public string? Mic { get; init; }
        public int[]? Region { get; init; }
        public int Fps { get; init; } = 30;
        public string? Camera { get; init; }
        public int CameraFps { get; init; } = 30;
        public AudioMixOptions Options { get; init; } = new();
    }
}
