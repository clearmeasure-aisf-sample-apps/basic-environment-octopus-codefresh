using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>One line of a commit message or of PR text that carries an attribution the platform forbids.</summary>
/// <param name="Rule">The rule that matched: <c>co-author-trailer</c>, <c>attribution-footer</c> or <c>model-identifier</c>.</param>
/// <param name="LineNumber">1-based line number inside the text.</param>
/// <param name="Line">The matched line, trimmed (never more than the line).</param>
internal sealed record AttributionMatch(string Rule, int LineNumber, string Line);

/// <summary>
/// Guard of CAP-KIT-011 (commit and pull request text carry no model identifier and no co-author trailer): the messages of
/// the commits in the range <c>origin/&lt;default branch&gt;..HEAD</c>, and the PR title and body when the caller supplies
/// them, are matched line by line against identifier shapes, never against substrings, so the platform name Octopus (which
/// contains the letters of a model family) and the pin commit titles never match. Git history is read through the git
/// command line only, as <see cref="BotCommitAudit"/> does.
/// </summary>
/// <remarks>
/// <c>codefresh/env-checks</c> does not expose the PR title or body, so in CI only the commit range is checked; the PR text
/// is checked where the caller has it (the feature-loop pre-PR gate). A squash merge builds its message from the PR text and
/// discards the branch commits, which is why both inputs exist. A missing base ref (shallow clone, first push) skips the
/// range part and says so; it never fails and never passes silently.
/// </remarks>
internal static class CommitAttributionGuard
{
    /// <summary>Guard ID.</summary>
    public const string Id = "CAP-KIT-011";

    /// <summary>Guard statement.</summary>
    public const string Description = "Commit messages and PR text carry no model identifier, attribution footer or co-author trailer";

    /// <summary>Variable that overrides the commit range (tests, or a base other than the default branch).</summary>
    public const string RangeVariable = "PLATFORM_COMMIT_RANGE";

    /// <summary>Variable holding the PR title, when the caller has it.</summary>
    public const string PrTitleVariable = "PLATFORM_PR_TITLE";

    /// <summary>Variable holding the PR body, when the caller has it.</summary>
    public const string PrBodyVariable = "PLATFORM_PR_BODY";

    /// <summary>Rule name of a co-author trailer line.</summary>
    public const string CoAuthorTrailer = "co-author-trailer";

    /// <summary>Rule name of a generated-with footer or the vendor no-reply address.</summary>
    public const string AttributionFooter = "attribution-footer";

