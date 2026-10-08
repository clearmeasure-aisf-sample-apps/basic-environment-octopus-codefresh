using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>What a listed GitHub Actions workflow may do: its lane.</summary>
/// <param name="Name">How the findings call it: <c>board-only</c> or <c>alert-only</c>.</param>
/// <param name="Triggers">The events it may listen to; any other key of <c>on:</c> fails.</param>
/// <param name="Secrets">The repository secrets it may read.</param>
/// <param name="WriteScopes">The scopes of <c>GITHUB_TOKEN</c> it may hold at <c>write</c>.</param>
/// <param name="Checkout"><c>true</c>: it may check out the repository (pinned, and without persisted credentials).</param>
internal sealed record WorkflowLane(string Name, IReadOnlyList<string> Triggers, IReadOnlyList<string> Secrets, IReadOnlyList<string> WriteScopes, bool Checkout);

/// <summary>A GitHub Actions workflow of this repository that may exist, with its reason.</summary>
/// <param name="Path">Repository-relative path under <c>.github/workflows/</c>.</param>
/// <param name="Reason">Why it runs on GitHub Actions and in no other lane.</param>
/// <param name="Lane">What it may do.</param>
internal sealed record WorkflowException(string Path, string Reason, WorkflowLane Lane);

/// <summary>
/// TB24: GitHub enforces merge rules; it runs no platform workflow. Codefresh builds, Octopus releases and Argo CD applies,
/// so a file under <c>.github/workflows/</c> fails the rule unless <see cref="Exceptions"/> lists it. A listed workflow stays
/// in its lane and never builds, deploys or reaches a cluster or a cloud. In either lane its non-comment lines name no
/// build, deploy or cluster tool, use no action other than one pinned to a full commit SHA, run on no self-hosted runner,
/// declare top-level <c>permissions</c>, and write their triggers in the block form with two-space indentation, so each
/// event is a key of its own line.
/// <list type="bullet">
/// <item><see cref="BoardOnly"/> keeps the project board in step with issues and pull requests. It never uses
/// <c>actions/checkout</c> (so no pull request code runs), grants no <c>write</c> permission to <c>GITHUB_TOKEN</c> (which
/// may read, at <c>contents: read</c>), reads no secret but the board credentials (<see cref="AllowedSecrets"/>), and
/// listens to <c>issues</c>, <c>pull_request</c>, <c>repository_dispatch</c> and <c>workflow_dispatch</c> only. A workflow
/// that listens to <c>pull_request</c> must guard its job with
/// <c>github.event.pull_request.head.repo.full_name == github.repository</c>, so fork pull requests never reach the board
/// credentials.</item>
/// <item><see cref="AlertOnly"/> reads the history of the default branch, and through the GitHub REST API what anyone
/// may read (the open pull requests and commit statuses of public repositories), and writes GitHub issues. It listens to
/// <c>schedule</c> and <c>workflow_dispatch</c> only (no pull request, issue or push event, so it runs no code but the
/// branch it was started on), reads no secret at all, may hold <c>issues: write</c> and no other <c>write</c>, and may use
/// <c>actions/checkout</c> pinned to a full commit SHA with <c>persist-credentials: false</c>.</item>
/// </list>
/// No lane admits an event that runs with secrets for fork code or follows pushes or other workflows
/// (<c>pull_request_target</c>, <c>push</c>, <c>workflow_run</c>, ...).
/// </summary>
/// <remarks>
/// Markdown under <c>.github/workflows/</c> is ignored like everywhere else; a tree without the folder passes (there is
/// nothing to run). A new workflow goes in the list with its reason and its lane in the change that adds it, reviewed by
/// platform and security owners (<c>CODEOWNERS</c>, docs/tool-boundaries.md "Changing a lane").
/// </remarks>
internal static class GitHubWorkflowRule
{
    /// <summary>Rule ID.</summary>
    public const string Id = "TB24";

