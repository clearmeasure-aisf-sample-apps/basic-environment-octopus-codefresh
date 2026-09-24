using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Catalogue;
using static Platform.Conformance.Offline.Support.Catalogues;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the catalogue schema validation and the merge of <c>capabilities.d</c> fragments.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class CapabilityCatalogueTests
{
    private string repositoryRoot = null!;

    [SetUp]
    public void CreateRepositoryRoot()
    {
        repositoryRoot = Path.Combine(Path.GetTempPath(), "platform-conformance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
    }

    [TearDown]
    public void DeleteRepositoryRoot()
    {
        if (Directory.Exists(repositoryRoot))
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_CompleteEntry_ReadsEveryField()
    {
        const string yaml = """
            # Header comments are allowed.
            capabilities:
              - id: CAP-SLEEP-001
                statement: An idle nonprod cluster is stopped outside the working window
                owner: octopus
                adr: ADR-IR33
                observed_by: ARM power state of aks-platform-nonprod
                tests:
                  - Platform.Conformance.Tests.Sleep.SleepTests.WhenIdle_OutsideWindow_ClusterStops
                live: true
                destructive: true
                tier: nonprod
            """;

        var catalogue = CapabilityCatalogue.Parse(yaml, "sleep.yaml");

        var capability = catalogue.Capabilities.ShouldHaveSingleItem();
        capability.Id.ShouldBe("CAP-SLEEP-001");
        capability.Statement.ShouldBe("An idle nonprod cluster is stopped outside the working window");
        capability.Owner.ShouldBe(CapabilityOwner.Octopus);
        capability.Adr.ShouldBe("ADR-IR33");
        capability.ObservedBy.ShouldBe("ARM power state of aks-platform-nonprod");
        capability.Tests.ShouldBe(["Platform.Conformance.Tests.Sleep.SleepTests.WhenIdle_OutsideWindow_ClusterStops"]);
        capability.Live.ShouldBeTrue();
        capability.Destructive.ShouldBeTrue();
        capability.Tier.ShouldBe(CapabilityTier.NonProd);
        capability.WhyOffline.ShouldBeNull();
        capability.Location.ShouldBe("sleep.yaml:3");
        catalogue.Find("CAP-SLEEP-001").ShouldBe(capability);
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_FileWithOnlyComments_ReturnsEmptyCatalogue()
    {
        var catalogue = CapabilityCatalogue.Parse("# Nothing here yet." + Environment.NewLine, "empty.yaml");

        catalogue.Capabilities.ShouldBeEmpty();
        catalogue.Sources.ShouldBe(["empty.yaml"]);
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_EntryMissingRequiredKeys_ReportsEachKeyWithFileAndLine()
    {
        const string yaml = """
            capabilities:
              - id: CAP-SAMPLE-001
                live: true
                destructive: false
            """;

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "partial.yaml"));

        exception.Errors.ShouldContain("partial.yaml:2: CAP-SAMPLE-001: missing required key 'statement'");
        exception.Errors.ShouldContain("partial.yaml:2: CAP-SAMPLE-001: missing required key 'owner'");
        exception.Errors.ShouldContain(error => error.StartsWith("partial.yaml:2: CAP-SAMPLE-001: missing required key 'tests'", StringComparison.Ordinal));
        exception.Errors.ShouldContain("partial.yaml:2: CAP-SAMPLE-001: missing required key 'tier'");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_UnknownKey_ReportsTheKeyAndTheAllowedKeys()
    {
        var yaml = "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", OfflineTest).Replace("    live:", "    owners: platform" + Environment.NewLine + "    live:", StringComparison.Ordinal);

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "typo.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldContain("unknown key 'owners'; allowed keys: id, statement, owner");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_InvalidOwnerTierAndBoolean_ReportsTheAllowedValues()
    {
        var yaml = "capabilities:" + Environment.NewLine
            + Entry("CAP-SAMPLE-001", OfflineTest, owner: "github", tier: "staging").Replace("live: false", "live: yes", StringComparison.Ordinal);

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "values.yaml"));

        exception.Errors.ShouldContain(error => error.Contains("owner must be one of codefresh, octopus, argocd, azure, kyverno, onboarding, platform (was 'github')"));
        exception.Errors.ShouldContain(error => error.Contains("tier must be one of build, nonprod, prod, all (was 'staging')"));
        exception.Errors.ShouldContain(error => error.Contains("'live' must be true or false (was 'yes')"));
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_OfflineCapabilityWithoutWhyOffline_ReportsWhyOfflineRequired()
    {
        var yaml = "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", OfflineTest, whyOffline: null);

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "offline.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldContain("why_offline is required when live is false");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_LiveCapabilityWithWhyOffline_ReportsWhyOfflineNotAllowed()
    {
        var yaml = "capabilities:" + Environment.NewLine
            + Entry("CAP-SAMPLE-001", LiveTest, live: true) + Environment.NewLine + "    why_offline: it is not";

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "live.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldContain("why_offline applies only when live is false");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_DestructiveCapabilityWithTierProd_ReportsTierError()
    {
        var yaml = "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", LiveTest, live: true, destructive: true, tier: "prod");

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "destructive.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldContain("a destructive capability cannot have tier prod");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_MalformedIdAndTestName_ReportsExpectedFormats()
    {
        var yaml = "capabilities:" + Environment.NewLine + Entry("cap-sample-1", "ConfigTests.WhenParsing(1)");

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "format.yaml"));

        exception.Errors.ShouldContain(error => error.Contains("id 'cap-sample-1' must look like CAP-AREA-001"));
        exception.Errors.ShouldContain(error => error.Contains("'ConfigTests.WhenParsing(1)' is not a fully qualified test name"));
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_SameTestListedTwice_ReportsDuplicateTest()
    {
        var yaml = "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", $"{OfflineTest}, {OfflineTest}");

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "twice.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldContain($"test '{OfflineTest}' is listed twice");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_DuplicateIdInOneFile_ReportsBothLines()
    {
        var yaml = "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", OfflineTest) + Environment.NewLine + Entry("CAP-SAMPLE-001", LiveTest);

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "twice.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldBe("capability CAP-SAMPLE-001 is defined more than once: twice.yaml:2 and twice.yaml:13");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenFromSources_DuplicateIdAcrossFragments_ReportsBothFiles()
    {
        CatalogueSource[] sources =
        [
            new("catalogue/capabilities.d/argocd.yaml", "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", OfflineTest)),
            new("catalogue/capabilities.d/octopus.yaml", "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", LiveTest)),
        ];

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.FromSources(sources));

        var error = exception.Errors.ShouldHaveSingleItem();
        error.ShouldContain("capability CAP-SAMPLE-001 is defined more than once");
        error.ShouldContain("catalogue/capabilities.d/argocd.yaml:2");
        error.ShouldContain("catalogue/capabilities.d/octopus.yaml:2");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_InvalidYaml_ReportsFileAndLine()
    {
        const string yaml = """
            capabilities:
              - id: CAP-SAMPLE-001
                statement: [unclosed
            """;

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "broken.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldStartWith("broken.yaml:");
        exception.Message.ShouldContain("invalid YAML");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenParse_UnknownTopLevelKey_ReportsTheOnlyAllowedKey()
    {
        var yaml = "version: 2" + Environment.NewLine + "capabilities:" + Environment.NewLine + Entry("CAP-SAMPLE-001", OfflineTest);

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.Parse(yaml, "top.yaml"));

        exception.Errors.ShouldHaveSingleItem().ShouldBe("top.yaml:1: unknown top-level key 'version'; the only top-level key is 'capabilities'");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenLoadDirectory_MainFileAndFragments_MergesMainFirstThenFragmentsByFileName()
    {
        WriteCatalogueFile("capabilities.yaml", Entry("CAP-MAIN-001", OfflineTest));
        WriteCatalogueFile("capabilities.d/octopus.yaml", Entry("CAP-OCTO-001", OfflineTest));
        WriteCatalogueFile("capabilities.d/argocd.yaml", Entry("CAP-ARGO-001", OfflineTest));
        WriteCatalogueFile("capabilities.d/README.md", "Fragments, one per owning role.");

        var catalogue = CapabilityCatalogue.LoadDirectory(repositoryRoot);

        catalogue.Sources.ShouldBe(["catalogue/capabilities.yaml", "catalogue/capabilities.d/argocd.yaml", "catalogue/capabilities.d/octopus.yaml"]);
        catalogue.Capabilities.Select(capability => capability.Id).ShouldBe(["CAP-MAIN-001", "CAP-ARGO-001", "CAP-OCTO-001"]);
        catalogue.Find("CAP-ARGO-001")!.Source.ShouldBe("catalogue/capabilities.d/argocd.yaml");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenLoadDirectory_FragmentsWithoutMainFile_LoadsTheFragments()
    {
        WriteCatalogueFile("capabilities.d/harness.yaml", Entry("CAP-HARNESS-901", OfflineTest));

        var catalogue = CapabilityCatalogue.LoadDirectory(repositoryRoot);

        catalogue.Capabilities.ShouldHaveSingleItem().Id.ShouldBe("CAP-HARNESS-901");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenLoadDirectory_FragmentWithYmlExtension_ReportsThatItMustBeRenamed()
    {
        WriteCatalogueFile("capabilities.d/harness.yaml", Entry("CAP-HARNESS-901", OfflineTest));
        WriteCatalogueFile("capabilities.d/kyverno.yml", Entry("CAP-KYVERNO-001", OfflineTest));

        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.LoadDirectory(repositoryRoot));

        exception.Errors.ShouldHaveSingleItem().ShouldStartWith("catalogue/capabilities.d/kyverno.yml: fragments must end in lowercase .yaml");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenLoadDirectory_NoCatalogueFiles_ReportsWhereItLooked()
    {
        var exception = Should.Throw<CatalogueValidationException>(() => CapabilityCatalogue.LoadDirectory(repositoryRoot));

        exception.Errors.ShouldHaveSingleItem().ShouldContain("expected catalogue/capabilities.yaml or catalogue/capabilities.d/*.yaml");
    }

    [Test]
    [Capability("CAP-HARNESS-005")]
    public void WhenLoad_FileDoesNotExist_ThrowsFileNotFound()
    {
        var missing = Path.Combine(repositoryRoot, "nowhere.yaml");

        var exception = Should.Throw<FileNotFoundException>(() => CapabilityCatalogue.Load(missing));

        exception.Message.ShouldContain(missing);
    }

    private void WriteCatalogueFile(string relativePath, string entry)
    {
        var path = Path.Combine(repositoryRoot, "catalogue", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relativePath.EndsWith(".md", StringComparison.Ordinal) ? entry : "capabilities:" + Environment.NewLine + entry + Environment.NewLine);
    }
}
