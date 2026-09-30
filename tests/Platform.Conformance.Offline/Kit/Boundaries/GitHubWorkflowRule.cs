using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>A GitHub Actions workflow of this repository that may exist, with its reason.</summary>
/// <param name="Path">Repository-relative path under <c>.github/workflows/</c>.</param>
/// <param name="Reason">Why it runs on GitHub Actions and in no other lane.</param>
internal sealed record WorkflowException(string Path, string Reason);

/// <summary>
/// TB24: GitHub enforces merge rules; it runs no platform workflow. Codefresh builds, Octopus releases and Argo CD applies,
/// so a file under <c>.github/workflows/</c> fails the rule unless <see cref="Exceptions"/> lists it. A listed workflow stays
/// in its lane: it keeps the project board in step with issues and pull requests and never builds, deploys or reaches a
/// cluster or a cloud. Its non-comment lines therefore name no build, deploy or cluster tool, use no action other than one
/// pinned to a full commit SHA (and never <c>actions/checkout</c>, so no pull request code runs), grant no <c>write</c>
/// permission to <c>GITHUB_TOKEN</c> (which may read, at <c>contents: read</c>), read no secret but the board credentials (<see cref="AllowedSecrets"/>), run
/// on no self-hosted runner, and listen to no event that runs with secrets for fork code or follows pushes, schedules or other
/// workflows (<c>pull_request_target</c>, <c>push</c>, <c>schedule</c>, <c>workflow_run</c>, ...). The pull request event is
/// <c>pull_request</c> only, and a workflow that listens to it must guard its job with
/// <c>github.event.pull_request.head.repo.full_name == github.repository</c>, so fork pull requests never reach the board
/// credentials. The triggers use the block form with two-space indentation, so each event is a key of its own line.
/// </summary>
/// <remarks>
/// Markdown under <c>.github/workflows/</c> is ignored like everywhere else; a tree without the folder passes (there is
/// nothing to run). A new board-only workflow goes in the list with its reason in the change that adds it, reviewed by
/// platform and security owners (<c>CODEOWNERS</c>, docs/tool-boundaries.md "Changing a lane").
/// </remarks>
internal static class GitHubWorkflowRule
{
    /// <summary>Rule ID.</summary>
    public const string Id = "TB24";

    /// <summary>Rule statement.</summary>
    public const string Description = "GitHub runs no platform workflow: only listed board-only workflows, which never build, deploy or reach a cluster";

    /// <summary>The workflows that may exist, each with its reason.</summary>
    public static IReadOnlyList<WorkflowException> Exceptions { get; } =
    [
        new(".github/workflows/project-board.yml",
            "Keeps the GitHub Project board in step with issues, pull requests and deployment status pushes; it reads events and calls only the GitHub GraphQL API with a GitHub App installation token (BOARD_APP_ID, BOARD_APP_PRIVATE_KEY) or, as fallback, PROJECTS_PAT"),
    ];

    /// <summary>The secrets a listed workflow may read: the GitHub App credentials and the fallback token.</summary>
    public static IReadOnlyList<string> AllowedSecrets { get; } = ["BOARD_APP_ID", "BOARD_APP_PRIVATE_KEY", "PROJECTS_PAT"];

    private static readonly Regex Comment = PosixPatterns.Ere("^[[:space:]]*#");

    private static readonly Regex Uses = PosixPatterns.Ere("(^|[[:space:]-])uses:[[:space:]]*[\"']?([^[:space:]\"'#]+)");

    private static readonly Regex PinnedAction = PosixPatterns.Ere("^[[:alnum:]_.-]+/[[:alnum:]_./-]+@[0-9a-f]{40}$");

    private static readonly Regex Tools = PosixPatterns.Ere(
        "(^|[^[:alnum:]_./-])(dotnet|docker|kubectl|helm|argocd|kustomize|terraform|az|octopus|cosign|codefresh)[[:space:]]+[[:alnum:]-]");

    private static readonly Regex Write = PosixPatterns.Ere(":[[:space:]]*[\"']?write(-all)?[\"']?([[:space:]]|#|$)|permissions:[[:space:]]*[\"']?write-all");

    private static readonly Regex Secret = PosixPatterns.Ere("secrets\\.([[:alnum:]_]+)");

    private static readonly Regex SelfHosted = PosixPatterns.Ere("self-hosted");

    private static readonly Regex InlineTriggers = PosixPatterns.Ere("^[\"']?on[\"']?:[[:space:]]*[^[:space:]#]");

    private static readonly Regex ForbiddenTrigger = PosixPatterns.Ere(
        "^[ ]{2}(push|pull_request_target|schedule|workflow_run|workflow_call|release|deployment|deployment_status|check_run|check_suite|status|registry_package|merge_group)[[:space:]]*:");

