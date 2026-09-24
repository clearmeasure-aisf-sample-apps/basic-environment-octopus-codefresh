using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-003: uat waits for the UAT sign-off and prod for the Prod go/no-go. Each deployment must pause at its manual
/// intervention, owned by the approver team of the environment; the test then approves with the run reason.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ApprovalTests : OctopusCapabilityTestBase
{
    /// <summary>A uat deployment pauses at "UAT sign-off", owned by UAT Approvers.</summary>
    [Test]
    [Capability("CAP-OCT-003")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_ToUat_PausesForUatSignOff()
    {
        Rest("the uat approval test");
        var release = await FindDeployedReleaseAsync("tdd", "Default") ?? await ReleaseAfterTddAsync();
        var team = await TeamIdAsync("UAT Approvers");
        var deployment = await DeployAsync(release, "uat");
        CancelAtTeardown(deployment.TaskId, "the uat deployment");

        var intervention = await WaitForInterventionAsync(deployment.TaskId, "UAT sign-off");

        intervention.ResponsibleTeamIds.ShouldContain(team);
        await CompleteAsync(deployment.TaskId, "uat after sign-off");
    }

    /// <summary>A prod deployment pauses at "Prod go/no-go", owned by Prod Approvers.</summary>
    [Test]
    [Capability("CAP-OCT-003")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_ToProd_PausesForGoNoGo()
    {
        Rest("the prod approval test");
        var release = await ReleaseReadyForProdAsync("Default");
        var team = await TeamIdAsync("Prod Approvers");
        var deployment = await DeployAsync(release, "prod");
        CancelAtTeardown(deployment.TaskId, "the prod deployment");

        var intervention = await WaitForInterventionAsync(deployment.TaskId, "Prod go/no-go");

        intervention.ResponsibleTeamIds.ShouldContain(team);
        await CompleteAsync(deployment.TaskId, "prod after go/no-go");
    }

    private async Task<SandboxRelease> ReleaseAfterTddAsync()
    {
        var release = await CreateSandboxReleaseAsync("Default");
        await CompleteAsync(await WaitForAutomaticDeploymentAsync(release, "tdd"), "tdd");
        return release;
    }
}
