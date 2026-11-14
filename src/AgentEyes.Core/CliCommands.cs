using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentEyes
{
    /// <summary>One "--name" option a command accepts. <see cref="ValueName"/> null means a boolean flag.</summary>
    internal sealed record CliOption(string Name, string? ValueName, string Description)
    {
        public bool IsBoolean => ValueName == null;
    }

    /// <summary>
    /// One CLI command (issue #122). <see cref="Path"/> is the words that select it ("record start").
    /// <see cref="Synopsis"/> is its usage text, one or more lines each starting "  agenteyes".
    /// <see cref="Options"/> is the ONLY list of flags the command accepts: the dispatcher refuses any
    /// other, the help prints exactly these, and tests prove the synopsis names exactly these and that
    /// the handler reads exactly these. <see cref="Run"/> is the handler; it is reached only after the
    /// arguments are valid and no help was asked for.
    /// </summary>
    internal sealed record CliCommand(
        string Path,
        string Synopsis,
        IReadOnlyList<CliOption> Options,
        int MaxPositionals,
        Func<CliArgs, int> Run,
        bool AvailableOnThisPlatform = true)
    {
        /// <summary>The first word of <see cref="Path"/> - the command group ("record").</summary>
        public string Group => Path.Split(' ')[0];

        public CliOption? FindOption(string name) =>
            Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<string> BooleanFlags => Options.Where(o => o.IsBoolean).Select(o => o.Name);
    }

    /// <summary>
    /// The table of every command the agenteyes CLI has (issue #122). Program.Main dispatches through
    /// it via <see cref="CliDispatcher"/>, the top-level usage is printed from it, and each command's
    /// --help is printed from it - so the help, the parser and the accepted flags cannot drift apart.
    /// </summary>
    internal static class CliCommands
    {
        // Options shared by the audio and video commands (Commands.MixOpts, NewSessionDir, NewManifest).
        private static readonly CliOption[] MicOptions =
        {
            new("no-denoise", null, "switch off mic noise suppression"),
            new("gate", null, "force the noise gate on"),
            new("no-gate", null, "force the noise gate off"),
            new("no-level", null, "switch off voice leveling"),
            new("mic-vol", "PCT", "microphone volume, percent"),
            new("sys-vol", "PCT", "system-audio volume, percent"),
        };

        private static readonly CliOption[] SessionOptions =
        {
            new("label", "NAME", "label used in the session folder name and manifest"),
            new("out", "DIR", "session folder (default: ./recordings/<stamp>_<label>)"),
        };

        public static readonly IReadOnlyList<CliCommand> All = new List<CliCommand>
        {
            new("screens",
                "  agenteyes screens                         # list monitors and microphones",
                Array.Empty<CliOption>(), 0,
                _ => Commands.Screens()),

            new("shot",
                "  agenteyes shot    --screen N [--region] [--out DIR] [--label NAME]",
                new CliOption[]
                {
                    new("screen", "N", "monitor number (see 'agenteyes screens')"),
                    new("region", null, "drag a region to capture instead of the whole monitor"),
                }.Concat(SessionOptions).ToList(), 0,
                Commands.Shot),

            new("audio",
                "  agenteyes audio   --screen N (--mic \"NAME\" | --loopback | --mix --mic \"NAME\") [--seconds N] [--out DIR] [--label NAME]\n"
                + "       mic options:  --no-denoise  --gate  --no-gate  --no-level  --mic-vol PCT  --sys-vol PCT",
                new CliOption[]
                {
                    new("screen", "N", "monitor number (see 'agenteyes screens')"),
                    new("mic", "NAME", "microphone name fragment"),
                    new("loopback", null, "record system audio only"),
                    new("mix", null, "record the mic AND system audio (needs --mic)"),
                    new("seconds", "N", "stop by itself after N seconds"),
                }.Concat(MicOptions).Concat(SessionOptions).ToList(), 0,
                Commands.Audio),

            new("video",
                "  agenteyes video   --screen N [--mic \"NAME\"] [--mix | --loopback] [--region] [--seconds N] [--fps N] [--crf N] [--out DIR] [--label NAME]\n"
                + "       mic options:  --no-denoise  --gate  --no-gate  --no-level  --mic-vol PCT  --sys-vol PCT\n"
                + "       camera:       --camera \"NAME\" [--camera-fps N]   # webcam -> camera.mp4 (video only)",
                new CliOption[]
                {
                    new("screen", "N", "monitor number (see 'agenteyes screens')"),
                    new("mic", "NAME", "microphone name fragment"),
                    new("mix", null, "record the mic AND system audio"),
                    new("loopback", null, "record system audio only"),
                    new("region", null, "drag a region to record instead of the whole monitor"),
                    new("seconds", "N", "stop by itself after N seconds"),
                    new("fps", "N", "screen frame rate (default 30)"),
                    new("crf", "N", "video quality, lower is better (default 23)"),
                    new("camera", "NAME", "webcam name fragment; records camera.mp4"),
                    new("camera-fps", "N", "webcam frame rate (default 30)"),
                }.Concat(MicOptions).Concat(SessionOptions).ToList(), 0,
                Commands.Video),

            new("package",
                "  agenteyes package <recording-dir | video.mp4> [--interval N | --scene THRESHOLD] [--frames | --no-frames]",
                new CliOption[]
                {
                    new("interval", "N", "seconds between extracted frames (default 5)"),
                    new("scene", "THRESHOLD", "extract frames on scene change instead of an interval"),
                    new("frames", null, "write the frames to disk"),
                    new("no-frames", null, "serve the frames on demand instead of writing them"),
                }, 1,
                Commands.Package),

            new("compose",
                "  agenteyes compose <recording-dir>         # render the camera into recording.mp4 at the framing the preset chose",
                Array.Empty<CliOption>(), 1,
                Commands.Compose),

            new("always-on",
                "  agenteyes always-on --screen N [--mic \"NAME\"] [--system] [--counts mic|system|both] [--threshold DB|auto]\n"
                + "                     [--before SEC] [--after SEC] [--gap SEC] [--cap-gb N] [--piece SECONDS] [--fps N] [--seconds N]\n"
                + "                     [--clips DIR] [--work DIR]    record all day, keep only the stretches with sound",
                new CliOption[]
                {
                    new("screen", "N", "monitor number (default 1)"),
                    new("mic", "NAME", "microphone name fragment"),
                    new("system", null, "record system audio"),
                    new("counts", "mic|system|both", "which sound decides what is kept"),
                    new("threshold", "DB|auto", "loudness that counts as sound, dBFS at or below 0, or auto"),
                    new("before", "SEC", "seconds kept before the sound"),
                    new("after", "SEC", "seconds kept after the sound"),
                    new("gap", "SEC", "silence that closes a clip"),
                    new("cap-gb", "N", "disk cap for the clips, GB (default 5)"),
                    new("piece", "SECONDS", "length of each recorded piece (default 60)"),
                    new("fps", "N", "screen frame rate (default 10)"),
                    new("seconds", "N", "stop by itself after N seconds"),
                    new("clips", "DIR", "where kept clips go"),
                    new("work", "DIR", "working folder for the pieces"),
                }, 0,
                RunAlwaysOn,
                AvailableOnThisPlatform: AlwaysOnAvailable),

            new("housekeep",
                "  agenteyes housekeep [root] [--apply] [--days N] [--keep-video-days N] [--ceiling-gb N] [--no-transcode] [--no-frames] [--frame-days N] [--smaller-audio]\n"
                + "       reduce the recordings folder's own footprint. REPORTS ONLY unless --apply is given.",
                new CliOption[]
                {
                    new("apply", null, "actually change files (without it the pass only reports)"),
                    new("days", "N", "days to keep preserved originals"),
                    new("keep-video-days", "N", "days to keep the composed video (0 = never expire it)"),
                    new("ceiling-gb", "N", "footprint ceiling, GB: past it the oldest recordings are aged early"),
                    new("no-transcode", null, "do not transcode preserved audio"),
                    new("no-frames", null, "do not convert frames to JPEG"),
                    new("frame-days", "N", "age in days at which frames are converted to JPEG"),
                    new("smaller-audio", null, "allow preserved audio that is not bit-exact"),
                }, 1,
                Commands.Housekeep),

            new("import",
                "  agenteyes import  <video.mp4>             # import an external video into the library",
                Array.Empty<CliOption>(), 1,
                Commands.Import),

            new("translate",
                "  agenteyes translate <id> --to LANG        # translate a transcript into LANG (e.g. tr), timing preserved",
                new CliOption[] { new("to", "LANG", "target language code (e.g. tr)") }, 1,
                Commands.Translate),

            new("subtitle",
                "  agenteyes subtitle  <id> --lang LANG      # burn LANG captions into a new subtitled MP4 (ffmpeg)",
                new CliOption[] { new("lang", "LANG", "language of the transcript to burn in") }, 1,
                Commands.Subtitle),

            new("selftest",
                "  agenteyes selftest                        # headless end-to-end self-test",
                Array.Empty<CliOption>(), 0,
                _ => SelfTest.Run()),

            // Issue #102: the headless service and its clients.
            new("serve",
                "  agenteyes serve [--port N]                # headless recorder + Control API (the systemd service runs this)",
                new CliOption[] { new("port", "N", "Control API port (default 7882)") }, 0,
                Headless.HeadlessHost.Run),

            new("record start",
                "  agenteyes record start [--mix | --loopback] [--mic \"NAME\"] [--screen N] [--camera \"NAME\"] [--fps N] [--audio-only]",
                new CliOption[]
                {
                    new("mix", null, "record the mic AND system audio (needs --mic)"),
                    new("loopback", null, "record system audio only"),
                    new("mic", "NAME", "microphone name fragment (see 'agenteyes screens')"),
                    new("screen", "N", "monitor number (default 1)"),
                    new("camera", "NAME", "webcam name fragment"),
                    new("fps", "N", "screen frame rate"),
                    new("audio-only", null, "record audio without the screen"),
                }, 0,
                Headless.HeadlessCli.RecordStart),

            new("record stop",
                "  agenteyes record stop                     # stop and save the service's recording",
                Array.Empty<CliOption>(), 0,
                Headless.HeadlessCli.RecordStop),

            new("record status",
                "  agenteyes record status                   # what the service is doing",
                Array.Empty<CliOption>(), 0,
                Headless.HeadlessCli.RecordStatus),

            new("signin",
                "  agenteyes signin                          # sign in to the DevThrottle account used for transcription and titles",
                Array.Empty<CliOption>(), 0,
                _ => Headless.HeadlessCli.SignIn()),

            new("signout",
                "  agenteyes signout                         # delete the stored DevThrottle key",
                Array.Empty<CliOption>(), 0,
                _ => Headless.HeadlessCli.SignOut()),

            new("account",
                "  agenteyes account                         # which DevThrottle account is signed in",
                Array.Empty<CliOption>(), 0,
                _ => Headless.HeadlessCli.Account()),
        };

#if WINDOWS
        private const bool AlwaysOnAvailable = true;
#else
        private const bool AlwaysOnAvailable = false;
#endif

        private static int RunAlwaysOn(CliArgs opts)
        {
#if MACOS
            throw Platform.Mac.NotOnMac.Feature("Always-on recording");
#elif !WINDOWS
            throw Platform.Linux.NotOnLinux.Feature("Always-on recording");
#else
            return AlwaysOn.AlwaysOnCli.Run(opts);
#endif
        }
    }
}
