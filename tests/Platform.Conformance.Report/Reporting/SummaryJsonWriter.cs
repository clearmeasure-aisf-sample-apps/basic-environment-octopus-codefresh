using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Report;

/// <summary>Writes a <see cref="ConformanceReport"/> as <c>summary.json</c> for pipelines and dashboards.</summary>
internal static class SummaryJsonWriter
{
    /// <summary>Longest message kept per test.</summary>
    public const int MaxMessageLength = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes the report.</summary>
    /// <param name="report">The report.</param>
    public static string Write(ConformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var document = new
        {
            schema = 1,
            title = report.Title,
            generatedAt = report.GeneratedAt,
            result = report.HasFailures ? "failed" : "passed",
            trxFiles = report.TrxFiles,
            totals = new
            {
                results = report.ResultCount,
                passed = report.Verdicts.GetValueOrDefault(TestVerdict.Passed),
                failed = report.Verdicts.GetValueOrDefault(TestVerdict.Failed),
                inconclusive = report.Verdicts.GetValueOrDefault(TestVerdict.Inconclusive),
                rawOutcomes = report.RawOutcomes,
                testDurationSeconds = Math.Round(report.TestDuration.TotalSeconds, 3),
                wallClockSeconds = report.WallClock is { } clock ? Math.Round(clock.TotalSeconds, 3) : (double?)null,
            },
            capabilityTotals = new
            {
                pass = report.CountOf(CapabilityStatus.Pass),
                fail = report.CountOf(CapabilityStatus.Fail),
                inconclusive = report.CountOf(CapabilityStatus.Inconclusive),
                notRun = report.CountOf(CapabilityStatus.NotRun),
            },
            capabilities = report.Capabilities.Select(capability => new
            {
                id = capability.Capability.Id,
                owner = capability.Capability.Owner.ToYaml(),
                tier = capability.Capability.Tier.ToYaml(),
                live = capability.Capability.Live,
                destructive = capability.Capability.Destructive,
                statement = capability.Capability.Statement,
                status = StatusName(capability.Status),
                testsPassed = capability.Passed,
                testsTotal = capability.Tests.Count,
                durationSeconds = Math.Round(capability.Duration.TotalSeconds, 3),
                tests = capability.Tests.Select(test => new
                {
                    name = test.Name,
                    verdict = test.Verdict?.ToString() ?? "NotRun",
                    durationSeconds = Math.Round(test.Duration.TotalSeconds, 3),
                    message = test.Message is null ? null : MarkdownText.Truncate(test.Message, MaxMessageLength),
                }),
            }),
            failures = report.Failures.Select(Describe),
            inconclusive = report.Inconclusive.Select(Describe),
            withoutCapability = report.WithoutCapability.Select(test => test.Name),
        };
        return JsonSerializer.Serialize(document, Json);
    }

    private static object Describe(TestSummary test) => new
    {
        name = test.Name,
        capabilities = test.CapabilityIds,
        outcomes = test.Outcomes,
        results = test.Results,
        durationSeconds = Math.Round(test.Duration.TotalSeconds, 3),
        message = test.Message is null ? null : MarkdownText.Truncate(test.Message, MaxMessageLength),
    };

    private static string StatusName(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Pass => "pass",
        CapabilityStatus.Fail => "fail",
        CapabilityStatus.Inconclusive => "inconclusive",
        _ => "notRun",
    };
}
