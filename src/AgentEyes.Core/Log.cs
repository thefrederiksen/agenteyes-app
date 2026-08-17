using System;
using System.IO;

namespace AgentEyes
{
    /// <summary>
    /// Minimal append-only logger shared by the engine, CLI, and GUI.
    /// Writes to %LOCALAPPDATA%\AgentEyes\logs\AgentEyes-YYYYMMDD.log. ASCII only.
    /// </summary>
    internal static class Log
    {
        private static readonly object Lock = new();

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEyes", "logs");

        public static string CurrentFile => Path.Combine(Dir, $"AgentEyes-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(CurrentFile,
                        $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
