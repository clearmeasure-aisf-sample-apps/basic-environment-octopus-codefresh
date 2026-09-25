using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>Names of the phases of the shared sandbox tdd rollout, in the order they run.</summary>
public static class RolloutPhase
{
    /// <summary>Loads the settings and holds the nonprod tier.</summary>
    public const string HoldTier = "hold tier";

    /// <summary>The release running in tdd and the newest earlier one with other images.</summary>
    public const string Baseline = "baseline";

    /// <summary>A new sandbox release and the tdd deployment the lifecycle starts for it (CAP-OCT-001).</summary>
    public const string NewRelease = "new release";

    /// <summary>One deployment of the previous release to tdd: its pin commits (CAP-OCT-012), pins and /version (CAP-OCT-007).</summary>
    public const string RollBack = "roll back";

    /// <summary>Redeploys the images tdd ran before the rollout.</summary>
    public const string Restore = "restore";
}

/// <summary>What the one deployment of the previous release to tdd did.</summary>
/// <param name="Current">The release tdd ran before.</param>
/// <param name="Previous">The release deployed.</param>
/// <param name="Deployment">The deployment task.</param>
/// <param name="CommitFiles">Files of each environment repository commit the deployment added, by commit (SHA and author).</param>
/// <param name="Pins">The tdd pins after the deployment.</param>
/// <param name="ReportedVersion">The version /version of sandbox-tdd reported, polled until it matched the previous web image (or the last value).</param>
public sealed record RollbackObservation(
    SandboxRelease Current,
    SandboxRelease Previous,
    OctopusTask Deployment,
    IReadOnlyList<(string Commit, string Author, IReadOnlyList<string> Files)> CommitFiles,
    IReadOnlyDictionary<string, string> Pins,
    string? ReportedVersion);

/// <summary>
/// One sandbox release and one rollback deployment in tdd, shared by CAP-OCT-001 (LifecycleTests), CAP-OCT-012
/// (PinWriterTests) and CAP-OCT-007 (RollbackTests) instead of five deployments. The lifecycle's automatic tdd deployment
/// of a new release runs first; then the previous release is deployed once (its pin commits and its pins and version are
/// what the pin-writer and rollback tests assert); last, the images tdd ran before are deployed again, as the rollback
/// test's teardown did. Runs once, on the first test's demand; the fixtures stay non-parallel, so it never overlaps a sleep
/// and wake cycle.
/// </summary>
public sealed class SandboxTddRollout : OctopusCapabilityTestBase
{
    private static readonly Lazy<SandboxTddRollout> Shared = new(() => new SandboxTddRollout());
    private readonly List<string> artifacts = [];
    private readonly Lock gate = new();
    private IDisposable? hold;
    private CancellationToken phaseToken;

    private SandboxTddRollout()
    {
        Phases = new PhasedCycle(
            "sandbox tdd rollout",
            [
                new CyclePhase(RolloutPhase.HoldTier, HoldAsync),
                new CyclePhase(RolloutPhase.Baseline, token => Guarded(token, BaselineAsync)),
                new CyclePhase(RolloutPhase.NewRelease, token => Guarded(token, NewReleaseAsync), [RolloutPhase.HoldTier]),
                new CyclePhase(RolloutPhase.RollBack, token => Guarded(token, RollBackAsync), [RolloutPhase.Baseline]),
                new CyclePhase(RolloutPhase.Restore, token => Guarded(token, RestoreAsync), [RolloutPhase.RollBack]),
            ],
            TimeSpan.FromHours(3),
            CycleProgress.Write,
            onEnd: EndAsync);
    }

    /// <summary>The rollout of this run.</summary>
    public static SandboxTddRollout Instance => Shared.Value;

    /// <summary>The phases.</summary>
    public PhasedCycle Phases { get; }

    /// <summary>The release tdd ran and the newest earlier one with other images.</summary>
    public Observation<(SandboxRelease Current, SandboxRelease Previous)>? Releases { get; private set; }

    /// <summary>The new release and its automatic tdd deployment.</summary>
    public Observation<(SandboxRelease Release, OctopusTask Deployment)>? AutomaticDeployment { get; private set; }

    /// <summary>The deployment of the previous release to tdd.</summary>
    public Observation<RollbackObservation>? Rollback { get; private set; }

    /// <inheritdoc />
    protected override CancellationToken Token => phaseToken;

    /// <summary>Waits for the phases a test asserts on, attaches the rollout's task logs and ends the test when one did not pass.</summary>
    /// <param name="phases">Phases the test needs.</param>
    public async Task RequireAsync(params string[] phases)
    {
        try
        {
            await Phases.RequireAsync(TestContext.CurrentContext.CancellationToken, phases);
        }
        finally
        {
            string[] paths;
            lock (gate)
            {
                paths = [.. artifacts];
            }

            foreach (var path in paths)
            {
                TestContext.AddTestAttachment(path);
            }
        }
    }

