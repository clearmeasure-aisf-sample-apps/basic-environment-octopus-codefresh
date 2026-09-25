using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-001: a build on the platform runtime succeeds. Observed on the build record (status) and on the pipeline the
/// build ran (<c>spec.runtimeEnvironment.name</c>): the build API names only the scheduler type of a build
/// (<c>runtime.schedulerConfig.type</c>, verified 2026-09-25), so a record that names the runtime anywhere passes, and
/// otherwise the build's pipeline must run on it. The build is the sandbox/ci build of the green branch that
/// platform-env/conformance-arm pushed; run by hand without it, the test reads the newest successful
/// platform-env/env-checks build instead.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class PlatformRuntimeTests : CodefreshCapabilityTestBase
{
    private const string EnvChecks = "platform-env/env-checks";

    /// <summary>The build ran on aks-platform-build/codefresh and succeeded.</summary>
    [Test]
    [Capability("CAP-CF-001")]
    [Category(Categories.Build)]
    [CancelAfter(75 * 60 * 1000)]
    public async Task Should_GetBuildAsync_BuildOnPlatformRuntime_Succeeds()
    {
        var codefresh = RequireCodefresh("the platform runtime test");
        var runtimes = await Codefresh.GetRuntimeEnvironmentsAsync(Token);
        var build = CodefreshPlatform.Variable(CodefreshPlatform.GreenShaVariable) is { } green
            ? await FinishedBuildForAsync(CodefreshPlatform.SandboxCi, green)
            : (await codefresh.ListBuildsAsync(EnvChecks, 20, Token)).FirstOrDefault(candidate => candidate.Status == "success");

        build.ShouldNotBeNull($"no successful {EnvChecks} build to read; run platform-env/conformance-arm first");
        var record = await codefresh.GetBuildAsync(build.Id, Token) ?? build;

        var runtime = JsonRead.ContainsString(record.Record, CodefreshPlatform.Runtime)
            ? CodefreshPlatform.Runtime
            : await PipelineRuntimeAsync(codefresh, record);

        runtimes.Select(candidate => candidate.Name).ShouldContain(CodefreshPlatform.Runtime);
        record.Status.ShouldBe("success", $"build {record}");
        runtime.ShouldBe(CodefreshPlatform.Runtime, $"build {record.Id} ran pipeline {JsonRead.Text(record.Record, "pipelineName")} on runtime '{runtime}'");
    }

    private async Task<string?> PipelineRuntimeAsync(CodefreshRest codefresh, CodefreshBuildRecord record)
    {
        var pipelineId = JsonRead.Text(record.Record, "serviceId");
        pipelineId.ShouldNotBeNullOrWhiteSpace($"build {record.Id} names no pipeline (serviceId)");
        var pipeline = await codefresh.GetPipelineAsync(pipelineId, Token);
        pipeline.ShouldNotBeNull($"pipeline {pipelineId} of build {record.Id} does not exist");
        return JsonRead.Text(JsonRead.Path(pipeline.GetValueOrDefault(), "spec", "runtimeEnvironment"), "name");
    }
}
