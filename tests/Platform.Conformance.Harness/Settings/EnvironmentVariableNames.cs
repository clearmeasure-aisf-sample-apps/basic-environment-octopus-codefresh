namespace Platform.Conformance.Harness.Settings;

/// <summary>
/// Environment variables the harness reads. Secrets come only from these variables, never from a file.
/// </summary>
public static class EnvironmentVariableNames
{
    /// <summary>Secret: API key of the Octopus automation user (sent as <c>X-Octopus-ApiKey</c>).</summary>
    public const string OctopusApiKey = "OCTOPUS_API_KEY";

    /// <summary>Secret: Codefresh API key (sent as the <c>Authorization</c> header).</summary>
    public const string CodefreshApiKey = "CODEFRESH_API_KEY";

    /// <summary>Client ID of the Azure service principal used with <see cref="AzureClientSecret"/>.</summary>
    public const string AzureClientId = "AZURE_CLIENT_ID";

    /// <summary>Secret: client secret of the Azure service principal.</summary>
    public const string AzureClientSecret = "AZURE_CLIENT_SECRET";

    /// <summary>Entra tenant ID; overrides <c>AzureTenantId</c> of the settings file.</summary>
    public const string AzureTenantId = "AZURE_TENANT_ID";

    /// <summary>Azure subscription ID; overrides <c>AzureSubscriptionId</c> of the settings file.</summary>
    public const string AzureSubscriptionId = "AZURE_SUBSCRIPTION_ID";

    /// <summary>Secret: GitHub token for the environment and application repositories.</summary>
    public const string GitHubToken = "GITHUB_TOKEN";

    /// <summary>
    /// Not a secret: the path of a file that holds the current GitHub token (<c>0600</c>). The pipeline scripts re-mint the
    /// one-hour GitHub App installation token and rewrite it; the harness reads it per request (<see cref="GitHubTokenSource"/>).
    /// </summary>
    public const string GitHubTokenFile = "GITHUB_TOKEN_FILE";

    /// <summary>Path of the settings file; default <c>tests/platform.settings.json</c> under the repository root.</summary>
    public const string SettingsFile = "PLATFORM_SETTINGS_FILE";

    /// <summary>Path of a single catalogue file to use instead of the merged <c>catalogue/</c> folder.</summary>
    public const string CatalogueFile = "PLATFORM_CATALOGUE_FILE";

    /// <summary>Repository root (the folder holding <c>tests/</c> and <c>catalogue/</c>); found from the test assembly location when unset.</summary>
    public const string RepositoryRoot = "PLATFORM_REPO_ROOT";

    /// <summary>
    /// <c>true</c> when TLS is re-terminated by a proxy whose CA is in the system trust store: cluster connections then
    /// validate against the system trust store instead of pinning the cluster CA. TLS verification is never disabled.
    /// </summary>
    public const string TlsSystemTrust = "PLATFORM_TLS_SYSTEM_TRUST";

    /// <summary>Run identifier used in resource names and the artifacts folder; generated when unset.</summary>
    public const string RunId = "PLATFORM_RUN_ID";

    /// <summary>Folder for run artifacts such as task logs; default <c>tests/TestResults/artifacts/&lt;run id&gt;</c>.</summary>
    public const string ArtifactsDirectory = "PLATFORM_ARTIFACTS_DIR";

    /// <summary>Every variable that holds a secret.</summary>
    public static IReadOnlyList<string> Secrets { get; } = [OctopusApiKey, CodefreshApiKey, AzureClientSecret, GitHubToken];
}
