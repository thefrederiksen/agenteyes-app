using System.Text;

namespace AgentEyes.Setup.Cli;

/// <summary>
/// The help text of the setup CLI: the command list, the general page, and one page per
/// command. Pure text - building a page touches no layout, no log file, no engine and no
/// network, which is what lets <see cref="Program"/> answer "--help" before it resolves
/// anything (issue #83). Every option a page documents is one <see cref="CliArgs"/>
/// accepts; SetupCliHelpTests pins the two vocabularies to each other.
///
/// No logging here on purpose: help is answered before the host decides whether a log file may
/// be opened at all, so a log line from here would go to a null sink (or create the very file
/// help must not create). Program logs the request once a sink exists.
/// </summary>
public static class CliHelp
{
    /// <summary>The commands the CLI dispatches, in the order the general page lists them.</summary>
    public static readonly IReadOnlyList<string> Commands =
        new[] { "components", "status", "plan", "install", "update", "uninstall" };

    /// <summary>What a usage error tells the person to do next.</summary>
    public const string UsageHint = "Run 'agenteyes-setup help' or 'agenteyes-setup <command> --help'.";

    private static readonly string NL = Environment.NewLine;

    // One line per option, shared between the general page and the command pages so the
    // wording cannot drift between them.
    private const string OptManifest = "  --manifest <path|latest>   Release source (default latest = GitHub Releases)";
    private const string OptReleaseDir = "  --release-dir <dir>        Use a local directory as the release (offline)";
    private const string OptComponent = "  --component <id|all>       Limit to one component (default all)";
    private const string OptAutostart = "  --autostart <on|off>       Set run-at-login (default: keep as-is) - install only";
    private const string OptDesktopShortcut = "  --desktop-shortcut         Also create a desktop shortcut - install only";
    private const string OptRoot = "  --root <dir>               Override the per-user root %LOCALAPPDATA%\\AgentEyes (testing)";
    private const string OptNoFinalize = "  --no-finalize              Skip PATH/shortcut/registry finalization (testing)";
    private const string OptDryRun = "  --dry-run                  Show what would change; download, apply and remove nothing";
    private const string OptJson = "  --json                     Machine-readable output";
    private const string OptHelp = "  --help, -h                 Show help and exit (nothing is run)";

    /// <summary>True when <paramref name="name"/> is a command the CLI dispatches (case-insensitive).</summary>
    public static bool IsCommand(string name)
        => Commands.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The general page: every command and every option. Ends with a newline.</summary>
    public static string General()
    {
        return Lines(
            "agenteyes-setup - install, update, and uninstall AgentEyes",
            "",
            "Usage:",
            "  agenteyes-setup <command> [options]",
            "  agenteyes-setup <command> --help      Help for one command",
            "  agenteyes-setup help [<command>]",
            "",
            "Commands:",
            "  components                 List known components and their assets/paths",
            "  status                     Show installed components and their versions",
            "  plan                       Show what an update/install would change",
            "  install                    Install or update all components, then finalize",
            "                             (PATH, Start Menu shortcut, Add/Remove Programs)",
            "  update                     Download, verify, and apply updates only",
            "  uninstall                  Remove install-owned files (your data is preserved)",
            "",
            "Options:",
            OptManifest,
            OptReleaseDir,
            OptComponent,
            OptAutostart,
            OptDesktopShortcut,
            OptRoot,
            OptNoFinalize,
            OptDryRun,
            OptJson,
            OptHelp,
            "",
            "An unknown option is a usage error (exit code 2) and nothing is run.",
            "Exit codes: 0 ok, 1 runtime error, 2 usage error.");
    }

    /// <summary>
    /// The page for one command. Ends with a newline. Throws <see cref="UsageException"/> for
    /// a name that is not a command (the switch's default arm - the one place that decides),
    /// so a typo is reported rather than answered with the wrong page.
    /// </summary>
    public static string ForCommand(string command)
    {
        return command.ToLowerInvariant() switch
        {
            "components" => Page("components",
                new[] { "List known components and their assets/paths. Reads nothing from disk or the network." },
                OptRoot, OptJson),

            "status" => Page("status",
                new[] { "Show installed components and their versions, read from the install root." },
                OptRoot, OptJson),

            "plan" => Page("plan",
                new[]
                {
                    "Show what an update/install would change: the release is fetched (or read locally) and",
                    "compared with the install root. Downloads nothing, applies nothing.",
                },
                OptManifest, OptReleaseDir, OptRoot, OptJson),

            "install" => Page("install",
                new[]
                {
                    "Install or update ALL components from the release, then finalize the per-user install:",
                    "PATH, Start Menu shortcut, Add/Remove Programs entry. Repair semantics - always finalizes.",
                    "Every component is downloaded and verified first, with AgentEyes still running. Only then",
                    "is a running AgentEyes stopped, the files swapped in (all or nothing - a failure rolls the",
                    "swapped ones back) and the installed app started again with the arguments it was running",
                    "with. If it cannot be stopped, nothing is replaced. A failed attempt is recorded so the",
                    "app's AutoUpdate does not retry that version by itself; a successful one clears the record.",
                },
                OptManifest, OptReleaseDir, OptComponent, OptAutostart, OptDesktopShortcut,
                OptNoFinalize, OptDryRun, OptRoot, OptJson),

            "update" => Page("update",
                new[]
                {
                    "Download, verify, and apply updates only. Finalizes only when something was replaced.",
                    "Every component is downloaded and verified first, with AgentEyes still running. Only then",
                    "is a running AgentEyes stopped, the files swapped in (all or nothing - a failure rolls the",
                    "swapped ones back) and the installed app started again with the arguments it was running",
                    "with. If it cannot be stopped, nothing is replaced. A failed attempt is recorded so the",
                    "app's AutoUpdate does not retry that version by itself; a successful one clears the record.",
                },
                OptManifest, OptReleaseDir, OptComponent, OptNoFinalize, OptDryRun, OptRoot, OptJson),

            "uninstall" => Page("uninstall",
                new[]
                {
                    "Remove install-owned files under the install root. Your recordings and settings are preserved.",
                    "Refuses while AgentEyes is running - quit it first (tray icon -> Quit).",
                },
                OptDryRun, OptRoot, OptJson),

            _ => throw new UsageException($"unknown command: {command}. {UsageHint}"),
        };
    }

    private static string Page(string command, string[] description, params string[] options)
    {
        var sb = new StringBuilder();
        sb.Append("Usage: agenteyes-setup ").Append(command).Append(" [options]").Append(NL);
        sb.Append(NL);
        foreach (var line in description) sb.Append(line).Append(NL);
        sb.Append(NL);
        sb.Append("Options:").Append(NL);
        foreach (var line in options) sb.Append(line).Append(NL);
        sb.Append(OptHelp).Append(NL);
        return sb.ToString();
    }

    private static string Lines(params string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(line).Append(NL);
        return sb.ToString();
    }
}
