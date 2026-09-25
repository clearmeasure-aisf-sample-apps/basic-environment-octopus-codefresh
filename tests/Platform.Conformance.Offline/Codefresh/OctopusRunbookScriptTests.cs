using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>Routes of a stub Octopus and a stub Azure for the platform script tests.</summary>
internal static class PlatformStubRoutes
{
    /// <summary>OCTOPUS_URL of the tests.</summary>
    public const string OctopusUrl = "https://octopus.example/";

    /// <summary>OCTOPUS_SPACE_ID of the tests.</summary>
    public const string Space = "Spaces-9";

    /// <summary>A key that only the stub sees.</summary>
    public const string OctopusKey = "octopus-key-for-tests";

    /// <summary>The API base of the stub Octopus.</summary>
    public const string Api = "https://octopus.example/api/Spaces-9";

    /// <summary>A finished, successful task.</summary>
    public const string Success = """{"IsCompleted": true, "State": "Success", "FinishedSuccessfully": true}""";

    /// <summary>A finished, failed task.</summary>
    public const string Failed = """{"IsCompleted": true, "State": "Failed", "FinishedSuccessfully": false}""";

    /// <summary>
    /// env-sleep of platform-infrastructure at refs/heads/main with the prompted variable Sleep.Force: the run of
    /// infra-nonprod (Environments-1) becomes task ServerTasks-1, that of infra-prod (Environments-2) ServerTasks-2.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="nonprodTask">Task JSON of infra-nonprod.</param>
    /// <param name="prodTask">Task JSON of infra-prod.</param>
    public static PlatformScriptHarness WithEnvSleep(this PlatformScriptHarness harness, string nonprodTask = Success, string prodTask = Success)
    {
        harness.With("OCTOPUS_URL", OctopusUrl).With("OCTOPUS_SPACE_ID", Space).With("OCTOPUS_API_KEY", OctopusKey);
        harness.Route("curl", [$"{Api}/projects/platform-infrastructure"], """{"Id": "Projects-1", "Slug": "platform-infrastructure"}""");
        harness.Route("curl", [$"{Api}/environments/all"], """[{"Id": "Environments-1", "Name": "infra-nonprod"}, {"Id": "Environments-2", "Name": "infra-prod"}]""");
        harness.Route("curl", [$"{Api}/projects/Projects-1/refs%2Fheads%2Fmain/runbooks?take=1000"], """{"Items": [{"Id": "Runbooks-3", "Slug": "env-wake", "Name": "env-wake"}, {"Id": "Runbooks-7", "Slug": "env-sleep", "Name": "env-sleep"}, {"Id": "Runbooks-11", "Slug": "sleep-hold", "Name": "sleep-hold"}]}""");
        harness.Route("curl", ["/runbooks/Runbooks-7/runbookRuns/preview/Environments-"], """{"Form": {"Elements": [{"Name": "a9b8", "Control": {"Name": "Sleep.DryRun"}}, {"Name": "d1e2f3", "Control": {"Name": "Sleep.Force"}}]}}""");
        harness.Route("curl", ["/runbooks/Runbooks-7/run/v1", "Environments-1"], """{"Resources": [{"TaskId": "ServerTasks-1"}]}""");
        harness.Route("curl", ["/runbooks/Runbooks-7/run/v1", "Environments-2"], """{"Resources": [{"TaskId": "ServerTasks-2"}]}""");
        harness.Route("curl", [$"{Api}/tasks/ServerTasks-1"], nonprodTask);
        harness.Route("curl", [$"{Api}/tasks/ServerTasks-2"], prodTask);
        return harness;
    }

