using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-AZ-004 (offline half): alerts are never muted on a running cluster. env-sleep enables apr-sleep-&lt;tier&gt; just
/// before 'az aks stop --no-wait'; a stop that Azure accepts and that then fails (or a wake that never finished) would
/// leave the cluster running with its alerts muted. So every env-sleep and env-wake run starts by healing: rule enabled,
/// power state Running and provisioning state not Stopping means disable the rule and log a warning that names the state.
/// The inline PowerShell runs under the stub Octopus runtime.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class AlertRuleHealTests
{
    private const string Runbooks = ".octopus/platform-infrastructure/runbooks";
    private const string RuleEnabled = "^az monitor alert-processing-rule show --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --query properties.enabled --output tsv --only-show-errors$";
    private const string AksState = @"^az aks show --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --query \[powerState\.code, provisioningState\] --output tsv$";
    private const string RuleOff = "^az monitor alert-processing-rule update --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --enabled false --output none --only-show-errors$";

    /// <summary>env-wake heals before it waits for the cluster: the rule is disabled first, with a warning.</summary>
    [Test]
    [Capability("CAP-AZ-004")]
    public void Should_StartCluster_RuleEnabledOnRunningCluster_HealsFirst()
    {
        var run = RunbookScript.Of($"{Runbooks}/env-wake.ocl", "start-cluster").InTier().With("Wake.TimeoutMinutes", "20")
            .Reply(RuleEnabled, "true\n")
            .Reply(AksState, "Running\nFailed\n", times: 1)
            .Reply(AksState, "Running\nSucceeded\n")
            .Reply("^az monitor alert-processing-rule show --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --output none$")
            .Reply(RuleOff)
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Calls.Take(3).Select(call => call.Line).ShouldBe(
        [
            "az monitor alert-processing-rule show --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --query properties.enabled --output tsv --only-show-errors",
            "az aks show --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --query [powerState.code, provisioningState] --output tsv",
            "az monitor alert-processing-rule update --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --enabled false --output none --only-show-errors",
        ], run.Transcript);
        run.Warnings.ShouldContain(
            "Healed: alert processing rule apr-sleep-nonprod was enabled while aks-platform-nonprod is Running/Failed (a stop that Azure accepted and that then failed, or a wake that did not finish); disabled it, alerts notify again.",
            run.Transcript);
        run.Outputs["Wake.ClusterStarted"].ShouldBe("False");
    }
}
