using System;
using System.Collections.Generic;
using System.IO;
using AgentEyes.Setup.Engine;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The test assembly's own entry point (issue #94). xunit never calls it; the test SDK's generated
    /// empty Main is switched off in the csproj so this one can stand in for the UPDATER in
    /// <see cref="ProcessAppLauncherTests"/>: a test starts this assembly as a separate process with its
    /// stdout captured through a pipe - exactly how a script captures "agenteyes-setup update" - and this
    /// Main runs the real <see cref="ProcessAppLauncher"/>, prints the child's pid and exits. Whether the
    /// test's pipe then closes while the child keeps running is the whole question of issue #94.
    /// </summary>
    public static class LaunchProbe
    {
        /// <summary>The switch that selects the probe; anything else is a usage error.</summary>
        public const string Switch = "--launch-probe";

        /// <summary>
        /// Usage: <c>--launch-probe &lt;layoutRoot&gt; &lt;exe&gt; [arg ...]</c>. Prints <c>pid=N</c> on stdout
        /// and exits 0; exits 1 with the reason on stderr when the launch fails; 2 on a usage error.
        /// </summary>
        public static int Main(string[] args)
        {
            if (args.Length < 3 || !string.Equals(args[0], Switch, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"usage: AgentEyes.Tests.dll {Switch} <layoutRoot> <exe> [arg ...]");
                return 2;
            }

            EngineLog.Sink = line => Console.Error.WriteLine(line);
            try
            {
                var layout = new InstallLayout(args[1]);
                var arguments = new List<string>();
                for (int i = 3; i < args.Length; i++) arguments.Add(args[i]);

                int pid = new ProcessAppLauncher(layout).Launch(args[2], arguments);
                Console.Out.WriteLine($"pid={pid}");
                Console.Out.Flush();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"launch-probe FAILED: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
    }
}
