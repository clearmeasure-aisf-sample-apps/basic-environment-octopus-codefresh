using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// Raw Azure Resource Manager reads the harness's <see cref="IAzureApi"/> does not cover: resource lists with tags,
/// role assignments, identities, networks, the registry, budgets, public IPs and cluster properties. Read-only GETs
/// with the harness credential (<see cref="AzureCredentialFactory"/>); errors name the path and status, never a token.
/// </summary>
public sealed class ArmReader : IDisposable
{
    /// <summary>API version of <c>resourceGroups/{group}/resources</c>.</summary>
    public const string ResourcesApiVersion = "2021-04-01";

    /// <summary>API version of <c>Microsoft.Authorization/roleAssignments</c>.</summary>
    public const string RoleAssignmentsApiVersion = "2022-04-01";

    /// <summary>API version of <c>Microsoft.ManagedIdentity/userAssignedIdentities</c>.</summary>
    public const string IdentitiesApiVersion = "2023-01-31";

    /// <summary>API version of <c>Microsoft.Network</c> reads (virtual networks, public IPs).</summary>
    public const string NetworkApiVersion = "2024-05-01";

    /// <summary>API version of <c>Microsoft.ContainerRegistry/registries</c>.</summary>
    public const string RegistryApiVersion = "2023-07-01";

    /// <summary>API version of <c>Microsoft.ContainerService/managedClusters</c>.</summary>
    public const string ManagedClustersApiVersion = "2024-09-01";

    /// <summary>API version of <c>Microsoft.Consumption/budgets</c>.</summary>
    public const string BudgetsApiVersion = "2023-05-01";

    private static readonly string[] ArmScope = [AzureApi.ArmScope];
    private readonly TokenCredential credential;
    private readonly HttpClient http;
    private AccessToken token;

