using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

// Which OpenProcess access mask does System.Diagnostics.Process (the runtime this probe runs on) pass
// on the StartTime and HasExited paths? Prints every ldc.i4 constant passed in the IL of the methods
// involved, with the location of the assembly read.
static class I
{
    static void Dump(MethodBase m)
    {
        byte[] il = m.GetMethodBody()?.GetILAsByteArray();
        if (il == null) { Console.WriteLine($"  {m.DeclaringType.Name}.{m.Name}: no body"); return; }
        var consts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < il.Length; i++)
        {
            if (il[i] == 0x20 && i + 4 < il.Length) consts.Add("0x" + BitConverter.ToInt32(il, i + 1).ToString("x"));   // ldc.i4 <int32>
            if (il[i] == 0x28 && i + 4 < il.Length)                                                                       // call <token>
            {
                try { var callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); consts.Add("call " + callee.DeclaringType.Name + "." + callee.Name); } catch { }
            }
        }
        Console.WriteLine($"  {m.DeclaringType.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}): {string.Join("  ", consts)}");
    }

    static void Main()
    {
        var t = typeof(Process);
        Console.WriteLine($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {t.Assembly.Location}");
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var name in new[] { "get_StartTime", "GetProcessTimes", "get_HasExited", "GetProcessHandle", "get_ExitTime", "UpdateHasExited", "get_StartTimeCore" })
            foreach (var m in t.GetMethods(all).Where(x => x.Name == name)) Dump(m);
        Console.WriteLine("  (0x1000 = PROCESS_QUERY_LIMITED_INFORMATION, 0x400 = PROCESS_QUERY_INFORMATION, 0x100000 = SYNCHRONIZE, 0x101000 = both limited|sync)");
    }
}