    /// <summary>Rule statement.</summary>
    public const string Description = "GitHub runs no platform workflow: only listed board-only and alert-only workflows, which never build, deploy or reach a cluster";

    /// <summary>The secrets a board-only workflow may read: the GitHub App credentials, and nothing else (no personal access token).</summary>
    public static IReadOnlyList<string> AllowedSecrets { get; } = ["BOARD_APP_ID", "BOARD_APP_PRIVATE_KEY"];

    /// <summary>The lane of a workflow that only moves cards on the project board.</summary>
    public static WorkflowLane BoardOnly { get; } = new("board-only", ["issues", "pull_request", "repository_dispatch", "workflow_dispatch"], AllowedSecrets, [], Checkout: false);

    /// <summary>The lane of a workflow that only reads the default branch and public GitHub data, and writes GitHub issues.</summary>
    public static WorkflowLane AlertOnly { get; } = new("alert-only", ["schedule", "workflow_dispatch"], [], ["issues"], Checkout: true);

    /// <summary>The workflows that may exist, each with its reason and its lane.</summary>
    public static IReadOnlyList<WorkflowException> Exceptions { get; } =
    [
        new(".github/workflows/project-board.yml",
            "Keeps the GitHub Project board in step with issues, pull requests and deployment status pushes; it reads events and calls only the GitHub GraphQL API with a GitHub App installation token (BOARD_APP_ID, BOARD_APP_PRIVATE_KEY) and no personal access token",
            BoardOnly),
        new(".github/workflows/release-stall-check.yml",
            "Opens one GitHub issue for a release that is pinned in an environment and not in the next one (#86), and one for a pull request whose Codefresh build never started (#101), and closes each when its condition clears; it reads the pin commits of main and the open pull requests and commit statuses of this repository and the app repository (public data, read through the REST API), and writes only issues of this repository with GITHUB_TOKEN (contents: read, issues: write) and no secret. Neither Octopus nor Codefresh can run it: the first holds no schedule but env-sleep (C23) and the second holds no credential that writes here (TB16) and cannot report a build it did not start",
            AlertOnly),
    ];

    private static readonly Regex Comment = PosixPatterns.Ere("^[[:space:]]*#");

    private static readonly Regex Uses = PosixPatterns.Ere("(^|[[:space:]-])uses:[[:space:]]*[\"']?([^[:space:]\"'#]+)");

    private static readonly Regex PinnedAction = PosixPatterns.Ere("^[[:alnum:]_.-]+/[[:alnum:]_./-]+@[0-9a-f]{40}$");

    private static readonly Regex Tools = PosixPatterns.Ere(
        "(^|[^[:alnum:]_./-])(dotnet|docker|kubectl|helm|argocd|kustomize|terraform|az|octopus|cosign|codefresh)[[:space:]]+[[:alnum:]-]");

    private static readonly Regex Write = PosixPatterns.Ere(":[[:space:]]*[\"']?write(-all)?[\"']?([[:space:]]|#|$)|permissions:[[:space:]]*[\"']?write-all");

    private static readonly Regex Secret = PosixPatterns.Ere("secrets\\.([[:alnum:]_]+)");

    private static readonly Regex SelfHosted = PosixPatterns.Ere("self-hosted");

    private static readonly Regex InlineTriggers = PosixPatterns.Ere("^[\"']?on[\"']?:[[:space:]]*[^[:space:]#]");

    private static readonly Regex Trigger = PosixPatterns.Ere("^[ ]{2}[\"']?([[:alnum:]_]+)[\"']?[[:space:]]*:");

