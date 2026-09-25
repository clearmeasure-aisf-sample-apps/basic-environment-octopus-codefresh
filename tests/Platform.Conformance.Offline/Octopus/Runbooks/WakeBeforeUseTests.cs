using System.Text.Json;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-OCT-008 (offline half): whatever needs a cluster wakes it first. Step wake-environment of the platform-infrastructure
/// runbooks runs env-wake in its own infrastructure environment through the Octopus REST API (route runGitRunbookV1, from
/// refs/heads/main) and waits for it; the Terraform runbooks skip it while the cluster does not exist. The step-scoped key
/// reaches curl only on standard input. The inline PowerShell runs under the stub Octopus runtime.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class WakeBeforeUseTests
{
    private const string Runbooks = ".octopus/platform-infrastructure/runbooks";
    private const string AksShow = "^az aks show --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --output none$";
    private const string RunEnvWake = "/api/spaces/Spaces-1/projects/Projects-1/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1";

    /// <summary>The keyed wake step runs env-wake in its own environment and waits until it succeeds.</summary>
    /// <param name="runbook">Runbook with a wake-environment step.</param>
    [TestCase("env-plan")]
    [TestCase("env-apply")]
    [TestCase("env-destroy")]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_ClusterExists_RunsEnvWakeAndWaits(string runbook)
    {
        var run = Wake(runbook)
            .Reply(AksShow)
            .Listing("projects", "platform-infrastructure", "Projects-1")
            .Listing("environments", "infra-nonprod", "Environments-3")
            .Api("POST", RunEnvWake, new { Resources = new[] { new { TaskId = "ServerTasks-500" } } })
            .Api("GET", "/api/Spaces-1/tasks/ServerTasks-500", new { IsCompleted = false }, times: 1)
            .Api("GET", "/api/Spaces-1/tasks/ServerTasks-500", exitCode: 22, times: 1)
            .Api("GET", "/api/Spaces-1/tasks/ServerTasks-500", new { IsCompleted = true, FinishedSuccessfully = true, State = "Success" })
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Highlights.ShouldBe(["The cluster of infra-nonprod is awake (env-wake in infra-nonprod, ServerTasks-500)."], run.Transcript);
        run.Log.ShouldContain($"env-wake is running in infra-nonprod: {OctopusReplies.Url}/app#/Spaces-1/tasks/ServerTasks-500", run.Transcript);
        var post = run.CallsMatching("--request POST").ShouldHaveSingleItem(run.Transcript);
        post.Arguments[^1].ShouldBe(OctopusReplies.Url + RunEnvWake);
        using (var payload = JsonDocument.Parse(post.Option("--data")!))
        {
            payload.RootElement.GetProperty("Runs")[0].GetProperty("EnvironmentId").GetString().ShouldBe("Environments-3");
            payload.RootElement.GetProperty("Runs")[0].GetProperty("TenantId").ValueKind.ShouldBe(JsonValueKind.Null);
            payload.RootElement.GetProperty("SelectedPackages").GetArrayLength().ShouldBe(0);
        }

        run.CallsMatching("^sleep 15$").Count.ShouldBe(2, run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    /// <summary>A Terraform runbook skips the wake while the cluster does not exist: the first env-apply creates it.</summary>
    /// <param name="runbook">Terraform runbook.</param>
    [TestCase("env-plan")]
    [TestCase("env-apply")]
    [TestCase("env-destroy")]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_TerraformRunbookWithoutCluster_SkipsTheWake(string runbook)
    {
        var run = Wake(runbook)
            .Reply(AksShow, exitCode: 3, error: "ERROR: (ResourceNotFound) The Resource 'Microsoft.ContainerService/managedClusters/aks-platform-nonprod' under resource group 'rg-platform-nonprod-aks' was not found.\n")
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Highlights.ShouldBe(["Cluster aks-platform-nonprod does not exist yet: nothing to wake."], run.Transcript);
        run.Calls.Where(call => call.Tool == "curl").ShouldBeEmpty(run.Transcript);
    }

    /// <summary>A failed env-wake, an unreadable cluster or a refused run fails the step with a precise message.</summary>
    /// <param name="runbook">Runbook with a wake-environment step.</param>
    /// <param name="scenario">What goes wrong.</param>
    /// <param name="failure">Expected message.</param>
    [TestCase("env-plan", "wake-failed", "env-wake in infra-nonprod ended Failed: The cluster did not start")]
    [TestCase("env-plan", "cluster-unreadable", "Cannot read cluster aks-platform-nonprod: ERROR: (AuthorizationFailed) no access")]
    [TestCase("env-plan", "run-refused", "Octopus refused to run env-wake in infra-nonprod.")]
    [TestCase("env-plan", "no-task", "Octopus returned no task for env-wake in infra-nonprod: {\"Resources\":[{\"TaskId\":5}]}")]
    [TestCase("env-plan", "key-empty", "Platform.OctopusApiKey is empty in this step: octopus/terraform scopes it to the REST-calling steps of platform-infrastructure (S5).")]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_WakeCannotComplete_FailsTheStep(string runbook, string scenario, string failure)
    {
        var script = Wake(runbook);
        if (scenario == "cluster-unreadable")
        {
            script.Reply(AksShow, exitCode: 1, error: "ERROR: (AuthorizationFailed) no access\n");
        }

        script.Reply(AksShow)
            .Listing("projects", "platform-infrastructure", "Projects-1")
            .Listing("environments", "infra-nonprod", "Environments-3");
        switch (scenario)
        {
            case "wake-failed":
                script.Api("POST", RunEnvWake, new { TaskId = "ServerTasks-500" })
                    .Api("GET", "/api/Spaces-1/tasks/ServerTasks-500", new { IsCompleted = true, FinishedSuccessfully = false, State = "Failed", ErrorMessage = "The cluster did not start" });
                break;
            case "run-refused":
                script.Api("POST", RunEnvWake, exitCode: 22);
                break;
            case "no-task":
                script.Api("POST", RunEnvWake, "{\"Resources\":[{\"TaskId\":5}]}");
                break;
            case "key-empty":
                script.With("Platform.OctopusApiKey", string.Empty);
                break;
        }

        var run = script.Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.ExitCode.ShouldNotBe(0, run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    private static RunbookScript Wake(string runbook) => RunbookScript.Of($"{Runbooks}/{runbook}.ocl", "wake-environment").InTier();
}
