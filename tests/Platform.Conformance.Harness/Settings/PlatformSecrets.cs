namespace Platform.Conformance.Harness.Settings;

/// <summary>
/// Secrets read from environment variables. <see cref="ToString"/> never prints a value, so an object dump in
/// a test message cannot leak one.
/// </summary>
public sealed class PlatformSecrets
{
    /// <summary>Creates the secrets; blank values count as missing.</summary>
    /// <param name="octopusApiKey">Value of <c>OCTOPUS_API_KEY</c>.</param>
    /// <param name="codefreshApiKey">Value of <c>CODEFRESH_API_KEY</c>.</param>
    /// <param name="azureClientId">Value of <c>AZURE_CLIENT_ID</c>.</param>
    /// <param name="azureClientSecret">Value of <c>AZURE_CLIENT_SECRET</c>.</param>
    /// <param name="gitHubToken">Value of <c>GITHUB_TOKEN</c>.</param>
    /// <param name="gitHubTokenFile">Value of <c>GITHUB_TOKEN_FILE</c>: a file that holds the current GitHub token, read on every access.</param>
    public PlatformSecrets(string? octopusApiKey, string? codefreshApiKey, string? azureClientId, string? azureClientSecret, string? gitHubToken, string? gitHubTokenFile = null)
    {
        GitHubTokens = new GitHubTokenSource(gitHubToken, gitHubTokenFile);
        OctopusApiKey = Normalize(octopusApiKey);
        CodefreshApiKey = Normalize(codefreshApiKey);
        AzureClientId = Normalize(azureClientId);
        AzureClientSecret = Normalize(azureClientSecret);
    }

    /// <summary>Octopus API key, or <c>null</c>.</summary>
    public string? OctopusApiKey { get; }

    /// <summary>Codefresh API key, or <c>null</c>.</summary>
    public string? CodefreshApiKey { get; }

    /// <summary>Azure service principal client ID, or <c>null</c>.</summary>
    public string? AzureClientId { get; }

    /// <summary>Azure service principal client secret, or <c>null</c>.</summary>
    public string? AzureClientSecret { get; }

    /// <summary>
    /// The current GitHub token, or <c>null</c>: the content of the file named by <c>GITHUB_TOKEN_FILE</c> when it holds
    /// one (re-read on every access, so a token a pipeline script re-mints is picked up), else <c>GITHUB_TOKEN</c>.
    /// </summary>
    public string? GitHubToken => GitHubTokens.Current;

    /// <summary>The source of <see cref="GitHubToken"/>.</summary>
    public GitHubTokenSource GitHubTokens { get; }

    /// <summary>Reads every secret from <paramref name="environment"/>.</summary>
    /// <param name="environment">Source of environment variables.</param>
    public static PlatformSecrets FromEnvironment(IEnvironmentVariables environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new PlatformSecrets(
            environment.Get(EnvironmentVariableNames.OctopusApiKey),
            environment.Get(EnvironmentVariableNames.CodefreshApiKey),
            environment.Get(EnvironmentVariableNames.AzureClientId),
            environment.Get(EnvironmentVariableNames.AzureClientSecret),
            environment.Get(EnvironmentVariableNames.GitHubToken),
            environment.Get(EnvironmentVariableNames.GitHubTokenFile));
    }

    /// <summary>Lists which secrets are set, without their values.</summary>
    public override string ToString() =>
        $"PlatformSecrets {{ {EnvironmentVariableNames.OctopusApiKey} = {State(OctopusApiKey)}, "
        + $"{EnvironmentVariableNames.CodefreshApiKey} = {State(CodefreshApiKey)}, "
        + $"{EnvironmentVariableNames.AzureClientId} = {State(AzureClientId)}, "
        + $"{EnvironmentVariableNames.AzureClientSecret} = {State(AzureClientSecret)}, "
        + $"{EnvironmentVariableNames.GitHubToken} = {State(GitHubToken)} }}";

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string State(string? value) => value is null ? "missing" : "set";
}
