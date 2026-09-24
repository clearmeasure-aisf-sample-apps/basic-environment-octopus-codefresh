using Platform.Onboarding.Checks;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Scaffolding;
using Platform.Onboarding.Tests.Support;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class AppFilesCheckTests
{
    private static AppDescriptor Load(string yaml) =>
        DescriptorCatalog.LoadFile("apps/demoapp.yaml", yaml, DescriptorSchema.Load(TempRepository.CommittedSchemaPath)).Descriptor.ShouldNotBeNull();

    private static TempRepository Scaffolded(AppDescriptor descriptor)
    {
        var repository = new TempRepository().WithStarters();
        var scaffolder = new Scaffolder(repository.Repository);
        scaffolder.Apply(scaffolder.Plan(descriptor, new ScaffoldRequest("minimal", ["deploy-minimal"], "kustomize")), force: false, descriptor.Name);
        return repository;
    }

    [Test]
    public void Should_Check_FreshScaffold_HasNoFinding()
    {
        var descriptor = Load(SampleDescriptors.WithDatabase());
        using var repository = Scaffolded(descriptor);

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp", "otherapp"]);

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Should_Check_NothingScaffolded_ReportsEachMissingFolder()
    {
        using var repository = new TempRepository();

        var findings = new AppFilesCheck(repository.Repository).Check(Load(SampleDescriptors.WithDatabase()), ["demoapp"]);

        findings.Count(finding => finding.Rule == "files").ShouldBe(9);
    }

    [Test]
    public void Should_Check_PinWithDigest_ReportsTagsOnly()
    {
        var descriptor = Load(SampleDescriptors.Minimal());
        using var repository = Scaffolded(descriptor);
        repository.Write("gitops/apps/demoapp/envs/tdd/app/kustomization.yaml",
            "images:\n  - name: <acr-name>.azurecr.io/apps/demoapp/web\n    newTag: 1.0.0\n    digest: sha256:abc\n");

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp"]);

        findings.ShouldHaveSingleItem().Message.ShouldContain("pins are tags only");
    }

    [Test]
    public void Should_Check_PinOfUndeclaredImage_ReportsPinRule()
    {
        var descriptor = Load(SampleDescriptors.Minimal());
        using var repository = Scaffolded(descriptor);
        repository.Write("gitops/apps/demoapp/envs/uat/app/kustomization.yaml",
            "images:\n  - name: <acr-name>.azurecr.io/apps/demoapp/web\n    newTag: 1.0.0\n  - name: <acr-name>.azurecr.io/apps/otherapp/web\n    newTag: 1.0.0\n");

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp", "otherapp"]);

        findings.ShouldContain(finding => finding.Rule == "pin" && finding.Message.Contains("apps/otherapp/web", StringComparison.Ordinal));
        findings.ShouldContain(finding => finding.Rule == "cross-app");
    }

    [Test]
    public void Should_Check_ReferenceToOtherAppNamespaceOrVault_ReportsCrossApp()
    {
        var descriptor = Load(SampleDescriptors.Minimal());
        using var repository = Scaffolded(descriptor);
        repository.Write("gitops/apps/demoapp/app/base/extra.yaml", "store: otherapp-tdd\nvault: kv-otherapp-t-0000\n");

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp", "otherapp"]);

        findings.Count(finding => finding.Rule == "cross-app").ShouldBe(2);
    }

    [Test]
    public void Should_Check_OtherAppNamedInCommentOrMarkedLine_IsIgnored()
    {
        var descriptor = Load(SampleDescriptors.Minimal());
        using var repository = Scaffolded(descriptor);
        repository.Write("gitops/apps/demoapp/app/base/extra.yaml", "# shared with apps/otherapp/web\nstore: otherapp-tdd # name-lint: allow\n");

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp", "otherapp"]);

        findings.ShouldNotContain(finding => finding.Rule == "cross-app");
    }

    [Test]
    public void Should_Check_SpecOfUndeclaredProject_ReportsFilesRule()
    {
        var descriptor = Load(SampleDescriptors.Minimal());
        using var repository = Scaffolded(descriptor);
        repository.Write("codefresh/apps/demoapp/specs/extra.yml", "metadata:\n  name: demoapp-api/release-x\n");

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp"]);

        findings.ShouldHaveSingleItem().Message.ShouldContain("project 'demoapp-api' is not in codefresh.projects");
    }

    [Test]
    public void Should_Check_ReleasePipelineWithBadName_ReportsSignerRule()
    {
        var descriptor = Load(SampleDescriptors.Minimal());
        using var repository = Scaffolded(descriptor);
        repository.Write("codefresh/apps/demoapp/specs/release.yml", "metadata:\n  name: demoapp/releaseNightly\n");

        var findings = new AppFilesCheck(repository.Repository).Check(descriptor, ["demoapp"]);

        findings.ShouldHaveSingleItem().Message.ShouldContain("the prod signer rule");
    }
}
