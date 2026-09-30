using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>Where a retired credential name may still appear, and why.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Names">The retired names the file may carry.</param>
/// <param name="Reason">Why the file names them.</param>
/// <param name="HistoryOnly">
/// <c>true</c>: only between <see cref="RetiredCredentialGuard.HistoryStart"/> and <see cref="RetiredCredentialGuard.HistoryEnd"/>.
/// </param>
internal sealed record RetiredCredentialAllowance(string Path, IReadOnlyList<string> Names, string Reason, bool HistoryOnly = false);

/// <summary>
/// Guard of CAP-KIT-007 (no secret, and no retired credential): the names of the personal access tokens the platform
/// retired in favour of the GitHub App <c>aisf-board</c> never come back. Every tracked text file (Markdown included, unlike
/// the tool-boundary rules) that names a retired credential fails unless <see cref="Allowances"/> lists it for that name,
/// and an allowance that no longer matches fails as stale, so the list only shrinks. The one credential still in use,
/// the Codefresh context variable of the conformance suite, is allowed only in the files that describe that context.
/// </summary>
/// <remarks>
/// The tracked files come from <c>git ls-files</c>; a tree that is no Git work tree (the tests' synthetic trees) is walked
/// instead. Binary files never match.
/// </remarks>
internal static class RetiredCredentialGuard
{
    /// <summary>Guard ID (a member of the CAP-KIT-007 family, no tool-boundary number).</summary>
    public const string Id = "CAP-KIT-007";

    /// <summary>Guard statement.</summary>
    public const string Description = "No retired credential name (personal access tokens replaced by the GitHub App) appears outside the allow-list";

    /// <summary>Start marker of the history section of the credential-rotation runbook.</summary>
    public const string HistoryStart = "<!-- retired-credentials:start -->";

    /// <summary>End marker of the history section of the credential-rotation runbook.</summary>
    public const string HistoryEnd = "<!-- retired-credentials:end -->";

    /// <summary>The retired repository secret of the board workflow (a fine-grained personal access token).</summary>
    public const string ProjectsPat = "PROJECTS_PAT";

    /// <summary>The retired environment variable of the feature-loop scripts (a fine-grained personal access token).</summary>
    public const string SampleAppsPat = "GITHUB_SAMPLE_APPS_PAT";

    /// <summary>The Codefresh context variable that still holds the conformance suite's organization token.</summary>
    public const string ConformanceGitHubToken = "CONFORMANCE_GITHUB_TOKEN";

    /// <summary>The runbook whose history section may name the retired tokens.</summary>
    public const string RunbookPath = "docs/runbooks/credential-rotation.md";

    private const string GuardSource = "tests/Platform.Conformance.Offline/Kit/Boundaries/RetiredCredentialGuard.cs";
    private const string ConformanceReason = "Codefresh context platform-conformance, owner-managed; migrating it off the organization token is separate work (a child of #45)";

    /// <summary>The retired names.</summary>
    public static IReadOnlyList<string> Names { get; } = [ProjectsPat, SampleAppsPat, ConformanceGitHubToken];

    /// <summary>Every place a retired name may appear, each with its reason; an entry that no longer matches fails as stale.</summary>
    public static IReadOnlyList<RetiredCredentialAllowance> Allowances { get; } =
    [
        new(RunbookPath, [ProjectsPat, SampleAppsPat], "the history section 'Retired credentials' records what was retired and how to revoke it", HistoryOnly: true),
        new(GuardSource, Names, "the guard names what it forbids (its tests use these constants, so they name nothing)"),
        new("codefresh/platform/integrations.yaml", [ConformanceGitHubToken], ConformanceReason),
        new("docs/preview-codefresh.md", [ConformanceGitHubToken], ConformanceReason),
        new("docs/runbooks/conformance.md", [ConformanceGitHubToken], ConformanceReason),
    ];

    /// <summary>Checks the tracked files of a tree against the retired names and <paramref name="allowances"/>.</summary>
    /// <param name="tree">The repository tree.</param>
    /// <param name="allowances">The allow-list; the real one unless a test brings its own.</param>
    public static BoundaryResult Check(BoundaryTree tree, IReadOnlyList<RetiredCredentialAllowance>? allowances = null)
    {
        allowances ??= Allowances;
        var findings = new List<BoundaryFinding>();
        var matched = new HashSet<(string Path, string Name)>();
        foreach (var file in tree.TrackedFiles(string.Empty))
        {
            if (tree.Lines(file) is not { } lines)
            {
                continue;
            }

            var allowance = allowances.FirstOrDefault(entry => string.Equals(entry.Path, file, StringComparison.Ordinal));
            var inHistory = false;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (allowance is { HistoryOnly: true })
                {
                    if (line.Contains(HistoryStart, StringComparison.Ordinal))
                    {
                        inHistory = true;
                    }
                    else if (line.Contains(HistoryEnd, StringComparison.Ordinal))
                    {
                        inHistory = false;
                    }
                }

                foreach (var name in Names.Where(name => Mentions(line, name)))
                {
                    var permitted = allowance is not null
                        && allowance.Names.Contains(name, StringComparer.Ordinal)
                        && (!allowance.HistoryOnly || inHistory);
                    if (permitted)
                    {
                        matched.Add((file, name));
                    }
                    else
                    {
                        findings.Add(new BoundaryFinding(Id, file, index + 1, $"names the retired credential {name}: {Advice(name)}"));
                    }
                }
            }

            if (allowance is { HistoryOnly: true } && inHistory)
            {
                findings.Add(new BoundaryFinding(Id, file, null, $"the history section opened by {HistoryStart} is never closed by {HistoryEnd}"));
            }
        }

        foreach (var entry in allowances)
        {
            foreach (var name in entry.Names.Where(name => !matched.Contains((entry.Path, name))))
            {
                var where = entry.HistoryOnly ? $"between {HistoryStart} and {HistoryEnd} of {entry.Path}" : entry.Path;
                findings.Add(new BoundaryFinding(Id, entry.Path, null, $"stale allow-list entry: {name} is no longer named {where} ({entry.Reason}); delete the entry"));
            }
        }

        return BoundaryResult.Of(Id, Description, findings);
    }

    private static bool Mentions(string line, string name) =>
        line.Contains(name, StringComparison.Ordinal) && Boundary(name).IsMatch(line);

    private static Regex Boundary(string name) =>
        new($"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static string Advice(string name) => name == ConformanceGitHubToken
        ? "only the three files that describe Codefresh context platform-conformance may name it (RetiredCredentialGuard.Allowances)"
        : $"personal access tokens are replaced by the GitHub App aisf-board; only the history section of {RunbookPath} may name it";
}
