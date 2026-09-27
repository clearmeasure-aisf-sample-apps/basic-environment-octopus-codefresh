using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// Minimal JSON-over-HTTP reads for the CAP-CF tests, for what the harness clients do not cover. Errors name the system,
/// path and status, never a credential; a 404 reads as <c>null</c>.
/// </summary>
public sealed class JsonRest : IDisposable
{
    private readonly HttpClient http;
    private readonly string system;

    /// <summary>Creates the reader.</summary>
    /// <param name="baseAddress">API base.</param>
    /// <param name="system">System name for messages.</param>
    /// <param name="timeout">Timeout of one request.</param>
    /// <param name="configure">Adds the authentication headers.</param>
    public JsonRest(Uri baseAddress, string system, TimeSpan timeout, Action<HttpRequestHeaders> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.system = system;
        http = PlatformHttp.Create(baseAddress, timeout);
        configure(http.DefaultRequestHeaders);
    }

    /// <summary>GETs a JSON document; <c>null</c> on 404.</summary>
    /// <param name="path">Path relative to the base, with its query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="accept">Accept header values; JSON when omitted.</param>
    /// <exception cref="PlatformApiException">Any other non-success status.</exception>
    public async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken, params string[] accept)
    {
        var (status, body, _) = await SendAsync(HttpMethod.Get, path, null, cancellationToken, accept).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : Parse(body, "GET", path, status);
    }

    /// <summary>GETs a JSON document and the value of its <c>Link</c> header.</summary>
    /// <param name="path">Path relative to the base, with its query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PlatformApiException">A non-success status other than 404.</exception>
    public async Task<(JsonElement? Body, string? Link)> GetPageAsync(string path, CancellationToken cancellationToken)
    {
        var (status, body, link) = await SendAsync(HttpMethod.Get, path, null, cancellationToken, []).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? (null, null) : (Parse(body, "GET", path, status), link);
    }

    /// <summary>POSTs a form and reads the JSON answer.</summary>
    /// <param name="path">Path relative to the base.</param>
    /// <param name="form">Form fields.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PlatformApiException">A non-success status.</exception>
    public async Task<JsonElement> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        var (status, body, _) = await SendAsync(HttpMethod.Post, path, content, cancellationToken, []).ConfigureAwait(false);
        return Parse(body, "POST", path, status);
    }

    /// <summary>Sends a request with an explicit bearer token (registry data plane).</summary>
    /// <param name="path">Path relative to the base.</param>
    /// <param name="bearer">Access token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="accept">Accept header values.</param>
    /// <returns>The document (<c>null</c> on 404) and the <c>Link</c> header.</returns>
    public async Task<(JsonElement? Body, string? Link)> GetWithBearerAsync(string path, string bearer, CancellationToken cancellationToken, params string[] accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        foreach (var value in accept.Length == 0 ? ["application/json"] : accept)
        {
            request.Headers.Accept.ParseAdd(value);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }

        return (Parse(body, "GET", path, response.StatusCode, response.IsSuccessStatusCode), LinkOf(response));
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    /// <summary>The target of a <c>rel="next"</c> link, or <c>null</c>.</summary>
    /// <param name="link">A <c>Link</c> header value.</param>
    public static string? NextLink(string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return null;
        }

        foreach (var part in link.Split(','))
        {
            if (part.Contains("rel=\"next\"", StringComparison.Ordinal))
            {
                var start = part.IndexOf('<', StringComparison.Ordinal);
                var end = part.IndexOf('>', StringComparison.Ordinal);
                if (start >= 0 && end > start)
                {
                    return part[(start + 1)..end].TrimStart('/');
                }
            }
        }

        return null;
    }

    private async Task<(HttpStatusCode Status, string Body, string? Link)> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, string[] accept)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        foreach (var value in accept.Length == 0 ? ["application/json"] : accept)
        {
            request.Headers.Accept.ParseAdd(value);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new PlatformApiException(system, method.Method, path, response.StatusCode, Truncate(body));
        }

        return (response.StatusCode, body, LinkOf(response));
    }

    private JsonElement Parse(string body, string method, string path, HttpStatusCode status, bool success = true)
    {
        if (!success)
        {
            throw new PlatformApiException(system, method, path, status, Truncate(body));
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PlatformApiException(system, method, path, status, $"the response is not JSON: {Truncate(body)}", ex);
        }
    }

    private static string? LinkOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values) ? string.Join(",", values) : null;

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}

