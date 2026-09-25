using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Conformance.Harness.Clients;

/// <summary>REST implementation of <see cref="IGitHubApi"/>.</summary>
public sealed class GitHubApi : IGitHubApi, IDisposable
{
    /// <summary>Default API base.</summary>
    public const string DefaultApiUrl = "https://api.github.com/";

    /// <summary>REST API version header value.</summary>
    public const string ApiVersion = "2022-11-28";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RestClient rest;

    /// <summary>Creates the client over an <see cref="HttpClient"/> whose base address is the API root and that sends the token.</summary>
    /// <param name="http">Configured HTTP client; owned and disposed by this instance.</param>
    public GitHubApi(HttpClient http)
    {
        rest = new RestClient(http, "GitHub", Json);
    }

    /// <summary>Creates a client that authenticates with <paramref name="token"/>.</summary>
    /// <param name="token">GitHub token; sent only as a bearer <c>Authorization</c> header.</param>
    /// <param name="timeout">Request timeout.</param>
    /// <param name="handler">Message handler (unit tests pass a stub).</param>
    /// <param name="apiUrl">API base (default <c>https://api.github.com/</c>).</param>
    public static GitHubApi Create(string token, TimeSpan timeout, HttpMessageHandler? handler = null, string apiUrl = DefaultApiUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var http = PlatformHttp.Create(new Uri(apiUrl), timeout, handler);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
        return new GitHubApi(http);
    }

