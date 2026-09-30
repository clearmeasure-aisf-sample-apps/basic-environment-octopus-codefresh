namespace Platform.Conformance.Harness.Settings;

/// <summary>
/// The current GitHub token. A GitHub App installation token lives one hour while a suite may run for hours, so a
/// pipeline script re-mints it and rewrites the file named by <c>GITHUB_TOKEN_FILE</c>; every request asks this source
/// for the token, which reads that file. Without the file (or when it is empty or unreadable for a moment) the value of
/// <c>GITHUB_TOKEN</c> read at start is used. The value is never printed.
/// </summary>
public sealed class GitHubTokenSource
{
    private readonly string? token;
    private readonly string? tokenFile;

    /// <summary>Creates the source; blank values count as missing.</summary>
    /// <param name="token">Value of <c>GITHUB_TOKEN</c>, the one-shot token.</param>
    /// <param name="tokenFile">Value of <c>GITHUB_TOKEN_FILE</c>, the path of the file that holds the current token.</param>
    public GitHubTokenSource(string? token, string? tokenFile)
    {
        this.token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        this.tokenFile = string.IsNullOrWhiteSpace(tokenFile) ? null : tokenFile.Trim();
    }

    /// <summary>The token to send now, or <c>null</c> when there is none.</summary>
    public string? Current
    {
        get
        {
            if (tokenFile is not null)
            {
                try
                {
                    var fromFile = File.ReadAllText(tokenFile).Trim();
                    if (fromFile.Length > 0)
                    {
                        return fromFile;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The file is being rewritten or is gone: fall back to the token read at start.
                }
            }

            return token;
        }
    }

    /// <summary>Says whether a token is available, never its value.</summary>
    public override string ToString() => Current is null ? "GitHubTokenSource { missing }" : "GitHubTokenSource { set }";
}
