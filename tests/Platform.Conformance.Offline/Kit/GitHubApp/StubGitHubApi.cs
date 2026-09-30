using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Platform.Conformance.Offline.Kit.GitHubApp;

/// <summary>A request the stub GitHub API recorded.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">The raw path with its query.</param>
/// <param name="Authorization">The Authorization header (test values only).</param>
/// <param name="Body">The request body.</param>
internal sealed record StubRequest(string Method, string Path, string Authorization, string Body)
{
    /// <summary>The bearer value, or empty.</summary>
    public string Bearer => Authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? Authorization["Bearer ".Length..] : string.Empty;

    /// <summary>The path without its query.</summary>
    public string PathOnly => Path.Split('?')[0];
}

/// <summary>
/// A GitHub API on 127.0.0.1 (the <c>GITHUB_API_URL</c> seam) that records every request with its Authorization header.
/// It knows the GitHub App flow: a JWT (three dot-separated segments) is accepted only by
/// <c>POST /app/installations/{id}/access_tokens</c> and <c>GET /repos/{o}/{r}/installation</c>; the minted token
/// (<see cref="AppToken"/>) is an App token that, like the real App aisf-board, cannot send a repository dispatch or comment
/// (no Contents: write, no Issues: write) and, when <see cref="AppMayReadStatuses"/> is false, cannot read commit statuses;
/// <see cref="CliToken"/> is the GitHub CLI token and may do everything. Any other bearer is answered with 401.
/// </summary>
internal sealed class StubGitHubApi : IDisposable
{
    /// <summary>The installation token the stub mints.</summary>
    public const string AppToken = "stub-app-installation-token-0001";

    /// <summary>The GitHub CLI token: a full-rights token.</summary>
    public const string CliToken = "stub-gh-cli-token-0002";

    /// <summary>A pre-minted installation token a test hands over as AISF_BOARD_APP_TOKEN (App rights).</summary>
    public const string PreMintedAppToken = "stub-preminted-app-token-0003";

    /// <summary>The head commit of the stub's pull request.</summary>
    public const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    private readonly HttpListener listener = new();
    private readonly List<StubRequest> requests = [];
    private readonly Task loop;
    private int mints;

    /// <summary>Starts the stub on a free loopback port.</summary>
    public StubGitHubApi()
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

    /// <summary>HTTP status of the token exchange (201 = a token is minted).</summary>
    public int MintStatus { get; set; } = 201;

    /// <summary>
    /// <c>true</c>: the nth token minted is <see cref="AppToken"/> followed by <c>-n</c> (a re-mint gives a different token, for
    /// the tests of the one-hour lifetime); <c>false</c> (default): always <see cref="AppToken"/>.
    /// </summary>
    public bool NumberedTokens { get; set; }

    /// <summary>The token the nth mint (1-based) returns.</summary>
    /// <param name="number">Which mint.</param>
    public string MintedToken(int number) => NumberedTokens ? $"{AppToken}-{number}" : AppToken;

    /// <summary><c>true</c>: the App token may read commit statuses; <c>false</c>: it gets 403, as the real App does.</summary>
    public bool AppMayReadStatuses { get; set; } = true;

    /// <summary>HTTP status a full-rights token gets for a repository dispatch (204 = accepted).</summary>
    public int DispatchStatusForCli { get; set; } = 204;

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

    private static bool IsJwt(string bearer) => bearer.Count(character => character == '.') == 2;

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
            context.Response.StatusCode = status;
            if (status == 204 || answer.Length == 0)
            {
                context.Response.ContentLength64 = 0;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(answer);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }

