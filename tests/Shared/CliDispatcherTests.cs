using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Xunit;

// Issue #122. ONE source file, compiled into all three suites (AgentEyes.Tests on Windows,
// AgentEyes.Tests.Linux, AgentEyes.Tests.Mac) through a <Compile Include="..\Shared\..." Link> in each
// csproj: these are pure argument-parsing tests over the platform-independent dispatcher and command
// table, so every platform's build of AgentEyes.Core must pass the same ones. Nothing here launches a
// process, touches the network or records anything.
namespace AgentEyes.Tests.Shared
{
    using CliArgs = global::AgentEyes.CliArgs;
    using CliCommand = global::AgentEyes.CliCommand;
    using CliCommandTable = global::AgentEyes.CliCommandTable;
    using CliDispatcher = global::AgentEyes.CliDispatcher;

    /// <summary>
    /// The CLI dispatcher refuses or answers help BEFORE any handler runs (issue #122).
    ///
    /// HOW "NO RECORDING STARTED" IS MEASURED: every test runs the REAL command table with only the
    /// handlers swapped for a spy that records each call, synchronously, before Run returns. "No
    /// recording" is the spy's call list being EMPTY - an absence - so every absence assertion here
    /// has a paired PRESENCE control through the same harness: <see cref="CorrectInvocation_EveryCommand_RunsExactlyThatHandler"/>
    /// and <see cref="RecordStart_CorrectInvocations_ReachTheHandlerWithTheirFlags"/> prove the spy
    /// DOES see a call when a handler runs, so an empty list cannot mean a broken spy. The help tests
    /// also require the help TEXT to be present (the synopsis and every option), not merely that
    /// nothing else happened.
    /// </summary>
    public sealed class CliDispatcherTests
    {
        private sealed class Harness
        {
            public readonly List<(string Path, CliArgs Opts)> Calls = new();
            public readonly StringWriter Out = new();
            public readonly StringWriter Err = new();
            public readonly List<CliCommand> Commands;

            public Harness()
            {
                Commands = CliCommandTable.All
                    .Select(c => c with { Run = o => { Calls.Add((c.Path, o)); return 0; } })
                    .ToList();
            }

            public int Run(params string[] args) =>
                CliDispatcher.Run(args, Out, Err, Commands);
        }

        public static IEnumerable<object[]> EveryCommand() =>
            CliCommandTable.All.Select(c => new object[] { c.Path });

        public static IEnumerable<object[]> EveryCommandAndHelpForm() =>
            from c in CliCommandTable.All
            from form in new[] { "--help", "-h", "help" }
            select new object[] { c.Path, form };

        private static CliCommand Spec(string path) => CliCommandTable.All.Single(c => c.Path == path);

        private static string[] Words(string path, params string[] more) =>
            path.Split(' ').Concat(more).ToArray();

        // ---- criterion 1, 2, 3: help on every command runs nothing ---------------------------

        [Theory]
        [MemberData(nameof(EveryCommandAndHelpForm))]
        public void Run_CommandWithHelp_PrintsItsHelpAndRunsNoHandler(string path, string helpForm)
        {
            var h = new Harness();

            int code = h.Run(Words(path, helpForm));

            Assert.Equal(0, code);
            Assert.Empty(h.Calls);
            string help = h.Out.ToString();
            Assert.Contains(Spec(path).Synopsis, help);
            foreach (var o in Spec(path).Options)
                Assert.Contains("--" + o.Name, help);
            Assert.Contains("-h, --help", help);
            Assert.Equal("", h.Err.ToString());
        }

        [Fact]
        public void Run_RecordStartHelp_ListsEveryOptionRecordStartAccepts()
        {
            var h = new Harness();

            int code = h.Run("record", "start", "--help");

            Assert.Equal(0, code);
            Assert.Empty(h.Calls);
            string help = h.Out.ToString();
            foreach (string flag in new[] { "--mix", "--loopback", "--mic NAME", "--screen N", "--camera NAME", "--fps N", "--audio-only" })
                Assert.Contains(flag, help);
        }

