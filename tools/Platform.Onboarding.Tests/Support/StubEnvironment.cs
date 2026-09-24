using Platform.Onboarding.Cli;

namespace Platform.Onboarding.Tests.Support;

/// <summary>Environment variables and a fixed date for tests.</summary>
internal sealed class StubEnvironment : IEnvironment
{
    private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);

    public StubEnvironment(DateOnly? today = null)
    {
        Today = today ?? new DateOnly(2026, 9, 24);
    }

    public DateOnly Today { get; }

    public StubEnvironment With(string name, string value)
    {
        variables[name] = value;
        return this;
    }

    public string? Get(string name) => variables.GetValueOrDefault(name);
}