            context.Response.Close();
        }
    }

    private (int Status, string Body) Answer(StubRequest request)
    {
        var segments = request.PathOnly.Trim('/').Split('/');
        var bearer = request.Bearer;

        if (request.Method == "POST" && segments is ["app", "installations", _, "access_tokens"])
        {
            if (!IsJwt(bearer))
            {
                return (401, Message("A JSON web token could not be decoded"));
            }

            return MintStatus == 201
                ? (201, new JsonObject { ["token"] = MintedToken(Interlocked.Increment(ref mints)), ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("o") }.ToJsonString())
                : (MintStatus, Message("stub mint refused"));
        }

        if (request.Method == "GET" && segments is ["repos", _, _, "installation"])
        {
            return IsJwt(bearer) ? (200, """{"id":4242}""") : (401, Message("A JSON web token could not be decoded"));
        }

        var isApp = bearer.StartsWith(AppToken, StringComparison.Ordinal) || bearer == PreMintedAppToken;
        if (!isApp && bearer != CliToken)
        {
            return (401, Message("Bad credentials"));
        }

        return (request.Method, segments) switch
        {
            ("GET", ["repos", _, _, "pulls", _]) => (200, PullRequest()),
            ("GET", ["repos", _, _, "pulls"]) => (200, "[]"),
            ("GET", ["repos", _, _, "commits", _, "status"]) when isApp && !AppMayReadStatuses => (403, Message("Resource not accessible by integration")),
            ("GET", ["repos", _, _, "commits", _, "status"]) => (200, Statuses()),
            ("POST", ["repos", _, _, "dispatches"]) when isApp => (403, Message("Resource not accessible by integration")),
            ("POST", ["repos", _, _, "dispatches"]) => (DispatchStatusForCli, DispatchStatusForCli == 204 ? string.Empty : Message("dispatch refused")),
            ("POST", ["repos", _, _, "issues", _, "comments"]) when isApp => (403, Message("Resource not accessible by integration")),
            ("POST", ["repos", _, _, "issues", _, "comments"]) => (201, """{"html_url":"http://stub.invalid/comment/1"}"""),
            _ => (404, Message("Not Found")),
        };
    }

    private static string Message(string text) => new JsonObject { ["message"] = text }.ToJsonString();

    private static string PullRequest() => new JsonObject
    {
        ["number"] = 45,
        ["state"] = "open",
        ["merged_at"] = null,
        ["updated_at"] = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("o"),
        ["mergeable_state"] = "clean",
        ["head"] = new JsonObject { ["sha"] = HeadSha, ["ref"] = "jeffreypalermo/stub" },
        ["merge_commit_sha"] = null,
    }.ToJsonString();

    private static string Statuses() => new JsonObject
    {
        ["statuses"] = new JsonArray(new JsonObject
        {
            ["context"] = "codefresh/env-checks",
            ["state"] = "success",
            ["updated_at"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o"),
            ["created_at"] = DateTimeOffset.UtcNow.AddMinutes(-9).ToString("o"),
            ["target_url"] = "http://stub.invalid/build/1",
        }),
    }.ToJsonString();
}

