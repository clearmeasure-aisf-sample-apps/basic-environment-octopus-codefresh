using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness.Clients;

/// <summary>REST implementation of <see cref="IOctopusApi"/>.</summary>
public sealed partial class OctopusApi : IOctopusApi, IDisposable
{
    /// <summary>Header that carries the API key.</summary>
    public const string ApiKeyHeader = "X-Octopus-ApiKey";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RestClient rest;
    private readonly string spaceId;
    private readonly IClock clock;
    private readonly TimeSpan pollInterval;

    /// <summary>Creates the client over an <see cref="HttpClient"/> whose base address is the server's <c>/api/</c> and that sends the API key.</summary>
    /// <param name="http">Configured HTTP client; owned and disposed by this instance.</param>
    /// <param name="spaceId">Space ID, for example <c>Spaces-1</c>.</param>
    /// <param name="clock">Clock for waits; the system clock when omitted.</param>
    /// <param name="pollInterval">Pause between task polls (default 10 s).</param>
    public OctopusApi(HttpClient http, string spaceId, IClock? clock = null, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        rest = new RestClient(http, "Octopus", Json);
        this.spaceId = spaceId;
        this.clock = clock ?? SystemClock.Instance;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
    }

    private string Space => Uri.EscapeDataString(spaceId);

    /// <summary>Creates a client for <paramref name="serverUrl"/> that authenticates with <paramref name="apiKey"/>.</summary>
    /// <param name="serverUrl">Octopus server URL, for example <c>https://example.octopus.app</c>.</param>
    /// <param name="spaceId">Space ID.</param>
    /// <param name="apiKey">API key; sent only as the <c>X-Octopus-ApiKey</c> header.</param>
    /// <param name="timeout">Request timeout.</param>
    /// <param name="clock">Clock for waits.</param>
    /// <param name="pollInterval">Pause between task polls.</param>
    /// <param name="handler">Message handler (unit tests pass a stub).</param>
    public static OctopusApi Create(string serverUrl, string spaceId, string apiKey, TimeSpan timeout, IClock? clock = null, TimeSpan? pollInterval = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var http = PlatformHttp.Create(new Uri(serverUrl.TrimEnd('/') + "/api/"), timeout, handler);
        http.DefaultRequestHeaders.Add(ApiKeyHeader, apiKey);
        return new OctopusApi(http, spaceId, clock, pollInterval);
    }

    /// <inheritdoc />
    public Task<OctopusSpace> GetSpaceAsync(CancellationToken cancellationToken = default) =>
        rest.GetAsync<OctopusSpace>($"spaces/{Space}", cancellationToken);

