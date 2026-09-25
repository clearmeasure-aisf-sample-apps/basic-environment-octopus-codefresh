using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-008: a deployment to a stopped cluster wakes it first and succeeds. The wake phase of the tier's shared sleep and
/// wake cycle (<see cref="TierSleepCycle"/>) is that deployment: once the tier sleeps (nonprod with <c>Sleep.Force</c>;
/// prod only by env-sleep's own rules, never forced, so a daytime run stays Inconclusive) and the stop has settled, it
/// deploys the release running in tdd to tdd, or promotes the newest Default release deployed to uat to prod. The tests
/// check the chain: the power state Stopped before, a platform-wake child deployment, an env-wake run in the
/// infrastructure environment within <c>Wake.TimeoutMinutes</c>, the cluster running and the deployment succeeding.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class WakeOnDeploymentTests : OctopusCapabilityTestBase
{
    /// <summary>A tdd deployment while nonprod sleeps wakes the cluster and succeeds.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_DeployReleaseAsync_TddWhileNonprodSleeps_WakesAndSucceeds() => AssertWokenAsync(PlatformTier.NonProd);

    /// <summary>A promotion to prod while prod sleeps wakes the cluster and succeeds.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_DeployReleaseAsync_PromotionWhileProdSleeps_WakesAndSucceeds() => AssertWokenAsync(PlatformTier.Prod);

    private static async Task AssertWokenAsync(PlatformTier tier)
    {
        var cycle = TierSleepCycle.For(tier);
        await cycle.RequireAsync(SleepPhase.Sleep, SleepPhase.Asleep, SleepPhase.Wake, SleepPhase.AwakeAfter);

        var before = cycle.AsleepState!.Require(SleepPhase.Asleep);
        var woken = cycle.WakeDeployment!.Require(SleepPhase.Wake);
        var after = cycle.AwakeState!.Require(SleepPhase.AwakeAfter);

        before.PowerState.ShouldBe("Stopped", $"{before} before the deployment");
        woken.PlatformWake.ShouldNotBeNull($"no {WakeProject} deployment to {woken.Environment} since {woken.Started:O}");
        woken.PlatformWake.FinishedSuccessfully.ShouldBeTrue($"{WakeProject} child {woken.PlatformWake}");
        woken.EnvWake.ShouldNotBeNull($"no env-wake run in {InfraEnvironment(tier)} since {woken.Started:O}");
        woken.EnvWake.FinishedSuccessfully.ShouldBeTrue($"env-wake {woken.EnvWake}");
        (woken.EnvWake.CompletedTime!.Value - woken.EnvWake.StartTime!.Value).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(woken.WakeTimeoutMinutes), "the wake took longer than Wake.TimeoutMinutes");
        after.IsRunning.ShouldBeTrue($"{after}");
        woken.Deployment.FinishedSuccessfully.ShouldBeTrue($"deployment of sandbox {woken.Release} to {woken.Environment}: {woken.Deployment}");
    }
}
