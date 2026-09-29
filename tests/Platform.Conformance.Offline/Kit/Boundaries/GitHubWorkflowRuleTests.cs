using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006 for TB24: GitHub runs no platform workflow. Small trees in a temporary folder show that an unlisted
/// workflow fails the rule, that a listed board-only workflow fails on each way out of its lane (checkout, an unpinned
/// action, a build or cluster tool, a write permission, another secret, a self-hosted runner, a push or pull_request
/// trigger), and that a board-only workflow in its lane passes.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class GitHubWorkflowRuleTests
{
    private const string Board = ".github/workflows/project-board.yml";

    private string root = null!;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("github-workflows-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>TB24: a workflow missing from the exception list fails; the listed board workflow in its lane passes.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_UnlistedWorkflow_FailsAndBoardOnlyWorkflowPasses()
    {
        Write(".github/workflows/build.yml", "on:", "  workflow_dispatch:", "permissions:", "  contents: read", "jobs: {}");
        Write(Board,
            "# dotnet build and kubectl apply in a comment are fine",
            "on:",
            "  issues:",
            "    types: [opened]",
            "  pull_request_target:",
            "    types: [opened]",
            "  workflow_dispatch:",
            "    inputs:",
            "      status:",
            "        type: string",
            "permissions:",
            "  contents: read",
            "jobs:",
            "  board:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - uses: actions/github-script@f28e40c7f34bde8b3046d885e986cb6290c5673b",
            "        env:",
            "          PROJECTS_PAT: ${{ secrets.PROJECTS_PAT }}");

        Findings().ShouldBe([".github/workflows/build.yml:"]);
    }

    /// <summary>TB24: a listed workflow that leaves its lane fails once per offending line.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_ListedWorkflowLeavingItsLane_FailsOnEachLine()
    {
        Write(Board,
            "on:",
            "  push:",
            "  pull_request:",
            "  pull_request_target:",
            "jobs:",
            "  board:",
            "    runs-on: [self-hosted, linux]",
            "    permissions:",
            "      contents: write",
            "    steps:",
            "      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683",
            "      - uses: actions/github-script@v7",
            "      - run: dotnet build",
            "      - run: kubectl apply -f x.yaml",
            "        env:",
            "          TOKEN: ${{ secrets.GITHUB_TOKEN }}");

        Findings().ShouldBe(
            [
                $"{Board}:", $"{Board}:2", $"{Board}:3", $"{Board}:7", $"{Board}:9", $"{Board}:11", $"{Board}:12", $"{Board}:13",
                $"{Board}:14", $"{Board}:16",
            ],
            ignoreOrder: true);
    }

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    private string[] Findings()
    {
        var result = ToolBoundaryRules.Check(new BoundaryTree(root), GitHubWorkflowRule.Id);
        result.Skipped.ShouldBeFalse(result.Report());
        return result.Findings.Select(finding => $"{finding.Path}:{finding.Line}").ToArray();
    }
}
