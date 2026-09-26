using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-010 (destructive, nonprod, sandbox only): a failed migration keeps the old version serving and fails the
/// deployment. The test commits the marker <c>toggles/failing-migration</c> to the sandbox repository, so
/// <c>sandbox/release</c> ships a migrator with a failing script and Octopus deploys that release to tdd. The PreSync Job
/// <c>db-migrate</c> fails, the sync fails, the Octopus deployment fails, and Deployment <c>web</c> of
/// <c>sandbox-tdd</c> keeps the previous image and its ready pods. The cleanup removes the marker; the next release
/// deploys cleanly and brings <c>sandbox-app-tdd</c> back to Synced and Healthy.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class FailedMigrationTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string Application = "sandbox-app-tdd";
    private const string Marker = "toggles/failing-migration";
    private const string AllowedOwner = "clearmeasure-aisf-sample-apps";
    private const string Gateway = "argocd-nonprod";

    [Test]
    [Capability("CAP-GIT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Destructive)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_Deploy_ReleaseWithFailingMigrationToTdd_FailsAndKeepsTheOldVersionServing()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var sandbox = App(GitOpsNames.FixtureApp);
        var repository = sandbox.PrimaryRepository;
        if (repository is null || PlatformSettings.IsMissing(repository))
        {
            Unobservable($"apps/{GitOpsNames.FixtureApp}.yaml still names the placeholder repository '{repository}' (set at P1-11)");
        }

        repository.Split('/')[0].ShouldBe(AllowedOwner, "the fixture repository must live in the sample-apps organization");
        var kubernetes = await KubernetesAsync(PlatformTier.NonProd, cancellationToken);
        var before = (await kubernetes.ListDeploymentsAsync(Namespace, cancellationToken: cancellationToken)).FirstOrDefault(deployment => deployment.Name == "web");
        if (before is not { ReadyReplicas: > 0 })
        {
            Unobservable($"Deployment {Namespace}/web is not serving; the old version cannot be shown to survive");
        }

        // Without a live gateway registration no pin reaches the Application and the migration never runs (a stale
        // registration after a rebuild blocks the new one): fail in minutes with the cause, not after the deployment.
        var gateway = await Poll.UntilAsync(
            token => Rest.GetArgoCDInstanceHealthAsync(Gateway, token),
            health => health is not null and not "Unavailable",
            TimeSpan.FromMinutes(15),
            Settings.TimeLimits.PollInterval,
            $"Octopus Argo CD instance {Gateway} to be registered and reachable",
            cancellationToken: cancellationToken);
        gateway.ShouldNotBeNull($"Octopus has no Argo CD instance {Gateway}; the gateway of the nonprod cluster did not register");

        var project = await Octopus.GetProjectAsync(GitOpsNames.FixtureApp, cancellationToken);
        var tdd = await Octopus.FindEnvironmentByNameAsync("tdd", cancellationToken);
        tdd.ShouldNotBeNull("Octopus environment tdd is missing");
        const string branch = "main";
        Cleanup.Register($"remove {Marker} from {repository} and wait for {Application} to recover", token => DisarmAsync(repository, branch, kubernetes, token));

        var armed = await GitHub.CommitFileAsync(repository, branch, Marker, $"conformance run {Run.RunId} (CAP-GIT-010)\n", $"conformance {Run.RunId}: ship a failing migration (CAP-GIT-010)", cancellationToken);
        var release = await Poll.UntilAsync(
            token => Rest.FindReleaseByCommitAsync(project.Id, armed, token),
            found => found is not null,
            Settings.TimeLimits.BuildTimeout,
            Settings.TimeLimits.PollInterval,
            $"sandbox/release to create the Octopus release of commit {armed}",
            cancellationToken: cancellationToken) ?? throw new InvalidOperationException("the release poll returned nothing");
        var taskId = await Poll.UntilAsync(
            token => Rest.FindDeploymentTaskAsync(release.Id, tdd.Id, token),
            found => found is not null,
            Settings.TimeLimits.DeploymentTimeout,
            Settings.TimeLimits.PollInterval,
            $"the lifecycle to deploy release {release.Version} to tdd",
            cancellationToken: cancellationToken) ?? throw new InvalidOperationException("the deployment poll returned nothing");
        // The Argo CD step pauses the task with an ArgoCDApplicationSync interruption while the sync runs (the PreSync
        // Job db-migrate among it): wait through those, and stop only at a prompt (guided failure or a manual step).
        var waited = await Poll.UntilAsync(
            async token =>
            {
                var current = await Octopus.GetTaskAsync(taskId, token);
                if (current.IsCompleted || !current.HasPendingInterruptions)
                {
                    return (Task: current, Prompt: false);
                }

                var pending = await Octopus.GetPendingInterruptionsAsync(taskId, token);
                return (Task: current, Prompt: pending.Any(interruption => interruption.Type != "ArgoCDApplicationSync"));
            },
            observed => observed.Task.IsCompleted || observed.Prompt,
            Settings.TimeLimits.DeploymentTimeout,
            Settings.TimeLimits.PollInterval,
            $"Octopus task {taskId} to complete or stop at a prompt",
            cancellationToken: cancellationToken);
        var task = waited.Task;
        if (!task.IsCompleted)
        {
            await Rest.CancelOctopusTaskAsync(task.Id, cancellationToken);
        }

        AttachArtifact("failed-migration-deployment.log", await Octopus.GetTaskLogAsync(task.Id, cancellationToken));
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var job = await cluster.JobAsync(Namespace, "db-migrate", cancellationToken);
        var after = (await kubernetes.ListDeploymentsAsync(Namespace, cancellationToken: cancellationToken)).FirstOrDefault(deployment => deployment.Name == "web");
        var application = await kubernetes.GetArgoApplicationAsync(Application, cancellationToken: cancellationToken);
        Assert.Multiple(() =>
        {
            task.FinishedSuccessfully.ShouldBeFalse($"the deployment of release {release.Version} to tdd succeeded despite the failing migration");
            (job?.Status?.Failed ?? 0).ShouldBeGreaterThan(0, $"Job {Namespace}/db-migrate did not fail");
            string.Join(", ", after?.Images ?? []).ShouldBe(string.Join(", ", before.Images), $"{Namespace}/web rolled out although the migration failed");
            (after?.ReadyReplicas ?? 0).ShouldBeGreaterThan(0, $"{Namespace}/web stopped serving");
            application.SyncStatus.ShouldBe("OutOfSync", $"{Application} applied the release whose migration failed (last operation {application.OperationPhase})");
        });
    }

    private async Task DisarmAsync(string repository, string branch, IKubernetesApi kubernetes, CancellationToken cancellationToken)
    {
        GitHubFile marker;
        try
        {
            marker = await GitHub.GetFileAsync(repository, Marker, branch, cancellationToken);
        }
        catch (PlatformApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await Rest.DeleteGitHubFileAsync(repository, branch, Marker, marker.Sha, $"conformance {Run.RunId}: remove the failing migration (CAP-GIT-010)", cancellationToken);
        await Poll.UntilAsync(
            async token => (await kubernetes.GetArgoApplicationAsync(Application, cancellationToken: token)).IsSyncedAndHealthy,
            Settings.TimeLimits.BuildTimeout + Settings.TimeLimits.DeploymentTimeout,
            Settings.TimeLimits.PollInterval,
            $"{Application} to recover (Synced and Healthy) after the marker was removed",
            cancellationToken: cancellationToken);
    }
}
