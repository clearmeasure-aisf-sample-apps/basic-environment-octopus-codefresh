using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Kit.ReleaseStall;

/// <summary>An issue (or pull request) the stub holds.</summary>
internal sealed class StubIssue
{
    /// <summary>Issue number.</summary>
    public required int Number { get; init; }

    /// <summary>Title.</summary>
    public required string Title { get; init; }

    /// <summary>Body.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary><c>open</c> or <c>closed</c>.</summary>
    public string State { get; set; } = "open";

    /// <summary>The reason of the last close.</summary>
    public string? StateReason { get; set; }

    /// <summary>When it was closed.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary><c>true</c>: the entry is a pull request, which the issues listing returns too.</summary>
    public bool PullRequest { get; init; }

    /// <summary>The comments, oldest first.</summary>
    public List<string> Comments { get; } = [];
}

/// <summary>A pull request the stub holds, with the head commit and the statuses that commit carries.</summary>
internal sealed class StubPull
{
    /// <summary><c>owner/repo</c> of the pull request.</summary>
    public required string Repository { get; init; }

    /// <summary>Pull request number.</summary>
    public required int Number { get; init; }

    /// <summary>The head commit.</summary>
    public required string Head { get; set; }

    /// <summary>The committer date of the head commit.</summary>
    public required DateTimeOffset CommittedAt { get; set; }

    /// <summary>When the pull request was opened.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The head branch.</summary>
    public string Branch { get; init; } = "people/change";

    /// <summary><c>owner/repo</c> of the head; <c>null</c> is the repository itself, empty a fork that was deleted.</summary>
    public string? HeadRepository { get; init; }

    /// <summary><c>true</c>: a draft.</summary>
    public bool Draft { get; set; }

    /// <summary><c>open</c> or <c>closed</c>.</summary>
    public string State { get; set; } = "open";

    /// <summary><c>true</c>: closed by a merge.</summary>
    public bool Merged { get; set; }

    /// <summary>The statuses of the head commit, one per context: context and state.</summary>
    public List<(string Context, string State)> Statuses { get; } = [];
}

/// <summary>
/// The part of the GitHub REST API that the release stall check uses, on 127.0.0.1 (the <c>GITHUB_API_URL</c> seam),
/// holding its data in memory. Issues (of whatever repository the path names): the paged listing with <c>state</c> and
/// <c>since</c>, create, comment and close. Pull requests, per repository: the paged listing of the open ones, one pull
/// request by number, the head commit with its committer date and the combined status of that commit; a repository in
/// <see cref="Unreadable"/> answers 404 to all of these, as a private repository does. It accepts one bearer token
/// (<see cref="Token"/>, the workflow's <c>GITHUB_TOKEN</c>) and records every request.
/// </summary>
internal sealed class StubIssueApi : IDisposable
{
    /// <summary>The token the stub accepts.</summary>
    public const string Token = "stub-workflow-token-0086";

    private readonly HttpListener listener = new();
    private readonly List<StubRequest> requests = [];
    private readonly List<StubIssue> issues = [];
    private readonly List<StubPull> pulls = [];
    private readonly Task loop;

    /// <summary>Starts the stub on a free loopback port.</summary>
    public StubIssueApi()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    /// <summary>Base URL, for GITHUB_API_URL.</summary>
    public string Url { get; }

    /// <summary><c>true</c>: every write is answered 403, as for a token without <c>issues: write</c>.</summary>
    public bool RefuseWrites { get; set; }

    /// <summary>The issues, in the order they were added.</summary>
    public IReadOnlyList<StubIssue> Issues
    {
        get
        {
            lock (issues)
            {
                return issues.ToArray();
            }
        }
    }

