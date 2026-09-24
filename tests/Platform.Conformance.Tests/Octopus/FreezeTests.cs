using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-006: a prod freeze blocks deployments unless it is overridden with a reason. Each test creates a temporary
/// project freeze on sandbox in prod and deletes it as soon as the deployment attempt is made (again at teardown if that
/// failed); the freeze ends one hour after it starts, so no path leaves it in place. The weekly
/// prod-weekend-freeze-sandbox never matters.
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
        var freeze = await FreezeProdAsync("blocked");
        RestAnswer answer;
        try
        {
            answer = await rest.CreateDeploymentAsync(SandboxProject, release.Version, "prod", [], null, Token);
        }
        finally
        {
            await rest.DeleteFreezeAsync(freeze.Id, CancellationToken.None);
        }

        if (answer.IsSuccess)
        {
            foreach (var task in OctopusRest.TaskIds(answer))
            {
                CancelAtTeardown(task, "the prod deployment the freeze let through");
                await rest.CancelTaskAsync(task, CancellationToken.None);
            }
        }

        answer.IsSuccess.ShouldBeFalse($"a deployment to frozen prod was accepted (and cancelled): {answer.Body}");
        // [VERIFY] the wording of the executions API's refusal; it is expected to name the deployment freeze.
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
        RestAnswer answer;
        try
        {
            answer = await rest.CreateDeploymentAsync(SandboxProject, release.Version, "prod", [freeze.Name], Reason, Token);
        }
        finally
        {
            await rest.DeleteFreezeAsync(freeze.Id, CancellationToken.None);
        }

        var tasks = answer.IsSuccess ? OctopusRest.TaskIds(answer) : [];
        foreach (var task in tasks)
        {
            CancelAtTeardown(task, "the overriding prod deployment");
            await rest.CancelTaskAsync(task, CancellationToken.None);
        }

        answer.IsSuccess.ShouldBeTrue($"the override was refused ({answer.StatusCode}): {answer.Body}");
        tasks.ShouldNotBeEmpty();
    }

    private async Task<(string Id, string Name)> FreezeProdAsync(string purpose)
    {
        var rest = Rest("the freeze test");
        var project = await Octopus.GetProjectAsync(SandboxProject, Token);
        var prod = await EnvironmentIdAsync("prod");
        var name = Run.ResourceName($"freeze-{purpose}");
        var now = DateTimeOffset.UtcNow;
        var id = await rest.CreateProjectFreezeAsync(name, project.Id, prod, now.AddMinutes(-5), now.AddHours(1), Token);
        Cleanup.Register($"delete deployment freeze {name} ({id}) if it is still there", token => rest.DeleteFreezeAsync(id, token));
        return (id, name);
    }
}
