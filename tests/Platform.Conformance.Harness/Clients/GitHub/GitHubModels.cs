namespace Platform.Conformance.Harness.Clients;

/// <summary>A commit.</summary>
/// <param name="Sha">Commit SHA.</param>
/// <param name="Message">Commit message.</param>
/// <param name="AuthorName">Author name.</param>
/// <param name="AuthorEmail">Author email.</param>
/// <param name="CommittedAt">Committer date.</param>
public sealed record GitHubCommit(string Sha, string Message, string? AuthorName, string? AuthorEmail, DateTimeOffset? CommittedAt);

/// <summary>A file at a Git reference.</summary>
/// <param name="Path">Path in the repository.</param>
/// <param name="Sha">Blob SHA (needed to update the file).</param>
/// <param name="Content">Decoded UTF-8 content.</param>
public sealed record GitHubFile(string Path, string Sha, string Content);

/// <summary>Comparison of two references.</summary>
/// <param name="Status"><c>identical</c>, <c>ahead</c>, <c>behind</c> or <c>diverged</c>.</param>
/// <param name="AheadBy">Commits in head not in base.</param>
/// <param name="BehindBy">Commits in base not in head.</param>
/// <param name="Commits">Commits in head not in base.</param>
/// <param name="Files">Changed file paths.</param>
public sealed record GitHubComparison(string Status, int AheadBy, int BehindBy, IReadOnlyList<GitHubCommit> Commits, IReadOnlyList<string> Files);

/// <summary>An opened pull request.</summary>
/// <param name="Number">Pull request number.</param>
/// <param name="Url">Web URL.</param>
/// <param name="HeadSha">Head commit SHA.</param>
public sealed record GitHubPullRequest(int Number, string Url, string? HeadSha);
