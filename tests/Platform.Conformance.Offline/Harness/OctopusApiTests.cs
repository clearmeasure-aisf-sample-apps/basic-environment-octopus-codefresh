using System.Net;
using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the Octopus REST shapes: config-as-code runbook runs, tasks, releases, deployments and interruptions.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class OctopusApiTests
{
    private const string ApiKey = "<stub-octopus-api-key>";
    private const string ProjectJson = """{ "Id": "Projects-42", "Name": "platform-infrastructure", "Slug": "platform-infrastructure", "IsVersionControlled": true, "VariableSetId": "variableset-Projects-42" }""";
    private const string EnvironmentsJson = """{ "Items": [ { "Id": "Environments-7", "Name": "infra-nonprod", "Slug": "infra-nonprod" } ] }""";

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGetSpaceAsync_AnyCall_SendsTheApiKeyOnlyAsHeader()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "Id": "Spaces-1", "Name": "Work Orders", "Slug": "work-orders" }"""));
        using var octopus = Create(handler);

        var space = await octopus.GetSpaceAsync();

        space.Name.ShouldBe("Work Orders");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.ToString().ShouldBe("https://octopus.example.test/api/spaces/Spaces-1");
        request.Header(OctopusApi.ApiKeyHeader).ShouldBe(ApiKey);
        request.Uri.ToString().ShouldNotContain(ApiKey);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenStartRunbookAsync_GitRunbookWithPromptedVariable_PostsRunV1WithTheFormValueOfThatVariable()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/api/Spaces-1/projects/platform-infrastructure" => StubHttpMessageHandler.Json(ProjectJson),
            "/api/Spaces-1/environments?name=infra-nonprod&take=100" => StubHttpMessageHandler.Json(EnvironmentsJson),
            "/api/Spaces-1/projects/Projects-42/refs%2Fheads%2Fmain/runbooks?take=1000" => StubHttpMessageHandler.Json("""{ "Items": [ { "Id": "Runbooks-8", "Name": "env-wake", "Slug": "env-wake" }, { "Id": "Runbooks-9", "Name": "env-sleep", "Slug": "env-sleep" } ] }"""),
            "/api/Spaces-1/projects/Projects-42/refs%2Fheads%2Fmain/runbooks/Runbooks-9/runbookRuns/preview/Environments-7" => StubHttpMessageHandler.Json("""{ "Form": { "Values": {}, "Elements": [ { "Name": "b1f0c9e2", "Control": { "Type": "VariableValue", "Name": "Sleep.Force", "Label": "Force" }, "IsValueRequired": false } ] } }"""),
            "/api/Spaces-1/projects/Projects-42/refs%2Fheads%2Fmain/runbooks/Runbooks-9/run/v1" => StubHttpMessageHandler.Json("""{ "Resources": [ { "Id": "RunbookRuns-5", "TaskId": "ServerTasks-77", "RunbookId": "Runbooks-9", "EnvironmentId": "Environments-7", "RunbookSnapshotId": "" } ] }"""),
            _ => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound),
        });
        using var octopus = Create(handler);
        var runRequest = new OctopusRunbookRunRequest
        {
            Project = "platform-infrastructure",
            Runbook = "env-sleep",
            Environment = "infra-nonprod",
            PromptedVariables = new Dictionary<string, string> { ["Sleep.Force"] = "True" },
            Comments = "conformance run 42",
        };

        var run = await octopus.StartRunbookAsync(runRequest);

        run.ShouldBe(new OctopusRunbookRun("RunbookRuns-5", "ServerTasks-77", "Projects-42", "Runbooks-9", "Environments-7"));
        var post = handler.Requests.Last();
        post.Method.ShouldBe("POST");
        using var body = JsonDocument.Parse(post.Body!);
        body.RootElement.GetProperty("SpaceId").GetString().ShouldBe("Spaces-1");
        body.RootElement.GetProperty("ProjectId").GetString().ShouldBe("Projects-42");
        body.RootElement.GetProperty("RunbookId").GetString().ShouldBe("Runbooks-9");
        body.RootElement.GetProperty("GitRef").GetString().ShouldBe("refs/heads/main");
        var runs = body.RootElement.GetProperty("Runs");
        runs.GetArrayLength().ShouldBe(1);
        runs[0].GetProperty("EnvironmentId").GetString().ShouldBe("Environments-7");
        runs[0].GetProperty("FormValues").GetProperty("b1f0c9e2").GetString().ShouldBe("True");
        runs[0].GetProperty("Comments").GetString().ShouldBe("conformance run 42");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenStartRunbookAsync_UnknownPromptedVariable_ThrowsListingThePromptedVariables()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/api/Spaces-1/projects/platform-infrastructure" => StubHttpMessageHandler.Json(ProjectJson),
            "/api/Spaces-1/environments?name=infra-nonprod&take=100" => StubHttpMessageHandler.Json(EnvironmentsJson),
            "/api/Spaces-1/projects/Projects-42/refs%2Fheads%2Fmain/runbooks?take=1000" => StubHttpMessageHandler.Json("""{ "Items": [ { "Id": "Runbooks-9", "Name": "env-sleep" } ] }"""),
            _ => StubHttpMessageHandler.Json("""{ "Form": { "Elements": [ { "Name": "b1f0c9e2", "Control": { "Type": "VariableValue", "Name": "Sleep.Force" } } ] } }"""),
        });
        using var octopus = Create(handler);
        var runRequest = new OctopusRunbookRunRequest
        {
            Project = "platform-infrastructure",
            Runbook = "env-sleep",
            Environment = "infra-nonprod",
            PromptedVariables = new Dictionary<string, string> { ["Sleep.Now"] = "True" },
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => octopus.StartRunbookAsync(runRequest));

        exception.Message.ShouldBe("Runbook env-sleep has no prompted variable 'Sleep.Now' in infra-nonprod; prompted variables: Sleep.Force.");
        handler.Requests.ShouldNotContain(recorded => recorded.Method == "POST");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenStartRunbookAsync_DatabaseRunbook_PostsCreateRunbookRunV1WithVariablesByName()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/api/Spaces-1/projects/workorders" => StubHttpMessageHandler.Json("""{ "Id": "Projects-3", "Name": "workorders" }"""),
            "/api/Spaces-1/environments?name=uat&take=100" => StubHttpMessageHandler.Json("""{ "Items": [ { "Id": "Environments-2", "Name": "uat" } ] }"""),
            _ => StubHttpMessageHandler.Json("""{ "RunbookRunServerTasks": [ { "RunbookRunId": "RunbookRuns-6", "ServerTaskId": "ServerTasks-78" } ] }"""),
        });
        using var octopus = Create(handler);
        var runRequest = new OctopusRunbookRunRequest
        {
            Project = "workorders",
            Runbook = "db-backup",
            Environment = "uat",
            GitRef = null,
            PromptedVariables = new Dictionary<string, string> { ["Backup.Label"] = "conformance" },
        };

        var run = await octopus.StartRunbookAsync(runRequest);

        run.TaskId.ShouldBe("ServerTasks-78");
        var post = handler.Requests.Last();
        post.PathAndQuery.ShouldBe("/api/Spaces-1/runbook-runs/create/v1");
        using var body = JsonDocument.Parse(post.Body!);
        body.RootElement.GetProperty("SpaceIdOrName").GetString().ShouldBe("Spaces-1");
        body.RootElement.GetProperty("ProjectName").GetString().ShouldBe("workorders");
        body.RootElement.GetProperty("RunbookName").GetString().ShouldBe("db-backup");
        body.RootElement.GetProperty("EnvironmentNames")[0].GetString().ShouldBe("uat");
        body.RootElement.GetProperty("Variables").GetProperty("Backup.Label").GetString().ShouldBe("conformance");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGetTasksAsync_ProjectSlugEnvironmentNameAndStates_ResolvesIdsAndFilters()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/api/Spaces-1/projects/platform-infrastructure" => StubHttpMessageHandler.Json(ProjectJson),
            "/api/Spaces-1/environments?name=infra-nonprod&take=100" => StubHttpMessageHandler.Json(EnvironmentsJson),
            _ => StubHttpMessageHandler.Json("""{ "Items": [ { "Id": "ServerTasks-9", "State": "Executing", "IsCompleted": false } ] }"""),
        });
        using var octopus = Create(handler);

        var tasks = await octopus.GetTasksAsync(new OctopusTaskQuery
        {
            Project = "platform-infrastructure",
            Environment = "infra-nonprod",
            States = [OctopusTaskStates.Queued, OctopusTaskStates.Executing],
            Take = 5,
        });

        tasks.ShouldHaveSingleItem().State.ShouldBe("Executing");
        handler.Requests.Last().PathAndQuery.ShouldBe("/api/Spaces-1/tasks?project=Projects-42&environment=Environments-7&states=Queued,Executing&take=5");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenWaitForTaskAsync_TaskCompletesOnThirdPoll_ReturnsTheCompletedTask()
    {
        var polls = 0;
        var handler = new StubHttpMessageHandler(_ => ++polls < 3
            ? StubHttpMessageHandler.Json("""{ "Id": "ServerTasks-77", "State": "Executing", "IsCompleted": false }""")
            : StubHttpMessageHandler.Json("""{ "Id": "ServerTasks-77", "State": "Success", "IsCompleted": true, "FinishedSuccessfully": true }"""));
        var clock = new StubClock(DateTimeOffset.UnixEpoch);
        using var octopus = Create(handler, clock);

        var task = await octopus.WaitForTaskAsync("ServerTasks-77", TimeSpan.FromMinutes(5));

        task.State.ShouldBe(OctopusTaskStates.Success);
        task.FinishedSuccessfully.ShouldBeTrue();
        clock.Delays.ShouldBe([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)]);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenWaitForTaskAsync_UntilPendingInterruption_ReturnsWhileTheTaskWaitsForApproval()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "Id": "ServerTasks-80", "State": "Executing", "IsCompleted": false, "HasPendingInterruptions": true }"""));
        using var octopus = Create(handler, new StubClock(DateTimeOffset.UnixEpoch));

        var task = await octopus.WaitForTaskAsync("ServerTasks-80", TimeSpan.FromMinutes(5), OctopusTaskWait.CompletedOrPendingInterruption);

        task.HasPendingInterruptions.ShouldBeTrue();
        task.IsCompleted.ShouldBeFalse();
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenApproveInterruptionAsync_PendingIntervention_TakesResponsibilityThenSubmitsProceedWithTheNote()
    {
        var handler = new StubHttpMessageHandler(request => request.Method == "PUT"
            ? StubHttpMessageHandler.Json("""{ "Id": "Users-1" }""")
            : StubHttpMessageHandler.Json("""{ "Id": "Interruptions-3", "IsPending": false, "TaskId": "ServerTasks-80" }"""));
        using var octopus = Create(handler);

        var interruption = await octopus.ApproveInterruptionAsync("Interruptions-3", "Approved by conformance run 42");

        interruption.IsPending.ShouldBeFalse();
        handler.Requests.Select(request => $"{request.Method} {request.PathAndQuery}").ShouldBe(
        [
            "PUT /api/Spaces-1/interruptions/Interruptions-3/responsible",
            "POST /api/Spaces-1/interruptions/Interruptions-3/submit",
        ]);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("Result").GetString().ShouldBe("Proceed");
        body.RootElement.GetProperty("Notes").GetString().ShouldBe("Approved by conformance run 42");
        body.RootElement.GetProperty("Instructions").ValueKind.ShouldBe(JsonValueKind.Null);
        body.RootElement.GetProperty("Id").GetString().ShouldBe("Interruptions-3");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenCreateReleaseAndDeployReleaseAsync_Requests_PostTheExecutionsApiCommands()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery.EndsWith("/releases/create/v1", StringComparison.Ordinal)
            ? StubHttpMessageHandler.Json("""{ "ReleaseId": "Releases-12", "ReleaseVersion": "2.5.120" }""")
            : StubHttpMessageHandler.Json("""{ "DeploymentServerTasks": [ { "DeploymentId": "Deployments-30", "ServerTaskId": "ServerTasks-90" } ] }"""));
        using var octopus = Create(handler);

        var release = await octopus.CreateReleaseAsync(new OctopusReleaseRequest { ProjectName = "workorders", ReleaseVersion = "2.5.120", ChannelName = "Default", GitRef = "refs/heads/main", Packages = ["workorders/ui-server:2.5.120"] });
        var deployments = await octopus.DeployReleaseAsync(new OctopusDeploymentRequest { ProjectName = "workorders", ReleaseVersion = release.ReleaseVersion, EnvironmentNames = ["tdd"] });

        release.ShouldBe(new OctopusRelease("Releases-12", "2.5.120"));
        deployments.ShouldBe([new OctopusDeploymentTask("Deployments-30", "ServerTasks-90")]);
        handler.Requests.Select(request => request.PathAndQuery).ShouldBe(["/api/Spaces-1/releases/create/v1", "/api/Spaces-1/deployments/create/untenanted/v1"]);
        using var releaseBody = JsonDocument.Parse(handler.Requests[0].Body!);
        releaseBody.RootElement.GetProperty("GitRef").GetString().ShouldBe("refs/heads/main");
        releaseBody.RootElement.GetProperty("Packages")[0].GetString().ShouldBe("workorders/ui-server:2.5.120");
        using var deployBody = JsonDocument.Parse(handler.Requests[1].Body!);
        deployBody.RootElement.GetProperty("EnvironmentNames")[0].GetString().ShouldBe("tdd");
        deployBody.RootElement.TryGetProperty("Variables", out _).ShouldBeFalse();
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGetTaskLogAndDeploymentAndVariables_Requests_ReadTheDocumentedRoutes()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/api/Spaces-1/tasks/ServerTasks-90/raw" => StubHttpMessageHandler.Text("Step 1: Wake environment"),
            "/api/Spaces-1/deployments/Deployments-30" => StubHttpMessageHandler.Json("""{ "Id": "Deployments-30", "ReleaseId": "Releases-12", "EnvironmentId": "Environments-1", "TaskId": "ServerTasks-90" }"""),
            "/api/Spaces-1/projects/platform-infrastructure" => StubHttpMessageHandler.Json(ProjectJson),
            "/api/Spaces-1/projects/Projects-42/refs%2Fheads%2Fmain/variables" => StubHttpMessageHandler.Json("""{ "Id": "vs-git", "Variables": [ { "Id": "v1", "Name": "Sleep.Enabled", "Value": "true", "Scope": { "Environment": [ "Environments-7" ] } } ] }"""),
            _ => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound),
        });
        using var octopus = Create(handler);

        var log = await octopus.GetTaskLogAsync("ServerTasks-90");
        var deployment = await octopus.GetDeploymentAsync("Deployments-30");
        var variables = await octopus.GetProjectVariablesAsync("platform-infrastructure", "refs/heads/main");

        log.ShouldBe("Step 1: Wake environment");
        deployment.TaskId.ShouldBe("ServerTasks-90");
        var variable = variables.Variables.ShouldHaveSingleItem();
        variable.Name.ShouldBe("Sleep.Enabled");
        variable.Scope["Environment"].ShouldBe(["Environments-7"]);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenCall_ServerRejectsTheKey_ThrowsApiExceptionWithStatusAndBodyButNotTheKey()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "ErrorMessage": "You must be logged in to perform this action." }""", HttpStatusCode.Unauthorized));
        using var octopus = Create(handler);

        var exception = await Should.ThrowAsync<PlatformApiException>(() => octopus.GetSpaceAsync());

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        exception.Message.ShouldStartWith("Octopus GET spaces/Spaces-1 failed (401 Unauthorized): ");
        exception.Message.ShouldContain("You must be logged in");
        exception.Message.ShouldNotContain(ApiKey);
    }

    private static OctopusApi Create(StubHttpMessageHandler handler, StubClock? clock = null) =>
        OctopusApi.Create("https://octopus.example.test/", "Spaces-1", ApiKey, TimeSpan.FromSeconds(30), clock, TimeSpan.FromSeconds(2), handler);
}
