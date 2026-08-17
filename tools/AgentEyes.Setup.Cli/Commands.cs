using System.Diagnostics;
using AgentEyes.Setup.Engine;

namespace AgentEyes.Setup.Cli;

/// <summary>Implements each CLI command over the engine. Thin: no business logic lives here.</summary>
internal static class Commands
{
    private const int Ok = 0;
    private const int Error = 1;

    // ---- commands ----------------------------------------------------------

    public static int Components(CliArgs args, InstallLayout layout, bool json)
    {
        var components = ComponentRegistry.All;
        if (json)
        {
            Program.WriteJson(components.Select(c => new
            {
                id = c.Id,
                kind = c.Kind.ToString(),
                asset = c.Asset,
                path = layout.PathFor(c),
            }));
            return Ok;
        }

        Console.WriteLine("Components:");
        foreach (var c in components)
            Console.WriteLine($"  {c.Id,-10} {c.Kind,-9} {c.Asset}");
        return Ok;
    }

    public static int Status(CliArgs args, InstallLayout layout, bool json)
    {
        var components = ComponentRegistry.All;
        var reader = new InstalledStateReader(layout);
        var state = reader.ReadAll(components);

        if (json)
        {
            Program.WriteJson(components.Select(c =>
            {
                var s = state[c.Id];
                return new { id = c.Id, present = s.Present, version = s.Version, path = s.Path };
            }));
            return Ok;
        }

        Console.WriteLine($"Installed status (root '{layout.LocalRoot}'):");
        foreach (var c in components)
        {
            var s = state[c.Id];
            var ver = s.Present ? (s.Version ?? "version unknown") : "not installed";
            Console.WriteLine($"  {c.Id,-10} {ver}");
        }
        return Ok;
    }

    public static async Task<int> PlanAsync(CliArgs args, InstallLayout layout, bool json)
    {
        var (plan, _) = await ComputePlanAsync(args, layout);
        PrintPlan(plan, json);
        return Ok;
    }

    public static async Task<int> UpdateAsync(CliArgs args, InstallLayout layout, bool json, bool installMode)
    {
        // Updating can replace agenteyes-setup.exe ITSELF. A single-file exe must not swap
        // its own file while running (lazy assembly loads then read the WRONG bundle -
        // field-tested: Microsoft.Win32.Registry failed to load mid-update). Same
        // remedy as uninstall: continue from a temp copy.
        if (!args.HasFlag("dry-run") && RelaunchFromTempIfInsideInstall(args, layout, json, "update"))
            return Ok;

        var (plan, release) = await ComputePlanAsync(args, layout);

        // Optionally narrow to one component.
        var only = args.Option("component");
        if (!string.IsNullOrWhiteSpace(only) && !only.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var filtered = plan.Items.Where(i => i.ComponentId.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count == 0) throw new UsageException($"--component '{only}' is not in scope.");
            plan = new UpdatePlan { Items = filtered };
        }

        if (args.HasFlag("dry-run"))
        {
            PrintPlan(plan, json);
            return Ok;
        }

        // Taking over the Inno v0.1 install wipes the app dir first, which needs the
        // app stopped - and forces a fresh Install plan since nothing remains on disk.
        if (installMode && InnoMigration.IsInnoInstall(layout))
        {
            // The Inno takeover wipes the app dir, which needs the app stopped. Stop it
            // automatically (issue #95) instead of aborting with a manual-quit error. The
            // stop is bounded + confirmed; only a genuine failure to stop aborts.
            if (RunningApp.IsRunning(layout))
            {
                if (!json) Console.WriteLine("AgentEyes is running - stopping it to replace the previous (v0.1) install...");
                if (!RunningApp.StopAndWait(layout))
                {
                    const string msg = "ERROR: AgentEyes is running and could not be stopped automatically.\n" +
                                       "       Close it (tray icon -> Quit) and re-run this command.";
                    if (json) Program.WriteJson(new { failed = msg }); else Console.Error.WriteLine(msg);
                    return Error;
                }
            }
            InnoMigration.RemoveInnoInstall(layout);
            (plan, release) = await ComputePlanAsync(args, layout);
            if (!json) Console.WriteLine("Removed the previous Inno-based install (taking over in place).");
        }

        var source = new ReleaseSource();
        var result = new UpdateRunResult { Results = Array.Empty<ApplyResult>() };
        if (plan.HasWork)
        {
            var runner = new UpdateRunner(layout, ComponentRegistry.All,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));
            result = await runner.ApplyAsync(plan);
            PrintRun(result, installMode, json);
        }
        else
        {
            if (json) Program.WriteJson(new { mode = installMode ? "install" : "update", applied = Array.Empty<object>(), message = "nothing to do" });
            else Console.WriteLine("Nothing to do - all components up to date.");
            if (!installMode) return Ok;
        }

