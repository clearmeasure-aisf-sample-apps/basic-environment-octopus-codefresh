using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006 (public-repository half, #47): the safe-usage rule for the <c>pull_request_target</c> trigger. That trigger
/// runs a workflow from the default branch with the repository's secrets and a write-capable token, on events raised by
/// anyone, including a fork's pull request; in a public repository it is the classic way to run attacker-controlled code
/// with secrets. So it is allowed only in a workflow that TB24 lists (<see cref="GitHubWorkflowRule.Exceptions"/>, today the
/// board workflow), and such a workflow must still (a) never check out or fetch pull request code (no
/// <c>actions/checkout</c>, <c>gh pr checkout</c>, <c>git clone</c>, <c>git fetch</c> or <c>git checkout</c>) and (b) never
/// read the pull request head or expand attacker-controlled text into a step: no <c>github.head_ref</c>, no
/// <c>github.event.pull_request.head</c>, no <c>refs/pull/</c>, and no title, body, label, message or branch-name expression
/// of the event. Reading such text as data from the event file inside a script, as the board workflow does, is fine.
/// </summary>
/// <remarks>
/// A blanket ban of <c>pull_request_target</c> would fail the existing board workflow (the TB24 exception), so this is the
/// safe-usage form. Comment lines are ignored, so a workflow may explain what it does not do.
/// </remarks>
internal static partial class PullRequestTargetWorkflowRule
{
    /// <summary>Rule ID.</summary>
    public const string Id = "PRT";

    /// <summary>Rule statement.</summary>
    public const string Description =
        "pull_request_target only in a listed board workflow that never checks out code or reads the pull request head or attacker-controlled text";

    private const string WorkflowFolder = ".github/workflows";

    /// <summary>Checks every workflow file of the tree.</summary>
    /// <param name="tree">The repository tree.</param>
    public static BoundaryResult Check(BoundaryTree tree)
    {
        var findings = new List<BoundaryFinding>();
        foreach (var file in tree.FilesIn(WorkflowFolder))
        {
            var lines = tree.Lines(file) ?? [];
            if (!lines.Any(line => !Comment().IsMatch(line) && Trigger().IsMatch(line)))
            {
                continue;
            }

            findings.AddRange(FileFindings(file, lines));
        }

        return BoundaryResult.Of(Id, Description, findings);
    }

    private static IEnumerable<BoundaryFinding> FileFindings(string file, IReadOnlyList<string> lines)
    {
        var listed = GitHubWorkflowRule.Exceptions.Any(exception => string.Equals(exception.Path, file, StringComparison.Ordinal));
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (Comment().IsMatch(line))
            {
                continue;
            }

            var number = index + 1;
            if (!listed && Trigger().IsMatch(line))
            {
                yield return new BoundaryFinding(Id, file, number,
                    "pull_request_target runs with secrets on events anyone can raise: only a workflow listed in GitHubWorkflowRule.Exceptions (TB24) may use it");
            }

            if (CodeAccess().Match(line) is { Success: true } access)
            {
                yield return new BoundaryFinding(Id, file, number,
                    $"'{access.Value.Trim()}' brings pull request code into a pull_request_target workflow: it checks out and fetches nothing");
            }

            if (UntrustedInput().Match(line) is { Success: true } input)
            {
                yield return new BoundaryFinding(Id, file, number,
                    $"'{input.Value}' is attacker-controlled: a pull_request_target workflow never reads the pull request head or expands its text into a step (read it as data from the event file)");
            }
        }
    }

    [GeneratedRegex("""^\s*#""")]
    private static partial Regex Comment();

    // The trigger name as a whole word: a key of the on: block, an inline list or a string compared with the event name.
    [GeneratedRegex("""(?<![A-Za-z0-9_])pull_request_target(?![A-Za-z0-9_])""")]
    private static partial Regex Trigger();

    [GeneratedRegex("""(?<![A-Za-z0-9_./-])(?:actions/checkout\b|gh\s+pr\s+checkout\b|git\s+(?:clone|fetch|checkout|pull)\b)""")]
    private static partial Regex CodeAccess();

    [GeneratedRegex(
        """github\.head_ref|github\.event\.pull_request\.head|refs/pull/|github\.event\.(?:pull_request|issue|comment|review|review_comment|discussion)\.(?:title|body|label|labels)\b|github\.event\.(?:head_commit|commits)\b""")]
    private static partial Regex UntrustedInput();
}
