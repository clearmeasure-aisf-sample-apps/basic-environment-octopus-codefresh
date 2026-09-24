namespace Platform.Onboarding.Cli;

/// <summary>Environment variables and the clock, replaceable in tests.</summary>
internal interface IEnvironment
{
    /// <summary>Value of an environment variable, or <c>null</c>.</summary>
    /// <param name="name">Variable name.</param>
    string? Get(string name);

    /// <summary>Today's date (UTC).</summary>
    DateOnly Today { get; }
}

/// <summary>The process environment and the system clock.</summary>
internal sealed class ProcessEnvironment : IEnvironment
{
    /// <summary>The single instance.</summary>
    public static ProcessEnvironment Instance { get; } = new();

    /// <inheritdoc />
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc />
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