/// <summary>JSON helpers of the CAP-CF tests.</summary>
public static class JsonRead
{
    /// <summary>A string property, or <c>null</c>.</summary>
    /// <param name="element">An object.</param>
    /// <param name="name">Property name.</param>
    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A date property, or <c>null</c>.</summary>
    /// <param name="element">An object.</param>
    /// <param name="name">Property name.</param>
    public static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    /// <summary>A nested property by path, or <c>default</c>.</summary>
    /// <param name="element">An object.</param>
    /// <param name="path">Property names from the outside in.</param>
    public static JsonElement Path(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return default;
            }
        }

        return current;
    }

    /// <summary>The items of an array property (or of the element itself when it is an array).</summary>
    /// <param name="element">An array, or an object that holds one.</param>
    /// <param name="name">Property name, or <c>null</c> for the element itself.</param>
    public static IEnumerable<JsonElement> Items(JsonElement element, string? name = null)
    {
        var list = name is null ? element : Path(element, name);
        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
    }

    /// <summary><c>true</c> when any string anywhere in <paramref name="element"/> equals <paramref name="value"/>.</summary>
    /// <param name="element">A document.</param>
    /// <param name="value">Text to find.</param>
    public static bool ContainsString(JsonElement element, string value) => element.ValueKind switch
    {
        JsonValueKind.String => string.Equals(element.GetString(), value, StringComparison.Ordinal),
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsString(property.Value, value)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsString(item, value)),
        _ => false,
    };
}

/// <summary>A Codefresh build as the CAP-CF tests read it (the raw workflow record).</summary>
/// <param name="Id">Build ID.</param>
/// <param name="Status">Status: success, error, terminated, denied, or a running or queued state.</param>
/// <param name="Revision">Commit SHA.</param>
/// <param name="Branch">Branch.</param>
/// <param name="Created">When it was created.</param>
/// <param name="Started">When it started running, or <c>null</c>.</param>
/// <param name="Finished">When it finished, or <c>null</c>.</param>
/// <param name="Record">The raw record.</param>
public sealed record CodefreshBuildRecord(string Id, string Status, string? Revision, string? Branch, DateTimeOffset? Created, DateTimeOffset? Started, DateTimeOffset? Finished, JsonElement Record)
{
    /// <summary><c>true</c> when the status is final.</summary>
    public bool IsTerminal => Status is "success" or "error" or "terminated" or "denied";

    /// <summary>When the build began to run: <see cref="Started"/>, else <see cref="Created"/>.</summary>
    public DateTimeOffset? Began => Started ?? Created;

    /// <summary>The steps the build ran, in order (<c>steps</c> of the record); a step skipped by its condition is absent.</summary>
    public IReadOnlyList<string> Steps =>
        JsonRead.Items(Record, "steps").Where(step => step.ValueKind == JsonValueKind.String).Select(step => step.GetString() ?? string.Empty).ToArray();

    /// <summary>Reads a raw workflow record.</summary>
    /// <param name="record">The record.</param>
    public static CodefreshBuildRecord From(JsonElement record) => new(
        JsonRead.Text(record, "id") ?? JsonRead.Text(record, "_id") ?? string.Empty,
        JsonRead.Text(record, "status") ?? string.Empty,
        JsonRead.Text(record, "revision"),
        JsonRead.Text(record, "branchName") ?? JsonRead.Text(record, "branch"),
        JsonRead.Date(record, "created"),
        JsonRead.Date(record, "started"),
        JsonRead.Date(record, "finished"),
        record);

    /// <summary>Compact text for messages.</summary>
    public override string ToString() => $"{Id} {Status} ({Branch} {Revision}, created {Created:u})";
}