    /// <summary>Creates the reader.</summary>
    /// <param name="credential">The harness credential.</param>
    /// <param name="subscriptionId">Platform subscription.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public ArmReader(TokenCredential credential, string subscriptionId, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        this.credential = credential;
        SubscriptionId = subscriptionId.Trim().ToLowerInvariant();
        http = PlatformHttp.Create(new Uri("https://management.azure.com/"), timeout);
    }

    /// <summary>The subscription, lowercase.</summary>
    public string SubscriptionId { get; }

    /// <summary><c>/subscriptions/{id}</c>.</summary>
    public string SubscriptionScope => $"/subscriptions/{SubscriptionId}";

    /// <summary>ARM ID of a resource group.</summary>
    /// <param name="resourceGroup">Group name.</param>
    public string GroupScope(string resourceGroup) => $"{SubscriptionScope}/resourceGroups/{resourceGroup}";

    /// <summary>GETs one object; <c>null</c> when it does not exist.</summary>
    /// <param name="path">Path from the ARM root, for example <c>/subscriptions/…/resourceGroups/…/providers/…</c>.</param>
    /// <param name="apiVersion">API version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PlatformApiException">ARM answered with another error, for example 403.</exception>
    public async Task<JsonElement?> GetAsync(string path, string apiVersion, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(WithApiVersion(path, apiVersion), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GETs every item of a list, following <c>nextLink</c>; empty when the parent does not exist.</summary>
    /// <param name="path">List path from the ARM root; may already carry a query such as <c>$filter</c>.</param>
    /// <param name="apiVersion">API version.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <exception cref="PlatformApiException">ARM answered with another error, for example 403.</exception>
    public async Task<IReadOnlyList<JsonElement>> ListAsync(string path, string apiVersion, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var next = WithApiVersion(path, apiVersion);
        while (next is not null)
        {
            using var response = await SendAsync(next, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return items;
            }

            var page = await ReadAsync(response, path, cancellationToken).ConfigureAwait(false);
            if (page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(value.EnumerateArray().Select(item => item.Clone()));
            }

            next = page.TryGetProperty("nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        }

        return items;
    }

    /// <summary>Every resource of a group with its type and tags; empty when the group does not exist.</summary>
    /// <param name="resourceGroup">Group name.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public Task<IReadOnlyList<JsonElement>> ListResourcesAsync(string resourceGroup, CancellationToken cancellationToken) =>
        ListAsync($"{GroupScope(resourceGroup)}/resources", ResourcesApiVersion, cancellationToken);

    /// <summary>Role assignments that apply at a group: at, above and below it.</summary>
    /// <param name="resourceGroup">Group name.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public Task<IReadOnlyList<JsonElement>> ListRoleAssignmentsAsync(string resourceGroup, CancellationToken cancellationToken) =>
        ListAsync($"{GroupScope(resourceGroup)}/providers/Microsoft.Authorization/roleAssignments", RoleAssignmentsApiVersion, cancellationToken);

    /// <summary>User-assigned identities of a group with their principal IDs.</summary>
    /// <param name="resourceGroup">Group name.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public Task<IReadOnlyList<JsonElement>> ListIdentitiesAsync(string resourceGroup, CancellationToken cancellationToken) =>
        ListAsync($"{GroupScope(resourceGroup)}/providers/Microsoft.ManagedIdentity/userAssignedIdentities", IdentitiesApiVersion, cancellationToken);

    /// <summary>Virtual networks of a group with their peerings.</summary>
    /// <param name="resourceGroup">Group name.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public Task<IReadOnlyList<JsonElement>> ListVirtualNetworksAsync(string resourceGroup, CancellationToken cancellationToken) =>
        ListAsync($"{GroupScope(resourceGroup)}/providers/Microsoft.Network/virtualNetworks", NetworkApiVersion, cancellationToken);

    /// <summary>The address of a static public IP, or <c>null</c> when the IP does not exist or has none.</summary>
    /// <param name="resourceGroup">Group name.</param>
    /// <param name="name">Public IP name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string?> GetPublicIpAddressAsync(string resourceGroup, string name, CancellationToken cancellationToken)
    {
        var ip = await GetAsync($"{GroupScope(resourceGroup)}/providers/Microsoft.Network/publicIPAddresses/{name}", NetworkApiVersion, cancellationToken).ConfigureAwait(false);
        return ip is { } found && Text(found, "properties", "ipAddress") is { Length: > 0 } address ? address : null;
    }

    /// <summary>A managed cluster's OIDC issuer URL (it changes when the cluster is recreated), or <c>null</c> when the cluster does not exist.</summary>
    /// <param name="resourceGroup">Cluster group.</param>
    /// <param name="clusterName">Cluster name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string?> GetClusterIssuerAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken)
    {
        var cluster = await GetAsync($"{GroupScope(resourceGroup)}/providers/Microsoft.ContainerService/managedClusters/{clusterName}", ManagedClustersApiVersion, cancellationToken).ConfigureAwait(false);
        return cluster is { } found ? Text(found, "properties", "oidcIssuerProfile", "issuerURL") ?? string.Empty : null;
    }

    /// <summary>Reads a string at a property path, or <c>null</c>.</summary>
    /// <param name="element">JSON object.</param>
    /// <param name="path">Property names, outermost first.</param>
    public static string? Text(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => current.GetRawText(),
            _ => null,
        };
    }

    /// <summary>The tags of an ARM object; empty when it has none.</summary>
    /// <param name="element">ARM object.</param>
    public static IReadOnlyDictionary<string, string> Tags(JsonElement element) =>
        element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            ? tags.EnumerateObject().ToDictionary(tag => tag.Name, tag => tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() ?? string.Empty : tag.Value.GetRawText(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    private static string WithApiVersion(string path, string apiVersion) =>
        $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={apiVersion}";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformApiException("Azure Resource Manager", "GET", path, response.StatusCode, body.Length <= 500 ? body : body[..500]);
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? new Uri(pathOrUrl) : new Uri(pathOrUrl.TrimStart('/'), UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken).ConfigureAwait(false));
        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        if (token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return token.Token;
        }

        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(ArmScope), cancellationToken).ConfigureAwait(false);
            return token.Token;
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PlatformPrerequisiteException(AzureCredentialFactory.MissingCredentialMessage(ex.Message), ex);
        }
    }
}
