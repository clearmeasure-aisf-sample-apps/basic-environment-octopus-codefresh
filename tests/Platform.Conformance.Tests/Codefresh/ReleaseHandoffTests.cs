using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-008: the handoff creates exactly one Octopus release numbered with the build version. Observed on the releases
/// of project <c>sandbox</c> whose notes start with <c>app-commit: &lt;sha&gt;</c> (written by buildinfo.sh) for the
/// release commit that platform-env/conformance-arm pushed, and on the second sandbox/release build of that commit that
/// the arm queued (<c>CONFORMANCE_RERUN_BUILD_ID</c>). The release version equals the image tag the build pushed next to
/// <c>sha-&lt;sha7&gt;</c>. The rerun cannot push the locked tags again and never reaches the handoff; either way it adds
/// no release.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ReleaseHandoffTests : CodefreshCapabilityTestBase
{
    /// <summary>One release for the commit, numbered like the images and packages of the build.</summary>
    [Test]
    [Capability("CAP-CF-008")]
    [Category(Categories.NonProd)]
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_ListReleasesAsync_ReleaseBuild_CreatesOneReleaseWithTheBuildVersion()
    {
        var sha = RequireArmVariable(CodefreshPlatform.ReleaseShaVariable, "the release handoff test");
        var registry = RequireRegistry("the release handoff test");
        var builds = await BuildsOfAsync(sha);

        var releases = await ReleasesForAsync(sha);

        builds.ShouldContain(build => build.Status == "success", $"no successful sandbox/release build of {sha}: {string.Join("; ", builds)}");
        releases.Count.ShouldBe(1, $"sandbox releases for {sha}: {string.Join("; ", releases)}");
        var release = releases[0];
        release.PackageVersions.ShouldAllBe(version => version == release.Version, $"release {release} selects other package versions: {string.Join(", ", release.PackageVersions)}");
        var tags = await registry.ListTagsAsync($"apps/{CodefreshPlatform.Sandbox}/web", Token);
        var byCommit = tags.FirstOrDefault(tag => tag.Name == $"sha-{CodefreshPlatform.Short(sha)}");
        var byVersion = tags.FirstOrDefault(tag => tag.Name == release.Version);
        byCommit.ShouldNotBeNull($"apps/sandbox/web:sha-{CodefreshPlatform.Short(sha)} does not exist");
        byVersion.ShouldNotBeNull($"apps/sandbox/web:{release.Version} does not exist");
        byVersion.Digest.ShouldBe(byCommit.Digest, $"release {release.Version} and commit {sha} name different images");
    }

    /// <summary>A second build of the same commit creates no second release.</summary>
    [Test]
    [Capability("CAP-CF-008")]
    [Category(Categories.NonProd)]
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_ListReleasesAsync_RerunOfTheBuild_CreatesNoSecondRelease()
    {
        var sha = RequireArmVariable(CodefreshPlatform.ReleaseShaVariable, "the release rerun test");
        var rerunId = RequireArmVariable(CodefreshPlatform.RerunBuildVariable, "the release rerun test");
        var codefresh = RequireCodefresh("the release rerun test");
        var queued = await codefresh.GetBuildAsync(rerunId, Token);
        queued.ShouldNotBeNull($"Codefresh build {rerunId} does not exist");
        var rerun = await FinishedAsync(queued);
        var builds = await BuildsOfAsync(sha);

        var releases = await ReleasesForAsync(sha);

        rerun.Revision.ShouldBe(sha, $"build {rerun} is not a build of {sha}");
        builds.Length.ShouldBeGreaterThanOrEqualTo(2, $"sandbox/release builds of {sha}: {string.Join("; ", builds)}");
        releases.Count.ShouldBe(1, $"sandbox releases for {sha} after the rerun {rerun}: {string.Join("; ", releases)}");
    }

    private async Task<CodefreshBuildRecord[]> BuildsOfAsync(string sha)
    {
        var codefresh = RequireCodefresh("the release handoff test");
        var builds = (await codefresh.ListBuildsAsync(CodefreshPlatform.SandboxRelease, 50, Token))
            .Where(build => string.Equals(build.Revision, sha, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var finished = new List<CodefreshBuildRecord>();
        foreach (var build in builds)
        {
            finished.Add(await FinishedAsync(build));
        }

        return [.. finished];
    }

    private async Task<IReadOnlyList<OctopusReleaseRecord>> ReleasesForAsync(string sha)
    {
        Settings.Check("the release handoff test")
            .Setting(nameof(Settings.OctopusUrl), Settings.OctopusUrl)
            .Setting(nameof(Settings.OctopusSpaceId), Settings.OctopusSpaceId)
            .Secret(EnvironmentVariableNames.OctopusApiKey, Settings.Secrets.OctopusApiKey)
            .ThrowIfMissing();
        var project = await Octopus.GetProjectAsync(CodefreshPlatform.Sandbox, Token);
        using var releases = new OctopusReleases(Settings.OctopusUrl!, Settings.OctopusSpaceId!, Settings.Secrets.OctopusApiKey!, Settings.TimeLimits.HttpTimeout);
        return (await releases.ListAsync(project.Id, 100, Token)).Where(release => release.IsFor(sha)).ToArray();
    }
}
