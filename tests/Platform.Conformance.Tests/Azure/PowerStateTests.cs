using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// CAP-AZ-004: alerts are muted while a cluster sleeps and unmuted after wake. Per tier, the alert processing rule
/// <c>apr-sleep-&lt;tier&gt;</c> is enabled while the cluster is stopped and disabled after env-wake. A running tier is put to
/// sleep with env-sleep first: forced in nonprod; in prod only by env-sleep's own rules (outside the working window, as
/// in the nightly run), so a daytime run never stops prod and stays Inconclusive instead.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SleepAlertTests : AzureConformanceTest
{
    [Test]
    [Order(1)]
    [Capability("CAP-AZ-004")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_NonProdAsleep_BeEnabled() => AssertMutedWhileAsleepAsync(PlatformTier.NonProd, force: true);

    [Test]
    [Order(2)]
    [Capability("CAP-AZ-004")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_NonProdAwake_BeDisabled() => AssertUnmutedAfterWakeAsync(PlatformTier.NonProd);

    [Test]
    [Order(3)]
    [Capability("CAP-AZ-004")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_ProdAsleep_BeEnabled() => AssertMutedWhileAsleepAsync(PlatformTier.Prod, force: false);

    [Test]
    [Order(4)]
    [Capability("CAP-AZ-004")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public Task Should_GetAlertProcessingRule_ProdAwake_BeDisabled() => AssertUnmutedAfterWakeAsync(PlatformTier.Prod);

    private async Task AssertMutedWhileAsleepAsync(PlatformTier tier, bool force)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var rule = AzurePlatform.SleepRule(tier);
        if ((await ClusterStateAsync(tier, cancellationToken)).IsRunning)
        {
            var decision = await SleepAsync(tier, force, cancellationToken);
            if (!decision.Sleeps)
            {
                throw new PlatformPrerequisiteException(
                    $"env-sleep kept {AzurePlatform.ClusterName(tier)} awake ({decision.Reason}); the asleep half of CAP-AZ-004 needs a sleeping tier, as in the nightly run after conformance-arm.");
            }
        }

        var state = await WaitForPowerStateAsync(tier, running: false, cancellationToken);
        var observed = await Azure.GetAlertProcessingRuleAsync(AzurePlatform.ClusterGroup(tier), rule, cancellationToken);

        state.PowerState.ShouldBe("Stopped", $"{state}");
        observed.Enabled.ShouldBeTrue($"{rule} must be enabled while {state.Name} sleeps, so nothing pages");
    }

    private async Task AssertUnmutedAfterWakeAsync(PlatformTier tier)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var rule = AzurePlatform.SleepRule(tier);

        await WakeAsync(tier, cancellationToken);
        var observed = await ObserveAsync(
            token => Azure.GetAlertProcessingRuleAsync(AzurePlatform.ClusterGroup(tier), rule, token),
            candidate => !candidate.Enabled,
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var state = await ClusterStateAsync(tier, cancellationToken);

        state.IsRunning.ShouldBeTrue($"{state}");
        observed.Enabled.ShouldBeFalse($"{rule} must be disabled once env-wake has started {state.Name}; an enabled rule hides every alert of a running cluster");
    }
}

/// <summary>
/// CAP-AZ-005: a stop succeeds with Kyverno installed (ADR-IR34 decision 22). The test wakes nonprod, checks that Kyverno
/// serves ready policies (its webhooks are registered), force-sleeps the tier with env-sleep and expects the runbook to
/// succeed and the cluster to stop.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class StopWithAdmissionTests : AzureConformanceTest
{
    private static readonly CustomResourceKind ValidatingWebhooks = new("admissionregistration.k8s.io", "v1", "validatingwebhookconfigurations");

    [Test]
    [Capability("CAP-AZ-005")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(75 * 60 * 1000)]
    public async Task Should_RunEnvSleep_KyvernoWebhooksRegistered_StopTheCluster()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await EnsureAwakeAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var readyPolicies = (await cluster.ListKyvernoPoliciesAsync(cancellationToken)).Where(policy => policy.Ready == true).Select(policy => policy.Name).ToArray();
        var webhooks = await KyvernoWebhooksAsync(cluster, cancellationToken);

        var decision = await SleepAsync(PlatformTier.NonProd, force: true, cancellationToken);
        var state = await WaitForPowerStateAsync(PlatformTier.NonProd, running: false, cancellationToken);

        readyPolicies.ShouldNotBeEmpty("Kyverno must serve ready policies (registered webhooks) before the stop, or the test proves nothing");
        webhooks?.ShouldNotBeEmpty("Kyverno's validating webhook configurations must be registered before the stop");
        decision.Sleeps.ShouldBeTrue($"env-sleep with Sleep.Force decided '{decision.Decision}': {decision.Reason}");
        state.PowerState.ShouldBe("Stopped", $"{state}");
    }

    private static async Task<IReadOnlyList<string>?> KyvernoWebhooksAsync(IKubernetesApi cluster, CancellationToken cancellationToken)
    {
        try
        {
            return (await cluster.ListCustomObjectsAsync(ValidatingWebhooks, null, cancellationToken))
                .Select(item => ArmReader.Text(item, "metadata", "name") ?? string.Empty)
                .Where(name => name.Contains("kyverno", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (PlatformApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            TestContext.Out.WriteLine($"Webhook configurations are not readable with AKS RBAC Reader; the ready policies stand in for them: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            TestContext.Out.WriteLine($"Webhook configurations could not be read: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// CAP-AZ-006: the tier layer is idempotent. env-plan (which wakes the tier first) plans terraform/tier with the
/// committed tfvars and reports no changes in both tiers.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class TierIdempotenceTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-006")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public Task Should_RunEnvPlan_NonProd_ReportNoChanges() => AssertNoChangesAsync(PlatformTier.NonProd);

    [Test]
    [Capability("CAP-AZ-006")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public Task Should_RunEnvPlan_Prod_ReportNoChanges() => AssertNoChangesAsync(PlatformTier.Prod);

    private async Task AssertNoChangesAsync(PlatformTier tier)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        var outcome = await RunInfrastructureRunbookAsync("env-plan", tier, null, Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.WakeTimeout, approve: false, cancellationToken);
        var summary = RunbookLogs.PlanSummary(outcome.Log);

        summary.ShouldNotBeNull($"{outcome} logged no Terraform plan summary");
        summary.NoChanges.ShouldBeTrue($"terraform/tier is not idempotent in {tier.ToKey()}: {summary.Line} (plan attached to the result)");
    }
}
