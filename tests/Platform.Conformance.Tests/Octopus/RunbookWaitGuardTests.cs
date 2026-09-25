using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-011: app runbooks wait for a sleeping cluster and fail with guidance. An app runbook holds no platform key, so
/// its first step waits up to <c>Wake.WaitMinutes</c> for the app to answer and then names both ways to wake the cluster.
/// The run happens in the asleep phase of the nonprod sleep and wake cycle (<see cref="TierSleepCycle"/>).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class RunbookWaitGuardTests : OctopusCapabilityTestBase
{
    /// <summary>db-restore of sandbox in uat, with nonprod asleep and Wake.WaitMinutes=1, fails and says how to wake it.</summary>
    [Test]
    [Capability("CAP-OCT-011")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_DbRestore_ClusterAsleepWithShortWait_FailsWithGuidance()
    {
        var cycle = TierSleepCycle.For(PlatformTier.NonProd);
        await cycle.RequireAsync(SleepPhase.Sleep, SleepPhase.Asleep);

        var (task, log) = cycle.WaitGuardRun!.Require(SleepPhase.Asleep);

        task.FinishedSuccessfully.ShouldBeFalse($"db-restore ran against a sleeping cluster: {task}");
        log.ShouldContain("did not answer");
        log.ShouldContain("within 1 minutes");
        log.ShouldContain("deploy the latest platform-wake release to uat, or run runbook env-wake of platform-infrastructure in infra-nonprod");
    }
}
