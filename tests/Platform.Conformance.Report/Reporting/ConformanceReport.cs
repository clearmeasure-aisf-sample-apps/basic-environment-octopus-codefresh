using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Report;

/// <summary>Status of a capability in one report.</summary>
internal enum CapabilityStatus
{
    /// <summary>Every test of the capability passed.</summary>
    Pass,

    /// <summary>At least one test of the capability failed.</summary>
    Fail,

    /// <summary>Some tests ran but not every one passed and none failed (inconclusive, skipped or partly run).</summary>
    Inconclusive,

    /// <summary>No test of the capability ran.</summary>
    NotRun,
}

/// <summary>All results of one test (several for a parameterized test or several TRX files).</summary>
/// <param name="Name">Fully qualified test name.</param>
/// <param name="Verdict">Worst verdict: Failed, then Inconclusive, then Passed.</param>
/// <param name="Outcomes">Distinct raw TRX outcomes.</param>
/// <param name="Results">Number of TRX results.</param>
/// <param name="Duration">Sum of the result durations.</param>
/// <param name="Message">Message of the worst result, if any.</param>
/// <param name="CapabilityIds">Capabilities the test proves (from the assemblies or map, and the catalogue).</param>
internal sealed record TestSummary(string Name, TestVerdict Verdict, IReadOnlyList<string> Outcomes, int Results, TimeSpan Duration, string? Message, IReadOnlyList<string> CapabilityIds);

/// <summary>Result of one test of a capability; <see cref="Verdict"/> is <c>null</c> when the test did not run.</summary>
/// <param name="Name">Fully qualified test name.</param>
/// <param name="Verdict">Verdict, or <c>null</c> when absent from every TRX file.</param>
/// <param name="Duration">Duration.</param>
/// <param name="Message">Failure or inconclusive message.</param>
internal sealed record CapabilityTestResult(string Name, TestVerdict? Verdict, TimeSpan Duration, string? Message);

/// <summary>A capability with the results of its tests.</summary>
/// <param name="Capability">The catalogue entry.</param>
/// <param name="Status">Derived status.</param>
/// <param name="Tests">Its tests, sorted by name.</param>
internal sealed record CapabilitySummary(Capability Capability, CapabilityStatus Status, IReadOnlyList<CapabilityTestResult> Tests)
{
    /// <summary>Tests that passed.</summary>
    public int Passed => Tests.Count(test => test.Verdict == TestVerdict.Passed);

    /// <summary>Sum of the durations of its tests.</summary>
    public TimeSpan Duration => Tests.Aggregate(TimeSpan.Zero, (total, test) => total + test.Duration);
}

/// <summary>The conformance report derived from TRX results, the catalogue and the test-to-capability map.</summary>
internal sealed record ConformanceReport
{
    /// <summary>Report title.</summary>
    public required string Title { get; init; }

    /// <summary>When the report was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>TRX files read.</summary>
    public required IReadOnlyList<string> TrxFiles { get; init; }

    /// <summary>Number of TRX results.</summary>
    public required int ResultCount { get; init; }

    /// <summary>Results per verdict.</summary>
    public required IReadOnlyDictionary<TestVerdict, int> Verdicts { get; init; }

    /// <summary>Results per raw TRX outcome.</summary>
    public required IReadOnlyDictionary<string, int> RawOutcomes { get; init; }

    /// <summary>Sum of all result durations.</summary>
    public required TimeSpan TestDuration { get; init; }

    /// <summary>Sum of the wall-clock durations of the runs, when recorded.</summary>
    public required TimeSpan? WallClock { get; init; }

    /// <summary>Every catalogue capability with its status, ordered by owner then ID.</summary>
    public required IReadOnlyList<CapabilitySummary> Capabilities { get; init; }

    /// <summary>Tests with a failed verdict.</summary>
    public required IReadOnlyList<TestSummary> Failures { get; init; }

    /// <summary>Tests with an inconclusive verdict.</summary>
    public required IReadOnlyList<TestSummary> Inconclusive { get; init; }

    /// <summary>The slowest tests, slowest first.</summary>
    public required IReadOnlyList<TestSummary> Slowest { get; init; }

    /// <summary>Tests with results that prove no catalogue capability.</summary>
    public required IReadOnlyList<TestSummary> WithoutCapability { get; init; }

    /// <summary><c>true</c> when any test failed; the tool then exits non-zero.</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>Number of capabilities with <paramref name="status"/>.</summary>
    /// <param name="status">The status.</param>
    public int CountOf(CapabilityStatus status) => Capabilities.Count(capability => capability.Status == status);
}
