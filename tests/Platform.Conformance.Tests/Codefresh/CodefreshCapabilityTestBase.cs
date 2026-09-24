using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// Shared steps of the live CAP-CF tests. The account runs one build at a time (BASIC_1), so the tests never start a
/// build and wait for it from inside <c>platform-env/conformance</c>: they observe the builds that
/// <c>platform-env/conformance-arm</c> queued before it (the sandbox branches, the release commit and its rerun), whose
/// commit SHAs and build ID arrive as run variables. Without them the tests that need them are Inconclusive.
/// </summary>
public abstract class CodefreshCapabilityTestBase : PlatformTestBase
{
    /// <summary>How long a queued build may wait before the test gives up on it (it cannot start while this build runs).</summary>
    protected static readonly TimeSpan QueuePatience = TimeSpan.FromMinutes(10);

    private CodefreshRest? codefreshRest;
    private GitHubReads? gitHubReads;
    private BuildArm? arm;
    private RegistryReader? registry;

    /// <summary>The cancellation token of the current test (<c>[CancelAfter]</c>).</summary>
    protected static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

    /// <summary><c>true</c> when the suite runs inside a Codefresh build (<c>CF_BUILD_ID</c> is set).</summary>
    protected static bool InsideCodefresh => CodefreshPlatform.Variable(CodefreshPlatform.BuildIdVariable) is not null;

    /// <summary>Raw Codefresh reads; Inconclusive without <c>CODEFRESH_API_KEY</c>.</summary>
    /// <param name="purpose">What needs them.</param>
    protected CodefreshRest RequireCodefresh(string purpose)
    {
        Settings.Check(purpose).Secret(EnvironmentVariableNames.CodefreshApiKey, Settings.Secrets.CodefreshApiKey).ThrowIfMissing();
        return codefreshRest ??= Owned(new CodefreshRest(Settings.CodefreshUrl, Settings.Secrets.CodefreshApiKey!, Settings.TimeLimits.HttpTimeout));
    }

    /// <summary>GitHub status and pull request reads; Inconclusive without <c>GITHUB_TOKEN</c>.</summary>
    /// <param name="purpose">What needs them.</param>
    protected GitHubReads RequireGitHub(string purpose)
    {
        Settings.Check(purpose).Secret(EnvironmentVariableNames.GitHubToken, Settings.Secrets.GitHubToken).ThrowIfMissing();
        return gitHubReads ??= Owned(new GitHubReads(Settings.Secrets.GitHubToken!, Settings.TimeLimits.HttpTimeout));
    }

    /// <summary>ARM reads of the build cluster; Inconclusive without the subscription or a credential.</summary>
    /// <param name="purpose">What needs them.</param>
    protected BuildArm RequireArm(string purpose)
    {
        Settings.Check(purpose).Setting(nameof(Settings.AzureSubscriptionId), Settings.AzureSubscriptionId).ThrowIfMissing();
        return arm ??= Owned(new BuildArm(AzureCredentialFactory.Create(Settings), Settings.AzureSubscriptionId!, Settings.TimeLimits.HttpTimeout));
    }

    /// <summary>Registry data-plane reads; Inconclusive without <c>RegistryLoginServer</c> or a credential.</summary>
    /// <param name="purpose">What needs them.</param>
    protected RegistryReader RequireRegistry(string purpose)
    {
        Settings.Check(purpose).Setting(nameof(Settings.RegistryLoginServer), Settings.RegistryLoginServer).ThrowIfMissing();
        var tenantId = PlatformSettings.IsMissing(Settings.AzureTenantId) ? null : Settings.AzureTenantId;
        return registry ??= Owned(new RegistryReader(AzureCredentialFactory.Create(Settings), Settings.RegistryLoginServer!, tenantId, Settings.TimeLimits.HttpTimeout));
    }

    /// <summary>The fixture repository; Inconclusive while it is still <c>&lt;sandbox-app-repo&gt;</c>.</summary>
    /// <param name="purpose">What needs it.</param>
    protected static string RequireSandboxRepository(string purpose) =>
        CodefreshPlatform.SandboxRepository()
        ?? throw new PlatformPrerequisiteException($"Prerequisites missing for {purpose}: the fixture repository is unknown; set {CodefreshPlatform.SandboxRepoVariable} or replace <sandbox-app-repo> in apps/sandbox.yaml (P1-11).");

    /// <summary>A run variable of platform-env/conformance-arm; Inconclusive when it is absent.</summary>
    /// <param name="name">Variable name.</param>
    /// <param name="purpose">What needs it.</param>
    protected static string RequireArmVariable(string name, string purpose) =>
        CodefreshPlatform.Variable(name)
        ?? throw new PlatformPrerequisiteException($"Prerequisites missing for {purpose}: run variable {name} is not set; platform-env/conformance-arm passes it when it queues platform-env/conformance.");

    /// <summary>
    /// The newest build of <paramref name="pipeline"/> for commit <paramref name="sha"/>, once it is final. Fails when no
    /// such build exists; Inconclusive when it stays queued behind this build.
    /// </summary>
    /// <param name="pipeline">Full pipeline name.</param>
    /// <param name="sha">Commit SHA.</param>
    protected async Task<CodefreshBuildRecord> FinishedBuildForAsync(string pipeline, string sha)
    {
        var codefresh = RequireCodefresh($"the {pipeline} build of {CodefreshPlatform.Short(sha)}");
        var builds = await codefresh.ListBuildsAsync(pipeline, 50, Token);
        var build = builds.FirstOrDefault(candidate => string.Equals(candidate.Revision, sha, StringComparison.OrdinalIgnoreCase));
        build.ShouldNotBeNull($"no {pipeline} build ran for commit {sha}; the newest builds are: {string.Join("; ", builds.Take(5))}");
        return await FinishedAsync(build);
    }

    /// <summary>Waits until a build is final; Inconclusive when it stays queued (one build at a time).</summary>
    /// <param name="build">A build.</param>
    protected async Task<CodefreshBuildRecord> FinishedAsync(CodefreshBuildRecord build)
    {
        if (build.IsTerminal)
        {
            return build;
        }

        var codefresh = RequireCodefresh($"build {build.Id}");
        var limit = InsideCodefresh ? QueuePatience : Settings.TimeLimits.BuildTimeout;
        try
        {
            return await Poll.UntilAsync(
                async token => await codefresh.GetBuildAsync(build.Id, token) ?? build,
                current => current.IsTerminal,
                limit,
                Settings.TimeLimits.PollInterval,
                $"Codefresh build {build.Id} to finish",
                cancellationToken: Token);
        }
        catch (PollTimeoutException) when (InsideCodefresh)
        {
            throw new PlatformPrerequisiteException(
                $"Codefresh build {build.Id} is still {build.Status} after {limit.TotalMinutes:0} minutes; with one build at a time it cannot run while this build runs (Q41).");
        }
    }

    private T Owned<T>(T client)
        where T : IDisposable
    {
        Cleanup.Register($"dispose {typeof(T).Name}", _ =>
        {
            client.Dispose();
            return Task.CompletedTask;
        });
        return client;
    }
}
