using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// CAP-AZ-008: database data survives a nonprod rebuild. A canary row is written to <c>sandbox-tdd</c> before the
/// shared rebuild (<see cref="NonProdRebuild"/>) and read back after it: the database disk lives in
/// <c>rg-platform-nonprod-data</c>, outside the tier state. tdd is used so the uat canary stays as the latest backup
/// holds it (CAP-AZ-010). Ordered after the restore and rotation tests, which need the cluster as it was.
/// </summary>
[TestFixture]
[Order(3)]
[Category(Categories.Live)]
public class RebuildDataSurvivalTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-008")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(4 * 60 * 60 * 1000)]
    public async Task Should_ReadCanary_AfterNonProdRebuild_KeepTheRow()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        if (NonProdRebuild.Started)
        {
            throw new PlatformPrerequisiteException("The nonprod rebuild of this run started before this test could write its canary; run this fixture first (it carries [Order(3)]).");
        }

        var expected = $"rebuild-{Run.RunId}";
        await DestructivePreflight.EnsureAsync(RunDestructivePreflightAsync, cancellationToken);
        await EnsureAwakeAsync(PlatformTier.NonProd, cancellationToken);
        var sandbox = await HealthySandboxAsync("tdd", cancellationToken);
        await ObserveAsync(async token =>
        {
            await sandbox.PutCanaryAsync(expected, token);
            return true;
        }, _ => true, TimeSpan.FromMinutes(10), cancellationToken);

        var rebuild = await NonProdRebuild.RunOnceAsync(RebuildNonProdAsync, cancellationToken);
        var canary = await ObserveAsync(sandbox.GetCanaryAsync, observed => observed?.Value == expected, TimeSpan.FromMinutes(45), cancellationToken);

        rebuild.IssuerAfter.ShouldNotBe(rebuild.IssuerBefore, "the cluster was not recreated, so the canary proves nothing about a rebuild");
        canary.ShouldNotBeNull($"{sandbox.BaseUri}data/canary has no row after the rebuild");
        canary.Value.ShouldBe(expected, $"the canary written before the rebuild was not kept ({sandbox.BaseUri})");
    }
}

/// <summary>
/// CAP-AZ-007 (live half): nonprod can be destroyed and rebuilt. After the shared rebuild (env-destroy, env-apply,
/// apps-apply for the sandbox) the old cluster was gone between the runs, a new cluster runs with a new OIDC issuer,
/// the ingress IP (and so the apps domain) is unchanged, and the sandbox Applications are Synced and Healthy again.
/// The offline half (no destroy runbook for infra-prod) is Platform.Conformance.Offline.Azure.TierRebuildTests.
/// </summary>
[TestFixture]
[Order(4)]
[Category(Categories.Live)]
public class TierRebuildTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-007")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(4 * 60 * 60 * 1000)]
    public async Task Should_RunEnvDestroyThenEnvApply_NonProd_RebuildTheCluster()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        string[] applications = ["tdd", "uat"];

        var rebuild = await NonProdRebuild.RunOnceAsync(RebuildNonProdAsync, cancellationToken);
        var state = await WaitForPowerStateAsync(PlatformTier.NonProd, running: true, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var unhealthy = new List<string>();
        foreach (var name in applications.SelectMany(environment => new[] { $"{AzurePlatform.Sandbox}-app-{environment}", $"{AzurePlatform.Sandbox}-db-{environment}" }))
        {
            var status = await ObserveAsync(
                token => cluster.GetArgoApplicationAsync(name, cancellationToken: token),
                observed => observed.IsSyncedAndHealthy,
                Settings.TimeLimits.ArgoSyncTimeout + Settings.TimeLimits.ArgoSyncTimeout,
                cancellationToken,
                exception => exception is PlatformApiException or HttpRequestException);
            if (!status.IsSyncedAndHealthy)
            {
                unhealthy.Add($"{name}: {status.SyncStatus}/{status.HealthStatus}");
            }
        }

        rebuild.IssuerBefore.ShouldNotBeNull("aks-platform-nonprod did not exist before env-destroy");
        rebuild.IssuerBetween.ShouldBeNull("env-destroy left aks-platform-nonprod in place");
        rebuild.IssuerAfter.ShouldNotBeNull("env-apply did not create aks-platform-nonprod");
        rebuild.IssuerAfter.ShouldNotBe(rebuild.IssuerBefore, "the rebuilt cluster kept the old OIDC issuer, so it was not recreated");
        rebuild.IngressAfter.ShouldBe(rebuild.IngressBefore, "the ingress IP changed, so every <app>-<env>.<apps-domain-nonprod> host broke");
        state.IsRunning.ShouldBeTrue($"{state}");
        unhealthy.ShouldBeEmpty("the sandbox Applications must be Synced and Healthy on the rebuilt cluster");
    }
}
