using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-004: one project variable, <c>Platform.ApprovalsRequired</c>, decides whether an app's manual interventions and
/// their guards run. Every manual step of an app process or app runbook, and every guard of one, runs only unless the
/// variable is <c>false</c>, so an app without a variable keeps its approvals and an app that sets <c>false</c> (workorders,
/// lifecycle platform-continuous) deploys to prod with nobody answering anything.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class ApprovalSwitchTests
{
    private const string Switch = "#{if Platform.ApprovalsRequired == \\\"false\\\"}False#{else}True#{/if}";
    private static readonly string[] Guards = ["sod-guard", "uat-signoff-guard"];

    /// <summary>Each manual step and guard of the app processes and runbooks carries the switch in its run condition.</summary>
    [Test]
    [Capability("CAP-OCT-004")]
    public void Should_ReadOcl_AppManualStepsAndGuards_RunOnlyWhenApprovalsAreRequired()
    {
        var files = OctopusRepository.OclFiles(".octopus/apps").Concat(OctopusRepository.OclFiles("octopus/templates")).ToArray();
        var problems = new List<string>();
        var checkedSteps = 0;

        foreach (var file in files)
        {
            foreach (var step in OctopusRepository.Steps(OctopusRepository.Read(file)))
            {
                if (!step.Text.Contains("action_type = \"Octopus.Manual\"", StringComparison.Ordinal) && !Guards.Contains(step.Slug))
                {
                    continue;
                }

                checkedSteps++;
                if (!step.Text.Contains("condition = \"Variable\"", StringComparison.Ordinal) || !step.Text.Contains(Switch, StringComparison.Ordinal))
                {
                    problems.Add($"{file} step {step.Slug}: no Platform.ApprovalsRequired run condition");
                }
            }
        }

        checkedSteps.ShouldBeGreaterThan(0, "no manual step found; the rule checks nothing");
        problems.ShouldBeEmpty();
    }

    /// <summary>workorders turns approvals off and uses the lifecycle whose phases are all automatic.</summary>
    [Test]
    [Capability("CAP-OCT-004")]
    public void Should_ReadWorkorders_ApprovalsOff_UsesTheContinuousLifecycle()
    {
        var variables = OctopusRepository.Read(".octopus/apps/workorders/workorders/variables.ocl");
        var descriptor = OctopusRepository.Read("apps/workorders.yaml");
        var lifecycles = OctopusRepository.Read("octopus/terraform/lifecycles.tf");

        variables.ShouldContain("variable \"Platform.ApprovalsRequired\" {\n    value \"false\" {}\n}");
        descriptor.ShouldContain("lifecycle: platform-continuous");
        var continuous = lifecycles[lifecycles.IndexOf("resource \"octopusdeploy_lifecycle\" \"platform_continuous\"", StringComparison.Ordinal)..];
        continuous = continuous[..continuous.IndexOf("\n}\n", StringComparison.Ordinal)];
        foreach (var env in new[] { "tdd", "uat", "prod" })
        {
            continuous.ShouldContain($"automatic_deployment_targets = [octopusdeploy_environment.this[\"{env}\"].id]");
        }

        continuous.ShouldNotContain("optional_deployment_targets");
    }
}