    /// <inheritdoc />
    public async Task<string> GetBranchHeadAsync(string repository, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        var reference = await rest.GetAsync<JsonElement>($"repos/{Repository(repository)}/git/ref/heads/{EscapePath(branch)}", cancellationToken).ConfigureAwait(false);
        return Text(Child(reference, "object"), "sha") ?? throw new InvalidOperationException($"GitHub returned no SHA for {repository}@{branch}.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubCommit>> GetCommitsAsync(string repository, string reference, string? path = null, int count = 30, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var query = $"sha={Uri.EscapeDataString(reference)}&per_page={Math.Clamp(count, 1, 100)}";
        if (!string.IsNullOrWhiteSpace(path))
        {
            query += $"&path={Uri.EscapeDataString(path)}";
        }

        var commits = await rest.GetAsync<JsonElement>($"repos/{Repository(repository)}/commits?{query}", cancellationToken).ConfigureAwait(false);
        return commits.ValueKind == JsonValueKind.Array ? commits.EnumerateArray().Select(ReadCommit).ToArray() : [];
    }

    /// <inheritdoc />
    public async Task<GitHubFile> GetFileAsync(string repository, string path, string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var file = await rest.GetAsync<JsonElement>($"repos/{Repository(repository)}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(reference)}", cancellationToken).ConfigureAwait(false);
        if (Text(file, "type") is { } type && type != "file")
        {
            throw new InvalidOperationException($"{repository}/{path} at {reference} is a {type}, not a file.");
        }

        var encoded = (Text(file, "content") ?? "").Replace("\n", "", StringComparison.Ordinal);
        return new GitHubFile(Text(file, "path") ?? path, Text(file, "sha") ?? "", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    /// <inheritdoc />
    public async Task<GitHubComparison> CompareAsync(string repository, string baseReference, string headReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(headReference);
        var comparison = await rest.GetAsync<JsonElement>($"repos/{Repository(repository)}/compare/{Uri.EscapeDataString(baseReference)}...{Uri.EscapeDataString(headReference)}", cancellationToken).ConfigureAwait(false);
        var commits = comparison.TryGetProperty("commits", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(ReadCommit).ToArray() : [];
        var files = comparison.TryGetProperty("files", out var changed) && changed.ValueKind == JsonValueKind.Array
            ? changed.EnumerateArray().Select(file => Text(file, "filename")).OfType<string>().ToArray()
            : [];
        return new GitHubComparison(Text(comparison, "status") ?? "", Number(comparison, "ahead_by"), Number(comparison, "behind_by"), commits, files);
    }

    /// <inheritdoc />
    public Task CreateBranchAsync(string repository, string branch, string sha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        return rest.SendAsync(HttpMethod.Post, $"repos/{Repository(repository)}/git/refs", new CreateReference($"refs/heads/{branch}", sha), cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteBranchAsync(string repository, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        try
        {
            await rest.SendAsync(HttpMethod.Delete, $"repos/{Repository(repository)}/git/refs/heads/{EscapePath(branch)}", body: null, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
        }
    }

    /// <inheritdoc />
    public async Task<string> CommitFileAsync(string repository, string branch, string path, string content, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        string? existingSha = null;
        try
        {
            existingSha = (await GetFileAsync(repository, path, branch, cancellationToken).ConfigureAwait(false)).Sha;
        }
        catch (PlatformApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        var body = new PutContent(message, Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), branch, existingSha);
        var result = await rest.SendAsync<JsonElement>(HttpMethod.Put, $"repos/{Repository(repository)}/contents/{EscapePath(path)}", body, cancellationToken).ConfigureAwait(false);
        return Text(Child(result, "commit"), "sha") ?? throw new InvalidOperationException($"GitHub returned no commit for {repository}/{path}.");
    }

    /// <inheritdoc />
    public async Task<string> CreateBranchWithFileAsync(string repository, string branch, string baseSha, string path, string content, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var name = Repository(repository);
        var parent = await rest.GetAsync<JsonElement>($"repos/{name}/git/commits/{Uri.EscapeDataString(baseSha)}", cancellationToken).ConfigureAwait(false);
        var baseTree = Text(Child(parent, "tree"), "sha") ?? throw new InvalidOperationException($"GitHub returned no tree for {repository}@{baseSha}.");
        var tree = await rest.SendAsync<JsonElement>(HttpMethod.Post, $"repos/{name}/git/trees", new CreateTree(baseTree, [new TreeEntry(path, "100644", "blob", content)]), cancellationToken).ConfigureAwait(false);
        var treeSha = Text(tree, "sha") ?? throw new InvalidOperationException($"GitHub returned no tree for {repository}/{path}.");
        var commit = await rest.SendAsync<JsonElement>(HttpMethod.Post, $"repos/{name}/git/commits", new CreateCommit(message, treeSha, [baseSha]), cancellationToken).ConfigureAwait(false);
        var commitSha = Text(commit, "sha") ?? throw new InvalidOperationException($"GitHub returned no commit for {repository}/{path}.");
        await rest.SendAsync(HttpMethod.Post, $"repos/{name}/git/refs", new CreateReference($"refs/heads/{branch}", commitSha), cancellationToken).ConfigureAwait(false);
        return commitSha;
    }

    /// <inheritdoc />
    public async Task<GitHubPullRequest> OpenPullRequestAsync(string repository, string head, string baseBranch, string title, string body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var pull = await rest.SendAsync<JsonElement>(HttpMethod.Post, $"repos/{Repository(repository)}/pulls", new CreatePull(title, head, baseBranch, body), cancellationToken).ConfigureAwait(false);
        return new GitHubPullRequest(Number(pull, "number"), Text(pull, "html_url") ?? "", Text(Child(pull, "head"), "sha"));
    }

    /// <inheritdoc />
    public Task ClosePullRequestAsync(string repository, int number, CancellationToken cancellationToken = default) =>
        rest.SendAsync(HttpMethod.Patch, $"repos/{Repository(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", new UpdatePull("closed"), cancellationToken);

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();

    private static string Repository(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"'{repository}' is not an owner/name repository.", nameof(repository));
        }

        return $"{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static GitHubCommit ReadCommit(JsonElement item)
    {
        var commit = Child(item, "commit");
        var author = Child(commit, "author");
        var committer = Child(commit, "committer");
        var date = Text(committer, "date");
        return new GitHubCommit(
            Text(item, "sha") ?? "",
            Text(commit, "message") ?? "",
            Text(author, "name"),
            Text(author, "email"),
            date is not null && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null);
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private sealed record CreateReference(string Ref, string Sha);

    private sealed record CreateTree([property: JsonPropertyName("base_tree")] string BaseTree, IReadOnlyList<TreeEntry> Tree);

    private sealed record TreeEntry(string Path, string Mode, string Type, string Content);

    private sealed record CreateCommit(string Message, string Tree, IReadOnlyList<string> Parents);

    private sealed record PutContent(string Message, string Content, string Branch, string? Sha);

    private sealed record CreatePull(string Title, string Head, string Base, string Body);

    private sealed record UpdatePull(string State);
}
