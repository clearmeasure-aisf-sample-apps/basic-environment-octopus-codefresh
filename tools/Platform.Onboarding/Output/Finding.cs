namespace Platform.Onboarding.Output;

/// <summary>Severity of a finding; only errors fail a command.</summary>
internal enum Severity
{
    /// <summary>Fails the command (exit code 1).</summary>
    Error,

    /// <summary>Reported, never fails the command.</summary>
    Warning,
}

/// <summary>One problem found by <c>check</c>, <c>scaffold</c> or <c>retire</c>.</summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Rule">Short rule ID, for example <c>schema</c> or <c>blast-radius</c>.</param>
/// <param name="Subject">The app the finding is about, or <c>-</c>.</param>
/// <param name="Message">What is wrong and how to fix it.</param>
/// <param name="Path">Repository-relative file, when there is one.</param>
/// <param name="Line">1-based line in <paramref name="Path"/>, when known.</param>
internal sealed record Finding(Severity Severity, string Rule, string Subject, string Message, string? Path = null, int? Line = null)
{
    /// <summary>Creates an error.</summary>
    /// <param name="rule">Rule ID.</param>
    /// <param name="subject">App or <c>-</c>.</param>
    /// <param name="message">Message.</param>
    /// <param name="path">File, if any.</param>
    /// <param name="line">Line, if known.</param>
    public static Finding Error(string rule, string subject, string message, string? path = null, int? line = null) =>
        new(Severity.Error, rule, subject, message, path, line);

    /// <summary>Creates a warning.</summary>
    /// <param name="rule">Rule ID.</param>
    /// <param name="subject">App or <c>-</c>.</param>
    /// <param name="message">Message.</param>
    /// <param name="path">File, if any.</param>
    /// <param name="line">Line, if known.</param>
    public static Finding Warning(string rule, string subject, string message, string? path = null, int? line = null) =>
        new(Severity.Warning, rule, subject, message, path, line);

    /// <summary><c>ERROR rule app path:line: message</c>.</summary>
    public override string ToString()
    {
        var level = Severity == Severity.Error ? "ERROR" : "WARN ";
        var location = Path is null ? string.Empty : Line is null ? $"{Path}: " : $"{Path}:{Line}: ";
        return $"{level} {Rule,-13} {Subject}: {location}{Message}";
    }
}

/// <summary>Collects findings and writes them in order.</summary>
internal sealed class FindingList
{
    private readonly List<Finding> findings = [];

    /// <summary>Every finding so far.</summary>
    public IReadOnlyList<Finding> All => findings;

    /// <summary>Number of errors.</summary>
    public int ErrorCount => findings.Count(finding => finding.Severity == Severity.Error);

    /// <summary>Number of warnings.</summary>
    public int WarningCount => findings.Count(finding => finding.Severity == Severity.Warning);

    /// <summary>Adds a finding.</summary>
    /// <param name="finding">The finding.</param>
    public void Add(Finding finding) => findings.Add(finding);

    /// <summary>Adds several findings.</summary>
    /// <param name="items">The findings.</param>
    public void AddRange(IEnumerable<Finding> items) => findings.AddRange(items);

    /// <summary>Writes every finding, one per line.</summary>
    /// <param name="writer">Output.</param>
    public void WriteTo(TextWriter writer)
    {
        foreach (var finding in findings)
        {
            writer.WriteLine(finding);
        }
    }
}
