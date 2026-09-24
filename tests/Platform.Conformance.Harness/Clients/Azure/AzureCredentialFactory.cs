using Azure.Core;
using Azure.Identity;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Chooses the Azure credential: a <see cref="ClientSecretCredential"/> when <c>AZURE_CLIENT_ID</c>,
/// <c>AZURE_CLIENT_SECRET</c> and the tenant are all set, otherwise <see cref="DefaultAzureCredential"/>
/// (workload identity, managed identity, Azure CLI and so on; never interactive).
/// </summary>
public static class AzureCredentialFactory
{
    /// <summary>Creates the credential for <paramref name="settings"/>.</summary>
    /// <param name="settings">Harness settings (secrets and tenant).</param>
    public static TokenCredential Create(PlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tenantId = PlatformSettings.IsMissing(settings.AzureTenantId) ? null : settings.AzureTenantId;
        if (settings.Secrets is { AzureClientId: { } clientId, AzureClientSecret: { } clientSecret } && tenantId is not null)
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = tenantId,
            ExcludeInteractiveBrowserCredential = true,
        });
    }

    /// <summary><c>true</c> when the service principal secret, its client ID and the tenant are all available.</summary>
    /// <param name="settings">Harness settings.</param>
    public static bool UsesServicePrincipal(PlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Secrets.AzureClientId is not null
            && settings.Secrets.AzureClientSecret is not null
            && !PlatformSettings.IsMissing(settings.AzureTenantId);
    }

    /// <summary>The message used when no credential is available, naming the variables to set.</summary>
    /// <param name="detail">What the credential chain reported.</param>
    public static string MissingCredentialMessage(string detail) =>
        $"No Azure credential is available: set {EnvironmentVariableNames.AzureClientId}, {EnvironmentVariableNames.AzureClientSecret} and "
        + $"{EnvironmentVariableNames.AzureTenantId} for a service principal, or provide a credential DefaultAzureCredential can find. {detail}";
}