/// <summary>Raw Codefresh reads: pipelines and their builds with revision and start time (<c>GET /workflow</c>).</summary>
public sealed class CodefreshRest : IDisposable
{
    private readonly JsonRest rest;

    /// <summary>Creates the reader.</summary>
    /// <param name="apiUrl">API base, for example <c>https://g.codefresh.io/api</c>.</param>
    /// <param name="apiKey">API key; sent only as the <c>Authorization</c> header.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public CodefreshRest(string apiUrl, string apiKey, TimeSpan timeout) =>
        rest = new JsonRest(new Uri(apiUrl), "Codefresh", timeout, headers => headers.TryAddWithoutValidation("Authorization", apiKey));

    /// <summary>The raw pipeline, or <c>null</c> when it does not exist.</summary>
    /// <param name="name">Full name, for example <c>sandbox/release</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonElement?> GetPipelineAsync(string name, CancellationToken cancellationToken) =>
        rest.GetAsync($"pipelines/{Uri.EscapeDataString(name)}", cancellationToken);

    /// <summary>The newest builds of a pipeline, newest first.</summary>
    /// <param name="name">Full pipeline name.</param>
    /// <param name="limit">Maximum number of builds.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <exception cref="InvalidOperationException">The pipeline does not exist.</exception>
    public async Task<IReadOnlyList<CodefreshBuildRecord>> ListBuildsAsync(string name, int limit, CancellationToken cancellationToken)
    {
        var pipeline = await GetPipelineAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Codefresh pipeline {name} does not exist (pwsh codefresh/register.ps1 --full registers it)");
        var id = JsonRead.Text(JsonRead.Path(pipeline, "metadata"), "id")
            ?? throw new InvalidOperationException($"Codefresh pipeline {name} has no metadata.id");
        var page = await rest.GetAsync($"workflow?pipeline={Uri.EscapeDataString(id)}&limit={Math.Max(1, limit)}&page=1", cancellationToken).ConfigureAwait(false);
        return page is { } body
            ? JsonRead.Items(JsonRead.Path(body, "workflows"), "docs").Select(CodefreshBuildRecord.From).ToArray()
            : [];
    }

    /// <summary>A build, or <c>null</c> when it does not exist.</summary>
    /// <param name="buildId">Build ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<CodefreshBuildRecord?> GetBuildAsync(string buildId, CancellationToken cancellationToken) =>
        await rest.GetAsync($"builds/{Uri.EscapeDataString(buildId)}", cancellationToken).ConfigureAwait(false) is { } record
            ? CodefreshBuildRecord.From(record)
            : null;

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();
}

/// <summary>A GitHub commit status.</summary>
/// <param name="Context">Status context, for example <c>codefresh/ci</c>.</param>
/// <param name="State">success, failure, error or pending.</param>
/// <param name="CreatedAt">When it was posted.</param>
/// <param name="Description">Description.</param>
public sealed record CommitStatus(string Context, string State, DateTimeOffset? CreatedAt, string? Description);

/// <summary>GitHub reads the harness's <see cref="IGitHubApi"/> does not cover: commit statuses and pull requests.</summary>
public sealed class GitHubReads : IDisposable
{
    private readonly JsonRest rest;

    /// <summary>Creates the reader.</summary>
    /// <param name="token">GitHub token; sent only as a bearer header.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public GitHubReads(string token, TimeSpan timeout) =>
        rest = new JsonRest(new Uri(GitHubApi.DefaultApiUrl), "GitHub", timeout, headers =>
        {
            headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            headers.Add("X-GitHub-Api-Version", "2022-11-28");
        });

    /// <summary>A repository as JSON (<c>allow_forking</c> among its settings); <c>null</c> when it does not exist.</summary>
    /// <param name="repository">owner/name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonElement?> GetRepositoryAsync(string repository, CancellationToken cancellationToken) =>
        rest.GetAsync($"repos/{repository}", cancellationToken, "application/vnd.github+json");

