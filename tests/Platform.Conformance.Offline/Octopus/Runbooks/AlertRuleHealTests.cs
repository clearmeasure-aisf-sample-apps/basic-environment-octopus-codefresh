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

    /// <summary>
    /// Step Heal alert rule, the first step of env-sleep, in every state: only an enabled rule on a running cluster that is
    /// not stopping is disabled; a rule it cannot read or a cluster it cannot read is left alone with a warning.
    /// </summary>
    /// <param name="enabled">What the rule reports, or <c>missing</c> or <c>unreadable</c>.</param>
    /// <param name="state">Power and provisioning state, or <c>unreadable</c>; <c>-</c> when the cluster is not read.</param>
    /// <param name="healed"><c>true</c> when the rule must be disabled.</param>
    /// <param name="message">The warning or log line that names the outcome.</param>
    [TestCase("true", "Running/Failed", true, "WARNING Healed: alert processing rule apr-sleep-nonprod was enabled while aks-platform-nonprod is Running/Failed (a stop that Azure accepted and that then failed, or a wake that did not finish); disabled it, alerts notify again.")]
    [TestCase("True", "Running/Succeeded", true, "WARNING Healed: alert processing rule apr-sleep-nonprod was enabled while aks-platform-nonprod is Running/Succeeded (a stop that Azure accepted and that then failed, or a wake that did not finish); disabled it, alerts notify again.")]
    [TestCase("true", "Running/Starting", true, "WARNING Healed: alert processing rule apr-sleep-nonprod was enabled while aks-platform-nonprod is Running/Starting (a stop that Azure accepted and that then failed, or a wake that did not finish); disabled it, alerts notify again.")]
    [TestCase("true", "Running/Stopping", false, "LOG Alert processing rule apr-sleep-nonprod is enabled while aks-platform-nonprod is Running/Stopping: consistent.")]
    [TestCase("true", "Stopped/Succeeded", false, "LOG Alert processing rule apr-sleep-nonprod is enabled while aks-platform-nonprod is Stopped/Succeeded: consistent.")]
    [TestCase("true", "unreadable", false, "WARNING Alert processing rule apr-sleep-nonprod is enabled, and cluster aks-platform-nonprod was not found in rg-platform-nonprod-aks (or cannot be read). Not healed.")]
    [TestCase("false", "-", false, "LOG Alert processing rule apr-sleep-nonprod is disabled; nothing to heal.")]
    [TestCase("missing", "-", false, "LOG Alert processing rule apr-sleep-nonprod was not found in rg-platform-nonprod-aks; nothing to heal.")]
    [TestCase("unreadable", "-", false, "WARNING Cannot read alert processing rule apr-sleep-nonprod in rg-platform-nonprod-aks: ERROR: (AuthorizationFailed) no access. Not healed; the next run checks again.")]
    [Capability("CAP-AZ-004")]
    public void Should_HealAlertRule_RuleAndClusterState_DisablesOnlyAMutedRunningCluster(string enabled, string state, bool healed, string message)
    {
        var script = RunbookScript.Of($"{Runbooks}/env-sleep.ocl", "heal-alert-rule").InTier();
        _ = enabled switch
        {
            "missing" => script.Reply(RuleEnabled, exitCode: 3, error: "ERROR: (ResourceNotFound) The Resource 'Microsoft.AlertsManagement/actionRules/apr-sleep-nonprod' was not found.\n"),
            "unreadable" => script.Reply(RuleEnabled, exitCode: 1, error: "ERROR: (AuthorizationFailed) no access\n"),
            _ => script.Reply(RuleEnabled, enabled + "\n"),
        };
        _ = state == "unreadable"
            ? script.Reply(AksState, exitCode: 1, error: "ERROR: timeout\n")
            : script.Reply(AksState, state.Replace('/', '\n') + "\n");
        script.Reply(RuleOff);

        var run = script.Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Events.ShouldBe([message], run.Transcript);
        run.CallsMatching("--enabled false").Count.ShouldBe(healed ? 1 : 0, run.Transcript);
        run.CallsMatching("aks show").Count.ShouldBe(state == "-" ? 0 : 1, run.Transcript);
    }

    /// <summary>A rule that stays muted on a running cluster because the disable fails fails the step, so the run is red.</summary>
    [Test]
    [Capability("CAP-AZ-004")]
    public void Should_HealAlertRule_DisableFails_FailsTheStep()
    {
        var run = RunbookScript.Of($"{Runbooks}/env-sleep.ocl", "heal-alert-rule").InTier()
            .Reply(RuleEnabled, "true\n")
            .Reply(AksState, "Running\nFailed\n")
            .Reply(RuleOff, exitCode: 1, error: "ERROR: (AuthorizationFailed)\n")
            .Run();

        run.Failure.ShouldBe("Alert processing rule apr-sleep-nonprod is enabled while aks-platform-nonprod is Running/Failed, and disabling it failed (see above): its alerts stay muted. Disable it by hand (docs/runbooks/sleep-and-wake.md).", run.Transcript);
    }

    /// <summary>The heal is the first step of env-sleep: an Azure step with the tier's lifecycle account, on every run.</summary>
    [Test]
    [Capability("CAP-AZ-004")]
    public void Should_ReadEnvSleep_HealStep_RunsFirstOnEveryRun()
    {
        var steps = OctopusRepository.Steps(OctopusRepository.Read($"{Runbooks}/env-sleep.ocl"));

        steps.Select(step => step.Slug).ShouldBe(["heal-alert-rule", "decide-sleep", "stop-cluster"]);
        var heal = steps[0].Text;
        heal.ShouldContain("action_type = \"Octopus.AzurePowerShell\"");
        heal.ShouldContain("Octopus.Action.Azure.AccountId = \"#{Azure.LifecycleAccount}\"");
        heal.ShouldContain("worker_pool = \"hosted-ubuntu\"");
        heal.ShouldContain("environments = [\"infra-nonprod\", \"infra-prod\"]");
        heal.ShouldNotContain("condition");
    }

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
