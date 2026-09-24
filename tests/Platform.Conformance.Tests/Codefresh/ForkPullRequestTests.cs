using System.Globalization;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-005, live half: a fork pull request never starts a pipeline. Observed on a pull request of the fixture
/// repository opened from a fork outside the org (R33; its number in <c>CONFORMANCE_FORK_PULL_REQUEST</c>): its head
/// commit has no <c>codefresh/*</c> status and no sandbox build. Inconclusive until R33 provides the fork. The offline
/// half checks that every trigger keeps fork events off.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ForkPullRequestTests : CodefreshCapabilityTestBase
{
    /// <summary>No status and no build for the head commit of the fork pull request.</summary>
    [Test]
    [Capability("CAP-CF-005")]
    [Category(Categories.Build)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_ListBuildsAsync_ForkPullRequest_StartsNoPipeline()
    {
        var number = int.Parse(
            CodefreshPlatform.Variable(CodefreshPlatform.ForkPullRequestVariable)
            ?? throw new PlatformPrerequisiteException($"Prerequisites missing for the fork pull request test: {CodefreshPlatform.ForkPullRequestVariable} is not set (R33: a fork of <sandbox-app-repo> outside the org with an open pull request)."),
            CultureInfo.InvariantCulture);
        var repository = RequireSandboxRepository("the fork pull request test");
        var gitHub = RequireGitHub("the fork pull request test");
        var codefresh = RequireCodefresh("the fork pull request test");
        var pullRequest = await gitHub.GetPullRequestAsync(repository, number, Token);
        pullRequest.ShouldNotBeNull($"{repository} has no pull request {number}");
        var head = JsonRead.Path(pullRequest.Value, "head");
        var headRepository = JsonRead.Text(JsonRead.Path(head, "repo"), "full_name") ?? string.Empty;
        var sha = JsonRead.Text(head, "sha") ?? string.Empty;

        var statuses = await gitHub.GetStatusesAsync(repository, sha, Token);
        var builds = (await codefresh.ListBuildsAsync(CodefreshPlatform.SandboxCi, 50, Token))
            .Concat(await codefresh.ListBuildsAsync(CodefreshPlatform.SandboxRelease, 50, Token))
            .Where(build => string.Equals(build.Revision, sha, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        headRepository.ToLowerInvariant().ShouldNotBe(repository.ToLowerInvariant(), $"pull request {number} comes from {repository} itself, not from a fork");
        statuses.Where(status => status.Context.StartsWith("codefresh/", StringComparison.Ordinal)).ShouldBeEmpty($"the fork commit {sha} got Codefresh statuses");
        builds.ShouldBeEmpty($"the fork commit {sha} started builds: {string.Join("; ", builds.Select(build => build.ToString()))}");
    }
}
