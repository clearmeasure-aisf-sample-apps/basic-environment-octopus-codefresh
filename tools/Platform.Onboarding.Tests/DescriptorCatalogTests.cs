using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Output;
using Platform.Onboarding.Tests.Support;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class DescriptorCatalogTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);
    private DescriptorSchema schema = null!;

    [OneTimeSetUp]
    public void LoadSchema() => schema = DescriptorSchema.Load(TempRepository.CommittedSchemaPath);

    private IReadOnlyList<Finding> Validate(params (string File, string Yaml)[] files) =>
        DescriptorCatalog.FromFiles(files.Select(file => DescriptorCatalog.LoadFile($"apps/{file.File}.yaml", file.Yaml, schema)).ToArray(), Today).Findings;

    [Test]
    public void Should_FromFiles_MinimalDescriptor_HasNoFinding()
    {
        var findings = Validate(("demoapp", SampleDescriptors.Minimal()));

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Should_FromFiles_CommittedDescriptors_HaveNoError()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(TempRepository.CommittedSchemaPath))!;
        var files = Directory.GetFiles(Path.Combine(root, "apps"), "*.yaml")
            .Select(path => (Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)))
            .ToArray();

        var findings = Validate(files);

        files.Length.ShouldBeGreaterThanOrEqualTo(2);
        findings.Where(finding => finding.Severity == Severity.Error).ShouldBeEmpty();
    }

    [Test]
    public void Should_FromFiles_UnknownKey_ReportsUnknownKeyWithLine()
    {
        var yaml = SampleDescriptors.Minimal() + "\ndeployable: typo\n";

        var findings = Validate(("demoapp", yaml));

        var finding = findings.ShouldHaveSingleItem();
        finding.Rule.ShouldBe("schema");
        finding.Message.ShouldContain("unknown key 'deployable'");
        finding.Line.ShouldBe(16);
    }

    [TestCase("platform")]
    [TestCase("Demo-App")]
    [TestCase("ab")]
    public void Should_FromFiles_BadName_IsRejectedBySchema(string name)
    {
        var yaml = SampleDescriptors.Minimal().Replace("name: demoapp", $"name: {name}", StringComparison.Ordinal);

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.StartsWith("/name:", StringComparison.Ordinal) && finding.Line == 2);
    }

    [Test]
    public void Should_FromFiles_NameDiffersFromFile_ReportsNameRule()
    {
        var findings = Validate(("otherapp", SampleDescriptors.Minimal()));

        findings.ShouldContain(finding => finding.Rule == "name" && finding.Message.Contains("must equal the file name 'otherapp'", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_DeployableNamedDb_IsRejected()
    {
        var yaml = SampleDescriptors.Minimal().Replace("  - name: app", "  - name: db", StringComparison.Ordinal);

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.Contains("/deployables/0/name", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_DeployableNamesUndeclaredProject_ReportsDeployableRule()
    {
        var yaml = SampleDescriptors.Minimal().Replace("    octopusProject: demoapp", "    octopusProject: demoapp-api", StringComparison.Ordinal);

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "deployable" && finding.Message.Contains("'demoapp-api'", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_ProjectWithoutAppPrefix_ReportsProjectRule()
    {
        var yaml = SampleDescriptors.Minimal().Replace("  projects: [demoapp]", "  projects: [other]", StringComparison.Ordinal);

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "project" && finding.Message.Contains("Codefresh project 'other'", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_RepositoryOutsideOrg_IsRejected()
    {
        var yaml = SampleDescriptors.Minimal(repository: "ClearMeasureLabs/bootcamp-palermo-workorders");

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.Contains("/repositories/0/name", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_HostOfItsOwnNamespace_HasNoFinding()
    {
        var yaml = SampleDescriptors.Minimal() + "\nhosts:\n  prod: demoapp-prod.southcentralus.cloudapp.azure.com\n";

        var loaded = DescriptorCatalog.LoadFile("apps/demoapp.yaml", yaml, schema);
        var findings = Validate(("demoapp", yaml));

        findings.ShouldBeEmpty();
        loaded.Descriptor.ShouldNotBeNull().Hosts["prod"].ShouldBe("demoapp-prod.southcentralus.cloudapp.azure.com");
    }

    [TestCase("prod: other-prod.southcentralus.cloudapp.azure.com", "must start with 'demoapp-prod.'")]
    [TestCase("prod: demoapp-uat.southcentralus.cloudapp.azure.com", "must start with 'demoapp-prod.'")]
    [TestCase("uat: demoapp-uat.20-65-1-2.sslip.io", "is an sslip.io host, the default")]
    public void Should_FromFiles_HostOutsideTheRules_ReportsHostRule(string entry, string expected)
    {
        var yaml = SampleDescriptors.Minimal() + $"\nhosts:\n  {entry}\n";

        var findings = Validate(("demoapp", yaml));

        var finding = findings.ShouldHaveSingleItem();
        finding.Rule.ShouldBe("host");
        finding.Message.ShouldContain(expected);
        finding.Line.ShouldBe(17);
    }

    [Test]
    public void Should_FromFiles_HostOfUnlistedEnvironment_ReportsHostRule()
    {
        var yaml = SampleDescriptors.Minimal() + "\nenvironments: [tdd, uat]\nhosts:\n  prod: demoapp-prod.southcentralus.cloudapp.azure.com\n";

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "host" && finding.Message.Contains("environment 'prod' is not in environments", StringComparison.Ordinal));
    }

    [TestCase("hosts:\n  staging: demoapp-staging.example.com\n", "unknown key 'staging'")]
    [TestCase("hosts:\n  prod: Demoapp-Prod.example.com\n", "/hosts/prod")]
    [TestCase("hosts:\n  prod: demoapp-prod\n", "/hosts/prod")]
    [TestCase("hosts: {}\n", "/hosts")]
    public void Should_FromFiles_MalformedHosts_IsRejectedBySchema(string block, string pointer)
    {
        var yaml = SampleDescriptors.Minimal() + "\n" + block;

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.Contains(pointer, StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_QuotaAboveDefault_IsRejected()
    {
        var yaml = SampleDescriptors.Minimal() + "\nquotas:\n  memoryGiB: 8\n";

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.Contains("/quotas/memoryGiB", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_HelmWithoutHelmBlock_IsRejected()
    {
        var yaml = SampleDescriptors.Minimal().Replace("packaging: kustomize", "packaging: helm", StringComparison.Ordinal);

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.Contains("helm", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_RolesWithoutWorkloadIdentity_IsRejected()
    {
        var yaml = SampleDescriptors.Minimal() + "\nazure:\n  resourceGroup: true\n  roles: [Reader]\n";

        var findings = Validate(("demoapp", yaml));

        findings.ShouldContain(finding => finding.Rule == "schema" && finding.Message.Contains("workloadIdentity", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_SameProjectInTwoDescriptors_ReportsClash()
    {
        var first = SampleDescriptors.Minimal("alpha");
        var second = SampleDescriptors.Minimal("beta").Replace("  projects: [beta]", "  projects: [alpha]", StringComparison.Ordinal);

        var findings = Validate(("alpha", first), ("beta", second));

        findings.ShouldContain(finding => finding.Rule == "project" && finding.Message.Contains("claimed by apps/alpha.yaml and apps/beta.yaml", StringComparison.Ordinal));
    }

    [Test]
    public void Should_FromFiles_ExpiredApp_WarnsOnly()
    {
        var yaml = SampleDescriptors.Minimal() + "\nexpires: \"2026-09-01\"\n";

        var findings = Validate(("demoapp", yaml));

        var finding = findings.ShouldHaveSingleItem();
        finding.Severity.ShouldBe(Severity.Warning);
        finding.Rule.ShouldBe("expiry");
    }

    [Test]
    public void Should_FromJson_AbsentOptionalKeys_TakeSchemaDefaults()
    {
        var loaded = DescriptorCatalog.LoadFile("apps/demoapp.yaml", SampleDescriptors.Minimal(), schema);

        var descriptor = loaded.Descriptor.ShouldNotBeNull();
        descriptor.Status.ShouldBe("active");
        descriptor.Environments.ShouldBe(["tdd", "uat", "prod"]);
        descriptor.OctopusProjects.ShouldHaveSingleItem().Channels.ShouldBe(["Default", "Hotfix"]);
        descriptor.OctopusProjects[0].Lifecycle.ShouldBe("platform-standard");
        descriptor.Database.ShouldBeNull();
    }
}
