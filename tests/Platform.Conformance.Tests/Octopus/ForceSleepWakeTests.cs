using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-010: force-sleep and force-wake work on demand, in both app tiers. env-sleep with <c>Sleep.Force</c> stops the
/// cluster (never while a task runs); env-wake starts it. The power state comes from Azure Resource Manager. The nonprod
/// tests and the prod wake assert on the tier's shared sleep and wake cycle (<see cref="TierSleepCycle"/>), whose wake runs
/// env-wake through the platform-wake step of a deployment (or directly when there is nothing to deploy). Tests never
/// force-sleep prod: its force-sleep is observed on the forced env-sleep run of infra-prod that
/// platform-env/conformance-arm makes before each nightly run, and the prod wake starts from a stop that env-sleep's own
/// rules made (or the arm's, when the cycle finds prod stopped).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
[Parallelizable(ParallelScope.All)]
public class ForceSleepWakeTests : OctopusCapabilityTestBase
{
    private static readonly TimeSpan ArmWindow = TimeSpan.FromHours(26);

    /// <summary>Force-sleep stops aks-platform-nonprod.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_EnvSleep_ForceNonprod_StopsCluster()
    {
        var cycle = TierSleepCycle.For(PlatformTier.NonProd);
        await cycle.RequireAsync(SleepPhase.Sleep, SleepPhase.Asleep);

        var decision = cycle.Decision!;
        var state = cycle.AsleepState!.Require(SleepPhase.Asleep);

        decision.ShouldSatisfyAllConditions(
            () => decision.Decision.ShouldBe("sleep", $"env-sleep {cycle.SleepTask?.Id}: {decision}"),
            () => decision.Forced.ShouldBeTrue($"env-sleep {cycle.SleepTask?.Id} slept, but not because it was forced: {decision}"));
        state.PowerState.ShouldBe("Stopped", $"{state}");
    }

    /// <summary>env-wake starts aks-platform-nonprod.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_EnvWake_Nonprod_StartsCluster() => StartsAsync(PlatformTier.NonProd);

    /// <summary>
    /// Force-sleep of aks-platform-prod, observed without forcing it: the newest forced env-sleep of infra-prod in the last
    /// 26 hours (conformance-arm's) decided to sleep because it was forced and stopped the cluster or found it stopped.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-010")]
    [Category(Categories.Prod)]
    [CancelAfter(30 * 60 * 1000)]
    public async Task Should_EnvSleep_ForceProd_StopsCluster()
    {
        var cluster = RequireTier(PlatformTier.Prod, "the prod force-sleep observation");
        Rest("the prod force-sleep observation");
        var since = DateTimeOffset.UtcNow - ArmWindow;
        string? forcedLog = null;
        foreach (var task in (await RunbookTasksAsync(InfraEnvironment(PlatformTier.Prod), "env-sleep", take: 80)).Where(task => task.CompletedTime >= since && task.FinishedSuccessfully))
        {
            var log = await Octopus.GetTaskLogAsync(task.Id, Token);
            if (SleepDecision(log) is { Decision: "sleep", DryRun: false, Forced: true })
            {
                forcedLog = log;
                AttachArtifact($"env-sleep-forced-prod-{task.Id}.log", log);
                break;
            }
        }

        if (forcedLog is null)
        {
            Assert.Inconclusive(
                $"no forced env-sleep ran in {InfraEnvironment(PlatformTier.Prod)} in the last {ArmWindow.TotalHours:0} hours. Tests never force-sleep prod; "
                + "this observes the one platform-env/conformance-arm runs before each weekday night's suite.");
        }

        (forcedLog!.Contains($"Stopping {cluster.ClusterName}", StringComparison.Ordinal) || forcedLog.Contains($"{cluster.ClusterName} is already stopped", StringComparison.Ordinal))
            .ShouldBeTrue($"the forced env-sleep of {InfraEnvironment(PlatformTier.Prod)} neither stopped {cluster.ClusterName} nor found it stopped (log attached)");
    }

    /// <summary>env-wake starts aks-platform-prod (stopped by env-sleep's own rules when it runs, never forced).</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public Task Should_EnvWake_Prod_StartsCluster() => StartsAsync(PlatformTier.Prod);

    private static async Task StartsAsync(PlatformTier tier)
    {
        var cycle = TierSleepCycle.For(tier);
        await cycle.RequireAsync(SleepPhase.Sleep, SleepPhase.Wake, SleepPhase.AwakeAfter);

        var wake = cycle.EnvWakeTask;
        var state = cycle.AwakeState!.Require(SleepPhase.AwakeAfter);

        wake.ShouldNotBeNull($"no env-wake run started {state.Name} ({cycle.WakeMethod})");
        wake.FinishedSuccessfully.ShouldBeTrue($"env-wake {wake} ({cycle.WakeMethod})");
        state.IsRunning.ShouldBeTrue($"{state} after {cycle.WakeMethod}");
    }
}
