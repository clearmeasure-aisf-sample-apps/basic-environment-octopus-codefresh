using System.Globalization;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Tests.Kit;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-005, live half: a fork pull request never starts a pipeline. The fixture repository and every app repository
/// of the descriptors refuse forks (GitHub setting <c>allow_forking: false</c>), so no fork pull request can exist; that
/// is the check. A repository that allows forking needs the fork-PR observation instead: a pull request from a fork
/// outside the org (its number in <c>CONFORMANCE_FORK_PULL_REQUEST</c>) whose head commit has no <c>codefresh/*</c> status
/// and no sandbox build. The offline half checks that every trigger keeps fork events off.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ForkPullRequestTests : CodefreshCapabilityTestBase
{
    /// <summary>Every repository refuses forks, or a real fork pull request started nothing.</summary>
    [Test]
    [Capability("CAP-CF-005")]
    [Category(Categories.Build)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_ListBuildsAsync_ForkPullRequest_StartsNoPipeline()
    {
        var repository = RequireSandboxRepository("the fork pull request test");
        var gitHub = RequireGitHub("the fork pull request test");
        var descriptors = KitDescriptors.Load(RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance));
        var repositories = descriptors.SelectMany(app => app.Repositories.Select(entry => entry.Name)).Append(repository)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Contains('<', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var forkable = new List<string>();
        foreach (var name in repositories)
        {
            var found = await gitHub.GetRepositoryAsync(name, Token);
            found.ShouldNotBeNull($"repository {name} does not exist or is not readable");
            if (!found.Value.TryGetProperty("allow_forking", out var allow) || allow.ValueKind != System.Text.Json.JsonValueKind.False)
            {
                forkable.Add(name);
            }
        }

        if (forkable.Count == 0)
        {
            return;
        }

        var variable = CodefreshPlatform.Variable(CodefreshPlatform.ForkPullRequestVariable);
        variable.ShouldNotBeNull(
            $"{string.Join(", ", forkable)} allow forking, and no fork pull request proves that a fork cannot start a pipeline: turn forking off (the platform's setting) or set {CodefreshPlatform.ForkPullRequestVariable}.");
        var number = int.Parse(variable, CultureInfo.InvariantCulture);
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
