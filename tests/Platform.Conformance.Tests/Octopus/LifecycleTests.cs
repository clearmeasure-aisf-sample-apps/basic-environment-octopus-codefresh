using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-001: a new release deploys to tdd automatically. Lifecycle <c>platform-standard</c> starts with an automatic
/// TDD phase (<c>tdd_auto_deploy</c> in octopus/terraform); the test creates a sandbox release and never deploys it itself.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class LifecycleTests : OctopusCapabilityTestBase
{
    /// <summary>A new sandbox release gets a tdd deployment from the lifecycle, and that deployment succeeds.</summary>
    [Test]
    [Capability("CAP-OCT-001")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public async Task Should_CreateReleaseAsync_NewSandboxRelease_AutoDeploysToTdd()
    {
        Rest("the lifecycle test");
        var release = await CreateSandboxReleaseAsync("Default");

        var taskId = await WaitForAutomaticDeploymentAsync(release, "tdd");
        var task = await CompleteAsync(taskId, "automatic tdd deployment");

        task.FinishedSuccessfully.ShouldBeTrue();
    }
}
