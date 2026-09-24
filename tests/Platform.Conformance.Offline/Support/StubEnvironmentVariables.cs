using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Offline.Support;

/// <summary>Environment variables from a dictionary; nothing else is visible.</summary>
internal sealed class StubEnvironmentVariables : IEnvironmentVariables
{
    private readonly Dictionary<string, string?> values;

    public StubEnvironmentVariables(params (string Name, string? Value)[] variables)
    {
        values = variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal);
    }

    public string? Get(string name) => values.GetValueOrDefault(name);
}
