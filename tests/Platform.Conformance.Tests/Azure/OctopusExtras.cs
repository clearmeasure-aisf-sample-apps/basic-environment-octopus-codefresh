using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// Octopus calls the CAP-AZ tests need beyond <see cref="IOctopusApi"/>: the prompted variables of a config-as-code
/// runbook (checked before anything is destroyed), a short-lived worker registration token for the rebuild, and
/// cancelling a task that waits on an intervention the automation user cannot answer. Raw REST with the harness
/// settings; the API key travels only in the <c>X-Octopus-ApiKey</c> header.
/// </summary>
public sealed class OctopusExtras : IDisposable
{
    private readonly PlatformSettings settings;
    private readonly IOctopusApi octopus;
    private readonly HttpClient http;

    /// <summary>Creates the helper; the caller has already obtained <paramref name="octopus"/>, so the settings are present.</summary>
    /// <param name="settings">Harness settings and secrets.</param>
    /// <param name="octopus">The harness client, for project and environment lookups.</param>
    public OctopusExtras(PlatformSettings settings, IOctopusApi octopus)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(octopus);
        this.settings = settings;
        this.octopus = octopus;
        http = PlatformHttp.Create(new Uri(settings.OctopusUrl!.TrimEnd('/') + "/api/"), settings.TimeLimits.HttpTimeout);
    }

    private string Space => settings.OctopusSpaceId!;

    /// <summary>Names of the prompted variables a config-as-code runbook asks for in an environment (the run preview's form).</summary>
    /// <param name="project">Project slug.</param>
    /// <param name="runbook">Runbook name.</param>
    /// <param name="environment">Environment name.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<IReadOnlyList<string>> PromptedVariablesAsync(string project, string runbook, string environment, CancellationToken cancellationToken)
    {
        var projectId = (await octopus.GetProjectAsync(project, cancellationToken).ConfigureAwait(false)).Id;
        var environmentId = (await octopus.FindEnvironmentByNameAsync(environment, cancellationToken).ConfigureAwait(false))?.Id
            ?? throw new InvalidOperationException($"Octopus environment {environment} does not exist.");
        var gitRef = Uri.EscapeDataString(OctopusRunbookRunRequest.MainBranch);
        var runbooks = await GetJsonAsync($"{Space}/projects/{projectId}/{gitRef}/runbooks?take=1000", cancellationToken).ConfigureAwait(false);
        var runbookId = runbooks.GetProperty("Items").EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("Name").GetString(), runbook, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GetProperty("Id").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Runbook {runbook} is not in project {project} at {OctopusRunbookRunRequest.MainBranch}.");
        var preview = await GetJsonAsync($"{Space}/projects/{projectId}/{gitRef}/runbooks/{runbookId}/runbookRuns/preview/{environmentId}", cancellationToken).ConfigureAwait(false);
        if (!preview.TryGetProperty("Form", out var form) || !form.TryGetProperty("Elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return elements.EnumerateArray()
            .Select(element => element.TryGetProperty("Control", out var control) && control.ValueKind == JsonValueKind.Object && control.TryGetProperty("Name", out var name) ? name.GetString() : null)
            .OfType<string>()
            .ToArray();
    }

    /// <summary>
    /// A short-lived bearer token of the automation user for the Kubernetes worker chart (<c>agent.bearerToken</c>), from
    /// <c>/api/users/access-token</c> [VERIFY the method and the response field on the first live run].
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The token, or <c>null</c> when this Octopus instance does not issue one.</returns>
    public async Task<string?> CreateWorkerRegistrationTokenAsync(CancellationToken cancellationToken)
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get })
        {
            using var request = Request(method, "users/access-token");
            if (method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            foreach (var property in new[] { "AccessToken", "Token", "BearerToken" })
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } issued)
                {
                    return issued;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>Cancels a task when it has not completed; errors are ignored (best effort, used in cleanup).</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task CancelIfRunningAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await octopus.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task.IsCompleted)
        {
            return;
        }

        using var request = Request(HttpMethod.Post, $"{Space}/tasks/{Uri.EscapeDataString(taskId)}/cancel");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformApiException("Octopus", "POST", $"{Space}/tasks/{taskId}/cancel", response.StatusCode, "the task could not be cancelled; cancel it in the Octopus UI");
        }
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Octopus-ApiKey", settings.Secrets.OctopusApiKey);
        return request;
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformApiException("Octopus", "GET", path, response.StatusCode, body.Length <= 500 ? body : body[..500]);
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