    /// <inheritdoc />
    public Task<OctopusProject> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrSlug);
        return rest.GetAsync<OctopusProject>($"{Space}/projects/{Escape(idOrSlug)}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OctopusEnvironment?> FindEnvironmentByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var page = await rest.GetAsync<Collection<OctopusEnvironment>>($"{Space}/environments?name={Escape(name)}&take=100", cancellationToken).ConfigureAwait(false);
        return page.Items.FirstOrDefault(environment => string.Equals(environment.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OctopusTask>> GetTasksAsync(OctopusTaskQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            parameters.Add($"project={Escape(await ResolveProjectIdAsync(query.Project, cancellationToken).ConfigureAwait(false))}");
        }

        if (!string.IsNullOrWhiteSpace(query.Environment))
        {
            var environment = await ResolveEnvironmentAsync(query.Environment, cancellationToken).ConfigureAwait(false);
            parameters.Add($"environment={Escape(environment.Id)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Runbook))
        {
            parameters.Add($"runbook={Escape(query.Runbook)}");
        }

        if (query.States.Count > 0)
        {
            parameters.Add($"states={string.Join(',', query.States.Select(Escape))}");
        }

        if (query.HasPendingInterruptions is { } pending)
        {
            parameters.Add($"hasPendingInterruptions={(pending ? "true" : "false")}");
        }

        parameters.Add($"take={Math.Max(1, query.Take)}");
        var page = await rest.GetAsync<Collection<OctopusTask>>($"{Space}/tasks?{string.Join('&', parameters)}", cancellationToken).ConfigureAwait(false);
        return page.Items;
    }

    /// <inheritdoc />
    public Task<OctopusTask> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return rest.GetAsync<OctopusTask>($"{Space}/tasks/{Escape(taskId)}", cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> GetTaskLogAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return rest.GetStringAsync($"{Space}/tasks/{Escape(taskId)}/raw", cancellationToken);
    }

    /// <inheritdoc />
    public Task<OctopusTask> WaitForTaskAsync(string taskId, TimeSpan timeout, OctopusTaskWait until = OctopusTaskWait.Completed, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var awaited = until == OctopusTaskWait.Completed ? "complete" : "complete or wait for a manual intervention";
        return Poll.UntilAsync(
            token => GetTaskAsync(taskId, token),
            task => task.IsCompleted || (until == OctopusTaskWait.CompletedOrPendingInterruption && task.HasPendingInterruptions),
            timeout,
            pollInterval,
            $"Octopus task {taskId} to {awaited}",
            clock,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OctopusRunbookRun> StartRunbookAsync(OctopusRunbookRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await GetProjectAsync(request.Project, cancellationToken).ConfigureAwait(false);
        var environment = await ResolveEnvironmentAsync(request.Environment, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.GitRef))
        {
            return await StartDatabaseRunbookAsync(project, environment, request, cancellationToken).ConfigureAwait(false);
        }

        var gitRef = Escape(request.GitRef);
        var runbook = await FindGitRunbookAsync(project, request.GitRef, request.Runbook, cancellationToken).ConfigureAwait(false);
        var formValues = await BuildFormValuesAsync(project, gitRef, runbook, environment, request, cancellationToken).ConfigureAwait(false);
        var command = new RunGitRunbookCommand(
            spaceId,
            project.Id,
            runbook.Id,
            request.GitRef,
            request.Comments,
            [new RunGitRunbookRun(environment.Id, formValues, request.Comments)]);
        var response = await rest.SendAsync<RunGitRunbookResponse>(
            HttpMethod.Post,
            $"{Space}/projects/{Escape(project.Id)}/{gitRef}/runbooks/{Escape(runbook.Id)}/run/v1",
            command,
            cancellationToken).ConfigureAwait(false);
        var run = response.Resources.FirstOrDefault()
            ?? throw new InvalidOperationException($"Octopus accepted the run of runbook {runbook.Name} but returned no runbook run.");
        return new OctopusRunbookRun(run.Id, run.TaskId, project.Id, runbook.Id, environment.Id);
    }

    /// <inheritdoc />
    public async Task<OctopusRunbookRunResult> RunRunbookAsync(OctopusRunbookRunRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var run = await StartRunbookAsync(request, cancellationToken).ConfigureAwait(false);
        var task = await WaitForTaskAsync(run.TaskId, timeout, OctopusTaskWait.Completed, cancellationToken).ConfigureAwait(false);
        return new OctopusRunbookRunResult(run, task);
    }

    /// <inheritdoc />
    public async Task<OctopusRelease> CreateReleaseAsync(OctopusReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new CreateReleaseCommand(
            spaceId,
            spaceId,
            request.ProjectName,
            request.ReleaseVersion,
            request.ChannelName,
            request.GitRef,
            request.PackageVersion,
            request.Packages.Count == 0 ? null : request.Packages,
            request.ReleaseNotes,
            request.IgnoreIfAlreadyExists);
        return await rest.SendAsync<OctopusRelease>(HttpMethod.Post, $"{Space}/releases/create/v1", command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OctopusDeploymentTask>> DeployReleaseAsync(OctopusDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EnvironmentNames.Count == 0)
        {
            throw new ArgumentException("Name at least one environment.", nameof(request));
        }

        var command = new CreateDeploymentCommand(
            spaceId,
            spaceId,
            request.ProjectName,
            request.ReleaseVersion,
            request.EnvironmentNames,
            request.Variables.Count == 0 ? null : request.Variables,
            request.UseGuidedFailure);
        var response = await rest.SendAsync<CreateDeploymentResponse>(HttpMethod.Post, $"{Space}/deployments/create/untenanted/v1", command, cancellationToken).ConfigureAwait(false);
        return response.DeploymentServerTasks.Select(task => new OctopusDeploymentTask(task.DeploymentId, task.ServerTaskId)).ToArray();
    }

    /// <inheritdoc />
    public Task<OctopusDeployment> GetDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        return rest.GetAsync<OctopusDeployment>($"{Space}/deployments/{Escape(deploymentId)}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OctopusInterruption>> GetPendingInterruptionsAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var page = await rest.GetAsync<Collection<OctopusInterruption>>($"{Space}/interruptions?regarding={Escape(taskId)}&pendingOnly=true&take=100", cancellationToken).ConfigureAwait(false);
        return page.Items;
    }

    /// <inheritdoc />
    public async Task<OctopusInterruption> ApproveInterruptionAsync(string interruptionId, string note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var path = $"{Space}/interruptions/{Escape(interruptionId)}";
        await rest.SendAsync(HttpMethod.Put, $"{path}/responsible", body: null, cancellationToken).ConfigureAwait(false);
        var submission = new SubmitInterruptionCommand(interruptionId, spaceId, Instructions: null, Notes: note, Result: "Proceed");
        return await rest.SendAsync<OctopusInterruption>(HttpMethod.Post, $"{path}/submit", submission, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<OctopusVariableSet> GetVariableSetAsync(string variableSetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableSetId);
        return rest.GetAsync<OctopusVariableSet>($"{Space}/variables/{Escape(variableSetId)}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OctopusVariableSet> GetProjectVariablesAsync(string projectIdOrSlug, string? gitRef = null, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectIdOrSlug, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            return await rest.GetAsync<OctopusVariableSet>($"{Space}/projects/{Escape(project.Id)}/{Escape(gitRef)}/variables", cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(project.VariableSetId))
        {
            throw new InvalidOperationException($"Octopus project {project.Name} has no database variable set.");
        }

        return await GetVariableSetAsync(project.VariableSetId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();

    private static string Escape(string value) => Uri.EscapeDataString(value);

    [GeneratedRegex("^[A-Za-z]+-[0-9]+$")]
    private static partial Regex OctopusIdPattern();

    private async Task<string> ResolveProjectIdAsync(string project, CancellationToken cancellationToken) =>
        project.StartsWith("Projects-", StringComparison.Ordinal) && OctopusIdPattern().IsMatch(project)
            ? project
            : (await GetProjectAsync(project, cancellationToken).ConfigureAwait(false)).Id;

    private async Task<OctopusEnvironment> ResolveEnvironmentAsync(string environment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        if (environment.StartsWith("Environments-", StringComparison.Ordinal) && OctopusIdPattern().IsMatch(environment))
        {
            return await rest.GetAsync<OctopusEnvironment>($"{Space}/environments/{Escape(environment)}", cancellationToken).ConfigureAwait(false);
        }

        return await FindEnvironmentByNameAsync(environment, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Octopus environment '{environment}' does not exist in space {spaceId}.");
    }

    private async Task<RunbookSummary> FindGitRunbookAsync(OctopusProject project, string gitRef, string runbook, CancellationToken cancellationToken)
    {
        var page = await rest.GetAsync<Collection<RunbookSummary>>($"{Space}/projects/{Escape(project.Id)}/{Escape(gitRef)}/runbooks?take=1000", cancellationToken).ConfigureAwait(false);
        return page.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, runbook, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Slug, runbook, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, runbook, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Runbook '{runbook}' is not in project {project.Name} at {gitRef}; runbooks there: {string.Join(", ", page.Items.Select(candidate => candidate.Name))}.");
    }

    private async Task<Dictionary<string, string>> BuildFormValuesAsync(
        OctopusProject project,
        string escapedGitRef,
        RunbookSummary runbook,
        OctopusEnvironment environment,
        OctopusRunbookRunRequest request,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(request.FormValues, StringComparer.Ordinal);
        if (request.PromptedVariables.Count == 0)
        {
            return values;
        }

        var preview = await rest.GetAsync<RunPreview>(
            $"{Space}/projects/{Escape(project.Id)}/{escapedGitRef}/runbooks/{Escape(runbook.Id)}/runbookRuns/preview/{Escape(environment.Id)}",
            cancellationToken).ConfigureAwait(false);
        var elements = preview.Form?.Elements ?? [];
        foreach (var (variable, value) in request.PromptedVariables)
        {
            var element = elements.FirstOrDefault(candidate => string.Equals(ControlName(candidate.Control), variable, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Runbook {runbook.Name} has no prompted variable '{variable}' in {environment.Name}; prompted variables: {string.Join(", ", elements.Select(candidate => ControlName(candidate.Control)).Where(name => name is not null))}.");
            values[element.Name] = value;
        }

        return values;
    }

    private static string? ControlName(JsonElement control) =>
        control.ValueKind == JsonValueKind.Object && control.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;

    private async Task<OctopusRunbookRun> StartDatabaseRunbookAsync(OctopusProject project, OctopusEnvironment environment, OctopusRunbookRunRequest request, CancellationToken cancellationToken)
    {
        if (request.FormValues.Count > 0)
        {
            throw new ArgumentException("FormValues apply to config-as-code runbooks only; use PromptedVariables for a database runbook.", nameof(request));
        }

        var command = new CreateRunbookRunCommand(
            spaceId,
            spaceId,
            project.Name,
            request.Runbook,
            [environment.Name],
            request.PromptedVariables.Count == 0 ? null : request.PromptedVariables);
        var response = await rest.SendAsync<CreateRunbookRunResponse>(HttpMethod.Post, $"{Space}/runbook-runs/create/v1", command, cancellationToken).ConfigureAwait(false);
        var task = response.RunbookRunServerTasks.FirstOrDefault()
            ?? throw new InvalidOperationException($"Octopus accepted the run of runbook {request.Runbook} but returned no task.");
        return new OctopusRunbookRun(task.RunbookRunId, task.ServerTaskId, project.Id, request.Runbook, environment.Id);
    }

    private sealed record Collection<T>(IReadOnlyList<T> Items);

    private sealed record RunbookSummary(string Id, string Name, string? Slug);

    private sealed record RunPreview(PreviewForm? Form);

    private sealed record PreviewForm(IReadOnlyList<PreviewElement>? Elements);

    private sealed record PreviewElement(string Name, JsonElement Control);

    private sealed record RunGitRunbookCommand(string SpaceId, string ProjectId, string RunbookId, string GitRef, string? Notes, IReadOnlyList<RunGitRunbookRun> Runs);

    private sealed record RunGitRunbookRun(string EnvironmentId, IReadOnlyDictionary<string, string> FormValues, string? Comments);

    private sealed record RunGitRunbookResponse(IReadOnlyList<RunbookRunResource> Resources);

    private sealed record RunbookRunResource(string Id, string TaskId);

    private sealed record CreateRunbookRunCommand(string SpaceId, string SpaceIdOrName, string ProjectName, string RunbookName, IReadOnlyList<string> EnvironmentNames, IReadOnlyDictionary<string, string>? Variables);

    private sealed record CreateRunbookRunResponse(IReadOnlyList<ServerTaskOfRunbookRun> RunbookRunServerTasks);

    private sealed record ServerTaskOfRunbookRun(string RunbookRunId, string ServerTaskId);

    private sealed record CreateReleaseCommand(
        string SpaceId,
        string SpaceIdOrName,
        string ProjectName,
        string? ReleaseVersion,
        string? ChannelName,
        string? GitRef,
        string? PackageVersion,
        IReadOnlyList<string>? Packages,
        string? ReleaseNotes,
        bool IgnoreIfAlreadyExists);

    private sealed record CreateDeploymentCommand(
        string SpaceId,
        string SpaceIdOrName,
        string ProjectName,
        string ReleaseVersion,
        IReadOnlyList<string> EnvironmentNames,
        IReadOnlyDictionary<string, string>? Variables,
        bool? UseGuidedFailure);

    private sealed record CreateDeploymentResponse(IReadOnlyList<ServerTaskOfDeployment> DeploymentServerTasks);

    private sealed record ServerTaskOfDeployment(string DeploymentId, string ServerTaskId);

    private sealed record SubmitInterruptionCommand(
        string Id,
        string SpaceId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Instructions,
        string Notes,
        string Result);
}
