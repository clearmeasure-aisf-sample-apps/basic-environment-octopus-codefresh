using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Catalogue;
using Platform.Conformance.Offline.Support;
using static Platform.Conformance.Offline.Support.Catalogues;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves each rule of <see cref="CatalogueConsistency.Check"/> with small in-memory catalogues.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class CatalogueConsistencyCheckTests
{
    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_CatalogueAndTestsMatch_ReportsNoViolation()
    {
        var catalogue = Parse(
            Entry("CAP-SAMPLE-001", OfflineTest),
            Entry("CAP-SAMPLE-002", LiveTest, live: true, tier: "nonprod"));
        DiscoveredTest[] tests =
        [
            OfflineTestOf(OfflineTest, "CAP-SAMPLE-001"),
            LiveTestOf(LiveTest, ["CAP-SAMPLE-002"], Categories.NonProd),
        ];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        violations.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_CapabilityHasNoTestCarryingItsId_ReportsCapabilityWithoutTest()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", OfflineTest));
        DiscoveredTest[] tests = [OfflineTestOf(OfflineTest, "CAP-SAMPLE-999")];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        violations.ShouldContain(violation => violation.Kind == ViolationKind.CapabilityWithoutTest && violation.CapabilityId == "CAP-SAMPLE-001");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_TestCarriesNoCapability_ReportsTestWithoutCapability()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", OfflineTest));
        DiscoveredTest[] tests =
        [
            OfflineTestOf(OfflineTest, "CAP-SAMPLE-001"),
            OfflineTestOf("Sample.Offline.ConfigTests.WhenOrphaned_NoCapability_Fails"),
        ];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        violations.ShouldHaveSingleItem().Kind.ShouldBe(ViolationKind.TestWithoutCapability);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_TestCarriesIdMissingFromCatalogue_ReportsUnknownCapability()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", OfflineTest));
        DiscoveredTest[] tests = [OfflineTestOf(OfflineTest, "CAP-SAMPLE-001", "CAP-SAMPLE-404")];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.UnknownCapability);
        violation.Message.ShouldContain("CAP-SAMPLE-404");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_CatalogueListsTestThatDoesNotExist_ReportsStaleTestEntry()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", $"{OfflineTest}, Sample.Offline.ConfigTests.WhenRemoved_LongAgo_StillListed"));
        DiscoveredTest[] tests = [OfflineTestOf(OfflineTest, "CAP-SAMPLE-001")];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.StaleTestEntry);
        violation.TestName.ShouldBe("Sample.Offline.ConfigTests.WhenRemoved_LongAgo_StillListed");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_CatalogueListsTestWithoutTheAttribute_ReportsStaleTestEntry()
    {
        var catalogue = Parse(
            Entry("CAP-SAMPLE-001", OfflineTest),
            Entry("CAP-SAMPLE-002", $"{OfflineTest}, Sample.Offline.OtherTests.WhenOther_Case_Holds"));
        DiscoveredTest[] tests =
        [
            OfflineTestOf(OfflineTest, "CAP-SAMPLE-001"),
            OfflineTestOf("Sample.Offline.OtherTests.WhenOther_Case_Holds", "CAP-SAMPLE-002"),
        ];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.StaleTestEntry);
        violation.Message.ShouldContain("does not carry [Capability(\"CAP-SAMPLE-002\")]");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_TestCarriesCapabilityThatDoesNotListIt_ReportsUnlistedTest()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", OfflineTest));
        DiscoveredTest[] tests =
        [
            OfflineTestOf(OfflineTest, "CAP-SAMPLE-001"),
            OfflineTestOf("Sample.Offline.ConfigTests.WhenAdded_Recently_NotListed", "CAP-SAMPLE-001"),
        ];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.UnlistedTest);
        violation.TestName.ShouldBe("Sample.Offline.ConfigTests.WhenAdded_Recently_NotListed");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_OfflineCapabilityLacksWhyOffline_ReportsMissingWhyOffline()
    {
        var capability = new Capability
        {
            Id = "CAP-SAMPLE-001",
            Statement = "Built without the loader, which would reject it",
            Owner = CapabilityOwner.Platform,
            Adr = "design/platform-design.md",
            ObservedBy = "a unit test",
            Tests = [OfflineTest],
            Live = false,
            Destructive = false,
            Tier = CapabilityTier.All,
            WhyOffline = null,
            Source = "in-memory",
            Line = 1,
        };
        var catalogue = CapabilityCatalogue.FromCapabilities([capability]);
        DiscoveredTest[] tests = [OfflineTestOf(OfflineTest, "CAP-SAMPLE-001")];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        violations.ShouldHaveSingleItem().Kind.ShouldBe(ViolationKind.MissingWhyOffline);
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_DestructiveCapabilityTestNotCategorisedDestructiveAndNonProd_ReportsBothCategories()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", LiveTest, live: true, destructive: true, tier: "all"));
        DiscoveredTest[] tests = [LiveTestOf(LiveTest, ["CAP-SAMPLE-001"])];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        violations.Count(violation => violation.Kind == ViolationKind.DestructiveCategory).ShouldBe(2);
        violations.ShouldContain(violation => violation.Message.Contains($"not categorised {Categories.Destructive}"));
        violations.ShouldContain(violation => violation.Message.Contains($"not categorised {Categories.NonProd}"));
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_DestructiveTestCategorisedProd_ReportsDestructiveCategory()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", LiveTest, live: true, destructive: true, tier: "nonprod"));
        DiscoveredTest[] tests = [LiveTestOf(LiveTest, ["CAP-SAMPLE-001"], Categories.Destructive, Categories.NonProd, Categories.Prod)];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.DestructiveCategory);
        violation.Message.ShouldContain("never run in prod");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_TestCategorisedDestructiveButCapabilityIsNot_ReportsDestructiveCategory()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", LiveTest, live: true, tier: "nonprod"));
        DiscoveredTest[] tests = [LiveTestOf(LiveTest, ["CAP-SAMPLE-001"], Categories.Destructive, Categories.NonProd)];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.DestructiveCategory);
        violation.Message.ShouldContain("has destructive: true");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_DestructiveTestCategorisedDestructiveAndNonProd_ReportsNoViolation()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", LiveTest, live: true, destructive: true, tier: "nonprod"));
        DiscoveredTest[] tests = [LiveTestOf(LiveTest, ["CAP-SAMPLE-001"], Categories.Destructive, Categories.NonProd, Categories.Slow)];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        violations.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_TestCategorisedNeitherLiveNorOffline_ReportsLiveCategory()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", OfflineTest));
        DiscoveredTest[] tests = [Test(OfflineTest, ["CAP-SAMPLE-001"])];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.LiveCategory);
        violation.Message.ShouldContain("neither");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_LiveTestOfOfflineCapability_ReportsLiveCategory()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", LiveTest));
        DiscoveredTest[] tests = [LiveTestOf(LiveTest, ["CAP-SAMPLE-001"])];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.LiveCategory);
        violation.Message.ShouldContain("live: false");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_LiveCapabilityWithOnlyOfflineTests_ReportsLiveCategory()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", OfflineTest, live: true));
        DiscoveredTest[] tests = [OfflineTestOf(OfflineTest, "CAP-SAMPLE-001")];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.LiveCategory);
        violation.Message.ShouldContain("none of its tests is categorised Live");
    }

    [Test]
    [Capability("CAP-HARNESS-001")]
    public void WhenCheck_LiveTestLacksTierCategory_ReportsTierCategory()
    {
        var catalogue = Parse(Entry("CAP-SAMPLE-001", LiveTest, live: true, tier: "prod"));
        DiscoveredTest[] tests = [LiveTestOf(LiveTest, ["CAP-SAMPLE-001"], Categories.NonProd)];

        var violations = CatalogueConsistency.Check(catalogue, tests);

        var violation = violations.ShouldHaveSingleItem();
        violation.Kind.ShouldBe(ViolationKind.TierCategory);
        violation.Message.ShouldContain($"not categorised {Categories.Prod}");
    }
}