        [Theory]
        [InlineData("record", "start", "--audio-only", "--help")]
        [InlineData("record", "start", "--audio-only", "-h")]          // a boolean must not swallow -h
        [InlineData("record", "start", "--audio-only", "help")]        // ... nor the word help
        [InlineData("record", "start", "--mic", "-h")]                 // -h is never an option's value
        [InlineData("record", "start", "--mic", "help")]               // ... nor is the word help
        [InlineData("record", "start", "--mic", "Yealink", "--help")]
        [InlineData("record", "--help", "start")]
        [InlineData("record", "start", "--nonsense", "--help")]        // help wins over a refusal
        [InlineData("help", "record", "start")]
        [InlineData("--help", "record", "start")]
        [InlineData("-h", "record", "start")]
        [InlineData("RECORD", "START", "--HELP")]
        public void Run_RecordStartHelpInAnyPosition_PrintsRecordStartHelpAndRunsNothing(params string[] args)
        {
            var h = new Harness();

            int code = h.Run(args);

            Assert.Equal(0, code);
            Assert.Empty(h.Calls);
            Assert.Contains(Spec("record start").Synopsis, h.Out.ToString());
            Assert.Contains("--audio-only", h.Out.ToString());
        }

        [Theory]
        [InlineData("record", "--help")]
        [InlineData("record", "help")]
        [InlineData("help", "record")]
        public void Run_RecordGroupHelp_ListsAllThreeVerbsAndRunsNothing(params string[] args)
        {
            var h = new Harness();

            int code = h.Run(args);

            Assert.Equal(0, code);
            Assert.Empty(h.Calls);
            foreach (string verb in new[] { "record start", "record stop", "record status" })
                Assert.Contains(Spec(verb).Synopsis, h.Out.ToString());
        }

        [Theory]
        [InlineData("--help")]
        [InlineData("-h")]
        [InlineData("help")]
        public void Run_TopLevelHelp_PrintsEverySynopsisExitsZeroRunsNothing(string form)
        {
            var h = new Harness();

            int code = h.Run(form);

            Assert.Equal(0, code);
            Assert.Empty(h.Calls);
            foreach (var c in CliCommandTable.All) Assert.Contains(c.Synopsis, h.Out.ToString());
        }

        // ---- criterion 4: an unknown flag is refused ----------------------------------------

        [Theory]
        [MemberData(nameof(EveryCommand))]
        public void Run_CommandWithUnknownFlag_ExitsOneNamingTheFlagAndRunsNoHandler(string path)
        {
            var h = new Harness();

            int code = h.Run(Words(path, "--nonsense"));

            Assert.Equal(1, code);
            Assert.Empty(h.Calls);
            string err = h.Err.ToString();
            Assert.Contains("--nonsense", err);
            Assert.Contains("--help", err);
        }

        [Theory]
        [InlineData("unknown option --nonsense", "record", "start", "--nonsense")]
        [InlineData("unknown option --fps=30", "record", "start", "--fps=30")]
        [InlineData("unknown option -x", "record", "start", "-x")]
        [InlineData("an option's value goes after a space: --mic Yeti", "record", "start", "--mic=Yeti")]
        [InlineData("unknown option --region", "record", "start", "--region")]           // a flag of another command
        [InlineData("unexpected argument 'hepl'", "record", "start", "hepl")]
        [InlineData("--mic needs a value", "record", "start", "--mic")]
        [InlineData("--screen needs a value", "record", "start", "--screen", "--audio-only")]
        [InlineData("unknown option --nonsense", "record", "--nonsense", "start")]
        [InlineData("unknown option --audio-only", "record", "stop", "--audio-only")]
        [InlineData("record needs start, stop or status", "record")]
        [InlineData("record needs start, stop or status", "record", "begin")]
        public void Run_RecordWithBadArguments_IsRefusedWithAClearMessageAndRunsNothing(string message, params string[] args)
        {
            var h = new Harness();

            int code = h.Run(args);

            Assert.Equal(1, code);
            Assert.Empty(h.Calls);
            Assert.Contains(message, h.Err.ToString());
        }

