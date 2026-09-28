using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Kit;

/// <summary>
/// Read calls the Kit live tests need beyond the harness clients: Octopus lists, Codefresh projects, GitHub commit
/// statuses and merges, ARM resource lists and the registry catalogue. Raw REST with the harness settings and secrets;
/// every call names the system in its errors and never prints a secret.
/// </summary>
public sealed class KitRest : IDisposable
{
    private static readonly string[] ArmScope = ["https://management.azure.com/.default"];
    private static readonly string[] RegistryScope = ["https://containerregistry.azure.net/.default"];
    private readonly PlatformSettings settings;
    private readonly HttpClient http;

    /// <summary>Creates the helper.</summary>
    /// <param name="settings">Harness settings and secrets.</param>
    public KitRest(PlatformSettings settings)
    {
        this.settings = settings;
        http = PlatformHttp.Create(new Uri("https://localhost/"), settings.TimeLimits.HttpTimeout);
    }

    /// <summary>GET /api/{space}/{resource}/all of Octopus (projects, projectgroups).</summary>
    /// <param name="resource">Collection name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> OctopusAllAsync(string resource, CancellationToken cancellationToken)
    {
        using var document = await OctopusGetAsync($"/api/{settings.OctopusSpaceId}/{resource}/all", cancellationToken).ConfigureAwait(false);
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    /// <summary>GET a path of the Octopus API.</summary>
    /// <param name="pathAndQuery">Path such as <c>/api/Spaces-1/projects/all</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonDocument> OctopusGetAsync(string pathAndQuery, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Get, new Uri(settings.OctopusUrl!.TrimEnd('/') + pathAndQuery), request => request.Headers.Add("X-Octopus-ApiKey", settings.Secrets.OctopusApiKey), null, "Octopus", cancellationToken);

    /// <summary>
    /// The release of an Octopus project whose notes name the app commit (first line <c>app-commit: &lt;sha&gt;</c>, ADR-IR23),
    /// or <c>null</c>.
    /// </summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="commitSha">40-hex commit SHA.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<(string Id, string Version)?> FindReleaseByCommitAsync(string projectId, string commitSha, CancellationToken cancellationToken)
    {
        using var document = await OctopusGetAsync($"/api/{settings.OctopusSpaceId}/projects/{projectId}/releases?take=30", cancellationToken).ConfigureAwait(false);
        foreach (var release in document.RootElement.GetProperty("Items").EnumerateArray())
        {
            var notes = release.TryGetProperty("ReleaseNotes", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            if (notes.Contains(commitSha, StringComparison.OrdinalIgnoreCase))
            {
                return (release.GetProperty("Id").GetString()!, release.GetProperty("Version").GetString()!);
            }
        }

        return null;
    }

    /// <summary>The task of a release's deployment to an environment, or <c>null</c> while there is none.</summary>
    /// <param name="releaseId">Release ID.</param>
    /// <param name="environmentId">Environment ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string?> FindDeploymentTaskAsync(string releaseId, string environmentId, CancellationToken cancellationToken)
    {
        using var document = await OctopusGetAsync($"/api/{settings.OctopusSpaceId}/releases/{releaseId}/deployments?take=30", cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("Items").EnumerateArray()
            .Where(deployment => deployment.GetProperty("EnvironmentId").GetString() == environmentId)
            .Select(deployment => deployment.GetProperty("TaskId").GetString())
            .FirstOrDefault();
    }

    /// <summary>Whether a Codefresh project exists (<c>GET /projects/name/{name}</c>) [VERIFY 404 for an unknown name].</summary>
    /// <param name="name">Project name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<bool> CodefreshProjectExistsAsync(string name, CancellationToken cancellationToken)
    {
        using var request = CodefreshRequest($"/projects/name/{Uri.EscapeDataString(name)}");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Every Codefresh project of the account with its number of pipelines (<c>GET /projects</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<(string Name, int Pipelines)>> CodefreshProjectsAsync(CancellationToken cancellationToken)
    {
        using var request = CodefreshRequest("/projects?limit=1000");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement : document.RootElement.GetProperty("projects");
        return items.EnumerateArray()
            .Select(project => (
                project.GetProperty("projectName").GetString() ?? string.Empty,
                project.TryGetProperty("pipelinesNumber", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0))
            .ToArray();
    }

    /// <summary>The state of one commit status context (success, pending, failure, error), or <c>null</c> while absent.</summary>
    /// <param name="repository">owner/name.</param>
    /// <param name="sha">Commit SHA.</param>
    /// <param name="context">Status context, for example <c>codefresh/ci</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string?> CommitStatusAsync(string repository, string sha, string context, CancellationToken cancellationToken)
    {
        using var document = await GitHubAsync(HttpMethod.Get, $"/repos/{repository}/commits/{sha}/status", null, cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("statuses").EnumerateArray()
            .Where(status => status.GetProperty("context").GetString() == context)
            .Select(status => status.GetProperty("state").GetString())
            .FirstOrDefault();
    }

    /// <summary>Merges a pull request and returns the merge commit SHA.</summary>
    /// <param name="repository">owner/name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="title">Merge commit title.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string> MergePullRequestAsync(string repository, int number, string title, CancellationToken cancellationToken)
    {
        using var document = await GitHubAsync(HttpMethod.Put, $"/repos/{repository}/pulls/{number}/merge", new { merge_method = "merge", commit_title = title }, cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("sha").GetString()!;
    }

    /// <summary>Names of the resources of one type in a resource group; empty when the group does not exist.</summary>
    /// <param name="resourceGroup">Resource group.</param>
    /// <param name="providerType">For example <c>Microsoft.KeyVault/vaults</c>.</param>
    /// <param name="apiVersion">ARM API version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<string>> ArmNamesAsync(string resourceGroup, string providerType, string apiVersion, CancellationToken cancellationToken)
    {
        var token = await AzureCredentialFactory.Create(settings).GetTokenAsync(new TokenRequestContext(ArmScope), cancellationToken).ConfigureAwait(false);
        var uri = new Uri($"https://management.azure.com/subscriptions/{settings.AzureSubscriptionId}/resourceGroups/{resourceGroup}/providers/{providerType}?api-version={apiVersion}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("value").EnumerateArray().Select(item => item.GetProperty("name").GetString() ?? string.Empty).ToArray();
    }

    /// <summary>Every repository of the registry (<c>/v2/_catalog</c> after the ACR token exchange).</summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<IReadOnlyList<string>> RegistryRepositoriesAsync(CancellationToken cancellationToken)
    {
        var login = settings.RegistryLoginServer!;
        var aad = await AzureCredentialFactory.Create(settings).GetTokenAsync(new TokenRequestContext(RegistryScope), cancellationToken).ConfigureAwait(false);
        var exchangeForm = new Dictionary<string, string>
        {
            ["grant_type"] = "access_token",
            ["service"] = login,
            ["access_token"] = aad.Token,
        };
        if (!PlatformSettings.IsMissing(settings.AzureTenantId))
        {
            exchangeForm["tenant"] = settings.AzureTenantId!;
        }

        using var exchange = new FormUrlEncodedContent(exchangeForm);
        using var exchanged = await http.PostAsync(new Uri($"https://{login}/oauth2/exchange"), exchange, cancellationToken).ConfigureAwait(false);
        exchanged.EnsureSuccessStatusCode();
        var refresh = (await exchanged.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false)).GetProperty("refresh_token").GetString()!;
        using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["service"] = login,
            ["scope"] = "registry:catalog:*",
            ["refresh_token"] = refresh,
        });
        using var tokenResponse = await http.PostAsync(new Uri($"https://{login}/oauth2/token"), tokenForm, cancellationToken).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();
        var access = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false)).GetProperty("access_token").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{login}/v2/_catalog?n=1000"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var catalog = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return catalog.TryGetProperty("repositories", out var repositories) && repositories.ValueKind == JsonValueKind.Array
            ? repositories.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    private HttpRequestMessage CodefreshRequest(string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.CodefreshUrl.TrimEnd('/') + pathAndQuery));
        request.Headers.TryAddWithoutValidation("Authorization", settings.Secrets.CodefreshApiKey);
        return request;
    }

    private Task<JsonDocument> GitHubAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        SendJsonAsync(method, new Uri("https://api.github.com" + path), request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Secrets.GitHubToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }, body, "GitHub", cancellationToken);

    // A GET answered 403, 429 or 5xx is retried: GitHub's secondary rate limit answers 403 to a poll now and then (run
    // r20260928t0307 failed 26 minutes in on one), and a transient answer must not fail an hour-long pass. Writes are never
    // retried: a repeated POST or PUT is not safe.
    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, Uri uri, Action<HttpRequestMessage> authorize, object? body, string system, CancellationToken cancellationToken)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            authorize(request);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            var transient = status is 403 or 429 || status >= 500;
            if (method != HttpMethod.Get || !transient || attempt == attempts)
            {
                throw new InvalidOperationException($"{system} {method} {uri.AbsolutePath} answered {status} {response.ReasonPhrase}");
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(15 * attempt);
            TestContext.Out.WriteLine($"{system} {method} {uri.AbsolutePath} answered {status}; retry {attempt}/{attempts - 1} in {delay.TotalSeconds:0} s");
            await Task.Delay(delay < TimeSpan.FromMinutes(2) ? delay : TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
    }
}
