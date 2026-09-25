using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-011: data survives a cluster sleep. On the nonprod sleep and wake cycle (<see cref="TierSleepCycle"/>): the
/// awake-before phase writes a canary row through the sandbox's <c>PUT /data/canary</c> on
/// <c>https://sandbox-tdd.&lt;apps-domain-nonprod&gt;</c>, the tier is force-slept (runbook <c>env-sleep</c> with
/// <c>Sleep.Force</c>), the stop settles (<see cref="Harness.Support.StopSettle"/>), env-wake starts it, and the
/// awake-after phase reads the row back: the database's static volume <c>disk-sandbox-tdd-db</c> kept it. The cluster is
/// left awake; the pipeline's teardown sleeps what the run woke.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class SleepDataSurvivalTests : GitOpsTestBase
{
    [Test]
    [Capability("CAP-GIT-011")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_Sleep_CanaryRowWrittenBeforeForceSleep_IsReadAfterWake()
    {
        var cycle = TierSleepCycle.For(PlatformTier.NonProd);
        await cycle.RequireAsync(SleepPhase.AwakeBefore, SleepPhase.Sleep, SleepPhase.Wake, SleepPhase.AwakeAfter);

        var written = cycle.CanaryWrite!.Require(SleepPhase.AwakeBefore);
        var stored = cycle.CanaryRead!.Require(SleepPhase.AwakeAfter);

        stored.ShouldBe(written, $"the canary row written through {cycle.CanaryUrl} before the sleep ({cycle.WakeMethod})");
    }
}
