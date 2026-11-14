using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentEyes
{
    /// <summary>
    /// Turns an agenteyes command line into exactly one of: help printed, a refusal, or ONE command
    /// handler run (issue #122).
    ///
    /// The defect this exists for: "agenteyes record start --help" started a real screen recording,
    /// because nothing handled --help and an unrecognised flag was ignored. The rules now are:
    /// <list type="bullet">
    /// <item>--help, -h, or the word "help" anywhere on a command line prints that command's help
    /// and exits 0. No command handler runs; the only thing written is this class's own lines in
    /// the CLI log.</item>
    /// <item>A flag the command does not declare in <see cref="CliCommands"/>, a value-taking flag
    /// given no value, or more positional arguments than the command takes is refused with exit 1,
    /// naming the token and pointing at --help. No command handler runs.</item>
    /// <item>Only then does the handler run.</item>
    /// </list>
    /// </summary>
    internal static class CliDispatcher
    {
        /// <summary>
        /// Run one command line against <paramref name="commands"/> (the real table is
        /// <see cref="CliCommands.All"/>). Returns the process exit code:
        /// 0 ok/help, 1 usage error, 2 not built, 3 unexpected error, 4 not on this platform.
        /// </summary>
        public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
            IReadOnlyList<CliCommand> commands)
        {
            Log.Info($"[CliDispatcher] Run: args={args.Count} command={(args.Count > 0 ? args[0] : "(none)")}");
            int code;
            try
            {
                code = Dispatch(args, stdout, stderr, commands);
            }
            catch (UsageException ux)
            {
                Log.Warn($"[CliDispatcher] Run: usage error: {ux.Message}");
                stderr.WriteLine("[error] " + ux.Message);
                code = 1;
            }
#if !WINDOWS
            catch (PlatformNotSupportedException px)
            {
                // Issue #102: a Windows-only feature reached on Linux - said plainly, its own exit code.
                Log.Warn($"[CliDispatcher] Run: not on this platform: {px.Message}");
                stderr.WriteLine("[not-on-this-platform] " + px.Message);
                code = 4;
            }
#endif
            catch (NotYetBuiltException nb)
            {
                // Honest scaffold boundary - no silent fallback, clear next step.
                Log.Warn($"[CliDispatcher] Run: not built: {nb.Message}");
                stderr.WriteLine("[not-built] " + nb.Message);
                code = 2;
            }
            catch (Exception ex)
            {
                Log.Error("[CliDispatcher] Run FAILED", ex);
                stderr.WriteLine("[error] " + ex.Message);
                code = 3;
            }
            Log.Info($"[CliDispatcher] Run: exit={code}");
            return code;
        }

        private static int Dispatch(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
            IReadOnlyList<CliCommand> commands)
        {
            if (args.Count == 0)
            {
                WriteTopUsage(stdout, commands);
                return 1;
            }

            string word = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToList();

            // "agenteyes help [command...]", "agenteyes --help", "agenteyes -h".
            if (CliArgs.IsHelpToken(word))
            {
                var topic = rest.Where(t => !CliArgs.IsHelpToken(t)).Select(t => t.ToLowerInvariant()).ToList();
                if (topic.Count == 0)
                {
                    Log.Info("[CliDispatcher] Dispatch: top-level help");
                    WriteTopUsage(stdout, commands);
                    return 0;
                }
                var topicGroup = commands.Where(c => c.Group == topic[0]).ToList();
                if (topicGroup.Count == 0)
                    throw new UsageException($"no help for unknown command '{string.Join(" ", topic)}'. See: agenteyes --help");
                var topicCommand = topicGroup.FirstOrDefault(c => c.Path == string.Join(" ", topic.Take(2)))
                                   ?? topicGroup.FirstOrDefault(c => c.Path == topic[0]);
                Log.Info($"[CliDispatcher] Dispatch: help for {(topicCommand?.Path ?? topic[0])}");
                WriteHelp(stdout, topicCommand, topicGroup);
                return 0;
            }

            var group = commands.Where(c => c.Group == word).ToList();
            if (group.Count == 0)
            {
                // As before #122: the error on stderr first, then the usage on stdout, exit 1.
                Log.Warn($"[CliDispatcher] Dispatch: unknown command {word}");
                stderr.WriteLine("[error] unknown command: " + word);
                WriteTopUsage(stdout, commands);
                return 1;
            }

            // Parse with the group's boolean flags. For a one-word command that IS the command's own
            // parse. For a multi-word one ("record start") it finds the verb wherever it sits among
            // the flags, and the tokens are parsed again with that verb's own flags below.
            var groupArgs = CliArgs.Parse(rest, group.SelectMany(c => c.BooleanFlags).Append(CliArgs.HelpWord));
            CliCommand? command = group.FirstOrDefault(c => c.Path == word);
            CliArgs opts = groupArgs;
            if (command == null)
            {
                int verbAt = Enumerable.Range(0, groupArgs.Positional.Count)
                    .FirstOrDefault(i => !CliArgs.IsHelpToken(groupArgs.Positional[i]), -1);
                if (verbAt >= 0)
                {
                    string verb = groupArgs.Positional[verbAt].ToLowerInvariant();
                    command = group.FirstOrDefault(c => c.Path == word + " " + verb);
                    if (command != null)
                    {
                        int tokenAt = groupArgs.PositionalIndex[verbAt];
                        var commandTokens = rest.Where((_, idx) => idx != tokenAt).ToList();
                        opts = CliArgs.Parse(commandTokens, command.BooleanFlags.Append(CliArgs.HelpWord));
                    }
                }
            }

            if (WantsHelp(groupArgs) || WantsHelp(opts))
            {
                Log.Info($"[CliDispatcher] Dispatch: help for {(command?.Path ?? word)}");
                WriteHelp(stdout, command, group);
                return 0;
            }

            if (command == null)
            {
                // A flag no command of the group accepts is named as such, even before the verb
                // ("record --nonsense start" would otherwise read "start" as the flag's value).
                foreach (string name in groupArgs.OptionNames.OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (group.All(c => c.FindOption(name) == null))
                        throw new UsageException($"{UnknownOption("--" + name)} for 'agenteyes {word}'. See: agenteyes {word} --help");
                }
                throw new UsageException(
                    $"{word} needs {JoinOr(group.Select(c => c.Path.Substring(word.Length + 1)))}:\n"
                    + string.Join("\n", group.Select(c => c.Synopsis))
                    + $"\nSee: agenteyes {word} --help");
            }

            Log.Info($"[CliDispatcher] Dispatch: parsed {command.Path} options=[{string.Join(",", opts.OptionNames)}] "
                     + $"positionals={opts.Positional.Count}");
            Validate(command, opts);

            Log.Info($"[CliDispatcher] Dispatch: running {command.Path}");
            return command.Run(opts);
        }

        /// <summary>Refuse anything the command does not accept. Throws <see cref="UsageException"/>.</summary>
        internal static void Validate(CliCommand command, CliArgs opts)
        {
            string see = $"See: agenteyes {command.Path} --help";
            foreach (string name in opts.OptionNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                var option = command.FindOption(name);
                if (option == null)
                    throw new UsageException($"{UnknownOption("--" + name)} for 'agenteyes {command.Path}'. {see}");
                if (!option.IsBoolean && opts.Get(name) == null)
                    throw new UsageException($"--{name} needs a value ({option.ValueName}). {see}");
            }
            if (opts.Positional.Count > command.MaxPositionals)
            {
                // A dash-led token is a path or id where the command takes one (import -intro.mp4
                // still works); past what the command takes, it is a mistyped option.
                string extra = opts.Positional[command.MaxPositionals];
                throw new UsageException(extra.Length > 1 && extra.StartsWith("-", StringComparison.Ordinal)
                    ? $"{UnknownOption(extra)} for 'agenteyes {command.Path}'. {see}"
                    : $"unexpected argument '{extra}' for 'agenteyes {command.Path}'. {see}");
            }
        }

        private static string UnknownOption(string token)
        {
            int eq = token.IndexOf('=');
            return eq > 2
                ? $"unknown option {token} (an option's value goes after a space: {token.Substring(0, eq)} {token.Substring(eq + 1)})"
                : $"unknown option {token}";
        }

        private static bool WantsHelp(CliArgs opts) =>
            opts.Has(CliArgs.HelpWord) || opts.Positional.Any(CliArgs.IsHelpToken);

        private static string JoinOr(IEnumerable<string> words)
        {
            var list = words.ToList();
            return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1];
        }

        /// <summary>The top-level usage: every command's synopsis, from the table.</summary>
        internal static void WriteTopUsage(TextWriter w, IReadOnlyList<CliCommand> commands)
        {
            w.WriteLine("AgentEyes - screen + audio recorder (CLI: agenteyes)");
            w.WriteLine();
            w.WriteLine("USAGE");
            foreach (var c in commands) w.WriteLine(c.Synopsis);
            w.WriteLine("  agenteyes <command> --help                # help for one command (also -h, or 'agenteyes help <command>')");
            w.WriteLine();
            w.WriteLine("SESSION HOTKEYS (audio/video)");
            w.WriteLine("  S = screenshot   P = pause/resume   Q = stop");
        }

        /// <summary>
        /// Help for one command (its synopsis and every option it accepts, from the table), or for a
        /// group when no single command was named ("agenteyes record --help").
        /// </summary>
        internal static void WriteHelp(TextWriter w, CliCommand? command, IReadOnlyList<CliCommand> group)
        {
            var shown = command != null ? new List<CliCommand> { command } : group.ToList();
            w.WriteLine("USAGE");
            foreach (var c in shown) w.WriteLine(c.Synopsis);
            foreach (var c in shown)
            {
                w.WriteLine();
                w.WriteLine(shown.Count == 1 ? "OPTIONS" : $"OPTIONS for agenteyes {c.Path}");
                if (c.Options.Count == 0) w.WriteLine("  (none)");
                foreach (var o in c.Options)
                {
                    string left = o.IsBoolean ? $"--{o.Name}" : $"--{o.Name} {o.ValueName}";
                    w.WriteLine($"  {left,-26} {o.Description}");
                }
                w.WriteLine($"  {"-h, --help",-26} print this help and exit; no command runs");
                if (!c.AvailableOnThisPlatform)
                    w.WriteLine("  NOTE: this command is not available on this platform.");
            }
        }
    }
}
