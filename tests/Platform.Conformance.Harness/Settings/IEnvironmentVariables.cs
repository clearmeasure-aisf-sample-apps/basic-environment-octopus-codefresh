namespace Platform.Conformance.Harness.Settings;

/// <summary>Reads environment variables; replaced by a stub in unit tests.</summary>
public interface IEnvironmentVariables
{
    /// <summary>Returns the value of <paramref name="name"/>, or <c>null</c> when it is not set.</summary>
    /// <param name="name">Variable name, for example <c>OCTOPUS_API_KEY</c>.</param>
    string? Get(string name);
}

/// <summary>Reads the variables of the current process.</summary>
public sealed class ProcessEnvironmentVariables : IEnvironmentVariables
{
    /// <summary>The shared instance.</summary>
    public static ProcessEnvironmentVariables Instance { get; } = new();

    /// <inheritdoc />
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}
