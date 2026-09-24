using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>Response of an HTTP probe of an app host.</summary>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="Location">The <c>Location</c> header, if any.</param>
/// <param name="Body">Response body (at most 64 KiB).</param>
/// <param name="Certificate">The server certificate of an HTTPS request, validated against the system trust store.</param>
public sealed record HostResponse(HttpStatusCode StatusCode, Uri? Location, string Body, X509Certificate2? Certificate);

/// <summary>
/// Raw REST calls of the GitOps tests beyond the harness clients: Key Vault secret writes (the sandbox tdd vault only),
/// a GitHub file deletion, Octopus release and deployment lookups, and HTTP probes of app hosts that never follow
/// redirects. TLS is always validated against the system trust store, and no secret is ever written to a message.
/// </summary>
public sealed class GitOpsRest : IDisposable
{
    private static readonly string[] VaultScope = ["https://vault.azure.net/.default"];
    private readonly PlatformSettings settings;
    private readonly HttpClient http;

    /// <summary>Creates the helper.</summary>
    /// <param name="settings">Harness settings and secrets.</param>
    public GitOpsRest(PlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        http = PlatformHttp.Create(new Uri("https://localhost/"), settings.TimeLimits.HttpTimeout);
    }

    /// <summary>Sets a secret in a Key Vault (a new version); needs Key Vault Secrets Officer on that vault.</summary>
    /// <param name="vaultName">Vault name.</param>
    /// <param name="secretName">Secret name.</param>
    /// <param name="value">Secret value; never logged.</param>
    /// <param name="runId">Run ID, recorded as a tag.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task SetVaultSecretAsync(string vaultName, string secretName, string value, string runId, CancellationToken cancellationToken)
    {
        var token = await VaultTokenAsync(cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["value"] = value,
            ["contentType"] = "text/plain",
            ["tags"] = new JsonObject { ["conformance-run"] = runId },
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"https://{vaultName}.vault.azure.net/secrets/{secretName}?api-version=7.4"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Key Vault", "PUT", $"{vaultName}/secrets/{secretName}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a file on a branch through the GitHub contents API (the harness client only writes files).</summary>
    /// <param name="repository"><c>owner/name</c>.</param>
    /// <param name="branch">Branch.</param>
    /// <param name="path">File path.</param>
    /// <param name="blobSha">The file's blob SHA on that branch.</param>
    /// <param name="message">Commit message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The SHA of the new commit.</returns>
    public async Task<string> DeleteGitHubFileAsync(string repository, string branch, string path, string blobSha, string message, CancellationToken cancellationToken)
    {
        settings.Check("the GitHub API").Secret(EnvironmentVariableNames.GitHubToken, settings.Secrets.GitHubToken).ThrowIfMissing();
        var body = new JsonObject { ["message"] = message, ["sha"] = blobSha, ["branch"] = branch };
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"https://api.github.com/repos/{repository}/contents/{path}"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Secrets.GitHubToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "GitHub", "DELETE", $"repos/{repository}/contents/{path}", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return GitOpsCluster.Text(document.RootElement, "commit", "sha") ?? string.Empty;
    }

    /// <summary>
    /// The release of an Octopus project whose notes name an app commit (first line <c>app-commit: &lt;sha&gt;</c>, written by
    /// the app's release pipeline), or <c>null</c> while there is none.
    /// </summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="commitSha">Commit SHA.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<(string Id, string Version)?> FindReleaseByCommitAsync(string projectId, string commitSha, CancellationToken cancellationToken)
    {
        using var document = await OctopusGetAsync($"/api/{settings.OctopusSpaceId}/projects/{projectId}/releases?take=30", cancellationToken).ConfigureAwait(false);
        foreach (var release in document.RootElement.GetProperty("Items").EnumerateArray())
        {
            var notes = GitOpsCluster.Text(release, "ReleaseNotes") ?? string.Empty;
            if (notes.Contains(commitSha, StringComparison.OrdinalIgnoreCase))
            {
                return (GitOpsCluster.Text(release, "Id")!, GitOpsCluster.Text(release, "Version")!);
            }
        }

        return null;
    }

    /// <summary>The server task of a release's deployment to an environment, or <c>null</c> while there is none.</summary>
    /// <param name="releaseId">Release ID.</param>
    /// <param name="environmentId">Environment ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string?> FindDeploymentTaskAsync(string releaseId, string environmentId, CancellationToken cancellationToken)
    {
        using var document = await OctopusGetAsync($"/api/{settings.OctopusSpaceId}/releases/{releaseId}/deployments?take=30", cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("Items").EnumerateArray()
            .Where(deployment => GitOpsCluster.Text(deployment, "EnvironmentId") == environmentId)
            .Select(deployment => GitOpsCluster.Text(deployment, "TaskId"))
            .FirstOrDefault();
    }

    /// <summary>Cancels an Octopus server task (a deployment held at a guided-failure prompt).</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task CancelOctopusTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.OctopusUrl!.TrimEnd('/') + $"/api/{settings.OctopusSpaceId}/tasks/{taskId}/cancel"));
        request.Headers.Add("X-Octopus-ApiKey", settings.Secrets.OctopusApiKey);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Octopus", "POST", $"tasks/{taskId}/cancel", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request to an app host without following redirects; HTTPS certificates are validated as usual. Reads that fail
    /// transiently are retried (<see cref="TransientRetryHandler"/>); writes are sent once.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="url">URL.</param>
    /// <param name="jsonBody">Optional JSON body.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<HostResponse> SendToHostAsync(HttpMethod method, Uri url, string? jsonBody, CancellationToken cancellationToken)
    {
        X509Certificate2? certificate = null;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                {
                    if (presented is not null)
                    {
                        certificate = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
                    }

                    return errors == SslPolicyErrors.None;
                },
            },
        };
        // Reads (GET, HEAD) that fail transiently, such as a proxy reset or a 503 during a rollout, are retried within the timeout.
        using var client = new HttpClient(new TransientRetryHandler(handler), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(PlatformHttp.UserAgent);
        using var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HostResponse(response.StatusCode, response.Headers.Location, body.Length > 65536 ? body[..65536] : body, certificate);
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    private async Task<string> VaultTokenAsync(CancellationToken cancellationToken)
    {
        settings.Check("the Key Vault data plane").Setting($"{nameof(PlatformSettings.AzureSubscriptionId)} (or {EnvironmentVariableNames.AzureSubscriptionId})", settings.AzureSubscriptionId).ThrowIfMissing();
        try
        {
            var token = await AzureCredentialFactory.Create(settings).GetTokenAsync(new TokenRequestContext(VaultScope), cancellationToken).ConfigureAwait(false);
            return token.Token;
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PlatformPrerequisiteException(AzureCredentialFactory.MissingCredentialMessage(ex.Message), ex);
        }
    }

    private async Task<JsonDocument> OctopusGetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        settings.Check("the Octopus API")
            .Setting(nameof(PlatformSettings.OctopusUrl), settings.OctopusUrl)
            .Setting(nameof(PlatformSettings.OctopusSpaceId), settings.OctopusSpaceId)
            .Secret(EnvironmentVariableNames.OctopusApiKey, settings.Secrets.OctopusApiKey)
            .ThrowIfMissing();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.OctopusUrl!.TrimEnd('/') + pathAndQuery));
        request.Headers.Add("X-Octopus-ApiKey", settings.Secrets.OctopusApiKey);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Octopus", "GET", pathAndQuery, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string system, string method, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new PlatformApiException(system, method, path, response.StatusCode, detail.Length > 500 ? detail[..500] : detail);
    }
}
