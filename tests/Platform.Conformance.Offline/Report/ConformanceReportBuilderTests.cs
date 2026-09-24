using Platform.Conformance.Harness;
using Platform.Conformance.Report;

namespace Platform.Conformance.Offline.Report;

/// <summary>Proves the per-capability status rules and the failure, inconclusive and stray-result lists.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceReportBuilderTests
{
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenBuild_MixedResults_DerivesEachCapabilityStatus()
    {
        var report = ReportSamples.Report();

        report.Capabilities.Select(capability => $"{capability.Capability.Id}={capability.Status}").ShouldBe(
        [
            "CAP-C-001=Inconclusive",
            "CAP-B-001=Fail",
            "CAP-D-001=NotRun",
            "CAP-A-001=Pass",
            "CAP-E-001=Inconclusive",
        ]);
        report.HasFailures.ShouldBeTrue();
        report.ResultCount.ShouldBe(7);
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenBuild_FailedTest_ListsItWithItsCapabilityAndMessage()
    {
        var report = ReportSamples.Report();

        var failure = report.Failures.ShouldHaveSingleItem();
        failure.Name.ShouldBe("Ns.Live.WhenB_Fails_Always");
        failure.CapabilityIds.ShouldBe(["CAP-B-001"]);
        failure.Message.ShouldNotBeNull().ShouldContain("should be");
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenBuild_InconclusiveAndStrayResults_ListsThemSeparately()
    {
        var report = ReportSamples.Report();

        report.Inconclusive.Select(test => test.Name).ShouldBe(["Ns.Live.WhenC_Lacks_Secret", "Ns.Suite.WhenE_Case_Varies"]);
        report.WithoutCapability.ShouldHaveSingleItem().Name.ShouldBe("Ns.Stray.WhenStray_HasNo_Capability");
        report.Verdicts[TestVerdict.Passed].ShouldBe(4);
        report.Verdicts[TestVerdict.Failed].ShouldBe(1);
        report.Verdicts[TestVerdict.Inconclusive].ShouldBe(2);
        report.RawOutcomes["NotExecuted"].ShouldBe(2);
    }

    [TestCase("Passed,Passed", "Pass")]
    [TestCase("Passed,Failed", "Fail")]
    [TestCase("Inconclusive,Failed", "Fail")]
    [TestCase("Passed,Inconclusive", "Inconclusive")]
    [TestCase("Passed,NotRun", "Inconclusive")]
    [TestCase("NotRun,NotRun", "NotRun")]
    [TestCase("", "NotRun")]
    [Capability("CAP-HARNESS-011")]
    public void WhenStatusOf_TestVerdicts_DerivesTheCapabilityStatus(string verdicts, string expected)
    {
        var parsed = verdicts.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(verdict => verdict == "NotRun" ? (TestVerdict?)null : Enum.Parse<TestVerdict>(verdict))
            .ToArray();

        var status = ConformanceReportBuilder.StatusOf(parsed);

        status.ToString().ShouldBe(expected);
    }
}
