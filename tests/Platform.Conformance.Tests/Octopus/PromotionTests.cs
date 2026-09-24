using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-002: promotion to uat and prod moves only pins, and the image references are identical everywhere. The same
/// sandbox release goes to uat and prod; the environment repository changes only in the pin files of those
/// environments, and both clusters run the same image references.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class PromotionTests : OctopusCapabilityTestBase
{
    /// <summary>Promoting one release to uat and prod pins the same tags and runs the same images in both.</summary>
    [Test]
    [Capability("CAP-OCT-002")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_DeployReleaseAsync_PromotionToProd_KeepsIdenticalImages()
    {
        Rest("the promotion test", gitHub: true);
        var repository = Settings.EnvRepo!;
        var release = await ReleaseReadyForProdAsync("Default");
        var before = await GitHub.GetBranchHeadAsync(repository, "main", Token);

        await DeployAndCompleteAsync(release, "uat");
        await DeployAndCompleteAsync(release, "prod");

        var changed = await GitHub.CompareAsync(repository, before, "main", Token);
        changed.Files.ShouldAllBe(file => file == PinPath("uat") || file == PinPath("prod"), "promotion changed files other than the uat and prod pins");
        var uatPins = PinnedTags((await GitHub.GetFileAsync(repository, PinPath("uat"), "main", Token)).Content);
        var prodPins = PinnedTags((await GitHub.GetFileAsync(repository, PinPath("prod"), "main", Token)).Content);
        foreach (var (image, version) in release.Packages)
        {
            uatPins.ShouldContainKeyAndValue(image, version, $"uat pin of {image}");
            prodPins.ShouldContainKeyAndValue(image, version, $"prod pin of {image}");
        }

        var uatImages = await SandboxImagesAsync(PlatformTier.NonProd, "uat");
        var prodImages = await SandboxImagesAsync(PlatformTier.Prod, "prod");
        prodImages.ShouldBe(uatImages, ignoreOrder: true);
    }

    private async Task<IReadOnlyList<string>> SandboxImagesAsync(PlatformTier tier, string environment)
    {
        RequireTier(tier, $"reading the {environment} workloads");
        var cluster = await KubernetesAsync(tier, Token);
        var workloads = await cluster.ListDeploymentsAsync($"{SandboxProject}-{environment}", cancellationToken: Token);
        return workloads.SelectMany(workload => workload.Images).Where(image => image.Contains($"/apps/{SandboxProject}/", StringComparison.Ordinal)).Distinct().ToArray();
    }
}