        [Fact]
        public void Run_UnknownCommand_ExitsOneAndRunsNothing()
        {
            var h = new Harness();

            int code = h.Run("recrod", "start");

            Assert.Equal(1, code);
            Assert.Empty(h.Calls);
            Assert.Contains("unknown command: recrod", h.Err.ToString());
        }

        // ---- criterion 5 (the presence controls): correct invocations still run ----------------

        [Theory]
        [MemberData(nameof(EveryCommand))]
        public void CorrectInvocation_EveryCommand_RunsExactlyThatHandler(string path)
        {
            var h = new Harness();

            int code = h.Run(Words(path));

            Assert.Equal(0, code);
            Assert.Single(h.Calls);
            Assert.Equal(path, h.Calls[0].Path);
            Assert.Equal("", h.Err.ToString());
        }

        [Fact]
        public void RecordStart_CorrectInvocations_ReachTheHandlerWithTheirFlags()
        {
            var h = new Harness();

            Assert.Equal(0, h.Run("record", "start", "--audio-only"));
            Assert.Equal(0, h.Run("record", "start", "--screen", "1", "--mic", "Yealink Speaker"));
            Assert.Equal(0, h.Run("record", "--mix", "start", "--mic", "Yeti", "--fps", "15", "--camera", "C920"));
            Assert.Equal(0, h.Run("record", "stop"));

            Assert.Equal(new[] { "record start", "record start", "record start", "record stop" }, h.Calls.Select(c => c.Path));
            Assert.True(h.Calls[0].Opts.Has("audio-only"));
            Assert.Equal("1", h.Calls[1].Opts.Get("screen"));
            Assert.Equal("Yealink Speaker", h.Calls[1].Opts.Get("mic"));
            Assert.True(h.Calls[2].Opts.Has("mix"));
            Assert.Equal("Yeti", h.Calls[2].Opts.Get("mic"));
            Assert.Equal("15", h.Calls[2].Opts.Get("fps"));
            Assert.Empty(h.Calls[2].Opts.Positional);   // the verb is not passed on as an argument
            // The handler's own body builder still reads them exactly as before.
            Assert.Equal("audio", global::AgentEyes.Headless.HeadlessCli.StartBody(h.Calls[0].Opts)["mode"]);
            Assert.Equal("mixed", global::AgentEyes.Headless.HeadlessCli.StartBody(h.Calls[2].Opts)["source"]);
        }

        [Theory]
        [InlineData("--threshold", "-40")]   // a negative number is still an option's value
        [InlineData("--mic", "Yeti")]
        public void CorrectInvocation_AlwaysOnValueFlags_ReachTheHandler(string flag, string value)
        {
            var h = new Harness();

            int code = h.Run("always-on", "--screen", "1", flag, value);

            Assert.Equal(0, code);
            Assert.Single(h.Calls);
            Assert.Equal(value, h.Calls[0].Opts.Get(flag.Substring(2)));
        }

        [Fact]
        public void CorrectInvocation_ImportOfADashLedFileName_PassesItThroughAsThePath()
        {
            var h = new Harness();

            int code = h.Run("import", "-intro.mp4");

            Assert.Equal(0, code);
            Assert.Single(h.Calls);
            Assert.Equal(new[] { "-intro.mp4" }, h.Calls[0].Opts.Positional);
        }

        [Fact]
        public void CorrectInvocation_PackageWithDirAndBooleanFlag_KeepsTheDirPositional()
        {
            var h = new Harness();

            int code = h.Run("package", "--no-frames", "/tmp/rec");

            Assert.Equal(0, code);
            Assert.Single(h.Calls);
            Assert.Equal(new[] { "/tmp/rec" }, h.Calls[0].Opts.Positional);
        }

