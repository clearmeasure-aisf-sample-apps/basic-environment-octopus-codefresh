using System.Text.Json;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-OCT-010 (offline half): force-wake works on demand. Step Start cluster of env-wake starts a stopped cluster once,
/// waits until it runs (Wake.TimeoutMinutes), disables the alert suppression rule and outputs Wake.ClusterStarted; step
/// Wait for workers and gateway checks the worker pools of the tier without waiting for a task. The inline PowerShell runs
/// under the stub Octopus runtime.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class EnvWakeScriptTests
{
    private const string Runbook = ".octopus/platform-infrastructure/runbooks/env-wake.ocl";
    private const string AksState = @"^az aks show --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --query \[powerState\.code, provisioningState\] --output tsv$";
    private const string RuleEnabled = "^az monitor alert-processing-rule show --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --query properties.enabled ";
    private const string RuleShow = "^az monitor alert-processing-rule show --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --output none$";
    private const string RuleOff = "^az monitor alert-processing-rule update --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --enabled false --output none --only-show-errors$";
    private const string AksStart = "^az aks start --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --no-wait$";

    /// <summary>A running cluster is not started; the rule is disabled and Wake.ClusterStarted is False.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_StartCluster_Running_DisablesTheRuleWithoutAStart()
    {
        var run = Start()
            .Reply(RuleEnabled, "false\n")
            .Reply(AksState, "Running\nSucceeded\n")
            .Reply(RuleShow)
            .Reply(RuleOff)
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("aks start").ShouldBeEmpty(run.Transcript);
        run.CallsMatching("--enabled false").Count.ShouldBe(1, run.Transcript);
        run.Outputs["Wake.ClusterStarted"].ShouldBe("False");
        run.Log.ShouldContain("aks-platform-nonprod is Running.", run.Transcript);
        run.Log.ShouldContain("Alert processing rule apr-sleep-nonprod disabled: alerts flow again.", run.Transcript);
    }

    /// <summary>A stopped cluster is started once, polled every 30 seconds until it runs, then the rule is disabled.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_StartCluster_Stopped_StartsOnceAndWaitsForRunning()
    {
        var run = Start()
            .Reply(RuleEnabled, "true\n")
            .Reply(AksState, "Stopped\nSucceeded\n", times: 3)
            .Reply(AksState, "Running\nStarting\n", times: 1)
            .Reply(AksState, "Running\nSucceeded\n")
            .Reply(AksStart)
            .Reply(RuleShow)
            .Reply(RuleOff)
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("aks start").Count.ShouldBe(1, run.Transcript);
        run.CallsMatching("^sleep 30$").Count.ShouldBe(3, run.Transcript);
        run.IndexOf("--enabled false").ShouldBeGreaterThan(run.IndexOf("aks start"), run.Transcript);
        run.Outputs["Wake.ClusterStarted"].ShouldBe("True");
        run.Log.ShouldContain("aks-platform-nonprod is stopped; starting it (up to 20 minutes).", run.Transcript);
        run.Log.ShouldContain("Waiting for aks-platform-nonprod to leave Stopped.", run.Transcript);
        run.Log.ShouldContain("aks-platform-nonprod is Running/Starting; waiting.", run.Transcript);
    }

    /// <summary>A cluster that does not run in time, or does not exist, fails the wake with a precise message.</summary>
    [TestCase("timeout", "aks-platform-nonprod did not reach Running within 0 minutes (last state Stopped/Succeeded).")]
    [TestCase("missing", "Cluster aks-platform-nonprod was not found in rg-platform-nonprod-aks; run env-apply first.")]
    [TestCase("timeout-invalid", "Wake.TimeoutMinutes must be a whole number of minutes; got '20m'.")]
    [Capability("CAP-OCT-010")]
    public void Should_StartCluster_NoRunningCluster_FailsTheWake(string scenario, string failure)
    {
        var script = Start().Reply(RuleEnabled, "false\n").Reply(AksStart);
        switch (scenario)
        {
            case "timeout":
                script.With("Wake.TimeoutMinutes", "0").Reply(AksState, "Stopped\nSucceeded\n");
                break;
            case "missing":
                script.Reply(AksState, exitCode: 3, error: "ERROR: (ResourceNotFound) not found\n");
                break;
            default:
                script.With("Wake.TimeoutMinutes", "20m");
                break;
        }

        var run = script.Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.CallsMatching("--enabled false").ShouldBeEmpty(run.Transcript);
    }

    /// <summary>Healthy pools are reported; a pool with an unhealthy worker gets one health check, requested and not awaited.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_WaitForWorkers_UnhealthyWorker_RequestsOneHealthCheck()
    {
        var run = Workers()
            .Listing("workerpools", "k8s-tdd", "WorkerPools-1")
            .Listing("workerpools", "k8s-uat", "WorkerPools-2")
            .Api("GET", @"/api/Spaces-1/workerpools/WorkerPools-1/workers\?take=100", new { Items = new object[] { Worker("w-tdd-0", "Healthy"), Worker("w-tdd-1", "Unavailable", disabled: true) } })
            .Api("GET", @"/api/Spaces-1/workerpools/WorkerPools-2/workers\?take=100", new { Items = new object[] { Worker("w-uat-0", "Unavailable") } })
            .Api("POST", "/api/Spaces-1/tasks", new { Id = "ServerTasks-77" })
            .Api("GET", @"/api/spaces/Spaces-1/argocdinstances/summaries\?name=argocd-nonprod", new { Resources = new[] { new { Name = "argocd-nonprod", GatewayId = "ArgoCDGateways-5", HealthStatus = "Healthy" } } })
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Log.ShouldContain("k8s-tdd: w-tdd-0=Healthy (cluster started now: True).", run.Transcript);
        run.Warnings.ShouldBe(["k8s-uat: w-uat-0=Unavailable. Health check ServerTasks-77 requested, not awaited (task cap); a step on k8s-uat that starts before it finishes can fail with no healthy worker: rerun it."], run.Transcript);
        using (var request = JsonDocument.Parse(run.CallsMatching("--request POST").ShouldHaveSingleItem(run.Transcript).Option("--data")!))
        {
            request.RootElement.GetProperty("Name").GetString().ShouldBe("Health");
            request.RootElement.GetProperty("Arguments").GetProperty("WorkerpoolId").GetString().ShouldBe("WorkerPools-2");
        }

        run.Log.ShouldContain("argocd-nonprod: Healthy.", run.Transcript);
        run.Outputs["Wake.CompletedAt"].ShouldMatch(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$");
        run.Highlights.ShouldBe([$"aks-platform-nonprod is awake ({run.Outputs["Wake.CompletedAt"]})."], run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    /// <summary>An Unavailable gateway gets one Octopus health check, and the wait goes on until it reads Healthy.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_WaitForGateway_Unavailable_RequestsOneGatewayHealthCheck()
    {
        var run = Workers()
            .Listing("workerpools", "k8s-tdd", "WorkerPools-1")
            .Listing("workerpools", "k8s-uat", "WorkerPools-2")
            .Api("GET", @"/api/Spaces-1/workerpools/WorkerPools-1/workers\?take=100", new { Items = new object[] { Worker("w-tdd-0", "Healthy") } })
            .Api("GET", @"/api/Spaces-1/workerpools/WorkerPools-2/workers\?take=100", new { Items = new object[] { Worker("w-uat-0", "Healthy") } })
            .Api("GET", @"/api/spaces/Spaces-1/argocdinstances/summaries\?name=argocd-nonprod", new { Resources = new[] { new { Name = "argocd-nonprod", GatewayId = "ArgoCDGateways-5", HealthStatus = "Unavailable" } } }, times: 1)
            .Api("POST", "/api/Spaces-1/tasks", new { Id = "ServerTasks-78" })
            .Api("GET", @"/api/spaces/Spaces-1/argocdinstances/summaries\?name=argocd-nonprod", new { Resources = new[] { new { Name = "argocd-nonprod", GatewayId = "ArgoCDGateways-5", HealthStatus = "Healthy" } } })
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        using (var request = JsonDocument.Parse(run.CallsMatching("--request POST").ShouldHaveSingleItem(run.Transcript).Option("--data")!))
        {
            request.RootElement.GetProperty("Name").GetString().ShouldBe("ArgoCDGatewayHealthCheck");
            request.RootElement.GetProperty("Arguments").GetProperty("ArgoCDGatewayId").GetString().ShouldBe("ArgoCDGateways-5");
        }

        run.Log.ShouldContain("argocd-nonprod: Healthy.", run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    private static RunbookScript Start() => RunbookScript.Of(Runbook, "start-cluster").InTier().With("Wake.TimeoutMinutes", "20");

    private static RunbookScript Workers() => RunbookScript.Of(Runbook, "wait-for-workers-and-gateway").InTier()
        .With("Octopus.Action[Start cluster].Output.Wake.ClusterStarted", "True");

    private static object Worker(string name, string status, bool disabled = false) => new { Name = name, HealthStatus = status, IsDisabled = disabled };
}
