using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Scaffolding;
using Platform.Onboarding.Tests.Support;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class ScaffolderTests
{
    private static AppDescriptor Load(string yaml) =>
        DescriptorCatalog.LoadFile("apps/demoapp.yaml", yaml, DescriptorSchema.Load(TempRepository.CommittedSchemaPath)).Descriptor.ShouldNotBeNull();

    [Test]
    public void Should_Apply_AllRoles_SubstitutesTokensInPathsAndContents()
    {
        using var repository = new TempRepository().WithStarters();
        var scaffolder = new Scaffolder(repository.Repository);
        var plan = scaffolder.Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest("minimal", ["deploy-minimal"], "kustomize"));

        var (written, conflicts) = scaffolder.Apply(plan, force: false, "demoapp");

        conflicts.ShouldBeEmpty();
        written.ShouldContain("codefresh/apps/demoapp/specs/release.yml");
        repository.Read("codefresh/apps/demoapp/specs/release.yml").ShouldContain("name: demoapp/release");
        repository.Read("codefresh/apps/demoapp/pipelines/ci.yml").ShouldContain("app demoapp from clearmeasure-aisf-sample-apps/20260924-001 on main");
        repository.Read(".octopus/apps/demoapp/demoapp/deployment_process.ocl").ShouldContain("Wake demoapp");
        repository.Read(".octopus/apps/demoapp/demoapp/variables.ocl").ShouldContain("value \"demoapp\"");
        repository.Read("gitops/apps/demoapp/envs/uat/app/kustomization.yaml").ShouldContain("namespace: demoapp-uat");
        repository.Read("gitops/apps/demoapp/envs/prod/app/kustomization.yaml").ShouldContain("<acr-name>.azurecr.io/apps/demoapp/web");
    }

    [Test]
    public void Should_Apply_CodefreshStarterScripts_CopiesThePowerShellScriptsWithTokens()
    {
        using var repository = new TempRepository().WithStarters();
        repository.Write("codefresh/templates/minimal/scripts/version.ps1", "#!/usr/bin/env pwsh\n# codefresh/apps/<app>/scripts/version.ps1 for <app-repo> on <app-branch>\n");
        var scaffolder = new Scaffolder(repository.Repository);
        var plan = scaffolder.Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest("minimal", [], null));

        var (written, conflicts) = scaffolder.Apply(plan, force: false, "demoapp");

        conflicts.ShouldBeEmpty();
        written.ShouldContain("codefresh/apps/demoapp/scripts/version.ps1");
        repository.Read("codefresh/apps/demoapp/scripts/version.ps1")
            .ShouldBe("#!/usr/bin/env pwsh\n# codefresh/apps/demoapp/scripts/version.ps1 for clearmeasure-aisf-sample-apps/20260924-001 on main\n");
    }

    [Test]
    public void Should_Plan_StarterReadme_IsNotCopied()
    {
        using var repository = new TempRepository().WithStarters();

        var plan = new Scaffolder(repository.Repository).Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest("minimal", [], null));

        plan.Files.ShouldNotContain(file => file.Target.EndsWith("README.md", StringComparison.Ordinal));
    }

    [Test]
    public void Should_Plan_NoDatabase_SkipsDbFolders()
    {
        using var repository = new TempRepository().WithStarters();

        var withoutDatabase = new Scaffolder(repository.Repository).Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest(null, [], "kustomize"));
        var withDatabase = new Scaffolder(repository.Repository).Plan(Load(SampleDescriptors.WithDatabase()), new ScaffoldRequest(null, [], "kustomize"));

        withoutDatabase.Files.ShouldNotContain(file => file.Target.Contains("/db/", StringComparison.Ordinal));
        withDatabase.Files.Count(file => file.Target.Contains("/db/", StringComparison.Ordinal)).ShouldBe(3);
    }

    [Test]
    public void Should_Plan_PartDeployable_UsesPartNamespace()
    {
        using var repository = new TempRepository().WithStarters();
        var yaml = SampleDescriptors.Minimal().Replace("    octopusProject: demoapp", "    part: api\n    octopusProject: demoapp", StringComparison.Ordinal);
        var scaffolder = new Scaffolder(repository.Repository);

        scaffolder.Apply(scaffolder.Plan(Load(yaml), new ScaffoldRequest(null, [], "kustomize")), force: false, "demoapp");

        repository.Read("gitops/apps/demoapp/envs/tdd/app/kustomization.yaml").ShouldContain("namespace: demoapp-api-tdd");
    }

    [Test]
    public void Should_Plan_LayeredOctopusStarters_CopyEachPerProject()
    {
        using var repository = new TempRepository().WithStarters();
        var yaml = SampleDescriptors.Minimal().Replace("    - name: demoapp\n", "    - name: demoapp\n    - name: demoapp-api\n", StringComparison.Ordinal);

        var plan = new Scaffolder(repository.Repository).Plan(Load(yaml), new ScaffoldRequest(null, ["deploy-minimal", "db-runbooks"], null));

        plan.Files.Select(file => file.Target).ShouldBe(
        [
            ".octopus/apps/demoapp/demoapp/deployment_process.ocl",
            ".octopus/apps/demoapp/demoapp/variables.ocl",
            ".octopus/apps/demoapp/demoapp-api/deployment_process.ocl",
            ".octopus/apps/demoapp/demoapp-api/variables.ocl",
            ".octopus/apps/demoapp/demoapp/runbooks/db-restore.ocl",
            ".octopus/apps/demoapp/demoapp-api/runbooks/db-restore.ocl",
        ], ignoreOrder: true);
    }

    [Test]
    public void Should_Apply_ExistingTarget_WritesNothingWithoutForce()
    {
        using var repository = new TempRepository().WithStarters();
        repository.Write("codefresh/apps/demoapp/specs/ci.yml", "owned by the app\n");
        var scaffolder = new Scaffolder(repository.Repository);
        var plan = scaffolder.Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest("minimal", [], null));

        var (written, conflicts) = scaffolder.Apply(plan, force: false, "demoapp");

        written.ShouldBeEmpty();
        conflicts.ShouldHaveSingleItem().Path.ShouldBe("codefresh/apps/demoapp/specs/ci.yml");
        repository.Read("codefresh/apps/demoapp/specs/ci.yml").ShouldBe("owned by the app\n");
        repository.Exists("codefresh/apps/demoapp/specs/release.yml").ShouldBeFalse();
    }

    [Test]
    public void Should_Apply_ExistingTargetWithForce_Overwrites()
    {
        using var repository = new TempRepository().WithStarters();
        repository.Write("codefresh/apps/demoapp/specs/ci.yml", "owned by the app\n");
        var scaffolder = new Scaffolder(repository.Repository);
        var plan = scaffolder.Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest("minimal", [], null));

        var (_, conflicts) = scaffolder.Apply(plan, force: true, "demoapp");

        conflicts.ShouldBeEmpty();
        repository.Read("codefresh/apps/demoapp/specs/ci.yml").ShouldContain("name: demoapp/ci");
    }

    [Test]
    public void Should_Apply_LayeredStartersSharingAFile_AppendsContent()
    {
        using var repository = new TempRepository().WithStarters();
        repository.Write("octopus/templates/db-runbooks/variables.ocl", "variable \"Restore.BackupName\" {\n}\n");
        var scaffolder = new Scaffolder(repository.Repository);
        var plan = scaffolder.Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest(null, ["deploy-minimal", "db-runbooks"], null));

        scaffolder.Apply(plan, force: false, "demoapp");

        var variables = repository.Read(".octopus/apps/demoapp/demoapp/variables.ocl");
        variables.ShouldContain("variable \"App.Name\"");
        variables.ShouldContain("variable \"Restore.BackupName\"");
        variables.IndexOf("App.Name", StringComparison.Ordinal).ShouldBeLessThan(variables.IndexOf("Restore.BackupName", StringComparison.Ordinal));
    }

    [Test]
    public void Should_Plan_GitopsStarterWithoutDbFolder_GeneratesDatabaseFolders()
    {
        using var repository = new TempRepository().WithStarters();
        File.Delete(Path.Combine(repository.Root, "gitops", "templates", "kustomize", "envs", "__ENV__", "db", "kustomization.yaml"));
        var scaffolder = new Scaffolder(repository.Repository);

        scaffolder.Apply(scaffolder.Plan(Load(SampleDescriptors.WithDatabase()), new ScaffoldRequest(null, [], "kustomize")), force: false, "demoapp");

        var db = repository.Read("gitops/apps/demoapp/envs/uat/db/kustomization.yaml");
        db.ShouldContain("namespace: demoapp-uat");
        db.ShouldContain("../../../../../platform/components/db/mssql-2022-express");
        db.ShouldContain("DB_SECRET_STORE=demoapp-uat");
        db.ShouldContain("DB_VOLUME=disk-demoapp-uat-db");
    }

    [TestCase("missing")]
    [TestCase("../gitops")]
    public void Should_Plan_UnknownStarter_ReportsAvailableStarters(string starter)
    {
        using var repository = new TempRepository().WithStarters();

        var plan = new Scaffolder(repository.Repository).Plan(Load(SampleDescriptors.Minimal()), new ScaffoldRequest(starter, [], null));

        plan.Findings.ShouldHaveSingleItem().Message.ShouldContain("available: minimal");
    }

    [Test]
    public void Should_Substitute_LongerTokensFirst_KeepsAppRepoIntact()
    {
        var tokens = new Dictionary<string, string> { ["<app>"] = "x", ["<app-repo>"] = "org/y" };

        var text = Scaffolder.Substitute("<app-repo> <app>", tokens);

        text.ShouldBe("org/y x");
    }
}
