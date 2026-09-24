using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-012: a deployment writes only its own app's pins. The test moves the sandbox tdd pins (redeploying the previous
/// release, then the current one at teardown) and reads every commit the deployment added to the environment repository:
/// each touches only <c>gitops/apps/sandbox/envs/tdd/</c>. The offline half (the bot-path audit of <c>main</c>) is in
/// Platform.Conformance.Offline.
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
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_ImageTagStep_SandboxTddDeployment_WritesOnlyOwnPins()
    {
        var rest = Rest("the pin writer test", gitHub: true);
        var repository = Settings.EnvRepo!;
        var (current, previous) = await CurrentAndPreviousAsync("tdd");
        Cleanup.Register($"redeploy {current.Version} to tdd", async _ => await DeployAndCompleteAsync(current, "tdd"));
        var before = await GitHub.GetBranchHeadAsync(repository, "main", Token);

        await DeployAndCompleteAsync(previous, "tdd");

        var comparison = await GitHub.CompareAsync(repository, before, "main", Token);
        comparison.Commits.ShouldNotBeEmpty("the deployment moved the pins, so it must have committed");
        foreach (var commit in comparison.Commits)
        {
            var files = await rest.GitHubCommitFilesAsync(repository, commit.Sha, Token);
            files.ShouldAllBe(
                file => file.StartsWith($"gitops/apps/{SandboxProject}/envs/tdd/", StringComparison.Ordinal),
                $"commit {commit.Sha} by {commit.AuthorName} changed files outside sandbox's tdd pins: {string.Join(", ", files)}");
        }
    }
}
