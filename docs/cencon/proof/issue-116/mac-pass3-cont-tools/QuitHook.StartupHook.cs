// QA #116 pass 3 (continuation seat) - a .NET startup hook for the DESKTOP APP built from the branch
// (never the installed recorder). It changes no product code. It waits for a trigger file, then, ON
// THE UI THREAD, lists the tray icon's NativeMenu and raises the click of the "Quit AgentEyes" item
// through INativeMenuItemExporterEventsImplBridge.RaiseClicked() - the managed entry point that
// Avalonia.Native's menu action callback calls when a person picks the item.
// WHAT THIS DOES NOT EXERCISE: the AppKit side - the status item, NSMenu tracking, the mouse click.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal class StartupHook
{
    public static void Initialize()
    {
        string outDir = Environment.GetEnvironmentVariable("QA_OUT") ?? "/tmp";
        string trigger = Path.Combine(outDir, "quit.trigger");
        var w = new StreamWriter(Path.Combine(outDir, $"quit-hook-{Environment.ProcessId}.txt"), append: false) { AutoFlush = true };
        void Say(string s) { lock (w) w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [quit-hook pid {Environment.ProcessId}] {s}"); }
        Say($"loaded into {Environment.ProcessPath}; waiting for {trigger}");
        var t = new Thread(() =>
        {
            try
            {
                while (!File.Exists(trigger)) Thread.Sleep(100);
                Say("trigger seen");
                Type Find(string full) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(full)).First(x => x != null)!;
                var dispatcherT = Find("Avalonia.Threading.Dispatcher");
                object ui = dispatcherT.GetProperty("UIThread", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                var post = dispatcherT.GetMethods().First(m => m.Name == "Post" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Action));
                Action work = () =>
                {
                    try
                    {
                        object app = Find("Avalonia.Application").GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                        var trayT = Find("Avalonia.Controls.TrayIcon");
                        var icons = (IEnumerable)trayT.GetMethod("GetIcons", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new[] { app })!;
                        object? quit = null; int n = 0;
                        foreach (var icon in icons)
                        {
                            n++;
                            Say($"tray icon {n}: IsVisible={trayT.GetProperty("IsVisible")!.GetValue(icon)} ToolTipText='{trayT.GetProperty("ToolTipText")!.GetValue(icon)}'");
                            object menu = trayT.GetProperty("Menu")!.GetValue(icon)!;
                            foreach (var item in (IEnumerable)menu.GetType().GetProperty("Items")!.GetValue(menu)!)
                            {
                                var h = item.GetType().GetProperty("Header");
                                string header = h == null ? "(separator)" : (string?)h.GetValue(item) ?? "";
                                var en = item.GetType().GetProperty("IsEnabled");
                                Say($"  menu item: '{header}' enabled={(en == null ? "-" : en.GetValue(item))}");
                                if (header.StartsWith("Quit AgentEyes", StringComparison.Ordinal)) quit = item;
                            }
                        }
                        if (quit == null) { Say($"FAILED: no 'Quit AgentEyes' item found on {n} tray icon(s)"); return; }
                        var bridge = quit.GetType().GetInterfaces().First(i => i.Name == "INativeMenuItemExporterEventsImplBridge");
                        Say($"bridge interface: {bridge.FullName} in {bridge.Assembly.GetName().Name} {bridge.Assembly.GetName().Version}");
                        Say("raising the Quit item's click: INativeMenuItemExporterEventsImplBridge.RaiseClicked()");
                        bridge.GetMethod("RaiseClicked")!.Invoke(quit, null);
                        Say("RaiseClicked returned");
                    }
                    catch (Exception ex) { Say("FAILED on the UI thread: " + ex); }
                };
                var ps = post.GetParameters();
                var args = new object?[ps.Length]; args[0] = work;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                post.Invoke(ui, args);
                Say("posted to the UI thread");
            }
            catch (Exception ex) { Say("FAILED: " + ex); }
        }) { IsBackground = true, Name = "qa-quit-hook" };
        t.Start();
    }
}