    /// <summary>
    /// sleep-hold of platform-infrastructure (Runbooks-11, listed by <see cref="WithEnvSleep"/>) with the prompted variables
    /// Sleep.HoldMinutes and Sleep.HoldBy: the run of infra-nonprod becomes task ServerTasks-901, that of infra-prod
    /// ServerTasks-902.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="nonprodTask">Task JSON of infra-nonprod.</param>
    /// <param name="prodTask">Task JSON of infra-prod.</param>
    public static PlatformScriptHarness WithSleepHold(this PlatformScriptHarness harness, string nonprodTask = Success, string prodTask = Success)
    {
        harness.Route("curl", ["/runbooks/Runbooks-11/runbookRuns/preview/Environments-"], """{"Form": {"Elements": [{"Name": "h1", "Control": {"Name": "Sleep.HoldMinutes"}}, {"Name": "h2", "Control": {"Name": "Sleep.HoldBy"}}]}}""");
        harness.Route("curl", ["/runbooks/Runbooks-11/run/v1", "Environments-1"], """{"Resources": [{"TaskId": "ServerTasks-901"}]}""");
        harness.Route("curl", ["/runbooks/Runbooks-11/run/v1", "Environments-2"], """{"Resources": [{"TaskId": "ServerTasks-902"}]}""");
        harness.Route("curl", [$"{Api}/tasks/ServerTasks-901"], nonprodTask);
        harness.Route("curl", [$"{Api}/tasks/ServerTasks-902"], prodTask);
        return harness;
    }

    /// <summary>
    /// A settings file with both app tiers, the service principal of platform-conformance, an Entra ID token and both
    /// clusters in the given states.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="nonprod">Power state of the nonprod cluster.</param>
    /// <param name="prod">Power state of the prod cluster.</param>
    public static PlatformScriptHarness WithClusters(this PlatformScriptHarness harness, string nonprod = "Stopped", string prod = "Stopped")
    {
        var settings = Path.Combine(harness.Root, "platform.settings.json");
        File.WriteAllText(settings, """
            {"AzureSubscriptionId": "sub-1", "AzureTenantId": "tenant-1",
             "Tiers": {"nonprod": {"ResourceGroup": "rg-np", "ClusterName": "aks-np"}, "prod": {"ResourceGroup": "rg-p", "ClusterName": "aks-p"},
                       "build": {"ResourceGroup": "<rg>", "ClusterName": "<cluster>"}}}
            """);
        harness.With("PLATFORM_SETTINGS_FILE", settings).With("AZURE_CLIENT_ID", "client-1").With("AZURE_CLIENT_SECRET", AzureSecret);
        harness.Route("curl", ["https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token"], """{"access_token": "token-for-tests", "token_type": "Bearer"}""");
        harness.Route("curl", ["/resourceGroups/rg-np/providers/Microsoft.ContainerService/managedClusters/aks-np?api-version=2024-10-01"], Cluster(nonprod));
        harness.Route("curl", ["/resourceGroups/rg-p/providers/Microsoft.ContainerService/managedClusters/aks-p?api-version=2024-10-01"], Cluster(prod));
        return harness;
    }

    /// <summary>The client secret of the tests' service principal.</summary>
    public const string AzureSecret = "azure-secret-for-tests";

    /// <summary>The run requests of env-sleep the stub received, as JSON.</summary>
    /// <param name="harness">The harness.</param>
    public static JsonNode[] EnvSleepRuns(this PlatformScriptHarness harness) => RunRequests(harness, "Runbooks-7");

    /// <summary>The run requests of sleep-hold the stub received, as JSON, in order.</summary>
    /// <param name="harness">The harness.</param>
    public static JsonNode[] SleepHoldRuns(this PlatformScriptHarness harness) => RunRequests(harness, "Runbooks-11");

    private static JsonNode[] RunRequests(PlatformScriptHarness harness, string runbookId) =>
        harness.Calls("curl").Where(call => call.Method == "POST" && call.Url?.EndsWith($"/runbooks/{runbookId}/run/v1", StringComparison.Ordinal) == true)
            .Select(call => JsonNode.Parse(call.Body!)!)
            .ToArray();

    private static string Cluster(string power) => $$$"""{"properties": {"powerState": {"code": "{{{power}}}"}, "provisioningState": "Succeeded"}}""";
}

