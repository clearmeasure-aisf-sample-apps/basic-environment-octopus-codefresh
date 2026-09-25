using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>How a bot-path audit ended.</summary>
internal enum BotAuditOutcome
{
    /// <summary>Every audited bot commit changes only pin lines.</summary>
    Passed,

    /// <summary>A bot commit changes more than a pin, or the history cannot be listed.</summary>
    Failed,

    /// <summary>Nothing to audit: the root is not in a Git work tree, or the branch has no commit.</summary>
    Skipped,
}

/// <summary>The result of a bot-path audit.</summary>
/// <param name="Outcome">Passed, failed or skipped.</param>
/// <param name="Summary">The one-line verdict, as the script prints it.</param>
/// <param name="Findings">Per offending commit: <c>&lt;sha7&gt; &lt;author&gt;: &lt;subject&gt;</c>, then each violation indented by two spaces.</param>
/// <param name="Warnings">Notes that never fail the audit (a shallow clone).</param>
internal sealed record BotAuditResult(BotAuditOutcome Outcome, string Summary, IReadOnlyList<string> Findings, IReadOnlyList<string> Warnings)
{
    /// <summary>The audit's report in the script's format.</summary>
    public string Report() =>
        string.Concat(Warnings.Select(warning => $"WARN AUDIT {warning}\n"))
        + $"{(Outcome switch { BotAuditOutcome.Passed => "PASS", BotAuditOutcome.Failed => "FAIL", _ => "SKIP" }),-4} AUDIT {Summary}\n"
        + string.Concat(Findings.Select(finding => $"        {finding}\n"));
}

/// <summary>The inputs of the audit, from the environment as the script reads them.</summary>
/// <param name="BotAuthors">Extended regex matched against <c>Name &lt;email&gt;</c> of each commit's author and committer.</param>
/// <param name="BotAuthorsFromEnvironment"><c>false</c> when <c>PLATFORM_BOT_AUTHORS</c> was unset and the design value is used.</param>
/// <param name="Depth">First-parent commits audited (<c>AUDIT_DEPTH</c>, default 20), digits only.</param>
internal sealed record BotAuditSettings(string BotAuthors, bool BotAuthorsFromEnvironment, string Depth)
{
    /// <summary>Variable naming the bot identities.</summary>
    public const string BotAuthorsVariable = "PLATFORM_BOT_AUTHORS";

    /// <summary>Variable naming the number of first-parent commits to audit.</summary>
    public const string DepthVariable = "AUDIT_DEPTH";

    /// <summary>
    /// The design value of <c>&lt;platform-bots-author-regex&gt;</c> (design §6.2): the Octopus step "Update Argo CD image
    /// tags" commits as <c>Octopus &lt;octopus@octopus.com&gt;</c>, step template platform-pin-writer as
    /// <c>octopus-argocd-pin-bot</c>. A dot stands for <c>&lt;</c> because Codefresh stores <c>&lt;</c> in a variable as
    /// <c>&amp;lt;</c>.
    /// </summary>
    public const string DesignBotAuthors = @"^(Octopus .octopus@octopus\.com>|octopus-argocd-pin-bot .[^>]+>)$";

    /// <summary>
    /// Reads <c>PLATFORM_BOT_AUTHORS</c> and <c>AUDIT_DEPTH</c>. An unset or empty <c>PLATFORM_BOT_AUTHORS</c> falls back to
    /// <see cref="DesignBotAuthors"/>; an unset or empty <c>AUDIT_DEPTH</c> means 20.
    /// </summary>
    /// <param name="environment">Environment variables.</param>
    public static BotAuditSettings From(IEnvironmentVariables environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var authors = environment.Get(BotAuthorsVariable);
        var depth = environment.Get(DepthVariable);
        return new BotAuditSettings(
            string.IsNullOrEmpty(authors) ? DesignBotAuthors : authors,
            !string.IsNullOrEmpty(authors),
            string.IsNullOrEmpty(depth) ? "20" : depth);
    }
}

/// <summary>
/// C# port of the bot-path audit of <c>scripts/checks/tool-boundaries.sh --audit-bot-commits</c> (design §6.2): fails
/// when a first-parent commit made by a bot identity changes anything other than a pin field under
/// <c>gitops/apps/&lt;app&gt;/envs/&lt;env&gt;/&lt;deployable&gt;/</c>: <c>newTag</c> lines (and removed digests) of a
/// <c>kustomization.yaml</c>, tag values of a Helm <c>values.yaml</c>, or raw <c>image:</c> fields, never the floating tag
/// <c>latest</c>. Git history is read through the git command line only.
/// </summary>
internal static class BotCommitAudit
{
    private const string Tag = "\"?[0-9A-Za-z][0-9A-Za-z.+_-]*\"?[[:space:]]*(#.*)?$";
    private static readonly Regex KustomizePin = PosixPatterns.Ere($"^[+-][[:space:]]*newTag:[[:space:]]*{Tag}|^-[[:space:]]*digest:[[:space:]]*\"?sha256:[0-9a-f]+\"?[[:space:]]*$");
    private static readonly Regex HelmPin = PosixPatterns.Ere($"^[+-][[:space:]]*[A-Za-z0-9_.-]*[Tt]ag:[[:space:]]*{Tag}");
    private static readonly Regex RawPin = PosixPatterns.Ere(@"^[+-][[:space:]]*(-[[:space:]]+)?image:[[:space:]]*""?<acr-name>\.azurecr\.io/apps/[a-z0-9-]+/[a-z0-9-]+:[0-9A-Za-z.+_-]+""?[[:space:]]*$");
    private static readonly Regex Latest = PosixPatterns.Ere(@"^\+.*(newTag|[Tt]ag|image):.*latest");
    private static readonly Regex ChangedLine = PosixPatterns.Ere("^[+-]");

