// Issue #161 Windows QA harness. Two modes:
//   derive <agenteyes.dll> <realLocalAppData> <realVideos> <scratch>
//       Prints what this process's ENVIRONMENT says and what .NET's special folders say, then runs the
//       branch's own RealLibrary.DeriveForThisProcess and LibraryRedirect.Decide (path rules only - no disk
//       callbacks, so nothing is read or written) for two attacks, against (a) the roots the guard really
//       derives and (b) COUNTERFACTUAL roots derived from the environment.
//   standin <port>
//       Plays a post-#161 service: GET /status answers DataRoot/RecordingsRoot (scratch paths), nothing else.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

static class P
{
    static int Main(string[] a)
    {
        if (a[0] == "standin") return Standin(int.Parse(a[1]));
        return Derive(a[1], a[2], a[3], a[4]);
    }

    static int Standin(int port)
    {
        var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/"); l.Start();
        var body = Encoding.ASCII.GetBytes("{\"DataRoot\":\"C:/qa161-standin/data/AgentEyes\",\"RecordingsRoot\":\"C:/qa161-standin/videos/AgentEyes\"}");
        Console.WriteLine($"standin listening 127.0.0.1:{port}");
        while (true)
        {
            var c = l.GetContext();
            if (c.Request.Url!.AbsolutePath != "/status") { c.Response.StatusCode = 404; c.Response.Close(); continue; }
            c.Response.ContentType = "application/json"; c.Response.ContentLength64 = body.Length;
            c.Response.OutputStream.Write(body); c.Response.Close();
        }
    }

    static int Derive(string dll, string realLad, string realVideos, string scratch)
    {
        var asm = Assembly.LoadFrom(dll);
        var rl = asm.GetType("AgentEyes.RealLibrary", true)!;
        var lr = asm.GetType("AgentEyes.LibraryRedirect", true)!;
        const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        Console.WriteLine("== the environment this process was given");
        foreach (var v in new[] { "USERPROFILE", "LOCALAPPDATA", "APPDATA", "HOMEDRIVE", "HOMEPATH" })
            Console.WriteLine($"  env {v,-13}= {Environment.GetEnvironmentVariable(v)}");
        Console.WriteLine("== what .NET's special folders return in this process (the guard's source on Windows)");
        foreach (var f in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
                                  Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyVideos })
            Console.WriteLine($"  GetFolderPath({f,-20}) = {Environment.GetFolderPath(f)}");

        string envLad = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        string envVideos = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Videos");
        Console.WriteLine("== COUNTERFACTUAL: the real library a guard reading the ENVIRONMENT would derive");
        Console.WriteLine($"  local app data = %LOCALAPPDATA%          = {envLad}");
        Console.WriteLine($"  Videos         = %USERPROFILE%/Videos    = {envVideos}");

        var guardRoots = rl.GetMethod("DeriveForThisProcess", F)!.Invoke(null, null)!;
        Console.WriteLine("== RealLibrary.DeriveForThisProcess() - the branch's own code, in this process");
        foreach (var r in (IEnumerable)guardRoots) Console.WriteLine($"  {r}");

        var derive = rl.GetMethod("Derive", F)!;
        Func<string, string?> noRead = _ => null;
        var envRoots = derive.Invoke(null, new object?[] { null, 0u, false, noRead, envLad, envVideos })!;

        var decide = lr.GetMethods(F).Single(m => m.Name == "Decide" && m.GetParameters().Length == 9);
        string sd = Path.Combine(scratch, "d"), sv = Path.Combine(scratch, "v");
        var attacks = new[]
        {
            ("ATTACK 1: AGENTEYES_REDIRECT_DATA = the REAL local app data", realLad, sv),
            ("ATTACK 2: AGENTEYES_REDIRECT_VIDEOS = the REAL Videos", sd, realVideos),
            ("CONTROL : both at scratch (must be accepted)", sd, sv),
        };
        var names = new[] { "AGENTEYES_REDIRECT_DATA", "AGENTEYES_REDIRECT_VIDEOS" };
        foreach (var (label, data, videos) in attacks)
        {
            Console.WriteLine($"== {label}  (data={data} videos={videos})");
            foreach (var (which, roots) in new[] { ("guard's real roots      ", guardRoots), ("COUNTERFACTUAL env roots", envRoots) })
            {
                var d = decide.Invoke(null, new object?[] { data, videos, names, roots, true, null, null, null, null })!;
                var refusal = (string?)d.GetType().GetProperty("Refusal")!.GetValue(d);
                Console.WriteLine($"  {which}: {(refusal == null ? "ACCEPTED" : "REFUSED - " + refusal)}");
            }
        }
        return 0;
    }
}