    /// <summary>Every status of a commit, newest first.</summary>
    /// <param name="repository"><c>owner/name</c>.</param>
    /// <param name="sha">Commit SHA.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<CommitStatus>> GetStatusesAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var list = await rest.GetAsync($"repos/{repository}/commits/{sha}/statuses?per_page=100", cancellationToken, "application/vnd.github+json").ConfigureAwait(false);
        return list is { } items
            ? JsonRead.Items(items)
                .Select(item => new CommitStatus(JsonRead.Text(item, "context") ?? string.Empty, JsonRead.Text(item, "state") ?? string.Empty, JsonRead.Date(item, "created_at"), JsonRead.Text(item, "description")))
                .OrderByDescending(status => status.CreatedAt)
                .ToArray()
            : [];
    }

    /// <summary>A pull request, or <c>null</c> when it does not exist.</summary>
    /// <param name="repository"><c>owner/name</c>.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonElement?> GetPullRequestAsync(string repository, int number, CancellationToken cancellationToken) =>
        rest.GetAsync($"repos/{repository}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", cancellationToken, "application/vnd.github+json");

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();
}

/// <summary>
/// Raw Azure Resource Manager reads of the build cluster: its identities, their role assignments and the scale set of
/// its <c>builds</c> pool. Read-only GETs with the harness credential.
/// </summary>
public sealed class BuildArm : IDisposable
{
    private const string ClusterApiVersion = "2024-09-01";
    private const string RoleAssignmentsApiVersion = "2022-04-01";
    private const string ComputeApiVersion = "2024-07-01";
    private readonly TokenCredential credential;
    private readonly JsonRest rest;
    private readonly string subscriptionId;
    private AccessToken token;

    /// <summary>Creates the reader.</summary>
    /// <param name="credential">The harness credential.</param>
    /// <param name="subscriptionId">Platform subscription.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public BuildArm(TokenCredential credential, string subscriptionId, TimeSpan timeout)
    {
        this.credential = credential;
        this.subscriptionId = subscriptionId.Trim().ToLowerInvariant();
        rest = new JsonRest(new Uri("https://management.azure.com/"), "Azure Resource Manager", timeout, _ => { });
    }

    /// <summary>The build cluster resource, or <c>null</c> when it does not exist.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonElement?> GetClusterAsync(CancellationToken cancellationToken) =>
        GetAsync($"subscriptions/{subscriptionId}/resourceGroups/{CodefreshPlatform.BuildGroup}/providers/Microsoft.ContainerService/managedClusters/{CodefreshPlatform.BuildCluster}?api-version={ClusterApiVersion}", cancellationToken);

