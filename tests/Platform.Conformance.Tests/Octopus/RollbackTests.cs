using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-007: redeploying the previous release rolls back. In tdd the test redeploys the newest earlier sandbox release
/// whose images differ from the current one, checks the pin and <c>/version</c>, and redeploys the current release at
/// teardown.
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
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_PreviousReleaseToTdd_RestoresPreviousVersion()
    {
        Rest("the rollback test", gitHub: true);
        var (current, previous) = await CurrentAndPreviousAsync("tdd");
        Cleanup.Register($"redeploy {current.Version} to tdd", async _ => await DeployAndCompleteAsync(current, "tdd"));
        var baseUrl = await SandboxBaseUrlAsync("tdd");

        await DeployAndCompleteAsync(previous, "tdd");

        var pins = PinnedTags((await GitHub.GetFileAsync(Settings.EnvRepo!, PinPath("tdd"), "main", Token)).Content);
        foreach (var (image, version) in previous.Packages)
        {
            pins.ShouldContainKeyAndValue(image, version, $"tdd pin of {image} after the rollback");
        }

        using var http = PlatformHttp.Create(baseUrl, Settings.TimeLimits.HttpTimeout);
        using var reported = JsonDocument.Parse(await http.GetStringAsync(new Uri("version", UriKind.Relative), Token));
        reported.RootElement.GetProperty("version").GetString().ShouldBe(previous.Packages["web"]);
    }
}
