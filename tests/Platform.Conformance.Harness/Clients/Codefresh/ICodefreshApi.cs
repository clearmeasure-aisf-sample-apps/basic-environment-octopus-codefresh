namespace Platform.Conformance.Harness.Clients;

/// <summary>Codefresh REST API (base <c>https://g.codefresh.io/api</c>; the API key is the <c>Authorization</c> header).</summary>
public interface ICodefreshApi
{
    /// <summary>Gets the user that owns the API key and its active account (<c>GET /user</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<CodefreshUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a pipeline (<c>POST /pipelines/run/{name}</c>) and returns the build ID.</summary>
    /// <param name="pipelineName">Full pipeline name, for example <c>workorders/release</c>.</param>
    /// <param name="request">Branch, trigger and variables.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> RunPipelineAsync(string pipelineName, CodefreshRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets a build (<c>GET /builds/{id}</c>).</summary>
    /// <param name="buildId">Build ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<CodefreshBuild> GetBuildAsync(string buildId, CancellationToken cancellationToken = default);

    /// <summary>Polls a build until its status is final (success, error, terminated or denied).</summary>
    /// <param name="buildId">Build ID.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task<CodefreshBuild> WaitForBuildAsync(string buildId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Terminates a build (<c>DELETE /progress/{progressId}</c>, the build's <c>progress</c> field).</summary>
    /// <param name="buildId">Build ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task TerminateBuildAsync(string buildId, CancellationToken cancellationToken = default);

    /// <summary>Lists the newest builds of a pipeline (<c>GET /workflow?pipeline={pipelineId}</c>).</summary>
    /// <param name="pipelineName">Full pipeline name; resolved to its ID.</param>
    /// <param name="limit">Maximum number of builds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<CodefreshBuild>> ListBuildsAsync(string pipelineName, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Lists the account's runtime environments (<c>GET /runtime-environments</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<CodefreshRuntimeEnvironment>> GetRuntimeEnvironmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the account's runners (<c>GET /agents</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<CodefreshAgent>> GetAgentsAsync(CancellationToken cancellationToken = default);
}
