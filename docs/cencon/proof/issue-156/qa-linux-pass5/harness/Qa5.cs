// QA #156 pass 5/5b (Linux seat): does the statx path read stx_uid and not stx_gid?
//   uidgid <pid-with-euid!=egid> [rootPidWithGid]   all reads REAL; only kill(2)'s EPERM is injected in arm 4
using System.Reflection;
using System.Runtime.InteropServices;
using AgentEyes.Video;
static class Qa5
{
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] static extern int Statx(int dirfd, string path, int flags, uint mask, byte[] buf);
    [DllImport("libc")] static extern uint getuid();
    [DllImport("libc")] static extern uint geteuid();
    static string StatC(string path) { var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/stat", $"-c %u:%g {path}") { RedirectStandardOutput = true }); string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return o; }
    // total <pid>... : the real owner read and the real EPERM row, each call caught and PRINTED (a throw is the finding)
    public static int Total(string[] a)
    {
        Console.WriteLine($"product {typeof(FfmpegChildRegistry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion}; {RuntimeInformation.OSDescription}; glibc {Marshal.PtrToStringAnsi(GnuVer())}; runtime {RuntimeInformation.FrameworkDescription}; uid {getuid()} euid {geteuid()}");
        uint r = getuid(), e = geteuid();
        Func<int, (int, int)> eperm = _ => (-1, 1);
        foreach (var s in a.Skip(1))
        {
            int pid = int.Parse(s);
            try { Console.WriteLine($"  ReadLinuxProcOwner({pid}): {FfmpegChildRegistry.ReadLinuxProcOwner(pid)}"); }
            catch (Exception ex) { Console.WriteLine($"  ReadLinuxProcOwner({pid}): THREW {ex.GetType().FullName}: {ex.Message}"); }
            try { Console.WriteLine($"  predicate EPERM row({pid}): {FfmpegChildRegistry.ClassifyUnreadable(pid, 13, "t", false, false, eperm, _ => default, u => u == r || u == e, FfmpegChildRegistry.ReadLinuxProcOwner)}"); }
            catch (Exception ex) { Console.WriteLine($"  predicate EPERM row({pid}): THREW {ex.GetType().FullName}: {ex.Message}"); }
        }
        return 0;
    }
    [DllImport("libc", EntryPoint = "gnu_get_libc_version")] static extern IntPtr GnuVer();
    public static int UidGid(string[] a)
    {
        int pid = int.Parse(a[1]);
        Console.WriteLine($"product {typeof(FfmpegChildRegistry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion}; prober uid {getuid()} euid {geteuid()}");
        // 1. Independent raw statx (my own P/Invoke, same flags/mask as the product) - the layout itself
        foreach (var path in new[] { "/etc/shadow", $"/proc/{pid}" })
        {
            var buf = new byte[256];
            int rc = Statx(-100, path, 0, 0x8 | 0x10, buf);
            Console.WriteLine($"  [raw statx] {path}: rc {rc}, stx_mask 0x{BitConverter.ToUInt32(buf, 0):x}, u32@20 (stx_uid) = {BitConverter.ToUInt32(buf, 20)}, u32@24 (stx_gid) = {BitConverter.ToUInt32(buf, 24)}; stat(1) says uid:gid {StatC(path)}");
        }
        // 2. The PRODUCT's statx path (ReadLinuxProcOwner(pid), which tries statx first)
        var own = FfmpegChildRegistry.ReadLinuxProcOwner(pid);
        Console.WriteLine($"  [product ReadLinuxProcOwner({pid})] {own}");
        // 3. The PRODUCT's stat fallback path on the same pid and on /etc/shadow
        Console.WriteLine($"  [product StatOwnerOfPath(/proc/{pid})] {FfmpegChildRegistry.StatOwnerOfPath($"/proc/{pid}")}");
        Console.WriteLine($"  [product StatOwnerOfPath(/etc/shadow)] {FfmpegChildRegistry.StatOwnerOfPath("/etc/shadow")}");
        // 4. The PRODUCT predicate's Linux EPERM row with the REAL owner reader and the real own uids
        uint r = getuid(), e = geteuid();
        Func<int, (int, int)> eperm = _ => (-1, 1);
        var ans = FfmpegChildRegistry.ClassifyUnreadable(pid, 13, "t", false, false, eperm, _ => default, u => u == r || u == e, FfmpegChildRegistry.ReadLinuxProcOwner);
        Console.WriteLine($"  [product predicate, EPERM row, real owner read] {ans}");
        if (a.Length > 2)
        {
            int rp = int.Parse(a[2]);
            Console.WriteLine($"  [product ReadLinuxProcOwner({rp})] {FfmpegChildRegistry.ReadLinuxProcOwner(rp)}; stat(1) says {StatC($"/proc/{rp}")}");
            Console.WriteLine($"  [product predicate, EPERM row, pid {rp}] {FfmpegChildRegistry.ClassifyUnreadable(rp, 13, "t", false, false, eperm, _ => default, u => u == r || u == e, FfmpegChildRegistry.ReadLinuxProcOwner)}");
        }
        return 0;
    }
}