/// <summary>
/// CAP-HARNESS-009 for octopus-runbook.ps1, the runbook helper of the conformance pipelines: the documented Octopus
/// requests (those of the harness's OctopusApi), the API key only in a private header file, the prompted variable mapped
/// to its form element, the task awaited, and precise failures.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class OctopusRunbookScriptTests
{
    /// <summary>A forced env-sleep with a wait: six requests in order, the key never on a command line.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenRun_ForcedSleepWithWait_SendsTheDocumentedRequestsWithTheKeyOnlyInAPrivateHeaderFile()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep().With("CF_BUILD_ID", "6600000000000000000000aa");

        var result = harness.Run("octopus-runbook.ps1", "-Project", "platform-infrastructure", "-Runbook", "env-sleep", "-Environment", "infra-nonprod",
            "-Prompt", "sleep.force=true", "-WaitMinutes", "5");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe("ServerTasks-1");
        var calls = harness.Calls("curl");
        calls.Select(call => $"{call.Method} {call.Url}").ShouldBe(
        [
            $"GET {PlatformStubRoutes.Api}/projects/platform-infrastructure",
            $"GET {PlatformStubRoutes.Api}/environments/all",
            $"GET {PlatformStubRoutes.Api}/projects/Projects-1/refs%2Fheads%2Fmain/runbooks?take=1000",
            $"GET {PlatformStubRoutes.Api}/projects/Projects-1/refs%2Fheads%2Fmain/runbooks/Runbooks-7/runbookRuns/preview/Environments-1",
            $"POST {PlatformStubRoutes.Api}/projects/Projects-1/refs%2Fheads%2Fmain/runbooks/Runbooks-7/run/v1",
            $"GET {PlatformStubRoutes.Api}/tasks/ServerTasks-1",
        ]);
        calls.ShouldAllBe(call => call.Arguments.Take(3).SequenceEqual(new[] { "-fsS", "--max-time", "60" }));
        calls.ShouldAllBe(call => call.Headers.SequenceEqual(new[] { $"X-Octopus-ApiKey: {PlatformStubRoutes.OctopusKey}", "Content-Type: application/json" }));
        calls.SelectMany(call => call.HeaderFiles).ShouldAllBe(file => file.Private);
        calls.SelectMany(call => call.Arguments).ShouldNotContain(argument => argument.Contains(PlatformStubRoutes.OctopusKey, StringComparison.Ordinal));
        var run = harness.EnvSleepRuns().Single();
        run["SpaceId"]!.GetValue<string>().ShouldBe(PlatformStubRoutes.Space);
        run["ProjectId"]!.GetValue<string>().ShouldBe("Projects-1");
        run["RunbookId"]!.GetValue<string>().ShouldBe("Runbooks-7");
        run["GitRef"]!.GetValue<string>().ShouldBe("refs/heads/main");
        run["Notes"]!.GetValue<string>().ShouldBe("platform-env conformance 6600000000000000000000aa");
        run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>().ShouldBe("Environments-1");
        run["Runs"]![0]!["FormValues"]!.ToJsonString().ShouldBe("""{"d1e2f3":"true"}""");
        run["Runs"]![0]!["Comments"]!.GetValue<string>().ShouldBe("platform-env conformance 6600000000000000000000aa");
    }

    /// <summary>A failed task: exit 1, with the task and its link.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenRun_TaskFinishesUnsuccessfully_ExitsOneNamingTheTaskAndItsLink()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep(nonprodTask: PlatformStubRoutes.Failed);

        var result = harness.Run("octopus-runbook.ps1", "-Project", "platform-infrastructure", "-Runbook", "env-sleep", "-Environment", "infra-nonprod",
            "-Prompt", "Sleep.Force=true", "-Notes", "conformance:r1 teardown", "-WaitMinutes", "30");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Error.ShouldContain("ServerTasks-1 finished: Failed");
        result.Error.ShouldContain("platform-infrastructure/env-sleep in infra-nonprod did not finish successfully (task ServerTasks-1, state Failed): https://octopus.example/app#/Spaces-9/tasks/ServerTasks-1");
        harness.EnvSleepRuns().Single()["Notes"]!.GetValue<string>().ShouldBe("conformance:r1 teardown");
    }

    /// <summary>A prompt without a value is invalid input: exit 2 before any request.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenRun_PromptWithoutValue_ExitsTwoBeforeAnyRequest()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep();

        var result = harness.Run("octopus-runbook.ps1", "-Project", "platform-infrastructure", "-Runbook", "env-sleep", "-Environment", "infra-nonprod", "-Prompt", "Sleep.Force");

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Error.ShouldContain("-Prompt takes Name=value");
        harness.Calls().ShouldBeEmpty();
    }
}
