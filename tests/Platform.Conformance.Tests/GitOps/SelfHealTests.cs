using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-001: Argo CD self-heals drift in app namespaces. A manual change to a field that Git declares (a label of
/// Deployment <c>web</c> in <c>sandbox-tdd</c>) must be reverted by Application <c>sandbox-app-tdd</c> within five minutes
/// (automated sync with <c>selfHeal: true</c>, rendered by the tenant chart).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SelfHealTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string Application = "sandbox-app-tdd";
    private const string Deployment = "web";
    private const string Label = "app.kubernetes.io/component";
    private static readonly TimeSpan RevertBudget = TimeSpan.FromMinutes(5);

    [Test]
    [Capability("CAP-GIT-001")]
    [Category(Categories.NonProd)]
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_SelfHeal_ManualLabelChangeInSandboxTdd_IsRevertedWithinFiveMinutes()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var kubernetes = await KubernetesAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var application = await kubernetes.GetArgoApplicationAsync(Application, cancellationToken: cancellationToken);
        if (!application.IsSyncedAndHealthy)
        {
            Unobservable($"{Application} is {application.SyncStatus}/{application.HealthStatus}; drift is measured from Synced and Healthy");
        }

        var deployment = await cluster.DeploymentAsync(Namespace, Deployment, cancellationToken);
        var declared = deployment?.Metadata?.Labels?.TryGetValue(Label, out var value) == true ? value : null;
        declared.ShouldNotBeNull($"Deployment {Namespace}/{Deployment} carries no {Label} label from Git");
        Cleanup.Register($"restore label {Label}={declared} on {Namespace}/{Deployment}", token => cluster.SetDeploymentLabelAsync(Namespace, Deployment, Label, declared, token));
        var drifted = DateTimeOffset.UtcNow;

        await cluster.SetDeploymentLabelAsync(Namespace, Deployment, Label, "conformance-drift", cancellationToken);

        var reverted = await Poll.UntilAsync(
            async token => (await cluster.DeploymentAsync(Namespace, Deployment, token))?.Metadata?.Labels?.TryGetValue(Label, out var current) == true ? current : null,
            current => current == declared,
            RevertBudget,
            TimeSpan.FromSeconds(5),
            $"Argo CD ({Application}) to revert label {Label} of {Namespace}/{Deployment} to {declared}",
            cancellationToken: cancellationToken);
        reverted.ShouldBe(declared);
        (DateTimeOffset.UtcNow - drifted).ShouldBeLessThanOrEqualTo(RevertBudget + TimeSpan.FromSeconds(30));
    }
}
