using System.Globalization;
using k8s.Models;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-006: quotas and limit ranges are enforced. Server-side dry runs of bare pods in <c>sandbox-tdd</c> (admission
/// runs on a dry run, Q46): a pod requesting more CPU than ResourceQuota <c>tenant</c> allows is refused, a container
/// whose memory limit exceeds LimitRange <c>tenant</c>'s maximum is refused, and a small pod is admitted. The pods use the
/// sandbox's own web image, so no image policy is involved.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class QuotaTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";

    [Test]
    [Capability("CAP-GIT-006")]
    [Category(Categories.NonProd)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Admit_OversizedPodsInSandboxTdd_AreRefusedAndSmallPodIsAdmitted()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var kubernetes = await KubernetesAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var quota = (await kubernetes.ListResourceQuotasAsync(Namespace, cancellationToken)).FirstOrDefault(candidate => candidate.Name == "tenant");
        quota.ShouldNotBeNull($"ResourceQuota {Namespace}/tenant is missing");
        quota.Hard.TryGetValue("requests.cpu", out var hardCpu).ShouldBeTrue("ResourceQuota tenant has no requests.cpu");
        var limitRange = await cluster.LimitRangeAsync(Namespace, "tenant", cancellationToken);
        var maxMemory = limitRange?.Spec?.Limits?.FirstOrDefault(limit => limit.Type == "Container")?.Max?.TryGetValue("memory", out var max) == true ? max : null;
        maxMemory.ShouldNotBeNull($"LimitRange {Namespace}/tenant has no Container max memory");
        var image = (await kubernetes.ListDeploymentsAsync(Namespace, cancellationToken: cancellationToken)).FirstOrDefault(deployment => deployment.Name == "web")?.Images.FirstOrDefault();
        if (image is null)
        {
            Unobservable($"Deployment {Namespace}/web is missing; its image names the pods");
        }

        var overQuotaCpu = new ResourceQuantity(hardCpu).ToDecimal() + 1m;
        var overLimitMemory = $"{(long)(maxMemory.ToDecimal() / (1024 * 1024)) + 1024}Mi";

        var overQuota = await kubernetes.CreatePodAsync(Pod("over-quota", image, overQuotaCpu.ToString(CultureInfo.InvariantCulture), "64Mi", "128Mi"), dryRun: true, cancellationToken);
        var overLimit = await kubernetes.CreatePodAsync(Pod("over-limit", image, "50m", "64Mi", overLimitMemory), dryRun: true, cancellationToken);
        var small = await kubernetes.CreatePodAsync(Pod("small", image, "50m", "64Mi", "128Mi"), dryRun: true, cancellationToken);

        Assert.Multiple(() =>
        {
            overQuota.Created.ShouldBeFalse($"a pod requesting {overQuotaCpu} CPU (quota {hardCpu}) was admitted");
            (overQuota.Message ?? string.Empty).ShouldContain("exceeded quota", Case.Insensitive);
            overLimit.Created.ShouldBeFalse($"a container limited to {overLimitMemory} (maximum {maxMemory}) was admitted");
            (overLimit.Message ?? string.Empty).ShouldContain("maximum memory", Case.Insensitive);
            small.Created.ShouldBeTrue($"a small pod was refused: {small.Message}");
        });
    }

    private V1Pod Pod(string purpose, string image, string cpuRequest, string memoryRequest, string memoryLimit)
    {
        var pod = ProbePod(Namespace, Run.ResourceName($"quota-{purpose}"), image, FixtureLabels, ["/bin/true"]);
        pod.Spec.Containers[0].Resources = new V1ResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(cpuRequest), ["memory"] = new(memoryRequest) },
            Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new(memoryLimit) },
        };
        return pod;
    }
}
