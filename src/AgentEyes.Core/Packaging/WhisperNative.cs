using System;
using System.IO;
using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// Makes Whisper.net's native library resolvable in single-file published builds.
    ///
    /// Whisper.net probes runtimes/win-{arch}/ under AppContext.BaseDirectory and the
    /// executable directory. In a PublishSingleFile build those point at the install
    /// directory, but the native payload is extracted to the bundle extraction
    /// directory (%TEMP%\.net\&lt;app&gt;\&lt;hash&gt;\runtimes\win-{arch}\), which Whisper.net
    /// never probes - so every installed build failed transcription with
    /// [native library not found] while dev (framework-dependent) builds worked.
    ///
    /// The .NET host publishes the extraction directory in the AppContext data
    /// NATIVE_DLL_SEARCH_DIRECTORIES; point RuntimeOptions.LibraryPath there BEFORE
    /// the first WhisperFactory is created. Call EnsureLoadable() in front of every
    /// WhisperFactory.FromPath.
    /// </summary>
    internal static class WhisperNative
    {
        private static bool _configured;

        public static void EnsureLoadable()
        {
            if (_configured) return;
            _configured = true;

            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string sub = Path.Combine("runtimes", $"win-{arch}");

            // Normal (framework-dependent) layout: runtimes/ sits next to the binaries
            // and Whisper.net's default probing finds it. Nothing to do.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, sub, "whisper.dll")))
            {
                return;
            }

            string dirs = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "";
            foreach (string dir in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(dir, sub, "whisper.dll")))
                {
                    // The loader takes Path.GetDirectoryName(LibraryPath) as the base
                    // directory and appends runtimes/win-{arch} itself, so hand it a
                    // child path of the extraction root rather than the root.
                    RuntimeOptions.LibraryPath = Path.Combine(dir, "runtimes");
                    return;
                }
            }

            throw new InvalidOperationException(
                $"whisper.dll not found: neither {Path.Combine(AppContext.BaseDirectory, sub)} nor any " +
                "NATIVE_DLL_SEARCH_DIRECTORIES entry contains it. The Whisper.net.Runtime native payload " +
                "is missing from this build.");
        }
    }
}
