namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Octopus Deploy REST API of one space (<c>X-Octopus-ApiKey</c> header, routes under <c>/api/{spaceId}/</c>).
/// Config-as-code runbooks run through <c>/api/{spaceId}/projects/{projectId}/{gitRef}/runbooks/{runbookId}/run/v1</c>.
/// </summary>
public interface IOctopusApi
{
    /// <summary>Gets the configured space (<c>GET /api/spaces/{spaceId}</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusSpace> GetSpaceAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a project by ID or slug.</summary>
    /// <param name="idOrSlug">Project ID or slug, for example <c>workorders</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusProject> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default);

    /// <summary>Finds an environment by exact name (case-insensitive); <c>null</c> when absent.</summary>
    /// <param name="name">Environment name, for example <c>infra-nonprod</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusEnvironment?> FindEnvironmentByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists tasks by project, environment, runbook and state, newest first.</summary>
    /// <param name="query">The filter; slugs and names are resolved to IDs.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<OctopusTask>> GetTasksAsync(OctopusTaskQuery query, CancellationToken cancellationToken = default);

    /// <summary>Gets a task and its state.</summary>
    /// <param name="taskId">Task ID, for example <c>ServerTasks-1</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusTask> GetTaskAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Gets the raw task log as text.</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> GetTaskLogAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Polls a task until it completes (or, optionally, waits for a manual intervention).</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="until">What ends the wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The task as last observed.</returns>
    /// <exception cref="Support.PollTimeoutException">The task did not reach the awaited state in time.</exception>
    Task<OctopusTask> WaitForTaskAsync(string taskId, TimeSpan timeout, OctopusTaskWait until = OctopusTaskWait.Completed, CancellationToken cancellationToken = default);

    /// <summary>Starts a runbook run (config-as-code at a Git reference, or a database runbook's published snapshot).</summary>
    /// <param name="request">Project, runbook, environment, prompted variables or form values.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusRunbookRun> StartRunbookAsync(OctopusRunbookRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts a runbook run and waits for its task to complete.</summary>
    /// <param name="request">Project, runbook, environment, prompted variables or form values.</param>
    /// <param name="timeout">Longest wait for the task.</param>
    /// <param name="cancellationToken">Cancels the call and the wait.</param>
    Task<OctopusRunbookRunResult> RunRunbookAsync(OctopusRunbookRunRequest request, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Creates a release (<c>POST /api/{spaceId}/releases/create/v1</c>).</summary>
    /// <param name="request">Project, version, channel, Git reference and packages.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusRelease> CreateReleaseAsync(OctopusReleaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deploys a release (<c>POST /api/{spaceId}/deployments/create/untenanted/v1</c>).</summary>
    /// <param name="request">Project, release version, environments and prompted variables.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One deployment and task per environment.</returns>
    Task<IReadOnlyList<OctopusDeploymentTask>> DeployReleaseAsync(OctopusDeploymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets a deployment (its release, environment and task).</summary>
    /// <param name="deploymentId">Deployment ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusDeployment> GetDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default);

    /// <summary>Lists the pending interruptions (manual interventions, guided failures) of a task.</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<OctopusInterruption>> GetPendingInterruptionsAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a manual intervention: takes responsibility (<c>PUT .../interruptions/{id}/responsible</c>), then submits
    /// <c>Result=Proceed</c> with <paramref name="note"/> (<c>POST .../interruptions/{id}/submit</c>).
    /// </summary>
    /// <param name="interruptionId">Interruption ID.</param>
    /// <param name="note">Notes recorded with the approval, naming the test run.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    Task<OctopusInterruption> ApproveInterruptionAsync(string interruptionId, string note, CancellationToken cancellationToken = default);

    /// <summary>Gets a database-stored variable set by ID (project or library variable set).</summary>
    /// <param name="variableSetId">Variable set ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusVariableSet> GetVariableSetAsync(string variableSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a project's variables: the Git-stored ones at <paramref name="gitRef"/> for a config-as-code project,
    /// or the database variable set when <paramref name="gitRef"/> is <c>null</c>.
    /// </summary>
    /// <param name="projectIdOrSlug">Project ID or slug.</param>
    /// <param name="gitRef">Git reference such as <c>refs/heads/main</c>, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<OctopusVariableSet> GetProjectVariablesAsync(string projectIdOrSlug, string? gitRef = null, CancellationToken cancellationToken = default);
}
