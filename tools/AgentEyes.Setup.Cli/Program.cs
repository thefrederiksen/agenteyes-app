using System.Text.Json;
using AgentEyes.Setup.Engine;

namespace AgentEyes.Setup.Cli;

/// <summary>
/// The headless CLI front-end over AgentEyes.Setup.Engine. Same engine the
/// wizard uses, so a human and an agent install/update identically.
///
/// Exit codes: 0 ok, 1 runtime error, 2 usage error.
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;

    public static async Task<int> Main(string[] argv)
    {
        var args = CliArgs.Parse(argv);
        var json = args.HasFlag("json");

        var layout = ResolveLayout(args);
        WireLogging(layout);

        try
        {
            return args.Command.ToLowerInvariant() switch
            {
                "components" => Commands.Components(args, layout, json),
                "status" => Commands.Status(args, layout, json),
                "plan" => await Commands.PlanAsync(args, layout, json),
                "update" => await Commands.UpdateAsync(args, layout, json, installMode: false),
                "install" => await Commands.UpdateAsync(args, layout, json, installMode: true),
                "uninstall" => Commands.Uninstall(args, layout, json),
                "help" or "--help" => Help(),
                _ => Unknown(args.Command),
            };
        }
        catch (UsageException ux)
        {
            Console.Error.WriteLine($"usage error: {ux.Message}");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            EngineLog.Write($"[Program] FAILED: {ex}");
            return ExitError;
        }
    }

    private static InstallLayout ResolveLayout(CliArgs args)
    {
        var root = args.Option("root");
        return root is null ? InstallLayout.Default() : new InstallLayout(root);
    }

    private static void WireLogging(InstallLayout layout)
    {
        try
        {
            Directory.CreateDirectory(layout.LogsDir);
            var logPath = Path.Combine(layout.LogsDir, "setup-cli.log");
            EngineLog.Sink = line =>
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
                catch { /* logging must never throw */ }
            };
        }
        catch { /* logging setup must never block the command */ }
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            agenteyes-setup - install, update, and uninstall AgentEyes

            Commands:
              components                 List known components and their assets/paths
              status                     Show installed components and their versions
              plan                       Show what an update/install would change
              install                    Install or update all components, then finalize
                                         (PATH, Start Menu shortcut, Add/Remove Programs)
              update                     Download, verify, and apply updates only
              uninstall                  Remove install-owned files (your data is preserved)

            Options:
              --manifest <path|latest>   Release source (default latest = GitHub Releases)
              --release-dir <dir>        Use a local directory as the release (offline)
              --component <id|all>       Limit update to one component (default all)
              --autostart <on|off>       install only: set run-at-login (default: keep as-is)
              --desktop-shortcut         install only: also create a desktop shortcut
              --root <dir>               Override the per-user root %LOCALAPPDATA%\AgentEyes (testing)
              --no-finalize              Skip PATH/shortcut/registry finalization (testing)
              --dry-run                  Plan only; do not download or apply
              --json                     Machine-readable output
            """);
        return ExitOk;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}. Run 'help'.");
        return ExitUsage;
    }

    internal static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Thrown for malformed command invocations; mapped to exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);
