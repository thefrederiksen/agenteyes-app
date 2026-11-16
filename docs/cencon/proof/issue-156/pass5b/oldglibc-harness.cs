// #156 pass 5b: the REAL Linux owner read and the REAL EPERM row of the ownership predicate, loaded by
// reflection from whichever agenteyes.dll is given, so the before and after builds run unmodified.
using System.Reflection;
using System.Runtime.InteropServices;
static class P
{
    [DllImport("libc")] static extern uint getuid();
    [DllImport("libc")] static extern uint geteuid();
    static int Main(string[] a)
    {
        var asm = Assembly.LoadFrom(a[0]);
        int rootPid = int.Parse(a[1]);
        Console.WriteLine($"product {asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        Console.WriteLine($"libc: {RuntimeInformation.OSDescription}; gnu_get_libc_version {Marshal.PtrToStringAnsi(GnuVer())}; runtime {RuntimeInformation.FrameworkDescription}; uid {getuid()} euid {geteuid()}");
        var reg = asm.GetType("AgentEyes.Video.FfmpegChildRegistry", true);
        var read = reg.GetMethod("ReadLinuxProcOwner", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(int) });
        var classify = reg.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "ClassifyUnreadable" && m.GetParameters().Length == 9);
        var ownerFunc = Delegate.CreateDelegate(classify.GetParameters()[8].ParameterType, read);
        Func<int, (int, int)> eperm = _ => (-1, 1);   // kill(pid, 0) answering EPERM, as the seccomp prober does
        uint ruid = getuid(), euid = geteuid(); Func<uint, bool> ours = u => u == ruid || u == euid;
        foreach (var (name, pid) in new[] { ("this process", Environment.ProcessId), ($"a root process (pid {rootPid})", rootPid), ("a missing pid", int.MaxValue - 7) })
        {
            try { Console.WriteLine($"  ReadLinuxProcOwner({name}): {read.Invoke(null, new object[] { pid })}"); }
            catch (TargetInvocationException ex) { Console.WriteLine($"  ReadLinuxProcOwner({name}): THREW {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}"); }
            try
            {
                var ans = classify.Invoke(null, new object[] { pid, 13, "t", false, false, eperm, null, ours, ownerFunc });
                Console.WriteLine($"  predicate, EPERM row ({name}): {ans}");
            }
            catch (TargetInvocationException ex) { Console.WriteLine($"  predicate, EPERM row ({name}): THREW {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}"); }
        }
        return 0;
    }
    [DllImport("libc", EntryPoint = "gnu_get_libc_version")] static extern IntPtr GnuVer();
}