        // ---- criterion 6: the usage text cannot drift from the parser ---------------------------

        [Theory]
        [MemberData(nameof(EveryCommand))]
        public void Synopsis_EveryCommand_NamesExactlyTheOptionsItAccepts(string path)
        {
            var spec = Spec(path);

            var named = Regex.Matches(spec.Synopsis, @"--([a-z][a-z0-9-]*)")
                .Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            var declared = spec.Options.Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

            Assert.Equal(declared, named);
        }

        [Fact]
        public void CommandTable_HasEveryCommandTheCliDocuments()
        {
            // A PRESENCE check on the table itself, so an empty or truncated table cannot make every
            // theory above vacuously pass.
            var expected = new[]
            {
                "account", "always-on", "compose", "housekeep", "import", "package", "record start",
                "record status", "record stop", "screens", "selftest", "serve", "shot", "signin",
                "signout", "subtitle", "translate", "audio", "video",
            }.OrderBy(p => p, StringComparer.Ordinal);

            Assert.Equal(expected, CliCommandTable.All.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
        }

        /// <summary>
        /// The flags each handler READS (from its compiled IL) are exactly the flags its table entry
        /// DECLARES. Declared-but-unread would be a flag the parser accepts and the command silently
        /// ignores (this issue's defect); read-but-undeclared would be a working flag the parser now
        /// refuses (a regression of a correct invocation).
        ///
        /// What it cannot see, stated honestly: a key that is not a string literal at the call site
        /// (built at run time, or a helper that takes the key as a parameter) is reported as
        /// UNRESOLVED and fails the test rather than being skipped; a CliArgs read inside a lambda or
        /// local function whose closure captures the args is not followed (no handler does that
        /// today); and callees are followed only when they take a CliArgs parameter.
        /// </summary>
        [Fact]
        public void Handlers_ReadExactlyTheFlagsTheirTableEntryDeclares()
        {
            var mismatches = new List<string>();
            int totalReads = 0;
            foreach (var c in CliCommandTable.All)
            {
                var reads = CliArgsIl.KeysRead(c.Run.Method);
                totalReads += reads.Count;
                var declared = c.Options.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var r in reads.Where(r => !declared.Contains(r)))
                    mismatches.Add($"{c.Path}: reads --{r} but does not declare it");
                if (c.AvailableOnThisPlatform)
                {
                    foreach (var d in declared.Where(d => !reads.Contains(d)))
                        mismatches.Add($"{c.Path}: declares --{d} but its handler never reads it");
                }
            }

            Assert.Empty(mismatches);
            // The instrument is live: it found the reads it must find (an empty result is a broken
            // instrument, never a clean run).
            Assert.Equal(
                new[] { "audio-only", "camera", "fps", "loopback", "mic", "mix", "screen" },
                CliArgsIl.KeysRead(Spec("record start").Run.Method).OrderBy(k => k, StringComparer.Ordinal));
            Assert.True(totalReads > 40, $"only {totalReads} flag reads found across every handler");
        }

        /// <summary>
        /// The one-time qa-record -> AgentEyes folder move must run BEFORE the dispatcher, because the
        /// dispatcher logs and Log creates the AgentEyes folder, after which the move skips itself for
        /// good (StorageMigration.Migrate). Read from Program.Main's compiled IL: the order of its calls.
        /// </summary>
        [Fact]
        public void ProgramMain_RunsTheStorageMigrationBeforeTheDispatcher()
        {
            var main = typeof(CliArgs).Assembly.GetType("AgentEyes.Program")!
                .GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;

            var calls = CliArgsIl.CallsInOrder(main);

            int migration = calls.IndexOf("StorageMigration.Run");
            int dispatcher = calls.IndexOf("CliDispatcher.Run");
            Assert.True(migration >= 0, "Main does not call StorageMigration.Run: " + string.Join(", ", calls));
            Assert.True(dispatcher >= 0, "Main does not call CliDispatcher.Run: " + string.Join(", ", calls));
            Assert.True(migration < dispatcher, "Main calls the dispatcher before the migration: " + string.Join(", ", calls));
        }

