using System.Text.Json;
using AgentEyes.Setup.Engine;

namespace AgentEyes.Setup.Cli;

/// <summary>
/// The headless CLI front-end over AgentEyes.Setup.Engine. Same engine the
/// wizard uses, so a human and an agent install/update identically.
///
/// Exit codes: 0 ok, 1 runtime error, 2 usage error.
///
/// Order of business (issue #83): parse, then answer help / reject an unknown command or
/// option, and only THEN resolve the install root, open the log file and run a command.
/// "install --help" used to run a real install because help was dispatched only when it
/// was the first argument; the line below that touches anything is the one after help.
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;

    public static Task<int> Main(string[] argv) => RunAsync(argv, Console.Out, Console.Error);

    /// <summary>
    /// The whole CLI, with the two streams injectable so a test can run the real entry
    /// point and read what it printed. Help and usage errors are written to the given
    /// writers; a command that runs writes through the same streams (Console) it always did.
    /// </summary>
    public static async Task<int> RunAsync(string[] argv, TextWriter stdout, TextWriter stderr)
    {
        CliArgs args;
        try
        {
            args = CliArgs.Parse(argv);
        }
        catch (UsageException ux)
        {
            stderr.WriteLine($"usage error: {ux.Message}");
            stderr.WriteLine(CliHelp.UsageHint);
            return ExitUsage;
        }

        var command = args.Command.ToLowerInvariant();

        // Help exits here: no layout, no log directory, no engine object, no network.
        if (args.WantsHelp || command == "help")
        {
            var topic = command == "help" ? args.Positionals.FirstOrDefault() : command;
            if (topic is null)
            {
                stdout.Write(CliHelp.General());
                return ExitOk;
            }
            if (!CliHelp.IsCommand(topic))
                return Unknown(topic, stderr);
            stdout.Write(CliHelp.ForCommand(topic));
            return ExitOk;
        }

        if (!CliHelp.IsCommand(command))
            return Unknown(command, stderr);

        var json = args.HasFlag("json");
        var layout = ResolveLayout(args);
        WireLogging(layout);
        EngineLog.Write($"[Program] RunAsync: command={command} root={layout.LocalRoot}");

        try
        {
            var exit = command switch
            {
                "components" => Commands.Components(args, layout, json),
                "status" => Commands.Status(args, layout, json),
                "plan" => await Commands.PlanAsync(args, layout, json),
                "update" => await Commands.UpdateAsync(args, layout, json, installMode: false),
                "install" => await Commands.UpdateAsync(args, layout, json, installMode: true),
                "uninstall" => Commands.Uninstall(args, layout, json),
                _ => throw new InvalidOperationException($"'{command}' is listed in CliHelp.Commands but has no dispatch."),
            };
            EngineLog.Write($"[Program] RunAsync: command={command} exit={exit}");
            return exit;
        }
        catch (UsageException ux)
        {
            stderr.WriteLine($"usage error: {ux.Message}");
            EngineLog.Write($"[Program] RunAsync usage error: {ux.Message}");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
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

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine($"unknown command: {command}. {CliHelp.UsageHint}");
        return ExitUsage;
    }

    internal static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Thrown for malformed command invocations; mapped to exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);
