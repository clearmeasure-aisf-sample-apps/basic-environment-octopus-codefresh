using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-AZ-007 (offline half, with Platform.Conformance.Offline.Azure.TierRebuildTests): prod has no destroy runbook. Beside
/// the environment scope of every destroying action, the scripts of env-destroy refuse anything but infra-nonprod with
/// Environment.Class nonprod, and stop a destroy plan that touches role assignments, locks or resource groups. The inline
/// PowerShell runs under the stub Octopus runtime.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class EnvDestroyGuardTests
{
    private const string Runbook = ".octopus/platform-infrastructure/runbooks/env-destroy.ocl";

    /// <summary>Guard nonprod only: only infra-nonprod with Environment.Class nonprod passes; another ref only warns.</summary>
    [TestCase("infra-nonprod", "nonprod", "refs/heads/main", null, null)]
    [TestCase("infra-nonprod", "nonprod", "refs/heads/wip", null, "Destroying with Terraform from 'refs/heads/wip', not refs/heads/main.")]
    [TestCase("infra-prod", "prod", "refs/heads/main", "env-destroy runs only in infra-nonprod with Environment.Class nonprod.", null)]
    [TestCase("infra-nonprod", "prod", "refs/heads/main", "env-destroy runs only in infra-nonprod with Environment.Class nonprod.", null)]
    [Capability("CAP-AZ-007")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_GuardNonprodOnly_EnvDestroy_RefusesEverythingButNonprod(string environment, string tier, string gitRef, string? failure, string? warning)
    {
        var run = RunbookScript.Of(Runbook, "guard-nonprod-only")
            .With("Octopus.Environment.Name", environment).With("Environment.Class", tier).With("Octopus.RunbookRun.Git.Ref", gitRef)
            .Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Succeeded.ShouldBe(failure is null, run.Transcript);
        run.Warnings.ShouldBe(warning is null ? [] : [warning], run.Transcript);
        run.Calls.ShouldBeEmpty(run.Transcript);
        if (failure is null)
        {
            run.Log.ShouldBe([$"Guard passed for {environment}."], run.Transcript);
        }
    }

    /// <summary>Check destroy scope: the destroy plan is attached, and one that touches the provisioner's resources fails.</summary>
    [TestCase("  # azurerm_kubernetes_cluster.this will be destroyed\n", null)]
    [TestCase("  # azurerm_management_lock.cluster will be destroyed\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("", "Plan environment destroy produced no plan output.")]
    [Capability("CAP-AZ-007")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_CheckDestroyScope_ProvisionerResources_FailTheDestroy(string plan, string? failure)
    {
        var run = RunbookScript.Of(Runbook, "check-destroy-scope")
            .With("Environment.Class", "nonprod")
            .With("Octopus.Action[Plan environment destroy].Output.TerraformPlanOutput", plan)
            .Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Succeeded.ShouldBe(failure is null, run.Transcript);
        if (plan.Length > 0)
        {
            run.Events.ShouldContain("ARTIFACT terraform-destroy-plan-nonprod.txt", run.Transcript);
        }
    }
}
