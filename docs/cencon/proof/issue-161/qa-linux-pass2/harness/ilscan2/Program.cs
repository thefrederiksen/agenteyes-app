// QA #161 pass 2: the #155 IL guard's OWN scanner (tests/AgentEyes.Tests/CompiledCode.cs, linked unchanged) over one
// compiled agenteyes.dll: the native-import and file-write inventories EveryNativeImport_IsPinned /
// EveryFileWriteInTheProduct_IsAPinnedCallSite compare against their pins.
using System; using System.Linq; using AgentEyes.Tests;
namespace AgentEyes { internal class Manifest { } }
namespace AgentEyes.Setup.Engine { internal class ReleaseSource { } }
static class P {
  static int Main(string[] a) {
    foreach (var l in CompiledCode.NativeImports(a[0]).Distinct().OrderBy(x => x, StringComparer.Ordinal)) Console.WriteLine("IMPORT " + l);
    foreach (var l in CompiledCode.Describe(CompiledCode.FileWrites(new[] { a[0] })).Split('\n')) Console.WriteLine("WRITE " + l.Trim());
    return 0; } }