        // Per-user finalization (wizard parity): PATH, Start Menu shortcut, and the
        // Add/Remove Programs entry. Install mode always finalizes (repair semantics);
        // update mode finalizes only when something was placed.
        var touched = installMode || result.Results.Any(r => r.Status is ApplyStatus.Installed or ApplyStatus.Updated);
        if (touched && !args.HasFlag("no-finalize"))
        {
            var pathChanged = InstallFinalizer.AddAppDirToPath(layout);
            var bundleDirChanged = InstallFinalizer.SetBundleExtractBaseDir(layout);
            var shortcut = InstallFinalizer.CreateStartMenuShortcut(layout);
            var appVersion = release.Manifest.TryGetAsset(ComponentRegistry.App.Asset)?.Version ?? release.Manifest.Version;
            InstallFinalizer.RegisterUninstallEntry(layout, appVersion);

            if (installMode && args.HasFlag("desktop-shortcut"))
                InstallFinalizer.CreateDesktopShortcut(layout);

            var autostart = args.Option("autostart");
            if (installMode && autostart is not null)
            {
                if (autostart is not ("on" or "off"))
                    throw new UsageException($"--autostart must be 'on' or 'off', got '{autostart}'.");
                InstallFinalizer.SetAutostart(layout, autostart == "on");
            }

            if (!json)
            {
                Console.WriteLine(pathChanged ? $"PATH: added {layout.AppDir} (open a new terminal to use agenteyes)" : "PATH: already set");
                Console.WriteLine(bundleDirChanged
                    ? $"Native extraction dir: {InstallFinalizer.BundleExtractBaseDirVariable}={layout.BundleExtractDir}"
                    : $"Native extraction dir: already set ({layout.BundleExtractDir})");
                Console.WriteLine(shortcut ? "Start Menu shortcut: created" : "Start Menu shortcut: skipped (app not installed)");
                Console.WriteLine($"Add/Remove Programs: registered v{appVersion}");
            }
        }

