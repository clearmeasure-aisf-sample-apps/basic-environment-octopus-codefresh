using Platform.Onboarding.Cli;
using Platform.Onboarding.Tests.Support;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class OnboardingCliTests
{
    private const string Repo = "clearmeasure-aisf-sample-apps/20260924-001";

    [Test]
    public void Should_Run_NewScaffoldRenderCheck_OnboardsAValidApp()
    {
        using var repository = new TempRepository().WithStarters();

        var created = repository.Run(null, "new", "demoapp", "--repo", Repo, "--database", "mssql-2022-express");
        var scaffolded = repository.Run(null, "scaffold", "demoapp", "--codefresh", "minimal", "--octopus", "deploy-minimal,db-runbooks", "--gitops", "kustomize");
        var rendered = repository.Run(null, "render", "demoapp", "--subscription-id", "00000000-0000-0000-0000-000000000000");
        var checkedResult = repository.Run(null, "check", "demoapp");

        created.ExitCode.ShouldBe(OnboardingCli.Success, created.Error);
        scaffolded.ExitCode.ShouldBe(OnboardingCli.Success, scaffolded.Output);
        rendered.ExitCode.ShouldBe(OnboardingCli.Success, rendered.Error);
        rendered.Output.ShouldContain("vault: kv-demoapp-t-");
        rendered.Output.ShouldContain("disk: disk-demoapp-prod-db");
        checkedResult.ExitCode.ShouldBe(OnboardingCli.Success, checkedResult.Output);
        checkedResult.Output.ShouldContain("0 error(s)");
    }

    [Test]
    public void Should_Run_NewForFixtureName_IsAUsageError()
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, "new", "sandbox", "--repo", Repo);

        result.ExitCode.ShouldBe(OnboardingCli.Usage);
        result.Error.ShouldContain("reserved for the conformance fixture");
        repository.Exists("apps/sandbox.yaml").ShouldBeFalse();
    }

    [Test]
    public void Should_Run_NewWithUpstreamRepository_IsAUsageError()
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, "new", "demoapp", "--repo", "ClearMeasureLabs/bootcamp-palermo-workorders");

        result.ExitCode.ShouldBe(OnboardingCli.Usage);
        result.Error.ShouldContain("clearmeasure-aisf-sample-apps");
    }

    [Test]
    public void Should_Run_NewTwice_RefusesToOverwrite()
    {
        using var repository = new TempRepository();
        repository.Run(null, "new", "demoapp", "--repo", Repo);

        var second = repository.Run(null, "new", "demoapp", "--repo", Repo);

        second.ExitCode.ShouldBe(OnboardingCli.Failed);
        second.Error.ShouldContain("already exists");
    }

    [Test]
    public void Should_Run_NewHelm_WritesAValidHelmDescriptor()
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, "new", "demoapp", "--repo", Repo, "--packaging", "helm", "--images", "web,worker");
        var check = repository.Run(null, "check", "--scope", "descriptors");

        result.ExitCode.ShouldBe(OnboardingCli.Success, result.Error);
        repository.Read("apps/demoapp.yaml").ShouldContain("{{ .Values.worker.repository }}:{{ .Values.worker.tag }}");
        check.ExitCode.ShouldBe(OnboardingCli.Success, check.Output);
    }

    [Test]
    public void Should_Run_CheckInvalidDescriptor_ExitsOneWithLocation()
    {
        using var repository = new TempRepository();
        repository.Write("apps/demoapp.yaml", SampleDescriptors.Minimal() + "\ntypo: 1\n");

        var result = repository.Run(null, "check", "--scope", "descriptors");

        result.ExitCode.ShouldBe(OnboardingCli.Failed);
        result.Output.ShouldContain("apps/demoapp.yaml:16: unknown key 'typo'");
    }

    [Test]
    public void Should_Run_CheckWithChangesFile_AppliesBlastRadius()
    {
        using var repository = new TempRepository();
        repository.Write("apps/demoapp.yaml", SampleDescriptors.Minimal());
        var changes = repository.Write("changes.txt", "A\tapps/demoapp.yaml\nM\targocd/clusters/nonprod/appset-apps.yaml\n");

        var result = repository.Run(null, "check", "--scope", "descriptors", "--changes", changes);

        result.ExitCode.ShouldBe(OnboardingCli.Failed);
        result.Output.ShouldContain("blast-radius");
    }

    [Test]
    public void Should_Run_CheckUnknownApp_IsAUsageError()
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, "check", "nosuchapp");

        result.ExitCode.ShouldBe(OnboardingCli.Usage);
    }

    [Test]
    public void Should_Run_CheckLiveWithoutOctopusVariables_IsAUsageError()
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, "check", "--live");

        result.ExitCode.ShouldBe(OnboardingCli.Usage);
        result.Error.ShouldContain("OCTOPUS_API_KEY");
    }

    [Test]
    public void Should_Run_List_PrintsOneRowPerValidDescriptor()
    {
        using var repository = new TempRepository();
        repository.Write("apps/alpha.yaml", SampleDescriptors.Minimal("alpha", "clearmeasure-aisf-sample-apps/20260924-002"));
        repository.Write("apps/beta.yaml", SampleDescriptors.Minimal("beta", "clearmeasure-aisf-sample-apps/20260924-003"));

        var result = repository.Run(null, "list");

        result.ExitCode.ShouldBe(OnboardingCli.Success);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
    }

    [Test]
    public void Should_Run_RetireFreezeThenDelete_ParksThenRemovesAppPaths()
    {
        using var repository = new TempRepository().WithStarters();
        repository.Run(null, "new", "demoapp", "--repo", Repo);
        repository.Run(null, "scaffold", "demoapp", "--codefresh", "minimal", "--gitops", "kustomize");

        var deleteActive = repository.Run(null, "retire", "demoapp", "--delete");
        var freeze = repository.Run(null, "retire", "demoapp", "--freeze");
        var frozenText = repository.Read("apps/demoapp.yaml");
        var delete = repository.Run(null, "retire", "demoapp", "--delete");

        deleteActive.ExitCode.ShouldBe(OnboardingCli.Failed);
        freeze.ExitCode.ShouldBe(OnboardingCli.Success);
        frozenText.ShouldContain("status: frozen");
        delete.ExitCode.ShouldBe(OnboardingCli.Success);
        repository.Exists("apps/demoapp.yaml").ShouldBeFalse();
        repository.Exists("gitops/apps/demoapp").ShouldBeFalse();
        repository.Exists("codefresh/apps/demoapp").ShouldBeFalse();
        delete.Output.ShouldContain("Teardown of demoapp");
    }

    [Test]
    public void Should_Run_RetireFixture_IsAUsageError()
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, "retire", "sandbox", "--freeze");

        result.ExitCode.ShouldBe(OnboardingCli.Usage);
    }

    [TestCase("frobnicate")]
    [TestCase("check", "--bogus")]
    [TestCase("render", "--format", "xml")]
    [TestCase("scaffold")]
    public void Should_Run_BadCommandLine_ExitsTwo(params string[] args)
    {
        using var repository = new TempRepository();

        var result = repository.Run(null, args);

        result.ExitCode.ShouldBe(OnboardingCli.Usage);
    }

    [Test]
    public void Should_Run_WithoutRepository_IsAUsageError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "onboarding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = new OnboardingCli(output, error, new StubEnvironment(), directory).Run(["list"]);

        code.ShouldBe(OnboardingCli.Usage);
        error.ToString().ShouldContain("repository root not found");
        Directory.Delete(directory);
    }
}
