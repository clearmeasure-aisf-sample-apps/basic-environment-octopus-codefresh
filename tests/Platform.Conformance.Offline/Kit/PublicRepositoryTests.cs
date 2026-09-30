using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// The environment repository is public (design R36), so everything committed is world-readable. Two checks need no
/// secret and no external tool: no tracked file holds a value shaped like a credential (a backstop that runs where
/// gitleaks is missing; CAP-KIT-007 keeps gitleaks as the full scan), and no workflow listens to
/// <c>pull_request_target</c>, which would run with secrets on a pull request from any fork (TB24).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PublicRepositoryTests
{
    private static readonly string[] PrunedFolders = [".git", ".terraform", "node_modules", "bin", "obj"];

    private static readonly (string Name, Regex Pattern)[] CredentialShapes =
    [
        ("private key block", new Regex("-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex("\\b(gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\\b", RegexOptions.CultureInvariant)),
        ("Octopus API key", new Regex("\\bAPI-[A-Z0-9]{20,}\\b", RegexOptions.CultureInvariant)),
        ("AWS access key ID", new Regex("\\bAKIA[0-9A-Z]{16}\\b", RegexOptions.CultureInvariant)),
        ("Slack token", new Regex("\\bxox[baprs]-[A-Za-z0-9-]{10,}\\b", RegexOptions.CultureInvariant)),
    ];

    private static readonly Regex Comment = new("^\\s*#", RegexOptions.CultureInvariant);

    private string root = null!;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("public-repo-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>A file with each credential shape is reported once per shape and line; placeholders and clean files pass.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_FindCredentialShapes_EachShape_IsReportedAndPlaceholdersPass()
    {
        Write("scripts/a.ps1", "# clean", "$token = $env:GITHUB_TOKEN", "$ref = 'ghp_<token>'", "$id = 'API-<key>'");
        Write("docs/b.md", "text", "-----BEGIN " + "RSA PRIVATE KEY-----");
        Write("c.yaml", "token: " + "ghp_" + new string('a', 36), "key: " + "API-" + new string('A', 27));
        Write("node_modules/d.txt", "ghp_" + new string('b', 36));
        File.WriteAllBytes(Path.Combine(root, "e.bin"), [0, .. "AKIA"u8.ToArray(), .. new string('A', 16).Select(c => (byte)c)]);

        FindCredentialShapes(root).ShouldBe(
        [
            "c.yaml:1: GitHub token",
            "c.yaml:2: Octopus API key",
            "docs/b.md:2: private key block",
        ]);
    }

    /// <summary>CAP-KIT-007: no tracked file of the repository holds a credential-shaped value.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_FindCredentialShapes_RepositoryTree_FindsNone()
    {
        FindCredentialShapes(KitToolbox.RepositoryRoot).ShouldBeEmpty("a public repository must hold no credential; remove the value and rotate it (docs/runbooks/public-repository.md)");
    }

    /// <summary>A workflow that names pull_request_target on a non-comment line is reported; comments and other events pass.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_FindPullRequestTarget_WorkflowsInATempTree_ReportsOnlyNonCommentLines()
    {
        Write(".github/workflows/a.yml", "# pull_request_target is banned", "on:", "  pull_request:", "  pull_request_target:");
        Write(".github/workflows/b.yaml", "on:", "  issues:", "jobs:", "  x:", "    if: github.event_name != 'pull_request_target'");
        Write(".github/workflows/c.yml", "on:", "  workflow_dispatch:");
        Write("docs/d.yml", "  pull_request_target:");

        FindPullRequestTarget(root).ShouldBe([".github/workflows/a.yml:4", ".github/workflows/b.yaml:5"]);
    }

    /// <summary>TB24: no workflow of the repository listens to pull_request_target.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_FindPullRequestTarget_RepositoryTree_FindsNone()
    {
        FindPullRequestTarget(KitToolbox.RepositoryRoot).ShouldBeEmpty("pull_request_target runs with secrets on a pull request from any fork of a public repository: use pull_request with a same-repository check");
    }

    private static List<string> FindCredentialShapes(string folder)
    {
        var findings = new List<string>();
        foreach (var file in Files(folder))
        {
            var bytes = File.ReadAllBytes(file);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue;
            }

            var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var (name, pattern) in CredentialShapes.Where(shape => shape.Pattern.IsMatch(lines[index])))
                {
                    findings.Add($"{Relative(folder, file)}:{index + 1}: {name}");
                }
            }
        }

        return findings;
    }

    private static List<string> FindPullRequestTarget(string folder)
    {
        var findings = new List<string>();
        var workflows = Path.Combine(folder, ".github", "workflows");
        if (!Directory.Exists(workflows))
        {
            return findings;
        }

        foreach (var file in Directory.EnumerateFiles(workflows).Where(path => path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!Comment.IsMatch(lines[index]) && lines[index].Contains("pull_request_target", StringComparison.Ordinal))
                {
                    findings.Add($"{Relative(folder, file)}:{index + 1}");
                }
            }
        }

        return findings;
    }

    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(path => !Relative(folder, path).Split('/').SkipLast(1).Any(part => PrunedFolders.Contains(part, StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal);

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
