using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Report;

namespace Platform.Conformance.Offline.Report;

/// <summary>Proves the Markdown summary and summary.json produced from a report.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class SummaryWriterTests
{
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenWrite_ReportWithAFailure_ShowsResultTotalsCapabilitiesAndTheFailure()
    {
        var markdown = MarkdownSummaryWriter.Write(ReportSamples.Report());

        markdown.ShouldStartWith("# Platform conformance summary");
        markdown.ShouldContain("- Result: **FAILED**: 1 failed test");
        markdown.ShouldContain("- Capabilities: 1 pass, 1 fail, 2 inconclusive, 1 not run (of 5)");
        markdown.ShouldContain("| Passed | 4 |");
        markdown.ShouldContain("| Failed | 1 |");
        markdown.ShouldContain("| Inconclusive | 2 |");
        markdown.ShouldContain("| **Total** | **7** |");
        markdown.ShouldContain("### codefresh");
        markdown.ShouldContain("| CAP-C-001 | INCONCLUSIVE | 0/1 |");
        markdown.ShouldContain("| CAP-D-001 | NOT RUN | 0/1 | 0 ms | nonprod |");
        markdown.ShouldContain("| CAP-A-001 | PASS | 2/2 | 1.0 s | all |");
        markdown.ShouldContain("### `Ns.Live.WhenB_Fails_Always`");
        markdown.ShouldContain("- Capabilities: CAP-B-001");
        markdown.ShouldContain("| `Ns.Live.WhenC_Lacks_Secret` | CAP-C-001 | Prerequisites missing for the Codefresh API");
        markdown.ShouldContain("## Results without a catalogue capability");
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenWrite_VeryLongFailureMessage_TruncatesIt()
    {
        var message = new string('x', 5000);

        var markdown = MarkdownSummaryWriter.Write(ReportSamples.Report(message));

        markdown.ShouldContain(new string('x', MarkdownSummaryWriter.MaxFailureMessageLength - 1) + "…");
        markdown.ShouldNotContain(new string('x', MarkdownSummaryWriter.MaxFailureMessageLength + 1));
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenWriteJson_Report_WritesResultTotalsAndCapabilityStatuses()
    {
        var json = SummaryJsonWriter.Write(ReportSamples.Report());

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("result").GetString().ShouldBe("failed");
        root.GetProperty("totals").GetProperty("results").GetInt32().ShouldBe(7);
        root.GetProperty("totals").GetProperty("failed").GetInt32().ShouldBe(1);
        root.GetProperty("totals").GetProperty("wallClockSeconds").GetDouble().ShouldBe(65);
        root.GetProperty("capabilityTotals").GetProperty("notRun").GetInt32().ShouldBe(1);
        root.GetProperty("capabilities").EnumerateArray().Select(capability => $"{capability.GetProperty("id").GetString()}={capability.GetProperty("status").GetString()}")
            .ShouldBe(["CAP-C-001=inconclusive", "CAP-B-001=fail", "CAP-D-001=notRun", "CAP-A-001=pass", "CAP-E-001=inconclusive"]);
        root.GetProperty("failures")[0].GetProperty("capabilities")[0].GetString().ShouldBe("CAP-B-001");
    }
}
