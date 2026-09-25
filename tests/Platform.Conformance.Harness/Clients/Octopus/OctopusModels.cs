namespace Platform.Conformance.Harness.Clients;

/// <summary>An Octopus space.</summary>
public sealed record OctopusSpace
{
    /// <summary>Space ID, for example <c>Spaces-1</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>URL slug.</summary>
    public string? Slug { get; init; }

    /// <summary><c>true</c> when the task queue of the space is stopped.</summary>
    public bool TaskQueueStopped { get; init; }
}

/// <summary>An Octopus project.</summary>
public sealed record OctopusProject
{
    /// <summary>Project ID, for example <c>Projects-1</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>URL slug, for example <c>platform-infrastructure</c>.</summary>
    public string? Slug { get; init; }

    /// <summary><c>true</c> for a config-as-code project (process and runbooks stored in Git).</summary>
    public bool IsVersionControlled { get; init; }

    /// <summary>ID of the database-stored variable set (sensitive variables of a config-as-code project).</summary>
    public string? VariableSetId { get; init; }

    /// <summary>Lifecycle ID.</summary>
    public string? LifecycleId { get; init; }
}

/// <summary>An Octopus environment.</summary>
public sealed record OctopusEnvironment
{
    /// <summary>Environment ID, for example <c>Environments-1</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name, for example <c>infra-nonprod</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>URL slug.</summary>
    public string? Slug { get; init; }
}

/// <summary>An Octopus server task (a deployment, a runbook run or a system task).</summary>
public sealed record OctopusTask
{
    /// <summary>Task ID, for example <c>ServerTasks-1</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Task type, for example <c>Deploy</c> or <c>RunbookRun</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Human description.</summary>
    public string? Description { get; init; }

    /// <summary>State: see <see cref="OctopusTaskStates"/>.</summary>
    public string State { get; init; } = "";

    /// <summary><c>true</c> once the task reached a final state.</summary>
    public bool IsCompleted { get; init; }

    /// <summary><c>true</c> when the task completed successfully.</summary>
    public bool FinishedSuccessfully { get; init; }

    /// <summary><c>true</c> while a manual intervention or guided failure waits for a person.</summary>
    public bool HasPendingInterruptions { get; init; }

    /// <summary>Error message of a failed task.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Project of the task, if any.</summary>
    public string? ProjectId { get; init; }

    /// <summary>When the task was queued.</summary>
    public DateTimeOffset? QueueTime { get; init; }

    /// <summary>When the task started.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>When the task completed.</summary>
    public DateTimeOffset? CompletedTime { get; init; }

    /// <summary>Duration as Octopus formats it.</summary>
    public string? Duration { get; init; }

    /// <summary>Compact summary for messages.</summary>
    public override string ToString() =>
        $"{Id} {State}{(HasPendingInterruptions ? " (pending interruption)" : "")}{(string.IsNullOrEmpty(ErrorMessage) ? "" : $": {ErrorMessage}")}";
}

/// <summary>Octopus task state names.</summary>
public static class OctopusTaskStates
{
    /// <summary>Waiting for a task slot.</summary>
    public const string Queued = "Queued";

    /// <summary>Running.</summary>
    public const string Executing = "Executing";

    /// <summary>Being cancelled.</summary>
    public const string Cancelling = "Cancelling";

    /// <summary>Completed successfully.</summary>
    public const string Success = "Success";

    /// <summary>Completed with an error.</summary>
    public const string Failed = "Failed";

    /// <summary>Cancelled.</summary>
    public const string Canceled = "Canceled";

    /// <summary>Timed out.</summary>
    public const string TimedOut = "TimedOut";

    /// <summary>States of a task that has not completed.</summary>
    public static IReadOnlyList<string> Active { get; } = [Queued, Executing, Cancelling];
}

/// <summary>Filter for <see cref="IOctopusApi.GetTasksAsync"/>.</summary>
public sealed record OctopusTaskQuery
{
    /// <summary>Project ID or slug; slugs are resolved to IDs.</summary>
    public string? Project { get; init; }