    private static readonly Regex PersistCredentialsOff = PosixPatterns.Ere("^[[:space:]]*persist-credentials:[[:space:]]*[\"']?false[\"']?([[:space:]]|#|$)");

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
                    "GitHub Actions runs no platform workflow (Codefresh builds, Octopus releases, Argo CD applies): remove it, or list a board-only or alert-only workflow in GitHubWorkflowRule.Exceptions with its reason and its lane"));
                continue;
            }

            findings.AddRange(ListedWorkflowFindings(tree, file, Exceptions.First(exception => string.Equals(exception.Path, file, StringComparison.Ordinal)).Lane));
        }

        return BoundaryResult.Of(Id, Description, findings);
    }

    private static bool GrantsOnly(string line, IReadOnlyList<string> scopes) =>
        scopes.Any(scope => PosixPatterns.Ere($"^[[:space:]]*{scope}:[[:space:]]*write[[:space:]]*(#.*)?$").IsMatch(line));

    private static IEnumerable<BoundaryFinding> ListedWorkflowFindings(BoundaryTree tree, string file, WorkflowLane lane)
    {
        var lines = tree.Lines(file) ?? [];
        var kind = $"{(lane.Name[0] is 'a' ? "an" : "a")} {lane.Name} workflow";
        var checkoutLine = (int?)null;
        var persistsNoCredentials = false;
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
            persistsNoCredentials |= PersistCredentialsOff.IsMatch(line);
            if (triggers && PullRequestTrigger.IsMatch(line) && lane.Triggers.Contains("pull_request", StringComparer.Ordinal))
            {
                pullRequestLine = number;
            }

            if (Uses.Match(line) is { Success: true } uses)
            {
                var action = uses.Groups[2].Value;
                var checkout = action.StartsWith("actions/checkout@", StringComparison.Ordinal);
                if (!PinnedAction.IsMatch(action) || (checkout && !lane.Checkout))
                {
                    yield return new BoundaryFinding(Id, file, number, lane.Checkout
                        ? $"uses {action}: {kind} pins every action to a full commit SHA"
                        : $"uses {action}: {kind} checks out no code and pins every action to a full commit SHA");
                }
                else if (checkout)
                {
                    checkoutLine ??= number;
                }
            }

            if (Tools.Match(line) is { Success: true } tool)
            {
                yield return new BoundaryFinding(Id, file, number, $"runs {tool.Groups[2].Value}: {kind} never builds, deploys or reaches a cluster");
            }

            if (Write.IsMatch(line) && !GrantsOnly(line, lane.WriteScopes))
            {
                yield return new BoundaryFinding(Id, file, number, lane.WriteScopes.Count == 0
                    ? "grants a write permission: GITHUB_TOKEN stays at contents: read (the board goes through the GitHub App token)"
                    : $"grants a write permission: GITHUB_TOKEN of {kind} writes {string.Join(", ", lane.WriteScopes)} only, each on a line of its own");
            }

            foreach (Match secret in Secret.Matches(line))
            {
                if (!lane.Secrets.Contains(secret.Groups[1].Value, StringComparer.Ordinal))
                {
                    yield return new BoundaryFinding(Id, file, number, lane.Secrets.Count == 0
                        ? $"reads secret {secret.Groups[1].Value}: {kind} reads no secret"
                        : $"reads secret {secret.Groups[1].Value}: {kind} reads {string.Join(", ", lane.Secrets)} only");
                }
            }

            if (SelfHosted.IsMatch(line))
            {
                yield return new BoundaryFinding(Id, file, number, $"self-hosted runner: {kind} runs on a GitHub-hosted runner, never on the build cluster");
            }

            if (InlineTriggers.IsMatch(line))
            {
                yield return new BoundaryFinding(Id, file, number, "inline triggers: write on: in the block form, one event per line");
            }

            if (triggers && Trigger.Match(line) is { Success: true } trigger && !lane.Triggers.Contains(trigger.Groups[1].Value, StringComparer.Ordinal))
            {
                yield return new BoundaryFinding(Id, file, number, $"trigger {trigger.Groups[1].Value}: {kind} listens to {string.Join(", ", lane.Triggers)} only");
            }
        }

        if (checkoutLine is { } checkedOut && !persistsNoCredentials)
        {
            yield return new BoundaryFinding(Id, file, checkedOut, "checkout keeps GITHUB_TOKEN in the work tree: set persist-credentials: false");
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