    /// <summary>Rule name of a model identifier.</summary>
    public const string ModelIdentifier = "model-identifier";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const int MaxEchoedLine = 120;
    private const char RecordSeparator = '\u001e';
    private const char UnitSeparator = '\u001f';
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        (CoAuthorTrailer, new(@"^\s*Co-Authored-By\s*:", Options, MatchTimeout)),
        (AttributionFooter, new(@"\bGenerated\s+with\s+\[?Claude\b", Options, MatchTimeout)),
        (AttributionFooter, new(@"(?<![\w.-])noreply@anthropic\.com\b", Options, MatchTimeout)),
        (ModelIdentifier, new(@"\bclaude-(opus|sonnet|haiku|fable)\b[-\w.]*", Options, MatchTimeout)),
        (ModelIdentifier, new(@"\bClaude\s+(Opus|Sonnet|Fable|Haiku)\b(\s+\d[\d.]*)?", Options, MatchTimeout)),
        (ModelIdentifier, new(@"\b(Opus|Sonnet|Fable|Haiku)[ \t-]+\d", Options, MatchTimeout)),
    ];

    /// <summary>Every forbidden line of <paramref name="text"/>, at most one match per line (the first rule that matches).</summary>
    /// <param name="text">A commit message or PR text; any line ending.</param>
    public static IReadOnlyList<AttributionMatch> Find(string? text)
    {
        var matches = new List<AttributionMatch>();
        if (string.IsNullOrEmpty(text))
        {
            return matches;
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            foreach (var (rule, pattern) in Rules)
            {
                if (pattern.IsMatch(line))
                {
                    matches.Add(new AttributionMatch(rule, index + 1, Echo(line)));
                    break;
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Checks the commits of a range and the PR text. The range is <paramref name="range"/>, else the variable
    /// <see cref="RangeVariable"/>, else <c>origin/&lt;default branch&gt;..HEAD</c>. PR text comes from the parameters, else
    /// from <see cref="PrTitleVariable"/> and <see cref="PrBodyVariable"/>; unset or empty means none was supplied.
    /// </summary>
    /// <param name="git">The git executable.</param>
    /// <param name="root">Repository root, or a folder inside the work tree.</param>
    /// <param name="environment">Environment variables.</param>
    /// <param name="range">A revision range to check instead of the default.</param>
    /// <param name="prTitle">The PR title, instead of <see cref="PrTitleVariable"/>.</param>
    /// <param name="prBody">The PR body, instead of <see cref="PrBodyVariable"/>.</param>
    public static BoundaryResult Check(string git, string root, IEnvironmentVariables environment, string? range = null, string? prTitle = null, string? prBody = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var findings = new List<BoundaryFinding>();
        var warnings = new List<string>();
        var checkedSomething = false;

        var title = Supplied(prTitle) ?? Supplied(environment.Get(PrTitleVariable));
        var body = Supplied(prBody) ?? Supplied(environment.Get(PrBodyVariable));
        if (title is null && body is null)
        {
            warnings.Add($"no PR text supplied ({PrTitleVariable}, {PrBodyVariable}): the PR title and body were not checked");
        }
        else
        {
            checkedSomething = true;
            findings.AddRange(TextFindings("PR title", title));
            findings.AddRange(TextFindings("PR body", body));
        }

        var absent = CheckRange(git, root, Supplied(range) ?? Supplied(environment.Get(RangeVariable)), findings, warnings);
        if (absent is null)
        {
            checkedSomething = true;
        }
        else
        {
            warnings.Add($"commit range not checked (absent: {absent})");
        }

        return checkedSomething
            ? BoundaryResult.Of(Id, Description, findings, warnings)
            : BoundaryResult.Skip(Id, Description, absent ?? "range");
    }

    private static IEnumerable<BoundaryFinding> TextFindings(string name, string? text) =>
        Find(text).Select(match => new BoundaryFinding(Id, name, match.LineNumber, $"{match.Rule}: {match.Line}"));

    /// <summary>Reads the range into <paramref name="findings"/>; returns what is missing when it cannot be read, else <c>null</c>.</summary>
    private static string? CheckRange(string git, string root, string? range, List<BoundaryFinding> findings, List<string> warnings)
    {
        var topLevel = GitCli.Run(git, root, "rev-parse", "--show-toplevel");
        if (topLevel.ExitCode != 0)
        {
            return $"git work tree at {root}";
        }

        var top = topLevel.Trimmed;
        if (range is null)
        {
            var baseRef = DefaultBase(git, top);
            if (GitCli.Run(git, top, "rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}").ExitCode != 0)
            {
                return baseRef;
            }

            range = $"{baseRef}..HEAD";
        }

        var log = GitCli.Run(git, top, "log", "--no-color", $"--format={RecordSeparator}%h{UnitSeparator}%B", range, "--");
        if (log.ExitCode != 0)
        {
            return range;
        }

        if (GitCli.Run(git, top, "rev-parse", "--is-shallow-repository").Trimmed == "true")
        {
            warnings.Add("shallow clone: only the fetched history is checked");
        }

        foreach (var record in log.Output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf(UnitSeparator, StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var sha = record[..separator];
            var message = record[(separator + 1)..];
            var subject = message.Split('\n', 2)[0].TrimEnd('\r');
            foreach (var match in Find(message))
            {
                findings.Add(new BoundaryFinding(Id, $"{sha} {Echo(subject)}", null, $"{match.Rule} (line {match.LineNumber}): {match.Line}"));
            }
        }

        return null;
    }

    /// <summary><c>origin/&lt;default branch&gt;</c> from <c>origin/HEAD</c>, else <c>origin/main</c>.</summary>
    private static string DefaultBase(string git, string top)
    {
        var head = GitCli.Run(git, top, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        return head.ExitCode == 0 && head.Trimmed.StartsWith("origin/", StringComparison.Ordinal) ? head.Trimmed : "origin/main";
    }

    private static string? Supplied(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Echo(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxEchoedLine ? trimmed : trimmed[..MaxEchoedLine] + "...";
    }
}