    /// <summary>Environment ID or name; names are resolved to IDs.</summary>
    public string? Environment { get; init; }

    /// <summary>Runbook ID.</summary>
    public string? Runbook { get; init; }

    /// <summary>States to match (see <see cref="OctopusTaskStates"/>); any state when empty.</summary>
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>Only tasks with (or without) a pending interruption.</summary>
    public bool? HasPendingInterruptions { get; init; }

    /// <summary>Maximum number of tasks, newest first (default 30).</summary>
    public int Take { get; init; } = 30;
}

/// <summary>What <see cref="IOctopusApi.WaitForTaskAsync"/> waits for.</summary>
public enum OctopusTaskWait
{
    /// <summary>The task reaches a final state.</summary>
    Completed,

    /// <summary>The task reaches a final state or waits for a manual intervention.</summary>
    CompletedOrPendingInterruption,
}

/// <summary>A runbook to run.</summary>
public sealed record OctopusRunbookRunRequest
{
    /// <summary>Default Git reference of config-as-code runbooks.</summary>
    public const string MainBranch = "refs/heads/main";

    /// <summary>Project ID or slug, for example <c>platform-infrastructure</c>.</summary>
    public required string Project { get; init; }

    /// <summary>Runbook name, slug or ID, for example <c>env-wake</c>.</summary>
    public required string Runbook { get; init; }

    /// <summary>Environment name or ID, for example <c>infra-nonprod</c>.</summary>
    public required string Environment { get; init; }

    /// <summary>
    /// Git reference of a config-as-code runbook (default <c>refs/heads/main</c>). <c>null</c> runs a database runbook's
    /// published snapshot instead.
    /// </summary>
    public string? GitRef { get; init; } = MainBranch;

    /// <summary>Prompted variables by variable name, for example <c>Sleep.Force = True</c>.</summary>
    public IReadOnlyDictionary<string, string> PromptedVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>Raw form values by form element name, for config-as-code runbooks; prefer <see cref="PromptedVariables"/>.</summary>
    public IReadOnlyDictionary<string, string> FormValues { get; init; } = new Dictionary<string, string>();

    /// <summary>Comment recorded on the run.</summary>
    public string? Comments { get; init; }
}

/// <summary>A started runbook run.</summary>
/// <param name="RunbookRunId">Runbook run ID.</param>
/// <param name="TaskId">Server task ID.</param>
/// <param name="ProjectId">Project ID.</param>
/// <param name="RunbookId">Runbook ID.</param>
/// <param name="EnvironmentId">Environment ID.</param>
public sealed record OctopusRunbookRun(string RunbookRunId, string TaskId, string ProjectId, string RunbookId, string EnvironmentId);

/// <summary>A finished (or timed-out wait for a) runbook run.</summary>
/// <param name="Run">The started run.</param>
/// <param name="Task">The task as last observed.</param>
public sealed record OctopusRunbookRunResult(OctopusRunbookRun Run, OctopusTask Task);

/// <summary>A release to create (executions API <c>releases/create/v1</c>).</summary>
public sealed record OctopusReleaseRequest
{
    /// <summary>Project name, for example <c>workorders</c>.</summary>
    public required string ProjectName { get; init; }

    /// <summary>Release version; Octopus derives one when omitted.</summary>
    public string? ReleaseVersion { get; init; }

    /// <summary>Channel name, for example <c>Default</c>.</summary>
    public string? ChannelName { get; init; }

    /// <summary>Git reference of a config-as-code project, for example <c>refs/heads/main</c>.</summary>
    public string? GitRef { get; init; }

    /// <summary>Default version of every package.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>Package versions as <c>PackageId:Version</c> or <c>StepName:PackageId:Version</c>.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>Release notes.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>Return the existing release instead of failing when the version exists.</summary>
    public bool IgnoreIfAlreadyExists { get; init; }
}

