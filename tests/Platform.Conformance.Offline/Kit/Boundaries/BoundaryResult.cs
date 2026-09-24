using System.Text;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>One violation of a tool-boundary rule.</summary>
/// <param name="RuleId">Rule ID, for example <c>TB01</c>.</param>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">1-based line number, or <c>null</c> when the finding is about the whole file.</param>
/// <param name="Text">
/// The offending line as the script prints it (for the context rules TB17, TB18 and TB20 prefixed with the enclosing
/// OCL step or YAML key chain and a colon), or what is wrong with the file.
/// </param>
internal sealed record BoundaryFinding(string RuleId, string Path, int? Line, string Text)
{
    /// <summary><c>path:line:text</c> for a line, <c>path: text</c> for a file: the script's spelling.</summary>
    public override string ToString() => Line is { } line ? $"{Path}:{line}:{Text}" : $"{Path}: {Text}";
}

/// <summary>What one rule found in a tree.</summary>
/// <param name="RuleId">Rule ID, for example <c>TB01</c>.</param>
/// <param name="Description">The rule's one-line statement, as the script prints it.</param>
/// <param name="Findings">Every violation, in path and line order.</param>
/// <param name="Warnings">Advisory notes that never fail the rule (TB15).</param>
/// <param name="Absent">The paths the rule reads when none of them exists (the rule is skipped), else <c>null</c>.</param>
internal sealed record BoundaryResult(string RuleId, string Description, IReadOnlyList<BoundaryFinding> Findings, IReadOnlyList<string> Warnings, string? Absent)
{
    /// <summary><c>true</c> when the tree holds none of the paths the rule reads.</summary>
    public bool Skipped => Absent is not null;

    /// <summary>A rule whose paths are all absent.</summary>
    /// <param name="ruleId">Rule ID.</param>
    /// <param name="description">Rule statement.</param>
    /// <param name="absent">The paths it reads, space-separated.</param>
    public static BoundaryResult Skip(string ruleId, string description, string absent) => new(ruleId, description, [], [], absent);

    /// <summary>A rule that ran.</summary>
    /// <param name="ruleId">Rule ID.</param>
    /// <param name="description">Rule statement.</param>
    /// <param name="findings">Its findings; duplicates are dropped.</param>
    /// <param name="warnings">Its warnings.</param>
    public static BoundaryResult Of(string ruleId, string description, IEnumerable<BoundaryFinding> findings, IEnumerable<string>? warnings = null) =>
        new(ruleId, description, findings.Distinct().ToArray(), (warnings ?? []).ToArray(), null);

    /// <summary>
    /// The rule's report in the script's format: <c>PASS</c>, <c>FAIL</c> or <c>SKIP</c> with the ID and statement, each
    /// finding on its own line indented by eight spaces, then any <c>WARN</c> lines.
    /// </summary>
    public string Report()
    {
        var report = new StringBuilder();
        if (Skipped)
        {
            report.Append(Say("SKIP", $"{Description} (absent: {Absent})"));
            return report.ToString();
        }

        report.Append(Say(Findings.Count == 0 ? "PASS" : "FAIL", Description));
        foreach (var finding in Findings)
        {
            report.Append("        ").Append(finding).Append('\n');
        }

        foreach (var warning in Warnings)
        {
            report.Append(Say("WARN", warning));
        }

        return report.ToString();
    }

    private string Say(string verdict, string text) => $"{verdict,-4} {RuleId,-5} {text}\n";
}
