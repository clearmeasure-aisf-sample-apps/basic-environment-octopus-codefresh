using System.Text.RegularExpressions;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// ADR-IR14 (public repository, #47, #51): the committed <c>*.tfvars</c> files (not the <c>.example</c> files) hold identifiers
/// and sizing that the owner accepted as public: tenant and subscription IDs, network CIDRs, resource and vault names, chart
/// versions. They must never hold what can be used against the platform or reaches a person: an operator IP range
/// (<c>api_server_authorized_ip_ranges</c>, <c>key_vault_allowed_ip_ranges</c>) or an e-mail address
/// (<c>oncall_email_receivers</c>, or any address anywhere in the file). Those values go through Octopus variables
/// (<c>TF_VAR_*</c>) if they are ever used. Offline: reads the tracked files only.
/// </summary>
internal static partial class CommittedTfvarsGuard
{
    /// <summary>Variables that must stay empty in a committed tfvars file.</summary>
    public static IReadOnlyList<string> MustStayEmpty { get; } =
        ["api_server_authorized_ip_ranges", "key_vault_allowed_ip_ranges", "oncall_email_receivers"];

    /// <summary>Checks every tracked <c>*.tfvars</c> file of the tree.</summary>
    /// <param name="tree">The repository tree.</param>
    public static IReadOnlyList<string> Check(BoundaryTree tree)
    {
        var findings = new List<string>();
        foreach (var file in tree.TrackedFiles(".tfvars"))
        {
            var lines = tree.Lines(file) ?? [];
            for (var index = 0; index < lines.Count; index++)
            {
                if (EmailAddress().IsMatch(lines[index]))
                {
                    findings.Add($"{file}:{index + 1}: an e-mail address (the file is public; use an Octopus variable TF_VAR_*)");
                }
            }

            var code = string.Join('\n', lines.Select(StripComment));
            foreach (var name in MustStayEmpty)
            {
                if (NonEmptyValue(code, name))
                {
                    findings.Add($"{file}: {name} is not empty (the file is public; use an Octopus variable TF_VAR_*)");
                }
            }
        }

        return findings;
    }

    private static string StripComment(string line)
    {
        var quotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                quotes = !quotes;
            }
            else if (!quotes && (line[index] == '#' || (line[index] == '/' && index + 1 < line.Length && line[index + 1] == '/')))
            {
                return line[..index];
            }
        }

        return line;
    }

    // The value of `name = [ ... ]` or `name = { ... }` (also spread over lines) is non-empty when anything but whitespace
    // sits between the brackets. A value that is not a list or map (`name = null`) counts as empty.
    private static bool NonEmptyValue(string code, string name)
    {
        foreach (Match start in Assignment(name).Matches(code))
        {
            var open = start.Groups[1].Value[0];
            var close = open == '[' ? ']' : '}';
            var depth = 1;
            var body = new System.Text.StringBuilder();
            for (var index = start.Index + start.Length; index < code.Length && depth > 0; index++)
            {
                depth += code[index] == open ? 1 : code[index] == close ? -1 : 0;
                if (depth > 0)
                {
                    body.Append(code[index]);
                }
            }

            if (body.ToString().Any(character => !char.IsWhiteSpace(character)))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex Assignment(string name) =>
        new($@"(?m)^\s*{Regex.Escape(name)}\s*=\s*([\[{{])", RegexOptions.CultureInvariant);

    [GeneratedRegex("""[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}""")]
    private static partial Regex EmailAddress();
}
