using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Reads the repository's own SOURCE, for the handful of facts that are source facts rather than
    /// runtime ones - "which file calls this", "is this trigger still wired here".
    ///
    /// Wiring inside the WPF app is otherwise hard to reach from a test, which is exactly how the same
    /// defect shipped three times (issues #141, #142, #151). The repo root is stamped into the
    /// assembly by the .csproj, so a scan can never silently look at nothing. StopPathTests carries
    /// its own copy of this for issue #151.
    ///
    /// For "what does the code DO" rather than "what does it SAY", use <see cref="CompiledCode"/>,
    /// which reads the built assemblies' IL - including AgentEyesApp's, which the test project now
    /// references purely so it is built and available to read (issue #155).
    /// </summary>
    internal static class RepoSource
    {
        /// <summary>The repo root, stamped in at build time by the .csproj.</summary>
        public static string Root
        {
            get
            {
                string? root = typeof(RepoSource).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "The RepoRoot assembly metadata is missing - add it to AgentEyes.Tests.csproj.");
                if (!Directory.Exists(Path.Combine(root, "src")))
                    throw new InvalidOperationException($"No src directory under the stamped repo root '{root}'.");
                return root;
            }
        }

        /// <summary>The text of one repo file. Throws when it is gone, so a rename cannot turn an
        /// assertion into a check that passes by finding nothing.</summary>
        public static string Read(string relativePath)
        {
            string full = Path.Combine(Root, relativePath);
            if (!File.Exists(full)) throw new FileNotFoundException("Expected source file is missing", full);
            return File.ReadAllText(full);
        }

        /// <summary>The text of one method, from its signature to its closing brace, so an assertion
        /// about that method cannot be answered by code somewhere else in the file.</summary>
        public static string MethodBody(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException($"'{signature}' is not in this file any more.");

            int open = text.IndexOf('{', start);
            if (open < 0) throw new InvalidOperationException($"'{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException($"'{signature}' body is unbalanced.");
        }
    }
}
