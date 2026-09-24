namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// GitHub REST API (token from <c>GITHUB_TOKEN</c>). Repositories are written <c>owner/name</c>.
/// The write operations exist for the end-to-end test only.
/// </summary>
public interface IGitHubApi
{
    /// <summary>Gets the SHA at the head of a branch.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="branch">Branch name, for example <c>main</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> GetBranchHeadAsync(string repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>Lists commits, newest first.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="reference">Branch, tag or SHA to list from.</param>
    /// <param name="path">Only commits that touch this path, when given.</param>
    /// <param name="count">Maximum number of commits (1–100).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<GitHubCommit>> GetCommitsAsync(string repository, string reference, string? path = null, int count = 30, CancellationToken cancellationToken = default);

    /// <summary>Gets a file's content at a reference.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="path">File path, for example <c>gitops/workorders/envs/tdd/kustomization.yaml</c>.</param>
    /// <param name="reference">Branch, tag or SHA.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<GitHubFile> GetFileAsync(string repository, string path, string reference, CancellationToken cancellationToken = default);

    /// <summary>Compares two references (<c>base...head</c>).</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="baseReference">Base reference.</param>
    /// <param name="headReference">Head reference.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<GitHubComparison> CompareAsync(string repository, string baseReference, string headReference, CancellationToken cancellationToken = default);

    /// <summary>Creates a branch at a commit.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="branch">New branch name.</param>
    /// <param name="sha">Commit to branch from.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CreateBranchAsync(string repository, string branch, string sha, CancellationToken cancellationToken = default);

    /// <summary>Deletes a branch (cleanup of the end-to-end test); succeeds when it is already gone.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="branch">Branch name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task DeleteBranchAsync(string repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates one file in a single commit on a branch and returns the commit SHA.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="branch">Branch to commit to.</param>
    /// <param name="path">File path.</param>
    /// <param name="content">New UTF-8 content.</param>
    /// <param name="message">Commit message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> CommitFileAsync(string repository, string branch, string path, string content, string message, CancellationToken cancellationToken = default);

    /// <summary>Opens a pull request.</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="head">Branch with the changes.</param>
    /// <param name="baseBranch">Branch to merge into.</param>
    /// <param name="title">Title.</param>
    /// <param name="body">Description.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<GitHubPullRequest> OpenPullRequestAsync(string repository, string head, string baseBranch, string title, string body, CancellationToken cancellationToken = default);

    /// <summary>Closes a pull request without merging (cleanup of the end-to-end test).</summary>
    /// <param name="repository">Repository, <c>owner/name</c>.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task ClosePullRequestAsync(string repository, int number, CancellationToken cancellationToken = default);
}
