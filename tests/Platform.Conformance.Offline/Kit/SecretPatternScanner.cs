using System.Text;
using System.Text.RegularExpressions;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit;

/// <summary>A credential-shaped string found in a file. The value is never kept, so a finding cannot leak it.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Rule">Name of the credential kind.</param>
internal sealed record SecretFinding(string Path, int Line, string Rule)
{
    /// <summary><c>path:line: rule (value redacted)</c>.</summary>
    public override string ToString() => $"{Path}:{Line}: {Rule} (value redacted)";
}

/// <summary>
/// CAP-KIT-007 (public-repository half, #47): finds high-confidence credential shapes in the tracked files, offline and
/// without gitleaks: private key PEM blocks, GitHub tokens, Octopus API keys, Azure storage account keys, Slack tokens
/// and AWS access key IDs. It follows the policy of <c>.gitleaks.toml</c>: an obvious placeholder (a whole secret that is
/// an angle-bracket token such as <c>&lt;OCTOPUS_API_KEY&gt;</c>, or a <c>${...}</c> or <c>${{...}}</c> reference) is not a
/// finding, and no path is allow-listed. Findings name the file, the line and the kind, never the value.
/// </summary>
internal static partial class SecretPatternScanner
{
    private const int MaxFileBytes = 4 * 1024 * 1024;

    private static readonly (string Name, Regex Pattern)[] Rules =
    [
        ("private key PEM block", PrivateKey()),
        ("GitHub token", GitHubToken()),
        ("GitHub fine-grained token", GitHubFineGrainedToken()),
        ("Octopus API key", OctopusApiKey()),
        ("Azure storage account key", AzureAccountKey()),
        ("Slack token", SlackToken()),
        ("AWS access key ID", AwsAccessKey()),
    ];

    /// <summary>Scans every tracked file of the tree: <c>git ls-files</c> in a work tree, else every file outside <c>.git</c>.</summary>
    /// <param name="tree">The repository tree.</param>
    public static IReadOnlyList<SecretFinding> Scan(BoundaryTree tree)
    {
        var findings = new List<SecretFinding>();
        foreach (var file in tree.TrackedFiles(string.Empty))
        {
            var path = tree.FullPath(file);
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            {
                continue;
            }

            findings.AddRange(ScanText(file, Encoding.UTF8.GetString(bytes)));
        }

        return findings;
    }

    /// <summary>Scans one text.</summary>
    /// <param name="path">Repository-relative path, for the findings.</param>
    /// <param name="text">File content.</param>
    public static IEnumerable<SecretFinding> ScanText(string path, string text)
    {
        foreach (var (name, pattern) in Rules)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var secret = match.Groups["secret"] is { Success: true } group ? group.Value : match.Value;
                if (IsPlaceholder(secret) || IsDocumentedExample(secret))
                {
                    continue;
                }

                yield return new SecretFinding(path, LineOf(text, match.Index), name);
            }
        }
    }

    /// <summary>The placeholder exception of <c>.gitleaks.toml</c>: a whole <c>&lt;...&gt;</c> token or a <c>${...}</c> reference.</summary>
    /// <param name="secret">The secret part of a match.</param>
    public static bool IsPlaceholder(string secret) => AngleToken().IsMatch(secret) || TemplateReference().IsMatch(secret);

    // The value AWS documents as its example key ID.
    private static bool IsDocumentedExample(string secret) => secret.EndsWith("EXAMPLE", StringComparison.Ordinal);

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var position = 0; position < index; position++)
        {
            if (text[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    [GeneratedRegex("""^<[A-Za-z0-9][A-Za-z0-9_.{}*/ -]{0,80}>$""")]
    private static partial Regex AngleToken();

    [GeneratedRegex("""^\$\{\{?[A-Za-z0-9_. -]{1,80}\}?\}$""")]
    private static partial Regex TemplateReference();

    // A PEM header followed, on the next line (or after a literal \n in a JSON string), by a base64 body line.
    [GeneratedRegex("""-----BEGIN (?:[A-Z]+ )*PRIVATE KEY(?: BLOCK)?-----(?:\r?\n|\\n)[A-Za-z0-9+/=]{40,}""")]
    private static partial Regex PrivateKey();

    [GeneratedRegex("""\bgh[pousr]_[A-Za-z0-9]{36,255}\b""")]
    private static partial Regex GitHubToken();

    [GeneratedRegex("""\bgithub_pat_[A-Za-z0-9_]{60,}""")]
    private static partial Regex GitHubFineGrainedToken();

    [GeneratedRegex("""\bAPI-[A-Z0-9]{27}\b""")]
    private static partial Regex OctopusApiKey();

    [GeneratedRegex("""AccountKey=(?<secret>[A-Za-z0-9+/]{40,}={0,2})""")]
    private static partial Regex AzureAccountKey();

    [GeneratedRegex("""\bxox[abposr]-[0-9]{8,}-[0-9A-Za-z-]{10,}|\bxapp-[0-9]-[A-Z0-9]+-[0-9]+-[a-f0-9]{20,}""")]
    private static partial Regex SlackToken();

    [GeneratedRegex("""\b(?:AKIA|ASIA)[0-9A-Z]{16}\b""")]
    private static partial Regex AwsAccessKey();
}