    private static readonly Regex PullRequestTrigger = PosixPatterns.Ere("^[ ]{2}pull_request[[:space:]]*:");

    private static readonly Regex SameRepositoryGuard = PosixPatterns.Ere(
        "github\\.event\\.pull_request\\.head\\.repo\\.full_name[[:space:]]*==[[:space:]]*github\\.repository");

    private static readonly Regex TriggerBlock = PosixPatterns.Ere("^[\"']?on[\"']?:");

    private static readonly Regex TopLevelKey = PosixPatterns.Ere("^[^[:space:]#]");

    private static readonly Regex TopLevelPermissions = PosixPatterns.Ere("^permissions:");

    /// <summary>Checks every workflow file of the tree.</summary>
    /// <param name="tree">The repository tree.</param>
    public static BoundaryResult Check(BoundaryTree tree)
    {
        var findings = new List<BoundaryFinding>();
        foreach (var file in tree.FilesIn(".github/workflows"))
        {
            if (!Exceptions.Any(exception => string.Equals(exception.Path, file, StringComparison.Ordinal)))
            {
                findings.Add(new BoundaryFinding(Id, file, null,
                    "GitHub Actions runs no platform workflow (Codefresh builds, Octopus releases, Argo CD applies): remove it, or list a board-only workflow in GitHubWorkflowRule.Exceptions with its reason"));
                continue;
            }

            findings.AddRange(ListedWorkflowFindings(tree, file));
        }

        return BoundaryResult.Of(Id, Description, findings);
    }

    private static IEnumerable<BoundaryFinding> ListedWorkflowFindings(BoundaryTree tree, string file)
    {
        var lines = tree.Lines(file) ?? [];
        var permissions = false;
        var triggers = false;
        var pullRequestLine = (int?)null;
        var guarded = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (Comment.IsMatch(line))
            {
                continue;
            }

            var number = index + 1;
            if (TopLevelKey.IsMatch(line))
            {
                triggers = TriggerBlock.IsMatch(line);
            }

            permissions |= TopLevelPermissions.IsMatch(line);
            guarded |= SameRepositoryGuard.IsMatch(line);
            if (triggers && PullRequestTrigger.IsMatch(line))
            {
                pullRequestLine = number;
            }

            if (Uses.Match(line) is { Success: true } uses)
            {
                var action = uses.Groups[2].Value;
                if (action.StartsWith("actions/checkout@", StringComparison.Ordinal) || !PinnedAction.IsMatch(action))
                {
                    yield return new BoundaryFinding(Id, file, number,
                        $"uses {action}: a board-only workflow checks out no code and pins every action to a full commit SHA");
                }
            }

            if (Tools.Match(line) is { Success: true } tool)
            {
                yield return new BoundaryFinding(Id, file, number, $"runs {tool.Groups[2].Value}: a board-only workflow never builds, deploys or reaches a cluster");
            }

            if (Write.IsMatch(line))
            {
                yield return new BoundaryFinding(Id, file, number, "grants a write permission: GITHUB_TOKEN stays at contents: read (the board goes through the App token or PROJECTS_PAT)");
            }

            foreach (Match secret in Secret.Matches(line))
            {
                if (!AllowedSecrets.Contains(secret.Groups[1].Value, StringComparer.Ordinal))
                {
                    yield return new BoundaryFinding(Id, file, number, $"reads secret {secret.Groups[1].Value}: a board-only workflow reads {string.Join(", ", AllowedSecrets)} only");
                }
            }

            if (SelfHosted.IsMatch(line))
            {
                yield return new BoundaryFinding(Id, file, number, "self-hosted runner: a board-only workflow runs on a GitHub-hosted runner, never on the build cluster");
            }

            if (InlineTriggers.IsMatch(line))
            {
                yield return new BoundaryFinding(Id, file, number, "inline triggers: write on: in the block form, one event per line");
            }

            if (triggers && ForbiddenTrigger.Match(line) is { Success: true } trigger)
            {
                yield return new BoundaryFinding(Id, file, number,
                    $"trigger {trigger.Groups[1].Value}: a board-only workflow listens to issues, pull_request, repository_dispatch and workflow_dispatch only");
            }
        }

        if (pullRequestLine is { } pullRequest && !guarded)
        {
            yield return new BoundaryFinding(Id, file, pullRequest,
                "pull_request trigger without the same-repository guard: the job runs only when github.event.pull_request.head.repo.full_name == github.repository");
        }

        if (!permissions)
        {
            yield return new BoundaryFinding(Id, file, null, "no top-level permissions: declare permissions: contents: read");
        }
    }
}
