using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-003: build compute scales from zero on the first job and back to zero after idle. Observed on the
/// <c>builds</c> pool of aks-platform-build through ARM. The suite itself runs as a build on that pool, so "during a
/// build" is now; "back to zero" is read from the pool's machines: every one of them was created after the previous
/// platform-env/conformance build finished, which a pool that never emptied cannot show (Q51).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class BuildScalingTests : CodefreshCapabilityTestBase
{
    /// <summary>The pool autoscales from zero and holds a node while this build runs.</summary>
    [Test]
    [Capability("CAP-CF-003")]
    [Category(Categories.Build)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_GetAgentPoolsAsync_DuringABuild_BuildsPoolScaledUpFromZero()
    {
        if (!InsideCodefresh)
        {
            throw new PlatformPrerequisiteException("Prerequisites missing for the scale-up test: it observes the build it runs in; run it in platform-env/conformance (CF_BUILD_ID is not set).");
        }

        var pools = await Azure.GetAgentPoolsAsync(CodefreshPlatform.BuildGroup, CodefreshPlatform.BuildCluster, Token);

        var builds = pools.SingleOrDefault(pool => pool.Name == CodefreshPlatform.BuildsPool);

        builds.ShouldNotBeNull($"{CodefreshPlatform.BuildCluster} has no pool {CodefreshPlatform.BuildsPool}; pools: {string.Join(", ", pools.Select(pool => pool.Name))}");
        builds.AutoScaling.ShouldBe(true, "the builds pool autoscales");
        builds.MinCount.ShouldBe(0, "the builds pool scales to zero");
        (builds.Count ?? 0).ShouldBeGreaterThanOrEqualTo(1, "this build runs on the builds pool, so it holds a node");
    }

    /// <summary>Every machine of the pool is younger than the end of the previous conformance build.</summary>
    [Test]
    [Capability("CAP-CF-003")]
    [Category(Categories.Build)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_ListBuildsNodeCreationTimesAsync_AfterIdle_BuildsPoolReturnedToZero()
    {
        var codefresh = RequireCodefresh("the scale-down test");
        var arm = RequireArm("the scale-down test");
        var current = CodefreshPlatform.Variable(CodefreshPlatform.BuildIdVariable);
        var previous = (await codefresh.ListBuildsAsync(CodefreshPlatform.ConformancePipeline, 20, Token))
            .FirstOrDefault(build => build.Id != current && build.IsTerminal && build.Finished is not null)
            ?? throw new PlatformPrerequisiteException($"Prerequisites missing for the scale-down test: no earlier finished {CodefreshPlatform.ConformancePipeline} build to measure from.");
        var cluster = await arm.GetClusterAsync(Token);
        cluster.ShouldNotBeNull($"cluster {CodefreshPlatform.BuildCluster} does not exist in {CodefreshPlatform.BuildGroup}");
        var nodeGroup = JsonRead.Text(JsonRead.Path(cluster.Value, "properties"), "nodeResourceGroup") ?? CodefreshPlatform.BuildNodeGroup;

        var created = await arm.ListBuildsNodeCreationTimesAsync(nodeGroup, Token);

        created.ShouldAllBe(time => time != null, "every machine of the builds pool reports timeCreated");
        created.ShouldAllBe(
            time => time > previous.Finished,
            $"a builds machine is older than the end of build {previous} ({previous.Finished:O}): the pool did not return to zero in between; created: {string.Join(", ", created.Select(time => time?.ToString("O")))}");
    }
}
