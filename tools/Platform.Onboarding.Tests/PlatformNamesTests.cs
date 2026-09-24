using Platform.Onboarding.Naming;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class PlatformNamesTests
{
    [TestCase("00000000-0000-0000-0000-000000000000", "workorders", "tdd", "d1a3")]
    [TestCase("00000000-0000-0000-0000-000000000000", "workorders", "prod", "b88a")]
    [TestCase("11111111-2222-3333-4444-555555555555", "sandbox", "uat", "dbc2")]
    public void Should_VaultHash_KnownInput_MatchesSha1OfSubscriptionAppEnv(string subscription, string app, string environment, string expected)
    {
        var hash = PlatformNames.VaultHash(subscription, app, environment);

        hash.ShouldBe(expected);
    }

    [Test]
    public void Should_VaultHash_UppercaseSubscription_EqualsLowercaseForm()
    {
        var upper = PlatformNames.VaultHash("AAAAAAAA-0000-0000-0000-000000000000", "demoapp", "tdd");

        upper.ShouldBe(PlatformNames.VaultHash("aaaaaaaa-0000-0000-0000-000000000000", "demoapp", "tdd"));
    }

    [Test]
    public void Should_VaultName_LongestSlug_FitsTwentyFourCharacters()
    {
        var name = PlatformNames.VaultName("abcdefghijkl", "prod", "00000000-0000-0000-0000-000000000000");

        name.Length.ShouldBeLessThanOrEqualTo(24);
        name.ShouldStartWith("kv-abcdefghijkl-p-");
    }

    [Test]
    public void Should_VaultName_NoSubscription_KeepsHashPlaceholder()
    {
        var name = PlatformNames.VaultName("demoapp", "uat", null);

        name.ShouldBe("kv-demoapp-u-<hash4>");
    }

    [TestCase("ab")]
    [TestCase("abcdefghijklm")]
    [TestCase("work-orders")]
    [TestCase("WorkOrders")]
    [TestCase("1app")]
    public void Should_SlugError_MalformedSlug_ReturnsPatternMessage(string app)
    {
        var message = PlatformNames.SlugError(app, allowFixture: true);

        message.ShouldNotBeNull();
        message.ShouldContain("not a valid app slug");
    }

    [TestCase("platform")]
    [TestCase("argocd")]
    [TestCase("kube")]
    public void Should_SlugError_ReservedWord_ReturnsReservedMessage(string app)
    {
        var message = PlatformNames.SlugError(app, allowFixture: true);

        message.ShouldNotBeNull();
        message.ShouldContain("reserved");
    }

    [Test]
    public void Should_SlugError_FixtureWhenNotAllowed_IsRefused()
    {
        var refused = PlatformNames.SlugError("sandbox", allowFixture: false);
        var accepted = PlatformNames.SlugError("sandbox", allowFixture: true);

        refused.ShouldNotBeNull();
        accepted.ShouldBeNull();
    }

    [Test]
    public void Should_TierOf_EachEnvironment_FollowsTheFixedMap()
    {
        var tiers = PlatformNames.Environments.Select(PlatformNames.TierOf).ToArray();

        tiers.ShouldBe(["nonprod", "nonprod", "prod"]);
    }

    [Test]
    public void Should_Namespace_WithPart_InsertsPartBeforeEnvironment()
    {
        var name = PlatformNames.Namespace("demoapp", "uat", "api");

        name.ShouldBe("demoapp-api-uat");
    }
}
