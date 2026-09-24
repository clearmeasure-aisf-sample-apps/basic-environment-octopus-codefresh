using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Support;
using Platform.Conformance.Report;

namespace Platform.Conformance.Offline.Report;

/// <summary>Proves TRX parsing with System.Xml.Linq.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class TrxReaderTests
{
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenParse_NUnitTrx_ReadsNamesOutcomesDurationsMessagesAndTimes()
    {
        var run = ReportSamples.Run();

        run.Results.Count.ShouldBe(7);
        run.WallClock.ShouldBe(TimeSpan.FromSeconds(65));
        var failed = run.Results.Single(result => result.Outcome == "Failed");
        failed.TestName.ShouldBe("Ns.Live.WhenB_Fails_Always");
        failed.Duration.ShouldBe(TimeSpan.FromSeconds(12));
        failed.Message.ShouldNotBeNull().ShouldContain("but was");
        failed.StackTrace.ShouldBe("at Sample.Method()");
        failed.Verdict.ShouldBe(TestVerdict.Failed);
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenParse_ParameterizedResults_StripsTheArgumentsFromTheTestName()
    {
        var run = ReportSamples.Run();

        var cases = run.Results.Where(result => result.TestName == "Ns.Suite.WhenE_Case_Varies").ToList();

        cases.Select(result => result.DisplayName).ShouldBe(["WhenE_Case_Varies(1)", "WhenE_Case_Varies(2)"]);
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenParse_XmlThatIsNotTrx_ThrowsInvalidData()
    {
        using var stream = TrxSamples.AsStream("<testsuites><testsuite name=\"junit\" /></testsuites>");

        var exception = Should.Throw<InvalidDataException>(() => TrxReader.Parse(stream, "junit.xml"));

        exception.Message.ShouldBe("junit.xml is not a TRX file (the root element is not TestRun).");
    }

    [TestCase("Passed", "Passed")]
    [TestCase("Failed", "Failed")]
    [TestCase("Error", "Failed")]
    [TestCase("Timeout", "Failed")]
    [TestCase("Aborted", "Failed")]
    [TestCase("NotExecuted", "Inconclusive")]
    [TestCase("Inconclusive", "Inconclusive")]
    [TestCase("Pending", "Inconclusive")]
    [Capability("CAP-HARNESS-011")]
    public void WhenToVerdict_RawOutcome_MapsToTheReportVerdict(string outcome, string expected)
    {
        var verdict = TrxReader.ToVerdict(outcome);

        verdict.ToString().ShouldBe(expected);
    }

    [TestCase("Ns.Class", "WhenX_Y_Z", "Ns.Class.WhenX_Y_Z")]
    [TestCase("Ns.Class", "WhenX_Y_Z(1,\"a\")", "Ns.Class.WhenX_Y_Z")]
    [TestCase("Ns.Class", "Ns.Class.WhenX_Y_Z", "Ns.Class.WhenX_Y_Z")]
    [TestCase(null, "Ns.Class.WhenX_Y_Z(2)", "Ns.Class.WhenX_Y_Z")]
    [Capability("CAP-HARNESS-011")]
    public void WhenFullName_ClassAndMethod_BuildsTheCatalogueName(string? className, string methodName, string expected)
    {
        var name = TrxReader.FullName(className, methodName);

        name.ShouldBe(expected);
    }
}
