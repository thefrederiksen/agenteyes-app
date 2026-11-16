// QA #161: run the TestIsolationTests guard's OWN scanner (CompiledCode.cs, linked unchanged) over one
// compiled agenteyes.dll and print the three inventories its pins compare against.
using System; using System.Linq; using AgentEyes.Tests;
namespace AgentEyes { internal class Manifest { } }
namespace AgentEyes.Setup.Engine { internal class ReleaseSource { } }
static class P {
  static int Main(string[] a) {
    string dll = a[0];
    Console.WriteLine("== FolderLookups"); Console.WriteLine(CompiledCode.Describe(CompiledCode.FolderLookups(dll)));
    Console.WriteLine("== EnvironmentVariableReads"); Console.WriteLine(CompiledCode.Describe(CompiledCode.EnvironmentVariableReads(dll)));
    Console.WriteLine("== MachineLocalAppData readers"); Console.WriteLine(CompiledCode.Describe(CompiledCode.CallSites(dll, c => c == "AgentEyes.AppDataPaths::get_MachineLocalAppData")));
    return 0; } }
