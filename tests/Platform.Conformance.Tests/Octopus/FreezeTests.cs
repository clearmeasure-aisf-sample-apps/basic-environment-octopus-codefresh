using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-006: a prod freeze blocks deployments unless it is overridden with a reason. Each test creates a temporary
/// project freeze on sandbox in prod (deleted at teardown), so the weekly prod-weekend-freeze-sandbox never matters.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class FreezeTests : OctopusCapabilityTestBase
{
    /// <summary>A deployment to a frozen prod is refused.</summary>
    [Test]
    [Capability("CAP-OCT-006")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_CreateDeploymentAsync_ProdFrozen_IsRefused()
    {
        var rest = Rest("the freeze test");
        var release = await ReleaseReadyForProdAsync("Default");
        await FreezeProdAsync("blocked");

        var answer = await rest.CreateDeploymentAsync(SandboxProject, release.Version, "prod", [], null, Token);

        answer.IsSuccess.ShouldBeFalse($"a deployment to frozen prod was accepted: {answer.Body}");
        answer.Body.ShouldContain("freeze", Case.Insensitive);
    }

    /// <summary>A deployment to a frozen prod starts when the freeze is overridden with a reason.</summary>
    [Test]
    [Capability("CAP-OCT-006")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_CreateDeploymentAsync_ProdFrozenWithOverrideReason_Starts()
    {
        var rest = Rest("the freeze override test");
        var release = await ReleaseReadyForProdAsync("Default");
        var freeze = await FreezeProdAsync("override");

        var answer = await rest.CreateDeploymentAsync(SandboxProject, release.Version, "prod", [freeze], Reason, Token);

        answer.IsSuccess.ShouldBeTrue($"the override was refused ({answer.StatusCode}): {answer.Body}");
        var tasks = OctopusRest.TaskIds(answer);
        tasks.ShouldNotBeEmpty();
        foreach (var task in tasks)
        {
            CancelAtTeardown(task, "the overriding prod deployment");
            await rest.CancelTaskAsync(task, Token);
        }
    }

    private async Task<string> FreezeProdAsync(string purpose)
    {
        var rest = Rest("the freeze test");
        var project = await Octopus.GetProjectAsync(SandboxProject, Token);
        var prod = await EnvironmentIdAsync("prod");
        var name = Run.ResourceName($"freeze-{purpose}");
        var now = DateTimeOffset.UtcNow;
        var id = await rest.CreateProjectFreezeAsync(name, project.Id, prod, now.AddMinutes(-5), now.AddHours(2), Token);
        Cleanup.Register($"delete deployment freeze {name} ({id})", token => rest.DeleteFreezeAsync(id, token));
        return name;
    }
}
