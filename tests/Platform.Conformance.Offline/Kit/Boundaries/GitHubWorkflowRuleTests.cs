using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006 for TB24: GitHub runs no platform workflow. Small trees in a temporary folder show that an unlisted
/// workflow fails the rule, that a listed board-only workflow fails on each way out of its lane (checkout, an unpinned
/// action, a build or cluster tool, a write permission, another secret, a self-hosted runner, a push or pull_request_target
/// trigger), and that a board-only workflow in its lane passes. The alert-only lane (the release stall check, #86) is shown
/// the same way: schedule and manual runs, a pinned checkout without persisted credentials and <c>issues: write</c> pass;
/// every other event, permission, secret or tool fails, and the board workflow gains none of it.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class GitHubWorkflowRuleTests
{
    private const string Board = ".github/workflows/project-board.yml";
    private const string Alert = ".github/workflows/release-stall-check.yml";
    private const string PinnedCheckout = "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683";

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
            "  pull_request:",
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
            "    if: github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository",
            "    steps:",
            "      - uses: actions/github-script@f28e40c7f34bde8b3046d885e986cb6290c5673b",
            "        env:",
            "          BOARD_APP_ID: ${{ secrets.BOARD_APP_ID }}");

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
                $"{Board}:", $"{Board}:2", $"{Board}:3", $"{Board}:4", $"{Board}:7", $"{Board}:9", $"{Board}:11", $"{Board}:12", $"{Board}:13",
                $"{Board}:14", $"{Board}:16",
            ],
            ignoreOrder: true);
    }

    /// <summary>TB24: the board workflow may read the App credentials and mint a token with a SHA-pinned action, but no other secret.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_AppTokenWorkflow_PassesOnlyWithPinnedActionAndBoardSecrets()
    {
        Write(Board,
            "on:",
            "  repository_dispatch:",
            "    types: [board-status]",
            "permissions:",
            "  contents: read",
            "jobs:",
            "  board:",
            "    runs-on: ubuntu-latest",
            "    env:",
            "      BOARD_APP_ID: ${{ secrets.BOARD_APP_ID }}",
            "      BOARD_APP_PRIVATE_KEY: ${{ secrets.BOARD_APP_PRIVATE_KEY }}",
            "    steps:",
            "      - uses: actions/create-github-app-token@fee1f7d63c2ff003460e3d139729b119787bc349 # v2.2.2",
            "        with:",
            "          app-id: ${{ env.BOARD_APP_ID }}",
            "          private-key: ${{ env.BOARD_APP_PRIVATE_KEY }}",
            "      - run: echo ${{ secrets.BOARD_APP_ID }}");
        Findings().ShouldBeEmpty();

        Write(Board,
            "on:",
            "  repository_dispatch:",
            "permissions:",
            "  contents: read",
            "jobs:",
            "  board:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - uses: actions/create-github-app-token@v2",
            "      - run: echo ${{ secrets.OTHER_SECRET }}");
        Findings().ShouldBe([$"{Board}:9", $"{Board}:10"], ignoreOrder: true);
    }

    /// <summary>TB24: a personal access token secret (the retired one of the board workflow) is rejected; only the two App secrets are admitted.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_RetiredProjectsPatSecret_IsRejectedAndOnlyAppSecretsAreAllowed()
    {
        GitHubWorkflowRule.AllowedSecrets.ShouldBe(["BOARD_APP_ID", "BOARD_APP_PRIVATE_KEY"], ignoreOrder: true);

        Write(Board,
            "on:",
            "  repository_dispatch:",
            "permissions:",
            "  contents: read",
            "jobs:",
            "  board:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            $"      - run: echo ${{{{ secrets.{RetiredCredentialGuard.ProjectsPat} }}}}");
        Findings().ShouldBe([$"{Board}:9"]);
    }

    /// <summary>The real board workflow reads only the App secrets, has no PAT fallback and no pull_request_target, and fails loudly without the App.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_BoardWorkflow_Repository_HasNoPatFallbackAndFailsLoudlyWithoutTheApp()
    {
        var workflow = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, ".github", "workflows", "project-board.yml"));

        workflow.ShouldNotContain(RetiredCredentialGuard.ProjectsPat);
        var code = string.Join(Environment.NewLine, workflow.ReplaceLineEndings().Split(Environment.NewLine).Where(line => !line.TrimStart().StartsWith('#')));
        code.ShouldNotContain("pull_request_target");
        code.ShouldContain("GitHub App token unavailable (mint outcome:");
        code.ShouldContain("BOARD_APP_ID and BOARD_APP_PRIVATE_KEY");
        code.ShouldContain("Board credential: GitHub App installation token (BOARD_APP_ID / BOARD_APP_PRIVATE_KEY).");
        System.Text.RegularExpressions.Regex.Matches(code, @"secrets\.(\w+)").Select(match => match.Groups[1].Value).Distinct()
            .ShouldBe(["BOARD_APP_ID", "BOARD_APP_PRIVATE_KEY"], ignoreOrder: true);
    }

    /// <summary>TB24: the alert-only workflow may run on a schedule, check out pinned without persisted credentials and write issues; the board workflow may do none of it.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_AlertOnlyWorkflow_PassesInItsLaneAndTheBoardWorkflowGainsNothing()
    {
        string[] alert =
        [
            "on:",
            "  schedule:",
            "    - cron: \"17 * * * *\"",
            "  workflow_dispatch:",
            "permissions:",
            "  contents: read",
            "  issues: write",
            "jobs:",
            "  check:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            $"      - uses: {PinnedCheckout} # v4.2.2",
            "        with:",
            "          fetch-depth: 0",
            "          persist-credentials: false",
            "      - shell: pwsh",
            "        env:",
            "          GITHUB_TOKEN: ${{ github.token }}",
            "        run: ./scripts/release/release-stall-check.ps1 -Issues",
        ];

        Write(Alert, alert);
        Findings().ShouldBeEmpty();

        Write(Board, alert);
        Findings().ShouldBe([$"{Board}:2", $"{Board}:7", $"{Board}:12"], ignoreOrder: true);
    }

    /// <summary>TB24: an alert-only workflow that leaves its lane fails once per offending line.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_AlertOnlyWorkflowLeavingItsLane_FailsOnEachLine()
    {
        Write(Alert,
            "on:",
            "  schedule:",
            "    - cron: \"17 * * * *\"",
            "  pull_request:",
            "  issues:",
            "  push:",
            "permissions:",
            "  contents: write",
            "  issues: write",
            "  pull-requests: write",
            "jobs:",
            "  check:",
            "    runs-on: [self-hosted]",
            "    permissions: { issues: write, contents: write }",
            "    steps:",
            $"      - uses: {PinnedCheckout}",
            "      - uses: actions/checkout@v4",
            "      - run: terraform apply",
            "        env:",
            "          TOKEN: ${{ secrets.BOARD_APP_PRIVATE_KEY }}",
            "          OTHER: ${{ secrets.GITHUB_TOKEN }}");

        Findings().ShouldBe(
            [
                $"{Alert}:4", $"{Alert}:5", $"{Alert}:6", $"{Alert}:8", $"{Alert}:10", $"{Alert}:13", $"{Alert}:14", $"{Alert}:16", $"{Alert}:17",
                $"{Alert}:18", $"{Alert}:20", $"{Alert}:21",
            ],
            ignoreOrder: true);
    }

    /// <summary>The real stall workflow runs hourly and by hand, holds contents: read and issues: write, reads no secret, and only starts the script.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_StallWorkflow_Repository_RunsOnlyTheScriptWithTheDefaultTokenAndNoSecret()
    {
        var workflow = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, ".github", "workflows", "release-stall-check.yml"));
        var code = workflow.ReplaceLineEndings("\n").Split('\n').Where(line => !line.TrimStart().StartsWith('#')).ToArray();
        var text = string.Join('\n', code);

        text.ShouldNotContain("secrets.");
        text.ShouldContain("GITHUB_TOKEN: ${{ github.token }}");
        text.ShouldContain("fetch-depth: 0", customMessage: "a shallow history would hide pins and close issues of releases that still wait");
        text.ShouldContain("persist-credentials: false");
        code.Where(line => line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal))
            .Select(line => line.Trim()).TakeWhile(line => line != "check:")
            .ShouldBe(["schedule:", "workflow_dispatch:", "contents: read", "issues: write", "group: release-stall-check", "cancel-in-progress: false"]);
        System.Text.RegularExpressions.Regex.IsMatch(text, @"cron: ""\d+ \* \* \* \*""").ShouldBeTrue("the check runs every hour");
        code.Where(line => line.TrimStart().StartsWith("run:", StringComparison.Ordinal)).Select(line => line.Trim())
            .ShouldBe(["run: ./scripts/release/release-stall-check.ps1 -Issues -Ref origin/main -DryRun:($env:DRY_RUN -eq 'true')"]);
        File.Exists(Path.Combine(KitToolbox.RepositoryRoot, "scripts", "release", "release-stall-check.ps1")).ShouldBeTrue();
        GitHubWorkflowRule.Exceptions.Select(exception => (exception.Path, exception.Lane.Name))
            .ShouldBe([(Board, "board-only"), (Alert, "alert-only")]);
    }

    /// <summary>TB24: the pull_request trigger needs the same-repository guard; pull_request_target is never allowed.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB24_PullRequestTrigger_RequiresSameRepositoryGuardAndRejectsPullRequestTarget()
    {
        string[] header = ["on:", "  pull_request:", "    types: [opened]", "permissions:", "  contents: read", "jobs:", "  board:", "    runs-on: ubuntu-latest"];
        string[] steps = ["    steps:", "      - run: echo board"];

        Write(Board, [.. header, .. steps]);
        Findings().ShouldBe([$"{Board}:2"]);

        Write(Board, [.. header, "    if: github.event.pull_request.head.repo.full_name != github.repository", .. steps]);
        Findings().ShouldBe([$"{Board}:2"]);

        Write(Board, [.. header, "    if: github.event.pull_request.head.repo.full_name == github.repository", .. steps]);
        Findings().ShouldBeEmpty();

        Write(Board, "on:", "  pull_request_target:", "permissions:", "  contents: read", "jobs: {}");
        Findings().ShouldBe([$"{Board}:2"]);
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
