using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-007 (handling half): no script puts a secret on a command line, where other processes of the node can read it
/// (docs/scripting.md, "Secrets"). Every tracked PowerShell script and OCL file (the inline Octopus scripts) is read for
/// the arguments that carried secrets before: an Authorization header given to curl, a password, client secret or
/// federated token given to az, a Sigstore ID token given to cosign, and sqlcmd's <c>-P</c>. The way that stays allowed
/// is named in each finding. Comment lines are skipped.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class SecretArgumentTests
{
    /// <summary>No tracked script passes a secret as an argument.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_ReadScripts_RepositoryTree_PassNoSecretAsAnArgument()
    {
        var tree = new BoundaryTree(KitToolbox.RepositoryRoot);
        var files = new[] { ".ps1", ".psm1", ".ocl" }.SelectMany(tree.TrackedFiles).ToArray();

        var findings = files.SelectMany(file => Findings(file, File.ReadAllLines(Path.Combine(KitToolbox.RepositoryRoot, file)))).ToArray();

        files.ShouldNotBeEmpty("no PowerShell script or OCL file found");
        findings.ShouldBeEmpty();
    }

    /// <summary>Each rule finds its secret argument and lets the stdin, file and environment forms pass.</summary>
    /// <param name="line">A script line.</param>
    /// <param name="found">Whether a rule reports it.</param>
    [TestCase("""$r = curl -fsS -H "Authorization: $($env:TOKEN)" "$url" """, true)]
    [TestCase("""curl --header "Authorization: Bearer $token" $url""", true)]
    [TestCase("""$r = "header = `"Authorization: $token`"" | curl --config - -fsS $url""", false)]
    [TestCase("""    --password $credential.Password `""", true)]
    [TestCase("""    --password '@-' `""", false)]
    [TestCase("""az login --service-principal --client-secret $secret""", true)]
    [TestCase("""az login --federated-token $jwt""", true)]
    [TestCase("""    --identity-token $token $reference""", true)]
    [TestCase("""    --identity-token $tokenFile $Reference""", false)]
    [TestCase("""sqlcmd -S db-0 -U sa -P $password -Q 'SELECT 1'""", true)]
    [TestCase("""# curl -H "Authorization: $token" is how it was done before""", false)]
    [Capability("CAP-KIT-007")]
    public void Should_ReadLine_SecretArgumentRules_FindOnlyTheArgumentForms(string line, bool found)
    {
        var findings = Findings("sample.ps1", [line]).ToArray();

        (findings.Length > 0).ShouldBe(found, string.Join("; ", findings));
    }

    private static IEnumerable<string> Findings(string file, IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var text = line.TrimStart();
            if (text.StartsWith('#') || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (rule, allowed) in Rules)
            {
                if (rule.Match(line) is { Success: true } match && !IsFileArgument(match))
                {
                    yield return $"{file}:{index + 1}: '{match.Value.Trim()}' puts a secret on a command line; {allowed}";
                }
            }
        }
    }

    // A variable named ...File or ...Path holds a path to a private file, not the secret itself.
    private static bool IsFileArgument(Match match) =>
        match.Groups["variable"] is { Success: true } variable
        && (variable.Value.EndsWith("File", StringComparison.Ordinal) || variable.Value.EndsWith("Path", StringComparison.Ordinal));

    private static readonly (Regex Rule, string Allowed)[] Rules =
    [
        (HeaderArgument(), "give curl the header in its configuration on standard input (curl --config -)"),
        (AzSecretArgument(), "give az the value on standard input ('@-') or from a private file ('@<path>')"),
        (IdentityTokenArgument(), "write the token to a private file and pass its path (cosign accepts a path)"),
        (SqlcmdPasswordArgument(), "set SQLCMDPASSWORD in the environment of sqlcmd instead"),
    ];

    [GeneratedRegex("""(-H|--header)\s+["']?Authorization""")]
    private static partial Regex HeaderArgument();

    [GeneratedRegex("""--(password|client-secret|federated-token)\s+["']?\$""")]
    private static partial Regex AzSecretArgument();

    [GeneratedRegex("""--identity-token\s+["']?\$(?<variable>\w+)""")]
    private static partial Regex IdentityTokenArgument();

    [GeneratedRegex("""\bsqlcmd\b.*\s-P\s""")]
    private static partial Regex SqlcmdPasswordArgument();
}
