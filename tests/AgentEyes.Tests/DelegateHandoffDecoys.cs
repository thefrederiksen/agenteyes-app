using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AgentEyes.Tests.HandoffDecoys
{
    /// <summary>
    /// Negative controls for the delegate-handoff rules in <c>CompiledCode.Reachable</c> (issue #75).
    ///
    /// Issue #75 taught the reachability walk that a delegate handed straight to <c>new Thread(...)</c>
    /// runs on that new thread, and that a delegate parked in a private field runs wherever the
    /// field is READ. Both rules REMOVE edges, so both could weaken the HUD and preview guards that
    /// stand on the walk. This file proves they did not: every REAL defect shape below must still be
    /// reported by the same scan the product guards run, and the product's own worker shapes -
    /// copied here member for member - must not be.
    ///
    /// Nothing here is ever called. These methods exist to be READ - as IL by CompiledCode.
    /// </summary>
    internal static class HandoffDecoyNote
    {
        public const string Why =
            "Negative controls for DelegateHandoffWalkTests. Never called; read as IL.";
    }

    // ---- the HUD side: a file write on the UI thread -------------------------------------

    /// <summary>The seeds, one per click shape. Each stands in for a HUD click handler.</summary>
    internal static class HudDecoy
    {
        /// <summary>REAL: touching a type runs its static constructor on THIS thread, and that one
        /// writes a file.</summary>
        public static string ClickTouchesAWritingTypeInitializer() => CctorWritingSettings.Value;

        /// <summary>REAL: the delegate is parked in a private field, and this click's path READS
        /// the field and invokes it, on this thread.</summary>
        public static void ClickInvokesAParkedDelegate() => new ParkedAndInvokedHere().Save("x");

        /// <summary>REAL: the delegate goes to an external invoker that runs it synchronously on
        /// this thread. The walk cannot see inside List.ForEach, so the builder keeps its edge.</summary>
        public static void ClickHandsADelegateToASameThreadInvoker() =>
            new List<string> { "a", "b" }.ForEach(WriteOne);

        /// <summary>REAL: the constructor parks the delegate AND invokes the parameter itself, so
        /// the parameter is not purely parked and the builder keeps its edge.</summary>
        public static void ClickBuildsAWriterWhoseCtorAlsoInvokesIt() =>
            _ = new CtorAlsoInvokesItsParameter(WriteTwo);

        /// <summary>NARROWNESS: the product's Config shape - a static writer built in the type
        /// initializer over a method group, its thread started elsewhere. Queuing must not be
        /// reported: the write runs only on the writer's own thread.</summary>
        public static void ClickQueuesThroughTheBackgroundWriter() => DecoyConfig.SaveQueued("{}");

        private static void WriteOne(string text) => File.WriteAllText("decoy-one.txt", text);

        private static void WriteTwo(string path, string text) => File.WriteAllText(path, text);
    }

    internal static class CctorWritingSettings
    {
        public static readonly string Value = Load();

        private static string Load()
        {
            File.WriteAllText("decoy-settings.json", "{}");
            return "{}";
        }
    }

    internal sealed class ParkedAndInvokedHere
    {
        private readonly Action<string, string> _write;

        public ParkedAndInvokedHere(Action<string, string>? write = null) => _write = write ?? WriteToDisk;

        public void Save(string text) => _write("decoy-parked.txt", text);

        private static void WriteToDisk(string path, string text) => File.WriteAllText(path, text);
    }

    internal sealed class CtorAlsoInvokesItsParameter
    {
        private readonly Action<string, string> _write;

        public CtorAlsoInvokesItsParameter(Action<string, string> write)
        {
            _write = write;
            write("decoy-ctor.txt", "now");
        }

        public void Again() => _write("decoy-ctor.txt", "again");
    }

    /// <summary>The product's Config: a writer built in the type initializer over WriteJson.</summary>
    internal static class DecoyConfig
    {
        private static readonly DecoyBackgroundWriter Writer = new("decoy-config.json", WriteJson);

        public static void SaveQueued(string json) => Writer.Queue(json);

        public static void Load() => Writer.Start();

        private static void WriteJson(string path, string json) => File.WriteAllText(path, json);
    }

    /// <summary>The product's BackgroundFileWriter, member for member where it matters: the
    /// `write ?? WriteToDisk` park, the thread started from Start(), the parked delegate read only
    /// by the loop.</summary>
    internal sealed class DecoyBackgroundWriter
    {
        private readonly string _path;
        private readonly Action<string, string> _write;
        private readonly AutoResetEvent _work = new(false);
        private string? _pending;
        private Thread? _thread;

        public DecoyBackgroundWriter(string path, Action<string, string>? write = null)
        {
            _path = path;
            _write = write ?? WriteToDisk;
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public void Queue(string text)
        {
            Interlocked.Exchange(ref _pending, text);
            _work.Set();
        }

        private void Loop()
        {
            while (true)
            {
                _work.WaitOne(250);
                var text = Interlocked.Exchange(ref _pending, null);
                if (text != null) _write(_path, text);
            }
        }

        private static void WriteToDisk(string path, string text) => File.WriteAllText(path, text);
    }

    // ---- the drain side: the shared logger on a recording's thread ------------------------

    /// <summary>The seeds, one per drain shape. Each stands in for PreviewTap.Drain.</summary>
    internal static class DrainDecoy
    {
        /// <summary>REAL: the drain calls the shared logger outright.</summary>
        public static void DrainLogsDirectly() => Log.Error("decoy: the drain logged synchronously");

        /// <summary>REAL: touching a type whose static constructor logs, on THIS thread.</summary>
        public static int DrainTouchesALoggingTypeInitializer() => CctorLogging.Count;

        /// <summary>REAL: the drain reads a parked delegate and invokes it on this thread, and the
        /// parked method is the shared logger's caller.</summary>
        public static void DrainInvokesAParkedLogger() => new ParkedLogger().Say("decoy");

        /// <summary>NARROWNESS: the product's PreviewLog shape - the appender thread started in the
        /// type initializer, SAYING a line is an enqueue. Must not be reported.</summary>
        public static void DrainSaysThroughTheLogLane() => DecoyPreviewLog.Info("decoy line");

        /// <summary>NARROWNESS: the product's PreviewChores shape - a shared instance built in the
        /// type initializer, `perform ?? Carry` parked, worker thread started in the constructor.
        /// Handing it a chore must not be reported.</summary>
        public static void DrainHandsAChoreToTheWorker() => DecoyChores.Prepare("decoy-frame.jpg");
    }

    internal static class CctorLogging
    {
        public static readonly int Count = Announce();

        private static int Announce()
        {
            Log.Warn("decoy: a type initializer logged synchronously");
            return 1;
        }
    }

    internal sealed class ParkedLogger
    {
        private readonly Action<string> _say;

        public ParkedLogger(Action<string>? say = null) => _say = say ?? SayNow;

        public void Say(string message) => _say(message);

        private static void SayNow(string message) => Log.Info(message);
    }

    /// <summary>The product's PreviewLog, member for member where it matters.</summary>
    internal static class DecoyPreviewLog
    {
        private static readonly ConcurrentQueue<string> Lines = new();
        private static readonly AutoResetEvent Work = new(false);
        private static readonly Thread Appender = StartAppender();

        public static bool Alive => Appender.IsAlive;

        public static void Info(string message)
        {
            Lines.Enqueue(message);
            Work.Set();
        }

        private static Thread StartAppender()
        {
            var thread = new Thread(Loop) { IsBackground = true };
            thread.Start();
            return thread;
        }

        private static void Loop()
        {
            while (true)
            {
                Work.WaitOne(250);
                while (Lines.TryDequeue(out var line)) Log.Info(line);
            }
        }
    }

    /// <summary>The product's PreviewChores, member for member where it matters.</summary>
    internal sealed class DecoyChores
    {
        private static readonly DecoyChores Shared = new();

        private readonly ConcurrentQueue<string> _queue = new();
        private readonly AutoResetEvent _work = new(false);
        private readonly Action<int, string> _perform;

        internal DecoyChores(Action<int, string>? perform = null)
        {
            _perform = perform ?? Carry;
            var worker = new Thread(Loop) { IsBackground = true };
            worker.Start();
        }

        public static bool Prepare(string framePath) => Shared.Run(framePath);

        private bool Run(string framePath)
        {
            _queue.Enqueue(framePath);
            _work.Set();
            return true;
        }

        private void Loop()
        {
            while (true)
            {
                _work.WaitOne(250);
                while (_queue.TryDequeue(out var path)) _perform(0, path);
            }
        }

        private static void Carry(int kind, string framePath)
        {
            string? dir = Path.GetDirectoryName(framePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(framePath)) File.Delete(framePath);
        }
    }
}
