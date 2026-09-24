using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the Codefresh REST shapes: run, wait, terminate, list builds, runtimes, agents and the current user.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class CodefreshApiTests
{
    private const string ApiKey = "<stub-codefresh-api-key>";

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenRunPipelineAsync_NameWithSlash_PostsTheEscapedNameWithBranchAndVariables()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("\"65f0c0ffee\""));
        using var codefresh = Create(handler);

        var buildId = await codefresh.RunPipelineAsync("workorders/release", new CodefreshRunRequest
        {
            Branch = "master",
            Variables = new Dictionary<string, string> { ["CONFORMANCE_RUN"] = "42" },
        });

        buildId.ShouldBe("65f0c0ffee");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Uri.ToString().ShouldBe("https://g.codefresh.example.test/api/pipelines/run/workorders%2Frelease");
        request.Header("Authorization").ShouldBe(ApiKey);
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("branch").GetString().ShouldBe("master");
        body.RootElement.GetProperty("variables").GetProperty("CONFORMANCE_RUN").GetString().ShouldBe("42");
        body.RootElement.TryGetProperty("trigger", out _).ShouldBeFalse();
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenWaitForBuildAsync_BuildFinishesOnThirdPoll_ReturnsTheTerminalBuild()
    {
        var statuses = new Queue<string>(["pending", "running", "success"]);
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json($$"""{ "id": "b1", "status": "{{statuses.Dequeue()}}", "progress": "p9", "pipelineName": "workorders/ci" }"""));
        var clock = new StubClock(DateTimeOffset.UnixEpoch);
        using var codefresh = Create(handler, clock);

        var build = await codefresh.WaitForBuildAsync("b1", TimeSpan.FromMinutes(10));

        build.Status.ShouldBe(CodefreshBuildStatuses.Success);
        build.IsTerminal.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(3);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/builds/b1");
    }

    [TestCase("success", true)]
    [TestCase("error", true)]
    [TestCase("terminated", true)]
    [TestCase("denied", true)]
    [TestCase("running", false)]
    [TestCase("pending-approval", false)]
    [TestCase("terminating", false)]
    [Capability("CAP-HARNESS-009")]
    public void WhenIsTerminal_Status_IsTrueOnlyForFinalStatuses(string status, bool expected)
    {
        var build = new CodefreshBuild { Id = "b1", Status = status };

        var terminal = build.IsTerminal;

        terminal.ShouldBe(expected);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenTerminateBuildAsync_RunningBuild_DeletesItsProgress()
    {
        var handler = new StubHttpMessageHandler(request => request.Method == "GET"
            ? StubHttpMessageHandler.Json("""{ "id": "b1", "status": "running", "progress": "p9" }""")
            : StubHttpMessageHandler.Json("{}"));
        using var codefresh = Create(handler);

        await codefresh.TerminateBuildAsync("b1");

        handler.Requests.Select(request => $"{request.Method} {request.PathAndQuery}").ShouldBe(["GET /api/builds/b1", "DELETE /api/progress/p9"]);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenListBuildsAsync_PipelineName_ResolvesItsIdAndReadsTheWorkflows()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery == "/api/pipelines/workorders%2Fci"
            ? StubHttpMessageHandler.Json("""{ "metadata": { "id": "pipe-1", "name": "workorders/ci" } }""")
            : StubHttpMessageHandler.Json("""{ "workflows": { "docs": [ { "id": "b2", "status": "success", "pipelineName": "workorders/ci", "branchName": "feature/x", "revision": "abc123", "created": "2026-09-24T05:00:00Z", "progress": "p2" } ] } }"""));
        using var codefresh = Create(handler);

        var builds = await codefresh.ListBuildsAsync("workorders/ci", limit: 5);

        var build = builds.ShouldHaveSingleItem();
        build.Id.ShouldBe("b2");
        build.Branch.ShouldBe("feature/x");
        build.Created.ShouldBe(new DateTimeOffset(2026, 9, 24, 5, 0, 0, TimeSpan.Zero));
        handler.Requests.Last().PathAndQuery.ShouldBe("/api/workflow?pipeline=pipe-1&limit=5&page=1");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenReadingUserRuntimesAndAgents_Responses_AreMappedToTheirModels()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/api/user" => StubHttpMessageHandler.Json("""{ "_id": "u1", "userName": "platform-bot", "activeAccountName": "clearmeasure", "account": [ { "name": "clearmeasure" }, { "name": "sandbox" } ] }"""),
            "/api/runtime-environments" => StubHttpMessageHandler.Json("""[ { "metadata": { "name": "cf-runtime-ci", "agent": true } } ]"""),
            _ => StubHttpMessageHandler.Json("""[ { "id": "a1", "name": "runner", "runtimes": [ "cf-runtime-ci", "cf-runtime-release" ], "status": { "healthStatus": "healthy", "reportedAt": "2026-09-24T05:59:00Z" } } ]"""),
        });
        using var codefresh = Create(handler);

        var user = await codefresh.GetCurrentUserAsync();
        var runtimes = await codefresh.GetRuntimeEnvironmentsAsync();
        var agents = await codefresh.GetAgentsAsync();

        user.ShouldBe(new CodefreshUser("u1", "platform-bot", "clearmeasure", ["clearmeasure", "sandbox"]), new CodefreshUserComparer());
        runtimes.ShouldBe([new CodefreshRuntimeEnvironment("cf-runtime-ci", true)]);
        var agent = agents.ShouldHaveSingleItem();
        agent.Runtimes.ShouldBe(["cf-runtime-ci", "cf-runtime-release"]);
        agent.HealthStatus.ShouldBe("healthy");
    }

    private static CodefreshApi Create(StubHttpMessageHandler handler, StubClock? clock = null) =>
        CodefreshApi.Create("https://g.codefresh.example.test/api", ApiKey, TimeSpan.FromSeconds(30), clock, TimeSpan.FromSeconds(5), handler);

    private sealed class CodefreshUserComparer : IEqualityComparer<CodefreshUser>
    {
        public bool Equals(CodefreshUser? x, CodefreshUser? y) =>
            x is not null && y is not null && x.Id == y.Id && x.UserName == y.UserName && x.ActiveAccountName == y.ActiveAccountName && x.AccountNames.SequenceEqual(y.AccountNames);

        public int GetHashCode(CodefreshUser obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