        return result.Failed > 0 ? Error : Ok;
    }

    public static int Uninstall(CliArgs args, InstallLayout layout, bool json)
    {
        var uninstaller = new Uninstaller(layout);

        if (args.HasFlag("dry-run"))
        {
            var plan = uninstaller.Plan();
            if (json)
            {
                Program.WriteJson(plan.Select(t => new { kind = t.Kind.ToString(), t.Description, t.Path, t.Present }));
            }
            else
            {
                Console.WriteLine("Uninstall plan - removes ONLY install-owned files; your data is preserved:");
                foreach (var t in plan)
                    Console.WriteLine($"  [{(t.Present ? "x" : " ")}] {t.Kind,-10} {t.Description} ({t.Path})");
            }
            return Ok;
        }

        // Uninstall keeps its manual-quit behavior (out of scope for issue #95); only the
        // detection is consolidated onto the shared, correctly-named helper.
        if (RunningApp.IsRunning(layout))
        {
            const string msg = "ERROR: AgentEyes is running. Quit it (tray icon -> Quit) and re-run uninstall.";
            if (json) Program.WriteJson(new { failed = msg }); else Console.Error.WriteLine(msg);
            return Error;
        }

        // This exe lives inside the app dir it is about to delete. Relaunch a temp
        // copy of ourselves (the same trick Inno's uninstaller uses) so the dir can
        // actually go away; the engine's delete retries while this process exits.
        if (RelaunchFromTempIfInsideInstall(args, layout, json, "uninstall"))
            return Ok;

        var report = uninstaller.Apply();
        if (json)
        {
            Program.WriteJson(new { success = report.Success, steps = report.Steps, errors = report.Errors });
        }
        else
        {
            Console.WriteLine(report.Success ? "Uninstall complete (your data under %LOCALAPPDATA%\\AgentEyes is preserved):" : "Uninstall finished with errors:");
            foreach (var s in report.Steps) Console.WriteLine($"  {s}");
            foreach (var e in report.Errors) Console.WriteLine($"  ERROR: {e}");
        }
        return report.Success ? Ok : Error;
    }

    // ---- shared helpers ----------------------------------------------------

    /// <summary>
    /// When this process is the INSTALLED agenteyes-setup.exe, copy it to %TEMP% and
    /// re-run the same command from there (with --relaunched as the loop guard).
    /// Returns true when the relaunch was started and the caller should exit.
    /// </summary>
    private static bool RelaunchFromTempIfInsideInstall(CliArgs args, InstallLayout layout, bool json, string what)
    {
        var self = Environment.ProcessPath!;
        if (args.HasFlag("relaunched") ||
            !self.StartsWith(layout.AppDir, StringComparison.OrdinalIgnoreCase))
            return false;

        var temp = Path.Combine(Path.GetTempPath(), $"agenteyes-setup-{what}-{Guid.NewGuid():N}.exe");
        File.Copy(self, temp);
        var psi = new ProcessStartInfo(temp) { UseShellExecute = false };
        // The temp copy must NOT unpack its own native DLLs into the bundle dir it is about
        // to delete - dropping the variable sends this one child back to the host default
        // under %TEMP%, so nothing inside the bundle dir is locked while uninstall runs.
        psi.Environment.Remove(InstallFinalizer.BundleExtractBaseDirVariable);
        foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--relaunched");
        Process.Start(psi);
        if (!json) Console.WriteLine($"Continuing {what} from a temporary copy...");
        return true;
    }

    private static async Task<(UpdatePlan plan, ResolvedRelease release)> ComputePlanAsync(CliArgs args, InstallLayout layout)
    {
        var release = await ResolveReleaseAsync(args);
        var reader = new InstalledStateReader(layout);
        var installed = reader.ReadAll(ComponentRegistry.All);
        var plan = UpdatePlanner.Plan(ComponentRegistry.All, installed, release.Manifest);
        return (plan, release);
    }

    private static async Task<ResolvedRelease> ResolveReleaseAsync(CliArgs args)
    {
        // --release-dir wins: a local directory acting as a full release (offline).
        var releaseDir = args.Option("release-dir");
        if (!string.IsNullOrWhiteSpace(releaseDir))
            return ReleaseSource.LoadLocalReleaseDir(releaseDir);

        var manifest = args.Option("manifest", "latest");
        if (manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return await new ReleaseSource().FetchLatestAsync(CancellationToken.None);
        return ReleaseSource.LoadLocalManifest(manifest);
    }

    private static void PrintPlan(UpdatePlan plan, bool json)
    {
        if (json)
        {
            Program.WriteJson(plan.Items.Select(i => new
            {
                component = i.ComponentId,
                action = i.Kind.ToString(),
                from = i.FromVersion,
                to = i.ToVersion,
            }));
            return;
        }

        Console.WriteLine("Plan:");
        foreach (var i in plan.Items)
        {
            var detail = i.Kind switch
            {
                PlanItemKind.Update => $"{i.FromVersion} -> {i.ToVersion}",
                PlanItemKind.Install => $"install {i.ToVersion}",
                PlanItemKind.UpToDate => $"up to date ({i.ToVersion})",
                PlanItemKind.MissingAsset => "no asset in release",
                _ => i.Kind.ToString(),
            };
            Console.WriteLine($"  {i.ComponentId,-10} {i.Kind,-12} {detail}");
        }
        Console.WriteLine($"Actionable: {plan.Actionable.Count} ({plan.ToInstall.Count} install, {plan.ToUpdate.Count} update)");
    }

    private static void PrintRun(UpdateRunResult result, bool installMode, bool json)
    {
        if (json)
        {
            Program.WriteJson(new
            {
                mode = installMode ? "install" : "update",
                installed = result.Installed,
                updated = result.Updated,
                failed = result.Failed,
                results = result.Results.Select(r => new
                {
                    component = r.ComponentId,
                    status = r.Status.ToString(),
                    from = r.FromVersion,
                    to = r.ToVersion,
                    error = r.Error,
                }),
            });
            return;
        }

        Console.WriteLine($"{(installMode ? "Install" : "Update")} complete:");
        foreach (var r in result.Results)
        {
            var line = $"  {r.ComponentId,-10} {r.Status}";
            if (r.Error != null) line += $" - {r.Error}";
            Console.WriteLine(line);
        }
        Console.WriteLine($"installed={result.Installed} updated={result.Updated} failed={result.Failed}");
    }
}
