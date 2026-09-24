using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Support;

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
        var baseUrl = await SandboxBaseUrlAsync("tdd");
        previous.Packages.TryGetValue("web", out var expected).ShouldBeTrue($"release {previous.Version} selects no web image");
        Cleanup.Register($"redeploy {current.Version} to tdd", async _ => await DeployAndCompleteAsync(current, "tdd"));

        await DeployAndCompleteAsync(previous, "tdd");

        var pins = PinnedTags((await GitHub.GetFileAsync(Settings.EnvRepo!, PinPath("tdd"), "main", Token)).Content);
        foreach (var (image, version) in previous.Packages)
        {
            pins.ShouldContainKeyAndValue(image, version, $"tdd pin of {image} after the rollback");
        }

        using var http = PlatformHttp.Create(baseUrl, Settings.TimeLimits.HttpTimeout);
        var reported = await Poll.UntilAsync(
            async token =>
            {
                using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri("version", UriKind.Relative), token));
                return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
            },
            version => version == expected,
            TimeSpan.FromMinutes(5),
            Settings.TimeLimits.PollInterval,
            $"{baseUrl}version to report {expected}",
            retryWhen: exception => exception is HttpRequestException or JsonException,
            cancellationToken: Token);
        reported.ShouldBe(expected);
    }
}