    /// <summary>
    /// Every role assignment of a principal that this credential can read: subscription-wide when it may read role
    /// assignments at the subscription, else at, above and below each of <paramref name="resourceGroups"/> (the
    /// conformance principal holds Reader on the platform groups only, §7.0), without duplicates.
    /// </summary>
    /// <param name="principalId">Object ID.</param>
    /// <param name="resourceGroups">Groups to read when the subscription-wide list is forbidden.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<IReadOnlyList<JsonElement>> ListRoleAssignmentsAsync(string principalId, IReadOnlyList<string> resourceGroups, CancellationToken cancellationToken)
    {
        var filter = $"api-version={RoleAssignmentsApiVersion}&$filter={Uri.EscapeDataString($"principalId eq '{principalId}'")}";
        try
        {
            return await ListAsync($"subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleAssignments?{filter}", cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            var assignments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in resourceGroups.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var assignment in await ListAsync($"subscriptions/{subscriptionId}/resourceGroups/{group}/providers/Microsoft.Authorization/roleAssignments?{filter}", cancellationToken).ConfigureAwait(false))
                {
                    assignments[JsonRead.Text(assignment, "id") ?? assignment.GetRawText()] = assignment;
                }
            }

            return assignments.Values.ToArray();
        }
    }

    /// <summary>Creation times of the virtual machines of the <c>builds</c> pool (its scale sets in the node group).</summary>
    /// <param name="nodeResourceGroup">The cluster's node resource group.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<IReadOnlyList<DateTimeOffset?>> ListBuildsNodeCreationTimesAsync(string nodeResourceGroup, CancellationToken cancellationToken)
    {
        var sets = await ListAsync($"subscriptions/{subscriptionId}/resourceGroups/{nodeResourceGroup}/providers/Microsoft.Compute/virtualMachineScaleSets?api-version={ComputeApiVersion}", cancellationToken).ConfigureAwait(false);
        var times = new List<DateTimeOffset?>();
        foreach (var set in sets.Where(set => JsonRead.Text(JsonRead.Path(set, "tags"), "aks-managed-poolName") == CodefreshPlatform.BuildsPool))
        {
            var name = JsonRead.Text(set, "name");
            var machines = await ListAsync($"subscriptions/{subscriptionId}/resourceGroups/{nodeResourceGroup}/providers/Microsoft.Compute/virtualMachineScaleSets/{name}/virtualMachines?api-version={ComputeApiVersion}", cancellationToken).ConfigureAwait(false);
            times.AddRange(machines.Select(machine => JsonRead.Date(JsonRead.Path(machine, "properties"), "timeCreated")));
        }

        return times;
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();

    private async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var bearer = await BearerAsync(cancellationToken).ConfigureAwait(false);
        return (await rest.GetWithBearerAsync(path, bearer, cancellationToken).ConfigureAwait(false)).Body;
    }

    private async Task<IReadOnlyList<JsonElement>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, cancellationToken).ConfigureAwait(false);
            if (page is not { } body)
            {
                break;
            }

            items.AddRange(JsonRead.Items(body, "value"));
            next = JsonRead.Text(body, "nextLink") is { } link ? link.Replace("https://management.azure.com/", string.Empty, StringComparison.OrdinalIgnoreCase) : null;
        }

        return items;
    }

    private async Task<string> BearerAsync(CancellationToken cancellationToken)
    {
        if (token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            token = await EntraToken.GetAsync(credential, AzureApi.ArmScope, cancellationToken).ConfigureAwait(false);
        }

        return token.Token;
    }
}

/// <summary>A tag of a registry repository.</summary>
/// <param name="Name">Tag.</param>
/// <param name="Digest">Manifest digest.</param>
/// <param name="CreatedTime">When it was created.</param>
/// <param name="Record">The raw ACR tag record (for the retention inventory).</param>
public sealed record RegistryTag(string Name, string Digest, DateTimeOffset? CreatedTime, JsonElement Record);

/// <summary>
/// ACR data-plane reads with the harness credential (Entra token exchanged at <c>/oauth2/exchange</c>, then a
/// repository-scoped access token): tag lists, manifests and OCI referrers.
/// </summary>
public sealed class RegistryReader : IDisposable
{
    private const string ManifestTypes = "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json";
    private readonly TokenCredential credential;
    private readonly string loginServer;
    private readonly string? tenantId;
    private readonly JsonRest rest;
    private string? refreshToken;

    /// <summary>Creates the reader.</summary>
    /// <param name="credential">The harness credential.</param>
    /// <param name="loginServer">Registry login server, for example <c>myregistry.azurecr.io</c>.</param>
    /// <param name="tenantId">Entra tenant, when known.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public RegistryReader(TokenCredential credential, string loginServer, string? tenantId, TimeSpan timeout)
    {
        this.credential = credential;
        this.loginServer = loginServer.Trim().TrimEnd('/');
        this.tenantId = tenantId;
        rest = new JsonRest(new Uri($"https://{this.loginServer}/"), "Azure Container Registry", timeout, _ => { });
    }

    /// <summary>The registry login server.</summary>
    public string LoginServer => loginServer;

    /// <summary>Every tag of a repository, newest first; empty when the repository does not exist.</summary>
    /// <param name="repository">Repository, for example <c>apps/sandbox/web</c>.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<IReadOnlyList<RegistryTag>> ListTagsAsync(string repository, CancellationToken cancellationToken)
    {
        var access = await AccessTokenAsync($"repository:{repository}:metadata_read", cancellationToken).ConfigureAwait(false);
        var tags = new List<RegistryTag>();
        string? next = $"acr/v1/{repository}/_tags?n=100&orderby=timedesc";
        while (next is not null)
        {
            var (body, link) = await rest.GetWithBearerAsync(next, access, cancellationToken).ConfigureAwait(false);
            if (body is not { } page)
            {
                break;
            }

            tags.AddRange(JsonRead.Items(page, "tags").Select(tag => new RegistryTag(
                JsonRead.Text(tag, "name") ?? string.Empty,
                JsonRead.Text(tag, "digest") ?? string.Empty,
                JsonRead.Date(tag, "createdTime"),
                tag)));
            next = JsonRest.NextLink(link);
        }

        return tags;
    }

