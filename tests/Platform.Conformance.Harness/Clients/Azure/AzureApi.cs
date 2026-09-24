using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerService;
using Azure.ResourceManager.Resources;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Clients;

/// <summary>Azure Resource Manager and registry implementation of <see cref="IAzureApi"/>.</summary>
public sealed class AzureApi : IAzureApi, IDisposable
{
    /// <summary>Token scope of Azure Resource Manager.</summary>
    public const string ArmScope = "https://management.azure.com/.default";

    /// <summary>Token scope of the container registry data plane (accepted even when ARM-audience tokens are disabled).</summary>
    public const string RegistryScope = "https://containerregistry.azure.net/.default";

    /// <summary>API version of the generic <c>Microsoft.Compute/disks</c> reads.</summary>
    public const string DiskApiVersion = "2024-03-02";

    /// <summary>API version of the generic <c>Microsoft.AlertsManagement/actionRules</c> reads.</summary>
    public const string AlertProcessingRuleApiVersion = "2021-08-08";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TokenCredential credential;
    private readonly ArmClient arm;
    private readonly AzureApiOptions options;
    private readonly RestClient? registry;

    /// <summary>Creates the client.</summary>
    /// <param name="credential">Credential for ARM and the registry (see <see cref="AzureCredentialFactory"/>).</param>
    /// <param name="options">Subscription, tenant and registry.</param>
    /// <param name="registryHandler">Message handler for registry calls (unit tests pass a stub).</param>
    public AzureApi(TokenCredential credential, AzureApiOptions options, HttpMessageHandler? registryHandler = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SubscriptionId);
        this.credential = credential;
        this.options = options;
        var armOptions = new ArmClientOptions();
        armOptions.SetApiVersion(new ResourceType("Microsoft.Compute/disks"), DiskApiVersion);
        armOptions.SetApiVersion(new ResourceType("Microsoft.AlertsManagement/actionRules"), AlertProcessingRuleApiVersion);
        arm = new ArmClient(credential, options.SubscriptionId, armOptions);
        if (!PlatformSettings.IsMissing(options.RegistryLoginServer))
        {
            registry = new RestClient(PlatformHttp.Create(new Uri($"https://{options.RegistryLoginServer}/"), options.HttpTimeout, registryHandler), "Azure Container Registry", Json);
        }
    }

    /// <inheritdoc />
    public Task<AzureSubscriptionInfo> GetSubscriptionAsync(CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var subscription = await arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(options.SubscriptionId)).GetAsync(cancellationToken).ConfigureAwait(false);
        var data = subscription.Value.Data;
        return new AzureSubscriptionInfo(data.SubscriptionId, data.DisplayName, data.State?.ToString(), data.TenantId?.ToString());
    });

    /// <inheritdoc />
    public Task<AksClusterState> GetClusterStateAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var cluster = await GetClusterAsync(resourceGroup, clusterName, cancellationToken).ConfigureAwait(false);
        var data = cluster.Data;
        return new AksClusterState(
            clusterName,
            resourceGroup,
            data.PowerStateCode?.ToString(),
            data.ProvisioningState,
            data.CurrentKubernetesVersion ?? data.KubernetesVersion,
            data.Fqdn ?? data.PrivateFqdn,
            data.DisableLocalAccounts,
            data.AadProfile?.IsAzureRbacEnabled,
            ReadPools(cluster));
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<AksAgentPoolState>> GetAgentPoolsAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var cluster = await GetClusterAsync(resourceGroup, clusterName, cancellationToken).ConfigureAwait(false);
        return ReadPools(cluster);
    });

    /// <inheritdoc />
    public Task<byte[]> GetClusterUserKubeconfigAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var cluster = await GetClusterAsync(resourceGroup, clusterName, cancellationToken).ConfigureAwait(false);
        var credentials = await cluster.GetClusterUserCredentialsAsync(serverFqdn: null, format: null, cancellationToken).ConfigureAwait(false);
        var kubeconfig = credentials.Value.Kubeconfigs.FirstOrDefault()
            ?? throw new InvalidOperationException($"AKS cluster {clusterName} returned no user kubeconfig.");
        return kubeconfig.Value;
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<AzureResourceGroupInfo>> ListResourceGroupsAsync(CancellationToken cancellationToken = default) => GuardAsync<IReadOnlyList<AzureResourceGroupInfo>>(async () =>
    {
        var groups = new List<AzureResourceGroupInfo>();
        var subscription = arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(options.SubscriptionId));
        await foreach (var group in subscription.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            groups.Add(new AzureResourceGroupInfo(
                group.Data.Name,
                group.Data.Location.Name,
                group.Data.ResourceGroupProvisioningState,
                new Dictionary<string, string>(group.Data.Tags)));
        }

        return groups;
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<AzureManagedDisk>> ListManagedDisksAsync(string resourceGroup, CancellationToken cancellationToken = default) => GuardAsync<IReadOnlyList<AzureManagedDisk>>(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        var disks = new List<AzureManagedDisk>();
        var group = arm.GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(options.SubscriptionId, resourceGroup));
        await foreach (var listed in group.GetGenericResourcesAsync(filter: "resourceType eq 'Microsoft.Compute/disks'", cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var disk = (await listed.GetAsync(cancellationToken).ConfigureAwait(false)).Value.Data;
            var properties = Properties(disk.Properties);
            disks.Add(new AzureManagedDisk(
                disk.Name,
                disk.Id.ToString(),
                disk.Location.Name,
                disk.ManagedBy,
                Text(properties, "diskState"),
                Number(properties, "diskSizeGB"),
                disk.Sku?.Name,
                new Dictionary<string, string>(disk.Tags)));
        }

        return disks;
    });

    /// <inheritdoc />
    public Task<AlertProcessingRuleState> GetAlertProcessingRuleAsync(string resourceGroup, string ruleName, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        var id = new ResourceIdentifier($"{ResourceGroupResource.CreateResourceIdentifier(options.SubscriptionId, resourceGroup)}/providers/Microsoft.AlertsManagement/actionRules/{ruleName}");
        var rule = (await arm.GetGenericResource(id).GetAsync(cancellationToken).ConfigureAwait(false)).Value.Data;
        var properties = Properties(rule.Properties);
        var enabled = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new AlertProcessingRuleState(ruleName, resourceGroup, enabled, Text(properties, "description"));
    });

    /// <inheritdoc />
    public async Task<AcrRepositoryAttributes> GetRegistryRepositoryAsync(string repository, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var (client, token) = await RegistryAccessAsync(repository, cancellationToken).ConfigureAwait(false);
        var response = await client.GetAsync<JsonElement>($"acr/v1/{EscapeRepository(repository)}", cancellationToken, Bearer(token)).ConfigureAwait(false);
        return new AcrRepositoryAttributes(
            Text(response, "imageName") ?? repository,
            Number(response, "tagCount"),
            Number(response, "manifestCount"),
            Date(response, "lastUpdateTime"),
            Attributes(response));
    }

    /// <inheritdoc />
    public async Task<AcrTagAttributes> GetRegistryTagAsync(string repository, string tag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var (client, token) = await RegistryAccessAsync(repository, cancellationToken).ConfigureAwait(false);
        var response = await client.GetAsync<JsonElement>($"acr/v1/{EscapeRepository(repository)}/_tags/{Uri.EscapeDataString(tag)}", cancellationToken, Bearer(token)).ConfigureAwait(false);
        var detail = response.TryGetProperty("tag", out var item) ? item : response;
        return new AcrTagAttributes(
            repository,
            Text(detail, "name") ?? tag,
            Text(detail, "digest"),
            Date(detail, "createdTime"),
            Date(detail, "lastUpdateTime"),
            detail.TryGetProperty("signed", out var signed) && signed.ValueKind is JsonValueKind.True or JsonValueKind.False ? signed.GetBoolean() : null,
            Attributes(detail));
    }

    /// <summary>Disposes the registry HTTP client.</summary>
    public void Dispose() => registry?.Dispose();

    private static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PlatformPrerequisiteException(AzureCredentialFactory.MissingCredentialMessage(ex.Message), ex);
        }
    }

    private async Task<ContainerServiceManagedClusterResource> GetClusterAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterName);
        var group = arm.GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(options.SubscriptionId, resourceGroup));
        Response<ContainerServiceManagedClusterResource> cluster = await group.GetContainerServiceManagedClusterAsync(clusterName, cancellationToken).ConfigureAwait(false);
        return cluster.Value;
    }

    private static IReadOnlyList<AksAgentPoolState> ReadPools(ContainerServiceManagedClusterResource cluster) =>
        cluster.Data.AgentPoolProfiles
            .Select(pool => new AksAgentPoolState(
                pool.Name,
                pool.Mode?.ToString(),
                pool.Count,
                pool.MinCount,
                pool.MaxCount,
                pool.EnableAutoScaling,
                pool.PowerStateCode?.ToString(),
                pool.ProvisioningState,
                pool.VmSize))
            .ToArray();

    private async Task<(RestClient Client, string Token)> RegistryAccessAsync(string repository, CancellationToken cancellationToken)
    {
        if (registry is null)
        {
            throw new PlatformPrerequisiteException("Registry calls need the setting RegistryLoginServer (for example myregistry.azurecr.io); it is not set or still a placeholder.");
        }

        AccessToken entraToken;
        try
        {
            entraToken = await credential.GetTokenAsync(new TokenRequestContext([RegistryScope]), cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PlatformPrerequisiteException(AzureCredentialFactory.MissingCredentialMessage(ex.Message), ex);
        }

        var service = options.RegistryLoginServer!;
        var exchange = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "access_token"),
            new("service", service),
            new("access_token", entraToken.Token),
        };
        if (!PlatformSettings.IsMissing(options.TenantId))
        {
            exchange.Add(new("tenant", options.TenantId!));
        }

        var refresh = await registry.PostFormAsync<JsonElement>("oauth2/exchange", exchange, cancellationToken).ConfigureAwait(false);
        var refreshToken = Text(refresh, "refresh_token") ?? throw new InvalidOperationException("The registry token exchange returned no refresh_token.");
        var access = await registry.PostFormAsync<JsonElement>(
            "oauth2/token",
            [
                new("grant_type", "refresh_token"),
                new("service", service),
                new("scope", $"repository:{repository}:metadata_read"),
                new("refresh_token", refreshToken),
            ],
            cancellationToken).ConfigureAwait(false);
        var accessToken = Text(access, "access_token") ?? throw new InvalidOperationException("The registry token endpoint returned no access_token.");
        return (registry, accessToken);
    }

    private static Action<HttpRequestMessage> Bearer(string token) =>
        request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static string EscapeRepository(string repository) =>
        string.Join('/', repository.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static AcrChangeableAttributes Attributes(JsonElement element)
    {
        var attributes = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("changeableAttributes", out var found) ? found : default;
        return new AcrChangeableAttributes(Flag(attributes, "deleteEnabled"), Flag(attributes, "writeEnabled"), Flag(attributes, "readEnabled"), Flag(attributes, "listEnabled"));
    }

    private static JsonElement Properties(BinaryData? properties) =>
        properties is null ? default : properties.ToObjectFromJson<JsonElement>();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
