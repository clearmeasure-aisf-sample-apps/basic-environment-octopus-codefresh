using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness.Clients;

/// <summary>REST implementation of <see cref="ICodefreshApi"/>.</summary>
public sealed class CodefreshApi : ICodefreshApi, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RestClient rest;
    private readonly IClock clock;
    private readonly TimeSpan pollInterval;

    /// <summary>Creates the client over an <see cref="HttpClient"/> whose base address is the API root and that sends the key.</summary>
    /// <param name="http">Configured HTTP client; owned and disposed by this instance.</param>
    /// <param name="clock">Clock for waits; the system clock when omitted.</param>
    /// <param name="pollInterval">Pause between build polls (default 10 s).</param>
    public CodefreshApi(HttpClient http, IClock? clock = null, TimeSpan? pollInterval = null)
    {
        rest = new RestClient(http, "Codefresh", Json);
        this.clock = clock ?? SystemClock.Instance;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Creates a client for <paramref name="apiUrl"/> that authenticates with <paramref name="apiKey"/>.</summary>
    /// <param name="apiUrl">API base, for example <c>https://g.codefresh.io/api</c>.</param>
    /// <param name="apiKey">API key; sent only as the <c>Authorization</c> header.</param>
    /// <param name="timeout">Request timeout.</param>
    /// <param name="clock">Clock for waits.</param>
    /// <param name="pollInterval">Pause between build polls.</param>
    /// <param name="handler">Message handler (unit tests pass a stub).</param>
    public static CodefreshApi Create(string apiUrl, string apiKey, TimeSpan timeout, IClock? clock = null, TimeSpan? pollInterval = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var http = PlatformHttp.Create(new Uri(apiUrl), timeout, handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", apiKey);
        return new CodefreshApi(http, clock, pollInterval);
    }

    /// <inheritdoc />
    public async Task<CodefreshUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var user = await rest.GetAsync<JsonElement>("user", cancellationToken).ConfigureAwait(false);
        var accounts = user.TryGetProperty("account", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(account => Text(account, "name")).OfType<string>().ToArray()
            : [];
        return new CodefreshUser(Text(user, "_id") ?? Text(user, "id") ?? "", Text(user, "userName") ?? "", Text(user, "activeAccountName"), accounts);
    }

    /// <inheritdoc />
    public async Task<string> RunPipelineAsync(string pipelineName, CodefreshRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
        ArgumentNullException.ThrowIfNull(request);
        var body = new RunBody(request.Branch, request.Trigger, request.Variables.Count == 0 ? null : request.Variables);
        var buildId = await rest.SendAsync<JsonElement>(HttpMethod.Post, $"pipelines/run/{Uri.EscapeDataString(pipelineName)}", body, cancellationToken).ConfigureAwait(false);
        var id = buildId.ValueKind == JsonValueKind.String ? buildId.GetString() : Text(buildId, "id") ?? Text(buildId, "_id");
        return string.IsNullOrWhiteSpace(id)
            ? throw new InvalidOperationException($"Codefresh accepted the run of {pipelineName} but returned no build ID ({buildId.GetRawText()}).")
            : id;
    }

    /// <inheritdoc />
    public async Task<CodefreshBuild> GetBuildAsync(string buildId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        var build = await rest.GetAsync<JsonElement>($"builds/{Uri.EscapeDataString(buildId)}", cancellationToken).ConfigureAwait(false);
        return ReadBuild(build);
    }

    /// <inheritdoc />
    public Task<CodefreshBuild> WaitForBuildAsync(string buildId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Poll.UntilAsync(
            token => GetBuildAsync(buildId, token),
            build => build.IsTerminal,
            timeout,
            pollInterval,
            $"Codefresh build {buildId} to finish ({string.Join(", ", CodefreshBuildStatuses.Terminal)})",
            clock,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task TerminateBuildAsync(string buildId, CancellationToken cancellationToken = default)
    {
        var build = await GetBuildAsync(buildId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(build.ProgressId))
        {
            throw new InvalidOperationException($"Codefresh build {buildId} has no progress ID to terminate.");
        }

        await rest.SendAsync(HttpMethod.Delete, $"progress/{Uri.EscapeDataString(build.ProgressId)}", body: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodefreshBuild>> ListBuildsAsync(string pipelineName, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
        var pipeline = await rest.GetAsync<JsonElement>($"pipelines/{Uri.EscapeDataString(pipelineName)}", cancellationToken).ConfigureAwait(false);
        var pipelineId = pipeline.TryGetProperty("metadata", out var metadata) ? Text(metadata, "id") : null;
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            throw new InvalidOperationException($"Codefresh pipeline {pipelineName} has no metadata.id.");
        }

        var page = await rest.GetAsync<JsonElement>($"workflow?pipeline={Uri.EscapeDataString(pipelineId)}&limit={Math.Max(1, limit)}&page=1", cancellationToken).ConfigureAwait(false);
        return page.TryGetProperty("workflows", out var workflows) && workflows.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array
            ? docs.EnumerateArray().Select(ReadBuild).ToArray()
            : [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodefreshRuntimeEnvironment>> GetRuntimeEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
        var response = await rest.GetAsync<JsonElement>("runtime-environments", cancellationToken).ConfigureAwait(false);
        return Items(response)
            .Select(runtime => runtime.TryGetProperty("metadata", out var metadata)
                ? new CodefreshRuntimeEnvironment(Text(metadata, "name") ?? "", metadata.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.True)
                : new CodefreshRuntimeEnvironment(Text(runtime, "name") ?? "", false))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodefreshAgent>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        var response = await rest.GetAsync<JsonElement>("agents", cancellationToken).ConfigureAwait(false);
        return Items(response)
            .Select(agent =>
            {
                var runtimes = agent.TryGetProperty("runtimes", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                    : [];
                var status = agent.TryGetProperty("status", out var health) ? health : default;
                return new CodefreshAgent(
                    Text(agent, "id") ?? Text(agent, "_id") ?? "",
                    Text(agent, "name") ?? "",
                    runtimes,
                    status.ValueKind == JsonValueKind.Object ? Text(status, "healthStatus") : null,
                    status.ValueKind == JsonValueKind.Object ? Date(status, "reportedAt") : null);
            })
            .ToArray();
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();

    private static CodefreshBuild ReadBuild(JsonElement build) => new()
    {
        Id = Text(build, "id") ?? Text(build, "_id") ?? "",
        Status = Text(build, "status") ?? "",
        ProgressId = Text(build, "progress"),
        PipelineName = Text(build, "pipelineName") ?? Text(build, "serviceName"),
        Branch = Text(build, "branchName") ?? Text(build, "branch"),
        Revision = Text(build, "revision"),
        Created = Date(build, "created"),
        Finished = Date(build, "finished"),
    };

    private static IEnumerable<JsonElement> Items(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Array)
        {
            return response.EnumerateArray();
        }

        foreach (var name in new[] { "docs", "items", "runtimeEnvironments" })
        {
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                return list.EnumerateArray();
            }
        }

        return [];
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private sealed record RunBody(string? Branch, string? Trigger, IReadOnlyDictionary<string, string>? Variables);
}
