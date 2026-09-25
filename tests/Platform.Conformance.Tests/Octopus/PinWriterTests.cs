using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-012: a deployment writes only its own app's pins. The shared sandbox tdd rollout (<see cref="SandboxTddRollout"/>)
/// moves the sandbox tdd pins once (it redeploys the previous release, then the images tdd ran before); the test reads every
/// commit that deployment added to the environment repository: each touches only <c>gitops/apps/sandbox/envs/tdd/</c>.
/// The offline half (the bot-path audit of <c>main</c>) is in Platform.Conformance.Offline.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class PinWriterTests : OctopusCapabilityTestBase
{
    /// <summary>The pin commits of a sandbox tdd deployment change only sandbox's tdd pin files.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_ImageTagStep_SandboxTddDeployment_WritesOnlyOwnPins()
    {
        var rollout = SandboxTddRollout.Instance;
        await rollout.RequireAsync(RolloutPhase.RollBack);

        var rollback = rollout.Rollback!.Require(RolloutPhase.RollBack);

        rollback.Deployment.FinishedSuccessfully.ShouldBeTrue($"deployment of {rollback.Previous.Version} to tdd: {rollback.Deployment}");
        rollback.CommitFiles.ShouldNotBeEmpty("the deployment moved the pins, so it must have committed");
        foreach (var (commit, author, files) in rollback.CommitFiles)
        {
            files.ShouldAllBe(
                file => file.StartsWith($"gitops/apps/{SandboxProject}/envs/tdd/", StringComparison.Ordinal),
                $"commit {commit} by {author} changed files outside sandbox's tdd pins: {string.Join(", ", files)}");
        }
    }
}
