using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-009: migrations finish before new pods start. In <c>sandbox-tdd</c>, the PreSync hook Job <c>db-migrate</c> of
/// the last sync (kept until the next one, <c>BeforeHookCreation</c>) must have completed before the ReplicaSet that sync
/// created for Deployment <c>web</c>. The nightly release commit of the sandbox (platform-env/conformance-arm) makes a
/// rollout; when the last sync rolled nothing out, the order is not observable and the test is Inconclusive.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class MigrationOrderTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string RevisionAnnotation = "deployment.kubernetes.io/revision";

    [Test]
    [Capability("CAP-GIT-009")]
    [Category(Categories.NonProd)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Migrate_PreSyncJobInSandboxTdd_CompletesBeforeTheNewReplicaSet()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var job = await cluster.JobAsync(Namespace, "db-migrate", cancellationToken);
        if (job is null)
        {
            Unobservable($"no PreSync Job {Namespace}/db-migrate is recorded yet");
        }

        var deployment = await cluster.DeploymentAsync(Namespace, "web", cancellationToken);
        var revision = deployment?.Metadata?.Annotations?.TryGetValue(RevisionAnnotation, out var value) == true ? value : null;
        var current = (await cluster.ReplicaSetsAsync(Namespace, cancellationToken))
            .Where(set => set.Metadata.OwnerReferences?.Any(owner => owner.Kind == "Deployment" && owner.Name == "web") == true)
            .FirstOrDefault(set => set.Metadata.Annotations?.TryGetValue(RevisionAnnotation, out var setRevision) == true && setRevision == revision);
        var started = job.Status?.StartTime ?? job.Metadata.CreationTimestamp;
        var rolledOut = current?.Metadata?.CreationTimestamp;
        if (current is null || rolledOut is null || started is null || rolledOut < started)
        {
            Unobservable($"the last sync (PreSync at {started:u}) rolled out no new ReplicaSet of {Namespace}/web (current created {rolledOut:u}); the next release rollout shows the order");
        }

        (job.Metadata.Annotations?.TryGetValue("argocd.argoproj.io/hook", out var hook) == true ? hook : null).ShouldBe("PreSync", $"Job {Namespace}/db-migrate is not a PreSync hook");
        (job.Status?.Succeeded ?? 0).ShouldBeGreaterThan(0, $"Job {Namespace}/db-migrate did not succeed, yet {current.Metadata.Name} was created at {rolledOut:u}");
        job.Status!.CompletionTime.ShouldNotBeNull();
        job.Status.CompletionTime.Value.ShouldBeLessThanOrEqualTo(rolledOut.Value, $"ReplicaSet {current.Metadata.Name} was created before the migration completed");
    }
}