    /// <inheritdoc />
    protected override string AttachArtifact(string fileName, string content)
    {
        var path = Run.WriteArtifact($"tdd-rollout-{fileName}", content);
        lock (gate)
        {
            artifacts.Add(path);
        }

        return path;
    }

    /// <inheritdoc />
    protected override void Log(string line) => CycleProgress.Write($"sandbox tdd rollout: {line}");

    private async Task Guarded(CancellationToken token, Func<Task> phase)
    {
        phaseToken = token;
        await phase();
    }

    private async Task HoldAsync(CancellationToken token)
    {
        LoadPlatformSettings();
        hold = await TierLock.AcquireAsync(PlatformTier.NonProd, "sandbox tdd rollout", CycleProgress.Write, cancellationToken: token);
        Rest("the sandbox tdd rollout");
    }

    private async Task EndAsync()
    {
        hold?.Dispose();
        foreach (var failure in await DisposePlatformAsync())
        {
            Log($"cleanup '{failure.Description}' failed: {failure.Exception.Message}");
        }
    }

    private async Task BaselineAsync()
    {
        Releases = await Observation<(SandboxRelease, SandboxRelease)>.CaptureAsync("releases of tdd", _ => CurrentAndPreviousAsync("tdd"), Token);
        Log($"baseline: {Releases}");
    }

    private async Task NewReleaseAsync()
    {
        AutomaticDeployment = await Observation<(SandboxRelease, OctopusTask)>.CaptureAsync(
            "new sandbox release and its automatic tdd deployment",
            async _ =>
            {
                var release = await CreateSandboxReleaseAsync("Default");
                var taskId = await WaitForAutomaticDeploymentAsync(release, "tdd");
                return (release, await CompleteAsync(taskId, "automatic tdd deployment", requireSuccess: false));
            },
            Token);
        Log($"new release: {AutomaticDeployment}");
    }

    private async Task RollBackAsync()
    {
        var (current, previous) = Releases!.Require(RolloutPhase.Baseline);
        Rollback = await Observation<RollbackObservation>.CaptureAsync(
            $"deployment of {previous.Version} to tdd",
            async _ =>
            {
                var rest = Rest("the rollback deployment", gitHub: true);
                var repository = Settings.EnvRepo!;
                var baseUrl = await SandboxBaseUrlAsync("tdd");
                var before = await GitHub.GetBranchHeadAsync(repository, "main", Token);
                var deployment = await DeployAndCompleteAsync(previous, "tdd", requireSuccess: false);
                var commits = new List<(string, string, IReadOnlyList<string>)>();
                foreach (var commit in (await GitHub.CompareAsync(repository, before, "main", Token)).Commits)
                {
                    commits.Add((commit.Sha, commit.AuthorName ?? "unknown", await rest.GitHubCommitFilesAsync(repository, commit.Sha, Token)));
                }

                var pins = PinnedTags((await GitHub.GetFileAsync(repository, PinPath("tdd"), "main", Token)).Content);
                string? reported = null;
                if (deployment.FinishedSuccessfully && previous.Packages.TryGetValue("web", out var expected))
                {
                    using var http = PlatformHttp.Create(baseUrl, Settings.TimeLimits.HttpTimeout);
                    reported = await ReportedVersionAsync(http, baseUrl, expected);
                }

                return new RollbackObservation(current, previous, deployment, commits, pins, reported);
            },
            Token);
        Log($"roll back: {Rollback}");
    }

    private async Task<string?> ReportedVersionAsync(HttpClient http, Uri baseUrl, string expected)
    {
        try
        {
            return await Poll.UntilAsync(
                        async token =>
                        {
                            using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri("version", UriKind.Relative), token));
                            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
                        },
                        version => version == expected,
                        TimeSpan.FromMinutes(5),
                        Settings.TimeLimits.PollInterval,
                        $"{baseUrl}version to report {expected}",
                        retryWhen: exception => exception is HttpRequestException or JsonException,
                        cancellationToken: Token);
        }
        catch (PollTimeoutException ex)
        {
            Log(ex.Message);
            return ex.LastObservation;
        }
    }

    private async Task RestoreAsync()
    {
        var restoreTo = AutomaticDeployment is { Error: null } created && created.Require(RolloutPhase.NewRelease).Deployment.FinishedSuccessfully
            ? created.Require(RolloutPhase.NewRelease).Release
            : Releases!.Require(RolloutPhase.Baseline).Current;
        var task = await DeployAndCompleteAsync(restoreTo, "tdd", requireSuccess: false);
        Log($"restored tdd to {restoreTo.Version}: {task}");
        task.FinishedSuccessfully.ShouldBeTrue($"redeploying {restoreTo.Version} to tdd (the images tdd ran before the rollout): {task}");
    }
}
