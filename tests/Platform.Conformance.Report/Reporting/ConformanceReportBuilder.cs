using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Report;

/// <summary>Derives a <see cref="ConformanceReport"/> from TRX runs, the catalogue and the discovered tests.</summary>
internal static class ConformanceReportBuilder
{
    /// <summary>Number of tests in the slowest-tests table.</summary>
    public const int SlowestCount = 10;

    /// <summary>Builds the report.</summary>
    /// <param name="title">Report title.</param>
    /// <param name="runs">Parsed TRX files.</param>
    /// <param name="catalogue">The merged catalogue.</param>
    /// <param name="tests">Tests with their capability IDs (from the assemblies or a capability map).</param>
    /// <param name="generatedAt">Timestamp written into the report.</param>
    public static ConformanceReport Build(string title, IReadOnlyList<TrxRun> runs, CapabilityCatalogue catalogue, IReadOnlyList<DiscoveredTest> tests, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(tests);
        var capabilitiesByTest = CapabilitiesByTest(catalogue, tests);
        var results = runs.SelectMany(run => run.Results).ToList();
        var summaries = results
            .GroupBy(result => result.TestName, StringComparer.Ordinal)
            .Select(group => Summarize(group.Key, group.ToList(), capabilitiesByTest))
            .ToDictionary(summary => summary.Name, StringComparer.Ordinal);
        var capabilities = catalogue.Capabilities
            .OrderBy(capability => capability.Owner)
            .ThenBy(capability => capability.Id, StringComparer.Ordinal)
            .Select(capability => SummarizeCapability(capability, tests, summaries))
            .ToArray();
        var known = catalogue.Capabilities.Select(capability => capability.Id).ToHashSet(StringComparer.Ordinal);
        return new ConformanceReport
        {
            Title = title,
            GeneratedAt = generatedAt,
            TrxFiles = runs.Select(run => run.Path).ToArray(),
            ResultCount = results.Count,
            Verdicts = Enum.GetValues<TestVerdict>().ToDictionary(verdict => verdict, verdict => results.Count(result => result.Verdict == verdict)),
            RawOutcomes = results.GroupBy(result => result.Outcome, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            TestDuration = results.Aggregate(TimeSpan.Zero, (total, result) => total + result.Duration),
            WallClock = runs.All(run => run.WallClock is not null) && runs.Count > 0 ? runs.Aggregate(TimeSpan.Zero, (total, run) => total + run.WallClock!.Value) : null,
            Capabilities = capabilities,
            Failures = summaries.Values.Where(summary => summary.Verdict == TestVerdict.Failed).OrderBy(summary => summary.Name, StringComparer.Ordinal).ToArray(),
            Inconclusive = summaries.Values.Where(summary => summary.Verdict == TestVerdict.Inconclusive).OrderBy(summary => summary.Name, StringComparer.Ordinal).ToArray(),
            Slowest = summaries.Values.OrderByDescending(summary => summary.Duration).ThenBy(summary => summary.Name, StringComparer.Ordinal).Take(SlowestCount).ToArray(),
            WithoutCapability = summaries.Values.Where(summary => !summary.CapabilityIds.Any(known.Contains)).OrderBy(summary => summary.Name, StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>Derives a capability status from the verdicts of its tests (<c>null</c> = did not run).</summary>
    /// <param name="verdicts">One verdict per test of the capability.</param>
    public static CapabilityStatus StatusOf(IReadOnlyCollection<TestVerdict?> verdicts)
    {
        if (verdicts.Any(verdict => verdict == TestVerdict.Failed))
        {
            return CapabilityStatus.Fail;
        }

        if (verdicts.Count > 0 && verdicts.All(verdict => verdict == TestVerdict.Passed))
        {
            return CapabilityStatus.Pass;
        }

        return verdicts.All(verdict => verdict is null) ? CapabilityStatus.NotRun : CapabilityStatus.Inconclusive;
    }

    private static Dictionary<string, SortedSet<string>> CapabilitiesByTest(CapabilityCatalogue catalogue, IReadOnlyList<DiscoveredTest> tests)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string testName, string capabilityId)
        {
            if (!map.TryGetValue(testName, out var ids))
            {
                ids = new SortedSet<string>(StringComparer.Ordinal);
                map[testName] = ids;
            }

            ids.Add(capabilityId);
        }

        foreach (var test in tests)
        {
            foreach (var id in test.CapabilityIds)
            {
                Add(test.FullName, id);
            }
        }

        foreach (var capability in catalogue.Capabilities)
        {
            foreach (var test in capability.Tests)
            {
                Add(test, capability.Id);
            }
        }

        return map;
    }

    private static TestSummary Summarize(string name, List<TrxTestResult> results, Dictionary<string, SortedSet<string>> capabilitiesByTest)
    {
        var verdict = results.Any(result => result.Verdict == TestVerdict.Failed)
            ? TestVerdict.Failed
            : results.Any(result => result.Verdict == TestVerdict.Inconclusive) ? TestVerdict.Inconclusive : TestVerdict.Passed;
        var message = results.Where(result => result.Verdict == verdict).Select(result => result.Message).FirstOrDefault(text => text is not null);
        return new TestSummary(
            name,
            verdict,
            results.Select(result => result.Outcome).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            results.Count,
            results.Aggregate(TimeSpan.Zero, (total, result) => total + result.Duration),
            message,
            capabilitiesByTest.TryGetValue(name, out var ids) ? ids.ToArray() : []);
    }

    private static CapabilitySummary SummarizeCapability(Capability capability, IReadOnlyList<DiscoveredTest> tests, Dictionary<string, TestSummary> summaries)
    {
        var names = capability.Tests
            .Concat(tests.Where(test => test.CapabilityIds.Contains(capability.Id, StringComparer.Ordinal)).Select(test => test.FullName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var results = names
            .Select(name => summaries.TryGetValue(name, out var summary)
                ? new CapabilityTestResult(name, summary.Verdict, summary.Duration, summary.Message)
                : new CapabilityTestResult(name, null, TimeSpan.Zero, null))
            .ToArray();
        return new CapabilitySummary(capability, StatusOf(results.Select(result => result.Verdict).ToArray()), results);
    }
}
