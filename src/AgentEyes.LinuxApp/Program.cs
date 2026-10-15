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
