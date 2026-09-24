using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Offline;

/// <summary>
/// Enforces the one-to-one mapping between the capability catalogue (<c>catalogue/capabilities.yaml</c> merged with
/// <c>catalogue/capabilities.d/*.yaml</c>, or <c>PLATFORM_CATALOGUE_FILE</c>) and the tests of both conformance assemblies.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class CatalogueConsistencyTests
{
    private CapabilityCatalogue catalogue = null!;
    private IReadOnlyList<DiscoveredTest> tests = null!;
    private IReadOnlyList<CatalogueViolation> violations = null!;

    [OneTimeSetUp]
    public void LoadCatalogueAndDiscoverTests()
    {
        catalogue = CatalogueLocator.Load();
        tests = TestDiscovery.Discover(ConformanceAssemblies.Load());
        violations = CatalogueConsistency.Check(catalogue, tests);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenLoadingCatalogue_MergedFiles_AreValidAndDefineTheHarnessCapability()
    {
        var harness = catalogue.Find("CAP-HARNESS-001");

        harness.ShouldNotBeNull();
        tests.ShouldNotBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingCatalogue_EveryCapability_HasATestCarryingItsId()
    {
        var problems = Messages(ViolationKind.CapabilityWithoutTest);

        problems.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingTests_EveryTest_CarriesACatalogueCapability()
    {
        var problems = Messages(ViolationKind.TestWithoutCapability, ViolationKind.UnknownCapability);

        problems.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingCatalogue_TestLists_MatchTheCapabilityAttributesExactly()
    {
        var problems = Messages(ViolationKind.StaleTestEntry, ViolationKind.UnlistedTest);

        problems.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingCatalogue_EveryOfflineCapability_SaysWhyItIsOffline()
    {
        var problems = Messages(ViolationKind.MissingWhyOffline);

        problems.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingTests_EveryDestructiveTest_IsCategorisedDestructiveAndNonProdAndNeverProd()
    {
        var problems = Messages(ViolationKind.DestructiveCategory);

        problems.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingTests_Categories_AgreeWithTheLiveAndTierFlags()
    {
        var problems = Messages(ViolationKind.LiveCategory, ViolationKind.TierCategory);

        problems.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheckingCatalogue_EveryRule_IsCoveredByATestOfThisFixture()
    {
        var covered = new[]
        {
            ViolationKind.CapabilityWithoutTest, ViolationKind.TestWithoutCapability, ViolationKind.UnknownCapability,
            ViolationKind.StaleTestEntry, ViolationKind.UnlistedTest, ViolationKind.MissingWhyOffline,
            ViolationKind.DestructiveCategory, ViolationKind.LiveCategory, ViolationKind.TierCategory,
        };

        Enum.GetValues<ViolationKind>().Except(covered).ShouldBeEmpty();
        violations.Select(violation => violation.ToString()).ShouldBeEmpty();
    }

    private List<string> Messages(params ViolationKind[] kinds) =>
        violations.Where(violation => kinds.Contains(violation.Kind)).Select(violation => violation.Message).ToList();
}
