namespace Platform.Conformance.Harness.Clients;

/// <summary>The Codefresh user that owns the API key.</summary>
/// <param name="Id">User ID.</param>
/// <param name="UserName">User name.</param>
/// <param name="ActiveAccountName">Name of the account the key acts in.</param>
/// <param name="AccountNames">Every account the user belongs to.</param>
public sealed record CodefreshUser(string Id, string UserName, string? ActiveAccountName, IReadOnlyList<string> AccountNames);

/// <summary>A pipeline run to start.</summary>
public sealed record CodefreshRunRequest
{
    /// <summary>Branch to build, as if a trigger fired for it.</summary>
    public string? Branch { get; init; }

    /// <summary>ID of the trigger to simulate.</summary>
    public string? Trigger { get; init; }

    /// <summary>Build variables.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();
}

/// <summary>A Codefresh build (workflow).</summary>
public sealed record CodefreshBuild
{
    /// <summary>Build ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Status: see <see cref="CodefreshBuildStatuses"/>.</summary>
    public string Status { get; init; } = "";

    /// <summary>Progress ID, used to terminate the build.</summary>
    public string? ProgressId { get; init; }

    /// <summary>Pipeline name, for example <c>workorders/release</c>.</summary>
    public string? PipelineName { get; init; }

    /// <summary>Branch.</summary>
    public string? Branch { get; init; }

    /// <summary>Commit SHA.</summary>
    public string? Revision { get; init; }

    /// <summary>When the build was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the build finished.</summary>
    public DateTimeOffset? Finished { get; init; }

    /// <summary><c>true</c> when the status is final: success, error, terminated or denied.</summary>
    public bool IsTerminal => CodefreshBuildStatuses.Terminal.Contains(Status, StringComparer.OrdinalIgnoreCase);

    /// <summary>Compact summary for messages.</summary>
    public override string ToString() => $"{Id} {Status} ({PipelineName} {Branch} {Revision})";
}

/// <summary>Codefresh build statuses.</summary>
public static class CodefreshBuildStatuses
{
    /// <summary>Finished successfully.</summary>
    public const string Success = "success";

    /// <summary>Failed.</summary>
    public const string Error = "error";

    /// <summary>Terminated by a user or a newer build.</summary>
    public const string Terminated = "terminated";

    /// <summary>An approval was denied.</summary>
    public const string Denied = "denied";

    /// <summary>Final statuses.</summary>
    public static IReadOnlyList<string> Terminal { get; } = [Success, Error, Terminated, Denied];
}

/// <summary>A Codefresh runtime environment.</summary>
/// <param name="Name">Runtime name.</param>
/// <param name="IsAgent"><c>true</c> when it runs on a runner (agent).</param>
public sealed record CodefreshRuntimeEnvironment(string Name, bool IsAgent);

/// <summary>A Codefresh runner (agent) and its health.</summary>
/// <param name="Id">Agent ID.</param>
/// <param name="Name">Agent name.</param>
/// <param name="Runtimes">Runtime environments it serves.</param>
/// <param name="HealthStatus">Reported health, for example <c>healthy</c>.</param>
/// <param name="ReportedAt">Time of the last health report.</param>
public sealed record CodefreshAgent(string Id, string Name, IReadOnlyList<string> Runtimes, string? HealthStatus, DateTimeOffset? ReportedAt);
