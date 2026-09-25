using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-AZ-012 (offline half): each tier's runbooks act only on their own tier and layer. The guard steps of the
/// platform-infrastructure runbooks accept a run only when Environment.Class matches the infrastructure environment,
/// infra-prod only from refs/heads/main, and App.Name only as an app slug that is no reserved word (ADR-IR34 decision
/// 10); the plan checks stop an apply whose plan touches role assignments, locks or resource groups, which the provisioner
/// owns (decision 3). The inline PowerShell of each step runs under the stub Octopus runtime.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class RunbookGuardTests
{
    private const string Runbooks = ".octopus/platform-infrastructure/runbooks";

    private const string CleanPlan = """

        Terraform will perform the following actions:

          # azurerm_kubernetes_cluster.this will be updated in-place
          ~ resource "azurerm_kubernetes_cluster" "this" {
            }

          # data.azurerm_role_assignment.lookup will be read during apply

        Plan: 0 to add, 1 to change, 0 to destroy.
        """;

    /// <summary>Guard app of apps-plan, apps-apply and rotate-db-passwords: the prompted App.Name and the run source.</summary>
    [TestCase("apps-plan", "sandbox", "infra-nonprod", "nonprod", "refs/heads/main", null, null)]
    [TestCase("apps-apply", "abcdefghijkl", "infra-prod", "prod", "refs/heads/main", null, null)]
    [TestCase("apps-plan", "ab", "infra-nonprod", "nonprod", "refs/heads/main", "App.Name 'ab' is not an app slug (^[a-z][a-z0-9]{2,11}$).", null)]
    [TestCase("apps-apply", "app-1", "infra-nonprod", "nonprod", "refs/heads/main", "App.Name 'app-1' is not an app slug (^[a-z][a-z0-9]{2,11}$).", null)]
    [TestCase("apps-plan", "Sandbox", "infra-nonprod", "nonprod", "refs/heads/main", "App.Name 'Sandbox' is not an app slug (^[a-z][a-z0-9]{2,11}$).", null)]
    [TestCase("apps-plan", "kyverno", "infra-nonprod", "nonprod", "refs/heads/main", "App.Name 'kyverno' is a reserved word (ADR-IR34 decision 10).", null)]
    [TestCase("apps-apply", "sandbox", "infra-prod", "nonprod", "refs/heads/main", "Environment.Class 'nonprod' does not match environment 'infra-prod' (library variable set Platform Infrastructure).", null)]
    [TestCase("apps-apply", "sandbox", "infra-prod", "prod", "refs/heads/feature", "infra-prod runs only from refs/heads/main (this run: 'refs/heads/feature').", null)]
    [TestCase("apps-plan", "sandbox", "infra-nonprod", "nonprod", "refs/heads/feature", null, "Running unmerged configuration from 'refs/heads/feature' in infra-nonprod.")]
    [TestCase("rotate-db-passwords", "sandbox", "infra-nonprod", "nonprod", "refs/heads/main", null, null)]
    [TestCase("rotate-db-passwords", "sandbox", "infra-prod", "prod", "refs/heads/feature", "infra-prod runs only from refs/heads/main (this run: 'refs/heads/feature').", null)]
    [Capability("CAP-AZ-012")]
    public void Should_GuardApp_AppAndRunSource_AcceptOnlyTheirOwnTier(string runbook, string app, string environment, string tier, string gitRef, string? failure, string? warning)
    {
        var run = RunbookScript.Of($"{Runbooks}/{runbook}.ocl", "guard-app")
            .With("App.Name", app).With("Octopus.Environment.Name", environment).With("Environment.Class", tier).With("Octopus.RunbookRun.Git.Ref", gitRef)
            .Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Succeeded.ShouldBe(failure is null, run.Transcript);
        run.Calls.ShouldBeEmpty(run.Transcript);
        if (failure is null)
        {
            run.Highlights.ShouldBe([$"App {app}, tier {tier}, from {gitRef} (state apps-{app}.tfstate)."], run.Transcript);
        }

        run.Warnings.ShouldBe(warning is null ? [] : [warning], run.Transcript);
    }

    /// <summary>Guard run source of env-apply: the environment's own tier, and infra-prod only from refs/heads/main.</summary>
    [TestCase("infra-nonprod", "nonprod", "refs/heads/main", null, null)]
    [TestCase("infra-prod", "prod", "refs/heads/main", null, null)]
    [TestCase("infra-nonprod", "nonprod", "refs/heads/feature", null, "Applying unmerged Terraform from 'refs/heads/feature' to infra-nonprod.")]
    [TestCase("infra-prod", "prod", "refs/tags/v1", "infra-prod applies only from refs/heads/main (this run: 'refs/tags/v1').", null)]
    [TestCase("infra-prod", "nonprod", "refs/heads/main", "Environment.Class 'nonprod' does not match environment 'infra-prod' (library variable set Platform Infrastructure).", null)]
    [Capability("CAP-AZ-012")]
    public void Should_GuardRunSource_EnvApply_AppliesOnlyItsOwnTier(string environment, string tier, string gitRef, string? failure, string? warning)
    {
        var run = RunbookScript.Of($"{Runbooks}/env-apply.ocl", "guard-run-source")
            .With("Octopus.Environment.Name", environment).With("Environment.Class", tier).With("Octopus.RunbookRun.Git.Ref", gitRef)
            .Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Succeeded.ShouldBe(failure is null, run.Transcript);
        run.Warnings.ShouldBe(warning is null ? [] : [warning], run.Transcript);
        if (failure is null)
        {
            run.Log.ShouldBe([$"Run source OK: {environment} (tier {tier}) from {gitRef}."], run.Transcript);
        }
    }

    /// <summary>Save and check plan: the plan is attached, and a plan that touches the provisioner's resources fails.</summary>
    [TestCase("apps-plan", "Plan app", "terraform-apps-plan-nonprod.txt", "clean", null)]
    [TestCase("apps-apply", "Plan app", "terraform-apps-plan-nonprod.txt", "  # azurerm_role_assignment.aks will be created\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("apps-apply", "Plan app", "terraform-apps-plan-nonprod.txt", "\t# module.core.azurerm_management_lock.rg will be destroyed\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("apps-plan", "Plan app", "terraform-apps-plan-nonprod.txt", "x\r\n  # azurerm_resource_group.apps must be replaced\r\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("apps-plan", "Plan app", "terraform-apps-plan-nonprod.txt", "", "Plan app produced no plan output.")]
    [TestCase("env-plan", "Plan environment", "terraform-plan-nonprod.txt", "clean", null)]
    [TestCase("env-apply", "Plan environment", "terraform-plan-nonprod.txt", "  # azurerm_resource_group.nodes will be created\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("env-plan", "Plan environment", "terraform-plan-nonprod.txt", "  # module.aks.azurerm_role_assignment.kubelet will be created\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("env-plan", "Plan environment", "terraform-plan-nonprod.txt", "  # module.core.data.azurerm_resource_group.shared will be read during apply\n", null)]
    [TestCase("apps-plan", "Plan app", "terraform-apps-plan-nonprod.txt", "  # module.apps[\"sandbox\"].azurerm_role_assignment.pull will be created\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [TestCase("env-apply", "Plan environment", "terraform-plan-nonprod.txt", "  # module.tier.module.aks.azurerm_management_lock.cluster will be created\n", "The plan touches role assignments, locks or resource groups; the provisioner owns them (ADR-IR34 decision 3).")]
    [Capability("CAP-AZ-012")]
    public void Should_SaveAndCheckPlan_ProvisionerResources_FailThePlan(string runbook, string planStep, string planFile, string plan, string? failure)
    {
        var run = RunbookScript.Of($"{Runbooks}/{runbook}.ocl", "save-and-check-plan")
            .With("Environment.Class", "nonprod")
            .With($"Octopus.Action[{planStep}].Output.TerraformPlanOutput", plan == "clean" ? CleanPlan : plan)
            .Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Succeeded.ShouldBe(failure is null, run.Transcript);
        if (plan.Length > 0)
        {
            run.Events.ShouldContain($"ARTIFACT {planFile}", run.Transcript);
        }

        if (failure is null)
        {
            run.Log.ShouldBe([$"Plan saved as artifact {planFile}; the layer boundary holds."], run.Transcript);
        }
    }
}
