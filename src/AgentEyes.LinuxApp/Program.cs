using System;
using System.Linq;
using AgentEyes.LinuxApp.Services;
using Avalonia;

namespace AgentEyes.LinuxApp
{
    /// <summary>
    /// The AgentEyes desktop app for Linux (issue #107).
    ///   AgentEyesLinux          open the window (or bring the running one forward)
    ///   AgentEyesLinux --tray   start in the tray only - what the login autostart runs
    ///   AgentEyesLinux --view library|record|dictionary, --details ID, --preview ID, --settings general|account
    ///                           open that view or window (in the running app when there is one)
    /// </summary>
    internal static class Program
    {
        public static bool StartInTray { get; private set; }
        public static SingleInstance? Instance { get; private set; }

        /// <summary>What to open once the window is up ("show" by default).</summary>
        public static string StartCommand { get; private set; } = "show";

        [STAThread]
        public static int Main(string[] args)
        {
            // Issue #161: before anything logs or resolves a path. AGENTEYES_REDIRECT_DATA and
            // AGENTEYES_REDIRECT_VIDEOS move this app's settings and log with the service's library,
            // or the start is refused on stderr (not logged: the only reachable log is the real one).
            var redirect = LibraryRedirect.ApplyFromEnvironment(out string redirectMessage);
            if (redirect == LibraryRedirect.Outcome.Refused)
            {
                Console.Error.WriteLine("[error] " + redirectMessage);
                return LibraryRedirect.RefusedExitCode;
            }
            if (redirect == LibraryRedirect.Outcome.Redirected)
            {
                // The Linux app is a CLIENT of the service and always drives it on the fixed address
                // ControlClient.BaseUrl - the person's REAL service. Redirected, it would keep scratch
                // settings and a scratch log while showing and driving the real library: a half-redirect,
                // which is worse than none. Refused, loudly; the redirected way to drive a scratch
                // service is the redirected CLI (agenteyes record start|stop|status).
                string refusal = $"library redirect refused: the Linux app always drives the service at "
                    + $"{ControlClient.BaseUrl}, the person's REAL AgentEyes, so it cannot run redirected. Fix: unset "
                    + $"{LibraryRedirect.DataVariable} and {LibraryRedirect.VideosVariable}, or drive a redirected "
                    + "'agenteyes serve --port N' with the redirected 'agenteyes record ...' commands.";
                Log.Error("[Program] Main: " + refusal);
                Console.Error.WriteLine("[error] " + refusal);
                return LibraryRedirect.RefusedExitCode;
            }

            StartInTray = args.Contains("--tray");
            StartCommand = CommandFrom(args);
            Log.Info($"[Program] Main: AgentEyes Linux app starting (tray={StartInTray}, pid={Environment.ProcessId})");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Error("[Program] unhandled exception - the app is going down", e.ExceptionObject as Exception);

            Instance = SingleInstance.TryAcquire(StartCommand);
            if (Instance == null) return 0;
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            }
            finally
            {
                Instance.Dispose();
                Log.Info("[Program] Main: exited");
            }
        }

        /// <summary>The command a launch asks for, from its arguments. Pure, unit-tested.</summary>
        internal static string CommandFrom(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--view": return "view:" + args[i + 1].ToLowerInvariant();
                    case "--details": return "details:" + args[i + 1];
                    case "--preview": return "preview:" + args[i + 1];
                    case "--settings": return "settings:" + args[i + 1].ToLowerInvariant();
                    case "--snap": return "snap:" + args[i + 1];
                }
            }
            return args.Contains("--tray") ? "tray" : "show";
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions { WmClass = "AgentEyesLinux" })
                .LogToTrace();
    }
}
