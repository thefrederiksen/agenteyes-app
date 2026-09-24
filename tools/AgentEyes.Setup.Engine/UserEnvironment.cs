namespace AgentEyes.Setup.Engine;

/// <summary>
/// The per-user environment variables the installer owns (the user PATH entry and
/// DOTNET_BUNDLE_EXTRACT_BASE_DIR). A seam so a TEST HOST can keep those writes in memory: the
/// real store is the user's registry, and it is shared with the installed AgentEyes - a test that
/// set, removed or crashed half-way through changing DOTNET_BUNDLE_EXTRACT_BASE_DIR left the
/// installed app extracting into %TEMP% again (issue #78 investigation; the #120 failure mode).
/// </summary>
public interface IUserEnvironment
{
    /// <summary>The variable's current value, or null when it is not set.</summary>
    string? Get(string name);

    /// <summary>Set the variable; null removes it.</summary>
    void Set(string name, string? value);
}

/// <summary>The real store: HKCU\Environment, via <see cref="EnvironmentVariableTarget.User"/>.
/// A set persists to the registry and broadcasts WM_SETTINGCHANGE, so new Explorer-launched
/// processes pick it up.</summary>
public sealed class RegistryUserEnvironment : IUserEnvironment
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void Set(string name, string? value)
    {
        EngineLog.Write($"[RegistryUserEnvironment] Set: {name}={(value ?? "(removed)")}");
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    }
}
