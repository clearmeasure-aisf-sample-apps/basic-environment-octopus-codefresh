using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Smoke;

/// <summary>
/// Read-only smoke tests: the harness can reach each external system with the configured credentials.
/// Without the secrets they are Inconclusive, never failed.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class PlatformReachabilityTests : PlatformTestBase
{
    [Test]
    [Capability("CAP-HARNESS-002")]
    public async Task WhenGetSpaceAsync_WithConfiguredSpace_ReturnsThatSpace()
    {
        var space = await Octopus.GetSpaceAsync();

        space.Id.ShouldBe(Settings.OctopusSpaceId);
        space.Name.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    [Capability("CAP-HARNESS-003")]
    [Category(Categories.Build)]
    public async Task WhenGetCurrentUserAsync_WithApiKey_ReturnsUserAndActiveAccount()
    {
        var user = await Codefresh.GetCurrentUserAsync();

        user.UserName.ShouldNotBeNullOrWhiteSpace();
        user.ActiveAccountName.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    [Capability("CAP-HARNESS-004")]
    public async Task WhenGetSubscriptionAsync_WithConfiguredSubscription_ReturnsEnabledSubscription()
    {
        var subscription = await Azure.GetSubscriptionAsync();

        subscription.SubscriptionId.ShouldBe(Settings.AzureSubscriptionId, StringCompareShould.IgnoreCase);
        subscription.State.ShouldBe("Enabled");
    }
}
