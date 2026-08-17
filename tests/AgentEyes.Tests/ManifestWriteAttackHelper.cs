using System.IO;

namespace AgentEyes.Tests
{
    /// <summary>
    /// NEGATIVE CONTROL for issue #155, criterion 4 - bypass shape (b), exactly as the QA Agent wrote
    /// it in round 2: the write is one hop away, in a NEW FILE that never names manifest.json, so no
    /// scan of source TEXT can connect it to the manifest.
    ///
    /// It is never called. It exists to be COMPILED, so that <see cref="ManifestWriterIlTests"/> can
    /// run the real guard over real IL and prove the guard reports it. Nothing in the product calls
    /// into this type, and nothing here touches a real recording.
    /// </summary>
    internal static class ManifestWriteAttackHelper
    {
        /// <summary>The one-line helper. A source scan sees a file that never mentions the manifest;
        /// the IL scan sees a method containing System.IO.File::WriteAllText, which is the fact that
        /// matters.</summary>
        internal static void WriteText(string path, string text) => File.WriteAllText(path, text);
    }
}
