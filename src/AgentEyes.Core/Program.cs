using System;

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
    ///   compose &lt;dir&gt;           render camera.mp4 into recording.mp4 as a corner inset
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
            // FIRST of all, before anything resolves a path or logs (issue #161): a person who set
            // AGENTEYES_REDIRECT_DATA / AGENTEYES_REDIRECT_VIDEOS gets BOTH roots - state, log and
            // recordings - moved, or the start refused. A refusal is said on stderr only: the one log it
            // could reach is the real one the redirect exists to keep this process out of.
            var redirect = LibraryRedirect.ApplyFromEnvironment(out string redirectMessage);
            if (redirect == LibraryRedirect.Outcome.Refused)
            {
                Console.Error.WriteLine("[error] " + redirectMessage);
                return LibraryRedirect.RefusedExitCode;
            }
            if (redirect == LibraryRedirect.Outcome.Redirected)
                Console.Error.WriteLine("[redirect] " + redirectMessage);

            // Then, before anything else can log: Log creates the AgentEyes data folder, and the one-time
            // qa-record -> AgentEyes move skips itself for good once that folder exists.
            StorageMigration.Run();

            // Issue #122: every command line goes through the dispatcher, which prints help or refuses
            // an unknown flag BEFORE any command handler runs.
            return CliDispatcher.Run(args, Console.Out, Console.Error, CliCommandTable.All);
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
