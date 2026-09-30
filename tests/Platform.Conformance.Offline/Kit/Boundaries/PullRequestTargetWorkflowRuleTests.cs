using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006 (public-repository half, #47): <c>pull_request_target</c> is used safely or not at all. Small trees in a
/// temporary folder show that an unlisted workflow using the trigger fails, that the listed board workflow in its safe form
/// passes, and that a listed workflow fails on each way it would run pull request code or expand attacker-controlled text;
/// the real tree must pass.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PullRequestTargetWorkflowRuleTests
{
    private const string Board = ".github/workflows/project-board.yml";

    private string root = null!;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("pr-target-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>Integration: the real workflows use pull_request_target only in the safe, listed form.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_CheckWorkflows_RepositoryTree_UsePullRequestTargetOnlySafely()
    {
        var result = PullRequestTargetWorkflowRule.Check(new BoundaryTree(KitToolbox.RepositoryRoot));

        result.Findings.ShouldBeEmpty(result.Report());
    }

    /// <summary>Unit: the safe board workflow passes, including a comment that names what it does not do.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_CheckWorkflows_SafeBoardWorkflow_Passes()
    {
        Write(Board,
            "# pull_request_target runs from the default branch; it never runs actions/checkout or reads github.head_ref",
            "on:",
            "  issues:",
            "    types: [opened]",
            "  pull_request_target:",
            "    types: [opened, closed]",
            "concurrency:",
            "  group: board-${{ github.event.pull_request.number || github.event.issue.number }}",
            "jobs:",
            "  board:",
            "    if: github.event_name != 'pull_request_target' || github.event.pull_request.merged == true",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - shell: pwsh",
            "        run: |",
            "          $payload = Get-Content $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json",
            "          $body = [string] $payload.pull_request.body");
        Write(".github/workflows/other.yml", "on:", "  workflow_dispatch:", "jobs: {}");

        Findings().ShouldBeEmpty();
    }

    /// <summary>Unit: a workflow outside the TB24 list fails on every line that names the trigger, in any spelling.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_CheckWorkflows_UnlistedWorkflowUsingTheTrigger_Fails()
    {
        Write(".github/workflows/label.yml", "on:", "  pull_request_target:", "    types: [opened]", "jobs: {}");
        Write(".github/workflows/inline.yml", "on: pull_request_target", "jobs: {}");
        Write(".github/workflows/list.yml", "on: [push, pull_request_target]", "jobs: {}");
        Write(".github/workflows/plain.yml", "on:", "  pull_request:", "jobs: {}");

        Findings().ShouldBe(
            [".github/workflows/inline.yml:1", ".github/workflows/label.yml:2", ".github/workflows/list.yml:1"], ignoreOrder: true);
    }

    /// <summary>Unit: a listed workflow fails when it checks out or fetches pull request code.</summary>
    /// <param name="line">A step line.</param>
    [TestCase("      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683")]
    [TestCase("      - run: gh pr checkout 5")]
    [TestCase("      - run: git clone https://example.invalid/repo.git")]
    [TestCase("      - run: git fetch origin pull/5/head")]
    [TestCase("      - run: git checkout FETCH_HEAD")]
    [Capability("CAP-KIT-006")]
    public void Should_CheckWorkflows_ListedWorkflowCheckingOutCode_Fails(string line)
    {
        Write(Board, "on:", "  pull_request_target:", "jobs:", "  board:", "    steps:", line);

        Findings().ShouldBe([$"{Board}:6"]);
    }

    /// <summary>Unit: a listed workflow fails when it reads the pull request head or expands attacker-controlled text.</summary>
    /// <param name="line">A step line.</param>
    [TestCase("      - run: echo ${{ github.head_ref }}")]
    [TestCase("      - run: echo ${{ github.event.pull_request.head.ref }}")]
    [TestCase("        with: { ref: ${{ github.event.pull_request.head.sha }} }")]
    [TestCase("      - run: echo \"${{ github.event.pull_request.title }}\"")]
    [TestCase("      - run: echo \"${{ github.event.pull_request.body }}\"")]
    [TestCase("      - run: echo \"${{ github.event.issue.title }}\"")]
    [TestCase("      - run: echo \"${{ github.event.comment.body }}\"")]
    [TestCase("      - run: echo ${{ github.event.head_commit.message }}")]
    [TestCase("      - run: echo refs/pull/5/merge")]
    [Capability("CAP-KIT-006")]
    public void Should_CheckWorkflows_ListedWorkflowReadingAttackerText_Fails(string line)
    {
        Write(Board, "on:", "  pull_request_target:", "jobs:", "  board:", "    steps:", line);

        Findings().ShouldBe([$"{Board}:6"]);
    }

    /// <summary>Unit: a workflow that never uses the trigger may check out code; the rule does not reach it (TB24 governs it).</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_CheckWorkflows_WorkflowWithoutTheTrigger_IsNotJudgedByThisRule()
    {
        Write(".github/workflows/build.yml",
            "on:", "  pull_request:", "jobs:", "  b:", "    steps:", "      - uses: actions/checkout@v4", "      - run: echo ${{ github.head_ref }}");

        Findings().ShouldBeEmpty();
    }

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    private string[] Findings() =>
        PullRequestTargetWorkflowRule.Check(new BoundaryTree(root)).Findings.Select(finding => $"{finding.Path}:{finding.Line}").ToArray();
}
