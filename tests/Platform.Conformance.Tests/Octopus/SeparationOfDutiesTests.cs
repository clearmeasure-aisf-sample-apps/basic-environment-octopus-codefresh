using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-004: separation of duties is configurable. <c>platform-sod-guard</c> reads <c>Platform.SoDMode</c>: channel
/// Default uses <c>single-operator</c> (library set Platform Environment), where the deployment creator may approve with
/// a reason; channel Strict of sandbox sets <c>enforce</c>, where the creator may not approve. In both tests the
/// automation user creates the prod deployment and answers the Prod go/no-go itself.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SeparationOfDutiesTests : OctopusCapabilityTestBase
{
    /// <summary>Single-operator mode: the creator approves with a reason and the deployment proceeds.</summary>
    [Test]
    [Capability("CAP-OCT-004")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_SodGuard_SingleOperatorCreatorApprovesWithReason_Proceeds()
    {
        Rest("the single-operator test");
        var release = await ReleaseReadyForProdAsync("Default");

        var task = await DeployAndCompleteAsync(release, "prod");

        var log = await Octopus.GetTaskLogAsync(task.Id, Token);
        log.ShouldContain("Single-operator mode");
        log.ShouldContain("Separation of duties holds for 'Prod go/no-go' (Platform.SoDMode single-operator)");
    }

    /// <summary>Enforce mode (channel Strict): the creator approves and the guard fails the deployment.</summary>
    [Test]
    [Capability("CAP-OCT-004")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_SodGuard_EnforceCreatorApproves_FailsTheDeployment()
    {
        Rest("the enforce test");
        var release = await ReleaseReadyForProdAsync("Strict");
        var deployment = await DeployAsync(release, "prod");
        CancelAtTeardown(deployment.TaskId, "the Strict prod deployment");

        var task = await CompleteAsync(deployment.TaskId, "prod on channel Strict", requireSuccess: false);

        task.FinishedSuccessfully.ShouldBeFalse($"the creator approved under enforce and {task} still succeeded");
        (await Octopus.GetTaskLogAsync(task.Id, Token)).ShouldContain("Separation of duties (enforce)");
    }
}
