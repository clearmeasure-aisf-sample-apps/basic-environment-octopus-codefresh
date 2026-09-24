using Platform.Onboarding.Checks;
using Platform.Onboarding.Output;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class BlastRadiusTests
{
    [Test]
    public void Should_Evaluate_OnboardingWithinAppPaths_Passes()
    {
        var changes = BlastRadius.Parse("A\tapps/demoapp.yaml\nA\tcodefresh/apps/demoapp/specs/ci.yml\nA\t.octopus/apps/demoapp/demoapp/deployment_process.ocl\nA\tgitops/apps/demoapp/envs/tdd/app/kustomization.yaml\nA\tcontainers/apps/demoapp/web/Dockerfile\n");

        var findings = BlastRadius.Evaluate(changes);

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Should_Evaluate_OnboardingTouchingPlatformPath_Fails()
    {
        var changes = BlastRadius.Parse("A\tapps/demoapp.yaml\nM\tgitops/platform/tenant/values.yaml\n");

        var findings = BlastRadius.Evaluate(changes);

        var finding = findings.ShouldHaveSingleItem();
        finding.Severity.ShouldBe(Severity.Error);
        finding.Path.ShouldBe("gitops/platform/tenant/values.yaml");
    }

    [Test]
    public void Should_Evaluate_OnboardingTouchingAnotherApp_Fails()
    {
        var changes = BlastRadius.Parse("A\tapps/demoapp.yaml\nM\tgitops/apps/otherapp/envs/tdd/app/kustomization.yaml\n");

        var findings = BlastRadius.Evaluate(changes);

        findings.ShouldHaveSingleItem().Message.ShouldContain("touches app 'otherapp'");
    }

    [Test]
    public void Should_Evaluate_SchemaChangeWithOnboarding_Fails()
    {
        var changes = BlastRadius.Parse("A\tapps/demoapp.yaml\nM\tapps/schema.json\n");

        var findings = BlastRadius.Evaluate(changes);

        findings.ShouldHaveSingleItem().Path.ShouldBe("apps/schema.json");
    }

    [Test]
    public void Should_Evaluate_RetirementDeletingAppPaths_Passes()
    {
        var changes = BlastRadius.Parse("D\tapps/demoapp.yaml\nD\tgitops/apps/demoapp/envs/tdd/app/kustomization.yaml\n");

        var findings = BlastRadius.Evaluate(changes);

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Should_Evaluate_PlatformChangeWithoutDescriptor_Passes()
    {
        var changes = BlastRadius.Parse("M\tgitops/platform/tenant/values.yaml\nM\tscripts/checks/consistency.sh\n");

        var findings = BlastRadius.Evaluate(changes);

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Should_Evaluate_AppChangeMixedWithPlatform_WarnsOnly()
    {
        var changes = BlastRadius.Parse("M\tgitops/apps/demoapp/envs/tdd/app/kustomization.yaml\nM\tREADME.md\n");

        var findings = BlastRadius.Evaluate(changes);

        findings.ShouldHaveSingleItem().Severity.ShouldBe(Severity.Warning);
    }

    [Test]
    public void Should_Parse_RenameAndBarePath_KeepsBothPathsOfARename()
    {
        var changes = BlastRadius.Parse("R100\tgitops/apps/a1b/x.yaml\tgitops/apps/a1b/y.yaml\nREADME.md\n# comment\n");

        changes.Count.ShouldBe(2);
        changes[0].OldPath.ShouldBe("gitops/apps/a1b/x.yaml");
        changes[1].Status.ShouldBe('M');
    }
}
