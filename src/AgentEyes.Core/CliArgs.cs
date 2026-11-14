using System;
using System.Collections.Generic;

namespace AgentEyes
{
    /// <summary>
    /// Minimal argument parser. Supports "--flag value", boolean "--flag",
    /// and collects bare positional arguments (e.g. the package directory).
    ///
    /// Issue #122: the parser alone does not know which flags take a value, so on its own
    /// "--audio-only foo" reads "foo" as the flag's value. <see cref="Parse(IReadOnlyList{string}, ICollection{string})"/>
    /// takes the command's boolean flags so they never swallow the next token, and a help token
    /// ("-h" or "help") is never taken as a value - otherwise "record start --mic -h" would try to
    /// record from a microphone called "-h" instead of printing help. Which flags a command ACCEPTS is decided by
    /// <see cref="CliDispatcher"/> against <see cref="CliCommandTable"/>, not here.
    /// </summary>
    internal sealed class CliArgs
    {
        /// <summary>The short help switch. Never read as an option's value.</summary>
        public const string ShortHelp = "-h";

        /// <summary>The help word ("--help" as a flag, "help" as a bare word). Never read as an option's value.</summary>
        public const string HelpWord = "help";

        private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = new();
        private readonly List<int> _positionalIndex = new();

        public static CliArgs Parse(IReadOnlyList<string> args) => Parse(args, Array.Empty<string>());

        /// <summary>
        /// Parse <paramref name="args"/>. A flag named in <paramref name="booleanFlags"/> never takes
        /// the next token as its value, so that token stays a positional argument.
        /// </summary>
        public static CliArgs Parse(IReadOnlyList<string> args, IEnumerable<string> booleanFlags)
        {
            var booleans = new HashSet<string>(booleanFlags, StringComparer.OrdinalIgnoreCase);
            var a = new CliArgs();
            for (int i = 0; i < args.Count; i++)
            {
                string token = args[i];
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    string key = token.Substring(2);
                    // Look ahead: is the next token a value or another flag?
                    if (!booleans.Contains(key)
                        && i + 1 < args.Count
                        && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        && !IsHelpToken(args[i + 1]))
                    {
                        a._opts[key] = args[++i];
                    }
                    else
                    {
                        a._opts[key] = null; // boolean flag (or a value-taking flag given no value)
                    }
                }
                else
                {
                    a._positional.Add(token);
                    a._positionalIndex.Add(i);
                }
            }
            return a;
        }

        /// <summary>"-h", "--help" or "help" (the word and the long flag in any case).</summary>
        public static bool IsHelpToken(string token) =>
            string.Equals(token, HelpWord, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "--" + HelpWord, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, ShortHelp, StringComparison.Ordinal);

        public bool Has(string key) => _opts.ContainsKey(key);

        public string? Get(string key) => _opts.TryGetValue(key, out var v) ? v : null;

        public string Require(string key, string commandHint)
        {
            if (!_opts.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
            {
                throw new UsageException($"missing required --{key}. {commandHint}");
            }
            return v!;
        }

        public int RequireInt(string key, string commandHint)
        {
            string raw = Require(key, commandHint);
            if (!int.TryParse(raw, out int value))
            {
                throw new UsageException($"--{key} must be a number, got '{raw}'.");
            }
            return value;
        }

        public IReadOnlyList<string> Positional => _positional;

        /// <summary>The names of every "--flag" given, in no particular order (without the dashes).</summary>
        public IEnumerable<string> OptionNames => _opts.Keys;

        /// <summary>For each entry of <see cref="Positional"/>, its index in the token list that was parsed.</summary>
        public IReadOnlyList<int> PositionalIndex => _positionalIndex;
    }
}
