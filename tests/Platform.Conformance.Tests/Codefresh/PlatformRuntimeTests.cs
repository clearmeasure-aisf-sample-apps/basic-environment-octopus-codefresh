using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-001: a build on the platform runtime succeeds. Observed on the build record: status and runtime. The build is
/// the sandbox/ci build of the green branch that platform-env/conformance-arm pushed; run by hand without it, the test
/// reads the newest successful platform-env/env-checks build instead. The runtime is found anywhere in the build record
/// [VERIFY the field name].
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
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_GetBuildAsync_BuildOnPlatformRuntime_Succeeds()
    {
        var codefresh = RequireCodefresh("the platform runtime test");
        var runtimes = await Codefresh.GetRuntimeEnvironmentsAsync(Token);
        var build = CodefreshPlatform.Variable(CodefreshPlatform.GreenShaVariable) is { } green
            ? await FinishedBuildForAsync(CodefreshPlatform.SandboxCi, green)
            : (await codefresh.ListBuildsAsync(EnvChecks, 20, Token)).FirstOrDefault(candidate => candidate.Status == "success");

        build.ShouldNotBeNull($"no successful {EnvChecks} build to read; run platform-env/conformance-arm first");
        var record = await codefresh.GetBuildAsync(build.Id, Token) ?? build;

        runtimes.Select(runtime => runtime.Name).ShouldContain(CodefreshPlatform.Runtime);
        record.Status.ShouldBe("success", $"build {record}");
        JsonRead.ContainsString(record.Record, CodefreshPlatform.Runtime).ShouldBeTrue($"build {record.Id} does not name runtime {CodefreshPlatform.Runtime}");
    }
}
