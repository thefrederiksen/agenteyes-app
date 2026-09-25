namespace AgentEyes.Setup.Cli;

/// <summary>
/// Tiny argument parser: a leading positional command, then "--key value" pairs
/// and "--flag" switches. No external dependency; deterministic and easy to test.
///
/// The vocabulary is CLOSED (issue #83). Every option token must be one of
/// <see cref="KnownFlags"/> or <see cref="KnownOptions"/>, and no command but "help" takes
/// a positional; anything else is a <see cref="UsageException"/> and the command never
/// runs. Before this, an unknown token was silently filed as a flag, so "install --bogus"
/// ran a real install - and so did "install --help", because the parser knew "help" but
/// nothing acted on it unless it was the first argument. <see cref="WantsHelp"/> is that
/// flag, seen anywhere.
///
/// No logging here on purpose: Parse runs before the host has decided whether a log file
/// may be opened at all (help must not create one). Program logs the parse summary
/// (<see cref="ToString"/>) once the sink exists.
/// </summary>
public sealed class CliArgs
{
    /// <summary>The leading positional, or "help" when the line is empty or starts with an option.</summary>
    public string Command { get; }

    /// <summary>Positionals after the command. Only "help &lt;command&gt;" takes one.</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>True when "--help" or "-h" appears ANYWHERE on the line (issue #83).</summary>
    public bool WantsHelp => HasFlag(HelpFlag);

    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private const string HelpFlag = "help";
    private const string HelpCommand = "help";

    /// <summary>Switches that take no value.</summary>
    public static readonly IReadOnlySet<string> KnownFlags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "json", "dry-run", HelpFlag, "relaunched", "desktop-shortcut", "no-finalize",
        };

    /// <summary>Options that take exactly one value ("--key value").</summary>
    public static readonly IReadOnlySet<string> KnownOptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "manifest", "release-dir", "component", "autostart", "root",
        };

    private CliArgs(string command, List<string> positionals, Dictionary<string, string> options, HashSet<string> flags)
    {
        Command = command;
        Positionals = positionals;
        _options = options;
        _flags = flags;
    }

    /// <summary>
    /// Parse a command line. Throws <see cref="UsageException"/> for an unknown option, a
    /// value option with no value, or a positional no command takes; the caller maps that to
    /// the usage exit code without running anything. A malformed line is rejected even when
    /// it also carries --help.
    /// </summary>
    public static CliArgs Parse(string[] argv)
    {
        var hasCommand = argv.Length > 0 && !IsOptionToken(argv[0]);
        var command = hasCommand ? argv[0] : HelpCommand;
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = hasCommand ? 1 : 0; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!IsOptionToken(a))
            {
                positionals.Add(a);
                continue;
            }

            if (a.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(HelpFlag);
                continue;
            }

            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"unknown option '{a}'.");

            var key = a[2..];
            if (KnownFlags.Contains(key))
            {
                flags.Add(key);
            }
            else if (KnownOptions.Contains(key))
            {
                if (i + 1 >= argv.Length || IsOptionToken(argv[i + 1]))
                    throw new UsageException($"option '{a}' requires a value.");
                options[key] = argv[++i];
            }
            else
            {
                throw new UsageException($"unknown option '{a}'.");
            }
        }

        // Only "help <command>" takes a positional. Anything else ("install /?", "install help",
        // "install extra") is a token the command would silently ignore while it RAN - the shape
        // this issue exists to close - so it is a usage error instead.
        var allowedPositionals = command.Equals(HelpCommand, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (positionals.Count > allowedPositionals)
            throw new UsageException($"unexpected argument '{positionals[allowedPositionals]}'. '{command}' takes options only.");

        return new CliArgs(command, positionals, options, flags);
    }

    public bool HasFlag(string name) => _flags.Contains(name);
    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;
    public string Option(string name, string fallback) => _options.TryGetValue(name, out var v) ? v : fallback;

    /// <summary>One-line summary for the log: command, positionals, flags, and options with their values.</summary>
    public override string ToString() =>
        $"command={Command} positionals=[{string.Join(",", Positionals)}] flags=[{string.Join(",", _flags)}] " +
        $"options=[{string.Join(",", _options.Select(kv => $"{kv.Key}={kv.Value}"))}]";

    /// <summary>An option token is '-' followed by at least one character. A bare "-" is a positional.</summary>
    private static bool IsOptionToken(string token) => token.Length > 1 && token[0] == '-';
}