        /// <summary>A minimal IL reader: the literal keys a method (and the CliArgs-taking methods it calls) passes to CliArgs.</summary>
        private static class CliArgsIl
        {
            private static readonly Dictionary<short, OpCode> OpCodesByValue =
                typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Select(f => (OpCode)f.GetValue(null)!)
                    .ToDictionary(o => o.Value);

            private static readonly string[] ReadMethods = { "Has", "Get", "Require", "RequireInt" };

            /// <summary>"Type.Method" of every call/callvirt/newobj in <paramref name="method"/>, in IL order.</summary>
            public static List<string> CallsInOrder(MethodInfo method)
            {
                var calls = new List<string>();
                byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
                int pos = 0;
                while (pos < il.Length)
                {
                    short value = il[pos] == 0xFE ? (short)(0xFE00 | il[pos + 1]) : il[pos];
                    pos += il[pos] == 0xFE ? 2 : 1;
                    var op = OpCodesByValue[value];
                    int operandStart = pos;
                    pos += OperandSize(op.OperandType, il, pos);
                    if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                    {
                        var callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, operandStart));
                        if (callee != null) calls.Add($"{callee.DeclaringType?.Name}.{callee.Name}");
                    }
                }
                return calls;
            }

            public static HashSet<string> KeysRead(MethodInfo root)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<MethodBase>();
                var queue = new Queue<MethodBase>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    var m = queue.Dequeue();
                    if (!seen.Add(m)) continue;
                    Scan(m, keys, queue);
                }
                return keys;
            }

            private static void Scan(MethodBase method, HashSet<string> keys, Queue<MethodBase> queue)
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return;
                var module = method.Module;
                Type[]? typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
                Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                var stringsSinceCall = new List<string>();
                int pos = 0;
                while (pos < il.Length)
                {
                    short value = il[pos] == 0xFE ? (short)(0xFE00 | il[pos + 1]) : il[pos];
                    pos += il[pos] == 0xFE ? 2 : 1;
                    var op = OpCodesByValue[value];
                    int operandStart = pos;
                    pos += OperandSize(op.OperandType, il, pos);

                    if (op == OpCodes.Ldstr)
                    {
                        stringsSinceCall.Add(module.ResolveString(BitConverter.ToInt32(il, operandStart)));
                    }
                    else if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                    {
                        var callee = module.ResolveMethod(BitConverter.ToInt32(il, operandStart), typeArgs, methodArgs);
                        if (callee != null && callee.DeclaringType == typeof(CliArgs) && ReadMethods.Contains(callee.Name))
                        {
                            // The key is the first of the callee's string arguments, all of which
                            // are the last literals loaded (Has/Get take the key; Require/RequireInt
                            // take the key and a hint). Anything else is unresolved, and fails.
                            int argCount = callee.GetParameters().Length;
                            if (stringsSinceCall.Count < argCount)
                                keys.Add($"UNRESOLVED({method.DeclaringType?.Name}.{method.Name})");
                            else
                                keys.Add(stringsSinceCall[stringsSinceCall.Count - argCount]);
                        }
                        else if (callee != null && callee.Module == typeof(CliArgs).Module
                                 && callee.GetParameters().Any(p => p.ParameterType == typeof(CliArgs)))
                        {
                            queue.Enqueue(callee);
                        }
                        stringsSinceCall.Clear();
                    }
                }
            }

            private static int OperandSize(OperandType t, byte[] il, int pos) => t switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
                _ => 4,
            };
        }
    }
}
