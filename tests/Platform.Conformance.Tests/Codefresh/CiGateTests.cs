using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-004: CI fails the required check on a failing test and passes a green branch. Observed as the
/// <c>codefresh/ci</c> commit status on the two sandbox branches that platform-env/conformance-arm pushed:
/// <c>conformance/&lt;run&gt;/failing-test</c> (adds <c>toggles/failing-test</c>) and <c>conformance/&lt;run&gt;/green</c>.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class CiGateTests : CodefreshCapabilityTestBase
{
    private static readonly TimeSpan StatusWait = TimeSpan.FromMinutes(3);

    /// <summary>The failing-test branch gets a failed codefresh/ci.</summary>
    [Test]
    [Capability("CAP-CF-004")]
    [Category(Categories.Build)]
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_GetStatusesAsync_FailingTestBranch_FailsTheRequiredCheck()
    {
        var sha = RequireArmVariable(CodefreshPlatform.FailingShaVariable, "the failing CI test");

        var (build, state) = await CiResultAsync(sha);

        build.Status.ShouldBe("error", $"sandbox/ci build {build}");
        state.ShouldBeOneOf(["failure", "error"], $"{CodefreshPlatform.CiStatus} of {sha}");
    }

    /// <summary>The green branch gets a successful codefresh/ci.</summary>
    [Test]
    [Capability("CAP-CF-004")]
    [Category(Categories.Build)]
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_GetStatusesAsync_GreenBranch_PassesTheRequiredCheck()
    {
        var sha = RequireArmVariable(CodefreshPlatform.GreenShaVariable, "the green CI test");

        var (build, state) = await CiResultAsync(sha);

        build.Status.ShouldBe("success", $"sandbox/ci build {build}");
        state.ShouldBe("success", $"{CodefreshPlatform.CiStatus} of {sha}");
    }

    private async Task<(CodefreshBuildRecord Build, string State)> CiResultAsync(string sha)
    {
        var repository = RequireSandboxRepository("the CI gate test");
        var gitHub = RequireGitHub("the CI gate test");
        var build = await FinishedBuildForAsync(CodefreshPlatform.SandboxCi, sha);
        var status = await Poll.UntilAsync(
            async token => (await gitHub.GetStatusesAsync(repository, sha, token)).FirstOrDefault(candidate => candidate.Context == CodefreshPlatform.CiStatus),
            candidate => candidate is not null && candidate.State != "pending",
            StatusWait,
            Settings.TimeLimits.PollInterval,
            $"a final {CodefreshPlatform.CiStatus} status on {repository}@{sha}",
            cancellationToken: Token);
        return (build, status!.State);
    }
}
