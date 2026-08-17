using System;
using System.Collections.Generic;

namespace AgentEyes
{
    /// <summary>
    /// Minimal argument parser. Supports "--flag value", boolean "--flag",
    /// and collects bare positional arguments (e.g. the package directory).
    /// </summary>
    internal sealed class CliArgs
    {
        private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = new();

        public static CliArgs Parse(IReadOnlyList<string> args)
        {
            var a = new CliArgs();
            for (int i = 0; i < args.Count; i++)
            {
                string token = args[i];
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    string key = token.Substring(2);
                    // Look ahead: is the next token a value or another flag?
                    if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        a._opts[key] = args[++i];
                    }
                    else
                    {
                        a._opts[key] = null; // boolean flag
                    }
                }
                else
                {
                    a._positional.Add(token);
                }
            }
            return a;
        }

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
    }
}
