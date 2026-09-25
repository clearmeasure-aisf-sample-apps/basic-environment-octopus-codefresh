using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-007: redeploying the previous release rolls back. The shared sandbox tdd rollout (<see cref="SandboxTddRollout"/>)
/// redeploys the newest earlier sandbox release whose images differ from the current one; the test checks the pin and
/// <c>/version</c>. The rollout then redeploys the images tdd ran before, as this test's teardown did.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class RollbackTests : OctopusCapabilityTestBase
{
    /// <summary>Redeploying release N-1 to tdd restores its pins and the version it reports.</summary>
    [Test]
    [Capability("CAP-OCT-007")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_PreviousReleaseToTdd_RestoresPreviousVersion()
    {
        var rollout = SandboxTddRollout.Instance;
        await rollout.RequireAsync(RolloutPhase.RollBack);

        var rollback = rollout.Rollback!.Require(RolloutPhase.RollBack);

        rollback.Deployment.FinishedSuccessfully.ShouldBeTrue($"deployment of {rollback.Previous.Version} to tdd: {rollback.Deployment}");
        rollback.Previous.Packages.TryGetValue("web", out var expected).ShouldBeTrue($"release {rollback.Previous.Version} selects no web image");
        foreach (var (image, version) in rollback.Previous.Packages)
        {
            rollback.Pins.ShouldContainKeyAndValue(image, version, $"tdd pin of {image} after the rollback");
        }

        rollback.ReportedVersion.ShouldBe(expected, "the version sandbox-tdd reports after the rollback");
    }
}
