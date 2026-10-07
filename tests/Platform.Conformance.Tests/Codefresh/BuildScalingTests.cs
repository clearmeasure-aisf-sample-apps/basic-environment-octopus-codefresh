using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-003: build compute keeps a warm floor of at most one node, scales up for jobs and back down to that floor after
/// idle. Observed on the <c>builds</c> pool of aks-platform-build through ARM. The floor is the pool's autoscaler minimum,
/// which terraform/build validates as 0 or 1 (<c>builds_pool.min_count</c>; 1 since 2026-09-27, because a cold node added
/// 5 to 7 minutes to a build after idle). The suite itself runs as a build on that pool, so "during a build" is now; "back
/// to the floor" is read from the pool's machines: at most the floor of them were created before the previous
/// platform-env/conformance build finished, which a pool that kept its extra nodes cannot show (Q51).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class BuildScalingTests : CodefreshCapabilityTestBase
{
    // The most nodes the builds pool keeps warm (the min_count validation of terraform/build).
    private const int MaxWarmNodes = 1;

    /// <summary>The pool autoscales from a floor of at most one node and holds a node while this build runs.</summary>
    [Test]
    [Capability("CAP-CF-003")]
    [Category(Categories.Build)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_GetAgentPoolsAsync_DuringABuild_BuildsPoolScaledUpFromItsFloor()
    {
        if (!InsideCodefresh)
        {
            throw new PlatformPrerequisiteException("Prerequisites missing for the scale-up test: it observes the build it runs in; run it in platform-env/conformance (CF_BUILD_ID is not set).");
        }

        var builds = await GetBuildsPoolAsync();

        builds.AutoScaling.ShouldBe(true, "the builds pool autoscales");
        (builds.MinCount ?? 0).ShouldBeInRange(0, MaxWarmNodes, "the builds pool keeps at most one node warm (terraform/build builds_pool.min_count)");
        (builds.Count ?? 0).ShouldBeGreaterThanOrEqualTo(1, "this build runs on the builds pool, so it holds a node");
    }

    /// <summary>At most the pool's warm floor of its machines is older than the end of the previous conformance build.</summary>
    [Test]
    [Capability("CAP-CF-003")]
    [Category(Categories.Build)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_ListBuildsNodeCreationTimesAsync_AfterIdle_BuildsPoolReturnedToItsFloor()
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
        var floor = (await GetBuildsPoolAsync()).MinCount ?? 0;

        var created = await arm.ListBuildsNodeCreationTimesAsync(nodeGroup, Token);

        created.ShouldAllBe(time => time != null, "every machine of the builds pool reports timeCreated");
        var older = created.Where(time => time <= previous.Finished).ToArray();
        older.Length.ShouldBeLessThanOrEqualTo(
            floor,
            $"{older.Length} builds machine(s) are older than the end of build {previous} ({previous.Finished:O}), more than the pool's warm floor of {floor}: the pool did not return to its floor in between; created: {string.Join(", ", created.Select(time => time?.ToString("O")))}");
    }

    private async Task<AksAgentPoolState> GetBuildsPoolAsync()
    {
        var pools = await Azure.GetAgentPoolsAsync(CodefreshPlatform.BuildGroup, CodefreshPlatform.BuildCluster, Token);
        var builds = pools.SingleOrDefault(pool => pool.Name == CodefreshPlatform.BuildsPool);
        builds.ShouldNotBeNull($"{CodefreshPlatform.BuildCluster} has no pool {CodefreshPlatform.BuildsPool}; pools: {string.Join(", ", pools.Select(pool => pool.Name))}");
        return builds;
    }
}
