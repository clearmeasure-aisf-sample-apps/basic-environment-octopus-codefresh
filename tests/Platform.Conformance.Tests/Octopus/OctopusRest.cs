using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>An HTTP answer of Octopus or GitHub: status, body and whether it succeeded.</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Body">Response body (JSON or text).</param>
public sealed record RestAnswer(int StatusCode, string Body)
{
    /// <summary><c>true</c> for a 2xx status.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The body parsed as JSON.</summary>
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>
/// Raw REST calls of the Octopus capability tests beyond <see cref="IOctopusApi"/>: releases and their deployments,
/// deployment freezes, freeze overrides, task cancellation, projects' included sets, library variable sets, triggers,
/// and the commit files of the environment repository on GitHub. Settings and secrets come from the harness; no call
/// prints a secret.
/// </summary>
public sealed class OctopusRest : IDisposable
{
    private readonly PlatformSettings settings;
    private readonly HttpClient http;

    /// <summary>Creates the helper; the caller has checked the Octopus prerequisites.</summary>
    /// <param name="settings">Harness settings and secrets.</param>
    public OctopusRest(PlatformSettings settings)
    {
        this.settings = settings;
        http = PlatformHttp.Create(new Uri(settings.OctopusUrl!.TrimEnd('/') + "/"), settings.TimeLimits.HttpTimeout);
    }

    private string Space => settings.OctopusSpaceId!;

