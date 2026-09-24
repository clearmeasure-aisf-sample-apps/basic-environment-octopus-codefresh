using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves that discovery reads capabilities and categories from both real test assemblies.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class TestDiscoveryTests
{
    private IReadOnlyList<DiscoveredTest> tests = null!;

    [OneTimeSetUp]
    public void DiscoverBothAssemblies()
    {
        tests = TestDiscovery.Discover(ConformanceAssemblies.Load());
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenDiscover_BothAssemblies_FindsTestsOfEach()
    {
        var assemblies = tests.Select(test => test.Assembly).Distinct().Order(StringComparer.Ordinal);

        assemblies.ShouldBe(["Platform.Conformance.Offline", "Platform.Conformance.Tests"]);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenDiscover_OfflineTest_ReadsCapabilityAndClassCategory()
    {
        var test = tests.Single(candidate => candidate.FullName == "Platform.Conformance.Offline.CatalogueConsistencyTests.WhenCheckingCatalogue_EveryCapability_HasATestCarryingItsId");

        test.CapabilityIds.ShouldBe(["CAP-HARNESS-001"]);
        test.Categories.ShouldBe([Categories.Offline]);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenDiscover_LiveTestWithMethodCategory_MergesClassAndMethodCategories()
    {
        var test = tests.Single(candidate => candidate.FullName == "Platform.Conformance.Tests.Smoke.PlatformReachabilityTests.WhenGetCurrentUserAsync_WithApiKey_ReturnsUserAndActiveAccount");

        test.Assembly.ShouldBe("Platform.Conformance.Tests");
        test.CapabilityIds.ShouldBe(["CAP-HARNESS-003"]);
        test.Categories.ShouldBe([Categories.Build, Categories.Live]);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenDiscover_ParameterizedTest_ListsTheMethodOnce()
    {
        var matches = tests.Where(candidate => candidate.FullName == "Platform.Conformance.Offline.Harness.DurationFormatTests.WhenHuman_Duration_UsesTheLargestSensibleUnit");

        matches.ShouldHaveSingleItem().CapabilityIds.ShouldBe(["CAP-HARNESS-007"]);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenDiscover_AbstractBaseFixture_IsNotATest()
    {
        tests.ShouldNotContain(test => test.FullName.StartsWith(typeof(PlatformTestBase).FullName!, StringComparison.Ordinal));
    }
}
