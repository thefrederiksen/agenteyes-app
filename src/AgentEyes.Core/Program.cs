using System;
using System.Collections.Generic;

namespace AgentEyes
{
    /// <summary>
    /// AgentEyes - the always-on screen + audio recorder engine (CLI: agenteyes).
    /// CLI entry point and command dispatch. See docs/ for the product vision.
    ///
    /// Commands:
    ///   screens                 list monitors and microphones
    ///   shot    --screen N      Mode C: instant screenshot (full monitor or --region)
    ///   audio   --screen N ...  Mode A: mic audio + on-demand screenshots
    ///   video   --screen N ...  Mode B: screen video + audio (Phase 3, not yet built)
    ///   package &lt;dir|video&gt;     transcribe + assemble walkthrough (dir with manifest, or bare video file)
    ///   import  &lt;video&gt;          import an external video file into the recording library
    ///   translate &lt;id&gt; --to L   translate a recording's transcript into another language (VTT, timing preserved)
    ///   subtitle  &lt;id&gt; --lang L  burn a language's transcript into a new subtitled library video (ffmpeg)
    /// </summary>
    internal static class Program
    {
        // STA is required for WPF (region overlay) and WinForms clipboard access.
        [STAThread]
        private static int Main(string[] args)
        {
            StorageMigration.Run();   // qa-record -> AgentEyes folders, one time

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string command = args[0].ToLowerInvariant();
            var rest = new List<string>(args);
            rest.RemoveAt(0);
            var opts = CliArgs.Parse(rest);

            try
            {
                switch (command)
                {
                    case "screens":
                        return Commands.Screens();

                    case "shot":
                        return Commands.Shot(opts);

                    case "audio":
                        return Commands.Audio(opts);

                    case "video":
                        return Commands.Video(opts);

                    case "package":
                        return Commands.Package(opts);

                    case "import":
                        return Commands.Import(opts);

                    case "translate":
                        return Commands.Translate(opts);

                    case "subtitle":
                        return Commands.Subtitle(opts);

                    case "selftest":
                        return SelfTest.Run();

                    case "-h":
                    case "--help":
                    case "help":
                        PrintUsage();
                        return 0;

                    default:
                        Console.Error.WriteLine("[error] unknown command: " + command);
                        PrintUsage();
                        return 1;
                }
            }
            catch (UsageException ux)
            {
                Console.Error.WriteLine("[error] " + ux.Message);
                return 1;
            }
            catch (NotYetBuiltException nb)
            {
                // Honest scaffold boundary - no silent fallback, clear next step.
                Console.Error.WriteLine("[not-built] " + nb.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[error] " + ex.Message);
                return 3;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("AgentEyes - screen + audio recorder (CLI: agenteyes)");
            Console.WriteLine();
            Console.WriteLine("USAGE");
            Console.WriteLine("  agenteyes screens");
            Console.WriteLine("  agenteyes shot    --screen N [--region] [--out DIR] [--label NAME]");
            Console.WriteLine("  agenteyes audio   --screen N (--mic \"NAME\" | --loopback | --mix --mic \"NAME\") [--seconds N]");
            Console.WriteLine("  agenteyes video   --screen N [--mic \"NAME\"] [--mix | --loopback] [--region] [--seconds N]");
            Console.WriteLine("       mic options:  --no-denoise  --no-gate  --no-level  --mic-vol PCT  --sys-vol PCT");
            Console.WriteLine("       camera:       --camera \"NAME\" [--camera-fps N]   # webcam -> camera.mp4 (video only)");
            Console.WriteLine("  agenteyes package <recording-dir | video.mp4> [--interval N | --scene THRESHOLD]");
            Console.WriteLine("  agenteyes import  <video.mp4>             # import an external video into the library");
            Console.WriteLine("  agenteyes translate <id> --to LANG        # translate a transcript into LANG (e.g. tr), timing preserved");
            Console.WriteLine("  agenteyes subtitle  <id> --lang LANG      # burn LANG captions into a new subtitled MP4 (ffmpeg)");
            Console.WriteLine("  agenteyes selftest                        # headless end-to-end self-test");
            Console.WriteLine();
            Console.WriteLine("SESSION HOTKEYS (audio/video)");
            Console.WriteLine("  S = screenshot   P = pause/resume   Q = stop");
        }
    }

    /// <summary>Raised for bad/missing arguments - maps to a clean exit code 1.</summary>
    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
        public UsageException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Raised by scaffold stubs for phases not yet implemented - exit code 2.</summary>
    internal sealed class NotYetBuiltException : Exception
    {
        public NotYetBuiltException(string message) : base(message) { }
    }
}
