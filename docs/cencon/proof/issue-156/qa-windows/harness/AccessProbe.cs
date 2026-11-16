using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

// Windows probe for #156 pass 3 QA. For each pid: which OpenProcess rights succeed, what .NET's
// HasExited/StartTime do, and the target's owner + integrity level when a token can be opened.
static class P
{
    const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
    const uint TOKEN_QUERY = 0x0008;
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
    [DllImport("advapi32", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint a, out IntPtr t);
    [DllImport("advapi32", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int cls, IntPtr buf, int len, out int ret);
    [DllImport("advapi32", SetLastError = true)] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
    [DllImport("advapi32", SetLastError = true)] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    static string TryOpen(int pid, uint access, out IntPtr h)
    {
        h = OpenProcess(access, false, pid);
        return h == IntPtr.Zero ? "ERR" + Marshal.GetLastWin32Error() : "ok";
    }

    static string Owner(int pid, out string il)
    {
        il = "?";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "?(open " + Marshal.GetLastWin32Error() + ")";
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out IntPtr t)) return "?(token " + Marshal.GetLastWin32Error() + ")";
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(1024);
                try
                {
                    string user = "?";
                    if (GetTokenInformation(t, 1 /*TokenUser*/, buf, 1024, out _))
                    {
                        var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buf));
                        try { user = sid.Translate(typeof(NTAccount)).Value; } catch { user = sid.Value; }
                    }
                    if (GetTokenInformation(t, 25 /*TokenIntegrityLevel*/, buf, 1024, out _))
                    {
                        IntPtr sid = Marshal.ReadIntPtr(buf);
                        int n = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                        int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(n - 1)));
                        il = rid switch { 0x1000 => "Low", 0x2000 => "Medium", 0x2100 => "Medium+", 0x3000 => "High", 0x4000 => "System", 0x5000 => "Protected", _ => "0x" + rid.ToString("x") };
                    }
                    return user;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(t); }
        }
        finally { CloseHandle(h); }
    }

    static string Row(int pid)
    {
        string name = "?";
        string hasExited, startTime;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try { hasExited = p.HasExited.ToString(); } catch (Exception ex) { hasExited = ex.GetType().Name + (ex is Win32Exception w ? "(" + w.NativeErrorCode + ")" : ""); }
            try { startTime = p.StartTime.ToUniversalTime().ToString("o"); } catch (Exception ex) { startTime = ex.GetType().Name + (ex is Win32Exception w ? "(" + w.NativeErrorCode + ")" : ""); }
        }
        catch (Exception ex) { hasExited = startTime = "GetProcessById:" + ex.GetType().Name; }
        string qi = TryOpen(pid, PROCESS_QUERY_INFORMATION, out IntPtr h1); if (h1 != IntPtr.Zero) CloseHandle(h1);
        string ql = TryOpen(pid, PROCESS_QUERY_LIMITED_INFORMATION, out IntPtr h2);
        string gpt = "-";
        if (h2 != IntPtr.Zero) { gpt = GetProcessTimes(h2, out long c, out _, out _, out _) ? DateTime.FromFileTimeUtc(c).ToString("o") : "ERR" + Marshal.GetLastWin32Error(); CloseHandle(h2); }
        string qls = TryOpen(pid, PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, out IntPtr h3); if (h3 != IntPtr.Zero) CloseHandle(h3);
        string owner = Owner(pid, out string il);
        return $"pid={pid,-6} name={name,-28} owner={owner,-28} IL={il,-9} QUERY_INFORMATION={qi,-6} QUERY_LIMITED={ql,-6} LIMITED|SYNC={qls,-6} GetProcessTimes(limited)={gpt,-34} .NET HasExited={hasExited,-22} .NET StartTime={startTime}";
    }

    static int Main(string[] args)
    {
        using (var me = Process.GetCurrentProcess())
        {
            Owner(me.Id, out string myIl);
            Console.WriteLine($"probe pid={me.Id} user={WindowsIdentity.GetCurrent().Name} IL={myIl} elevated={new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)} runtime={RuntimeInformation.FrameworkDescription}");
        }
        if (args.Length > 0 && args[0] == "--all")
        {
            foreach (var p in Process.GetProcesses()) { int id = p.Id; p.Dispose(); Console.WriteLine(Row(id)); }
            return 0;
        }
        foreach (var a in args) Console.WriteLine(Row(int.Parse(a)));
        return 0;
    }
}
