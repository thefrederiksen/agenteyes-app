namespace AgentEyes.Setup.Engine;

/// <summary>Outcome of one orchestrated update pass.</summary>
public sealed record OrchestratorResult(UpdatePlan Plan, UpdateRunResult? Run)
{
    /// <summary>True when the plan found nothing to do (Run is null).</summary>
    public bool NoWork => Run is null;
}

/// <summary>
/// The single "update everything in scope" entry point hosts call: the tray
/// app's "Check for updates" and the setup CLI's update command both compose
/// the read -> plan -> apply pipeline through here so neither re-implements it.
/// </summary>
public sealed class Orchestrator
{
    private readonly InstallLayout _layout;
    private readonly InstalledStateReader _reader;

    public Orchestrator(InstallLayout layout, InstalledStateReader reader)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// Plan the given components against the manifest and apply any actionable
    /// items. Returns the plan plus the run result (null when there was no work).
    /// </summary>
    public async Task<OrchestratorResult> RunAsync(
        IReadOnlyList<Component> components,
        ReleaseManifest manifest,
        UpdateRunner.Downloader downloader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(downloader);

        var installed = _reader.ReadAll(components);
        var plan = UpdatePlanner.Plan(components, installed, manifest);

        EngineLog.Write($"[Orchestrator] RunAsync: {components.Count} components, {plan.Actionable.Count} actionable.");
        if (!plan.HasWork)
            return new OrchestratorResult(plan, null);

        var runner = new UpdateRunner(_layout, components, downloader);
        var run = await runner.ApplyAsync(plan, ct);
        return new OrchestratorResult(plan, run);
    }
}
