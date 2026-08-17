using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AgentEyes.App
{
    /// <summary>
    /// Keep the UI responsive. The rule (see docs/design/responsiveness.md): NEVER do file
    /// I/O, ffmpeg/Process, JSON-of-many-files, or other slow/CPU work on the WPF UI thread -
    /// hand it to <see cref="Run"/> (a worker thread) and marshal results back to the UI with
    /// <see cref="Post"/>. This is the cc-director SynchronizationContext/Dispatcher pattern,
    /// wrapped so every call site reads the same.
    /// </summary>
    internal static class Ui
    {
        private static Dispatcher D => Application.Current.Dispatcher;

        /// <summary>Run <paramref name="action"/> on the UI thread (call from a worker thread).</summary>
        public static void Post(Action action) => D.BeginInvoke(action);

        /// <summary>Run blocking work on the thread pool. Await it, then touch the UI in the continuation.</summary>
        public static Task Run(Action work) => Task.Run(work);

        /// <summary>Run blocking work on the thread pool and return its result. Await on the UI thread.</summary>
        public static Task<T> Run<T>(Func<T> work) => Task.Run(work);

        /// <summary>Fire-and-forget background work, then update the UI on completion. Errors are logged.</summary>
        public static void RunThenPost(Action work, Action onDone)
        {
            _ = Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex) { AgentEyes.Log.Error("background work", ex); }
                finally { Post(onDone); }
            });
        }
    }
}
