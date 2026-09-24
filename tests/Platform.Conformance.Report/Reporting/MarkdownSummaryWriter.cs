using System.Globalization;
using System.Text;
using Platform.Conformance.Harness.Catalogue;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Report;

/// <summary>Writes a <see cref="ConformanceReport"/> as a Markdown summary.</summary>
internal static class MarkdownSummaryWriter
{
    /// <summary>Longest failure message shown.</summary>
    public const int MaxFailureMessageLength = 1500;

    /// <summary>Renders the summary.</summary>
    /// <param name="report">The report.</param>
    public static string Write(ConformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {report.Title}");
        text.AppendLine();
        var verdict = report.HasFailures
            ? $"**FAILED**: {report.Failures.Count} failed test{Plural(report.Failures.Count)}"
            : "**PASSED**: no failed tests";
        text.AppendLine(CultureInfo.InvariantCulture, $"- Result: {verdict}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Generated: {report.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC from {report.TrxFiles.Count} TRX file{Plural(report.TrxFiles.Count)}: {string.Join(", ", report.TrxFiles.Select(file => MarkdownText.Code(Path.GetFileName(file))))}");
        var wallClock = report.WallClock is { } clock ? $"; wall clock {DurationFormat.Human(clock)}" : "";
        text.AppendLine(CultureInfo.InvariantCulture, $"- Duration: {DurationFormat.Human(report.TestDuration)} of test time{wallClock}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Capabilities: {report.CountOf(CapabilityStatus.Pass)} pass, {report.CountOf(CapabilityStatus.Fail)} fail, {report.CountOf(CapabilityStatus.Inconclusive)} inconclusive, {report.CountOf(CapabilityStatus.NotRun)} not run (of {report.Capabilities.Count})");
        text.AppendLine();

        WriteTotals(text, report);
        WriteCapabilities(text, report);
        WriteFailures(text, report);
        WriteInconclusive(text, report);
        WriteSlowest(text, report);
        WriteWithoutCapability(text, report);
        return text.ToString();
    }

    /// <summary>Label of a capability status.</summary>
    /// <param name="status">The status.</param>
    public static string Label(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Pass => "PASS",
        CapabilityStatus.Fail => "FAIL",
        CapabilityStatus.Inconclusive => "INCONCLUSIVE",
        _ => "NOT RUN",
    };

    private static void WriteTotals(StringBuilder text, ConformanceReport report)
    {
        text.AppendLine("## Totals by outcome");
        text.AppendLine();
        text.AppendLine("| Outcome | Results |");
        text.AppendLine("|---|---:|");
        foreach (var (verdict, count) in report.Verdicts)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {verdict} | {count} |");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"| **Total** | **{report.ResultCount}** |");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Raw TRX outcomes: {(report.RawOutcomes.Count == 0 ? "none" : string.Join(", ", report.RawOutcomes.Select(pair => $"{pair.Key} {pair.Value}")))}. NUnit reports Inconclusive as NotExecuted in TRX.");
        text.AppendLine();
    }

    private static void WriteCapabilities(StringBuilder text, ConformanceReport report)
    {
        text.AppendLine("## Capabilities");
        text.AppendLine();
        text.AppendLine("A capability passes only when all its tests passed; it fails when any failed; otherwise it is inconclusive, or not run when none of its tests ran.");
        text.AppendLine();
        foreach (var group in report.Capabilities.GroupBy(summary => summary.Capability.Owner))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {group.Key.ToYaml()}");
            text.AppendLine();
            text.AppendLine("| Capability | Status | Passed | Duration | Tier | Statement |");
            text.AppendLine("|---|---|---:|---:|---|---|");
            foreach (var capability in group)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {capability.Capability.Id} | {Label(capability.Status)} | {capability.Passed}/{capability.Tests.Count} | {DurationFormat.Human(capability.Duration)} | {capability.Capability.Tier.ToYaml()} | {MarkdownText.Cell(capability.Capability.Statement)} |");
            }

            text.AppendLine();
        }
    }

    private static void WriteFailures(StringBuilder text, ConformanceReport report)
    {
        text.AppendLine("## Failures");
        text.AppendLine();
        if (report.Failures.Count == 0)
        {
            text.AppendLine("None.");
            text.AppendLine();
            return;
        }

        foreach (var failure in report.Failures)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {MarkdownText.Code(failure.Name)}");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"- Capabilities: {(failure.CapabilityIds.Count == 0 ? "none" : string.Join(", ", failure.CapabilityIds))}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Outcome: {string.Join(", ", failure.Outcomes)} ({failure.Results} result{Plural(failure.Results)}, {DurationFormat.Human(failure.Duration)})");
            text.AppendLine();
            text.AppendLine("```text");
            text.AppendLine(MarkdownText.Block(failure.Message ?? "(no message)", MaxFailureMessageLength));
            text.AppendLine("```");
            text.AppendLine();
        }
    }

    private static void WriteInconclusive(StringBuilder text, ConformanceReport report)
    {
        if (report.Inconclusive.Count == 0)
        {
            return;
        }

        text.AppendLine("## Inconclusive and not executed");
        text.AppendLine();
        text.AppendLine("| Test | Capabilities | Reason |");
        text.AppendLine("|---|---|---|");
        foreach (var test in report.Inconclusive)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {MarkdownText.Code(test.Name)} | {string.Join(", ", test.CapabilityIds)} | {MarkdownText.Cell(test.Message ?? string.Join(", ", test.Outcomes))} |");
        }

        text.AppendLine();
    }

    private static void WriteSlowest(StringBuilder text, ConformanceReport report)
    {
        if (report.Slowest.Count == 0)
        {
            return;
        }

        text.AppendLine("## Slowest tests");
        text.AppendLine();
        text.AppendLine("| Test | Verdict | Duration |");
        text.AppendLine("|---|---|---:|");
        foreach (var test in report.Slowest)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {MarkdownText.Code(test.Name)} | {test.Verdict} | {DurationFormat.Human(test.Duration)} |");
        }

        text.AppendLine();
    }

    private static void WriteWithoutCapability(StringBuilder text, ConformanceReport report)
    {
        if (report.WithoutCapability.Count == 0)
        {
            return;
        }

        text.AppendLine("## Results without a catalogue capability");
        text.AppendLine();
        text.AppendLine("These results map to no capability in the catalogue; `CatalogueConsistencyTests` should have failed.");
        text.AppendLine();
        foreach (var test in report.WithoutCapability)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {MarkdownText.Code(test.Name)} ({test.Verdict})");
        }

        text.AppendLine();
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}