    /// <summary>A manifest by tag or digest, or <c>null</c> when it does not exist.</summary>
    /// <param name="repository">Repository.</param>
    /// <param name="reference">Tag or digest.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<JsonElement?> GetManifestAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        var access = await AccessTokenAsync($"repository:{repository}:pull", cancellationToken).ConfigureAwait(false);
        return (await rest.GetWithBearerAsync($"v2/{repository}/manifests/{reference}", access, cancellationToken, ManifestTypes.Split(", ")).ConfigureAwait(false)).Body;
    }

    /// <summary>The OCI referrers of a digest (an image index); empty when the registry lists none.</summary>
    /// <param name="repository">Repository.</param>
    /// <param name="digest">Subject digest.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> ListReferrersAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        var access = await AccessTokenAsync($"repository:{repository}:pull", cancellationToken).ConfigureAwait(false);
        var (body, _) = await rest.GetWithBearerAsync($"v2/{repository}/referrers/{digest}", access, cancellationToken, "application/vnd.oci.image.index.v1+json").ConfigureAwait(false);
        return body is { } index ? JsonRead.Items(index, "manifests").ToArray() : [];
    }

    /// <summary>A JSON blob by digest (for example a Sigstore bundle), or <c>null</c> when it does not exist.</summary>
    /// <param name="repository">Repository.</param>
    /// <param name="digest">Blob digest.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<JsonElement?> GetJsonBlobAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        var access = await AccessTokenAsync($"repository:{repository}:pull", cancellationToken).ConfigureAwait(false);
        return (await rest.GetWithBearerAsync($"v2/{repository}/blobs/{digest}", access, cancellationToken, "application/json", "application/octet-stream").ConfigureAwait(false)).Body;
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();

    private async Task<string> AccessTokenAsync(string scope, CancellationToken cancellationToken)
    {
        if (refreshToken is null)
        {
            var entra = await EntraToken.GetAsync(credential, AzureApi.RegistryScope, cancellationToken).ConfigureAwait(false);
            var exchange = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "access_token"),
                new("service", loginServer),
                new("access_token", entra.Token),
            };
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                exchange.Add(new("tenant", tenantId));
            }

            var refresh = await rest.PostFormAsync("oauth2/exchange", exchange, cancellationToken).ConfigureAwait(false);
            refreshToken = JsonRead.Text(refresh, "refresh_token") ?? throw new InvalidOperationException("The registry token exchange returned no refresh_token.");
        }

        var access = await rest.PostFormAsync(
            "oauth2/token",
            [
                new("grant_type", "refresh_token"),
                new("service", loginServer),
                new("scope", scope),
                new("refresh_token", refreshToken),
            ],
            cancellationToken).ConfigureAwait(false);
        return JsonRead.Text(access, "access_token") ?? throw new InvalidOperationException("The registry token endpoint returned no access_token.");
    }
}

/// <summary>Entra tokens for the raw reads; a missing credential makes the test Inconclusive, as in the harness.</summary>
public static class EntraToken
{
    /// <summary>Gets a token for one scope.</summary>
    /// <param name="credential">The harness credential.</param>
    /// <param name="scope">Scope, for example <c>https://management.azure.com/.default</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PlatformPrerequisiteException">No credential is available (Inconclusive).</exception>
    public static async Task<AccessToken> GetAsync(TokenCredential credential, string scope, CancellationToken cancellationToken)
    {
        try
        {
            return await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PlatformPrerequisiteException(AzureCredentialFactory.MissingCredentialMessage(ex.Message), ex);
        }
    }
}