    /// <summary>Sends a request to the Octopus API and returns the answer without throwing on an HTTP error.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path below the server, for example <c>/api/Spaces-1/projects/all</c>.</param>
    /// <param name="body">JSON body, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<RestAnswer> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path.TrimStart('/'), UriKind.Relative));
        request.Headers.Add("X-Octopus-ApiKey", settings.Secrets.OctopusApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new RestAnswer((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>GETs JSON from the Octopus API and fails with the status when the call fails.</summary>
    /// <param name="path">Path below the server.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        answer.IsSuccess.ShouldBeTrue($"Octopus GET {path} answered {answer.StatusCode}: {Shorten(answer.Body)}");
        return answer.Json;
    }

    /// <summary>The raw project resource (included library variable sets, persistence settings).</summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonElement> GetProjectJsonAsync(string projectId, CancellationToken cancellationToken) =>
        GetAsync($"/api/{Space}/projects/{projectId}", cancellationToken);

    /// <summary>Every project of the space.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> GetAllProjectsAsync(CancellationToken cancellationToken) =>
        (await GetAsync($"/api/{Space}/projects/all", cancellationToken).ConfigureAwait(false)).EnumerateArray().ToArray();

    /// <summary>Every project group of the space.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> GetAllProjectGroupsAsync(CancellationToken cancellationToken) =>
        (await GetAsync($"/api/{Space}/projectgroups/all", cancellationToken).ConfigureAwait(false)).EnumerateArray().ToArray();

    /// <summary>A library variable set by exact name, or <c>null</c>.</summary>
    /// <param name="name">Set name, for example <c>Platform Environment</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<JsonElement?> FindLibraryVariableSetAsync(string name, CancellationToken cancellationToken)
    {
        var page = await GetAsync($"/api/{Space}/libraryvariablesets?partialName={Uri.EscapeDataString(name)}&take=50", cancellationToken).ConfigureAwait(false);
        foreach (var item in page.GetProperty("Items").EnumerateArray())
        {
            if (item.GetProperty("Name").GetString() == name)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>The newest releases of a project, newest first.</summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="take">How many.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> GetReleasesAsync(string projectId, int take, CancellationToken cancellationToken) =>
        (await GetAsync($"/api/{Space}/projects/{projectId}/releases?take={take}", cancellationToken).ConfigureAwait(false))
            .GetProperty("Items").EnumerateArray().ToArray();

    /// <summary>A release with its selected packages.</summary>
    /// <param name="releaseId">Release ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonElement> GetReleaseAsync(string releaseId, CancellationToken cancellationToken) =>
        GetAsync($"/api/{Space}/releases/{releaseId}", cancellationToken);

    /// <summary>The deployments of a release, newest first.</summary>
    /// <param name="releaseId">Release ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> GetReleaseDeploymentsAsync(string releaseId, CancellationToken cancellationToken) =>
        (await GetAsync($"/api/{Space}/releases/{releaseId}/deployments?take=100", cancellationToken).ConfigureAwait(false))
            .GetProperty("Items").EnumerateArray().ToArray();

    /// <summary>The newest deployments of a project to an environment, newest first.</summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="environmentId">Environment ID.</param>
    /// <param name="take">How many.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> GetDeploymentsAsync(string projectId, string environmentId, int take, CancellationToken cancellationToken) =>
        (await GetAsync($"/api/{Space}/deployments?projects={projectId}&environments={environmentId}&take={take}", cancellationToken).ConfigureAwait(false))
            .GetProperty("Items").EnumerateArray().ToArray();

    /// <summary>The package versions a release selected, by package reference name (for example <c>web</c>).</summary>
    /// <param name="release">Release resource.</param>
    public static IReadOnlyDictionary<string, string> SelectedPackages(JsonElement release)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (release.TryGetProperty("SelectedPackages", out var packages) && packages.ValueKind == JsonValueKind.Array)
        {
            foreach (var package in packages.EnumerateArray())
            {
                var name = package.TryGetProperty("PackageReferenceName", out var reference) && reference.GetString() is { Length: > 0 } referenceName
                    ? referenceName
                    : package.GetProperty("ActionName").GetString() ?? string.Empty;
                result[name] = package.GetProperty("Version").GetString() ?? string.Empty;
            }
        }

        return result;
    }

    /// <summary>Starts a deployment through the executions API, optionally overriding deployment freezes.</summary>
    /// <param name="project">Project name.</param>
    /// <param name="releaseVersion">Release version.</param>
    /// <param name="environment">Environment name.</param>
    /// <param name="freezeNames">Freezes to override, or empty.</param>
    /// <param name="overrideReason">Reason recorded for the override.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer; a refused deployment is not an exception.</returns>
    public Task<RestAnswer> CreateDeploymentAsync(string project, string releaseVersion, string environment, IReadOnlyList<string> freezeNames, string? overrideReason, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["SpaceIdOrName"] = Space,
            ["ProjectName"] = project,
            ["ReleaseVersion"] = releaseVersion,
            ["EnvironmentNames"] = new[] { environment },
        };
        if (freezeNames.Count > 0)
        {
            body["DeploymentFreezeNames"] = freezeNames;
            body["DeploymentFreezeOverrideReason"] = overrideReason;
        }

        return SendAsync(HttpMethod.Post, $"/api/{Space}/deployments/create/untenanted/v1", body, cancellationToken);
    }

    /// <summary>The server task IDs in an executions-API answer.</summary>
    /// <param name="answer">Answer of <see cref="CreateDeploymentAsync"/>.</param>
    public static IReadOnlyList<string> TaskIds(RestAnswer answer)
    {
        var ids = new List<string>();
        Collect(answer.Json, ids);
        return ids;

        static void Collect(JsonElement element, List<string> found)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name is "ServerTaskId" or "TaskId" && property.Value.GetString() is { Length: > 0 } id)
                        {
                            found.Add(id);
                        }
                        else
                        {
                            Collect(property.Value, found);
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, found);
                    }

                    break;
            }
        }
    }

    /// <summary>Cancels a server task (<c>POST /api/tasks/{id}/cancel</c>); a completed task is left alone.</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task CancelTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Post, $"/api/tasks/{taskId}/cancel", new { }, cancellationToken).ConfigureAwait(false);
        (answer.IsSuccess || answer.StatusCode == 400).ShouldBeTrue($"cancelling {taskId} answered {answer.StatusCode}: {Shorten(answer.Body)}");
    }

    /// <summary>Submits a manual intervention with <c>Result=Abort</c> after taking responsibility.</summary>
    /// <param name="interruptionId">Interruption ID.</param>
    /// <param name="notes">Notes recorded with the answer.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task AbortInterruptionAsync(string interruptionId, string notes, CancellationToken cancellationToken)
    {
        var take = await SendAsync(HttpMethod.Put, $"/api/{Space}/interruptions/{interruptionId}/responsible", new { }, cancellationToken).ConfigureAwait(false);
        take.IsSuccess.ShouldBeTrue($"taking responsibility for {interruptionId} answered {take.StatusCode}: {Shorten(take.Body)}");
        var submit = await SendAsync(HttpMethod.Post, $"/api/{Space}/interruptions/{interruptionId}/submit", new { Notes = notes, Result = "Abort" }, cancellationToken).ConfigureAwait(false);
        submit.IsSuccess.ShouldBeTrue($"aborting {interruptionId} answered {submit.StatusCode}: {Shorten(submit.Body)}");
    }

    /// <summary>Creates a project deployment freeze and returns its ID.</summary>
    /// <param name="name">Freeze name.</param>
    /// <param name="projectId">Owning project.</param>
    /// <param name="environmentId">Frozen environment.</param>
    /// <param name="start">Start.</param>
    /// <param name="end">End.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string> CreateProjectFreezeAsync(string name, string projectId, string environmentId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["Name"] = name,
            ["OwnerId"] = projectId,
            ["Start"] = start.UtcDateTime.ToString("O"),
            ["End"] = end.UtcDateTime.ToString("O"),
            ["ProjectEnvironmentScope"] = new Dictionary<string, string[]> { [projectId] = [environmentId] },
        };
        var answer = await SendAsync(HttpMethod.Post, "/api/deploymentfreezes", body, cancellationToken).ConfigureAwait(false);
        answer.IsSuccess.ShouldBeTrue($"creating deployment freeze {name} answered {answer.StatusCode}: {Shorten(answer.Body)}");
        return answer.Json.GetProperty("Id").GetString()!;
    }

    /// <summary>Deletes a deployment freeze.</summary>
    /// <param name="freezeId">Freeze ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task DeleteFreezeAsync(string freezeId, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Delete, $"/api/deploymentfreezes/{freezeId}", null, cancellationToken).ConfigureAwait(false);
        (answer.IsSuccess || answer.StatusCode == 404).ShouldBeTrue($"deleting deployment freeze {freezeId} answered {answer.StatusCode}: {Shorten(answer.Body)}");
    }

    /// <summary>The triggers of a project.</summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<JsonElement>> GetProjectTriggersAsync(string projectId, CancellationToken cancellationToken) =>
        (await GetAsync($"/api/{Space}/projects/{projectId}/triggers?take=100", cancellationToken).ConfigureAwait(false))
            .GetProperty("Items").EnumerateArray().ToArray();

    /// <summary>Files changed by one commit of a GitHub repository (<c>GET /repos/{repo}/commits/{sha}</c>).</summary>
    /// <param name="repository">owner/name.</param>
    /// <param name="sha">Commit SHA.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<string>> GitHubCommitFilesAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{repository}/commits/{sha}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Secrets.GitHubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.IsSuccessStatusCode.ShouldBeTrue($"GitHub GET commit {sha} of {repository} answered {(int)response.StatusCode}: {Shorten(body)}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("filename").GetString() ?? string.Empty).ToArray();
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    private static string Shorten(string text) => text.Length <= 500 ? text : string.Concat(text.AsSpan(0, 500), "…");
}