    /// <summary>Audits the first-parent history of the work tree that holds <paramref name="root"/>.</summary>
    /// <param name="git">The git executable.</param>
    /// <param name="root">Repository root, or a folder inside the work tree.</param>
    /// <param name="botAuthors">Extended regex of the bot identities (<c>Name &lt;email&gt;</c>).</param>
    /// <param name="depth">First-parent commits to audit from HEAD when <paramref name="range"/> is absent (digits).</param>
    /// <param name="range">A revision range to audit instead, for example <c>origin/main..HEAD</c>.</param>
    public static BotAuditResult Run(string git, string root, string botAuthors, string depth = "20", string? range = null)
    {
        var topLevel = GitCli.Run(git, root, "rev-parse", "--show-toplevel");
        if (topLevel.ExitCode != 0)
        {
            return new BotAuditResult(BotAuditOutcome.Skipped, $"not a git work tree: {root}", [], []);
        }

        if (depth.Length == 0 || !depth.All(char.IsAsciiDigit))
        {
            return new BotAuditResult(BotAuditOutcome.Failed, $"{BotAuditSettings.DepthVariable} must be a number, not '{depth}'", [], []);
        }

        Regex bots;
        try
        {
            bots = PosixPatterns.Ere(botAuthors);
        }
        catch (ArgumentException exception)
        {
            return new BotAuditResult(BotAuditOutcome.Failed, $"{BotAuditSettings.BotAuthorsVariable} '{botAuthors}' is not a valid extended regular expression: {exception.Message}", [], []);
        }

        var top = topLevel.Trimmed;
        var prefix = GitCli.Run(git, root, "rev-parse", "--show-prefix").Trimmed;
        var pinPath = new Regex($@"^{Regex.Escape(prefix)}gitops/apps/[^/]+/envs/[^/]+/[^/]+/[^/]+\.ya?ml$", RegexOptions.CultureInvariant);
        if (range is null && GitCli.Run(git, top, "rev-parse", "--verify", "--quiet", "HEAD^{commit}").ExitCode != 0)
        {
            return new BotAuditResult(BotAuditOutcome.Skipped, $"no commit on the checked-out branch of {top}", [], []);
        }

        var revisions = range is null
            ? GitCli.Run(git, top, "rev-list", "--first-parent", "--no-merges", $"--max-count={depth}", "HEAD")
            : GitCli.Run(git, top, "rev-list", "--first-parent", "--no-merges", range);
        if (revisions.ExitCode != 0)
        {
            var reason = (revisions.Output + revisions.Error).TrimEnd('\n');
            return new BotAuditResult(BotAuditOutcome.Failed, range is null ? $"cannot list commits: {reason}" : $"cannot list commits for range '{range}': {reason}", [], []);
        }

        var warnings = GitCli.Run(git, top, "rev-parse", "--is-shallow-repository").Trimmed == "true"
            ? new[] { "shallow clone: only the fetched history is audited" }
            : [];
        var total = 0;
        var botCommits = 0;
        var badCommits = 0;
        var findings = new List<string>();
        foreach (var commit in revisions.Lines)
        {
            total++;
            var identities = GitCli.Run(git, top, "show", "-s", "--format=%an <%ae>%n%cn <%ce>", commit).Trimmed.Split('\n');
            if (!identities.Any(identity => bots.IsMatch(identity)))
            {
                continue;
            }

            botCommits++;
            var violations = Violations(git, top, commit, pinPath).ToArray();
            if (violations.Length > 0)
            {
                badCommits++;
                findings.Add(GitCli.Run(git, top, "show", "-s", "--format=%h %an: %s", commit).Trimmed);
                findings.AddRange(violations);
            }
        }

        return badCommits > 0
            ? new BotAuditResult(BotAuditOutcome.Failed, $"{badCommits} of {botCommits} platform-bots commits (of {total} audited) change more than a pin", findings, warnings)
            : new BotAuditResult(BotAuditOutcome.Passed, $"{total} commits audited, {botCommits} by platform-bots, all limited to pin lines under gitops/apps/*/envs/", [], warnings);
    }

    private static IEnumerable<string> Violations(string git, string top, string commit, Regex pinPath)
    {
        foreach (var file in GitCli.Run(git, top, "diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "--no-renames", commit).Lines)
        {
            if (!pinPath.IsMatch(file))
            {
                yield return $"  changes {file} (only pin files under gitops/apps/<app>/envs/ are allowed)";
                continue;
            }

            var pin = CasePattern.Matches(file, "*/kustomization.yaml") ? KustomizePin
                : CasePattern.Matches(file, "*/values.yaml") ? HelmPin
                : RawPin;
            var diff = GitCli.Run(git, top, "show", "--format=", "--unified=0", "--no-color", "--no-ext-diff", commit, "--", file);
            foreach (var line in diff.Output.Split('\n').Where(text => ChangedLine.IsMatch(text)))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!pin.IsMatch(line))
                {
                    yield return $"  {file}: changes a line other than a pin: {line}";
                }
                else if (Latest.IsMatch(line))
                {
                    yield return $"  {file}: writes the floating tag 'latest'";
                }
            }
        }
    }
}
