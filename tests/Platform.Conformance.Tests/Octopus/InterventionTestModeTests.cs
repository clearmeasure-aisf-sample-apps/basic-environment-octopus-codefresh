using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-005: the automation user answers interventions only with a recorded reason. With
/// <c>Platform.InterventionTestMode</c> true, <c>platform-sod-guard</c> accepts an answer of the automation user only when
/// the first line of its notes is <c>conformance:&lt;run-id&gt;</c> or <c>e2e:&lt;run-id&gt;</c>.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class InterventionTestModeTests : OctopusCapabilityTestBase
{
    /// <summary>The automation user answers the Prod go/no-go with the run reason: the guard records it and passes.</summary>
    [Test]
    [Capability("CAP-OCT-005")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_SodGuard_AutomationAnswersWithRunReason_Proceeds()
    {
        Rest("the intervention test mode test");
        var release = await ReleaseReadyForProdAsync("Default");

        var task = await DeployAndCompleteAsync(release, "prod");

        (await Octopus.GetTaskLogAsync(task.Id, Token)).ShouldContain($"was answered by the automation user in intervention test mode: {Reason}");
    }

    /// <summary>The automation user answers the Prod go/no-go without a run reason: the guard fails the deployment.</summary>
    [Test]
    [Capability("CAP-OCT-005")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_SodGuard_AutomationAnswersWithoutReason_FailsTheDeployment()
    {
        Rest("the intervention test mode test");
        var release = await ReleaseReadyForProdAsync("Default");
        var deployment = await DeployAsync(release, "prod");
        CancelAtTeardown(deployment.TaskId, "the prod deployment");

        var task = await CompleteAsync(deployment.TaskId, "prod without a reason", _ => "approved", requireSuccess: false);

        task.FinishedSuccessfully.ShouldBeFalse($"the automation user answered without a run reason and {task} still succeeded");
        (await Octopus.GetTaskLogAsync(task.Id, Token)).ShouldContain("without the reason conformance:<run-id> or e2e:<run-id>");
    }
}