/// <summary>A created release.</summary>
/// <param name="ReleaseId">Release ID.</param>
/// <param name="ReleaseVersion">Release version.</param>
public sealed record OctopusRelease(string ReleaseId, string ReleaseVersion);

/// <summary>A deployment to start (executions API <c>deployments/create/untenanted/v1</c>).</summary>
public sealed record OctopusDeploymentRequest
{
    /// <summary>Project name, for example <c>workorders</c>.</summary>
    public required string ProjectName { get; init; }

    /// <summary>Release version to deploy.</summary>
    public required string ReleaseVersion { get; init; }

    /// <summary>Environment names, for example <c>tdd</c>.</summary>
    public required IReadOnlyList<string> EnvironmentNames { get; init; }

    /// <summary>Prompted variables by name.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>Overrides the guided-failure mode.</summary>
    public bool? UseGuidedFailure { get; init; }
}

/// <summary>A started deployment and its task.</summary>
/// <param name="DeploymentId">Deployment ID.</param>
/// <param name="TaskId">Server task ID.</param>
public sealed record OctopusDeploymentTask(string DeploymentId, string TaskId);

/// <summary>An Octopus deployment.</summary>
public sealed record OctopusDeployment
{
    /// <summary>Deployment ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Release ID.</summary>
    public string ReleaseId { get; init; } = "";

    /// <summary>Environment ID.</summary>
    public string EnvironmentId { get; init; } = "";

    /// <summary>Project ID.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Server task ID.</summary>
    public string TaskId { get; init; } = "";

    /// <summary>When it was created.</summary>
    public DateTimeOffset? Created { get; init; }
}

/// <summary>A manual intervention or guided failure waiting on a task.</summary>
public sealed record OctopusInterruption
{
    /// <summary>Interruption ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Title, usually the step name.</summary>
    public string? Title { get; init; }

    /// <summary>Task it interrupts.</summary>
    public string? TaskId { get; init; }

    /// <summary>Type, for example <c>ManualIntervention</c> or <c>GuidedFailure</c>.</summary>
    public string? Type { get; init; }

    /// <summary><c>true</c> while it waits for a response.</summary>
    public bool IsPending { get; init; }

    /// <summary><c>true</c> when the caller may take responsibility.</summary>
    public bool CanTakeResponsibility { get; init; }

    /// <summary><c>true</c> when the caller holds responsibility.</summary>
    public bool HasResponsibility { get; init; }

    /// <summary>User who holds responsibility.</summary>
    public string? ResponsibleUserId { get; init; }

    /// <summary>Teams that may respond.</summary>
    public IReadOnlyList<string> ResponsibleTeamIds { get; init; } = [];

    /// <summary>
    /// <c>true</c> for an interruption only Octopus itself may answer (for example the wait of an Argo CD step); Octopus
    /// refuses to let any user take responsibility for it or submit it.
    /// </summary>
    public bool IsAnsweredBySystem => Type is "ArgoCDApplicationSync" or "PullRequestCompletion" or "KubernetesResourceVerification";
}

/// <summary>An Octopus variable set (project, library or Git-stored project variables).</summary>
public sealed record OctopusVariableSet
{
    /// <summary>Variable set ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Owner (project or library variable set) ID.</summary>
    public string? OwnerId { get; init; }

    /// <summary>Version number.</summary>
    public int Version { get; init; }

    /// <summary>The variables; sensitive values are never returned by Octopus.</summary>
    public IReadOnlyList<OctopusVariable> Variables { get; init; } = [];
}

/// <summary>One Octopus variable value with its scope.</summary>
public sealed record OctopusVariable
{
    /// <summary>Variable ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Variable name, for example <c>Sleep.Enabled</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Value; <c>null</c> for sensitive variables.</summary>
    public string? Value { get; init; }

    /// <summary><c>true</c> for a sensitive variable.</summary>
    public bool IsSensitive { get; init; }

    /// <summary>Type, for example <c>String</c> or <c>AzureAccount</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Scope by dimension, for example <c>Environment</c> → environment IDs.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Scope { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}
