using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// Minimal append-only logger shared by the engine, CLI, and GUI.
    /// Writes to %LOCALAPPDATA%\AgentEyes\logs\AgentEyes-YYYYMMDD.log (via <see cref="AppDataPaths"/>,
    /// so a test host writes its own per-run log instead - issue #78). ASCII only.
    ///
    /// Every line carries the writing process's id: the app, the CLI and a second instance can all
    /// append to one day's file, and without the pid their lines are indistinguishable.
    /// Line shape: <c>HH:mm:ss.fff [pid 1234] [INFO] message</c>.
    /// </summary>
    internal static class Log
    {
        private static readonly object Lock = new();

        private static readonly int ProcessId = Environment.ProcessId;

        public static string Dir => Path.Combine(AppDataPaths.Root, "logs");

        public static string CurrentFile => Path.Combine(Dir, $"AgentEyes-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

        /// <summary>One log line, without the newline. Pure.</summary>
        internal static string FormatLine(DateTime at, int processId, string level, string message) =>
            $"{at:HH:mm:ss.fff} [pid {processId}] [{level}] {message}";

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(CurrentFile,
                        FormatLine(DateTime.Now, ProcessId, level, message) + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
