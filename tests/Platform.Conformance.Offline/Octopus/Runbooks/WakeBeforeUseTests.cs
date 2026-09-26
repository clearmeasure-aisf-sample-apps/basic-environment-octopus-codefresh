using System.Text.Json;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-OCT-008 (offline half): whatever needs a cluster wakes it first. Step wake-environment of the platform-infrastructure
/// runbooks runs env-wake in its own infrastructure environment through the Octopus REST API (route runGitRunbookV1, from
/// refs/heads/main) and waits for it; while the cluster does not exist, the Terraform runbooks skip it and
/// rotate-db-passwords fails. The step-scoped key reaches curl only on standard input. The inline PowerShell runs under the
/// stub Octopus runtime.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
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
    [TestCase("rotate-db-passwords")]
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

    /// <summary>
    /// env-apply without a nonprod cluster skips the wake and removes the Argo CD gateway registration the destroyed cluster
    /// left behind, so the rebuilt cluster's gateway can register under the same name.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_EnvApplyWithoutNonProdCluster_RemovesTheStaleGateway()
    {
        var run = Wake("env-apply")
            .Reply(AksShow, exitCode: 3, error: "ERROR: (ResourceNotFound) The Resource 'Microsoft.ContainerService/managedClusters/aks-platform-nonprod' under resource group 'rg-platform-nonprod-aks' was not found.\n")
            .Api("GET", @"/api/spaces/Spaces-1/argocdinstances/summaries\?name=argocd-nonprod", new
            {
                Resources = new[]
                {
                    new { Name = "argocd-nonprod", GatewayId = "ArgoCDGateways-1" },
                    new { Name = "argocd-nonprod-other", GatewayId = "ArgoCDGateways-9" },
                },
            })
            .Api("DELETE", "/api/spaces/Spaces-1/argocdgateways/ArgoCDGateways-1", "")
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Highlights.ShouldBe(["Cluster aks-platform-nonprod does not exist yet: nothing to wake; 1 stale Argo CD gateway registration(s) of argocd-nonprod removed."], run.Transcript);
        run.CallsMatching("--request DELETE").ShouldHaveSingleItem(run.Transcript).Arguments[^1].ShouldBe(OctopusReplies.Url + "/api/spaces/Spaces-1/argocdgateways/ArgoCDGateways-1");
        run.CallsMatching("--request POST").ShouldBeEmpty(run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    /// <summary>A refused removal fails env-apply before the apply, naming the gateway.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_EnvApplyGatewayRemovalRefused_FailsTheStep()
    {
        var run = Wake("env-apply")
            .Reply(AksShow, exitCode: 3, error: "ERROR: (ResourceNotFound) The Resource 'Microsoft.ContainerService/managedClusters/aks-platform-nonprod' under resource group 'rg-platform-nonprod-aks' was not found.\n")
            .Api("GET", @"/api/spaces/Spaces-1/argocdinstances/summaries\?name=argocd-nonprod", new { Resources = new[] { new { Name = "argocd-nonprod", GatewayId = "ArgoCDGateways-1" } } })
            .Api("DELETE", "/api/spaces/Spaces-1/argocdgateways/ArgoCDGateways-1", "{ \"ErrorMessage\": \"denied\" }", exitCode: 22)
            .Run();

        run.Failure.ShouldBe("Octopus refused to remove the stale Argo CD gateway ArgoCDGateways-1 (argocd-nonprod): { \"ErrorMessage\": \"denied\" }", run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    /// <summary>env-apply without a prod cluster only skips the wake: prod has no destroy runbook to leave a gateway behind.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_EnvApplyWithoutProdCluster_SkipsTheWake()
    {
        var run = Wake("env-apply", "prod")
            .Reply("^az aks show --resource-group rg-platform-prod-aks --name aks-platform-prod --output none$", exitCode: 3, error: "ERROR: (ResourceNotFound) The Resource 'Microsoft.ContainerService/managedClusters/aks-platform-prod' was not found.\n")
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Highlights.ShouldBe(["Cluster aks-platform-prod does not exist yet: nothing to wake."], run.Transcript);
        run.Calls.Where(call => call.Tool == "curl").ShouldBeEmpty(run.Transcript);
    }

    /// <summary>rotate-db-passwords needs the cluster: without it the wake fails and names the runbook that creates it.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    public void Should_WakeEnvironment_RotateWithoutCluster_FailsTheStep()
    {
        var run = Wake("rotate-db-passwords")
            .Reply(AksShow, exitCode: 3, error: "ERROR: (ResourceNotFound) The Resource 'Microsoft.ContainerService/managedClusters/aks-platform-nonprod' under resource group 'rg-platform-nonprod-aks' was not found.\n")
            .Run();

        run.Failure.ShouldBe("Cluster aks-platform-nonprod does not exist: run env-apply in infra-nonprod first.", run.Transcript);
        run.Calls.Where(call => call.Tool == "curl").ShouldBeEmpty(run.Transcript);
    }

    /// <summary>A failed env-wake, an unreadable cluster or a refused run fails the step with a precise message.</summary>
    /// <param name="runbook">Runbook with a wake-environment step.</param>
    /// <param name="scenario">What goes wrong.</param>
    /// <param name="failure">Expected message.</param>
    [TestCase("env-plan", "wake-failed", "env-wake in infra-nonprod ended Failed: The cluster did not start")]
    [TestCase("env-plan", "cluster-unreadable", "Cannot read cluster aks-platform-nonprod: ERROR: (AuthorizationFailed) no access")]
    [TestCase("env-plan", "run-refused", "Octopus refused to run env-wake in infra-nonprod: { \"ErrorMessage\": \"There was a problem with your request.\" }")]
    [TestCase("env-plan", "no-task", "Octopus returned no task for env-wake in infra-nonprod: {\"Resources\":[{\"TaskId\":5}]}")]
    [TestCase("env-plan", "key-empty", "Platform.OctopusApiKey is empty in this step: octopus/terraform scopes it to the REST-calling steps of platform-infrastructure (S5).")]
    [TestCase("rotate-db-passwords", "wake-failed", "env-wake in infra-nonprod ended Failed: The cluster did not start")]
    [TestCase("rotate-db-passwords", "cluster-unreadable", "Cannot read cluster aks-platform-nonprod: ERROR: (AuthorizationFailed) no access")]
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
                script.Api("POST", RunEnvWake, "{\n  \"ErrorMessage\": \"There was a problem with your request.\"\n}", exitCode: 22);
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

    /// <summary>
    /// Step run-env-wake of platform-wake (step 0 of every app deployment) wakes the tier of the deployment's environment
    /// with PlatformWake.OctopusApiKey and waits.
    /// </summary>
    /// <param name="environment">The app environment of the deployment.</param>
    /// <param name="target">The infrastructure environment whose env-wake runs.</param>
    [TestCase("tdd", "infra-nonprod")]
    [TestCase("uat", "infra-nonprod")]
    [TestCase("prod", "infra-prod")]
    [Capability("CAP-OCT-008")]
    public void Should_RunEnvWake_PlatformWake_WakesTheTierOfTheDeployment(string environment, string target)
    {
        var run = PlatformWake(environment)
            .Listing("projects", "platform-infrastructure", "Projects-1")
            .Listing("environments", target, OctopusReplies.EnvironmentIds[target])
            .Api("POST", RunEnvWake, new { Resources = new[] { new { TaskId = "ServerTasks-501" } } })
            .Api("GET", "/api/Spaces-1/tasks/ServerTasks-501", new { IsCompleted = true, FinishedSuccessfully = true })
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Highlights.ShouldBe([$"The cluster of {environment} is awake (env-wake in {target}, ServerTasks-501)."], run.Transcript);
        using (var payload = JsonDocument.Parse(run.CallsMatching("--request POST").ShouldHaveSingleItem(run.Transcript).Option("--data")!))
        {
            payload.RootElement.GetProperty("Runs")[0].GetProperty("EnvironmentId").GetString().ShouldBe(OctopusReplies.EnvironmentIds[target]);
        }

        run.ShouldKeepTheKeyOffCommandLines("wake-key-for-tests");
    }

    /// <summary>platform-wake sends its key only to an Octopus Cloud URL and a space ID, for an app environment it knows.</summary>
    /// <param name="variable">The variable that is wrong.</param>
    /// <param name="value">Its value.</param>
    /// <param name="failure">Expected message.</param>
    [TestCase("Octopus.Web.ServerUri", "https://octopus.example.com", "Octopus.Web.ServerUri 'https://octopus.example.com' is not an Octopus Cloud URL; the key is sent nowhere else.")]
    [TestCase("Octopus.Web.ServerUri", "https://example.octopus.app/extra", "Octopus.Web.ServerUri 'https://example.octopus.app/extra' is not an Octopus Cloud URL; the key is sent nowhere else.")]
    [TestCase("Octopus.Space.Id", "Spaces-x", "Octopus.Space.Id 'Spaces-x' is not a space ID.")]
    [TestCase("Octopus.Environment.Name", "infra-nonprod", "platform-wake has no cluster for environment 'infra-nonprod'.")]
    [TestCase("PlatformWake.OctopusApiKey", "", "PlatformWake.OctopusApiKey is empty: platform-wake includes library variable set Platform Automation (octopus/terraform).")]
    [Capability("CAP-OCT-008")]
    public void Should_RunEnvWake_UntrustedTarget_FailsBeforeAnyCall(string variable, string value, string failure)
    {
        var run = PlatformWake("uat").With(variable, value).Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Calls.ShouldBeEmpty(run.Transcript);
    }

    private static RunbookScript PlatformWake(string environment) =>
        RunbookScript.Of(".octopus/platform-wake/deployment_process.ocl", "run-env-wake")
            .With("Octopus.Web.ServerUri", OctopusReplies.Url + "/")
            .With("Octopus.Space.Id", "Spaces-1")
            .With("Octopus.Environment.Name", environment)
            .With("PlatformWake.OctopusApiKey", "wake-key-for-tests");

    private static RunbookScript Wake(string runbook, string tier = "nonprod") => RunbookScript.Of($"{Runbooks}/{runbook}.ocl", "wake-environment").InTier(tier);
}
