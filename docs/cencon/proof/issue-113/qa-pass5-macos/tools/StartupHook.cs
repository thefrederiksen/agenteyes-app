// QA #113 macOS - startup hook loaded into the INSTALLED agenteyes apphost by a one-shot launchd job,
// so every child it starts (ffmpeg) is attributed to the binary holding the Screen Recording grant.
// It loads QA_HARNESS (a build of the branch under test) in its OWN AssemblyLoadContext, so the
// installed agenteyes.dll is never used, runs its Main, then exits. Changes no installed file.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
internal class StartupHook
{
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern bool CGPreflightScreenCaptureAccess();
    private sealed class Ctx : AssemblyLoadContext
    {
        private readonly string _dir;
        public Ctx(string dir) : base("qa113", false) { _dir = dir; }
        protected override Assembly Load(AssemblyName n)
        {
            string p = Path.Combine(_dir, n.Name + ".dll");
            return File.Exists(p) ? LoadFromAssemblyPath(p) : null;
        }
    }
    public static void Initialize()
    {
        string harness = Environment.GetEnvironmentVariable("QA_HARNESS");
        string[] args = (Environment.GetEnvironmentVariable("QA_ARGS") ?? "").Split('|');
        Console.WriteLine($"[hook] pid={Environment.ProcessId} exe={Environment.ProcessPath} screenGrant={CGPreflightScreenCaptureAccess()}");
        int rc = 99;
        try
        {
            if (string.IsNullOrEmpty(harness)) { Console.WriteLine("[hook] QA_HARNESS unset - probe only"); rc = 0; }
            else
            {
                var ctx = new Ctx(Path.GetDirectoryName(harness));
                var asm = ctx.LoadFromAssemblyPath(harness);
                Console.WriteLine($"[hook] loaded {asm.FullName} from {asm.Location}");
                var r = asm.EntryPoint.Invoke(null, new object[] { args });
                rc = r is int i ? i : 0;
            }
        }
        catch (Exception ex) { Console.WriteLine("[hook] FAILED: " + ex); }
        Console.WriteLine($"[hook] Environment.Exit({rc}) - the installed Main is never reached");
        Console.Out.Flush();
        Environment.Exit(rc);
    }
}