    /// <summary>Repositories whose pull requests and commits answer 404, as for a token that may not read them.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests in arrival order.</summary>
    public IReadOnlyList<StubRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return requests.ToArray();
            }
        }
    }

    /// <summary>The requests that change something.</summary>
    public IReadOnlyList<StubRequest> Writes => Requests.Where(request => request.Method != "GET").ToArray();

    /// <summary>Adds an issue as if someone had written it earlier.</summary>
    /// <param name="title">Title.</param>
    /// <param name="body">Body.</param>
    /// <param name="closedAt">When it was closed; <c>null</c> leaves it open.</param>
    /// <param name="pullRequest"><c>true</c> for a pull request.</param>
    public StubIssue Seed(string title, string body = "", DateTimeOffset? closedAt = null, bool pullRequest = false)
    {
        lock (issues)
        {
            var issue = new StubIssue
            {
                Number = issues.Count + 1,
                Title = title,
                Body = body,
                State = closedAt is null ? "open" : "closed",
                ClosedAt = closedAt,
                UpdatedAt = closedAt ?? DateTimeOffset.UtcNow,
                PullRequest = pullRequest,
            };
            issues.Add(issue);
            return issue;
        }
    }

    /// <summary>Adds an open pull request whose head commit carries no status.</summary>
    /// <param name="repository"><c>owner/repo</c>.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="head">Head commit.</param>
    /// <param name="committedAt">Committer date of the head commit, ISO 8601.</param>
    /// <param name="createdAt">When the pull request was opened, ISO 8601; <c>null</c>: three seconds after the commit.</param>
    /// <param name="draft"><c>true</c> for a draft.</param>
    /// <param name="headRepository"><c>owner/repo</c> of the head for a fork; empty for a deleted fork.</param>
    /// <param name="branch">Head branch.</param>
    public StubPull Pull(string repository, int number, string head, string committedAt, string? createdAt = null, bool draft = false, string? headRepository = null, string branch = "people/change")
    {
        var committed = Moment(committedAt);
        var pull = new StubPull
        {
            Repository = repository,
            Number = number,
            Head = head,
            CommittedAt = committed,
            CreatedAt = createdAt is null ? committed.AddSeconds(3) : Moment(createdAt),
            Draft = draft,
            HeadRepository = headRepository,
            Branch = branch,
        };
        lock (issues)
        {
            pulls.Add(pull);
        }

        return pull;
    }

    /// <summary>Stops the stub.</summary>
    public void Dispose()
    {
        listener.Close();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private static string Message(string text) => new JsonObject { ["message"] = text }.ToJsonString();

    private static DateTimeOffset Moment(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private static string Iso(DateTimeOffset moment) => moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static JsonObject Json(StubPull pull) => new()
    {
        ["number"] = pull.Number,
        ["state"] = pull.State,
        ["draft"] = pull.Draft,
        ["merged"] = pull.Merged,
        ["title"] = $"Title of pull request {pull.Number}, which no issue may repeat",
        ["created_at"] = Iso(pull.CreatedAt),
        ["html_url"] = $"http://stub.invalid/{pull.Repository}/pull/{pull.Number}",
        ["head"] = new JsonObject
        {
            ["sha"] = pull.Head,
            ["ref"] = pull.Branch,
            ["repo"] = pull.HeadRepository is { Length: 0 } ? null : new JsonObject { ["full_name"] = pull.HeadRepository ?? pull.Repository },
        },
    };

    private static JsonObject CombinedStatus(StubPull pull, StubRequest request)
    {
        var query = HttpUtility.ParseQueryString(request.Path.Contains('?', StringComparison.Ordinal) ? request.Path[(request.Path.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
        var size = int.Parse(query["per_page"] ?? "30", CultureInfo.InvariantCulture);
        var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
        var statuses = pull.Statuses.Skip((page - 1) * size).Take(size)
            .Select(status => (JsonNode)new JsonObject { ["context"] = status.Context, ["state"] = status.State }).ToArray();
        return new JsonObject
        {
            ["state"] = pull.Statuses.Count == 0 ? "pending" : pull.Statuses[^1].State,
            ["sha"] = pull.Head,
            ["total_count"] = pull.Statuses.Count,
            ["statuses"] = new JsonArray(statuses),
        };
    }

    private static JsonObject Json(StubIssue issue)
    {
        var json = new JsonObject
        {
            ["number"] = issue.Number,
            ["title"] = issue.Title,
            ["body"] = issue.Body,
            ["state"] = issue.State,
            ["state_reason"] = issue.StateReason,
            ["closed_at"] = issue.ClosedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["updated_at"] = issue.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["html_url"] = $"http://stub.invalid/issues/{issue.Number}",
        };
        if (issue.PullRequest)
        {
            json["pull_request"] = new JsonObject { ["url"] = $"http://stub.invalid/pulls/{issue.Number}" };
        }

        return json;
    }

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var request = new StubRequest(context.Request.HttpMethod, context.Request.RawUrl ?? string.Empty, context.Request.Headers["Authorization"] ?? string.Empty, body);
            lock (requests)
            {
                requests.Add(request);
            }

            var (status, answer) = Answer(request);
            var bytes = Encoding.UTF8.GetBytes(answer);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    private (int Status, string Body) Answer(StubRequest request)
    {
        if (request.Bearer != Token)
        {
            return (401, Message("Bad credentials"));
        }

        if (request.Method != "GET" && RefuseWrites)
        {
            return (403, Message("Resource not accessible by integration"));
        }

        var segments = request.PathOnly.Trim('/').Split('/');
        lock (issues)
        {
            if (segments is ["repos", var owner, var name, "pulls" or "commits", ..])
            {
                var repository = $"{owner}/{name}";
                return request.Method == "GET" && !Unreadable.Contains(repository) ? Read(repository, segments[3..], request) : (404, Message("Not Found"));
            }

            switch (request.Method, segments)
            {
                case ("GET", ["repos", _, _, "issues"]):
                    return (200, Listing(request));
                case ("POST", ["repos", _, _, "issues"]):
                {
                    var input = JsonNode.Parse(request.Body)!;
                    var issue = new StubIssue { Number = issues.Count + 1, Title = input["title"]!.GetValue<string>(), Body = input["body"]?.GetValue<string>() ?? string.Empty };
                    issues.Add(issue);
                    return (201, Json(issue).ToJsonString());
                }

                case ("POST", ["repos", _, _, "issues", var number, "comments"]) when Find(number) is { } issue:
                    issue.Comments.Add(JsonNode.Parse(request.Body)!["body"]!.GetValue<string>());
                    issue.UpdatedAt = DateTimeOffset.UtcNow;
                    return (201, """{"html_url":"http://stub.invalid/comment/1"}""");
                case ("PATCH", ["repos", _, _, "issues", var number]) when Find(number) is { } issue:
                {
                    var input = JsonNode.Parse(request.Body)!;
                    if (input["state"]?.GetValue<string>() is { } state)
                    {
                        issue.State = state;
                        issue.StateReason = input["state_reason"]?.GetValue<string>();
                        issue.ClosedAt = state == "closed" ? DateTimeOffset.UtcNow : null;
                        issue.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    return (200, Json(issue).ToJsonString());
                }

                default:
                    return (404, Message("Not Found"));
            }
        }
    }

    private (int Status, string Body) Read(string repository, string[] segments, StubRequest request)
    {
        var own = pulls.Where(pull => string.Equals(pull.Repository, repository, StringComparison.OrdinalIgnoreCase)).ToArray();
        switch (segments)
        {
            case ["pulls"]:
            {
                var query = HttpUtility.ParseQueryString(request.Path.Contains('?', StringComparison.Ordinal) ? request.Path[(request.Path.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
                var state = query["state"] ?? "open";
                var size = int.Parse(query["per_page"] ?? "30", CultureInfo.InvariantCulture);
                var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
                var listed = own.Where(pull => state == "all" || pull.State == state).Skip((page - 1) * size).Take(size).Select(pull => (JsonNode)Json(pull)).ToArray();
                return (200, new JsonArray(listed).ToJsonString());
            }

            case ["pulls", var number] when own.FirstOrDefault(pull => pull.Number.ToString(CultureInfo.InvariantCulture) == number) is { } pull:
                return (200, Json(pull).ToJsonString());
            case ["commits", var sha] when own.FirstOrDefault(pull => pull.Head == sha) is { } pull:
                return (200, new JsonObject { ["sha"] = sha, ["commit"] = new JsonObject { ["committer"] = new JsonObject { ["date"] = Iso(pull.CommittedAt) } } }.ToJsonString());
            case ["commits", var sha, "status"] when own.FirstOrDefault(pull => pull.Head == sha) is { } pull:
                return (200, CombinedStatus(pull, request).ToJsonString());
            default:
                return (404, Message("Not Found"));
        }
    }

    private StubIssue? Find(string number) => issues.FirstOrDefault(issue => issue.Number.ToString(CultureInfo.InvariantCulture) == number);

    private string Listing(StubRequest request)
    {
        var query = HttpUtility.ParseQueryString(request.Path.Contains('?', StringComparison.Ordinal) ? request.Path[(request.Path.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
        var state = query["state"] ?? "open";
        var since = query["since"] is { Length: > 0 } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : (DateTimeOffset?)null;
        var size = int.Parse(query["per_page"] ?? "30", CultureInfo.InvariantCulture);
        var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
        var matching = issues
            .Where(issue => state == "all" || issue.State == state)
            .Where(issue => since is null || issue.UpdatedAt >= since)
            .Skip((page - 1) * size).Take(size)
            .Select(issue => (JsonNode)Json(issue)).ToArray();
        return new JsonArray(matching).ToJsonString();
    }
}
