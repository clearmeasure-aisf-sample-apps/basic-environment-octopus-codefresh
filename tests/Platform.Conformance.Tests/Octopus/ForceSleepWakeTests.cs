using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-010: force-sleep and force-wake work on demand, in both app tiers. env-sleep with <c>Sleep.Force</c> stops the
/// cluster (never while a task runs); env-wake starts it. The power state comes from Azure Resource Manager.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ForceSleepWakeTests : OctopusCapabilityTestBase
{
    /// <summary>Force-sleep stops aks-platform-nonprod.</summary>
    [Test]
    [Order(1)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_EnvSleep_ForceNonprod_StopsCluster() => StopsAsync(PlatformTier.NonProd);

    /// <summary>env-wake starts aks-platform-nonprod.</summary>
    [Test]
    [Order(2)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_EnvWake_Nonprod_StartsCluster() => StartsAsync(PlatformTier.NonProd);

    /// <summary>Force-sleep stops aks-platform-prod.</summary>
    [Test]
    [Order(3)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_EnvSleep_ForceProd_StopsCluster() => StopsAsync(PlatformTier.Prod);

    /// <summary>env-wake starts aks-platform-prod.</summary>
    [Test]
    [Order(4)]
    [Capability("CAP-OCT-010")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_EnvWake_Prod_StartsCluster() => StartsAsync(PlatformTier.Prod);

    private async Task StopsAsync(PlatformTier tier)
    {
        var cluster = RequireTier(tier, "the force-sleep test");
        Rest("the force-sleep test");
        if (!(await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning)
        {
            await WakeAsync(tier);
        }

        await ForceSleepAsync(tier);

        (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).PowerState.ShouldBe("Stopped");
    }

    private async Task StartsAsync(PlatformTier tier)
    {
        var cluster = RequireTier(tier, "the force-wake test");
        Rest("the force-wake test");
        if ((await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning)
        {
            await ForceSleepAsync(tier);
        }

        await WakeAsync(tier);

        (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning.ShouldBeTrue();
    }
}
