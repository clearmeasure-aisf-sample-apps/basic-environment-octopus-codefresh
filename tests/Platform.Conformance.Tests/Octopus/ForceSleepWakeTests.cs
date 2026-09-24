using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-010: force-sleep and force-wake work on demand, in both app tiers. env-sleep with <c>Sleep.Force</c> stops the
/// cluster (never while a task runs); env-wake starts it. The power state comes from Azure Resource Manager. Tests never
/// force-sleep prod: its force-sleep is observed on the forced env-sleep run of infra-prod that
/// platform-env/conformance-arm makes before each nightly run, and the prod wake starts from a stop that env-sleep's own
/// rules made.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ForceSleepWakeTests : OctopusCapabilityTestBase
{
    private static readonly TimeSpan ArmWindow = TimeSpan.FromHours(26);

    /// <summary>Force-sleep stops aks-platform-nonprod.</summary>
    [Test]
    [Order(1)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_EnvSleep_ForceNonprod_StopsCluster()
    {
        var cluster = RequireTier(PlatformTier.NonProd, "the force-sleep test");
        Rest("the force-sleep test");
        if (!(await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning)
        {
            await WakeAsync(PlatformTier.NonProd);
        }

        await ForceSleepAsync(PlatformTier.NonProd);

        (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).PowerState.ShouldBe("Stopped");
    }

    /// <summary>env-wake starts aks-platform-nonprod.</summary>
    [Test]
    [Order(2)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public Task Should_EnvWake_Nonprod_StartsCluster() => StartsAsync(PlatformTier.NonProd);

    /// <summary>
    /// Force-sleep of aks-platform-prod, observed without forcing it: the newest forced env-sleep of infra-prod in the last
    /// 26 hours (conformance-arm's) decided to sleep because it was forced and stopped the cluster or found it stopped.
    /// </summary>
    [Test]
    [Order(3)]
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
    [Order(4)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public Task Should_EnvWake_Prod_StartsCluster() => StartsAsync(PlatformTier.Prod);

    private async Task StartsAsync(PlatformTier tier)
    {
        var cluster = RequireTier(tier, "the force-wake test");
        Rest("the force-wake test");
        if ((await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning)
        {
            if (tier == PlatformTier.Prod)
            {
                await SleepByScheduleAsync(tier);
            }
            else
            {
                await ForceSleepAsync(tier);
            }
        }

        await WakeAsync(tier);

        (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning.ShouldBeTrue();
    }
}