/// <summary>A throw-away RSA key of the GitHub App, generated at test time (never committed).</summary>
internal sealed class TestAppKey : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("app-key-").FullName;

    /// <summary>Generates a 2048-bit key and writes its PEM to a file.</summary>
    public TestAppKey()
    {
        Rsa = RSA.Create(2048);
        Pem = Rsa.ExportRSAPrivateKeyPem();
        Path = System.IO.Path.Combine(directory, "app-private-key.pem");
        File.WriteAllText(Path, Pem);
    }

    /// <summary>The key.</summary>
    public RSA Rsa { get; }

    /// <summary>The private key as PEM text.</summary>
    public string Pem { get; }

    /// <summary>The file holding <see cref="Pem"/>.</summary>
    public string Path { get; }

    /// <summary>A fragment of the PEM's base64 body, for asserting that a transcript does not leak the key.</summary>
    public string BodyFragment => Pem.Split('\n')[1].Trim();

    /// <summary>Verifies the RS256 signature of a JWT with the public half of the key.</summary>
    /// <param name="jwt">The token.</param>
    public bool Verifies(string jwt)
    {
        var parts = jwt.Split('.');
        return parts.Length == 3
            && Rsa.VerifyData(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"), System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>Deletes the file.</summary>
    public void Dispose()
    {
        Rsa.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A folder holding a fake <c>gh</c> that answers <c>gh auth token</c> with the value of FAKE_GH_TOKEN (and exits 1 when it is
/// empty), so tests never touch the developer's real GitHub CLI login.
/// </summary>
internal sealed class FakeGhCli : IDisposable
{
    /// <summary>Creates the folder and the fake.</summary>
    public FakeGhCli()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("fake-gh-").FullName;
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(System.IO.Path.Combine(Directory, "gh.cmd"), "@echo off\r\nif \"%FAKE_GH_TOKEN%\"==\"\" exit /b 1\r\necho %FAKE_GH_TOKEN%\r\n");
        }
        else
        {
            var path = System.IO.Path.Combine(Directory, "gh");
            File.WriteAllText(path, "#!/bin/sh\n[ -n \"$FAKE_GH_TOKEN\" ] || exit 1\necho \"$FAKE_GH_TOKEN\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The folder to put first on PATH.</summary>
    public string Directory { get; }

    /// <summary>Deletes the folder.</summary>
    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Runs the platform's PowerShell scripts against the stub with a clean, explicit environment.</summary>
internal static class GitHubScriptHost
{
    private static readonly string[] Scrubbed =
    [
        "GH_TOKEN", "GITHUB_TOKEN", "AISF_BOARD_APP_TOKEN", "AISF_BOARD_APP_ID", "AISF_BOARD_APP_INSTALLATION_ID",
        "AISF_BOARD_APP_PRIVATE_KEY", "AISF_BOARD_APP_PRIVATE_KEY_PATH", "FAKE_GH_TOKEN", "GITHUB_API_URL", "GITHUB_TOKEN_FILE",
        "AISF_CONFORMANCE_APP_ID", "AISF_CONFORMANCE_APP_INSTALLATION_ID", "AISF_CONFORMANCE_APP_PRIVATE_KEY", "AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH",
        "SANDBOX_APP_REPO", "PLATFORM_E2E_REPO",
        "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY",
    ];

    /// <summary>The pwsh executable, or ends the test (Inconclusive locally, failed when <c>CI=true</c>).</summary>
    public static string Pwsh => KitToolbox.Find("pwsh") ?? KitToolbox.Find("pwsh.exe") ?? KitToolbox.Require("pwsh");

    /// <summary>The repository-relative script as an absolute path.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public static string Script(string relative) => Path.Combine(KitToolbox.RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The environment of a run: every credential variable removed, then <paramref name="set"/> applied on top.</summary>
    /// <param name="api">The stub, for GITHUB_API_URL; <c>null</c> leaves it unset.</param>
    /// <param name="fakeGh">The fake gh folder to put first on PATH; <c>null</c> keeps PATH unchanged.</param>
    /// <param name="set">Variables to set.</param>
    public static Dictionary<string, string?> Environment(StubGitHubApi? api, FakeGhCli? fakeGh, params (string Name, string Value)[] set)
    {
        var environment = Scrubbed.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal);
        environment["NO_PROXY"] = "127.0.0.1,localhost";
        if (api is not null)
        {
            environment["GITHUB_API_URL"] = api.Url;
        }

        if (fakeGh is not null)
        {
            environment["PATH"] = fakeGh.Directory + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH");
        }

        foreach (var (name, value) in set)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>Runs <c>pwsh -NoProfile -File &lt;script&gt; &lt;arguments&gt;</c> from the repository root.</summary>
    /// <param name="scriptPath">Absolute script path.</param>
    /// <param name="environment">The run's environment.</param>
    /// <param name="arguments">Script arguments.</param>
    public static ProcessResult Run(string scriptPath, IReadOnlyDictionary<string, string?> environment, params string[] arguments) =>
        KitToolbox.Run(Pwsh, ["-NoProfile", "-File", scriptPath, .. arguments], KitToolbox.RepositoryRoot, TimeSpan.FromMinutes(2), environment);
}
