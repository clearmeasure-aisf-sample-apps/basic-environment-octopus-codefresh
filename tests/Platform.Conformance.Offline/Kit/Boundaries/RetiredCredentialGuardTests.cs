using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-007, the retired-credential guard: the real repository passes, and small trees in a temporary folder show that
/// each retired name outside the allow-list fails (Markdown included), that the history section of the runbook passes
/// (the conformance token and its Octopus variable and Terraform input are retired there too, #44), that a name outside the
/// history markers fails, and that a stale allow-list entry fails.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class RetiredCredentialGuardTests
{
    private string root = null!;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("retired-credentials-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>The repository names no retired credential outside its allow-list, and no allow-list entry is stale.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_RepositoryTree_Passes()
    {
        var result = RetiredCredentialGuard.Check(new BoundaryTree(KitToolbox.RepositoryRoot));

        result.Findings.ShouldBeEmpty(result.Report());
    }

    /// <summary>The guard's own names are the retired ones (three board and conformance tokens, and the conformance runbook variable and input), and the allow-list carries a reason for each entry.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_ListAllowances_EveryEntry_HasAReasonAndOnlyRetiredNames()
    {
        RetiredCredentialGuard.Names.ShouldBe(
        [
            RetiredCredentialGuard.ProjectsPat, RetiredCredentialGuard.SampleAppsPat, RetiredCredentialGuard.ConformanceGitHubToken,
            RetiredCredentialGuard.E2eGitHubToken, RetiredCredentialGuard.E2eGitHubTokenInput,
        ]);
        foreach (var entry in RetiredCredentialGuard.Allowances)
        {
            entry.Reason.ShouldNotBeNullOrWhiteSpace(entry.Path);
            entry.Names.ShouldNotBeEmpty(entry.Path);
            entry.Names.ShouldAllBe(name => RetiredCredentialGuard.Names.Contains(name), entry.Path);
        }

        // #44: the conformance token is retired too, so no Codefresh-context file may name it any more; every retired name is
        // allowed in the history section of the runbook alone.
        foreach (var name in RetiredCredentialGuard.Names)
        {
            RetiredCredentialGuard.Allowances
                .Where(entry => entry.Names.Contains(name) && !entry.Path.StartsWith("tests/", StringComparison.Ordinal))
                .Select(entry => entry.Path)
                .ShouldBe([RetiredCredentialGuard.RunbookPath], name);
        }

        RetiredCredentialGuard.Allowances.Where(entry => !entry.Path.StartsWith("tests/", StringComparison.Ordinal))
            .ShouldAllBe(entry => entry.HistoryOnly);
    }

    /// <summary>Each retired name fails in a workflow, a script, a JSON file and a Markdown file, one finding per line.</summary>
    [TestCase(RetiredCredentialGuard.ProjectsPat)]
    [TestCase(RetiredCredentialGuard.SampleAppsPat)]
    [TestCase(RetiredCredentialGuard.ConformanceGitHubToken)]
    [TestCase(RetiredCredentialGuard.E2eGitHubToken)]
    [TestCase(RetiredCredentialGuard.E2eGitHubTokenInput)]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_NameOutsideTheAllowList_FailsEvenInMarkdown(string name)
    {
        Write(".github/workflows/x.yml", "env:", $"  T: ${{{{ secrets.{name} }}}}");
        Write("scripts/y.ps1", $"$token = $env:{name}");
        Write("settings.json", $"{{\"tokenEnv\": [\"{name}\"]}}");
        Write("docs/notes.md", "# Notes", $"Set `{name}` before running.");
        Write("docs/clean.md", "Nothing to see. MY_" + name + "_SUFFIX and " + name + "S are other names.");

        var findings = Findings();

        findings.ShouldBe([".github/workflows/x.yml:2", "docs/notes.md:2", "scripts/y.ps1:1", "settings.json:1"], ignoreOrder: true);
    }

    /// <summary>The history section of the runbook may name the retired tokens; outside its markers the same name fails.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_HistorySectionOfTheRunbook_PassesAndOutsideItFails()
    {
        Write(RetiredCredentialGuard.RunbookPath,
            "# Credential rotation",
            "The old token was " + RetiredCredentialGuard.ProjectsPat + " (outside the section).",
            RetiredCredentialGuard.HistoryStart,
            "## Retired credentials (history)",
            AllNames + " were revoked.",
            RetiredCredentialGuard.HistoryEnd,
            "After the section: " + RetiredCredentialGuard.SampleAppsPat);

        var findings = RetiredCredentialGuard.Check(new BoundaryTree(root), OnlyRunbook).Findings.Select(finding => $"{finding.Path}:{finding.Line}").ToArray();

        findings.ShouldBe([$"{RetiredCredentialGuard.RunbookPath}:2", $"{RetiredCredentialGuard.RunbookPath}:7"], ignoreOrder: true);

        Write(RetiredCredentialGuard.RunbookPath,
            RetiredCredentialGuard.HistoryStart,
            AllNames + " were revoked.",
            RetiredCredentialGuard.HistoryEnd);
        RetiredCredentialGuard.Check(new BoundaryTree(root), OnlyRunbook).Findings.ShouldBeEmpty();
    }

    /// <summary>A history section that is never closed fails, so it cannot swallow the rest of the file.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_UnclosedHistorySection_Fails()
    {
        Write(RetiredCredentialGuard.RunbookPath,
            RetiredCredentialGuard.HistoryStart,
            AllNames + " were revoked.");

        var findings = RetiredCredentialGuard.Check(new BoundaryTree(root), OnlyRunbook).Findings;

        findings.ShouldHaveSingleItem().Text.ShouldContain(RetiredCredentialGuard.HistoryEnd);
    }

    /// <summary>
    /// The conformance token, its Octopus variable and its Terraform input (#44) fail everywhere outside the history section of
    /// the runbook (the three files that used to describe the Codefresh context included); inside the section they pass.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_ConformanceToken_FailsEverywhereOutsideTheHistorySection()
    {
        var names = new[] { RetiredCredentialGuard.ConformanceGitHubToken, RetiredCredentialGuard.E2eGitHubToken, RetiredCredentialGuard.E2eGitHubTokenInput };
        Write("codefresh/platform/integrations.yaml", $"GITHUB_TOKEN: {{fromEnv: {names[0]}}}");
        Write("docs/preview-codefresh.md", $"| `{names[0]}` | context |");
        Write("docs/runbooks/conformance.md", $"Seed `{names[1]}` with `{names[0]}` and set {names[2]}.");
        Write(".octopus/platform-infrastructure/runbooks/e2e-pass.ocl", $"$t = $OctopusParameters['{names[1]}']");
        Write("octopus/apply.ps1", $"# needs {names[2]}");

        var outside = RetiredCredentialGuard.Check(new BoundaryTree(root), OnlyRunbook).Findings
            .Where(finding => finding.Text.StartsWith("names the retired credential", StringComparison.Ordinal))
            .Select(finding => $"{finding.Path}:{finding.Line}").ToArray();

        outside.ShouldBe(
        [
            "codefresh/platform/integrations.yaml:1", "docs/preview-codefresh.md:1", "docs/runbooks/conformance.md:1",
            "docs/runbooks/conformance.md:1", "docs/runbooks/conformance.md:1", ".octopus/platform-infrastructure/runbooks/e2e-pass.ocl:1", "octopus/apply.ps1:1",
        ], ignoreOrder: true);

        Write(RetiredCredentialGuard.RunbookPath, RetiredCredentialGuard.HistoryStart, $"{names[0]}, {names[1]} and {names[2]} were retired.", RetiredCredentialGuard.HistoryEnd);
        RetiredCredentialGuard.Check(new BoundaryTree(root), OnlyRunbook).Findings
            .Where(finding => finding.Path == RetiredCredentialGuard.RunbookPath && finding.Text.StartsWith("names the retired credential", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    /// <summary>An allow-list entry whose file no longer names the credential, or whose file is gone, fails as stale.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_StaleAllowListEntry_Fails()
    {
        var name = RetiredCredentialGuard.ConformanceGitHubToken;
        Write("docs/still.md", $"Mentions {name}.");
        Write("docs/moved.md", "No longer mentions it.");
        RetiredCredentialAllowance[] entries =
        [
            new("docs/still.md", [name], "still describes the context"),
            new("docs/moved.md", [name], "used to describe the context"),
            new("docs/deleted.md", [name], "file removed"),
        ];

        var findings = RetiredCredentialGuard.Check(new BoundaryTree(root), entries).Findings;

        findings.Select(finding => finding.Path).ShouldBe(["docs/moved.md", "docs/deleted.md"], ignoreOrder: true);
        findings.ShouldAllBe(finding => finding.Text.StartsWith("stale allow-list entry", StringComparison.Ordinal));
    }

    /// <summary>A binary file that happens to hold the bytes of a name is not text and never matches.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardRetiredCredentials_BinaryFile_IsIgnored()
    {
        File.WriteAllBytes(Path.Combine(root, "image.png"), [0, 1, 2, .. System.Text.Encoding.ASCII.GetBytes(RetiredCredentialGuard.ProjectsPat)]);

        RetiredCredentialGuard.Check(new BoundaryTree(root), []).Findings.ShouldBeEmpty();
    }

    // Every retired name, as the history section of the runbook must name them all (the allowance of the runbook covers each).
    private static string AllNames => string.Join(", ", RetiredCredentialGuard.Names);

    private static IReadOnlyList<RetiredCredentialAllowance> OnlyRunbook =>
        RetiredCredentialGuard.Allowances.Where(entry => entry.Path == RetiredCredentialGuard.RunbookPath).ToArray();

    private string[] Findings() =>
        RetiredCredentialGuard.Check(new BoundaryTree(root), []).Findings.Select(finding => $"{finding.Path}:{finding.Line}").ToArray();

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }
}
