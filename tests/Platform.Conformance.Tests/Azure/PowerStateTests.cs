using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// CAP-AZ-004: alerts are muted while a cluster sleeps and unmuted after wake. Per tier, the alert processing rule
/// <c>apr-sleep-&lt;tier&gt;</c> is enabled while the cluster is stopped and disabled after the wake. The tests assert on the
/// tier's shared sleep and wake cycle (<see cref="TierSleepCycle"/>): nonprod is force-slept; prod only by env-sleep's own
/// rules (outside the working window, as in the nightly run), so a daytime run never stops prod and its asleep half stays
/// Inconclusive instead.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class SleepAlertTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-004")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_NonProdAsleep_BeEnabled() => AssertMutedWhileAsleepAsync(PlatformTier.NonProd);

    [Test]
    [Capability("CAP-AZ-004")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_NonProdAwake_BeDisabled() => AssertUnmutedAfterWakeAsync(PlatformTier.NonProd);

    [Test]
    [Capability("CAP-AZ-004")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_ProdAsleep_BeEnabled() => AssertMutedWhileAsleepAsync(PlatformTier.Prod);

    [Test]
    [Capability("CAP-AZ-004")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_ProdAwake_BeDisabled() => AssertUnmutedAfterWakeAsync(PlatformTier.Prod);

    private static async Task AssertMutedWhileAsleepAsync(PlatformTier tier)
    {
        var cycle = TierSleepCycle.For(tier);
        await cycle.RequireAsync(SleepPhase.Sleep, SleepPhase.Asleep);
        var rule = AzurePlatform.SleepRule(tier);

        var state = cycle.AsleepState!.Require(SleepPhase.Asleep);
        var observed = cycle.AsleepRule!.Require(SleepPhase.Asleep);

        state.PowerState.ShouldBe("Stopped", $"{state}");
        observed.Enabled.ShouldBeTrue($"{rule} must be enabled while {state.Name} sleeps, so nothing pages");
    }

    private static async Task AssertUnmutedAfterWakeAsync(PlatformTier tier)
    {
        var cycle = TierSleepCycle.For(tier);
        await cycle.RequireAsync(SleepPhase.Wake, SleepPhase.AwakeAfter);
        var rule = AzurePlatform.SleepRule(tier);

        var state = cycle.AwakeState!.Require(SleepPhase.AwakeAfter);
        var observed = cycle.AwakeRule!.Require(SleepPhase.AwakeAfter);

        state.IsRunning.ShouldBeTrue($"{state}");
        observed.Enabled.ShouldBeFalse($"{rule} must be disabled once env-wake has started {state.Name} ({cycle.WakeMethod}); an enabled rule hides every alert of a running cluster");
    }
}

/// <summary>
/// CAP-AZ-005: a stop succeeds with Kyverno installed (ADR-IR34 decision 22). On the nonprod sleep and wake cycle
/// (<see cref="TierSleepCycle"/>): Kyverno serves ready policies (its webhooks are registered) while the tier is awake,
/// env-sleep with <c>Sleep.Force</c> decides to sleep and succeeds, the cluster stops, and the webhooks are registered
/// again after the wake.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class StopWithAdmissionTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-005")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_RunEnvSleep_KyvernoWebhooksRegistered_StopTheCluster()
    {
        var cycle = TierSleepCycle.For(PlatformTier.NonProd);
        await cycle.RequireAsync(SleepPhase.AwakeBefore, SleepPhase.Sleep, SleepPhase.Asleep, SleepPhase.AwakeAfter);

        var readyPolicies = cycle.ReadyPoliciesBefore!.Require(SleepPhase.AwakeBefore);
        var webhooks = cycle.WebhooksBefore!.Require(SleepPhase.AwakeBefore);
        var decision = cycle.Decision!;
        var state = cycle.AsleepState!.Require(SleepPhase.Asleep);
        var webhooksAfter = cycle.WebhooksAfter!.Require(SleepPhase.AwakeAfter);

        readyPolicies.ShouldNotBeEmpty("Kyverno must serve ready policies (registered webhooks) before the stop, or the test proves nothing");
        webhooks?.ShouldNotBeEmpty("Kyverno's validating webhook configurations must be registered before the stop");
        (decision is { Decision: "sleep", DryRun: false }).ShouldBeTrue($"env-sleep with Sleep.Force decided '{decision.Decision}': {decision.Reason}");
        state.PowerState.ShouldBe("Stopped", $"{state}");
        webhooksAfter?.ShouldNotBeEmpty("Kyverno's validating webhook configurations must be registered again after the wake");
    }
}

/// <summary>
/// CAP-AZ-006: the tier layer is idempotent. env-plan (which wakes the tier first) plans terraform/tier with the
/// committed tfvars and reports no changes in both tiers. The two tiers plan in parallel; each takes its tier's
/// <see cref="TierLock"/>, so env-plan never wakes a tier while its sleep and wake cycle runs.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class TierIdempotenceTests : AzureConformanceTest
{
    /// <summary>
    /// Lets a sleep and wake cycle of the same run take the tier first when both start together, so env-plan does not wake
    /// a prod cluster that conformance-arm stopped before the prod cycle could observe it asleep.
    /// </summary>
    private static readonly TimeSpan CycleHeadStart = TimeSpan.FromMinutes(1);

    [Test]
    [Capability("CAP-AZ-006")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(4 * 60 * 60 * 1000)]
    public Task Should_RunEnvPlan_NonProd_ReportNoChanges() => AssertNoChangesAsync(PlatformTier.NonProd);

    [Test]
    [Capability("CAP-AZ-006")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(4 * 60 * 60 * 1000)]
    public Task Should_RunEnvPlan_Prod_ReportNoChanges() => AssertNoChangesAsync(PlatformTier.Prod);

    private async Task AssertNoChangesAsync(PlatformTier tier)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await Task.Delay(CycleHeadStart, cancellationToken);
        using var hold = await TierLock.AcquireAsync(tier, $"env-plan in {AzurePlatform.InfraEnvironment(tier)}", CycleProgress.Write, cancellationToken: cancellationToken);

        var outcome = await RunInfrastructureRunbookAsync("env-plan", tier, null, Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.WakeTimeout, approve: false, cancellationToken);
        var summary = RunbookLogs.PlanSummary(outcome.Log);

        summary.ShouldNotBeNull($"{outcome} logged no Terraform plan summary");
        summary.NoChanges.ShouldBeTrue($"terraform/tier is not idempotent in {tier.ToKey()}: {summary.Line} (plan attached to the result)");
    }
}
