using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-008: a deployment to a stopped cluster wakes it first and succeeds. The test stops the tier (nonprod with
/// <c>Sleep.Force</c>; prod only by env-sleep's own rules, never forced, so a daytime run stays Inconclusive), waits out
/// the stop grace (E50), deploys, and checks the chain: a platform-wake child deployment, an env-wake run in the
/// infrastructure environment, the cluster running and the deployment succeeding within <c>Wake.TimeoutMinutes</c>.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class WakeOnDeploymentTests : OctopusCapabilityTestBase
{
    /// <summary>A tdd deployment while nonprod sleeps wakes the cluster and succeeds.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_TddWhileNonprodSleeps_WakesAndSucceeds()
    {
        Rest("the nonprod wake test");
        var release = await LastDeployedReleaseAsync("tdd");
        if (release is null)
        {
            Assert.Inconclusive("no sandbox release has been deployed to tdd yet (P1-11)");
        }

        await ForceSleepAsync(PlatformTier.NonProd);
        await WaitOutStopGraceAsync(PlatformTier.NonProd);
        var started = DateTimeOffset.UtcNow;

        var task = await DeployAndCompleteAsync(release!, "tdd");

        await AssertWokenAsync(PlatformTier.NonProd, "tdd", started, task);
    }

    /// <summary>A promotion to prod while prod sleeps wakes the cluster and succeeds.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_PromotionWhileProdSleeps_WakesAndSucceeds()
    {
        Rest("the prod wake test");
        var release = await ReleaseReadyForProdAsync("Default");
        await SleepByScheduleAsync(PlatformTier.Prod);
        await WaitOutStopGraceAsync(PlatformTier.Prod);
        var started = DateTimeOffset.UtcNow;

        var task = await DeployAndCompleteAsync(release, "prod");

        await AssertWokenAsync(PlatformTier.Prod, "prod", started, task);
    }

    private async Task AssertWokenAsync(PlatformTier tier, string environment, DateTimeOffset started, OctopusTask deployment)
    {
        var cluster = RequireTier(tier, "the wake check");
        (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning.ShouldBeTrue();
        var child = (await Octopus.GetTasksAsync(new OctopusTaskQuery { Project = WakeProject, Environment = environment, Take = 10 }, Token))
            .FirstOrDefault(task => task.QueueTime >= started);
        child.ShouldNotBeNull($"no {WakeProject} deployment to {environment} since {started:O}");
        child.FinishedSuccessfully.ShouldBeTrue($"{WakeProject} child {child}");
        var wake = (await RunbookTasksAsync(InfraEnvironment(tier), "env-wake")).FirstOrDefault(task => task.QueueTime >= started);
        wake.ShouldNotBeNull($"no env-wake run in {InfraEnvironment(tier)} since {started:O}");
        wake.FinishedSuccessfully.ShouldBeTrue($"env-wake {wake}");
        var variables = await Octopus.GetProjectVariablesAsync(InfrastructureProject, OctopusRunbookRunRequest.MainBranch, Token);
        var timeout = int.TryParse(VariableValue(variables, "Wake.TimeoutMinutes", await EnvironmentIdAsync(InfraEnvironment(tier))), out var minutes) ? minutes : 30;
        (wake.CompletedTime!.Value - wake.StartTime!.Value).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(timeout), "the wake took longer than Wake.TimeoutMinutes");
        deployment.FinishedSuccessfully.ShouldBeTrue();
    }
}
