using Platform.Conformance.Harness;
using Platform.Conformance.Tests.Azure;

namespace Platform.Conformance.Offline.Azure;

/// <summary>CAP-AZ-006 (offline half): the Terraform plan summary is read from a task log with Terraform's colour codes.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class RunbookLogsTests
{
    [Test]
    [Capability("CAP-AZ-006")]
    public void Should_ReadPlanSummary_ColouredNoChanges_FindsNoChanges()
    {
        const string log = "05:03:34   Info     |       \u001b[0m\u001b[1m\u001b[32mNo changes.\u001b[0m\u001b[1m Your infrastructure matches the configuration.\u001b[0m";

        var summary = RunbookLogs.PlanSummary(log);

        summary.ShouldNotBeNull();
        summary.Add.ShouldBe(0);
        summary.Destroy.ShouldBe(0);
    }

    [Test]
    [Capability("CAP-AZ-006")]
    public void Should_ReadPlanSummary_ColouredPlanLine_ReadsTheCounts()
    {
        const string log = "\u001b[1mPlan:\u001b[0m 1 to add, 2 to change, 0 to destroy.";

        var summary = RunbookLogs.PlanSummary(log);

        summary.ShouldNotBeNull();
        summary.Add.ShouldBe(1);
        summary.Change.ShouldBe(2);
    }
}
