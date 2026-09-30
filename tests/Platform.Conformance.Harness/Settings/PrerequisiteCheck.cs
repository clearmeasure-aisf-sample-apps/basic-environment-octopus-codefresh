namespace Platform.Conformance.Harness.Settings;

/// <summary>
/// Collects the settings and secrets a live test needs and, when any is missing, throws one
/// <see cref="PlatformPrerequisiteException"/> (reported as Inconclusive) that names all of them.
/// </summary>
/// <example><code>Settings.Check("the prod wake test").Setting("Tiers.prod.ClusterName", tier.ClusterName).ThrowIfMissing();</code></example>
public sealed class PrerequisiteCheck
{
    private readonly List<string> missing = [];
    private readonly string purpose;
    private readonly string settingsSource;

    /// <summary>Starts a check.</summary>
    /// <param name="purpose">What needs the prerequisites, completing "prerequisites missing for ..." (for example "the Octopus API").</param>
    /// <param name="settingsSource">Where settings come from, for messages (usually the settings file path).</param>
    public PrerequisiteCheck(string purpose, string settingsSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        this.purpose = purpose;
        this.settingsSource = settingsSource;
    }

    /// <summary>What is missing so far, one entry per setting or secret.</summary>
    public IReadOnlyList<string> Missing => missing;

    /// <summary><c>true</c> when nothing is missing.</summary>
    public bool IsSatisfied => missing.Count == 0;

    /// <summary>Requires a non-secret setting that is set and not a placeholder.</summary>
    /// <param name="name">Setting name as written in the settings file, for example <c>OctopusUrl</c>.</param>
    /// <param name="value">Current value.</param>
    /// <returns>This check, for chaining.</returns>
    public PrerequisiteCheck Setting(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add($"setting {name} is not set in {settingsSource}");
        }
        else if (PlatformSettings.IsPlaceholder(value))
        {
            missing.Add($"setting {name} in {settingsSource} is still the placeholder '{value}' (the provisioning step fills it in)");
        }

        return this;
    }

    /// <summary>Requires a secret environment variable to be set.</summary>
    /// <param name="environmentVariable">Variable name, for example <c>OCTOPUS_API_KEY</c>.</param>
    /// <param name="value">Current value.</param>
    /// <returns>This check, for chaining.</returns>
    public PrerequisiteCheck Secret(string environmentVariable, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add($"environment variable {environmentVariable} is not set (secrets come only from the environment)");
        }

        return this;
    }

    /// <summary>
    /// Requires a GitHub token: <c>GITHUB_TOKEN</c>, or a <c>GITHUB_TOKEN_FILE</c> whose file holds one (the conformance
    /// GitHub App <c>aisf-conformance</c> mints it, docs/runbooks/conformance.md).
    /// </summary>
    /// <param name="value">Current token (<see cref="PlatformSecrets.GitHubToken"/>).</param>
    /// <returns>This check, for chaining.</returns>
    public PrerequisiteCheck GitHubToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add($"environment variable {EnvironmentVariableNames.GitHubToken} is not set, and {EnvironmentVariableNames.GitHubTokenFile} names no token file "
                + "(the GitHub App aisf-conformance is not configured yet, or its token was not minted; secrets come only from the environment)");
        }

        return this;
    }

    /// <summary>Throws when anything is missing.</summary>
    /// <exception cref="PlatformPrerequisiteException">One or more prerequisites are missing; NUnit reports the test as Inconclusive.</exception>
    public void ThrowIfMissing()
    {
        if (missing.Count > 0)
        {
            throw new PlatformPrerequisiteException(
                $"Prerequisites missing for {purpose}: {string.Join("; ", missing)}. "
                + "A live test without its prerequisites is Inconclusive, never passed or failed; see tests/README.md, section Running the live tests.");
        }
    }
}
